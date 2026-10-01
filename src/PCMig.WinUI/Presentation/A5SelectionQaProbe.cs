namespace PCMig.WinUI.Presentation;

/// <summary>
/// A.5 取证探针：**只在环境变量 <c>PCMIG_STEP2_TREE_QA_SELECT</c> 非空且非 "0" 时被调用**
/// （由 <c>Views\Step2SelectDataPage</c> 在同样的环境变量门控下触发，生产路径不可达）。
///
/// 为什么需要它：用户对本阶段的验收要求是「三态对应的**实际迁移范围**必须正确，不能只看 UI 图标」，
/// 而本机实测**合成鼠标点不动 WinUI 的 TreeViewItem 展开按钮**（chevron），
/// 且 UIA 注入在本项目历史上会让被测应用崩溃。因此改为：用**与用户点击完全相同的方法**
/// （<see cref="DirNode.ToggleFromUi"/>，即 XAML 里 <c>NodeCheck_Click</c> 走的那一个），
/// 把用户指定的三态序列跑一遍，并把每一步的**模型真实状态**与
/// <see cref="DirectoryTreeViewModel.CollectCustomSelections"/>（也就是 plan 的 customs 来源）
/// 的真实输出写进 exe 旁的日志。
///
/// 它不改变任何产品语义：只调用公开方法、只写日志。
/// </summary>
public static class A5SelectionQaProbe
{
    /// <summary>环境变量名：非空且非 "0" 时才跑（生产不设，故永不执行）。</summary>
    public const string EnvVar = "PCMIG_STEP2_TREE_QA_SELECT";

    public static bool Requested =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvVar))
        && Environment.GetEnvironmentVariable(EnvVar) != "0";

    /// <summary>跑三态序列并把每步真实状态写进 <paramref name="logPath"/>。</summary>
    public static async Task RunAsync(DirectoryTreeViewModel tree, string logPath, string rootName = "C$")
    {
        var lines = new List<string>();
        void W(string s) => lines.Add(s);
        try
        {
            var root = tree.RootNodes.FirstOrDefault(r => r.Name.Equals(rootName, StringComparison.OrdinalIgnoreCase));
            if (root is null) { W($"找不到根 {rootName}（现有：{string.Join(",", tree.RootNodes.Select(r => r.Name))}）"); return; }

            W($"=== A.5 三态取证 {DateTime.Now:HH:mm:ss.fff} 根={root.Name} 根集合={tree.RootNodes.Count} 个 ===");
            Dump("0 初始", root, tree, W);

            root.ToggleFromUi();                       // 用户点根复选框：☐ → ☑
            Dump("1 点根（期望：全勾 ☑，且已加载子项全变勾）", root, tree, W);

            var kids = LoadedChildren(root);
            if (kids.FirstOrDefault() is { } first)
            {
                first.ToggleFromUi();                  // 用户点一个子目录的复选框 = 取消它
                Dump($"2 取消子目录 {first.Name}（期望：根半选 ◩、根仍在树中、其余子项保持勾）", root, tree, W);
            }

            root.ToggleFromUi();                       // 半选 → 全勾
            Dump("3 再点根（期望：回到全勾 ☑）", root, tree, W);

            root.ToggleFromUi();                       // 全勾 → 全不勾
            Dump("4 再点根（期望：全不勾 ☐ ⇒ 完全排除）", root, tree, W);

            root.ToggleFromUi();                       // 全不勾 → 全勾
            Dump("5 再点根（期望：全部重新逻辑选中 ☑）", root, tree, W);

            // ---- 用户最关心的那条 lazy loading 语义：**先勾根、再首次展开** ⇒ 新解出的子项必须默认全选 ----
            var firstLoad = tree.RootNodes.FirstOrDefault(r => !r.ChildrenLoaded && r.Name.Equals("D$", StringComparison.OrdinalIgnoreCase))
                            ?? tree.RootNodes.FirstOrDefault(r => !r.ChildrenLoaded);
            if (firstLoad is not null)
            {
                var before = firstLoad.IsChecked;
                firstLoad.ToggleFromUi();              // 先把它勾上
                await tree.EnsureChildrenAsync(firstLoad);
                var loaded = LoadedChildren(firstLoad);
                W($"[6 先勾根({firstLoad.Name} {Fmt(before)}→{Fmt(firstLoad.IsChecked)})再首次展开（期望：新解出的子项默认全部勾选）]");
                W($"    首次加载子目录={loaded.Count} 其中勾选={loaded.Count(d => d.IsChecked == true)} 未勾={loaded.Count(d => d.IsChecked == false)}");
                W($"    样例：{string.Join(", ", loaded.Take(5).Select(d => d.Name + "=" + Fmt(d.IsChecked)))}");
            }

            // ---- 把树留在“半选：只勾 pcmig-smoke”状态，供后续在真实 UI 上点「预检并生成计划」，
            //      核对 plan.json 里的 customs 是否就是这个目录 ----
            root.SetChecked(false);                    // 先全不勾
            var smoke = LoadedChildren(root).FirstOrDefault(d => d.Name.Equals("pcmig-smoke", StringComparison.OrdinalIgnoreCase));
            if (smoke is not null)
            {
                smoke.ToggleFromUi();                  // 只勾它 ⇒ 根半选
                Dump("7 留给 UI 的半选状态（只勾 pcmig-smoke）", root, tree, W);
            }
        }
        catch (Exception ex) { W("EX " + ex); }
        finally
        {
            try { File.AppendAllLines(logPath, lines); } catch { /* 取证失败不影响业务 */ }
        }
    }

    private static List<DirNode> LoadedChildren(DirNode node)
        => node.Children.OfType<DirNode>().Where(d => d.FullPath.Length > 0).ToList();

    private static void Dump(string tag, DirNode root, DirectoryTreeViewModel tree, Action<string> w)
    {
        var kids = LoadedChildren(root);
        var sel = tree.CollectCustomSelections();
        w($"[{tag}]");
        w($"    根 IsChecked={Fmt(root.IsChecked)}  根仍在 RootNodes={tree.RootNodes.Contains(root)}  根集合数={tree.RootNodes.Count}");
        w($"    已加载子目录={kids.Count}（勾={kids.Count(d => d.IsChecked == true)} 未勾={kids.Count(d => d.IsChecked == false)} 半选={kids.Count(d => d.IsChecked is null)}）");
        w($"    子项样例：{string.Join(", ", kids.Take(4).Select(d => d.Name + "=" + Fmt(d.IsChecked)))}");
        w($"    CollectCustomSelections()：{sel.Count} 条" + (sel.Count > 0 ? " :: " + string.Join(" | ", sel.Take(4)) : "（根全勾 ⇒ 走 wholeShares 整盘；根全不勾 ⇒ 完全排除）"));
        w($"    FindShareRootOf(\"C:\\Aomei\") = {tree.FindShareRootOf(@"C:\Aomei") ?? "(null)"}");
    }

    private static string Fmt(bool? v) => v is null ? "半选◩" : v.Value ? "全勾☑" : "全不勾☐";
}