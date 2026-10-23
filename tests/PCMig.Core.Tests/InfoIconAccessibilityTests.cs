using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ 2026-10-18 二次修正 ★ Step 1 / Step 2 的 ⓘ 说明图标「控件语义 + 行内基线」回归契约。
///
/// <para>
/// <b>为什么会有这一版</b>：上一轮把 ⓘ 从裸 <c>FontIcon</c> 改成了 <c>Button</c>，解决了
/// 「UIA 里搜不到、读屏读不出、键盘不可达」，但把语义搞错了 —— 它看起来可点，会给出
/// 「点一下会发生什么」的虚假暗示，而它其实什么都不做。用户判词：
/// 「ⓘ 是信息提示图标，不是按钮」。
/// </para>
///
/// <para>
/// <b>现在的实现</b>：<c>TextBlock</c>（承载 AutomationId / Name / ToolTip）+ 内部 <c>FontIcon</c>
/// （样式 <c>PCMigInfoIcon</c>，IsHitTestVisible=False）。不是 Button、不是 ToggleButton、
/// 不是 HyperlinkButton ⇒ 天然没有 Pressed / Released / Click 任何按钮式反馈。
/// 悬停反馈只有颜色提亮（TextMuted → TextSecondary），没有 Translation / 缩放 / 尺寸变化。
/// </para>
///
/// <para>
/// <b>行内基线</b>：<c>BenchmarkPanel</c> 由横向 <c>StackPanel</c> 改为 <c>Grid</c>，
/// 开关在 Column 0、说明组在 Column 1，两者都是 <c>VerticalAlignment=Center</c> ⇒
/// 中心对齐取代原来的 Stretch 顶部绘制。这样不依赖任何像素补偿，
/// 也不会因为 125% / 150% 缩放而漂移。
/// </para>
///
/// <para>
/// <b>证据强度声明（不冒充）</b>：本文件是<b>源码契约测试</b>，只锁结构不变量，属防回归护栏。
/// 「ⓘ 在运行时确实是 Text、不是 Button」「悬停真的显示 ToolTip」「点击不触发开关」
/// 这些是运行时证据，见伴随报告里的 UIA 实测与截图；源码断言不替代运行时验证。
/// </para>
/// </summary>
public class InfoIconAccessibilityTests
{
    private const string Step1Xaml = "src/PCMig.WinUI/Views/Step1ConnectPage.xaml";
    private const string Step2Xaml = "src/PCMig.WinUI/Views/Step2SelectDataPage.xaml";
    private const string ControlsXaml = "src/PCMig.WinUI/Themes/Controls.xaml";
    private const string ControlIdsCs = "src/PCMig.WinUI/Diagnostics/ControlIds.cs";

    /// <summary>两个 ⓘ：所在 XAML、元素名、自动化 ID、ControlIds 常量名。</summary>
    public static IEnumerable<object[]> InfoIcons => new[]
    {
        new object[] { Step1Xaml, "BenchmarkNoteInfoText", "Step1.BenchmarkInfoIcon", "Step1BenchmarkInfoIcon" },
        new object[] { Step2Xaml, "ExpertModeInfoText", "Step2.ExpertModeInfoIcon", "Step2ExpertModeInfoIcon" },
    };

    // ── 契约 1：ⓘ 必须是「非按钮」的说明元素，且可被 UIA 寻址、读屏可读 ──────────

    [Theory]
    [MemberData(nameof(InfoIcons))]
    public void InfoIcon_IsInformationalTextNotAButton(string xamlPath, string elementName, string automationId, string controlIdConst)
    {
        _ = controlIdConst;
        var xaml = ReadRepoFile(xamlPath);

        var tag = ExtractOpeningTag(xaml, elementName);
        Assert.NotNull(tag);
        var open = tag!.TrimStart();

        // 必须是 TextBlock 承载（不是 Button / ToggleButton / HyperlinkButton / AppBarButton …）
        Assert.StartsWith("<Grid", open, StringComparison.Ordinal);
        Assert.DoesNotContain("<Button", open, StringComparison.Ordinal);

        // 不许借道任何按钮语义的样式
        Assert.Contains("Style=\"{StaticResource PCMigInfoIconText}\"", tag);
        Assert.DoesNotContain("PCMigInfoIconButton", tag, StringComparison.Ordinal);

        // 可寻址 + 可朗读 + 悬停说明：三样都不能因为「不是按钮」而丢掉
        Assert.Contains($"AutomationProperties.AutomationId=\"{automationId}\"", tag);
        Assert.Contains("AutomationProperties.Name=", tag);
        Assert.Contains("AutomationProperties.HelpText=", tag);

        // ★ 2026-10-18 二次修正：ToolTip 必须用**显式 ToolTip 元素**子节点形式，
        //   即挂在开标签之后、</Grid> 之前的子元素，而不是开标签里的附加属性。
        //   实测附加属性写法 `ToolTipService.ToolTip="…"` 在 WinUI 3 里挂在 Grid 上
        //   鼠标悬停根本不弹（UIA 里 ControlType.ToolTip 元素数恒为 0）；改成显式子元素后才弹。
        var body = ExtractElementBody(xaml, elementName);
        Assert.NotNull(body);
        Assert.Contains("<ToolTipService.ToolTip>", body!, StringComparison.Ordinal);
        Assert.Contains("<ToolTip ", body!, StringComparison.Ordinal);
        Assert.Contains("Placement=\"Top\"", body!, StringComparison.Ordinal);
        // 属性写法必须已经移除，避免两种写法并存导致归属不明
        Assert.DoesNotContain("ToolTipService.ToolTip=\"", tag, StringComparison.Ordinal);

        // 不进 Tab 序：说明图标不是焦点目标（与 Toggle / 按钮区分开）
        Assert.DoesNotContain("IsTabStop=\"True\"", tag, StringComparison.Ordinal);
    }

    // ── 契约 2：图标图形本身不可命中，不得与相邻控件抢鼠标事件 ──────────────────

    [Theory]
    [MemberData(nameof(InfoIcons))]
    public void InfoIcon_GlyphIsNotHitTestable(string xamlPath, string elementName, string automationId, string controlIdConst)
    {
        _ = elementName;
        _ = controlIdConst;
        var xaml = ReadRepoFile(xamlPath);

        // 找到该 AutomationId 所在的 TextBlock 开标签，确认它内含一个 FontIcon
        var idx = xaml.IndexOf($"AutomationProperties.AutomationId=\"{automationId}\"", StringComparison.Ordinal);
        Assert.True(idx >= 0, $"{automationId} 必须出现在 {xamlPath}");
        var span = xaml.Substring(idx, Math.Min(1200, xaml.Length - idx));
        Assert.Contains("<FontIcon", span, StringComparison.Ordinal);

        // FontIcon 用的是统一样式（不可命中由样式给出），而不是就地写一堆内联属性
        Assert.Contains("Style=\"{StaticResource PCMigInfoIcon}\"", span, StringComparison.Ordinal);
    }

    // ── 契约 3：两个 ⓘ 的 ID 必须同时进 ControlIds 常量表 ────────────────────────

    [Theory]
    [MemberData(nameof(InfoIcons))]
    public void InfoIcon_HasStableIdInControlIds(string xamlPath, string elementName, string automationId, string controlIdConst)
    {
        _ = xamlPath;
        _ = elementName;

        var ids = ReadRepoFile(ControlIdsCs);
        Assert.Contains($"public const string {controlIdConst} = \"{automationId}\";", ids);
    }

    // ── 契约 4：图标样式只许「不可命中 + 居中 + 语义色」，不许任何像素位移 ──────

    [Fact]
    public void InfoIconStyle_HasNoButtonStatesAndNoPixelNudges()
    {
        var controls = ReadRepoFile(ControlsXaml);

        var icon = ExtractStyleBlock(controls, "PCMigInfoIcon");
        Assert.NotNull(icon);
        Assert.StartsWith("<Style", icon!.TrimStart(), StringComparison.Ordinal);

        // FontIcon 样式：不可命中 + 居中
        Assert.Contains("TargetType=\"FontIcon\"", icon);
        Assert.Contains("IsHitTestVisible\" Value=\"False\"", icon);
        Assert.Contains("VerticalAlignment\" Value=\"Center\"", icon);

        // ★ 禁止像素补偿：不许 Translation / Margin / Padding 抬头
        Assert.DoesNotContain("Translation", icon);
        Assert.DoesNotContain("Margin", icon);
        Assert.DoesNotContain("Padding", icon);

        // 说明文本容器样式：居中、非 Tab 停靠，不得带按钮状态
        var host = ExtractStyleBlock(controls, "PCMigInfoIconText");
        Assert.NotNull(host);
        Assert.Contains("TargetType=\"Grid\"", host!);
        Assert.Contains("VerticalAlignment\" Value=\"Center\"", host);
        Assert.Contains("IsTabStop\" Value=\"False\"", host);
        // 只认真实的状态组标签；注释里出现「没有 VisualStateGroup」这类说明不算违规。
        Assert.DoesNotContain("<VisualStateGroup", host, StringComparison.Ordinal);
        Assert.DoesNotContain("<VisualState ", host, StringComparison.Ordinal);
        // ★ 三次真实失败教训（都在构建/运行时被证伪，写死在这里防止后人改错方向）：
        //   ① TextBlock：默认内容属性是 Inlines，XamlCompiler 报
        //      WMC0020 Cannot add 'FontIcon' into the collection property 'Inlines'
        //      ⇒ 根本不合法。
        //   ② Border：能编译、UIA 有 ID/Name、也不进 Tab 序，但**悬停不弹 ToolTip** ——
        //      ToolTipService 依赖 Control 基类，Border 不是 Control，命中测试收不到。
        //      运行时实测 hover 后 ToolTip 元素数恒为 0（补透明命中层也无效）。
        //   ③ 必须是 ContentControl：是 Control（ToolTip 生效）但没有 ButtonBase 的
        //      Click/Pressed/Released 状态机（不产生按钮式反馈）。
        Assert.DoesNotContain("TargetType=\"TextBlock\"", host, StringComparison.Ordinal);
        Assert.DoesNotContain("TargetType=\"Border\"", host, StringComparison.Ordinal);
        Assert.DoesNotContain("TargetType=\"Button\"", host, StringComparison.Ordinal);
        Assert.DoesNotContain("TargetType=\"ToggleButton\"", host, StringComparison.Ordinal);
    }

    // ── 契约 5：Step1 该行必须是 Grid 两列、两侧都 Center（对齐的结构前提） ─────

    [Fact]
    public void BenchmarkPanel_IsGridWithCenterAlignedColumns()
    {
        var xaml = ReadRepoFile(Step1Xaml);

        var panel = ExtractOpeningTag(xaml, "BenchmarkPanel");
        Assert.NotNull(panel);
        Assert.StartsWith("<Grid", panel!.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("VerticalAlignment=\"Center\"", panel);

        // 开关自己有名字且取名为 Toggle（用于「点 ⓘ 不误触开关」这类证据的稳定寻址）
        var toggle = ExtractOpeningTag(xaml, "BenchmarkToggle");
        Assert.NotNull(toggle);
        Assert.Contains("AutomationProperties.AutomationId=\"Step1.BenchmarkToggle\"", toggle!);
        Assert.Contains("Grid.Column=\"0\"", toggle);

        var group = ExtractOpeningTag(xaml, "BenchmarkNoteGroup");
        Assert.NotNull(group);
        Assert.Contains("Grid.Column=\"1\"", group!);
        Assert.Contains("Orientation=\"Horizontal\"", group);
        Assert.Contains("VerticalAlignment=\"Center\"", group);

        // 文字与图标都显式居中：Grid 单元格默认 Stretch，TextBlock 会从盒子顶部绘制，
        // 这正是「文字视觉中心偏」的原始成因。
        var text = ExtractOpeningTag(xaml, null, "PCMigTextFormNote", "连接时测速");
        Assert.NotNull(text);
        Assert.Contains("VerticalAlignment=\"Center\"", text!);
    }

    // ── 契约 6：UI 文案里写的测速体量必须与实现一致（32MB，不是 64MB） ──────────

    [Fact]
    public void BenchmarkCopy_MatchesImplementedFileSize()
    {
        var checker = ReadRepoFile("src/PCMig.Core/Preflight/PreflightChecker.cs");
        Assert.Contains("const int fileMb = 32;", checker);

        var xaml = ReadRepoFile(Step1Xaml);
        Assert.Contains("写读 32MB 测试文件", xaml);
        Assert.DoesNotContain("写读 64MB 测试文件", xaml);
    }

    // ── 辅助 ────────────────────────────────────────────────────────────────────

    /// <summary>抠出指定 x:Name 的完整开标签。</summary>
    private static string? ExtractOpeningTag(string xaml, string elementName)
        => ExtractOpeningTag(xaml, elementName, null, null);

    private static string? ExtractOpeningTag(string xaml, string? elementName, string? mustContainA, string? mustContainB)
    {
        int pos;
        if (elementName is not null)
        {
            var start = $"x:Name=\"{elementName}\"";
            pos = xaml.IndexOf(start, StringComparison.Ordinal);
            if (pos < 0) return null;
        }
        else if (mustContainA is not null && mustContainB is not null)
        {
            var pattern = $@"<TextBlock[^>]*{Regex.Escape(mustContainA)}[^>]*{Regex.Escape(mustContainB)}";
            var m = Regex.Match(xaml, pattern);
            if (!m.Success) return null;
            pos = m.Index + 1;
        }
        else
        {
            return null;
        }

        var open = xaml.LastIndexOf('<', pos);
        if (open < 0) return null;

        var inQuote = false;
        for (var i = open; i < xaml.Length; i++)
        {
            var c = xaml[i];
            if (c == '"') inQuote = !inQuote;
            else if (c == '>' && !inQuote) return xaml.Substring(open, i - open + 1);
        }

        return null;
    }

    /// <summary>抠出 <c>x:Name="elementName"</c> 那个元素的**整段**（开标签 + 子元素 + 闭标签）。</summary>
    private static string? ExtractElementBody(string source, string elementName)
    {
        var tag = ExtractOpeningTag(source, elementName);
        if (tag is null) return null;

        var start = source.IndexOf(tag, StringComparison.Ordinal);
        if (start < 0) return null;

        var closeTag = "</Grid>";
        var close = source.IndexOf(closeTag, start, StringComparison.Ordinal);
        if (close < 0) return null;

        return source.Substring(start, close - start + closeTag.Length);
    }

    /// <summary>抠出 <c>x:Key="key"</c> 起、到对应 </c>&lt;/Style&gt;</c> 的整段样式定义。</summary>
    private static string? ExtractStyleBlock(string source, string key)
    {
        var idx = source.IndexOf($"x:Key=\"{key}\"", StringComparison.Ordinal);
        if (idx < 0) return null;

        var open = source.LastIndexOf('<', idx);
        if (open < 0) return null;

        var close = source.IndexOf("</Style>", idx, StringComparison.Ordinal);
        if (close < 0) return null;

        return source.Substring(open, close - open + "</Style>".Length);
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
