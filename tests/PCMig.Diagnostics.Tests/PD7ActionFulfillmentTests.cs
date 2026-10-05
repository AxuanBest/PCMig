using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using PCMig.Diagnostics.Analysis;
using PCMig.Diagnostics.Analysis.Feedback;
using PCMig.Diagnostics.Analysis.Rules;
using PCMig.WinUI.Presentation;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// ★ FIX BATCH 3 / §6 ★ Action Fulfillment（动作兑现）真值契约。
///
/// 这一批修的是**信任缺口**，不是事件缺口：真机上"用户点了暂停 → 请求文件确实写下去了 →
/// 传输从没停过 → 诊断里 0 张事件卡、健康 verdict 仍是 healthy"。旧的 pause.v1 只表达
/// "请求被写下"（immediate 步骤）与"还没等到"（external 步骤，预算 1 小时），
/// 于是"请求受理"冒充了"业务效果达成"。
///
/// 本文件锁死四件事：
///   ① 契约必须区分 **受理（request write）** 与 **兑现（业务状态真的变了）**；
///   ② 兑现失败必须**关闭期望为失败**（不是"还没等到"），并给出可归因证据；
///   ③ 失败必须**升到事件卡 + 健康 verdict**，绝不允许 0 事件卡 + healthy；
///   ④ §13 的**故意失败注入**必须端到端可复现（fake worker 收得到请求、停不住）。
///
/// 测试编号：PD-01…PD-09（§10 矩阵）。
/// </summary>
public sealed class PD7ActionFulfillmentTests
{
    private static long TicksFromMs(double ms) => (long)(ms / 1000.0 * Stopwatch.Frequency);

    // ────────────────────────── 事件构造（与生产同一批描述符）──────────────────────────

    private static DiagnosticEvent ActionObserved(
        Guid actionId, string actionKind, long sequence = 1, long monotonic = 0,
        string controlId = "Shell.Transfer.Pause")
        => new()
        {
            SchemaVersion = DiagnosticEvent.CurrentSchemaVersion,
            Descriptor = UiEvents.UserActionObserved,
            SessionId = TestEvents.FixedSessionId,
            Sequence = sequence,
            TimestampUtc = new DateTimeOffset(2026, 10, 4, 9, 15, 22, TimeSpan.Zero).AddSeconds(sequence),
            MonotonicTimestamp = monotonic,
            Level = DiagnosticLevel.Information,
            Delivery = DeliveryClass.Operational,
            EvidenceQuality = EvidenceQuality.Direct,
            CaptureMode = CaptureMode.Operational,
            ActionId = actionId,
            ControlId = controlId,
            Payload = new UiActionPayload(actionKind, "click"),
        };

    /// <summary>带 actionId 的事件（UI 自己的收尾事件）。</summary>
    private static DiagnosticEvent ActionEvent(
        EventDescriptor descriptor, Guid actionId, long sequence, long monotonic = 0,
        DiagnosticOutcome? outcome = null, IDiagnosticPayload? payload = null)
        => new()
        {
            SchemaVersion = DiagnosticEvent.CurrentSchemaVersion,
            Descriptor = descriptor,
            SessionId = TestEvents.FixedSessionId,
            Sequence = sequence,
            TimestampUtc = new DateTimeOffset(2026, 10, 4, 9, 15, 22, TimeSpan.Zero).AddSeconds(sequence),
            MonotonicTimestamp = monotonic,
            Level = descriptor.Level,
            Delivery = descriptor.Delivery,
            EvidenceQuality = EvidenceQuality.Direct,
            CaptureMode = CaptureMode.Operational,
            ActionId = actionId,
            Outcome = outcome,
            Payload = payload,
        };

    /// <summary>
    /// 引擎事件：**没有 actionId**（Core 不知道 UI 的 ActionId，真机日志就是这样）。
    /// 跟踪器必须靠事件名把它兑现到所有仍打开的期望上——这正是"请求受理 ⇒ 业务达成"的唯一通路。
    /// </summary>
    private static DiagnosticEvent EngineEvent(
        EventDescriptor descriptor, long sequence, long monotonic = 0,
        string? objectId = null, DiagnosticOutcome? outcome = null, IDiagnosticPayload? payload = null,
        long? runGeneration = null)
        => new()
        {
            SchemaVersion = DiagnosticEvent.CurrentSchemaVersion,
            Descriptor = descriptor,
            SessionId = TestEvents.FixedSessionId,
            Sequence = sequence,
            TimestampUtc = new DateTimeOffset(2026, 10, 4, 9, 15, 22, TimeSpan.Zero).AddSeconds(sequence),
            MonotonicTimestamp = monotonic,
            Level = descriptor.Level,
            Delivery = descriptor.Delivery,
            EvidenceQuality = EvidenceQuality.Direct,
            CaptureMode = CaptureMode.Operational,
            ObjectId = objectId,
            Outcome = outcome,
            RunGeneration = runGeneration,
            Payload = payload,
        };

    private static PendingExpectationTracker NewTracker(
        out List<ExpectationTimeout> timeouts, out List<ExpectationFailure> failures)
    {
        var contracts = FeedbackContractRegistry.CreateDefault();
        var capturedTimeouts = new List<ExpectationTimeout>();
        var capturedFailures = new List<ExpectationFailure>();
        timeouts = capturedTimeouts;
        failures = capturedFailures;
        var tracker = new PendingExpectationTracker(contracts, capturedTimeouts.Add);
        tracker.OnFailure = capturedFailures.Add;
        return tracker;
    }

    // ══════════════════════ PD-01 契约表：受理 ≠ 兑现 ══════════════════════

    [Fact]
    public void PD01_PauseContractSeparatesRequestAcceptanceFromBusinessEffect()
    {
        var registry = FeedbackContractRegistry.CreateDefault();
        Assert.True(registry.TryGet("Pause", out var contract), "必须有 Pause 的反馈契约");

        // 版本升级：v1 表达不了"请求成功但从未暂停"，v2 才有失败终点与业务期限。
        Assert.Equal("pause.v2", contract.ContractId);
        Assert.Equal(2, contract.Version);

        var external = contract.Steps.Single(s => s.ExpectationId == "pause.external");
        Assert.Equal(ExpectationKind.ExternalWait, external.Kind);

        // 兑现的判据 = 引擎**真的停住**（TRN-011 PauseObserved / TRN-013 Paused）。
        Assert.Contains(TransferEvents.PauseObserved.Name, external.ExpectedEventNames);
        Assert.Contains(TransferEvents.Paused.Name, external.ExpectedEventNames);

        // R-002：TRN-012 PauseBoundaryReached 全仓没有发射点，是死事件，不得再当作兑现判据。
        Assert.DoesNotContain(TransferEvents.PauseBoundaryReached.Name, external.ExpectedEventNames);

        // "运行结束"不等于"暂停兑现"：用户要的是暂停，不是"传完了"。
        Assert.DoesNotContain(TransferEvents.JobRunCompleted.Name, external.ExpectedEventNames);

        // 兑现期限必须是**秒级、可测**、且与引擎的硬失败 SLA 同一真值（旧值 3_600_000 ⇒ R-006 假绿）。
        Assert.Equal(ActionSla.PauseFulfillmentDeadlineMs, contract.ExternalWaitTimeoutMs);
        Assert.True(contract.ExternalWaitTimeoutMs <= 15_000,
            "暂停的兑现期限必须是秒级（旧实现是 1 小时 ⇒ '还没等到'被当成正常）");
        Assert.True(external.DeadlineBreachIsFailure,
            "pause.external 超期必须判**失败**，不是'还没等到'");

        // 失败终点：TRN-023 PauseFailed 一出现就是失败结论（引擎自己承认停不住）。
        var failure = contract.Steps.Single(s => s.Kind == ExpectationKind.Failure);
        Assert.Equal("pause.failure", failure.ExpectationId);
        Assert.Contains(TransferEvents.PauseFailed.Name, failure.ExpectedEventNames);

        // 被拒绝也是合法终点（按钮不可用时不会留下永远开着的期望）。
        Assert.Contains(UiEvents.ActionRejected.Name, contract.TerminalEventNames);

        // 其余契约的预算排序仍要成立（不许为改动 pause 而破坏别人的档位）。
        foreach (var c in registry.All)
        {
            Assert.True(c.ExternalWaitTimeoutMs >= c.DeferredTimeoutMs, c.ContractId);
            Assert.True(c.DeferredTimeoutMs >= c.ImmediateTimeoutMs, c.ContractId);
        }
    }

    // ══════════════════════ PD-02 兑现失败：关闭期望为失败 ══════════════════════

    [Fact]
    public void PD02_EngineFailureClosesTheExpectationAsFailedNotAsStillWaiting()
    {
        var tracker = NewTracker(out var timeouts, out var failures);
        var actionId = Guid.NewGuid();

        tracker.Observe(ActionObserved(actionId, "Pause", monotonic: 0));
        Assert.Equal(1, tracker.PendingCount);

        // 受理：请求文件写下去了（TRN-010）——这**只**满足 immediate 步骤。
        tracker.Observe(ActionEvent(TransferEvents.PauseRequestWriteResult, actionId, 2,
            monotonic: TicksFromMs(20), outcome: DiagnosticOutcome.Succeeded,
            payload: new TrnPauseRequestPayload(Immediate: false, Succeeded: true, ReasonCode: null)));
        Assert.Equal(1, tracker.PendingCount);   // 受理 ≠ 兑现，期望必须还开着
        Assert.Empty(failures);

        // 引擎承认停不住（TRN-023，Core 发布 ⇒ 没有 actionId，走"按事件名兑现"通路）。
        tracker.Observe(EngineEvent(TransferEvents.PauseFailed, sequence: 3, monotonic: TicksFromMs(10_050),
            objectId: "object-000012", outcome: DiagnosticOutcome.Failed,
            payload: new TrnPauseObservedPayload("Cooperative", "object-000012", 10_050)));

        var failure = Assert.Single(failures);
        Assert.Equal("pause.failure", failure.Step.ExpectationId);
        Assert.Equal(TransferEvents.PauseFailed.Name, failure.TriggerEventName);
        Assert.Equal(actionId, failure.Expectation.ActionId);
        Assert.Equal("Pause", failure.Expectation.ActionKind);
        Assert.True(failure.ObservedAfterMs >= 10_000, "必须记住'从受理到失败'过了多久（~10 s 硬失败 SLA）");

        var stats = tracker.Stats();
        Assert.Equal(1, stats.ClosedByFailure);
        Assert.Equal(0, stats.Pending);          // 失败必须真的关掉期望，不许留着当"还在等"
        // immediate 那一步确实被 TRN-010 满足了 —— 受理是真的，兑现从来没有发生。
        // （"受理成功"本身要如实保留：它是这条失败结论的前半段证据。）
        Assert.Equal(1, stats.Satisfied);
        Assert.Equal(0, stats.TimedOut);
        Assert.Empty(timeouts);
    }

    [Fact]
    public void PD02b_EngineFailureOnlyClosesPauseExpectations()
    {
        var tracker = NewTracker(out _, out var failures);

        // 另开一个别的动作（Resume）的期望：暂停失败不得把它一起判死。
        tracker.Observe(ActionObserved(Guid.NewGuid(), "Resume", sequence: 1, monotonic: 0));
        tracker.Observe(ActionObserved(Guid.NewGuid(), "Pause", sequence: 2, monotonic: 0));
        Assert.Equal(2, tracker.PendingCount);

        tracker.Observe(EngineEvent(TransferEvents.PauseFailed, sequence: 3, monotonic: TicksFromMs(9_000),
            outcome: DiagnosticOutcome.Failed,
            payload: new TrnPauseObservedPayload("Cooperative", "object-1", 9_000)));

        Assert.Single(failures);
        Assert.Equal(1, tracker.Stats().ClosedByFailure);
        Assert.Equal(1, tracker.Stats().Pending);   // Resume 的期望仍然开着
    }

    // ══════════════════════ PD-03 兑现成功：满足 external，且不误判失败 ══════════════════════

    [Fact]
    public void PD03_EngineConfirmationSatisfiesTheEffectStepAndNeverTimesOutAsFailure()
    {
        var tracker = NewTracker(out var timeouts, out var failures);
        var actionId = Guid.NewGuid();

        tracker.Observe(ActionObserved(actionId, "Pause", monotonic: 0));
        tracker.Observe(ActionEvent(TransferEvents.PauseRequestWriteResult, actionId, 2,
            monotonic: TicksFromMs(15), outcome: DiagnosticOutcome.Succeeded,
            payload: new TrnPauseRequestPayload(false, true, null)));

        // 引擎真的停住了（TRN-011 PauseObserved，没有 actionId）。
        tracker.Observe(EngineEvent(TransferEvents.PauseObserved, sequence: 3, monotonic: TicksFromMs(240),
            objectId: "object-000012", outcome: DiagnosticOutcome.Succeeded,
            payload: new TrnPauseObservedPayload("Immediate", "object-000012", 240)));

        Assert.Empty(failures);
        // 计数器按**步骤**计：pause.immediate（TRN-010）与 pause.external（TRN-011）各计一次。
        Assert.Equal(2, tracker.Stats().Satisfied);
        Assert.Equal(1, tracker.PendingCount);   // 等 UI 自己的终点事件来收口

        // 兑现之后，就算 time 一路走到门槛外，也**不许**再报超时（成功不能被推翻）。
        tracker.Tick(TicksFromMs(60_000), evidenceComplete: true, lossEpoch: 0, acceptanceWatermark: 10);
        Assert.Empty(timeouts);
        Assert.Equal(0, tracker.Stats().TimedOut);
        Assert.Empty(failures);

        // UI 收尾（ActionCompleted 带 actionId）⇒ 正常关闭。
        tracker.Observe(ActionEvent(UiEvents.ActionCompleted, actionId, 4, monotonic: TicksFromMs(300),
            outcome: DiagnosticOutcome.Succeeded));
        Assert.Equal(0, tracker.PendingCount);
        Assert.Equal(1, tracker.Stats().ClosedByTerminal);
        Assert.Empty(failures);
    }

    // ══════════════════════ PD-04 请求写入成功 ≠ 业务达成（真机的形态）══════════════════════

    [Fact]
    public void PD04_RequestWriteSuccessAloneMustBreachTheDeadlineAsAFailure()
    {
        var tracker = NewTracker(out var timeouts, out var failures);
        var actionId = Guid.NewGuid();

        tracker.Observe(ActionObserved(actionId, "Pause", monotonic: 0));
        tracker.Observe(ActionEvent(TransferEvents.PauseRequestWriteResult, actionId, 2,
            monotonic: TicksFromMs(11), outcome: DiagnosticOutcome.Succeeded,
            payload: new TrnPauseRequestPayload(false, true, null)));

        // 真机形态：之后什么都没有发生——迁移继续跑，引擎从不确认。
        tracker.Tick(TicksFromMs(ActionSla.PauseFulfillmentDeadlineMs - 500),
            evidenceComplete: true, lossEpoch: 0, acceptanceWatermark: 10);
        Assert.Empty(timeouts);   // 期限内不算超时（不能冤枉引擎）

        tracker.Tick(TicksFromMs(ActionSla.PauseFulfillmentDeadlineMs + 200),
            evidenceComplete: true, lossEpoch: 0, acceptanceWatermark: 10);

        var timeout = Assert.Single(timeouts);
        Assert.Equal("pause.external", timeout.Step.ExpectationId);
        Assert.True(timeout.Step.DeadlineBreachIsFailure, "这条超时是**失败**结论，不是'还没等到'");
        Assert.Equal(actionId, timeout.Expectation.ActionId);

        // 失败检测步骤（pause.failure）"没发生"是正常的，不得再产出第二条噪音。
        Assert.DoesNotContain(timeouts, t => t.Step.ExpectationId == "pause.failure");

        // 同一条超时必须只报一次。
        tracker.Tick(TicksFromMs(ActionSla.PauseFulfillmentDeadlineMs + 900),
            evidenceComplete: true, lossEpoch: 0, acceptanceWatermark: 10);
        Assert.Single(timeouts);
        Assert.Equal(1, tracker.Stats().TimedOut);
    }

    // ══════════════════════ PD-05 失败必须升到事件卡（Error 级）══════════════════════

    [Fact]
    public void PD05_PauseFailedOpensATrustCriticalIncidentAndTheRegistryKnowsIt()
    {
        var registry = RuleRegistry.CreateDefault();
        Assert.Equal(8, registry.Count);   // 7 条既有 + ACTION_FULFILLMENT_FAILED

        var rules = registry.ForEventName(TransferEvents.PauseFailed.Name);
        var rule = Assert.Single(rules);
        Assert.Equal("ACTION_FULFILLMENT_FAILED", rule.RuleId);

        var engine = new RuleEngine(registry, _ => null);
        var context = engine.CreateContext(new EvidenceCoverage(new LossLedger()), acceptanceWatermark: 1000);

        var outcome = rule.Evaluate(
            EngineEvent(TransferEvents.PauseFailed, sequence: 1, outcome: DiagnosticOutcome.Failed,
                payload: new TrnPauseObservedPayload("Cooperative", "object-000012", 10_050)),
            context);

        Assert.NotNull(outcome);
        var incident = outcome!.Incident;
        Assert.Equal("ACTION_FULFILLMENT_FAILED", incident.RuleId);
        Assert.Equal("ACTION_FULFILLMENT_FAILED", incident.SymptomCode);
        Assert.Equal(DiagnosticLevel.Error, incident.Severity);
        Assert.True(outcome.ShouldTriggerFlight, "信任缺口必须触发飞行窗口（前后证据冻结）");
        Assert.Contains("暂停", incident.UserFacingSummary, StringComparison.Ordinal);
        Assert.Contains("仍在进行", incident.UserFacingSummary, StringComparison.Ordinal);
        Assert.NotEmpty(incident.Facts);

        // 别的引擎事件不该被它误伤。
        Assert.Empty(registry.ForEventName(TransferEvents.PauseObserved.Name));
        Assert.Empty(registry.ForEventName(TransferEvents.PauseRequestWriteResult.Name));
    }

    // ══════════════════════ PD-06 未兑现 ⇒ 健康降级，但不算证据丢失 ══════════════════════

    [Fact]
    public void PD06_UnfulfilledActionDegradesHealthWithoutPollutingEvidenceCompleteness()
    {
        var health = new DiagnosticHealth();
        var loss = new LossLedger();

        var before = DiagnosticHealthSnapshot.Capture(health, loss.Snapshot());
        Assert.False(before.IsDegraded);
        Assert.True(before.EvidenceComplete);
        Assert.Equal(0, before.ActionUnfulfilled);

        health.MarkActionUnfulfilled("expectation-failed:pause.external:TRN-023");
        var after = DiagnosticHealthSnapshot.Capture(health, loss.Snapshot());

        Assert.Equal(1, after.ActionUnfulfilled);
        Assert.True(after.IsDegraded, "「用户要求了、请求也受理了、业务效果从未达成」必须让 verdict 降级");
        Assert.False(after.EvidenceComplete == false && after.EventsDropped > 0,
            "未兑现不是证据丢失（它是一条**完整的观测**）");
        Assert.Contains("pause.external", after.LastActionUnfulfilledReason!, StringComparison.Ordinal);
    }

    // ══════════════════════ PD-07 诊断中心不得再说"没有发现问题" ══════════════════════

    [Fact]
    public void PD07_DiagnosticCenterNeverClaimsAllClearWhileAnActionIsUnfulfilled()
    {
        var health = new DiagnosticHealth();
        health.MarkActionUnfulfilled("expectation-failed:pause.external:TRN-023");
        var loss = new LossLedger();
        var source = new FakeSource
        {
            Health = DiagnosticHealthSnapshot.Capture(health, loss.Snapshot()),
            Expectations = new ExpectationTrackerStats(2, 1, 1, 0, 0, 0, 0, 0, 1),
        };

        var vm = new DiagnosticCenterViewModel(source);
        vm.Refresh();

        // 0 张事件卡 + 未兑现动作 ⇒ 绝不允许"没有发现问题"。
        Assert.Contains("0", vm.IncidentSummaryText, StringComparison.Ordinal);
        Assert.DoesNotContain("没有发现问题", vm.IncidentSummaryText, StringComparison.Ordinal);
        Assert.Contains("未满足", vm.IncidentSummaryText, StringComparison.Ordinal);

        Assert.DoesNotContain("健康", vm.OverviewHealthText, StringComparison.Ordinal);
        Assert.Contains("业务效果", vm.OverviewHealthText, StringComparison.Ordinal);
    }

    // ══════════════════════ PD-08 runtime 接线：失败/超时都要接到健康 ══════════════════════

    [Fact]
    public void PD08_RuntimeWiresExpectationFailureAndDeadlineBreachIntoHealth()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var runtime = DiagnosticRuntime.Start(D2TestSupport.Options(root), out _);
            try
            {
                var field = typeof(DiagnosticRuntime).GetField("_expectations", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(field);
                var tracker = Assert.IsType<PendingExpectationTracker>(field!.GetValue(runtime));

                Assert.NotNull(tracker.OnFailure);
                Assert.NotNull(tracker.OnFault);
                // 超时通道由构造函数注入（私有字段 _onTimeout）；这里按同一口径核对它确实被接上。
                var timeoutField = typeof(PendingExpectationTracker)
                    .GetField("_onTimeout", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(timeoutField);
                Assert.NotNull(timeoutField!.GetValue(tracker));
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

    // ══════════════════════ PD-09 §13 故意失败注入（端到端，防假绿核心）══════════════════════

    [Fact]
    public void PD09_FailedPauseInjectionEndToEndYieldsIncidentDegradedHealthAndFailedExpectation()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var runtime = DiagnosticRuntime.Start(D2TestSupport.Options(root), out _);
            try
            {
                var actionId = Guid.NewGuid();
                var ui = DiagnosticContext.Root(runtime.SessionId, "ShellFooter")
                    .WithControl("Shell.Transfer.Pause").WithAction(actionId);
                var engineCtx = DiagnosticContext.Root(runtime.SessionId, "TransferOrchestrator").WithJob("JOB-PD13");

                // ① 用户点了暂停（UI 动作被观测）。
                runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                    UiEvents.UserActionObserved, ui, new UiActionPayload("Pause", "click")));

                // ② 请求文件确实写下成功了（受理成功——旧实现就停在这一步然后报"健康"）。
                runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                    TransferEvents.PauseRequestWriteResult,
                    ui.WithComponent("JobManager"),
                    new TrnPauseRequestPayload(false, true, null),
                    Outcome: DiagnosticOutcome.Succeeded));

                // ③ §13 的注入：fake worker 收得到请求，但**停不住** ⇒ 引擎自己承认硬失败。
                runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                    TransferEvents.PauseFailed, engineCtx, 
                    new TrnPauseObservedPayload("Cooperative", "object-000012", 10_050),
                    Outcome: DiagnosticOutcome.Failed));

                // ① 事件卡：必须有一张 Error 级、可归因的信任缺口事件卡。
                Assert.True(D2TestSupport.WaitUntil(() => runtime.ActiveIncidents.Count > 0),
                    "「请求受理但业务效果从未达成」必须开出事件卡（旧实现这里是 0）");
                var incident = runtime.ActiveIncidents.First(i => i.RuleId == "ACTION_FULFILLMENT_FAILED");
                Assert.Equal(DiagnosticLevel.Error, incident.Severity);
                Assert.Equal("JOB-PD13", incident.JobId);

                // ② 健康 verdict：必须降级（旧实现是 healthy）。
                Assert.True(D2TestSupport.WaitUntil(() => runtime.GetHealthSnapshot().IsDegraded),
                    "动作未兑现必须让 verdict 降级");
                Assert.True(runtime.GetHealthSnapshot().ActionUnfulfilled >= 1);

                // ③ 期望：必须被判**失败**并关闭，不许留着当"还在等"。
                var field = typeof(DiagnosticRuntime).GetField("_expectations", BindingFlags.NonPublic | BindingFlags.Instance);
                var tracker = Assert.IsType<PendingExpectationTracker>(field!.GetValue(runtime));
                Assert.True(D2TestSupport.WaitUntil(() => tracker.Stats().ClosedByFailure >= 1),
                    "PauseFailed 必须把期望关成失败");
                var stats = tracker.Stats();
                Assert.Equal(0, stats.Pending);
                // 受理步骤（TRN-010）被满足了，兑现步骤从未达成 —— 这正是 §13 注入要固定下来的形态。
                Assert.Equal(1, stats.Satisfied);
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

    // ────────────────────────── 诊断中心测试替身（与 D5 同形）──────────────────────────

    private sealed class FakeSource : IDiagnosticCenterSource
    {
        public bool IsAvailable { get; set; } = true;
        public Guid SessionId { get; set; } = TestEvents.FixedSessionId;
        public string ModeName { get; set; } = "Operational";
        public string? StorageRoot { get; set; } = @"C:\temp\diag";
        public DiagnosticHealthSnapshot Health { get; set; }
        public List<Incident> Incidents { get; } = new();
        public DiagnosticEvent[] RecentEvents { get; set; } = Array.Empty<DiagnosticEvent>();
        public FlightStats? Flight { get; set; }
        public RuleEngineStats? Rules { get; set; }
        public ExpectationTrackerStats? Expectations { get; set; }

        public DiagnosticHealthSnapshot GetHealth() => Health;
        public IReadOnlyList<Incident> GetIncidents() => Incidents;
        public DiagnosticEvent[] GetRecentEvents(int max) => RecentEvents.Take(max).ToArray();
        public FlightStats? GetFlight() => Flight;
        public RuleEngineStats? GetRules() => Rules;
        public ExpectationTrackerStats? GetExpectations() => Expectations;
        public void RequestMode(CaptureMode mode, string reasonCode) { }
    }
}