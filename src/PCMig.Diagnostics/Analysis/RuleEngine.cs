using System.Collections.Concurrent;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;

namespace PCMig.Diagnostics.Analysis;

/// <summary>引擎状态快照（自身健康的一部分：分析器也会滞后/抑制/丢卡）。</summary>
public readonly record struct RuleEngineStats(
    long EventsEvaluated,
    long IncidentsOpened,
    long IncidentsUpdated,
    long IncidentsResolved,
    long IncidentsInconclusive,
    long SuppressedByCooldown,
    long SuppressedByCap,
    int ActiveIncidents,
    long RuleFaults)
{
    /// <summary>★ D6.1 §5 ★ 因 eventVersion 不受支持而**整条跳过**的事件数（可保存但不可解释）。</summary>
    public long UnsupportedVersionSkipped { get; init; }
}

/// <summary>
/// 确定性规则引擎（方案 §13/§14）。
///
/// 它是 analyzer 收件箱的消费者（由 <c>DiagnosticRuntime.SetAnalyzerSink</c> 接入），
/// 因此 **writer 挂掉不影响它、它挂掉不影响 writer**。
///
/// 纪律：
///   · 纯事件状态归约：只读事件与上下文，不碰业务对象、不执行命令、不重连、不改 CanResume；
///   · **有界**：活动卡数量上限 + 冷却抑制，绝不因风暴无限增长；
///   · **证据不足就说不知道**：采集有损时，依赖"没观察到"的规则降级为 Inconclusive；
///   · 迟到事件走同一张卡的**修订**（Revision++），而不是再开一张互相矛盾的卡；
///   · 任何规则抛异常都被隔离成 RuleFaults，不影响其它规则与 writer。
/// </summary>
public sealed class RuleEngine
{
    private const int MaxActiveIncidents = 256;
    private const int MaxFactsPerIncident = 32;
    private const int MaxEvidenceRefsResolved = 512;

    private readonly RuleRegistry _registry;
    private readonly ConcurrentDictionary<string, Incident> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _cooldownUntilUnixMs = new(StringComparer.Ordinal);
    private readonly Func<EventRef, DiagnosticEvent?> _evidenceResolver;

    private long _evaluated;
    private long _opened;
    private long _updated;
    private long _resolved;
    private long _inconclusive;
    private long _suppressedCooldown;
    private long _suppressedCap;
    private long _ruleFaults;
    private long _unsupportedVersionSkipped;

    /// <summary>冷却窗口：同一张卡在该窗口内只更新一次（防事件风暴把 UI/磁盘打满）。</summary>
    public int CooldownMs { get; init; } = 1_000;

    /// <summary>事件产生时回调（打开/更新/结案），由 runtime 用来发 DIA.IncidentOpened/Resolved 与触发 Flight。</summary>
    public Action<Incident, IncidentLifecycle>? OnIncident { get; set; }

    public RuleEngine(RuleRegistry registry, Func<EventRef, DiagnosticEvent?> evidenceResolver)
    {
        _registry = registry;
        _evidenceResolver = evidenceResolver;
    }

    public enum IncidentLifecycle
    {
        Opened = 0,
        Updated = 1,
        Resolved = 2,
        Inconclusive = 3,
    }

    public IReadOnlyCollection<Incident> ActiveIncidents => _active.Values.ToArray();

    public int ActiveCount => _active.Count;

    /// <summary>analyzer 收件箱的消费者。**绝不抛**（异常隔离在内部）。</summary>
    public ValueTask ConsumeAsync(DiagnosticEvent evt, RuleContext context)
    {
        try
        {
            Evaluate(in evt, context);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _ruleFaults);
        }

        return default;
    }

    /// <summary>求值入口（同步、可测；不依赖任何计时器或线程）。</summary>
    public void Evaluate(in DiagnosticEvent evt, RuleContext context)
    {
        // 分派按**事件名**（不是 "PST-004" 这种代码）：注册表与规则声明的是同一个字段，
    // 早期版本按代码查名称表 ⇒ 永远查不到，规则静默不生效（已被 D4 用例拦下）。
    var rules = _registry.ForEventDescriptor(evt.Descriptor);
        if (rules.Count == 0) return;

        // ★ D6.1 §5 ★ 版本不受支持的事件：**可以保存，但不能用当前契约解释**。
        // 因此不执行任何依赖其 payload 的规则（保守做法：整条跳过并计数，便于离线解释"为什么没出卡片"）。
        if (evt.VersionUnsupported)
        {
            Interlocked.Increment(ref _unsupportedVersionSkipped);
            return;
        }

        Interlocked.Increment(ref _evaluated);

        foreach (var rule in rules)
        {
            // ★ D6.1 §4 ★ 每条规则按**自己依赖的**证据范围判完整性：
            //   投递类（默认 Operational）× 分支（可选）。一次 Verbose 丢弃不会再让
            //   依赖 Operational 证据的规则全部降级；而 analyzer 收件箱丢过东西时，
            //   依赖它的反馈规则会如实降级。
            var ruleContext = ContextFor(rule, context);

            RuleOutcome? outcome;
            try
            {
                outcome = rule.Evaluate(in evt, ruleContext);
            }
            catch (Exception)
            {
                // 单规则失败必须被隔离（不能连累其它规则，更不能影响 writer）。
                Interlocked.Increment(ref _ruleFaults);
                continue;
            }

            if (outcome is null) continue;
            Apply(rule, outcome, evt, ruleContext);
        }
    }

    /// <summary>
    /// 按规则声明的依赖构造上下文。同档同分支时复用调用方传进来的那个（零额外分配），
    /// 只有声明了更高档位/特定分支的规则才会拿到一个新的上下文对象。
    /// </summary>
    private RuleContext ContextFor(IDiagnosticRule rule, RuleContext shared)
    {
        // ★ D6.3 §7 ★ 覆盖完整 **且** 诊断自身健康 ⇒ 才敢说"证据完整"。
        var complete = shared.Coverage.IsCompleteForRule(rule) && shared.HealthEvidenceIntact;
        var epoch = shared.Coverage.EpochFor(rule.RequiredDeliveryClass);

        if (complete == shared.EvidenceComplete && epoch == shared.LossEpoch) return shared;

        return new RuleContext
        {
            LossEpoch = epoch,
            EvidenceComplete = complete,
            AcceptanceWatermark = shared.AcceptanceWatermark,
            Coverage = shared.Coverage,
            ResolveEvidence = shared.ResolveEvidence,
            HealthEvidenceIntact = shared.HealthEvidenceIntact,
        };
    }

    private void Apply(IDiagnosticRule rule, RuleOutcome outcome, in DiagnosticEvent evt, RuleContext context)
    {
        var incoming = outcome.Incident;

        // ★ 代际隔离 ★ 事件卡身份统一带上 RunGeneration：
        //   同一次运行的第 N 次 Run 各自成卡 ⇒ Run A 的迟到事件只会修订 A 的卡，
        //   绝不会把 B 的卡结案或污染（方案 §14 的反例之一）。
        var generationKey = incoming.IncidentId + "|g" + (evt.RunGeneration?.ToString() ?? "-");
        var scoped = new Incident(
            generationKey, incoming.RuleId, incoming.RuleVersion, incoming.SymptomCode, incoming.Severity,
            incoming.FirstSeenUtc, incoming.LastSeenUtc,
            incoming.JobId, incoming.ObjectId, incoming.ActionId, incoming.OperationId, evt.RunGeneration);
        // 把规则给出的内容整体搬进"代际作用域"的那张卡（字段口径不变，只换身份）。
        scoped.Merge(new IncidentUpdate
        {
            AtUtc = incoming.LastSeenUtc,
            Fact = incoming.Facts.FirstOrDefault(),
            Candidates = incoming.Candidates.ToArray(),
            Missing = incoming.Missing.ToArray(),
            SuggestedChecks = incoming.SuggestedChecks.ToArray(),
            Severity = incoming.Severity,
            BreakPoint = incoming.BreakPoint,
            Confidence = incoming.Confidence,
            ConfidenceRationale = incoming.ConfidenceRationale,
            UserFacingSummary = incoming.UserFacingSummary,
            TechnicalSummary = incoming.TechnicalSummary,
            EvidenceIncomplete = incoming.EvidenceIncomplete,
            LossEpoch = incoming.LossEpoch,
            Status = incoming.Status,
        });
        incoming = scoped;

        // ★ 证据不足时的诚实降级 ★
        //   依赖"没观察到"的规则在采集有损时不得下确定结论；而"关于降级本身"的规则（
        //   DIAGNOSTICS_DEGRADED）必须继续工作，所以它声明 RequiresCompleteEvidence=false。
        if (rule.RequiresCompleteEvidence && !context.EvidenceComplete)
        {
            incoming.Merge(new IncidentUpdate
            {
                AtUtc = evt.TimestampUtc,
                Status = IncidentStatus.Inconclusive,
                EvidenceIncomplete = true,
                LossEpoch = context.LossEpoch,
                Confidence = ConfidenceBand.Unknown,
                ConfidenceRationale = "采集不完整（lossEpoch=" + context.LossEpoch + "）：无法区分「没发生」与「没记到」",
                Missing = new[]
                {
                    new MissingEvidence(
                        ContractId: rule.RuleId,
                        ExpectedEventCode: string.Join(",", rule.WatchedEventCodes),
                        ReasonCode: "evidence-loss",
                        CoverageWatermark: context.AcceptanceWatermark,
                        CollectionHealthy: context.EvidenceComplete),
                },
            });
            Interlocked.Increment(ref _inconclusive);
        }

        Upsert(incoming, evt.TimestampUtc, outcome.ShouldTriggerFlight);
        _ = rule;
    }

    /// <summary>
    /// 把一张卡并入存储——**事件驱动规则与"非事件结论"（反馈超时）共用的唯一入口**：
    /// 冷却抑制、活动卡上限、修订合并、生命周期回调只在这里实现一次，避免两套口径分叉。
    /// </summary>
    private void Upsert(Incident incoming, DateTimeOffset atUtc, bool triggerFlight)
    {
        if (_active.TryGetValue(incoming.IncidentId, out var existing))
        {
            // 冷却：同一张卡在窗口内只合并一次（迟到/风暴都不再刷屏）。
            var nowMs = atUtc.ToUnixTimeMilliseconds();
            if (_cooldownUntilUnixMs.TryGetValue(incoming.IncidentId, out var until) && nowMs < until)
            {
                Interlocked.Increment(ref _suppressedCooldown);
                return;
            }
            _cooldownUntilUnixMs[incoming.IncidentId] = nowMs + CooldownMs;

            existing.Merge(new IncidentUpdate
            {
                AtUtc = atUtc,
                Fact = incoming.Facts.FirstOrDefault(),
                Candidates = incoming.Candidates,
                Missing = incoming.Missing,
                SuggestedChecks = incoming.SuggestedChecks,
                Severity = incoming.Severity,
                BreakPoint = incoming.BreakPoint,
                Confidence = incoming.Confidence,
                ConfidenceRationale = incoming.ConfidenceRationale,
                UserFacingSummary = incoming.UserFacingSummary,
                TechnicalSummary = incoming.TechnicalSummary,
                EvidenceIncomplete = incoming.EvidenceIncomplete,
                LossEpoch = incoming.LossEpoch,
                Status = incoming.Status == IncidentStatus.Inconclusive ? IncidentStatus.Inconclusive : null,
            });

            TrimEvidence(existing);
            Interlocked.Increment(ref _updated);
            OnIncident?.Invoke(existing, existing.Status == IncidentStatus.Inconclusive
                ? IncidentLifecycle.Inconclusive
                : IncidentLifecycle.Updated);
            return;
        }

        // 上限：宁可抑制新卡并计数，也不让活动卡无限增长。
        if (_active.Count >= MaxActiveIncidents)
        {
            Interlocked.Increment(ref _suppressedCap);
            return;
        }

        TrimEvidence(incoming);
        if (_active.TryAdd(incoming.IncidentId, incoming))
        {
            _cooldownUntilUnixMs[incoming.IncidentId] = atUtc.ToUnixTimeMilliseconds() + CooldownMs;
            Interlocked.Increment(ref _opened);
            OnIncident?.Invoke(incoming, incoming.Status == IncidentStatus.Inconclusive
                ? IncidentLifecycle.Inconclusive
                : IncidentLifecycle.Opened);
        }
    }

    /// <summary>
    /// 提交一条**非事件驱动**的结论（例如"期望的反馈在预算内没有出现"）。
    /// 走同一套存储与生命周期，因此冷却/上限/落盘/飞行窗口全部一致。
    /// </summary>
    public void ReportIncident(Incident incident, bool triggerFlight = false)
    {
        try
        {
            Upsert(incident, incident.LastSeenUtc, triggerFlight);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _ruleFaults);
        }
    }

    /// <summary>把一张卡标记为结案（用于"条件消失/被后续证据纠正"）。</summary>
    public bool Resolve(string incidentId, string reasonCode, DateTimeOffset atUtc)
    {
        if (!_active.TryGetValue(incidentId, out var incident)) return false;
        incident.Merge(new IncidentUpdate
        {
            AtUtc = atUtc,
            Status = IncidentStatus.Resolved,
            ConfidenceRationale = "条件消失或被后续证据纠正：" + reasonCode,
        });
        _active.TryRemove(incidentId, out _);
        _cooldownUntilUnixMs.TryRemove(incidentId, out _);
        Interlocked.Increment(ref _resolved);
        OnIncident?.Invoke(incident, IncidentLifecycle.Resolved);
        return true;
    }

    private static void TrimEvidence(Incident incident)
    {
        while (incident.Facts.Count > MaxFactsPerIncident) incident.Facts.RemoveAt(0);
        while (incident.Candidates.Count > 16) incident.Candidates.RemoveAt(incident.Candidates.Count - 1);
        while (incident.SuggestedChecks.Count > 8) incident.SuggestedChecks.RemoveAt(incident.SuggestedChecks.Count - 1);
    }

    /// <summary>
    /// 构造规则上下文。★ D6.1 §4 ★ `Coverage` 是分档证据覆盖；
    /// 这里的 `evidenceComplete`/`lossEpoch` 用**默认档（Operational）**，
    /// 各规则若声明了不同档位/分支，会由 <see cref="ContextFor"/> 单独计算。
    /// </summary>
    public RuleContext CreateContext(EvidenceCoverage coverage, long acceptanceWatermark, bool healthEvidenceIntact = true) => new()
    {
        LossEpoch = coverage.EpochFor(DeliveryClass.Operational),
        EvidenceComplete = coverage.IsCompleteFor(DeliveryClass.Operational) && healthEvidenceIntact,
        AcceptanceWatermark = acceptanceWatermark,
        Coverage = coverage,
        ResolveEvidence = reference => _evidenceResolver(reference),
        HealthEvidenceIntact = healthEvidenceIntact,
    };

    public RuleEngineStats Stats()
    {
        _ = MaxEvidenceRefsResolved;
        return new RuleEngineStats(
            Interlocked.Read(ref _evaluated),
            Interlocked.Read(ref _opened),
            Interlocked.Read(ref _updated),
            Interlocked.Read(ref _resolved),
            Interlocked.Read(ref _inconclusive),
            Interlocked.Read(ref _suppressedCooldown),
            Interlocked.Read(ref _suppressedCap),
            _active.Count,
            Interlocked.Read(ref _ruleFaults))
        {
            UnsupportedVersionSkipped = Interlocked.Read(ref _unsupportedVersionSkipped),
        };
    }
}