using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// PMML v1.0 Contract Tests —— **轻量合约测试**（用户口径：防止设计体系被整个绕开，不把像素写死）。
///
/// 只检查"体系仍在"这一类事实：
///   · PMML 核心文档存在；
///   · 核心 ResourceDictionary 存在；
///   · 关键 Motion 实现没有消失；
///   · 参考实现（材质调节 / 更新日志 / Task Picker）仍存在；
///   · AGENTS 有 PMML 入口与 Gate；
///   · PMML 核心术语没有被删；
///   · 已冻结的关键 Token 仍在真实资源里。
/// **刻意不写**逐像素 / 逐个 XAML 字符串的脆弱断言。
/// </summary>
public class PmmlContractTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到仓库根（未在上级目录中发现 PCMig.sln）。");
    }

    private static string Root => FindRepoRoot();
    private static string WinUi => Path.Combine(Root, "src", "PCMig.WinUI");

    private static string MustRead(string relativePath)
    {
        var p = Path.Combine(Root, relativePath);
        Assert.True(File.Exists(p), "缺少文件（PMML 契约）: " + relativePath);
        return File.ReadAllText(p);
    }

    // ── 1. 核心文档 ──────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("docs/PCMig-Visual-Motion-Language.md")]
    [InlineData("docs/PMML-UI修改硬性规范.md")]
    [InlineData("docs/PMML-Implementation-Audit.md")]
    [InlineData("docs/PMML-Legacy-Deviations.md")]
    public void PMML_core_documents_exist(string doc) => MustRead(doc);

    // ── 2. 核心 ResourceDictionary ───────────────────────────────────────────────
    [Theory]
    [InlineData("Themes/Colors.xaml")]
    [InlineData("Themes/Materials.xaml")]
    [InlineData("Themes/Typography.xaml")]
    [InlineData("Themes/Controls.xaml")]
    [InlineData("Themes/Motion.xaml")]
    [InlineData("Themes/TextInputTemplates.xaml")]
    [InlineData("Themes/PcmigComboBoxRoll.xaml")]
    public void Core_resource_dictionaries_exist(string rel)
        => Assert.True(File.Exists(Path.Combine(WinUi, rel.Replace('/', Path.DirectorySeparatorChar))), rel);

    // 所有主题字典都必须在 App.xaml 里被合并（体系不被绕开的第一道闸）
    [Fact]
    public void App_xaml_merges_every_theme_dictionary()
    {
        var app = MustRead("src/PCMig.WinUI/App.xaml");
        foreach (var rel in new[]
                 {
                     "Themes/Colors.xaml", "Themes/Materials.xaml", "Themes/Typography.xaml",
                     "Themes/Motion.xaml", "Themes/Controls.xaml", "Themes/TextInputTemplates.xaml",
                     "Themes/PcmigComboBoxRoll.xaml",
                 })
        {
            Assert.Contains(rel, app, StringComparison.Ordinal);
        }
    }

    // ── 3. 关键 Motion 实现仍在 ──────────────────────────────────────────────────
    [Theory]
    [InlineData("Presentation/MotionDirector.cs")]
    [InlineData("Presentation/FluidZoomTransitionCoordinator.cs")]
    [InlineData("Presentation/InteractionFeedback.cs")]
    [InlineData("Presentation/UniformScaleHost.cs")]
    public void Motion_implementations_exist(string rel)
        => Assert.True(File.Exists(Path.Combine(WinUi, rel.Replace('/', Path.DirectorySeparatorChar))), rel);

    // ── 4. 参考实现仍在（材质调节 / 更新日志 / Task Picker）──────────────────────
    [Theory]
    [InlineData("Views/DeveloperTuningPanel.xaml")]
    [InlineData("Views/DeveloperTuningPanel.xaml.cs")]
    [InlineData("Views/ChangelogPanel.xaml")]
    [InlineData("Views/ChangelogPanel.xaml.cs")]
    public void Tool_surface_reference_implementations_exist(string rel)
        => Assert.True(File.Exists(Path.Combine(WinUi, rel.Replace('/', Path.DirectorySeparatorChar))), rel);

    [Fact]
    public void Task_picker_reference_implementation_exists()
    {
        var xaml = MustRead("src/PCMig.WinUI/Views/Step2SelectDataPage.xaml");
        Assert.Contains("PCMigTaskPickerPresenterStyle", xaml, StringComparison.Ordinal);   // 收起态 Presenter
        Assert.Contains("ExistingJobsList", xaml, StringComparison.Ordinal);               // 展开态 ListView
        Assert.Contains("Placement=\"BottomEdgeAlignedLeft\"", xaml, StringComparison.Ordinal); // TAOP 锚定

        var cs = MustRead("src/PCMig.WinUI/Views/Step2SelectDataPage.xaml.cs");
        Assert.Contains("ExistingJobsList_ItemClick", cs, StringComparison.Ordinal);        // 对象化点击
        Assert.Contains("AdoptExistingJobAsync", cs, StringComparison.Ordinal);
    }

    // ComboBox 卷帘打开动效（PMML Motion Family 的 Overlay 参考之一）仍在
    [Fact]
    public void ComboBox_roll_motion_still_present()
    {
        var roll = MustRead("src/PCMig.WinUI/Themes/PcmigComboBoxRoll.xaml");
        Assert.Contains("RollScale", roll, StringComparison.Ordinal);
        // 只断言"元素不存在"：文件头注释里合法地提到过被替换的 Split 动画名字（记录历史），
        // 因此不能对裸词做 DoesNotContain（那会把注释也算成违规）。
        Assert.DoesNotContain("<SplitOpenThemeAnimation", roll, StringComparison.Ordinal);
        Assert.DoesNotContain("<SplitCloseThemeAnimation", roll, StringComparison.Ordinal);
    }

    // ── 5. AGENTS 入口 + Gate ────────────────────────────────────────────────────
    [Fact]
    public void Agents_declares_pmml_entry_and_gate()
    {
        var agents = MustRead("AGENTS.md");
        Assert.Contains("PMML", agents, StringComparison.Ordinal);
        Assert.Contains("PCMig-Visual-Motion-Language.md", agents, StringComparison.Ordinal);
        Assert.Contains("PMML-UI修改硬性规范.md", agents, StringComparison.Ordinal);
        Assert.Contains("PMML Compliance Gate", agents, StringComparison.Ordinal);
    }

    // ── 6. 核心术语没有被删 ──────────────────────────────────────────────────────
    [Theory]
    [InlineData("PMML")]
    [InlineData("DAM")]
    [InlineData("LMDS")]
    [InlineData("DSL-45")]
    [InlineData("ESR")]
    [InlineData("DCST")]
    [InlineData("OACT")]
    [InlineData("TAOP")]
    [InlineData("MHE")]
    public void Core_terminology_present_in_spec(string term)
    {
        var spec = MustRead("docs/PCMig-Visual-Motion-Language.md");
        Assert.Contains(term, spec, StringComparison.Ordinal);
    }

    [Fact]
    public void Pmml_hard_rules_r1_to_r15_present()
    {
        var spec = MustRead("docs/PCMig-Visual-Motion-Language.md");
        var missing = Enumerable.Range(1, 15)
            .Select(n => "PMML-R" + n)
            .Where(tag => !spec.Contains(tag, StringComparison.Ordinal))
            .ToList();
        Assert.True(missing.Count == 0, "规范里缺少硬规则: " + string.Join(", ", missing));
    }

    [Fact]
    public void Compliance_gate_block_present_in_hard_rules_doc()
    {
        var rules = MustRead("docs/PMML-UI修改硬性规范.md");
        Assert.Contains("PMML Compliance", rules, StringComparison.Ordinal);
        Assert.Contains("Foreground Sharpness", rules, StringComparison.Ordinal);
        Assert.Contains("Legacy Deviation Introduced", rules, StringComparison.Ordinal);
        Assert.Contains("PMML Visual Impact: None", rules, StringComparison.Ordinal);
    }

    // ── 7. 已冻结的关键 Token 仍真实存在 ─────────────────────────────────────────
    [Theory]
    [InlineData("Themes/Colors.xaml", "AmbientBlueOpacity")]
    [InlineData("Themes/Colors.xaml", "TextPrimaryBrush")]
    [InlineData("Themes/Colors.xaml", "NavCardEmbossTopBrush")]
    [InlineData("Themes/Colors.xaml", "EdgeHighlightDiagonalBrush")]
    [InlineData("Themes/Materials.xaml", "PCMigShellMaterial")]
    [InlineData("Themes/Materials.xaml", "PCMigCardMaterial")]
    [InlineData("Themes/Materials.xaml", "PCMigInsetMaterial")]
    [InlineData("Themes/Materials.xaml", "PCMigRadiusInput")]
    [InlineData("Themes/Materials.xaml", "PCMigRadiusStepCard")]
    [InlineData("Themes/Materials.xaml", "PCMigRadiusButton")]
    [InlineData("Themes/Materials.xaml", "PCMigRadiusInset")]
    [InlineData("Themes/Materials.xaml", "ElevationLow")]
    [InlineData("Themes/Materials.xaml", "ElevationMedium")]
    [InlineData("Themes/Materials.xaml", "ElevationHigh")]
    public void Frozen_tokens_still_exist_in_real_resources(string rel, string token)
    {
        var text = MustRead("src/PCMig.WinUI/" + rel);
        Assert.Contains(token, text, StringComparison.Ordinal);
    }

    // ── 8. Audit 必含各体系分节（防止审计被清空成空壳）────────────────────────────
    [Theory]
    [InlineData("## DAM")]
    [InlineData("## LMDS")]
    [InlineData("## DSL-45")]
    [InlineData("## ESR")]
    [InlineData("## DCST")]
    [InlineData("## OACT")]
    [InlineData("## TAOP")]
    [InlineData("## MHE")]
    [InlineData("## Token")]
    public void Audit_document_contains_required_sections(string section)
    {
        var audit = MustRead("docs/PMML-Implementation-Audit.md");
        Assert.Contains(section, audit, StringComparison.Ordinal);
    }

    // ── 9. 轻量护栏：审计文档不得退化成"没有真实数值"的空文档 ─────────────────────
    [Fact]
    public void Audit_document_contains_real_file_line_references()
    {
        var audit = MustRead("docs/PMML-Implementation-Audit.md");
        var refs = audit.Split('\n').Count(l => l.Contains(".xaml:", StringComparison.Ordinal)
                                             || l.Contains(".cs:", StringComparison.Ordinal));
        Assert.True(refs >= 20, "审计文档里的「文件:行号」引用过少（实际 " + refs + "），疑似统计未落盘。");
    }
}