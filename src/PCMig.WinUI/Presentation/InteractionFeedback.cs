using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PCMig.WinUI.Views;

namespace PCMig.WinUI.Presentation;

/// <summary>
/// U64 Hover Lift / Pressed Sink —— **恢复历史交互反馈**（v0.4.5 定义过、v0.5.0 大改中丢失）。
///
/// 用户口径（原文要点）：
///   · 鼠标放到按钮/Step Card 上、还没点击，UI 就应该非常克制地回应"我知道你在这里" ——
///     **不是单纯换色**，而是表面有一点点真实空间位移；按下时再轻轻被压回去；
///   · 主反馈只能是 <b>Translation / Elevation</b>；**禁止用 Scale 放大**（文字发虚/边缘变形/与 DAEL 不协调）；
///   · Step Card 比按钮略大（Card Lift 2 DIP，Button 1.3 DIP）；Enter 130 ms / Exit 150 ms / Pressed 100 ms；
///   · 数值全部来自 <c>Themes\Motion.xaml</c> 的 Token，调用点不得写死。
///
/// 实现要点（为什么这样做）：
///   1. <b>在应用根上挂一次路由指针事件</b>，一次覆盖全部按钮 —— 包括 DataTemplate 里的侧栏 Step Card
///      （外部拿不到实例）与各个自定义 ControlTemplate 内部的按钮。**不动任何 Template / 几何 / 材质**。
///      ★ 关键修正（2026-09-28 实测）：<c>PointerEntered / PointerExited</c> 的路由策略是 **Direct**，
///      在祖先上 <c>AddHandler</c> **收不到**（日志证据：用户手动把鼠标移过四张卡时，
///      只有 <c>Pressed/Released</c> 被记录，从未出现独立的 Hover）⇒ hover 状态机改由
///      **冒泡的 <c>PointerMoved</c>** 驱动：每次移动取 <c>OriginalSource</c>、向上找 Button，
///      只有当"当前悬停宿主"发生变化时才切换状态（因此鼠标静止时不产生任何开销）。
///   2. 命中的"可交互宿主"只认 <see cref="Button"/>：纯展示 Card（报告清单 / 实时日志 / 统计卡 /
///      对象明细 / 正在复制）不是 Button ⇒ 天然不会获得 Hover（用户第 9 节：避免误导"可以点"）。
///   3. 位移走 Composition 的 <c>Translation</c> 通道（Storyboard 无法解析 Translation —— 本项目已实测），
///      **起点取元素当前实际位移**（接管式）⇒ 快速扫过 / 快速点击时不跳变、不残留。
///   4. 尊重 <see cref="MotionDirector.SystemAnimationsEnabled"/>：系统关动画时**直接落到正确终态**，
///      不播放过渡 —— 但状态本身必须正确（用户第 18 节）。
///   5. 单位 = Canonical DIP，由 UniformScaleHost 统一缩放 ⇒ **绝不再乘 ApplicationUIScale**（避免双倍位移）。
///
/// 与既有链路的边界：只写被命中元素自身的 Translation，绝不碰
/// PageTransitionCoordinator / FluidZoomTransitionCoordinator / UniformScaleHost 的任何状态。
/// </summary>
internal static class InteractionFeedback
{
    // ── Token（Themes\Motion.xaml，唯一数值来源） ────────────────────────────────
    private const string HoverEnterDurationKey = "PCMigMotionHoverEnterDuration";
    private const string HoverExitDurationKey = "PCMigMotionHoverExitDuration";
    private const string PressedDurationKey = "PCMigMotionPressedDuration";
    private const string LiftCardKey = "PCMigMotionHoverLiftCard";
    private const string LiftButtonKey = "PCMigMotionHoverLiftButton";
    private const string SinkCardKey = "PCMigMotionPressedSinkCard";
    private const string SinkButtonKey = "PCMigMotionPressedSinkButton";

    private enum State
    {
        Normal,
        Hover,
        Pressed,
    }

    private static readonly Dictionary<FrameworkElement, State> States = new();
    private static FrameworkElement? _root;
    private static FrameworkElement? _hovered;
    private static FrameworkElement? _pressed;
    private static readonly List<Microsoft.UI.Dispatching.DispatcherQueueTimer> DiagTimers = new();

    /// <summary>
    /// 不参与 Hover Lift / Pressed Sink 的按钮。
    /// 用户口径（2026-09-28）：标题栏的「开发者材质调节」与「查看更新日志」两个入口**不要**这个效果
    /// —— 它们是工具入口，不是主操作面，浮起反而显得它们与旁边的产品标题/徽章不在同一层。
    /// </summary>
    private static readonly HashSet<FrameworkElement> Excluded = new();

    /// <summary>把一个按钮排除在交互反馈之外（幂等）。</summary>
    public static void Exclude(FrameworkElement? element)
    {
        if (element is not null) Excluded.Add(element);
    }

    // ── 诊断（env PCMIG_HOVER_DIAG=1；默认完全关闭）──────────────────────────────
    // 存在的理由：合成输入（SetCursorPos / mouse_event 注入的移动）在 WinUI 3 里**不会**触发
    // PointerEntered，所以 Hover 无法用自动化截图取证 —— 只能让人手动移动鼠标，再看这份日志证明
    // "事件到达 + 目标位移正确"。PointerPressed 路径可以用合成输入验证（已实测通过）。
    private static readonly bool DiagEnabled =
        string.Equals(Environment.GetEnvironmentVariable("PCMIG_HOVER_DIAG"), "1", StringComparison.Ordinal);

    private static void Diag(string message)
    {
        if (!DiagEnabled) return;
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pcmig-hover-diag.log");
            System.IO.File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 诊断绝不影响产品行为
        }
    }

    /// <summary>是否已安装（幂等；同一根只挂一次）。</summary>
    public static bool IsInstalled => _root is not null;

    /// <summary>
    /// 一次性安装：在应用根上挂路由指针事件。必须在视觉树就绪之后调用
    /// （UniformScaleHost 重挂之后应传 <c>ApplicationRoot</c>）。
    /// </summary>
    public static void Install(FrameworkElement? root)
    {
        if (root is null) return;
        if (_root is not null) return;      // 只装一次
        _root = root;

        try
        {
            // ★ 真正驱动 hover 的是 PointerMoved（冒泡事件，祖先可以 AddHandler 捕获）。
            //   PointerEntered / PointerExited 是 Direct 路由 —— 在这里 AddHandler 收不到子元素的进入/离开
            //   （实测：用户手动移过四张卡，日志里从未出现独立的 Hover）。它们仍保留作为**直接命中**时的
            //   补充信号，但状态机以 PointerMoved 为准。
            root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnPointerMoved), true);
            root.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler(OnPointerEntered), true);
            root.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(OnPointerExited), true);
            root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
            root.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnPointerReleased), true);
            root.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnPointerCaptureLost), true);
            Debug.WriteLine("[Interaction] hover lift / pressed sink installed");
        }
        catch (Exception ex)
        {
            // 交互反馈失败绝不影响功能：退化为"没有反馈"，而不是让指针事件链出问题。
            Debug.WriteLine($"[Interaction] install failed: {ex.Message}");
            _root = null;
        }
    }

    // ── 指针事件（路由；handledEventsToo = true 保证即使子元素处理过也能收到） ──

    /// <summary>
    /// hover 状态的**唯一驱动**：鼠标每移动一次，取命中的最深元素、向上找可交互宿主；
    /// 只有"当前悬停宿主"变化时才切换状态 ⇒ 鼠标静止时不产生任何开销，也不会重复启动动画。
    /// </summary>
    private static void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var host = ResolveHost(e.OriginalSource);
        if (ReferenceEquals(host, _hovered)) return;
        Diag($"move -> host={(host as Button)?.Name ?? "null"} src={e.OriginalSource?.GetType().Name ?? "null"}");

        // 离开旧宿主 ⇒ 立即回落（快速扫过时不留下"停在浮起位"的卡片）。
        if (_hovered is not null && !ReferenceEquals(_hovered, _pressed))
        {
            SetState(_hovered, State.Normal);
        }
        _hovered = host;
        if (host is not null && !ReferenceEquals(host, _pressed))
        {
            SetState(host, State.Hover);
        }
    }

    private static void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        var host = ResolveHost(e.OriginalSource);
        if (host is null) return;
        if (ReferenceEquals(host, _hovered)) return;

        // 快速扫过：上一个还停在 Hover 的宿主立即回落 —— 不允许两个同时残留。
        if (_hovered is not null && !ReferenceEquals(_hovered, host)) SetState(_hovered, State.Normal);
        _hovered = host;
        SetState(host, ReferenceEquals(host, _pressed) ? State.Pressed : State.Hover);
    }

    private static void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        var host = ResolveHost(e.OriginalSource);
        if (host is null) return;
        if (!ReferenceEquals(host, _hovered) && !ReferenceEquals(host, _pressed)) return;

        if (ReferenceEquals(host, _hovered)) _hovered = null;
        // 按下后拖出：Pressed 归属仍在该元素上时由 CaptureLost / Released 收尾，这里只做回落。
        if (!ReferenceEquals(host, _pressed)) SetState(host, State.Normal);
    }

    private static void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var host = ResolveHost(e.OriginalSource);
        if (host is null) return;
        _pressed = host;
        _hovered = host;
        SetState(host, State.Pressed);
    }

    private static void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var host = ResolveHost(e.OriginalSource);
        var pressed = _pressed;
        _pressed = null;
        if (pressed is not null)
        {
            // 松开时指针仍在控件上 ⇒ 回 Hover；已离开 ⇒ 回 Normal（用户第 4 节）。
            var stillInside = host is not null && ReferenceEquals(host, pressed);
            SetState(pressed, stillInside ? State.Hover : State.Normal);
            if (!stillInside) _hovered = null;
        }
        else if (host is not null)
        {
            SetState(host, State.Normal);
        }
    }

    private static void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        var pressed = _pressed;
        _pressed = null;
        if (pressed is not null)
        {
            SetState(pressed, ReferenceEquals(pressed, _hovered) ? State.Hover : State.Normal);
        }
    }

    // ── 宿主解析：只认 Button（纯展示 Card 不是 Button ⇒ 天然无 Hover） ───────────

    private static FrameworkElement? ResolveHost(object? source)
    {
        if (_root is null) return null;
        var node = source as DependencyObject;
        while (node is not null)
        {
            if (node is Button button)
            {
                if (!button.IsEnabled || Excluded.Contains(button)) return null;
                return button;
            }
            if (ReferenceEquals(node, _root)) return null;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    /// <summary>该按钮是否位于左侧四步导航内（决定用 Card 档还是 Button 档位移）。</summary>
    private static bool IsStepCard(DependencyObject element)
    {
        var node = VisualTreeHelper.GetParent(element);
        while (node is not null)
        {
            if (node is StepNavigationControl) return true;
            if (ReferenceEquals(node, _root)) return false;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    // ── 状态 → 位移 ─────────────────────────────────────────────────────────────

    private static void SetState(FrameworkElement element, State state)
    {
        if (States.TryGetValue(element, out var previous) && previous == state) return;
        States[element] = state;
        Diag($"SetState {element.GetType().Name}/{(element as Button)?.Name ?? "-"} step={IsStepCard(element)} -> {state}");
        AnimateTo(element, state);
    }

    private static void AnimateTo(FrameworkElement element, State state)
    {
        var isCard = IsStepCard(element);
        var lift = ReadDouble(isCard ? LiftCardKey : LiftButtonKey, isCard ? 2.0 : 1.3);
        var sink = ReadDouble(isCard ? SinkCardKey : SinkButtonKey, isCard ? 0.5 : 0.6);

        var targetY = state switch
        {
            State.Hover => -lift,      // 上浮
            State.Pressed => sink,     // 被轻轻压回（略低于静止面）
            _ => 0.0,                  // Normal
        };

        var span = state switch
        {
            State.Pressed => ReadDuration(PressedDurationKey, 0.10),
            State.Hover => ReadDuration(HoverEnterDurationKey, 0.13),
            _ => ReadDuration(HoverExitDurationKey, 0.15),
        };

        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var current = ReadTranslation(visual);
            var target = new Vector3(current.X, (float)targetY, current.Z);   // 保留 X / Z（Z 是模板自身的层级抬升）

            if (!MotionDirector.SystemAnimationsEnabled || span <= TimeSpan.Zero)
            {
                // 系统关闭动画：不播放过渡，但**终态必须正确**。
                visual.StopAnimation("Translation");
                visual.Properties.InsertVector3("Translation", target);
                return;
            }

            var compositor = visual.Compositor;
            // 与页面 Push / 面板链同一族的手感：快速响应 + 柔和收尾（无 overshoot / 无果冻）。
            var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.10f, 0.90f), new Vector2(0.20f, 1.00f));

            var animation = compositor.CreateVector3KeyFrameAnimation();
            animation.InsertKeyFrame(0f, current);      // 接管式起点 = 当前实际位移
            animation.InsertKeyFrame(1f, target, easing);
            animation.Duration = span;
            visual.StartAnimation("Translation", animation);
            Diag($"AnimateTo {state} card={isCard} from=({current.X:F2},{current.Y:F2},{current.Z:F2}) to=({target.X:F2},{target.Y:F2},{target.Z:F2}) span={span.TotalMilliseconds:F0}ms");
            ScheduleDiagReadBack(element, span);
        }
        catch (Exception ex)
        {
            Diag($"AnimateTo EXCEPTION {ex.GetType().Name}: {ex.Message}");
            Debug.WriteLine($"[Interaction] hover animation skipped: {ex.Message}");
        }
    }

    /// <summary>诊断用：动画应结束后读回实际的 Composition Translation，确认位移真的写进去了。</summary>
    private static void ScheduleDiagReadBack(FrameworkElement element, TimeSpan span)
    {
        if (!DiagEnabled) return;
        try
        {
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (queue is null) return;
            var timer = queue.CreateTimer();
            timer.Interval = span + TimeSpan.FromMilliseconds(80);
            timer.IsRepeating = false;
            DiagTimers.Add(timer);      // 必须强引用（DispatcherQueueTimer 会被 GC 回收 —— 项目已踩过）
            timer.Tick += (sender, _) =>
            {
                sender.Stop();
                DiagTimers.Remove(sender);
                try
                {
                    var visual = ElementCompositionPreview.GetElementVisual(element);
                    var status = visual.Properties.TryGetVector3("Translation", out var after);
                    var xaml = element.Translation;
                    Diag($"AFTER {element.GetType().Name} status={status} comp=({after.X:F2},{after.Y:F2},{after.Z:F2}) xaml=({xaml.X:F2},{xaml.Y:F2},{xaml.Z:F2})");
                }
                catch (Exception ex)
                {
                    Diag($"AFTER read failed: {ex.Message}");
                }
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            Diag($"ScheduleDiagReadBack failed: {ex.Message}");
        }
    }

    private static Vector3 ReadTranslation(Visual visual)
    {
        try
        {
            if (visual.Properties.TryGetVector3("Translation", out var value) == CompositionGetValueStatus.Succeeded)
            {
                return value;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Interaction] read translation skipped: {ex.Message}");
        }
        return Vector3.Zero;
    }

    private static double ReadDouble(string key, double fallback)
    {
        if (Application.Current?.Resources is { } resources
            && resources.TryGetValue(key, out var value)
            && value is double number)
        {
            return number;
        }
        return fallback;
    }

    private static TimeSpan ReadDuration(string key, double fallbackSeconds)
    {
        if (Application.Current?.Resources is { } resources
            && resources.TryGetValue(key, out var value)
            && value is Duration duration)
        {
            return duration.TimeSpan;
        }
        return TimeSpan.FromSeconds(fallbackSeconds);
    }
}