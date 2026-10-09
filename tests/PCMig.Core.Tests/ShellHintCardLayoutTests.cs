using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ 2026-10-08（用户真机问题 §3：提示卡自适应展开）★ 提示卡**自适应高度**结构契约。
///
/// 为什么改这份测试（旧断言为什么被替换）：
///   · 旧契约（Round-2 2026-10-05）是"**固定高度**"：外层高度恒为 token 176，
///     内容超了就用 <c>MaxLines + TextTrimming="CharacterEllipsis"</c> 丢字。
///   · 用户 2026-10-08 明确判定该口径是缺陷：真实业务信息一长就变成"……"，看不到完整内容。
///   · 新契约是"**Clamp 到上界**"：176 变成**下界/默认高**，真实高度 =
///     <c>Clamp(正文自然所需高, 176, 侧栏允许最大高)</c>，且达到上界后改用**滚动条**而不是省略号。
///   因此本文件把"固定 176"换成"Clamp 到 [176, 上界]"、"必须有 MaxLines/省略号"换成
///   "**禁止** MaxLines/省略号"、"滚动条不可见"换成"滚动条 Auto"。被断言的对象（外层高度语义、
///   每个通道不被截断、溢出只发生在内容视口内、调用方不下发改高度）**意图全部保留**，只是换成新语义。
///
/// 为什么是源码契约而不是像素断言：本机没有可编程的 WinUI 视觉断言能力（同类说明见
/// <c>Batch5UiLayoutContractTests</c>），而"高度如何被决定""哪些属性被禁止"这两类规则
/// **可以在源码层完全锁死**——这也是"不存在逐帧 Layout Height"的可编译判据。
///
/// 真实像素验收（短消息/长消息各一张截图、长消息顶部向上扩展且不覆盖 Step4 与底栏）
/// 另行真机取证，见结论里的"未验证项"。
/// </summary>
public class ShellHintCardLayoutTests
{
    // ── ① 外层高度的语义：token 只是下界，真实高度由代码 Clamp 到上界 ──────────────
    [Fact]
    public void OuterHeight_IsDeclaredAsLowerBoundToken()
    {
        var xaml = ReadRepoFile("src", "PCMig.WinUI", "Views", "ShellHintCard.xaml");
        var card = ExtractElement(xaml, "x:Name=\"HintCardSurface\"");

        // 外层高度仍声明为唯一 token —— 新语义下它是**下界/默认高（176 DIP）**，
        // 运行时由 ShellHintCard 按内容一次性改写（唯一写入点）。
        Assert.Contains("Height=\"{StaticResource PCMigHintCardHeight}\"", card);

        // 不得用 MaxHeight / MinHeight 表达上界（旧实现的自激与"反向突破上界"都来自这两条）。
        Assert.DoesNotContain("MaxHeight=", card);
        Assert.DoesNotContain("MinHeight=", card);

        var materials = ReadRepoFile("src", "PCMig.WinUI", "Themes", "Materials.xaml");
        Assert.Contains("x:Key=\"PCMigHintCardHeight\">176<", materials);

        var code = ReadRepoFile("src", "PCMig.WinUI", "Views", "ShellHintCard.xaml.cs");
        Assert.DoesNotContain("MaxHeight =", code);
        Assert.DoesNotContain("MinHeight =", code);
        Assert.DoesNotContain("SetMaxSurfaceHeight", code);

        // 上界入口仍在：可用高由 MainWindow 按"侧栏高 − Step4 导航高 − 标准段间距"下发。
        Assert.Contains("public void SetAvailableHeight(double availableHeight)", code);

        // 下界仍来自 token，且有兜底常量（资源不可用时绝不让卡片消失）。
        Assert.Contains("PCMigHintCardHeight", code);
        Assert.Contains("FallbackFixedSurfaceHeight", code);
    }

    // ── ② 高度算法必须是"Clamp(内容自然高, 176, 上界)"，且只有一个写入点 ──────────
    [Fact]
    public void AdaptiveHeight_ClampsContentHeightBetweenDefaultAndCeiling()
    {
        var code = ReadRepoFile("src", "PCMig.WinUI", "Views", "ShellHintCard.xaml.cs");

        // 唯一写入点：ApplyAdaptiveHeight 里一次性写终值。
        Assert.Contains("private void ApplyAdaptiveHeight()", code);
        Assert.Contains("HintCardSurface.Height = target;", code);

        // 下界 = token 高；上界 = MainWindow 下发值；目标 = 三值夹取。
        Assert.Contains("var min = ResolveFixedHeight();", code);
        Assert.Contains("var ceiling = _availableHeight < min ? min : _availableHeight;", code);
        Assert.Contains("var target = Clamp(MeasureDesiredSurfaceHeight(), min, ceiling);", code);
        Assert.Contains("private static double Clamp(double value, double min, double max)", code);

        // 未测量（NaN/∞/≤0）⇒ 上界视为无穷，只用 176 下界（装配早期绝不把卡片压成 0 高）。
        Assert.Contains("_availableHeight = measured ? availableHeight : double.MaxValue;", code);

        // 自然高必须来自"不限行"的同步 Measure（这才是"不省略时需要多高"）。
        Assert.Contains("HintContentPanel.Measure(new Size(width, double.PositiveInfinity));", code);
        Assert.Contains("HintContentPanel.DesiredSize.Height", code);

        // 抖动抑制：差值小于阈值时完全不动作（Q1「不能每出现一行字就小幅度变化高度」）。
        Assert.Contains("HeightChangeThreshold", code);
        Assert.Contains("Math.Abs(target - current) < HeightChangeThreshold", code);

        // 只有一处把 Height 写成非 token 表达式：除此之外不允许再有 Height 赋值语句。
        Assert.Equal(1, CountOccurrences(code, "HintCardSurface.Height ="));
    }

    // ── ③ 长内容不得靠截断丢信息（Q5/Q6：MaxLines 与省略号都被禁止）───────────────
    [Fact]
    public void LongContent_IsNeverTrimmedByEllipsisOrMaxLines()
    {
        var xaml = ReadRepoFile("src", "PCMig.WinUI", "Views", "ShellHintCard.xaml");

        // 四个通道都必须完整保留文本；旧断言的 MaxLines(4/3/2/3) 与 CharacterEllipsis
        // 正是用户 2026-10-08 判定为缺陷的那套"信息丢弃手段"，现在**反向断言**它们不存在。
        AssertChannelUntruncated(xaml, "HintOperationalText");
        AssertChannelUntruncated(xaml, "HintObjectText");
        AssertChannelUntruncated(xaml, "HintUserText");
        AssertChannelUntruncated(xaml, "HintErrorText");

        // 换行方式改为纯 Wrap。
        var operational = ExtractElement(xaml, "x:Name=\"HintOperationalText\"");
        Assert.Contains("TextWrapping=\"Wrap\"", operational);

        // 调用方只下发"可用高度上界"，不下发改高度的上限；也不反向订阅卡片的尺寸变化
        //（断言订阅本身，而不是注释里可能出现的关键词）。
        var main = ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml.cs");
        Assert.DoesNotContain("HintCard.SetMaxSurfaceHeight", main);
        Assert.Contains("UpdateHintCardHeight", main);
        Assert.Contains("HintCard.SetAvailableHeight(", main);
        Assert.DoesNotContain("HintCard.SizeChanged +=", main);
    }

    // ── ④ 达到上界仍放不下时，内容区才出现滚动条（Q5）─────────────────────────────
    [Fact]
    public void ScrollbarAppearsOnlyWhenContentExceedsCeiling()
    {
        var xaml = ReadRepoFile("src", "PCMig.WinUI", "Views", "ShellHintCard.xaml");
        var scroll = ExtractElement(xaml, "x:Name=\"HintScroll\"");

        // 滚动条必须是 Auto：装得下时不显示，长到上界仍放不下时出现，
        // 供鼠标滚轮 / 触摸板 / 拖动滚动条查看完整内容。
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", scroll);
        Assert.DoesNotContain("VerticalScrollBarVisibility=\"Hidden\"", scroll);

        // 滚动仍然可用，横向永远不滚（长句靠 Wrap 换行）。
        Assert.Contains("VerticalScrollMode=\"Auto\"", scroll);
        Assert.Contains("HorizontalScrollMode=\"Disabled\"", scroll);
    }

    // ── ⑤ 溢出只发生在内容视口内部（固定五行结构）──────────────────────────────
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

        // 四个语义通道都在内容视口内部。
        // 注意：这里查整份 XAML —— ScrollViewer 是**容器标签**，ExtractElement 对它只能切到开标签的 '>'。
        Assert.Contains("x:Name=\"HintOperationalText\"", xaml);
        Assert.Contains("x:Name=\"HintObjectText\"", xaml);
        Assert.Contains("x:Name=\"HintUserText\"", xaml);
        Assert.Contains("x:Name=\"HintErrorText\"", xaml);

        // 内容必须**底对齐**：这样尺寸只在上方增长，视觉上等于"底边不动、顶部向上长"（Q3）。
        var scrollTag = ExtractElement(xaml, "x:Name=\"HintScroll\"");
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", scrollTag);
        Assert.Contains("VerticalAlignment=\"Bottom\"", xaml);
    }

    // ── ⑥ 上界必须与 Step4 的边界直接相关（Q4：绝不覆盖 Step4 / 底栏）────────────
    [Fact]
    public void CeilingIsDerivedFromStep4Boundary()
    {
        // 上界不是常量、也不是卡片自己算的：它来自 MainWindow 按几何下发的"侧栏高 − 导航高 − 段间距"。
        var main = ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml.cs");
        Assert.Contains("HintCard.SetAvailableHeight(sidebarHeight - navHeight - standardSectionGap);", main);
        Assert.Contains("const double standardSectionGap = 12d;", main);

        var xaml = ReadRepoFile("src", "PCMig.WinUI", "Views", "ShellHintCard.xaml");
        var card = ExtractElement(xaml, "x:Name=\"HintCardSurface\"");

        // 卡片自身的上外边距必须正好是那个"标准段间距"——上界公式里的 −12 就是它，
        // 因此即便卡片顶到上界，顶边也只是贴在 Step4 底部下方一个段间距处。
        Assert.Contains("Margin=\"0,12,0,0\"", card);

        // 代码侧把上界语义写清楚，防止以后有人把它当成"内容可无限增长"。
        var code = ReadRepoFile("src", "PCMig.WinUI", "Views", "ShellHintCard.xaml.cs");
        Assert.Contains("_availableHeight", code);
    }

    // ── 辅助 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 通道文本"未被截断"契约：标签存在、且**没有** MaxLines / TextTrimming / 省略号。
    /// 这是旧 <c>AssertLineLimit</c>（断言 MaxLines=4/3/2/3 + CharacterEllipsis）的**反向**版本。
    /// </summary>
    private static void AssertChannelUntruncated(string xaml, string element)
    {
        var tag = ExtractElement(xaml, $"x:Name=\"{element}\"");
        Assert.DoesNotContain("MaxLines", tag);
        Assert.DoesNotContain("TextTrimming", tag);
        Assert.DoesNotContain("CharacterEllipsis", tag);
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