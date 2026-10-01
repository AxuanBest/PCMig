using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PCMig.Diagnostics;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using PCMig.Diagnostics.Abstractions.Serialization;
using PCMig.Diagnostics.Analysis.Feedback;
using PCMig.Diagnostics.Export;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// ★ D6.3 §6.5「不受支持的事件版本不得被洗白」★
///
/// 审计结论（P1-6）：一条 `eventVersion=999` 的事件在 Parse → ToJsonLine → Parse
/// 之后变成了 `eventVersion=1`、`versionUnsupported=false` ——即诊断包在"重写"别人的声明，
/// 把"我不认识这条证据"洗成了"这条证据正常"。规则引擎与反馈跟踪器随后都按正常证据消费它。
///
/// 要求的行为（本组测试逐条钉住）：
///   a) 有声明版本 ⇒ 原样保留（999 还是 999），**绝不改成当前版本**；
///   b) 声明不受支持时，重写后必须显式标 `versionUnsupported:true`，且载荷不被当成解码过的内容；
///   c) 完全没有版本字段的旧行：**不许发明一个版本**，只能显式标不受支持；
///   d) 真导出后包内字节仍然是 999/不受支持 ⇒ 包不会在导出环节把证据洗干净；
///   e) 反馈跟踪器与规则引擎吃同一道版本门：不受支持的事件不得凭空开出期望。
///
/// 纪律：这里只做**字节层面**的往返断言，不改任何业务语义。
/// </summary>
public sealed class D63VersionHonestyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-d63ver-" + Guid.NewGuid().ToString("N")[..8]);

    public D63VersionHonestyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* 句柄延迟释放 */ }
    }

    // ─────────────── a) 声明版本不被改写成当前版本 ───────────────

    [Fact]
    public void UnsupportedDeclaredVersionSurvivesParseAndRewriteUnchanged()
    {
        // 用**带载荷**的事件：只有载荷存在时，"读懂 / 读不懂"才有区别可断。
        var original = DiagnosticEventJson.ToJsonLine(D2TestSupport.Event(
            1, PersistenceEvents.WriteFailed, payload: new PstWritePayload("Receipt", "Move", true, "IOException")));
        Assert.Contains("\"eventVersion\":", original, StringComparison.Ordinal);
        Assert.Contains("\"payload\"", original, StringComparison.Ordinal);

        var tampered = Regex.Replace(original, "\"eventVersion\":\\d+", "\"eventVersion\":999");
        Assert.Contains("\"eventVersion\":999", tampered, StringComparison.Ordinal);

        Assert.True(DiagnosticEventJson.TryParse(tampered, out var parsed, out var error), error);
        Assert.NotNull(parsed);
        Assert.Equal(999, parsed!.DeclaredEventVersion);
        Assert.True(parsed.VersionUnsupported);

        // 载荷不得被当成"解码过的内容"：读不出就不许读出。
        Assert.Null(parsed.Payload);

        var rewritten = DiagnosticEventJson.ToJsonLine(parsed);
        Assert.Contains("\"eventVersion\":999", rewritten, StringComparison.Ordinal);
        Assert.Contains("\"versionUnsupported\":true", rewritten, StringComparison.Ordinal);
        Assert.Contains("unsupported-version", rewritten, StringComparison.Ordinal);

        // 幂等：再读一遍，结论不许变。
        Assert.True(DiagnosticEventJson.TryParse(rewritten, out var again, out var error2), error2);
        Assert.Equal(999, again!.DeclaredEventVersion);
        Assert.True(again.VersionUnsupported);
    }

    // ─────────────── c) 缺版本字段：不许发明版本 ───────────────

    [Fact]
    public void MissingVersionIsNeverInventedOnRewrite()
    {
        var original = DiagnosticEventJson.ToJsonLine(D2TestSupport.Event(2));
        var stripped = Regex.Replace(original, "\"eventVersion\":\\d+,", string.Empty);
        Assert.DoesNotContain("\"eventVersion\"", stripped, StringComparison.Ordinal);

        Assert.True(DiagnosticEventJson.TryParse(stripped, out var parsed, out var error), error);
        Assert.NotNull(parsed);
        Assert.Null(parsed!.DeclaredEventVersion);
        Assert.True(parsed.VersionUnsupported);

        var rewritten = DiagnosticEventJson.ToJsonLine(parsed);
        Assert.DoesNotContain("\"eventVersion\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("\"versionUnsupported\":true", rewritten, StringComparison.Ordinal);

        Assert.True(DiagnosticEventJson.TryParse(rewritten, out var again, out var error2), error2);
        Assert.Null(again!.DeclaredEventVersion);
        Assert.True(again.VersionUnsupported);
    }

    // ─────────────── d) 导出环节不得洗白（Fixture 5） ───────────────

    [Fact]
    public void Fixture5_ExportDoesNotLaunderAnUnsupportedDeclaredVersion()
    {
        var runtime = Start("export", out var sessionDir);
        try
        {
            PublishEvents(runtime, 3);

            // ★ D6.3 ★ 先抓住"当前活动段"，再建截止点：截止点会把**当时的活动段**封存并写进导出清单。
            //   若反过来（先建截止点、再取"最新段"），writer 只要在这两步之间轮转出新段，
            //   塞进去的那一行就落到清单之外的段里、永远不会进包 —— 全量套件里表现为偶发
            //   "包里找不到 sequence 900001"。
            var segment = LatestSegment(sessionDir, "events");
            Assert.True(segment is not null, "没有找到 events 段文件");

            var cutoff = runtime.PrepareExportCutoff(TimeSpan.FromSeconds(3));

            // 把一条**能解析但声明了不受支持版本**的行塞进将被打包的那一段（模拟旧版本/外部写入的证据）。
            var first = File.ReadAllLines(segment!).First(l => l.Trim().Length > 0);
            var tampered = Regex.Replace(first, "\"eventVersion\":\\d+", "\"eventVersion\":999");
            tampered = Regex.Replace(tampered, "\"sequence\":\\d+", "\"sequence\":900001");
            File.AppendAllText(segment!, tampered + "\n");

            using var zip = Export(runtime, sessionDir, cutoff, includeFlight: false, tag: "version");

            var versionLines = ReadEventLines(zip).Where(e => e.GetProperty("sequence").GetInt64() == 900001).ToArray();
            Assert.True(versionLines.Length == 1,
                $"包里序列号 900001 的行有 {versionLines.Length} 条（段={Path.GetFileName(segment!)}）");
            var packaged = versionLines[0];
            Assert.Equal(999, packaged.GetProperty("eventVersion").GetInt32());
            Assert.True(packaged.GetProperty("versionUnsupported").GetBoolean());

            // 而且包内任何一行都不许把这条证据说成"当前版本正常证据"。
            Assert.DoesNotContain("\"sequence\":900001,\"eventVersion\":1", ReadAllText(zip), StringComparison.Ordinal);
        }
        finally { Stop(runtime); }
    }

    // ─────────────── e) 反馈跟踪器与规则引擎同一条版本门 ───────────────

    [Fact]
    public void FeedbackTrackerDoesNotOpenExpectationsFromUnsupportedVersions()
    {
        var contracts = FeedbackContractRegistry.CreateDefault();
        var timeouts = new List<ExpectationTimeout>();
        var tracker = new PendingExpectationTracker(contracts, timeouts.Add);

        var slot = tracker.Stats();
        Assert.Equal(0, slot.UnsupportedVersionSkipped);

        // 正常事件：开出期望（先证明这条路本身是通的）。
        var actionId = Guid.NewGuid();
        tracker.Observe(ActionObserved(actionId, "Connect", 1));
        Assert.Equal(1, tracker.Stats().Begun);

        // 不受支持的版本：同样的"动作被观察"事件**不得**开出期望，
        // 否则一条没被我们理解的事件会变成"用户点了但没反馈"的假事件卡。
        var ghost = Guid.NewGuid();
        var unsupported = ActionObserved(ghost, "Verify", 2) with { VersionUnsupported = true };
        tracker.Observe(unsupported);

        var stats = tracker.Stats();
        Assert.Equal(1, stats.Begun);
        Assert.Equal(1, stats.UnsupportedVersionSkipped);
        Assert.Equal(1, stats.Pending);
        Assert.Equal(1, tracker.PendingCount);
    }

    // ────────────────────────────── helpers ──────────────────────────────

    private static DiagnosticEvent ActionObserved(Guid actionId, string actionKind, long sequence)
        => new()
        {
            SchemaVersion = DiagnosticEvent.CurrentSchemaVersion,
            Descriptor = UiEvents.UserActionObserved,
            SessionId = TestEvents.FixedSessionId,
            Sequence = sequence,
            TimestampUtc = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(sequence),
            MonotonicTimestamp = sequence * TimeSpan.TicksPerMillisecond,
            Level = DiagnosticLevel.Information,
            Delivery = DeliveryClass.Operational,
            EvidenceQuality = EvidenceQuality.Direct,
            CaptureMode = CaptureMode.Operational,
            ActionId = actionId,
            Payload = new UiActionPayload(actionKind, "click"),
        };

    private DiagnosticRuntime Start(string tag, out string sessionDir)
    {
        var root = Path.Combine(_root, tag, "store");
        var runtime = DiagnosticRuntime.Start(D2TestSupport.Options(root), out _);
        sessionDir = runtime.Store!.SessionDir;
        return runtime;
    }

    private static void Stop(DiagnosticRuntime runtime)
    {
        D2TestSupport.Shutdown(runtime);
        D2TestSupport.Dispose(runtime);
    }

    private static void PublishEvents(DiagnosticRuntime runtime, int count)
    {
        for (var i = 0; i < count; i++)
        {
            runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                PersistenceEvents.WriteFailed,
                DiagnosticContext.Root(runtime.SessionId, "Test").WithJob("JOB-D63-VER").WithObject($"obj-{i}"),
                new PstWritePayload("Receipt", "Move", true, "IOException"),
                Outcome: DiagnosticOutcome.Failed,
                ExceptionType: "IOException",
                Message: "D6.3 版本诚实性样例"));
        }
        Assert.True(D2TestSupport.WaitUntil(() => runtime.Health.EventsWritten >= count));
    }

    private static string? LatestSegment(string sessionDir, string family)
    {
        var dir = Path.Combine(sessionDir, family);
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, "*.jsonl")
            .OrderBy(f => f, StringComparer.Ordinal)
            .LastOrDefault();
    }

    private ZipArchive Export(DiagnosticRuntime runtime, string sessionDir, DiagnosticExportCutoff? cutoff, bool includeFlight, string tag)
    {
        var outcome = new DiagnosticPackageExporter().Export(new DiagnosticExportRequest
        {
            SessionDir = sessionDir,
            OutputDirectory = Path.Combine(_root, tag, "out"),
            Health = runtime.GetHealthSnapshot(),
            Cutoff = cutoff,
            IncludeFlightWindows = includeFlight,
        });
        Assert.True(outcome.Succeeded, outcome.FailureReason);
        return ZipFile.OpenRead(outcome.ZipPath!);
    }

    private static List<JsonElement> ReadEventLines(ZipArchive zip)
    {
        var lines = new List<JsonElement>();
        foreach (var entry in zip.Entries.Where(e => e.FullName.Contains("/events/", StringComparison.Ordinal)
                                                     && e.FullName.EndsWith(".jsonl", StringComparison.Ordinal)))
        {
            foreach (var line in ReadEntry(zip, entry.FullName).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                lines.Add(ReadJson(line));
        }
        Assert.NotEmpty(lines);
        return lines;
    }

    private static string ReadAllText(ZipArchive zip)
    {
        var builder = new StringBuilder();
        foreach (var entry in zip.Entries)
        {
            if (entry.Length > 2 * 1024 * 1024) continue;
            builder.Append(ReadEntry(zip, entry.FullName)).Append('\n');
        }
        return builder.ToString();
    }

    private static JsonElement ReadJson(string text)
    {
        var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static string ReadEntry(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        Assert.NotNull(entry);
        using var stream = entry!.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}