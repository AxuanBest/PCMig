using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// 提示卡（ShellHintCard）动效与高度契约测试。
///
/// 背景（用户真机复验直接证伪了上一轮结论）：
///   · 文字全部堆到卡片顶部、互相重叠、内容越多越乱 → 根因是入场动画写 <c>Visual.Offset</c>
///     （Offset 是"相对父 Visual 的位置属性"，会把 XAML 已排好的行位置塌向父容器原点）；
///   · 出现不该出现的滚动条 → 根因是 <c>HintScroll.SizeChanged → 动画 Height → 尺寸再变</c> 自激，
///     叠加 "MaxHeight 被下界 160 反向抬高" 这两条链。
///
/// ★ 2026-10-08 更新（用户真机问题 §3：自适应展开）★
///   高度口径从"**固定尺寸**"改成"**Clamp(内容自然高, 176, 上界)**"。旧断言（第 6 条）断的是
///   `HintCardSurface.Height = availableHeight >= fixedHeight ? fixedHeight : availableHeight;` ——
///   那条代码在新实现里已被删除，所以这里把它换成新语义的等价断言：
///   **上界仍然存在、仍然只用夹取、仍然只有一处写入**，同时新增三条"新口径专属"的判据：
///     · 高度只写一次终值（不是逐帧累加）；
///     · 出现逐帧 Timer / Storyboard / 动画化 Height 一律判红（这是"不许 UI 线程逐帧手算高度"的可编译判据）；
///     · 揭示动画只走 Composition 的 Clip 偏移，且**不碰 Scale/CenterPoint**（不许把文字拉伸变形）。
///
/// 为什么用源码契约：本机没有可编程的 WinUI 布局断言（WinUI 3 无法在无头环境量布局），
/// 而这些缺陷的根因就是**代码结构本身**。因此把"结构不许回退"锁死 —— 谁再把 Offset 写回来、
/// 谁再把 Height 接到 SizeChanged 上、谁再引入逐帧高度动画，测试立刻红。
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

        // ★ 2026-10-08 ★ 新实现只订阅**内容面板**的尺寸变化作为触发信号，
        //   且测量与写高被推迟到下一拍（RequestAdaptiveHeightUpdate）——不是直接在回调里改高度。
        Assert.Contains("HintContentPanel.SizeChanged", card);
        Assert.Contains("RequestAdaptiveHeightUpdate();", card);
    }

    // ── 6. 高度永远是"Clamp 到上界"，且**一次写终值**（2026-10-08 新口径）──────────
    [Fact]
    public void HintCard_HeightIsClampedAndWrittenOnce()
    {
        var card = StripComments(ReadRepoFile("src", "PCMig.WinUI", "Views", "ShellHintCard.xaml.cs"));

        // 下界仍是 token（未测量到可用空间时绝不把卡片压成 0 高），且带兜底常量。
        Assert.Contains("PCMigHintCardHeight", card);
        Assert.Contains("FallbackFixedSurfaceHeight = 176d", card);

        // 上界入口 + 三值夹取：可用高 ≥ 内容所需 ⇒ 用内容所需；否则夹到上界。
        Assert.Contains("public void SetAvailableHeight(double availableHeight)", card);
        Assert.Contains("var ceiling = _availableHeight < min ? min : _availableHeight;", card);
        Assert.Contains("var target = Clamp(MeasureDesiredSurfaceHeight(), min, ceiling);", card);

        // ★ 唯一的写出语句 ★：证明"一次到目标高度"而不是逐帧累加高度。
        var writes = Regex.Matches(card, @"HintCardSurface\.Height\s*=").Count;
        Assert.Equal(1, writes);
        Assert.Contains("HintCardSurface.Height = target;", card);

        // 旧实现的固定高写法与旧自适应约束必须彻底消失。
        Assert.DoesNotContain("HintCardSurface.Height = availableHeight >= fixedHeight", card);
        Assert.DoesNotContain("MaxHeight =", card);
        Assert.DoesNotContain("MinHeight =", card);
        Assert.DoesNotContain("IdealMinSurfaceHeight", card);
        Assert.DoesNotContain("maxHeight < 160d ? 160d : maxHeight", card);
        Assert.DoesNotContain("if (target < 160d) target = 160d;", card);
    }

    // ── 7. ★ 2026-10-08 ★ 展开动画必须是"布局一次到位 + Composition 视觉展开"────────
    [Fact]
    public void HintCard_ExpansionIsCompositionRevealNotPerFrameLayout()
    {
        var card = StripComments(ReadRepoFile("src", "PCMig.WinUI", "Views", "ShellHintCard.xaml.cs"));
        var motion = StripComments(ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MotionDirector.cs"));

        // ① 不允许任何"逐帧改高度"的实现：没有定时器、没有 Height 属性动画、没有 Height 累加。
        //    注意不能用 DoesNotContain("Timer")——DispatcherQueueTimer 本身包含 "Timer"，会误伤。
        Assert.DoesNotContain("DispatcherQueueTimer", card);
        Assert.DoesNotContain("CreateTimer", card);
        Assert.DoesNotContain("Tick +=", card);
        Assert.DoesNotContain("Height +=", card);
        Assert.DoesNotContain("Height -=", card);
        Assert.DoesNotContain("DoubleAnimation", card);

        // ② 展开/缩回都交给 MotionDirector 的 Composition 揭示方法。
        Assert.Contains("MotionDirector.PlayHintCardReveal(HintCardSurface", card);
        Assert.Contains("MotionDirector.PlayHintCardCollapse(HintCardSurface", card);
        Assert.Contains("public static void PlayHintCardReveal(FrameworkElement? element", motion);
        Assert.Contains("public static void PlayHintCardCollapse(FrameworkElement? element", motion);

        // ③ MotionDirector 侧：揭示靠 Clip 偏移动画（合成层），不是 Height 动画。
        //   ★ 2026-10-08 二次返修（用户 m01438）★ RectangleClip 兜底路径已被**删除**：
        //     它自身就是横向裁切源（`Left = 0` 且 `Right = 0` ⇒ 裁剪矩形宽度为 0），
        //     而需求只需要纵向揭示。因此这里从"断言 rect 动画存在"改成"断言它彻底不复存在"。
        Assert.DoesNotContain("CreateRectangleClip", motion);
        Assert.DoesNotContain("rect.StartAnimation", motion);
        Assert.Contains("inset.StartAnimation(\"TopInset\", slide);", motion);

        // ④ 禁止把文字整体拉伸变形：本卡与揭示实现都不许碰 Scale / CenterPoint。
        Assert.DoesNotContain("Scale", card);
        Assert.DoesNotContain("CenterPoint", card);
        var reveal = motion.IndexOf("public static void PlayHintCardReveal", StringComparison.Ordinal);
        var collapse = motion.IndexOf("public static void PlayHintCardCollapse", StringComparison.Ordinal);
        Assert.True(reveal > 0 && collapse > reveal, "PlayHintCardReveal 必须先于 PlayHintCardCollapse 定义");
        Assert.DoesNotContain("Scale", motion[reveal..collapse]);

        // ⑤ Reduced Motion：展开/缩回都必须先看系统动画偏好，关闭时直接落终态；
        //    "落终态"就是 MotionDirector 里那个可重复调用的 ResetHintCardVisual（摘掩码 / 归零 Translation / Opacity=1）。
        Assert.Contains("if (!SystemAnimationsEnabled)", motion[reveal..collapse]);
        Assert.Contains("public static void ResetHintCardVisual(FrameworkElement? element", motion);

        // ⑥ 缓动必须是无过冲的减速曲线（复用既有 CreateDecelerateEasing，不引入弹簧）。
        Assert.Contains("CreateDecelerateEasing(compositor)", motion[reveal..collapse]);
    }

    // ── 8. Token 化：时长 / 位移取自 Motion.xaml，调用点不写死数值（PMML-R14）────────
    [Fact]
    public void MotionXaml_DeclaresHintLineTokens()
    {
        var motionXaml = ReadRepoFile("src", "PCMig.WinUI", "Themes", "Motion.xaml");
        Assert.Contains("PCMigMotionHintLineDuration", motionXaml);
        Assert.Contains("PCMigMotionHintLineOffset", motionXaml);

        // ★ 2026-10-08 ★ 自适应展开专属 token（时长 260 ms / 缩回 220 ms / 正文上浮 8 DIP）。
        Assert.Contains("PCMigMotionHintRevealDuration", motionXaml);
        Assert.Contains("PCMigMotionHintCollapseDuration", motionXaml);
        Assert.Contains("PCMigMotionHintRevealOffset", motionXaml);

        var motion = StripComments(ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MotionDirector.cs"));
        Assert.Contains("\"PCMigMotionHintLineDuration\"", motion);
        Assert.Contains("\"PCMigMotionHintLineOffset\"", motion);
        Assert.Contains("HintRevealDurationKey = \"PCMigMotionHintRevealDuration\"", motion);
        Assert.Contains("HintCollapseDurationKey = \"PCMigMotionHintCollapseDuration\"", motion);
        Assert.Contains("HintRevealOffsetKey = \"PCMigMotionHintRevealOffset\"", motion);
    }

    // ── 9. ★ 2026-10-08 二次返修（用户 m01438）★ 揭示遮罩绝不允许裁掉正文左侧 ──────
    [Fact]
    public void HintCard_RevealClipMustNeverCropLeftSide()
    {
        var motion = StripComments(ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MotionDirector.cs"));

        // 根因留档：`Compositor.CreateInsetClip` 的真实参数顺序是 (leftInset, topInset, rightInset, bottomInset)。
        // 旧代码按 (top, left, bottom, right) 写成 CreateInsetClip(fromOffset, 0f, 0f, 0f)，
        // 把"遮住顶部的高度"灌进了 leftInset ⇒ 整个卡片 Visual 左侧被裁；
        // 而动画挂在 TopInset（0→0）无效、Reset 又只清 TopInset ⇒ 左侧裁切永久残留。
        Assert.DoesNotContain("CreateInsetClip(fromOffset, 0f, 0f, 0f)", motion);

        // 唯一允许的写法：**具名实参**，横向一律 0，只有 topInset 参与揭示。
        Assert.Contains("leftInset: 0f", motion);
        Assert.Contains("topInset: fromOffset", motion);
        Assert.Contains("rightInset: 0f", motion);
        Assert.Contains("bottomInset: 0f", motion);

        // 终态必须**摘除** Clip，而不是把偏移写回 0（写回 0 覆盖不到未知残留 ⇒ 会永久裁切正文）。
        var reset = motion.IndexOf("public static void ResetHintCardVisual", StringComparison.Ordinal);
        Assert.True(reset > 0, "ResetHintCardVisual 必须存在");
        var resetBody = motion[reset..];
        Assert.Contains("visual.Clip = null;", resetBody);
        Assert.DoesNotContain("rect.Offset = Vector2.Zero;", resetBody);
    }

    // ── 10. ★ 2026-10-08 二次返修（用户 m01438 §四）★ 动画 QA 对照开关 ────────────
    [Fact]
    public void HintCard_AnimationCanBeSwitchedOffForQa()
    {
        var motion = StripComments(ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MotionDirector.cs"));

        // 用户要求：同一个 QA exe 用 PCMIG_HINTCARD_ANIMATION=0 / =1 分别启动做对照。
        Assert.Contains("PCMIG_HINTCARD_ANIMATION", motion);
        Assert.Contains("HintCardAnimationEnabled", motion);

        // 开关关掉时必须"直接最终布局"：展开与缩回都要先看开关，并落到 ResetHintCardVisual 的终态
        //（不挂 Clip、不做 reveal / translation / opacity）。
        var reveal = motion.IndexOf("public static void PlayHintCardReveal", StringComparison.Ordinal);
        var collapse = motion.IndexOf("public static void PlayHintCardCollapse", StringComparison.Ordinal);
        Assert.True(reveal > 0 && collapse > reveal, "PlayHintCardReveal 必须先于 PlayHintCardCollapse 定义");
        Assert.Contains("if (!HintCardAnimationEnabled)", motion[reveal..collapse]);
        Assert.Contains("if (!HintCardAnimationEnabled)", motion[collapse..]);
    }

    // ── 11. ★ 2026-10-08 Preview.2 返修（用户真机：提示卡抽搐 / 闪动 / 动画播不完整）★ ──────
    //  两条已证实根因（详见 PREVIEW-INFO / REPAIR-REPORT）：
    //   R1 旧代动画收尾没有归属校验 → 旧回调落在新动画的时间轴上摘掉新掩码，新动画在 40–60% 处被截断；
    //   R2 收缩先写终值再动画 → 掩码按"变矮前的高度"计算，动画终点只剩底部一条，收尾摘掩码后整卡弹回。
    //  本机无头环境量不了 WinUI 布局，只能把"结构不许回退"锁死（判据必须是可编译/可 grep 的源码结构）。
    [Fact]
    public void HintCard_CollapseHeightIsWrittenAtAnimationSettleNotBefore()
    {
        var card = StripComments(ReadRepoFile("src", "PCMig.WinUI", "Views", "ShellHintCard.xaml.cs"));
        var motion = StripComments(ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MotionDirector.cs"));

        // 写入点收敛成一个方法（参数名必须叫 target：第 6 条契约锁定"写入语句恰好一处 + 字面量不变"）。
        Assert.Contains("private void ApplySurfaceHeight(double target)", card);
        // 收缩方向**不立即写**，写入时机按 shrinking 分流。
        Assert.Contains("var shrinking = target < oldHeight;", card);
        Assert.Contains("if (!shrinking || !IsLoaded || HintCardSurface.XamlRoot is null)", card);
        // 收缩的写入交给动画收尾回调；回调里读"最新目标"，因此途中合并也不会写错。
        Assert.Contains("applyTargetHeight: () => ApplySurfaceHeight(_targetSurfaceHeight))", card);
        Assert.Contains("Action? applyTargetHeight = null", motion);
        Assert.Contains("applyTargetHeight?.Invoke();", motion);
    }

    [Fact]
    public void HintCard_StaleTransitionCallbackMustNotResetNewerAnimation()
    {
        var motion = StripComments(ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MotionDirector.cs"));

        Assert.Contains("private static int _hintCardGeneration;", motion);
        // 展开与缩回两个入口都必须"成为最新一代"。
        Assert.Equal(2, Regex.Matches(motion, @"var generation = \+\+_hintCardGeneration;").Count);
        // 收尾一律先校验代次。
        Assert.Contains("if (generation != _hintCardGeneration) return;", motion);
        // 兜底定时器同样受代次约束（旧代兜底绝不能写终态高度 / 摘掩码）。
        Assert.Contains("() => generation == _hintCardGeneration", motion);
        // 旧实现（无条件 Reset）不许回来。
        Assert.DoesNotContain("batch.Completed += (_, _) => ResetHintCardVisual", motion);
    }

    [Fact]
    public void HintCard_SettleAppliesHeightBeforeDroppingTheMask()
    {
        var motion = StripComments(ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MotionDirector.cs"));

        var collapse = motion.IndexOf("public static void PlayHintCardCollapse", StringComparison.Ordinal);
        Assert.True(collapse > 0, "PlayHintCardCollapse 必须存在");
        var body = motion[collapse..];

        var guard = body.IndexOf("if (generation != _hintCardGeneration) return;", StringComparison.Ordinal);
        var apply = body.IndexOf("applyTargetHeight?.Invoke();", StringComparison.Ordinal);
        var reset = body.IndexOf("ResetHintCardVisual(element, content);", StringComparison.Ordinal);
        Assert.True(guard >= 0 && apply > guard && reset > apply, "收尾顺序必须是：校验代次 → 写终态高 → 摘掩码归零");
    }

    [Fact]
    public void HintCard_ResetStopsOpacityToo_NotOnlyTranslation()
    {
        var motion = StripComments(ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MotionDirector.cs"));
        var reset = motion.IndexOf("public static void ResetHintCardVisual", StringComparison.Ordinal);
        Assert.True(reset > 0, "ResetHintCardVisual 必须存在");
        var resetBody = motion[reset..];
        Assert.Contains("contentVisual.StopAnimation(\"Translation\");", resetBody);
        // R1 的对称性要求：动画途中被打断时不许留下"半透明正文"（旧实现只停 Translation）。
        Assert.Contains("contentVisual.StopAnimation(\"Opacity\");", resetBody);
    }

    [Fact]
    public void HintCard_InFlightCollapseMergesInsteadOfReplaying()
    {
        var card = StripComments(ReadRepoFile("src", "PCMig.WinUI", "Views", "ShellHintCard.xaml.cs"));

        Assert.Contains("_surfaceTransitionActive = true;", card);
        Assert.Contains("if (_surfaceTransitionActive && target < oldHeight)", card);
        Assert.Contains("_surfaceTransitionActive = false;", card);
        // 判据基准必须与动画基数同源：否则同一布局回合的两次请求会以过期基数重播一遍完整动画。
        Assert.Contains("_targetSurfaceHeight > 0d ? _targetSurfaceHeight : actual", card);
        Assert.Contains("var newHeight = _targetSurfaceHeight;", card);
        Assert.DoesNotContain("var newHeight = HintCardSurface.Height;", card);
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