using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace PCMig.WinUI.Presentation;

/// <summary>
/// PCMig Fluid Zoom Transition（Matched-Geometry）—— Utility Panel 的**触发入口本身**连续 morph 成完整面板。
///
/// 为什么需要新机制（用户第 5/6/42 节）：
///   上一版 "Origin Reveal" 技术上是 Fade + 小幅 Scale（Panel 自己 0.945→1、Opacity 0→1），
///   主视觉仍然是"淡进来" —— 用户人工目视判定为"本质还是淡入弹窗"，机制不符。
///   本机制的主视觉是**几何 morph**：一个从"被点击的那个入口"的真实矩形连续长大成面板矩形
///   （位置 / 尺寸 / 形状 / 表面填充 / 边缘光全部连续变化），Fade 只作为内容交接的辅助层。
///
/// 三层结构（用户第 17 节：绝不能把完整 Panel 压成图标大小 —— 文字会变噪点）：
///   Layer A  Morph Shell —— **纯 Composition** 圆角矩形（无任何文字/控件内容，因此非均匀形变不会
///            产生任何内容变形）。承担 100% 的主视觉：位置 + 非均匀尺寸 + 表面填充 + 边缘光连续变化。
///   Layer B  Source —— 真实触发元素（Developer 图标 / v0.5.0 徽章）本身：朝面板方向轻微漂移 + 淡出
///            （"刚刚点击的东西开始被吞没"），随后在收尾前恢复。
///   Layer C  Destination —— **真实 Panel**（不改尺寸、不缩放，绝不产生内容变形）：从 55% 起淡入接管。
///
/// 坐标系（用户第 10 节：成功关键）：Source Rect / Destination Rect / Overlay 全部位于**同一个
/// Canonical DesignSurface 坐标空间** —— 即 <see cref="UniformScaleHost.ApplicationRoot"/>（未启用
/// UniformScaleHost 时就是 window.Content 本身）。全部用 TransformToVisual(root) 实测，
/// 绝不手写固定 X/Y，也绝不与物理窗口像素 / RasterizationScale 混用；Overlay 挂在应用根内并由
/// Viewbox 一起缩放，因此 Scale = 1.00 / 0.85 / 0.75 下 morph 都不会错位。
///
/// ★ 为什么是"逐帧插值"而不是 Composition 关键帧动画（本机实测 + 官方文档双重证据）：
///   本机实测：<c>Compositor.CreateMatrix4x4KeyFrameAnimation()</c> **不存在**（编译 CS1061）。
///   官方文档：<c>CompositionObject.StartAnimation</c> 的 Remarks 给出**唯一权威的可动画属性表**，
///   其中只有 Visual 的 AnchorPoint/CenterPoint/Offset/Opacity/Orientation/RotationAngle/RotationAxis/
///   Size/TransformMatrix、InsetClip 四边、<c>CompositionColorBrush.Color</c> 与 CompositionPropertySet；
///   **不含** <c>Visual.Scale</c>、不含任何 CompositionShape/CompositionGeometry 属性，
///   且 <c>CompositionBrush.Opacity</c> 这个属性根本不存在。
///   而矩阵没有对应的 KeyFrameAnimation 工厂 ⇒ TransformMatrix 无法用关键帧驱动。
///   因此本类改为：在 UI 线程按**时间**（Stopwatch）逐帧直接给 Composition 属性赋值 ——
///     · <c>ShapeVisual.TransformMatrix</c>（Matrix4x4：非均匀缩放 + 平移）= 位置与尺寸 morph；
///     · <c>CompositionColorBrush.Color</c>（改 alpha）= 表面填充与边缘光"长出来"；
///     · <c>Visual.Opacity</c> = Shell 让位 / Panel 接管 / Source 淡出。
///   这三类属性**存在性**都已由官方 API 页确认，逐帧赋值不依赖任何"可动画"假设，也不触发布局；
///   帧回调与合成帧对齐（CompositionTarget.Rendering），进度按真实时间计算 ⇒ 即使掉帧也匀速且准时收尾。
///   圆角矩形几何固定为"面板尺寸 + 面板圆角"，形变由矩阵的非均匀缩放承担 ⇒ 起点尺寸极小时圆角与
///   1 DIP 描边会被同比例压小，恰好满足用户第 21 节"不要在 Source 极小时把完整 DAEL 挤成很粗的一圈"，
///   且全程连续、无瞬间跳变（第 15 节）。
///
/// 与 <see cref="MotionDirector"/> 的分工：本类只管 Utility Panel 的 morph 编排；
/// 页面 Push 链与旧 Origin Reveal（保留为**第一层 fallback**）仍归 MotionDirector。
///
/// 安全原则：
///   · 系统关闭动画（<see cref="MotionDirector.SystemAnimationsEnabled"/> = false）⇒ 本类一律拒绝启动，
///     调用方直接吸附终态（Snap Open / Snap Close），功能绝不依赖动画；
///   · 任何一步失败 ⇒ 返回 false，调用方回退 Origin Reveal（第二层）→ snap（第三层）；
///   · 收尾按**代次**归属（同 PageTransitionCoordinator 的接管思想）：被新一代接管的旧收尾
///     （帧回调或定时兜底）一律作废，避免把新 Shell 拆掉；
///   · Acrylic / Mica 属 external content（官方已证实 compositor 采样不到）⇒ 本类**绝不**尝试
///     在 Composition 里呈现或采样面板材质本体，只用"接近表面的实色"过渡，随后交回真实 Acrylic。
///
/// 只动视觉：不碰绑定、不碰 Acrylic / Material / DAEL 参数本体、不重建页面或 ViewModel。
/// </summary>
internal sealed class FluidZoomTransitionCoordinator
{
    /// <summary>面板开合的状态（用户第 28 节：快速连点必须没有多个 Morph Visual 叠加）。</summary>
    public enum Phase
    {
        Closed,
        Opening,
        Open,
        Closing,
    }

    // ── Token（Themes\Motion.xaml） ──────────────────────────────────────────────
    private const string OpenDurationKey = "PCMigFluidZoomOpenDuration";
    private const string CloseDurationKey = "PCMigFluidZoomCloseDuration";

    /// <summary>取证专用慢放（默认 1，不影响产品行为）；与页面 Push 链同一个环境变量。</summary>
    private const string SlowMotionVariable = "PCMIG_MOTION_SLOWMO";

    /// <summary>两个 Utility Panel 的内层 Border 都是 CornerRadius=18（Views\DeveloperTuningPanel.xaml / ChangelogPanel.xaml）。</summary>
    private const float PanelCornerRadius = 18f;

    /// <summary>Panel 的 BorderThickness=1（DAEL 边，只借用几何厚度，不改 DAEL 参数）。</summary>
    private const float PanelStrokeThickness = 1f;

    /// <summary>Shell 表面填充的 alpha：Panel 本体是 Acrylic（TintOpacity 0.02 + Luminosity 0），
    /// Shell 阶段用"接近 Panel 表面"的实色近似，贴合后交给真实 Acrylic 接管（用户第 20 节）。</summary>
    private const byte ShellFillAlpha = 0xEB;

    private readonly Func<FrameworkElement?> _rootProvider;
    private readonly List<Microsoft.UI.Dispatching.DispatcherQueueTimer> _fallbackTimers = new();

    // Comp 资源（Shell 生命周期内复用）
    private Canvas? _overlayHost;
    private Compositor? _compositor;
    private ContainerVisual? _shellContainer;
    private ShapeVisual? _shellVisual;
    private CompositionSpriteShape? _shellShape;
    private CompositionRoundedRectangleGeometry? _shellGeometry;
    private CompositionColorBrush? _shellFill;
    private CompositionColorBrush? _shellStroke;

    // 当前这一代的动画状态（逐帧插值用）
    private Stopwatch? _clock;
    private TimeSpan _span = TimeSpan.FromMilliseconds(360);
    private bool _opening;
    private double _fromX, _fromY, _fromScaleX, _fromScaleY;
    private double _toX, _toY, _toScaleX, _toScaleY;
    private double _shellOpacityFrom;
    private double _panelOpacityFrom;
    private Windows.UI.Color _fillBase;
    private Windows.UI.Color _strokeBase;
    private Visual? _panelVisual;
    private Visual? _sourceVisual;
    private FrameworkElement? _panel;
    private FrameworkElement? _source;
    private Action? _onSettled;
    private bool _settled = true;
    private int _generation;

    public FluidZoomTransitionCoordinator(Func<FrameworkElement?> rootProvider)
    {
        _rootProvider = rootProvider ?? throw new ArgumentNullException(nameof(rootProvider));
    }

    /// <summary>当前状态。Opening / Closing 表示 Morph Shell 正在屏幕上。</summary>
    public Phase Current { get; private set; } = Phase.Closed;

    /// <summary>Morph Shell 是否还存在（决定下一次请求是"从当前几何接管"还是"从另一端几何开始"）。</summary>
    public bool HasShell => _shellVisual is not null;

    /// <summary>本机制是否可用（系统动画偏好）。false 时调用方必须走 fallback。</summary>
    public bool IsAvailable => MotionDirector.SystemAnimationsEnabled;

    // ── 诊断（env PCMIG_FLUIDZOOM_DIAG=1 时写 %TEMP%\pcmig-fluidzoom-diag.log；默认完全关闭） ──
    private static readonly bool DiagEnabled =
        string.Equals(Environment.GetEnvironmentVariable("PCMIG_FLUIDZOOM_DIAG"), "1", StringComparison.Ordinal);

    private void Diag(string message)
    {
        if (!DiagEnabled) return;
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pcmig-fluidzoom-diag.log");
            System.IO.File.AppendAllText(
                path,
                $"{DateTime.Now:HH:mm:ss.fff} gen{_generation} settled={_settled} {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 诊断绝不影响产品行为
        }
    }

    // ── 对外入口 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 打开方向：source（触发入口）→ destination（面板）。
    /// 调用前要求：panel 已 <c>Visibility=Visible</c>、已由调用方定位完成、XAML <c>Opacity=0</c>。
    /// </summary>
    /// <returns>false = 未能启动（调用方必须回退 Origin Reveal / snap）。</returns>
    public bool TryOpen(FrameworkElement? panel, FrameworkElement? source, Action? onSettled) =>
        StartTransition(panel, source, opening: true, onSettled);

    /// <summary>
    /// 关闭方向：destination（面板）→ source（触发入口），**完整反向 morph**（用户第 23 节）。
    /// Source Rect 在每次关闭时**实时重算**（用户第 24 节：窗口尺寸 / 整体缩放 / 布局都可能变过）。
    /// </summary>
    public bool TryClose(FrameworkElement? panel, FrameworkElement? source, Action? onSettled) =>
        StartTransition(panel, source, opening: false, onSettled);

    /// <summary>
    /// 立即停止逐帧回调、拆除 Morph Shell 与残留（不改变 panel 可见性）。
    /// 用于：窗口关闭、fallback 到 Origin Reveal 之前、以及系统关动画时的吸附路径。
    /// </summary>
    public void Abort()
    {
        _generation++;      // 作废所有在途收尾
        _settled = true;
        DetachFrameCallback();
        StopFallbackTimers();
        RemoveShell();
        _panelVisual = null;
        _sourceVisual = null;
        _panel = null;
        _source = null;
        _onSettled = null;
    }

    // ── 主流程 ──────────────────────────────────────────────────────────────────

    private bool StartTransition(FrameworkElement? panel, FrameworkElement? source, bool opening, Action? onSettled)
    {
        Diag($"StartTransition? opening={opening} panel={panel?.GetType().Name ?? "null"} source={source?.GetType().Name ?? "null"} anim={MotionDirector.SystemAnimationsEnabled}");
        if (panel is null || source is null) return false;
        if (!MotionDirector.SystemAnimationsEnabled) return false;

        // 几何：全部在 Canonical DesignSurface 坐标空间内实测（绝不用手写 X/Y）。
        if (_rootProvider() is not FrameworkElement root) return false;
        if (root is not Panel host) return false;   // Overlay 必须能挂进应用根的子集合
        if (!TryMeasure(source, root, out var sourceRect)) return false;
        if (!TryMeasure(panel, root, out var panelRect)) return false;
        if (panelRect.Width < 40.0 || panelRect.Height < 40.0) return false;

        if (!TryEnsureShell(host, panelRect, out var inherited)) return false;
        Diag($"StartTransition opening={opening} inherited={inherited} srcRect={sourceRect.X:F0},{sourceRect.Y:F0} {sourceRect.Width:F0}x{sourceRect.Height:F0} panelRect={panelRect.X:F0},{panelRect.Y:F0} {panelRect.Width:F0}x{panelRect.Height:F0}");

        // 起点（接管式）：Shell 已在跑 ⇒ 从它**当前实际几何**接续，绝不跳回固定起点 ——
        // 这正是页面链 U59 已验证的接管思想：快速连点时动画持续在跑、不消失、不重叠。
        double fromX, fromY, fromScaleX, fromScaleY, shellOpacityFrom;
        if (inherited)
        {
            var current = _shellVisual!.TransformMatrix;
            fromX = current.M41;
            fromY = current.M42;
            fromScaleX = current.M11;
            fromScaleY = current.M22;
            shellOpacityFrom = _shellVisual.Opacity;
        }
        else
        {
            var fromRect = opening ? sourceRect : panelRect;
            fromX = fromRect.X;
            fromY = fromRect.Y;
            fromScaleX = fromRect.Width / panelRect.Width;
            fromScaleY = fromRect.Height / panelRect.Height;
            shellOpacityFrom = 1.0;
        }

        // 终点：打开 ⇒ 面板几何；关闭 ⇒ 实时重算的入口几何。
        var toRect = opening ? panelRect : sourceRect;
        var toX = toRect.X;
        var toY = toRect.Y;
        var toScaleX = toRect.Width / panelRect.Width;
        var toScaleY = toRect.Height / panelRect.Height;

        if (!IsFinite(fromX, fromY, fromScaleX, fromScaleY)
            || !IsFinite(toX, toY, toScaleX, toScaleY)
            || fromScaleX <= 0 || fromScaleY <= 0 || toScaleX <= 0 || toScaleY <= 0)
        {
            RemoveShell();
            return false;
        }

        Visual panelVisual;
        Visual sourceVisual;
        try
        {
            panelVisual = ElementCompositionPreview.GetElementVisual(panel);
            sourceVisual = ElementCompositionPreview.GetElementVisual(source);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FluidZoom] backing visual unavailable, fallback: {ex.Message}");
            RemoveShell();
            return false;
        }

        var span = ApplySlowMotion(ReadDuration(opening ? OpenDurationKey : CloseDurationKey, opening ? 0.36 : 0.30).TimeSpan);
        if (span <= TimeSpan.Zero) span = TimeSpan.FromMilliseconds(opening ? 360 : 300);

        // ── 记账（新一代接管：旧收尾因代次不匹配而作废）──────────────────────────
        var generation = ++_generation;
        _opening = opening;
        _span = span;
        _fromX = fromX; _fromY = fromY; _fromScaleX = fromScaleX; _fromScaleY = fromScaleY;
        _toX = toX; _toY = toY; _toScaleX = toScaleX; _toScaleY = toScaleY;
        _shellOpacityFrom = shellOpacityFrom;
        _panelOpacityFrom = opening ? 0.0 : Math.Clamp(panelVisual.Opacity, 0.0, 1.0);
        _fillBase = ResolveShellFillColor();
        _strokeBase = ResolveShellStrokeColor();
        _panelVisual = panelVisual;
        _sourceVisual = sourceVisual;
        _panel = panel;
        _source = source;
        _onSettled = onSettled;
        _settled = false;

        try
        {
            // 打开方向：Panel 先全透明参与布局（几何已由调用方定位完成），从 55% 起才接管可见性。
            panelVisual.Opacity = (float)_panelOpacityFrom;

            ApplyFrame(0.0);                       // 起点帧：绝不出现"第一帧在旧位置"
            _clock = Stopwatch.StartNew();
            AttachFrameCallback();
            Current = opening ? Phase.Opening : Phase.Closing;
            Diag($"Started gen{generation} span={span.TotalMilliseconds:F0}ms opening={opening}");
            ScheduleFallback(span + TimeSpan.FromMilliseconds(140), () => FinishNow(generation));
            return true;
        }
        catch (Exception ex)
        {
            // 任何 Composition 失败都绝不能把面板留在半透明 / 不可见状态。
            Debug.WriteLine($"[FluidZoom] transition failed, fallback: {ex.Message}");
            DetachFrameCallback();
            RemoveShell();
            RestoreSourceVisual(source);
            if (opening) panel.Opacity = 1.0;
            if (generation == _generation) Current = opening ? Phase.Open : Phase.Closed;
            _settled = true;
            return false;
        }
    }

    // ── 逐帧插值（唯一驱动路径；只写"存在性已确认"的 Composition 属性，不依赖可动画假设） ──

    private void AttachFrameCallback()
    {
        try
        {
            CompositionTarget.Rendering -= OnRendering;
            CompositionTarget.Rendering += OnRendering;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FluidZoom] frame callback attach skipped: {ex.Message}");
        }
    }

    private void DetachFrameCallback()
    {
        try
        {
            CompositionTarget.Rendering -= OnRendering;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FluidZoom] frame callback detach skipped: {ex.Message}");
        }
        _clock = null;
    }

    private void OnRendering(object? sender, object e)
    {
        if (_clock is null || _settled) return;
        var total = _span.TotalMilliseconds;
        if (total <= 0) total = 1.0;
        var progress = Math.Clamp(_clock.Elapsed.TotalMilliseconds / total, 0.0, 1.0);
        ApplyFrame(progress);
        if (progress >= 1.0) FinishNow(_generation);
    }

    /// <summary>
    /// 把进度 <paramref name="t"/>（0–1，真实时间口径）落到所有通道上。
    /// 几何用 EaseOutCubic（前段就走完近一半 ⇒ 绝不会出现用户第 37 节禁止的"0–60% 几乎不动、
    /// 最后突然展开"）；表面/交接类通道用线性分段，让每个阶段的可读性可预测。
    /// </summary>
    private void ApplyFrame(double t)
    {
        if (_shellVisual is null || _shellFill is null || _shellStroke is null) return;

        // Layer A 几何：位置 + 非均匀尺寸（矩阵的 M11/M22 缩放、M41/M42 平移）
        var morph = EaseOutCubic(t);
        var x = _fromX + (_toX - _fromX) * morph;
        var y = _fromY + (_toY - _fromY) * morph;
        var scaleX = _fromScaleX + (_toScaleX - _fromScaleX) * morph;
        var scaleY = _fromScaleY + (_toScaleY - _fromScaleY) * morph;
        _shellVisual.TransformMatrix = BuildMorphMatrix(x, y, scaleX, scaleY);

        // Layer A 表面：填充与边缘光（DAEL）"长出来" —— 只改 alpha，颜色本身来自真实材质资源。
        double fillFactor, strokeFactor, shellOpacity;
        if (_opening)
        {
            fillFactor = 0.22 + 0.78 * Segment(t, 0.00, 0.32);
            strokeFactor = Segment(t, 0.10, 0.55);
            shellOpacity = _shellOpacityFrom * (1.0 - Segment(t, 0.80, 1.00));
        }
        else
        {
            fillFactor = 1.0 - Segment(t, 0.55, 1.00);
            strokeFactor = 1.0 - Segment(t, 0.50, 0.95);
            shellOpacity = _shellOpacityFrom * (1.0 - Segment(t, 0.70, 1.00));
        }
        _shellFill.Color = WithAlpha(_fillBase, ScaleAlpha(_fillBase.A, fillFactor));
        _shellStroke.Color = WithAlpha(_strokeBase, ScaleAlpha(_strokeBase.A, strokeFactor));
        _shellVisual.Opacity = (float)Math.Clamp(shellOpacity, 0.0, 1.0);

        // Layer C 真实 Panel：55% 之后淡入接管（位置/尺寸已是终值 ⇒ 交接处看不出跳变）。
        if (_panelVisual is not null)
        {
            var panelOpacity = _opening
                ? Segment(t, 0.55, 1.00)
                : _panelOpacityFrom * (1.0 - Segment(t, 0.00, 0.40));
            _panelVisual.Opacity = (float)Math.Clamp(panelOpacity, 0.0, 1.0);
        }

        // Layer B 真实 Source：打开时淡出、面板就位后恢复。
        // ★ 刻意**不做位移漂移**：source 的 Composition Translation 会被 TransformToVisual 计入，
        //   而关闭方向必须实时重算"入口当前真实矩形"（用户第 24 节）—— 一旦 source 被漂移，
        //   关闭时量到的就是漂移后的位置（实测 445,128 vs 图标真实 416,60，偏移 29/68 px），
        //   Shell 会缩到图标的下方偏右而不是图标本身。把"移动"完全交给 Layer A 的几何 morph。
        if (_opening && _sourceVisual is not null)
        {
            double sourceOpacity;
            if (t < 0.22) sourceOpacity = 1.0 - Segment(t, 0.00, 0.22);
            else if (t < 0.70) sourceOpacity = 0.0;
            else sourceOpacity = Segment(t, 0.70, 0.88);
            _sourceVisual.Opacity = (float)Math.Clamp(sourceOpacity, 0.0, 1.0);
        }
    }

    /// <summary>收尾（代次归属：被新一代接管的旧收尾一律作废）。</summary>
    private void FinishNow(int generation)
    {
        if (generation != _generation)
        {
            Diag($"FinishNow IGNORED (stale gen{generation})");
            return;
        }
        if (_settled)
        {
            Diag($"FinishNow IGNORED (already settled, gen{generation})");
            return;
        }
        Diag($"FinishNow RUN opening={_opening} elapsed={_clock?.Elapsed.TotalMilliseconds:F0}ms span={_span.TotalMilliseconds:F0}ms hasShell={HasShell}");
        _settled = true;

        DetachFrameCallback();
        StopFallbackTimers();
        RemoveShell();

        var panel = _panel;
        var source = _source;
        var opening = _opening;
        var callback = _onSettled;
        _onSettled = null;
        _panelVisual = null;
        _sourceVisual = null;

        if (panel is not null) panel.Opacity = 1.0;   // 可见终态；关闭方向的折叠由调用方在同一回调里做
        if (source is not null) RestoreSourceVisual(source);

        Current = opening ? Phase.Open : Phase.Closed;
        callback?.Invoke();
    }

    // ── Morph Shell（Layer A）────────────────────────────────────────────────────

    /// <summary>
    /// 建立或复用 Morph Shell。复用（<paramref name="inherited"/> = true）时把几何尺寸换到新面板尺寸，
    /// 并按**当前渲染矩阵**反算起点，保证复用瞬间的渲染结果逐像素不变（接管不跳变）。
    /// </summary>
    private bool TryEnsureShell(Panel host, Rect panelRect, out bool inherited)
    {
        inherited = false;
        if (_shellVisual is not null && _shellGeometry is not null)
        {
            inherited = true;
            var renderedWidth = _shellGeometry.Size.X * _shellVisual.TransformMatrix.M11;
            var renderedHeight = _shellGeometry.Size.Y * _shellVisual.TransformMatrix.M22;
            _shellGeometry.Size = new Vector2((float)panelRect.Width, (float)panelRect.Height);
            _shellVisual.TransformMatrix = BuildMorphMatrix(
                _shellVisual.TransformMatrix.M41,
                _shellVisual.TransformMatrix.M42,
                renderedWidth / panelRect.Width,
                renderedHeight / panelRect.Height);
            return true;
        }

        try
        {
            // Overlay：应用根的最后一个子元素 ⇒ z 序高于业务内容与两个 Utility Panel，
            // 但它 IsHitTestVisible=false，绝不拦截任何点击。
            _overlayHost = new Canvas { IsHitTestVisible = false };
            host.Children.Add(_overlayHost);

            var compositor = ElementCompositionPreview.GetElementVisual(_overlayHost).Compositor;
            var surface = new Vector2(
                (float)Math.Max(1.0, host.ActualWidth),
                (float)Math.Max(1.0, host.ActualHeight));

            var container = compositor.CreateContainerVisual();
            container.Size = surface;

            var shapeVisual = compositor.CreateShapeVisual();
            shapeVisual.Size = surface;

            // 几何固定为**面板尺寸 + 面板圆角**；形变由 TransformMatrix 承担（见类注释的通道选择理由）。
            var geometry = compositor.CreateRoundedRectangleGeometry();
            geometry.Size = new Vector2((float)panelRect.Width, (float)panelRect.Height);
            geometry.Offset = Vector2.Zero;
            geometry.CornerRadius = new Vector2(PanelCornerRadius);

            var fillBrush = compositor.CreateColorBrush(WithAlpha(ResolveShellFillColor(), 0));
            var strokeBrush = compositor.CreateColorBrush(WithAlpha(ResolveShellStrokeColor(), 0));

            var shape = compositor.CreateSpriteShape(geometry);
            shape.FillBrush = fillBrush;
            shape.StrokeBrush = strokeBrush;
            shape.StrokeThickness = PanelStrokeThickness;

            shapeVisual.Shapes.Add(shape);
            container.Children.InsertAtTop(shapeVisual);
            ElementCompositionPreview.SetElementChildVisual(_overlayHost, container);

            _compositor = compositor;
            _shellContainer = container;
            _shellVisual = shapeVisual;
            _shellShape = shape;
            _shellGeometry = geometry;
            _shellFill = fillBrush;
            _shellStroke = strokeBrush;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FluidZoom] shell build failed, fallback: {ex.Message}");
            RemoveShell();
            return false;
        }
    }

    /// <summary>拆除 Shell 与 Overlay（幂等；不触碰 Panel / Source 的可见性）。</summary>
    private void RemoveShell()
    {
        try
        {
            if (_overlayHost is not null)
            {
                ElementCompositionPreview.SetElementChildVisual(_overlayHost, null);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FluidZoom] shell teardown skipped: {ex.Message}");
        }

        try
        {
            if (_overlayHost is not null && _overlayHost.Parent is Panel parent)
            {
                parent.Children.Remove(_overlayHost);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FluidZoom] overlay removal skipped: {ex.Message}");
        }

        _overlayHost = null;
        _compositor = null;
        _shellContainer = null;
        _shellVisual = null;
        _shellShape = null;
        _shellGeometry = null;
        _shellFill = null;
        _shellStroke = null;
    }

    // ── 辅助 ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Morph 矩阵 = 非均匀缩放（M11/M22，相对 Shape 本地原点）+ 平移（M41/M42，Canonical 坐标）。
    /// 起点尺寸由"源矩形 / 面板矩形"的比值给出，因此从入口矩形**连续**长成面板矩形。
    /// </summary>
    private static Matrix4x4 BuildMorphMatrix(double x, double y, double scaleX, double scaleY) =>
        new(
            (float)scaleX, 0f, 0f, 0f,
            0f, (float)scaleY, 0f, 0f,
            0f, 0f, 1f, 0f,
            (float)x, (float)y, 0f, 1f);

    /// <summary>线性分段（用于"某通道在 t ∈ [a,b] 区间内从 0 走到 1"的时序）。</summary>
    private static double Segment(double t, double from, double to)
    {
        if (to <= from) return t >= to ? 1.0 : 0.0;
        return Math.Clamp((t - from) / (to - from), 0.0, 1.0);
    }

    /// <summary>前段快速展开、收尾柔和（几何主通道用；保证中间帧可见的连续形变）。</summary>
    private static double EaseOutCubic(double t)
    {
        var inverse = 1.0 - Math.Clamp(t, 0.0, 1.0);
        return 1.0 - inverse * inverse * inverse;
    }

    private static byte ScaleAlpha(byte alpha, double factor) =>
        (byte)Math.Clamp((int)Math.Round(alpha * Math.Clamp(factor, 0.0, 1.0)), 0, 255);

    /// <summary>把元素吸附回"入口可见"的静止状态（Translation 归零 / XAML Opacity 1）。</summary>
    private static void RestoreSourceVisual(FrameworkElement source)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(source);
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FluidZoom] source restore skipped: {ex.Message}");
        }
        source.Opacity = 1.0;
    }

    /// <summary>在 Canonical 根坐标系内实测元素矩形（TransformToVisual；NaN/∞ 一律判失败）。</summary>
    private static bool TryMeasure(FrameworkElement element, FrameworkElement root, out Rect rect)
    {
        rect = default;
        try
        {
            var origin = element.TransformToVisual(root).TransformPoint(new Point(0, 0));
            var width = element.ActualWidth;
            var height = element.ActualHeight;
            if (!IsFinite(origin.X, origin.Y, width, height)) return false;
            if (width <= 0 || height <= 0) return false;
            rect = new Rect(origin.X, origin.Y, width, height);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FluidZoom] measure skipped: {ex.Message}");
            return false;
        }
    }

    private static bool IsFinite(params double[] values)
    {
        foreach (var value in values)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return false;
        }
        return true;
    }

    private static Duration ReadDuration(string key, double fallbackSeconds)
    {
        if (Application.Current?.Resources is { } resources
            && resources.TryGetValue(key, out var value)
            && value is Duration duration)
        {
            return duration;
        }
        return new Duration(TimeSpan.FromSeconds(fallbackSeconds));
    }

    private static TimeSpan ApplySlowMotion(TimeSpan span)
    {
        var raw = Environment.GetEnvironmentVariable(SlowMotionVariable);
        if (string.IsNullOrWhiteSpace(raw)) return span;
        if (!double.TryParse(raw, out var factor)) return span;
        if (factor <= 0.1 || factor >= 60.0) return span;
        return TimeSpan.FromMilliseconds(span.TotalMilliseconds * factor);
    }

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha) =>
        Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>
    /// Shell 表面色 —— 从**真实材质资源**解析（材质若调整，Shell 自动跟随），并朝白色提亮一档：
    /// 面板本体是 Acrylic（Tint #F2F6FF / Luminosity 0），在浅色背景上"更亮的白"才是它真实的观感；
    /// 若直接用 tint 混色，Shell 会与背景几乎同色 ⇒ 中间帧只剩"遮挡内容"这一条可见线索。
    /// 解析不到资源时退回保守浅色。绝不硬编码"看起来像"的白色。
    /// </summary>
    private static Windows.UI.Color ResolveShellFillColor()
    {
        try
        {
            if (Application.Current?.Resources is { } resources
                && resources.TryGetValue("PCMigUtilityPanelMaterial80", out var resource))
            {
                switch (resource)
                {
                    case AcrylicBrush acrylic:
                        return Blend(
                            Blend(acrylic.FallbackColor, acrylic.TintColor, 0.55, ShellFillAlpha),
                            Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF),
                            0.45,
                            ShellFillAlpha);
                    case SolidColorBrush solid:
                        return WithAlpha(solid.Color, ShellFillAlpha);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FluidZoom] shell color resolve skipped: {ex.Message}");
        }
        return Windows.UI.Color.FromArgb(ShellFillAlpha, 0xFA, 0xFC, 0xFF);
    }

    /// <summary>边缘光（DAEL）颜色 —— 取 Quiet 档渐变的第一个停点（只借颜色，不改 DAEL 参数本体）。</summary>
    private static Windows.UI.Color ResolveShellStrokeColor()
    {
        try
        {
            if (Application.Current?.Resources is { } resources
                && resources.TryGetValue("PCMigDAELBrushQuiet", out var resource)
                && resource is RadialGradientBrush radial
                && radial.GradientStops.Count > 0)
            {
                return radial.GradientStops[0].Color;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FluidZoom] shell stroke resolve skipped: {ex.Message}");
        }
        return Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);
    }

    private static Windows.UI.Color Blend(Windows.UI.Color from, Windows.UI.Color to, double amount, byte alpha)
    {
        byte Mix(byte a, byte b) => (byte)Math.Clamp((int)Math.Round(a + (b - a) * amount), 0, 255);
        return Windows.UI.Color.FromArgb(alpha, Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
    }

    /// <summary>
    /// 定时兜底：帧回调不是可靠的单次保证（同页面链的教训）。
    /// 定时器必须**持有强引用**，否则会被 GC 回收、兜底永不触发。
    /// </summary>
    private void ScheduleFallback(TimeSpan delay, Action action)
    {
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (queue is null) return;
        var timer = queue.CreateTimer();
        timer.Interval = delay;
        timer.IsRepeating = false;
        _fallbackTimers.Add(timer);
        timer.Tick += (sender, _) =>
        {
            sender.Stop();
            _fallbackTimers.Remove(sender);
            action();
        };
        timer.Start();
    }

    private void StopFallbackTimers()
    {
        foreach (var timer in _fallbackTimers.ToArray())
        {
            try
            {
                timer.Stop();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FluidZoom] timer stop skipped: {ex.Message}");
            }
        }
        _fallbackTimers.Clear();
    }
}