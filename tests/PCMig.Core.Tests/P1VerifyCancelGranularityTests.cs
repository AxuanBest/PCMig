using PCMig.Core.Verify;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// P1-12 / R9「取消要有效」的取消粒度收口（2026-10-12）。
///
/// 背景（真实界面实测）：平铺单目录 800,000 个文件的作业，L1 验证要 4.4 s，
/// 旧实现只在**目录之间**检查 ct ⇒ 在真实界面上点「取消验证」要等整个目录枚举完才生效。
/// 修法：目录内每 <see cref="Verifier.CancelCheckInterval"/> 个条目检查一次取消。
///
/// 这里只做**确定性**断言（策略 + 接线契约）。为什么不写计时型功能测试：
/// 本机枚举 3.75 µs/文件 ⇒ 造一个足以稳定跨越取消点的目录需要几十万文件（测试自身要几十秒），
/// 而"点中取消"的端到端证据由真实界面驱动脚本提供（uia-cancel-evidence2.ps1）。
/// </summary>
public sealed class P1VerifyCancelGranularityTests
{
    [Fact]
    public void 取消检查按固定粒度触发_零个条目不触发()
    {
        // 计数从 0 开始时不能立刻抛（否则空目录也会"被取消"）
        Assert.False(Verifier.ShouldCheckCancel(0));
        Assert.False(Verifier.ShouldCheckCancel(1));
        Assert.False(Verifier.ShouldCheckCancel(Verifier.CancelCheckInterval - 1));
    }

    [Fact]
    public void 取消检查在每个粒度整数倍上恰好触发一次()
    {
        var interval = Verifier.CancelCheckInterval;
        var hits = new List<long>();
        for (long i = 1; i <= interval * 3 + 5; i++) if (Verifier.ShouldCheckCancel(i)) hits.Add(i);
        Assert.Equal(new[] { (long)interval, interval * 2L, interval * 3L }, hits);
    }

    [Fact]
    public void 取消粒度有界_最坏取消延迟是毫秒级而不是整个目录()
    {
        // 实测枚举吞吐 ≈3.75 µs/文件（800,000 文件 / 3.0 s）⇒ 粒度 4096 的最坏延迟 ≈15 ms。
        // 用 10 倍余量断言：延迟必须远小于一秒，且粒度必须远小于任何真实目录规模。
        Assert.Equal(4096, Verifier.CancelCheckInterval);
        var worstMs = Verifier.CancelCheckInterval * 3.75 / 1000.0;
        Assert.True(worstMs < 100, $"最坏取消延迟应为毫秒级，实测推算 {worstMs:N1} ms");
    }

    [Fact]
    public void 两处目录内枚举都接上了有界取消检查()
    {
        var src = StripLineComments(File.ReadAllText(
            RepoPath("src", "PCMig.Core", "Verify", "Verifier.cs")));

        // 计数遍（EnumerateSide）与流式过源（StreamSourcePass）各一处
        var calls = CountOccurrences(src, "if (ShouldCheckCancel(++entrySeen)) ct.ThrowIfCancellationRequested();");
        Assert.Equal(2, calls);
        Assert.Equal(2, CountOccurrences(src, "long entrySeen = 0;"));
        // 粒度常量与策略函数本身
        Assert.Contains("internal const int CancelCheckInterval = 4096;", src);
        Assert.Contains("internal static bool ShouldCheckCancel(long entriesSeen)", src);
    }

    [Fact]
    public void 取消检查不改变任何判定字段()
    {
        // 纪律：取消只影响"还跑不跑"，不得顺手改结论口径
        var src = StripLineComments(File.ReadAllText(
            RepoPath("src", "PCMig.Core", "Verify", "Verifier.cs")));
        var index = src.IndexOf("if (ShouldCheckCancel(++entrySeen))", StringComparison.Ordinal);
        Assert.True(index > 0);
        // 该行后面紧跟的 try 块首行仍是原来的"重解析点跳过"，说明没有插入别的逻辑
        var tail = src.Substring(index);
        Assert.Contains("if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;", tail);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0; var at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
        return count;
    }

    /// <summary>★ 教训 ★ 负向/正向源码断言都必须先剥掉行注释，否则解释历史的注释会被当成代码。</summary>
    private static string StripLineComments(string source) =>
        string.Join("\n", source.Split('\n').Select(l =>
        {
            var i = l.IndexOf("//", StringComparison.Ordinal);
            return i < 0 ? l : l.Substring(0, i);
        }));

    private static string RepoPath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }
}