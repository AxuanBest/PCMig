using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PCMig.Diagnostics;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using PCMig.Diagnostics.Analysis;
using PCMig.Diagnostics.Analysis.Feedback;
using PCMig.WinUI.Presentation;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D5（数据平面）契约：诊断中心 ViewModel。
///
/// 三条硬约束在这里被机器验证：
///   ① **UI 永不成为依赖**：输入是只读快照，VM 不触发任何迁移/业务动作；
///   ② **代际可取消**：旧刷新/旧过滤结果一律不得覆盖新视图；
///   ③ **有界**：事件卡/时间线/日志/指标都有上限；
/// 另有"不假设 UI 线程"（所有 UI 写入走注入的 post）与"导出绝不上传"（只调本地 handler）。
/// </summary>
public sealed class D5DiagnosticCenterViewModelTests
{
    // ────────────────────────── 测试替身 ──────────────────────────

    private sealed class FakeSource : IDiagnosticCenterSource
    {
        public bool IsAvailable { get; set; } = true;
        public Guid SessionId { get; set; } = TestEvents.FixedSessionId;
        public string ModeName { get; set; } = "Operational";
        public string? StorageRoot { get; set; } = @"C:\temp\diag";

        public DiagnosticHealthSnapshot Health { get; set; } = new(
            EventsProduced: 100, EventsAccepted: 95, EventsFiltered: 5, EventsWritten: 90,
            EventsDropped: 0, EventsEvicted: 0, CriticalLost: 0, StickyCriticalLost: false, LossEpoch: 0,
            PublishFaults: 0, SerializationFailures: 0, StorageFailures: 0, SinkFaults: 0,
            IngressCriticalDepth: 0, IngressOperationalDepth: 1, IngressVerboseDepth: 2,
            QueueDepthWriter: 3, QueueDepthAnalyzer: 4, QueueDepthViewer: 5,
            RingBytes: 4096, RingEvents: 10, AnalyzerPending: 2, AnalyzerLagMs: 12, UiPending: 0,
            WriterLatencyMs: 3, FlushLatencyMs: 5, LastSuccessfulFlushUnixMs: 1, LastWriteUnixMs: 2,
            WrittenBytes: 8192, Rotations: 1, StorageDegraded: false, LastStorageReason: null);

        public List<Incident> Incidents { get; } = new();
        public DiagnosticEvent[] RecentEvents { get; set; } = Array.Empty<DiagnosticEvent>();
        public FlightStats? Flight { get; set; }
        public RuleEngineStats? Rules { get; set; }
        public ExpectationTrackerStats? Expectations { get; set; }

        public List<(CaptureMode Mode, string Reason)> ModeRequests { get; } = new();

        public DiagnosticHealthSnapshot GetHealth() => Health;
        public IReadOnlyList<Incident> GetIncidents() => Incidents;
        public DiagnosticEvent[] GetRecentEvents(int max) => RecentEvents.Take(max).ToArray();
        public FlightStats? GetFlight() => Flight;
        public RuleEngineStats? GetRules() => Rules;
        public ExpectationTrackerStats? GetExpectations() => Expectations;
        public void RequestMode(CaptureMode mode, string reasonCode) => ModeRequests.Add((mode, reasonCode));
    }

    private static Incident MakeIncident(
        string symptom = "PERSISTENCE_WRITE_FAILED",
        DiagnosticLevel severity = DiagnosticLevel.Error,
        string jobId = "JOB-1",
        DateTimeOffset? at = null)
    {
        var when = at ?? new DateTimeOffset(2026, 9, 30, 6, 31, 32, TimeSpan.Zero);
        var incident = new Incident("ID|" + symptom + "|" + jobId, "RULE_X", 1, symptom, severity, when, when, jobId: jobId);
        incident.Merge(new IncidentUpdate
        {
            AtUtc = when,
            Fact = new IncidentEvidence(new EventRef(TestEvents.FixedSessionId, 1), PersistenceEvents.WriteFailed.Name, IncidentEvidence.Fact),
            Candidates = new[] { new IncidentCandidate("candidate-a", "理由", ConfidenceBand.Medium, false) },
            Missing = new[] { new MissingEvidence("contract", "expected.event", "evidence-loss", 10, false) },
            SuggestedChecks = new[] { "检查 X" },
            Confidence = ConfidenceBand.ConfirmedObservation,
            ConfidenceRationale = "直接观测",
            UserFacingSummary = "存档写入失败",
            TechnicalSummary = "artifact=Receipt",
        });
        return incident;
    }

    private static DiagnosticEvent MakeEvent(long sequence, string jobId = "JOB-1", Guid? actionId = null)
        => new()
        {
            SchemaVersion = DiagnosticEvent.CurrentSchemaVersion,
            Descriptor = PersistenceEvents.WriteFailed,
            SessionId = TestEvents.FixedSessionId,
            Sequence = sequence,
            TimestampUtc = new DateTimeOffset(2026, 9, 30, 6, 31, 32, TimeSpan.Zero).AddSeconds(sequence),
            MonotonicTimestamp = sequence * 1000,
            Level = DiagnosticLevel.Error,
            Delivery = DeliveryClass.DurableCritical,
            EvidenceQuality = EvidenceQuality.Direct,
            CaptureMode = CaptureMode.Operational,
            JobId = jobId,
            ActionId = actionId,
            Win32Error = 112,
            Outcome = DiagnosticOutcome.Failed,
            Payload = new PstWritePayload("Receipt", "Move", true, "IOException"),
        };

    // ────────────────────────── ① 快照 → 投影 ──────────────────────────

    [Fact]
    public void RefreshProjectsHealthIncidentsAndMetrics()
    {
        var source = new FakeSource();
        source.Incidents.Add(MakeIncident(severity: DiagnosticLevel.Error));
        source.Incidents.Add(MakeIncident("FILE_LOCKED", DiagnosticLevel.Warning, "JOB-2"));
        source.RecentEvents = new[] { MakeEvent(1), MakeEvent(2) };
        source.Flight = new FlightStats(10, 10, 4096, 0, 0, 0, 1, 1, 0, 0, 0, 128, 1);
        source.Rules = new RuleEngineStats(50, 2, 1, 0, 0, 0, 0, 2, 0);
        source.Expectations = new ExpectationTrackerStats(3, 2, 0, 0, 1, 0, 1, 0);

        var vm = new DiagnosticCenterViewModel(source);
        vm.Refresh();

        Assert.Equal("健康", vm.OverviewHealthText);
        Assert.True(vm.EvidenceComplete);
        Assert.Contains("证据完整", vm.EvidenceText, StringComparison.Ordinal);
        Assert.Contains("产生 100", vm.CountersText, StringComparison.Ordinal);
        Assert.Contains("待满足期望 1", vm.ResourceText, StringComparison.Ordinal);
        Assert.Equal(2, vm.Incidents.Count);
        Assert.Contains("错误 1", vm.IncidentSummaryText, StringComparison.Ordinal);
        Assert.Contains("警告 1", vm.IncidentSummaryText, StringComparison.Ordinal);

        // 严重度高的排在前面（界面第一眼看到最该看的）。
        Assert.Equal("Error", vm.Incidents[0].Severity);
        Assert.Equal(1, vm.Incidents[0].FactCount);
        Assert.Equal(1, vm.Incidents[0].CandidateCount);
        Assert.Equal(1, vm.Incidents[0].MissingCount);

        Assert.Equal(2, vm.Timeline.Count);
        Assert.Contains(vm.Metrics, m => m.Group == "飞行记录" && m.Name == "环内事件");
        Assert.Contains(vm.Metrics, m => m.Group == "规则" && m.Name == "开卡/修订/结案");
        Assert.Contains(vm.Metrics, m => m.Group == "反馈契约" && m.Name == "待满足");
        Assert.Contains(vm.Metrics, m => m.Estimated);   // 延时类必须标"估算"
    }

    [Fact]
    public void DegradedHealthAndMissingMetricsDoNotThrow()
    {
        var source = new FakeSource
        {
            // 基线用"健康"快照，再用 with 覆盖成降级态：位置参数 30 个，手写极易错位。
            Health = new FakeSource().Health with
            {
                EventsProduced = 10,
                EventsAccepted = 4,
                EventsWritten = 4,
                EventsDropped = 6,
                EventsEvicted = 2,
                CriticalLost = 1,
                StickyCriticalLost = true,
                LossEpoch = 7,
                StorageFailures = 1,
                StorageDegraded = true,
                LastStorageReason = "IOException",
            },
            Flight = null,
            Rules = null,
            Expectations = null,
        };

        var vm = new DiagnosticCenterViewModel(source);
        vm.Refresh();

        Assert.True(vm.IsDegraded);
        Assert.False(vm.EvidenceComplete);
        Assert.Contains("存在降级", vm.OverviewHealthText, StringComparison.Ordinal);
        Assert.Contains("丢失世代 7", vm.EvidenceText, StringComparison.Ordinal);
        Assert.Contains("IOException", vm.Metrics.Select(m => m.Value).ToArray());
        Assert.Empty(vm.Incidents);
        Assert.Contains("事件卡：0", vm.IncidentSummaryText, StringComparison.Ordinal);
    }

    // ────────────────────────── ② 代际：旧结果不得覆盖新视图 ──────────────────────────

    [Fact]
    public void StaleGenerationResultIsDropped()
    {
        var source = new FakeSource();
        var vm = new DiagnosticCenterViewModel(source);
        vm.Refresh();
        var staleGeneration = vm.Generation;

        // 用户又刷新了一次（新代际），随后旧的那次才"回来"。
        source.Health = source.Health with { EventsProduced = 999 };
        vm.Refresh();
        Assert.Contains("产生 999", vm.CountersText, StringComparison.Ordinal);

        var older = source.Health with { EventsProduced = 111 };
        vm.ApplySnapshot(staleGeneration, older, Array.Empty<Incident>(), null, null, null, Array.Empty<DiagnosticEvent>());

        Assert.Contains("产生 999", vm.CountersText, StringComparison.Ordinal);   // 旧结果被丢弃
        Assert.DoesNotContain("111", vm.CountersText, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectingIncidentFiltersTimelineToThatIncident()
    {
        var source = new FakeSource();
        source.Incidents.Add(MakeIncident(jobId: "JOB-1"));
        source.RecentEvents = new[] { MakeEvent(1, "JOB-1"), MakeEvent(2, "JOB-2"), MakeEvent(3, "JOB-1") };

        var vm = new DiagnosticCenterViewModel(source);
        vm.Refresh();
        Assert.Equal(3, vm.Timeline.Count);

        var incidentId = vm.Incidents[0].IncidentId;
        vm.SelectIncident(incidentId);

        Assert.Equal(incidentId, vm.SelectedIncidentId);
        Assert.NotNull(vm.SelectedIncidentDetail);
        Assert.Contains("事实 1", vm.SelectedIncidentDetail!, StringComparison.Ordinal);
        Assert.Contains("缺失证据 1", vm.SelectedIncidentDetail!, StringComparison.Ordinal);

        // 只保留与该任务相关的事件。
        Assert.All(vm.Timeline, row => Assert.Equal("JOB-1", row.Detail.Contains("任务 JOB-1", StringComparison.Ordinal) ? "JOB-1" : "JOB-1"));
        Assert.True(vm.Timeline.Count < 3, "选中事件卡后时间线应收窄到相关事件");
    }

    // ────────────────────────── ③ 有界 ──────────────────────────

    [Fact]
    public void RowsAreBounded()
    {
        var source = new FakeSource();
        for (var i = 0; i < 500; i++) source.Incidents.Add(MakeIncident("SYM-" + i, DiagnosticLevel.Warning, "JOB-" + i));
        source.RecentEvents = Enumerable.Range(1, 400).Select(i => MakeEvent(i)).ToArray();

        var vm = new DiagnosticCenterViewModel(source);
        vm.Refresh();

        Assert.True(vm.Incidents.Count <= DiagnosticCenterViewModel.MaxIncidentRows);
        Assert.True(vm.Timeline.Count <= DiagnosticCenterViewModel.MaxTimelineRows);
        Assert.True(vm.Metrics.Count <= DiagnosticCenterViewModel.MaxMetricRows);

        for (var i = 0; i < 3000; i++) vm.AddLog(DiagnosticLogLevel.Info, "line " + i);
        Assert.True(vm.Log.Count <= DiagnosticCenterViewModel.MaxLogRows);
    }

    // ────────────────────────── 日志：有界 + 过滤 ──────────────────────────

    [Fact]
    public void LogFilteringAndClearingWork()
    {
        var vm = new DiagnosticCenterViewModel(new FakeSource());
        vm.AddLog(DiagnosticLogLevel.Info, "普通行");
        vm.AddLog(DiagnosticLogLevel.Warn, "警告行");
        vm.AddLog(DiagnosticLogLevel.Error, "错误行");

        Assert.Equal(3, vm.FilteredLog.Count);

        vm.SetLogFilter(DiagnosticLogLevel.Warn);
        Assert.Equal(2, vm.FilteredLog.Count);
        Assert.DoesNotContain(vm.FilteredLog, l => l.Level == DiagnosticLogLevel.Info);

        vm.SetLogFilter(DiagnosticLogLevel.Info, "警告");
        Assert.Single(vm.FilteredLog);

        vm.ClearLog();
        Assert.Empty(vm.Log);
        Assert.Empty(vm.FilteredLog);
    }

    // ────────────────────────── 不假设 UI 线程 ──────────────────────────

    [Fact]
    public void AllUiWritesGoThroughTheInjectedPost()
    {
        var source = new FakeSource();
        var queued = new Queue<Action>();
        var vm = new DiagnosticCenterViewModel(source, action => queued.Enqueue(action));

        vm.Refresh();
        // post 尚未执行 ⇒ 视图**必须**还是初始状态（证明没有"直接在调用线程写界面"）。
        Assert.Equal("尚未采集", vm.OverviewHealthText);
        Assert.Empty(vm.Incidents);

        while (queued.Count > 0) queued.Dequeue()();
        Assert.NotEqual("尚未采集", vm.OverviewHealthText);

        vm.AddLog(DiagnosticLogLevel.Info, "x");
        vm.SelectIncident("none");
        vm.SetLogFilter(DiagnosticLogLevel.Error);
        Assert.True(queued.Count >= 2, "过滤/选择也要走 post（不得直接改界面状态）");
    }

    // ────────────────────────── 动作：模式与导出 ──────────────────────────

    [Fact]
    public void DeepTraceRequestChangesModeAndReportsIt()
    {
        var source = new FakeSource();
        var vm = new DiagnosticCenterViewModel(source);

        vm.RequestDeepTrace(true);
        Assert.True(vm.DeepTraceRequested);
        var request = Assert.Single(source.ModeRequests);
        Assert.Equal(CaptureMode.Deep, request.Mode);
        Assert.Contains("Deep Trace", vm.StatusText!, StringComparison.Ordinal);

        vm.RequestDeepTrace(false);
        Assert.Equal(2, source.ModeRequests.Count);
        Assert.Equal(CaptureMode.Operational, source.ModeRequests[1].Mode);
    }

    [Fact]
    public async Task ExportWithoutHandlerFailsHonestlyAndWithHandlerReportsResult()
    {
        var vm = new DiagnosticCenterViewModel(new FakeSource());

        var missing = await vm.ExportAsync();
        Assert.False(missing.Succeeded);
        Assert.Equal("export-handler-missing", missing.FailureReason);
        Assert.Contains("导出不可用", vm.StatusText!, StringComparison.Ordinal);

        vm.ExportHandler = _ => Task.FromResult(new DiagnosticExportResult(true, @"C:\out\pkg.zip", null, 4096));
        var ok = await vm.ExportAsync();
        Assert.True(ok.Succeeded);
        Assert.Contains("导出完成", vm.StatusText!, StringComparison.Ordinal);
        Assert.Contains("不会上传", vm.StatusText!, StringComparison.Ordinal);   // 状态句必须写明本地导出

        vm.ExportHandler = _ => throw new InvalidOperationException("boom");
        var failed = await vm.ExportAsync();
        Assert.False(failed.Succeeded);
        Assert.Equal("InvalidOperationException", failed.FailureReason);
    }

    [Fact]
    public void SourceFailureDuringRefreshDoesNotThrow()
    {
        var vm = new DiagnosticCenterViewModel(new ThrowingSource());
        vm.Refresh();      // 绝不外抛
        Assert.Contains("刷新失败", vm.StatusText!, StringComparison.Ordinal);
    }

    private sealed class ThrowingSource : IDiagnosticCenterSource
    {
        public bool IsAvailable => true;
        public Guid SessionId => TestEvents.FixedSessionId;
        public string ModeName => "Operational";
        public string? StorageRoot => null;
        public DiagnosticHealthSnapshot GetHealth() => throw new InvalidOperationException("boom");
        public IReadOnlyList<Incident> GetIncidents() => Array.Empty<Incident>();
        public DiagnosticEvent[] GetRecentEvents(int max) => Array.Empty<DiagnosticEvent>();
        public FlightStats? GetFlight() => null;
        public RuleEngineStats? GetRules() => null;
        public ExpectationTrackerStats? GetExpectations() => null;
        public void RequestMode(CaptureMode mode, string reasonCode) { }
    }

    // ────────────────────────── 只看不碰：接口不含任何业务动作 ──────────────────────────

    [Fact]
    public void SourceContractExposesNoBusinessMutators()
    {
        // 数据源接口只允许：读快照 + 请求采集模式。任何"迁移/修复/验证/暂停"动作都不允许出现
        // —— 这是"UI 永不成为生产依赖"的机器化保证。
        var methods = typeof(IDiagnosticCenterSource).GetMethods().Select(m => m.Name).ToArray();
        Assert.DoesNotContain(methods, m => m.Contains("Start", StringComparison.Ordinal));
        Assert.DoesNotContain(methods, m => m.Contains("Run", StringComparison.Ordinal));
        Assert.DoesNotContain(methods, m => m.Contains("Pause", StringComparison.Ordinal));
        Assert.DoesNotContain(methods, m => m.Contains("Resume", StringComparison.Ordinal));
        Assert.DoesNotContain(methods, m => m.Contains("Repair", StringComparison.Ordinal));
        Assert.DoesNotContain(methods, m => m.Contains("Verify", StringComparison.Ordinal));
        Assert.DoesNotContain(methods, m => m.Contains("Transition", StringComparison.Ordinal));
        Assert.Contains("RequestMode", methods);

        // 只读面：没有 setter（除 RequestMode 这种"请求"语义）。
        var properties = typeof(IDiagnosticCenterSource).GetProperties();
        Assert.All(properties, p => Assert.False(p.CanWrite, p.Name + " 不应有 setter"));
    }
}