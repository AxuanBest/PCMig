using PCMig.Core.Jobs;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using PCMig.Diagnostics.Abstractions;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ NEW-01 专项回归（v0.5.4 Preview.3，2026-10-16 真机）★
///
/// 真机现象（JOB-20261016-190438-5542，robocopy exitBulk=9）：
///   · UI 线程 100% CPU 自旋，WM_NULL 5s 超时（完全无响应）；
///   · DrainOnTick 停止（心跳停在 9695 ms，watchdog 报一次 ui-stall 后再无记录）；
///   · 失败对象 object-000002 的 receipt **从未落盘**；
///   · job-state.json 冻结在 phase=running / completedBytes=30 / percent=0.3578；
///   · 进程 20 分钟后仍未恢复；成功路径（exitBulk=1）不复现。
///
/// 本文件把"引擎侧失败收尾路径"变成**可断言的行为**：无论 robocopy 以哪种失败退出码
///（8=有条目没落到目标 / 9=有失败也有成功 / 11=复制失败）结束，编排器都必须
///   ① 正常返回（不挂起）② 该对象 receipt 落盘 ③ job-state 写成 CompletedWithErrors
///   ④ FailedObjects 正确 ⑤ 失败对象的部分字节不进 CompletedBytes ⑥ 不可能到 100%。
///
/// 纪律：这些用例**不测** UI 层（那是 WinUI 项目的事），只测引擎真值链 —— 因为
/// "receipt 未落盘 + job-state 停在 running" 这两条已经足以证明收尾路径没有走完。
/// </summary>
public class Preview3New01HangTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-new01-" + Guid.NewGuid().ToString("N")[..8]);

    public Preview3New01HangTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* 句柄延迟释放 */ } }

    // ──────────────────────────── 假 Worker ────────────────────────────

    /// <summary>
    /// 以**指定退出码**返回的假 worker：不真跑 robocopy，只复现"退出码 → 结算路径"的分支。
    /// <paramref name="copiedBytes"/> 模拟"失败前已经复制到目标的那部分字节"（真机为 1,990,998,416 B）。
    /// </summary>
    private sealed class ExitCodeWorker : ITransferWorker
    {
        private readonly int _exitCode;
        private readonly long _copiedBytes;
        private readonly bool _writeCopiedBytes;

        public ExitCodeWorker(int exitCode, long copiedBytes = 0, bool writeCopiedBytes = false)
        {
            _exitCode = exitCode;
            _copiedBytes = copiedBytes;
            _writeCopiedBytes = writeCopiedBytes;
        }

        public DiagnosticContext Diagnostics { get; set; } = DiagnosticContext.Root(Guid.Empty);
        public bool HasRunningWorker => false;
        public void KillCurrent() { }
        public bool TryGetWorkerReadBytes(out long bytes) { bytes = 0; return false; }

        public Task<RobocopyRunResult> RunPassAsync(
            string src, string dst, MigrationOptions opt, MigrationMatrix matrix,
            PassKind pass, string unicodeLogPath, CancellationToken ct,
            IReadOnlyList<string>? fileList = null, bool restartableLarge = false)
        {
            // 失败对象在真机上"已经复制了一部分，但有一个文件因权限被拒没能复制"。
            // 这里把那份已落盘的字节真实写进目标目录 —— 它是"失败对象的部分字节"，
            // 必须留在盘上（供 MeasureSettledTarget 实测），但**绝不能**进 CompletedBytes。
            if (_writeCopiedBytes && _copiedBytes > 0)
            {
                Directory.CreateDirectory(dst);
                File.WriteAllBytes(Path.Combine(dst, "already-copied.bin"), new byte[_copiedBytes]);
            }

            return Task.FromResult(new RobocopyRunResult(
                ExitCode: _exitCode,
                Success: _exitCode < 8,
                Killed: false,
                Duration: TimeSpan.FromSeconds(1),
                LastErrorLine: _exitCode >= 8 ? "错误 5 (0x00000005) 正在复制文件 DumpStack.log 拒绝访问。" : null));
        }
    }

    /// <summary>
    /// 挂起的 worker —— **严格遵守 RobocopyRunner 的真实取消契约**。
    ///
    /// ★ NEW-01 分析中的关键教训 ★
    /// 第一版这里写成 <c>await Task.Delay(Infinite, ct)</c>，取消时抛 TaskCanceledException，
    /// 于是用例"失败"了 —— 但那是**假失败**：真实 RobocopyRunner 在
    /// <c>RobocopyRunner.cs:708 catch (OperationCanceledException)</c> 处把取消**吸收**成
    /// <c>return new RobocopyRunResult(-1, false, Killed: true, sw.Elapsed, "canceled")</c>，
    /// 从不把取消异常抛给编排器。假 worker 若不遵守这个契约，测出来的"缺陷"只存在于假件里。
    /// 这里如实复刻真实行为：等取消信号 → 返回 Killed=true（编排器
    /// <c>TransferOrchestrator.cs:1117 if (result.Killed) return null;</c>）。
    /// </summary>
    private sealed class HangingWorker : ITransferWorker
    {
        public DiagnosticContext Diagnostics { get; set; } = DiagnosticContext.Root(Guid.Empty);
        public bool HasRunningWorker => true;
        public void KillCurrent() { }
        public bool TryGetWorkerReadBytes(out long bytes) { bytes = 0; return false; }

        public async Task<RobocopyRunResult> RunPassAsync(
            string src, string dst, MigrationOptions opt, MigrationMatrix matrix,
            PassKind pass, string unicodeLogPath, CancellationToken ct,
            IReadOnlyList<string>? fileList = null, bool restartableLarge = false)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 与 RobocopyRunner.cs:708-712 逐字同义：取消被吸收，返回 Killed=true。
                return new RobocopyRunResult(-1, false, Killed: true, TimeSpan.FromMilliseconds(300), "canceled");
            }
            throw new InvalidOperationException("不可达");
        }
    }

    // ──────────────────────────── 夹具 ────────────────────────────

    /// <summary>构造 2 对象计划：object-000001 成功（由返回码 1 决定），object-000002 失败。</summary>
    private JobContext CreateContext(long goodBytes, long badBytes)
    {
        const string jobId = "J-new01";
        var ctx = new JobContext
        {
            JobDir = Path.Combine(_root, jobId),
            Definition = new JobDefinition { JobId = jobId, TargetRoot = Path.Combine(_root, "target", jobId) },
            Plan = new MigrationPlan
            {
                JobId = jobId,
                TotalBytes = goodBytes + badBytes,
                Objects =
                [
                    new PlannedObject
                    {
                        ObjectId = "object-000001",
                        Kind = ObjectKind.DataVolume,
                        SourcePath = Path.Combine(_root, "source", "good"),
                        TargetPath = Path.Combine(_root, "target", jobId, "good"),
                        EstimatedBytes = goodBytes,
                    },
                    new PlannedObject
                    {
                        ObjectId = "object-000002",
                        Kind = ObjectKind.DataVolume,
                        SourcePath = Path.Combine(_root, "source", "bad"),
                        TargetPath = Path.Combine(_root, "target", jobId, "bad"),
                        EstimatedBytes = badBytes,
                    },
                ],
            },
        };
        ctx.EnsureDirs();
        foreach (var name in new[] { "good", "bad" })
        {
            var dir = Path.Combine(_root, "source", name);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "src.bin"), new byte[16]);
        }
        return ctx;
    }

    /// <summary>按对象 ID 决定退出码的 worker：good ⇒ 1（成功），bad ⇒ 指定失败码。</summary>
    private static ITransferWorker SplitWorker(int badExitCode, long badCopiedBytes)
        => new PerObjectWorker(badExitCode, badCopiedBytes);

    private sealed class PerObjectWorker : ITransferWorker
    {
        private readonly int _badExitCode;
        private readonly long _badCopiedBytes;

        public PerObjectWorker(int badExitCode, long badCopiedBytes)
        {
            _badExitCode = badExitCode;
            _badCopiedBytes = badCopiedBytes;
        }

        public DiagnosticContext Diagnostics { get; set; } = DiagnosticContext.Root(Guid.Empty);
        public bool HasRunningWorker => false;
        public void KillCurrent() { }
        public bool TryGetWorkerReadBytes(out long bytes) { bytes = 0; return false; }

        public Task<RobocopyRunResult> RunPassAsync(
            string src, string dst, MigrationOptions opt, MigrationMatrix matrix,
            PassKind pass, string unicodeLogPath, CancellationToken ct,
            IReadOnlyList<string>? fileList = null, bool restartableLarge = false)
        {
            var isBad = src.EndsWith("bad", StringComparison.OrdinalIgnoreCase);
            if (isBad)
            {
                // 失败对象：把"已复制的部分字节"真实写到盘上（真机 1,990,998,416 B 就是这样躺着的）
                if (_badCopiedBytes > 0)
                {
                    Directory.CreateDirectory(dst);
                    File.WriteAllBytes(Path.Combine(dst, "already-copied.bin"), new byte[_badCopiedBytes]);
                }
                return Task.FromResult(new RobocopyRunResult(
                    ExitCode: _badExitCode,
                    Success: false,
                    Killed: false,
                    Duration: TimeSpan.FromSeconds(1),
                    LastErrorLine: "错误 5 (0x00000005) 正在复制文件 DumpStack.log 拒绝访问。"));
            }

            // 成功对象：把计划字节真实落盘，让 MeasureSettledTarget 量得到
            Directory.CreateDirectory(dst);
            File.WriteAllBytes(Path.Combine(dst, "ok.bin"), new byte[1024]);
            return Task.FromResult(new RobocopyRunResult(
                ExitCode: 1, Success: true, Killed: false,
                Duration: TimeSpan.FromSeconds(1), LastErrorLine: null));
        }
    }

    private static Task<JobPhase> RunAsync(JobContext ctx, ITransferWorker worker)
        => new TransferOrchestrator(ctx, new MigrationMatrix(), Serilog.Core.Logger.None, worker).RunAsync();

    // ──────────────────────── 核心：退码 9 必须正常结算 ────────────────────────

    /// <summary>
    /// ★ NEW-01 主用例 ★ 退码 9（有失败文件也有成功文件）⇒ 编排器必须**正常返回**
    /// CompletedWithErrors，且失败对象的 receipt **必须落盘**。
    ///
    /// 真机失败形态：返回后 job-state 仍停在 running、receipt 目录里没有 object-000002。
    /// 因此本用例的超时保护就是断言的一部分 —— 挂起即失败。
    /// </summary>
    [Fact]
    public async Task N01_ExitCode9_CompletesWithErrors_AndWritesFailedReceipt()
    {
        var ctx = CreateContext(goodBytes: 1024, badBytes: 4096);
        var phase = await RunAsync(ctx, SplitWorker(9, badCopiedBytes: 800))
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(JobPhase.CompletedWithErrors, phase);

        // ① job-state 必须离开 running
        var state = ctx.LoadStateOrNew();
        Assert.Equal(JobPhase.CompletedWithErrors, state.Phase);
        Assert.True(state.Phase != JobPhase.Running, "NEW-01：job-state 不得停留在 running");

        // ② 失败对象的 receipt 必须落盘（真机这条从未发生）
        var receipts = ctx.LoadReceipts(Serilog.Core.Logger.None);
        var bad = receipts.LastOrDefault(r => r.ObjectId == "object-000002");
        Assert.NotNull(bad);
        Assert.Equal(ObjectStatus.Failed, bad!.Status);

        // ③ FailedObjects / CompletedObjects 正确
        Assert.Equal(1, state.FailedObjects);
        Assert.Equal(1, state.CompletedObjects);
    }

    /// <summary>
    /// ★ NEW-01 语义用例 ★ 失败对象"已复制到目标的那部分字节"**绝不能**进 CompletedBytes。
    /// 真机的 35.78% 就是"9 个成功对象之和 + 失败对象已传的 1,990,998,416 B"凑出来的。
    /// </summary>
    [Fact]
    public async Task N01_ExitCode9_FailedPartialBytes_DoNotEnterCompletedBytes()
    {
        var ctx = CreateContext(goodBytes: 1024, badBytes: 4096);
        var phase = await RunAsync(ctx, SplitWorker(9, badCopiedBytes: 800))
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(JobPhase.CompletedWithErrors, phase);

        var state = ctx.LoadStateOrNew();
        // 失败对象实际在盘上躺了 800 B，但它是失败的 ⇒ 不得进入已确认完成字节。
        Assert.True(state.CompletedBytes <= state.TotalBytes,
            $"CompletedBytes={state.CompletedBytes} 不得超过 TotalBytes={state.TotalBytes}");
        Assert.True(state.CompletedBytes < state.TotalBytes,
            "NEW-01：存在失败对象时 CompletedBytes 必须严格小于 TotalBytes");

        // 百分比永远不得因失败对象而变成 100%
        Assert.True(state.Percent < 100.0, $"NEW-01：CompletedWithErrors 不得显示 100%（实际 {state.Percent}）");
        Assert.True(state.Percent <= 99.9, $"NEW-01：CompletedWithErrors 必须封顶 99.9%（实际 {state.Percent}）");
    }

    /// <summary>退码 8（有条目没落到目标）与 11（复制失败）必须走同一条收尾路径，不得挂起。</summary>
    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(11)]
    public async Task N01_FailureExitCodes_AllSettle(int exitCode)
    {
        var ctx = CreateContext(goodBytes: 1024, badBytes: 2048);
        var phase = await RunAsync(ctx, SplitWorker(exitCode, badCopiedBytes: 512))
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(JobPhase.CompletedWithErrors, phase);
        var state = ctx.LoadStateOrNew();
        Assert.Equal(JobPhase.CompletedWithErrors, state.Phase);
        Assert.Equal(1, state.FailedObjects);
        Assert.NotNull(ctx.LoadReceipts(Serilog.Core.Logger.None).LastOrDefault(r => r.ObjectId == "object-000002"));
    }

    /// <summary>退码 1（全部成功）必须到达 Completed 且 100% —— 反向对照，证明上面的断言不是恒真。</summary>
    [Fact]
    public async Task N01_ExitCode1_ReachesCompletedWithHundredPercent()
    {
        var ctx = CreateContext(goodBytes: 1024, badBytes: 1024);
        // 两个对象都走"成功"分支：把 bad 目录也改成会成功 —— 用 same-host worker 不方便，
        // 这里用 badCopiedBytes 让 bad 对象也能落盘，并通过 ExitCode=1 让它算成功。
        var worker = new AllSuccessWorker();
        var phase = await RunAsync(ctx, worker).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(JobPhase.Completed, phase);
        var state = ctx.LoadStateOrNew();
        Assert.Equal(0, state.FailedObjects);
        Assert.True(state.Percent > 99.9, $"真正完成必须是 100%（实际 {state.Percent}）");
    }

    private sealed class AllSuccessWorker : ITransferWorker
    {
        public DiagnosticContext Diagnostics { get; set; } = DiagnosticContext.Root(Guid.Empty);
        public bool HasRunningWorker => false;
        public void KillCurrent() { }
        public bool TryGetWorkerReadBytes(out long bytes) { bytes = 0; return false; }

        public Task<RobocopyRunResult> RunPassAsync(
            string src, string dst, MigrationOptions opt, MigrationMatrix matrix,
            PassKind pass, string unicodeLogPath, CancellationToken ct,
            IReadOnlyList<string>? fileList = null, bool restartableLarge = false)
        {
            Directory.CreateDirectory(dst);
            File.WriteAllBytes(Path.Combine(dst, "ok.bin"), new byte[1024]);
            return Task.FromResult(new RobocopyRunResult(
                ExitCode: 1, Success: true, Killed: false,
                Duration: TimeSpan.FromSeconds(1), LastErrorLine: null));
        }
    }

    /// <summary>
    /// ★ 反例守卫 ★ 挂起的 worker（永不返回）必须能被取消终结 —— 证明"编排器会等引擎结束"
    /// 这条路径有明确的出口，不会把 UI 永远吊在那里。
    /// </summary>
    [Fact]
    public async Task N01_Cancellation_AlwaysReleasesTheEngine()
    {
        var ctx = CreateContext(goodBytes: 1024, badBytes: 1024);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var orch = new TransferOrchestrator(ctx, new MigrationMatrix(), Serilog.Core.Logger.None, new HangingWorker());

        var phase = await orch.RunAsync(null, cts.Token).WaitAsync(TimeSpan.FromSeconds(30));

        // 取消必须让 RunAsync 返回（而不是永远挂着）—— 这是"UI 不会永久等待"的引擎侧保证。
        Assert.True(phase is JobPhase.Interrupted or JobPhase.Paused or JobPhase.Failed or JobPhase.Canceled,
            $"取消后必须离开 Running，实际 {phase}");
    }
}
