// ══════════════════════════════════════════════════════════════════════════════
// ★ Round-2 2026-10-05（§9 必补测试）★ ContinuationDisplayState 行为级回归
// ══════════════════════════════════════════════════════════════════════════════
//  数据量级取自用户真机（176.9 GB 计划；43.95 GB = 24.8%；75.9 GB ≈ 42.9%）。
//  六个测试名逐字对应 Round-2 方案的 §9 清单：
//    Stop_FreezesDisplayedProgress_BeforeInterruptedRawDrops
//    Stop_Resume_DoesNotRegress_24_8_To_0
//    Pause_Resume_DoesNotRegress_42_9_To_24_8
//    RawCatchUp_ReleasesHighWater
//    NewJob_ResetsHighWater
//    ExplicitRollback_AllowsExplainedRegression
//
//  纪律：这些测试只断言**显示层**（ContinuationDisplayState 的 Effective/Mode/高水位）；
//  它们绝不把 Interrupted 对象说成 Completed，也不写回执 —— 业务权威仍是 Completed Receipt。
// ══════════════════════════════════════════════════════════════════════════════

using System;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using PCMig.WinUI.Presentation;
using Xunit;

namespace PCMig.Core.Tests;

public class ContinuationDisplayStateTests
{
    private const string JobId = "JOB-20261005-120000-r2";
    private const long Planned = 176_900_000_000;          // 176.9 GB
    private const long StoppedBytes = 43_950_000_000;      // 视频里的 43.95 GB（24.8%）
    private const long CommittedAtStop = 43_900_000_000;   // 停止前 committed（≈24.8%）
    private const long CommittedAtPause = 75_900_000_000;  // 暂停前 committed（≈42.9%）

    private static ProgressTruthSnapshot Raw(
        long committedBytes,
        long inFlightConfirmedBytes,
        RetryState retryState = RetryState.None,
        string currentObjectId = "object-000015")
        => ProgressTruthSnapshot.Create(
            plannedBytes: Planned,
            committedBytes: committedBytes,
            inFlightConfirmedBytes: inFlightConfirmedBytes,
            currentObjectId: currentObjectId,
            currentObjectPlannedBytes: 0,
            currentObjectConfirmedBytes: 0,
            attemptEpoch: 1,
            retryState: retryState,
            speedBytesPerSecond: 0,
            settled: false,
            paused: false,
            targetStatTrusted: false,
            inFlightSource: inFlightConfirmedBytes > 0 ? ProgressTruthSource.WorkerIoCounters : ProgressTruthSource.None,
            timestampUtc: DateTime.UtcNow);

    [Fact]
    public void Stop_FreezesDisplayedProgress_BeforeInterruptedRawDrops()
    {
        var state = new ContinuationDisplayState();
        // 用户点「停止」之前捕获检查点（StopAsync 在 _cts.Cancel() 之前调用）。
        Assert.True(state.CaptureCheckpoint(JobId, StoppedBytes, 24.8, 0, "user-stop"));
        Assert.Equal(ContinuationMode.Frozen, state.Mode);
        Assert.Equal(StoppedBytes, state.HighWaterBytes);

        // Core 收尾为 Interrupted，并从 committed 回执重建真值 ⇒ raw 掉到 ~43.9 GB（甚至 0）。
        var interrupted = state.Apply(Raw(CommittedAtStop, 0), JobPhase.Interrupted, JobId);
        Assert.Equal(ContinuationMode.Frozen, interrupted.Mode);
        Assert.False(interrupted.ClearedThisCall);
        Assert.Equal(CommittedAtStop, interrupted.RawDisplayedBytes);      // raw 如实降低
        Assert.True(interrupted.Effective!.DisplayedTransferredBytes >= StoppedBytes,
            "停止后显示分子不得低于用户已经看到的 43.95 GB");
        Assert.True(interrupted.Effective.Percent >= 24.8,
            "停止后显示百分比不得低于用户已经看到的 24.8%");

        // 极端情况：Core 重建为 0（committed-only）也必须停住。
        var zero = state.Apply(Raw(0, 0), JobPhase.Interrupted, JobId);
        Assert.Equal(0, zero.RawDisplayedBytes);
        Assert.Equal(StoppedBytes, zero.Effective!.DisplayedTransferredBytes);
        Assert.True(zero.Effective.Percent >= 24.8);
    }

    [Fact]
    public void Stop_Resume_DoesNotRegress_24_8_To_0()
    {
        var state = new ContinuationDisplayState();
        Assert.True(state.CaptureCheckpoint(JobId, StoppedBytes, 24.8, 0, "user-stop"));

        // 停止后：显示停住（不出现 24.8% → 0.0%）。
        var interrupted = state.Apply(Raw(0, 0), JobPhase.Interrupted, JobId);
        Assert.True(interrupted.Effective!.Percent >= 24.8);

        // 用户点「恢复任务」⇒ 追赶模式。
        Assert.True(state.BeginCatchUp(JobId));
        Assert.Equal(ContinuationMode.CatchingUp, state.Mode);

        // 恢复后前几个 raw 样本仍是 0 / 较低值 —— 显示必须纹丝不动。
        foreach (var raw in new long[] { 0, 4_000_000_000, 20_000_000_000 })
        {
            var step = state.Apply(Raw(raw, 0), JobPhase.Running, JobId);
            Assert.Equal(ContinuationMode.CatchingUp, step.Mode);
            Assert.True(step.Effective!.Percent >= 24.8, $"raw={raw} 时显示仍不得低于 24.8%");
        }

        // raw 追上高水位 ⇒ 自动解除，随后正常前进（视频里的 47.3%）。
        var caught = state.Apply(Raw(StoppedBytes, 0), JobPhase.Running, JobId);
        Assert.Equal(ContinuationMode.Live, caught.Mode);
        Assert.True(caught.ClearedThisCall);
        Assert.Equal("truth-caught-up", caught.ClearReason);

        var forward = state.Apply(Raw(83_670_000_000, 0), JobPhase.Running, JobId);
        Assert.Equal(ContinuationMode.Live, forward.Mode);
        Assert.Equal(83_670_000_000, forward.Effective!.DisplayedTransferredBytes);
        Assert.Equal(47.3, forward.Effective.Percent, 1);
    }

    [Fact]
    public void Pause_Resume_DoesNotRegress_42_9_To_24_8()
    {
        var state = new ContinuationDisplayState();
        // 暂停时用户看到 42.9%：committed 75.9 GB + in-flight 已确认（不足 100 ms 的余量）⇒ 用同一值即可。
        Assert.True(state.CaptureCheckpoint(JobId, CommittedAtPause, 42.9, 0, "pause-requested"));

        // 暂停态：raw 可能就是 committed（75.9 GB）；显示冻结在检查点。
        var paused = state.Apply(Raw(CommittedAtPause, 0), JobPhase.Paused, JobId);
        Assert.Equal(ContinuationMode.Frozen, paused.Mode);
        Assert.True(paused.Effective!.Percent >= 42.9);

        // 恢复：新一轮只从 Completed 回执重建 ⇒ raw 掉到 24.8%（43.9 GB）。
        Assert.True(state.BeginCatchUp(JobId));
        var first = state.Apply(Raw(43_900_000_000, 0), JobPhase.Running, JobId);
        Assert.Equal(43_900_000_000, first.RawDisplayedBytes);
        Assert.True(first.Effective!.Percent >= 42.9, "恢复后第一个样本不得掉到 24.8%");

        // 续传追赶期间持续不倒退，raw 追上后解除并前进。
        var mid = state.Apply(Raw(60_000_000_000, 0), JobPhase.Running, JobId);
        Assert.True(mid.Effective!.Percent >= 42.9);

        var caught = state.Apply(Raw(CommittedAtPause, 0), JobPhase.Running, JobId);
        Assert.Equal(ContinuationMode.Live, caught.Mode);
        Assert.Equal("truth-caught-up", caught.ClearReason);
        Assert.Equal(42.9, caught.Effective!.Percent, 1);
    }

    [Fact]
    public void RawCatchUp_ReleasesHighWater()
    {
        var state = new ContinuationDisplayState();
        Assert.True(state.CaptureCheckpoint(JobId, StoppedBytes, 24.8, 0, "user-stop"));
        state.BeginCatchUp(JobId);

        // 尚未追上：保持追赶模式，高水位仍在。
        var before = state.Apply(Raw(StoppedBytes - 1, 0), JobPhase.Running, JobId);
        Assert.Equal(ContinuationMode.CatchingUp, before.Mode);
        Assert.True(state.IsActive);
        Assert.Equal(StoppedBytes, state.HighWaterBytes);

        // 追平（等于高水位也算）：一次性解除，并清空高水位，之后显示完全跟随 raw。
        var caught = state.Apply(Raw(StoppedBytes, 0), JobPhase.Running, JobId);
        Assert.Equal(ContinuationMode.Live, caught.Mode);
        Assert.True(caught.ClearedThisCall);
        Assert.False(state.IsActive);
        Assert.Equal(0, state.HighWaterBytes);

        // 再出现一次真实的 raw 降低（例如引擎重建）时不再托底 —— 但会被诊断捕获为 raw 回退。
        var after = state.Apply(Raw(StoppedBytes - 5_000_000, 0), JobPhase.Running, JobId);
        Assert.Equal(ContinuationMode.Live, after.Mode);
        Assert.Equal(StoppedBytes - 5_000_000, after.Effective!.DisplayedTransferredBytes);
        Assert.True(state.LastRawRegressionBytes >= 5_000_000);
    }

    /// <summary>
    /// ★ Round-2 真机缺陷回归（run F，2026-10-05 04:38:24.383 → .394）★
    /// 用户点「停止」之后引擎 raw 先反超高水位（本机 42 GB 在 6 秒内传完，raw 直接到 99.9%），
    /// 随后又被重建为 0。冻结期的高水位**不得**被 raw 追平解除，
    /// 否则随后的 raw 回落就再无托底 —— 那正是用户视频里 24.8% → 0.0% 的路径。
    /// </summary>
    [Fact]
    public void FrozenFromStop_RawCatchesUpThenDrops_DisplayStaysAtHighWater()
    {
        var state = new ContinuationDisplayState();
        Assert.True(state.CaptureCheckpoint(JobId, StoppedBytes, 24.8, 0, "user-stop"));
        Assert.Equal(ContinuationMode.Frozen, state.Mode);

        // ① Stop 之后 raw 反超高水位（真机：10387193856 → 45097156608 只用了 38 ms）。
        var boosted = StoppedBytes + 10_000_000_000;
        var over = state.Apply(Raw(boosted, 0), JobPhase.Running, JobId);
        Assert.Equal(boosted, over.Effective!.DisplayedTransferredBytes);   // 显示照常跟随更快的 raw
        Assert.Equal(ContinuationMode.Frozen, state.Mode);   // 但冻结不解除
        Assert.False(over.ClearedThisCall);
        Assert.Equal(boosted, state.HighWaterBytes);         // 高水位被正常前进推进到 raw 位置（规则④）

        // ② raw 被重建为 0（中断收尾 / 尚未重建回执）⇒ 显示必须停在用户已看到的高水位，绝不掉到 0。
        var dropped = state.Apply(Raw(0, 0), JobPhase.Interrupted, JobId);
        Assert.Equal(ContinuationMode.Frozen, dropped.Mode);
        Assert.False(dropped.ClearedThisCall);
        Assert.Equal(boosted, dropped.Effective!.DisplayedTransferredBytes);
        Assert.True(dropped.Effective.Percent >= 24.8, "停止后 raw 归零不得让显示掉回 0.0%");
        Assert.Equal(boosted, state.HighWaterBytes);

        // ③ 用户点「恢复任务」⇒ 进入追赶；raw 追平后**才**解除，随后正常前进。
        Assert.True(state.BeginCatchUp(JobId));
        var catching = state.Apply(Raw(boosted - 1_000_000, 0), JobPhase.Running, JobId);
        Assert.Equal(ContinuationMode.CatchingUp, catching.Mode);
        Assert.True(catching.Effective!.Percent >= 24.8);

        var caught = state.Apply(Raw(boosted, 0), JobPhase.Running, JobId);
        Assert.Equal(ContinuationMode.Live, caught.Mode);
        Assert.Equal("truth-caught-up", caught.ClearReason);
        Assert.Equal(0, state.HighWaterBytes);

        var moved = state.Apply(Raw(boosted + 1_000_000_000, 0), JobPhase.Running, JobId);
        Assert.Equal(boosted + 1_000_000_000, moved.Effective!.DisplayedTransferredBytes);
    }

    [Fact]
    public void NewJob_ResetsHighWater()
    {
        var state = new ContinuationDisplayState();
        Assert.True(state.CaptureCheckpoint(JobId, StoppedBytes, 24.8, 0, "user-stop"));
        Assert.True(state.IsActive);

        // 换任务：旧高水位对新任务毫无意义 ⇒ 一次性解除，并且新任务显示从 0 重新长。
        var otherJob = state.Apply(Raw(1_000_000, 0, currentObjectId: "object-000001"), JobPhase.Running, "JOB-OTHER-0001");
        Assert.Equal(ContinuationMode.Live, otherJob.Mode);
        Assert.True(otherJob.ClearedThisCall);
        Assert.Equal("job-changed", otherJob.ClearReason);
        Assert.Equal(0, state.HighWaterBytes);
        Assert.Equal(1_000_000, otherJob.Effective!.DisplayedTransferredBytes);

        // 新任务在更低数值上起步不会被旧高水位托住。
        var fresh = new ContinuationDisplayState();
        var freshStep = fresh.Apply(Raw(1_000_000, 0), JobPhase.Running, "JOB-OTHER-0001");
        Assert.Equal(ContinuationMode.Live, freshStep.Mode);
        Assert.Equal(1_000_000, freshStep.Effective!.DisplayedTransferredBytes);
        Assert.True(freshStep.Effective.Percent < 1.0);
    }

    [Fact]
    public void ExplicitRollback_AllowsExplainedRegression()
    {
        var state = new ContinuationDisplayState();
        // 正常高水位 60%（这里直接用 StoppedBytes 的更高值代表"已经涨到 60 GB"）。
        Assert.True(state.CaptureCheckpoint(JobId, 106_000_000_000, 60.0, 0, "pause-requested"));

        // 引擎显式回滚 ⇒ 允许显示下降，必须带原因码（§2.1 第 7 条）。
        var rolledBack = state.Apply(Raw(88_000_000_000, 0, retryState: RetryState.RollingBack), JobPhase.Running, JobId);
        Assert.Equal(ContinuationMode.Live, rolledBack.Mode);
        Assert.True(rolledBack.ClearedThisCall);
        Assert.Equal("explicit-rollback", rolledBack.ClearReason);
        Assert.Equal(88_000_000_000, rolledBack.Effective!.DisplayedTransferredBytes);
        Assert.True(rolledBack.Effective.Percent < 60.0, "显式回滚允许显示下降（并且这条下降是有解释的）");
        Assert.Equal(0, state.HighWaterBytes);
    }
}