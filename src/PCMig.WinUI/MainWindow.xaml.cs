using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using PCMig.WinUI.Diagnostics;
using PCMig.Diagnostics.Abstractions;
using PCMig.WinUI.Presentation;
using Windows.Graphics;

namespace PCMig.WinUI;

public sealed partial class MainWindow : Window
{
    /// <summary>Canonical 16:10 验收视口的 DIP 尺寸（XAML 客户端空间，与设计参考图同比例）。</summary>
    private const int CanonicalClientWidthDip = 1424;
    private const int CanonicalClientHeightDip = 891;

    /// <summary>
    /// 最小追踪尺寸（DIP，客户区口径），由 WM_GETMINMAXINFO 拦截。
    ///   · 非 Uniform 模式：沿用原 960×640（逐位不变）；
    ///   · UniformScaleHost 模式：= Canonical 客户区 × <see cref="UniformScaleHost.MinimumScale"/>，
    ///     即"整体缩放到光学下限"对应的窗口尺寸 —— 这正是产品口径要求的实现方式：
    ///     Scale 到达 Minimum 后**整个应用停止继续缩小**，由窗口最小尺寸挡住，而不是压字/折行。
    ///     同一份常量派生，避免两处数值漂移。
    /// </summary>
    private static readonly double MinimumClientWidthDip = UniformScaleHost.IsRequested
        ? UniformScaleHost.DesignWidth * UniformScaleHost.MinimumScale
        : 960.0;

    private static readonly double MinimumClientHeightDip = UniformScaleHost.IsRequested
        ? UniformScaleHost.DesignHeight * UniformScaleHost.MinimumScale
        : 640.0;

    private bool _viewportCalibrated;

    /// <summary>关闭收尾只执行一次（AppWindow.Closing 可能被多次触发）。</summary>
    private bool _shuttingDown;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public ConnectionViewModel ViewModel { get; } = new();

    /// <summary>
    /// 迁移会话的**唯一状态源**（Step 2/3/4 的全部业务状态：计划、线程数、进度、验证、报告）。
    /// Step 1 的连接/共享仍由 <see cref="ViewModel"/> 负责；本对象只投影它的 Host/Username/IsConnected，
    /// 不重造第二条连接链路。Core 的迁移引擎（Preflight/Scan/Planner/Orchestrator/Verifier/Report）
    /// 只经由它调用。
    /// </summary>
    public MigrationSessionViewModel Session { get; }

    /// <summary>
    /// 四步导航的唯一状态源（Shell 层）：侧栏、ContentHost 页面可见性、上一步/下一步按钮都读这一份。
    /// **导航可用性与业务动作可用性分离**：任何 Step 都允许被打开（哪怕尚未连接 / 未选数据），
    /// 页面内部用 Empty / Not Ready / Waiting 表达"还不能做那件事"。
    /// </summary>
    public StepNavigation Nav { get; } = new();

    /// <summary>页面就绪状态（薄适配层）：把既有的连接状态与迁移会话状态投影成四页共用的状态文案。</summary>
    public PageReadiness Readiness { get; }

    public MainWindow()
    {
        // 适配层必须在 InitializeComponent 之前构造：后面装配页面时要立刻注入它，
        // 否则传 null 会在页面的 ApplyState 里抛 NullReferenceException（实测踩过）。
        // 会话状态源同样必须先于适配层：Step3/Step4 的文案由它的真实状态决定（不再有假数据）。
        Session = new MigrationSessionViewModel(ViewModel);

        // ★ A.5（P1-5/D2）★ 节拍薄泵的生产装配点（唯一）。
        //   为什么在装配层注入而不是在 VM 里直接 new：MigrationSessionViewModel.cs 被**链入**
        //   tests\PCMig.Core.Tests 编译，而该测试项目没有 DispatcherQueueTimer 替身
        //   （见 TestOnlyDispatcherQueueShim.cs）⇒ 被链入的文件不得出现该类型名。
        //   真实泵 DispatcherQueueUiFlushPump 住在**不链入**的 Presentation\UiFlushPump.cs，
        //   由这里（不链入的 MainWindow）把它注入进去。注入的是窗口级常量装配，不是会话状态。
        MigrationSessionViewModel.ProductionFlushPumpFactory = onTick =>
        {
            // 必须用**类型名**限定：本类是 Window，实例属性 DispatcherQueue 会遮蔽同名的静态类型。
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (queue is null) throw new InvalidOperationException("MainWindow 构造应在 UI 线程（拿不到 DispatcherQueue）。");
            return new DispatcherQueueUiFlushPump(queue, onTick);
        };

        // ★ A.5（P2-6）★ 「未完成任务探测」去抖计时器的生产装配点（唯一），与上面同一个理由：
        //   被链入测试的 MigrationSessionViewModel.cs 不得出现 WinUI 定时器类型名，真实泵住在
        //   **不链入**的 Presentation\UiProbeDebouncePump.cs，由这里注入。
        //   停止走的是同一个 Session.StopUiRefresh() 出口（ShutdownAndExit 里已接线），本文件不需要再加一行。
        MigrationSessionViewModel.ProductionProbeDebouncePumpFactory = onElapsed =>
        {
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (queue is null) throw new InvalidOperationException("MainWindow 构造应在 UI 线程（拿不到 DispatcherQueue）。");
            return new DispatcherQueueProbeDebouncePump(queue, onElapsed);
        };
        Readiness = new PageReadiness(ViewModel, Session);
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        // A21：拖动区改为**独立的细标题栏**（左侧有品牌内容），不再是 Product Header —— 这样系统
        // Caption Buttons 落在标题栏细条内，不再压在 Product Header 上；我们不自绘 — □ ×。
        SetTitleBar(TitleBarRoot);
        ApplyTitleBarInset();
        // 必须先订阅再改尺寸：ResizeClient 触发的首次布局可能是同步完成的，
        // 若订阅晚于它，视口自校正事件将永不触发（实测踩到过）。
        if (Content is FrameworkElement root) root.SizeChanged += OnRootSizeChanged;
        ApplyCanonicalClientSize();
        // Win32 最小追踪尺寸：WinUI 3 未公开 min-size API，这里在窗口 HWND 上挂 WM_GETMINMAXINFO 子类过程。
        // 必须在窗口句柄可用后安装；纯窗口行为，不接触业务/导航/绑定。
        WindowMinimumSize.Install(
            WinRT.Interop.WindowNative.GetWindowHandle(this),
            MinimumClientWidthDip,
            MinimumClientHeightDip,
            CurrentScale());

        // Motion 防重入（IsTransitioning）+ 方向性入场（§26/§31）：
        //   Changing —— 可见性变化之前：算方向、清掉可能残留的位移（防闪帧）；
        //   Changed  —— 目标页已可见之后：交给 PageTransitionCoordinator 播放 Vertical Cross-Slide。
        // 导航状态一律照常切换，被抑制的只是重入的那一次动画本身。
        // U57：整页纵向 Push 的过渡容器 = PageViewport（Workspace 内容视口）。
        // Clip 必须按实际尺寸维护：整页位移时页面会被移出容器，没有 Clip 就会盖住
        // Header / Sidebar / BottomBar（用户要求滑动严格限制在 Workspace 内容区内）。
        _pageTransition = new PageTransitionCoordinator(PageForStep, () => PageViewport?.ActualHeight ?? 0.0);
        // ★ U62：Utility Panel 的 Fluid Zoom 协调器（几何全在 Canonical 应用根坐标系内计算）。
        //   用 lambda 延迟取 ApplicationRoot：UniformScaleHost.Install 在本构造函数最末才重挂，
        //   而首次真正用到它是在用户点击入口之后，届时 ApplicationRoot 已是 DesignSurface 内的真实根。
        _fluidZoom = new FluidZoomTransitionCoordinator(() => ApplicationRoot);
        if (PageViewport is not null)
        {
            PageViewport.SizeChanged += (_, _) => UpdatePageViewportClip();
            UpdatePageViewportClip();
        }
        Nav.Changing += OnNavChanging;
        Nav.Changed += OnNavChanged;

        // ── Layer 0/1：Desktop Acrylic Technical Spike（env 驱动；不设环境变量则完全不动）──
        // 只接管窗口最底层 Backdrop，不碰 Card/Input/Button/页面布局；详见 Presentation\BackdropSpike.cs
        BackdropSpike.Install(this);

        // ── Developer Visual Tuning（开发者材质实时调节器）──────────────────────────
        // 视觉标定工具，不是业务功能：把写死的材质参数变成可实时拖动的参数。
        // 只改已存在画刷的 alpha 与 Backdrop 控制器参数，不重构任何既有 UI/业务。
        // 加载顺序必须在 BackdropSpike.Install 之后 —— 否则 _controller 还没建立，Backdrop 参数写不进去。
        DeveloperVisualTuning.Current.LoadIfExists();
        TuningPanel.Attach(DeveloperVisualTuning.Current);
        // §29：关闭按钮也必须走"动画结束后再折叠"，不能硬切（原先这里是直接 Collapsed）。
        // 复用 ClosePanel —— 它与 TogglePanel 的关闭分支是同一条已被验证的路径，不新增动效实现。
        TuningPanel.CloseRequested += (_, _) => ClosePanel(TuningPanel);
        ChangelogPanel.CloseRequested += (_, _) => ClosePanel(ChangelogPanel);

        // ── 诊断中心（工具面；观察式自诊断）──────────────────────────────────────
        // 数据源是**只读**适配器：运行时不存在时界面显示"尚未采集"，绝不让 Shell 起不来。
        DiagnosticCenterPanel.CloseRequested += (_, _) => ClosePanel(DiagnosticCenterPanel);
        DiagnosticCenterPanel.DeepTraceToggled += (_, enabled) => SetDeepTraceInputObservation(enabled);
        _diagnosticCenterVm = new DiagnosticCenterViewModel(new RuntimeDiagnosticCenterSource())
        {
            ExportHandler = ExportDiagnosticPackageAsync,
        };

        // ── Shell 装配（唯一入口；之后不再把页面逻辑散落回 MainWindow） ──────────────
        // 1) 侧栏导航与 ContentHost 共享同一份 Nav
        StepNav.Nav = Nav;
        StepNav.StepSelected += (_, kind) => Nav.GoTo(kind);
        // 2) Step 1 页注入 Shell 的共享 ViewModel（唯一实例；页面自己不新建业务状态）
        PageConnect.Vm = ViewModel;
        // 页面就绪状态适配层：由 Shell 创建唯一实例并注入四页（薄投影，不含业务规则）。
        // 用 ApplyState（挂 DataContext）而不是属性注入 + x:Bind：嵌套路径的 x:Bind 在
        // UserControl 上不会随后置注入重算，实测渲染为空；经典 Binding 注入即生效。
        PageSelectData.ApplyState(Readiness);
        // 阶段 A 包 2：Step2 的计划生成链需要会话状态源、连接状态源（取“已勾选的共享”）
        // 与口令来源（Step 1 的口令框）——三者都是 Shell 注入，页面自己不新建业务状态。
        PageSelectData.AttachSession(Session);
        PageSelectData.AttachConnection(ViewModel);
        PageSelectData.AttachPasswordProvider(() => PageConnect.CurrentPassword);
        // 导航状态源：Step2 页面在 Shell 构造期就被注入状态（那时目录树还是空的），
        // 必须在每次切到该页时重新推一次真实状态，否则会出现“树已就绪却仍显示空状态”。
        PageSelectData.AttachNavigation(Nav);
        PageProgress.ApplyState(Readiness);
        PageResult.ApplyState(Readiness);
        // 阶段 A 包 4：Step4 的「结果与校验」链与 Step2/3 共用同一个会话状态源（Shell 注入，页面不新建业务状态）：
        //   · 结果清单 / 实时日志 / 六个动作按钮的可用性全部读 MigrationSessionViewModel；
        //   · 口令来源仍是 Step 1 的口令框（修复与恢复要凭据续连旧电脑），口令只作参数传递，不落字段、不写存档。
        PageResult.AttachSession(Session);
        PageResult.AttachNavigation(Nav);
        PageResult.AttachPasswordProvider(() => PageConnect.CurrentPassword);
        // 阶段 A 包 3：Step3 的进度位与「共用底栏」共用同一个会话状态源（Shell 注入，页面不新建业务状态）。
        //   · PageProgress：补 x:Name 后的进度位 + 对象清单 + 文件流，全部读 MigrationSessionViewModel；
        //   · 底栏（BottomBar）：四页常驻的进度位与四个动作按钮同样读它，这里只做一次装配。
        PageProgress.AttachSession(Session);
        PageProgress.AttachNavigation(Nav);
        AttachFooterToSession();
        // 3) A29（用户人工标注项）：原先这里会在导航变化时刷新浮层区右下角的
        //    「上一步 / 下一步」按钮。用户标注原话：
        //      「这（上一步，下一步）这两个按钮在四个功能板块中都很多余 可以去除，
        //        这样旁边的空间才可以更高效的利用」。
        //    该按钮组已按 A29 从浮层区删除，因此这里没有需要刷新的目标。
        //    A50（本会话）起改为在 WorkspaceShell 内以统一的 Navigation Footer 形式恢复：
        //      MainWindow.xaml 的 WorkspaceShell 新增第 5 行 Auto 承载右下角 Footer，
        //      显示/隐藏与文案由 x:Bind Nav.IsXxxCurrent 声明式决定，Click 走既有 Nav.GoTo；
        //      因此**依然不需要**在这里做任何命令式刷新。
        //    导航能力没有减少：左侧栏四张卡（始终可点）与 Ctrl+1..4 / Ctrl+Tab 键盘路径均保留。

        // 4) 键盘导航（Presentation 级）：Ctrl+Tab 向下一步、Ctrl+Shift+Tab 向上一步、
        //    Ctrl+1..4 直达。放在 Shell 而不是各页面，保证四页行为一致。
        if (Content is UIElement shellRoot)
        {
            shellRoot.IsTabStop = true;
        }
        // 5) ProductHeader 的源状态胶囊属于 Shell 层（四个 Step 共用同一条链路显示），
        //    因此这段逻辑留在 MainWindow，而不是塞进 Step 1 页面。
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ConnectionViewModel.IsConnected) or nameof(ConnectionViewModel.Host))
                UpdateSourceStatus();
        };
        UpdateSourceStatus();

        // 【QA 探针接入点】仅在环境变量 PCMIG_STEP2_TREE_QA=<真实目录绝对路径> 时启用：
        // 把该目录挂成 Step2 目录树的根、走真实懒加载（EnsureChildrenAsync 真读盘）、
        // 驱动 UI 真正展开并写取证日志。未设该变量 ⇒ 本方法立刻返回，生产路径逐位不变。
        // 用于证明“TreeView 能真实展开出子目录/文件”，详见 Presentation\Step2TreeQaProbe.cs。
        InstallStep2TreeQaProbeIfRequested();

        // ★ 阶段 A 包 4（用户要求）：应用重新启动时必须**主动调用真实的** JobManager.FindUnfinished()
        //   （经会话的 FindUnfinishedOnStartupAsync）；但**绝不自动恢复**——
        //   探测只产生「检测到未完成任务」的真实提示（Step 4 状态行 + 实时日志），
        //   恢复必须由用户在 Step 4 点「恢复任务」明确确认。
        //   启动时若尚未连接旧电脑，会话如实回报「无法按源匹配」（≠「没有未完成任务」）；
        //   连接成功（源电脑名可用）后再探测一次。
        Session.PropertyChanged += OnSessionChangedForUnfinishedProbe;
        if (Content is FrameworkElement probeRoot)
        {
            probeRoot.Loaded += async (_, _) => await ProbeUnfinishedAsync();
        }

        // 【P0 关闭崩溃】关闭路由：先释放 thread-pool 支撑的 WinRT 定时器与 ViewModel 会话，
        // 再由本进程主动结束，绕开 Windows App SDK 的卸载期缺陷。
        //
        // 依据（转储实测，7/7 转储指纹一致）：
        //   异常码 0xC0000005、读 NULL、失败地址 0x58、
        //   故障模块 Microsoft.UI.Xaml.dll **固定偏移 +0x7F9880**（落在其 .text 段内）。
        //   关闭期该 DLL 唯一会执行的自有代码就是它自己的 DllMain ⇒ 即已知缺陷族的
        //   DllMain(DLL_PROCESS_DETACH) → DeinitializeDll → ThreadPoolService::ReleaseFactories
        //   路径在释放已被卸载的 threadpoolwinrt.dll 中的线程池工厂。
        //   详见 docs\技术发现-20260927-关闭崩溃转储级定位.md。
        //
        // 为什么用 AppWindow.Closing 而不是 Window.Closed：
        //   Closed 触发时 Window.Close() 已开始拆卸 XAML，时机偏晚；
        //   Closing 更早，取消默认关闭后本进程主动 Exit，可避开 LdrShutdownProcess 的 Xaml 卸载。
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;      // 不交给框架的关闭流程
            ShutdownAndExit();
        };

        // ── Whole-App Uniform Scaling 宿主（P0 修复后**生产默认启用**；PCMIG_UNIFORM_HOST=0 回退旧响应式）──
        // 放在构造函数**最末**：此前所有现有逻辑（自定义标题栏、视口自校正订阅、浮层装配）
        // 依旧跑在原始 window.Content 上，重挂只发生在最后一步 ⇒ 未设环境变量时生产路径逐位不变。
        // 结构：Window → Viewbox(Stretch=Uniform) → DesignSurface 1424×892 → 原根。
        // 详见 Presentation\UniformScaleHost.cs
        UniformScaleHost.Install(this);

        // ── U64：Hover Lift / Pressed Sink（恢复历史交互反馈）──────────────────────────
        // 在**应用根**上挂一次路由指针事件即覆盖全部按钮与侧栏 Step Card（含 DataTemplate 内实例），
        // 因此不需要改任何 ControlTemplate / 几何 / 材质。必须在 UniformScaleHost 重挂之后取
        // ApplicationRoot，否则会挂到已被替换掉的旧根上。
        // 位移单位是 Canonical DIP，由 Viewbox 统一缩放 ⇒ 内部绝不再乘 ApplicationUIScale（避免双倍）。
        InteractionFeedback.Install(ApplicationRoot);
        // 用户口径（2026-09-28）：标题栏这两个工具入口**不要** Hover Lift / Pressed Sink
        //   · DeveloperTuningButton —— 开发者材质调节（齿轮图标）
        //   · ChangelogButton        —— 更新日志（v0.5.0 徽章）
        // 其余按钮（含侧栏 Step Card、页面动作按钮、底栏四动作、Utility Panel 内按钮）保留该效果。
        InteractionFeedback.Exclude(DeveloperTuningButton);
        InteractionFeedback.Exclude(ChangelogButton);
    }

    /// <summary>
    /// QA 探针的 Shell 侧接入：**只有** 环境变量 <c>PCMIG_STEP2_TREE_QA=&lt;真实目录&gt;</c> 存在时才做事。
    /// 作用：把该目录挂成 Step2 目录树根 → 走真实懒加载 → 切到 Step2 → 驱动 UI 展开 → 写取证日志。
    /// 这样就能在没有旧电脑/SMB 的条件下，用**真实磁盘**证明“目录树层级真的渲染出来了、三态勾选可见”。
    /// 不设该环境变量时本方法立即返回：生产启动路径逐位不变。
    /// </summary>
    private void InstallStep2TreeQaProbeIfRequested()
    {
        if (string.IsNullOrWhiteSpace(Step2TreeQaProbe.RequestedPath)) return;
        // 等窗口内容真正加载完再跑，避免在 XAML 尚未完成布局时触碰树控件。
        if (Content is FrameworkElement root)
        {
            root.Loaded += async (_, _) =>
            {
                var node = await Step2TreeQaProbe.RunAsync(Session);
                if (node is null) return;
                Nav.GoTo(StepKind.SelectData);
                PageSelectData.ExpandAllRootsForVerification();
            };
        }
    }

    /// <summary>
    /// 启动时的一次性「未完成任务」主动探测：**真实调用** Core 的 JobManager.FindUnfinished
    /// （经会话的 MigrationSessionViewModel.FindUnfinishedOnStartupAsync），只提示、**不自动恢复**。
    /// 探测失败绝不影响 Shell：异常只记诊断，不弹窗、不改业务状态。
    ///
    /// ★ A.5（P2-6）★ 本方法现在**只服务**这一个一次性语义：属性变化触发的重复探测已改为
    /// 同步的 <c>Session.RequestUnfinishedProbe()</c>（去抖 400ms + 连接门 + 世代门都在 Session 里）。
    /// 手工 <c>PageResult.RefreshFromSession()</c> 也已删除：<c>PendingResumeCandidate</c> 现在自己会
    /// <c>Raise</c>（Step4 已订阅会话的 PropertyChanged ⇒ 按钮态自动刷新），不再依赖 Shell 的一次性补刷。
    /// </summary>
    private async System.Threading.Tasks.Task ProbeUnfinishedAsync()
    {
        try
        {
            await Session.FindUnfinishedOnStartupAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"未完成任务探测失败：{ex}");
        }
    }

    /// <summary>
    /// 会话的源电脑名 / 连接状态变化时**请求**重新探测未完成任务（真实探测仍不自动恢复）。
    ///
    /// ★ A.5（P2-6）★ 改前这里是 <c>async void</c> + <c>await ProbeUnfinishedAsync()</c>：
    /// 用户每敲一个 Host 字符就可能跑一次真实的全量任务目录扫描（<c>JobManager.FindUnfinished</c>），
    /// 且旧探测的结果会覆盖新状态（Step4「恢复任务」因此可能指向错任务）。
    /// 现在处理体是**同步、不抛**的一行：去抖（400ms）+ 连接门（未连接不做 IO）+
    /// 世代门（旧结果不得覆盖新状态）全在 Session 内部 —— 门控必须与"写状态"同处一地，
    /// 放在 Shell 只能挡住发起、挡不住在途返回。
    /// </summary>
    private void OnSessionChangedForUnfinishedProbe(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MigrationSessionViewModel.Host) or nameof(MigrationSessionViewModel.IsSourceConnected))) return;
        Session.RequestUnfinishedProbe();
    }

    /// <summary>
    /// 关闭收尾：释放 UI 层持有物，然后立即结束进程。
    ///
    /// 只做释放与退出，不动任何业务状态、不写存档、不改交互语义；可重复调用。
    /// </summary>
    private void ShutdownAndExit()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;

        // U62：拆除可能仍在屏幕上的 Morph Shell / Overlay（不改变任何业务状态）。
        try { _fluidZoom.Abort(); } catch { /* 关闭期异常不得外溢 */ }

        // ★ A.5（D2）★ **必须在 Environment.Exit(0) 之前**停掉会话的 UI 刷新节拍（DispatcherQueueTimer）：
        //   它是 thread-pool 支撑的 WinRT 对象，进程退出时 Windows App SDK 会在
        //   Microsoft.UI.Xaml.dll 的 DllMain(DLL_PROCESS_DETACH) → DeinitializeDll →
        //   ThreadPoolService::ReleaseFactories 路径上释放其缓存的线程池激活工厂；
        //   若对象仍被持有会命中已卸载模块而崩溃（实测 Microsoft.UI.Xaml.dll+0x7F9880 读 NULL +0x58，
        //   7/7 转储指纹一致 —— 与 MotionState.Dispose 同一族机理，见本文件上方的关闭路由注释）。
        //   放在 MotionState / ViewModel 释放之前：先掐掉"还会产生新 UI 写入"的源头。
        try { Session.StopUiRefresh(); } catch { /* 关闭期异常不得外溢 */ }

        // ★ D6.1 §17 ★ 关窗也要停掉诊断面板的刷新节拍（它可能仍开着）——
        //   必须排在 StopUiRefresh 之后、诊断收尾之前：先不再产生新的 UI 读取，再封段。
        try { StopPanelRefresh(DiagnosticCenterPanel); } catch { /* 关闭期异常不得外溢 */ }

        try { MotionState.Current.Dispose(); } catch { /* 关闭期异常不得外溢 */ }
        try { ViewModel.Dispose(); } catch { /* 同上 */ }

        // ★ 诊断收尾（方案 §21）★ **有界**排空 + 封段 + 写 clean marker：
        //   必须排在 StopUiRefresh 之后（不再产生新的 UI 事件）且在 Environment.Exit(0) 之前；
        //   预算到期就如实记 ShutdownIncomplete，绝不假装"诊断已全部保存"，也绝不无限等待。
        //   先注销 Deep Trace 的输入观测（若用户在开着的时候直接关窗）。
        try { _deepTraceObserver?.Stop(); } catch { /* 注销失败不得影响退出 */ }
        try { DiagnosticBootstrap.Shutdown(); } catch { /* 诊断收尾失败不得影响退出 */ }

        // 绕开 LdrShutdownProcess → Microsoft.UI.Xaml.dll!DllMain → ThreadPoolService::ReleaseFactories
        // 这条会命中已卸载模块的路径（框架缺陷，应用侧只能规避）。
        Environment.Exit(0);
    }

    // ── 键盘导航（KeyboardAccelerator 回调；与侧栏点击、上一步/下一步共用同一个 Nav） ──
    private void GoStep1_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { Nav.GoTo(StepKind.Connect); args.Handled = true; }
    private void GoStep2_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { Nav.GoTo(StepKind.SelectData); args.Handled = true; }
    private void GoStep3_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { Nav.GoTo(StepKind.Progress); args.Handled = true; }
    private void GoStep4_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { Nav.GoTo(StepKind.Result); args.Handled = true; }
    private void GoNextStep_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        Nav.GoTo((StepKind)Math.Min((int)StepKind.Result, (int)Nav.Current + 1));
        args.Handled = true;
    }
    private void GoPrevStep_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        Nav.GoTo((StepKind)Math.Max((int)StepKind.Connect, (int)Nav.Current - 1));
        args.Handled = true;
    }

    // ── Shell 级页面导航 Footer（工作区右下角的「上一步 / 下一步」）────────────────────
    // 与上面的键盘导航**共用同一个 Nav、同一套边界算术**（Connect 为下界 / Result 为上界），
    // 因此没有第二条流程：显示与文案全部由 XAML 的 x:Bind Nav.IsXxxCurrent 决定。
    private void NavNext_Click(object sender, RoutedEventArgs e) =>
        Nav.GoTo((StepKind)Math.Min((int)StepKind.Result, (int)Nav.Current + 1));

    private void NavPrevious_Click(object sender, RoutedEventArgs e) =>
        Nav.GoTo((StepKind)Math.Max((int)StepKind.Connect, (int)Nav.Current - 1));

    // ── 共用底栏（BottomBar）：真实进度位 + 四个动作按钮 ─────────────────────────────
    // 底栏四页常驻（XAML 注释：Step 1 为 Disabled 视觉态，不得删除）。阶段 A 包 3 把它接回真实状态：
    //   · 进度位（百分比 / 已传-计划 / 进度条 / 两组速率）＝ MigrationSessionViewModel 的显示位；
    //   · 四个动作按钮 ＝ 同一个会话的 CanStart / CanPause / CanStop / CanResume 与
    //     RunAsync / PauseAsync / StopAsync / ResumeAsync（VM 内已封装 Core 的
    //     JobContext.RequestPause / ClearPauseRequest / 取消令牌），这里**不发明第二套状态**；
    //   · 口令来源＝Step 1 的口令框（与 Step2 的 _passwordProvider 同一来源；不落任何字段、不写存档）；
    //   · Step 3 页**不加按钮**（零按钮是冻结布局），暂停/停止/恢复一律由这三个常驻按钮承担。
    private void AttachFooterToSession()
    {
        Session.PropertyChanged += (_, _) => PushFooter();
        PushFooter();
    }

    /// <summary>把会话的真实状态推到共用底栏（唯一入口）。</summary>
    private void PushFooter()
    {
        // 线程纪律硬防线：非 UI 线程一律排队到 UI 线程，绝不在后台线程写控件。
        var queue = DispatcherQueue;
        if (queue is not null && !queue.HasThreadAccess) { queue.TryEnqueue(PushFooter); return; }

        var percent = Session.Percent;
        FooterPercentText.Text = $"{percent:0.0}%";
        FooterBytesText.Text = Session.ProgressText;
        FooterProgressBar.Value = double.IsNaN(percent) ? 0 : Math.Clamp(percent, 0, 100);
        FooterSpeedText.Text = Session.EngineSpeedText;
        FooterEtaText.Text = Session.EtaText;

        // 四态在底栏的呈现（可用性矩阵，全部来自会话的真实布尔量，不自行推断）：
        //   运行中（Resume 后正在传）：开始✗ 暂停✓ 停止✓ 恢复✗
        //   已请求暂停（Pause 合作式中）：开始✗ 暂停✗ 停止✓ 恢复✗（IsPaused ⇒ CanPause=false，仍在跑）
        //   已暂停 Paused（可续传）    ：开始✓ 暂停✗ 停止✗ 恢复✓
        //   已中断 Interrupted（可续传）：开始✓ 暂停✗ 停止✗ 恢复✓
        //   已取消 Canceled（不可续传）：开始✓ 暂停✗ 停止✗ 恢复✗ —— 与「已中断」必须能区分
        FooterStartButton.IsEnabled = Session.CanStart;
        FooterPauseButton.IsEnabled = Session.CanPause;
        FooterStopButton.IsEnabled = Session.CanStop;
        FooterResumeButton.IsEnabled = Session.CanResume;
    }

    /// <summary>开始迁移（底栏）：与 Step2 的「开始迁移」同一入口（同一个会话、同一份 Core 计划）。</summary>
    private async void FooterStart_Click(object sender, RoutedEventArgs e)
    {
        // ★ D6.1 §11 ★ 观察既有 guard（CanStart）的结果；不改任何迁移行为。
        using var trace = ActionTrace.Begin(ActionKinds.Start, ControlIds.ShellTransferStart, "click", "ShellFooter");
        trace.Eligibility(Session.CanStart, Session.CanStart ? "allowed" : "cannot-start");
        if (!Session.CanStart) { trace.Reject("cannot-start", "ShellFooter"); return; }
        trace.Started();
        await Session.RunAsync(PageConnect.CurrentPassword);
        trace.Complete(DiagnosticOutcome.Succeeded, "footer-start-returned");
        PushFooter();
    }

    /// <summary>暂停（底栏）：走 Core 的 JobContext.RequestPause（VM 的 PauseAsync，合作式）。</summary>
    private async void FooterPause_Click(object sender, RoutedEventArgs e)
    {
        using var trace = ActionTrace.Begin(ActionKinds.Pause, ControlIds.ShellTransferPause, "click", "ShellFooter");
        trace.Eligibility(true, "allowed");
        trace.Started();
        await Session.PauseAsync();
        trace.Complete(DiagnosticOutcome.Succeeded, "footer-pause-returned");
        PushFooter();
    }

    /// <summary>停止（底栏）：走 VM 的 StopAsync（取消令牌；已拷入部分保留，可续传）。</summary>
    private async void FooterStop_Click(object sender, RoutedEventArgs e)
    {
        using var trace = ActionTrace.Begin(ActionKinds.Stop, ControlIds.ShellTransferStop, "click", "ShellFooter");
        trace.Eligibility(true, "allowed");
        trace.Started();
        await Session.StopAsync();
        trace.Complete(DiagnosticOutcome.Succeeded, "footer-stop-returned");
        PushFooter();
    }

    /// <summary>恢复（底栏）：走 VM 的 ResumeAsync（先 ClearPauseRequest，再沿用 job.json 原定义续传）。</summary>
    private async void FooterResume_Click(object sender, RoutedEventArgs e)
    {
        using var trace = ActionTrace.Begin(ActionKinds.Resume, ControlIds.ShellTransferResume, "click", "ShellFooter");
        trace.Eligibility(Session.CanResume, Session.CanResume ? "allowed" : "cannot-resume");
        if (!Session.CanResume) { trace.Reject("cannot-resume", "ShellFooter"); return; }
        trace.Started();
        await Session.ResumeAsync(PageConnect.CurrentPassword);
        trace.Complete(DiagnosticOutcome.Succeeded, "footer-resume-returned");
        PushFooter();
    }
    /// <summary>
    /// 开发者材质调节入口（标题栏小图标）：切换浮层显示。
    /// 属于 Developer Visual Tuning，不是普通业务功能；不改任何业务状态。
    /// </summary>
    private void DeveloperTuningButton_Click(object sender, RoutedEventArgs e) =>
        TogglePanel(TuningPanel, ChangelogPanel, sender as FrameworkElement ?? DeveloperTuningButton, onOpened: () =>
            TuningPanel.Attach(DeveloperVisualTuning.Current));

    /// <summary>
    /// 更新日志的唯一入口：**v0.5.0 版本徽章**（ChangelogButton）。
    /// U61（用户决定）：原先还有一个「历史更新」文字入口，属重复入口，已删除 —— 只保留徽章点击。
    /// 锚点仍取实际被点击的元素（sender），这样将来若再加入口也不会出现"点 A、面板按 B 弹出"。
    /// 内容仅来自内置 docs\更新日志.md。
    /// </summary>
    private void ChangelogButton_Click(object sender, RoutedEventArgs e) =>
        TogglePanel(ChangelogPanel, TuningPanel, sender as FrameworkElement ?? ChangelogButton, onOpened: () => ChangelogPanel.FocusFirstElement());

    /// <summary>
    /// 开/关 Deep Trace 的**有限 Routed 输入观测**（方案 §9）：注册在 Shell 自己的根元素上，
    /// `handledEventsToo: true` 以便看到已被子控件处理的事件；**限时**、**不采正文**、**不写 Handled**。
    /// 关窗时统一注销（见 ShutdownAndExit）。
    /// </summary>
    private void SetDeepTraceInputObservation(bool enabled)
    {
        try
        {
            var runtime = DiagnosticBootstrap.Runtime;
            if (runtime is null) return;

            _deepTraceObserver ??= new DeepTraceInputObserver(runtime.Publisher, "Shell");
            if (!enabled)
            {
                _deepTraceObserver.Stop();
                return;
            }

            if (Content is not FrameworkElement root) return;
            _deepTraceObserver.Start(root);
            // 浮层是独立生命周期：单独登记，否则它上面的事件到不了根的路由链。
            _deepTraceObserver.RegisterOverlay(DiagnosticCenterPanel);
        }
        catch (Exception)
        {
            // 观测开关绝不影响交互。
        }
    }

    /// <summary>
    /// 导出诊断包（**本地 ZIP，绝不上传**）。
    /// 只读运行时快照 + 会话目录；运行时不可用（未启动/已降级）时如实返回失败，不假装导出成功。
    /// 输出目录优先用桌面（用户最容易找到），取不到就退到本应用数据目录；同名绝不覆盖（由导出服务保证）。
    /// </summary>
    private static async System.Threading.Tasks.Task<DiagnosticExportResult> ExportDiagnosticPackageAsync(
        System.Threading.CancellationToken ct)
    {
        try
        {
            var runtime = DiagnosticBootstrap.Runtime;
            if (runtime?.Store is null)
                return new DiagnosticExportResult(false, null, "diagnostics-unavailable", 0);

            var outputDir = ResolveExportDirectory();

            // ★ D6.1 §18 ★ 真正放到**后台线程**执行：
            //   cutoff（含对 writer 的有界等待）、封段、逐行脱敏、JSON 生成、ZIP 压缩、SHA256 全部
            //   不在 UI 线程上做；UI 只 await 这个 Task（按钮禁用=Running，可取消）。
            //   旧实现在 UI 线程同步跑完再 Task.FromResult ⇒ 大包会把界面卡住。
            return await System.Threading.Tasks.Task.Run(() =>
            {
                // 截止点：有界等待 writer 追上 → 就地封存活动段 ⇒
                // "用户点导出前最后一段时间"的证据才会真正进包（旧实现会跳过活动段）。
                var cutoff = runtime.PrepareExportCutoff();

                var request = new PCMig.Diagnostics.Export.DiagnosticExportRequest
                {
                    SessionDir = runtime.Store!.SessionDir,
                    OutputDirectory = outputDir,
                    AppVersion = typeof(App).Assembly.GetName().Version?.ToString(),
                    Health = runtime.GetHealthSnapshot(),
                    Flight = runtime.FlightStatistics(),
                    Rules = runtime.RuleEngineStatistics(),
                    Incidents = runtime.ActiveIncidents.ToArray(),
                    Cutoff = cutoff,
                };

                var outcome = new PCMig.Diagnostics.Export.DiagnosticPackageExporter().Export(request, ct);
                return new DiagnosticExportResult(outcome.Succeeded, outcome.ZipPath, outcome.FailureReason, outcome.Bytes);
            }, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return new DiagnosticExportResult(false, null, "canceled", 0);
        }
        catch (Exception ex)
        {
            return new DiagnosticExportResult(false, null, "export-failed:" + ex.GetType().Name, 0);
        }
    }

    private static string ResolveExportDirectory()
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrWhiteSpace(desktop)) return Path.Combine(desktop, "PCMig-Diagnostic");
        }
        catch (Exception) { /* 取不到桌面就退到应用数据目录 */ }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCMig", "Diagnostics", "exports");
    }

    /// <summary>
    /// 诊断中心入口（工具入口区：**紧挨「材质调节」右侧**，与它同一水平轴、同一几何 —— 宽高/内边距/
    /// 背景/边框/图标字号全部取相同值，因此不依赖肉眼对齐，也不靠负 Margin/Translate 硬拉）。
    /// 只切换浮层可见性与焦点，不碰任何业务状态；关闭时停掉它自己的刷新节拍（无常驻计时器）。
    /// </summary>
    private void DiagnosticCenterButton_Click(object sender, RoutedEventArgs e)
    {
        var anchor = sender as FrameworkElement ?? DiagnosticCenterButton;

        // 互切：若另一个工具浮层正开着，让它先完整收回自己的入口，再打开诊断中心（与既有语义一致）。
        var other = new FrameworkElement[] { TuningPanel, ChangelogPanel }
            .FirstOrDefault(p => p.Visibility == Visibility.Visible) ?? ChangelogPanel;

        var willOpen = DiagnosticCenterPanel.Visibility != Visibility.Visible;
        TogglePanel(DiagnosticCenterPanel, other, anchor, onOpened: () =>
        {
            DiagnosticCenterPanel.Attach(_diagnosticCenterVm!);
            DiagnosticCenterPanel.FocusFirstElement();
        });

        // 关闭路径：停掉面板自己的刷新节拍（打开路径由 Attach 启动）。
        if (!willOpen) DiagnosticCenterPanel.StopRefresh();
    }

    /// <summary>
    /// 两个浮层共用的开合语义（§28/§29：打开与关闭都必须有动画，关闭不是 Hard Cut）：
    ///   • 互斥：打开一个就关掉另一个（同样是带动画的关闭）；
    ///   • 打开：先就位 → 再切换可见性 → 再播放，避免闪一帧（见 MotionDirector 注释）；
    ///   • 关闭：播反向动画，**动画结束后**才真正折叠。
    /// 只动视觉与焦点，不碰任何业务状态。
    /// </summary>
    private void TogglePanel(FrameworkElement panel, FrameworkElement other, FrameworkElement anchor, Action? onOpened)
    {
        if (panel.Visibility == Visibility.Visible)
        {
            // U62 接管语义：这个面板**正在收回**（Closing）时用户又点了入口 ⇒ 他的意图是"重新打开"，
            // 于是从当前可见几何直接接管，反向长回面板 —— 而不是把关闭动画再重启一遍。
            if (_fluidZoom.Current == FluidZoomTransitionCoordinator.Phase.Closing)
            {
                OpenPanel(panel, anchor, onOpened);
                return;
            }
            ClosePanel(panel);
            return;
        }

        // 互切（用户第 27 节）：另一个面板必须先**完整 Fluid Zoom 回它自己的入口**，再打开新的。
        // 第一版采用串行 Close A → Open B（先保证逻辑正确，不做并行双 morph）。
        if (other.Visibility == Visibility.Visible)
        {
            ClosePanel(other, () => OpenPanel(panel, anchor, onOpened));
            return;
        }

        OpenPanel(panel, anchor, onOpened);
    }

    /// <summary>
    /// 打开一个 Utility Panel。顺序对观感是决定性的（U60 的教训，U62 继续遵守）：
    ///   **先在不可见状态下完成最终定位**（透明就位 → 定位 → 强制同步布局 → 再定位）→
    ///   几何确定之后**才**播动画 ⇒ 绝不会出现"第一帧在旧位置、第二帧跳到入口"。
    /// </summary>
    private void OpenPanel(FrameworkElement panel, FrameworkElement anchor, Action? onOpened)
    {
        // 已经在打开 / 已打开 ⇒ 不做任何重复编排（绝不叠加第二个 Morph Shell）。
        if (_fluidZoom.Current is FluidZoomTransitionCoordinator.Phase.Opening or FluidZoomTransitionCoordinator.Phase.Open
            && ReferenceEquals(panel, _activePanel))
        {
            return;
        }

        // 关闭占用的入场判定窗口会影响紧随其后的打开，这里显式释放一次。
        MotionState.Current.Release("panel-entrance");
        MotionState.Current.TryBegin("panel-entrance", MotionWindow);

        _activePanel = panel;
        _activeAnchor = anchor;

        // 透明就位：布局能跑、但用户看不到（动画期间也不接受点击）。
        panel.Opacity = 0;
        panel.IsHitTestVisible = false;
        panel.Visibility = Visibility.Visible;

        TryPlacePanel(panel, anchor, PanelFallbackWidth(panel), out var centerPointX, out var centerPointY);
        panel.UpdateLayout();   // 同步布局：到此 ActualWidth/ActualHeight 已是真实值
        if (TryPlacePanel(panel, anchor, PanelFallbackWidth(panel), out var finalX, out var finalY))
        {
            centerPointX = finalX;
            centerPointY = finalY;
        }
        _activeCenterPoint = new Windows.Foundation.Point(centerPointX, centerPointY);

        // ★ U62：首选 Fluid Zoom（入口本身 morph 成面板）。逐级回退，保证功能永不依赖动画：
        //    ① Fluid Zoom → ② 上一版 Origin Reveal（已验收）→ ③ 直接吸附到可见终态。
        if (!_fluidZoom.TryOpen(panel, anchor, () => SettlePanelOpened(panel)))
        {
            MotionDirector.PlayUtilityPanelOpen(panel, centerPointX, centerPointY, completed: () => SettlePanelOpened(panel));
        }

        if (onOpened is not null) onOpened();
    }

    /// <summary>面板打开的最终可交互状态（Fluid Zoom 与 Origin Reveal 两条路径共用）。</summary>
    private static void SettlePanelOpened(FrameworkElement panel)
    {
        panel.Opacity = 1.0;
        panel.IsHitTestVisible = true;
    }

    /// <summary>
    /// U60：定位已改为**同步**完成（`UpdateLayout()` + 二次夹紧），不再需要"补一帧"的常驻回调 ——
    /// 那正是"Panel 先出现在旧位置、下一帧才跳到锚点"的来源。
    /// </summary>
    private static double PanelFallbackWidth(FrameworkElement panel)
    {
        var width = panel.Width;
        return double.IsNaN(width) || width <= 0 ? 440.0 : width;
    }

    /// <summary>读取 Motion.xaml 的数值 Token（与 MotionDirector 同源，避免两处 Token 读取口径）。</summary>
    private static double ReadMotionDouble(string key, double fallback)
    {
        if (Application.Current?.Resources is { } res && res.TryGetValue(key, out var value) && value is double d) return d;
        return fallback;
    }

    /// <summary>
    /// 关闭浮层：轻微缩回**入口方向** + Fade，**动画结束后**才折叠可见性（绝不是 Hard Cut）。
    /// CenterPoint 用打开时那个入口算出的原点 ⇒ 缩回方向仍然朝向触发按钮。
    /// </summary>
    private void ClosePanel(FrameworkElement panel) => ClosePanel(panel, afterClosed: null);

    /// <summary>
    /// 关闭浮层：**完整反向 morph** 回它自己的触发入口（用户第 23 节："窗口收回那个按钮里"）。
    /// 绝不是 Hard Cut，也不是单纯 Fade Out。
    ///   · Source Rect 每次关闭都**实时重算**（用户第 24 节：窗口尺寸 / 整体缩放 / 布局可能变过）；
    ///   · 动画结束后才折叠可见性；
    ///   · Fluid Zoom 不可用时回退上一版 Origin Reveal（缩回入口方向 + Fade），功能不受影响。
    /// </summary>
    private void ClosePanel(FrameworkElement panel, Action? afterClosed)
    {
        if (panel.Visibility != Visibility.Visible)
        {
            afterClosed?.Invoke();
            return;
        }
        MotionState.Current.Release("panel-entrance");

        // 缩回方向的入口：优先用打开时那个**真实被点击的元素**；否则退回该面板的默认入口。
        var anchor = ReferenceEquals(panel, _activePanel) && _activeAnchor is not null
            ? _activeAnchor
            : ReferenceEquals(panel, DiagnosticCenterPanel) ? DiagnosticCenterButton
            : ReferenceEquals(panel, ChangelogPanel) ? ChangelogButton
            : DeveloperTuningButton;

        var centerPointX = _activeCenterPoint.X;
        var centerPointY = _activeCenterPoint.Y;
        if (TryPlacePanel(panel, anchor, PanelFallbackWidth(panel), out var freshX, out var freshY))
        {
            // 打开期间窗口尺寸 / 整体缩放可能变过：用当前真实位置重算，保证缩回方向仍朝入口。
            centerPointX = freshX;
            centerPointY = freshY;
            _activeCenterPoint = new Windows.Foundation.Point(freshX, freshY);
        }

        _activePanel = null;
        _activeAnchor = null;
        panel.IsHitTestVisible = false;   // 收回过程中不再接受点击

        void AfterClosed()
        {
            panel.Visibility = Visibility.Collapsed;

            // ★ D6.1 §17 ★ **统一生命周期收口**：无论是点入口关、点面板 × 关、
            //   还是与另一个工具面板互切，只要浮层被折叠就停掉它自己的刷新节拍与 Viewer 订阅。
            //   旧实现只在"点入口关闭"这一条路径停表 ⇒ 点 × 或互切后，隐藏的面板仍每 500ms
            //   刷新并写控件（实测的 P2 问题）。
            StopPanelRefresh(panel);

            ChangelogButton.Focus(FocusState.Programmatic);
            afterClosed?.Invoke();
        }

        if (!_fluidZoom.TryClose(panel, anchor, AfterClosed))
        {
            MotionDirector.PlayUtilityPanelClose(panel, centerPointX, centerPointY, AfterClosed);
        }
    }

    /// <summary>
    /// ★ D6.1 §17 ★ 停掉某个浮层的"刷新节拍 + Viewer 订阅"（幂等）。
    ///
    /// 本实现的"Viewer 订阅"就是**这个节拍 + 每次读取有界快照**（没有长期事件订阅持有），
    /// 因此停表即等于停止订阅；再次打开时 <c>Attach</c> 会重新起表并重新读快照。
    /// 后台采集（writer/analyzer/flight）**完全不受影响**：这里只碰 UI 侧。
    /// </summary>
    private void StopPanelRefresh(FrameworkElement panel)
    {
        try
        {
            if (ReferenceEquals(panel, DiagnosticCenterPanel)) DiagnosticCenterPanel.StopRefresh();
        }
        catch
        {
            // 关闭期异常不得外溢。
        }
    }

    /// <summary>源状态文案（ProductHeader）：未连接 = 参考图文案「未指定旧电脑」；已连接 = 真实主机名。</summary>
    private void UpdateSourceStatus() =>
        SourceStatusText.Text = ViewModel.IsConnected ? $"已连接 {ViewModel.Host.Trim()}" : "未指定旧电脑";

    /// <summary>把当前有效客户区映射为集中管理的 Wide / Normal / Compact 视觉密度。</summary>
    private void ApplyResponsiveLayout(double width, double height)
    {
        if (SidebarColumn is null || RailGapColumn is null || WorkspaceLayout is null) return;
        // ★ UniformScaleHost 激活时：结构恒为 Canonical，屏蔽一切结构性 reflow。
        //   缩放由 Viewbox 统一完成；这里若仍按真实窗口尺寸重算，就会把 Wide/Normal/Compact
        //   的结构分支重新引进来（用户修订 4 明令禁止：Step2 不改左右结构、Step3 不堆叠、
        //   Step4 不折行）。Canonical 尺寸下算出的就是原始 Wide 几何。
        if (UniformScaleHost.IsActive)
        {
            width = UniformScaleHost.DesignWidth;
            height = UniformScaleHost.DesignHeight;
        }
        // 传入真实 DPI：仅用于圆角/Pixel 对齐吸附，不改变 DIP 口径（§9 的三口径区分）。
        var layout = ResponsiveLayoutController.Calculate(width, height, CurrentScale());
        SidebarColumn.Width = new GridLength(layout.SidebarWidth);
        RailGapColumn.Width = new GridLength(layout.RailGap);
        PageProgress.ApplyLayoutMode(layout.Mode);
        PageResult.ApplyLayoutMode(layout.Mode);
        // §36/§37/§55：把同一份 Token 落到 Header / 底栏 / 工作区外壳 / Step1 的实际元素上。
        // 纯视觉赋值，解析不到的元素会被跳过；回退 = 删掉这一行。
        // ★ 必须传 ApplicationRoot（DesignSurface 内的真实根）：重挂后 window.Content 已是
        //   Viewbox，而全部 x:Name 仍在旧根的作用域里 —— 传 window.Content 会让 FindName
        //   全部返回 null，响应式整链**静默失效**（不报错）。集成点 1。
        ShellResponsiveLayout.Apply(ApplicationRoot, layout);
        PositionOverlayPanels();
        DiagResponsive("apply", width, height, layout);
    }

    /// <summary>
    /// P0 诊断（env <c>PCMIG_RESPONSIVE_DIAG=1</c>，默认完全关闭，不影响产品行为）：
    /// 记录**一次布局决策**的全部尺寸与来源，用来找出"UniformScaleHost 之外的第二层 Scale/Reflow"。
    /// 关键观测点：真实 client 尺寸 vs UniformScaleHost.AppliedScale vs DesignSurface/应用根/工作区/
    /// 页面视口/四页根元素的实际尺寸 —— 若 0.75 下 DesignSurface 与四页尺寸不是 Canonical，
    /// 就说明有第二层在按缩小后的窗口改内容布局。
    /// </summary>
    private void DiagResponsive(string stage, double inputWidth, double inputHeight, ResponsiveLayout layout)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("PCMIG_RESPONSIVE_DIAG"), "1", StringComparison.Ordinal))
        {
            return;
        }
        try
        {
            var client = (Content as FrameworkElement)?.XamlRoot?.Size;
            var line =
                $"{DateTime.Now:HH:mm:ss.fff} {stage} " +
                $"in={inputWidth:F0}x{inputHeight:F0} mode={layout.Mode} " +
                $"client={(client?.Width ?? 0):F0}x{(client?.Height ?? 0):F0} " +
                $"uniActive={UniformScaleHost.IsActive} uniScale={UniformScaleHost.AppliedScale:F4} " +
                $"design={UniformScaleHost.DesignSurface?.ActualWidth ?? 0:F0}x{UniformScaleHost.DesignSurface?.ActualHeight ?? 0:F0} " +
                $"appRoot={ApplicationRoot?.ActualWidth ?? 0:F0}x{ApplicationRoot?.ActualHeight ?? 0:F0} " +
                $"shell={(ShellRowsGrid?.ActualWidth ?? 0):F0}x{(ShellRowsGrid?.ActualHeight ?? 0):F0} " +
                $"workspace={(WorkspaceShell?.ActualWidth ?? 0):F0}x{(WorkspaceShell?.ActualHeight ?? 0):F0} " +
                $"viewport={(PageViewport?.ActualWidth ?? 0):F0}x{(PageViewport?.ActualHeight ?? 0):F0} " +
                $"p1={(PageConnect?.ActualWidth ?? 0):F0}x{(PageConnect?.ActualHeight ?? 0):F0} " +
                $"p2={(PageSelectData?.ActualWidth ?? 0):F0}x{(PageSelectData?.ActualHeight ?? 0):F0} " +
                $"p3={(PageProgress?.ActualWidth ?? 0):F0}x{(PageProgress?.ActualHeight ?? 0):F0} " +
                $"p4={(PageResult?.ActualWidth ?? 0):F0}x{(PageResult?.ActualHeight ?? 0):F0}";
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pcmig-responsive-diag.log"),
                line + Environment.NewLine);
        }
        catch (Exception)
        {
            // 诊断绝不影响产品行为
        }
    }

    /// <summary>
    /// A25 / A26（用户人工标注项，两轮反馈）：
    ///   第一轮标注：「点击左边的调节按钮，面板却出现在最右侧」；
    ///   第二轮标注：「可读性不高，而且弹出来的位置错误，我希望它的**打开点击的按钮在哪，
    ///               它这个页面就出现在哪**。更新日志那个页面也是一模一样的问题」。
    ///
    /// 实测（Round 12，用 vision_ground 量运行中窗口）：
    ///   触发按钮在窗口内 x≈519（宽 28）；面板实测 x=1051..1384（宽 334）、上缘 y=113。
    ///   → 上缘确实挂在按钮下方，但**左缘离按钮 532 DIP**，视觉上"从按钮弹出"的联系完全没有建立。
    ///
    /// 修法：改为**左缘对齐触发按钮**（垂直在按钮正下方 `PCMigUtilityPanelAnchorGap`），并夹紧在窗口内。
    ///   每个浮层用它自己的触发入口：
    ///     TuningPanel     ← DeveloperTuningButton
    ///     ChangelogPanel  ← ChangelogButton（v0.5.0 版本徽章；U61 起更新日志只有这一个入口）
    /// 只改位置，不动浮层内容、动效与业务状态。
    /// </summary>
    private void PositionOverlayPanels()
    {
        TryPlacePanel(TuningPanel, ResolvePanelAnchor(TuningPanel, DeveloperTuningButton), 348, out _, out _);
        TryPlacePanel(ChangelogPanel, ResolvePanelAnchor(ChangelogPanel, ChangelogButton), 440, out _, out _);
        TryPlacePanel(DiagnosticCenterPanel, ResolvePanelAnchor(DiagnosticCenterPanel, DiagnosticCenterButton), 520, out _, out _);
    }

    /// <summary>
    /// U60 §10：**谁触发就锚谁**（锚点按实际点击的元素决定，而不是写死某个按钮）。
    /// 未打开（或不是这个面板）时退回该面板的默认入口，保证 Resize 重算也有合理锚点。
    /// </summary>
    private FrameworkElement ResolvePanelAnchor(FrameworkElement panel, FrameworkElement fallback) =>
        ReferenceEquals(panel, _activePanel) && _activeAnchor is not null ? _activeAnchor : fallback;

    /// <summary>
    /// 定位单个面板，并给出 **Scale 原点（CenterPoint）**：
    ///   · CenterPoint.X = 触发按钮中心 相对 Panel 左边缘的位移（anchorCenterX − panelLeft）；
    ///   · CenterPoint.Y = 0（Panel 顶边）—— 于是 Panel 看起来是"从入口下面展开出来"的。
    /// 全部在 Canonical DesignSurface 坐标系内计算（与 anchor 同坐标系，绝不与真实窗口坐标混用）。
    /// 垂直间距统一取自 Motion.xaml 的 `PCMigUtilityPanelAnchorGap`（两个面板共用，不各调各的 Margin）。
    /// </summary>
    private bool TryPlacePanel(
        FrameworkElement? panel,
        FrameworkElement? anchor,
        double fallbackWidth,
        out double centerPointX,
        out double centerPointY)
    {
        centerPointX = 0;
        centerPointY = 0;
        if (panel is null || panel.Visibility != Visibility.Visible) return false;
        if (anchor is null) return false;
        // ★ 坐标系必须是 DesignSurface 内的应用根（集成点 2）：Uniform 模式下 panel 与 anchor
        //   都在其内，root 尺寸恒为 1424×892，夹紧逻辑与 TransformToVisual 才落在同一坐标空间；
        //   若用真实窗口尺寸去夹紧未缩放的本地坐标，面板位置会算错。
        if (ApplicationRoot is not FrameworkElement root) return false;

        var rootWidth = root.ActualWidth;
        var rootHeight = root.ActualHeight;
        if (rootWidth <= 0 || rootHeight <= 0) return false;

        double anchorLeft, anchorTop, anchorWidth, anchorHeight;
        try
        {
            var anchorOrigin = anchor.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0));
            anchorLeft = anchorOrigin.X;
            anchorTop = anchorOrigin.Y;
            anchorWidth = anchor.ActualWidth > 0 ? anchor.ActualWidth : 28;
            anchorHeight = anchor.ActualHeight > 0 ? anchor.ActualHeight : 28;
        }
        catch
        {
            return false; // 视觉树未就绪：保持现有边距，不抛异常
        }

        // 统一 Anchor Gap（用户 U60 §9）：PanelTop − AnchorBottom，默认 5 DIP。
        var anchorGap = ReadMotionDouble("PCMigUtilityPanelAnchorGap", 5.0);
        const double edgeMargin = 24;  // 距窗口左右边缘的最小留白
        const double topSafety = 44;   // 不低于标题栏带（42 DIP）

        var panelWidth = panel.ActualWidth > 0 ? panel.ActualWidth : panel.Width;
        if (double.IsNaN(panelWidth) || panelWidth <= 0) panelWidth = fallbackWidth;

        // 水平：面板左缘贴住按钮左缘；越界就夹回来（窄窗口时退化为贴右缘）。
        var left = anchorLeft;
        var maxLeft = rootWidth - panelWidth - edgeMargin;
        if (maxLeft < edgeMargin) maxLeft = edgeMargin;
        if (left > maxLeft) left = maxLeft;
        if (left < edgeMargin) left = edgeMargin;

        var top = anchorTop + anchorHeight + anchorGap;
        if (double.IsNaN(top) || top < topSafety) top = topSafety;

        // ★ U60：高度处理改为"**保顶部间距、改面板高度**"，而不是"把面板往上提"。
        //   旧做法在面板过高时会把 top 上提，于是面板顶边盖住触发入口 —— 用户实测的
        //   "Developer Tuning 与入口过紧 / 有覆盖感"正是这个。垂直间距必须**永远**保持。
        //   两个面板内部本来都有 ScrollViewer，缩小高度只是让它开始滚动，不会丢内容。
        var availableHeight = rootHeight - top - edgeMargin;
        if (availableHeight > 160)
        {
            var currentMax = panel.MaxHeight;
            if (double.IsNaN(currentMax) || currentMax > availableHeight) panel.MaxHeight = availableHeight;
        }

        panel.Margin = new Thickness(left, top, 0, 0);
        panel.HorizontalAlignment = HorizontalAlignment.Left;
        panel.VerticalAlignment = VerticalAlignment.Top;

        // Scale 原点对准触发入口：X = 按钮中心 − Panel 左缘；Y = Panel 顶边。
        centerPointX = (anchorLeft + anchorWidth / 2.0) - left;
        centerPointY = 0.0;
        return true;
    }

    // ── Motion 防重入（IsTransitioning）+ 方向性入场（§26/§31） ────────────────
    // 一次入场动画的判定窗口：约等于 PCMigMotionDurationNormal(200ms) + 抖动余量。
    /// <summary>
    /// 一次入场动画的判定窗口 —— **仅供面板链（Utility Panel 的开合）使用**。
    /// U59：页面链已改为接管式过渡，不再使用本窗口做抑制（抑制会让"点得快时动画消失"）。
    /// </summary>
    private static readonly TimeSpan MotionWindow = TimeSpan.FromMilliseconds(230);

    /// <summary>
    /// 导航方向（+1 前进 / -1 后退 / 0 未知，例如侧栏或 Ctrl+数字 直达）。
    /// 每次 Changed 时由 from→to 的真实位置差算出，供方向性入场使用。
    /// </summary>
    private int _navDirection;

    /// <summary>四页整页 Push 的纯 Presentation 协调器（接管式过渡；不重建页面 / 不重建 ViewModel）。</summary>
    private readonly PageTransitionCoordinator _pageTransition;

    /// <summary>
    /// U62：两个 Utility Panel 的 Matched-Geometry Fluid Zoom 协调器 —— 让**被点击的入口本身**
    /// 连续 morph 成完整面板（取代被用户否决的 Fade + 0.945 Scale "Origin Reveal"）。
    /// 几何测量、Morph Shell、状态机与接管逻辑全部在协调器内；MainWindow 只负责"谁触发、何时开合"。
    /// </summary>
    private readonly FluidZoomTransitionCoordinator _fluidZoom;

    /// <summary>当前打开的 Utility Panel 与它的**真实触发入口**（谁触发就锚谁；关闭后清空）。</summary>
    private FrameworkElement? _activePanel;

    /// <summary>诊断中心 ViewModel（只读投影；数据源在运行时缺失时返回"尚未采集"）。</summary>
    private DiagnosticCenterViewModel? _diagnosticCenterVm;

    /// <summary>Deep Trace 的有限输入观测（仅在用户显式开启且限时窗口内注册）。</summary>
    private DeepTraceInputObserver? _deepTraceObserver;
    private FrameworkElement? _activeAnchor;

    /// <summary>当前打开面板的 Scale 原点（Panel 局部坐标，对应触发入口）；关闭时据此缩回入口方向。</summary>
    private Windows.Foundation.Point _activeCenterPoint;

    /// <summary>
    /// Changing 在可见性变化**之前**触发：只做方向记账（+1 前进 / -1 后退 / 0 未知）。
    ///
    /// U59 起这里**不再**做两件旧事：
    ///   ① 不再用 MotionState 抑制"动画进行中的新导航" —— 抑制的代价正是用户实测到的
    ///      "点得快时动画直接消失"；现在页面链改为**接管式过渡**（每段 Push 从页面当前实际位置接续）。
    ///   ② 不再对目标页 ResetTransitionState —— 接管式过渡需要保留目标页当前的真实位移
    ///      （它可能正从视口外滑入，或被上一段推在途中），归零会让下一段 Push 跳变。
    /// </summary>
    private void OnNavChanging(StepKind from, StepKind to)
    {
        _navDirection = (int)to == (int)from ? 0 : ((int)to > (int)from ? 1 : -1);
    }

    /// <summary>
    /// 导航已完成、目标页已可见之后（Changed）：交给 <see cref="PageTransitionCoordinator"/>
    /// 播放 Cross-Slide。必须在这里而不是 Changing —— Changing 时目标页还不可见，
    /// 且 x:Bind 尚未把旧页折叠，两页的可见性状态还没到位。
    ///
    /// 页面实例与 ViewModel 全程复用（四页一直存在于视觉树，导航只切 Visibility），
    /// 因此过渡不会丢失输入内容、滚动位置或任何业务状态。
    /// </summary>
    private void OnNavChanged(StepKind from, StepKind to)
    {
        // 跨级导航（例如 1→4）也只播这一遍 Forward，不分段。
        var direction = _navDirection == 0 ? ((int)to >= (int)from ? 1 : -1) : _navDirection;
        _pageTransition?.OnNavigated(from, to, direction, allowAnimation: true);
        _navDirection = 0;
    }

    private FrameworkElement? PageForStep(StepKind kind) => kind switch
    {
        StepKind.Connect => PageConnect,
        StepKind.SelectData => PageSelectData,
        StepKind.Progress => PageProgress,
        StepKind.Result => PageResult,
        _ => null,
    };

    /// <summary>
    /// 设定 Canonical 初始视口。进程已声明 PerMonitorV2（见 app.manifest），
    /// AppWindow 尺寸单位是**物理像素**，因此必须按当前窗口 DPI 换算：
    /// 直接写死 1440x900 会在 125% 缩放下把 XAML 可用空间压成 1139x713 DIP，破坏 Canonical 布局。
    /// 这里固定 XAML 客户端空间为 1424x891 DIP，使 Canonical 比例在任何缩放率下都不变。
    /// </summary>
    private void ApplyCanonicalClientSize()
    {
        var scale = CurrentScale();
        AppWindow.ResizeClient(new SizeInt32(
            (int)Math.Round(CanonicalClientWidthDip * scale),
            (int)Math.Round(CanonicalClientHeightDip * scale)));
    }

    /// <summary>
    /// A21：为系统 Caption Buttons 预留安全区。宽度取自 <see cref="Microsoft.UI.Windowing.AppWindowTitleBar.RightInset"/>
    /// （物理像素，运行时实测），换算成 DIP 后作为标题栏内容的右保留列宽；
    /// **不硬编码猜测系统按钮宽度**（不同 DPI / 系统主题下按钮宽度不同）。取不到时用保守下限兜底。
    /// </summary>
    private void ApplyTitleBarInset()
    {
        try
        {
            var scale = CurrentScale();
            var rightDip = AppWindow.TitleBar.RightInset / scale;
            var leftDip = AppWindow.TitleBar.LeftInset / scale;
            // 系统按钮组通常 ≥ 3×44 DIP；下限用于 API 尚未就绪的瞬间，避免内容被压在按钮下面。
            TitleBarRightReserve.Width = new GridLength(Math.Max(136, rightDip + 10));
            TitleBarRoot.Padding = new Thickness(Math.Max(20, leftDip + 12), 0, 0, 0);
        }
        catch
        {
            TitleBarRightReserve.Width = new GridLength(136);
        }
    }

    /// <summary>
    /// 响应式与浮层定位真正作用的**应用根**。
    ///   非 Uniform 模式：就是 window.Content（现状，逐位不变）；
    ///   UniformScaleHost 激活后：window.Content 已变成 Viewbox，真正的根（全部 x:Name 的名字
    ///   作用域）位于 DesignSurface 内，必须用它，否则 FindName 全 null（集成点 1）。
    /// </summary>
    private FrameworkElement? ApplicationRoot =>
        UniformScaleHost.ApplicationRoot ?? Content as FrameworkElement;

    /// <summary>
    /// U57：把过渡视口的 Clip 维护成"页面容器自身的矩形"。
    /// 整页 Push 期间两页会在纵向超出容器，Clip 保证它们只在 Workspace 内容区内可见；
    /// 静止时 Clip 恰好等于容器本身，因此不影响任何既有渲染（DAEL / 阴影 / 圆角都不变）。
    /// </summary>
    private void UpdatePageViewportClip()
    {
        if (PageViewport is null) return;
        var width = PageViewport.ActualWidth;
        var height = PageViewport.ActualHeight;
        if (width <= 0 || height <= 0) return;
        PageViewport.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, width, height),
        };
    }

    private double CurrentScale()
    {
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        return scale > 0 ? scale : 1.0;
    }

    /// <summary>
    /// 视口自校正（只做一次）。ResizeClient 保证的是"客户区"高度，而 ExtendsContentIntoTitleBar
    /// 让 XAML 视口额外覆盖标题栏带，两者差值随 DPI / 系统主题变化（本机 125% 实测差约 30 DIP），
    /// 会悄悄改掉 Canonical 比例。布局跑通后用实测视口回算一次，把视口锁到 1424x891 DIP。
    /// </summary>
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // ★ 观测值口径（UniformScaleHost 集成点 4 的窗口侧配套）：
        //   Uniform 模式下原根被固定在 1424×892 的 DesignSurface 内，它的 SizeChanged **不再代表视口**
        //   （恒等于设计面）⇒ 必须改读真实客户区 XamlRoot.Size，否则启动时那次一次性视口自校正会读到
        //   "已等于 Canonical"而提前返回，窗口高度停在 ResizeClient 请求值上（实测多出约 30 DIP 空白带）。
        //   非 Uniform 模式：观测值与原来逐位一致（e.NewSize）。
        var observedWidth = e.NewSize.Width;
        var observedHeight = e.NewSize.Height;
        if (UniformScaleHost.IsActive && Content is FrameworkElement hostRoot && hostRoot.XamlRoot is { } hostXamlRoot)
        {
            observedWidth = hostXamlRoot.Size.Width;
            observedHeight = hostXamlRoot.Size.Height;
        }
        ApplyTitleBarInset(); // 窗口激活后 RightInset 才可靠，这里再校正一次（幂等）
        ApplyResponsiveLayout(observedWidth, observedHeight);
        if (_viewportCalibrated) return;
        if (observedWidth <= 0 || observedHeight <= 0) return; // 首帧尺寸未就绪时不消耗这次校正机会
        _viewportCalibrated = true; // 只校正一次，避免窗口与布局互相触发的抖动循环
        // 实测：ResizeClient 的"客户区"不含 ExtendsContentIntoTitleBar 覆盖的那条标题栏带，
        // 系统会固定多给一段高度（本机 125% 实测 +38px / +29.8 DIP）。所以不能用"增量"补，
        // 必须先量出这段常量偏移，再从请求值里扣掉，才能让 XAML 视口回到 1424x891 DIP。
        var offsetWidthDip = observedWidth - CanonicalClientWidthDip;
        var offsetHeightDip = observedHeight - CanonicalClientHeightDip;
        if (Math.Abs(offsetWidthDip) < 1.5 && Math.Abs(offsetHeightDip) < 1.5) return; // 已经准确
        var scale = CurrentScale();
        AppWindow.ResizeClient(new SizeInt32(
            (int)Math.Round((CanonicalClientWidthDip - offsetWidthDip) * scale),
            (int)Math.Round((CanonicalClientHeightDip - offsetHeightDip) * scale)));
    }






}
