using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// FIX BATCH 5（§8）：底栏布局稳定性 / 进度条视觉 / 对象明细对齐 / 数字排版 的**源码契约**测试（UI-01…UI-10）。
///
/// 为什么用源码契约而不是 UI 自动化：本机没有可编程的 WinUI 视觉断言（WinUI 3 无法在无头环境里量布局），
/// 而这些缺陷的根因**就是 XAML 结构本身**（Auto 列 + 内容驱动按钮宽 + 框架默认 ProgressBar 模板）。
/// 因此这里把"结构不许回退"锁死：谁把 `Width` 换回内容定宽、谁把四按钮改成 Visibility=Collapsed、
/// 谁把 ProgressBar 样式换回默认模板，测试立刻红。
/// 真实像素验收（按钮左边界漂移 ≤1 px / 填充是否占满轨道）另行用截图 + 像素测量完成，证据在
/// E:\PCMigLab\Evidence\Trust-Critical-Recovery\FIX-BATCH-5-UI.md。
/// </summary>
public class Batch5UiLayoutContractTests
{
    // ── UI-01：底栏四块数字区必须是**保留宽**、不换行，且声明值与响应式 token 一致 ──────────
    // PHASE D 口径校正（2026-10-05）：原先这里锁死 Width=48/100 与 TextTrimming=CharacterEllipsis，
    // 但 UI Closure 之后 MainWindow.xaml 的实际值是 68 / 150 / 100 / 112，且百分比列与 ETA 列改成
    // TextTrimming="None"（这两列内容长度固定，例如 "100.0"、"1 分 15 秒"，不会溢出，无需省略号）。
    // 断言因此升级为**更强**的两条：① XAML 的实际宽度必须等于 ResponsiveLayoutController 的 Wide 档
    // 声明值（消除"声明 48 / 渲染 68"这种死代码不一致）；② 数字块必须 NoWrap、必须有预留宽度与
    // 显式 TextTrimming 声明。
    [Theory]
    [InlineData("FooterPercentText", 68, "FooterPercentWidth")]
    [InlineData("FooterBytesText", 150, "FooterBytesWidth")]
    [InlineData("FooterSpeedText", 100, "FooterSpeedWidth")]
    [InlineData("FooterEtaText", 112, "FooterEtaWidth")]
    public void UI01_FooterNumericBlocksHaveReservedWidthAndTrimming(string element, int width, string token)
    {
        var xaml = ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml");
        var tag = ExtractElement(xaml, element);

        Assert.Contains($"Width=\"{width}\"", tag);
        Assert.Contains("TextWrapping=\"NoWrap\"", tag);
        Assert.Contains("TextTrimming=", tag);

        // Wide 档（1440x900 默认布局）的声明值必须与 XAML 实际宽度一致。
        var controller = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "ResponsiveLayoutController.cs");
        Assert.Contains($"public double {token} => Mode switch {{ LayoutMode.Wide => {width},", controller);
    }

    // ── UI-02：列结构 = 固定保留宽 + 单一弹性列（弹性列是进度轨道所在的第 2 列）────
    [Fact]
    public void UI02_BottomBarUsesReservedWidthsWithSingleElasticColumn()
    {
        var xaml = ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml");

        Assert.Contains("<Grid x:Name=\"BottomBarGrid\" ColumnSpacing=\"18\">", xaml);

        // 只在**底栏**这段列定义里数（整份 XAML 里还有别的网格）。
        var open = xaml.IndexOf("<Grid x:Name=\"BottomBarGrid\"", StringComparison.Ordinal);
        var defsEnd = xaml.IndexOf("</Grid.ColumnDefinitions>", open, StringComparison.Ordinal);
        Assert.True(open > 0 && defsEnd > open, "底栏列定义必须存在");
        var defs = xaml[open..defsEnd];

        Assert.Equal(9, Regex.Matches(defs, "<ColumnDefinition", RegexOptions.None).Count);
        Assert.Contains("<ColumnDefinition Width=\"0\"/>", defs);      // 第 6 列：中段隔离列已归零
        Assert.Contains("<ColumnDefinition Width=\"*\"/>", defs);      // 唯一的弹性列

        // FIX BATCH 5（§8）：弹性列必须是**进度轨道**那一列（第 2 列），不是中段隔离列。
        // 把弹性放在数字块之后、速率/ETA/网络/动作区之前 ⇒ 轨道填满可用宽度，
        // 且右端动作区仍被钉在右边缘（左边界 X 与文本无关）。若弹性列改成第 6 列，
        // 固定宽之和会在 Wide 档非 canonical 宽度下溢出、裁掉最右的"恢复"按钮（UIA 实测 w=62）。
        var defsList = Regex.Matches(defs, "<ColumnDefinition Width=\"([^\"]*)\"/>", RegexOptions.None)
            .Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(9, defsList.Length);
        Assert.Equal("*", defsList[2]);
        Assert.Equal("0", defsList[5]);
        Assert.Equal("0", defsList[6]);

        // 弹性列自己**不能**再放元素：它的宽度全部用于承载进度轨道（第 2 列由 FooterProgressHost 拉伸填满）。
        Assert.DoesNotContain("Grid.Column=\"6\"", xaml);
        // ★ R33 口径更新 ★ 底栏轨道槽高改由 Compact token 提供（14），不再是字面量 12。
        //   断言从"字面量 12"改为"槽高必须来自 token"，这比原来更严（禁止再散写数字）。
        Assert.Contains("Grid.Column=\"2\" MinWidth=\"0\" Height=\"{StaticResource PCMigImmersiveProgressCompactHostHeight}\"", xaml);
    }

    // ── UI-03：四动作按钮固定宽，且**绝不**用 Visibility 让列宽塌缩 ────────────────
    [Fact]
    public void UI03_ActionButtonsHaveFixedWidthAndNeverCollapse()
    {
        var xaml = ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml");
        var code = ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml.cs");
        var responsive = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "ShellResponsiveLayout.cs");

        Assert.Equal(4, Regex.Matches(xaml, "Width=\"124\"", RegexOptions.None).Count);
        foreach (var button in new[] { "FooterStartButton", "FooterPauseButton", "FooterStopButton", "FooterResumeButton" })
        {
            Assert.Contains($"Width=\"124\"", ExtractElement(xaml, button));
            // 代码后置里禁止把这个按钮藏起来（§8：Pause/Resume 切换不得改变动作区 X 坐标）
            Assert.DoesNotContain($"{button}.Visibility", code);
        }

        // 响应式接线必须**始终**给四个按钮设固定宽（无论哪一档），而不是只设 MinWidth。
        Assert.Contains("action.Width = layout.FooterActionWidth;", responsive);
    }

    // ── UI-04：顶栏与底栏共用同一套进度视觉语言 ───────────────────────────────────
    //   ★ 口径演进（2026-10-04 → Round-3 视觉纠偏，非放宽）★
    //     第一版：ProgressBar 控件（真机证明 WinUI 3 不按 DeterminateRoot 驱动填充宽度 ⇒ 填充恒 0）。
    //     第二版：「轨道 Border + 填充 Grid(Rectangle)」，填充宽度由唯一写入者按真值驱动。
    //     ★ 第三版（R33 Hero / Compact Material Family）★ 用户真机判词明确指出两根条属于两种材质语言：
    //       Hero = Immersive Renderer，Footer = 旧静态蓝 Rectangle（"直角条"）。
    //       现在**两条都**是 controls:ImmersiveTransferProgress（Hero / Compact 变体），共用 Capsule /
    //       Track-Space Chroma / Head 体积光场与**同一个 VisualProgress**。
    //       本条断言：两端都是同一控件族，且旧写法（Border 轨道 / Grid 填充 / 圆角矩形）在**两处都已不在**。
    [Fact]
    public void UI04_TopAndFooterProgressBarsShareOneStyle()
    {
        var footer = ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml");
        var step3 = ReadRepoFile("src", "PCMig.WinUI", "Views", "Step3ProgressPage.xaml");

        // 底栏：Compact 变体的 Immersive 控件（不再是 Border 轨道 + Grid 填充 + 静态圆角矩形）
        Assert.Contains("<controls:ImmersiveTransferProgress x:Name=\"FooterImmersiveProgress\" Variant=\"Compact\"", footer);
        Assert.Contains("Height=\"{StaticResource PCMigImmersiveProgressCompactHostHeight}\"", footer);
        Assert.DoesNotContain("x:Name=\"FooterProgressFill\"", footer);
        Assert.DoesNotContain("RadiusX=\"4\" RadiusY=\"4\" Fill=\"{StaticResource AccentGradientBrush}\"", footer);
        Assert.DoesNotContain("<Border Style=\"{StaticResource PCMigProgressTrack}\"/>", footer);

        // ★ Round-3 PHASE E（§7/§23）口径更新 ★ Step3 主进度条已换成 PCMig Immersive Transfer Progress。
        //   为什么是"更新"而不是"放宽"：同一条进度条上**视觉拥有者必须唯一**，所以这里不只断言新控件在，
        //   还断言旧写法（轨道 Border / 填充 Grid / 圆角矩形）已经**彻底不在**——叠层即两道 Push Band 打架。
        Assert.Contains("<controls:ImmersiveTransferProgress x:Name=\"TotalImmersiveProgress\"", step3);
        Assert.Contains("Height=\"{StaticResource PCMigImmersiveProgressHostHeight}\"", step3);
        Assert.DoesNotContain("x:Name=\"TotalProgressFill\"", step3);
        Assert.DoesNotContain("Style=\"{StaticResource PCMigProgressFill}\"", step3);
        Assert.DoesNotContain("<Border Style=\"{StaticResource PCMigProgressTrack}\"/>", step3);

        // 尺寸与几何都必须来自 token（PMML-R14/R22/R33）：Hero 16/12/6，Compact 14/10/5，两者半径都 = 厚度/2。
        var materials = ReadRepoFile("src", "PCMig.WinUI", "Themes", "Materials.xaml");
        Assert.Contains("x:Key=\"PCMigImmersiveProgressHostHeight\">16<", materials);
        Assert.Contains("x:Key=\"PCMigImmersiveProgressThickness\">12<", materials);
        Assert.Contains("x:Key=\"PCMigImmersiveProgressRadius\">6<", materials);
        Assert.Contains("x:Key=\"PCMigImmersiveProgressCompactHostHeight\">12<", materials);
        Assert.Contains("x:Key=\"PCMigImmersiveProgressCompactThickness\">10<", materials);
        Assert.Contains("x:Key=\"PCMigImmersiveProgressCompactRadius\">5<", materials);
    }

    // ── UI-05：填充占满轨道高度、由**唯一写入者**驱动、且没有任何动画推进它 ──────────
    [Fact]
    public void UI05_ProgressFillFillsTrackAndIsDrivenBySingleWriter()
    {
        var controls = ReadRepoFile("src", "PCMig.WinUI", "Themes", "Controls.xaml");
        var main = ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml.cs");
        var step3Code = ReadRepoFile("src", "PCMig.WinUI", "Views", "Step3ProgressPage.xaml.cs");

        var trackStart = controls.IndexOf("x:Key=\"PCMigProgressTrack\"", StringComparison.Ordinal);
        Assert.True(trackStart > 0, "必须存在 PCMigProgressTrack 样式");
        var track = controls[trackStart..controls.IndexOf("</Style>", trackStart, StringComparison.Ordinal)];
        Assert.Contains("<Setter Property=\"CornerRadius\" Value=\"{StaticResource PCMigProgressRadius}\"/>", track);
        Assert.Contains("Value=\"{StaticResource ProgressTrackBrush}\"", track);    // 复用既有 token（PMML-R14）
        // ★ Round-2（§3.5）★ 视觉厚度独立成 token 并垂直居中（不得用 12 DIP 满高充数、不得靠加粗制造运动感）
        Assert.Contains("<Setter Property=\"Height\" Value=\"{StaticResource PCMigProgressVisualThickness}\"/>", track);
        Assert.Contains("<Setter Property=\"VerticalAlignment\" Value=\"Center\"/>", track);

        var fillStart = controls.IndexOf("x:Key=\"PCMigProgressFill\"", StringComparison.Ordinal);
        Assert.True(fillStart > 0, "必须存在 PCMigProgressFill 样式");
        var fill = controls[fillStart..controls.IndexOf("</Style>", fillStart, StringComparison.Ordinal)];
        Assert.Contains("<Setter Property=\"VerticalAlignment\" Value=\"Center\"/>", fill);    // 视觉厚度内垂直居中（Round-2 §3.5）
        Assert.Contains("<Setter Property=\"Height\" Value=\"{StaticResource PCMigProgressVisualThickness}\"/>", fill);   // 填充厚度 = 轨道视觉厚度
        Assert.Contains("<Setter Property=\"HorizontalAlignment\" Value=\"Left\"/>", fill);
        Assert.Contains("<Setter Property=\"Width\" Value=\"0\"/>", fill);
        Assert.Contains("IsHitTestVisible", fill);

        // 唯一写入者（底栏）：把**同一个** VisualProgress 百分比写给 Compact 控件（不再换算像素宽度）
        //   ★ R33 口径更新 ★ 底栏改用 ImmersiveTransferProgress(Compact) 后，"宽度"这个概念已不在底栏存在；
        //   唯一写入者仍然只有 UpdateFooterProgressFill，但它只写 Value + ProgressState。
        Assert.Contains("UpdateFooterProgressFill", main);
        Assert.Contains("FooterProgressHost.SizeChanged += (_, _) => UpdateFooterProgressFill();", main);
        Assert.Contains("FooterImmersiveProgress.Value = clamped;", main);
        Assert.DoesNotContain("Math.Round(trackWidth * clamped / 100.0, 1)", main);

        // ★ Round-3 PHASE E（§16/§23、PMML-R23）★ 主进度条的唯一写入者仍是 UpdateTotalProgressFill，
        //   但它现在只写一个**百分比**（与大号百分比、字节数同源 = 同一个 VisualProgress），
        //   像素几何与光学层全部由控件自持；本页对已冻结的旧驱动必须零引用（§7 旧视觉路线下线）。
        Assert.Contains("private void UpdateTotalProgressFill()", step3Code);
        Assert.Contains("immersive.Value = percent;", step3Code);
        Assert.Contains("immersive.ProgressState = MapProgressState(", step3Code);
        Assert.DoesNotContain("ProgressMotionDriver", step3Code);
        Assert.DoesNotContain("SetTargetFromTimeline", step3Code);
        // 旧 XAML 元素 TotalProgressFill 已不存在于本页（注意方法名 UpdateTotalProgressFill 含同名前缀，
        //   所以要按"元素用法"断言：`TotalProgressFill.` / `(TotalProgressFill,`）。
        Assert.DoesNotContain("TotalProgressFill.", step3Code);
        Assert.DoesNotContain("(TotalProgressFill,", step3Code);

        // 动画只能装饰、绝不能成为进度真值的来源（§8 + PMML-R8）：样式里不许有任何时间线。
        foreach (var block in new[] { track, fill })
        {
            Assert.DoesNotContain("Storyboard", block);
            Assert.DoesNotContain("Animation", block);
            Assert.DoesNotContain("DispatcherTimer", block);
        }
    }

    // ── UI-06：对象明细标题区三列对齐（图标与标题同轴、说明可换行不抖动）────────────
    [Fact]
    public void UI06_ObjectPaneHeaderAlignsIconTitleAndHint()
    {
        var step3 = ReadRepoFile("src", "PCMig.WinUI", "Views", "Step3ProgressPage.xaml");
        var icon = step3.IndexOf("Glyph=\"&#xE8FD;\"", StringComparison.Ordinal);
        Assert.True(icon > 0, "对象明细图标必须存在");
        var open = step3.LastIndexOf("<Grid ", icon, StringComparison.Ordinal);
        var close = step3.IndexOf("</Grid>", icon, StringComparison.Ordinal);
        var block = step3[open..close];

        Assert.Contains("<ColumnDefinition Width=\"24\"/>", block);
        Assert.Contains("<ColumnDefinition Width=\"Auto\"/>", block);
        Assert.Contains("<ColumnDefinition Width=\"*\"/>", block);
        Assert.Contains("Grid.Column=\"0\"", ExtractElement(step3, "Glyph=\"&#xE8FD;\""));
        Assert.Contains("VerticalAlignment=\"Center\"", ExtractElement(step3, "Glyph=\"&#xE8FD;\""));
        Assert.Contains("HorizontalAlignment=\"Center\"", ExtractElement(step3, "Glyph=\"&#xE8FD;\""));
        Assert.Contains("UseLayoutRounding=\"True\"", ExtractElement(step3, "Glyph=\"&#xE8FD;\""));

        var hint = ExtractElement(step3, "ObjectPaneHintText");
        Assert.Contains("TextWrapping=\"Wrap\"", hint);
        Assert.Contains("MaxLines=\"2\"", hint);
        Assert.Contains("LineHeight=\"18\"", hint);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", hint);

        // 旧结构是横向 StackPanel（图标/标题/说明各按自身墨迹排布）⇒ 不允许回退。
        Assert.DoesNotContain("<StackPanel Orientation=\"Horizontal\" Spacing=\"10\">", block);
    }

    // ── UI-07：数字排版的**行高策略**（不裁顶/不裁底）+ 亚像素取整 ──────────────────
    // PHASE D 口径校正（2026-10-05）：原先逐个锁死魔法行高数字（24/18/28/52）。返修方案 §4 明确
    // 要求"不得继续堆 magic LineHeight"：TextBlock.LineHeight 默认 0 = 按字体度量自动算行盒，写死
    // 数字既可能与真实字体度量不符（把墨迹顶到行盒上沿 ⇒ 视觉上"顶部被削一条"），又在 DPI 缩放
    // 变化时失效。四张统计卡的大号数值（PCMigTextStatValue）现在不再声明 LineHeight —— 见 UI07b。
    // ★ Round-2 §5 追加 ★ 40px 的 PCMigTextTotalPercent 也**移出**本列表：它的 LineHeight=60 正是
    // "顶部摘要三组不共面"的 XAML 根因（40px 字形的 60 DIP 行盒 vs 同行两个 Bottom 对齐的小字号）。
    // 该样式现在必须**不带**显式行高 —— 由 Step3SummaryAlignmentTests.PercentBytesStateCentersAligned 锁死。
    // 其余两处底栏数字保持既有显式行高与亚像素取整（本轮未改其视觉）。
    [Theory]
    [InlineData("PCMigTextFooterPercent", 26)]
    [InlineData("PCMigTextFooterValue", 20)]
    public void UI07_NumericStylesDeclareLineHeight(string styleKey, int lineHeight)
    {
        var typography = ReadRepoFile("src", "PCMig.WinUI", "Themes", "Typography.xaml");
        var start = typography.IndexOf($"x:Key=\"{styleKey}\"", StringComparison.Ordinal);
        Assert.True(start > 0, $"{styleKey} 必须存在");
        var end = typography.IndexOf("</Style>", start, StringComparison.Ordinal);
        var style = typography[start..end];

        Assert.Contains($"<Setter Property=\"LineHeight\" Value=\"{lineHeight}\"/>", style);
        Assert.Contains("<Setter Property=\"UseLayoutRounding\" Value=\"True\"/>", style);
    }

    // ── UI-07b：统计卡大号数值**不再**依赖魔法 LineHeight，改用行高策略 + 容器几何 ──────────
    [Fact]
    public void UI07b_StatValueUsesLineStackingInsteadOfMagicLineHeight()
    {
        var typography = ReadRepoFile("src", "PCMig.WinUI", "Themes", "Typography.xaml");
        var start = typography.IndexOf("x:Key=\"PCMigTextStatValue\"", StringComparison.Ordinal);
        Assert.True(start > 0, "PCMigTextStatValue 必须存在");
        var end = typography.IndexOf("</Style>", start, StringComparison.Ordinal);
        var style = typography[start..end];

        Assert.DoesNotContain("LineHeight", style);                                   // 不再堆魔法行高
        Assert.Contains("<Setter Property=\"LineStackingStrategy\" Value=\"MaxHeight\"/>", style);
        Assert.Contains("<Setter Property=\"VerticalAlignment\" Value=\"Center\"/>", style);

        // 四张统计卡的大号数值各自有专用 host：MinHeight + 上下 3 DIP Padding 保证几何高度稳定，
        // 不再依赖 TextBlock 行高，且 host 不设 Clip（不得靠裁剪掩盖排版问题）。
        var step3 = ReadRepoFile("src", "PCMig.WinUI", "Views", "Step3ProgressPage.xaml");
        foreach (var host in new[] { "SpeedValueHost", "EtaValueHost", "ObjectValueHost", "BytesValueHost" })
        {
            var tag = ExtractElement(step3, host);
            Assert.Contains("MinHeight=\"34\"", tag);
            Assert.Contains("Padding=\"0,3,0,3\"", tag);
            Assert.DoesNotContain("Clip", tag);
        }
    }

    // ── UI-08：响应式保留宽 token 齐备（且弹性列恒 0）──────────────────────────────
    [Fact]
    public void UI08_ResponsiveControllerProvidesReservedWidthTokens()
    {
        var controller = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "ResponsiveLayoutController.cs");

        foreach (var token in new[]
                 {
                     "FooterPercentWidth", "FooterBytesWidth", "FooterSpeedWidth", "FooterEtaWidth",
                     "FooterActionWidth", "FooterNetWidth",
                 })
        {
            Assert.Contains(token, controller);
        }

Assert.Contains("public double FooterSpacerWidth => 0;", controller);
        // ★ 2026-10-06（用户方案第三步）★ 进度宿主改为**无硬下限**：原来的 Wide=160 比星号列在
        //   canonical 1424 下实际可用的约 138 DIP 还宽 22 DIP ⇒ 宿主越出槽位、右端胶囊圆弧被裁成直角。
        //   这里保留的**行为断言**是"进度轨道占用弹性列、自身不再要求固定宽"；只把"硬下限 160"这一
        //   实现细节换成"无硬下限"，不是删断言换绿灯。
        Assert.Contains("public double FooterProgressWidth => 0d;", controller);
        Assert.DoesNotContain("LayoutMode.Wide => 160", controller);
        Assert.Contains("LayoutMode.Wide => 18", controller);        // 与 XAML 里的 ColumnSpacing 一致
        Assert.Contains("LayoutMode.Wide => 18", controller);        // 与 XAML 里的 ColumnSpacing 一致
    }

    // ── UI-09：ShellResponsiveLayout 真的把它们接到控件上 ─────────────────────────
    [Fact]
    public void UI09_ShellLayoutWiresReservedWidthsToControls()
    {
        var responsive = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "ShellResponsiveLayout.cs");

        Assert.Contains("percentText.Width = layout.FooterPercentWidth;", responsive);
        Assert.Contains("bytesText.Width = layout.FooterBytesWidth;", responsive);
        Assert.Contains("speedText.Width = layout.FooterSpeedWidth;", responsive);
        Assert.Contains("etaText.Width = layout.FooterEtaWidth;", responsive);
        Assert.Contains("FooterNetPanel", responsive);
        Assert.Contains("netPanel.Visibility", responsive);
        Assert.Contains("progressHost.MinWidth = layout.FooterProgressWidth;", responsive);
    }

    // ── UI-10：数字可读性（字重 + 截断）与 IEC 单位口径 ─────────────────────────────
    [Fact]
    public void UI10_NumericReadabilityAndIecUnits()
    {
        var xaml = ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml");
        var step3 = ReadRepoFile("src", "PCMig.WinUI", "Views", "Step3ProgressPage.xaml");
        var format = ReadRepoFile("src", "PCMig.Core", "Util", "Format.cs");

        Assert.Contains("FontWeight=\"SemiBold\"", ExtractElement(xaml, "FooterBytesText"));
        foreach (var value in new[] { "SpeedValueText", "EtaValueText", "ObjectValueText", "BytesValueText" })
        {
            var tag = ExtractElement(step3, value);
            Assert.Contains("TextWrapping=\"NoWrap\"", tag);
            Assert.Contains("TextTrimming=\"CharacterEllipsis\"", tag);
        }

        // ★ UI Closure 2026-10-05（用户指令 UI-04）★ §7.5 原口径为 IEC 标签（KiB/MiB/GiB/TiB）；
        //   用户要求用户面向单位改回 Windows 惯例 KB/MB/GB/TB ⇒ 数值仍按 1024 计算，标签同步更新，
        //   且必须仍然只有 Format.cs 这一个 user-facing formatter（本断言即锁定该文件里的标签数组）。
        Assert.Contains("\"KB\"", format);
        Assert.Contains("\"GB\"", format);
        Assert.DoesNotContain("\"KiB\"", format);
        Assert.DoesNotContain("\"GiB\"", format);
    }

    // ── 辅助：从元素名/特征串切出该标签的文本（到下一个 `/>` 或 `>`）──────────────
    private static string ExtractElement(string text, string marker)
    {
        var hit = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(hit > 0, $"找不到标记：{marker}");
        // 标记可能落在标签中间（例如 Glyph=...），回退到它所属标签的 '<'。
        var start = text.LastIndexOf('<', hit);
        Assert.True(start >= 0, $"标记之前没有标签起始符：{marker}");
        var gt = text.IndexOf('>', hit);
        var selfClose = text.IndexOf("/>", hit, StringComparison.Ordinal);
        var end = selfClose >= 0 && (gt < 0 || selfClose <= gt) ? selfClose + 2 : gt + 1;
        return text[start..end];
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var path = Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"文件不存在：{path}");
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
        throw new InvalidOperationException("找不到含 PCMig.sln 的仓库根：" + AppContext.BaseDirectory);
    }
}
