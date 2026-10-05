using System;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace PCMig.WinUI.Views;

/// <summary>
/// ★ PHASE 5 / 用户指令 §6：Metric Typography 隔离探针（临时诊断组件）★
///
/// 目的：把"统计卡大号数值顶部是否被削"从"我看着没问题"变成**可测像素事实**。
/// 方案 §6 要求同一屏渲染六个变体（同一批字符串），并区分：
///   · 隔离 TextBlock 就已削顶  ⇒ 字体 / 字重 / 光栅化问题（改生产样式）
///   · 隔离正确但生产卡不正确  ⇒ 父级布局 / Clip / 高度问题（改父级）
///
/// 已核实的字体事实（WPF 字体枚举，本机 Windows）：
///   Microsoft YaHei UI 只有 Light(290) / Normal(400) / Bold(700) —— **没有 SemiBold(600)**。
///   而生产样式 PCMigTextStatValue 声明 FontWeight="SemiBold" ⇒ 必然发生字重回退/合成。
///   变体 A/B/C/D/E 就是围绕这一点做对照：SemiBold 对照 Normal、YaHei UI 对照 Segoe UI Variable Text。
///
/// 本组件不参与业务：无 ViewModel、无事件订阅、不写状态；只在
/// Environment.GetEnvironmentVariable("PCMIG_METRIC_PROBE") == "1" 时被 MainWindow 挂载。
/// </summary>
public sealed partial class MetricTypographyProbe : UserControl
{
    /// <summary>一个渲染变体：决定字体族、字重、布局取整、以及是否使用生产样式。</summary>
    private sealed record Variant(
        string Key,
        string Note,
        string? Family,
        Windows.UI.Text.FontWeight Weight,
        bool LayoutRounding,
        bool ProductionStyle);

    private static readonly Variant[] Variants =
    [
        new("A", "Microsoft YaHei UI / SemiBold / 20 / 自然行高 / UseLayoutRounding=False",
            "Microsoft YaHei UI", FontWeights.SemiBold, false, false),
        new("B", "Microsoft YaHei UI / SemiBold / 20 / 自然行高 / UseLayoutRounding=True",
            "Microsoft YaHei UI", FontWeights.SemiBold, true, false),
        new("C", "Microsoft YaHei UI / Normal / 20（对照 SemiBold）",
            "Microsoft YaHei UI", FontWeights.Normal, true, false),
        new("D", "系统默认 UI 字体 / SemiBold / 20（不设 FontFamily）",
            null, FontWeights.SemiBold, true, false),
        new("E", "Segoe UI Variable Text / SemiBold / 20（拉丁+数字，CJK 回退）",
            "Segoe UI Variable Text", FontWeights.SemiBold, true, false),
        new("F", "生产样式 PCMigTextStatValue + MinHeight=34 / Padding 0,3,0,3 (GridView host)",
            null, FontWeights.SemiBold, true, true),
    ];

    /// <summary>§6 指定必须覆盖的真实字符串：混合中文 + 数字 + 大写英文 + 斜杠。</summary>
    private static readonly string[] Samples =
    [
        "5.04 GB/s", "约 18 秒", "2/30", "83.66 GB / 176.9 GB",
        "2.07 GiB/s", "约 1 分 15 秒", "20.84 GiB / 176.9 GB",
    ];

    public MetricTypographyProbe()
    {
        InitializeComponent();
        Build();
    }

    private void Build()
    {
        ProbeRoot.Children.Add(new TextBlock
        {
            Text = "PHASE 5 · Metric Typography Probe —— 变体 A–F 同屏隔离渲染（PCMIG_METRIC_PROBE=1）",
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Colors.White),
        });
        ProbeRoot.Children.Add(new TextBlock
        {
            Text = "读法：每张样本上方有一条洋红基准线（ProbeLine_<变体>_<序号>），紧贴被测量 Body 顶部；" +
                   "像素分析中它与第一条字形墨迹行的距离 = 行盒顶净空。净空 ≤ 1 物理像素或墨迹顶呈水平平切 ⇒ 该变体被削顶。",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 150, 160, 180)),
        });

        foreach (var variant in Variants)
        {
            ProbeRoot.Children.Add(BuildVariantRow(variant));
        }
    }

    private FrameworkElement BuildVariantRow(Variant variant)
    {
        var container = new StackPanel { Spacing = 8 };
        container.Children.Add(new TextBlock
        {
            Text = $"{variant.Key} · {variant.Note}",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 140, 200, 255)),
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        for (var i = 0; i < Samples.Length; i++)
        {
            row.Children.Add(BuildSample(variant, i));
        }

        container.Children.Add(row);
        return container;
    }

    private FrameworkElement BuildSample(Variant variant, int index)
    {
        var value = new TextBlock
        {
            Text = Samples[index],
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            // 探针跑在深色底（#FF0B0E13）上，必须显式给浅色前景：
            // 应用默认主题是 Light 时，TextBlock 的默认前景是近黑色 ⇒ 在深底上完全不可见
            // （首轮真机取证就被这一点坑过：所有样本像素 luma == 背景 luma，探针等于空白）。
            // F 变体随后由生产样式覆盖 Foreground，这里只是 A–E 的隔离测量基准。
            Foreground = new SolidColorBrush(Colors.White),
        };
        AutomationProperties.SetAutomationId(value, $"Probe_{variant.Key}_{index}");

        if (variant.ProductionStyle)
        {
            // F：完全照搬生产结构（样式 + MinHeight host + 上下 3 DIP Padding），
            // 用来判断"生产卡不正确"时是否父级/宿主造成。
            value.Style = (Style)Application.Current.Resources["PCMigTextStatValue"];
            // 生产样式里 Foreground 走 StaticResource，探针的深色底上可能仍是深色 ⇒ 取证时强制浅色，
            // 只影响探针内的可见性，不改生产样式的任何属性。
            value.Foreground = new SolidColorBrush(Colors.White);
        }
        else
        {
            value.FontSize = 20;
            value.FontWeight = variant.Weight;
            value.UseLayoutRounding = variant.LayoutRounding;
            // 显式字体族（D 变体故意不设，用于观察系统默认 UI 字体的行为）。
            if (variant.Family is not null)
            {
                value.FontFamily = new FontFamily(variant.Family);
            }
        }

        FrameworkElement body = value;
        if (variant.ProductionStyle)
        {
            var host = new Grid { MinHeight = 34, Padding = new Thickness(0, 3, 0, 3) };
            host.Children.Add(value);
            body = host;
        }

        // 基准线紧贴 body 顶部：线自身 1 DIP 高，Body 在下一行 ⇒ 线底 == Body 顶。
        var probe = new Grid();
        probe.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        probe.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var baseline = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromArgb(255, 255, 0, 170)),
        };
        AutomationProperties.SetAutomationId(baseline, $"ProbeLine_{variant.Key}_{index}");
        Grid.SetRow(baseline, 0);
        Grid.SetRow(body, 1);
        probe.Children.Add(baseline);
        probe.Children.Add(body);
        return probe;
    }
}