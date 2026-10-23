using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ 2026-10-18（用户实测）★ Step 1「连接时测速」开关点不动的回归契约。
///
/// <para>
/// <b>缺陷现象</b>：用户在 Step 1 点击「连接时测速」ToggleSwitch，开关状态不翻转，点击无任何反应。
/// </para>
///
/// <para>
/// <b>根因（已用运行时布局转储 + 真实鼠标点击定位并验证，非源码猜测）</b>：
/// <list type="number">
/// <item>XAML 为「状态行」声明了第 4 个 <c>RowDefinition</c>，
///       <c>StatusScroll</c> / <c>StatusLineText</c> / <c>StatusAdviceText</c> 都写 <c>Grid.Row="3"</c>。</item>
/// <item><c>ShellResponsiveLayout.ApplyFormReflow</c> 的 Wide/Normal 分支无条件
///       <c>while (Count &gt; 3) RemoveAt(...)</c>，把这一行删掉 ⇒ 运行时只剩 3 行。</item>
/// <item>Grid 对越界行号的行为是 <b>clamp 到最后一行</b>，于是状态区落回 Row2，
///       与 <c>BenchmarkPanel</c>（测速开关）同格、同纵向区间。</item>
/// <item>状态区在可视树中后声明、Z 序在上层 ⇒ 盖住测速开关的整块命中区，点击实际命中状态区。</item>
/// </list>
/// 运行时实测数据（修复前）：<c>RowDefinitions.Count=3</c>、BenchmarkPanel 纵向 116–156、
/// StatusScroll 纵向 108–156（完全覆盖）。修复后：<c>Count=4</c>、BenchmarkPanel 116–156、
/// StatusScroll 164–202（完全分离）。
/// </para>
///
/// <para>
/// <b>关于本文件的证据强度（如实声明，不冒充）</b>：
/// 本文件是<b>源码契约测试</b>，只锁定「行数不再被裁到不足」这一结构不变量，属于<b>防回归护栏</b>。
/// 真正的运行时证据是另外两件，均已实测：
/// ① 布局转储（<c>RowDefinitions.Count</c> 与各控件 <c>TransformToVisual</c> 的实际站位）；
/// ② 真实鼠标点击（<c>SetCursorPos</c> + <c>mouse_event</c>）驱动 UIA <c>TogglePattern</c>，修复后 3/3 翻转、
///    负向对照 0/3 翻转（把 <c>required: 4</c> 改回 <c>3</c> 即复现缺陷）。
/// 源码断言不能替代上述两项，此处也不作此宣称。
/// </para>
/// </summary>
public class Step1BenchmarkToggleRowTests
{
    private const string LayoutPath = "src/PCMig.WinUI/Presentation/ShellResponsiveLayout.cs";
    private const string XamlPath = "src/PCMig.WinUI/Views/Step1ConnectPage.xaml";

    // ── 契约 1：不得再出现「无条件裁到 3 行」 ───────────────────────────────────

    [Fact]
    public void Reflow_DoesNotTrimRowsToThreeAnyMore()
    {
        var src = ReadRepoFile(LayoutPath);

        // 旧写法（缺陷本体）：while (formGrid.RowDefinitions.Count > 3) formGrid.RowDefinitions.RemoveAt(...)
        Assert.DoesNotContain("RowDefinitions.Count > 3", src);
        Assert.DoesNotContain("required: 3", src);
    }

    // ── 契约 2：两档都必须为状态行至少保留一行 ──────────────────────────────────

    [Theory]
    [InlineData("required: 4")]  // Wide / Normal：3 个表单行 + 1 个状态行
    [InlineData("required: 6")]  // Compact：5 个表单行 + 1 个状态行
    public void Reflow_KeepsEnoughRowsForStatusLine(string expected)
    {
        var src = ReadRepoFile(LayoutPath);

        Assert.Contains(expected, src);
    }

    // ── 契约 3：状态行站位由代码统一设置，而不是散落的字面量 ────────────────────

    [Fact]
    public void Reflow_PlacesStatusRowExplicitly()
    {
        var src = ReadRepoFile(LayoutPath);

        // 状态行三个成员必须先被登记，再在两条分支里各自被放置到正确的行。
        Assert.Contains("\"StatusScroll\"", src);
        Assert.Contains("\"StatusLineText\"", src);
        Assert.Contains("\"StatusAdviceText\"", src);
        Assert.Contains("PlaceStatusRow(page, formGrid, 3)", src);
        Assert.Contains("PlaceStatusRow(page, formGrid, 5)", src);

        // 行数收敛必须走「既补齐也裁剪」的辅助方法，不能只删不补。
        Assert.Contains("TrimFormRows(formGrid, required:", src);
        Assert.True(
            Regex.IsMatch(src, @"private static void TrimFormRows\(Grid formGrid, int required\)[\s\S]{0,400}?RowDefinitions\.Count < required[\s\S]{0,400}?RowDefinitions\.Count > required"),
            "TrimFormRows 必须同时保证「行数不足要补齐」和「行数多余要裁剪」");
    }

    // ── 契约 4：XAML 声明与代码期望的行号必须对得上 ─────────────────────────────

    [Fact]
    public void Xaml_DeclaresFourRowsAndStatusAtRowThree()
    {
        var xaml = ReadRepoFile(XamlPath);

        var formGrid = ExtractFormGridRowDefinitions(xaml);
        Assert.NotNull(formGrid);

        var rowCount = Regex.Matches(formGrid!, @"<RowDefinition\s").Count;
        Assert.True(rowCount == 4,
            $"FormGrid 应声明 4 行（3 个表单行 + 1 个状态行），实际 {rowCount} 行；" +
            "若确实改了结构，必须同步改 ShellResponsiveLayout 的 PlaceStatusRow/TrimFormRows 目标行号与本测试。");

        // 状态行的三个成员必须都写着 Grid.Row="3"，与代码里的 PlaceStatusRow(..., 3) 一致。
        foreach (var name in new[] { "StatusScroll", "StatusLineText", "StatusAdviceText" })
        {
            var tag = ExtractOpeningTag(xaml, name);
            Assert.NotNull(tag);
            Assert.Contains("Grid.Row=\"3\"", tag!);
        }
    }

    // ── 契约 5：测速面板与状态行不得占用同一行（同格的根因） ────────────────────

    [Fact]
    public void Xaml_BenchmarkPanelAndStatusRowUseDifferentRows()
    {
        var xaml = ReadRepoFile(XamlPath);

        var benchmark = ExtractOpeningTag(xaml, "BenchmarkPanel");
        Assert.NotNull(benchmark);
        Assert.Contains("Grid.Row=\"2\"", benchmark!);

        var status = ExtractOpeningTag(xaml, "StatusScroll");
        Assert.NotNull(status);
        Assert.Contains("Grid.Row=\"3\"", status!);
    }

    // ── 辅助：从 XAML 里抠出 FormGrid 的行定义块 ────────────────────────────────

    private static string? ExtractFormGridRowDefinitions(string xaml)
    {
        var m = Regex.Match(
            xaml,
            @"<Grid\s+x:Name=""FormGrid""[\s\S]*?</Grid\.RowDefinitions>",
            RegexOptions.None);

        return m.Success ? m.Value : null;
    }

    /// <summary>抠出指定 x:Name 的**完整开标签文本**（从 < 到第一个 >，跳过属性值内的引号）。</summary>
    private static string? ExtractOpeningTag(string xaml, string elementName)
    {
        var start = xaml.IndexOf($"x:Name=\"{elementName}\"", StringComparison.Ordinal);
        if (start < 0) return null;

        // 往回找这个元素的 '<'
        var open = xaml.LastIndexOf('<', start);
        if (open < 0) return null;

        // 从 open 往后找到第一个不在引号内的 '>'
        var inQuote = false;
        for (var i = open; i < xaml.Length; i++)
        {
            var c = xaml[i];
            if (c == '"') inQuote = !inQuote;
            else if (c == '>' && !inQuote) return xaml.Substring(open, i - open + 1);
        }

        return null;
    }

    private static string ReadRepoFile(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("找不到 PCMig.sln，无法定位仓库根。");
    }
}
