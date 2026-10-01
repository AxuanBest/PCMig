using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using PCMig.Core.Diagnostics;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Planning;
using PCMig.Diagnostics;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using PCMig.Diagnostics.Abstractions.Serialization;
using Serilog;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D6.1 §16（Core 插桩覆盖：**Planner** 与 **NetworkShare**）收口测试。
///
/// 两个重点：
///   ① Planner：真的产出 PLN 证据；并且 **装与不装诊断，产出的计划逐字节一致**（Observer 边界）；
///   ② NetworkShare：会话/枚举载荷**只记凭据语义**，绝不出现用户名/口令（类型 + 源码双重断言）。
/// </summary>
[Collection(DiagnosticsAmbientCollection.Name)]
public sealed class D61PlannerAndNetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-pln-" + Guid.NewGuid().ToString("N")[..8]);

    public D61PlannerAndNetTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* 句柄延迟释放 */ }
    }

    private static readonly ILogger Silent = new LoggerConfiguration().CreateLogger();

    private static JobDefinition Job() => new()
    {
        JobId = "JOB-PLN-1",
        SourceHost = "synthetic-host",
        TargetRoot = @"C:\target",
        Sources = new List<SourceSpec> { new() { Path = @"\\host\d$", Kind = ObjectKind.DataVolume } },
        CreatedBy = "test",
    };

    private static ObservedState ObservedWith(params (string Path, long Bytes, long Files)[] objects)
    {
        var state = new ObservedState { JobId = "JOB-PLN-1" };
        var i = 0;
        foreach (var (path, bytes, files) in objects)
        {
            state.Objects.Add(new ScannedObject
            {
                ObjectId = $"object-{++i:000000}",
                Kind = ObjectKind.DataVolume,
                SourcePath = path,
                Bytes = bytes,
                Files = files,
            });
        }
        return state;
    }

    // ────────────────────────── ① Planner ──────────────────────────

    [Fact]
    public void PlannerEmitsRequestedAndCreatedEvidence()
    {
        var runtime = DiagnosticRuntime.Start(new DiagnosticRuntimeOptions
        {
            StorageRoot = Path.Combine(_root, "diag"),
            InitialMode = CaptureMode.Operational,
            AppVersion = "d61-test",
            ShutdownBudgetMs = 5000,
        }, out _);

        using (CoreDiagnostics.Install(runtime))
        {
            var plan = new Planner(Silent).CreatePlan(
                Job(),
                ObservedWith((@"\\host\d$\data", 5_000_000, 12), (@"\\host\d$\photos", 9_000_000, 30)),
                MigrationMatrix.Load(null, Silent));

            Assert.Equal(2, plan.Objects.Count);
        }

        D2TestSupport.Shutdown(runtime);
        var payloads = ReadPayloads(runtime);

        var requested = Assert.Single(payloads.OfType<PlnPlanRequestedPayload>());
        Assert.Equal(1, requested.Sources);
        Assert.Equal(2, requested.ObservedObjects);

        var created = Assert.Single(payloads.OfType<PlnPlanCreatedPayload>());
        Assert.Equal(2, created.Objects);
        Assert.Equal(14_000_000, created.TotalBytes);
        Assert.Equal(9_000_000, created.LargestObjectBytes);

        D2TestSupport.Dispose(runtime);
    }

    [Fact]
    public void EmptyObservedStateProducesAnExplicitEmptyPlanFact()
    {
        var runtime = DiagnosticRuntime.Start(new DiagnosticRuntimeOptions
        {
            StorageRoot = Path.Combine(_root, "diag-empty"),
            InitialMode = CaptureMode.Operational,
            AppVersion = "d61-test",
            ShutdownBudgetMs = 5000,
        }, out _);

        using (CoreDiagnostics.Install(runtime))
        {
            var plan = new Planner(Silent).CreatePlan(Job(), new ObservedState { JobId = "JOB-PLN-1" }, MigrationMatrix.Load(null, Silent));
            Assert.Empty(plan.Objects);
        }

        D2TestSupport.Shutdown(runtime);
        var empty = Assert.Single(ReadPayloads(runtime).OfType<PlnPlanEmptyPayload>());
        Assert.Equal(0, empty.ObservedObjects);
        Assert.Equal("no-observed-objects", empty.ReasonCode);

        D2TestSupport.Dispose(runtime);
    }

    /// <summary>
    /// ★ Observer 边界（最强形式）★ 装与不装诊断，Planner 的产物必须**逐字节一致**。
    /// </summary>
    [Fact]
    public void PlanOutputIsByteIdenticalWithAndWithoutDiagnostics()
    {
        var job = Job();
        var observed = ObservedWith((@"\\host\d$\data", 5_000_000, 12), (@"\\host\d$\photos", 9_000_000, 30));
        var matrix = MigrationMatrix.Load(null, Silent);

        var withoutDiagnostics = new Planner(Silent).CreatePlan(job, observed, matrix);

        var runtime = DiagnosticRuntime.Start(new DiagnosticRuntimeOptions
        {
            StorageRoot = Path.Combine(_root, "diag-onoff"),
            InitialMode = CaptureMode.Deep,
            AppVersion = "d61-test",
            ShutdownBudgetMs = 5000,
        }, out _);

        MigrationPlan withDiagnostics;
        using (CoreDiagnostics.Install(runtime))
        {
            withDiagnostics = new Planner(Silent).CreatePlan(job, observed, matrix);
        }
        D2TestSupport.Shutdown(runtime);

        // 计划里含"生成时刻"（PlannedUtc）⇒ 先归零再比对（否则比的是时间，不是内容）。
        withoutDiagnostics.PlannedUtc = default;
        withDiagnostics.PlannedUtc = default;

        var a = JsonSerializer.Serialize(withoutDiagnostics);
        var b = JsonSerializer.Serialize(withDiagnostics);
        Assert.Equal(a, b);

        D2TestSupport.Dispose(runtime);
    }

    // ────────────────────────── ② NetworkShare 隐私边界 ──────────────────────────

    [Fact]
    public void SessionPayloadHasNoCredentialFields()
    {
        var forbidden = new[] { "user", "username", "password", "passwd", "pwd", "secret", "credential" };

        foreach (var type in new[] { typeof(NetSmbSessionPayload), typeof(NetShareDiscoveryPayload) })
        {
            foreach (var property in type.GetProperties())
            {
                var name = property.Name.ToLowerInvariant();
                Assert.DoesNotContain(forbidden, f => name.Contains(f, StringComparison.Ordinal));
            }
        }

        // 但必须**有**凭据语义字段（否则"用了哪套凭据"就无从判断）。
        Assert.True(typeof(NetSmbSessionPayload).GetProperty("UsedExplicitCreds") is not null);
    }

    [Fact]
    public void NetworkShareNeverPassesCredentialsIntoDiagnostics()
    {
        var source = ReadRepo("src", "PCMig.Core", "Native", "NetworkShare.cs");

        // 观察调用必须存在（§16 覆盖）。
        Assert.Contains("NetEvents.SmbSessionConnectStarted", source, StringComparison.Ordinal);
        Assert.Contains("NetEvents.SmbSessionReused", source, StringComparison.Ordinal);
        Assert.Contains("NetEvents.SmbSessionConnected", source, StringComparison.Ordinal);
        Assert.Contains("NetEvents.SmbConnectFailed", source, StringComparison.Ordinal);
        Assert.Contains("NetEvents.ShareResolved", source, StringComparison.Ordinal);
        Assert.Contains("NetEvents.ShareEnumerationFailed", source, StringComparison.Ordinal);

        // 观察调用里**不得**出现口令/用户名变量（只看诊断相关行）。
        foreach (var line in source.Split('\n'))
        {
            if (!line.Contains("PublishSession", StringComparison.Ordinal)
                && !line.Contains("NetSmbSessionPayload", StringComparison.Ordinal)
                && !line.Contains("PublishCore", StringComparison.Ordinal)) continue;

            Assert.DoesNotContain("password", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("string.IsNullOrEmpty(user)", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SessionPayloadRoundTripsThroughTheCodec()
    {
        var original = new NetSmbSessionPayload("WNetAddConnection2", Reused: true, ElapsedMs: 12, Succeeded: false)
        {
            Win32Error = 1219,
            UsedExplicitCreds = true,
            ReasonCode = "credential-conflict-reuse",
        };

        var line = SerializePayload(original);
        Assert.Contains("\"win32Error\":1219", line, StringComparison.Ordinal);
        Assert.Contains("\"usedExplicitCreds\":true", line, StringComparison.Ordinal);
        Assert.Contains("credential-conflict-reuse", line, StringComparison.Ordinal);

        // 用**真实的**事件（NET.SmbSessionConnected）并显式给出 payloadName，
        // 否则解析器会按 eventId 找目录里的默认载荷名（我第一版就因此解成了别的类型）。
        Assert.True(DiagnosticEventJson.TryParse(
            WrapAsEvent(NetEvents.SmbSessionConnected, "NetSmbSession", line), out var evt, out var error), error);
        var decoded = Assert.IsType<NetSmbSessionPayload>(evt!.Payload);
        Assert.Equal(1219, decoded.Win32Error);
        Assert.True(decoded.UsedExplicitCreds);
        Assert.Equal("credential-conflict-reuse", decoded.ReasonCode);

        var shares = new NetShareDiscoveryPayload(true, 5, true) { AdminShareCount = 2, Level = "level2-or-level1" };
        Assert.True(DiagnosticEventJson.TryParse(
            WrapAsEvent(NetEvents.ShareResolved, "NetShareDiscovery", SerializePayload(shares)), out var shareEvt, out _));
        var decodedShares = Assert.IsType<NetShareDiscoveryPayload>(shareEvt!.Payload);
        Assert.Equal(2, decodedShares.AdminShareCount);
        Assert.Equal("level2-or-level1", decodedShares.Level);
    }

    // ────────────────────────── helpers ──────────────────────────

    private static string SerializePayload(IDiagnosticPayload payload)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            payload.WriteJson(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>把载荷文本包成一条**真实事件**的合法 JSON 行（显式给出 payloadName，避免按 eventId 猜）。</summary>
    private static string WrapAsEvent(EventDescriptor descriptor, string payloadName, string payloadJson)
        => "{\"schemaVersion\":\"1.0\",\"sessionId\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\",\"sequence\":1," +
           "\"timestampUtc\":\"2026-10-01T00:00:00.0000000+00:00\",\"monotonicTimestamp\":1," +
           $"\"eventId\":{descriptor.EventId},\"eventCode\":\"{descriptor.Code}\",\"eventName\":\"{descriptor.Name}\"," +
           $"\"eventVersion\":{descriptor.Version},\"level\":\"Information\",\"deliveryClass\":\"Operational\"," +
           $"\"evidenceQuality\":\"Direct\",\"captureMode\":\"Operational\"," +
           $"\"payloadName\":\"{payloadName}\",\"payload\":{payloadJson}}}";

    private static List<IDiagnosticPayload> ReadPayloads(DiagnosticRuntime runtime)
    {
        var dir = Path.Combine(runtime.Store!.SessionDir, "events");
        var payloads = new List<IDiagnosticPayload>();
        foreach (var file in Directory.GetFiles(dir, "*.jsonl"))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                if (DiagnosticEventJson.TryParse(line, out var evt, out _) && evt?.Payload is not null)
                    payloads.Add(evt.Payload);
            }
        }
        return payloads;
    }

    private static string ReadRepo(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln")))
            {
                var path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
                Assert.True(File.Exists(path), "缺少文件：" + path);
                return File.ReadAllText(path);
            }
        }
        throw new InvalidOperationException("找不到仓库根");
    }
}