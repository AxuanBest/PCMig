using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PCMig.Gui;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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
                MessageBox.Show(this, "无法打开更新日志：" + ex.Message, "PCMig 迁移工具",
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
            var r = MessageBox.Show(this,
                "迁移正在进行中。关闭窗口将立即停止传输（已传部分保留，下次可断点续传）。\n确定关闭吗？",
                "PCMig", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }
        (DataContext as MainViewModel)?.OnClosing();
        base.OnClosing(e);
    }
}
