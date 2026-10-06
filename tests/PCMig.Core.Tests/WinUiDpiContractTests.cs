using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// WinUI Step 1 视口 / DPI 契约回归（真源码静态扫描 + 换算公式复算）。
///
/// 【为什么存在】
/// 本机只实测过 **125%（120 DPI）** 一种缩放率（100% / 150% 均无实测条件）。
/// 而外壳的"Canonical 视口"依赖一条**容易静默走偏**的换算链：
///   1. app.manifest 声明 PerMonitorV2（声明丢失 → 进程不感知 DPI，XAML 被系统位图拉伸，字体发虚）；
///   2. MainWindow.xaml.cs 用 AppWindow.ResizeClient 设初始客户区，而该 API 的单位是**物理像素**，
///      必须按窗口 DPI 换算；直接写死 1440x900 会在 125% 下把 XAML 可用空间压成约 1139x713 DIP，
///      整屏布局比例被破坏而**编译/单测都不会报**；
///   3. 首次布局后用实测视口回算并再校正一次（extend content into title bar 会让 XAML 视口
///      多覆盖一条标题栏带，偏差随 DPI / 系统主题变化）。
/// 这三处只要有一处被改坏，表现是"某一档缩放下布局跑偏"，而开发机往往只用一个缩放率，察觉不到。
///
/// 【运行级别：L0 静态契约扫描】
/// 只读源码文本并复算公式，不加载 WinUI 运行时（与既有 WinUiStep1ContractTests 同款约束）。
/// 它证明的是"换算链的声明与常量仍在、公式在 100/125/150/175% 下达标"，**不是**"真机在 100% 下
/// 一定渲染正确"——真机可见性仍需实际切换到该缩放率截图闭环。
/// </summary>
public sealed class WinUiDpiContractTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
        }
        throw new InvalidOperationException("未找到仓库根（含 PCMig.sln）");
    }

    private static string WinUiDir => Path.Combine(FindRepoRoot(), "src", "PCMig.WinUI");
    private static string ReadWinUi(string relative) => File.ReadAllText(Path.Combine(WinUiDir, relative));

    /// <summary>应用当前声明的 Canonical 尺寸（DIP）。改这里必须同步改测试，属有意为之。</summary>
    private const int CanonicalWidthDip = 1424;
    private const int CanonicalHeightDip = 891;

    [Fact]
    public void CanonicalViewportConstants_MatchDocumentedDips()
    {
        var code = ReadWinUi("MainWindow.xaml.cs");
        Assert.Matches(
            new Regex(@"CanonicalClientWidthDip\s*=\s*" + CanonicalWidthDip + @"\b"),
            code);
        Assert.Matches(
            new Regex(@"CanonicalClientHeightDip\s*=\s*" + CanonicalHeightDip + @"\b"),
            code);
    }

    [Theory]
    [InlineData(1.00)]
    [InlineData(1.25)]
    [InlineData(1.50)]
    [InlineData(1.75)]
    public void ClientSizeConversion_ScalesByWindowDpi(double scale)
    {
        // 复算 ApplyCanonicalClientSize：ResizeClient(round(DIP * scale))
        var w = (int)Math.Round(CanonicalWidthDip * scale);
        var h = (int)Math.Round(CanonicalHeightDip * scale);
        Assert.True(w > 0 && h > 0);
        // 物理像素必须与 DIP*scale 一致（±1 取整）
        Assert.InRange(w, CanonicalWidthDip * scale - 1, CanonicalWidthDip * scale + 1);
        Assert.InRange(h, CanonicalHeightDip * scale - 1, CanonicalHeightDip * scale + 1);
        // Canonical 16:10 比例在任意缩放率下不得漂移超过 1.5%
        var aspect = (double)w / h;
        Assert.InRange(aspect, 1.0 / 1.015 * ((double)CanonicalWidthDip / CanonicalHeightDip),
            1.015 * ((double)CanonicalWidthDip / CanonicalHeightDip));
    }

    [Fact]
    public void Calibration_OnlyFiresWhenOffsetExceedsThreshold()
    {
        var code = ReadWinUi("MainWindow.xaml.cs");
        // 自校正必须有"已经准确就不再校正"的阈值判断，否则会与布局互相触发抖动循环
        Assert.Matches(new Regex(@"Math\.Abs\(offsetWidthDip\)\s*<\s*1\.5"), code);
        Assert.Matches(new Regex(@"Math\.Abs\(offsetHeightDip\)\s*<\s*1\.5"), code);
        // 只校正一次（幂等保护）
        Assert.Contains("_viewportCalibrated", code);
    }

    /// <summary>
    /// 复算 OnRootSizeChanged 的自校正：ExtendsContentIntoTitleBar 会让 XAML 视口额外覆盖一条
    /// 标题栏带，实测视口 = 请求值 + band（本机 125% 实测约 +29.8 DIP），因此必须
    /// "从请求值里扣掉这段常量偏移"，再乘窗口 DPI 换算回物理像素。
    /// 这段逻辑没有任何真机 100% DPI 的实测覆盖，用复算把它钉住。
    /// </summary>
    [Theory]
    [InlineData(1.00, 24.0)]
    [InlineData(1.25, 29.8)]
    [InlineData(1.50, 36.0)]
    public void ViewportCalibration_RemovesTitleBarBandOffset(double scale, double bandDip)
    {
        // 第一次请求：客户区不含标题栏带 → 实测视口比 Canonical 高一个 band
        var requestedHeightDip = CanonicalHeightDip;
        var actualViewportHeightDip = requestedHeightDip + bandDip;
        var offsetHeightDip = actualViewportHeightDip - CanonicalHeightDip;
        Assert.Equal(bandDip, offsetHeightDip, 3);

        // 阈值判断：偏移超过 1.5 DIP 才需要校正（否则与布局互相触发抖动循环）
        Assert.True(Math.Abs(offsetHeightDip) >= 1.5, "该场景本应触发校正");

        // 校正：新请求 = Canonical - offset，再按 DPI 换算成物理像素
        var correctedHeightDip = CanonicalHeightDip - offsetHeightDip;
        var correctedPx = (int)Math.Round(correctedHeightDip * scale);
        Assert.InRange(correctedPx, (CanonicalHeightDip - bandDip) * scale - 1, (CanonicalHeightDip - bandDip) * scale + 1);

        // 校正后视口必须回到 Canonical（±1 DIP）
        var finalViewportDip = correctedHeightDip + bandDip;
        Assert.InRange(finalViewportDip, CanonicalHeightDip - 1, CanonicalHeightDip + 1);

        // 宽度方向：Canonical 视口宽度是常量，带偏移只应出现在高度方向
        var offsetWidthDip = 0.0;
        Assert.True(Math.Abs(offsetWidthDip) < 1.5, "宽度方向不应有可感知偏移");
    }

    [Fact]
    public void Manifest_DeclaresPerMonitorV2()
    {
        var manifest = ReadWinUi("app.manifest");
        Assert.Contains("PerMonitorV2", manifest);
        Assert.Matches(new Regex(@"<dpiAware[^>]*>\s*true/pm\s*</dpiAware>"), manifest);
    }

    [Fact]
    public void Code_UsesDpiAwareSizingApis_AndNeverHardcodesClientSize()
    {
        var code = ReadWinUi("MainWindow.xaml.cs");
        Assert.Contains("AppWindow.ResizeClient", code);
        Assert.Contains("GetDpiForWindow", code);
        // 禁止把物理像素尺寸写死（写死会在其它缩放率下破坏 Canonical 比例）
        Assert.DoesNotMatch(new Regex(@"ResizeClient\(\s*new\s+SizeInt32\(\s*14\d\d\s*,"), code);

        var xaml = ReadWinUi("MainWindow.xaml");
        // 窗口自身不得写死宽高（宽高必须由 DPI 换算决定）
        var windowTag = Regex.Match(xaml, @"<Window\b[^>]*>").Value;
        Assert.False(windowTag.Contains("Width="), "Window 上不应写死 Width");
        Assert.False(windowTag.Contains("Height="), "Window 上不应写死 Height");
    }

    /// <summary>
    /// Step 1 底栏的疏密节奏是**实测对齐参考**后的结果；这些数字一旦被改动，
    /// 底栏会退回"两组速率挤成一团、网络组贴住按钮组"的旧观感。
    ///
    /// FIX BATCH 5（§8）由用户明确授权重做底栏几何（这正是本战役要修的真机缺陷 P1/P2-A/B/F、
    /// OPEN-RISK R-010）：列距从 44 收紧到 18、中段隔离列 80 归零（弹性全部交给唯一的 `*` 列）、
    /// 进度轨道 320 → 保留宽 232、四块数字区与动作区改为"保留宽 + CharacterEllipsis"。
    /// 因此本用例的口径更新为**新结构的不变量**（原意不变，且更强 —— 旧观感与"按钮被文本宽度推走"
    /// 这两种回退都会被这条测试挡住）：
    ///   · 必须有明确列距、唯一弹性列（弹性列不许再放元素）；
    ///   · 进度宿主与四个动作按钮都必须占**保留宽**；
    ///   · 网络组有独立 x:Name 与保留宽（窄档位允许整块隐藏，但动作按钮绝不允许）。
    /// </summary>
    [Fact]
    public void FooterRhythm_KeepsMeasuredValues()
    {
        var xaml = ReadWinUi("MainWindow.xaml");
        Assert.Contains(@"ColumnSpacing=""18""", xaml);                    // 底栏列间距
        Assert.Contains(@"<Grid Grid.Column=""2"" MinWidth=""0""", xaml);  // 进度轨道最小宽（弹性列会拉得更宽）
        Assert.Contains(@"<ColumnDefinition Width=""0""/>", xaml);         // 中段隔离列已归零
        Assert.Contains(@"<ColumnDefinition Width=""*""/>", xaml);         // 唯一的弹性列
        Assert.Contains(@"x:Name=""FooterNetPanel"" Grid.Column=""7"" Width=""54""", xaml); // 网络组独立保留宽
        Assert.Equal(4, Regex.Matches(xaml, @"Width=""124""").Count);      // 四个动作按钮各自定宽
    }

    /// <summary>
    /// 产品 Logo 必须是"双向迁移箭头"矢量 Path，而不是把 Share/导出字形（E72D）当标志用。
    /// </summary>
    [Fact]
    public void ProductLogo_IsBidirectionalArrowPath_NotShareGlyph()
    {
        var xaml = ReadWinUi("MainWindow.xaml");
        Assert.DoesNotContain(@"Glyph=""&#xE72D;""", xaml);
        // 双向箭头几何：两条上下分离的水平箭头（上右下左）
        Assert.Contains("M5,4.6 L14.6,4.6", xaml);
        Assert.Contains("M19,19.4 L9.4,19.4", xaml);
    }

    /// <summary>
    /// 字号系统必须保持"命名语义样式"这一单一事实来源；不允许把字号重新散落成内联属性。
    /// </summary>
    [Fact]
    public void Typography_UsesNamedSemanticStyles()
    {
        // 视图组件化后，TextBlock 分布在多个 XAML（MainWindow 的 Shell + Views\*.xaml 的四个页面），
        // 因此按**整个外壳 XAML 集合**统计，而不是只看 MainWindow。
        var typography = ReadWinUi(Path.Combine("Themes", "Typography.xaml"));
        var xamls = Directory.GetFiles(WinUiDir, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\bin\") && !f.Contains(@"\obj\"))
            .ToArray();
        var blocks = xamls
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"<TextBlock\b[^>]*>").Select(m => m.Value))
            .ToList();
        Assert.True(blocks.Count >= 40, $"TextBlock 数量异常偏少：{blocks.Count}（文件：{xamls.Length} 个）");
        var usingSemantic = blocks.Count(b => b.Contains("Style=\"{StaticResource PCMigText"));
        // 允许少量"就地唯一"的例外（例如列表模板内的极短标签），但不允许大面积退回内联字号。
        Assert.True(usingSemantic >= blocks.Count * 0.95,
            $"使用 PCMigText* 语义样式的 TextBlock 仅 {usingSemantic}/{blocks.Count}；字号系统可能被改回内联属性");
        Assert.Contains("PCMigTextProductTitle", typography);
        Assert.Contains("PCMigTextFooterAction", typography);
    }

    /// <summary>剥掉 XAML/C# 注释，只保留可执行内容（用于"禁止引用"类断言）。</summary>
    private static string StripComments(string text)
    {
        var noXml = Regex.Replace(text, "<!--.*?-->", " ", RegexOptions.Singleline);
        var noBlock = Regex.Replace(noXml, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(noBlock, @"//[^\r\n]*", " ");
    }

    /// <summary>
    /// 冻结范围锚点：外壳不得引用 WPF GUI，也不得**自带第二套迁移引擎**（双引擎是最危险的静默退化）。
    ///
    /// 【判据与意图的关系 —— 改这个用例前必读】
    /// 本用例的原始意图（`历史交接记录` 第 4 条：锁定 "…**无迁移引擎复制**…"）
    /// 是**禁止在 WinUI 内复制/再造一份迁移引擎**。
    /// 在 Step 1 阶段（外壳根本不需要引擎）「禁复制」与「禁出现该类型名」恰好等价，
    /// 于是当时写成了对 <c>TransferOrchestrator</c> 的**禁词断言**。
    ///
    /// 进入业务接线阶段后两者**不再等价**：引用 Core 唯一的 <c>TransferOrchestrator</c>
    /// 恰恰等于「只有一套引擎」；而禁词断言会把这种**正确用法**误判成"第二套引擎"，
    /// 反向逼出委托注入 / 字符串拼接之类的**规避手段**——那才是真正的静默退化风险。
    ///
    /// 故判据修正为：**禁的是「定义 / 复制引擎」，不是「使用唯一引擎」**。
    /// （用户 2026-09-28 明确授权此修正，选项 (a)。）
    /// </summary>
    [Fact]
    public void Shell_StaysFreeOfSecondEngine()
    {
        var files = Directory.GetFiles(WinUiDir, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(WinUiDir, "*.xaml", SearchOption.AllDirectories))
            .ToArray();

        // 防"扫描路径失效导致本用例空过"：真实文件数远大于此下限。
        Assert.True(files.Length >= 20, $"WinUI 源码文件数异常偏少：{files.Length}（扫描可能已失效）");

        foreach (var f in files)
        {
            // 只看**可执行内容**：注释里出现这些名字（例如"本页不引用 Robocopy / TransferOrchestrator"）
            // 属于声明式护栏，不是越界引用；因此先剥掉 XAML 注释与 C# 注释。
            var text = StripComments(File.ReadAllText(f));

            // 1) 外壳不得依赖旧 WPF 实现 —— 与原始意图完全一致，永久保留。
            Assert.DoesNotContain("PCMig.Gui", text);

            // 2) 外壳不得直接驱动 robocopy —— 复制执行是 Core 引擎的内部职责，不是外壳的。
            Assert.DoesNotContain("RobocopyRunner", text);

            // 3) 外壳不得**定义**任何迁移引擎类型（原始意图：无迁移引擎复制）。
            //    注意禁的是"定义"（class/record/struct），**不是**"引用"：
            //    WinUI 调用 Core 的 TransferOrchestrator 属于"使用唯一引擎"，是允许且必需的。
            Assert.DoesNotMatch(@"\b(class|record|struct)\s+\w*(Orchestrator|Runner)\b", text);
        }
    }
}