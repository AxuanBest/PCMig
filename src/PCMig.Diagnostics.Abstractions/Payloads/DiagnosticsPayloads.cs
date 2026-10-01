using System.Text.Json;

namespace PCMig.Diagnostics.Abstractions.Payloads;

/// <summary>
/// DIA.SessionStarted：诊断会话元数据。MonotonicFrequency 与 UTC 锚点使离线包能解释
/// MonotonicTimestamp 的真实含义（跨机不可比）。
///
/// ★ D6.1 §2.3 ★ `StorageRootToken` 是**令牌**而不是原始路径：存储根形如
/// `C:\Users\&lt;用户名&gt;\AppData\...`，属 Personal 级，明文不得进入规范事件
/// （原始路径只留在本地 session.json 的受限元数据里）。
/// </summary>
public sealed record DiaSessionStartedPayload(
    string AppVersion,
    string RuntimeVersion,
    string OsVersion,
    int ProcessId,
    DateTimeOffset ProcessStartUtc,
    string ProcessIdentity,
    CaptureMode InitialMode,
    string CatalogVersion,
    string CatalogHash,
    long MonotonicFrequency,
    string Architecture,
    string StorageRootToken) : IDiagnosticPayload
{
    public const string Name = "DiaSessionStarted";

    public string PayloadName => Name;

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteString("appVersion", AppVersion);
        writer.WriteString("runtimeVersion", RuntimeVersion);
        writer.WriteString("osVersion", OsVersion);
        writer.WriteNumber("processId", ProcessId);
        writer.WriteString("processStartUtc", ProcessStartUtc);
        writer.WriteString("processIdentity", ProcessIdentity);
        writer.WriteString("initialMode", InitialMode.ToString());
        writer.WriteString("catalogVersion", CatalogVersion);
        writer.WriteString("catalogHash", CatalogHash);
        writer.WriteNumber("monotonicFrequency", MonotonicFrequency);
        writer.WriteString("architecture", Architecture);
        writer.WriteString("storageRootToken", StorageRootToken);
    }
}

/// <summary>DIA.SessionCleanShutdown：只有真的完成 drain/封段才允许写。</summary>
public sealed record DiaSessionCleanShutdownPayload(
    long DrainedEvents,
    bool FlushAcknowledged,
    int SegmentsSealed,
    bool CleanMarkerWritten) : IDiagnosticPayload
{
    public const string Name = "DiaSessionCleanShutdown";

    public string PayloadName => Name;

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteNumber("drainedEvents", DrainedEvents);
        writer.WriteBoolean("flushAcknowledged", FlushAcknowledged);
        writer.WriteNumber("segmentsSealed", SegmentsSealed);
        writer.WriteBoolean("cleanMarkerWritten", CleanMarkerWritten);
    }
}

/// <summary>DIA.PreviousSessionUnclean：上次会话未确认正常关闭（**不等于**崩溃）。</summary>
public sealed record DiaPreviousSessionUncleanPayload(
    Guid PreviousSessionId,
    string ReasonCode,
    long? LastKnownSequence,
    int TailCompleteLines,
    bool MarkerWriteFailed) : IDiagnosticPayload
{
    public const string Name = "DiaPreviousSessionUnclean";

    public string PayloadName => Name;

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteString("previousSessionId", DiagnosticId.Format(PreviousSessionId));
        writer.WriteString("reasonCode", ReasonCode);
        PayloadJson.WriteNumberOrNull(writer, "lastKnownSequence", LastKnownSequence);
        writer.WriteNumber("tailCompleteLines", TailCompleteLines);
        writer.WriteBoolean("markerWriteFailed", MarkerWriteFailed);
    }
}

/// <summary>DIA.ModeChanged：采集模式变化（Off/Operational/Flight/Deep）。</summary>
public sealed record DiaModeChangedPayload(CaptureMode From, CaptureMode To, string ReasonCode) : IDiagnosticPayload
{
    public const string Name = "DiaModeChanged";

    public string PayloadName => Name;

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteString("from", From.ToString());
        writer.WriteString("to", To.ToString());
        writer.WriteString("reasonCode", ReasonCode);
    }
}

/// <summary>DIA.Backpressure：某分支达到容量（**满 ≠ 丢**：丢另由 EventsDropped/CriticalLost 报告）。</summary>
public sealed record DiaBackpressurePayload(
    string Branch,
    DeliveryClass DeliveryClass,
    int QueueDepth,
    int Capacity,
    long DroppedTotal) : IDiagnosticPayload
{
    public const string Name = "DiaBackpressure";

    public string PayloadName => Name;

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteString("branch", Branch);
        writer.WriteString("deliveryClass", DeliveryClass.ToString());
        writer.WriteNumber("queueDepth", QueueDepth);
        writer.WriteNumber("capacity", Capacity);
        writer.WriteNumber("droppedTotal", DroppedTotal);
    }
}

/// <summary>DIA.EventsDropped：真实丢弃计数与序号范围（缺事件规则据此降置信度）。</summary>
public sealed record DiaEventsDroppedPayload(
    string Branch,
    DeliveryClass DeliveryClass,
    long Count,
    long? FirstSequence,
    long? LastSequence,
    string ReasonCode) : IDiagnosticPayload
{
    public const string Name = "DiaEventsDropped";

    public string PayloadName => Name;

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteString("branch", Branch);
        writer.WriteString("deliveryClass", DeliveryClass.ToString());
        writer.WriteNumber("count", Count);
        PayloadJson.WriteNumberOrNull(writer, "firstSequence", FirstSequence);
        PayloadJson.WriteNumberOrNull(writer, "lastSequence", LastSequence);
        writer.WriteString("reasonCode", ReasonCode);
    }
}

/// <summary>DIA.CriticalLost：DurableCritical 事件丢失（sticky，必须可见）。</summary>
public sealed record DiaCriticalLostPayload(long Count, string ReasonCode, bool Sticky) : IDiagnosticPayload
{
    public const string Name = "DiaCriticalLost";

    public string PayloadName => Name;

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteNumber("count", Count);
        writer.WriteString("reasonCode", ReasonCode);
        writer.WriteBoolean("sticky", Sticky);
    }
}

/// <summary>DIA.StorageFailed：诊断自身存储失败（EmergencyFallbackActive 表示已切占位路径）。</summary>
public sealed record DiaStorageFailedPayload(
    string StorageKind,
    string ReasonCode,
    int? Win32Error,
    bool EmergencyFallbackActive) : IDiagnosticPayload
{
    public const string Name = "DiaStorageFailed";

    public string PayloadName => Name;

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteString("storageKind", StorageKind);
        writer.WriteString("reasonCode", ReasonCode);
        PayloadJson.WriteNumberOrNull(writer, "win32Error", Win32Error);
        writer.WriteBoolean("emergencyFallbackActive", EmergencyFallbackActive);
    }
}

/// <summary>DIA.StorageRecovered：存储恢复（自愈可见，不刷屏）。</summary>
public sealed record DiaStorageRecoveredPayload(string StorageKind, long OutageMs) : IDiagnosticPayload
{
    public const string Name = "DiaStorageRecovered";

    public string PayloadName => Name;

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteString("storageKind", StorageKind);
        writer.WriteNumber("outageMs", OutageMs);
    }
}

/// <summary>
/// DIA.SerializationFailed：某条事件无法序列化（丢弃该条并继续，不回退敏感原文）。
/// ★ 命名陷阱 ★ 字段名**不能**叫 <c>PayloadName</c>：那会与 <see cref="IDiagnosticPayload.PayloadName"/>
/// 同名，record 编译器会因此**不生成**该位置参数对应的属性 ⇒ 传进来的值被静默丢弃
/// （实测：CS8907 警告 + 该字段永远写成自身 payload 名）。故用 FailedPayloadName。
/// </summary>
public sealed record DiaSerializationFailedPayload(
    string EventCode,
    string ReasonCode,
    string? FailedPayloadName) : IDiagnosticPayload
{
    public const string Name = "DiaSerializationFailed";

    public string PayloadName => Name;

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteString("eventCode", EventCode);
        writer.WriteString("reasonCode", ReasonCode);
        PayloadJson.WriteStringOrNull(writer, "failedPayloadName", FailedPayloadName);
    }
}

/// <summary>DIA.AnalyzerLagged：在线分析器落后（不完全等于丢事件）。</summary>
public sealed record DiaAnalyzerLaggedPayload(int PendingCount, long LagMs) : IDiagnosticPayload
{
    public const string Name = "DiaAnalyzerLagged";

    public string PayloadName => Name;

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteNumber("pendingCount", PendingCount);
        writer.WriteNumber("lagMs", LagMs);
    }
}

/// <summary>DIA.SnapshotUnavailable：快照取不到（超时/UI 线程阻塞/来源不可用）。</summary>
public sealed record DiaSnapshotUnavailablePayload(
    string SnapshotKind,
    string ReasonCode,
    int TimeoutMs) : IDiagnosticPayload
{
    public const string Name = "DiaSnapshotUnavailable";

    public string PayloadName => Name;

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteString("snapshotKind", SnapshotKind);
        writer.WriteString("reasonCode", ReasonCode);
        writer.WriteNumber("timeoutMs", TimeoutMs);
    }
}

/// <summary>DIA.CoverageChanged：实际覆盖区间变化（"真实覆盖多少就写多少"）。</summary>
public sealed record DiaCoverageChangedPayload(
    CaptureMode Mode,
    string ReasonCode,
    DateTimeOffset? CoverageStartUtc,
    DateTimeOffset? CoverageEndUtc) : IDiagnosticPayload
{
    public const string Name = "DiaCoverageChanged";

    public string PayloadName => Name;

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteString("mode", Mode.ToString());
        writer.WriteString("reasonCode", ReasonCode);
        PayloadJson.WriteStringOrNull(writer, "coverageStartUtc", CoverageStartUtc?.ToString("O"));
        PayloadJson.WriteStringOrNull(writer, "coverageEndUtc", CoverageEndUtc?.ToString("O"));
    }
}

/// <summary>DIA.ShutdownIncomplete：关闭预算到期仍有未落盘事件（**不写"全部已保存"**）。</summary>
public sealed record DiaShutdownIncompletePayload(
    int PendingBranches,
    long DrainedEvents,
    long PendingEvents,
    int BudgetMs) : IDiagnosticPayload
{
    public const string Name = "DiaShutdownIncomplete";

    public string PayloadName => Name;

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteNumber("pendingBranches", PendingBranches);
        writer.WriteNumber("drainedEvents", DrainedEvents);
        writer.WriteNumber("pendingEvents", PendingEvents);
        writer.WriteNumber("budgetMs", BudgetMs);
    }
}

/// <summary>DIA.RingTriggered：Flight Recorder 触发（冻结前窗口，post 窗口随后由同一 scheduler 关闭）。</summary>
public sealed record DiaRingTriggeredPayload(
    string TriggerId,
    string TriggerCode,
    int RequestedPreWindowMs,
    int MaxPostWindowMs) : IDiagnosticPayload
{
    public const string Name = "DiaRingTriggered";

    public string PayloadName => Name;

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteString("triggerId", TriggerId);
        writer.WriteString("triggerCode", TriggerCode);
        writer.WriteNumber("requestedPreWindowMs", RequestedPreWindowMs);
        writer.WriteNumber("maxPostWindowMs", MaxPostWindowMs);
    }
}

/// <summary>
/// DIA.RingSealed：冻结段封存（**实际**覆盖多少就写多少，不允许假装保住了"前 60 秒"）。
/// </summary>
public sealed record DiaRingSealedPayload(
    string TriggerId,
    int ActualCoverageMs,
    long OverwrittenEvents,
    long DroppedEvents,
    bool PostWindowComplete,
    int CheckpointCount) : IDiagnosticPayload
{
    public const string Name = "DiaRingSealed";

    public string PayloadName => Name;

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteString("triggerId", TriggerId);
        writer.WriteNumber("actualCoverageMs", ActualCoverageMs);
        writer.WriteNumber("overwrittenEvents", OverwrittenEvents);
        writer.WriteNumber("droppedEvents", DroppedEvents);
        writer.WriteBoolean("postWindowComplete", PostWindowComplete);
        writer.WriteNumber("checkpointCount", CheckpointCount);
    }
}

/// <summary>DIA.HealthSummary：自健康摘要（Verbose；UI 另有内存 health 可读，不依赖它）。</summary>
public sealed record DiaHealthSummaryPayload(
    long Produced,
    long Accepted,
    long Written,
    long Dropped,
    long Evicted,
    long CriticalLost,
    bool StorageDegraded,
    long LossEpoch) : IDiagnosticPayload
{
    public const string Name = "DiaHealthSummary";

    public string PayloadName => Name;

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteNumber("produced", Produced);
        writer.WriteNumber("accepted", Accepted);
        writer.WriteNumber("written", Written);
        writer.WriteNumber("dropped", Dropped);
        writer.WriteNumber("evicted", Evicted);
        writer.WriteNumber("criticalLost", CriticalLost);
        writer.WriteBoolean("storageDegraded", StorageDegraded);
        writer.WriteNumber("lossEpoch", LossEpoch);
    }
}

/// <summary>DIA.ClockAnchorAdjusted：UTC 锚点跳变（duration 不回退，但跨点比较要带不确定度）。</summary>
public sealed record DiaClockAnchorAdjustedPayload(
    long JumpCount,
    string Direction,
    long DeltaMs) : IDiagnosticPayload
{
    public const string Name = "DiaClockAnchorAdjusted";

    public string PayloadName => Name;

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteNumber("jumpCount", JumpCount);
        writer.WriteString("direction", Direction);
        writer.WriteNumber("deltaMs", DeltaMs);
    }
}

/// <summary>
/// DIA.IncidentOpened / DIA.IncidentResolved：事件卡生命周期。
/// ★ 只带结构化字段（不含正文摘要）★：事件卡正文由 incidents.jsonl 承载，
/// 事件流里只留"哪张卡、什么状态、几版、证据是否完整"——便于离线重建时间线。
/// </summary>
public sealed record DiaIncidentPayload(
    string IncidentId,
    string RuleId,
    int RuleVersion,
    string Status,
    string Severity,
    string SymptomCode,
    string Confidence,
    bool EvidenceIncomplete,
    int Revision,
    string? BreakPoint) : IDiagnosticPayload
{
    public const string Name = "DiaIncident";

    public string PayloadName => Name;

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteString("incidentId", IncidentId);
        writer.WriteString("ruleId", RuleId);
        writer.WriteNumber("ruleVersion", RuleVersion);
        writer.WriteString("status", Status);
        writer.WriteString("severity", Severity);
        writer.WriteString("symptomCode", SymptomCode);
        writer.WriteString("confidence", Confidence);
        writer.WriteBoolean("evidenceIncomplete", EvidenceIncomplete);
        writer.WriteNumber("revision", Revision);
        PayloadJson.WriteStringOrNull(writer, "breakPoint", BreakPoint);
    }
}