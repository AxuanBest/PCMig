using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D6.1 §11 + §12 收口测试（源码级契约，因为 WinUI 不能在本测试项目里加载）：
///   §20.19 九条 Action Contract **每一条**都要有 production 侧的 Action 生产者；
///   §12 关键业务控件必须有稳定 ControlId（XAML 里真实存在，且不重复）。
///
/// 说明：这里断言的是"接线存在 + ID 真实存在"（结构事实）；
/// 运行期的因果链正确性由 D3/D4 的运行时用例与独立探针覆盖。
/// </summary>
public sealed class D61ActionCoverageTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
        throw new InvalidOperationException("找不到仓库根：" + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> WinUiSources()
    {
        var root = Path.Combine(RepoRoot(), "src", "PCMig.WinUI");
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToArray();
    }

    private static string ReadAll(IEnumerable<string> paths) =>
        string.Join("\n", paths.Select(File.ReadAllText));

    /// <summary>九条动作契约在生产代码里都必须真的被开启（Begin）。</summary>
    [Theory]
    [InlineData("Connect")]
    [InlineData("Prepare")]
    [InlineData("Start")]
    [InlineData("Resume")]
    [InlineData("Pause")]
    [InlineData("Stop")]
    [InlineData("Verify")]
    [InlineData("Repair")]
    [InlineData("Export")]
    public void EveryActionContractHasAProductionProducer(string actionKind)
    {
        var sources = WinUiSources()
            .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        && !p.EndsWith("ActionTrace.cs", StringComparison.OrdinalIgnoreCase)
                        && !p.EndsWith("ControlIds.cs", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var expected = "ActionTrace.Begin(ActionKinds." + actionKind;
        var hits = sources.Where(p => File.ReadAllText(p).Contains(expected, StringComparison.Ordinal)).ToArray();

        Assert.True(hits.Length > 0, $"动作 {actionKind} 没有任何 production 侧生产者（应为 {expected}）");
    }

    /// <summary>
    /// 关键业务控件必须在**源码 XAML** 里真实带着稳定 AutomationId，
    /// 且同一个 ID 不得在整棵 XAML 树里出现两次（重复会让自动化选错对象）。
    /// </summary>
    [Fact]
    public void BusinessCriticalControlsCarryUniqueStableAutomationIds()
    {
        var xamlFiles = WinUiSources().Where(p => p.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)).ToArray();
        var occurrences = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var file in xamlFiles)
        {
            var text = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(text, "AutomationProperties\\.AutomationId=\"([^\"]*)\""))
            {
                var id = match.Groups[1].Value;
                if (!occurrences.TryGetValue(id, out var list)) occurrences[id] = list = new List<string>();
                list.Add(Path.GetFileName(file));
            }
        }

        // 关键清单（与 WinUI 侧 ControlIds.BusinessCritical 对齐；这里用字面量避免跨项目引用 WinUI 类型）。
        var critical = new[]
        {
            "Step1.Connect", "Step1.AddShare",
            "Step2.Prepare", "Step2.Start", "Step2.Pause", "Step2.Stop", "Step2.Resume", "Step2.ExistingJobs",
            "Step4.Verify", "Step4.Repair",
            // ★ 缺口②收口（D6.3 §11）★ Step4 的「恢复任务」原先漏在清单外：实机点击后零 UI-00x。
            "Step4.Resume",
            "Shell.Transfer.Start", "Shell.Transfer.Pause", "Shell.Transfer.Stop", "Shell.Transfer.Resume",
            "Shell.Tool.MaterialTuning", "Shell.Tool.Diagnostics", "Shell.Panel.Diagnostics",
            "Diagnostics.Refresh", "Diagnostics.DeepTrace", "Diagnostics.WarnOnly", "Diagnostics.Export",
        };

        var missing = critical.Where(id => !occurrences.ContainsKey(id)).ToArray();
        Assert.True(missing.Length == 0, "以下关键控件缺少稳定 AutomationId：" + string.Join(", ", missing));

        var duplicated = occurrences.Where(kv => kv.Value.Count > 1)
            .Select(kv => $"{kv.Key} -> {string.Join("/", kv.Value)}").ToArray();
        Assert.True(duplicated.Length == 0, "AutomationId 在 XAML 里重复出现（自动化会选错对象）：" + string.Join("; ", duplicated));
    }

    // ───────── 缺口②红灯：同一业务动作的**每个**入口都必须留下因果链 ─────────

    /// <summary>
    /// ★ 缺口②红灯（D6.3 §11）★ 结果页工具条「恢复任务」（`Step4.Resume`）实机点击后**零 UI-00x**：
    ///   `ToolbarResume_Click` 完全没有 ActionTrace，且 `Step4Resume` 不在 BusinessCritical 清单里 ——
    ///   于是"用户确实点了恢复"这件事在诊断里**根本不可答**（同一业务动作的 Step2 入口却有完整因果链）。
    ///
    /// 修复后必须同时满足（只声明不插桩等于没修）：
    ///   ① 源码 XAML 里真有稳定 AutomationId `Step4.Resume`（可被自动化定位）；
    ///   ② `ControlIds.BusinessCritical` 声明它（清单不再漏掉第三个入口）；
    ///   ③ 处理函数体里**真的**开了动作：Begin + 期望 + 至少一个终点。
    /// </summary>
    [Fact]
    public void Step4ResumeClickMustHaveACausalTraceLikeItsOtherTwoEntryPoints()
    {
        var page = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PCMig.WinUI", "Views", "Step4ResultPage.xaml.cs"));
        // 用**声明**定位（`void Xxx_Click(`），否则会命中 `ToolbarResume.Click += ToolbarResume_Click;` 这类接线行。
        var body = MethodBody(page, "void ToolbarResume_Click");

        Assert.Contains("ActionTrace.Begin(ActionKinds.Resume, ControlIds.Step4Resume", body, StringComparison.Ordinal);
        Assert.Contains("trace.Expect(\"resume.v1\"", body, StringComparison.Ordinal);
        // 终点必须有：只开不落终点 ⇒ Dispose 只能给 Unknown，"用户点了什么"依旧答不上。
        Assert.True(
            body.Contains("trace.Finish(", StringComparison.Ordinal)
            || body.Contains("trace.Complete(", StringComparison.Ordinal)
            || body.Contains("trace.Reject(", StringComparison.Ordinal),
            "ToolbarResume_Click 开了动作却没有终点（Finish/Complete/Reject）");

        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PCMig.WinUI", "Views", "Step4ResultPage.xaml"));
        Assert.Contains("AutomationProperties.AutomationId=\"Step4.Resume\"", xaml, StringComparison.Ordinal);

        var registry = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PCMig.WinUI", "Diagnostics", "ControlIds.cs"));
        Assert.Contains("public const string Step4Resume = \"Step4.Resume\";", registry, StringComparison.Ordinal);
        Assert.Contains("Step4Resume", MethodBody(registry, "BusinessCritical"), StringComparison.Ordinal);
    }

    /// <summary>取出某个方法/字段初始化的花括号区块（按配对扫描，跳过字符串、字符与注释里的花括号）。</summary>
    private static string MethodBody(string text, string memberName)
    {
        var at = text.IndexOf(memberName, StringComparison.Ordinal);
        Assert.True(at >= 0, $"源码里找不到 {memberName}");
        var open = text.IndexOf('{', at);
        Assert.True(open > 0, $"{memberName} 之后找不到花括号区块起点");

        var depth = 0;
        var inString = false;
        var inChar = false;
        var inLineComment = false;
        var inBlockComment = false;

        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (inLineComment) { if (c == '\n') inLineComment = false; continue; }
            if (inBlockComment) { if (c == '*' && next == '/') { inBlockComment = false; i++; } continue; }
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (inChar)
            {
                if (c == '\\') { i++; continue; }
                if (c == '\'') inChar = false;
                continue;
            }

            if (c == '/' && next == '/') { inLineComment = true; i++; continue; }
            if (c == '/' && next == '*') { inBlockComment = true; i++; continue; }
            if (c == '"') { inString = true; continue; }
            if (c == '\'') { inChar = true; continue; }

            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return text.Substring(open, i - open + 1);
            }
        }

        throw new InvalidOperationException($"{memberName} 的花括号不配对");
    }

    /// <summary>ControlId 不得用显示文字/序号/坐标兜底（§9 纪律的可判定部分）。</summary>
    [Fact]
    public void ControlIdsAreSemanticTokensNotDisplayText()
    {
        var registry = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PCMig.WinUI", "Diagnostics", "ControlIds.cs"));
        foreach (Match match in Regex.Matches(registry, "public const string \\w+ = \"([^\"]+)\""))
        {
            var value = match.Groups[1].Value;
            // 允许：字母/数字/点/连字符，且**只含 ASCII**（显示文字通常是中文/带空格）。
            Assert.Matches("^[A-Za-z][A-Za-z0-9.\\-]*$", value);
        }
    }
}