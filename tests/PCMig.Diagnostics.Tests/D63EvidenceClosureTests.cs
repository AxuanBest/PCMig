using System;
using System.IO;
using PCMig.Diagnostics;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// ★ D6.3 §13「红灯 Fixture 必须保留并反转」★
///
/// 本文件把**独立审计（GPT6 Reliability Audit）的红灯反例**逐条变成回归测试，而不是另写一批
/// "看起来类似"的绿测试去替代它们。红灯在这里是**规格**，不是垃圾：
///
///   Fixture 1（P1-1）：事件流 {1, 3}、缺 2 —— 旧实现取 max(sequence)=3，
///                      于是得到 acknowledged / pending=0 / anyTailLoss=false / coverageEnd=3
///                      的**假 Complete**；正确行为是连续水位只能到 1，且必须知道 missing=2。
///   Fixture 4（P1-1/D-Q2）：损坏行跳过 + 已知 tail loss ⇒ 包不得宣称 Complete（WP D 收口）。
///
/// 这里实现的是 Fixture 1 的水位语义与"已知丢弃 ≠ 未知缺口"的口径；其余红灯（sink 故障、
/// flight partial、unsupported version、Verify 四态）由各自工作包的回归测试承担。
/// </summary>
public sealed class D63EvidenceClosureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-d63-" + Guid.NewGuid().ToString("N")[..8]);

    public D63EvidenceClosureTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* 句柄延迟释放 */ }
    }

    // ───────────────────────── Fixture 1：{1,3} 缺 2 ─────────────────────────

    [Fact]
    public void Fixture1_SequenceGapStopsTheContiguousWatermark()
    {
        var ledger = new SequenceLedger();
        ledger.SettleWritten(1);
        ledger.SettleWritten(3);

        Assert.Equal(1, ledger.Contiguous);        // ★ 旧实现会在这里说 3
        Assert.Equal(3, ledger.SettledMax);        // 最大序号仍如实记录（只作展示，不作完整性判据）
        Assert.Equal(1, ledger.UnknownCount);      // 明确知道缺 1 条
        Assert.Equal(2, ledger.FirstUnknown);      // 缺的就是 2
        Assert.True(ledger.HasUnknownGap);
        Assert.Equal(2, ledger.SettledCount);
    }

    [Fact]
    public void Fixture1b_TheHoleMustActuallyArriveBeforeTheWatermarkMoves()
    {
        var ledger = new SequenceLedger();
        ledger.SettleWritten(1);
        ledger.SettleWritten(3);
        Assert.Equal(1, ledger.Contiguous);

        ledger.SettleWritten(2);                   // 洞补上
        Assert.Equal(3, ledger.Contiguous);        // 现在才允许说"覆盖到 3"
        Assert.Equal(0, ledger.UnknownCount);
        Assert.False(ledger.HasUnknownGap);
    }

    [Fact]
    public void AccountedDropFillsTheHoleButIsNeverWashedAway()
    {
        var ledger = new SequenceLedger();
        ledger.SettleWritten(1);
        ledger.SettleDropped(2);                   // 已入台账的丢弃 ⇒ 不是"未知缺口"
        ledger.SettleWritten(3);

        Assert.Equal(3, ledger.Contiguous);        // 水位可以跨过它
        Assert.Equal(0, ledger.UnknownCount);      // 因为它**已经被解释过**
        Assert.Equal(1, ledger.DroppedCount);      // 但丢弃计数留着（不洗白）
        Assert.Equal(2, ledger.WrittenCount);
        Assert.False(ledger.HasUnknownGap);
    }

    [Fact]
    public void GapTrackingOverflowMustSayIDontKnowRatherThanClaimComplete()
    {
        var ledger = new SequenceLedger();

        // 序号 2..N+1 全到齐，只有 1 一直没来 ⇒ 水位停在 0，而待跟踪集合溢出。
        for (var i = 2; i <= SequenceLedger.BeyondCapacity + 2; i++) ledger.SettleWritten(i);

        Assert.Equal(0, ledger.Contiguous);
        Assert.True(ledger.TrackingOverflowed);
        Assert.True(ledger.HasUnknownGap);         // 说不清就**不许**说完整
    }

    [Fact]
    public void NonPositiveSequencesAreIgnored()
    {
        var ledger = new SequenceLedger();
        ledger.SettleWritten(0);                   // 家族级/未知来源的丢弃记 0，不能污染水位
        ledger.SettleDropped(0);
        ledger.SettleWritten(1);

        Assert.Equal(1, ledger.Contiguous);
        Assert.Equal(1, ledger.SettledCount);      // 只有真实到达的 1 号进入结算（0 号被忽略）
        Assert.False(ledger.HasUnknownGap);
    }

    // ─────────────── runtime 级：已知进丢弃不许变成未知缺口，且仍须 degraded ───────────────

    [Fact]
    public void Runtime_AccountedIngressDropKeepsTheWatermarkContiguousYetStillDegradesHealth()
    {
        var runtime = DiagnosticRuntime.Start(new DiagnosticRuntimeOptions
        {
            StorageRoot = Path.Combine(_root, "store-drop"),
            InitialMode = CaptureMode.Operational,
            AppVersion = "d63-test",
            ShutdownBudgetMs = 5000,
            MaxEventBytes = 2048,                  // 单条上限压到 2 KB，便于制造"已知丢弃"
        }, out var degraded);
        Assert.NotNull(runtime.Store);

        try
        {
            var before = runtime.Health.EventsProduced;

            Publish(runtime, "before");                                     // 小事件：正常
            Publish(runtime, new string('x', 8192));                        // 超大载荷：被入口拒收并记账
            Publish(runtime, "after");                                      // 小事件：正常

            var produced = runtime.Health.EventsProduced;
            // ★ 三条都分配了序号：丢弃发生在序号分配**之后**（否则"被拒收"就等于"没发生过"）。
            Assert.True(produced >= before + 3, $"只有 {produced - before} 条被分配序号，期望 >= 3");

            Assert.True(D2TestSupport.WaitUntil(() => runtime.Health.PayloadRejected >= 1, 5000),
                "超大载荷没有被入口拒收（PayloadRejected 未增长）");
            Assert.True(D2TestSupport.WaitUntil(() => runtime.ContiguousSequence >= produced, 5000),
                $"连续水位={runtime.ContiguousSequence}，期望 >= {produced}（丢弃必须已被结算，而不是留下未知缺口）");

            // ★ 已知丢弃把洞"解释掉"了：水位连续、没有未知缺口、也没有跟踪溢出。
            Assert.Equal(0, runtime.UnknownGapCount);
            Assert.False(runtime.GapTrackingOverflowed);
            Assert.Equal(runtime.SettledMaxSequence, runtime.ContiguousSequence);

            // ★ 但"解释掉了"不等于"没发生"：健康与证据完整性必须仍然说实话。
            var health = runtime.GetHealthSnapshot();
            Assert.True(health.EventsDropped >= 1, $"丢弃未进入丢失台账：dropped={health.EventsDropped}");
            Assert.True(health.IsDegraded, "丢弃已经入账，却仍宣称健康");
            Assert.False(health.EvidenceComplete, "丢弃已经入账，却仍宣称证据完整");
        }
        finally
        {
            D2TestSupport.Shutdown(runtime);
            D2TestSupport.Dispose(runtime);
        }
    }

    // ───────────────────── 截止点口径：不许与"证据连续"自相矛盾 ─────────────────────

    [Fact]
    public void Runtime_CutoffNeverSaysAcknowledgedWhileEvidenceIsNotContiguous()
    {
        const int published = 40;
        var runtime = StartRuntime("cutoff-contiguous");
        try
        {
            for (var i = 0; i < published; i++) Publish(runtime, "e" + i);
            var produced = runtime.Health.EventsProduced;
            Assert.True(D2TestSupport.WaitUntil(() => runtime.ContiguousSequence >= produced, 10_000),
                $"连续水位={runtime.ContiguousSequence}，期望 >= {produced}");

            var cutoff = runtime.PrepareExportCutoff(TimeSpan.FromSeconds(3));

            Assert.Equal("acknowledged", cutoff.FlushStatus);
            Assert.True(cutoff.IsEvidenceContiguous);
            Assert.Equal(0, cutoff.GapCount);
            Assert.Equal(0, cutoff.PendingAtCutoff);
            Assert.False(cutoff.AnyTailLoss);

            // ★ Cutoff 的"覆盖终点"必须是**连续水位**，不是"见过的最大序号"。
            Assert.Equal(runtime.ContiguousSequence, cutoff.CutoffSequence);
            Assert.Equal(cutoff.CutoffSequence, cutoff.CoverageEndSequence);
            Assert.Equal(cutoff.SettledMaxSequence, cutoff.CutoffSequence);
        }
        finally
        {
            D2TestSupport.Shutdown(runtime);
            D2TestSupport.Dispose(runtime);
        }
    }

    [Fact]
    public void Runtime_CutoffWithZeroBudgetCannotPretendToBeContiguous()
    {
        var runtime = StartRuntime("cutoff-zero");
        try
        {
            for (var i = 0; i < 400; i++) Publish(runtime, "z" + i);

            // 0 预算：不等排空。要么真的都写完了，要么必须如实说"尾巴没落完"。
            var cutoff = runtime.PrepareExportCutoff(TimeSpan.Zero);

            if (cutoff.FlushStatus == "acknowledged")
            {
                Assert.Equal(0, cutoff.PendingAtCutoff);
                Assert.True(cutoff.IsEvidenceContiguous);
            }
            else
            {
                Assert.NotEqual("acknowledged", cutoff.FlushStatus);
            }

            // 无论哪条路：不允许出现"自相矛盾的完整"。
            Assert.False(cutoff.IsEvidenceContiguous && (cutoff.AnyTailLoss || cutoff.GapCount > 0 || cutoff.TrackingOverflowed));
        }
        finally
        {
            D2TestSupport.Shutdown(runtime);
            D2TestSupport.Dispose(runtime);
        }
    }

    // ───────────────────────────────── 助手 ─────────────────────────────────

    private DiagnosticRuntime StartRuntime(string tag, int events = 0)
    {
        var runtime = DiagnosticRuntime.Start(new DiagnosticRuntimeOptions
        {
            StorageRoot = Path.Combine(_root, tag, "Diagnostics"),
            InitialMode = CaptureMode.Operational,
            AppVersion = "d63-test",
            ShutdownBudgetMs = 5000,
        }, out _);
        Assert.NotNull(runtime.Store);
        for (var i = 0; i < events; i++) Publish(runtime, "e" + i);
        return runtime;
    }

    /// <summary>
    /// 投递一条 Operational 事件。**故意走真实入口（Publisher.TryPublish）**：
    /// 过滤、序号分配、字节闸门、丢弃记账都必须走产品路径，不能在测试里绕过。
    /// </summary>
    private static void Publish(DiagnosticRuntime runtime, string reason)
        => runtime.Publisher.TryPublish(new DiagnosticEventDraft(
            PersistenceEvents.WriteFailed,
            DiagnosticContext.Root(runtime.SessionId, "D63").WithControl("Step1.Connect"),
            new PstWritePayload("Receipt", "Move", true, reason),
            Outcome: DiagnosticOutcome.Failed,
            ErrorDomain: ErrorDomain.Managed,
            ExceptionType: "IOException",
            Message: "d63-fixture"));
}