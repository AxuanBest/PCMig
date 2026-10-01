using PCMig.Diagnostics.Abstractions;

namespace PCMig.Diagnostics.Analysis;

/// <summary>规则求值上下文：只读，且**必须**带上证据覆盖（按投递类/分支分档）。</summary>
public sealed class RuleContext
{
    public required long LossEpoch { get; init; }
    public required bool EvidenceComplete { get; init; }
    public required long AcceptanceWatermark { get; init; }

    /// <summary>★ D6.1 §4 ★ 分档证据覆盖：规则可用它做更精细的判断（例如反馈规则额外要求 analyzer 分支完整）。</summary>
    public required EvidenceCoverage Coverage { get; init; }

    /// <summary>本会话已接受事件的水位（缺事件规则的"水位是否越过"判据）。</summary>
    public required Func<EventRef, DiagnosticEvent?> ResolveEvidence { get; init; }
}

/// <summary>
/// 一条确定性规则。纪律：
///   · 只读事件与上下文，**不执行业务命令**（绝不修状态、绝不重试、绝不改 CanResume）；
///   · 判据只用 EventId + typed payload；中文文案只能出现在给人看的摘要里；
///   · 规则必须能说出"我缺什么证据"（<see cref="IncidentUpdate.Missing"/>），
///     以及"我的结论建立在什么强度上"（<see cref="IncidentUpdate.Confidence"/>）；
///   · <see cref="RequiresCompleteEvidence"/> = true 的规则在采集有丢失时**不得**下确定结论。
/// </summary>
public interface IDiagnosticRule
{
    string RuleId { get; }

    int Version { get; }

    /// <summary>该规则是否属于"缺事件/负向判断"（采集有损时必须降级为 Inconclusive）。</summary>
    bool RequiresCompleteEvidence { get; }

    /// <summary>
    /// ★ D6.1 §4 ★ 本规则**依赖哪一档投递类**的证据。默认 Operational：
    /// 只有 Operational 及以上（含 DurableCritical）的丢失才会让本规则降级；
    /// 纯粹丢 Verbose（调试细节）不影响它。
    /// 依赖 Verbose 细节的规则应显式覆盖为 <see cref="DeliveryClass.Verbose"/>。
    /// </summary>
    DeliveryClass RequiredDeliveryClass => DeliveryClass.Operational;

    /// <summary>
    /// 可选：本规则是否额外依赖**某个分支**不丢东西（例如反馈规则依赖 analyzer 收件箱）。
    /// null/空 = 不额外要求。
    /// </summary>
    string? RequiredBranch => null;

    /// <summary>该规则关心的**稳定事件名**（例如 PST.WriteFailed —— 注意不是 "PST-004" 这种代码）。
    /// 分派按名称进行：名称与代码 1:1，但名称可读且不会与数值段位混淆。</summary>
    IReadOnlyCollection<string> WatchedEventCodes { get; }

    /// <summary>求值。返回 null = 这条事件与它无关（或不需要变更）。</summary>
    RuleOutcome? Evaluate(in DiagnosticEvent evt, RuleContext context);
}

/// <summary>规则注册表：**只增不改**的显式清单（不反射、不扫描程序集）。</summary>
public sealed class RuleRegistry
{
    private readonly Dictionary<string, IDiagnosticRule> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<IDiagnosticRule>> _byCode = new(StringComparer.Ordinal);

    public RuleRegistry(IEnumerable<IDiagnosticRule> rules)
    {
        foreach (var rule in rules)
        {
            if (_byId.ContainsKey(rule.RuleId))
                throw new InvalidOperationException("规则 ID 重复：" + rule.RuleId);
            _byId[rule.RuleId] = rule;

            foreach (var code in rule.WatchedEventCodes)
            {
                if (!_byCode.TryGetValue(code, out var list))
                {
                    list = new List<IDiagnosticRule>();
                    _byCode[code] = list;
                }
                list.Add(rule);
            }
        }
    }

    public IReadOnlyCollection<IDiagnosticRule> All => _byId.Values;

    public int Count => _byId.Count;

    /// <summary>按**事件名**分派（没有规则关心就返回空列表，调用点零分配地跳过）。</summary>
    public IReadOnlyList<IDiagnosticRule> ForEventName(string eventName) =>
        _byCode.TryGetValue(eventName, out var list) ? list : Array.Empty<IDiagnosticRule>();

    /// <summary>
    /// 按**事件代码**（PST-004）分派：内部先解析成事件名再查表。
    /// 保留这个重载是为了让调用方不必关心代码/名称的区别，同时避免"按代码查名表"这类错配。
    /// </summary>
    public IReadOnlyList<IDiagnosticRule> ForEventDescriptor(EventDescriptor descriptor) =>
        ForEventName(descriptor.Name);

    public static RuleRegistry CreateDefault() => new(new IDiagnosticRule[]
    {
        new Rules.PersistenceWriteFailureRule(),
        new Rules.DiagnosticsDegradedRule(),
        new Rules.AccessDeniedRule(),
        new Rules.TargetSpaceExhaustedRule(),
        new Rules.RobocopyUnexpectedExitRule(),
        new Rules.FileLockedRule(),
        new Rules.PreviousSessionUncleanRule(),
    });
}