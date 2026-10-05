using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PCMig.Core.Models;
using PCMig.Core.Util;
using PCMig.WinUI.Controls.ImmersiveProgress;
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
    /// ★ Round-3 PHASE E（§7/§23）★ 旧「补间驱动」与"真值像素宽"字段已从生产路径移除：
    /// Step3 主进度条现在只有一个视觉拥有者 = <c>TotalImmersiveProgress</c>（PCMig Immersive Transfer Progress）。
    /// 平滑不再由"每来一个目标就起一段 0.2~1.2 s 补间"完成，而由呈现协调器的**连续指数滤波**完成
    /// （见 Presentation\ProgressPresentationCoordinator.cs，PMML-R24）；控件只负责按给定值渲染几何 + 光学层。
    /// 旧驱动类文件冻结保留（底栏仍用其轻量补间），**本页对它的引用数为零**（由 UI05 契约锁定）。
    /// </summary>

    /// <summary>★ PHASE C-1 探针 ★ 调试覆盖层刷新定时器（仅 PCMIG_PROGRESS_DEBUG=1 时存在）。</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _debugTimer;

    /// <summary>
    /// ★ Round-2 2026-10-05（§3.3）★ 呈现节拍（80 ms = 12.5 Hz）：从 VM 的**共享呈现时间线**
    /// 取一个视觉百分比，用它同时驱动大号百分比文本与蓝色进度条。
    /// 为什么要一个独立节拍：真值从引擎来是**离散**的（几秒一个台阶），而"数字立刻跳到新真值、条慢慢追"
    /// 会让人眼看到明显的不同步（用户视频 52.0 s / 54.9 s 的两次大跳）。数字与条读同一个视觉值即可消除。
    /// </summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _presentTimer;

    /// <summary>
    /// 呈现节拍间隔（毫秒）。★ Round-3 PHASE B（§15）★ 由 80 ms（12.5 Hz）改为 **16 ms（≈60 Hz 渲染帧）**：
    /// 视觉推进现在是连续指数滤波器 <c>visual += (target-visual)*(1-exp(-k*dt))</c>，它必须**按渲染帧**求值；
    /// 按 12.5 Hz 求值会让每帧跨过 1/3 个时间步，视觉上重新变成"一格一格挪"。
    /// 本拍只做"取值 + 写文本 + 设条宽"（同一个 <c>VisualPercent</c>），不做任何分配。
    /// </summary>
    private const int PresentIntervalMs = 16;

    /// <summary>
    /// ★ Round-3 PHASE E（§7）★ 调试探针开关：环境变量 <c>PCMIG_PROGRESS_DEBUG=1</c>（或 <c>true</c>）。
    /// 为什么本页自己读、不再借用旧驱动类的静态开关：生产路径必须与**已冻结的旧驱动**
    /// 彻底脱钩（本页对旧驱动类的引用数为零，由 UI05 契约锁定），否则"旧视觉路线已下线"就只能靠嘴说。
    /// 只读环境变量，绝不改变任何生产行为。
    /// </summary>
    private static bool ProgressDebugEnabled { get; } = ResolveProgressDebugEnabled();

    private static bool ResolveProgressDebugEnabled()
    {
        try
        {
            var raw = Environment.GetEnvironmentVariable("PCMIG_PROGRESS_DEBUG");
            return string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public Step3ProgressPage()
    {
        InitializeComponent();
        // ★ Round-3 PHASE E（§23）★ 轨道宽度变化不再需要本页重算像素宽：ImmersiveTransferProgress 每帧按自己的
        //   ActualWidth 求几何（Metrics.ProgressWidth = Width × Progress01），窗口缩放自然跟随。
        //   写入者仍是 UpdateTotalProgressFill（唯一），它只写一个**百分比**，不再写像素。
        StartPresentationTimer();
        StartDebugProbe();
        TryExportStatCards();
    }

    /// <summary>
    /// ★ PHASE 5B 诊断专用（PCMIG_STATCARD_EXPORT=&lt;目录&gt;）★ 把四张统计卡的**真实渲染结果**导出为 PNG，
    /// 用于在屏幕捕获不可用的环境下取得"生产卡字形"的像素证据。
    /// 只读环境变量、只写文件，绝不改变任何生产行为；未设变量时立即返回。
    /// 为什么需要它：本机 SetForegroundWindow / CopyFromScreen / PrintWindow 三条路都拿不到 WinUI 3 窗口的
    /// 真实画面（详见 Themes\Typography.xaml 的 PHASE 5B 注释），而 RenderTargetBitmap 直接渲染 XAML 视觉树，
    /// 不受窗口遮挡、桌面合成与前台状态影响。
    /// </summary>
    private async void TryExportStatCards()
    {
        string? dir = null;
        try { dir = Environment.GetEnvironmentVariable("PCMIG_STATCARD_EXPORT"); } catch { }
        if (string.IsNullOrWhiteSpace(dir)) return;

        await Task.Delay(2500);
        var log = new System.Text.StringBuilder();
        try
        {
            log.AppendLine("dir=" + dir);
            log.AppendLine("statcard0=" + (StatCard0 is null ? "null" : "ok"));
        }
        catch { }
        foreach (var pair in new (FrameworkElement? el, string name)[]
                 {
                     (StatCardsGrid, "statcards-grid"),
                     (SpeedValueText, "value-speed"),
                     (EtaValueText, "value-eta"),
                     (ObjectValueText, "value-object"),
                     (BytesValueText, "value-bytes"),
                 })
        {
            try
            {
                if (pair.el is null) { log.AppendLine(pair.name + ": element-null"); continue; }
                await ExportElementAsync(pair.el, dir!, pair.name);
                log.AppendLine(pair.name + ": ok");
            }
            catch (Exception ex)
            {
                log.AppendLine(pair.name + ": EX " + ex.GetType().Name + " " + ex.Message);
            }
        }

        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir!, "export.log"), log.ToString());
        }
        catch { }
    }

    /// <summary>把一个元素渲染成 PNG（诊断专用；异常由调用方吞掉，绝不影响进度真值）。</summary>
    private static async Task ExportElementAsync(FrameworkElement element, string dir, string name)
    {
        var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await rtb.RenderAsync(element);
        var pixels = await rtb.GetPixelsAsync();
        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(dir);
        var file = await folder.CreateFileAsync(name + ".png", Windows.Storage.CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
            Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            (uint)rtb.PixelWidth,
            (uint)rtb.PixelHeight,
            96,
            96,
            System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixels));
        await encoder.FlushAsync();
    }

    // ★ Round-3 PHASE E（§10）★ 页面侧的 ResolveProgressVisualThickness() 已删除：视觉厚度现在完全由控件
    //   按 token PCMigImmersiveProgressThickness(12) 与端帽半径 PCMigImmersiveProgressRadius(6) 自持，
    //   页面不再有机会在同一根进度条上写第二个厚度数字（"一个进度条只能有一个视觉拥有者"，§7）。

    /// <summary>启动 16 ms 呈现节拍（失败绝不影响进度显示本身）。</summary>
    private void StartPresentationTimer()
    {
        try
        {
            var timer = DispatcherQueue?.CreateTimer();
            if (timer is null) return;
            timer.Interval = TimeSpan.FromMilliseconds(PresentIntervalMs);
            timer.IsRepeating = true;
            timer.Tick += (_, _) => AdvancePresentationTick();
            timer.Start();
            _presentTimer = timer;
        }
        catch
        {
            // 呈现节拍起不来时，PushState 仍会按真值兜底写数字与条（显示正确性优先于平滑度）。
        }
    }

    /// <summary>
    /// ★ Round-2 2026-10-05（§3.3）★ 呈现节拍：推进共享呈现时间线并**同时**刷新大号百分比、字节与进度条。
    /// 语义边界：视觉值只会**落后**于已确认显示真值、永不超出（协调器内部强制），也不会在真值不动时自爬；
    ///   暂停 / 停止 / 中断 / 完成时协调器已冻结或落位，本方法照实渲染，不制造任何"假进度"。
    /// </summary>
    private void AdvancePresentationTick()
    {
        var session = _session;
        if (session is null) return;

        double visual;
        try { visual = session.AdvancePresentation(DateTime.UtcNow); }
        catch { return; }

        var planned = session.PresentationTruth?.PlannedBytes ?? 0L;
        TotalPercentText.Text = $"{visual:0.0}%";
        if (planned > 0)
        {
            // §3.3 选 A：主进度面上的字节数与百分比**同源**，不出现"数字已到、字节还在旧值"的错位。
            TotalBytesText.Text = $"{Format.Bytes(session.PresentationVisualBytes)} / {Format.Bytes(planned)}";
        }
        else if (string.IsNullOrEmpty(TotalBytesText.Text))
        {
            TotalBytesText.Text = session.ProgressText;
        }

        _totalProgressPercent = double.IsNaN(visual) ? 0 : Math.Clamp(visual, 0, 100);
        UpdateTotalProgressFill();
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
        if (!ProgressDebugEnabled) return;
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
        if (!ProgressDebugEnabled) return;

        try
        {
            // ★ Round-3 PHASE E ★ 旧 PMD 诊断行已下线：本页不再引用旧驱动，
            //   进度链路的自证改由下面 headX / fillW / bandX / state 四个真实渲染量承担。
            var motion = "PMD (removed in PHASE E)";
            var session = _session;
            var raw = session?.LastTruth;
            var shown = session?.PresentationTruth;
            ProgressDebugText.Text =
                $"{motion}\n"
                + $"phase={session?.Phase.ToString() ?? "-"} uiPercent={_totalProgressPercent:0.0}\n"
                + $"raw: pct={raw?.Percent:0.0} displayed={raw?.DisplayedTransferredBytes ?? 0} committed={raw?.CommittedBytes ?? 0} "
                + $"inFlight={raw?.InFlightConfirmedBytes ?? 0} src={raw?.InFlightSource.ToString() ?? "-"} retry={raw?.RetryState.ToString() ?? "-"}\n"
                + $"display: pct={shown?.Percent:0.0} displayed={shown?.DisplayedTransferredBytes ?? 0} "
                + $"continuation={session?.ContinuationDisplayMode.ToString() ?? "-"} active={session?.ContinuationActive ?? false} highWaterBytes={session?.ContinuationHighWaterBytes ?? 0}\n"
                + $"visual: pct={session?.PresentationVisualPercent ?? 0:0.0} bytes={session?.PresentationVisualBytes ?? 0} lag={session?.PresentationLagPercent ?? 0:0.00} lagging={session?.PresentationIsLagging ?? false}\n"
                + $"timeline: {session?.PresentationDescribe ?? "-"}\n"
                + $"track={TotalProgressHost.ActualWidth:0.0} headX={TotalImmersiveProgress?.HeadX ?? 0:0.0} "
                + $"fillW={TotalImmersiveProgress?.ProgressWidth ?? 0:0.0} bandX={TotalImmersiveProgress?.BandCenterX ?? 0:0.0} "
                + $"state={TotalImmersiveProgress?.ProgressState.ToString() ?? "-"} localNow={DateTime.Now:HH:mm:ss.fff}";
        }
        catch
        {
            // 同上。
        }
    }

    /// <summary>
    /// ★ Round-3 PHASE E（§16/§23、PMML-R23）★ 主进度条的**唯一写入者**：把同一个 <c>VisualProgress</c>
    /// （左侧大号百分比与字节数用的就是它）交给 <c>TotalImmersiveProgress</c>，并在同一处映射任务状态。
    ///
    /// 为什么改成"只写一个百分比"：旧写法在这里算像素宽、再喂给旧补间驱动起一段补间，
    /// 于是"数字一个源、条另一个源"，并制造出用户视频里"一段走完、停一下、再走一段"的速度断点（§15）。
    /// 现在几何由控件按自身宽度求（<c>Metrics.ProgressWidth = Width × Progress01</c>），
    /// 平滑由协调器的连续指数滤波负责，本方法**不做任何动画决策**。
    ///
    /// 装饰层（Push Band / 粒子 / Ripple / Halo）不属于进度真值：状态一到 Paused/Interrupted/Failed/Completed
    /// 就把它们收干净（§19），但**绝不回写 Value**——Head 永远只表示"已确认的迁移事实"（§8、PMML-R21）。
    /// </summary>
    private void UpdateTotalProgressFill()
    {
        var percent = Math.Clamp(double.IsNaN(_totalProgressPercent) ? 0 : _totalProgressPercent, 0, 100);

        var immersive = TotalImmersiveProgress;
        if (immersive is not null)
        {
            immersive.Value = percent;
            immersive.ProgressState = MapProgressState(_session?.Phase ?? JobPhase.Created);
        }
    }

    /// <summary>
    /// ★ Round-3 PHASE E（§19 状态机）★ Core 任务阶段 → 控件视觉状态。这是"事实 → 表现"的单向映射：
    /// 控件永远不反过来决定业务阶段（PMML-R28）。Holding（Head 静止但活动继续）由 §18 的语义单列，
    /// 本页只在阶段确实是 Running 时给 Running；真值长时间不动时 PresentationTruth 自己会停住，
    /// Push Band 仍会继续跑（§18 Progress Head = Fact / Push Band = Activity）。
    /// </summary>
    private static ImmersiveProgressState MapProgressState(JobPhase phase) => phase switch
    {
        JobPhase.Running => ImmersiveProgressState.Running,
        JobPhase.Paused => ImmersiveProgressState.Paused,
        JobPhase.Interrupted => ImmersiveProgressState.Interrupted,
        JobPhase.Verifying => ImmersiveProgressState.Verifying,
        JobPhase.Completed or JobPhase.CompletedWithErrors => ImmersiveProgressState.Completed,
        JobPhase.Failed => ImmersiveProgressState.Failed,
        // 用户主动取消：已完成的事实仍然有效，但不该再用"正在搬运"的动效暗示后台还在干活。
        JobPhase.Canceled => ImmersiveProgressState.Warning,
        // Created / Preflight / Scanning / Planned / AwaitingReview：还没有任何可确认的传输事实。
        _ => ImmersiveProgressState.Preparing,
    };

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
        // ★ Round-2（§3.3）★ 大号百分比 / 字节 / 进度条都归**呈现节拍**（AdvancePresentationTick，80 ms）驱动；
        //   这里不再直接写 —— 否则真值一到就把数字跳到最前，而条还在补间，用户看到的就是"数字先跳、条后追"。
        //   只有呈现节拍起不来（DispatcherQueue 不可用）时按真值兜底，保证任何情况下数字都与真值一致。
        if (_presentTimer is null)
        {
            TotalPercentText.Text = $"{percent:0.0}%";
            TotalBytesText.Text = truth is null
                ? session.ProgressText
                : $"{Format.Bytes(truth.DisplayedTransferredBytes)} / {Format.Bytes(truth.PlannedBytes)}";
            _totalProgressPercent = double.IsNaN(percent) ? 0 : Math.Clamp(percent, 0, 100);
            UpdateTotalProgressFill();
        }

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