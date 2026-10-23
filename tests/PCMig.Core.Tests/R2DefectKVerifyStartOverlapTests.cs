using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PCMig.Core.Jobs;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using PCMig.Core.Verify;
using PCMig.Diagnostics.Abstractions;
using PCMig.WinUI.Presentation;
using Serilog;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ R2 接手修复 · 缺陷 K（专家 🔴-3 的核实结论）★
///
/// <para>
/// 专家原话是「<c>MissingObjects</c> 覆盖式写可能被静默丢弃（并发无锁）」。核实后机制比"无锁"
/// 更具体，也更容易漏：
/// </para>
/// <list type="number">
///   <item><description>
///     传输引擎在 <c>RunAsync</c> 开头 <c>LoadStateOrNew()</c> 拿一份 <see cref="JobState"/>，
///     之后整个运行期间反复 <c>SaveState(state)</c> —— 用的是**内存里那个对象**，
///     不是每次都重读磁盘。所以它会把别人刚写进磁盘的字段整体覆盖掉。
///   </description></item>
///   <item><description>
///     <c>VerifyReconciliation.Apply</c> 是另一条 Read-Modify-Write：
///     读磁盘 → 改 <c>MissingObjects</c> → 写磁盘。
///   </description></item>
///   <item><description>
///     正常情况下两者不该同时活着：<c>VerifyAsync</c> 有 <c>IsRunning</c> 守卫，
///     运行中拒绝验证。<b>但反方向没有守卫</b> ——
///     <c>CanStart =&gt; !IsRunning &amp;&amp; HasPlan &amp;&amp; TargetRoot 非空</c>，
///     而 <c>VerifyAsync</c> 从不置 <c>IsRunning</c>（它只改 <c>Phase = Verifying</c>）。
///     于是「验证进行中」<c>CanStart</c> 仍为 true，用户点「开始」就能让两条链同时写同一个 JobState。
///   </description></item>
/// </list>
///
/// <para>
/// 后果有两个，都不轻：① 校验刚确证的 <c>MissingObjects</c> 被引擎的下一次 <c>SaveState</c>
/// 用内存旧值（开跑时清成的 0）覆盖 ⇒ 「校验发现目标少文件」这条结论**静默消失**，
/// 而这正是缺陷 A 花力气建立起来的唯一防线；② 外壳的 Percent 会被两条链交替改写。
/// </para>
///
/// <para>
/// 纪律：本组用例只断言"行为可达"与"修好后不可达"，不锁死具体实现（加锁 / 加标志 / 排队都行）。
/// </para>
/// </summary>
[Collection("A5-jobdir-isolation")]
public sealed class R2DefectKVerifyStartOverlapTests : IDisposable
{
    private readonly string _root;
    private readonly ILogger _log = new LoggerConfiguration().CreateLogger();

    public R2DefectKVerifyStartOverlapTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pcmig-r2-defect-k", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("PCMIG_JOBS", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PCMIG_JOBS", null);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>单对象任务：目标与源内容一致（校验会判 OK），状态落在 Completed 且字节闭环。</summary>
    private JobContext CreateJob(string jobId, long bytes = 4096, bool withFailedReceipt = false)
    {
        var src = Path.Combine(_root, jobId, "src");
        var dst = Path.Combine(_root, jobId, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        var file = Path.Combine(src, "a.txt");
        File.WriteAllText(file, new string('x', (int)bytes));

        var mgr = new JobManager(_log);
        var def = new JobDefinition
        {
            JobId = jobId,
            SourceHost = "localhost",
            TargetRoot = dst,
            CreatedUtc = DateTime.UtcNow,
        };
        var ctx = mgr.Create(def);

        var plan = new MigrationPlan
        {
            JobId = jobId,
            TotalBytes = bytes,
            Objects =
            {
                new PlannedObject
                {
                    ObjectId = "object-000001",
                    Kind = ObjectKind.DataVolume,
                    SourcePath = src,
                    TargetPath = dst,
                    EstimatedBytes = bytes,
                    EstimatedFiles = 1,
                },
            },
        };
        ctx.SavePlan(plan);
        ctx.SaveState(new JobState
        {
            JobId = jobId,
            Phase = JobPhase.Completed,
            TotalObjects = 1,
            CompletedObjects = 1,
            FailedObjects = 0,
            MissingObjects = 0,
            TotalBytes = bytes,
            CompletedBytes = bytes,
            Percent = 100,
            LastUpdateUtc = DateTime.UtcNow,
        });

        if (withFailedReceipt)
        {
            ctx.SaveReceipt(new ObjectReceipt
            {
                ObjectId = "object-000001",
                Status = ObjectStatus.Failed,
                Attempt = 1,
                SourcePath = src,
                TargetPath = dst,
                StartedUtc = DateTime.UtcNow.AddSeconds(-1),
                CompletedUtc = DateTime.UtcNow,
                ErrorClass = ErrorClass.Permanent,
                ErrorDetail = "注入的失败回执（让修复轮有活可干）",
            });
        }

        return ctx;
    }

    private static VerifyReport MissingConfirmedReport(string jobId)
        => new()
        {
            JobId = jobId,
            VerifiedUtc = DateTime.UtcNow,
            Level = VerifyLevel.L1_CountSize,
            OverallPass = false,
            RunStatus = "completed",
            Objects =
            {
                new ObjectVerifyResult
                {
                    ObjectId = "object-000001",
                    SourceFiles = 1,
                    TargetFiles = 0,
                    SourceBytes = 4096,
                    TargetBytes = 0,
                    CountMatch = false,
                    BytesMatch = false,
                    MissingConfirmed = true,
                    MissingSamples = { "a.txt" },
                },
            },
        };

    /// <summary>不切换线程的同步上下文：Post 就地执行，让续体留在当前线程（与真机 UI 线程语义一致）。</summary>
    private sealed class InlineSyncContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }

    private static async Task WithInlineSyncContext(Func<Task> body)
    {
        var prev = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        try { await body(); }
        finally { SynchronizationContext.SetSynchronizationContext(prev); }
    }

    // ─────────────────────────────────────────────────────────────────────
    // K01：验证进行中，「开始迁移」必须不可用
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// ★ 本组核心用例 ★ 缺陷 K 的可达路径就这一条：<c>CanStart</c> 只看 <c>!IsRunning</c>，
    /// 而验证只改 <c>Phase</c> 不改 <c>IsRunning</c>。
    ///
    /// <para>
    /// 判据：验证进行中（外壳已进入 <see cref="JobPhase.Verifying"/>）时，
    /// <c>CanStart</c> 必须为 false。修好之后用户点不到「开始」，
    /// 两条 Read-Modify-Write 就不可能同时在飞。
    /// </para>
    /// </summary>
    [Fact]
    public async Task K01_WhileVerifying_StartMustNotBeAvailable()
    {
        var ctx = CreateJob("JOB-R2K-START");
        var vm = new MigrationSessionViewModel(null, null, _log);
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        var observed = new List<(JobPhase Phase, bool CanStart)>();

        // 用真实 VerifyAsync 走一遍，在它真正跑起来的那一刻采样。
        // Verifier 会通过 progress 报告每个对象，正好当采样点。
        // 采样器必须同步执行：Progress<T> 在无 SynchronizationContext 时走线程池，
        // 回调会滞后于真实采样点，采不到"验证进行中"这一刻的真相。
        var probe = new InlineProgress(_ => observed.Add((vm.Phase, vm.CanStart)));

        await WithInlineSyncContext(async () =>
        {
            var task = vm.VerifyAsync(VerifyLevel.L1_CountSize, probe);
            // 验证已开始但尚未结束：此刻 Phase 应为 Verifying、IsRunning 为 false。
            observed.Add((vm.Phase, vm.CanStart));
            await task;
        });

        Assert.NotEmpty(observed);
        var duringVerify = observed.Where(o => o.Phase == JobPhase.Verifying).ToList();
        Assert.True(duringVerify.Count > 0,
            "夹具前提不成立：整个验证过程中没有采到 Phase=Verifying 的样本，" +
            "本用例无法证明「验证中 CanStart 是否可用」。采样=" +
            string.Join(", ", observed.Select(o => $"{o.Phase}/CanStart={o.CanStart}")));

        var offenders = duringVerify.Where(o => o.CanStart).ToList();
        Assert.True(offenders.Count == 0,
            $"验证进行中 CanStart 仍为 true（采到 {offenders.Count} 次）—— " +
            "用户此刻点「开始」会让传输引擎与校验结论同时读改写同一个 job-state：" +
            "引擎持有开跑时载入的那份内存对象并周期性 SaveState，把校验刚确证的 MissingObjects " +
            "整体覆盖回 0，「校验发现目标少文件」这条结论就静默消失了（缺陷 K）。");
    }

    // ─────────────────────────────────────────────────────────────────────
    // K02：校验确证的 MissingObjects 不得被引擎的 SaveState 覆盖
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 直接复刻覆盖现场：校验写进 <c>MissingObjects = 1</c> 之后，模拟引擎用它自己那份
    /// （开跑时载入、<c>MissingObjects = 0</c> 的）内存对象再写一次磁盘。
    ///
    /// <para>
    /// 这是引擎的真实行为：<c>TransferOrchestrator</c> 在 <c>RunAsync</c> 开头 <c>LoadStateOrNew()</c>
    /// 一次，之后所有 <c>SaveState(state)</c> 传的都是那个引用。
    /// 只要"验证中能点开始"这条路存在，覆盖就是必然的，与线程时序无关。
    /// </para>
    ///
    /// <para>
    /// 本用例断言的是**最终事实**：无论覆盖发生与否，磁盘上的 <c>MissingObjects</c>
    /// 必须仍能反映"校验确证目标少文件"。若实现选择"验证中禁止开始"（K01），
    /// 则本用例在正常路径下也自然成立。
    /// </para>
    /// </summary>
    [Fact]
    public void K02_EngineSaveState_MustNotSilentlyDropConfirmedMissingObjects()
    {
        var ctx = CreateJob("JOB-R2K-COVER");

        // ① 校验确证缺失，经唯一出口写盘。
        var applied = VerifyReconciliation.Apply(ctx, MissingConfirmedReport(ctx.JobId));
        Assert.Equal(1, applied);
        Assert.Equal(1, ctx.LoadStateOrNew().MissingObjects);

        // ② 复刻引擎持有旧内存对象并 SaveState 的行为。
        var engineState = new JobState
        {
            JobId = ctx.JobId,
            Phase = JobPhase.Completed,
            TotalObjects = 1,
            CompletedObjects = 1,
            FailedObjects = 0,
            MissingObjects = 0,   // 开跑时被清成 0，之后一直没再读过盘
            TotalBytes = 4096,
            CompletedBytes = 4096,
            Percent = 100,
            LastUpdateUtc = DateTime.UtcNow,
        };
        ctx.SaveState(engineState);

        var after = ctx.LoadStateOrNew();
        Assert.True(after.MissingObjects == 1,
            $"引擎的一次 SaveState 把校验确证的 MissingObjects（1）覆盖成了 {after.MissingObjects}。" +
            "job-state 是「可信完成」判据的唯一数据源，这条字段被覆盖等于校验结论被静默丢弃 —— " +
            "迁移报成功而目标实际少文件时，界面会重新变成 100% + 已完成。");
    }

    // ─────────────────────────────────────────────────────────────────────
    // K03：验证与迁移不得并行（行为级，走真实入口）
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 从真实入口验证互斥：先让一次运行活着（<c>IsRunning = true</c>），
    /// 此时 <c>VerifyAsync</c> 必须仍拒绝（缺陷 I 的 <c>allowDuringRun</c> 默认 false）；
    /// 反向也成立 —— 验证活着时 <c>StartAsync</c> 那条路要么不可用、要么不真正开跑。
    ///
    /// <para>
    /// 判据用"引擎有没有被调用"这个客观事实，而不是按钮的 IsEnabled：
    /// 按钮状态是表现层，入口守卫才是防线。
    /// </para>
    /// </summary>
    [Fact]
    public async Task K03_VerifyAndTransfer_MutuallyExclusiveAtTheEntry()
    {
        var ctx = CreateJob("JOB-R2K-MUTEX");
        var vm = new MigrationSessionViewModel(null, null, _log);
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        // ① 运行中 → 验证必须被拒（这条已由缺陷 I 的 I02 守住，这里作为互斥的另一半再确认）。
        var engineRuns = 0;
        vm.TransferRunner = (c, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref engineRuns);
            Assert.True(vm.IsRunning, "夹具前提：引擎运行时 IsRunning 必须为 true。");
            vm.VerifyAsync(VerifyLevel.L1_CountSize).GetAwaiter().GetResult();
            return Task.FromResult(JobPhase.Completed);
        };

        await WithInlineSyncContext(() => vm.RunAsync(password: null));
        Assert.Equal(1, engineRuns);
        Assert.Equal(DiagnosticOutcome.Rejected, vm.LastVerifyOutcome.Outcome);

        // ② 验证中 → 不得真正开跑第二次传输。
        var secondRun = 0;
        vm.TransferRunner = (c, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref secondRun);
            return Task.FromResult(JobPhase.Completed);
        };

        await WithInlineSyncContext(async () =>
        {
            var verify = vm.VerifyAsync(VerifyLevel.L1_CountSize);
            // 验证在飞：此时再点「开始」。
            await vm.RunAsync(password: null);
            await verify;
        });

        Assert.True(secondRun == 0,
            $"验证进行中又真的启动了一次传输（引擎被调用 {secondRun} 次）。" +
            "两条链会同时读改写同一个 job-state（缺陷 K），最终由后写者决定用户看到的结论。");
    }

    /// <summary>就地把回调跑完的 <see cref="IProgress{T}"/>，用于确定性采样。</summary>
    private sealed class InlineProgress : IProgress<string>
    {
        private readonly Action<string> _onReport;
        public InlineProgress(Action<string> onReport) => _onReport = onReport;
        public void Report(string value) => _onReport(value);
    }}
