namespace PCMig.Diagnostics.Abstractions.Events;

/// <summary>
/// PFL 族（6000 段）：预检编排（DNS/IPC$/共享/源路径/目标卷/测速的**结论级**观测）。
/// 逐地址的网络事实在 NET 族；这里只记预检自己的阶段与结论。
/// </summary>
public static class PreflightEvents
{
    public static readonly EventDescriptor PreflightStarted = EventDescriptor.Define(
        DiagnosticCategory.Preflight, 1, "PFL.PreflightStarted",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Personal, "PflSummary");

    /// <summary>单项检查完成（用稳定 checkCode，不用中文检查名做判据）。</summary>
    public static readonly EventDescriptor CheckCompleted = EventDescriptor.Define(
        DiagnosticCategory.Preflight, 2, "PFL.CheckCompleted",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Personal, "PflCheck");

    public static readonly EventDescriptor SourcePathProbeResult = EventDescriptor.Define(
        DiagnosticCategory.Preflight, 3, "PFL.SourcePathProbeResult",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Personal, "PflSourceProbe");

    public static readonly EventDescriptor TargetVolumeObserved = EventDescriptor.Define(
        DiagnosticCategory.Preflight, 4, "PFL.TargetVolumeObserved",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Personal, "PflTargetVolume");

    /// <summary>链路吞吐基准（仅在用户显式开启 benchmark 时存在；诊断**不会**主动跑它）。</summary>
    public static readonly EventDescriptor BenchmarkCompleted = EventDescriptor.Define(
        DiagnosticCategory.Preflight, 5, "PFL.BenchmarkCompleted",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Personal, "PflBenchmark");

    public static readonly EventDescriptor PreflightCompleted = EventDescriptor.Define(
        DiagnosticCategory.Preflight, 6, "PFL.PreflightCompleted",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Personal, "PflSummary");

    internal static readonly EventDescriptor[] All =
    {
        PreflightStarted, CheckCompleted, SourcePathProbeResult, TargetVolumeObserved,
        BenchmarkCompleted, PreflightCompleted,
    };
}