namespace PCMig.Diagnostics.Abstractions;

/// <summary>
/// 规范诊断事件（envelope）。这是**落盘契约**：字段一旦发布只允许新增可选字段，
/// 不允许改名/改义（改义必须新增 EventId 或升 EventVersion）。
///
/// 纪律（架构方案 §6 / §8）：
///   · 事件实例身份 = <c>(SessionId, Sequence)</c>，不是 EventId（EventId 是"类型"）；
///   · Sequence 是进程内唯一排序键，**不是**跨线程 happens-before，也不保证异步通道写盘顺序；
///   · MonotonicTimestamp 只在同一 session 内可比（跨机比较 UTC 必须带 ClockOffset/Uncertainty）；
///   · Message 只给人看，**任何规则不得以它作为判据**（判据 = EventId + typed payload）。
/// </summary>
public sealed record DiagnosticEvent
{
    public const string CurrentSchemaVersion = "1.0";

    public required string SchemaVersion { get; init; }

    public required EventDescriptor Descriptor { get; init; }

    public required Guid SessionId { get; init; }

    public required long Sequence { get; init; }

    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>Stopwatch.GetTimestamp()（频率见 session 元数据）。</summary>
    public required long MonotonicTimestamp { get; init; }

    public required DiagnosticLevel Level { get; init; }

    public required DeliveryClass Delivery { get; init; }

    public required EvidenceQuality EvidenceQuality { get; init; }

    public required CaptureMode CaptureMode { get; init; }

    public string? Component { get; init; }

    /// <summary>
    /// 稳定控件 ID（ControlId registry，例如 Step1.Connect）。语义动作链与投影观测都靠它对齐，
    /// 不允许用显示文字/列表序号/屏幕坐标当标识（方案 §9）。
    /// </summary>
    public string? ControlId { get; init; }

    public Guid? ActionId { get; init; }

    public Guid? OperationId { get; init; }

    public Guid? ParentOperationId { get; init; }

    public string? JobId { get; init; }

    public string? ObjectId { get; init; }

    public long? RunGeneration { get; init; }

    public int? Attempt { get; init; }

    /// <summary>通道 token（DiagnosticPass.*）。</summary>
    public string? Pass { get; init; }

    public DiagnosticOutcome? Outcome { get; init; }

    /// <summary>诊断来源域内的观测序号（含 RunGeneration）——**不是**业务 StateVersion。</summary>
    public long? ObservationVersion { get; init; }

    /// <summary>View 回读所对应的投影版本（用于"投影陈旧"判定）。</summary>
    public long? ProjectionVersion { get; init; }

    public StateOwner? StateOwner { get; init; }

    /// <summary>被观测阶段名（JobPhase 的稳定英文名或 UI 阶段 token）。规则优先用 typed payload。</summary>
    public string? Phase { get; init; }

    public string? TraceId { get; init; }

    public string? SpanId { get; init; }

    public string? ParentSpanId { get; init; }

    /// <summary>直接原因的事件实例（多因关联另用 Incidents 的 RelatedIncidents/有限 Links）。</summary>
    public EventRef? Causation { get; init; }

    public string? CorrelationId { get; init; }

    public ErrorDomain ErrorDomain { get; init; }

    public string? ExceptionType { get; init; }

    public int? HResult { get; init; }

    public int? Win32Error { get; init; }

    public int? SocketError { get; init; }

    public int? RobocopyExitCode { get; init; }

    public int? DurationMs { get; init; }

    /// <summary>presentation 文本（有长度上限、可含未受信任内容 ⇒ 只经 allowlist 转换后落盘）。</summary>
    public string? Message { get; init; }

    public PathRef? Path { get; init; }

    public bool Truncated { get; init; }

    /// <summary>丢失世代：这条事件所在分支经历过丢失时，用于让"缺事件"类规则降置信度。</summary>
    public long? LossEpoch { get; init; }

    public int? ContractVersion { get; init; }

    public IDiagnosticPayload? Payload { get; init; }

    /// <summary>反序列化时无法识别的枚举 token（保留 raw，**不静默强转**）。最多 8 项。</summary>
    public IReadOnlyList<string>? UnknownTokens { get; init; }

    /// <summary>本事件实例引用（证据只用它，不能用 Descriptor.Code）。</summary>
    public EventRef Ref => new(SessionId, Sequence);

    /// <summary>
    /// ★ D6.1 §5 ★ 行里声明的 `eventVersion`，**原样保留**。
    /// 旧解析器把它丢掉、直接用当前契约版本 ⇒ 未来版本的事件会被当成本版本"成功解析"。
    /// </summary>
    public int? DeclaredEventVersion { get; init; }

    /// <summary>
    /// ★ D6.1 §5 ★ 已知事件但**版本不受支持**：可以保存（不丢数据），
    /// 但**不得**用当前契约解释它 —— 规则引擎会跳过它，且其 payload 不会被解码成 typed payload。
    /// 纪律：能保存未知 ≠ 能用当前规则解释未知。
    /// </summary>
    public bool VersionUnsupported { get; init; }
}

/// <summary>
/// 发布草稿：调用点提供的"只减不增"信息。时间/序号/Activity 身份由 runtime 在
/// <see cref="IDiagnosticPublisher.TryPublish"/> 内统一盖戳，调用点不许自带时钟。
///
/// 作为 readonly record struct 传递（<c>in</c>）以避免热路径装箱；payload 由调用点按需构造，
/// 因此**必须先判 <see cref="IDiagnosticPublisher.IsEnabledFor"/>**。
/// </summary>
public readonly record struct DiagnosticEventDraft(
    EventDescriptor Descriptor,
    DiagnosticContext Context,
    IDiagnosticPayload? Payload = null,
    DiagnosticOutcome? Outcome = null,
    DiagnosticLevel? Level = null,
    DeliveryClass? Delivery = null,
    EvidenceQuality? EvidenceQuality = null,
    EventRef? Causation = null,
    ErrorDomain ErrorDomain = ErrorDomain.None,
    string? ExceptionType = null,
    int? HResult = null,
    int? Win32Error = null,
    int? SocketError = null,
    int? RobocopyExitCode = null,
    int? DurationMs = null,
    string? Message = null,
    PathRef? Path = null,
    string? Phase = null,
    StateOwner? StateOwner = null,
    long? ObservationVersion = null,
    long? ProjectionVersion = null,
    string? CorrelationId = null,
    bool Truncated = false,
    int? ContractVersion = null);