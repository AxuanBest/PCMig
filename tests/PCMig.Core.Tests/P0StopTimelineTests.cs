using System.Diagnostics;
using PCMig.Core.Transfer;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ P0-06(1) ★ 停止时间线：先把"点击停止→可见反馈→Flush→取消投递→引擎收尾→窗口可响应"
/// 量出来，再谈改哪里。这里只测事实记录是否正确、是否非阻塞、是否幂等；
/// 停止"成功与否"的结论仍由引擎真值决定（另见 P0StopChainTests）。
/// </summary>
public class P0StopTimelineTests
{
    [Fact]
    public void T1_构造即开始计时_并记下click阶段()
    {
        var tl = new StopTimeline("user-stop");

        Assert.Equal("user-stop", tl.Reason);
        Assert.True(tl.Has("click"));
        Assert.Equal(0, tl.MsAt("click"));
        Assert.False(tl.IsComplete);
        Assert.Null(tl.Outcome);
    }

    [Fact]
    public void T2_阶段按发生顺序记录_且毫秒单调不减()
    {
        var tl = new StopTimeline();
        tl.Mark("feedback-shown");
        Thread.Sleep(15);
        tl.Mark("flush-done", "3 条待刷日志");
        Thread.Sleep(15);
        tl.Mark("cancel-delivered");
        tl.Complete("stop-settled");

        var segs = tl.Segments;
        Assert.Equal(new[] { "click", "feedback-shown", "flush-done", "cancel-delivered", "complete" },
            segs.Select(s => s.Phase).ToArray());
        long prev = -1;
        foreach (var s in segs)
        {
            Assert.True(s.AtMs >= prev, $"阶段 {s.Phase} 的 {s.AtMs} ms 小于前一阶段的 {prev} ms");
            prev = s.AtMs;
        }
        Assert.Equal("3 条待刷日志", segs.Single(s => s.Phase == "flush-done").Detail);
    }

    [Fact]
    public void T3_SpanMs给出两阶段差值_缺失阶段返回负一()
    {
        var tl = new StopTimeline();
        Thread.Sleep(20);
        tl.Mark("cancel-delivered");

        Assert.True(tl.SpanMs("click", "cancel-delivered") >= 15);
        Assert.Equal(-1, tl.MsAt("engine-settled"));
        Assert.Equal(-1, tl.SpanMs("engine-settled", "cancel-delivered"));
        Assert.False(tl.Has("engine-settled"));
    }

    [Fact]
    public void T4_收口写入结果码_重复收口以最后一次为准_且不抛()
    {
        var tl = new StopTimeline();
        tl.Complete("stop-settled");
        Assert.True(tl.IsComplete);
        Assert.Equal("stop-settled", tl.Outcome);

        tl.Complete("stop-unsettled", "超时");
        Assert.Equal("stop-unsettled", tl.Outcome);
        Assert.True(tl.IsComplete);
    }

    [Fact]
    public void T5_摘要包含每个已发生阶段与总计_未收口时如实标注()
    {
        var tl = new StopTimeline("network-lost");
        tl.Mark("feedback-shown");
        tl.Mark("flush-done");
        tl.Mark("cancel-delivered");
        tl.Mark("engine-settled");

        var whileRunning = tl.ToSummary();
        Assert.Contains("停止时间线[network-lost]", whileRunning);
        Assert.Contains("点击→反馈", whileRunning);
        Assert.Contains("点击→Flush 完成", whileRunning);
        Assert.Contains("点击→取消投递", whileRunning);
        Assert.Contains("点击→引擎收尾", whileRunning);
        Assert.Contains("结果（未收口）", whileRunning);
        Assert.DoesNotContain("窗口可再操作", whileRunning);

        tl.Complete("stop-settled");
        var done = tl.ToSummary();
        Assert.Contains("结果 stop-settled", done);
        Assert.DoesNotContain("未收口", done);
    }

    [Fact]
    public void T6_空阶段名被拒绝_不产生半条假记录()
    {
        var tl = new StopTimeline();
        Assert.ThrowsAny<ArgumentException>(() => tl.Mark("  "));
        Assert.ThrowsAny<ArgumentException>(() => tl.Complete(""));
        Assert.Single(tl.Segments);
    }

    [Fact]
    public async Task T7_多线程并发打点不丢阶段_且总段数正确()
    {
        var tl = new StopTimeline();
        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            for (var j = 0; j < 50; j++) tl.Mark($"t{i}-{j}");
        })).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(1 + 400, tl.Segments.Count);
        Assert.Equal(401, tl.Segments.Select(s => s.Phase).Distinct().Count());
    }

    [Fact]
    public void T8_计时用单调时钟_不受系统时间回拨影响()
    {
        // 记录实现事实：只有 Stopwatch 单调时钟，没有 DateTime.UtcNow / Environment.TickCount。
        var src = ReadRepoFile(@"src\PCMig.Core\Transfer\StopTimeline.cs");
        Assert.Contains("Stopwatch.GetTimestamp()", src, StringComparison.Ordinal);
        Assert.DoesNotContain("DateTime", src, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.TickCount", src, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PCMig.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative));
    }
}