using System;
using PCMig.Diagnostics.Abstractions;

namespace PCMig.Diagnostics.Tests;

/// <summary>测试用的事件构造小工具（不进入产品程序集）。</summary>
internal static class TestEvents
{
    public static readonly Guid FixedSessionId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    public static DiagnosticEvent Minimal(EventDescriptor descriptor, IDiagnosticPayload? payload = null) => new()
    {
        SchemaVersion = DiagnosticEvent.CurrentSchemaVersion,
        Descriptor = descriptor,
        SessionId = FixedSessionId,
        Sequence = 1,
        TimestampUtc = new DateTimeOffset(2026, 9, 30, 6, 31, 32, 123, TimeSpan.Zero),
        MonotonicTimestamp = 123456789,
        Level = descriptor.Level,
        Delivery = descriptor.Delivery,
        EvidenceQuality = EvidenceQuality.Direct,
        CaptureMode = CaptureMode.Operational,
        Payload = payload,
    };

    public static DiagnosticEvent WithAllOptionalFields(EventDescriptor descriptor, IDiagnosticPayload? payload = null) => new()
    {
        SchemaVersion = DiagnosticEvent.CurrentSchemaVersion,
        Descriptor = descriptor,
        SessionId = FixedSessionId,
        Sequence = 42,
        TimestampUtc = new DateTimeOffset(2026, 9, 30, 6, 31, 32, TimeSpan.Zero),
        MonotonicTimestamp = 987654321,
        Level = DiagnosticLevel.Error,
        Delivery = DeliveryClass.DurableCritical,
        EvidenceQuality = EvidenceQuality.Derived,
        CaptureMode = CaptureMode.Flight,
        Component = "JsonStateStore",
        ActionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        OperationId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        ParentOperationId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        JobId = "JOB-20260930-121847-49fc",
        ObjectId = "obj-000007",
        RunGeneration = 3,
        Attempt = 2,
        Pass = DiagnosticPass.Large,
        Outcome = DiagnosticOutcome.Failed,
        ObservationVersion = 77,
        ProjectionVersion = 66,
        StateOwner = StateOwner.Core,
        Phase = "Running",
        TraceId = "0af7651916cd43dd8448eb211c80319c",
        SpanId = "b7ad6b7169203331",
        ParentSpanId = "a3ce929d0e0e4736",
        Causation = new EventRef(FixedSessionId, 41),
        CorrelationId = "scenario-1",
        ErrorDomain = ErrorDomain.Win32,
        ExceptionType = "System.IO.IOException",
        HResult = unchecked((int)0x80070070),
        Win32Error = 112,
        SocketError = 10060,
        RobocopyExitCode = 8,
        DurationMs = 1234,
        Message = "presentation text (not a rule input)",
        Path = new PathRef
        {
            Role = PathRole.Target,
            RootKind = "Drive",
            RootAlias = "dst",
            PathToken = "hmac:deadbeef",
            KeyId = "k1",
            Scope = "session",
            ExtensionClass = "document",
            DepthBucket = 3,
        },
        Truncated = true,
        LossEpoch = 5,
        ContractVersion = 2,
        Payload = payload,
    };
}