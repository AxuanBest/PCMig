// ============================================================================
//  ★ R2 · 缺陷 J 回归 ★ 「校验确证缺失」必须能把界面上那个 100% 真的压下来
// ============================================================================
//
//  ── 问题（R2 独立代码审查 🔴-2，2026-10-17）──────────────────────────────────
//  界面上三处显示百分比的地方读的都是 `truth?.Percent ?? Session.Percent`：
//    • MainWindow.PushFooter        （底栏百分比）；
//    • Step3ProgressPage.PushState  （页内大号百分比）；
//    • 两处 80ms 呈现节拍（RefreshFooterPresentation / AdvancePresentationTick）。
//  也就是说：**只要 PresentationTruth 非空，Session.Percent 根本不会被读**。
//
//  而缺陷 H（S-3）的修复只改了 Session.Percent ——于是:
//     迁移跑完（收到过 Completed 快照，PresentationTruth 里 100% 存着）
//     → 用户点「验证」→ 校验确证目标少文件 → VerifyReconciliation.Apply 写 MissingObjects=1
//     → ApplySettledCompletion 把 Session.Percent 压到 99.9
//     → **但界面仍从旧 PresentationTruth 读到 100%**。
//  用户看到的依然是「进度 100% + 校验确证缺失」，正是任务令明令禁止的画面。
//
//  ── 本文件钉死的契约 ─────────────────────────────────────────────────────
//    J-1 校验确证缺失后，**Session.Percent 与 PresentationTruth.Percent 必须同时降下来**
//        （旧真值残留 100% 时不得留在界面上）；
//    J-2 呈现层（80ms 节拍读的那个视觉值）也必须能被压下来，
//        否则底栏大号数字仍停在 100%；
//    J-3 反向保护：真的可信完成时仍必须是 100%（修 J 不能把正常路径改坏）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PCMig.Core.Jobs;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using PCMig.Core.Verify;
using PCMig.WinUI.Presentation;
using Serilog;
using Xunit;

namespace PCMig.Core.Tests;

[Collection("A5-jobdir-isolation")]
public sealed class R2DefectJStaleTruthPercentTests : IDisposable
{
    private readonly string _sandbox;
    private readonly ILogger _log;
    private readonly string? _previousJobsRoot;

    public R2DefectJStaleTruthPercentTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "pcmig-r2-defect-j", Guid.NewGuid().ToString("N"));
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

    private JobContext CreateJob(string jobId, long totalBytes, string? objectId = null, string? srcPath = null)
    {
        var src = srcPath ?? Path.Combine(_sandbox, jobId, "source");
        Directory.CreateDirectory(src);
        var obj = new PlannedObject
        {
            ObjectId = objectId ?? "object-000001",
            Kind = ObjectKind.DataVolume,
            SourcePath = src,
            TargetPath = Path.Combine(_sandbox, jobId, "target", "obj1"),
            EstimatedBytes = totalBytes,
            EstimatedFiles = 1,
        };
        Directory.CreateDirectory(obj.TargetPath);
        var ctx = new JobContext
        {
            JobDir = Path.Combine(_sandbox, jobId, "job"),
            Definition = new JobDefinition { JobId = jobId, TargetRoot = Path.Combine(_sandbox, jobId, "target") },
        };
        ctx.EnsureDirs();
        ctx.SaveDefinition();
        ctx.SavePlan(new MigrationPlan
        {
            JobId = jobId,
            Objects = new List<PlannedObject> { obj },
            TotalBytes = totalBytes,
        });
        ctx.SaveState(new JobState
        {
            JobId = jobId,
            Phase = JobPhase.Completed,
            TotalObjects = 1,
            CompletedObjects = 1,
            FailedObjects = 0,
            TotalBytes = totalBytes,
            CompletedBytes = totalBytes,
            Percent = 100,
            LastUpdateUtc = DateTime.UtcNow,
        });
        return ctx;
    }

    /// <summary>一份"字节闭环、但校验确证目标少文件"的报告 —— 比迁移更强的证据。</summary>
    private static VerifyReport MissingConfirmedReport(string jobId, string objectId)
        => new()
        {
            JobId = jobId,
            VerifiedUtc = DateTime.UtcNow,
            Level = VerifyLevel.L1_CountSize,
            OverallPass = false,
            RunStatus = "completed",
            Objects = new List<ObjectVerifyResult>
            {
                new()
                {
                    ObjectId = objectId,
                    SourceFiles = 1,
                    TargetFiles = 0,
                    SourceBytes = 4096,
                    TargetBytes = 0,
                    CountMatch = false,
                    BytesMatch = false,
                    MissingConfirmed = true,
                    MissingSamples = new List<string> { "gone.txt" },
                },
            },
        };

    /// <summary>让 VM 先吃到一份"已完成、字节闭环"的快照 ⇒ PresentationTruth 里留下 100%。</summary>
    private static ProgressSnapshot CompletedSnapshot(long totalBytes)
        => new(
            Phase: JobPhase.Completed,
            TotalObjects: 1,
            CompletedObjects: 1,
            FailedObjects: 0,
            TotalBytes: totalBytes,
            CompletedBytes: totalBytes,
            Percent: 100,
            BytesPerSecond: 0,
            EtaSeconds: double.NaN,
            CurrentObjectId: null,
            CurrentObjectPath: null,
            Message: string.Empty,
            Truth: ProgressTruthSnapshot.Create(
                plannedBytes: totalBytes,
                committedBytes: totalBytes,
                inFlightConfirmedBytes: 0,
                currentObjectId: null,
                currentObjectPlannedBytes: 0,
                currentObjectConfirmedBytes: 0,
                attemptEpoch: 1,
                retryState: RetryState.None,
                speedBytesPerSecond: 0,
                settled: true,
                paused: false,
                targetStatTrusted: false,
                inFlightSource: ProgressTruthSource.CompletedReceipts,
                timestampUtc: DateTime.UtcNow));

    private static async Task WithInlineSyncContext(Func<Task> body)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        try { await body(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private sealed class InlineSyncContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    /// <summary>
    /// 把 VM 推进到"迁移已完成、界面显示 100%"这个真实状态：
    /// 跑一次假引擎（快照 = Completed 且字节闭环），收尾路径会写下 100% 的 Percent 与 PresentationTruth。
    /// </summary>
    private async Task<MigrationSessionViewModel> RunToCompletedAsync(JobContext ctx, long totalBytes)
    {
        var vm = new MigrationSessionViewModel(null, null, _log);
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));
        vm.TransferRunner = (c, _, progress, _, _, _) =>
        {
            progress.Report(CompletedSnapshot(totalBytes));
            return Task.FromResult(JobPhase.Completed);
        };
        await WithInlineSyncContext(() => vm.RunAsync(password: null));
        return vm;
    }

    // ══════════════ J-1 校验确证缺失后，旧真值里的 100% 必须降下来 ══════════════

    [Fact]
    public async Task J01_VerifyFindsMissing_StalePresentationTruthHundredMustComeDown()
    {
        const long total = 4096;
        var ctx = CreateJob("JOB-R2J-01", total);
        var vm = await RunToCompletedAsync(ctx, total);

        // 前提：此刻界面确实是 100%（真值里也存着 100%）—— 否则本用例测不到缺陷 J
        Assert.Equal(100.0, vm.Percent, 3);
        Assert.NotNull(vm.PresentationTruth);
        Assert.Equal(100.0, vm.PresentationTruth!.Percent, 3);

        // 校验确证目标少文件 ⇒ 结论必须压下来
        vm.ApplyVerifyConclusion(ctx, MissingConfirmedReport(ctx.JobId, "object-000001"));

        Assert.False(vm.TrustworthyCompletion);
        // ★ 缺陷 J 的判据：不只是 Session.Percent 要降，**真值里的那个 100% 也必须降**
        Assert.True(vm.PresentationTruth!.Percent < 100.0,
            $"校验已确证目标少文件，PresentationTruth.Percent 仍是 {vm.PresentationTruth.Percent:0.###} —— " +
            "界面三处读数都优先读它，不降下去用户就仍然看到 100%（缺陷 J）。");
        Assert.True(vm.Percent < 100.0);
    }

    // ══════════════ J-2 呈现层（80ms 节拍）的视觉值也必须能降 ══════════════

    [Fact]
    public async Task J02_VerifyFindsMissing_VisualPercentMustComeDownToo()
    {
        const long total = 4096;
        var ctx = CreateJob("JOB-R2J-02", total);
        var vm = await RunToCompletedAsync(ctx, total);

        // 让呈现层追平到 100%（真机上 80 ms 节拍会这样收敛）。
        // 注意：Advance 的单拍步长上限是 ProgressPresentationCoordinator.MaxStepSeconds（1/30 s），
        // 且首次调用时 _lastTickUtc 为 MinValue ⇒ dt=0 不移动。因此必须**多拍**推进，
        // 不能指望一次 Advance(now+5s) 就跳到目标（那样反而测不到真实收敛过程）。
        var visualBefore = AdvanceToSettle(vm);
        Assert.True(visualBefore > 99.0, $"夹具前提：收尾后视觉值应收敛到接近 100（实得 {visualBefore:0.###}）");

        vm.ApplyVerifyConclusion(ctx, MissingConfirmedReport(ctx.JobId, "object-000001"));

        var visualAfter = AdvanceToSettle(vm);
        Assert.True(visualAfter < 100.0,
            $"校验已确证目标少文件，呈现层视觉值仍是 {visualAfter:0.###} —— " +
            "底栏与 Step3 的大号数字读的就是它（缺陷 J）。");
    }

    /// <summary>按 80 ms 节拍推进足够多拍，让视觉值收敛到当前目标（最多 200 拍 ≈ 16 秒）。</summary>
    private static double AdvanceToSettle(MigrationSessionViewModel vm)
    {
        var utc = DateTime.UtcNow;
        var visual = 0d;
        for (var i = 0; i < 200; i++)
        {
            utc = utc.AddMilliseconds(80);
            visual = vm.AdvancePresentation(utc);
        }
        return visual;
    }

    // ══════════════ J-3 反向保护：真可信完成仍是 100% ══════════════

    [Fact]
    public async Task J03_TrustworthyCompletion_StillShowsHundred()
    {
        const long total = 8192;
        var ctx = CreateJob("JOB-R2J-03", total);
        var vm = await RunToCompletedAsync(ctx, total);

        // 校验通过（没有任何缺失）⇒ 必须仍然是 100%，且真值不许被"只压不抬"误伤
        vm.ApplyVerifyConclusion(ctx, new VerifyReport
        {
            JobId = ctx.JobId,
            VerifiedUtc = DateTime.UtcNow,
            Level = VerifyLevel.L1_CountSize,
            OverallPass = true,
            RunStatus = "completed",
            Objects = new List<ObjectVerifyResult>
            {
                new()
                {
                    ObjectId = "object-000001",
                    SourceFiles = 1,
                    TargetFiles = 1,
                    SourceBytes = total,
                    TargetBytes = total,
                    CountMatch = true,
                    BytesMatch = true,
                },
            },
        });

        Assert.True(vm.TrustworthyCompletion);
        Assert.Equal(100.0, vm.Percent, 3);
        Assert.Equal(100.0, vm.PresentationTruth!.Percent, 3);
    }
}
