using System.Text.Json;

namespace PCMig.Diagnostics.Abstractions.Payloads;

/// <summary>
/// RPR 族载荷（D6.1 §16）。修复链路的证据：**谁要求修、目标从哪来、删了多少/失败多少、结果如何**。
/// 一律只陈述事实；本族**绝不发动**修复、不改变 repair 参数或判定。
/// </summary>
public sealed record RprRepairRequestedPayload(
    bool ForceOverwrite,
    int RequestedObjects) : IDiagnosticPayload
{
    public const string Name = "RprRepairRequested";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteBoolean("forceOverwrite", ForceOverwrite);
        w.WriteNumber("requestedObjects", RequestedObjects);
    }
}

/// <summary>
/// 修复目标来源：来自"验证报告里不一致的对象"与"回执里失败/有错/中断的对象"各多少。
/// `plannedObjects` = 计划里总对象数（用于解释"为什么没有目标"）。
/// </summary>
public sealed record RprTargetsPayload(
    int TargetCount,
    int BeforeMismatch,
    int FailedReceiptObjects,
    int PlannedObjects) : IDiagnosticPayload
{
    public const string Name = "RprTargets";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteNumber("targetCount", TargetCount);
        w.WriteNumber("beforeMismatch", BeforeMismatch);
        w.WriteNumber("failedReceiptObjects", FailedReceiptObjects);
        w.WriteNumber("plannedObjects", PlannedObjects);
    }
}

/// <summary>
/// 强制覆盖前的删除（purge）聚合：**删除成功数 / 跳过的大文件数 / 删除失败数**分开计。
/// 失败被静默会让"修复后仍不一致"变得无法解释，所以这里是本族最重要的一条。
/// </summary>
public sealed record RprPurgePayload(
    long Files,
    long Bytes,
    long SkippedLarge,
    long Failed,
    string ReasonCode) : IDiagnosticPayload
{
    public const string Name = "RprPurge";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteNumber("files", Files);
        w.WriteNumber("bytes", Bytes);
        w.WriteNumber("skippedLarge", SkippedLarge);
        w.WriteNumber("failed", Failed);
        w.WriteString("reasonCode", ReasonCode);
    }
}

/// <summary>修复结束：对象数 + 回执统计（只读既有回执，不新增判定）。</summary>
public sealed record RprCompletedPayload(
    int Objects,
    int OkObjects,
    int FailedObjects,
    bool ForceOverwrite) : IDiagnosticPayload
{
    public const string Name = "RprCompleted";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteNumber("objects", Objects);
        w.WriteNumber("okObjects", OkObjects);
        w.WriteNumber("failedObjects", FailedObjects);
        w.WriteBoolean("forceOverwrite", ForceOverwrite);
    }
}