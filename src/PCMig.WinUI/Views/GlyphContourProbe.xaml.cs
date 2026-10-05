using System;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace PCMig.WinUI.Views;

/// <summary>
/// ★ PHASE 5B / 用户 2026-10-05 追加纠正：字形顶部轮廓（Glyph Contour）专项探针 ★
///
/// 背景：用户进一步确认问题不是"顶部留白不够"，而是**字形顶部轮廓本身被拍平** ——
/// 数字 2 与字母 G 的顶部弧线变成水平线。旧主线（Padding / LineHeight / 整体下移）
/// 因此被判定为修错了层。
///
/// 已确认的文字层事实（WPF 字体枚举 + 逐列墨迹轮廓，本机 Windows）：
///   Microsoft YaHei UI 只注册 290 (MSYHL.TTC) / 400 (MSYH.TTC) / 700 (MSYHBD.TTC)，
///   **没有 SemiBold(600)**；请求 600 时向上取整命中 700 (Bold)。
///   20 px 下 '2' 的顶部墨迹跨度：400 = 12 行（弧度保留），600→700 = 2 行（被压成水平线）；
///   200 px 下同一字形 400 = 147 行、700 = 139 行 ⇒ 字体轮廓完好，问题出在低字号的栅格化。
///
/// 本组件把"字重"作为唯一自变量，同屏渲染同一批字形，每格上方带一条洋红基准线，
/// 供逐列 top-profile 像素分析：顶部跨度大 + 首行墨迹窄 ⇒ 弧度保留；反之 ⇒ 被拍平。
///
/// 本组件不参与业务：无 ViewModel、无事件订阅、不写状态；只在
/// Environment.GetEnvironmentVariable("PCMIG_GLYPH_PROBE") == "1" 时被 MainWindow 挂载。
/// </summary>
public sealed partial class GlyphContourProbe : UserControl
{
    /// <summary>字重变体：Key 用于 AutomationId，Note 说明"请求值 → 实际命中字面"。</summary>
    private sealed record WeightVariant(string Key, string Note, Windows.UI.Text.FontWeight Weight);

    private static readonly WeightVariant[] WeightVariants =
    [
        new("W290", "Light 300  ⇒ YaHei 命中 290 (MSYHL.TTC)",
            FontWeights.Light),
        new("W400", "Normal 400 ⇒ YaHei 命中 400 (MSYH.TTC)   ← 弧度保留（对照基准）",
            FontWeights.Normal),
        new("W500", "Medium 500 ⇒ YaHei 命中 400",
            FontWeights.Medium),
        new("W600", "SemiBold 600 ⇒ YaHei 命中 700 (MSYHBD.TTC)   ★生产 PCMigTextStatValue 请求的就是 600",
            FontWeights.SemiBold),
        new("W700", "Bold 700   ⇒ YaHei 命中 700 (MSYHBD.TTC)",
            FontWeights.Bold),
    ];

    /// <summary>用户点名的两个字形排在前面：'2'（顶部应为弧）与 'G'（顶部应为弧）。</summary>
    private static readonly string[] Glyphs = ["2", "G", "5", "S", "3"];

    /// <summary>字体族变体：同屏对照"有真 600 字面的字体"与 YaHei UI 的差别。</summary>
    private sealed record FamilyVariant(string Key, string Note, string? Family);

    private static readonly FamilyVariant[] FamilyVariants =
    [
        new("FDEF", "不设 FontFamily（系统默认 UI 字体）/ SemiBold 600", null),
        new("FYH", "Microsoft YaHei UI / SemiBold 600", "Microsoft YaHei UI"),
        new("FSV", "Segoe UI Variable Text / SemiBold 600（该族注册了真 600 字面）", "Segoe UI Variable Text"),
        new("FSU", "Segoe UI / SemiBold 600", "Segoe UI"),
    ];

    public GlyphContourProbe()
    {
        InitializeComponent();
        Build();
    }

    private void Build()
    {
        ProbeRoot.Children.Add(Header(
            "PHASE 5B · Glyph Contour Probe —— 字形顶部轮廓：字重是唯一自变量（PCMIG_GLYPH_PROBE=1）",
            14, Color.FromArgb(255, 200, 215, 255)));
        ProbeRoot.Children.Add(Header(
            "判读：每格上方有一条洋红基准线（GlyphLine_*），紧贴字形行盒顶；下方是白色字形（Glyph_*）。" +
            "像素分析逐列找第一条墨迹行 ⇒ 「顶部跨度 topRange」与「首行墨迹宽度占比」。" +
            "topRange 大且占比小 = 顶部弧线完好；topRange 小且占比大 = 顶部被拍平成一条水平线。",
            11, Color.FromArgb(255, 150, 160, 180)));

        // ── A. 字重矩阵 @ 生产字号 20 ───────────────────────────────────────────
        ProbeRoot.Children.Add(Header("A. 字重矩阵 @ FontSize 20（生产字号）· Microsoft YaHei UI", 13,
            Color.FromArgb(255, 255, 214, 140)));
        foreach (var variant in WeightVariants)
        {
            ProbeRoot.Children.Add(BuildWeightRow(variant, fontSize: 20d, glyphs: Glyphs, tag: "20"));
        }

        // ── B. 同一批字重 @ 大字号 96（证明字体轮廓本身完好）────────────────────
        ProbeRoot.Children.Add(Header(
            "B. 同一批字重 @ FontSize 96（对照：字体轮廓本身完好，问题只在低字号栅格化）· Microsoft YaHei UI", 13,
            Color.FromArgb(255, 255, 214, 140)));
        foreach (var variant in WeightVariants)
        {
            ProbeRoot.Children.Add(BuildWeightRow(variant, fontSize: 96d, glyphs: ["2", "G"], tag: "96"));
        }

        // ── C. 字体族对照 @ 20 / SemiBold 600 ──────────────────────────────────
        ProbeRoot.Children.Add(Header(
            "C. 字体族对照 @ FontSize 20 / SemiBold 600（哪些字体族真正注册了 600 字面）", 13,
            Color.FromArgb(255, 255, 214, 140)));
        foreach (var variant in FamilyVariants)
        {
            ProbeRoot.Children.Add(BuildFamilyRow(variant, fontSize: 20d, glyphs: Glyphs));
        }

        // ── D. UseLayoutRounding 对照 @ 20 / SemiBold 600 ──────────────────────
        ProbeRoot.Children.Add(Header(
            "D. UseLayoutRounding 对照 @ FontSize 20 / SemiBold 600 · Microsoft YaHei UI", 13,
            Color.FromArgb(255, 255, 214, 140)));
        foreach (var lr in new[] { false, true })
        {
            ProbeRoot.Children.Add(BuildRoundingRow(lr, fontSize: 20d, glyphs: Glyphs));
        }

        // ── E. 生产样式对照 @ 20（改生产字重前后可逐像素对比）──────────────────
        // 本段不显式设 FontWeight：字形完全继承 Style="{StaticResource PCMigTextStatValue}"，
        // 因此它渲染出来的字重就是生产环境的字重。修改生产样式前后各截一次本段，
        // 即可在同一坐标系里逐列对比"顶部轮廓是否真的变好"，而不必跑完整迁移任务。
        ProbeRoot.Children.Add(Header(
            "E. 生产样式对照 @ FontSize 20 · Style=PCMigTextStatValue" +
            "（该样式的 FontWeight 就是生产值；AutomationId 前缀 STYLE）" +
            "—— 用于在修改生产字重前后逐像素对比顶部轮廓", 13,
            Color.FromArgb(255, 255, 214, 140)));
        ProbeRoot.Children.Add(BuildProductionStyleRow());
    }

    /// <summary>
    /// E 段：直接用生产样式 <c>PCMigTextStatValue</c> 渲染字形，AutomationId = <c>Glyph_STYLE_20_&lt;i&gt;</c>。
    /// 故意不设置 FontWeight / FontSize / FontFamily —— 让它们全部来自生产样式，
    /// 这样"改样式前 vs 改样式后"两张探针截图才具备可比性。
    /// </summary>
    private FrameworkElement BuildProductionStyleRow()
    {
        Style? style = null;
        string styleNote;
        try
        {
            if (Application.Current.Resources.TryGetValue("PCMigTextStatValue", out var raw) && raw is Style s)
            {
                style = s;
                styleNote = string.Empty;
            }
            else
            {
                styleNote = "  [STYLE-MISSING: 字形将退回默认样式]";
            }
        }
        catch
        {
            styleNote = "  [STYLE-ERROR: 字形将退回默认样式]";
        }

        var caption = Header(
            "STYLE · PCMigTextStatValue（FontWeight 来自应用资源，未在本段覆盖）" + styleNote, 12,
            Color.FromArgb(255, 140, 200, 255));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        for (var i = 0; i < Glyphs.Length; i++)
        {
            var value = new TextBlock
            {
                Text = Glyphs[i],
                TextWrapping = TextWrapping.NoWrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                // 深底上必须显式给浅色前景（应用默认 Light 主题的近黑前景不可见）。
                Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            };
            if (style is not null)
            {
                value.Style = style;
            }
            AutomationProperties.SetAutomationId(value, $"Glyph_STYLE_20_{i}");

            var baseline = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromArgb(255, 255, 0, 170)),
            };
            AutomationProperties.SetAutomationId(baseline, $"GlyphLine_STYLE_20_{i}");

            var grid = new Grid { Width = Math.Max(44d, Math.Ceiling(20d * 2.4d)) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(baseline, 0);
            Grid.SetRow(value, 1);
            grid.Children.Add(baseline);
            grid.Children.Add(value);
            row.Children.Add(grid);
        }

        return Wrap(caption, row);
    }

    private static TextBlock Header(string text, double size, Color color) => new()
    {
        Text = text,
        FontSize = size,
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(color),
    };

    private FrameworkElement BuildWeightRow(WeightVariant variant, double fontSize, string[] glyphs, string tag)
    {
        var caption = Header($"{variant.Key} · {variant.Note}", 12, Color.FromArgb(255, 140, 200, 255));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        var cells = BuildGlyphRow(
            glyphs, fontSize,
            idPrefix: variant.Key + "_" + tag,
            family: "Microsoft YaHei UI",
            weight: variant.Weight,
            layoutRounding: true);
        foreach (var cell in cells) { row.Children.Add(cell); }
        return Wrap(caption, row);
    }

    private FrameworkElement BuildFamilyRow(FamilyVariant variant, double fontSize, string[] glyphs)
    {
        var caption = Header($"{variant.Key} · {variant.Note}", 12, Color.FromArgb(255, 140, 200, 255));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        var cells = BuildGlyphRow(
            glyphs, fontSize,
            idPrefix: variant.Key + "_20",
            family: variant.Family,
            weight: FontWeights.SemiBold,
            layoutRounding: true);
        foreach (var cell in cells) { row.Children.Add(cell); }
        return Wrap(caption, row);
    }

    private FrameworkElement BuildRoundingRow(bool layoutRounding, double fontSize, string[] glyphs)
    {
        var caption = Header(
            $"LR-{layoutRounding} · UseLayoutRounding={layoutRounding} / SemiBold 600", 12,
            Color.FromArgb(255, 140, 200, 255));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        var cells = BuildGlyphRow(
            glyphs, fontSize,
            idPrefix: "LR" + (layoutRounding ? "1" : "0") + "_20",
            family: "Microsoft YaHei UI",
            weight: FontWeights.SemiBold,
            layoutRounding: layoutRounding);
        foreach (var cell in cells) { row.Children.Add(cell); }
        return Wrap(caption, row);
    }

    private static FrameworkElement Wrap(FrameworkElement caption, FrameworkElement row)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(caption);
        stack.Children.Add(row);
        return stack;
    }

    /// <summary>
    /// 一个字形一格：Row0 = 1 DIP 洋红基准线（线底 == Body 顶），Row1 = 字形本身。
    /// 每格固定宽度，避免相邻字形在逐列像素分析里互相污染。
    /// </summary>
    private static FrameworkElement[] BuildGlyphRow(
        string[] glyphs,
        double fontSize,
        string idPrefix,
        string? family,
        Windows.UI.Text.FontWeight weight,
        bool layoutRounding)
    {
        var cellWidth = Math.Max(44d, Math.Ceiling(fontSize * 2.4d));
        var cells = new FrameworkElement[glyphs.Length];
        for (var i = 0; i < glyphs.Length; i++)
        {
            var glyph = glyphs[i];
            var value = new TextBlock
            {
                Text = glyph,
                FontSize = fontSize,
                FontWeight = weight,
                UseLayoutRounding = layoutRounding,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                // 探针跑在深色底上：应用默认主题是 Light 时 TextBlock 默认前景近黑，
                // 在 #FF0B0E13 上完全不可见（首轮 MetricTypographyProbe 已被这一点坑过）。
                // 注意：WinUI 3 里不要用 Colors.White —— Windows.UI 命名空间在 WinUI 3 下
                // 不提供 Colors 类（error CS0103），统一用 Color.FromArgb。
                Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            };
            if (family is not null)
            {
                value.FontFamily = new FontFamily(family);
            }
            AutomationProperties.SetAutomationId(value, $"Glyph_{idPrefix}_{i}");

            var baseline = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromArgb(255, 255, 0, 170)),
            };
            AutomationProperties.SetAutomationId(baseline, $"GlyphLine_{idPrefix}_{i}");

            var grid = new Grid { Width = cellWidth };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(baseline, 0);
            Grid.SetRow(value, 1);
            grid.Children.Add(baseline);
            grid.Children.Add(value);
            cells[i] = grid;
        }
        return cells;
    }
}