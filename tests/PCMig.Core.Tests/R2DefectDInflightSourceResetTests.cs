// ============================================================================
//  ★ R2 · 缺陷 D 回归 ★ 「有无可信中途来源」派生位的跨轮泄漏
// ============================================================================
//
//  ── 为什么必须存在（R2 接手审计 · 缺陷 D，2026-10-17）──────────────────────
//  `MigrationSessionViewModel.ResetRunProjection()` 负责把上一轮的**实时投影**整体作废：
//  `_lastCompletedCount` / `TrustworthyCompletion` / `_progressSealed` / `ProgressNeedsAttention` /
//  `_postFinishSnapshotsIgnored` / `_batcher` 与全部丢弃计数都在复位清单里，唯独漏了
//  `_hasTrustedInflightSource`（本文件 :1281 字段、:1286 只读投影、:1293 SyncInflightSourceFlag）。
//
//  而 `SyncInflightSourceFlag()` 的第一句就是去重短路：
//      :1299  if (_hasTrustedInflightSource == has) return;
//      :1300  _hasTrustedInflightSource = has;
//      :1301  Raise(nameof(HasTrustedInflightSource));
//
//  于是出现这种组合：上一轮失败/中断时停在"有可信中途来源"（true），新一次运行开跑后
//  首帧算出的 has 仍是 true（本轮第一个对象刚起、确有来源）⇒ 短路命中 ⇒ **不发通知**。
//  订阅该派生位的读者（PageReadiness / Step3 状态句）拿不到"这是一次新的活动"的翻转，
//  界面上会短暂沿用上一轮的结论 —— 正是「拿不到证据却说有活动」的反面：**有证据却不更新**。
//
//  ── 怎么够得着 ───────────────────────────────────────────────────────────────
//  与 P0StopChain / Batch6 同一策略：`PCMig.Core.Tests.csproj` 把
//  `src\PCMig.WinUI\Presentation\*.cs` 源码链入编译，传输引擎通过产品自带测试缝
//  `MigrationSessionViewModel.TransferRunner` 注入假实现 ⇒ 本文件不启动 robocopy、不触网。
//
//  三条纪律（本文件要钉死的就是它们的**跨轮**版本）：
//    ① 新一轮开跑瞬间（引擎还没报任何快照）必须已经"没有可信来源"——不能继承上一轮的 true；
//    ② 新一轮首帧真的带来可信来源时，**必须**发一次通知（不得被上一轮的残留值短路）；
//    ③ 换任务（ResetForJobSwitch 路径）与同任务重启（RunAsync 路径）两条入口都要复位。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
public sealed class R2DefectDInflightSourceResetTests : IDisposable
{
    private const string ProbeHost = "R2-D-PROBE-HOST";

    private readonly string _sandbox;
    private readonly ILogger _log;
    private readonly string? _previousJobsRoot;

    public R2DefectDInflightSourceResetTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "pcmig-r2-defect-d", Guid.NewGuid().ToString("N"));
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

    private JobContext CreateJob(string jobId, long bytes = 4096)
    {
        var sourcePath = @"\\" + ProbeHost + @"\C$\data";
        var def = new JobDefinition
        {
            JobId = jobId,
            SourceHost = ProbeHost,
            TargetRoot = NewDir("target", jobId),
            Sources = { new SourceSpec { Path = sourcePath, Kind = ObjectKind.DataVolume } },
            Options = new MigrationOptions { Threads = 4 },
        };
        var ctx = new JobManager(_log).Create(def);
        ctx.SavePlan(new MigrationPlan
        {
            JobId = jobId,
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
        });
        ctx.SaveState(new JobState
        {
            JobId = jobId,
            Phase = JobPhase.Running,
            TotalObjects = 1,
            TotalBytes = bytes,
        });
        return ctx;
    }

    /// <summary>构造带"可信中途来源"的真值快照（InFlightSource != None 且 InFlightConfirmedBytes &gt; 0）。</summary>
    private static ProgressTruthSnapshot TruthWithInflight(long planned, long committed, long inFlight)
        => ProgressTruthSnapshot.Create(
            plannedBytes: planned,
            committedBytes: committed,
            inFlightConfirmedBytes: inFlight,
            currentObjectId: "object-000001",
            currentObjectPlannedBytes: planned,
            currentObjectConfirmedBytes: committed + inFlight,
            attemptEpoch: 0,
            retryState: RetryState.None,
            speedBytesPerSecond: 1_000_000,
            settled: false,
            paused: false,
            targetStatTrusted: false,
            inFlightSource: ProgressTruthSource.WorkerIoCounters,
            timestampUtc: new DateTime(2026, 10, 17, 5, 0, 0, DateTimeKind.Utc));

    private static ProgressSnapshot SnapshotWithInflight(long planned, long committed, long inFlight)
        => new(
            JobPhase.Running, 1, 0, 0, planned, committed,
            planned > 0 ? committed * 100.0 / planned : 0,
            1_000_000, 30, "object-000001", @"\\" + ProbeHost + @"\C$\data", "复制中",
            CurrentObjectDoneBytes: committed,
            CurrentObjectTotalBytes: planned,
            Truth: TruthWithInflight(planned, committed, inFlight));

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

    /// <summary>跑一次"脚本化"的运行：<paramref name="body"/> 在引擎内部执行（此刻 IsRunning == true）。</summary>
    private static async Task RunScriptedAsync(
        MigrationSessionViewModel vm,
        Func<IProgress<ProgressSnapshot>, Task> body,
        JobPhase result)
    {
        vm.TransferRunner = (_, _, progress, _, _, _) =>
        {
            body(progress).GetAwaiter().GetResult();
            return Task.FromResult(result);
        };
        await WithInlineSyncContext(() => vm.RunAsync(password: null));
    }

    // ══════════════════════════ D-01 ══════════════════════════

    /// <summary>
    /// 上一轮结束时"有可信中途来源"（true），下一轮**开跑瞬间**必须已经复位为 false。
    /// 引擎体内第一行断言：此刻还没有任何快照进来，绝不能继承上一轮的结论。
    /// </summary>
    [Fact]
    public async Task D01_SecondRun_StartsWithNoTrustedInflightSource()
    {
        var ctx = CreateJob("JOB-R2D-01");
        var vm = new MigrationSessionViewModel(null, null, _log);
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        // ── 第一轮：真的带来可信中途来源（InFlightConfirmedBytes > 0）──
        await RunScriptedAsync(vm, async progress =>
        {
            progress.Report(SnapshotWithInflight(planned: 4096, committed: 1024, inFlight: 512));
            await Task.Yield();
        }, JobPhase.Interrupted);

        Assert.True(vm.HasTrustedInflightSource,
            "前提不成立：第一轮报了 InFlightConfirmedBytes > 0 之后，HasTrustedInflightSource 应当为 true");

        // ── 第二轮：引擎还没报任何快照，就已经是"本轮尚无已确认来源" ──
        bool observedBeforeAnySnapshot = true;
        await RunScriptedAsync(vm, async _ =>
        {
            observedBeforeAnySnapshot = vm.HasTrustedInflightSource;
            await Task.Yield();
        }, JobPhase.Interrupted);

        Assert.False(observedBeforeAnySnapshot,
            "第二轮开跑时仍带着上一轮的 HasTrustedInflightSource=true —— ResetRunProjection() 漏复位 _hasTrustedInflightSource（缺陷 D）");
    }

    // ══════════════════════════ D-02 ══════════════════════════

    /// <summary>
    /// 复位之后，新一轮首帧真的带来可信来源时**必须发一次通知**。
    /// 这是缺陷 D 的另一半危害：残留值会让去重短路命中，读者收不到"本轮有活动"的翻转。
    /// </summary>
    [Fact]
    public async Task D02_SecondRun_RaisesInflightSourceFlagAgain()
    {
        var ctx = CreateJob("JOB-R2D-02");
        var vm = new MigrationSessionViewModel(null, null, _log);
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        var firstRunRaises = 0;
        void CountFirst(object? _, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MigrationSessionViewModel.HasTrustedInflightSource)) firstRunRaises++;
        }

        vm.PropertyChanged += CountFirst;
        await RunScriptedAsync(vm, async progress =>
        {
            progress.Report(SnapshotWithInflight(planned: 4096, committed: 1024, inFlight: 512));
            await Task.Yield();
        }, JobPhase.Interrupted);
        vm.PropertyChanged -= CountFirst;

        Assert.True(firstRunRaises >= 1, $"第一轮应当至少发一次 HasTrustedInflightSource 通知（实测 {firstRunRaises}）");
        Assert.True(vm.HasTrustedInflightSource);

        // ── 第二轮：同样的首帧必须再发一次通知，不能被残留 true 短路 ──
        var secondRunRaises = 0;
        void CountSecond(object? _, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MigrationSessionViewModel.HasTrustedInflightSource)) secondRunRaises++;
        }

        bool atStart = true;
        vm.PropertyChanged += CountSecond;
        await RunScriptedAsync(vm, async progress =>
        {
            atStart = vm.HasTrustedInflightSource;
            progress.Report(SnapshotWithInflight(planned: 4096, committed: 1024, inFlight: 512));
            await Task.Yield();
        }, JobPhase.Interrupted);
        vm.PropertyChanged -= CountSecond;

        Assert.False(atStart, "第二轮开跑时应已是 false（缺陷 D 复位点）");
        Assert.True(vm.HasTrustedInflightSource);
        Assert.True(secondRunRaises >= 1,
            $"第二轮首帧带来可信来源后应当再发一次通知（实测 {secondRunRaises}）—— 为 0 说明被上一轮残留值短路，读者看不到本轮活动");
    }

    // ══════════════════════════ D-03 ══════════════════════════

    /// <summary>
    /// 换任务（ResetForJobSwitch 路径：载入另一个 Job）同样必须复位该派生位。
    /// 上一个任务的"有可信来源"绝不能留给下一个任务看。
    /// </summary>
    [Fact]
    public async Task D03_JobSwitch_AlsoClearsTrustedInflightSource()
    {
        var ctxA = CreateJob("JOB-R2D-03A");
        var ctxB = CreateJob("JOB-R2D-03B");
        var vm = new MigrationSessionViewModel(null, null, _log);

        Assert.True(await vm.AdoptExistingJobAsync(ctxA.JobDir));
        await RunScriptedAsync(vm, async progress =>
        {
            progress.Report(SnapshotWithInflight(planned: 4096, committed: 2048, inFlight: 1024));
            await Task.Yield();
        }, JobPhase.Interrupted);
        Assert.True(vm.HasTrustedInflightSource, "前提不成立：任务 A 结束后应为 true");

        // 换到任务 B
        Assert.True(await vm.AdoptExistingJobAsync(ctxB.JobDir));
        Assert.False(vm.HasTrustedInflightSource,
            "换任务后仍带着上一个任务的 HasTrustedInflightSource=true —— 换任务路径同样要复位（缺陷 D）");
    }
}
