using System;
using System.Diagnostics;
using System.Text;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using PCMig.Diagnostics.Abstractions.Serialization;
using Xunit;
using Xunit.Abstractions;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D1 出口证据：**OFF 路径零分配** + 规范序列化的实测基线。
///
/// ⚠ 这是**合成微探针**，只证明"契约层自身的成本"，**不等于**迁移吞吐验收：
///   端到端 OFF/Operational/Flight/Deep 对照属于 D7（三 VM + 210 万文件），本阶段不做。
/// 分配量用 <see cref="GC.GetAllocatedBytesForCurrentThread"/>（当前线程精确值，不受其它线程干扰）；
/// 时间只做量级参考（机器相关 ⇒ 不做断言，只记录）。
/// </summary>
public sealed class D1ProbeTests
{
    private readonly ITestOutputHelper _output;

    public D1ProbeTests(ITestOutputHelper output) => _output = output;

    private const int Iterations = 200_000;
    private const int WarmupIterations = 5_000;

    [Fact]
    public void OffPathAllocatesZeroBytes()
    {
        var publisher = NoOpDiagnosticPublisher.Instance;
        var ctx = DiagnosticContext.Root(TestEvents.FixedSessionId, "Probe");

        for (var i = 0; i < WarmupIterations; i++) PublishGuarded(publisher, UiEvents.UserActionObserved, ctx);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Iterations; i++) PublishGuarded(publisher, UiEvents.UserActionObserved, ctx);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        _output.WriteLine($"OFF 路径：{Iterations} 次受保护发布共分配 {allocated} 字节（{allocated / (double)Iterations:F3} B/次）");
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void OffPathAllocatesZeroBytesForEveryCatalogEvent()
    {
        var publisher = NoOpDiagnosticPublisher.Instance;
        var ctx = DiagnosticContext.Root(TestEvents.FixedSessionId, "Probe");
        var all = EventCatalog.All;

        // ★ 用索引循环而不是 foreach：对 IReadOnlyList<T> 做 foreach 会为数组枚举器分配 32 字节
        //   （实测踩过），那是**测试写法**的开销，不是 OFF 路径的开销，会污染本探针的结论。
        for (var i = 0; i < all.Count; i++) PublishGuarded(publisher, all[i], ctx);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < all.Count; i++)
            for (var n = 0; n < 500; n++)
                PublishGuarded(publisher, all[i], ctx);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        _output.WriteLine($"OFF 路径（全 catalog {all.Count} 个事件 × 500）：分配 {allocated} 字节");
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void OnPathIsBoundedPerEvent()
    {
        var publisher = new CountingPublisher();
        var ctx = DiagnosticContext.Root(TestEvents.FixedSessionId, "Probe").WithJob("JOB-1").WithOperation(Guid.Empty, runGeneration: 1);

        for (var i = 0; i < WarmupIterations; i++) PublishGuarded(publisher, DiagnosticsEvents.ModeChanged, ctx);
        var acceptedBaseline = publisher.Accepted;

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Iterations; i++) PublishGuarded(publisher, DiagnosticsEvents.ModeChanged, ctx);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var perEvent = allocated / (double)Iterations;

        _output.WriteLine($"ON 路径（小 payload 记录 + draft 结构）：{perEvent:F2} B/次，本轮接受 {publisher.Accepted - acceptedBaseline} 次");
        Assert.Equal(Iterations, publisher.Accepted - acceptedBaseline);

        // 目标：一次"受保护发布 + 小 payload"保持在数百字节以内（含 record 对象开销）。
        Assert.True(perEvent < 512, $"ON 路径每次发布分配 {perEvent:F2} 字节，超出契约层预算");
    }

    /// <summary>
    /// 规范事件的实际序列化体积/耗时基线（用于校准方案 §25 里"每事件 600 B"的假设，
    /// 以及 D2 writer 的批量大小候选值）。**不做时间断言**（机器相关）。
    /// </summary>
    [Fact]
    public void CanonicalSerializationSizeAndCostBaseline()
    {
        var payload = new AppUnhandledExceptionPayload(
            "System.IO.IOException", unchecked((int)0x80070070), "Running", false,
            "PCMig.Core.Transfer.TransferOrchestrator.RunOneObjectAsync <- PCMig.Core.Transfer.TransferOrchestrator.RunAsync", true);

        var evt = TestEvents.WithAllOptionalFields(PersistenceEvents.WriteFailed, payload);
        var line = DiagnosticEventJson.ToJsonLine(evt);
        var bytes = Encoding.UTF8.GetByteCount(line);

        _output.WriteLine($"典型事件（含全部可选字段 + payload）JSONL 长度 = {bytes} 字节（{line.Length} 字符）");
        _output.WriteLine($"其中 envelope 字段数与 trace/span 均在场（最坏形状），章节预算假设为约 600 B/事件。");

        // 方案 §25 的存储算式用的是"每事件平均字节数"，因此这里给出三种真实形状，
        // 避免拿最坏形状当平均值（那会把 2.1M 文件场景的磁盘预算估高一倍以上）。
        var lifecycle = TestEvents.Minimal(TransferEvents.ObjectCompleted) with { JobId = "JOB-20260930-121847-49fc", ObjectId = "obj-000123", OperationId = Guid.Empty };
        var minimal = TestEvents.Minimal(UiEvents.UserActionObserved);
        var lifecycleBytes = Encoding.UTF8.GetByteCount(DiagnosticEventJson.ToJsonLine(lifecycle));
        var minimalBytes = Encoding.UTF8.GetByteCount(DiagnosticEventJson.ToJsonLine(minimal));
        _output.WriteLine($"生命周期事件（带 JobId/ObjectId/OperationId、无 payload）= {lifecycleBytes} 字节；最小事件 = {minimalBytes} 字节");

        const int rounds = 20_000;
        for (var i = 0; i < 2_000; i++) DiagnosticEventJson.ToJsonLine(evt);

        var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < rounds; i++) DiagnosticEventJson.ToJsonLine(evt);
        sw.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;

        _output.WriteLine($"序列化 {rounds} 次：{sw.Elapsed.TotalMilliseconds / rounds * 1000:F2} µs/次，" +
                          $"{allocated / (double)rounds:F0} B/次（含 MemoryStream + 字符串）");

        Assert.True(bytes > 200, "序列化结果异常地小，格式可能没写全");
        Assert.True(bytes < 4096, $"单事件 JSONL 达到 {bytes} 字节，已超出方案的单行上限预算");
    }

    /// <summary>解析（回放路径）的成本基线：允许更高，但必须是有界且可预测的。</summary>
    [Fact]
    public void CanonicalParseRoundtripsAtReplayCost()
    {
        var payload = new DiaStorageFailedPayload("segment-writer", "io-error", 112, true);
        var line = DiagnosticEventJson.ToJsonLine(TestEvents.WithAllOptionalFields(PersistenceEvents.WriteFailed, payload));

        const int rounds = 20_000;
        for (var i = 0; i < 2_000; i++) DiagnosticEventJson.TryParse(line, out _, out _);

        var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        var ok = 0;
        for (var i = 0; i < rounds; i++)
            if (DiagnosticEventJson.TryParse(line, out var parsed, out _) && parsed is not null) ok++;
        sw.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;

        _output.WriteLine($"解析 {rounds} 次：{sw.Elapsed.TotalMilliseconds / rounds * 1000:F2} µs/次，" +
                          $"{allocated / (double)rounds:F0} B/次；成功 {ok} 次");

        Assert.Equal(rounds, ok);
    }

    /// <summary>受保护发布的标准写法（生产代码必须照这个形状写：先判 IsEnabledFor，再构造 payload）。</summary>
    private static void PublishGuarded(IDiagnosticPublisher publisher, EventDescriptor descriptor, DiagnosticContext ctx)
    {
        if (!publisher.IsEnabledFor(descriptor)) return;

        publisher.TryPublish(new DiagnosticEventDraft(
            descriptor,
            ctx,
            new DiaModeChangedPayload(CaptureMode.Operational, CaptureMode.Deep, "probe")));
    }

    /// <summary>只计数、不做任何 I/O 的发布器：用于测量"发布路径本身"的成本。</summary>
    private sealed class CountingPublisher : IDiagnosticPublisher
    {
        public long Accepted { get; private set; }

        public bool IsEnabled => true;

        public CaptureMode Mode => CaptureMode.Operational;

        public bool IsEnabledFor(EventDescriptor descriptor) => true;

        public bool TryPublish(in DiagnosticEventDraft draft)
        {
            Accepted++;
            return true;
        }
    }
}