using System;
using Microsoft.UI.Xaml.Data;
using PCMig.Core.Jobs;

namespace PCMig.WinUI.Presentation.Converters;

/// <summary>
/// A7.1（2026-09-30）Task Picker 行内文本：**绑定项本身 + 用 ConverterParameter 取字段**。
///
/// 为什么不用 <c>{Binding JobId}</c> 这类路径绑定：
///   · 本页有实测记录——模板内的路径绑定在这套组合下**静默绑不上**（名字全空），
///     项目既有做法一律是"绑定对象 + 显式转换器"（见本页 RowName/RowChecked/RowSize 与 TreeRowConverters）；
///   · 另：WinUI 经典 <c>{Binding}</c> 不支持 StringFormat，百分比格式也需要转换器。
/// 只做取字段与格式化：**纯显示层、无业务判定、不碰任何状态**；参数不认识或类型不符一律返回空串。
/// </summary>
public sealed class TaskFieldConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not JobSummary job) return string.Empty;
        return (parameter as string) switch
        {
            "JobId" => job.JobId,
            "PhaseText" => job.PhaseText,
            "Percent" => $"{job.Percent:0.0}%",
            _ => string.Empty,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}