using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Serialization;
using PCMig.Diagnostics.Analysis;

namespace PCMig.Diagnostics.Export;

/// <summary>导出请求（**范围与脱敏级别由调用方显式给出**，导出服务不自行扩大范围）。</summary>
public sealed record DiagnosticExportRequest
{
    /// <summary>当前会话目录（`&lt;root&gt;\&lt;sessionId&gt;`）。</summary>
    public required string SessionDir { get; init; }

    /// <summary>输出目录（不存在则创建）。</summary>
    public required string OutputDirectory { get; init; }

    public string? AppVersion { get; init; }

    public string? BuildId { get; init; }

    /// <summary>是否包含飞行窗口（可能含更详细的近尾证据）。</summary>
    public bool IncludeFlightWindows { get; init; } = true;

    /// <summary>高敏感附件（nettrace/dump/截图等）：**默认不含**，需要调用方显式同意。</summary>
    public bool IncludeHighSensitivityAttachments { get; init; }

    /// <summary>导出时点的自身健康快照（由 runtime 提供；缺失时导出**不得**声称证据完整）。</summary>
    public DiagnosticHealthSnapshot? Health { get; init; }

    public FlightStats? Flight { get; init; }

    public RuleEngineStats? Rules { get; init; }

    public IReadOnlyList<Incident>? Incidents { get; init; }

    /// <summary>
    /// 导出截止点（由 runtime `PrepareExportCutoff()` 提供）：保证"用户点导出前最后一段时间"的证据
    /// 已封段并进包；缺失时导出按"当前已封段"处理，并在 manifest 如实标注 `not-provided` + `anyTailLoss`。
    /// </summary>
    public DiagnosticExportCutoff? Cutoff { get; init; }

    /// <summary>
    /// ★ D6.1 §2.4 ★ 是否保留**跨包可关联**信息。默认 false ⇒ 每次导出**现场生成新 key**，
    /// 包内只出现**每包别名**（S1），不出现原始 SessionId；只有用户显式选择高级模式才置 true。
    /// </summary>
    public bool PreserveCrossPackageCorrelation { get; init; }
}

/// <summary>导出结果（**绝不假装成功**：失败时给出稳定 reasonCode）。</summary>
public readonly record struct DiagnosticExportOutcome(
    bool Succeeded,
    string? ZipPath,
    string? FailureReason,
    long Bytes,
    int FileCount)
{
    public static DiagnosticExportOutcome Failed(string reason) => new(false, null, reason, 0, 0);
}

/// <summary>
/// 诊断包导出（方案 §22 / D6.1 §2）。
///
/// 硬约束（逐条都在实现里体现）：
///   · **本地文件、绝不上传**：本类不含任何网络调用（有用例扫描源码守住）；
///   · **allowlist**：只导出诊断产物，业务存档（job/plan/receipt/verify）绝不入包；
///   · **所有 JSON 结构化生成**（`DiagnosticPackageJson` + `Utf8JsonWriter`），不再手写拼串；
///   · **二次脱敏**：每包新 key 重映射路径令牌；整条事件的**每个字符串值**再过一遍清洗；
///   · **默认跨包不可关联**：包内用每包别名（S1），不写原始 SessionId；
///   · **cutoff**：导出前由 runtime 封存活动段，包尾覆盖写进 manifest；
///   · **流式**：边写边压缩；**不覆盖**：同名换新名；先写 `.partial` 再原子改名。
/// </summary>
public sealed class DiagnosticPackageExporter
{
    public const string ExporterVersion = "1.1";
    public const string LocalOnlyNotice = "local-file-only";

    private static readonly string[] AllowedSessionFiles = { "session.json", "catalog.json" };
    private static readonly string[] AllowedEventDirs = { "events", "incidents", "metrics", "flight" };

    private readonly RedactionPolicy? _pinnedRedaction;

    /// <param name="exportRedaction">
    /// 显式固定的脱敏策略。**默认 null ⇒ 每次 <see cref="Export"/> 现场新建（每包新 key）**；
    /// 固定策略只在请求开 <see cref="DiagnosticExportRequest.PreserveCrossPackageCorrelation"/> 时生效。
    /// </param>
    public DiagnosticPackageExporter(RedactionPolicy? exportRedaction = null)
        => _pinnedRedaction = exportRedaction;

    /// <summary>当前实例最近一次导出使用的 key（诊断/测试用；未导出过则为 null）。</summary>
    public string? LastExportKeyId { get; private set; }

    /// <summary>兼容旧调用点：固定策略时返回其 KeyId，否则返回最近一次导出的 KeyId。</summary>
    public string? ExportKeyId => _pinnedRedaction?.KeyId ?? LastExportKeyId;

    public DiagnosticExportOutcome Export(DiagnosticExportRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.SessionDir) || !Directory.Exists(request.SessionDir))
            return DiagnosticExportOutcome.Failed("session-dir-missing");
        if (string.IsNullOrWhiteSpace(request.OutputDirectory))
            return DiagnosticExportOutcome.Failed("output-dir-missing");

        // ★ 每包一个新 key（默认路径）★
        var redaction = request.PreserveCrossPackageCorrelation
            ? _pinnedRedaction ?? RedactionPolicy.CreateForSession()
            : RedactionPolicy.CreateForSession();
        LastExportKeyId = redaction.KeyId;
        var exportId = redaction.KeyId;

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string finalPath;
        string partialPath;

        try
        {
            Directory.CreateDirectory(request.OutputDirectory);
            (finalPath, partialPath) = ResolvePaths(request.OutputDirectory, stamp);
        }
        catch (Exception ex)
        {
            return DiagnosticExportOutcome.Failed("prepare-failed:" + ex.GetType().Name);
        }

        var entries = new List<DiagnosticPackageEntry>();
        var warnings = new List<string>();
        var rawSessionId = Path.GetFileName(request.SessionDir.TrimEnd(Path.DirectorySeparatorChar));
        var sessionAlias = request.PreserveCrossPackageCorrelation ? rawSessionId : "S1";
        // 包内事件用**本包别名 SessionId**：包内仍可互相关联，跨包不可（原始 id 一律不出现）。
        var packageSessionId = request.PreserveCrossPackageCorrelation
            ? Guid.TryParse(rawSessionId, out var raw) ? raw : DerivePackageSessionId(exportId, sessionAlias)
            : DerivePackageSessionId(exportId, sessionAlias);

        try
        {
            using (var fileStream = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                // ① events / incidents / metrics / flight（只导**已封段**；导出前 runtime 已把活动段封好）
                foreach (var dirName in AllowedEventDirs)
                {
                    ct.ThrowIfCancellationRequested();
                    if (dirName == "flight" && !request.IncludeFlightWindows) continue;
                    var dir = Path.Combine(request.SessionDir, dirName);
                    if (!Directory.Exists(dir)) continue;

                    foreach (var file in Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.Ordinal))
                    {
                        ct.ThrowIfCancellationRequested();
                        var name = Path.GetFileName(file);

                        if (name.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase))
                        {
                            // 段清单：只含长度/哈希/序号范围/时间范围，无个人信息 ⇒ 原样。
                            AddBytes(zip, $"sessions/{sessionAlias}/{dirName}/{name}", file, entries);
                            continue;
                        }

                        if (!name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) continue;

                        if (!File.Exists(file + ".manifest.json"))
                        {
                            // 仍未封的段：跳过并如实标注（不假装完整）。正常情况下不会发生 ——
                            // 导出前 runtime 已 PrepareExportCutoff() 封存活动段。
                            warnings.Add($"active-segment-skipped:{dirName}/{name}");
                            continue;
                        }

                        if (dirName == "events" || dirName == "flight")
                            // flight 窗口/检查点文件与事件段是**同一种** event JSONL ⇒ 同一套深度脱敏+
                            // 别名 SessionId（旧实现把它们原样拷贝，等于把原始 id 与未清洗 payload 带出包）。
                            AddRedactedEventLines(zip, $"sessions/{sessionAlias}/{dirName}/{name}", file, entries, warnings, redaction, packageSessionId);
                        else if (dirName == "incidents")
                            AddRedactedIncidentLines(zip, $"sessions/{sessionAlias}/incidents/{name}", file, entries, warnings, redaction, rawSessionId, sessionAlias);
                        else
                            AddBytes(zip, $"sessions/{sessionAlias}/{dirName}/{name}", file, entries);
                    }
                }

                // ② session.json（结构化重写 + StorageRoot 令牌化 + sessionId→别名）+ catalog.json（原样）
                foreach (var name in AllowedSessionFiles)
                {
                    var file = Path.Combine(request.SessionDir, name);
                    if (!File.Exists(file)) continue;
                    if (name == "session.json") AddRedactedSessionJson(zip, $"sessions/{sessionAlias}/session.json", file, entries, redaction, sessionAlias);
                    else AddBytes(zip, $"sessions/{sessionAlias}/catalog.json", file, entries);
                }

                // ③ summary.json
                AddText(zip, "summary.json",
                    DiagnosticPackageJson.Summary(request, sessionAlias, warnings, exportId, redaction), entries);

                // ④ metrics 快照（缺失就省略，不编造）
                var metricsLine = DiagnosticPackageJson.MetricsSnapshotLine(request, redaction);
                if (metricsLine is not null)
                    AddText(zip, $"sessions/{sessionAlias}/metrics/export-snapshot.jsonl", metricsLine + "\n", entries);

                // ⑤ manifest.json（含 cutoff 真实性字段）
                AddText(zip, "manifest.json",
                    DiagnosticPackageJson.Manifest(request, sessionAlias, rawSessionId, entries, warnings, exportId, stamp), entries);

                // ⑥ checksums.sha256
                AddText(zip, "checksums.sha256", BuildChecksums(entries), entries);
            }

            ct.ThrowIfCancellationRequested();

            File.Move(partialPath, finalPath);
            var bytes = new FileInfo(finalPath).Length;
            return new DiagnosticExportOutcome(true, finalPath, null, bytes, entries.Count);
        }
        catch (OperationCanceledException)
        {
            CleanupPartial(partialPath);
            return DiagnosticExportOutcome.Failed("canceled");
        }
        catch (Exception ex)
        {
            CleanupPartial(partialPath);
            return DiagnosticExportOutcome.Failed("export-failed:" + ex.GetType().Name);
        }
    }

    // ────────────────────────── 路径：不覆盖 ──────────────────────────

    private static (string Final, string Partial) ResolvePaths(string dir, string stamp)
    {
        var baseName = $"PCMig-Diagnostic-{stamp}";
        for (var attempt = 1; attempt <= 50; attempt++)
        {
            var suffix = attempt == 1 ? string.Empty : "-" + attempt.ToString(CultureInfo.InvariantCulture);
            var final = Path.Combine(dir, baseName + suffix + ".zip");
            if (File.Exists(final)) continue;
            return (final, final + ".partial");
        }
        throw new IOException("同名导出包过多，放弃以避免覆盖");
    }

    private static void CleanupPartial(string partialPath)
    {
        try { if (File.Exists(partialPath)) File.Delete(partialPath); }
        catch (Exception) { /* 清理失败不影响结论（留下 .partial 肉眼可见，不会被误当成品） */ }
    }

    // ────────────────────────── 条目写入（流式）──────────────────────────

    private static void AddBytes(ZipArchive zip, string entryPath, string sourceFile, List<DiagnosticPackageEntry> entries)
    {
        var entry = zip.CreateEntry(entryPath, CompressionLevel.Optimal);
        using var target = entry.Open();
        using var source = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        source.CopyTo(target);      // 流式：不把文件读进内存
        target.Flush();
        entries.Add(Record(entryPath, sourceFile));
    }

    private static void AddText(ZipArchive zip, string entryPath, string text, List<DiagnosticPackageEntry> entries)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var entry = zip.CreateEntry(entryPath, CompressionLevel.Optimal);
        using (var stream = entry.Open()) stream.Write(bytes, 0, bytes.Length);
        entries.Add(new DiagnosticPackageEntry(entryPath, bytes.Length, Sha256Of(bytes)));
    }

    /// <summary>
    /// 事件行：逐行解析 → 用**本包 key** 重映射路径令牌 + 清洗 Message →
    /// 再对整行 JSON 的**每一个字符串值**做深度清洗 → 重新结构化写出。
    /// 解析失败的行**跳过并记警告**（绝不把损坏内容原样塞进包）。
    /// </summary>
    private static void AddRedactedEventLines(
        ZipArchive zip, string entryPath, string sourceFile, List<DiagnosticPackageEntry> entries,
        List<string> warnings, RedactionPolicy redaction, Guid packageSessionId)
    {
        var output = new StringBuilder(64 * 1024);
        var kept = 0;
        var skipped = 0;

        foreach (var line in ReadLines(sourceFile))
        {
            if (line.Length == 0) continue;
            if (!DiagnosticEventJson.TryParse(line, out var evt, out _) || evt is null)
            {
                skipped++;
                continue;
            }

            var remapped = evt with
            {
                SessionId = packageSessionId,          // ★ 原始 SessionId 绝不入包
                Path = evt.Path is null ? null : evt.Path with
                {
                    PathToken = redaction.Token(evt.Path.PathToken),
                    KeyId = redaction.KeyId,
                    Scope = "export",
                    RootAlias = evt.Path.RootAlias,
                },
                Message = redaction.SanitizeText(evt.Message, 256),
            };

            var deepSanitized = DiagnosticPackageJson.SanitizeAllStrings(DiagnosticEventJson.ToJsonLine(remapped), redaction, 256);
            if (deepSanitized is null)
            {
                skipped++;
                continue;
            }

            output.Append(deepSanitized).Append('\n');
            kept++;
        }

        if (skipped > 0) warnings.Add($"unparsable-lines-skipped:{Path.GetFileName(sourceFile)}={skipped}");
        AddText(zip, entryPath, output.ToString(), entries);
        _ = kept;
    }

    /// <summary>
    /// 事件卡行：逐行**按 JSON 结构**清洗（旧实现把整行当文本截断，会把卡片 JSON 弄坏），
    /// 并把证据引用里的**原始 session 短 ID** 换成每包别名（`S1#12`）。
    /// </summary>
    private static void AddRedactedIncidentLines(
        ZipArchive zip, string entryPath, string sourceFile, List<DiagnosticPackageEntry> entries,
        List<string> warnings, RedactionPolicy redaction, string rawSessionId, string sessionAlias)
    {
        var output = new StringBuilder(16 * 1024);
        var skipped = 0;
        var rawShort = rawSessionId.Length >= 8 ? rawSessionId.Substring(0, 8) : rawSessionId;

        foreach (var line in ReadLines(sourceFile))
        {
            if (line.Length == 0) continue;
            var sanitized = DiagnosticPackageJson.SanitizeAllStrings(line, redaction, 512);
            if (sanitized is null)
            {
                skipped++;
                continue;
            }

            // 证据引用形如 "<8位十六进制>#<序号>"：只在这种**形状**上替换，不做盲目全局替换
            // （盲目替换会误伤哈希等十六进制文本）。
            var rewritten = DiagnosticPackageJson.RewriteEvidenceRefs(sanitized, rawShort, sessionAlias);
            output.Append(rewritten).Append('\n');
        }

        if (skipped > 0) warnings.Add($"unparsable-incident-lines-skipped:{Path.GetFileName(sourceFile)}={skipped}");
        AddText(zip, entryPath, output.ToString(), entries);
    }

    /// <summary>由"本包 key + 别名"派生包内事件使用的 SessionId：包内一致、跨包不同。</summary>
    private static Guid DerivePackageSessionId(string keyId, string alias)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(keyId + "|" + alias + "|pcmig-diagnostic-export"));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>session.json：结构化重写（StorageRoot 令牌化、sessionId→每包别名、数字字段不再丢失）。</summary>
    private static void AddRedactedSessionJson(
        ZipArchive zip, string entryPath, string sourceFile, List<DiagnosticPackageEntry> entries,
        RedactionPolicy redaction, string sessionAlias)
    {
        try
        {
            var text = File.ReadAllText(sourceFile, Encoding.UTF8);
            var json = DiagnosticPackageJson.SessionJson(text, redaction, sessionAlias, out _);
            AddText(zip, entryPath, json, entries);
        }
        catch (Exception ex)
        {
            // 读不出来就如实记一条占位（绝不假装已包含）。
            AddText(zip, entryPath, DiagnosticPackageJson.Write(w =>
            {
                w.WriteStartObject();
                w.WriteString("unavailable", ex.GetType().Name);
                w.WriteEndObject();
            }), entries);
        }
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line) yield return line;
    }

    private static string BuildChecksums(IReadOnlyList<DiagnosticPackageEntry> entries)
    {
        var sb = new StringBuilder(entries.Count * 100);
        sb.Append("# 逐文件完整性校验（长度 + SHA256）。**这不是签名，只用于校验包内文件未被截断/篡改。**\n");
        foreach (var entry in entries.OrderBy(e => e.Path, StringComparer.Ordinal))
            sb.Append(entry.Sha256).Append("  ").Append(entry.Length.ToString(CultureInfo.InvariantCulture))
              .Append("  ").Append(entry.Path).Append('\n');
        return sb.ToString();
    }

    private static DiagnosticPackageEntry Record(string entryPath, string sourceFile)
    {
        var info = new FileInfo(sourceFile);
        using var stream = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return new DiagnosticPackageEntry(entryPath, info.Length, Convert.ToHexString(hash).ToLowerInvariant());
    }

    private static string Sha256Of(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}