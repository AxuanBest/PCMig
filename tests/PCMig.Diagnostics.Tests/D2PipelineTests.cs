using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D2 契约：有界摄入 + 显式 fan-out。
/// 这里验证的是**架构不变量**，不是性能承诺：
///   · 生产路径永不等待（满了就拒收并记账）；
///   · 字节预算真的生效（不只是条数）；
///   · writer 卡住不拖 analyzer/viewer；viewer 满不影响 writer；
///   · 丢失全部进台账，且 DurableCritical 丢失是 sticky 的。
/// </summary>
public sealed class D2PipelineTests
{
    private static BoundedBranch Branch(
        string name, int capacity, long byteBudget, LossLedger loss, DiagnosticHealth health,
        Func<BranchItem, CancellationToken, ValueTask>? consumer = null, bool evictsOldest = false)
        => new(name, capacity, byteBudget, evictsOldest, loss, health,
            consumer ?? ((_, _) => default));

    [Fact]
    public void FullQueueRejectsInsteadOfBlocking()
    {
        var loss = new LossLedger();
        var health = new DiagnosticHealth();
        var gate = new ManualResetEventSlim(false);

        // 消费者故意卡住：队列会很快填满。
        var branch = Branch("ingress-operational", capacity: 8, byteBudget: 1_000_000, loss, health,
            (_, _) => { gate.Wait(TimeSpan.FromSeconds(30)); return default; });
        branch.Start(CancellationToken.None);
        try
        {
            var accepted = 0;
            var rejected = 0;
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < 5_000; i++)
            {
                var item = new BranchItem(D2TestSupport.Event(i + 1), 300);
                if (branch.TryAccept(in item)) accepted++; else rejected++;
            }
            sw.Stop();

            Assert.True(rejected > 0, "队列满时必须拒收，而不是全部接受");
            Assert.True(accepted > 0, "至少应有若干条被接受");
            Assert.True(sw.ElapsedMilliseconds < 2_000,
                $"5000 次 TryAccept 用了 {sw.ElapsedMilliseconds} ms —— 生产路径不允许等待");
            Assert.True(loss.Epoch >= rejected, "每一次拒收都必须进丢失台账");
            Assert.Equal(rejected, loss.Snapshot().TotalDropped);
        }
        finally
        {
            gate.Set();
            D2TestSupport.Stop(branch, 200);
        }
    }

    [Fact]
    public void ByteBudgetRejectsEvenWhenEventCountIsFine()
    {
        var loss = new LossLedger();
        var health = new DiagnosticHealth();
        var gate = new ManualResetEventSlim(false);

        var branch = Branch("ingress-operational", capacity: 1000, byteBudget: 10_000, loss, health,
            (_, _) => { gate.Wait(TimeSpan.FromSeconds(30)); return default; });
        branch.Start(CancellationToken.None);
        try
        {
            var accepted = 0;
            var rejected = 0;
            for (var i = 0; i < 200; i++)
            {
                var item = new BranchItem(D2TestSupport.Event(i + 1), 4_000); // 每条 4KB，预算 10KB
                if (branch.TryAccept(in item)) accepted++; else rejected++;
            }

            Assert.True(accepted <= 3, $"字节预算 10KB、每条 4KB：接受数应 <= 3，实际 {accepted}");
            Assert.True(rejected > 0);
            Assert.Contains(loss.Snapshot().Records, r => r.ReasonCode == "byte-budget");
        }
        finally
        {
            gate.Set();
            D2TestSupport.Stop(branch, 200);
        }
    }

    [Fact]
    public void DroppedOldestIsCountedAsEvictionAndReleasesBudget()
    {
        var loss = new LossLedger();
        var health = new DiagnosticHealth();
        var gate = new ManualResetEventSlim(false);

        var branch = Branch("ingress-verbose", capacity: 4, byteBudget: 1_000_000, loss, health,
            (_, _) => { gate.Wait(TimeSpan.FromSeconds(30)); return default; }, evictsOldest: true);
        branch.Start(CancellationToken.None);
        try
        {
            for (var i = 0; i < 50; i++)
            {
                var item = new BranchItem(D2TestSupport.Event(i + 1), 100);
                branch.TryAccept(in item);
            }

            var stats = branch.Stats();
            Assert.True(stats.Evicted > 0, "DropOldest 模式下必须记到 Evicted（Coalesced 口径）");
            Assert.Equal(0, stats.DroppedFull); // DropOldest 下 TryWrite 不因满而失败
            Assert.True(loss.Snapshot().TotalEvicted > 0);
            Assert.True(branch.BudgetUtilization <= 1.0, "淘汰必须释放字节预算，利用率不能超过 100%");
        }
        finally
        {
            gate.Set();
            D2TestSupport.Stop(branch, 200);
        }
    }

    [Fact]
    public void CriticalLossIsStickyAndRaisesEpoch()
    {
        var loss = new LossLedger();
        Assert.Equal(0, loss.Epoch);
        Assert.False(loss.StickyCriticalLost);

        loss.RecordDrop("ingress-operational", DeliveryClass.Operational, 10, "queue-full", evicted: false);
        Assert.Equal(1, loss.Epoch);
        Assert.False(loss.StickyCriticalLost);

        loss.RecordDrop("ingress-critical", DeliveryClass.DurableCritical, 11, "reserve-exhausted", evicted: false);
        Assert.Equal(2, loss.Epoch);
        Assert.True(loss.StickyCriticalLost);
        Assert.Equal(1, loss.Snapshot().CriticalLost);

        // sticky：后续普通丢失不得复位这个状态。
        loss.RecordDrop("ingress-verbose", DeliveryClass.Verbose, 12, "evicted-oldest", evicted: true);
        Assert.True(loss.StickyCriticalLost);
        Assert.Equal(3, loss.Epoch);
    }

    /// <summary>★ 架构不变量 ★ writer 卡住时 analyzer/viewer 必须照常推进（显式 fan-out 的意义）。</summary>
    [Fact]
    public void SlowWriterDoesNotStarveAnalyzerOrViewer()
    {
        var loss = new LossLedger();
        var health = new DiagnosticHealth();
        var writerGate = new ManualResetEventSlim(false);
        var analyzerSeen = 0;
        var viewerSeen = 0;

        var writer = Branch("writer", capacity: 4, byteBudget: 1_000_000, loss, health,
            (_, ct) => { writerGate.Wait(TimeSpan.FromSeconds(30), ct); return default; });
        var analyzer = Branch("analyzer", capacity: 1024, byteBudget: 1_000_000, loss, health,
            (_, _) => { Interlocked.Increment(ref analyzerSeen); return default; });
        var viewer = Branch("viewer", capacity: 64, byteBudget: 1_000_000, loss, health,
            (_, _) => { Interlocked.Increment(ref viewerSeen); return default; });

        writer.Start(CancellationToken.None);
        analyzer.Start(CancellationToken.None);
        viewer.Start(CancellationToken.None);

        try
        {
            var fanOut = new FanOutStage(writer, analyzer, viewer);
            for (var i = 0; i < 200; i++)
            {
                var item = new BranchItem(D2TestSupport.Event(i + 1), 200);
                D2TestSupport.Route(fanOut, item);
            }

            // 先等待，再取快照：这样断言消息里的数字是**等待之后**的真实状态（采样时机不影响判定）。
            var analyzerOk = D2TestSupport.WaitUntil(() => Volatile.Read(ref analyzerSeen) >= 100);
            var viewerOk = D2TestSupport.WaitUntil(() => Volatile.Read(ref viewerSeen) >= 50);

            var stats = fanOut.Stats();
            Assert.True(analyzerOk,
                $"writer 卡住时 analyzer 必须继续收到事件：seen={Volatile.Read(ref analyzerSeen)} " +
                $"accepted={stats.AnalyzerAccepted} rejected={stats.AnalyzerRejected} " +
                $"sinkFaults={analyzer.Stats().SinkFaults} depth={analyzer.Depth} processed={analyzer.Stats().Processed}");
            Assert.True(viewerOk,
                $"writer 卡住时 viewer 必须继续收到事件：seen={Volatile.Read(ref viewerSeen)} accepted={stats.ViewerAccepted}");

            Assert.True(stats.WriterRejected > 0, "writer 收件箱应当被填满并出现拒收（这正是隔离的证据）");
            Assert.True(stats.AnalyzerAccepted >= 100);
        }
        finally
        {
            writerGate.Set();
            foreach (var branch in new[] { writer, analyzer, viewer })
                D2TestSupport.Stop(branch, 300);
        }
    }

    /// <summary>Verbose 只进 writer（非 Deep 模式）：它"允许被丢"，不应占用分析/显示预算。</summary>
    [Fact]
    public void VerboseEventsAreNotRoutedToAnalyzerOrViewer()
    {
        var loss = new LossLedger();
        var health = new DiagnosticHealth();
        var analyzerSeen = 0;
        var viewerSeen = 0;

        var writer = Branch("writer", 64, 1_000_000, loss, health);
        var analyzer = Branch("analyzer", 64, 1_000_000, loss, health, (_, _) => { Interlocked.Increment(ref analyzerSeen); return default; });
        var viewer = Branch("viewer", 64, 1_000_000, loss, health, (_, _) => { Interlocked.Increment(ref viewerSeen); return default; });
        foreach (var b in new[] { writer, analyzer, viewer }) b.Start(CancellationToken.None);

        try
        {
            // 非 Deep：Verbose 既不进 analyzer 也不进 viewer。
            var fanOut = new FanOutStage(writer, analyzer, viewer, meters: null, modeProvider: () => CaptureMode.Operational);
            for (var i = 0; i < 10; i++)
            {
                var verbose = D2TestSupport.Event(i + 1, RobocopyEvents.FileAttemptObserved, DeliveryClass.Verbose);
                D2TestSupport.Route(fanOut, new BranchItem(verbose, 200));
            }

            Thread.Sleep(100);
            Assert.Equal(0, Volatile.Read(ref analyzerSeen));
            Assert.Equal(0, Volatile.Read(ref viewerSeen));
            Assert.Equal(10, fanOut.Stats().VerboseSkippedForAnalyzer);

            // Deep：Verbose 仍不进 analyzer（规则只看 Operational 证据），但会进 viewer
            // —— Deep 模式存在的意义就是"现在把每一行都给我看"。
            var deepFanOut = new FanOutStage(writer, analyzer, viewer, meters: null, modeProvider: () => CaptureMode.Deep);
            for (var i = 0; i < 5; i++)
            {
                var verbose = D2TestSupport.Event(100 + i, RobocopyEvents.FileAttemptObserved, DeliveryClass.Verbose);
                D2TestSupport.Route(deepFanOut, new BranchItem(verbose, 200));
            }

            Assert.True(D2TestSupport.WaitUntil(() => Volatile.Read(ref viewerSeen) >= 5),
                $"Deep 模式下 Verbose 必须进 viewer，实际 {Volatile.Read(ref viewerSeen)}");
            Assert.Equal(0, Volatile.Read(ref analyzerSeen));
        }
        finally
        {
            foreach (var b in new[] { writer, analyzer, viewer })
                D2TestSupport.Stop(b, 300);
        }
    }

    [Fact]
    public async Task ShutdownReportsAbandonedItemsInsteadOfPretendingDrained()
    {
        var loss = new LossLedger();
        var health = new DiagnosticHealth();
        var gate = new ManualResetEventSlim(false);

        var branch = Branch("writer", capacity: 64, byteBudget: 1_000_000, loss, health,
            (_, ct) => { gate.Wait(TimeSpan.FromSeconds(30), ct); return default; });
        branch.Start(CancellationToken.None);

        for (var i = 0; i < 20; i++)
        {
            var item = new BranchItem(D2TestSupport.Event(i + 1), 200);
            branch.TryAccept(in item);
        }

        var result = await branch.StopAsync(TimeSpan.FromMilliseconds(100));
        Assert.True(result.Abandoned > 0, "预算到期时必须如实报告被放弃的条目数");
        Assert.Contains(loss.Snapshot().Records, r => r.ReasonCode == "shutdown-budget-exhausted");
        gate.Set();
    }
}