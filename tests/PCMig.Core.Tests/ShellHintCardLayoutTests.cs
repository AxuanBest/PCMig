using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ Round-2 2026-10-05（用户指令 §4 + §9）★ 提示卡**固定尺寸**结构契约（UI-07 返修）。
///
/// 为什么是源码契约而不是像素断言：本机没有可编程的 WinUI 视觉断言能力（同类说明见
/// <c>Batch5UiLayoutContractTests</c>），而"内容变化绝不改变外层高度"这条规则**可以在源码层完全锁死**：
///   · 外层高度必须来自唯一 token（<c>PCMigHintCardHeight</c>），不得再出现 MaxHeight/MinHeight 自适应；
///   · 内容溢出必须只发生在 Row1 的 ScrollViewer 内部，且视觉滚动条隐藏（滚轮仍可用）；
///   · 每个通道必须有 MaxLines + 省略号，长句不可能撑高卡片；
///   · 调用方不得监听 <c>HintCard.SizeChanged</c>（否则"内容 → 高度"自激链复活）。
///
/// 对应的用户证据（m01282 §0/§4）：提示卡在 30 s 因 OperationalStatus 长句"变得很高"、37.5 s 又缩回；
/// 用户已明确接受并选择**固定尺寸**。真实像素验收（短消息与长消息各一张截图、同一窗口模式下外层高度必须
/// 相同）另行真机取证。
/// </summary>
public class ShellHintCardLayoutTests
{
    // ── ① 内容变化不得改变外层高度 ──────────────────────────────────────────────
    [Fact]
    public void ContentChange_DoesNotChangeOuterHeight()
    {
        var xaml = ReadRepoFile("src", "PCMig.WinUI", "Views", "ShellHintCard.xaml");
        var card = ExtractElement(xaml, "x:Name=\"HintCardSurface\"");

        // 外层高度 = 唯一 token（与 Materials.xaml 的 PCMigHintCardHeight 同源）。
        Assert.Contains("Height=\"{StaticResource PCMigHintCardHeight}\"", card);

        // 不得再有 MaxHeight / MinHeight 自适应（旧实现的自激与"反向突破上界"都来自这两条）。
        Assert.DoesNotContain("MaxHeight=", card);
        Assert.DoesNotContain("MinHeight=", card);

        var materials = ReadRepoFile("src", "PCMig.WinUI", "Themes", "Materials.xaml");
        Assert.Contains("x:Key=\"PCMigHintCardHeight\">176<", materials);

        var code = ReadRepoFile("src", "PCMig.WinUI", "Views", "ShellHintCard.xaml.cs");
        Assert.DoesNotContain("MaxHeight =", code);
        Assert.DoesNotContain("MinHeight =", code);
        Assert.DoesNotContain("SetMaxSurfaceHeight", code);

        // 唯一的高度写入点：可用高 ≥ token 用 token，否则夹到可用高（"断点换档"只有这一处）。
        Assert.Contains("public void SetAvailableHeight(double availableHeight)", code);
        Assert.Contains("availableHeight >= fixedHeight ? fixedHeight : availableHeight", code);

        // 高度写入必须在 SetAvailableHeight 之内被 token 兜底覆盖（未测量 ⇒ 用固定 token 高）。
        Assert.Contains("PCMigHintCardHeight", code);
        Assert.Contains("FallbackFixedSurfaceHeight", code);
    }

    // ── ② 超长 OperationalStatus 不得放大外层卡 ────────────────────────────────
    [Fact]
    public void LongOperationalStatus_DoesNotGrowOuterCard()
    {
        var xaml = ReadRepoFile("src", "PCMig.WinUI", "Views", "ShellHintCard.xaml");

        // §4 推荐的行数上限：OperationalStatus 4 / CurrentObjectStatus 3 / UserHint 2 / ErrorSummary 3。
        AssertLineLimit(xaml, "HintOperationalText", 4);
        AssertLineLimit(xaml, "HintObjectText", 3);
        AssertLineLimit(xaml, "HintUserText", 2);
        AssertLineLimit(xaml, "HintErrorText", 3);

        // 调用方只下发"可用高度"，不再下发改高度的上界；也不反向订阅卡片的尺寸变化
        //（断言订阅本身，而不是注释里可能出现的关键词）。
        var main = ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml.cs");
        Assert.DoesNotContain("HintCard.SetMaxSurfaceHeight", main);
        Assert.Contains("UpdateHintCardHeight", main);
        Assert.Contains("HintCard.SetAvailableHeight(", main);
        Assert.DoesNotContain("HintCard.SizeChanged +=", main);
    }

    // ── ③ 溢出只发生在内容视口内部（固定五行结构）──────────────────────────────
    [Fact]
    public void OverflowRemainsInsideViewport()
    {
        var xaml = ReadRepoFile("src", "PCMig.WinUI", "Views", "ShellHintCard.xaml");

        // Row1 是唯一的 "*" 行（唯一可变高度区），其余四行 Auto。
        var starRows = CountOccurrences(xaml, "Height=\"*\"");
        Assert.Equal(1, starRows);
        Assert.Equal(4, CountOccurrences(xaml, "<RowDefinition Height=\"Auto\"/>"));

        // 内容视口落在 Row1；状态行 / 分隔线 / 署名分别在 Row2 / Row3 / Row4。
        var scroll = ExtractElement(xaml, "x:Name=\"HintScroll\"");
        Assert.Contains("Grid.Row=\"1\"", scroll);
        Assert.Contains("Grid.Row=\"2\"", xaml);
        Assert.Contains("Grid.Row=\"3\"", xaml);
        Assert.Contains("Grid.Row=\"4\"", xaml);

        // 四个语义通道都在内容视口内部（各自带 MaxLines，见测试②）。
        // 注意：这里查整份 XAML —— ScrollViewer 是**容器标签**，ExtractElement 对它只能切到开标签的 '>'。
        Assert.Contains("x:Name=\"HintOperationalText\"", xaml);
        Assert.Contains("x:Name=\"HintObjectText\"", xaml);
        Assert.Contains("x:Name=\"HintUserText\"", xaml);
        Assert.Contains("x:Name=\"HintErrorText\"", xaml);
    }

    // ── ④ 标准窗口下不得出现可见滚动条（隐藏轨道，但保留滚轮）──────────────────
    [Fact]
    public void NoVisibleScrollbarInStandardMode()
    {
        var xaml = ReadRepoFile("src", "PCMig.WinUI", "Views", "ShellHintCard.xaml");
        var scroll = ExtractElement(xaml, "x:Name=\"HintScroll\"");

        Assert.Contains("VerticalScrollBarVisibility=\"Hidden\"", scroll);
        Assert.DoesNotContain("VerticalScrollBarVisibility=\"Auto\"", scroll);
        Assert.DoesNotContain("VerticalScrollBarVisibility=\"Visible\"", scroll);

        // 仍然可滚动（滚轮/触摸），只是滚动条不可见。
        Assert.Contains("VerticalScrollMode=\"Auto\"", scroll);
        // 横向永远不滚（长句靠 Wrap + MaxLines 处理）。
        Assert.Contains("HorizontalScrollMode=\"Disabled\"", scroll);
    }

    // ── 辅助 ────────────────────────────────────────────────────────────────
    private static void AssertLineLimit(string xaml, string element, int maxLines)
    {
        var tag = ExtractElement(xaml, $"x:Name=\"{element}\"");
        Assert.Contains($"MaxLines=\"{maxLines}\"", tag);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", tag);
    }

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