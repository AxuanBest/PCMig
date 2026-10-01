using System;
using System.Linq;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using PCMig.Diagnostics.Abstractions.Serialization;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>D1 契约：规范 JSON 编解码的字段完整性与容错口径。</summary>
public sealed class DiagnosticEventJsonTests
{
    [Fact]
    public void EnvelopeWithEveryOptionalFieldRoundtripsExactly()
    {
        var original = TestEvents.WithAllOptionalFields(PersistenceEvents.WriteFailed);
        var line = DiagnosticEventJson.ToJsonLine(original);

        Assert.True(DiagnosticEventJson.TryParse(line, out var parsed, out var error), error);
        Assert.NotNull(parsed);

        // 逐组断言（比 Assert.Equal(record) 更容易定位差异）。
        Assert.Equal(original.SchemaVersion, parsed!.SchemaVersion);
        Assert.Equal(original.SessionId, parsed.SessionId);
        Assert.Equal(original.Sequence, parsed.Sequence);
        Assert.Equal(original.TimestampUtc, parsed.TimestampUtc);
        Assert.Equal(original.MonotonicTimestamp, parsed.MonotonicTimestamp);
        Assert.Equal(original.Descriptor.EventId, parsed.Descriptor.EventId);
        Assert.Equal(original.Descriptor.Code, parsed.Descriptor.Code);
        Assert.Equal(original.Descriptor.Name, parsed.Descriptor.Name);
        Assert.Equal(original.Descriptor.Version, parsed.Descriptor.Version);
        Assert.Equal(original.Level, parsed.Level);
        Assert.Equal(original.Delivery, parsed.Delivery);
        Assert.Equal(original.EvidenceQuality, parsed.EvidenceQuality);
        Assert.Equal(original.CaptureMode, parsed.CaptureMode);
        Assert.Equal(original.Component, parsed.Component);
        Assert.Equal(original.ActionId, parsed.ActionId);
        Assert.Equal(original.OperationId, parsed.OperationId);
        Assert.Equal(original.ParentOperationId, parsed.ParentOperationId);
        Assert.Equal(original.JobId, parsed.JobId);
        Assert.Equal(original.ObjectId, parsed.ObjectId);
        Assert.Equal(original.RunGeneration, parsed.RunGeneration);
        Assert.Equal(original.Attempt, parsed.Attempt);
        Assert.Equal(original.Pass, parsed.Pass);
        Assert.Equal(original.Outcome, parsed.Outcome);
        Assert.Equal(original.ObservationVersion, parsed.ObservationVersion);
        Assert.Equal(original.ProjectionVersion, parsed.ProjectionVersion);
        Assert.Equal(original.StateOwner, parsed.StateOwner);
        Assert.Equal(original.Phase, parsed.Phase);
        Assert.Equal(original.TraceId, parsed.TraceId);
        Assert.Equal(original.SpanId, parsed.SpanId);
        Assert.Equal(original.ParentSpanId, parsed.ParentSpanId);
        Assert.Equal(original.Causation, parsed.Causation);
        Assert.Equal(original.CorrelationId, parsed.CorrelationId);
        Assert.Equal(original.ErrorDomain, parsed.ErrorDomain);
        Assert.Equal(original.ExceptionType, parsed.ExceptionType);
        Assert.Equal(original.HResult, parsed.HResult);
        Assert.Equal(original.Win32Error, parsed.Win32Error);
        Assert.Equal(original.SocketError, parsed.SocketError);
        Assert.Equal(original.RobocopyExitCode, parsed.RobocopyExitCode);
        Assert.Equal(original.DurationMs, parsed.DurationMs);
        Assert.Equal(original.Message, parsed.Message);
        Assert.Equal(original.Truncated, parsed.Truncated);
        Assert.Equal(original.LossEpoch, parsed.LossEpoch);
        Assert.Equal(original.ContractVersion, parsed.ContractVersion);
        Assert.Equal(original.Path, parsed.Path);
        Assert.Equal(original.Ref, parsed.Ref);
        Assert.Null(parsed.UnknownTokens);
    }

    [Fact]
    public void MinimalEventRoundtripsWithNullsAbsent()
    {
        var original = TestEvents.Minimal(UiEvents.UserActionObserved);
        var line = DiagnosticEventJson.ToJsonLine(original);

        // 未设置的字段不应写进 JSONL（控制行长度）。
        Assert.DoesNotContain("\"jobId\"", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\"payload\"", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\"truncated\"", line, StringComparison.Ordinal);
        Assert.DoesNotContain("null", line, StringComparison.Ordinal);

        Assert.True(DiagnosticEventJson.TryParse(line, out var parsed, out var error), error);
        Assert.Equal(original.SessionId, parsed!.SessionId);
        Assert.Equal(original.Descriptor.EventId, parsed.Descriptor.EventId);
        Assert.Null(parsed.JobId);
        Assert.Null(parsed.Payload);
        Assert.False(parsed.Truncated);
    }

    [Fact]
    public void JsonLineIsSingleLine()
    {
        var line = DiagnosticEventJson.ToJsonLine(TestEvents.WithAllOptionalFields(PersistenceEvents.WriteFailed));
        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", line, StringComparison.Ordinal);
        Assert.StartsWith("{", line, StringComparison.Ordinal);
        Assert.EndsWith("}", line, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownEventIdIsPreservedAndMarkedUnknown()
    {
        const string line = """
        {"schemaVersion":"1.0","sessionId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","sequence":9,
         "timestampUtc":"2026-09-30T06:31:32.0000000+00:00","monotonicTimestamp":5,
         "eventId":777777,"eventCode":"ZZ-777","eventName":"ZZ.FutureThing","eventVersion":1,
         "level":"Warning","deliveryClass":"Operational","evidenceQuality":"Direct","captureMode":"Operational"}
        """;

        Assert.True(DiagnosticEventJson.TryParse(line, out var parsed, out var error), error);
        Assert.False(parsed!.Descriptor.IsKnown);
        Assert.Equal(777777, parsed.Descriptor.EventId);
        Assert.Equal("ZZ-777", parsed.Descriptor.Code);
        Assert.Equal("ZZ.FutureThing", parsed.Descriptor.Name);
        // 未知事件仍带着它自己的 level（这一条是写入方声明的，不是 catalog 猜的）。
        Assert.Equal(DiagnosticLevel.Warning, parsed.Level);
    }

    [Fact]
    public void UnknownEnumTokenIsKeptRawAndNotCoerced()
    {
        const string line = """
        {"schemaVersion":"1.0","sessionId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","sequence":9,
         "timestampUtc":"2026-09-30T06:31:32.0000000+00:00","monotonicTimestamp":5,
         "eventId":9004,"eventVersion":1,"level":"Catastrophic","deliveryClass":"Operational","outcome":"Exploded",
         "evidenceQuality":"Direct","captureMode":"Operational"}
        """;

        Assert.True(DiagnosticEventJson.TryParse(line, out var parsed, out var error), error);
        Assert.NotNull(parsed!.UnknownTokens);
        Assert.Contains(parsed.UnknownTokens!, t => t.Contains("Catastrophic", StringComparison.Ordinal));
        Assert.Contains(parsed.UnknownTokens!, t => t.Contains("Exploded", StringComparison.Ordinal));

        // 未识别的 level 不得被强转成 Critical/Error 这类有语义的值：回落到 catalog 声明的级别。
        Assert.Equal(PersistenceEvents.WriteFailed.Level, parsed.Level);
        Assert.Null(parsed.Outcome);
        Assert.True(parsed.UnknownTokens!.Count <= 8);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"schemaVersion\":\"1.0\",")]
    public void CorruptInputFailsWithoutThrowing(string line)
    {
        Assert.False(DiagnosticEventJson.TryParse(line, out var parsed, out var error));
        Assert.Null(parsed);
        Assert.NotNull(error);
    }

    [Fact]
    public void PayloadWithUnknownNameIsKeptAsTokenNotFabricated()
    {
        const string line = """
        {"schemaVersion":"1.0","sessionId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","sequence":9,
         "timestampUtc":"2026-09-30T06:31:32.0000000+00:00","monotonicTimestamp":5,
         "eventId":7002,"eventVersion":1,"level":"Information","deliveryClass":"Operational",
         "evidenceQuality":"Direct","captureMode":"Operational",
         "payloadName":"NotYetImplemented","payload":{"whatever":1}}
        """;

        Assert.True(DiagnosticEventJson.TryParse(line, out var parsed, out var error), error);
        Assert.Null(parsed!.Payload);
        Assert.NotNull(parsed.UnknownTokens);
        Assert.Contains(parsed.UnknownTokens!, t => t.Contains("NotYetImplemented", StringComparison.Ordinal));
    }

    [Fact]
    public void PayloadWithoutNameFallsBackToCatalogPayloadName()
    {
        var original = TestEvents.Minimal(DiagnosticsEvents.SessionStarted,
            new DiaSessionStartedPayload("0.4.9", "8.0.0", "Windows", 1, DateTimeOffset.UnixEpoch,
                "pid:1", CaptureMode.Operational, EventCatalog.CatalogVersion, EventCatalog.CatalogHash,
                10_000_000, "x64", "root"));

        var line = DiagnosticEventJson.ToJsonLine(original);
        var withoutName = line.Replace("\"payloadName\":\"DiaSessionStarted\",", string.Empty, StringComparison.Ordinal);
        Assert.NotEqual(line, withoutName);

        Assert.True(DiagnosticEventJson.TryParse(withoutName, out var parsed, out var error), error);
        Assert.IsType<DiaSessionStartedPayload>(parsed!.Payload);
    }

    [Fact]
    public void SameSessionWithDifferentSequenceProducesDifferentEvidenceRefs()
    {
        var a = TestEvents.Minimal(UiEvents.UserActionObserved);
        var b = a with { Sequence = 2 };
        Assert.NotEqual(a.Ref, b.Ref);
    }
}