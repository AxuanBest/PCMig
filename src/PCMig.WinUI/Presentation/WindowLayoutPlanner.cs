using System;

namespace PCMig.WinUI.Presentation;

/// <summary>
/// 首次启动窗口的**目标外框矩形**（物理像素，屏幕坐标系）。
/// 与 WinUI 的 <c>RectInt32</c> 同语义，但刻意不引用任何 WinRT 类型：
/// 这样它可以被 <c>tests\PCMig.Core.Tests</c> 源码链入，直接对**纯数学**做行为级断言
/// （见 <c>R3WindowLayoutTests</c>），而不是只对着源码文本猜。
/// </summary>
public readonly record struct PlannedWindowPlacement(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    /// <summary>整块矩形是否完全落在给定工作区内（含边界）。</summary>
    public bool IsInside(int workX, int workY, int workWidth, int workHeight) =>
        X >= workX
        && Y >= workY
        && Width > 0
        && Height > 0
        && Right <= workX + workWidth
        && Bottom <= workY + workHeight;
}

/// <summary>
/// 首次启动窗口布局的**唯一计算入口**（纯函数，零 WinUI / 零 P/Invoke）。
///
/// 【为什么需要它 —— 真机缺陷】
/// 修复前启动路径只有 <c>AppWindow.ResizeClient(Canonical 1424×891 DIP × windowScale)</c>：
///   · 尺寸是**写死的 DIP**，与显示器工作区无关 ⇒ 在 200% 缩放、2880×1800 的机器上
///     1424×891 DIP = 2848×1782 px，加上标题栏后**高于工作区 1704 px**，底部被裁；
///   · 位置**一行代码都没设** ⇒ 由系统按默认级联位置摆放，右侧溢出屏幕。
/// 用户真机截图（2026-10-17）表现正是"默认尺寸过大 + 位置偏移 + 右侧内容超出屏幕"。
///
/// 【按什么算】
///   · 尺寸 = 工作区宽度 × 92%、工作区高度 × 91%（用户给定的目标比例），
///     再夹到工作区内 —— 工作区本身已排除任务栏，故任务栏不会被压住；
///   · 位置 = 在工作区内**水平、垂直居中**（整数除法的 1px 余数留给右侧/下方）；
///   · 全部用**物理像素**参与比值运算 ⇒ 与 Windows 显示缩放无关：
///     工作区与目标同时按 scale 放大/缩小，比值恒定，不存在"某档 DPI 才跑偏"的换算链；
///   · 多显示器：调用方传入**窗口所在显示器**的工作区（不一定从 0,0 开始，
///     副屏可以是负坐标），居中偏移全部相对该工作区计算，不做任何绝对坐标假设。
///
/// 【为什么不用 DIP】
/// 一旦把"92%"先换算成 DIP 再换算回物理像素，就引入两次取整和一次 DPI 假设；
/// 而 92% 是**纯比例**，在物理像素域直接相乘既更准也更简单。
/// </summary>
public static class WindowLayoutPlanner
{
    /// <summary>目标宽度占工作区宽度的比例（用户 2026-10-17 给定的目标效果：约 92%）。</summary>
    public const double DefaultWidthRatio = 0.92;

    /// <summary>目标高度占工作区高度的比例（用户 2026-10-17 给定的目标效果：约 91%）。</summary>
    public const double DefaultHeightRatio = 0.91;

    /// <summary>
    /// 计算首次启动的窗口外框矩形。
    /// </summary>
    /// <param name="workX">工作区左上角 X（物理像素，可为负 —— 副屏在主屏左侧时就是负数）。</param>
    /// <param name="workY">工作区左上角 Y（物理像素）。</param>
    /// <param name="workWidth">工作区宽度（物理像素，已排除任务栏）。</param>
    /// <param name="workHeight">工作区高度（物理像素，已排除任务栏）。</param>
    /// <param name="minimumWidth">
    /// 窗口最小宽度（物理像素，可选）。来自 WM_GETMINMAXINFO 的同一口径；为 0 表示不约束。
    /// **不得因它突破工作区** —— 小屏上宁可退化成占满工作区，也不能把窗口放大到屏幕外。
    /// </param>
    /// <param name="minimumHeight">窗口最小高度（物理像素，可选），语义同上。</param>
    public static PlannedWindowPlacement Plan(
        int workX,
        int workY,
        int workWidth,
        int workHeight,
        int minimumWidth = 0,
        int minimumHeight = 0)
    {
        // 工作区无效时不能抛：启动路径抛异常会直接变成"应用打不开"。
        // 退化为 1×1 的居中矩形，交由调用方（及系统）继续兜底。
        if (workWidth <= 0 || workHeight <= 0)
        {
            return new PlannedWindowPlacement(workX, workY, 1, 1);
        }

        var width = (int)Math.Round(workWidth * DefaultWidthRatio, MidpointRounding.AwayFromZero);
        var height = (int)Math.Round(workHeight * DefaultHeightRatio, MidpointRounding.AwayFromZero);

        // 最小尺寸是**下限**，但绝不能压过工作区上限：先抬到 min，再一律夹回工作区。
        if (minimumWidth > 0 && width < minimumWidth) width = minimumWidth;
        if (minimumHeight > 0 && height < minimumHeight) height = minimumHeight;

        if (width > workWidth) width = workWidth;
        if (height > workHeight) height = workHeight;

        if (width < 1) width = 1;
        if (height < 1) height = 1;

        // 居中：整数除法的余数（0 或 1 px）留在右侧 / 下方，保证矩形始终落在工作区内。
        var x = workX + (workWidth - width) / 2;
        var y = workY + (workHeight - height) / 2;

        return new PlannedWindowPlacement(x, y, width, height);
    }
}
