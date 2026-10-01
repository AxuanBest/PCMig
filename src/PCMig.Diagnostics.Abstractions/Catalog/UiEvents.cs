namespace PCMig.Diagnostics.Abstractions.Events;

/// <summary>
/// UI 族（2000 段）：用户语义动作 + 反馈契约 + 投影观测。
/// 这是"点击没反应发生在哪一步"的因果链骨架（架构方案 §9/§10）。
/// </summary>
public static class UiEvents
{
    /// <summary>用户语义动作被观察到（Click/键盘/测试意图）——**不是**所有鼠标移动。</summary>
    public static readonly EventDescriptor UserActionObserved = EventDescriptor.Define(
        DiagnosticCategory.Ui, 1, "UI.UserActionObserved",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public, "UiAction");

    /// <summary>原有 guard/CanExecute 判断结果（只观察已执行的结果，不为诊断多调用一次判据）。</summary>
    public static readonly EventDescriptor CommandEligibilityEvaluated = EventDescriptor.Define(
        DiagnosticCategory.Ui, 2, "UI.CommandEligibilityEvaluated",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public, "UiEligibility");

    /// <summary>动作进入处理路径（handler/VM 方法真正开始执行）。</summary>
    public static readonly EventDescriptor CommandStarted = EventDescriptor.Define(
        DiagnosticCategory.Ui, 3, "UI.CommandStarted",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public, "UiAction");

    /// <summary>该动作按契约**应当**产生的反馈（期望登记，不是事实）。</summary>
    public static readonly EventDescriptor FeedbackExpected = EventDescriptor.Define(
        DiagnosticCategory.Ui, 4, "UI.FeedbackExpected",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public, "UiFeedback");

    /// <summary>VM 状态已被写入（投影来源变化）。</summary>
    public static readonly EventDescriptor ProjectionChanged = EventDescriptor.Define(
        DiagnosticCategory.Ui, 5, "UI.ProjectionChanged",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public, "UiProjectionChanged");

    /// <summary>反馈已确认（契约闭环：期望在期限内被观察到）。</summary>
    public static readonly EventDescriptor FeedbackConfirmed = EventDescriptor.Define(
        DiagnosticCategory.Ui, 6, "UI.FeedbackConfirmed",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public, "UiFeedback");

    /// <summary>动作真正结束（以实际结果分支为准，不能用"Task 返回了"冒充成功）。</summary>
    public static readonly EventDescriptor ActionCompleted = EventDescriptor.Define(
        DiagnosticCategory.Ui, 7, "UI.ActionCompleted",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public, "UiAction");

    /// <summary>动作被合法拒绝（guard 拦住），带稳定 reasonCode。</summary>
    public static readonly EventDescriptor ActionRejected = EventDescriptor.Define(
        DiagnosticCategory.Ui, 8, "UI.ActionRejected",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Public, "UiEligibility");

    /// <summary>动作抛异常/失败（异常文本可能含路径 ⇒ 视为 Personal）。</summary>
    public static readonly EventDescriptor ActionFaulted = EventDescriptor.Define(
        DiagnosticCategory.Ui, 9, "UI.ActionFaulted",
        DiagnosticLevel.Error, DeliveryClass.Operational, PrivacyClassification.Personal);

    /// <summary>DispatcherQueue.TryEnqueue 被拒绝（关窗期正常，其他时候是风险）。</summary>
    public static readonly EventDescriptor DispatchRejected = EventDescriptor.Define(
        DiagnosticCategory.Ui, 10, "UI.DispatchRejected",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Public, "UiDispatchRejected");

    /// <summary>UI 线程响应性哨兵（发出/回收），最多一个在途。</summary>
    public static readonly EventDescriptor ThreadResponsivenessProbe = EventDescriptor.Define(
        DiagnosticCategory.Ui, 11, "UI.ThreadResponsivenessProbe",
        DiagnosticLevel.Debug, DeliveryClass.Verbose, PrivacyClassification.Public);

    /// <summary>某个状态归属（Core/VM/View/StoredState）的快照观测。</summary>
    public static readonly EventDescriptor StateObserved = EventDescriptor.Define(
        DiagnosticCategory.Ui, 12, "UI.StateObserved",
        DiagnosticLevel.Debug, DeliveryClass.Operational, PrivacyClassification.Public, "UiStateObserved");

    /// <summary>控件投影只读回读（L2：在真实 PushFooter/PushState 之后取值）。</summary>
    public static readonly EventDescriptor ProjectionReadback = EventDescriptor.Define(
        DiagnosticCategory.Ui, 13, "UI.ProjectionReadback",
        // ★ D6.1 §13 ★ 投递类从 Verbose 改为 **Operational**：
        //   L2 回读是"UI 是否真的显示成 VM 说的那样"的核心证据；留在 Verbose 等于
        //   只有开 Deep 才可能发现 UI 不一致 —— 那正是审计指出的问题。
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public, "UiProjectionReadback");

    /// <summary>Deep Trace 下的有限输入观测（不含文本/IME 内容）。</summary>
    public static readonly EventDescriptor InputObserved = EventDescriptor.Define(
        DiagnosticCategory.Ui, 14, "UI.InputObserved",
        DiagnosticLevel.Debug, DeliveryClass.Verbose, PrivacyClassification.Public, "UiInputObserved");

    /// <summary>测试意图（自动化测试声明的"我打算点这个"）——判定"输入未送达"的独立前提。</summary>
    public static readonly EventDescriptor TestIntentObserved = EventDescriptor.Define(
        DiagnosticCategory.Ui, 15, "UI.TestIntentObserved",
        DiagnosticLevel.Information, DeliveryClass.Verbose, PrivacyClassification.Public, "UiAction");

    /// <summary>页面/步骤导航变化（语义事实：from/to/reasonCode[/actionKind][/operationId]，不含帧与视觉状态）。</summary>
    public static readonly EventDescriptor NavigationChanged = EventDescriptor.Define(
        DiagnosticCategory.Ui, 16, "UI.NavigationChanged",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public, "UiNavigation");

    internal static readonly EventDescriptor[] All =
    {
        UserActionObserved, CommandEligibilityEvaluated, CommandStarted, FeedbackExpected,
        ProjectionChanged, FeedbackConfirmed, ActionCompleted, ActionRejected, ActionFaulted,
        DispatchRejected, ThreadResponsivenessProbe, StateObserved, ProjectionReadback,
        InputObserved, TestIntentObserved, NavigationChanged,
    };
}