using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Analysis;

namespace PCMig.Diagnostics.Export;

/// <summary>包内一个条目的完整性信息（路径 + 字节数 + SHA256）。</summary>
internal readonly record struct DiagnosticPackageEntry(string Path, long Length, string Sha256);

/// <summary>
/// 诊断包内**所有 JSON 的结构化构建器**（D6.1 §2.1）。
///
/// 为什么单独一个文件：旧实现用手写字符串拼 JSON，结果 `session.json`（`exportScope` 前缺逗号）
/// 与 `summary.json`（缺失证据时少一个引号）**都不是合法 JSON**，而测试只做 `ReadAllText/Contains`，
/// 于是缺陷长期存在。这里统一用 <see cref="Utf8JsonWriter"/> 生成 ⇒ **合法 JSON 由构造保证**，
/// 并由"逐个 entry 真实 `JsonDocument.Parse`"的测试守住。
///
/// 原则：结构化生成 + 需要时对**字符串值**做脱敏；不再有"手工加引号/加逗号"的代码。
/// </summary>
internal static class DiagnosticPackageJson
{
    internal static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        // 中文诊断摘要保持可读（不转义成 \uXXXX）；仍会正确转义引号/反斜杠，JSON 合法。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonWriterOptions CompactOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>用结构化 writer 生成一个 JSON 文本。</summary>
    internal static string Write(Action<Utf8JsonWriter> body, bool indented = true)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(buffer, indented ? WriterOptions : CompactOptions))
        {
            body(writer);
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// 把一个 JSON 文本里的**每一个字符串值**过一遍脱敏，再结构化重新写出。
    /// 用于"整条事件的深度清洗"：即使某个 payload 字段未来又夹带了 Personal 字符串，
    /// 导出包也不会把它原样带出去。解析失败返回 null（调用方按"跳过并记警告"处理，绝不假装成功）。
    /// </summary>
    internal static string? SanitizeAllStrings(string json, RedactionPolicy redaction, int maxStringLength = 512)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Write(writer => CopySanitized(doc.RootElement, writer, redaction, maxStringLength, 0), indented: false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private const int MaxDepth = 12;

    private static void CopySanitized(JsonElement element, Utf8JsonWriter writer, RedactionPolicy redaction, int maxLen, int depth)
    {
        if (depth > MaxDepth)
        {
            writer.WriteNullValue();
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    CopySanitized(property.Value, writer, redaction, maxLen, depth + 1);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    CopySanitized(item, writer, redaction, maxLen, depth + 1);
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                var value = element.GetString();
                writer.WriteStringValue(redaction.SanitizeText(value, maxLen) ?? string.Empty);
                break;

            case JsonValueKind.Number:
                if (element.TryGetInt64(out var l)) writer.WriteNumberValue(l);
                else writer.WriteNumberValue(element.GetDouble());
                break;

            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            default: writer.WriteNullValue(); break;
        }
    }

    // ────────────────────────── session.json ──────────────────────────

    /// <summary>
    /// session.json：**结构化重写**。
    ///   · `storageRoot` ⇒ `storageRootToken`（Personal 令牌化）；
    ///   · `sessionId` ⇒ **每包别名**（默认跨包不可直接关联）；
    ///   · 其余字段（含**数字**字段）逐字段原样搬运 —— 旧实现只读字符串，把 processId /
    ///     eventCatalogCount / 单调锚点整段丢掉。
    /// </summary>
    internal static string SessionJson(string sourceJson, RedactionPolicy redaction, string sessionAlias, out string? keyId)
    {
        keyId = redaction.KeyId;
        var currentKeyId = redaction.KeyId;      // lambda 里不能用 out 参数 ⇒ 先取到局部变量
        using var doc = JsonDocument.Parse(sourceJson);
        var root = doc.RootElement;

        return Write(writer =>
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "storageRoot":
                        writer.WriteString("storageRootToken", redaction.Token(property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null));
                        continue;
                    case "sessionId":
                        writer.WriteString("sessionAlias", sessionAlias);
                        continue;
                    case "redactionKeyId":
                        writer.WriteString("redactionKeyId", currentKeyId);
                        continue;
                    default:
                        writer.WritePropertyName(property.Name);
                        WriteSanitizedValue(property.Value, writer, redaction, depth: 0);
                        continue;
                }
            }
            writer.WriteString("exportScope", "export");
            writer.WriteEndObject();
        });
    }

    private static void WriteSanitizedValue(JsonElement element, Utf8JsonWriter writer, RedactionPolicy redaction, int depth)
        => CopySanitized(element, writer, redaction, maxLen: 256, depth);

    private static readonly System.Text.RegularExpressions.Regex EvidenceRefPattern =
        new(@"\b[0-9a-fA-F]{8}#\d+\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 把形如 `&lt;8位十六进制&gt;#&lt;序号&gt;` 的证据引用前缀改写成每包别名。
    /// 只在**这种形状**上替换 ⇒ 不会误伤哈希、序号等其它十六进制文本。
    /// </summary>
    internal static string RewriteEvidenceRefs(string json, string rawShortId, string alias)
        => string.IsNullOrEmpty(rawShortId)
            ? json
            : EvidenceRefPattern.Replace(json, match =>
                match.Value.StartsWith(rawShortId, StringComparison.OrdinalIgnoreCase)
                    ? alias + match.Value.Substring(rawShortId.Length)
                    : match.Value);

    // ────────────────────────── metrics 快照行 ──────────────────────────

    internal static string? MetricsSnapshotLine(DiagnosticExportRequest request, RedactionPolicy redaction)
    {
        if (request.Health is not { } h) return null;

        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", "1.0");
            writer.WriteString("kind", "export-snapshot");
            writer.WriteString("redactionKeyId", redaction.KeyId);
            writer.WriteNumber("eventsProduced", h.EventsProduced);
            writer.WriteNumber("eventsAccepted", h.EventsAccepted);
            writer.WriteNumber("eventsWritten", h.EventsWritten);
            writer.WriteNumber("lostEpoch", h.LossEpoch);
            writer.WriteNumber("eventsDropped", h.EventsDropped);
            writer.WriteNumber("eventsEvicted", h.EventsEvicted);
            writer.WriteNumber("criticalLost", h.CriticalLost);
            writer.WriteBoolean("evidenceComplete", h.EvidenceComplete);
            writer.WriteNumber("analyzerLagMs", h.AnalyzerLagMs);
            writer.WriteNumber("writerLatencyMs", h.WriterLatencyMs);
            writer.WriteNumber("flushLatencyMs", h.FlushLatencyMs);
            writer.WriteNumber("ringBytes", h.RingBytes);
            writer.WriteNumber("writtenBytes", h.WrittenBytes);

            if (request.Flight is { } f)
            {
                writer.WriteStartObject("flight");
                writer.WriteNumber("ringEvents", f.RingEvents);
                writer.WriteNumber("overwritten", f.OverwrittenEvents);
                writer.WriteNumber("persistedWindows", f.PersistedWindows);
                writer.WriteEndObject();
            }
            if (request.Rules is { } r)
            {
                writer.WriteStartObject("rules");
                writer.WriteNumber("evaluated", r.EventsEvaluated);
                writer.WriteNumber("opened", r.IncidentsOpened);
                writer.WriteNumber("faults", r.RuleFaults);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }, indented: false);
    }

    // ────────────────────────── manifest.json ──────────────────────────

    internal static string Manifest(
        DiagnosticExportRequest request,
        string sessionAlias,
        string rawSessionId,
        IReadOnlyList<DiagnosticPackageEntry> entries,
        IReadOnlyList<string> warnings,
        string exportKeyId,
        string stamp)
    {
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", "1.0");
            writer.WriteString("exporterVersion", DiagnosticPackageExporter.ExporterVersion);
            writer.WriteString("packageId", stamp);
            writer.WriteString("exportedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("sessionAlias", sessionAlias);

            // 只有显式高级模式才写原始 SessionId（否则包与包之间可直接关联）。
            if (request.PreserveCrossPackageCorrelation)
                writer.WriteString("sessionId", rawSessionId);

            writer.WriteString("appVersion", request.AppVersion ?? "unknown");
            if (request.BuildId is not null) writer.WriteString("buildId", request.BuildId);
            writer.WriteString("catalogVersion", EventCatalog.CatalogVersion);
            writer.WriteString("catalogHash", EventCatalog.CatalogHash);

            writer.WriteStartObject("redaction");
            writer.WriteString("keyId", exportKeyId);
            writer.WriteString("scope", "export");
            writer.WriteString("crossPackageCorrelation", request.PreserveCrossPackageCorrelation ? "preserved-by-explicit-request" : "unlinkable-by-default");
            writer.WriteEndObject();

            writer.WriteString("delivery", DiagnosticPackageExporter.LocalOnlyNotice);
            writer.WriteBoolean("includedHighSensitivityAttachments", request.IncludeHighSensitivityAttachments);
            writer.WriteBoolean("includedFlightWindows", request.IncludeFlightWindows);
            writer.WriteNumber("entryCount", entries.Count);

            // ★ D6.1 §2.5 ★ 截止点：如实说明"覆盖到哪里、有没有没落盘的尾巴"。
            writer.WriteStartObject("cutoff");
            if (request.Cutoff is { } c)
            {
                writer.WriteNumber("requestedSequence", c.RequestedSequence);
                writer.WriteNumber("cutoffSequence", c.CutoffSequence);
                writer.WriteString("cutoffUtc", c.CutoffUtc.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("flushStatus", c.FlushStatus);
                writer.WriteNumber("coverageEndSequence", c.CoverageEndSequence);
                writer.WriteBoolean("anyTailLoss", c.AnyTailLoss);
                writer.WriteNumber("pendingAtCutoff", c.PendingAtCutoff);
                writer.WriteNumber("sealedSegments", c.SealedSegments);
                if (c.Note is not null) writer.WriteString("note", c.Note);
            }
            else
            {
                writer.WriteString("flushStatus", "not-provided");
                writer.WriteBoolean("anyTailLoss", true);
                writer.WriteString("note", "调用方未提供截止点：只导出导出时点已封存的段，包尾可能缺少最近事件");
            }
            writer.WriteEndObject();

            writer.WriteStartArray("warnings");
            foreach (var warning in warnings) writer.WriteStringValue(warning);
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    // ────────────────────────── summary.json ──────────────────────────

    internal static string Summary(
        DiagnosticExportRequest request,
        string sessionAlias,
        IReadOnlyList<string> warnings,
        string exportKeyId,
        RedactionPolicy redaction)
    {
        var incidents = request.Incidents ?? Array.Empty<Incident>();

        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", "1.0");
            writer.WriteString("sessionAlias", sessionAlias);
            writer.WriteString("generatedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("redactionKeyId", exportKeyId);
            writer.WriteString("delivery", DiagnosticPackageExporter.LocalOnlyNotice);

            // ★ D6.1 §2.2 ★ 不知道 ≠ 完整。
            var completeness = request.Health is not { } h
                ? "Unknown"
                : !h.EvidenceComplete ? "Partial"
                : h.IsDegraded ? "Degraded"
                : "Complete";

            writer.WriteStartObject("collectionHealth");
            if (request.Health is { } health)
            {
                writer.WriteBoolean("degraded", health.IsDegraded);
                writer.WriteBoolean("evidenceComplete", health.EvidenceComplete);
                writer.WriteNumber("lossEpoch", health.LossEpoch);
                writer.WriteNumber("eventsDropped", health.EventsDropped);
                writer.WriteNumber("eventsEvicted", health.EventsEvicted);
                writer.WriteNumber("criticalLost", health.CriticalLost);
                writer.WriteBoolean("storageDegraded", health.StorageDegraded);
            }
            else
            {
                writer.WriteString("unavailable", "health-snapshot-missing");
            }
            writer.WriteEndObject();

            writer.WriteString("evidenceCompleteness", completeness);
            writer.WriteString("evidenceNote", completeness switch
            {
                "Complete" => "采集证据完整（无丢失、无降级）：包内「没有观察到」可以按「没有发生」使用",
                "Partial" => "采集证据不完整（有丢失/降级）：包内「没有观察到」不等于「没有发生」",
                "Degraded" => "采集处于降级状态（存储或分支异常）：包内「没有观察到」不等于「没有发生」",
                _ => "证据完整性**未知**（缺少采集健康快照）：不得据此断言「没有发生」",
            });

            writer.WriteStartArray("incidents");
            foreach (var incident in incidents)
            {
                writer.WriteStartObject();
                writer.WriteString("incidentId", redaction.SanitizeText(incident.IncidentId, 128));
                writer.WriteString("symptomCode", incident.SymptomCode);
                writer.WriteString("ruleId", incident.RuleId);
                writer.WriteString("severity", incident.Severity.ToString());
                writer.WriteString("status", incident.Status.ToString());
                writer.WriteString("confidence", incident.Confidence.ToString());
                writer.WriteBoolean("evidenceIncomplete", incident.EvidenceIncomplete);
                writer.WriteString("userSummary", redaction.SanitizeText(incident.UserFacingSummary, 512) ?? string.Empty);
                writer.WriteString("breakPoint", incident.BreakPoint ?? "-");

                writer.WriteStartArray("facts");
                foreach (var fact in incident.Facts)
                {
                    writer.WriteStartObject();
                    // 证据引用只给**本包别名 + 序号**（不泄露原始 SessionId）。
                    writer.WriteString("evidence", $"S1#{fact.Ref.Sequence.ToString(CultureInfo.InvariantCulture)}");
                    writer.WriteString("eventCode", fact.EventCode);
                    writer.WriteString("role", fact.Role);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                writer.WriteStartArray("candidates");
                foreach (var candidate in incident.Candidates)
                {
                    writer.WriteStartObject();
                    writer.WriteString("code", candidate.CandidateCode);
                    writer.WriteString("rationale", redaction.SanitizeText(candidate.Rationale, 512) ?? string.Empty);
                    writer.WriteString("confidence", candidate.Confidence.ToString());
                    writer.WriteBoolean("refuted", candidate.Refuted);
                    if (candidate.RefutationReason is not null)
                        writer.WriteString("refutationReason", redaction.SanitizeText(candidate.RefutationReason, 512));
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                writer.WriteStartArray("missingEvidence");
                foreach (var missing in incident.Missing)
                {
                    writer.WriteStartObject();
                    writer.WriteString("contractId", missing.ContractId);
                    writer.WriteString("expected", missing.ExpectedEventCode);
                    writer.WriteString("reason", missing.ReasonCode);
                    writer.WriteNumber("coverageWatermark", missing.CoverageWatermark);
                    writer.WriteBoolean("collectionHealthy", missing.CollectionHealthy);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                writer.WriteStartArray("nextChecks");
                foreach (var check in incident.SuggestedChecks)
                    writer.WriteStringValue(redaction.SanitizeText(check, 512) ?? string.Empty);
                writer.WriteEndArray();

                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("warnings");
            foreach (var warning in warnings) writer.WriteStringValue(warning);
            writer.WriteEndArray();

            writer.WriteEndObject();
        });
    }
}