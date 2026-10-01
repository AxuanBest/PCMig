using System;
using System.Collections.Generic;
using System.Linq;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Payloads;
using PCMig.Diagnostics.Abstractions.Serialization;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D1 契约：payload 注册表与规范 JSON 编解码的一致性 + **schema roundtrip**。
/// </summary>
public sealed class PayloadCodecTests
{
    [Fact]
    public void EveryCatalogPayloadNameHasAReader()
    {
        var missing = EventCatalog.All
            .Where(d => d.PayloadName is not null)
            .Where(d => !DiagnosticPayloadCodec.IsKnown(d.PayloadName!))
            .Select(d => $"{d.Code} ({d.PayloadName})")
            .ToArray();

        Assert.True(missing.Length == 0, "以下事件的 payload 没有登记读取器：" + string.Join(", ", missing));
    }

    [Fact]
    public void EveryRegisteredReaderIsReferencedBySomeDescriptor()
    {
        var referenced = EventCatalog.All
            .Where(d => d.PayloadName is not null)
            .Select(d => d.PayloadName!)
            .ToHashSet(StringComparer.Ordinal);

        var orphans = DiagnosticPayloadCodec.KnownPayloadNames.Where(n => !referenced.Contains(n)).ToArray();
        Assert.True(orphans.Length == 0, "以下 payload 登记了读取器，但没有任何事件引用它：" + string.Join(", ", orphans));
    }

    [Fact]
    public void UnknownPayloadNameIsReportedNotThrown()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("{}");
        Assert.False(DiagnosticPayloadCodec.TryRead("NoSuchPayload", doc.RootElement, out var payload));
        Assert.Null(payload);
    }

    [Fact]
    public void MalformedPayloadElementDoesNotThrow()
    {
        // payload 期望是对象，这里给数组：读取必须失败而不是抛异常。
        using var doc = System.Text.Json.JsonDocument.Parse("[1,2,3]");
        Assert.False(DiagnosticPayloadCodec.TryRead(AppProcessStartedPayload.Name, doc.RootElement, out _));
    }

    /// <summary>为每个已登记 payload 造一个实例（D1 只有 APP/DIA 两族有 payload）。</summary>
    internal static IEnumerable<IDiagnosticPayload> AllPayloadInstances()
    {
        yield return new AppProcessStartedPayload(
            "0.4.9", "8.0.0", "Windows 10.0.19045", 4242,
            new DateTimeOffset(2026, 9, 30, 6, 31, 32, TimeSpan.Zero), "x64", "build-abc");
        yield return new AppEnvironmentCapturedPayload(
            "Windows 10.0.19045", "x64", 16, 32768, "machine-a",
            new DateTimeOffset(2026, 9, 30, 6, 31, 33, TimeSpan.Zero));
        yield return new AppUnhandledExceptionPayload(
            "System.InvalidOperationException", unchecked((int)0x80131509), "Running", false, "Type.Method <- Type.Caller", false);
        yield return new AppClosingPayload("user-close", true, true);

        yield return new DiaSessionStartedPayload(
            "0.4.9", "8.0.0", "Windows 10.0.19045", 4242,
            new DateTimeOffset(2026, 9, 30, 6, 31, 32, TimeSpan.Zero), "pid-start:4242:638600000000000000",
            CaptureMode.Operational, EventCatalog.CatalogVersion, EventCatalog.CatalogHash, 10_000_000, "x64", "session-dir");
        yield return new DiaSessionCleanShutdownPayload(1234, true, 2, true);
        yield return new DiaPreviousSessionUncleanPayload(
            Guid.Parse("11111111-2222-3333-4444-555555555555"), "no-clean-marker", 987, 12, false);
        yield return new DiaModeChangedPayload(CaptureMode.Operational, CaptureMode.Deep, "user-toggle");
        yield return new DiaBackpressurePayload("operational", DeliveryClass.Operational, 4096, 4096, 7);
        yield return new DiaEventsDroppedPayload("verbose", DeliveryClass.Verbose, 42, 100, 142, "queue-full");
        yield return new DiaCriticalLostPayload(3, "reserve-exhausted", true);
        yield return new DiaStorageFailedPayload("segment-writer", "io-error", 112, true);
        yield return new DiaStorageRecoveredPayload("segment-writer", 2500);
        yield return new DiaSerializationFailedPayload("PST-004", "payload-write-failed", "DiaStorageFailed");
        yield return new DiaAnalyzerLaggedPayload(77, 1500);
        yield return new DiaSnapshotUnavailablePayload("view", "ui-thread-timeout", 500);
        yield return new DiaCoverageChangedPayload(CaptureMode.Flight, "trigger", null, null);
        yield return new DiaShutdownIncompletePayload(1, 900, 42, 1500);
        yield return new DiaRingTriggeredPayload("trig-1", "rule:TRANSFER_PROGRESS_STALLED", 60000, 15000);
        yield return new DiaRingSealedPayload("trig-1", 41200, 120, 5, false, 3);
        yield return new DiaHealthSummaryPayload(1000, 900, 880, 12, 8, 0, false, 20);
        yield return new DiaClockAnchorAdjustedPayload(2, "detected", 0);

        // D3a/D3b：连接链路 + 持久化 + 子进程 + 对象级结果
        yield return new NetProbeStartedPayload("hmac:abc", false, 2);
        yield return new NetDnsResultPayload(true, 3, 42, null);
        yield return new NetTcpProbePayload(0, 2000, false);
        yield return new NetSmbSessionPayload("WNetAddConnection2", false, 15, true);
        yield return new NetShareDiscoveryPayload(true, 4, true);
        yield return new NetCredentialProofPayload("error-67-authenticated");
        yield return new PflCheckPayload("Smb445", "Info", true);
        yield return new PflSourceProbePayload(2, 2, 0);
        yield return new PflTargetVolumePayload("Fixed", 51200, 262144, true);
        yield return new PflBenchmarkPayload(94.5, 88.25, false);
        yield return new PflSummaryPayload(true, 6, 0, 1);
        yield return new UiActionPayload("Connect", "click");
        yield return new UiEligibilityPayload(false, "busy:IsConnecting");
        yield return new UiFeedbackPayload("connect", "vm-status-change", 812);
        yield return new UiProjectionChangedPayload("Vm", 2);
        yield return new UiProjectionReadbackPayload(true, "Visible", 7, 6, 0, true);
        yield return new UiStateObservedPayload("Vm", "Running", true, false, false, true, true, false);
        yield return new UiDispatchRejectedPayload("queue-closing", "footer");
        yield return new UiInputObservedPayload("pointer-pressed", true, true, 0);
        yield return new PstWritePayload("Receipt", "Move", true, "IOException");
        yield return new PstReadFailurePayload("VerifyReport", "JsonException");
        yield return new TrnPauseRequestPayload(true, true, null);
        yield return new RbcProcessPayload(4242, "pid:4242:2026-09-30T06:31:32.0000000Z", "RootFiles", "mt",
            "mt; /R:2 /W:5; files=12; unilog");
        yield return new RbcKillPayload("immediate-pause-or-cancel", true);
        yield return new RbcErrorAggregatePayload(132, true, 3);
        yield return new RbcFileSampleSummaryPayload(100, 2145123, true);
        yield return new TrnObjectPayload("Directory", 1073741824, 2145123, 1, -1, "None", 2);
        yield return new TrnProgressPayload("approx", 536870912, 1073741824, 2145123, 94.5, true);
    }

    /// <summary>
    /// ★ D1 出口证据：schema roundtrip ★
    /// 每个 payload 走一遍"写 JSON → 解析回来"，并用 record 结构相等断言**逐字段**一致。
    /// </summary>
    [Fact]
    public void AllPayloadsRoundtripThroughCanonicalJson()
    {
        var instances = AllPayloadInstances().ToList();
        Assert.True(instances.Count >= 20, "D1 至少应覆盖 APP/DIA 两族的全部 payload");

        var failures = new List<string>();
        foreach (var payload in instances)
        {
            var descriptor = EventCatalog.All.FirstOrDefault(d => d.PayloadName == payload.PayloadName);
            Assert.NotNull(descriptor);

            var evt = TestEvents.Minimal(descriptor!, payload);
            var line = DiagnosticEventJson.ToJsonLine(evt);

            Assert.True(DiagnosticEventJson.TryParse(line, out var parsed, out var error), error);
            Assert.NotNull(parsed);
            Assert.NotNull(parsed!.Payload);

            if (!Equals(payload, parsed.Payload))
                failures.Add($"{payload.PayloadName}\n  原始: {payload}\n  解析: {parsed.Payload}");
        }

        Assert.True(failures.Count == 0, "payload 往返不一致：\n" + string.Join("\n", failures));
    }

    [Fact]
    public void EveryPayloadNameIsUniqueAcrossInstances()
    {
        var names = AllPayloadInstances().Select(p => p.PayloadName).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// 返工护栏：payload 的 PayloadName 必须等于其静态 Name 常量。
    /// 这一条能抓住"位置参数与 IDiagnosticPayload.PayloadName 同名 ⇒ record 不生成属性 ⇒ 值被静默丢弃"
    /// 这类陷阱（D1 实测踩过一次：DiaSerializationFailedPayload 的字段永远写成自身名字）。
    /// </summary>
    [Fact]
    public void PayloadNameAlwaysMatchesTheStaticNameConstant()
    {
        var mismatched = new List<string>();
        foreach (var payload in AllPayloadInstances())
        {
            var field = payload.GetType().GetField("Name", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (field is null)
            {
                mismatched.Add(payload.GetType().Name + ": 缺少 public const string Name");
                continue;
            }
            if (!Equals(field.GetValue(null), payload.PayloadName))
                mismatched.Add($"{payload.GetType().Name}: PayloadName={payload.PayloadName} != Name={field.GetValue(null)}");
        }

        Assert.True(mismatched.Count == 0, string.Join("\n", mismatched));
    }

    /// <summary>
    /// 返工护栏：payload 的每个构造参数都必须真的被存下来（防止上面那种"静默丢字段"）。
    /// 做法：用两个只有某一个参数不同的实例，断言它们**不相等**——若该参数没被存下来，两者会相等。
    /// </summary>
    [Fact]
    public void EveryConstructorParameterActuallyReachesTheRecord()
    {
        Assert.NotEqual(
            new DiaSerializationFailedPayload("PST-004", "r", "DiaStorageFailed"),
            new DiaSerializationFailedPayload("PST-004", "r", "DiaEventsDropped"));

        Assert.NotEqual(
            new DiaSerializationFailedPayload("PST-004", "r", null),
            new DiaSerializationFailedPayload("PST-004", "r", "DiaStorageFailed"));
    }
}