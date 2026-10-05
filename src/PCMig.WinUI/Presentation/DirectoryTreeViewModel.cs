using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using PCMig.Core.Matrix;
using PCMig.Core.Util;

namespace PCMig.WinUI.Presentation;

/// <summary>
/// 目录树根的同步源：一个共享的 UNC 路径 + 该共享行当前的勾选态。
/// ★ A.5（P0-2）★ 勾选态只用于**新建根时的初始 <see cref="DirNode.IsChecked"/>**；
/// 根是否存在只由“UNC 在不在共享列表里”决定（见 <see cref="DirectoryTreeViewModel.SyncRoots(IEnumerable{ShareRootSpec})"/>）。
/// </summary>
public readonly record struct ShareRootSpec(string UncPath, bool IsSelected);

/// <summary>
/// Step 2 的目录树状态源（懒加载 + 三态勾选 + 选择汇总）。
///
/// 【来源与边界】
/// 本类是 <c>src\PCMig.Gui\MainViewModel</c> 中 <c>EnsureChildrenAsync</c>（1881-1967）、
/// <c>CollectCustomSelections</c>（1974-1995）、<c>WalkPartial</c>（1998-2010）、
/// <c>FindShareRootOf</c>（2012-2014）与 <c>AddShareNode</c> 的**逐语义移植**。
///   · 懒加载**真实读盘**：子目录/文件一律经 <c>Directory.EnumerateDirectories/EnumerateFiles</c>
///     在 <see cref="Task.Run(Func{Task})"/> 后台线程枚举，**没有任何假目录或假数量**；
///   · 过滤口径与 A4 传输同源：排除规则来自 Core 的 <see cref="MigrationMatrix"/>
///     （ExcludedDirectoryNames / ExcludedFileNames / SecurityBlockedFileNames），不另造一套；
///   · 跨线程纪律：读盘在后台，回到 UI 线程改 <c>ObservableCollection</c> 一律经 <see cref="DispatcherQueue"/>；
///   · 本类**不写任何存档**、不参与预检/扫描/计划，只回答“用户勾了什么”。
/// </summary>
public sealed class DirectoryTreeViewModel : ObservableObject
{
    /// <summary>单目录文件显示上限（超大数据保护）：勾选目录本身就包含全部文件，列表仅为可视化。</summary>
    public const int MaxFilesShownPerDir = 5000;

    private readonly DispatcherQueue? _queue;
    private readonly Serilog.ILogger _log;

    private MigrationMatrix? _matrixCache;

    /// <summary>
    /// 懒加载的**代次**（"连接已重新建立"计数器）。
    /// ★ A.5（C2/R20）★ 只经 <see cref="Volatile.Read(ref int)"/> / <see cref="Interlocked.Increment(ref int)"/>
    /// 访问：`_queue is null` 的离屏/QA 路径下，`await Task.Run` 的续体可能落在线程池线程，
    /// 普通 `int` 的读（返回时比较）与写（作废）之间没有 happens-before。
    /// </summary>
    private int _browseVersion;

    /// <summary>
    /// ★ A.5（C1）★ **重入守卫**（纯并发闸门，只活在一次调用的生命周期内）：
    /// 改前"防重入"是靠在 `await` **之前**就置 <see cref="DirNode.ChildrenLoaded"/> = true 实现的，
    /// 代价是"任何中途失败/结果被作废 ⇒ 该节点永久不再重试，只剩 `…` 占位"。
    /// 置位时机改为"成功路径的 UI 线程块内"之后，读盘期间就没有任何标记了 ⇒ 必须补这个集合，
    /// 否则用户快速连点展开箭头会并发读盘两次（R15）。
    ///
    /// 职责划分（两者不许互相兼职）：
    ///   · `_loading`       = "本次读盘正在进行"（只在本方法首行加、`finally` 移除；**绝不参与 UI 判定**）；
    ///   · `ChildrenLoaded` = "持久事实：子内容已经成功读出来过"（只在成功路径置 true，跨调用保留）。
    /// 用 <see cref="HashSet{T}"/> 的默认引用语义（`DirNode` 未重写 Equals/GetHashCode）。
    /// </summary>
    private readonly HashSet<DirNode> _loading = new();

    private bool _bigDirHintShown;

    public DirectoryTreeViewModel(DispatcherQueue? queue = null, Serilog.ILogger? logger = null)
    {
        _queue = queue ?? DispatcherQueue.GetForCurrentThread();
        _log = logger ?? Core.Logging.LogBootstrap.CreateAppLogger(console: false);
    }

    /// <summary>共享根节点集合（每个勾选的共享一个根；勾=整盘迁移）。</summary>
    public ObservableCollection<DirNode> RootNodes { get; } = new();

    /// <summary>是否已有可展开的根（用于页面在“空状态 / 目录树”之间二选一）。</summary>
    public bool HasRoots => RootNodes.Count > 0;

    /// <summary>目录树是否正在从网络读取子项（页面可据此给出“正在展开…”提示）。</summary>
    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }

    /// <summary>给用户看的一句话（超大规模目录提示等），由页面订阅后显示。</summary>
    public event Action<string>? Notice;

    /// <summary>
    /// 某个**共享根**的勾选态发生变化（三态都算：全勾 / 半勾 / 全不勾）。
    ///
    /// 为什么必须有：旧 WPF 的目录树根与 Step 1 的共享行是**联动的一份状态**
    /// （`src\PCMig.Gui\MainViewModel.cs:582-586` 的 `root.PropertyChanged` →
    /// `row.IsSelected = root.IsChecked == true`；`:467-475` 同款）。
    /// 若新 UI 不联动，用户在 Step2 取消共享根勾选后 `wholeShares` 仍会包含该共享 ⇒
    /// **整盘照传、取消被静默忽略**。本事件就是那条单向联动的出口
    /// （单向即可：全勾→整盘；半勾/全不勾→`IsSelected=false`→走 customs 或完全排除）。
    /// </summary>
    public event Action<DirNode>? RootCheckedChanged;

    private void OnRootPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DirNode.IsChecked)) return;
        if (sender is DirNode node) RootCheckedChanged?.Invoke(node);
    }

    /// <summary>
    /// ★ 2026-10-05（执行书 §2.2「未选择的共享不得进入 Step 2」）★
    /// 该共享的根是否已被用户在 Step 2 **实际使用过**（展开过，或已做成精确的子树选择）。
    ///
    /// 为什么需要它：本轮要求"Step 1 只选 H，Step 2 顶层就只能有 H"，因此建根不能再无脑接受
    /// 全部共享；但直接只按 <c>IsSelected</c> 建根会重新引入 A.5 修掉的那个回路 ——
    /// 用户取消一个子目录 ⇒ 根半选 ⇒ 联动把共享行置 false ⇒ 根被删、精确选择消失。
    ///
    /// 判据为什么是 <c>ChildrenLoaded || IsChecked is null</c>：
    ///   · <c>ChildrenLoaded</c> = 用户真的展开过这棵树（读盘发生过）；
    ///   · <c>IsChecked is null</c> = 用户做出了"只迁移部分子目录"的精确选择。
    ///   两者都只有**用户操作**才能达成。故意**不**把"初始同步的勾选态"算进来 ——
    ///   新建根时 <c>SetCheckedSilent</c> 同样会触发 <c>PropertyChanged</c>，
    ///   若用它当"用户动过"，那么任何一个根都会立刻被视为已使用，根就永远删不掉了。
    /// </summary>
    public bool IsUserEngagedRoot(string uncPath)
    {
        if (string.IsNullOrWhiteSpace(uncPath)) return false;
        var root = RootNodes.FirstOrDefault(r => r.FullPath.Equals(uncPath.Trim(), StringComparison.OrdinalIgnoreCase));
        if (root is null) return false;
        return root.ChildrenLoaded || root.IsChecked is null;
    }

    /// <summary>
    /// 按 UNC 列表重建整棵树（等价于“全部都以勾选态 true 传入”，供 QA/自动化等无共享行的场景使用）。
    /// 已存在的同名根**原样保留**（连同它的展开状态与勾选状态），只补新增、去掉已不在列表里的。
    ///
    /// 与旧 WPF 的对齐（`MainViewModel.cs:461-475` / `:567-589`）：
    ///   · 新增根：先静默置勾选态（`SetCheckedSilent`，**不回写**共享行，避免连接/重建时冲掉用户既有选择），
    ///     再订阅 `IsChecked` 变化 → 触发 <see cref="RootCheckedChanged"/>（单向联动到共享行）；
    ///   · 已存在的根：**不动**它的勾选态（用户的选择优先）。
    /// </summary>
    public void SyncRoots(IEnumerable<string> uncPaths)
        => SyncRoots(uncPaths.Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new ShareRootSpec(p, true)));

    /// <summary>
    /// ★ A.5（P0-2）★ 带勾选态的同步：输入是**全部共享**（不只已勾选的），
    /// 每个共享的勾选态只决定**新根的初始 IsChecked**（同旧 WPF
    /// <c>Gui\MainViewModel.cs:474 / :588</c> 的 <c>root.SetCheckedSilent(row.IsSelected)</c>），
    /// **不决定根是否存在**。已存在的根**一律不动勾选态**（用户的选择优先）。
    ///
    /// 为什么必须这样：只按 <c>IsSelected</c> 传根会成环——
    /// 用户取消一个子目录 ⇒ 根变半选 ⇒ 联动把共享行 <c>IsSelected</c> 置 false ⇒ 页面
    /// <c>OnShareItemChanged</c> 立刻回调 <c>SyncTreeRootsFromShares</c> ⇒ 该共享不在 wanted 里
    /// ⇒ 整棵根被删掉，用户刚做的精确选择随之消失。旧 WPF 的 DirTree 只在连接时构建一次、
    /// 根的存在只由“共享列表”决定，故这里恢复同语义。
    /// </summary>
    public void SyncRoots(IEnumerable<ShareRootSpec> shares)
    {
        // 同一 UNC 出现多行（理论上不会）时按“任一勾选即勾选”收敛，保证根的唯一性与确定性
        var wanted = shares.Where(s => !string.IsNullOrWhiteSpace(s.UncPath))
            .Select(s => new ShareRootSpec(s.UncPath.Trim(), s.IsSelected))
            .GroupBy(s => s.UncPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ShareRootSpec(g.Key, g.Any(x => x.IsSelected)))
            .ToList();

        // 去掉**已不在共享列表里**的根（自后向前删，避免索引漂移）。
        // ★ 判据是“共享还在不在列表里”，**不是** IsSelected——后者会导致 P0-2 的反馈回路。
        var changed = false;   // ★ A.5（C2）★ 只在**真的增/删了根**时才作废在途懒加载
        for (var i = RootNodes.Count - 1; i >= 0; i--)
        {
            if (!wanted.Any(p => p.UncPath.Equals(RootNodes[i].FullPath, StringComparison.OrdinalIgnoreCase)))
            {
                RootNodes[i].PropertyChanged -= OnRootPropertyChanged;
                RootNodes.RemoveAt(i);
                changed = true;
            }
        }
        // 补新增的根 + 同步已存在根的勾选态（后者见下方注释）
        foreach (var spec in wanted)
        {
            var existing = RootNodes.FirstOrDefault(r => r.FullPath.Equals(spec.UncPath, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                // ★ A.5 配套一致性修正（P0-2 同一回路暴露出来的另一半）★
                //   已存在的根：勾选态**只在“明确全勾 ⇄ 明确全不勾”之间跟随共享行**；
                //   半选（IsChecked == null = 用户做了精确目录选择）**一律不动** —— 否则同步会抹掉精细选择。
                //   为什么必须跟随：`wholeShares` 取的是 `conn.Shares.Where(s => s.IsSelected)`，
                //   用户在 Step1 取消该共享后它已被排除；若树里仍画着“全勾”，界面就在骗用户
                //   （显示“整盘迁移”，实际既不进 wholeShares 也不进 customs）——A.5 实测正是这个现象。
                //   为什么不会成环：SetCheckedSilent 不主动写回共享行；即便经 RootCheckedChanged 回流，
                //   写回值与共享行现状相同 ⇒ 页面回调进来时 spec.IsSelected 不变 ⇒ 立刻收敛。
                if (existing.IsChecked == true && !spec.IsSelected) existing.SetCheckedSilent(false);
                else if (existing.IsChecked == false && spec.IsSelected) existing.SetCheckedSilent(true);
                continue;
            }
            var display = spec.UncPath.TrimEnd('\\');
            var slash = display.LastIndexOf('\\');
            var node = new DirNode
            {
                Name = slash >= 0 ? display[(slash + 1)..] : display,
                FullPath = spec.UncPath,
                IsShareRoot = true,
            };
            node.AddDummy();
            node.PropertyChanged += OnRootPropertyChanged;   // 单向联动：根 IsChecked → 共享行 IsSelected
            // 静默初始化 = 共享行当前勾选态（未勾选的共享 ⇒ 根以“全不勾”出现在树里，与旧 WPF 一致）；
            // SetCheckedSilent 不回写共享行，故不会在重建时制造新的反馈边。
            node.SetCheckedSilent(spec.IsSelected);
            RootNodes.Add(node);
            changed = true;
        }
        Raise(nameof(HasRoots));

        // ★ A.5（C2）★ 根集合真的变了 ⇒ 作废**在途**的懒加载结果：
        //   在途结果对应的节点可能已不在树上（Parent 链断裂），继续回填会往孤儿节点写数据。
        //   条件必须是"真的增/删"而不是无条件 —— 每次勾选共享都会走到本方法（OnShareItemChanged →
        //   SyncTreeRootsFromShares），无条件作废就等于"勾一个共享作废一次"，反复展开反复白读盘（R19/C8）。
        //   ⚠ 范围边界：作废**只对"在途（尚未成功）"的加载有效** —— 已成功加载过的节点不会被重置
        //     ChildrenLoaded ⇒「换主机后让已加载的树重读」不在本修复范围内。
        if (changed) InvalidateInFlightBrowses();
    }

    /// <summary>连接已重新建立：作废**在途**的懒加载结果（与 WPF 的 <c>_browseVersion</c> 同语义）。</summary>
    public void InvalidateInFlightBrowses() => Interlocked.Increment(ref _browseVersion);

    /// <summary>
    /// ★ 仅测试缝 ★ —— 当前懒加载代次的**只读诊断**（生产代码从不读它）。
    /// 用途：让单测能确定性地断言"哪些时机真的作废了在途加载"（C2/R19）——例如
    /// 「反复勾/取消共享不得作废」只能靠"代次没变"来正向证明，而不必去数磁盘调用次数。
    /// 它不改变任何行为（只读一个 <see cref="Volatile.Read(ref int)"/>）。
    /// </summary>
    internal int BrowseVersionForTest => Volatile.Read(ref _browseVersion);

    /// <summary>
    /// 把一个路径挂成本地根节点（省去 SyncRoots 对“\\主机\共享”形式的依赖）。
    /// 仅供**流程式（QA）/自动化**场景：例如没有旧电脑可连时，用本机真实目录验证
    /// 「TreeView 能否真实展开 + 三态勾选是否可见 + 懒加载是否真读盘」。
    /// 不改变任何读取口径：子项仍由 <see cref="EnsureChildrenAsync"/> 真实枚举磁盘。
    /// </summary>
    public DirNode AddLocalRootForVerification(string path)
    {
        var display = path.TrimEnd('\\');
        var slash = display.LastIndexOf('\\');
        var node = new DirNode
        {
            Name = slash >= 0 ? display[(slash + 1)..] : display,
            FullPath = path,
            IsShareRoot = true,
        };
        node.AddDummy();
        node.PropertyChanged += OnRootPropertyChanged;
        node.SetCheckedSilent(true);
        RootNodes.Add(node);
        Raise(nameof(HasRoots));
        return node;
    }

    /// <summary>
    /// 懒加载节点的子内容：子目录（DirNode）+ 一级文件（FileRow）挂在同一棵树上。
    /// **真实读盘**，失败一律按“跳过”处理（与 WPF 同口径：单目录/单文件失败不致命）。
    /// </summary>
    public async Task EnsureChildrenAsync(DirNode node, int? version = null)
    {
        // ★ A.5（C1）★ 改前这里是：
        //     if (node.ChildrenLoaded || string.IsNullOrEmpty(node.FullPath)) return;
        //     node.ChildrenLoaded = true; // 防重入      ← 在 await **之前**就置位
        //   后果（A.5 实测：7 个根全部踩中）：任何中途失败、或结果被 _browseVersion 作废，
        //   该标记都已经是 true ⇒ 首行守卫从此恒真 ⇒ 该节点**永久不再重试**，界面上只剩一个
        //   看起来能展开的 `…` 占位（假 affordance）。
        //   现在的语义：**只有成功路径的 UI 线程块内才置 true**；失败/作废都不置 ⇒ 用户再点一次展开即重试。
        //   读盘期间的并发闸门改由 _loading 承担（见该字段的注释）。
        if (node.ChildrenLoaded || _loading.Contains(node) || string.IsNullOrEmpty(node.FullPath)) return;
        _loading.Add(node);
        var v = version ?? Volatile.Read(ref _browseVersion);
        try
        {
            _matrixCache ??= MigrationMatrix.Load(null, _log);
            var exclDirs = new HashSet<string>(_matrixCache.ExcludedDirectoryNames, StringComparer.OrdinalIgnoreCase);
            var exclFiles = new HashSet<string>(
                _matrixCache.ExcludedFileNames.Concat(_matrixCache.SecurityBlockedFileNames), StringComparer.OrdinalIgnoreCase);
            var path = node.FullPath;
            IsLoading = true;
            var (dirs, files, denied) = await Task.Run(() =>
            {
                var dlist = new List<string>();
                var flist = new List<(string Name, long Size)>();
                var deniedCount = 0;
                try
                {
                    foreach (var d in Directory.EnumerateDirectories(path))
                    {
                        try
                        {
                            var attr = File.GetAttributes(d);
                            if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                            var name = Path.GetFileName(d.TrimEnd('\\'));
                            if (name == null || exclDirs.Contains(name)) continue;
                            dlist.Add(d);
                        }
                        catch { deniedCount++; /* 单目录跳过 */ }
                    }
                }
                catch { deniedCount++; /* 目录整体不可访问 */ }
                try
                {
                    foreach (var f in Directory.EnumerateFiles(path))
                    {
                        try
                        {
                            var fi = new FileInfo(f);
                            if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            if (exclFiles.Contains(fi.Name)) continue;
                            flist.Add((fi.Name, fi.Length));
                        }
                        catch { deniedCount++; /* 单文件跳过 */ }
                    }
                }
                catch { deniedCount++; /* 文件枚举失败不致命 */ }
                return (dlist.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                        flist.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                        deniedCount);
            });
            if (v != Volatile.Read(ref _browseVersion)) return; // 已重新连接，丢弃过期结果
            // ★ A.5（C1）★ 上面这条 `return` **刻意不置 ChildrenLoaded**：结果已过期，本节点
            //   并没有"成功加载出内容"这个事实 ⇒ 箭头必须还在、再点一次必须能重新读盘（与 C2 互补，R18）。
            await OnUiAsync(() =>
            {
                var inherit = node.IsChecked != false; // 父为“勾/半勾” → 子默认勾（半勾=只排除了亲手取消的项）
                node.Children.Clear();
                foreach (var d in dirs)
                {
                    var child = new DirNode { Name = Path.GetFileName(d.TrimEnd('\\')) ?? d, FullPath = d, Parent = node };
                    child.AddDummy();
                    child.SetCheckedSilent(inherit);
                    node.Children.Add(child);
                }
                var shown = files.Count <= MaxFilesShownPerDir ? files : files.Take(MaxFilesShownPerDir).ToList();
                foreach (var f in shown)
                {
                    var row = new FileRow { Name = f.Name, DirPath = node.FullPath, Size = f.Size, Parent = node };
                    row.SetCheckedSilent(inherit);
                    node.Children.Add(row);
                }
                // ★ 2026-10-05 紧急修复（用户真机判词：展开 H 只看到一个可勾选的「…」）★
                //   过去这里往 `Children` 里塞一个 `FullPath = ""` 的"提示 DirNode"，UI 侧把它
                //   materialize 成一个**可勾选的假节点** —— 这是用户明确要求"不能再出现"的东西。
                //   提示信息改走既有的 `Notice` 通道：内容不丢，但树里只呈现真实目录 / 真实文件。
                if (files.Count > shown.Count)
                    Notice?.Invoke($"目录「{node.Name}」共 {files.Count} 个文件，仅显示前 {shown.Count} 个；勾选该目录即包含其全部文件。");
                // 目录读不全时如实告知（不静默）：这些位置不会被迁移，与扫描残缺闸门同一口径的事实来源
                if (denied > 0)
                    Notice?.Invoke($"目录「{node.Name}」有 {denied} 个子项因权限/网络原因无法列出——它们不会被迁移，请在此处确认后再生成计划。");
                // 单目录文件特别多（图片库等）：如实告知规模，并给出**不依赖未接线开关**的建议。
                // ⚠ 阶段 A 的「超大数数据模式」开关按用户决议**未接线**（保持 IsEnabled=False），
                //   因此这里绝不可以引导用户去开它（会让人去找一个点不动的开关）。
                if (files.Count > 20000 && !_bigDirHintShown)
                {
                    _bigDirHintShown = true;
                    Notice?.Invoke($"目录「{node.Name}」含 {files.Count:N0} 个文件——展开与逐项勾选会比较慢，建议只勾选需要的子目录（勾选目录即包含其全部文件）。");
                }
                // ★ A.5（P0-1）：此处**不再** `node.AddDummy()`（原注释称“末位补回占位子节点，语义不可省”）。
                //   核实后的结论：那条注释与事实不符，且与用户指定的修复原则直接冲突——
                //     · 走到这里时本节点即将被标记为"已成功加载"（见本块最后一行），
                //       而 `EnsureChildrenAsync` 首行含 `if (node.ChildrenLoaded …) return;` ⇒
                //       再次进入本方法会被拦下，补的箭头是点不出任何新内容的假 affordance；
                //     · “懒加载后只剩文件就没有箭头”这一顾虑不成立：节点树由 `SyncNodeChildren` 按
                //       `Children` 真实搬运，只要列表里有目录/文件行，TreeViewNode.Children 就非空 ⇒ 箭头自然在；
                //     · 真正受影响的只有**真实空目录**（含被排除规则清空的目录）：补占位 = 永久假箭头，
                //       正是用户明确要求“不得保留”的情况。
                //   占位节点的唯一合法来源是 `DirNode.AddDummy()`，而它已加 `ChildrenLoaded` 守卫，
                //   加载后自动 no-op（双保险，防止日后有人再加回这一行）。
                //
                // ★ A.5（C1）★ **成功路径的唯一置位点**：放在 UI 块的最后一行，与上面这批
                //   `Children` 集合变更处在**同一次 UI 线程执行**内 —— 不会出现"集合已换、标记还没换"
                //   的中间态被 `BuildNode` / `SyncNodeChildren` 观察到。
                //   本方法内 `node.ChildrenLoaded = true;` 恰好出现这一次（首行是**读**，不是写）。
                node.ChildrenLoaded = true;
            });
        }
        catch (Exception ex) { _log.Debug(ex, "加载子内容失败 {Path}", node.FullPath); }
        finally
        {
            // 只移除本次自己加的那一项（绝不整体 Clear：那会误放行别的节点的并发加载）；
            // 失败路径同样到这里 ⇒ ChildrenLoaded 保持 false ⇒ 用户再点一次展开即可重试。
            _loading.Remove(node);
            IsLoading = false;
        }
    }

    /// <summary>
    /// 汇总用户选择（级联三态语义）：
    /// 根“全勾”=整盘（走 wholeShares）；根“半勾”=走子树：全勾目录收目录，半勾目录继续下钻，勾的文件收文件。
    /// 目录折叠到最上层；文件仅当其目录未被整体勾选时生效。
    /// </summary>
    public List<string> CollectCustomSelections()
    {
        var dirs = new List<string>();
        var files = new List<string>();
        foreach (var root in RootNodes)
        {
            if (root.IsChecked != null) continue; // 全勾走整盘；全不勾跳过
            WalkPartial(root, dirs, files);
        }
        var collapsed = dirs
            .Where(d => !dirs.Any(p => p != d && d.StartsWith(p.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var result = new List<string>(collapsed);
        foreach (var f in files)
        {
            var dir = f[..f.LastIndexOf('\\')];
            if (collapsed.Any(d => dir.Equals(d.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                || dir.StartsWith(d.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(f);
        }
        return result;
    }

    /// <summary>递归收集半勾节点下的有效选择。</summary>
    private static void WalkPartial(DirNode node, List<string> dirs, List<string> files)
    {
        foreach (var c in node.Children)
        {
            switch (c)
            {
                case DirNode d when d.FullPath.Length == 0: break; // 占位节点
                case DirNode d when d.IsChecked == true: dirs.Add(d.FullPath); break;
                case DirNode d when d.IsChecked == null: WalkPartial(d, dirs, files); break;
                case FileRow f when f.IsChecked: files.Add(f.DirPath.TrimEnd('\\') + "\\" + f.Name); break;
            }
        }
    }

    /// <summary>自定义选择涉及的共享根（供 Planner 做相对路径映射与 Preflight 检查）。</summary>
    public string? FindShareRootOf(string path)
        => RootNodes.FirstOrDefault(s => path.StartsWith(s.FullPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
            || path.Equals(s.FullPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))?.FullPath;

    /// <summary>在 UI 线程上执行动作；已在 UI 线程时直接执行（避免 TryEnqueue 与等待互相打架）。</summary>
    private Task OnUiAsync(Action action)
    {
        if (_queue is null) { action(); return Task.CompletedTask; }
        if (_queue.HasThreadAccess) { action(); return Task.CompletedTask; }
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.TryEnqueue(() =>
            {
                try { action(); tcs.TrySetResult(true); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }))
        {
            return Task.CompletedTask; // 队列已关（应用退出中）：不抛异常打断退出
        }
        return tcs.Task;
    }

    /// <summary>格式化字节（与项目其它位置同一实现，不另造一份）。</summary>
    public static string FormatBytes(long bytes) => Format.Bytes(bytes);
}