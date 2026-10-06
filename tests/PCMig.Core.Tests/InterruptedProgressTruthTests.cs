// ══════════════════════════════════════════════════════════════════════════════
// ★ Round-3 PHASE A（2026-10-05，执行书 §5 必补测试）★ Interrupted 真值污染回归 TEST A–E
// ══════════════════════════════════════════════════════════════════════════════
//  背景（用户真机视频实测）：暂停前 72~74% / 30~31 GB，暂停瞬间 99.9% / 41.96 GB，
//  Resume 后长期 99.9%，而对象完成数仍是 0/41、当前对象甚至显示 0 B / 42 GB —— 三者不可能
//  同时为真。根因不是呈现层，而是 Core 进度真值被污染：
//      /Z 可续传会先给目标文件**预分配最终长度**（CurrentLargePartial 与
//      TrustsTargetStatForProgress 都据此拒绝信任目标长度），但 MarkInterrupted 旧实现
//      无条件走 MeasureTarget（现名 MeasureSettledTarget）⇒ 把"只真传了 31 GiB、长度却
//      已经 42 GiB"的对象记成 42 GiB ⇒ baseBytes += receipt.TargetBytes ⇒
//      state.CompletedBytes 被抬到计划总量 ⇒ 再被 RunningPercentCeiling 夹成 99.9%。
//
//  ★ 为什么必须有 TEST D ★ 上一轮的验收判据只查"倒退"（rawRegressionBytes），于是把
//  "9.2% → 99.9%" 这个巨大**前跳**判成了"零倒退 ⇒ 通过"。只查倒退是不够的。
//
//  纪律：本文件的 A1/A2 是行为级（真实调用生产方法），A3/D2 是源码契约级（锁住修复形状），
//  B/C/D/E 是显示层行为级。任何一条都不修改业务权威（Completed Receipt / skip / 最终判定）。
// ══════════════════════════════════════════════════════════════════════════════

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using PCMig.WinUI.Presentation;
using Xunit;

namespace PCMig.Core.Tests;

public class InterruptedProgressTruthTests
{
    private const string JobId = "JOB-20261005-140000-r3";
    private const long Planned = 176_900_000_000;          // 176.9 GB（用户真机）
    private const long StopBytes = 43_950_000_000;         // 视频里的 43.95 GB（24.8%）
    private const long PauseBytes = 75_900_000_000;        // 暂停前（≈42.9%）

    private static long Pct(double percent) => (long)(Planned * percent / 100d);

    private static ProgressTruthSnapshot Raw(
        long committedBytes,
        long inFlightConfirmedBytes,
        RetryState retryState = RetryState.None,
        string currentObjectId = "object-000015")
        => ProgressTruthSnapshot.Create(
            plannedBytes: Planned,
            committedBytes: committedBytes,
            inFlightConfirmedBytes: inFlightConfirmedBytes,
            currentObjectId: currentObjectId,
            currentObjectPlannedBytes: 0,
            currentObjectConfirmedBytes: 0,
            attemptEpoch: 1,
            retryState: retryState,
            speedBytesPerSecond: 0,
            settled: false,
            paused: false,
            targetStatTrusted: false,
            inFlightSource: inFlightConfirmedBytes > 0
                ? ProgressTruthSource.WorkerIoCounters
                : ProgressTruthSource.None,
            timestampUtc: DateTime.UtcNow);

    // ── TEST A：/Z 预分配 ⇒ Interrupted 只认真实可信量，绝不认目标长度 ────────────
    [Fact]
    public void TEST_A_SlashZPreallocation_InterruptedUsesTrustedCheckpoint_NotTargetLength()
    {
        const long PlanBytes = 42L * 1024 * 1024 * 1024;      // FileInfo.Length（/Z 已预分配）
        const long TrustedBytes = 31L * 1024 * 1024 * 1024;   // 本趟真正可信确认的落盘量

        var resolved = TransferOrchestrator.ResolveInterruptedConfirmedBytes(TrustedBytes, PlanBytes);

        Assert.Equal(TrustedBytes, resolved);
        Assert.NotEqual(PlanBytes, resolved);

        var percent = resolved * 100d / PlanBytes;
        Assert.InRange(percent, 73.7d, 73.9d);
        Assert.True(percent < ProgressTruthSnapshot.RunningPercentCeiling,
            "Interrupted 结算不得被夹到 99.9% —— 那正是把 /Z 预分配当成已传字节的签名");
    }

    [Fact]
    public void TEST_A2_TrustedCheckpoint_IsClampedToPlannedAndNeverNegative()
    {
        Assert.Equal(100L, TransferOrchestrator.ResolveInterruptedConfirmedBytes(500L, 100L));
        Assert.Equal(0L, TransferOrchestrator.ResolveInterruptedConfirmedBytes(-5L, 100L));
        Assert.Equal(0L, TransferOrchestrator.ResolveInterruptedConfirmedBytes(0L, 100L));
        // 计划量未知（≤0）时不夹取：不知道上界就不要假装知道。
        Assert.Equal(777L, TransferOrchestrator.ResolveInterruptedConfirmedBytes(777L, 0L));
    }

    // 源码契约：Interrupted 结算路径上不得出现任何"读目标长度"的调用。
    [Fact]
    public void TEST_A3_InterruptedSettlementNeverReadsTargetLength()
    {
        var orch = ReadSource(@"src\PCMig.Core\Transfer\TransferOrchestrator.cs");

        var capture = ExtractMethod(orch, "private ObjectReceipt CaptureInterruptedConfirmedProgress");
        Assert.Contains("ResolveInterruptedConfirmedBytes", capture);
        Assert.DoesNotContain("FileInfo", capture);
        Assert.DoesNotContain("EnumerateFiles", capture);
        Assert.DoesNotContain("DirStat.Measure", capture);
        Assert.DoesNotContain("MeasureSettledTarget", capture);

        // MarkInterrupted 必须从调用点接收"可信确认量 + 趟次"，而不是自己去量。
        var mark = ExtractMethod(orch, "private ObjectReceipt MarkInterrupted");
        Assert.Contains("trustedObjectConfirmedBytes", mark);
        Assert.Contains("CaptureInterruptedConfirmedProgress", mark);
        Assert.DoesNotContain("MeasureSettledTarget(receipt, obj)", mark);

        // 名字即纪律：旧的、语义含混的 MeasureTarget 必须已不存在。
        Assert.DoesNotContain("private void MeasureTarget(", orch);
    }

    // ── TEST B：暂停 42.9% → 恢复新基线 24.8% ⇒ 显示停在高水位直到 raw 追平 ──────
    [Fact]
    public void TEST_B_ResumeCatchUp_HoldsHighWaterUntilRawCatchesUp()
    {
        var state = new ContinuationDisplayState();
        Assert.True(state.CaptureCheckpoint(JobId, PauseBytes, 42.9, 0, "pause-requested"));
        Assert.True(state.BeginCatchUp(JobId));

        var sequence = new[] { 24.8d, 30d, 38d, 42.9d, 45d };
        var expected = new[] { 42.9d, 42.9d, 42.9d, 42.9d, 45d };

        for (var i = 0; i < sequence.Length; i++)
        {
            var outcome = state.Apply(Raw(Pct(sequence[i]), 0), JobPhase.Running, JobId);
            Assert.True(outcome.Effective!.Percent >= expected[i] - 0.001d,
                $"第 {i + 1} 个采样（raw {sequence[i]}%）显示不得低于 {expected[i]}%，实际 {outcome.Effective.Percent:0.###}%");
            Assert.True(outcome.Effective.Percent >= sequence[i] - 0.001d,
                "显示值永不低于引擎 raw 真值（高水位只能抬高，不能压低）");
        }

        // raw 追平后自动解除，显示回到 Live 跟随真值。
        Assert.Equal(ContinuationMode.Live, state.Mode);
    }

    // ── TEST C：Running 24.8% → Stop ⇒ 显示保持，不得先归零、不得跳到 99.9 ───────
    [Fact]
    public void TEST_C_StopAtMidPercent_DisplayHoldsAndNeverJumpsToCeiling()
    {
        var state = new ContinuationDisplayState();
        Assert.True(state.CaptureCheckpoint(JobId, StopBytes, 24.8, 0, "user-stop"));

        // Core 把对象收尾为 Interrupted 后，raw 只能按"可信 checkpoint"重建 ⇒ 仍约 24.8%。
        var afterStop = state.Apply(Raw(StopBytes - 50_000_000, 0), JobPhase.Interrupted, JobId);
        Assert.Equal(ContinuationMode.Frozen, afterStop.Mode);
        Assert.True(afterStop.Effective!.Percent >= 24.8 - 0.001d,
            "停止后显示不得低于用户已经看到的 24.8%（不得先归零）");
        Assert.True(afterStop.Effective.Percent < ProgressTruthSnapshot.RunningPercentCeiling,
            "停止后显示不得跳到 99.9%");

        // 极端：Core 重建为 0 也必须停住（这正是 PHASE A 修好之后不该再发生 99.9 的原因）。
        var zero = state.Apply(Raw(0, 0), JobPhase.Interrupted, JobId);
        Assert.True(zero.Effective!.Percent >= 24.8 - 0.001d);
    }

    // ── TEST D：无证据前跳必须被报出来（上一轮判据的漏洞） ──────────────────────
    [Fact]
    public void TEST_D_RawLeapWithoutEvidence_IsReported()
    {
        var state = new ContinuationDisplayState();
        // 9.2% 的动作边界检查点（Round-2 报告里被误判为"通过"的那组数字）。
        state.MarkActionCheckpoint(Pct(9.2));

        // 暂停后 Core 报出 99.9%，期间没有任何可信 Worker I/O 增量 ⇒ 必须报警。
        Assert.True(state.TrackRawForwardLeap(Pct(99.9), Planned, 0L),
            "9.2% → 99.9% 且无 I/O 证据，必须判定为无解释前跳（UnexpectedProgressLeapForward）");
        Assert.True(state.LastRawForwardLeapBytes >= Math.Max(ContinuationDisplayState.ForwardLeapFloorBytes,
            (long)(Planned * ContinuationDisplayState.ForwardLeapPlannedRatio)));
        Assert.True(state.LastRawForwardLeapPercent >= ContinuationDisplayState.ForwardLeapPercentThreshold);

        // 正常前进（有 I/O 证据支撑）不得误报。
        var normal = new ContinuationDisplayState();
        normal.MarkActionCheckpoint(Pct(9.2));
        Assert.False(normal.TrackRawForwardLeap(Pct(30), Planned, Pct(30) - Pct(9.2)));
        Assert.Equal(0, normal.LastRawForwardLeapBytes);

        // 小幅抖动（低于阈值）不得误报。
        var small = new ContinuationDisplayState();
        small.MarkActionCheckpoint(Pct(9.2));
        Assert.False(small.TrackRawForwardLeap(Pct(9.2) + 1024, Planned, 0L));

        // 没有动作边界基线时不做判定（不能凭空报错）。
        Assert.False(new ContinuationDisplayState().TrackRawForwardLeap(Pct(99.9), Planned, 0L));

        // 诊断名必须真的存在于生产代码里。
        var vm = ReadSource(@"src\PCMig.WinUI\Presentation\MigrationSessionViewModel.cs");
        Assert.Contains("UnexpectedProgressLeapForward", vm);
        Assert.Contains("_continuation.MarkActionCheckpoint(displayed);", vm);
    }

    // ── TEST D2：高吞吐下的**正常**前进不得被误判（Round-3 PHASE A 真机校准） ──────
    [Fact]
    public async Task TEST_D2_FastButPlausibleProgress_IsNotReported()
    {
        var state = new ContinuationDisplayState();
        // 真机实测（2026-10-05，PHASE A 闸门）：暂停前 9.0% / 3.97 GB → 暂停后 34.3% / 14.4 GB，
        // 跨度约 2.5 秒。本机 C:→E: 吞吐实测 2.7~5 GB/s ⇒ 这段增量是**真实传输**，
        // 恰恰是修复生效的证明（修复前这里会跳到 99.9% / 41.96 GB）。
        // 若不做时间豁免，这个真实场景会在**每次暂停**时误报，guard 直接废掉。
        // 注意用本轮真机的计划量（42 GiB）而非 Round-2 的 176.9 GB ——
        // 10% 比例阈值随计划量放大，用错量纲会把真实场景和污染场景一起放过。
        const long Plan42 = 42L * 1024 * 1024 * 1024;
        state.MarkActionCheckpoint(3_970_000_000L);
        await Task.Delay(1200);
        Assert.False(state.TrackRawForwardLeap(14_400_000_000L, Plan42, 0L),
            "高吞吐下 2 秒内前进 10 GB 属真实传输，不得被判成无解释前跳");
        Assert.Equal(0, state.LastRawForwardLeapBytes);

        // 同一段增量若出现在"一瞬间"，可解释速率上界吃不下 ⇒ 必须报警（就是污染签名）。
        var instant = new ContinuationDisplayState();
        instant.MarkActionCheckpoint(3_970_000_000L);
        Assert.True(instant.TrackRawForwardLeap(14_400_000_000L, Plan42, 0L),
            "同样的 10.4 GB 若瞬间出现，任何速率都解释不了 ⇒ 必须报警");
    }

    // ── TEST E：显式回滚（RollingBack）允许下降，但必须有解释来源 ────────────────
    [Fact]
    public void TEST_E_ExplicitRollback_AllowsRegressionWithReason()
    {
        var state = new ContinuationDisplayState();
        var first = state.Apply(Raw(Pct(60), 0), JobPhase.Running, JobId);
        Assert.Equal(ContinuationMode.Live, first.Mode);

        // RollingBack：真值回冲（重试/回滚），raw 允许下降 —— 但 RetryState 必须说明原因。
        var rolled = state.Apply(Raw(Pct(50), 0, RetryState.RollingBack), JobPhase.Running, JobId);
        Assert.Equal(ContinuationMode.Live, rolled.Mode);
        Assert.True(rolled.RawRegressionBytes > 0, "回滚导致的下降必须被记录（RawRegressionBytes）");
        Assert.Equal(RetryState.RollingBack, Raw(Pct(50), 0, RetryState.RollingBack).RetryState);

        // 回滚不是"完成回退"：显示层绝不能把它说成 100%，也绝不能写回执。
        Assert.True(rolled.Effective!.Percent < 60d);
    }

    // ── TEST F：Stop→Resume 的 P0 —— 续传基线不得取预分配目标长度 ──────────────────
    //  真机证据（job JOB-20261005-143738-4963，Stop 于 85.7% 后点 Resume）：
    //    ProgressTruthTransition phase=Running previousDisplayedBytes=0
    //      newDisplayedBytes=45097156608 newPercent=99.9 source=ParsedWorkerOutput
    //  ⇒ 0 → 42 GiB 只用了一次轮询，而对象完成数仍 0/41。旧基线
    //    `hasPriorReceipt ? MeasureCurrentTargetBytes(obj) : 0` 取到了 /Z 预分配后的目标长度。
    //  这是与 MarkInterrupted 同源的第二条注入项，必须在同一闸门内一起修掉。
    [Fact]
    public void TEST_F_ResumeBaselineOnPreallocatingPass_NeverUsesTargetLength()
    {
        var src = ReadSource(@"src\PCMig.Core\Transfer\TransferOrchestrator.cs");
        var poll = ExtractMethod(src,
            "private async Task PollProgressAsync(PlannedObject obj, JobState state, long baseBytes,");

        Assert.Contains("var baseline = hasPriorReceipt ? ResolveResumeBaselineBytes(obj) : 0;", poll);
        Assert.DoesNotContain("hasPriorReceipt ? MeasureCurrentTargetBytes(obj)", poll);

        // 预分配通道的判别落在 ResolveResumeBaselineBytes 里（PollProgressAsync 只负责调用它）
        var resolve = ExtractMethod(src, "private long ResolveResumeBaselineBytes(PlannedObject obj)");
        Assert.Contains("ResolveResumeBaselineForPass(_currentPass, measured, trusted, mayPreallocate)", resolve);
        Assert.Contains("var mayPreallocate = obj.UseRestartablePass;", resolve);
        Assert.Contains("var measured = mayPreallocate ? 0L : MeasureCurrentTargetBytes(obj);", resolve);
        // 基线一旦非 0 就必须留痕（下一次真机闸门可以直接从日志读出三个输入）
        Assert.Contains("续传基线：object={ObjectId}", resolve);

        // 纯函数口径：可信 Interrupted 回执优先（任何通道）；会预分配的对象永不采信目标长度
        const long preallocatedTarget = 42L * 1024 * 1024 * 1024;   // /Z 已把目标写成最终长度
        const long trustedCheckpoint = 31L * 1024 * 1024 * 1024;    // 运行时可信确认量
        Assert.Equal(trustedCheckpoint, TransferOrchestrator.ResolveResumeBaselineForPass(PassKind.Bulk, preallocatedTarget, trustedCheckpoint, false));
        Assert.Equal(trustedCheckpoint, TransferOrchestrator.ResolveResumeBaselineForPass(PassKind.Large, preallocatedTarget, trustedCheckpoint, true));
        Assert.Equal(trustedCheckpoint, TransferOrchestrator.ResolveResumeBaselineForPass(PassKind.RootFiles, preallocatedTarget, trustedCheckpoint, false));

        // ★ P0 复现判据 ★：预分配通道上，哪怕目标长度已是 42 GiB 计划量，
        //   只要可信回执是 31 GiB，基线就必须是 31 GiB ⇒ 有效百分比 ≈73.8%，绝不能 99.9%。
        var baseline = TransferOrchestrator.ResolveResumeBaselineForPass(PassKind.Large, preallocatedTarget, trustedCheckpoint, true);
        Assert.NotEqual(preallocatedTarget, baseline);
        var percent = baseline * 100d / preallocatedTarget;
        Assert.InRange(percent, 73.7d, 73.9d);
        Assert.True(percent < 99.9d, "续传基线绝不能把计划总量当成已传完");

        // 无可信回执 ⇒ 0（安全方向：显示滞后可由 continuation 高水位兜住，虚报不可恢复）
        Assert.Equal(0L, TransferOrchestrator.ResolveResumeBaselineForPass(PassKind.Large, preallocatedTarget, 0L, true));
        Assert.Equal(0L, TransferOrchestrator.ResolveResumeBaselineForPass(PassKind.Large, preallocatedTarget, -5L, true));
    }

    // ★ Round-3 PHASE A（Stop 路径 P0 的第三条注入项）★
    //   真机 job JOB-20261005-150030-19a6：Resume 时先跑的是 **Bulk** 通道（robocopy `/MAX:… /MT:16`），
    //   而目标上 6 个 8 GiB 文件是上一趟 Large(/Z) 预分配出来的 ⇒ 旧口径 `Bulk ⇒ 实测目标长度`
    //   当场把基线取成 42 GiB（trace `base=45097156608`、UI 立刻 99.9% / 41.96 GB），
    //   同一刻 robocopy 汇总行却是 `文件: 6 复制 0 跳过 6` / `字节: 42.000 g 0 42.000 g`（零复制）。
    [Fact]
    public void TEST_H_BulkPassOnPreallocatedTarget_NeverUsesMeasuredTargetAsBaseline()
    {
        const long preallocatedTarget = 42L * 1024 * 1024 * 1024;

        // 会预分配的对象 + 无回执 ⇒ 必须是 0，绝不能是 42 GiB
        var baseline = TransferOrchestrator.ResolveResumeBaselineForPass(PassKind.Bulk, preallocatedTarget, 0L, true);
        Assert.Equal(0L, baseline);
        Assert.NotEqual(preallocatedTarget, baseline);

        // 不预分配的纯 Bulk 小文件对象 + 无回执 ⇒ 仍允许实测目标长度当基线（否则会退回"暂停后 0 B"假归零）
        Assert.Equal(preallocatedTarget, TransferOrchestrator.ResolveResumeBaselineForPass(PassKind.Bulk, preallocatedTarget, 0L, false));

        // 有可信回执时任何组合都不允许超过回执（回执是"本趟确认过的字节"的唯一权威）
        Assert.Equal(8L * 1024 * 1024 * 1024, TransferOrchestrator.ResolveResumeBaselineForPass(PassKind.Bulk, preallocatedTarget, 8L * 1024 * 1024 * 1024, true));
        Assert.Equal(8L * 1024 * 1024 * 1024, TransferOrchestrator.ResolveResumeBaselineForPass(PassKind.Bulk, preallocatedTarget, 8L * 1024 * 1024 * 1024, false));
    }

    [Fact]
    public void TEST_F2_TrustedResumeBaselineComesOnlyFromInterruptedReceipt()
    {
        var src = ReadSource(@"src\PCMig.Core\Transfer\TransferOrchestrator.cs");
        var read = ExtractMethod(src, "private long ReadTrustedInterruptedReceiptBytes(PlannedObject obj)");
        // 真值逻辑在 PHASE E 收尾时抽成纯函数（同一对象可能有多份回执 ⇒ 口径必须确定且单调），
        // 因此两条通道都要一起守：读取侧不许碰目标长度，权威解析侧必须只认 Interrupted 回执的字节。
        var resolve = ExtractMethod(src,
            "internal static long ResolveTrustedInterruptedBytes(\n        IEnumerable<ObjectReceipt> receipts, string objectId, long plannedBytes)");

        Assert.Contains("ObjectStatus.Interrupted", resolve);
        Assert.Contains("r.TargetBytes", resolve);
        Assert.Contains("ResolveTrustedInterruptedBytes(receipts, obj.ObjectId, obj.EstimatedBytes)", read);

        // 这条通道同样不许碰目标长度（预分配污染的唯一入口）——读取侧与解析侧都不许
        foreach (var body in new[] { read, resolve })
        {
            Assert.DoesNotContain("FileInfo", body);
            Assert.DoesNotContain("EnumerateFiles", body);
            Assert.DoesNotContain("DirStat.Measure", body);
            Assert.DoesNotContain("MeasureCurrentTargetBytes", body);
            Assert.DoesNotContain("MeasureSettledTarget", body);
        }
    }

    [Fact]
    public void TEST_F3_PreallocatingPassSettlementIgnoresLiveIoIncrement()
    {
        var src = ReadSource(@"src\PCMig.Core\Transfer\TransferOrchestrator.cs");
        var live = ExtractMethod(src, "private long ResolveLiveTrustedObjectBytes(PlannedObject obj)");

        // 预分配通道必须在算 live 增量**之前**就返回采样 checkpoint：
        // I/O 计数器是"从源读出"的累计，/Z 重启续传会重读整段 ⇒ 0.2 秒内就能冲上对象计划量。
        var earlyReturn = live.IndexOf("if (_currentPass != PassKind.Bulk)", StringComparison.Ordinal);
        var liveIncrement = live.IndexOf("var liveIncrement =", StringComparison.Ordinal);
        Assert.True(earlyReturn >= 0, "预分配通道必须有早退分支");
        Assert.True(liveIncrement > earlyReturn, "早退必须发生在计算 live 增量之前");
        Assert.Contains("return sampled;", live);

        // 结算真值仍不许读目标长度（预分配通道那条 early return 已保证）
        Assert.DoesNotContain("MeasureSettledTarget", live);
        Assert.DoesNotContain("FileInfo", live);
    }

    [Fact]
    public void TEST_G_FailedLargeFileCredit_IsRetractedAndNeverStaysInNumerator()
    {
        // ★ Round-3 PHASE A 第 3 轮真机 ★ 目标盘只剩 1.6 GB 时，robocopy 的块缓冲把 6 个大文件的
        //   "新文件 8.0 g" 行与紧随的 `错误 112 (0x00000070) 磁盘空间不足` 挤在同一批里送达。
        //   旧实现只在 /MT 通道（_pendingCredit）能按错误行回冲，/Z 通道按"近似大小"入账后**没有任何回冲键**
        //   ⇒ 42 GiB 虚报永久留在分子上，界面报 42 GB / 42 GB（99.9%）而对象 0/41、目标盘上一个字节都没落盘。
        var src = ReadSource(@"src\PCMig.Core\Transfer\TransferOrchestrator.cs");

        var credit = ExtractMethod(src, "private void CreditCurrentLarge()");
        Assert.Contains("_failedCreditPaths.ContainsKey(src)", credit);
        Assert.Contains("_largeCredited[src] = done;", credit);
        Assert.Contains("_largeFileSourcePath = null;", credit);

        var onError = ExtractMethod(src, "private void OnRunnerErrorLine(string line)");
        Assert.Contains("_largeCredited.TryRemove(srcPath, out var creditedLarge)", onError);
        Assert.Contains("Interlocked.Add(ref _largeCompletedBytes, -creditedLarge);", onError);

        // 入账键必须与失败记忆键**同一键空间**（都是源路径）：/Z 分支若只记目标路径，
        //   OnRunnerErrorLine 里的 srcPath 永远匹配不上，回冲就退化成死代码。
        Assert.Contains("_largeFileSourcePath = sourcePath;", src);
        Assert.Contains("string.Equals(_largeFileSourcePath, srcPath, StringComparison.OrdinalIgnoreCase)", onError);

        // 重置点必须同时清空回冲键，否则上一趟的入账会跨趟残留。
        Assert.True(
            CountOccurrences(src, "_largeCredited.Clear();") >= 2,
            "对象开始与趟次重开两处都必须清空 _largeCredited");
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static string ReadSource(string relativePath)
    {
        var path = Path.Combine(FindRepoRoot(), relativePath);
        Assert.True(File.Exists(path), $"找不到源文件：{path}");
        // 行尾无关：同一提交在 core.autocrlf=false 的检出里是 LF、在 autocrlf=true 的检出里是
        // CRLF（git archive 同样按 autocrlf 展开），源码契约只关心文本形状，不关心换行编码。
        return File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
    }

    /// <summary>从签名处开始按大括号配平截取一个方法体（源码契约测试用）。</summary>
    private static string ExtractMethod(string text, string signature)
    {
        var start = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"源码里找不到方法签名：{signature}");
        var open = text.IndexOf('{', start);
        Assert.True(open > start, $"方法签名后找不到方法体：{signature}");
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0) return text[start..(i + 1)];
            }
        }
        Assert.Fail($"方法体大括号未配平：{signature}");
        return string.Empty;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}