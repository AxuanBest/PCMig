using System.Text.Json;

namespace PCMig.Diagnostics.Abstractions.Payloads;

/// <summary>
/// VRF 族载荷（D6.1 §16）。**只描述事实**：级别、对象数、两侧统计、抽样规模、失败原因、
/// 完成统计与完整性。**不含** OverallPass 判定逻辑，也不参与任何业务分支。
///
/// 隐私：路径一律走 envelope 的 <see cref="PathRef"/>（令牌化）；载荷里最多出现
/// "首个样本的令牌"（`firstSampleToken`），不出现明文文件名。
/// </summary>
public sealed record VrfVerifyStartedPayload(
    string Level,
    int ObjectCount,
    int SampleHashPercent) : IDiagnosticPayload
{
    public const string Name = "VrfVerifyStarted";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteString("level", Level);
        w.WriteNumber("objectCount", ObjectCount);
        w.WriteNumber("sampleHashPercent", SampleHashPercent);
    }
}

/// <summary>计划可用性观察：对象数 + 是否**每个对象都有源/目标路径**（只报事实，不做业务校验）。</summary>
public sealed record VrfPlanValidatedPayload(
    int ObjectCount,
    bool AllObjectsHavePaths,
    string PlanSource) : IDiagnosticPayload
{
    public const string Name = "VrfPlanValidated";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteNumber("objectCount", ObjectCount);
        w.WriteBoolean("allObjectsHavePaths", AllObjectsHavePaths);
        w.WriteString("planSource", PlanSource);
    }
}

/// <summary>单侧枚举统计（源/目标共用同一形状；<c>role</c> 区分）。</summary>
public sealed record VrfSideStatPayload(
    string Role,
    long Files,
    long Bytes,
    bool Overflow,
    long EnumerationErrors,
    long ElapsedMs) : IDiagnosticPayload
{
    public const string Name = "VrfSideStat";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteString("role", Role);
        w.WriteNumber("files", Files);
        w.WriteNumber("bytes", Bytes);
        w.WriteBoolean("overflow", Overflow);
        // ★ 枚举期被跳过的条目数 ★ 没有它，"OK" 就无法与"其实很多项没枚举到"区分。
        w.WriteNumber("enumerationErrors", EnumerationErrors);
        w.WriteNumber("elapsedMs", ElapsedMs);
    }
}

/// <summary>抽样规模（清单模式 / 流式模式）。</summary>
public sealed record VrfHashSamplePayload(
    string Mode,
    int CandidateFiles,
    int SampleCount,
    int SamplePercent) : IDiagnosticPayload
{
    public const string Name = "VrfHashSample";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteString("mode", Mode);
        w.WriteNumber("candidateFiles", CandidateFiles);
        w.WriteNumber("sampleCount", SampleCount);
        w.WriteNumber("samplePercent", SamplePercent);
    }
}

/// <summary>一个对象的哈希结果（**抽样数 / 不一致数 / 哈希本身失败数**分开记）。</summary>
public sealed record VrfHashResultPayload(
    long Sampled,
    long Mismatched,
    long Failed) : IDiagnosticPayload
{
    public const string Name = "VrfHashResult";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteNumber("sampled", Sampled);
        w.WriteNumber("mismatched", Mismatched);
        w.WriteNumber("failed", Failed);
    }
}

/// <summary>不一致（按类别计数；首个样本只给**令牌**）。</summary>
public sealed record VrfMismatchPayload(
    string Kind,
    long Count,
    string? FirstSampleToken) : IDiagnosticPayload
{
    public const string Name = "VrfMismatch";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteString("kind", Kind);
        w.WriteNumber("count", Count);
        PayloadJson.WriteStringOrNull(w, "firstSampleToken", FirstSampleToken);
    }
}

/// <summary>验证过程中的错误/失败（阶段 + 稳定原因码 + 计数；不写异常原文）。</summary>
public sealed record VrfFailurePayload(
    string Stage,
    string ReasonCode,
    long Count) : IDiagnosticPayload
{
    public const string Name = "VrfFailure";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteString("stage", Stage);
        w.WriteString("reasonCode", ReasonCode);
        w.WriteNumber("count", Count);
    }
}

/// <summary>验证结束的统计事实（与业务 OverallPass 并列陈述，不替代它）。</summary>
public sealed record VrfCompletedPayload(
    int ObjectCount,
    bool OverallPass,
    long HashSampled,
    long HashMismatched,
    long MissingTotal,
    string StatsCompleteness) : IDiagnosticPayload
{
    public const string Name = "VrfCompleted";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteNumber("objectCount", ObjectCount);
        w.WriteBoolean("overallPass", OverallPass);
        w.WriteNumber("hashSampled", HashSampled);
        w.WriteNumber("hashMismatched", HashMismatched);
        w.WriteNumber("missingTotal", MissingTotal);
        w.WriteString("statsCompleteness", StatsCompleteness);
    }
}

/// <summary>
/// 统计完整性（Complete / Partial / Unknown）——"0/0 也是 OK"这类形态的第一证据。
/// 判定口径：枚举期无跳过且无流式降级 = Complete；否则 Partial；根本没跑完 = Unknown。
/// </summary>
public sealed record VrfStatsCompletenessPayload(
    string Status,
    long EnumerationErrors,
    bool AnyOverflow,
    string Note) : IDiagnosticPayload
{
    public const string Name = "VrfStatsCompleteness";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteString("status", Status);
        w.WriteNumber("enumerationErrors", EnumerationErrors);
        w.WriteBoolean("anyOverflow", AnyOverflow);
        w.WriteString("note", Note);
    }
}