using PCMig.Core.Jobs;
using PCMig.Core.Models;
using PCMig.Core.State;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// 回执文件名的可信度（v0.5.3 Stable 发版前收口，对应 P1/A5 报告里"趟表少一行、平均速度偏高"那条）。
///
/// 旧实现：文件名只精确到秒（<c>{ObjectId}-{yyyyMMddHHmmss}.json</c>）且 <c>JsonStateStore.WriteAtomic</c>
/// 内部走 <c>File.Move(tmp, path, overwrite: true)</c> ⇒ 同一对象在同一秒内写第二份回执时**静默覆盖**第一份。
/// 那不是"显示问题"：被覆盖掉的那一趟从趟表里消失，平均速度分母变大、Attempt 号可能重复。
///
/// 本用例锁的是**行为**：同秒两趟都必须留下文件、都必须能被读出来。
/// 读取端只枚举 <c>*.json</c>、从不解析文件名，所以加 <c>-1</c> 后缀不破坏任何历史作业（含 v0.5.0/v0.5.1 与预览版）。
/// </summary>
public sealed class ReceiptFileNameTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pcmig-receipt-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 独占句柄可能延迟释放 */ }
    }

    private JobContext NewContext()
    {
        var ctx = new JobContext
        {
            JobDir = _dir,
            Definition = new JobDefinition
            {
                JobId = "JOB-R",
                SourceHost = "SRC-PC",
                TargetRoot = @"E:\",
                CreatedBy = "User"
            }
        };
        Directory.CreateDirectory(ctx.ReceiptsDir);
        return ctx;
    }

    private static ObjectReceipt Receipt(string objectId, int attempt, long targetBytes) => new()
    {
        ObjectId = objectId,
        Kind = ObjectKind.DataVolume,
        SourcePath = @"\\SRC-PC\E\a",
        TargetPath = @"E:\a",
        StartedUtc = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc),
        CompletedUtc = new DateTime(2026, 10, 9, 0, 1, 0, DateTimeKind.Utc),
        Status = ObjectStatus.Failed,
        TargetBytes = targetBytes,
        TargetFiles = 1,
        RobocopyExitCodeBulk = 9,
        Attempt = attempt
    };

    [Fact]
    public void SameObjectTwiceInTheSameSecond_KeepsBothReceipts()
    {
        var ctx = NewContext();

        // 两次调用之间不 sleep：就是要撞在同一个秒里（真机上的同秒重试/恢复就是这个形状）
        ctx.SaveReceipt(Receipt("object-000001", attempt: 1, targetBytes: 100));
        ctx.SaveReceipt(Receipt("object-000001", attempt: 2, targetBytes: 200));

        var names = Directory.GetFiles(ctx.ReceiptsDir, "*.json").Select(Path.GetFileName).ToList();
        Assert.Equal(2, names.Count);
        Assert.All(names, n => Assert.StartsWith("object-000001-", n));
        Assert.Single(names, n => n!.EndsWith("-1.json", StringComparison.Ordinal));

        // 读回来也必须还是两趟（被覆盖的话这里只剩 Attempt 2）
        var loaded = ctx.LoadReceipts();
        Assert.Equal(2, loaded.Count);
        Assert.Equal(new[] { 1, 2 }, loaded.Select(r => r.Attempt).OrderBy(a => a).ToArray());
        Assert.Contains(loaded, r => r.TargetBytes == 100);
        Assert.Contains(loaded, r => r.TargetBytes == 200);
    }

    [Fact]
    public void ThirdWriteInTheSameSecond_IsAlsoKept()
    {
        var ctx = NewContext();

        ctx.SaveReceipt(Receipt("object-000002", 1, 10));
        ctx.SaveReceipt(Receipt("object-000002", 2, 20));
        ctx.SaveReceipt(Receipt("object-000002", 3, 30));

        Assert.Equal(3, Directory.GetFiles(ctx.ReceiptsDir, "*.json").Length);
        Assert.Equal(new[] { 1, 2, 3 }, ctx.LoadReceipts().Select(r => r.Attempt).OrderBy(a => a).ToArray());
    }

    [Fact]
    public void SuffixDoesNotLeakIntoTheObjectIdentity()
    {
        var ctx = NewContext();

        ctx.SaveReceipt(Receipt("object-000003", 1, 10));
        ctx.SaveReceipt(Receipt("object-000003", 2, 20));

        // ObjectId 必须原样来自回执内容，绝不能因为文件名带了 -1 就变成 object-000003-1
        Assert.All(ctx.LoadReceipts(), r => Assert.Equal("object-000003", r.ObjectId));
    }

    [Fact]
    public void HistoricalSecondPrecisionReceiptNames_StayReadable()
    {
        var ctx = NewContext();

        // v0.5.1 及更早、以及两个预览版写下的名字（无后缀）必须原样可读
        JsonStateStore.WriteAtomic(
            Path.Combine(ctx.ReceiptsDir, "object-000001-20261008060509.json"),
            Receipt("object-000001", 1, 42));
        JsonStateStore.WriteAtomic(
            Path.Combine(ctx.ReceiptsDir, "object-000001-20261008060509-1.json"),
            Receipt("object-000001", 2, 43));

        var loaded = ctx.LoadReceipts();
        Assert.Equal(2, loaded.Count);
        Assert.Equal(new long[] { 42, 43 }, loaded.Select(r => r.TargetBytes).OrderBy(b => b).ToArray());
    }
}