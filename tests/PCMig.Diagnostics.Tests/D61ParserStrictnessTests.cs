using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using PCMig.Diagnostics.Abstractions.Serialization;
using PCMig.Diagnostics.Analysis;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D6.1 §5（Parser 严格性）收口测试。对应验收清单 §20.12 / §20.13：
///   · 未知 `eventVersion` ⇒ `Unsupported`，**不是** current；
///   · `{}` ⇒ 解析失败；
///   · 缺必填字段 ⇒ 失败；
///   · 未知 EventId 可保留，但**不得**冒充已知 typed 事件；
///   · 规则引擎遇 unsupported version ⇒ 不执行依赖 payload 的规则。
/// </summary>
public sealed class D61ParserStrictnessTests
{
    private static string ValidLine(Action<JsonObject>? mutate = null, int eventVersion = 1)
    {
        var evt = new DiagnosticEvent
        {
            SchemaVersion = DiagnosticEvent.CurrentSchemaVersion,
            Descriptor = PersistenceEvents.WriteFailed,
            SessionId = Guid.NewGuid(),
            Sequence = 7,
            TimestampUtc = DateTimeOffset.UtcNow,
            MonotonicTimestamp = 123456,
            Level = DiagnosticLevel.Error,
            Delivery = DeliveryClass.Operational,
            EvidenceQuality = EvidenceQuality.Direct,
            CaptureMode = CaptureMode.Operational,
            Payload = new PstWritePayload("Receipt", "Move", true, "IOException"),
        };

        var node = JsonNode.Parse(DiagnosticEventJson.ToJsonLine(evt))!.AsObject();
        node["eventVersion"] = eventVersion;
        mutate?.Invoke(node);
        return node.ToJsonString();
    }

    [Fact]
    public void EmptyObjectIsNotParsable()
    {
        Assert.False(DiagnosticEventJson.TryParse("{}", out var evt, out var error));
        Assert.Null(evt);
        Assert.Contains("missing-required-field", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("eventId")]
    [InlineData("sessionId")]
    [InlineData("sequence")]
    [InlineData("timestampUtc")]
    [InlineData("monotonicTimestamp")]
    [InlineData("level")]
    [InlineData("deliveryClass")]
    [InlineData("evidenceQuality")]
    [InlineData("captureMode")]
    public void MissingRequiredEnvelopeFieldFailsTheParse(string field)
    {
        var line = ValidLine(node => node.Remove(field));
        Assert.False(DiagnosticEventJson.TryParse(line, out var evt, out var error), field + " 缺失时不应解析成功");
        Assert.Null(evt);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    /// <summary>
    /// `eventVersion` 缺失**不判失败**：这是"旧数据/手写数据"，我们无法核对版本 ⇒
    /// 按**不受支持**处理（可读、可保存，但不用当前契约解释它）。
    /// 判据的分界：**字段在场但 token 不认识** ≠ 缺字段（前者保留 raw token 并回落）。
    /// </summary>
    [Fact]
    public void MissingEventVersionIsTreatedAsUnsupportedRatherThanFatal()
    {
        var line = ValidLine(node => node.Remove("eventVersion"));
        Assert.True(DiagnosticEventJson.TryParse(line, out var evt, out _));
        Assert.NotNull(evt);
        Assert.True(evt!.VersionUnsupported);
        Assert.Null(evt.DeclaredEventVersion);
        Assert.Contains(evt.UnknownTokens ?? new List<string>(), t => t.Contains("eventVersion:missing", StringComparison.Ordinal));
        Assert.Null(evt.Payload);
    }

    /// <summary>字段在场但 token 不认识 ⇒ **保留 raw token 并回落到 catalog 值**，不是"缺字段"。</summary>
    [Fact]
    public void PresentButUnknownEnumTokenIsNotTreatedAsMissingField()
    {
        var line = ValidLine(node =>
        {
            node["level"] = "Catastrophic";
            node["outcome"] = "Exploded";
        });

        Assert.True(DiagnosticEventJson.TryParse(line, out var evt, out var error), error);
        Assert.NotNull(evt);
        Assert.Equal(PersistenceEvents.WriteFailed.Level, evt!.Level);
        Assert.Null(evt.Outcome);
        Assert.Contains(evt.UnknownTokens ?? new List<string>(), t => t.Contains("Catastrophic", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsupportedSchemaVersionFailsInsteadOfBeingAssumed()
    {
        var line = ValidLine(node => node["schemaVersion"] = "9.9");
        Assert.False(DiagnosticEventJson.TryParse(line, out _, out var error));
        Assert.Contains("unsupported-schema-version", error, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownEventVersionIsReportedAndNeverNormalizedToCurrent()
    {
        var line = ValidLine(eventVersion: 999);
        Assert.True(DiagnosticEventJson.TryParse(line, out var evt, out _));
        Assert.NotNull(evt);

        // ★ 原始版本必须被保留，且**不能**被当成当前契约版本 ★
        Assert.Equal(999, evt!.DeclaredEventVersion);
        Assert.True(evt.VersionUnsupported);
        Assert.Equal(PersistenceEvents.WriteFailed.Version, evt.Descriptor.Version);   // descriptor 本身未被改写
        Assert.NotEqual(999, evt.Descriptor.Version);

        // 未来版本不得被解码成 typed payload（否则规则会拿"当前契约"去解释未来数据）。
        Assert.Null(evt.Payload);
        Assert.Contains(evt.UnknownTokens ?? new List<string>(), t => t.Contains("unsupported-version", StringComparison.Ordinal));
    }

    [Fact]
    public void CurrentVersionStillRoundTripsWithTypedPayload()
    {
        var line = ValidLine();
        Assert.True(DiagnosticEventJson.TryParse(line, out var evt, out _));
        Assert.NotNull(evt);
        Assert.False(evt!.VersionUnsupported);
        Assert.IsType<PstWritePayload>(evt.Payload);
        Assert.Equal(PersistenceEvents.WriteFailed, evt.Descriptor);
    }

    [Fact]
    public void UnknownEventIdIsKeptButNeverPretendsToBeATypedEvent()
    {
        var line = ValidLine(node => node["eventId"] = 999999);
        Assert.True(DiagnosticEventJson.TryParse(line, out var evt, out _));
        Assert.NotNull(evt);

        Assert.False(evt!.Descriptor.IsKnown);
        Assert.Equal(999999, evt.Descriptor.EventId);
        // 原始 envelope 仍在（不丢数据）…
        Assert.Equal(7, evt.Sequence);
        Assert.NotEqual(Guid.Empty, evt.SessionId);
        // …但**不得**冒充 typed 事件：payload 不解码，且留痕说明原因。
        Assert.Null(evt.Payload);
        Assert.Contains(evt.UnknownTokens ?? new List<string>(), t => t.Contains("unknown-event", StringComparison.Ordinal));
    }

    [Fact]
    public void RuleEngineSkipsEventsWithUnsupportedVersionAndCountsThem()
    {
        var engine = new RuleEngine(RuleRegistry.CreateDefault(), _ => null);
        var context = engine.CreateContext(new EvidenceCoverage(new LossLedger()), 100);

        var line = ValidLine(eventVersion: 999);
        Assert.True(DiagnosticEventJson.TryParse(line, out var evt, out _));

        engine.Evaluate(evt!, context);

        Assert.Empty(engine.ActiveIncidents);
        Assert.True(engine.Stats().UnsupportedVersionSkipped >= 1);

        // 对照：同一条事件在**受支持版本**下会开卡（证明上面"没开卡"是版本判定导致的，不是规则本身不工作）。
        Assert.True(DiagnosticEventJson.TryParse(ValidLine(), out var supported, out _));
        engine.Evaluate(supported!, context);
        Assert.Contains(engine.ActiveIncidents, i => i.RuleId == "PERSISTENCE_WRITE_FAILED");
    }
}