using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using PCMig.Diagnostics.Abstractions.Serialization;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D6.3 §14（审计 P2-2）赤灯回归：Deep Trace 生命周期的两件事必须收口。
///
///   ① **时钟**：时限与去重窗口只能读**单调**毫秒。收口前用的是 <c>DateTime.UtcNow</c>——
///      那是墙上时钟，NTP 校时/夏令时/手工改表都能拨动它：
///         · 回拨 ⇒ "限时 60 秒"变成假的（窗口被无限延长）；
///         · 前跳 ⇒ 观测**悄悄**提前结束，而事件里没有任何痕迹。
///   ② **停机出口**：停止必须走**唯一**一条权威路径，并且**必须带理由**——
///      否则"到期自动停"和"用户关掉了"在证据里长得一模一样。
///
/// 这两条都能脱离 WinUI 直接行为验证：策略在契约层（<see cref="InputObservationWindow"/>），
/// 适配器只做"取事实 → 调策略"。
/// </summary>
public sealed class D63DeepTraceLifecycleTests
{
    // ───────────────── Fixture 9：时限只认单调时钟，改表既不能延长也不能提前 ─────────────────

    [Fact]
    public void Fixture9_DeadlineIsMonotonicSoAWallClockJumpCannotExtendOrShortenIt()
    {
        var window = new InputObservationWindow();
        Assert.True(window.Start(nowMilliseconds: 1_000, InputObservationWindow.DefaultMaxDuration));

        // 时限就是"过了 60000 毫秒"——与墙上时钟无关。
        Assert.False(window.IsExpired(1_000));
        Assert.False(window.IsExpired(60_999));
        Assert.Equal(1_000, window.RemainingMilliseconds(60_000));
        Assert.True(window.IsExpired(61_000));
        Assert.Equal(0, window.RemainingMilliseconds(61_000));      // 绝不返回负数

        // 同一条时间线上，**收口前的判据**（墙上时钟）会怎样：
        //   开窗时刻 20:00:00，截止 20:01:00；开窗 30 秒后 NTP 把钟**回拨** 10 分钟。
        var wallStart = new DateTimeOffset(2026, 10, 1, 20, 0, 0, TimeSpan.FromHours(8));
        var wallDeadline = wallStart + InputObservationWindow.DefaultMaxDuration;
        var wallNow = wallStart + TimeSpan.FromSeconds(30) - TimeSpan.FromMinutes(10);

        // 单调时钟走到 60 秒时，旧的墙上判据仍然认为"还没到期"（还差 10 分钟）
        // ⇒ 观测会被继续 10 分钟，"限时 60 秒"这句话是假的。
        Assert.True(wallNow < wallDeadline,
            "构造失败：回拨后墙上时钟应仍早于旧截止点（那才是审计说的 bug）");
        // 而单调判据在同一时刻给出唯一答案。
        Assert.True(window.IsExpired(61_000), "单调时钟到点就必须到期，不受任何改表影响");

        // 前跳同样不得让它提前结束：判据只看"过了多久"，没有墙钟输入。
        Assert.False(window.IsExpired(60_999));

        // 关窗后"到期"不再成立（停止必须有明确出口，而不是靠时间到了猜）。
        window.Close();
        Assert.False(window.IsExpired(1_000_000));
        Assert.Equal(0, window.RemainingMilliseconds(1_000_000));
        Assert.False(window.Start(2_000, TimeSpan.Zero), "零时长不得被当作『已开窗』");
        Assert.False(window.Start(2_000, TimeSpan.FromMilliseconds(-5)));
    }

    // ───────────────── Fixture 10：重复抑制也读单调时钟，且字典有界 ─────────────────

    [Fact]
    public void Fixture10_DuplicateSuppressionUsesMonotonicDeltasAndStaysBounded()
    {
        var window = new InputObservationWindow();
        Assert.True(window.Start(0, InputObservationWindow.DefaultMaxDuration));

        const string key = "Step1.Connect|pointer-pressed";
        var duplicateWindowMs = (long)InputObservationWindow.DuplicateWindow.TotalMilliseconds;

        // 首次：必须发布。
        Assert.True(window.TryObserve(key, 1_000, out var first));
        Assert.Equal(0, first);

        // 抑制窗口内：只累加，不逐次建事件。
        Assert.False(window.TryObserve(key, 1_000 + duplicateWindowMs - 1, out _));
        Assert.False(window.TryObserve(key, 1_000 + duplicateWindowMs - 1, out _));

        // 越过抑制窗口：发布，并如实带上"被抑制了几条"。
        Assert.True(window.TryObserve(key, 1_000 + duplicateWindowMs, out var suppressed));
        Assert.Equal(2, suppressed);

        // 边界再次精确判定（用的是单调差值，不是墙钟日期）。
        Assert.False(window.TryObserve(key, 1_000 + 2 * duplicateWindowMs - 1, out _));
        Assert.True(window.TryObserve(key, 1_000 + 2 * duplicateWindowMs, out var suppressed2));
        Assert.Equal(1, suppressed2);

        // 字典有界：条目数绝不随会话长度增长（超限即清空重来）。
        var clock = 10_000_000L;
        for (var i = 0; i < InputObservationWindow.MaxDedupeEntries * 2; i++)
            window.TryObserve("Control" + i + "|tapped", clock += 1_000, out _);

        Assert.True(window.TrackedKeyCount <= InputObservationWindow.MaxDedupeEntries,
            "去重字典没有守住上界：" + window.TrackedKeyCount);
    }

    // ───────────────── Fixture 11：停机理由必须进证据，且未知理由不许编 ─────────────────

    [Fact]
    public void Fixture11_StopReasonIsWrittenIntoEvidenceAndUnknownReasonsAreNotInvented()
    {
        foreach (var reason in new[]
                 {
                     DeepTraceStopReasons.DeadlineReached,
                     DeepTraceStopReasons.UserDisabled,
                     DeepTraceStopReasons.WindowClosing,
                     DeepTraceStopReasons.Restarted,
                 })
        {
            var line = DiagnosticEventJson.ToJsonLine(D2TestSupport.Event(
                sequence: 1,
                descriptor: UiEvents.InputObserved,
                payload: new UiInputObservedPayload(InputCategories.LifecycleStopped, true, true, 7, 0)
                {
                    ReasonCode = DeepTraceStopReasons.Normalize(reason),
                }));

            Assert.Contains("\"inputKind\":\"" + InputCategories.LifecycleStopped + "\"", line, StringComparison.Ordinal);
            Assert.Contains("\"reasonCode\":\"" + reason + "\"", line, StringComparison.Ordinal);

            // 解析回来仍是同一条理由（导出后的证据必须还能回答"为什么停了"）。
            Assert.True(DiagnosticEventJson.TryParse(line, out var parsed, out var error), error);
            Assert.NotNull(parsed);
            Assert.Contains("\"reasonCode\":\"" + reason + "\"", DiagnosticEventJson.ToJsonLine(parsed!),
                StringComparison.Ordinal);
        }

        // 普通输入事件没有理由可言 ⇒ 该字段根本不该出现（空值不写进 JSONL，不污染证据）。
        var inputLine = DiagnosticEventJson.ToJsonLine(D2TestSupport.Event(
            sequence: 2,
            descriptor: UiEvents.InputObserved,
            payload: new UiInputObservedPayload(InputCategories.KeyTab, true, true, 0, 0)));
        Assert.Contains("\"inputKind\":\"" + InputCategories.KeyTab + "\"", inputLine, StringComparison.Ordinal);
        Assert.DoesNotContain("reasonCode", inputLine, StringComparison.Ordinal);

        // 未知理由绝不原样写进事件（自由文本可能夹带身份），一律如实降级。
        Assert.Equal(DeepTraceStopReasons.Unspecified, DeepTraceStopReasons.Normalize("whatever-i-typed"));
        Assert.Equal(DeepTraceStopReasons.Unspecified, DeepTraceStopReasons.Normalize(null));
        Assert.False(DeepTraceStopReasons.IsKnown("whatever-i-typed"));
        foreach (var reason in DeepTraceStopReasons.All) Assert.True(DeepTraceStopReasons.IsKnown(reason));
        Assert.Contains(DeepTraceStopReasons.Unspecified, DeepTraceStopReasons.All);
    }

    // ───────────────── Fixture 11b：源码化石契约（唯一出口 + 没有墙上时钟） ─────────────────

    [Fact]
    public void Fixture11b_ObserverHasASingleReasonedStopPathAndNoWallClock()
    {
        var observerPath = Path.Combine(RepoRoot(), "src", "PCMig.WinUI", "Diagnostics", "DeepTraceInputObserver.cs");
        Assert.True(File.Exists(observerPath), "缺少 " + observerPath);
        var observer = File.ReadAllText(observerPath);

        // ① 唯一停机出口，且**必须**给理由（没有无参 Stop() 可走后门）。
        Assert.Contains("public void Stop(string reasonCode)", observer, StringComparison.Ordinal);
        Assert.DoesNotContain("public void Stop()", observer, StringComparison.Ordinal);

        // ② 时限与去重都交给契约层的窗口（判决只读单调时钟）。
        Assert.Contains("InputObservationWindow", observer, StringComparison.Ordinal);
        Assert.Contains("_window.IsExpired(", observer, StringComparison.Ordinal);
        Assert.Contains("_window.TryObserve(", observer, StringComparison.Ordinal);

        // ③ 墙上时钟在这个生命周期里必须彻底消失（这正是审计 P2-2 的化石）。
        Assert.DoesNotContain("DateTime.UtcNow", observer, StringComparison.Ordinal);
        Assert.DoesNotContain("_deadlineUtc", observer, StringComparison.Ordinal);
        Assert.DoesNotContain("_lastByControl", observer, StringComparison.Ordinal);
        Assert.Contains("IMonotonicClock", observer, StringComparison.Ordinal);
        Assert.Contains("StopwatchMonotonicClock.Shared", observer, StringComparison.Ordinal);

        // ④ WinUI 侧每一处停机调用都必须带上理由码（而不是"静默 Stop()"）。
        var callSites = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "PCMig.WinUI"),
                     "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                              && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
        {
            foreach (var line in File.ReadAllLines(file))
                if (line.Contains("_deepTraceObserver", StringComparison.Ordinal)
                    && line.Contains(".Stop(", StringComparison.Ordinal))
                    callSites.Add(Path.GetFileName(file) + ": " + line.Trim());
        }

        Assert.True(callSites.Count >= 3, "预期至少 3 处停机调用（关窗/用户关闭/运行期不可用），实际：" + callSites.Count);
        foreach (var site in callSites)
            Assert.Contains("Stop(DeepTraceStopReasons.", site, StringComparison.Ordinal);

        // 到期自动停也必须带理由（"到期"与"用户关了"在证据里必须能分开）。
        Assert.Contains("Stop(DeepTraceStopReasons.DeadlineReached)", observer, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
        throw new InvalidOperationException("找不到仓库根：" + AppContext.BaseDirectory);
    }
}