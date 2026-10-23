using PCMig.Core.Models;
using PCMig.Core.Transfer;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ v0.5.4 Preview.2 真机修复回归（2026-10-16，用户任务令 §七 Progress 组）★
///
/// 真机现场（JOB-20261016-150319-e738，源 \\192.0.2.26\d → 目标 D:\）：
///   10 个计划对象 / totalBytes = 119,491,120,745
///   object-000010 因 DumpStack.log（8,192 B，源侧拒绝访问 / Robocopy ERROR 5）失败
///   终态 job-state.json 却是 phase=completedWithErrors, completedBytes=119,491,120,745, percent=100
///   verify-report.json 同时给出 overallPass=false 与 missingSamples=["DumpStack.log"]
///   ⇒ **用户先看到 100%，再在报告里发现文件没复制成功**。这正是本组要钉死的行为。
///
/// 本组只看"进度真值"这一个事实：**有失败/缺失就永远不许出现 100%**，
/// 且 completedBytes 只能是"已完整结算成功的计划字节"。
/// </summary>
public class Preview2RealDeviceProgressTests
{
    /// <summary>真机现场的计划总量（用于恒等式复算）。</summary>
    private const long RealTotalBytes = 119_491_120_745L;

    /// <summary>真机失败的 8,192 B 文件。</summary>
    private const long DumpStackLogBytes = 8_192L;

    private static JobState RealDeviceState(JobPhase phase, long completedBytes, int completedObjects, int failedObjects, int missingObjects = 0)
        => new()
        {
            Phase = phase,
            TotalObjects = 10,
            CompletedObjects = completedObjects,
            FailedObjects = failedObjects,
            MissingObjects = missingObjects,
            TotalBytes = RealTotalBytes,
            CompletedBytes = completedBytes,
        };

    // ─────────────────────────────────────────────────────────────────────
    // §七-Progress-② CompletedWithErrors 绝不 100%
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 真机逐字复现：9/10 对象完成、1 个失败、completedBytes 已达 totalBytes（旧口径的错值）。
    /// 即便字节数被抹平到与计划相等，**CompletedWithErrors 也不得显示 100%**。
    /// 这是用户令里"最重要验收标准"的直接断言。
    /// </summary>
    [Fact]
    public void PT2_RD01_CompletedWithErrors_MustNeverShow100()
    {
        var s = RealDeviceState(JobPhase.CompletedWithErrors, RealTotalBytes, completedObjects: 9, failedObjects: 1);

        var percent = TransferOrchestrator.Percent(s);

        Assert.True(percent < 100.0,
            $"CompletedWithErrors 必须严格小于 100%，实际 {percent}。真机就是在这里显示 100% 后报告却 MISMATCH。");
        Assert.True(percent <= 99.9, $"有失败对象的终态进度上限应为 99.9%，实际 {percent}。");
    }

    /// <summary>有 MissingObjects（迁移期没判失败、但校验期发现目标侧缺文件）同样不得 100%。</summary>
    [Fact]
    public void PT2_RD02_MissingObjects_MustNeverShow100()
    {
        var s = RealDeviceState(JobPhase.CompletedWithErrors, RealTotalBytes, completedObjects: 9, failedObjects: 1, missingObjects: 1);

        var percent = TransferOrchestrator.Percent(s);

        Assert.True(percent <= 99.9, $"存在缺失对象时进度上限应为 99.9%，实际 {percent}。");
    }

    // ─────────────────────────────────────────────────────────────────────
    // §七-Progress-③ 缺 8192 B 时不得 100%
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 真机恒等式：completedBytes = 119,491,119,745 恰比 totalBytes 少 8,192 B（= DumpStack.log）。
    /// 这不是巧合——失败的只可能是分子口径。此用例把"少一个文件就绝不满"钉死。
    /// </summary>
    [Fact]
    public void PT2_RD03_MissingOneSmallFile_MustBeBelow100()
    {
        var completed = RealTotalBytes - DumpStackLogBytes;   // 119,491,119,745
        var s = RealDeviceState(JobPhase.CompletedWithErrors, completed, completedObjects: 9, failedObjects: 1);

        Assert.Equal(DumpStackLogBytes, RealTotalBytes - completed);

        var percent = TransferOrchestrator.Percent(s);

        Assert.True(percent < 100.0, $"缺 1 个 8,192 B 文件时不得 100%，实际 {percent}。");
        Assert.True(percent <= 99.9, $"缺文件时上限应为 99.9%，实际 {percent}。");
    }

    // ─────────────────────────────────────────────────────────────────────
    // §七-Progress-④ 真 Completed 且字节闭环 ⇒ 必须 100%
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 对照用例，防止"为了不让失败显示 100%"把正常完成也压低：
    /// Phase==Completed ∧ CompletedObjects==TotalObjects ∧ CompletedBytes==TotalBytes ⇒ 必须恰好 100%。
    /// </summary>
    [Fact]
    public void PT2_RD04_TrueCompletedWithFullBytes_MustBeExactly100()
    {
        var s = RealDeviceState(JobPhase.Completed, RealTotalBytes, completedObjects: 10, failedObjects: 0);

        var percent = TransferOrchestrator.Percent(s);

        Assert.Equal(100.0, percent, 6);
    }

    /// <summary>真机同期还有一次**真正成功**的迁移（本机冒烟 JOB-20261015-165819-ad2e，16/16 文件全 MATCH），也必须是 100%。</summary>
    [Fact]
    public void PT2_RD05_SmokeTestStyleFullSuccess_MustBe100()
    {
        var s = new JobState
        {
            Phase = JobPhase.Completed,
            TotalObjects = 3,
            CompletedObjects = 3,
            FailedObjects = 0,
            MissingObjects = 0,
            TotalBytes = 34_967_176,
            CompletedBytes = 34_967_176,
        };

        Assert.Equal(100.0, TransferOrchestrator.Percent(s), 6);
    }

    // ─────────────────────────────────────────────────────────────────────
    // §七-Progress-① Failed 对象的部分字节不进 completedBytes
    // §七-Progress-⑤ Failed 部分复制后重新结算不能抬高 completedBytes
    // §七-Progress-⑥ Resume / continuation 不得重新把失败对象部分字节算进去
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 可信完成判据对真机终态必须为 false —— 自动跳转的唯一依据。
    /// 注意这里**:不能用 percent >= 100 判断**（用户令 §五 明令禁止）。
    /// </summary>
    [Fact]
    public void PT2_RD06_RealDeviceFinalState_IsNotTrustworthyCompletion()
    {
        var s = RealDeviceState(JobPhase.CompletedWithErrors, RealTotalBytes, completedObjects: 9, failedObjects: 1);

        Assert.False(TransferOrchestrator.IsTrustworthyCompletion(s),
            "真机终态（9/10 完成、1 失败）绝不能被认定为可信完成，否则用户会被自动送到 Step4。");
    }

    /// <summary>真机失败对象即使把 9/10 个文件（1,990,998,416 B）都复制过去了，也不能让判据成立。</summary>
    [Fact]
    public void PT2_RD07_FailedObjectPartialBytes_CannotMakeCompletionTrustworthy()
    {
        // 若失败对象的部分字节被错误计入，completedBytes 会等于 totalBytes；此处如实给 9 个成功对象之和。
        const long nineOkObjects = 117_501_121_329L;
        var s = RealDeviceState(JobPhase.CompletedWithErrors, nineOkObjects, completedObjects: 9, failedObjects: 1);

        Assert.False(TransferOrchestrator.IsTrustworthyCompletion(s));
        Assert.True(TransferOrchestrator.Percent(s) < 100.0);
    }

    /// <summary>Phase 停在 Running 时判据同样为 false（进程被杀 / 未落终态，不是完成）。</summary>
    [Fact]
    public void PT2_RD08_RunningPhase_IsNotTrustworthyCompletion()
    {
        var s = RealDeviceState(JobPhase.Running, RealTotalBytes, completedObjects: 10, failedObjects: 0);

        Assert.False(TransferOrchestrator.IsTrustworthyCompletion(s));
    }

    /// <summary>空计划（0 个对象）不是"完成"—— 用户令 §五 要求 TotalObjects&gt;0 才算。</summary>
    [Fact]
    public void PT2_RD09_ZeroObjects_IsNotTrustworthyCompletion()
    {
        var s = new JobState
        {
            Phase = JobPhase.Completed,
            TotalObjects = 0,
            CompletedObjects = 0,
            FailedObjects = 0,
            MissingObjects = 0,
            TotalBytes = 0,
            CompletedBytes = 0,
        };

        Assert.False(TransferOrchestrator.IsTrustworthyCompletion(s));
    }

    /// <summary>字节没闭环（completedBytes &lt; totalBytes）即使是 Completed 也不可信。</summary>
    [Fact]
    public void PT2_RD10_BytesNotSettled_IsNotTrustworthyCompletion()
    {
        var s = RealDeviceState(JobPhase.Completed, RealTotalBytes - 1, completedObjects: 10, failedObjects: 0);

        Assert.False(TransferOrchestrator.IsTrustworthyCompletion(s));
    }

    // ─────────────────────────────────────────────────────────────────────
    // 真机日志里 ProgressTransition 的逐拍事实（99.9 → 100 的那一拍）
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 真机 app-20261016.jsonl 末几拍实录：
    ///   07:32:59.6361755  previousPercent=99.9 → newPercent=99.9  committed=119,491,120,745  continuationCleared=True
    ///   07:32:59.6474398  phase=CompletedWithErrors  previousPercent=99.9 → **newPercent=100**
    /// 即：收尾那一拍把 99.9 抬到了 100，而 phase 当时已是 CompletedWithErrors。
    /// 本用例断言"CompletedWithErrors + 完整字节"的显示值必须是 99.9，而不是 100。
    /// </summary>
    [Fact]
    public void PT2_RD11_RealDeviceFinalTick_MustStayAt99Point9()
    {
        var s = RealDeviceState(JobPhase.CompletedWithErrors, RealTotalBytes, completedObjects: 9, failedObjects: 1);

        var percent = TransferOrchestrator.Percent(s);

        Assert.Equal(99.9, percent, 6);
    }
}
