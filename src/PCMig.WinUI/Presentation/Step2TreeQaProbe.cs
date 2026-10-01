using System.Diagnostics;
using PCMig.Core.Logging;

namespace PCMig.WinUI.Presentation;

/// <summary>
/// Step 2 目录树的**流程式（QA）自证探针** —— 只在环境变量门控下运行，生产路径完全不受影响。
///
/// 【它解决什么问题】
/// TreeView 的“能不能真实展开出子目录/文件”是**静态扫描证明不了**的事（编译通过 ≠ 渲染出来）。
/// 真机取证需要一台旧电脑 + SMB 共享；本轮没有。因此这里给出一个**只走真实读盘**的探针：
///   · 把本机一个真实目录挂成树根（<see cref="DirectoryTreeViewModel.AddLocalRootForVerification"/>）；
///   · 调**与用户点展开箭头完全同一条**懒加载方法 EnsureChildrenAsync(node)（真实枚举磁盘）；
///   · 把结果（根数 / 子项数 / 前几项名字 / 三态勾选值）落进 exe 旁的 step2-tree-qa.log；
///   · 再把第一项展开（<c>TreeView.RootNodes[0].IsExpanded = true</c>），
///     让 UI 真正渲染出子项，供截图取证。
/// 它**不伪造任何目录/数量**：所有数字都来自真实文件系统；也不改任何业务规则与存档。
///
/// 开启方式（不设该环境变量 ⇒ 本类一个方法都不会被调用）：
///   <c>PCMIG_STEP2_TREE_QA=&lt;要展开的真实目录绝对路径&gt;</c>
/// </summary>
public static class Step2TreeQaProbe
{
    public const string EnvVar = "PCMIG_STEP2_TREE_QA";

    /// <summary>当前是否处于探针模式（未设环境变量时为 null）。</summary>
    public static string? RequestedPath => Environment.GetEnvironmentVariable(EnvVar);

    /// <summary>
    /// 执行探针：挂根 + 真实懒加载 + 写日志 + 返回可展开的根节点（供调用方驱动 UI 展开）。
    /// 任何异常都只写日志，**绝不打断正常启动**。
    /// </summary>
    public static async Task<DirNode?> RunAsync(MigrationSessionViewModel session)
    {
        var path = RequestedPath;
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var log = LogBootstrap.CreateAppLogger(console: false);
            var root = session.Tree.AddLocalRootForVerification(path);
            await session.Tree.EnsureChildrenAsync(root);

            var dirs = root.Children.OfType<DirNode>().Where(d => d.FullPath.Length > 0).ToList();
            var files = root.Children.OfType<FileRow>().ToList();
            var sample = root.Children.Take(5)
                .Select(c => c switch { DirNode d => $"DIR  {d.Name} [checked={d.IsChecked}]", FileRow f => $"FILE {f.Name} [{f.SizeText}] checked={f.IsChecked}", _ => "?" });

            var lines = new List<string>
            {
                $"=== Step2 目录树 QA 探针 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===",
                $"真实目录            : {path}",
                $"磁盘确实存在        : {Directory.Exists(path)}",
                $"根节点数            : {session.Tree.RootNodes.Count}",
                $"根的 FullPath       : {root.FullPath}",
                $"懒加载后 子目录数   : {dirs.Count}（占位节点不计：占位 FullPath 为空）",
                $"懒加载后 子文件数   : {files.Count}",
                $"根 ChildrenLoaded   : {root.ChildrenLoaded}",
                $"根 IsChecked        : {root.IsChecked}（true=全勾）",
                "前 5 个子项         :",
            };
            lines.AddRange(sample.Select(s => "  · " + s));

            // 三态自证（顺序很重要：先取子目录的**当前**状态再改动，且改动后立即读根的状态）：
            //   ① 取消一个子目录 → 根应变成半勾（null），因为还有别的子项是勾的；
            //   ② 恢复该子目录 → 根应回到全勾（true）。
            if (dirs.Count > 0)
            {
                var first = dirs[0];
                lines.Add($"三态自证 改前：子目录「{first.Name}」CheckStateTrue={first.CheckStateTrue}, Indeterminate={first.CheckStateIndeterminate}；根 IsChecked={root.IsChecked}");
                first.SetChecked(false);
                lines.Add($"三态自证 改后：子目录「{first.Name}」CheckStateTrue={first.CheckStateTrue}, Indeterminate={first.CheckStateIndeterminate}；根 IsChecked={root.IsChecked}（期望 null=半勾）");
                first.SetChecked(true);
                lines.Add($"三态自证 还原：子目录「{first.Name}」CheckStateTrue={first.CheckStateTrue}, Indeterminate={first.CheckStateIndeterminate}；根 IsChecked={root.IsChecked}（期望 True=全勾）");
            }
            else
            {
                lines.Add("三态自证：该目录下没有子目录，无法做半勾验证（如实记录，不编造）");
            }

            var logPath = Path.Combine(AppContext.BaseDirectory, "step2-tree-qa.log");
            File.AppendAllLines(logPath, lines);
            log.Information("Step2 目录树 QA 探针完成：{Lines}", string.Join(" | ", lines));
            Debug.WriteLine(string.Join(Environment.NewLine, lines));
            return root;
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "step2-tree-qa.log"),
                    $"探针失败：{ex}{Environment.NewLine}");
            }
            catch { /* 探针失败不影响启动 */ }
            return null;
        }
    }
}