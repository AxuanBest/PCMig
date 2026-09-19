using PCMig.Core.Jobs;
using PCMig.Core.Models;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>阶段视图判定（T05/O1 回归）：只影响显示，不改存档。</summary>
public class PhaseViewTests
{
    [Theory]
    [InlineData(JobPhase.Running, true, false, false)]     // 锁有持有者 = 真在跑
    [InlineData(JobPhase.Running, false, false, true)]     // 无持有者 = 陈旧 running
    [InlineData(JobPhase.AwaitingReview, false, true, true)]  // 无持有者+有回执 = 跑过但视图陈旧
    [InlineData(JobPhase.AwaitingReview, false, false, false)] // 刚生成计划，正常
    [InlineData(JobPhase.Completed, false, true, false)]   // 完成不算陈旧
    [InlineData(JobPhase.Paused, false, true, false)]      // 暂停不算陈旧
    public void IsStale_MatrixOfPhaseHolderAndReceipts(JobPhase stored, bool holder, bool receipts, bool expected)
        => Assert.Equal(expected, PhaseView.IsStale(stored, holder, receipts));

    [Fact]
    public void Effective_StaleRunning_DisplaysAsInterrupted()
        => Assert.Equal(JobPhase.Interrupted, PhaseView.Effective(JobPhase.Running, false, true));

    [Fact]
    public void Describe_StaleRunning_MentionsLockEvidence()
    {
        var text = PhaseView.Describe(JobPhase.Running, false, true);
        Assert.Contains("job.lock", text);
        Assert.Contains("续传", text);
    }
}
