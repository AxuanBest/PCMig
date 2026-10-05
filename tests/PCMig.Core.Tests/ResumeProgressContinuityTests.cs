// ══════════════════════════════════════════════════════════════════════════════
// ★ Round-2 2026-10-05（§2 P0 / §9）★ 进度显示连续性回归（ContinuationDisplayState）
// ══════════════════════════════════════════════════════════════════════════════
//  视频证据（用户录屏 2026-10-05，帧级复核）：
//    · 43.7 s 显示 24.8%（43.95 GB / 176.9 GB），鼠标在底栏「停止」，点击；
//    · 44.0 s 立刻变成 0.0% / 0 B（**显示连续性失败** —— 旧实现把 Interrupted 当解除条件）；
//    · 52.0 s 才跳回 24.8%，紧接着 47.3%；54.9 s → 62.3%。
//  旧实现（ResumeDisplayFloor）只覆盖"暂停 → 恢复"，且只在 Paused 态记候选
//  （StopAsync 从不捕获检查点）⇒ 停止路径完全裸奔。
//
//  修复口径（分层，绝不可合并）：
//    ① Committed / Durable Progress —— 权威 = Completed Receipt（业务：skip / CompletedObjects /
//       最终判定）。本测试**断言它不被显示层污染**（ContinuationDisplayState 只改 Percent 与
//       DisplayedTransferredBytes，CommittedBytes 原样）。
//    ② Continuation Display State —— 只抬高 UI 显示（Live / Frozen / CatchingUp）；
//       引擎 raw 追平高水位即自动解除。
//
//  测试形式：ContinuationDisplayState 是**纯逻辑、零 WinUI 依赖**（定义在
//    src\PCMig.WinUI\Presentation\MigrationSessionViewModel.cs 内，由 PCMig.Core.Tests.csproj
//    源码链入）⇒ 这里可以直接 new 出来做行为级断言（不是文本扫描）。
//    另加少量源码契约：锁住 VM 的接入点（检查点只在 Pause / Stop / Resume 的用户动作路径里捕获、
//    Clear 只在"非恢复"运行、ApplySnapshot 的显示真值单一来源）。
// ══════════════════════════════════════════════════════════════════════════════

using System;
using System.IO;
using System.Linq;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using PCMig.WinUI.Presentation;
using Xunit;

namespace PCMig.Core.Tests;

public class ResumeProgressContinuityTests
{
    private const string JobId = "JOB-20261005-030000-ui01";
    private const long Planned = 1000;
    private const double Percent429 = 42.9;
    private const double Percent248 = 24.8;

    /// <summary>构造一个引擎 raw 真值快照：displayed = committed + inFlight（与 ProgressTruthSnapshot.Create 同口径）。</summary>
    private static ProgressTruthSnapshot Raw(
        long committedBytes,
        long inFlightConfirmedBytes,
        string objectId = "obj-1",
        RetryState retryState = RetryState.None,
        long attemptEpoch = 1)
        => ProgressTruthSnapshot.Create(
            plannedBytes: Planned,
            committedBytes: committedBytes,
            inFlightConfirmedBytes: inFlightConfirmedBytes,
            currentObjectId: objectId,
            currentObjectPlannedBytes: 0,
            currentObjectConfirmedBytes: 0,
            attemptEpoch: attemptEpoch,
            retryState: retryState,
            speedBytesPerSecond: 0,
            settled: false,
            paused: false,
            targetStatTrusted: false,
            inFlightSource: inFlightConfirmedBytes > 0 ? ProgressTruthSource.WorkerIoCounters : ProgressTruthSource.None,
            timestampUtc: DateTime.UtcNow);

    // ────────────────────────── ① 精确复现：暂停 42.9% → raw 24.8% → 追上 42.9% ──────────────────────────

    [Fact]
    public void ResumeContinuity_ReproducesThe429To248FieldScenario_AndNeverLetsUiDrop()
    {
        var state = new ContinuationDisplayState();
        // 暂停时用户已经看到 429（committed 248 + in-flight 已确认 181）。
        var pausedTruth = Raw(committedBytes: 248, inFlightConfirmedBytes: 181);
        Assert.Equal(429, pausedTruth.DisplayedTransferredBytes);

        // ★ 关键：检查点在**用户点暂停之前**捕获（PauseAsync 的点击路径）。
        Assert.True(state.CaptureCheckpoint(JobId, pausedTruth.DisplayedTransferredBytes, Percent429, 0, "pause-requested"));
        Assert.Equal(ContinuationMode.Frozen, state.Mode);

        // 暂停态：raw 落到 248 ⇒ 显示**冻结**在 429（Round-2 语义：暂停不得导致显示倒退）。
        var paused = state.Apply(Raw(248, 0), JobPhase.Paused, JobId);
        Assert.Equal(ContinuationMode.Frozen, paused.Mode);
        Assert.Equal(248, paused.RawDisplayedBytes);                            // raw 真值如实保留
        Assert.Equal(429, paused.Effective!.DisplayedTransferredBytes);         // 显示不得倒退
        Assert.Equal(Percent429, paused.Effective.Percent, 1);

        // 用户点恢复 ⇒ 进入追赶模式。
        Assert.True(state.BeginCatchUp(JobId));
        Assert.Equal(ContinuationMode.CatchingUp, state.Mode);

        // 恢复后的第一个 raw 样本：新一轮只从 Completed 回执重建 ⇒ 248（24.8%）。
        var first = state.Apply(Raw(248, 0), JobPhase.Running, JobId);
        Assert.Equal(ContinuationMode.CatchingUp, first.Mode);
        Assert.False(first.ClearedThisCall);
        Assert.Equal(248, first.RawDisplayedBytes);
        Assert.Equal(429, first.Effective!.DisplayedTransferredBytes);
        Assert.Equal(Percent429, first.Effective.Percent, 1);

        // 续传追赶：300 → 380 —— raw 在涨、显示纹丝不动（仍 429）。
        foreach (var raw in new long[] { 300, 380 })
        {
            var step = state.Apply(Raw(raw, 0), JobPhase.Running, JobId);
            Assert.Equal(ContinuationMode.CatchingUp, step.Mode);
            Assert.Equal(429, step.Effective!.DisplayedTransferredBytes);
            Assert.True(step.Effective.DisplayedTransferredBytes >= 429, "显示值在任何一步都不得下降");
        }

        // raw 追上高水位（429）：自动解除，显示完全跟随引擎真值。
        var caught = state.Apply(Raw(429, 0), JobPhase.Running, JobId);
        Assert.Equal(ContinuationMode.Live, caught.Mode);
        Assert.True(caught.ClearedThisCall);
        Assert.Equal("truth-caught-up", caught.ClearReason);
        Assert.Equal(429, caught.Effective!.DisplayedTransferredBytes);

        // 之后正常前进（450），不再回头干预。
        var after = state.Apply(Raw(450, 0), JobPhase.Running, JobId);
        Assert.Equal(ContinuationMode.Live, after.Mode);
        Assert.Equal(450, after.Effective!.DisplayedTransferredBytes);
        Assert.False(state.IsActive);
    }

    [Fact]
    public void ResumeContinuity_NeverTouchesCommittedBytes_BusinessAuthorityStaysWithReceipts()
    {
        var state = new ContinuationDisplayState();
        Assert.True(state.CaptureCheckpoint(JobId, 429, Percent429, 0, "pause-requested"));

        var step = state.Apply(Raw(committedBytes: 248, inFlightConfirmedBytes: 0), JobPhase.Running, JobId);

        // 显示被抬高到 429，但**业务权威（committed）仍是 248** —— 它只可能来自 Completed 回执。
        Assert.Equal(429, step.Effective!.DisplayedTransferredBytes);
        Assert.Equal(248, step.Effective.CommittedBytes);
        Assert.Equal(0, step.Effective.InFlightConfirmedBytes);
    }

    [Fact]
    public void ResumeContinuity_IsNotFrozenWithoutARealCandidate()
    {
        var state = new ContinuationDisplayState();
        Assert.False(state.CaptureCheckpoint(JobId, 0, 0, 0, "pause-requested"));
        Assert.False(state.IsActive);

        var step = state.Apply(Raw(248, 0), JobPhase.Running, JobId);
        Assert.Equal(ContinuationMode.Live, step.Mode);
        Assert.Equal(248, step.Effective!.DisplayedTransferredBytes);   // 没有候选就完全跟随真值
    }

    [Fact]
    public void CaptureCheckpoint_NeverLowersAnExistingHighWater()
    {
        var state = new ContinuationDisplayState();
        Assert.True(state.CaptureCheckpoint(JobId, 429, Percent429, 0, "pause-requested"));
        Assert.False(state.CaptureCheckpoint(JobId, 300, 30.0, 0, "user-stop"));   // 更低候选不覆盖
        Assert.Equal(429, state.HighWaterBytes);
        Assert.Equal(Percent429, state.HighWaterPercent);
    }

    // ────────────────────────── ② 正常前进 / 暂停 / 停止 ──────────────────────────

    [Fact]
    public void ResumeContinuity_NeverLowersEngineTruth_WhenRawIsAhead()
    {
        var state = new ContinuationDisplayState();
        Assert.True(state.CaptureCheckpoint(JobId, 300, 30.0, 0, "pause-requested"));

        var step = state.Apply(Raw(429, 0), JobPhase.Running, JobId);
        // ★ Round-2 修正（run F 真机缺陷 2026-10-05 04:38:24）★
        //   冻结来自用户动作（Pause / Stop）时，raw 追平**不解除**冻结：
        //   否则"Stop 后 raw 先反超、再被引擎重建为 0"时托底已经丢掉，显示会掉回 0。
        //   解除只发生在 Resume 之后的追赶模式（CatchingUp 追平 ⇒ Live）。
        Assert.Equal(ContinuationMode.Frozen, step.Mode);
        Assert.False(step.ClearedThisCall);
        Assert.Equal(429, step.Effective!.DisplayedTransferredBytes);   // 绝不把引擎真值压低到高水位
        Assert.Equal(429, state.HighWaterBytes);                        // 高水位被正常前进推进到 raw 位置
    }

    [Fact]
    public void Paused_FreezesDisplay_EvenWhenRawEqualsHighWater()
    {
        // ★ Round-2 语义 ★ 暂停态**保持冻结**（旧实现此时显示 raw 并解除）：
        //   恢复时引擎会把 raw 重建为更低（只算 Completed 回执），提前跟随就等于预告一次倒退。
        var state = new ContinuationDisplayState();
        Assert.True(state.CaptureCheckpoint(JobId, 429, Percent429, 0, "pause-requested"));

        var paused = state.Apply(Raw(429, 0), JobPhase.Paused, JobId);
        Assert.Equal(ContinuationMode.Frozen, paused.Mode);
        Assert.False(paused.ClearedThisCall);
        Assert.Equal(429, paused.Effective!.DisplayedTransferredBytes);
        Assert.True(state.IsActive);
    }

    // ────────────────────────── ③ 允许回退的场景必须"有解释"──────────────────────────

    [Fact]
    public void ResumeContinuity_ReleasesOnExplicitRollback_AndExplainsIt()
    {
        var state = new ContinuationDisplayState();
        Assert.True(state.CaptureCheckpoint(JobId, 429, Percent429, 0, "pause-requested"));

        var rolledBack = state.Apply(Raw(300, 0, retryState: RetryState.RollingBack), JobPhase.Running, JobId);
        Assert.Equal(ContinuationMode.Live, rolledBack.Mode);
        Assert.True(rolledBack.ClearedThisCall);
        Assert.Equal("explicit-rollback", rolledBack.ClearReason);
        Assert.Equal(300, rolledBack.Effective!.DisplayedTransferredBytes);   // 显式回滚允许下降
    }

    [Fact]
    public void ResumeContinuity_KeepsFrozenDisplayDuringAutomaticRetry()
    {
        // ★ 真机复验返修（2026-10-05 run2）★ 恢复后的正常 catch-up 期内引擎会短暂进入 Retrying
        //   （真机日志 20:33:48.498 曾因此丢掉 floor）——自动重试不是"允许显示回退"的语义事件。
        var state = new ContinuationDisplayState();
        Assert.True(state.CaptureCheckpoint(JobId, 429, Percent429, 0, "pause-requested"));

        var retry = state.Apply(Raw(300, 0, retryState: RetryState.Retrying), JobPhase.Running, JobId);
        Assert.Equal(ContinuationMode.Frozen, retry.Mode);
        Assert.Equal(string.Empty, retry.ClearReason);
        Assert.Equal(429, retry.Effective!.DisplayedTransferredBytes);   // 显示仍被托住，不倒退

        // 显式回滚才是可解释的回退（原因码 explicit-rollback，显示跟随 raw）。
        var rollback = state.Apply(Raw(248, 0, retryState: RetryState.RollingBack), JobPhase.Running, JobId);
        Assert.Equal(ContinuationMode.Live, rollback.Mode);
        Assert.Equal("explicit-rollback", rollback.ClearReason);
        Assert.Equal(248, rollback.Effective!.DisplayedTransferredBytes);
    }

    [Fact]
    public void ResumeContinuity_ReleasesOnJobChange()
    {
        var state = new ContinuationDisplayState();
        Assert.True(state.CaptureCheckpoint(JobId, 429, Percent429, 0, "pause-requested"));

        var other = state.Apply(Raw(248, 0), JobPhase.Running, "JOB-OTHER-0001");
        Assert.Equal(ContinuationMode.Live, other.Mode);
        Assert.Equal("job-changed", other.ClearReason);
        Assert.Equal(248, other.Effective!.DisplayedTransferredBytes);
    }

    [Fact]
    public void Completed_ReleasesFrozenDisplay_SoFinalSettlementFollowsEngine()
    {
        // 只有真结算（Completed / CompletedWithErrors）才允许显示跟随引擎 —— 此时 100% 是真的。
        foreach (var phase in new[] { JobPhase.Completed, JobPhase.CompletedWithErrors })
        {
            var state = new ContinuationDisplayState();
            Assert.True(state.CaptureCheckpoint(JobId, 429, Percent429, 0, "pause-requested"));

            var step = state.Apply(Raw(1000, 0), phase, JobId);
            Assert.Equal(ContinuationMode.Live, step.Mode);
            Assert.True(step.ClearedThisCall);
            Assert.Equal("phase-" + phase, step.ClearReason);
            Assert.Equal(1000, step.Effective!.DisplayedTransferredBytes);
        }
    }

    [Fact]
    public void Interrupted_KeepsFrozenDisplay_BecauseStopIsResumable()
    {
        // ★ Round-2 P0 核心 ★ 用户点「停止」⇒ Core 收尾为 Interrupted 并从 committed 回执重建真值；
        //   显示必须**停在检查点**，绝不掉回 committed-only 值（视频：24.8% → 0.0% 就是旧行为）。
        foreach (var phase in new[] { JobPhase.Interrupted, JobPhase.Failed, JobPhase.Canceled, JobPhase.AwaitingReview })
        {
            var state = new ContinuationDisplayState();
            Assert.True(state.CaptureCheckpoint(JobId, 248, Percent248, 0, "user-stop"));

            var step = state.Apply(Raw(0, 0), phase, JobId);
            Assert.Equal(ContinuationMode.Frozen, step.Mode);
            Assert.False(step.ClearedThisCall);
            Assert.Equal(string.Empty, step.ClearReason);
            Assert.Equal(0, step.RawDisplayedBytes);                              // raw 如实为 0
            Assert.Equal(248, step.Effective!.DisplayedTransferredBytes);         // 显示停住 = 24.8%
            Assert.Equal(Percent248, step.Effective.Percent, 1);
        }
    }

    [Fact]
    public void Clear_ReportsWhetherItWasActive()
    {
        var state = new ContinuationDisplayState();
        Assert.False(state.Clear("new-run"));       // 本来就没生效 ⇒ 没发生解除
        Assert.True(state.CaptureCheckpoint(JobId, 429, Percent429, 0, "pause-requested"));
        Assert.True(state.Clear("new-run"));        // 真的从生效变为解除
        Assert.False(state.IsActive);
        Assert.Equal("new-run", state.LastClearReason);
        Assert.Equal(0, state.HighWaterBytes);      // 高水位必须一并清掉（否则新任务会继承旧托底）
    }

    // ────────────────────────── ④ raw 回退检测（§5 / §7 的诊断依据）──────────────────────────

    [Fact]
    public void RawRegression_IsDetectedBetweenSamples_EvenWithoutAnyFrozenDisplay()
    {
        var state = new ContinuationDisplayState();
        Assert.False(state.IsActive);

        state.ResetRawTracking(JobId);
        state.Apply(Raw(429, 0), JobPhase.Running, JobId);
        Assert.Equal(0, state.LastRawRegressionBytes);

        state.Apply(Raw(248, 0), JobPhase.Running, JobId);
        Assert.Equal(181, state.LastRawRegressionBytes);
    }

    [Fact]
    public void RawRegression_IgnoresToleranceAndJobSwitch()
    {
        var state = new ContinuationDisplayState();
        state.ResetRawTracking(JobId);

        state.Apply(Raw(429, 0), JobPhase.Running, JobId);
        state.Apply(Raw(428, 0), JobPhase.Running, JobId);          // 1 字节抖动 = 容差内，不算回退
        Assert.Equal(0, state.LastRawRegressionBytes);

        state.Apply(Raw(300, 0), JobPhase.Running, JobId);          // 真回退
        Assert.Equal(128, state.LastRawRegressionBytes);

        state.ResetRawTracking(JobId);
        state.Apply(Raw(429, 0), JobPhase.Running, JobId);
        state.Apply(Raw(248, 0), JobPhase.Running, "JOB-OTHER-0001");   // 换任务 ⇒ 不可比，不算回退
        Assert.Equal(0, state.LastRawRegressionBytes);
    }

    // ────────────────────────── ⑤ VM 接入点契约（单一写入者 / 单一显示来源）──────────────────────────

    [Fact]
    public void SessionViewModel_CapturesCheckpointBeforeUserPauseAndStop()
    {
        var vm = StripComments(ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MigrationSessionViewModel.cs"));

        // ★ Round-2 §2.1 第 2/4 条 ★ 检查点必须在**用户动作之前**捕获：
        //   PauseAsync 在写 pause.request 之前、StopAsync 在 _cts.Cancel() 之前。
        Assert.Contains("CaptureContinuationCheckpoint(\"pause-requested\")", vm, StringComparison.Ordinal);
        Assert.Contains("CaptureContinuationCheckpoint(\"user-stop\")", vm, StringComparison.Ordinal);
        Assert.Contains("\"resume-requested\"", vm, StringComparison.Ordinal);
        Assert.Contains("BeginCatchUp(ctx.JobId)", vm, StringComparison.Ordinal);

        // 检查点捕获必须先于取消令牌（否则 Core 可能已经用 committed-only 值回写一次）。
        var stopIndex = vm.IndexOf("CaptureContinuationCheckpoint(\"user-stop\")", StringComparison.Ordinal);
        var cancelIndex = vm.IndexOf("_cts?.Cancel();", stopIndex, StringComparison.Ordinal);
        Assert.True(stopIndex > 0 && cancelIndex > stopIndex, "StopAsync 必须先捕获检查点再取消令牌");
    }

    [Fact]
    public void SessionViewModel_ClearsContinuityOnFreshRunsOnly()
    {
        var vm = StripComments(ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MigrationSessionViewModel.cs"));

        // Arm 只在 ResumeAsync 里出现（且只有"用户发起的恢复"那一次运行携带它）。
        Assert.Contains("resumeContinuity: true", vm, StringComparison.Ordinal);

        // "非恢复"的运行（RunAsync / Repair）必须显式解除连续性。
        Assert.Contains("if (!resumeContinuity && _continuation.Clear(\"new-run\"))", vm, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionViewModel_DisplayTruthComesFromContinuity_WhileLastTruthStaysRaw()
    {
        var vm = StripComments(ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MigrationSessionViewModel.cs"));

        // 显示真值单一来源 = continuity.Effective；raw 真值单独保留给诊断。
        Assert.Contains("var rawTruth = s.Truth;", vm, StringComparison.Ordinal);
        Assert.Contains("var continuity = _continuation.Apply(rawTruth, s.Phase, Ctx?.JobId);", vm, StringComparison.Ordinal);
        Assert.Contains("LastTruth = rawTruth;", vm, StringComparison.Ordinal);
        Assert.Contains("var displayTruth = continuity.Effective;", vm, StringComparison.Ordinal);
        Assert.Contains("ProgressText = $\"{Format.Bytes(displayedBytes)} / {Format.Bytes(plannedBytes)}\";", vm, StringComparison.Ordinal);

        // 旧写法（每处各自读 s.Truth）必须已经消失：否则顶栏/底栏/文案会出现两套口径。
        Assert.DoesNotContain("Percent = s.Truth?.Percent", vm, StringComparison.Ordinal);
        Assert.DoesNotContain("var displayedBytes = s.Truth?.DisplayedTransferredBytes", vm, StringComparison.Ordinal);
        Assert.DoesNotContain("var truthSpeed = s.Truth?.SpeedBytesPerSecond", vm, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionViewModel_LogsUnexpectedRegressionAsAnError()
    {
        var vm = StripComments(ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MigrationSessionViewModel.cs"));
        Assert.Contains("UnexpectedProgressRegression", vm, StringComparison.Ordinal);
        Assert.Contains("ProgressRegressionExplained", vm, StringComparison.Ordinal);
        Assert.Contains("ProgressTruthTransition job=", vm, StringComparison.Ordinal);
        Assert.Contains("TransitionJumpThreshold", vm, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionViewModel_ExposesPresentationTruthAsTheUiTruth()
    {
        var vm = StripComments(ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MigrationSessionViewModel.cs"));
        Assert.Contains("public ProgressTruthSnapshot? PresentationTruth", vm, StringComparison.Ordinal);
        Assert.Contains("PresentationTruth = displayTruth;", vm, StringComparison.Ordinal);
    }

    [Fact]
    public void UiConsumers_ReadPresentationTruth_NotRawLastTruth()
    {
        // ★ 真机复验返修（2026-10-05 run2）★ 顶栏与底栏都是"用户看得见的进度"的消费端：
        //   它们必须读显示真值（被高水位托底过），读 raw 会让停止/恢复变成 24.8% → 0.0% 的倒退。
        var footer = StripComments(ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml.cs"));
        Assert.Contains("var truth = Session.PresentationTruth;", footer, StringComparison.Ordinal);
        Assert.DoesNotContain("var truth = Session.LastTruth;", footer, StringComparison.Ordinal);

        var step3 = StripComments(ReadRepoFile("src", "PCMig.WinUI", "Views", "Step3ProgressPage.xaml.cs"));
        Assert.Contains("var truth = session.PresentationTruth;", step3, StringComparison.Ordinal);
        Assert.DoesNotContain("var truth = session.LastTruth;", step3, StringComparison.Ordinal);
    }

    // ────────────────────────── helpers（与既有契约测试同口径）──────────────────────────

    private static string ReadRepoFile(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray()));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string StripComments(string text)
        => string.Join('\n', text.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
}