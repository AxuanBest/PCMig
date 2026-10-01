using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using PCMig.Diagnostics;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using PCMig.Diagnostics.Analysis;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D6.3 §9（审计 P2-1）的**红灯 Fixture 7/8**：保留策略与真实丢失必须**只有一份账**。
///
/// 收口前的自相矛盾：同一件"内存环按容量覆盖"，
///   · <see cref="EvidenceCoverage"/> 说：保留策略不是丢失，证据仍然完整；
///   · 而它经 <c>LossLedger.TotalEvicted</c> 灌进健康快照的 <c>EventsEvicted</c>，
///     于是 <c>IsDegraded = true</c>、<c>EvidenceComplete = false</c>。
/// 一个诚实性收口如果自己有两份互相矛盾的账，那它说的"完整/健康"就不可信。
///
/// 现在：<c>RetentionEvictions</c> 可见但不降级；<c>EventsEvicted</c> 只统计真的被合并/挤掉的条目。
/// </summary>
public sealed class D63RetentionAccountingTests : IDisposable
{
    private readonly string _root = D2TestSupport.NewTempRoot();

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* 句柄延迟释放 */ }
    }

    // ───────────────── Fixture 7：环覆盖 = 保留策略（可见，但不叫"证据不完整"）─────────────────

    /// <summary>
    /// 环容量只有 1：第二条**必然**覆盖第一条（连 DurableCritical 也覆盖）。
    /// 保留要看得见（<c>RetentionEvictions</c>），但**不得**被读成"证据没了"或"系统坏了"。
    /// </summary>
    [Fact]
    public void Fixture7_RingRetentionIsVisibleButNeverReadsAsIncompleteOrDegraded()
    {
        var store = new DiagnosticSessionStore(Path.Combine(_root, "flight"), Guid.NewGuid());
        store.EnsureCreated();

        var ledger = new LossLedger();
        var coverage = new EvidenceCoverage(ledger);
        var health = new DiagnosticHealth();
        var options = new DiagnosticRuntimeOptions { RingEventCapacity = 1, RingByteBudget = 4096 };
        var flight = new FlightRecorder(store, options, health, ledger);

        flight.Observe(Event(DeliveryClass.DurableCritical, 1));
        flight.Observe(Event(DeliveryClass.DurableCritical, 2));
        flight.Observe(Event(DeliveryClass.Operational, 3));

        var snapshot = DiagnosticHealthSnapshot.Capture(health, ledger.Snapshot());

        // ① 保留必须可见（不许"悄悄覆盖"）。
        Assert.True(snapshot.RetentionEvictions > 0,
            $"环覆盖没有留下任何痕迹：retention={snapshot.RetentionEvictions}");

        // ② 但它不是丢失：既不进 EventsEvicted/EventsDropped，也不推进世代、不计 CriticalLost。
        Assert.Equal(0, snapshot.EventsEvicted);
        Assert.Equal(0, snapshot.EventsDropped);
        Assert.Equal(0, snapshot.LossEpoch);
        Assert.Equal(0, snapshot.CriticalLost);
        Assert.False(snapshot.StickyCriticalLost);

        // ③ 于是"健康/完整"这两句话不再自相矛盾。
        Assert.False(snapshot.IsDegraded, "保留策略不该把一次正常会话读成降级");
        Assert.True(snapshot.EvidenceComplete,
            $"保留策略被算成了证据丢失：evicted={snapshot.EventsEvicted} retention={snapshot.RetentionEvictions}");

        // ④ 与 EvidenceCoverage 的口径**一致**（这正是审计 P2-1 要求的"一份账"）。
        Assert.True(coverage.IsCompleteFor(DeliveryClass.Operational));
        Assert.True(coverage.IsCompleteFor(DeliveryClass.DurableCritical));
        Assert.False(coverage.HasFaultLoss);
        Assert.Equal(coverage.RetentionEvictions, snapshot.RetentionEvictions);

        // ⑤ 记录仍然如实标着"这是保留性淘汰、不是故障"。
        var record = ledger.Snapshot().Records.Single(r => r.ReasonCode == "ring-overwritten");
        Assert.Equal(LossKind.FlightOverwrite, record.Kind);
        Assert.False(record.IsFault);
    }

    // ───────────────── Fixture 8：真的被挤掉 = 证据不完整（必须降级）─────────────────

    /// <summary>
    /// 队列 DropOldest 合并（<c>evicted: true</c> 且不是环覆盖）**是**真实证据不完整：
    /// 条目已经被接受又被挤掉 ⇒ 必须降级、必须说证据不完整，且**不得**混进保留计数。
    /// </summary>
    [Fact]
    public void Fixture8_CoalescedEvictionIsRealEvidenceIncompletenessAndSaysSo()
    {
        var ledger = new LossLedger();
        var coverage = new EvidenceCoverage(ledger);
        var health = new DiagnosticHealth();

        ledger.RecordDrop(DiagnosticBranches.Writer, DeliveryClass.Operational, 5, "queue-full-drop-oldest", evicted: true);

        var snapshot = DiagnosticHealthSnapshot.Capture(health, ledger.Snapshot());

        Assert.Equal(1, snapshot.EventsEvicted);              // 真的少了一条
        Assert.Equal(0, snapshot.EventsDropped);
        Assert.Equal(0, snapshot.RetentionEvictions);         // 这不是保留策略，不许冒充
        Assert.Equal(1, snapshot.LossEpoch);
        Assert.True(snapshot.IsDegraded, "条目真的被挤掉了，却仍宣称健康");
        Assert.False(snapshot.EvidenceComplete, "条目真的被挤掉了，却仍宣称证据完整");

        // 与 EvidenceCoverage 同口径：Operational 档证据不完整、并且是"货真价实的损失"。
        Assert.False(coverage.IsCompleteFor(DeliveryClass.Operational));
        Assert.True(coverage.HasFaultLoss || coverage.IsCompleteFor(DeliveryClass.Operational) == false);
        Assert.Equal(0, coverage.RetentionEvictions);
    }

    // ───────────────── 真实运行时：小环跑真事件，两份账各归各位 ─────────────────

    /// <summary>
    /// 端到端版：真实 <see cref="DiagnosticRuntime"/> + 环容量 1 + 一批真实投递的事件。
    /// 环必然覆盖若干条 ⇒ <c>RetentionEvictions &gt; 0</c>，而会话本身**仍然**是
    /// Healthy + EvidenceComplete（环覆盖只说明"环只保最近一段"）。
    /// </summary>
    [Fact]
    public void Runtime_TinyRingDoesNotReadAsBrokenSession()
    {
        var runtime = DiagnosticRuntime.Start(new DiagnosticRuntimeOptions
        {
            StorageRoot = Path.Combine(_root, "runtime", "Diagnostics"),
            InitialMode = CaptureMode.Operational,
            AppVersion = "d63-test",
            ShutdownBudgetMs = 5000,
            RingEventCapacity = 1,
            RingByteBudget = 64 * 1024,
        }, out _);
        Assert.NotNull(runtime.Store);

        try
        {
            for (var i = 0; i < 12; i++) Publish(runtime, "e" + i);

            Assert.True(D2TestSupport.WaitUntil(() => runtime.Loss.RetentionEvictions > 0, 10_000),
                $"环容量 1 却没有任何保留性覆盖：retention={runtime.Loss.RetentionEvictions}");

            var health = runtime.GetHealthSnapshot();
            Assert.True(health.RetentionEvictions > 0);
            Assert.Equal(0, health.EventsEvicted);
            Assert.Equal(0, health.EventsDropped);
            Assert.False(health.IsDegraded, $"只剩保留性覆盖，却被读成降级：retention={health.RetentionEvictions}");
            Assert.True(health.EvidenceComplete,
                $"只剩保留性覆盖，却被读成证据不完整：evicted={health.EventsEvicted} retention={health.RetentionEvictions}");

            // 覆盖率口径必须与健康口径给出同一句话。
            Assert.Equal(runtime.Loss.RetentionEvictions, health.RetentionEvictions);
            Assert.True(new EvidenceCoverage(runtime.Loss).IsCompleteFor(DeliveryClass.Operational));
        }
        finally
        {
            D2TestSupport.Shutdown(runtime);
            D2TestSupport.Dispose(runtime);
        }
    }

    // ───────────────────────────────── 助手 ─────────────────────────────────

    private static void Publish(DiagnosticRuntime runtime, string reason)
        => runtime.Publisher.TryPublish(new DiagnosticEventDraft(
            PersistenceEvents.WriteFailed,
            DiagnosticContext.Root(runtime.SessionId, "D63").WithControl("Step1.Connect"),
            new PstWritePayload("Receipt", "Move", true, reason),
            Outcome: DiagnosticOutcome.Failed,
            ErrorDomain: ErrorDomain.Managed,
            ExceptionType: "IOException",
            Message: "d63-retention"));

    private static DiagnosticEvent Event(DeliveryClass delivery, long sequence) => new()
    {
        SchemaVersion = DiagnosticEvent.CurrentSchemaVersion,
        Descriptor = PersistenceEvents.WriteFailed,
        SessionId = Guid.NewGuid(),
        Sequence = sequence,
        TimestampUtc = DateTimeOffset.UtcNow,
        MonotonicTimestamp = Stopwatch.GetTimestamp(),
        Level = DiagnosticLevel.Error,
        Delivery = delivery,
        EvidenceQuality = EvidenceQuality.Direct,
        CaptureMode = CaptureMode.Operational,
        Payload = new PstWritePayload("Receipt", "Move", true, "IOException"),
    };
}