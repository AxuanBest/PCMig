// ============================================================================
//  ★ P0-06 ★ 停止链路：点击 → 立即可见反馈 → Flush → 取消投递 → 引擎收尾 → 窗口可响应
// ============================================================================
//
//  ── 为什么这些测试必须存在（现场事实）───────────────────────────────────────
//  用户现场描述「共享断开、点击停止时 UI 卡住或无响应」，而 ZIP 里只有 Robocopy 被强制
//  终止这一条结果事实，没有 WER / Application Hang 1002 / 转储 / 录像 ⇒ 不能凭猜改代码。
//  所以先把这条链路变成**可断言的分段事实**（StopTimeline），再钉死三条纪律：
//    ① 点击之后必须立刻出现可见进行时（按钮「正在停止…」+ 状态句），且调用**不阻塞**；
//    ② 重复按停止必须幂等（只有一次取消投递），不得叠加第二个取消/第二段收尾；
//    ③ 超时未收尾时如实说"仍在清理/等待网络 I/O"，**绝不**谎称已停止，也不假装已停止；
//       而"没有在跑的运行"时不得留下"正在停止…"这种假进行时。
//
//  ── 怎么够得着 ─────────────────────────────────────────────────────────────
//  与 PauseUiStateMachineTests 同一策略：`PCMig.Core.Tests.csproj` 把
//  `src\PCMig.WinUI\Presentation\*.cs` 源码链入编译；传输引擎通过产品自带测试缝
//  `MigrationSessionViewModel.TransferRunner` 注入假实现 ⇒ 本文件不启动 robocopy、不触网。
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PCMig.Core.Jobs;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using PCMig.Diagnostics.Abstractions;
using PCMig.WinUI.Presentation;
using Serilog;
using Xunit;

namespace PCMig.Core.Tests;

[Collection("A5-jobdir-isolation")]
public sealed class P0StopChainTests : IDisposable
{
    private const string ProbeHost = "P0-STOP-PROBE-HOST";

    private readonly string _sandbox;
    private readonly ILogger _log;
    private readonly string? _previousJobsRoot;

    public P0StopChainTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "pcmig-stop-chain", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
        _previousJobsRoot = Environment.GetEnvironmentVariable("PCMIG_JOBS");
        Environment.SetEnvironmentVariable("PCMIG_JOBS", Path.Combine(_sandbox, "Jobs"));
        _log = new LoggerConfiguration().CreateLogger();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PCMIG_JOBS", _previousJobsRoot);
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    // ────────────────────────────── 夹具 ──────────────────────────────

    private string NewDir(params string[] parts)
    {
        var path = Path.Combine(new[] { _sandbox }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private JobContext CreateJob(string jobId, MigrationPlan? plan = null, JobState? state = null)
    {
        var def = new JobDefinition
        {
            JobId = jobId,
            SourceHost = ProbeHost,
            TargetRoot = NewDir("target", jobId),
            Sources = { new SourceSpec { Path = @"\\" + ProbeHost + @"\C$", Kind = ObjectKind.DataVolume } },
            Options = new MigrationOptions { Threads = 16 },
        };
        var ctx = new JobManager(_log).Create(def);
        if (plan is not null) ctx.SavePlan(plan);
        if (state is not null) ctx.SaveState(state);
        return ctx;
    }

    private static MigrationPlan PlanWithOneObject(string sourcePath, long bytes = 4096)
        => new()
        {
            JobId = "plan",
            TotalBytes = bytes,
            Objects =
            {
                new PlannedObject
                {
                    ObjectId = "object-000001",
                    Kind = ObjectKind.DataVolume,
                    SourcePath = sourcePath,
                    TargetPath = sourcePath,
                    EstimatedBytes = bytes,
                    EstimatedFiles = 1,
                },
            },
        };

    private static JobState RunningState(string jobId) => new()
    {
        JobId = jobId,
        Phase = JobPhase.Running,
        TotalObjects = 3,
        CompletedObjects = 1,
        TotalBytes = 300L * 1024 * 1024,
        CompletedBytes = 100L * 1024 * 1024,
    };

    private sealed class InlineSyncContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    private static async Task WithInlineSyncContext(Func<Task> body)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        try { await body(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    /// <summary>跑一次"脚本化"的运行：<paramref name="body"/> 在引擎内部执行（此刻 IsRunning == true）。</summary>
    private async Task RunScriptedAsync(
        MigrationSessionViewModel vm,
        Func<JobContext, Task> body,
        JobPhase result = JobPhase.Interrupted)
    {
        vm.TransferRunner = (ctx, hooks, progress, ct, onlyObjectIds, forceRecopy) =>
        {
            body(ctx).GetAwaiter().GetResult();
            return Task.FromResult(result);
        };
        await WithInlineSyncContext(() => vm.RunAsync(password: null));
    }

    // ══════════════════════════ S1 ══════════════════════════

    /// <summary>点击停止：立刻出现可见进行时，且这次调用本身不阻塞（不得在 Dispatcher 上等待）。</summary>
    [Fact]
    public async Task S1_ClickStop_ShowsVisibleProgressImmediately_AndDoesNotBlock()
    {
        var src = NewDir("s1", "src");
        var ctx = CreateJob("JOB-S1", PlanWithOneObject(src), RunningState("JOB-S1"));
        var vm = new MigrationSessionViewModel(null, null, _log);
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        var before = (IsRunning: false, IsStopping: true, CanStop: false, Text: string.Empty, Status: string.Empty);
        var after = before;
        long elapsed = -1;
        StopTimeline? tl = null;

        await RunScriptedAsync(vm, async _ =>
        {
            before = (vm.IsRunning, vm.IsStopping, vm.CanStop, vm.StopButtonText, vm.StatusMessage);

            var sw = Stopwatch.StartNew();
            await vm.StopAsync();                 // ← 用户点击底栏「停止」
            elapsed = sw.ElapsedMilliseconds;

            after = (vm.IsRunning, vm.IsStopping, vm.CanStop, vm.StopButtonText, vm.StatusMessage);
            tl = vm.LastStopTimeline;
        });

        // 点击之前：确实在跑、可以停止、文案就是「停止」
        Assert.True(before.IsRunning);
        Assert.False(before.IsStopping);
        Assert.True(before.CanStop);
        Assert.Equal("停止", before.Text);

        // 点击之后（引擎尚未收尾）：可见进行时 + 按钮进入 busy（不可重复按）
        Assert.True(after.IsRunning);                 // 引擎真值仍是"在跑"，不乐观
        Assert.True(after.IsStopping);                // ★ 进行时真的存在
        Assert.Equal("正在停止…", after.Text);         // ★ 点下去立刻有反馈
        Assert.False(after.CanStop);                  // busy：不叠加第二次意图
        Assert.Contains("正在停止", after.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("已停止", after.Status, StringComparison.Ordinal);

        // 请求受理 ≠ 业务达成
        Assert.Equal(DiagnosticOutcome.Accepted, vm.LastStopOutcome.Outcome);
        Assert.Equal("stop-requested", vm.LastStopOutcome.ReasonCode);

        // 分段计时事实：点击→反馈→Flush→取消投递
        Assert.NotNull(tl);
        Assert.Equal("user-stop", tl!.Reason);
        Assert.True(tl.Has("click"));
        Assert.True(tl.Has("feedback-shown"));
        Assert.True(tl.Has("flush-done"));
        Assert.True(tl.Has("cancel-delivered"));
        Assert.True(elapsed < 250, $"StopAsync 不该阻塞（实测 {elapsed} ms）");

        // 运行收尾后（引擎真值 IsRunning=false）：进行时结束、时间线记下"引擎收尾"
        Assert.False(vm.IsStopping);
        Assert.Equal("停止", vm.StopButtonText);
        Assert.True(tl.Has("engine-settled"), tl.ToSummary());
    }

    // ══════════════════════════ S2 ══════════════════════════

    /// <summary>重复按停止：幂等，只允许一次取消投递，不得产生第二个取消/第二段收尾。</summary>
    [Fact]
    public async Task S2_RepeatedStop_IsIdempotent_OnlyOneCancelDelivery()
    {
        var src = NewDir("s2", "src");
        var ctx = CreateJob("JOB-S2", PlanWithOneObject(src), RunningState("JOB-S2"));
        var vm = new MigrationSessionViewModel(null, null, _log);
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        var texts = new System.Collections.Generic.List<string>();
        var outcomes = new System.Collections.Generic.List<string>();

        await RunScriptedAsync(vm, async _ =>
        {
            await vm.StopAsync();
            texts.Add(vm.StopButtonText);
            await vm.StopAsync();                  // 用户连点第二次
            texts.Add(vm.StopButtonText);
            outcomes.Add(vm.LastStopOutcome.Outcome + "/" + vm.LastStopOutcome.ReasonCode);
        });

        Assert.Equal(new[] { "正在停止…", "正在停止…" }, texts.ToArray());
        Assert.All(outcomes, o => Assert.Equal("Accepted/stop-requested", o));

        var tl = vm.LastStopTimeline!;
        Assert.Equal(1, tl.Segments.Count(s => s.Phase == "cancel-delivered"));
        Assert.True(tl.Has("already-stopping") || tl.Has("reentry-ignored"));
        Assert.Equal(1, tl.Segments.Count(s => s.Phase == "feedback-shown"));
    }

    // ══════════════════════════ S3 ══════════════════════════

    /// <summary>SLA 内没等到引擎收尾：如实说"仍在清理/等待网络 I/O"，绝不谎称已停止。</summary>
    [Fact]
    public async Task S3_NotSettledInSla_SaysStillCleaning_NeverClaimsStopped()
    {
        var src = NewDir("s3", "src");
        var ctx = CreateJob("JOB-S3", PlanWithOneObject(src), RunningState("JOB-S3"));
        var vm = new MigrationSessionViewModel(null, null, _log);
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        var settled = true;
        var statusAfterWait = string.Empty;
        var stoppingAfterWait = false;
        var textAfterWait = string.Empty;

        await RunScriptedAsync(vm, async _ =>
        {
            await vm.StopAsync();
            // 用"外部取消"把等待提前结束（等价于 SLA 到期仍未收尾），避免测试真的睡满 SLA。
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
            settled = await vm.WaitForStopSettledAsync(cts.Token);
            statusAfterWait = vm.StatusMessage;
            stoppingAfterWait = vm.IsStopping;
            textAfterWait = vm.StopButtonText;
        });

        Assert.False(settled);                                     // ★ 如实：还没停住
        Assert.True(stoppingAfterWait);                            // 进行时保持
        Assert.Equal("正在停止…", textAfterWait);
        Assert.Contains("停止尚未落定", statusAfterWait, StringComparison.Ordinal);
        Assert.Contains("清理或等待网络 I/O", statusAfterWait, StringComparison.Ordinal);
        Assert.DoesNotContain("已停止", statusAfterWait, StringComparison.Ordinal);

        var tl = vm.LastStopTimeline!;
        Assert.True(tl.Has("settle-timeout"), tl.ToSummary());
        Assert.False(tl.IsComplete);                                // 收口由 Shell 层在底栏投影后完成

        // 引擎随后真的收尾 ⇒ 进行时结束、时间线记下 engine-settled
        Assert.False(vm.IsStopping);
        Assert.True(tl.Has("engine-settled"), tl.ToSummary());
    }

    // ══════════════════════════ S4 ══════════════════════════

    /// <summary>没有在跑的运行时点停止：不得留下"正在停止…"假进行时，也不得报"已停止"。</summary>
    [Fact]
    public async Task S4_StopWithoutRunningRun_LeavesNoFakeProgressState()
    {
        var src = NewDir("s4", "src");
        var ctx = CreateJob("JOB-S4", PlanWithOneObject(src));
        var vm = new MigrationSessionViewModel(null, null, _log);
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));
        Assert.False(vm.IsRunning);

        await vm.StopAsync();

        Assert.False(vm.IsStopping);
        Assert.Equal("停止", vm.StopButtonText);
        Assert.False(vm.CanStop);
        Assert.Contains("未在运行", vm.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("正在停止", vm.StatusMessage, StringComparison.Ordinal);

        var tl = vm.LastStopTimeline!;
        Assert.True(tl.IsComplete);
        Assert.Equal("stop-no-running-run", tl.Outcome);
        Assert.Equal(DiagnosticOutcome.Skipped, vm.LastStopOutcome.Outcome);
        Assert.Equal("stop-no-running-run", vm.LastStopOutcome.ReasonCode);
    }

    // ══════════════════════════ S5（契约）══════════════════════════

    /// <summary>
    /// 契约：进行时真值必须来自会话（真值 ⇒ 文案/使能，不反过来），并且 Shell 层是这段
    /// 时间线的收口人（点击→等待→投影→可再操作都留痕）。窗口可响应的终点不允许缺失。
    /// </summary>
    [Fact]
    public void S5_ShellProjectsBusyState_AndOwnsTheTimelineTail()
    {
        var vmSrc = ReadRepoFile(@"src\PCMig.WinUI\Presentation\MigrationSessionViewModel.cs");
        Assert.Contains("public bool CanStop => IsRunning && !IsStopping;", vmSrc, StringComparison.Ordinal);
        Assert.Contains("public string StopButtonText => IsStopping ? \"正在停止…\" : \"停止\";", vmSrc, StringComparison.Ordinal);

        var shell = ReadRepoFile(@"src\PCMig.WinUI\MainWindow.xaml.cs");
        Assert.Contains("FooterStopLabel.Text = Session.StopButtonText;", shell, StringComparison.Ordinal);
        Assert.Contains("FooterStopIcon.Glyph = Session.IsStopping ?", shell, StringComparison.Ordinal);
        var click = MethodWindow(shell, "private async void FooterStop_Click(object sender, RoutedEventArgs e)", 2200);
        Assert.Contains("await Session.StopAsync();", click, StringComparison.Ordinal);
        Assert.Contains("Session.LastStopTimeline?.Mark(\"footer-await-settle\");", click, StringComparison.Ordinal);
        Assert.Contains("await Session.WaitForStopSettledAsync();", click, StringComparison.Ordinal);
        Assert.Contains("Session.LastStopTimeline?.Mark(\"footer-pushed\");", click, StringComparison.Ordinal);
        Assert.Contains("Session.LastStopTimeline?.Complete(stopped ? \"stop-settled\" : \"stop-unsettled\");", click, StringComparison.Ordinal);

        // 底栏图标必须有名字才投影得动（缺 x:Name 会在运行时抛 NullReference/静默不更新）
        var xaml = ReadRepoFile(@"src\PCMig.WinUI\MainWindow.xaml");
        Assert.Contains("x:Name=\"FooterStopIcon\"", xaml, StringComparison.Ordinal);

        // 等待必须异步轮询（await Task.Delay），不得出现同步阻塞伪装响应
        var wait = MethodWindow(vmSrc, "public async Task<bool> WaitForStopSettledAsync(CancellationToken ct = default)", 1400);
        Assert.Contains("await Task.Delay(50, ct)", wait, StringComparison.Ordinal);
        Assert.DoesNotContain(".Wait(", wait, StringComparison.Ordinal);
        Assert.DoesNotContain(".Result", wait, StringComparison.Ordinal);
    }

    // ────────────────────────────── 工具 ──────────────────────────────

    /// <summary>按签名取方法窗口（从签名之后开始，长度 cap 防止跨方法误判）。</summary>
    private static string MethodWindow(string text, string signature, int length)
    {
        var at = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"找不到方法签名：{signature}");
        var start = at + signature.Length;
        return text.Substring(start, Math.Min(length, text.Length - start));
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