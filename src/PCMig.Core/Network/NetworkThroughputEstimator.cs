namespace PCMig.Core.Network;

/// <summary>一次采样的结局（用于如实说明"这个样本为什么没算进速率"）。</summary>
public enum NetworkSampleOutcome
{
    /// <summary>样本有效，已纳入 raw / EMA / 稳定窗口。</summary>
    Ok = 0,
    /// <summary>首次采样：只建立基线，还不产生速率（没有前一个读数就没法求 Δ）。</summary>
    NoBaseline,
    /// <summary>Δt 太小（或被上层合并得太近）：保留基线不推进，字节不丢，等下一次更长间隔一起算。</summary>
    TooSoon,
    /// <summary>Δt ≤ 0（时钟回退/重复时间戳）：丢弃本次推进，保留基线，绝不产生负速率或巨大尖峰。</summary>
    ClockWentBackwards,
    /// <summary>计数器回退（Δ &lt; 0）：网卡被重置/重插/驱动重启 ⇒ 本次样本作废，重新建立基线并清空平滑状态。</summary>
    CounterReset,
    /// <summary>与上次有效采样相隔过久（休眠/挂起/长时间不可用）：不再跨这个空洞求平均，重新开始窗口。</summary>
    StaleGap,
    /// <summary>接口不可用（索引为 0 / 读不到行信息 / 布局自检未通过）：本类型不产生任何速率。</summary>
    InterfaceUnavailable,
}

/// <summary>一次采样的结果快照（纯数据，不含任何 P/Invoke）。</summary>
/// <param name="Outcome">结局。</param>
/// <param name="DeltaSeconds">本次采样与前一次有效采样的时间差（秒；无效样本为 0）。</param>
/// <param name="DeltaOctets">本次采样与前一次基线的接收字节差（无效样本为 0）。</param>
/// <param name="RawBytesPerSecond">瞬时速率（仅 <see cref="NetworkSampleOutcome.Ok"/> 有效，其余为 <see cref="double.NaN"/>）。</param>
/// <param name="EmaBytesPerSecond">轻度 EMA（约 1~2 秒口径，供界面实时速度用）；从未采到有效样本时为 <see cref="double.NaN"/>。</param>
/// <param name="StableBytesPerSecond">稳定窗口均值（约 5~10 秒口径，供 ETA 用）；窗口为空时为 <see cref="double.NaN"/>。</param>
/// <param name="HasSufficientSamples">样本是否足以支撑 ETA。</param>
/// <param name="StableSampleCount">稳定窗口内的样本数。</param>
/// <param name="StableWindowSeconds">稳定窗口实际覆盖的时长（秒）。</param>
/// <param name="Reason">中文理由（无效样本必填，用于日志与界面如实说明）。</param>
public readonly record struct NetworkSampleResult(
    NetworkSampleOutcome Outcome,
    double DeltaSeconds,
    ulong DeltaOctets,
    double RawBytesPerSecond,
    double EmaBytesPerSecond,
    double StableBytesPerSecond,
    bool HasSufficientSamples,
    int StableSampleCount,
    double StableWindowSeconds,
    string Reason)
{
    /// <summary>是否已采到至少一个有效样本（即 EMA 可用）。</summary>
    public bool HasReading => !double.IsNaN(EmaBytesPerSecond);
}

/// <summary>
/// 纯计算：把"累计接收字节 + 时间间隔"的采样序列换算成速率。
///
/// <para>
/// 【为什么单独一层】旧缺陷是"逻辑完成速率被当成网络吞吐"：目标端已存在的文件被 Robocopy 快速 Skip 时，
/// 逻辑字节飞快进入完成口径，但这些字节**没有再次经过网卡**；SMB/文件缓存又会让 Preflight 测速虚高。
/// 所以速率必须从网卡计数器来，而且必须与 Progress Truth 完全解耦——本类型只吃
/// (接口索引, 累计字节, Δt)，吐速率，不碰 Receipt / CompletedBytes / Verifier。
/// </para>
///
/// <para>平滑参数与理由：</para>
/// <list type="bullet">
///   <item><b>EMA 时间常数 τ=1.5 s</b>：界面实时速度要求 1~2 秒口径，选 1.5 s 居中；
///         系数用 <c>1-exp(-Δt/τ)</c>（时间常数式），与采样频率无关——采样从 500 ms 变成 200 ms，平滑手感不变；</item>
///   <item><b>稳定窗口 8 s</b>：ETA 不能用抖动的瞬时值，任务要求"最近 5~10 秒更稳定的吞吐"，
///         8 s 居中；窗口内按**累计字节 ÷ 累计时长**算（不是把样本速率再平均），对采样间隔不均匀更稳健；</item>
///   <item><b>样本充足阈值：≥3 个样本 且 覆盖 ≥3.0 s</b>：不足就给"—"，绝不用一两个样本编出 ETA
///         （迁移刚开始时 ETA 乱跳比没有 ETA 更糟）；</item>
///   <item><b>Δt 过小阈值 0.2 s</b>：Δt 太小会让计数器量化误差放大成尖峰，此时保留基线不推进；</item>
///   <item><b>空洞阈值 24 s（=窗口 3 倍）</b>：休眠/长时间不可用之后不再跨空洞求平均，重新开始窗口。</item>
/// </list>
///
/// <para>线程安全：<b>非</b>线程安全（由采样器在单线程内使用，跨线程读取请由采样器加锁）。</para>
/// </summary>
public sealed class NetworkThroughputEstimator
{
    /// <summary>EMA 时间常数默认值（秒）：界面实时速度 1~2 秒口径的居中取值。</summary>
    public const double DefaultEmaTauSeconds = 1.5;

    /// <summary>稳定窗口默认值（秒）：ETA 用的 5~10 秒口径的居中取值。</summary>
    public const double DefaultStableWindowSeconds = 8.0;

    /// <summary>Δt 过小阈值（秒）：小于它就不推进基线（计数器量化误差会放大成尖峰）。</summary>
    public const double DefaultMinDeltaSeconds = 0.2;

    /// <summary>ETA 所需最少有效样本数。</summary>
    public const int DefaultMinStableSamples = 3;

    /// <summary>ETA 所需最少时间覆盖（秒）：与样本数双阈值，避免"500 ms 内来 3 个样本就敢报 ETA"。</summary>
    public const double DefaultMinEtaBasisSeconds = 3.0;

    /// <summary>空洞阈值倍数：Δt 超过 窗口 × 该倍数 ⇒ 视为 StaleGap，重新开始窗口。</summary>
    public const double DefaultStaleGapFactor = 3.0;

    private readonly double _emaTauSeconds;
    private readonly double _stableWindowSeconds;
    private readonly double _minDeltaSeconds;
    private readonly int _minStableSamples;
    private readonly double _minEtaBasisSeconds;
    private readonly double _staleGapSeconds;

    // 稳定窗口：每项 = (本次 Δt, 本次 Δbytes)。用"累计字节 ÷ 累计时长"求窗口均值。
    private readonly Queue<(double Dt, ulong DBytes)> _window = new();

    private uint _interfaceIndex;
    private ulong _lastOctets;
    private bool _hasBaseline;
    private double _ema;
    private bool _emaValid;
    private double _windowSeconds;
    private ulong _windowOctets;
    private long _resetsObserved;
    private long _samplesAccepted;

    /// <summary>构造（默认参数即为任务书给定的口径）。</summary>
    public NetworkThroughputEstimator(
        double emaTauSeconds = DefaultEmaTauSeconds,
        double stableWindowSeconds = DefaultStableWindowSeconds,
        double minDeltaSeconds = DefaultMinDeltaSeconds,
        int minStableSamples = DefaultMinStableSamples,
        double minEtaBasisSeconds = DefaultMinEtaBasisSeconds,
        double staleGapFactor = DefaultStaleGapFactor)
    {
        _emaTauSeconds = emaTauSeconds > 0 ? emaTauSeconds : DefaultEmaTauSeconds;
        _stableWindowSeconds = stableWindowSeconds > 0 ? stableWindowSeconds : DefaultStableWindowSeconds;
        _minDeltaSeconds = minDeltaSeconds > 0 ? minDeltaSeconds : DefaultMinDeltaSeconds;
        _minStableSamples = minStableSamples > 0 ? minStableSamples : DefaultMinStableSamples;
        _minEtaBasisSeconds = minEtaBasisSeconds > 0 ? minEtaBasisSeconds : DefaultMinEtaBasisSeconds;
        _staleGapSeconds = _stableWindowSeconds * (staleGapFactor > 0 ? staleGapFactor : DefaultStaleGapFactor);
    }

    /// <summary>是否已建立字节基线（没有基线时第一次采样只做基线）。</summary>
    public bool HasBaseline => _hasBaseline;

    /// <summary>基线所属接口索引；没有基线时为 0。</summary>
    public uint BaselineInterfaceIndex => _hasBaseline ? _interfaceIndex : 0;

    /// <summary>上一次的累计接收字节；没有基线时为 0。</summary>
    public ulong LastOctets => _lastOctets;

    /// <summary>是否已有有效读数（至少接受过一条样本，EMA 有效）。
    /// ★ 2026-10-08 修复 ★ 采样器 <c>BuildSnapshot</c> 读的就是这个属性：区分
    /// "已连接但还没采到任何样本 ⇒ 速度显示 —" 与 "已连接且有读数 ⇒ 显示真实速率"。</summary>
    public bool HasReading => _emaValid;

    /// <summary>轻度 EMA 速率（界面实时速度口径）；无有效样本时为 <see cref="double.NaN"/>。</summary>
    public double EmaBytesPerSecond => _emaValid ? _ema : double.NaN;

    /// <summary>稳定窗口均值（ETA 口径）；窗口为空时为 <see cref="double.NaN"/>。</summary>
    public double StableBytesPerSecond => _windowSeconds > 0 ? _windowOctets / _windowSeconds : double.NaN;

    /// <summary>样本是否足以支撑 ETA（样本数 + 时间覆盖双阈值）。</summary>
    public bool HasSufficientSamples => _window.Count >= _minStableSamples && _windowSeconds >= _minEtaBasisSeconds;

    /// <summary>稳定窗口内样本数。</summary>
    public int StableSampleCount => _window.Count;

    /// <summary>稳定窗口实际覆盖时长（秒）。</summary>
    public double StableWindowSeconds => _windowSeconds;

    /// <summary>累计接受的有效样本数。</summary>
    public long SamplesAccepted => _samplesAccepted;

    /// <summary>累计观察到的计数器重置次数（用于诊断"网卡被重置"）。</summary>
    public long ResetsObserved => _resetsObserved;

    /// <summary>稳定窗口上限（秒），供上层解释口径。</summary>
    public double StableWindowLimitSeconds => _stableWindowSeconds;

    /// <summary>EMA 时间常数（秒），供上层解释口径。</summary>
    public double EmaTauSeconds => _emaTauSeconds;

    /// <summary>
    /// 主动重设（换网卡、重新开始任务、用户手动重连等）。清空基线、窗口与 EMA。
    /// 【为什么】不同网卡的计数器不同源，相减得到的是垃圾；换网卡必须重新建立基线。
    /// </summary>
    public void Reset(uint interfaceIndex = 0)
    {
        _interfaceIndex = interfaceIndex;
        _lastOctets = 0;
        _hasBaseline = false;
        ClearWindowAndEma();
    }

    /// <summary>
    /// 喂入一次采样。<paramref name="deltaSeconds"/> 由调用方用自己的时钟算出（本类型不碰时钟，便于单测）。
    /// 任何异常情况都返回明确结局，<b>不抛异常</b>。
    /// </summary>
    public NetworkSampleResult Observe(uint interfaceIndex, ulong inOctets, double deltaSeconds)
    {
        if (interfaceIndex == 0)
        {
            return Unavailable("接口索引为 0：还没确定实际走哪块网卡，读数按不可用处理", deltaSeconds);
        }

        if (_hasBaseline && interfaceIndex != _interfaceIndex)
        {
            // 换网卡：计数器不同源，立刻重建基线（旧窗口作废）。
            _hasBaseline = false;
            ClearWindowAndEma();
        }

        if (!_hasBaseline)
        {
            _interfaceIndex = interfaceIndex;
            _lastOctets = inOctets;
            _hasBaseline = true;
            return new NetworkSampleResult(
                NetworkSampleOutcome.NoBaseline, 0, 0, double.NaN, EmaOrNaN(), StableOrNaN(),
                HasSufficientSamples, _window.Count, _windowSeconds,
                $"接口 #{interfaceIndex} 首次采样：只建立字节基线（{inOctets} 字节），本条不产生速率");
        }

        if (double.IsNaN(deltaSeconds) || deltaSeconds < 0)
        {
            // 时钟回退/时间戳无效：保留基线不推进（字节不丢，下一次有效间隔一起算），绝不算负速率。
            return new NetworkSampleResult(
                NetworkSampleOutcome.ClockWentBackwards, 0, 0, double.NaN, EmaOrNaN(), StableOrNaN(),
                HasSufficientSamples, _window.Count, _windowSeconds,
                $"Δt={deltaSeconds} 无效（≤0 或 NaN，系统时钟可能被调整过）：本次不推进基线，等下一个有效间隔");
        }

        if (deltaSeconds == 0)
        {
            return new NetworkSampleResult(
                NetworkSampleOutcome.ClockWentBackwards, 0, 0, double.NaN, EmaOrNaN(), StableOrNaN(),
                HasSufficientSamples, _window.Count, _windowSeconds,
                "Δt=0：同一时刻的重复采样，不产生速率（保留基线，字节留到下一次一起算）");
        }

        if (deltaSeconds < _minDeltaSeconds)
        {
            // Δt 过小：保留基线不推进。理由：计数器是整数，短时间内的量化误差会被除法放大成尖峰。
            return new NetworkSampleResult(
                NetworkSampleOutcome.TooSoon, deltaSeconds, 0, double.NaN, EmaOrNaN(), StableOrNaN(),
                HasSufficientSamples, _window.Count, _windowSeconds,
                $"Δt={deltaSeconds:0.###} s 小于下限 {_minDeltaSeconds:0.###} s：本次不推进基线（字节不丢，下一次一起算）");
        }

        if (inOctets < _lastOctets)
        {
            // 计数器回退 = 网卡重置/重插/驱动重启/32 位回绕。绝不能用 (ulong) 相减（会得到天文数字）。
            var previous = _lastOctets;
            _resetsObserved++;
            _lastOctets = inOctets;
            ClearWindowAndEma();
            return new NetworkSampleResult(
                NetworkSampleOutcome.CounterReset, deltaSeconds, 0, double.NaN, double.NaN, double.NaN,
                HasSufficientSamples, _window.Count, _windowSeconds,
                $"网卡累计接收字节从 {previous} 回退到 {inOctets}：判定计数器已重置（网卡被禁用/重插/驱动重启），" +
                "本次样本作废并重新建立基线（绝不用相减得到的谎报尖峰）");
        }

        if (deltaSeconds > _staleGapSeconds)
        {
            // 休眠/长时间不可用之后的空洞：不跨空洞求平均，重新开始窗口（否则会把 10 分钟的平均值当成"当前速度"）。
            _lastOctets = inOctets;
            ClearWindowAndEma();
            return new NetworkSampleResult(
                NetworkSampleOutcome.StaleGap, deltaSeconds, 0, double.NaN, double.NaN, double.NaN,
                false, 0, 0,
                $"距上次有效采样已过 {deltaSeconds:0.#} s（超过空洞阈值 {_staleGapSeconds:0.#} s，期间可能休眠/网卡不可用）：" +
                "不跨这个空洞求平均，从现在重新开始采样");
        }

        var delta = inOctets - _lastOctets;
        var raw = delta / deltaSeconds;
        _lastOctets = inOctets;
        _samplesAccepted++;

        // 时间常数式 EMA：采样间隔变化时平滑手感不变。
        var alpha = 1.0 - Math.Exp(-deltaSeconds / _emaTauSeconds);
        _ema = _emaValid ? (alpha * raw) + ((1.0 - alpha) * _ema) : raw;
        _emaValid = true;

        _window.Enqueue((deltaSeconds, delta));
        _windowSeconds += deltaSeconds;
        _windowOctets += delta;
        TrimWindow();

        return new NetworkSampleResult(
            NetworkSampleOutcome.Ok, deltaSeconds, delta, raw, _ema, StableOrNaN(),
            HasSufficientSamples, _window.Count, _windowSeconds,
            $"接口 #{interfaceIndex}：Δt={deltaSeconds:0.###} s，Δ接收={delta} 字节 ⇒ 瞬时 {raw:0} B/s，EMA {_ema:0} B/s");
    }

    /// <summary>接口读不到时调用：本类型不产生速率，但保留基线（下一次成功读数会覆盖整段间隔，字节不丢）。</summary>
    public NetworkSampleResult ObserveUnavailable(string reason, double deltaSeconds = 0)
    {
        return Unavailable(reason, deltaSeconds);
    }

    private NetworkSampleResult Unavailable(string reason, double deltaSeconds)
    {
        return new NetworkSampleResult(
            NetworkSampleOutcome.InterfaceUnavailable, 0, 0, double.NaN, EmaOrNaN(), StableOrNaN(),
            HasSufficientSamples, _window.Count, _windowSeconds, reason);
    }

    private void ClearWindowAndEma()
    {
        _window.Clear();
        _windowSeconds = 0;
        _windowOctets = 0;
        _ema = 0;
        _emaValid = false;
    }

    /// <summary>窗口只覆盖最近 <see cref="StableWindowLimitSeconds"/> 秒：从队首弹出最老的样本直到时长进入窗口内。</summary>
    private void TrimWindow()
    {
        while (_window.Count > 1)
        {
            var oldest = _window.Peek();
            if (_windowSeconds - oldest.Dt <= _stableWindowSeconds) break;
            _window.Dequeue();
            _windowSeconds -= oldest.Dt;
            _windowOctets -= oldest.DBytes;
        }
    }

    private double EmaOrNaN() => _emaValid ? _ema : double.NaN;

    private double StableOrNaN() => _windowSeconds > 0 ? _windowOctets / _windowSeconds : double.NaN;
}