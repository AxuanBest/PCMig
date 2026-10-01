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
/// ★ D6.3 §6.3「统一 typed identifier 重映射」★
///
/// 审计结论：诊断包里 `jobId` / `actionId` / `operationId` / `objectId` / `traceId` /
/// `processIdentity`（`pid:<pid>:<启动时刻>`）这类**直接稳定标识符**是原样写的，
/// 于是同一台机器上导出的多个包可以拿去直接做关联。
///
/// 目标行为（本组测试逐条钉住）：
///   a) 默认路径下，包内**不得出现**原始标识符（含 `pid:` 前缀）；改写成每包假名（`JOB-…`/`PID-…`）。
///   b) 同一个包内，同一原值无论出现在多少条事件里，都是**同一个**假名 ⇒ 包内引用仍然自洽；
///      引用"本包会话"的因果/证据引用也仍然指向本包会话。
///   c) 同一个会话导出两次：同一个原值在两个包里得到**不同**假名 ⇒ 跨包不可直接关联。
///   d) 显式选择跨包关联（高级模式）时才相反：两包假名一致 —— 这是用户显式要的，不是默认泄漏。
///
/// 纪律：这里只断"包内字节"，不碰 DiagnosticPackageJson 的 internal 类型；
/// 隐私结论必须在**真导出**的产物上验证，不能在内存对象上验证。
/// </summary>
public sealed class D63IdentifierPrivacyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-d63id-" + Guid.NewGuid().ToString("N")[..8]);

    public D63IdentifierPrivacyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* 句柄延迟释放 */ }
    }

    [Fact]
    public void StableIdentifiersArePseudonymizedPerPackage()
    {
        var runtime = Start("ids", out var sessionDir);
        try
        {
            var actionId = Guid.NewGuid();
            var operationId = Guid.NewGuid();
            var correlationId = "corr-raw-d63";
            PublishRich(runtime, actionId, operationId, correlationId);
            var cutoff = runtime.PrepareExportCutoff(TimeSpan.FromSeconds(3));

            using var zip = Export(runtime, sessionDir, cutoff, tag: "ids", pinned: null);
            var events = ReadEventLines(zip);

            var sessionId = events[0].GetProperty("sessionId").GetString();
            Assert.NotEqual(DiagnosticId.Format(runtime.SessionId), sessionId);

            // ② 同一原值 ⇒ 同一假名（包内自洽），且带类别前缀（看得出是哪种标识符）。
            //    注意：包里还有会话起始等**没有** jobId 的事件，所以要按字段过滤后再断言。
            var jobIds = WithField(events, "jobId").Select(e => e.GetProperty("jobId").GetString()).Distinct().ToList();
            Assert.Single(jobIds);
            Assert.StartsWith("JOB-", jobIds[0]);
            Assert.DoesNotContain("JOB-D63-RAW", jobIds[0]);

            var objectIds = WithField(events, "objectId").Select(e => e.GetProperty("objectId").GetString()).Distinct().ToList();
            Assert.Single(objectIds);
            Assert.StartsWith("OBJ-", objectIds[0]);

            var actionIds = WithField(events, "actionId").Select(e => e.GetProperty("actionId").GetString()).Distinct().ToList();
            Assert.Single(actionIds);
            Assert.StartsWith("ACT-", actionIds[0]);

            var operationIds = WithField(events, "operationId").Select(e => e.GetProperty("operationId").GetString()).Distinct().ToList();
            Assert.Single(operationIds);
            Assert.StartsWith("OPR-", operationIds[0]);

            var correlations = WithField(events, "correlationId")
                .Select(e => e.GetProperty("correlationId").GetString()).Distinct().ToList();
            Assert.Single(correlations);
            Assert.StartsWith("COR-", correlations[0]);

            // ② 因果引用指向的仍是**本包会话**：引用不能被单独换成另一个假名，
            //    否则包内会出现"这条事件属于会话 A，它的原因却属于会话 B"的自相矛盾。
            var withCausation = events.Where(e => e.TryGetProperty("causation", out _)).ToList();
            Assert.NotEmpty(withCausation);
            foreach (var e in withCausation)
                Assert.Equal(sessionId, e.GetProperty("causation").GetProperty("sessionId").GetString());

            // ① 原始标识符不得出现在包内任何字节里（`pid:` 前缀也不许）。
            var allText = ReadAllText(zip);
            Assert.DoesNotContain("JOB-D63-RAW", allText, StringComparison.Ordinal);
            Assert.DoesNotContain("OBJ-D63-RAW", allText, StringComparison.Ordinal);
            Assert.DoesNotContain(DiagnosticId.Format(actionId), allText, StringComparison.Ordinal);
            Assert.DoesNotContain(DiagnosticId.Format(operationId), allText, StringComparison.Ordinal);
            Assert.DoesNotContain(correlationId, allText, StringComparison.Ordinal);
            Assert.DoesNotContain(DiagnosticId.Format(runtime.SessionId), allText, StringComparison.Ordinal);
            Assert.DoesNotContain("pid:", allText, StringComparison.Ordinal);

            // session.json 里的进程身份同样是假名（它原来是 `pid:<pid>:<启动时刻>`，跨包稳定的锚点）。
            var session = ReadJson(zip, zip.Entries.Select(e => e.FullName)
                .Single(n => n.EndsWith("/session.json", StringComparison.Ordinal)));
            Assert.StartsWith("PID-", session.GetProperty("processIdentity").GetString()!);
        }
        finally { Stop(runtime); }
    }

    [Fact]
    public void TheSameRawIdentifierGetsDifferentPseudonymsInDifferentPackages()
    {
        var runtime = Start("twopkg", out var sessionDir);
        try
        {
            var actionId = Guid.NewGuid();
            var operationId = Guid.NewGuid();
            PublishRich(runtime, actionId, operationId, "corr-raw-d63");

            var first = runtime.PrepareExportCutoff(TimeSpan.FromSeconds(3));
            using var zipA = Export(runtime, sessionDir, first, tag: "twopkg-a", pinned: null);
            var second = runtime.PrepareExportCutoff(TimeSpan.FromSeconds(3));
            using var zipB = Export(runtime, sessionDir, second, tag: "twopkg-b", pinned: null);

            var jobA = WithField(ReadEventLines(zipA), "jobId")[0].GetProperty("jobId").GetString();
            var jobB = WithField(ReadEventLines(zipB), "jobId")[0].GetProperty("jobId").GetString();

            // ③ 默认：每包独立假名 ⇒ 同一个原值在两个包里对不上。
            Assert.StartsWith("JOB-", jobA);
            Assert.StartsWith("JOB-", jobB);
            Assert.NotEqual(jobA, jobB);

            var sessionA = ReadEventLines(zipA)[0].GetProperty("sessionId").GetString();
            var sessionB = ReadEventLines(zipB)[0].GetProperty("sessionId").GetString();
            Assert.NotEqual(sessionA, sessionB);
            Assert.NotEqual(sessionA, DiagnosticId.Format(runtime.SessionId));

            // 两个包都必须仍然"包内自洽"：同一个包里的三条事件共享同一个 jobId 假名。
            Assert.Single(WithField(ReadEventLines(zipA), "jobId").Select(e => e.GetProperty("jobId").GetString()).Distinct());
            Assert.Single(WithField(ReadEventLines(zipB), "jobId").Select(e => e.GetProperty("jobId").GetString()).Distinct());
        }
        finally { Stop(runtime); }
    }

    [Fact]
    public void CrossPackageCorrelationIsOptInAndThenPseudonymsMatch()
    {
        var runtime = Start("optin", out var sessionDir);
        try
        {
            var actionId = Guid.NewGuid();
            var operationId = Guid.NewGuid();
            PublishRich(runtime, actionId, operationId, "corr-raw-d63");

            // 显式高级模式：复用同一个 pinned policy ⇒ 两包对同一原值给出同一假名。
            var pinned = RedactionPolicy.CreateForSession();
            var first = runtime.PrepareExportCutoff(TimeSpan.FromSeconds(3));
            using var zipA = Export(runtime, sessionDir, first, tag: "optin-a", pinned);
            var second = runtime.PrepareExportCutoff(TimeSpan.FromSeconds(3));
            using var zipB = Export(runtime, sessionDir, second, tag: "optin-b", pinned);

            var lineA = WithField(ReadEventLines(zipA), "jobId")[0];
            var lineB = WithField(ReadEventLines(zipB), "jobId")[0];

            Assert.Equal(lineA.GetProperty("jobId").GetString(), lineB.GetProperty("jobId").GetString());
            Assert.Equal(lineA.GetProperty("actionId").GetString(), lineB.GetProperty("actionId").GetString());
            Assert.Equal(lineA.GetProperty("operationId").GetString(), lineB.GetProperty("operationId").GetString());
            Assert.Equal(lineA.GetProperty("correlationId").GetString(), lineB.GetProperty("correlationId").GetString());

            // 但"可关联"不等于"原样导出"：假名仍然不是原值。
            Assert.NotEqual("JOB-D63-RAW", lineA.GetProperty("jobId").GetString());
            Assert.NotEqual(DiagnosticId.Format(actionId), lineA.GetProperty("actionId").GetString());
        }
        finally { Stop(runtime); }
    }

    // ───────────────────────── helpers ─────────────────────────

    private DiagnosticRuntime Start(string tag, out string sessionDir)
    {
        var root = Path.Combine(_root, tag);
        Directory.CreateDirectory(root);
        var runtime = DiagnosticRuntime.Start(D2TestSupport.Options(root), out _);
        sessionDir = runtime.Store!.SessionDir;
        return runtime;
    }

    private static void Stop(DiagnosticRuntime runtime)
    {
        D2TestSupport.Shutdown(runtime);
        D2TestSupport.Dispose(runtime);
    }

    /// <summary>三条事件共享同一组标识符；最后一条带因果引用（指向本会话的第 1 条）。</summary>
    private static void PublishRich(DiagnosticRuntime runtime, Guid actionId, Guid operationId, string correlationId)
    {
        var context = DiagnosticContext.Root(runtime.SessionId, "Test")
            .WithJob("JOB-D63-RAW")
            .WithObject("OBJ-D63-RAW")
            .WithAction(actionId)
            .WithOperation(operationId);

        for (var i = 0; i < 3; i++)
        {
            runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                PersistenceEvents.WriteFailed,
                context,
                new PstWritePayload("Receipt", "Move", true, "IOException"),
                Outcome: DiagnosticOutcome.Failed,
                ExceptionType: "IOException",
                Message: "D6.3 标识符隐私样例",
                Causation: i == 0 ? null : new EventRef(runtime.SessionId, 1),
                CorrelationId: correlationId));
        }
        Assert.True(D2TestSupport.WaitUntil(() => runtime.Health.EventsWritten >= 3));
    }

    private ZipArchive Export(DiagnosticRuntime runtime, string sessionDir, DiagnosticExportCutoff? cutoff, string tag, RedactionPolicy? pinned)
    {
        var outcome = new DiagnosticPackageExporter(pinned).Export(new DiagnosticExportRequest
        {
            SessionDir = sessionDir,
            OutputDirectory = Path.Combine(_root, tag, "out"),
            Health = runtime.GetHealthSnapshot(),
            Cutoff = cutoff,
            IncludeFlightWindows = false,
            PreserveCrossPackageCorrelation = pinned is not null,
        });
        Assert.True(outcome.Succeeded, outcome.FailureReason);
        return ZipFile.OpenRead(outcome.ZipPath!);
    }

    /// <summary>包内所有事件段的每一行（按序）。</summary>
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

    /// <summary>只保留**带该字段**的事件行（会话起始等事件没有业务标识符，不该参与标识符断言）。</summary>
    private static List<JsonElement> WithField(List<JsonElement> events, string field)
    {
        var filtered = events.Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(field, out var v)
                                        && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 }).ToList();
        Assert.NotEmpty(filtered);
        return filtered;
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

    private static JsonElement ReadJson(ZipArchive zip, string path) => ReadJson(ReadEntry(zip, path));
}