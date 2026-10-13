using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using PCMig.Diagnostics;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using PCMig.Diagnostics.Export;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D6.1 §2（导出/脱敏正确性）**收口测试**。对应验收清单里的：
///   §20.2 每个 JSON entry 必须能 Parse；§20.3 缺 Health 不得说"完整"；
///   §20.4 Personal 结构化脱敏 + Secret canary 不在包内；§20.5 同实例两次导出 key 不同；
///   §20.6 默认包不可直接关联（无原始 SessionId）；§20.16 active segment 必须进包。
/// </summary>
public sealed class D61ExportClosureTests : IDisposable
{
    private const string SecretCanary = "Sup3rSecret!P@ssw0rd";
    private const string SyntheticUser = "D61_SYNTH_USER";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-d61e-" + Guid.NewGuid().ToString("N")[..8]);

    public D61ExportClosureTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* 句柄延迟释放 */ }
    }

    /// <summary>存储根故意带上"用户名"形状的路径：它属 Personal，明文不得进事件/包。</summary>
    private string SyntheticStorageRoot(string tag) =>
        Path.Combine(_root, tag, "Users", SyntheticUser, "AppData", "Local", "PCMig", "Diagnostics");

    private DiagnosticRuntime StartRuntime(string tag, int events = 0, string? message = null)
    {
        var runtime = DiagnosticRuntime.Start(new DiagnosticRuntimeOptions
        {
            StorageRoot = SyntheticStorageRoot(tag),
            InitialMode = CaptureMode.Operational,
            AppVersion = "d61-test",
            ShutdownBudgetMs = 5000,
        }, out var degraded);
        Assert.NotNull(runtime.Store);

        // ★ 用 **Operational** 投递类的事件 ★ —— 若用 Verbose（如 UI.InputObserved），在默认
        //   Operational 模式下会被入口过滤掉，测试里的"不存在"断言就变成假绿（什么都验不到）。
        for (var i = 0; i < events; i++)
        {
            runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                PersistenceEvents.WriteFailed,
                DiagnosticContext.Root(runtime.SessionId, "D61").WithControl("Step1.Connect"),
                new PstWritePayload("Receipt", "Move", true, "IOException"),
                Outcome: DiagnosticOutcome.Failed,
                ErrorDomain: ErrorDomain.Managed,
                ExceptionType: "IOException",
                Message: message));
        }

        return runtime;
    }

    /// <summary>等 writer 真正把这些事件写下去（有界）。</summary>
    private static void WaitForWritten(DiagnosticRuntime runtime, int expected)
        => Assert.True(D2TestSupport.WaitUntil(() => runtime.Health.EventsWritten >= expected, 10_000),
            $"只写下了 {runtime.Health.EventsWritten} 条，期望 {expected}");

    /// <summary>
    /// 等 **viewer 消费者**也把这些事件追上（有界）。
    /// ★ D6.3 修正 ★ viewer 与 writer 是两条独立通道：只等 <see cref="WaitForWritten"/> 就去读
    /// <c>TryGetViewerSnapshot</c>，在并发负载下会读到落后快照（实测在全量套件里偶发失败）。
    /// 这里只补"等的条件"，断言强度不变（仍要求这些事件一条不少地出现在 viewer 里）。
    /// </summary>
    private static void WaitForViewer(DiagnosticRuntime runtime, int expected)
    {
        var seen = 0;
        Assert.True(D2TestSupport.WaitUntil(() =>
        {
            if (!runtime.TryGetViewerSnapshot(out var snapshot)) return false;
            seen = snapshot.Count(e => e.Descriptor.Name == PersistenceEvents.WriteFailed.Name);
            return seen >= expected;
        }, 10_000), $"viewer 只看到 {seen} 条，期望 {expected}");
    }

    /// <summary>活动段可能仍被 writer 打开 ⇒ 共享方式读（不复制产品逻辑，只是同样的文件共享口径）。</summary>
    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static Dictionary<string, string> ReadPackage(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            map[entry.FullName] = reader.ReadToEnd();
        }
        return map;
    }

    // ────────────────────────── §20.3 不知道 ≠ 完整 ──────────────────────────

    [Fact]
    public void MissingHealthSnapshotNeverClaimsEvidenceComplete()
    {
        var runtime = StartRuntime("health", events: 3);
        var sessionDir = runtime.Store!.SessionDir;
        WaitForWritten(runtime, 3);
        D2TestSupport.Shutdown(runtime);

        var outcome = new DiagnosticPackageExporter().Export(new DiagnosticExportRequest
        {
            SessionDir = sessionDir,
            OutputDirectory = Path.Combine(_root, "out-health"),
            Health = null,                                   // ★ 故意不给健康快照
        });
        Assert.True(outcome.Succeeded, outcome.FailureReason);

        var package = ReadPackage(outcome.ZipPath!);
        using var summary = JsonDocument.Parse(package["summary.json"]);

        Assert.Equal("Unknown", summary.RootElement.GetProperty("evidenceCompleteness").GetString());
        var note = summary.RootElement.GetProperty("evidenceNote").GetString();
        Assert.Contains("未知", note, StringComparison.Ordinal);
        Assert.DoesNotContain("采集证据完整", package["summary.json"], StringComparison.Ordinal);
        Assert.Equal("health-snapshot-missing",
            summary.RootElement.GetProperty("collectionHealth").GetProperty("unavailable").GetString());
    }

    // ────────────────────────── §20.5 / §20.6 key 与关联性 ──────────────────────────

    [Fact]
    public void EveryExportGetsItsOwnKeyEvenFromTheSameInstance()
    {
        var runtime = StartRuntime("keys", events: 5);
        var sessionDir = runtime.Store!.SessionDir;
        WaitForWritten(runtime, 5);
        D2TestSupport.Shutdown(runtime);

        var exporter = new DiagnosticPackageExporter();
        var request = new DiagnosticExportRequest
        {
            SessionDir = sessionDir,
            OutputDirectory = Path.Combine(_root, "out-keys"),
        };

        var first = exporter.Export(request);
        var firstKey = exporter.LastExportKeyId;
        var second = exporter.Export(request);
        var secondKey = exporter.LastExportKeyId;

        Assert.True(first.Succeeded && second.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(firstKey));
        Assert.NotEqual(firstKey, secondKey);                // ★ 同实例两次导出，key 必须不同

        var packageA = ReadPackage(first.ZipPath!);
        var packageB = ReadPackage(second.ZipPath!);
        using var manifestA = JsonDocument.Parse(packageA["manifest.json"]);
        using var manifestB = JsonDocument.Parse(packageB["manifest.json"]);
        Assert.NotEqual(
            manifestA.RootElement.GetProperty("redaction").GetProperty("keyId").GetString(),
            manifestB.RootElement.GetProperty("redaction").GetProperty("keyId").GetString());
    }

    [Fact]
    public void DefaultPackagesDoNotCarryTheRawSessionId()
    {
        var runtime = StartRuntime("unlinkable", events: 4);
        var rawSessionId = runtime.SessionId;
        var sessionDir = runtime.Store!.SessionDir;
        WaitForWritten(runtime, 4);
        D2TestSupport.Shutdown(runtime);

        var rawIdText = DiagnosticId.Format(rawSessionId);
        var request = new DiagnosticExportRequest
        {
            SessionDir = sessionDir,
            OutputDirectory = Path.Combine(_root, "out-unlink"),
        };

        var normal = new DiagnosticPackageExporter().Export(request);
        var advanced = new DiagnosticPackageExporter().Export(request with { PreserveCrossPackageCorrelation = true });
        Assert.True(normal.Succeeded && advanced.Succeeded);

        var normalText = string.Join("\n", ReadPackage(normal.ZipPath!).Select(kv => kv.Key + "\n" + kv.Value));
        Assert.DoesNotContain(rawIdText, normalText, StringComparison.OrdinalIgnoreCase);   // ★ 默认不可直接关联
        Assert.Contains("S1", ReadPackage(normal.ZipPath!)["manifest.json"], StringComparison.Ordinal);

        // 高级模式（用户显式选择保留稳定关联信息）才写原始 id —— 证明"默认"与"显式"确实不同。
        var advancedText = ReadPackage(advanced.ZipPath!)["manifest.json"];
        Assert.Contains(rawIdText, advancedText, StringComparison.OrdinalIgnoreCase);
    }

    // ────────────────────────── §20.4 Personal / Secret ──────────────────────────

    [Fact]
    public void StorageRootAndSecretNeverLeaveInClearText()
    {
        var runtime = StartRuntime("privacy", events: 6, message: $"写入失败 password={SecretCanary}");
        var sessionDir = runtime.Store!.SessionDir;
        WaitForWritten(runtime, 6);                          // 必须先真的写下去，否则下面的"不存在"是假绿

        // ① 本地事件里：存储根只以**令牌**出现（不含用户名形状的路径片段）。
        //    活动段仍被 writer 打开着 ⇒ 必须以共享方式读（与产品侧读法一致）。
        var localEvents = string.Join("\n",
            Directory.GetFiles(Path.Combine(sessionDir, "events"), "*.jsonl").Select(ReadAllTextShared));
        Assert.Contains("PST.WriteFailed", localEvents, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticUser, localEvents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SecretCanary, localEvents, StringComparison.Ordinal);

        D2TestSupport.Shutdown(runtime);

        // ② 导出包里：同样不得出现（包括 session.json 的 storageRootToken 与深度清洗后的事件）。
        var outcome = new DiagnosticPackageExporter().Export(new DiagnosticExportRequest
        {
            SessionDir = sessionDir,
            OutputDirectory = Path.Combine(_root, "out-privacy"),
            Health = runtime.GetHealthSnapshot(),
        });
        Assert.True(outcome.Succeeded, outcome.FailureReason);

        var package = ReadPackage(outcome.ZipPath!);
        var wholePackage = string.Join("\n", package.Select(kv => kv.Key + "\n" + kv.Value));
        Assert.DoesNotContain(SyntheticUser, wholePackage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SecretCanary, wholePackage, StringComparison.Ordinal);

        var sessionEntry = package.Single(kv => kv.Key.EndsWith("/session.json", StringComparison.Ordinal)).Value;
        using var sessionDoc = JsonDocument.Parse(sessionEntry);
        Assert.True(sessionDoc.RootElement.TryGetProperty("storageRootToken", out _));
        Assert.False(sessionDoc.RootElement.TryGetProperty("storageRoot", out _));
    }

    // ────────────────────────── §20.16 active segment 必须进包 ──────────────────────────

    [Fact]
    public void ActiveSegmentIsSealedAtTheCutoffSoTheFreshestEventsAreIncluded()
    {
        const int published = 25;
        var runtime = StartRuntime("cutoff", events: published);
        WaitForWritten(runtime, published);                  // 事件真的落盘之后才谈截止点
        WaitForViewer(runtime, published);                   // ★ D6.3 ★ viewer 是独立消费者，必须也追平再读快照

        Assert.True(runtime.TryGetViewerSnapshot(out var all), "viewer 快照不可得");
        var publishedSequences = all.Where(e => e.Descriptor.Name == PersistenceEvents.WriteFailed.Name)
            .Select(e => e.Sequence).ToArray();
        Assert.True(publishedSequences.Length >= published);
        var lastSequence = publishedSequences.Max();

        var sessionDir = runtime.Store!.SessionDir;

        // ★ 关键动作：导出前建立截止点（有界等待 + 就地把活动段封存）——**不关闭运行时、不暂停任何业务**。
        var cutoff = runtime.PrepareExportCutoff(TimeSpan.FromSeconds(3));
        Assert.Equal("acknowledged", cutoff.FlushStatus);
        Assert.True(cutoff.CutoffSequence >= lastSequence, $"cutoff={cutoff.CutoffSequence} last={lastSequence}");

        var outcome = new DiagnosticPackageExporter().Export(new DiagnosticExportRequest
        {
            SessionDir = sessionDir,
            OutputDirectory = Path.Combine(_root, "out-cutoff"),
            Health = runtime.GetHealthSnapshot(),
            Cutoff = cutoff,
        });
        Assert.True(outcome.Succeeded, outcome.FailureReason);

        var package = ReadPackage(outcome.ZipPath!);
        var eventEntries = package.Where(kv => kv.Key.Contains("/events/") && kv.Key.EndsWith(".jsonl", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(eventEntries);

        var sequences = new List<long>();
        foreach (var entry in eventEntries)
            foreach (var line in entry.Value.Split('\n'))
            {
                if (line.Trim().Length == 0) continue;
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("sequence", out var seq)) sequences.Add(seq.GetInt64());
            }

        Assert.True(sequences.Contains(lastSequence),
            $"最新事件（seq={lastSequence}）不在包里。" +
            $"cutoff: requested={cutoff.RequestedSequence} cutoff={cutoff.CutoffSequence} " +
            $"pending={cutoff.PendingAtCutoff} flush={cutoff.FlushStatus} anyTailLoss={cutoff.AnyTailLoss}；" +
            $"包内事件段={eventEntries.Length} 个，包内序号范围=[{(sequences.Count == 0 ? "空" : $"{sequences.Min()}..{sequences.Max()}")}]");
        Assert.True(sequences.Max() >= lastSequence);

        using var manifest = JsonDocument.Parse(package["manifest.json"]);
        var cutoffNode = manifest.RootElement.GetProperty("cutoff");
        Assert.Equal("acknowledged", cutoffNode.GetProperty("flushStatus").GetString());
        Assert.Equal(0, cutoffNode.GetProperty("pendingAtCutoff").GetInt64());
        Assert.True(cutoffNode.GetProperty("cutoffSequence").GetInt64() >= lastSequence);
        Assert.False(cutoffNode.GetProperty("anyTailLoss").GetBoolean());
        Assert.DoesNotContain("active-segment-skipped", package["manifest.json"], StringComparison.Ordinal);

        D2TestSupport.Shutdown(runtime);
    }

    // ────────── ★ D61 偶发失败收口 ★ 截止点之后的新段 ≠ 包尾丢失 ──────────

    private static void PublishOne(DiagnosticRuntime runtime, string? message = null)
        => runtime.Publisher.TryPublish(new DiagnosticEventDraft(
            PersistenceEvents.WriteFailed,
            DiagnosticContext.Root(runtime.SessionId, "D61").WithControl("Step1.Connect"),
            new PstWritePayload("Receipt", "Move", true, "IOException"),
            Outcome: DiagnosticOutcome.Failed,
            ErrorDomain: ErrorDomain.Managed,
            ExceptionType: "IOException",
            Message: message));

    /// <summary>等某族真的多出一个"未封段"（有界）：在线系统在封段之后继续落事件就是这个形状。</summary>
    private static void WaitForUnsealedSegment(string sessionDir, string family)
    {
        var dir = Path.Combine(sessionDir, family);
        Assert.True(D2TestSupport.WaitUntil(
                () => Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.jsonl").Any(f => !File.Exists(f + ".manifest.json")),
                10_000),
            $"{family} 目录里始终没出现未封段");
    }

    private static long[] SequencesInPackage(Dictionary<string, string> package, string folder)
    {
        var sequences = new List<long>();
        foreach (var entry in package.Where(kv => kv.Key.Contains(folder) && kv.Key.EndsWith(".jsonl", StringComparison.Ordinal)))
            foreach (var line in entry.Value.Split('\n'))
            {
                if (line.Trim().Length == 0) continue;
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("sequence", out var seq)) sequences.Add(seq.GetInt64());
            }
        return sequences.ToArray();
    }

    /// <summary>
    /// ★ 回归本轮偶发失败 ★ 截止点**干净**（acknowledged / 无尾巴）之后又落了一条事件（在线系统的常态），
    /// 导出必须：① 不因此自称"包尾丢失"；② 如实登记"截止点之后的段被排除"；
    /// ③ 截止点自身的覆盖一条不少地进包；④ **不**把截止点之后的内容混进包里冒充覆盖。
    /// </summary>
    [Fact]
    public void OneEventPublishedAfterTheCutoffIsExcludedWithoutClaimingTailLoss()
    {
        const int published = 25;
        var runtime = StartRuntime("post-cutoff", events: published);
        WaitForWritten(runtime, published);
        WaitForViewer(runtime, published);

        Assert.True(runtime.TryGetViewerSnapshot(out var all), "viewer 快照不可得");
        var lastSequence = all.Where(e => e.Descriptor.Name == PersistenceEvents.WriteFailed.Name).Max(e => e.Sequence);

        var sessionDir = runtime.Store!.SessionDir;
        var cutoff = runtime.PrepareExportCutoff(TimeSpan.FromSeconds(3));
        Assert.Equal("acknowledged", cutoff.FlushStatus);
        Assert.False(cutoff.AnyTailLoss, "截止点本身不该有尾巴丢失");

        // 截止点之后照样会发生事（维护循环的丢失/存储/滞后事件就是这样来的）。
        var writtenBefore = runtime.Health.EventsWritten;
        PublishOne(runtime, "post-cutoff");
        Assert.True(D2TestSupport.WaitUntil(() => runtime.Health.EventsWritten > writtenBefore, 10_000),
            "截止点之后的新事件没落盘");
        WaitForUnsealedSegment(sessionDir, "events");

        var outcome = new DiagnosticPackageExporter().Export(new DiagnosticExportRequest
        {
            SessionDir = sessionDir,
            OutputDirectory = Path.Combine(_root, "out-post-cutoff"),
            Health = runtime.GetHealthSnapshot(),
            Cutoff = cutoff,
        });
        Assert.True(outcome.Succeeded, outcome.FailureReason);

        var package = ReadPackage(outcome.ZipPath!);
        var manifestText = package["manifest.json"];

        using var manifest = JsonDocument.Parse(manifestText);
        var cutoffNode = manifest.RootElement.GetProperty("cutoff");
        Assert.Equal("acknowledged", cutoffNode.GetProperty("flushStatus").GetString());
        Assert.Equal(0, cutoffNode.GetProperty("pendingAtCutoff").GetInt64());
        Assert.False(cutoffNode.GetProperty("anyTailLoss").GetBoolean(),
            "截止点之后又落了一条事件，不等于包尾丢了东西");
        Assert.True(cutoffNode.GetProperty("postCutoffSegmentsExcluded").GetInt32() >= 1,
            "至少那条截止点之后落盘的新段要被登记为「排除」");
        Assert.Contains("post-cutoff-segment-excluded:", manifestText, StringComparison.Ordinal);
        Assert.DoesNotContain("active-segment-skipped:events/", manifestText, StringComparison.Ordinal);

        // ③ 截止点的覆盖一条不少（最新那条仍在包里）；④ 截止点之后的内容不许混进包冒充覆盖。
        var sequences = SequencesInPackage(package, "/events/");
        Assert.NotEmpty(sequences);
        Assert.Contains(lastSequence, sequences);
        Assert.True(sequences.Max() <= cutoff.CutoffSequence,
            $"包内出现了截止点（{cutoff.CutoffSequence}）之后的事件：max={sequences.Max()}");

        D2TestSupport.Shutdown(runtime);
    }

    /// <summary>
    /// ★ 反掩盖守卫 ★ 截止点处**封段失败**时，目录里那个没封成的活动段仍然必须算作包尾丢失 ——
    /// 不能因为"它没 manifest"和"新段的形状一样"就把它当成"截止点之后的段"放过。
    /// </summary>
    [Fact]
    public void AFailedSealAtTheCutoffIsStillReportedAsTailLoss()
    {
        const int published = 25;
        var runtime = StartRuntime("seal-failed", events: published);
        WaitForWritten(runtime, published);

        var sessionDir = runtime.Store!.SessionDir;
        var cutoff = runtime.PrepareExportCutoff(TimeSpan.FromSeconds(3));
        Assert.Equal("acknowledged", cutoff.FlushStatus);

        var writtenBefore = runtime.Health.EventsWritten;
        PublishOne(runtime, "post-cutoff");
        Assert.True(D2TestSupport.WaitUntil(() => runtime.Health.EventsWritten > writtenBefore, 10_000), "新事件没落盘");
        WaitForUnsealedSegment(sessionDir, "events");

        // 复现"截止点处封段没成功"：此时那个未封段就是**截止点自己的尾巴**，不是后来的新段。
        var failedSeal = cutoff with { FlushStatus = "seal-failed", AnyTailLoss = true };
        var outcome = new DiagnosticPackageExporter().Export(new DiagnosticExportRequest
        {
            SessionDir = sessionDir,
            OutputDirectory = Path.Combine(_root, "out-seal-failed"),
            Health = runtime.GetHealthSnapshot(),
            Cutoff = failedSeal,
        });
        Assert.True(outcome.Succeeded, outcome.FailureReason);

        var manifestText = ReadPackage(outcome.ZipPath!)["manifest.json"];
        using var manifest = JsonDocument.Parse(manifestText);
        var cutoffNode = manifest.RootElement.GetProperty("cutoff");
        Assert.True(cutoffNode.GetProperty("anyTailLoss").GetBoolean(), "封段失败必须是包尾丢失");
        // ★ 按族断言 ★ 别的族（如 incidents）在负载下同样可能出现"截止点之后的新段"，那是正常的；
        //   这里只咬死一点：**events 族封段失败**时，那个未封段绝不许被当成"截止点之后的段"放过。
        Assert.Contains("active-segment-skipped:events/", manifestText, StringComparison.Ordinal);
        Assert.DoesNotContain("post-cutoff-segment-excluded:events/", manifestText, StringComparison.Ordinal);

        D2TestSupport.Shutdown(runtime);
    }

    /// <summary>没有截止点（调用方没给）时不许猜：未封段照旧算跳过、包照旧自称不完整。</summary>
    [Fact]
    public void WithoutACutoffAnUnsealedSegmentIsStillNotClaimedComplete()
    {
        const int published = 25;
        var runtime = StartRuntime("no-cutoff", events: published);
        WaitForWritten(runtime, published);
        WaitForUnsealedSegment(runtime.Store!.SessionDir, "events");

        var outcome = new DiagnosticPackageExporter().Export(new DiagnosticExportRequest
        {
            SessionDir = runtime.Store!.SessionDir,
            OutputDirectory = Path.Combine(_root, "out-no-cutoff"),
            Health = runtime.GetHealthSnapshot(),
        });
        Assert.True(outcome.Succeeded, outcome.FailureReason);

        var manifestText = ReadPackage(outcome.ZipPath!)["manifest.json"];
        using var manifest = JsonDocument.Parse(manifestText);
        var cutoffNode = manifest.RootElement.GetProperty("cutoff");
        Assert.Equal("not-provided", cutoffNode.GetProperty("flushStatus").GetString());
        Assert.True(cutoffNode.GetProperty("anyTailLoss").GetBoolean());
        Assert.Contains("active-segment-skipped", manifestText, StringComparison.Ordinal);
        Assert.DoesNotContain("post-cutoff-segment-excluded", manifestText, StringComparison.Ordinal);

        D2TestSupport.Shutdown(runtime);
    }
}