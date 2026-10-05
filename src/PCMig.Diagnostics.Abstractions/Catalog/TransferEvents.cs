namespace PCMig.Diagnostics.Abstractions.Events;

/// <summary>
/// TRN 族（7000 段）：传输编排（Job/Object/重试/进度/暂停恢复停止）。
/// 纪律：暂停/恢复/停止只**观察**请求与结果，绝不代为改变业务状态（不改 CanResume、不删 pause.request）。
/// </summary>
public static class TransferEvents
{
    public static readonly EventDescriptor JobRunStarted = EventDescriptor.Define(
        DiagnosticCategory.Transfer, 1, "TRN.JobRunStarted",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public);

    public static readonly EventDescriptor JobRunCompleted = EventDescriptor.Define(
        DiagnosticCategory.Transfer, 2, "TRN.JobRunCompleted",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public);

    public static readonly EventDescriptor ObjectStarted = EventDescriptor.Define(
        DiagnosticCategory.Transfer, 3, "TRN.ObjectStarted",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Personal, "TrnObject");

    public static readonly EventDescriptor ObjectCompleted = EventDescriptor.Define(
        DiagnosticCategory.Transfer, 4, "TRN.ObjectCompleted",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Personal, "TrnObject");

    public static readonly EventDescriptor ObjectInterrupted = EventDescriptor.Define(
        DiagnosticCategory.Transfer, 5, "TRN.ObjectInterrupted",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Personal);

    public static readonly EventDescriptor RetryScheduled = EventDescriptor.Define(
        DiagnosticCategory.Transfer, 6, "TRN.RetryScheduled",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Personal, "TrnRetry");

    public static readonly EventDescriptor RetryStarted = EventDescriptor.Define(
        DiagnosticCategory.Transfer, 7, "TRN.RetryStarted",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Personal, "TrnRetry");

    /// <summary>进度观测（高频 ⇒ Verbose；字节口径必须标 source=approx/raw/stat/receipt）。</summary>
    public static readonly EventDescriptor ProgressObserved = EventDescriptor.Define(
        DiagnosticCategory.Transfer, 8, "TRN.ProgressObserved",
        DiagnosticLevel.Debug, DeliveryClass.Verbose, PrivacyClassification.Public, "TrnProgress");

    public static readonly EventDescriptor PauseRequested = EventDescriptor.Define(
        DiagnosticCategory.Transfer, 9, "TRN.PauseRequested",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public, "TrnPauseObserved");

    /// <summary>暂停请求文件写入结果（写失败是必须先说的事实，否则"点了没反应"无从判断）。</summary>
    public static readonly EventDescriptor PauseRequestWriteResult = EventDescriptor.Define(
        DiagnosticCategory.Transfer, 10, "TRN.PauseRequestWriteResult",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Personal, "TrnPauseRequest");

    public static readonly EventDescriptor PauseObserved = EventDescriptor.Define(
        DiagnosticCategory.Transfer, 11, "TRN.PauseObserved",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public, "TrnPauseObserved");

    public static readonly EventDescriptor PauseBoundaryReached = EventDescriptor.Define(
        DiagnosticCategory.Transfer, 12, "TRN.PauseBoundaryReached",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public);

    public static readonly EventDescriptor Paused = EventDescriptor.Define(
        DiagnosticCategory.Transfer, 13, "TRN.Paused",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public);

    public static readonly EventDescriptor ResumeRequested = EventDescriptor.Define(
        DiagnosticCategory.Transfer, 14, "TRN.ResumeRequested",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public, "TrnPauseObserved");

    /// <summary>暂停请求被清除（恢复路径的第一步事实）。</summary>
    public static readonly EventDescriptor PauseRequestCleared = EventDescriptor.Define(
        DiagnosticCategory.Transfer, 15, "TRN.PauseRequestCleared",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public, "TrnPauseRequest");

    public static readonly EventDescriptor CheckpointLoaded = EventDescriptor.Define(
        DiagnosticCategory.Transfer, 16, "TRN.CheckpointLoaded",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public);

    public static readonly EventDescriptor Resumed = EventDescriptor.Define(
        DiagnosticCategory.Transfer, 17, "TRN.Resumed",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public, "TrnPauseObserved");

    public static readonly EventDescriptor StopRequested = EventDescriptor.Define(
        DiagnosticCategory.Transfer, 18, "TRN.StopRequested",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public, "TrnPauseObserved");

    public static readonly EventDescriptor StopObserved = EventDescriptor.Define(
        DiagnosticCategory.Transfer, 19, "TRN.StopObserved",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public);

    /// <summary>根目录散落文件通道收尾后强制目标根可见（业务既有行为，只观察）。</summary>
    public static readonly EventDescriptor RootFilesVisibilityEnsured = EventDescriptor.Define(
        DiagnosticCategory.Transfer, 20, "TRN.RootFilesVisibilityEnsured",
        DiagnosticLevel.Information, DeliveryClass.Verbose, PrivacyClassification.Personal);

    /// <summary>引擎提示（单发失败通知，例如磁盘满/空间不足）。</summary>
    public static readonly EventDescriptor TransferNoticeRaised = EventDescriptor.Define(
        DiagnosticCategory.Transfer, 21, "TRN.TransferNoticeRaised",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Public);

    public static readonly EventDescriptor SpaceAbortRequested = EventDescriptor.Define(
        DiagnosticCategory.Transfer, 22, "TRN.SpaceAbortRequested",
        DiagnosticLevel.Error, DeliveryClass.Operational, PrivacyClassification.Public);

    /// <summary>
    /// 暂停**失败**（Trust-Critical Recovery FIX BATCH 1）：请求已被引擎读到并受理，
    /// 但在硬失败 SLA 内 worker 拒绝停止 ⇒ 迁移仍在进行。
    /// 与 <see cref="PauseRequested"/> 的区别就是"请求受理"和"业务效果达成"的区别：
    /// 真机上只有前者、没有后者，事件卡却是 0、健康却是 healthy —— 这条事件是那道防线的输入。
    /// </summary>
    public static readonly EventDescriptor PauseFailed = EventDescriptor.Define(
        DiagnosticCategory.Transfer, 23, "TRN.PauseFailed",
        DiagnosticLevel.Error, DeliveryClass.Operational, PrivacyClassification.Public, "TrnPauseObserved");

    internal static readonly EventDescriptor[] All =
    {
        JobRunStarted, JobRunCompleted, ObjectStarted, ObjectCompleted, ObjectInterrupted,
        RetryScheduled, RetryStarted, ProgressObserved, PauseRequested, PauseRequestWriteResult,
        PauseObserved, PauseBoundaryReached, Paused, ResumeRequested, PauseRequestCleared,
        CheckpointLoaded, Resumed, StopRequested, StopObserved, RootFilesVisibilityEnsured,
        TransferNoticeRaised, SpaceAbortRequested, PauseFailed,
    };
}