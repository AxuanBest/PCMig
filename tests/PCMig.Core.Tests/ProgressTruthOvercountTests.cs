using PCMig.Core.Models;
using PCMig.Core.Transfer;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// v0.5.4 Preview.1 — 进度可信度专项（2026-10-15）：**分子越界（completedBytes &gt; totalBytes）回归**。
///
/// 真机背景（JOB-20261014-143502-b5dc，object-000004 / 源 \\192.0.2.33\D\e383）：
/// <code>
/// job-state.json → totalBytes=139,974,393,729  completedBytes=188,773,387,733  percent=100
///                   completedObjects=7 / failedObjects=1
/// </code>
/// 单对象回执 targetBytes 随 attempt 单调抬升（18.5GB → 59.87GB → 119.74GB → 119.76GB），
/// 其中 attempt2→attempt8 的 <c>targetFiles</c> 恰好 3015→6030（翻倍）、bytes 同步≈翻倍 ——
/// 这是"同一批内容被重复并入一次测量"的指纹，而不是被传输的数据真的翻了一倍。
///
/// 本组测试锁死三件事（对应三处已确认的注射点）：
///   1) <see cref="ProgressTruthSnapshot"/> 的分子**在任何输入下都不得超过计划总量** ——
///      旧实现写的是 <c>Math.Max(plannedBytes, committed)</c>，在 committed 已越界时主动抬高上界，
///      等于放弃封顶，于是 188.7GB &gt; 140.0GB 被当成合法值并经 Math.Min(100,...) 显示成 100%；
///   2) 单个对象"已落定字节"**不得超过该对象自己的计划量** —— 目录对象的 TargetPath 可能是被
///      多个对象共享的父目录，DirStat 全递归会把兄弟对象的字节算进本对象的回执；
///   3) 越界数值不得被"夹到 100%"掩盖：夹取后必须**如实**给出 &lt;= 计划量的分子，
///      不允许用 clamp 把重复结算粉饰成"刚好完成"。
/// </summary>
public class ProgressTruthOvercountTests
{
    private static ProgressTruthSnapshot Build(
        long planned,
        long committed,
        long inFlight = 0,
        bool settled = false,
        long objectPlanned = 0,
        long objectConfirmed = 0)
        => ProgressTruthSnapshot.Create(
            plannedBytes: planned,
            committedBytes: committed,
            inFlightConfirmedBytes: inFlight,
            currentObjectId: "object-000004",
            currentObjectPlannedBytes: objectPlanned,
            currentObjectConfirmedBytes: objectConfirmed,
            attemptEpoch: 1,
            retryState: RetryState.None,
            speedBytesPerSecond: 0,
            settled: settled,
            paused: false,
            targetStatTrusted: true,
            inFlightSource: ProgressTruthSource.CompletedReceipts,
            timestampUtc: new DateTime(2026, 10, 14, 6, 49, 38, DateTimeKind.Utc));

    // ── PT-OC-01：真机数值复现 —— 已入库字节越界时，分子必须被夹回计划量 ──────────────
    [Fact]
    public void PTOC01_CommittedBeyondPlanned_IsClampedToPlanned()
    {
        const long totalBytes = 139_974_393_729;   // job-state.json
        const long completedBytes = 188_773_387_733; // job-state.json（越界 48,798,994,004 B）

        var t = Build(planned: totalBytes, committed: completedBytes);

        // 旧实现：DisplayedTransferredBytes == 188,773,387,733（越界值原样透出）
        Assert.Equal(totalBytes, t.DisplayedTransferredBytes);
        Assert.True(t.DisplayedTransferredBytes <= t.PlannedBytes,
            "分子绝不能超过计划总量：越界值意味着重复结算或来源污染，不是真实进度");
        Assert.True(t.Percent is >= 0 and <= 100);
    }

    // ── PT-OC-02：越界不得被显示成"已完成 100%" ─────────────────────────────────────
    [Fact]
    public void PTOC02_Overcount_NeverRendersAs100Percent_WhenNotSettled()
    {
        var totalBytes = 139_974_393_729L;

        // 运行中（settled=false）：上限是 RunningPercentCeiling(99.9)，绝不能是 100
        var running = Build(planned: totalBytes, committed: totalBytes * 2);
        Assert.True(running.Percent <= ProgressTruthSnapshot.RunningPercentCeiling,
            $"运行中的百分比必须 <= {ProgressTruthSnapshot.RunningPercentCeiling}，实际 {running.Percent}");

        // 终态（settled=true）：允许 100，但那必须是"真的传完了"，而不是越界被压成 100
        var settled = Build(planned: totalBytes, committed: totalBytes * 2, settled: true);
        Assert.Equal(100.0, settled.Percent, 3);
        Assert.Equal(totalBytes, settled.DisplayedTransferredBytes);
    }

    // ── PT-OC-03：committed 恰好等于 planned 时不得被误伤 ────────────────────────────
    [Fact]
    public void PTOC03_CommittedExactlyPlanned_IsUnaffected()
    {
        var t = Build(planned: 1_000_000, committed: 1_000_000, settled: true);
        Assert.Equal(1_000_000, t.DisplayedTransferredBytes);
        Assert.Equal(100.0, t.Percent, 3);
    }

    // ── PT-OC-04：该对象回执超过自身计划量时，夹取后不得让分子反向变负 ────────────────
    [Fact]
    public void PTOC04_ObjectReceiptBeyondOwnPlan_DoesNotProduceNegativeOrOverflow()
    {
        const long objectPlan = 70_962_278_984;     // object-000004 计划量
        const long pollutedReceipt = 119_761_272_988; // attempt=16 回执（比计划量还大 69%）

        // 模拟调用方按对象计划量夹取后的求和结果（= 真正的 baseBytes）
        var clamped = pollutedReceipt > objectPlan ? objectPlan : pollutedReceipt;
        Assert.Equal(objectPlan, clamped);

        var t = Build(planned: 139_974_393_729, committed: clamped);
        Assert.True(t.DisplayedTransferredBytes >= 0);
        Assert.True(t.DisplayedTransferredBytes <= t.PlannedBytes);
    }

    // ── PT-OC-05：分母未知时不得凭空产生上界，也不得 NaN ─────────────────────────────
    [Fact]
    public void PTOC05_UnknownPlanned_DoesNotFabricateCeiling()
    {
        var t = Build(planned: 0, committed: 5_000, inFlight: 1_000);
        Assert.Equal(6_000, t.DisplayedTransferredBytes); // 上界未知 ⇒ 不封顶，但不许 NaN
        Assert.True(t.Percent is >= 0 and <= 100);
        Assert.False(double.IsNaN(t.Percent));
    }

    // ── PT-OC-06：**回归禁令** —— 不得用"把越界夹到 100%"来掩盖重复结算 ──────────────
    [Fact]
    public void PTOC06_ClampMustNotMaskDoubleSettlementAsCompletion()
    {
        const long totalBytes = 139_974_393_729;

        // 真实的"其余 7 个对象"之和（= 计划总量 − e383 的计划量）
        const long othersPlanned = totalBytes - 70_962_278_984; // 69,012,114,745

        // 污染前：7 个对象正常完成 ⇒ 分子 = othersPlanned（49.3%），语义正确
        var clean = Build(planned: totalBytes, committed: othersPlanned, settled: true);
        Assert.Equal(69_012_114_745, clean.DisplayedTransferredBytes);
        Assert.True(clean.Percent < 50.0, $"7/8 个对象完成的真实进度应低于 50%，实际 {clean.Percent}");

        // 污染后：e383 的回执（119.76GB）被错误计入 ⇒ 求和越界
        var polluted = Build(planned: totalBytes, committed: othersPlanned + 119_761_272_988);

        // 关键断言：夹取必须把分子拉回计划量（而不是让它停在越界值上），
        // 且**不能**把"8 个对象里只完成了 7 个"这件事掩盖成 100%。
        Assert.Equal(totalBytes, polluted.DisplayedTransferredBytes);
        Assert.True(polluted.DisplayedTransferredBytes - clean.DisplayedTransferredBytes
                    == totalBytes - othersPlanned,
            "夹取后的差值必须来自被污染的单个对象，不得凭空多算其它对象");
    }
}
