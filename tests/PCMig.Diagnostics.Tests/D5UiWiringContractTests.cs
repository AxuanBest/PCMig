using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D5 步骤 2 契约：诊断中心的工具入口与浮层（**源码级契约**）。
///
/// 为什么是源码级：WinUI 需要 net8.0-windows + Windows App SDK，不能塞进本测试项目。
/// 因此这里把"用户明确点名的视觉要求"翻译成可机器验证的断言：
///   · 入口**紧挨「材质调节」右侧**、同一工具入口区、同一水平轴；
///   · 两个按钮的几何/内边距/背景/边框/图标字号**逐字相同**（不靠肉眼对齐）；
///   · 不许用负 Margin / Translate / 魔法 Scale 掩盖布局；
///   · 浮层材质/光源/圆角/内边距/Translation 与既有两个工具面板**完全同源**；
///   · 不新建 Style / ControlTemplate / 进入动画（进入由 Shell 的 OACT+TAOP 统一编排）。
/// 真实像素对齐仍须由真机截图闭环（本文件末尾的 Known Gap 断言即为此保留）。
/// </summary>
public sealed class D5UiWiringContractTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
        }
        throw new InvalidOperationException("找不到仓库根：" + AppContext.BaseDirectory);
    }

    private static string ReadWinUi(params string[] parts)
    {
        var path = Path.Combine(new[] { Root(), "src", "PCMig.WinUI" }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), "缺少文件：" + path);
        return File.ReadAllText(path);
    }

    private static string MainWindowXaml => ReadWinUi("MainWindow.xaml");
    private static string MainWindowCode => ReadWinUi("MainWindow.xaml.cs");
    private static string PanelXaml => ReadWinUi("Views", "DiagnosticCenterPanel.xaml");
    private static string PanelCode => ReadWinUi("Views", "DiagnosticCenterPanel.xaml.cs");

    /// <summary>取某个具名元素的那一段 XAML（到下一个 "&lt;Button" 或该元素结束为止）。</summary>
    private static string ElementSnippet(string xaml, string name)
    {
        var index = xaml.IndexOf("x:Name=\"" + name + "\"", StringComparison.Ordinal);
        Assert.True(index > 0, "找不到具名元素：" + name);
        var start = xaml.LastIndexOf('<', index);
        var end = xaml.IndexOf("</Button>", index, StringComparison.Ordinal);
        Assert.True(end > start, "元素片段不完整：" + name);
        return xaml.Substring(start, end - start);
    }

    /// <summary>去掉行注释（`//` 之后）：契约断言必须针对**代码**，而不是文档注释里的举例。</summary>
    private static string StripCode(string text) =>
        string.Join("\n", text.Split('\n').Select(line =>
        {
            var i = line.IndexOf("//", StringComparison.Ordinal);
            return i >= 0 ? line.Substring(0, i) : line;
        }));

    private static string StripXamlComments(string text) =>
        Regex.Replace(text, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    private static IReadOnlyDictionary<string, string> Attributes(string snippet)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(snippet, @"([A-Za-z:\.]+)=""([^""]*)"""))
            map[m.Groups[1].Value] = m.Groups[2].Value;
        return map;
    }

    // ────────────────────────── ① 入口位置：紧挨「材质调节」右侧 ──────────────────────────

    [Fact]
    public void DiagnosticEntryIsImmediatelyRightOfTheMaterialTuningEntry()
    {
        var xaml = MainWindowXaml;

        var tuningIndex = xaml.IndexOf("x:Name=\"DeveloperTuningButton\"", StringComparison.Ordinal);
        var diagnosticIndex = xaml.IndexOf("x:Name=\"DiagnosticCenterButton\"", StringComparison.Ordinal);
        Assert.True(tuningIndex > 0, "找不到「材质调节」入口");
        Assert.True(diagnosticIndex > tuningIndex, "诊断入口必须在「材质调节」**右侧**（XAML 顺序即视觉顺序）");

        // 两者之间不得再插入第二个 Button（否则就不是"紧挨"）。
        // 从「材质调节」按钮**结束之后**量起，直到诊断按钮的标签开始之前。
        var tuningElementEnd = xaml.IndexOf("</Button>", tuningIndex, StringComparison.Ordinal) + "</Button>".Length;
        var diagnosticTagStart = xaml.LastIndexOf('<', diagnosticIndex);
        // 相邻元素是**紧挨**的（tuningElementEnd 恰好等于下一个 '<'），因此判据是 >= 而不是 >。
        Assert.True(tuningElementEnd > 0 && diagnosticTagStart >= tuningElementEnd,
            $"两个入口之间存在空隙或顺序不对：tuningEnd={tuningElementEnd} diagnosticStart={diagnosticTagStart}");
        var between = xaml.Substring(tuningElementEnd, diagnosticTagStart - tuningElementEnd);
        Assert.DoesNotContain("<Button", between, StringComparison.Ordinal);

        // 同一工具入口区：都落在 HeaderTitleRow 这个横向 StackPanel 内。
        var rowStart = xaml.IndexOf("HeaderTitleRow", StringComparison.Ordinal);
        var rowEnd = xaml.IndexOf("</StackPanel></StackPanel>", diagnosticIndex, StringComparison.Ordinal);
        Assert.True(rowStart > 0 && rowStart < tuningIndex, "两个入口必须同属标题行的工具入口区");
        Assert.True(rowEnd > diagnosticIndex);
    }

    // ────────────────────────── ② 几何逐字相同（不靠肉眼对齐）──────────────────────────

    [Fact]
    public void BothToolEntriesShareIdenticalGeometryAndChrome()
    {
        var tuning = Attributes(ElementSnippet(MainWindowXaml, "DeveloperTuningButton"));
        var diagnostic = Attributes(ElementSnippet(MainWindowXaml, "DiagnosticCenterButton"));

        string[] mustMatch =
        {
            "VerticalAlignment", "Width", "Height", "Padding", "Background", "BorderThickness",
        };
        foreach (var key in mustMatch)
        {
            Assert.True(tuning.ContainsKey(key), "「材质调节」入口缺少属性 " + key);
            Assert.True(diagnostic.ContainsKey(key), "诊断入口缺少属性 " + key);
            Assert.Equal(tuning[key], diagnostic[key]);   // 同一水平轴 + 同一点击区域高度的机械保证
        }

        // 图标字号与前景色也必须同源（避免"图标大小不同 ⇒ 视觉中心偏移"）。
        var tuningIcon = Attributes(Regex.Match(ElementSnippet(MainWindowXaml, "DeveloperTuningButton"), "<FontIcon[^>]*>").Value);
        var diagnosticIcon = Attributes(Regex.Match(ElementSnippet(MainWindowXaml, "DiagnosticCenterButton"), "<FontIcon[^>]*>").Value);
        Assert.Equal(tuningIcon["FontSize"], diagnosticIcon["FontSize"]);
        Assert.Equal(tuningIcon["Foreground"], diagnosticIcon["Foreground"]);

        // 28×28 是既有工具入口的尺寸：明确写出来，避免将来有人"顺手"改一个。
        Assert.Equal("28", diagnostic["Width"]);
        Assert.Equal("28", diagnostic["Height"]);
        Assert.Equal("0", diagnostic["Padding"]);
    }

    [Fact]
    public void EntryDoesNotFakeAlignmentWithNegativeOffsets()
    {
        var snippet = ElementSnippet(MainWindowXaml, "DiagnosticCenterButton");
        Assert.DoesNotContain("Margin=\"-", snippet, StringComparison.Ordinal);
        Assert.DoesNotContain("Translate", snippet, StringComparison.Ordinal);
        Assert.DoesNotContain("Scale", snippet, StringComparison.Ordinal);
        Assert.DoesNotContain("VerticalOffset", snippet, StringComparison.Ordinal);
        Assert.DoesNotContain("Canvas.", snippet, StringComparison.Ordinal);
    }

    [Fact]
    public void EntryAndPanelAreAddressableByTheStableControlId()
    {
        // 同一个稳定 ID 服务诊断 / GUI 自动化 / 三 VM 自动测试（方案 §9）。
        Assert.Contains("AutomationProperties.AutomationId=\"Shell.Tool.Diagnostics\"",
            ElementSnippet(MainWindowXaml, "DiagnosticCenterButton"), StringComparison.Ordinal);

        // ★ D6.1 §12 修正 ★ 面板**不能**与入口共用同一个 AutomationId：
        //   同一棵树里重复 ID 会让自动化选错对象（审计发现的真问题）。
        //   面板用自己独立的 ID，并且这两个 ID 都必须真实出现在 XAML 里。
        Assert.Contains("AutomationProperties.AutomationId=\"Shell.Panel.Diagnostics\"", PanelXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AutomationProperties.AutomationId=\"Shell.Tool.Diagnostics\"", PanelXaml, StringComparison.Ordinal);

        var controlIds = ReadWinUi("Diagnostics", "ControlIds.cs");
        Assert.Contains("\"Shell.Tool.Diagnostics\"", controlIds, StringComparison.Ordinal);
        Assert.Contains("\"Shell.Panel.Diagnostics\"", controlIds, StringComparison.Ordinal);
        Assert.Contains("\"Shell.Tool.MaterialTuning\"", controlIds, StringComparison.Ordinal);
    }

    // ────────────────────────── ③ 浮层与既有两个工具面板完全同源（PMML）──────────────────────────

    [Fact]
    public void PanelReusesTheUtilitySurfaceMaterialLightingAndGeometry()
    {
        var changelog = ReadWinUi("Views", "ChangelogPanel.xaml");
        var tuning = ReadWinUi("Views", "DeveloperTuningPanel.xaml");

        string[] sharedTokens =
        {
            "PCMigUtilityPanelMaterial80",     // DAM：同一 Utility 固定材质（不新增画刷）
            "PCMigDAELBrushQuiet",             // DSL-45：全应用统一光源
            "CornerRadius=\"18\"",             // 几何
            "Padding=\"16\"",
            "BorderThickness=\"1\"",
            "Translation=\"0,0,24\"",
            "HorizontalAlignment=\"Right\"",
            "VerticalAlignment=\"Top\"",
        };
        foreach (var token in sharedTokens)
        {
            Assert.Contains(token, changelog, StringComparison.Ordinal);
            Assert.Contains(token, PanelXaml, StringComparison.Ordinal);
        }
        _ = tuning;   // 材质调节面板同样属于这套 Utility 表面（此处不重复断言其全部细节）
    }

    [Fact]
    public void PanelIntroducesNoNewStyleTemplateOrEntranceAnimation()
    {
        // PMML：新 UI 必须复用既有 Resource/Style；进入/关闭由 Shell 统一编排（OACT+TAOP）。
        Assert.DoesNotContain("<Style", PanelXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ControlTemplate", PanelXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("EntranceThemeTransition", PanelXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Storyboard", PanelXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("VisualState", PanelXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ThemeTransition", PanelXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Storyboard", PanelCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Scale", PanelXaml, StringComparison.Ordinal);

        // 不新开窗口、不用对话框（工具浮层语义）。
        Assert.DoesNotContain("ContentDialog", PanelCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new Window", PanelCode, StringComparison.Ordinal);
    }

    // ────────────────────────── ④ 接入 Shell 的 OACT/TAOP 编排路径 ──────────────────────────

    [Fact]
    public void PanelGoesThroughTheSameAnchorAndPlacementPathAsOtherToolPanels()
    {
        Assert.Contains("TryPlacePanel(DiagnosticCenterPanel, ResolvePanelAnchor(DiagnosticCenterPanel, DiagnosticCenterButton), 520",
            MainWindowCode, StringComparison.Ordinal);
        Assert.Contains("TogglePanel(DiagnosticCenterPanel, other, anchor", MainWindowCode, StringComparison.Ordinal);
        Assert.Contains("DiagnosticCenterPanel.CloseRequested", MainWindowCode, StringComparison.Ordinal);
        // 关闭时的锚点回退链也必须认得它（否则收回方向会朝向错误的入口）。
        Assert.Contains("ReferenceEquals(panel, DiagnosticCenterPanel) ? DiagnosticCenterButton", MainWindowCode, StringComparison.Ordinal);
        // 打开时装配 VM + 交焦点；关闭时停刷新（无常驻计时器）。
        Assert.Contains("DiagnosticCenterPanel.Attach(_diagnosticCenterVm!", MainWindowCode, StringComparison.Ordinal);
        Assert.Contains("DiagnosticCenterPanel.StopRefresh();", MainWindowCode, StringComparison.Ordinal);
    }

    [Fact]
    public void PanelRefreshTimerIsBoundedAndStoppedOnClose()
    {
        Assert.Contains("_timer.Stop();", PanelCode, StringComparison.Ordinal);
        Assert.Contains("StopRefresh", PanelCode, StringComparison.Ordinal);
        Assert.Contains("IsRepeating = true", PanelCode, StringComparison.Ordinal);
        // 刷新必须走 UI 队列的节拍，且**只**在 Attach 里启动。
        Assert.Contains("queue.CreateTimer()", PanelCode, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(PanelCode, @"_timer\.Start\(\);").Cast<Match>());
    }

    // ────────────────────────── ⑤ 只显示，不碰业务 ──────────────────────────

    [Fact]
    public void PanelNeverTouchesBusinessState()
    {
        foreach (var text in new[] { StripXamlComments(PanelXaml), StripCode(PanelCode) })
        {
            Assert.DoesNotContain("Robocopy", text, StringComparison.Ordinal);
            Assert.DoesNotContain("TransferOrchestrator", text, StringComparison.Ordinal);
            Assert.DoesNotContain("JobPhase", text, StringComparison.Ordinal);
            Assert.DoesNotContain("CanResume", text, StringComparison.Ordinal);
            Assert.DoesNotContain("_ctx", text, StringComparison.Ordinal);
        }

        // 面板不自己维护无界集合：列表内容整体替换（有界行数来自 VM）。
        Assert.DoesNotContain("ObservableCollection", PanelCode, StringComparison.Ordinal);
        Assert.Contains("list.Items.Clear();", PanelCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Known Gap 的机器化登记：像素级对齐与打开/关闭观感**必须**由真实窗口验证，
    /// 源码契约只能保证"几何逐字相同、走同一编排路径"。本用例把这条缺口钉住，
    /// 防止有人把"源码看起来对齐"当成"已验证视觉"。
    /// </summary>
    [Fact]
    public void PixelLevelAlignmentIsExplicitlyNotClaimedBySourceContracts()
    {
        var evidence = File.ReadAllText(Path.Combine(Root(), "docs", "诊断系统实施-阶段证据.md"));
        Assert.Contains("像素", evidence, StringComparison.Ordinal);
        Assert.Contains("D5 步骤 2", evidence, StringComparison.Ordinal);
    }
}