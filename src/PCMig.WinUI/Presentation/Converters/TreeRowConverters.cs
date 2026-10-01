using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Controls;

namespace PCMig.WinUI.Presentation.Converters;

/// <summary>
/// 目录树**一行内容**的取值助手 + 四个 OneWay 转换器。
///
/// 【为什么用显式转换器而不是绑定路径】
/// 本包真机实测（截图/日志留证），在这套组合下绑定路径解析不可靠：
///   · 模板里写 <c>{Binding Name}</c> → 名字全空（复选框与展开箭头都在）；
///   · 模板里写 <c>{Binding Content}</c> → 显示成 "PCMig.WinUI.Presentation.DirNode" 这种类型名；
///   · 嵌套路径 <c>{Binding Content.Name}</c> 在本项目 WinUI 页面上同样不可靠。
/// 而 <c>{Binding Converter=...}</c>（不带路径）会把**整个 DataContext**交给转换器，
/// 行为完全由 C# 决定、可读可调试。节点模式下这个 DataContext 就是 TreeViewNode
/// （或页面直接放进去的 DirNode / FileRow），下面的取值逻辑把三种形态都兜住。
/// 纯取值/翻译，不含任何业务规则（三态语义仍由 DirTreeModels.cs 唯一负责）。
/// </summary>
public static class TreeRowConverters
{
    /// <summary>把一行内容取成 DirNode（TreeViewNode 会再取一层 Content）。</summary>
    public static DirNode? AsDirectory(object? value) => value switch
    {
        DirNode d => d,
        FileRow => null,
        TreeViewNode n => AsDirectory(n.Content),
        _ => null,
    };

    /// <summary>把一行内容取成 FileRow。</summary>
    public static FileRow? AsFile(object? value) => value switch
    {
        FileRow f => f,
        DirNode => null,
        TreeViewNode n => AsFile(n.Content),
        _ => null,
    };
}

/// <summary>一行内容 → 显示名（目录名 / 文件名；占位行 → 空串）。</summary>
public sealed class TreeRowNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => TreeRowConverters.AsDirectory(value)?.Name ?? TreeRowConverters.AsFile(value)?.Name ?? string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException("TreeRowNameConverter 只支持 OneWay。");
}

/// <summary>一行内容 → 是否全勾（目录行读三态模型的 true，文件行读它的布尔值）。
/// ★ A.5：XAML 绑定已改为显式路径 <c>Content.CheckStateTrue</c>（空路径在 TreeViewNode 上不刷新，
/// 见 Step2SelectDataPage.xaml 内注释），因此这里额外支持**直接传入 bool** 的输入形态。</summary>
public sealed class TreeRowCheckedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b ? b
           : TreeRowConverters.AsDirectory(value)?.CheckStateTrue
             ?? TreeRowConverters.AsFile(value)?.CheckStateTrue
             ?? false;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException("TreeRowCheckedConverter 只支持 OneWay。");
}

/// <summary>一行内容 → 是否半勾（半勾方块可见）。文件行恒 false。
/// ★ A.5：同上，支持直接传入 bool（<c>Content.CheckStateIndeterminate</c>）。</summary>
public sealed class TreeRowIndeterminateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b ? b
           : TreeRowConverters.AsDirectory(value)?.CheckStateIndeterminate ?? false;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException("TreeRowIndeterminateConverter 只支持 OneWay。");
}

/// <summary>一行内容 → 文件体积文案（目录行 / 占位行 → 空串）。</summary>
public sealed class TreeRowSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => TreeRowConverters.AsFile(value)?.SizeText ?? string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException("TreeRowSizeConverter 只支持 OneWay。");
}