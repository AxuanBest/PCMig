// ============================================================================
//  ★ R2 · 缺陷 A 回归 ★ MissingObjects 必须是活的判据，不能是空转字段
// ============================================================================
//
//  ── 问题（R2 接手审计 · 缺陷 A，2026-10-17）─────────────────────────────────
//  `JobState.MissingObjects` 的设计意图写得很清楚（Models.cs:252-261）：
//      「FailedObjects 来自迁移期回执，MissingObjects 来自校验期枚举对账
//        （verify-report 的 MISMATCH + MissingConfirmed）。真机上二者恰好指同一对象，
//        但存在「迁移报成功、校验却发现目标少了文件」的真实情形，
//        那种情况 FailedObjects=0 而 MissingObjects>0，**绝不能算完成**。」
//  而 `TransferOrchestrator.IsTrustworthyCompletion` 正是靠这一项把关：
//      Phase==Completed ∧ TotalObjects>0 ∧ FailedObjects==0 ∧ **MissingObjects==0**
//      ∧ CompletedObjects>=TotalObjects ∧ CompletedBytes==TotalBytes
//
//  实际源码里：**没有任何生产代码给 MissingObjects 赋过值**（全仓 15 处命中全是
//  定义 / 注释 / 该判据本身 / 测试里手工构造）。于是这一项恒为 0 ——
//  六项判据里最关键的「校验发现目标少文件」那道防线**根本不存在**。
//
//  ── 本文件钉死的五条纪律 ────────────────────────────────────────────────────
//  R2A-1  只有**确证缺失**（MissingConfirmed）才计入，UNKNOWN 的"读不到"决不算缺失
//         （否则会重演 F-1 的"假缺失"事故）；
//  R2A-2  校验结论必须真的写进 job-state：确证缺失 > 0 ⇒ MissingObjects>0，
//         且 IsTrustworthyCompletion 随即变 false；
//  R2A-3  校验没有缺失时必须把旧值**归零**，不得残留上一轮的计数；
//  R2A-4  每次开跑（Run/Resume/Repair 共用入口）必须复位，上一轮的校验结论
//         不能贴在新一轮上；
//  R2A-5  调用点必须真实存在：Verifier 落盘报告后对账、VM 校验后重算可信完成。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PCMig.Core.Jobs;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using PCMig.Core.Verify;
using Serilog.Core;
using Xunit;

namespace PCMig.Core.Tests;

public class R2DefectAMissingObjectsTests
{
    // ───────────────────────── 夹具 ─────────────────────────

    private static JobContext CreateJob(string root, string jobId, long bytes)
    {
        var ctx = new JobContext
        {
            JobDir = Path.Combine(root, jobId),
            Definition = new JobDefinition { JobId = jobId, TargetRoot = Path.Combine(root, "target", jobId) },
            Plan = new MigrationPlan
            {
                JobId = jobId,
                TotalBytes = bytes,
                Objects =
                {
                    new PlannedObject
                    {
                        ObjectId = "object-000001",
                        Kind = ObjectKind.DataVolume,
                        SourcePath = Path.Combine(root, "source", jobId),
                        TargetPath = Path.Combine(root, "target", jobId, "one"),
                        EstimatedBytes = bytes,
                        EstimatedFiles = 1,
                    },
                },
            },
        };
        ctx.EnsureDirs();
        return ctx;
    }

    /// <summary>字节闭环、零失败、全对象完成的 Completed 状态 —— 可信完成的正面基线。</summary>
    private static JobState ClosedCompletedState(JobContext ctx) => new()
    {
        JobId = ctx.JobId,
        Phase = JobPhase.Completed,
        TotalObjects = 1,
        CompletedObjects = 1,
        FailedObjects = 0,
        TotalBytes = ctx.Plan!.TotalBytes,
        CompletedBytes = ctx.Plan.TotalBytes,
        Percent = 100.0,
    };

    private static ObjectVerifyResult Verified(string objectId, string status, bool missingConfirmed, int missingSamples)
        => new()
        {
            ObjectId = objectId,
            Status = status,
            MissingConfirmed = missingConfirmed,
            MissingSamples = Enumerable.Range(0, missingSamples).Select(i => $"file-{i}.bin").ToList(),
            SourceFiles = 10,
            TargetFiles = 10 - missingSamples,
        };

    // ══════════════════════════ R2A-1 ══════════════════════════

    [Fact]
    public void R2A01_OnlyConfirmedMissingCounts_UnknownWithSamplesDoesNot()
    {
        var report = new VerifyReport
        {
            JobId = "J-a1",
            Objects =
            {
                // 确证缺失（两侧都读到了、确实比对出差异）
                Verified("object-000001", "MISMATCH", missingConfirmed: true, missingSamples: 3),
                // ★ F-1 ★ 源/目标不可达时比对过程会把每个源文件都记进 MissingSamples，
                //   那些**不是**缺失证据 —— 绝不能因此把任务判成"少了对象"。
                Verified("object-000002", "UNKNOWN", missingConfirmed: false, missingSamples: 42),
                // 完全一致
                Verified("object-000003", "OK", missingConfirmed: false, missingSamples: 0),
            },
        };

        var counted = VerifyReconciliation.CountMissingObjects(report);

        Assert.Equal(1, counted);
        // 用"读不到"当缺失会计成 2 个 —— 这里明确禁止。
        Assert.NotEqual(2, counted);
    }

    // ══════════════════════════ R2A-2 ══════════════════════════

    [Fact]
    public void R2A02_ConfirmedMissing_IsPersisted_AndFlipsTrustworthyCompletion()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcmig-r2-defect-a", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ctx = CreateJob(root, "J-a2", bytes: 8192);
            ctx.SaveState(ClosedCompletedState(ctx));

            // 迁移期的结论：字节闭环、零失败 ⇒ 现在判"可信完成"（正面基线必须成立）。
            Assert.True(TransferOrchestrator.IsTrustworthyCompletion(ctx.LoadStateOrNew()),
                "字节闭环的 Completed 状态本来就应该算可信完成 —— 基线不成立，后面的判据就没有意义。");

            // 校验期发现"源有目标无"。
            var report = new VerifyReport
            {
                JobId = ctx.JobId,
                Objects = { Verified("object-000001", "MISMATCH", missingConfirmed: true, missingSamples: 2) },
            };
            var counted = VerifyReconciliation.Apply(ctx, report);

            Assert.Equal(1, counted);

            // 结论必须落盘（不是只活在内存里）。
            var reloaded = ctx.LoadStateOrNew();
            Assert.Equal(1, reloaded.MissingObjects);

            // 而且必须立刻推翻"可信完成" —— 这就是 Models.cs 里那句"绝不能算完成"。
            Assert.False(TransferOrchestrator.IsTrustworthyCompletion(reloaded),
                "校验已确证目标少文件，却仍被判为可信完成 —— 这正是缺陷 A 要消灭的假绿。");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ══════════════════════════ R2A-3 ══════════════════════════

    [Fact]
    public void R2A03_CleanVerify_ClearsStaleMissingCount()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcmig-r2-defect-a", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ctx = CreateJob(root, "J-a3", bytes: 4096);
            var stale = ClosedCompletedState(ctx);
            stale.MissingObjects = 3;       // 上一轮校验留下的计数
            // ★ 缺陷 K 契约 ★ 该字段只允许「校验结论所有者」写：普通 SaveState 会按磁盘现值把它覆盖回去。
            ctx.SaveStateWithVerifyConclusion(stale);
            Assert.False(TransferOrchestrator.IsTrustworthyCompletion(ctx.LoadStateOrNew()));

            // 用户补完之后重新校验：这一轮没有任何确证缺失。
            var report = new VerifyReport
            {
                JobId = ctx.JobId,
                Objects = { Verified("object-000001", "OK", missingConfirmed: false, missingSamples: 0) },
            };
            var counted = VerifyReconciliation.Apply(ctx, report);

            Assert.Equal(0, counted);
            Assert.Equal(0, ctx.LoadStateOrNew().MissingObjects);
            Assert.True(TransferOrchestrator.IsTrustworthyCompletion(ctx.LoadStateOrNew()),
                "旧计数没有清掉 ⇒ 补完之后仍然永远不算完成（另一种假红）。");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ══════════════════════════ R2A-4（行为层）══════════════════════════

    private sealed class SuccessWorker : ITransferWorker
    {
        public PCMig.Diagnostics.Abstractions.DiagnosticContext Diagnostics { get; set; }
            = PCMig.Diagnostics.Abstractions.DiagnosticContext.Root(Guid.Empty);
        public bool HasRunningWorker => false;
        public void KillCurrent() { }
        public bool TryGetWorkerReadBytes(out long bytes) { bytes = 0; return false; }

        public Task<RobocopyRunResult> RunPassAsync(
            string src, string dst, MigrationOptions opt, MigrationMatrix matrix,
            PassKind pass, string unicodeLogPath, CancellationToken ct,
            IReadOnlyList<string>? fileList = null, bool restartableLarge = false)
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(dst);
            var payload = Path.Combine(dst, "ok.bin");
            File.WriteAllBytes(payload, new byte[64]);
            return Task.FromResult(new RobocopyRunResult(
                ExitCode: 1, Success: true, Killed: false,
                Duration: TimeSpan.FromMilliseconds(10), LastErrorLine: null));
        }
    }

    [Fact]
    public async Task R2A04_RunStart_ResetsStaleMissingCount()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcmig-r2-defect-a", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const long bytes = 64;
            var ctx = CreateJob(root, "J-a4", bytes);
            Directory.CreateDirectory(Path.Combine(root, "source", "J-a4"));

            var stale = new JobState { JobId = ctx.JobId, Phase = JobPhase.CompletedWithErrors, MissingObjects = 2 };
            // ★ 缺陷 K 契约 ★ 同上：夹具复刻的是「上一轮校验留下的结论」，只能走结论所有者入口。
            ctx.SaveStateWithVerifyConclusion(stale);

            var orch = new TransferOrchestrator(ctx, new MigrationMatrix(), Logger.None, new SuccessWorker());
            await orch.RunAsync(null, ct: default).WaitAsync(TimeSpan.FromSeconds(30));

            // 新一轮开跑必须清掉上一轮的校验结论：它描述的是**旧**一轮的现场。
            Assert.Equal(0, ctx.LoadStateOrNew().MissingObjects);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ══════════════════════════ R2A-5（调用点必须真实存在）══════════════════════════

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
        throw new InvalidOperationException("找不到含 PCMig.sln 的仓库根：" + AppContext.BaseDirectory);
    }

    private static string ReadRepoFile(string relative)
        => File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// 剥掉注释，只留代码 —— 供"不得出现某类型名"这类断言使用。
    ///
    /// 【实现已统一到 <see cref="SourceStripper"/> —— 不要在本文件里另写一份】
    /// 这里曾经是每个测试文件各写一份的块注释正则，而块注释正则会**失控**：
    /// 文档注释里出现字面量 `/*` 极其常见（例如「禁止定义任何 *Orchestrator/*Runner 类型」
    /// 这句注释里就有 `/*Runner`），正则把它当成块注释开头后会一路吃到下一个 `*/`，
    /// 把中间的**全部可执行代码**静默删掉。实测事故：`MigrationSessionViewModel.cs`
    /// 原文 240,758 B 被吞到只剩 90,929 B（丢 62.2%），而本文件的禁词断言当时之所以"通过"，
    /// 正是因为被吞掉的那 107 KB 里含有它要找的名字 —— 断言看着在守，其实全瞎。
    /// 现在统一走状态机实现，并有 <see cref="R2SourceStripperTests"/> 直接盯着它。
    /// </summary>
    private static string StripComments(string text) => SourceStripper.StripComments(text);

    [Fact]
    public void R2A05_ReconciliationIsWiredAtBothEnds()
    {
        // ① 校验侧：报告落盘之后就地对账（顺序必须如此 —— 先落盘再改状态，
        //    否则状态里会出现"缺失数已更新、报告还没写"的窗口）。
        var verifier = StripComments(ReadRepoFile("src/PCMig.Core/Verify/Verifier.cs"));
        var saveVerify = verifier.IndexOf("_ctx.SaveVerify(report)", StringComparison.Ordinal);
        var reconcile = verifier.IndexOf("VerifyReconciliation.Apply(", StringComparison.Ordinal);
        Assert.True(saveVerify > 0, "Verifier 里找不到 _ctx.SaveVerify(report)。");
        Assert.True(reconcile > saveVerify,
            "Verifier 必须在 SaveVerify 之后调用 VerifyReconciliation.Apply —— 否则校验发现的缺失永远写不进 job-state。");

        // ② 外壳侧：拿到校验报告后重算"可信完成"（否则界面仍拿着迁移期的旧结论）。
        //
        // ★ R2 缺陷 H 收敛后（2026-10-17）★ 外壳不再直接调 VerifyReconciliation.Apply，
        //   而是统一走 ApplyVerifyConclusion —— 它是"校验结论落地到界面"的唯一入口，
        //   内部先写 job-state、再重算可信完成与百分比。因此本用例要断言的契约变成两段：
        //     ②-a VerifyAsync 必须调用 ApplyVerifyConclusion(report)（拿到报告就落地）
        //     ②-b ApplyVerifyConclusion 内部必须"先 Apply 再重算"，顺序不可颠倒
        //   这不是放宽断言：如果哪天有人把 ApplyVerifyConclusion 内的 Apply 删掉，
        //   ②-b 会直接失败；如果有人让 VerifyAsync 自己另起一条通路，②-a 会失败。
        var vm = StripComments(ReadRepoFile("src/PCMig.WinUI/Presentation/MigrationSessionViewModel.cs"));
        var verifyStart = vm.IndexOf("public async Task VerifyAsync(", StringComparison.Ordinal);
        Assert.True(verifyStart > 0, "VM 里找不到 VerifyAsync。");
        var verifyBody = vm.Substring(verifyStart, Math.Min(20000, vm.Length - verifyStart));
        Assert.Contains("ApplyVerifyConclusion(ctx, report)", verifyBody, StringComparison.Ordinal);

        var landStart = vm.IndexOf("internal void ApplyVerifyConclusion(", StringComparison.Ordinal);
        Assert.True(landStart > 0, "VM 里找不到 ApplyVerifyConclusion —— 校验结论就没有落地入口。");
        var landBody = vm.Substring(landStart, Math.Min(1200, vm.Length - landStart));
        var landApply = landBody.IndexOf("VerifyReconciliation.Apply(", StringComparison.Ordinal);
        var landSettle = landBody.IndexOf("ApplySettledCompletion(", StringComparison.Ordinal);
        Assert.True(landApply >= 0,
            "ApplyVerifyConclusion 必须调用 VerifyReconciliation.Apply —— 否则校验发现的缺失写不进 job-state。");
        Assert.True(landSettle > landApply,
            "ApplyVerifyConclusion 必须「先落盘证据、再改界面结论」 —— ApplySettledCompletion 不得早于 VerifyReconciliation.Apply。");
    }
}
