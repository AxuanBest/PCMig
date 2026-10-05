namespace PCMig.WinUI.Presentation;

/// <summary>
/// ★ Round-2 2026-10-05（§3.2 共享呈现时间线）★ 进度**呈现**协调器所处的视觉档位。
/// </summary>
internal enum ProgressVisualMode
{
    /// <summary>视觉跟随已确认的**显示真值**（正常推进，允许滞后追平）。</summary>
    Live = 0,

    /// <summary>已冻结：暂停 / 停止 / 中断 / 失败期间视觉停在有效显示真值上，不制造任何运动。</summary>
    Frozen = 1,

    /// <summary>已完成：真值本身已到 100%，视觉收敛上去（只有引擎真的完成才进入）。</summary>
    Completed = 2,
}

/// <summary>
/// ★ Round-2 §3.2 / ★ Round-3 PHASE B（§15）重写 ★ 进度**呈现**协调器 ——
/// 数字、字节与进度条共用的**唯一视觉时间线**。
///
/// 为什么需要它（用户 2026-10-05 视频证据）：
///   · 大号百分比文本**瞬间**跳到新真值，而进度条之后才补间追上去 ⇒ 用户看到"数字已经 47.3%、蓝条还在半路"；
///   · 真值本身可能一次前进十几到二十几个百分点（恢复时基线一次性发现已存在的字节、对象边界结算），
///     任何"每来一个目标就开一段补间"的做法都会在目标改变时**重启速度**，位置连续但速度每秒断几次 ⇒ 抽动；
///   · 轮询 200 ms 一次 ≠ 每 200 ms 有一个有用的新真值（采样频率不等于信息频率）。
///
/// ★ Round-3 PHASE B：从"逐 target 段动画"改为**连续指数状态滤波器**（§15）★
/// 旧实现（已废弃）：每次 <c>ApplyTruth</c> 都算一个 0.20~1.20 s 的时长、记 <c>_from/_to/_startedUtc</c>、
///   在 <c>Advance</c> 里按 <c>elapsed/duration</c> 线性插值。结构上"位置连续"，但**速度在每次目标改变时被重置**，
///   这正是用户视频里 52.0/52.2/54.9 s 那几处"一段一段跳"的根源之一。
/// 新实现：<c>TargetProgress</c> 与 <c>VisualProgress</c> 长期存在，每个渲染拍只做一件事
///   <c>visual += (target - visual) * (1 - exp(-k * dt))</c>（<c>k = <see cref="VisualK"/></c>，默认 10）。
///   目标改变时**只改目标**：不重启动画、不清速度状态、不新建段 —— 速度天然连续。
///
/// 四条硬边界（任何实现改动都不得突破）：
///   1. <see cref="VisualPercent"/> 永远 **不大于**最近一次已确认的显示真值（无预测、无外推、绝不自爬到 99%）；
///   2. 真值不变且视觉追上目标时**必须停下**，不得为了"看起来在动"而伪造运动；
///   3. 视觉在正常运行中**不倒退**；允许的倒退只有调用方显式声明的
///     <see cref="SnapTo"/>（显式回滚 / 重新计划 / 换任务）；
///   4. 暂停 / 停止 / 中断 / 失败由调用方 <see cref="Freeze"/> 冻结；只有引擎真的完成才 <see cref="ProgressVisualMode.Completed"/>。
///
/// 本类零 WinUI 依赖（不引用任何 Windows.* 类型）⇒ 可被测试项目直接链入并按行为断言。
/// </summary>
internal sealed class ProgressPresentationCoordinator
{
    /// <summary>
    /// ★ Round-3 PHASE B ★ 滤波器增益（每秒）：<c>visual += (target - visual) * (1 - exp(-k*dt))</c>。
    /// 取值依据（§15）：<c>k = 9~11</c> 先取 <b>10</b> ⇒ 时间常数 0.1 s、95% 收敛约 0.30 s。
    /// 太大（20+）= 又变成"瞬间跳"；太小（≤4）= 数字与条长期落后于真值，用户会认为界面卡住。
    /// </summary>
    public const double VisualK = 10d;

    /// <summary>滤波器时间常数（秒）= 1/k：视觉落后以它为单位指数衰减。</summary>
    public const double TimeConstantSeconds = 1d / VisualK;

    /// <summary>等效"收敛时长"（秒，诊断用）= 3τ：此时视觉已走完 95% 的差距。</summary>
    public const double SettleSeconds = 0.30d;

    /// <summary>
    /// 单拍时间步长上限（秒）= 1/30：窗口最小化、系统卡顿、断点调试之后 <c>dt</c> 可能很大，
    /// 若不夹住就会"一步跳完"（视觉上仍是一次大跳）——夹住后最坏也只是多花几帧追平。
    /// </summary>
    public const double MaxStepSeconds = 1d / 30d;

    /// <summary>百分比比较容差（百分点）：小于它的差视为"已到达"，不制造无意义的运动。</summary>
    public const double Epsilon = 0.0001;

    /// <summary>
    /// ★ Round-3 PHASE D（验收判据 ⑬）★ Completed 尾态吸附阈值（百分点）。
    /// 指数滤波只会渐近逼近目标，Completed 会永远停在 99.999…；本阈值把**已确认的**目标吃掉
    /// 最后一小段，让"真正完成后到 100%"成为可验证的确切数字。
    /// 取值 0.05 = 一位小数显示分辨率的**一半** ⇒ 吸附不改变屏幕上的数字，因此不算无证据前跳。
    /// </summary>
    public const double CompletedSnapEpsilon = 0.05d;

    private string? _jobId;
    private ProgressVisualMode _mode = ProgressVisualMode.Live;
    private double _visualPercent;
    private double _targetPercent;
    private double _confirmedPercent;
    private long _confirmedBytes;
    private long _plannedBytes;
    private DateTime _lastTickUtc = DateTime.MinValue;
    private string _lastReason = "init";

    /// <summary>当前视觉百分比（数字文本与进度条共用的唯一值）。</summary>
    public double VisualPercent => _visualPercent;

    /// <summary>当前视觉分子（按视觉百分比折算到计划字节；计划未知时退化为已确认字节）。</summary>
    public long VisualBytes
        => _plannedBytes > 0 ? (long)Math.Round(_plannedBytes * _visualPercent / 100.0) : _confirmedBytes;

    /// <summary>最近一次已确认的显示真值百分比（<see cref="VisualPercent"/> 的上限）。</summary>
    public double ConfirmedPercent => _confirmedPercent;

    /// <summary>最近一次已确认的显示真值分子。</summary>
    public long ConfirmedBytes => _confirmedBytes;

    /// <summary>计划总字节（分母）。</summary>
    public long PlannedBytes => _plannedBytes;

    /// <summary>当前视觉档位。</summary>
    public ProgressVisualMode Mode => _mode;

    /// <summary>视觉是否仍在追赶（冻结 / 已追上时为 false）。</summary>
    public bool IsAnimating => _mode != ProgressVisualMode.Frozen && LagPercent > Epsilon;

    /// <summary>最近一次状态变化的理由（诊断用）。</summary>
    public string LastReason => _lastReason;

    /// <summary>
    /// 等效收敛时长（秒，诊断用）。★ Round-3：本类不再使用"补间时长"，这里是 3τ（95% 收敛）；
    /// 保留该成员是为了让仍在读它的诊断/页面在 PHASE E 替换完成前不炸掉。
    /// </summary>
    public double LastDurationSeconds => SettleSeconds;

    /// <summary>视觉落后已确认真值的百分点（0 = 已追上）。</summary>
    public double LagPercent => Math.Max(0d, _confirmedPercent - _visualPercent);

    /// <summary>视觉是否落后于已知真值（落后期间应当"大多数采样帧都在动"）。</summary>
    public bool IsLagging => LagPercent > Epsilon;

    /// <summary>当前任务标识（换任务即重置视觉）。</summary>
    public string? JobId => _jobId;

    /// <summary>重置到全新状态（换任务 / 全新运行）。视觉归零是允许的——这是"新任务从 0 开始"。</summary>
    public void Reset(string? jobId, string reason)
    {
        _jobId = jobId;
        _visualPercent = 0d;
        _targetPercent = 0d;
        _confirmedPercent = 0d;
        _confirmedBytes = 0L;
        _plannedBytes = 0L;
        _lastTickUtc = DateTime.MinValue;
        _mode = ProgressVisualMode.Live;
        _lastReason = reason;
    }

    /// <summary>
    /// 接收一拍**显示真值**（已经过 <c>ContinuationDisplayState</c> 托底的 effective 值）。
    /// 只有这里会设定视觉目标；目标一律 ≤ 传入的已确认真值，且**只改目标**（不重启任何动画）。
    /// </summary>
    public void ApplyTruth(double confirmedPercent, long confirmedBytes, long plannedBytes, bool settled,
        string? jobId, DateTime utcNow, string reason = "truth")
    {
        var changedJob = !string.Equals(jobId, _jobId, StringComparison.Ordinal);
        if (changedJob && (jobId is not null || _jobId is not null))
        {
            Reset(jobId, "job-changed");
        }
        else
        {
            _jobId = jobId;
        }

        if (plannedBytes > 0) _plannedBytes = plannedBytes;

        // 显示真值在正常运行中只增不减（倒退只能由调用方显式 SnapTo 声明）：这样上限永远可靠。
        _confirmedBytes = Math.Max(_confirmedBytes, Math.Max(0L, confirmedBytes));
        _confirmedPercent = Math.Max(_confirmedPercent, Clamp(confirmedPercent, 0d, 100d));

        if (settled)
        {
            _mode = ProgressVisualMode.Completed;
            _lastReason = "settled";
        }
        else if (_mode == ProgressVisualMode.Completed)
        {
            // 引擎回退了"已完成"判定（例如又发现失败对象）⇒ 回到正常档位，但不制造视觉回退
            _mode = ProgressVisualMode.Live;
            _lastReason = "unsettled";
        }

        if (_mode == ProgressVisualMode.Frozen) return;   // 冻结期间不接受任何新目标

        // ★ 只改目标 ★ 目标 = 已确认真值（单调）。不记 from/to、不算时长、不重置速度状态。
        if (_confirmedPercent > _targetPercent)
        {
            _targetPercent = _confirmedPercent;
            _lastReason = reason;
        }
        else
        {
            _lastReason = reason + ":at-target";
        }

        _ = utcNow;   // 保留参数以兼容既有调用点：本实现不需要"动画起点"，时间只由 Advance 的 dt 决定
    }

    /// <summary>
    /// 冻结视觉并落到有效显示真值上（暂停 / 停止 / 中断 / 失败）。
    /// 语义：用户此刻看到的值必须**保持不动**，直到调用方显式恢复（§2 的显示连续性在同一层被复用）。
    /// </summary>
    public void Freeze(double effectivePercent, DateTime utcNow, string reason)
    {
        _mode = ProgressVisualMode.Frozen;
        _lastReason = reason;
        _lastTickUtc = utcNow;
        var target = Clamp(effectivePercent, 0d, 100d);
        if (target > _visualPercent) _visualPercent = target;
        // 冻结值即新的视觉上限：它来自 ContinuationDisplayState 的高水位，代表"用户已经看到过"的水平
        if (_visualPercent > _confirmedPercent) _confirmedPercent = _visualPercent;
        _targetPercent = _visualPercent;   // 冻结期间目标即冻结值 ⇒ 解冻瞬间不会突然上冲
    }

    /// <summary>从冻结恢复到正常档位（Resume 时调用；视觉仍停在原处，由后续真值驱动前进）。</summary>
    public void ResumeLive(DateTime utcNow, string reason)
    {
        if (_mode == ProgressVisualMode.Frozen)
        {
            _mode = ProgressVisualMode.Live;
            _lastReason = reason;
            _lastTickUtc = utcNow;   // 暂停期间的时间不参与 dt（否则解冻第一帧会"补"一大步）
        }
    }

    /// <summary>
    /// 显式允许的视觉回退（显式回滚 / 重新计划 / 换任务）。**只有调用方明确要求**才会倒退，
    /// 且调用方必须在日志与诊断里给出理由（不得静默倒退）。
    /// </summary>
    public void SnapTo(double percent, DateTime utcNow, string reason)
    {
        _lastTickUtc = utcNow;
        _visualPercent = Clamp(percent, 0d, 100d);
        _targetPercent = _visualPercent;
        _confirmedPercent = _visualPercent;
        _lastReason = reason;
        if (_mode == ProgressVisualMode.Frozen) _mode = ProgressVisualMode.Live;
    }

    /// <summary>
    /// 推进到 <paramref name="utcNow"/> 时刻的视觉值，并返回它。UI 每个渲染拍（约 60 Hz）调用它取文本与条宽，
    /// 两者消费同一个比例 ⇒ 严格同源（§16 / R23）。
    /// <c>dt</c> 来自调用方传入的高精度时间戳并夹到 <see cref="MaxStepSeconds"/>。
    /// </summary>
    public double Advance(DateTime utcNow)
    {
        var dt = 0d;
        if (_lastTickUtc != DateTime.MinValue)
        {
            dt = (utcNow - _lastTickUtc).TotalSeconds;
            if (dt < 0d) dt = 0d;
            else if (dt > MaxStepSeconds) dt = MaxStepSeconds;
        }
        _lastTickUtc = utcNow;

        if (_mode != ProgressVisualMode.Frozen && dt > 0d && _targetPercent > _visualPercent + Epsilon)
        {
            var alpha = 1d - Math.Exp(-VisualK * dt);
            var next = _visualPercent + (_targetPercent - _visualPercent) * alpha;
            _visualPercent = next > _targetPercent ? _targetPercent : next;
        }

        // ★ Round-3 PHASE D（验收判据 ⑬）★ 指数滤波永远只**渐近**逼近目标，Completed 尾态会停在
        // 99.999… 而永远不写 100。产品语义要求"Core 真正完成后才到 100%"（§19），所以这里把
        // **已经确认的**目标吃掉最后一小段：阈值 0.05 个百分点 = 显示分辨率（一位小数）的一半
        // ⇒ 吸附绝不会改变屏幕上显示的数字，因此不构成"无证据前跳"（判据 ④），也不是预测：
        // 目标本身就是已确认真值，且只可能在 CurrentMode == Completed 时发生。
        if (_mode == ProgressVisualMode.Completed && _targetPercent > _visualPercent
            && _targetPercent - _visualPercent <= CompletedSnapEpsilon)
        {
            _visualPercent = _targetPercent;
        }

        return ClampVisual();
    }

    /// <summary>诊断文本（DEBUG 探针用）。</summary>
    public string Describe()
        => $"visual={_visualPercent:0.###}% target={_targetPercent:0.###}% confirmed={_confirmedPercent:0.###}% " +
           $"bytes={_confirmedBytes} planned={_plannedBytes} mode={_mode} lagging={IsLagging} " +
           $"k={VisualK:0.##} tau={TimeConstantSeconds:0.###} lag={LagPercent:0.###} reason={_lastReason}";

    private double ClampVisual()
    {
        if (_mode != ProgressVisualMode.Frozen && _visualPercent > _confirmedPercent)
        {
            _visualPercent = _confirmedPercent;   // 硬边界 1：绝不越过已确认真值
        }
        return _visualPercent;
    }

    private static double Clamp(double value, double min, double max)
        => double.IsNaN(value) ? min : (value < min ? min : (value > max ? max : value));
}