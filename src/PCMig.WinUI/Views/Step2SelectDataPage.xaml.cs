using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using PCMig.Core.Jobs;
using PCMig.Core.Models;
using PCMig.WinUI.Presentation;
using PCMig.WinUI.Presentation.Converters;
using PCMig.WinUI.Diagnostics;
using PCMig.Diagnostics.Abstractions;

namespace PCMig.WinUI.Views;

/// <summary>
/// Step 2 — 选择数据与目标。
///
/// 状态接线（本文件关键）：**代码直推 + 变更订阅**。
/// 试过两条绑定路线都不成立：{x:Bind State.X}（嵌套路径在后置注入后不重算）与
/// 经典 {Binding X} + DataContext（本页实测同样渲染为空）。因此改为由 Shell 注入适配层后
/// 直接把文案写进命名元素，并订阅 PropertyChanged 统一刷新——行为确定、可测。
/// 架构不变：View → PageReadiness（薄投影）→ 既有 ConnectionViewModel / MigrationSessionViewModel → Core；零业务规则。
///
/// 阶段 A 包 2（本文件新增）：Step 2 的计划生成链接线。
///   · 目录树 = MigrationSessionViewModel.Tree（唯一实例，懒加载真实读盘 + 三态勾选）；
///   · 「预检并生成计划」→ 组装 JobDefinition → PrepareAsync（Core 的
///     JobManager/PreflightChecker/SourceScanner/Planner/ScanGate 全部真实调用）；
///   · 两处需要用户确认的交互（扫描残缺闸门 / 发现未完成任务）在本层用 ContentDialog 实现
///     （ContentDialog 需要 XamlRoot，属页面层职责；ViewModel 只暴露回调与决定枚举）。
///   · **本层不实现任何迁移/扫描/规划规则**，也不新增第二套状态机。
/// </summary>
public sealed partial class Step2SelectDataPage : UserControl
{
    private PageReadiness? _state;
    private MigrationSessionViewModel? _session;
    private bool _suppressSync;

    /// <summary>口令取值口（由 Shell 注入：返回 Step 1 口令框的当前内容；不落任何字段、不写存档）。</summary>
    private Func<string?>? _passwordProvider;

    /// <summary>用户在某次预检里已确认「扫描不完整仍继续」——同一次点击的重跑用它，避免二次弹窗。</summary>
    private bool _incompleteScanConfirmed;

    /// <summary>
    /// A.5 取证展开（**只在 PCMIG_STEP2_TREE_QA_EXPAND 环境变量存在时被调用**，生产路径不可达）。
    /// 流程：等连接流程彻底结束 → 记录各根加载前后的真实状态 → 走既有取证装置展开 → 记录真实子项。
    /// 为什么要把根的 <c>ChildrenLoaded</c> 复位：<c>EnsureChildrenAsync</c> 在**读盘之前**就置位该标记，
    /// 若该次加载中途异常/结果被丢弃，标记仍为 true ⇒ 该节点**永久**不再重试、且 <c>Children</c> 里
    /// 只剩占位「…」（A.5 实测：7 个根全部如此）。复位只是让**取证**这一次能真正读盘，
    /// 不改变产品语义（生产路径下没有任何代码做这件事）。
    /// </summary>
    private async Task QaExpandForEvidenceAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "a5-expand-qa.log");
        try
        {
            await Task.Delay(3000);   // 等 Step1 的连接流程（枚举共享/测速/填充列表）彻底结束
            var session = _session;
            if (session is null) return;
            // 只展开环境变量点名的根（PCMIG_STEP2_TREE_QA_EXPAND_ONLY=C$,F$）：全部展开会把
            // ADMIN$ 的 111 个子项铺开，把要操作的根挤出视口，UI 层三态取证就没法做了。
            var only = (Environment.GetEnvironmentVariable("PCMIG_STEP2_TREE_QA_EXPAND_ONLY") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var log = new List<string> { $"--- A.5 取证展开 {DateTime.Now:HH:mm:ss.fff} 根数={session.Tree.RootNodes.Count} 只展开=[{string.Join(",", only)}] ---" };
            var targets = DirTreeView.RootNodes
                .Where(n => n.Content is DirNode m
                    && (only.Length == 0 || only.Contains(m.Name, StringComparer.OrdinalIgnoreCase)))
                .ToList();
            foreach (var node in targets)
            {
                if (node.Content is not DirNode m) continue;
                log.Add($"before: {m.Name,-8} ChildrenLoaded={m.ChildrenLoaded,-6} Children={m.Children.Count} IsChecked={m.IsChecked}");
                m.ChildrenLoaded = false;   // 仅取证：清掉“加载失败后永久置位”的标记
            }
            foreach (var node in targets)
            {
                if (node.Content is not DirNode m) continue;
                // 走与用户点箭头完全相同的路径：真实读盘 → 把该层子项搬进节点树 → 置展开
                await session.Tree.EnsureChildrenAsync(m);
                SyncNodeChildren(node);
                node.IsExpanded = true;
            }
            await Task.Delay(3000);
            foreach (var node in targets)
            {
                if (node.Content is not DirNode m) continue;
                var sample = string.Join(" / ", m.Children.Take(8).Select(c => c switch
                {
                    DirNode d => "DIR " + d.Name,
                    FileRow f => "FILE " + f.Name,
                    _ => "?"
                }));
                log.Add($"after : {m.Name,-8} ChildrenLoaded={m.ChildrenLoaded,-6} Children={m.Children.Count} 节点树子项={node.Children.Count} :: {sample}");
            }
            log.Add($"UI: DirTreeView.RootNodes={DirTreeView.RootNodes.Count}");
            File.AppendAllLines(path, log);

            // 展开完成后（若被点名）跑三态取证序列：用与用户点击相同的 ToggleFromUi，
            // 把每步的模型状态与 CollectCustomSelections() 真实输出写进 a5-selection-qa.log。
            // 只用一次性开关跑一遍：根集合在连接过程中是逐个到达的，若每次变化都跑就会出现多个
            // 探针并发改同一棵树（A.5 第一次实测的日志互相污染，正是这个原因）。
            if (A5SelectionQaProbe.Requested && !_qaSelectRan)
            {
                _qaSelectRan = true;
                await A5SelectionQaProbe.RunAsync(session.Tree, Path.Combine(AppContext.BaseDirectory, "a5-selection-qa.log"));
            }
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(path, "EX " + ex + Environment.NewLine); } catch { /* 取证失败不影响业务 */ }
        }
    }

    /// <summary>A.5 取证钩子开关（见 PushState 内注释；生产路径恒为 false）。</summary>
    private readonly bool _qaExpandEnabled =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PCMIG_STEP2_TREE_QA_EXPAND"))
        && Environment.GetEnvironmentVariable("PCMIG_STEP2_TREE_QA_EXPAND") != "0";

    /// <summary>上次触发取证展开时的根数量（只在 <see cref="_qaExpandEnabled"/> 为真时使用）。</summary>
    private int _qaExpandedAtRootCount;

    /// <summary>三态取证序列的一次性开关（见 PushState 内注释；生产路径永不置位）。</summary>
    private bool _qaSelectRan;

    /// <summary>
    /// A6 取证钩子开关：「已有任务」下拉的真实数据流取证（**只在 PCMIG_STEP2_JOBS_QA 非空且非 "0" 时启用**，
    /// 生产路径逐位不变）。为什么需要：WinUI 的 ComboBox 下拉**不响应合成点击**（A6 实测：合成左键
    /// 按下/抬起 + Alt+Down 都不开，进程里唯一新增的顶层窗口是内容为「Ctrl+1」的 tooltip），
    /// 因此"下拉里到底有几项"无法用截图取证 ⇒ 改用本探针把**真实进程内的集合状态**写进日志。
    /// 只读、不写任何业务状态，与既有 a5-expand-qa.log / a5-selection-qa.log 同款。
    /// </summary>
    private readonly bool _jobsQaEnabled =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PCMIG_STEP2_JOBS_QA"))
        && Environment.GetEnvironmentVariable("PCMIG_STEP2_JOBS_QA") != "0";

    /// <summary>上次写进探针日志的 (模型数, 下拉项数)，避免 PushState 高频重复写。</summary>
    private (int Jobs, int Items) _jobsQaLast = (-1, -1);

    /// <summary>程序化选中「已有任务」的一次性开关（见 <see cref="QaSelectJobIfRequested"/>）。</summary>
    private bool _jobsQaSelectDone;

    public Step2SelectDataPage()
    {
        InitializeComponent();
        // ★ 节点模式（Not ItemsSource）：层级由 TreeViewNode.Children 承载。
        //   反复实测的结论：
        //     · ItemsSource + DataTemplate 路线在本页不可用——模板根写 TreeViewItem 会“容器套容器”，
        //       真机展开直接崩（连空目录都崩）；写成内容模板则**展开不出子项**（容器不认领 Children）；
        //     · 容器层绑定 ItemsSource={Binding Children} 会在展开时无限递归（进程消失）；
        //   故采用节点模式：TreeViewNode 树由本文件构建（内容仍绑到同一个 DirNode/FileRow 模型）。
        DirTreeView.ItemsSource = null;
        DirTreeView.ItemTemplate = (DataTemplate)Resources["DirTreeRowTemplate"];
        RebuildTreeNodes();
        StartQaModelDumpIfRequested();   // ★ 只读模型 dump（PCMIG_STEP2_TREE_QA_DUMP=1 时启用）
        // ★ A7.1 ★ 「已有任务」已改为专用 Task Picker（Button + Bottom 锚定 Flyout + ListView）；
        //   原先为"修正原生 ComboBox 浮层位置"加的 DropDownOpened 校正补丁**已实测无效并删除**。
    }

    /// <summary>Shell 注入口令来源（Step 1 的口令框）。传 null 表示没有口令可复用。</summary>
    public void AttachPasswordProvider(Func<string?>? provider) => _passwordProvider = provider;

    public void ApplyState(PageReadiness state)
    {
        if (_state is not null) _state.PropertyChanged -= OnStateChanged;
        _state = state;
        _state.PropertyChanged += OnStateChanged;
        PushState();
    }

    /// <summary>Shell 注入会话状态源（Step2 的计划/进度/验证唯一来源）。</summary>
    public void AttachSession(MigrationSessionViewModel session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_session is not null)
        {
            _session.PropertyChanged -= OnSessionChanged;
            _session.Tree.Notice -= OnTreeNotice;
            _session.ExistingJobs.CollectionChanged -= OnExistingJobsChanged;
        }
        _session = session;
        _session.PropertyChanged += OnSessionChanged;
        _session.Tree.Notice += OnTreeNotice;
        // ★ A6 修复 ★ 见 OnExistingJobsChanged 的方法注释：投影必须跟随「已有任务」这个状态源。
        _session.ExistingJobs.CollectionChanged += OnExistingJobsChanged;
        RebuildTreeNodes();
        PushState();
        // ★ A7.1 ★ Task Picker 的展开列表**直接绑定真实 JobSummary 对象集合**（点谁就是谁，
        //   全程不用 index）；刷新由 ObservableCollection 自身驱动。
        ExistingJobsList.ItemsSource = session.ExistingJobs;
        RefreshExistingJobsOnce(session);
    }

    /// <summary>
    /// ★ A6 修复（2026-09-30，「已有任务」下拉为空的 P1）★ 投影跟随状态源。
    ///
    /// 缺陷：本页的「已有任务」当时是**代码投影**（由 <see cref="PushState"/> 驱动重建下拉项），
    /// 而状态源 <c>session.ExistingJobs</c> 只在 <c>RefreshExistingJobsAsync</c> 完成后被写入。
    /// 改前全仓只有 4 个刷新调用点（Prepare 之后 / 运行结束 / 验证之后 / 载入任务之后）——**没有任何一个
    /// 在启动或进入本页时触发**，且刷新是异步的（写集合发生在 PushState 之后）⇒ 集合被填满后
    /// **不会再有 PushState**，下拉因此长期停在 XAML 里硬编码的那一项。
    /// 实测（PCMIG_STEP2_JOBS_QA=1，磁盘上 8 个任务）：改前 `ExistingJobs=0`，改后 `ExistingJobs=8`。
    ///
    /// 修法（与 P0 目录树同一原则：一个状态源 + 一个写入路径 + 一个投影机制）：
    ///   · 状态源不变：<c>ExistingJobs</c>；
    ///   · 写入路径不变：<c>RefreshExistingJobsAsync</c>（唯一写入者）；
    ///   · 投影补上"变更即重推"：集合一变就重推 ⇒ 任何时机的刷新都会反映到下拉，不再依赖时序。
    /// 本类不新增第二套状态、不改任何业务规则、不写存档。
    ///
    /// ★ 合批（必须）★ <c>RefreshExistingJobsAsync</c> 是 `Clear()` + **逐项 Add** ⇒ 每个任务各触发
    /// 一次 CollectionChanged（实测 8 个任务 = 8 次）。若每次都直接 PushState，一轮刷新就会把下拉
    /// 重建 N 次（项对象分配 O(N²)，历史任务多时是可见的抖动）。与 P1-5 的 UiFlushPump 同一纪律：
    /// **一个待处理批次 + 一次 DispatcherQueue 回调** ⇒ 一轮刷新只重建一次。
    /// 这里天然正确：整轮 Add 都在同一个 Post 回调内同步完成，排队的 PushState 必然在它之后执行，
    /// 因此读到的是**最终**集合（不会停在中间态）。
    /// </summary>
    private void OnExistingJobsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_jobsPushPending) return;          // 已有待处理批次：合并掉这一次
        _jobsPushPending = true;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _jobsPushPending = false;
                PushState();
            }))
        {
            _jobsPushPending = false;           // 队列已关（关窗期）：丢弃这次投影，不影响业务
        }
    }

    /// <summary>合批标志（见 <see cref="OnExistingJobsChanged"/>）：同一轮刷新只排队一次重推。</summary>
    private bool _jobsPushPending;

    /// <summary>
    /// ★ A6 修复 ★ 补上"进入本页即刷新一次"的时机（与旧 WPF 壳在构造时调用 RefreshExistingJobs 等价）：
    /// 磁盘上的任务（含 CLI / 另一个实例刚创建的）在本页都必须能看到。只读枚举，不写存档。
    /// </summary>
    private void RefreshExistingJobsOnce(MigrationSessionViewModel session)
    {
        try { _ = session.RefreshExistingJobsAsync(); }
        catch { /* 刷新失败不影响本页其它功能：失败原因由 VM 侧写进 StatusMessage / 日志 */ }
    }

    /// <summary>
    /// 用目录树模型的根集合重建 TreeViewNode 树（节点模式）。
    /// 层级 = <see cref="TreeViewNode.Children"/>；节点的 <c>Content</c> 就是 <see cref="DirNode"/> /
    /// <see cref="FileRow"/> 模型本身，XAML 模板按内容类型呈现（三态勾选仍绑模型的只读投影）。
    /// 未加载的目录挂一个占位子节点，保证 TreeView 显示展开箭头（占位内容为 null）。
    /// </summary>
    private void RebuildTreeNodes()
    {
        DirTreeView.RootNodes.Clear();
        if (_session is null) return;
        foreach (var root in _session.Tree.RootNodes) DirTreeView.RootNodes.Add(BuildNode(root));
    }

    private static TreeViewNode BuildNode(DirNode model)
    {
        var node = new TreeViewNode { Content = model };
        // ★ A.5（P0-1）：展开 affordance 只看**"是否已加载"**（model.ChildrenLoaded），**不看 Children.Count**。
        //
        // ★ A.5（P0-4，2026-09-30 修复"子节点点了不展开"）★
        //   改前用**占位子节点**（`node.Children.Add(new TreeViewNode())`）表达"未加载"。
        //   实测证明这条路走不通（诊断日志 a5-expanding-trace.log）：
        //     · 有占位子节点 ⇒ TreeView 认为**子项已存在** ⇒ 展开时不走"延迟填充"语义；
        //     · 而本页的 Expanding 处理器是 async void，在 `await EnsureChildrenAsync(...)` 处
        //       立即返回 ⇒ TreeView 展开时子项还没到 ⇒ 展开空节点 ⇒ 状态错乱
        //       （日志实证：Expanding 被触发两次，第二次 uiChildren=0，最终 IsExpanded 回落为折叠）。
        //     · 对比：C$ 之所以正常，是因为 QA 取证路径在展开**之前**就 SyncNodeChildren 填好了子项。
        //   ⇒ 改用 WinUI 官方推荐的懒加载表达：**HasUnrealizedChildren = true**。
        //     TreeView 据此显示展开箭头、并在用户点击时触发 Expanding，且**允许应用异步填充**，
        //     展开状态不会被"空节点"回滚。真正加载完成后由 SyncNodeChildren 置回 false。
        //     真实空目录（加载后确实没有子项）⇒ Children 为空且 HasUnrealizedChildren=false ⇒
        //     **不留假箭头**（用户指定原则，P0-1 不回归）。
        if (!model.ChildrenLoaded) node.HasUnrealizedChildren = true;
        return node;
    }

    /// <summary>
    /// 把某一层模型节点的子项**整体重建**进对应的 TreeViewNode。
    ///
    /// ★ 职责边界（用户设计原则 6：把生命周期拆开）★ 本方法**只用于不处于展开生命周期中的场景**：
    ///   · 根集合重建（<see cref="RebuildTreeNodes"/> 路径）；
    ///   · QA 取证路径（<see cref="ExpandAllRootsForVerificationAsync"/>）。
    ///   **绝不用于用户点击展开的路径** —— 那里的 `Children.Clear()` 会打断 TreeView 的展开状态机。
    ///   展开路径一律走 <see cref="RealizeChildrenForExpansion"/>（只 Add 缺失项、不 Clear、不写 IsExpanded）。
    /// </summary>
    private void SyncNodeChildren(TreeViewNode node)
    {
        if (node.Content is not DirNode model) return;
        node.Children.Clear();
        foreach (var child in model.Children)
        {
            switch (child)
            {
                case DirNode d:
                    // ★ 2026-10-05 紧急修复（用户真机判词：展开 H 只看到一个可勾选的「…」）★
                    //   模型侧存在两类**非真实目录**的 DirNode：`AddDummy()` 的懒加载占位，
                    //   以及"文件过多仅显示前 N 个"的提示行 —— 它们的 `FullPath` 都为空串。
                    //   它们**绝不能**成为用户可见、可勾选的树节点（用户明确要求"不能再出现一个 ..."）。
                    //   箭头 affordance 由 <see cref="TreeViewNode.HasUnrealizedChildren"/> 负责，
                    //   不需要、也不允许再靠一个假子节点来表达。
                    if (d.FullPath.Length == 0) break;
                    node.Children.Add(BuildNode(d));
                    break;
                case FileRow f:
                    node.Children.Add(new TreeViewNode { Content = f });
                    break;
                default:
                    break;   // 模型侧只产出 DirNode / FileRow：绝不向 UI 插入"无模型空节点"
            }
        }
        // 未加载 ⇒ true（显示箭头；点击时触发 Expanding 走异步 realize）；
        // 已加载 ⇒ false（真实空目录因此不留假箭头 —— 用户指定原则）。
        node.HasUnrealizedChildren = !model.ChildrenLoaded;
    }

    /// <summary>
    /// Shell 注入导航状态源：**本页必须订阅它**。
    /// 原因（本包实测踩到）：页面在 Shell 构造期间就被注入状态，那时目录树还是空的（共享尚未勾选），
    /// 于是页面停在“空状态”分支；等用户切到本页、或树根建好之后，**没有任何东西会再触发一次
    /// PushState**，结果目录树已经就绪却仍显示空状态（截图实测：显示“请连接旧电脑后加载目录”）。
    /// 因此订阅导航变化：每次切到本页就重新推一次真实状态。
    /// </summary>
    public void AttachNavigation(StepNavigation nav)
    {
        ArgumentNullException.ThrowIfNull(nav);
        if (_nav is not null) _nav.Changed -= OnNavigated;
        _nav = nav;
        _nav.Changed += OnNavigated;
    }

    private StepNavigation? _nav;

    private void OnNavigated(StepKind from, StepKind to)
    {
        if (to != StepKind.SelectData) return;
        // 这里**只推状态、不重建树根**：树根的重建由“共享列表/勾选变化”驱动
        // （AttachConnection 的订阅）。若在这里也调 SyncTreeRootsFromShares，
        // 在“尚未勾选任何共享”时会按空集把已有根删掉——本包实测踩到过。
        PushState();
        // ★ A6 修复 ★ 每次切到本页都按磁盘真实情况刷一次「已有任务」（见 RefreshExistingJobsOnce）。
        // 与旧 WPF 壳"打开界面即刷新"同口径；刷新完成后由 OnExistingJobsChanged 自动重推下拉。
        if (_session is not null) RefreshExistingJobsOnce(_session);
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e) => PushState();

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e) => PushState();

    private void OnTreeNotice(string message)
    {
        // 目录读不全 / 目录文件极多等事实提示：写进页面状态句（不静默吞掉）。
        // 注意：树模型里的提示是**一次性**的（_bigDirHintShown / 每次展开各自判断），这里不做去重逻辑。
        if (!string.IsNullOrWhiteSpace(message)) StateMessageText.Text = message;
    }

    /// <summary>
    /// 把真实状态推给界面元素（本页唯一的 UI 更新入口）。
    /// 每个按钮的可用性都来自会话的真实布尔量：StartButton ← CanStart（= HasPlan 且有计划对象且非运行中）。
    /// </summary>
    private void PushState()
    {
        if (_state is null) return;
        StateLineText.Text = _state.Step2StateText;
        EmptyTitleText.Text = _state.Step2EmptyTitle;
        EmptyBodyText.Text = _state.Step2EmptyBody;

        var session = _session;
        if (session is null)
        {
            EmptyStatePanel.Visibility = Visibility.Visible;
            DirTreeView.Visibility = Visibility.Collapsed;
            return;
        }

        // ---- 目录区：空状态 / 目录树 二选一（互斥，不用转换器）----
        var hasTree = session.Tree.HasRoots;
        DirTreeView.Visibility = hasTree ? Visibility.Visible : Visibility.Collapsed;
        EmptyStatePanel.Visibility = hasTree ? Visibility.Collapsed : Visibility.Visible;
        // 节点树与模型的根集合保持一致（节点模式：层级不靠绑定，靠这里显式搬运）
        if (DirTreeView.RootNodes.Count != session.Tree.RootNodes.Count) RebuildTreeNodes();
        if (hasTree)
        {
            EmptyTitleText.Text = session.Tree.IsLoading ? "正在读取目录…" : "已加载目录";
            EmptyBodyText.Text = "在左侧树里勾选要迁移的目录或文件；勾选共享根 = 整盘迁移。";
            // ★ A.5 取证钩子（环境变量门控；不设该变量时生产路径逐位不变）★
            //   为什么需要：WinUI 的 TreeViewItem 展开按钮（chevron）**不响应 Win32 合成点击**
            //   （A.5 实测：像素级定位到 chevron 后，在 x=380/390/402/410 四点扫描全部无效，
            //   截图逐字节相同），而 UIA 注入在本项目历史上会让被测应用崩溃。
            //   因此取证时改用**项目既有的**取证装置 ExpandAllRootsForVerification()：
            //   它内部走的是与用户点箭头完全相同的 EnsureChildrenAsync + SyncNodeChildren 路径，
            //   只是由代码把 IsExpanded 置真。仅当 PCMIG_STEP2_TREE_QA_EXPAND 非空且非 "0" 时启用。
            //   触发条件：根集合发生变化（连接过程中共享是逐个/分批进来的，若只在第一次触发，
            //   就只会展开当时那一个根——A.5 第一次实测正是如此：日志里 7 个根只有第 1 个被处理过）
            //   且**仍有未加载的根**（已加载的根不再触发，避免反复读盘）。
            if (_qaExpandEnabled
                && session.Tree.RootNodes.Count > 0
                && session.Tree.RootNodes.Count != _qaExpandedAtRootCount)
            {
                _qaExpandedAtRootCount = session.Tree.RootNodes.Count;
                _ = QaExpandForEvidenceAsync();
            }
        }

        // ---- 执行卡：按钮可用性 100% 来自会话真实状态 ----
        PrepareButton.IsEnabled = !session.IsRunning;
        StartButton.IsEnabled = session.CanStart;      // plan.Objects.Count == 0 ⇒ HasPlan=false ⇒ 恒不可点
        PauseButton.IsEnabled = session.CanPause;
        StopButton.IsEnabled = session.CanStop;
        ResumeButton.IsEnabled = session.CanResume;

        // ---- 目标路径框：只在用户没在框里输入时同步（避免把光标/输入顶掉）----
        if (!TargetRootBox.FocusState.Equals(FocusState.Unfocused)) { /* 用户正在输入：不回写 */ }
        else if (!string.Equals(TargetRootBox.Text, session.TargetRoot, StringComparison.Ordinal))
        {
            _suppressSync = true;
            TargetRootBox.Text = session.TargetRoot;
            _suppressSync = false;
        }

        // ---- 线程数下拉：选项来自会话（自动（推荐）/ 4 / 8 / 16 / 32 / 64 / 128；自动写盘为 16）；
        //      任务原值（含旧任务的 32/64/128）会动态追加并只读显示 ----
        SyncThreadsCombo(session);

        // ---- 已有任务 Task Picker：收起态显示（DisplayedItem ≠ 已载入任务）+ 展开列表绑定真实 JobSummary ----
        UpdateExistingJobsPicker(session);
        QaShowJobsPickerIfRequested();   // ★ A7.1 取证（环境变量门控；生产路径不执行）

        // ---- 状态句：★ FIX BATCH 6（§9）★ 右侧执行卡只显示**短句**（ExecutionStatus：
        //      第一句 + 限长 + 单行截断），完整状态句归左侧提示面板；
        //      长文本从此不再把执行卡无限撑高（真机 P2-G）。
        //      完整文本另挂在 ToolTip 上，用户悬停仍能看到全部内容。----
        if (!string.IsNullOrWhiteSpace(session.ExecutionStatus))
        {
            StateMessageText.Text = session.ExecutionStatus;
            ToolTipService.SetToolTip(StateMessageText, string.IsNullOrWhiteSpace(session.StatusMessage)
                ? session.ExecutionStatus
                : session.StatusMessage);
        }

        // ---- 连接与安全提示卡：只有 SecurityHint 通道的内容进这里（§9 映射）----
        if (!string.IsNullOrWhiteSpace(session.SecurityHint)) SecurityHintText.Text = session.SecurityHint;
    }

    // ────────────────────────── 目标路径 ──────────────────────────

    private void TargetRootBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSync || _session is null) return;
        _session.TargetRoot = TargetRootBox.Text;
    }

    /// <summary>
    /// 「浏览」：弹出标准"选择文件夹"对话框，选中结果**同时**写回会话与输入框。
    ///
    /// ★ 2026-09-30 修复（用户实测反馈：「浏览按钮点了没反应、看上去像个假的」）★
    ///   根因：本应用是 **unpackaged**（`PCMig.WinUI.csproj` 里 `WindowsPackageType = None`），
    ///   而 `Windows.Storage.Pickers.FolderPicker.PickSingleFolderAsync()` 在 unpackaged WinUI 3 里
    ///   会**静默返回 null**（既不弹窗也不抛异常）⇒ 界面表现就是"点了毫无反应"。
    ///   ⇒ 改用 Win32 原生 `IFileOpenDialog` + `FOS_PICKFOLDERS`（见 <see cref="FolderPickerInterop"/>），
    ///     它不依赖 MSIX 打包上下文，任何 Win32/WinUI 进程都能正常弹出标准选择框。
    ///   同时保证：选中后**写回输入框**（此前用户担心"即便弹出了也不会同步到输入框"）。
    /// </summary>
    private void BrowseTarget_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var owner = App.MainWindowInstance is null
                ? System.IntPtr.Zero
                : WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);

            // 初始定位：优先会话里的目标，其次输入框当前内容（都不合法时对话框自己忽略它）
            var seed = !string.IsNullOrWhiteSpace(_session?.TargetRoot)
                ? _session!.TargetRoot
                : (TargetRootBox.Text ?? string.Empty);

            var picked = FolderPickerInterop.PickFolder(owner, "选择目标文件夹", seed);
            if (string.IsNullOrWhiteSpace(picked)) return;   // 用户取消 / 失败：静默返回，不打扰

            if (_session is not null) _session.TargetRoot = picked;
            _suppressSync = true;
            TargetRootBox.Text = picked;                     // ★ 关键：同步回输入框
            _suppressSync = false;
            StateMessageText.Text = $"目标位置已选：{picked}";
        }
        catch (Exception ex)
        {
            StateMessageText.Text = $"选择目标目录失败：{ex.Message}（可直接在框里手打路径）";
        }
    }

    // ────────────────────────── 线程数 / 已有任务 ──────────────────────────

    private void SyncThreadsCombo(MigrationSessionViewModel session)
    {
        // 选项集合已变化（恢复既有任务时旧值会被追加）→ 重建 ComboBoxItem
        var wanted = session.MtOptions.ToList();
        var existing = ThreadsCombo.Items.OfType<ComboBoxItem>().Select(i => (string)i.Content).ToList();
        var wantedTexts = wanted.Select(session.MtTextFor).ToList();
        if (!existing.SequenceEqual(wantedTexts, StringComparer.Ordinal))
        {
            _suppressSync = true;
            ThreadsCombo.Items.Clear();
            foreach (var t in wantedTexts)
            {
                var item = new ComboBoxItem { Content = t };
                if (t == session.MtTextFor(session.SelectedMt)) item.IsSelected = true;
                ThreadsCombo.Items.Add(item);
            }
            if (ThreadsCombo.SelectedIndex < 0) ThreadsCombo.SelectedIndex = 0;
            _suppressSync = false;
        }
        QaOpenThreadsDropIfRequested();   // ★ A7 取证（环境变量门控；生产路径不执行）
    }

    /// <summary>程序化展开「线程数」下拉的一次性开关（见 <see cref="QaOpenThreadsDropIfRequested"/>）。</summary>
    private bool _threadsDropQaDone;

    /// <summary>
    /// ★ A7 取证（**仅环境变量门控**，生产路径不可达）★ 程序化把「线程数」下拉展开。
    ///
    /// 为什么需要：WinUI 的 ComboBox Popup **不响应合成输入**（A6 已实测：左键、Alt+Down、F4 全都不开），
    /// 而本轮要取证"Popup 前景文字是否比主界面发糊"（B2），必须让下拉真的处于展开态才能原生截屏。
    /// 本探针只设置 `IsDropDownOpen = true`，不改任何线程档位/材质/模板。`PCMIG_STEP2_THREADS_QA_OPEN=1`。
    /// </summary>
    private void QaOpenThreadsDropIfRequested()
    {
        if (_threadsDropQaDone) return;
        // ★ A7 取证扩展：PCMIG_STEP2_JOBS_QA_OPEN=1 时打开的是「已有任务」下拉（位置取证用）
        var jobsCombo = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PCMIG_STEP2_JOBS_QA_OPEN"));
        if (!jobsCombo && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PCMIG_STEP2_THREADS_QA_OPEN"))) return;
        _threadsDropQaDone = true;
        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "a7-threads-qa.log");
            void Trace(string m)
            {
                try { File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} {m}{Environment.NewLine}"); } catch { }
            }
            try
            {
                await Task.Delay(700);
                if (jobsCombo) { Trace("jobs picker handled by QaShowJobsPickerIfRequested"); return; }
                var target = ThreadsCombo;
                Trace($"driver fired: target=ThreadsCombo " +
                      $"IsLoaded={target.IsLoaded} IsDropDownOpen={target.IsDropDownOpen} " +
                      $"UniformScaleHost.IsActive={PCMig.WinUI.Presentation.UniformScaleHost.IsActive} " +
                      $"AppliedScale={PCMig.WinUI.Presentation.UniformScaleHost.AppliedScale:0.#####} " +
                      $"XamlRootSize={(XamlRoot is null ? "-" : $"{XamlRoot.Size.Width:0.##}x{XamlRoot.Size.Height:0.##}")} " +
                      $"RasterizationScale={(XamlRoot is null ? 0 : XamlRoot.RasterizationScale):0.###}");
                // 持续保持展开：单选一次会立刻被 light-dismiss/清项关掉（实测），截图根本抓不到。
                for (var i = 0; i < 40; i++)
                {
                    await Task.Delay(250);
                    target.IsDropDownOpen = true;
                    await Task.Delay(500);
                    if (i % 8 == 0) Trace($"keep #{i}: IsDropDownOpen={target.IsDropDownOpen}");
                }
                Trace("keep loop done");
            }
            catch (Exception ex) { Trace("EX " + ex.Message); }
        });
    }

    private void ThreadsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSync || _session is null) return;
        if (ThreadsCombo.SelectedItem is not ComboBoxItem item) return;
        _session.SelectedMt = _session.MtFromText(item.Content as string);
    }

    // ────────────────────────── 已有任务 Task Picker（A7.1）──────────────────────────

    /// <summary>
    /// ★ A7.1（2026-09-30，用户裁决）★ Task Picker 收起态的**显示**与**已载入任务**严格分开：
    ///   · 有 CurrentJob ⇒ 显示 CurrentJob（它确实已载入）；
    ///   · 没有 CurrentJob 但有历史任务 ⇒ 显示**第一条真实任务作为 preview**（只是预览，**不等于已载入**）；
    ///   · 一个任务都没有 ⇒ 显示空状态占位。
    /// 这里**绝不写 SelectedIndex、也不使用任何 index**：不存在"用选中态伪造预览"的做法，
    /// 因此用户随后点击任意一项（**包括第一条**）都必然触发 ItemClick。
    /// 展开态的列表项直接绑定真实 <c>JobSummary</c> 对象（ItemsSource = session.ExistingJobs）。
    /// </summary>
    private void UpdateExistingJobsPicker(MigrationSessionViewModel session)
    {
        var jobs = session.ExistingJobs;
        JobsQaTrace(session);
        var currentId = session.Ctx?.JobId;
        JobSummary? shown = null;
        if (!string.IsNullOrWhiteSpace(currentId))
            shown = jobs.FirstOrDefault(j => string.Equals(j.JobId, currentId, StringComparison.OrdinalIgnoreCase));
        shown ??= jobs.FirstOrDefault();                 // 没有当前任务 ⇒ 第一条真实任务作预览（不载入）
        ExistingJobsPreviewText.Text = shown is null
            ? "（暂无任务）"
            : $"{shown.JobId}｜{shown.PhaseText}｜{shown.Percent:0.0}%";
        QaSelectJobIfRequested(session);                 // ★ A7.1 取证（环境变量门控；按 JobId，不用 index）
    }

    /// <summary>
    /// 收起态 Presenter 点击。Flyout 由 <c>Button.Flyout</c> **自动**展开，
    /// 其 <c>Placement="BottomEdgeAlignedLeft"</c> ⇒ 浮层顶边始终从本控件底边向下展开
    /// （框架正常锚定，**没有**魔法 VerticalOffset / 负 Margin / TranslateY / 运行后校正坐标）。
    /// </summary>
    private void ExistingJobsPickerButton_Click(object sender, RoutedEventArgs e)
    {
        AlignExistingJobsFlyoutWidth();
        PickerQaTrace($"picker opened (listItems={ExistingJobsList.Items.Count})");
    }

    /// <summary>
    /// ★ UI Closure（2026-10-05，用户人工标注项）★ 浮层几何对齐：
    /// 「已有任务」下拉展开后，面板**内容边界**必须与锚点控件（收起态 Presenter）
    /// 的左右几何边界一致；阴影允许视觉外溢，但不计入内容边界。
    /// <para>
    /// 实现口径：FlyoutPresenter 自带 <c>Padding=2</c> + <c>BorderThickness=1</c>
    /// ⇒ 水平方向比内容多 6 DIP，故 <c>ListView.Width = 锚点 ActualWidth - 6</c>，
    /// 使浮层外边界 == 锚点边界。宽度每次展开时按真实布局重算（DPI / 窗口宽度变化自适应），
    /// **不使用**固定宽度、负 Margin 或 XAML 魔法偏移（PMML-R10 / R12）。
    /// </para>
    /// </summary>
    private void AlignExistingJobsFlyoutWidth()
    {
        var anchorWidth = ExistingJobsPickerButton.ActualWidth;
        if (anchorWidth <= 0) return;

        const double flyoutChrome = 6d; // FlyoutPresenter Padding 2×2 + BorderThickness 1×2
        var listWidth = Math.Max(180d, anchorWidth - flyoutChrome);
        ExistingJobsList.Width = listWidth;
        PickerQaTrace($"picker width aligned: anchor={anchorWidth:F1} list={listWidth:F1}");
    }

    /// <summary>
    /// ★ A7.1 关键收口 ★ 点击项**直接携带 JobSummary 对象**：
    /// clicked item → JobSummary → JobDir → 既有 <c>AdoptExistingJobAsync</c>。
    /// **零 idx 偏移**：排序、刷新、插入新任务都不会让"点谁"变成"载入别人"。
    /// </summary>
    private async void ExistingJobsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (_session is null) return;
        if (e.ClickedItem is not JobSummary job)
        {
            PickerQaTrace("click ignored: clicked item is not a JobSummary");
            return;
        }
        PickerQaTrace($"click JobId={job.JobId} (listIndex={_session.ExistingJobs.IndexOf(job)})");
        try
        {
            await _session.AdoptExistingJobAsync(job.JobDir);
            PickerQaTrace($"adopted: clicked={job.JobId}  current={_session.Ctx?.JobId}  " +
                          $"match={string.Equals(job.JobId, _session.Ctx?.JobId, StringComparison.OrdinalIgnoreCase)}");
        }
        catch (Exception ex) { PickerQaTrace("adopt EX " + ex.Message); }
        finally { PushState(); }
    }

    /// <summary>
    /// ★ A7.1 取证（**仅环境变量门控**，生产路径不可达）★ 按 **JobId 找到 JobSummary 对象**，
    /// 再走与点击**完全相同**的对象路径（<c>AdoptExistingJobAsync</c>）——**不使用任何 index**，
    /// 避免"产品已改成对象选择、QA 还按旧索引测试然后给出假通过"。
    /// 环境变量：<c>PCMIG_STEP2_JOBS_QA_SELECT=&lt;JobId&gt;</c>。
    /// </summary>
    private async void QaSelectJobIfRequested(MigrationSessionViewModel session)
    {
        if (_jobsQaSelectDone) return;
        var wanted = Environment.GetEnvironmentVariable("PCMIG_STEP2_JOBS_QA_SELECT");
        if (string.IsNullOrWhiteSpace(wanted)) return;
        var job = session.ExistingJobs.FirstOrDefault(j => string.Equals(j.JobId, wanted, StringComparison.OrdinalIgnoreCase));
        if (job is null) return;                         // 目标还没出现在列表里：等下一次刷新
        _jobsQaSelectDone = true;
        PickerQaTrace($"QA select by JobId={job.JobId} (no index math)");
        try
        {
            await session.AdoptExistingJobAsync(job.JobDir);
            PickerQaTrace($"QA adopted: wanted={wanted}  current={session.Ctx?.JobId}");
        }
        catch (Exception ex) { PickerQaTrace("QA adopt EX " + ex.Message); }
        PushState();
    }

    /// <summary>Task Picker 取证的一次性开关（见 <see cref="QaShowJobsPickerIfRequested"/>）。</summary>
    private bool _jobsPickerQaShown;

    /// <summary>
    /// ★ A7.1 取证（环境变量门控）★ <c>PCMIG_STEP2_JOBS_QA_OPEN</c> 非空时程序化展开 Task Picker 的 Flyout，
    /// 并**持续保持展开**（单选一次可能被 light-dismiss 关掉，截图抓不到）。位置取证用。
    /// </summary>
    private void QaShowJobsPickerIfRequested()
    {
        if (_jobsPickerQaShown) return;
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PCMIG_STEP2_JOBS_QA_OPEN"))) return;
        _jobsPickerQaShown = true;
        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await Task.Delay(700);
                for (var i = 0; i < 40; i++)
                {
                    ExistingJobsPickerButton.Flyout?.ShowAt(ExistingJobsPickerButton);
                    if (i % 8 == 0) PickerQaTrace($"QA flyout shown #{i}");
                    await Task.Delay(400);
                }
            }
            catch (Exception ex) { PickerQaTrace("QA show EX " + ex.Message); }
        });
    }

    /// <summary>Task Picker 取证日志（PCMIG_STEP2_JOBS_QA / _OPEN / _SELECT 任一非空时写 a7-picker-qa.log）。</summary>
    private static void PickerQaTrace(string message)
    {
        var on = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PCMIG_STEP2_JOBS_QA"))
              || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PCMIG_STEP2_JOBS_QA_OPEN"))
              || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PCMIG_STEP2_JOBS_QA_SELECT"));
        if (!on) return;
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "a7-picker-qa.log"),
                $"{DateTime.Now:HH:mm:ss.fff} [TaskPicker] {message}{Environment.NewLine}");
        }
        catch { /* 取证失败绝不影响业务 */ }
    }

    /// <summary>
    /// ★ A7.1 取证（只读）★ 记录**真实状态源**与**真实投影**：模型任务数、ListView 实际项数、
    /// 当前已载入 JobId、收起态显示的那一条、以及首条任务。仅 PCMIG_STEP2_JOBS_QA 非空且非 "0" 时写盘。
    /// </summary>
    private void JobsQaTrace(MigrationSessionViewModel session)
    {
        if (!_jobsQaEnabled) return;
        var listCount = ExistingJobsList.Items.Count;
        if (_jobsQaLast == (session.ExistingJobs.Count, listCount)) return;
        _jobsQaLast = (session.ExistingJobs.Count, listCount);
        try
        {
            var line = string.Format(
                "{0:HH:mm:ss.fff}  session.ExistingJobs={1}  list.Items={2}  current={3}  preview=[{4}]  first=[{5}]",
                DateTime.Now, session.ExistingJobs.Count, listCount, session.Ctx?.JobId ?? "-",
                ExistingJobsPreviewText.Text,
                session.ExistingJobs.FirstOrDefault()?.JobId ?? "-");
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "a6-jobs-qa.log"), line + Environment.NewLine);
        }
        catch { /* 取证失败绝不影响业务 */ }
    }

    // ────────────────────────── 目录树 ──────────────────────────

    /// <summary>
    /// 节点展开（**用户点击路径**）。设计原则（2026-09-30 按用户口径重写）：
    ///   · **不做预取**：点哪一级就只读哪一级，不递归、不预读兄弟/孙节点（真 Lazy Loading）；
    ///   · **不争夺 `IsExpanded`**：展开状态由 TreeView 自己拥有，本路径**绝不写 `node.IsExpanded`**
    ///     （只有 QA/程序主动展开场景才显式控制，见 <see cref="ExpandAllRootsForVerification"/>）；
    ///   · **不 `Clear()` 正在展开节点的 Children**：改为 realization（只 Add 缺失项，
    ///     见 <see cref="RealizeChildrenForExpansion"/>）；
    ///   · `HasUnrealizedChildren` 生命周期：未加载=true；加载成功且已 materialize=false；
    ///     失败/取消 ⇒ 保持 true 且 `ChildrenLoaded=false` ⇒ 下次点击可重试。
    ///
    /// ★ 更正记录（主控自我更正）★ 此前把本缺陷定性为"WinUI Expanding 要求同步填充，异步读盘天然不可行"
    ///   —— **该定性错误**。Microsoft 官方 TreeView Lazy Loading 示例本身就是异步 `FillTreeNode`：
    ///   `HasUnrealizedChildren=true` → `Expanding` → `await` 读盘 → `Add Children` → `HasUnrealizedChildren=false`。
    ///   真正打断 TreeView 展开状态机的，是本页自己做的三件事：`Children.Clear()` 整体重建、
    ///   `HasUnrealizedChildren` 抖动、以及在异步完成后反复人工写 `IsExpanded=true`。本次全部移除。
    /// </summary>
    private void DirTreeView_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        TraceExpand("before Expanding", args.Node);
        if (_session is null || args.Node is not { } node) return;
        if (node.Content is not DirNode model) return;   // 只对目录行懒加载

        if (model.ChildrenLoaded)
        {
            // 已加载过（折叠后再展开）：同步 realize 即可，TreeView 立刻看得到内容。
            RealizeChildrenForExpansion(node, model);
            return;
        }

        // 未加载：保持"可能有未实现的子项"，异步读盘（官方 Lazy Loading 的正常路径）。
        node.HasUnrealizedChildren = true;
        _ = RealizeChildrenAsync(node, model);
    }

    /// <summary>
    /// 异步 realize：真实读盘 → 把子项 Add 进**正在展开的**节点。
    /// **不 `Clear()`、不写 `IsExpanded`**（理由见 <see cref="DirTreeView_Expanding"/>）。
    /// 失败/取消 ⇒ `ChildrenLoaded` 仍为 false ⇒ `HasUnrealizedChildren` 保持 true ⇒ 下次点击可重试。
    /// </summary>
    private async Task RealizeChildrenAsync(TreeViewNode node, DirNode model)
    {
        try
        {
            if (_session is null) return;
            await _session.Tree.EnsureChildrenAsync(model);
            TraceExpand($"after EnsureChildrenAsync(model={model.Children.Count}, loaded={model.ChildrenLoaded})", node);
            RealizeChildrenForExpansion(node, model);
            TraceExpand("after realize", node);
            // 用户原则 7：展开后 100ms / 500ms 再各记一次真实状态
            //（用来见证 TreeView 自己是否保持展开、Children 是否留存）。
            _ = TraceLaterAsync(node, 100);
            _ = TraceLaterAsync(node, 500);
        }
        catch (Exception ex) { TraceExpand($"realize-failed {ex.GetType().Name}:{ex.Message}", node); }
    }

    private async Task TraceLaterAsync(TreeViewNode node, int ms)
    {
        try { await Task.Delay(ms); TraceExpand($"after handler+{ms}ms", node); } catch { }
    }

    /// <summary>
    /// ★ realization（**用户展开路径专用**）★
    /// 把 `model.Children` 里**尚未 materialize 到 UI** 的项 Add 进去。纪律：
    ///   · **绝不 `Children.Clear()`** —— 整体重建会打断 TreeView 的展开状态机；
    ///   · **绝不写 `IsExpanded`** —— 展开状态归 TreeView 所有，我们只负责提供 Children；
    ///   · 用 `Content` 的**引用标识**去重 ⇒ 重复调用不会产生重复行；
    ///   · 末尾落 `HasUnrealizedChildren`：加载成功=false（真实空目录 ⇒ `Children.Count=0` + false
    ///     ⇒ 箭头自然消失）；失败/作废 ⇒ 仍为 true 且 `ChildrenLoaded=false` ⇒ 箭头还在、可重试。
    /// </summary>
    private void RealizeChildrenForExpansion(TreeViewNode node, DirNode model)
    {
        var existing = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var c in node.Children) if (c.Content is { } cc) existing.Add(cc);

        foreach (var child in model.Children)
        {
            switch (child)
            {
                case DirNode d:
                    // ★ 2026-10-05 紧急修复 ★ 与 SyncNodeChildren 同一纪律：`FullPath` 为空的
                    //   占位/提示 DirNode 永不 materialize 到 UI（用户真机看到的可勾选「…」就是它）。
                    if (d.FullPath.Length == 0) continue;
                    if (existing.Contains(d)) continue;
                    node.Children.Add(BuildNode(d));
                    break;
                case FileRow f:
                    if (existing.Contains(f)) continue;
                    node.Children.Add(new TreeViewNode { Content = f });
                    break;
                default:
                    // ★ 2026-10-05 ★ 绝不再插入"无模型空节点"：它会在树上渲染成一行空白/假行，
                    //   而它对用户没有任何意义（本页只呈现 DirNode / FileRow）。
                    break;
            }
        }

        node.HasUnrealizedChildren = !model.ChildrenLoaded;
    }

    /// <summary>
    /// 展开路径诊断日志（只在设置了 QA 门控时写文件，生产零开销）。
    /// 字段：节点名 / 模型是否已加载 / 模型子项数 / UI 子项数 / **UI 的 IsExpanded** / UI 的 unrealized 标记。
    /// </summary>
    private void TraceExpand(string tag, TreeViewNode? node)
    {
        if (!_qaExpandEnabled) return;
        try
        {
            var m = node?.Content as DirNode;
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppContext.BaseDirectory, "a5-expanding-trace.log"),
                $"{DateTime.Now:HH:mm:ss.fff} [{tag}] name={m?.Name} loaded={m?.ChildrenLoaded} " +
                $"modelChildren={m?.Children.Count} uiChildren={node?.Children.Count} " +
                $"uiIsExpanded={node?.IsExpanded} uiHasUnrealized={node?.HasUnrealizedChildren}{Environment.NewLine}");
        }
        catch { /* 诊断不得影响业务 */ }
    }

    // ★ A.5（P0-4）：**预取方案已整体删除**（2026-09-30，按用户设计原则 1）。
    //   用户明确要求：不做预取 —— 点哪一级就只读哪一级，不递归、不预读兄弟/孙节点。
    //   且该方案实测引入过无限递归（把应用打崩）与"C$ 展开消失"的副作用。
    //   现由 <see cref="DirTreeView_Expanding"/> + <see cref="RealizeChildrenForExpansion"/> 承担，
    //   走官方 Lazy Loading 语义（HasUnrealizedChildren + 异步 realize）。

    // ★ A.5（P0 目录树选择状态收敛，2026-09-30）★ NodeCheck_Click 已整体删除（按用户裁决）。
    //   原因：CheckBox 现为 TwoWay 绑定 Content.IsChecked —— 绑定本身就是唯一的模型写入路径。
    //   若再保留 Click 处理器执行 ToggleFromUi()，就会形成第二套写入 => 双重 Toggle、点一次弹回、
    //   一次点击模型变化两次、点击顺序依赖等竞态（这正是点一次不生效、要点 2-3 次的成因之一）。
    //   收敛后：用户点击 -> TwoWay 绑定写模型 -> 模型级联/父链回卷 -> PropertyChanged -> 绑定投影回 UI。
    //   半选（IsChecked==null）仍由模板里的互斥方块做纯视觉投影（IsHitTestVisible=False）。

    // ────────────────────────── 预检并生成计划 ──────────────────────────

    private async void Prepare_Click(object sender, RoutedEventArgs e)
    {
        var session = _session;
        // ★ D6.1 §11 观察点 ★ 只记录**原有判断的结果**，不改任何业务分支。
        using var trace = ActionTrace.Begin(ActionKinds.Prepare, ControlIds.Step2Prepare, "click", "Step2SelectDataPage");
        trace.Eligibility(session is not null, session is not null ? "allowed" : "no-session");
        if (session is null) { trace.Reject("no-session", "Step2SelectDataPage"); return; }

        // 目标根以界面为准（用户在框里改过但没离开焦点时也算数）
        session.TargetRoot = TargetRootBox.Text.Trim();

        // 源侧：Step1 勾选的共享（整盘）+ 目录树勾选（customs 恒生效）
        var shares = CollectSelectedShares();
        var customs = session.Tree.CollectCustomSelections();
        if (shares.Count == 0 && customs.Count == 0)
        {
            StateMessageText.Text = "请先在 Step 1 勾选要迁移的共享，或在目录树里勾选要迁移的内容。";
            trace.Reject("no-selection", "Step2SelectDataPage");            // ← 只加这一行观察
            return;
        }
        if (string.IsNullOrWhiteSpace(session.TargetRoot))
        {
            StateMessageText.Text = "请先填写目标路径（新电脑接收数据的位置）。";
            trace.Reject("no-target-root", "Step2SelectDataPage");          // ← 只加这一行观察
            return;
        }
        trace.Started();

        // ---- 未完成任务检测（同主机+同目标）→ 询问续传或新建（同旧 WPF 706-725 语义）----
        var decision = MigrationSessionViewModel.ResumeDecision.CreateNew;
        var dup = await session.FindUnfinishedForPrepareAsync(session.Host, session.TargetRoot);
        if (dup is not null)
        {
            var resume = await AskResumeAsync(dup);
            if (resume is null) return;   // 用户取消
            if (resume == true)
            {
                await session.AdoptExistingJobAsync(dup.JobDir);
                PushState();
                return;
            }
            decision = MigrationSessionViewModel.ResumeDecision.CreateNew;
        }

        var definition = new JobDefinition
        {
            SourceHost = session.Host,
            SourceUser = string.IsNullOrWhiteSpace(session.Username) ? null : session.Username,
            TargetRoot = session.TargetRoot,
            Sources = BuildSources(shares, customs),
            CustomSelections = customs,      // ★ 目录树勾选始终生效（ExpertMode 阶段 A 不接线）
            CreatedBy = $"{Environment.UserDomainName}\\{Environment.UserName}",
        };

        _incompleteScanConfirmed = false;
        var ok = await session.PrepareAsync(
            definition,
            _passwordProvider?.Invoke(),
            progress: new Progress<string>(m => StateMessageText.Text = m),
            ct: default,
            confirmIncompleteScan: ConfirmIncompleteScanAsync,
            resumeDecision: decision);

        // ★ R-3 收口（D6.3 剩余风险关闭轮）★ 旧判据把「session 的 Ctx 是否为空」当成了"预检是否成功"的替身：
        //   只要上一次预检留下过 Ctx，本次真实失败（未勾共享 / 目标非法 / 取消 / 预检未通过 /
        //   扫描残缺闸门拦下）也会写成 UI-007 `Succeeded`。动作的成功必须来自**本次调用自己的真实
        //   结果**，因此判据只看 `ok`（同名判据由 D61ActionCoverageTests 的 R-3 夹具守住）。
        if (!ok)
        {
            // 闸门拦下或预检失败：状态句已由会话写清，这里只把界面刷新到真实状态
            trace.Complete(DiagnosticOutcome.Failed, "prepare-rejected");
            PushState();
            return;
        }
        trace.Complete(DiagnosticOutcome.Succeeded, "prepare-completed");
        PushState();
    }

    /// <summary>Step1 已勾选的共享（整盘迁移口径；唯一来源是注入的 ConnectionViewModel）。</summary>
    private List<string> CollectSelectedShares()
    {
        var conn = _shellConnection;
        if (conn is null) return new List<string>();
        return conn.Shares.Where(s => s.IsSelected).Select(s => s.UncPath).ToList();
    }

    private ConnectionViewModel? _shellConnection;

    /// <summary>
    /// Shell 注入连接状态源（只读投影：取“已勾选的共享”，并把共享列表变化同步进目录树根）。
    /// 订阅是**幂等**的（重复注入不会叠加）：先退订旧实例。
    /// </summary>
    public void AttachConnection(ConnectionViewModel connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (_shellConnection is not null)
        {
            _shellConnection.Shares.CollectionChanged -= OnSharesChanged;
            foreach (var s in _shellConnection.Shares) s.PropertyChanged -= OnShareItemChanged;
        }
        _shellConnection = connection;
        _shellConnection.Shares.CollectionChanged += OnSharesChanged;
        foreach (var s in _shellConnection.Shares) s.PropertyChanged += OnShareItemChanged;

        // ★ A.5（C2）★ **连接已重新建立/换主机**的最强锚点（幂等退订+重订就是那个真实时刻）：
        //   作废**在途**的懒加载结果，否则旧主机的 UNC 枚举结果会被画到新主机的树上。
        //   ⚠ 只允许两处调用点：这里（连接重建）与 DirectoryTreeViewModel.SyncRoots（真的增/删根时）。
        //   **禁止**加在 OnShareItemChanged：那会让"用户每勾一个共享就作废一次"，与 R19/C8 的
        //   验收标准（反复勾/取消共享**不得**触发新读盘）直接冲突。
        //   ⚠ 范围边界：作废只对"在途（尚未成功）"的加载有效；已成功加载过的节点不会被重置
        //   ChildrenLoaded ⇒「换主机后让已加载的树重读」不在本修复范围内。
        _session?.Tree.InvalidateInFlightBrowses();

        _session?.SyncTreeRootsFromShares();
        PushState();
    }

    private void OnSharesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_shellConnection is not null)
        {
            foreach (var s in _shellConnection.Shares) s.PropertyChanged -= OnShareItemChanged;
            foreach (var s in _shellConnection.Shares) s.PropertyChanged += OnShareItemChanged;
        }
        _session?.SyncTreeRootsFromShares();
        PushState();
    }

    private void OnShareItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShareItem.IsSelected)) return;
        _session?.SyncTreeRootsFromShares();
        PushState();
    }

    /// <summary>共享根涉及的共享根集合（供 Planner 做相对路径映射与 Preflight 检查）。</summary>
    private List<SourceSpec> BuildSources(List<string> wholeShares, List<string> customs)
    {
        var sources = wholeShares
            .Select(s => new SourceSpec { Path = s, Kind = ObjectKind.DataVolume })
            .ToList();
        foreach (var root in customs.Select(_session!.Tree.FindShareRootOf)
                     .Where(r => r != null).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!sources.Any(s => string.Equals(s.Path, root, StringComparison.OrdinalIgnoreCase)))
                sources.Add(new SourceSpec { Path = root!, Kind = ObjectKind.DataVolume });
        }
        return sources;
    }

    /// <summary>发现未完成任务：询问续传或新建。返回 null = 用户取消（不新建、不续传）。</summary>
    private async Task<bool?> AskResumeAsync(JobSummary dup)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "发现未完成任务",
            Content = $"该源电脑和目标路径下已有未完成的任务：\n\n任务：{dup.JobId}\n" +
                      $"进度：{(dup.StateUnreliable ? "状态文件损坏，未知（以回执为准）" : $"已传 {dup.Percent:0.0}%")}\n" +
                      $"状态：{dup.PhaseText}\n\n" +
                      "「继续上次任务」= 已完成部分不重传；「创建全新任务」= 另建一个任务（既有任务存档不改动）。",
            PrimaryButtonText = "继续上次任务",
            SecondaryButtonText = "创建全新任务",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        var r = await dialog.ShowAsync();
        return r switch
        {
            ContentDialogResult.Primary => true,
            ContentDialogResult.Secondary => false,
            _ => (bool?)null,
        };
    }

    /// <summary>
    /// 扫描残缺闸门确认（同旧 WPF ConfirmIncompleteScan 语义，**不可简化**）：
    /// 默认「否」（先不迁移）；点「是」才把 allowIncompleteScan 写进 job.json 并继续。
    /// 同一次点击的重跑不再二次询问（用户已经明确选过）。
    /// </summary>
    private async Task<bool> ConfirmIncompleteScanAsync()
    {
        if (_incompleteScanConfirmed) return true;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "扫描不完整：默认不允许开始迁移",
            Content = "扫描存在不可访问的目录/文件——这些位置里的文件既不在计划内、也不会被复制；" +
                      "事后报告只会显示“完成”。\n\n" +
                      "「否」= 先不迁移（推荐：修好权限/网络后重新「预检并生成计划」）；\n" +
                      "「是」= 我已知风险，仍要继续（会写入任务定义，后续续传不再拦）。\n\n" +
                      "明细已逐条列进失败清单（对象｜路径｜原因）。",
            PrimaryButtonText = "是（我已知风险仍要继续）",
            CloseButtonText = "否（先不迁移）",
            DefaultButton = ContentDialogButton.Close,
        };
        var r = await dialog.ShowAsync();
        _incompleteScanConfirmed = r == ContentDialogResult.Primary;
        return _incompleteScanConfirmed;
    }

    // ────────────────────────── 开始 / 暂停 / 停止 / 恢复 ──────────────────────────

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        // ★ D6.1 §11 ★ 观察既有 guard（CanStart）的结果，不新增判据、不改分支。
        using var trace = ActionTrace.Begin(ActionKinds.Start, ControlIds.Step2Start, "click", "Step2SelectDataPage");
        trace.Eligibility(_session.CanStart, _session.CanStart ? "allowed" : "cannot-start");

        if (!_session.CanStart)
        {
            StateMessageText.Text = _session.HasPlan
                ? "还不能开始迁移：请先指定目标路径。"
                : "计划里没有任何对象：不能开始迁移（请先「预检并生成计划」并确认计划里有内容）。";
            trace.Reject("cannot-start", "Step2SelectDataPage");
            return;
        }
        trace.Started();
        trace.Expect("transfer.v1", "start.external");
        await _session.RunAsync(_passwordProvider?.Invoke(), new Progress<string>(m => StateMessageText.Text = m));
        trace.Confirm("transfer.v1", "start.external");
        // D6.3 §11：真实业务结果 = 既有 JobPhase，不是"RunAsync 返回了"（审计 P1-4）。
        trace.Finish(_session.LastRunOutcome, "Step2SelectDataPage");
        PushState();
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        using var trace = ActionTrace.Begin(ActionKinds.Pause, ControlIds.Step2Pause, "click", "Step2SelectDataPage");
        trace.Eligibility(true, "allowed");
        trace.Started();
        // ★ FIX BATCH 3 / §6 ★ 旧写法是 `Expect("pause.v1","pause-request-write-result")` + await 之后立刻
        //   Confirm —— 契约里根本没有这个步骤名，而且"请求写下"被当成了"暂停成功"（自证式期望）。
        //   现在：期望 = 契约里的兑现步骤（pause.v2 / pause.external），且**等引擎真值落定**才收口。
        trace.Expect("pause.v2", "pause.external");
        await _session.PauseAsync();
        var paused = await _session.WaitForPauseSettledAsync();
        if (paused)
        {
            trace.Confirm("pause.v2", "pause.external");
            trace.Complete(DiagnosticOutcome.Succeeded, "paused");
        }
        else if (_session.EnginePauseState == PauseState.PauseFailed)
        {
            // 暂停失败：绝不 Confirm；引擎还会发 TRN-023，诊断侧据此开卡 + 降级健康。
            trace.Complete(DiagnosticOutcome.Failed, "pause-failed");
        }
        else
        {
            trace.Complete(DiagnosticOutcome.Unknown, "pause-unsettled");
        }
        PushState();
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        using var trace = ActionTrace.Begin(ActionKinds.Stop, ControlIds.Step2Stop, "click", "Step2SelectDataPage");
        trace.Eligibility(true, "allowed");
        trace.Started();
        trace.Expect("stop.v1", "stop.external");
        await _session.StopAsync();
        // ★ FIX BATCH 3 / §6.4 ★ 等运行真的收尾再收口（StopAsync 只发取消令牌就返回）。
        if (await _session.WaitForStopSettledAsync())
        {
            trace.Confirm("stop.v1", "stop.external");
            trace.Complete(DiagnosticOutcome.Succeeded, "stop-settled");
        }
        else
        {
            trace.Complete(DiagnosticOutcome.Unknown, "stop-unsettled");
        }
        PushState();
    }

    private async void Resume_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        using var trace = ActionTrace.Begin(ActionKinds.Resume, ControlIds.Step2Resume, "click", "Step2SelectDataPage");
        trace.Eligibility(true, "allowed");
        trace.Started();
        trace.Expect("resume.v1", "resume.external");
        await _session.ResumeAsync(_passwordProvider?.Invoke(), new Progress<string>(m => StateMessageText.Text = m));
        // ★ FIX BATCH 3 / §6.4 ★ 等引擎真的离开暂停再收口。
        if (await _session.WaitForResumeSettledAsync())
        {
            trace.Confirm("resume.v1", "resume.external");
            trace.Complete(DiagnosticOutcome.Succeeded, "resume-settled");
        }
        else
        {
            trace.Complete(DiagnosticOutcome.Unknown, "resume-unsettled");
        }
        PushState();
    }

    /// <summary>
    /// QA 探针驱动口：等本页真正加载后，把树根逐一展开（走的是**与用户点箭头完全相同**的
    /// Expanding → EnsureChildrenAsync 路径），以便截图取证“层级真的出来了”。
    /// 只有环境变量门控的探针会调用它；生产路径不调用。
    /// </summary>
    public void ExpandAllRootsForVerification() => _ = ExpandAllRootsForVerificationAsync();

    private async Task ExpandAllRootsForVerificationAsync()
    {
        if (Environment.GetEnvironmentVariable("PCMIG_STEP2_TREE_QA_EXPAND") == "0") return;
        try
        {
            if (!IsLoaded)
            {
                var tcs = new TaskCompletionSource<bool>();
                Loaded += (_, _) => tcs.TrySetResult(true);
                await tcs.Task;
            }
            if (_session is null || DirTreeView.RootNodes.Count == 0) return;

            // 逐层展开：展开前先按**与 Expanding 完全相同**的路径把子项搬进节点树
            // （真实读盘 → EnsureChildrenAsync；节点树同步 → SyncNodeChildren）。
            // 这样做不是“绕开 UI”，而是把 Expanding 该做的那件事显式写出来，
            // 因为程序化设置 IsExpanded 在本页实测不会触发 Expanding。
            var level = DirTreeView.RootNodes.ToList();
            var deep = Environment.GetEnvironmentVariable("PCMIG_STEP2_TREE_QA_EXPAND") == "2";
            for (var depth = 0; depth < (deep ? 2 : 1) && level.Count > 0; depth++)
            {
                var next = new List<TreeViewNode>();
                foreach (var node in level)
                {
                    if (node.Content is DirNode model)
                    {
                        await _session.Tree.EnsureChildrenAsync(model);
                        SyncNodeChildren(node);
                    }
                    node.IsExpanded = true;
                    foreach (var child in node.Children)
                        if (child.Content is DirNode) next.Add(child);
                }
                level = next;
                await Task.Delay(400);   // 给布局一拍时间
            }
            QaDumpTreeState("最终");
        }
        catch { /* 取证辅助，不容错到影响业务 */ }
    }

    /// <summary>把某一层的容器全部展开（QA 取证用）。deep=true 时再往下一层展开一次。</summary>
    /// <summary>把某一层容器全部展开（保留为备用取证路径；节点模式下由 RootNodes 直接驱动）。</summary>
    private void ExpandNodeLevel(List<object> nodes, bool deep)
    {
        var containers = new List<TreeViewItem>();
        foreach (var node in nodes)
        {
            if (DirTreeView.ContainerFromItem(node) is TreeViewItem tvi) containers.Add(tvi);
        }
        foreach (var tvi in containers) tvi.IsExpanded = true;
        if (!deep || containers.Count == 0) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try
            {
                var next = new List<TreeViewItem>();
                foreach (var tvi in containers) CollectTreeViewItems(tvi, next, skipSelf: true);
                foreach (var child in next) child.IsExpanded = true;
            }
            catch { /* 取证辅助，不容错到影响业务 */ }
        });
    }

    /// <summary>从视觉树里收集 TreeViewItem（QA 取证用；skipSelf=true 时不把 parent 本身算进去）。</summary>
    private static void CollectTreeViewItems(DependencyObject parent, List<TreeViewItem> into, bool skipSelf)
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is TreeViewItem tvi && !(skipSelf && ReferenceEquals(child, parent))) into.Add(tvi);
            CollectTreeViewItems(child, into, skipSelf: false);
        }
    }

    /// <summary>
    /// QA 取证：把**容器层面**的真实状态（根容器的展开态、视觉树里 TreeViewItem 总数、前几项文本）
    /// 写进 exe 旁的 step2-tree-qa.log。它回答的问题只有一个是静态扫描回答不了的：
    /// **“TreeView 到底有没有把子项渲染出来”**。
    /// </summary>
    private void QaDumpTreeState(string tag)
    {
        try
        {
            var items = new List<TreeViewItem>();
            CollectTreeViewItems(DirTreeView, items, skipSelf: true);
            var texts = items.Take(12).Select(tvi => tvi.Content?.ToString() ?? "?").ToList();
            var nodeDump = new List<string>();
            foreach (var n in DirTreeView.RootNodes.Take(3))
            {
                nodeDump.Add($"root(Content={n.Content?.GetType().Name}, IsExpanded={n.IsExpanded}, 子节点={n.Children.Count}, HasUnrealized={n.HasUnrealizedChildren})");
                foreach (var c in n.Children.Take(2))
                    nodeDump.Add($"  └ child(Content={c.Content?.GetType().Name}, 子节点={c.Children.Count})");
            }
            var lines = new List<string>
            {
                $"--- 容器取证[{tag}] {DateTime.Now:HH:mm:ss.fff} ---",
                $"TreeView 可见性        : {DirTreeView.Visibility}",
                $"TreeView.RootNodes 数  : {DirTreeView.RootNodes.Count}",
                $"ItemsSource 元素数     : {(DirTreeView.ItemsSource as System.Collections.ICollection)?.Count.ToString() ?? "(null)"}",
                $"视觉树 TreeViewItem 数 : {items.Count}",
                $"前 12 项 Content 文本  : {string.Join(" / ", texts)}",
                "节点层: " + string.Join(" | ", nodeDump),
            };
            File.AppendAllLines(Path.Combine(AppContext.BaseDirectory, "step2-tree-qa.log"), lines);
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "step2-tree-qa.log"),
                    $"容器取证失败[{tag}]：{ex.Message}{Environment.NewLine}");
            }
            catch { /* 取证失败不影响业务 */ }
        }
    }

    /// <summary>Compact 时把左右两栏重排为上下单栏；纯视觉，不接触任何业务状态。</summary>
    /// <remarks>
    /// A53（Preflight P0-1）防御性修复：**消除对 RowDefinitions 固定索引的依赖**。
    /// 起因：默认布局里 <c>SelectColumns</c> 已不再保留 <c>&lt;RowDefinition Height="0"/&gt;</c>
    /// 占位行（那一行会让 <c>RowSpacing="12"</c> 白占 12 DIP，本会话已实测并把空间还给了目录树），
    /// 于是 XAML 只剩 1 个内容行，而本方法原先无条件访问 <c>RowDefinitions[1]</c>
    /// —— 一旦 Compact 路径被启用就会直接抛 IndexOutOfRange（目前是死代码，故未暴露）。
    ///
    /// 修法：**第二行按需动态创建 / 退出 Compact 时移除**，不再依赖 XAML 里存在占位行。
    /// 默认（非 Compact）分支不再触碰 RowDefinitions，几何与修复前完全一致 ⇒ 零视觉变化。
    /// </remarks>
    public void ApplyLayoutMode(LayoutMode mode)
    {
        if (SelectColumns is null || SelectRightStack is null) return;
        var compact = mode == LayoutMode.Compact;
        if (compact)
        {
            // 第二行按需创建：默认布局不保留 Height="0" 占位行，避免 RowSpacing 白占空隙。
            if (SelectColumns.RowDefinitions.Count < 2)
            {
                SelectColumns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            SelectColumns.RowDefinitions[1].Height = GridLength.Auto;
            if (SelectColumns.ColumnDefinitions.Count > 1)
            {
                SelectColumns.ColumnDefinitions[1].Width = new GridLength(0);
            }
            Grid.SetRow(SelectRightStack, 1);
            Grid.SetColumn(SelectRightStack, 0);
        }
        else
        {
            // 先把右栏放回第 0 行第 1 列，再移除 Compact 期间可能动态加过的多余行。
            Grid.SetRow(SelectRightStack, 0);
            Grid.SetColumn(SelectRightStack, 1);
            if (SelectColumns.ColumnDefinitions.Count > 1)
            {
                SelectColumns.ColumnDefinitions[1].Width = new GridLength(360);
            }
            while (SelectColumns.RowDefinitions.Count > 1)
            {
                SelectColumns.RowDefinitions.RemoveAt(SelectColumns.RowDefinitions.Count - 1);
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ★ 只读模型 dump（A.5 串线诊断专用；env 门控；**完全不碰任何 UI 控件**）★
    //  为什么需要：TreeView 的视觉缩进判断不可靠，而"父级联没生效"既可能是模型没级联、
    //  也可能是这些行根本不是那个父的子节点。本 dump 打印**模型真实父子关系**与每行 IsChecked，
    //  用于与真实 UI 截图做两类独立证据对照。
    //  纪律：只读模型（Children/Parent/IsChecked/FullPath），不写任何 UI 属性、不订阅控件事件
    //        ⇒ 不存在与绑定争夺所有权的问题（这正是它与已删除的 Row Push 的本质区别）。
    // ══════════════════════════════════════════════════════════════════════
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _qaDumpTimer;
    private bool _qaDumpStarted;

    private void StartQaModelDumpIfRequested()
    {
        if (Environment.GetEnvironmentVariable("PCMIG_STEP2_TREE_QA_DUMP") != "1") return;
        if (_qaDumpStarted) return;
        _qaDumpStarted = true;
        var q = DispatcherQueue;
        if (q is null) return;
        var timer = q.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(1500);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => { try { DumpModelTreeOnce(); } catch { } };
        _qaDumpTimer = timer;   // 强引用（防 GC 回收导致回调永不触发）
        timer.Start();
    }

    private void DumpModelTreeOnce()
    {
        if (_session is null) return;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== MODEL DUMP {DateTime.Now:HH:mm:ss.fff} ===");
        foreach (var root in _session.Tree.RootNodes) DumpNode(sb, root, 0);
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "a5-model-dump.log"), sb.ToString()); } catch { }
    }

    private static void DumpNode(System.Text.StringBuilder sb, DirNode node, int depth)
    {
        var pad = new string(' ', depth * 2);
        var st = node.IsChecked is null ? "HALF" : node.IsChecked == true ? "ON" : "off";
        var kind = node.FullPath.Length == 0 ? "DUMMY" : "DIR";
        sb.AppendLine($"{pad}{kind} [{st}] {node.Name} | loaded={node.ChildrenLoaded} | children={node.Children.Count}");
        if (depth >= 8 || !node.ChildrenLoaded) return;
        foreach (var c in node.Children)
        {
            switch (c)
            {
                case DirNode d: DumpNode(sb, d, depth + 1); break;
                case FileRow fr:
                    var fs = fr.IsChecked ? "ON" : "off";
                    sb.AppendLine($"{pad}  FILE [{fs}] {fr.Name} | parent={fr.Parent?.Name ?? "(null)"}");
                    break;
            }
        }
    }
}
