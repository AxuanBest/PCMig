using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PCMig.Core.Models;
using PCMig.WinUI.Presentation;
using PCMig.WinUI.Diagnostics;
using PCMig.Diagnostics.Abstractions;

namespace PCMig.WinUI.Views;

/// <summary>
/// Step 4 — 结果与校验（状态接线同 Step2/Step3：**代码直推 + PushState()**）。
///
/// 阶段 A 包 4（本文件）：
///   · 本页**已无任何示例行 / 假日志**：原先报告清单的 3 条示例行、日志面板的 4 条硬编码日志已全部删除；
///   · 报告清单 = <see cref="MigrationSessionViewModel.FailItems"/>（真实失败 / 不一致项，时间取该项**真实产生时刻**）；
///   · 实时日志 = <see cref="MigrationSessionViewModel.LogLines"/>（引擎真实输出行 + 本次会话真实动作行）；
///   · 顶部操作条六个按钮 + 日志卡的「打开日志目录」全部接真实状态：
///     验证 / 修复（含「强制覆盖」开关）/ 打开报告 / 打开目标文件夹 / 恢复任务 / 打开日志目录，
///     可用性由 VM 的真实布尔量决定，**不再是恒 False**；
///   · 验证前置闸门在**页面层先挡一次**（无任务 / plan.Objects.Count == 0 / 源根不可访问），
///     这是阶段 A 的 UI 防呆，**不替代**阶段 B 对 Verifier 的真正修复；
///   · 绑定形态沿用本项目已验收的唯一路线：页面级代码直推（页面级经典 {Binding} 与嵌套 {x:Bind}
///     在本项目 WinUI 页实测渲染为空），**仅 DataTemplate 内部**用经典 {Binding}（Step1/2/3 同款）。
///
/// 线程纪律：本文件所有控件写入都在 UI 线程；非 UI 线程调用 PushState 时改由 DispatcherQueue 排队，
/// 绝不直接碰控件（与 Step3 的硬防线同一做法）。
/// </summary>
public sealed partial class Step4ResultPage : UserControl
{
    private PageReadiness? _state;
    private MigrationSessionViewModel? _session;
    private StepNavigation? _nav;
    private Func<string?>? _passwordProvider;
    private bool _actionsHooked;

    /// <summary>
    /// 最近一次**页面闸门**的拒绝原因（验证前挡：无任务 / 计划 0 对象 / 源根不可访问）。
    /// 空 = 无拒绝。成功进入验证时清空。
    /// </summary>
    private string _gateReason = string.Empty;

    public Step4ResultPage()
    {
        InitializeComponent();
        HookActions();
    }

    /// <summary>
    /// 七个动作的**唯一挂接点**（用代码后置挂 Click，避免为接线去改已冻结的按钮结构）。
    /// 全部落到 <see cref="MigrationSessionViewModel"/> 的既有方法上，本页不重写任何业务逻辑。
    /// </summary>
    private void HookActions()
    {
        if (_actionsHooked) return;
        _actionsHooked = true;
        ToolbarVerify.Click += ToolbarVerify_Click;
        ToolbarRepair.Click += ToolbarRepair_Click;
        ToolbarOpenReport.Click += ToolbarOpenReport_Click;
        ToolbarOpenTarget.Click += ToolbarOpenTarget_Click;
        ToolbarResume.Click += ToolbarResume_Click;
        ToolbarOpenLogs.Click += ToolbarOpenLogs_Click;
    }

    public void ApplyState(PageReadiness state)
    {
        if (_state is not null) _state.PropertyChanged -= OnStateChanged;
        _state = state;
        _state.PropertyChanged += OnStateChanged;
        PushState();
    }

    /// <summary>
    /// Shell 注入会话状态源（Step2/3/4 共用的唯一业务状态）：本页的结果清单、实时日志与按钮可用性都读它。
    /// 两个集合用 <c>ItemsSource</c> 一次性挂上：VM 只在 UI 线程增删，ListView 自己跟随（页面不复制、不缓存）。
    /// </summary>
    public void AttachSession(MigrationSessionViewModel session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_session is not null) _session.PropertyChanged -= OnSessionChanged;
        _session = session;
        _session.PropertyChanged += OnSessionChanged;
        FailList.ItemsSource = session.FailItems;
        LiveLogList.ItemsSource = session.LogLines;
        PushState();
    }

    /// <summary>
    /// Shell 注入导航状态源：每次切到本页重新推一次真实状态
    /// （避免注入时机早于数据就绪而停在初始值，与 Step2/3 同一理由）。
    /// </summary>
    public void AttachNavigation(StepNavigation nav)
    {
        ArgumentNullException.ThrowIfNull(nav);
        if (_nav is not null) _nav.Changed -= OnNavigated;
        _nav = nav;
        _nav.Changed += OnNavigated;
    }

    /// <summary>
    /// Shell 注入口令来源（Step 1 的口令框）：修复 / 恢复需要凭据续连旧电脑。
    /// 与 Step2 的 _passwordProvider 同一来源，口令只作为参数传递，**不落任何字段、不写存档**。
    /// </summary>
    public void AttachPasswordProvider(Func<string?> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _passwordProvider = provider;
    }

    /// <summary>Shell 在启动探测「未完成任务」之后调用，让本页立刻反映探测结论（真实文案）。</summary>
    public void RefreshFromSession() => PushState();

    private void OnNavigated(StepKind from, StepKind to)
    {
        if (to == StepKind.Result) PushState();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e) => PushState();

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e) => PushState();

    /// <summary>
    /// 本页唯一的 UI 更新入口：把会话的**真实状态**推给命名元素。
    /// 每个位的权威来源见 <see cref="MigrationSessionViewModel"/> 的对应属性
    /// （LastVerifyResultText / FailItems / LogLines / CanVerifyNow / CanRepair / HasJob /
    ///  CanResume / PendingResumeCandidate / UnfinishedProbeText）。
    /// </summary>
    private void PushState()
    {
        // ── 线程纪律硬防线：非 UI 线程一律排队到 UI 线程，绝不在后台线程写控件 ──
        var queue = DispatcherQueue;
        if (queue is not null && !queue.HasThreadAccess) { queue.TryEnqueue(PushState); return; }

        if (_state is not null) StateLineText.Text = _state.Step4StateText;

        var session = _session;
        if (session is null) return;

        // ── 未完成任务探测结论（真实：来自 Core 的 JobManager.FindUnfinished）与状态行并列显示，不改任何几何 ──
        if (session.UnfinishedProbeText.Length > 0)
            StateLineText.Text = StateLineText.Text + "　" + session.UnfinishedProbeText;

        // ── 页面闸门的拒绝原因（有则常显，直到下一次成功进入验证） ──
        if (_gateReason.Length > 0)
            StateLineText.Text = "⛔ " + _gateReason + "　" + StateLineText.Text;

        // ── 按钮可用性：全部来自 VM 的真实布尔量，页面不自行推断 ──
        ToolbarVerify.IsEnabled = session.CanVerifyNow && !session.IsRunning;
        ToolbarRepair.IsEnabled = session.CanRepair;
        ToolbarOverwrite.IsEnabled = session.CanRepair;
        // ★ A6（F2，2026-09-30 巡检修复）★ 复选框的标签是**独立 TextBlock**（XAML :94），
        //   不随 CheckBox 的禁用态变灰 ⇒ 禁用时只有框体灰化、标签仍是深蓝（TextPrimaryBrush），
        //   与同一行其它禁用项（走 DisabledActionForegroundBrush 的按钮）视觉不一致（巡检截图 8× 实证）。
        //   这里显式对齐：禁用 ⇒ DisabledActionForegroundBrush（项目既有的禁用前景资源，Colors.xaml:31），
        //   可用 ⇒ 还原 PCMigTextFieldLabel 自己的 TextPrimaryBrush。不新增颜色、不改样式。
        ApplyOverwriteLabelForeground(session.CanRepair);
        ToolbarOpenReport.IsEnabled = session.HasJob;
        ToolbarOpenTarget.IsEnabled = session.HasJob || !string.IsNullOrWhiteSpace(session.TargetRoot);
        ToolbarResume.IsEnabled = session.CanResume || session.PendingResumeCandidate is not null;
        ToolbarOpenLogs.IsEnabled = true;   // 日志目录恒可打开（VM 按需创建该目录，不会"点了没反应"）

        // ── 真实数据与空状态二选一（互斥，不用转换器；与 Step3 的空状态同款做法） ──
        var hasFails = session.FailItems.Count > 0;
        FailList.Visibility = hasFails ? Visibility.Visible : Visibility.Collapsed;
        FailEmptyPanel.Visibility = hasFails ? Visibility.Collapsed : Visibility.Visible;

        var hasLogs = session.LogLines.Count > 0;
        LiveLogList.Visibility = hasLogs ? Visibility.Visible : Visibility.Collapsed;
        LiveLogEmptyHint.Visibility = hasLogs ? Visibility.Collapsed : Visibility.Visible;
    }

    // ────────────────────────── 动作：验证 / 修复 / 报告 / 目标 / 恢复 / 日志目录 ──────────────────────────

    /// <summary>
    /// 「验证完整性」：**页面层先过闸门**（与 VM 的 EvaluateVerifyGateAsync 同一判定，纵深两道），
    /// 通过后走 VM 的 VerifyAsync（L1 文件数/字节对账；结论文案由 VM 统一产出，绝不说成"完整性验证通过"）。
    /// </summary>
    private async void ToolbarVerify_Click(object sender, RoutedEventArgs e)
    {
        var session = _session;
        // ★ D6.1 §11 ★ 观察既有页面闸门（EvaluateVerifyGateAsync）的结果，不新增判据。
        using var trace = ActionTrace.Begin(ActionKinds.Verify, ControlIds.Step4Verify, "click", "Step4ResultPage");
        trace.Eligibility(session is not null, session is not null ? "allowed" : "no-session");
        if (session is null) { trace.Reject("no-session", "Step4ResultPage"); return; }
        ToolbarVerify.IsEnabled = false;   // 防重入；结束后由 PushState 恢复真实可用性
        try
        {
            var gate = await session.EvaluateVerifyGateAsync();
            trace.Eligibility(gate.CanVerify, gate.CanVerify ? "allowed" : "gate-blocked");
            if (!gate.CanVerify)
            {
                _gateReason = gate.Reason;
                session.AppendLog("WARN", "拒绝验证（页面闸门）：" + gate.Reason);
                trace.Reject("gate-blocked", "Step4ResultPage");
                return;
            }

            _gateReason = string.Empty;
            trace.Started();
            trace.Expect("verify.v1", "verify-completed");
            await session.VerifyAsync(VerifyLevel.L1_CountSize);
            trace.Confirm("verify.v1", "verify-completed");
            trace.Complete(DiagnosticOutcome.Succeeded, "verify-returned");
        }
        catch (Exception ex)
        {
            // 观察只记录现有异常路径（不改吞/抛语义）。
            trace.Fault(ex, "ToolbarVerify");
            session.AppendLog("ERROR", "验证异常：" + ex.Message);
        }
        finally
        {
            PushState();
        }
    }

    /// <summary>
    /// ★ A6（F2，2026-09-30 巡检修复）★ 让「强制覆盖」的标签跟随它自己的复选框可用性。
    ///
    /// 为什么需要手写：复选框旁的标签是**独立 TextBlock**（XAML :94），不是 CheckBox 的内容，
    /// 因此 WinUI 的 Disabled 视觉态只作用于框体本身；标签会一直保持 PCMigTextFieldLabel 的
    /// TextPrimaryBrush（深蓝），与同一行走 DisabledActionForegroundBrush 的禁用按钮不一致。
    ///
    /// 取值一律用**项目既有资源**（Colors.xaml:31 的 DisabledActionForegroundBrush =
    /// 多个 Disabled 视觉态共用的禁用前景色），不新增颜色、不改任何样式定义；
    /// 资源缺失时保持原样（不抛、不猜颜色）。注意这里只影响**标签的前景色**，
    /// 复选框的勾选值与「尝试修复」传入的 forceOverwrite 语义**完全不变**。
    /// </summary>
    private void ApplyOverwriteLabelForeground(bool enabled)
    {
        var key = enabled ? "TextPrimaryBrush" : "DisabledActionForegroundBrush";
        if (Application.Current.Resources.TryGetValue(key, out var res) && res is Brush brush)
            ToolbarOverwriteLabel.Foreground = brush;
    }

    /// <summary>
    /// 「尝试修复」：走 VM 的 RepairAsync（定向重拷 → 自动复验，旧 WPF 既有编排），
    /// 「强制覆盖」开关 ↔ <c>forceOverwrite</c>（旧 WPF 的 RepairForceOverwrite 默认开）。
    /// </summary>
    private async void ToolbarRepair_Click(object sender, RoutedEventArgs e)
    {
        var session = _session;
        // ★ D6.1 §11 ★ 观察点：不改任何修复参数与业务分支。
        using var trace = ActionTrace.Begin(ActionKinds.Repair, ControlIds.Step4Repair, "click", "Step4ResultPage");
        trace.Eligibility(session is not null, session is not null ? "allowed" : "no-session");
        if (session is null) { trace.Reject("no-session", "Step4ResultPage"); return; }
        var force = ToolbarOverwrite.IsChecked == true;
        session.AppendLog("INFO", $"用户点「尝试修复」（强制覆盖同名文件：{(force ? "开" : "关")}）。");
        trace.Started();
        trace.Expect("repair.v1", "repair-targets-collected");
        try
        {
            await session.RepairAsync(forceOverwrite: force, password: _passwordProvider?.Invoke());
            trace.Confirm("repair.v1", "repair-targets-collected");
            trace.Complete(DiagnosticOutcome.Succeeded, "repair-returned");
        }
        catch (Exception ex)
        {
            trace.Fault(ex, "ToolbarRepair");
            session.AppendLog("ERROR", "修复异常：" + ex.Message);
        }
        finally
        {
            PushState();
        }
    }

    /// <summary>「打开报告」：走 Core 的 ReportGenerator（唯一权威报告源），生成后用资源管理器打开。</summary>
    private async void ToolbarOpenReport_Click(object sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null) return;
        try { await session.OpenReportAsync(); }
        catch (Exception ex) { session.AppendLog("ERROR", "打开报告异常：" + ex.Message); }
        finally { PushState(); }
    }

    /// <summary>「打开目标文件夹」：打开当前任务的真实目标根（job.json 权威，退回界面目标根）。</summary>
    private void ToolbarOpenTarget_Click(object sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null) return;
        session.OpenTargetFolder();
        PushState();
    }

    /// <summary>
    /// 「恢复任务」：**必须由用户明确点击确认**（本页绝不自动恢复，也绝不自动载入）。
    /// 若当前没有活动任务但启动探测找到了候选任务，则先载入该候选（用户点击 = 明确确认），再续传；
    /// 续传沿用 job.json 里的线程原值与任务定义（VM 的 ResumeAsync 语义）。
    /// </summary>
    private async void ToolbarResume_Click(object sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null) return;
        try
        {
            if (!session.CanResume)
            {
                var candidate = session.PendingResumeCandidate;
                if (candidate is null)
                {
                    session.AppendLog("WARN", "恢复任务被拒绝：当前没有可恢复的任务（也没有检测到未完成任务）。");
                    return;
                }
                session.AppendLog("INFO", $"用户确认载入未完成任务 {candidate.JobId}（{candidate.PhaseText}）。");
                await session.AdoptExistingJobAsync(candidate.JobDir);
            }
            await session.ResumeAsync(_passwordProvider?.Invoke());
        }
        catch (Exception ex)
        {
            session.AppendLog("ERROR", "恢复任务异常：" + ex.Message);
        }
        finally
        {
            PushState();
        }
    }

    /// <summary>
    /// 「打开日志目录」：打开 Core 的真实应用日志目录（LogBootstrap.AppLogDir = %ProgramData%\PCMig\Logs）。
    /// 旧 WPF 无对应命令，属本包新增的纯导航动作，不碰任何业务状态。
    /// </summary>
    private void ToolbarOpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null) return;
        session.OpenLogDirectory();
        PushState();
    }

    /// <summary>Compact 时顶部操作条折为两行（验证/修复/覆盖 一行，报告/文件夹/恢复 一行）；纯视觉。</summary>
    public void ApplyLayoutMode(LayoutMode mode)
    {
        if (ToolbarGrid is null) return;
        var compact = mode == LayoutMode.Compact;
        ToolbarGrid.RowDefinitions[1].Height = compact ? GridLength.Auto : new GridLength(0);
        var second = new[] { ToolbarOpenReport, ToolbarOpenTarget, ToolbarResume };
        for (var i = 0; i < second.Length; i++)
        {
            if (second[i] is null) continue;
            Grid.SetRow(second[i], compact ? 1 : 0);
            Grid.SetColumn(second[i], compact ? i : i + 4);
        }
    }
}