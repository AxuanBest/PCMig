using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// GUI 资源契约回归（WarnTextColor / ErrorTextColor 缺失缺陷的看门狗）。
///
/// 【背景 — 2026-09-19 实测确认】
/// AppDialog.ApplyContent 按图标挑选语义色：
///   MessageBoxImage.Error   → "ErrorTextColor"
///   MessageBoxImage.Warning → "WarnTextColor"
///   Question / 其它         → "AccentColorBrush"
/// 然后调用 <c>(Brush)FindResource(brushKey)</c>（**不是** TryFindResource）。
///
/// WPF 语义：FindResource 找不到 key 会抛 ResourceReferenceKeyNotFoundException；
/// 该异常在 AppDialog.xaml.cs:39 抛出后，L40 的 ShowDialog() 永远执行不到，
/// 于是弹窗从未显示、Result 保持 None。
/// 崩溃兜底路径（App.xaml.cs:104 传 Error/Warning）外层是 catch { }，
/// 异常被静默吞掉 —— 用户什么也看不到，连崩溃提示都没有。
///
/// 【为什么单独扫 XAML 而不解析】
/// 用真 WPF 运行时验证需要把本测试项目改成 UseWPF + net8.0-windows，
/// 会动到已经有 85 个用例的 testhost 配置，按"最小回归测试"的要求不划算。
/// 因此这里用**静态契约检查**：证明"被引用的 key 在主题里确实有定义"。
/// 运行级别标注见每个用例的注释。
/// </summary>
public class GuiResourceContractTests
{
    /// <summary>三套主题必须各自定义完整（key 数一致是项目既有不变量）。</summary>
    private static readonly string[] ThemeFiles =
    {
        @"src\PCMig.Gui\Theme\Glass.xaml",
        @"src\PCMig.Gui\Theme\Glass.Dark.xaml",
        @"src\PCMig.Gui\Theme\Classic.xaml",
    };

    /// <summary>AppDialog 会通过 FindResource 请求的语义色 key（缺失即抛异常）。</summary>
    private static readonly string[] RequiredSemanticKeys =
    {
        "ErrorTextColor",
        "WarnTextColor",
        "AccentColorBrush",
    };

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到仓库根（未发现 PCMig.sln）。起始：" + AppContext.BaseDirectory);
    }

    private static string ReadTheme(string relPath)
    {
        var full = Path.Combine(FindRepoRoot(), relPath);
        Assert.True(File.Exists(full), "找不到主题文件：" + full);
        return File.ReadAllText(full);
    }

    /// <summary>
    /// [运行级别：L0 静态契约扫描]
    /// 三套主题都必须定义 AppDialog 需要的三个语义色 key。
    /// 本用例是 WarnTextColor/ErrorTextColor 缺失缺陷的直接看门狗 ——
    /// 修复前它会 FAIL，修复后必须 PASS。
    /// </summary>
    [Fact]
    public void Themes_DefineAllSemanticColorKeysUsedByAppDialog()
    {
        var missing = new List<string>();
        foreach (var theme in ThemeFiles)
        {
            var text = ReadTheme(theme);
            foreach (var key in RequiredSemanticKeys)
            {
                if (!text.Contains("x:Key=\"" + key + "\"", StringComparison.Ordinal))
                {
                    missing.Add(Path.GetFileName(theme) + " 缺 " + key);
                }
            }
        }

        Assert.True(missing.Count == 0,
            "主题缺少 AppDialog 会 FindResource 的语义色 key（缺失将导致弹窗抛异常、永不显示）：\n  "
            + string.Join("\n  ", missing));
    }

    /// <summary>
    /// [运行级别：L0 静态契约扫描]
    /// 全局范围内"被引用的 key"必须在某套主题里有定义 —— 防止今后再出现悬空引用。
    /// 检查对象：src\PCMig.Gui 下所有 {DynamicResource Xxx} / {StaticResource Xxx} 引用。
    /// 允许例外：WPF 内置 key（SystemColors / 控件模板部件等）不在本检查范围，
    /// 因此只断言项目自定义的语义色前缀（*Color / *Brush / *Surface）。
    /// </summary>
    [Fact]
    public void NoDanglingSemanticResourceReferences()
    {
        var root = FindRepoRoot();
        var guiDir = Path.Combine(root, "src", "PCMig.Gui");
        Assert.True(Directory.Exists(guiDir), "找不到 Gui 目录：" + guiDir);

        // 收集**全部 Gui XAML** 里定义的 key。
        // 注意（2026-09-19 实测修正）：最早只扫三套主题，导致把 Controls.xaml 里
        // 以 Style 形式定义的 GlassInnerSurface / GlassContentSurface /
        // DialogSurface / DialogInnerSurface 误报成"悬空"。
        // 它们确实是合法定义（只是控件样式而非主题 token），故范围必须覆盖整个 Gui。
        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var xaml in Directory.EnumerateFiles(guiDir, "*.xaml", SearchOption.AllDirectories))
        {
            var xamlText = File.ReadAllText(xaml);
            var marker = "x:Key=" + (char)34;
            foreach (var seg in xamlText.Split(marker))
            {
                var end = seg.IndexOf((char)34);
                if (end > 0) { defined.Add(seg.Substring(0, end)); }
            }
        }

        // 收集所有引用
        var dangling = new List<string>();
        foreach (var file in Directory.EnumerateFiles(guiDir, "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(
                         text, "\\{(?:Dynamic|Static)Resource\\s+([A-Za-z_][A-Za-z0-9_]*)\\}"))
            {
                var key = m.Groups[1].Value;
                // 只关心项目自定义语义色（避免误报 WPF 内置）
                var isProjectSemantic = key.EndsWith("Color", StringComparison.Ordinal)
                                        || key.EndsWith("Brush", StringComparison.Ordinal)
                                        || key.EndsWith("Surface", StringComparison.Ordinal);
                if (!isProjectSemantic) continue;
                // Controls.xaml 自身定义的样式 key 也在 defined 里；未定义即悬空
                if (!defined.Contains(key) && !text.Contains("x:Key=\"" + key + "\"", StringComparison.Ordinal))
                {
                    dangling.Add(Path.GetFileName(file) + " → " + key);
                }
            }
        }

        Assert.True(dangling.Count == 0,
            "发现悬空的自定义语义资源引用（被引用但三套主题都没定义）：\n  "
            + string.Join("\n  ", dangling.Distinct()));
    }
}
