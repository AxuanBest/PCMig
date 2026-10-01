namespace PCMig.Diagnostics.Abstractions.Events;

/// <summary>RPR 族（11000 段）：修复（定向重拷 + 强制覆盖前删除）。只观察，绝不发动修复。</summary>
public static class RepairEvents
{
    public static readonly EventDescriptor RepairRequested = EventDescriptor.Define(
        DiagnosticCategory.Repair, 1, "RPR.RepairRequested",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public, "RprRepairRequested");

    public static readonly EventDescriptor RepairTargetsCollected = EventDescriptor.Define(
        DiagnosticCategory.Repair, 2, "RPR.RepairTargetsCollected",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public, "RprTargets");

    public static readonly EventDescriptor RepairNoTargets = EventDescriptor.Define(
        DiagnosticCategory.Repair, 3, "RPR.RepairNoTargets",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public, "RprTargets");

    public static readonly EventDescriptor PurgeAttempted = EventDescriptor.Define(
        DiagnosticCategory.Repair, 4, "RPR.PurgeAttempted",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Personal, "RprPurge");

    public static readonly EventDescriptor PurgeFailed = EventDescriptor.Define(
        DiagnosticCategory.Repair, 5, "RPR.PurgeFailed",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Personal, "RprPurge");

    public static readonly EventDescriptor RepairCompleted = EventDescriptor.Define(
        DiagnosticCategory.Repair, 6, "RPR.RepairCompleted",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public, "RprCompleted");

    internal static readonly EventDescriptor[] All =
    {
        RepairRequested, RepairTargetsCollected, RepairNoTargets, PurgeAttempted,
        PurgeFailed, RepairCompleted,
    };
}