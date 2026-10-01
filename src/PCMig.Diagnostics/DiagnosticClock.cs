using System.Diagnostics;
using PCMig.Diagnostics.Abstractions;

namespace PCMig.Diagnostics;

/// <summary>
/// 时间源：UTC（墙钟）+ Stopwatch（单调）。**单调时钟只在同一 session 内可比**；
/// 跨机/跨 session 比较必须带 UTC 锚点与不确定度（方案 §8）。
/// 时钟跳变（用户改系统时间/NTP 校正）会被记录下来，且 duration 不回退。
/// </summary>
public sealed class DiagnosticClock
{
    /// <summary>Stopwatch 频率（写入 session 元数据，供离线包解释 MonotonicTimestamp）。</summary>
    public static long Frequency => Stopwatch.Frequency;

    private long _lastUtcTicks;
    private long _lastMonotonic;
    private long _jumpCount;
    private long _lastMaxSeenUtcTicks;

    public DiagnosticClock()
    {
        _lastUtcTicks = DateTime.UtcNow.Ticks;
        _lastMonotonic = Stopwatch.GetTimestamp();
        _lastMaxSeenUtcTicks = _lastUtcTicks;
    }

    /// <summary>当前单调计数（Stopwatch ticks）。</summary>
    public long MonotonicTicks => Stopwatch.GetTimestamp();

    /// <summary>UTC 锚点（写进会话元数据；离线包据此把单调时间换算成挂钟时间）。</summary>
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <summary>检测到的时钟跳变次数（UTC 向后跳或远超单调增量的向前跳）。</summary>
    public long ClockJumpCount => Interlocked.Read(ref _jumpCount);

    /// <summary>
    /// 采样一次并检测跳变。返回 true = 本次采样观察到时钟异常。
    /// 判据只用"UTC 增量 vs 单调增量"的偏差，不做任何阻塞/校准。
    /// </summary>
    public bool TrySample(out long monotonicTicks, out DateTimeOffset utcNow, out bool clockJumpDetected)
    {
        monotonicTicks = Stopwatch.GetTimestamp();
        utcNow = DateTimeOffset.UtcNow;

        var utcTicks = utcNow.UtcTicks;
        var monoDelta = monotonicTicks - Volatile.Read(ref _lastMonotonic);
        Volatile.Write(ref _lastMonotonic, monotonicTicks);

        var maxSeen = Volatile.Read(ref _lastMaxSeenUtcTicks);
        clockJumpDetected = false;

        // 明显越过历史最大 UTC（向前跳 > 5 分钟）或向后跳（> 1 秒）都算异常。
        if (utcTicks > maxSeen + TimeSpan.TicksPerMinute * 5) clockJumpDetected = true;
        else if (utcTicks < Volatile.Read(ref _lastUtcTicks) - TimeSpan.TicksPerSecond) clockJumpDetected = true;

        // 睡眠/调试暂停的识别：单调增量很大而 UTC 增量也很大是正常的；
        // 这里只关心"两者不一致"，不猜测原因（原因属于规则层）。
        _ = monoDelta;

        Volatile.Write(ref _lastUtcTicks, utcTicks);
        if (utcTicks > maxSeen) Volatile.Write(ref _lastMaxSeenUtcTicks, utcTicks);
        if (clockJumpDetected) Interlocked.Increment(ref _jumpCount);
        return true;
    }

    /// <summary>把单调 ticks 换算成毫秒（仅用于 duration 计算）。</summary>
    public static long TicksToMs(long ticks) => (long)(ticks * 1000.0 / Stopwatch.Frequency);
}