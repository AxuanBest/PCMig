// ============================================================================
//  ★ R2 · 缺陷 B 回归 ★ 「100%」与「可信完成」必须是同一个真相
// ============================================================================
//
//  ── 问题（R2 接手审计 · 缺陷 B，2026-10-17）─────────────────────────────────
//  程序里对"完成"有两套判据，宽度不同：
//    • Core `TransferOrchestrator.Percent()` 的完成分支只看三项
//        Phase==Completed ∧ FailedObjects==0 ∧ CompletedObjects>=TotalObjects
//      就放行 100%；
//    • Core `IsTrustworthyCompletion()` 要求六项（额外还要 MissingObjects==0 ∧
//      CompletedBytes==TotalBytes ∧ TotalObjects>0）。
//    • 外壳 `MigrationSessionViewModel.FinishRunAsync()` 更宽：只要 phase==Completed
//      就无条件 `Percent = 100.0`，连字节闭环都不看。
//
//  后果是用户在真机上会看到「进度条 100% + 状态句"已完成" + 不跳 Step4」这种
//  自相矛盾的组合；反过来也可能「字节没闭环却显示 100%」，正是任务令明令禁止的
//  「有文件缺失但进度 100%」。
//
//  ── 本文件钉死的一致性律 ─────────────────────────────────────────────────────
//      **凡是显示 100%，就必须同时是可信任的完成；只要不可信，就不得显示 100%。**
//  即对任意 JobState：`Percent(s) >= 100.0` ⟺ `IsTrustworthyCompletion(s)`。
//
//  五类反例（任务令 §阶段三 第二批 B 明确要求）：
//    B-1 全对象完成且字节闭环           ⇒ 100% ∧ 可信（正面基线）
//    B-2 对象计数完成但字节不足         ⇒ <100% ∧ 不可信
//    B-3 有失败对象但字节接近计划总量   ⇒ ≤99.9 ∧ 不可信
//    B-4 字节闭环但校验确证目标少文件   ⇒ <100% ∧ 不可信（隔离旧副本/残留字节不能当闭环）
//    B-5 外壳收尾不得越过 Core 判据     ⇒ VM 的 Percent 必须与 TrustworthyCompletion 同步
// ============================================================================

using System;
using System.IO;
using System.Threading.Tasks;
using PCMig.Core.Jobs;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using PCMig.WinUI.Presentation;
using Serilog;
using Xunit;

namespace PCMig.Core.Tests;

[Collection("A5-jobdir-isolation")]
public sealed class R2DefectBPercentConsistencyTests : IDisposable
{
    private readonly string _sandbox;
    private readonly ILogger _log;
    private readonly string? _previousJobsRoot;

    public R2DefectBPercentConsistencyTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "pcmig-r2-defect-b", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
        _previousJobsRoot = Environment.GetEnvironmentVariable("PCMIG_JOBS");
        Environment.SetEnvironmentVariable("PCMIG_JOBS", Path.Combine(_sandbox, "Jobs"));
        _log = new LoggerConfiguration().CreateLogger();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PCMIG_JOBS", _previousJobsRoot);
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); }
        catch { /* 清理失败不影响结论 */ }
    }

    // ───────────────────────── 夹具 ─────────────────────────

    private static JobState State(
        JobPhase phase = JobPhase.Completed,
        int totalObjects = 2,
        int completedObjects = 2,
        int failedObjects = 0,
        int missingObjects = 0,
        long totalBytes = 1000,
        long completedBytes = 1000)
        => new()
        {
            JobId = "JOB-R2B",
            Phase = phase,
            TotalObjects = totalObjects,
            CompletedObjects = completedObjects,
            FailedObjects = failedObjects,
            MissingObjects = missingObjects,
            TotalBytes = totalBytes,
            CompletedBytes = completedBytes,
        };

    /// <summary>本文件的一致性律：100% 与可信完成必须同时成立或同时不成立。</summary>
    private static void AssertPercentMatchesTrust(double percent, bool trustworthy, string why)
    {
        var shows100 = percent >= 100.0;
        Assert.True(shows100 == trustworthy,
            $"{why}：Percent={percent:0.###}（显示 100% = {shows100}），IsTrustworthyCompletion={trustworthy} —— " +
            "「100%」与「可信完成」必须是同一个真相（缺陷 B）。");
    }

    // ══════════════════════════ B-1 正面基线 ══════════════════════════

    [Fact]
    public void B01_AllObjectsDoneAndBytesClosed_IsHundredPercentAndTrustworthy()
    {
        var s = State();
        Assert.True(TransferOrchestrator.IsTrustworthyCompletion(s), "基线不成立：字节闭环的全完成状态本来就该可信。");
        Assert.Equal(100.0, TransferOrchestrator.Percent(s));
        AssertPercentMatchesTrust(TransferOrchestrator.Percent(s), TransferOrchestrator.IsTrustworthyCompletion(s), "B-1");
    }

    // ══════════════════════════ B-2 字节不足 ══════════════════════════

    [Fact]
    public void B02_ObjectCountDoneButBytesShort_MustNotShowHundred()
    {
        // 对象计数全部完成，但只落了 60% 的字节（重试/预分配/日志解析领先都可能造出这种局面）。
        var s = State(totalBytes: 1000, completedBytes: 600);

        Assert.False(TransferOrchestrator.IsTrustworthyCompletion(s));
        Assert.True(TransferOrchestrator.Percent(s) < 100.0,
            $"字节只落 60%，Percent 却是 {TransferOrchestrator.Percent(s):0.###} —— 用户会以为传完了。");
        AssertPercentMatchesTrust(TransferOrchestrator.Percent(s), TransferOrchestrator.IsTrustworthyCompletion(s), "B-2");
    }

    // ══════════════════════════ B-3 有失败对象 ══════════════════════════

    [Fact]
    public void B03_HasFailedObject_WithBytesNearPlanned_MustCapBelowHundred()
    {
        // 一个对象失败、另一个把字节几乎填满计划总量（真机 10 对象差 8192 B 的形态）。
        var s = State(totalObjects: 2, completedObjects: 1, failedObjects: 1,
                      totalBytes: 100_000, completedBytes: 99_992);

        Assert.False(TransferOrchestrator.IsTrustworthyCompletion(s));
        Assert.True(TransferOrchestrator.Percent(s) <= 99.9,
            $"有失败对象时 Percent={TransferOrchestrator.Percent(s):0.###}，超过封顶 99.9 —— 「有失败就不可能 100%」。");
        AssertPercentMatchesTrust(TransferOrchestrator.Percent(s), TransferOrchestrator.IsTrustworthyCompletion(s), "B-3");
    }

    // ══════════════════════════ B-4 字节闭环但校验确证缺失 ══════════════════════════

    [Fact]
    public void B04_BytesClosedButVerifyFoundMissing_MustNotShowHundred()
    {
        // ★ 本轮最需要的那条 ★ 字节账面对得上（隔离旧副本被排除后残留字节不再计入，
        //   于是 CompletedBytes 恰好压到 TotalBytes），但校验已确证目标少文件。
        //   这正是 Models.cs 里写明的"迁移报成功、校验发现目标少了文件"。
        var s = State(totalBytes: 5000, completedBytes: 5000, missingObjects: 1);

        Assert.False(TransferOrchestrator.IsTrustworthyCompletion(s),
            "校验已确证缺失，IsTrustworthyCompletion 必须为 false。");
        Assert.True(TransferOrchestrator.Percent(s) < 100.0,
            $"校验已确证目标少文件，Percent 却是 {TransferOrchestrator.Percent(s):0.###} —— " +
            "这正是任务令明令禁止的「有文件缺失但进度 100%」。");
        AssertPercentMatchesTrust(TransferOrchestrator.Percent(s), TransferOrchestrator.IsTrustworthyCompletion(s), "B-4");
    }

    // ══════════════════════════ B-5 外壳收尾不得越过 Core 判据 ══════════════════════════

    private JobContext CreateJob(string jobId, JobState state)
    {
        var target = Path.Combine(_sandbox, "target", jobId);
        Directory.CreateDirectory(target);
        var def = new JobDefinition
        {
            JobId = jobId,
            SourceHost = "R2-B-PROBE-HOST",
            TargetRoot = target,
            Sources = { new SourceSpec { Path = @"\\R2-B-PROBE-HOST\share", Kind = ObjectKind.DataVolume } },
            Options = new MigrationOptions { Threads = 2 },
        };
        var ctx = new JobManager(_log).Create(def);
        ctx.SavePlan(new MigrationPlan
        {
            JobId = jobId,
            TotalBytes = state.TotalBytes,
            Objects =
            {
                new PlannedObject
                {
                    ObjectId = "object-000001",
                    Kind = ObjectKind.DataVolume,
                    SourcePath = @"\\R2-B-PROBE-HOST\share",
                    TargetPath = Path.Combine(target, "one"),
                    EstimatedBytes = state.TotalBytes,
                    EstimatedFiles = 1,
                },
            },
        });
        // ★ 缺陷 K 契约 ★ 这份夹具状态可能已带着校验结论（MissingObjects），只能走结论所有者入口；
        //   普通 SaveState 会按磁盘现值把它盖回去 —— 那正是缺陷 K 本身。
        ctx.SaveStateWithVerifyConclusion(state);
        return ctx;
    }

    private sealed class InlineSyncContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    /// <summary>跑一次"引擎已报 phase=Completed、但落盘状态另有其说"的收尾。</summary>
    private static async Task RunToFinishAsync(MigrationSessionViewModel vm, JobContext ctx, JobState landAs, JobPhase reported)
    {
        vm.TransferRunner = (_, _, _, _, _, _) =>
        {
            ctx.SaveState(landAs);                    // 引擎真实落盘的状态
            return Task.FromResult(reported);         // 引擎返回的 phase
        };
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        try { await vm.RunAsync(password: null); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    [Fact]
    public async Task B05_ShellFinish_DoesNotShowHundredWhenCoreSaysUntrustworthy()
    {
        // 引擎报 Completed，但落盘状态里校验已确证缺失（不可信完成）。
        var landAs = State(totalBytes: 4096, completedBytes: 4096, missingObjects: 1);
        var ctx = CreateJob("JOB-R2B-05", landAs);
        var vm = new MigrationSessionViewModel(null, null, _log);
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        await RunToFinishAsync(vm, ctx, landAs, JobPhase.Completed);

        Assert.False(vm.TrustworthyCompletion,
            "Core 判为不可信完成，外壳的 TrustworthyCompletion 却为 true —— 两侧判据没有对齐。");
        Assert.True(vm.Percent < 100.0,
            $"引擎已报 Completed 但状态不可信，外壳 Percent 仍被写成 {vm.Percent:0.###} —— " +
            "这是「进度条 100% + 不跳 Step4」自相矛盾的直接来源（缺陷 B）。");
        AssertPercentMatchesTrust(vm.Percent, vm.TrustworthyCompletion, "B-5");
    }

    [Fact]
    public async Task B06_ShellFinish_ShowsHundredOnlyWhenTrustworthy()
    {
        // 对照组：真正闭环完成 ⇒ 外壳才允许 100%，且必须同时可信。
        var landAs = State(totalBytes: 4096, completedBytes: 4096);
        var ctx = CreateJob("JOB-R2B-06", landAs);
        var vm = new MigrationSessionViewModel(null, null, _log);
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        await RunToFinishAsync(vm, ctx, landAs, JobPhase.Completed);

        Assert.True(vm.TrustworthyCompletion, "真正闭环完成却没被判为可信完成 —— 修 B 不能把正面情形一起打死。");
        Assert.Equal(100.0, vm.Percent);
        AssertPercentMatchesTrust(vm.Percent, vm.TrustworthyCompletion, "B-6");
    }
}
