using System;
using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace PCMig.WinUI.Presentation;

/// <summary>
/// 统一 Motion 消费层（Final Polish §24–§33 / §56）。
///
/// 设计约束（全部来自用户指令原文）：
///   • §25/§56：时长与位移必须从 <c>Themes\Motion.xaml</c> 的集中 Token 取，调用点不写死数值；
///   • §26：页面入场是**方向性**的 —— 下一步自右进入、上一步自左进入，位移仅 8–12 DIP；
///   • §28/§29：Panel 关闭必须是「反向动画」而不是 Hard Cut；
///   • §32：尊重系统动画偏好 —— 关闭动画时不做 Slide/Scale，直接落到终态；
///   • §33：动画**绝不作用于 Window Root 或 Backdrop**，只作用于 Page Content / Surface / Panel；
///   • §31：结束后位移必须归零、Opacity 不得停在 0.99、收尾只发生一次。
///
/// **为什么位移用声明式 ThemeTransition、只有透明度用 Storyboard** —— 这是本机实测逼出来的结论，
/// 不是风格选择（两种路径都在运行期被单独验证过）：
///   · Storyboard 目标是 `Opacity`  → **Begin 成功**；
///   · Storyboard 目标是 `TranslateX` / `(UIElement.Translation.X)` → **Begin 抛 COMException
///     "Cannot resolve TargetProperty"**（`Translation` 是 Vector3，不是可动画 DIP；`TranslateX`
///     作为裸路径也解析不了，它属于 RenderTransform 而非元素本身的属性）。
/// 而 `EntranceThemeTransition` 同时提供淡入与方向性位移、跟随系统动画偏好、且不会闪帧（框架在渲染
/// 提交前生效）。因此：**位移交给声明式转场，透明度交给 Storyboard**，各用其能解析的那条路径。
///
/// 只属于 UI 层：不读不写任何业务状态，不碰 ViewModel / Command / 绑定 / 存档 / 导航语义。
/// 与 <see cref="MotionState"/> 的分工：MotionState 判定"本次是否允许重放动画"（防重入），
/// 本类负责"怎么播放"。被判定为不可重放时，本类只做**终态吸附**，保证视觉结果永远正确。
/// </summary>
internal static class MotionDirector
{
    private const string DurationFastKey = "PCMigMotionDurationFast";
    private const string DurationNormalKey = "PCMigMotionDurationNormal";
    private const string EaseStandardKey = "PCMigMotionEaseStandard";
    private const string PageEnterDistanceKey = "PCMigMotionPageEnterDistance";
    private const string PanelEnterDistanceKey = "PCMigMotionPanelEnterDistance";

    /// <summary>页面前进方向（下一步）：+1。**当前实现为整页纵向 Push（视口高度），不是「自右进入」**（横向位移已废弃）。</summary>
    public const int DirectionForward = 1;

    /// <summary>页面后退方向（上一步）：-1，与前进方向符号相反。**同样为纵向整页 Push，不是「自左进入」**。</summary>
    public const int DirectionBackward = -1;

    /// <summary>退场动画真正结束后的收尾回调（折叠可见性 / 归还焦点由调用方决定语义）。</summary>
    public delegate void MotionCompleted();

    private static bool _systemAnimationsChecked;
    private static bool _systemAnimationsEnabled = true;

    /// <summary>
    /// §32：系统是否允许播放动画。取不到该偏好时保守地**按允许**处理 ——
    /// 宁可播放一次克制的过渡，也不要因为 API 不可用而整体失去动效。
    /// </summary>
    public static bool SystemAnimationsEnabled
    {
        get
        {
            if (!_systemAnimationsChecked)
            {
                _systemAnimationsChecked = true;
                try
                {
                    _systemAnimationsEnabled = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Motion] UISettings unavailable, assuming animations on: {ex.Message}");
                    _systemAnimationsEnabled = true;
                }
            }
            return _systemAnimationsEnabled;
        }
    }

    // ── Token 读取（集中，调用点永不写死毫秒 / 距离） ──────────────────────────

    private static Duration GetDuration(string key, double fallbackSeconds)
    {
        if (Application.Current?.Resources is { } res && res.TryGetValue(key, out var value) && value is Duration d)
            return d;
        return new Duration(TimeSpan.FromSeconds(fallbackSeconds));
    }

    private static double GetDouble(string key, double fallback)
    {
        if (Application.Current?.Resources is { } res && res.TryGetValue(key, out var value) && value is double d)
            return d;
        return fallback;
    }

    /// <summary>§56：缓动以语义名集中在 Motion.xaml；这里只做语义名到缓动函数的映射。</summary>
    private static EasingFunctionBase GetEasing(string key, bool isExit)
    {
        var name = Application.Current?.Resources is { } res && res.TryGetValue(key, out var value)
            ? value as string
            : null;

        // 入场：减速（快速起步、柔和收尾）。退场：加速（干净离开，不拖沓）。
        return name switch
        {
            "exit" => new CubicEase { EasingMode = EasingMode.EaseIn },
            _ => new CubicEase { EasingMode = isExit ? EasingMode.EaseIn : EasingMode.EaseOut },
        };
    }

    // ── 页面入场（方向性，§26） ───────────────────────────────────────────────

    /// <summary>
    /// 页面入场。调用时机：**可见性切换之前**（Changing）。
    /// 方向性由 <paramref name="direction"/> 决定：前进自右进入、后退自左进入。
    /// </summary>
    /// <param name="allowAnimation">MotionState 的判定结果；false 时只吸附终态、不挂转场。</param>
    public static void PreparePageEntrance(FrameworkElement element, int direction, bool allowAnimation)
    {
        if (element is null) return;

        if (!allowAnimation || !SystemAnimationsEnabled)
        {
            // 不允许重放时摘掉转场并吸附终态 —— 页面照常切换，只是没有那一次动画。
            element.Transitions.Clear();
            SnapToRest(element);
            return;
        }

        // §26：Token 位移 10 DIP，方向由导航方向决定（前进为正 → 自右进入）。
        // EntranceThemeTransition 自带淡入（WinUI 3 没有 OpacityThemeTransition 这个类型）。
        var distance = GetDouble(PageEnterDistanceKey, 10.0) * (direction >= 0 ? 1 : -1);
        element.Transitions.Clear();
        element.Transitions.Add(new EntranceThemeTransition { FromHorizontalOffset = distance });
        SnapToRest(element);
    }

    // ── 面板入场 / 退场（§28 / §29） ──────────────────────────────────────────

    /// <summary>面板打开：Fade + 右侧轻微 Slide（§28：12–20 DIP）。必须在变可见之前调用。</summary>
    public static void PreparePanelEntrance(FrameworkElement element, bool allowAnimation)
    {
        if (element is null) return;

        if (!allowAnimation || !SystemAnimationsEnabled)
        {
            element.Transitions.Clear();
            SnapToRest(element);
            return;
        }

        var distance = GetDouble(PanelEnterDistanceKey, 16.0);
        element.Transitions.Clear();
        element.Transitions.Add(new EntranceThemeTransition { FromHorizontalOffset = distance });
        SnapToRest(element);
    }

    /// <summary>
    /// §28/§29：面板关闭 —— 反向淡出 + 向右滑出，**动画结束后**才收尾。
    /// 关闭绝不是 Hard Cut；若系统关闭动画或元素本来就不可见，则立刻收尾，不播中间态。
    /// 只用 Storyboard 的 Opacity 通道（该通道已实测可解析）；位移由声明式 Exit 转场承担。
    /// </summary>
    /// <param name="completed">动画真正结束时调用一次；调用方在此折叠面板 / 归还焦点。</param>
    public static void PlayPanelExit(FrameworkElement element, MotionCompleted? completed)
    {
        if (element is null)
        {
            completed?.Invoke();
            return;
        }

        if (!SystemAnimationsEnabled || element.Visibility != Visibility.Visible)
        {
            SnapToRest(element);
            completed?.Invoke();
            return;
        }

        var storyboard = new Storyboard();
        var fade = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = GetDuration(DurationFastKey, 0.14),
            EasingFunction = GetEasing("PCMigMotionEaseExit", isExit: true),
            EnableDependentAnimation = false,
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);

        var fired = false;
        storyboard.Completed += (_, _) =>
        {
            // §31：收尾只允许发生一次，避免重复折叠可见性 / 重复操作焦点。
            if (fired) return;
            fired = true;
            SnapToRest(element);
            completed?.Invoke();
        };

        try
        {
            storyboard.Begin();
        }
        catch (Exception ex)
        {
            // 动效失败绝不能让面板卡在半透明：直接吸附终态并立刻收尾。
            Debug.WriteLine($"[Motion] panel exit failed: {ex.Message}");
            SnapToRest(element);
            if (!fired)
            {
                fired = true;
                completed?.Invoke();
            }
        }
    }

    // ── 终态吸附（§31） ───────────────────────────────────────────────────────

    /// <summary>
    /// 把元素吸附到静止终态 —— 位移归零、Opacity 恰为 1。可安全重复调用。
    /// 注意：不缓存 RenderTransform；位移由声明式转场负责，这里只保证残留状态被清掉。
    /// </summary>
    public static void SnapToRest(FrameworkElement? element)
    {
        if (element is null) return;
        if (element.RenderTransform is CompositeTransform t)
        {
            t.TranslateX = 0;
            t.TranslateY = 0;
        }
        element.Opacity = 1.0;
    }

    // ── 提示卡逐行入场（UI Closure 2026-10-05 返修；**post-layout Translation**） ──────
    //
    // 为什么单独抽这一条：ShellHintCard 旧实现直接写 `Visual.Offset`，而 Offset 是
    // "Visual 相对父 Visual 的位置属性"——对一个已被 StackPanel 排好位置的 TextBlock 强行赋值，
    // 会覆盖掉 XAML 布局算出的 Y 位置，多个文本行于是全部塌向父容器原点（真机表现为
    // "文字全堆到卡片顶部、互相重叠、内容越多越乱"）。
    //
    // 正确通道是 **Translation**：先用 ElementCompositionPreview.SetIsTranslationEnabled 打开，
    // 再动画 Translation——它是 render-time 的 post-layout 附加位移，XAML 布局位置**不受影响**。
    // （本类已有的整页 Push / Utility Panel 走的就是这条通道，此处复用同一语义，不再另发明。）

    private const string HintLineDurationKey = "PCMigMotionHintLineDuration";
    private const string HintLineOffsetKey = "PCMigMotionHintLineOffset";

    /// <summary>
    /// 单行文本入场：Translation Y 由 <c>+offset → 0</c>、Opacity <c>0 → 1</c>（时长/位移取自 Motion.xaml Token）。
    /// 只作用于传入的那一个元素；重复调用会**接管**（先 StopAnimation）而不是叠加多份动画。
    /// 系统关闭动画时不做任何位移动画，直接吸附终态（Translation=0 / Opacity=1）。
    /// </summary>
    public static void PlayLineEntrance(FrameworkElement? element)
    {
        if (element is null) return;

        if (!SystemAnimationsEnabled)
        {
            ResetLineEntrance(element);
            return;
        }

        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;

            var span = GetDuration(HintLineDurationKey, 0.17).TimeSpan;
            if (span <= TimeSpan.Zero) span = TimeSpan.FromMilliseconds(170);
            var offset = (float)GetDouble(HintLineOffsetKey, 6.0);

            // 接管旧动画：同一元素上绝不允许多个入场动画并行（否则会出现半途重启的跳变）。
            visual.StopAnimation("Translation");
            visual.StopAnimation("Opacity");

            var easing = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.10f, 0.90f), new Vector2(0.20f, 1.00f));

            var slide = compositor.CreateVector3KeyFrameAnimation();
            slide.InsertKeyFrame(0f, new Vector3(0f, offset, 0f));
            slide.InsertKeyFrame(1f, Vector3.Zero, easing);
            slide.Duration = span;

            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f, easing);
            fade.Duration = span;

            visual.StartAnimation("Translation", slide);
            visual.StartAnimation("Opacity", fade);
        }
        catch (Exception ex)
        {
            // 入场动效是纯装饰（PMML-R8）：失败也只能落到可见终态，绝不能留在半透明或半位移上。
            Debug.WriteLine($"[Motion] hint line entrance failed: {ex.Message}");
            ResetLineEntrance(element);
        }
    }

    /// <summary>把单行文本吸附到静止终态：Translation 归零、Opacity 恰为 1。可安全重复调用。</summary>
    public static void ResetLineEntrance(FrameworkElement? element)
    {
        if (element is null) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation("Translation");
            visual.StopAnimation("Opacity");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            visual.Opacity = 1f;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Motion] hint line reset skipped: {ex.Message}");
        }
        element.Opacity = 1.0;
    }

    // ── 提示卡自适应展开 / 缩回（★ 2026-10-08 真机问题 3 ★）──────────────────────────
    //
    // 口径（用户 2026-10-08 指令）：高度"一次到位"，**绝不逐帧改 Layout Height**。
    //   做法：调用方（ShellHintCard）先算好目标高度并把 Border.Height **一次性**设到终值，
    //         随后调用本组方法——它们只动 Composition 通道（Clip 揭示 + Opacity + Translation），
    //         在合成线程上跑，不触发 Measure/Arrange，因此不存在"每帧重排 / 掉帧地一点点长高"。
    //   方向：揭示遮罩**固定露出卡片底部**，高度越大被遮住的顶部区域越小
    //         ⇒ 视觉上就是"底边不动、顶边向上推"（Q3）。
    //   禁止：任何弹簧 / 过冲（缓动用无回弹的减速曲线）；任何 ScaleY 文字拉伸（本组方法不碰 Scale）。
    //
    // 为什么 Clip 要试三种写法：本机 WinUI 3 版本对"元素视觉上挂 Clip"的支持路径不止一条
    // （`Visual.Clip` / `Visual.Properties["Clip"]` / 旧的 `InsetClip` 属性）。三条都失败时
    // 只保留 Opacity + Translation —— **纯装饰的降级**，卡片照样在正确高度上正常显示。

    private const string HintRevealDurationKey = "PCMigMotionHintRevealDuration";
    private const string HintCollapseDurationKey = "PCMigMotionHintCollapseDuration";
    private const string HintRevealOffsetKey = "PCMigMotionHintRevealOffset";

    /// <summary>揭示遮罩的"不裁底"下缘常量（远大于任何可能的卡片高度）：遮罩只靠顶边下移生效。</summary>
    private const float ClipBottomOpen = 100000f;

    /// <summary>
    /// ★ 2026-10-08 二次返修（用户 m01438 §四）★ QA 对照开关：环境变量 <c>PCMIG_HINTCARD_ANIMATION</c>。
    ///   · 未设置 / <c>"1"</c> ⇒ 正式动画（InsetClip 纵向揭示 + 正文 Opacity/Translation）；
    ///   · <c>"0"</c> ⇒ 本卡完全不参与视觉过渡：不挂 Clip、不做 reveal / translation / opacity，
    ///           布局直接落到终态（<see cref="ResetHintCardVisual"/> 保证 Clip=null、Translation=0、Opacity=1）。
    /// 用途：用同一个 QA exe 分别以两个模式启动，肉眼对照"左侧裁切是否由动画层引入"。
    /// 只读一次并缓存（进程生存期不变），避免每次展开都查环境变量。
    /// </summary>
    private static readonly bool HintCardAnimationEnabled = ResolveHintCardAnimationEnabled();

    private static bool ResolveHintCardAnimationEnabled()
    {
        try
        {
            var raw = Environment.GetEnvironmentVariable("PCMIG_HINTCARD_ANIMATION");
            if (string.IsNullOrWhiteSpace(raw)) return true;
            return !string.Equals(raw.Trim(), "0", StringComparison.Ordinal);
        }
        catch
        {
            // 读环境变量失败绝不能让卡片失去视觉终态：按"正式动画"处理（终态由 Reset 保证）。
            return true;
        }
    }

    /// <summary>
    /// 提示卡高度过渡的**代次**（U58 同一缺陷类的提示卡分支）：只有"最新一次"调用的收尾回调才有权
    /// 摘掩码 / 写终态高度，旧代回调一律作废 —— 否则内容在动画途中再变一次时，旧回调会落在新动画的
    /// 时间轴上把新动画截断（表现为抽搐、闪动、"动画播不完整"）。
    /// 同类既有方案见 <c>PageTransitionCoordinator</c> 的 <c>_generation</c>（本文件 :747-751 已留档 U58）。
    /// </summary>
    private static int _hintCardGeneration;

    /// <summary>
    /// 提示卡**展开揭示**：调用方已经把 Border 高度一次性设到 <paramref name="targetHeight"/>，
    /// 这里在合成层把"多出来的顶部区域"平滑揭示出来（Clip 遮罩自下而上让位），
    /// 同时正文极轻微淡入 + 上浮。时长 = <c>PCMigMotionHintRevealDuration</c>。
    /// </summary>
    /// <param name="element">卡片外层 Border。</param>
    /// <param name="previousHeight">展开前的卡片高度（DIP）——决定遮罩起点，保证从"当前视觉"接续。</param>
    /// <param name="targetHeight">已生效的新高度（DIP）。</param>
    /// <param name="content">正文容器（做 Opacity / Translation 的极轻微入场；可为 null）。</param>
    public static void PlayHintCardReveal(FrameworkElement? element, double previousHeight, double targetHeight, FrameworkElement? content)
    {
        if (element is null) return;

        // 本拍成为"最新一代"：此前排队的所有收尾回调全部作废（旧回调会摘掉本拍新动画的掩码）。
        var generation = ++_hintCardGeneration;

        // ★ QA 对照开关（PCMIG_HINTCARD_ANIMATION=0）★：不挂 Clip、不做 reveal/translation/opacity，
        //   高度已经在调用方一次性写好，这里只需把视觉终态钉死（Clip=null / Translation=0 / Opacity=1）。
        if (!HintCardAnimationEnabled)
        {
            ResetHintCardVisual(element, content);
            return;
        }

        // Reduced Motion（§32）：不做揭示位移，直接落到终态（高度已经是目标高）。
        if (!SystemAnimationsEnabled)
        {
            ResetHintCardVisual(element, content);
            return;
        }

        var span = GetDuration(HintRevealDurationKey, 0.26).TimeSpan;
        if (span <= TimeSpan.Zero) span = TimeSpan.FromMilliseconds(260);
        var offset = (float)GetDouble(HintRevealOffsetKey, 8.0);

        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;

            // 遮罩揭示：起点 = 多出来的那部分先被遮住（H = 新高度 − 旧高度），终点 = 全部露出。
            // 卡片背景/边框全程保持不透明（只揭示、不整体淡入 ⇒ 不会"闪一下"）。
            var from = (float)Math.Max(0d, targetHeight - previousHeight);
            var clipApplied = ApplyHintRevealClip(visual, compositor, from, 0f, span);

            var easing = CreateDecelerateEasing(compositor);
            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

            if (content is not null)
            {
                // 正文极轻微淡入 + 上浮；**不做 Scale**（绝不把文字拉变形）。
                // 有遮罩时可以从 0 起（多出来的正文本来就被遮着）；无遮罩（Clip 不受支持）时只做极轻微淡入，
                // 否则整块正文"从无到有"会闪一下。
                ElementCompositionPreview.SetIsTranslationEnabled(content, true);
                var contentVisual = ElementCompositionPreview.GetElementVisual(content);

                var fade = compositor.CreateScalarKeyFrameAnimation();
                fade.InsertKeyFrame(0f, clipApplied ? 0f : 0.72f);
                fade.InsertKeyFrame(1f, 1f, easing);
                fade.Duration = span;
                contentVisual.StopAnimation("Opacity");
                contentVisual.StartAnimation("Opacity", fade);

                var slide = compositor.CreateVector3KeyFrameAnimation();
                slide.InsertKeyFrame(0f, new Vector3(0f, offset, 0f));
                slide.InsertKeyFrame(1f, Vector3.Zero, easing);
                slide.Duration = span;
                contentVisual.StopAnimation("Translation");
                contentVisual.StartAnimation("Translation", slide);
            }

            batch.Completed += (_, _) =>
            {
                if (generation != _hintCardGeneration) return;
                ResetHintCardVisual(element, content);
            };
            batch.End();
            ScheduleFallback(
                span + TimeSpan.FromMilliseconds(120),
                () =>
                {
                    if (generation != _hintCardGeneration) return;
                    ResetHintCardVisual(element, content);
                },
                () => generation == _hintCardGeneration);
        }
        catch (Exception ex)
        {
            // 纯装饰失败也只能落到"完全可见"的终态（高度已正确）。
            Debug.WriteLine($"[Motion] hint reveal failed: {ex.Message}");
            ResetHintCardVisual(element, content);
        }
    }

    /// <summary>
    /// 提示卡**缩回揭示**：调用方已经把高度一次性设回默认高，这里在合成层把"要收掉的顶部区域"
    /// 平滑遮回去 + 正文轻微淡出。时长 = <c>PCMigMotionHintCollapseDuration</c>（比展开更干脆）。
    /// </summary>
    /// <param name="element">卡片外层 Border。</param>
    /// <param name="previousHeight">收缩前的卡片高度（DIP）。</param>
    /// <param name="targetHeight">已生效的新高度（DIP，通常 = 默认 176）。</param>
    /// <param name="content">正文容器（可为 null）。</param>
    public static void PlayHintCardCollapse(FrameworkElement? element, double previousHeight, double targetHeight, FrameworkElement? content, Action? applyTargetHeight = null)
    {
        if (element is null) return;

        // 本拍成为"最新一代"：旧代收尾回调作废（见 _hintCardGeneration 说明）。
        var generation = ++_hintCardGeneration;

        // ★ 收尾语义（本次返修的核心）★：收缩时布局高度**只在动画收尾写一次**（applyTargetHeight）。
        //   理由：遮罩只能"遮住已有的像素"，无法重建已经消失的像素。若先写矮（旧实现 :222），
        //   遮罩仍按"变矮前的高度"计算 ⇒ 动画终点只剩底部一小条可见，收尾摘掉掩码后整卡弹回 = 闪动。
        //   增长方向相反（卡片已经变高，遮罩能掩盖），所以只有收缩走这条延迟写入路径。
        void Settle()
        {
            if (generation != _hintCardGeneration) return;
            applyTargetHeight?.Invoke();
            ResetHintCardVisual(element, content);
        }

        // ★ QA 对照开关（PCMIG_HINTCARD_ANIMATION=0）★：同 PlayHintCardReveal，直接钉死视觉终态。
        if (!HintCardAnimationEnabled)
        {
            Settle();
            return;
        }

        if (!SystemAnimationsEnabled)
        {
            Settle();
            return;
        }

        var span = GetDuration(HintCollapseDurationKey, 0.22).TimeSpan;
        if (span <= TimeSpan.Zero) span = TimeSpan.FromMilliseconds(220);

        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;

            // 遮罩收回：起点 = 全部露出，终点 = 只保留新高度（要收掉的那段自上而下遮回去）。
            var to = (float)Math.Max(0d, previousHeight - targetHeight);
            ApplyHintRevealClip(visual, compositor, 0f, to, span);
            // 缩回时遮罩是"从无到有"遮上去：即使 Clip 不受支持也不会闪（正文本来就在淡出），
            // 因此这里不需要 clipApplied 分支。

            var easing = CreateDecelerateEasing(compositor);
            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

            if (content is not null)
            {
                // 只让正文轻微淡出，卡片背景/边框不参与透明度（避免整卡"闪一下"）。
                var contentVisual = ElementCompositionPreview.GetElementVisual(content);
                var fade = compositor.CreateScalarKeyFrameAnimation();
                fade.InsertKeyFrame(0f, 1f);
                fade.InsertKeyFrame(1f, 0.6f, easing);
                fade.Duration = span;
                contentVisual.StopAnimation("Opacity");
                contentVisual.StartAnimation("Opacity", fade);
            }

            batch.Completed += (_, _) => Settle();
            batch.End();
            ScheduleFallback(span + TimeSpan.FromMilliseconds(120), () => Settle(), () => generation == _hintCardGeneration);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Motion] hint collapse failed: {ex.Message}");
            Settle();
        }
    }

    /// <summary>
    /// 提示卡视觉归零：摘掉提示卡的 Clip 遮罩、清零正文 Translation、Opacity 恰为 1。
    /// **不碰 Scale / CenterPoint**（本卡从不做缩放，避免任何文字形变）。可安全重复调用。
    /// </summary>
    public static void ResetHintCardVisual(FrameworkElement? element, FrameworkElement? content)
    {
        if (element is not null)
        {
            try
            {
                var visual = ElementCompositionPreview.GetElementVisual(element);
                // ★ 2026-10-08 二次返修（用户 m01438：提示卡左侧整体裁切）★
                //   旧实现只把遮罩"偏移写回 0"（RectangleClip.Offset / InsetClip.TopInset），
                //   这不等于**摘除** Clip：任何没被这两个 if 覆盖到的残留（例如 InsetClip 的
                //   LeftInset 被写错、或类型判断漏项）都会永久挂在 HintCardSurface 上把正文裁掉。
                //   ⇒ 终态必须 `visual.Clip = null`：这是唯一能保证"卡片绝不被任何 CompositionClip 裁切"的做法。
                //   设 null 会连同该 Clip 上正在跑的动画一起失效，因此无需再逐个 StopAnimation。
                visual.Clip = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Motion] hint visual reset skipped: {ex.Message}");
            }
        }

        if (content is not null)
        {
            try
            {
                var contentVisual = ElementCompositionPreview.GetElementVisual(content);
                contentVisual.StopAnimation("Translation");
                // 对称性（本次返修）：旧实现只停 Translation，不停 Opacity —— 动画途中被打断时
                // 会留下"半透明的正文"（下次显示从中间态开始 = 闪）。Opacity 也必须显式停车再写终值。
                contentVisual.StopAnimation("Opacity");
                contentVisual.Properties.InsertVector3("Translation", Vector3.Zero);
                contentVisual.Opacity = 1f;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Motion] hint content reset skipped: {ex.Message}");
            }
            content.Opacity = 1.0;
        }
    }

    /// <summary>
    /// 给元素视觉挂"只露出底部一条带"的揭示遮罩（揭示/缩回共用）：遮罩下缘取 <see cref="ClipBottomOpen"/>
    /// （远大于卡片高，等于"不裁底"），只靠**顶边下移**生效——偏移量 = 被遮住的顶部高度。
    /// 数学：遮罩覆盖卡片顶端向下 <c>H</c>，只剩下方全部可见；卡片底边固定、顶端上移，
    /// 因此任意时刻露出的正是"卡片当前顶边 + H 以下"的内容——视觉表现为"底边不动、顶边向上推"，
    /// 且整段展开**不需要逐帧布局**。
    ///
    /// ★ 为什么首选 InsetClip（而不是 RectangleClip）★
    ///   <c>InsetClip.TopInset</c> 的"被动画的值"**就是** clip 自身的状态：动画结束后它停在终值，
    ///   不存在"动画值和 clip 状态不一致 ⇒ 收尾瞬间跳一下"的闪烁。
    ///   RectangleClip 的偏移是 <c>Clip.Offset</c>，而"给属性设初值"这条路径在不同 WinUI 版本上并不一致，
    ///   万一没设进去，动画结束时会从"新高度"突跳到"全露出"——正是任务书禁止的"闪一下再重新布局"。
    ///   因此把 InsetClip 放首选，RectangleClip 只作兜底。
    /// </summary>
    /// <returns>是否成功挂上遮罩。false 时调用方应改用纯 Opacity 揭示（纯装饰降级）。</returns>
    private static bool ApplyHintRevealClip(Visual visual, Compositor compositor, float fromOffset, float toOffset, TimeSpan span)
    {
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1.0f), new Vector2(0.30f, 1.0f));

        var slide = compositor.CreateScalarKeyFrameAnimation();
        slide.InsertKeyFrame(0f, fromOffset);
        slide.InsertKeyFrame(1f, toOffset, easing);
        slide.Duration = span;

        // ★ 唯一路径：Visual.Clip = InsetClip，动画 TopInset（遮住顶部 = 上内缩）★
        //
        // ★★ 2026-10-08 二次返修（用户 m01438）根因留档 ★★
        //   `Compositor.CreateInsetClip` 的真实参数顺序是 **(leftInset, topInset, rightInset, bottomInset)**
        //   （见 MS Learn「Compositor.CreateInsetClip Method」：`CreateInsetClip(Single leftInset, Single topInset,
        //   Single rightInset, Single bottomInset)`，leftInset = "Inset from the left of the visual"）。
        //   上一轮按 (top, left, bottom, right) 写成 `CreateInsetClip(fromOffset, 0f, 0f, 0f)`，
        //   于是"要遮住的顶部高度"被灌进了 **leftInset** ⇒ 整个提示卡 Visual 左侧被横向裁掉；
        //   而动画挂在 TopInset 上（初值/终值都是 0）毫无效果，Reset 又只清 TopInset
        //   ⇒ 左侧裁切永久残留（标题 / 正文 / 状态行 / 署名全部被切）。
        //   现在改为**具名实参**，杜绝再按位置传参传错；横向一律 0（只允许纵向揭示）。
        try
        {
            var inset = compositor.CreateInsetClip(
                leftInset: 0f,
                topInset: fromOffset,
                rightInset: 0f,
                bottomInset: 0f);
            visual.Clip = inset;
            inset.StartAnimation("TopInset", slide);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Motion] InsetClip reveal unavailable: {ex.Message}");
        }

        // ★ 已按用户 m01438 指令删除 RectangleClip 兜底路径 ★
        //   理由：① 需求只是"纵向展开"，InsetClip.TopInset 足够表达；
        //        ② 兜底路径自身也是横向裁切源（`Left = 0` 且 `Right = 0` ⇒ 裁剪矩形宽度为 0）；
        //        ③ 动画是装饰 —— 宁可少一个 reveal 效果，也绝不留任何可能裁切正文的 Clip。
        //   ⇒ 降级为纯 Opacity + Translation.Y（卡片完整可见，仅少一点揭示质感），且不挂任何 Clip。
        Debug.WriteLine("[Motion] hint reveal clip unsupported on this build; opacity-only reveal (no clip attached)");
        return false;
    }

    // ── 源连接指示器呼吸（★ 2026-10-08 真机问题 4 ★） ─────────────────────────────
    //
    // 为什么走 Composition：状态灯"正在连接"的表达需要**柔和呼吸**（Opacity min ↔ 1.0 循环）。
    // 若用 DispatcherTimer 逐帧改颜色，观感是生硬的开/关闪烁，还把 UI 线程拖进每帧写控件；
    // Composition 的 Opacity 动画在合成线程上跑，与 XAML 布局无关（不触发 Measure/Arrange）。

    private const string SourceBreathDurationKey = "PCMigMotionSourceBreathDuration";
    private const string SourceBreathMinOpacityKey = "PCMigMotionSourceBreathMinOpacity";

    /// <summary>
    /// 源连接指示器"正在连接"的琥珀色柔和呼吸：Opacity <c>min → 1 → min</c> 无限循环
    /// （周期取自 <c>PCMigMotionSourceBreathDuration</c>，默认 1.05 s）。
    /// 重复调用**接管**旧动画而不是叠加；系统关闭动画时直接吸附到常亮终态（Opacity = 1）。
    /// </summary>
    public static void PlaySourceBreathing(FrameworkElement? element)
    {
        if (element is null) return;

        if (!SystemAnimationsEnabled)
        {
            ResetSourceBreathing(element);
            return;
        }

        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;

            var span = GetDuration(SourceBreathDurationKey, 1.05).TimeSpan;
            if (span <= TimeSpan.Zero) span = TimeSpan.FromMilliseconds(1050);
            var min = (float)Math.Clamp(GetDouble(SourceBreathMinOpacityKey, 0.35), 0.05, 1.0);

            // 接管旧动画：同一元素上绝不并行两条呼吸（否则相位不同步会出现"抖一下"）。
            visual.StopAnimation("Opacity");

            var easing = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.40f, 0.00f), new Vector2(0.60f, 1.00f));

            var breath = compositor.CreateScalarKeyFrameAnimation();
            breath.InsertKeyFrame(0f, min);
            breath.InsertKeyFrame(0.5f, 1f, easing);
            breath.InsertKeyFrame(1f, min, easing);
            breath.Duration = span;
            breath.IterationBehavior = AnimationIterationBehavior.Forever;

            visual.StartAnimation("Opacity", breath);
        }
        catch (Exception ex)
        {
            // 呼吸是纯装饰：失败也只能落到可见终态（常亮），绝不留在半透明上。
            Debug.WriteLine($"[Motion] source breathing failed: {ex.Message}");
            ResetSourceBreathing(element);
        }
    }

    /// <summary>停止呼吸并吸附到常亮终态（Opacity 恰为 1）。可安全重复调用。</summary>
    public static void ResetSourceBreathing(FrameworkElement? element)
    {
        if (element is null) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation("Opacity");
            visual.Opacity = 1f;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Motion] source breathing reset skipped: {ex.Message}");
        }
        element.Opacity = 1.0;
    }

    // ── 页面过渡：四页 Vertical Cross-Slide（§26；Composition 路线） ────────────────

    private const string PagePushDurationKey = "PCMigMotionPagePushDuration";
    private const string PageSlideDistanceKey = "PCMigMotionPageSlideDistance";

    /// <summary>
    /// 四页 Vertical Cross-Slide：**旧页轻微上移 + FadeOut，新页自下方进入 + FadeIn**；
    /// Backward 时方向整体反过来。<paramref name="direction"/> ≥ 0 视为 Forward。
    /// 跨级导航（例如 1→4）只播这一遍，不分段。
    ///
    /// **为什么必须走 Composition**：Storyboard 无法解析 `Translation` / `TranslateX`
    /// （本机实测抛 "Cannot resolve TargetProperty"），而 Composition 的 Translation 通道
    /// （由 <c>ElementCompositionPreview.SetIsTranslationEnabled</c> 启用）可以正常动画。
    ///
    /// 收尾（§31）由本方法保证且**只发生一次**：停动画 + 位移归零 + 调 <paramref name="completed"/>；
    /// 另外挂一个略长的定时兜底 —— 即使 Composition 的 Completed 因元素被移出视觉树等原因未触发，
    /// 旧页也一定会被折叠，**不会留下两页同时可见**。
    ///
    /// 只动视觉：不碰绑定、不重建页面、不重建 ViewModel。
    /// </summary>
    public static void PlayPagePush(
        FrameworkElement? outgoing,
        FrameworkElement? incoming,
        int direction,
        double viewportHeight,
        MotionCompleted? completed)
    {
        var forward = direction >= 0;
        // U57：位移 = **整个页面视口高度** —— 旧页整页滑出、新页整页滑入，动画中间两页各占一半，
        // 中间那道分界线清晰可见。（旧的小位移 Cross-Slide 口径已废弃。）
        var distance = viewportHeight > 1.0 ? viewportHeight : GetDouble(PageSlideDistanceKey, 26.0);
        var duration = GetPushDuration();

        if (incoming is null && outgoing is null)
        {
            completed?.Invoke();
            return;
        }

        if (!SystemAnimationsEnabled || outgoing is null || ReferenceEquals(outgoing, incoming))
        {
            ResetTransitionState(outgoing);
            ResetTransitionState(incoming);
            completed?.Invoke();
            return;
        }

        // Forward：新页自下方(+Y)进入、旧页向上(-Y)退出；Backward 相反。
        var incomingFrom = forward ? distance : -distance;
        var outgoingTo = forward ? -distance : distance;

        var fired = false;
        void Finish()
        {
            if (fired) return;      // §31：收尾只允许一次
            fired = true;
            // U58 修复：**这里绝不能再无条件 Reset 这两个元素**。
            // 快速连点时，新一代 Push 已经接管了这两个元素的 Translation 动画；旧一代的收尾
            // （ScopedBatch.Completed 或定时兜底）若在此把 Translation 归零 + StopAnimation，
            // 就会当场打断新动画 ⇒ 页面停在视口外或停在半途，表现为"两页重叠 / 一片空白"。
            // 收尾的归属判断（哪一代还有权动这些元素）交给调用方按代次决定，见 PageTransitionCoordinator。
            completed?.Invoke();
        }

        try
        {
            // Composition 的 KeyFrameAnimation **没有** Completed 事件；成组收尾要用 ScopedBatch
            // （两个动画都进入 batch，再在 Completed 里收尾）。另有定时兜底保证一定会收尾。
            var compositor = ElementCompositionPreview.GetElementVisual(outgoing).Compositor;
            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

            // 用户在 U57 明确要求：**不要依赖 Fade 作为主要过渡手段**，主体必须是整页位移，
            // 因此这里 Opacity 全程恒为 1（不做淡入淡出），只有 Translation 在动。
            // U59：**接管式过渡**。每一段 Push 都从页面"当前实际位置"接续，而不是从固定起点重来 ——
            // 上一段还在途中的页面会自然继续往上/往下走，所以快速连点时动画**永远在跑、不会消失**，
            // 也不会因为跳回固定起点而出现错位/跳变。
            var outgoingStart = ReadTranslationY(outgoing);
            var incomingCurrent = incoming is null ? 0.0 : ReadTranslationY(incoming);
            // 目标页若已经在半屏之外（上一段把它推出去的残留），就从它现在的位置进入，保持连续；
            // 若它是干净页（位移为 0），则按标准方向从视口外整页滑入。
            var incomingStart = Math.Abs(incomingCurrent) >= distance * 0.5 ? incomingCurrent : incomingFrom;

            if (incoming is not null) AnimateSlideFade(incoming, incomingStart, 0, 1, 1, duration);
            AnimateSlideFade(outgoing, outgoingStart, outgoingTo, 1, 1, duration);

            batch.Completed += (_, _) => Finish();
            batch.End();

            ScheduleFallback(duration.TimeSpan + TimeSpan.FromMilliseconds(140), Finish);
        }
        catch (Exception ex)
        {
            // 动效失败绝不能让过渡卡住：直接吸附终态并立刻收尾。
            Debug.WriteLine($"[Motion] page cross-slide failed: {ex.Message}");
            Finish();
        }
    }

    /// <summary>
    /// U57：整页 Push 的时长（420 ms 量级；ease-in-out，无弹簧 / 无回弹）。
    /// 环境变量 <c>PCMIG_MOTION_SLOWMO</c>（默认 1）只用于本机**取证录屏**时把时长放大，
    /// 便于逐帧看清整页推入过程；不设它时产品行为完全不受影响。
    /// </summary>
    private static Duration GetPushDuration()
    {
        var baseDuration = GetDuration(PagePushDurationKey, 0.42);
        var scale = 1.0;
        var raw = Environment.GetEnvironmentVariable("PCMIG_MOTION_SLOWMO");
        if (!string.IsNullOrWhiteSpace(raw) && double.TryParse(raw, out var parsed) && parsed > 0.1 && parsed < 60.0)
        {
            scale = parsed;
        }
        if (Math.Abs(scale - 1.0) < 0.001) return baseDuration;
        return new Duration(TimeSpan.FromMilliseconds(baseDuration.TimeSpan.TotalMilliseconds * scale));
    }

    /// <summary>按位移 + 透明度播放一段 Composition 动画（Translation 通道需先启用）。</summary>
    private static void AnimateSlideFade(
        FrameworkElement element,
        double fromY,
        double toY,
        double fromOpacity,
        double toOpacity,
        Duration duration)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;

        var span = duration.TimeSpan;
        if (span <= TimeSpan.Zero) span = TimeSpan.FromMilliseconds(200);

        // 与 Storyboard 路径一致的"快速起步、柔和收尾"。
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.10f, 0.90f), new Vector2(0.20f, 1.00f));

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0f, new Vector3(0f, (float)fromY, 0f));
        slide.InsertKeyFrame(1f, new Vector3(0f, (float)toY, 0f), easing);
        slide.Duration = span;

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, (float)fromOpacity);
        fade.InsertKeyFrame(1f, (float)toOpacity, easing);
        fade.Duration = span;

        visual.StartAnimation("Translation", slide);
        visual.StartAnimation("Opacity", fade);
    }

    /// <summary>
    /// U59：读取元素**当前实际的 Composition Translation.Y**（DIP）。接管式过渡的起点就取它，
    /// 这样新一段 Push 与前一段的可见位置严丝合缝，不会跳变、也不会把动画"重置掉"。
    /// 读不到时返回 0（等价于"从静止位置开始"，安全退化）。
    /// </summary>
    private static double ReadTranslationY(FrameworkElement element)
    {
        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            var visual = ElementCompositionPreview.GetElementVisual(element);
            if (visual.Properties.TryGetVector3("Translation", out var value) == CompositionGetValueStatus.Succeeded)
            {
                return value.Y;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Motion] read translation skipped: {ex.Message}");
        }
        return 0.0;
    }

    // ── Utility Panel：Origin-aware Reveal（从真实触发入口展开） ─────────────────────
    //
    // 口径（用户 U60）：Panel 打开时应该像"从刚刚点击的那个入口自然展开出来"，
    // 不是中央凭空出现 —— 关键在 **Scale 的 CenterPoint 必须对应触发入口**：
    //   · CenterPoint.Y 靠近 Panel 顶边（0）；
    //   · CenterPoint.X = 触发按钮中心 相对 Panel 左边缘的位移（anchorCenterX − panelLeft）。
    // 两个面板共用同一组 Token（Motion.xaml），不各写一套时长。
    //
    // 全程走 Composition（Opacity + Translation + Scale 并行），复用本类已有的
    // ScopedBatch 收尾与定时兜底；**绝不碰 Acrylic / Material / Brush**（那部分已冻结）。
    //
    // 安全原则（用户 U60 §7）：任何失败路径都必须把 Panel 吸附到**可见的终态** ——
    // 绝不能出现"Opacity=0 但 Visibility=Visible"这种"存在却看不见"的状态。

    private const string UtilityRevealDurationKey = "PCMigMotionUtilityRevealDuration";
    private const string UtilityDismissDurationKey = "PCMigMotionUtilityDismissDuration";
    private const string UtilityRevealOffsetKey = "PCMigMotionUtilityRevealOffset";
    private const string UtilityRevealScaleKey = "PCMigMotionUtilityRevealScale";
    private const string UtilityDismissScaleKey = "PCMigMotionUtilityDismissScale";
    private const string UtilityDismissOffsetKey = "PCMigMotionUtilityDismissOffset";

    /// <summary>
    /// Utility Panel 打开：从触发入口展开（Opacity 0→1、Scale 0.945→1、TranslationY −7→0）。
    /// </summary>
    /// <param name="centerPointX">Scale 原点的 X（Panel 局部坐标；= 触发按钮中心 − Panel 左缘）</param>
    /// <param name="centerPointY">Scale 原点的 Y（Panel 局部坐标；靠近 Panel 顶边 ⇒ 0）</param>
    public static void PlayUtilityPanelOpen(
        FrameworkElement? panel,
        double centerPointX,
        double centerPointY,
        MotionCompleted? completed)
    {
        if (panel is null)
        {
            completed?.Invoke();
            return;
        }

        var offset = GetDouble(UtilityRevealOffsetKey, 7.0);
        var startScale = GetDouble(UtilityRevealScaleKey, 0.945);
        var duration = GetDuration(UtilityRevealDurationKey, 0.26);

        if (!SystemAnimationsEnabled)
        {
            ResetUtilityPanelState(panel);
            completed?.Invoke();
            return;
        }

        var fired = false;
        void Finish()
        {
            if (fired) return;
            fired = true;
            ResetUtilityPanelState(panel);
            completed?.Invoke();
        }

        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(panel, true);
            var visual = ElementCompositionPreview.GetElementVisual(panel);
            var compositor = visual.Compositor;
            var span = duration.TimeSpan;
            if (span <= TimeSpan.Zero) span = TimeSpan.FromMilliseconds(260);

            // 起点：Scale 原点钉在触发入口处；同时把三个通道都设到起始值。
            visual.CenterPoint = new Vector3((float)centerPointX, (float)centerPointY, 0f);
            visual.Scale = new Vector3((float)startScale, (float)startScale, 1f);
            visual.Properties.InsertVector3("Translation", new Vector3(0f, -(float)offset, 0f));
            visual.Opacity = 0f;

            var easing = CreateDecelerateEasing(compositor);
            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            StartPanelAnimation(visual, compositor, span, easing,
                startScale, 1.0, -(float)offset, 0f, 0f, 1f);
            batch.Completed += (_, _) => Finish();
            batch.End();

            ScheduleFallback(span + TimeSpan.FromMilliseconds(120), Finish);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Motion] utility open failed: {ex.Message}");
            Finish();   // 失败也照样是"可见的终态"，Panel 正常打开
        }
    }

    /// <summary>
    /// Utility Panel 关闭：轻微缩回入口方向 + Fade（Opacity 1→0、Scale 1→0.985、TranslationY 0→−5）。
    /// **动画结束后**才由调用方折叠可见性；本方法在收尾里复位状态，保证下次打开干净。
    /// </summary>
    public static void PlayUtilityPanelClose(
        FrameworkElement? panel,
        double centerPointX,
        double centerPointY,
        MotionCompleted? completed)
    {
        if (panel is null)
        {
            completed?.Invoke();
            return;
        }

        if (!SystemAnimationsEnabled || panel.Visibility != Visibility.Visible)
        {
            completed?.Invoke();
            ResetUtilityPanelState(panel);
            return;
        }

        var offset = GetDouble(UtilityDismissOffsetKey, 5.0);
        var endScale = GetDouble(UtilityDismissScaleKey, 0.985);
        var duration = GetDuration(UtilityDismissDurationKey, 0.19);

        var fired = false;
        void Finish()
        {
            if (fired) return;
            fired = true;
            // 先让调用方折叠可见性，再复位 —— 复位会把 Opacity 设回 1，必须在折叠**之后**做，避免闪一帧。
            completed?.Invoke();
            ResetUtilityPanelState(panel);
        }

        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(panel, true);
            var visual = ElementCompositionPreview.GetElementVisual(panel);
            var compositor = visual.Compositor;
            var span = duration.TimeSpan;
            if (span <= TimeSpan.Zero) span = TimeSpan.FromMilliseconds(190);

            visual.CenterPoint = new Vector3((float)centerPointX, (float)centerPointY, 0f);

            var easing = CreateDecelerateEasing(compositor);
            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            StartPanelAnimation(visual, compositor, span, easing,
                1.0, endScale, 0f, -(float)offset, 1f, 0f);
            batch.Completed += (_, _) => Finish();
            batch.End();

            ScheduleFallback(span + TimeSpan.FromMilliseconds(120), Finish);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Motion] utility close failed: {ex.Message}");
            Finish();   // 失败也必须正常关闭（绝不留在半透明）
        }
    }

    /// <summary>
    /// 快速启动 + 柔和收尾的减速曲线（**无 overshoot / 无回弹 / 无果冻感**）。
    /// 参考 iOS 的减速手感，但按桌面生产力软件收得更克制。
    /// </summary>
    private static CompositionEasingFunction CreateDecelerateEasing(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1.0f), new Vector2(0.30f, 1.0f));

    /// <summary>并行启动 Scale / Translation / Opacity 三条通道（共用同一时长与缓动）。</summary>
    private static void StartPanelAnimation(
        Visual visual,
        Compositor compositor,
        TimeSpan span,
        CompositionEasingFunction easing,
        double fromScale,
        double toScale,
        float fromY,
        float toY,
        float fromOpacity,
        float toOpacity)
    {
        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0f, new Vector3((float)fromScale, (float)fromScale, 1f));
        scale.InsertKeyFrame(1f, new Vector3((float)toScale, (float)toScale, 1f), easing);
        scale.Duration = span;

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0f, new Vector3(0f, fromY, 0f));
        slide.InsertKeyFrame(1f, new Vector3(0f, toY, 0f), easing);
        slide.Duration = span;

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, fromOpacity);
        fade.InsertKeyFrame(1f, toOpacity, easing);
        fade.Duration = span;

        visual.StartAnimation("Scale", scale);
        visual.StartAnimation("Translation", slide);
        visual.StartAnimation("Opacity", fade);
    }

    /// <summary>
    /// 把 Panel 吸附到**可见的静止终态**：Scale 1、Translation 0、Opacity **1**、CenterPoint 归零。
    /// 任何路径（成功收尾 / 异常 / 系统关动画）都会走到这里 ⇒ 绝不会"存在却看不见"。
    /// </summary>
    private static void ResetUtilityPanelState(FrameworkElement panel)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(panel);
            visual.StopAnimation("Scale");
            visual.StopAnimation("Translation");
            visual.StopAnimation("Opacity");
            visual.CenterPoint = Vector3.Zero;
            visual.Scale = Vector3.One;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            visual.Opacity = 1f;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Motion] utility reset skipped: {ex.Message}");
        }
        panel.Opacity = 1.0;
    }

    /// <summary>§31：把页面吸附到静止终态 —— 停动画、位移归零、Opacity 恰为 1、清掉残留转场。可安全重复调用。</summary>
    public static void ResetTransitionState(FrameworkElement? element)
    {
        if (element is null) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation("Translation");
            visual.StopAnimation("Opacity");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Motion] page reset skipped: {ex.Message}");
        }
        element.Transitions.Clear();
        element.Opacity = 1.0;
    }

    /// <summary>
    /// 定时兜底：保证收尾一定会发生（Composition Completed 不是可靠的单次保证）。
    /// U58：定时器必须**持有强引用** —— DispatcherQueueTimer 若无人引用会被 GC 回收，
    /// 兜底就永远不会触发，快速连点时就可能留下"两页同时可见"的状态。
    /// </summary>
    private static readonly System.Collections.Generic.List<Microsoft.UI.Dispatching.DispatcherQueueTimer> FallbackTimers = new();

    private static void ScheduleFallback(TimeSpan delay, Action action)
        => ScheduleFallback(delay, action, null);

    /// <summary>
    /// 带"是否仍然有效"判定的兜底：<paramref name="isCurrent"/> 返回 false 时**不执行**动作。
    /// 提示卡高度过渡用它做代次校验（旧代兜底绝不能写终态高度）。
    /// </summary>
    private static void ScheduleFallback(TimeSpan delay, Action action, Func<bool>? isCurrent)
    {
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (queue is null) return;
        var timer = queue.CreateTimer();
        timer.Interval = delay;
        timer.IsRepeating = false;
        FallbackTimers.Add(timer);
        timer.Tick += (sender, _) =>
        {
            sender.Stop();
            FallbackTimers.Remove(sender);
            if (isCurrent is not null && !isCurrent()) return;
            action();
        };
        timer.Start();
    }
}