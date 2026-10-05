using System;
using System.IO;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ Round-3 视觉纠偏（PMML-R31 / R32 / R33 / R34）★ 视觉契约测试。
///
/// 为什么是"源码文本契约"而不是像素回归：本轮执行书明确要求**不要把自动化视觉验证当 Gate** ——
/// 用户亲自做最终视觉验收，自动化只负责证明"没有破坏业务真实性与稳定性"以及"设计约束没有被改回去"。
/// 因此这些测试锁的是**结构不变量**（几何来源、色场锚定、变体同源、禁止硬编码），
/// 它们是防止后续会话把用户已否定的写法（固定蓝条 / 外描边 / 矩形裁剪 / 白描边 Head）改回去的护栏。
///
/// 它们不与任何业务真值耦合：改 Value / Receipt / Verifier 不会让这些测试变绿或变红。
/// </summary>
public sealed class ImmersiveCapsuleIntegrityTests
{
    private static string ReadRepoFile(params string[] parts)
    {
        var root = FindRepoRoot();
        var path = Path.Combine(new[] { root }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"缺少文件：{path}");
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
        throw new InvalidOperationException("找不到仓库根（PCMig.sln 所在目录）");
    }

    private static string Renderer() =>
        ReadRepoFile("src", "PCMig.WinUI", "Controls", "ImmersiveProgress", "ImmersiveTransferProgressRenderer.cs");

    private static string Parameters() =>
        ReadRepoFile("src", "PCMig.WinUI", "Controls", "ImmersiveProgress", "ImmersiveProgressParameters.cs");

    // ── R31-①：动态层必须裁进**圆角胶囊**，不得再用矩形裁剪 ─────────────────────
    [Fact]
    public void DynamicLayersAreClippedToRoundedCapsuleNotPlainRect()
    {
        var code = Renderer();

        // 必须存在真胶囊几何（RoundedRectangle），且它被用作图层裁剪
        Assert.Contains("CanvasGeometry.CreateRoundedRectangle", code);
        Assert.Contains("EnsureFillCapsuleGeometry", code);
        Assert.Contains("ds.CreateLayer(1f, capsule)", code);

        // ★ 关键回归：旧写法是把 PushBand / Particles 裁到 Rect(0, Top, ProgressWidth, Thickness) ——
        //   那是矩形，会把圆角端部重新铺成平切/方切（用户看到的"直角色块"）。
        Assert.DoesNotContain("var clip = new Windows.Foundation.Rect(0d, m.Top, m.ProgressWidth, m.Thickness);", code);
        Assert.DoesNotContain("using var layer = ds.CreateLayer(1f, clip);", code);
    }

    // ── R31-②：不允许"外面套壳"—— 生产路径不得再画全轨道外描边，也不得有硬 TrackEdge 条 ──
    [Fact]
    public void ProductionRendererHasNoOuterShellBorderOrHardTrackEdgeStrip()
    {
        var code = Renderer();

        Assert.DoesNotContain("DrawBorder(", code);
        Assert.DoesNotContain("TrackEdge", code);
        Assert.DoesNotContain("DrawRoundedRectangle(m.TrackRect", code);

        // LastLayers 必须显式报告 border=0（诊断上可验证"没有外壳层"）
        Assert.Contains("border=0", code);
    }

    // ── R31-③：胶囊几何半径 = 厚度/2；低进度时半径收到 ProgressWidth/2 ──────────
    [Fact]
    public void CapsuleRadiusIsHalfThicknessAndShrinksAtLowProgress()
    {
        var code = Renderer();
        // FillRadius = min(Radius, ProgressWidth/2)
        Assert.Contains("Math.Min(Radius, Math.Max(0d, ProgressWidth / 2d))", code);

        var p = Parameters();
        // Hero：Radius = Thickness/2（6 = 12/2）
        Assert.Contains("public const double Thickness = 12d;", p);
        Assert.Contains("public const double Radius = 6d;", p);
        // Compact：同样是 Thickness/2（5 = 10/2）—— 两个变体都是真胶囊
        Assert.Contains("public const double CompactThickness = 10d;", p);
        Assert.Contains("public const double CompactRadius = 5d;", p);
    }

    // ── R31-④：几何缓存（不许每帧 new Geometry，也不许留错几何）─────────────────
    [Fact]
    public void CapsuleGeometryIsCachedWithARebuildThreshold()
    {
        var code = Renderer();
        Assert.Contains("_fillCapsuleGeometry", code);
        Assert.Contains("GeometryRebuildThreshold", code);
        Assert.Contains("DisposeCapsuleGeometry", code);

        var p = Parameters();
        Assert.Contains("public const double GeometryRebuildThreshold = 0.5d;", p);
    }

    // ── R33：底栏不得再使用旧矩形进度路线 ──────────────────────────────────────
    [Fact]
    public void FooterNoLongerUsesLegacyRectangleProgressRoute()
    {
        var footer = ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml");
        Assert.DoesNotContain("x:Name=\"FooterProgressFill\"", footer);
        Assert.DoesNotContain("RadiusX=\"4\"", footer);
        Assert.Contains("x:Name=\"FooterImmersiveProgress\"", footer);
        Assert.Contains("Variant=\"Compact\"", footer);
    }
}

/// <summary>
/// ★ R32（Progress-Space Chroma Field）+ R34（Volumetric Head Halo）★ 色彩与 Head 契约。
/// </summary>
public sealed class ImmersiveChromaContractTests
{
    private static string ReadRepoFile(params string[] parts)
    {
        var root = FindRepoRoot();
        var path = Path.Combine(new[] { root }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"缺少文件：{path}");
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
        throw new InvalidOperationException("找不到仓库根（PCMig.sln 所在目录）");
    }

    private static string Renderer() =>
        ReadRepoFile("src", "PCMig.WinUI", "Controls", "ImmersiveProgress", "ImmersiveTransferProgressRenderer.cs");

    // ── R32-①：Fill 必须是**锚定整个 Track 宽度**的水平色场，而不是进度宽度 ────────
    [Fact]
    public void ChromaFieldIsAnchoredToTrackWidthNotProgressWidth()
    {
        var code = Renderer();

        // 必须存在 7 个色停靠点的形状函数
        Assert.Contains("ChromaStops", code);
        Assert.Contains("ChromaStopCount = 7", code);

        // ★ 关键：EndPoint = Track 宽度（m.Width），不是进度宽度 —— 进度只"逐渐揭示"色场
        Assert.Contains("_chromaBrush.EndPoint = new Vector2((float)m.Width, (float)m.CenterY);", code);
        Assert.DoesNotContain("_chromaBrush.EndPoint = new Vector2((float)m.ProgressWidth", code);
    }

    // ── R32-②：不得退化成"固定纯蓝纵向条"（旧的 FillTop/FillBottom 两支蓝）────────
    [Fact]
    public void FillNoLongerDegeneratesToFixedBlueVerticalGradient()
    {
        var code = Renderer();
        var colors = ReadRepoFile("src", "PCMig.WinUI", "Themes", "Colors.xaml");

        // 旧 token 必须已被 Chroma 家族取代
        Assert.DoesNotContain("ImmersiveProgressFillTopBrush", colors);
        Assert.DoesNotContain("ImmersiveProgressFillBottomBrush", colors);
        Assert.DoesNotContain("#FF5AA3FF", colors);
        Assert.DoesNotContain("#FF1677F2", colors);

        // 新的 Chroma 家族必须在 Theme Resource 里登记
        Assert.Contains("ImmersiveProgressChromaStartBrush", colors);
        Assert.Contains("ImmersiveProgressChromaCyanBrush", colors);
        Assert.Contains("ImmersiveProgressChromaBlueBrush", colors);
        Assert.Contains("ImmersiveProgressChromaDeepBrush", colors);
        Assert.Contains("ImmersiveProgressChromaElectricBrush", colors);

        // 色场语义：左 Aqua/Cyan，后电蓝（reference video 方向值）
        Assert.Contains("#FFA7E1DF", colors);   // light aqua
        Assert.Contains("#FF55BFE5", colors);   // cyan
        Assert.Contains("#FF1F83F7", colors);   // deep blue
        Assert.Contains("#FF258EF8", colors);   // electric
    }

    // ── R32-③：禁止时间驱动的色相循环 / 彩虹化 ──────────────────────────────────
    [Fact]
    public void NoTimeDrivenHueCyclingAnywhere()
    {
        var code = Renderer();
        // 色相旋转类 API 一律不得出现（本断言只匹配真实调用形态，不匹配注释文字）
        Assert.DoesNotContain("HueToRgb", code);
        Assert.DoesNotContain("FromHsv", code);
        Assert.DoesNotContain("FromHsl", code);
        Assert.DoesNotContain("ColorFromHsv", code);

        // 色场只能由调色板（Theme Resource）决定
        Assert.Contains("ImmersiveProgressPalette", code);
    }

    // ── R34：Head 只能是体积光场，不能是可辨识的 Stroke / Rim / Outline ──────────
    [Fact]
    public void HeadGlowIsVolumetricAndHasNoTraceableRim()
    {
        var code = Renderer();

        // 必须是两层径向（椭圆）光场
        Assert.Contains("CanvasRadialGradientBrush", code);
        Assert.Contains("_haloOuterBrush", code);
        Assert.Contains("_haloInnerBrush", code);
        Assert.Contains("ds.FillEllipse", code);

        // ★ 关键回归：旧版那圈可辨识的白色锐光弧必须彻底不在 —— 断言按**元素用法**精确匹配，
        //   避免命中注释里对该类名/常量的文字提及（先例：Assert.DoesNotContain 会被自己的注释命中）。
        Assert.DoesNotContain("ds.DrawGeometry(", code);
        Assert.DoesNotContain("_headRimArc.", code);
        Assert.DoesNotContain("_headRimArc is not null", code);
        Assert.DoesNotContain("ImmersiveProgressParameters.HeadRimThickness", code);

        // 光场剖面必须两端为 0（无硬边界）
        Assert.Contains("HaloRadialStops", code);
        Assert.Contains("Position = 1.00f, Color = WithAlpha(color, 0.00f)", code);

        // 光场中心必须在 Head **后方**
        var p = ReadRepoFile("src", "PCMig.WinUI", "Controls", "ImmersiveProgress", "ImmersiveProgressParameters.cs");
        Assert.Contains("HeadHaloCenterBackOffset", p);
        Assert.Contains("m.HeadX - ImmersiveProgressParameters.HeadHaloCenterBackOffset", code);
    }
}

/// <summary>
/// ★ R33（Hero / Compact Material Family）★ 变体契约：同控件、同源、同语义。
/// </summary>
public sealed class ImmersiveVariantContractTests
{
    private static string ReadRepoFile(params string[] parts)
    {
        var root = FindRepoRoot();
        var path = Path.Combine(new[] { root }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"缺少文件：{path}");
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
        throw new InvalidOperationException("找不到仓库根（PCMig.sln 所在目录）");
    }

    private static string Parameters() =>
        ReadRepoFile("src", "PCMig.WinUI", "Controls", "ImmersiveProgress", "ImmersiveProgressParameters.cs");

    // ── 变体枚举与几何 token 必须存在且自洽 ────────────────────────────────────
    [Fact]
    public void VariantEnumAndGeometryTokensExist()
    {
        var p = Parameters();
        Assert.Contains("public enum ImmersiveProgressVariant", p);
        Assert.Contains("Hero = 0", p);
        Assert.Contains("Compact = 1", p);

        // 变体解析函数（HostHeightFor / ThicknessFor / RadiusFor）
        Assert.Contains("public static double HostHeightFor(ImmersiveProgressVariant variant)", p);
        Assert.Contains("public static double ThicknessFor(ImmersiveProgressVariant variant)", p);
        Assert.Contains("public static double RadiusFor(ImmersiveProgressVariant variant)", p);
    }

    // ── Hero 与 Compact 必须是**同一个控件类型** ───────────────────────────────
    [Fact]
    public void HeroAndCompactUseTheSameControlType()
    {
        var main = ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml");
        var step3 = ReadRepoFile("src", "PCMig.WinUI", "Views", "Step3ProgressPage.xaml");

        Assert.Contains("<controls:ImmersiveTransferProgress x:Name=\"FooterImmersiveProgress\" Variant=\"Compact\"", main);
        Assert.Contains("<controls:ImmersiveTransferProgress x:Name=\"TotalImmersiveProgress\"", step3);

        // 底栏不得再有自己的像素宽度写入者
        Assert.DoesNotContain("FooterProgressFill", main);
    }

    // ── 同源：两条都只接受 Value（VisualProgress），不得各读一份真值 ─────────────
    [Fact]
    public void BothVariantsShareTheSingleVisualProgressSource()
    {
        var code = ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml.cs");

        // 底栏写入者是 FooterImmersiveProgress.Value = clamped（同一个 _footerProgressPercent，
        // 而后者由 RefreshFooterPresentation 从呈现协调器的 VisualProgress 推进）
        Assert.Contains("FooterImmersiveProgress.Value = clamped;", code);
        Assert.Contains("FooterImmersiveProgress.ProgressState = MapFooterProgressState(", code);

        // 底栏不得再直接写宽度或读 raw 真值
        Assert.DoesNotContain("_footerMotion", code);
        Assert.DoesNotContain("FooterProgressFill.Width", code);
    }

    // ── Compact 关闭粒子与 Ripple，但仍保留 Capsule / Chroma / Head ────────────
    [Fact]
    public void CompactDisablesParticlesAndRipplesButKeepsCapsuleChromaAndHead()
    {
        var p = Parameters();
        Assert.Contains("CompactParticlesEnabled = false", p);
        Assert.Contains("CompactRipplesEnabled = false", p);
        Assert.Contains("CompactBandScale", p);

        // 控件在某变体下必须能关闭装饰（VariantParticlesEnabled / VariantRipplesEnabled）
        var xamlCs = ReadRepoFile("src", "PCMig.WinUI", "Controls", "ImmersiveProgress", "ImmersiveTransferProgress.xaml.cs");
        Assert.Contains("VariantParticlesEnabled", xamlCs);
        Assert.Contains("VariantRipplesEnabled", xamlCs);
    }

    // ── 旧动效路线已彻底退出生产（ProgressMotionDriver 无消费者）────────────────
    [Fact]
    public void LegacyMotionDriverHasNoProductionConsumers()
    {
        var step3 = ReadRepoFile("src", "PCMig.WinUI", "Views", "Step3ProgressPage.xaml.cs");
        var main = ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml.cs");

        Assert.DoesNotContain("ProgressMotionDriver", step3);
        Assert.DoesNotContain("ProgressMotionDriver", main);
    }
}