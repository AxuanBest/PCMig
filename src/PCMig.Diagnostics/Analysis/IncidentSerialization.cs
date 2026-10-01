using System.Text.Json;
using System.Text.Json.Serialization;
using PCMig.Diagnostics.Abstractions;

namespace PCMig.Diagnostics.Analysis;

/// <summary>
/// 事件卡的落盘形态（incidents.jsonl 一行一张卡）。
///
/// 为什么单独定义一个 DTO 而不是直接序列化 <see cref="Incident"/>：
///   · 字段是**契约**，必须显式、稳定、可离线阅读（反射序列化会随重构悄悄改字段名）；
///   · 证据引用要落成 `sessionId#sequence` 形式，便于离线包直接引用；
///   · 不落任何给人看的富文本之外的东西 —— 卡片正文本身是可读的（中文摘要 + 结构化事实）。
/// </summary>
public sealed record IncidentDto
{
    public string SchemaVersion { get; init; } = "1.0";
    public required string IncidentId { get; init; }
    public required string RuleId { get; init; }
    public required int RuleVersion { get; init; }
    public required string Status { get; init; }
    public required string Severity { get; init; }
    public required string SymptomCode { get; init; }
    public required string Confidence { get; init; }
    public required string ConfidenceRationale { get; init; }
    public required bool EvidenceIncomplete { get; init; }
    public required int Revision { get; init; }
    public required string FirstSeenUtc { get; init; }
    public required string LastSeenUtc { get; init; }
    public string? BreakPoint { get; init; }
    public string? JobId { get; init; }
    public string? ObjectId { get; init; }
    public string? ActionId { get; init; }
    public string? OperationId { get; init; }
    public long? RunGeneration { get; init; }
    public long? LossEpoch { get; init; }
    public required string UserSummary { get; init; }
    public required string TechnicalSummary { get; init; }
    public required IReadOnlyList<IncidentFactDto> Facts { get; init; }
    public required IReadOnlyList<IncidentCandidateDto> Candidates { get; init; }
    public required IReadOnlyList<IncidentMissingDto> Missing { get; init; }
    public required IReadOnlyList<string> SuggestedChecks { get; init; }
}

public sealed record IncidentFactDto(string Evidence, string EventCode, string Role);

public sealed record IncidentCandidateDto(string Code, string Rationale, string Confidence, bool Refuted, string? RefutationReason);

public sealed record IncidentMissingDto(string ContractId, string ExpectedEventCode, string ReasonCode, long CoverageWatermark, bool CollectionHealthy);

/// <summary>事件卡 → JSON 行（供 incidents.jsonl 与离线包共用）。</summary>
public static class IncidentSerialization
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IncidentDto ToDto(Incident incident) => new()
    {
        IncidentId = incident.IncidentId,
        RuleId = incident.RuleId,
        RuleVersion = incident.RuleVersion,
        Status = incident.Status.ToString(),
        Severity = incident.Severity.ToString(),
        SymptomCode = incident.SymptomCode,
        Confidence = incident.Confidence.ToString(),
        ConfidenceRationale = incident.ConfidenceRationale,
        EvidenceIncomplete = incident.EvidenceIncomplete,
        Revision = incident.Revision,
        FirstSeenUtc = incident.FirstSeenUtc.ToUniversalTime().ToString("O"),
        LastSeenUtc = incident.LastSeenUtc.ToUniversalTime().ToString("O"),
        BreakPoint = incident.BreakPoint,
        JobId = incident.JobId,
        ObjectId = incident.ObjectId,
        ActionId = incident.ActionId is null ? null : DiagnosticId.Format(incident.ActionId.Value),
        OperationId = incident.OperationId is null ? null : DiagnosticId.Format(incident.OperationId.Value),
        RunGeneration = incident.RunGeneration,
        LossEpoch = incident.LossEpoch,
        UserSummary = incident.UserFacingSummary,
        TechnicalSummary = incident.TechnicalSummary,
        Facts = incident.Facts.Select(f => new IncidentFactDto(f.Ref.ToString(), f.EventCode, f.Role)).ToArray(),
        Candidates = incident.Candidates
            .Select(c => new IncidentCandidateDto(c.CandidateCode, c.Rationale, c.Confidence.ToString(), c.Refuted, c.RefutationReason))
            .ToArray(),
        Missing = incident.Missing
            .Select(m => new IncidentMissingDto(m.ContractId, m.ExpectedEventCode, m.ReasonCode, m.CoverageWatermark, m.CollectionHealthy))
            .ToArray(),
        SuggestedChecks = incident.SuggestedChecks.ToArray(),
    };

    public static string ToJsonLine(Incident incident) => JsonSerializer.Serialize(ToDto(incident), Options);
}