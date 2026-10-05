using System;
using System.Collections.Generic;
using System.Linq;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using PCMig.Diagnostics.Analysis;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D4 契约：确定性规则引擎。
///
/// 每条真规则都要覆盖六类情形：**positive / negative / loss / late / cancellation / generation switch**
/// （方案 §14.3 的要求），此外还有"引擎自身的边界"：冷却抑制、活动卡上限、规则异常隔离、
/// 以及"证据不足时必须敢说不知道"。
/// </summary>
public sealed class D4RuleEngineTests
{
    private static DiagnosticEvent Event(
        EventDescriptor descriptor,
        long sequence,
        IDiagnosticPayload? payload = null,
        string? jobId = null,
        string? objectId = null,
        int? win32 = null,
        ErrorDomain errorDomain = ErrorDomain.None,
        string? exceptionType = null,
        int? robocopyExitCode = null,
        DiagnosticOutcome? outcome = null,
        long? runGeneration = null,
        string pass = DiagnosticPass.Bulk,
        DateTimeOffset? atUtc = null)
        => new()
        {
            SchemaVersion = DiagnosticEvent.CurrentSchemaVersion,
            Descriptor = descriptor,
            SessionId = TestEvents.FixedSessionId,
            Sequence = sequence,
            TimestampUtc = atUtc ?? new DateTimeOffset(2026, 9, 30, 6, 31, 32, TimeSpan.Zero).AddSeconds(sequence),
            MonotonicTimestamp = sequence * 1000,
            Level = descriptor.Level,
            Delivery = descriptor.Delivery,
            EvidenceQuality = EvidenceQuality.Direct,
            CaptureMode = CaptureMode.Operational,
            JobId = jobId,
            ObjectId = objectId,
            Win32Error = win32,
            ErrorDomain = errorDomain,
            ExceptionType = exceptionType,
            RobocopyExitCode = robocopyExitCode,
            Outcome = outcome,
            RunGeneration = runGeneration,
            Pass = pass,
            Payload = payload,
        };

    private static RuleEngine NewEngine(out List<(Incident Incident, RuleEngine.IncidentLifecycle Lifecycle)> observed)
    {
        var sink = new List<(Incident, RuleEngine.IncidentLifecycle)>();
        observed = sink;
        var engine = new RuleEngine(RuleRegistry.CreateDefault(), _ => null)
        {
            OnIncident = (incident, lifecycle) => sink.Add((incident, lifecycle)),
        };
        return engine;
    }

    /// <summary>
    /// 完整证据的上下文：用一个**真** LossLedger（没有任何丢失）构造分档覆盖
    /// （D6.1 §4 起，完整性不再是外部塞进来的 bool，而是由台账按投递类/分支推导）。
    /// </summary>
    private static RuleContext CompleteContext(RuleEngine engine) =>
        engine.CreateContext(new EvidenceCoverage(new LossLedger()), acceptanceWatermark: 1000);

    /// <summary>
    /// 有损证据的上下文：真的记账一次 **Operational** 丢失 ⇒ Operational 档完整性受损
    /// （这正是"缺事件类规则必须降级"的那一档）。
    /// </summary>
    private static RuleContext LossyContext(RuleEngine engine, long lossEpoch = 3)
    {
        var ledger = new LossLedger();
        for (var i = 0; i < Math.Max(1, lossEpoch); i++)
            ledger.RecordDrop(DiagnosticBranches.Analyzer, DeliveryClass.Operational, 0, "synthetic-loss", evicted: false);
        return engine.CreateContext(new EvidenceCoverage(ledger), acceptanceWatermark: 1000);
    }

    // ────────────────────────── positive ──────────────────────────

    [Fact]
    public void PersistenceWriteFailureOpensAnIncidentWithFactsCandidatesAndChecks()
    {
        var engine = NewEngine(out var observed);
        var evt = Event(PersistenceEvents.WriteFailed, 1,
            new PstWritePayload("Receipt", "Move", DestinationExisted: true, ReasonCode: "IOException"),
            jobId: "JOB-1", objectId: "obj-7", exceptionType: "IOException");

        engine.Evaluate(evt, CompleteContext(engine));

        var incident = Assert.Single(engine.ActiveIncidents);
        Assert.Equal("PERSISTENCE_WRITE_FAILED", incident.RuleId);
        Assert.Equal("PERSISTENCE_WRITE_FAILED", incident.SymptomCode);
        Assert.Equal(DiagnosticLevel.Error, incident.Severity);      // Receipt ⇒ 数据可靠性风险
        Assert.Equal(ConfidenceBand.ConfirmedObservation, incident.Confidence);
        Assert.Equal(IncidentStatus.Open, incident.Status);
        Assert.NotEmpty(incident.Facts);
        Assert.Equal(evt.Ref, incident.Facts[0].Ref);                // 证据必须指向"这一次"事件
        Assert.Contains(incident.Candidates, c => c.CandidateCode == "file-locked-by-external-process");
        Assert.NotEmpty(incident.SuggestedChecks);
        Assert.Equal("Persistence.Move", incident.BreakPoint);
        Assert.Equal("JOB-1", incident.JobId);
        Assert.Equal("obj-7", incident.ObjectId);
        Assert.Single(observed);
        Assert.Equal(RuleEngine.IncidentLifecycle.Opened, observed[0].Lifecycle);
        Assert.DoesNotContain("IOException", incident.UserFacingSummary, StringComparison.Ordinal); // 人话里不塞异常原文
    }

    [Fact]
    public void JobStateWriteSkipIsWarningNotError()
    {
        var engine = NewEngine(out _);
        engine.Evaluate(Event(PersistenceEvents.WriteSkipped, 1,
            new PstWritePayload("JobState", "Move", true, "IOException"), jobId: "JOB-1"), CompleteContext(engine));

        var incident = Assert.Single(engine.ActiveIncidents);
        Assert.Equal(DiagnosticLevel.Warning, incident.Severity);     // 视图写失败 ≠ 数据可靠性风险
        Assert.Equal("PERSISTENCE_WRITE_SKIPPED", incident.SymptomCode);
    }

    [Fact]
    public void AccessDeniedAndSpaceExhaustedAndUnexpectedExitAreDetected()
    {
        var engine = NewEngine(out _);
        var ctx = CompleteContext(engine);

        engine.Evaluate(Event(NetEvents.SmbConnectFailed, 1, jobId: "JOB-1",
            win32: 5, errorDomain: ErrorDomain.Win32), ctx);
        engine.Evaluate(Event(RobocopyEvents.ErrorLinesAggregated, 2,
            new RbcErrorAggregatePayload(7, false, 1), jobId: "JOB-1", objectId: "obj-1",
            win32: 112, errorDomain: ErrorDomain.Win32), ctx);
        engine.Evaluate(Event(RobocopyEvents.ProcessExited, 3, jobId: "JOB-1", objectId: "obj-1",
            robocopyExitCode: 8, outcome: DiagnosticOutcome.Failed), ctx);

        var rules = engine.ActiveIncidents.Select(i => i.RuleId).Distinct().OrderBy(x => x).ToArray();
        Assert.Contains("ACCESS_DENIED", rules);
        Assert.Contains("TARGET_SPACE_EXHAUSTED", rules);
        Assert.Contains("ROBOCOPY_UNEXPECTED_EXIT", rules);
    }

    // ────────────────────────── negative ──────────────────────────

    [Fact]
    public void HealthyEventsAndBenignExitCodesProduceNoIncident()
    {
        var engine = NewEngine(out _);
        var ctx = CompleteContext(engine);

        engine.Evaluate(Event(PersistenceEvents.WriteSucceeded, 1,
            new PstWritePayload("Receipt", "Move", true, null), jobId: "JOB-1"), ctx);
        engine.Evaluate(Event(RobocopyEvents.ProcessExited, 2, jobId: "JOB-1", objectId: "obj-1",
            robocopyExitCode: 1, outcome: DiagnosticOutcome.Succeeded), ctx);   // 1 = 有文件成功（成功位掩码）
        engine.Evaluate(Event(RobocopyEvents.ProcessExited, 3, jobId: "JOB-1", objectId: "obj-2",
            robocopyExitCode: 3, outcome: DiagnosticOutcome.Succeeded), ctx);   // 1|2 = 成功+额外文件
        engine.Evaluate(Event(TransferEvents.ObjectCompleted, 4, jobId: "JOB-1", objectId: "obj-3"), ctx);

        Assert.Empty(engine.ActiveIncidents);
        Assert.Equal(0, engine.Stats().IncidentsOpened);
    }

    [Fact]
    public void AccessDeniedRuleDoesNotFireOnUnrelatedWin32Codes()
    {
        var engine = NewEngine(out _);
        // 67 = 找不到网络名（不是权限问题；产品里这类失败不阻断迁移）。
        engine.Evaluate(Event(NetEvents.SmbConnectFailed, 1, jobId: "JOB-1",
            win32: 67, errorDomain: ErrorDomain.Win32), CompleteContext(engine));
        Assert.Empty(engine.ActiveIncidents);
    }

    // ────────────────────────── loss（证据不足必须敢说不知道）──────────────────────────

    /// <summary>测试专用：一条依赖"没观察到"的规则（引擎的降级契约必须对它生效）。</summary>
    private sealed class AbsenceBasedTestRule : IDiagnosticRule
    {
        public string RuleId => "TEST_ABSENCE_RULE";
        public int Version => 1;
        public bool RequiresCompleteEvidence => true;
        public IReadOnlyCollection<string> WatchedEventCodes { get; } = new[] { TransferEvents.JobRunStarted.Name };

        public RuleOutcome? Evaluate(in DiagnosticEvent evt, RuleContext context)
        {
            var incident = new Incident(
                "TEST_ABSENCE_RULE|" + (evt.JobId ?? "-"), RuleId, Version, "TEST_ABSENCE", DiagnosticLevel.Warning,
                evt.TimestampUtc, evt.TimestampUtc, jobId: evt.JobId);
            incident.Merge(new IncidentUpdate
            {
                AtUtc = evt.TimestampUtc,
                Fact = new IncidentEvidence(evt.Ref, evt.Descriptor.Name, IncidentEvidence.Fact),
                Confidence = ConfidenceBand.Medium,
                ConfidenceRationale = "缺事件推断",
                UserFacingSummary = "疑似缺少预期反馈",
            });
            return new RuleOutcome(incident, IsNew: true, ShouldTriggerFlight: false);
        }
    }

    [Fact]
    public void AbsenceBasedRuleDegradesToInconclusiveWhenEvidenceIsLossy()
    {
        var engine = new RuleEngine(new RuleRegistry(new IDiagnosticRule[] { new AbsenceBasedTestRule() }), _ => null);
        engine.Evaluate(Event(TransferEvents.JobRunStarted, 1, jobId: "JOB-1"), LossyContext(engine, lossEpoch: 5));

        var incident = Assert.Single(engine.ActiveIncidents);
        Assert.Equal(IncidentStatus.Inconclusive, incident.Status);
        Assert.True(incident.EvidenceIncomplete);
        Assert.Equal(ConfidenceBand.Unknown, incident.Confidence);
        Assert.Contains("无法区分", incident.ConfidenceRationale, StringComparison.Ordinal);

        var missing = Assert.Single(incident.Missing);
        Assert.Equal("TEST_ABSENCE_RULE", missing.ContractId);
        Assert.Equal("evidence-loss", missing.ReasonCode);
        Assert.False(missing.CollectionHealthy);
        Assert.Equal(5, incident.LossEpoch);
        Assert.Equal(1, engine.Stats().IncidentsInconclusive);
    }

    /// <summary>反向：**关于降级本身**的规则在证据有损时仍必须给出确定结论。</summary>
    [Fact]
    public void DiagnosticsDegradedRuleStillConcludesWhenEvidenceIsLossy()
    {
        var engine = NewEngine(out _);
        engine.Evaluate(Event(DiagnosticsEvents.CriticalLost, 1,
            new DiaCriticalLostPayload(3, "reserve-exhausted", true)), LossyContext(engine, 7));

        var incident = Assert.Single(engine.ActiveIncidents);
        Assert.Equal("DIAGNOSTICS_DEGRADED", incident.RuleId);
        Assert.Equal(DiagnosticLevel.Error, incident.Severity);
        Assert.Equal(ConfidenceBand.ConfirmedObservation, incident.Confidence);   // 计数是直接量的
        Assert.True(incident.EvidenceIncomplete);                                 // 但它同时声明证据不完整
    }

    // ────────────────────────── late（迟到 = 修订，不是新卡）──────────────────────────

    [Fact]
    public void LateEventRevisesTheSameIncidentAndCooldownSuppressesStorms()
    {
        var engine = NewEngine(out var observed);
        var ctx = CompleteContext(engine);

        var t0 = new DateTimeOffset(2026, 9, 30, 6, 31, 32, TimeSpan.Zero);
        engine.Evaluate(Event(PersistenceEvents.WriteFailed, 1,
            new PstWritePayload("Receipt", "Move", true, "IOException"), jobId: "JOB-1", atUtc: t0), ctx);
        var afterFirst = Assert.Single(engine.ActiveIncidents).Revision;

        // 冷却窗口内的迟到/风暴：不新增卡、不刷版本，但要计数（可见）。
        engine.Evaluate(Event(PersistenceEvents.WriteFailed, 2,
            new PstWritePayload("Receipt", "Move", true, "IOException"), jobId: "JOB-1", atUtc: t0.AddMilliseconds(50)), ctx);
        Assert.Single(engine.ActiveIncidents);
        Assert.Equal(afterFirst, Assert.Single(engine.ActiveIncidents).Revision);
        Assert.True(engine.Stats().SuppressedByCooldown >= 1);

        // 冷却窗口之后：同一张卡被修订（Revision 增加），仍是一张卡。
        engine.Evaluate(Event(PersistenceEvents.WriteFailed, 3,
            new PstWritePayload("Receipt", "Move", true, "IOException"), jobId: "JOB-1", atUtc: t0.AddSeconds(5)), ctx);
        Assert.Single(engine.ActiveIncidents);
        Assert.True(Assert.Single(engine.ActiveIncidents).Revision > afterFirst);
        Assert.Equal(1, engine.Stats().IncidentsOpened);
        Assert.True(observed.Count >= 2);
    }

    // ────────────────────────── cancellation ──────────────────────────

    [Fact]
    public void CanceledOrKilledProcessIsNotAnUnexpectedExit()
    {
        var engine = NewEngine(out _);
        var ctx = CompleteContext(engine);

        engine.Evaluate(Event(RobocopyEvents.ProcessExited, 1, jobId: "JOB-1", objectId: "obj-1",
            robocopyExitCode: -1, outcome: DiagnosticOutcome.Canceled), ctx);
        engine.Evaluate(Event(RobocopyEvents.ProcessExited, 2, jobId: "JOB-1", objectId: "obj-2",
            robocopyExitCode: 16, outcome: DiagnosticOutcome.Canceled), ctx);   // 被我们强杀：不算非预期退出

        Assert.Empty(engine.ActiveIncidents);
    }

    [Fact]
    public void PausedRunIsRecordedAsAcceptedNotFailure()
    {
        var engine = NewEngine(out _);
        // 暂停/停止本身不产生事件卡（它们不是失败）。
        engine.Evaluate(Event(TransferEvents.Paused, 1, jobId: "JOB-1", outcome: DiagnosticOutcome.Accepted),
            CompleteContext(engine));
        engine.Evaluate(Event(TransferEvents.StopObserved, 2, jobId: "JOB-1", outcome: DiagnosticOutcome.Canceled),
            CompleteContext(engine));
        Assert.Empty(engine.ActiveIncidents);
    }

    // ────────────────────────── generation switch ──────────────────────────

    [Fact]
    public void DifferentRunGenerationsGetSeparateCardsAndLateEventOnlyTouchesItsOwn()
    {
        var engine = NewEngine(out _);
        var ctx = CompleteContext(engine);
        var t0 = new DateTimeOffset(2026, 9, 30, 6, 31, 32, TimeSpan.Zero);

        engine.Evaluate(Event(PersistenceEvents.WriteFailed, 1,
            new PstWritePayload("Receipt", "Move", true, "IOException"), jobId: "JOB-1", runGeneration: 1, atUtc: t0), ctx);
        engine.Evaluate(Event(PersistenceEvents.WriteFailed, 2,
            new PstWritePayload("Receipt", "Move", true, "IOException"), jobId: "JOB-1", runGeneration: 2, atUtc: t0.AddSeconds(1)), ctx);

        Assert.Equal(2, engine.ActiveIncidents.Count);
        var gen1 = engine.ActiveIncidents.Single(i => i.IncidentId.EndsWith("|g1", StringComparison.Ordinal));
        var gen2 = engine.ActiveIncidents.Single(i => i.IncidentId.EndsWith("|g2", StringComparison.Ordinal));
        Assert.Equal(1, gen1.RunGeneration);
        Assert.Equal(2, gen2.RunGeneration);

        var gen2RevisionBefore = gen2.Revision;

        // Run A 的迟到事件（gen=1）只能修订 A 的卡，绝不能碰 B 的卡。
        engine.Evaluate(Event(PersistenceEvents.WriteFailed, 3,
            new PstWritePayload("Receipt", "Move", true, "IOException"), jobId: "JOB-1", runGeneration: 1,
            atUtc: t0.AddSeconds(10)), ctx);

        Assert.Equal(2, engine.ActiveIncidents.Count);
        Assert.True(gen1.Revision > 1);
        Assert.Equal(gen2RevisionBefore, gen2.Revision);
    }

    // ────────────────────────── 引擎自身边界 ──────────────────────────

    [Fact]
    public void RuleFaultsAreIsolatedAndCounted()
    {
        var engine = new RuleEngine(new RuleRegistry(new IDiagnosticRule[] { new ThrowingRule() }), _ => null);
        engine.Evaluate(Event(TransferEvents.JobRunStarted, 1, jobId: "JOB-1"), CompleteContext(engine));

        Assert.Equal(1, engine.Stats().RuleFaults);
        Assert.Empty(engine.ActiveIncidents);
    }

    /// <summary>
    /// ★ R-2 收口（D6.3 剩余风险关闭轮）★ 隔离计数**不足以**说明发生过什么：
    /// 规则故障必须带着"哪条规则 + 什么异常"上报到诊断自身健康通道（runtime 把它接到健康计数器上），
    /// 否则"规则全炸"这件事永远出不了导出快照的 `rules.faults` 那一格。
    /// </summary>
    [Fact]
    public void RuleFaultIsReportedToTheSelfHealthChannelWithItsRuleId()
    {
        var engine = new RuleEngine(new RuleRegistry(new IDiagnosticRule[] { new ThrowingRule() }), _ => null);
        var reported = new List<string>();
        engine.OnRuleFault = reported.Add;

        engine.Evaluate(Event(TransferEvents.JobRunStarted, 1, jobId: "JOB-1"), CompleteContext(engine));

        Assert.Equal(1, engine.Stats().RuleFaults);
        var reason = Assert.Single(reported);
        Assert.Contains("rule:TEST_THROWING_RULE:", reason, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveIncidentsAreCappedInsteadOfGrowingUnbounded()
    {
        var engine = NewEngine(out _);
        var ctx = CompleteContext(engine);

        // 用不同的 objectId 造出远超上限的独立卡。
        for (var i = 0; i < 400; i++)
        {
            engine.Evaluate(Event(AccessDeniedRuleProbe, i + 1, jobId: "JOB-1", objectId: "obj-" + i,
                win32: 5, errorDomain: ErrorDomain.Win32), ctx);
        }

        Assert.True(engine.ActiveCount <= 256, $"活动卡上限必须生效，实际 {engine.ActiveCount}");
        Assert.True(engine.Stats().SuppressedByCap > 0, "被上限抑制的数量必须可见");
    }

    private static readonly EventDescriptor AccessDeniedRuleProbe = FsEvents.FileReadFailure;

    [Fact]
    public void ResolveRemovesCardAndRecordsTheRevision()
    {
        var engine = NewEngine(out var observed);
        engine.Evaluate(Event(PersistenceEvents.WriteFailed, 1,
            new PstWritePayload("Receipt", "Move", true, "IOException"), jobId: "JOB-1"), CompleteContext(engine));

        var incident = Assert.Single(engine.ActiveIncidents);
        Assert.True(engine.Resolve(incident.IncidentId, "artifact-written-after-retry",
            new DateTimeOffset(2026, 9, 30, 6, 40, 0, TimeSpan.Zero)));

        Assert.Empty(engine.ActiveIncidents);
        Assert.Equal(IncidentStatus.Resolved, incident.Status);
        Assert.Equal(1, engine.Stats().IncidentsResolved);
        Assert.Contains(observed, o => o.Lifecycle == RuleEngine.IncidentLifecycle.Resolved);
    }

    private sealed class ThrowingRule : IDiagnosticRule
    {
        public string RuleId => "TEST_THROWING_RULE";
        public int Version => 1;
        public bool RequiresCompleteEvidence => false;
        public IReadOnlyCollection<string> WatchedEventCodes { get; } = new[] { TransferEvents.JobRunStarted.Name };

        public RuleOutcome? Evaluate(in DiagnosticEvent evt, RuleContext context) =>
            throw new InvalidOperationException("规则内部故障必须被隔离");
    }

    /// <summary>
    /// 端到端：runtime 把引擎接到 analyzer 收件箱上 ⇒ 真实发布的一条事件能开出事件卡、
    /// 落成 DIA.IncidentOpened 事件、并触发一次飞行窗口（错误级）。
    /// </summary>
    [Fact]
    public void RuntimeWiresEngineToAnalyzerInboxAndPublishesIncidentLifecycle()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var runtime = DiagnosticRuntime.Start(D2TestSupport.Options(root), out _);
            try
            {
                runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                    PersistenceEvents.WriteFailed,
                    DiagnosticContext.Root(runtime.SessionId, "Test").WithJob("JOB-D4"),
                    new PstWritePayload("Receipt", "Move", DestinationExisted: true, ReasonCode: "IOException"),
                    Outcome: DiagnosticOutcome.Failed,
                    ExceptionType: "IOException"));

                Assert.True(D2TestSupport.WaitUntil(() => runtime.ActiveIncidents.Count > 0),
                    "规则引擎必须真的被接到 analyzer 收件箱上");

                var incident = runtime.ActiveIncidents.First();
                Assert.Equal("PERSISTENCE_WRITE_FAILED", incident.RuleId);
                Assert.Equal("JOB-D4", incident.JobId);
                Assert.True(incident.Facts.Count > 0);

                // 事件卡生命周期必须落成事件（离线包据此重建时间线）。
                Assert.True(D2TestSupport.WaitUntil(() =>
                {
                    runtime.TryGetViewerSnapshot(out var snapshot);
                    return snapshot.Any(e => e.Descriptor.Name == DiagnosticsEvents.IncidentOpened.Name);
                }), "必须发布 DIA.IncidentOpened");

                runtime.TryGetViewerSnapshot(out var events);
                var opened = events.Last(e => e.Descriptor.Name == DiagnosticsEvents.IncidentOpened.Name);
                var payload = Assert.IsType<DiaIncidentPayload>(opened.Payload);
                Assert.Equal("PERSISTENCE_WRITE_FAILED", payload.RuleId);
                Assert.Equal("ConfirmedObservation", payload.Confidence);
                Assert.False(payload.EvidenceIncomplete);

                // 错误级事件卡 ⇒ 触发飞行窗口（前后证据冻结）。
                var flight = runtime.FlightStatistics();
                Assert.NotNull(flight);
                Assert.True(flight!.Value.OpenWindows + flight.Value.SealedWindows + flight.Value.PersistedWindows >= 1,
                    "错误级事件卡必须触发飞行窗口");

                var stats = runtime.RuleEngineStatistics();
                Assert.True(stats.EventsEvaluated > 0);
                Assert.Equal(1, stats.IncidentsOpened);
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

    // ────────────────────────── registry 本身 ──────────────────────────

    [Fact]
    public void RegistryDispatchesByStableEventCodeAndRejectsDuplicates()
    {
        var registry = RuleRegistry.CreateDefault();
        Assert.Equal(8, registry.Count);   // 5 条首批 + FILE_LOCKED + PREVIOUS_SESSION_UNCLEAN + ACTION_FULFILLMENT_FAILED(§6)
        Assert.Contains(registry.ForEventName(PersistenceEvents.WriteFailed.Name), r => r.RuleId == "PERSISTENCE_WRITE_FAILED");
        Assert.Empty(registry.ForEventName("NO.SUCH.EVENT"));

        // 按描述符分派也必须命中（内部解析名称，避免"按代码查名称表"的错配）。
        Assert.NotEmpty(registry.ForEventDescriptor(PersistenceEvents.WriteFailed));
        Assert.Empty(registry.ForEventDescriptor(TransferEvents.ProgressObserved));

        Assert.Throws<InvalidOperationException>(() =>
            new RuleRegistry(new IDiagnosticRule[] { new AbsenceBasedTestRule(), new AbsenceBasedTestRule() }));
    }
}