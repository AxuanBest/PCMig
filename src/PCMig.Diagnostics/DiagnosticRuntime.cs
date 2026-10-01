using System.Diagnostics;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using PCMig.Diagnostics.Analysis;
using PCMig.Diagnostics.Analysis.Feedback;

namespace PCMig.Diagnostics;

/// <summary>关闭报告（如实回答"排空了多少、放弃了多少、有没有确认刷盘、标记写没写"）。</summary>
public readonly record struct DiagnosticShutdownReport(
    bool CleanShutdown,
    int DrainedEvents,
    int AbandonedEvents,
    int SealedSegments,
    bool FlushAcknowledged,
    bool CleanMarkerWritten,
    string? FailureReason);

/// <summary>
/// 导出截止点（D6.1 §2.5）：导出前把"当前活动段"就地封存，使**用户点导出前最后一段时间的证据**
/// 真正进入已封段集合。
///   · <see cref="RequestedSequence"/> 是请求时的会话水位；
///   · <see cref="CutoffSequence"/> 是**真正已落盘**的水位（导出只承诺覆盖到这里）；
///   · <see cref="PendingAtCutoff"/> &gt; 0 表示"请求时还有事件没写下去"——如实标注，不假装包全；
///   · <see cref="FlushStatus"/>：`acknowledged` / `seal-failed` / `no-active-segment`；
///   · <see cref="AnyTailLoss"/>：为真表示包尾可能不完整（有未覆盖或本会话有丢失世代）。
/// </summary>
public readonly record struct DiagnosticExportCutoff(
    long RequestedSequence,
    long CutoffSequence,
    DateTimeOffset CutoffUtc,
    string FlushStatus,
    long CoverageEndSequence,
    bool AnyTailLoss,
    long PendingAtCutoff,
    int SealedSegments,
    string? Note);

/// <summary>
/// 诊断运行时装配根（方案 §5/§32-D2）。
///
/// 装配出的管线：
/// <code>
/// 生产线程 → DiagnosticHub（TryPublish，非阻塞）
///              → ingress-critical / ingress-operational / ingress-verbose（按投递类）
///                  → FanOutStage（后台显式广播）
///                       ├→ writer 收件箱 → JsonlSegmentWriter（单写者、分段、manifest）
///                       ├→ analyzer 收件箱 →（D4 接入规则引擎；D2 只记滞后）
///                       └→ viewer 收件箱 → ViewerEventCache（有界显示缓存）
///              → FlightRecorder（独立预算的内存环 + 触发窗口 + 低频检查点）
/// </code>
///
/// fail-open：存储不可用时**不抛异常**，而是降级为"只有内存分支"（analyzer/viewer 照常工作，
/// writer 缺席并在健康里标注），产品启动与迁移完全不受影响。
/// </summary>
public sealed class DiagnosticRuntime : IAsyncDisposable, IDiagnosticSink
{
    private readonly DiagnosticRuntimeOptions _options;
    private readonly DiagnosticClock _clock = new();
    private readonly DiagnosticHealth _health = new();
    private readonly LossLedger _loss = new();
    private readonly EvidenceCoverage _coverage;
    private readonly RedactionPolicy _redaction;
    private readonly DiagnosticHub _hub;
    private readonly DiagnosticMeters _meters;
    private readonly DiagnosticSessionStore? _store;
    private readonly JsonlSegmentWriter? _writer;
    private readonly JsonlSegmentWriter? _incidentWriter;
    private readonly FlightRecorder? _flight;
    private readonly ViewerEventCache _viewerCache;
    private readonly RuleEngine _ruleEngine;
    private readonly FeedbackContractRegistry _feedbackContracts;
    private readonly PendingExpectationTracker _expectations;
    private readonly UiCommandNotDispatchedRule _notDispatchedRule = new();
    private readonly UiFeedbackMissingRule _feedbackMissingRule = new();

    private BoundedBranch? _ingressCritical;
    private BoundedBranch? _ingressOperational;
    private BoundedBranch? _ingressVerbose;
    private BoundedBranch? _writerInbox;
    private BoundedBranch? _analyzerInbox;
    private BoundedBranch? _viewerInbox;
    private FanOutStage? _fanOut;

    /// <summary>
    /// ★ D6.1 §9 ★ 事件卡落盘队列：analyzer 只**产出**卡片行并 TryAccept；
    /// 真正的 durable IO（含 Flush(true)）由这个分支的消费者做 ⇒ 磁盘慢不会卡住 analyzer。
    /// </summary>
    private BoundedBranch? _incidentInbox;

    /// <summary>
    /// ★ D6.1 §3 ★ 两个取消源**必须分开**：
    ///   · `_schedulerCts` 只取消周期生产（timer / 期望调度）；
    ///   · `_pipelineCts` 只取消管线 worker（分支 pump）。
    /// 旧实现共用一个 CTS：关闭时 `StopTimer()` 一取消，所有分支 pump 立刻退出，
    /// 后面的"排空"实际上什么都没排，却仍可能落下 clean marker（**假 clean**）。
    /// </summary>
    private CancellationTokenSource? _schedulerCts;

    private CancellationTokenSource? _pipelineCts;

    private int _forcedPipelineCancel;
    private Task? _timerTask;
    private int _started;
    private int _shutdownCompleted;
    private long _lastAnalyzerLagPublishMs;
    private long _lastStorageFailurePublishMs;
    private long _lastCriticalLostPublishMs;
    private long _lastBackpressurePublishMs;
    private long _lastPublishedLossCount;
    private int _retentionCounter;
    private Func<DiagnosticEvent, ValueTask>? _analyzerSink;

    private DiagnosticRuntime(
        DiagnosticRuntimeOptions options,
        Guid sessionId,
        DiagnosticSessionStore? store,
        bool storageAvailable,
        string? degradedReason)
    {
        _options = options;
        _store = store;
        IsStorageAvailable = storageAvailable;
        StorageDegradedReason = degradedReason;
        SessionId = sessionId;

        _redaction = RedactionPolicy.CreateForSession();
        _coverage = new EvidenceCoverage(_loss);
        _hub = new DiagnosticHub(sessionId, options, _clock, _health, _loss, _redaction);
        _meters = new DiagnosticMeters(_health, _loss);
        _viewerCache = new ViewerEventCache(options.ViewerMaxEvents);

        if (store is not null)
        {
            _writer = new JsonlSegmentWriter(store, options, _health, _loss, _meters, "events");
            // 事件卡单独一个写者（不同文件、不同调用线程）：incidents.jsonl 是**修订流水**
            // （同一 IncidentId 每次修订一行，离线端按 Revision 取最新）。
            _incidentWriter = new JsonlSegmentWriter(store, options, _health, _loss, _meters, "incidents");
            _flight = new FlightRecorder(store, options, _health, _loss);
            _hub.AttachFlight(_flight);
        }

        _hub.AttachMeters(_meters);
        BuildPipeline();

        // 规则引擎：analyzer 收件箱的消费者。证据解析走**有界**的 viewer 缓存
        // （拿不到就老实说拿不到，绝不因此下确定结论）。
        _ruleEngine = new RuleEngine(RuleRegistry.CreateDefault(), ResolveEvidenceFromCache);
        _ruleEngine.OnIncident = HandleIncidentLifecycle;

        // 反馈契约 + 待满足期望跟踪器：**单个 scheduler**（本 runtime 的定时器统一 Tick），
        // 绝不为每条期望创建 Timer；超时只产出"没等到"的事实，由规则决定怎么落卡。
        _feedbackContracts = FeedbackContractRegistry.CreateDefault();
        _expectations = new PendingExpectationTracker(_feedbackContracts, HandleExpectationTimeout);

        SetAnalyzerSink(evt =>
        {
            // 顺序：先让跟踪器看见事件（开/关期望、满足步骤），再交给规则引擎。
            _expectations.Observe(evt);
            return _ruleEngine.ConsumeAsync(evt, CreateRuleContext());
        });

        if (!storageAvailable && degradedReason is not null)
            _health.MarkStorageDegraded(degradedReason);
    }

    /// <summary>规则引擎（D5 的诊断中心会读它的活动事件卡）。</summary>
    public RuleEngine RuleEngine => _ruleEngine;

    public IReadOnlyCollection<Incident> ActiveIncidents => _ruleEngine.ActiveIncidents;

    public RuleEngineStats RuleEngineStatistics() => _ruleEngine.Stats();

    /// <summary>待满足期望的统计（有界性与落后都要可见）。</summary>
    public ExpectationTrackerStats ExpectationStatistics() => _expectations.Stats();

    public int PendingExpectations => _expectations.PendingCount;

    /// <summary>
    /// 期望超时 ⇒ 落成事件卡（**事实与结论分离**：超时是事实，"没反应"是结论）。
    /// 两类超时用不同规则：Immediate 步骤 ⇒ 动作没被派发；其余 ⇒ 应当出现的反馈没出现。
    /// </summary>
    private void HandleExpectationTimeout(ExpectationTimeout timeout)
    {
        try
        {
            var incident = UiCommandNotDispatchedRule.Handles(timeout)
                ? _notDispatchedRule.Create(in timeout)
                : _feedbackMissingRule.Create(in timeout);

            _ruleEngine.ReportIncident(incident, triggerFlight: false);
        }
        catch (Exception)
        {
            // 超时结论生成失败绝不影响管线。
        }
    }

    /// <summary>
    /// 证据完整性（D6.1 §4）：**按投递类分档**判断，而不是"有任何丢失就全盘降级"。
    ///   · Verbose 丢弃不影响依赖 Operational 证据的规则；
    ///   · 保留性淘汰（环覆盖/队列合并）不算丢失；
    ///   · 存储降级仍然算不完整（它意味着"本该写下去的证据可能没写下"）。
    /// </summary>
    private bool EvidenceIsComplete => _coverage.IsCompleteFor(DeliveryClass.Operational) && !_health.StorageDegraded;

    /// <summary>规则上下文（分档证据覆盖 + 接受水位）。</summary>
    private RuleContext CreateRuleContext() =>
        _ruleEngine.CreateContext(_coverage, _hub.CurrentSequence);

    private DiagnosticEvent? ResolveEvidenceFromCache(EventRef reference)
    {
        // 有界缓存解析（找不到返回 null ⇒ 规则必须按"证据不可得"处理，而不是当作"没发生"）。
        foreach (var candidate in _viewerCache.Snapshot())
            if (candidate.Ref.Equals(reference)) return candidate;
        return null;
    }

    private void HandleIncidentLifecycle(Incident incident, RuleEngine.IncidentLifecycle lifecycle)
    {
        try
        {
            var descriptor = lifecycle == RuleEngine.IncidentLifecycle.Resolved
                ? DiagnosticsEvents.IncidentResolved
                : DiagnosticsEvents.IncidentOpened;

            var payload = new DiaIncidentPayload(
                incident.IncidentId,
                incident.RuleId,
                incident.RuleVersion,
                incident.Status.ToString(),
                incident.Severity.ToString(),
                incident.SymptomCode,
                incident.Confidence.ToString(),
                incident.EvidenceIncomplete,
                incident.Revision,
                incident.BreakPoint);

            _hub.PublishInternal(
                descriptor,
                payload,
                level: incident.Severity,
                outcome: lifecycle == RuleEngine.IncidentLifecycle.Resolved
                    ? DiagnosticOutcome.Succeeded
                    : DiagnosticOutcome.Accepted,
                component: "RuleEngine");

            // 高影响事件卡 ⇒ 抓一段飞行窗口（前后证据），便于离线复核。
            if (lifecycle == RuleEngine.IncidentLifecycle.Opened && incident.Severity >= DiagnosticLevel.Error)
                TriggerFlight(incident.RuleId, incident.SymptomCode);

            // ★ D6.1 §9 ★ 事件卡正文落盘改走**独立有界队列**：
            //   analyzer 只序列化成一行并入队（非阻塞、不碰磁盘），
            //   durable IO 由 incident 分支的消费者负责 ⇒ 磁盘慢不会再卡住规则引擎。
            //   队列满 ⇒ 由该分支按 Operational 记账（可见、不静默），规则计算继续。
            if (_incidentInbox is not null)
            {
                try
                {
                    var line = IncidentSerialization.ToJsonLine(incident);
                    _incidentInbox.TryAccept(new BranchItem(
                        null,
                        System.Text.Encoding.UTF8.GetByteCount(line) + 16,
                        line));
                }
                catch (Exception ex)
                {
                    // ★ 不得静默 ★ 序列化失败也要进台账（否则"卡片为什么没落盘"永远查不出来）。
                    _loss.RecordDrop(DiagnosticBranches.Incident, DeliveryClass.Operational, 0,
                        "incident-line-failed:" + ex.GetType().Name, evicted: false);
                }
            }
            else
            {
                // 没有事件卡写者（诊断存储不可用）也必须可见。
                _loss.RecordDrop(DiagnosticBranches.Incident, DeliveryClass.Operational, 0,
                    "incident-writer-unavailable", evicted: false);
            }
        }
        catch (Exception)
        {
            // 事件卡发布失败绝不影响规则引擎与 writer。
        }
    }

    public Guid SessionId { get; }

    /// <summary>是否成功建立磁盘存储（false = 只跑内存分支：分析/显示仍可用，落盘缺席）。</summary>
    public bool IsStorageAvailable { get; }

    /// <summary>存储不可用/启动降级的原因（null = 正常）。</summary>
    public string? StorageDegradedReason { get; }

    public IDiagnosticPublisher Publisher => _hub;

    /// <summary>本次运行的配置（只读用途：UI/导出要显示"这份数据是按什么预算采集的"）。</summary>
    public DiagnosticRuntimeOptions Options => _options;

    public DiagnosticHealth Health => _health;

    public LossLedger Loss => _loss;

    public RedactionPolicy Redaction => _redaction;

    public DiagnosticSessionStore? Store => _store;

    public CaptureMode Mode => _hub.Mode;

    /// <summary>D4 接入点：规则引擎作为 analyzer 收件箱的消费者。</summary>
    public void SetAnalyzerSink(Func<DiagnosticEvent, ValueTask>? sink) => _analyzerSink = sink;

    /// <summary>
    /// 启动运行时。**绝不抛**：任何存储/装配失败都降级返回（调用方拿 degradedReason 如实展示）。
    /// </summary>
    public static DiagnosticRuntime Start(DiagnosticRuntimeOptions options, out string? degradedReason)
    {
        var sessionId = Guid.NewGuid();
        DiagnosticSessionStore? store = null;
        var storageAvailable = false;
        degradedReason = null;

        try
        {
            var root = string.IsNullOrWhiteSpace(options.StorageRoot)
                ? DiagnosticSessionStore.DefaultRoot
                : options.StorageRoot!;
            store = new DiagnosticSessionStore(root, sessionId);
            store.EnsureCreated();
            store.WriteCatalog();
            storageAvailable = true;
        }
        catch (Exception ex)
        {
            degradedReason = "storage-unavailable:" + ex.GetType().Name;
            store = null;
        }

        var runtime = new DiagnosticRuntime(options, sessionId, store, storageAvailable, degradedReason);
        runtime.StartCore();
        return runtime;
    }

    private void BuildPipeline()
    {
        _writerInbox = _writer is null
            ? null
            : new BoundedBranch(
                DiagnosticBranches.Writer,
                _options.OperationalQueueCapacity,
                _options.OperationalByteBudget,
                evictsOldest: false,
                _loss,
                _health,
                (item, ct) => _writer!.ConsumeAsync(item, ct));

        _analyzerInbox = new BoundedBranch(
            DiagnosticBranches.Analyzer,
            _options.AnalyzerMaxPending,
            _options.OperationalByteBudget,
            evictsOldest: false,
            _loss,
            _health,
            AnalyzerConsumeAsync);

        _viewerInbox = new BoundedBranch(
            DiagnosticBranches.Viewer,
            _options.ViewerMaxEvents,
            _options.OperationalByteBudget,
            evictsOldest: false,
            _loss,
            _health,
            ViewerConsumeAsync);

        _fanOut = new FanOutStage(_writerInbox, _analyzerInbox, _viewerInbox, _meters, () => Mode);

        // ★ D6.1 §9 ★ 事件卡落盘分支（独立消费者、独立预算、独立丢失会计）。
        if (_incidentWriter is not null)
        {
            _incidentInbox = new BoundedBranch(
                DiagnosticBranches.Incident,
                capacity: 512,
                byteBudget: 4L * 1024 * 1024,
                evictsOldest: false,
                _loss,
                _health,
                (item, ct) =>
                {
                    _incidentWriter.AppendRawLine(item.RawJson ?? string.Empty);
                    return default;
                });
        }

        _ingressCritical = new BoundedBranch(
            DiagnosticBranches.IngressCritical,
            _options.CriticalReserveCapacity,
            2L * 1024 * 1024,
            evictsOldest: false,
            _loss, _health, _fanOut.RouteAsync);

        _ingressOperational = new BoundedBranch(
            DiagnosticBranches.IngressOperational,
            _options.OperationalQueueCapacity,
            _options.OperationalByteBudget,
            evictsOldest: false,
            _loss, _health, _fanOut.RouteAsync);

        _ingressVerbose = new BoundedBranch(
            DiagnosticBranches.IngressVerbose,
            _options.VerboseQueueCapacity,
            _options.VerboseByteBudget,
            evictsOldest: _options.VerboseEvictsOldest,
            _loss, _health, _fanOut.RouteAsync);

        _hub.AttachIngress(_ingressCritical, _ingressOperational, _ingressVerbose);
    }

    private void StartCore()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;

        _schedulerCts = new CancellationTokenSource();
        _pipelineCts = new CancellationTokenSource();
        var pipelineToken = _pipelineCts.Token;

        // 分支 pump 只跟 pipeline token 走（绝不被"停周期生产"连带取消）。
        _ingressCritical?.Start(pipelineToken);
        _ingressOperational?.Start(pipelineToken);
        _ingressVerbose?.Start(pipelineToken);
        _writerInbox?.Start(pipelineToken);
        _analyzerInbox?.Start(pipelineToken);
        _viewerInbox?.Start(pipelineToken);
        _incidentInbox?.Start(pipelineToken);

        WriteSessionMetadata();
        PublishSessionStarted();
        PublishPreviousSessionUncleanIfAny();

        _timerTask = Task.Run(() => TimerLoopAsync(_schedulerCts.Token), CancellationToken.None);
    }

    private void WriteSessionMetadata()
    {
        if (_store is null) return;
        try
        {
            var processIdentity = ProcessIdentity();
            _store.WriteSessionMetadata(new DiagnosticSessionMetadata
            {
                SessionId = DiagnosticId.Format(SessionId),
                StartedUtc = DateTimeOffset.UtcNow,
                MonotonicFrequency = DiagnosticClock.Frequency,
                StartedMonotonicTicks = _clock.MonotonicTicks,
                AppVersion = _options.AppVersion,
                BuildId = _options.BuildId,
                RuntimeVersion = Environment.Version.ToString(),
                OsVersion = Environment.OSVersion.VersionString,
                Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                ProcessId = Environment.ProcessId,
                ProcessIdentity = processIdentity,
                InitialMode = _options.InitialMode.ToString(),
                CatalogVersion = EventCatalog.CatalogVersion,
                CatalogHash = EventCatalog.CatalogHash,
                EventCatalogCount = EventCatalog.Count,
                RedactionKeyId = _redaction.KeyId,
                StorageRoot = _store.Root,
            });
        }
        catch (Exception ex)
        {
            _health.MarkStorageDegraded("session-metadata:" + ex.GetType().Name);
        }
    }

    /// <summary>进程身份 = PID + 启动时间（只看 PID 会在 PID 复用后误判"同一进程还活着"）。</summary>
    private static string ProcessIdentity()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return $"pid:{process.Id}:{process.StartTime.ToUniversalTime():O}";
        }
        catch (Exception)
        {
            return "pid:" + Environment.ProcessId;
        }
    }

    private void PublishSessionStarted()
    {
        var payload = new DiaSessionStartedPayload(
            _options.AppVersion,
            Environment.Version.ToString(),
            Environment.OSVersion.VersionString,
            Environment.ProcessId,
            DateTimeOffset.UtcNow,
            ProcessIdentity(),
            Mode,
            EventCatalog.CatalogVersion,
            EventCatalog.CatalogHash,
            DiagnosticClock.Frequency,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            // ★ D6.1 §2.3 ★ 只发**令牌**，不发原始路径（原始路径含 Windows 用户名，属 Personal）。
            _store is null ? "no-storage" : _redaction.Token(_store.Root));

        _hub.PublishInternal(DiagnosticsEvents.SessionStarted, payload, component: "DiagnosticRuntime");
    }

    private void PublishPreviousSessionUncleanIfAny()
    {
        if (_store is null) return;
        var previous = DiagnosticSessionStore.FindPreviousUncleanSession(_store.Root, SessionId);
        if (previous is null) return;

        _hub.PublishInternal(
            DiagnosticsEvents.PreviousSessionUnclean,
            new DiaPreviousSessionUncleanPayload(
                previous.Value.SessionId,
                previous.Value.ReasonCode,
                previous.Value.LastKnownSequence,
                previous.Value.TailCompleteLines,
                previous.Value.MarkerWriteFailed),
            level: DiagnosticLevel.Warning,
            component: "DiagnosticRuntime");
    }

    private ValueTask ViewerConsumeAsync(BranchItem item, CancellationToken ct)
    {
        _viewerCache.Add(item.Event!);
        _health.SetUiPending(_viewerCache.Count);
        return default;
    }

    private async ValueTask AnalyzerConsumeAsync(BranchItem item, CancellationToken ct)
    {
        _health.SetAnalyzerPending(_analyzerInbox?.Depth ?? 0);

        var lag = DiagnosticClock.TicksToMs(Stopwatch.GetTimestamp() - item.Event!.MonotonicTimestamp);
        if (lag >= 0)
        {
            _health.SetAnalyzerLagMs(lag);
            _meters.RecordAnalyzerLag(lag);
        }

        var sink = _analyzerSink;
        if (sink is not null)
            await sink(item.Event!).ConfigureAwait(false);
    }

    private async Task TimerLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        var tick = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                // ★ 唯一的期望调度器 ★ 250ms 节拍做到期判定（绝不为每条期望创建 Timer）。
                _expectations.Tick(
                    Stopwatch.GetTimestamp(),
                    // ★ D6.1 §4 ★ 反馈期望依赖的是 **analyzer 收件箱里的 Operational 证据**：
                    //   只有这一档/这一分支真的丢了东西，"没等到反馈"才必须降级。
                    EvidenceIsComplete && _coverage.IsCompleteForBranch(DiagnosticBranches.Analyzer),
                    _coverage.EpochFor(DeliveryClass.Operational),
                    _hub.CurrentSequence);

                // 其余维护（刷盘/飞行窗口/保留/健康）每秒一次。
                if (++tick % 4 == 0) Tick();
            }
        }
        catch (OperationCanceledException) { /* 正常停止 */ }
        catch (Exception)
        {
            _health.IncSinkFault();
        }
    }

    /// <summary>
    /// ★ D6.1 §16 ★ 把**丢失**变成可见事件（限速 + 只在变化时发）：
    ///   · `DIA.EventsDropped` —— 投递类丢/淘汰总数增长时；
    ///   · `DIA.CriticalLost`  —— DurableCritical 丢失（sticky，发作一次就够，随后限速重复提醒）；
    ///   · `DIA.Backpressure`  —— 某分支达到容量 80% 以上（队列压力，不等于丢）。
    ///
    /// 只读台账与健康快照；绝不改变任何丢弃/淘汰决策。
    /// </summary>
    private void PublishLossEvents()
    {
        var snapshot = _loss.Snapshot();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // ① 丢/淘汰总量增长 ⇒ 记一条（含本分支/类别明细由台账承担，这里只报总数与世代）。
        var dropped = snapshot.TotalDropped + snapshot.TotalEvicted;
        if (dropped > Interlocked.Read(ref _lastPublishedLossCount))
        {
            Interlocked.Exchange(ref _lastPublishedLossCount, dropped);
            _hub.PublishInternal(
                DiagnosticsEvents.EventsDropped,
                new DiaEventsDroppedPayload("aggregate", DeliveryClass.Operational, dropped, null, null, "loss-ledger-growth"),
                level: DiagnosticLevel.Warning,
                component: "LossLedger");
        }

        // ② 关键丢失（sticky）：首次立刻发；之后按 30s 限速重提醒。
        if (snapshot.StickyCriticalLost && nowMs - Interlocked.Read(ref _lastCriticalLostPublishMs) > 30_000)
        {
            Interlocked.Exchange(ref _lastCriticalLostPublishMs, nowMs);
            _hub.PublishInternal(
                DiagnosticsEvents.CriticalLost,
                new DiaCriticalLostPayload(snapshot.CriticalLost, "durable-critical-lost", Sticky: true),
                level: DiagnosticLevel.Error,
                component: "LossLedger");
        }

        // ③ 背压：任一分支达到容量 80% 以上（限速 30s，且与"丢"区分）。
        if (nowMs - Interlocked.Read(ref _lastBackpressurePublishMs) > 30_000)
        {
            foreach (var branch in BranchStatistics())
            {
                var capacity = BranchCapacity(branch.Name);
                if (capacity <= 0 || branch.Depth < capacity * 0.8) continue;

                Interlocked.Exchange(ref _lastBackpressurePublishMs, nowMs);
                _hub.PublishInternal(
                    DiagnosticsEvents.Backpressure,
                    new DiaBackpressurePayload(branch.Name, DeliveryClass.Operational, (int)Math.Min(int.MaxValue, branch.Depth), capacity, branch.DroppedFull + branch.DroppedBudget),
                    level: DiagnosticLevel.Warning,
                    component: "FanOutStage");
                break;
            }
        }
    }

    private int BranchCapacity(string branch) => branch switch
    {
        DiagnosticBranches.IngressCritical => _options.CriticalReserveCapacity,
        DiagnosticBranches.IngressVerbose => _options.VerboseQueueCapacity,
        DiagnosticBranches.Writer => _options.OperationalQueueCapacity,
        DiagnosticBranches.Analyzer => _options.AnalyzerMaxPending,
        DiagnosticBranches.Viewer => _options.ViewerMaxEvents,
        DiagnosticBranches.Incident => 512,
        _ => _options.OperationalQueueCapacity,
    };

    /// <summary>每秒一次的维护（全部在后台线程：**绝不**在生产路径上做这些）。</summary>
    private void Tick()
    {
        try
        {
            _writer?.FlushIfDue(durable: false);
            _incidentWriter?.FlushIfDue(durable: false);
            _flight?.Tick(checkpointEnabled: Mode is CaptureMode.Flight or CaptureMode.Deep);

            // ★ D6.1 §4/§16 ★ 丢失必须**作为事件可见**，而不只是台账里的数字：
            //   规则（DIAGNOSTICS_DEGRADED）与离线包都要能"看到"什么时候丢了什么。
            //   旧实现从不发布这三个事件 ⇒ 那几条触发路径等于**永远死掉**（实测：目录里定义了、
            //   规则也在等，但没人发）。这里只在**真的发生变化**时发一次，并做节流。
            PublishLossEvents();

            // 存储降级/恢复的可解释性：进入与退出各记一次（限速，不刷屏）。
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_health.StorageDegraded && nowMs - Interlocked.Read(ref _lastStorageFailurePublishMs) > 30_000)
            {
                Interlocked.Exchange(ref _lastStorageFailurePublishMs, nowMs);
                _hub.PublishInternal(
                    DiagnosticsEvents.StorageFailed,
                    new DiaStorageFailedPayload("segment-writer", _health.LastStorageReason ?? "unknown", null, EmergencyFallbackActive: false),
                    level: DiagnosticLevel.Error,
                    component: "JsonlSegmentWriter");
            }
            else if (!_health.StorageDegraded && Interlocked.Read(ref _lastStorageFailurePublishMs) > 0)
            {
                Interlocked.Exchange(ref _lastStorageFailurePublishMs, 0);
                _hub.PublishInternal(
                    DiagnosticsEvents.StorageRecovered,
                    new DiaStorageRecoveredPayload("segment-writer", 0),
                    component: "JsonlSegmentWriter");
            }

            // 分析器滞后：只报告"落后"，不冒充"丢事件"。
            var depth = _analyzerInbox?.Depth ?? 0;
            if (depth > _options.AnalyzerMaxPending * 0.8 && nowMs - Interlocked.Read(ref _lastAnalyzerLagPublishMs) > 30_000)
            {
                Interlocked.Exchange(ref _lastAnalyzerLagPublishMs, nowMs);
                _hub.PublishInternal(
                    DiagnosticsEvents.AnalyzerLagged,
                    new DiaAnalyzerLaggedPayload((int)Math.Min(int.MaxValue, depth), _health.AnalyzerLagMs),
                    level: DiagnosticLevel.Warning,
                    component: "FanOutStage");
            }

            // 保留/配额：每分钟一次（并如实标注配额是否仍然超）。
            if (++_retentionCounter >= 60)
            {
                _retentionCounter = 0;
                if (_store is not null)
                {
                    var pinned = _flight?.PersistedWindowPaths() ?? Array.Empty<string>();
                    var result = RetentionManager.Enforce(_store, _options, pinned);
                    if (result.QuotaExhausted)
                    {
                        _hub.PublishInternal(
                            DiagnosticsEvents.CoverageChanged,
                            new DiaCoverageChangedPayload(Mode, "quota-exhausted", null, null),
                            level: DiagnosticLevel.Warning,
                            component: "RetentionManager");
                    }
                }
            }
        }
        catch (Exception)
        {
            _health.IncSinkFault();
        }
    }

    public void SetMode(CaptureMode mode, string reasonCode) => _hub.SetMode(mode, reasonCode);

    /// <summary>触发一次 Flight 冻结（D4 规则命中时调用；返回合并/超限理由，不静默丢弃）。</summary>
    public FlightTriggerOutcome TriggerFlight(string triggerCode, string reasonCode)
        => _flight?.Trigger(triggerCode, reasonCode) ?? new FlightTriggerOutcome(false, null, false, "flight-unavailable");

    public FlightStats? FlightStatistics() => _flight?.Stats();

    public PathRef PathRef(string? path, PathRole role, string? rootAlias = null)
        => _redaction.CreatePathRef(path, role, rootAlias);

    // ---- IDiagnosticSink：Core/WinUI 只依赖这三个能力 ----

    DiagnosticContext IDiagnosticSink.Root(string? component) => DiagnosticContext.Root(SessionId, component);

    string IDiagnosticSink.Token(string? value) => _redaction.Token(value);

    public bool TryGetViewerSnapshot(out DiagnosticEvent[] events, int max = int.MaxValue)
    {
        events = _viewerCache.Snapshot(max);
        return events.Length > 0;
    }

    public IReadOnlyList<BranchStats> BranchStatistics()
    {
        var list = new List<BranchStats>(6);
        if (_ingressCritical is not null) list.Add(_ingressCritical.Stats());
        if (_ingressOperational is not null) list.Add(_ingressOperational.Stats());
        if (_ingressVerbose is not null) list.Add(_ingressVerbose.Stats());
        if (_writerInbox is not null) list.Add(_writerInbox.Stats());
        if (_analyzerInbox is not null) list.Add(_analyzerInbox.Stats());
        if (_viewerInbox is not null) list.Add(_viewerInbox.Stats());
        if (_incidentInbox is not null) list.Add(_incidentInbox.Stats());
        return list;
    }

    /// <summary>
    /// ★ D6.1 §2.5 ★ 为导出建立**截止点**：有界等待 writer 追上 → 就地封存活动段 → 返回如实覆盖信息。
    ///
    /// 纪律：
    ///   · **不暂停迁移、不锁 Job**：只在诊断 writer 自己的写路径上加一次有界停顿；
    ///   · 等待是**有界**的（默认 500ms；到期就按"已落盘水位"给截止点，并如实标 AnyTailLoss）；
    ///   · 事件卡 writer 同样封段，保证最新的卡片修订也在包内。
    /// </summary>
    public DiagnosticExportCutoff PrepareExportCutoff(TimeSpan? waitBudget = null)
    {
        var requested = _hub.CurrentSequence;
        var budget = waitBudget ?? TimeSpan.FromMilliseconds(500);
        var deadline = DateTime.UtcNow + budget;

        // 有界等待：让已经盖戳的事件尽量落盘（不阻塞业务、不影响迁移）。
        while (_writer is not null
               && _writer.WrittenWatermark < requested
               && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(5);
        }

        var events = _writer?.SealForExport();
        var incidents = _incidentWriter?.SealForExport();

        var written = events?.WrittenWatermark ?? 0;
        var pending = Math.Max(0, requested - written);
        var flushStatus = events is null
            ? "no-writer"
            : !events.Value.Success ? "seal-failed"
            : events.Value.SealedSegmentPath is null ? "no-active-segment"
            : "acknowledged";

        var coverageEnd = 0L;
        if (_writer is not null)
            foreach (var manifest in _writer.SealedSegments) coverageEnd = Math.Max(coverageEnd, manifest.LastSequence);

        var anyTailLoss = pending > 0 || _loss.Epoch > 0 || (events is { Success: false });
        var note = events is null
            ? "诊断存储不可用：本次导出的包不含任何事件段"
            : pending > 0
                ? $"请求时有 {pending} 条事件尚未落盘，包尾覆盖到序号 {written}"
                : null;

        _ = incidents;
        return new DiagnosticExportCutoff(
            requested, written, DateTimeOffset.UtcNow, flushStatus, coverageEnd, anyTailLoss, pending,
            _writer?.SealedSegments.Count ?? 0, note);
    }

    public DiagnosticHealthSnapshot GetHealthSnapshot()
    {
        var loss = _loss.Snapshot();
        return new DiagnosticHealthSnapshot(
            _health.EventsProduced,
            _health.EventsAccepted,
            _health.EventsFiltered,
            _health.EventsWritten,
            loss.TotalDropped,
            loss.TotalEvicted,
            loss.CriticalLost,
            loss.StickyCriticalLost,
            loss.Epoch,
            _health.PublishFaults,
            _health.SerializationFailures,
            _health.StorageFailures,
            _health.SinkFaults,
            _health.IngressCriticalDepth,
            _health.IngressOperationalDepth,
            _health.IngressVerboseDepth,
            _health.WriterDepth,
            _health.AnalyzerDepth,
            _health.ViewerDepth,
            _health.RingBytes,
            _health.RingEvents,
            _health.AnalyzerPending,
            _health.AnalyzerLagMs,
            _health.UiPending,
            _health.WriterLatencyMs,
            _health.FlushLatencyMs,
            _health.LastSuccessfulFlushUnixMs,
            _health.LastWriteUnixMs,
            _health.WrittenBytes,
            _health.Rotations,
            _health.StorageDegraded,
            _health.LastStorageReason);
    }

    /// <summary>
    /// 有界关闭：先停摄入（新事件不再进入），再排空收件箱，最后封段并把"最终状态事件"写进新段。
    /// **绝不无限等待**：预算到期即如实记 ShutdownIncomplete，且不写 clean marker。
    /// </summary>
    public async Task<DiagnosticShutdownReport> ShutdownAsync(TimeSpan? budget = null)
    {
        if (Interlocked.Exchange(ref _shutdownCompleted, 1) == 1)
            return new DiagnosticShutdownReport(false, 0, 0, 0, false, false, "already-shutdown");

        var totalBudget = budget ?? TimeSpan.FromMilliseconds(_options.ShutdownBudgetMs);

        // ★ D6.1 §3 关闭顺序 ★
        //   ① 停周期生产（只停 scheduler，**不动**管线 worker）
        StopTimer();

        //   ② 停止接受新的普通事件（之后 TryPublish 一律拒收，不再产生新证据）
        _hub.StopAccepting();

        //   ③ 会话收尾：把所有仍待满足的期望按"会话结束"关闭（**不**产出缺反馈结论——
        //      关窗时"还没等到"是正常的，不是缺陷）。
        _expectations.CloseAll("session-shutdown");

        var perBranch = TimeSpan.FromMilliseconds(Math.Max(50, totalBudget.TotalMilliseconds / 6));
        var abandoned = 0;
        var drained = 0;

        //   ④ 完成摄入端（入队完毕）→ 依次有界排空：ingress → fan-out 推送完 → 收件箱 → writer。
        foreach (var branch in new[] { _ingressCritical, _ingressOperational, _ingressVerbose })
            branch?.CompleteIngest();

        foreach (var branch in new[] { _ingressCritical, _ingressOperational, _ingressVerbose })
        {
            var result = await StopBranchAsync(branch, perBranch).ConfigureAwait(false);
            abandoned += result.Abandoned;
            drained += result.Drained;
        }

        foreach (var branch in new[] { _analyzerInbox, _viewerInbox, _incidentInbox })
        {
            var result = await StopBranchAsync(branch, perBranch).ConfigureAwait(false);
            abandoned += result.Abandoned;
        }

        var writerResult = await StopBranchAsync(_writerInbox, perBranch).ConfigureAwait(false);
        abandoned += writerResult.Abandoned;
        drained += writerResult.Drained;

        //   ⑤ 排空失败 ⇒ 如实记录 remaining/loss，**再**强制取消；本次关闭不再可能是 clean。
        var forcedCancel = Volatile.Read(ref _forcedPipelineCancel) != 0;
        if (abandoned > 0 || forcedCancel) ForceCancelPipeline();

        // 事件卡段单独封段（它由 analyzer 线程写，没有收件箱）。
        try { _incidentWriter?.SealActive(partial: false); } catch (Exception) { /* 封段失败已计入 health */ }

        _flight?.SealOpenWindows();

        // ★ D6.1 §3 ★ clean 的判据必须"全部为真"：没有放弃任何条目、没有强制取消、
//   存储没有降级、writer 存在、最终刷盘被确认。任何一条不成立都不得声称"完整保存"。
        var clean = abandoned == 0
                    && !forcedCancel
                    && Volatile.Read(ref _forcedPipelineCancel) == 0
                    && !_health.StorageDegraded
                    && _writer is not null;
        var sealedSegments = _writer?.SealedSegments.Count ?? 0;
        var flushAcknowledged = false;
        var cleanMarkerWritten = false;
        string? failureReason = _store is null
            ? "storage-unavailable"
            : abandoned > 0 ? "drain-budget-exhausted"
            : forcedCancel ? "pipeline-force-canceled"
            : _health.StorageDegraded ? "storage-degraded"
            : null;

        if (_writer is not null)
        {
            var finalEvents = new List<DiagnosticEvent>(1);
            var detached = _hub.CreateDetached(new DiagnosticEventDraft(
                clean ? DiagnosticsEvents.SessionCleanShutdown : DiagnosticsEvents.ShutdownIncomplete,
                DiagnosticContext.Root(SessionId, "DiagnosticRuntime"),
                clean
                    ? new DiaSessionCleanShutdownPayload(_health.EventsWritten, true, sealedSegments, false)
                    : new DiaShutdownIncompletePayload(6, _health.EventsWritten, abandoned, (int)totalBudget.TotalMilliseconds),
                Delivery: DeliveryClass.DurableCritical));
            finalEvents.Add(detached);

            var finalize = _writer.AppendFinal(finalEvents);
            sealedSegments = finalize.SealedSegments;
            flushAcknowledged = finalize.FlushAcknowledged;
            if (!finalize.Success)
            {
                clean = false;
                failureReason ??= finalize.FailureReason ?? "finalize-failed";
            }

            if (clean && flushAcknowledged && _store is not null)
            {
                try
                {
                    _store.WriteCleanShutdownMarker(new DiagnosticCleanShutdownMarker
                    {
                        SessionId = DiagnosticId.Format(SessionId),
                        ShutdownUtc = DateTimeOffset.UtcNow,
                        DrainedEvents = _health.EventsWritten,
                        SealedSegments = sealedSegments,
                        FlushAcknowledged = flushAcknowledged,
                        LastSequence = _hub.CurrentSequence,
                        // ★ 标记本身也要说清"这次会话的证据完不完整"：
                        //   drain 成功 ≠ 本次会话没有丢过证据（真话必须一起写下来）。
                        EvidenceLossEpoch = _coverage.EpochFor(DeliveryClass.Operational),
                        AnyFaultLoss = _coverage.HasFaultLoss,
                        RetentionEvictions = _coverage.RetentionEvictions,
                    });
                    cleanMarkerWritten = true;
                }
                catch (Exception ex)
                {
                    clean = false;
                    failureReason ??= "marker:" + ex.GetType().Name;
                }
            }
        }

        return new DiagnosticShutdownReport(clean && cleanMarkerWritten, drained, abandoned, sealedSegments, flushAcknowledged, cleanMarkerWritten, failureReason);
    }

    /// <summary>只停**周期生产**（timer/期望调度）。**绝不**连带取消管线 worker（D6.1 §3）。</summary>
    private void StopTimer()
    {
        try { _schedulerCts?.Cancel(); } catch (Exception) { /* 停止失败不阻断关闭 */ }
    }

    /// <summary>
    /// 强制取消管线 worker（只在**有界排空失败之后**调用；调用即意味着本次关闭不再是 clean）。
    /// </summary>
    private void ForceCancelPipeline()
    {
        if (Interlocked.Exchange(ref _forcedPipelineCancel, 1) == 1) return;
        try { _pipelineCts?.Cancel(); } catch (Exception) { /* ignore */ }
    }

    private static async Task<BranchDrainResult> StopBranchAsync(BoundedBranch? branch, TimeSpan budget)
        => branch is null
            ? new BranchDrainResult("none", 0, 0)
            : await branch.StopAsync(budget).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _shutdownCompleted) == 0)
        {
            try { await ShutdownAsync(TimeSpan.FromMilliseconds(Math.Min(500, _options.ShutdownBudgetMs))).ConfigureAwait(false); }
            catch (Exception) { /* 释放路径绝不外抛 */ }
        }

        try { _schedulerCts?.Dispose(); } catch (Exception) { /* ignore */ }
        try { _pipelineCts?.Dispose(); } catch (Exception) { /* ignore */ }
        _meters.Dispose();

        if (_ingressCritical is not null) await _ingressCritical.DisposeAsync().ConfigureAwait(false);
        if (_ingressOperational is not null) await _ingressOperational.DisposeAsync().ConfigureAwait(false);
        if (_ingressVerbose is not null) await _ingressVerbose.DisposeAsync().ConfigureAwait(false);
        if (_writerInbox is not null) await _writerInbox.DisposeAsync().ConfigureAwait(false);
        if (_analyzerInbox is not null) await _analyzerInbox.DisposeAsync().ConfigureAwait(false);
        if (_viewerInbox is not null) await _viewerInbox.DisposeAsync().ConfigureAwait(false);
        if (_incidentInbox is not null) await _incidentInbox.DisposeAsync().ConfigureAwait(false);

        _ = _timerTask;
    }
}