using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using PCMig.Core.Diagnostics;
using PCMig.Diagnostics;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using PCMig.Diagnostics.Abstractions.Serialization;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// ★ D6.3 WP I（Fixture 13）：导航证据的"**真事实**"契约 ★
///
/// 收口的事（D6.2 缺口 G-9）：`UI.NavigationChanged` 描述符早已登记却**从未产生**，
/// 于是证据里根本没有"用户什么时候从哪一页到了哪一页、为什么"这条事实 ——
/// 事后只能靠时间戳猜，而"猜"不是证据。
///
/// 本契约锁三件事：
///   ① **行为**（走真实管道 + 真实编解码往返）：发布后事件真的落盘，
///      且 from/to/reasonCode/actionKind/operationId **逐字**解回来（少解一个字段就会被往返洗掉，
///      与 `eventVersion` 被清洗是同一类缺陷）；
///   ② **诚实**：原因码只允许来自封闭集合，未知一律降级 `unspecified`；同一页不制造事件；
///      "没有 operationId"必须**省略属性**，而不是写个空串冒充有值；
///   ③ **只在导航真的发生之后发布**（WinUI 侧只能做源码级证明）：
///      发布点必须在状态已切换之后、每个 GoTo 调用点都必须带原因码、
///      原因必须在下一次切换前被取走复位（否则"同页赋值"会把原因留给下一次真实切换 —— 编造原因）。
/// </summary>
public sealed class D63NavigationEvidenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-d63-nav-" + Guid.NewGuid().ToString("N")[..8]);

    public D63NavigationEvidenceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        CoreDiagnostics.Reset();
        try { Directory.Delete(_root, true); } catch (Exception) { /* 句柄延迟释放 */ }
    }

    private DiagnosticRuntime StartRuntime(string name) =>
        DiagnosticRuntime.Start(new DiagnosticRuntimeOptions
        {
            StorageRoot = Path.Combine(_root, name),
            InitialMode = CaptureMode.Operational,
            AppVersion = "d63-nav-test",
            ShutdownBudgetMs = 5000,
        }, out _);

    private static List<IDiagnosticPayload> ReadPayloads(DiagnosticRuntime runtime)
    {
        var dir = Path.Combine(runtime.Store!.SessionDir, "events");
        var payloads = new List<IDiagnosticPayload>();
        foreach (var line in ReadRawLines(runtime))
        {
            if (DiagnosticEventJson.TryParse(line, out var evt, out _) && evt?.Payload is not null)
                payloads.Add(evt.Payload);
        }
        return payloads;
    }

    private static List<string> ReadRawLines(DiagnosticRuntime runtime)
    {
        var dir = Path.Combine(runtime.Store!.SessionDir, "events");
        var lines = new List<string>();
        foreach (var file in Directory.GetFiles(dir, "*.jsonl"))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
                if (line.Length > 0) lines.Add(line);
        }
        return lines;
    }

    // ─────────────────────────── ① 行为（真实管道 + 真实往返） ───────────────────────────

    [Fact]
    public void Fixture13_NavigationLandsInEvidenceWithRealFromToAndReason()
    {
        var runtime = StartRuntime("nav-basic");
        using (CoreDiagnostics.Install(runtime))
        {
            NavigationEvidence.Publish("Connect", "SelectData", NavigationReasons.RailClick);
        }
        D2TestSupport.Shutdown(runtime);

        var payload = Assert.Single(ReadPayloads(runtime).OfType<UiNavigationPayload>());
        Assert.Equal("Connect", payload.From);
        Assert.Equal("SelectData", payload.To);
        Assert.Equal(NavigationReasons.RailClick, payload.ReasonCode);
        Assert.Equal(NavigationEvidence.DefaultActionKind, payload.ActionKind);
        Assert.Null(payload.OperationId);                  // 没有就**没有**，不编造

        D2TestSupport.Dispose(runtime);
    }

    [Fact]
    public void Fixture13_NavEventAndItsPayloadNameAreTheSameContract()
    {
        // 描述符登记名与 codec 注册名必须一致，否则事件落盘后**无人能解回**载荷
        // （这正是 P1-6 那类"写进去又被洗掉"缺陷的入口）。
        Assert.Equal(UiNavigationPayload.Name, UiEvents.NavigationChanged.PayloadName);
        Assert.Equal("UI.NavigationChanged", UiEvents.NavigationChanged.Name);
        Assert.Equal(DiagnosticCategory.Ui, UiEvents.NavigationChanged.Category);
        Assert.Equal(16, UiEvents.NavigationChanged.Ordinal);
    }

    [Fact]
    public void Fixture13_OperationIdIsOnlyPresentWhenItReallyExists()
    {
        var runtime = StartRuntime("nav-opid");
        using (CoreDiagnostics.Install(runtime))
        {
            NavigationEvidence.Publish("Connect", "SelectData", NavigationReasons.RailClick, operationId: null);
            NavigationEvidence.Publish("SelectData", "Progress", NavigationReasons.FooterNext, operationId: "OPR-D63-NAV");
        }
        D2TestSupport.Shutdown(runtime);

        var payloads = ReadPayloads(runtime).OfType<UiNavigationPayload>().ToList();
        Assert.Equal(2, payloads.Count);
        Assert.Null(payloads[0].OperationId);
        Assert.Equal("OPR-D63-NAV", payloads[1].OperationId);

        // 线级证据：没有 operationId 的那条**不得出现该属性**（空串冒充有值同样是假事实）。
        var without = ReadRawLines(runtime).Single(l => l.Contains("\"to\":\"SelectData\"", StringComparison.Ordinal));
        Assert.DoesNotContain("operationId", without, StringComparison.Ordinal);

        D2TestSupport.Dispose(runtime);
    }

    [Fact]
    public void Fixture13_InitialAnnouncementSaysThereWasNoPreviousPage()
    {
        var runtime = StartRuntime("nav-initial");
        using (CoreDiagnostics.Install(runtime))
        {
            NavigationEvidence.Publish(
                NavigationEvidence.NoPreviousStep, "Connect", NavigationReasons.Initial);
        }
        D2TestSupport.Shutdown(runtime);

        var payload = Assert.Single(ReadPayloads(runtime).OfType<UiNavigationPayload>());
        Assert.Equal("(none)", payload.From);              // 诚实：之前不在任何一页
        Assert.Equal("Connect", payload.To);
        Assert.Equal(NavigationReasons.Initial, payload.ReasonCode);

        D2TestSupport.Dispose(runtime);
    }

    // ─────────────────────────── ② 诚实：不制造、不编造 ───────────────────────────

    [Fact]
    public void Fixture13_NoChangeMeansNoEventAndEmptyFromBecomesNone()
    {
        var runtime = StartRuntime("nav-honest");
        using (CoreDiagnostics.Install(runtime))
        {
            Assert.False(NavigationEvidence.Publish("Connect", "Connect", NavigationReasons.RailClick)); // 同页 ≠ 变化
            Assert.False(NavigationEvidence.Publish("Connect", "", NavigationReasons.RailClick));        // 没有目标页
            Assert.False(NavigationEvidence.Publish("Connect", "   ", NavigationReasons.RailClick));
            Assert.True(NavigationEvidence.Publish("", "SelectData", NavigationReasons.RailClick));      // 空 from ⇒ (none)
        }
        D2TestSupport.Shutdown(runtime);

        var payload = Assert.Single(ReadPayloads(runtime).OfType<UiNavigationPayload>());
        Assert.Equal(NavigationEvidence.NoPreviousStep, payload.From);
        Assert.Equal("SelectData", payload.To);

        D2TestSupport.Dispose(runtime);
    }

    [Fact]
    public void Fixture13_UnknownReasonIsDowngradedNotInvented()
    {
        // 封闭集合：不知道就写 unspecified，绝不把自由文本带进事件。
        Assert.Equal(NavigationReasons.Unspecified, NavigationReasons.Normalize("用户点了什么东西"));
        Assert.Equal(NavigationReasons.Unspecified, NavigationReasons.Normalize(null));
        Assert.Equal(NavigationReasons.Unspecified, NavigationReasons.Normalize(""));
        Assert.Equal(NavigationReasons.RailClick, NavigationReasons.Normalize(NavigationReasons.RailClick));

        Assert.False(NavigationReasons.IsKnown("rail click"));   // 带空格的自造变体不算已知
        Assert.False(NavigationReasons.IsKnown("RailClick"));    // 大小写不同也不算

        // 所有已登记原因码都必须能被认出来（否则 Normalize 会把合法原因悄悄降级）。
        foreach (var reason in NavigationReasons.All)
            Assert.True(NavigationReasons.IsKnown(reason), "未登记进 All 的原因码：" + reason);

        Assert.Contains(NavigationReasons.Unspecified, NavigationReasons.All);
        Assert.Contains(NavigationReasons.Initial, NavigationReasons.All);
    }

    [Fact]
    public void Fixture13_UnknownReasonArrivesAsUnspecifiedInTheEvent()
    {
        var runtime = StartRuntime("nav-unknown-reason");
        using (CoreDiagnostics.Install(runtime))
        {
            NavigationEvidence.Publish("Connect", "Progress", "我看心情跳的");
        }
        D2TestSupport.Shutdown(runtime);

        var payload = Assert.Single(ReadPayloads(runtime).OfType<UiNavigationPayload>());
        Assert.Equal(NavigationReasons.Unspecified, payload.ReasonCode);

        D2TestSupport.Dispose(runtime);
    }

    // ─────────────────────────── ③ 发布时机与原因归属（源码级证明） ───────────────────────────

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
        }
        throw new InvalidOperationException("找不到仓库根：" + AppContext.BaseDirectory);
    }

    private static string WinUiFile(params string[] parts) =>
        Path.Combine(new[] { RepoRoot(), "src", "PCMig.WinUI" }.Concat(parts).ToArray());

    private static IEnumerable<string> WinUiSourceFiles() =>
        Directory.EnumerateFiles(WinUiFile(), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar));

    [Fact]
    public void Fixture13_TheEventIsPublishedOnlyAfterTheStepReallyChanged()
    {
        var src = File.ReadAllText(WinUiFile("Presentation", "StepNavigation.cs"));

        var set = src.IndexOf("Set(ref _current, value)", StringComparison.Ordinal);
        var publish = src.IndexOf("NavigationEvidence.Publish(", StringComparison.Ordinal);
        Assert.True(set > 0, "StepNavigation.cs 里找不到状态变更点 Set(ref _current, value)");
        Assert.True(publish > set,
            "导航事件必须在状态**已切换成功之后**发布；在之前发布就是把「打算切页」说成「已经切过去了」");

        // 唯一发布点：多一条发布路径 = 多一份可能与真实状态不一致的"导航事实"。
        // 允许的恰好两处：状态切换成功的 setter 分支、以及首屏归属 AnnounceInitial（各一处）。
        var setterStart = src.IndexOf("public StepKind Current", StringComparison.Ordinal);
        var setterEnd = src.IndexOf("public StepNavItem CurrentItem", setterStart, StringComparison.Ordinal);
        Assert.True(setterStart > 0 && setterEnd > setterStart, "找不到 StepNavigation.Current 的成员边界");
        var setter = src.Substring(setterStart, setterEnd - setterStart);
        Assert.True(Regex.Matches(setter, "NavigationEvidence\\.Publish\\(").Count == 1,
            "Current 的 setter 里必须恰好一处发布点");

        var announceStart = src.IndexOf("public void AnnounceInitial()", StringComparison.Ordinal);
        Assert.True(announceStart > 0, "找不到 AnnounceInitial");
        var announceEnd = src.IndexOf("private static string NavToken", announceStart, StringComparison.Ordinal);
        Assert.True(announceEnd > announceStart, "找不到 AnnounceInitial 的成员边界");
        var announce = src.Substring(announceStart, announceEnd - announceStart);
        Assert.True(Regex.Matches(announce, "NavigationEvidence\\.Publish\\(").Count == 1,
            "AnnounceInitial 里必须恰好一处发布点");

        var publishSites = Regex.Matches(src, "NavigationEvidence\\.Publish\\(");
        Assert.True(publishSites.Count == 2,
            "StepNavigation.cs 只允许两处发布点（切换成功 / 首屏归属），实际 " + publishSites.Count);

        // 发布必须带 id 与 component（不是发一条无出处的匿名事件）。
        Assert.Contains("component: \"Shell\"", src, StringComparison.Ordinal);
    }

    [Fact]
    public void Fixture13_TheReasonIsConsumedBeforeAnyEarlyReturn()
    {
        var src = File.ReadAllText(WinUiFile("Presentation", "StepNavigation.cs"));

        var setterStart = src.IndexOf("public StepKind Current", StringComparison.Ordinal);
        Assert.True(setterStart > 0, "找不到 StepNavigation.Current");
        var setterEnd = src.IndexOf("public StepNavItem CurrentItem", setterStart, StringComparison.Ordinal);
        Assert.True(setterEnd > setterStart, "找不到 Current 之后的成员边界");
        var setter = src.Substring(setterStart, setterEnd - setterStart);

        var take = setter.IndexOf("var reason = _pendingReason;", StringComparison.Ordinal);
        var reset = setter.IndexOf("_pendingReason = NavigationReasons.Unspecified;", StringComparison.Ordinal);
        var earlyReturn = setter.IndexOf("if (_current == value) return;", StringComparison.Ordinal);

        Assert.True(take > 0 && reset > take, "原因必须在 setter 开头被取走并复位");
        Assert.True(earlyReturn > 0, "找不到同页提前返回分支");
        Assert.True(reset < earlyReturn,
            "原因复位必须早于任何提前返回：否则一次「同页赋值」会把原因留给下一次真实切换，等于给下一次导航编造原因");
    }

    [Fact]
    public void Fixture13_EveryRealNavigationCallSiteCarriesAReason()
    {
        var src = File.ReadAllText(WinUiFile("MainWindow.xaml.cs"));

        var calls = new List<string>();
        var search = 0;
        while (true)
        {
            var i = src.IndexOf(".GoTo(", search, StringComparison.Ordinal);
            if (i < 0) break;
            var open = i + ".GoTo".Length;
            var depth = 0;
            var end = -1;
            for (var p = open; p < src.Length; p++)
            {
                if (src[p] == '(') depth++;
                else if (src[p] == ')')
                {
                    depth--;
                    if (depth == 0) { end = p; break; }
                }
            }
            Assert.True(end > open, "无法解析 .GoTo( 调用");
            calls.Add(src.Substring(open + 1, end - open - 1));
            search = end;
        }

        Assert.True(calls.Count >= 8, "导航调用点数量异常（预期至少 8 处：侧栏/键盘 4+2/Footer 2/探针），实际 " + calls.Count);

        var ignored = new[] { "NavigationReasons." };
        var withoutReason = calls.Where(c => !ignored.Any(t => c.Contains(t, StringComparison.Ordinal))).ToList();
        Assert.True(withoutReason.Count == 0,
            "这些导航调用点没有带原因码（事后无法回答「为什么跳页」）：\n" + string.Join("\n", withoutReason));
    }

    [Fact]
    public void Fixture13_EveryReasonTokenUsedByTheUiIsRegistered()
    {
        var byName = typeof(NavigationReasons)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);

        var used = new List<string>();
        foreach (var file in WinUiSourceFiles())
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "NavigationReasons\\.(\\w+)\\b(?!\\s*\\()"))
                used.Add(m.Groups[1].Value);

        Assert.True(used.Count >= 5, "预期 UI 侧至少引用 5 个不同原因码，实际 " + used.Count);
        foreach (var name in used.Distinct(StringComparer.Ordinal))
        {
            Assert.True(byName.ContainsKey(name), "UI 引用了不存在的 NavigationReasons." + name);
            Assert.True(NavigationReasons.IsKnown(byName[name]),
                $"NavigationReasons.{name}（\"{byName[name]}\"）不在封闭集合 All 内 ⇒ 会被降级成 unspecified，白记一次");
        }
    }

    [Fact]
    public void Fixture13_TheInitialStepIsAnnouncedExactlyOnceByTheAssemblyRoot()
    {
        var callers = (from file in WinUiSourceFiles()
                       // 只认**调用点**（`.AnnounceInitial()`），排除 StepNavigation.cs 里的成员声明。
                       where File.ReadAllText(file).Contains(".AnnounceInitial()", StringComparison.Ordinal)
                       select Path.GetFileName(file)).ToList();

        Assert.Equal(new[] { "MainWindow.xaml.cs" }, callers);
        Assert.Contains("Nav.AnnounceInitial();", File.ReadAllText(WinUiFile("MainWindow.xaml.cs")), StringComparison.Ordinal);
    }
}