using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System.Numerics;
using Windows.UI;

namespace PCMig.WinUI.Controls.ImmersiveProgress;

/// <summary>
/// ★ Round-3（执行书 §13）★ 一帧的几何度量。全部由控件按**当前实际尺寸**算出，
/// Renderer 不读任何 XAML 元素、不做布局查询。
/// </summary>
internal readonly record struct ImmersiveProgressMetrics(
    double Width,
    double Height,
    double Thickness,
    double Radius,
    double Top,
    double CenterY,
    double Progress01)
{
    /// <summary>胶囊左端 X（DIP）。</summary>
    public double Left => 0d;

    /// <summary>已完成进度宽度（DIP）。</summary>
    public double ProgressWidth => Math.Max(0d, Width * Math.Clamp(Progress01, 0d, 1d));

    /// <summary>Head（进度前沿）X（DIP）。</summary>
    public double HeadX => ProgressWidth;

    /// <summary>轨道矩形。</summary>
    public Windows.Foundation.Rect TrackRect => new(0d, Top, Math.Max(0d, Width), Thickness);

    /// <summary>填充矩形。</summary>
    public Windows.Foundation.Rect FillRect
    {
        get
        {
            var width = ProgressWidth;
            return new Windows.Foundation.Rect(0d, Top, width, Thickness);
        }
    }

    /// <summary>
    /// ★ R31（Capsule Integrity）★ 填充胶囊几何：矩形 + 真圆角。
    /// 低进度时半径收敛到 <c>ProgressWidth/2</c> ⇒ 形成"小圆珠/短胶囊"，而不是小方块。
    /// </summary>
    public Windows.Foundation.Rect FillCapsuleRect => FillRect;

    /// <summary>填充胶囊圆角半径（DIP）。</summary>
    public double FillRadius => Math.Min(Radius, Math.Max(0d, ProgressWidth / 2d));

    /// <summary>当前填充端帽圆角（低进度时自动收小，避免几何翻折）。</summary>
    public double ProgressRadius => FillRadius;
}

/// <summary>
/// ★ Round-3（执行书 §13 / §14）★ PCMig Immersive Transfer Progress 的绘制后端。
///
/// ★ Round-3 视觉纠偏（R31 / R32 / R34）★ 本版相对上一版的四处实质修正：
///   1. <b>R31 Capsule Integrity</b>：删掉"全轨道 1 DIP 外描边"（<c>DrawBorder</c>）与
///      "全宽硬 Top Edge 条"（原 <c>DrawTrack</c> 里那条 Rect 层）—— 这两样叠加在 12 DIP 生产厚度上
///      正是用户看到的"外面套了一个方形壳"。改为：Track 只用自身纵向材质渐变，不做任何可见外壳。
///   2. <b>R31 动态层真裁剪</b>：Push Band / Particles / Ripple 过去用 <c>Rect(0,Top,ProgressWidth,Thickness)</c>
///      裁剪 —— 那是**矩形**，会把圆角端部重新铺成平切/方切。现在统一用
///      <see cref="FillCapsuleGeometry"/>（RoundedRectangle 真胶囊）裁剪。
///   3. <b>R32 Track-Space Chroma Field</b>：Fill 过去是 <c>#5AA3FF → #1677F2</c> 的**固定纵向蓝渐变**
///      （所以 100% 只会得到"一根蓝条"）。现在 Fill 主体是**锚定整个 Track 宽度**的水平色场
///      （Aqua/Cyan → 青蓝 → 电蓝），EndPoint = <c>Width</c>（**不是** ProgressWidth）；
///      进度增长只是**逐渐揭示**这个色场 ⇒ 颜色随进度自然变化，且完全确定性。
///      纵向只保留极轻的材质修饰（不再是主导）。
///   4. <b>R34 Volumetric Head Halo</b>：删掉旧版那圈可辨识的 1 DIP 白色锐光弧与它的半圆弧几何构造。
///      改为两层**无硬边界**的高斯体积光场（Outer / Inner 椭圆径向渐变），中心位于 Head **后方** 3~6 DIP，
///      让光从材质内部溢出。绝不以"一圈白线"表达 Head。
///
/// 分配纪律（§14）：**每帧禁止** new Brush / new Geometry / new Particle / LINQ / 集合抖动。
/// 画刷在设备变化时创建；胶囊几何只在 <c>ProgressWidth</c> 变化超过
/// <see cref="ImmersiveProgressParameters.GeometryRebuildThreshold"/> 时重建（几何正确 > 纸面零分配）。
///
/// 语义纪律（§17 / R28）：本类**只**接受度量、动画状态、调色板、状态机枚举与档位。
/// 它不引用、也不可能读到 Robocopy / SMB / 回执 / Verifier / 作业状态 / 日志 / 源或目标路径。
/// </summary>
internal sealed class ImmersiveTransferProgressRenderer : IDisposable
{
    private const int BandStops = 9;            // 高斯近似采样点数
    private const int BandVerticalStripes = 7;  // 垂直柔化条纹数
    private const int ChromaStopCount = 7;      // Track-Space Chroma Field 的固定停靠点数

    // ★ 2026-10-05 真机修复（用户判词「进度条外面有明显黑边/黑灰色外壳」）★
    //   所有画刷必须用 CanvasAlphaMode.Straight，**不能**用 Premultiplied：
    //   真机像素实测（无遮罩状态下 8 倍放大采样）显示 Track 颜色与 token 逐字节一致
    //   （#2A2D31 → #1F2226），但紧贴 Track 的 1px 边缘是 #1E1E1E / #222222 —— 比 Track 更暗。
    //   这正是预乘 alpha 下**不透明几何边缘**的衰减（边缘像素 RGB 被衰减而 alpha 仍为 1），
    //   在任何背景上都会呈现为一圈近黑描边。改为 Straight 后边缘不再变暗。
    private CanvasLinearGradientBrush? _trackBrush;
    private CanvasLinearGradientBrush? _chromaBrush;      // 横向色场（EndPoint = TrackWidth）
    private CanvasLinearGradientBrush? _fillLightBrush;   // 纵向极轻材质修饰
    private CanvasLinearGradientBrush? _bandOuterBrush;
    private CanvasLinearGradientBrush? _bandCoreBrush;
    private CanvasRadialGradientBrush? _haloOuterBrush;   // Layer C: Cyan Support Glow
    private CanvasRadialGradientBrush? _haloBloomBrush;   // Layer B: Soft Outer Bloom
    private CanvasRadialGradientBrush? _haloInnerBrush;   // Layer A: Arc-attached Inner Glow

    // ★ R31 胶囊几何缓存 ★ 只在宽度变化超过阈值时重建，绝不每帧 new Geometry
    private CanvasGeometry? _fillCapsuleGeometry;
    private float _fillCapsuleWidth = -1f;
    private float _fillCapsuleRadius = -1f;

    private ICanvasResourceCreator? _device;
    private ImmersiveProgressPalette _palette = ImmersiveProgressPalette.Fallback;
    private bool _paletteValid;

    /// <summary>最近一帧的层清单（诊断：确认各层都被走到）。</summary>
    public string LastLayers { get; private set; } = "-";

    /// <summary>胶囊几何最近一次重建时的进度宽度（诊断用；-1 表示尚未构建）。</summary>
    public double LastCapsuleGeometryWidth => _fillCapsuleWidth;

    /// <summary>
    /// 确保画刷与几何已就绪。**只在设备变化时**创建对象；颜色变化走 <see cref="UpdatePalette"/>
    /// （就地改 Stop / Color，不新建对象）。调用前必须先 <see cref="UpdatePalette"/>（否则画刷会是全透明）。
    /// </summary>
    public void EnsureResources(ICanvasResourceCreator device)
    {
        if (_trackBrush is not null && ReferenceEquals(_device, device)) return;

        DisposeResources();
        _device = device;

        // Track：自身纵向材质渐变（本身就极轻，不需要任何"外壳"层去表达材质）
        _trackBrush = new CanvasLinearGradientBrush(device, VerticalStops(_palette.TrackTop, _palette.TrackBottom),
            CanvasEdgeBehavior.Clamp, CanvasAlphaMode.Straight);

        // ★ R32 ★ Fill 主体：横向 Track-Space Chroma Field。
        //   StartPoint / EndPoint 每帧按 **Track 宽度** 设置（见 DrawFill），这里只固化 7 个色停靠点的形状。
        _chromaBrush = new CanvasLinearGradientBrush(device, ChromaStops(_palette),
            CanvasEdgeBehavior.Clamp, CanvasAlphaMode.Straight);

        // Fill 纵向修饰：极轻的上下亮度差（材质感），不再是色彩主导
        _fillLightBrush = new CanvasLinearGradientBrush(device, VerticalStops(_palette.FillLightTop, _palette.FillLightBottom),
            CanvasEdgeBehavior.Clamp, CanvasAlphaMode.Straight);

        // ★ 关键（Round-3 PHASE C 踩坑）★ C#/WinRT 投影下 CanvasLinearGradientBrush.Stops 是**只读属性**
        //   （CS0200：无法为属性或索引器赋值），但属性返回的列表本身可写 ⇒ 渐变形状只在创建 / 调色板变化时
        //   写入一次，帧内**只改 StartPoint / EndPoint / Opacity**，既满足 §14"每帧不许 new Brush"，
        //   也不会每帧写 9×7 次 Stop（那是 63 次 WinRT 互操作调用）。
        _bandOuterBrush = new CanvasLinearGradientBrush(device,
            GaussianStops(BandStops, (float)ImmersiveProgressParameters.BandSigmaX, _palette.BandOuter),
            CanvasEdgeBehavior.Clamp, CanvasAlphaMode.Straight);
        _bandCoreBrush = new CanvasLinearGradientBrush(device,
            GaussianStops(BandStops, (float)ImmersiveProgressParameters.BandCoreSigmaX, _palette.BandCore),
            CanvasEdgeBehavior.Clamp, CanvasAlphaMode.Straight);

        // ★ R34 ★ Head 体积光场：两层**径向**渐变（中心透明→峰值→外缘 0），无任何硬边界。
        _haloOuterBrush = new CanvasRadialGradientBrush(device, HaloRadialStops(_palette.HeadHalo),
            CanvasEdgeBehavior.Clamp, CanvasAlphaMode.Straight);
        _haloBloomBrush = new CanvasRadialGradientBrush(device, HaloRadialStops(_palette.HeadRim),
            CanvasEdgeBehavior.Clamp, CanvasAlphaMode.Straight);
        _haloInnerBrush = new CanvasRadialGradientBrush(device, HaloRadialStops(_palette.HeadRim),
            CanvasEdgeBehavior.Clamp, CanvasAlphaMode.Straight);

        // 胶囊几何在第一次绘制时按真实宽度构建
        _fillCapsuleWidth = -1f;
        _fillCapsuleRadius = -1f;
        _fillCapsuleGeometry = null;
        _paletteValid = false;
    }

    /// <summary>纵向两段渐变（Track / Fill 材质修饰：极轻纵向亮度差，不做 3D 凹槽）。</summary>
    private static CanvasGradientStop[] VerticalStops(Color top, Color bottom)
        => new[]
        {
            new CanvasGradientStop { Position = 0f, Color = WithAlpha(top, 1f) },
            new CanvasGradientStop { Position = 1f, Color = WithAlpha(bottom, 1f) },
        };

    /// <summary>
    /// ★ R32 ★ Track-Space Chroma Field 的 7 个固定停靠点（位置 0→1 对应 Track 0%→100%）。
    /// 语义必须守住：左＝Aqua/Cyan，中＝青蓝，后＝电蓝/蓝。**不得**退化成纯蓝纵向条。
    /// </summary>
    private static CanvasGradientStop[] ChromaStops(ImmersiveProgressPalette p)
        => new[]
        {
            new CanvasGradientStop { Position = 0.00f, Color = WithAlpha(p.ChromaStart, 1f) },
            new CanvasGradientStop { Position = 0.10f, Color = WithAlpha(p.ChromaCyanSoft, 1f) },
            new CanvasGradientStop { Position = 0.24f, Color = WithAlpha(p.ChromaCyan, 1f) },
            new CanvasGradientStop { Position = 0.42f, Color = WithAlpha(p.ChromaBlue, 1f) },
            new CanvasGradientStop { Position = 0.60f, Color = WithAlpha(p.ChromaDeep, 1f) },
            new CanvasGradientStop { Position = 0.78f, Color = WithAlpha(p.ChromaDeep, 1f) },
            new CanvasGradientStop { Position = 1.00f, Color = WithAlpha(p.ChromaElectric, 1f) },
        };

    /// <summary>水平高斯形状（σ 由 token 给定；只在创建 / 调色板变化时计算）。</summary>
    private static CanvasGradientStop[] GaussianStops(int count, float sigmaX, Color color)
    {
        var stops = new CanvasGradientStop[count];
        var span = sigmaX * 3f;
        for (var i = 0; i < count; i++)
        {
            var t = i / (float)(count - 1);
            var x = -span + 2f * span * t;
            var g = MathF.Exp(-(x * x) / (2f * sigmaX * sigmaX));
            stops[i] = new CanvasGradientStop { Position = t, Color = WithAlpha(color, g) };
        }
        return stops;
    }

    /// <summary>
    /// ★ R34 ★ 体积光场的径向剖面：中心透明 → 峰值 → 外缘精确 0。
    /// 中心留一点透明是为了让"最亮处"落在 Head **内部**而不是轮廓线上（避免读成一根白线）。
    /// </summary>
    private static CanvasGradientStop[] HaloRadialStops(Color color)
        => new[]
        {
            new CanvasGradientStop { Position = 0.00f, Color = WithAlpha(color, 0.00f) },
            new CanvasGradientStop { Position = 0.28f, Color = WithAlpha(color, 0.55f) },
            new CanvasGradientStop { Position = 0.52f, Color = WithAlpha(color, 1.00f) },
            new CanvasGradientStop { Position = 0.78f, Color = WithAlpha(color, 0.28f) },
            new CanvasGradientStop { Position = 1.00f, Color = WithAlpha(color, 0.00f) },
        };

    /// <summary>
    /// 把预计算的渐变形状写入画刷（就地写列表项；只读属性不可整体赋值）。
    /// 不去读列表的 <c>Count</c>（投影出来的成员形状不确定），直接按已知 Stop 数走索引 —— 索引器可写已由本文件其它调用点证明。
    /// </summary>
    private static void CopyStops(CanvasLinearGradientBrush brush, CanvasGradientStop[] stops)
    {
        var list = brush.Stops;
        // ★ Win2D 投影坑（已被编译器确证）★ ICanvasGradientStopCollection.Count 是**方法组**，不是属性
        //   ⇒ 不能写 list.Count，必须取 stops.Length 作为上限（形状由创建时的 Stop 数决定，长度已知）。
        var count = stops.Length;
        for (var i = 0; i < count; i++) list[i] = stops[i];
    }

    /// <summary>设置调色板（来自 Theme Resource；每帧调用也无分配）。</summary>
    public void UpdatePalette(ImmersiveProgressPalette palette)
    {
        if (_paletteValid && palette.Equals(_palette)) return;
        _palette = palette;
        _paletteValid = true;

        if (_trackBrush is not null)
        {
            CopyStops(_trackBrush, VerticalStops(palette.TrackTop, palette.TrackBottom));
        }
        if (_chromaBrush is not null)
        {
            CopyStops(_chromaBrush, ChromaStops(palette));
        }
        if (_fillLightBrush is not null)
        {
            CopyStops(_fillLightBrush, VerticalStops(palette.FillLightTop, palette.FillLightBottom));
        }
        if (_bandOuterBrush is not null)
        {
            CopyStops(_bandOuterBrush, GaussianStops(BandStops, (float)ImmersiveProgressParameters.BandSigmaX, palette.BandOuter));
        }
        if (_bandCoreBrush is not null)
        {
            CopyStops(_bandCoreBrush, GaussianStops(BandStops, (float)ImmersiveProgressParameters.BandCoreSigmaX, palette.BandCore));
        }
    }

    /// <summary>
    /// 画一帧。所有坐标均为 DIP，绘制会话已由 CanvasAnimatedControl 提供。
    /// 层序（R31 之后不再有 Border 层）：
    ///   ① Track ② Chroma Field ③ 纵向材质修饰 ④⑤ Push Band ⑥ Head 体积光场 ⑦⑧ Particles / Ripples
    /// </summary>
    public void Draw(CanvasDrawingSession ds, ImmersiveProgressMetrics m, ImmersiveProgressAnimationState anim,
        ImmersiveProgressState state, ImmersiveEffectsQuality quality, bool reducedMotion)
    {
        if (_trackBrush is null || m.Width <= 0d || m.Thickness <= 0d) return;

        EnsureVerticalGradient(_trackBrush, m);

        // ── ① Track（只有自身材质渐变；没有外壳、没有全宽硬边）────────────────────
        ds.FillRoundedRectangle(m.TrackRect, (float)m.Radius, (float)m.Radius, _trackBrush!);

        // ── ②③ Fill：Track-Space Chroma Field + 极轻纵向修饰，全部裁在真胶囊内 ────
        var capsule = EnsureFillCapsuleGeometry(m);
        var fillDrawn = DrawFill(ds, m, capsule);

        // ── ④⑤ Push Band（外层光压 + 亮芯）：裁进**同一枚胶囊**───────────────
        var bandDrawn = DrawPushBand(ds, m, anim, quality, reducedMotion, capsule);

        // ── ⑥ Head 体积光场（两层高斯衰减，中心在 Head 后方；不裁剪、无硬边）────
        var haloDrawn = DrawHead(ds, m, anim, quality);

        // ── ⑦⑧ Particles / Ripples：同一枚胶囊内 ────────────────────────────
        var particleDrawn = DrawParticles(ds, m, anim, quality, reducedMotion, capsule);

        LastLayers = $"track=1 chroma={(fillDrawn ? 1 : 0)} band={(bandDrawn ? 1 : 0)} " +
                     $"halo={(haloDrawn ? 1 : 0)} particles={anim.ActiveParticleCount} ripples={anim.ActiveRippleCount} " +
                     $"border=0 capsuleW={_fillCapsuleWidth:0.##} bandPhase={anim.BandPhaseSeconds:0.###} bandOpacity={anim.BandOpacity:0.###}";
    }

    /// <summary>
    /// ★ R31 ★ 取（必要时重建）填充胶囊几何。宽度未超过阈值就复用 ⇒ 帧内零几何分配；
    /// 阈值存在的原因：几何正确性优先于"纸面零分配"，但也不能每帧 new 一个 Geometry。
    /// </summary>
    private CanvasGeometry? EnsureFillCapsuleGeometry(ImmersiveProgressMetrics m)
    {
        if (_device is null) return null;
        if (m.ProgressWidth <= 0.5d)
        {
            DisposeCapsuleGeometry();
            return null;
        }

        var width = (float)m.ProgressWidth;
        var radius = (float)m.FillRadius;
        if (_fillCapsuleGeometry is not null
            && Math.Abs(width - _fillCapsuleWidth) < (float)ImmersiveProgressParameters.GeometryRebuildThreshold
            && Math.Abs(radius - _fillCapsuleRadius) < (float)ImmersiveProgressParameters.GeometryRebuildThreshold)
        {
            return _fillCapsuleGeometry;
        }

        try
        {
            DisposeCapsuleGeometry();
            var rect = new Windows.Foundation.Rect(0d, m.Top, width, m.Thickness);
            _fillCapsuleGeometry = CanvasGeometry.CreateRoundedRectangle(_device, rect, radius, radius);
            _fillCapsuleWidth = width;
            _fillCapsuleRadius = radius;
            return _fillCapsuleGeometry;
        }
        catch
        {
            // 几何构造失败绝不影响进度真值：退化为"不裁剪"（仍然画，只是不裁边）
            DisposeCapsuleGeometry();
            return null;
        }
    }

    private void DisposeCapsuleGeometry()
    {
        _fillCapsuleGeometry?.Dispose();
        _fillCapsuleGeometry = null;
        _fillCapsuleWidth = -1f;
        _fillCapsuleRadius = -1f;
    }

    /// <summary>
    /// ★ R32 ★ Fill 主体 = 横向色场（EndPoint 锚定 <c>m.Width</c>，即整个 Track 空间），
    /// 上层再叠一层极轻的纵向材质修饰。两层都裁在同一枚圆角胶囊里。
    /// </summary>
    private bool DrawFill(CanvasDrawingSession ds, ImmersiveProgressMetrics m, CanvasGeometry? capsule)
    {
        if (m.ProgressWidth <= 0d) return false;
        if (capsule is null)
        {
            // 几何不可用时也保持正确的圆角外观（FillRoundedRectangle 本身就是胶囊），只是少了硬裁剪
            var r = (float)m.FillRadius;
            DrawChromaAndLight(ds, m, r);
            return true;
        }

        using var layer = ds.CreateLayer(1f, capsule);
        DrawChromaAndLight(ds, m, 0f);
        return true;
    }

    private void DrawChromaAndLight(CanvasDrawingSession ds, ImmersiveProgressMetrics m, float fallbackRadius)
    {
        // ② Track-Space Chroma Field：StartPoint/EndPoint 按 Track 宽度，而不是进度宽度
        _chromaBrush!.StartPoint = new Vector2(0f, (float)m.CenterY);
        _chromaBrush.EndPoint = new Vector2((float)m.Width, (float)m.CenterY);
        _chromaBrush.Opacity = 1f;

        if (fallbackRadius > 0f)
        {
            ds.FillRoundedRectangle(m.FillRect, fallbackRadius, fallbackRadius, _chromaBrush);
        }
        else
        {
            ds.FillRectangle(m.FillRect, _chromaBrush);
        }

        // ③ 纵向材质修饰：极轻（Clamp 到 ShadeOpacity），只表达"这是一根有厚度的材质"
        EnsureVerticalGradient(_fillLightBrush!, m);
        _fillLightBrush!.Opacity = (float)ImmersiveProgressParameters.FillShadeOpacity;
        if (fallbackRadius > 0f)
        {
            ds.FillRoundedRectangle(m.FillRect, fallbackRadius, fallbackRadius, _fillLightBrush);
        }
        else
        {
            ds.FillRectangle(m.FillRect, _fillLightBrush);
        }
        _fillLightBrush.Opacity = 1f;
    }

    private bool DrawPushBand(CanvasDrawingSession ds, ImmersiveProgressMetrics m,
        ImmersiveProgressAnimationState anim, ImmersiveEffectsQuality quality, bool reducedMotion, CanvasGeometry? capsule)
    {
        if (reducedMotion) return false;
        if (anim.BandOpacity <= 0.001f) return false;
        if (m.ProgressWidth <= 1d) return false;

        var center = (float)(m.HeadX + anim.BandOffsetFromHead);
        var outerSigma = (float)ImmersiveProgressParameters.BandSigmaX;
        var coreSigma = (float)ImmersiveProgressParameters.BandCoreSigmaX;
        var qualityScale = quality switch
        {
            ImmersiveEffectsQuality.High => 1.0f,
            ImmersiveEffectsQuality.Balanced => 0.8f,
            _ => 0.6f,
        };

        // ★ R31 ★ Band 只能出现在已完成区域内，且必须裁进**圆角胶囊**（不是矩形）
        if (capsule is null) return false;
        using var layer = ds.CreateLayer(1f, capsule);

        DrawGaussianBand(ds, m, _bandOuterBrush!, center, outerSigma, 3f,
            (float)ImmersiveProgressParameters.BandOuterOpacity * qualityScale * anim.BandOpacity);
        DrawGaussianBand(ds, m, _bandCoreBrush!, center, coreSigma, 2f,
            (float)ImmersiveProgressParameters.BandCoreOpacity * qualityScale * anim.BandOpacity);
        return true;
    }

    /// <summary>
    /// 用一条缓存线性渐变画"水平高斯 + 垂直高斯"的柔软光压：
    /// 水平形状已固化在 Stop 里（创建 / 调色板变化时写入），垂直方向用 7 条**互不重叠**的连续横条
    /// （各自的 alpha 通过 <c>brush.Opacity</c> 施加）近似，因此不会叠加变亮、也不需要二维模糊，
    /// 帧内**零分配、零 Stop 写入**。
    /// </summary>
    private void DrawGaussianBand(CanvasDrawingSession ds, ImmersiveProgressMetrics m, CanvasLinearGradientBrush brush,
        float center, float sigmaX, float sigmaYScale, float peakOpacity)
    {
        var span = sigmaX * 3f;
        brush.StartPoint = new Vector2(center - span, 0f);
        brush.EndPoint = new Vector2(center + span, 0f);

        var sigmaY = Math.Max(0.5f, (float)m.Thickness / sigmaYScale / 2f);
        var stripeHeight = (float)m.Thickness / BandVerticalStripes;
        for (var s = 0; s < BandVerticalStripes; s++)
        {
            var yLocal = stripeHeight * (s + 0.5f) - (float)m.Thickness / 2f;
            var vg = MathF.Exp(-(yLocal * yLocal) / (2f * sigmaY * sigmaY));
            var alpha = Math.Clamp(peakOpacity * vg, 0f, 1f);
            if (alpha <= 0.002f) continue;

            brush.Opacity = alpha;
            ds.FillRectangle(new Windows.Foundation.Rect(center - span, m.Top + stripeHeight * s,
                span * 2f, stripeHeight + 0.6f), brush);
        }
        brush.Opacity = 1f;
    }

    /// <summary>
    /// ★ R34 Volumetric Head Halo ★
    /// 两层**无硬边界**的椭圆径向光场（Outer / Inner），中心位于 Head **后方** 3~6 DIP，
    /// 让亮度从胶囊内部自然向外扩散。**不画任何 Rim / Stroke / Outline** ——
    /// "Head 的亮"与"Head 的白边"是两件事，本实现只表达前者。
    /// </summary>
    private bool DrawHead(CanvasDrawingSession ds, ImmersiveProgressMetrics m, ImmersiveProgressAnimationState anim,
        ImmersiveEffectsQuality quality)
    {
        var halo = anim.HaloStrength;
        if (halo <= 0.01f || m.ProgressWidth <= 0.5d) return false;
        if (_haloOuterBrush is null || _haloBloomBrush is null || _haloInnerBrush is null) return false;

        var centerY = (float)m.CenterY;
        var qualityScale = quality switch
        {
            ImmersiveEffectsQuality.High => 1.0f,
            ImmersiveEffectsQuality.Balanced => 0.85f,
            _ => 0.65f,
        };

        // ★ 执行书 §9.2（2026-10-05 视觉第二轮）★ 三层，中心逐层前移（越靠内越贴近 Head 圆弧）：
        //   Layer C Cyan Support（最外、最淡、青）→ Layer B Soft Bloom（白青柔光团）
        //   → Layer A Arc-attached Inner Glow（纯白、最亮、最小、最贴弧）。
        //   三层都是径向渐变且外缘精确为 0 ⇒ 光学边界模糊、没有可追踪的轮廓线；
        //   它们**不被胶囊裁剪**（允许极轻 bloom 越出），但形状是椭圆 ⇒ 不会形成矩形块。

        // Layer C：Cyan Support Glow
        var cx = (float)(m.HeadX - ImmersiveProgressParameters.HeadHaloCenterBackOffset);
        var rx = (float)ImmersiveProgressParameters.HeadHaloOuterRadiusX;
        var ry = (float)ImmersiveProgressParameters.HeadHaloOuterRadiusY;
        _haloOuterBrush.Center = new Vector2(cx, centerY);
        _haloOuterBrush.RadiusX = rx;
        _haloOuterBrush.RadiusY = ry;
        _haloOuterBrush.Opacity = Math.Clamp(
            (float)ImmersiveProgressParameters.HeadHaloOuterOpacity * halo * qualityScale, 0f, 1f);
        ds.FillEllipse(new Vector2(cx, centerY), rx, ry, _haloOuterBrush);

        // Layer B：Soft Outer Bloom（白青柔光团，范围中等）
        var bx = (float)(m.HeadX - ImmersiveProgressParameters.HeadHaloBloomBackOffset);
        var brx = (float)ImmersiveProgressParameters.HeadHaloBloomRadiusX;
        var bry = (float)ImmersiveProgressParameters.HeadHaloBloomRadiusY;
        _haloBloomBrush.Center = new Vector2(bx, centerY);
        _haloBloomBrush.RadiusX = brx;
        _haloBloomBrush.RadiusY = bry;
        _haloBloomBrush.Opacity = Math.Clamp(
            (float)ImmersiveProgressParameters.HeadHaloBloomOpacity * halo * qualityScale, 0f, 1f);
        ds.FillEllipse(new Vector2(bx, centerY), brx, bry, _haloBloomBrush);

        // Layer A：Arc-attached Inner Glow（纯白、最亮、最小、最贴近右端圆弧）
        var ax = (float)(m.HeadX - ImmersiveProgressParameters.HeadHaloArcBackOffset);
        var arx = (float)ImmersiveProgressParameters.HeadHaloArcRadiusX;
        var ary = (float)ImmersiveProgressParameters.HeadHaloArcRadiusY;
        _haloInnerBrush.Center = new Vector2(ax, centerY);
        _haloInnerBrush.RadiusX = arx;
        _haloInnerBrush.RadiusY = ary;
        _haloInnerBrush.Opacity = Math.Clamp(
            (float)ImmersiveProgressParameters.HeadHaloArcOpacity * halo * qualityScale, 0f, 1f);
        ds.FillEllipse(new Vector2(ax, centerY), arx, ary, _haloInnerBrush);

        _haloOuterBrush.Opacity = 1f;
        _haloBloomBrush.Opacity = 1f;
        _haloInnerBrush.Opacity = 1f;
        return true;
    }

    private bool DrawParticles(CanvasDrawingSession ds, ImmersiveProgressMetrics m,
        ImmersiveProgressAnimationState anim, ImmersiveEffectsQuality quality, bool reducedMotion, CanvasGeometry? capsule)
    {
        if (reducedMotion) return false;
        var span = anim.Particles.Items;
        if (span.Length == 0) return false;
        if (m.ProgressWidth <= 1d || capsule is null) return false;

        var drawn = false;

        // ★ R31 ★ 粒子与 Ripple 都裁进**圆角胶囊**（过去是矩形 ⇒ 端部会被铺方）
        using (var layer = ds.CreateLayer(1f, capsule))
        {
            for (var i = 0; i < span.Length; i++)
            {
                ref readonly var p = ref span[i];
                if (!p.Alive) continue;
                var fade = p.Fade;
                if (fade <= 0.01f) continue;

                var alpha = Math.Clamp(fade * (0.55f + 0.45f * p.Influence), 0f, 1f);
                var radius = p.DrawRadius;
                if (radius <= 0.1f) continue;

                var x = p.X;
                var y = (float)m.CenterY + p.Y;
                ds.FillCircle(x, y, radius, WithAlpha(_palette.Particle, alpha * 0.85f));
                // 极轻 glow：只在被 Band 照亮时出现，避免"屏幕空间乱飞的光点"
                if (p.Influence > 0.35f)
                {
                    ds.FillCircle(x, y, radius * 2.1f, WithAlpha(_palette.Particle, alpha * 0.10f));
                }
                drawn = true;
            }

            var ripples = anim.Ripples;
            for (var i = 0; i < ripples.Length; i++)
            {
                ref readonly var r = ref ripples[i];
                if (!r.Alive) continue;
                var t = r.MaxLife > 0f ? Math.Clamp(r.Life / r.MaxLife, 0f, 1f) : 1f;
                var radius = (float)(ImmersiveProgressParameters.RippleStartRadius
                    + (ImmersiveProgressParameters.RippleEndRadius - ImmersiveProgressParameters.RippleStartRadius) * t);
                var alpha = (float)ImmersiveProgressParameters.RipplePeakOpacity * (1f - t);
                ds.DrawCircle(r.X, (float)m.CenterY + r.Y, radius, WithAlpha(_palette.Particle, alpha), 1f);
                drawn = true;
            }
        }

        _ = quality;
        return drawn;
    }

    private void EnsureVerticalGradient(CanvasLinearGradientBrush brush, ImmersiveProgressMetrics m)
    {
        brush.StartPoint = new Vector2(0f, (float)m.Top);
        brush.EndPoint = new Vector2(0f, (float)(m.Top + m.Thickness));
    }

    private static Color WithAlpha(Color color, float alpha)
        => Color.FromArgb((byte)Math.Clamp(color.A * Math.Clamp(alpha, 0f, 1f), 0f, 255f), color.R, color.G, color.B);

    private void DisposeResources()
    {
        _trackBrush?.Dispose();
        _chromaBrush?.Dispose();
        _fillLightBrush?.Dispose();
        _bandOuterBrush?.Dispose();
        _bandCoreBrush?.Dispose();
        _haloOuterBrush?.Dispose();
        _haloBloomBrush?.Dispose();
        _haloInnerBrush?.Dispose();
        DisposeCapsuleGeometry();
        _trackBrush = null;
        _chromaBrush = null;
        _fillLightBrush = null;
        _bandOuterBrush = null;
        _bandCoreBrush = null;
        _haloOuterBrush = null;
        _haloBloomBrush = null;
        _haloInnerBrush = null;
    }

    public void Dispose()
    {
        DisposeResources();
        _device = null;
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// ★ Round-3（§11 / §17）★ 一个**已经解析好颜色**的调色板。
/// 颜色一律来自 Theme Resource（控件负责解析），Renderer 只消费本结构 ⇒
/// Renderer 里不可能出现硬编码颜色，也不可能去查业务状态。
/// <para>
/// ★ Round-3 视觉纠偏（R32 / R34）★ 结构变化：
/// <list type="bullet">
///   <item>Fill 由"上下两支固定蓝"改为 <b>Track-Space Chroma Field</b> 的 6 个色停靠点
///     （<c>ChromaStart / ChromaCyanSoft / ChromaCyan / ChromaBlue / ChromaDeep / ChromaElectric</c>）；</item>
///   <item>纵向只留极轻的材质修饰 <c>FillLightTop / FillLightBottom</c>；</item>
///   <item>不再有边框色（R31 删除了外壳描边），Track 也不需要"全宽硬上缘条"；</item>
///   <item><c>HeadHalo</c>（外层青）与 <c>HeadRim</c>（内层白青）都作为**体积光场的颜色**使用，
///     不再作为"描边色"。</item>
/// </list>
/// </para>
/// <para>
/// 位置说明：本记录带 <c>Windows.UI.Color</c>（WinRT 类型），故与 Renderer 同居本文件，
/// 而不放在 <c>ImmersiveProgressParameters.cs</c> —— 后者必须保持纯 <c>System.*</c>，
/// 才能被 net8.0 的测试项目链入，从而对粒子池边界做**真实数字**回归断言。
/// </para>
/// </summary>
internal sealed record ImmersiveProgressPalette(
    Windows.UI.Color TrackTop,
    Windows.UI.Color TrackBottom,
    Windows.UI.Color ChromaStart,
    Windows.UI.Color ChromaCyanSoft,
    Windows.UI.Color ChromaCyan,
    Windows.UI.Color ChromaBlue,
    Windows.UI.Color ChromaDeep,
    Windows.UI.Color ChromaElectric,
    Windows.UI.Color FillLightTop,
    Windows.UI.Color FillLightBottom,
    Windows.UI.Color HeadHalo,
    Windows.UI.Color HeadRim,
    Windows.UI.Color BandOuter,
    Windows.UI.Color BandCore,
    Windows.UI.Color Particle)
{
    /// <summary>
    /// 兜底调色板（Theme Resource 尚未解析时的初值）：与 `Themes\Colors.xaml` 里登记的同名画刷同色。
    /// 存在它的意义是"控件任何时刻都有颜色可画"，而不是让 Renderer 去猜。
    /// </summary>
    public static readonly ImmersiveProgressPalette Fallback = new(
        // Track：中性炭黑/暖黑方向（明显收敛，不再是被用户读成"蓝灰壳"的蓝调）
        TrackTop: Windows.UI.Color.FromArgb(0xFF, 0xD6, 0xE1, 0xF8),
        TrackBottom: Windows.UI.Color.FromArgb(0xFF, 0xD6, 0xE1, 0xF8),
        // R32 Track-Space Chroma Field：左 Aqua/Cyan → 中青蓝 → 后电蓝
        ChromaStart: Windows.UI.Color.FromArgb(0xFF, 0xA7, 0xE1, 0xDF),
        ChromaCyanSoft: Windows.UI.Color.FromArgb(0xFF, 0x83, 0xD5, 0xDC),
        ChromaCyan: Windows.UI.Color.FromArgb(0xFF, 0x55, 0xBF, 0xE5),
        ChromaBlue: Windows.UI.Color.FromArgb(0xFF, 0x35, 0xA2, 0xEF),
        ChromaDeep: Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x83, 0xF7),
        ChromaElectric: Windows.UI.Color.FromArgb(0xFF, 0x25, 0x8E, 0xF8),
        // 纵向修饰：只在顶部加一点亮、底部压一点暗（alpha 极低，靠 FillShadeOpacity 控制）
        FillLightTop: Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
        FillLightBottom: Windows.UI.Color.FromArgb(0x10, 0x00, 0x10, 0x28),
        // R34 体积光场颜色（不是描边色）
        HeadHalo: Windows.UI.Color.FromArgb(0xFF, 0x9B, 0xDF, 0xFF),
        HeadRim: Windows.UI.Color.FromArgb(0xFF, 0xF0, 0xFC, 0xFF),
        BandOuter: Windows.UI.Color.FromArgb(0xFF, 0xB8, 0xD8, 0xFF),
        BandCore: Windows.UI.Color.FromArgb(0xFF, 0xE8, 0xF2, 0xFF),
        Particle: Windows.UI.Color.FromArgb(0xFF, 0xDC, 0xEC, 0xFF));
}
