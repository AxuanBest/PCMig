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
    internal static string? SanitizeAllStrings(string json, RedactionPolicy redaction, int maxStringLength = 512, TypedIdContext? ids = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Write(writer => CopySanitized(doc.RootElement, writer, redaction, maxStringLength, 0, ids, propertyName: null), indented: false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// ★ D6.3 §6.3 ★ 本包的身份语境：当前会话在包内的 id/别名。
    /// 有它才会对"直接稳定标识符"做每包独立假名化。
    /// </summary>
    internal readonly record struct TypedIdContext(Guid PackageSessionId, string SessionAlias);

    /// <summary>
    /// 会被每包假名化的标识符字段名（统一一处登记，避免各调用点各写一套）。
    ///
    /// ★ 比较**大小写不敏感** ★：事件信封是 camelCase（`jobId`），事件卡 DTO 是 PascalCase
    /// （`JobId` / `IncidentId`）—— 两条路都得覆盖，否则卡片会把原始标识符漏出去（实测踩过）。
    ///
    /// `incidentId` 是**复合键**（`PERSISTENCE_WRITE_FAILED|Receipt|<jobId>` /
    /// `PREVIOUS_SESSION_UNCLEAN|<sessionId>`），里面直接嵌着 jobId / objectId / 会话 id，
    /// 所以整串一起假名化：同包同串仍同假名，跨包对不上。
    /// 注意 `evidence` / `evidenceRefs` **不能**进这张表：它们随后还要被换成 `S1#<序号>` 别名。
    /// </summary>
    private static bool IsPseudonymizedIdField(string name) => name.ToLowerInvariant() switch
    {
        "sessionid" or "previoussessionid" or "sessionalias" => true,
        "actionid" or "operationid" or "parentoperationid" => true,
        "jobid" or "objectid" => true,
        "traceid" or "spanid" or "parentspanid" or "correlationid" => true,
        "processidentity" or "storageroottoken" => true,
        "incidentid" => true,
        _ => false,
    };

    /// <summary>
    /// 标识符 → 每包假名。规则：
    ///   · 本包自身会话 id / 别名 ⇒ **保持一致**（包内引用不能自相矛盾）；
    ///   · 其它值（含指向"上一次会话"的 id）⇒ HMAC 假名，同值同假名、异值异假名。
    /// </summary>
    private static string RemapTypedId(string field, string? value, TypedIdContext ids, RedactionPolicy redaction)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        switch (field.ToLowerInvariant())
        {
            case "sessionalias":
                if (string.Equals(value, ids.SessionAlias, StringComparison.Ordinal)) return value;
                break;
            case "sessionid":
                if (Guid.TryParse(value, out var current) && current == ids.PackageSessionId) return value;
                break;
        }

        var kind = field.ToLowerInvariant() switch
        {
            "sessionid" or "previoussessionid" or "sessionalias" => "SES",
            "actionid" => "ACT",
            "operationid" => "OPR",
            "parentoperationid" => "OPRP",
            "jobid" => "JOB",
            "objectid" => "OBJ",
            "traceid" => "TRC",
            "spanid" => "SPN",
            "parentspanid" => "SPNP",
            "correlationid" => "COR",
            "processidentity" => "PID",
            "storageroottoken" => "SRT",
            "incidentid" => "INC",
            _ => "ID",
        };
        return redaction.Pseudonym(kind, value);
    }

    private const int MaxDepth = 12;

    private static void CopySanitized(
        JsonElement element, Utf8JsonWriter writer, RedactionPolicy redaction, int maxLen, int depth,
        TypedIdContext? ids = null, string? propertyName = null)
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
                    CopySanitized(property.Value, writer, redaction, maxLen, depth + 1, ids, property.Name);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    // 数组元素沿用外层字段名：`"actionIds": ["..."]` 也得按标识符处理。
                    CopySanitized(item, writer, redaction, maxLen, depth + 1, ids, propertyName);
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                var value = element.GetString();
                if (ids is { } ctx && propertyName is not null && IsPseudonymizedIdField(propertyName))
                {
                    writer.WriteStringValue(RemapTypedId(propertyName, value, ctx, redaction));
                    break;
                }
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
    internal static string SessionJson(string sourceJson, RedactionPolicy redaction, string sessionAlias, out string? keyId, TypedIdContext? ids = null)
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
                        WriteSanitizedValue(property.Value, writer, redaction, depth: 0, propertyName: property.Name, ids);
                        continue;
                }
            }
            writer.WriteString("exportScope", "export");
            writer.WriteEndObject();
        });
    }

    private static void WriteSanitizedValue(
        JsonElement element, Utf8JsonWriter writer, RedactionPolicy redaction, int depth,
        string? propertyName = null, TypedIdContext? ids = null)
        => CopySanitized(element, writer, redaction, maxLen: 256, depth, ids, propertyName);

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
            // ★ D6.3 §9（审计 P2-1）★ 保留性淘汰与"真的丢了"分开写：
            //   它可见、有数，但既不代表证据不完整，也不代表诊断系统不健康。
            writer.WriteNumber("retentionEvictions", h.RetentionEvictions);
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
            // ★ D6.3 §6.4 ★ 废除 unlinkable-by-default 这种绝对承诺：假名化 ≠ 匿名。
            writer.WriteString("crossPackageCorrelation", request.PreserveCrossPackageCorrelation ? "preserved-by-explicit-request" : "pseudonymized-per-package");
            writer.WriteString("crossPackageCorrelationNotice", request.PreserveCrossPackageCorrelation
                ? "显式请求保留跨包关联：包内保留原始稳定标识符，可与其它包直接关联。"
                : "默认对已登记的直接稳定标识符执行每包独立假名化；不保证匿名或跨包不可关联——时间、事件顺序与内容仍可能形成关联。");
            writer.WriteString("crossPackageCorrelationNoticeEn", request.PreserveCrossPackageCorrelation
                ? "Cross-package correlation preserved by explicit request: original stable identifiers are retained in this package."
                : "Stable direct identifiers are pseudonymized independently per package by default. This does not guarantee anonymity or unlinkability; timestamps, event ordering and content may still permit correlation.");
            writer.WriteEndObject();

            writer.WriteString("delivery", DiagnosticPackageExporter.LocalOnlyNotice);
            writer.WriteBoolean("includedHighSensitivityAttachments", request.IncludeHighSensitivityAttachments);
            // ★ D6.3 §6.1 ★ Included 只能由**最终 ZIP 里真实存在的条目**算出：
            //   配置说 include=true、实际没打进包 ⇒ 必须写 false（旧实现直接抄 request，是假包含）。
            var flightEntryCount = entries.Count(e => e.Path.Contains("/flight/", StringComparison.OrdinalIgnoreCase));
            writer.WriteBoolean("includedFlightWindows", flightEntryCount > 0);
            writer.WriteNumber("flightEntryCount", flightEntryCount);
            writer.WriteBoolean("requestedFlightWindows", request.IncludeFlightWindows);
            if (request.IncludeFlightWindows && flightEntryCount == 0)
                writer.WriteString("flightWindowsNote", "请求包含飞行窗口，但最终包内没有任何 flight 条目 ⇒ included=false（不以配置意图冒充包含）");

            // 本导出器不打包任何高敏附件（该开关目前只是请求意图）⇒ 包含声明必须写 false，
            // 请求意图另用一个字段如实记录，二者不得混淆。
            writer.WriteBoolean("includedHighSensitivityAttachments", false);
            writer.WriteBoolean("requestedHighSensitivityAttachments", request.IncludeHighSensitivityAttachments);
            writer.WriteNumber("entryCount", entries.Count);

            // ★ 缺口①收口（D6.3 §3）★ 本包"是否完整"只有一份判据实现（EvaluatePackageEvidence）：
            //   summary.json 与本 manifest 都从它取结论。谁自己另算一次，同一个 ZIP 里就会出现两种说法。
            var verdict = EvaluatePackageEvidence(request, warnings);

            // ★ D6.1 §2.5 ★ 截止点：如实说明"覆盖到哪里、有没有没落盘的尾巴"。
            writer.WriteStartObject("cutoff");
            if (request.Cutoff is { } c)
            {
                writer.WriteNumber("requestedSequence", c.RequestedSequence);
                writer.WriteNumber("cutoffSequence", c.CutoffSequence);
                // ★ D6.3 §3 ★ 最小已写序号与连续水位必须分开写：
                //   cutoffSequence = 连续水位（唯一允许用来判"覆盖到哪里"的数字）；
                //   settledMaxSequence = 最大已结算序号（仅展示，绝不可当完整性判据）。
                writer.WriteNumber("settledMaxSequence", c.SettledMaxSequence);
                writer.WriteNumber("gapCount", c.GapCount);
                writer.WriteBoolean("trackingOverflowed", c.TrackingOverflowed);
                writer.WriteBoolean("evidenceContiguous", c.IsEvidenceContiguous);
                writer.WriteString("cutoffUtc", c.CutoffUtc.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("flushStatus", c.FlushStatus);
                writer.WriteNumber("coverageEndSequence", c.CoverageEndSequence);
                writer.WriteBoolean("anyTailLoss", c.AnyTailLoss);
                writer.WriteNumber("pendingAtCutoff", c.PendingAtCutoff);
                writer.WriteNumber("sealedSegments", c.SealedSegments);
                // ★ D6.3 §3 ★ 导出闸门聚合：本包证据面**全部**产出方各自的结果都在这里如实登记，
                //   不允许"事件段 OK 就说整包完整"。
                writer.WriteBoolean("incidentSealOk", c.IncidentSealOk);
                writer.WriteNumber("serializationFailures", c.SerializationFailures);
                writer.WriteNumber("corruptLinesSkipped", c.CorruptLinesSkipped);
                writer.WriteBoolean("flightEvidenceIncomplete", c.FlightEvidenceIncomplete);
                // ★ 缺口①收口 ★ 绝不在 manifest 里自算：必须与 summary.json 的 packageEvidenceComplete 同源同值，
                //   否则同一个包一处说 false、一处说 true，复核者无从判断该信哪句（链路 9 实机缺陷）。
                writer.WriteBoolean("packageEvidenceComplete", verdict.PackageEvidenceComplete);
                // 逐条理由 = 捕捉侧聚合理由 + **导出时才知道的事实**（跳过的损坏行 / 未封段 / 飞行窗口一条都没进包）。
                var manifestBlockers = verdict.Blockers.Count > 0
                    ? string.Join("; ", verdict.Blockers)
                    : c.EvidenceBlockReason;
                if (manifestBlockers is not null)
                    writer.WriteString("evidenceBlockers", manifestBlockers);
                if (c.Note is not null) writer.WriteString("note", c.Note);
            }
            else
            {
                writer.WriteString("flushStatus", "not-provided");
                writer.WriteBoolean("anyTailLoss", true);
                // ★ 缺口①收口 ★ 没有截止点 ⇒ 判据给的是 Unknown/Partial：manifest 必须与 summary 写同一句话。
                writer.WriteBoolean("packageEvidenceComplete", verdict.PackageEvidenceComplete);
                // ★ 缺口①收口 ★ 理由也必须同样给全：只说 false、不说被谁挡住，复核者仍旧无从下手。
                if (verdict.Blockers.Count > 0)
                    writer.WriteString("evidenceBlockers", string.Join("; ", verdict.Blockers));
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

            // ★ D6.3 §3 / §6.1 ★ 「Complete」只能由**本包实际证据面**得出：
            //   运行时健康乐观状态、配置意图、导出时跳过的损坏行，都不能被忽略。
            //
            // ★ 缺口①收口 ★ 判据只有一份实现（EvaluatePackageEvidence）：manifest.json 与 summary.json
            //   都只许从它取结论。两处各自算一次 ⇒ 同一个 ZIP 里出现两种说法（实机缺陷）。
            var verdict = EvaluatePackageEvidence(request, warnings);
            var completeness = verdict.Completeness;

            writer.WriteStartObject("collectionHealth");
            if (request.Health is { } health)
            {
                writer.WriteBoolean("degraded", health.IsDegraded);
                writer.WriteBoolean("evidenceComplete", health.EvidenceComplete);
                writer.WriteNumber("lossEpoch", health.LossEpoch);
                writer.WriteNumber("eventsDropped", health.EventsDropped);
                writer.WriteNumber("eventsEvicted", health.EventsEvicted);
                writer.WriteNumber("retentionEvictions", health.RetentionEvictions);
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
                "Complete" => "采集证据完整（无丢失、无降级、导出无跳过）：包内「没有观察到」可以按「没有发生」使用",
                "Partial" => "采集证据不完整（有丢失/降级/导出跳过/覆盖不连续）：包内「没有观察到」不等于「没有发生」；具体原因见 evidenceBlockers",
                "Degraded" => "采集处于降级状态（存储或分支异常）：包内「没有观察到」不等于「没有发生」；具体原因见 evidenceBlockers",
                _ => "证据完整性**未知**（缺少采集健康快照）：不得据此断言「没有发生」",
            });

            // ★ D6.3 §3 ★ 不完整的理由必须逐条可见：只说 Partial 而不说"被谁挡住"，复核者无从下手。
            writer.WriteStartArray("evidenceBlockers");
            foreach (var blocker in verdict.Blockers) writer.WriteStringValue(blocker);
            writer.WriteEndArray();
            writer.WriteBoolean("packageEvidenceComplete", verdict.PackageEvidenceComplete);

            writer.WriteStartArray("incidents");
            foreach (var incident in incidents)
            {
                writer.WriteStartObject();
                // 复合键里嵌着 jobId / objectId / 会话 id ⇒ 整串按每包假名（`INC-…`）；
                // 与包内事件卡里同一个 incidentId 用同一 kind + 同一策略 ⇒ 包内仍对得上。
                writer.WriteString("incidentId", redaction.Pseudonym("INC", incident.IncidentId));
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

    // ─────────── 包级完整性判据（manifest.json / summary.json **共用同一份实现**） ───────────

    /// <summary>
    /// ★ D6.3 §3 缺口①收口：同一个包不许说两句相反的话 ★
    /// 「本包证据是否完整」的**唯一**实现 —— `manifest.json` 与 `summary.json` 都必须从这里取结论。
    ///
    /// 实机缺陷（链路 9 导出包）：请求了飞行窗口、最终一条都没进包时，summary 判 `Partial`
    /// （blockers 逐条可见），manifest 却直接写捕捉侧判据 ⇒ 同一个 ZIP 里 `packageEvidenceComplete`
    /// 一处 false、一处 true，复核者无从判断该信哪一句。
    ///
    /// 判据（**只来自本包实际证据面**，不看配置意图、不看运行时乐观状态）：
    ///   · 运行时健康：缺健康快照 ⇒ Unknown（不知道 ≠ 完整）；EvidenceComplete=false ⇒ Partial；降级 ⇒ Degraded；
    ///   · 捕捉侧聚合判据 <see cref="DiagnosticExportCutoff.IsPackageEvidenceComplete"/>
    ///     （事件连续 / 事件卡封存 / 无序列化失败 / 无损坏行 / 飞行记录器无缺损）；
    ///   · **导出时才知道的事实**：导出过程中跳过的损坏行、未封段、段清单不可读、
    ///     请求了飞行窗口却没有任何条目进包。
    /// </summary>
    internal static PackageEvidenceVerdict EvaluatePackageEvidence(
        DiagnosticExportRequest request,
        IReadOnlyList<string> warnings)
    {
        var blockers = new List<string>();
        var captureSideComplete = false;

        if (request.Cutoff is { } cutoff)
        {
            captureSideComplete = cutoff.IsPackageEvidenceComplete;
            if (!cutoff.IncidentSealOk) blockers.Add("incident-seal-failed");
            if (cutoff.SerializationFailures > 0) blockers.Add($"serialization-failures={cutoff.SerializationFailures}");
            if (cutoff.CorruptLinesSkipped > 0) blockers.Add($"corrupt-lines-skipped={cutoff.CorruptLinesSkipped}");
            if (cutoff.FlightEvidenceIncomplete) blockers.Add("flight-evidence-incomplete");
            if (cutoff.TrackingOverflowed) blockers.Add("gap-tracking-overflow");
            if (cutoff.GapCount > 0) blockers.Add($"unknown-gaps={cutoff.GapCount}");
            if (cutoff.PendingAtCutoff > 0) blockers.Add($"tail-pending={cutoff.PendingAtCutoff}");
            if (cutoff.AnyTailLoss) blockers.Add("tail-loss");
        }
        else
        {
            // 没有截止点就**不知道**包尾覆盖到哪里（manifest 同样写 anyTailLoss=true）⇒ 不算完整。
            blockers.Add("cutoff-not-provided");
        }

        // 导出过程中真实跳过的行 / 未封段 / 请求了但没进包 ⇒ 包内证据有洞，同样不许说 Complete。
        foreach (var warning in warnings)
        {
            if (warning.StartsWith("unparsable-lines-skipped:", StringComparison.Ordinal)
                || warning.StartsWith("unparsable-incident-lines-skipped:", StringComparison.Ordinal)
                || warning.StartsWith("active-segment-skipped:", StringComparison.Ordinal)
                || warning.StartsWith("segment-manifest-unreadable:", StringComparison.Ordinal)
                || warning.StartsWith("flight-windows-requested-but-not-included", StringComparison.Ordinal))
                blockers.Add(warning);
        }

        // 捕捉侧判据说不完整时，上面逐条登记的 blocker 必然非空（两者枚举同一组事实）——
        // 这里两条都判，是为了"新增聚合项却忘了登记理由"时**仍然不许说 Complete**（宁可 Partial 带少理由，也不许假完整）。
        // ★ D6.1 §2.2 ★ 不知道 ≠ 完整。★ D6.3 §3 ★ 有 blocker 就只能是 Partial。
        var completeness = request.Health is not { } h
            ? "Unknown"
            : !h.EvidenceComplete ? "Partial"
            : h.IsDegraded ? "Degraded"
            : !captureSideComplete || blockers.Count > 0 ? "Partial"
            : "Complete";

        return new PackageEvidenceVerdict(completeness, completeness == "Complete", blockers);
    }

    /// <summary>包级完整性判定结果（判据与逐条理由必须一起传递，避免调用方只看布尔）。</summary>
    internal readonly record struct PackageEvidenceVerdict(
        string Completeness,
        bool PackageEvidenceComplete,
        List<string> Blockers);
}