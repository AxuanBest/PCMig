using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PCMig.WinUI.Presentation;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// WinUI Step 1 视口 / DPI 契约回归（真源码静态扫描 + 换算公式复算）。
///
/// 【为什么存在】
/// 本机只实测过 **125%（120 DPI）** 与 **200%（192 DPI）** 两种缩放率。
/// 而外壳的"Canonical 视口"依赖一条**容易静默走偏**的换算链：
///   1. app.manifest 声明 PerMonitorV2（声明丢失 → 进程不感知 DPI，XAML 被系统位图拉伸，字体发虚）；
///   2. 初始客户区必须按窗口 DPI 换算（API 单位是**物理像素**）；直接写死 1440x900 会在 125% 下
///      把 XAML 可用空间压成约 1139x713 DIP，整屏布局比例被破坏而**编译/单测都不会报**；
///   3. **【2026-10-17 变更】** 初始窗口矩形改为"工作区 × 92%/91% 居中"（见 WindowLayoutPlanner），
///      不再锁 Canonical DIP；旧的首帧自校正已删除，并在
///      <c>Startup_DoesNotLockViewportBackToCanonicalDips</c> 里反转成"必须不存在"的契约。
/// 这几处只要有一处被改坏，表现是"某一档缩放下布局跑偏"，而开发机往往只用一个缩放率，察觉不到。
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

    /// <summary>
    /// 【2026-10-17 变更 · 用户真机截图缺陷】默认窗口尺寸不再由 Canonical DIP 决定。
    ///
    /// 旧口径 `ResizeClient(round(1424×891 DIP × scale))` 与显示器工作区**无关**：在 2880×1800@200%
    /// 的机器上客户区是 2848×1782 **物理像素**，连标题栏后高出工作区 1704 px（底部被裁），
    /// 且从不设位置（右侧溢出）。这正是用户第二张截图。
    ///
    /// 新口径：默认尺寸 = 工作区 × 92% / 91%，居中，且必须完整落在工作区内（<c>WindowLayoutPlanner</c>）。
    /// 本用例用两个正交断言钉住这次换轨：
    ///   · 行为侧——同一工作区上新算法给出的矩形**不等于** Canonical 矩形（相等即说明又退回固定尺寸）；
    ///   · 硬约束侧——结果必须不越界（旧实现的第一条硬伤）。
    /// </summary>
    [Theory]
    [InlineData(1920, 1040)]
    [InlineData(2880, 1704)]
    [InlineData(2560, 1400)]
    public void DefaultWindowSize_IsDerivedFromWorkArea_NotFromCanonicalDips(int workW, int workH)
    {
        var p = WindowLayoutPlanner.Plan(0, 0, workW, workH);

        Assert.True(p.Width != CanonicalWidthDip || p.Height != CanonicalHeightDip,
            $"工作区 {workW}×{workH} 上算出 {p.Width}×{p.Height}，与 Canonical {CanonicalWidthDip}×{CanonicalHeightDip} 相同 —— 说明仍是固定尺寸");
        Assert.True(p.IsInside(0, 0, workW, workH), $"{p} 超出 {workW}×{workH} 工作区");
    }

    /// <summary>
    /// 【2026-10-17 契约反转】旧的"首帧视口自校正"已删除，现在断言它**必须不存在**。
    ///
    /// 那段逻辑的唯一前提是"初始尺寸由 <c>ResizeClient</c> 请求 Canonical 客户区，实测值会多出
    /// 一条标题栏带"。初始布局改为 <c>MoveAndResize</c> 外框矩形后，客户区由边框自然决定，
    /// 不再存在"请求值 vs 实测值差一个常量"这回事；若保留自校正，它会在启动后把窗口又拉回
    /// 1424×891 DIP（200% 下 = 2848×1782 px），把本轮修复原地撤销。
    /// </summary>
    [Fact]
    public void Startup_DoesNotLockViewportBackToCanonicalDips()
    {
        var code = ReadWinUi("MainWindow.xaml.cs");

        Assert.DoesNotContain("_viewportCalibrated", code);
        Assert.DoesNotMatch(new Regex(@"Math\.Abs\(offsetWidthDip\)"), code);
        Assert.DoesNotMatch(new Regex(@"Math\.Abs\(offsetHeightDip\)"), code);

        // 初始布局必须走规划器 + MoveAndResize（一个 API 同时定尺寸与位置）
        Assert.Contains("WindowLayoutPlanner.Plan(", code);
        Assert.Contains("AppWindow.MoveAndResize(", code);
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
        // ★ 2026-10-08 真机复验（返修 R3）★ 该组**不再使用固定宽度**：
        //   真机截图证明固定宽（54/66）会把 9~10 DIP 的状态圆点裁成非圆，且 100%/125%/150% DPI 都可能复现。
        //   现行口径：外层 Auto 宽（由底栏 Auto 列自适应）+ 12×12 定尺寸容器居中放圆点 ⇒
        //   "独立 x:Name + 不因标签宽度挤压"这条不变量保留，但**不再把错误尺寸固化成契约**。
        Assert.Contains(@"x:Name=""FooterNetPanel"" Grid.Column=""7""", xaml);          // 源连接组独立存在
        Assert.DoesNotContain(@"x:Name=""FooterNetPanel"" Grid.Column=""7"" Width=", xaml); // 且不再固定宽
        Assert.Contains(@"<Grid MinWidth=""12"" Width=""12"" Height=""12"" VerticalAlignment=""Center"">", xaml); // 圆点容器居中且不可缩
        Assert.Contains(@"VerticalAlignment=""Center"" MinWidth=""64""", xaml);  // 整组保底宽：Auto 列不得把圆点压扁
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

    /// <summary>
    /// 剥掉注释，只留代码 —— 供"不得出现某类型名"这类断言使用。
    ///
    /// 【实现已统一到 <see cref="SourceStripper"/> —— 不要在本文件里另写一份】
    /// 这里曾经是每个测试文件各写一份的块注释正则，而块注释正则会**失控**：
    /// 文档注释里出现字面量 `/*` 极其常见（例如「禁止定义任何 *Orchestrator/*Runner 类型」
    /// 这句注释里就有 `/*Runner`），正则把它当成块注释开头后会一路吃到下一个 `*/`，
    /// 把中间的**全部可执行代码**静默删掉。实测事故：`MigrationSessionViewModel.cs`
    /// 原文 240,758 B 被吞到只剩 90,929 B（丢 62.2%），而本文件的禁词断言当时之所以"通过"，
    /// 正是因为被吞掉的那 107 KB 里含有它要找的名字 —— 断言看着在守，其实全瞎。
    /// 现在统一走状态机实现，并有 <see cref="R2SourceStripperTests"/> 直接盯着它。
    /// </summary>
    private static string StripComments(string text) => SourceStripper.StripComments(text);

    /// <summary>
    /// 单块注释允许的最大跨度（字符）。给人看的注释不会横跨几万字符；
    /// 超过这条线就意味着匹配失控 —— 历史事故里一次"注释"吞掉了 107,081 字符的代码。
    /// </summary>
    private const int MaxBlockCommentChars = 4000;

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