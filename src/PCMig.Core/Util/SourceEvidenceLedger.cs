namespace PCMig.Core.Util;

/// <summary>
/// ★ P0-05 ★ 源共享可达性**状态**（与 WinUI 的 <c>SourceConnectionState</c> 一一对应，
/// 放在 Core 里是为了让"证据过期 ⇒ 不再显示已连接"这条规则可以被单元测试直接推进时间验证）。
/// </summary>
public enum SourceEvidenceState
{
    /// <summary>没有任何可用证据（或已确认失效）⇒ 红灯。</summary>
    Disconnected,
    Connecting,
    /// <summary>**当前**有新鲜的可读探测证据 ⇒ 绿灯（"刚刚确认过读得到"，不是"曾连上过"）。</summary>
    Connected,
    /// <summary>至少一条新鲜可达 + 至少一条确认失效。</summary>
    Partial,
    /// <summary>只有过期证据 / 只有历史连接事实 ⇒ 琥珀"待确认"，绝不是绿。</summary>
    SuspectOrUnknown,
    Recovering,
}

/// <summary>
/// ★ P0-05 ★ 源共享可达性**证据账本**（PCMig 2026-10-13 定点修复）。
///
/// 【为什么需要它】旧实现只按"有没有可达事实"分档，**从不比对事实的时间**
/// （<c>ConnectionViewModel.UpdateSourceState</c> 只看 <c>_shareFacts.Values.Count(f =&gt; f.Reachable)</c>），
/// 于是共享被公司策略关掉之后，几分钟甚至几小时前的"可读"结论会让底栏一直亮绿灯
/// （真机实测：撤销共享 600 秒后仍显示"已连接（实测可读）"）。
///
/// 【本类表达的唯一事实】"**多久以前**确认过它读得到"。规则：
///   · 可达证据有**有效期**（默认 60 秒）——过期即不再算数，状态退回 <see cref="SourceEvidenceState.SuspectOrUnknown"/>（琥珀"待确认"）；
///   · 失效证据是"确认坏了"的结论，不设有效期（由后续成功的实测覆盖），因此它单独出现时是 <see cref="SourceEvidenceState.Disconnected"/>（红）；
///   · 想重新变绿，**只能靠一次新的实测**（<see cref="Record"/> 写入可达证据），历史连接事实永远不足以点绿；
///   · 时间由调用方传入（<c>nowUtc</c>），因此本类不依赖系统时钟、可被单测精确推进。
/// </summary>
public sealed class SourceEvidenceLedger
{
    /// <summary>可达证据的有效期默认值。60 秒 = 用户指定的隔离测试目标（"60 秒内不再错误显示已连接"）。</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);

    /// <summary>单条源目标的实测结论（★ 不是"上次连上过"，是"何时读到/读不到"★）。</summary>
    public sealed record Fact(string Target, bool Reachable, string Evidence, DateTime AtUtc);

    private readonly Dictionary<string, Fact> _facts =
        new(StringComparer.OrdinalIgnoreCase);

    public SourceEvidenceLedger(TimeSpan? ttl = null) => Ttl = ttl ?? DefaultTtl;

    /// <summary>可达证据的有效期。</summary>
    public TimeSpan Ttl { get; }

    public int Count => _facts.Count;

    public IReadOnlyList<Fact> All
        => _facts.Values.OrderBy(f => f.Target, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>记录一次实测结论（同一目标后来者覆盖先前结论）。`reachable=true` 才可能点绿。</summary>
    public void Record(string target, bool reachable, string evidence, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        var key = target.Trim();
        _facts[key] = new Fact(key, reachable, evidence ?? string.Empty, nowUtc);
    }

    public void Clear() => _facts.Clear();

    /// <summary>可达证据是否仍然新鲜。失效证据（<c>Reachable=false</c>）不设有效期。</summary>
    public bool IsFresh(Fact fact, DateTime nowUtc)
    {
        if (fact is null) return false;
        if (!fact.Reachable) return true;
        var age = nowUtc - fact.AtUtc;
        return age <= Ttl;
    }

    /// <summary>仍然**新鲜**的可达证据（= 配得上绿灯的那些目标）。</summary>
    public IReadOnlyList<Fact> FreshReachable(DateTime nowUtc)
        => _facts.Values.Where(f => f.Reachable && IsFresh(f, nowUtc))
            .OrderBy(f => f.Target, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>**已过期**的可达证据（曾经读得到，但已超出有效期 ⇒ 不得再当绿）。</summary>
    public IReadOnlyList<Fact> ExpiredReachable(DateTime nowUtc)
        => _facts.Values.Where(f => f.Reachable && !IsFresh(f, nowUtc))
            .OrderBy(f => f.Target, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>已确认失效的证据。</summary>
    public IReadOnlyList<Fact> Unreachable(DateTime nowUtc)
        => _facts.Values.Where(f => !f.Reachable)
            .OrderBy(f => f.Target, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// 需要重新探测的目标（**有界**：最多 <paramref name="maxTargets"/> 条）：
    /// 已过期的可达证据 + 已确认失效的证据。都按"最旧优先"返回，避免总是探同一条。
    /// </summary>
    public IReadOnlyList<Fact> TargetsNeedingProbe(DateTime nowUtc, int maxTargets)
    {
        if (maxTargets <= 0) return Array.Empty<Fact>();
        return _facts.Values
            .Where(f => !f.Reachable || !IsFresh(f, nowUtc))
            .OrderBy(f => f.AtUtc)
            .Take(maxTargets)
            .ToList();
    }

    /// <summary>
    /// 由证据**单点派生**状态。
    /// ★ 关键纪律：过期的可达证据**不算绿**——没有任何新鲜证据时只能是"待确认"（琥珀），
    /// 绝不因为"以前读到过"而继续显示已连接。
    /// </summary>
    public SourceEvidenceState Derive(DateTime nowUtc, bool connecting, bool recovering, bool historicalConnected)
    {
        if (connecting) return SourceEvidenceState.Connecting;
        if (recovering) return SourceEvidenceState.Recovering;

        var fresh = FreshReachable(nowUtc).Count;
        var expired = ExpiredReachable(nowUtc).Count;
        var dead = Unreachable(nowUtc).Count;

        if (fresh > 0 && dead > 0) return SourceEvidenceState.Partial;
        if (fresh > 0) return SourceEvidenceState.Connected;
        if (dead > 0) return SourceEvidenceState.Disconnected;
        if (expired > 0) return SourceEvidenceState.SuspectOrUnknown;
        return historicalConnected ? SourceEvidenceState.SuspectOrUnknown : SourceEvidenceState.Disconnected;
    }
}
