using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ Round-2 2026-10-05（用户指令 §5 + §9）★ Step3 顶部摘要（总百分比 / 总字节 / 状态句）**共面**结构契约。
///
/// 用户标注截图证据：62.3% / 110.17 GB / 176.9 GB / 状态句 三者不在同一视觉平面。XAML 根因：
///   · <c>TotalBytesText</c> 与 <c>StateLineText</c> 都是 <c>VerticalAlignment="Bottom"</c>；
///   · 而 40px 的 <c>PCMigTextTotalPercent</c> 带显式 <c>LineHeight="60"</c>，行盒比字形高得多
///   ⇒ 一个 60 DIP 高的行盒与两个 Bottom 对齐的小文本不可能重心重合。
///
/// 修复口径（§5）：三列全部 <c>VerticalAlignment="Center"</c> + 整行 <c>MinHeight="52"</c>；
/// 百分比去掉显式行高；状态句居中 + MaxLines=2 + 省略号；**绝不靠正负 Margin 凑对齐**（PMML-R10）。
/// 真实像素验收（percentCenterY / bytesCenterY / stateCenterY，差值 ≤2 DIP）另行真机几何 dump；
/// 本契约只锁"结构不许回退"。
/// </summary>
public class Step3SummaryAlignmentTests
{
    // ── ① 三组必须在同一垂直平面上居中 ─────────────────────────────────────────
    [Fact]
    public void PercentBytesStateCentersAligned()
    {
        var xaml = ReadRepoFile("src", "PCMig.WinUI", "Views", "Step3ProgressPage.xaml");

        var percent = ExtractElement(xaml, "x:Name=\"TotalPercentText\"");
        var bytes = ExtractElement(xaml, "x:Name=\"TotalBytesText\"");
        var state = ExtractElement(xaml, "x:Name=\"StateLineText\"");

        Assert.Contains("VerticalAlignment=\"Center\"", percent);
        Assert.Contains("VerticalAlignment=\"Center\"", bytes);
        Assert.Contains("VerticalAlignment=\"Center\"", state);

        // 旧的底对齐写法必须彻底消失（它是"不共面"的直接原因）。
        Assert.DoesNotContain("VerticalAlignment=\"Bottom\"", percent);
        Assert.DoesNotContain("VerticalAlignment=\"Bottom\"", bytes);
        Assert.DoesNotContain("VerticalAlignment=\"Bottom\"", state);

        // 整行提供稳定垂直基准；三列宽度分配保持 A6 的 Auto/Auto/*（状态句仍可折行、不被右缘裁掉）。
        Assert.Contains("MinHeight=\"52\"", xaml);
        Assert.Contains("VerticalAlignment=\"Center\"", ExtractElement(xaml, "ColumnSpacing=\"12\" MinHeight=\"52\""));
        Assert.True(CountOccurrences(xaml, "<ColumnDefinition Width=\"Auto\"/>") >= 2,
            "顶部摘要行必须至少有两个 Auto 列（百分比 + 字节数）");
        Assert.Contains("<ColumnDefinition Width=\"*\"/>", xaml);

        // 禁止用正负 Margin 凑对齐（§5 明令）。
        Assert.DoesNotContain("Margin", percent);
        Assert.DoesNotContain("Margin", bytes);
        Assert.DoesNotContain("Margin", state);

        // 40px 百分比样式不得再带显式 LineHeight（60 DIP 的行盒正是"重心不重合"的另一半原因）。
        // 注意：Style 是容器标签，必须切到 </Style>，不能用 ExtractElement（那只到开标签的 '>'）。
        var typography = ReadRepoFile("src", "PCMig.WinUI", "Themes", "Typography.xaml");
        var styleStart = typography.IndexOf("x:Key=\"PCMigTextTotalPercent\"", StringComparison.Ordinal);
        Assert.True(styleStart > 0, "PCMigTextTotalPercent 必须存在");
        var totalPercentStyle = typography[styleStart..typography.IndexOf("</Style>", styleStart, StringComparison.Ordinal)];
        Assert.DoesNotContain("LineHeight", totalPercentStyle);
        Assert.Contains("<Setter Property=\"FontSize\" Value=\"40\"/>", totalPercentStyle);
    }

    // ── ② 长状态句不得把百分比挤偏（只允许在状态句列内折行/截断）─────────────────
    [Fact]
    public void LongStateDoesNotPushPercentVertically()
    {
        var xaml = ReadRepoFile("src", "PCMig.WinUI", "Views", "Step3ProgressPage.xaml");
        var state = ExtractElement(xaml, "x:Name=\"StateLineText\"");

        // 状态句最多两行 + 省略号 ⇒ 即使很长的文案也不会把整行撑高到影响其它两组的位置。
        Assert.Contains("MaxLines=\"2\"", state);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", state);
        Assert.Contains("TextWrapping=\"Wrap\"", state);

        // 百分比与字节数都不换行（它们的宽度由 Auto 列决定，不会被状态句挤压换行）。
        var percent = ExtractElement(xaml, "x:Name=\"TotalPercentText\"");
        var bytes = ExtractElement(xaml, "x:Name=\"TotalBytesText\"");
        Assert.Contains("TextWrapping=\"NoWrap\"", bytes);
        Assert.DoesNotContain("TextWrapping=\"Wrap\"", percent);
    }

    // ── 辅助 ────────────────────────────────────────────────────────────────
    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static string ExtractElement(string text, string marker)
    {
        var hit = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(hit > 0, $"找不到标记：{marker}");
        var start = text.LastIndexOf('<', hit);
        Assert.True(start >= 0, $"标记之前没有标签起始符：{marker}");
        var gt = text.IndexOf('>', hit);
        var selfClose = text.IndexOf("/>", hit, StringComparison.Ordinal);
        var end = selfClose >= 0 && (gt < 0 || selfClose <= gt) ? selfClose + 2 : gt + 1;
        return text[start..end];
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var path = Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"文件不存在：{path}");
        return File.ReadAllText(path);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到含 PCMig.sln 的仓库根：" + AppContext.BaseDirectory);
    }
}