using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PCMig.Core.Models;
using PCMig.Core.Util;
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

    /// <summary>★ FIX BATCH 5 ★ 顶栏进度填充的唯一真值来源（由 PushState 从 ProgressTruthSnapshot 写入）。</summary>
    private double _totalProgressPercent;

    /// <summary>
    /// ★ UI Closure 2026-10-05（用户指令 UI-05）★ 进度条**渲染补间**驱动：真值仍由本页唯一写入者提供，
    /// 驱动只把它在 Composition 层（GPU、InsetClip.RightInset）平滑过去，不参与任何业务判断。
    /// </summary>
    private ProgressMotionDriver? _progressMotion;

    /// <summary>上一次交给驱动的真值像素宽（用于识别"目标回退" ⇒ 必须 SnapTo 而不是回爬）。</summary>
    private double _lastRenderedWidth;

    /// <summary>★ PHASE C-1 探针 ★ 调试覆盖层刷新定时器（仅 PCMIG_PROGRESS_DEBUG=1 时存在）。</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _debugTimer;

    public Step3ProgressPage()
    {
        InitializeComponent();
        // ★ FIX BATCH 5（§8）★ 轨道宽度变化（窗口缩放 / 密度档位切换）时必须重算填充宽度，
        //   否则填充会停留在上一次的像素值上与真实百分比不符。写入者仍是 UpdateTotalProgressFill（唯一）。
        TotalProgressHost.SizeChanged += (_, _) => UpdateTotalProgressFill();
        // ★ UI Closure 2026-10-05（用户指令 UI-05）★ 填充元素常驻满宽（XAML 本地 Width="Auto" 覆盖样式里的 0），
        //   用 Composition 的 InsetClip 表示"未完成部分"，在 GPU 上把视觉补间到真值。
        _progressMotion = new ProgressMotionDriver(TotalProgressFill, TotalProgressHost);
        StartDebugProbe();
    }

    /// <summary>
    /// ★ PHASE C-1 探针（PCMIG_PROGRESS_DEBUG=1）★ 每 250 ms 把进度条渲染链路的内部状态写进屏幕角落的
    /// 诊断覆盖层：驱动是否 Active、轨道/真值/视觉像素、RightInset、三个装饰物 IsVisible、真值到达间隔 EMA，
    /// 以及 raw（引擎真值）与 display（显示真值，含 Resume floor）两层数据。
    ///
    /// 为什么需要它：上一轮把"代码里调用了 StartAnimation"当成验收结论，而真机上像素可能毫无变化
    /// （§0 的 UI-05/UI-06 被证伪）。探针 + 帧序列像素差分才是证据。探针不改任何业务与真值行为。
    /// </summary>
    private void StartDebugProbe()
    {
        if (!ProgressMotionDriver.DebugEnabled) return;
        ProgressDebugPanel.Visibility = Visibility.Visible;
        try
        {
            var timer = DispatcherQueue?.CreateTimer();
            if (timer is null)
            {
                UpdateDebugProbe();
                return;
            }

            timer.Interval = TimeSpan.FromMilliseconds(250);
            timer.IsRepeating = true;
            timer.Tick += (_, _) => UpdateDebugProbe();
            timer.Start();
            _debugTimer = timer;
        }
        catch
        {
            // 探针失败绝不影响进度显示本身。
        }

        UpdateDebugProbe();
    }

    private void UpdateDebugProbe()
    {
        if (!ProgressMotionDriver.DebugEnabled) return;

        try
        {
            var motion = _progressMotion?.DescribeDebug() ?? "PMD (none)";
            var session = _session;
            var raw = session?.LastTruth;
            var shown = session?.PresentationTruth;
            ProgressDebugText.Text =
                $"{motion}\n"
                + $"phase={session?.Phase.ToString() ?? "-"} uiPercent={_totalProgressPercent:0.0}\n"
                + $"raw: pct={raw?.Percent:0.0} displayed={raw?.DisplayedTransferredBytes ?? 0} committed={raw?.CommittedBytes ?? 0} "
                + $"inFlight={raw?.InFlightConfirmedBytes ?? 0} src={raw?.InFlightSource.ToString() ?? "-"} retry={raw?.RetryState.ToString() ?? "-"}\n"
                + $"display: pct={shown?.Percent:0.0} displayed={shown?.DisplayedTransferredBytes ?? 0} "
                + $"floorActive={session?.ResumeDisplayFloorActive ?? false} floorBytes={session?.ResumeDisplayFloorBytes ?? 0}\n"
                + $"track={TotalProgressHost.ActualWidth:0.0} fillActual={TotalProgressFill.ActualWidth:0.0} localNow={DateTime.Now:HH:mm:ss.fff}";
        }
        catch
        {
            // 同上。
        }
    }

    /// <summary>
    /// ★ FIX BATCH 5（§8）★ 顶栏进度填充的**唯一写入者**：宽度 = 百分比 × 轨道实际宽度。
    /// 为什么不用 ProgressBar 控件：见 <c>Themes\Controls.xaml</c> 中 PCMigProgressTrack/PCMigProgressFill
    /// 的说明 —— WinUI 3 的 ProgressBar 不会按 DeterminateRoot/ProgressBarIndicator 驱动自定义模板的
    /// 填充宽度（实测 UIA 值 47.29% 而像素零变化）。这里没有任何动画推进宽度；百分比本身来自唯一真值。
    /// </summary>
    private void UpdateTotalProgressFill()
    {
        var percent = Math.Clamp(double.IsNaN(_totalProgressPercent) ? 0 : _totalProgressPercent, 0, 100);
        var trackWidth = TotalProgressHost.ActualWidth;
        var width = trackWidth > 0 ? Math.Round(trackWidth * percent / 100.0, 1) : 0;

        // ★ UI Closure 2026-10-05（用户指令 UI-05 / §23 禁止的假修复）★ 补间只用于"运行中的正常推进"：
        //   · 暂停 / 暂停中 / 停止 / 中断 / 失败 / 完成 —— 一律 SnapTo（不留在半路，也绝不让光波/粒子继续前进）；
        //   · 目标比上一次更小（新任务、真值重置）—— SnapTo（进度不可能变小，视觉回爬就是伪造）；
        //   · 系统关闭动画 —— 驱动内部直接落位，业务状态与最终像素完全一致。
        var motion = _progressMotion;
        if (motion is not null)
        {
            motion.SetTrackWidth(trackWidth);
            var phase = _session?.Phase ?? JobPhase.Created;
            // ★ UI-06 状态映射 ★ 只有 Running 开装饰：暂停 / 停止 / 失败 / 完成一律关（暂停时让光波继续扫 = 撒谎）。
            //   走局部引用 motion 而不是字段：字段可能被其它路径改写，编译器对字段解引用会报 CS8602。
            motion.SetActive(phase == JobPhase.Running);
            var normalProgress = phase == JobPhase.Running && width >= _lastRenderedWidth;
            if (normalProgress) motion.SetTarget(width); else motion.SnapTo(width);
            _lastRenderedWidth = width;
        }

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            TotalProgressHost, $"迁移总进度 {percent:0.0}%");
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
        // ★ FIX BATCH 4（§7.1）★ 与底栏**同一个真值**（引擎的 ProgressTruthSnapshot）：页内不再有
        //   第二套算术，顶栏与底栏因此不可能再出现"两个百分比/两个字节数"。
        //   ★ 真机复验返修（2026-10-05 run2）★ 与底栏同一个 **PresentationTruth**（显示连续性 floor 托底）；
        //   LastTruth(raw) 只用于诊断：恢复后 raw 会先重建到 0，直接读它会让用户看到进度倒退。
        var truth = session.PresentationTruth;
        var percent = truth?.Percent ?? session.Percent;
        TotalPercentText.Text = $"{percent:0.0}%";
        TotalBytesText.Text = truth is null
            ? session.ProgressText
            : $"{Format.Bytes(truth.DisplayedTransferredBytes)} / {Format.Bytes(truth.PlannedBytes)}";
        _totalProgressPercent = double.IsNaN(percent) ? 0 : Math.Clamp(percent, 0, 100);
        UpdateTotalProgressFill();

        // ── 四张统计卡：传输速度 / 预计剩余 / 对象进度 / 已传-计划 ──
        SpeedValueText.Text = truth is null
            ? session.EngineSpeedText
            : (truth.SpeedBytesPerSecond > 0 ? Format.Speed(truth.SpeedBytesPerSecond) : "—");
        EtaValueText.Text = truth is null ? session.EtaText : Format.Eta(truth.EtaSeconds);
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