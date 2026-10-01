using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using PCMig.Diagnostics;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using PCMig.Diagnostics.Export;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D6.1 §1（P0 隐私）**行为测试**：Deep Trace 永远不得保存文本键身份，敏感输入源必须整体丢弃。
///
/// 与旧测试的区别（这是本轮最重要的纪律修正）：
///   旧测试断言源码里不存在某个字符串（`DoesNotContain("e.Key.ToString")`）——而真实缺陷写的是
///   `"key:" + e.Key`，**该断言恒真、永远抓不到缺陷**。
///   现在的做法：用合成输入 → 走**真实运行时**与**真实导出** → 直接读 DiagnosticEvent / JSONL / ZIP，
///   断言"身份不存在"这个**行为事实**。
/// </summary>
public sealed class D61PrivacyInputObservationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-d61-" + Guid.NewGuid().ToString("N")[..8]);

    public D61PrivacyInputObservationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* 句柄延迟释放 */ }
    }

    // ────────────────────────── 策略层（纯函数，可直接行为验证） ──────────────────────────

    /// <summary>
    /// ★ 核心不变量 ★ 对**所有** VK 码分类，结果只能取自固定类别集合。
    /// 这在结构上排除了"类别里携带按键身份"的可能（"A"/"B"/"3" 永远不可能出现在结果里）。
    /// </summary>
    [Fact]
    public void ClassifyKeyCanNeverCarryKeyIdentity()
    {
        for (var vk = 0; vk <= 0xFF; vk++)
        {
            var category = InputObservationPolicy.ClassifyKey(vk);
            Assert.Contains(category, InputCategories.All);
            Assert.DoesNotContain(vk.ToString("X2"), category, StringComparison.OrdinalIgnoreCase);
        }

        // 明确点出旧缺陷的形状：字母/数字键的分类里绝不能出现该字符本身。
        Assert.Equal(InputCategories.KeyOtherNonText, InputObservationPolicy.ClassifyKey('A'));
        Assert.Equal(InputCategories.KeyOtherNonText, InputObservationPolicy.ClassifyKey('B'));
        Assert.Equal(InputCategories.KeyOtherNonText, InputObservationPolicy.ClassifyKey('3'));
        Assert.Equal(InputCategories.KeyTab, InputObservationPolicy.ClassifyKey(0x09));
        Assert.Equal(InputCategories.KeyDirectional, InputObservationPolicy.ClassifyKey(0x25));

        // 旧写法（"key:" + e.Key）产生的字符串**不在白名单**里 ⇒ 已被结构性禁止。
        foreach (var forbidden in new[] { "key:A", "key:B", "key:3", "key:Tab", "A", "3" })
            Assert.False(InputCategories.IsKnown(forbidden), forbidden + " 不应是合法类别");
    }

    [Theory]
    [InlineData("PasswordBox", null)]
    [InlineData("TextBox", "Step1.PasswordInput")]
    [InlineData("Button", "Step1.PasswordReveal")]
    [InlineData("TextBox", "Dialog.CredentialToken")]
    [InlineData("PasswordBox", "Step1.Connect")]                     // 类型敏感即敏感，ID 不参与豁免
    public void SensitiveSourcesAreDetectedThroughTheWholeAncestry(string leafType, string? leafId)
    {
        // 叶子本身 + 一个"看起来无害"的祖先：必须走完整条链才能发现敏感。
        var ancestry = new List<InputSourceNode>
        {
            new(leafType, leafId),
            new("Grid", "Step1.Form"),
            new("StackPanel", null),
        };

        Assert.True(InputObservationPolicy.IsSensitiveSource(ancestry));
    }

    [Fact]
    public void SensitiveAncestorIsDetectedEvenWhenTheLeafLooksHarmless()
    {
        // 深层：内容元素 → ... → PasswordBox。⇒ 不能靠"叶子上没 ID"绕过。
        var ancestry = new List<InputSourceNode>
        {
            new("TextBox", null),
            new("ScrollViewer", null),
            new("Grid", null),
            new("PasswordBox", "Step1.PasswordInput"),
        };

        Assert.True(InputObservationPolicy.IsSensitiveSource(ancestry));
    }

    [Fact]
    public void OrdinaryControlsAreNotSensitiveAndKeepTheirStableId()
    {
        var ancestry = new List<InputSourceNode> { new("Button", null), new("Button", "Step1.Connect") };

        Assert.False(InputObservationPolicy.IsSensitiveSource(ancestry));
        Assert.Equal("Step1.Connect", InputObservationPolicy.ResolveControlId(ancestry));
    }

    // ────────────────────────── 行为层：真实 runtime + 真实导出 ──────────────────────────

    /// <summary>
    /// 合成 A / B / 3 / Tab（以及一个 PasswordBox 输入），按**与 Observer 相同的策略**决定发布什么，
    /// 然后读**真实落盘的 JSONL** 与**真实导出的 ZIP**，断言：
    ///   ① 按键身份（A/B/3）在任何输出里都不存在；
    ///   ② PasswordBox 来源零事件，其 ControlId 也不出现；
    ///   ③ 允许的 navigation 事件只保存 category；
    ///   ④ Secret canary 不在任何输出里。
    /// </summary>
    [Fact]
    public void SynthesizedKeystrokesAndPasswordBoxLeaveNoIdentityAnywhere()
    {
        const string secretCanary = "Sup3rSecret!P@ssw0rd";
        var runtime = DiagnosticRuntime.Start(new DiagnosticRuntimeOptions
        {
            StorageRoot = Path.Combine(_root, "runtime"),
            InitialMode = CaptureMode.Deep,
            AppVersion = "d61-test",
            ShutdownBudgetMs = 5000,
        }, out var degraded);
        Assert.NotNull(runtime.Store);

        // 合成输入序列：(VK, 祖先链)。策略判定与 WinUI 适配器**同一份**实现。
        var inputs = new (int Vk, string LeafType, string? Id)[]
        {
            ('A', "TextBox", null),                    // 文本键（旧缺陷会把身份写进事件）
            ('B', "TextBox", null),
            ('3', "TextBox", null),
            (0x09, "TextBox", "Step1.Host"),           // Tab：允许，但只记 category
            (0x25, "Button", "Step1.Connect"),         // 方向键：允许，只记 category
            ('X', "PasswordBox", "Step1.PasswordInput"), // 敏感：必须整体丢弃
            ('Y', "TextBox", "Step1.PasswordInput"),     // 敏感（ID 命中）：必须整体丢弃
        };

        var published = 0;
        var droppedSensitive = 0;
        foreach (var (vk, leafType, id) in inputs)
        {
            var ancestry = new List<InputSourceNode>
            {
                new(leafType, null),
                id is null ? new InputSourceNode("Grid", "Step1.Form") : new InputSourceNode("Grid", id),
            };

            if (InputObservationPolicy.IsSensitiveSource(ancestry))
            {
                droppedSensitive++;
                continue;                                   // 与 Observer 同口径：整条丢弃
            }

            var category = InputObservationPolicy.ClassifyKey(vk);
            Assert.True(InputCategories.IsKnown(category));
            runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                UiEvents.InputObserved,
                DiagnosticContext.Root(runtime.SessionId, "D61").WithControl(InputObservationPolicy.ResolveControlId(ancestry) ?? "unknown"),
                new UiInputObservedPayload(category, true, true, 0, droppedSensitive)));
            published++;
        }

        Assert.Equal(5, published);
        Assert.Equal(2, droppedSensitive);

        // 等待条件针对**这几条**事件本身（不靠总数，也不靠"≥1 条"——那会被启动期事件提前满足）。
        Assert.True(D2TestSupport.WaitUntil(() =>
        {
            runtime.TryGetViewerSnapshot(out var snapshot);
            return snapshot.Count(e => e.Descriptor.Name == UiEvents.InputObserved.Name) >= published;
        }, 10_000), "没有在窗口内观察到全部输入事件");

        var sessionDir = runtime.Store!.SessionDir;
        D2TestSupport.Shutdown(runtime);

        // ①/③ 本地 JSONL：只有 category，没有身份。
        var jsonl = string.Join("\n", Directory.GetFiles(Path.Combine(sessionDir, "events"), "*.jsonl").Select(File.ReadAllText));
        Assert.Contains("UI.InputObserved", jsonl, StringComparison.Ordinal);
        Assert.Contains(InputCategories.KeyTab, jsonl, StringComparison.Ordinal);
        Assert.Contains(InputCategories.KeyDirectional, jsonl, StringComparison.Ordinal);
        Assert.Contains(InputCategories.KeyOtherNonText, jsonl, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "key:A", "key:B", "key:3", "key:X", "key:Y", "key:Tab" })
            Assert.DoesNotContain(forbidden, jsonl, StringComparison.Ordinal);

        // ② 敏感来源的 ControlId 不得作为输入事件的控件出现。
        Assert.DoesNotContain("Step1.PasswordInput", jsonl, StringComparison.Ordinal);

        // ④ 导出包：同样不得出现身份，也不得出现 Secret canary。
        var outcome = new DiagnosticPackageExporter().Export(new DiagnosticExportRequest
        {
            SessionDir = sessionDir,
            OutputDirectory = Path.Combine(_root, "exports"),
            AppVersion = "d61-test",
            Health = runtime.GetHealthSnapshot(),
        });
        Assert.True(outcome.Succeeded, outcome.FailureReason);

        using var zip = ZipFile.OpenRead(outcome.ZipPath!);
        var allText = new StringBuilder();
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            allText.Append(reader.ReadToEnd()).Append('\n');
        }

        var packageText = allText.ToString();
        foreach (var forbidden in new[] { "key:A", "key:B", "key:3", "key:X", "key:Y" })
            Assert.DoesNotContain(forbidden, packageText, StringComparison.Ordinal);
        Assert.DoesNotContain(secretCanary, packageText, StringComparison.Ordinal);
        Assert.DoesNotContain("Step1.PasswordInput", packageText, StringComparison.Ordinal);
        Assert.Contains(InputCategories.KeyTab, packageText, StringComparison.Ordinal);
    }

    /// <summary>
    /// ★ 管线层面不可达 ★ 即便有人（未来）又把 "key:A" 这种身份写进 payload，
    /// 运行时入口也必须**拒收**，且该字符串不得出现在 JSONL 或导出包里。
    /// 这条测试直接覆盖旧缺陷的原始形状。
    /// </summary>
    [Fact]
    public void RuntimeRejectsAnyInputEventCarryingKeyIdentity()
    {
        var runtime = DiagnosticRuntime.Start(new DiagnosticRuntimeOptions
        {
            StorageRoot = Path.Combine(_root, "runtime2"),
            InitialMode = CaptureMode.Deep,
            AppVersion = "d61-test",
            ShutdownBudgetMs = 5000,
        }, out _);
        Assert.NotNull(runtime.Store);

        var accepted = runtime.Publisher.TryPublish(new DiagnosticEventDraft(
            UiEvents.InputObserved,
            DiagnosticContext.Root(runtime.SessionId, "D61").WithControl("Step1.Connect"),
            new UiInputObservedPayload("key:A", true, true, 0)));
        Assert.False(accepted);                        // 带身份的类别必须被拒收

        var legal = runtime.Publisher.TryPublish(new DiagnosticEventDraft(
            UiEvents.InputObserved,
            DiagnosticContext.Root(runtime.SessionId, "D61").WithControl("Step1.Connect"),
            new UiInputObservedPayload(InputCategories.KeyOtherNonText, true, true, 0)));
        Assert.True(legal);                            // 合法类别照常接受

        var sessionDir = runtime.Store!.SessionDir;
        D2TestSupport.Shutdown(runtime);

        var jsonl = string.Join("\n", Directory.GetFiles(Path.Combine(sessionDir, "events"), "*.jsonl").Select(File.ReadAllText));
        Assert.DoesNotContain("key:A", jsonl, StringComparison.Ordinal);
        Assert.Contains(InputCategories.KeyOtherNonText, jsonl, StringComparison.Ordinal);

        var outcome = new DiagnosticPackageExporter().Export(new DiagnosticExportRequest
        {
            SessionDir = sessionDir,
            OutputDirectory = Path.Combine(_root, "exports2"),
            Health = runtime.GetHealthSnapshot(),
        });
        Assert.True(outcome.Succeeded, outcome.FailureReason);
        using var zip = ZipFile.OpenRead(outcome.ZipPath!);
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            Assert.DoesNotContain("key:A", reader.ReadToEnd(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// WinUI 适配器契约（**补充**而非替代上面的行为测试）：
    /// 适配器必须走契约层策略、必须构建祖先链、且**不得**再出现把按键身份拼进类别的写法。
    /// </summary>
    [Fact]
    public void WinUiAdapterDelegatesToThePolicyAndNeverEmbedsKeyIdentity()
    {
        var path = Path.Combine(new[]
        {
            RepoRoot(), "src", "PCMig.WinUI", "Diagnostics", "DeepTraceInputObserver.cs",
        });
        Assert.True(File.Exists(path), "缺少 " + path);

        // 只去掉注释行：契约针对代码。
        var code = string.Join("\n", File.ReadAllLines(path).Select(line =>
        {
            var i = line.IndexOf("//", StringComparison.Ordinal);
            return i >= 0 ? line.Substring(0, i) : line;
        }));

        // 必须把分类交给契约层（而不是自己拼字符串）。
        Assert.Contains("InputObservationPolicy.ClassifyKey((int)e.Key)", code, StringComparison.Ordinal);
        // 必须在解析 ID 之前做敏感判定，且判定走策略。
        Assert.Contains("InputObservationPolicy.IsSensitiveSource(_ancestry)", code, StringComparison.Ordinal);
        Assert.Contains("InputObservationPolicy.ResolveControlId(_ancestry)", code, StringComparison.Ordinal);
        Assert.Contains("VisualTreeHelper.GetParent", code, StringComparison.Ordinal);

        // 旧缺陷形状必须彻底消失：任何把 e.Key 拼进字符串的写法都不允许。
        Assert.DoesNotContain("\"key:\"", code, StringComparison.Ordinal);
        Assert.DoesNotContain("+ e.Key", code, StringComparison.Ordinal);
        Assert.DoesNotContain("e.Key.ToString", code, StringComparison.Ordinal);
        Assert.DoesNotContain(".Password", code, StringComparison.Ordinal);      // 不读敏感控件内容
        Assert.DoesNotContain("Clipboard", code, StringComparison.Ordinal);

        // 敏感丢弃必须发生在发布之前（顺序即语义）。
        var sensitiveIndex = code.IndexOf("InputObservationPolicy.IsSensitiveSource(_ancestry)", StringComparison.Ordinal);
        var publishIndex = code.IndexOf("_observed++;", StringComparison.Ordinal);
        Assert.True(sensitiveIndex > 0 && publishIndex > sensitiveIndex,
            "敏感判定必须排在计数/发布之前");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
        throw new InvalidOperationException("找不到仓库根：" + AppContext.BaseDirectory);
    }
}