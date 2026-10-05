using System.Diagnostics;
using PCMig.Core.Diagnostics;
using PCMig.Core.Jobs;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using PCMig.Diagnostics.Abstractions;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// FIX BATCH 1 — Pause Core Semantics（Trust-Critical Recovery Campaign §4 / §10 P-01…P-12）。
///
/// 这批测试存在的唯一理由：旧实现里 **"写入 Pause Request 成功" ≠ "迁移已暂停"**。
/// 真机证据（JOB-20261004-171522-b785）：8/8 次点击、8/8 请求文件写入成功、0 次引擎确认、传输从未停止。
/// 因此下列断言全部围绕"**业务效果**是否达成"，而不是"请求是否发出"：
///   · 任意模式的暂停请求（含旧 UI 的 Cooperative）都必须**立即打断当前对象**，不得等到对象边界；
///   · 被打断的对象**绝不产生 Completed 回执**（禁止假成功）；
///   · 暂停真值必须落进 job-state.json（Paused + 达成时刻 + 未完成口径）；
///   · worker 拒绝停止时必须在 SLA 内如实报 PauseFailed，且不谎称已暂停。
/// </summary>
public class PauseCoreSemanticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-pause-" + Guid.NewGuid().ToString("N")[..8]);

    public PauseCoreSemanticsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* 文件句柄延迟释放 */ }
    }

    // ───────────────────────── P-01：SLA 必须是秒级真值，而不是"一小时" ─────────────────────────

    [Fact]
    public void P01_PauseSla_IsSeconds_AndTheEngineActuallyUsesIt()
    {
        Assert.Equal(5, PauseSla.AchieveSeconds);
        Assert.Equal(10, PauseSla.HardFailSeconds);

        var src = ReadSource("src", "PCMig.Core", "Transfer", "TransferOrchestrator.cs");
        Assert.Contains("PauseSla.Achieve", src);
        Assert.Contains("PauseSla.HardFail", src);
        // 1 小时（pause.v1 的 ExternalWaitTimeoutMs = 3_600_000）绝不允许再充当"暂停是否达成"的判据。
        Assert.DoesNotContain("3600000", src);
        Assert.DoesNotContain("3_600_000", src);
    }

    // ───────────────── P-02/P-04：Cooperative 请求必须立即打断当前对象，并落真值 ─────────────────

    [Fact]
    public async Task P02_CooperativeRequest_MustInterruptTheCurrentObject_WithinSla()
    {
        var ctx = CreateContext(("object-1", 4L * 1024 * 1024));
        var worker = new ScriptedWorker();
        var run = RunAsync(ctx, worker);
        Assert.True(await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(15)), "worker 必须真的开始复制（前提）");

        ctx.RequestPause(immediate: false);          // ← 旧 UI footer 的真实路径：Cooperative

        var phase = await run.WaitAsync(TimeSpan.FromSeconds(25));
        var state = ctx.LoadStateOrNew();

        Assert.True(worker.KillIssued,
            "Cooperative（旧 UI 默认模式）暂停请求也必须立即打断当前对象；等对象边界 = 28.5 GB 对象下永不暂停");
        Assert.Equal(JobPhase.Paused, phase);
        Assert.Equal(PauseState.Paused, state.PauseState);
        Assert.Equal(PauseOutcomeKind.Achieved, state.PauseOutcome);
        Assert.NotNull(state.PauseRequestedUtc);
        Assert.NotNull(state.PauseAchievedUtc);
        Assert.NotNull(state.PauseElapsedMs);
        Assert.True(state.PauseElapsedMs <= PauseSla.HardFailSeconds * 1000 + 1000,
            $"暂停达成耗时必须落在 SLA 内，实测 {state.PauseElapsedMs} ms");
        Assert.Equal("Cooperative", state.PauseRequestMode);
    }

    [Fact]
    public async Task P04_PausedTruth_IsPersisted_AndDoesNotClaimProgressItDoesNotHave()
    {
        var ctx = CreateContext(("object-1", 4L * 1024 * 1024));
        var worker = new ScriptedWorker();
        var run = RunAsync(ctx, worker);
        Assert.True(await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(15)));

        ctx.RequestPause(immediate: false);
        await run.WaitAsync(TimeSpan.FromSeconds(25));

        // 从磁盘重新读（不是读内存里的对象）：App 崩溃后必须仍能解释"当时暂停到了什么程度"。
        var persisted = ctx.LoadStateOrNew();
        Assert.Equal(JobPhase.Paused, persisted.Phase);
        Assert.Equal(PauseState.Paused, persisted.PauseState);
        Assert.Equal(0, persisted.CompletedObjects);
        // ★ UI Closure 2026-10-05（用户指令 UI-02）★ 旧口径要求暂停时 CompletedBytes 必须为 0，
        //   真机因此出现「暂停后 0.0% / 0 B / 剩余 100% 未传」的假归零。新口径：暂停/中断必须保留
        //   已确认落盘的真实字节（被打断对象也实测落盘量，且累计单调不回退），
        //   但**对象计数绝不谎报完成**——下面 CompletedObjects == 0 与 Percent < 100 两条锁保留不变。
        Assert.True(persisted.CompletedBytes > 0,
            $"暂停必须保留已传字节，实测 {persisted.CompletedBytes} —— 归零即 UI-02 的假进度");
        Assert.True(persisted.CompletedBytes < 4L * 1024 * 1024,
            $"已传字节不得超过该对象计划量（4 MiB），实测 {persisted.CompletedBytes}");
        Assert.True(persisted.Percent < 100.0, $"未完成的运行绝不允许显示 100%（实测 {persisted.Percent}）");
        Assert.Null(persisted.CurrentObjectId);   // 收尾必须清掉"当前对象"指针
    }

    // ───────────────────────── P-03/P-11：被打断的对象绝不产生假成功回执 ─────────────────────────

    [Fact]
    public async Task P03_InterruptedObject_MustNotProduceACompletedReceipt()
    {
        var ctx = CreateContext(("object-1", 4L * 1024 * 1024));
        var worker = new ScriptedWorker();
        var run = RunAsync(ctx, worker);
        Assert.True(await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(15)));

        ctx.RequestPause(immediate: false);
        await run.WaitAsync(TimeSpan.FromSeconds(25));

        var receipts = ctx.LoadReceipts();
        Assert.DoesNotContain(receipts, r => r.Status == ObjectStatus.Completed);
        Assert.All(receipts, r => Assert.Equal(ObjectStatus.Interrupted, r.Status));
        // 复原时权威进度只认 Completed 回执（TransferOrchestrator.cs:335-339）⇒ 该对象一定会被重跑
        Assert.Empty(ReceiptsConsideredComplete(ctx));
    }

    // ───────────────────────── P-06：暂停后没有"幽灵 worker" ─────────────────────────

    [Fact]
    public async Task P06_AfterPauseIsAchieved_NoWorkerIsStillRunning()
    {
        var ctx = CreateContext(("object-1", 4L * 1024 * 1024));
        var worker = new ScriptedWorker();
        var run = RunAsync(ctx, worker);
        Assert.True(await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(15)));

        ctx.RequestPause(immediate: true);
        await run.WaitAsync(TimeSpan.FromSeconds(25));

        Assert.False(worker.HasRunningWorker, "暂停达成后不得还有在跑的 worker（幽灵 robocopy 会让'已暂停'变成假话）");
    }

    // ───────────────── P-08：暂停达成后目标字节必须真的静止（≥5 s） ─────────────────

    [Fact]
    public async Task P08_AfterPause_TargetBytesStopGrowing()
    {
        var ctx = CreateContext(("object-1", 4L * 1024 * 1024));
        var worker = new ScriptedWorker();
        var run = RunAsync(ctx, worker);
        Assert.True(await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(15)));

        ctx.RequestPause(immediate: false);
        await run.WaitAsync(TimeSpan.FromSeconds(25));

        var target = Path.Combine(ctx.Plan!.Objects[0].TargetPath, "big.bin");
        Assert.True(File.Exists(target), "前提：暂停发生在对象内部复制期间，目标上应当留下未写完的部分文件");
        var atPause = new FileInfo(target).Length;

        await Task.Delay(PauseSla.QuiescentConfirmMs);
        var after5s = new FileInfo(target).Length;

        Assert.Equal(atPause, after5s);
        Assert.True(atPause > 0, "暂停前必须真的已经写了字节（否则这条断言毫无意义）");
    }

    // ───────────────── P-05：worker 拒绝停止 ⇒ 必须如实报 PauseFailed，绝不谎称已暂停 ─────────────────

    [Fact]
    public async Task P05_WorkerThatRefusesToStop_MustReportPauseFailed_NotPaused()
    {
        var ctx = CreateContext(("object-1", 4L * 1024 * 1024));
        var worker = new ScriptedWorker { RefuseToDie = true };
        var run = RunAsync(ctx, worker);
        Assert.True(await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(15)));

        ctx.RequestPause(immediate: false);

        var failed = await WaitForAsync(
            () => ctx.LoadStateOrNew().PauseState == PauseState.PauseFailed,
            TimeSpan.FromSeconds(PauseSla.HardFailSeconds + 8));
        var state = ctx.LoadStateOrNew();

        Assert.True(failed, "worker 在硬失败 SLA（10 s）内拒绝停止时，必须如实落 PauseFailed");
        Assert.Equal(PauseOutcomeKind.Failed, state.PauseOutcome);
        Assert.False(string.IsNullOrWhiteSpace(state.PauseFailureReason), "PauseFailed 必须给出可解释的失败原因");
        Assert.NotEqual(JobPhase.Paused, state.Phase);        // 迁移仍在进行 —— 不谎称已暂停
        Assert.Null(state.PauseAchievedUtc);                  // 没达成就不许有达成时刻

        worker.Release();
        var phase = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(JobPhase.Completed, phase);              // 确实"仍在进行"，而且真的跑完了
    }

    // ───────────────── P-07：Resume 必须重跑被暂停的对象并真正收敛 ─────────────────

    [Fact]
    public async Task P07_ResumeAfterPause_MustRerunTheObject_AndConvergeToSource()
    {
        var ctx = CreateContext(("object-1", 4096));

        // ① 第一次运行：对象内部复制期间被暂停 ⇒ Interrupted（不是 Completed）
        var paused = new ScriptedWorker();
        var run1 = RunAsync(ctx, paused);
        Assert.True(await paused.Started.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        ctx.RequestPause(immediate: false);
        Assert.Equal(JobPhase.Paused, await run1.WaitAsync(TimeSpan.FromSeconds(25)));

        // ② Resume 的真实第一步：清掉暂停请求文件
        ctx.ClearPauseRequest();
        Assert.False(File.Exists(ctx.PauseRequestPath));

        // ③ 第二次运行：对象必须被重跑（而不是因为"上一趟看起来传过了"被跳过），并真正收敛
        var resume = new ScriptedWorker { MirrorSourceTree = true };
        var phase = await RunAsync(ctx, resume).WaitAsync(TimeSpan.FromSeconds(30));
        var state = ctx.LoadStateOrNew();

        Assert.True(resume.Ran, "被暂停的对象在 Resume 后必须重跑（权威进度只认 Completed 回执）");
        Assert.Equal(JobPhase.Completed, phase);
        Assert.Equal(1, state.CompletedObjects);
        var receipt = Assert.Single(ctx.LoadReceipts());
        Assert.Equal(ObjectStatus.Completed, receipt.Status);
        Assert.Equal(4096, receipt.TargetBytes);
        Assert.Equal(4096, new FileInfo(Path.Combine(ctx.Plan!.Objects[0].TargetPath, "src.bin")).Length);
    }

    // ─────────────────────────────── 测试夹具 ───────────────────────────────

    private JobContext CreateContext(params (string Id, long Bytes)[] objects)
    {
        const string jobId = "J-pause";
        var ctx = new JobContext
        {
            JobDir = Path.Combine(_root, jobId),
            Definition = new JobDefinition { JobId = jobId, TargetRoot = Path.Combine(_root, "target", jobId) },
            Plan = new MigrationPlan
            {
                JobId = jobId,
                TotalBytes = objects.Sum(x => x.Bytes),
                Objects = objects.Select(x => new PlannedObject
                {
                    ObjectId = x.Id,
                    Kind = ObjectKind.DataVolume,
                    SourcePath = Path.Combine(_root, "source", x.Id),
                    TargetPath = Path.Combine(_root, "target", jobId, x.Id),
                    EstimatedBytes = x.Bytes
                }).ToList()
            }
        };
        ctx.EnsureDirs();

        // 真实源数据（Resume 收敛断言要用它）
        foreach (var (id, bytes) in objects)
        {
            var dir = Path.Combine(_root, "source", id);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "src.bin"), new byte[bytes]);
        }
        return ctx;
    }

    private static Task<JobPhase> RunAsync(JobContext ctx, ITransferWorker worker)
        => new TransferOrchestrator(ctx, new MigrationMatrix(), Serilog.Core.Logger.None, worker).RunAsync();

    private static List<ObjectReceipt> ReceiptsConsideredComplete(JobContext ctx)
        => ctx.LoadReceipts().Where(r => r.Status == ObjectStatus.Completed).ToList();

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    private static string ReadSource(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
    }

    /// <summary>
    /// 受控 worker（Batch-1 测试 seam）。
    /// 生产实现是 <see cref="RobocopyRunner"/>（真实 robocopy + 进程树强杀）；这里只替换"被复制的东西"，
    /// 用来把两件在真机上偶发、难以稳定复现的事实变成可断言的事实：
    ///   ① worker 是否真的被打断（KillIssued）；② worker 拒绝停止时引擎是否如实报 PauseFailed。
    /// 它**不**替代真机/实验室的 1–5 GB 数据验证（见 RECOVERY-GATE）。
    /// </summary>
    private sealed class ScriptedWorker : ITransferWorker
    {
        public DiagnosticContext Diagnostics { get; set; } = CoreDiagnostics.ContextFor("ScriptedWorker");

        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool KillIssued;
        public bool RefuseToDie;
        /// <summary>true = 把源目录镜像到目标（模拟"这个对象真的跑完了一遍"）。</summary>
        public bool MirrorSourceTree;
        public volatile bool Ran;
        private volatile bool _running;
        private volatile bool _release;

        public bool HasRunningWorker => _running;
        public bool LastKillSucceeded => KillIssued;

        /// <summary>FIX BATCH 4：受控 worker 不产生内核 I/O 计数器 ⇒ 如实返回"不可用"（UI 保留上一次确认值）。</summary>
        public bool TryGetWorkerReadBytes(out long bytes) { bytes = 0; return false; }

        public void Release() => _release = true;

        public async Task<RobocopyRunResult> RunPassAsync(string src, string dst, MigrationOptions opt,
            MigrationMatrix matrix, PassKind pass, string unicodeLogPath, CancellationToken ct,
            IReadOnlyList<string>? fileList = null, bool restartableLarge = false)
        {
            _running = true;
            Ran = true;
            var sw = Stopwatch.StartNew();
            try
            {
                if (MirrorSourceTree)
                {
                    Directory.CreateDirectory(dst);
                    // 真镜像语义：源里有什么，目标就只剩什么。
                    // 这里顺带把"上一趟暂停留下的部分文件"清掉——正是"当前对象的未完成产物如何回收"这个
                    // 数据安全问题的模型（真实 robocopy 靠 /Z 重启模式 + 增量覆盖达成同样效果）。
                    foreach (var f in Directory.EnumerateFiles(dst))
                        if (!File.Exists(Path.Combine(src, Path.GetFileName(f))))
                            File.Delete(f);
                    foreach (var f in Directory.EnumerateFiles(src))
                        File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
                    return new RobocopyRunResult(1, true, false, sw.Elapsed, null);
                }

                Directory.CreateDirectory(dst);
                var target = Path.Combine(dst, "big.bin");
                var buf = new byte[256 * 1024];
                await using var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read);
                Started.TrySetResult(true);
                while (!KillIssued && !_release && !ct.IsCancellationRequested && sw.Elapsed < TimeSpan.FromSeconds(40))
                {
                    await fs.WriteAsync(buf, ct);
                    await fs.FlushAsync(ct);
                    await Task.Delay(20, ct);
                }
                return new RobocopyRunResult(KillIssued ? -1 : 1, !KillIssued, KillIssued, sw.Elapsed,
                    KillIssued ? "killed-by-us" : null);
            }
            catch (OperationCanceledException)
            {
                return new RobocopyRunResult(-1, false, true, sw.Elapsed, "canceled");
            }
            finally { _running = false; }
        }

        public void KillCurrent()
        {
            if (!RefuseToDie) KillIssued = true;
        }
    }
}