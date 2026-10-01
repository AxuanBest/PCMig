using System.Text.Json;

namespace PCMig.Diagnostics.Abstractions.Payloads;

/// <summary>
/// FS 族扫描证据（D6.1 §16）。**只汇总既有统计**（对象/文件/字节/不可访问位置/不完整对象），
/// 不额外遍历任何目录、不改变跳过决策；策略条数用于回答"哪些规则影响了这次扫描"。
/// </summary>
public sealed record FsScanPayload(
    string Mode,
    int Objects,
    long Files,
    long Bytes,
    long Inaccessible,
    long IncompleteObjects,
    long LockRiskFiles,
    long EncryptedFiles,
    int ExcludedDirRules,
    int ExcludedFileRules,
    int SecurityBlockedRules,
    long ElapsedMs,
    string Completeness) : IDiagnosticPayload
{
    public const string Name = "FsScan";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteString("mode", Mode);
        w.WriteNumber("objects", Objects);
        w.WriteNumber("files", Files);
        w.WriteNumber("bytes", Bytes);
        w.WriteNumber("inaccessible", Inaccessible);
        w.WriteNumber("incompleteObjects", IncompleteObjects);
        w.WriteNumber("lockRiskFiles", LockRiskFiles);
        w.WriteNumber("encryptedFiles", EncryptedFiles);
        w.WriteNumber("excludedDirRules", ExcludedDirRules);
        w.WriteNumber("excludedFileRules", ExcludedFileRules);
        w.WriteNumber("securityBlockedRules", SecurityBlockedRules);
        w.WriteNumber("elapsedMs", ElapsedMs);
        w.WriteString("completeness", Completeness);
    }
}

/// <summary>
/// FS 失败聚合（D6.1 §16）：阶段（source/target/scan）+ 稳定原因码 + 计数。
/// **只汇总既有计数**（枚举期跳过的条目数），不做任何额外文件系统访问，也不改变跳过决策。
/// </summary>
public sealed record FsFailPayload(
    string Stage,
    long Count,
    string ReasonCode) : IDiagnosticPayload
{
    public const string Name = "FsFail";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteString("stage", Stage);
        w.WriteNumber("count", Count);
        w.WriteString("reasonCode", ReasonCode);
    }
}

/// <summary>计划阶段的选择快照摘要（只报规模，不重审目录树、不改变选择语义）。</summary>
public sealed record PlnSelectionPayload(
    int SourceRoots,
    int CustomSelections,
    int EnabledSources,
    int TreeSelectedNodes) : IDiagnosticPayload
{
    public const string Name = "PlnSelection";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteNumber("sourceRoots", SourceRoots);
        w.WriteNumber("customSelections", CustomSelections);
        w.WriteNumber("enabledSources", EnabledSources);
        w.WriteNumber("treeSelectedNodes", TreeSelectedNodes);
    }
}