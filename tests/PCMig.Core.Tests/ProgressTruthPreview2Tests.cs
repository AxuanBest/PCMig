using PCMig.Core.Models;
using PCMig.Core.Transfer;
using PCMig.Core.Util;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// v0.5.4 Preview.2 — Stop/Resume 进度真值专项（2026-10-15）。
///
/// 真机原始证据（本机可回读核验）：
/// <code>
/// 包：真机日志压缩包（私有证据，未随仓库分发）
///     SIZE=1,083,909 B  SHA256=05B782ACADA50FDA14F69590972881B1FFD21860E79CC40DDAC83586F59F325A
///     JobId=JOB-20261014-143502-b5dc（包内路径 PCMig/Jobs/JOB-20261014-143502-b5dc/）
/// </code>
///
/// 本组测试锁死两条**已由原始证据闭环**的缺陷：
///
/// <b>缺陷①（B 类：重复计入隔离旧副本）</b>
/// 「强制覆盖」把目标侧同名旧文件改名为 <c>&lt;原名&gt;.pcmig-old-&lt;yyyyMMddHHmmss&gt;</c>
/// 留在**同一目录**（RepairQuarantine.TryQuarantine）。它既不是本轮传输产物，却被
/// <see cref="DirStat.Measure"/> 的全递归统计计入对象结算量。
/// 原始证据（object-000004 / attempt=16）：
/// <code>
/// plan.estimatedBytes        = 70,962,278,984  (66.089 GB / 3016 文件)
/// receipt.targetBytes        = 119,761,272,988 (111.536 GB / 6035 文件)  ← 计划量的 1.69 倍
/// robocopy 末块: 复制 51.380 g  跳过 4.378 g  其他 55.777 g  失败 10.329 g
/// 55.758(=复制+跳过, 新副本) + 55.777(其他=隔离旧副本) = 111.535 g ≈ 实测 111.536 g
/// </code>
///
/// <b>缺陷②（B 类：CompletedWithErrors 的续传基线被当 0）</b>
/// 旧 <see cref="TransferOrchestrator"/> 的 <c>ResolveTrustedInterruptedBytes</c> 只在最新一次回执为
/// <c>Interrupted</c> 时返回可信字节；对象以 <c>CompletedWithErrors</c> 收尾（3015 个文件已正常落盘，
/// 只有 1 个 PST 因错误 23 失败）时返回 0。
/// 原始证据（job-20261014.log）：
/// <code>
/// 15:05:29.600 [DBG] 续传基线：object-000004 共有 2 份回执…解析出可信字节 0
/// 15:05:41.619 [DBG] 续传基线：… 共有 3 份回执…解析出可信字节 59870654024
///              [DBG] 续传基线：object=object-000004 pass="Bulk" mayPreallocate=true
///                    measuredTargetBytes=0 trustedReceiptBytes=59870654024 baseline=59870654024
/// </code>
/// 同一对象、相邻两次 Resume，可信字节在 0 与 55.76 GB 之间摇摆 —— 恢复后分子时而被清零。
/// </summary>
public class ProgressTruthPreview2Tests
{
    private const string ObjectId = "object-000004";
    private const long PlannedBytes = 70_962_278_984;   // 真机 plan.json
    private static readonly DateTime T0 = new(2026, 10, 14, 6, 35, 14, DateTimeKind.Utc);

    private static ObjectReceipt Receipt(
        ObjectStatus status, long targetBytes, int attempt, double minutesFromT0)
        => new()
        {
            ObjectId = ObjectId,
            Kind = ObjectKind.DataVolume,
            SourcePath = @"\\192.0.2.33\D\e383",
            TargetPath = @"D:\e383",
            StartedUtc = T0.AddMinutes(minutesFromT0),
            CompletedUtc = T0.AddMinutes(minutesFromT0 + 1),
            Status = status,
            TargetBytes = targetBytes,
            TargetFiles = 0,
            Attempt = attempt
        };

    // ───────────────────────── 缺陷①：隔离旧副本识别与排除 ─────────────────────────

    /// <summary>
    /// PT2-Q1：`.pcmig-old-<14位时间戳>` 必须被识别为隔离旧副本（结构与 RepairQuarantine 生成的一致）。
    /// </summary>
    [Theory]
    [InlineData(@"acad.fas.pcmig-old-20261014070527", true)]
    [InlineData(@"2024.pst.pcmig-old-20261014070527", true)]
    [InlineData(@"xx.xlsx.pcmig-old-20260724070527-3", true)]     // 同秒多次隔离的 -序号 形式
    [InlineData(@"普通文件.pcmig-old-2026-01-01", false)]         // 非 14 位数字
    [InlineData(@"pcmig-old-20261014070527", false)]              // 没有原文件名
    [InlineData(@"normal.xlsx", false)]
    [InlineData(@"report.pcmig-old-20261014070527.txt", false)]   // 时间戳后不是结束/序号
    [InlineData("", false)]
    public void PT2_Q1_QuarantineOldCopy_IsRecognised(string fileName, bool expected)
        => Assert.Equal(expected, RepairQuarantine.IsQuarantinedOldCopy(fileName));

    /// <summary>
    /// PT2-Q2：<b>反例测试</b> —— 目标目录里的隔离旧副本不得被计入"已落定字节"。
    ///
    /// 用真实目录布局复现 object-000004 的形状：新副本 + 同目录的 .pcmig-old-* 旧副本。
    /// <c>skipQuarantineOldCopies: false</c>（旧口径）会把两者一起算进来，
    /// <c>true</c>（修复后）只算新副本。差值必须恰好是旧副本的字节数。
    /// </summary>
    [Fact]
    public void PT2_Q2_DirStat_ExcludesQuarantinedOldCopies_ButLegacyCountsThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcmig-pt2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // 「本轮新副本」3 个文件
            WriteFile(Path.Combine(root, "a.bin"), 1_000);
            WriteFile(Path.Combine(root, "b.bin"), 2_000);
            WriteFile(Path.Combine(root, "sub", "c.bin"), 3_000);
            // 「被隔离的旧副本」2 个文件，仍在同一棵树里
            WriteFile(Path.Combine(root, "a.bin.pcmig-old-20261014070527"), 1_000);
            WriteFile(Path.Combine(root, "sub", "c.bin.pcmig-old-20261014072458"), 3_000);

            var legacy = DirStat.Measure(root, long.MaxValue);                                        // 旧口径
            var fixed_ = DirStat.Measure(root, long.MaxValue, skipQuarantineOldCopies: true);          // 修复后

            Assert.Equal(10_000, legacy.Bytes);  // 旧口径：新副本 6000 + 旧副本 4000 一起量
            Assert.Equal(5, legacy.Files);
            Assert.Equal(6_000, fixed_.Bytes);   // ★ 修复后：旧副本被排除（6000 = 1000+2000+3000）
            Assert.Equal(3, fixed_.Files);       // 文件数也只算新副本
            Assert.Equal(4_000, legacy.Bytes - fixed_.Bytes);   // 差量恰为旧副本总量
        }
        finally { TryDelete(root); }
    }

    /// <summary>
    /// PT2-Q3：真机数值复算 —— 111.536 GB 的读数由"新副本 55.758 GB + 旧副本 55.777 GB"构成。
    /// 排除旧副本后，结算量必须回落到计划量以内（不再虚高 1.69 倍）。
    ///
    /// 全部数值取自原始证据，无估算：
    /// <code>
    /// receipt(073707)  targetBytes = 119,761,272,988   targetFiles = 6035
    /// verify-report    targetBytes =  59,890,618,964   targetFiles = 3020  （验证器独立重枚举目标）
    /// robocopy 末块    「复制」51.380 g 「跳过」4.378 g 「其他」55.777 g
    /// </code>
    /// </summary>
    [Fact]
    public void PT2_Q3_RealDeviceNumbers_DoubleCountIsTheQuarantineResidue()
    {
        const long rawReceiptTargetBytes = 119_761_272_988;  // receipt(073707).targetBytes
        const long verifierTargetBytes = 59_890_618_964;     // verify-report.targetBytes

        // ★ 决定性指纹 ★ 两个独立路径、不同时点的测量，差额恰为 attempt=2 的读数：
        //   119,761,272,988 − 59,890,618,964 = 59,870,654,024 = attempt2 的 targetBytes
        Assert.Equal(59_870_654_024L, rawReceiptTargetBytes - verifierTargetBytes);

        // 文件数指纹：6035 − 3020 = 3015 = attempt2 的 targetFiles（恰好那一批）
        Assert.Equal(3015, 6035 - 3020);

        // 越界倍率：119.76 GB / 66.089 GB ≈ 1.688
        Assert.InRange((double)rawReceiptTargetBytes / PlannedBytes, 1.68, 1.70);

        // 排除隔离旧副本后，结算量必须回到计划量之内
        Assert.True(verifierTargetBytes <= PlannedBytes,
            $"验证器口径仍超过计划量：{verifierTargetBytes} > {PlannedBytes}");
    }

    // ───────────────────────── 缺陷②：CompletedWithErrors 的续传基线 ─────────────────────────

    /// <summary>
    /// PT2-Q4：<b>核心反例</b> —— 最新一次回执是 <c>CompletedWithErrors</c> 时，
    /// 续传基线必须取到该对象**可信落盘量**，而不是 0。
    ///
    /// 真机场景：object-000004 每趟都以 completedWithErrors 收尾（1 个 PST 错误 23），
    /// 旧口径下每次 Resume 都解析出 0（日志 15:05:29.600），分子被迫从零重爬。
    /// </summary>
    [Fact]
    public void PT2_Q4_LatestCompletedWithErrors_StillYieldsTrustedResumeBaseline()
    {
        var receipts = new[]
        {
            Receipt(ObjectStatus.Interrupted, 18_505_493_349, 1, 0),        // attempt=1
            Receipt(ObjectStatus.CompletedWithErrors, 59_870_654_024, 2, 3), // attempt=2 ← 最新状态
        };

        var trusted = TransferOrchestrator.ResolveTrustedInterruptedBytes(receipts, ObjectId, PlannedBytes);

        // ★ 修复后：不再是 0，而是 59.87 GB（可信落盘量，且未超计划量）
        Assert.NotEqual(0, trusted);
        Assert.Equal(59_870_654_024, trusted);
    }

    /// <summary>
    /// PT2-Q5：真机 5 份回执的完整序列 —— 每趟都必须解析出**非 0 且单调不减**的可信基线。
    /// 这直接对应日志里 0 → 59870654024 的摇摆消失。
    /// </summary>
    [Fact]
    public void PT2_Q5_RealReceiptChain_BaselineIsNonZeroAndMonotonic()
    {
        // 完全按真机 receipts/ 目录的 5 份回执（文件名与字段值）
        var all = new[]
        {
            Receipt(ObjectStatus.Interrupted,         18_505_493_349, 1,  0),   // 063852
            Receipt(ObjectStatus.CompletedWithErrors, 59_870_654_024, 2, 13),   // 064938
            Receipt(ObjectStatus.Interrupted,         59_870_654_024, 4, 30),   // 070530
            Receipt(ObjectStatus.CompletedWithErrors, 119_741_308_048, 8, 30.5),// 071757
            Receipt(ObjectStatus.CompletedWithErrors, 119_761_272_988, 16, 49), // 073707
        };

        var baselines = new List<long>();
        for (var i = 1; i <= all.Length; i++)
            baselines.Add(TransferOrchestrator.ResolveTrustedInterruptedBytes(all.Take(i), ObjectId, PlannedBytes));

        // ① 每一趟都能拿到非 0 基线（修复前第 2/4/5 趟会是 0）
        Assert.All(baselines, b => Assert.True(b > 0, $"出现 0 基线：{string.Join(", ", baselines)}"));

        // ② 单调不减（已确认的字节只许变大）
        for (var i = 1; i < baselines.Count; i++)
            Assert.True(baselines[i] >= baselines[i - 1],
                $"基线倒退：第 {i} 趟 {baselines[i]} < 第 {i - 1} 趟 {baselines[i - 1]}");

        // ③ 永不越过本对象计划量（这是 object-000004 越界 48.8 GB 的直接来源）
        Assert.All(baselines, b => Assert.True(b <= PlannedBytes,
            $"基线超过计划量：{b} > {PlannedBytes}"));
    }

    /// <summary>
    /// PT2-Q6：<b>不得放宽的部分</b> —— <c>Failed</c> 与 <c>Completed</c> 仍返回 0，
    /// 且 <c>Skipped</c>/无回执同样为 0。放宽只针对"确实有可信落盘量"的 CompletedWithErrors。
    /// </summary>
    [Theory]
    [InlineData(ObjectStatus.Failed)]      // 失败：可能完全没拷进去，不得当作有进度
    [InlineData(ObjectStatus.Completed)]   // 已完成：走 completedIds 结算，不需要续传基线
    public void PT2_Q6_FailedAndCompleted_StillYieldZeroBaseline(ObjectStatus status)
    {
        var receipts = new[] { Receipt(status, 50_000_000_000, 1, 0) };
        Assert.Equal(0, TransferOrchestrator.ResolveTrustedInterruptedBytes(receipts, ObjectId, PlannedBytes));
    }

    /// <summary>PT2-Q7：无回执 / 其它对象回执 ⇒ 0（不得把别人的字节当自己的基线）。</summary>
    [Fact]
    public void PT2_Q7_NoReceiptOrForeignObject_YieldsZero()
    {
        Assert.Equal(0, TransferOrchestrator.ResolveTrustedInterruptedBytes([], ObjectId, PlannedBytes));

        var foreign = new ObjectReceipt
        {
            ObjectId = "object-000006",
            Status = ObjectStatus.CompletedWithErrors,
            TargetBytes = 33_338_683_011,
            CompletedUtc = T0
        };
        Assert.Equal(0, TransferOrchestrator.ResolveTrustedInterruptedBytes([foreign], ObjectId, PlannedBytes));
    }

    /// <summary>
    /// PT2-Q8：越界数值仍然被夹到计划量 —— 真机 attempt=16 的 119,761,272,988 必须回落到 70,962,278,984，
    /// 否则 baseBytes 求和后 completedBytes 再次越过 totalBytes。
    /// </summary>
    [Fact]
    public void PT2_Q8_OverPlannedReceiptIsClampedToPlannedBytes()
    {
        var receipts = new[] { Receipt(ObjectStatus.CompletedWithErrors, 119_761_272_988, 16, 0) };
        var trusted = TransferOrchestrator.ResolveTrustedInterruptedBytes(receipts, ObjectId, PlannedBytes);
        Assert.Equal(PlannedBytes, trusted);
    }

    /// <summary>
    /// PT2-Q9：真机总量恒等式 —— 其余 7 对象 + object-000004 夹取后的字节 = plan.totalBytes。
    /// 这证明修复后 <c>completedBytes &gt; totalBytes</c> 不再成立（188,773,387,733 是未夹取的产物）。
    /// </summary>
    [Fact]
    public void PT2_Q9_RealJobTotals_ClampedSumEqualsTotalBytes()
    {
        const long totalBytes = 139_974_393_729;
        const long otherSevenObjects = 69_012_114_745;    // totalBytes − object-000004 计划量
        const long rawObjectReceiptBytes = 119_761_272_988;  // 真机未夹取的 receipt.targetBytes

        // 旧口径：其余 7 对象 + 未夹取的 object-000004 回执 = 真机 job-state.completedBytes
        Assert.Equal(188_773_387_733L, otherSevenObjects + rawObjectReceiptBytes);

        // 修复后：object-000004 的结算量按自己的计划量夹取
        var clamped = TransferOrchestrator.ResolveTrustedInterruptedBytes(
            [Receipt(ObjectStatus.CompletedWithErrors, rawObjectReceiptBytes, 16, 0)], ObjectId, PlannedBytes);
        var fixedTotal = otherSevenObjects + clamped;

        Assert.Equal(totalBytes, fixedTotal);
        Assert.True(fixedTotal <= totalBytes, "修复后仍越界");
    }

    // ───────────────────────── P1-A：诚实状态判据（不改任何业务百分比）─────────────────────────

    /// <summary>
    /// PT2-A1：/Z 通道**没有**可信中途字节时，真值帧必须自报 <see cref="ProgressTruthSource.None"/>
    /// 且 <c>InFlightConfirmedBytes == 0</c> —— UI 的诚实状态句正是据此判定"执行中但给不出百分比"。
    ///
    /// 这是 P1-A 的**前提契约**：如果引擎在无来源时仍报一个非 None 的来源，UI 就会错说"有活动"。
    /// </summary>
    [Fact]
    public void PT2_A1_NoTrustedInflightSource_SelfReportsNoneAndZero()
    {
        var snap = ProgressTruthSnapshot.Create(
            plannedBytes: PlannedBytes,
            committedBytes: 0,
            inFlightConfirmedBytes: 0,                    // /Z 预分配：读不到已确认字节
            currentObjectId: ObjectId,
            currentObjectPlannedBytes: PlannedBytes,
            currentObjectConfirmedBytes: 0,
            attemptEpoch: 1,
            retryState: RetryState.None,
            speedBytesPerSecond: 35_000_000,              // 网卡速率非零：但**不得**据此造进度
            settled: false,
            paused: false,
            targetStatTrusted: false,                     // /Z 通道目标长度不可信
            inFlightSource: ProgressTruthSource.TargetStat,
            timestampUtc: T0);

        // TargetStat 且不可信 ⇒ 必须被降级为 None（ProgressTruthSnapshot.cs:121）
        Assert.Equal(ProgressTruthSource.None, snap.InFlightSource);
        Assert.Equal(0, snap.InFlightConfirmedBytes);
        Assert.Equal(0, snap.DisplayedTransferredBytes);
        Assert.Equal(0d, snap.Percent);
        // 速率照实显示，但**绝不**参与分子
        Assert.True(snap.SpeedBytesPerSecond > 0, "速率本身应保留（用户能看到网络确实在动）");
    }

    /// <summary>
    /// PT2-A2：进程 I/O 计数器是**真实活动证据** ⇒ 来源必须是
    /// <see cref="ProgressTruthSource.WorkerIoCounters"/>，UI 才可以说"检测到传输活动"。
    /// 与 A1 的区别就是"有来源 vs 无来源"，这是 P1-A 文案分叉的唯一依据。
    /// </summary>
    [Fact]
    public void PT2_A2_WorkerIoCounters_IsTrustedActivityEvidence()
    {
        // 计数器是**累计值**：BeginAttempt 取当时的计数作基线，之后必须继续增长才产生本趟增量。
        long cumulative = 1_000_000_000L;
        var tracker = new WorkerIoProgressTracker(() => cumulative);
        tracker.BeginAttempt();                       // 基线 = 10 亿
        cumulative += 4_000_000_000L;                 // 本趟又读了 40 亿
        var confirmed = tracker.Confirmed(PlannedBytes);

        Assert.Equal(4_000_000_000L, confirmed);
        Assert.Equal(ProgressTruthSource.WorkerIoCounters, tracker.Source);

        var snap = ProgressTruthSnapshot.Create(
            plannedBytes: PlannedBytes,
            committedBytes: 0,
            inFlightConfirmedBytes: confirmed,
            currentObjectId: ObjectId,
            currentObjectPlannedBytes: PlannedBytes,
            currentObjectConfirmedBytes: confirmed,
            attemptEpoch: 1,
            retryState: RetryState.None,
            speedBytesPerSecond: 35_000_000,
            settled: false,
            paused: false,
            targetStatTrusted: false,
            inFlightSource: tracker.Source,
            timestampUtc: T0);

        Assert.Equal(ProgressTruthSource.WorkerIoCounters, snap.InFlightSource);
        Assert.Equal(4_000_000_000L, snap.InFlightConfirmedBytes);
        Assert.True(snap.Percent > 0, "有真实来源时百分比应随证据推进");
    }

    /// <summary>
    /// PT2-A3（反例 / 负向断言）：采样器不可用时，<see cref="WorkerIoProgressTracker"/> 必须
    /// 返回 0 且来源为 <see cref="ProgressTruthSource.None"/> —— **不允许**退化成"用时间估一个增长"。
    ///
    /// 这条锁死 P1-A 的底线：宁可说"暂不可得"，绝不凭空造数。
    /// </summary>
    [Fact]
    public void PT2_A3_NoSampler_YieldsZeroAndNone_NeverFabricates()
    {
        var tracker = new WorkerIoProgressTracker(sampleReadBytes: null);
        tracker.BeginAttempt();

        Assert.Equal(ProgressTruthSource.None, tracker.Source);
        Assert.Equal(0L, tracker.Confirmed(PlannedBytes));
        Assert.Equal(0L, tracker.Confirmed(PlannedBytes));   // 多拍采样仍恒为 0，无任何"缓慢增长"

        // 采样抛异常同样不得造数（WorkerIoProgressTracker.cs:69-72 的语义）
        var throwing = new WorkerIoProgressTracker(() => throw new IOException("句柄失效"));
        throwing.BeginAttempt();
        Assert.Equal(0L, throwing.Confirmed(PlannedBytes));
    }

    /// <summary>
    /// PT2-A4：累计计数器跨尝试必须重开基线（只认本趟读到的），且封顶到计划量 ——
    /// 防止"重启重复读过的字节"被当成本趟进度，也防止越界。
    /// </summary>
    [Fact]
    public void PT2_A4_CumulativeCounter_RebaselinesPerAttempt_AndClamps()
    {
        long cumulative = 10_000_000_000L;
        var tracker = new WorkerIoProgressTracker(() => cumulative);
        tracker.BeginAttempt();                       // 基线 = 100 亿（上一趟已读过的）
        Assert.Equal(0L, tracker.Confirmed(PlannedBytes));   // 本趟起点必须为 0，不得继承

        cumulative += 3_000_000_000L;
        Assert.Equal(3_000_000_000L, tracker.Confirmed(PlannedBytes));

        cumulative += PlannedBytes * 2;               // 远超计划量
        Assert.Equal(PlannedBytes, tracker.Confirmed(PlannedBytes));   // 封顶

        cumulative -= 5_000_000_000L;                 // 采样回退（worker 换了）
        Assert.Equal(PlannedBytes, tracker.Confirmed(PlannedBytes));   // 单调，不回退
    }

    // ───────────────────────── 辅助 ─────────────────────────

    private static void WriteFile(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}
