using System.Text.Json;

namespace PCMig.Diagnostics.Abstractions.Payloads;

/// <summary>
/// PLN 族载荷（D6.1 §16）。计划产出的**因果证据**：谁请求、输入规模多大、产出多少对象、
/// 总字节、应用了多少条排除规则。
///
/// 纪律：**不做**任何计划校验、不参与对象排序/目标映射决策、**不额外扫描**任何东西；
/// 全部字段都来自调用方已经拿在手里的值。
/// </summary>
public sealed record PlnPlanRequestedPayload(
    int Sources,
    int ObservedObjects,
    bool SplitLargeFiles,
    bool CustomSelectionMode) : IDiagnosticPayload
{
    public const string Name = "PlnPlanRequested";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteNumber("sources", Sources);
        w.WriteNumber("observedObjects", ObservedObjects);
        w.WriteBoolean("splitLargeFiles", SplitLargeFiles);
        w.WriteBoolean("customSelectionMode", CustomSelectionMode);
    }
}

/// <summary>
/// 计划产出摘要。`objectsWithoutSourceRoot` 是回答"树选择与 plan 是否一致"这类问题的**只读**输入
/// （只报计数，不重审目录树、不改映射）。
/// </summary>
public sealed record PlnPlanCreatedPayload(
    int Objects,
    long TotalBytes,
    long LargestObjectBytes,
    int ExcludedRules,
    int ObjectsWithoutSourceRoot,
    int RestartablePassObjects) : IDiagnosticPayload
{
    public const string Name = "PlnPlanCreated";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteNumber("objects", Objects);
        w.WriteNumber("totalBytes", TotalBytes);
        w.WriteNumber("largestObjectBytes", LargestObjectBytes);
        w.WriteNumber("excludedRules", ExcludedRules);
        w.WriteNumber("objectsWithoutSourceRoot", ObjectsWithoutSourceRoot);
        w.WriteNumber("restartablePassObjects", RestartablePassObjects);
    }
}

/// <summary>空计划：这是"计划里没有任何对象"的**第一手事实**（而不是让人从界面猜测）。</summary>
public sealed record PlnPlanEmptyPayload(
    int ObservedObjects,
    int Sources,
    string ReasonCode) : IDiagnosticPayload
{
    public const string Name = "PlnPlanEmpty";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteNumber("observedObjects", ObservedObjects);
        w.WriteNumber("sources", Sources);
        w.WriteString("reasonCode", ReasonCode);
    }
}