using PCMig.Core.Models;
using PCMig.Core.Transfer;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// 对象级回执权威解析（Round-3 PHASE E 收尾）。
///
/// 存在的理由：同一对象在一次任务里可能被跑多趟（暂停后 Resume / 停止后恢复 / 定向修复重拷），
/// 而 <c>JobManager.SaveReceipt</c> 每次尝试写一份独立回执，文件名只精确到秒
/// （<c>{ObjectId}-{yyyyMMddHHmmss}.json</c>）⇒ 一个对象**可以留下多份回执**。
/// 若读取侧"取第一份命中的"，结果就随目录枚举顺序变化：两份 Interrupted 时可能取到更小的那份，
/// 让"已确认字节"倒退 —— 这正是 UI Closure 要根治的那一类真值污染。
///
/// 因此权威解析必须是**纯函数、确定、单调**：
///   · 最新一次尝试（先 CompletedUtc、同刻比 Attempt）决定状态；
///   · 最新为 Interrupted 时，可信字节取该对象所有 Interrupted 回执的**最大值**，再按计划字节夹取；
///   · 最新不是 Interrupted（Completed / Failed）或无回执 ⇒ 0（本趟从零起算，显示不倒退由呈现层高水位负责）。
///
/// 说明：断言里不出现任何真实时钟或文件系统 —— 秒边界不再影响结论（旧用例正是被秒边界绊倒的）。
/// </summary>
public sealed class ReceiptAuthorityResolutionTests
{
    private const string ObjectId = "object-1";
    private static readonly DateTime T1 = new(2026, 10, 5, 8, 13, 42, DateTimeKind.Utc);
    private static readonly DateTime T2 = T1.AddMilliseconds(43);
    private const long Plan = 42L * 1024 * 1024 * 1024;   // 42 GiB

    private static ObjectReceipt Receipt(
        ObjectStatus status, long targetBytes, DateTime completedUtc, int attempt)
        => new()
        {
            ObjectId = ObjectId,
            Status = status,
            TargetBytes = targetBytes,
            CompletedUtc = completedUtc,
            Attempt = attempt,
        };

    [Fact]
    public void RepeatedInterrupts_TakeTheHighestCheckpoint_RegardlessOfEnumerationOrder()
    {
        var older = Receipt(ObjectStatus.Interrupted, 20L * 1024 * 1024 * 1024, T1, attempt: 1);
        var newer = Receipt(ObjectStatus.Interrupted, 30L * 1024 * 1024 * 1024, T2, attempt: 2);

        var forward = TransferOrchestrator.ResolveTrustedInterruptedBytes(new[] { older, newer }, ObjectId, Plan);
        var reversed = TransferOrchestrator.ResolveTrustedInterruptedBytes(new[] { newer, older }, ObjectId, Plan);

        // 已确认字节只许变大：不能因为枚举到更早/更小的那份就倒退
        Assert.Equal(30L * 1024 * 1024 * 1024, forward);
        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void OlderInterruptedBytesSurvive_ANewerSmallerCheckpoint()
    {
        // 第二趟的可信 checkpoint 反而更小（例如重跑途中目标被清理）：仍取最大值，绝不改小已确认字节
        var first = Receipt(ObjectStatus.Interrupted, 30L * 1024 * 1024 * 1024, T1, attempt: 1);
        var second = Receipt(ObjectStatus.Interrupted, 5L * 1024 * 1024 * 1024, T2, attempt: 2);

        Assert.Equal(30L * 1024 * 1024 * 1024,
            TransferOrchestrator.ResolveTrustedInterruptedBytes(new[] { first, second }, ObjectId, Plan));
        Assert.Equal(30L * 1024 * 1024 * 1024,
            TransferOrchestrator.ResolveTrustedInterruptedBytes(new[] { second, first }, ObjectId, Plan));
    }

    [Fact]
    public void NewestAttemptCompleted_MakesTheResumeBaselineZero()
    {
        var interrupted = Receipt(ObjectStatus.Interrupted, 30L * 1024 * 1024 * 1024, T1, attempt: 1);
        var completed = Receipt(ObjectStatus.Completed, Plan, T2, attempt: 2);

        // 最新一趟已完成 ⇒ 该对象在续传时直接跳过，基线必须是 0（不能把完成的字节也算成本趟已传）
        Assert.Equal(0, TransferOrchestrator.ResolveTrustedInterruptedBytes(new[] { interrupted, completed }, ObjectId, Plan));
        Assert.Equal(0, TransferOrchestrator.ResolveTrustedInterruptedBytes(new[] { completed, interrupted }, ObjectId, Plan));
    }

    [Fact]
    public void NewestAttemptFailed_AlsoResolvesToZero()
    {
        var interrupted = Receipt(ObjectStatus.Interrupted, 30L * 1024 * 1024 * 1024, T1, attempt: 1);
        var failed = Receipt(ObjectStatus.Failed, 0, T2, attempt: 2);

        Assert.Equal(0, TransferOrchestrator.ResolveTrustedInterruptedBytes(new[] { interrupted, failed }, ObjectId, Plan));
    }

    [Fact]
    public void TrustedBytesAreClampedToThePlannedSize()
    {
        // /Z 预分配会让目标文件长度等于最终大小：任何来源的可信字节都不得超过计划
        var receipt = Receipt(ObjectStatus.Interrupted, Plan + 1024, T1, attempt: 1);

        Assert.Equal(Plan, TransferOrchestrator.ResolveTrustedInterruptedBytes(new[] { receipt }, ObjectId, Plan));
    }

    [Fact]
    public void SameInstantReceipts_AreOrderedByAttempt()
    {
        // 同一秒内两趟都留下回执（这正是旧断言 flaky 的场景）：按 Attempt 判最新，最大值仍生效
        var attempt1 = Receipt(ObjectStatus.Interrupted, 12L * 1024 * 1024 * 1024, T1, attempt: 1);
        var attempt2 = Receipt(ObjectStatus.Interrupted, 13L * 1024 * 1024 * 1024, T1, attempt: 2);

        Assert.Equal(13L * 1024 * 1024 * 1024,
            TransferOrchestrator.ResolveTrustedInterruptedBytes(new[] { attempt1, attempt2 }, ObjectId, Plan));
        Assert.Equal(13L * 1024 * 1024 * 1024,
            TransferOrchestrator.ResolveTrustedInterruptedBytes(new[] { attempt2, attempt1 }, ObjectId, Plan));
    }

    [Fact]
    public void NoReceiptForTheObject_ResolvesToZero()
    {
        var other = Receipt(ObjectStatus.Interrupted, 10L * 1024 * 1024 * 1024, T1, attempt: 1);
        other.ObjectId = "object-2";

        Assert.Equal(0, TransferOrchestrator.ResolveTrustedInterruptedBytes(new[] { other }, ObjectId, Plan));
        Assert.Equal(0, TransferOrchestrator.ResolveTrustedInterruptedBytes(Array.Empty<ObjectReceipt>(), ObjectId, Plan));
    }

    [Fact]
    public void ZeroOrMissingTrustedBytes_ResolvesToZero_NotTheTargetLength()
    {
        // 缺失可信 checkpoint 时返回 0 是本轮口径：绝不回退到目标实测长度（/Z 预分配会等于最终大小）
        var receipt = Receipt(ObjectStatus.Interrupted, 0, T1, attempt: 1);

        Assert.Equal(0, TransferOrchestrator.ResolveTrustedInterruptedBytes(new[] { receipt }, ObjectId, Plan));
    }
}