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
            action();
        };
        timer.Start();
    }
}