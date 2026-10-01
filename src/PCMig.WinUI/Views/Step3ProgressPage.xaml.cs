using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PCMig.WinUI.Presentation;

namespace PCMig.WinUI.Views;

/// <summary>
/// Step 3 — 迁移进度（状态接线同 Step2：**代码直推 + 变更订阅**）。
///
/// 阶段 A 包 3（本文件）：
///   · 页面里所有进度数值都来自 <see cref="MigrationSessionViewModel"/> 的真实状态
///     （该状态又只来自 Core：ProgressSnapshot / 引擎三事件 / 回执），**页面自己不含任何假数据**；
///   · 绑定形态沿用本页既有唯一可行路线：Shell 注入状态源 → 直接写命名元素；
///     （页面级经典 {Binding} + DataContext 与嵌套 {x:Bind} 在本项目 WinUI 页实测渲染为空，
///       故不做第二次尝试；DataTemplate **内部**的经典 {Binding} 仍然可用，列表行就是这样绑的。）
///   · 页面**不加任何按钮**（零按钮是冻结布局）：暂停 / 停止 / 恢复一律走四页常驻的共用底栏按钮，
///     它们在 Shell（MainWindow）里接同一个会话。
///
/// 线程纪律：本文件所有控件写入都在 UI 线程。会话的属性变更本就发生在 UI 线程
/// （VM 的 Post = DispatcherQueue.TryEnqueue，见 MigrationSessionViewModel.cs:1628-1633），
/// 这里再加一道硬防线：非 UI 线程调用 PushState 时改由 DispatcherQueue 排队，绝不直接碰控件。
/// </summary>
public sealed partial class Step3ProgressPage : UserControl
{
    private PageReadiness? _state;
    private MigrationSessionViewModel? _session;
    private StepNavigation? _nav;

    public Step3ProgressPage()
    {
        InitializeComponent();
    }

    public void ApplyState(PageReadiness state)
    {
        if (_state is not null) _state.PropertyChanged -= OnStateChanged;
        _state = state;
        _state.PropertyChanged += OnStateChanged;
        PushState();
    }

    /// <summary>
    /// Shell 注入会话状态源（Step2/3/4 的唯一业务状态）：本页的全部进度位都读它。
    /// 集合用 <c>ItemsSource = ObservableCollection</c> 一次性挂上：VM 只在 UI 线程增删，
    /// ListView 自己跟随，页面不重建、不复制、不缓存任何一份数据。
    /// </summary>
    public void AttachSession(MigrationSessionViewModel session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_session is not null) _session.PropertyChanged -= OnSessionChanged;
        _session = session;
        _session.PropertyChanged += OnSessionChanged;
        ObjectList.ItemsSource = session.Objects;
        LiveFileList.ItemsSource = session.LiveFiles;
        PushState();
    }

    /// <summary>
    /// Shell 注入导航状态源：每次切到本页重新推一次真实状态
    /// （避免注入时机早于数据就绪而停在初始值，与 Step2 的 AttachNavigation 同一理由）。
    /// </summary>
    public void AttachNavigation(StepNavigation nav)
    {
        ArgumentNullException.ThrowIfNull(nav);
        if (_nav is not null) _nav.Changed -= OnNavigated;
        _nav = nav;
        _nav.Changed += OnNavigated;
    }

    private void OnNavigated(StepKind from, StepKind to)
    {
        if (to == StepKind.Progress) PushState();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e) => PushState();

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e) => PushState();

    /// <summary>
    /// 本页唯一的 UI 更新入口：把会话的真实状态推给命名元素。
    /// 每个数值的权威来源见 <see cref="MigrationSessionViewModel"/> 的对应属性
    /// （Percent/ProgressText/ObjectText/PlanBytesText/ActualBytesText/DataLabel/
    ///   EngineSpeedText/EtaText/CurrentFileText/StallHintText/CurrentObjectDetail/PhaseText）。
    /// </summary>
    private void PushState()
    {
        // ── 线程纪律硬防线：非 UI 线程一律排队到 UI 线程，绝不在后台线程写控件 ──
        var queue = DispatcherQueue;
        if (queue is not null && !queue.HasThreadAccess) { queue.TryEnqueue(PushState); return; }

        // 总进度卡的状态句仍由薄适配层投影（Step1 连接 + 会话真实状态），此处不重算。
        if (_state is not null) StateLineText.Text = _state.Step3StateText;

        var session = _session;
        if (session is null) return;

        // ── 总进度：百分比 / 已传字节 / 进度条 ──
        var percent = session.Percent;
        TotalPercentText.Text = $"{percent:0.0}%";
        TotalBytesText.Text = session.ProgressText;
        TotalProgressBar.Value = double.IsNaN(percent) ? 0 : Math.Clamp(percent, 0, 100);

        // ── 四张统计卡：传输速度 / 预计剩余 / 对象进度 / 已传-计划 ──
        SpeedValueText.Text = session.EngineSpeedText;
        EtaValueText.Text = session.EtaText;
        ObjectValueText.Text = session.ObjectText;
        BytesCardLabel.Text = session.DataLabel;
        BytesValueText.Text = $"{session.ActualBytesText} / {session.PlanBytesText}";

        // ── 对象明细提示行：运行中给“正在做什么”，非运行态给**阶段**（四态在此彼此可区分）──
        ObjectPaneHintText.Text = DescribePhaseOrActivity(session);

        // ── 文件流提示行：停滞提示优先，否则给真实“最近一条已复制文件” ──
        //
        // ★ A6（F4，2026-09-30 巡检修复）★ 没有**真实文件流**时这一行必须留空并折叠。
        //   缺陷：复位路径会把 CurrentFileText 置为占位符「—」（MigrationSessionViewModel.cs:2501），
        //   改前它被无条件当作提示行显示，于是空状态卡里出现「当前没有正在复制的文件。」+
        //   一条孤立「—」，两行自相矛盾（巡检截图 2× 实证）。
        //   语义边界：HasStallHint（停滞提示）是**运行中真实产生的结论**，任何情况下都照实显示；
        //   只有"还没跑过任何文件、又要拿占位符充数"这一种情况才留空。
        var hasFiles = session.LiveFiles.Count > 0;
        var hintText = session.HasStallHint
            ? session.StallHintText
            : (hasFiles ? session.CurrentFileText : string.Empty);
        LiveFilesHintText.Text = hintText;
        LiveFilesHintText.Visibility = string.IsNullOrWhiteSpace(hintText) ? Visibility.Collapsed : Visibility.Visible;

        // ── 空状态 / 真实清单 二选一（互斥，不用转换器；与 Step1 的 EmptySharesHint 同款做法）──
        var hasObjects = session.Objects.Count > 0;
        ObjectList.Visibility = hasObjects ? Visibility.Visible : Visibility.Collapsed;
        ObjectEmptyPanel.Visibility = hasObjects ? Visibility.Collapsed : Visibility.Visible;

        LiveFileList.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        LiveFilesEmptyPanel.Visibility = hasFiles ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 运行中优先显示“当前对象 / 停滞提示”；**非运行态一律显示阶段文案**。
    ///
    /// 为什么必须这样分：
    ///   Pause（已暂停，可续传）、Stop→Interrupted（已中断，可续传）、Canceled（已取消，不可续传）、
    ///   以及 Resume 后的 Running（正在传输），四者在界面上都“没在跑”，但语义完全不同；
    ///   若统一显示成一句“未在传输”，用户就无法判断该点「恢复」还是该重新开始。
    ///   这里直接取会话的 <see cref="MigrationSessionViewModel.PhaseText"/>（由 Core 的 JobPhase 驱动），
    ///   不自行发明第二套状态。
    /// </summary>
    private static string DescribePhaseOrActivity(MigrationSessionViewModel session)
    {
        if (session.IsRunning)
        {
            if (session.HasStallHint) return session.StallHintText;
            if (session.HasCurrentObjectDetail) return session.CurrentObjectDetail;
        }
        return session.PhaseText;
    }

    /// <summary>Compact 时统计卡折行 2x2、下双栏改单栏堆叠；纯视觉，不接触业务状态。</summary>
    public void ApplyLayoutMode(LayoutMode mode)
    {
        if (StatCardsGrid is null || LowerPanesGrid is null) return;
        var compact = mode == LayoutMode.Compact;

        var statCols = StatCardsGrid.ColumnDefinitions;
        statCols[2].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        statCols[3].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        StatCardsGrid.RowDefinitions[1].Height = compact ? GridLength.Auto : new GridLength(0);

        var cards = new[] { StatCard0, StatCard1, StatCard2, StatCard3 };
        for (var i = 0; i < cards.Length; i++)
        {
            if (cards[i] is null) continue;
            Grid.SetRow(cards[i], compact ? i / 2 : 0);
            Grid.SetColumn(cards[i], compact ? i % 2 : i);
        }

        LowerPanesGrid.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        LowerPanesGrid.RowDefinitions[1].Height = compact ? GridLength.Auto : new GridLength(0);
        if (LowerPaneRight is not null)
        {
            Grid.SetRow(LowerPaneRight, compact ? 1 : 0);
            Grid.SetColumn(LowerPaneRight, compact ? 0 : 1);
        }
    }
}