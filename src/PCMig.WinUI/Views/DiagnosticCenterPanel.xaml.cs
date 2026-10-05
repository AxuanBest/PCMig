using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.WinUI.Diagnostics;
using PCMig.WinUI.Presentation;

namespace PCMig.WinUI.Views;

/// <summary>
/// 诊断中心浮层（工具面）。
///
/// 职责边界（与其它工具面板同一纪律）：
///   · **只显示与交互**：数据全部来自 <see cref="DiagnosticCenterViewModel"/> 的只读投影；
///   · **不新建业务状态**：不碰迁移/暂停/验证，也不改任何 JobPhase；
///   · **不做进入动画**：打开/关闭由 Shell 的 Fluid Zoom（OACT）与 TryPlacePanel（TAOP）统一编排；
///   · **有界刷新**：面板打开时用一个 500ms 的 DispatcherQueueTimer 驱动刷新，
///     关闭即停（绝不在后台常驻计时器，也不在引擎线程上写控件）；
///   · **刷新失败不影响任何业务**：VM 内部已吞异常并回显状态。
/// </summary>
public sealed partial class DiagnosticCenterPanel : UserControl
{
    private const int RefreshIntervalMs = 500;

    private DiagnosticCenterViewModel? _vm;
    private DispatcherQueueTimer? _timer;

    /// <summary>导出进行中的取消源（null = 没有导出在跑；再次点击导出按钮即取消）。</summary>
    private CancellationTokenSource? _exportCts;

    public DiagnosticCenterPanel() => InitializeComponent();

    /// <summary>请求关闭（由 Shell 播放反向 morph 后折叠可见性）。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Deep Trace 开关变化（true=开启）。**由 Shell 负责真正的输入观测注册**：
    /// 面板不持有根元素，也不该自己往整棵树上挂处理器（生命周期归 Shell，便于关窗时统一注销）。
    /// </summary>
    public event EventHandler<bool>? DeepTraceToggled;

    /// <summary>装配 ViewModel（幂等；可在每次打开时重新注入）。</summary>
    public void Attach(DiagnosticCenterViewModel viewModel)
    {
        _vm = viewModel;
        RefreshNow();
        StartTimer();
    }

    /// <summary>面板关闭时调用：停掉刷新节拍（幂等）。</summary>
    public void StopRefresh()
    {
        if (_timer is not null) _timer.Stop();
        _timer = null;
    }

    /// <summary>打开后把焦点交给第一个可操作控件（键盘可达性）。</summary>
    public void FocusFirstElement() => RefreshButton.Focus(FocusState.Programmatic);

    private void StartTimer()
    {
        var queue = DispatcherQueue;
        if (queue is null) return;                 // 无 UI 队列（测试/异常路径）⇒ 只做一次刷新

        _timer ??= queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(RefreshIntervalMs);
        _timer.IsRepeating = true;
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(DispatcherQueueTimer sender, object args) => RefreshNow();

    /// <summary>刷新一次并落到控件（**UI 线程**；绝不在后台线程写控件）。</summary>
    private void RefreshNow()
    {
        var vm = _vm;
        if (vm is null) return;

        try
        {
            vm.Refresh();

            CaptionText.Text = $"会话 {vm.SessionShortId} · 采集 {vm.ModeName} · {vm.StorageRootText}";
            HealthText.Text = vm.OverviewHealthText;
            EvidenceText.Text = vm.EvidenceText;
            CountersText.Text = vm.CountersText;
            ResourceText.Text = vm.ResourceText;
            IncidentSummaryText.Text = vm.IncidentSummaryText;
            IncidentDetailText.Text = vm.SelectedIncidentDetail ?? "选中一张事件卡可看它的证据与候选原因。";
            StatusText.Text = vm.StatusText ?? string.Empty;

            // 列表：只在内容变化时整体替换（有界行数 ⇒ 不构成 UI 压力），并尽量避免无谓重绑。
            ReplaceItems(IncidentList, vm.Incidents.ToArray());
            ReplaceItems(TimelineList, vm.Timeline.ToArray());
            ReplaceItems(MetricList, vm.Metrics.ToArray());
            ReplaceItems(LogList, vm.FilteredLog.ToArray());

            DeepTraceToggle.IsChecked = vm.DeepTraceRequested;
        }
        catch (Exception ex)
        {
            // 诊断中心自己坏掉也不得拖垮 Shell。
            StatusText.Text = "刷新失败：" + ex.GetType().Name;
            ActionTrace.DispatchRejected("panel-refresh-failed", "DiagnosticCenterPanel", "DiagnosticCenterPanel");
        }
    }

    private static void ReplaceItems<T>(ListView list, T[] items)
    {
        if (list.Items.Count == items.Length)
        {
            var same = true;
            for (var i = 0; i < items.Length; i++)
            {
                if (!Equals(list.Items[i], items[i])) { same = false; break; }
            }
            if (same) return;
        }

        list.Items.Clear();
        foreach (var item in items) list.Items.Add(item);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshNow();

    private void DeepTrace_Click(object sender, RoutedEventArgs e)
    {
        var enabled = DeepTraceToggle.IsChecked == true;
        // 顺序有语义：先把采集模式切到 Deep（Verbose 类事件才会被接受），再让 Shell 注册输入观测。
        _vm?.RequestDeepTrace(enabled);
        DeepTraceToggled?.Invoke(this, enabled);
        RefreshNow();
    }

    private void WarnOnly_Click(object sender, RoutedEventArgs e)
    {
        _vm?.SetLogFilter(WarnOnlyToggle.IsChecked == true ? DiagnosticLogLevel.Warn : DiagnosticLogLevel.Info);
        RefreshNow();
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        // ★ D6.1 §18 ★ 运行中再点一次 = **取消**：不新增控件（不改版面、不碰 PMML 视觉），
        //   复用既有按钮与状态行；真正的 seal/cutoff/脱敏/压缩/哈希都在后台线程做。
        if (_exportCts is not null)
        {
            try { _exportCts.Cancel(); } catch (Exception) { /* 取消失败不影响结论 */ }
            StatusText.Text = "已请求取消导出…";
            return;
        }

        using var exportCts = new CancellationTokenSource();
        _exportCts = exportCts;

        // ★ D6.1 §11 ★ 导出动作链：只观察，不改导出逻辑与产物。
        using var trace = ActionTrace.Begin(ActionKinds.Export, ControlIds.DiagnosticsExport, "click", "DiagnosticCenterPanel");
        trace.Eligibility(true, "allowed");
        trace.Started();
        trace.Expect("export.v1", "export.external");
        try
        {
            StatusText.Text = "正在导出诊断包（本地文件，不会上传）…再次点击「导出诊断包」可取消。";
            PublishExport(DiagnosticsEvents.ExportStarted, DiagnosticOutcome.Accepted, DiagnosticLevel.Information);

            var result = await _vm.ExportAsync(exportCts.Token);

            PublishExport(
                result.Succeeded ? DiagnosticsEvents.ExportCompleted : DiagnosticsEvents.ExportFailed,
                result.Succeeded ? DiagnosticOutcome.Succeeded : DiagnosticOutcome.Failed,
                result.Succeeded ? DiagnosticLevel.Information : DiagnosticLevel.Warning,
                result.FailureReason);

            if (result.Succeeded)
            {
                trace.Confirm("export.v1", "export.external");
                trace.Complete(DiagnosticOutcome.Succeeded, "export-completed");
            }
            else
            {
                trace.Complete(DiagnosticOutcome.Failed, "export-failed");
            }
        }
        finally
        {
            _exportCts = null;
            RefreshNow();
        }
    }

    /// <summary>导出链路的证据事件（D6.1 §11）。只发布观察，不参与导出决策。</summary>
    private static void PublishExport(
        PCMig.Diagnostics.Abstractions.EventDescriptor descriptor,
        DiagnosticOutcome outcome,
        DiagnosticLevel level,
        string? exceptionType = null)
        => PCMig.Core.Diagnostics.CoreDiagnostics.PublishUiAction(
            descriptor, payload: null, outcome: outcome, level: level,
            component: "DiagnosticCenterPanel", exceptionType: exceptionType);

    private void IncidentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is null) return;
        var row = IncidentList.SelectedItem as IncidentRow;
        _vm.SelectIncident(row?.IncidentId);
        RefreshNow();
    }
}