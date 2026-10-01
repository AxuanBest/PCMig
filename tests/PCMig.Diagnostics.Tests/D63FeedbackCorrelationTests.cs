using System;
using System.Diagnostics;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using PCMig.Diagnostics.Analysis;
using PCMig.Diagnostics.Analysis.Feedback;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D6.3 §11 红灯 fixture 18（缺陷 D63-18）：期望跟踪器的**关联策略**。
///
/// 实机证据（2026-10-02 00:37，会话 3987cad85cc84f15a3c0b6d8d120edef）：
///   真实 UI 点击 Start ⇒ 20s 后开出事件卡
///   <c>UI_FEEDBACK_MISSING|19ace98267cc4f558cacab605b522564|start.deferred|g0</c>
///   （EvidenceIncomplete=false、Confidence=Medium、
///    TechnicalSummary 里 <c>expected=TRN.JobRunStarted … satisfied=start.immediate</c>），
///   而 <c>TRN.JobRunStarted</c> 就在点击后 12ms 落盘。
///
/// 根因：<c>PendingExpectationTracker.ObserveCore</c> 原来"事件没有 actionId 就 return"，
/// 而 JobManager/robocopy/预检线程发布的业务事件**按设计不带 actionId**——
/// 它们恰恰是 Deferred/ExternalWait 步骤指名要等的证据 ⇒ 这些步骤永远无法被满足，
/// 动作只要没在预算内走到终止事件，就会产出"用户没看到反馈"的假结论。
///
/// 本组测试锁死修好后的口径：**无 actionId 的业务事件可以满足步骤，错配与关闭仍然严格。**
/// </summary>
public sealed class D63FeedbackCorrelationTests
{
    private static long TicksFromMs(double ms) => (long)(ms / 1000.0 * Stopwatch.Frequency);

    /// <summary>真实用户动作事件：带 actionId（UI 层事实）。</summary>
    private static DiagnosticEvent ActionObserved(
        Guid actionId, string actionKind, long sequence = 1, long monotonic = 0,
        string controlId = "Step2.Start")
        => new()
        {
            SchemaVersion = DiagnosticEvent.CurrentSchemaVersion,
            Descriptor = UiEvents.UserActionObserved,
            SessionId = TestEvents.FixedSessionId,
            Sequence = sequence,
            TimestampUtc = new DateTimeOffset(2026, 10, 2, 0, 37, 2, TimeSpan.Zero).AddSeconds(sequence),
            MonotonicTimestamp = monotonic,
            Level = DiagnosticLevel.Information,
            Delivery = DeliveryClass.Operational,
            EvidenceQuality = EvidenceQuality.Direct,
            CaptureMode = CaptureMode.Operational,
            ActionId = actionId,
            ControlId = controlId,
            Payload = new UiActionPayload(actionKind, "click"),
        };

    /// <summary>业务事件：<paramref name="actionId"/> 为 null 时就是"后台线程发布、无关联"的真实形态。</summary>
    private static DiagnosticEvent Business(
        EventDescriptor descriptor, Guid? actionId, long sequence, long monotonic,
        DiagnosticOutcome? outcome = null)
        => new()
        {
            SchemaVersion = DiagnosticEvent.CurrentSchemaVersion,
            Descriptor = descriptor,
            SessionId = TestEvents.FixedSessionId,
            Sequence = sequence,
            TimestampUtc = new DateTimeOffset(2026, 10, 2, 0, 37, 2, TimeSpan.Zero).AddSeconds(sequence),
            MonotonicTimestamp = monotonic,
            Level = descriptor.Level,
            Delivery = descriptor.Delivery,
            EvidenceQuality = EvidenceQuality.Direct,
            CaptureMode = CaptureMode.Operational,
            ActionId = actionId,
            Outcome = outcome,
        };

    private static PendingExpectationTracker NewTracker(out System.Collections.Generic.List<ExpectationTimeout> timeouts)
    {
        var captured = new System.Collections.Generic.List<ExpectationTimeout>();
        timeouts = captured;
        return new PendingExpectationTracker(FeedbackContractRegistry.CreateDefault(), captured.Add);
    }

    // ─────────────────── 红灯（修复前必失败）：无 actionId 的业务事件必须满足步骤 ───────────────────

    [Fact]
    public void Fixture18_UncorrelatedBusinessEventsSatisfyTheStartStepsSoNoFalseIncidentIsOpened()
    {
        var tracker = NewTracker(out var timeouts);
        var actionId = Guid.NewGuid();

        tracker.Observe(ActionObserved(actionId, "Start", 1, monotonic: 0));
        // 真实序列：UI.CommandStarted（带 actionId）→ TRN.JobRunStarted（无）→ TRN.ObjectStarted（无）
        tracker.Observe(Business(UiEvents.CommandStarted, actionId, 2, TicksFromMs(1)));
        tracker.Observe(Business(TransferEvents.JobRunStarted, actionId: null, 3, TicksFromMs(12)));
        tracker.Observe(Business(TransferEvents.ObjectStarted, actionId: null, 4, TicksFromMs(14)));

        // 越过 Deferred 预算（20s）与 External 预算（300s）都不得产出任何"缺反馈"结论。
        tracker.Tick(TicksFromMs(21_000), true, 0, 10);
        tracker.Tick(TicksFromMs(400_000), true, 0, 10);

        Assert.Empty(timeouts);
        Assert.Equal(3, tracker.Stats().Satisfied);
    }

    [Fact]
    public void Fixture18_UncorrelatedBusinessEventSatisfiesOnlyTheStepThatNamesIt()
    {
        var tracker = NewTracker(out var timeouts);
        var actionId = Guid.NewGuid();

        tracker.Observe(ActionObserved(actionId, "Start", 1, monotonic: 0));
        // TRN.JobRunStarted 只该满足 start.deferred：既不是 start.immediate（等 CommandStarted），
        // 也不是 start.external（等 ObjectStarted/JobRunCompleted）。
        tracker.Observe(Business(TransferEvents.JobRunStarted, actionId: null, 2, TicksFromMs(12)));

        Assert.Equal(1, tracker.Stats().Satisfied);

        // start.immediate 到期仍必须开卡（放松的只是"无 actionId"，不是"步骤要求"）。
        tracker.Tick(TicksFromMs(2_000), true, 0, 10);
        var timeout = Assert.Single(timeouts);
        Assert.Equal("start.immediate", timeout.Step.ExpectationId);
    }

    // ─────────────────── 不放松：错配仍然不算证据，关闭仍然要求本次动作 ───────────────────

    [Fact]
    public void Fixture18_BusinessEventCarryingAForeignActionIdIsNotCreditedToThisAction()
    {
        var tracker = NewTracker(out var timeouts);
        var actionId = Guid.NewGuid();

        tracker.Observe(ActionObserved(actionId, "Start", 1, monotonic: 0));
        tracker.Observe(Business(UiEvents.CommandStarted, actionId, 2, TicksFromMs(1)));
        // 别的动作的业务事件：绝不能被记到这一次动作上。
        tracker.Observe(Business(TransferEvents.JobRunStarted, Guid.NewGuid(), 3, TicksFromMs(12)));

        Assert.Equal(1, tracker.Stats().Satisfied);

        tracker.Tick(TicksFromMs(21_000), true, 0, 10);
        var timeout = Assert.Single(timeouts);
        Assert.Equal("start.deferred", timeout.Step.ExpectationId);

        var incident = new UiFeedbackMissingRule().Create(in timeout);
        Assert.Equal("UI_FEEDBACK_MISSING", incident.RuleId);
        Assert.Equal("Ui.Feedback.start.deferred", incident.BreakPoint);
        Assert.Equal(actionId, incident.ActionId);
    }

    [Fact]
    public void Fixture18_UncorrelatedEventCannotCloseAnExpectation()
    {
        var tracker = NewTracker(out var timeouts);
        var actionId = Guid.NewGuid();

        tracker.Observe(ActionObserved(actionId, "Start", 1, monotonic: 0));
        // 终止事件**必须**带 actionId 才能结案：无 actionId 的 ActionCompleted 不得关闭期望。
        tracker.Observe(Business(UiEvents.ActionCompleted, actionId: null, 2, TicksFromMs(20),
            DiagnosticOutcome.Succeeded));

        Assert.Equal(1, tracker.PendingCount);
        Assert.Equal(0, tracker.Stats().ClosedByTerminal);

        // 带对了 actionId 才结案，且不产出任何结论。
        tracker.Observe(Business(UiEvents.ActionCompleted, actionId, 3, TicksFromMs(30),
            DiagnosticOutcome.Succeeded));
        Assert.Equal(0, tracker.PendingCount);
        Assert.Equal(1, tracker.Stats().ClosedByTerminal);

        tracker.Tick(TicksFromMs(500_000), true, 0, 10);
        Assert.Empty(timeouts);
    }

    // ─────────────────── 反向：真的没有业务事件时，仍然必须敢说"缺反馈" ───────────────────

    [Fact]
    public void Fixture18_GenuinelyMissingBusinessStartStillOpensTheFeedbackMissingIncident()
    {
        var tracker = NewTracker(out var timeouts);
        var actionId = Guid.NewGuid();

        tracker.Observe(ActionObserved(actionId, "Start", 1, monotonic: 0));
        tracker.Observe(Business(UiEvents.CommandStarted, actionId, 2, TicksFromMs(1)));
        // 这里**故意**不发布任何 TRN.* 事件：业务确实没有开始。

        tracker.Tick(TicksFromMs(21_000), true, 0, 10);
        var timeout = Assert.Single(timeouts);
        Assert.Equal("start.deferred", timeout.Step.ExpectationId);
        Assert.True(timeout.EvidenceComplete);

        var incident = new UiFeedbackMissingRule().Create(in timeout);
        Assert.Equal("UI_FEEDBACK_MISSING", incident.RuleId);
        Assert.Equal("start.deferred", timeout.Step.ExpectationId);
        Assert.False(incident.EvidenceIncomplete);
        Assert.Equal(ConfidenceBand.Medium, incident.Confidence);
        Assert.Contains("反馈没有出现", incident.UserFacingSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Fixture18_UncorrelatedEventForAnUnrelatedActionKindIsIgnored()
    {
        var tracker = NewTracker(out var timeouts);
        var actionId = Guid.NewGuid();

        // Export 契约的步骤不指名 TRN.* ⇒ 无 actionId 的 TRN 事件不该满足它，
        // 也不该制造任何其它动作的满足计数。
        tracker.Observe(ActionObserved(actionId, "Export", 1, monotonic: 0, controlId: "Toolbar.Export"));
        tracker.Observe(Business(TransferEvents.JobRunStarted, actionId: null, 2, TicksFromMs(12)));
        Assert.Equal(0, tracker.Stats().Satisfied);

        tracker.Tick(TicksFromMs(3_000), true, 0, 10);
        var timeout = Assert.Single(timeouts);
        Assert.Equal("export.immediate", timeout.Step.ExpectationId);
    }
}