namespace PCMig.Diagnostics.Abstractions;

/// <summary>
/// 一次控件投影回读的判定结果（D6.1 §13）。
///   · <see cref="PageMounted"/> = false ⇒ **页面没挂载**，结论是"未观察到"，**不是** mismatch；
///   · <see cref="Observed"/> = false ⇒ 本次没有可用的真实读数；
///   · <see cref="Matches"/> 只在真的读到控件状态时才有意义；
///   · <see cref="ReasonCode"/> 是稳定 token（不给人看的文案）。
/// </summary>
public readonly record struct ProjectionReadbackDecision(
    bool PageMounted,
    bool Observed,
    bool Matches,
    string ReasonCode);

/// <summary>
/// L2 投影回读的**纯判定策略**（D6.1 §13）。
///
/// 为什么放在契约层：这是"UI 是否真的显示成 VM 说的那样"的判据，
/// 必须能被**行为测试**直接验证（含"把真实控件状态改掉 ⇒ 必须检出 mismatch"），
/// 而不是靠源码字符串或"自己告诉自己 matchesSource = true"。
///
/// 纪律：
///   · 只有**真实读数**参与比较；没读到就说没读到；
///   · 页面未挂载 ⇒ PageUnavailable/NotObserved，绝不当成 mismatch（否则会误报投影陈旧）；
///   · 期望值来自**投影承诺**（VM/契约），实际值来自**控件本身**——两者不同才是 mismatch。
/// </summary>
public static class ProjectionReadbackPolicy
{
    public const string ReasonMatch = "match";
    public const string ReasonPageUnavailable = "page-unavailable";
    public const string ReasonEnabledMismatch = "enabled-mismatch";
    public const string ReasonVisibilityMismatch = "visibility-mismatch";

    public static ProjectionReadbackDecision Evaluate(
        bool pageMounted,
        bool actualEnabled,
        string actualVisibility,
        bool? expectedEnabled = null,
        string? expectedVisibility = null)
    {
        if (!pageMounted)
            return new ProjectionReadbackDecision(false, false, false, ReasonPageUnavailable);

        if (expectedEnabled is { } wantEnabled && wantEnabled != actualEnabled)
            return new ProjectionReadbackDecision(true, true, false, ReasonEnabledMismatch);

        if (expectedVisibility is { } wantVisibility
            && !string.Equals(wantVisibility, actualVisibility, StringComparison.Ordinal))
            return new ProjectionReadbackDecision(true, true, false, ReasonVisibilityMismatch);

        return new ProjectionReadbackDecision(true, true, true, ReasonMatch);
    }
}