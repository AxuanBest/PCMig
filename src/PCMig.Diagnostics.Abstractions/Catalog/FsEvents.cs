namespace PCMig.Diagnostics.Abstractions.Events;

/// <summary>
/// FS 族（4000 段）：扫描 / 枚举 / 路径策略 / 文件访问失败。
/// 纪律：<c>Directory.Exists == false</c> **不得**伪造成一个 NativeErrorCode；
/// 它只能作为"路径不存在或不可访问"这一事实（架构方案 §11）。
/// </summary>
public static class FsEvents
{
    public static readonly EventDescriptor ScanStarted = EventDescriptor.Define(
        DiagnosticCategory.Fs, 1, "FS.ScanStarted",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Personal, "FsScan");

    public static readonly EventDescriptor ScanCompleted = EventDescriptor.Define(
        DiagnosticCategory.Fs, 2, "FS.ScanCompleted",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Personal, "FsScan");

    /// <summary>扫描残缺（权限/异常导致条目不全）——"数量对不上"的第一候选原因。</summary>
    public static readonly EventDescriptor ScanIncomplete = EventDescriptor.Define(
        DiagnosticCategory.Fs, 3, "FS.ScanIncomplete",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Personal, "FsScan");

    public static readonly EventDescriptor EnumerateError = EventDescriptor.Define(
        DiagnosticCategory.Fs, 4, "FS.EnumerateError",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Personal, "FsFail");

    /// <summary>路径策略跳过（长路径/reparse/占位符/非法名）——合法行为，默认 Info。</summary>
    public static readonly EventDescriptor PathPolicySkip = EventDescriptor.Define(
        DiagnosticCategory.Fs, 5, "FS.PathPolicySkip",
        DiagnosticLevel.Information, DeliveryClass.Verbose, PrivacyClassification.Personal);

    public static readonly EventDescriptor StatFailure = EventDescriptor.Define(
        DiagnosticCategory.Fs, 6, "FS.StatFailure",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Personal, "FsFail");

    public static readonly EventDescriptor FileReadFailure = EventDescriptor.Define(
        DiagnosticCategory.Fs, 7, "FS.FileReadFailure",
        DiagnosticLevel.Error, DeliveryClass.Operational, PrivacyClassification.Personal);

    public static readonly EventDescriptor TargetStatFailure = EventDescriptor.Define(
        DiagnosticCategory.Fs, 8, "FS.TargetStatFailure",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Personal, "FsFail");

    /// <summary>路径不可用（存在性判定为否），**不带**伪造的原生错误码。</summary>
    public static readonly EventDescriptor DirectoryUnavailable = EventDescriptor.Define(
        DiagnosticCategory.Fs, 9, "FS.DirectoryUnavailable",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Personal, "FsFail");

    internal static readonly EventDescriptor[] All =
    {
        ScanStarted, ScanCompleted, ScanIncomplete, EnumerateError, PathPolicySkip,
        StatFailure, FileReadFailure, TargetStatFailure, DirectoryUnavailable,
    };
}