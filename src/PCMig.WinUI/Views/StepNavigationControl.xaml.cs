using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PCMig.WinUI.Presentation;

namespace PCMig.WinUI.Views;

/// <summary>
/// 四步导航侧栏。
/// 只做两件事：把 <see cref="StepNavItem"/> 集合渲染成四张可选卡片；把点击冒泡成
/// <see cref="StepSelected"/> 事件交给 Shell（MainWindow）切换页面。
///
/// 刻意**不**在这里判断"能不能去"：业务可用性不属于导航层（见 StepNavItem 的说明）。
/// </summary>
public sealed partial class StepNavigationControl : UserControl
{
    public StepNavigationControl()
    {
        InitializeComponent();
    }

    /// <summary>导航状态由 Shell 注入（Shell 拥有唯一实例，四个页面共用同一份）。</summary>
    public StepNavigation Nav
    {
        get => (StepNavigation)GetValue(NavProperty);
        set => SetValue(NavProperty, value);
    }

    public static readonly DependencyProperty NavProperty =
        DependencyProperty.Register(
            nameof(Nav),
            typeof(StepNavigation),
            typeof(StepNavigationControl),
            new PropertyMetadata(null, OnNavChanged));

    private static void OnNavChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is StepNavigationControl c && c.Nav is not null)
        {
            // ItemsSource 由 x:Bind 负责；这里只保证初次绑定时数据已就绪。
            c.Bindings.Update();
        }
    }

    /// <summary>用户点了某一步。Shell 据此切换 ContentHost 的当前页。</summary>
    public event EventHandler<StepKind>? StepSelected;

    private void StepCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StepNavItem item })
        {
            StepSelected?.Invoke(this, item.Kind);
        }
    }
}