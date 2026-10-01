using System;
using System.Diagnostics;

namespace PCMig.Diagnostics.Abstractions;

/// <summary>
/// **单调时钟**：唯一允许用来判断"到期/限流窗口"的时间源。
///
/// 为什么不能用 <c>DateTime.UtcNow</c>（D6.3 §14 / 审计 P2-2）：
///   UtcNow 是**墙上时钟**——NTP 校时、夏令时切换、用户手工改表都可能让它**回拨**或**前跳**。
///   一旦它被用作"60 秒观测窗口"或"150ms 去重窗口"的基准：
///     · 回拨 ⇒ 窗口被无限延长，"限时"这句话变成假的；
///     · 前跳 ⇒ 窗口提前结束，观测**悄悄**少记，而事件里没有任何痕迹。
///   单调时钟只回答"过了多久"，因此"限时 60 秒"在任何改表情形下都仍然成立。
/// </summary>
public interface IMonotonicClock
{
    /// <summary>自某个固定原点起的单调毫秒数。**只保证不减、不跳**；绝对值无意义。</summary>
    long NowMilliseconds { get; }
}

/// <summary>
/// 基于 <see cref="Stopwatch"/> 的单调时钟（进程级共享实例）。
/// 分辨率远高于 <c>Environment.TickCount64</c>（后者在 Windows 上约 15.6ms 一跳，
/// 会把 150ms 去重窗口量化成 10% 的误差）。
/// </summary>
public sealed class StopwatchMonotonicClock : IMonotonicClock
{
    /// <summary>进程级共享实例（无状态，可安全并发读取）。</summary>
    public static readonly StopwatchMonotonicClock Shared = new();

    private readonly double _ticksToMilliseconds = 1000.0 / Stopwatch.Frequency;

    public long NowMilliseconds => (long)(Stopwatch.GetTimestamp() * _ticksToMilliseconds);
}