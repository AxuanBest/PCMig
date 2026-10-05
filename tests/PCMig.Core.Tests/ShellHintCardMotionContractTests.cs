using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// UI Closure 2026-10-05 **返修**：提示卡（ShellHintCard）动效与高度契约测试。
///
/// 背景（用户真机复验直接证伪了上一轮结论）：
///   · 文字全部堆到卡片顶部、互相重叠、内容越多越乱 → 根因是入场动画写 <c>Visual.Offset</c>
///     （Offset 是"相对父 Visual 的位置属性"，会把 XAML 已排好的行位置塌向父容器原点）；
///   · 出现不该出现的滚动条 → 根因是 <c>HintScroll.SizeChanged → 动画 Height → 尺寸再变</c> 自激，
///     叠加 "MaxHeight 被下界 160 反向抬高" 这两条链。
///
/// 为什么用源码契约：本机没有可编程的 WinUI 布局断言（WinUI 3 无法在无头环境量布局），
/// 而这些缺陷的根因就是**代码结构本身**。因此把"结构不许回退"锁死 —— 谁再把 Offset 写回来、
/// 谁再把 Height 接到 SizeChanged 上、谁再把 MaxHeight 抬到真实上界之上，测试立刻红。
/// 真实像素/几何验收另行用真机截图 + bounds dump + 视觉模型判定完成。
/// </summary>
public class ShellHintCardMotionContractTests
{
    // ── 1. 入场动画禁止走 Visual.Offset（返修第一根因）──────────────────────────────
    [Fact]
    public void HintCard_LineEntranceMustNotUseVisualOffset()
    {
        var code = StripComments(ReadRepoFile("src", "PCMig.WinUI", "Views", "ShellHintCard.xaml.cs"));

        Assert.DoesNotContain("StartAnimation(\"Offset\"", code);
        Assert.DoesNotContain("visual.Offset", code);
        Assert.DoesNotContain(".Offset =", code);
        // 反向要求：必须走 MotionDirector 的 Translation 路线。
        Assert.Contains("MotionDirector.PlayLineEntrance(target)", code);
    }

    // ── 2. MotionDirector 提供 post-layout Translation helper（唯一实现处）──────────
    [Fact]
    public void MotionDirector_LineEntranceUsesTranslationChannelOnly()
    {
        var motion = StripComments(ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MotionDirector.cs"));

        Assert.Contains("public static void PlayLineEntrance(FrameworkElement? element)", motion);
        Assert.Contains("ElementCompositionPreview.SetIsTranslationEnabled(element, true);", motion);
        Assert.Contains("visual.StartAnimation(\"Translation\", slide);", motion);
        // 接管旧动画：同一元素上绝不并行多份入场动画（半途重启会跳变）。
        Assert.Contains("visual.StopAnimation(\"Translation\");", motion);
    }

    // ── 3. Reduced Motion / 折叠：必须归零到可见终态（Translation=0 / Opacity=1）────
    [Fact]
    public void LineEntrance_HonoursReducedMotionAndResetsToRest()
    {
        var motion = StripComments(ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MotionDirector.cs"));

        // 门控：系统关闭动画时不做位移，直接吸附终态。
        Assert.Contains("public static void ResetLineEntrance(FrameworkElement? element)", motion);
        Assert.Contains("visual.Properties.InsertVector3(\"Translation\", Vector3.Zero);", motion);
        Assert.Contains("visual.Opacity = 1f;", motion);

        // PlayLineEntrance 的第一道门就是系统动画偏好；折叠时调用方也会主动 Reset。
        var play = motion.IndexOf("public static void PlayLineEntrance", StringComparison.Ordinal);
        var reset = motion.IndexOf("public static void ResetLineEntrance", StringComparison.Ordinal);
        Assert.True(play > 0 && reset > play, "PlayLineEntrance 必须先于 ResetLineEntrance 定义（同段代码内自洽）");
        var body = motion[play..reset];
        Assert.Contains("if (!SystemAnimationsEnabled)", body);

        var card = StripComments(ReadRepoFile("src", "PCMig.WinUI", "Views", "ShellHintCard.xaml.cs"));
        Assert.Contains("MotionDirector.ResetLineEntrance(target);", card);   // 折叠即归零
    }

    // ── 4. 动画是**语义级**的：不再"字符串变了就重播位移"（返修第二根因）────────────
    [Fact]
    public void HintCard_EntranceIsSemanticNotPerTextChange()
    {
        var card = StripComments(ReadRepoFile("src", "PCMig.WinUI", "Views", "ShellHintCard.xaml.cs"));

        // 三种语义策略必须都在（AppearOnly / ObjectSwitch / FlowStatus）。
        Assert.Contains("LineEntrancePolicy.ObjectSwitch", card);
        Assert.Contains("LineEntrancePolicy.AppearOnly", card);
        Assert.Contains("LineEntrancePolicy.FlowStatus", card);
        // 最小间隔节流：同一元素 600 ms 内只允许一次位移动画。
        Assert.Contains("MinLineEntranceIntervalMs", card);
        // 对象切换判定用"首行对象键"，不是整串文本。
        Assert.Contains("FirstLine(next)", card);

        // 旧实现的写法必须消失（"文本变了就无条件播"）。
        Assert.DoesNotContain("if (shouldShow && textChanged) PlayEntrance(target);", card);
    }

    // ── 5. 高度不得再被 SizeChanged 驱动（自激链 / 假滚动条根因）──────────────────
    [Fact]
    public void HintCard_HeightMustNotBeDrivenByScrollSizeChanged()
    {
        var card = StripComments(ReadRepoFile("src", "PCMig.WinUI", "Views", "ShellHintCard.xaml.cs"));

        Assert.DoesNotContain("HintScroll.SizeChanged", card);
        Assert.DoesNotContain("AnimateSurfaceHeight", card);
        Assert.DoesNotContain("SetTargetProperty(animation, \"Height\")", card);
        Assert.DoesNotContain("Storyboard", card);   // 本卡不再有任何 Storyboard 高度动画
    }

    // ── 6. MaxHeight 必须等于真实物理上界，不得被"下界 160"反向抬高（返修第四根因）──
    [Fact]
    public void HintCard_MaxHeightNeverExceedsRealCeiling()
    {
        var card = StripComments(ReadRepoFile("src", "PCMig.WinUI", "Views", "ShellHintCard.xaml.cs"));

        Assert.Contains("HintCardSurface.MaxHeight = safeCeiling;", card);
        // 160 只作为**理想最小高度**，且必须被 ceiling 夹住。
        Assert.Contains("HintCardSurface.MinHeight = Math.Min(IdealMinSurfaceHeight, safeCeiling);", card);
        // 旧的"反向突破"写法必须消失。
        Assert.DoesNotContain("maxHeight < 160d ? 160d : maxHeight", card);
        Assert.DoesNotContain("if (target < 160d) target = 160d;", card);
    }

    // ── 7. Token 化：时长 / 位移取自 Motion.xaml，调用点不写死数值（PMML-R14）────────
    [Fact]
    public void MotionXaml_DeclaresHintLineTokens()
    {
        var motionXaml = ReadRepoFile("src", "PCMig.WinUI", "Themes", "Motion.xaml");
        Assert.Contains("PCMigMotionHintLineDuration", motionXaml);
        Assert.Contains("PCMigMotionHintLineOffset", motionXaml);

        var motion = StripComments(ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MotionDirector.cs"));
        Assert.Contains("\"PCMigMotionHintLineDuration\"", motion);
        Assert.Contains("\"PCMigMotionHintLineOffset\"", motion);
    }

    // ── 辅助 ──────────────────────────────────────────────────────────────────────
    /// <summary>去掉 <c>//</c> 行注释：注释里写"禁止 Offset"这类说明不算违规代码。</summary>
    private static string StripComments(string source)
    {
        var lines = source.Split('\n').Select(line =>
        {
            var index = line.IndexOf("//", StringComparison.Ordinal);
            return index >= 0 ? line[..index] : line;
        });
        return string.Join('\n', lines);
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