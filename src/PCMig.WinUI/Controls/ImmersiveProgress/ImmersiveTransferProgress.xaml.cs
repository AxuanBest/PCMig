using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PCMig.WinUI.Presentation;
using Windows.UI;

namespace PCMig.WinUI.Controls.ImmersiveProgress;

/// <summary>
/// ★ Round-3（执行书 §8 / §12 / §17 / §23）★ <b>PCMig Immersive Transfer Progress</b> ——
/// PCMig 0.5.x 的正式进度视觉控件（不是实验特效）。
///
/// 它表达的五件事必须严格分开（§8）：
///   · <b>Progress Head</b> = 已经确认的迁移事实（由 <see cref="Value"/> 唯一决定）；
///   · <b>Push Band</b> = 任务活性（数据正在工作）；
///   · <b>Particles</b> = 数据材质的微弱流动；
///   · <b>Ripple</b> = Push Band 与材质的局部交互反馈；
///   · <b>Head Halo</b> = 当前活动前沿。
/// 永远不能让 Band / 粒子 / Ripple / Halo 修改 Value / Percent / 已确认字节 / 回执 / Verifier / 作业状态。
///
/// ★ 输入边界（§17 / R28）★ 本控件只接受 <c>Value / Minimum / Maximum / ProgressState / EffectsQuality /
/// ReducedMotion</c> 六个输入。它不知道也不允许知道 Robocopy / SMB / 回执 / Verifier / 作业状态 / 日志 /
/// 源路径 / 目标路径 —— 数据流方向永远是
/// <c>引擎 → Core 可信真值 → 续接显示状态 → 呈现协调器 VisualProgress → 本控件</c>，单向。
///
/// ★ Progress Head = Fact / Push Band = Activity（§18）★
/// 若真实进度 50% 且 8 秒没有新确认字节：Head 必须**固定在 50%**，而 Band 继续周期运行、粒子轻微流动，
/// 让人看出"任务仍然活着"；但绝对禁止 Head 自己从 50 爬到 51、52。
/// </summary>
public sealed partial class ImmersiveTransferProgress : UserControl
{
    /// <summary>视觉进度（0~100）：来自呈现协调器的唯一 <c>VisualProgress</c>。</summary>
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ImmersiveTransferProgress),
        new PropertyMetadata(0d, OnProgressPropertyChanged));

    /// <summary>
    /// ★ Round-3 视觉纠偏（R33）★ 尺寸变体：<see cref="ImmersiveProgressVariant.Hero"/>（Step3 主条）
    /// 或 <see cref="ImmersiveProgressVariant.Compact"/>（底栏）。两者共用同一套 Capsule / Chroma / Head
    /// 语义，只允许几何 token 与装饰强度不同。
    /// </summary>
    public static readonly DependencyProperty VariantProperty = DependencyProperty.Register(
        nameof(Variant), typeof(ImmersiveProgressVariant), typeof(ImmersiveTransferProgress),
        new PropertyMetadata(ImmersiveProgressVariant.Hero, OnProgressPropertyChanged));

    /// <summary>最小值（固定 0）。</summary>
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(ImmersiveTransferProgress),
        new PropertyMetadata(0d, OnProgressPropertyChanged));

    /// <summary>最大值（固定 100）。</summary>
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(ImmersiveTransferProgress),
        new PropertyMetadata(100d, OnProgressPropertyChanged));

    /// <summary>业务状态机（只决定装饰是否活动，不参与任何数值计算）。</summary>
    public static readonly DependencyProperty ProgressStateProperty = DependencyProperty.Register(
        nameof(ProgressState), typeof(ImmersiveProgressState), typeof(ImmersiveTransferProgress),
        new PropertyMetadata(ImmersiveProgressState.Idle, OnProgressPropertyChanged));

    /// <summary>特效质量档（办公机/远程会话降级；绝不改变业务值）。</summary>
    public static readonly DependencyProperty EffectsQualityProperty = DependencyProperty.Register(
        nameof(EffectsQuality), typeof(ImmersiveEffectsQuality), typeof(ImmersiveTransferProgress),
        new PropertyMetadata(ImmersiveEffectsQuality.High, OnProgressPropertyChanged));

    /// <summary>要求降低动效（系统"减少动画"或远程会话）。关闭动效**不改变** Value 与可访问性语义。</summary>
    public static readonly DependencyProperty ReducedMotionProperty = DependencyProperty.Register(
        nameof(ReducedMotion), typeof(bool), typeof(ImmersiveTransferProgress),
        new PropertyMetadata(false, OnProgressPropertyChanged));

    private readonly ImmersiveTransferProgressRenderer _renderer = new();
    private readonly ImmersiveProgressAnimationState _animation = new();

// ★★ 2026-10-06 宿主替换（用户技术方案第一步）★★

    //   旧宿主 CanvasAnimatedControl 底层是 CanvasSwapChainPanel + CanvasSwapChain。WinUI 3 把

    //   SwapChainPanel 作为 external content 交给 Windows，并在自己的合成结果里为它开孔；

    //   普通 XAML 内容不能位于这种外部内容之后参与所期望的透明混合 ⇒ 画布 alpha=0 的位置透出的是

    //   窗口背景（实测近黑暖色 #1C1B1A），而不是父内容卡片。这正是「矩形外壳随窗口材质变化」的机制。

    //   CanvasControl 通过 CanvasImageSource 进入正常 XAML 合成路径 ⇒ 才能真正透出最近的父 Surface。

    private CanvasControl? _canvas;



    // 帧时钟：单调高精度时间戳（方案要求用真实 dt，并维持既有 MaxStepSeconds 上限）

    private readonly System.Diagnostics.Stopwatch _frameClock = System.Diagnostics.Stopwatch.StartNew();
    private System.TimeSpan _lastFrameTime;
    private bool _renderingHooked;
    private bool _frameSuppressed;
    private bool _redrawRequested = true;
    private ImmersiveProgressPalette _palette = ImmersiveProgressPalette.Fallback;
    private bool _paletteReady;
    private ImmersiveProgressMetrics _metrics;
    private double _lastDt;

    // ── ★ UI 线程快照（关键：Win2D 的 Update/Draw 跑在**游戏循环线程**，不是 UI 线程）────────────
    // 教训（真机崩溃 APPCRASH 0xc000027c / combase.dll 0x8001010E = RPC_E_WRONG_THREAD）：
    //   从游戏循环线程读 DependencyProperty、读 Application.Current.Resources、读 AccessibilitySettings
    //   全是**跨线程访问 XAML 对象**，会被 WinRT 直接拒绝并让进程崩溃。
    //   因此所有 XAML / 资源访问都只在 UI 线程发生，结果写进这些普通字段；渲染线程只读普通字段。
    private double _snapshotValue;
    private double _snapshotMinimum;
    private double _snapshotMaximum = 100d;
    private ImmersiveProgressState _snapshotState = ImmersiveProgressState.Idle;
    private ImmersiveEffectsQuality _snapshotQuality = ImmersiveEffectsQuality.High;
    private ImmersiveProgressVariant _snapshotVariant = ImmersiveProgressVariant.Hero;
    private bool _snapshotReducedMotion;
    private bool _snapshotReady;

    public ImmersiveTransferProgress()
    {
        InitializeComponent();
        BuildCanvas();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>视觉进度（0~100）。</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>最小值。</summary>
    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    /// <summary>最大值。</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>业务状态机。</summary>
    public ImmersiveProgressState ProgressState
    {
        get => (ImmersiveProgressState)GetValue(ProgressStateProperty);
        set => SetValue(ProgressStateProperty, value);
    }

    /// <summary>特效质量档。</summary>
    public ImmersiveEffectsQuality EffectsQuality
    {
        get => (ImmersiveEffectsQuality)GetValue(EffectsQualityProperty);
        set => SetValue(EffectsQualityProperty, value);
    }

    /// <summary>是否要求降低动效。</summary>
    public bool ReducedMotion
    {
        get => (bool)GetValue(ReducedMotionProperty);
        set => SetValue(ReducedMotionProperty, value);
    }

    /// <summary>尺寸变体（Hero / Compact）。只改变几何 token 与装饰强度，绝不改变 Value 语义。</summary>
    public ImmersiveProgressVariant Variant
    {
        get => (ImmersiveProgressVariant)GetValue(VariantProperty);
        set => SetValue(VariantProperty, value);
    }

    /// <summary>本变体是否绘制粒子（Compact 关闭）。</summary>
    internal bool VariantParticlesEnabled => _snapshotVariant != ImmersiveProgressVariant.Compact
        || ImmersiveProgressParameters.CompactParticlesEnabled;

    /// <summary>本变体是否绘制 Ripple（Compact 关闭）。</summary>
    internal bool VariantRipplesEnabled => _snapshotVariant != ImmersiveProgressVariant.Compact
        || ImmersiveProgressParameters.CompactRipplesEnabled;

    // ── 只读诊断量（§26 timeline.csv 与验收需要；不参与绘制决策）──────────────────
    /// <summary>当前帧的绘制度量（DIP）。</summary>
    internal ImmersiveProgressMetrics Metrics => _metrics;

    /// <summary>Head（进度前沿）X（DIP）。</summary>
    public double HeadX => _metrics.HeadX;

    /// <summary>已完成进度宽度（DIP）。</summary>
    public double ProgressWidth => _metrics.ProgressWidth;

    /// <summary>Push Band 中心 X（DIP）。</summary>
    public double BandCenterX => _metrics.HeadX + _animation.BandOffsetFromHead;

    /// <summary>Push Band 相位（秒）。</summary>
    public double BandPhaseSeconds => _animation.BandPhaseSeconds;

    /// <summary>Push Band 当前不透明度（0 = 静止段；任意时刻最多一道）。</summary>
    public double BandOpacity => _animation.BandOpacity;

    /// <summary>当前存活粒子数。</summary>
    public int ActiveParticleCount => _animation.ActiveParticleCount;

    /// <summary>
    /// 存活粒子的横向范围（DIP）。**验收判据 ⑨**：`0 <= ParticleMinX` 且 `ParticleMaxX <= ProgressWidth`。
    /// 无存活粒子时两者都为 0（此时判据自动成立）。
    /// </summary>
    public double ParticleMinX => _animation.TryParticleExtent(out var minX, out _) ? minX : 0d;

    /// <summary>存活粒子的最大横向边界（DIP）；无存活粒子时为 0。见 <see cref="ParticleMinX"/>。</summary>
    public double ParticleMaxX => _animation.TryParticleExtent(out _, out var maxX) ? maxX : 0d;

    /// <summary>当前存活 Ripple 数。</summary>
    public int ActiveRippleCount => _animation.ActiveRippleCount;

    /// <summary>Halo 强度（0~1）。</summary>
    public double HaloStrength => _animation.HaloStrength;

    /// <summary>已推进的帧数（确认帧循环真的在跑）。</summary>
    public long TickCount => _animation.TickCount;

    /// <summary>最近一帧的层清单（诊断）。</summary>
    public string LastLayerSummary => _renderer.LastLayers;

    /// <summary>最近一帧的间隔（秒）。</summary>
    public double LastFrameSeconds => _lastDt;

    /// <summary>是否已经挂上绘制面（未加载时为 false）。</summary>
    public bool IsCanvasAttached => _canvas is not null;

    /// <summary>
    /// ★ 离屏自检（PHASE D 取证用，§26）★ 把**当前状态的一帧**画进离屏
    /// <see cref="Microsoft.Graphics.Canvas.CanvasRenderTarget"/>，逐点采样像素并返回文本剖面。
    ///
    /// 它存在的唯一理由：把「<b>Renderer 自己产出的颜色</b>」与「<b>屏幕合成后看到的颜色</b>」分开。
    /// 屏幕截图会经过交换链与合成（可能是非透明合成、可能是陈旧帧），而离屏目标只反映 Renderer 的真实输出 ——
    /// 二者一致 ⇒ 问题在合成/表面；不一致 ⇒ 问题在 Renderer 的调色板或画刷。
    ///
    /// <b>绝不进入生产绘制路径</b>：只在显式调用时执行，不改变任何 DP、不改变动画状态（只读 <c>_animation</c> 当前值）。
    /// </summary>
    internal async System.Threading.Tasks.Task<string> OffscreenSelfTestAsync(double width, double height, int stepPx = 8)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();

        // ★ 关键（实测踩坑）★ 动画控件的 Draw 跑在**游戏循环线程**上，并会就地改写同一批画刷的
        //   StartPoint / EndPoint / Opacity。Win2D 画刷不是线程安全的 ⇒ 若在它绘制的同时用同一批画刷
        //   画离屏目标，结果是**不确定的**（实测：整个离屏目标全透明，即便 Value=100 也是）。
        //   因此：先停帧 → 等一拍 → 离屏绘制 → 恢复。
        // ★ 2026-10-06 ★ CanvasControl 没有 Paused；用帧抑制标志达到同样效果（停帧后再离屏绘制）
        _frameSuppressed = true;

        await System.Threading.Tasks.Task.Delay(180);

        try
        {
            Microsoft.Graphics.Canvas.CanvasDevice device;
            try
            {
                device = _canvas?.Device ?? Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
            }
            catch
            {
                device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
            }

            var w = (float)Math.Max(1d, width);
            var h = (float)Math.Max(1d, height);
            using var target = new Microsoft.Graphics.Canvas.CanvasRenderTarget(device, w, h, 96f);
            var m = BuildMetrics(w, h);

            _renderer.UpdatePalette(_palette);
            _renderer.EnsureResources(device);
            using (var ds = target.CreateDrawingSession())
            {
                ds.Clear(Colors.Transparent);
                _renderer.Draw(ds, m, _animation, _snapshotState, _snapshotQuality, _snapshotReducedMotion);
            }

            sb.Append("OFFSCREEN-SELFTEST value=").Append(_snapshotValue.ToString("0.###", inv))
              .Append(" state=").Append(_snapshotState)
              .Append(" variant=").Append(_snapshotVariant)
              .Append(" quality=").Append(_snapshotQuality)
              .Append(" reducedMotion=").Append(_snapshotReducedMotion)
              .Append(" size=").Append(w.ToString("0.###", inv)).Append('x').Append(h.ToString("0.###", inv))
              .Append(" thickness=").Append(m.Thickness.ToString("0.###", inv))
              .Append(" top=").Append(m.Top.ToString("0.###", inv))
              .Append(" radius=").Append(m.Radius.ToString("0.###", inv))
              .Append(" progress01=").Append(m.Progress01.ToString("0.####", inv))
              .Append(" progressWidth=").Append(m.ProgressWidth.ToString("0.###", inv))
              .Append(" headX=").Append(m.HeadX.ToString("0.###", inv))
              .Append(" layers=").Append(_renderer.LastLayers)
              .AppendLine();
            sb.Append("palette trackTop=#").Append(Hex(_palette.TrackTop))
              .Append(" trackBottom=#").Append(Hex(_palette.TrackBottom))
              .Append(" chromaStart=#").Append(Hex(_palette.ChromaStart))
              .Append(" chromaCyan=#").Append(Hex(_palette.ChromaCyan))
              .Append(" chromaBlue=#").Append(Hex(_palette.ChromaBlue))
              .Append(" chromaDeep=#").Append(Hex(_palette.ChromaDeep))
              .Append(" chromaElectric=#").Append(Hex(_palette.ChromaElectric))
              .Append(" headHalo=#").Append(Hex(_palette.HeadHalo))
              .Append(" headInner=#").Append(Hex(_palette.HeadRim))
              .AppendLine();

            var pixels = target.GetPixelBytes();   // BGRA8，预乘 alpha
            var stride = (int)w * 4;
            var rows = new[] { 0, (int)m.Top, (int)m.CenterY, (int)Math.Max(0d, m.Top + m.Thickness - 1d), (int)h - 1 };
            foreach (var y in rows)
            {
                if (y < 0 || y >= (int)h) continue;
                sb.Append("row y=").Append(y.ToString(inv));
                for (var x = 0; x < (int)w; x += stepPx)
                {
                    var i = y * stride + x * 4;
                    if (i + 3 >= pixels.Length) break;
                    byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];
                    // 反预乘，便于与截图（直通色）直接比较
                    byte ur = a == 0 ? (byte)0 : (byte)Math.Min(255, r * 255 / a);
                    byte ug = a == 0 ? (byte)0 : (byte)Math.Min(255, g * 255 / a);
                    byte ub = a == 0 ? (byte)0 : (byte)Math.Min(255, b * 255 / a);
                    sb.Append(' ').Append(x.ToString(inv)).Append(':')
                      .Append(ur.ToString("X2")).Append(ug.ToString("X2")).Append(ub.ToString("X2"))
                      .Append('/').Append(a.ToString("X2"));
                }
                sb.AppendLine();
            }
        }
        catch (Exception ex)
        {
            sb.Append("SELFTEST-ERROR ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
        }
        finally
        {
            _frameSuppressed = false;
        }

        return sb.ToString();
    }

    private static string Hex(Color c)
        => c.A.ToString("X2") + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");

    /// <summary>
    /// 在视觉树上创建 Win2D 绘制面。
    /// ★ 2026-10-06 ★ 改用 CanvasControl（CanvasImageSource 合成路径），不再使用交换链宿主；
    /// 清屏仍全透明、Host 仍为透明 Grid（PMML-R36）。可重复调用：Unloaded 释放并置空，
    /// Loaded 时若为空则重建（方案第 5.8 条生命周期要求）。
    /// </summary>
    private void BuildCanvas()
    {
        if (_canvas is not null) return;
        _canvas = new CanvasControl
        {
            ClearColor = Colors.Transparent,
            IsTabStop = false,
        };
        _canvas.Draw += OnCanvasDraw;
        Host.Children.Add(_canvas);
        HookRendering();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_canvas is null) BuildCanvas();
        HookRendering();
        RefreshSnapshot();
        RequestRedraw();
    }

    // ★★ 2026-10-05 根因纠正（用户用开发者「材质调节」亲自定位，PMML-R36）★★
    //
    // 【被移除的错误实现】上一版这里有一个 ApplyHostClearColor()：它沿祖先链找"第一个 A>=255 的
    //   Panel/Border 背景色"，然后把它设为画布清屏色。**这个做法本身就是缺陷**，因为：
    //     · 本控件所处的**内容卡片**（Step3 的 ElevatedSurface / 底栏 Surface）是**半透明**材质
    //       （Acrylic / ShellMaterial，alpha < 255）⇒ 它被那段逻辑**跳过**；
    //     · 于是清屏色一路往上取到了**窗口级不透明底色**，等于在 Progress 的整个矩形 Bounds 上
    //       铺了一层"窗口背景层级 Material"。这正是用户观察到的现象：
    //       浅色环境隐约看到矩形壳、深色环境整个 Host 暴露成规整矩形、
    //       用开发者材质调节改窗口背景时这块矩形跟着变。
    //
    // 【正确纪律（PMML-R36 Embedded Material Context）】
    //   Host 必须**完全透明**、**不得**在整个控件矩形范围重新铺任何 Material；
    //   可见材质只能从 **Capsule Track 几何**开始（Track / Chroma / Halo / PushBand / Particle）。
    //   胶囊外的四个角必须直接透出**最近的父 Surface**（内容卡片 / 底栏），而不是窗口底色。
    //   ⇒ ClearColor 恢复为 Transparent（在 BuildCanvas 里设定），本类不再参与任何"填底色"行为。

    /// <summary>
    /// ★ 只在 UI 线程调用 ★ 把 XAML 侧的一切（依赖属性 + 主题资源 + 系统动效设置）拍成普通字段快照，
    /// 供游戏循环线程读取。渲染线程**永不**接触 XAML 对象（否则 RPC_E_WRONG_THREAD 崩溃）。
    /// </summary>
    private void RefreshSnapshot()
    {
        _snapshotValue = Value;
        _snapshotMinimum = Minimum;
        _snapshotMaximum = Maximum;
        _snapshotState = ProgressState;
        _snapshotQuality = EffectsQuality;
        _snapshotVariant = Variant;

        bool systemAnimations;
        try
        {
            systemAnimations = MotionDirector.SystemAnimationsEnabled;
        }
        catch
        {
            systemAnimations = true;   // 探测失败时按"系统允许动画"处理，绝不因此改变业务值
        }
        _snapshotReducedMotion = ReducedMotion || !systemAnimations;

        if (!_paletteReady)
        {
            _palette = ResolvePalette();
            _paletteReady = true;
        }

        _snapshotReady = true;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 帧事件是静态事件 ⇒ 必须成对退订，否则静态事件会持有控件（方案第 5.7 条）
        UnhookRendering();
        if (_canvas is not null)
        {
            _canvas.Draw -= OnCanvasDraw;
            _canvas.RemoveFromVisualTree();
            Host.Children.Remove(_canvas);
            _canvas = null;
        }
        _renderer.Dispose();
    }

    /// <summary>订阅 UI 线程帧事件（CompositionTarget.Rendering 是静态事件，必须成对退订）。</summary>
    private void HookRendering()
    {
        if (_renderingHooked) return;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
        _renderingHooked = true;
        _lastFrameTime = _frameClock.Elapsed;   // 重置时间基线，避免长时间隐藏后跳变（方案第 5.10 条）
    }

    private void UnhookRendering()
    {
        if (!_renderingHooked) return;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
        _renderingHooked = false;
    }

    /// <summary>请求一次重绘（UI 线程）。CanvasControl 的 Invalidate 会合并同一轮的多次请求。</summary>
    private void RequestRedraw()
    {
        _redrawRequested = true;
        try { _canvas?.Invalidate(); } catch { /* 重绘请求失败绝不影响业务真值 */ }
    }

    /// <summary>
    /// 每帧只决定"要不要重绘"；真正的输入读取、度量、装饰推进与渲染**全部在 Draw 里发生一次**
    /// （方案第 5.4 条：不得在 Rendering 与 Draw 各推进一次动画）。
    /// </summary>
    private void OnRendering(object? sender, object e)
    {
        if (_frameSuppressed) return;
        if (!_snapshotReady) return;
        if (Visibility != Visibility.Visible) return;
        if (_redrawRequested) return;
        if (!NeedsContinuousRedraw()) return;
        RequestRedraw();
    }

    /// <summary>
    /// 是否处于"需要连续动效"的状态。
    /// 注意：业务暂停（Paused / Interrupted）**仍需要**继续重绘 —— 因为 R35 要求材质保持低强度活性；
    /// 只有 Idle / Completed / Failed 这类真正静止的状态才停（值变化时由 RequestRedraw 兜一帧）。
    /// </summary>
    private bool NeedsContinuousRedraw()
    {
        if (_snapshotReducedMotion) return false;
        return _snapshotState is ImmersiveProgressState.Running
            or ImmersiveProgressState.Holding
            or ImmersiveProgressState.Warning
            or ImmersiveProgressState.Preparing
            or ImmersiveProgressState.Pausing
            or ImmersiveProgressState.Paused
            or ImmersiveProgressState.Stopping
            or ImmersiveProgressState.Interrupted
            or ImmersiveProgressState.Verifying;
    }

    /// <summary>
    /// 一次实际绘制：算 dt → 取度量 → **推进一次**装饰状态 → 交给 Renderer。
    /// Head 仍然完全由 Value（呈现协调器的 VisualProgress）决定；这里的帧时钟只推进
    /// 粒子 / Push Band / Halo，**不改进度百分比**，也不取代 ProgressPresentationCoordinator。
    /// </summary>
    private void OnCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        _redrawRequested = false;

        // 快照未就绪（尚未 Loaded / 尚未刷新）时不画：宁可空一帧，也不画一份没有真值来源的画面
        if (!_snapshotReady) return;

        var now = _frameClock.Elapsed;
        var dt = (now - _lastFrameTime).TotalSeconds;
        _lastFrameTime = now;
        if (double.IsNaN(dt) || dt < 0d) dt = 0d;
        else if (dt > ImmersiveProgressParameters.MaxStepSeconds) dt = ImmersiveProgressParameters.MaxStepSeconds;
        _lastDt = dt;

        var m = BuildMetrics(sender.Size.Width, sender.Size.Height);
        _metrics = m;

        _animation.Update(dt, (float)m.HeadX, (float)m.ProgressWidth, (float)m.Thickness,
            _snapshotState, _snapshotQuality, _snapshotReducedMotion);

        _renderer.UpdatePalette(_palette);
        _renderer.EnsureResources(sender);
        _renderer.Draw(args.DrawingSession, m, _animation, _snapshotState, _snapshotQuality,
            _snapshotReducedMotion);
    }

    /// <summary>有效"降低动效"：控件显式要求 **或** 系统关闭了动画。**只在 UI 线程求值**（写入快照）。</summary>
    private bool EffectiveReducedMotion
    {
        get
        {
            try
            {
                return ReducedMotion || !MotionDirector.SystemAnimationsEnabled;
            }
            catch
            {
                return ReducedMotion;
            }
        }
    }

    /// <summary>
    /// 由实际尺寸算出度量：厚度取 min(token, 实际高)，垂直居中 ⇒ 同一个控件可以在主 Hero（16 DIP 槽）
    /// 与底栏 Compact 槽里复用，而不需要两份代码（§23）。
    /// <b>只读快照字段</b>（由 UI 线程刷新）⇒ 可在游戏循环线程安全调用。
    /// </summary>
    private ImmersiveProgressMetrics BuildMetrics(double width, double height)
    {
        // ★ R33 ★ 几何 token 由变体决定（Hero: 16/12/6；Compact: 14/10/5）。
        //   两者都满足 Radius = Thickness/2 ⇒ 各自都是**真胶囊**，不是"近似有点圆"。
        var variant = _snapshotVariant;
        var hostHeight = ImmersiveProgressParameters.HostHeightFor(variant);
        var tokenThickness = ImmersiveProgressParameters.ThicknessFor(variant);
        var tokenRadius = ImmersiveProgressParameters.RadiusFor(variant);

        var h = height > 0d ? height : hostHeight;
        var thickness = Math.Min(tokenThickness, h);
        var top = Math.Max(0d, (h - thickness) / 2d);
        var radius = Math.Min(tokenRadius, thickness / 2d);

        var min = _snapshotMinimum;
        var max = _snapshotMaximum;
        var range = max - min;
        var progress01 = range > 0d ? Math.Clamp((_snapshotValue - min) / range, 0d, 1d) : 0d;

        return new ImmersiveProgressMetrics(width, h, thickness, radius, top, top + thickness / 2d, progress01);
    }

    /// <summary>
    /// 解析调色板：颜色**只**来自 Theme Resource（§22）；资源缺失时用登记在此的兜底色，
    /// 兜底色的存在本身也是显式的（而不是散落在 Renderer 里的硬编码）。
    /// </summary>
    private static ImmersiveProgressPalette ResolvePalette()
        => new(
            // Track：中性炭黑/暖黑（R31：不再是被读成"蓝灰外壳"的蓝调，也没有外层描边）
            TrackTop: ResolveColor("ImmersiveProgressTrackTopBrush", Color.FromArgb(0xFF, 0xD6, 0xE1, 0xF8)),
            TrackBottom: ResolveColor("ImmersiveProgressTrackBottomBrush", Color.FromArgb(0xFF, 0xD6, 0xE1, 0xF8)),
            // ★ R32 ★ Track-Space Chroma Field（锚定整个 Track 宽度；左 Aqua/Cyan → 中青蓝 → 后电蓝）
            ChromaStart: ResolveColor("ImmersiveProgressChromaStartBrush", Color.FromArgb(0xFF, 0xA7, 0xE1, 0xDF)),
            ChromaCyanSoft: ResolveColor("ImmersiveProgressChromaCyanSoftBrush", Color.FromArgb(0xFF, 0x83, 0xD5, 0xDC)),
            ChromaCyan: ResolveColor("ImmersiveProgressChromaCyanBrush", Color.FromArgb(0xFF, 0x55, 0xBF, 0xE5)),
            ChromaBlue: ResolveColor("ImmersiveProgressChromaBlueBrush", Color.FromArgb(0xFF, 0x35, 0xA2, 0xEF)),
            ChromaDeep: ResolveColor("ImmersiveProgressChromaDeepBrush", Color.FromArgb(0xFF, 0x1F, 0x83, 0xF7)),
            ChromaElectric: ResolveColor("ImmersiveProgressChromaElectricBrush", Color.FromArgb(0xFF, 0x25, 0x8E, 0xF8)),
            // 纵向材质修饰：极轻（真实强度由 FillShadeOpacity 施加）
            FillLightTop: ResolveColor("ImmersiveProgressFillLightTopBrush", Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            FillLightBottom: ResolveColor("ImmersiveProgressFillLightBottomBrush", Color.FromArgb(0x10, 0x00, 0x10, 0x28)),
            // ★ R34 ★ 体积光场的两层颜色（不是描边色）
            HeadHalo: ResolveColor("ImmersiveProgressHeadHaloBrush", Color.FromArgb(0xFF, 0x9B, 0xDF, 0xFF)),
            HeadRim: ResolveColor("ImmersiveProgressHeadInnerBrush", Color.FromArgb(0xFF, 0xF0, 0xFC, 0xFF)),
            BandOuter: ResolveColor("ImmersiveProgressBandOuterBrush", Color.FromArgb(0xFF, 0xB8, 0xD8, 0xFF)),
            BandCore: ResolveColor("ImmersiveProgressBandCoreBrush", Color.FromArgb(0xFF, 0xE8, 0xF2, 0xFF)),
            Particle: ResolveColor("ImmersiveProgressParticleBrush", Color.FromArgb(0xFF, 0xDC, 0xEC, 0xFF)));

    private static Color ResolveColor(string key, Color fallback)
    {
        try
        {
            var resources = Application.Current?.Resources;
            if (resources is not null && resources.TryGetValue(key, out var raw) && raw is SolidColorBrush brush)
            {
                return brush.Color;
            }
        }
        catch
        {
            // 资源系统异常绝不阻塞进度呈现
        }
        return fallback;
    }

    protected override AutomationPeer OnCreateAutomationPeer()
        => new ImmersiveTransferProgressAutomationPeer(this);

    private static void OnProgressPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ImmersiveTransferProgress progress) return;

        // ★ 任何输入变化都在 UI 线程刷新快照 ★ 渲染线程只读快照，绝不回读 XAML（否则跨线程崩溃）
        progress.RefreshSnapshot();
        // 2026-10-06: CanvasControl is redraw-on-demand, so any input change must request a frame
        // explicitly; otherwise Idle/Completed (non-continuous) states would never repaint.
        progress.RequestRedraw();

        if (e.Property == ValueProperty || e.Property == MaximumProperty || e.Property == MinimumProperty)
        {
            // 只通知可访问性值变化；**不**在这里做任何业务判定
            if (progress.OnCreateAutomationPeer() is ImmersiveTransferProgressAutomationPeer peer)
            {
                peer.RaiseValueChanged();
            }
        }
    }
}
