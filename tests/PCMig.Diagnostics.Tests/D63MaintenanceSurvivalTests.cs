using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using PCMig.Diagnostics.Analysis.Feedback;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// ★ D6.3 §7「维护存活」★ 审计 P1-2-③ 的红灯：
///
/// `PendingExpectationTracker` 的 <c>_pending</c> 原来是**裸 Dictionary**：
///   · <see cref="PendingExpectationTracker.Observe"/> 跑在 analyzer 消费线程上（增删条目）；
///   · <see cref="PendingExpectationTracker.Tick"/> 跑在维护定时器线程上（遍历同一个字典）；
///   · 收尾时关停路径还会调 <see cref="PendingExpectationTracker.CloseAll"/>。
/// 并发写字典会撕裂它的内部状态 —— 之后每一次 `foreach` 都可能自旋不返回，
/// **整个维护循环就此被拖死**：此后不再有到期判定、不再有刷盘，
/// 而快照却仍会报 Healthy + EvidenceComplete（正是审计抓到的那句谎话）。
///
/// 本测试是**存活测试**：四条线程同时 Observe（开 + 关）、一条线程同时 Tick 并读 Stats，
/// 用看门狗判定"有没有卡死"，再核对计数必须自洽（记账不能被并发撕开）。
/// </summary>
public sealed class D63MaintenanceSurvivalTests
{
    private static long TicksFromMs(double ms) => (long)(ms / 1000.0 * Stopwatch.Frequency);

    [Fact]
    public async Task Fixture2c_ConcurrentObserveTickAndCloseCannotWedgeTheMaintenanceLoop()
    {
        var contracts = FeedbackContractRegistry.CreateDefault();
        var timeouts = new List<ExpectationTimeout>();
        var tracker = new PendingExpectationTracker(contracts, timeout =>
        {
            lock (timeouts) timeouts.Add(timeout);
        });

        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        long sequence = 0;
        var observed = 0;

        var workers = new Task[4];
        for (var w = 0; w < workers.Length; w++)
        {
            workers[w] = Task.Run(() =>
            {
                var local = 0;
                while (!stop.IsCancellationRequested)
                {
                    var actionId = Guid.NewGuid();

                    // 开一条期望 …
                    tracker.Observe(Observed(actionId, Interlocked.Increment(ref sequence)));
                    // … 同时用终止事件把它关掉（增删并发，正是裸字典会被撕开的动作）。
                    tracker.Observe(Terminal(actionId, Interlocked.Increment(ref sequence)));
                    local++;
                }

                Interlocked.Add(ref observed, local);
            });
        }

        var maintenance = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                // ★ 关键：让 Tick 的遍历与 Observe 的增删**真的同时发生**。
                //   把"现在"推到一小时后 ⇒ 每条待满足期望都立即到期，遍历路径每次都跑满。
                tracker.Tick(Stopwatch.GetTimestamp() + TicksFromMs(3_600_000), evidenceComplete: false, lossEpoch: 0, acceptanceWatermark: 0);
                _ = tracker.Stats();
                _ = tracker.PendingCount;
            }
        });

        var all = workers.Append(maintenance).ToArray();

        // 看门狗：卡死（旧实现在裸字典上自旋不返回）必须让这个用例失败，而不是把测试挂住。
        var watchdog = Task.Delay(TimeSpan.FromSeconds(30));
        var finished = await Task.WhenAny(Task.WhenAll(all), watchdog);
        Assert.True(finished != watchdog,
            $"并发 Observe/Tick/Stats 卡死了（维护循环会被拖死）：已完成={all.Count(t => t.IsCompleted)}/{all.Length} " +
            $"pending={tracker.PendingCount} begun={tracker.Stats().Begun}");

        Assert.True(Volatile.Read(ref observed) > 100, $"压力太小，说明不了问题：observed={Volatile.Read(ref observed)}");

        // 并发不得撕开记账：每条开出来的期望要么被终止事件关掉，要么还在待满足里。
        var stats = tracker.Stats();
        Assert.True(stats.Begun > 0, "一条期望都没开出来，用例没生效");
        Assert.True(stats.ClosedByTerminal > 0, $"终止事件没有关掉任何期望：terminal={stats.ClosedByTerminal} pending={stats.Pending}");
        Assert.Equal(stats.Begun, stats.ClosedByTerminal + stats.ClosedByRejection + stats.Pending);

        // 关停路径也要跑得动，并且不得留下残余。
        tracker.CloseAll("d63-test");
        Assert.Equal(0, tracker.PendingCount);
        Assert.Equal(0, tracker.Stats().Pending);
        Assert.True(timeouts.Count > 0, "Tick 与 Observe 并发时一条到期判定都没做出来");
    }

    // ───────────────────────────────── 事件构造 ─────────────────────────────────

    private static DiagnosticEvent Observed(Guid actionId, long sequence) => new()
    {
        SchemaVersion = DiagnosticEvent.CurrentSchemaVersion,
        Descriptor = UiEvents.UserActionObserved,
        SessionId = TestEvents.FixedSessionId,
        Sequence = sequence,
        TimestampUtc = new DateTimeOffset(2026, 10, 1, 6, 0, 0, TimeSpan.Zero).AddMilliseconds(sequence),
        MonotonicTimestamp = sequence,
        Level = DiagnosticLevel.Information,
        Delivery = DeliveryClass.Operational,
        EvidenceQuality = EvidenceQuality.Direct,
        CaptureMode = CaptureMode.Operational,
        ActionId = actionId,
        ControlId = "Step1.Connect",
        Payload = new UiActionPayload("Connect", "click"),
    };

    private static DiagnosticEvent Terminal(Guid actionId, long sequence) => new()
    {
        SchemaVersion = DiagnosticEvent.CurrentSchemaVersion,
        Descriptor = UiEvents.ActionCompleted,
        SessionId = TestEvents.FixedSessionId,
        Sequence = sequence,
        TimestampUtc = new DateTimeOffset(2026, 10, 1, 6, 0, 0, TimeSpan.Zero).AddMilliseconds(sequence),
        MonotonicTimestamp = sequence,
        Level = DiagnosticLevel.Information,
        Delivery = DeliveryClass.Operational,
        EvidenceQuality = EvidenceQuality.Direct,
        CaptureMode = CaptureMode.Operational,
        ActionId = actionId,
        Outcome = DiagnosticOutcome.Succeeded,
    };
}