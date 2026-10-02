using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PCMig.Diagnostics;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using PCMig.Diagnostics.Export;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// ★ D6.3 §7「自检健康 / 维护存活」★
///
/// 独立审计 P1-2 的红灯是这句话：**诊断自己已经坏掉了，快照却仍然报"健康 + 证据完整"**。
/// 三条根因在本文件里逐条变成规格：
///
///   Fixture 2（P1-2-①）：analyzer 消费者抛异常（sink 故障）—— 一条事件被吞掉 = 那条证据不存在。
///                        旧实现 `SinkFaults` 既不进 `IsDegraded` 也不进 `EvidenceComplete`，
///                        于是 `SinkFaults=1` 的同时还能宣称"健康 + 完整"。
///   Fixture 2b（P1-2-②）：维护定时循环只有**一根** try/catch —— 任何一个维护步骤抛异常，
///                        整个调度就此静默死亡（此后不再有到期判定、不再有刷盘）。
///                        现在每一步各自隔离，并区分"某一步失败"（不健康）与"调度死亡"（证据不完整）。
///   Fixture 2c（P1-2-③）：期望跟踪器用裸 `Dictionary` 被两条线程读写 —— 并发写可能自旋不返回，
///                        把整个维护循环拖死（在 `D63MaintenanceSurvivalTests` 与跟踪器测试中覆盖）。
///
/// 另有一条**端到端**的自检故障注入：在真实运行时把下一个段文件名占成目录，
/// 让真实写盘路径失败 —— 此时快照必须说"降级"，并且不得再说"证据完整"。
/// 判据一律走生产投影 <see cref="DiagnosticHealthSnapshot.Capture"/>，测试不另写一套口径。
/// </summary>
public sealed class D63SelfHealthTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-d63-self-" + Guid.NewGuid().ToString("N")[..8]);

    public D63SelfHealthTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* 句柄延迟释放 */ }
    }

    // ───────────────────── Fixture 2：sink 故障必须可见、必须降级 ─────────────────────

    [Fact]
    public void Fixture2_AnalyzerSinkFaultIsVisiblePerBranchAndForbidsHealthy()
    {
        var loss = new LossLedger();
        var health = new DiagnosticHealth();
        var seen = 0;

        var analyzer = new BoundedBranch(
            "analyzer",
            capacity: 64,
            byteBudget: 1_000_000,
            evictsOldest: false,
            loss,
            health,
            (_, _) =>
            {
                Interlocked.Increment(ref seen);
                throw new InvalidOperationException("analyzer-sink-boom");
            });

        analyzer.Start(CancellationToken.None);
        try
        {
            Assert.True(analyzer.TryAccept(new BranchItem(D2TestSupport.Event(1, PersistenceEvents.WriteFailed), 200)));
            Assert.True(D2TestSupport.WaitUntil(() => analyzer.Stats().SinkFaults == 1, 5000),
                $"消费者抛异常后必须记下 sink 故障：seen={Volatile.Read(ref seen)} faults={analyzer.Stats().SinkFaults}");

            // ★ 三个数字必须**在每个分支上**各自可见（审计要求的 Accepted / ProcessedSuccessfully / ProcessingFailed）：
            //   `Processed` 在 finally 里前进，所以它同时包含成功与失败两种结局 —— 相减才是真话。
            var stats = analyzer.Stats();
            Assert.Equal(1, stats.Accepted);
            Assert.Equal(1, stats.ProcessingFailed);
            Assert.Equal(0, stats.ProcessedSuccessfully);
            Assert.Equal(1, health.SinkFaults);

            // ★ 判据走生产投影：被吞掉的那条事件等于不存在 ⇒ 既不健康、也不完整。
            var snapshot = DiagnosticHealthSnapshot.Capture(health, loss.Snapshot());
            Assert.True(snapshot.IsDegraded,
                $"sink 故障已经发生，快照却仍宣称健康：sink={snapshot.SinkFaults} degraded={snapshot.StorageDegraded}");
            Assert.False(snapshot.EvidenceComplete,
                $"sink 故障已经发生（一条证据被吞掉），快照却仍宣称证据完整：sink={snapshot.SinkFaults}");
        }
        finally
        {
            D2TestSupport.Stop(analyzer, 300);
        }
    }

    // ───────────────── 维护故障的两种性质：不健康 ≠ 证据不完整 ─────────────────

    [Fact]
    public void MaintenanceFaultDegradesHealthButOnlyADeadSchedulerMakesEvidenceIncomplete()
    {
        var health = new DiagnosticHealth();
        var loss = new LossLedger();

        var healthy = DiagnosticHealthSnapshot.Capture(health, loss.Snapshot());
        Assert.False(healthy.IsDegraded);
        Assert.True(healthy.EvidenceComplete);
        Assert.True(healthy.MaintenanceAlive);
        Assert.Equal(0, healthy.MaintenanceFaults);

        // 维护的**某一步**抛异常：必须可见、必须降级（粘性），但那一步失败本身没有让已有证据消失。
        health.MarkMaintenanceFault("flight-tick", new InvalidOperationException("boom"));

        var faulted = DiagnosticHealthSnapshot.Capture(health, loss.Snapshot());
        Assert.True(faulted.IsDegraded, "维护步骤失败却仍宣称健康");
        Assert.True(faulted.MaintenanceAlive, "调度还在跑，不得被标成死掉");
        Assert.Equal(1, faulted.MaintenanceFaults);
        Assert.Contains("flight-tick", faulted.LastMaintenanceFaultReason ?? string.Empty);
        Assert.Contains("InvalidOperationException", faulted.LastMaintenanceFaultReason ?? string.Empty);
        Assert.True(faulted.EvidenceComplete, "某一步失败 ≠ 已有证据缺失，这两件事必须分开说");

        // 维护调度整体死亡：以后不会再有任何到期判定与刷盘 ⇒ 谁都不许再说"证据完整"。
        health.MarkMaintenanceStopped("timer-loop:InvalidOperationException");

        var dead = DiagnosticHealthSnapshot.Capture(health, loss.Snapshot());
        Assert.True(dead.IsDegraded);
        Assert.False(dead.MaintenanceAlive, "调度已经死了，却仍宣称存活");
        Assert.False(dead.EvidenceComplete, "调度已经死了（后续判定根本不会再发生），却仍宣称证据完整");
        Assert.Contains("timer-loop", dead.LastMaintenanceFaultReason ?? string.Empty);
    }

    // ────────────────── 端到端：真实写盘路径失败 ⇒ 降级 + 不完整 ──────────────────

    /// <summary>
    /// 自检故障注入（真实运行时不改一行产品代码）：
    /// 把**下一个**段文件名占成目录 ⇒ 真实 writer 的 `EnsureOpen` 必然抛异常。
    /// 这不是"人造 DTO"，而是真实存储路径真的坏掉。
    /// </summary>
    [Fact]
    public void SelfHealth_RealWriterStorageFaultCannotStillReadHealthyAndComplete()
    {
        var runtime = DiagnosticRuntime.Start(new DiagnosticRuntimeOptions
        {
            StorageRoot = Path.Combine(_root, "selfhealth", "Diagnostics"),
            InitialMode = CaptureMode.Operational,
            AppVersion = "d63-test",
            ShutdownBudgetMs = 5000,
            WriterFlushIntervalMs = 1,
            SegmentMaxBytes = 1,        // 每条事件后都封段 ⇒ 下一个段文件名可被精确预测
        }, out _);
        Assert.NotNull(runtime.Store);

        var sessionDir = runtime.Store!.SessionDir;
        var eventsDir = Path.Combine(sessionDir, "events");

        try
        {
            Publish(runtime, "before");
            Assert.True(D2TestSupport.WaitUntil(() => File.Exists(Path.Combine(eventsDir, "events-0001.jsonl")), 5000),
                $"第一条事件没有落成 events-0001.jsonl：{Describe(eventsDir)}");

            // ★ 注入：把**下一个**段文件名占成目录（`NextSegmentIndex` 只看文件，所以它仍会选这个号）。
            //   段号在真实运行中会自己前进，所以这里重算并可重试；重试必定推进（失败即说明 writer 已经先用了这个号）。
            var sabotaged = false;
            for (var attempt = 0; attempt < 20 && !sabotaged; attempt++)
            {
                var next = Path.Combine(eventsDir, $"events-{MaxSegmentIndex(eventsDir) + 1:0000}.jsonl");
                try
                {
                    Directory.CreateDirectory(next);
                    sabotaged = true;
                }
                catch (IOException)
                {
                    // writer 已经先行打开并占用了这个段号：换下一个号。
                }
            }

            Assert.True(sabotaged, $"没能占住下一个段文件名：{Describe(eventsDir)}");

            for (var i = 0; i < 30; i++) Publish(runtime, "after-" + i);

            Assert.True(D2TestSupport.WaitUntil(() => runtime.Health.StorageDegraded, 10_000),
                $"真实写盘失败没有被记账：{Describe(eventsDir)}");

            var snapshot = runtime.GetHealthSnapshot();
            Assert.True(snapshot.StorageDegraded, "存储失败未被标成降级");
            Assert.True(snapshot.IsDegraded, $"存储已降级却仍宣称健康：reason={snapshot.LastStorageReason}");
            Assert.False(snapshot.EvidenceComplete,
                $"落盘失败的事件已经进丢失台账（epoch={snapshot.LossEpoch} dropped={snapshot.EventsDropped}），却仍宣称证据完整");
            Assert.True(snapshot.EventsDropped >= 1 || snapshot.LossEpoch > 0,
                $"落盘失败的事件没有进丢失台账：dropped={snapshot.EventsDropped} epoch={snapshot.LossEpoch}");
            Assert.NotNull(snapshot.LastStorageReason);

            // 外部真话：故障是"某个段文件名是个目录"造成的，而它确实是目录、不是文件。
            var blocker = Directory.EnumerateDirectories(eventsDir, "events-*.jsonl").FirstOrDefault();
            Assert.NotNull(blocker);
            Assert.False(File.Exists(blocker));
            Assert.True(File.Exists(Path.Combine(eventsDir, "events-0001.jsonl")));
        }
        finally
        {
            D2TestSupport.Shutdown(runtime);
            D2TestSupport.Dispose(runtime);
        }
    }

    // ───────────────────── 正向对照：干净会话不得谎报降级/停摆 ─────────────────────

    [Fact]
    public void SelfHealth_MaintenanceReallyRunsAndTheCleanPathIsNotFalselyDegraded()
    {
        var runtime = DiagnosticRuntime.Start(new DiagnosticRuntimeOptions
        {
            StorageRoot = Path.Combine(_root, "liveness", "Diagnostics"),
            InitialMode = CaptureMode.Operational,
            AppVersion = "d63-test",
            ShutdownBudgetMs = 5000,
            WriterFlushIntervalMs = 1,
        }, out _);
        Assert.NotNull(runtime.Store);

        try
        {
            for (var i = 0; i < 20; i++) Publish(runtime, "e" + i);

            Assert.True(D2TestSupport.WaitUntil(() => runtime.Health.LastSuccessfulMaintenanceUnixMs > 0, 5000),
                "维护调度从未成功跑过一拍");
            var before = runtime.Health.LastSuccessfulMaintenanceUnixMs;
            Assert.True(D2TestSupport.WaitUntil(() => runtime.Health.LastSuccessfulMaintenanceUnixMs > before, 5000),
                "维护调度没有继续推进（被某一拍拖死了）");

            var snapshot = runtime.GetHealthSnapshot();
            Assert.True(snapshot.MaintenanceAlive, "运行时维护调度仍在跑，却被标成死掉");
            Assert.Equal(0, snapshot.MaintenanceFaults);
            Assert.Null(snapshot.LastMaintenanceFaultReason);
            Assert.False(snapshot.IsDegraded,
                $"干净会话不得谎报降级：reason={snapshot.LastStorageReason} sink={snapshot.SinkFaults} dropped={snapshot.EventsDropped}");
            Assert.True(snapshot.EvidenceComplete, "干净会话不得谎报证据不完整");
        }
        finally
        {
            D2TestSupport.Shutdown(runtime);
            D2TestSupport.Dispose(runtime);
        }
    }

    // ───────────────── Fixture 19（R-2）：规则内部故障必须进健康与证据判定 ─────────────────

    /// <summary>
    /// ★ R-2 收口（D6.3 剩余风险关闭轮）★ 规则抛异常原来只进 `rules.faults`（导出快照 / 诊断中心计数器），
    /// **不进健康、不进证据完整性判定** ⇒ 规则每条都炸时包仍写 `degraded:false` +
    /// `evidenceComplete:true` + `Complete`。异常被隔离是"不许拖垮 writer"，不是"等于没发生"：
    /// 规则没跑成 ⇒ 它负责的那类事实没被判读。
    ///
    /// 本夹具同时保留正对照（故障之前必须健康且完整），否则"变红"可能只是把一切都判成坏的假修复。
    /// </summary>
    [Fact]
    public void Fixture19_RuleFaultMustReachSelfHealthAndForbidCompleteEvidence()
    {
        var runtime = DiagnosticRuntime.Start(new DiagnosticRuntimeOptions
        {
            StorageRoot = Path.Combine(_root, "rulefault", "Diagnostics"),
            InitialMode = CaptureMode.Operational,
            AppVersion = "d63-test",
            ShutdownBudgetMs = 5000,
            WriterFlushIntervalMs = 1,
        }, out _);
        Assert.NotNull(runtime.Store);

        try
        {
            Publish(runtime, "rule-fault-baseline");
            Assert.True(D2TestSupport.WaitUntil(() => runtime.Health.EventsWritten >= 1, 5000));

            // 正对照：故障之前必须"健康 + 证据完整"。
            var clean = runtime.GetHealthSnapshot();
            Assert.False(clean.IsDegraded, "干净会话不得谎报降级");
            Assert.True(clean.EvidenceComplete, "干净会话不得谎报证据不完整");
            Assert.Equal(0, clean.RuleFaults);

            // ① 规则内部故障必须真的接到健康通道上（未接线时这里是 null ⇒ 红灯）。
            var hook = runtime.RuleEngine.OnRuleFault;
            Assert.True(hook is not null,
                "RuleEngine.OnRuleFault 没有接到诊断自身健康：规则全炸时包仍会写 Complete");

            hook!("rule:ACCESS_DENIED:InvalidOperationException");

            // ② 投影唯一且如实：同一个快照里既不健康、也不完整。
            var after = runtime.GetHealthSnapshot();
            Assert.Equal(1, after.RuleFaults);
            Assert.Equal("rule:ACCESS_DENIED:InvalidOperationException", after.LastRuleFaultReason);
            Assert.True(after.IsDegraded, "规则内部故障必须让健康降级");
            Assert.False(after.EvidenceComplete, "规则没判读成功 ⇒ 不许宣称证据完整");

            // ③ 导出包不许 Complete，也不许 packageEvidenceComplete=true。
            var outcome = new DiagnosticPackageExporter().Export(new DiagnosticExportRequest
            {
                SessionDir = runtime.Store!.SessionDir,
                OutputDirectory = Path.Combine(_root, "rulefault", "out"),
                Health = after,
                Cutoff = runtime.PrepareExportCutoff(TimeSpan.FromSeconds(3)),
                IncludeFlightWindows = false,
            });
            Assert.True(outcome.Succeeded, outcome.FailureReason);

            using var zip = System.IO.Compression.ZipFile.OpenRead(outcome.ZipPath!);
            var entry = zip.GetEntry("summary.json");
            Assert.NotNull(entry);
            using var reader = new StreamReader(entry!.Open(), System.Text.Encoding.UTF8);
            var summary = System.Text.Json.JsonDocument.Parse(reader.ReadToEnd()).RootElement.Clone();
            Assert.NotEqual("Complete", summary.GetProperty("evidenceCompleteness").GetString());
            Assert.False(summary.GetProperty("packageEvidenceComplete").GetBoolean());
        }
        finally
        {
            D2TestSupport.Shutdown(runtime);
            D2TestSupport.Dispose(runtime);
        }
    }

    // ───────────────────────────────── 助手 ─────────────────────────────────

    /// <summary>走真实入口投递一条 Operational 事件（过滤/序号/字节闸门/丢弃记账都在产品路径上）。</summary>
    private static void Publish(DiagnosticRuntime runtime, string reason)
        => runtime.Publisher.TryPublish(new DiagnosticEventDraft(
            PersistenceEvents.WriteFailed,
            DiagnosticContext.Root(runtime.SessionId, "D63-Self").WithJob("JOB-D63-SELF"),
            new PstWritePayload("Receipt", "Move", true, reason),
            Outcome: DiagnosticOutcome.Failed,
            ErrorDomain: ErrorDomain.Managed,
            ExceptionType: "IOException",
            Message: "d63-self-health"));

    /// <summary>当前最大的**真段**号（只看文件：目录不算，这正是注入能生效的原因）。</summary>
    private static int MaxSegmentIndex(string eventsDir)
    {
        var max = 0;
        foreach (var file in Directory.EnumerateFiles(eventsDir, "events-*.jsonl"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var dash = name.LastIndexOf('-');
            if (dash < 0) continue;
            if (int.TryParse(name.Substring(dash + 1), out var index) && index > max) max = index;
        }

        return max;
    }

    private static string Describe(string dir)
    {
        if (!Directory.Exists(dir)) return dir + " (不存在)";
        return dir + " => " + string.Join(", ", Directory.EnumerateFileSystemEntries(dir));
    }
}