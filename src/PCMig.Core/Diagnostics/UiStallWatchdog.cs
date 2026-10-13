using System.Diagnostics;

namespace PCMig.Core.Diagnostics;

/// <summary>
/// 一次"界面没响应"的实测事实（★ P0-06(1) ★ 现场要的正是这条证据）。
/// </summary>
/// <param name="StallMs">被判定为卡住时，距离**最后一次成功心跳**已过去的毫秒数（下限 = 阈值）。</param>
/// <param name="LastHeartbeatMs">最后一次心跳发生在监视器启动后多少毫秒（= 最后一次可用交互的时间点）。</param>
/// <param name="LastHeartbeatContext">最后一次心跳是谁打的（如 "tick" / "stop-click"），用来回答"最后一次可用操作是什么"。</param>
/// <param name="DetectedUtc">检测到卡住的时刻（UTC）。</param>
/// <param name="Recovered">true = 这是一条"已恢复"的记录（心跳重新出现），<paramref name="TotalStallMs"/> 是这次卡住的总时长。</param>
/// <param name="TotalStallMs">卡住总时长（仅 <paramref name="Recovered"/> = true 时有值）。</param>
public sealed record UiStallFact(
    long StallMs,
    long LastHeartbeatMs,
    string? LastHeartbeatContext,
    DateTime DetectedUtc,
    bool Recovered,
    long? TotalStallMs = null);

/// <summary>
/// ★ P0-06(1)/(4) ★ UI 线程心跳监视器 —— 用来把"卡住"从用户口述变成可读事实。
///
/// 现场只有一句"点击停止时 UI 卡住或无响应"，ZIP 里没有 WER / Application Hang 1002 / 转储，
/// 因此**没有**任何一条"界面未响应了多久、最后还能操作的是什么"的证据。本类型补的就是这一条：
///
///   · UI 线程每拍调用一次 <see cref="Heartbeat"/>（只做一次 volatile 写，零分配、不抛）；
///   · 采样跑在**后台线程**（这样 UI 线程真的卡住时才有能力观察它的沉默——
///     任何"用 UI 定时器检测 UI 卡死"的写法在原理上都不可能成立）；
///   · 超过阈值没心跳 ⇒ 判定"界面未响应"，**在卡住期间**就把事实交给回调（写日志/落盘），
///     因为应用如果被强杀，事后什么也留不下；
///   · 心跳重新出现 ⇒ 再补一条"已恢复 + 总时长"。
///
/// 纪律：本类型不改变任何业务结论（不停止、不取消、不改任务状态），只记录事实；
/// 任何回调异常都被吞掉并只记在 <see cref="CallbackFailures"/> 计数里，绝不让监视器本身成为崩溃源。
/// </summary>
public sealed class UiStallWatchdog : IDisposable
{
    private readonly int _stallThresholdMs;
    private readonly int _sampleIntervalMs;
    private readonly Action<UiStallFact>? _onStall;
    private readonly Stopwatch _clock = new();
    private readonly List<UiStallFact> _stalls = new();
    private readonly object _gate = new();

    private Timer? _timer;
    private long _lastBeatMs;
    private string? _lastContext;
    private bool _stalled;
    private long _stalledSinceMs;
    private int _callbackFailures;
    private volatile bool _disposed;

    /// <param name="stallThresholdMs">多久没心跳算"未响应"（默认 1500ms：20Hz 心跳下等于漏掉 30 拍）。</param>
    /// <param name="sampleIntervalMs">后台采样间隔（默认 250ms）。</param>
    /// <param name="onStall">检测到/恢复到时的回调（在**后台线程**调用；不得抛，抛了也只计数）。</param>
    public UiStallWatchdog(int stallThresholdMs = 1500, int sampleIntervalMs = 250, Action<UiStallFact>? onStall = null)
    {
        if (stallThresholdMs < 100) throw new ArgumentOutOfRangeException(nameof(stallThresholdMs));
        if (sampleIntervalMs < 50) throw new ArgumentOutOfRangeException(nameof(sampleIntervalMs));
        _stallThresholdMs = stallThresholdMs;
        _sampleIntervalMs = sampleIntervalMs;
        _onStall = onStall;
    }

    /// <summary>判定"未响应"的阈值（毫秒）。</summary>
    public int StallThresholdMs => _stallThresholdMs;

    /// <summary>是否正在采样。</summary>
    public bool IsRunning => _timer is not null && !_disposed;

    /// <summary>已记录的事实（检测 + 恢复，按发生顺序）。</summary>
    public IReadOnlyList<UiStallFact> Stalls
    {
        get { lock (_gate) return _stalls.ToArray(); }
    }

    /// <summary>最严重的一次卡住（检测记录里 StallMs 最大的那条；从未卡住时 null）。</summary>
    public UiStallFact? Worst
    {
        get
        {
            lock (_gate)
            {
                UiStallFact? worst = null;
                foreach (var f in _stalls)
                    if (!f.Recovered && (worst is null || f.StallMs > worst.StallMs))
                        worst = f;
                return worst;
            }
        }
    }

    /// <summary>回调自身抛异常的次数（监视器不因回调失败而停止采样）。</summary>
    public int CallbackFailures => _callbackFailures;

    /// <summary>距离最后一次心跳过去了多少毫秒（用于诊断与报告）。</summary>
    public long SinceLastHeartbeatMs
    {
        get
        {
            lock (_gate) return IdleMs();
        }
    }

    /// <summary>开始采样（幂等；<see cref="Dispose"/> 之后不再启动）。</summary>
    public void Start()
    {
        if (_disposed || _timer is not null) return;
        lock (_gate)
        {
            if (_disposed || _timer is not null) return;
            _clock.Start();
            // ★ 起点即"刚启动"：UI 线程还没打过心跳也不能立刻算卡住。
            _lastBeatMs = _clock.ElapsedMilliseconds;
            _lastContext = "watchdog-start";
            _timer = new Timer(_ => Sample(), null, _sampleIntervalMs, _sampleIntervalMs);
        }
    }

    /// <summary>
    /// UI 线程心跳（每拍一次）。**只做一次写入，绝不抛、不分配、不锁竞争**——
    /// 它本身绝不能是界面变慢的原因。
    /// </summary>
    public void Heartbeat(string context = "ui-tick")
    {
        if (_disposed) return;
        UiStallFact? recovered = null;
        lock (_gate)
        {
            _lastBeatMs = _clock.IsRunning ? _clock.ElapsedMilliseconds : 0;
            _lastContext = context;
            if (!_stalled) return;
            // 心跳回来了 ⇒ 补一条"已恢复 + 总时长"。
            var total = _lastBeatMs - _stalledSinceMs;
            _stalled = false;
            recovered = new UiStallFact(_stalledSinceMs, _lastBeatMs, context, DateTime.UtcNow, Recovered: true, TotalStallMs: total < 0 ? 0 : total);
            _stalls.Add(recovered);
        }
        // ★ 通知（含证据落盘）放在锁外 ★：落盘要做 fsync，不能占着心跳锁。
        Notify(recovered);
    }

    /// <summary>停止采样（幂等；保留已记录的事实）。</summary>
    public void Stop()
    {
        Timer? timer;
        lock (_gate)
        {
            timer = _timer;
            _timer = null;
            _clock.Stop();
        }
        try { timer?.Dispose(); } catch { /* 停止期异常不得外溢 */ }
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }

    // ────────────────────────────── 内部 ──────────────────────────────

    private void Sample()
    {
        if (_disposed) return;
        List<UiStallFact> fresh = new();
        lock (_gate)
        {
            if (_timer is null) return;
            var idle = IdleMs();
            if (idle < _stallThresholdMs) return;
            if (_stalled) return;                     // 同一段卡住只报一次（避免刷屏淹没证据）
            _stalled = true;
            _stalledSinceMs = _clock.ElapsedMilliseconds;
            var fact = new UiStallFact(idle, _lastBeatMs, _lastContext, DateTime.UtcNow, Recovered: false);
            _stalls.Add(fact);
            fresh.Add(fact);
        }
        foreach (var f in fresh) Notify(f);
    }

    private long IdleMs()
    {
        var now = _clock.IsRunning ? _clock.ElapsedMilliseconds : _lastBeatMs;
        var idle = now - _lastBeatMs;
        return idle < 0 ? 0 : idle;
    }

    private void Notify(UiStallFact fact)
    {
        // ★ 先落盘、再回调 ★：回调可能没接（_onStall 为空）或自身崩掉，而进程还可能随后被强杀 /
        // 被 WER 回收 —— 这条证据必须在磁盘上，不依赖任何下游。
        PersistEvidence(fact);
        if (_onStall is null) return;
        try { _onStall(fact); }
        catch { Interlocked.Increment(ref _callbackFailures); }
    }

    /// <summary>把一条卡死/恢复事实写进崩溃证据通道（自身绝不抛）。</summary>
    private static void PersistEvidence(UiStallFact fact)
    {
        var kind = fact.Recovered ? "ui-stall-recovered" : "ui-stall";
        var detail = fact.Recovered
            ? $"界面未响应的总时长 {fact.TotalStallMs ?? 0} ms（最后一次心跳上下文：{fact.LastHeartbeatContext}）"
            : $"距最后一次心跳（{fact.LastHeartbeatContext}）已 {fact.StallMs} ms 没有滴答；界面判定为未响应。";
        CrashEvidence.Write(kind, detail, where: "UiStallWatchdog");
    }
}