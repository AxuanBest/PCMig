using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using PCMig.Diagnostics.Analysis;
using PCMig.Diagnostics.Analysis.Feedback;
using PCMig.Diagnostics.Export;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D6 契约：诊断包导出。
///
/// 全部硬约束都在这里被机器验证：
///   ① **本地文件、绝不上传**（源码扫描：无任何网络类型）；
///   ② **allowlist**：只导诊断产物，业务存档（job/plan/receipt/verify/preflight）绝不入包；
///   ③ **二次脱敏**：导出用新 key 重映射路径令牌 ⇒ 包内可关联、**跨包不可关联**；
///   ④ **cutoff**：只导已封段，active 段跳过并在 manifest/warnings 里如实标注；
///   ⑤ **不覆盖**：同名换新名；`.partial` 只在成功后原子改名；
///   ⑥ **可独立阅读**：summary.json 自带事实/候选/缺失证据/下一步检查。
/// </summary>
public sealed class D6ExportTests : IDisposable
{
    private const string CanarySecret = "Sup3rSecret!P@ssw0rd";
    private const string CanaryHost = "fileserver01";
    private const string CanaryUser = "zhangsan";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-d6-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _runtimeRoot;

    public D6ExportTests()
    {
        Directory.CreateDirectory(_root);
        _runtimeRoot = Path.Combine(_root, "runtime");
        Directory.CreateDirectory(_runtimeRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* 句柄延迟释放 */ }
    }

    /// <summary>造一个真实会话目录（用真 runtime 产生事件/事件卡/会话元数据）。</summary>
    private DiagnosticRuntime CreateSessionWithData(out string sessionDir)
    {
        var options = D2TestSupport.Options(_runtimeRoot);
        var runtime = DiagnosticRuntime.Start(options, out _);
        sessionDir = runtime.Store!.SessionDir;

        // 触发一张事件卡（同时产生事件 + incidents.jsonl + DIA.IncidentOpened）
        for (var i = 0; i < 3; i++)
        {
            runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                PersistenceEvents.WriteFailed,
                DiagnosticContext.Root(runtime.SessionId, "Test").WithJob("JOB-D6").WithObject("obj-1"),
                new PstWritePayload("Receipt", "Move", true, "IOException"),
                Outcome: DiagnosticOutcome.Failed,
                ExceptionType: "IOException",
                Message: $"写入失败 password={CanarySecret} host={CanaryHost} user={CanaryUser}",
                Path: runtime.PathRef($@"\\{CanaryHost}\share\{CanaryUser}\secret.xlsx", PathRole.Target, "dst")));
        }

        Assert.True(D2TestSupport.WaitUntil(() => runtime.Health.EventsWritten >= 3));
        Assert.True(D2TestSupport.WaitUntil(() => runtime.ActiveIncidents.Count > 0));
        return runtime;
    }

    /// <summary>
    /// 造一个**已正常关闭**的会话（段已封 ⇒ 事件可入包），并带回导出需要的时点快照。
    /// 这样用例既能验证"已封段入包"，也不会与 cutoff 语义（active 段跳过）混淆。
    /// </summary>
    private (string SessionDir, DiagnosticHealthSnapshot Health, FlightStats? Flight, RuleEngineStats? Rules, Incident[] Incidents)
        CreateSealedSession()
    {
        var runtime = CreateSessionWithData(out var sessionDir);
        var health = runtime.GetHealthSnapshot();
        var flight = runtime.FlightStatistics();
        var rules = runtime.RuleEngineStatistics();
        var incidents = runtime.ActiveIncidents.ToArray();
        D2TestSupport.Shutdown(runtime);
        D2TestSupport.Dispose(runtime);
        return (sessionDir, health, flight, rules, incidents);
    }

    private static string[] ReadEntry(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        Assert.NotNull(entry);
        using var stream = entry!.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Split('\n').Where(l => l.Length > 0).ToArray();
    }

    private static string ReadEntryText(ZipArchive zip, string path) => string.Join("\n", ReadEntry(zip, path));

    /// <summary>
    /// ★ D6.1 §2.1 ★ 包内**每一个** JSON / JSONL 条目都必须能被 `JsonDocument.Parse` 成功解析。
    /// 任何一个 entry 解析失败 ⇒ 这个用例 FAIL（旧实现里两个非法 JSON 就是这样漏掉的：
    /// 测试当时只做 ReadAllText/Contains）。
    /// </summary>
    private static void AssertEveryJsonEntryParses(ZipArchive zip)
    {
        var checkedEntries = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            var isJson = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            var isJsonl = name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
            if (!isJson && !isJsonl) continue;

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var text = reader.ReadToEnd();

            if (isJson)
            {
                using var _ = JsonDocument.Parse(text);          // 抛异常即 FAIL
                checkedEntries++;
                continue;
            }

            var lineNo = 0;
            foreach (var line in text.Split('\n'))
            {
                lineNo++;
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                try
                {
                    using var _ = JsonDocument.Parse(trimmed);
                }
                catch (JsonException ex)
                {
                    throw new Xunit.Sdk.XunitException($"{name} 第 {lineNo} 行不是合法 JSON：{ex.Message}");
                }
            }
            checkedEntries++;
        }

        Assert.True(checkedEntries > 0, "包里没有任何 JSON 条目可校验");
    }

    /// <summary>会话目录名就是原始 SessionId（用来断言"默认包内不得出现原始 id"）。</summary>
    private static string SessionIdOf(string _, string sessionDir) =>
        Path.GetFileName(sessionDir.TrimEnd(Path.DirectorySeparatorChar));

    // ────────────────────────── ① 基本结构与完整性 ──────────────────────────

    [Fact]
    public void ExportProducesManifestSummaryChecksumsAndSessionData()
    {
        var (sessionDir, health, flight, rules, incidents) = CreateSealedSession();

        var exporter = new DiagnosticPackageExporter();
        var request = new DiagnosticExportRequest
        {
            SessionDir = sessionDir,
            OutputDirectory = Path.Combine(_root, "out"),
            AppVersion = "0.5.0",
            Health = health,
            Flight = flight,
            Rules = rules,
            Incidents = incidents,
        };

        var outcome = exporter.Export(request);
        Assert.True(outcome.Succeeded, outcome.FailureReason);
        Assert.NotNull(outcome.ZipPath);
        Assert.True(File.Exists(outcome.ZipPath!));
        Assert.EndsWith(".zip", outcome.ZipPath!, StringComparison.Ordinal);
        Assert.True(outcome.Bytes > 0);
        Assert.False(File.Exists(outcome.ZipPath! + ".partial"), "成功后不得残留 .partial");

        using var zip = ZipFile.OpenRead(outcome.ZipPath!);
        var names = zip.Entries.Select(e => e.FullName).ToArray();

        Assert.Contains("manifest.json", names);
        Assert.Contains("summary.json", names);
        Assert.Contains("checksums.sha256", names);
        Assert.Contains(names, n => n.EndsWith("/session.json", StringComparison.Ordinal));
        Assert.Contains(names, n => n.EndsWith("/catalog.json", StringComparison.Ordinal));
        Assert.Contains(names, n => n.Contains("/events/events-", StringComparison.Ordinal));

        // ★ D6.1 §2.1 ★ 包内**所有**声明为 JSON 的条目都必须能被真实解析
        //   （旧测试只做 ReadAllText/Contains，所以"非法 JSON"长期进了包也没被发现）。
        AssertEveryJsonEntryParses(zip);

        // manifest 必须声明"本地文件"、导出密钥、目录版本，并声明**默认跨包不可关联**。
        using var manifest = JsonDocument.Parse(ReadEntryText(zip, "manifest.json"));
        var m = manifest.RootElement;
        Assert.Equal("local-file-only", m.GetProperty("delivery").GetString());
        Assert.Equal(exporter.LastExportKeyId, m.GetProperty("redaction").GetProperty("keyId").GetString());
        Assert.Equal("unlinkable-by-default", m.GetProperty("redaction").GetProperty("crossPackageCorrelation").GetString());
        Assert.True(m.GetProperty("entryCount").GetInt32() >= 4);
        Assert.Equal(EventCatalog.CatalogHash, m.GetProperty("catalogHash").GetString());
        Assert.False(m.TryGetProperty("sessionId", out _));                 // 默认不写原始 SessionId
        Assert.Equal("S1", m.GetProperty("sessionAlias").GetString());
        Assert.Equal("not-provided", m.GetProperty("cutoff").GetProperty("flushStatus").GetString());

        // checksums 必须逐条可核对（长度 + sha256 + 相对路径），且写明"不是签名"
        var checksums = ReadEntry(zip, "checksums.sha256");
        Assert.Contains("不是签名", checksums[0], StringComparison.Ordinal);
        Assert.True(checksums.Length >= 4);
        foreach (var line in checksums.Skip(1))
        {
            var parts = line.Split("  ", StringSplitOptions.None);
            Assert.Equal(3, parts.Length);
            Assert.Equal(64, parts[0].Length);
            Assert.True(long.TryParse(parts[1], out var length) && length >= 0);
        }
    }

    [Fact]
    public void ChecksumsMatchTheActualEntryContents()
    {
        var runtime = CreateSessionWithData(out var sessionDir);
        try
        {
            var outcome = new DiagnosticPackageExporter().Export(new DiagnosticExportRequest
            {
                SessionDir = sessionDir,
                OutputDirectory = Path.Combine(_root, "out2"),
            });
            Assert.True(outcome.Succeeded, outcome.FailureReason);

            using var zip = ZipFile.OpenRead(outcome.ZipPath!);
            var declared = ReadEntry(zip, "checksums.sha256").Skip(1)
                .Select(line => line.Split("  ", StringSplitOptions.None)[0])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in zip.Entries)
            {
                if (entry.FullName == "checksums.sha256") continue;   // 自包含校验不包含自己
                using var stream = entry.Open();
                using var sha = System.Security.Cryptography.SHA256.Create();
                var hash = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
                Assert.Contains(hash, declared);
            }
        }
        finally
        {
            D2TestSupport.Shutdown(runtime);
            D2TestSupport.Dispose(runtime);
        }
    }

    // ────────────────────────── ② 隐私：包内不得出现任何明文 ──────────────────────────

    [Fact]
    public void PackageContainsNoPlaintextSecretsHostsOrUsernames()
    {
        var runtime = CreateSessionWithData(out var sessionDir);
        try
        {
            var outcome = new DiagnosticPackageExporter().Export(new DiagnosticExportRequest
            {
                SessionDir = sessionDir,
                OutputDirectory = Path.Combine(_root, "out3"),
                Health = runtime.GetHealthSnapshot(),
                Incidents = runtime.ActiveIncidents.ToArray(),
            });
            Assert.True(outcome.Succeeded, outcome.FailureReason);

            using var zip = ZipFile.OpenRead(outcome.ZipPath!);
            foreach (var entry in zip.Entries)
            {
                using var stream = entry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var text = reader.ReadToEnd();

                Assert.DoesNotContain(CanarySecret, text, StringComparison.Ordinal);
                Assert.DoesNotContain(CanaryHost, text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(CanaryUser, text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(@"\\", text, StringComparison.Ordinal);          // 不得出现 UNC 形态
                Assert.DoesNotContain("secret.xlsx", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(_runtimeRoot, text, StringComparison.OrdinalIgnoreCase);  // 本机绝对路径也不得出现
            }
        }
        finally
        {
            D2TestSupport.Shutdown(runtime);
            D2TestSupport.Dispose(runtime);
        }
    }

    [Fact]
    public void SessionStorageRootIsTokenizedWithTheExportKey()
    {
        var runtime = CreateSessionWithData(out var sessionDir);
        try
        {
            var exporter = new DiagnosticPackageExporter();
            var outcome = exporter.Export(new DiagnosticExportRequest
            {
                SessionDir = sessionDir,
                OutputDirectory = Path.Combine(_root, "out4"),
            });
            Assert.True(outcome.Succeeded, outcome.FailureReason);

            using var zip = ZipFile.OpenRead(outcome.ZipPath!);
            var sessionJson = ReadEntryText(zip, zip.Entries.Single(e => e.FullName.EndsWith("/session.json", StringComparison.Ordinal)).FullName);

            using var sessionDoc = JsonDocument.Parse(sessionJson);
            var sessionRoot = sessionDoc.RootElement;

            // 结构化断言（不再靠字符串包含 + 手工拼串的格式）。
            Assert.True(sessionRoot.TryGetProperty("storageRootToken", out var token));
            Assert.False(sessionRoot.TryGetProperty("storageRoot", out _));      // 原始路径绝不入包
            Assert.False(string.IsNullOrWhiteSpace(token.GetString()));
            Assert.Equal(exporter.LastExportKeyId, sessionRoot.GetProperty("redactionKeyId").GetString());
            Assert.Equal("export", sessionRoot.GetProperty("exportScope").GetString());
            Assert.Equal("S1", sessionRoot.GetProperty("sessionAlias").GetString());
            // 数字字段不能再被丢掉（旧实现只读字符串 ⇒ processId/eventCatalogCount 整段消失）。
            Assert.True(sessionRoot.TryGetProperty("processId", out _));
            Assert.True(sessionRoot.TryGetProperty("eventCatalogCount", out _));
            // 原始 SessionId 不得出现（默认跨包不可关联）。
            Assert.DoesNotContain(SessionIdOf(sessionJson, sessionDir), sessionJson, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            D2TestSupport.Shutdown(runtime);
            D2TestSupport.Dispose(runtime);
        }
    }

    /// <summary>★ 跨包不可关联 ★：同一个会话导出两次，路径令牌必须不同（每包新 key）。</summary>
    [Fact]
    public void PathTokensAreRemappedPerPackageSoPackagesCannotBeLinked()
    {
        var (sessionDir, _, _, _, _) = CreateSealedSession();

        var request = new DiagnosticExportRequest
        {
            SessionDir = sessionDir,
            OutputDirectory = Path.Combine(_root, "out5"),
        };

        var first = new DiagnosticPackageExporter();
        var second = new DiagnosticPackageExporter();
        var a = first.Export(request);
        var b = second.Export(request);
        Assert.True(a.Succeeded && b.Succeeded);

        Assert.NotEqual(first.ExportKeyId, second.ExportKeyId);

        string FirstPathTokenOf(string zipPath)
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var eventsEntry = zip.Entries.First(e => e.FullName.Contains("/events/events-", StringComparison.Ordinal));
            using var reader = new StreamReader(eventsEntry.Open(), Encoding.UTF8);
            var line = reader.ReadToEnd().Split('\n').First(l => l.Contains("\"path\"", StringComparison.Ordinal));
            var marker = "\"pathToken\":\"";
            var start = line.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            return line.Substring(start, line.IndexOf('"', start) - start);
        }

        var tokenA = FirstPathTokenOf(a.ZipPath!);
        var tokenB = FirstPathTokenOf(b.ZipPath!);
        Assert.NotEqual(tokenA, tokenB);          // 跨包不可关联
        Assert.DoesNotContain(CanaryHost, tokenA, StringComparison.OrdinalIgnoreCase);
    }

    // ────────────────────────── ③ allowlist：业务存档绝不入包 ──────────────────────────

    [Fact]
    public void BusinessArchivesAreNeverIncluded()
    {
        var runtime = CreateSessionWithData(out var sessionDir);
        try
        {
            // 在会话目录里放一份"业务存档"（真实布局是 Job 目录，但即便被误放在这里也不得入包）。
            File.WriteAllText(Path.Combine(sessionDir, "job.json"), "{\"jobId\":\"JOB-D6\"}");
            File.WriteAllText(Path.Combine(sessionDir, "plan.json"), "{\"objects\":[]}");
            File.WriteAllText(Path.Combine(sessionDir, "verify-report.json"), "{\"overallPass\":true}");
            File.WriteAllText(Path.Combine(sessionDir, "pause.request"), "Cooperative");

            var outcome = new DiagnosticPackageExporter().Export(new DiagnosticExportRequest
            {
                SessionDir = sessionDir,
                OutputDirectory = Path.Combine(_root, "out6"),
            });
            Assert.True(outcome.Succeeded, outcome.FailureReason);

            using var zip = ZipFile.OpenRead(outcome.ZipPath!);
            var names = zip.Entries.Select(e => e.FullName).ToArray();
            Assert.DoesNotContain(names, n => n.Contains("job.json", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, n => n.Contains("plan.json", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, n => n.Contains("verify-report", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, n => n.Contains("pause.request", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            D2TestSupport.Shutdown(runtime);
            D2TestSupport.Dispose(runtime);
        }
    }

    /// <summary>★ 绝不上传 ★：导出实现里不允许出现任何网络类型。</summary>
    [Fact]
    public void ExportImplementationContainsNoNetworkCode()
    {
        var root = FindRepoRoot();
        var text = File.ReadAllText(Path.Combine(root, "src", "PCMig.Diagnostics", "Export", "DiagnosticPackageExporter.cs"));

        foreach (var forbidden in new[]
                 {
                     "HttpClient", "WebRequest", "WebClient", "Socket", "SmtpClient",
                     "OpenRead(", "DownloadFile", "UploadFile", "UploadData", "ftp://", "https://",
                 })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }

        // 并且必须显式声明"本地文件"
        Assert.Contains("local-file-only", text, StringComparison.Ordinal);
    }

    // ────────────────────────── ④ cutoff：只导已封段 ──────────────────────────

    [Fact]
    public void UnsealedActiveSegmentIsSkippedAndReported()
    {
        var runtime = CreateSessionWithData(out var sessionDir);
        try
        {
            // 会话仍在运行 ⇒ events 段还没有 manifest（未封段）。
            var eventsDir = Path.Combine(sessionDir, "events");
            var active = Directory.GetFiles(eventsDir, "events-*.jsonl");
            Assert.NotEmpty(active);
            Assert.All(active, f => Assert.False(File.Exists(f + ".manifest.json"), "前提：此时段尚未封"));

            var outcome = new DiagnosticPackageExporter().Export(new DiagnosticExportRequest
            {
                SessionDir = sessionDir,
                OutputDirectory = Path.Combine(_root, "out7"),
            });
            Assert.True(outcome.Succeeded, outcome.FailureReason);

            using var zip = ZipFile.OpenRead(outcome.ZipPath!);
            var names = zip.Entries.Select(e => e.FullName).ToArray();
            Assert.DoesNotContain(names, n => n.Contains("/events/events-", StringComparison.Ordinal));

            var manifest = ReadEntryText(zip, "manifest.json");
            Assert.Contains("active-segment-skipped", manifest, StringComparison.Ordinal);   // 如实标注，不假装完整
        }
        finally
        {
            D2TestSupport.Shutdown(runtime);
            D2TestSupport.Dispose(runtime);
        }
    }

    [Fact]
    public void SealedSegmentsAreIncludedAfterShutdown()
    {
        var runtime = CreateSessionWithData(out var sessionDir);
        D2TestSupport.Shutdown(runtime);
        D2TestSupport.Dispose(runtime);

        // 关闭后段已封（有 manifest）⇒ 应当入包。
        Assert.Contains(Directory.GetFiles(Path.Combine(sessionDir, "events"), "events-*.jsonl"),
            f => File.Exists(f + ".manifest.json"));

        var outcome = new DiagnosticPackageExporter().Export(new DiagnosticExportRequest
        {
            SessionDir = sessionDir,
            OutputDirectory = Path.Combine(_root, "out8"),
        });
        Assert.True(outcome.Succeeded, outcome.FailureReason);

        using var zip = ZipFile.OpenRead(outcome.ZipPath!);
        Assert.Contains(zip.Entries, e => e.FullName.Contains("/events/events-", StringComparison.Ordinal));
    }

    // ────────────────────────── ⑤ 不覆盖 / 取消 / 失败 ──────────────────────────

    [Fact]
    public void SecondExportDoesNotOverwriteTheFirst()
    {
        var runtime = CreateSessionWithData(out var sessionDir);
        try
        {
            var request = new DiagnosticExportRequest
            {
                SessionDir = sessionDir,
                OutputDirectory = Path.Combine(_root, "out9"),
            };

            var first = new DiagnosticPackageExporter().Export(request);
            var second = new DiagnosticPackageExporter().Export(request);

            Assert.True(first.Succeeded && second.Succeeded);
            Assert.NotEqual(first.ZipPath, second.ZipPath);
            Assert.True(File.Exists(first.ZipPath!));
            Assert.True(File.Exists(second.ZipPath!));
        }
        finally
        {
            D2TestSupport.Shutdown(runtime);
            D2TestSupport.Dispose(runtime);
        }
    }

    [Fact]
    public void CancelLeavesNoPartialFile()
    {
        var runtime = CreateSessionWithData(out var sessionDir);
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();   // 一进来就取消

            var outputDir = Path.Combine(_root, "out10");
            var outcome = new DiagnosticPackageExporter().Export(new DiagnosticExportRequest
            {
                SessionDir = sessionDir,
                OutputDirectory = outputDir,
            }, cts.Token);

            Assert.False(outcome.Succeeded);
            Assert.Equal("canceled", outcome.FailureReason);
            if (Directory.Exists(outputDir))
                Assert.Empty(Directory.GetFiles(outputDir, "*.partial"));
            Assert.Empty(Directory.Exists(outputDir) ? Directory.GetFiles(outputDir, "*.zip") : Array.Empty<string>());
        }
        finally
        {
            D2TestSupport.Shutdown(runtime);
            D2TestSupport.Dispose(runtime);
        }
    }

    [Fact]
    public void MissingSessionDirectoryFailsHonestly()
    {
        var outcome = new DiagnosticPackageExporter().Export(new DiagnosticExportRequest
        {
            SessionDir = Path.Combine(_root, "does-not-exist"),
            OutputDirectory = Path.Combine(_root, "out11"),
        });

        Assert.False(outcome.Succeeded);
        Assert.Equal("session-dir-missing", outcome.FailureReason);
    }

    // ────────────────────────── ⑥ summary 可独立阅读 ──────────────────────────

    [Fact]
    public void SummaryIsStandaloneReadableWithFactsCandidatesAndNextChecks()
    {
        var runtime = CreateSessionWithData(out var sessionDir);
        try
        {
            var outcome = new DiagnosticPackageExporter().Export(new DiagnosticExportRequest
            {
                SessionDir = sessionDir,
                OutputDirectory = Path.Combine(_root, "out12"),
                Health = runtime.GetHealthSnapshot(),
                Incidents = runtime.ActiveIncidents.ToArray(),
            });
            Assert.True(outcome.Succeeded, outcome.FailureReason);

            using var zip = ZipFile.OpenRead(outcome.ZipPath!);
            var summary = ReadEntryText(zip, "summary.json");

            using var summaryDoc = JsonDocument.Parse(summary);
            Assert.Equal("collectionHealth", summaryDoc.RootElement.GetProperty("collectionHealth").ValueKind == JsonValueKind.Object
                ? "collectionHealth" : "missing");
            Assert.True(summaryDoc.RootElement.TryGetProperty("evidenceNote", out _));
            // ★ D6.1 §2.2 ★ 有健康快照且无丢失时才允许 Complete。
            var completeness = summaryDoc.RootElement.GetProperty("evidenceCompleteness").GetString();
            Assert.Contains(completeness, new[] { "Complete", "Partial", "Degraded", "Unknown" });

            var incidents = summaryDoc.RootElement.GetProperty("incidents");
            Assert.Equal(JsonValueKind.Array, incidents.ValueKind);
            Assert.True(incidents.GetArrayLength() > 0);
            var first = incidents[0];
            Assert.Equal("PERSISTENCE_WRITE_FAILED", first.GetProperty("symptomCode").GetString());
            Assert.Equal("Error", first.GetProperty("severity").GetString());
            Assert.Equal(JsonValueKind.Array, first.GetProperty("facts").ValueKind);
            Assert.Equal(JsonValueKind.Array, first.GetProperty("candidates").ValueKind);
            Assert.Equal(JsonValueKind.Array, first.GetProperty("missingEvidence").ValueKind);
            Assert.Equal(JsonValueKind.Array, first.GetProperty("nextChecks").ValueKind);
            Assert.Equal("local-file-only", summaryDoc.RootElement.GetProperty("delivery").GetString());

            // 摘要里的 textual 字段也不得带明文
            Assert.DoesNotContain(CanarySecret, summary, StringComparison.Ordinal);
            Assert.DoesNotContain(CanaryHost, summary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            D2TestSupport.Shutdown(runtime);
            D2TestSupport.Dispose(runtime);
        }
    }

    /// <summary>
    /// 导出钩子接线：诊断中心 VM 的 `ExportAsync` 必须能把结果如实回显
    /// （D6 只提供实现，回显口径由 D5 的用例守住）。
    /// </summary>
    [Fact]
    public void ExporterPluggedThroughViewModelReportsLocalOnlyResult()
    {
        var runtime = CreateSessionWithData(out var sessionDir);
        try
        {
            var exporter = new DiagnosticPackageExporter();
            // 用测试替身代替 WinUI 适配器：本用例验证的是"VM + 导出服务"的协作，不验证适配器本身。
            var vm = new PCMig.WinUI.Presentation.DiagnosticCenterViewModel(new StubCenterSource())
            {
                ExportHandler = _ =>
                {
                    var outcome = exporter.Export(new DiagnosticExportRequest
                    {
                        SessionDir = sessionDir,
                        OutputDirectory = Path.Combine(_root, "out13"),
                    });
                    return System.Threading.Tasks.Task.FromResult(
                        new PCMig.WinUI.Presentation.DiagnosticExportResult(
                            outcome.Succeeded, outcome.ZipPath, outcome.FailureReason, outcome.Bytes));
                },
            };

            var result = D2TestSupport.Run(vm.ExportAsync());
            Assert.True(result.Succeeded, result.FailureReason);
            Assert.True(File.Exists(result.Path!));
            Assert.Contains("不会上传", vm.StatusText!, StringComparison.Ordinal);
        }
        finally
        {
            D2TestSupport.Shutdown(runtime);
            D2TestSupport.Dispose(runtime);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
        }
        throw new InvalidOperationException("找不到仓库根：" + AppContext.BaseDirectory);
    }

    /// <summary>最小只读数据源替身（WinUI 适配器不链入本测试项目）。</summary>
    private sealed class StubCenterSource : PCMig.WinUI.Presentation.IDiagnosticCenterSource
    {
        public bool IsAvailable => true;
        public Guid SessionId => TestEvents.FixedSessionId;
        public string ModeName => "Operational";
        public string? StorageRoot => null;
        public DiagnosticHealthSnapshot GetHealth() => default;   // record struct ⇒ 全零即"无数据"的中性快照
        public IReadOnlyList<Incident> GetIncidents() => Array.Empty<Incident>();
        public DiagnosticEvent[] GetRecentEvents(int max) => Array.Empty<DiagnosticEvent>();
        public FlightStats? GetFlight() => null;
        public RuleEngineStats? GetRules() => null;
        public ExpectationTrackerStats? GetExpectations() => null;
        public void RequestMode(CaptureMode mode, string reasonCode) { }
    }
}