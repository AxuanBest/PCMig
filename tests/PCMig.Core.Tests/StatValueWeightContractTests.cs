using System;
using System.IO;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ PHASE 5B（2026-10-05）统计卡大号数值的**字重**契约 ★
///
/// 背景：用户进一步澄清"大号数值顶部被削"不是留白不够，而是**字形顶部轮廓被拍平**
/// （数字 2 / 字母 G 的顶部弧线变成水平切线）。用真机字形轮廓探针
/// （<c>Views\GlyphContourProbe.xaml.cs</c>，环境变量 <c>PCMIG_GLYPH_PROBE=1</c>）
/// 以"逐列第一条墨迹行"量化后得到 96 DPI / Microsoft YaHei UI / FontSize 20 的实测值：
///
///              '2' topRange / ratio      'G' topRange / ratio      'S' topRange / ratio
///   Normal(400)      13 / 0.5000              5 / 0.5000              3 / 0.5556
///   Medium(500)      13 / 0.5000              5 / 0.5000              3 / 0.5556
///   SemiBold(600)    12 / 0.6000              5 / 0.5385              3 / 0.6667
///   Bold(700)        12 / 0.7000              4 / 0.6154              2 / 0.8000
///
/// 其中 topRange = 顶部墨迹跨多少行像素（越大＝弧线越完整），
/// firstRowRatio = 最顶那一行覆盖的墨迹列占比（越大＝顶部越像一条水平切线）。
/// 生产样式改前的渲染与探针 W600 行**逐字段完全相同**（topRange 12 / ratio 0.6000），
/// 改成 Normal 后与 W400 行**逐字段完全相同**（topRange 13 / ratio 0.5000）。
///
/// 这些契约测试锁死的是"结论"而不是"实现细节"：
///   1. 统计卡大号数值不得回到 SemiBold / Bold —— 真机实测这两档的顶部轮廓更平；
///   2. 该结论必须以注释形式留在样式旁，避免下一轮又被"看起来变细了"改回去；
///   3. 探针组件必须存在且保留"洋红基准线 + 逐格 AutomationId"这两个分析前提，
///      否则上面那组数字就无法被任何人重测。
/// </summary>
public sealed class StatValueWeightContractTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("未能定位仓库根目录（找不到 PCMig.sln）");
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var path = Path.Combine(FindRepoRoot(), Path.Combine(parts));
        Assert.True(File.Exists(path), $"文件必须存在：{path}");
        return File.ReadAllText(path);
    }

    private static string StatValueStyleBlock()
    {
        var typography = ReadRepoFile("src", "PCMig.WinUI", "Themes", "Typography.xaml");
        var start = typography.IndexOf("x:Key=\"PCMigTextStatValue\"", StringComparison.Ordinal);
        Assert.True(start > 0, "PCMigTextStatValue 必须存在");
        var end = typography.IndexOf("</Style>", start, StringComparison.Ordinal);
        Assert.True(end > start, "PCMigTextStatValue 的 </Style> 必须在其后");
        return typography[start..end];
    }

    [Fact]
    public void StatValueUsesNormalWeight()
    {
        var style = StatValueStyleBlock();

        // 真机轮廓实测：Normal(400) 的顶部跨度最大、顶行占比最小。
        Assert.Contains("<Setter Property=\"FontWeight\" Value=\"Normal\"/>", style);
    }

    [Fact]
    public void StatValueNeverUsesSemiBoldOrBold()
    {
        var style = StatValueStyleBlock();

        // 回归防护：SemiBold(600) 与 Bold(700) 在 20 DIP 下顶部轮廓单调变平
        // （'2' ratio 0.6000 / 0.7000，'S' ratio 0.6667 / 0.8000）。
        Assert.DoesNotContain("Value=\"SemiBold\"", style);
        Assert.DoesNotContain("Value=\"Bold\"", style);
    }

    [Fact]
    public void StatValueKeepsFontSize20AndNoMagicLineHeight()
    {
        var style = StatValueStyleBlock();

        // 字号与行高策略本轮都不动：只换字重，几何高度由 *ValueHost 的 MinHeight/Padding 保证。
        Assert.Contains("<Setter Property=\"FontSize\" Value=\"20\"/>", style);
        Assert.DoesNotContain("LineHeight", style);
        Assert.Contains("<Setter Property=\"LineStackingStrategy\" Value=\"MaxHeight\"/>", style);
    }

    [Fact]
    public void TypographyCommentRecordsRealMachineContourMeasurements()
    {
        var typography = ReadRepoFile("src", "PCMig.WinUI", "Themes", "Typography.xaml");
        var start = typography.IndexOf("x:Key=\"PCMigTextStatValue\"", StringComparison.Ordinal);
        var commentStart = typography.LastIndexOf("<!--", start, StringComparison.Ordinal);
        Assert.True(commentStart >= 0, "样式上方必须有说明注释");
        var comment = typography[commentStart..start];

        // 证据必须留在代码里：没有这组数字，下一轮就会有人用"看起来太细"把它改回去。
        Assert.Contains("PHASE 5B", comment);
        Assert.Contains("Normal(400)", comment);
        Assert.Contains("SemiBold(600)", comment);
        Assert.Contains("Bold(700)", comment);
        Assert.Contains("0.5000", comment);
        Assert.Contains("0.6000", comment);
        Assert.Contains("GlyphContourProbe", comment);
        // 诚实口径：必须写明这是渐进改善，不是"从削平变回圆弧"。
        Assert.Contains("渐进改善", comment);
    }

    [Fact]
    public void GlyphContourProbeRemainsRerunnable()
    {
        // 探针是上述全部数字的**唯一**可重测入口，因此它的两个分析前提不能丢：
        //   ① 每一格上方有一条洋红基准线（#FFFF00AA，Height=1），
        //   ② 每个字形 / 基准线都有确定性 AutomationId，分析脚本据此定位矩形。
        var code = ReadRepoFile("src", "PCMig.WinUI", "Views", "GlyphContourProbe.xaml.cs");

        Assert.Contains("GlyphLine_", code);
        Assert.Contains("Color.FromArgb(255, 255, 0, 170)", code);
        Assert.Contains("Glyph_{idPrefix}_{i}", code);
        Assert.Contains("Glyph_STYLE_20_", code);
        // 深底上必须显式给浅色前景，否则字形不可见、探针取证会静默失败。
        Assert.Contains("Color.FromArgb(255, 255, 255, 255)", code);
        Assert.Contains("PCMIG_GLYPH_PROBE", ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml.cs"));
    }
}