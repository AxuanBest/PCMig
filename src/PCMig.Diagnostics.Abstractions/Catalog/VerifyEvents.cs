namespace PCMig.Diagnostics.Abstractions.Events;

/// <summary>
/// VRF 族（10000 段）：验证器的证据类观测。
/// ★ 本阶段只埋证据 ★ 不修改 <c>OverallPass</c> 判定、不写回 report：
/// 诊断可以指出"业务说 OK 但证据不完整"，那正是 Stage B 的输入（架构方案 §19）。
/// </summary>
public static class VerifyEvents
{
    public static readonly EventDescriptor VerifyStarted = EventDescriptor.Define(
        DiagnosticCategory.Verify, 1, "VRF.VerifyStarted",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public, "VrfVerifyStarted");

    public static readonly EventDescriptor PlanValidated = EventDescriptor.Define(
        DiagnosticCategory.Verify, 2, "VRF.PlanValidated",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public, "VrfPlanValidated");

    public static readonly EventDescriptor SourceStatStarted = EventDescriptor.Define(
        DiagnosticCategory.Verify, 3, "VRF.SourceStatStarted",
        DiagnosticLevel.Debug, DeliveryClass.Verbose, PrivacyClassification.Personal, "VrfSideStat");

    public static readonly EventDescriptor SourceStatSucceeded = EventDescriptor.Define(
        DiagnosticCategory.Verify, 4, "VRF.SourceStatSucceeded",
        DiagnosticLevel.Debug, DeliveryClass.Verbose, PrivacyClassification.Personal, "VrfSideStat");

    public static readonly EventDescriptor SourceStatFailed = EventDescriptor.Define(
        DiagnosticCategory.Verify, 5, "VRF.SourceStatFailed",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Personal, "VrfSideStat");

    public static readonly EventDescriptor TargetStatStarted = EventDescriptor.Define(
        DiagnosticCategory.Verify, 6, "VRF.TargetStatStarted",
        DiagnosticLevel.Debug, DeliveryClass.Verbose, PrivacyClassification.Personal, "VrfSideStat");

    public static readonly EventDescriptor TargetStatSucceeded = EventDescriptor.Define(
        DiagnosticCategory.Verify, 7, "VRF.TargetStatSucceeded",
        DiagnosticLevel.Debug, DeliveryClass.Verbose, PrivacyClassification.Personal, "VrfSideStat");

    public static readonly EventDescriptor TargetStatFailed = EventDescriptor.Define(
        DiagnosticCategory.Verify, 8, "VRF.TargetStatFailed",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Personal, "VrfSideStat");

    public static readonly EventDescriptor SampleSelected = EventDescriptor.Define(
        DiagnosticCategory.Verify, 9, "VRF.SampleSelected",
        DiagnosticLevel.Debug, DeliveryClass.Verbose, PrivacyClassification.Personal, "VrfHashSample");

    public static readonly EventDescriptor HashStarted = EventDescriptor.Define(
        DiagnosticCategory.Verify, 10, "VRF.HashStarted",
        DiagnosticLevel.Debug, DeliveryClass.Verbose, PrivacyClassification.Personal, "VrfHashSample");

    public static readonly EventDescriptor HashSucceeded = EventDescriptor.Define(
        DiagnosticCategory.Verify, 11, "VRF.HashSucceeded",
        DiagnosticLevel.Debug, DeliveryClass.Verbose, PrivacyClassification.Personal, "VrfHashResult");

    /// <summary>哈希计算本身失败（**不得**计成 mismatch——现有业务口径也要保持这样）。</summary>
    public static readonly EventDescriptor HashFailed = EventDescriptor.Define(
        DiagnosticCategory.Verify, 12, "VRF.HashFailed",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Personal, "VrfFailure");

    public static readonly EventDescriptor Mismatch = EventDescriptor.Define(
        DiagnosticCategory.Verify, 13, "VRF.Mismatch",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Personal, "VrfMismatch");

    public static readonly EventDescriptor Error = EventDescriptor.Define(
        DiagnosticCategory.Verify, 14, "VRF.Error",
        DiagnosticLevel.Error, DeliveryClass.Operational, PrivacyClassification.Personal, "VrfFailure");

    public static readonly EventDescriptor Completed = EventDescriptor.Define(
        DiagnosticCategory.Verify, 15, "VRF.Completed",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public, "VrfCompleted");

    /// <summary>统计完整性（Complete/Partial/Unknown）——"0/0 也是 OK"这类形态的第一证据。</summary>
    public static readonly EventDescriptor StatsCompletenessObserved = EventDescriptor.Define(
        DiagnosticCategory.Verify, 16, "VRF.StatsCompletenessObserved",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Personal, "VrfStatsCompleteness");

    internal static readonly EventDescriptor[] All =
    {
        VerifyStarted, PlanValidated, SourceStatStarted, SourceStatSucceeded, SourceStatFailed,
        TargetStatStarted, TargetStatSucceeded, TargetStatFailed, SampleSelected, HashStarted,
        HashSucceeded, HashFailed, Mismatch, Error, Completed, StatsCompletenessObserved,
    };
}