using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PCMig.Core.Models;
using PCMig.Core.Verify;
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

    // ★ PMML 返修（2026-10-11）★ 这里曾有 `private VerifyDetailWindow? _verifyWindow;`（P1-12 的独立二级窗口）。
    // 验证的进度/结论现在由 Shell 内的唯一面板 `VerificationProgressPanel` 承担，
    // 本页不再持有任何窗口句柄：面板的装载/打开/收起全由 `EnsureVerifyPanel` + Shell 编排完成。

    /// <summary>当前验证的取消源（只有页面层持有它 ⇒ 窗口的"取消"才真的能取消引擎）。</summary>
    private System.Threading.CancellationTokenSource? _verifyCts;

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

        // 「验证完整性」现在只是打开只读工具面板；可否开始验证由面板内的显式开始动作和 VM 真闸门决定。
        ToolbarVerify.IsEnabled = true;
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
    /// ★ v0.5.4 Preview.2 真机修复（2026-10-16，用户任务令 §六）★
    /// 打开既有验证浮层（浮层里才有「开始验证」）。打开本身绝不执行闸门、验证或任何 IO。
    ///
    /// 为什么文案改成「校验与验证」：本机冒烟测试（2026-10-15）与真机日志都显示，
    /// 用户点「验证完整性」后拿到的是 `ActionKinds.Verify / "open-panel"`，报告目录没有新文件、
    /// 任务日志没有新验证记录 —— 也就是说这个按钮**从来没执行过验证**。
    /// 用户令要求「UI 语义必须让用户明确知道按钮是执行验证还是查看报告」，
    /// 且**不许靠改文案掩盖功能缺口**。因此这里：①按钮名不再冒充"执行验证"这个动作，
    /// 改成它实际做的事（打开校验与验证面板）；②真正的执行入口是面板内那颗
    /// 「开始验证」按钮（<c>StartVerifyButton</c>），文案直说"开始"，不会被误读成"查看"；
    /// ③ToolTip 把两者的分工讲清，避免用户再猜。功能一行没有删减。
    /// </summary>
    private void ToolbarVerify_Click(object sender, RoutedEventArgs e)
    {
        var session = _session;
        using var trace = ActionTrace.Begin(ActionKinds.Verify, ControlIds.Step4Verify, "open-panel", "Step4ResultPage");
        trace.Eligibility(session is not null, session is not null ? "allowed" : "no-session");
        if (session is null) { trace.Reject("no-session", "Step4ResultPage"); return; }
        var panel = EnsureVerifyPanel(session);
        if (panel is null) { trace.Reject("panel-unavailable", "Step4ResultPage"); return; }
        trace.Complete(DiagnosticOutcome.Succeeded, "panel-opened");
    }

    /// <summary>唯一显式 L1 启动入口；互斥覆盖整个验证生命周期，而不仅是按钮视觉状态。</summary>
    private async Task StartVerificationAsync(MigrationSessionViewModel session, VerificationProgressPanel panel)
    {
        if (_verifyCts is not null) return;
        var cts = new System.Threading.CancellationTokenSource();
        _verifyCts = cts;
        using var trace = ActionTrace.Begin(ActionKinds.Verify, ControlIds.Step4Verify, "start", "Step4ResultPage");
        try
        {
            panel.BeginRun();
            panel.BeginPreflight();
            var progress = new Progress<string>(panel.ApplyLine);
            var detail = new Progress<VerifyProgressSnapshot>(panel.ApplySnapshot);
            panel.SetCancelHandler(() => { try { cts.Cancel(); } catch { } });
            trace.Started();
            trace.Expect("verify.v1", "verify.external");
            await session.VerifyAsync(VerifyLevel.L1_CountSize, progress, cts.Token, detail);
            trace.Confirm("verify.v1", "verify.external");
            trace.Finish(session.LastVerifyOutcome, "Step4ResultPage");
            if (session.LastVerifyOutcome.ReasonCode == "verify-gate-blocked")
                panel.FinishBlocked(session.LastVerifyResultText);
            else
                panel.FinishWith(session.LastVerifyReport, session.LastVerifyResultText,
                    session.LastVerifyOutcome.Outcome == DiagnosticOutcome.Canceled, SourcePathMap(session));
        }
        catch (Exception ex)
        {
            trace.Fault(ex, "StartVerificationAsync");
            session.AppendLog("ERROR", "验证异常：" + ex.Message);
            panel.FinishWith(null, "验证异常：" + ex.Message, canceled: false);
        }
        finally
        {
            if (ReferenceEquals(_verifyCts, cts)) _verifyCts = null;
            cts.Dispose();
            PushState();
        }
    }

    /// <summary>
    /// ★ PMML 返修（2026-10-11）★ 取得并打开验证完整性面板（Shell 内的唯一实例）。
    ///
    /// 与旧独立二级窗口的差别：
    ///   · 面板属于**应用内工具浮层**（与诊断中心同一 PMML 家族），锚点 = 本页真实的「校验与验证」按钮，
    ///     开合与定位由 Shell 的 FluidZoom + TAOP 负责 —— 本页只负责"装载作业文案 + 打开 + 喂数据"；
    ///   · 不存在"面板被用户关掉"的生命周期问题：打开是幂等的，收起后再点入口仍然是同一个面板。
    ///
    /// 面板取不到（极端环境：Shell 还没构造 / 类型不符）返回 null ⇒ 调用方必须仍然正常跑验证
    /// （真实计数只是**附加视图**，绝不因为面板开不出来就阻断验证）。
    /// </summary>
    private VerificationProgressPanel? EnsureVerifyPanel(MigrationSessionViewModel session)
    {
        try
        {
            if (App.MainWindowInstance is not MainWindow shell) return null;
            var panel = shell.VerificationPanel;
            panel.Attach(session.JobIdText, "L1 文件数与字节对账");
            // ★ m03985 §UI-C/§UI-D ★ 「打开验证报告」的处理器由页面层提供：面板自己不做文件操作，
            //   只显示结果（成功 / 已生成本机路径但浏览器打不开 / 还没有结论）。
            panel.SetOpenReportHandler(() => session.OpenVerifyReportAsync());
            panel.SetStartHandler(() => StartVerificationAsync(session, panel));
            panel.SetCancelHandler(() => { try { _verifyCts?.Cancel(); } catch { } });
            if (_verifyCts is null)
            {
                if (session.LastVerifyReport is not null)
                    panel.ShowPreviousResult(session.LastVerifyReport, session.LastVerifyResultText, SourcePathMap(session));
                else
                    panel.ShowIdle();
            }
            shell.ShowVerificationPanel(ToolbarVerify);
            session.AppendLog("INFO", $"验证面板已打开（{DateTime.Now:HH:mm:ss}）。");
            return panel;
        }
        catch (Exception ex)
        {
            session.AppendLog("WARN", "验证面板打开失败（验证照常进行）：" + ex.Message);
            return null;
        }
    }

    /// <summary>ObjectId → 源路径（来自计划；窗口只用来显示"在核对谁"，取不到就空着，不臆造）。</summary>
    private static IReadOnlyDictionary<string, string> SourcePathMap(MigrationSessionViewModel session)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in session.Objects)
            if (!string.IsNullOrWhiteSpace(row.ObjectId)) map[row.ObjectId] = row.SourcePath;
        return map;
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
        trace.Expect("repair.v1", "repair.external");
        try
        {
            await session.RepairAsync(forceOverwrite: force, password: _passwordProvider?.Invoke());
            trace.Confirm("repair.v1", "repair.external");
            // D6.3 §11：终点结果 = 既有回执统计（无目标 ⇒ Skipped；有失败 ⇒ Failed；全成 ⇒ Succeeded）。
            trace.Finish(session.LastRepairOutcome, "Step4ResultPage");
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
    ///
    /// ★ D6.3 §11（缺口②收口）★ 本方法原先**完全没有** ActionTrace：Step4 工具栏点「恢复任务」
    /// 在诊断里不产生任何 UI-00x（真实用户动作成了不可答的黑洞），而同页的「验证」/「尝试修复」
    /// 与 Step2 的「恢复」都有完整因果链。现在按同一范式补齐插桩：
    /// **只补观察，不改任何业务语义与判据**（点击后的处理路径、拒绝条件、日志文本逐字未变）。
    /// </summary>
    private async void ToolbarResume_Click(object sender, RoutedEventArgs e)
    {
        var session = _session;
        using var trace = ActionTrace.Begin(ActionKinds.Resume, ControlIds.Step4Resume, "click", "Step4ResultPage");
        if (session is null)
        {
            trace.Eligibility(false, "no-session");
            trace.Reject("no-session", "Step4ResultPage");
            return;
        }

        // 资格判据 = 本页启用该按钮的**既有**条件（PushState 里同一个表达式），不为诊断另造一套判据。
        var canClick = session.CanResume || session.PendingResumeCandidate is not null;
        trace.Eligibility(canClick, canClick ? "allowed" : "no-resumable-task");

        try
        {
            if (!session.CanResume)
            {
                var candidate = session.PendingResumeCandidate;
                if (candidate is null)
                {
                    session.AppendLog("WARN", "恢复任务被拒绝：当前没有可恢复的任务（也没有检测到未完成任务）。");
                    // 拒绝也必须留痕，否则"点了没反应"在诊断里依旧无从解释。
                    trace.Reject("cannot-resume", "Step4ResultPage");
                    return;
                }
                session.AppendLog("INFO", $"用户确认载入未完成任务 {candidate.JobId}（{candidate.PhaseText}）。");
                await session.AdoptExistingJobAsync(candidate.JobDir);
            }

            trace.Started();
            // ★ FIX BATCH 3 / §6.4 ★ 步骤名必须与契约（resume.v1）里的名字一致：
            //   旧值 "run-resumed" 在注册表里不存在（契约里是 resume.immediate / resume.external）。
            trace.Expect("resume.v1", "resume.external");
            await session.ResumeAsync(_passwordProvider?.Invoke());
            // ★ FIX BATCH 3 / §6.4 ★ 等引擎真的离开暂停/真的开始运行再收口 —— 
            //   "ResumeAsync 返回了"绝不等于"迁移已恢复"。
            if (await session.WaitForResumeSettledAsync())
            {
                trace.Confirm("resume.v1", "resume.external");
                trace.Complete(DiagnosticOutcome.Succeeded, "resume-settled");
            }
            else
            {
                trace.Complete(DiagnosticOutcome.Unknown, "resume-unsettled");
            }
        }
        catch (Exception ex)
        {
            trace.Fault(ex, "ToolbarResume");
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

    /// <summary>
    /// ★ P1-4（Preview.2）★ 「查看完整原因」：把该条失败/中断项的**完整原因**整段展示出来
    /// （回执原文未截断 + 退出码位含义 + robocopy 日志证据摘要 + 日志/任务/报告路径）。
    /// 纯只读展示：不写回执、不改判定、不产生任何业务副作用 —— 因此不开 ActionTrace 业务动作
    /// （它记录的是"用户发起了哪个业务动作"，打开一段说明文字不属于其中之一）。
    /// </summary>
    private async void OnFailDetailClick(object sender, RoutedEventArgs e)
    {
        // ★ P0-06(4) ★ ContentDialog 的既有硬限制：同一时刻只允许一个对话框。
        //   本处理器是 `async void` ⇒ ShowAsync 抛出来没有调用者能接住，直接崩进程。
        try
        {
        if (sender is not FrameworkElement { DataContext: SessionFailItem item }) return;

        // 没有全文就不该有按钮（可见性绑的是同一个布尔 CanShowDetail）；万一被点到，也只能如实显示已有的截断文案。
        var text = string.IsNullOrWhiteSpace(item.FullText) ? item.Detail : item.FullText!;

        var body = new ScrollViewer
        {
            Content = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,   // 允许选中复制（真机排障要把它贴进工单）
                FontSize = 13,
            },
            MaxHeight = 460,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var dialog = new ContentDialog
        {
            Title = item.Title,
            Content = body,
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
        }
        catch (Exception ex) { UiFaultGuard.Report("Step4ResultPage.OnFailDetailClick", ex, m => _session?.AppendLog("ERROR", m)); }
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