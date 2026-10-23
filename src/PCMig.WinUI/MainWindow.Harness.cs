// ═══════════════════════════════════════════════════════════════════════════════
// ★ Preview.6 · 仅 Debug 编译 ★ MainWindow 的开发者实测缝（partial class 外挂）
//
// 为什么单独一个文件：它由 PCMig.WinUI.csproj 的 Debug 项组条件编译，
// Release 下**整段代码都不存在**，因此正式产物里既没有这个方法，也没有它的调用方。
//
// 【2026-10-22 隔离修正 · 编译期】上一版留下两个缺口，本轮一并封死：
//   1) `HarnessRequested` 读环境变量的分支参与了 Release 编译；
//   2) XAML 里的测试横幅在 Release 下靠运行时移除隐藏 —— 那是"运行期关闭"。
// 现在的做法：
//   · 整个文件只在 DEBUG 下参与编译（csproj 项组条件）；
//   · XAML 里的测试横幅**已整段删除**，对时标记只写窗口标题；
//   · MainWindow.xaml.cs 里不再有任何 #if !DEBUG 的测试分支。
// 实测校验：Release 的 PCMig.WinUI.dll 中 HarnessRequested / PCMIG_UI_HARNESS /
//   UiHarness / HarnessBanner / HarnessProjectIndicator 全部不存在。
// ═══════════════════════════════════════════════════════════════════════════════

#if DEBUG

using System;
using PCMig.WinUI.Presentation;

namespace PCMig.WinUI
{
    public sealed partial class MainWindow
    {
        /// <summary>
        /// ★ 仅 Debug ★ 供 <c>Harness.UiHarness</c> 调用真实投影路径（与 16 ms 节拍 / PushFooter 同一条）。
        /// 它不改任何状态，只是把既有私有方法暴露给同 assembly 的实测驱动。
        /// </summary>
        internal void HarnessProjectIndicator() => UpdateSourceConnectionIndicator();

        /// <summary>
        /// ★ 仅 Debug ★ 把真实的 SourceStateDot 元素交给读数用（只读，不改任何状态）。
        /// </summary>
        internal Microsoft.UI.Xaml.Shapes.Ellipse? SourceStateDotForHarness => SourceStateDot;

        /// <summary>
        /// ★ 仅 Debug ★ 当前会话 ViewModel（可能为 null：界面尚未开始任何任务）。
        /// 每次现取，不缓存 —— 「重建会话」会换掉实例。
        /// </summary>
        internal MigrationSessionViewModel? SessionForHarness => Session;

        /// <summary>
        /// ★ 仅 Debug ★ 让底栏呈现节拍**立即**跑一拍（走的是真实 RefreshFooterPresentation，
        /// 包含 HasLiveTransferActivity → SyncTransferActivity 的正式覆盖链）。
        /// 测试 B 用它模拟"高频重复状态投影"，而不是自己拼一条旁路。
        /// </summary>
        internal void HarnessPumpFooterPresentation() => RefreshFooterPresentation();

        /// <summary>
        /// ★ 仅 Debug ★ 把底栏进度条固定到指定百分比（R2-B 进度条光效视觉取证用）。
        /// 前置条件：先 <c>glob off</c> 停掉呈现节拍，否则会被 RefreshFooterPresentation 覆盖回会话真值。
        /// 它只写 Value / ProgressState 两个投影属性，不碰任何业务状态。
        /// </summary>
        internal void HarnessSetFooterProgress(double value, string state)
        {
            FooterImmersiveProgress.ProgressState = state.ToLowerInvariant() switch
            {
                "running" => PCMig.WinUI.Controls.ImmersiveProgress.ImmersiveProgressState.Running,
                "paused" => PCMig.WinUI.Controls.ImmersiveProgress.ImmersiveProgressState.Paused,
                "warning" => PCMig.WinUI.Controls.ImmersiveProgress.ImmersiveProgressState.Warning,
                "failed" => PCMig.WinUI.Controls.ImmersiveProgress.ImmersiveProgressState.Failed,
                _ => PCMig.WinUI.Controls.ImmersiveProgress.ImmersiveProgressState.Idle,
            };
            FooterImmersiveProgress.Value = Math.Clamp(value, 0d, 100d);
            FooterPercentText.Text = value.ToString("0.0") + "%";
        }

        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _harnessHoldTimer;

        /// <summary>
        /// ★ 仅 Debug ★ 在指定时长内**每 16 ms** 把底栏进度条钉在给定百分比。
        /// 存在的理由：底栏呈现节拍每拍都会把 Value 写回会话真值（没有真实会话时是 0），
        /// 一次性赋值会在下一拍被打回。这里只是持续重写同一个投影属性，不碰业务状态。
        /// </summary>
        internal void HarnessHoldFooterProgress(double value, int holdMs, string state)
        {
            try { _harnessHoldTimer?.Stop(); } catch { }
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(16);
            timer.IsRepeating = true;
            timer.Tick += (_, _) => HarnessSetFooterProgress(value, state);
            timer.Start();
            _harnessHoldTimer = timer;
            HarnessSetFooterProgress(value, state);
            var ms = Math.Max(200, holdMs);
            _ = System.Threading.Tasks.Task.Delay(ms).ContinueWith(_ =>
                DispatcherQueue.TryEnqueue(() => { try { timer.Stop(); } catch { } }));
        }

        /// <summary>
        /// ★ 仅 Debug ★ 读底栏进度条的**真实**渲染量（不含任何推算），供取证脚本核对。
        /// </summary>
        internal string HarnessFooterProgressReport()
        {
            var host = FooterProgressHost;
            double rootX = double.NaN, rootY = double.NaN, scale = double.NaN;
            try
            {
                var pt = host.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
                rootX = pt.X; rootY = pt.Y;
                scale = host.XamlRoot?.RasterizationScale ?? 1d;
            }
            catch { /* 布局尚未完成时按 NaN 上报，取证脚本会重试 */ }
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "value={0:0.###} progressWidth={1:0.###} head={2:0.###} halo={3:0.###} canvas={4} rootXY=({5:0.#},{6:0.#}) hostWH={7:0.#}x{8:0.#} scale={9:0.###} layers={10}",
                FooterImmersiveProgress.Value,
                FooterImmersiveProgress.ProgressWidth,
                FooterImmersiveProgress.HeadX,
                FooterImmersiveProgress.HaloStrength,
                FooterImmersiveProgress.IsCanvasAttached,
                rootX, rootY, host.ActualWidth, host.ActualHeight, scale,
                FooterImmersiveProgress.LastLayerSummary);
        }

        /// <summary>
        /// ★ 仅 Debug ★ 把本窗口排到交互桌面最前（无交互桌面时静默失败）。
        /// </summary>
        internal void HarnessBringToFront()
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }
            catch { /* 失败就保持原状，由实测脚本如实记录 */ }
        }

        /// <summary>
        /// ★ 仅 Debug ★ 是否启用开发者实测驱动（未设环境变量 ⇒ false，正常启动零介入）。
        /// 【隔离要点】本属性整段只在 DEBUG 编译，Release 产物里不存在，因此正式运行时
        /// 没有任何分支会因为环境变量而改变行为。
        /// </summary>
        internal static bool HarnessRequested =>
            !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("PCMIG_UI_" + "HARNESS"));

        private const int SW_RESTORE = 9;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}

#endif
