using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PCMig.WinUI.Presentation;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// 首次启动窗口布局（尺寸 + 位置）回归 —— 2026-10-17 用户真机截图缺陷。
///
/// 【缺陷形态】修复前启动只做 <c>ResizeClient(1424×891 DIP × scale)</c>，且从不设位置：
///   · 尺寸与显示器工作区无关 ⇒ 200% 缩放的 2880×1800 机器上客户区 = 2848×1782 px，
///     加标题栏后**高于工作区 1704 px**（底部被裁）；
///   · 位置由系统默认级联摆放 ⇒ 右侧溢出屏幕。
///
/// 【验证策略：两层，缺一不可】
///   1) **行为级**（真正的数学断言）：<see cref="WindowLayoutPlanner"/> 是纯函数、零 WinUI 依赖，
///      被源码链入本测试项目 ⇒ 直接 new/调用，断言"92%/91% + 居中 + 不越界 + 多屏负坐标 + DPI 无关"。
///      这一层能证明**算得对**。
///   2) **静态契约**（防止有人把调用链改回去）：扫描 <c>MainWindow.xaml.cs</c> 的**代码文本**
///      （先剥注释，避免被注释里的字样骗过），断言走 DisplayArea 工作区、走 MoveAndResize、
///      不调用 Maximize、不把 Canonical 客户区锁回来、没有写死的绝对坐标。
///      这一层只能证明**调用链还在**，不能证明真机上窗口真的显示成那样 —— 真机可见性
///      仍须用户人工视觉验收（本轮机器无交互桌面，截图不可用）。
/// </summary>
public sealed class R3WindowLayoutTests
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

    /// <summary>窗口初始化代码（已剥注释，只留可执行文本）。</summary>
    private static string MainWindowCode =>
        SourceStripper.StripComments(File.ReadAllText(Path.Combine(WinUiDir, "MainWindow.xaml.cs")));

    private static string PlannerCode =>
        SourceStripper.StripComments(
            File.ReadAllText(Path.Combine(WinUiDir, "Presentation", "WindowLayoutPlanner.cs")));

    // ───────────────────────────── 1) 行为级：规划器数学 ─────────────────────────────

    /// <summary>用户 2026-10-17 给定的目标比例：宽度约工作区 92%、高度约 91%。</summary>
    [Fact]
    public void Planner_UsesDocumentedTargetRatios()
    {
        Assert.Equal(0.92, WindowLayoutPlanner.DefaultWidthRatio, 6);
        Assert.Equal(0.91, WindowLayoutPlanner.DefaultHeightRatio, 6);

        // 2880×1704（本机实测工作区）→ 2650×1551，居中留 115/76 px
        var p = WindowLayoutPlanner.Plan(0, 0, 2880, 1704);
        Assert.Equal(2650, p.Width);
        Assert.Equal(1551, p.Height);
        Assert.Equal(115, p.X);
        Assert.Equal(76, p.Y);
    }

    [Theory]
    [InlineData(1920, 1040)]   // 1080p@100%，任务栏 40
    [InlineData(2560, 1400)]   // 1440p@100%
    [InlineData(2880, 1704)]   // 本机 2880×1800@200%
    [InlineData(1366, 728)]    // 笔记本 100%
    [InlineData(3840, 2080)]   // 4K@100%
    [InlineData(1280, 680)]    // 1080p@150% 的 DIP 等价工作区
    public void Planner_SizeStaysOnTargetRatio_AndNeverExceedsWorkArea(int workW, int workH)
    {
        var p = WindowLayoutPlanner.Plan(0, 0, workW, workH);

        // 比例：取整误差不超过半个像素
        Assert.InRange(p.Width, workW * 0.92 - 0.5, workW * 0.92 + 0.5);
        Assert.InRange(p.Height, workH * 0.91 - 0.5, workH * 0.91 + 0.5);

        // 硬约束：完整落在工作区内（含边界）—— 这条对应"不超出屏幕"
        Assert.True(p.IsInside(0, 0, workW, workH), $"{p} 超出 {workW}×{workH} 工作区");
    }

    [Theory]
    [InlineData(0, 0, 1920, 1040)]
    [InlineData(0, 0, 2880, 1704)]
    [InlineData(1920, 0, 1920, 1040)]     // 副屏在主屏右侧
    [InlineData(-1920, 0, 1920, 1040)]    // 副屏在主屏左侧（负坐标）
    [InlineData(0, -1080, 3840, 1080)]    // 上方副屏（负 Y）
    public void Planner_CentersWithinTheGivenWorkArea(int wx, int wy, int ww, int wh)
    {
        var p = WindowLayoutPlanner.Plan(wx, wy, ww, wh);

        var leftMargin = p.X - wx;
        var rightMargin = wx + ww - p.Right;
        var topMargin = p.Y - wy;
        var bottomMargin = wy + wh - p.Bottom;

        // 居中：两侧留白相等，允许 1 px 的取整余数（余数只许落在右侧/下方）
        Assert.InRange(leftMargin - rightMargin, -1, 1);
        Assert.InRange(topMargin - bottomMargin, -1, 1);
        Assert.True(leftMargin >= 0 && topMargin >= 0, $"{p} 越出工作区左上角");
        Assert.True(rightMargin >= 0 && bottomMargin >= 0, $"{p} 越出工作区右下角");
    }

    /// <summary>
    /// 同一台机器换缩放率（100% → 125% → 150% → 200%）时，工作区物理像素按同倍率变化。
    /// 目标矩形必须**按同一倍率**缩放（±1 px），否则就是"某档 DPI 下窗口跑偏"这一类历史缺陷。
    /// </summary>
    [Theory]
    [InlineData(1.25)]
    [InlineData(1.50)]
    [InlineData(2.00)]
    public void Planner_IsDpiInvariant(double scale)
    {
        const int logicalW = 1440;
        const int logicalH = 852;   // 2880×1704 @200% 的 DIP 等价
        var basePlan = WindowLayoutPlanner.Plan(0, 0, logicalW, logicalH);
        var scaledPlan = WindowLayoutPlanner.Plan(
            0, 0,
            (int)Math.Round(logicalW * scale),
            (int)Math.Round(logicalH * scale));

        Assert.InRange(scaledPlan.Width, basePlan.Width * scale - 1, basePlan.Width * scale + 1);
        Assert.InRange(scaledPlan.Height, basePlan.Height * scale - 1, basePlan.Height * scale + 1);
        Assert.InRange(scaledPlan.X, basePlan.X * scale - 1, basePlan.X * scale + 1);
        Assert.InRange(scaledPlan.Y, basePlan.Y * scale - 1, basePlan.Y * scale + 1);
    }

    /// <summary>
    /// 最小尺寸是下限，**但不得因此突破工作区**：小屏上宁可退化占满工作区，
    /// 也不能把窗口放大到屏幕外（那就是本轮要修的缺陷本身）。
    /// </summary>
    [Fact]
    public void Planner_ClampsMinimumSizeToWorkArea()
    {
        var p = WindowLayoutPlanner.Plan(0, 0, 800, 600, minimumWidth: 1068, minimumHeight: 668);
        Assert.Equal(800, p.Width);
        Assert.Equal(600, p.Height);
        Assert.Equal(0, p.X);
        Assert.Equal(0, p.Y);
        Assert.True(p.IsInside(0, 0, 800, 600));
    }

    [Fact]
    public void Planner_AppliesMinimumSize_WhenWorkAreaIsLargeEnough()
    {
        // 92% 的 1000×700 = 920×637，低于最小 1068×668 ⇒ 抬到最小尺寸，且仍居中
        var p = WindowLayoutPlanner.Plan(0, 0, 1000, 700, minimumWidth: 1068, minimumHeight: 668);
        Assert.Equal(1000, p.Width);   // 1068 > 工作区 1000 ⇒ 夹回工作区
        Assert.Equal(668, p.Height);   // 668 < 700 ⇒ 用最小尺寸
        Assert.Equal(0, p.X);
        Assert.Equal(16, p.Y);         // (700-668)/2 = 16
    }

    /// <summary>工作区无效时不得抛异常（启动路径抛异常 = 应用打不开）。</summary>
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(0, 0, -100, 500)]
    public void Planner_InvalidWorkArea_DegradesWithoutThrowing(int wx, int wy, int ww, int wh)
    {
        var p = WindowLayoutPlanner.Plan(wx, wy, ww, wh);
        Assert.True(p.Width >= 1 && p.Height >= 1);
    }

    // ───────────────────────── 2) 静态契约：调用链没被改回去 ─────────────────────────

    [Fact]
    public void WindowInitialization_UsesDisplayWorkArea_NotAHardcodedScreen()
    {
        var code = MainWindowCode;
        Assert.Contains("DisplayArea.GetFromWindowId", code);
        Assert.Contains("DisplayAreaFallback", code);
        Assert.Contains(".WorkArea", code);
        // 主显示器兜底必须存在（GetFromWindowId 可能返回 null）
        Assert.Contains("DisplayArea.Primary", code);
    }

    [Fact]
    public void WindowInitialization_PlacesWindowViaMoveAndResize_WithPlannerValues()
    {
        var code = MainWindowCode;
        Assert.Contains("WindowLayoutPlanner.Plan(", code);
        Assert.Contains("AppWindow.MoveAndResize(", code);
        // 必须用规划器算出的值，不能就地写死坐标/尺寸
        Assert.DoesNotMatch(new Regex(@"MoveAndResize\(\s*new\s+RectInt32\(\s*\d"), code);
        Assert.Matches(new Regex(@"MoveAndResize\(\s*new\s+RectInt32\("), code);
    }

    [Fact]
    public void WindowInitialization_NeverForcesMaximizedState()
    {
        var code = MainWindowCode;
        Assert.DoesNotContain(".Maximize(", code);
        // 必须显式回到普通窗口状态（不继承系统记得的最大化）
        Assert.Contains("OverlappedPresenterState.Restored", code);
        Assert.Contains(".Restore()", code);
    }

    /// <summary>
    /// 旧的"Canonical 视口自校正"必须**不再存在**：它会在启动后把窗口又拉回 1424×891 DIP
    /// （200% 下 = 2848×1782 px），等于把本轮修复原地撤销。
    /// </summary>
    [Fact]
    public void WindowInitialization_DoesNotReinstateCanonicalViewportLock()
    {
        var code = MainWindowCode;
        Assert.DoesNotContain("_viewportCalibrated", code);
        // 名称：Canonical 客户区常量仍作为设计面/最小尺寸基准存在，但**不得**再用于初始布局的计算。
        Assert.DoesNotContain("ApplyCanonicalClientSize", code);
    }

    /// <summary>规划器本身不得内建任何具体屏幕的绝对坐标或像素尺寸。</summary>
    [Fact]
    public void Planner_HasNoHardcodedScreenGeometry()
    {
        var code = PlannerCode;
        Assert.DoesNotContain("1424", code);
        Assert.DoesNotContain("891", code);
        Assert.DoesNotContain("1920", code);
        Assert.DoesNotContain("1080", code);
        // 比例常量必须留在规划器里（单一事实来源）
        Assert.Contains("DefaultWidthRatio = 0.92", code);
        Assert.Contains("DefaultHeightRatio = 0.91", code);
    }
}
