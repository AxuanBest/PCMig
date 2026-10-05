// ============================================================================
//  FIX BATCH 6（PCMig Trust-Critical Recovery）— 状态消息路由测试 SR-01…SR-10
// ============================================================================
//
//  ── 为什么这些测试必须存在（真机证据）────────────────────────────────────────
//  真机 200+ GB 验收时（JOB-20261004-171522-b785 现场）：
//    · 整会话只有一根 StatusMessage，业务运行状态、用户操作引导、安全提示、错误摘要
//      全部挤进同一个字符串，并被绑到右侧执行卡 —— 长状态句（计划已生成 + 空间守护 +
//      锁与 EFS + 超大规模说明）把执行卡无限撑高；
//    · 左侧「提示」卡却永远显示冻结文案「就绪 / 暂无报错」，与真实状态无关（假绿）；
//    · 「连接与安全提示」卡同样与真实安全状态无关（缺密码这类真提示只出现在右侧长句里）。
//  ⇒ 本文件把 §9 的**五条语义通道 + 面板映射**钉成可执行断言。
//
//  ── 怎么够得着 WinUI 的类型 ────────────────────────────────────────────────
//  与 PU-01…PU-08 同一策略：`PCMig.Core.Tests.csproj` 把 `src\PCMig.WinUI\Presentation\*.cs`
//  源码链入编译（含 MigrationSessionViewModel 与 StatusChannel），因此这里测的是**产品真实的
//  会话状态机**；传输引擎一律通过产品自带的测试缝 `MigrationSessionViewModel.TransferRunner`
//  注入假实现 ⇒ 本文件永不启动 robocopy、不触网。
//
//  作业目录（PCMIG_JOBS）隔离：与 A5/PU 同 collection（串行），避免并发改写进程级环境变量。
// ============================================================================

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PCMig.Core.Jobs;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using PCMig.WinUI.Presentation;
using Serilog;
using Xunit;

namespace PCMig.Core.Tests;

[Collection("A5-jobdir-isolation")]
public sealed class Batch6StatusRoutingTests : IDisposable
{
    private const string ProbeHost = "SR-PROBE-HOST";

    private readonly string _sandbox;
    private readonly ILogger _log;
    private readonly string? _previousJobsRoot;

    public Batch6StatusRoutingTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "pcmig-status-routing", Guid.NewGuid().ToString("N"));
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

    private MigrationSessionViewModel NewSession() => new(null, null, _log);

    private JobContext CreateJob(string jobId, string? sourceUser = null)
    {
        var def = new JobDefinition
        {
            JobId = jobId,
            SourceHost = ProbeHost,
            SourceUser = sourceUser ?? string.Empty,
            TargetRoot = NewDir("target", jobId),
            Sources = { new SourceSpec { Path = @"\\" + ProbeHost + @"\C$", Kind = ObjectKind.DataVolume } },
            Options = new MigrationOptions { Threads = 16 },
        };
        var plan = new MigrationPlan
        {
            JobId = jobId,
            TotalBytes = 4096,
            Objects =
            {
                new PlannedObject
                {
                    ObjectId = "object-000001",
                    Kind = ObjectKind.DataVolume,
                    SourcePath = NewDir("src", jobId),
                    TargetPath = NewDir("src", jobId),
                    EstimatedBytes = 4096,
                    EstimatedFiles = 1,
                },
            },
        };
        var ctx = new JobManager(_log).Create(def);
        ctx.SavePlan(plan);
        ctx.SaveState(new JobState
        {
            JobId = jobId,
            Phase = JobPhase.Running,
            TotalObjects = 1,
            CompletedObjects = 0,
            TotalBytes = 4096,
            CompletedBytes = 0,
        });
        return ctx;
    }

    private static ProgressSnapshot Snap(
        JobPhase phase = JobPhase.Running,
        string message = "正在传输：object-000002",
        string? currentObjectId = "object-000002",
        string? currentObjectPath = null)
        => new(phase, 3, 1, 0, 300L * 1024 * 1024, 100L * 1024 * 1024,
            33.3, 12_000_000, 600, currentObjectId,
            currentObjectPath ?? @"\\" + ProbeHost + @"\C$\data", message,
            0, 0, false, 0, PauseState.None, null, null, PauseOutcomeKind.None, null);

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

    /// <summary>跑一次假运行：<paramref name="body"/> 在引擎内部执行（此刻 IsRunning == true）。</summary>
    private static async Task RunScriptedAsync(
        MigrationSessionViewModel vm,
        Func<IProgress<ProgressSnapshot>, Task> body,
        JobPhase result = JobPhase.Paused)
    {
        vm.TransferRunner = (_, _, progress, _, _, _) =>
        {
            body(progress).GetAwaiter().GetResult();
            return Task.FromResult(result);
        };
        await WithInlineSyncContext(() => vm.RunAsync(password: null));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
        throw new InvalidOperationException("找不到含 PCMig.sln 的仓库根：" + AppContext.BaseDirectory);
    }

    private static string ReadRepoFile(string relative)
        => File.ReadAllText(Path.Combine(FindRepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>去掉注释行后的代码文本（SR-09 用：注释里提到业务字段名不算"自己造句"）。</summary>
    private static string StripComments(string text)
        => string.Join('\n', text.Split('\n')
            .Select(line => line.TrimStart())
            .Where(line => !line.StartsWith("//", StringComparison.Ordinal)));

    // ══════════════════════════ SR-01 ══════════════════════════

    /// <summary>五条通道必须存在，且初始互不污染；执行卡短句有初始文案（不是空串）。</summary>
    [Fact]
    public void SR01_FiveChannelsExistAndStartEmpty()
    {
        var vm = NewSession();

        Assert.Equal(string.Empty, vm.OperationalStatus);
        Assert.Equal(string.Empty, vm.UserHint);
        Assert.Equal(string.Empty, vm.SecurityHint);
        Assert.Equal(string.Empty, vm.ErrorSummary);
        Assert.Equal(string.Empty, vm.CurrentObjectStatus);

        // 执行卡短句与聚合句在没有任何动作时也有诚实文案（界面不出现空白状态行）。
        Assert.False(string.IsNullOrWhiteSpace(vm.ExecutionStatus));
        Assert.Equal(vm.StatusMessage, vm.ExecutionStatus);

        // 通道枚举是"语义五通道"，不是随手加的字符串开关。
        Assert.Equal(5, Enum.GetValues<StatusChannel>().Length);
        Assert.Contains(StatusChannel.OperationalStatus, Enum.GetValues<StatusChannel>());
        Assert.Contains(StatusChannel.UserHint, Enum.GetValues<StatusChannel>());
        Assert.Contains(StatusChannel.SecurityHint, Enum.GetValues<StatusChannel>());
        Assert.Contains(StatusChannel.ErrorSummary, Enum.GetValues<StatusChannel>());
        Assert.Contains(StatusChannel.CurrentObjectStatus, Enum.GetValues<StatusChannel>());
    }

    // ══════════════════════════ SR-02 ══════════════════════════

    /// <summary>运行快照的文案只进 OperationalStatus；错误摘要与用户引导通道不被污染。</summary>
    [Fact]
    public async Task SR02_EngineMessageRoutesToOperationalChannelOnly()
    {
        var ctx = CreateJob("JOB-SR-02");
        var vm = NewSession();
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        // 载入任务本身会写一条用户引导（"已载入任务…"）——先记下它的原值，
        // 之后断言**只有运行状态通道**被引擎文案改写。
        var hintBefore = vm.UserHint;

        string? operational = null, error = null, hint = null, aggregate = null, shortLine = null;
        await RunScriptedAsync(vm, progress =>
        {
            progress.Report(Snap(message: "正在传输：object-000002（大文件直传）"));
            operational = vm.OperationalStatus;
            error = vm.ErrorSummary;
            hint = vm.UserHint;
            aggregate = vm.StatusMessage;
            shortLine = vm.ExecutionStatus;
            return Task.CompletedTask;
        });

        Assert.Contains("正在传输", operational);
        Assert.Equal(string.Empty, error);
        Assert.Equal(hintBefore, hint);          // 引导通道不被引擎运行文案污染
        // 聚合句仍是最近一次通道写入（既有消费者口径不变），执行卡短句由它派生。
        Assert.Equal(operational, aggregate);
        Assert.Equal(MigrationSessionViewModel.Shorten(operational), shortLine);
    }

    // ══════════════════════════ SR-03 ══════════════════════════

    /// <summary>
    /// 执行卡短句必须是**单行 + 限长**：长状态句（换行 + 超长）压平、截到第一句并加省略号，
    /// 而完整文本仍留在 OperationalStatus（左侧面板）。
    /// 这一条正是"右侧执行卡被长句无限撑高"的回归钉。
    /// </summary>
    [Fact]
    public async Task SR03_ExecutionStatusIsShortSingleLineWhileChannelKeepsFullText()
    {
        var ctx = CreateJob("JOB-SR-03");
        var vm = NewSession();
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        var longMessage = "计划已生成（Job JOB-SR-03）：1 个对象，共 4 KiB" + new string('说', 160) +
                          Environment.NewLine + "空间守护：目标盘剩余空间充足。";
        string? channelDuringRun = null, shortDuringRun = null;
        await RunScriptedAsync(vm, progress =>
        {
            progress.Report(Snap(JobPhase.Running, longMessage));
            channelDuringRun = vm.OperationalStatus;
            shortDuringRun = vm.ExecutionStatus;
            return Task.CompletedTask;
        });

        Assert.Equal(longMessage, channelDuringRun);                         // 完整文本仍在通道里
        Assert.DoesNotContain('\n', shortDuringRun!);                        // 单行
        Assert.DoesNotContain('\r', shortDuringRun!);
        Assert.True(shortDuringRun!.Length <= MigrationSessionViewModel.ExecutionStatusMaxChars + 1,
            $"执行卡短句过长：{shortDuringRun.Length}");
        Assert.EndsWith("…", shortDuringRun);                                // 明确"被截断"，不假装完整
        Assert.StartsWith("计划已生成", shortDuringRun);

        // 运行收尾（本夹具以 Paused 结束）后短句仍必须是短句 —— 短句纪律不只在运行中成立。
        Assert.DoesNotContain('\n', vm.ExecutionStatus);
        Assert.True(vm.ExecutionStatus.Length <= MigrationSessionViewModel.ExecutionStatusMaxChars + 1);

        // 两条压缩规则各自成立：① 短句优先在第一句处断开（不是硬截）；② 无句号时按 96 字硬截并加 '…'。
        Assert.Equal("计划已生成（Job JOB-SR-03）：1 个对象，共 4 KiB。",
            MigrationSessionViewModel.Shorten("计划已生成（Job JOB-SR-03）：1 个对象，共 4 KiB。空间守护：目标盘剩余空间充足。"));
        var hardCut = MigrationSessionViewModel.Shorten(new string('说', 200));
        Assert.Equal(MigrationSessionViewModel.ExecutionStatusMaxChars + 1, hardCut.Length);
        Assert.EndsWith("…", hardCut);
    }

    // ══════════════════════════ SR-04 ══════════════════════════

    /// <summary>没有任务时的"不能验证"是**用户引导**，必须进 UserHint，而不是混进运行状态。</summary>
    [Fact]
    public async Task SR04_UserGuidanceRoutesToHintChannel()
    {
        var vm = NewSession();
        await WithInlineSyncContext(() => vm.VerifyAsync());

        Assert.Contains("尚无任务", vm.UserHint);
        Assert.Equal(string.Empty, vm.OperationalStatus);
        Assert.Equal(string.Empty, vm.ErrorSummary);
    }

    // ══════════════════════════ SR-05 ══════════════════════════

    /// <summary>目标路径非法字符是**错误摘要**通道（用户必须能在左栏一眼看到失败原因）。</summary>
    [Fact]
    public async Task SR05_PrepareFailureRoutesToErrorChannel()
    {
        var vm = NewSession();
        var def = new JobDefinition
        {
            JobId = "JOB-SR-05",
            SourceHost = ProbeHost,
            TargetRoot = NewDir("target", "sr05") + Path.DirectorySeparatorChar + "bad\u0001name",
            Sources = { new SourceSpec { Path = @"\\" + ProbeHost + @"\C$", Kind = ObjectKind.DataVolume } },
            Options = new MigrationOptions { Threads = 16 },
        };

        var ok = await vm.PrepareAsync(def, password: null);

        Assert.False(ok);
        Assert.Contains("非法字符", vm.ErrorSummary);
        Assert.Equal(string.Empty, vm.UserHint);      // 引导通道不被错误串污染
    }

    // ══════════════════════════ SR-06 ══════════════════════════

    /// <summary>
    /// 未提供密码而任务带账号 → 这条**安全提示**必须走 SecurityHint 通道
    /// （旧实现把它混在业务状态句里，右侧执行卡既看不见重点、安全卡又永远显示冻结文案）。
    /// </summary>
    [Fact]
    public async Task SR06_SecurityNoticeRoutesToSecurityChannel()
    {
        var ctx = CreateJob("JOB-SR-06", sourceUser: "SR-USER");
        var vm = NewSession();
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        string? security = null, operational = null;
        await RunScriptedAsync(vm, progress =>
        {
            security = vm.SecurityHint;
            operational = vm.OperationalStatus;
            return Task.CompletedTask;
        });

        Assert.Contains("未提供密码", security);
        Assert.DoesNotContain("未提供密码", operational);   // 安全提示不混进运行状态通道
    }

    // ══════════════════════════ SR-07 ══════════════════════════

    /// <summary>
    /// 当前对象通道只由**引擎快照**写入：运行中显示对象 id，运行结束（暂停/完成）即清空——
    /// 不允许 UI 自己从路径反推、也不允许结束后残留"当前对象"。
    /// </summary>
    [Fact]
    public async Task SR07_CurrentObjectChannelFollowsEngineTruth()
    {
        var ctx = CreateJob("JOB-SR-07");
        var vm = NewSession();
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        string? during = null;
        await RunScriptedAsync(vm, progress =>
        {
            progress.Report(Snap(JobPhase.Running, currentObjectId: "object-000007",
                currentObjectPath: @"\\" + ProbeHost + @"\C$\seven"));
            during = vm.CurrentObjectStatus;
            return Task.CompletedTask;
        });

        Assert.Contains("object-000007", during);
        Assert.Contains(@"\\" + ProbeHost + @"\C$\seven", during);
        // 运行以 Paused 结束 ⇒ 引擎不再有"当前对象"，通道必须清空（不是残留旧值）。
        Assert.Equal(string.Empty, vm.CurrentObjectStatus);
    }

    // ══════════════════════════ SR-08 ══════════════════════════

    /// <summary>面板映射（§9）：左栏读四通道、安全卡读 SecurityHint、执行卡读短句且限行。</summary>
    [Fact]
    public void SR08_PanelsReadTheSemanticChannels()
    {
        // 右侧执行卡：读短句 + 限行截断，不再直接绑定聚合句。
        var step2Cs = ReadRepoFile("src/PCMig.WinUI/Views/Step2SelectDataPage.xaml.cs");
        Assert.Contains("StateMessageText.Text = session.ExecutionStatus;", step2Cs);
        Assert.Contains("SecurityHintText.Text = session.SecurityHint", step2Cs);
        Assert.DoesNotContain("StateMessageText.Text = session.StatusMessage", step2Cs);

        var step2Xaml = ReadRepoFile("src/PCMig.WinUI/Views/Step2SelectDataPage.xaml");
        Assert.Contains("x:Name=\"StateMessageText\"", step2Xaml);
        Assert.Contains("MaxLines=\"2\"", step2Xaml);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", step2Xaml);
        Assert.Contains("x:Name=\"SecurityHintText\"", step2Xaml);

        // 左栏提示卡：投影四条通道（安全提示按映射不进本卡）。
        var hintCs = ReadRepoFile("src/PCMig.WinUI/Views/ShellHintCard.xaml.cs");
        Assert.Contains(nameof(MigrationSessionViewModel.OperationalStatus), hintCs);
        Assert.Contains(nameof(MigrationSessionViewModel.UserHint), hintCs);
        Assert.Contains(nameof(MigrationSessionViewModel.ErrorSummary), hintCs);
        Assert.Contains(nameof(MigrationSessionViewModel.CurrentObjectStatus), hintCs);
        Assert.DoesNotContain("." + nameof(MigrationSessionViewModel.SecurityHint), hintCs);

        var hintXaml = ReadRepoFile("src/PCMig.WinUI/Views/ShellHintCard.xaml");
        Assert.Contains("x:Name=\"HintOperationalText\"", hintXaml);
        Assert.Contains("x:Name=\"HintObjectText\"", hintXaml);
        Assert.Contains("x:Name=\"HintUserText\"", hintXaml);
        Assert.Contains("x:Name=\"HintErrorText\"", hintXaml);
        Assert.Contains("x:Name=\"HintStatusText\"", hintXaml);

        // Shell 层接线：MainWindow 把同一会话交给提示卡（四页共用，不各写一份）。
        Assert.Contains("HintCard.Attach(Session);", ReadRepoFile("src/PCMig.WinUI/MainWindow.xaml.cs"));
    }

    // ══════════════════════════ SR-09 ══════════════════════════

    /// <summary>
    /// 状态源纪律（§9）：文案的唯一来源是 ViewModel 的通道属性；
    /// 提示卡**不得**根据业务字段（Phase / Percent …）自己造句。
    /// </summary>
    [Fact]
    public void SR09_HintCardDoesNotComposeBusinessStatus()
    {
        var hintCs = ReadRepoFile("src/PCMig.WinUI/Views/ShellHintCard.xaml.cs");
        // 只对**代码**断言：注释里出现字段名（例如"Percent/ProgressText 这类高频道一律忽略"）不算造句。
        var hintCode = StripComments(hintCs);
        Assert.DoesNotContain(".Phase", hintCode);
        Assert.DoesNotContain("Percent", hintCode);
        Assert.DoesNotContain("IsRunning", hintCode);
        Assert.DoesNotContain("JobPhase", hintCode);

        // 旧冻结文案不得复活：状态行文案由 ErrorSummary 通道决定。
        Assert.Contains("有需要处理的项（见上）", hintCs);
        Assert.Contains("暂无报错", hintCs);
    }

    // ══════════════════════════ SR-10 ══════════════════════════

    /// <summary>
    /// 写入者纪律：五条通道各有**唯一**写入者（VM 的 SetXxx / SetChannel），
    /// 且 VM 里不再存在裸 StatusMessage 赋值（那是"一根字符串绑所有地方"的旧形态）。
    /// </summary>
    [Fact]
    public void SR10_SingleWriterPerChannelAndNoBareStatusMessageAssignment()
    {
        var vm = ReadRepoFile("src/PCMig.WinUI/Presentation/MigrationSessionViewModel.cs");

        foreach (var writer in new[] { "SetOperational(", "SetUserHint(", "SetSecurityHint(", "SetErrorSummary(", "AppendOperational(" })
            Assert.Contains(writer, vm);
        Assert.Contains("SetChannel(StatusChannel.CurrentObjectStatus,", vm);
        Assert.Contains("private void SetChannel(StatusChannel channel, string? text)", vm);

        // 唯一允许的聚合赋值在 SetChannel 内部（_statusMessage 字段直写 + Raise）。
        var assignments = vm.Split('\n').Count(line =>
            line.Contains("StatusMessage =", StringComparison.Ordinal) && !line.Contains("//", StringComparison.Ordinal));
        Assert.Equal(0, assignments);

        // 通道定义必须是枚举（语义化），不是散落的字符串常量。
        Assert.Contains("public enum StatusChannel", ReadRepoFile("src/PCMig.WinUI/Presentation/StatusChannel.cs"));
    }
}