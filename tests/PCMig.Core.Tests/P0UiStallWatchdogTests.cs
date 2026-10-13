using PCMig.Core.Diagnostics;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ P0-06(1)/(4) ★ UI 卡死证据：现场只有一句"点击停止时 UI 卡住或无响应"，
/// ZIP 里没有 WER / Application Hang 1002 / 转储，因此必须补出**可读事实**：
/// 未响应了多久、最后一次可用交互发生在什么时候、是谁打的最后一次心跳。
///
/// 本文件只测监视器本身是否如实记录、是否不阻塞、是否不成为新的崩溃源；
/// 它**不做**任何业务判定（不停止、不取消、不改任务状态）。
/// </summary>
[Collection("crash-evidence")]
public class P0UiStallWatchdogTests : IDisposable
{
    // ★ 监视器现在会把卡死/恢复事实同步落盘（P0-06(4)）★ ⇒ 测试必须把证据目录指到临时目录，
    // 不能让测试去写真实机器的 %ProgramData%\PCMig\Logs。
    private readonly string? _prevEvidenceRoot;
    private readonly string _evidenceRoot = Path.Combine(Path.GetTempPath(), "pcmiG-watchdog-tests", Guid.NewGuid().ToString("N"));

    public P0UiStallWatchdogTests()
    {
        _prevEvidenceRoot = CrashEvidence.RootDirectory;
        CrashEvidence.RootDirectory = _evidenceRoot;
    }

    public void Dispose()
    {
        if (_prevEvidenceRoot is not null) CrashEvidence.RootDirectory = _prevEvidenceRoot;
        try { if (Directory.Exists(_evidenceRoot)) Directory.Delete(_evidenceRoot, true); } catch { /* 清理失败不影响断言 */ }
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(25);
        }
        return condition();
    }

    [Fact]
    public void W1_超过阈值没有心跳_判定未响应并留下最后心跳与上下文()
    {
        using var wd = new UiStallWatchdog(stallThresholdMs: 120, sampleIntervalMs: 50);
        wd.Start();
        wd.Heartbeat("stop-click");

        Assert.True(WaitFor(() => wd.Stalls.Any(f => !f.Recovered), 3000), "超时未判定未响应");

        var fact = wd.Stalls.First(f => !f.Recovered);
        Assert.True(fact.StallMs >= 120, $"判定时的沉默时长必须达到阈值，实际 {fact.StallMs}ms");
        Assert.Equal("stop-click", fact.LastHeartbeatContext);
        Assert.False(fact.Recovered);
        Assert.Null(fact.TotalStallMs);
        Assert.True(fact.DetectedUtc > DateTime.UtcNow.AddMinutes(-5), "检测时间必须是真实墙上时间");
    }

    [Fact]
    public void W2_心跳持续_不得误报任何一条未响应()
    {
        using var wd = new UiStallWatchdog(stallThresholdMs: 150, sampleIntervalMs: 50);
        wd.Start();
        for (var i = 0; i < 20; i++)
        {
            wd.Heartbeat("tick");
            Thread.Sleep(40);
        }
        Assert.Empty(wd.Stalls);
        Assert.Null(wd.Worst);
    }

    [Fact]
    public void W3_恢复后补一条总时长_且同一段卡住只记一次()
    {
        using var wd = new UiStallWatchdog(stallThresholdMs: 120, sampleIntervalMs: 50);
        wd.Start();
        wd.Heartbeat("tick");
        Assert.True(WaitFor(() => wd.Stalls.Any(f => !f.Recovered), 3000), "超时未判定未响应");
        Thread.Sleep(300);
        wd.Heartbeat("tick");

        Assert.True(WaitFor(() => wd.Stalls.Any(f => f.Recovered), 2000), "心跳恢复后必须补一条已恢复记录");
        Assert.Equal(2, wd.Stalls.Count);                 // 检测 + 恢复，同一段卡住不得刷屏
        var recovery = wd.Stalls.Last();
        Assert.True(recovery.Recovered);
        Assert.NotNull(recovery.TotalStallMs);
        Assert.True(recovery.TotalStallMs >= 300, $"总时长必须覆盖真实沉默期，实际 {recovery.TotalStallMs}ms");
    }

    [Fact]
    public void W4_两段独立卡住_各记一条检测事实()
    {
        using var wd = new UiStallWatchdog(stallThresholdMs: 120, sampleIntervalMs: 50);
        wd.Start();
        wd.Heartbeat("tick");
        Assert.True(WaitFor(() => wd.Stalls.Count(f => !f.Recovered) == 1, 3000), "第 1 段未判定");
        wd.Heartbeat("tick");
        Assert.True(WaitFor(() => wd.Stalls.Any(f => f.Recovered), 2000), "第 1 段未恢复");

        wd.Heartbeat("tick");
        Assert.True(WaitFor(() => wd.Stalls.Count(f => !f.Recovered) == 2, 3000), "第 2 段未判定");
        Assert.Equal(2, wd.Stalls.Count(f => !f.Recovered));
    }

    [Fact]
    public void W5_回调自身抛异常_监视器不得停摆也不得外溢()
    {
        var calls = 0;
        using var wd = new UiStallWatchdog(stallThresholdMs: 120, sampleIntervalMs: 50, onStall: _ =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("回调故意炸");
        });
        wd.Start();
        wd.Heartbeat("tick");
        Assert.True(WaitFor(() => wd.CallbackFailures >= 1, 3000), "回调异常必须被计数");
        wd.Heartbeat("tick");
        Assert.True(WaitFor(() => calls >= 2, 2000), "回调失败后监视器必须仍在采样（恢复记录照发）");
        Assert.True(wd.IsRunning);
        Assert.True(wd.CallbackFailures >= 2);
    }

    [Fact]
    public void W6_多线程并发打心跳_不抛且不产生假卡住()
    {
        using var wd = new UiStallWatchdog(stallThresholdMs: 400, sampleIntervalMs: 50);
        wd.Start();
        var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
        {
            for (var i = 0; i < 300; i++) wd.Heartbeat("tick");
        })).ToArray();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        Assert.Empty(wd.Stalls);
        Assert.True(wd.SinceLastHeartbeatMs < 400);
    }

    [Fact]
    public void W7_停止与释放幂等_停止后不再产生新事实()
    {
        using var wd = new UiStallWatchdog(stallThresholdMs: 120, sampleIntervalMs: 50);
        wd.Start();
        wd.Start();                                   // 幂等
        wd.Heartbeat("tick");
        Assert.True(WaitFor(() => wd.Stalls.Any(f => !f.Recovered), 3000), "未判定");

        wd.Stop();
        wd.Stop();                                    // 幂等
        var snapshot = wd.Stalls.Count;
        Assert.False(wd.IsRunning);
        Thread.Sleep(400);
        Assert.Equal(snapshot, wd.Stalls.Count);      // 停止后不得再采样
        Assert.NotEmpty(wd.Stalls);                    // 事实必须保留
        wd.Dispose();
        wd.Dispose();                                  // 幂等
    }

    [Fact]
    public void W8_阈值下限之外的参数_必须拒绝而不是默默跑()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiStallWatchdog(stallThresholdMs: 99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiStallWatchdog(sampleIntervalMs: 49));
        using var ok = new UiStallWatchdog(stallThresholdMs: 100, sampleIntervalMs: 50);
        Assert.Equal(100, ok.StallThresholdMs);
    }

    [Fact]
    public void W9_Worst取最严重的一次检测_且不把恢复记录当卡住()
    {
        using var wd = new UiStallWatchdog(stallThresholdMs: 120, sampleIntervalMs: 50);
        wd.Start();
        wd.Heartbeat("tick");
        Assert.True(WaitFor(() => wd.Stalls.Count(f => !f.Recovered) == 1, 3000), "第 1 段未判定");
        wd.Heartbeat("tick");
        Thread.Sleep(900);
        wd.Heartbeat("tick");
        Assert.True(WaitFor(() => wd.Stalls.Count(f => !f.Recovered) == 2, 3000), "第 2 段未判定");

        Assert.NotNull(wd.Worst);
        Assert.False(wd.Worst!.Recovered);
        Assert.Equal(wd.Stalls.Where(f => !f.Recovered).Max(f => f.StallMs), wd.Worst.StallMs);
    }

    [Fact]
    public void W10_采样必须跑在后台定时器上_不得用UI线程定时器检测UI卡死()
    {
        var src = ReadRepoFile(@"src\PCMig.Core\Diagnostics\UiStallWatchdog.cs");

        // 后台采样：System.Threading.Timer（原理上唯一能观察到 UI 线程沉默的写法）
        Assert.Contains("new Timer(", src, StringComparison.Ordinal);
        Assert.Contains("_sampleIntervalMs", src, StringComparison.Ordinal);
        // 绝不能引入任何 UI 框架定时器 / Dispatcher —— Core 不依赖 WinUI，也不该依赖
        Assert.DoesNotContain("DispatcherQueueTimer", src, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcherTimer", src, StringComparison.Ordinal);
        Assert.DoesNotContain("Dispatcher", src, StringComparison.Ordinal);
        Assert.DoesNotContain("using Microsoft.UI", src, StringComparison.Ordinal);
        // 时长必须用单调时钟（墙上时间只用于记时刻）
        Assert.Contains("Stopwatch", src, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.TickCount", src, StringComparison.Ordinal);
        // ★ 卡住期间就要把事实交出去 ★：检测到的那一刻就回调，而不是等恢复后才报
        var detectWindow = MethodWindow(src, "private void Sample()", 1400);
        Assert.Contains("_stalls.Add(fact)", detectWindow, StringComparison.Ordinal);
        Assert.Contains("fresh.Add(fact)", detectWindow, StringComparison.Ordinal);
        Assert.Contains("foreach (var f in fresh) Notify(f);", detectWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void W11_监视器必须真接线_心跳来自UI节拍与用户操作_判定只在节拍存活期间()
    {
        // 本仓库的既成失败模式是"设计了却从不接线"（见 MigrationSessionViewModel 的关闭路由注释），
        // 所以"卡死证据"必须逐点锁住接线位置，否则它只是好看的空壳。
        var vm = ReadRepoFile(@"src\PCMig.WinUI\Presentation\MigrationSessionViewModel.cs");

        Assert.Contains("private readonly UiStallWatchdog _uiStallWatchdog;", vm, StringComparison.Ordinal);
        Assert.Contains("_uiStallWatchdog = new UiStallWatchdog(onStall: OnUiStallFact);", vm, StringComparison.Ordinal);
        Assert.Contains("public IReadOnlyList<UiStallFact> UiStalls => _uiStallWatchdog.Stalls;", vm, StringComparison.Ordinal);

        // 心跳 1：UI 节拍的每一拍（必须在任何 return 之前）
        var tick = MethodWindow(vm, "private bool DrainOnTick()", 400);
        Assert.Contains("_uiStallWatchdog.Heartbeat(\"tick\");", tick, StringComparison.Ordinal);
        Assert.True(tick.IndexOf("_uiStallWatchdog.Heartbeat(\"tick\");", StringComparison.Ordinal)
                    < tick.IndexOf("return false;", StringComparison.Ordinal),
            "心跳必须打在 DrainOnTick 的所有早退之前，否则自发停止的那一拍不算数");

        // 心跳 2：用户操作（点停止）——事后要能回答"最后一次可用交互是什么、什么时候"
        var stop = MethodWindow(vm, "public Task StopAsync()", 900);
        Assert.Contains("_uiStallWatchdog.Heartbeat(\"stop-click\");", stop, StringComparison.Ordinal);

        // 采样起止只跟节拍走：没有节拍时"没有心跳"不是卡住
        Assert.Contains("_uiStallWatchdog.Start();", MethodWindow(vm, "private void StartFlushTimer()", 700), StringComparison.Ordinal);
        Assert.Contains("_uiStallWatchdog.Stop();", MethodWindow(vm, "private void StopFlushTimer()", 700), StringComparison.Ordinal);

        // 出口只写日志事实：不得在回调里改业务状态
        var handler = MethodWindow(vm, "private void OnUiStallFact(UiStallFact fact)", 1600);
        Assert.Contains("_log.Warning", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("Cancel", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("IsStopping =", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("IsRunning =", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void W12_窗口未响应事实必须能被报告读到_且不冒充业务结论()
    {
        var vm = ReadRepoFile(@"src\PCMig.WinUI\Presentation\MigrationSessionViewModel.cs");

        Assert.Contains("public UiStallFact? WorstUiStall => _uiStallWatchdog.Worst;", vm, StringComparison.Ordinal);
        Assert.Contains("public long UiSilenceMs => _uiStallWatchdog.SinceLastHeartbeatMs;", vm, StringComparison.Ordinal);

        // 界面未响应 ≠ 迁移失败：它不得出现在任何"通过/失败"判据里（OverallPass / AllowsDestructiveRepair 等）。
        var watchdogOnly = vm[..vm.IndexOf("private void OnUiStallFact", StringComparison.Ordinal)];
        Assert.Contains("UiStalls => _uiStallWatchdog.Stalls;", watchdogOnly, StringComparison.Ordinal);
    }

    private static string MethodWindow(string text, string signature, int length)
    {
        var at = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"源码里找不到 {signature}");
        var len = Math.Min(length, text.Length - at);
        return text.Substring(at, len);
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