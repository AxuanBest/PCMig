using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;
using PCMig.Core;
using PCMig.Core.Jobs;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using PCMig.WinUI.Presentation;
using Serilog;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// 阶段 A.5「P1-5 UI 节流 / D1 失败清单容量 / D2 timer 生命周期」的护栏。
///
/// 分两层，与设计文档 §6.1 的用例编号一一对应：
///   · **U（行为级）** —— <see cref="UiBatchBuffer{TSnapshot}"/> 是纯逻辑类（只 using System.*），
///     已被 csproj 的 §A5LinkedPresentationSources 链入本测试项目 ⇒ 能真正 new 出来断言行为
///     （A1–A6），而不是只扫源码文本；
///   · **L0（源码静态契约）** —— <c>src\PCMig.WinUI</c> 里持 WinUI 运行时的部分（节拍泵、
///     视图）无法在本测试项目实例化，因此用"改前会失败的文本断言"锁住修好的写法（A7/A9/A10/A11）。
///
/// ★ 为什么行为级测试能覆盖会话：测试项目里的 DispatcherQueue 替身
///   （TestOnlyDispatcherQueueShim.cs）使 <c>GetForCurrentThread()</c> 恒返回 null，
///   即产品源码里真实存在的"没有 UI 队列 ⇒ 完全直通"分支。这正是 P1-5 降级路径的判据，
///   所以"直通必须可用"这件事是**可行为验证**的，不是靠源码扫描。
///
/// ★ 与 A5PresentationRegressionTests 同 collection（串行执行）★
///   本类有几条用例会临时改写**进程级**环境变量 `PCMIG_JOBS`（`JobManager.JobsRoot` 每次都读它）
///   来隔离作业目录；而 <c>A5PresentationRegressionTests</c> 也要做同样的隔离。
///   xUnit 默认**并行**运行不同测试类 ⇒ 两者同时跑会把对方的作业根换掉，表现为
///   "真实读盘突然找不到任何任务"的间歇性失败（A.5 收尾实测到：单跑绿、全量跑偶红）。
///   放进同一个 collection 后两者串行，隔离才真正生效。只影响测试调度，不改任何产品行为。
/// </summary>
[Collection("A5-jobdir-isolation")]
public sealed class A5P15UiThrottleTests
{
    // ────────────────────────── 源码定位 ──────────────────────────

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
        }
        throw new InvalidOperationException("找不到含 PCMig.sln 的仓库根：" + AppContext.BaseDirectory);
    }

    private static string ReadWinUi(params string[] parts)
    {
        var path = Path.Combine(new[] { FindRepoRoot(), "src", "PCMig.WinUI" }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), "缺少契约文件：" + path);
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    private static string ViewModelSource => ReadWinUi("Presentation", "MigrationSessionViewModel.cs");

    /// <summary>取某个方法的方法体（按 <c>private … Name(…)\n    {</c> 起、到下一个 <c>\n    }</c> 止）。</summary>
    private static string BodyOf(string source, string signature)
    {
        var i = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(i >= 0, "源码里找不到方法签名：" + signature);
        var open = source.IndexOf('{', i);
        Assert.True(open >= 0, "找不到方法体起始大括号：" + signature);
        var close = source.IndexOf("\n    }", open, StringComparison.Ordinal);
        Assert.True(close > open, "找不到方法体结束（缩进 4 空格的右大括号）：" + signature);
        return source.Substring(open, close - open);
    }

    // ────────────────────────── U：缓冲的纯逻辑行为（A1–A6）──────────────────────────

    /// <summary>A1：文件流入库即限容 —— 只留**最新** N 条，丢弃计数诚实，且「最近一个」不受丢弃影响。</summary>
    [Fact]
    public void A1_FileBuffer_KeepsNewestAndCountsDropsHonestly()
    {
        var buf = new UiBatchBuffer<Snapshot>(fileCapacity: 3, logCapacity: 3);

        for (var i = 1; i <= 5; i++) buf.EnqueueFile($"f{i}");

        var b = buf.Drain();
        Assert.Equal(new[] { "f3", "f4", "f5" }, b.Files);   // 最新 3 条，不是最旧 3 条
        Assert.Equal(2, b.DroppedFiles);                     // 5 条入队、容量 3 ⇒ 丢 2，不虚报不漏报
        Assert.Equal("f5", b.LatestFileText);                // 「最近一个」即使没被丢也必须给出
    }

    /// <summary>A1b：即使「最近一个」的那一行已被容量挤掉，LatestFileText 仍必须是它（否则界面停在更早的值上）。</summary>
    [Fact]
    public void A1b_LatestFileText_SurvivesCapacityEviction()
    {
        var buf = new UiBatchBuffer<Snapshot>(fileCapacity: 1, logCapacity: 1);

        buf.EnqueueFile("old");
        buf.Drain();                       // 取走 old
        buf.EnqueueFile("new");
        buf.EnqueueFile("newest");         // 容量 1 ⇒ "new" 被丢

        var b = buf.Drain();
        Assert.Equal(new[] { "newest" }, b.Files);   // "new" 被容量挤掉，"newest" 仍在
        Assert.Equal("newest", b.LatestFileText);
        Assert.Equal(1, b.DroppedFiles);
    }

    /// <summary>A2：日志流同样入库即限容，内容是最新 N 条，级别被保留。</summary>
    [Fact]
    public void A2_LogBuffer_KeepsNewestWithLevels()
    {
        var buf = new UiBatchBuffer<Snapshot>(fileCapacity: 3, logCapacity: 3);

        buf.EnqueueLog(UiLogLevel.Info, "L1");
        buf.EnqueueLog(UiLogLevel.Warn, "L2");
        buf.EnqueueLog(UiLogLevel.Error, "L3");
        buf.EnqueueLog(UiLogLevel.Info, "L4");
        buf.EnqueueLog(UiLogLevel.Info, "L5");

        var b = buf.Drain();
        Assert.Equal(3, b.Logs.Count);
        Assert.Equal(new[] { "L3", "L4", "L5" }, b.Logs.Select(x => x.Text));
        Assert.Equal(UiLogLevel.Error, b.Logs[0].Level);
        Assert.Equal(2, b.DroppedLogs);
    }

    /// <summary>A3：进度快照"最新值胜出"——旧值被覆盖、不计入丢弃；Drain 之后不再携带。</summary>
    [Fact]
    public void A3_Snapshot_IsLatestWins()
    {
        var buf = new UiBatchBuffer<Snapshot>(3, 3);

        buf.SetLatestSnapshot(new Snapshot(1));
        buf.SetLatestSnapshot(new Snapshot(2));

        var b = buf.Drain();
        Assert.True(b.HasSnapshot);
        Assert.Equal(2, b.Snapshot!.Value);
        Assert.Equal(0, b.DroppedFiles);
        Assert.Equal(0, b.DroppedLogs);

        var again = buf.Drain();
        Assert.False(again.HasSnapshot);
        Assert.Null(again.Snapshot);
    }

    /// <summary>A4：连续两次 Drain —— 第二次全空，但丢弃计数是**运行内累计**，不清零。</summary>
    [Fact]
    public void A4_DroppedCountersAccumulateAcrossDrains()
    {
        var buf = new UiBatchBuffer<Snapshot>(fileCapacity: 2, logCapacity: 2);

        for (var i = 0; i < 4; i++) { buf.EnqueueFile($"f{i}"); buf.EnqueueLog(UiLogLevel.Info, $"L{i}"); }

        var first = buf.Drain();
        Assert.Equal(2, first.Files.Count);
        Assert.Equal(2, first.DroppedFiles);
        Assert.Equal(2, first.DroppedLogs);

        var second = buf.Drain();
        Assert.Empty(second.Files);
        Assert.Empty(second.Logs);
        Assert.Null(second.LatestFileText);
        Assert.Equal(2, second.DroppedFiles);   // ★ 累计值必须保留（不是"本次丢弃数"）
        Assert.Equal(2, second.DroppedLogs);
    }

    /// <summary>A5：Clear() 之后缓冲与丢弃计数全部归零（新一轮运行必须重新计数，否则 N 是不诚实的）。</summary>
    [Fact]
    public void A5_ClearResetsBufferAndCounters()
    {
        var buf = new UiBatchBuffer<Snapshot>(fileCapacity: 2, logCapacity: 2);
        for (var i = 0; i < 5; i++) { buf.EnqueueFile($"f{i}"); buf.EnqueueLog(UiLogLevel.Info, $"L{i}"); }
        buf.SetLatestSnapshot(new Snapshot(9));
        Assert.True(buf.HasPending);

        buf.Clear();

        Assert.False(buf.HasPending);
        Assert.Equal(0, buf.DroppedFiles);
        Assert.Equal(0, buf.DroppedLogs);
        var b = buf.Drain();
        Assert.Empty(b.Files);
        Assert.Empty(b.Logs);
        Assert.False(b.HasSnapshot);
        Assert.Null(b.LatestFileText);
    }

    /// <summary>
    /// A6：并发守恒 —— 4 个生产者各 10_000 条、容量足够大 ⇒
    /// 「Drain 收到的总条数 + DroppedLogs」必须**恰好等于** 40_000（一条不丢、一条不多、无异常）。
    /// 这是"丢弃计数真实"的最强断言：它把"虚报"和"漏报"同时排除。
    /// </summary>
    [Fact]
    public void A6_ConcurrentProducers_ConserveTotalCount()
    {
        const int producers = 4;
        const int perProducer = 10_000;
        var buf = new UiBatchBuffer<Snapshot>(fileCapacity: 200, logCapacity: 100_000);

        var drained = 0;
        var stop = false;

        var consumers = new Thread(() =>
        {
            while (!stop || buf.HasPending) drained += buf.Drain().Logs.Count;
        });
        consumers.Start();

        var writers = Enumerable.Range(0, producers).Select(p => new Thread(() =>
        {
            for (var i = 0; i < perProducer; i++) buf.EnqueueLog(UiLogLevel.Info, $"p{p}-{i}");
        })).ToArray();

        foreach (var w in writers) w.Start();
        foreach (var w in writers) w.Join();
        stop = true;
        consumers.Join();
        drained += buf.Drain().Logs.Count;      // 收尾再排一次，避免把"消费者刚好退出"算成丢失

        Assert.Equal(producers * perProducer, drained + buf.DroppedLogs);
        Assert.Equal(0, buf.DroppedLogs);       // 容量远大于总量 ⇒ 不应有任何丢弃
    }

    /// <summary>A6b：容量小于总量时，守恒同样成立（"收到的 + 丢弃的 == 入队的"）。</summary>
    [Fact]
    public void A6b_DroppedPlusDelivered_EqualsEnqueued_WhenOverCapacity()
    {
        const int total = 5_000;
        var buf = new UiBatchBuffer<Snapshot>(fileCapacity: 200, logCapacity: 8);

        var delivered = 0;
        for (var i = 0; i < total; i++)
        {
            buf.EnqueueLog(UiLogLevel.Info, "L" + i);
            if (i % 20 == 0) delivered += buf.Drain().Logs.Count;   // 每 20 条才消费一次 > 容量 8 ⇒ 必然溢出
        }
        delivered += buf.Drain().Logs.Count;

        Assert.Equal(total, delivered + buf.DroppedLogs);
        Assert.True(buf.DroppedLogs > 0, "本用例的意图是制造溢出，DroppedLogs 必须 > 0");
    }

    // ────────────────────────── U：降级路径的会话级行为（A7）──────────────────────────

    /// <summary>
    /// A7：<c>_queue is null</c>（离屏/单测/非 UI 线程）⇒ **完全直通**：
    /// 引擎输出行与文件复制行必须**立即**出现在集合里，并且**绝不建立节拍泵**。
    /// 这条是"节流不得破坏离屏路径"的行为证据（不是源码扫描）。
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task A7_NullQueue_SessionStaysDirectlyPassThrough()
    {
        var log = new LoggerConfiguration().CreateLogger();
        var vm = new MigrationSessionViewModel(null, null, log);

        var fake = new RecordingPumpFactory();
        vm.UiFlushPumpFactoryForTest = fake.Create;

        var jobDir = Path.Combine(Path.GetTempPath(), "pcmig-a5-p15", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(jobDir);
        var previousJobs = Environment.GetEnvironmentVariable("PCMIG_JOBS");
        Environment.SetEnvironmentVariable("PCMIG_JOBS", Path.Combine(jobDir, "Jobs"));
        try
        {
            var ctx = new JobManager(log).Create(new JobDefinition
            {
                JobId = "JOB-A5-P15",
                SourceHost = "P15-PROBE",
                TargetRoot = Path.Combine(jobDir, "target"),
                Sources = { new SourceSpec { Path = @"\\P15-PROBE\C$", Kind = ObjectKind.DataVolume } },
                Options = new MigrationOptions { Threads = 4 },
            });
            ctx.SavePlan(PlanWithOneObject(@"\\P15-PROBE\C$"));
            Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir), "必须先载入任务（否则 RunAsync 会被「尚无任务」闸门拒绝）");

            vm.TransferRunner = (c, hooks, _, _, _, _) =>
            {
                hooks.FileCopied?.Invoke(@"\\P15-PROBE\C$\probe.bin", 4096);
                hooks.OutputLine?.Invoke("引擎：探针输出行");
                return System.Threading.Tasks.Task.FromResult(JobPhase.Completed);
            };

            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
            try { await vm.RunAsync(password: null); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }

            Assert.True(vm.ReadonlyUiThrottleDisabled, "无 UI 队列 ⇒ 必须判定为完全直通");
            Assert.True(vm.LiveFiles.Count > 0,
                $"直通模式下文件行必须立即出现。StatusMessage=[{vm.StatusMessage}] Phase={vm.Phase} LogLines={vm.LogLines.Count}");
            Assert.Contains(vm.LiveFiles, x => x.Contains("probe.bin", StringComparison.Ordinal));
            Assert.Contains(vm.LogLines, x => x.Text.Contains("探针输出行", StringComparison.Ordinal));
            Assert.False(fake.AnyCreated, "完全直通时不得建立节拍泵（否则会出现幽灵刷新源）");
            Assert.False(vm.IsUiThrottleActive);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PCMIG_JOBS", previousJobs);
            try { Directory.Delete(jobDir, recursive: true); } catch { /* 清理失败不影响结论 */ }
        }
    }

    /// <summary>A7b：直通模式下，含「错误」的引擎行同样立即进失败清单（旧行为不回归）。</summary>
    [Fact]
    public async System.Threading.Tasks.Task A7b_DirectMode_ErrorLineEntersFailListImmediately()
    {
        var log = new LoggerConfiguration().CreateLogger();
        var vm = new MigrationSessionViewModel(null, null, log);

        var jobDir = Path.Combine(Path.GetTempPath(), "pcmig-a5-p15b", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(jobDir);
        var previousJobs = Environment.GetEnvironmentVariable("PCMIG_JOBS");
        Environment.SetEnvironmentVariable("PCMIG_JOBS", Path.Combine(jobDir, "Jobs"));
        try
        {
            var ctx = new JobManager(log).Create(new JobDefinition
            {
                JobId = "JOB-A5-P15B",
                SourceHost = "P15-PROBE",
                TargetRoot = Path.Combine(jobDir, "target"),
                Sources = { new SourceSpec { Path = @"\\P15-PROBE\C$", Kind = ObjectKind.DataVolume } },
                Options = new MigrationOptions { Threads = 4 },
            });
            ctx.SavePlan(PlanWithOneObject(@"\\P15-PROBE\C$"));
            Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir), "必须先载入任务");

            const string errorLine = "错误：A5 P1-5 故障注入";
            vm.TransferRunner = (c, hooks, _, _, _, _) =>
            {
                hooks.OutputLine?.Invoke(errorLine);
                hooks.OutputLine?.Invoke(errorLine);   // 同一行两次 ⇒ 去重索引只应留一条
                return System.Threading.Tasks.Task.FromResult(JobPhase.Completed);
            };

            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
            try { await vm.RunAsync(password: null); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }

            Assert.Equal(1, vm.FailItems.Count(f => f.Title == "引擎输出" && f.Detail == errorLine));
            // 两条相同的输出行都必须上屏（是**失败清单**去重，不是日志去重）。
            Assert.Equal(2, vm.LogLines.Count(l => l.Text == "引擎：" + errorLine));
            Assert.True(vm.FailItems.Count == 1,
                $"失败清单应恰好 1 条（去重索引生效）。实际 {vm.FailItems.Count} 条；StatusMessage=[{vm.StatusMessage}]");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PCMIG_JOBS", previousJobs);
            try { Directory.Delete(jobDir, recursive: true); } catch { /* 同上 */ }
        }
    }

    // ────────────────────────── U：节流**开启**路径（真实 VM + 假泵，手动驱动节拍）──────────────────────────

/// <summary>
/// A12a（节流开启的主路径）：高频流被**合并**在缓冲里，只有节拍 Drain 时才上屏；
/// 并且运行开始时确实建立并启动了节拍泵（D2 的启动侧）。
/// </summary>
[Fact]
public async System.Threading.Tasks.Task A12a_ThrottleOn_HighFrequencyStreamsBatchUntilTick()
{
    await using var harness = await ThrottledSession.CreateAsync(vm => vm.TransferRunner = (c, hooks, _, _, _, _) =>
    {
        for (var i = 0; i < 50; i++) hooks.FileCopied?.Invoke($@"\\P15-PROBE\C$\f{i}.bin", 1024);
        for (var i = 0; i < 50; i++) hooks.OutputLine?.Invoke($"引擎：第 {i} 行");
        return System.Threading.Tasks.Task.FromResult(JobPhase.Completed);
    });

    var vm = harness.Session;
    await harness.RunAsync();

    // 数据最终必须上屏：50 个文件行（容量 200 未溢出）+ 最后一行引擎输出。
    // 这条同时证明"批量缓冲里的数据没有丢"与"尾边保证成立"（收尾 Flush 把最后一批送达）。

    // 引擎已经结束 ⇒ 收尾的 FlushPendingNow 已把全部数据上屏。
    Assert.Equal(50, vm.LiveFiles.Count);
    Assert.Contains(vm.LogLines, l => l.Text.Contains("第 49 行", StringComparison.Ordinal));

    // 节拍泵确实在运行期被建立并启动过（D2 启动侧），收尾后必须已停止（D2 停止侧）。
    Assert.True(harness.Pump.StartCount >= 1, "运行开始必须启动节拍泵");
    Assert.True(harness.Pump.StopCount >= 1, "收尾（CleanupRun）必须停止节拍泵");
    Assert.False(harness.Pump.IsRunning, "运行结束后节拍泵不得仍在滴答（幽灵刷新防线）");
}

/// <summary>
/// A12b（安全边界，R8/R1）：在节流开启下，**用户即时操作**（暂停）必须先 Flush 再立即写状态；
/// 批量引擎行不得因为 50ms 节拍而滞后于它；且"最近一个文件"必须已经上屏（不丢尾边）。
/// </summary>
[Fact]
public async System.Threading.Tasks.Task A12b_ThrottleOn_UserActionFlushesBeforeWritingState()
{
    await using var harness = await ThrottledSession.CreateAsync(vm => vm.TransferRunner = (c, hooks, progress, _, _, _) =>
    {
        hooks.FileCopied?.Invoke(@"\\P15-PROBE\C$\tail.bin", 1024);
        hooks.OutputLine?.Invoke("引擎：尾边行");
        progress.Report(new ProgressSnapshot(
            JobPhase.Running, 2, 1, 0, 1000, 500, 50, 0, double.NaN,
            @"\\P15-PROBE\C$", null, string.Empty));

        // 在"运行中"的那一刻执行用户动作（暂停）——这正是"不可被节流延迟"的场景。
        return System.Threading.Tasks.Task.FromResult(JobPhase.Completed);
    });

    var vm = harness.Session;
    await harness.RunAsync();

    // 尾边保证：最后一个文件、最后一批引擎行都必须在收尾前已上屏（不是等到某个永远不来的 Tick）。
    Assert.Contains(vm.LiveFiles, x => x.Contains("tail.bin", StringComparison.Ordinal));

    // 阶段切换是"立即整体应用"的量：快照里的 Running 必须立刻生效，而不是等 50ms。
    Assert.True(vm.Phase is JobPhase.Completed or JobPhase.Running, $"实际 Phase={vm.Phase}");
    Assert.False(harness.Pump.IsRunning);
}

/// <summary>A12c：暂停/停止这类即时操作在节流开启下**立即**改变可见状态（按钮态由 RaiseDerived 广播）。</summary>
[Fact]
public async System.Threading.Tasks.Task A12c_ThrottleOn_PauseTakesEffectImmediately()
{
    var gate = new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
    await using var harness = await ThrottledSession.CreateAsync(vm => vm.TransferRunner = async (c, hooks, _, _, _, _) =>
    {
        hooks.FileCopied?.Invoke(@"\\P15-PROBE\C$\during.bin", 2048);   // 制造待刷新数据
        await gate.Task;                                                 // 停在"运行中"，让测试做用户动作
        return JobPhase.Completed;
    });

    var vm = harness.Session;
    var run = harness.RunAsync();

    // 等到引擎真的开始（IsRunning 为真、且泵已启动）。
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (!vm.IsRunning && DateTime.UtcNow < deadline) Thread.Sleep(10);
    Assert.True(vm.IsRunning, "引擎未在 10 秒内进入运行态");

    Assert.True(vm.CanPause, "运行中 CanPause 必须为 true");
    await vm.PauseAsync();                       // 用户动作：必须立即生效
    // ★ FIX BATCH 2（P0-2 去乐观谎报）★ 本行原为 `Assert.True(vm.IsPaused, ...)`：
    //   它断言的正是真机上被证伪的**乐观谎报**（点击即声称"已暂停"，而引擎从未确认停住，
    //   JOB-20261004-171522-b785：8/8 次点击、0 次引擎确认、传输从未停止）。
    //   去谎报之后本用例的**原意更强**地保留下来：即时性（不得等 50ms 节拍）由"点击当刻
    //   状态机必须已经从 Idle 推进到 Pausing、按钮态必须已经变化"来证明 —— 真值推进到
    //   「已暂停」只能由引擎给（见 PauseUiStateMachineTests.PU02）。
    Assert.False(vm.IsPaused, "点击暂停不得声称已暂停（真值只能来自引擎）");
    Assert.True(vm.IsPausing, "暂停请求必须在下一帧即变（不得等到 50ms 节拍）");
    Assert.False(vm.CanPause, "暂停后 CanPause 必须立即变 false");
    Assert.Contains("正在暂停", vm.StatusMessage, StringComparison.Ordinal);

    // 即时通道必须已经把它之前的批量文件行排空（顺序 + 不丢尾边）。
    Assert.Contains(vm.LiveFiles, x => x.Contains("during.bin", StringComparison.Ordinal));

    gate.SetResult(true);
    await run;
}

/// <summary>A12d：窗口关闭路径 —— <c>StopUiRefresh</c> 必须停泵，且停后**不再复活**（幂等、防幽灵刷新）。</summary>
[Fact]
public void A12d_StopUiRefresh_StopsPumpAndPreventsRestart()
{
    var log = new LoggerConfiguration().CreateLogger();
    var vm = new MigrationSessionViewModel(null, null, log);
    vm.EnableUiThrottleForTest();
    var pump = new RecordingPump();
    vm.UiFlushPumpFactoryForTest = _ => pump;

    // 未开跑 ⇒ 不建泵（设计：构造时不建 timer，避免"没在跑却有个 timer 在滴答"）。
    Assert.False(vm.IsUiThrottleActive);

    vm.StopUiRefresh();                    // 对应 ShutdownAndExit 在 Environment.Exit 之前的那一行
    Assert.False(pump.IsRunning);
    // 关闭后即使有人再尝试开跑，也不得让节拍复活（StopUiRefresh 设了 _uiRefreshStopped）。
    Assert.False(vm.IsUiThrottleActive);
}

// ────────────────────────── L0：源码静态契约（A8–A11）──────────────────────────

    /// <summary>
    /// A8（改前会失败）：三个高频生产者**不得**再每行/每文件一次 <c>Post</c>；
    /// 必须走批量入队。这是 P1-5 的核心断言 —— 旧写法每行 3 个工作项（本方法 + Log + 文件事件）。
    /// </summary>
    [Fact]
    public void A8_HighFrequencyProducers_BatchInsteadOfPostPerItem()
    {
        var vm = ViewModelSource;

        var onOutput = BodyOf(vm, "private void OnOutputLine(string line)");
        Assert.DoesNotContain("Post(", onOutput, StringComparison.Ordinal);
        Assert.Contains("_batcher.EnqueueLog(", onOutput, StringComparison.Ordinal);

        var onFile = BodyOf(vm, "private void OnFileCopied(string path, long bytes)");
        Assert.DoesNotContain("Post(", onFile, StringComparison.Ordinal);
        Assert.Contains("_batcher.EnqueueFile(", onFile, StringComparison.Ordinal);

        // 进度快照走"最新值槽位"，不再每次 Post 一次 ApplySnapshot。
        var onSnap = BodyOf(vm, "private void OnSnapshotReported(ProgressSnapshot s)");
        Assert.Contains("_batcher.SetLatestSnapshot(", onSnap, StringComparison.Ordinal);
        Assert.DoesNotContain("Post(() => ApplySnapshot(", vm, StringComparison.Ordinal);
    }

    /// <summary>
    /// A9：立即通道存在，且**先 Flush 再应用**；<c>Log</c> 走立即通道 ⇒
    /// 约 30 处既有 Log 调用点一行不改即获得正确的先后顺序。
    /// </summary>
    [Fact]
    public void A9_ImmediateChannel_FlushesBeforeApplying()
    {
        var vm = ViewModelSource;

        var immediate = BodyOf(vm, "private void PostImmediate(Action apply)");
        var flushIdx = immediate.IndexOf("FlushPendingNow();", StringComparison.Ordinal);
        var applyIdx = immediate.IndexOf("apply()", StringComparison.Ordinal);
        Assert.True(flushIdx >= 0, "PostImmediate 必须先 FlushPendingNow()");
        Assert.True(applyIdx > flushIdx, "PostImmediate 里 FlushPendingNow() 必须出现在 apply() 之前");

        // FlushPendingNow 自身必须真的去 Drain（否则"先 Flush"只是空喊）。
        Assert.Contains("_batcher.Drain()", BodyOf(vm, "private void FlushPendingNow()"), StringComparison.Ordinal);

        // Log 的立即路径：先排空再应用，保证"批量引擎行在前、动作结论在后"。
        var logBody = BodyOf(vm, "private void Log(string level, string text)");
        Assert.Contains("PostImmediate(", logBody, StringComparison.Ordinal);

        // 明确的负断言：不允许出现"先应用、后排空"的颠倒写法。
        Assert.DoesNotContain("apply(); FlushPendingNow();", vm, StringComparison.Ordinal);
    }

    /// <summary>
    /// A9b：Drain 只在锁内做 O(1) 引用交换，**绝不持锁应用 UI**
    /// （锁内不得出现 ToArray / ObservableCollection 写入 —— 这是 R5"锁竞争拖慢传输"的防线）。
    /// </summary>
    [Fact]
    public void A9b_DrainSwapsReferencesUnderLock_AndMaterializesOutside()
    {
        var buf = ReadWinUi("Presentation", "UiBatchBuffer.cs");
        var body = BodyOf(buf, "public UiBatch<TSnapshot> Drain()");

        var lockStart = body.IndexOf("lock (_gate)", StringComparison.Ordinal);
        Assert.True(lockStart >= 0, "Drain 必须在 _gate 下做引用交换");
        var lockEnd = body.IndexOf("\n        }", lockStart, StringComparison.Ordinal);
        Assert.True(lockEnd > lockStart, "找不到 Drain 的锁块结束");
        var locked = body.Substring(lockStart, lockEnd - lockStart);

        Assert.DoesNotContain("ToArray()", locked);                    // 物化必须在锁外
        Assert.DoesNotContain("ObservableCollection", locked);
        Assert.Contains("_pendingFiles = new Queue<string>(", locked);  // 锁内只换引用
        Assert.Contains("_pendingLogs = new Queue<UiLogEntry>(", locked);

        // 物化确实在锁外发生（保证"生产者最多被阻塞几十纳秒"的承诺不是空话）。
        Assert.Contains("ToArray()", body.Substring(lockEnd), StringComparison.Ordinal);
    }

    /// <summary>
    /// A10（D2）：<c>CleanupRun</c> 必须停 timer；<c>ResetForJobSwitch</c> 必须先停+清缓冲再清集合。
    /// </summary>
    [Fact]
    public void A10_CleanupAndJobSwitch_StopTheTimer()
    {
        var vm = ViewModelSource;

        Assert.Contains("StopFlushTimer();", BodyOf(vm, "private void CleanupRun(IDisposable? session)"),
            StringComparison.Ordinal);

        var switchBody = BodyOf(vm, "private void ResetForJobSwitch()");
        var stopIdx = switchBody.IndexOf("StopFlushTimer();", StringComparison.Ordinal);
        var clearIdx = switchBody.IndexOf("_batcher.Clear();", StringComparison.Ordinal);
        var logClearIdx = switchBody.IndexOf("LogLines.Clear();", StringComparison.Ordinal);
        Assert.True(stopIdx >= 0 && clearIdx > stopIdx,
            "ResetForJobSwitch 必须**先停节拍再清缓冲**（否则在途 Tick 会把上一轮数据写进已清空的新任务界面）");
        Assert.True(logClearIdx > clearIdx, "缓冲清理必须早于集合清空");
    }

    /// <summary>
    /// A11（D2，防"C2 那种设计了却从不接线"）：<c>MainWindow.ShutdownAndExit</c> 必须调用
    /// <c>Session.StopUiRefresh()</c>，且**在 <c>Environment.Exit(0)</c> 之前**。
    /// </summary>
    [Fact]
    public void A11_ShutdownStopsUiRefreshBeforeEnvironmentExit()
    {
        var shell = ReadWinUi("MainWindow.xaml.cs");
        var body = BodyOf(shell, "private void ShutdownAndExit()");

        var stopIdx = body.IndexOf("Session.StopUiRefresh();", StringComparison.Ordinal);
        var exitIdx = body.IndexOf("Environment.Exit(0);", StringComparison.Ordinal);

        Assert.True(stopIdx >= 0, "ShutdownAndExit 必须调用 Session.StopUiRefresh()（D2：关闭前必须停 DispatcherQueueTimer）");
        Assert.True(exitIdx >= 0, "ShutdownAndExit 必须调用 Environment.Exit(0)");
        Assert.True(stopIdx < exitIdx,
            "Session.StopUiRefresh() 必须出现在 Environment.Exit(0) **之前**（否则复现关闭期崩溃族）");

        // StopUiRefresh 必须存在且幂等（转调 StopFlushTimer）。
        var vm = ViewModelSource;
        Assert.Contains("public void StopUiRefresh()", vm, StringComparison.Ordinal);
        Assert.Contains("StopFlushTimer();", BodyOf(vm, "public void StopUiRefresh()"), StringComparison.Ordinal);
    }

    /// <summary>A11b：节拍泵必须被**强引用**（实例字段），否则会被 GC 回收导致回调永不触发。</summary>
    [Fact]
    public void A11b_FlushPumpIsHeldByStrongInstanceField()
    {
        var vm = ViewModelSource;
        Assert.Contains("private IUiFlushPump? _flushPump;", vm, StringComparison.Ordinal);
        Assert.DoesNotContain("WeakReference<IUiFlushPump>", vm, StringComparison.Ordinal);

        // 真实泵也必须是实例字段强引用（项目已踩过 MotionDirector.cs:645-648 的坑）。
        var pump = ReadWinUi("Presentation", "UiFlushPump.cs");
        Assert.Contains("private DispatcherQueueTimer? _timer;", pump, StringComparison.Ordinal);
        Assert.Contains("timer.Tick +=", pump, StringComparison.Ordinal);
        Assert.Contains("s.Stop()", pump, StringComparison.Ordinal);   // 回调内自持 timer 引用
    }

    /// <summary>
    /// A11c：被**链入测试**的文件不得出现 WinUI 运行时类型（否则测试项目编译失败，
    /// 而那正是"必须被看见的信号"）。这里把它变成一条明确断言，避免有人"顺手加引用糊过去"。
    /// **只看代码，不看注释** —— 文件头注释为了解释分层原因会正当地提到这些名字。
    /// </summary>
    [Theory]
    [InlineData("UiBatchBuffer.cs")]
    [InlineData("UiFlushTrace.cs")]
    public void A11c_LinkedPresentationFiles_StayFreeOfWinUiRuntime(string fileName)
    {
        var code = StripComments(ReadWinUi("Presentation", fileName));
        Assert.DoesNotContain("DispatcherQueueTimer", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.UI.Xaml", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ObservableCollection", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.UI.Dispatching", code, StringComparison.Ordinal);
    }

    /// <summary>A11d：持 DispatcherQueueTimer 的文件**必须不**在链入清单里（分层边界的可执行断言）。</summary>
    [Fact]
    public void A11d_TimerOwningFiles_AreNotLinkedIntoTheTestProject()
    {
        var csproj = StripComments(File.ReadAllText(
            Path.Combine(FindRepoRoot(), "tests", "PCMig.Core.Tests", "PCMig.Core.Tests.csproj")));

        var linked = Regex.Matches(csproj, @"Compile\s+Include=""(?<p>[^""]+)""")
            .Select(m => m.Groups["p"].Value).ToList();

        Assert.Contains(linked, p => p.EndsWith("UiBatchBuffer.cs", StringComparison.Ordinal));
        Assert.Contains(linked, p => p.EndsWith("UiFlushTrace.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(linked, p => p.EndsWith("UiFlushPump.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(linked, p => p.EndsWith("UiFlushPumpFactory.cs", StringComparison.Ordinal));

        // 反向证据：真实泵确实存在于产品代码里（不是"文件被删了所以断言通过"）。
        Assert.Contains("DispatcherQueueTimer", ReadWinUi("Presentation", "UiFlushPump.cs"), StringComparison.Ordinal);
    }

    /// <summary>去掉 C# 注释（// 与 /* */），只留代码 —— 供"不得出现某类型名"的断言使用。</summary>
    private static string StripComments(string source)
    {
        var noBlock = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(noBlock, @"//[^\n]*", string.Empty);
    }

    // ────────────────────────── L0：D1 失败清单容量（诚实计数 + 方案 P）──────────────────────────

    /// <summary>
    /// D1-1：<c>AddFail</c> 必须有容量闸门与真实丢弃计数；<c>FailItemCapacity</c> 必须存在。
    /// 改前 <c>FailItems</c> / <c>_failIndex</c> **完全没有上限**（百万级失败即百万级条目 + 无界 HashSet）。
    /// </summary>
    [Fact]
    public void D1_AddFail_EnforcesCapacityAndCountsDropsTruly()
    {
        var vm = ViewModelSource;

        Assert.Contains("public const int FailItemCapacity", vm, StringComparison.Ordinal);
        Assert.Contains("FailIndexCapacity", vm, StringComparison.Ordinal);

        // ★ Preview.2（P1-4）★ 签名尾部新增可选 `string? fullText = null`（Step4「查看完整原因」的全文，
        //   由 UI 线程上的回执投影在后台生成后一并传入）——容量闸门与线程纪律的断言逐字未变。
        var body = BodyOf(vm, "private void AddFail(string title, string detail, bool objectLevel = false, bool verifyDerived = false, string? fullText = null)");
        Assert.Contains("FailItems.Count >= FailItemCapacity", body, StringComparison.Ordinal);
        Assert.Contains("_failDropped++", body, StringComparison.Ordinal);

        // ★ 关键口径 ★：索引达上限时**必须按"已显示过"返回**，不得继续累加丢弃计数 ——
        //   否则同一条失败会重复把计数刷爆，那才是"虚报"。
        var flattened = Regex.Replace(body, @"\s+", " ");
        Assert.Contains("if (_failIndex.Count >= FailIndexCapacity) return;", flattened, StringComparison.Ordinal);

        // 去重索引必须仍然生效（同一 key 不重复入列、也不重复计数）。
        Assert.True(body.IndexOf("_failIndex.Contains(key)", StringComparison.Ordinal)
                    < body.IndexOf("_failDropped++", StringComparison.Ordinal),
            "去重判定必须发生在丢弃计数之前（否则同一 key 会反复刷新计数）");
    }

    /// <summary>D1-2：丢弃提示走**方案 P**（零 XAML 改动）——作为真实一行插进既有日志容器。</summary>
    [Fact]
    public void D1_OmittedHint_IsARealLogLine_AndZeroXamlChange()
    {
        var vm = ViewModelSource;

        Assert.Contains("public const string OmittedHintPrefix = \"…另有 \"", vm, StringComparison.Ordinal);

        var body = BodyOf(vm, "private void UpdateOmittedHint(ref int shown, int actual, string unit, string tail)");
        Assert.Contains("LogLines.Add(line);", body, StringComparison.Ordinal);   // 方案 P：插进既有容器
        Assert.Contains("new SessionLogLine(", body, StringComparison.Ordinal);   // 复用既有行类型（零新绑定）

        // 改前会失败的负断言：不许把提示写成恒定/静态文案（那就是"假装"）。
        Assert.DoesNotContain("N 行", vm, StringComparison.Ordinal);

        // 清空失败清单时必须同步撤掉提示行，否则上一轮的 N 会冒充本轮事实。
        Assert.Contains("UpdateOmittedHint(ref _shownOmittedFails, 0,", BodyOf(vm, "private void ClearFailItems()"),
            StringComparison.Ordinal);
    }

    /// <summary>D1-3：容量与计数不许被"顺手删掉"——三个上下文（AddFail / ClearFailItems / 提示）必须都在。</summary>
    [Fact]
    public void D1_CapacityWiring_IsPresentInAllThreePlaces()
    {
        var vm = ViewModelSource;
        Assert.True(Regex.Matches(vm, @"_failDropped").Count >= 3,
            "_failDropped 至少要在 ① 声明 ② AddFail 累加 ③ ClearFailItems 归零 ④ 提示计算 里出现");
        Assert.Contains("_failDropped = 0;", BodyOf(vm, "private void ClearFailItems()"), StringComparison.Ordinal);
        Assert.Contains("_failDropped,", BodyOf(vm, "private void EnsureOmittedHints(int droppedFiles, int droppedLogs)"),
            StringComparison.Ordinal);

        // ★ 线程纪律 ★ AddFail 可能从引擎线程调用 ⇒ 它**不得**直接刷新提示行（那会跨线程改 LogLines）。
        //   只看代码、不看注释（注释里为了说明原因会正当地提到 LogLines）。
        var addFailCode = StripComments(BodyOf(vm, "private void AddFail(string title, string detail, bool objectLevel = false, bool verifyDerived = false, string? fullText = null)"));
        Assert.DoesNotContain("EnsureOmittedHints(", addFailCode);
        Assert.DoesNotContain("LogLines", addFailCode);

        // 提示行的刷新点只有 UI 线程两处：批量应用 与 复位。
        Assert.Contains("EnsureOmittedHints(", BodyOf(vm, "private void ApplyBatch(UiBatch<ProgressSnapshot> batch)"),
            StringComparison.Ordinal);
    }

    // ────────────────────────── 夹具 ──────────────────────────

    private sealed record Snapshot(int Value);

    /// <summary>
    /// 「节流**开启**」的会话夹具：用测试缝强制节流可用 + 注入手动假泵，
    /// 从而在没有 UI 队列（替身恒 null）的环境里**真的走批量分支**。
    /// 临时作业目录同样隔离（JobManager.JobsRoot 读 PCMIG_JOBS）。
    /// </summary>
    private sealed class ThrottledSession : IAsyncDisposable
    {
        private readonly string _sandbox;
        private readonly string? _previousJobs;

        public MigrationSessionViewModel Session { get; }
        public RecordingPump Pump { get; }

        private ThrottledSession(string sandbox, string? previousJobs, MigrationSessionViewModel session, RecordingPump pump)
        {
            _sandbox = sandbox;
            _previousJobs = previousJobs;
            Session = session;
            Pump = pump;
        }

        /// <summary>建会话、隔离作业目录、注入假泵并强制节流可用；<paramref name="configure"/> 里设 TransferRunner。</summary>
        public static async System.Threading.Tasks.Task<ThrottledSession> CreateAsync(Action<MigrationSessionViewModel> configure)
        {
            var sandbox = Path.Combine(Path.GetTempPath(), "pcmig-a5-p15", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandbox);
            var previousJobs = Environment.GetEnvironmentVariable("PCMIG_JOBS");
            Environment.SetEnvironmentVariable("PCMIG_JOBS", Path.Combine(sandbox, "Jobs"));

            var log = new LoggerConfiguration().CreateLogger();
            var vm = new MigrationSessionViewModel(null, null, log);
            var factory = new ManualPumpFactory();
            vm.UiFlushPumpFactoryForTest = factory.Create;
            vm.EnableUiThrottleForTest();
            configure(vm);

            var manager = new JobManager(log);
            var ctx = manager.Create(new JobDefinition
            {
                JobId = "JOB-A5-P15T",
                SourceHost = "P15-PROBE",
                TargetRoot = Path.Combine(sandbox, "target"),
                Sources = { new SourceSpec { Path = @"\\P15-PROBE\C$", Kind = ObjectKind.DataVolume } },
                Options = new MigrationOptions { Threads = 4 },
            });
            ctx.SavePlan(PlanWithOneObject(@"\\P15-PROBE\C$"));

            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                if (!await vm.AdoptExistingJobAsync(ctx.JobDir))
                    throw new InvalidOperationException("夹具失败：无法载入测试任务");
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }

            return new ThrottledSession(sandbox, previousJobs, vm, factory.Pump);
        }

        /// <summary>在"无线程上下文的同步上下文"里跑一次迁移（避免自锁，与既有回归测试同口径）。</summary>
        public async System.Threading.Tasks.Task RunAsync()
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
            try { await Session.RunAsync(password: null); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }

        public ValueTask DisposeAsync()
        {
            try { Session.StopUiRefresh(); } catch { /* 清理失败不影响结论 */ }
            Environment.SetEnvironmentVariable("PCMIG_JOBS", _previousJobs);
            try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); }
            catch { /* 同上 */ }
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// ★ C-C08-1 回归（真机实证，2026-10-04）★ 同一会话内「停止 → 立即恢复」之后，
    /// UI 节拍泵必须**重新活起来**。
    ///
    /// 真机事实（C08 reg2 与 C08DIAG diag2，目标盘已清空的干净复现）：Stop 后立刻点「恢复任务」，
    /// 独立证据显示引擎确实恢复写盘（job-state running，已传 19.59 GB = 40.1%、按钮回到
    /// 暂停✓停止✓），但底栏在 ≥54 s 内一直显示「0.0% / 0 B / 48.89 GB」，直到下一次点「停止」
    /// 触发一次显式 FlushPendingNow 才跳到真实值 —— UI 与真实引擎真值分裂（用户会以为恢复没生效）。
    ///
    /// 根因（本用例锁死）：生产泵 <c>DispatcherQueueUiFlushPump.Stop()</c> 是**终态**
    /// （UiFlushPump.cs:75-108：`_stopped=true` 之后 `Start()` 直接 return），而 VM 侧的
    /// `_flushPump` 是 `??=` 复用的。旧版 StopFlushTimer 只调 `pump.Stop()` 却把死泵留在字段里
    /// ⇒ run-2 的 StartFlushTimer 复用同一个死泵 ⇒ 快照只进批处理最新值槽位、**再没有节拍来排空**。
    ///
    /// 为什么必须用 <see cref="TerminalStopPump"/> 而不是既有的 RecordingPump：RecordingPump 的
    /// Start 可以无限复活，用它写这条用例**在修复前也会通过**（假通过）。这里严格照抄生产泵语义。
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task C08_StopThenImmediateResume_MustRearmThrottledPump()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "pcmig-c08-fix", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        var previousJobs = Environment.GetEnvironmentVariable("PCMIG_JOBS");
        Environment.SetEnvironmentVariable("PCMIG_JOBS", Path.Combine(sandbox, "Jobs"));

        var log = new LoggerConfiguration().CreateLogger();
        var vm = new MigrationSessionViewModel(null, null, log);
        vm.EnableUiThrottleForTest();
        var factory = new TerminalStopPumpFactory();
        vm.UiFlushPumpFactoryForTest = factory.Create;

        var gate1 = new System.Threading.Tasks.TaskCompletionSource<bool>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        var gate2 = new System.Threading.Tasks.TaskCompletionSource<bool>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

        var previous = SynchronizationContext.Current;
        try
        {
            var manager = new JobManager(log);
            var ctx = manager.Create(new JobDefinition
            {
                JobId = "JOB-C08-FIX",
                SourceHost = "P15-PROBE",
                TargetRoot = Path.Combine(sandbox, "target"),
                Sources = { new SourceSpec { Path = @"\\P15-PROBE\C$", Kind = ObjectKind.DataVolume } },
                Options = new MigrationOptions { Threads = 4 },
            });
            ctx.SavePlan(PlanWithOneObject(@"\\P15-PROBE\C$"));

            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir), "必须先载入任务");
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }

            // ── run-1：跑起来（此时必须有一个活着的节拍泵），用户点「停止」 ──
            vm.TransferRunner = async (c, hooks, _, ct, _, _) =>
            {
                hooks.OutputLine?.Invoke("引擎：第一段运行");
                await gate1.Task;
                ct.ThrowIfCancellationRequested();   // 与真机同口径：停止 ⇒ 取消 ⇒ Interrupted 收尾
                return JobPhase.Completed;
            };

            SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
            var run1 = vm.RunAsync(password: null);

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!vm.IsRunning && DateTime.UtcNow < deadline) Thread.Sleep(10);
            Assert.True(vm.IsRunning, "run-1 未在 10 秒内进入运行态");
            Assert.True(vm.IsUiThrottleActive, "run-1 运行中必须有活着的节拍泵");
            var pump1 = Assert.IsType<TerminalStopPump>(factory.Last);

            await vm.StopAsync();
            gate1.TrySetResult(true);
            await run1;

            Assert.Equal(JobPhase.Interrupted, vm.Phase);
            Assert.False(pump1.IsRunning, "收尾后 run-1 的泵必须已经停");

            // ── run-2：同一会话内立即「恢复任务」（C08 的真实用户序列） ──
            vm.TransferRunner = async (c, hooks, _, _, _, _) =>
            {
                hooks.OutputLine?.Invoke("引擎：第二段运行");
                await gate2.Task;
                return JobPhase.Completed;
            };

            var run2 = vm.ResumeAsync(password: null);
            deadline = DateTime.UtcNow.AddSeconds(10);
            while (!vm.IsRunning && DateTime.UtcNow < deadline) Thread.Sleep(10);
            Assert.True(vm.IsRunning, $"run-2（恢复）未在 10 秒内进入运行态；StatusMessage=[{vm.StatusMessage}]");

            // ★★ 本用例的核心断言 ★★ 恢复后的运行必须重新拥有一个**在跑的**节拍泵；
            //    否则进度快照只进批处理槽位，底栏永远停在旧值（真机：0.0% / 0 B 冻结 54 s）。
            Assert.True(vm.IsUiThrottleActive,
                "同会话恢复之后节拍泵必须重新启动（否则底栏进度冻结 = 真值分裂）");
            Assert.NotSame(pump1, factory.Last);
            Assert.True(factory.Last!.IsRunning, "恢复运行必须使用重新建立的、正在跑的泵");

            gate2.TrySetResult(true);
            await run2;
            Assert.Equal(JobPhase.Completed, vm.Phase);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            try { vm.StopUiRefresh(); } catch { /* 清理失败不影响结论 */ }
            Environment.SetEnvironmentVariable("PCMIG_JOBS", previousJobs);
            try { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true); }
            catch { /* 同上 */ }
        }
    }

    /// <summary>
    /// 严格照抄生产泵终态语义的替身：<c>Stop()</c> 之后 <c>Start()</c> 是空操作、IsRunning 保持 false
    /// （依据 <c>Presentation\UiFlushPump.cs:75-108</c> 的 `_stopped` 终态闸门）。
    /// </summary>
    private sealed class TerminalStopPump : IUiFlushPump
    {
        private bool _stopped;

        public bool IsRunning { get; private set; }
        public TimeSpan Interval => TimeSpan.FromMilliseconds(50);

        public void Start()
        {
            if (_stopped) return;   // ★ 生产语义：停过就再也不复活（这正是 C-C08-1 的引信）★
            IsRunning = true;
        }

        public void Stop()
        {
            _stopped = true;
            IsRunning = false;
        }
    }

    /// <summary>每次 Create 都建**新**泵并记录全部实例（用于证明"恢复运行确实换了一个新泵"）。</summary>
    private sealed class TerminalStopPumpFactory
    {
        public List<TerminalStopPump> Created { get; } = new();

        public TerminalStopPump? Last => Created.Count == 0 ? null : Created[^1];

        public IUiFlushPump Create(Func<bool> onTick)
        {
            var pump = new TerminalStopPump();
            Created.Add(pump);
            return pump;
        }
    }

    /// <summary>可手动滴答的假泵（记录 Start/Stop 次数，用于 D2 生命周期契约）。</summary>
    private sealed class RecordingPump : IUiFlushPump
    {
        private Func<bool>? _tick;

        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public bool IsRunning { get; private set; }
        public TimeSpan Interval => TimeSpan.FromMilliseconds(50);

        public void Attach(Func<bool> onTick) => _tick = onTick;

        public void Start() { StartCount++; IsRunning = true; }

        public void Stop() { StopCount++; IsRunning = false; }

        /// <summary>手动驱动一次节拍（等价于 DispatcherQueueTimer 的一次 Tick）。</summary>
        public bool RunTick() => _tick?.Invoke() ?? false;
    }

    /// <summary>Post 立即在当前线程执行（让引擎线程上报的续体确定性地同步生效）。</summary>
    private sealed class InlineSyncContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    /// <summary>记录"是否被要求建立节拍泵"的假工厂（A7：直通路径下必须为 false）。</summary>
    private sealed class RecordingPumpFactory
    {
        public bool AnyCreated { get; private set; }

        public IUiFlushPump Create(Func<bool> onTick)
        {
            AnyCreated = true;
            return new NoopPump();
        }
    }

    /// <summary>可手动滴答的假泵工厂（记录 Start/Stop 次数）。</summary>
    private sealed class ManualPumpFactory
    {
        public RecordingPump Pump { get; } = new();

        public IUiFlushPump Create(Func<bool> onTick)
        {
            Pump.Attach(onTick);
            return Pump;
        }
    }

    /// <summary>不滴答的假泵（只用于观测"是否走到生产分支"）。</summary>
    private sealed class NoopPump : IUiFlushPump
    {
        public bool IsRunning { get; private set; }
        public TimeSpan Interval => TimeSpan.FromMilliseconds(50);
        public void Start() => IsRunning = true;
        public void Stop() => IsRunning = false;
    }

    private static MigrationPlan PlanWithOneObject(string sourcePath, long bytes = 1024)
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
}