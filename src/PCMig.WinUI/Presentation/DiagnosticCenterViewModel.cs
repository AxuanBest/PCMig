using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PCMig.Diagnostics;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Analysis;
using PCMig.Diagnostics.Analysis.Feedback;

namespace PCMig.WinUI.Presentation;

/// <summary>日志行级别（诊断中心自己的枚举：**不复用** MigrationSessionViewModel 里的类型，
/// 这样本文件可以被独立链入测试项目，而不必拖进那份 2900 行的会话 VM）。</summary>
public enum DiagnosticLogLevel
{
    Info = 0,
    Warn = 1,
    Error = 2,
}

/// <summary>事件卡的一行（供列表绑定）。Meta 把"严重度/状态/置信度/时间"合成一行，
/// 既少控件（绑定更轻），也让模板只需要一个语义样式（PMML：不得内联字号）。</summary>
public sealed record IncidentRow(
    string IncidentId,
    string RuleId,
    string SymptomCode,
    string Severity,
    string Status,
    string Confidence,
    string Summary,
    int FactCount,
    int CandidateCount,
    int MissingCount,
    string LastSeenText,
    bool EvidenceIncomplete)
{
    public string Meta => $"{Severity} · {Status} · 置信 {Confidence} · 事实 {FactCount}/候选 {CandidateCount}/缺失 {MissingCount} · {LastSeenText}";
}

/// <summary>因果时间线的一行。</summary>
public sealed record TimelineRow(
    long Sequence,
    string EventCode,
    string EventName,
    string Category,
    string Level,
    string Outcome,
    string Detail,
    bool IsEvidence);

/// <summary>指标/计数的一行（单位与"是否估算"必须显式呈现）。</summary>
public sealed record MetricRow(string Group, string Name, string Value, string Unit, bool Estimated);

/// <summary>日志行。</summary>
public sealed record DiagnosticLogRow(DiagnosticLogLevel Level, string Text);

/// <summary>导出结果（D6 提供实现；VM 只依赖这个结果形状）。</summary>
public readonly record struct DiagnosticExportResult(bool Succeeded, string? Path, string? FailureReason, long Bytes);

/// <summary>
/// 诊断中心的数据源抽象：**UI 只依赖它**，不直接摸运行时内部。
/// 这样 VM 可以在无 WinUI/无磁盘的条件下被完整测试（链入测试项目）。
/// </summary>
public interface IDiagnosticCenterSource
{
    bool IsAvailable { get; }

    Guid SessionId { get; }

    string ModeName { get; }

    string? StorageRoot { get; }

    DiagnosticHealthSnapshot GetHealth();

    IReadOnlyList<Incident> GetIncidents();

    DiagnosticEvent[] GetRecentEvents(int max);

    FlightStats? GetFlight();

    RuleEngineStats? GetRules();

    ExpectationTrackerStats? GetExpectations();

    /// <summary>请求切换采集模式（Deep 必须由用户显式开启）。</summary>
    void RequestMode(CaptureMode mode, string reasonCode);
}

/// <summary>
/// 诊断中心 ViewModel（D5 的数据平面）。
///
/// 纪律（方案 §23/§27）：
///   · **UI 永不成为生产/规则依赖**：这里只读快照，不触发迁移、不改任何业务状态；
///   · **有界**：事件卡/时间线/日志/指标行数都有上限，绝不把全部历史拉进内存；
///   · **代际可取消**：每次刷新带 generation，旧结果（用户已切 job/切卡/清空）一律丢弃，
///     避免"A 的迟到结果覆盖 B 的界面"；
///   · **不假设 UI 线程**：所有 UI 写入统一走注入的 post 回调（测试里是同步执行）；
///   · 日志走**受限容量 + 过滤**，过滤带 generation（旧过滤结果不得覆盖新视图）。
/// </summary>
public sealed class DiagnosticCenterViewModel : ObservableObject
{
    public const int MaxIncidentRows = 200;
    public const int MaxTimelineRows = 120;
    public const int MaxLogRows = 2000;
    public const int MaxMetricRows = 40;

    private readonly IDiagnosticCenterSource _source;
    private readonly Action<Action> _post;

    private readonly List<IncidentRow> _incidents = new();
    private readonly List<TimelineRow> _timeline = new();
    private readonly List<MetricRow> _metrics = new();
    private readonly List<DiagnosticLogRow> _log = new();

    private long _generation;
    private long _logGeneration;
    private string? _selectedIncidentId;
    private string? _statusText;
    private DiagnosticLogLevel _logFilterMin = DiagnosticLogLevel.Info;
    private string _logFilterText = string.Empty;
    private bool _autoScroll = true;
    private bool _deepTraceRequested;
    private long _lastLoggedSequence;

    public DiagnosticCenterViewModel(IDiagnosticCenterSource source, Action<Action>? post = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _post = post ?? (action => action());
    }

    /// <summary>导出钩子（由 D6 的导出服务注入；为 null 时界面显示"导出不可用"）。</summary>
    public Func<CancellationToken, Task<DiagnosticExportResult>>? ExportHandler { get; set; }

    public bool IsAvailable => _source.IsAvailable;

    public Guid SessionId => _source.SessionId;

    public string SessionShortId => _source.SessionId == Guid.Empty ? "—" : DiagnosticId.Short(_source.SessionId);

    public string ModeName => _source.ModeName;

    public string StorageRootText => string.IsNullOrWhiteSpace(_source.StorageRoot) ? "（无磁盘存储：仅内存采集）" : _source.StorageRoot!;

    public bool IsDegraded { get; private set; }

    public bool EvidenceComplete { get; private set; }

    public string OverviewHealthText { get; private set; } = "尚未采集";

    public string EvidenceText { get; private set; } = "—";

    public string CountersText { get; private set; } = "—";

    public string ResourceText { get; private set; } = "—";

    public string IncidentSummaryText { get; private set; } = "事件卡：0";

    public string? SelectedIncidentId => _selectedIncidentId;

    public string? SelectedIncidentDetail { get; private set; }

    public string? StatusText => _statusText;

    public bool AutoScroll
    {
        get => _autoScroll;
        set { if (_autoScroll != value) { _autoScroll = value; Raise(nameof(AutoScroll)); } }
    }

    public bool DeepTraceRequested
    {
        get => _deepTraceRequested;
        private set { if (_deepTraceRequested != value) { _deepTraceRequested = value; Raise(nameof(DeepTraceRequested)); } }
    }

    public string LogFilterText => _logFilterText;

    public DiagnosticLogLevel LogFilterMin => _logFilterMin;

    public IReadOnlyList<IncidentRow> Incidents => _incidents;

    public IReadOnlyList<TimelineRow> Timeline => _timeline;

    public IReadOnlyList<MetricRow> Metrics => _metrics;

    public IReadOnlyList<DiagnosticLogRow> Log => _log;

    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>
    /// 刷新全部投影（快照 → 视图）。**任何异常都不得外抛**：诊断中心自己坏掉不能拖垮界面。
    /// </summary>
    public void Refresh()
    {
        var generation = Interlocked.Increment(ref _generation);
        try
        {
            var health = _source.GetHealth();
            var incidents = _source.GetIncidents();
            var flight = _source.GetFlight();
            var rules = _source.GetRules();
            var expectations = _source.GetExpectations();
            var events = _source.GetRecentEvents(MaxTimelineRows);

            _post(() => ApplySnapshot(generation, health, incidents, flight, rules, expectations, events));
        }
        catch (Exception ex)
        {
            _post(() => SetStatus("刷新失败：" + ex.GetType().Name));
        }
    }

    /// <summary>把一份快照应用到视图（**代际不符即丢弃**）。internal 以便测试直接验证代际契约。</summary>
    internal void ApplySnapshot(
        long generation,
        DiagnosticHealthSnapshot health,
        IReadOnlyList<Incident> incidents,
        FlightStats? flight,
        RuleEngineStats? rules,
        ExpectationTrackerStats? expectations,
        DiagnosticEvent[] recentEvents)
    {
        if (generation != Interlocked.Read(ref _generation)) return;   // 旧结果：丢弃，不覆盖新视图

        IsDegraded = health.IsDegraded;
        EvidenceComplete = health.EvidenceComplete;
        Raise(nameof(IsDegraded));
        Raise(nameof(EvidenceComplete));

        OverviewHealthText = health.IsDegraded
            ? "存在降级：结论仍可用，但「没有观察到」不等于「没有发生」"
            : "健康";
        EvidenceText = health.EvidenceComplete
            ? "证据完整（无丢失）"
            : $"证据不完整：丢失世代 {health.LossEpoch}，丢弃 {health.EventsDropped}，替换 {health.EventsEvicted}";
        CountersText = $"产生 {health.EventsProduced} · 接受 {health.EventsAccepted} · 落盘 {health.EventsWritten} · " +
                       $"过滤 {health.EventsFiltered} · 关键丢失 {health.CriticalLost}";
        ResourceText = $"队列 写入 {health.QueueDepthWriter}/分析 {health.QueueDepthAnalyzer}/显示 {health.QueueDepthViewer} · " +
                       $"环 {health.RingBytes / 1024}KiB/{health.RingEvents} 条 · 分析滞后 {health.AnalyzerLagMs}ms · " +
                       $"待满足期望 {expectations?.Pending ?? 0}";

        RebuildIncidentRows(incidents);
        RebuildTimeline(recentEvents);
        RebuildMetrics(health, flight, rules, expectations);
        FeedLogFromEvents(recentEvents);
        RaiseProjectionChanged();
    }

    /// <summary>
    /// 把**新出现**的事件追加进实时日志（按 Sequence 去重，只增量）。
    /// 这样"实时日志"面板不需要额外的事件泵：刷新节拍本身就是泵，且日志仍然有界。
    /// </summary>
    private void FeedLogFromEvents(DiagnosticEvent[] events)
    {
        if (events.Length == 0) return;

        var appended = false;
        foreach (var evt in events.OrderBy(e => e.Sequence))
        {
            if (evt.Sequence <= _lastLoggedSequence) continue;
            _lastLoggedSequence = evt.Sequence;

            var level = evt.Level >= DiagnosticLevel.Error ? DiagnosticLogLevel.Error
                : evt.Level >= DiagnosticLevel.Warning ? DiagnosticLogLevel.Warn
                : DiagnosticLogLevel.Info;

            _log.Add(new DiagnosticLogRow(level, $"〔{evt.Descriptor.Code}〕{evt.Descriptor.Name} · {Describe(evt)}"));
            appended = true;
        }

        if (!appended) return;
        while (_log.Count > MaxLogRows) _log.RemoveAt(0);
        Raise(nameof(Log));
        Raise(nameof(FilteredLog));
    }

    private void RebuildIncidentRows(IReadOnlyList<Incident> incidents)
    {
        _incidents.Clear();
        foreach (var incident in incidents
                     .OrderByDescending(i => i.Severity)
                     .ThenByDescending(i => i.LastSeenUtc)
                     .Take(MaxIncidentRows))
        {
            _incidents.Add(new IncidentRow(
                incident.IncidentId,
                incident.RuleId,
                incident.SymptomCode,
                incident.Severity.ToString(),
                incident.Status.ToString(),
                incident.Confidence.ToString(),
                incident.UserFacingSummary,
                incident.Facts.Count,
                incident.Candidates.Count,
                incident.Missing.Count,
                incident.LastSeenUtc.ToLocalTime().ToString("HH:mm:ss"),
                incident.EvidenceIncomplete));
        }

        IncidentSummaryText = _incidents.Count == 0
            ? "事件卡：0（没有发现问题，或尚未开始采集）"
            : $"事件卡：{_incidents.Count}（错误 {_incidents.Count(i => i.Severity == "Error")}，警告 {_incidents.Count(i => i.Severity == "Warning")}）";

        Raise(nameof(Incidents));
    }

    private void RebuildTimeline(DiagnosticEvent[] events)
    {
        _timeline.Clear();

        var selected = _selectedIncidentId is null
            ? null
            : _incidents.FirstOrDefault(i => i.IncidentId == _selectedIncidentId);

        // 默认展示最近事件的因果时间线；选中事件卡时优先展示与它相关的事件（按 ActionId/JobId 关联）。
        var relevant = selected is null
            ? events
            : events.Where(e => MatchesIncident(e, selected.IncidentId)).ToArray();

        if (selected is not null && relevant.Length == 0) relevant = events;

        foreach (var evt in relevant.OrderBy(e => e.Sequence).TakeLast(MaxTimelineRows))
        {
            _timeline.Add(new TimelineRow(
                evt.Sequence,
                evt.Descriptor.Code,
                evt.Descriptor.Name,
                evt.Descriptor.Category.ToString(),
                evt.Level.ToString(),
                evt.Outcome?.ToString() ?? "—",
                Describe(evt),
                IsEvidenceEvent(evt)));
        }

        Raise(nameof(Timeline));
    }

    private static bool MatchesIncident(DiagnosticEvent evt, string incidentId)
    {
        if (incidentId.Contains(DiagnosticId.Format(evt.SessionId), StringComparison.Ordinal) && evt.ActionId is { } actionId)
            return incidentId.Contains(DiagnosticId.Format(actionId), StringComparison.Ordinal);
        if (evt.JobId is { } jobId && incidentId.Contains(jobId, StringComparison.Ordinal)) return true;
        if (evt.ObjectId is { } objectId && incidentId.Contains(objectId, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>低价值高频事件不进入"证据"高亮（避免时间线被逐文件噪声淹没）。</summary>
    private static bool IsEvidenceEvent(DiagnosticEvent evt) =>
        evt.Delivery != DeliveryClass.Verbose && evt.Level >= DiagnosticLevel.Warning;

    private static string Describe(DiagnosticEvent evt)
    {
        var parts = new List<string>(4);
        if (evt.ControlId is not null) parts.Add("控件 " + evt.ControlId);
        if (evt.JobId is not null) parts.Add("任务 " + evt.JobId);
        if (evt.ObjectId is not null) parts.Add("对象 " + evt.ObjectId);
        if (evt.RobocopyExitCode is not null) parts.Add("退出码 " + evt.RobocopyExitCode);
        if (evt.Win32Error is not null) parts.Add("win32 " + evt.Win32Error);
        if (evt.DurationMs is not null) parts.Add(evt.DurationMs + "ms");
        if (evt.Payload is not null) parts.Add(evt.Payload.PayloadName);
        return parts.Count == 0 ? "—" : string.Join(" · ", parts);
    }

    private void RebuildMetrics(
        DiagnosticHealthSnapshot health,
        FlightStats? flight,
        RuleEngineStats? rules,
        ExpectationTrackerStats? expectations)
    {
        _metrics.Clear();

        void Add(string group, string name, string value, string unit = "", bool estimated = false)
        {
            if (_metrics.Count >= MaxMetricRows) return;
            _metrics.Add(new MetricRow(group, name, value, unit, estimated));
        }

        Add("采集", "事件（产生/接受/落盘）", $"{health.EventsProduced}/{health.EventsAccepted}/{health.EventsWritten}", "条");
        Add("采集", "丢失（丢弃/替换/关键）", $"{health.EventsDropped}/{health.EventsEvicted}/{health.CriticalLost}", "条");
        Add("采集", "丢失世代 LossEpoch", health.LossEpoch.ToString(), "", estimated: false);
        Add("延时", "写入延时（最近）", health.WriterLatencyMs.ToString(), "ms", estimated: true);
        Add("延时", "刷盘延时（最近）", health.FlushLatencyMs.ToString(), "ms", estimated: true);
        Add("延时", "分析滞后（最近）", health.AnalyzerLagMs.ToString(), "ms", estimated: true);
        Add("资源", "队列深度（写入/分析/显示）", $"{health.QueueDepthWriter}/{health.QueueDepthAnalyzer}/{health.QueueDepthViewer}", "条");
        Add("资源", "环占用", (health.RingBytes / 1024).ToString(), "KiB");
        Add("资源", "已写入字节", (health.WrittenBytes / 1024).ToString(), "KiB");
        Add("资源", "段滚动次数", health.Rotations.ToString(), "次");
        Add("存储", "是否降级", health.StorageDegraded ? "是" : "否");
        if (health.LastStorageReason is not null) Add("存储", "最近故障原因", health.LastStorageReason);

        if (flight is { } f)
        {
            Add("飞行记录", "环内事件", f.RingEvents.ToString(), "条");
            Add("飞行记录", "被覆盖", f.OverwrittenEvents.ToString(), "条");
            Add("飞行记录", "冻结窗口（开/封/落盘）", $"{f.OpenWindows}/{f.SealedWindows}/{f.PersistedWindows}", "个");
            Add("飞行记录", "触发合并/拒绝", $"{f.MergedTriggers}/{f.RejectedTriggers}", "次");
            Add("飞行记录", "检查点字节", (f.CheckpointBytes / 1024).ToString(), "KiB");
        }

        if (rules is { } r)
        {
            Add("规则", "求值事件", r.EventsEvaluated.ToString(), "条");
            Add("规则", "开卡/修订/结案", $"{r.IncidentsOpened}/{r.IncidentsUpdated}/{r.IncidentsResolved}", "张");
            Add("规则", "不可判定", r.IncidentsInconclusive.ToString(), "张");
            Add("规则", "抑制（冷却/上限）", $"{r.SuppressedByCooldown}/{r.SuppressedByCap}", "次");
            Add("规则", "规则内部故障", r.RuleFaults.ToString(), "次");
        }

        if (expectations is { } e)
        {
            Add("反馈契约", "已开启期望", e.Begun.ToString(), "条");
            Add("反馈契约", "已满足步骤", e.Satisfied.ToString(), "步");
            Add("反馈契约", "判定超时", e.TimedOut.ToString(), "步");
            Add("反馈契约", "拒绝/正常结束关闭", $"{e.ClosedByRejection}/{e.ClosedByTerminal}", "条");
            Add("反馈契约", "待满足", e.Pending.ToString(), "条");
            if (e.DroppedByCap > 0) Add("反馈契约", "因上限丢弃", e.DroppedByCap.ToString(), "条");
        }

        Raise(nameof(Metrics));
    }

    private void RaiseProjectionChanged()
    {
        Raise(nameof(OverviewHealthText));
        Raise(nameof(EvidenceText));
        Raise(nameof(CountersText));
        Raise(nameof(ResourceText));
        Raise(nameof(IncidentSummaryText));
        Raise(nameof(ModeName));
        Raise(nameof(StorageRootText));
        Raise(nameof(SessionShortId));
    }

    /// <summary>选中一张事件卡（时间线随之聚焦；找不到就保持原样并如实提示）。</summary>
    public void SelectIncident(string? incidentId)
    {
        _selectedIncidentId = incidentId;
        var selected = incidentId is null ? null : _incidents.FirstOrDefault(i => i.IncidentId == incidentId);
        SelectedIncidentDetail = selected is null
            ? null
            : $"{selected.SymptomCode}｜{selected.Severity}｜{selected.Status}｜置信 {selected.Confidence}" +
              $"｜事实 {selected.FactCount}｜候选 {selected.CandidateCount}｜缺失证据 {selected.MissingCount}";

        Raise(nameof(SelectedIncidentId));
        Raise(nameof(SelectedIncidentDetail));

        var generation = Interlocked.Increment(ref _generation);
        try
        {
            var events = _source.GetRecentEvents(MaxTimelineRows);
            _post(() => { if (generation == Interlocked.Read(ref _generation)) RebuildTimeline(events); });
        }
        catch (Exception ex)
        {
            _post(() => SetStatus("时间线刷新失败：" + ex.GetType().Name));
        }
    }

    // ────────────────────────── 日志（有界 + 过滤带代际）──────────────────────────

    /// <summary>追加一条日志（由 UI 的事件泵调用，**UI 线程**）。超过容量丢最旧。</summary>
    public void AddLog(DiagnosticLogLevel level, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _log.Add(new DiagnosticLogRow(level, text));
        while (_log.Count > MaxLogRows) _log.RemoveAt(0);
        Raise(nameof(Log));
    }

    /// <summary>日志视图行数（过滤后）。过滤在**后台/调用线程**完成，结果按代际落地。</summary>
    public IReadOnlyList<DiagnosticLogRow> FilteredLog
    {
        get
        {
            IEnumerable<DiagnosticLogRow> query = _log;
            if (_logFilterMin != DiagnosticLogLevel.Info)
                query = query.Where(l => l.Level >= _logFilterMin);
            if (_logFilterText.Length > 0)
                query = query.Where(l => l.Text.Contains(_logFilterText, StringComparison.OrdinalIgnoreCase));
            return query.ToArray();
        }
    }

    public void SetLogFilter(DiagnosticLogLevel minLevel, string? text = null)
    {
        _logFilterMin = minLevel;
        _logFilterText = text?.Trim() ?? string.Empty;

        var generation = Interlocked.Increment(ref _logGeneration);
        // 过滤本身是纯计算（有界 2000 行）；这里仍走代际，避免将来的异步过滤覆盖新视图。
        _post(() =>
        {
            if (generation != Interlocked.Read(ref _logGeneration)) return;
            Raise(nameof(LogFilterMin));
            Raise(nameof(LogFilterText));
            Raise(nameof(FilteredLog));
        });
    }

    public void ClearLog()
    {
        _log.Clear();
        Raise(nameof(Log));
        Raise(nameof(FilteredLog));
    }

    // ────────────────────────── 动作（Deep / 导出）──────────────────────────

    /// <summary>
    /// 请求 Deep Trace 开关。★ 只**请求**模式切换并如实回显；不做任何输入捕获，
    /// 也不改变迁移行为（Deep 的高详细度输入观测由 UI 层按授权单独启用）★
    /// </summary>
    public void RequestDeepTrace(bool enabled)
    {
        DeepTraceRequested = enabled;
        try
        {
            _source.RequestMode(enabled ? CaptureMode.Deep : CaptureMode.Operational,
                enabled ? "user-enabled-deep-trace" : "user-disabled-deep-trace");
            SetStatus(enabled
                ? "已请求开启 Deep Trace：采集更详细（含 Verbose），仍不影响迁移行为。"
                : "已请求关闭 Deep Trace：回到 Operational 采集。");
        }
        catch (Exception ex)
        {
            SetStatus("模式切换失败：" + ex.GetType().Name);
        }
    }

    /// <summary>
    /// 导出诊断包。导出由 D6 的实现承担（VM 只驱动与回显）；**绝不上传**任何数据。
    /// </summary>
    public async Task<DiagnosticExportResult> ExportAsync(CancellationToken ct = default)
    {
        var handler = ExportHandler;
        if (handler is null)
        {
            SetStatus("导出不可用：本包未装配导出服务。");
            return new DiagnosticExportResult(false, null, "export-handler-missing", 0);
        }

        SetStatus("正在导出诊断包（本地文件，不会上传）…");
        try
        {
            var result = await handler(ct).ConfigureAwait(false);
            _post(() => SetStatus(result.Succeeded
                ? $"导出完成：{result.Path}（{result.Bytes / 1024} KiB，本地文件，不会上传）"
                : $"导出失败：{result.FailureReason ?? "unknown"}"));
            return result;
        }
        catch (Exception ex)
        {
            _post(() => SetStatus("导出异常：" + ex.GetType().Name));
            return new DiagnosticExportResult(false, null, ex.GetType().Name, 0);
        }
    }

    private void SetStatus(string text)
    {
        _statusText = text;
        Raise(nameof(StatusText));
    }
}