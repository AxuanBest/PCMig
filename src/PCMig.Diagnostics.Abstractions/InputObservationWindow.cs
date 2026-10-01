using System;
using System.Collections.Generic;

namespace PCMig.Diagnostics.Abstractions;

/// <summary>
/// Deep Trace 停止的**固定理由码**（D6.3 §14）。
///
/// 收口点：停止必须走**唯一一条**权威路径，并且**必须带着理由**——
/// 否则"观测为什么结束了"只能靠猜，而"到期自动停"和"用户关掉了"在证据里长得一模一样。
/// 理由码是固定 token（不是自由文本），因此既能被人读懂，又不会夹带任何身份信息。
/// </summary>
public static class DeepTraceStopReasons
{
    /// <summary>用户在诊断中心关掉了 Deep Trace。</summary>
    public const string UserDisabled = "deep-toggled-off";

    /// <summary>应用正在关闭（关窗收尾，必须先于诊断收尾）。</summary>
    public const string WindowClosing = "window-closing";

    /// <summary>到达观测时限，自动停止（"限时"这句话的兑现点）。</summary>
    public const string DeadlineReached = "deadline-reached";

    /// <summary>重新开始观测（幂等重启：先停旧的，再起新的）。</summary>
    public const string Restarted = "restarted";

    /// <summary>诊断运行期已不可用（运行期未启动/已收尾）⇒ 观测无意义。</summary>
    public const string RuntimeUnavailable = "runtime-unavailable";

    /// <summary>调用方给出的理由码不在白名单内时的**如实**兜底（绝不假装懂）。</summary>
    public const string Unspecified = "unspecified";

    /// <summary>全部允许出现在事件里的理由码。</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        UserDisabled, WindowClosing, DeadlineReached, Restarted, RuntimeUnavailable, Unspecified,
    };

    public static bool IsKnown(string? reason) =>
        reason is not null && ((IReadOnlyList<string>)All).Contains(reason);

    /// <summary>白名单校验：未知理由一律降级为 <see cref="Unspecified"/>，不把自由文本带进事件。</summary>
    public static string Normalize(string? reason) => IsKnown(reason) ? reason! : Unspecified;
}

/// <summary>
/// Deep Trace 输入观测的**有限窗口**：时限 + 重复抑制，二者都只读<b>单调</b>毫秒
/// （见 <see cref="IMonotonicClock"/> 里关于墙上时钟的说明）。
///
/// 它是**纯逻辑**（无 WinUI 依赖、无系统调用、时间由参数传入），因此"限时 60 秒"和
/// "150ms 内同控件同类别只累加计数"这两条承诺可以被任何测试项目直接行为验证，
/// 而不是只能靠"读代码相信它"。
///
/// 有界性：去重字典条目上限 <see cref="MaxDedupeEntries"/>，超限即清空重来
/// ⇒ 绝不随会话长度增长。
/// </summary>
public sealed class InputObservationWindow
{
    /// <summary>默认最长观测时长（候选值，不是 SLA；到期自动停止）。</summary>
    public static readonly TimeSpan DefaultMaxDuration = TimeSpan.FromSeconds(60);

    /// <summary>同一控件+类别的重复输入抑制窗口（防点风暴把事件流打满）。</summary>
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromMilliseconds(150);

    /// <summary>去重字典的条目上限（超出即清空重来：有界，绝不随会话长度增长）。</summary>
    public const int MaxDedupeEntries = 256;

    private readonly Dictionary<string, DedupeEntry> _lastByKey = new(StringComparer.Ordinal);

    private long _startedMilliseconds;
    private long _durationMilliseconds;
    private long _deadlineMilliseconds;

    /// <summary>窗口是否开着（到期判定不自动改这个标志：停止必须有明确出口与理由）。</summary>
    public bool IsOpen { get; private set; }

    /// <summary>窗口开始时的单调毫秒。</summary>
    public long StartedMilliseconds => _startedMilliseconds;

    /// <summary>窗口的绝对单调截止点（未开窗时为 0）。</summary>
    public long DeadlineMilliseconds => _deadlineMilliseconds;

    /// <summary>当前跟踪的去重键数量（用于验证有界性）。</summary>
    public int TrackedKeyCount => _lastByKey.Count;

    /// <summary>
    /// 开窗。<paramref name="duration"/> 非正 ⇒ 返回 false（调用方不得假装已开窗）。
    /// 重复调用会覆盖参数并重新计时。
    /// </summary>
    public bool Start(long nowMilliseconds, TimeSpan duration)
    {
        var durationMs = (long)duration.TotalMilliseconds;
        if (durationMs <= 0) return false;

        _startedMilliseconds = nowMilliseconds;
        _durationMilliseconds = durationMs;
        _deadlineMilliseconds = nowMilliseconds + durationMs;
        _lastByKey.Clear();
        IsOpen = true;
        return true;
    }

    /// <summary>关窗并清空去重表（幂等）。</summary>
    public void Close()
    {
        IsOpen = false;
        _lastByKey.Clear();
    }

    /// <summary>是否已到截止点。只有开着的窗口才可能"到期"。</summary>
    public bool IsExpired(long nowMilliseconds) =>
        IsOpen && nowMilliseconds - _startedMilliseconds >= _durationMilliseconds;

    /// <summary>剩余毫秒（夹在 0 以上；关窗后为 0）。绝不返回负数。</summary>
    public long RemainingMilliseconds(long nowMilliseconds)
    {
        if (!IsOpen) return 0;
        var remaining = _deadlineMilliseconds - nowMilliseconds;
        return remaining > 0 ? remaining : 0;
    }

    /// <summary>
    /// 记一次输入事件。返回 true 表示**应当发布**（<paramref name="suppressedDuplicates"/> 是
    /// 自上次发布以来被抑制掉的同键事件数）；返回 false 表示落在抑制窗口内，只累加计数。
    /// </summary>
    public bool TryObserve(string dedupeKey, long nowMilliseconds, out int suppressedDuplicates)
    {
        suppressedDuplicates = 0;

        if (_lastByKey.TryGetValue(dedupeKey, out var last))
        {
            if (nowMilliseconds - last.LastMillisecondsAtPublish < (long)DuplicateWindow.TotalMilliseconds)
            {
                // 抑制窗口内：只累加计数，不逐次建事件（防点风暴）。
                _lastByKey[dedupeKey] = last with { Suppressed = last.Suppressed + 1 };
                return false;
            }

            suppressedDuplicates = last.Suppressed;
            _lastByKey[dedupeKey] = new DedupeEntry(nowMilliseconds, 0);
            return true;
        }

        if (_lastByKey.Count >= MaxDedupeEntries) _lastByKey.Clear();   // 有界
        _lastByKey[dedupeKey] = new DedupeEntry(nowMilliseconds, 0);
        return true;
    }

    private readonly record struct DedupeEntry(long LastMillisecondsAtPublish, int Suppressed);
}