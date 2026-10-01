using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using Xunit;
using Xunit.Abstractions;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D2 出口证据：**OFF 路径零分配** + 管线突发吞吐/丢包 + 真实行长度的实测基线。
///
/// ⚠ 这些是**本机合成突发**的数字，不是产品性能保证：
///   端到端 OFF/Operational/Flight/Deep 对照属于 D7（三 VM + 210 万文件），本阶段不做。
/// </summary>
public sealed class D2ProbeTests
{
    private readonly ITestOutputHelper _output;

    public D2ProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void OffModePublishAllocatesNothing()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var runtime = DiagnosticRuntime.Start(D2TestSupport.Options(root, CaptureMode.Off), out _);
            try
            {
                var ctx = DiagnosticContext.Root(runtime.SessionId, "Probe");
                for (var i = 0; i < 5_000; i++)
                    runtime.Publisher.TryPublish(new DiagnosticEventDraft(TransferEvents.ObjectCompleted, ctx));

                const int iterations = 200_000;

                // ★ 必须在**专用线程**上测量 ★
                //   GC.GetAllocatedBytesForCurrentThread 只统计当前线程，而 xUnit 的测试线程是线程池线程：
                //   运行时后台管线（定时器/收件箱消费者）的**续体可能落到同一线程**并产生分配，
                //   于是"OFF 路径 0 字节"会偶发地测出非零（实测在整包并跑、负载较高时出现过一次）。
                //   搬到独占线程后测到的只剩被测代码；**要求不变**（仍必须为 0）。
                var allocated = 0L;
                var worker = new System.Threading.Thread(() =>
                {
                    const int warmup = 5_000;
                    for (var i = 0; i < warmup; i++)
                        runtime.Publisher.TryPublish(new DiagnosticEventDraft(TransferEvents.ObjectCompleted, ctx));

                    var start = GC.GetAllocatedBytesForCurrentThread();
                    for (var i = 0; i < iterations; i++)
                        runtime.Publisher.TryPublish(new DiagnosticEventDraft(TransferEvents.ObjectCompleted, ctx));
                    allocated = GC.GetAllocatedBytesForCurrentThread() - start;
                }, maxStackSize: 1 << 20)
                { IsBackground = true };
                worker.Start();
                Assert.True(worker.Join(TimeSpan.FromMinutes(2)), "测量线程未在预算内完成");

                _output.WriteLine($"OFF：{iterations} 次 TryPublish 共分配 {allocated} 字节（{allocated / (double)iterations:F3} B/次，专用线程）");
                Assert.Equal(0, allocated);
            }
            finally
            {
                D2TestSupport.Shutdown(runtime);
                D2TestSupport.Dispose(runtime);
            }
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    [Fact]
    public void OnModePublishAllocationPerEvent()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var runtime = DiagnosticRuntime.Start(D2TestSupport.Options(root), out _);
            try
            {
                var ctx = DiagnosticContext.Root(runtime.SessionId, "Probe").WithJob("JOB-1").WithObject("obj-1");
                for (var i = 0; i < 5_000; i++)
                    runtime.Publisher.TryPublish(new DiagnosticEventDraft(TransferEvents.ObjectCompleted, ctx));

                const int iterations = 100_000;
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < iterations; i++)
                    runtime.Publisher.TryPublish(new DiagnosticEventDraft(TransferEvents.ObjectCompleted, ctx));
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                var perEvent = allocated / (double)iterations;
                _output.WriteLine($"ON（Operational 事件、无 payload、含 Job/Object 上下文）：{perEvent:F1} B/次");
                _output.WriteLine("（该数字包含 DiagnosticEvent 记录本身与字节估算；payload 由调用点按需构造，不计入）");
                Assert.True(perEvent < 2_048, $"单次发布分配 {perEvent:F1} B，超出契约层预算");
            }
            finally
            {
                D2TestSupport.Shutdown(runtime);
                D2TestSupport.Dispose(runtime);
            }
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    /// <summary>真实量级的突发：全部落盘、零丢失，并报告实测排空时间。</summary>
    [Fact]
    public void PipelineDrainsRealisticBurstWithoutLoss()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var options = D2TestSupport.Options(root);
            options.OperationalQueueCapacity = 8_192;
            options.OperationalByteBudget = 32L * 1024 * 1024;
            var runtime = DiagnosticRuntime.Start(options, out _);

            // 2000 条生命周期事件 ≈ 一次大迁移里"几十秒量级"的对象完成流量（不是 20 万文件的逐文件流量）。
            const int total = 2_000;
            try
            {
                var ctx = DiagnosticContext.Root(runtime.SessionId, "Probe").WithJob("JOB-1");
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < total; i++)
                {
                    runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                        TransferEvents.ObjectCompleted, ctx.WithObject("obj-" + (i % 500)),
                        Outcome: DiagnosticOutcome.Succeeded));
                }
                var publishMs = sw.Elapsed.TotalMilliseconds;

                Assert.True(D2TestSupport.WaitUntil(() => runtime.Health.EventsWritten >= total, 20_000),
                    $"应有 {total} 条落盘，实际 {runtime.Health.EventsWritten}；" +
                    $"produced={runtime.Health.EventsProduced} accepted={runtime.Health.EventsAccepted} " +
                    $"dropped={runtime.GetHealthSnapshot().EventsDropped} evicted={runtime.GetHealthSnapshot().EventsEvicted}");
                sw.Stop();

                var health = runtime.GetHealthSnapshot();
                _output.WriteLine($"突发 {total} 条：发布 {publishMs:F1} ms，全部落盘 {sw.Elapsed.TotalMilliseconds:F0} ms");
                _output.WriteLine($"health: produced={health.EventsProduced} accepted={health.EventsAccepted} " +
                                  $"written={health.EventsWritten} dropped={health.EventsDropped} evicted={health.EventsEvicted} " +
                                  $"writtenBytes={health.WrittenBytes} rotations={health.Rotations} lossEpoch={health.LossEpoch}");
                _output.WriteLine($"平均每行 {health.WrittenBytes / (double)health.EventsWritten:F0} 字节" +
                                  "（含 JSON 转义与换行；用于校准方案 §25 的存储算式）");

                var lossRecords = string.Join("; ", runtime.Loss.Snapshot().Records
                    .Select(r => $"{r.Branch}/{r.DeliveryClass} {r.ReasonCode} x{r.Total}"));
                Assert.True(health.EvidenceComplete,
                    "真实量级突发不允许出现任何丢失；若再次出现，下面是**具体丢失台账**（便于一次定位，而不是再猜）：" +
                    $"lossEpoch={health.LossEpoch} dropped={health.EventsDropped} evicted={health.EventsEvicted} " +
                    $"storageDegraded={health.StorageDegraded} reason={health.LastStorageReason} records=[{lossRecords}]");
            }
            finally
            {
                D2TestSupport.Shutdown(runtime);
                D2TestSupport.Dispose(runtime);
            }
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    /// <summary>
    /// ★ 过载契约 ★ 故意用小队列灌爆它：**允许丢，但绝不允许静默丢**。
    /// 这就是方案 §16 的核心承诺——有界内存 + 永不阻塞业务 + 零丢失不可兼得，
    /// 因此系统必须给出可度量、可引用的损失台账，而不是假装没事。
    /// </summary>
    [Fact]
    public void OverloadLossesAreRecordedNotSilent()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var options = D2TestSupport.Options(root);
            options.OperationalQueueCapacity = 64;     // 故意极小
            options.OperationalByteBudget = 64 * 1024;
            options.CriticalReserveCapacity = 8;
            var runtime = DiagnosticRuntime.Start(options, out _);

            const int total = 50_000;
            try
            {
                var ctx = DiagnosticContext.Root(runtime.SessionId, "Probe").WithJob("JOB-1");
                var sw = Stopwatch.StartNew();
                var accepted = 0;
                for (var i = 0; i < total; i++)
                {
                    if (runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                            TransferEvents.ObjectCompleted, ctx.WithObject("obj-" + (i % 500)),
                            Outcome: DiagnosticOutcome.Succeeded)))
                        accepted++;
                }
                sw.Stop();

                var health = runtime.GetHealthSnapshot();
                _output.WriteLine($"过载 {total} 条（队列 64）：发布耗时 {sw.Elapsed.TotalMilliseconds:F0} ms，" +
                                  $"accepted={accepted} produced={health.EventsProduced}");
                _output.WriteLine($"loss: dropped={health.EventsDropped} evicted={health.EventsEvicted} " +
                                  $"lossEpoch={health.LossEpoch} evidenceComplete={health.EvidenceComplete} " +
                                  $"stickyCriticalLost={health.StickyCriticalLost}");

                Assert.True(sw.ElapsedMilliseconds < 10_000, "生产者绝不允许因为队列满而等待/卡住");
                Assert.False(health.EvidenceComplete, "过载之后证据完整性必须为假");
                Assert.True(health.LossEpoch > 0, "任何丢弃都必须推进丢失世代");
                Assert.True(health.EventsDropped + health.EventsEvicted > 0, "丢弃必须是可度量的");

                // 台账要能指出是哪一条分支丢的。
                var snapshot = runtime.Loss.Snapshot();
                Assert.NotEmpty(snapshot.Records);
                foreach (var record in snapshot.Records.Take(3))
                    _output.WriteLine($"  台账：branch={record.Branch} class={record.DeliveryClass} " +
                                      $"dropped={record.Dropped} evicted={record.Evicted} reason={record.ReasonCode}");

                // 过载不等于"系统坏了"：writer 仍然在正常工作（只是丢掉了排不进队的部分）。
                Assert.True(D2TestSupport.WaitUntil(() => runtime.Health.EventsWritten > 0, 10_000));
            }
            finally
            {
                D2TestSupport.Shutdown(runtime);
                D2TestSupport.Dispose(runtime);
            }
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    /// <summary>写一条最坏形状事件（全部可选字段 + payload）看它到底多大，用于单行上限预算。</summary>
    [Fact]
    public void WorstCaseEventLineSizeIsBounded()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var runtime = DiagnosticRuntime.Start(D2TestSupport.Options(root), out _);
            try
            {
                var ctx = DiagnosticContext.Root(runtime.SessionId, "Probe")
                    .WithJob("JOB-1").WithObject("obj-1").WithControl("Step4.Verify");

                runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                    VerifyEvents.Mismatch,
                    ctx,
                    new Abstractions.Payloads.DiaStorageFailedPayload("segment-writer", "io-error", 112, true),
                    Outcome: DiagnosticOutcome.Failed,
                    ErrorDomain: ErrorDomain.Win32,
                    Win32Error: 112,
                    HResult: unchecked((int)0x80070070),
                    DurationMs: 1234,
                    Message: new string('m', 400),
                    Path: runtime.PathRef(@"\\server\share\dir\file.dat", PathRole.Target, "dst"),
                    Phase: "Running",
                    StateOwner: StateOwner.Core,
                    ObservationVersion: 42,
                    ProjectionVersion: 41,
                    CorrelationId: "corr-1",
                    Truncated: true,
                    ContractVersion: 3));

                Assert.True(D2TestSupport.WaitUntil(() => runtime.TryGetViewerSnapshot(out var e) && e.Length >= 1));
                runtime.TryGetViewerSnapshot(out var events);
                var line = Abstractions.Serialization.DiagnosticEventJson.ToJsonLine(events.Last());
                var bytes = Encoding.UTF8.GetByteCount(line);

                _output.WriteLine($"最坏形状事件单行 = {bytes} 字节（Message 已被截断到 {runtime.Options.MaxMessageLength}）");
                Assert.True(bytes < 4 * 1024, $"单行 {bytes} 字节，超出方案的单行上限预算");
            }
            finally
            {
                D2TestSupport.Shutdown(runtime);
                D2TestSupport.Dispose(runtime);
            }
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }
}