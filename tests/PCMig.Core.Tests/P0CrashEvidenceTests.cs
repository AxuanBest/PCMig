using System.Text.Json;
using PCMig.Core.Diagnostics;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ P0-06(4) ★ 崩溃 / 卡死证据的独立落盘通道（<see cref="CrashEvidence"/>）。
///
/// 为什么单独立一套测试：这条通道是"进程被强杀后还能不能证明发生过什么"的最后一道保险。
/// 它必须满足三件事，缺一不可：
///   1) **真的落盘**（写完即 fsync，不依赖日志队列的后台线程）；
///   2) **永不外溢**（磁盘满 / 无权写 / 路径非法都不能把它变成新的崩溃源）；
///   3) **只是证据**（不参与任何业务判定，不许改任务状态或结论）。
/// </summary>
[Collection("crash-evidence")]
public class P0CrashEvidenceTests : IDisposable
{
    private readonly string? _prevRoot;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmiG-evidence-tests", Guid.NewGuid().ToString("N"));

    public P0CrashEvidenceTests()
    {
        _prevRoot = CrashEvidence.RootDirectory;
        CrashEvidence.RootDirectory = _root;
    }

    public void Dispose()
    {
        if (_prevRoot is not null) CrashEvidence.RootDirectory = _prevRoot;
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* 清理失败不影响断言 */ }
    }

    [Fact]
    public void E1_写入即落盘且字段可解析()
    {
        Assert.True(CrashEvidence.Write("ui-stall", "距最后一次心跳已 1800 ms 没有滴答。", "UiStallWatchdog"));

        var lines = CrashEvidence.ReadRecent(10);
        Assert.Single(lines);

        using var doc = JsonDocument.Parse(lines[0]);
        var root = doc.RootElement;
        Assert.Equal("ui-stall", root.GetProperty("kind").GetString());
        Assert.Equal("UiStallWatchdog", root.GetProperty("where").GetString());
        Assert.Contains("1800 ms", root.GetProperty("detail").GetString()!, StringComparison.Ordinal);
        Assert.Equal(Environment.ProcessId, root.GetProperty("pid").GetInt32());
        Assert.True(DateTime.TryParse(root.GetProperty("t").GetString(), out _), "时间戳必须可解析");
        // 写入后立刻 ReadRecent 就能看到 ⇒ 没有走任何延迟队列
        Assert.True(File.Exists(CrashEvidence.FilePath));
    }

    [Fact]
    public void E2_写不进去也不许抛异常()
    {
        Assert.False(CrashEvidence.Write("ui-stall", "x", dir: "Z:\\不存在的盘\\证据"));
        // 非法路径（含 NUL）同样只能返回 false
        Assert.False(CrashEvidence.Write("ui-stall", "x", dir: "C:\\a\0b"));
        Assert.Empty(CrashEvidence.ReadRecent(10, dir: "Z:\\不存在的盘\\证据"));
    }

    [Fact]
    public void E3_多线程并发写入不得交错或缺行()
    {
        const int threads = 8, perThread = 50;
        Parallel.For(0, threads, t =>
        {
            for (var i = 0; i < perThread; i++)
                CrashEvidence.Write("ui-exception", $"线程 {t} 第 {i} 条", "test");
        });

        var lines = File.ReadAllLines(CrashEvidence.FilePath);
        Assert.Equal(threads * perThread, lines.Length);
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line); // 交错 / 半行 ⇒ 这里会抛
            Assert.Equal("ui-exception", doc.RootElement.GetProperty("kind").GetString());
        }
    }

    [Fact]
    public void E4_超过上限必须轮转而不是丢证据()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(CrashEvidence.FilePath, new byte[CrashEvidence.MaxBytes + 16]); // 造一个"已经很大"的证据文件

        Assert.True(CrashEvidence.Write("ui-stall", "轮转后的新证据", "test"));

        Assert.True(File.Exists(CrashEvidence.FilePath + ".1"), "旧证据必须被轮转为 .1（不得直接删掉）");
        var lines = File.ReadAllLines(CrashEvidence.FilePath);
        Assert.Single(lines); // 新文件只含新证据
        Assert.Contains("轮转后的新证据", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void E5_超长细节必须截断并留标记()
    {
        var huge = new string('x', CrashEvidence.MaxDetailChars + 5000);
        Assert.True(CrashEvidence.Write("ui-exception", huge, "test"));

        using var doc = JsonDocument.Parse(CrashEvidence.ReadRecent(1)[0]);
        var detail = doc.RootElement.GetProperty("detail").GetString()!;
        Assert.True(detail.Length <= CrashEvidence.MaxDetailChars + 16, $"过长的堆栈把证据文件撑爆了：{detail.Length}");
        Assert.EndsWith("…（已截断）", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void E6_卡死监视器必须把卡死与恢复都写进证据文件()
    {
        using var wd = new UiStallWatchdog(stallThresholdMs: 100, sampleIntervalMs: 50);
        wd.Start();
        wd.Heartbeat("tick");
        Thread.Sleep(500);                 // 让采样线程判定"界面未响应"
        Assert.True(WaitFor(() => wd.Stalls.Count >= 1), "监视器必须先自己判定出卡住");
        wd.Heartbeat("tick");              // 恢复
        wd.Stop();

        var lines = CrashEvidence.ReadRecent(20);
        Assert.Contains(lines, l => l.Contains("\"kind\":\"ui-stall\"", StringComparison.Ordinal));
        var recovered = Assert.Single(lines, l => l.Contains("\"kind\":\"ui-stall-recovered\"", StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(recovered);
        Assert.Contains("总时长", doc.RootElement.GetProperty("detail").GetString()!, StringComparison.Ordinal);
        Assert.Equal("UiStallWatchdog", doc.RootElement.GetProperty("where").GetString());
    }

    [Fact]
    public void E7_证据通道必须只写证据_不得参与业务判定()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), @"src\PCMig.Core\Diagnostics\CrashEvidence.cs"));

        Assert.Contains("fs.Flush(flushToDisk: true)", src, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "IsRunning", "Cancel", "IsStopping", "JobState", "OverallPass", "Status =" })
            Assert.DoesNotContain(forbidden, src, StringComparison.Ordinal);
    }

    [Fact]
    public void E8_三条崩溃出口都必须接上同步落盘()
    {
        var guard = File.ReadAllText(Path.Combine(RepoRoot(), @"src\PCMig.WinUI\Diagnostics\UiFaultGuard.cs"));
        Assert.Contains("CrashEvidence.Write(\"ui-exception\", ex.ToString(), where);", guard, StringComparison.Ordinal);

        var app = File.ReadAllText(Path.Combine(RepoRoot(), @"src\PCMig.WinUI\App.xaml.cs"));
        Assert.Contains("CrashEvidence.Write(\"task-unobserved\"", app, StringComparison.Ordinal);
        Assert.Contains("CrashEvidence.Write(\n                \"appdomain-unhandled\"", app, StringComparison.Ordinal);
        // 两条出口都必须在"记录事实"的同时同步落盘（顺序：日志 → 证据 → 诊断事实）
        Assert.True(app.IndexOf("CrashEvidence.Write(\"task-unobserved\"", StringComparison.Ordinal)
                    < app.IndexOf("RecordUnhandledException(e.Exception, \"UnobservedTaskException\")", StringComparison.Ordinal));

        var wd = File.ReadAllText(Path.Combine(RepoRoot(), @"src\PCMig.Core\Diagnostics\UiStallWatchdog.cs"));
        Assert.Contains("CrashEvidence.Write(kind, detail, where: \"UiStallWatchdog\");", wd, StringComparison.Ordinal);
        Assert.Contains("var kind = fact.Recovered ? \"ui-stall-recovered\" : \"ui-stall\";", wd, StringComparison.Ordinal);
        // 先落盘再回调：回调没接或自身崩掉都不影响证据在磁盘上
        Assert.True(wd.IndexOf("PersistEvidence(fact);", StringComparison.Ordinal)
                    < wd.IndexOf("if (_onStall is null) return;", StringComparison.Ordinal));
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }
        return condition();
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