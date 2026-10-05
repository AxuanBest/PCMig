using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace PCMig.WinUI.Presentation;

/// <summary>
/// ★ UI Closure 2026-10-05（用户指令 UI-05 / UI-06）★
/// 进度条的**视觉层**驱动：平滑补间 + 前沿柔光 + 克制扫描高光 + 低密度粒子流。
///
/// 【UI-05：数据真值与视觉插值解耦】
///   · 数据真值仍以 20 Hz 到达（<c>UiFlushPump.FlushInterval = 50ms</c>）+ 状态切换时的瞬时更新；
///   · 本驱动把**渲染**层在 GPU 上补间到该真值，动画跑在 Composition 线程，不触发 XAML layout。
///
/// 【为什么不再写 Width】<c>Width</c> 是布局属性 —— 按 60 fps 插值会每秒触发 60 次布局；
///   只按 20 Hz 写就会出现用户看到的"一帧一帧跳"。因此填充元素改为**常驻满宽**，用
///   <see cref="InsetClip.RightInset"/>（= 轨道宽 − 已完成像素）表示"未完成部分"，只对该标量做动画。
///   Visual 的 Clip 会裁剪自身及其整棵后代子树，挂在填充子树里的装饰物会一起被裁剪。
///
/// 【真值纪律（用户指令 §23 明确禁止伪造进度）】
///   · 目标值**只能是**真实最新值（调用方传入的已传像素），绝不预测、绝不外推、绝不"自爬到 99%"；
///   · 动画值**绝不回写**业务（本类不持有任何业务引用，也不向业务暴露"当前动画值"）；
///   · 暂停 / 停止 / 失败 / 完成由调用方 <see cref="SnapTo"/>（立即落到真值）并 <see cref="SetActive"/>(false)：
///     暂停时让光波继续扫、让粒子继续前流，都属于"撒谎"（用户指令明确列为假修复）。
///
/// 【无跳帧的起点处理】<see cref="InsetClip"/> 没有可回读的当前动画值，所以本类用
///   <see cref="Stopwatch"/> + 上一段的起止值与时长**线性精确复现**视觉位置，作为新一段的起点
///   （故此段不加 easing —— 带 easing 就无法精确复现，会造成回跳）。
///
/// 【UI-06 装饰物的挂载位置与理由】
///   · 扫描高光（Sweep）挂**填充元素**的子树：自动被 <see cref="InsetClip"/> 限制在已完成区内，
///     绝不覆盖未完成区 / 文字 / 百分比（用户指令硬要求）；
///   · 前沿柔光（Glow）与粒子流挂**轨道宿主**的子树：填充元素的右边缘被裁成硬边，柔光挂在那里会被切平；
///     柔光的亮峰放在可见区右缘内侧，其右侧渐隐至全透明，所以未完成区不会被"亮条"越界；
///   · 柔光与粒子容器用**与填充前沿同一组起止值、同一时长**做并行插值物，因此三者严格同步（GPU 侧），
///     不需要任何每帧回调。
/// </summary>
internal sealed class ProgressMotionDriver
{
    // ── UI-05 补间参数 ────────────────────────────────────────────────────────────
    /// <summary>追赶速度上限：每秒最多追赶轨道宽度的 55%（避免"视觉长期落后于真实进度"）。</summary>
    private const double MaxCatchUpPerSecond = 0.55d;

    /// <summary>单段补间时长上下限（秒）。</summary>
    private const double MinSpanSeconds = 0.06d;
    private const double MaxSpanSeconds = 0.40d;

    /// <summary>等效"完全不显示"的右内缩（layout 未完成、轨道宽为 0 时的安全值）。</summary>
    private const float HiddenRightInset = 100000f;

    // ── PHASE C-1 调试探针（用户指令 §3：先用探针证明链路，再调克制参数）────────────────
    /// <summary>
    /// 调试探针开关：环境变量 <c>PCMIG_PROGRESS_DEBUG=1</c>（或 <c>true</c>）时启用。
    ///
    /// 它只做两件事，都不触碰业务因果（PMML-R8：动画永不是业务依赖）：
    ///   ① **高可见度装饰**：扫描高光峰值 alpha 放大、粒子尺寸/亮度放大、前沿画一条竖直标记线
    ///      —— 用来证明"child visual 真的挂上了、没有被别的层遮住"；
    ///   ② <see cref="DescribeDebug"/>：输出 Active / 轨道宽高 / 真值像素 / 视觉像素 / RightInset /
    ///      是否在动画 / 本段时长 / 真值到达间隔 EMA / 三个装饰物的 IsVisible / 最近一次真值时刻。
    ///
    /// 之所以先做探针：上一轮"代码里调用了 StartAnimation"被当成验收结论，而真机像素可能毫无变化
    /// （§0 UI-05/UI-06 被证伪就是这个教训）。探针 + 帧序列像素才是证据。
    /// </summary>
    public static bool DebugEnabled { get; } = ResolveDebugEnabled();

    /// <summary>调试模式下前沿标记线宽（DIP）。</summary>
    private const float EdgeMarkerWidth = 3f;

    // ── UI-06 装饰参数（集中在此，禁止散落到调用点 —— PMML Motion Token 纪律）──────
    /// <summary>前沿柔光带宽度（DIP）。峰值 alpha 低、两侧渐隐 ⇒ subtle glow，不是"亮条"。</summary>
    private const float GlowWidth = 26f;

    /// <summary>柔光亮峰距带左端的距离（DIP）：使峰值恰好落在已完成区右缘，右侧渐隐进未完成区。</summary>
    private const float GlowPeakAt = 9f;

    /// <summary>扫描高光带宽（DIP）。</summary>
    private const float SweepBandWidth = 44f;

    /// <summary>扫描高光峰值 alpha（约 12%，克制；不得亮成"闪光条"）。</summary>
    private const float SweepPeakAlpha = 0x1Eu;

    /// <summary>粒子活动带宽度（DIP）：粒子永远在**前沿之后**这么大的一段范围内流动。</summary>
    private const float ParticleSpan = 34f;

    /// <summary>单颗粒子的循环周期（秒）。</summary>
    private const double ParticleCycleSeconds = 0.90d;

    /// <summary>粒子数量（低密度：用户明确要求"不是火花 / 星空 / 闪粉 / 噪点"）。</summary>
    private const int ParticleCount = 8;

    /// <summary>扫描高光单程时长兜底值（秒）：正式值来自 <c>Themes\Motion.xaml</c> 的
    /// <c>PCMigMotionProgressSweepDuration = 0:0:1.60</c>（Token 是唯一出处，调用点不得写死；
    /// 这里只在该资源查不到时兜底，值必须与 Token 保持一致）。</summary>
    private const double FallbackSweepSeconds = 1.60d;

    // ── PHASE C-2：补间时长的自适应（修正"50 ms UI flush = 50 ms 新真值"这个错误前提）──────────
    /// <summary>补间时长的**安全上限**（秒）。真机实测真值约 2~4 秒才到一次（`intervalEmaMs` 上万毫秒），
    /// 固定 0.40 秒 ⇒ 每个目标只动 0.4 秒、其余时间完全静止 ⇒ 人眼"一段一段跳"。
    /// 自适应后最多用 1.8 秒追到，既填满静止期，又不允许"动画比真值更新还慢"造成永久滞后。</summary>
    private const double MaxSpanCeilingSeconds = 1.80d;

    /// <summary>补间时长占"最近一次真值到达间隔"的比例（0.85 ⇒ 略微提前抵达，末尾留一点静止余量，
    /// 避免下一个目标到来时上一段动画还没结束而被迫接管）。</summary>
    private const double AdaptiveSpanRatio = 0.85d;

    /// <summary>解析后的扫描时长（进程内解析一次，避免每次启动动画都查资源字典）。</summary>
    private static double? _resolvedSweepSeconds;

    private readonly FrameworkElement _fill;
    private readonly FrameworkElement? _trackHost;
    private readonly Compositor _compositor;
    private readonly InsetClip _clip;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private ShapeVisual? _sweepVisual;
    private ShapeVisual? _glowVisual;
    private ContainerVisual? _particleHost;
    private bool _active;
    private float _trackHeight = 12f;

    private double _trackWidth;
    private double _targetPixels;      // 真值目标（已完成像素）
    private double _fromPixels;        // 本段动画起点（视觉像素）
    private double _startedAtMs = double.NaN;
    private double _durationMs;

    // ── PHASE C-1 探针状态（只在 DebugEnabled 时被写入/读取，不影响真值路径）──────────
    private ShapeVisual? _edgeMarkerVisual;
    private DateTime _lastTargetChangeUtc = DateTime.MinValue;
    private double _targetIntervalEmaMs;
    private double _lastTargetChangeAtMs = double.NaN;
    private int _targetChangeCount;
    private double _lastAnimationSpanSeconds;
    private double _lastTargetGapMs = double.NaN;
    private bool _decorationsAttempted;
    private string? _decorationError;

    /// <param name="fill">常驻满宽的填充元素（其右边缘由 InsetClip 表示"未完成"）。</param>
    /// <param name="trackHost">轨道宿主（用于挂前沿柔光与粒子流；可省略，省略则只有扫描高光）。</param>
    public ProgressMotionDriver(FrameworkElement fill, FrameworkElement? trackHost = null)
    {
        _fill = fill;
        _trackHost = trackHost;
        _compositor = ElementCompositionPreview.GetElementVisual(fill).Compositor;
        _clip = _compositor.CreateInsetClip(0f, 0f, HiddenRightInset, 0f);
        ElementCompositionPreview.GetElementVisual(fill).Clip = _clip;

        if (trackHost is not null)
        {
            // ★ PHASE C-5 根因修复：装饰层**惰性创建**。
            //   真机探针证明：在构造函数里创建时 `CreateDecorations()` 抛异常并被下面的 catch 静默吞掉，
            //   于是 `_sweepVisual/_glowVisual/_particleHost` 全为 null ⇒ 屏幕上**根本没有**扫描高光、
            //   柔光与粒子（用户报的"肉眼基本无效果"其实是"根本不存在"，不是"参数太克制"）。
            //   元素只有在进入可视树（Loaded → SizeChanged → SetTrackWidth）之后才允许挂 child visual，
            //   所以改到第一次 SetTrackWidth / SetActive 时创建；异常原文留在诊断里，不再无声降级。
            try { EnsureDecorations(); }
            catch (Exception ex) { ClearDecorations(ex); }
        }
    }

    /// <summary>轨道宽度 / 高度变化（窗口缩放、响应式密度档位切换）时由宿主调用。</summary>
    public void SetTrackWidth(double width)
    {
        if (double.IsNaN(width) || width < 0d) return;
        _trackWidth = width;
        try
        {
            if (_trackHost is not null && _trackHost.ActualHeight > 0d)
            {
                _trackHeight = (float)_trackHost.ActualHeight;
            }

            EnsureDecorations();
            ResizeDecorations();

            // 宽度变了但真值没变 ⇒ 不播动画，直接把裁剪对齐到"当前视觉像素"在**新**轨道上的位置。
            var shown = double.IsNaN(_startedAtMs) ? _targetPixels : CurrentVisualPixels();
            _clip.RightInset = ClampInset(_trackWidth - shown);
            ApplyDecorationOffset(shown, shown, animate: false, spanSeconds: 0d);
        }
        catch
        {
            // 装饰层异常绝不影响进度真值的传递（真值仍由调用方持有）。
        }
    }

    /// <summary>真值目标变化：GPU 补间过去（系统关闭动画时直接落位）。</summary>
    public void SetTarget(double filledPixels)
    {
        if (double.IsNaN(filledPixels) || filledPixels < 0d) return;
        if (Math.Abs(filledPixels - _targetPixels) < 0.5d) return;

        var from = double.IsNaN(_startedAtMs) ? _targetPixels : CurrentVisualPixels();
        _targetPixels = filledPixels;
        NoteTargetArrival();

        if (!MotionDirector.SystemAnimationsEnabled || _trackWidth <= 0d)
        {
            SnapTo(filledPixels);
            return;
        }

        _fromPixels = from;
        var distance = Math.Abs(_targetPixels - _fromPixels);
        var spanSeconds = distance / (MaxCatchUpPerSecond * _trackWidth);
        spanSeconds = Math.Clamp(spanSeconds, MinSpanSeconds, MaxSpanSeconds);

        // ★ PHASE C-2：真值实际约 2~4 秒才到一次 ⇒ 固定 0.40 秒补间之后有 80% 时间是静止的，
        //   视觉上就是"每两秒跳一大段"。用最近一次真值到达间隔的 85%（上限 1.8 秒）填满静止期：
        //   这是**补间时长的自适应**，不是预测真值、不是自爬 —— 起点与终点仍严格等于两次真实目标。
        if (!double.IsNaN(_lastTargetGapMs) && _lastTargetGapMs > 0d)
        {
            var adaptive = Math.Min(_lastTargetGapMs * AdaptiveSpanRatio / 1000d, MaxSpanCeilingSeconds);
            if (adaptive > spanSeconds) spanSeconds = adaptive;
        }
        _durationMs = spanSeconds * 1000d;
        _lastAnimationSpanSeconds = spanSeconds;
        _startedAtMs = _clock.Elapsed.TotalMilliseconds;

        try
        {
            var animation = _compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0f, ClampInset(_trackWidth - _fromPixels));
            animation.InsertKeyFrame(1f, ClampInset(_trackWidth - _targetPixels));
            animation.Duration = TimeSpan.FromSeconds(spanSeconds);
            _clip.StartAnimation(nameof(InsetClip.RightInset), animation);
            ApplyDecorationOffset(_fromPixels, _targetPixels, animate: true, spanSeconds: spanSeconds);
        }
        catch
        {
            SnapTo(_targetPixels);
        }
    }

    /// <summary>
    /// 立即落到真值（暂停 / 停止 / 失败 / 完成 / 无动画模式）。
    /// 用户指令 §23：「Pause 时粒子仍前流」「100% 后无限 shimmer」都属于假修复 ⇒ 这些状态必须 SnapTo。
    /// </summary>
    public void SnapTo(double filledPixels)
    {
        _targetPixels = filledPixels;
        _fromPixels = filledPixels;
        _startedAtMs = double.NaN;
        _durationMs = 0d;
        try
        {
            _clip.StopAnimation(nameof(InsetClip.RightInset));
            _clip.RightInset = ClampInset(_trackWidth <= 0d ? HiddenRightInset : _trackWidth - filledPixels);
            ApplyDecorationOffset(filledPixels, filledPixels, animate: false, spanSeconds: 0d);
        }
        catch
        {
            // 同上：装饰层异常不影响真值。
        }
    }

    /// <summary>
    /// 装饰物开关（用户指令 UI-06 的状态映射）：
    /// **只有 Running 打开**；Pausing / Paused / Stopped / Failed / Completed / CompletedWithErrors
    /// 一律关闭 —— 暂停时让光波继续扫、粒子继续前流都是"动画在撒谎"。业务状态不因本开关而改变。
    /// </summary>
    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;

        if (active)
        {
            // ★ PHASE C-2：新一轮运行 ⇒ 真值间隔统计从头开始。
            //   否则"页面加载完成 → 用户点开始"之间的空闲（真机实测 30+ 秒）会被算进去，
            //   把自适应补间时长推到上限，看起来像"动画拖沓"。
            _lastTargetChangeAtMs = double.NaN;
            _lastTargetGapMs = double.NaN;
            _targetIntervalEmaMs = 0d;
        }

        try
        {
            EnsureDecorations();

            if (_sweepVisual is not null)
            {
                _sweepVisual.IsVisible = active;
                if (active) StartSweepLoop(); else _sweepVisual.StopAnimation("Offset.X");
            }

            if (_glowVisual is not null) _glowVisual.IsVisible = active;
            if (_edgeMarkerVisual is not null) _edgeMarkerVisual.IsVisible = active;
            if (_particleHost is not null)
            {
                _particleHost.IsVisible = active;
                if (active) { StartParticleLoops(); }
                else
                {
                    foreach (var child in _particleHost.Children)
                    {
                        child.StopAnimation("Offset.X");
                        child.StopAnimation("Opacity");
                    }
                }
            }
        }
        catch
        {
            // 同前：装饰失败不影响业务。
        }
    }

    /// <summary>当前**视觉**像素宽度（按上一段参数线性复现；未在动画中则等于真值目标）。</summary>
    private double CurrentVisualPixels()
    {
        if (double.IsNaN(_startedAtMs)) return _targetPixels;
        var elapsed = _clock.Elapsed.TotalMilliseconds - _startedAtMs;
        if (elapsed >= _durationMs) return _targetPixels;
        var t = _durationMs <= 0d ? 1d : elapsed / _durationMs;
        return _fromPixels + ((_targetPixels - _fromPixels) * t);
    }

    private static float ClampInset(double value) => (float)Math.Max(0d, value);

    /// <summary>
    /// PHASE C-1 探针：一行诊断。调用方把它显示在屏幕角落 / 写进日志 —— 这是"装饰物真的挂上、
    /// 真的在动、真的没被遮住"的第一手证据（上一轮的教训：代码里调了 StartAnimation ≠ 像素上有变化）。
    /// </summary>
    public string DescribeDebug()
    {
        var visual = CurrentVisualPixels();
        var rightInsetBase = _clip.RightInset;
        return string.Concat(
            FormattableString.Invariant($"PMD debug=1 active={_active} track={_trackWidth:0.0}x{_trackHeight:0.0} "),
            FormattableString.Invariant($"target={_targetPixels:0.0} visual={visual:0.0} rightInsetBase={rightInsetBase:0.0} "),
            FormattableString.Invariant($"animating={!double.IsNaN(_startedAtMs)} span={_lastAnimationSpanSeconds:0.000} "),
            FormattableString.Invariant($"targetChanges={_targetChangeCount} intervalEmaMs={_targetIntervalEmaMs:0.0} "),
            FormattableString.Invariant($"lastGapMs={_lastTargetGapMs:0.0} "),
            FormattableString.Invariant($"lastTruthUtc={_lastTargetChangeUtc:HH:mm:ss.fff} "),
            FormattableString.Invariant($"decoErr={_decorationError ?? "-"} "),
            FormattableString.Invariant($"sweep={_sweepVisual?.IsVisible ?? false} glow={_glowVisual?.IsVisible ?? false} "),
            FormattableString.Invariant($"particles={_particleHost?.IsVisible ?? false} marker={_edgeMarkerVisual?.IsVisible ?? false}"));
    }

    /// <summary>最近一次真值目标到达时刻（诊断用；<see cref="DateTime.MinValue"/> = 尚无真值）。</summary>
    public DateTime LastTruthUtc => _lastTargetChangeUtc;

    /// <summary>真值到达间隔的指数移动平均（毫秒）—— 用来回答"真值流到底多快"（§3 要求先纠正 20 Hz 前提）。</summary>
    public double TargetIntervalEmaMs => _targetIntervalEmaMs;

    /// <summary>最近一段补间的时长（秒，诊断用）。</summary>
    public double LastAnimationSpanSeconds => _lastAnimationSpanSeconds;

    private void NoteTargetArrival()
    {
        var nowMs = _clock.Elapsed.TotalMilliseconds;
        if (!double.IsNaN(_lastTargetChangeAtMs))
        {
            var gap = nowMs - _lastTargetChangeAtMs;
            if (gap > 0d)
            {
                _lastTargetGapMs = gap;
                _targetIntervalEmaMs = _targetIntervalEmaMs <= 0d ? gap : (_targetIntervalEmaMs * 0.7d) + (gap * 0.3d);
            }
        }

        _lastTargetChangeAtMs = nowMs;
        _targetChangeCount++;
        _lastTargetChangeUtc = DateTime.UtcNow;
    }

    /// <summary>读环境变量 <c>PCMIG_PROGRESS_DEBUG</c>（<c>1</c> / <c>true</c> 视为开启）。</summary>
    private static bool ResolveDebugEnabled()
    {
        try
        {
            var raw = Environment.GetEnvironmentVariable("PCMIG_PROGRESS_DEBUG");
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var value = raw.Trim();
            return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>宿主元素（填充元素；供调用方做几何查询）。</summary>
    public FrameworkElement Host => _fill;

    // ══════════════════════════════════════════════════════════════════════════════
    //  UI-06 装饰物：创建（一次性） / 尺寸对齐 / 位置同步
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 惰性创建装饰层（只尝试一次）。真值路径的任何调用点都可以安全调用它。
    /// </summary>
    private void EnsureDecorations()
    {
        if (_decorationsAttempted || _trackHost is null) return;
        _decorationsAttempted = true;
        CreateDecorations();
    }

    /// <summary>装饰层创建失败：清空句柄并留下异常原文（诊断里可见，不再无声降级）。</summary>
    private void ClearDecorations(Exception ex)
    {
        _sweepVisual = null;
        _glowVisual = null;
        _particleHost = null;
        _edgeMarkerVisual = null;
        _decorationError = ex.GetType().Name + ": " + ex.Message;
    }

    private void CreateDecorations()
    {
        // ① 扫描高光挂填充元素的子树 ⇒ 被 InsetClip 自动裁剪在已完成区内。
        _sweepVisual = BuildSweepVisual();
        var fillChild = _compositor.CreateContainerVisual();
        fillChild.Children.InsertAtTop(_sweepVisual);
        ElementCompositionPreview.SetElementChildVisual(_fill, fillChild);

        // ② 前沿柔光 + 粒子流挂轨道宿主的子树（一个元素只能挂 1 个 child visual ⇒ 共用一个容器）。
        _glowVisual = BuildGlowVisual();
        _particleHost = BuildParticleHost();
        var hostChild = _compositor.CreateContainerVisual();
        hostChild.Children.InsertAtTop(_glowVisual);
        hostChild.Children.InsertAtTop(_particleHost);

        // ③ PHASE C-1 探针：调试模式下在填充前沿画一条竖直标记线（挂轨道宿主子树 ⇒ 不被 InsetClip 裁掉），
        //    用来在任意一帧里肉眼/像素级确认"视觉前沿到底在哪里"。
        if (DebugEnabled)
        {
            _edgeMarkerVisual = BuildEdgeMarkerVisual();
            if (_edgeMarkerVisual is not null) hostChild.Children.InsertAtTop(_edgeMarkerVisual);
        }

        ElementCompositionPreview.SetElementChildVisual(_trackHost!, hostChild);

        _active = false;
        _sweepVisual.IsVisible = false;
        _glowVisual.IsVisible = false;
        _particleHost.IsVisible = false;
        if (_edgeMarkerVisual is not null) _edgeMarkerVisual.IsVisible = false;
        ResizeDecorations();
        StartSweepLoop();
        StartParticleLoops();
    }

    private ShapeVisual BuildSweepVisual()
    {
        var brush = _compositor.CreateLinearGradientBrush();
        brush.StartPoint = new Vector2(0f, 0f);
        brush.EndPoint = new Vector2(1f, 0f);
        // 透明 → 淡白/淡蓝高光 → 透明（用户指令 UI-06 的 Sweep/Shimmer 定义）。
        brush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Argb(0x00, 0xFF, 0xFF, 0xFF)));
        brush.ColorStops.Add(_compositor.CreateColorGradientStop(0.40f, Argb(0x0E, 0xD8, 0xE8, 0xFF)));
        brush.ColorStops.Add(_compositor.CreateColorGradientStop(0.50f, Argb(DebugEnabled ? (byte)0x66 : (byte)SweepPeakAlpha, 0xFF, 0xFF, 0xFF)));
        brush.ColorStops.Add(_compositor.CreateColorGradientStop(0.60f, Argb(0x0E, 0xD8, 0xE8, 0xFF)));
        brush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Argb(0x00, 0xFF, 0xFF, 0xFF)));

        var shape = _compositor.CreateSpriteShape();
        var geometry = _compositor.CreateRectangleGeometry();
        geometry.Size = new Vector2(SweepBandWidth, _trackHeight);
        shape.Geometry = geometry;
        shape.FillBrush = brush;

        var visual = _compositor.CreateShapeVisual();
        visual.Shapes.Add(shape);
        visual.Size = new Vector2(SweepBandWidth, _trackHeight);
        visual.Offset = new Vector3(-SweepBandWidth, 0f, 0f);
        return visual;
    }

    private ShapeVisual BuildGlowVisual()
    {
        var brush = _compositor.CreateLinearGradientBrush();
        brush.StartPoint = new Vector2(0f, 0f);
        brush.EndPoint = new Vector2(1f, 0f);
        // 柔和蓝紫，峰值落在可见区右缘内侧；右侧渐隐至**全透明** ⇒ 未完成区不会被亮条越界。
        brush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Argb(0x00, 0x8C, 0xB4, 0xFF)));
        brush.ColorStops.Add(_compositor.CreateColorGradientStop(0.20f, Argb(0x2E, 0x8C, 0xB4, 0xFF)));
        brush.ColorStops.Add(_compositor.CreateColorGradientStop(0.35f, Argb(DebugEnabled ? (byte)0xA0 : (byte)0x5A, 0xBC, 0xA4, 0xFF)));
        brush.ColorStops.Add(_compositor.CreateColorGradientStop(0.62f, Argb(0x1C, 0x8C, 0xB4, 0xFF)));
        brush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Argb(0x00, 0xBC, 0xA4, 0xFF)));

        var shape = _compositor.CreateSpriteShape();
        var geometry = _compositor.CreateRectangleGeometry();
        geometry.Size = new Vector2(GlowWidth, _trackHeight);
        shape.Geometry = geometry;
        shape.FillBrush = brush;

        var visual = _compositor.CreateShapeVisual();
        visual.Shapes.Add(shape);
        visual.Size = new Vector2(GlowWidth, _trackHeight);
        visual.Offset = new Vector3(0f, 0f, 0f);
        return visual;
    }

    /// <summary>粒子半径（PHASE C-1 探针下放大 1.8×，便于肉眼与像素检测确认它真的在动）。</summary>
    private static float ParticleRadius(int index)
    {
        var radius = index % 3 == 0 ? 2.0f : (index % 3 == 1 ? 1.65f : 1.3f);
        return DebugEnabled ? radius * 1.8f : radius;
    }

    private ContainerVisual BuildParticleHost()
    {
        var host = _compositor.CreateContainerVisual();
        var blue = _compositor.CreateColorBrush(Argb(0xFF, 0x8C, 0xB4, 0xFF));
        var lavender = _compositor.CreateColorBrush(Argb(0xFF, 0xBC, 0xA4, 0xFF));

        for (var i = 0; i < ParticleCount; i++)
        {
            var radius = ParticleRadius(i);
            var geometry = _compositor.CreateEllipseGeometry();
            geometry.Radius = new Vector2(radius, radius);

            var shape = _compositor.CreateSpriteShape(geometry);
            shape.FillBrush = i % 2 == 0 ? blue : lavender;

            var visual = _compositor.CreateShapeVisual();
            visual.Shapes.Add(shape);
            visual.Size = new Vector2(radius * 2f, radius * 2f);
            visual.Opacity = 0f;
            visual.Offset = new Vector3(0f, 0f, 0f);
            host.Children.InsertAtTop(visual);
        }

        return host;
    }

    /// <summary>PHASE C-1 探针：填充前沿的竖直标记线（调试模式专用，红-白-红高对比）。</summary>
    private ShapeVisual? BuildEdgeMarkerVisual()
    {
        try
        {
            var brush = _compositor.CreateLinearGradientBrush();
            brush.StartPoint = new Vector2(0f, 0f);
            brush.EndPoint = new Vector2(0f, 1f);
            brush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Argb(0xF0, 0xFF, 0x4D, 0x4D)));
            brush.ColorStops.Add(_compositor.CreateColorGradientStop(0.50f, Argb(0xFF, 0xFF, 0xFF, 0xFF)));
            brush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Argb(0xF0, 0xFF, 0x4D, 0x4D)));

            var shape = _compositor.CreateSpriteShape();
            var geometry = _compositor.CreateRectangleGeometry();
            geometry.Size = new Vector2(EdgeMarkerWidth, _trackHeight);
            shape.Geometry = geometry;
            shape.FillBrush = brush;

            var visual = _compositor.CreateShapeVisual();
            visual.Shapes.Add(shape);
            visual.Size = new Vector2(EdgeMarkerWidth, _trackHeight);
            visual.Offset = new Vector3(0f, 0f, 0f);
            return visual;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把装饰物的尺寸 / 纵向位置对齐到当前轨道高度与宽度。</summary>
    private void ResizeDecorations()
    {
        if (_sweepVisual is not null)
        {
            _sweepVisual.Size = new Vector2(SweepBandWidth, _trackHeight);
            if (_sweepVisual.Shapes.Count > 0 && _sweepVisual.Shapes[0] is CompositionSpriteShape sweepShape
                && sweepShape.Geometry is CompositionRectangleGeometry sweepGeometry)
            {
                sweepGeometry.Size = new Vector2(SweepBandWidth, _trackHeight);
            }

            if (_active) StartSweepLoop();
        }

        if (_glowVisual is not null)
        {
            _glowVisual.Size = new Vector2(GlowWidth, _trackHeight);
            if (_glowVisual.Shapes.Count > 0 && _glowVisual.Shapes[0] is CompositionSpriteShape glowShape
                && glowShape.Geometry is CompositionRectangleGeometry glowGeometry)
            {
                glowGeometry.Size = new Vector2(GlowWidth, _trackHeight);
            }
        }

        if (_edgeMarkerVisual is not null)
        {
            _edgeMarkerVisual.Size = new Vector2(EdgeMarkerWidth, _trackHeight);
            if (_edgeMarkerVisual.Shapes.Count > 0 && _edgeMarkerVisual.Shapes[0] is CompositionSpriteShape markerShape
                && markerShape.Geometry is CompositionRectangleGeometry markerGeometry)
            {
                markerGeometry.Size = new Vector2(EdgeMarkerWidth, _trackHeight);
            }
        }

        if (_particleHost is not null)
        {
            // 粒子纵向位置在轨道高度内按索引均匀分散（前沿附近视觉更活跃，主体克制）。
            // 注意：VisualCollection 没有索引器 ⇒ 先物化成数组再按序号取（顺序 = 插入顺序）。
            var children = _particleHost.Children.ToArray();
            var count = children.Length;
            for (var i = 0; i < count; i++)
            {
                var child = children[i];
                var radius = ParticleRadius(i);
                var span = Math.Max(0f, _trackHeight - (radius * 2f));
                var fraction = count <= 1 ? 0.5f : (i / (float)(count - 1));
                var y = span <= 0f ? 0f : span * (0.18f + (0.64f * fraction));
                child.Offset = new Vector3(child.Offset.X, y, 0f);
            }
        }
    }

    /// <summary>
    /// 柔光 / 粒子容器的水平位置与填充前沿**同步**：同一组起止值、同一时长、同样线性、GPU 并行插值。
    /// 这样"前沿略亮"与粒子活动带永远贴在真实前沿上，不需要任何每帧回调，也不会跑进未完成区。
    /// </summary>
    private void ApplyDecorationOffset(double fromPixels, double toPixels, bool animate, double spanSeconds)
    {
        var glowFrom = (float)Math.Max(0d, fromPixels - GlowPeakAt);
        var glowTo = (float)Math.Max(0d, toPixels - GlowPeakAt);
        var particleFrom = (float)Math.Max(0d, fromPixels);
        var particleTo = (float)Math.Max(0d, toPixels);

        if (_glowVisual is not null)
        {
            _glowVisual.StopAnimation("Offset.X");
            if (animate)
            {
                var animation = _compositor.CreateScalarKeyFrameAnimation();
                animation.InsertKeyFrame(0f, glowFrom);
                animation.InsertKeyFrame(1f, glowTo);
                animation.Duration = TimeSpan.FromSeconds(spanSeconds);
                _glowVisual.StartAnimation("Offset.X", animation);
            }
            else
            {
                _glowVisual.Offset = new Vector3(glowTo, _glowVisual.Offset.Y, 0f);
            }
        }

        // PHASE C-1 探针：标记线与填充前沿同一组起止值 / 同一时长（GPU 并行插值，不需要每帧回调）。
        if (_edgeMarkerVisual is not null)
        {
            var markerFrom = (float)Math.Max(0d, fromPixels - (EdgeMarkerWidth / 2d));
            var markerTo = (float)Math.Max(0d, toPixels - (EdgeMarkerWidth / 2d));
            _edgeMarkerVisual.StopAnimation("Offset.X");
            if (animate)
            {
                var markerAnimation = _compositor.CreateScalarKeyFrameAnimation();
                markerAnimation.InsertKeyFrame(0f, markerFrom);
                markerAnimation.InsertKeyFrame(1f, markerTo);
                markerAnimation.Duration = TimeSpan.FromSeconds(spanSeconds);
                _edgeMarkerVisual.StartAnimation("Offset.X", markerAnimation);
            }
            else
            {
                _edgeMarkerVisual.Offset = new Vector3(markerTo, 0f, 0f);
            }
        }

        if (_particleHost is null) return;
        _particleHost.StopAnimation("Offset.X");
        if (animate)
        {
            var animation = _compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0f, particleFrom);
            animation.InsertKeyFrame(1f, particleTo);
            animation.Duration = TimeSpan.FromSeconds(spanSeconds);
            _particleHost.StartAnimation("Offset.X", animation);
        }
        else
        {
            _particleHost.Offset = new Vector3(particleTo, _particleHost.Offset.Y, 0f);
        }
    }

    /// <summary>扫描高光循环：从轨道左外侧扫到右外侧（Forever）；挂在填充子树里 ⇒ 越界部分被裁掉。</summary>
    private void StartSweepLoop()
    {
        if (_sweepVisual is null) return;
        var target = (float)Math.Max(SweepBandWidth, _trackWidth);
        var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0f, -SweepBandWidth);
        animation.InsertKeyFrame(1f, target);
        animation.Duration = TimeSpan.FromSeconds(ResolveSweepSeconds());
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        _sweepVisual.StartAnimation("Offset.X", animation);
    }

    /// <summary>
    /// 粒子循环：每颗粒子在前沿后方 <see cref="ParticleSpan"/> 的带内向后循环流动，用**正**延时错开相位。
    /// ★ PHASE C-5 根因修复：这里原来用**负**延时错相，而 Composition 明确拒绝负 DelayTime
    /// （`ArgumentException: An invalid DelayTime is specified. It must be within the range of 0-24 days.`），
    /// 异常沿 CreateDecorations 冒泡后被静默吞掉 ⇒ Sweep/Glow/Particle **一个都没挂上**，
    /// 用户看到的"肉眼基本无效果"其实是"根本不存在"。正延时同样能错相，且未开始的粒子保持 Opacity=0。
    /// </summary>
    private void StartParticleLoops()
    {
        if (_particleHost is null) return;
        var particles = _particleHost.Children.ToArray();
        var count = particles.Length;
        for (var i = 0; i < count; i++)
        {
            var child = particles[i];
            var delay = TimeSpan.FromSeconds(ParticleCycleSeconds * (i / (double)Math.Max(1, count)));

            var move = _compositor.CreateScalarKeyFrameAnimation();
            move.InsertKeyFrame(0f, -ParticleSpan);
            move.InsertKeyFrame(1f, 0f);
            move.Duration = TimeSpan.FromSeconds(ParticleCycleSeconds);
            move.IterationBehavior = AnimationIterationBehavior.Forever;
            move.DelayTime = delay;
            child.StartAnimation("Offset.X", move);

            var fade = _compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0.00f, 0f);
            fade.InsertKeyFrame(0.45f, DebugEnabled ? 0.85f : 0.26f);
            fade.InsertKeyFrame(1.00f, 0f);
            fade.Duration = TimeSpan.FromSeconds(ParticleCycleSeconds);
            fade.IterationBehavior = AnimationIterationBehavior.Forever;
            fade.DelayTime = delay;
            child.StartAnimation("Opacity", fade);
        }
    }

    /// <summary>
    /// 读 <c>PCMigMotionProgressSweepDuration</c> Token（PMML 纪律：动效时长只有 Token 一个出处）。
    /// 资源查不到（或不是合法 Duration）时回退到与 Token 同值的兜底常量，绝不让装饰层报错。
    /// </summary>
    private static double ResolveSweepSeconds()
    {
        if (_resolvedSweepSeconds is double cached) return cached;

        var resolved = FallbackSweepSeconds;
        try
        {
            if (Application.Current?.Resources is { } resources
                && resources.TryGetValue("PCMigMotionProgressSweepDuration", out var value)
                && value is Duration duration
                && duration.TimeSpan.TotalSeconds > 0d)
            {
                resolved = duration.TimeSpan.TotalSeconds;
            }
        }
        catch
        {
            resolved = FallbackSweepSeconds;
        }

        _resolvedSweepSeconds = resolved;
        return resolved;
    }

    private static Windows.UI.Color Argb(byte a, byte r, byte g, byte b) => Windows.UI.Color.FromArgb(a, r, g, b);
}