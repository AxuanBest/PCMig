namespace PCMig.Diagnostics.Abstractions.Events;

/// <summary>
/// RBC 族（8000 段）：Robocopy 子进程生命周期。
/// ★ 关键口径修正 ★ 产品里那个 <c>FileCopied</c> 事件发生在**解析到文件行**时，
/// 它最多是 <see cref="FileAttemptObserved"/>（文件尝试），**绝不能**被解释成"文件复制成功"。
/// </summary>
public static class RobocopyEvents
{
    public static readonly EventDescriptor ProcessStartRequested = EventDescriptor.Define(
        DiagnosticCategory.Robocopy, 1, "RBC.ProcessStartRequested",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public);

    public static readonly EventDescriptor ProcessStarted = EventDescriptor.Define(
        DiagnosticCategory.Robocopy, 2, "RBC.ProcessStarted",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public, "RbcProcess");

    public static readonly EventDescriptor SpawnFailed = EventDescriptor.Define(
        DiagnosticCategory.Robocopy, 3, "RBC.SpawnFailed",
        DiagnosticLevel.Error, DeliveryClass.Operational, PrivacyClassification.Personal);

    /// <summary>原始输出行（高频 + 可能含路径 ⇒ Verbose/Personal，Deep 之外只做采样与聚合）。</summary>
    public static readonly EventDescriptor OutputObserved = EventDescriptor.Define(
        DiagnosticCategory.Robocopy, 4, "RBC.OutputObserved",
        DiagnosticLevel.Debug, DeliveryClass.Verbose, PrivacyClassification.Personal, "RbcFileSampleSummary");

    public static readonly EventDescriptor OutputParseFailed = EventDescriptor.Define(
        DiagnosticCategory.Robocopy, 5, "RBC.OutputParseFailed",
        DiagnosticLevel.Warning, DeliveryClass.Verbose, PrivacyClassification.Personal);

    /// <summary>解析到一个文件行（= 尝试，不是成功）。</summary>
    public static readonly EventDescriptor FileAttemptObserved = EventDescriptor.Define(
        DiagnosticCategory.Robocopy, 6, "RBC.FileAttemptObserved",
        DiagnosticLevel.Debug, DeliveryClass.Verbose, PrivacyClassification.Personal);

    public static readonly EventDescriptor ProcessExited = EventDescriptor.Define(
        DiagnosticCategory.Robocopy, 7, "RBC.ProcessExited",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public);

    public static readonly EventDescriptor KillRequested = EventDescriptor.Define(
        DiagnosticCategory.Robocopy, 8, "RBC.KillRequested",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Public, "RbcKill");

    /// <summary>终止调用结果（**KillRequested ≠ 已确认终止**，两者必须分开）。</summary>
    public static readonly EventDescriptor KillResult = EventDescriptor.Define(
        DiagnosticCategory.Robocopy, 9, "RBC.KillResult",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Public, "RbcKill");

    public static readonly EventDescriptor PassCompleted = EventDescriptor.Define(
        DiagnosticCategory.Robocopy, 10, "RBC.PassCompleted",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public);

    /// <summary>进程 Job 守卫装配失败（孤儿 robocopy 风险，如实记录，不改业务）。</summary>
    public static readonly EventDescriptor JobGuardAssignFailed = EventDescriptor.Define(
        DiagnosticCategory.Robocopy, 11, "RBC.JobGuardAssignFailed",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Public);

    /// <summary>错误行聚合（有界保留 + first/last/count，替代无限逐行保留）。</summary>
    public static readonly EventDescriptor ErrorLinesAggregated = EventDescriptor.Define(
        DiagnosticCategory.Robocopy, 12, "RBC.ErrorLinesAggregated",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Personal, "RbcErrorAggregate");

    internal static readonly EventDescriptor[] All =
    {
        ProcessStartRequested, ProcessStarted, SpawnFailed, OutputObserved, OutputParseFailed,
        FileAttemptObserved, ProcessExited, KillRequested, KillResult, PassCompleted,
        JobGuardAssignFailed, ErrorLinesAggregated,
    };
}