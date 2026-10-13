using System;
using System.IO;
using System.Text.RegularExpressions;
using PCMig.Diagnostics;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// 关闭排空预算的**分配策略**契约（负载敏感回归）。
///
/// 背景：重负载下 <c>D2RuntimeTests.RuntimeWritesEventsToSegmentsAndReportsHealth</c> 偶发
/// <c>正常关闭应当是 clean：drain-budget-exhausted</c> —— 旧实现给每个分支固定切
/// <c>totalBudget / 6</c>（默认 800 ms ⇒ 每支 133 ms，7 支合计 931 ms 本身已超总预算），
/// 线程池调度抖动让"还没轮到"被记成"放弃了条目"，于是**假的不干净关闭**。
///
/// 这里锁三件事，且**不削弱任何断言强度**：
///   ① 分配按「剩余预算 ÷ 剩余分支数」——先排空的分支把余量让给慢分支；
///   ② 任何一支的预算**绝不**超过剩余预算（总时长仍被封顶，预算见底返回零 = 如实放弃）；
///   ③ 这条策略确实接在 `ShutdownAsync` 的排空循环上（不是写了没用的死代码）。
/// </summary>
public sealed class D2ShutdownDrainBudgetTests
{
    private static readonly TimeSpan Total = TimeSpan.FromMilliseconds(800);

    // ───────── ① 份额分配：不是固定切片，先完成的分支把余量让出去 ─────────

    [Fact]
    public void FirstBranchGetsItsShareOfTheWholeBudget()
    {
        var first = DiagnosticRuntime.NextBranchDrainBudget(Total, TimeSpan.Zero, branchesLeft: 7);
        Assert.Equal(800 / 7.0, first.TotalMilliseconds, 1);   // ≈114.3 ms，**不是**旧的固定 133 ms
    }

    [Fact]
    public void BranchesThatFinishedEarlyHandTheirTimeToTheLastOne()
    {
        // 前 6 支只花了 50 ms 就排空 ⇒ 最后 1 支应当拿到几乎全部剩余预算（旧实现只能拿到固定 133 ms）。
        var last = DiagnosticRuntime.NextBranchDrainBudget(Total, TimeSpan.FromMilliseconds(50), branchesLeft: 1);
        Assert.Equal(750.0, last.TotalMilliseconds, 1);
        Assert.True(last > TimeSpan.FromMilliseconds(133),
            $"最后的分支必须能借用余量，实际 {last.TotalMilliseconds:0.#} ms");
    }

    // ───────── ② 绝不超预算：总时长仍被封顶，见底即如实放弃 ─────────

    [Fact]
    public void FloorNeverPushesABranchOverTheRemainingBudget()
    {
        var left = TimeSpan.FromMilliseconds(120) - TimeSpan.FromMilliseconds(100);
        var budget = DiagnosticRuntime.NextBranchDrainBudget(
            TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(100), branchesLeft: 5);
        Assert.True(budget <= left, $"分支预算 {budget.TotalMilliseconds:0.#} ms 超过剩余 {left.TotalMilliseconds:0.#} ms");
        Assert.Equal(20.0, budget.TotalMilliseconds, 1);
    }

    [Fact]
    public void ExhaustedBudgetIsZeroNotNegativeAndNotAFreePass()
    {
        Assert.Equal(TimeSpan.Zero, DiagnosticRuntime.NextBranchDrainBudget(
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(150), branchesLeft: 3));
        Assert.Equal(TimeSpan.Zero, DiagnosticRuntime.NextBranchDrainBudget(
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), branchesLeft: 1));
    }

    [Fact]
    public void ShareHasAFloorSoATinyBudgetStillGivesEachBranchAChance()
    {
        // 800/20 = 40 ms < 50 ms 下限 ⇒ 兜到 50 ms，但仍不得超过剩余预算。
        var budget = DiagnosticRuntime.NextBranchDrainBudget(Total, TimeSpan.Zero, branchesLeft: 20);
        Assert.Equal(50.0, budget.TotalMilliseconds, 1);
    }

    [Fact]
    public void SevenSequentialBranchesCanNeverExceedTheTotalBudget()
    {
        var elapsed = TimeSpan.Zero;
        for (var i = 7; i >= 1; i--)
        {
            var budget = DiagnosticRuntime.NextBranchDrainBudget(Total, elapsed, i);
            Assert.True(budget <= Total - elapsed,
                $"第 {8 - i} 支拿到 {budget.TotalMilliseconds:0.#} ms，超出剩余 {Total.TotalMilliseconds - elapsed.TotalMilliseconds:0.#} ms");
            elapsed += budget;
        }
        Assert.True(elapsed <= Total, $"合计 {elapsed.TotalMilliseconds:0.#} ms 超过总预算 {Total.TotalMilliseconds:0.#} ms");
    }

    // ───────── ③ 接线契约：策略确实用在排空循环上（源码级，防"写了没用"）─────────

    [Fact]
    public void ShutdownDrainLoopActuallyUsesTheSharedBudgetPolicy()
    {
        var body = MethodBody("src/PCMig.Diagnostics/DiagnosticRuntime.cs", "ShutdownAsync");
        Assert.Contains("NextBranchDrainBudget(totalBudget, drainClock.Elapsed, branchesLeft)", body, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(body, @"StopBranchAsync\([^)]*NextDrainBudget\(\)").Count);  // 入口 / 收件箱 / writer
        Assert.DoesNotContain("perBranch", body, StringComparison.Ordinal);                        // 旧的固定切片已彻底退出
        // clean 判据与放弃记账语义**一字未改**（不许借这次修复削弱它）。
        Assert.Contains("var clean = abandoned == 0", body, StringComparison.Ordinal);
        Assert.Contains("drain-budget-exhausted", body, StringComparison.Ordinal);
    }

    private static string MethodBody(string relative, string methodName)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), relative));
        // ★ 必须跳过**调用点**：名字到 `{` 之间若出现 `;` 就是调用点（结尾 `);`），继续找下一个。
        foreach (Match sig in Regex.Matches(text, @"\b" + methodName + @"\s*\("))
        {
            var open = text.IndexOf('{', sig.Index + sig.Length);
            if (open < 0) continue;
            if (text.IndexOf(';', sig.Index, open - sig.Index) >= 0) continue;
            var depth = 0;
            for (var i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}')
                {
                    depth--;
                    if (depth == 0) return text[open..(i + 1)];
                }
            }
        }
        Assert.Fail($"未能在 {relative} 中定位方法体：{methodName}");
        return string.Empty;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PCMig.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}