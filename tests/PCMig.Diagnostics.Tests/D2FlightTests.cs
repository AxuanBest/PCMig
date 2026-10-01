using System;
using System.IO;
using System.Linq;
using System.Threading;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D2 契约：Flight Recorder。
/// 重点验"诚实边界"：环是有界容量（不是"前 N 秒"承诺）、触发会合并/限流、
/// 封存时会如实写下**实际**覆盖与丢失，而不是假装保住了固定秒数。
/// </summary>
public sealed class D2FlightTests
{
    private static FlightRecorder NewRecorder(
        out DiagnosticSessionStore store,
        out string root,
        Action<DiagnosticRuntimeOptions>? configure = null)
    {
        root = D2TestSupport.NewTempRoot();
        store = new DiagnosticSessionStore(root, TestEvents.FixedSessionId);
        store.EnsureCreated();
        var options = D2TestSupport.Options(root);
        options.RingEventCapacity = 16;
        options.RingByteBudget = 64 * 1024;
        options.FlightPostWindowMs = 50;
        options.FlightTriggerCooldownMs = 1;
        configure?.Invoke(options);
        return new FlightRecorder(store, options, new DiagnosticHealth(), new LossLedger());
    }

    [Fact]
    public void RingIsBoundedByCapacityAndCountsOverwrites()
    {
        var recorder = NewRecorder(out _, out var root);
        try
        {
            for (var i = 1; i <= 100; i++) recorder.Observe(D2TestSupport.Event(i));

            var stats = recorder.Stats();
            Assert.True(stats.RingEvents <= 16, $"环容量 16，实际 {stats.RingEvents}");
            Assert.True(stats.OverwrittenEvents >= 84, "被覆盖的旧事件必须计数（不能假装还在）");
            Assert.Equal(100, stats.ObservedEvents);
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    [Fact]
    public void TriggerFreezesPreWindowThenPostWindowCloses()
    {
        var recorder = NewRecorder(out var store, out var root);
        try
        {
            for (var i = 1; i <= 10; i++) recorder.Observe(D2TestSupport.Event(i));

            var outcome = recorder.Trigger("rule:TEST_TRIGGER", "test");
            Assert.True(outcome.Accepted);
            Assert.NotNull(outcome.TriggerId);

            for (var i = 11; i <= 20; i++) recorder.Observe(D2TestSupport.Event(i));

            // post 窗口到期后 Tick 封存并落盘。
            Thread.Sleep(80);
            recorder.Tick(checkpointEnabled: false);

            var stats = recorder.Stats();
            Assert.Equal(1, stats.SealedWindows);
            Assert.Equal(1, stats.PersistedWindows);
            Assert.Equal(0, stats.FailedWindows);

            var windowFile = Directory.GetFiles(store.FlightDir, "window-*.jsonl").Single();
            var lines = File.ReadAllLines(windowFile).Where(l => l.Length > 0).ToArray();
            Assert.Equal(20, lines.Length); // 10 条 pre + 10 条 post

            Assert.True(File.Exists(windowFile + ".manifest.json"), "Flight 窗口也要有清单");
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    [Fact]
    public void NearbyTriggersAreMergedInsteadOfPinningManyWindows()
    {
        var recorder = NewRecorder(out _, out var root, o => o.FlightTriggerCooldownMs = 60_000);
        try
        {
            recorder.Observe(D2TestSupport.Event(1));
            var first = recorder.Trigger("rule:A", "test");
            var second = recorder.Trigger("rule:B", "test");
            var third = recorder.Trigger("rule:C", "test");

            Assert.True(first.Accepted);
            Assert.False(second.Accepted);
            Assert.True(second.Merged, "冷却期内的触发必须被合并，而不是再冻结一个窗口");
            Assert.Equal(first.TriggerId, second.TriggerId);
            Assert.False(third.Accepted);
            Assert.Equal(2, recorder.Stats().MergedTriggers);
            Assert.Equal(0, recorder.Stats().RejectedTriggers);
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    [Fact]
    public void TriggerQuotaIsEnforcedAndReported()
    {
        var recorder = NewRecorder(out _, out var root, o =>
        {
            o.FlightMaxPinnedWindows = 1;
            o.FlightTriggerCooldownMs = 1;
            o.FlightPostWindowMs = 60_000; // 让第一个窗口一直开着
        });
        try
        {
            recorder.Observe(D2TestSupport.Event(1));
            Assert.True(recorder.Trigger("rule:A", "test").Accepted);

            var second = recorder.Trigger("rule:B", "test");
            Assert.False(second.Accepted);
            Assert.False(second.Merged);
            Assert.Equal("trigger-quota-exceeded", second.ReasonCode);
            Assert.Equal(1, recorder.Stats().RejectedTriggers);
            Assert.Equal(1, recorder.Stats().OpenWindows);
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    [Fact]
    public void ShutdownSealsOpenWindowsAsIncomplete()
    {
        var recorder = NewRecorder(out var store, out var root, o => o.FlightPostWindowMs = 60_000);
        try
        {
            recorder.Observe(D2TestSupport.Event(1));
            recorder.Trigger("rule:A", "test");
            recorder.SealOpenWindows();

            var stats = recorder.Stats();
            Assert.Equal(1, stats.SealedWindows);
            Assert.Equal(0, stats.OpenWindows);

            var windowFile = Directory.GetFiles(store.FlightDir, "window-*.jsonl").Single();
            var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(windowFile + ".manifest.json"));
            Assert.True(manifest.RootElement.GetProperty("partial").GetBoolean(), "关闭时封存的窗口必须标 partial");
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    [Fact]
    public void CheckpointIsWrittenAndStopsAtItsByteBudget()
    {
        var recorder = NewRecorder(out var store, out var root, o =>
        {
            o.CheckpointMaxBytes = 512;   // 极小预算 ⇒ 很快停止，且必须记账
            o.CheckpointIntervalMs = 1;
        });
        try
        {
            for (var i = 1; i <= 20; i++) recorder.Observe(D2TestSupport.Event(i));
            recorder.Tick(checkpointEnabled: true);

            var checkpointFile = Path.Combine(store.FlightDir, "checkpoint-active.jsonl");
            Assert.True(File.Exists(checkpointFile), "启用 checkpoint 时必须真的写出检查点文件");
            Assert.True(recorder.Stats().CheckpointBytes > 0);

            // 再灌一批，触发预算耗尽路径。
            for (var i = 21; i <= 40; i++) recorder.Observe(D2TestSupport.Event(i));
            recorder.Tick(checkpointEnabled: true);
            Assert.True(recorder.Stats().CheckpointBytes >= 512);

            // 预算耗尽后不再增长（不无限写盘），并且这里必须没有抛异常。
            var bytesAfter = recorder.Stats().CheckpointBytes;
            for (var i = 41; i <= 60; i++) recorder.Observe(D2TestSupport.Event(i));
            recorder.Tick(checkpointEnabled: true);
            Assert.Equal(bytesAfter, recorder.Stats().CheckpointBytes);
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    [Fact]
    public void OversizedEventIsDroppedAndCountedInsteadOfFlushingTheRing()
    {
        var recorder = NewRecorder(out _, out var root, o => o.RingByteBudget = 1_024);
        try
        {
            recorder.Observe(D2TestSupport.Event(1));
            var before = recorder.Stats().RingEvents;

            recorder.Observe(D2TestSupport.Event(2, message: new string('x', 5_000)));

            var stats = recorder.Stats();
            Assert.Equal(1, stats.DroppedEvents);
            Assert.Equal(before, stats.RingEvents);
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }
}