// ══════════════════════════════════════════════════════════════════════════════
// ★ UI Closure 2026-10-05（§2 P0 / §8-C）★ Resume 显示连续性回归
// ══════════════════════════════════════════════════════════════════════════════
//  真机现场：暂停时 42.9% → 点「恢复任务」后立刻掉到 ~24.8% → 过一会又回 42.9%。
//  根因（不是 UI）：从"已暂停且这一次运行已收尾"恢复走的是**新一轮 RunAsync**，它只从
//    `Status == Completed` 的回执重建权威进度（TransferOrchestrator.RunAsync 的 baseBytes，
//    `state.CompletedBytes = baseBytes`），而暂停时用户看到的分子含 in-flight 已确认字节
//    （PollProgressAsync 的 `state.CompletedBytes = truth.DisplayedTransferredBytes`）
//    ⇒ 新一轮从 committed 起算，显示值先跳到 24.8%，再由 robocopy 续传逐段追回 42.9%。
//
//  修复口径（分层，绝不可合并）：
//    ① Committed / Durable Progress —— 权威 = Completed Receipt（业务：skip / CompletedObjects /
//       最终判定）。本测试**断言它不被显示层污染**（ResumeDisplayFloor 只改 Percent 与
//       DisplayedTransferredBytes，CommittedBytes 原样）。
//    ② Resume Presentation Floor —— 只抬高 UI 显示；引擎 raw 追上 floor 即自动解除。
//
//  测试形式：ResumeDisplayFloor 是**纯逻辑、零 WinUI 依赖**（定义在
//    src\PCMig.WinUI\Presentation\MigrationSessionViewModel.cs 内，由 PCMig.Core.Tests.csproj
//    源码链入）⇒ 这里可以直接 new 出来做行为级断言（不是文本扫描）。
//    另加少量源码契约：锁住 VM 的接入点（Arm 只在 ResumeAsync、Clear 只在"非恢复"运行、
//    ApplySnapshot 的显示真值单一来源）。
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

    // ────────────────────────── ① 精确复现：42.9% → raw 24.8% → 追上 42.9% ──────────────────────────

    [Fact]
    public void ResumeFloor_ReproducesThe429To248FieldScenario_AndNeverLetsUiDrop()
    {
        var floor = new ResumeDisplayFloor();
        // 暂停时用户已经看到 429（committed 248 + in-flight 已确认 181）。
        var pausedTruth = Raw(committedBytes: 248, inFlightConfirmedBytes: 181);
        Assert.Equal(429, pausedTruth.DisplayedTransferredBytes);
        Assert.True(floor.Arm(JobId, pausedTruth.DisplayedTransferredBytes));

        // 恢复后的第一个 raw 样本：新一轮只从 Completed 回执重建 ⇒ 248（24.8%）。
        var first = floor.Apply(Raw(248, 0), JobPhase.Running, JobId);
        Assert.True(first.FloorActive);
        Assert.False(first.ClearedThisCall);
        Assert.Equal(248, first.RawDisplayedBytes);                             // raw 真值如实保留
        Assert.Equal(429, first.Effective!.DisplayedTransferredBytes);          // 显示不得倒退
        Assert.Equal(42.9, first.Effective.Percent, 1);

        // 续传追赶：248 → 300 → 380 —— raw 在涨、显示纹丝不动（仍 429）。
        foreach (var raw in new long[] { 248, 300, 380 })
        {
            var step = floor.Apply(Raw(raw, 0), JobPhase.Running, JobId);
            Assert.True(step.FloorActive, $"raw={raw} 时 floor 应仍生效");
            Assert.Equal(429, step.Effective!.DisplayedTransferredBytes);
            Assert.True(step.Effective.DisplayedTransferredBytes >= 429, "显示值在任何一步都不得下降");
        }

        // raw 追上 floor（429）：floor 自动解除，显示完全跟随引擎真值。
        var caught = floor.Apply(Raw(429, 0), JobPhase.Running, JobId);
        Assert.False(caught.FloorActive);
        Assert.True(caught.ClearedThisCall);
        Assert.Equal("truth-caught-up", caught.ClearReason);
        Assert.Equal(429, caught.Effective!.DisplayedTransferredBytes);

        // 之后正常前进（450），floor 不再回头干预。
        var after = floor.Apply(Raw(450, 0), JobPhase.Running, JobId);
        Assert.False(after.FloorActive);
        Assert.Equal(450, after.Effective!.DisplayedTransferredBytes);
        Assert.False(floor.IsActive);
    }

    [Fact]
    public void ResumeFloor_NeverTouchesCommittedBytes_BusinessAuthorityStaysWithReceipts()
    {
        var floor = new ResumeDisplayFloor();
        Assert.True(floor.Arm(JobId, 429));

        var step = floor.Apply(Raw(committedBytes: 248, inFlightConfirmedBytes: 0), JobPhase.Running, JobId);

        // 显示被抬高到 429，但**业务权威（committed）仍是 248** —— 它只可能来自 Completed 回执。
        Assert.Equal(429, step.Effective!.DisplayedTransferredBytes);
        Assert.Equal(248, step.Effective.CommittedBytes);
        Assert.Equal(0, step.Effective.InFlightConfirmedBytes);
    }

    [Fact]
    public void ResumeFloor_IsNotArmedWithoutARealCandidate()
    {
        var floor = new ResumeDisplayFloor();
        Assert.False(floor.Arm(JobId, 0));
        Assert.False(floor.IsActive);

        var step = floor.Apply(Raw(248, 0), JobPhase.Running, JobId);
        Assert.False(step.FloorActive);
        Assert.Equal(248, step.Effective!.DisplayedTransferredBytes);   // 没有候选就完全跟随真值
    }

    [Fact]
    public void ResumeFloor_ArmNeverLowersAnExistingFloor()
    {
        var floor = new ResumeDisplayFloor();
        Assert.True(floor.Arm(JobId, 429));
        Assert.False(floor.Arm(JobId, 300));            // 更低候选不覆盖
        Assert.Equal(429, floor.FloorBytes);
    }

    // ────────────────────────── ② 正常前进 / 暂停态 ──────────────────────────

    [Fact]
    public void ResumeFloor_NeverLowersEngineTruth_WhenRawIsAhead()
    {
        var floor = new ResumeDisplayFloor();
        Assert.True(floor.Arm(JobId, 300));

        var step = floor.Apply(Raw(429, 0), JobPhase.Running, JobId);
        Assert.False(step.FloorActive);
        Assert.True(step.ClearedThisCall);
        Assert.Equal(429, step.Effective!.DisplayedTransferredBytes);   // 绝不把引擎真值压低到 floor
    }

    [Fact]
    public void ResumeFloor_KeepsFloorWhilePaused_ButShowsEngineTruth()
    {
        var floor = new ResumeDisplayFloor();
        Assert.True(floor.Arm(JobId, 429));

        var paused = floor.Apply(Raw(248, 0), JobPhase.Paused, JobId);
        Assert.True(paused.FloorActive);                                 // 等一下次恢复
        Assert.False(paused.ClearedThisCall);
        Assert.Equal(248, paused.Effective!.DisplayedTransferredBytes);  // 暂停态显示引擎真值，不伪造
    }

    // ────────────────────────── ③ 允许回退的场景必须"有解释"──────────────────────────

    [Fact]
    public void ResumeFloor_ReleasesOnExplicitRollback_AndExplainsIt()
    {
        var floor = new ResumeDisplayFloor();
        Assert.True(floor.Arm(JobId, 429));

        var rolledBack = floor.Apply(Raw(300, 0, retryState: RetryState.RollingBack), JobPhase.Running, JobId);
        Assert.False(rolledBack.FloorActive);
        Assert.True(rolledBack.ClearedThisCall);
        Assert.Equal("explicit-rollback", rolledBack.ClearReason);
        Assert.Equal(300, rolledBack.Effective!.DisplayedTransferredBytes);   // 显式回滚允许下降
    }

    [Fact]
    public void ResumeFloor_KeepsFloorDuringAutomaticRetry_AndOnlyExplicitRollbackReleases()
    {
        // ★ 真机复验返修（2026-10-05 run2）★ 恢复后的正常 catch-up 期内引擎会短暂进入 Retrying
        //   （真机日志 20:33:48.498 曾因此丢掉 floor）——自动重试不是"允许显示回退"的语义事件。
        var floor = new ResumeDisplayFloor();
        Assert.True(floor.Arm(JobId, 429));

        var retry = floor.Apply(Raw(300, 0, retryState: RetryState.Retrying), JobPhase.Running, JobId);
        Assert.True(retry.FloorActive);
        Assert.Equal(string.Empty, retry.ClearReason);
        Assert.Equal(429, retry.Effective!.DisplayedTransferredBytes);   // 显示仍被托住，不倒退

        // 显式回滚才是可解释的回退（原因码 explicit-rollback，显示跟随 raw）。
        var rollback = floor.Apply(Raw(248, 0, retryState: RetryState.RollingBack), JobPhase.Running, JobId);
        Assert.False(rollback.FloorActive);
        Assert.Equal("explicit-rollback", rollback.ClearReason);
        Assert.Equal(248, rollback.Effective!.DisplayedTransferredBytes);
    }

    [Fact]
    public void ResumeFloor_ReleasesOnJobChange()
    {
        var floor = new ResumeDisplayFloor();
        Assert.True(floor.Arm(JobId, 429));

        var other = floor.Apply(Raw(248, 0), JobPhase.Running, "JOB-OTHER-0001");
        Assert.False(other.FloorActive);
        Assert.Equal("job-changed", other.ClearReason);
        Assert.Equal(248, other.Effective!.DisplayedTransferredBytes);
    }

    [Fact]
    public void ResumeFloor_ReleasesWhenPhaseIsNoLongerRunningOrPaused()
    {
        foreach (var phase in new[] { JobPhase.Completed, JobPhase.CompletedWithErrors, JobPhase.Failed, JobPhase.Interrupted })
        {
            var floor = new ResumeDisplayFloor();
            Assert.True(floor.Arm(JobId, 429));

            var step = floor.Apply(Raw(248, 0), phase, JobId);
            Assert.False(step.FloorActive, $"phase={phase} 时不应继续抬高显示");
            Assert.Equal("phase-" + phase, step.ClearReason);
            Assert.Equal(248, step.Effective!.DisplayedTransferredBytes);
        }
    }

    [Fact]
    public void ResumeFloor_ClearReportsWhetherItWasActive()
    {
        var floor = new ResumeDisplayFloor();
        Assert.False(floor.Clear("new-run"));       // 本来就没生效 ⇒ 没发生解除
        Assert.True(floor.Arm(JobId, 429));
        Assert.True(floor.Clear("new-run"));        // 真的从生效变为解除
        Assert.False(floor.IsActive);
        Assert.Equal("new-run", floor.LastClearReason);
    }

    // ────────────────────────── ④ raw 回退检测（§5 的诊断依据）──────────────────────────

    [Fact]
    public void RawRegression_IsDetectedBetweenSamples_EvenWithoutAnyFloor()
    {
        var floor = new ResumeDisplayFloor();
        Assert.False(floor.IsActive);

        floor.ResetRawTracking(JobId);
        floor.Apply(Raw(429, 0), JobPhase.Running, JobId);
        Assert.Equal(0, floor.LastRawRegressionBytes);

        floor.Apply(Raw(248, 0), JobPhase.Running, JobId);
        Assert.Equal(181, floor.LastRawRegressionBytes);
    }

    [Fact]
    public void RawRegression_IgnoresToleranceAndJobSwitch()
    {
        var floor = new ResumeDisplayFloor();
        floor.ResetRawTracking(JobId);

        floor.Apply(Raw(429, 0), JobPhase.Running, JobId);
        floor.Apply(Raw(428, 0), JobPhase.Running, JobId);          // 1 字节抖动 = 容差内，不算回退
        Assert.Equal(0, floor.LastRawRegressionBytes);

        floor.Apply(Raw(300, 0), JobPhase.Running, JobId);          // 真回退
        Assert.Equal(128, floor.LastRawRegressionBytes);

        floor.ResetRawTracking(JobId);
        floor.Apply(Raw(429, 0), JobPhase.Running, JobId);
        floor.Apply(Raw(248, 0), JobPhase.Running, "JOB-OTHER-0001");   // 换任务 ⇒ 不可比，不算回退
        Assert.Equal(0, floor.LastRawRegressionBytes);
    }

    // ────────────────────────── ⑤ VM 接入点契约（单一写入者 / 单一显示来源）──────────────────────────

    [Fact]
    public void SessionViewModel_ArmsFloorOnlyOnResume_AndClearsItOnFreshRuns()
    {
        var vm = StripComments(ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MigrationSessionViewModel.cs"));

        // Arm 只在 ResumeAsync 里出现（且只有"用户发起的恢复"那一次运行携带它）。
        Assert.Contains("_resumeDisplayFloor.Arm(ctx.JobId, floorCandidate)", vm, StringComparison.Ordinal);
        Assert.Contains("resumeContinuity: true", vm, StringComparison.Ordinal);

        // "非恢复"的运行（RunAsync / Repair）必须显式解除 floor。
        Assert.Contains("if (!resumeContinuity && _resumeDisplayFloor.Clear(\"new-run\"))", vm, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionViewModel_DisplayTruthComesFromContinuity_WhileLastTruthStaysRaw()
    {
        var vm = StripComments(ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MigrationSessionViewModel.cs"));

        // 显示真值单一来源 = continuity.Effective；raw 真值单独保留给诊断。
        Assert.Contains("var rawTruth = s.Truth;", vm, StringComparison.Ordinal);
        Assert.Contains("var continuity = _resumeDisplayFloor.Apply(rawTruth, s.Phase, Ctx?.JobId);", vm, StringComparison.Ordinal);
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
        //   它们必须读显示真值（floor 托底过），读 raw 会让暂停 → 恢复变成 99.9% → 0.0% 的倒退。
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