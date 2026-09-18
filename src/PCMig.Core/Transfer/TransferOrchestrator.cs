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
    string Message,
    // ---- v0.3.8（缺陷 3：失败/暂停/停滞在界面不刺眼）----
    /// <summary>当前对象已落盘字节（"已传 A"）。</summary>
    long CurrentObjectDoneBytes = 0,
    /// <summary>当前对象计划字节（"共 B"）。0 = 未知。</summary>
    long CurrentObjectTotalBytes = 0,
    /// <summary>当前对象含大文件（≥分流阈值）→ 界面给出"可能数分钟无进度变化"的提示。</summary>
    bool CurrentObjectHasLargeFile = false,
    /// <summary>已有多少秒没看到落盘字节增长（≥45s 视为进度停滞）。</summary>
    double StallSeconds = 0);

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

    /// <summary>引擎主动提示（如速度异常偏低，疑似安全软件实时扫描）：UI 状态栏/日志面板用。每个任务最多提示一次。</summary>
    public event Action<string>? TransferNotice;

    // ---- 速度异常检测（安全软件实时扫描的典型症状：吞吐被拖垮到峰值的几分之一）----
    private double _peakSpeed;                          // 本任务速度 EMA 峰值
    private DateTime _anomalySince = DateTime.MinValue; // 进入低速状态的时刻
    private bool _anomalyNotified;                      // 每任务只提示一次

    // ---- 无枚举进度跟踪状态（超大磁盘：不反复全量枚举目标目录）----
    private long _bulkCopiedBytes;              // bulk/rootfiles 通道：按 robocopy 输出行累计近似字节
    private long _largeCompletedBytes;          // large(/Z) 通道：已切换到下一个文件的前序大文件入账字节
    private string? _largeFileTarget;           // large(/Z) 通道：当前大文件的目标路径
    private long _largeFileStartLen;            // 该大文件开始前的已有长度（续传部分不重复计）
    private long _largeFileApproxSize;          // 当前大文件的近似大小（robocopy 输出解析值，兜底用）
    private PassKind _currentPass;
    /// <summary>本 pass 是否走串行 /Z（只有串行才能按"当前大文件"精确入账；/MT+/J 走多文件并行，按行累计+枚举兜底）。</summary>
    private bool _serialLargePass;
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
    private string? _spaceCode;       // 目标盘空间不足的错误码（T07 熔断：null=未触发）

    // ---- 本次运行的累计速率口径（T01：自报 243 MB/s vs 实测 1115 MB/s，偏低 4.6 倍）----
    // 旧的 BytesPerSecond 是"robocopy 日志解析出的 2 秒瞬时值 + 每对象清零的 EMA"：
    // 单个对象 1–4 秒就结束，日志行到达时窗口早就过去了，于是自报值系统性偏低。
    // 现在改为"本次运行真正新增的落盘字节 ÷ 本次运行的有效用时"，与界面文案"任务实测"一致。
    private readonly System.Diagnostics.Stopwatch _runSw = new();
    private long _runStartCompletedBytes;   // 本次运行开始时已完成的字节（续传时不算进速率）
    private long _pausedTicks;              // 累计暂停等待时长（暂停不是传输慢，不计入分母）

    /// <summary>robocopy 每开始复制一个文件：bulk 通道累计近似字节；/Z 通道把前一个大文件入账并记录当前大文件。</summary>
    private void OnRunnerFileCopied(string sourcePath, long approxBytes)
    {
        _lastFileEventAt = DateTime.UtcNow;
        Interlocked.Increment(ref _passFileAttempts);
        if (_currentPass == PassKind.Large && _serialLargePass)
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

        // ①.5 磁盘满熔断（T07）：ENOSPC 属于"根因没除、重试毫无意义"——继续跑只会对每个文件
        //    反复重试（实测每个对象 3–33 次失败）并写一堆注定失败的数据。命中即主动终止当前 robocopy，
        //    本次运行提前收尾；错误归为可恢复（Transient），释放空间后 resume 即可补齐。
        if (_spaceCode == null && IsSpaceErrorCode(code))
        {
            _spaceCode = code;
            _log.Error("目标盘空间不足（错误码 {Code}）：主动终止当前对象并提前结束本次运行，" +
                       "避免对每个文件反复重试。释放目标盘空间后 resume 即可续传", code);
            _runner.KillCurrent();
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

    /// <param name="onlyObjectIds">只跑这些对象（定向修复）；null = 跑全部。</param>
    /// <param name="forceRecopy">true = 已"完全完成"的对象也重拷（验证发现不一致时用）；
    /// robocopy 增量特性保证只补差异，已一致的文件秒级跳过。</param>
    /// <summary>
    /// true = 本次运行对每个对象追加 robocopy /IS /IT：连"大小与时间都一样"的文件也从源重新拉取并覆盖。
    /// 修复场景专用（目标文件可能内容损坏但大小/时间未变，默认增量会判为"相同"直接跳过）。
    /// 普通传输/续传保持 false：靠增量秒级跳过，不做无谓重传。
    /// </summary>
    public bool ForceOverwriteFromSource { get; set; }

    public async Task<JobPhase> RunAsync(IProgress<ProgressSnapshot>? progress = null, CancellationToken ct = default,
        IReadOnlyCollection<string>? onlyObjectIds = null, bool forceRecopy = false)
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

        // ---- 本次运行的速率口径与熔断状态复位 ----
        _spaceCode = null;
        _runStartCompletedBytes = state.CompletedBytes;
        Interlocked.Exchange(ref _pausedTicks, 0);
        _runSw.Restart();

        // 目标根可见性：上一次整盘迁移可能被源盘根属性（Hidden+System）带成隐藏，开始前先修一次
        TargetRootGuard.EnsureVisible(job.TargetRoot, _log);

        // ---- 定向修复范围（验证发现不一致 / 上次失败的对象）----
        var scoped = onlyObjectIds is { Count: > 0 }
            ? new HashSet<string>(onlyObjectIds, StringComparer.OrdinalIgnoreCase)
            : null;

        Report(progress, state, null, null, 0, scoped != null ? "开始定向修复" : "开始/继续传输");

        _log.Information("传输开始: 共 {Total} 对象, 已完成 {Done}, 待传 {Bytes}{Scope}",
            plan.Objects.Count, completedIds.Count, Format.Bytes(Math.Max(0, plan.TotalBytes - baseBytes)),
            scoped != null ? $", 定向修复 {scoped.Count} 个对象（强制复拷）" : "");

        var interrupted = false;
        var spaceAborted = false;   // 目标盘满导致本次运行提前结束（可恢复）

        foreach (var obj in plan.Objects)
        {
            // 定向修复：名单外的对象直接跳过
            if (scoped != null && !scoped.Contains(obj.ObjectId)) continue;

            var prevReceipt = completedIds.TryGetValue(obj.ObjectId, out var pr) ? pr : null;
            var wasCompleted = prevReceipt != null;
            if (wasCompleted && !forceRecopy) continue;

            // ---- 暂停检查点（对象边界，协作式暂停生效处）----
            var paused = await WaitIfPausedAsync(state, progress, ct);
            if (paused == PauseOutcome.Canceled) { interrupted = true; break; }

            state.CurrentObjectId = obj.ObjectId;
            _ctx.SaveState(state);
            Report(progress, state, obj.ObjectId, obj.SourcePath, 0, $"开始对象 {obj.ObjectId}",
                objTotal: Math.Max(obj.EstimatedBytes, 0), objLarge: obj.UseRestartablePass);

            var receipt = await RunOneObjectAsync(obj, job, state, baseBytes, progress, ct);
            if (receipt == null) { interrupted = true; break; }  // 取消/Immediate 暂停导致中断

            _ctx.SaveReceipt(receipt);
            // 复拷已完成对象：先扣掉上一次计入的字节，避免同一对象被累计两次
            if (wasCompleted) baseBytes -= prevReceipt!.TargetBytes;
            baseBytes += receipt.TargetBytes;
            state.CompletedBytes = Math.Min(baseBytes, Math.Max(plan.TotalBytes, baseBytes));
            if (receipt.Status == ObjectStatus.Completed)
            {
                if (!wasCompleted) state.CompletedObjects++;
                completedIds[obj.ObjectId] = receipt;
            }
            else if (wasCompleted)
            {
                // 复拷后反而没完成（例如文件被占用）：如实回退计数，不制造"已完成"假象
                state.CompletedObjects--;
                completedIds.Remove(obj.ObjectId);
            }
            // RootFiles 通道的目标就是目标根目录本身：robocopy 刚把源盘根的 Hidden+System 写了上去。
            // 立刻清掉——即使任务被中断，用户也能在资源管理器里看到已迁移的数据。
            if (obj.Kind == ObjectKind.RootFiles) TargetRootGuard.EnsureVisible(job.TargetRoot, _log);

            state.Percent = Percent(state);
            _ctx.SaveState(state);
            Report(progress, state, obj.ObjectId, obj.SourcePath, 0,
                $"对象 {obj.ObjectId} {receipt.Status}（{Format.Bytes(receipt.TargetBytes)}）",
                objDone: receipt.TargetBytes, objTotal: Math.Max(obj.EstimatedBytes, receipt.TargetBytes),
                objLarge: obj.UseRestartablePass);

            if (receipt.Status == ObjectStatus.Interrupted) { interrupted = true; break; }
            // 目标盘满：本次运行到此为止（剩余对象不再逐个白试），释放空间后 resume 接着跑
            if (_spaceCode != null) { spaceAborted = true; break; }
        }

        // ---- 收尾 ----
        state.CurrentObjectId = null;
        if (spaceAborted || _spaceCode != null)
        {
            // 磁盘满不是"任务被中断"，而是"可恢复失败"：阶段如实记为 CompletedWithErrors，
            // 把该做的下一步（释放空间 → resume）写进 LastError，绝不假装完成。
            state.FailedObjects = Math.Max(0, state.TotalObjects - state.CompletedObjects);
            state.Phase = JobPhase.CompletedWithErrors;
            state.LastError = "目标磁盘空间不足：本次运行已提前停止（避免对每个文件反复重试）。" +
                "请释放目标盘空间后执行 resume，已完成的对象不会重传。";
            _log.Error("传输提前结束：目标盘空间不足（完成 {Done}/{Total} 对象）", state.CompletedObjects, state.TotalObjects);
        }
        else if (interrupted || ct.IsCancellationRequested)
        {
            state.Phase = File.Exists(_ctx.PauseRequestPath) ? JobPhase.Paused : JobPhase.Interrupted;
            state.LastError = "任务被暂停或中断；可用 resume 续传（已完成的对象不会重传）";
        }
        else if (state.Phase == JobPhase.Paused)
        {
            // 保持 Paused（等待 resume 后再进入下一轮 RunAsync）
        }
        else if (state.TotalObjects == 0)
        {
            // 空计划绝不能报"完成"（绿勾错觉防线）：所有选定路径都不存在/不可访问时，0 对象 ≠ 成功。
            // 真实环境实测踩中：粘贴的源路径少一个反斜杠 → 全部被跳过 → 若显示"迁移完成"会酿成事故。
            state.Phase = JobPhase.Failed;
            state.LastError = "计划为空：所有选定路径均不存在或不可访问，没有任何数据被迁移。请核对源路径/共享名后重新预检。";
            _log.Error("传输终止：计划为空（0 对象），按失败处理以防绿勾错觉");
        }
        else
        {
            // 失败数 = 未完全完成的对象数（跨多次 resume 精确收敛，不重复计尝试次数）
            state.FailedObjects = state.TotalObjects - state.CompletedObjects;
            state.Phase = state.FailedObjects > 0 ? JobPhase.CompletedWithErrors : JobPhase.Completed;
        }
        TargetRootGuard.EnsureVisible(job.TargetRoot, _log);   // 收尾再兜一次，杜绝"数据在但看不见"
        state.Percent = Percent(state);
        state.BytesPerSecond = CumulativeSpeed(state.CompletedBytes);  // 收尾速率 = 本次运行的平均值（status 显示同源）
        _ctx.SaveState(state);
        Report(progress, state, null, null, state.BytesPerSecond,
            _spaceCode != null
                ? "阶段结束: 目标磁盘空间不足，本次运行已提前停止（释放空间后 resume）"
                : $"阶段结束: {state.Phase}");
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

        // ---- 强制覆盖（修复专用）：先删目标侧同名文件，逼 robocopy 从共享重新拉取 ----
        // 不删的话，robocopy 对"大小+时间相同"的文件一律跳过（实测 /IS /IT 也无效）。
        if (ForceOverwriteFromSource)
        {
            var (pf, pb, pskip) = RepairPurge.PurgeTargetCopies(obj, job.Options, _matrix, _log);
            if (pf > 0 || pskip > 0)
                _log.Information("强制覆盖: 对象 {Id} 已删除目标侧 {Files} 个同名文件（{Bytes}），将从共享重新拉取{Skip}",
                    obj.ObjectId, pf, Format.Bytes(pb),
                    pskip > 0 ? $"；{pskip} 个大于分流阈值且本次不走 /Z 通道的文件已跳过不删" : "");
        }

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
            if (_spaceCode != null) return MarkSpaceFailed(receipt);     // 目标盘满：可恢复失败，不重试不续跑
            if (_stormCode != null) return MarkStormFailed(receipt);     // 错误风暴：主动终止，不重试不续跑
            if (bulk == null) return MarkInterrupted(receipt);           // 取消
            if (!bulk.Success)
            {
                receipt.ErrorClass = Classify(bulk);
                // 具体原因优先用"多少文件失败 + 主因 + 举例"的汇总；只留最后一行则等于没说（实测的"复制失败"投诉）
                receipt.ErrorDetail = RobocopyRunner.SummarizeFailures(bulk.ExitCode, bulk.ErrorLines)
                                      ?? bulk.LastErrorLine;
                receipt.Status = ObjectStatus.Failed;
                receipt.CompletedUtc = DateTime.UtcNow;
                _log.Error("对象 {Id} {Pass} 通道失败: Exit={Code}（{Meaning}） {Err}",
                    obj.ObjectId, passKind, bulk.ExitCode,
                    PCMig.Core.Util.ErrorTranslator.ExitCodeText(bulk.ExitCode), receipt.ErrorDetail);
                return receipt;
            }

            // ---- Pass 2: Large（/Z 可续传，仅扫描判定有大文件时）----
            if (obj.UseRestartablePass && passKind == PassKind.Bulk)
            {
                _currentPass = PassKind.Large;
                var large = await RunPassWithRetryAsync(obj, PassKind.Large, roboLog, attempts, maxAttempts, receipt, ct);
                receipt.RobocopyExitCodeLarge = large?.ExitCode ?? -1;
                if (_spaceCode != null) return MarkSpaceFailed(receipt);
                if (_stormCode != null) return MarkStormFailed(receipt);
                if (large == null) return MarkInterrupted(receipt);
                if (!large.Success)
                {
                    receipt.ErrorClass = Classify(large);
                    receipt.ErrorDetail = RobocopyRunner.SummarizeFailures(large.ExitCode, large.ErrorLines)
                                          ?? large.LastErrorLine;
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
            // v0.3.8 大文件通道：auto 下首次用 /MT+/J（吞吐），重试时退 /Z（续传优先）
            var restartable = RobocopyRunner.UseRestartableZ(_ctx.Definition.Options, pass, attempt);
            _serialLargePass = pass == PassKind.Large && restartable;
            if (pass == PassKind.Large)
                _log.Information("对象 {Id} 大文件通道: {Channel}（第 {Attempt} 次尝试）",
                    obj.ObjectId,
                    RobocopyRunner.ChannelText(pass, restartable,
                        RobocopyRunner.UseUnbufferedJ(obj.SourcePath, obj.TargetPath, _ctx.Definition.Options)),
                    attempt);
            var result = await _runner.RunPassAsync(obj.SourcePath, obj.TargetPath, _ctx.Definition.Options,
                _matrix, pass, roboLog, ct, obj.FileList, restartable);
            if (result.Killed) return null;
            if (result.Success) return result;

            var cls = Classify(result);
            // 目标盘满时对象级重试毫无意义（空间不会因为等待而变多）→ 交给上面的熔断路径提前收尾
            if (cls == ErrorClass.Transient && attempt < maxAttempts && _spaceCode == null)
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

        var pauseSw = System.Diagnostics.Stopwatch.StartNew();
        while (File.Exists(_ctx.PauseRequestPath))
        {
            if (ct.IsCancellationRequested) return PauseOutcome.Canceled;
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { return PauseOutcome.Canceled; }
        }
        // 暂停等待时间不计入"本次运行平均速率"的分母：暂停不是传得慢（T01 速率口径的一部分）
        pauseSw.Stop();
        Interlocked.Add(ref _pausedTicks, pauseSw.Elapsed.Ticks);

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
        // 进度停滞检测（v0.3.8）："界面长时间不动"是用户判断"卡死"并手动终止的直接原因，
        // 必须由引擎如实报出"已经多少秒没有落盘字节增长"，而不是让用户自己猜。
        var lastChangeAt = DateTime.UtcNow;

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
            {
                speed.Push((effBytes - lastBytes) / dt);
                lastChangeAt = now;      // 有增长 = 没停滞
            }
            lastBytes = effBytes; lastTime = now;
            var stallSeconds = (now - lastChangeAt).TotalSeconds;

            // 速度异常检测：当前 EMA 持续低于本任务峰值的 30%（且峰值曾 >5MB/s）超过 45 秒 → 提示
            // （企业环境里最常见的原因是杀软/EDR 正在实时扫描每一个落盘文件）
            if (speed.Current > _peakSpeed) _peakSpeed = speed.Current;
            var slow = _peakSpeed > 5 * 1024 * 1024 && speed.Current > 0 && speed.Current < _peakSpeed * 0.3;
            if (slow)
            {
                if (_anomalySince == DateTime.MinValue) _anomalySince = now;
                else if (!_anomalyNotified && now - _anomalySince > TimeSpan.FromSeconds(45))
                {
                    _anomalyNotified = true;
                    var pct = speed.Current / _peakSpeed * 100;
                    _log.Warning("速度异常：当前 {Cur} 仅为峰值 {Peak} 的 {Pct:0}%（疑似安全软件实时扫描或源盘瓶颈）",
                        Format.Speed(speed.Current), Format.Speed(_peakSpeed), pct);
                    TransferNotice?.Invoke($"速度异常：当前 {Format.Speed(speed.Current)}，仅为本任务峰值 {Format.Speed(_peakSpeed)} 的 {pct:0}%。" +
                        "常见原因：安全软件正在实时扫描传输的文件（可联系 IT 将 PCMig/robocopy 加入白名单），或旧电脑硬盘到达瓶颈。迁移仍在正常继续。");
                }
            }
            else { _anomalySince = DateTime.MinValue; }

            // baseBytes = 之前已完成对象的字节；effBytes = 本对象当前落盘（含续传基线）
            state.CompletedBytes = baseBytes + Math.Max(0, effBytes);
            state.Percent = Percent(state);
            // 对外一律报"本次运行的累计平均速率"（T01：旧口径偏低 4.6 倍，用户会误判剩余时间）；
            // speed.Current 只用于上面的速度异常检测，不再出现在给用户看的数字里。
            var shown = CumulativeSpeed(state.CompletedBytes);
            if (shown <= 0) shown = speed.Current;   // 起步不足 0.5 秒时的兜底
            state.BytesPerSecond = shown;
            _ctx.SaveState(state);
            // 停滞超过 45 秒：把"当前对象 / 已传 A / 共 B"和"大文件可能数分钟无进度变化"一并报出去，
            // 让用户知道引擎还活着、在等的是大文件而不是卡死（生产事故里用户据此误判并终止了任务）。
            var msg = stallSeconds >= StallWarnSeconds
                ? $"⏳ 进度停滞 {stallSeconds:0} 秒（未出现落盘字节增长）：当前对象 {obj.SourcePath}，已传 {Format.Bytes(Math.Max(0, effBytes - baseline))}/{Format.Bytes(Math.Max(obj.EstimatedBytes, 0))}。" +
                  (obj.UseRestartablePass ? "该对象含大文件，复制期间可能数分钟无进度变化，属正常，请勿终止。" : "正在重试或等待源端响应，请查看日志。")
                : $"复制中 {obj.ObjectId}（本次平均 {Format.Speed(shown)}）";
            Report(progress, state, obj.ObjectId, obj.SourcePath, shown, msg,
                objDone: Math.Max(0, effBytes - baseline),
                objTotal: Math.Max(obj.EstimatedBytes, 0),
                objLarge: obj.UseRestartablePass,
                stall: stallSeconds);
        }
    }

    /// <summary>进度停滞多久算"值得提示"（秒）。取 45 秒：小于最常见的"大文件首字节延迟"窗口。</summary>
    public const double StallWarnSeconds = 45;

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
        // 目标盘空间不足 → 可恢复（Transient）：空间是外部条件，释放后 resume 就该重试（T07）。
        // 注意：它不进入"对象级退避重试"——磁盘满时等待 10/20/30 秒毫无意义，
        // 由 _spaceCode 熔断路径提前结束本次运行（见 RunPassWithRetryAsync 的重试条件）。
        if (IsSpaceErrorText(r.LastErrorLine)
            || (r.ErrorLines != null && r.ErrorLines.Any(IsSpaceErrorText)))
            return ErrorClass.Transient;

        var detail = r.LastErrorLine ?? "";
        // 网络类错误（53/64/59/1219/1236 等）→ Transient；其余默认 Permanent
        string[] transientHints = ["ERROR 53", "ERROR 59", "ERROR 64", "ERROR 1219", "ERROR 1236",
            "网络", "network", "Network", "超时", "timeout", "semantics"];
        return transientHints.Any(h => detail.Contains(h, StringComparison.OrdinalIgnoreCase))
            ? ErrorClass.Transient : ErrorClass.Permanent;
    }

    /// <summary>目标盘空间不足的错误码：112=磁盘空间不足，39=磁盘已满，0x70/0x27 为等价十六进制写法。</summary>
    private static bool IsSpaceErrorCode(string? code) => code is "112" or "39"
        or "0x00000070" or "0x70" or "0x00000027" or "0x27";

    /// <summary>错误文本里是否写着"磁盘空间不足"（无错误码时的兜底判据）。</summary>
    private static bool IsSpaceErrorText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(text,
            @"(?:错误|ERROR)s+(?:112|39)|0x0*70|0x0*27|磁盘空间不足|磁盘已满|insufficient disk space",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>磁盘满熔断：对象记为"可恢复失败"（Transient），并如实说明已提前终止、怎么继续。</summary>
    private ObjectReceipt MarkSpaceFailed(ObjectReceipt receipt)
    {
        receipt.ErrorClass = ErrorClass.Transient;   // 可恢复：空间是外部条件，不是数据/权限问题
        receipt.Status = ObjectStatus.Failed;
        receipt.CompletedUtc = DateTime.UtcNow;
        receipt.ErrorDetail = $"目标磁盘空间不足（错误码 {_spaceCode}）：已主动终止本对象并提前结束本次运行，" +
            "避免对每个文件反复重试。请先释放目标盘空间（或更换目标盘）再执行 resume——" +
            "已完成的对象不会重传，失败对象会自动重试。";
        _log.Error("对象 {Id} 因目标盘空间不足终止：{Detail}", receipt.ObjectId, receipt.ErrorDetail);
        return receipt;
    }

    /// <summary>
    /// 本次运行的平均速率 = 本次运行真正新增的落盘字节 ÷ 本次运行的有效用时（不含暂停等待）。
    /// 与界面上的"任务实测"文案同一口径；不再用 robocopy 日志解析出的滞后瞬时值。
    /// </summary>
    private double CumulativeSpeed(long completedBytes)
    {
        var elapsed = _runSw.Elapsed - TimeSpan.FromTicks(Interlocked.Read(ref _pausedTicks));
        if (elapsed.TotalSeconds < 0.5) return 0;
        var done = completedBytes - _runStartCompletedBytes;
        if (done <= 0) return 0;
        return done / elapsed.TotalSeconds;
    }

    private void Report(IProgress<ProgressSnapshot>? progress, JobState s, string? objId, string? objPath,
        double speed, string msg, long objDone = 0, long objTotal = 0, bool objLarge = false, double stall = 0)
    {
        var eta = speed > 0 && s.TotalBytes > 0 ? Math.Max(0, s.TotalBytes - s.CompletedBytes) / speed : double.NaN;
        progress?.Report(new ProgressSnapshot(
            s.Phase, s.TotalObjects, s.CompletedObjects, s.FailedObjects,
            s.TotalBytes, s.CompletedBytes, Percent(s),
            speed, eta, objId, objPath, msg, objDone, objTotal, objLarge, stall));
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
