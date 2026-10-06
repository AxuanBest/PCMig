using System.Diagnostics;
using PCMig.Core.Diagnostics;
using PCMig.Core.Jobs;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Native;
using PCMig.Core.Util;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
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
    double StallSeconds = 0,
    // ---- FIX BATCH 1 / BATCH 2（暂停真值：Single Source of Truth）----
    // 这一组字段是"引擎真正知道的暂停事实"，UI 只允许映射它们，不允许自己猜（旧实现里
    // ProgressSnapshot 只有一个 Phase，UI 于是用 IsPaused = (Phase==Paused) 反推，并在点击后立刻
    // 乐观地把自己标成已暂停 ⇒ 真机上"传输还在跑、界面写着已暂停"）。
    /// <summary>暂停状态机真值：None / Pausing（请求已受理、正在等 worker 停）/ Paused / PauseFailed。</summary>
    PauseState PauseState = PauseState.None,
    /// <summary>请求被引擎看到并开始处理的时刻（UTC）。</summary>
    DateTime? PauseRequestedAt = null,
    /// <summary>worker 真的停住、暂停**达成**的时刻（UTC）。未达成为 null。</summary>
    DateTime? PauseAchievedAt = null,
    /// <summary>暂停的最终业务结果：None（未发生）/ Achieved / Failed。</summary>
    PauseOutcomeKind PauseOutcome = PauseOutcomeKind.None,
    /// <summary>暂停失败原因（仅在 <see cref="PauseOutcomeKind.Failed"/> 时有值）。</summary>
    string? PauseFailureReason = null,
    // ---- FIX BATCH 4（§7.1 进度真值：Single Source of Truth）----
    /// <summary>
    /// 进度唯一真值（P1-1）。顶栏进度条 / 底栏进度条 / 百分比 / 字节数 / 速率 / ETA **只允许**读这里的
    /// <see cref="ProgressTruthSnapshot.DisplayedTransferredBytes"/> 与
    /// <see cref="ProgressTruthSnapshot.Percent"/> / <see cref="ProgressTruthSnapshot.SpeedBytesPerSecond"/> /
    /// <see cref="ProgressTruthSnapshot.EtaSeconds"/>，不得再各自算一套（旧实现两处口径分叉：
    /// job-state 47.29% vs 界面 49.6%）。为 null 仅出现在"非引擎路径构造的临时快照"（测试/兼容）。</summary>
    ProgressTruthSnapshot? Truth = null);

/// <summary>
/// 传输编排器：PCMig 的心脏。
///  - 对象级调度：一个对象跑完（Bulk 通道 + 可选 Large 通道）→ 写 Receipt → 下一个
///  - 暂停（FIX BATCH 1 新语义）：pause.request 文件驱动；**任意模式都是"立即打断当前对象"**，
///    worker 停住才叫 Paused，10 s 硬失败 SLA 内停不住就如实报 PauseFailed（迁移仍在进行）。
///    `"Cooperative"` 这个名字现在只表示"由 UI 发起的普通暂停"，**不再表示"等当前对象传完"**——
///    旧语义在 28.5 GB 大对象下等于"永不暂停"，已被真机证伪（JOB-20261004-171522-b785）。
///  - 断点恢复：Receipt 是唯一权威；job-state.json 只是视图，损坏可从 Receipt 重建
///  - 错误分类：Transient（网络等）→ 对象级重试+退避；Permanent → 记 Receipt 继续下一对象
/// </summary>
public sealed class TransferOrchestrator
{
    private readonly JobContext _ctx;
    private readonly MigrationMatrix _matrix;
    private readonly ILogger _log;
    private readonly ITransferWorker _runner;

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
    /// <summary>
    /// F4：每开始一趟对象级尝试就自增，进度轮询据此把"本趟已确认字节"的单调基线重开——
    /// 否则上一趟失败时记进去的字节会一直留在分子上，重试几次就出现"分子 &gt; 计划"的假进度。
    /// </summary>
    private long _attemptEpoch;
    /// <summary>
    /// F12：只要发生一次"进度回冲"（robocopy 明确报某文件失败、把预记字节扣掉）就自增，
    /// 进度轮询据此重开单调下限——否则那一瞬间被采到的假高值会被 Math.Max 永久钉住到本趟结束
    /// （D01 r4 真机：11 秒内 1.4%→98.1%，随后钉在 99.9%，而目标盘上只有 20.4 MB）。
    /// </summary>
    private long _creditRetractEpoch;
    private long _largeCompletedBytes;          // large(/Z) 通道：已切换到下一个文件的前序大文件入账字节
    private string? _largeFileTarget;           // large(/Z) 通道：当前大文件的目标路径
    private string? _largeFileSourcePath;       // ★ Round-3 PHASE A ★ 当前大文件的**源**路径（失败记忆与回冲都用源路径作键，与 /MT 通道一致）
    private long _largeFileStartLen;            // 该大文件开始前的已有长度（续传部分不重复计）
    private long _largeFileApproxSize;          // 当前大文件的近似大小（robocopy 输出解析值，兜底用）
    private PassKind _currentPass;
    /// <summary>本 pass 是否走串行 /Z（只有串行才能按"当前大文件"精确入账；/MT+/J 走多文件并行，按行累计+枚举兜底）。</summary>
    private bool _serialLargePass;
    private PlannedObject? _activeObject;
    private DateTime _lastFileEventAt = DateTime.MinValue; // 最近一次解析到文件行的时刻
    private DateTime _lastEnumAt = DateTime.MinValue;      // 最近一次回退枚举的时刻
    private bool _enumSkipLogged;                          // 本对象是否已记录"该通道跳过回退枚举"的原因
    private TimeSpan _enumInterval = TimeSpan.FromSeconds(8); // 自适应枚举间隔（按上次枚举耗时×3，8s~120s）

    // ---- FIX BATCH 4（P1-1 进度真值）----
    /// <summary>本拍被进程 I/O 遥测确认的飞行中字节（来源标注用；只用于显示）。</summary>
    private long _ioConfirmedBytes;
    /// <summary>当前对象的重试/回冲真值（None / Retrying / RollingBack）——进度下降时必须能被解释。</summary>
    private RetryState _currentRetryState;

    // ---- Round-3 PHASE A（2026-10-05，P0 真值污染）----
    // 为什么必须有它：/Z 可续传会先给目标文件预分配最终长度（CurrentLargePartial 与
    //   TrustsTargetStatForProgress 都据此拒绝信任目标长度），但 MarkInterrupted 却无条件走
    //   MeasureSettledTarget ⇒ 把"只真传了 30 GB、长度却已预分配成 42 GB"的对象记成 42 GB，
    //   经 baseBytes += receipt.TargetBytes 抬进 state.CompletedBytes，再被 RunningPercentCeiling
    //   夹成 99.9%。用户真机视频：暂停前 72~74%，暂停瞬间 99.9% / 41.96 GB，Resume 后长期 99.9%，
    //   而对象完成数仍是 0/41 —— 这不是呈现层问题，是 Core 真值被污染。
    // 这里保存的是**已经过 ProgressTruthSnapshot.Create 同一套规则验证**的本对象确认量；
    //   绝不在结算时重新读目标 FileInfo.Length。
    /// <summary>本对象最近一次"可信确认的对象字节"（唯一允许用于 Interrupted 结算的量）。</summary>
    private long _lastTrustedObjectConfirmedBytes;
    /// <summary>本对象最近一次可信的**显示分子**（诊断与交叉校验用，不参与结算）。</summary>
    private long _lastTrustedDisplayedBytes;
    /// <summary>上面两个量所属的尝试趟次（attemptEpoch）；与当前趟不一致说明是新趟，必须重开。</summary>
    private long _lastTrustedAttemptEpoch;

    // ---- Round-2 2026-10-05（§3.1 真值源 trace）----
    // 为什么需要它：用户视频显示"进度条仍在一段一段跳"，但"轮询 200 ms 一次"不等于"每 200 ms 都有一个
    //   有用的新真值"。在猜测之前必须先区分四种情况：
    //     A. 进程 I/O 计数器不可用 / 采样失败；
    //     B. 可用但长时间不变（真值本身是稀疏的）；
    //     C. 它在平滑变化，只是被上层某一层折叠成"重复值 + 大跳"；
    //     D. 恢复时基线一次性发现了已存在的字节，大跳是真实且不可避免的。
    //   本 trace 只落盘、只观测，不参与任何业务语义；PCMIG_PROGRESS_TRACE=1 时才启用。
    private static readonly bool ProgressTraceEnabled = ResolveProgressTraceEnabled();
    private long _lastTraceDisplayedBytes = -1;
    private DateTime _lastTraceUtc = DateTime.MinValue;
    private int _traceSamples;

    private static bool ResolveProgressTraceEnabled()
    {
        try
        {
            var v = Environment.GetEnvironmentVariable("PCMIG_PROGRESS_TRACE");
            return v is not null && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    // ---- 错误风暴检测 + 进度回冲 ----
    // F12：文件级错误行解析统一走 RobocopyRunner.TryParseFileErrorLine，保证"入账的键"与"回冲的键"
    //   由同一段代码产出同一字符串（旧实现各自取名字：/MT 重试行的名字带着 "正在重试..." 后缀，
    //   于是回冲永远找不到那个键，失败的大文件字节永久留在分子上）。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _pendingCredit =
        new(StringComparer.OrdinalIgnoreCase); // 已按"新文件"行入账但尚未确认成功的文件（路径→字节）
    /// <summary>
    /// F12f：本趟已经报过文件级错误的源路径。robocopy 的内部重试会再报一次同一条"新文件"行，
    /// 若照旧入账，已经被回冲掉的字节会立刻回到分子上（D01 r5 真机：3 次重试 = 3 倍入账 ⇒ 界面钉在 99.9%）。
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _failedCreditPaths =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ★ Round-3 PHASE A ★ /Z 大文件通道"已入账但结果未知"的**目标路径**→已入账字节。
    ///
    /// 为什么必须单独有一份：/Z 通道的入账时机是"下一个大文件的『新文件』行到达 ⇒ 前一个大文件必然已复制完"
    /// （<see cref="CreditCurrentLarge"/>），而入账依据是 **robocopy 解析出的近似大小**——因为 /Z 会先把目标
    /// 文件预分配成最终长度，实测增量恒为 0，取大值必然落到近似值上。
    ///
    /// 真机反例（PHASE A 第 3 轮，目标盘只剩 1.6 GB）：robocopy 的 stdout 是块缓冲，6 个大文件的
    /// "新文件"行与紧随其后的 `错误 112 (0x00000070) 磁盘空间不足` **挤在同一批里**到达 ⇒ 42 GiB 全部
    /// 入账、而一个字节都没落盘 ⇒ 界面报 42 GB / 42 GB（99.9%）+ 对象 0/41。旧实现只有 /MT 通道
    /// （<see cref="_pendingCredit"/>）能按错误行回冲，/Z 通道的入账没有任何回冲键，于是失败字节永久留在分子上。
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _largeCredited =
        new(StringComparer.OrdinalIgnoreCase);

    private long _passFileAttempts;   // 本对象文件尝试数（含失败）
    private long _passErrors;         // 本对象文件错误数
    private string? _stormCode;       // 触发风暴终止的错误码（null=未触发）
    private string? _spaceCode;       // 目标盘空间不足的错误码（T07 熔断：null=未触发）

    // ---- ★ 缺陷 B12b4（数据完整性）★ 传输**过程中**的源身份守护 ----
    // 只在开跑前校验挡住的是"点续传时源已被换底"这一段；B12b4 的真实链路还有另一段：
    // 换底发生在**首轮传输飞行中**，那时 robocopy 仍在按 UNC 路径取数——共享名没变，
    // 于是一部分冒名数据照样落进目标端（实测：目标端出现只在冒名树里存在的文件名）。
    // 这里在对象传输期间按固定间隔复验源身份，一旦变化立即终止当前 robocopy 与本次运行，
    // 并把"已写入的部分必须重新核验"如实写进结论——绝不静默继续、绝不宣称完成。
    private string? _guardObjectPath;         // 当前受守护对象的源路径
    private string? _guardExpectedIdentity;   // 该对象的计划期身份基线
    private DateTime _guardNextCheckUtc = DateTime.MinValue;
    private volatile bool _identityLost;      // 已检测到源身份在传输中变化
    private string? _identityLostDetail;
    private static readonly TimeSpan IdentityGuardInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 复验当前受守护对象的源身份。不一致 ⇒ 立即 kill 当前 robocopy 并中止本次运行。
    /// 取不到当前身份时**不判定**（网络抖动/权限瞬变不能当成"身份变了"）。
    /// </summary>
    private void GuardActiveObjectIdentity(bool force = false)
    {
        if (_identityLost) return;
        var expected = _guardExpectedIdentity;
        var path = _guardObjectPath;
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(path)) return;

        var now = DateTime.UtcNow;
        if (!force && now < _guardNextCheckUtc) return;
        _guardNextCheckUtc = now + IdentityGuardInterval;

        var actual = SourceIdentity.Capture(path, _log);
        if (string.IsNullOrEmpty(actual))
        {
            // 取不到一律"不判定"（不误伤正常续传），但必须**可见**——否则事后无法区分
            // "守护没生效"与"当前指纹取不到"（TV-B12b4 tv2 就吃过这个哑巴亏）。
            if (force) _log.Information("源身份守护：对象 {Path} 本次取不到当前指纹，保持不判定", path);
            return;
        }
        if (!SourceIdentity.IdentityChanged(expected, actual))
        {
            if (force) _log.Information("源身份守护：{Path} 与基线一致（基线={Expected}）", path, expected);
            return;
        }

        _identityLost = true;
        _identityLostDetail = $"{path} {expected} → {actual}";
        _log.Error("源身份在传输过程中发生变化（同名共享被换底）：{Detail} —— 立即终止当前对象，" +
                   "本次运行不再继续任何对象；飞行中已写入的部分必须重新核验", _identityLostDetail);
        _runner.KillCurrent();
    }

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
        GuardActiveObjectIdentity();   // ★ B12b4 ★ 飞行中源身份复验（内部按间隔节流）
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
                // ★ Round-3 PHASE A ★ 记源路径：入账的键必须是源路径（与 OnRunnerErrorLine / _failedCreditPaths 同一键空间），
                //   否则 /Z 通道的入账永远找不到对应的回冲键——磁盘满时界面就会报"全部完成"（真机 42 GB / 99.9%）。
                _largeFileSourcePath = sourcePath;
                _largeFileApproxSize = approxBytes;
                try { _largeFileStartLen = File.Exists(target) ? new FileInfo(target).Length : 0; }
                catch { _largeFileStartLen = 0; }
            }
        }
        else
        {
            // 先记账、按错误行回冲：robocopy 块缓冲下"新文件"行先于结果到达，失败时由 OnRunnerErrorLine 扣回
            // F12f：同一文件在同一趟里会被反复报（"正在重试..."），只认第一次；本趟已判失败的路径不再入账。
            var creditedThisPass = _pendingCredit.ContainsKey(sourcePath);
            var failedThisPass = _failedCreditPaths.ContainsKey(sourcePath);
            if (RobocopyRunner.ShouldCreditAnnouncement(creditedThisPass, failedThisPass)
                && _pendingCredit.TryAdd(sourcePath, approxBytes))
            {
                Interlocked.Add(ref _bulkCopiedBytes, approxBytes);
            }
            if (_pendingCredit.Count > 200_000) _pendingCredit.Clear(); // 防爆：超限放弃精确回冲（枚举通道兜底）
            if (_failedCreditPaths.Count > 200_000) _failedCreditPaths.Clear();
        }
    }

    /// <summary>robocopy 文件级错误行：①回冲误入账的进度字节 ②连续失败达到风暴阈值→主动杀死当前对象。</summary>
    private void OnRunnerErrorLine(string line)
    {
        if (!RobocopyRunner.TryParseFileErrorLine(line, out var code, out var srcPath)) return;

        // F12f：本趟记住这个路径已经失败——robocopy 随后的"正在重试"行不得再把它加回分子
        _failedCreditPaths.TryAdd(srcPath, 0);

        // ① 进度回冲：该文件从未成功，把"新文件"行预记的字节扣掉
        if (_pendingCredit.TryRemove(srcPath, out var credited))
        {
            Interlocked.Add(ref _bulkCopiedBytes, -credited);
            Interlocked.Increment(ref _creditRetractEpoch);   // F12：回冲即重开单调下限，界面必须立刻掉下来
        }
        // /Z 通道：① 已经按"前序大文件已复制完"入账过的，必须回冲；② 当前大文件失败则不再为其入账。
        //   ★ Round-3 PHASE A ★ ①是新增的：旧实现只有 /MT 通道能回冲，/Z 通道的入账没有任何回冲路径，
        //   于是"目标盘满 ⇒ 6 个大文件全部失败"时 42 GiB 虚报永久留在分子上（真机 42 GB / 42 GB（99.9%）、对象 0/41）。
        var retractedLarge = 0L;
        if (_largeCredited.TryRemove(srcPath, out var creditedLarge))
        {
            Interlocked.Add(ref _largeCompletedBytes, -creditedLarge);
            retractedLarge = creditedLarge;
        }
        if (string.Equals(_largeFileSourcePath, srcPath, StringComparison.OrdinalIgnoreCase))
        {
            _largeFileTarget = null;
            _largeFileSourcePath = null;
            Interlocked.Increment(ref _creditRetractEpoch);   // F12：当前大文件已判失败，实时增量作废
        }
        if (retractedLarge > 0)
        {
            Interlocked.Increment(ref _creditRetractEpoch);   // F12：回冲即重开单调下限，界面必须立刻掉下来
            _log.Information("大文件入账回冲：{SourcePath} 失败，扣回 {Bytes} 字节（/Z 通道按近似大小入账，失败后必须回冲）",
                srcPath, retractedLarge);
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
        var src = _largeFileSourcePath;
        long done;
        if (src != null && _failedCreditPaths.ContainsKey(src))
        {
            // ★ Round-3 PHASE A ★ 本趟已经见过这个大文件的错误行 ⇒ 一个字节都不认。
            //   robocopy 的 stdout 是块缓冲（且 /MT 与 /Z 混跑时顺序无保证），"新文件"行与紧随的错误行
            //   会挤在同一批里到达；旧实现只看 File.Exists，而 /Z 预分配会让**失败的大文件也"存在且长度正确"**
            //   ⇒ 真机实测：目标盘只剩 1.6 GB，6 个大文件全部 `错误 112 磁盘空间不足`，界面却报 42 GB / 42 GB（99.9%）。
            done = 0;
        }
        else
        {
            done = _largeFileApproxSize;
            try
            {
                if (File.Exists(p))
                    // 实测增量与近似大小取大：缓冲爆发时行到达晚于复制完成，实测增量=0，用近似值入账
                    done = Math.Max(_largeFileApproxSize, Math.Max(0, new FileInfo(p).Length - _largeFileStartLen));
                else
                    // F12：目标上根本没有这个文件（失败/被跳过/写不进去）就一个字节都不认——旧实现照样按
                    //   _largeFileApproxSize 入账，等于给"失败的大文件"记满进度（D01 r4 的 1 GB 就是这么来的）。
                    done = 0;
            }
            catch { /* 用近似值兜底 */ }
        }
        if (done > 0)
        {
            Interlocked.Add(ref _largeCompletedBytes, done);
            if (src != null) _largeCredited[src] = done;   // 迟到错误行按此回冲（本文件可能已不是"当前大文件"）
        }
        _largeFileTarget = null;
        _largeFileSourcePath = null;
    }

    /// <summary>/Z 大文件通道的实时落盘增量（只 stat 当前一个文件，O(1)）。</summary>
    private long CurrentLargePartial()
    {
        var p = _largeFileTarget;
        if (p == null) return 0;
        // F12：可续传（/Z）会先给目标文件预分配最终长度，stat 长度 ≠ 已确认落盘字节——与本文件对
        //   "回退枚举"的既有结论完全一致（该通道不用长度，只用 robocopy 已确认的输出）。
        if (!RobocopyRunner.TrustsTargetStatForProgress(_serialLargePass)) return 0;
        try { return File.Exists(p) ? Math.Max(0, new FileInfo(p).Length - _largeFileStartLen) : 0; }
        catch { return 0; }
    }

    /// <summary>robocopy 每开始复制一个文件（源路径, 近似字节）。供 UI 做"实时文件流"展示；不影响传输。</summary>
    public event Action<string, long>? FileCopied;

    /// <param name="worker">
    /// 传输 worker。默认 = 真实 robocopy（<see cref="RobocopyRunner"/>）。
    /// 仅暂停语义的定向测试会注入受控 worker；生产路径永远是默认值。
    /// </param>
    public TransferOrchestrator(JobContext ctx, MigrationMatrix matrix, ILogger log, ITransferWorker? worker = null)
    {
        _ctx = ctx;
        // 任务级自定义排除规则（超大数据模式）叠加到矩阵，传输口径与扫描/验证一致
        _matrix = matrix.WithExtraExclusions(ctx.Definition.CustomExclusions, log);
        _log = log.ForContext<TransferOrchestrator>();
        _runner = worker ?? new RobocopyRunner(log);
        // 行级输出/文件级回调只有真实 robocopy 通道才有（受控 worker 不产生行文本）
        if (_runner is RobocopyRunner robo)
        {
            // ★ 注册顺序 + 隔离（场景 H 根因修复 / 2026-10-06）★
            //   两个订阅者挂在**同一个** OutputLine 多播委托上，而 RobocopyRunner 用一个 try/catch
            //   包住整条链（RobocopyRunner.cs:631）：链上**任何一个**订阅者抛异常，排在它**之后**的
            //   订阅者全部被跳过，异常本身也被吞掉。
            //   2026-10-06 真机实录：WinUI 的「转发 UI」在错误行上抛（跨线程改 ObservableCollection
            //   组成的 FailItems），而它注册在 OnRunnerErrorLine 之前 ⇒ 熔断（T07）与两条回冲路径
            //   从未执行 ⇒ 目标盘满后 completedBytes 永久钉在 42 GiB、percent 钉在 99.9%。
            //   ① 核心逻辑先注册（顺序保证）；② 转发各自加 try/catch（隔离保证）。两者缺一不可。
            robo.OutputLine += OnRunnerErrorLine;                 // 错误风暴检测 + 进度回冲 + 盘满熔断（核心逻辑，必须先于 UI 转发）
            robo.OutputLine += line =>
            {
                try { OutputLine?.Invoke(line); }                 // 转发给 UI（报错区实时展示）
                catch { /* UI 订阅方异常绝不影响核心逻辑：委托链是共享的（同下方 FileCopied 的既有纪律） */ }
            };
            robo.FileCopied += (p, b) =>
            {
                OnRunnerFileCopied(p, b);
                try { FileCopied?.Invoke(p, b); } catch { /* UI 订阅方异常不影响传输 */ }
            };
        }
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

        // ---- ★ 缺陷 B12b4（数据完整性 / 假成功）★ 源身份守卫：开跑前校验，早于任何状态写入与复制 ----
        // 计划里固化了每个对象源根的文件系统身份指纹（卷序列号 + 目录文件 ID）。
        // 同名共享换底（共享名不变、背后目录/磁盘被换）会让指纹变化 ⇒ 立刻拒绝继续传输。
        // Run / Resume / Repair 共用本入口 ⇒ 三个路径都会被这道闸门拦住。
        var identityFindings = SourceIdentityGuard.Evaluate(plan.Objects, p => SourceIdentity.Capture(p, _log));
        if (identityFindings.Count > 0)
        {
            var message = SourceIdentityGuard.BuildAbortMessage(identityFindings);
            _log.Error("源身份校验未通过，拒绝继续传输：{Count} 个对象源身份已变化 | {Details}",
                identityFindings.Count,
                string.Join("; ", identityFindings.Take(8)
                    .Select(f => $"{f.ObjectId} {f.SourcePath} {SourceIdentity.Describe(f.Expected)}->{SourceIdentity.Describe(f.Actual)}")));
            state.Phase = JobPhase.Failed;
            state.LastError = "源身份校验未通过（同名换底）：已拒绝继续传输，本次未复制任何文件。";
            _ctx.SaveState(state);
            throw new InvalidOperationException(message);
        }

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
        // 恢复运行：已经没有暂停请求时，上一轮的暂停真值必须清掉（否则"已暂停"会残留成假绿）
        if (!File.Exists(_ctx.PauseRequestPath)) ResetPauseTruth(state);
        _ctx.SaveState(state);

        PublishTransfer(TransferEvents.JobRunStarted, null, DiagnosticLevel.Information, DiagnosticOutcome.Started,
            phase: nameof(JobPhase.Running));

        // ---- 本次运行的速率口径与熔断状态复位 ----
        _spaceCode = null;
        // ★ 缺陷 B12b4 ★ 每次运行的飞行中源身份守护状态必须复位（run/resume/repair 各自独立判定）
        _identityLost = false;
        _identityLostDetail = null;
        _guardObjectPath = null;
        _guardExpectedIdentity = null;
        _guardNextCheckUtc = DateTime.MinValue;
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

            // ★ 缺陷 B12b4 ★ 对象边界校验：进入该对象前先确认它的源位置还是计划时那一份。
            // 之后的传输期间由 OnRunnerFileCopied 里的周期性复验持续守护。
            _guardObjectPath = obj.SourcePath;
            _guardExpectedIdentity = obj.SourceIdentity;
            GuardActiveObjectIdentity(force: true);
            if (_identityLost)
            {
                Report(progress, state, obj.ObjectId, obj.SourcePath, 0, "检测到源身份变化：已终止本次运行");
                break;
            }

            Report(progress, state, obj.ObjectId, obj.SourcePath, 0, $"开始对象 {obj.ObjectId}",
                objTotal: Math.Max(obj.EstimatedBytes, 0), objLarge: obj.UseRestartablePass);

            PublishTransfer(TransferEvents.ObjectStarted, null, DiagnosticLevel.Information, DiagnosticOutcome.Started,
                objectId: obj.ObjectId, phase: nameof(JobPhase.Running));

            var receipt = await RunOneObjectAsync(obj, job, state, baseBytes, progress, ct);
            if (receipt == null) { interrupted = true; break; }  // 取消/Immediate 暂停导致中断

            _ctx.SaveReceipt(receipt);

            // ★ 对象级结果事件 ★ 两个通道的退出码分开记（-1 = 未执行该通道），
            //   规则的输入是这些结构化字段，而不是去解析日志文本。
            PublishTransfer(
                receipt.Status == ObjectStatus.Interrupted ? TransferEvents.ObjectInterrupted : TransferEvents.ObjectCompleted,
                new TrnObjectPayload(
                    receipt.Kind.ToString(),
                    receipt.TargetBytes,
                    receipt.TargetFiles,
                    receipt.RobocopyExitCodeBulk,
                    receipt.RobocopyExitCodeLarge,
                    receipt.ErrorClass.ToString(),
                    receipt.Attempt),
                receipt.Status switch
                {
                    ObjectStatus.Completed => DiagnosticLevel.Information,
                    ObjectStatus.Interrupted => DiagnosticLevel.Warning,
                    _ => DiagnosticLevel.Warning,
                },
                receipt.Status switch
                {
                    ObjectStatus.Completed => DiagnosticOutcome.Succeeded,
                    ObjectStatus.Interrupted => DiagnosticOutcome.Canceled,
                    _ => DiagnosticOutcome.Failed,
                },
                objectId: obj.ObjectId,
                exitCode: receipt.RobocopyExitCodeBulk);
            // 复拷已完成对象：先扣掉上一次计入的字节，避免同一对象被累计两次
            if (wasCompleted) baseBytes -= prevReceipt!.TargetBytes;
            baseBytes += receipt.TargetBytes;
            var settledBytes = Math.Min(baseBytes, Math.Max(plan.TotalBytes, baseBytes));
            // ★ 进度真值单调性（UI Closure 2026-10-05，用户人工验收项）★
            // 被判为 Interrupted 的回执（暂停/取消打断）即使实测不到落盘量，也**绝不能把已经确认传完的字节往下改**：
            // 中断只表示"这个对象没完成"，不表示"之前传的都不算数"。
            // 旧实现这里无条件把 CompletedBytes 赋成 baseBytes ⇒ 暂停打断第一个对象时 baseBytes=0
            // ⇒ 已传 28.1 GiB 被覆写成 0 ⇒ 真值快照重算 percent=0 ⇒ 界面出现「0.0% / 0 B / 剩余 100% 未传」。
            state.CompletedBytes = receipt.Status == ObjectStatus.Interrupted
                ? Math.Max(state.CompletedBytes, settledBytes)
                : settledBytes;
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
        // 暂停是否**真的成立**：引擎侧已确认停住 + 请求仍然存在（未被 resume 抢先移除）。
        // 单凭 pause.request 是否存在判断 = 旧实现的假绿口径（请求写成功就报已暂停）。
        var pauseHolds = state.PauseState == PauseState.Paused && File.Exists(_ctx.PauseRequestPath);
        if (state.PauseState == PauseState.Paused && !pauseHolds) ResetPauseTruth(state);   // 暂停刚达成即被 resume 抢先：如实记中断
        if (spaceAborted || _spaceCode != null)
        {
            // 磁盘满不是"任务被中断"，而是"可恢复失败"：阶段如实记为 CompletedWithErrors，
            // 把该做的下一步（释放空间 → resume）写进 LastError，绝不假装完成。
            state.FailedObjects = Math.Max(0, state.TotalObjects - state.CompletedObjects);
            state.Phase = JobPhase.CompletedWithErrors;
            state.LastError = "目标磁盘空间不足：本次运行已提前停止（避免对每个文件反复重试）。" +
                "请释放目标盘空间后执行 resume，已完成的对象不会重传。";
            _log.Error("传输提前结束：目标盘空间不足（完成 {Done}/{Total} 对象）", state.CompletedObjects, state.TotalObjects);
            // _spaceCode 是文本形式的错误码（既有业务口径）⇒ 能解析成数字就填进 Win32Error 域，否则留空（不伪造）。
            PublishTransfer(TransferEvents.SpaceAbortRequested, null, DiagnosticLevel.Error, DiagnosticOutcome.Failed,
                win32: int.TryParse(_spaceCode, out var spaceCodeValue) ? spaceCodeValue : null,
                phase: nameof(JobPhase.CompletedWithErrors));
        }
        else if (_identityLost)
        {
            // ★ 缺陷 B12b4 ★ 飞行中检测到源身份变化：**不是**"被暂停/中断"，而是
            // "本次运行的数据来源在复制途中变了"——阶段必须如实记为 Failed，
            // 并把"可能已写入部分非源数据、必须重新核验"写清楚，绝不并入普通中断口径。
            state.FailedObjects = Math.Max(0, state.TotalObjects - state.CompletedObjects);
            state.Phase = JobPhase.Failed;
            state.LastError = $"源身份在传输过程中发生变化（同名共享被换底）：已立即终止本次运行。差异：{_identityLostDetail}";
            _log.Error("传输终止：源身份在传输中变化（完成 {Done}/{Total} 对象）。差异：{Detail}",
                state.CompletedObjects, state.TotalObjects, _identityLostDetail);
        }
        else if (interrupted || ct.IsCancellationRequested)
        {
            state.Phase = pauseHolds ? JobPhase.Paused : JobPhase.Interrupted;
            state.LastError = pauseHolds
                ? "已暂停：传输已停止，当前对象未完成（resume 会重跑该对象；已完成的对象不会重传）"
                : "任务被中断；可用 resume 续传（已完成的对象不会重传）";
            // ★ 请求态与真实态分开 ★ 只有**引擎确认停住**才是"已暂停"；否则是"被中断/取消"。
            PublishTransfer(
                pauseHolds ? TransferEvents.Paused : TransferEvents.StopObserved,
                null, DiagnosticLevel.Information,
                pauseHolds ? DiagnosticOutcome.Accepted : DiagnosticOutcome.Canceled,
                phase: state.Phase.ToString());
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
        else if (pauseHolds)
        {
            // 暂停在最后一个对象收尾的同一瞬间达成：用户的停止意图优先，阶段如实记 Paused。
            // resume 时已完成的对象按回执跳过（robocopy 增量），不会再传一遍。
            state.Phase = JobPhase.Paused;
            state.LastError = "已暂停（暂停达成时本阶段可传数据已传完；resume 只会做校验与续传）。";
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

        PublishTransfer(TransferEvents.JobRunCompleted, null,
            state.Phase is JobPhase.Completed ? DiagnosticLevel.Information : DiagnosticLevel.Warning,
            state.Phase switch
            {
                JobPhase.Completed => DiagnosticOutcome.Succeeded,
                JobPhase.Paused => DiagnosticOutcome.Accepted,
                JobPhase.Interrupted or JobPhase.Canceled => DiagnosticOutcome.Canceled,
                _ => DiagnosticOutcome.Failed,
            },
            phase: state.Phase.ToString());

        return state.Phase;
    }

    /// <summary>
    /// 发布观察事件（**绝不影响传输**）：未启用时提前返回；上下文带 Job 归属，
    /// 传了 objectId 时再带对象归属。规则的输入是 typed payload + envelope 字段，不是日志文本。
    /// </summary>
    private void PublishTransfer(
        EventDescriptor descriptor,
        IDiagnosticPayload? payload,
        DiagnosticLevel level,
        DiagnosticOutcome outcome,
        string? objectId = null,
        int? exitCode = null,
        int? durationMs = null,
        string? phase = null,
        int? win32 = null,
        string? errorClass = null)
    {
        try
        {
            var publisher = CoreDiagnostics.Sink.Publisher;
            if (!publisher.IsEnabledFor(descriptor)) return;

            var context = objectId is null ? _ctx.Diagnostics : _ctx.Diagnostics.WithObject(objectId);
            publisher.TryPublish(new DiagnosticEventDraft(
                descriptor,
                context,
                payload,
                Level: level,
                Outcome: outcome,
                RobocopyExitCode: exitCode,
                DurationMs: durationMs,
                Win32Error: win32,
                ErrorDomain: win32 is null ? ErrorDomain.None : ErrorDomain.Win32,
                Phase: phase,
                StateOwner: StateOwner.Core));
        }
        catch (Exception)
        {
            // 观察失败绝不影响传输。
        }
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

        // ── F2：同一对象在一次任务里可能被跑第二趟（停止后恢复 / 修复重拷）──
        //   旧实现每趟都从 attempt=1 起算，作业记录里两趟完全一样：看不出"修复跑了没有、收敛了没有"。
        //   把已有回执的尝试数作为基数，本次的 attempt 就从"第 N+1 次"开始记。
        var attemptBase = 0;
        try
        {
            attemptBase = _ctx.LoadReceipts()
                .Where(r => string.Equals(r.ObjectId, obj.ObjectId, StringComparison.OrdinalIgnoreCase))
                .Sum(r => Math.Max(0, r.Attempt));
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "读取既有回执求 attempt 基数失败：按首趟计");
            attemptBase = 0;
        }

        // 重置本对象的进度跟踪状态
        Interlocked.Exchange(ref _bulkCopiedBytes, 0);
        Interlocked.Exchange(ref _largeCompletedBytes, 0);
        Interlocked.Exchange(ref _passFileAttempts, 0);
        Interlocked.Exchange(ref _passErrors, 0);
        _stormCode = null;
        _pendingCredit.Clear();
        _failedCreditPaths.Clear(); // F12f：新的一趟尝试，失败记忆清零
        _largeCredited.Clear();     // ★ Round-3 PHASE A ★ 新对象/新趟：旧对象的入账回冲键不得跨对象残留
        _largeFileTarget = null; _largeFileSourcePath = null; _largeFileStartLen = 0; _largeFileApproxSize = 0;
        _currentRetryState = RetryState.None;   // FIX BATCH 4：新对象从"没有重试"开始
        Interlocked.Exchange(ref _ioConfirmedBytes, 0);
        // Round-3 PHASE A：本对象的可信 checkpoint 随对象重开。
        Interlocked.Exchange(ref _lastTrustedObjectConfirmedBytes, 0);
        Interlocked.Exchange(ref _lastTrustedDisplayedBytes, 0);
        Interlocked.Exchange(ref _lastTrustedAttemptEpoch, 0);
        _activeObject = obj; _currentPass = passKind;
        _lastFileEventAt = DateTime.MinValue; _lastEnumAt = DateTime.MinValue;
        _enumSkipLogged = false;

        // ---- 强制覆盖（修复专用）：先删目标侧同名文件，逼 robocopy 从共享重新拉取 ----
        // 不删的话，robocopy 对"大小+时间相同"的文件一律跳过（实测 /IS /IT 也无效）。
        if (ForceOverwriteFromSource)
        {
            var purge = RepairPurge.PurgeTargetCopies(obj, job.Options, _matrix, _log);
            if (purge.Files > 0 || purge.SkippedLarge > 0)
                _log.Information("强制覆盖: 对象 {Id} 已删除目标侧 {Files} 个同名文件（{Bytes}），将从共享重新拉取{Skip}",
                    obj.ObjectId, purge.Files, Format.Bytes(purge.Bytes),
                    purge.SkippedLarge > 0 ? $"；{purge.SkippedLarge} 个大于分流阈值且本次不走 /Z 通道的文件已跳过不删" : "");

            // ★ D6.1 §16 观察点 ★ 净化聚合（含**失败数**）：只陈述事实，不改任何删除决策。
            Diagnostics.CoreDiagnostics.PublishCore(
                RepairEvents.PurgeAttempted,
                new RprPurgePayload(purge.Files, purge.Bytes, purge.SkippedLarge, purge.Failed, "force-overwrite"),
                _ctx.Diagnostics.WithObject(obj.ObjectId),
                DiagnosticLevel.Information,
                purge.Failed > 0 ? DiagnosticOutcome.Unknown : DiagnosticOutcome.Succeeded,
                "TransferOrchestrator");

            if (purge.Failed > 0)
                Diagnostics.CoreDiagnostics.PublishCore(
                    RepairEvents.PurgeFailed,
                    new RprPurgePayload(purge.Files, purge.Bytes, purge.SkippedLarge, purge.Failed, "delete-failed"),
                    _ctx.Diagnostics.WithObject(obj.ObjectId),
                    DiagnosticLevel.Warning,
                    DiagnosticOutcome.Failed,
                    "TransferOrchestrator");
        }

        // 进度轮询任务：周期性实测目标目录字节增长 → 更新 CompletedBytes/速度/ETA
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var speedState = new SpeedTracker();
        var pollTask = PollProgressAsync(obj, state, baseBytes, speedState, progress, pollCts.Token);
        // 暂停监视：**任意模式**（Cooperative / Immediate）都立即打断当前对象，不等对象边界
        var pauseTask = MonitorPauseRequestAsync(obj, state, progress, pollCts.Token);

        try
        {
            // ---- Pass 1: Bulk（RootFiles 对象只有这一遍）----
            _currentPass = passKind;
            var bulk = await RunPassWithRetryAsync(obj, passKind, roboLog, attempts, maxAttempts, attemptBase, receipt, ct);
            receipt.RobocopyExitCodeBulk = bulk?.ExitCode ?? -1;
            if (_spaceCode != null) return MarkSpaceFailed(receipt);     // 目标盘满：可恢复失败，不重试不续跑
            if (_stormCode != null) return MarkStormFailed(receipt);     // 错误风暴：主动终止，不重试不续跑
            if (bulk == null) return MarkInterrupted(receipt, obj,
                ResolveLiveTrustedObjectBytes(obj),
                Interlocked.Read(ref _lastTrustedAttemptEpoch));       // 取消
            if (!bulk.Success)
            {
                receipt.ErrorClass = Classify(bulk);
                // 具体原因优先用"多少文件失败 + 主因 + 举例"的汇总；只留最后一行则等于没说（实测的"复制失败"投诉）
                // F1：不匹配位（4）由退出码直接说明"有条目没落到目标"——日志文本解析不到时也必须给出这条结论
                receipt.ErrorDetail = DetailFor(bulk);
                receipt.Status = ObjectStatus.Failed;
                receipt.CompletedUtc = DateTime.UtcNow;
                // F7（GROUP D 复核时发现的老问题）：失败对象也必须**实测目标落盘量**。
                //   旧实现只在成功/大文件失败分支量一次，bulk 失败分支留 0 ⇒ 报告里"已传 0 B"，
                //   而盘上实际躺着这趟已复制的那部分（D04 现场：三个文件被占用，其余 35 GB 已落盘）。
                //   "失败"只是判定，不能顺手把"已经落盘多少"也说成 0。
                MeasureSettledTarget(receipt, obj);
                _log.Error("对象 {Id} {Pass} 通道失败: Exit={Code}（{Meaning}） {Err}",
                    obj.ObjectId, passKind, bulk.ExitCode,
                    PCMig.Core.Util.ErrorTranslator.ExitCodeText(bulk.ExitCode), receipt.ErrorDetail);
                return receipt;
            }

            // ---- Pass 2: Large（/Z 可续传，仅扫描判定有大文件时）----
            if (obj.UseRestartablePass && passKind == PassKind.Bulk)
            {
                _currentPass = PassKind.Large;
                var large = await RunPassWithRetryAsync(obj, PassKind.Large, roboLog, attempts, maxAttempts, attemptBase, receipt, ct);
                receipt.RobocopyExitCodeLarge = large?.ExitCode ?? -1;
                if (_spaceCode != null) return MarkSpaceFailed(receipt);
                if (_stormCode != null) return MarkStormFailed(receipt);
                if (large == null) return MarkInterrupted(receipt, obj,
                    ResolveLiveTrustedObjectBytes(obj),
                    Interlocked.Read(ref _lastTrustedAttemptEpoch));
                if (!large.Success)
                {
                    receipt.ErrorClass = Classify(large);
                    receipt.ErrorDetail = DetailFor(large);
                    receipt.Status = ObjectStatus.CompletedWithErrors; // 主体已成，大文件有问题单独标记
                    receipt.CompletedUtc = DateTime.UtcNow;
                    MeasureSettledTarget(receipt, obj);
                    return receipt;
                }
            }

            receipt.Status = ObjectStatus.Completed;
            receipt.CompletedUtc = DateTime.UtcNow;
            MeasureSettledTarget(receipt, obj);
            return receipt;
        }
        finally
        {
            pollCts.Cancel();
            try { await Task.WhenAll(pollTask, pauseTask); } catch { /* 轮询任务取消属正常 */ }
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
        int attempts, int maxAttempts, int attemptBase, ObjectReceipt receipt, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            receipt.Attempt = attemptBase + attempt;   // F2：跨趟累计，作业记录里能区分"第几趟尝试"

            // ── F4：每一趟尝试都从"本对象已确认的落盘基线"重新累计 ──
            //   旧实现只在对象开始时清一次：磁盘满那一趟里记进去的字节会一直留在分子上，
            //   重试三次就出现底栏 4.02 GB / 1.02 GB（分子大于计划）这种"看起来快完成"的假进度。
            Interlocked.Exchange(ref _bulkCopiedBytes, 0);
            Interlocked.Exchange(ref _largeCompletedBytes, 0);
            _pendingCredit.Clear();
            _failedCreditPaths.Clear(); // F12f：新的一趟尝试，失败记忆清零（失败只在本趟内被记住）
            _largeCredited.Clear();     // ★ Round-3 PHASE A ★ 新的一趟尝试：上一趟的 /Z 入账回冲键作废
            _largeFileTarget = null; _largeFileSourcePath = null; _largeFileStartLen = 0; _largeFileApproxSize = 0;
        _currentRetryState = RetryState.None;   // FIX BATCH 4：新对象从"没有重试"开始
        Interlocked.Exchange(ref _ioConfirmedBytes, 0);
            // Round-3 PHASE A：每一趟尝试都重开可信 checkpoint（与上面的单调基线同理）——
            //   否则上一趟确认到的高值会被带进新趟，暂停结算时又变成假进度。
            Interlocked.Exchange(ref _lastTrustedObjectConfirmedBytes, 0);
            Interlocked.Exchange(ref _lastTrustedDisplayedBytes, 0);
            Interlocked.Exchange(ref _lastTrustedAttemptEpoch, 0);
            _lastFileEventAt = DateTime.MinValue;
            Interlocked.Increment(ref _attemptEpoch);   // 通知进度轮询：单调基线随本趟尝试重开

            // ★ D6.1 §16 观察点 ★ 重试开始（attempt>1 时）——只记事实，不改重试决策与通道选择。
            if (attempt > 1)
                PublishTransfer(TransferEvents.RetryStarted,
                    new TrnRetryPayload(pass.ToString(), attempt, maxAttempts, 0),
                    DiagnosticLevel.Information, DiagnosticOutcome.Accepted, obj.ObjectId);
            // v0.3.8 大文件通道：auto 下首次用 /MT+/J（吞吐），重试时退 /Z（续传优先）
            var restartable = RobocopyRunner.UseRestartableZ(_ctx.Definition.Options, pass, attempt);
            _serialLargePass = pass == PassKind.Large && restartable;
            if (pass == PassKind.Large)
                _log.Information("对象 {Id} 大文件通道: {Channel}（第 {Attempt} 次尝试）",
                    obj.ObjectId,
                    RobocopyRunner.ChannelText(pass, restartable,
                        RobocopyRunner.UseUnbufferedJ(obj.SourcePath, obj.TargetPath, _ctx.Definition.Options)),
                    attempt);
            // ★ 把对象/尝试/通道的不可变上下文交给 runner ★
            //   这样 robocopy 的子进程事件（PID/退出码/被杀/错误聚合）能**正确归属到对象**，
            //   而不是靠时间顺序去猜。下一次对象开始时会覆盖它（无需 finally 复位：
            //   编排器自己的事件用的是 _ctx.Diagnostics，不读 runner 的这份副本）。
            _runner.Diagnostics = _ctx.Diagnostics.WithObject(obj.ObjectId).WithAttempt(attempt)
                .WithPass(RobocopyRunner.PassToken(pass));

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

                // ★ D6.1 §16 ★ 重试排期（含退避毫秒与稳定错误分类）——只观察，不改退避算法。
                PublishTransfer(TransferEvents.RetryScheduled,
                    new TrnRetryPayload(pass.ToString(), attempt, maxAttempts, (long)backoff.TotalMilliseconds, cls.ToString()),
                    DiagnosticLevel.Warning, DiagnosticOutcome.Unknown, obj.ObjectId,
                    errorClass: cls.ToString());

                await Task.Delay(backoff, ct);
                continue;
            }
            return result; // Permanent 或重试耗尽
        }
        return null;
    }

    /// <summary>
    /// 枚举目标侧实测落盘量（**已完成/已结算**对象的 Receipt 以此为准，不依赖 robocopy 自报）。
    /// <para>
    /// ★ Round-3 PHASE A ★ 由 <c>MeasureTarget</c> 改名而来。<b>名字即纪律</b>：只有对象已经
    /// "落定"（Completed / 真正可实测的失败收尾 / Verifier 复验）才允许调用它；
    /// 被打断（Pause / Stop / Cancel）的对象**绝不能**走这里 —— 见
    /// <see cref="CaptureInterruptedConfirmedProgress"/>（<c>/Z</c> 预分配会把最终长度当成已传字节）。
    /// </para>
    /// </summary>
    private void MeasureSettledTarget(ObjectReceipt receipt, PlannedObject obj)
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

    /// <summary>
    /// 被打断（暂停 / 取消 / 停止）的回执。
    /// <para>
    /// ★ Round-3 PHASE A（2026-10-05，P0 真值污染修复）★
    /// 旧实现在这里**无条件** <c>MeasureTarget</c>（现名 <see cref="MeasureSettledTarget"/>）。那对
    /// "目标长度 == 真实落盘量"的通道是对的，但对 <c>/Z</c> 可续传通道是灾难：robocopy 会先给目标文件
    /// 预分配最终长度，于是"只真传了 30 GB、长度却已经是 42 GB"的对象被记成 <c>TargetBytes≈42 GB</c>，
    /// 经 <c>baseBytes += receipt.TargetBytes</c> 抬进 <c>state.CompletedBytes</c>，再被
    /// <c>RunningPercentCeiling</c> 夹成 99.9%。真机后果（用户视频）：暂停前 72~74%，暂停瞬间
    /// 99.9% / 41.96 GB，Resume 后长期 99.9%，而对象完成数仍是 0/41 —— 三者不可能同时为真。
    /// </para>
    /// <para>
    /// 现在改为**只认运行时可信 checkpoint**：<c>PollProgressAsync</c> 用同一套
    /// <see cref="ProgressTruthSnapshot"/> 规则验证过的"本对象确认量"。结算时再读一次目标
    /// <c>FileInfo.Length</c> 就等于把 <c>TrustsTargetStatForProgress</c> 那条保护绕过去。
    /// </para>
    /// <para>
    /// <b>为什么不能用"实测长度"做兜底</b>：本条通道分不出"已落盘"与"已预分配"，所以
    /// <c>measured</c> 永远只是<b>上界</b>而非事实。若将来某个通道确认 <c>TargetStatTrusted == true</c>
    /// （即该趟确实不做预分配），也只允许把实测作为"不超过可信上界"的**补充证据**，不能作为默认来源。
    /// </para>
    /// </summary>
    private ObjectReceipt MarkInterrupted(ObjectReceipt receipt, PlannedObject obj,
        long trustedObjectConfirmedBytes, long trustedAttemptEpoch)
    {
        receipt.Status = ObjectStatus.Interrupted;
        receipt.CompletedUtc = DateTime.UtcNow;
        receipt.ErrorDetail = "interrupted";
        CaptureInterruptedConfirmedProgress(receipt, obj, trustedObjectConfirmedBytes, trustedAttemptEpoch);
        return receipt;
    }

    /// <summary>
    /// 现算"本对象截至此刻的可信已确认字节"（**只读 Worker I/O 与解析累计，绝不读目标长度**）。
    /// <para>
    /// 为什么需要它：进度轮询是 2 秒一轮，短任务或"开始后立刻按暂停"时
    /// <c>_lastTrustedObjectConfirmedBytes</c> 可能一次都还没被采到 —— 若此时直接用它结算，
    /// 分子会变成 0（正是 P04 回归测试抓到的退化，也是 UI-02 那个"暂停后 0 B"的老现象）。
    /// 这里用与轮询**完全同一套来源**（bulk 解析累计 + /Z 已入账大文件 + 当前大文件 O(1) 实时长度 +
    /// 进程 I/O 计数器）现算一次，再与最近一次采样取大：
    /// 既不依赖目标长度（<c>/Z</c> 预分配污染的唯一入口），也不丢基线（采样值含 baseline）。
    /// </para>
    /// </summary>
    private long ResolveLiveTrustedObjectBytes(PlannedObject obj)
    {
        var sampled = Interlocked.Read(ref _lastTrustedObjectConfirmedBytes);

        // ★ Round-3 PHASE A（Stop 路径 P0 的第二条注入项）★
        //   预分配通道（Large 的 /Z、RootFiles 的 /J）**不得**再把 live 增量算进来。
        //   真机证据（job JOB-20261005-143738-4963，Stop 于 85.7%）：
        //     `Phase=Interrupted committed=45097156608`（42 GiB，= 预分配目标长度总和），
        //     同一时刻对象完成数仍 0/41 —— 分子只能来自"预分配长度"这一类伪证据。
        //   为什么 live 增量在这条通道上不可信：
        //     · `_ioConfirmedBytes` 是 worker 进程**从源读出的字节**累计。`/Z` 重启续传时 robocopy
        //       会重新读一遍已经预分配好的目标所对应的源区间，读计数因此可以在极短时间内冲上
        //       对象计划量（真机 0.2 秒内 `inFlight=45097156608`），它**证明不了目标落盘了多少**；
        //     · `CurrentLargePartial()` 对预分配通道恒 0（TrustsTargetStatForProgress → false），
        //       `_largeCompletedBytes` 只记已完成的前序文件 —— 都不是"当前对象本次确认量"。
        //   因此这条通道**只认运行时可信 checkpoint**（它由轮询用"本趟已确认"口径维护、只增不减）；
        //   若还没采到样本（对象开始后立刻被打断），宁可记 0 —— 呈现层有 continuation 高水位兜住
        //   显示连续性，而"凭空多出没传过的字节"是不可恢复的信任破坏（执行书 §18 / R24）。
        if (_currentPass != PassKind.Bulk)
            return sampled;

        var liveIncrement = Interlocked.Read(ref _bulkCopiedBytes)
            + Interlocked.Read(ref _largeCompletedBytes)
            + CurrentLargePartial()
            + Interlocked.Read(ref _ioConfirmedBytes);
        var trusted = Math.Max(sampled, liveIncrement);

        // ★ 目标长度在这里**只有一个合法用法**：Bulk 小文件通道的补充证据（取大，上界由
        //   ResolveInterruptedConfirmedBytes 夹到计划量）。这与轮询里"回退枚举只允许 Bulk"是同一条
        //   既有口径（见 PollProgressAsync 的 enumFallbackAllowed）。
        //   为什么其它通道不行：
        //     · Large（/Z）通道：robocopy 会先给目标文件预分配最终长度 ⇒ 长度 ≠ 已落盘字节，
        //       采信它就是把 31 GiB 的对象记成 42 GiB（这正是 P0：暂停瞬间 72% → 99.9%）；
        //     · RootFiles 通道：源根可能散落单个超大文件，一旦被 /J 预分配，枚举同样会虚报接近完成。
        //   反过来说，Bulk 通道若连这条补充证据都不给，"worker 已写盘但还没解析出任何行"的短暂窗口
        //   里分子就会掉成 0 —— 那是 UI-02 的假归零，同样不可接受。
        if (_currentPass == PassKind.Bulk)
        {
            var measured = MeasureCurrentTargetBytes(obj);
            if (measured > trusted) trusted = measured;
        }
        return trusted;
    }

    /// <summary>
    /// 续传基线（Round-3 PHASE A）：**只有不预分配的通道**才允许用"目标当前长度"当基线。
    /// <para>
    /// 真机证据（job JOB-20261005-143738-4963，Stop 后 Resume）：续传瞬间
    /// <c>Phase=Running previousDisplayedBytes=0 newDisplayedBytes=45097156608 newPercent=99.9
    /// source=ParsedWorkerOutput</c> —— 0 → 42 GiB 只用了一次轮询，而对象完成数还是 0/41。
    /// 唯一能解释这条读数的就是旧基线 <c>MeasureCurrentTargetBytes(obj)</c>：
    /// Stop 留下的 Interrupted 回执让 <c>hasPriorReceipt</c> 为真，于是基线取到了
    /// <b>/Z 预分配后的目标长度</b>（42 GiB），<c>nowBytes = baseline + …</c> 一步顶到计划量，
    /// 再被 <c>RunningPercentCeiling = 99.9</c> 夹住 —— 与用户视频里的 99.9% / 41.96 GB 完全同源。
    /// </para>
    /// <para>
    /// 口径与 <c>PollProgressAsync</c> 既有的 <c>enumFallbackAllowed = _currentPass == PassKind.Bulk</c>
    /// 一致；预分配通道唯一可信的续传基线是上一次运行落下的 <b>Interrupted 回执</b>
    /// （PHASE A 之后其 <c>TargetBytes</c> 已是运行时可信 checkpoint，不再是目标逻辑长度）。
    /// 取不到就记 0：宁可让呈现层用高水位拖住显示，也不能凭空多出没传过的字节。
    /// </para>
    /// </summary>
    private long ResolveResumeBaselineBytes(PlannedObject obj)
    {
        var trusted = ReadTrustedInterruptedReceiptBytes(obj);
        var mayPreallocate = obj.UseRestartablePass;
        var measured = mayPreallocate ? 0L : MeasureCurrentTargetBytes(obj);
        var baseline = ResolveResumeBaselineForPass(_currentPass, measured, trusted, mayPreallocate);
        if (baseline > 0)
        {
            _log.Debug("续传基线：object={ObjectId} pass={Pass} mayPreallocate={MayPreallocate} " +
                "measuredTargetBytes={Measured} trustedReceiptBytes={Trusted} baseline={Baseline}",
                obj.ObjectId, _currentPass, mayPreallocate, measured, trusted, baseline);
        }
        return baseline;
    }

    /// <summary>
    /// 续传基线选择规则（纯函数，便于契约测试直接锁死）：
    /// ① <b>可信 Interrupted 回执优先</b>（任何通道）：它由运行时 checkpoint 写入，是"本趟真正确认过的字节"；
    /// ② 会预分配的对象（<c>UseRestartablePass</c> ⇒ Large 通道挂 <c>/Z</c>）**永不**采信目标长度 —— 宁可为 0，
    ///    由呈现层高水位保证显示不倒退；
    /// ③ 只有"不预分配的 Bulk 小文件通道 + 没有回执"才允许退回目标长度。
    /// </summary>
    /// <remarks>
    /// ★ Round-3 PHASE A（Stop 路径 P0 的第三条注入项，真机定位）★
    /// 旧实现只按 <c>pass</c> 分流：Bulk ⇒ 目标长度、其余 ⇒ 回执。漏洞在于
    /// <b>"通道"与"目标上有没有预分配文件"是两件事</b>：一个对象的 Large 通道（/Z）先跑、把 6 个 8 GiB 文件
    /// 预分配成全长，随后 Resume 时**先跑 Bulk 通道**（robocopy 选项 <c>/MAX:… /MT:16</c>），
    /// 于是基线按 Bulk 口径取到实测目标长度 = <b>42 GiB</b>（恰好等于整对象计划量）。
    /// 真机证据（job JOB-20261005-150030-19a6，`phA4-gate`）：
    /// Resume 后第一次轮询的 trace 即为
    /// <c>base=45097156608 now=45097156608</c>（<c>io=0</c>，0.25 秒内不可能真传 42 GiB），
    /// 紧接着 UI 写 <c>99.9% / 41.96 GB</c>，而 robocopy 同一刻的汇总行是
    /// <c>文件: 6 复制 0 跳过 6</c> / <c>字节: 42.000 g 0 42.000 g</c>（一个字节都没复制）。
    /// trace 里 <c>pass=Large</c> 是"打印那一刻"的通道，<c>baseline</c> 是轮询启动时按 Bulk 算出来的 —— 两者不同刻。
    /// </remarks>
    internal static long ResolveResumeBaselineForPass(PassKind pass, long measuredTargetBytes,
        long trustedReceiptBytes, bool objectMayPreallocate)
    {
        if (trustedReceiptBytes > 0) return trustedReceiptBytes;
        if (pass != PassKind.Bulk || objectMayPreallocate) return 0;
        return measuredTargetBytes > 0 ? measuredTargetBytes : 0;
    }

    /// <summary>
    /// 读取"本对象上一次运行留下的、可信的已确认字节"。只有 <c>Interrupted</c> 回执可信：
    /// 它由 <c>CaptureInterruptedConfirmedProgress</c> 按运行时 checkpoint 写入，不是目标长度。
    /// 其它状态（Failed / 无回执）返回 0 —— 本趟从零开始算，由呈现层高水位保证显示不倒退。
    /// </summary>
    private long ReadTrustedInterruptedReceiptBytes(PlannedObject obj)
    {
        try
        {
            var receipts = _ctx.LoadReceipts();
            var trusted = ResolveTrustedInterruptedBytes(receipts, obj.ObjectId, obj.EstimatedBytes);
            var count = receipts.Count(r => string.Equals(r.ObjectId, obj.ObjectId, StringComparison.OrdinalIgnoreCase));
            if (count > 1)
            {
                _log.Debug(
                    "续传基线：object={ObjectId} 共有 {Count} 份回执（每次尝试一份），按「最新一次尝试的状态 + 所有 Interrupted 的最大值」解析出可信字节 {Trusted}",
                    obj.ObjectId, count, trusted);
            }
            return trusted;
        }
        catch (Exception ex)
        {
            _log.Warning("读取续传基线失败（按 0 处理，绝不回退到目标长度）: {Object} {Error}", obj.ObjectId, ex.Message);
        }
        return 0;
    }

    /// <summary>
    /// 对象级回执权威解析（纯函数，便于回归）：同一对象**可能有多份回执**——每次尝试写一份，
    /// 文件名精确到秒（<c>JobManager.SaveReceipt</c>）。因此解析口径必须**确定且单调**，
    /// 否则"已确认字节"会随目录枚举顺序变化（旧实现取第一份命中的：两份 Interrupted 时可能取到更小的那份，
    /// 让已确认字节倒退；这正是 UI Closure 要根治的那一类污染）。
    /// <list type="number">
    /// <item>只看**最新一次尝试**的状态：先比 <c>CompletedUtc</c>，同刻再比 <c>Attempt</c>；</item>
    /// <item>最新一次是 <c>Interrupted</c> ⇒ 可信字节取该对象**所有 Interrupted 回执的最大值**（已确认的字节只许变大），再按计划字节夹取；</item>
    /// <item>最新一次不是 <c>Interrupted</c>（Completed / Failed）或无回执 ⇒ 返回 0，本趟从零起算，显示不倒退由呈现层高水位负责。</item>
    /// </list>
    /// </summary>
    internal static long ResolveTrustedInterruptedBytes(
        IEnumerable<ObjectReceipt> receipts, string objectId, long plannedBytes)
    {
        ObjectReceipt? latest = null;
        var highestInterrupted = 0L;
        var seen = 0;
        foreach (var r in receipts)
        {
            if (!string.Equals(r.ObjectId, objectId, StringComparison.OrdinalIgnoreCase)) continue;
            seen++;
            if (r.Status == ObjectStatus.Interrupted && r.TargetBytes > highestInterrupted) highestInterrupted = r.TargetBytes;
            if (latest is null
                || r.CompletedUtc > latest.CompletedUtc
                || (r.CompletedUtc == latest.CompletedUtc && r.Attempt > latest.Attempt))
            {
                latest = r;
            }
        }

        if (seen == 0 || latest is null) return 0;
        if (latest.Status != ObjectStatus.Interrupted) return 0;

        var trusted = highestInterrupted > 0 ? highestInterrupted : latest.TargetBytes;
        if (trusted <= 0) return 0;
        if (plannedBytes > 0 && trusted > plannedBytes) trusted = plannedBytes;
        return trusted;
    }

    /// <summary>
    /// Interrupted 结算：<c>receipt.TargetBytes</c> 表示"**截至被打断时本趟已可信确认的对象字节**"，
    /// <b>不是</b>目标文件的逻辑长度（<c>/Z</c> 预分配会让后者等于最终大小）。
    /// <para>
    /// 口径与 <c>MeasureSettledTarget</c> 明确区分：
    /// <b>Completed Receipt Truth ≠ Interrupted In-flight Truth</b> ——
    /// 前者问"盘上现在有多少"（对象已经完整结束，可以直接量），
    /// 后者问"这一趟确认传到了多少"（对象还在半途，只认运行时 checkpoint）。
    /// </para>
    /// <para>
    /// Interrupted 永不进入 <c>completedIds</c>（skip 权威集合只取 <c>ObjectStatus.Completed</c>），
    /// 所以这个数不会冒充"完成"，只用于恢复时的进度连续性。
    /// </para>
    /// </summary>
    private ObjectReceipt CaptureInterruptedConfirmedProgress(ObjectReceipt receipt, PlannedObject obj,
        long trustedObjectConfirmedBytes, long trustedAttemptEpoch)
    {
        var trusted = ResolveInterruptedConfirmedBytes(trustedObjectConfirmedBytes, obj.EstimatedBytes);
        receipt.TargetBytes = trusted;
        // 文件计数无可靠运行时来源：保持 0，不猜（旧实现顺带把实测文件数也带进来了）。
        receipt.TargetFiles = 0;
        _log.Information(
            "对象 {ObjectId} 被打断：按运行时可信 checkpoint 结算 {TrustedBytes}（计划 {PlannedBytes}，" +
            "attemptEpoch={AttemptEpoch}）；不使用目标实测长度（/Z 会预分配最终大小）",
            obj.ObjectId, Format.Bytes(trusted), Format.Bytes(Math.Max(obj.EstimatedBytes, 0)), trustedAttemptEpoch);
        return receipt;
    }

    /// <summary>
    /// Interrupted 结算的夹取规则：可信确认量必须落在 <c>[0, 本对象计划量]</c> 内。
    /// <para>
    /// 单独抽成 <c>internal static</c> 是为了让回归测试能直接锁死这条规则
    /// （TEST A：<c>/Z</c> 预分配场景下 30.99 GiB 绝不能被写成 42 GiB）。
    /// 计划量未知（≤0）时不做上界夹取 —— 不知道上界就不要假装知道。
    /// </para>
    /// </summary>
    internal static long ResolveInterruptedConfirmedBytes(long trustedObjectConfirmedBytes, long estimatedBytes)
    {
        if (trustedObjectConfirmedBytes <= 0) return 0;
        var planned = estimatedBytes > 0 ? estimatedBytes : 0;
        return planned > 0 && trustedObjectConfirmedBytes > planned ? planned : trustedObjectConfirmedBytes;
    }

    private enum PauseOutcome { Running, Canceled }

    /// <summary>清空暂停真值（恢复 / 新一轮运行时调用）。**绝不改 Phase**——阶段由调用方按事实设置。</summary>
    private static void ResetPauseTruth(JobState state)
    {
        state.PauseState = PauseState.None;
        state.PauseRequestMode = null;
        state.PauseRequestedUtc = null;
        state.PauseAchievedUtc = null;
        state.PauseElapsedMs = null;
        state.PauseOutcome = PauseOutcomeKind.None;
        state.PauseFailureReason = null;
    }

    /// <summary>
    /// 暂停**达成**判定（Trust-Critical Recovery FIX BATCH 1）。
    ///
    /// 旧语义的致命缺陷：只有 <c>Immediate</c> 模式才打断当前 robocopy，UI footer 发的是
    /// <c>Cooperative</c> ⇒ 唯一的检查点在"对象边界" ⇒ 28.5 GB 单对象内部复制期间永不暂停。
    /// 真机证据：8/8 次点击、8/8 次请求文件写入成功、**0 次引擎确认**、传输从未停止。
    ///
    /// 新语义：**任何模式**的暂停请求都立即打断当前 worker，并在 SLA 内确认"真的停了"：
    ///   · 硬失败 SLA（<see cref="PauseSla.HardFailSeconds"/> s）内 <see cref="ITransferWorker.HasRunningWorker"/>
    ///     变为 false ⇒ Paused：真值落盘 + 事件，当前对象按未完成处理；
    ///   · 到点仍未停住 ⇒ PauseFailed：迁移仍在进行，**绝不用"请求已写入"冒充"已暂停"**。
    /// 返回 true = 已达成暂停。
    /// </summary>
    private async Task<bool> TryAchievePauseAsync(JobState state, IProgress<ProgressSnapshot>? progress,
        string mode, CancellationToken ct)
    {
        // ① 请求已被读到：写"正在暂停"真值。此刻传输**仍在进行**（Diagnostics 必须把这两件事分开记）。
        state.PauseRequestMode = mode;
        // 每次尝试都重打时间戳：这是"**本轮**暂停请求被引擎处理的时刻"，UI 用它区分"这是新一轮结论"还是
        // "上一轮那个失败结论又报了一遍"（用户重按暂停后，界面必须重新显示"正在暂停…"而不是旧的失败）。
        state.PauseRequestedUtc = DateTime.UtcNow;
        state.PauseState = PauseState.Pausing;
        state.PauseOutcome = PauseOutcomeKind.None;
        state.PauseAchievedUtc = null;
        state.PauseElapsedMs = null;
        state.PauseFailureReason = null;
        _ctx.SaveState(state);
        _log.Information("收到 {Mode} 暂停请求：立即打断当前对象（不再等待对象边界）", mode);
        Report(progress, state, state.CurrentObjectId, null, 0, "正在暂停…");
        PublishTransfer(TransferEvents.PauseRequested,
            new TrnPauseObservedPayload(mode, state.CurrentObjectId),
            DiagnosticLevel.Information, DiagnosticOutcome.Accepted, state.CurrentObjectId);

        // ② 立即打断当前 worker（对象内部复制期间也一样——这正是旧实现漏掉的那一步）
        var pending = Stopwatch.StartNew();
        if (_runner.HasRunningWorker) _runner.KillCurrent();

        // ③ 在硬失败 SLA 内确认 worker 真的不再运行。
        //    正常窗口（PauseSla.Achieve = 5 s）内没停就已经不正常了：先如实告警，再等到硬失败为止。
        //    两个常量都必须有真实语义——只写一个"一小时后超时"正是旧实现让暂停永远不失败的原因。
        var softWarned = false;
        while (_runner.HasRunningWorker && pending.Elapsed < PauseSla.HardFail)
        {
            if (!softWarned && pending.Elapsed >= PauseSla.Achieve)
            {
                softWarned = true;
                _log.Warning("暂停尚未达成：已超过正常窗口 {Soft} s（worker 仍在运行），继续等到硬失败 SLA {Hard} s",
                    PauseSla.AchieveSeconds, PauseSla.HardFailSeconds);
                Report(progress, state, state.CurrentObjectId, null, 0, "正在暂停…（超过 5 秒仍未停住）");
            }
            try { await Task.Delay(PauseSla.Poll, ct); }
            catch (OperationCanceledException) { break; }
        }
        pending.Stop();

        if (_runner.HasRunningWorker)
        {
            // 停不下来就如实说"停不下来"：请求已受理，但业务效果从未达成。
            state.PauseState = PauseState.PauseFailed;
            state.PauseOutcome = PauseOutcomeKind.Failed;
            state.PauseElapsedMs = (long)pending.ElapsedMilliseconds;
            state.PauseFailureReason =
                $"worker 在硬失败 SLA（{PauseSla.HardFailSeconds} s）内仍未停止，迁移仍在进行；可再次暂停重试。";
            _ctx.SaveState(state);
            _log.Error("暂停失败：worker 拒绝停止（{Ms} ms），迁移仍在进行", pending.ElapsedMilliseconds);
            Report(progress, state, state.CurrentObjectId, null, 0, "暂停失败，迁移仍在进行");
            PublishTransfer(TransferEvents.PauseFailed,
                new TrnPauseObservedPayload(mode, state.CurrentObjectId, (long)pending.ElapsedMilliseconds),
                DiagnosticLevel.Error, DiagnosticOutcome.Failed, state.CurrentObjectId);
            return false;
        }

        // ④ 真的停了：这才是"已暂停"
        state.PauseState = PauseState.Paused;
        state.PauseOutcome = PauseOutcomeKind.Achieved;
        state.PauseAchievedUtc = DateTime.UtcNow;
        state.PauseElapsedMs = (long)pending.ElapsedMilliseconds;
        state.PauseFailureReason = null;
        state.Phase = JobPhase.Paused;
        _ctx.SaveState(state);
        _log.Information("暂停达成（{Mode}，{Ms} ms）：worker 已停止，当前对象未完成（resume 时重跑）",
            mode, pending.ElapsedMilliseconds);
        Report(progress, state, state.CurrentObjectId, null, 0, "已暂停（等待 resume）");
        PublishTransfer(TransferEvents.PauseObserved,
            new TrnPauseObservedPayload(mode, state.CurrentObjectId, (long)pending.ElapsedMilliseconds),
            DiagnosticLevel.Information, DiagnosticOutcome.Succeeded, state.CurrentObjectId);
        return true;
    }

    /// <summary>pause.request 的最后写入时刻（拿不到 ⇒ null）。用于判断"用户是否又点了一次暂停"。</summary>
    private DateTime? SafeLastWriteUtc()
    {
        try
        {
            var t = File.GetLastWriteTimeUtc(_ctx.PauseRequestPath);
            return t == DateTime.MinValue ? null : t;
        }
        catch { return null; }
    }

    /// <summary>
    /// 对象边界暂停检查点：请求存在 ⇒ 走同一套达成判定（此处无 worker 在跑，通常瞬间达成），
    /// 然后**原地**等待 resume（pause.request 被移除），不结束本次运行。
    /// </summary>
    private async Task<PauseOutcome> WaitIfPausedAsync(JobState state, IProgress<ProgressSnapshot>? progress, CancellationToken ct)
    {
        if (!File.Exists(_ctx.PauseRequestPath)) return PauseOutcome.Running;

        var mode = _ctx.ReadPauseRequest() ?? "Cooperative";
        if (!await TryAchievePauseAsync(state, progress, mode, ct))
            return ct.IsCancellationRequested ? PauseOutcome.Canceled : PauseOutcome.Running;   // 暂停失败：迁移仍在进行

        var pauseSw = Stopwatch.StartNew();
        while (File.Exists(_ctx.PauseRequestPath))
        {
            if (ct.IsCancellationRequested) return PauseOutcome.Canceled;
            try { await Task.Delay(PauseSla.RequestPollMs, ct); }
            catch (OperationCanceledException) { return PauseOutcome.Canceled; }
        }
        // 暂停等待时间不计入"本次运行平均速率"的分母：暂停不是传得慢（T01 速率口径的一部分）
        pauseSw.Stop();
        Interlocked.Add(ref _pausedTicks, pauseSw.Elapsed.Ticks);

        ResetPauseTruth(state);
        state.Phase = JobPhase.Running;
        _ctx.SaveState(state);
        Report(progress, state, null, null, 0, "已恢复，继续传输");
        _log.Information("任务已恢复");

        // ★ D6.1 §16 ★ 恢复（含真实等待时长）——暂停不是"传得慢"，这个时长必须单独可见。
        PublishTransfer(TransferEvents.Resumed,
            new TrnPauseObservedPayload(mode, state.CurrentObjectId, (long)pauseSw.Elapsed.TotalMilliseconds),
            DiagnosticLevel.Information, DiagnosticOutcome.Succeeded, state.CurrentObjectId);

        return PauseOutcome.Running;
    }

    /// <summary>
    /// 飞行中暂停监视（**任意模式**，替代旧的 Immediate-only 监视）：
    /// 发现暂停请求 ⇒ 立即打断当前对象 ⇒ 在 SLA 内确认停住。达成后直接返回——
    /// 本次运行会以 Paused 收尾，当前对象记为未完成，resume 时重跑它。
    /// 未达成（PauseFailed）⇒ 继续盯着，但**只有请求文件被重新写入**（用户又点了一次暂停）才重试，
    /// 绝不因为"文件还在"就无限重复强杀。
    /// </summary>
    private async Task MonitorPauseRequestAsync(PlannedObject obj, JobState state,
        IProgress<ProgressSnapshot>? progress, CancellationToken ct)
    {
        DateTime? failedAtWriteUtc = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var mode = _ctx.ReadPauseRequest();
                if (mode != null)
                {
                    var writeUtc = SafeLastWriteUtc();
                    var isRetry = failedAtWriteUtc != null && writeUtc != null && writeUtc != failedAtWriteUtc;
                    if (failedAtWriteUtc == null || isRetry)
                    {
                        if (await TryAchievePauseAsync(state, progress, mode, ct))
                        {
                            _log.Information("对象 {Object} 在传输中被暂停（{Mode}）", obj.ObjectId, mode);
                            return;
                        }
                        failedAtWriteUtc = SafeLastWriteUtc();
                    }
                }
                await Task.Delay(PauseSla.RequestPollMs, ct);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// 进度轮询（超大规模安全）：
    ///   主通道 = robocopy 输出行累计（bulk）+ 当前大文件 stat（/Z），平时零枚举；
    ///   基线 = 仅当本对象有历史 Receipt（即续传）时才有：**Bulk 通道**枚举一次目标；
    ///          **预分配通道**（Large /Z、RootFiles /J）改用上一次 Interrupted 回执里的可信已确认字节
    ///          （Round-3 PHASE A；目标长度在预分配通道上等于最终大小，拿它当基线就是把 42 GiB 计划量
    ///          一步变成"已传完 42 GiB"，真机表现为续传瞬间 99.9%）；全新对象基线为 0；
    ///   回退 = robocopy 重定向 stdout 是块缓冲，少量大文件时行会憋到进程退出才到达——
    ///         30 秒无文件行事件则实测枚举一次目标（间隔≥10s，取最大值保证单调不回退）。
    /// </summary>
    private async Task PollProgressAsync(PlannedObject obj, JobState state, long baseBytes,
        SpeedTracker speed, IProgress<ProgressSnapshot>? progress, CancellationToken ct)
    {
        var hasPriorReceipt = _ctx.LoadReceipts().Any(r =>
            string.Equals(r.ObjectId, obj.ObjectId, StringComparison.OrdinalIgnoreCase));
        var baseline = hasPriorReceipt ? ResolveResumeBaselineBytes(obj) : 0;
        long lastBytes = baseline;
        var lastTime = DateTime.UtcNow;
        // 进度停滞检测（v0.3.8）："界面长时间不动"是用户判断"卡死"并手动终止的直接原因，
        // 必须由引擎如实报出"已经多少秒没有落盘字节增长"，而不是让用户自己猜。
        var lastChangeAt = DateTime.UtcNow;
        var pollEpoch = Interlocked.Read(ref _attemptEpoch);
        var pollRetract = Interlocked.Read(ref _creditRetractEpoch);
        // FIX BATCH 4（P1-1）：本对象的连续进度来源。robocopy stdout 是块缓冲、/Z 通道又不允许用目标
        //   长度（预分配会虚报）、回退枚举在 Large/RootFiles 通道被禁 ⇒ 单个大对象整段没有任何信号。
        //   进程 I/O 计数器（读源字节）是这条通道上唯一连续的观测，只用于显示、按趟重开基线、封顶对象大小。
        var ioProgress = new WorkerIoProgressTracker(
            () => _runner.TryGetWorkerReadBytes(out var readBytes) ? readBytes : null);

        // ★ PHASE C-3（§3）★ 轮询拆成"轻量高频 + 重活低频"两档。
        //   为什么必须拆：Core 原来固定 2 秒一轮，于是 UI 每约 2 秒才拿到一个新目标，
        //   进度条只能在 0.4 秒内补到新目标、剩下 ~1.6 秒完全静止（真机探针实测 span 恒 0.400、
        //   12 秒里 targetChanges 只有 5~7 次）⇒ 人眼看到的仍是"一段一段跳"。
        //   轻量档只做 O(1) 读取：robocopy 进程 I/O 计数器、内存里已解析的累计字节、
        //   当前大文件实时长度；重活档（job-state 落盘、目录枚举回退、重型日志）保持原来 2 秒节流，
        //   否则 5 Hz 写盘会把磁盘打满、枚举会把引擎拖慢。
        var heavyInterval = TimeSpan.FromSeconds(2);
        var nextHeavyAt = DateTime.UtcNow + heavyInterval;

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(LightPollMs, ct); }
            catch (OperationCanceledException) { return; }

            // F4：对象级重试/修复重拷开始新一趟尝试 → 单调基线重开（本趟只认本趟已确认的字节）。
            //   不重开的话，失败尝试里记进去的字节会一直留在分子上（D02 实测：底栏 4.02 GB / 1.02 GB）。
            var epoch = Interlocked.Read(ref _attemptEpoch);
            if (epoch != pollEpoch)
            {
                pollEpoch = epoch;
                lastBytes = 0; lastTime = DateTime.UtcNow; lastChangeAt = DateTime.UtcNow;
                // FIX BATCH 4（§7.3）：单调基线重开是"本趟只认本趟字节"，但界面必须知道这是**重试**，
                //   否则就是"静默归零"（用户看到 99% 突然变 0% 却没有任何解释）。
                _currentRetryState = RetryState.Retrying;
                ioProgress.BeginAttempt();
                Interlocked.Exchange(ref _ioConfirmedBytes, 0);
            }

            // F12：本趟出现过"进度回冲"（robocopy 明确报某文件失败）→ 单调下限同样重开。回冲只把
            //   nowBytes 降下来；不重开下限的话，那个被采到的假高值仍会被 Math.Max 钉在分子上，界面
            //   就一直显示"接近完成"（D01 r4 真机：钉在 99.9% / 1.02 GB，而磁盘上只有 20.4 MB）。
            var retract = Interlocked.Read(ref _creditRetractEpoch);
            if (retract != pollRetract)
            {
                pollRetract = retract;
                lastBytes = baseline; lastChangeAt = DateTime.UtcNow;
                // 回冲 = 已入账的字节被扣回（某文件明确失败）⇒ 同样是"允许下降但必须解释"
                _currentRetryState = RetryState.RollingBack;
            }

            // 主通道：bulk 解析累计 + /Z 已入账大文件 + 当前大文件实时长度（O(1)）
            long nowBytes = baseline + Interlocked.Read(ref _bulkCopiedBytes)
                + Interlocked.Read(ref _largeCompletedBytes) + CurrentLargePartial();

            // ★ FIX BATCH 4（P1-1 核心）★ 连续飞行中来源：进程 I/O 计数器（worker 从源读出的累计字节）。
            //   为什么必须有它：/Z 串行大文件通道 ①目标长度被预分配污染（不能用）②回退枚举被禁
            //   ③robocopy stdout 块缓冲（行要等到进程退出/文件完成才到达）——三条叠加后，单个 28.5 GB
            //   对象在整个复制期间**分子一个字节都不涨**（真机现象：网络 35+ MB/s，底栏 0 B，直到对象
            //   整体结束才突然大跳）。计数器不受这三条限制，是这条通道唯一的连续观测。
            //   边界：只认本趟读到的增量（BeginAttempt 重开基线）、封顶对象计划字节（/Z 重启会重复读）、
            //   且**只用于显示**——回执与校验真值永远只走 robocopy 的实测结果。
            var ioConfirmed = ioProgress.Confirmed(obj.EstimatedBytes);
            Interlocked.Exchange(ref _ioConfirmedBytes, ioConfirmed);
            if (ioConfirmed > 0) nowBytes = Math.Max(nowBytes, baseline + ioConfirmed);

            // 回退通道：robocopy 重定向 stdout 是块缓冲，少量大文件时行会憋到很晚才到达——
            // 无文件行事件超过阈值（/Z 12s、bulk 30s）则实测枚举一次；
            // 枚举间隔自适应 = 上次枚举耗时×3（8s~120s），巨型目录不会被枚举拖垮。
            var now = DateTime.UtcNow;
            // PHASE C-3：重活档到期判定。目录枚举回退与 job-state 落盘都挂在这个节拍上。
            var heavyDue = now >= nextHeavyAt;
            if (heavyDue) nextHeavyAt = now + heavyInterval;
            var noEventWindow = _currentPass == PassKind.Large ? TimeSpan.FromSeconds(12) : TimeSpan.FromSeconds(30);
            // 目录枚举回退**只允许用在 Bulk 小文件通道**：
            //   · Large 通道：/J 会给目标文件预分配最终长度，枚举到的长度不等于已落盘字节；
            //   · RootFiles 通道：源根散落文件里同样可能有单个超大文件（真实任务里是 14.1GB 的素材.zip），
            //     目标文件一旦被预分配，枚举就会把整段长度当成"已完成"——真实任务总进度因此在
            //     仍在传输时跳到约 99%（预分配文件长度被误算成已完成字节）。
            // 这两个通道一律只认 robocopy 已确认的输出。代价是 robocopy 输出被块缓冲时进度会暂时
            // "停住"，但那是真实的，不会谎报接近完成。
            var enumFallbackAllowed = _currentPass == PassKind.Bulk;
            if (heavyDue && enumFallbackAllowed && now - _lastFileEventAt > noEventWindow && now - _lastEnumAt > _enumInterval)
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
            else if (!enumFallbackAllowed && !_enumSkipLogged && now - _lastFileEventAt > noEventWindow)
            {
                // 只记一次：说明为什么本通道不用目录枚举（否则日志里看不到任何"回退"线索会让人以为进度逻辑坏了）
                _enumSkipLogged = true;
                _log.Debug("进度回退枚举已跳过 {Id}：通道 {Pass} 的目标文件可能被预分配文件长度，" +
                           "枚举长度 ≠ 已确认落盘字节，会虚报进度；本通道只使用 robocopy 已确认的输出",
                    obj.ObjectId, _currentPass);
            }

            var dt = (now - lastTime).TotalSeconds;
            var effBytes = Math.Max(nowBytes, lastBytes); // 进度单调，不回退
            // F4b（D02 盘满重试实测）：分子**不得越过本对象的计划字节数**。
            //   /J 会给目标文件预分配最终长度，"当前大文件实时长度"这一项于是能报出整文件长度，
            //   叠加上已入账的大文件字节，底栏就出现「2 GB / 1.02 GB」这种自相矛盾的读数
            //   （分母是向用户承诺的计划值，分子却比它大）。在途读数最多只能到计划值；
            //   最终判定仍以回执里的实测落盘量为准（不制造假成功，见 receipt.TargetBytes）。
            if (obj.EstimatedBytes > 0 && effBytes > obj.EstimatedBytes) effBytes = obj.EstimatedBytes;
            if (ShouldPushSpeedSample(dt, effBytes - lastBytes))
            {
                speed.Push((effBytes - lastBytes) / dt);
                lastChangeAt = now;      // 有增长 = 没停滞
            }
            else if (effBytes > lastBytes) lastChangeAt = now;
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
            // FIX BATCH 4（§7.1）：先把这一步的结果写成**唯一真值**，再由真值反写 JobState 与 UI 快照——
            // 顺序反了就又会出现"持久化 47.29% vs 界面 49.6%"这种两套口径。
            var cumSpeed = CumulativeSpeed(baseBytes + Math.Max(0, effBytes));
            var truth = ProgressTruthSnapshot.Create(
                plannedBytes: state.TotalBytes,
                committedBytes: baseBytes,
                inFlightConfirmedBytes: Math.Max(0, effBytes),
                currentObjectId: obj.ObjectId,
                currentObjectPlannedBytes: Math.Max(obj.EstimatedBytes, 0),
                currentObjectConfirmedBytes: Math.Max(0, effBytes - baseline),
                attemptEpoch: Interlocked.Read(ref _attemptEpoch),
                retryState: _currentRetryState,
                // 起步不足 0.5 秒 / 尝试重开时累计平均为 0：退回 EMA（它只收 ≥0.5 秒窗口的样本）
                speedBytesPerSecond: cumSpeed > 0 ? cumSpeed : speed.Current,
                settled: false,
                paused: state.Phase == JobPhase.Paused,
                targetStatTrusted: RobocopyRunner.TrustsTargetStatForProgress(_serialLargePass),
                inFlightSource: ResolveInFlightSource(effBytes),
                timestampUtc: now);

            // ★ Round-3 PHASE A ★ 同步保存"本对象最近一次可信确认量"与所属趟次。
            //   这是 Interrupted 结算的**唯一**依据（MarkInterrupted 不再读目标文件长度）。
            //   这里写入的已经是经 ProgressTruthSnapshot.Create 规则验证过的值——它已经排除了
            //   /Z 预分配污染（TargetStat 在 targetStatTrusted=false 时不被承认）。结算时再读一次
            //   目标 FileInfo.Length 就等于把这条保护绕过去。
            //   趟次一致时只增不减（轮询可能采到 RollingBack 后的低值，但"已经确认过的量"不应被抹掉）。
            var sampledEpoch = Interlocked.Read(ref _lastTrustedAttemptEpoch);
            // 注意口径：checkpoint 存的是"本对象**全量**可信已确认字节"（含本对象开始前就已落盘的
            //   baseline），与 receipt.TargetBytes 的语义一致；truth.CurrentObjectConfirmedBytes
            //   只含本趟增量，所以这里要补回 baseline。
            var trustedFull = Math.Max(0L, baseline + truth.CurrentObjectConfirmedBytes);
            if (sampledEpoch != truth.AttemptEpoch)
            {
                Interlocked.Exchange(ref _lastTrustedAttemptEpoch, truth.AttemptEpoch);
                Interlocked.Exchange(ref _lastTrustedObjectConfirmedBytes, trustedFull);
                Interlocked.Exchange(ref _lastTrustedDisplayedBytes, truth.DisplayedTransferredBytes);
            }
            else
            {
                if (trustedFull > Interlocked.Read(ref _lastTrustedObjectConfirmedBytes))
                    Interlocked.Exchange(ref _lastTrustedObjectConfirmedBytes, trustedFull);
                if (truth.DisplayedTransferredBytes > Interlocked.Read(ref _lastTrustedDisplayedBytes))
                    Interlocked.Exchange(ref _lastTrustedDisplayedBytes, truth.DisplayedTransferredBytes);
            }

            // 对外一律报"本次运行的累计平均速率"（T01：旧口径偏低 4.6 倍，用户会误判剩余时间）；
            // speed.Current 只用于上面的速度异常检测，不再出现在给用户看的数字里。
            var shown = truth.SpeedBytesPerSecond;
            // PHASE C-3：job-state 落盘保持原来的 2 秒节奏（SaveState 每次都要写文件，
            //   5 Hz 会把磁盘写满）；UI 需要的高频真值走下面的 truth 通道，不经 job-state。
            if (heavyDue)
            {
                state.CompletedBytes = truth.DisplayedTransferredBytes;
                state.Percent = truth.Percent;
                state.BytesPerSecond = shown;
                _ctx.SaveState(state);
            }
            if (ProgressTraceEnabled)
            {
                TraceProgressSnapshot(state, obj, truth, ioConfirmed, ioProgress.Source, nowBytes, effBytes, baseline);
            }

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
                stall: stallSeconds,
                truth: truth);
        }
    }

    /// <summary>进度停滞多久算"值得提示"（秒）。取 45 秒：小于最常见的"大文件首字节延迟"窗口。</summary>
    public const double StallWarnSeconds = 45;

    /// <summary>
    /// Round-2（§3.1）：把每一拍的**真值来源**如实落盘，用来回答"真值到底是稀疏（B）
    /// 还是被上层折叠（C），还是恢复时基线一次性发现了已有字节（D）"。
    /// 只观测、不改变任何业务语义；由环境变量 <c>PCMIG_PROGRESS_TRACE=1</c> 开启。
    /// </summary>
    private void TraceProgressSnapshot(JobState state, PlannedObject obj, ProgressTruthSnapshot truth,
        long ioConfirmed, ProgressTruthSource ioSource, long nowBytes, long effBytes, long baseline)
    {
        try
        {
            var utc = truth.TimestampUtc;
            var prev = _lastTraceDisplayedBytes;
            var delta = prev < 0 ? 0L : truth.DisplayedTransferredBytes - prev;
            var deltaPercent = prev < 0 || truth.PlannedBytes <= 0 ? 0d : delta * 100.0 / truth.PlannedBytes;
            var gapMs = _lastTraceUtc == DateTime.MinValue ? 0d : (utc - _lastTraceUtc).TotalMilliseconds;
            _lastTraceDisplayedBytes = truth.DisplayedTransferredBytes;
            _lastTraceUtc = utc;
            _traceSamples++;
            _log.Information(
                "ProgressTrace#{N} job={JobId} obj={ObjectId} pass={Pass} phase={Phase} raw={Raw} committed={Committed} " +
                "inFlight={InFlight} source={Source} ioSource={IoSource} ioConfirmed={IoConfirmed} epoch={Epoch} " +
                "retry={Retry} baseline={Baseline} nowBytes={NowBytes} effBytes={EffBytes} delta={Delta} " +
                "deltaPercent={DeltaPercent} gapMs={GapMs} utc={Utc}",
                _traceSamples, state.JobId, obj.ObjectId, _currentPass, state.Phase,
                truth.DisplayedTransferredBytes, truth.CommittedBytes, truth.InFlightConfirmedBytes,
                truth.InFlightSource, ioSource, ioConfirmed, truth.AttemptEpoch, truth.RetryState,
                baseline, nowBytes, effBytes, delta, Math.Round(deltaPercent, 4), Math.Round(gapMs, 1),
                utc.ToString("O"));
        }
        catch
        {
            // 观测失败不是错误：绝不让 trace 影响传输或业务判定
        }
    }

    /// <summary>
    /// PHASE C-3（§3）：轻量进度采样间隔（毫秒）。取 200（5 Hz）——这一档只读内存里的解析累计、
    /// 当前大文件实时长度与 robocopy 进程 I/O 计数器（都是 O(1)），目的是让 UI 拿到连续真值；
    /// job-state 落盘（SaveState）与目录枚举回退仍按 2 秒节流，避免 5 Hz 写盘与高频枚举拖慢引擎。
    /// </summary>
    public const int LightPollMs = 200;

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

    /// <summary>
    /// 进度百分比。两条硬规则（GROUP D F4）：
    /// ① 字节记账可能**领先**真实落盘（重试/预分配/解析行已到而未确认），所以**运行中绝不显示 100%**——
    ///    迁移失败时底栏却显示 100.0%，用户会以为已经完成；
    /// ② 只有任务真的收尾（Completed / CompletedWithErrors）才允许 100%。
    /// </summary>
    internal static double Percent(JobState s)
    {
        var raw = s.TotalBytes > 0 ? Math.Min(100.0, s.CompletedBytes * 100.0 / s.TotalBytes)
           : s.TotalObjects > 0 ? Math.Min(100.0, s.CompletedObjects * 100.0 / s.TotalObjects) : 0;
        var settled = s.Phase is JobPhase.Completed or JobPhase.CompletedWithErrors;
        return settled ? raw : Math.Min(99.9, raw);
    }

    /// <summary>
    /// 失败对象的人话原因：优先"多少文件失败 + 主因 + 举例"的汇总；F1 起"不匹配位"由退出码直接给结论，
    /// 因为类型冲突（源是文件、目标是同名目录）在日志文本里未必被解析到，却必须报出来。
    /// </summary>
    internal static string? DetailFor(RobocopyRunResult r)
    {
        var summary = RobocopyRunner.SummarizeFailures(r.ExitCode, r.ErrorLines);
        if (!RobocopyRunner.HasMismatch(r.ExitCode))
            return summary ?? r.LastErrorLine;
        var mismatch = RobocopyRunner.MismatchDetail(r.ExitCode);
        return summary is null ? mismatch : mismatch + " " + summary;
    }

    /// <summary>
    /// 错误分类。F1 追加：不匹配位（4）是**静态类型冲突**，重试不可能自愈 → 直接 Permanent，
    /// 不再白白重跑 3 遍整个对象（旧实现走默认分支时也可能被日志文本里的"网络"等词误判为 Transient）。
    /// </summary>
    internal static ErrorClass Classify(RobocopyRunResult r)
    {
        if (RobocopyRunner.HasMismatch(r.ExitCode)) return ErrorClass.Permanent;

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
    internal static bool IsSpaceErrorCode(string? code) => code is "112" or "39"
        or "0x00000070" or "0x70" or "0x00000027" or "0x27";

    /// <summary>错误文本里是否写着"磁盘空间不足"（无错误码时的兜底判据）。</summary>
    internal static bool IsSpaceErrorText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(text,
            @"(?:错误|ERROR)\s+(?:112|39)|0x0*70\b|0x0*27\b|磁盘空间不足|磁盘已满|insufficient disk space",
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
    /// F11（D01/D02 完整回归实测）：瞬时速率样本只有在窗口足够大时才有意义。
    /// 「尝试重开」那一拍（见上方 attemptEpoch 分支）会把 lastBytes/lastTime 归零重开，
    /// 紧接着的一拍 dt 只有几微秒，而 effBytes 里可能已经含 /Z 在途大文件的整段长度，
    /// (Δ字节 / Δt) 于是算出天文数字写进 EMA；偏偏那一刻 CumulativeSpeed 因为
    /// completedBytes 被清零而返回 0，显示层就回退到这个被污染的 EMA —— 界面上实测出现过
    /// 「本次平均 341.8 TB/s」（物理上不可能）。因此小于 0.5 秒的窗口不产出速率样本，
    /// 与 <see cref="CumulativeSpeed"/> 的最小窗口口径一致（它们供同一个显示字段使用）。
    /// 注意：这只是不采样本，进度单调性与停滞检测（lastChangeAt）不受影响。
    /// </summary>
    internal static bool ShouldPushSpeedSample(double dtSeconds, long deltaBytes) =>
        deltaBytes > 0 && dtSeconds >= 0.5;

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

    /// <summary>
    /// 送出进度快照。FIX BATCH 4：**进度真值只能在这里被"出厂"一次**——
    /// 传入 <paramref name="truth"/>（进度轮询路径）时快照的百分比/速率/ETA/分子全部取真值；
    /// 未传入（对象边界等非轮询路径）时按"只有已完成字节、没有飞行中字节"如实构造，
    /// 绝不沿用上一次的飞行中读数（否则边界处会出现凭空的数字）。
    /// </summary>
    private void Report(IProgress<ProgressSnapshot>? progress, JobState s, string? objId, string? objPath,
        double speed, string msg, long objDone = 0, long objTotal = 0, bool objLarge = false, double stall = 0,
        ProgressTruthSnapshot? truth = null)
    {
        var settled = s.Phase is JobPhase.Completed or JobPhase.CompletedWithErrors;
        var t = truth ?? ProgressTruthSnapshot.Create(
            plannedBytes: s.TotalBytes,
            committedBytes: s.CompletedBytes,
            inFlightConfirmedBytes: 0,
            currentObjectId: objId,
            currentObjectPlannedBytes: objTotal,
            currentObjectConfirmedBytes: objDone,
            attemptEpoch: Interlocked.Read(ref _attemptEpoch),
            retryState: _currentRetryState,
            speedBytesPerSecond: speed,
            settled: settled,
            paused: s.Phase == JobPhase.Paused,
            targetStatTrusted: false,
            inFlightSource: ProgressTruthSource.CompletedReceipts,
            timestampUtc: DateTime.UtcNow);

        progress?.Report(new ProgressSnapshot(
            s.Phase, s.TotalObjects, s.CompletedObjects, s.FailedObjects,
            t.PlannedBytes, t.DisplayedTransferredBytes, t.Percent,
            t.SpeedBytesPerSecond, t.EtaSeconds, objId, objPath, msg, objDone, objTotal, objLarge, stall,
            // 暂停真值随每一个快照送达 UI（UI 不得自己猜）
            s.PauseState, s.PauseRequestedUtc, s.PauseAchievedUtc, s.PauseOutcome, s.PauseFailureReason,
            t));
    }

    /// <summary>
    /// 飞行中字节的来源标注（FIX BATCH 4）：/Z 串行通道的目标长度不可信，本趟若有进程 I/O 遥测确认的
    /// 字节则以它为准；否则按"解析行 + 目标实测"的既有通道标注。
    /// </summary>
    private ProgressTruthSource ResolveInFlightSource(long effBytes)
    {
        if (effBytes <= 0) return ProgressTruthSource.None;
        if (Interlocked.Read(ref _ioConfirmedBytes) > 0 && !RobocopyRunner.TrustsTargetStatForProgress(_serialLargePass))
            return ProgressTruthSource.WorkerIoCounters;
        return RobocopyRunner.TrustsTargetStatForProgress(_serialLargePass)
            ? ProgressTruthSource.TargetStat
            : ProgressTruthSource.ParsedWorkerOutput;
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
