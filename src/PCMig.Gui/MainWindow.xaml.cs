using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

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
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.IsRunning)) RestartWave();
                };
            }
        };
        WaveHost.SizeChanged += (_, _) => RestartWave();
    }

    // 进度条光波：仅在传输运行时，一道光带从左扫到右（循环），宽度自适应容器
    private void RestartWave()
    {
        WaveSlide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        if (DataContext is not MainViewModel { IsRunning: true }) return;
        var w = WaveHost.ActualWidth;
        if (w <= 0) return;
        WaveSlide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
            new System.Windows.Media.Animation.DoubleAnimation(-130, w + 20, TimeSpan.FromSeconds(1.4))
            {
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                EasingFunction = new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
            });
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
            var r = MessageBox.Show(
                "迁移正在进行中。关闭窗口将立即停止传输（已传部分保留，下次可断点续传）。\n确定关闭吗？",
                "PCMig", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }
        (DataContext as MainViewModel)?.OnClosing();
        base.OnClosing(e);
    }
}
