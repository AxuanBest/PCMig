using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D6.3 H（Fixture 12）：控件标识的"**登记 → 绑定 → 可观察**"矩阵契约（源码级）。
///
/// 为什么必须有这份契约（D6.2 实测缺陷 G-4）：
///   `ControlIds.cs` 登记了稳定 ID，但 XAML 里从来没有把它们绑成 `AutomationProperties.AutomationId`，
///   于是真实点击只能记下 `controlId="unknown"` —— **诊断说不出"用户点的是哪个控件"**。
///   `unknown` 同时表示"只有 x:Name"与"点到非控件表面/禁用控件"，在证据里不可区分。
///
/// 本契约锁三件事：
///   ① 登记表 ⊆ XAML 绑定集（登记了却不绑定 = 假承诺）；
///   ② XAML 绑定集 ⊆ 登记表（不允许冒出手写 ID，否则稳定 ID 表就不再是唯一事实源）；
///   ③ 代码里**当作控件标识使用**的每个 `ControlIds.Xxx` 都必须真的绑在 XAML 上
///      （这是"用户在哪个控件上做了什么"能落到证据里的前提）。
///
/// 另有两条防退化断言：同一个 ID 全 UI 只允许出现一次（兄弟唯一不够，重复会让自动化选错对象）；
/// 动作类型 token（`ActionKinds`）与控件 ID 是两套词汇表，不得互相污染。
///
/// 可观察性分两个通道，本契约只锁前一个（前者可静态证明，后者须真机 UIA 实测）：
///   · **视觉树通道**：Deep Trace / ProjectionObserver 走 `VisualTreeHelper.GetParent` +
///     `AutomationProperties.GetAutomationId` ⇒ 任意 FrameworkElement（含 StackPanel/Border）都能解析；
///   · **UIA 通道**：外部自动化（`tools\ui.ps1`）只能找到生成 UIA peer 的控件
///     （Button/TextBox/PasswordBox/ListView/ToggleSwitch…）；容器元素**不可见**，
///     这一差异由 D6.3 报告里的矩阵据实登记，不在这里假装成立。
/// </summary>
public sealed class D63AutomationIdBindingTests
{
    private const string IdAttributePattern = "AutomationProperties\\.AutomationId=\"([^\"]+)\"";
    private const string ConstPattern = "public const string (\\w+) = \"([^\"]+)\";";

    /// <summary>
    /// **模板生成型**绑定：形如 `AutomationProperties.AutomationId="{x:Bind ControlId}"`。
    ///
    /// 为什么允许它（WP I）：四张步骤卡共用一个 `DataTemplate`，字面量只能有一个值 ——
    /// 绑定前 UIA 树里 `StepCardButton`（x:Name 派生）出现 **4 次同名 ID**，外部自动化会选错对象。
    /// 允许生成，但**必须能被静态证明**：生成器（表达式体成员）里引用到的登记常量，
    /// 恰好等于"登记了却没有字面量绑定"的那一批 —— 两个方向都要闭合：
    /// 既不允许"登记了却根本没产出"，也不允许"凭空产出一个未登记的 ID"。
    /// </summary>
    private const string GeneratedBindingPattern = "AutomationProperties\\.AutomationId=\"\\{x:Bind ([A-Za-z_][A-Za-z0-9_]*)\\}\"";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
        }
        throw new InvalidOperationException("找不到仓库根：" + AppContext.BaseDirectory);
    }

    private static string WinUiDir() => Path.Combine(RepoRoot(), "src", "PCMig.WinUI");

    private static IEnumerable<string> WinUiSourceFiles() =>
        Directory.EnumerateFiles(WinUiDir(), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar));

    private static string Relative(string path) => path.Substring(RepoRoot().Length + 1);

    private static string ControlIdsSource() =>
        File.ReadAllText(Path.Combine(WinUiDir(), "Diagnostics", "ControlIds.cs"));

    private static string RegistrySection()
    {
        var src = ControlIdsSource();
        var i = src.IndexOf("public static class ControlIds", StringComparison.Ordinal);
        Assert.True(i > 0, "ControlIds.cs 里找不到 ControlIds 类声明");
        return src.Substring(i);
    }

    private static Dictionary<string, string> RegistryNames() =>
        Regex.Matches(RegistrySection(), ConstPattern).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

    private static IReadOnlyList<string> RegistryIds() => RegistryNames().Values.ToList();

    private static IReadOnlyList<string> ActionKindTokens()
    {
        var src = ControlIdsSource();
        var i = src.IndexOf("public static class ControlIds", StringComparison.Ordinal);
        return Regex.Matches(src.Substring(0, i), ConstPattern).Select(m => m.Groups[2].Value).ToList();
    }

    private static List<(string Id, string File)> BoundIds() =>
        (from file in WinUiSourceFiles()
         from Match m in Regex.Matches(File.ReadAllText(file), IdAttributePattern)
         // 排除模板生成型（{x:Bind …}）：它不是字面量 ID，单独由生成器证明（见 GeneratedIdProducers）。
         where !m.Groups[1].Value.StartsWith("{", StringComparison.Ordinal)
         select (Id: m.Groups[1].Value, File: Relative(file))).ToList();

    /// <summary>XAML 里以 <c>{x:Bind 属性}</c> 形式绑定的 AutomationId 生成型属性（含所在文件）。</summary>
    private static List<(string Property, string File)> GeneratedIdBindings() =>
        (from file in WinUiSourceFiles().Where(f => f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
         from Match m in Regex.Matches(File.ReadAllText(file), GeneratedBindingPattern)
         select (Property: m.Groups[1].Value, File: Relative(file))).ToList();

    /// <summary>
    /// 生成器的**静态来源证明**：在 `.cs` 里找到 `string &lt;属性&gt; =&gt; …;` 这一表达式体成员，
    /// 取出其中引用到的 `ControlIds.Xxx`。找不到成员 ⇒ 断言失败（不允许"生成来源无法证明"）。
    /// </summary>
    private static List<(string Name, string Where)> GeneratedIdProducers()
    {
        var result = new List<(string Name, string Where)>();
        var csFiles = WinUiSourceFiles()
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(f) != "ControlIds.cs")
            .ToList();

        foreach (var (property, xamlFile) in GeneratedIdBindings())
        {
            var marker = "string " + property + " =>";
            var hit = csFiles
                .Select(f => (File: f, Source: File.ReadAllText(f)))
                .FirstOrDefault(x => x.Source.Contains(marker, StringComparison.Ordinal));

            Assert.True(hit.Source is not null,
                $"{xamlFile} 以 {{x:Bind {property}}} 绑定 AutomationId，但 WinUI 源码里找不到生成该 ID 的表达式体成员 `{marker}`" +
                " ⇒ 生成来源无法静态证明");

            var i = hit.Source!.IndexOf(marker, StringComparison.Ordinal);
            var end = hit.Source.IndexOf(';', i);
            Assert.True(end > i, $"{Relative(hit.File)} 的 {property} 成员体未以 `;` 结束，无法取出生成集合");
            var body = hit.Source.Substring(i, end - i);
            foreach (Match m in Regex.Matches(body, "ControlIds\\.(\\w+)"))
                result.Add((Name: m.Groups[1].Value, Where: Relative(hit.File) + "::" + property));
        }
        return result;
    }

    /// <summary>全部可观察 ID ＝ 字面量绑定 ∪ 生成型绑定真正产出的登记 ID。</summary>
    private static List<string> AllObservableIds()
    {
        var names = RegistryNames();
        var ids = BoundIds().Select(b => b.Id).ToList();
        foreach (var (name, where) in GeneratedIdProducers())
        {
            Assert.True(names.ContainsKey(name), $"{where} 产出了未登记的常量 ControlIds.{name}");
            ids.Add(names[name]);
        }
        return ids;
    }

    private static List<(string Name, string File)> ReferencedControlIds() =>
        (from file in WinUiSourceFiles().Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
         where Path.GetFileName(file) != "ControlIds.cs"
         from Match m in Regex.Matches(File.ReadAllText(file), "ControlIds\\.(\\w+)")
         select (Name: m.Groups[1].Value, File: Relative(file))).ToList();

    [Fact]
    public void Fixture12_EveryRegisteredControlIdIsBoundAndEveryBoundIdIsRegistered()
    {
        var registry = RegistryIds();
        var bound = AllObservableIds();

        var neverBound = registry.Where(id => !bound.Contains(id, StringComparer.Ordinal)).ToList();
        Assert.True(neverBound.Count == 0,
            "登记在 ControlIds 里却从未绑到 XAML（真实点击只会记 controlId=\"unknown\"）：" + string.Join(", ", neverBound));

        var undeclared = bound.Distinct(StringComparer.Ordinal)
            .Where(id => !registry.Contains(id, StringComparer.Ordinal)).ToList();
        Assert.True(undeclared.Count == 0,
            "XAML 用了登记表之外的 AutomationId（稳定 ID 表不再是唯一事实源）：" + string.Join(", ", undeclared));
    }

    /// <summary>
    /// ★ WP I 新增 ★ 模板生成型 ID 的双向闭合：
    ///   左向：生成器引用到的常量必须都登记（否则是凭空 ID）；
    ///   右向："登记了但没有字面量绑定"的常量必须**恰好**由生成器产出（否则就是假承诺 —— 登记了却没人产）。
    /// 另外要求生成集合自身唯一（四张步骤卡必须是四个不同 ID，不能是同一个）。
    /// </summary>
    [Fact]
    public void Fixture12_TemplateGeneratedIdsAreExactlyTheOnesNoLiteralCanBind()
    {
        var names = RegistryNames();
        var literal = BoundIds().Select(b => b.Id).ToList();
        var producers = GeneratedIdProducers();

        Assert.True(GeneratedIdBindings().Count > 0,
            "本契约预期至少存在一处模板生成型绑定（步骤卡）；若已改回字面量，请删除本 Fixture 而不是放宽它");

        foreach (var (name, where) in producers)
            Assert.True(names.ContainsKey(name), $"{where} 产出了未登记的常量 ControlIds.{name}");

        var producedNames = producers.Select(p => p.Name).Distinct(StringComparer.Ordinal).ToList();
        var producedIds = producedNames.Select(n => names[n]).ToList();
        Assert.Equal(producedIds.Count, producedIds.Distinct(StringComparer.Ordinal).Count());

        var notLiteral = names.Where(kv => !literal.Contains(kv.Value, StringComparer.Ordinal))
            .Select(kv => kv.Key).ToList();

        var missingProducer = notLiteral.Where(n => !producedNames.Contains(n, StringComparer.Ordinal)).ToList();
        Assert.True(missingProducer.Count == 0,
            "这些 ID 既没有字面量绑定、也没有生成器产出（登记了却无人产 = 假承诺）：" + string.Join(", ", missingProducer));

        var orphanProducer = producedNames.Where(n => !notLiteral.Contains(n, StringComparer.Ordinal)).ToList();
        Assert.True(orphanProducer.Count == 0,
            "生成器产出了同时存在字面量绑定的 ID（两条来源会漂移）：" + string.Join(", ", orphanProducer));
    }

    [Fact]
    public void Fixture12_EveryAutomationIdIsUsedExactlyOnce()
    {
        var duplicates = BoundIds()
            .GroupBy(b => b.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key + " → " + string.Join(" / ", g.Select(x => x.File).Distinct()))
            .ToList();

        Assert.True(duplicates.Count == 0,
            "同一个 AutomationId 被多个界面元素使用（自动化与诊断都会选错对象）：" + string.Join("；", duplicates));
    }

    [Fact]
    public void Fixture12_EveryControlIdTheUiClaimsIsBound()
    {
        var names = RegistryNames();
        var bound = AllObservableIds();
        var referenced = ReferencedControlIds();

        Assert.True(referenced.Count > 0, "本契约必须至少覆盖一处真实引用（否则等于没锁）");

        foreach (var r in referenced)
        {
            Assert.True(names.ContainsKey(r.Name),
                $"{r.File} 引用了 ControlIds.{r.Name}，但登记表里没有这个常量");

            var id = names[r.Name];
            Assert.True(bound.Contains(id, StringComparer.Ordinal),
                $"{r.File} 把 ControlIds.{r.Name}（\"{id}\"）当作控件标识使用，但该 ID 在 XAML 里没有绑定" +
                " ⇒ 诊断只能记 controlId=\"unknown\"");
        }
    }

    [Fact]
    public void Fixture12_ActionTokensAreNeverControlIds()
    {
        var kinds = ActionKindTokens();
        var registry = RegistryIds();
        var bound = AllObservableIds();

        Assert.True(kinds.Count > 0, "ActionKinds 必须仍是一份非空词汇表");
        Assert.True(kinds.Intersect(registry, StringComparer.Ordinal).ToList().Count == 0,
            "动作类型 token 与控件 ID 混用（两套词汇表语义不同）：" + string.Join(", ", kinds.Intersect(registry, StringComparer.Ordinal)));
        Assert.True(kinds.Intersect(bound, StringComparer.Ordinal).ToList().Count == 0,
            "动作类型 token 被当成了 AutomationId：" + string.Join(", ", kinds.Intersect(bound, StringComparer.Ordinal)));
    }

    [Fact]
    public void Fixture12_IdsFollowTheRegisteredVocabulary()
    {
        var prefixes = new[] { "Step1.", "Step2.", "Step3.", "Step4.", "Shell.", "Diagnostics." };

        // 字面量 + 生成型都查：生成型 ID 同样是与外部自动化/Deep Trace 之间的契约。
        var subjects = BoundIds().Select(b => (b.Id, b.File))
            .Concat(GeneratedIdProducers().Select(p => (Id: RegistryNames()[p.Name], File: p.Where)))
            .ToList();
        Assert.True(subjects.Count > 0, "本契约必须至少覆盖一个绑定");

        foreach (var (id, file) in subjects)
        {
            Assert.True(prefixes.Any(p => id.StartsWith(p, StringComparison.Ordinal)),
                $"{file} 的 AutomationId \"{id}\" 不在已登记前缀集合 {string.Join("/", prefixes)} 内");
            Assert.True(id.All(ch => ch < 128 && !char.IsWhiteSpace(ch)),
                $"{file} 的 AutomationId \"{id}\" 含空白或非 ASCII 字符（不得用显示文字当标识）");
        }
    }
}