using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using PCMig.Diagnostics.Analysis;
using PCMig.Diagnostics.Analysis.Feedback;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D4b 契约：反馈契约 + 待满足期望跟踪器 + 两条反馈规则（UI_COMMAND_NOT_DISPATCHED / UI_FEEDBACK_MISSING）
/// + 事件卡落盘 + 两条证据规则（FILE_LOCKED / PREVIOUS_SESSION_UNCLEAN）。
///
/// 超时判定用**单调时钟**：测试直接构造"已经过了多少毫秒"，不真实 sleep（虚拟时间）。
/// </summary>
public sealed class D4bFeedbackTests
{
    private static long TicksFromMs(double ms) => (long)(ms / 1000.0 * Stopwatch.Frequency);

    private static DiagnosticEvent ActionObserved(
        Guid actionId, string actionKind, long sequence = 1, long runGeneration = 0,
        long monotonic = 0, string controlId = "Step1.Connect")
        => new()
        {
            SchemaVersion = DiagnosticEvent.CurrentSchemaVersion,
            Descriptor = UiEvents.UserActionObserved,
            SessionId = TestEvents.FixedSessionId,
            Sequence = sequence,
            TimestampUtc = new DateTimeOffset(2026, 9, 30, 6, 31, 32, TimeSpan.Zero).AddSeconds(sequence),
            MonotonicTimestamp = monotonic,
            Level = DiagnosticLevel.Information,
            Delivery = DeliveryClass.Operational,
            EvidenceQuality = EvidenceQuality.Direct,
            CaptureMode = CaptureMode.Operational,
            ActionId = actionId,
            ControlId = controlId,
            RunGeneration = runGeneration == 0 ? null : runGeneration,
            Payload = new UiActionPayload(actionKind, "click"),
        };

    private static DiagnosticEvent ActionEvent(
        EventDescriptor descriptor, Guid actionId, long sequence, long monotonic = 0,
        DiagnosticOutcome? outcome = null, IDiagnosticPayload? payload = null)
        => new()
        {
            SchemaVersion = DiagnosticEvent.CurrentSchemaVersion,
            Descriptor = descriptor,
            SessionId = TestEvents.FixedSessionId,
            Sequence = sequence,
            TimestampUtc = new DateTimeOffset(2026, 9, 30, 6, 31, 32, TimeSpan.Zero).AddSeconds(sequence),
            MonotonicTimestamp = monotonic,
            Level = descriptor.Level,
            Delivery = descriptor.Delivery,
            EvidenceQuality = EvidenceQuality.Direct,
            CaptureMode = CaptureMode.Operational,
            ActionId = actionId,
            Outcome = outcome,
            Payload = payload,
        };

    private static PendingExpectationTracker NewTracker(
        out FeedbackContractRegistry contracts,
        out System.Collections.Generic.List<ExpectationTimeout> timeouts)
    {
        contracts = FeedbackContractRegistry.CreateDefault();
        var captured = new System.Collections.Generic.List<ExpectationTimeout>();
        timeouts = captured;
        return new PendingExpectationTracker(contracts, captured.Add);
    }

    // ────────────────────────── 契约表自身 ──────────────────────────

    [Fact]
    public void ContractsCoverEveryActionKindWithSaneTimeoutsAndNoDuplicates()
    {
        var registry = FeedbackContractRegistry.CreateDefault();

        string[] expectedActions =
        {
            "Connect", "Prepare", "Start", "Resume", "Pause", "Stop", "Verify", "Repair", "Export",
        };
        foreach (var action in expectedActions)
        {
            Assert.True(registry.TryGet(action, out var contract), "缺少动作的反馈契约：" + action);
            Assert.NotEmpty(contract.Steps);
            Assert.Contains(contract.Steps, s => s.Kind == ExpectationKind.Immediate);   // 每个动作都必须有立即反馈
            Assert.NotEmpty(contract.TerminalEventNames);
            Assert.True(contract.ExternalWaitTimeoutMs >= contract.DeferredTimeoutMs);
            Assert.True(contract.DeferredTimeoutMs >= contract.ImmediateTimeoutMs);
        }

        // 合作式暂停的预算必须**远大于** UI 即时预算：暂停要等 worker 真正停住，不能用 100ms 判失败。
        // ★ FIX BATCH 3 / §6 ★ 但它**必须**等于引擎自己的硬失败 SLA（+ 宽限）—— 旧值 600_000（1 小时）
        //   正是 R-006 假绿根源：引擎 10 秒就自报"停不住"，诊断却要等一小时才可能开卡。
        var pauseContract = registry.All.Single(c => c.ActionKind == "Pause");
        Assert.Equal(ActionSla.PauseFulfillmentDeadlineMs, pauseContract.ExternalWaitTimeoutMs);
        Assert.True(pauseContract.ExternalWaitTimeoutMs >= 10_000, "暂停预算必须覆盖引擎的 10 秒硬失败 SLA");
        Assert.True(pauseContract.ExternalWaitTimeoutMs <= 15_000, "暂停预算必须与引擎 SLA 同量级，不是 1 小时");

        // 重复 ActionKind 必须直接拒绝（两套口径会互相覆盖）。
        Assert.Throws<InvalidOperationException>(() => new FeedbackContractRegistry(new[]
        {
            registry.All.First(), registry.All.First(),
        }));
    }

    // ────────────────────────── positive：没派发 ──────────────────────────

    [Fact]
    public void MissingImmediateStepAfterBudgetProducesNotDispatchedIncident()
    {
        var tracker = NewTracker(out _, out var timeouts);
        var actionId = Guid.NewGuid();

        tracker.Observe(ActionObserved(actionId, "Connect", monotonic: 0));
        Assert.Equal(1, tracker.PendingCount);

        // 尚未到期：不产生任何超时。
        tracker.Tick(TicksFromMs(500), evidenceComplete: true, lossEpoch: 0, acceptanceWatermark: 10);
        Assert.Empty(timeouts);

        // 超过 Immediate 预算（1500ms）：产出"没派发"超时事实。
        tracker.Tick(TicksFromMs(2_000), true, 0, 10);
        var timeout = Assert.Single(timeouts);
        Assert.Equal("connect.immediate", timeout.Step.ExpectationId);
        Assert.True(timeout.EvidenceComplete);
        Assert.True(timeout.OverdueMs > 0);

        var incident = new UiCommandNotDispatchedRule().Create(in timeout);
        Assert.Equal("UI_COMMAND_NOT_DISPATCHED", incident.RuleId);
        Assert.Equal("Ui.ActionDispatch", incident.BreakPoint);
        Assert.Equal(actionId, incident.ActionId);
        Assert.Equal(ConfidenceBand.Medium, incident.Confidence);
        Assert.Equal(IncidentStatus.Open, incident.Status);
        Assert.Contains(incident.Facts, f => f.EventCode == UiEvents.UserActionObserved.Name);
        Assert.Contains(incident.Candidates, c => c.CandidateCode == "handler-not-wired");
        Assert.NotEmpty(incident.SuggestedChecks);

        // 只判一次：再 Tick 不重复产出（否则会刷屏）。
        tracker.Tick(TicksFromMs(5_000), true, 0, 10);
        Assert.Single(timeouts);
    }

    // ────────────────────────── negative：按时派发 ──────────────────────────

    [Fact]
    public void SatisfiedStepsProduceNoTimeout()
    {
        var tracker = NewTracker(out _, out var timeouts);
        var actionId = Guid.NewGuid();

        tracker.Observe(ActionObserved(actionId, "Connect", monotonic: 0));
        tracker.Observe(ActionEvent(UiEvents.CommandStarted, actionId, 2, monotonic: TicksFromMs(50)));

        tracker.Tick(TicksFromMs(3_000), true, 0, 10);
        Assert.Empty(timeouts);
        Assert.Equal(1, tracker.Stats().Satisfied);

        // 连 Deferred/External 步骤也满足（预检开始 + 预检结束）⇒ 全部满足，仍无超时。
        tracker.Observe(ActionEvent(PreflightEvents.PreflightStarted, actionId, 3, TicksFromMs(100)));
        tracker.Observe(ActionEvent(PreflightEvents.PreflightCompleted, actionId, 4, TicksFromMs(200)));
        tracker.Tick(TicksFromMs(400_000), true, 0, 10);
        Assert.Empty(timeouts);
    }

    // ────────────────────────── positive：反馈缺失（Deferred / External 分开）──────────────────────────

    [Fact]
    public void MissingDeferredFeedbackProducesFeedbackMissingIncidentWithWeakerConfidence()
    {
        var tracker = NewTracker(out _, out var timeouts);
        var actionId = Guid.NewGuid();

        tracker.Observe(ActionObserved(actionId, "Connect", monotonic: 0));
        tracker.Observe(ActionEvent(UiEvents.CommandStarted, actionId, 2, TicksFromMs(10)));

        // 越过 Deferred 预算（10s）但未越过 External 预算（180s）。
        tracker.Tick(TicksFromMs(11_000), true, 0, 10);
        var timeout = Assert.Single(timeouts);
        Assert.Equal("connect.deferred", timeout.Step.ExpectationId);
        Assert.Equal(ExpectationKind.Deferred, timeout.Step.Kind);

        var incident = new UiFeedbackMissingRule().Create(in timeout);
        Assert.Equal("UI_FEEDBACK_MISSING", incident.RuleId);
        Assert.Equal("Ui.Feedback.connect.deferred", incident.BreakPoint);
        Assert.Equal(ConfidenceBand.Medium, incident.Confidence);       // 前段已满足 ⇒ 更强
        Assert.Contains(incident.Candidates, c => c.CandidateCode == "external-slow" || c.CandidateCode == "projection-not-pushed");
    }

    [Fact]
    public void ExternalWaitTimeoutOnANonTrustCriticalContractOnlySaysNotYet()
    {
        var tracker = NewTracker(out _, out var timeouts);
        var actionId = Guid.NewGuid();

        // ★ FIX BATCH 3 / §6 ★ 这条用例原本用 Pause 表达"外部等待超时只是还没等到"，
        //   而那恰恰是假绿本身。保留它**真实的意图**（不是每个 external 步骤都该判失败）：
        //   Connect 的 external 步骤没有声明 DeadlineBreachIsFailure ⇒ 超时仍只是"还没等到"。
        tracker.Observe(ActionObserved(actionId, "Connect", monotonic: 0));
        tracker.Tick(TicksFromMs(200_000), true, 0, 10);

        var timeout = timeouts.Single(t => t.Step.ExpectationId == "connect.external");
        Assert.False(timeout.Step.DeadlineBreachIsFailure);

        var incident = new UiFeedbackMissingRule().Create(in timeout);
        Assert.Equal(DiagnosticLevel.Warning, incident.Severity);
        Assert.Contains("还没等到", incident.UserFacingSummary, StringComparison.Ordinal);
        Assert.Contains("不一定是失败", incident.UserFacingSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void PauseExternalWaitBreachIsReportedAsFailureNotAsStillWaiting()
    {
        var tracker = NewTracker(out _, out var timeouts);
        var actionId = Guid.NewGuid();

        tracker.Observe(ActionObserved(actionId, "Pause", monotonic: 0));
        tracker.Observe(ActionEvent(TransferEvents.PauseRequestWriteResult, actionId, 2, TicksFromMs(20),
            DiagnosticOutcome.Succeeded, new TrnPauseRequestPayload(true, true, null)));

        // ★ §6 ★ 预算不再是一小时：12 秒（引擎 10 秒硬失败 SLA + 2 秒诊断宽限）之内不判超时。
        tracker.Tick(TicksFromMs(11_000), true, 0, 10);
        Assert.Empty(timeouts);

        tracker.Tick(TicksFromMs(13_000), true, 0, 10);
        var timeout = Assert.Single(timeouts);
        Assert.Equal("pause.external", timeout.Step.ExpectationId);
        Assert.True(timeout.Step.DeadlineBreachIsFailure);

        var incident = new UiFeedbackMissingRule().Create(in timeout);
        // 旧实现：Warning + "目前只是「还没等到」，不一定是失败" —— 请求受理成功就冒充了业务成功。
        Assert.Equal(DiagnosticLevel.Error, incident.Severity);
        Assert.Contains("业务效果", incident.UserFacingSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("不一定是失败", incident.UserFacingSummary, StringComparison.Ordinal);
    }

    // ────────────────────────── loss：采集有损必须降级 ──────────────────────────

    [Fact]
    public void LossyCollectionDegradesBothFeedbackRulesToInconclusive()
    {
        var tracker = NewTracker(out _, out var timeouts);
        var actionId = Guid.NewGuid();

        tracker.Observe(ActionObserved(actionId, "Connect", monotonic: 0));
        tracker.Tick(TicksFromMs(2_000), evidenceComplete: false, lossEpoch: 5, acceptanceWatermark: 42);

        var timeout = Assert.Single(timeouts);
        Assert.False(timeout.EvidenceComplete);

        var notDispatched = new UiCommandNotDispatchedRule().Create(in timeout);
        Assert.Equal(IncidentStatus.Inconclusive, notDispatched.Status);
        Assert.Equal(ConfidenceBand.Unknown, notDispatched.Confidence);
        Assert.True(notDispatched.EvidenceIncomplete);
        Assert.Equal(5, notDispatched.LossEpoch);
        var missing = Assert.Single(notDispatched.Missing);
        Assert.Equal("evidence-loss", missing.ReasonCode);
        Assert.Equal(42, missing.CoverageWatermark);
        Assert.False(missing.CollectionHealthy);
    }

    // ────────────────────────── cancellation：合法终点不产生结论 ──────────────────────────

    [Fact]
    public void RejectedAndCompletedActionsCloseExpectationsWithoutIncident()
    {
        var tracker = NewTracker(out _, out var timeouts);
        var rejected = Guid.NewGuid();
        var completed = Guid.NewGuid();

        tracker.Observe(ActionObserved(rejected, "Connect", 1, monotonic: 0));
        tracker.Observe(ActionEvent(UiEvents.ActionRejected, rejected, 2, TicksFromMs(30),
            DiagnosticOutcome.Rejected, new UiEligibilityPayload(false, "busy:IsConnecting")));

        tracker.Observe(ActionObserved(completed, "Connect", 3, monotonic: 0));
        tracker.Observe(ActionEvent(UiEvents.ActionCompleted, completed, 4, TicksFromMs(40),
            DiagnosticOutcome.Succeeded));

        tracker.Tick(TicksFromMs(500_000), true, 0, 10);

        Assert.Empty(timeouts);
        Assert.Equal(0, tracker.PendingCount);
        Assert.Equal(1, tracker.Stats().ClosedByRejection);
        Assert.Equal(1, tracker.Stats().ClosedByTerminal);
    }

    [Fact]
    public void FaultedActionClosesExpectationAndDoesNotDoubleReport()
    {
        var tracker = NewTracker(out _, out var timeouts);
        var actionId = Guid.NewGuid();

        tracker.Observe(ActionObserved(actionId, "Verify", monotonic: 0));
        tracker.Observe(ActionEvent(UiEvents.ActionFaulted, actionId, 2, TicksFromMs(30), DiagnosticOutcome.Failed));

        tracker.Tick(TicksFromMs(10_000), true, 0, 10);
        Assert.Empty(timeouts);
        Assert.Equal(0, tracker.PendingCount);
    }

    // ────────────────────────── generation switch ──────────────────────────

    [Fact]
    public void DifferentGenerationsProduceSeparateIncidentIdentities()
    {
        var tracker = NewTracker(out _, out var timeouts);
        var gen1 = Guid.NewGuid();
        var gen2 = Guid.NewGuid();

        tracker.Observe(ActionObserved(gen1, "Start", 1, runGeneration: 1, monotonic: 0));
        tracker.Observe(ActionObserved(gen2, "Start", 2, runGeneration: 2, monotonic: 0));
        tracker.Tick(TicksFromMs(2_000), true, 0, 10);

        Assert.Equal(2, timeouts.Count);
        var ids = timeouts
            .Select(t => UiCommandNotDispatchedRule.IncidentIdFor(t.Expectation))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, ids.Length);
        Assert.Contains(ids, id => id.EndsWith("|g1", StringComparison.Ordinal));
        Assert.Contains(ids, id => id.EndsWith("|g2", StringComparison.Ordinal));
    }

    [Fact]
    public void PendingExpectationsAreBounded()
    {
        var tracker = NewTracker(out _, out _);
        for (var i = 0; i < 300; i++)
            tracker.Observe(ActionObserved(Guid.NewGuid(), "Connect", i + 1, monotonic: 0));

        Assert.True(tracker.PendingCount <= 128, "待满足期望必须有上限，实际 " + tracker.PendingCount);
        Assert.True(tracker.Stats().DroppedByCap > 0, "被上限丢弃的期望数量必须可见");
    }

    [Fact]
    public void ShutdownClosesPendingWithoutProducingIncidents()
    {
        var tracker = NewTracker(out _, out var timeouts);
        tracker.Observe(ActionObserved(Guid.NewGuid(), "Connect", 1, monotonic: 0));
        tracker.CloseAll("session-shutdown");

        tracker.Tick(TicksFromMs(500_000), true, 0, 10);
        Assert.Empty(timeouts);
        Assert.Equal(0, tracker.PendingCount);
    }

    // ────────────────────────── 事件卡落盘 ──────────────────────────

    [Fact]
    public void IncidentsArePersistedAsAppendOnlyRevisionLog()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var runtime = DiagnosticRuntime.Start(D2TestSupport.Options(root), out _);
            try
            {
                for (var i = 0; i < 2; i++)
                {
                    runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                        PersistenceEvents.WriteFailed,
                        DiagnosticContext.Root(runtime.SessionId, "Test").WithJob("JOB-P"),
                        new PstWritePayload("Receipt", "Move", true, "IOException"),
                        Outcome: DiagnosticOutcome.Failed, ExceptionType: "IOException"));
                }

                Assert.True(D2TestSupport.WaitUntil(() => runtime.ActiveIncidents.Count > 0));
                Assert.True(D2TestSupport.WaitUntil(() =>
                    Directory.Exists(runtime.Store!.IncidentsDir)
                    && Directory.GetFiles(runtime.Store.IncidentsDir, "incidents-*.jsonl").Length > 0),
                    "事件卡必须落成 incidents.jsonl");

                var file = Directory.GetFiles(runtime.Store!.IncidentsDir, "incidents-*.jsonl").Single();
                var lines = D2TestSupport.ReadAllLinesShared(file);
                Assert.NotEmpty(lines);

                var dto = System.Text.Json.JsonSerializer.Deserialize<IncidentDto>(lines[^1],
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                Assert.NotNull(dto);
                Assert.Equal("PERSISTENCE_WRITE_FAILED", dto!.RuleId);
                Assert.Equal("JOB-P", dto.JobId);
                Assert.Contains("PERSISTENCE_WRITE_FAILED", dto.IncidentId, StringComparison.Ordinal);
                Assert.NotEmpty(dto.Facts);
                Assert.NotEmpty(dto.SuggestedChecks);
                Assert.Contains(dto.Candidates, c => c.Code == "file-locked-by-external-process");
            }
            finally
            {
                D2TestSupport.Shutdown(runtime);
                D2TestSupport.Dispose(runtime);
            }
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    // ────────────────────────── 两条新证据规则 ──────────────────────────

    [Fact]
    public void FileLockedIsDistinctFromAccessDenied()
    {
        var engine = new RuleEngine(RuleRegistry.CreateDefault(), _ => null);
        var ctx = engine.CreateContext(new EvidenceCoverage(new LossLedger()), 100);

        engine.Evaluate(Event(PersistenceEvents.WriteSkipped, 1,
            new PstWritePayload("Receipt", "Move", true, "IOException"),
            jobId: "JOB-1", objectId: "obj-1", win32: 32, errorDomain: ErrorDomain.Win32), ctx);

        var incident = engine.ActiveIncidents.Single(i => i.RuleId == "FILE_LOCKED");
        Assert.Equal(DiagnosticLevel.Warning, incident.Severity);      // 锁冲突不是权限错误
        Assert.Contains(incident.Candidates, c => c.CandidateCode == "file-in-use-by-app");
        Assert.Contains(incident.Candidates, c => c.CandidateCode == "permission-denied" && c.Refuted);
        // 同一条事件同时也会开一张"存档写失败/跳过"的卡（那是另一条规则的正向观测），互不冲突。
        Assert.Contains(engine.ActiveIncidents, i => i.RuleId == "PERSISTENCE_WRITE_FAILED");

        // 而 win32=5 走 ACCESS_DENIED，不产生 FILE_LOCKED。
        var engine2 = new RuleEngine(RuleRegistry.CreateDefault(), _ => null);
        engine2.Evaluate(Event(PersistenceEvents.WriteFailed, 1,
            new PstWritePayload("Receipt", "Move", true, "UnauthorizedAccessException"),
            jobId: "JOB-2", objectId: "obj-1", win32: 5, errorDomain: ErrorDomain.Win32),
            engine2.CreateContext(new EvidenceCoverage(new LossLedger()), 100));
        Assert.Contains(engine2.ActiveIncidents, i => i.RuleId == "ACCESS_DENIED");
        Assert.DoesNotContain(engine2.ActiveIncidents, i => i.RuleId == "FILE_LOCKED");
    }

    [Fact]
    public void PreviousSessionUncleanNeverClaimsACrash()
    {
        var engine = new RuleEngine(RuleRegistry.CreateDefault(), _ => null);
        engine.Evaluate(Event(DiagnosticsEvents.PreviousSessionUnclean, 1,
            new DiaPreviousSessionUncleanPayload(Guid.NewGuid(), "no-clean-marker", 987, 12, false)),
            engine.CreateContext(new EvidenceCoverage(new LossLedger()), 100));

        var incident = Assert.Single(engine.ActiveIncidents);
        Assert.Equal("PREVIOUS_SESSION_UNCLEAN", incident.RuleId);
        Assert.Equal(DiagnosticLevel.Warning, incident.Severity);
        Assert.Equal(ConfidenceBand.Medium, incident.Confidence);

        // ★ 绝不能宣称"上次崩溃了" ★
        Assert.DoesNotContain("崩溃了", incident.UserFacingSummary, StringComparison.Ordinal);
        Assert.Contains("不等于崩溃", incident.UserFacingSummary, StringComparison.Ordinal);
        Assert.Contains(incident.Candidates, c => c.CandidateCode == "power-loss-or-shutdown");
        // "崩溃"只能作为候选且置信度低、需外部佐证。
        var crash = incident.Candidates.Single(c => c.CandidateCode == "process-crash");
        Assert.Equal(ConfidenceBand.Low, crash.Confidence);
        Assert.Contains("需外部", crash.Rationale, StringComparison.Ordinal);
    }

    private static DiagnosticEvent Event(
        EventDescriptor descriptor, long sequence, IDiagnosticPayload? payload,
        string? jobId = null, string? objectId = null, int? win32 = null,
        ErrorDomain errorDomain = ErrorDomain.None)
        => new()
        {
            SchemaVersion = DiagnosticEvent.CurrentSchemaVersion,
            Descriptor = descriptor,
            SessionId = TestEvents.FixedSessionId,
            Sequence = sequence,
            TimestampUtc = new DateTimeOffset(2026, 9, 30, 6, 31, 32, TimeSpan.Zero).AddSeconds(sequence),
            MonotonicTimestamp = sequence * 1000,
            Level = descriptor.Level,
            Delivery = descriptor.Delivery,
            EvidenceQuality = EvidenceQuality.Direct,
            CaptureMode = CaptureMode.Operational,
            JobId = jobId,
            ObjectId = objectId,
            Win32Error = win32,
            ErrorDomain = errorDomain,
            Payload = payload,
        };
}