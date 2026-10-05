// ============================================================================
//  A.5 回归测试：WinUI 侧 Presentation 逻辑的**行为级**覆盖（用户第 8 条）
// ============================================================================
//
//  ── 一、测试策略：为什么能"够得着" WinUI 项目里的类型 ──────────────────────
//
//  待测类型（DirNode / FileRow / DirectoryTreeViewModel / MigrationSessionViewModel /
//  PageReadiness）都在 `src\PCMig.WinUI`（WinUI 3 应用项目，
//  TFM = net8.0-windows10.0.19041.0 + UseWinUI=true + WindowsAppSDKSelfContained）。
//  测试项目 `tests\PCMig.Core.Tests` 只引用 `PCMig.Core`，**不能** project-reference
//  PCMig.WinUI：那会把 Windows App SDK 拖进单测宿主，并且会与"正在独占构建
//  src\PCMig.WinUI 的进程"抢 obj/bin 文件锁。
//
//  → 采用的方案：**源码链入（Compile Include）** + **测试专用 DispatcherQueue 替身**
//
//    1) 把下列 7 个「不依赖 XAML 运行时」的 Presentation 源码文件链进本测试项目编译
//       （见 PCMig.Core.Tests.csproj 的 A5LinkedPresentationSources 项）：
//         · Presentation\ObservableObject.cs          （INPC 薄基类）
//         · Presentation\ShareItem.cs                 （共享行模型）
//         · Presentation\DirTreeModels.cs             （DirNode / FileRow 三态模型）
//         · Presentation\DirectoryTreeViewModel.cs    （懒加载 + 三态 + 选择汇总）
//         · Presentation\ConnectionViewModel.cs       （Step1 连接状态源）
//         · Presentation\PageReadiness.cs             （页面就绪文案薄适配层）
//         · Presentation\MigrationSessionViewModel.cs （会话唯一状态源）
//      逐文件核对了 using 与类型闭包：这 7 个文件只 using System.* / PCMig.Core.*；
//      全项目 grep 确认只有三个文件使用 Microsoft.UI.Dispatching（本清单里的两个，
//      外加未链入的 MotionState.cs），其余 WinUI 文件（Views\*.xaml.cs、Converters、
//      MotionDirector、UniformScaleHost…）一律未链入。
//
//    2) 唯一的 WinUI 类型依赖 DispatcherQueue 由 tests\TestOnlyDispatcherQueueShim.cs
//       提供替身，其 GetForCurrentThread() **恒返回 null** —— 这不是"糊一个假实现"，
//       而是让测试走产品源码里**真实存在**的分支：
//         · DirectoryTreeViewModel ctor:  _queue = queue ?? DispatcherQueue.GetForCurrentThread();
//         · DirectoryTreeViewModel.OnUiAsync: if (_queue is null) { action(); return ...; }
//         · MigrationSessionViewModel.Post:   if (_queue is null || _queue.HasThreadAccess) { action(); return; }
//       真实 WinUI 中"当前线程没有 dispatcher"（例如在非 UI 线程上构造 VM）就返回 null，
//       语义与测试环境一致 ⇒ 断言到的是产品源码，不是替身行为。
//
//    3) 既有的 WinUiStep1ContractTests / WinUiDpiContractTests 那种"只扫源码文本"的
//       契约级测试**保留不动**；本文件只在"确实无法实例化"时才补契约级断言，
//       并在方法名与注释里显式标注 `[契约级]`。
//
//  ── 二、行为级 / 契约级划分（逐条对应用户要求的 8 条）──────────────────────
//
//   | # | 用户要求                                    | 本文件覆盖                     | 级别 |
//   |---|---|---|---|
//   | 1 | 根半选后 RootNodes 不消失                   | A5_1a / A5_1b / A5_1c          | 行为级 |
//   | 2 | 子目录选择生成的 selections 与预期一致       | A5_2a … A5_2f                  | 行为级（真实读盘） |
//   | 3 | 全选 / 半选 / 全不选三态语义                | A5_3a … A5_3d                  | 行为级 |
//   | 4 | 旧任务 Threads=32/64/128 恢复后不被改写     | A5_4a … A5_4e                  | 行为级（真实 job.json + SHA256） |
//   | 5 | CompletedWithErrors 不被强制 100%           | A5_5a … A5_5e                  | 行为级 |
//   | 6 | 新 Job / 换 Job / Repair 清理旧状态         | A5_6a（Repair / _failIndex）    | 行为级 |
//   |   |                                             | A5_6b、A5_6c（换 Job / 新 Job） | 行为级（已核对源码：ResetForJobSwitch 已接在换任务/新建任务上） |
//   | 7 | unfinished 探测不被旧异步结果反向覆盖       | A5_7a / A5_7b / A5_7c          | 行为级·**按契约写·当前源码可能未实现** |
//   | 8 | TreeView 未加载根有正确的"可展开"状态       | A5_8a / A5_8b / A5_8c          | 行为级 |
//   |   | （UI 侧占位判据只有页面 code-behind 能表达） | A5_8d                          | **契约级** |
//
//  ── 三、确定性纪律（为什么这些测试不靠"时序侥幸"）──────────────────────────
//   · `Progress<ProgressSnapshot>` 会捕获构造点的 SynchronizationContext：本文件为需要它
//     同步生效的测试装了 InlineSyncContext（Post 立即执行），从而
//     `progress.Report(...)` → ApplySnapshot 在同一次调用里完成，断言无竞态；
//   · 竞态类用例（A5_7a / A5_7b，P2-6 的回归）**一律用产品自带的测试缝做确定性构造**：
//     ① `UnfinishedProbeDebouncePumpFactoryForTest` 注入假去抖泵 ⇒ "计时器到期"变成测试的
//        一个显式动作（`pump.Fire()`），不需要等待 400ms 真实时间；
//     ② `UnfinishedProbeForTest` 注入可控探测实现 ⇒ 第一次探测可以被**确定性地挂起**
//        （ManualResetEventSlim），第二次正常跑完，随后放行第一次；
//     ③ `LastProbeTaskForTest`（只读诊断）给出"这一次探测确实已经结束"的**确定完成信号**。
//     ⇒ 全程无 `Thread.Sleep` 轮询、无自定义 SynchronizationContext 扣续体、无 `AsyncLocal`
//        标记、无"退化路径"——这些正是改前 A5_7a/7b 抖动（同一份代码 181/0 与 179/2）的来源，
//        已随重写**全部拆除**；
//   · 全部作业目录走环境变量 PCMIG_JOBS 指向测试临时目录（现有 130 条测试无人使用
//     JobManager / PCMIG_JOBS，已 grep 核实），**绝不触碰 %ProgramData% 里的真实任务存档**；
//   · 传输引擎一律通过产品自带的**测试缝** `MigrationSessionViewModel.TransferRunner`
//     注入假实现 ⇒ 测试进程里**永远不会真的启动 robocopy**，不需要网络与 SMB 共享；
//    · 需要"同步等待异步方法"的地方统一走 AwaitNoContext（先摘掉当前同步上下文再阻塞），
//     不会与任何单测宿主自带的上下文互相锁死；
//    · 连接态（`ConnectionViewModel.IsConnected` 的 setter 是私有的、只有真实 SMB 预检成功才置 true）
//      由**测试缝** `ForceConnectedForTest` 注入 ⇒ 「已连接 ⇒ 允许探测 IO」这条主路径可被行为级覆盖。
//
//  ── 四、不得违反的约束 ────────────────────────────────────────────────────
//   · 本文件不修改 `src\` 下任何产品代码，也不减少/改写既有 130 条测试的断言；
//   · 断言失败就是失败：**没有**任何"环境不对就跳过/把断言变弱"的逃生门
//     （改前 A5_7a/7b 的那条"退化路径 + 基础设施异常"已随重写删除）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using PCMig.Core.Jobs;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using PCMig.WinUI.Presentation;
using Serilog;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// A.5 正确性修正的 Presentation 层回归测试（详见文件头：策略、链入清单、行为级/契约级划分）。
///
/// ★ PCs 隔离（A.5 收尾新增，堵住一条真实的跨类竞态）★
/// `A5P15UiThrottleTests` 与本类都会临时改写**进程级**环境变量 `PCMIG_JOBS`
/// （`JobManager.JobsRoot` 每次都读它）来隔离作业目录；xUnit 默认让不同测试类**并行**运行
/// ⇒ 两个类同时跑时会把对方的作业根换掉，表现为"真实读盘突然找不到任何任务"的**间歇性失败**
/// （本包重写 A5_7a/7b 时实测到：单跑绿、全量跑偶红）。
/// 因此把两个类放进同一个 xUnit collection（同一 collection 内的类串行执行），
/// 让"作业目录隔离"这件事真正生效 —— 这是测试基建层的确定性修正，不改任何产品行为。
/// </summary>
[Collection("A5-jobdir-isolation")]
public sealed class A5PresentationRegressionTests : IDisposable
{
    // ────────────────────────── 夹具 ──────────────────────────

    private const string ProbeHost = "A5-PROBE-HOST";

    /// <summary>竞态用例里那台"没有任何任务"的旧电脑（它的探测结论注定会被后来的覆盖）。</summary>
    private const string RaceStaleHost = "A5-RACE-STALE-HOST";
    private readonly string _sandbox;
    private readonly ILogger _log;              // 无 sink 的静默 logger：不写 %ProgramData% 日志
    private readonly string? _previousJobsRoot;
    private ConnectionViewModel? _connection;
    private DirectoryTreeViewModel? _selectionTree;
    private DirNode? _selectionRoot;

    public A5PresentationRegressionTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "pcmig-a5-regression", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);

        // 作业目录隔离：JobManager.JobsRoot 每次都读该环境变量（Core\Jobs\JobManager.cs:140-142）
        _previousJobsRoot = Environment.GetEnvironmentVariable("PCMIG_JOBS");
        Environment.SetEnvironmentVariable("PCMIG_JOBS", Path.Combine(_sandbox, "Jobs"));

        _log = new LoggerConfiguration().CreateLogger();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PCMIG_JOBS", _previousJobsRoot);
        _connection?.Dispose();
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    /// <summary>建目录并返回**不带尾部反斜杠**的绝对路径（SyncRoots 的路径比较是逐字符的）。</summary>
    private string NewDir(params string[] parts)
    {
        var path = Path.Combine(new[] { _sandbox }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void WriteFile(string path, string content) => File.WriteAllText(path, content);

    private DirectoryTreeViewModel NewTree() => new(null, _log);

    /// <summary>
    /// 同步等待一个异步方法（仅测试用）：先把当前 SynchronizationContext 摘掉，
    /// 保证续体不会被投递回"正被本线程阻塞等待"的那个上下文（避免任何形式的自锁）。
    /// 必须在<b>调用之前</b>摘掉 —— await 点捕获的是当时的 Current。
    /// </summary>
    private static void AwaitNoContext(Func<Task> factory)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { factory().GetAwaiter().GetResult(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private MigrationSessionViewModel NewSession(ConnectionViewModel? connection = null)
        => new(connection, null, _log);

    /// <summary>建一个 Step1 连接状态源，带若干共享行（不触网：只填模型）。</summary>
    private ConnectionViewModel NewConnection(params (string Name, string Unc, bool Selected)[] shares)
    {
        var conn = new ConnectionViewModel { Host = ProbeHost };
        foreach (var (name, unc, selected) in shares)
            conn.Shares.Add(new ShareItem { Name = name, UncPath = unc, Kind = "管理共享", IsSelected = selected });
        _connection = conn;
        return conn;
    }

    private JobContext CreateJob(
        string jobId,
        int threads = 16,
        string? sourceHost = null,
        MigrationPlan? plan = null,
        JobState? state = null,
        IEnumerable<ObjectReceipt>? receipts = null)
    {
        var def = new JobDefinition
        {
            JobId = jobId,
            SourceHost = sourceHost ?? ProbeHost,
            TargetRoot = NewDir("target", jobId),
            Sources = { new SourceSpec { Path = @"\\" + ProbeHost + @"\C$", Kind = ObjectKind.DataVolume } },
            Options = new MigrationOptions { Threads = threads },
        };
        var ctx = new JobManager(_log).Create(def);
        if (plan is not null) ctx.SavePlan(plan);
        if (state is not null) ctx.SaveState(state);
        if (receipts is not null)
            foreach (var r in receipts) ctx.SaveReceipt(r);
        return ctx;
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

    private static string Sha256Of(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // ────────────────────────── 假传输引擎（测试缝） ──────────────────────────

    /// <summary>
    /// 注入到 <see cref="MigrationSessionViewModel.TransferRunner"/> 的假引擎：
    /// 用产品自己给出的 <see cref="TransferEngineHooks"/> 真实触发"引擎输出 / 每文件复制"两条投影，
    /// 并在**运行中的那一刻**采样会话状态（这才是"清理是否真的发生过"的确定观测点）。
    /// 它不会启动 robocopy、不碰网络。
    /// </summary>
    private sealed class FakeEngine
    {
        private readonly MigrationSessionViewModel _vm;
        public FakeEngine(MigrationSessionViewModel vm) => _vm = vm;

        /// <summary>含"错误"二字 ⇒ OnOutputLine 会把它计入失败清单（与真实引擎同一条路径）。</summary>
        public string ErrorLine { get; init; } = "错误：A5 故障注入（每次运行使用完全相同的一行）";

        /// <summary>每次被调用时触发几次"文件已复制"事件。</summary>
        public int FileCopiedEvents { get; init; }

        /// <summary>非 null 时通过 progress 上报一次快照（走 ApplySnapshot 路径）。</summary>
        public ProgressSnapshot? Snapshot { get; init; }

        /// <summary>假引擎返回的结束阶段。</summary>
        public JobPhase Result { get; init; } = JobPhase.Completed;

        // ── 采样 ──
        public List<int> ErrorLineCounts { get; } = new();
        public List<bool> StaleObjectFailVisible { get; } = new();
        public List<int> LiveFileCounts { get; } = new();
        public List<bool> RepairingFlags { get; } = new();
        public List<int> ThreadsSeen { get; } = new();
        public List<double> PercentRightAfterSnapshot { get; } = new();
        public List<JobPhase> PhaseRightAfterSnapshot { get; } = new();
        public List<string> StatusRightAfterSnapshot { get; } = new();
        public List<bool> FinishedRightAfterSnapshot { get; } = new();

        public Task<JobPhase> Run(
            JobContext ctx,
            TransferEngineHooks hooks,
            IProgress<ProgressSnapshot> progress,
            CancellationToken ct,
            IReadOnlyCollection<string>? onlyObjectIds,
            bool forceRecopy)
        {
            ThreadsSeen.Add(ctx.Definition.Options.Threads);

            for (var i = 0; i < FileCopiedEvents; i++)
                hooks.FileCopied?.Invoke(Path.Combine(ctx.JobDir, $"a5-probe-{i}.bin"), 4096);

            // 同一行发两次：AddFail 的去重索引（_failIndex）应当只留一条
            hooks.OutputLine?.Invoke(ErrorLine);
            hooks.OutputLine?.Invoke(ErrorLine);

            ErrorLineCounts.Add(_vm.FailItems.Count(f => f.Title == "引擎输出" && f.Detail == ErrorLine));
            StaleObjectFailVisible.Add(_vm.FailItems.Any(f => f.Title.StartsWith("✘ 对象失败", StringComparison.Ordinal)));
            LiveFileCounts.Add(_vm.LiveFiles.Count);
            RepairingFlags.Add(_vm.IsRepairing);

            if (Snapshot is not null)
            {
                progress.Report(Snapshot);
                PercentRightAfterSnapshot.Add(_vm.Percent);
                PhaseRightAfterSnapshot.Add(_vm.Phase);
                StatusRightAfterSnapshot.Add(_vm.StatusMessage);
                FinishedRightAfterSnapshot.Add(_vm.IsFinished);
            }

            return Task.FromResult(Result);
        }

        public TransferRunnerDelegate Delegate => Run;
    }

    // ────────────────────────── 同步上下文 ──────────────────────────

    /// <summary>Post 立即在当前线程执行 —— 让 Progress&lt;T&gt; 的回调确定性地同步生效。</summary>
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

    /// <summary>
    /// 假去抖泵（**仅本文件使用**，P2-6）：把"计时器到期"变成测试的一个**显式动作**
    /// （<see cref="Fire"/>），于是"输入风暴只发起一次探测""未连接不做 IO""跳变立即探测"
    /// 这类契约可以完全确定性地断言 —— 不需要等待 400ms 真实时间、也没有任何 sleep 轮询。
    ///
    /// 它实现的是产品里那个接口（<see cref="IUnfinishedProbeDebouncePump"/>），
    /// 因此产品侧走的仍是真实分支（`_probeDebouncePumpFactory` 非 null ⇒ 用它），
    /// 只是"时间"由测试掌握。
    /// </summary>
    private sealed class FakeProbeDebouncePump : IUnfinishedProbeDebouncePump
    {
        public Action? OnElapsed { get; set; }
        public int ArmCount { get; private set; }
        public int DisarmCount { get; private set; }
        public bool StopCalled { get; private set; }
        private bool _armed;

        public bool IsArmed => _armed && !StopCalled;

        public void Arm() { ArmCount++; _armed = true; }
        public void Disarm() { DisarmCount++; _armed = false; }
        public void Stop() { StopCalled = true; _armed = false; }

        /// <summary>放行"去抖窗口到期"（= 真实计时器到点回调产品代码）。</summary>
        public void Fire()
        {
            _armed = false;
            OnElapsed?.Invoke();
        }

        public void ResetCounts() { ArmCount = 0; DisarmCount = 0; }
    }

    /// <summary>把假去抖泵装进会话（产品侧只在第一次 <c>Arm</c> 时惰性取泵，故这里预建实例）。</summary>
    private FakeProbeDebouncePump InstallFakeProbeDebouncePump(MigrationSessionViewModel vm)
    {
        var pump = new FakeProbeDebouncePump();
        vm.UnfinishedProbeDebouncePumpFactoryForTest = onElapsed =>
        {
            pump.OnElapsed = onElapsed;
            return pump;
        };
        return pump;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  要求 1：根半选后 RootNodes 不消失（P0-2 的反馈回路）
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 行为级：完整复现 P0-2 回路 ——
    /// 用户取消共享根下一个子目录 ⇒ 根变半选 ⇒ 单向联动把共享行 IsSelected 置 false
    /// ⇒ 页面回调 SyncTreeRootsFromShares 重建根。根**必须还在**，且半选与精确选择都不得被抹掉。
    /// （依据：DirectoryTreeViewModel.SyncRoots(ShareRootSpec) 的删除判据是"UNC 在不在共享列表里"，
    ///   MigrationSessionViewModel.SyncTreeRootsFromShares 传的是全部共享。）
    /// </summary>
    [Fact]
    public void A5_1a_HalfCheckedShareRoot_IsNotRemovedBySharedRowFeedbackLoop()
    {
        var conn = NewConnection(
            ("C$", @"\\" + ProbeHost + @"\C$", true),
            ("D$", @"\\" + ProbeHost + @"\D$", true));
        var vm = NewSession(conn);
        vm.SyncTreeRootsFromShares();

        Assert.Equal(2, vm.Tree.RootNodes.Count);
        var rootC = vm.Tree.RootNodes.Single(r => r.FullPath == @"\\" + ProbeHost + @"\C$");
        Assert.True(rootC.IsChecked); // 共享行 IsSelected=true ⇒ 新根以"全勾"出现

        // 造出"已加载 + 两个子目录"的形态（这里验的是根的存续语义，不是懒加载；懒加载见 A5_2*）
        rootC.ChildrenLoaded = true;
        rootC.Children.Clear();
        var child1 = new DirNode { Name = "A5One", FullPath = rootC.FullPath + @"\A5One", Parent = rootC };
        var child2 = new DirNode { Name = "A5Two", FullPath = rootC.FullPath + @"\A5Two", Parent = rootC };
        child1.SetCheckedSilent(true);
        child2.SetCheckedSilent(true);
        rootC.Children.Add(child1);
        rootC.Children.Add(child2);

        // ① 用户点掉一个子目录 ⇒ ② 根变半选 ⇒ ③ 单向联动写回共享行
        child1.SetChecked(false);
        Assert.Null(rootC.IsChecked);
        Assert.False(conn.Shares.Single(s => s.Name == "C$").IsSelected);

        // ④⑤ 页面收到共享行变化后立刻重建根（这就是 P0-2 成环的那条边）
        vm.SyncTreeRootsFromShares();

        Assert.Equal(2, vm.Tree.RootNodes.Count);
        Assert.Same(rootC, vm.Tree.RootNodes.Single(r => r.FullPath == rootC.FullPath)); // 根没被删、也没被重建
        Assert.Null(rootC.IsChecked);              // 半选被保留（"取消全选"本身是正确行为）
        Assert.False(child1.IsChecked);            // 精确选择被保留
        Assert.True(child2.IsChecked);
    }

    /// <summary>行为级：同一个根被反复"同步 + 精确勾选"，根实例必须始终是同一个（不得被删后重建）。</summary>
    [Fact]
    public void A5_1b_RepeatedPartialToggles_NeverRecreateOrDropTheRoot()
    {
        var conn = NewConnection(("C$", @"\\" + ProbeHost + @"\C$", true));
        var vm = NewSession(conn);
        vm.SyncTreeRootsFromShares();
        var root = vm.Tree.RootNodes.Single();

        root.ChildrenLoaded = true;
        root.Children.Clear();
        var a = new DirNode { Name = "A", FullPath = root.FullPath + @"\A", Parent = root };
        var b = new DirNode { Name = "B", FullPath = root.FullPath + @"\B", Parent = root };
        a.SetCheckedSilent(true);
        b.SetCheckedSilent(true);
        root.Children.Add(a);
        root.Children.Add(b);

        for (var round = 0; round < 4; round++)
        {
            a.SetChecked(false);                 // 取消一个子目录 ⇒ 半选
            vm.SyncTreeRootsFromShares();        // 页面的重建边
            Assert.Single(vm.Tree.RootNodes);
            Assert.Same(root, vm.Tree.RootNodes[0]);
            Assert.Null(root.IsChecked);

            a.SetChecked(true);                  // 再勾回来 ⇒ 回到全选
            vm.SyncTreeRootsFromShares();
            Assert.Single(vm.Tree.RootNodes);
            Assert.Same(root, vm.Tree.RootNodes[0]);
            Assert.True(root.IsChecked);
        }
    }

    /// <summary>
    /// 行为级（反向对照）：根被删除的**唯一**判据是"该 UNC 已不在共享列表里"，不是 IsSelected=false。
    /// 没有这条反向断言，"永不删根"的实现也能骗过 A5_1a / A5_1b。
    /// </summary>
    [Fact]
    public void A5_1c_RootIsRemovedOnlyWhenShareLeavesTheShareList()
    {
        var conn = NewConnection(
            ("C$", @"\\" + ProbeHost + @"\C$", true),
            ("D$", @"\\" + ProbeHost + @"\D$", true));
        var vm = NewSession(conn);
        vm.SyncTreeRootsFromShares();
        Assert.Equal(2, vm.Tree.RootNodes.Count);

        // ① 共享行取消勾选但仍在共享列表里 ⇒ 根必须保留（只把显示态改成"全不勾"，见 A.5 额外修正 1）
        vm.Tree.SyncRoots(new[]
        {
            new ShareRootSpec(@"\\" + ProbeHost + @"\C$", false),
            new ShareRootSpec(@"\\" + ProbeHost + @"\D$", true),
        });
        Assert.Equal(2, vm.Tree.RootNodes.Count);
        var rootC = vm.Tree.RootNodes.Single(r => r.FullPath == @"\\" + ProbeHost + @"\C$");
        Assert.False(rootC.IsChecked); // 显示与事实一致：该共享已不进 wholeShares，就不能画成"整盘迁移"

        // ② 共享真的从列表里消失（Step1 重新枚举后发现该共享不存在）⇒ 根才允许被删
        vm.Tree.SyncRoots(new[] { new ShareRootSpec(@"\\" + ProbeHost + @"\D$", true) });
        Assert.Single(vm.Tree.RootNodes);
        Assert.Equal(@"\\" + ProbeHost + @"\D$", vm.Tree.RootNodes[0].FullPath);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  要求 2：子目录选择生成的 selections 与预期一致（真实读盘）
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 造出真实目录树并真实展开（EnsureChildrenAsync 走 Directory.EnumerateDirectories/Files）：
    ///   root/ A5Alpha/{ A5AlphaSub/, alpha.txt }   A5Beta/{ beta.txt }   A5root.txt
    /// 名字刻意避开迁移矩阵的排除名单（默认矩阵排除 $RECYCLE.BIN / desktop.ini / Thumbs.db 等）。
    /// </summary>
    private (string Root, DirNode Alpha, DirNode AlphaSub, DirNode Beta, FileRow RootFile) BuildSelectionTree()
    {
        var root = NewDir("sel-root");
        var alpha = NewDir("sel-root", "A5Alpha");
        var alphaSub = NewDir("sel-root", "A5Alpha", "A5AlphaSub");
        var beta = NewDir("sel-root", "A5Beta");
        WriteFile(Path.Combine(alpha, "alpha.txt"), "alpha");
        WriteFile(Path.Combine(alphaSub, "deep.txt"), "deep");
        WriteFile(Path.Combine(beta, "beta.txt"), "beta");
        WriteFile(Path.Combine(root, "A5root.txt"), "root");

        var tree = NewTree();
        var node = tree.AddLocalRootForVerification(root);
        AwaitNoContext(() => tree.EnsureChildrenAsync(node));
        Assert.True(node.ChildrenLoaded);

        var alphaNode = node.Children.OfType<DirNode>().Single(d => d.Name == "A5Alpha");
        var betaNode = node.Children.OfType<DirNode>().Single(d => d.Name == "A5Beta");
        var rootFile = node.Children.OfType<FileRow>().Single(f => f.Name == "A5root.txt");
        AwaitNoContext(() => tree.EnsureChildrenAsync(alphaNode));
        var alphaSubNode = alphaNode.Children.OfType<DirNode>().Single(d => d.Name == "A5AlphaSub");
        Assert.Contains(alphaNode.Children.OfType<FileRow>(), f => f.Name == "alpha.txt");

        // 根全勾时懒加载出来的子项必须默认全勾（inherit = 父 IsChecked != false）
        Assert.True(alphaNode.IsChecked);
        Assert.True(betaNode.IsChecked);
        Assert.True(rootFile.IsChecked);
        Assert.True(alphaSubNode.IsChecked);

        _selectionTree = tree;      // 选择汇总必须走同一棵树
        _selectionRoot = node;
        return (root, alphaNode, alphaSubNode, betaNode, rootFile);
    }

    private List<string> Selections() => _selectionTree!.CollectCustomSelections();

    /// <summary>
    /// 行为级（要求 2 主用例）：取消 A5Beta ⇒ 根半选 ⇒
    /// customs 必须 = 仍全勾的 A5Alpha（目录折叠到最上层）+ 根目录散落文件 A5root.txt，
    /// 且**不得**包含被取消的 A5Beta 及其内部文件。
    /// </summary>
    [Fact]
    public void A5_2a_UncheckOneChild_SelectionsAreExactlyTheStillCheckedScope()
    {
        var (root, _, _, beta, _) = BuildSelectionTree();

        beta.SetChecked(false);

        Assert.Null(_selectionRoot!.IsChecked);
        Assert.Equal(
            new[] { Path.Combine(root, "A5Alpha"), Path.Combine(root, "A5root.txt") },
            Selections());
    }

    /// <summary>行为级：根全不选 ⇒ 完全排除（selections 必须为空，走"不迁移"而不是"整盘"）。</summary>
    [Fact]
    public void A5_2b_UncheckEverything_ProducesNoSelections()
    {
        BuildSelectionTree();

        _selectionRoot!.SetChecked(false);

        Assert.False(_selectionRoot.IsChecked);
        Assert.Empty(Selections());
    }

    /// <summary>
    /// 行为级：根全选 ⇒ **不进 customs**（整盘迁移走 wholeShares）。
    /// 这条同时是"A5_2b 的空结果不是 bug"的反向对照。
    /// </summary>
    [Fact]
    public void A5_2c_CheckRoot_ProducesNoCustomSelections_BecauseWholeShare()
    {
        var (_, alpha, _, _, _) = BuildSelectionTree();

        alpha.SetChecked(false);
        Assert.Null(_selectionRoot!.IsChecked);
        Assert.NotEmpty(Selections());

        _selectionRoot.SetChecked(true);      // 回到全勾
        Assert.True(_selectionRoot.IsChecked);
        Assert.Empty(Selections());
    }

    /// <summary>
    /// 行为级：半勾目录继续下钻 —— 只勾 A5Alpha 下的子目录 A5AlphaSub 时，
    /// customs 必须是那个更深一层的目录（而不是它的父目录，也不是空）。
    /// </summary>
    [Fact]
    public void A5_2d_NestedPartialDirectory_DivesDeeperAndCollapsesCorrectly()
    {
        var (root, alpha, alphaSub, beta, rootFile) = BuildSelectionTree();

        alpha.SetChecked(false);          // Alpha 下全不勾
        beta.SetChecked(false);           // Beta 整棵排除
        alphaSub.SetChecked(true);        // 只勾深一层的子目录 ⇒ Alpha 变半选、根半选
        rootFile.IsChecked = false;       // 排除根目录散落文件

        Assert.Null(alpha.IsChecked);
        Assert.Null(_selectionRoot!.IsChecked);
        Assert.Equal(new[] { alphaSub.FullPath }, Selections());
        Assert.Equal(alphaSub.FullPath, Path.Combine(root, "A5Alpha", "A5AlphaSub"));
    }

    /// <summary>
    /// 行为级：勾一个**文件**（其所在目录未被整体勾选）时，selections 里出现的是那个文件的完整路径；
    /// 一旦整棵根被勾上，文件行必须被折叠掉（走整盘 wholeShares ⇒ customs 清空）。
    /// </summary>
    [Fact]
    public void A5_2e_CheckedFile_IsListed_ButCollapsedWhenItsDirectoryIsFullyChecked()
    {
        var (root, alpha, alphaSub, _, rootFile) = BuildSelectionTree();

        alpha.SetChecked(false);
        alphaSub.SetChecked(false);
        _selectionRoot!.SetChecked(false);
        Assert.Empty(Selections());

        // 只勾根目录下的散落文件
        rootFile.IsChecked = true;
        Assert.True(rootFile.IsChecked);
        Assert.Equal(new[] { Path.Combine(root, "A5root.txt") }, Selections());

        // 再把整个根勾上 ⇒ 走整盘，customs 清空
        _selectionRoot.SetChecked(true);
        Assert.Empty(Selections());
    }

    /// <summary>行为级：FindShareRootOf 必须把 customs 路径正确归属回共享根（Planner 相对路径映射的依据）。</summary>
    [Fact]
    public void A5_2f_FindShareRootOf_MapsCustomSelectionBackToItsRoot()
    {
        var (root, alpha, _, _, _) = BuildSelectionTree();
        alpha.SetChecked(false);

        Assert.Equal(root, _selectionTree!.FindShareRootOf(Path.Combine(root, "A5Beta")));
        Assert.Equal(root, _selectionTree.FindShareRootOf(root));
        Assert.Null(_selectionTree.FindShareRootOf(Path.Combine(NewDir("other-root"), "X")));
    }

    // ══════════════════════════════════════════════════════════════════════
    //  要求 3：全选 / 半选 / 全不选三态语义
    // ══════════════════════════════════════════════════════════════════════

    private static (DirNode Root, DirNode A, DirNode B) TwoChildRoot()
    {
        var root = new DirNode { Name = "root", FullPath = @"\\h\s" };
        var a = new DirNode { Name = "A", FullPath = @"\\h\s\A", Parent = root };
        var b = new DirNode { Name = "B", FullPath = @"\\h\s\B", Parent = root };
        root.Children.Add(a);
        root.Children.Add(b);
        return (root, a, b);
    }

    /// <summary>行为级：三态级联 + 两个 XAML 只读投影（实心勾 / 半勾方块）互斥且正确。</summary>
    [Fact]
    public void A5_3a_ThreeStateCascade_AndMutuallyExclusiveProjections()
    {
        var (root, a, b) = TwoChildRoot();

        root.SetChecked(true);                       // 全选
        Assert.True(root.IsChecked);
        Assert.True(root.CheckStateTrue);
        Assert.False(root.CheckStateIndeterminate);
        Assert.True(a.IsChecked);
        Assert.True(b.IsChecked);

        a.SetChecked(false);                         // 取消一个 ⇒ 半选
        Assert.Null(root.IsChecked);
        Assert.False(root.CheckStateTrue);
        Assert.True(root.CheckStateIndeterminate);
        Assert.False(a.IsChecked);
        Assert.True(b.IsChecked);

        b.SetChecked(false);                         // 全不选
        Assert.False(root.IsChecked);
        Assert.False(root.CheckStateTrue);
        Assert.False(root.CheckStateIndeterminate);

        root.ToggleFromUi();                         // 用户点根：全不选 → 全选
        Assert.True(root.IsChecked);
        Assert.True(a.IsChecked);
        Assert.True(b.IsChecked);

        root.ToggleFromUi();                         // 全选 → 全不选
        Assert.False(root.IsChecked);
        Assert.False(a.IsChecked);
        Assert.False(b.IsChecked);

        root.ToggleFromUi();                         // 再点 → 全选
        Assert.True(root.IsChecked);

        a.SetChecked(false);
        Assert.Null(root.IsChecked);
        root.ToggleFromUi();                         // **半选 → 再点 = 全选**（用户第 3 条的状态机）
        Assert.True(root.IsChecked);
        Assert.True(a.IsChecked);
    }

    /// <summary>
    /// 行为级：三态变化的通知必须**同时**发 IsChecked 与两个只读投影。
    /// 少发 IsChecked ⇒ 根↔共享行的联动静默失效；少发投影 ⇒ 界面图标不刷新。
    /// </summary>
    [Fact]
    public void A5_3b_CheckStateChange_RaisesIsCheckedAndBothProjections()
    {
        var (root, a, _) = TwoChildRoot();
        root.SetChecked(true);

        var raised = new List<string>();
        root.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        a.SetChecked(false);   // 经 RecomputeFromChildren 把根推到半选

        Assert.Contains(nameof(DirNode.IsChecked), raised);
        Assert.Contains(nameof(DirNode.CheckStateTrue), raised);
        Assert.Contains(nameof(DirNode.CheckStateIndeterminate), raised);
    }

    /// <summary>行为级：文件行没有半选语义（恒 false），且它的勾选变化必须向上重算父目录。</summary>
    [Fact]
    public void A5_3c_FileRow_HasNoIndeterminateState_AndPropagatesToParent()
    {
        var root = new DirNode { Name = "root", FullPath = @"\\h\s" };
        var dir = new DirNode { Name = "D", FullPath = @"\\h\s\D", Parent = root };
        var file = new FileRow { Name = "f.txt", DirPath = dir.FullPath, Size = 10, Parent = dir };
        var other = new FileRow { Name = "g.txt", DirPath = dir.FullPath, Size = 20, Parent = dir };
        root.Children.Add(dir);
        dir.Children.Add(file);
        dir.Children.Add(other);

        root.SetChecked(true);
        Assert.True(file.IsChecked);
        Assert.True(other.IsChecked);

        var raised = new List<string>();
        file.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);
        file.IsChecked = false;

        // 目录里还有一个仍勾着的文件 ⇒ 目录半选，根也随之半选
        Assert.Null(dir.IsChecked);
        Assert.Null(root.IsChecked);
        Assert.False(file.CheckStateIndeterminate);      // 文件行永远没有半选
        Assert.Contains(nameof(FileRow.IsChecked), raised);
        Assert.Contains(nameof(FileRow.CheckStateTrue), raised);
    }

    /// <summary>
    /// 行为级：**首次展开继承父的当前勾选态**（用户 2026-09-29 定的目录树产品逻辑）——
    /// 父为"勾/半勾" ⇒ 新解出的子项默认勾；父为"全不勾" ⇒ 默认不勾。
    /// 走的是真实懒加载路径（EnsureChildrenAsync 里的 inherit 计算）。
    /// </summary>
    [Fact]
    public void A5_3d_NewlyLoadedChildren_InheritHalfCheckedParentAsChecked_AndUncheckedParentAsUnchecked()
    {
        var root = NewDir("inherit-root");
        var alpha = NewDir("inherit-root", "A5Alpha");
        WriteFile(Path.Combine(alpha, "alpha.txt"), "a");
        var gamma = NewDir("inherit-root", "A5Gamma");
        WriteFile(Path.Combine(gamma, "gamma.txt"), "g");

        var tree = NewTree();
        var node = tree.AddLocalRootForVerification(root);
        AwaitNoContext(() => tree.EnsureChildrenAsync(node));

        var alphaNode = node.Children.OfType<DirNode>().Single(d => d.Name == "A5Alpha");
        var gammaNode = node.Children.OfType<DirNode>().Single(d => d.Name == "A5Gamma");

        gammaNode.SetChecked(false);                 // 让根变半选（A5Alpha 仍全勾）
        Assert.Null(node.IsChecked);

        AwaitNoContext(() => tree.EnsureChildrenAsync(alphaNode));   // 半勾父下首次展开
        Assert.NotEmpty(alphaNode.Children);
        Assert.All(alphaNode.Children, c => Assert.True(c is DirNode d ? d.IsChecked == true : ((FileRow)c).IsChecked));

        node.SetChecked(false);                      // 根全不勾 ⇒ A5Gamma 也是全不勾
        AwaitNoContext(() => tree.EnsureChildrenAsync(gammaNode));
        Assert.NotEmpty(gammaNode.Children);
        Assert.All(gammaNode.Children, c => Assert.False(c is DirNode d ? d.IsChecked == true : ((FileRow)c).IsChecked));
    }

    // ══════════════════════════════════════════════════════════════════════
    //  要求 4：旧任务 Threads=32/64/128 恢复后不被新 UI 改写
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>行为级：把 job.json 里的旧线程值设为当前显示值 —— 下拉里没有对应项也要动态追加，且绝不改写。</summary>
    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(128)]
    public void A5_4a_PinThreadsFromLegacyJob_KeepsTheLegacyValue(int legacyThreads)
    {
        var vm = NewSession();

        vm.PinThreadsFromJob(legacyThreads);

        Assert.Equal(legacyThreads, vm.SelectedMt);
        Assert.Equal(legacyThreads, vm.ResolvedThreads);
        Assert.True(vm.MtValuePinnedByJob);
        Assert.Equal(legacyThreads, vm.ThreadsForRun);              // 无任务 ⇒ 用显示值；有任务时以 job.json 为准（A5_4e）
        Assert.Contains(legacyThreads, vm.MtOptions);               // 原值被追加进下拉，而不是被换成最近的预设项
        Assert.Equal(vm.MtOptions.OrderBy(x => x).ToList(), vm.MtOptions.ToList()); // 升序插入，顺序自然
        Assert.Contains("任务原值", vm.MtText, StringComparison.Ordinal);
        Assert.Contains("任务原值", vm.MtTextFor(legacyThreads), StringComparison.Ordinal);
        Assert.Equal("自动（推荐）", vm.MtTextFor(MigrationSessionViewModel.AutoThreadsSentinel)); // 哨兵 0 =「自动（推荐）」
        Assert.Equal("16", vm.MtTextFor(MigrationSessionViewModel.AutoThreadsResolved));           // 16 是独立档位，不再显示成「自动」
        Assert.Equal(16, MigrationSessionViewModel.ResolveThreads(MigrationSessionViewModel.AutoThreadsSentinel));
        Assert.Contains("沿用原值", vm.ThreadsDisclaimer, StringComparison.Ordinal);
        Assert.Equal(legacyThreads, vm.MtFromText(vm.MtTextFor(legacyThreads)));    // 下拉文案→数值 可逆
    }

    /// <summary>行为级：只有 >0 才当真值；0/负数一律按 Core 默认 16 解析（写盘口径）。</summary>
    [Theory]
    [InlineData(0, 16)]
    [InlineData(-3, 16)]
    [InlineData(4, 4)]
    [InlineData(8, 8)]
    [InlineData(32, 32)]
    public void A5_4b_ResolveThreads_NormalisesAutoAndIllegalValues(int input, int expected)
    {
        Assert.Equal(expected, MigrationSessionViewModel.ResolveThreads(input));
        Assert.Equal(16, MigrationSessionViewModel.AutoThreadsResolved);
    }

    /// <summary>行为级（用户要求）：线程数下拉必须是完整 7 档，「自动（推荐）」与 16 并列且互不冒充。</summary>
    [Fact]
    public void A5_4b2_MtOptions_CoverTheFullSevenTiers()
    {
        var vm = NewSession();

        Assert.Equal(new[] { 0, 4, 8, 16, 32, 64, 128 }, vm.MtOptions.ToArray());
        Assert.Equal("自动（推荐）", vm.MtTextFor(0));
        Assert.Equal("4", vm.MtTextFor(4));
        Assert.Equal("8", vm.MtTextFor(8));
        Assert.Equal("16", vm.MtTextFor(16));
        Assert.Equal("32", vm.MtTextFor(32));
        Assert.Equal("64", vm.MtTextFor(64));
        Assert.Equal("128", vm.MtTextFor(128));
        Assert.Equal("自动（推荐）", vm.MtText);            // 新建任务默认档 = 自动
        Assert.Equal(16, vm.ResolvedThreads);              // 自动解析成真实整数 16（写盘口径）
        Assert.Equal(0, vm.MtFromText("自动（推荐）"));      // 文案→哨兵 0，可逆
        Assert.Equal(16, vm.MtFromText("16"));
        Assert.Equal(128, vm.MtFromText("128"));
    }

    /// <summary>行为级：只有用户**显式**改下拉才解除"任务原值"锁（恢复中的任务不会因界面动作被无声改写）。</summary>
    [Fact]
    public void A5_4c_OnlyExplicitUserSelection_ReleasesTheLegacyPin()
    {
        var vm = NewSession();
        vm.PinThreadsFromJob(64);

        vm.MtOptions.Add(255);                       // 类似界面的集合变化，不得解除锁定
        Assert.True(vm.MtValuePinnedByJob);
        Assert.Equal(64, vm.SelectedMt);

        vm.SelectedMt = 8;                           // 用户显式选择
        Assert.False(vm.MtValuePinnedByJob);
        Assert.Equal(8, vm.ResolvedThreads);
        Assert.Equal("8", vm.MtText);
        Assert.DoesNotContain("任务原值", vm.MtText, StringComparison.Ordinal);
    }

    /// <summary>
    /// 行为级（要求 4 的硬核部分）：恢复一个 Threads=64 的**真实 job.json** 后，
    /// ① 界面显示 64；② job.json 字节**逐字节不变**（SHA256 相同）。
    /// </summary>
    [Fact]
    public async Task A5_4d_AdoptingLegacyJob_ShowsLegacyThreads_AndNeverRewritesJobJson()
    {
        var ctx = CreateJob("JOB-A5-THREADS", threads: 64);
        var before = Sha256Of(ctx.JobJsonPath);

        var vm = NewSession();
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        Assert.Equal(64, vm.SelectedMt);
        Assert.True(vm.MtValuePinnedByJob);
        Assert.Equal(64, vm.ThreadsForRun);
        Assert.Equal(64, vm.Ctx!.Definition.Options.Threads);
        Assert.Equal(before, Sha256Of(ctx.JobJsonPath));    // ★ 新 UI 没有改写任务定义
    }

    /// <summary>
    /// 行为级：Resume 沿用 job.json 的线程值（引擎拿到的就是 128），且运行前后 job.json 一字未改。
    /// 断言取的是**假引擎收到的 ctx.Definition.Options.Threads**，即引擎真正会用的那个值。
    /// </summary>
    [Fact]
    public async Task A5_4e_Resume_UsesLegacyThreadsForTheEngine_AndNeverRewritesJobJson()
    {
        var ctx = CreateJob("JOB-A5-RESUME", threads: 128);
        var before = Sha256Of(ctx.JobJsonPath);

        var vm = NewSession();
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        var engine = new FakeEngine(vm) { FileCopiedEvents = 0 };
        vm.TransferRunner = engine.Delegate;
        await WithInlineSyncContext(() => vm.ResumeAsync(password: null));

        Assert.Equal(new[] { 128 }, engine.ThreadsSeen);
        Assert.Equal(128, vm.ThreadsForRun);
        Assert.Equal(128, vm.SelectedMt);
        Assert.True(vm.MtValuePinnedByJob);
        Assert.Equal(before, Sha256Of(ctx.JobJsonPath));
    }

    // ══════════════════════════════════════════════════════════════════════
    //  要求 5：CompletedWithErrors 不被 UI 强制成 100%
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 行为级：载入一个 CompletedWithErrors 的旧任务（计划 320GB、实际落盘 80GB）——
    /// 必须显示真实比例 25%，不得被界面强制成 100%；文案必须明说"没传完、可恢复"。
    /// </summary>
    [Fact]
    public async Task A5_5a_LoadingCompletedWithErrorsJob_KeepsTheRealPercent()
    {
        var ctx = CreateJob("JOB-A5-CWE", state: new JobState
        {
            JobId = "JOB-A5-CWE",
            Phase = JobPhase.CompletedWithErrors,
            TotalObjects = 10,
            CompletedObjects = 3,
            FailedObjects = 2,
            TotalBytes = 320L * 1024 * 1024 * 1024,
            CompletedBytes = 80L * 1024 * 1024 * 1024,
            Percent = 25,
        });

        var conn = NewConnection();
        var vm = NewSession(conn);
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        Assert.Equal(JobPhase.CompletedWithErrors, vm.Phase);
        Assert.Equal(25.0, vm.Percent);                       // ★ 不是 100
        Assert.NotEqual(100.0, vm.Percent);
        Assert.Equal("实际落盘 / 计划", vm.DataLabel);
        Assert.Contains("未完整完成", vm.PhaseText, StringComparison.Ordinal);
        Assert.Contains("没有全部传完", vm.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("迁移完成！", vm.StatusMessage, StringComparison.Ordinal);

        var readiness = new PageReadiness(conn, vm);
        Assert.Contains("未完整完成", readiness.Step3StateText, StringComparison.Ordinal);
        Assert.DoesNotContain("迁移已完成", readiness.Step3StateText, StringComparison.Ordinal);
        Assert.Contains("未完整完成", readiness.Step4StateText, StringComparison.Ordinal);
    }

    /// <summary>行为级（反向对照）：真 Completed 才允许 100%，否则 A5_5a 的"不是 100"毫无意义。</summary>
    [Fact]
    public async Task A5_5b_LoadingCompletedJob_ShowsExactly100Percent()
    {
        var ctx = CreateJob("JOB-A5-DONE", state: new JobState
        {
            JobId = "JOB-A5-DONE",
            Phase = JobPhase.Completed,
            TotalObjects = 10,
            CompletedObjects = 10,
            TotalBytes = 320L * 1024 * 1024 * 1024,
            CompletedBytes = 320L * 1024 * 1024 * 1024,
            Percent = 100,
        });

        var conn = NewConnection();
        var vm = NewSession(conn);
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        Assert.Equal(JobPhase.Completed, vm.Phase);
        Assert.Equal(100.0, vm.Percent);
        Assert.Contains("已完成", vm.PhaseText, StringComparison.Ordinal);

        var readiness = new PageReadiness(conn, vm);
        Assert.Contains("迁移已完成", readiness.Step3StateText, StringComparison.Ordinal);
        Assert.DoesNotContain("未完整完成", readiness.Step3StateText, StringComparison.Ordinal);
    }

    /// <summary>
    /// 行为级：**运行中**收到 CompletedWithErrors 快照（ApplySnapshot 路径）时，
    /// 进度百分比必须是引擎报的真实值；同一快照必须把 IsFinished 置 true（流程结束 ≠ 进度已满）。
    /// </summary>
    [Fact]
    public async Task A5_5c_CompletedWithErrorsSnapshotDuringRun_IsNotForcedTo100Percent()
    {
        var src = NewDir("run-src", "cwe");
        WriteFile(Path.Combine(src, "one.bin"), "x");
        var ctx = CreateJob("JOB-A5-RUN-CWE", plan: PlanWithOneObject(src), state: new JobState
        {
            JobId = "JOB-A5-RUN-CWE",
            Phase = JobPhase.AwaitingReview,
            TotalObjects = 10,
            CompletedObjects = 0,
            TotalBytes = 320L * 1024 * 1024 * 1024,
            CompletedBytes = 80L * 1024 * 1024 * 1024,
        });

        var vm = NewSession();
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        var engine = new FakeEngine(vm)
        {
            Snapshot = new ProgressSnapshot(
                JobPhase.CompletedWithErrors, 10, 3, 2, 320L * 1024 * 1024 * 1024, 80L * 1024 * 1024 * 1024,
                41.7, 0, double.NaN, null, null, string.Empty),
            Result = JobPhase.CompletedWithErrors,
        };
        vm.TransferRunner = engine.Delegate;

        await WithInlineSyncContext(() => vm.RunAsync(password: null));

        // 快照那一刻
        Assert.Equal(new[] { 41.7 }, engine.PercentRightAfterSnapshot);
        Assert.Equal(new[] { JobPhase.CompletedWithErrors }, engine.PhaseRightAfterSnapshot);
        Assert.Equal(new[] { true }, engine.FinishedRightAfterSnapshot);
        Assert.Contains("未完整完成", engine.StatusRightAfterSnapshot[0], StringComparison.Ordinal);
        Assert.DoesNotContain("迁移完成！", engine.StatusRightAfterSnapshot[0], StringComparison.Ordinal);

        // 收尾之后：Percent 由 job-state.json 的真实字节重算 = 80/320 = 25%，仍不是 100%
        Assert.Equal(JobPhase.CompletedWithErrors, vm.Phase);
        Assert.Equal(25.0, vm.Percent);
        Assert.True(vm.IsFinished);
        Assert.Contains("未完整完成", vm.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("迁移完成！", vm.StatusMessage, StringComparison.Ordinal);
    }

    /// <summary>行为级（反向对照）：同一路径下真 Completed 必须 100%。</summary>
    [Fact]
    public async Task A5_5d_CompletedSnapshotDuringRun_Is100Percent()
    {
        var src = NewDir("run-src", "done");
        WriteFile(Path.Combine(src, "one.bin"), "x");
        var ctx = CreateJob("JOB-A5-RUN-DONE", plan: PlanWithOneObject(src), state: new JobState
        {
            JobId = "JOB-A5-RUN-DONE",
            Phase = JobPhase.AwaitingReview,
            TotalObjects = 1,
            CompletedObjects = 1,
            TotalBytes = 1024,
            CompletedBytes = 1024,
        });

        var vm = NewSession();
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        var engine = new FakeEngine(vm)
        {
            Snapshot = new ProgressSnapshot(JobPhase.Completed, 1, 1, 0, 1024, 1024, 100, 0, double.NaN, null, null, string.Empty),
            Result = JobPhase.Completed,
        };
        vm.TransferRunner = engine.Delegate;

        await WithInlineSyncContext(() => vm.RunAsync(password: null));

        Assert.Equal(new[] { 100.0 }, engine.PercentRightAfterSnapshot);
        Assert.Equal(100.0, vm.Percent);
        Assert.Contains("迁移完成！", vm.StatusMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// 行为级：PageReadiness 的 Step3/Step4 文案必须**分开** Completed 与 CompletedWithErrors
    /// （改前两者并进同一分支，会让"只传了一部分"的任务看起来跟正常完成一样）。
    /// </summary>
    [Fact]
    public async Task A5_5e_PageReadiness_DistinguishesCompletedFromCompletedWithErrors()
    {
        var cweCtx = CreateJob("JOB-A5-PR-CWE", state: new JobState
        {
            JobId = "JOB-A5-PR-CWE", Phase = JobPhase.CompletedWithErrors,
            TotalObjects = 4, CompletedObjects = 1, TotalBytes = 400, CompletedBytes = 100, Percent = 25,
        });
        var doneCtx = CreateJob("JOB-A5-PR-DONE", state: new JobState
        {
            JobId = "JOB-A5-PR-DONE", Phase = JobPhase.Completed,
            TotalObjects = 4, CompletedObjects = 4, TotalBytes = 400, CompletedBytes = 400, Percent = 100,
        });

        var conn = NewConnection();
        var cwe = NewSession(conn);
        Assert.True(await cwe.AdoptExistingJobAsync(cweCtx.JobDir));
        var done = NewSession(conn);
        Assert.True(await done.AdoptExistingJobAsync(doneCtx.JobDir));

        var cweStep3 = new PageReadiness(conn, cwe).Step3StateText;
        var doneStep3 = new PageReadiness(conn, done).Step3StateText;

        Assert.NotEqual(doneStep3, cweStep3);
        Assert.Contains("未完整完成", cweStep3, StringComparison.Ordinal);
        Assert.Contains("25", cweStep3, StringComparison.Ordinal);       // 真实比例必须出现在文案里
        Assert.Contains("迁移已完成", doneStep3, StringComparison.Ordinal);
        Assert.DoesNotContain("未完整完成", doneStep3, StringComparison.Ordinal);

        // L1 口径纪律：完成文案里绝不能出现"完整性验证通过"
        Assert.DoesNotContain("完整性验证通过", doneStep3, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  要求 6：新 Job / 换 Job / Repair 清理旧 FailItems / LiveFiles / _failIndex
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 行为级（Repair + _failIndex 同步清 —— P1-4 ①）：
    /// 同一句引擎错误行跨两次 Repair 都能重新显示 ⇒ 证明 FailItems.Clear() 时 _failIndex 也被清。
    /// 观测点在假引擎体内（清理刚发生、别的 AddFail 还没机会插进来），不依赖时序。
    ///
    /// 若 _failIndex 未同步清：第 2 次 Repair 里同一行会被去重索引吞掉（计数 0），断言立刻失败。
    /// </summary>
    [Fact]
    public async Task A5_6a_Repair_ClearsFailItems_AndClearsFailIndexSoSameFailureCanReappear()
    {
        var src = NewDir("repair-src");
        WriteFile(Path.Combine(src, "payload.bin"), "1234567890");
        var ctx = CreateJob(
            "JOB-A5-REPAIR",
            plan: PlanWithOneObject(src),
            state: new JobState
            {
                JobId = "JOB-A5-REPAIR", Phase = JobPhase.CompletedWithErrors,
                TotalObjects = 1, CompletedObjects = 0, FailedObjects = 1,
                TotalBytes = 10, CompletedBytes = 0, Percent = 0,
            },
            receipts: new[]
            {
                new ObjectReceipt
                {
                    ObjectId = "object-000001",
                    Kind = ObjectKind.DataVolume,
                    SourcePath = src,
                    TargetPath = src,
                    Status = ObjectStatus.Failed,
                    ErrorDetail = "A5 测试用失败回执",
                    RobocopyExitCodeBulk = 8,
                },
            });

        var vm = NewSession();
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));
        Assert.NotEmpty(vm.FailItems);                        // 载入失败回执 ⇒ 失败清单里有对象级失败项
        Assert.NotEmpty(vm.CollectRepairTargets(out _));       // 确有需要修复的对象（Repair 的前置条件）

        // ① 先跑一次普通运行，把"同一句引擎错误行"登记进去重索引（_failIndex）
        var seed = new FakeEngine(vm);
        vm.TransferRunner = seed.Delegate;
        await WithInlineSyncContext(() => vm.RunAsync(password: null));
        Assert.Equal(new[] { 1 }, seed.ErrorLineCounts);          // 同一行发两次只留一条（去重生效）
        Assert.Contains(vm.FailItems, f => f.Detail == seed.ErrorLine && f.Title == "引擎输出");

        // ② Repair：必须在开跑前清空失败清单（含去重索引）
        var repair1 = new FakeEngine(vm);
        vm.TransferRunner = repair1.Delegate;
        await WithInlineSyncContext(() => vm.RepairAsync(forceOverwrite: true, password: null));

        Assert.Equal(new[] { 1 }, repair1.ErrorLineCounts);            // 清理后同一行能重新显示
        Assert.Equal(new[] { false }, repair1.StaleObjectFailVisible); // 旧的"对象失败"项确实被清掉了
        Assert.Equal(new[] { true }, repair1.RepairingFlags);          // 确实走的是 Repair 路径（forceRecopy=true）

        // ③ 再 Repair 一次：同一行**必须还能**重新显示（这正是 _failIndex 未同步清时会失效的地方）
        var repair2 = new FakeEngine(vm);
        vm.TransferRunner = repair2.Delegate;
        await WithInlineSyncContext(() => vm.RepairAsync(forceOverwrite: true, password: null));

        Assert.Equal(new[] { 1 }, repair2.ErrorLineCounts);
    }

    /// <summary>
    /// 行为级（换 Job 复位 —— P1-4 ③，**按修复后的契约写**）：
    /// 载入另一个任务时，上一个任务遗留的 FailItems / LiveFiles 必须被清空。
    /// </summary>
    [Fact]
    public async Task A5_6b_AdoptingAnotherJob_ClearsPreviousFailItemsAndLiveFiles()
    {
        var srcA = NewDir("switch-a");
        WriteFile(Path.Combine(srcA, "a.bin"), "a");
        var jobA = CreateJob("JOB-A5-SWITCH-A", plan: PlanWithOneObject(srcA), state: new JobState
        {
            JobId = "JOB-A5-SWITCH-A", Phase = JobPhase.AwaitingReview, TotalObjects = 1, TotalBytes = 1,
        });
        var jobB = CreateJob("JOB-A5-SWITCH-B", state: new JobState
        {
            JobId = "JOB-A5-SWITCH-B", Phase = JobPhase.Created,
        });

        var vm = NewSession();
        Assert.True(await vm.AdoptExistingJobAsync(jobA.JobDir));

        var engine = new FakeEngine(vm) { FileCopiedEvents = 3 };
        vm.TransferRunner = engine.Delegate;
        await WithInlineSyncContext(() => vm.RunAsync(password: null));

        Assert.NotEmpty(vm.FailItems);                       // 旧任务的失败项
        Assert.NotEmpty(vm.LiveFiles);                       // 旧任务的实时文件流
        Assert.Equal(new[] { 3 }, engine.LiveFileCounts);

        Assert.True(await vm.AdoptExistingJobAsync(jobB.JobDir));

        Assert.Equal("JOB-A5-SWITCH-B", vm.JobIdText);
        Assert.Empty(vm.FailItems);                          // ★ 换 Job 必须清空
        Assert.Empty(vm.LiveFiles);                          // ★ 换 Job 必须清空
    }

    /// <summary>
    /// 行为级（新建任务复位 —— P1-4 ③，**按修复后的契约写**）：
    /// 用户点「新建任务」（ClearCurrentJobSelection）后，上一个任务的失败清单与实时文件流必须清空。
    /// </summary>
    [Fact]
    public async Task A5_6c_NewJobSelection_ClearsPreviousFailItemsAndLiveFiles()
    {
        var src = NewDir("newjob-src");
        WriteFile(Path.Combine(src, "a.bin"), "a");
        var job = CreateJob("JOB-A5-NEW", plan: PlanWithOneObject(src), state: new JobState
        {
            JobId = "JOB-A5-NEW", Phase = JobPhase.AwaitingReview, TotalObjects = 1, TotalBytes = 1,
        });

        var vm = NewSession();
        Assert.True(await vm.AdoptExistingJobAsync(job.JobDir));

        var engine = new FakeEngine(vm) { FileCopiedEvents = 2 };
        vm.TransferRunner = engine.Delegate;
        await WithInlineSyncContext(() => vm.RunAsync(password: null));

        Assert.NotEmpty(vm.FailItems);
        Assert.NotEmpty(vm.LiveFiles);

        vm.ClearCurrentJobSelection();

        Assert.False(vm.HasJob);
        Assert.Equal(0.0, vm.Percent);
        Assert.Empty(vm.FailItems);                          // ★ 新建任务必须清空
        Assert.Empty(vm.LiveFiles);                          // ★ 新建任务必须清空
    }

    // ══════════════════════════════════════════════════════════════════════
    //  要求 7：unfinished 探测不会被旧异步结果反向覆盖（P2-6，按契约写）
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>造一个"可续传"的真实任务：有 plan.json、阶段 ∈ Core 的 ResumablePhases、且无存活锁。</summary>
    private JobContext CreateResumableJob(string jobId)
    {
        var src = NewDir("probe-src", jobId);
        WriteFile(Path.Combine(src, "a.bin"), "a");
        return CreateJob(jobId, sourceHost: ProbeHost, plan: PlanWithOneObject(src), state: new JobState
        {
            JobId = jobId,
            Phase = JobPhase.Interrupted,
            TotalObjects = 1,
            TotalBytes = 1,
            CompletedBytes = 0,
            Percent = 12.5,
        });
    }

    /// <summary>
    /// 行为级（前置条件，正常 await 路径）：连上旧电脑后主动探测必须真的报出候选；
    /// 尚未连接时必须说"无法匹配"，而不是谎报"没有未完成任务"。
    /// </summary>
    [Fact]
    public async Task A5_7c_UnfinishedProbe_ReportsRealCandidate_AndNeverLiesWhenNotConnected()
    {
        CreateResumableJob("JOB-A5-PROBE");
        var conn = NewConnection();
        var vm = NewSession(conn);

        conn.Host = string.Empty;
        var none = await vm.FindUnfinishedOnStartupAsync();
        Assert.Empty(none);
        Assert.Contains("尚未连接旧电脑", vm.UnfinishedProbeText, StringComparison.Ordinal);
        Assert.DoesNotContain("未匹配到可续传任务", vm.UnfinishedProbeText, StringComparison.Ordinal);
        Assert.Null(vm.PendingResumeCandidate);

        conn.Host = ProbeHost;
        var found = await vm.FindUnfinishedOnStartupAsync();
        Assert.Equal(new[] { "JOB-A5-PROBE" }, found.Select(j => j.JobId).ToArray());
        Assert.NotNull(vm.PendingResumeCandidate);
        Assert.Contains("检测到未完成的迁移任务", vm.UnfinishedProbeText, StringComparison.Ordinal);
        Assert.Contains("JOB-A5-PROBE", vm.UnfinishedProbeText, StringComparison.Ordinal);
    }

    /// <summary>
    /// 行为级（要求 7 主用例，**按契约写：旧请求返回后不得覆盖新 Host 的状态**）。设计 §6.2 B3。
    ///
    /// ★ 重写说明（2026-09-30）★ 改前这条用 `ManualSyncContext`（自定义 SynchronizationContext 扣续体）
    /// + `private static readonly AsyncLocal&lt;string?&gt; Badge` + 故意不 await + 30 秒 `Thread.Sleep(5)` 轮询
    /// + "退化路径"，**同一份代码两次跑分别得到 181/0 与 179/2** ⇒ 没有判定力，已全部拆除。
    /// 现在：① 假去抖泵掌握"计时器到期"；② `UnfinishedProbeForTest` 让第一次探测**确定性地挂起**；
    /// ③ `LastProbeTaskForTest` 给出"这次探测确实已结束"的确定信号。**全程零 sleep 轮询**。
    ///
    /// 构造：先发起"旧电脑（无任务）"的探测并把它挂起 ⇒ 期间改 Host 到"有可续传任务"的旧电脑并
    /// 跑完第二次探测 ⇒ 随后放行第一次 ⇒ 最终 `UnfinishedProbeText` 与 `PendingResumeCandidate`
    /// 必须**都来自第二次**。旧代码（无世代门）在这里会红：过期结论会把候选清成 null、文案改写成
    /// "未匹配到可续传任务"。
    /// </summary>
    [Fact]
    public async Task A5_7a_StaleUnfinishedProbe_CannotOverwriteTheNewerResult()
    {
        CreateResumableJob("JOB-A5-RACE");
        var conn = NewConnection();
        conn.ForceConnectedForTest(true);
        var vm = NewSession(conn);
        var pump = InstallFakeProbeDebouncePump(vm);

        using var firstProbeEntered = new ManualResetEventSlim(false);
        using var releaseFirstProbe = new ManualResetEventSlim(false);
        var probes = 0;
        vm.UnfinishedProbeForTest = (host, target) =>
        {
            if (Interlocked.Increment(ref probes) == 1)
            {
                firstProbeEntered.Set();
                releaseFirstProbe.Wait(TimeSpan.FromSeconds(30));   // ← 确定性挂起（不是轮询等待）
            }
            return new JobManager(_log).FindUnfinished(host, target);   // 真实读盘 + 真实候选
        };

        // ① 过期探测：host = 一台没有任何任务的旧电脑（结论注定过期）
        conn.Host = RaceStaleHost;
        vm.RequestUnfinishedProbe();                 // 首次请求 = IsSourceConnected false→true ⇒ 立即分支
        var staleProbe = vm.LastProbeTaskForTest;
        Assert.NotNull(staleProbe);
        Assert.True(firstProbeEntered.Wait(TimeSpan.FromSeconds(30)), "第一次探测没能进入测试缝（基础设施异常）");

        // ② 最新探测：host = 有真实可续传任务 JOB-A5-RACE 的旧电脑
        conn.Host = ProbeHost;
        vm.RequestUnfinishedProbe();                 // 已连接且非跳变 ⇒ 走去抖
        Assert.Equal(1, pump.ArmCount);
        pump.Fire();                                 // 去抖到期 ⇒ 发起第二次探测
        var freshProbe = vm.LastProbeTaskForTest;
        Assert.NotNull(freshProbe);
        Assert.False(ReferenceEquals(staleProbe, freshProbe), "去抖到期没有发起新的探测");
        await freshProbe!;                           // 第二次已落地（真实写状态）
        Assert.Contains("检测到未完成的迁移任务", vm.UnfinishedProbeText, StringComparison.Ordinal);

        // ③ 放行过期探测：它已经拿到结果，但**不得**改写任何状态
        releaseFirstProbe.Set();
        await staleProbe!;

        Assert.Contains("检测到未完成的迁移任务", vm.UnfinishedProbeText, StringComparison.Ordinal);
        Assert.DoesNotContain("尚未连接旧电脑", vm.UnfinishedProbeText, StringComparison.Ordinal);
        Assert.DoesNotContain(RaceStaleHost, vm.UnfinishedProbeText, StringComparison.Ordinal);
        Assert.Equal("JOB-A5-RACE", vm.PendingResumeCandidate?.JobId);
    }

    /// <summary>
    /// 行为级（要求 7 的第二半，单独一条以便精确定位）：过期探测不得把最新探测刚找到的
    /// 续传候选（`PendingResumeCandidate`）清成 null —— 那正是 Step4「恢复任务」指向错任务的成因。
    /// 与 A5_7a 同一套确定性构造（假去抖泵 + 挂起第一次探测 + 确定完成信号），无 sleep。
    /// </summary>
    [Fact]
    public async Task A5_7b_StaleUnfinishedProbe_CannotClearTheNewerResumeCandidate()
    {
        CreateResumableJob("JOB-A5-RACE2");
        var conn = NewConnection();
        conn.ForceConnectedForTest(true);
        var vm = NewSession(conn);
        var pump = InstallFakeProbeDebouncePump(vm);

        using var firstProbeEntered = new ManualResetEventSlim(false);
        using var releaseFirstProbe = new ManualResetEventSlim(false);
        var probes = 0;
        vm.UnfinishedProbeForTest = (host, target) =>
        {
            if (Interlocked.Increment(ref probes) == 1)
            {
                firstProbeEntered.Set();
                releaseFirstProbe.Wait(TimeSpan.FromSeconds(30));
            }
            return new JobManager(_log).FindUnfinished(host, target);
        };

        conn.Host = RaceStaleHost;                   // 过期探测：该主机下没有任何任务 ⇒ 结果是"候选 = null"
        vm.RequestUnfinishedProbe();
        var staleProbe = vm.LastProbeTaskForTest;
        Assert.NotNull(staleProbe);
        Assert.True(firstProbeEntered.Wait(TimeSpan.FromSeconds(30)), "第一次探测没能进入测试缝（基础设施异常）");

        conn.Host = ProbeHost;                       // 最新探测：能匹配到 JOB-A5-RACE2
        vm.RequestUnfinishedProbe();
        pump.Fire();
        var freshProbe = vm.LastProbeTaskForTest;
        Assert.NotNull(freshProbe);
        await freshProbe!;
        Assert.Equal("JOB-A5-RACE2", vm.PendingResumeCandidate?.JobId);   // 先确认新探测确实拿到了候选

        releaseFirstProbe.Set();                     // 放行过期探测（返回空列表）
        await staleProbe!;

        Assert.Equal("JOB-A5-RACE2", vm.PendingResumeCandidate?.JobId);   // ★ 不得被清成 null
        Assert.Contains("检测到未完成的迁移任务", vm.UnfinishedProbeText, StringComparison.Ordinal);
        Assert.DoesNotContain("未匹配到可续传任务", vm.UnfinishedProbeText, StringComparison.Ordinal);
    }

    // ── P2-6 的其余契约（设计 §6.2 B1 / B2 / B4 / B5 / B6）──────────────────────

    /// <summary>
    /// 设计 §6.2 **B1**（R10/R12）：未连接时**不做任何 IO**（连接门），但仍如实回报
    /// "尚未连接 ⇒ 无法匹配（≠ 没有未完成任务）"。
    /// 改前每个 Host 字符都会触发一次真实的全量任务目录扫描（JobManager.ListAll）。
    /// </summary>
    [Fact]
    public void A5_7d_NotConnected_HostStormDoesNoProbeIo_ButNeverLies()
    {
        CreateResumableJob("JOB-A5-GATE");
        var conn = NewConnection();
        conn.ForceConnectedForTest(false);
        var vm = NewSession(conn);
        var pump = InstallFakeProbeDebouncePump(vm);

        var probes = 0;
        vm.UnfinishedProbeForTest = (_, _) => { probes++; return Array.Empty<JobSummary>(); };

        foreach (var host in new[] { "h1", "h2", "h3", "h4", "h5" })
        {
            conn.Host = host;
            vm.RequestUnfinishedProbe();
        }

        Assert.Equal(5, pump.ArmCount);              // 每个字符都请求了，但只是重置去抖窗口
        Assert.Equal(0, probes);                     // 输入风暴期间一次 IO 都没有

        pump.Fire();                                 // 去抖到期
        Assert.Equal(0, probes);                     // ★ 未连接 ⇒ 仍然不做 IO（连接门）

        Assert.Equal(MigrationSessionViewModel.UnfinishedProbeNotConnectedText, vm.UnfinishedProbeText);
        Assert.Contains("尚未连接旧电脑", vm.UnfinishedProbeText, StringComparison.Ordinal);
        Assert.Contains("无法匹配", vm.UnfinishedProbeText, StringComparison.Ordinal);
        Assert.DoesNotContain("未匹配到可续传任务", vm.UnfinishedProbeText, StringComparison.Ordinal);
        Assert.Null(vm.PendingResumeCandidate);
    }

    /// <summary>
    /// 设计 §6.2 **B2**（R10）：已连接时，连续 5 次 Host 赋值（间隔远小于去抖窗口）
    /// 只允许发起**一次**探测，且用的是**到期那一刻**的 Host。
    /// </summary>
    [Fact]
    public async Task A5_7e_Connected_HostStormCoalescesToOneProbe_WithTheFinalHost()
    {
        CreateResumableJob("JOB-A5-COALESCE");
        var conn = NewConnection();
        conn.ForceConnectedForTest(true);
        var vm = NewSession(conn);
        var pump = InstallFakeProbeDebouncePump(vm);

        var seenHosts = new List<string?>();
        vm.UnfinishedProbeForTest = (host, _) => { seenHosts.Add(host); return Array.Empty<JobSummary>(); };

        vm.RequestUnfinishedProbe();                 // 先把 false→true 的"立即探测"走掉（B4 单独覆盖）
        await vm.LastProbeTaskForTest!;
        seenHosts.Clear();
        pump.ResetCounts();

        foreach (var host in new[] { "h1", "h2", "h3", "h4", "h5" })
        {
            conn.Host = host;
            vm.RequestUnfinishedProbe();
        }

        Assert.Empty(seenHosts);                     // 风暴期间没有发起探测
        Assert.Equal(5, pump.ArmCount);              // 只是把窗口不断重置（= debounce 的定义）

        pump.Fire();
        await vm.LastProbeTaskForTest!;

        Assert.Equal(new[] { "h5" }, seenHosts.ToArray());   // ★ 恰好一次，且 key = 最终 Host
    }

    /// <summary>
    /// 设计 §6.2 **B4**（R9）：`IsSourceConnected` false→true ⇒ **立即**探测，不被 400ms 去抖延迟。
    /// 判据（确定性）：跳变时**根本不去武装去抖窗口**（ArmCount 不变），而是先取消排队中的窗口。
    /// </summary>
    [Fact]
    public async Task A5_7f_ConnectedEdge_ProbesImmediately_WithoutArmingTheDebounce()
    {
        var conn = NewConnection();
        conn.ForceConnectedForTest(false);
        var vm = NewSession(conn);
        var pump = InstallFakeProbeDebouncePump(vm);

        var probes = 0;
        vm.UnfinishedProbeForTest = (_, _) => { probes++; return Array.Empty<JobSummary>(); };

        vm.RequestUnfinishedProbe();                 // 未连接：走去抖（记下"未连接"这一态）
        Assert.Equal(1, pump.ArmCount);
        pump.ResetCounts();

        conn.ForceConnectedForTest(true);            // 真实跳变（页面/Shell 会据此触发请求）
        var started = DateTime.UtcNow;
        vm.RequestUnfinishedProbe();
        await vm.LastProbeTaskForTest!;
        var elapsed = DateTime.UtcNow - started;

        Assert.Equal(1, probes);                     // 立即发起，没有被去抖窗口挡住
        Assert.Equal(0, pump.ArmCount);              // ★ 跳变分支根本不武装窗口
        Assert.Equal(1, pump.DisarmCount);           // 并且会取消排队中的窗口（避免紧接着重复探测）
        Assert.True(elapsed < MigrationSessionViewModel.UnfinishedProbeDebounceInterval,
            $"立即分支耗时 {elapsed.TotalMilliseconds:F0}ms ≥ 去抖窗口 {MigrationSessionViewModel.UnfinishedProbeDebounceInterval.TotalMilliseconds:F0}ms");
    }

    /// <summary>
    /// 设计 §6.2 **B5**（R13）：探测失败时如实写"未完成任务检测失败：…"，且**同步入口绝不外抛**。
    /// </summary>
    [Fact]
    public async Task A5_7g_ProbeFailurePath_IsHonestAndNeverThrows()
    {
        var conn = NewConnection();
        conn.ForceConnectedForTest(true);
        var vm = NewSession(conn);
        var pump = InstallFakeProbeDebouncePump(vm);

        vm.UnfinishedProbeForTest = (_, _) => throw new InvalidOperationException("probe-boom");

        conn.Host = "h1";
        vm.RequestUnfinishedProbe();                 // 同步入口：绝不外抛（抛了本行就红）
        conn.Host = "h2";
        vm.RequestUnfinishedProbe();
        pump.Fire();                                 // 到期 ⇒ 发起探测（也是 fire-and-forget，同样绝不外抛）
        await vm.LastProbeTaskForTest!;

        Assert.StartsWith("未完成任务检测失败：", vm.UnfinishedProbeText, StringComparison.Ordinal);
        Assert.Contains("probe-boom", vm.UnfinishedProbeText, StringComparison.Ordinal);
        Assert.Null(vm.PendingResumeCandidate);
    }

    /// <summary>
    /// 设计 §6.2 **B6**（notes §15.4 的关键细节）：`PendingResumeCandidate` 必须自己发通知 ——
    /// **即使文案一字未变**。因为 Step4 的「恢复任务」按钮态与点击取候选两处都只读它，
    /// 而 `UnfinishedProbeText` 的 `Set(...)` 在文案没变时**不** Raise（判等）。
    /// </summary>
    [Fact]
    public async Task A5_7h_PendingResumeCandidateNotifies_EvenWhenTheTextIsUnchanged()
    {
        CreateResumableJob("JOB-A5-NOTIFY");
        var conn = NewConnection();
        conn.ForceConnectedForTest(true);
        var vm = NewSession(conn);
        var pump = InstallFakeProbeDebouncePump(vm);

        var candidateRaises = 0;
        var textRaises = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MigrationSessionViewModel.PendingResumeCandidate)) candidateRaises++;
            else if (e.PropertyName == nameof(MigrationSessionViewModel.UnfinishedProbeText)) textRaises++;
        };

        vm.RequestUnfinishedProbe();                 // false→true ⇒ 立即探测（真的找到候选）
        await vm.LastProbeTaskForTest!;
        Assert.Equal(1, candidateRaises);
        var firstText = vm.UnfinishedProbeText;
        Assert.Contains("JOB-A5-NOTIFY", firstText, StringComparison.Ordinal);

        // 第二次探测：同一 host、同一任务 ⇒ Core 每次都返回**新的 JobSummary 实例**，
        // 文案一字不变，但候选引用变了 ⇒ 只能靠 PendingResumeCandidate 自己的 Raise 刷到按钮。
        candidateRaises = 0;
        textRaises = 0;
        vm.RequestUnfinishedProbe();
        pump.Fire();
        await vm.LastProbeTaskForTest!;

        Assert.Equal(firstText, vm.UnfinishedProbeText);   // 文案确实没变
        Assert.Equal(0, textRaises);                       // ⇒ 文案属性不会 Raise（Set 判等）
        Assert.True(candidateRaises >= 1,
            "文案未变、候选变化时 PendingResumeCandidate 没有 Raise ⇒ Step4「恢复任务」按钮刷不到（notes §15.4）");
    }

    /// <summary>
    /// R13 的补充：去抖泵**没有装配**（离屏/单测环境、且未注入测试缝）时，
    /// `RequestUnfinishedProbe` 也必须只记诊断、**绝不外抛**（它是从 PropertyChanged 分发里调的同步入口）。
    /// </summary>
    [Fact]
    public void A5_7i_RequestWithoutAnyWiredPump_StillNeverThrows()
    {
        var conn = NewConnection();
        conn.ForceConnectedForTest(false);
        var vm = NewSession(conn);                   // 刻意不注入任何泵工厂

        var probes = 0;
        vm.UnfinishedProbeForTest = (_, _) => { probes++; return Array.Empty<JobSummary>(); };

        conn.Host = "h1";
        vm.RequestUnfinishedProbe();                 // 未连接 ⇒ 需要泵 ⇒ 取不到泵 ⇒ 只记日志，不外抛

        Assert.Equal(0, probes);
    }

    /// <summary>
    /// ★ C-C04B-1（2026-10-04 真机：LAB-DST01 硬断电 → 恢复 → 把那个被中断的任务续传完成）★
    ///
    /// 现场：**同一次会话内**跑完那个中断任务之后，Step4 的「未完成任务检测」仍是
    /// **进入本页时的探测快照**，于是同屏同时出现两句话：
    ///   ①「迁移已完成，可在本页做基础一致性检查并打开报告。…」
    ///   ②「检测到未完成的迁移任务：任务 JOB-…｜…｜Interrupted。点「恢复任务」可从断点继续…」
    /// 用户会被 ② 误导为"还要再恢复一次"（真机 C04 run5 实测；重启应用后自愈）。
    ///
    /// 本用例：① 连接后真实探测（真读盘、真候选）⇒ 出现"检测到未完成的迁移任务"；
    ///         ② 用**按真实引擎契约落盘**的假引擎把该任务跑到 Completed
    ///            （真引擎在运行结束时会自己把 `Phase` 写进 job-state.json；
    ///             不落盘的假引擎覆盖不到这条性质）；
    ///         ③ 结束后那句话必须已被**重新探测**刷新，不得继续留在屏幕上。
    ///
    /// 改前会红：`UnfinishedProbeText` 保持快照文案（`DoesNotContain` 失败）。
    /// </summary>
    [Fact]
    public async Task A5_7j_FinishRun_RefreshesTheStaleUnfinishedProbeConclusion()
    {
        var ctx = CreateResumableJob("JOB-A5-C04B");          // Phase=Interrupted，源=ProbeHost
        var conn = NewConnection();
        conn.ForceConnectedForTest(true);
        var vm = NewSession(conn);
        var pump = InstallFakeProbeDebouncePump(vm);
        vm.UnfinishedProbeForTest = (host, target) => new JobManager(_log).FindUnfinished(host, target);

        // ① 进入 Step4：真实探测报出"被中断的任务"
        vm.RequestUnfinishedProbe();
        if (pump.ArmCount == 1) pump.Fire();                  // 走了去抖分支就手动到期（无 sleep）
        var probe = vm.LastProbeTaskForTest;
        Assert.NotNull(probe);
        await probe!;
        Assert.Contains("检测到未完成的迁移任务", vm.UnfinishedProbeText, StringComparison.Ordinal);

        // ② 把这个任务跑完（假引擎按真实契约把结束阶段落盘）
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));
        vm.TransferRunner = (JobContext c, TransferEngineHooks _, IProgress<ProgressSnapshot> _,
                             CancellationToken _, IReadOnlyCollection<string>? _, bool _) =>
        {
            var st = c.LoadStateOrNew();
            st.Phase = JobPhase.Completed;
            st.Percent = 100.0;
            st.CompletedBytes = st.TotalBytes;
            c.SaveState(st);
            return Task.FromResult(JobPhase.Completed);
        };
        await WithInlineSyncContext(() => vm.ResumeAsync(password: null));
        Assert.Equal(JobPhase.Completed, vm.Phase);

        // ③ 结束态必须重新探测 ⇒ 过期结论不得留在屏幕上
        var refreshed = vm.LastProbeTaskForTest;
        Assert.NotNull(refreshed);
        Assert.False(ReferenceEquals(probe, refreshed), "任务结束后没有重新发起未完成任务探测");
        await refreshed!;
        Assert.DoesNotContain("检测到未完成的迁移任务", vm.UnfinishedProbeText, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  要求 8：TreeView 未加载根具有正确的"可展开"状态
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 行为级：SyncRoots 建出的共享根处于"未加载"状态，必须**恰好**带一个占位子节点
    /// （Step2 页面按 `!model.ChildrenLoaded` 决定给 TreeViewNode 加占位 ⇒
    ///  TreeViewNode.Children 非空，WinUI 才会画展开箭头），且重复 AddDummy 不会越堆越多。
    /// </summary>
    [Fact]
    public void A5_8a_NotYetLoadedRoot_HasExactlyOnePlaceholderChild_ForTheChevron()
    {
        var tree = NewTree();
        tree.SyncRoots(new[] { @"\\" + ProbeHost + @"\C$", @"\\" + ProbeHost + @"\D$" });

        Assert.Equal(2, tree.RootNodes.Count);
        foreach (var root in tree.RootNodes)
        {
            Assert.False(root.ChildrenLoaded);
            Assert.True(root.IsShareRoot);
            var placeholder = Assert.IsType<DirNode>(Assert.Single(root.Children));
            Assert.Equal(string.Empty, placeholder.FullPath);   // 空 FullPath = 占位节点的既有约定
            root.AddDummy();
            Assert.Single(root.Children);                       // 幂等：不得堆占位
        }
    }

    /// <summary>
    /// 行为级：**真实空目录**加载完成后不得留假箭头（P0-1 的补充约束）——
    /// ChildrenLoaded=true 且 Children 为空，且此时 AddDummy 必须是 no-op（双保险）。
    /// </summary>
    [Fact]
    public void A5_8b_ReallyEmptyDirectory_LeavesNoFakeChevron()
    {
        var empty = NewDir("empty-dir");
        var tree = NewTree();
        var root = tree.AddLocalRootForVerification(empty);

        Assert.False(root.ChildrenLoaded);
        Assert.Single(root.Children);                            // 未加载：有占位 ⇒ 能点开

        AwaitNoContext(() => tree.EnsureChildrenAsync(root));

        Assert.True(root.ChildrenLoaded);
        Assert.Empty(root.Children);                             // ★ 空目录不留假 affordance
        root.AddDummy();
        Assert.Empty(root.Children);                             // ★ 加载后 AddDummy 恒 no-op
    }

    /// <summary>行为级：真实非空目录加载后子节点就在模型里（箭头由真实内容撑起来，不靠占位）。</summary>
    [Fact]
    public void A5_8c_LoadedNonEmptyDirectory_KeepsItsRealChildren()
    {
        var dir = NewDir("nonempty-dir");
        NewDir("nonempty-dir", "A5Child");
        WriteFile(Path.Combine(dir, "A5file.txt"), "x");

        var tree = NewTree();
        var root = tree.AddLocalRootForVerification(dir);
        AwaitNoContext(() => tree.EnsureChildrenAsync(root));

        Assert.True(root.ChildrenLoaded);
        Assert.Single(root.Children.OfType<DirNode>().Where(d => d.FullPath.Length > 0));
        Assert.Single(root.Children.OfType<FileRow>());
        Assert.DoesNotContain(root.Children.OfType<DirNode>(), d => d.FullPath.Length == 0); // 没有残留占位
    }

    /// <summary>
    /// 【契约级】要求 8 的 UI 半边只能在 WinUI 页面 code-behind 里表达（TreeViewNode 需要 XAML 运行时，
    /// 本测试项目无法实例化），因此这里只断言**源码文本里的判据**。
    ///
    /// ★ 2026-09-30 更新（A.5 P0-4）★ 实现方式从"**占位子节点**"换成 WinUI 官方的
    ///   **`HasUnrealizedChildren`**：用户真实鼠标实测证明占位方案下"箭头显示但点了毫无反应"
    ///   （诊断日志 a5-expanding-trace.log 定位到：`Expanding` 是异步的，处理器返回时 Children 尚空，
    ///   TreeView 会把 `IsExpanded` 重置回 false）。
    ///   不变式保持：**未加载 ⇒ 必须有展开 affordance；已加载 ⇒ 不得留假 affordance**。
    /// </summary>
    [Fact]
    public void A5_8d_Contract_PlaceholderRuleLooksOnlyAtChildrenLoaded()
    {
        var root = FindRepoRoot();
        var page = File.ReadAllText(Path.Combine(root, "src", "PCMig.WinUI", "Views", "Step2SelectDataPage.xaml.cs"));
        var models = File.ReadAllText(Path.Combine(root, "src", "PCMig.WinUI", "Presentation", "DirTreeModels.cs"));

        // 未加载 ⇒ 用 HasUnrealizedChildren 表达"可展开"（BuildNode）
        Assert.Contains("if (!model.ChildrenLoaded) node.HasUnrealizedChildren = true;", page, StringComparison.Ordinal);
        // 已加载 ⇒ 关掉 affordance（真实空目录不留假箭头）
        Assert.Contains("node.HasUnrealizedChildren = !model.ChildrenLoaded;", page, StringComparison.Ordinal);
        // 不得回退到看 Children.Count 的旧判据
        Assert.DoesNotContain("if (model.Children.Count == 0) node.Children.Add(new TreeViewNode());", page, StringComparison.Ordinal);
        // 不得再用"占位子节点"表达未加载（那正是"点了没反应"的方案）
        Assert.DoesNotContain("if (!model.ChildrenLoaded) node.Children.Add(new TreeViewNode());", page, StringComparison.Ordinal);
        // 模型侧 AddDummy 的守卫（未变）
        Assert.Contains("if (ChildrenLoaded || Children.Count > 0) return;", models, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  问题 C（C1 / C2）：懒加载"成功后才置位 + 失败/作废后可重试 + 作废只在这些时机"
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// C1 主用例（R16）：`ChildrenLoaded` 是"**成功**读出来过"这个持久事实 ——
    /// 必须在成功路径的 UI 线程块内才置 true，且与 `Children` 集合的更换在同一次执行里完成。
    /// 同时覆盖 R15 的重入守卫：同一节点被并发展开两次，**只允许真读盘一次**。
    /// </summary>
    [Fact]
    public void A5_9a_LoadSucceeds_SetsChildrenLoadedOnce_AndConcurrentExpandDoesNotReadTwice()
    {
        var dir = NewDir("c-load");
        NewDir("c-load", "A5Child");
        WriteFile(Path.Combine(dir, "A5file.txt"), "x");

        var tree = NewTree();
        var root = tree.AddLocalRootForVerification(dir);
        Assert.False(root.ChildrenLoaded);
        Assert.Single(root.Children);                        // 未加载：有占位 ⇒ 箭头在

        // 计"UI 块真的跑过"的次数：UI 块首行必是 Children.Clear()（ObservableCollection ⇒ Reset）
        var resets = 0;
        root.Children.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++;
        };

        // 并发展开（不改动界面）：第二次必须被 _loading 守卫或 ChildrenLoaded 守卫拦下
        var first = tree.EnsureChildrenAsync(root);
        var second = tree.EnsureChildrenAsync(root);
        AwaitNoContext(() => Task.WhenAll(first, second));

        Assert.True(root.ChildrenLoaded);
        Assert.Equal(1, resets);                             // ★ 只读了一次盘、只换了一次集合（R15）
        Assert.DoesNotContain(root.Children.OfType<DirNode>(), d => d.FullPath.Length == 0);  // 没残留占位
    }

    /// <summary>
    /// C1 的核心回归（R18）：结果被版本作废时**不得**置 `ChildrenLoaded`，
    /// 于是"再点一次展开"必须能真的重读盘并成功 —— 这正是改前做不到的事
    /// （改前在 await 之前就置位 ⇒ 该节点永久不再重试、只剩 `…` 占位）。
    /// </summary>
    [Fact]
    public void A5_9b_DiscardedResult_KeepsNodeRetryable()
    {
        var dir = NewDir("c-retry");
        NewDir("c-retry", "A5Child");

        var tree = NewTree();
        var root = tree.AddLocalRootForVerification(dir);

        // ① 用一个**已经过期**的代次跑一次（等价于"读盘期间连接被重建 ⇒ 结果被作废"）
        var stale = tree.BrowseVersionForTest - 1;
        AwaitNoContext(() => tree.EnsureChildrenAsync(root, stale));

        Assert.False(root.ChildrenLoaded);                   // ★ 作废 ⇒ 不置位
        Assert.Single(root.Children);                        // 模型里仍只有那个 `…` 占位（可重试的箭头）

        // ② 用户"再点一次展开" ⇒ 必须真的重新读盘并成功
        AwaitNoContext(() => tree.EnsureChildrenAsync(root));

        Assert.True(root.ChildrenLoaded);
        Assert.Single(root.Children.OfType<DirNode>(), d => d.FullPath.Length > 0);
        Assert.DoesNotContain(root.Children.OfType<DirNode>(), d => d.FullPath.Length == 0);
    }

    /// <summary>
    /// C2 主用例（R19 / C8）：`SyncRoots` **只在真的增/删根时**才作废在途懒加载；
    /// 反复同步同一份共享集合（= 用户在 Step1/Step2 反复勾选/取消共享所走的路径）
    /// 必须**不作废**（否则"勾一个共享作废一次 ⇒ 反复展开反复白读盘"）。
    /// </summary>
    [Fact]
    public void A5_9c_SyncRoots_InvalidatesOnlyWhenTheRootSetReallyChanges()
    {
        var tree = NewTree();
        var shareA = new ShareRootSpec(@"\\" + ProbeHost + @"\C$", true);
        var shareB = new ShareRootSpec(@"\\" + ProbeHost + @"\D$", false);

        tree.SyncRoots(new[] { shareA });
        var afterFirstAdd = tree.BrowseVersionForTest;

        // 反复同步（根集合没有增删）⇒ 代次必须**一动不动**
        for (var i = 0; i < 5; i++) tree.SyncRoots(new[] { shareA });
        Assert.Equal(afterFirstAdd, tree.BrowseVersionForTest);

        // 同一份集合、只是勾选态变了（= 用户反复勾/取消共享）⇒ 仍然不得作废
        tree.SyncRoots(new[] { new ShareRootSpec(shareA.UncPath, false) });
        tree.SyncRoots(new[] { shareA });
        tree.SyncRoots(new[] { new ShareRootSpec(shareA.UncPath, false) });
        Assert.Equal(afterFirstAdd, tree.BrowseVersionForTest);

        // 真的新增一个根 ⇒ 作废一次
        tree.SyncRoots(new[] { shareA, shareB });
        Assert.Equal(afterFirstAdd + 1, tree.BrowseVersionForTest);

        // 真的删掉一个根 ⇒ 再作废一次
        tree.SyncRoots(new[] { shareA });
        Assert.Equal(afterFirstAdd + 2, tree.BrowseVersionForTest);
        Assert.Single(tree.RootNodes);
    }

    /// <summary>
    /// C1 + C2 的互补性（R18）：作废之后节点**必须仍可展开**（不会变成"永久不可展开"）。
    /// 这条把"作废能力"与"可恢复性"绑在一起断言 —— 只接线 C2 不修 C1 时本用例必红。
    /// </summary>
    [Fact]
    public void A5_9d_AfterInvalidation_TheNodeCanStillBeExpanded()
    {
        var dir = NewDir("c-invalidate");
        NewDir("c-invalidate", "A5Child");

        var tree = NewTree();
        var root = tree.AddLocalRootForVerification(dir);
        AwaitNoContext(() => tree.EnsureChildrenAsync(root));
        Assert.True(root.ChildrenLoaded);

        // 连接重建（C2）：作废在途加载 —— 已成功加载过的节点**不会被重置**（已登记的范围边界）
        tree.InvalidateInFlightBrowses();
        Assert.True(root.ChildrenLoaded);
        Assert.Equal(1, tree.BrowseVersionForTest);

        // 在新代次上重新展开：结果属于新代次 ⇒ 正常落地
        root.ChildrenLoaded = false;                         // 模拟"重连后由页面/探针要求重读"
        AwaitNoContext(() => tree.EnsureChildrenAsync(root));
        Assert.True(root.ChildrenLoaded);
        Assert.Single(root.Children.OfType<DirNode>(), d => d.FullPath.Length > 0);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
        throw new InvalidOperationException("找不到含 PCMig.sln 的仓库根：" + AppContext.BaseDirectory);
    }
}