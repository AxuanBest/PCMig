namespace PCMig.Core.Transfer;

/// <summary>
/// 进度真值的来源通道（FIX BATCH 4 / P1-1）。"已确认字节"必须能说清它是怎么被确认的，
/// 否则"界面数字"就又变成了一次猜测。
/// </summary>
public enum ProgressTruthSource
{
    /// <summary>本次没有任何飞行中确认字节，或来源不可信（不允许冒充"已确认"）。</summary>
    None = 0,

    /// <summary>已完成对象的持久化回执（最可信：有 receipt 才有字节）。</summary>
    CompletedReceipts = 1,

    /// <summary>robocopy 输出行解析（"新文件"行 + 文件级结果）。</summary>
    ParsedWorkerOutput = 2,

    /// <summary>目标文件长度实测。**只有在本趟不做预分配**（<see cref="RobocopyRunner.TrustsTargetStatForProgress"/> 为 true）时才可信。</summary>
    TargetStat = 3,

    /// <summary>worker 进程的内核 I/O 计数器（读源字节）。连续、不受 stdout 块缓冲影响。</summary>
    WorkerIoCounters = 4,
}

/// <summary>重试/回冲真值（§7.3）：进度允许下降，但必须解释清楚，绝不静默归零。</summary>
public enum RetryState
{
    /// <summary>没有发生重试或回冲。</summary>
    None = 0,

    /// <summary>本对象正在重试（新一趟尝试已开始，单调基线重开）。</summary>
    Retrying = 1,

    /// <summary>本趟出现进度回冲（robocopy 明确报某文件失败，已入账字节被扣回）。</summary>
    RollingBack = 2,
}

/// <summary>
/// ★ FIX BATCH 4（P1-1）：全局进度唯一真值 ★
///
/// 真机症状（200+ GB 验收）：网络实测 35+ MB/s，界面底栏却长期停在 0 B；对象边界处数字突然大跳；
/// 重试/回冲时归零或回跳；顶栏与底栏口径不一致。它们的同一个根因是：
/// **分子只有"已完成对象的回执字节 + 已确认的飞行中字节"，而 /Z 串行大文件通道既不能用目标
/// 长度（预分配会虚报）、又不允许回退枚举、robocopy stdout 又是块缓冲** ⇒ 单个 28.5 GB 对象在
/// 整个复制期间分子纹丝不动。
///
/// 本类型把"显示什么"变成一个可测的定义，而不是 UI 各处的即兴计算：
///
/// <list type="bullet">
///   <item><see cref="PlannedBytes"/>：本任务计划总字节（job-state 的 TotalBytes，分母）。</item>
///   <item><see cref="CommittedBytes"/>：**已完成对象**的持久化回执字节之和（有 receipt 才算）。</item>
///   <item><see cref="InFlightConfirmedBytes"/>：当前对象内**已被可信来源确认**的落盘字节
///         （见 <see cref="InFlightSource"/>）。"确认"的含义 = 该来源能证明这部分字节已经真的落到目标上，
///         或者已被 worker 从源读出（进程 I/O 计数器，可被预分配污染的长度的替代品，且**只用于显示**）。</item>
///   <item><see cref="DisplayedTransferredBytes"/>：**唯一允许显示给用户的分子**：
///         <c>CommittedBytes + InFlightConfirmedBytes</c>，下限 <see cref="CommittedBytes"/>（已入库的
///         字节绝不被重试/回冲抹掉），上限 <see cref="PlannedBytes"/>（分子永不大于分母），
///         上限未知（PlannedBytes==0）时不封顶。</item>
///   <item><see cref="Percent"/>：0–100；运行中永不 100%（最多 99.9），收尾（settled）才精确收敛到 100。</item>
///   <item><see cref="SpeedBytesPerSecond"/> / <see cref="EtaSeconds"/>：暂停时一律 0 / NaN（不显示旧 ETA）。</item>
///   <item><see cref="AttemptEpoch"/> / <see cref="RetryState"/>：重试与回冲必须能被解释（<see cref="IsRetryExplained"/>）。</item>
/// </list>
///
/// 纪律：顶栏进度条、底栏进度条、百分比、字节数、速率、ETA **必须**全部来自同一个
/// <see cref="ProgressTruthSnapshot"/> 实例；UI 不允许再做第二套算术。持久化 job-state 的
/// <c>CompletedBytes</c> 也必须写 <see cref="DisplayedTransferredBytes"/>（见 <see cref="MatchesPersisted"/>）。
/// </summary>
public sealed record ProgressTruthSnapshot(
    long PlannedBytes,
    long CommittedBytes,
    long InFlightConfirmedBytes,
    long DisplayedTransferredBytes,
    double Percent,
    string? CurrentObjectId,
    long CurrentObjectPlannedBytes,
    long CurrentObjectConfirmedBytes,
    long AttemptEpoch,
    RetryState RetryState,
    double SpeedBytesPerSecond,
    double EtaSeconds,
    bool Paused,
    ProgressTruthSource InFlightSource,
    DateTime TimestampUtc)
{
    /// <summary>运行中允许显示的最高百分比（避免"99.9% 卡住"与"未完成却 100%"两种假象）。</summary>
    public const double RunningPercentCeiling = 99.9;

    /// <summary>重试/回冲是否被如实解释（<see cref="RetryState.None"/> 且已进入第 2+ 趟 = 静默归零）。</summary>
    public bool IsRetryExplained => RetryState != RetryState.None || AttemptEpoch <= 1;

    /// <summary>持久化状态是否与本真值同源（§7 要求"persisted state 与 UI snapshot 不矛盾"）。</summary>
    public bool MatchesPersisted(Models.JobState state)
        => state.TotalBytes == PlannedBytes && state.CompletedBytes == DisplayedTransferredBytes;

    /// <summary>
    /// 产出真值。所有算术只在这里做一次（UI 只读结果）。
    /// </summary>
    /// <param name="inFlightSource">飞行中字节的来源；<see cref="ProgressTruthSource.TargetStat"/> 在
    /// <paramref name="targetStatTrusted"/> 为 false（/Z 预分配通道）时不被承认。</param>
    public static ProgressTruthSnapshot Create(
        long plannedBytes,
        long committedBytes,
        long inFlightConfirmedBytes,
        string? currentObjectId,
        long currentObjectPlannedBytes,
        long currentObjectConfirmedBytes,
        long attemptEpoch,
        RetryState retryState,
        double speedBytesPerSecond,
        bool settled,
        bool paused,
        bool targetStatTrusted,
        ProgressTruthSource inFlightSource,
        DateTime timestampUtc)
    {
        var committed = Math.Max(0, committedBytes);
        var inFlight = Math.Max(0, inFlightConfirmedBytes);

        // 不可信来源不允许记账：/Z 通道的目标长度是预分配长度，不是已落盘字节。
        var source = inFlightSource;
        if (source == ProgressTruthSource.TargetStat && !targetStatTrusted) source = ProgressTruthSource.None;
        if (inFlight == 0) source = ProgressTruthSource.None;

        var upper = plannedBytes > 0 ? Math.Max(plannedBytes, committed) : long.MaxValue;
        var displayed = Math.Min(committed + inFlight, upper);

        double percent;
        if (plannedBytes > 0)
        {
            var raw = Math.Min(100.0, displayed * 100.0 / plannedBytes);
            percent = settled ? raw : Math.Min(RunningPercentCeiling, raw);
        }
        else
        {
            percent = settled ? 100.0 : 0.0;
        }
        percent = Math.Clamp(percent, 0.0, 100.0);

        var speed = paused ? 0.0 : Math.Max(0.0, speedBytesPerSecond);
        double eta;
        if (paused || speed <= 0 || plannedBytes <= 0 || displayed >= plannedBytes) eta = double.NaN;
        else eta = (plannedBytes - displayed) / speed;

        return new ProgressTruthSnapshot(
            plannedBytes, committed, inFlight, displayed, percent,
            currentObjectId, currentObjectPlannedBytes, currentObjectConfirmedBytes,
            attemptEpoch, retryState, speed, eta, paused, source, timestampUtc);
    }
}