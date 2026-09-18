using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PCMig.Gui;

/// <summary>
/// MessageBox 的应用内替代：外观完全走 App 级 Design System（Theme/Controls.xaml），
/// 保证「主窗口是新版、弹窗也一定是新版」。
///
/// 关键约定：<see cref="Show"/> 的参数与返回值和 <c>MessageBox.Show</c> 完全一致（含 6 参数重载），
/// 因此调用点只把 <c>MessageBox.Show</c> 换成 <c>AppDialog.Show</c> 即可，
/// 不需要改动任何判断分支——业务逻辑零变化，只是不再出现系统默认样式的窗口。
/// </summary>
public partial class AppDialog : Window
{
    private MessageBoxResult _defaultResult = MessageBoxResult.None;

    private AppDialog()
    {
        InitializeComponent();
    }

    /// <summary>与 MessageBox.Show 语义完全一致的替代（含默认按钮参数，返回值可直接参与原有判断）。</summary>
    public static MessageBoxResult Show(
        Window? owner,
        string messageBoxText,
        string caption = "PCMig",
        MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var dlg = new AppDialog { _defaultResult = defaultResult };
        if (owner != null && owner.IsLoaded && owner.IsVisible)
        {
            try { dlg.Owner = owner; } catch { /* 无主窗口时仍可独立显示 */ }
        }
        dlg.ApplyContent(messageBoxText, caption, button, icon);
        dlg.ShowDialog();
        return dlg.Result;
    }

    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    private void ApplyContent(string message, string caption, MessageBoxButton button, MessageBoxImage icon)
    {
        MessageText.Text = message ?? string.Empty;
        CaptionText.Text = string.IsNullOrWhiteSpace(caption) ? "PCMig" : caption;

        // 语义色：与主窗口同一套 token，不另造颜色
        string brushKey;
        string glyph;
        switch (icon)
        {
            case MessageBoxImage.Error:
                brushKey = "ErrorTextColor"; glyph = "!"; break;
            case MessageBoxImage.Warning:
                brushKey = "WarnTextColor"; glyph = "!"; break;
            case MessageBoxImage.Question:
                brushKey = "AccentColorBrush"; glyph = "?"; break;
            default:
                brushKey = "AccentColorBrush"; glyph = "i"; break;
        }
        var brush = (Brush)FindResource(brushKey);
        IconRing.BorderBrush = brush;
        IconGlyph.Foreground = brush;
        IconGlyph.Text = glyph;

        BtnOk.Visibility = Visibility.Collapsed;
        BtnCancel.Visibility = Visibility.Collapsed;
        BtnYes.Visibility = Visibility.Collapsed;
        BtnNo.Visibility = Visibility.Collapsed;

        switch (button)
        {
            case MessageBoxButton.OKCancel:
                BtnOk.Visibility = Visibility.Visible;
                BtnCancel.Visibility = Visibility.Visible;
                break;
            case MessageBoxButton.YesNo:
                BtnYes.Visibility = Visibility.Visible;
                BtnNo.Visibility = Visibility.Visible;
                break;
            case MessageBoxButton.YesNoCancel:
                BtnYes.Visibility = Visibility.Visible;
                BtnNo.Visibility = Visibility.Visible;
                BtnCancel.Visibility = Visibility.Visible;
                break;
            default:
                BtnOk.Visibility = Visibility.Visible;
                break;
        }

        // 默认焦点：优先尊重调用方传入的默认按钮，否则落在主操作上；回车即可确认（与系统 MessageBox 一致）
        Loaded += (_, _) =>
        {
            Button? target = _defaultResult switch
            {
                MessageBoxResult.Yes when BtnYes.Visibility == Visibility.Visible => BtnYes,
                MessageBoxResult.No when BtnNo.Visibility == Visibility.Visible => BtnNo,
                MessageBoxResult.Cancel when BtnCancel.Visibility == Visibility.Visible => BtnCancel,
                MessageBoxResult.OK when BtnOk.Visibility == Visibility.Visible => BtnOk,
                _ => BtnYes.Visibility == Visibility.Visible ? BtnYes
                   : BtnOk.Visibility == Visibility.Visible ? BtnOk
                   : BtnNo.Visibility == Visibility.Visible ? BtnNo
                   : BtnCancel
            };
            try { target?.Focus(); } catch { }
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Finish(MessageBoxResult.OK);
    private void Cancel_Click(object sender, RoutedEventArgs e) => Finish(MessageBoxResult.Cancel);
    private void Yes_Click(object sender, RoutedEventArgs e) => Finish(MessageBoxResult.Yes);
    private void No_Click(object sender, RoutedEventArgs e) => Finish(MessageBoxResult.No);

    private void Finish(MessageBoxResult r)
    {
        Result = r;
        Close();
    }
}