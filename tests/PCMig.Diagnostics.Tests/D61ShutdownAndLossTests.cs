using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PCMig.Diagnostics;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using PCMig.Diagnostics.Analysis;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D6.1 §3 + §4（Shutdown / clean marker / Loss 与证据语义）**收口测试**。对应验收清单：
///   §20.7 飞行环正常覆盖 ⇒ CriticalLost 必须保持 false；§20.8 真实丢失 ⇒ 对应档位证据降级；
///   §20.14 有排队事件时关闭 ⇒ 成功排空之后才写 clean marker；
///   §20.15 卡住 writer 的关闭 ⇒ 不得出现"假 clean-complete"。
/// </summary>
public sealed class D61ShutdownAndLossTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-d61s-" + Guid.NewGuid().ToString("N")[..8]);

    public D61ShutdownAndLossTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* 句柄延迟释放 */ }
    }

    private DiagnosticRuntime StartRuntime(string tag, int publish = 0)
    {
        var runtime = DiagnosticRuntime.Start(new DiagnosticRuntimeOptions
        {
            StorageRoot = Path.Combine(_root, tag),
            InitialMode = CaptureMode.Operational,
            AppVersion = "d61-test",
            ShutdownBudgetMs = 5000,
        }, out _);
        Assert.NotNull(runtime.Store);

        for (var i = 0; i < publish; i++)
        {
            runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                PersistenceEvents.WriteFailed,
                DiagnosticContext.Root(runtime.SessionId, "D61").WithJob("JOB-S"),
                new PstWritePayload("Receipt", "Move", true, "IOException"),
                Outcome: DiagnosticOutcome.Failed,
                ErrorDomain: ErrorDomain.Managed));
        }

        return runtime;
    }

    // ────────────────────────── §20.7 Retention ≠ Loss ──────────────────────────

    [Fact]
    public void FlightRingOverwriteIsRetentionAndNeverMarksCriticalLost()
    {
        var store = new DiagnosticSessionStore(Path.Combine(_root, "flight"), Guid.NewGuid());
        store.EnsureCreated();

        var ledger = new LossLedger();
        var coverage = new EvidenceCoverage(ledger);
        var options = new DiagnosticRuntimeOptions { RingEventCapacity = 1, RingByteBudget = 4096 };

        var flight = new FlightRecorder(store, options, new DiagnosticHealth(), ledger);

        // 环容量只有 1：第二条**必须**覆盖第一条 —— 而且第一条是 DurableCritical。
        var critical = Event(DeliveryClass.DurableCritical, 1);
        flight.Observe(critical);
        flight.Observe(Event(DeliveryClass.DurableCritical, 2));
        flight.Observe(Event(DeliveryClass.Operational, 3));

        // ★ 保留策略不是丢失 ★
        Assert.False(ledger.StickyCriticalLost);
        Assert.Equal(0, ledger.Snapshot().CriticalLost);
        Assert.True(ledger.RetentionEvictions > 0);

        // ★ 也不会污染 Operational 档证据完整性 ★
        Assert.True(coverage.IsCompleteFor(DeliveryClass.Operational));
        Assert.True(coverage.IsCompleteFor(DeliveryClass.DurableCritical));
        Assert.False(coverage.HasFaultLoss);

        var record = Assert.Single(ledger.Snapshot().Records.Where(r => r.ReasonCode == "ring-overwritten").Take(1));
        Assert.Equal(LossKind.FlightOverwrite, record.Kind);
        Assert.False(record.IsFault);
    }

    // ────────────────────────── §20.8 分档证据降级 ──────────────────────────

    [Fact]
    public void VerboseDropDoesNotDegradeOperationalEvidenceButOperationalLossDoes()
    {
        var ledger = new LossLedger();
        var coverage = new EvidenceCoverage(ledger);

        // ① 只丢 Verbose（调试细节）
        ledger.RecordDrop(DiagnosticBranches.IngressVerbose, DeliveryClass.Verbose, 10, "verbose-queue-full", evicted: false);

        Assert.False(coverage.IsCompleteFor(DeliveryClass.Verbose));           // Verbose 档确实不完整
        Assert.True(coverage.IsCompleteFor(DeliveryClass.Operational));        // ★ 但 Operational 档仍然完整 ★
        Assert.True(coverage.IsCompleteFor(DeliveryClass.DurableCritical));
        Assert.Single(ledger.Snapshot().Records).Kind.Equals(LossKind.VerboseDrop);

        // ② 真的丢了 Operational（在 analyzer 分支上）
        ledger.RecordDrop(DiagnosticBranches.Analyzer, DeliveryClass.Operational, 11, "analyzer-overflow", evicted: false);

        Assert.False(coverage.IsCompleteFor(DeliveryClass.Operational));       // ★ 这一档必须降级 ★
        Assert.False(coverage.IsCompleteForBranch(DiagnosticBranches.Analyzer));
        Assert.True(coverage.IsCompleteForBranch(DiagnosticBranches.Writer));  // 其它分支不受影响

        // ③ DurableCritical 真丢 ⇒ sticky
        ledger.RecordDrop(DiagnosticBranches.IngressCritical, DeliveryClass.DurableCritical, 12, "critical-queue-full", evicted: false);
        Assert.True(ledger.StickyCriticalLost);
        Assert.False(coverage.IsCompleteFor(DeliveryClass.DurableCritical));
    }

    [Fact]
    public void RuleCompletenessFollowsTheRuleDeclaredScope()
    {
        var ledger = new LossLedger();
        var coverage = new EvidenceCoverage(ledger);

        // 只丢 Verbose：依赖 Operational 的规则必须**保持**完整（不做过度降级）。
        ledger.RecordDrop(DiagnosticBranches.IngressVerbose, DeliveryClass.Verbose, 1, "verbose-full", evicted: false);

        var operationalRule = new ScopeProbeRule(DeliveryClass.Operational, null);
        var verboseRule = new ScopeProbeRule(DeliveryClass.Verbose, null);
        var analyzerBoundRule = new ScopeProbeRule(DeliveryClass.Operational, DiagnosticBranches.Analyzer);

        Assert.True(coverage.IsCompleteForRule(operationalRule));
        Assert.False(coverage.IsCompleteForRule(verboseRule));                  // 它依赖 Verbose ⇒ 降级
        Assert.True(coverage.IsCompleteForRule(analyzerBoundRule));             // analyzer 没丢东西

        // analyzer 分支丢一条 Operational ⇒ 只有"依赖 analyzer"的规则降级。
        ledger.RecordDrop(DiagnosticBranches.Analyzer, DeliveryClass.Operational, 2, "analyzer-overflow", evicted: false);
        Assert.False(coverage.IsCompleteForRule(analyzerBoundRule));
        Assert.False(coverage.IsCompleteForRule(operationalRule));
    }

    private sealed class ScopeProbeRule : IDiagnosticRule
    {
        private readonly DeliveryClass _required;
        private readonly string? _branch;

        public ScopeProbeRule(DeliveryClass required, string? branch)
        {
            _required = required;
            _branch = branch;
        }

        public string RuleId => "SCOPE_PROBE";
        public int Version => 1;
        public bool RequiresCompleteEvidence => true;
        public DeliveryClass RequiredDeliveryClass => _required;
        public string? RequiredBranch => _branch;
        public IReadOnlyCollection<string> WatchedEventCodes { get; } = Array.Empty<string>();
        public RuleOutcome? Evaluate(in DiagnosticEvent evt, RuleContext context) => null;
    }

    // ────────────────────────── §20.14 成功排空才写 clean marker ──────────────────────────

    [Fact]
    public async Task QueuedEventsAreDrainedBeforeTheCleanMarkerIsWritten()
    {
        const int queued = 200;
        var runtime = StartRuntime("drain", publish: queued);

        // 立刻关闭：这正是旧实现出问题的地方（共享 CTS 一取消，分支 pump 立刻退出，
        // 队列里的事件被静默放弃，却仍可能写 clean marker）。
        var report = await runtime.ShutdownAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(0, report.AbandonedEvents);
        Assert.True(report.CleanShutdown, "排空成功时应当是 clean：" + report.FailureReason);
        Assert.True(report.FlushAcknowledged);
        Assert.True(report.CleanMarkerWritten);
        Assert.Null(report.FailureReason);

        var sessionDir = runtime.Store!.SessionDir;
        Assert.True(File.Exists(Path.Combine(sessionDir, "clean-shutdown.marker")));

        // 所有应排空的事件都真的落盘了。
        var allEvents = Directory.GetFiles(Path.Combine(sessionDir, "events"), "*.jsonl")
            .Select(ReadShared).ToArray();
        var written = allEvents.Sum(text => text.Split('\n').Count(line => line.Contains("PST.WriteFailed", StringComparison.Ordinal)));
        Assert.True(written >= queued, $"只写下 {written} 条，期望 >= {queued}");

        // 标记里也要如实写清"本次会话有没有丢过证据"。
        using var marker = System.Text.Json.JsonDocument.Parse(ReadShared(Path.Combine(sessionDir, "clean-shutdown.marker")));
        Assert.Equal(0, marker.RootElement.GetProperty("evidenceLossEpoch").GetInt64());
        Assert.False(marker.RootElement.GetProperty("anyFaultLoss").GetBoolean());

        await runtime.DisposeAsync();
    }

    // ────────────────────────── §20.15 卡住/超时 ⇒ 不得假 clean ──────────────────────────

    [Fact]
    public async Task OverwhelmingBacklogNeverProducesAFalseCleanMarker()
    {
        // 故意塞入远超预算的积压 + 极小预算 ⇒ 必然无法全部排空。
        var runtime = StartRuntime("backlog", publish: 0);
        for (var i = 0; i < 20_000; i++)
        {
            runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                PersistenceEvents.WriteFailed,
                DiagnosticContext.Root(runtime.SessionId, "D61").WithJob("JOB-B"),
                new PstWritePayload("Receipt", "Move", true, "IOException"),
                Outcome: DiagnosticOutcome.Failed));
        }

        var report = await runtime.ShutdownAsync(TimeSpan.FromMilliseconds(1));

        // ★ 核心不变量：只要写了 clean marker，就一定没有任何条目被放弃 ★
        if (report.CleanMarkerWritten)
        {
            Assert.Equal(0, report.AbandonedEvents);
            Assert.True(report.CleanShutdown);
        }

        // 而"没排空"的情况必须如实反映（不得声称完整保存）。
        if (report.AbandonedEvents > 0)
        {
            Assert.False(report.CleanShutdown);
            Assert.False(report.CleanMarkerWritten);
            Assert.False(File.Exists(Path.Combine(runtime.Store!.SessionDir, "clean-shutdown.marker")));
            Assert.Equal("drain-budget-exhausted", report.FailureReason);
        }

        // 积压导致的丢弃必须是**可见**的（无论走哪条分支）。
        var snapshot = runtime.Loss.Snapshot();
        Assert.True(snapshot.Records.Count > 0, "丢弃必须进台账，不得静默");
        Assert.True(runtime.Loss.EpochFor(DeliveryClass.Operational) > 0 || report.AbandonedEvents > 0);

        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task BranchStopAsyncReportsAbandonedItemsWhenTheConsumerBlocks()
    {
        // 确定性的"卡住的消费者"：分支级验证 —— 预算到期必须如实报告被放弃的条数并记账。
        var ledger = new LossLedger();
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();

        var branch = new BoundedBranch(
            DiagnosticBranches.Writer,
            capacity: 8,
            byteBudget: 1024 * 1024,
            evictsOldest: false,
            ledger,
            new DiagnosticHealth(),
            async (_, ct) =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(ct).ConfigureAwait(false);
            });

        branch.Start(CancellationToken.None);
        for (var i = 0; i < 3; i++) branch.TryAccept(new BranchItem(Event(DeliveryClass.Operational, i + 1), 256));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var result = await branch.StopAsync(TimeSpan.FromMilliseconds(50));

        Assert.True(result.Abandoned > 0, "卡住的消费者 + 预算到期 ⇒ 必须报告被放弃的条目");
        Assert.Equal(0, result.Drained);
        Assert.Contains(ledger.Snapshot().Records, r => r.ReasonCode == "shutdown-budget-exhausted");

        release.TrySetResult();
        await branch.DisposeAsync();
    }

    [Fact]
    public void QueueDepthNeverGoesNegativeUnderOverloadAndRecovers()
    {
        // §20.9 的台账侧前提：Depth 必须在过载/丢弃/淘汰之后仍 >= 0 并最终归零。
        var ledger = new LossLedger();
        var health = new DiagnosticHealth();
        var consumed = 0;
        var branch = new BoundedBranch(
            "depth-probe", capacity: 4, byteBudget: 4096, evictsOldest: false, ledger, health,
            (_, _) => { Interlocked.Increment(ref consumed); return default; });

        branch.Start(CancellationToken.None);
        for (var i = 0; i < 200; i++) branch.TryAccept(new BranchItem(Event(DeliveryClass.Operational, i + 1), 256));
        Assert.True(branch.Depth >= 0, "Depth 不得为负");

        D2TestSupport.WaitUntil(() => branch.Depth == 0, 5000);
        Assert.Equal(0, branch.Depth);
        Assert.True(consumed > 0);
    }

    // ────────────────────────── helpers ──────────────────────────

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

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}