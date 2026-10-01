using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PCMig.WinUI.Views;

/// <summary>
/// A33（20260928）：全应用统一方向性受光表面。
///
/// 背景（用户两轮人工标注）：
///   「Step Card 没有做出和全局一样的浮雕效果，就是高光和阴影里的一致性光源」；
///   「边缘白光太粗……一圈明显的白色描边……四条边都被框出来……
///     我要的是整个应用存在一个统一光源、来自应用整体左上角……
///     高光主要出现在左上边缘和左上表面，越往右下越弱，右下体现轻微阴影/暗部收边……
///     高光应该更像 Surface Highlight，而不是 Border Stroke」，
///   并要求「其他卡片和板块也有这样的效果，要全局统一」。
///
/// ★ 机制根因（全局性，不是某一处）：全应用此前把"高光"写成 **BorderBrush**
///   （EdgeHighlightCrispBrush / EdgeHighlightDiagonalBrush / EdgeHighlightSoftDiagonalBrush），
///   而 BorderBrush 会沿**整圈四条边**描一遍 —— 左右竖边因此也各自拿到一笔高光，
///   视觉上必然是"每张卡片自己四周发白 / 一圈粗白边"。
///   本控件把高光移到**表面叠加层**，四边不再等强：左上亮、右下暗，方向全应用一致。
///
/// 本控件只负责**外观**：填充 / 极轻轮廓 / 圆角 / 阴影 / 方向性受光。
/// 不含任何业务状态与事件；内部只有一个内容呈现器，行为与原来的 Border 等价。
/// </summary>
public sealed partial class PCMigSurface : UserControl
{
    public PCMigSurface()
    {
        InitializeComponent();
        Refresh();
    }

    /// <summary>受光表面的种类：决定圆角 / 填充 / 阴影。与既有 Surface 样式一一对应。</summary>
    public enum SurfaceKind
    {
        /// <summary>外壳（原 ShellMaterial）：22 圆角、Shell 填充、ElevationLow。</summary>
        Shell,
        /// <summary>大面板（原 PrimarySurface）：20 圆角、Primary 填充、ElevationHigh。</summary>
        Panel,
        /// <summary>普通卡（原 SecondarySurface）：16 圆角、Card 填充、ElevationLow。提示卡 / 共享卡用这档。</summary>
        Card,
        /// <summary>主内容卡（原 ElevatedSurface）：18 圆角、Elevated 填充、ElevationMedium。</summary>
        Elevated,
    }

    // ── 依赖属性 ────────────────────────────────────────────────────────────────

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(SurfaceKind), typeof(PCMigSurface),
        new PropertyMetadata(SurfaceKind.Card, OnChanged));

    public SurfaceKind Kind
    {
        get => (SurfaceKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>内容内边距。迁移时必须与原来 Border 的 Padding 一致，保证版式不变。</summary>
    public static readonly new DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding), typeof(Thickness), typeof(PCMigSurface),
        new PropertyMetadata(new Thickness(16), OnChanged));

    public new Thickness Padding
    {
        get => (Thickness)GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    /// <summary>卡片内容。</summary>
    public static readonly new DependencyProperty ContentProperty = DependencyProperty.Register(
        nameof(Content), typeof(object), typeof(PCMigSurface), new PropertyMetadata(null));

    public new object? Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    // ── 由 Kind 派生的视觉属性（供 x:Bind OneWay 使用）──────────────────────────

    public Brush? SurfaceFill => Res(FillKey);
    public Brush? SurfaceContour => Res("PCMigContourSoftBrush");
    public CornerRadius SurfaceCorner => Kind switch
    {
        SurfaceKind.Shell => new CornerRadius(22),
        SurfaceKind.Panel => new CornerRadius(20),
        SurfaceKind.Elevated => new CornerRadius(18),
        _ => new CornerRadius(16),
    };

    /// <summary>阴影对象（ThemeShadow 派生自 Shadow，不是 Brush，必须单独解析）。</summary>
    public Shadow? SurfaceShadow => ResObject(ShadowKey) as Shadow;

    /// <summary>Z 位移 —— ThemeShadow 必须配 Translation Z 才真的投影。</summary>
    public System.Numerics.Vector3 SurfaceTranslation => Kind switch
    {
        SurfaceKind.Shell => new System.Numerics.Vector3(0, 0, 4),
        SurfaceKind.Panel => new System.Numerics.Vector3(0, 0, 12),
        SurfaceKind.Elevated => new System.Numerics.Vector3(0, 0, 8),
        _ => new System.Numerics.Vector3(0, 0, 6),
    };

    // 上缘亮线：只占卡宽左侧 45%，向右迅速消失 → 不会读成"绕一圈的白描边"
    public double TopEdgeLength => 160;
    public Thickness TopEdgeInset => new(SurfaceCorner.TopLeft, 0, 0, 0);

    // 右下暗边：只覆盖右下约 96 DIP 一段，方向明确（右下收阴），不是整条边等暗
    public double ShadeLength => 96;

    private string FillKey => Kind switch
    {
        SurfaceKind.Shell => "ShellMaterialBrush",
        SurfaceKind.Panel => "PrimarySurfaceBrush",
        SurfaceKind.Elevated => "ElevatedSurfaceBrush",
        _ => "SecondarySurfaceBrush",
    };

    private string ShadowKey => Kind switch
    {
        SurfaceKind.Panel => "ElevationHigh",
        SurfaceKind.Elevated => "ElevationMedium",
        _ => "ElevationLow",
    };

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PCMigSurface s) s.Refresh();
    }

    /// <summary>让 OneWay 的 x:Bind 重新求值（Kind 变化后刷新派生视觉属性）。</summary>
    private void Refresh()
    {
        if (Host is null) return;
        Bindings.Update();
    }

    private static Brush? Res(string key) => ResObject(key) as Brush;

    private static object? ResObject(string key) =>
        Application.Current?.Resources is { } r && r.TryGetValue(key, out var v) ? v : null;
}