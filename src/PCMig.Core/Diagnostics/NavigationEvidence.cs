using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;

namespace PCMig.Core.Diagnostics;

/// <summary>
/// 导航原因码（**封闭集合**，稳定 token，不是显示文字）。
///
/// 为什么要有封闭集合：诊断事件里最不能容忍的是"自由文本冒充事实"——
/// 一旦允许随手写原因，事后取证时"为什么跳到这一页"就变成不可判定。
/// 因此与本项目的停止原因（`DeepTraceStopReasons`）同款口径：
/// 不知道就写 <see cref="Unspecified"/>，**绝不编造**一个看起来合理的原因。
/// </summary>
public static class NavigationReasons
{
    /// <summary>首屏归属：应用启动时"我现在在 Step1"，没有前一页（from = (none)）。</summary>
    public const string Initial = "initial";

    /// <summary>用户点击左侧步骤卡（真实鼠标/触摸点击）。</summary>
    public const string RailClick = "rail-click";

    /// <summary>Ctrl+1..4 直达某一步。</summary>
    public const string KeyboardStep = "keyboard-step";

    /// <summary>Ctrl+Tab 向下一步（键盘）。</summary>
    public const string KeyboardNext = "keyboard-next";

    /// <summary>Ctrl+Shift+Tab 向上一步（键盘）。</summary>
    public const string KeyboardPrev = "keyboard-prev";

    /// <summary>右下角 Footer 的「下一步」按钮。</summary>
    public const string FooterNext = "footer-next";

    /// <summary>右下角 Footer 的「上一步」按钮。</summary>
    public const string FooterPrev = "footer-prev";

    /// <summary>环境变量驱动的 QA 探针自行切页（`PCMIG_STEP2_TREE_QA`；生产路径不设置该变量）。</summary>
    public const string QaProbe = "qa-probe";

    /// <summary>触发来源未知（例如直接给 <c>Current</c> 赋值）。诚实地说"不知道"，而不是猜一个。</summary>
    public const string Unspecified = "unspecified";

    /// <summary>全部合法原因码（供契约测试与文档使用）。</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Initial, RailClick, KeyboardStep, KeyboardNext, KeyboardPrev,
        FooterNext, FooterPrev, QaProbe, Unspecified,
    };

    public static bool IsKnown(string? reasonCode) =>
        !string.IsNullOrWhiteSpace(reasonCode) && All.Contains(reasonCode);

    /// <summary>未知/空原因一律降级为 <see cref="Unspecified"/>（绝不把自由文本带进事件）。</summary>
    public static string Normalize(string? reasonCode) =>
        IsKnown(reasonCode) ? reasonCode! : Unspecified;
}

/// <summary>
/// 导航证据的唯一发布点。
///
/// 纪律（与 D6.3 其余收口一致）：
///   · **只在导航真的发生之后**发布（调用方必须在 `Current` 已切换成功后调用）——
///     不以"方法返回了"冒充"页面切过去了"；
///   · <see cref="NoPreviousStep"/> 表示"之前不在任何一页"（首屏），不是编出来的第 0 步；
///   · from == to 不发布（事件名是 NavigationChanged，没有变化就没有这条事实）；
///   · operationId 只在**确实有**业务关联 ID 时传入，没有就省略该属性。
///
/// 为什么 outcome 用 <see cref="DiagnosticOutcome.Succeeded"/>：
/// 这里的 Succeeded 表示"当前步**确实已经变了**"这一**观察事实**（与 UI 族既有的
/// `UI.ProjectionChanged` / `UI.StateObserved` / `UI.ProjectionReadback` 同款口径），
/// 它既不来自业务动作、也不来自方法返回值——导航不是业务动作，
/// 业务是否成功仍由 `UI.ActionCompleted`（WP E 的 ActionOutcomePolicy）负责。
/// </summary>
public static class NavigationEvidence
{
    /// <summary>首屏：之前不在任何一页。</summary>
    public const string NoPreviousStep = "(none)";

    /// <summary>步骤 token 无法识别时的占位（诚实标记，不猜是哪一页）。</summary>
    public const string UnknownStep = "(unknown)";

    /// <summary>因果链上的动作类别（与 `Diagnostic.ControlIds.ActionKinds.Navigate` 同一 token）。</summary>
    public const string DefaultActionKind = "Navigate";

    /// <summary>发布一次导航事实。返回 true 表示**这条事实已经交给诊断管道**（false = 无可发布内容或未采集）。</summary>
    public static bool Publish(
        string from,
        string to,
        string reasonCode,
        string? operationId = null,
        string component = "Shell")
    {
        // 没有目标页 ⇒ 没有"导航变化"这条事实。
        if (string.IsNullOrWhiteSpace(to)) return false;

        var normalizedFrom = string.IsNullOrWhiteSpace(from) ? NoPreviousStep : from;
        // 同一页不算变化：不制造假事件。
        if (string.Equals(normalizedFrom, to, StringComparison.Ordinal)) return false;

        // 未采集：不构造载荷（与 Core 其余观察点同款热路径口径）。
        if (!CoreDiagnostics.Enabled) return false;

        CoreDiagnostics.PublishUiAction(
            UiEvents.NavigationChanged,
            new UiNavigationPayload(
                normalizedFrom,
                to,
                NavigationReasons.Normalize(reasonCode),
                DefaultActionKind,
                string.IsNullOrWhiteSpace(operationId) ? null : operationId),
            DiagnosticOutcome.Succeeded,
            DiagnosticLevel.Information,
            component);
        return true;
    }
}