using PCMig.Diagnostics.Abstractions;

namespace PCMig.Diagnostics.Analysis;

/// <summary>事件卡（Incident）状态。</summary>
public enum IncidentStatus
{
    /// <summary>仍在观察（可能有后续证据）。</summary>
    Open = 0,

    /// <summary>已被后续证据关闭（例如条件消失）。</summary>
    Resolved = 1,

    /// <summary>**证据不足**：不判定（宁可承认不知道，也不制造假根因）。</summary>
    Inconclusive = 2,

    /// <summary>被抑制（同类冲突已登记为已知偏差 / 冷却中）。</summary>
    Suppressed = 3,

    /// <summary>被后续证据纠正（先前的判断有误，保留修订历史）。</summary>
    Corrected = 4,
}

/// <summary>
/// 置信带。**不是伪概率**：它表达"这条结论建立在什么证据强度上"，
/// 例如 ConfirmedObservation = 有直接观测（某个 API 就这样返回了）。
/// </summary>
public enum ConfidenceBand
{
    Unknown = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    ConfirmedObservation = 4,
}

/// <summary>一条证据引用：**指向某一次具体事件**（SessionId + Sequence），而不是事件类型。</summary>
public readonly record struct IncidentEvidence(EventRef Ref, string EventCode, string Role)
{
    /// <summary>Role：fact（已观测事实）/ exclusion（排除项）/ context（背景）。</summary>
    public const string Fact = "fact";
    public const string Exclusion = "exclusion";
    public const string Context = "context";
}

/// <summary>候选原因：**支持与反证都要写清**，且不给伪概率。</summary>
public sealed record IncidentCandidate(
    string CandidateCode,
    string Rationale,
    ConfidenceBand Confidence,
    bool Refuted,
    string? RefutationReason = null);

/// <summary>缺失证据：说明"按契约该出现什么、为什么没等到、当时的覆盖水位到哪"。</summary>
public sealed record MissingEvidence(
    string ContractId,
    string ExpectedEventCode,
    string ReasonCode,
    long CoverageWatermark,
    bool CollectionHealthy);

/// <summary>
/// 事件卡：诊断系统的输出单元。字段刻意区分
/// Observed facts / Candidates / Missing evidence / Break point / Confidence / Next checks
/// —— 因为"事实"与"候选"混在一起正是"假根因"的来源。
/// </summary>
public sealed class Incident
{
    public Incident(
        string incidentId,
        string ruleId,
        int ruleVersion,
        string symptomCode,
        DiagnosticLevel severity,
        DateTimeOffset firstSeenUtc,
        DateTimeOffset lastSeenUtc,
        string? jobId = null,
        string? objectId = null,
        Guid? actionId = null,
        Guid? operationId = null,
        long? runGeneration = null)
    {
        IncidentId = incidentId;
        RuleId = ruleId;
        RuleVersion = ruleVersion;
        SymptomCode = symptomCode;
        Severity = severity;
        FirstSeenUtc = firstSeenUtc;
        LastSeenUtc = lastSeenUtc;
        JobId = jobId;
        ObjectId = objectId;
        ActionId = actionId;
        OperationId = operationId;
        RunGeneration = runGeneration;
    }

    public string IncidentId { get; }
    public string RuleId { get; }
    public int RuleVersion { get; }
    public string SymptomCode { get; }
    public DiagnosticLevel Severity { get; private set; }
    public DateTimeOffset FirstSeenUtc { get; }

    public int Revision { get; private set; } = 1;
    public IncidentStatus Status { get; private set; } = IncidentStatus.Open;
    public DateTimeOffset LastSeenUtc { get; private set; }
    public string? BreakPoint { get; private set; }
    public ConfidenceBand Confidence { get; private set; } = ConfidenceBand.Unknown;
    public string ConfidenceRationale { get; private set; } = string.Empty;
    public string UserFacingSummary { get; private set; } = string.Empty;
    public string TechnicalSummary { get; private set; } = string.Empty;
    public bool EvidenceIncomplete { get; private set; }

    public string? JobId { get; }
    public string? ObjectId { get; }
    public Guid? ActionId { get; }
    public Guid? OperationId { get; }
    public long? RunGeneration { get; }
    public long? LossEpoch { get; private set; }

    public List<IncidentEvidence> Facts { get; } = new();
    public List<IncidentCandidate> Candidates { get; } = new();
    public List<MissingEvidence> Missing { get; } = new();
    public List<string> SuggestedChecks { get; } = new();
    public List<EventRef> FlightWindowRefs { get; } = new();

    /// <summary>把新证据并进同一张卡（迟到事件是"修订"，不是"再来一张卡"）。</summary>
    public void Merge(IncidentUpdate update)
    {
        var changed = false;

        if (update.Fact is { } fact && !Facts.Any(f => f.Ref.Equals(fact.Ref)))
        {
            Facts.Add(fact);
            changed = true;
        }

        foreach (var candidate in update.Candidates)
        {
            var index = Candidates.FindIndex(c => c.CandidateCode == candidate.CandidateCode);
            if (index >= 0)
            {
                if (Candidates[index] != candidate) { Candidates[index] = candidate; changed = true; }
            }
            else
            {
                Candidates.Add(candidate);
                changed = true;
            }
        }

        foreach (var missing in update.Missing)
        {
            var index = Missing.FindIndex(m => m.ContractId == missing.ContractId);
            if (index >= 0)
            {
                if (Missing[index] != missing) { Missing[index] = missing; changed = true; }
            }
            else
            {
                Missing.Add(missing);
                changed = true;
            }
        }

        foreach (var check in update.SuggestedChecks)
        {
            if (!SuggestedChecks.Contains(check)) { SuggestedChecks.Add(check); changed = true; }
        }

        if (update.Severity is { } severity) { Severity = severity; changed = true; }
        if (update.BreakPoint is not null && BreakPoint != update.BreakPoint) { BreakPoint = update.BreakPoint; changed = true; }
        if (update.Confidence is { } confidence && Confidence != confidence) { Confidence = confidence; changed = true; }
        if (!string.IsNullOrEmpty(update.ConfidenceRationale)) { ConfidenceRationale = update.ConfidenceRationale!; changed = true; }
        if (!string.IsNullOrEmpty(update.UserFacingSummary)) { UserFacingSummary = update.UserFacingSummary!; changed = true; }
        if (!string.IsNullOrEmpty(update.TechnicalSummary)) { TechnicalSummary = update.TechnicalSummary!; changed = true; }
        if (update.EvidenceIncomplete is { } incomplete && EvidenceIncomplete != incomplete) { EvidenceIncomplete = incomplete; changed = true; }
        if (update.LossEpoch is { } epoch) { LossEpoch = epoch; }
        if (update.Status is { } status && Status != status) { Status = status; changed = true; }
        if (update.AtUtc > LastSeenUtc) LastSeenUtc = update.AtUtc;

        if (changed) Revision++;
    }
}

/// <summary>规则对某张卡的一次更新意图（引擎负责合并与状态迁移）。</summary>
public sealed class IncidentUpdate
{
    public required DateTimeOffset AtUtc { get; init; }
    public IncidentEvidence? Fact { get; init; }
    public IReadOnlyList<IncidentCandidate> Candidates { get; init; } = Array.Empty<IncidentCandidate>();
    public IReadOnlyList<MissingEvidence> Missing { get; init; } = Array.Empty<MissingEvidence>();
    public IReadOnlyList<string> SuggestedChecks { get; init; } = Array.Empty<string>();
    public DiagnosticLevel? Severity { get; init; }
    public string? BreakPoint { get; init; }
    public ConfidenceBand? Confidence { get; init; }
    public string? ConfidenceRationale { get; init; }
    public string? UserFacingSummary { get; init; }
    public string? TechnicalSummary { get; init; }
    public bool? EvidenceIncomplete { get; init; }
    public long? LossEpoch { get; init; }
    public IncidentStatus? Status { get; init; }
}

/// <summary>规则求值结果：新建/更新一张卡，或者什么都不做（null）。</summary>
public sealed record RuleOutcome(Incident Incident, bool IsNew, bool ShouldTriggerFlight);