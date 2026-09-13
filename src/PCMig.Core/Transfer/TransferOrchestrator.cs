using System.Diagnostics;
using PCMig.Core.Jobs;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Util;
using Serilog;

namespace PCMig.Core.Transfer;

public sealed record ProgressSnapshot(
    JobPhase Phase,
    int TotalObjects, int CompletedObjects, int FailedObjects,
    long TotalBytes, long CompletedBytes, double Percent,
    double BytesPerSecond, double EtaSeconds,
    string? CurrentObjectId, string? CurrentObjectPath,
    string Message);

/// <summary>
/// 传输编排器：PCMig 的心脏。
///  - 对象级调度：一个对象跑完（Bulk 通道 + 可选 Large 通道）→ 写 Receipt → 下一个
///  - 协作式暂停：pause.request 文件驱动；Cooperative=跑完当前对象停；Immediate=立即终止 robocopy
///  - 断点恢复：Receipt 是唯一权威；job-state.json 只是视图，损坏可从 Receipt 重建
///  - 错误分类：Transient（网络等）→ 对象级重试+退避；Permanent → 记 Receipt 继续下一对象
/// </summary>
public sealed class TransferOrchestrator
{
    private readonly JobContext _ctx;
    private readonly MigrationMatrix _matrix;
    private readonly ILogger _log;
    private readonly RobocopyRunner _runner;

    /// <summary>robocopy 行级输出转发（UI 报错区用）。</summary>
    public event Action<string>? OutputLine;

    // ---- 无枚举进度跟踪状态（超大磁盘：不反复全量枚举目标目录）----
    private long _bulkCopiedBytes;              // bulk/rootfiles 通道：按 robocopy 输出行累计近似字节
    private long _largeCompletedBytes;          // large(/Z) 通道：已切换到下一个文件的前序大文件入账字节
    private string? _largeFileTarget;           // large(/Z) 通道：当前大文件的目标路径
    private long _largeFileStartLen;            // 该大文件开始前的已有长度（续传部分不重复计）
    private long _largeFileApproxSize;          // 当前大文件的近似大小（robocopy 输出解析值，兜底用）
    private PassKind _currentPass;
    private PlannedObject? _activeObject;
    private DateTime _lastFileEventAt = DateTime.MinValue; // 最近一次解析到文件行的时刻
    private DateTime _lastEnumAt = DateTime.MinValue;      // 最近一次回退枚举的时刻
    private TimeSpan _enumInterval = TimeSpan.FromSeconds(8); // 自适应枚举间隔（按上次枚举耗时×3，8s~120s）

    // ---- 错误风暴检测 + 进度回冲 ----
    private static readonly System.Text.RegularExpressions.Regex s_errFileLineRx =
        new(@"(?:错误|ERROR)\s+(?<code>\d+)\s+\(0x[0-9A-Fa-f]{8}\)\s+正在复制文件\s+(?<path>.+?)\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _pendingCredit =
        new(StringComparer.OrdinalIgnoreCase); // 已按"新文件"行入账但尚未确认成功的文件（路径→字节）
    private long _passFileAttempts;   // 本对象文件尝试数（含失败）
    private long _passErrors;         // 本对象文件错误数
    private string? _stormCode;       // 触发风暴终止的错误码（null=未触发）

    /// <summary>robocopy 每开始复制一个文件：bulk 通道累计近似字节；/Z 通道把前一个大文件入账并记录当前大文件。</summary>
    private void OnRunnerFileCopied(string sourcePath, long approxBytes)
    {
        _lastFileEventAt = DateTime.UtcNow;
        Interlocked.Increment(ref _passFileAttempts);
        if (_currentPass == PassKind.Large)
        {
            var obj = _activeObject;
            if (obj != null && sourcePath.StartsWith(obj.SourcePath, StringComparison.OrdinalIgnoreCase))
            {
                CreditCurrentLarge(); // 前一个大文件至此必然已复制完（/Z 串行），入账
                var rel = sourcePath[obj.SourcePath.Length..].TrimStart('\\');
                var target = Path.Combine(obj.TargetPath, rel);
                _largeFileTarget = target;
                _largeFileApproxSize = approxBytes;
                try { _largeFileStartLen = File.Exists(target) ? new FileInfo(target).Length : 0; }
                catch { _largeFileStartLen = 0; }
            }
        }
        else
        {
            // 先记账、按错误行回冲：robocopy 块缓冲下"新文件"行先于结果到达，失败时由 OnRunnerErrorLine 扣回
            _pendingCredit[sourcePath] = approxBytes;
            if (_pendingCredit.Count > 200_000) _pendingCredit.Clear(); // 防爆：超限放弃精确回冲（枚举通道兜底）
            Interlocked.Add(ref _bulkCopiedBytes, approxBytes);
        }
    }

    /// <summary>robocopy 文件级错误行：①回冲误入账的进度字节 ②连续失败达到风暴阈值→主动杀死当前对象。</summary>
    private void OnRunnerErrorLine(string line)
    {
        var m = s_errFileLineRx.Match(line);
        if (!m.Success) return;
        var code = m.Groups["code"].Value;
        var srcPath = m.Groups["path"].Value;

        // ① 进度回冲：该文件从未成功，把"新文件"行预记的字节扣掉
        if (_pendingCredit.TryRemove(srcPath, out var credited))
            Interlocked.Add(ref _bulkCopiedBytes, -credited);
        // /Z 通道：当前大文件失败则不再为其入账
        if (_largeFileTarget != null)
        {
            var obj = _activeObject;
            if (obj != null && srcPath.StartsWith(obj.SourcePath, StringComparison.OrdinalIgnoreCase))
            {
                var rel = srcPath[obj.SourcePath.Length..].TrimStart('\\');
                if (string.Equals(_largeFileTarget, Path.Combine(obj.TargetPath, rel), StringComparison.OrdinalIgnoreCase))
                    _largeFileTarget = null;
            }
        }

        // ② 风暴判定：错误 ≥200 且错误率 ≥90%（几乎每个文件都在失败）→ 继续跑只会空转数小时
        var errs = Interlocked.Increment(ref _passErrors);
        var attempts = Interlocked.Read(ref _passFileAttempts);
        if (_stormCode == null && errs >= 200 && errs >= attempts * 0.9)
        {
            _stormCode = code;
            _log.Error("检测到错误风暴：{Errors}/{Attempts} 个文件失败（最后错误码 {Code}），主动终止当前对象", errs, attempts, code);
            _runner.KillCurrent();
        }
    }

    /// <summary>把“当前大文件”按目标实测长度（近似大小兜底）入账到已完成累计。</summary>
    private void CreditCurrentLarge()
    {
        var p = _largeFileTarget;
        if (p == null) return;
        long done = _largeFileApproxSize;
        try
        {
            if (File.Exists(p))
                // 实测增量与近似大小取大：缓冲爆发时行到达晚于复制完成，实测增量=0，用近似值入账
                done = Math.Max(_largeFileApproxSize, Math.Max(0, new FileInfo(p).Length - _largeFileStartLen));
        }
        catch { /* 用近似值兜底 */ }
        Interlocked.Add(ref _largeCompletedBytes, done);
        _largeFileTarget = null;
    }

    /// <summary>/Z 大文件通道的实时落盘增量（只 stat 当前一个文件，O(1)）。</summary>
    private long CurrentLargePartial()
    {
        var p = _largeFileTarget;
        if (p == null) return 0;
        try { return File.Exists(p) ? Math.Max(0, new FileInfo(p).Length - _largeFileStartLen) : 0; }
        catch { return 0; }
    }

    /// <summary>robocopy 每开始复制一个文件（源路径, 近似字节）。供 UI 做"实时文件流"展示；不影响传输。</summary>
    public event Action<string, long>? FileCopied;

    public TransferOrchestrator(JobContext ctx, MigrationMatrix matrix, ILogger log)
    {
        _ctx = ctx;
        // 任务级自定义排除规则（超大数据模式）叠加到矩阵，传输口径与扫描/验证一致
        _matrix = matrix.WithExtraExclusions(ctx.Definition.CustomExclusions, log);
        _log = log.ForContext<TransferOrchestrator>();
        _runner = new RobocopyRunner(log);
        _runner.OutputLine += line => OutputLine?.Invoke(line); // 转发给 UI（报错区实时展示）
        _runner.OutputLine += OnRunnerErrorLine;                 // 错误风暴检测 + 进度回冲
        _runner.FileCopied += (p, b) =>
        {
            OnRunnerFileCopied(p, b);
            try { FileCopied?.Invoke(p, b); } catch { /* UI 订阅方异常不影响传输 */ }
        };
    }

    public async Task<JobPhase> RunAsync(IProgress<ProgressSnapshot>? progress = null, CancellationToken ct = default)
    {
        var plan = _ctx.Plan ?? throw new InvalidOperationException("plan.json 不存在：请先扫描并生成计划");
        var job = _ctx.Definition;
        var state = _ctx.LoadStateOrNew();

        // ---- 从 Receipt 重建权威进度 ----
        // 只有"完全完成"的对象才跳过；Failed / CompletedWithErrors / Interrupted 在续传时一律重跑。
        // robocopy 增量特性保证重跑只补差异（已完成文件秒级跳过），这是断网/断电恢复正确性的根基。
        var receipts = _ctx.LoadReceipts(_log);
        var completedIds = receipts
            .Where(r => r.Status == ObjectStatus.Completed)
            .GroupBy(r => r.ObjectId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.CompletedUtc).Last(), StringComparer.OrdinalIgnoreCase);
        long baseBytes = completedIds.Values.Sum(r => r.TargetBytes);

        state.TotalObjects = plan.Objects.Count;
        state.CompletedObjects = completedIds.Count;
        state.TotalBytes = plan.TotalBytes;
        state.CompletedBytes = Math.Min(baseBytes, plan.TotalBytes > 0 ? plan.TotalBytes : long.MaxValue);
        state.Phase = JobPhase.Running;
        state.LastError = null;
        _ctx.SaveState(state);
        Report(progress, state, null, null, 0, "开始/继续传输");

        _log.Information("传输开始: 共 {Total} 对象, 已完成 {Done}, 待传 {Bytes}",
            plan.Objects.Count, completedIds.Count, Format.Bytes(Math.Max(0, plan.TotalBytes - baseBytes)));

        var interrupted = false;

        foreach (var obj in plan.Objects)
        {
            if (completedIds.ContainsKey(obj.ObjectId)) continue;

            // ---- 暂停检查点（对象边界，协作式暂停生效处）----
            var paused = await WaitIfPausedAsync(state, progress, ct);
            if (paused == PauseOutcome.Canceled) { interrupted = true; break; }

            state.CurrentObjectId = obj.ObjectId;
            _ctx.SaveState(state);
            Report(progress, state, obj.ObjectId, obj.SourcePath, 0, $"开始对象 {obj.ObjectId}");

            var receipt = await RunOneObjectAsync(obj, job, state, baseBytes, progress, ct);
            if (receipt == null) { interrupted = true; break; }  // 取消/Immediate 暂停导致中断

            _ctx.SaveReceipt(receipt);
            baseBytes += receipt.TargetBytes;
            state.CompletedBytes = Math.Min(baseBytes, Math.Max(plan.TotalBytes, baseBytes));
            if (receipt.Status == ObjectStatus.Completed)
            {
                state.CompletedObjects++;
                completedIds[obj.ObjectId] = receipt;
            }
            state.Percent = Percent(state);
            _ctx.SaveState(state);
            Report(progress, state, obj.ObjectId, obj.SourcePath, 0,
                $"对象 {obj.ObjectId} {receipt.Status}（{Format.Bytes(receipt.TargetBytes)}）");

            if (receipt.Status == ObjectStatus.Interrupted) { interrupted = true; break; }
        }

        // ---- 收尾 ----
        state.CurrentObjectId = null;
        if (interrupted || ct.IsCancellationRequested)
        {
            state.Phase = File.Exists(_ctx.PauseRequestPath) ? JobPhase.Paused : JobPhase.Interrupted;
            state.LastError = "任务被暂停或中断；可用 resume 续传（已完成的对象不会重传）";
        }
        else if (state.Phase == JobPhase.Paused)
        {
            // 保持 Paused（等待 resume 后再进入下一轮 RunAsync）
        }
        else
        {
            // 失败数 = 未完全完成的对象数（跨多次 resume 精确收敛，不重复计尝试次数）
            state.FailedObjects = state.TotalObjects - state.CompletedObjects;
            state.Phase = state.FailedObjects > 0 ? JobPhase.CompletedWithErrors : JobPhase.Completed;
        }
        state.Percent = Percent(state);
        _ctx.SaveState(state);
        Report(progress, state, null, null, 0, $"阶段结束: {state.Phase}");
        _log.Information("传输阶段结束: {Phase}（完成 {Done}/{Total} 对象，失败 {Failed}）",
            state.Phase, state.CompletedObjects, state.TotalObjects, state.FailedObjects);
        return state.Phase;
    }

    /// <summary>执行单个对象：Bulk 通道（/MT）→ 可选 Large 通道（/Z），含 Transient 重试。</summary>
    private async Task<ObjectReceipt?> RunOneObjectAsync(PlannedObject obj, JobDefinition job, JobState state,
        long baseBytes, IProgress<ProgressSnapshot>? progress, CancellationToken ct)
    {
        var receipt = new ObjectReceipt
        {
            ObjectId = obj.ObjectId,
            Kind = obj.Kind,
            SourcePath = obj.SourcePath,
            TargetPath = obj.TargetPath,
            StartedUtc = DateTime.UtcNow
        };

        var passKind = obj.Kind == ObjectKind.RootFiles ? PassKind.RootFiles : PassKind.Bulk;
        var roboLog = Path.Combine(_ctx.RoboLogsDir, $"{obj.ObjectId}.log");
        var attempts = 0;
        var maxAttempts = job.Options.MaxObjectRetries + 1;

        // 重置本对象的进度跟踪状态
        Interlocked.Exchange(ref _bulkCopiedBytes, 0);
        Interlocked.Exchange(ref _largeCompletedBytes, 0);
        Interlocked.Exchange(ref _passFileAttempts, 0);
        Interlocked.Exchange(ref _passErrors, 0);
        _stormCode = null;
        _pendingCredit.Clear();
        _largeFileTarget = null; _largeFileStartLen = 0; _largeFileApproxSize = 0;
        _activeObject = obj; _currentPass = passKind;
        _lastFileEventAt = DateTime.MinValue; _lastEnumAt = DateTime.MinValue;

        // 进度轮询任务：周期性实测目标目录字节增长 → 更新 CompletedBytes/速度/ETA
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var speedState = new SpeedTracker();
        var pollTask = PollProgressAsync(obj, state, baseBytes, speedState, progress, pollCts.Token);
        // Immediate 暂停监视
        var immediateTask = WatchImmediatePauseAsync(pollCts.Token);

        try
        {
            // ---- Pass 1: Bulk（RootFiles 对象只有这一遍）----
            _currentPass = passKind;
            var bulk = await RunPassWithRetryAsync(obj, passKind, roboLog, attempts, maxAttempts, receipt, ct);
            receipt.RobocopyExitCodeBulk = bulk?.ExitCode ?? -1;
            if (_stormCode != null) return MarkStormFailed(receipt);     // 错误风暴：主动终止，不重试不续跑
            if (bulk == null) return MarkInterrupted(receipt);           // 取消
            if (!bulk.Success)
            {
                receipt.ErrorClass = Classify(bulk);
                receipt.ErrorDetail = bulk.LastErrorLine;
                receipt.Status = ObjectStatus.Failed;
                receipt.CompletedUtc = DateTime.UtcNow;
                _log.Error("对象 {Id} Bulk 通道失败: Exit={Code} {Err}", obj.ObjectId, bulk.ExitCode, bulk.LastErrorLine);
                return receipt;
            }

            // ---- Pass 2: Large（/Z 可续传，仅扫描判定有大文件时）----
            if (obj.UseRestartablePass && passKind == PassKind.Bulk)
            {
                _currentPass = PassKind.Large;
                var large = await RunPassWithRetryAsync(obj, PassKind.Large, roboLog, attempts, maxAttempts, receipt, ct);
                receipt.RobocopyExitCodeLarge = large?.ExitCode ?? -1;
                if (_stormCode != null) return MarkStormFailed(receipt);
                if (large == null) return MarkInterrupted(receipt);
                if (!large.Success)
                {
                    receipt.ErrorClass = Classify(large);
                    receipt.ErrorDetail = large.LastErrorLine;
                    receipt.Status = ObjectStatus.CompletedWithErrors; // 主体已成，大文件有问题单独标记
                    receipt.CompletedUtc = DateTime.UtcNow;
                    MeasureTarget(receipt, obj);
                    return receipt;
                }
            }

            receipt.Status = ObjectStatus.Completed;
            receipt.CompletedUtc = DateTime.UtcNow;
            MeasureTarget(receipt, obj);
            return receipt;
        }
        finally
        {
            pollCts.Cancel();
            try { await Task.WhenAll(pollTask, immediateTask); } catch { /* 轮询任务取消属正常 */ }
        }
    }

    /// <summary>错误风暴终止：标记失败（非中断——续传前必须先解决根因），给出可操作的中文诊断。</summary>
    private ObjectReceipt MarkStormFailed(ObjectReceipt receipt)
    {
        var hint = _stormCode switch
        {
            "82" => "目标盘极可能是 FAT32（单个文件夹约 2 万个文件的目录项上限，或文件超过 4GB）——请把目标改到 NTFS/exFAT 盘后重新迁移",
            "5" => "目标或源端权限不足——检查目标目录写入权限与源共享权限",
            "112" => "目标盘已满——清理空间或更换目标盘",
            _ => "请查看报告与日志定位共性原因"
        };
        receipt.ErrorClass = ErrorClass.Permanent; // 风暴=根因未除，不可盲目重试
        receipt.ErrorDetail = $"检测到错误风暴：{_passErrors} 个文件连续复制失败（最后错误码 {_stormCode}），已主动终止本对象避免数小时无效重试。{hint}。修复后删除本任务重建即可（已复制数据不丢）。";
        receipt.Status = ObjectStatus.Failed;
        receipt.CompletedUtc = DateTime.UtcNow;
        _log.Error("对象 {Id} 错误风暴终止: {Detail}", receipt.ObjectId, receipt.ErrorDetail);
        return receipt;
    }

    private async Task<RobocopyRunResult?> RunPassWithRetryAsync(PlannedObject obj, PassKind pass, string roboLog,
        int attempts, int maxAttempts, ObjectReceipt receipt, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            receipt.Attempt = attempt;
            var result = await _runner.RunPassAsync(obj.SourcePath, obj.TargetPath, _ctx.Definition.Options,
                _matrix, pass, roboLog, ct, obj.FileList);
            if (result.Killed) return null;
            if (result.Success) return result;

            var cls = Classify(result);
            if (cls == ErrorClass.Transient && attempt < maxAttempts)
            {
                var backoff = TimeSpan.FromSeconds(10.0 * attempt);
                _log.Warning("对象 {Id} [{Pass}] 失败(第{A}次, Transient): {Err}；{Backoff} 后重试",
                    obj.ObjectId, pass, attempt, result.LastErrorLine ?? $"Exit={result.ExitCode}", backoff);
                await Task.Delay(backoff, ct);
                continue;
            }
            return result; // Permanent 或重试耗尽
        }
        return null;
    }

    /// <summary>枚举目标侧实测落盘量（Receipt 的数据以此为准，不依赖 robocopy 自报）。</summary>
    private void MeasureTarget(ObjectReceipt receipt, PlannedObject obj)
    {
        try
        {
            if (obj.FileList != null)
            {
                // 文件清单对象：只统计清单内文件，避免把同目录其他对象的文件算进来
                long bytes = 0, files = 0;
                foreach (var name in obj.FileList)
                {
                    try
                    {
                        var p = Path.Combine(obj.TargetPath, name);
                        if (!File.Exists(p)) continue;
                        bytes += new FileInfo(p).Length; files++;
                    }
                    catch { /* 单文件跳过 */ }
                }
                receipt.TargetBytes = bytes; receipt.TargetFiles = files;
            }
            else if (obj.Kind == ObjectKind.RootFiles)
            {
                long bytes = 0, files = 0;
                foreach (var f in Directory.EnumerateFiles(obj.TargetPath))
                {
                    try { var fi = new FileInfo(f); bytes += fi.Length; files++; } catch { }
                }
                receipt.TargetBytes = bytes; receipt.TargetFiles = files;
            }
            else
            {
                var stat = DirStat.Measure(obj.TargetPath, long.MaxValue);
                receipt.TargetBytes = stat.Bytes; receipt.TargetFiles = stat.Files;
            }
        }
        catch (Exception ex) { _log.Warning(ex, "目标实测失败 {Path}", obj.TargetPath); }
    }

    private ObjectReceipt MarkInterrupted(ObjectReceipt r)
    {
        r.Status = ObjectStatus.Interrupted;
        r.CompletedUtc = DateTime.UtcNow;
        r.ErrorDetail = "interrupted";
        return r;
    }

    private enum PauseOutcome { Running, Canceled }

    /// <summary>暂停等待循环：发现 pause.request → 置 Paused → 等待文件被移除（resume）。</summary>
    private async Task<PauseOutcome> WaitIfPausedAsync(JobState state, IProgress<ProgressSnapshot>? progress, CancellationToken ct)
    {
        if (!File.Exists(_ctx.PauseRequestPath)) return PauseOutcome.Running;

        state.Phase = JobPhase.Paused;
        _ctx.SaveState(state);
        Report(progress, state, state.CurrentObjectId, null, 0, "已暂停（等待 resume）");
        _log.Information("任务已暂停（{Mode}），等待 resume 或取消", _ctx.ReadPauseRequest() ?? "Cooperative");

        while (File.Exists(_ctx.PauseRequestPath))
        {
            if (ct.IsCancellationRequested) return PauseOutcome.Canceled;
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { return PauseOutcome.Canceled; }
        }

        state.Phase = JobPhase.Running;
        _ctx.SaveState(state);
        Report(progress, state, null, null, 0, "已恢复，继续传输");
        _log.Information("任务已恢复");
        return PauseOutcome.Running;
    }

    /// <summary>Immediate 暂停监视：一旦发现 Immediate 请求，立即终止当前 robocopy。</summary>
    private async Task WatchImmediatePauseAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var mode = _ctx.ReadPauseRequest();
                if (string.Equals(mode, "Immediate", StringComparison.OrdinalIgnoreCase))
                {
                    _log.Warning("收到 Immediate 暂停请求，终止当前 robocopy 进程");
                    _runner.KillCurrent();
                    return;
                }
                await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// 进度轮询（超大规模安全）：
    ///   主通道 = robocopy 输出行累计（bulk）+ 当前大文件 stat（/Z），平时零枚举；
    ///   基线 = 仅当本对象有历史 Receipt（即续传）时枚举一次目标，全新对象基线为 0；
    ///   回退 = robocopy 重定向 stdout 是块缓冲，少量大文件时行会憋到进程退出才到达——
    ///         30 秒无文件行事件则实测枚举一次目标（间隔≥10s，取最大值保证单调不回退）。
    /// </summary>
    private async Task PollProgressAsync(PlannedObject obj, JobState state, long baseBytes,
        SpeedTracker speed, IProgress<ProgressSnapshot>? progress, CancellationToken ct)
    {
        var hasPriorReceipt = _ctx.LoadReceipts().Any(r =>
            string.Equals(r.ObjectId, obj.ObjectId, StringComparison.OrdinalIgnoreCase));
        var baseline = hasPriorReceipt ? MeasureCurrentTargetBytes(obj) : 0;
        long lastBytes = baseline;
        var lastTime = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(2000, ct); }
            catch (OperationCanceledException) { return; }

            // 主通道：bulk 解析累计 + /Z 已入账大文件 + 当前大文件实时长度（O(1)）
            long nowBytes = baseline + Interlocked.Read(ref _bulkCopiedBytes)
                + Interlocked.Read(ref _largeCompletedBytes) + CurrentLargePartial();

            // 回退通道：robocopy 重定向 stdout 是块缓冲，少量大文件时行会憋到很晚才到达——
            // 无文件行事件超过阈值（/Z 12s、bulk 30s）则实测枚举一次；
            // 枚举间隔自适应 = 上次枚举耗时×3（8s~120s），巨型目录不会被枚举拖垮。
            var now = DateTime.UtcNow;
            var noEventWindow = _currentPass == PassKind.Large ? TimeSpan.FromSeconds(12) : TimeSpan.FromSeconds(30);
            if (now - _lastFileEventAt > noEventWindow && now - _lastEnumAt > _enumInterval)
            {
                var enumSw = Stopwatch.StartNew();
                var measured = MeasureCurrentTargetBytes(obj);
                enumSw.Stop();
                _lastEnumAt = now;
                _enumInterval = TimeSpan.FromSeconds(Math.Clamp(enumSw.Elapsed.TotalSeconds * 3, 8, 120));
                _log.Debug("进度回退枚举 {Id}: 实测={Measured} 解析累计={Parsed} 大文件入账={LargeDone} 当前={LargePartial} 枚举耗时={Ms}ms",
                    obj.ObjectId, measured, Interlocked.Read(ref _bulkCopiedBytes),
                    Interlocked.Read(ref _largeCompletedBytes), CurrentLargePartial(), enumSw.ElapsedMilliseconds);
                if (measured > nowBytes) nowBytes = measured;
            }

            var dt = (now - lastTime).TotalSeconds;
            var effBytes = Math.Max(nowBytes, lastBytes); // 进度单调，不回退
            if (dt > 0 && effBytes > lastBytes)
                speed.Push((effBytes - lastBytes) / dt);
            lastBytes = effBytes; lastTime = now;

            // baseBytes = 之前已完成对象的字节；effBytes = 本对象当前落盘（含续传基线）
            state.CompletedBytes = baseBytes + Math.Max(0, effBytes);
            state.Percent = Percent(state);
            state.BytesPerSecond = speed.Current;
            _ctx.SaveState(state);
            Report(progress, state, obj.ObjectId, obj.SourcePath, speed.Current,
                $"复制中 {obj.ObjectId}（{Format.Speed(speed.Current)}）");
        }
    }

    private long MeasureCurrentTargetBytes(PlannedObject obj)
    {
        try
        {
            if (!Directory.Exists(obj.TargetPath)) return 0;
            if (obj.FileList != null)
            {
                long bytes = 0;
                foreach (var name in obj.FileList)
                {
                    try { var p = Path.Combine(obj.TargetPath, name); if (File.Exists(p)) bytes += new FileInfo(p).Length; }
                    catch { }
                }
                return bytes;
            }
            if (obj.Kind == ObjectKind.RootFiles)
                return Directory.EnumerateFiles(obj.TargetPath).Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
            return DirStat.Measure(obj.TargetPath, long.MaxValue).Bytes;
        }
        catch { return 0; }
    }

    private static double Percent(JobState s)
        => s.TotalBytes > 0 ? Math.Min(100.0, s.CompletedBytes * 100.0 / s.TotalBytes)
           : s.TotalObjects > 0 ? s.CompletedObjects * 100.0 / s.TotalObjects : 0;

    private static ErrorClass Classify(RobocopyRunResult r)
    {
        var detail = r.LastErrorLine ?? "";
        // 网络类错误（53/64/59/1219/1236 等）→ Transient；其余默认 Permanent
        string[] transientHints = ["ERROR 53", "ERROR 59", "ERROR 64", "ERROR 1219", "ERROR 1236",
            "网络", "network", "Network", "超时", "timeout", "semantics"];
        return transientHints.Any(h => detail.Contains(h, StringComparison.OrdinalIgnoreCase))
            ? ErrorClass.Transient : ErrorClass.Permanent;
    }

    private void Report(IProgress<ProgressSnapshot>? progress, JobState s, string? objId, string? objPath,
        double speed, string msg)
    {
        var eta = speed > 0 && s.TotalBytes > 0 ? Math.Max(0, s.TotalBytes - s.CompletedBytes) / speed : double.NaN;
        progress?.Report(new ProgressSnapshot(
            s.Phase, s.TotalObjects, s.CompletedObjects, s.FailedObjects,
            s.TotalBytes, s.CompletedBytes, Percent(s),
            speed, eta, objId, objPath, msg));
    }

    private sealed class SpeedTracker
    {
        private double _ema;
        public double Current => _ema;
        public void Push(double instant)
        {
            if (instant < 0) instant = 0;
            _ema = _ema <= 0 ? instant : 0.3 * instant + 0.7 * _ema;
        }
    }
}
