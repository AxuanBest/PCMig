using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PCMig.WinUI.Presentation;

/// <summary>
/// Whole-App Uniform Scaling 宿主（**2026-09-28 起生产默认启用**；`PCMIG_UNIFORM_HOST=0` 可回退）。
///
/// 目标产品行为（用户口径）：**Uniformly Scaled Desktop UI**，不是 Responsive Compact UI。
/// 窗口缩小时应当"像拿着整张 PCMig 按比例缩小" —— 字体 / Card / Button / Icon / Badge /
/// 输入框 / Sidebar / Header / Footer / Padding / Gap / 行高列宽全部同比例缩小，视觉结构不变：
/// 不重排、不折行、不往一起挤、不裁切、不出现页级滚动条。
///
/// 结构（运行时重挂，**XAML 一字不改**）：
/// <code>
/// Window
/// ├─ Desktop Acrylic Backdrop      ← 窗口级，天然不参与缩放
/// ├─ Native Caption Buttons        ← 不在 window.Content 内，天然不参与缩放
/// └─ Viewbox (Stretch=Uniform)
///    └─ DesignSurface 1424×891     ← Canonical 客户区（实测值；与 MainWindow.CanonicalClientHeightDip 一致）
///       └─ 原 window.Content（含 TitleBarRoot + ShellRowsGrid；全部 x:Name 作用域所在）
/// </code>
///
/// 隔离与回退（2026-09-28 起**默认启用**）：
///   · 默认即启用；显式设 <c>PCMIG_UNIFORM_HOST=0</c> 重启 ⇒ <see cref="Install"/> 直接返回，
///     回到旧的响应式路径（Compact 结构重排），无需回退代码；
///   · 因此"关掉开关"仍是可用的对照/应急手段，但**默认行为已是等比缩放**。
///
/// 纪律：
///   · <b>严禁非等比</b>：只用 <see cref="Stretch.Uniform"/>（ScaleX == ScaleY）；
///     绝不用 <c>UniformToFill</c>、不拉伸、不裁 UI；宽高比与 1424:891 不一致时出现的
///     剩余空间是 Uniform 的正常数学结果（由对齐吸收），不靠改 Stretch 填满。
///   · 不做单点 Font Clamp：若将来字号不可接受，正确做法是提高整体 MinimumUIScale
///     并禁止窗口继续缩小，而不是"字体停缩、Card 继续缩"。
/// </summary>
public static class UniformScaleHost
{
    /// <summary>
    /// 开关的环境变量名。**P0 修复（2026-09-28）：本宿主已改为生产默认启用** ——
    /// 显式设 <c>PCMIG_UNIFORM_HOST=0</c> 可回退到旧的响应式（Compact 结构重排）路径，用于对照与应急。
    /// </summary>
    public const string EnvironmentVariable = "PCMIG_UNIFORM_HOST";

    /// <summary>设计面宽度 = 实测 Canonical 客户区（XamlRoot.Size，DIP）。不是窗口外框 1440。</summary>
    public const double DesignWidth = 1424.0;

    /// <summary>
    /// 设计面高度 = Canonical 客户区高（DIP）。
    ///
    /// ★ A7（B2，2026-09-30）修正：原值 **892** 与 <c>MainWindow.CanonicalClientHeightDip = 891</c>
    /// （启动时 <c>ApplyCanonicalClientSize()</c> 把 XAML 客户区固定成的值）**差 1 像素**，
    /// 于是在默认窗口下 Viewbox 被迫按 <c>891/892 = 0.99888</c> 做**分数缩放**：
    ///   · 产品上零收益（差 0.11% 肉眼不可辨）；
    ///   · 代价是整窗内容的合成重采样，而 ComboBox 的 Popup 是**独立视觉岛**
    ///     （实测 <c>Microsoft.UI.Content.PopupWindowSiteBridge</c>）⇒ 浮层文字明显比主界面发糊（用户报的 B2）。
    /// A/B 实测（证据 archive\evidence\a7-two-ui-bugs）：宿主生效(0.9986) → 浮层发虚；
    /// <c>PCMIG_UNIFORM_HOST=0</c>(比例 1) → 清晰。按 1 像素对齐参考帧后，默认窗口下比例**恰好 = 1.0**，
    /// 浮层恢复原生像素渲染；窗口明显变小/变大时仍由 Viewbox 等比缩放，行为不变。
    /// </summary>
    public const double DesignHeight = 891.0;

    /// <summary>
    /// 光学安全下限（2026-09-28 按四页压力实测标定）：0.75 时四页仍**完整、无裁切、无留白**，
    /// 表格路径/原因、日志级别、底栏小字均清晰可辨。
    /// 下限**不**由 Viewbox 实现（那会变成"内容大于窗口 ⇒ 裁切"），而是交给 Win32 最小追踪尺寸：
    /// 客户区最小 = Canonical × 本值，窗口因此无法继续缩小。
    /// </summary>
    public const double MinimumScale = 0.75;

    /// <summary>
    /// 视觉上限（2026-09-28 实测标定）：1.15 时布局仍协调，不呈"电视 UI"。
    /// 超过 DesignSurface × 本值后由 Viewbox 的 MaxWidth/MaxHeight 停止放大并居中（四周露出 Acrylic）。
    /// </summary>
    public const double MaximumScale = 1.15;

    /// <summary>
    /// 是否启用。**默认启用**（除非显式设 <c>PCMIG_UNIFORM_HOST=0</c>）。
    ///
    /// ★ 为什么改默认（P0 回归诊断实测，2026-09-28）：
    ///   未启用时，窗口缩到最小会落进 <c>LayoutMode.Compact</c>，触发**结构性重排**
    ///   （Step3 统计卡一行四列→两行两列、下方双栏塌陷、Step4 工具栏折行）。
    ///   重排后内容变高，页面根元素（实测 816×436）**溢出页面视口**（实测 676×407）
    ///   ⇒ Step1 / Step3 / Step4 的下半部分被裁掉（对象明细整卡不可见等）。
    ///   启用后布局**恒为 Canonical 1424×891**（实测 mode=Wide、design=1424×891），
    ///   缩放只由本宿主承担 ⇒ 同一最小窗口下四页完整
    ///   （UIA 逐点 = 1.00 × 0.75；视觉对比确认"忠实等比缩小、无重排、无裁切"）。
    ///   这正是"整个应用只允许一个 Scale Owner"的产品口径。
    /// </summary>
    public static bool IsRequested =>
        !string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable), "0", StringComparison.Ordinal);

    /// <summary>是否已实际完成重挂（true 时窗口内容根 = Viewbox）。</summary>
    public static bool IsActive { get; private set; }

    /// <summary>DesignSurface 内的**真实应用根**（所有 x:Name 的名字作用域所在）。未激活时为 null。</summary>
    public static FrameworkElement? ApplicationRoot { get; private set; }

    /// <summary>固定 1424×891 的 DesignSurface。未激活时为 null。</summary>
    public static FrameworkElement? DesignSurface { get; private set; }

    /// <summary>
    /// ★ A7（B2，2026-09-30）★ 贴近 1.0 的比例带。
    ///
    /// 历史记录：本值一度用于"贴近 1.0 时动态把设计面设成客户区尺寸"，**实测有副作用**（动态改设计面尺寸
    /// 会打断壳的页面导航加速键与 Popup 宿主：Ctrl+1..4 失效、下拉浮层不再出现）⇒ 已撤销。
    /// 真正的根因是**参考帧与真实客户区差 1 像素**（见 <see cref="DesignHeight"/>），已按 1 像素修正常量解决。
    /// 本常量保留为 0（不使用），仅供将来若要再做"吸附"时参考，避免再次踩同一个坑。
    /// </summary>
    public const double SnapEpsilon = 0.0;

    /// <summary>缩放宿主（Viewbox）。未激活时为 null。</summary>
    public static Viewbox? Host { get; private set; }

    /// <summary>最近一次实测的统一缩放比例（诊断 / 验收口径）。未激活时恒 1.0。</summary>
    public static double AppliedScale { get; private set; } = 1.0;

    /// <summary>
    /// 运行时重挂：把现有 <see cref="Window.Content"/> 放进固定尺寸的 DesignSurface，
    /// DesignSurface 作为 Viewbox.Child，再把 Viewbox 挂回窗口内容。
    ///
    /// 单父级约束处理：<c>window.Content = null</c> 先把旧根摘离，否则加入 DesignSurface 会抛
    /// "元素已有父级"。任一步骤失败都会把旧根**还回窗口内容**并放弃激活，保证不留下空窗口。
    /// </summary>
    /// <returns>是否已激活（未请求启用 / 失败均为 false）。</returns>
    public static bool Install(Window? window)
    {
        if (window is null) return false;
        if (!IsRequested) return false;      // 仅在显式设 PCMIG_UNIFORM_HOST=0 时回退到旧的响应式路径
        if (IsActive) return false;
        if (window.Content is not FrameworkElement oldRoot) return false;

        var design = new Grid
        {
            Width = DesignWidth,
            Height = DesignHeight,
        };

        var host = new Viewbox
        {
            // 等比：ScaleX == ScaleY。Both 允许放大，再由下面的 Max 钳住上限。
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.Both,
            // 上限：超过 DesignSurface × MaximumScale 后 Viewbox 不再增长 ⇒ 内容停止放大并居中，
            // 四周露出窗口 Backdrop（Acrylic），避免大屏上"整台 UI 变电视"。
            // 下限**不在**这里做：给 Viewbox 设 Min 会得到"内容大于窗口 ⇒ 裁切"，与产品口径相反；
            // 下限由 Win32 最小追踪尺寸（Canonical × MinimumScale）保证。
            MaxWidth = DesignWidth * MaximumScale,
            MaxHeight = DesignHeight * MaximumScale,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 先摘除，避免同一元素双父。
        try
        {
            window.Content = null;
        }
        catch
        {
            return false;   // 摘除失败 ⇒ 未做任何改动
        }

        try
        {
            design.Children.Add(oldRoot);
            host.Child = design;
            window.Content = host;
        }
        catch
        {
            // 恢复生产路径：把旧根还回窗口内容。
            try { design.Children.Clear(); } catch { /* 收尾异常不得外溢 */ }
            try { window.Content = oldRoot; } catch { /* 同上 */ }
            return false;
        }

        ApplicationRoot = oldRoot;
        DesignSurface = design;
        Host = host;
        IsActive = true;
        AppliedScale = 1.0;

        // 宿主自身尺寸变化即代表客户区变化 ⇒ 刷新诊断比例（纯记账，不参与布局）。
        //   ★ A7（B2）★ 这里**只记账**：早期版本曾在此动态改设计面尺寸，实测会打断页面导航加速键与
        //   Popup 宿主（Ctrl+1..4 失效、下拉浮层不出现）⇒ 已撤销；根因改由 DesignHeight 的 1 像素对齐解决。
        host.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
            AppliedScale = Math.Min(e.NewSize.Width / DesignWidth, e.NewSize.Height / DesignHeight);
        };

        return true;
    }
}