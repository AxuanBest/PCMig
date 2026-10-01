namespace PCMig.Diagnostics.Abstractions.Events;

/// <summary>PLN 族（5000 段）：选择 → 扫描 → 计划的映射事实。只读比对，不另造 Planner。</summary>
public static class PlanEvents
{
    public static readonly EventDescriptor PlanRequested = EventDescriptor.Define(
        DiagnosticCategory.Plan, 1, "PLN.PlanRequested",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public, "PlnPlanRequested");

    /// <summary>已提交选择的摘要（数量/根集合/指纹，不含完整文件清单）。</summary>
    public static readonly EventDescriptor SelectionSnapshot = EventDescriptor.Define(
        DiagnosticCategory.Plan, 2, "PLN.SelectionSnapshot",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Personal, "PlnSelection");

    public static readonly EventDescriptor PlanCreated = EventDescriptor.Define(
        DiagnosticCategory.Plan, 3, "PLN.PlanCreated",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Personal, "PlnPlanCreated");

    /// <summary>计划为空（0 对象）——按失败处理的事实（防空计划被读成"迁移完成"）。</summary>
    public static readonly EventDescriptor PlanEmpty = EventDescriptor.Define(
        DiagnosticCategory.Plan, 4, "PLN.PlanEmpty",
        DiagnosticLevel.Error, DeliveryClass.Operational, PrivacyClassification.Public, "PlnPlanEmpty");

    public static readonly EventDescriptor PlanValidationFailed = EventDescriptor.Define(
        DiagnosticCategory.Plan, 5, "PLN.PlanValidationFailed",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Public);

    internal static readonly EventDescriptor[] All =
    {
        PlanRequested, SelectionSnapshot, PlanCreated, PlanEmpty, PlanValidationFailed,
    };
}