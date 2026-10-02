using System.Globalization;
using System.Text;
using System.Text.Json;
using PCMig.Diagnostics.Abstractions.Payloads;

namespace PCMig.Diagnostics.Abstractions.Serialization;

/// <summary>
/// 规范事件 JSON 编解码（**唯一**的落盘格式定义）。
///
/// 为什么放在契约层而不是 runtime：writer、回放读取、viewer、导出四个消费者必须用**同一份**
/// 格式定义，否则"写进去的"和"读出来的"会悄悄漂移（这正是诊断系统最不能出的错）。
/// 这里只做内存↔JSON，不做任何文件 I/O。
///
/// 读取容错口径：
///   · 缺失字段取默认值；未知字段忽略（向前兼容未知扩展）；
///   · 未知枚举 token **保留原文**（进 UnknownTokens），绝不静默强转成 Succeeded/Error 这类有语义的值；
///   · catalog 里查不到的 EventId ⇒ 用 `UNK-000` 描述符保留原始 id/name（规则必须跳过）；
///   · 任何异常都转成 `false + errorCode`，不回退成"看起来成功"。
/// </summary>
public static class DiagnosticEventJson
{
    private const int MaxUnknownTokens = 8;
    private const int MaxUnknownTokenLength = 64;

    /// <summary>把事件写成 JSON（调用方决定是否缩进；JSONL 用非缩进）。</summary>
    public static void Write(Utf8JsonWriter writer, DiagnosticEvent evt)
    {
        writer.WriteStartObject();

        writer.WriteString("schemaVersion", evt.SchemaVersion);

        writer.WriteString("sessionId", DiagnosticId.Format(evt.SessionId));
        writer.WriteNumber("sequence", evt.Sequence);
        writer.WriteString("timestampUtc", evt.TimestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        writer.WriteNumber("monotonicTimestamp", evt.MonotonicTimestamp);

        writer.WriteNumber("eventId", evt.Descriptor.EventId);
        writer.WriteString("eventCode", evt.Descriptor.Code);
        writer.WriteString("eventName", evt.Descriptor.Name);
        // ★ D6.3 §6.5 ★ 版本纪律：写**这一行声明的版本**，绝不改写成当前契约版本。
        //   旧实现写 `evt.Descriptor.Version`（当前版本）⇒ 一行 `eventVersion=999` 经
        //   Parse→ToJsonLine→Parse 之后会变成"当前版本且完全受支持"，未来版本被洗白成事实。
        //   · 有声明版本 ⇒ 原样写出（受支持与否由再解析按声明版本重新判定）；
        //   · 无声明版本但**不受支持**（源行本来就没写版本）⇒ 不发明一个版本，保持"缺失"；
        //   · 新产生的事件（无声明、且不受支持=false）⇒ 写当前契约版本，与旧行为一致。
        if (evt.DeclaredEventVersion is { } declaredVersion)
            writer.WriteNumber("eventVersion", declaredVersion);
        else if (!evt.VersionUnsupported)
            writer.WriteNumber("eventVersion", evt.Descriptor.Version);
        if (evt.VersionUnsupported)
            writer.WriteBoolean("versionUnsupported", true);
        writer.WriteString("category", evt.Descriptor.Category.ToString());

        writer.WriteString("level", evt.Level.ToString());
        writer.WriteString("deliveryClass", evt.Delivery.ToString());
        writer.WriteString("evidenceQuality", evt.EvidenceQuality.ToString());
        writer.WriteString("captureMode", evt.CaptureMode.ToString());

        PayloadJson.WriteStringOrNull(writer, "component", evt.Component);
        PayloadJson.WriteStringOrNull(writer, "controlId", evt.ControlId);
        PayloadJson.WriteStringOrNull(writer, "actionId", evt.ActionId is null ? null : DiagnosticId.Format(evt.ActionId.Value));
        PayloadJson.WriteStringOrNull(writer, "operationId", evt.OperationId is null ? null : DiagnosticId.Format(evt.OperationId.Value));
        PayloadJson.WriteStringOrNull(writer, "parentOperationId", evt.ParentOperationId is null ? null : DiagnosticId.Format(evt.ParentOperationId.Value));
        PayloadJson.WriteStringOrNull(writer, "jobId", evt.JobId);
        PayloadJson.WriteStringOrNull(writer, "objectId", evt.ObjectId);
        PayloadJson.WriteNumberOrNull(writer, "runGeneration", evt.RunGeneration);
        PayloadJson.WriteNumberOrNull(writer, "attempt", evt.Attempt);
        PayloadJson.WriteStringOrNull(writer, "pass", evt.Pass);

        if (evt.Outcome is not null) writer.WriteString("outcome", evt.Outcome.Value.ToString());

        PayloadJson.WriteNumberOrNull(writer, "observationVersion", evt.ObservationVersion);
        PayloadJson.WriteNumberOrNull(writer, "projectionVersion", evt.ProjectionVersion);
        if (evt.StateOwner is not null) writer.WriteString("stateOwner", evt.StateOwner.Value.ToString());
        PayloadJson.WriteStringOrNull(writer, "phase", evt.Phase);

        PayloadJson.WriteStringOrNull(writer, "traceId", evt.TraceId);
        PayloadJson.WriteStringOrNull(writer, "spanId", evt.SpanId);
        PayloadJson.WriteStringOrNull(writer, "parentSpanId", evt.ParentSpanId);

        if (evt.Causation is not null && !evt.Causation.Value.IsNone)
        {
            writer.WritePropertyName("causation");
            writer.WriteStartObject();
            writer.WriteString("sessionId", DiagnosticId.Format(evt.Causation.Value.SessionId));
            writer.WriteNumber("sequence", evt.Causation.Value.Sequence);
            writer.WriteEndObject();
        }

        PayloadJson.WriteStringOrNull(writer, "correlationId", evt.CorrelationId);

        writer.WriteString("errorDomain", evt.ErrorDomain.ToString());
        PayloadJson.WriteStringOrNull(writer, "exceptionType", evt.ExceptionType);
        PayloadJson.WriteNumberOrNull(writer, "hresult", evt.HResult);
        PayloadJson.WriteNumberOrNull(writer, "win32Error", evt.Win32Error);
        PayloadJson.WriteNumberOrNull(writer, "socketError", evt.SocketError);
        PayloadJson.WriteNumberOrNull(writer, "robocopyExitCode", evt.RobocopyExitCode);
        PayloadJson.WriteNumberOrNull(writer, "durationMs", evt.DurationMs);
        PayloadJson.WriteStringOrNull(writer, "message", evt.Message);

        if (evt.Path is not null)
        {
            writer.WritePropertyName("path");
            writer.WriteStartObject();
            writer.WriteString("role", evt.Path.Role.ToString());
            writer.WriteString("rootKind", evt.Path.RootKind);
            PayloadJson.WriteStringOrNull(writer, "rootAlias", evt.Path.RootAlias);
            writer.WriteString("pathToken", evt.Path.PathToken);
            PayloadJson.WriteStringOrNull(writer, "keyId", evt.Path.KeyId);
            PayloadJson.WriteStringOrNull(writer, "scope", evt.Path.Scope);
            PayloadJson.WriteStringOrNull(writer, "extensionClass", evt.Path.ExtensionClass);
            PayloadJson.WriteNumberOrNull(writer, "depthBucket", evt.Path.DepthBucket);
            writer.WriteEndObject();
        }

        if (evt.Truncated) writer.WriteBoolean("truncated", true);
        PayloadJson.WriteNumberOrNull(writer, "lossEpoch", evt.LossEpoch);
        PayloadJson.WriteNumberOrNull(writer, "contractVersion", evt.ContractVersion);

        if (evt.Payload is not null)
        {
            writer.WriteString("payloadName", evt.Payload.PayloadName);
            writer.WritePropertyName("payload");
            writer.WriteStartObject();
            evt.Payload.WriteJson(writer);
            writer.WriteEndObject();
        }
        // ★ R-5 ★ 解不开的 payload 必须逐字写回（保存 ≠ 解释）：字段名与值都不改，
        //   "无法用当前契约解释"这件事由 unknownTokens 的 `payload:unsupported-version` /
        //   `payload:unknown-event` 承担，二者缺一不可。
        else if (evt.RawPayload is { } rawPayload)
        {
            if (evt.RawPayloadName is not null) writer.WriteString("payloadName", evt.RawPayloadName);
            writer.WritePropertyName("payload");
            rawPayload.WriteTo(writer);
        }

        if (evt.UnknownTokens is { Count: > 0 })
        {
            writer.WritePropertyName("unknownTokens");
            writer.WriteStartArray();
            foreach (var token in evt.UnknownTokens) writer.WriteStringValue(token);
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    /// <summary>单行 JSON（JSONL 的一行；也用于测试与工具）。</summary>
    public static string ToJsonLine(DiagnosticEvent evt)
    {
        using var stream = new MemoryStream(512);
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            Write(writer, evt);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static bool TryParse(string json, out DiagnosticEvent? evt, out string? errorCode) =>
        TryParse(Encoding.UTF8.GetBytes(json), out evt, out errorCode);

    /// <summary>解析一行 JSON。**不抛**：失败返回 false + 稳定 errorCode。</summary>
    public static bool TryParse(ReadOnlySpan<byte> utf8Json, out DiagnosticEvent? evt, out string? errorCode)
    {
        evt = null;
        errorCode = null;
        try
        {
            var reader = new Utf8JsonReader(utf8Json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                errorCode = "not-an-object";
                return false;
            }

            string? schemaVersion = null;
            Guid sessionId = Guid.Empty;
            long sequence = 0;
            DateTimeOffset timestampUtc = default;
            long monotonicTimestamp = 0;
            var eventId = 0;
            string? eventCode = null;
            string? eventName = null;
            var eventVersion = 0;
            var hasEventVersion = false;
            // ★ D6.3 §6.5 ★ 行里可以自述"不受支持"：再解析必须尊重它，
            //   否则一次导出往返就能把不支持的证据洗成受支持。
            var declaredUnsupported = false;
            var hasEventId = false;
            var hasSessionId = false;
            var hasSequence = false;
            var hasTimestamp = false;
            var hasMonotonic = false;
            var hasLevel = false;
            var hasDelivery = false;
            var hasEvidenceQuality = false;
            var hasCaptureMode = false;
            DiagnosticCategory? category = null;

            DiagnosticLevel? level = null;
            DeliveryClass? delivery = null;
            EvidenceQuality? evidenceQuality = null;
            CaptureMode? captureMode = null;

            string? component = null, jobId = null, objectId = null, pass = null, phase = null, controlId = null;
            string? traceId = null, spanId = null, parentSpanId = null, correlationId = null;
            string? exceptionType = null, message = null, payloadName = null;
            Guid? actionId = null, operationId = null, parentOperationId = null;
            long? runGeneration = null, observationVersion = null, projectionVersion = null, lossEpoch = null;
            int? attempt = null, hresult = null, win32Error = null, socketError = null, robocopyExitCode = null, durationMs = null, contractVersion = null;
            DiagnosticOutcome? outcome = null;
            StateOwner? stateOwner = null;
            ErrorDomain? errorDomain = null;
            var truncated = false;
            EventRef? causation = null;
            PathRef? path = null;
            JsonElement? payloadElement = null;
            List<string>? unknownTokens = null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                var name = reader.GetString();
                if (!reader.Read()) break;

                switch (name)
                {
                    case "schemaVersion": schemaVersion = ReadString(ref reader); break;
                    case "sessionId": hasSessionId = true; Guid.TryParse(ReadString(ref reader), out sessionId); break;
                    case "sequence": hasSequence = true; sequence = reader.TokenType == JsonTokenType.Number ? reader.GetInt64() : 0; break;
                    case "timestampUtc": hasTimestamp = true; timestampUtc = ParseTime(ReadString(ref reader)); break;
                    case "monotonicTimestamp": hasMonotonic = true; monotonicTimestamp = reader.TokenType == JsonTokenType.Number ? reader.GetInt64() : 0; break;
                    case "eventId": hasEventId = true; eventId = reader.TokenType == JsonTokenType.Number ? reader.GetInt32() : 0; break;
                    case "eventCode": eventCode = ReadString(ref reader); break;
                    case "eventName": eventName = ReadString(ref reader); break;
                    case "eventVersion": hasEventVersion = true; eventVersion = reader.TokenType == JsonTokenType.Number ? reader.GetInt32() : 0; break;
                    case "versionUnsupported": declaredUnsupported = reader.TokenType == JsonTokenType.True; break;
                    case "category": category = ReadEnum(ref reader, DiagnosticCategory.Unknown, ref unknownTokens); break;
                    // ★ 存在性单独记 ★ "字段在场但 token 不认识"（例如 level:"Catastrophic"）**不是**缺字段：
                    //   保留 raw token 并回落到 catalog 声明的值（这是既有契约，必须保持）。
                    case "level": hasLevel = true; level = ReadEnumNullable<DiagnosticLevel>(ref reader, ref unknownTokens); break;
                    case "deliveryClass": hasDelivery = true; delivery = ReadEnumNullable<DeliveryClass>(ref reader, ref unknownTokens); break;
                    case "evidenceQuality": hasEvidenceQuality = true; evidenceQuality = ReadEnumNullable<EvidenceQuality>(ref reader, ref unknownTokens); break;
                    case "captureMode": hasCaptureMode = true; captureMode = ReadEnumNullable<CaptureMode>(ref reader, ref unknownTokens); break;
                    case "component": component = ReadString(ref reader); break;
                    case "controlId": controlId = ReadString(ref reader); break;
                    case "actionId": actionId = ReadGuid(ref reader); break;
                    case "operationId": operationId = ReadGuid(ref reader); break;
                    case "parentOperationId": parentOperationId = ReadGuid(ref reader); break;
                    case "jobId": jobId = ReadString(ref reader); break;
                    case "objectId": objectId = ReadString(ref reader); break;
                    case "runGeneration": runGeneration = ReadLongNullable(ref reader); break;
                    case "attempt": attempt = ReadIntNullable(ref reader); break;
                    case "pass": pass = ReadString(ref reader); break;
                    case "outcome": outcome = ReadEnumNullable<DiagnosticOutcome>(ref reader, ref unknownTokens); break;
                    case "observationVersion": observationVersion = ReadLongNullable(ref reader); break;
                    case "projectionVersion": projectionVersion = ReadLongNullable(ref reader); break;
                    case "stateOwner": stateOwner = ReadEnumNullable<StateOwner>(ref reader, ref unknownTokens); break;
                    case "phase": phase = ReadString(ref reader); break;
                    case "traceId": traceId = ReadString(ref reader); break;
                    case "spanId": spanId = ReadString(ref reader); break;
                    case "parentSpanId": parentSpanId = ReadString(ref reader); break;
                    case "correlationId": correlationId = ReadString(ref reader); break;
                    case "errorDomain": errorDomain = ReadEnumNullable<ErrorDomain>(ref reader, ref unknownTokens); break;
                    case "exceptionType": exceptionType = ReadString(ref reader); break;
                    case "hresult": hresult = ReadIntNullable(ref reader); break;
                    case "win32Error": win32Error = ReadIntNullable(ref reader); break;
                    case "socketError": socketError = ReadIntNullable(ref reader); break;
                    case "robocopyExitCode": robocopyExitCode = ReadIntNullable(ref reader); break;
                    case "durationMs": durationMs = ReadIntNullable(ref reader); break;
                    case "message": message = ReadString(ref reader); break;
                    case "truncated": truncated = reader.TokenType == JsonTokenType.True; break;
                    case "lossEpoch": lossEpoch = ReadLongNullable(ref reader); break;
                    case "contractVersion": contractVersion = ReadIntNullable(ref reader); break;
                    case "payloadName": payloadName = ReadString(ref reader); break;
                    case "causation": causation = ReadCausation(ref reader); break;
                    case "path": path = ReadPath(ref reader); break;
                    case "payload": payloadElement = ReadClone(ref reader); break;
                    case "unknownTokens":
                        if (reader.TokenType == JsonTokenType.StartArray)
                        {
                            unknownTokens ??= new List<string>();
                            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                            {
                                if (reader.TokenType == JsonTokenType.String && unknownTokens.Count < MaxUnknownTokens)
                                    unknownTokens.Add(Trim(reader.GetString()));
                            }
                        }
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            // ★ D6.1 §5 ★ 必填 envelope 校验：按**字段是否出现**判定（旧实现静默补默认值 ⇒ `{}` 也能"解析成功"）。
            //   注意：判定"存在性"，不判定"值是否好看"——例如 monotonicTimestamp=0 是合法值，
            //   不能因为值不寻常就当成缺字段（第一版就犯了这个错，被既有存储用例拦下）。
            if (string.IsNullOrEmpty(schemaVersion))
            {
                errorCode = "missing-required-field:schemaVersion";
                return false;
            }
            if (!string.Equals(schemaVersion, DiagnosticEvent.CurrentSchemaVersion, StringComparison.Ordinal))
            {
                errorCode = "unsupported-schema-version:" + schemaVersion;
                return false;
            }
            if (!hasEventId)
            {
                errorCode = "missing-required-field:eventId";
                return false;
            }
            if (!hasSessionId)
            {
                errorCode = "missing-required-field:sessionId";
                return false;
            }
            if (!hasSequence)
            {
                errorCode = "missing-required-field:sequence";
                return false;
            }
            if (!hasTimestamp)
            {
                errorCode = "missing-required-field:timestampUtc";
                return false;
            }
            if (!hasMonotonic)
            {
                errorCode = "missing-required-field:monotonicTimestamp";
                return false;
            }
            if (!hasLevel || !hasDelivery || !hasEvidenceQuality || !hasCaptureMode)
            {
                errorCode = "missing-required-field:level/delivery/evidenceQuality/captureMode";
                return false;
            }

            // catalog 匹配：查不到就保留原始 id/name（IsKnown=false），绝不伪造归属。
            var descriptor = EventCatalog.TryGetByEventId(eventId, out var known)
                ? known
                : EventDescriptor.Unknown(eventId, eventCode, eventName);

            // ★ D6.1 §5 ★ 版本纪律：能保存未知，**不能**用当前契约解释未知。
            //   · 行里没有 eventVersion（旧数据/手写数据）⇒ 无法核对 ⇒ 视为不受支持；
            //   · 已知事件但版本不匹配 ⇒ UnsupportedVersion（绝不静默归一成当前版本）；
            //   · 未知事件（IsKnown=false）⇒ 本来就不可解释，同样不解码 payload。
            var versionMismatch = descriptor.IsKnown && hasEventVersion && eventVersion != descriptor.Version;
            var versionUnsupported = declaredUnsupported || !hasEventVersion || versionMismatch;
            if (versionMismatch)
                AddUnknownToken(ref unknownTokens, "eventVersion:" + eventVersion);
            else if (!hasEventVersion)
                AddUnknownToken(ref unknownTokens, "eventVersion:missing");

            // category 与 catalog 不一致 = 可疑（catalog 变更或写入方串号）⇒ 记 raw token。
            if (category is not null && category.Value != descriptor.Category && descriptor.IsKnown)
                AddUnknownToken(ref unknownTokens, "category:" + category.Value);

            IDiagnosticPayload? payload = null;
            var effectivePayloadName = payloadName ?? descriptor.PayloadName;
            // ★ R-5 收口（D6.3 剩余风险关闭轮）★ 解不开 typed payload 时**必须原样保住 payload 体**：
            //   "可以保存但不能用当前契约解释" —— 旧实现只记一个 token 就把它扔掉，
            //   于是未来版本/未知事件写了什么，在重新序列化（尤其导出）后永久消失。
            JsonElement? rawPayload = null;
            string? rawPayloadName = null;
            // 只有"已知且版本受支持"的事件才解码 typed payload：
            // 未知事件/未来版本不得冒充成当前 typed event 交给规则。
            if (payloadElement is not null && effectivePayloadName is not null
                && descriptor.IsKnown && !versionUnsupported)
            {
                if (!DiagnosticPayloadCodec.TryRead(effectivePayloadName, payloadElement.Value, out payload))
                    AddUnknownToken(ref unknownTokens, "payload:" + effectivePayloadName);
            }
            else if (payloadElement is not null && (!descriptor.IsKnown || versionUnsupported))
            {
                AddUnknownToken(ref unknownTokens, versionUnsupported ? "payload:unsupported-version" : "payload:unknown-event");
                rawPayload = payloadElement.Value.Clone();
                rawPayloadName = payloadName;
            }

            evt = new DiagnosticEvent
            {
                SchemaVersion = schemaVersion ?? DiagnosticEvent.CurrentSchemaVersion,
                Descriptor = descriptor,
                SessionId = sessionId,
                Sequence = sequence,
                TimestampUtc = timestampUtc,
                MonotonicTimestamp = monotonicTimestamp,
                Level = level ?? descriptor.Level,
                Delivery = delivery ?? descriptor.Delivery,
                EvidenceQuality = evidenceQuality ?? EvidenceQuality.Unknown,
                CaptureMode = captureMode ?? CaptureMode.Off,
                Component = component,
                ControlId = controlId,
                ActionId = actionId,
                OperationId = operationId,
                ParentOperationId = parentOperationId,
                JobId = jobId,
                ObjectId = objectId,
                RunGeneration = runGeneration,
                Attempt = attempt,
                Pass = pass,
                Outcome = outcome,
                ObservationVersion = observationVersion,
                ProjectionVersion = projectionVersion,
                StateOwner = stateOwner,
                Phase = phase,
                TraceId = traceId,
                SpanId = spanId,
                ParentSpanId = parentSpanId,
                Causation = causation,
                CorrelationId = correlationId,
                ErrorDomain = errorDomain ?? ErrorDomain.None,
                ExceptionType = exceptionType,
                HResult = hresult,
                Win32Error = win32Error,
                SocketError = socketError,
                RobocopyExitCode = robocopyExitCode,
                DurationMs = durationMs,
                Message = message,
                Path = path,
                Truncated = truncated,
                LossEpoch = lossEpoch,
                ContractVersion = contractVersion,
                Payload = payload,
                UnknownTokens = unknownTokens,
                DeclaredEventVersion = hasEventVersion ? eventVersion : null,
                VersionUnsupported = versionUnsupported,
                RawPayload = rawPayload,
                RawPayloadName = rawPayloadName,
            };
            return true;
        }
        catch (Exception ex)
        {
            evt = null;
            errorCode = "parse:" + ex.GetType().Name;
            return false;
        }
    }

    // ────────────────────────── 读取小工具（全部不抛） ──────────────────────────

    private static string? ReadString(ref Utf8JsonReader reader) =>
        reader.TokenType == JsonTokenType.String ? reader.GetString() : null;

    private static Guid? ReadGuid(ref Utf8JsonReader reader) =>
        Guid.TryParse(ReadString(ref reader), out var v) ? v : null;

    private static int? ReadIntNullable(ref Utf8JsonReader reader) =>
        reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var v) ? v : null;

    private static long? ReadLongNullable(ref Utf8JsonReader reader) =>
        reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var v) ? v : null;

    private static DateTimeOffset ParseTime(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var v) ? v : default;

    private static TEnum? ReadEnumNullable<TEnum>(ref Utf8JsonReader reader, ref List<string>? unknownTokens)
        where TEnum : struct, Enum
    {
        if (reader.TokenType != JsonTokenType.String) return null;
        var text = reader.GetString();
        if (string.IsNullOrEmpty(text)) return null;
        if (Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsed)) return parsed;
        AddUnknownToken(ref unknownTokens, typeof(TEnum).Name + ":" + text);
        return null;
    }

    private static TEnum ReadEnum<TEnum>(ref Utf8JsonReader reader, TEnum fallback, ref List<string>? unknownTokens)
        where TEnum : struct, Enum
    {
        if (reader.TokenType != JsonTokenType.String) return fallback;
        var text = reader.GetString();
        if (string.IsNullOrEmpty(text)) return fallback;
        if (Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsed)) return parsed;
        AddUnknownToken(ref unknownTokens, typeof(TEnum).Name + ":" + text);
        return fallback;
    }

    private static EventRef? ReadCausation(ref Utf8JsonReader reader)
    {
        var element = ReadClone(ref reader);
        if (element is null) return null;
        var session = PayloadJson.GuidOrEmpty(element.Value, "sessionId");
        var sequence = PayloadJson.LongOr(element.Value, "sequence", 0);
        return new EventRef(session, sequence);
    }

    private static PathRef? ReadPath(ref Utf8JsonReader reader)
    {
        var element = ReadClone(ref reader);
        if (element is null) return null;
        var role = PayloadJson.EnumOr(element.Value, "role", PathRole.Unknown);
        return new PathRef
        {
            Role = role,
            RootKind = PayloadJson.StrOr(element.Value, "rootKind", "Unknown"),
            RootAlias = PayloadJson.Str(element.Value, "rootAlias"),
            PathToken = PayloadJson.StrOr(element.Value, "pathToken", "[unavailable]"),
            KeyId = PayloadJson.Str(element.Value, "keyId"),
            Scope = PayloadJson.Str(element.Value, "scope"),
            ExtensionClass = PayloadJson.Str(element.Value, "extensionClass"),
            DepthBucket = PayloadJson.Int(element.Value, "depthBucket"),
        };
    }

    /// <summary>读一个完整值并 Clone 出来（Clone 后与 JsonDocument 生命周期解耦，可安全 dispose）。</summary>
    private static JsonElement? ReadClone(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray)) return null;
        using var doc = JsonDocument.ParseValue(ref reader);
        return doc.RootElement.Clone();
    }

    private static void AddUnknownToken(ref List<string>? tokens, string token)
    {
        tokens ??= new List<string>();
        if (tokens.Count >= MaxUnknownTokens) return;
        tokens.Add(Trim(token));
    }

    private static string Trim(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= MaxUnknownTokenLength ? text : text.Substring(0, MaxUnknownTokenLength);
    }
}