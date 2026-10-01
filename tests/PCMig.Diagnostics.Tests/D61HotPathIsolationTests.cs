using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PCMig.Diagnostics;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using Xunit;
using Xunit.Abstractions;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D6.1 §8 + §9（热路径隔离）收口测试：
///   §20.17 慢/失败的 incident writer ⇒ analyzer 必须继续前进（不得被磁盘卡住）；
///   §20.18 环满时触发飞行窗口 ⇒ 生产侧延迟必须有界（不得随环内容线性阻塞）。
/// </summary>
public sealed class D61HotPathIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-d61h-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ITestOutputHelper _output;

    public D61HotPathIsolationTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* 句柄延迟释放 */ }
    }

    // ────────────────────────── §20.17 analyzer 不被事件卡磁盘 IO 卡住 ──────────────────────────

    [Fact]
    public async Task BlockedIncidentPersistenceDoesNotStallTheAnalyzerPath()
    {
        // 分支级确定性验证：消费者被卡住时，**生产侧（analyzer 会走的路径）必须立刻返回**，
        // 超出容量后按 Operational 记账（可见），而不是等待磁盘。
        var ledger = new LossLedger();
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();

        var branch = new BoundedBranch(
            DiagnosticBranches.Incident,
            capacity: 8,
            byteBudget: 1024 * 1024,
            evictsOldest: false,
            ledger,
            new DiagnosticHealth(),
            async (_, ct) =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(ct).ConfigureAwait(false);
            });

        branch.Start(CancellationToken.None);

        var sw = Stopwatch.StartNew();
        var accepted = 0;
        for (var i = 0; i < 200; i++)
        {
            // 模拟 analyzer 产出卡片行并入队（非阻塞）
            if (branch.TryAccept(new BranchItem(null, 256, "{\"incident\":\"synthetic\"}"))) accepted++;
        }
        sw.Stop();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _output.WriteLine($"incident 队列：接受 {accepted} 条，生产侧耗时 {sw.Elapsed.TotalMilliseconds:F1} ms（消费者被卡住）");

        Assert.True(accepted > 0);
        Assert.True(sw.Elapsed.TotalMilliseconds < 500,
            $"生产侧被消费者拖住了：{sw.Elapsed.TotalMilliseconds:F1} ms");
        Assert.True(accepted < 200, "容量上限必须生效（超出部分按丢失记账）");
        Assert.Contains(ledger.Snapshot().Records, r => r.Branch == DiagnosticBranches.Incident);

        release.TrySetResult();
        await branch.StopAsync(TimeSpan.FromSeconds(2));
        await branch.DisposeAsync();
    }

    [Fact]
    public void AnalyzerStillOpensIncidentsWhenIncidentStorageIsBroken()
    {
        // 运行时级：**启动前**就把事件卡目录占成同名文件 ⇒ 事件卡持久化从一开始就不可能；
        // 规则引擎（analyzer）必须照常出卡，且"没有事件卡写者"这件事必须可见。
        //
        // 为什么不在运行中破坏目录：那样第一次 AppendRawLine 会在**它自己的消费者线程**上阻塞
        // （durable IO 允许慢，隔离性本身没问题），失败痕迹要等它返回才出现 ⇒ 断言变成时序竞态。
        // 本用例要证明的是"分析器不被事件卡持久化拖住"，用启动前的确定性场景更能说明问题。
        var root = Path.Combine(_root, "runtime");
        // ★ 占用 **StorageRoot 本身**（不是它下面的子目录）：这样 EnsureCreated 必然失败，
        //   启动时就确定"没有事件卡写者"（会话 id 启动前未知，无法只占 incidents 子目录）。
        File.WriteAllText(root, "not-a-directory");

        var runtime = DiagnosticRuntime.Start(new DiagnosticRuntimeOptions
        {
            StorageRoot = root,
            InitialMode = CaptureMode.Operational,
            AppVersion = "d61-test",
            ShutdownBudgetMs = 3000,
        }, out var degraded);

        // 存储整体不可用（目录被占）⇒ 运行时必须 fail-open，而不是抛异常。
        Assert.False(string.IsNullOrWhiteSpace(degraded));
        Assert.Null(runtime.Store);

        for (var i = 0; i < 3; i++)
        {
            runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                PersistenceEvents.WriteFailed,
                DiagnosticContext.Root(runtime.SessionId, "D61").WithJob("JOB-H"),
                new PstWritePayload("Receipt", "Move", true, "IOException"),
                Outcome: DiagnosticOutcome.Failed,
                ErrorDomain: ErrorDomain.Managed));
        }

        // ★ 核心：没有存储也照常出卡（规则引擎与分析不依赖磁盘）★
        Assert.True(D2TestSupport.WaitUntil(() => runtime.ActiveIncidents.Count > 0, 10_000),
            "事件卡落盘不可用不得阻止规则引擎出卡");

        // 失败必须可见：没有事件卡写者 ⇒ 台账里有明确原因。
        // ★ 注意必须**有界等待**：ActiveIncidents 在 OnIncident 回调之前就可被读到，
        //   直接断言会是竞态（实测偶发失败）。要求不变，只是等它落地。
        Assert.True(D2TestSupport.WaitUntil(
                () => runtime.Loss.Snapshot().Records.Any(r =>
                    r.Branch == DiagnosticBranches.Incident && r.ReasonCode == "incident-writer-unavailable"),
                10_000),
            "没有事件卡写者这件事必须进台账（不得静默）");

        D2TestSupport.Shutdown(runtime);
        D2TestSupport.Dispose(runtime);
    }

    // ────────────────────────── §20.18 环满时触发延迟有界 ──────────────────────────

    [Fact]
    public void TriggerLatencyStaysBoundedWhenTheRingIsFull()
    {
        var store = new DiagnosticSessionStore(Path.Combine(_root, "flight"), Guid.NewGuid());
        store.EnsureCreated();

        var options = new DiagnosticRuntimeOptions
        {
            RingEventCapacity = 20_000,
            RingByteBudget = 64L * 1024 * 1024,
            FlightMaxPinnedWindows = 64,
            FlightPostWindowMs = 0,
            FlightTriggerCooldownMs = 0,
            FlightWindowPayloadByteBudget = 512L * 1024 * 1024,
        };
        var flight = new FlightRecorder(store, options, new DiagnosticHealth(), new LossLedger());

        // 把环填满（生产侧正常写入）
        for (var i = 0; i < 20_000; i++) flight.Observe(Event(i + 1));

        // 触发：现在锁内只做"钉住 + 换环"（O(1)），复制放到锁外。
        var worst = 0.0;
        var samples = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            for (var k = 0; k < 20_000; k++) flight.Observe(Event(100_000 + i * 20_000 + k));

            var sw = Stopwatch.StartNew();
            var outcome = flight.Trigger("latency", "hot-path");
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
            worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds);
            Assert.True(outcome.Accepted, outcome.ReasonCode);
        }

        _output.WriteLine("环满(20000 条)触发 5 次延迟(ms)：" + string.Join(", ", samples.Select(s => s.ToString("F2"))));
        _output.WriteLine($"最坏 {worst:F2} ms（旧实现需要在锁内复制整个环）");

        // 宽松上限：这不是性能验收，只是"不得随环内容线性阻塞生产侧"的守门线。
        Assert.True(worst < 50, $"触发最坏延迟 {worst:F2} ms 过高");
    }

    private static DiagnosticEvent Event(long sequence) => new()
    {
        SchemaVersion = DiagnosticEvent.CurrentSchemaVersion,
        Descriptor = PersistenceEvents.WriteFailed,
        SessionId = Guid.NewGuid(),
        Sequence = sequence,
        TimestampUtc = DateTimeOffset.UtcNow,
        MonotonicTimestamp = Stopwatch.GetTimestamp(),
        Level = DiagnosticLevel.Error,
        Delivery = DeliveryClass.Operational,
        EvidenceQuality = EvidenceQuality.Direct,
        CaptureMode = CaptureMode.Operational,
        Payload = new PstWritePayload("Receipt", "Move", true, "IOException"),
    };
}