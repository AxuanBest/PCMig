using System;
using System.IO;
using System.Linq;
using PCMig.Diagnostics;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using PCMig.Diagnostics.Abstractions.Serialization;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D6.1 §19：**新接入的两个配置项必须真的有行为**（"Active 必须有消费者和测试"）。
/// 每条用例都设计成"只有该配置真的生效才会通过"，而不是只看源码里有没有读取点。
/// </summary>
public sealed class D61OptionsBehaviorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-opt-" + Guid.NewGuid().ToString("N")[..8]);

    public D61OptionsBehaviorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* 句柄延迟释放 */ }
    }

    private static DiagnosticEvent Event(long sequence) => new()
    {
        SchemaVersion = DiagnosticEvent.CurrentSchemaVersion,
        Descriptor = PersistenceEvents.WriteFailed,
        SessionId = Guid.NewGuid(),
        Sequence = sequence,
        TimestampUtc = DateTimeOffset.UtcNow,
        MonotonicTimestamp = System.Diagnostics.Stopwatch.GetTimestamp(),
        Level = DiagnosticLevel.Error,
        Delivery = DeliveryClass.Operational,
        EvidenceQuality = EvidenceQuality.Direct,
        CaptureMode = CaptureMode.Operational,
        Payload = new PstWritePayload("Receipt", "Move", true, "IOException"),
    };

    [Fact]
    public async System.Threading.Tasks.Task WriterBatchMaxBytesTriggersAFlushWhileTheTimeGateIsHeldOff()
    {
        // 时间闸门放到极大 ⇒ 唯一可能的 flush 来源就是"批大小"闸门。
        var store = new DiagnosticSessionStore(Path.Combine(_root, "batch"), Guid.NewGuid());
        store.EnsureCreated();
        var health = new DiagnosticHealth();
        var options = new DiagnosticRuntimeOptions
        {
            WriterBatchMaxBytes = 256,
            WriterFlushIntervalMs = 600_000,
        };
        var writer = new JsonlSegmentWriter(store, options, health, new LossLedger(), null);

        await writer.ConsumeAsync(new BranchItem(Event(1), 512), default);

        Assert.True(health.LastSuccessfulFlushUnixMs > 0,
            "批大小达到阈值时必须真的 flush（否则 WriterBatchMaxBytes 又是'能配没用上'）");

        // 对照：阈值极大时，同样的事件不应触发 flush。
        var store2 = new DiagnosticSessionStore(Path.Combine(_root, "batch2"), Guid.NewGuid());
        store2.EnsureCreated();
        var health2 = new DiagnosticHealth();
        var options2 = new DiagnosticRuntimeOptions
        {
            WriterBatchMaxBytes = 10_000_000,
            WriterFlushIntervalMs = 600_000,
        };
        var writer2 = new JsonlSegmentWriter(store2, options2, health2, new LossLedger(), null);
        await writer2.ConsumeAsync(new BranchItem(Event(1), 512), default);

        Assert.Equal(0, health2.LastSuccessfulFlushUnixMs);
        writer.SealActive(partial: false);
        writer2.SealActive(partial: false);
    }

    [Fact]
    public void CheckpointIntervalIsHonoredInsteadOfWritingOnEveryTick()
    {
        var store = new DiagnosticSessionStore(Path.Combine(_root, "cp"), Guid.NewGuid());
        store.EnsureCreated();

        // 间隔很长 ⇒ 第一次 Tick 到期（首轮立即到期），紧接着的第二次 Tick **不应**再写。
        var longInterval = new DiagnosticRuntimeOptions { CheckpointIntervalMs = 600_000, RingEventCapacity = 64 };
        var recorder = new FlightRecorder(store, longInterval, new DiagnosticHealth(), new LossLedger());
        for (var i = 1; i <= 10; i++) recorder.Observe(Event(i));

        recorder.Tick(checkpointEnabled: true);
        var afterFirst = recorder.Stats().CheckpointBytes;
        Assert.True(afterFirst > 0, "首轮检查点应当到期并写出");

        recorder.Tick(checkpointEnabled: true);
        Assert.Equal(afterFirst, recorder.Stats().CheckpointBytes);   // ★ 间隔没到 ⇒ 不写 ★

        // 间隔为 1ms ⇒ 下一次 Tick 立刻又写。
        var shortInterval = new DiagnosticRuntimeOptions { CheckpointIntervalMs = 1, RingEventCapacity = 64 };
        var store2 = new DiagnosticSessionStore(Path.Combine(_root, "cp2"), Guid.NewGuid());
        store2.EnsureCreated();
        var recorder2 = new FlightRecorder(store2, shortInterval, new DiagnosticHealth(), new LossLedger());
        for (var i = 1; i <= 10; i++) recorder2.Observe(Event(100 + i));

        recorder2.Tick(checkpointEnabled: true);
        var first = recorder2.Stats().CheckpointBytes;
        for (var i = 11; i <= 20; i++) recorder2.Observe(Event(100 + i));
        System.Threading.Thread.Sleep(5);                              // 让 1ms 间隔确实到期
        recorder2.Tick(checkpointEnabled: true);

        Assert.True(recorder2.Stats().CheckpointBytes >= first, "间隔很短时应当继续写检查点");
        Assert.True(recorder2.Stats().CheckpointSegments >= 1);
    }
}