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
/// ★ D6.3 §13（红灯 Fixture 4）+ §3 导出闸门聚合 + §6.1 「Complete / Included 必须由实际包内容得出」★
///
/// 这一组测试直接反转独立审计里"包会说假话"的三条：
///
///   a) **导出跳过损坏行、cutoff 却报 acknowledged/anyTailLoss=false** ⇒ 旧实现照样写 Complete；
///      正确行为：包内证据有洞 ⇒ `evidenceCompleteness = Partial`，且 `evidenceBlockers`
///      必须逐条说明被谁挡住（`unparsable-lines-skipped:...`）。
///   b) **请求了飞行窗口、实际一条都没进包** ⇒ 旧实现 `includedFlightWindows = true`（抄配置）；
///      正确行为：按最终 ZIP 里真实存在的条目算，写 `false` + `flightEntryCount: 0` + 告警。
///   c) **不给截止点** ⇒ 旧实现只看 health 乐观状态就写 Complete；
///      正确行为：不知道覆盖到哪里 ⇒ 不许 Complete。
///
/// 同时保留**正对照**：干净会话 + 有截止点 + 没请求飞行窗口 ⇒ 仍必须是 Complete。
/// 没有这条正对照，"变红"可能只是把一切都判成 Partial 的假修复。
/// </summary>
public sealed class D63ExportTruthTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-d63x-" + Guid.NewGuid().ToString("N")[..8]);

    public D63ExportTruthTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* 句柄延迟释放 */ }
    }

    // ───────── Fixture 4：损坏行 + 假完整 ─────────

    [Fact]
    public void Fixture4_CorruptLineSkippedByExportCannotStillClaimComplete()
    {
        var runtime = Start("corrupt", out var sessionDir);
        try
        {
            PublishEvents(runtime, 3);
            var cutoff = runtime.PrepareExportCutoff(TimeSpan.FromSeconds(3));

            // 前提：这条路径在**只看 cutoff**时是"完整"的 —— 正是旧实现会写 Complete 的场景。
            Assert.True(cutoff.IsEvidenceContiguous, $"cutoff 自相矛盾：{cutoff.FlushStatus}");
            Assert.False(cutoff.AnyTailLoss);

            // 现在把一条**解析不了的行**塞进已封段（模拟磁盘上的损坏证据）。
            var segment = LatestSegment(sessionDir, "events");
            Assert.NotNull(segment);
            File.AppendAllText(segment!, "{\"sequence\": 999, \"broken\": \n");

            var zip = Export(runtime, sessionDir, cutoff, includeFlight: false, tag: "corrupt");
            var summary = ReadJson(zip, "summary.json");
            var manifest = ReadJson(zip, "manifest.json");

            // ① 不许 Complete，且必须说清为什么。
            Assert.NotEqual("Complete", summary.GetProperty("evidenceCompleteness").GetString());
            Assert.False(summary.GetProperty("packageEvidenceComplete").GetBoolean());
            var blockers = Blockers(summary);
            Assert.Contains(blockers, b => b.StartsWith("unparsable-lines-skipped:", StringComparison.Ordinal));

            // ② manifest 也必须在 cutoff 块里如实标注（事件段封存 OK ≠ 包内证据完整）。
            var cutoffNode = manifest.GetProperty("cutoff");
            Assert.False(cutoffNode.GetProperty("packageEvidenceComplete").GetBoolean());
            Assert.Equal(0, cutoffNode.GetProperty("serializationFailures").GetInt64());
            Assert.False(cutoffNode.GetProperty("flightEvidenceIncomplete").GetBoolean());

            // ③ 外层 checksums 全对**不能**证明段清单/包内数据一致：它只证明 ZIP 字节自身一致。
            AssertChecksumsMatchPackage(zip);

            // ④ 完整性等级必须与"能不能据包断言没发生"一致：Partial ⇒ 不能断言。
            Assert.Contains("不等于", summary.GetProperty("evidenceNote").GetString()!, StringComparison.Ordinal);
        }
        finally { Stop(runtime); }
    }

    [Fact]
    public void CleanSealedSessionStillReportsComplete()
    {
        var runtime = Start("clean", out var sessionDir);
        try
        {
            PublishEvents(runtime, 3);
            var cutoff = runtime.PrepareExportCutoff(TimeSpan.FromSeconds(3));

            var zip = Export(runtime, sessionDir, cutoff, includeFlight: false, tag: "clean");
            var summary = ReadJson(zip, "summary.json");
            var manifest = ReadJson(zip, "manifest.json");

            Assert.True(summary.GetProperty("packageEvidenceComplete").GetBoolean(),
                "干净会话被误判为不完整，说明「变红」只是把所有情况都判成 Partial：" +
                string.Join(" | ", Blockers(summary)));
            Assert.Equal("Complete", summary.GetProperty("evidenceCompleteness").GetString());
            Assert.Empty(Blockers(summary));

            // ★ 缺口①收口（D6.3 §11）★ 正对照要两边一致：同一个包里 manifest 与 summary 都只能说 true。
            Assert.True(manifest.GetProperty("cutoff").GetProperty("packageEvidenceComplete").GetBoolean(),
                "manifest 与 summary 对同一次干净导出给出了相反结论——同一个包说了两句相反的话。");
        }
        finally { Stop(runtime); }
    }

    // ───────── §6.1：Included 由实际条目决定 ─────────

    [Fact]
    public void RequestedFlightWindowsAbsentFromTheZipMustNotClaimIncluded()
    {
        var runtime = Start("flight", out var sessionDir);
        try
        {
            PublishEvents(runtime, 2);
            var cutoff = runtime.PrepareExportCutoff(TimeSpan.FromSeconds(3));

            var zip = Export(runtime, sessionDir, cutoff, includeFlight: true, tag: "flight");
            var manifest = ReadJson(zip, "manifest.json");
            var summary = ReadJson(zip, "summary.json");

            Assert.Equal(0, manifest.GetProperty("flightEntryCount").GetInt32());
            Assert.False(manifest.GetProperty("includedFlightWindows").GetBoolean());   // ★ 不抄配置
            Assert.True(manifest.GetProperty("requestedFlightWindows").GetBoolean());   // 请求意图另记
            Assert.True(manifest.TryGetProperty("flightWindowsNote", out _));

            Assert.Contains(Blockers(summary), b => b == "flight-windows-requested-but-not-included");
            Assert.NotEqual("Complete", summary.GetProperty("evidenceCompleteness").GetString());
            // ★ 缺口①收口（D6.3 §11；链路 9 实机缺陷）★ 这一条正是实机那条：summary 说 Partial，
            //   manifest 却凭捕捉侧判据说完整 ⇒ 同一个包里 packageEvidenceComplete 一处 false、一处 true。
            Assert.False(manifest.GetProperty("cutoff").GetProperty("packageEvidenceComplete").GetBoolean(),
                "summary 判 Partial，manifest 却宣称本包证据完整——同一个包说了两句相反的话。");
        }
        finally { Stop(runtime); }
    }

    // ───────── 缺口①红灯：同一个包不许对 packageEvidenceComplete 说两句相反的话 ─────────

    /// <summary>
    /// ★ 缺口①红灯（D6.3 §11）★
    /// 实机（链路 9 导出包）：请求了飞行窗口、最终包内 0 条 flight ⇒
    ///   summary.json 写 `evidenceCompleteness: "Partial"` + `packageEvidenceComplete: false`，
    ///   而 manifest.json 的 cutoff 块凭**捕捉侧**判据写 `packageEvidenceComplete: true`。
    ///   复核者拿同一个 ZIP 会得到两个互斥结论。
    ///
    /// 修复后 manifest 与 summary 必须**同源同值**（都取自 `EvaluatePackageEvidence`）。
    /// 四种"说真话的场景"逐一覆盖，避免只修实机那一条路径：
    ///   ① 干净 + 未请求飞行窗口 ⇒ 两边都 true（正对照：不许一刀切全判不完整）；
    ///   ② 干净 + 请求飞行窗口但没进包 ⇒ 两边都 false；
    ///   ③ 导出跳过损坏行 ⇒ 两边都 false；
    ///   ④ 没有截止点 ⇒ 两边都 false。
    /// </summary>
    [Fact]
    public void ManifestAndSummaryMustNeverDisagreeAboutPackageEvidenceComplete()
    {
        AssertSameVerdict("agree-clean", includeFlight: false, corruptLine: false, provideCutoff: true, expectComplete: true);
        AssertSameVerdict("agree-flight", includeFlight: true, corruptLine: false, provideCutoff: true, expectComplete: false);
        AssertSameVerdict("agree-corrupt", includeFlight: false, corruptLine: true, provideCutoff: true, expectComplete: false);
        AssertSameVerdict("agree-nocutoff", includeFlight: false, corruptLine: false, provideCutoff: false, expectComplete: false);
    }

    private void AssertSameVerdict(string tag, bool includeFlight, bool corruptLine, bool provideCutoff, bool expectComplete)
    {
        var runtime = Start(tag, out var sessionDir);
        try
        {
            PublishEvents(runtime, 3);
            DiagnosticExportCutoff? cutoff = provideCutoff ? runtime.PrepareExportCutoff(TimeSpan.FromSeconds(3)) : null;
            if (corruptLine)
            {
                var segment = LatestSegment(sessionDir, "events");
                Assert.NotNull(segment);
                File.AppendAllText(segment!, "{\"sequence\": 999, \"broken\": \n");
            }

            var zip = Export(runtime, sessionDir, cutoff, includeFlight: includeFlight, tag: tag);
            var summary = ReadJson(zip, "summary.json");
            var manifest = ReadJson(zip, "manifest.json");

            var summaryComplete = summary.GetProperty("packageEvidenceComplete").GetBoolean();
            var completeness = summary.GetProperty("evidenceCompleteness").GetString();
            var cutoffNode = manifest.GetProperty("cutoff");
            var manifestComplete = cutoffNode.GetProperty("packageEvidenceComplete").GetBoolean();

            Assert.True(summaryComplete == expectComplete,
                $"{tag}: summary 的 packageEvidenceComplete 应为 {expectComplete}，实为 {summaryComplete}（completeness={completeness}）");
            Assert.True(summaryComplete == manifestComplete,
                $"{tag}: 同一个包里 manifest 与 summary 对 packageEvidenceComplete 说法相反 —— summary={summaryComplete}, manifest={manifestComplete}");
            Assert.True((completeness == "Complete") == expectComplete,
                $"{tag}: evidenceCompleteness={completeness} 与 packageEvidenceComplete={summaryComplete} 不同步");

            // 逐条理由也必须两处都看得到：只说 false、不说被谁挡住，复核者无从下手。
            var summaryBlockers = Blockers(summary);
            var manifestBlockers = cutoffNode.TryGetProperty("evidenceBlockers", out var mb) ? mb.GetString() ?? string.Empty : string.Empty;
            foreach (var blocker in summaryBlockers)
                Assert.Contains(blocker, manifestBlockers, StringComparison.Ordinal);
            if (!expectComplete) Assert.NotEmpty(summaryBlockers);
        }
        finally { Stop(runtime); }
    }

    [Fact]
    public void NotRequestingFlightWindowsLeavesNoFlightBlocker()
    {
        var runtime = Start("noflight", out var sessionDir);
        try
        {
            PublishEvents(runtime, 2);
            var cutoff = runtime.PrepareExportCutoff(TimeSpan.FromSeconds(3));

            var zip = Export(runtime, sessionDir, cutoff, includeFlight: false, tag: "noflight");
            var manifest = ReadJson(zip, "manifest.json");
            var summary = ReadJson(zip, "summary.json");

            Assert.False(manifest.GetProperty("requestedFlightWindows").GetBoolean());
            Assert.False(manifest.TryGetProperty("flightWindowsNote", out _));
            Assert.DoesNotContain(Blockers(summary), b => b.StartsWith("flight-windows", StringComparison.Ordinal));
        }
        finally { Stop(runtime); }
    }

    // ───────── 没有截止点 ⇒ 不知道覆盖到哪里 ⇒ 不许 Complete ─────────

    [Fact]
    public void MissingCutoffMeansThePackageCannotClaimToKnowItsCoverage()
    {
        var runtime = Start("nocutoff", out var sessionDir);
        try
        {
            PublishEvents(runtime, 2);
            D2TestSupport.WaitUntil(() => runtime.Health.EventsWritten >= 2);

            // 健康快照此时是"乐观"的（无丢失、无降级）—— 正是旧实现只看它就说 Complete 的入口。
            var health = runtime.GetHealthSnapshot();
            Assert.True(health.EvidenceComplete);
            Assert.False(health.IsDegraded);

            var zip = Export(runtime, sessionDir, cutoff: null, includeFlight: false, tag: "nocutoff");
            var summary = ReadJson(zip, "summary.json");
            var manifest = ReadJson(zip, "manifest.json");

            Assert.Contains(Blockers(summary), b => b == "cutoff-not-provided");
            Assert.NotEqual("Complete", summary.GetProperty("evidenceCompleteness").GetString());
            Assert.False(summary.GetProperty("packageEvidenceComplete").GetBoolean());
            Assert.Equal("not-provided", manifest.GetProperty("cutoff").GetProperty("flushStatus").GetString());
            Assert.True(manifest.GetProperty("cutoff").GetProperty("anyTailLoss").GetBoolean());
        }
        finally { Stop(runtime); }
    }

    // ───────── §6.2 段清单必须描述**包内**那一段 ─────────

    [Fact]
    public void SegmentManifestDescribesThePackagedContentNotTheSourceFile()
    {
        var runtime = Start("segman", out var sessionDir);
        try
        {
            PublishEvents(runtime, 3);
            var cutoff = runtime.PrepareExportCutoff(TimeSpan.FromSeconds(3));

            var sourceSegment = LatestSegment(sessionDir, "events");
            Assert.NotNull(sourceSegment);
            var sourceManifest = ReadJson(File.ReadAllText(sourceSegment! + ".manifest.json"));
            var sourceSha = sourceManifest.GetProperty("sha256").GetString()!;
            var sourceLength = sourceManifest.GetProperty("length").GetInt64();

            using var zip = Export(runtime, sessionDir, cutoff, includeFlight: false, tag: "segman");
            var entryPath = zip.Entries
                .Select(e => e.FullName)
                .Single(n => n.EndsWith("/events/" + Path.GetFileName(sourceSegment) + ".manifest.json", StringComparison.Ordinal));
            var packagedManifestPath = entryPath[..^".manifest.json".Length];
            var packagedJsonl = zip.GetEntry(packagedManifestPath);
            Assert.NotNull(packagedJsonl);
            using (var stream = packagedJsonl!.Open())
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                var packagedBytes = ms.ToArray();
                var packagedManifest = ReadJson(ReadEntry(zip, entryPath));

                // ★ 清单描述的是包内字节：长度与哈希都必须与包内那份 jsonl 完全吻合。
                Assert.Equal(packagedBytes.Length, packagedManifest.GetProperty("length").GetInt64());
                Assert.Equal(
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(packagedBytes)).ToLowerInvariant(),
                    packagedManifest.GetProperty("sha256").GetString());
                Assert.Equal(
                    Encoding.UTF8.GetString(packagedBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length,
                    packagedManifest.GetProperty("eventCount").GetInt64());
            }

            var packaged = ReadJson(ReadEntry(zip, entryPath));
            // ★ 进包的是脱敏 + 别名重映射后的内容 ⇒ 原清单的指纹本来就描述不了它（旧实现照抄原清单）。
            Assert.NotEqual(sourceSha, packaged.GetProperty("sha256").GetString());
            Assert.True(packaged.GetProperty("length").GetInt64() != sourceLength || sourceLength == 0);
            Assert.Equal("redacted-and-remapped", packaged.GetProperty("contentRef").GetString());
            Assert.Equal(sourceLength, packaged.GetProperty("sourceLength").GetInt64());
            // ★ 隐私默认：不保留可与源目录/其它包稳定关联的原段哈希。
            Assert.Equal(JsonValueKind.Null, packaged.GetProperty("sourceSha256").ValueKind);
        }
        finally { Stop(runtime); }
    }

    [Fact]
    public void SourceSegmentFingerprintIsKeptOnlyWhenCrossPackageCorrelationIsRequested()
    {
        var runtime = Start("segman-keep", out var sessionDir);
        try
        {
            PublishEvents(runtime, 2);
            var cutoff = runtime.PrepareExportCutoff(TimeSpan.FromSeconds(3));

            var sourceSegment = LatestSegment(sessionDir, "events");
            Assert.NotNull(sourceSegment);
            var sourceSha = ReadJson(File.ReadAllText(sourceSegment! + ".manifest.json")).GetProperty("sha256").GetString()!;

            using var zip = Export(runtime, sessionDir, cutoff, includeFlight: false, tag: "segman-keep", preserveCrossPackage: true);
            var entryPath = zip.Entries
                .Select(e => e.FullName)
                .Single(n => n.EndsWith("/events/" + Path.GetFileName(sourceSegment) + ".manifest.json", StringComparison.Ordinal));
            var packaged = ReadJson(ReadEntry(zip, entryPath));

            // 显式要求跨包关联时才保留源段指纹 —— 且只作为"源"字段，不与包内哈希混淆。
            Assert.Equal(sourceSha, packaged.GetProperty("sourceSha256").GetString());
            Assert.NotEqual(sourceSha, packaged.GetProperty("sha256").GetString());
        }
        finally { Stop(runtime); }
    }

    // ───────── R-4（D6.3 剩余风险关闭轮）：同一个 manifest 里不许有同名键 ─────────

    /// <summary>
    /// ★ R-4 收口 ★ `manifest.json` 曾把 `includedHighSensitivityAttachments` 写两次
    /// （一处写**请求意图**、一处写**真实 false**）⇒ 请求为 true 时同一个键出现两个相反值，
    /// 取值完全取决于解析器策略（JsonDocument 保最后一个、部分消费者保第一个）。
    /// 导出 JSON 结构必须唯一，且"包含声明"只能由真实情况得出，请求意图另字段如实记录。
    /// </summary>
    [Fact]
    public void R4_ManifestNeverWritesTheSameKeyTwice()
    {
        var runtime = Start("r4-dupkey", out var sessionDir);
        try
        {
            PublishEvents(runtime, 2);
            var cutoff = runtime.PrepareExportCutoff(TimeSpan.FromSeconds(3));

            // 危险路径：**请求**包含高敏附件（旧实现会在同一份 manifest 里出现 true 与 false 两个值）。
            using var zip = Export(runtime, sessionDir, cutoff, includeFlight: false, tag: "r4-dupkey", includeAttachments: true);

            var manifestText = ReadEntry(zip, "manifest.json");
            var summaryText = ReadEntry(zip, "summary.json");

            // ① 根级不许有同名键（这是缺陷的直接形状）。
            Assert.Empty(DuplicateRootKeys(manifestText));
            Assert.Empty(DuplicateRootKeys(summaryText));

            // ② 包含声明只能写真实值 false；请求意图写另一个字段，二者不得混淆。
            var manifest = ReadJson(manifestText);
            Assert.False(manifest.GetProperty("includedHighSensitivityAttachments").GetBoolean());
            Assert.True(manifest.GetProperty("requestedHighSensitivityAttachments").GetBoolean());
            Assert.Equal(1, CountOccurrences(manifestText, "\"includedHighSensitivityAttachments\""));
        }
        finally { Stop(runtime); }
    }

    // ───────── R-1（D6.3 剩余风险关闭轮）：post 丢包必须一路走到导出 blocker ─────────

    /// <summary>
    /// ★ R-1 收口 ★ 审计发现：飞行窗口的 **post 丢包**既没进丢失台账，也没有任何路径给
    /// `FlightEvidenceIncomplete` 赋值 ⇒ blocker `flight-evidence-incomplete` 生产不可达，
    /// 于是包可以一边写 Complete、包内窗口清单一边写 `partial: true`。
    /// 本夹具走完整链路：post 超容量丢包 → LossLedger → 窗口 partial → cutoff 事实 → 导出 blocker。
    /// </summary>
    [Fact]
    public void R1_PostWindowLossMustReachTheLedgerAndBlockCompleteEvidence()
    {
        var runtime = Start("r1-postloss", out var sessionDir, options =>
        {
            options.RingEventCapacity = 2;                  // post 容量 = 环长度 = 2
            options.RingByteBudget = 64 * 1024;
            options.FlightPostWindowMs = 400;               // 窗口先开着，才可能把 post 发超容量
            options.FlightTriggerCooldownMs = 1;
            options.FlightWindowPayloadByteBudget = 8L * 1024 * 1024;
        });
        try
        {
            PublishEvents(runtime, 2);
            Assert.True(runtime.TriggerFlight("rule:D63_R1", "d63").Accepted);

            // 触发后连发 20 条：post 容量只有 2 ⇒ 后面的**必然**被丢（这正是 R-1 要说的事）。
            var writtenBefore = runtime.Health.EventsWritten;
            for (var i = 0; i < 20; i++)
            {
                runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                    PersistenceEvents.WriteFailed,
                    DiagnosticContext.Root(runtime.SessionId, "Test").WithJob("JOB-D63").WithObject($"post-{i}"),
                    new PstWritePayload("Receipt", "Move", true, "IOException"),
                    Outcome: DiagnosticOutcome.Failed,
                    ExceptionType: "IOException",
                    Message: "R-1 post 丢包样例"));
            }

            Assert.True(D2TestSupport.WaitUntil(() => runtime.Health.EventsWritten >= writtenBefore + 20));
            Assert.True(D2TestSupport.WaitUntil(() => runtime.FlightStatistics() is { SealedWindows: >= 1 }),
                "窗口应在 FlightPostWindowMs 到期后被维护拍封存");

            // ① 丢掉的 post 事件进了丢失台账，并且真的把健康拉下来了。
            var health = runtime.GetHealthSnapshot();
            Assert.True(health.EventsDropped >= 1, "post 丢包没有进 LossLedger（诊断自己悄悄少记）");
            Assert.True(health.IsDegraded, "证据已经缺损，健康仍是 Healthy");
            Assert.False(health.EvidenceComplete);

            // ② 截止快照里必须带上这个事实（旧实现这一条永远不可达）。
            var cutoff = runtime.PrepareExportCutoff(TimeSpan.FromSeconds(3));
            Assert.True(cutoff.FlightEvidenceIncomplete, "飞行证据缺损没有进 cutoff");
            Assert.False(cutoff.IsPackageEvidenceComplete);

            using var zip = Export(runtime, sessionDir, cutoff, includeFlight: true, tag: "r1-postloss");

            // ③ 包内该窗口清单必须说 partial。
            var windowManifest = zip.Entries
                .Select(e => e.FullName)
                .First(n => n.Contains("/flight/", StringComparison.Ordinal) && n.EndsWith(".jsonl.manifest.json", StringComparison.Ordinal));
            Assert.True(ReadJson(ReadEntry(zip, windowManifest)).GetProperty("partial").GetBoolean(),
                "post 丢了事件，包内窗口清单却仍写 partial=false");

            // ④ manifest / summary 同口径：不许说 Complete，并给出同一个 blocker。
            var manifest = ReadJson(zip, "manifest.json");
            Assert.True(manifest.GetProperty("cutoff").GetProperty("flightEvidenceIncomplete").GetBoolean());
            Assert.Contains("flight-evidence-incomplete", ReadEntry(zip, "manifest.json"), StringComparison.Ordinal);

            var summary = ReadJson(zip, "summary.json");
            Assert.False(summary.GetProperty("packageEvidenceComplete").GetBoolean());
            Assert.NotEqual("Complete", summary.GetProperty("evidenceCompleteness").GetString());
            Assert.Contains("flight-evidence-incomplete", Blockers(summary));
        }
        finally { Stop(runtime); }
    }

    // ───────────────────────── helpers ─────────────────────────

    private DiagnosticRuntime Start(string tag, out string sessionDir, Action<DiagnosticRuntimeOptions>? configure = null)
    {
        var root = Path.Combine(_root, tag);
        Directory.CreateDirectory(root);
        var options = D2TestSupport.Options(root);
        configure?.Invoke(options);
        var runtime = DiagnosticRuntime.Start(options, out _);
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
                DiagnosticContext.Root(runtime.SessionId, "Test").WithJob("JOB-D63").WithObject($"obj-{i}"),
                new PstWritePayload("Receipt", "Move", true, "IOException"),
                Outcome: DiagnosticOutcome.Failed,
                ExceptionType: "IOException",
                Message: "D6.3 导出真实性样例"));
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

    private ZipArchive Export(DiagnosticRuntime runtime, string sessionDir, DiagnosticExportCutoff? cutoff, bool includeFlight, string tag, bool preserveCrossPackage = false, bool includeAttachments = false)
    {
        var outcome = new DiagnosticPackageExporter().Export(new DiagnosticExportRequest
        {
            SessionDir = sessionDir,
            OutputDirectory = Path.Combine(_root, tag, "out"),
            Health = runtime.GetHealthSnapshot(),
            Cutoff = cutoff,
            IncludeFlightWindows = includeFlight,
            PreserveCrossPackageCorrelation = preserveCrossPackage,
            IncludeHighSensitivityAttachments = includeAttachments,
        });
        Assert.True(outcome.Succeeded, outcome.FailureReason);
        return ZipFile.OpenRead(outcome.ZipPath!);
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

    private static JsonElement ReadJson(ZipArchive zip, string path)
    {
        var doc = JsonDocument.Parse(ReadEntry(zip, path));
        return doc.RootElement.Clone();
    }

    private static List<string> Blockers(JsonElement summary)
        => summary.GetProperty("evidenceBlockers").EnumerateArray().Select(e => e.GetString()!).ToList();

    /// <summary>根级同名键（深度 1 的 PropertyName 重复）——JSON 结构唯一的可判定部分。</summary>
    private static List<string> DuplicateRootKeys(string json)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new List<string>();
        var reader = new System.Text.Json.Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        var depth = 0;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    depth++;
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    depth--;
                    break;
                case JsonTokenType.PropertyName when depth == 1:
                    var name = reader.GetString()!;
                    if (!seen.Add(name)) duplicates.Add(name);
                    break;
            }
        }

        return duplicates;
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        for (var i = text.IndexOf(token, StringComparison.Ordinal); i >= 0; i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>外层 checksums.sha256 逐条与 ZIP 实际字节核对（证明"只证明字节一致"这一点）。</summary>
    private static void AssertChecksumsMatchPackage(ZipArchive zip)
    {
        var lines = ReadEntry(zip, "checksums.sha256").Split('\n').Where(l => l.Contains("  ", StringComparison.Ordinal)).ToArray();
        Assert.True(lines.Length >= 3);
        foreach (var line in lines)
        {
            var parts = line.Split("  ", StringSplitOptions.None);
            if (parts.Length != 3) continue;
            var entry = zip.GetEntry(parts[2]);
            Assert.NotNull(entry);
            using var stream = entry!.Open();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ms.ToArray())).ToLowerInvariant(), parts[0]);
            Assert.Equal(ms.Length, long.Parse(parts[1]));
        }
    }
}