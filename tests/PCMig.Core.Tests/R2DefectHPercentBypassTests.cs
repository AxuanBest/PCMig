// ============================================================================
//  ★ R2 · 缺陷 H 回归 ★ 「进度 100%」在外壳里的四条绕过通路
// ============================================================================
//
//  ── 背景（R2 接手，2026-10-17）──────────────────────────────────────────────
//  Core 侧 `TransferOrchestrator.Percent(JobState)` 已改为
//      if (IsTrustworthyCompletion(s)) return raw;   else return Math.Min(99.9, raw);
//  函数级不变量「Percent>=100 ⟺ IsTrustworthyCompletion」成立（缺陷 B 已闭环）。
//
//  但**外壳**还有四条通路能绕过它写 100%。由专家审查提出、主会话逐条核实成立：
//    S-1  AdoptExistingJobAsync：`Percent = completed ? 100.0 : …`，按 Phase 判定，
//         且整条路径从不调用 ApplyTrustworthyCompletion ⇒ 载入历史任务后界面 100%
//         而内部认为不可信、页面也不会自动前进。真实存档里 6/77 份 Completed 任务
//         字节不闭环（Δ 分别达 +16.5 亿 / -2^31 / +60 B），会被这条通路显示成 100%。
//    S-2  修复轮：`if (verifyPassed) { Percent = 100.0; }`，verifyPassed 只等于
//         report.OverallPass，与"字节账是否闭环"是两个互不校验的维度。
//    S-3  VerifyAsync：刷新了 TrustworthyCompletion 却**不重算 Percent** ⇒
//         迁移 100% → 点验证 → 发现缺文件 → 可信完成翻 false，百分比却仍是 100，
//         而 _progressSealed 之后没有任何快照能再改它。
//    S-4  ApplySnapshot 兜底 `displayTruth?.Percent ?? (Phase==Completed ? 100.0 : …)`
//         有两重绕过；其中 truth.Percent 在 plannedBytes<=0 且 settled 时直接给 100，
//         而 settled 的来源还包含 CompletedWithErrors。
//
//  ── 本文件钉死的契约 ─────────────────────────────────────────────────────────
//    H-1 载入「Completed 但字节不闭环」的历史任务 ⇒ Percent < 100 且不可信
//    H-2 载入真正闭环的历史任务 ⇒ 仍必须 100% 且可信（不能把正常任务一起打死）
//    H-3 修复轮的 100% 放行条件必须与完成判定同源（源码契约）
//    H-4 校验确证缺失后，百分比必须从 100% 回落
//    H-5 进度真值自身不得在计划字节未知时输出 100
//    H-6 计划已知且已结算时，进度真值仍应为 100（正面基线）
//    H-7 任意形态下 `Percent>=100 ⟹ 可信完成`（表驱动）
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PCMig.Core.Jobs;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using PCMig.WinUI.Presentation;
using Serilog;
using Xunit;

namespace PCMig.Core.Tests;

[Collection("A5-jobdir-isolation")]
public sealed class R2DefectHPercentBypassTests : IDisposable
{
    private readonly string _sandbox;
    private readonly ILogger _log;
    private readonly string? _previousJobsRoot;

    public R2DefectHPercentBypassTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "pcmig-r2-defect-h", Guid.NewGuid().ToString("N"));
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
            JobId = "JOB-R2H",
            Phase = phase,
            TotalObjects = totalObjects,
            CompletedObjects = completedObjects,
            FailedObjects = failedObjects,
            MissingObjects = missingObjects,
            TotalBytes = totalBytes,
            CompletedBytes = completedBytes,
        };

    private JobContext CreateJob(string jobId, JobState state)
    {
        var target = Path.Combine(_sandbox, "target", jobId);
        Directory.CreateDirectory(target);
        var def = new JobDefinition
        {
            JobId = jobId,
            SourceHost = "R2-H-PROBE-HOST",
            TargetRoot = target,
            Sources = { new SourceSpec { Path = @"\\R2-H-PROBE-HOST\share", Kind = ObjectKind.DataVolume } },
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
                    SourcePath = @"\\R2-H-PROBE-HOST\share",
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

    /// <summary>按生产真实签名（14 个参数）构造进度真值。</summary>
    private static ProgressTruthSnapshot Truth(long plannedBytes, long committedBytes, bool settled)
        => ProgressTruthSnapshot.Create(
            plannedBytes: plannedBytes,
            committedBytes: committedBytes,
            inFlightConfirmedBytes: 0,
            currentObjectId: null,
            currentObjectPlannedBytes: 0,
            currentObjectConfirmedBytes: 0,
            attemptEpoch: 0,
            retryState: RetryState.None,
            speedBytesPerSecond: 0,
            settled: settled,
            paused: false,
            targetStatTrusted: false,
            inFlightSource: ProgressTruthSource.None,
            timestampUtc: DateTime.UtcNow);

    /// <summary>本文件的一致性律：显示 100% 必须蕴含可信完成。</summary>
    private static void AssertNoContradiction(double percent, bool trustworthy, string why)
    {
        if (percent >= 100.0)
        {
            Assert.True(trustworthy,
                $"{why}：Percent={percent:0.###}（>=100），但可信完成=false —— " +
                "这正是「进度条满了却不算完成、页面也不自动前进」的自相矛盾（缺陷 H）。");
        }
        if (!trustworthy)
        {
            Assert.True(percent < 100.0,
                $"{why}：不可信完成却显示 Percent={percent:0.###} —— 「100%」与「可信完成」必须同真同假。");
        }
    }

    // ══════════════ H-1 / H-2 · S-1 载入历史任务 ══════════════

    [Fact]
    public async Task H01_AdoptCompletedButBytesNotClosed_MustNotShowHundred()
    {
        // 真实存档形态：字节不闭环（JOB-20261008-123806-a967 是多 60 B；-002749-fd7d 是离谱偏大）。
        var ctx = CreateJob("JOB-R2H-01", State(totalBytes: 1000, completedBytes: 1000));
        ctx.SaveState(State(totalBytes: 1000, completedBytes: 1600));   // 先改成"已完成但账不平"
        var vm = new MigrationSessionViewModel(null, null, _log);

        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir), "Adopt 应成功");

        Assert.False(vm.TrustworthyCompletion,
            "字节不闭环（1600/1000）的 Completed 任务不满足 IsTrustworthyCompletion，可信完成必须为 false。");
        Assert.True(vm.Percent < 100.0,
            $"载入字节不闭环的历史 Completed 任务后 Percent={vm.Percent:0.###} —— 不得显示 100%。");
        AssertNoContradiction(vm.Percent, vm.TrustworthyCompletion, "H-1");
    }

    [Fact]
    public async Task H02_AdoptCompletedAndBytesClosed_StillShowsHundredAndTrustworthy()
    {
        // 正面基线：真正闭环的历史任务必须仍然 100% 且可信（修补不能把正常任务一起打死）。
        var ctx = CreateJob("JOB-R2H-02", State(totalBytes: 1000, completedBytes: 1000));
        var vm = new MigrationSessionViewModel(null, null, _log);

        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        Assert.True(vm.TrustworthyCompletion, "字节闭环的 Completed 任务应判为可信完成。");
        Assert.Equal(100.0, vm.Percent, 6);
        AssertNoContradiction(vm.Percent, vm.TrustworthyCompletion, "H-2");
    }

    // ══════════════ H-3 · S-2 修复轮放行条件同源 ══════════════

    [Fact]
    public void H03_RepairRoundHundredPercentMustGoThroughTrustworthyGate()
    {
        // 修复轮要真跑引擎与复验，代价高且受环境影响；这里钉住"放行条件必须同源"这条契约：
        // 修复轮的 100% 文本与赋值都必须经由可信完成闸门，不得只看 report.OverallPass。
        var src = ReadRepoFile("src/PCMig.WinUI/Presentation/MigrationSessionViewModel.cs");
        var idx = src.IndexOf("修复完成（本次重拷", StringComparison.Ordinal);
        Assert.True(idx > 0, "找不到修复完成文案 —— 本用例需随实现更新。");

        // 取该文案前 900 个字符作为"修复轮 100% 放行段"的判定窗口。
        var window = src.Substring(Math.Max(0, idx - 900), 900);
        Assert.Contains("ApplySettledCompletion", window);
        Assert.DoesNotContain("Percent = 100.0;", window);
    }

    // ══════════════ H-4 · S-3 校验确证缺失后必须回落 ══════════════

    [Fact]
    public async Task H04_VerifyFindsMissing_AfterHundredPercent_MustDropBelowHundred()
    {
        var ctx = CreateJob("JOB-R2H-04", State(totalBytes: 1000, completedBytes: 1000));
        var vm = new MigrationSessionViewModel(null, null, _log);
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));
        Assert.Equal(100.0, vm.Percent, 6);   // 迁移期结论：100%

        // 校验报告：一个对象确证缺失（MissingConfirmed=true）。
        var report = new VerifyReport
        {
            JobId = ctx.Definition.JobId,
            Level = VerifyLevel.L1_CountSize,
            OverallPass = false,
            StartedUtc = DateTime.UtcNow,
            FinishedUtc = DateTime.UtcNow,
            Objects = new List<ObjectVerifyResult>
            {
                new()
                {
                    ObjectId = "object-000001",
                    SourceFiles = 2, TargetFiles = 1,
                    SourceBytes = 1000, TargetBytes = 400,
                    CountMatch = false, BytesMatch = false,
                    MissingSamples = new List<string> { "lost.txt" },
                    MissingConfirmed = true,
                },
            },
        };
        ctx.SaveVerify(report);

        // 生产同序：先落盘证据，再据此重算可信完成与百分比（唯一入口）。
        vm.ApplyVerifyConclusion(ctx, report);

        Assert.Equal(1, ctx.LoadStateOrNew().MissingObjects);
        Assert.False(vm.TrustworthyCompletion, "校验确证缺失后不得再算可信完成。");
        Assert.True(vm.Percent < 100.0,
            $"校验已确证目标少文件，Percent={vm.Percent:0.###} —— 不得仍停 100%，否则「100% + 缺文件」同屏。");
        AssertNoContradiction(vm.Percent, vm.TrustworthyCompletion, "H-4");
    }

    // ══════════════ H-5 / H-6 · S-4 进度真值的 100% ══════════════

    [Fact]
    public void H05_ProgressTruth_UnknownPlannedBytes_MustNotClaimHundred()
    {
        // plannedBytes=0 且 settled 时，旧实现直接给 100.0（完全绕开判据的第二个 100% 源头）。
        var truth = Truth(plannedBytes: 0, committedBytes: 0, settled: true);

        Assert.True(truth.Percent < 100.0,
            $"计划字节未知（plannedBytes=0）时进度真值不得宣称 100%（实得 {truth.Percent:0.###}）—— " +
            "「不知道计划量」不等于「已经传完」。");
    }

    [Fact]
    public void H06_ProgressTruth_KnownPlannedBytesAndSettled_StillHundred()
    {
        // 正面基线：计划量已知、已结算 ⇒ 真值仍应给 100（不能把正常路径也压下去）。
        var truth = Truth(plannedBytes: 1000, committedBytes: 1000, settled: true);
        Assert.Equal(100.0, truth.Percent, 6);
    }

    // ══════════════ H-7 · 全形态一致性（表驱动）══════════════

    [Theory]
    // phase, totalObj, doneObj, failedObj, missingObj, totalBytes, doneBytes, expectTrustworthy
    [InlineData(JobPhase.Completed, 2, 2, 0, 0, 1000, 1000, true)]
    [InlineData(JobPhase.Completed, 2, 2, 0, 0, 1000, 999, false)]      // 对象齐但字节差 1
    [InlineData(JobPhase.Completed, 2, 2, 0, 0, 1000, 1600, false)]     // 字节超出（历史脏数据形态）
    [InlineData(JobPhase.Completed, 2, 2, 0, 1, 1000, 1000, false)]     // 校验确证缺失
    [InlineData(JobPhase.Completed, 2, 1, 0, 0, 1000, 1000, false)]     // 对象数不足
    [InlineData(JobPhase.CompletedWithErrors, 2, 1, 1, 0, 1000, 500, false)]
    [InlineData(JobPhase.Completed, 0, 0, 0, 0, 0, 0, false)]           // 空计划不得算完成
    public void H07_AnyShape_PercentAndTrustworthyMustAgree(
        JobPhase phase, int totalObj, int doneObj, int failedObj, int missingObj,
        long totalBytes, long doneBytes, bool expectTrustworthy)
    {
        var s = State(phase, totalObj, doneObj, failedObj, missingObj, totalBytes, doneBytes);
        var trustworthy = TransferOrchestrator.IsTrustworthyCompletion(s);
        Assert.Equal(expectTrustworthy, trustworthy);

        var percent = TransferOrchestrator.Percent(s);
        AssertNoContradiction(percent, trustworthy, $"H-7 {phase}/{doneObj}of{totalObj}/{doneBytes}of{totalBytes}");
    }

    // ── 读取仓库文件（源码契约用）────────────────────────────────────────

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadRepoFile(string relative)
        => File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));
}
