// ============================================================================
//  FIX BATCH 2（PCMig Trust-Critical Recovery）— 暂停 UI 状态机 / 按钮矩阵测试 PU-01…PU-08
// ============================================================================
//
//  ── 为什么这些测试必须存在（真机证据）────────────────────────────────────────
//  JOB-20261004-171522-b785：用户点击「暂停」8 次、8 次请求文件写入成功、引擎 0 次确认，
//  而界面每次都立刻显示"已暂停"（旧实现：`IsPaused = true` + `LastPauseOutcome = Accepted` 硬编码），
//  随后又被下一次快照里的 `IsPaused = s.Phase == JobPhase.Paused` 覆盖回"进行中"。
//  ⇒ 用户看到的是"一会儿已暂停、一会儿进行中、传输始终没停"，而且**无法区分**
//    "点了没反应"、"点了正在办"、"点了但办不成"三种完全不同的处境。
//
//  本文件把 §5(Button Matrix) 的 8 个状态 × 4 个按钮收敛成**可执行断言**，并且钉死三条纪律：
//    ① 点击暂停后 UI 只能说「正在暂停…」——绝不允许乐观谎报"已暂停"；
//    ② 只有引擎真值（ProgressSnapshot.PauseState）能推进到 已暂停 / 暂停失败；
//    ③ 引擎报 PauseFailed 之后，界面**必须**明说"暂停失败，迁移仍在进行"，且不得被后续快照抹掉。
//
//  ── 怎么够得着 WinUI 的类型 ────────────────────────────────────────────────
//  与 A5PresentationRegressionTests 同一策略：`PCMig.Core.Tests.csproj` 把
//  `src\PCMig.WinUI\Presentation\*.cs` **源码链入**编译（含 MigrationSessionViewModel），
//  因此这里测的是**产品真实的会话状态机**，不是替身；传输引擎一律通过产品自带的测试缝
//  `MigrationSessionViewModel.TransferRunner` 注入假实现 ⇒ 本文件永不启动 robocopy、不触网。
//
//  作业目录（PCMIG_JOBS）隔离：与 A5PresentationRegressionTests 同 collection（串行），
//  避免两个类同时改写进程级环境变量造成间歇性失败。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PCMig.Core.Diagnostics;
using PCMig.Core.Jobs;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using PCMig.Diagnostics.Abstractions;
using PCMig.WinUI.Presentation;
using Serilog;
using Xunit;

namespace PCMig.Core.Tests;

[Collection("A5-jobdir-isolation")]
public sealed class PauseUiStateMachineTests : IDisposable
{
    private const string ProbeHost = "PU-PROBE-HOST";

    private readonly string _sandbox;
    private readonly ILogger _log;
    private readonly string? _previousJobsRoot;

    public PauseUiStateMachineTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "pcmig-pause-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
        _previousJobsRoot = Environment.GetEnvironmentVariable("PCMIG_JOBS");
        Environment.SetEnvironmentVariable("PCMIG_JOBS", Path.Combine(_sandbox, "Jobs"));
        _log = new LoggerConfiguration().CreateLogger();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PCMIG_JOBS", _previousJobsRoot);
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    // ────────────────────────────── 夹具 ──────────────────────────────

    private string NewDir(params string[] parts)
    {
        var path = Path.Combine(new[] { _sandbox }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private MigrationSessionViewModel NewSession(ConnectionViewModel? connection = null)
        => new(connection, null, _log);

    private JobContext CreateJob(string jobId, MigrationPlan? plan = null, JobState? state = null)
    {
        var def = new JobDefinition
        {
            JobId = jobId,
            SourceHost = ProbeHost,
            TargetRoot = NewDir("target", jobId),
            Sources = { new SourceSpec { Path = @"\\" + ProbeHost + @"\C$", Kind = ObjectKind.DataVolume } },
            Options = new MigrationOptions { Threads = 16 },
        };
        var ctx = new JobManager(_log).Create(def);
        if (plan is not null) ctx.SavePlan(plan);
        if (state is not null) ctx.SaveState(state);
        return ctx;
    }

    private static MigrationPlan PlanWithOneObject(string sourcePath, long bytes = 4096)
        => new()
        {
            JobId = "plan",
            TotalBytes = bytes,
            Objects =
            {
                new PlannedObject
                {
                    ObjectId = "object-000001",
                    Kind = ObjectKind.DataVolume,
                    SourcePath = sourcePath,
                    TargetPath = sourcePath,
                    EstimatedBytes = bytes,
                    EstimatedFiles = 1,
                },
            },
        };

    private JobState RunningState(string jobId) => new()
    {
        JobId = jobId,
        Phase = JobPhase.Running,
        TotalObjects = 3,
        CompletedObjects = 1,
        TotalBytes = 300L * 1024 * 1024,
        CompletedBytes = 100L * 1024 * 1024,
    };

    /// <summary>构造一个"正在运行"的快照；暂停真值按参数给定（模拟引擎在真实编排器里的上报）。</summary>
    private static ProgressSnapshot Snap(
        JobPhase phase = JobPhase.Running,
        PauseState pauseState = PauseState.None,
        DateTime? requestedAt = null,
        DateTime? achievedAt = null,
        PauseOutcomeKind outcome = PauseOutcomeKind.None,
        string? failureReason = null,
        string message = "正在传输：object-000002")
        => new(phase, 3, 1, 0, 300L * 1024 * 1024, 100L * 1024 * 1024,
            33.3, 12_000_000, 600, "object-000002", @"\\" + ProbeHost + @"\C$\data", message,
            0, 0, false, 0, pauseState, requestedAt, achievedAt, outcome, failureReason);

    private sealed class InlineSyncContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    private static async Task WithInlineSyncContext(Func<Task> body)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        try { await body(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    /// <summary>一次假引擎运行里能观察到的会话状态切片（全部在**引擎执行中**采样，与真实时序一致）。</summary>
    private sealed class Samples
    {
        public int RunCount;
        public bool IsRunningDuringEngine;
        public bool IsPaused;
        public bool IsPausing;
        public bool IsPauseFailed;
        public string PauseButtonText = string.Empty;
        public string StatusMessage = string.Empty;
        public bool CanPause;
        public bool CanResume;
        public bool CanStop;
        public ActionOutcomeDecision PauseOutcome;
        public DateTime? PauseRequestedAt;
        public DateTime? PauseAchievedAt;
        public string? PauseFailureReason;
        public PauseState EnginePauseState;
    }

    private static Samples Capture(MigrationSessionViewModel vm) => new()
    {
        IsRunningDuringEngine = vm.IsRunning,
        IsPaused = vm.IsPaused,
        IsPausing = vm.IsPausing,
        IsPauseFailed = vm.IsPauseFailed,
        PauseButtonText = vm.PauseButtonText,
        StatusMessage = vm.StatusMessage,
        CanPause = vm.CanPause,
        CanResume = vm.CanResume,
        CanStop = vm.CanStop,
        PauseOutcome = vm.LastPauseOutcome,
        PauseRequestedAt = vm.PauseRequestedAt,
        PauseAchievedAt = vm.PauseAchievedAt,
        PauseFailureReason = vm.PauseFailureReason,
        EnginePauseState = vm.EnginePauseState,
    };

    /// <summary>
    /// 跑一次"带暂停脚本"的假运行：<paramref name="body"/> 在引擎内部执行（此刻 IsRunning == true，
    /// 与产品里"用户运行中点暂停"完全同一时序），可用 progress.Report 推进引擎真值。
    /// </summary>
    private async Task<Samples> RunScriptedAsync(
        MigrationSessionViewModel vm,
        Func<JobContext, IProgress<ProgressSnapshot>, Task> body,
        JobPhase result = JobPhase.Paused)
    {
        var s = new Samples();
        vm.TransferRunner = (ctx, hooks, progress, ct, onlyObjectIds, forceRecopy) =>
        {
            s.RunCount++;
            body(ctx, progress).GetAwaiter().GetResult();
            return Task.FromResult(result);
        };
        await WithInlineSyncContext(() => vm.RunAsync(password: null));
        return s;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
        throw new InvalidOperationException("找不到含 PCMig.sln 的仓库根：" + AppContext.BaseDirectory);
    }

    // ══════════════════════════ PU-01 ══════════════════════════

    /// <summary>
    /// PU-01（Trust-Critical）：点击暂停后界面只能说「正在暂停…」，**绝不**谎报"已暂停"。
    /// 旧实现正是在这里写 `IsPaused = true` ⇒ 真机上"界面写着已暂停、传输从未停止"。
    /// </summary>
    [Fact]
    public async Task PU01_ClickPause_NeverClaimsPaused_OnlySaysPausing()
    {
        var src = NewDir("pu01", "src");
        var ctx = CreateJob("JOB-PU-01", plan: PlanWithOneObject(src), state: RunningState("JOB-PU-01"));
        var vm = NewSession();
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        Samples? before = null, after = null;
        await RunScriptedAsync(vm, async (_, _) =>
        {
            before = Capture(vm);
            await vm.PauseAsync();          // ← 用户点击底栏「暂停」
            after = Capture(vm);
        });

        // 点击之前：确实在跑、可以暂停
        Assert.True(before!.IsRunningDuringEngine);
        Assert.True(before.CanPause);
        Assert.Equal("暂停", before.PauseButtonText);

        // 点击之后（引擎尚未表态）：只能"正在暂停…"
        Assert.False(after!.IsPaused);                       // ★ 绝不乐观谎报
        Assert.True(after.IsPausing);
        Assert.False(after.IsPauseFailed);
        Assert.Equal("正在暂停…", after.PauseButtonText);
        Assert.Contains("正在暂停", after.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("已暂停", after.StatusMessage, StringComparison.Ordinal);

        // 按钮矩阵：暂停不可再点（避免叠加意图）；停止仍可用（用户始终有一条真能结束的路）
        Assert.False(after.CanPause);
        Assert.False(after.CanResume);                       // 还没停住，恢复没有意义
        Assert.True(after.CanStop);

        // 请求受理 ≠ 业务达成：此处只允许 Accepted
        Assert.Equal(DiagnosticOutcome.Accepted, after.PauseOutcome.Outcome);
        Assert.Equal("pause-requested", after.PauseOutcome.ReasonCode);

        // 运行以 Phase=Paused 收尾（引擎真值）⇒ 界面保留"已暂停"，用户才点得到「恢复」。
        // 但**绝不伪造**达成时刻：引擎没报 PauseAchievedUtc，界面就必须是 null。
        Assert.Equal(PauseUiState.Paused, vm.PauseUiState);
        Assert.Null(vm.PauseAchievedAt);
    }

    // ══════════════════════════ PU-02 ══════════════════════════

    /// <summary>
    /// PU-02：只有引擎真值能推进到「已暂停」；推进后 Resume 必须可用（**即使那一次运行还活着**：
    /// 暂停达成于对象边界时，编排器正原地等请求文件消失，IsRunning 仍为 true）。
    /// </summary>
    [Fact]
    public async Task PU02_OnlyEngineTruth_CanReachPaused_AndThenResumeIsClickable()
    {
        var src = NewDir("pu02", "src");
        var ctx = CreateJob("JOB-PU-02", plan: PlanWithOneObject(src), state: RunningState("JOB-PU-02"));
        var vm = NewSession();
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        var requestedAt = DateTime.UtcNow;
        var achievedAt = requestedAt.AddMilliseconds(1_200);
        Samples? pausing = null, paused = null;

        await RunScriptedAsync(vm, async (_, progress) =>
        {
            await vm.PauseAsync();
            // 引擎受理（正在停 worker）
            progress.Report(Snap(pauseState: PauseState.Pausing, requestedAt: requestedAt));
            pausing = Capture(vm);
            // 引擎确认 worker 已停住 ⇒ 暂停达成
            progress.Report(Snap(JobPhase.Paused, PauseState.Paused, requestedAt, achievedAt,
                PauseOutcomeKind.Achieved));
            paused = Capture(vm);
        });

        Assert.True(pausing!.IsPausing);
        Assert.False(pausing.IsPaused);

        Assert.True(paused!.IsPaused);
        Assert.False(paused.IsPausing);
        Assert.Equal("已暂停", paused.PauseButtonText);
        Assert.True(paused.CanResume);                       // ★ 引擎已停住 ⇒ 恢复必须可点
        Assert.False(paused.CanPause);
        Assert.Equal(DiagnosticOutcome.Succeeded, paused.PauseOutcome.Outcome);
        Assert.Equal("pause-achieved", paused.PauseOutcome.ReasonCode);
        Assert.Equal(achievedAt, paused.PauseAchievedAt);
        Assert.Equal(PauseState.Paused, paused.EnginePauseState);
    }

    // ══════════════════════════ PU-03 ══════════════════════════

    /// <summary>
    /// PU-03（防假绿核心）：引擎在 SLA 内停不住 ⇒ 界面**必须**明说"暂停失败，迁移仍在进行"，
    /// 并且这件事**不得**被随后到来的普通运行快照抹掉（这是旧实现里"一会儿已暂停一会儿进行中"的同源缺陷）。
    /// </summary>
    [Fact]
    public async Task PU03_EnginePauseFailed_SaysTransferStillRunning_AndIsNeverSilentlyErased()
    {
        var src = NewDir("pu03", "src");
        var ctx = CreateJob("JOB-PU-03", plan: PlanWithOneObject(src), state: RunningState("JOB-PU-03"));
        var vm = NewSession();
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        var requestedAt = DateTime.MinValue;
        Samples? failed = null, laterRunning = null;

        await RunScriptedAsync(vm, async (_, progress) =>
        {
            await vm.PauseAsync();
            // 引擎在本轮**处理请求的那一刻**打时间戳（必须晚于点击，否则会被判成"上一轮的旧结论"）
            requestedAt = DateTime.UtcNow;
            progress.Report(Snap(pauseState: PauseState.PauseFailed, requestedAt: requestedAt,
                outcome: PauseOutcomeKind.Failed, failureReason: "worker-did-not-stop"));
            failed = Capture(vm);
            // 引擎侧真值仍是 PauseFailed（迁移继续进行，暂停没达成）⇒ 下一个快照不得把失败抹成"进行中"
            progress.Report(Snap(pauseState: PauseState.PauseFailed, requestedAt: requestedAt,
                outcome: PauseOutcomeKind.Failed, failureReason: "worker-did-not-stop",
                message: "正在传输：object-000003"));
            laterRunning = Capture(vm);
        }, result: JobPhase.Interrupted);

        Assert.True(failed!.IsPauseFailed);
        Assert.False(failed.IsPaused);
        Assert.False(failed.IsPausing);
        Assert.Contains("暂停失败", failed.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("迁移仍在进行", failed.StatusMessage, StringComparison.Ordinal);
        Assert.Equal("重试暂停", failed.PauseButtonText);
        Assert.True(failed.CanPause);                        // ★ 用户必须有一条重试的路
        Assert.False(failed.CanResume);
        Assert.True(failed.CanStop);
        Assert.Equal(DiagnosticOutcome.Failed, failed.PauseOutcome.Outcome);
        Assert.Equal("pause-failed", failed.PauseOutcome.ReasonCode);

        Assert.True(laterRunning!.IsPauseFailed);            // ★ 未被无声抹掉
        Assert.Contains("暂停失败", laterRunning.StatusMessage, StringComparison.Ordinal);
    }

    // ══════════════════════════ PU-04 ══════════════════════════

    /// <summary>
    /// PU-04：暂停失败后用户点「重试暂停」⇒ 界面必须重新回到「正在暂停…」，
    /// 不能被引擎对**上一轮**的旧失败结论立刻打回"暂停失败"（否则重试看起来毫无作用）。
    /// </summary>
    [Fact]
    public async Task PU04_RetryPause_IgnoresTheStaleFailureOfThePreviousAttempt()
    {
        var src = NewDir("pu04", "src");
        var ctx = CreateJob("JOB-PU-04", plan: PlanWithOneObject(src), state: RunningState("JOB-PU-04"));
        var vm = NewSession();
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        var firstRequestAt = DateTime.UtcNow.AddSeconds(-30);   // 上一轮（失败）的引擎时间戳
        Samples? retried = null;

        await RunScriptedAsync(vm, async (_, progress) =>
        {
            progress.Report(Snap(pauseState: PauseState.PauseFailed, requestedAt: firstRequestAt,
                outcome: PauseOutcomeKind.Failed, failureReason: "worker-did-not-stop"));
            Assert.True(vm.IsPauseFailed);
            Assert.Equal("重试暂停", vm.PauseButtonText);

            await vm.PauseAsync();                           // ← 用户点「重试暂停」
            // 引擎还未来得及处理新一轮 ⇒ 上报的仍是**上一轮**那个失败结论（requestedAt 早于重试时点）
            progress.Report(Snap(pauseState: PauseState.PauseFailed, requestedAt: firstRequestAt,
                outcome: PauseOutcomeKind.Failed, failureReason: "worker-did-not-stop"));
            retried = Capture(vm);
        }, result: JobPhase.Interrupted);

        Assert.True(retried!.IsPausing);                     // ★ 重试后必须显示"正在暂停…"
        Assert.False(retried.IsPauseFailed);
        Assert.Equal("正在暂停…", retried.PauseButtonText);
        Assert.Contains("正在暂停", retried.StatusMessage, StringComparison.Ordinal);
        Assert.False(retried.CanPause);
        Assert.False(retried.CanResume);
    }

    // ══════════════════════════ PU-05 ══════════════════════════

    /// <summary>
    /// PU-05（数据安全相关）：已暂停且**那一次运行还活着**（对象边界原地等待）时点「恢复」，
    /// 必须是"清除请求、让引擎自己继续"，**绝不能**启动第二次运行（那会是同一任务上两个 robocopy）。
    /// </summary>
    [Fact]
    public async Task PU05_ResumeWhileSessionAlive_ClearsRequest_AndNeverStartsASecondRun()
    {
        var src = NewDir("pu05", "src");
        var ctx = CreateJob("JOB-PU-05", plan: PlanWithOneObject(src), state: RunningState("JOB-PU-05"));
        var vm = NewSession();
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        var requestedAt = DateTime.UtcNow;
        var s = new Samples { RunCount = 0 };
        var resumeRanSecondEngine = false;

        vm.TransferRunner = (c, hooks, progress, ct, ids, recopy) =>
        {
            s.RunCount++;
            progress.Report(Snap(JobPhase.Paused, PauseState.Paused, requestedAt,
                requestedAt.AddMilliseconds(800), PauseOutcomeKind.Achieved));
            Assert.True(vm.IsPaused);
            Assert.True(vm.CanResume);
            Assert.True(File.Exists(c.PauseRequestPath));

            // ← 用户点「恢复任务」：会话仍认为自己在跑（IsRunning == true）
            vm.ResumeAsync(password: null).GetAwaiter().GetResult();

            Assert.False(File.Exists(c.PauseRequestPath));    // 请求已清除 ⇒ 引擎会自己继续
            Assert.Equal(PauseUiState.Resuming, vm.PauseUiState);
            Assert.Contains("已恢复", vm.StatusMessage, StringComparison.Ordinal);

            // 引擎继续传输后上报"没有暂停在进行"
            progress.Report(Snap());
            Assert.Equal(PauseUiState.Idle, vm.PauseUiState);
            Assert.Equal("暂停", vm.PauseButtonText);
            Assert.True(vm.CanPause);

            // 第二次调用 ResumeAsync **不得**再启动一次引擎（re-entry 必须被挡在"不启动"这一层）
            vm.ResumeAsync(password: null).GetAwaiter().GetResult();
            resumeRanSecondEngine = s.RunCount > 1;
            return Task.FromResult(JobPhase.Completed);
        };

        await WithInlineSyncContext(() => vm.RunAsync(password: null));

        Assert.Equal(1, s.RunCount);                          // ★ 只有一次运行
        Assert.False(resumeRanSecondEngine);
    }

    // ══════════════════════════ PU-06 ══════════════════════════

    /// <summary>
    /// PU-06：重新载入既有任务时，暂停真值必须来自**持久化真值**（job-state.json），
    /// 而不是猜（旧实现 `IsPaused = Phase == Paused`）。已暂停 ⇒ 可恢复；暂停失败 ⇒ 明说失败且可重试。
    /// </summary>
    [Fact]
    public async Task PU06_AdoptingExistingJob_RebuildsPauseTruthFromPersistedState()
    {
        var src = NewDir("pu06", "src");

        // ① 持久化为"已暂停"
        var achievedAt = DateTime.UtcNow.AddMinutes(-5);
        var pausedCtx = CreateJob("JOB-PU-06-PAUSED", plan: PlanWithOneObject(src), state: new JobState
        {
            JobId = "JOB-PU-06-PAUSED",
            Phase = JobPhase.Paused,
            TotalObjects = 3, CompletedObjects = 1,
            TotalBytes = 300L * 1024 * 1024, CompletedBytes = 100L * 1024 * 1024,
            PauseState = PauseState.Paused,
            PauseRequestMode = "Cooperative",
            PauseRequestedUtc = achievedAt.AddSeconds(-2),
            PauseAchievedUtc = achievedAt,
            PauseElapsedMs = 1_500,
            PauseOutcome = PauseOutcomeKind.Achieved,
        });
        var pausedVm = NewSession();
        Assert.True(await pausedVm.AdoptExistingJobAsync(pausedCtx.JobDir));
        Assert.True(pausedVm.IsPaused);
        Assert.True(pausedVm.CanResume);
        Assert.Equal("已暂停", pausedVm.PauseButtonText);
        Assert.Equal(achievedAt, pausedVm.PauseAchievedAt);

        // ② 持久化为"暂停失败"（迁移在那之后仍继续过）
        var failedCtx = CreateJob("JOB-PU-06-FAILED", plan: PlanWithOneObject(src), state: new JobState
        {
            JobId = "JOB-PU-06-FAILED",
            Phase = JobPhase.Interrupted,
            TotalObjects = 3, CompletedObjects = 2,
            TotalBytes = 300L * 1024 * 1024, CompletedBytes = 200L * 1024 * 1024,
            PauseState = PauseState.PauseFailed,
            PauseRequestMode = "Cooperative",
            PauseRequestedUtc = DateTime.UtcNow.AddMinutes(-9),
            PauseOutcome = PauseOutcomeKind.Failed,
            PauseFailureReason = "worker-did-not-stop",
        });
        var failedVm = NewSession();
        Assert.True(await failedVm.AdoptExistingJobAsync(failedCtx.JobDir));
        Assert.True(failedVm.IsPauseFailed);
        Assert.False(failedVm.IsPaused);
        Assert.Equal("重试暂停", failedVm.PauseButtonText);
        Assert.Equal("worker-did-not-stop", failedVm.PauseFailureReason);
    }

    // ══════════════════════════ PU-07 ══════════════════════════

    /// <summary>
    /// PU-07a（§5 Button Matrix，表驱动·运行中）：PauseState 的四种真值 × 按钮矩阵，
    /// 全部在**引擎执行中**采样（IsRunning == true，与产品真实时序一致）。
    /// </summary>
    [Theory]
    //                       pauseState                  CanPause CanResume CanStop 按钮文案
    [InlineData(PauseState.None, true, false, true, "暂停")]
    [InlineData(PauseState.Pausing, false, false, true, "正在暂停…")]
    [InlineData(PauseState.Paused, false, true, true, "已暂停")]
    [InlineData(PauseState.PauseFailed, true, false, true, "重试暂停")]
    public async Task PU07a_ButtonMatrix_DuringRun_IsDrivenByOneTruth(
        PauseState pauseState, bool canPause, bool canResume, bool canStop, string pauseText)
    {
        var src = NewDir("pu07a", Guid.NewGuid().ToString("N"));
        var jobId = "JOB-PU-07A-" + Guid.NewGuid().ToString("N")[..6];
        var ctx = CreateJob(jobId, plan: PlanWithOneObject(src), state: RunningState(jobId));
        var vm = NewSession();
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        Samples? live = null;
        await RunScriptedAsync(vm, (_, progress) =>
        {
            if (pauseState != PauseState.None)
                progress.Report(Snap(pauseState: pauseState, requestedAt: DateTime.UtcNow,
                    achievedAt: pauseState == PauseState.Paused ? DateTime.UtcNow : null,
                    outcome: pauseState switch
                    {
                        PauseState.Paused => PauseOutcomeKind.Achieved,
                        PauseState.PauseFailed => PauseOutcomeKind.Failed,
                        _ => PauseOutcomeKind.None,
                    },
                    failureReason: pauseState == PauseState.PauseFailed ? "worker-did-not-stop" : null));
            live = Capture(vm);
            return Task.CompletedTask;
        });

        Assert.True(live!.IsRunningDuringEngine);
        Assert.Equal(pauseText, live.PauseButtonText);
        Assert.Equal(canPause, live.CanPause);
        Assert.Equal(canResume, live.CanResume);
        Assert.Equal(canStop, live.CanStop);
        Assert.Equal(pauseState == PauseState.Paused, live.IsPaused);
        Assert.Equal(pauseState == PauseState.Pausing, live.IsPausing);
        Assert.Equal(pauseState == PauseState.PauseFailed, live.IsPauseFailed);

        // 按钮矩阵的核心纪律：暂停失败与"正在暂停"在界面上必须可区分（不能都是"暂停"）
        if (pauseState is PauseState.Pausing or PauseState.PauseFailed or PauseState.Paused)
            Assert.NotEqual("暂停", live.PauseButtonText);
    }

    /// <summary>
    /// PU-07b（§5 Button Matrix，表驱动·运行结束后）：终态阶段 × 恢复可用性。
    /// 可续传的阶段才允许「恢复」（与 Core 的 JobManager.ResumablePhases 同口径）。
    /// </summary>
    [Theory]
    [InlineData(JobPhase.Interrupted, true)]
    [InlineData(JobPhase.CompletedWithErrors, true)]
    [InlineData(JobPhase.Completed, false)]
    [InlineData(JobPhase.Canceled, false)]
    public async Task PU07b_ButtonMatrix_AfterRun_ResumeFollowsResumablePhases(JobPhase phase, bool canResume)
    {
        var src = NewDir("pu07b", Guid.NewGuid().ToString("N"));
        var jobId = "JOB-PU-07B-" + Guid.NewGuid().ToString("N")[..6];
        var ctx = CreateJob(jobId, plan: PlanWithOneObject(src), state: RunningState(jobId));
        var vm = NewSession();
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        await RunScriptedAsync(vm, (_, _) => Task.CompletedTask, result: phase);

        Assert.False(vm.IsRunning);
        Assert.Equal(phase, vm.Phase);
        Assert.Equal(canResume, vm.CanResume);
        Assert.False(vm.CanPause);
        Assert.False(vm.CanStop);
        Assert.Equal("暂停", vm.PauseButtonText);
    }

    // ══════════════════════════ PU-08 ══════════════════════════

    /// <summary>
    /// PU-08（契约级）：底栏按钮矩阵的**呈现**必须来自会话真值，且不允许用
    /// `Visibility=Collapsed` 让动态按钮所在的列宽塌缩（那会让整条底栏左右漂移，§8 UI-01）。
    /// </summary>
    [Fact]
    public void PU08_FooterPush_ReadsSessionTruth_AndNeverCollapsesTheActionColumns()
    {
        var root = FindRepoRoot();
        var cs = File.ReadAllText(Path.Combine(root, "src", "PCMig.WinUI", "MainWindow.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(root, "src", "PCMig.WinUI", "MainWindow.xaml"));

        // ① PushFooter 的可用性来自会话真值
        Assert.Contains("FooterPauseButton.IsEnabled = Session.CanPause;", cs, StringComparison.Ordinal);
        Assert.Contains("FooterResumeButton.IsEnabled = Session.CanResume;", cs, StringComparison.Ordinal);
        Assert.Contains("FooterStopButton.IsEnabled = Session.CanStop;", cs, StringComparison.Ordinal);
        Assert.Contains("FooterStartButton.IsEnabled = Session.CanStart;", cs, StringComparison.Ordinal);

        // ② 暂停文案与图标同样来自会话真值（旧实现永远显示「暂停」⇒"点了没反应"与"正在办"长得一样）
        Assert.Contains("FooterPauseLabel.Text = Session.PauseButtonText;", cs, StringComparison.Ordinal);
        Assert.Contains("FooterPauseIcon.Glyph", cs, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FooterPauseIcon\"", xaml, StringComparison.Ordinal);

        // ③ 暂停点击必须观察**真实**守卫（旧写法把 Eligibility 硬编码成 true/"allowed"）
        Assert.Contains("trace.Eligibility(Session.CanPause", cs, StringComparison.Ordinal);
        Assert.Contains("cannot-pause", cs, StringComparison.Ordinal);

        // ④ 动态按钮绝不允许通过 Visibility=Collapsed 让列宽消失（可用性一律走 IsEnabled）
        Assert.DoesNotContain("FooterPauseButton.Visibility", cs, StringComparison.Ordinal);
        Assert.DoesNotContain("FooterResumeButton.Visibility", cs, StringComparison.Ordinal);
        Assert.DoesNotContain("FooterStartButton.Visibility", cs, StringComparison.Ordinal);
        Assert.DoesNotContain("FooterStopButton.Visibility", cs, StringComparison.Ordinal);
        // 四个按钮在 XAML 里必须同处一个 StackPanel（Action block = 一个固定块），不是四列 Auto
        Assert.Contains("FooterStartButton", xaml, StringComparison.Ordinal);
        Assert.Contains("FooterResumeButton", xaml, StringComparison.Ordinal);
    }

    // ══════════════════════════ PU-09 / PU-10（FIX BATCH 7，RECOVERY GATE case02 现场缺陷）══════════════════════════

    /// <summary>
    /// PU-09：`WaitForPauseSettledAsync` 必须在**引擎真值还没到达**时继续等，而不是立刻返回 false。
    ///
    /// 现场证据（RECOVERY GATE case02，2026-10-05 00:36）：引擎在点击后 0.36 s 就发出了 TRN-011（真的停住），
    /// 但 footer 的动作终点事件是 `outcome=Unknown / phase=pause-unsettled / durationMs=17` —— 因为旧实现写成
    /// `while (_enginePauseState == PauseState.Pausing …)`：PauseState 只在每 2 s 的快照里送一次，
    /// 点击瞬间它还是 None ⇒ 循环一次都不进 ⇒ 立刻 false ⇒ 一次**真实达成**的暂停被记成"没落定"，
    /// pause.v2 的 FeedbackConfirmed 永远不出现（诊断侧只能靠 TRN-011 兜底）。
    /// </summary>
    [Fact]
    public async Task PU09_WaitForPauseSettled_WaitsForLateEngineTruth_InsteadOfReturningFalseEarly()
    {
        var src = NewDir("pu09", "src");
        var ctx = CreateJob("JOB-PU-09", plan: PlanWithOneObject(src), state: RunningState("JOB-PU-09"));
        var vm = NewSession();
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        bool? settled = null;
        var engineStateAtWaitStart = PauseState.None;

        await RunScriptedAsync(vm, async (_, progress) =>
        {
            await vm.PauseAsync();
            engineStateAtWaitStart = vm.EnginePauseState;      // 此刻引擎还没上报任何暂停态
            var waiter = vm.WaitForPauseSettledAsync();        // 旧实现：这里就已经是完成态 false
            await Task.Delay(150);
            var now = DateTime.UtcNow;
            progress.Report(Snap(JobPhase.Paused, PauseState.Paused, now, now,
                PauseOutcomeKind.Achieved, null, "已暂停（等待 resume）"));
            settled = await waiter;
        });

        Assert.Equal(PauseState.None, engineStateAtWaitStart);  // 前提：等待发起时真值尚未到达
        Assert.True(settled);                                   // 必须等到真值落定后判成功
        Assert.Equal(PauseState.Paused, vm.EnginePauseState);
        Assert.True(vm.IsPaused);
    }

    /// <summary>
    /// PU-10：引擎报 PauseFailed ⇒ 等待必须以 false 收口（不假装成功），且不能耗满整个预算。
    /// 与 PU-09 配对：同一个方法在"成功"与"失败"两侧都必须由引擎真值驱动。
    /// </summary>
    [Fact]
    public async Task PU10_WaitForPauseSettled_ReturnsFalse_WhenEngineReportsPauseFailed()
    {
        var src = NewDir("pu10", "src");
        var ctx = CreateJob("JOB-PU-10", plan: PlanWithOneObject(src), state: RunningState("JOB-PU-10"));
        var vm = NewSession();
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        bool? settled = null;
        var waitedMs = 0L;

        await RunScriptedAsync(vm, async (_, progress) =>
        {
            await vm.PauseAsync();
            var waiter = vm.WaitForPauseSettledAsync();
            var t0 = DateTime.UtcNow;
            await Task.Delay(150);
            var now = DateTime.UtcNow;
            progress.Report(Snap(JobPhase.Running, PauseState.PauseFailed, now, null,
                PauseOutcomeKind.Failed, "worker-did-not-stop", "正在传输：object-000002"));
            settled = await waiter;
            waitedMs = (long)(DateTime.UtcNow - t0).TotalMilliseconds;
        }, result: JobPhase.Interrupted);

        Assert.False(settled);                                  // 引擎说停不住 ⇒ 绝不报成功
        Assert.Equal(PauseState.PauseFailed, vm.EnginePauseState);
        Assert.True(vm.IsPauseFailed);
        Assert.True(waitedMs < ActionSla.PauseFulfillmentDeadlineMs, $"不应耗满预算，实测 {waitedMs} ms");
    }

    // ══════════════════════════ PU-11 / PU-12（FIX BATCH 7，RECOVERY GATE case03 现场缺陷）══════════════════════════

    /// <summary>
    /// PU-11：从「已暂停」恢复、而那一轮运行**已经跑完**时，<see cref="MigrationSessionViewModel.WaitForResumeSettledAsync"/>
    /// 必须判**成功**（引擎确实离开了暂停并真的继续传了），而不是等满 30 s 报"没落定"。
    ///
    /// 现场证据（RECOVERY GATE case03b，JOB-20261005-004639-d9b2）：00:47:03.576 点恢复 → 引擎起了一轮新运行并
    /// 在 00:47:12.178 以 Completed 收尾（`TRN.JobRunCompleted Succeeded`），而 footer 直到 00:47:42.253 才发
    /// `UI.ActionCompleted level=Warning outcome=Unknown phase=resume-unsettled durationMs=38676`（= 8.6 s 运行 + 30 s 空等），
    /// resume.v1 也就永远拿不到 FeedbackConfirmed。根因：判据的"起点是否已暂停"是在**等待开始时**才读 Phase，
    /// 而 ResumeAsync 会 await 整轮运行 ⇒ 那时 Phase 已是 Completed ⇒ 判据退化成"运行中"（恒 false）。
    /// </summary>
    [Fact]
    public async Task PU11_WaitForResumeSettled_IsTrue_WhenResumeRan_AndAlreadyFinished()
    {
        var src = NewDir("pu11", "src");
        var ctx = CreateJob("JOB-PU-11", plan: PlanWithOneObject(src), state: RunningState("JOB-PU-11"));
        var vm = NewSession();
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        // ① 第一次运行：暂停在对象边界达成 —— 引擎真值 Paused，本次运行以 Paused 收尾（IsRunning == false）
        vm.TransferRunner = (_, _, progress, _, _, _) =>
        {
            var now = DateTime.UtcNow;
            progress.Report(Snap(JobPhase.Paused, PauseState.Paused, now, now,
                PauseOutcomeKind.Achieved, null, "已暂停（等待 resume）"));
            return Task.FromResult(JobPhase.Paused);
        };
        await WithInlineSyncContext(() => vm.RunAsync(password: null));
        Assert.Equal(JobPhase.Paused, vm.Phase);
        Assert.False(vm.IsRunning);
        Assert.True(vm.IsPaused);

        // ② 用户点恢复：这是一轮**新运行**，并且（真实时序）已经跑完 —— ResumeAsync 会 await 整轮运行
        var runCount = 0;
        vm.TransferRunner = (_, _, progress, _, _, _) =>
        {
            runCount++;
            var now = DateTime.UtcNow;
            progress.Report(Snap(JobPhase.Running, PauseState.None, null, null,
                PauseOutcomeKind.Achieved, null, "已恢复，继续传输"));
            progress.Report(Snap(JobPhase.Completed, PauseState.None, null, null,
                PauseOutcomeKind.Achieved, null, "任务完成：全部对象已传输"));
            return Task.FromResult(JobPhase.Completed);
        };
        await WithInlineSyncContext(() => vm.ResumeAsync(password: null));
        Assert.Equal(1, runCount);
        Assert.Equal(JobPhase.Completed, vm.Phase);            // 恢复真的发生了，而且这一轮已经结束
        Assert.False(vm.IsRunning);

        // ③ 终点判定：必须由"引擎离开了暂停起点"这一**事实**得出成功（旧实现在这里恒 false，等满 30 s）
        var t0 = DateTime.UtcNow;
        var settled = await vm.WaitForResumeSettledAsync();
        var waitedMs = (long)(DateTime.UtcNow - t0).TotalMilliseconds;

        Assert.True(settled, "恢复真的发生了（引擎离开 Paused 并跑完），不得记成 resume-unsettled");
        Assert.True(waitedMs < 2_000, $"已经落定就不该再等预算，实测 {waitedMs} ms");
    }

    /// <summary>
    /// PU-12（PU-11 的配对守卫）：恢复**没有生效**（引擎始终停在 Paused）⇒ 等待必须以 false 收口。
    /// 与 PU-11 一起钉死"同一个方法在成功侧与失败侧都由引擎真值驱动"，而不是由"命令返回了"决定。
    /// 这里给一个短取消令牌：断言的是**终点判据**本身（避免在失败侧空等 30 s 预算）。
    /// </summary>
    [Fact]
    public async Task PU12_WaitForResumeSettled_IsFalse_WhenEngineNeverLeavesPaused()
    {
        var src = NewDir("pu12", "src");
        var ctx = CreateJob("JOB-PU-12", plan: PlanWithOneObject(src), state: RunningState("JOB-PU-12"));
        var vm = NewSession();
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        vm.TransferRunner = (_, _, progress, _, _, _) =>
        {
            var now = DateTime.UtcNow;
            progress.Report(Snap(JobPhase.Paused, PauseState.Paused, now, now,
                PauseOutcomeKind.Achieved, null, "已暂停（等待 resume）"));
            return Task.FromResult(JobPhase.Paused);
        };
        await WithInlineSyncContext(() => vm.RunAsync(password: null));
        Assert.Equal(JobPhase.Paused, vm.Phase);

        // 恢复命令"发出去了"，但这一轮运行结束后引擎仍然停在 Paused（恢复没生效）
        await WithInlineSyncContext(() => vm.ResumeAsync(password: null));
        Assert.Equal(JobPhase.Paused, vm.Phase);

        using var cts = new CancellationTokenSource(300);
        var settled = await vm.WaitForResumeSettledAsync(cts.Token);

        Assert.False(settled, "引擎从未离开 Paused ⇒ 绝不报 resume-settled");
        Assert.Equal(JobPhase.Paused, vm.Phase);
    }
}