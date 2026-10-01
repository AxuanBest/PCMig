using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;

namespace PCMig.Diagnostics;

/// <summary>
/// 诊断 Hub：<see cref="IDiagnosticPublisher"/> 的实现。
///
/// 生产线程上做的事（严格有界，方案 §4/§16）：
///   1. 模式/投递类过滤（不合规立即返回，**不构造任何对象**）；
///   2. 盖戳（序列号、UTC、单调时钟、Activity 标识）；
///   3. 字段截断 + 文本掩码（不做序列化、不碰磁盘、不碰 UI）；
///   4. 按投递类选**一个**摄入队列，做一次非阻塞 TryAccept；Flight 环一次 Observe。
///
/// **绝不**：等待队列、写文件、序列化整条事件、调用 UI、抛异常。
/// 任何内部故障都转成 PublishFaults 计数并返回 false。
/// </summary>
public sealed class DiagnosticHub : IDiagnosticPublisher
{
    private readonly DiagnosticRuntimeOptions _options;
    private readonly DiagnosticClock _clock;
    private readonly DiagnosticHealth _health;
    private readonly LossLedger _loss;
    private readonly RedactionPolicy _redaction;
    private readonly Guid _sessionId;

    private long _sequence;
    private int _mode;
    private int _internalPublishing;
    private int _accepting = 1;

    private BoundedBranch? _ingressCritical;
    private BoundedBranch? _ingressOperational;
    private BoundedBranch? _ingressVerbose;
    private FlightRecorder? _flight;
    private DiagnosticMeters? _meters;

    public DiagnosticHub(
        Guid sessionId,
        DiagnosticRuntimeOptions options,
        DiagnosticClock clock,
        DiagnosticHealth health,
        LossLedger loss,
        RedactionPolicy redaction)
    {
        _sessionId = sessionId;
        _options = options;
        _clock = clock;
        _health = health;
        _loss = loss;
        _redaction = redaction;
        _mode = (int)options.InitialMode;
    }

    public Guid SessionId => _sessionId;

    /// <summary>本会话已分配的最大序号（证据水位：writer/analyzer 各自另有自己的接收水位）。</summary>
    public long CurrentSequence => Interlocked.Read(ref _sequence);

    public bool IsEnabled => Mode != CaptureMode.Off;

    public CaptureMode Mode => (CaptureMode)Volatile.Read(ref _mode);

    public DiagnosticHealth Health => _health;

    public LossLedger Loss => _loss;

    internal void AttachIngress(BoundedBranch critical, BoundedBranch operational, BoundedBranch verbose)
    {
        _ingressCritical = critical;
        _ingressOperational = operational;
        _ingressVerbose = verbose;
    }

    internal void AttachFlight(FlightRecorder flight) => _flight = flight;

    internal void AttachMeters(DiagnosticMeters meters) => _meters = meters;

    /// <summary>
    /// 采集模式过滤（唯一判据）：
    ///   · Off → 全部拒绝；
    ///   · Operational / Flight → 收 Operational + DurableCritical，拒 Verbose；
    ///   · Deep → 全收（含 Verbose）。
    /// 注意：这与 DiagnosticLevel 正交 —— "级别"描述影响，"投递类"描述存储承诺。
    /// </summary>
    public bool IsEnabledFor(EventDescriptor descriptor)
    {
        var mode = Mode;
        if (mode == CaptureMode.Off) return false;
        if (descriptor.Delivery == DeliveryClass.Verbose && mode != CaptureMode.Deep) return false;
        return true;
    }

    /// <summary>
    /// ★ D6.1 §3 ★ 关闭第一步：不再接受**新的**事件（已进入管线的事件照常被排空）。
    /// 与 CaptureMode 无关（不改采集模式语义），只是"摄入闸门关闭"。
    /// </summary>
    public void StopAccepting() => Volatile.Write(ref _accepting, 0);

    /// <summary>摄入闸门是否打开（关闭过程中为 false）。</summary>
    public bool IsAccepting => Volatile.Read(ref _accepting) != 0;

    public bool TryPublish(in DiagnosticEventDraft draft)
    {
        try
        {
            var descriptor = draft.Descriptor;

            // 关闭摄入闸门后，一切新事件都不再进入（计入 filtered，不制造"丢失"假象）。
            if (!IsAccepting)
            {
                _health.AddFiltered();
                _meters?.OnFiltered();
                return false;
            }

            if (!IsEnabledFor(descriptor))
            {
                _health.AddFiltered();
                _meters?.OnFiltered();
                return false;
            }

            // ★ D6.1 §1 隐私兜底（P0）★
            // UI.InputObserved 的类别必须取自 InputCategories 白名单。任何"带按键身份"的字符串
            // （例如旧缺陷产生的 "key:A"）在**管线入口**就被拒收 ⇒ P0 不再依赖单个调用点是否写对。
            // 与"EventDescriptor.Define 拒绝 Secret"同一思路：把隐私不变量放在类型/入口边界上。
            if (draft.Payload is UiInputObservedPayload observed && !InputCategories.IsKnown(observed.InputKind))
            {
                _health.AddFiltered();
                _meters?.OnFiltered();
                return false;
            }

            _health.IncProduced();
            _meters?.OnProduced();

            _clock.TrySample(out var monotonic, out var utcNow, out var clockJumpDetected);
            var sequence = Interlocked.Increment(ref _sequence);

            var evt = CreateEvent(in draft, sequence, monotonic, utcNow);
            var estimatedBytes = EventSizeEstimator.Estimate(in evt);

            // ★ D6.1 §6 ★ 单条事件字节硬上限：超大载荷只能被**拒收并记账**，
            //   绝不允许绕过总预算（旧实现按固定 512 B 估算 ⇒ 大载荷实际无上限）。
            if (estimatedBytes > _options.MaxEventBytes)
            {
                _loss.RecordDrop(
                    evt.Delivery switch
                    {
                        DeliveryClass.DurableCritical => DiagnosticBranches.IngressCritical,
                        DeliveryClass.Verbose => DiagnosticBranches.IngressVerbose,
                        _ => DiagnosticBranches.IngressOperational,
                    },
                    evt.Delivery,
                    sequence,
                    "payload-too-large",
                    evicted: false);
                _health.IncPayloadRejected();
                _meters?.OnFiltered();
                return false;
            }

            var item = new BranchItem(evt, estimatedBytes);

            // 按投递类选摄入队列：Critical 有独立 reserve，Verbose 可以被淘汰，Operational 走保留优先。
            var ingress = evt.Delivery switch
            {
                DeliveryClass.DurableCritical => _ingressCritical ?? _ingressOperational,
                DeliveryClass.Verbose => _ingressVerbose ?? _ingressOperational,
                _ => _ingressOperational ?? _ingressCritical,
            };

            var accepted = ingress is not null && ingress.TryAccept(in item);

            // Flight 环独立预算：它不参与 accepted 判定（环是"尽力保留近尾"，不是投递承诺）。
            _flight?.Observe(in evt);

            if (accepted)
            {
                _health.IncAccepted();
                _meters?.OnAccepted(evt.Delivery.ToString());
            }

            if (clockJumpDetected) PublishClockJump(sequence);

            return accepted;
        }
        catch (Exception)
        {
            // 诊断绝不影响业务：任何内部异常都只记自身健康。
            _health.IncPublishFault();
            return false;
        }
    }

    /// <summary>
    /// 把草稿盖戳成事件但**不路由**（关闭路径专用：最终状态事件要写进封段之后的新段，
    /// 那时摄入分支已经停了）。与 TryPublish 共用同一段构造逻辑，避免两处字段口径分叉。
    /// </summary>
    internal DiagnosticEvent CreateDetached(in DiagnosticEventDraft draft)
    {
        _clock.TrySample(out var monotonic, out var utcNow, out _);
        var sequence = Interlocked.Increment(ref _sequence);
        return CreateEvent(in draft, sequence, monotonic, utcNow);
    }

    /// <summary>切换采集模式并如实记录切换事件（Deep 只能由用户显式开启）。</summary>
    public void SetMode(CaptureMode mode, string reasonCode)
    {
        var previous = Mode;
        if (previous == mode) return;
        Volatile.Write(ref _mode, (int)mode);

        if (mode != CaptureMode.Off)
        {
            TryPublish(new DiagnosticEventDraft(
                DiagnosticsEvents.ModeChanged,
                DiagnosticContext.Root(_sessionId, "DiagnosticHub"),
                new DiaModeChangedPayload(previous, mode, reasonCode),
                Delivery: DeliveryClass.Operational));
        }
    }

    /// <summary>诊断系统自身的失败/降级事件（runtime 用它报告存储、丢弃、分析滞后等）。</summary>
    internal bool PublishInternal(
        EventDescriptor descriptor,
        IDiagnosticPayload? payload,
        DiagnosticLevel? level = null,
        DiagnosticOutcome? outcome = null,
        int? win32Error = null,
        string? message = null,
        string? component = null)
    {
        if (Volatile.Read(ref _internalPublishing) == 1) return false;
        Volatile.Write(ref _internalPublishing, 1);
        try
        {
            return TryPublish(new DiagnosticEventDraft(
                descriptor,
                DiagnosticContext.Root(_sessionId, component ?? "DiagnosticRuntime"),
                payload,
                Level: level,
                Outcome: outcome,
                Win32Error: win32Error,
                ErrorDomain: win32Error is null ? ErrorDomain.None : ErrorDomain.Win32,
                Message: message,
                Delivery: DeliveryClass.Operational));
        }
        finally
        {
            Volatile.Write(ref _internalPublishing, 0);
        }
    }

    private void PublishClockJump(long atSequence)
    {
        _ = PublishInternal(
            DiagnosticsEvents.ClockAnchorAdjusted,
            new DiaClockAnchorAdjustedPayload(_clock.ClockJumpCount, "detected", 0),
            level: DiagnosticLevel.Warning,
            component: "DiagnosticClock");
        _ = atSequence;
    }

    private DiagnosticEvent CreateEvent(in DiagnosticEventDraft draft, long sequence, long monotonic, DateTimeOffset utcNow)
    {
        var descriptor = draft.Descriptor;
        var rawMessage = draft.Message;
        var message = _redaction.SanitizeText(rawMessage, _options.MaxMessageLength);
        var truncated = draft.Truncated || (rawMessage is not null && message is not null && message.Length < rawMessage.Length);
        var (traceId, spanId, parentSpanId) = DiagnosticActivities.Current();
        var lossEpoch = _loss.Epoch;

        return new DiagnosticEvent
        {
            SchemaVersion = DiagnosticEvent.CurrentSchemaVersion,
            Descriptor = descriptor,
            SessionId = _sessionId,
            Sequence = sequence,
            TimestampUtc = utcNow,
            MonotonicTimestamp = monotonic,
            Level = draft.Level ?? descriptor.Level,
            Delivery = draft.Delivery ?? descriptor.Delivery,
            EvidenceQuality = draft.EvidenceQuality ?? EvidenceQuality.Direct,
            CaptureMode = Mode,
            Component = Truncate(draft.Context.Component, 64),
            ControlId = Truncate(draft.Context.ControlId, 64),
            ActionId = draft.Context.ActionId,
            OperationId = draft.Context.OperationId,
            ParentOperationId = draft.Context.ParentOperationId,
            JobId = Truncate(draft.Context.JobId, 128),
            ObjectId = Truncate(draft.Context.ObjectId, 128),
            RunGeneration = draft.Context.RunGeneration == 0 ? null : draft.Context.RunGeneration,
            Attempt = draft.Context.Attempt,
            Pass = Truncate(draft.Context.Pass, 32),
            Outcome = draft.Outcome,
            ObservationVersion = draft.ObservationVersion ?? sequence,
            ProjectionVersion = draft.ProjectionVersion,
            StateOwner = draft.StateOwner,
            Phase = Truncate(draft.Phase, 64),
            TraceId = traceId,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            Causation = draft.Causation,
            CorrelationId = Truncate(draft.CorrelationId, 128),
            ErrorDomain = draft.ErrorDomain,
            ExceptionType = Truncate(draft.ExceptionType, 128),
            HResult = draft.HResult,
            Win32Error = draft.Win32Error,
            SocketError = draft.SocketError,
            RobocopyExitCode = draft.RobocopyExitCode,
            DurationMs = draft.DurationMs is < 0 ? 0 : draft.DurationMs,
            Message = message,
            Path = draft.Path,
            Truncated = truncated,
            LossEpoch = lossEpoch == 0 ? null : lossEpoch,
            ContractVersion = draft.ContractVersion,
            Payload = draft.Payload,
        };
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value.Substring(0, max);
    }
}