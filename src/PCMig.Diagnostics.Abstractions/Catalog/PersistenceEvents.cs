namespace PCMig.Diagnostics.Abstractions.Events;

/// <summary>
/// PST 族（9000 段）：存档/回执的真实写入阶段。
/// ★ 为什么要这个族 ★ 现在"<c>SaveReceipt()</c> 返回了"与"回执真的落盘了"是两件事：
/// WriteAtomic 在目标文件已存在且被占用时**只告警并返回**。本族把内部真实分支暴露出来，
/// 但**绝不修改**原 persistence 语义（架构方案 §18）。
/// </summary>
public static class PersistenceEvents
{
    public static readonly EventDescriptor WriteStarted = EventDescriptor.Define(
        DiagnosticCategory.Persistence, 1, "PST.WriteStarted",
        DiagnosticLevel.Debug, DeliveryClass.Verbose, PrivacyClassification.Personal, "PstWrite");

    /// <summary>写入 API 完成（**不等于**断电安全，flush ack 另见 <see cref="FlushAcknowledged"/>）。</summary>
    public static readonly EventDescriptor WriteSucceeded = EventDescriptor.Define(
        DiagnosticCategory.Persistence, 2, "PST.WriteSucceeded",
        DiagnosticLevel.Debug, DeliveryClass.Operational, PrivacyClassification.Personal, "PstWrite");

    /// <summary>写入被跳过（目标被占用且已存在旧值）：DurableCritical，因为它直接关系数据可靠性表述。</summary>
    public static readonly EventDescriptor WriteSkipped = EventDescriptor.Define(
        DiagnosticCategory.Persistence, 3, "PST.WriteSkipped",
        DiagnosticLevel.Warning, DeliveryClass.DurableCritical, PrivacyClassification.Personal, "PstWrite");

    public static readonly EventDescriptor WriteFailed = EventDescriptor.Define(
        DiagnosticCategory.Persistence, 4, "PST.WriteFailed",
        DiagnosticLevel.Error, DeliveryClass.DurableCritical, PrivacyClassification.Personal, "PstWrite");

    public static readonly EventDescriptor ReadFailed = EventDescriptor.Define(
        DiagnosticCategory.Persistence, 5, "PST.ReadFailed",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Personal, "PstReadFailure");

    /// <summary>损坏的回执被跳过（业务既有行为：跳过并继续）。</summary>
    public static readonly EventDescriptor ReceiptCorrupt = EventDescriptor.Define(
        DiagnosticCategory.Persistence, 6, "PST.ReceiptCorrupt",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Personal, "PstReadFailure");

    public static readonly EventDescriptor CheckpointLoaded = EventDescriptor.Define(
        DiagnosticCategory.Persistence, 7, "PST.CheckpointLoaded",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Personal);

    /// <summary>显式 flush 得到成功 ack（只有收到 ack 才允许称 DiskFlushAcknowledged）。</summary>
    public static readonly EventDescriptor FlushAcknowledged = EventDescriptor.Define(
        DiagnosticCategory.Persistence, 8, "PST.FlushAcknowledged",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public);

    internal static readonly EventDescriptor[] All =
    {
        WriteStarted, WriteSucceeded, WriteSkipped, WriteFailed, ReadFailed,
        ReceiptCorrupt, CheckpointLoaded, FlushAcknowledged,
    };
}