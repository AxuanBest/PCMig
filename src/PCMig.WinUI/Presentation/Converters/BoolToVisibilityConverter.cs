using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace PCMig.WinUI.Presentation.Converters;

/// <summary>
/// bool → Visibility（true = Visible）。**只用于视图层显隐**，不参与任何业务判定。
///
/// 为什么需要它：目录树的三态显示拆成两个互斥元素——实心勾（CheckBox 的 CheckStateTrue）
/// 与半勾方块（Border 的可见性由 CheckStateIndeterminate 决定）。XAML 里 Border.Visibility
/// 需要一个转换器才能绑 bool；WinUI 没有内置的 bool→Visibility 隐式转换。
/// 转换器仅做机械映射，不含业务规则（状态语义仍由 DirNode 的三态模型负责）。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b && b ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>反向转换不支持（本页只用 OneWay）。</summary>
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException("BoolToVisibilityConverter 只支持 OneWay。");
}