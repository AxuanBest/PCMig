using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using PCMig.Diagnostics;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D6.1 §6 + §7（有界性）**收口测试**。对应验收清单：
///   §20.10 128 KiB / 1 MiB / 超长 string ⇒ 预算不得被绕过；
///   §20.11 FlightMaxPinnedWindows=1 ⇒ 实际驻留载荷有界；
///   （另含"错误风暴 100 次触发"的内存有界性。）
/// </summary>
public sealed class D61BoundednessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-d61b-" + Guid.NewGuid().ToString("N")[..8]);

    public D61BoundednessTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* 句柄延迟释放 */ }
    }

    /// <summary>可注入任意长度字符串的测试载荷（模拟"未来某个载荷夹带大字符串"）。</summary>
    private sealed record FatPayload(string Name, string Blob) : IDiagnosticPayload
    {
        public const string PayloadNameConst = "Fat";
        public string PayloadName => PayloadNameConst;
        public void WriteJson(System.Text.Json.Utf8JsonWriter w)
        {
            w.WriteString("name", Name);
            w.WriteString("blob", Blob);
        }
    }

    // ────────────────────────── §20.10 字节预算不得被绕过 ──────────────────────────

    [Fact]
    public void EstimatorNeverUndercountsAnyRegisteredPayload()
    {
        // 通用不变量：对**全部已登记载荷实例**，推导上界必须 ≥ 真实编码字节。
        // 这条测试是"上界可信"的地基：将来新增载荷若夹带大字符串，会在这里失败，
        // 而不是悄悄绕过字节预算。
        var instances = PayloadCodecTests.AllPayloadInstances().ToArray();
        Assert.NotEmpty(instances);

        foreach (var payload in instances)
        {
            var actual = Encoding.UTF8.GetByteCount(SerializeOnlyPayload(payload));
            var estimate = PayloadSizeEstimator.Estimate(payload);
            Assert.True(estimate >= actual,
                $"{payload.PayloadName}: 推导上界 {estimate} < 实际 {actual}（会绕过字节预算）");
        }
    }

    [Fact]
    public void HugePayloadsAreRejectedWithVisibleAccountingAndSmallOnesAreNot()
    {
        var runtime = DiagnosticRuntime.Start(new DiagnosticRuntimeOptions
        {
            StorageRoot = Path.Combine(_root, "budget"),
            InitialMode = CaptureMode.Operational,
            AppVersion = "d61-test",
            ShutdownBudgetMs = 5000,
            MaxEventBytes = 64 * 1024,
        }, out _);

        // ① 正常大小：接受，并且**上界 ≥ 实际**（保守估算）。
        var small = new FatPayload("small", new string('s', 1024));
        var accepted = runtime.Publisher.TryPublish(Draft(runtime, small));
        Assert.True(accepted);
        Assert.True(PayloadSizeEstimator.Estimate(small) >= Encoding.UTF8.GetByteCount(SerializeOnlyPayload(small)));

        // ② 128 KiB 与 1 MiB：**拒收**（不得绕过总预算）。
        foreach (var size in new[] { 128 * 1024, 1024 * 1024 })
        {
            var fat = new FatPayload("fat", new string('x', size));
            Assert.False(runtime.Publisher.TryPublish(Draft(runtime, fat)));
        }

        // ③ 拒收必须可见（健康计数 + 台账原因），不能静默。
        Assert.True(runtime.Health.PayloadRejected >= 2);
        Assert.Contains(runtime.Loss.Snapshot().Records, r => r.ReasonCode == "payload-too-large");

        D2TestSupport.Shutdown(runtime);
        D2TestSupport.Dispose(runtime);
    }

    [Fact]
    public void QueueByteBudgetStopsAcceptingInsteadOfGrowing()
    {
        // ★ 必须用**没有消费者**的分支来验证"队列字节预算" ★
        //   （运行时的管线是边写边消费的：队列预算限制的是**在队字节**，不是总吞吐。
        //    我第一版把它当成吞吐上限来断言，那是测试理解错误，不是实现缺陷。）
        var ledger = new LossLedger();
        var branch = new BoundedBranch(
            DiagnosticBranches.IngressOperational,
            capacity: 100_000,                       // 条数闸门放很宽
            byteBudget: 64 * 1024,                   // 只留字节闸门起作用
            evictsOldest: false,
            ledger,
            new DiagnosticHealth(),
            (_, _) => default);

        const int itemBytes = 4096;
        var accepted = 0;
        for (var i = 0; i < 500; i++)
            if (branch.TryAccept(new BranchItem(Event(i + 1), itemBytes)))
                accepted++;

        // 64 KiB / 4 KiB = 16 条：预算生效后必须开始拒收。
        Assert.True(accepted <= 16, $"字节预算没有起作用：接受了 {accepted} 条");
        Assert.True(branch.Depth >= 0);
        Assert.Contains(ledger.Snapshot().Records, r => r.ReasonCode == "byte-budget");

        // 而运行时的"单事件上限"仍然拦住超大载荷（另一条独立闸门）。
        var runtime = DiagnosticRuntime.Start(new DiagnosticRuntimeOptions
        {
            StorageRoot = Path.Combine(_root, "queuebudget"),
            InitialMode = CaptureMode.Operational,
            AppVersion = "d61-test",
            ShutdownBudgetMs = 5000,
        }, out _);
        Assert.False(runtime.Publisher.TryPublish(Draft(runtime, new FatPayload("q", new string('y', 128 * 1024)))));
        Assert.True(runtime.Health.PayloadRejected >= 1);
        D2TestSupport.Shutdown(runtime);
        D2TestSupport.Dispose(runtime);
    }

    // ────────────────────────── §20.11 Flight 真有界 ──────────────────────────

    [Fact]
    public void FinishedWindowsReleaseTheirPayloadAndStayWithinTheMetadataCap()
    {
        var store = new DiagnosticSessionStore(Path.Combine(_root, "flight"), Guid.NewGuid());
        store.EnsureCreated();

        var options = new DiagnosticRuntimeOptions
        {
            RingEventCapacity = 64,
            FlightMaxPinnedWindows = 1,
            FlightMaxFinishedWindows = 4,
            FlightPostWindowMs = 0,
            FlightTriggerCooldownMs = 0,
            FlightWindowPayloadByteBudget = 4 * 1024 * 1024,
        };
        var ledger = new LossLedger();
        var flight = new FlightRecorder(store, options, new DiagnosticHealth(), ledger);

        for (var i = 0; i < 40; i++) flight.Observe(Event(i + 1));

        var triggers = 0;
        for (var i = 0; i < 30; i++)
        {
            if (flight.Trigger("storm", "error-storm").Accepted) triggers++;
            flight.Tick(checkpointEnabled: false);
        }

        var stats = flight.Stats();

        Assert.True(triggers > 0, "至少应有一批触发被接受（其余按上限/冷却合并或拒绝）");
        // ★ 已落盘窗口的元数据有上限 ★
        Assert.True(stats.SealedWindows <= options.FlightMaxFinishedWindows,
            $"已封窗口元数据 {stats.SealedWindows} 超过上限 {options.FlightMaxFinishedWindows}");
        // ★ 落盘后不再长期持有完整事件列表 ★
        Assert.Equal(0, stats.RetainedWindowEvents);
        Assert.True(stats.ReleasedWindowPayloadBytes > 0);
        Assert.True(stats.TrimmedFinishedWindows > 0, "风暴下必须有元数据被淘汰（有界性证据）");
        Assert.True(stats.RetainedWindowPayloadBytes <= options.FlightWindowPayloadByteBudget);

        // 落盘窗口仍可被引用（轻量元数据保留：位置 + 计数）。
        Assert.Equal(stats.PersistedWindows, flight.PersistedWindowPaths().Count);
        Assert.All(flight.PersistedWindowPaths(), p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public void ErrorStormStopsOpeningWindowsWhenThePayloadBudgetIsSpent()
    {
        var store = new DiagnosticSessionStore(Path.Combine(_root, "storm"), Guid.NewGuid());
        store.EnsureCreated();

        var options = new DiagnosticRuntimeOptions
        {
            RingEventCapacity = 128,
            FlightMaxPinnedWindows = 1000,                  // 故意放开并发上限
            FlightMaxFinishedWindows = 1000,
            FlightPostWindowMs = 0,
            FlightTriggerCooldownMs = 0,
            FlightWindowPayloadByteBudget = 256 * 1024,     // 只留**载荷预算**起作用
        };
        var ledger = new LossLedger();
        var flight = new FlightRecorder(store, options, new DiagnosticHealth(), ledger);

        for (var i = 0; i < 200; i++) flight.Observe(Event(i + 1));

        // ★ 关键：**不要** Tick/Seal —— 窗口保持打开，载荷驻留内存，才能把"载荷预算"顶到上限。
        //   并且每次触发前**重新产生事件把环填满**（真实错误风暴就是"错误持续来"）；
        //   环交换后空环触发本身不再增长内存（那是更正确的结果，不是缺陷）。
        var accepted = 0;
        for (var i = 0; i < 100; i++)
        {
            for (var k = 0; k < 200; k++) flight.Observe(Event(1000 + i * 200 + k));
            if (flight.Trigger("storm", "error-storm-100").Accepted) accepted++;
        }

        var snapshot = ledger.Snapshot();
        Assert.Contains(snapshot.Records, r => r.ReasonCode == "window-payload-budget-exhausted");
        Assert.True(accepted < 100, "载荷预算必须阻止窗口无限增长");
        Assert.True(flight.Stats().RetainedWindowPayloadBytes <= options.FlightWindowPayloadByteBudget);
    }

    // ────────────────────────── helpers ──────────────────────────

    private static DiagnosticEventDraft Draft(DiagnosticRuntime runtime, IDiagnosticPayload payload) =>
        new(PersistenceEvents.WriteFailed,
            DiagnosticContext.Root(runtime.SessionId, "D61"),
            payload,
            Outcome: DiagnosticOutcome.Failed);

    private static DiagnosticEvent Event(long sequence) => new()
    {
        SchemaVersion = DiagnosticEvent.CurrentSchemaVersion,
        Descriptor = PersistenceEvents.WriteFailed,
        SessionId = Guid.NewGuid(),
        Sequence = sequence,
        TimestampUtc = DateTimeOffset.UtcNow,
        MonotonicTimestamp = Stopwatch.GetTimestamp(),
        Level = DiagnosticLevel.Error,
        Delivery = DeliveryClass.Operational,
        EvidenceQuality = EvidenceQuality.Direct,
        CaptureMode = CaptureMode.Operational,
        Payload = new PstWritePayload("Receipt", "Move", true, "IOException"),
    };

    /// <summary>只序列化 payload（用于"上界 ≥ 实际"的通用断言）。</summary>
    private static string SerializeOnlyPayload(IDiagnosticPayload payload)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            payload.WriteJson(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}