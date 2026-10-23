// ============================================================================
//  ★ R2 · 缺陷 I 回归 ★ 修复轮的自动复验必须真的跑起来
// ============================================================================
//
//  ── 问题（R2 独立代码审查 · 缺陷 I，2026-10-17）──────────────────────────────
//  修复（Repair）走的是 RunAsync 那条主路径：
//      RunTransferCoreAsync  :2448  IsRunning = true;
//      RunTransferCoreAsync  :2532  await FinishRunAsync(...);
//      FinishRunAsync        :2734  await VerifyAsync(...)   ← 修复轮自动复验
//      finally               :2560  CleanupRun(session) → :2581 IsRunning = false;
//  而 VerifyAsync 开头的守卫是：
//      :2822  if (IsRunning) { ... return; }
//  于是**修复轮的自动复验在调用点就被自己的守卫拒掉了**：Verifier 从未构造过，
//  ctx.LoadVerify() 读到的是上一轮的旧报告（或 null），"修复到底成没成"永远判成未通过，
//  而界面同时显示"重拷已 100%"——正是 P0-04 要治的那个自相矛盾。
//
//  ── 本文件钉死的契约 ─────────────────────────────────────────────────────
//    ① 修复轮结束时，必须有一次**真的跑了**的验证（LastVerifyOutcome 不得是
//       Rejected/verify-already-running），并且落盘的 verify 报告是这一轮新写的；
//    ② 用户手点「验证」在运行中仍然必须被拒绝 —— 修好 ① 不能顺手把这道门拆了。
//
//  纪律：本文件用**真实 Verifier**（不注入假 verifier、不开任何测试缝来伪造验证结论），
//  只有传输引擎被换成"真的会复制文件"的假实现（无网络环境下的既有做法）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PCMig.Core.Jobs;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using PCMig.Diagnostics.Abstractions;
using PCMig.WinUI.Presentation;
using Serilog;
using Xunit;

namespace PCMig.Core.Tests;

[Collection("A5-jobdir-isolation")]
public sealed class R2DefectIRepairAutoVerifyTests : IDisposable
{
    private readonly string _sandbox;
    private readonly ILogger _log;
    private readonly string? _previousJobsRoot;

    public R2DefectIRepairAutoVerifyTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "pcmig-r2-defect-i", Guid.NewGuid().ToString("N"));
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

    /// <summary>
    /// 造一个"源与目标内容已经一致"的任务：
    ///   · 计划里 1 个 DataVolume 对象（源目录 → 目标目录）；
    ///   · job-state 处在 CompletedWithErrors（有失败对象 ⇒ CollectRepairTargets 非空 ⇒ Repair 会真的开跑）；
    ///   · 有 1 份 Failed 回执（同上）。
    /// 这样修复轮的**真实复验**会走 L1 数量/大小核对并给 OK —— 不需要伪造任何验证结论。
    /// </summary>
    private (JobContext Ctx, PlannedObject Obj, string TargetFile) Arrange(string jobId)
    {
        const string payload = "R2-DEFECT-I-PAYLOAD-0123456789";
        var src = Path.Combine(_sandbox, jobId, "source");
        var dst = Path.Combine(_sandbox, jobId, "target", "obj1");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        var srcFile = Path.Combine(src, "shared.txt");
        File.WriteAllText(srcFile, payload);
        // 目标侧预置同内容 ⇒ 修复轮重拷后真实复验必然 OK（测的是"复验有没有跑"，不是"复验判得准不准"）
        var dstFile = Path.Combine(dst, "shared.txt");
        File.WriteAllText(dstFile, payload);

        var size = new FileInfo(srcFile).Length;
        var obj = new PlannedObject
        {
            ObjectId = "object-000001",
            Kind = ObjectKind.DataVolume,
            SourcePath = src,
            TargetPath = dst,
            EstimatedBytes = size,
            EstimatedFiles = 1,
        };
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
            TotalBytes = size,
        });
        ctx.SaveState(new JobState
        {
            JobId = jobId,
            Phase = JobPhase.CompletedWithErrors,
            TotalObjects = 1,
            CompletedObjects = 0,
            FailedObjects = 1,
            TotalBytes = size,
            CompletedBytes = 0,
        });
        ctx.SaveReceipt(new ObjectReceipt
        {
            ObjectId = obj.ObjectId,
            Kind = ObjectKind.DataVolume,
            SourcePath = src,
            TargetPath = dst,
            Status = ObjectStatus.Failed,
            ErrorDetail = "R2 缺陷 I 夹具：上一轮失败，制造「需要修复」的前置条件",
            RobocopyExitCodeBulk = 8,
            CompletedUtc = DateTime.UtcNow,
        });
        return (ctx, obj, dstFile);
    }

    /// <summary>真会复制文件的假引擎：把源文件盖到目标上，然后如实落一个 Completed 的终态。</summary>
    private static TransferRunnerDelegate CopyingEngine(long totalBytes, string srcFile, string dstFile)
        => (JobContext ctx, TransferEngineHooks hooks, IProgress<ProgressSnapshot> progress,
            System.Threading.CancellationToken ct, IReadOnlyCollection<string>? onlyObjectIds, bool forceRecopy) =>
        {
            File.Copy(srcFile, dstFile, overwrite: true);
            hooks.FileCopied?.Invoke(srcFile, totalBytes);
            ctx.SaveState(new JobState
            {
                JobId = ctx.JobId,
                Phase = JobPhase.Completed,
                TotalObjects = 1,
                CompletedObjects = 1,
                FailedObjects = 0,
                TotalBytes = totalBytes,
                CompletedBytes = totalBytes,
                Percent = 100,
                LastUpdateUtc = DateTime.UtcNow,
            });
            ctx.SaveReceipt(new ObjectReceipt
            {
                ObjectId = "object-000001",
                Kind = ObjectKind.DataVolume,
                SourcePath = Path.GetDirectoryName(srcFile)!,
                TargetPath = Path.GetDirectoryName(dstFile)!,
                Status = ObjectStatus.Completed,
                RobocopyExitCodeBulk = 1,
                CompletedUtc = DateTime.UtcNow,
            });
            return Task.FromResult(JobPhase.Completed);
        };

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

    // ══════════════════ I-1 修复轮的自动复验必须真的执行 ══════════════════

    [Fact]
    public async Task I01_RepairRoundAutoVerify_ActuallyExecutesInsteadOfBeingRejectedAsRunning()
    {
        var (ctx, obj, dstFile) = Arrange("JOB-R2I-REPAIR");
        var srcDir = obj.SourcePath;
        var srcFile = Path.Combine(srcDir, "shared.txt");
        var stateBefore = ctx.LoadStateOrNew();

        var vm = new MigrationSessionViewModel(null, null, _log);
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));
        Assert.NotEmpty(vm.CollectRepairTargets(out _));

        vm.TransferRunner = CopyingEngine(stateBefore.TotalBytes, srcFile, dstFile);
        await WithInlineSyncContext(() => vm.RepairAsync(forceOverwrite: true, password: null));

        // ① 复验不得被"运行中"挡掉 —— 这正是缺陷 I 的形态
        Assert.NotEqual(DiagnosticOutcome.Rejected, vm.LastVerifyOutcome.Outcome);
        Assert.NotEqual("verify-already-running", vm.LastVerifyOutcome.ReasonCode);

        // ② 复验必须真的跑出结论：Executed（报告里有对象）+ 通过
        Assert.Equal(DiagnosticOutcome.Succeeded, vm.LastVerifyOutcome.Outcome);
        Assert.Equal("verify-pass", vm.LastVerifyOutcome.ReasonCode);

        // ③ 落盘的 verify 报告必须是这一轮新写的（不是上一轮的旧件）
        var report = ctx.LoadVerify();
        Assert.NotNull(report);
        Assert.True(report!.OverallPass, "修复轮自动复验必须留下'通过'的落盘报告");
        var single = Assert.Single(report.Objects);
        Assert.Equal("OK", single.Status);
    }

    // ══════════════════ I-2 运行中手点验证仍必须被拒绝 ══════════════════

    [Fact]
    public async Task I02_ManualVerifyWhileRunning_IsStillRejected()
    {
        var (ctx, obj, dstFile) = Arrange("JOB-R2I-GUARD");
        var srcFile = Path.Combine(obj.SourcePath, "shared.txt");
        var stateBefore = ctx.LoadStateOrNew();

        var vm = new MigrationSessionViewModel(null, null, _log);
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        // 假引擎在"运行中"这一刻手动点验证：必须被拒（I-1 修好不等于把这道门拆了）
        DiagnosticOutcome? duringRun = null;
        string? reasonDuringRun = null;
        TransferRunnerDelegate engine = (c, hooks, progress, ct, only, force) =>
        {
            // 引擎在跑 ⇒ IsRunning == true
            Assert.True(vm.IsRunning, "夹具前提：假引擎运行期间 IsRunning 必须为 true");
            // 手点验证（与 Step4 页面同一条调用：不传 allowDuringRun）
            vm.VerifyAsync(VerifyLevel.L1_CountSize).GetAwaiter().GetResult();
            duringRun = vm.LastVerifyOutcome.Outcome;
            reasonDuringRun = vm.LastVerifyOutcome.ReasonCode;
            File.Copy(srcFile, dstFile, overwrite: true);
            c.SaveState(new JobState
            {
                JobId = c.JobId,
                Phase = JobPhase.Completed,
                TotalObjects = 1,
                CompletedObjects = 1,
                FailedObjects = 0,
                TotalBytes = stateBefore.TotalBytes,
                CompletedBytes = stateBefore.TotalBytes,
                Percent = 100,
                LastUpdateUtc = DateTime.UtcNow,
            });
            return Task.FromResult(JobPhase.Completed);
        };

        vm.TransferRunner = engine;
        await WithInlineSyncContext(() => vm.RunAsync(password: null));

        Assert.Equal(DiagnosticOutcome.Rejected, duringRun);
        Assert.Equal("verify-already-running", reasonDuringRun);
    }
}
