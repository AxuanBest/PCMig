namespace PCMig.Diagnostics.Abstractions.Events;

/// <summary>
/// DIA 族（12000 段）：诊断系统**自己**的生命周期、降级与导出。
/// 纪律：这一族由 runtime 自身发布，且自身 health 不得再经由普通 logger 写回自己（避免递归风暴）。
/// </summary>
public static class DiagnosticsEvents
{
    public static readonly EventDescriptor SessionStarted = EventDescriptor.Define(
        DiagnosticCategory.Diagnostics, 1, "DIA.SessionStarted",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Personal,
        "DiaSessionStarted");

    /// <summary>正常关闭完成（**只有真的完成 drain/封段才允许写**）。</summary>
    public static readonly EventDescriptor SessionCleanShutdown = EventDescriptor.Define(
        DiagnosticCategory.Diagnostics, 2, "DIA.SessionCleanShutdown",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public,
        "DiaSessionCleanShutdown");

    /// <summary>上次会话未确认正常关闭（**不得**直接说"上次崩溃了"）。</summary>
    public static readonly EventDescriptor PreviousSessionUnclean = EventDescriptor.Define(
        DiagnosticCategory.Diagnostics, 3, "DIA.PreviousSessionUnclean",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Personal,
        "DiaPreviousSessionUnclean");

    public static readonly EventDescriptor ModeChanged = EventDescriptor.Define(
        DiagnosticCategory.Diagnostics, 4, "DIA.ModeChanged",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public,
        "DiaModeChanged");

    public static readonly EventDescriptor Backpressure = EventDescriptor.Define(
        DiagnosticCategory.Diagnostics, 5, "DIA.Backpressure",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Public,
        "DiaBackpressure");

    public static readonly EventDescriptor EventsDropped = EventDescriptor.Define(
        DiagnosticCategory.Diagnostics, 6, "DIA.EventsDropped",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Public,
        "DiaEventsDropped");

    public static readonly EventDescriptor CriticalLost = EventDescriptor.Define(
        DiagnosticCategory.Diagnostics, 7, "DIA.CriticalLost",
        DiagnosticLevel.Error, DeliveryClass.DurableCritical, PrivacyClassification.Public,
        "DiaCriticalLost");

    public static readonly EventDescriptor StorageFailed = EventDescriptor.Define(
        DiagnosticCategory.Diagnostics, 8, "DIA.StorageFailed",
        DiagnosticLevel.Error, DeliveryClass.DurableCritical, PrivacyClassification.Personal,
        "DiaStorageFailed");

    public static readonly EventDescriptor StorageRecovered = EventDescriptor.Define(
        DiagnosticCategory.Diagnostics, 9, "DIA.StorageRecovered",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public,
        "DiaStorageRecovered");

    public static readonly EventDescriptor SerializationFailed = EventDescriptor.Define(
        DiagnosticCategory.Diagnostics, 10, "DIA.SerializationFailed",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Public,
        "DiaSerializationFailed");

    public static readonly EventDescriptor AnalyzerLagged = EventDescriptor.Define(
        DiagnosticCategory.Diagnostics, 11, "DIA.AnalyzerLagged",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Public,
        "DiaAnalyzerLagged");

    public static readonly EventDescriptor SnapshotUnavailable = EventDescriptor.Define(
        DiagnosticCategory.Diagnostics, 12, "DIA.SnapshotUnavailable",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Public,
        "DiaSnapshotUnavailable");

    public static readonly EventDescriptor CoverageChanged = EventDescriptor.Define(
        DiagnosticCategory.Diagnostics, 13, "DIA.CoverageChanged",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public,
        "DiaCoverageChanged");

    /// <summary>关闭时未能完成 drain/封段（与 SessionCleanShutdown 互斥地如实报告）。</summary>
    public static readonly EventDescriptor ShutdownIncomplete = EventDescriptor.Define(
        DiagnosticCategory.Diagnostics, 14, "DIA.ShutdownIncomplete",
        DiagnosticLevel.Warning, DeliveryClass.DurableCritical, PrivacyClassification.Public,
        "DiaShutdownIncomplete");

    public static readonly EventDescriptor RingTriggered = EventDescriptor.Define(
        DiagnosticCategory.Diagnostics, 15, "DIA.RingTriggered",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public,
        "DiaRingTriggered");

    public static readonly EventDescriptor RingSealed = EventDescriptor.Define(
        DiagnosticCategory.Diagnostics, 16, "DIA.RingSealed",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public,
        "DiaRingSealed");

    public static readonly EventDescriptor IncidentOpened = EventDescriptor.Define(
        DiagnosticCategory.Diagnostics, 17, "DIA.IncidentOpened",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Public,
        "DiaIncident");

    public static readonly EventDescriptor IncidentResolved = EventDescriptor.Define(
        DiagnosticCategory.Diagnostics, 18, "DIA.IncidentResolved",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public,
        "DiaIncident");

    public static readonly EventDescriptor ExportStarted = EventDescriptor.Define(
        DiagnosticCategory.Diagnostics, 19, "DIA.ExportStarted",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public);

    public static readonly EventDescriptor ExportCompleted = EventDescriptor.Define(
        DiagnosticCategory.Diagnostics, 20, "DIA.ExportCompleted",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public);

    public static readonly EventDescriptor ExportFailed = EventDescriptor.Define(
        DiagnosticCategory.Diagnostics, 21, "DIA.ExportFailed",
        DiagnosticLevel.Error, DeliveryClass.Operational, PrivacyClassification.Public);

    /// <summary>周期性自健康摘要（Verbose：允许被丢，UI 另有内存 health 可读）。</summary>
    public static readonly EventDescriptor HealthSummary = EventDescriptor.Define(
        DiagnosticCategory.Diagnostics, 22, "DIA.HealthSummary",
        DiagnosticLevel.Debug, DeliveryClass.Verbose, PrivacyClassification.Public,
        "DiaHealthSummary");

    /// <summary>
    /// UTC/单调时钟锚点发生跳变（改系统时间、NTP 校正、休眠恢复）。
    /// 单调 duration 不回退，但跨该点的时间比较必须带上这个不确定度（方案 §8）。
    /// </summary>
    public static readonly EventDescriptor ClockAnchorAdjusted = EventDescriptor.Define(
        DiagnosticCategory.Diagnostics, 23, "DIA.ClockAnchorAdjusted",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Public,
        "DiaClockAnchorAdjusted");

    internal static readonly EventDescriptor[] All =
    {
        SessionStarted, SessionCleanShutdown, PreviousSessionUnclean, ModeChanged, Backpressure,
        EventsDropped, CriticalLost, StorageFailed, StorageRecovered, SerializationFailed,
        AnalyzerLagged, SnapshotUnavailable, CoverageChanged, ShutdownIncomplete, RingTriggered,
        RingSealed, IncidentOpened, IncidentResolved, ExportStarted, ExportCompleted,
        ExportFailed, HealthSummary, ClockAnchorAdjusted,
    };
}