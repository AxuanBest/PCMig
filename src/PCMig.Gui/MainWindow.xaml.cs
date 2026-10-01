using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PCMig.Gui;

public partial class MainWindow : Window
{
    // 设计最小尺寸：内容在 960×660 下仍完整可用（页面容器已 ClipToBounds，底栏永不被压盖）。
    // 若当前显示器工作区比它还小，运行时会按工作区下调（见 ApplyWorkAreaClamp）。
    private const double DesignMinWidth = 960;
    private const double DesignMinHeight = 660;

    private bool _clamping;

    public MainWindow()
    {
        InitializeComponent();
        // 初始尺寸/位置在**窗口创建之前**就按主显示器工作区（DIP）定好：
        // 这样 Windows 放置窗口时就已经放得下，不需要创建之后再改几何 ——
        // 实测创建后一旦程序化改过尺寸或位置，WPF 就会改写 ptMaxSize/ptMaxPosition，
        // 之后最大化该窗口时可视边框会越过工作区右下各 9px（A/B：不介入时正确 0,0-1920,1140）。
        var work = SystemParameters.WorkArea;
        if (work.Width > 400 && work.Height > 300)
        {
            MinWidth = Math.Max(320, Math.Min(DesignMinWidth, Math.Floor(work.Width)));
            MinHeight = Math.Max(240, Math.Min(DesignMinHeight, Math.Floor(work.Height)));
            Width = Math.Max(MinWidth, Math.Min(Width, Math.Floor(work.Width) - 16));
            Height = Math.Max(MinHeight, Math.Min(Height, Math.Floor(work.Height) - 16));
            WindowStartupLocation = WindowStartupLocation.CenterScreen;   // 居中即保证整窗落在工作区内
        }
        else
        {
            MinWidth = DesignMinWidth;
            MinHeight = DesignMinHeight;
        }
        // 拖到另一台显示器（分辨率/缩放不同）或最大化/还原时重新夹取，保证窗口始终落在可见区内。
        // 注意：**不订阅 SizeChanged** —— 最大化/还原的过渡帧里 WindowState 仍是 Normal，
        // 此时按"当前尺寸"重算位置会把最大化窗口硬掰到 (0,0)，实测导致可视边框越过工作区右下各 9px
        // （A/B：不订阅时最大化可视边框 = 0,0-1920,1140 正确；订阅后变 9,9-1929,1149）。
        // "拖拽放大到超出工作区"由 WM_GETMINMAXINFO 的 ptMaxTrackSize 钉住，不需要靠 SizeChanged。
        LocationChanged += (_, _) => ApplyWorkAreaClamp();
        StateChanged += (_, _) => ApplyWorkAreaClamp();
        // 纯缩放（位置不变）也要复查一次：拖大窗口同样可能把底栏推出屏幕。
        SizeChanged += (_, _) => ApplyWorkAreaClamp();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.LiveFiles.CollectionChanged += (_, _) =>
                {
                    if (LiveFilesList.Items.Count > 0)
                        LiveFilesList.ScrollIntoView(LiveFilesList.Items[^1]); // 自动滚到最新一行
                };
            }
        };
    }

    /// <summary>窗口句柄就绪后套用系统亚克力材质（失败自动退化，不影响功能）。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 视觉 v3：优先真·实时毛玻璃（DWM 官方亚克力，实时模糊窗后内容）；
        // 成功 → 窗口底透明让真模糊透上来；失败（老系统/RDP）→ 回退内置柔渐变底，绝不黑屏。
        // 视觉终案（试A 判决：raw 官方 API／旧 API／Loaded 时机／DwmExtendFrameIntoClientArea 四种配置，
        // 红蓝底实验均证明客户区拿不到 OS 桌面透视，ExtendFrame 还会弄坏标题栏）：
        // 永久采用 mockup 同款路线——窗内自绘柔彩底 + 分层半透明面板；全环境一致、可截图验证、无黑屏。
        // TryApplyLiveGlass/ApplyAcrylic 保留在 WindowEffects 内作历史参考，主窗不启用。
        if (PresentationSource.FromVisual(this) is HwndSource src) src.AddHook(WndProc);
        ApplyWorkAreaClamp();
    }

    /// <summary>WM_DPICHANGED / WM_GETMINMAXINFO：跨显示器后重新夹取，并限制"拖拽缩放上限"。</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DPICHANGED = 0x02E0;
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_DPICHANGED)
        {
            Dispatcher.BeginInvoke(new Action(ApplyWorkAreaClamp), DispatcherPriority.Background);
        }
        else if (msg == WM_GETMINMAXINFO && TryGetWorkArea(hwnd, out var work))
        {
            // 用 Win32 的 MINMAXINFO（设备像素）**只限制"拖拽缩放上限"**：
            //   · 不动 ptMinTrackSize（那是 WPF 按 MinWidth/MinHeight 维护的）；
            //   · 不自己指定 ptMaxSize/ptMaxPosition —— 实测（即便按真实边框补偿）反而会把最大化窗口推偏 9px；
            //     只要不在正常路径写 WPF 的 Left/Top（改用 SetWindowPos 移动窗口），
            //     系统默认的最大化几何就是正确的（A/B：可视边框 0,0-1920,1140，正好铺满工作区）。
            var mmi = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
            var w = work.Right - work.Left;
            var h = work.Bottom - work.Top;
            if (w > 0 && h > 0)
            {
                if (mmi.ptMaxTrackSize.x <= 0 || mmi.ptMaxTrackSize.x > w) mmi.ptMaxTrackSize.x = w;
                if (mmi.ptMaxTrackSize.y <= 0 || mmi.ptMaxTrackSize.y > h) mmi.ptMaxTrackSize.y = h;
                Marshal.StructureToPtr(mmi, lParam, false);
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>取窗口所在显示器的工作区（**设备像素**，与 WM_GETMINMAXINFO 同一坐标空间）。</summary>
    private static bool TryGetWorkArea(IntPtr hwnd, out NativeMethods.RECT work)
    {
        work = default;
        try
        {
            var mon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return false;
            var mi = NativeMethods.MONITORINFO.Create();
            if (!NativeMethods.GetMonitorInfo(mon, ref mi)) return false;
            work = mi.rcWork;
            return work.Right > work.Left && work.Bottom > work.Top;
        }
        catch { return false; }
    }

    /// <summary>
    /// 自适应（本次修复）：把窗口夹进「当前显示器的工作区」。
    /// 根因：窗口此前固定 1340×880、最小 1120×740；1366×768 这类屏幕的工作区只有约 728px 高，
    /// 于是窗口比屏幕还高、最小高度也大于工作区 —— 底部状态栏（进度条 + 开始/暂停/停止/恢复）
    /// 整个跑到屏幕外，用户既看不到也拖不回来。
    /// 这里按工作区收窄 MinWidth/MinHeight、把初始/拖屏后的尺寸夹进可见区，并在窗口超出可见区时用 SetWindowPos 挪回；
    /// 拖拽缩放上限由 WM_GETMINMAXINFO 的 ptMaxTrackSize 限制（见 WndProc）。
    /// 纯窗口尺寸计算，不涉及任何迁移/状态逻辑；失败时静默保留原尺寸（绝不影响功能）。
    /// </summary>
    private void ApplyWorkAreaClamp()
    {
        if (_clamping) return;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (!TryGetWorkArea(hwnd, out var work)) return;

            // 设备像素 → 设备无关单位（DIP）：在 125%/150% 缩放下也算得对
            var dpi = VisualTreeHelper.GetDpi(this);
            var sx = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
            var sy = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
            var wa = new Rect(
                work.Left / sx, work.Top / sy,
                (work.Right - work.Left) / sx,
                (work.Bottom - work.Top) / sy);
            if (wa.Width < 320 || wa.Height < 240) return;   // 读数异常：不动

            _clamping = true;
            // 工作区比设计最小值还小 → 把最小值也一起降下来，否则窗口必然超出屏幕
            MinWidth = Math.Max(320, Math.Min(DesignMinWidth, Math.Floor(wa.Width)));
            MinHeight = Math.Max(240, Math.Min(DesignMinHeight, Math.Floor(wa.Height)));

            if (WindowState != WindowState.Normal) return;   // 最大化/最小化交给系统，不抢尺寸

            if (Width > wa.Width) Width = Math.Max(MinWidth, Math.Floor(wa.Width));
            if (Height > wa.Height) Height = Math.Max(MinHeight, Math.Floor(wa.Height));

            // 自适应收尾（最小干预优先）：
            //  ① 先尝试"完全不挪窗口位置"就能放下的办法：把尺寸压到「当前位置到工作区边缘」之间（不低于 MinWidth/MinHeight）。
            //     大屏、或系统层叠偏移不大时这一步就够了，而且**不碰窗口位置** → 最大化几何保持系统默认
            //     （A/B 实测：不移动窗口时，最大化可视边框 0,0-1920,1140，正好铺满工作区）。
            //  ② 只有压到下限仍放不下（小屏 + 系统层叠位置很低）才用 SetWindowPos 把窗口挪回可见区 ——
            //     否则底栏（进度条 + 开始/暂停/停止/恢复）会落在屏幕外，这正是用户报的问题。
            //     已知代价（如实记录）：一旦发生过程序化移动，WPF 会自行改写 ptMaxSize/ptMaxPosition，
            //     之后最大化该窗口时可视边框会越过工作区右下各 9px；
            //     此时底栏四个按钮与进度条仍完整在屏内（UIA 逐项确认 bottomButtons=4 / 进度条 inWin=True）。
            if (double.IsNaN(Left) || double.IsNaN(Top)) return;
            var availW = wa.Right - Left;    // 当前左边缘到工作区右边还剩多少
            var availH = wa.Bottom - Top;    // 当前上边缘到工作区下边还剩多少
            if (availW > 0 && availH > 0)
            {
                if (Width > availW && availW >= MinWidth) Width = Math.Floor(availW);
                if (Height > availH && availH >= MinHeight) Height = Math.Floor(availH);
            }
            var outside = Left < wa.Left - 0.5 || Top < wa.Top - 0.5
                          || Left + Width > wa.Right + 0.5 || Top + Height > wa.Bottom + 0.5;
            if (outside)
            {
                var left = Math.Min(Math.Max(Left, wa.Left), Math.Max(wa.Left, wa.Right - Width));
                var top = Math.Min(Math.Max(Top, wa.Top), Math.Max(wa.Top, wa.Bottom - Height));
                NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
                    (int)Math.Round(left * sx), (int)Math.Round(top * sy), 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            }
        }
        catch { /* 夹取失败绝不影响功能：窗口按原尺寸显示 */ }
        finally { _clamping = false; }
    }

    /// <summary>
    /// 页面过渡：淡入 + 轻微上移（180~200ms，ease-out）。苹果那种"看得见但不抢戏"的动效，
    /// 只动透明度和一个 10px 位移，不做花哨效果。
    /// </summary>
    private int _lastStep;

    private void AnimateStepChange()
    {
        var step = (DataContext as MainViewModel)?.CurrentStep ?? 0;
        FrameworkElement? page = step switch
        {
            0 => PageConnect,
            1 => PageSelect,
            2 => PageTransfer,
            _ => PageResult
        };
        if (page == null) return;
        var dir = step >= _lastStep ? 1 : -1;   // 前进从右滑入、后退从左滑入
        _lastStep = step;
        page.Opacity = 0;
        var tt = new TranslateTransform(36 * dir, 0);
        page.RenderTransform = tt;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        page.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(36 * dir, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
    }

    // 顶栏「更新日志」→ 打开应用内更新日志窗口（内容为嵌入资源，另有随包 TXT 可用记事本看）
    private void Changelog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // v0.3.3 起不再套用亚克力与弹出动效（视觉回退）。
            // 注意：这里必须只有「建窗口 + ShowDialog」两步——曾因为把 SourceInitialized/Loaded 的
            // 空 lambda 接在 ShowDialog 前面，三行被编译器接成
            //   w.SourceInitialized += (_, _) => (w.Loaded += ((_, _) => w.ShowDialog()));
            // 于是 ShowDialog 只在 Loaded 时才会挂上，而窗口从没被显示过 → 点击「更新日志」毫无反应。
            App.Log?.Information("顶栏「更新日志」被点击：打开更新日志窗口");
            var w = new ChangelogWindow { Owner = this };
            w.ShowDialog();
            App.Log?.Information("更新日志窗口已关闭");
        }
        catch (Exception ex)
        {
            // 兜底：任何失败都必须让用户看得见，绝不静默。
            App.Log?.Error(ex, "打开更新日志窗口失败");
            try
            {
                AppDialog.Show(this, "无法打开更新日志：" + ex.Message, "PCMig 迁移工具",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { /* 连弹窗都失败时至少已经写进应用日志 */ }
        }
    }

    // 步骤导轨点击 → 切页（用 OneWay 绑定 + 本处理器：避免 ListBox 初始化时 SelectedIndex=-1 回写把步骤打乱）
    private bool _syncingStep;
    private void StepRail_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingStep) return;
        if (sender is not ListBox lb || lb.SelectedIndex < 0) return;
        if (DataContext is MainViewModel vm && vm.CurrentStep != lb.SelectedIndex)
        {
            _syncingStep = true;
            try { vm.CurrentStep = lb.SelectedIndex; }
            finally { _syncingStep = false; }
        }
    }

    // 目录树节点展开 → 懒加载子内容（子目录 + 文件）
    private void DirNode_Expanded(object sender, RoutedEventArgs e)
    {
        if ((e.OriginalSource as TreeViewItem)?.DataContext is DirNode node)
            _ = (DataContext as MainViewModel)?.EnsureChildrenAsync(node);
    }

    // 勾选框点击：手动接管（绕过 WPF 三态循环顺序），统一为“非全选→全选；全选→全不选”的级联语义
    private void NodeCheck_Toggle(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        ToggleNode((sender as FrameworkElement)?.DataContext);
    }

    private void NodeCheck_KeyToggle(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Space) return;
        e.Handled = true;
        ToggleNode((sender as FrameworkElement)?.DataContext);
    }

    private static void ToggleNode(object? dataContext)
    {
        switch (dataContext)
        {
            case DirNode d: d.ToggleFromUi(); break;
            case FileRow f: f.IsChecked = !f.IsChecked; break;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.IsRunning)
        {
            var r = AppDialog.Show(this,
                "迁移正在进行中。关闭窗口将立即停止传输（已传部分保留，下次可断点续传）。\n确定关闭吗？",
                "PCMig", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }
        (DataContext as MainViewModel)?.OnClosing();
        base.OnClosing(e);
    }
}
