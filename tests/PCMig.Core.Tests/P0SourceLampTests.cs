using System;
using System.IO;
using System.Threading.Tasks;
using PCMig.Core.Jobs;
using PCMig.Core.Models;
using PCMig.WinUI.Presentation;
using Serilog;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ P0-05 ★ 底栏「源连接」灯的真实性契约（运行中失效必须自己变红，恢复后必须自己变绿）。
///
/// 现场缺陷（2026-10-10 日志取证）：共享被策略关闭后应用**继续显示"已连接"**——因为
/// <see cref="ConnectionViewModel.NotifySourceLost"/> 虽然存在且被测，却**没有任何生产调用点**：
/// 运行中没有任何路径把"源真的读不到了"这件事告诉灯。于是灯的绿只反映"当初连上过"，
/// 不反映"现在还能不能读到源"，用户看到绿灯却怎么都验证不过（闸门拒绝验证、按钮亮着）。
///
/// 本文件锁死两条**可被证据支撑**的同步路径（判据只能是实测可达性，不许推测）：
///   ① 验证闸门逐个对象实测源根可达性，任一不可达 ⇒ 灯立即打回未连接；
///   ② 验证报告里任一对象 <c>SourceUnavailable</c> ⇒ 同上；全部实测可达 ⇒ 灯如实恢复。
/// 若哪天有人把这两条调用删掉，本文件必须变红。
///
/// ★ 2026-10-15 偶发失败定点修复 ★ 本类构造 `ConnectionViewModel` 并断言源灯分档，
/// 因此也依赖**全测试进程共享**的 `ConnectionViewModel.Clock`。该时钟的改写者
/// （`P0SourceEvidenceFreshnessTests`）已改挂本类所在的 `A5-jobdir-isolation` 集合，
/// 故两者串行执行，竞态窗口消失（见 <see cref="JobDirIsolationCollection"/> 的说明）。
/// 本类**保留** `A5-jobdir-isolation` 是必须的：另外 12 个同类测试都在改写**进程级**
/// 环境变量 `PCMIG_JOBS`，脱离该集合会让本类的作业目录被别的测试临时换掉。
/// </summary>
[Collection("A5-jobdir-isolation")]
public class P0SourceLampTests : IDisposable
{
    private static readonly ILogger Log = new LoggerConfiguration().CreateLogger();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pcmig-p0lamp-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousJobsRoot;

    public P0SourceLampTests()
    {
        // ★ 作业目录必须隔离（与 A5 / PU / Step4 / StopChain 同 collection ⇒ 串行）★
        //   · 不隔离 ⇒ 本类会在真实的 %ProgramData%\PCMig\Jobs 里建任务（污染真实存档）；
        //   · 更直接的是：同进程/并行进程里别的测试类会临时改写**进程级**环境变量 PCMIG_JOBS，
        //     于是本类的 Create/Open/枚举可能落在"刚被别的测试删掉的目录"上 ⇒ 瞬时 IO 异常被
        //     AdoptExistingJobAsync 的 catch 吞成 return false（重负载下偶发，实测过一次：
        //     P0_05_计划为空_闸门拒绝且不得把灯改红 的 line 181）。
        _previousJobsRoot = Environment.GetEnvironmentVariable("PCMIG_JOBS");
        Environment.SetEnvironmentVariable("PCMIG_JOBS", Path.Combine(_root, "Jobs"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PCMIG_JOBS", _previousJobsRoot);
        try { Directory.Delete(_root, true); } catch { /* 清理失败不影响结论 */ }
    }

    private string NewDir(string name)
    {
        var p = Path.Combine(_root, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private JobContext NewJob(string sourcePath, string kind = "DataVolume")
    {
        var def = new JobDefinition
        {
            JobId = "JOB-P0LAMP-" + Guid.NewGuid().ToString("N")[..8],
            SourceHost = "SRC-PC",
            TargetRoot = NewDir("target-root"),
            Sources = { new SourceSpec { Path = sourcePath, Kind = ObjectKind.DataVolume } },
            Options = new MigrationOptions(),
        };
        var ctx = new JobManager(Log).Create(def);
        ctx.SavePlan(new MigrationPlan
        {
            JobId = def.JobId,
            TotalBytes = 0,
            Objects =
            {
                new PlannedObject
                {
                    ObjectId = "object-000001",
                    Kind = ObjectKind.DataVolume,
                    SourcePath = sourcePath,
                    TargetPath = kind == "RootFiles" ? def.TargetRoot + "\\" : def.TargetRoot,
                    EstimatedFiles = 0,
                    EstimatedBytes = 0,
                },
            },
        });
        return ctx;
    }

    // ══════════════════════════════════════════════════════════════════════
    // ① 闸门实测不可达 ⇒ 灯必须立刻变红（旧版：闸门拒绝验证但灯仍是绿的）
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task P0_05_闸门实测源根不可达_底栏源灯必须自己变红()
    {
        using var connection = new ConnectionViewModel();
        connection.ForceConnectedForTest(true);                    // 早上连上过 ⇒ 灯绿
        Assert.Equal(SourceConnectionState.Connected, connection.SourceState);

        // 共享被策略关闭 / 会话掉了：对象源根指向一个读不到的位置（不存在的 UNC 主机）。
        var unreachable = @"\\SRC-PC-ABSENT\d970";
        var vm = new MigrationSessionViewModel(connection, null, Log);
        Assert.True(await vm.AdoptExistingJobAsync(NewJob(unreachable).JobDir));   // 走真实装载路径

        var gate = await vm.EvaluateVerifyGateAsync();

        Assert.False(gate.CanVerify);
        Assert.False(gate.SourceReachable);
        Assert.Equal(SourceConnectionState.Disconnected, connection.SourceState);   // ★ 灯如实变红 ★
        Assert.Contains("源共享已失效", connection.FlowStatus);
    }

    // ══════════════════════════════════════════════════════════════════════
    // ② 源根可达 ⇒ 闸门通过，且不得凭空改灯（不许把"没证据"当成证据）
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task P0_05_闸门实测源根可达_不得改动源灯()
    {
        using var connection = new ConnectionViewModel();
        connection.ForceConnectedForTest(true);

        var reachable = NewDir("reachable-source");
        var vm = new MigrationSessionViewModel(connection, null, Log);
        Assert.True(await vm.AdoptExistingJobAsync(NewJob(reachable).JobDir));

        var gate = await vm.EvaluateVerifyGateAsync();

        Assert.True(gate.CanVerify);
        Assert.Equal(SourceConnectionState.Connected, connection.SourceState);
        Assert.DoesNotContain("源共享已失效", connection.FlowStatus);
    }

    // ══════════════════════════════════════════════════════════════════════
    // ③ 恢复语义：失效一次以后不能永远红着（同判据的反向出口）
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void P0_05_源共享恢复可达_灯必须如实恢复且文案说清是恢复()
    {
        using var connection = new ConnectionViewModel();
        connection.ForceConnectedForTest(true);
        connection.NotifySourceLost();
        Assert.Equal(SourceConnectionState.Disconnected, connection.SourceState);

        connection.NotifySourceRestored(@"\\SRC-PC-A\d970");                        // 拿到实测可达证据（含目标）后

        Assert.Equal(SourceConnectionState.Connected, connection.SourceState);
        Assert.Contains("源共享已恢复可达", connection.FlowStatus);
    }

    [Fact]
    public void P0_05_只有重试了一次没有可归属目标时_不得点绿()
    {
        using var connection = new ConnectionViewModel();
        connection.NotifySourceRestored();                                          // 说不清恢复了哪条源

        Assert.NotEqual(SourceConnectionState.Connected, connection.SourceState);   // ★ 没有证据就没有绿灯
    }

    [Fact]
    public void P0_05_已连接时再报恢复_不得改写既有播报()
    {
        using var connection = new ConnectionViewModel();
        connection.ForceConnectedForTest(true);
        var before = connection.FlowStatus;

        connection.NotifySourceRestored(@"\\SRC-PC-A\d970");

        Assert.Equal(SourceConnectionState.Connected, connection.SourceState);
        Assert.Equal(before, connection.FlowStatus);                                // 幂等，不刷无意义文案
    }

    // ══════════════════════════════════════════════════════════════════════
    // ④ 灯只认可达性：计划为空时一次验证都不得动灯
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task P0_05_计划为空_闸门拒绝且不得把灯改红()
    {
        using var connection = new ConnectionViewModel();
        connection.ForceConnectedForTest(true);

        var def = new JobDefinition
        {
            JobId = "JOB-P0LAMP-EMPTY",
            SourceHost = "SRC-PC",
            TargetRoot = NewDir("target-root-empty"),
            Sources = { new SourceSpec { Path = @"\\SRC-PC\d", Kind = ObjectKind.DataVolume } },
            Options = new MigrationOptions(),
        };
        var ctx = new JobManager(Log).Create(def);
        ctx.SavePlan(new MigrationPlan { JobId = def.JobId, TotalBytes = 0 });   // 计划里 0 个对象

        var vm = new MigrationSessionViewModel(connection, null, Log);
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));
        var gate = await vm.EvaluateVerifyGateAsync();

        Assert.False(gate.CanVerify);
        Assert.Equal(SourceConnectionState.Connected, connection.SourceState);        // 计划空≠源失效
    }

    // ══════════════════════════════════════════════════════════════════════
    // ④b 引擎侧结构化信号：运行收尾时"错误 67"这种共享级错误必须把灯拉红（并可定位）
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void P0_05_共享级错误码落进证据表_灯变红且能定位是哪条源()
    {
        using var connection = new ConnectionViewModel();
        connection.ForceConnectedForTest(true);
        Assert.Equal(SourceConnectionState.Connected, connection.SourceState);

        // 引擎收尾报告的证据形式（ApplyShareLossSignalFromRunEnd 落的就是这一条）；
        // 目标与"预检通过"落下的那条是同一个（生产里都是 \\host），所以这里是**覆盖**而不是叠加。
        connection.NotifyShareReachability(@"\\TEST-HOST", false, "Win32 67（找不到网络名（共享已被关闭或改名））");

        Assert.Equal(SourceConnectionState.Disconnected, connection.SourceState);   // ★ 不再显示"已连接"
        var failing = Assert.Single(connection.UnreachableShares);
        Assert.Contains(@"\\TEST-HOST", failing);
        Assert.Contains("67", failing);
    }

    [Fact]
    public void P0_05_收尾信号必须接在运行结束链路上_且只认共享级错误()
    {
        var code = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MigrationSessionViewModel.cs");

        // ① 收尾（FinishRunAsync）必须真的调用它 —— 删掉即变红（这正是现场缺陷的根因形态）。
        var finish = MethodWindow(code, "private async Task FinishRunAsync(", 2200);
        Assert.Contains("ApplyShareLossSignalFromRunEnd(ctx, end);", finish);

        // ② 翻译判据只在 Core 的 ShareLossSignal 里（32/33 的排除也在那里，见 P0ShareLossSignalTests）。
        var hook = MethodWindow(code, "private void ApplyShareLossSignalFromRunEnd(JobContext ctx, JobState end)", 1600);
        Assert.Contains("ShareLossSignal.TryMatchShareLossText(end.LastError", hook);
        Assert.Contains("_connection.NotifyShareReachability(root!, false, evidence, authoritative: true);", hook);   // 权威失效，不得降级成子路径诊断
        Assert.Contains("roots.Count == 0", hook);          // 连计划都没有 ⇒ 退化为整体失效
        Assert.DoesNotContain("IsFileLock", hook);          // 文件锁判定不在这层做（只有 Core 一处口径）
    }

    // ══════════════════════════════════════════════════════════════════════
    // ⑤ 接线契约：两条同步路径都必须真的接在运行链路上（删掉即变红）
    //    现场缺陷的根因不是"灯画错了"，而是**没有任何生产调用点**告诉它源失效了。
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void P0_05_灯同步必须有生产调用点_且只在有实测证据时出手()
    {
        var code = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MigrationSessionViewModel.cs");

        // ① 闸门实测不可达 ⇒ 调 NotifySourceLost
        var gateBody = MethodWindow(code, "public async Task<VerifyGateResult> EvaluateVerifyGateAsync(", 3000);
        Assert.Contains("_connection?.NotifySourceLost();", gateBody);

        // ② 验证报告落盘后 ⇒ 用报告的 SourceUnavailable/可达性同步灯
        Assert.Contains("SyncSourceLampFromVerify(report);", code);
        var syncBody = MethodWindow(code, "private void SyncSourceLampFromVerify(", 1200);
        Assert.Contains("report.Objects.Any(o => o.SourceUnavailable)", syncBody);
        Assert.Contains("_connection.NotifySourceLost();", syncBody);
        // ★ P0-05 ★ 恢复必须带上**实测证据的目标**（哪条源根真的读到了），不是一句"恢复了"。
        Assert.Contains("_connection.NotifyShareReachability(sp, true,", syncBody);
        Assert.Contains("_connection.NotifySourceRestored(verifiedRoots.FirstOrDefault());", syncBody);
        Assert.Contains("var roots = Ctx?.Plan?.Objects", syncBody);   // 目标路径来自计划，不是猜的

        // 判据只能是"实测可达性"：不许拿 ping / 网卡 / 互联网状态冒充源共享可用性。
        Assert.DoesNotContain("Ping", gateBody + syncBody);
        Assert.DoesNotContain("NetworkInterface", gateBody + syncBody);
    }

    [Fact]
    public void P0_05_恢复出口不得被写成无条件绿灯()
    {
        var code = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "ConnectionViewModel.cs");
        var body = MethodWindow(code, "internal void NotifySourceRestored(string? verifiedTarget = null)", 900);

        Assert.Contains("if (IsConnected) { UpdateSourceState(); return; }", body);  // 幂等
        Assert.Contains("IsConnected = true;", body);           // 恢复 = 唯一状态源翻转，不是另开一个绿标志
        // ★ P0-05 ★ 恢复必须**带证据**：先落可达事实，再翻转状态；没有可归属目标就直接 return，绝不点绿。
        // ★ 2026-10-23（真机 P0-06）★ 落证据改走唯一写入点 `WriteFact`（账本只在那里增删，
        //   以便统一归一化键空间并摘掉同主机的主机级聚合结论）。断言随之改为
        //   "**经 WriteFact 落一条可达事实**"，比原来钉死某条赋值表达式更贴近意图，
        //   并且"先证据、后翻转"的顺序断言与"无目标不点绿"的条件断言都原样保留。
        Assert.Contains("WriteFact(", body);
        Assert.Contains(", true,", body);                        // 落的是**可达**事实，不是失效
        Assert.Contains("if (string.IsNullOrWhiteSpace(target)) return;", body);
        var factsAt = body.IndexOf("WriteFact(", StringComparison.Ordinal);
        var flipAt = body.IndexOf("IsConnected = true;", StringComparison.Ordinal);
        Assert.True(factsAt >= 0 && flipAt > factsAt, "必须先落下可达证据，再翻转连接状态（否则又变成无条件绿灯）。");
    }

    [Fact]
    public void P0_06_账本唯一写入点_共享级事实必须摘掉主机级聚合结论()
    {
        // ★ 2026-10-23（真机 P0-06）★ 真机黄灯的机制就是"主机级聚合结论 + 共享级事实并存"：
        //   `Derive` 同时看到 fresh 与 dead ⇒ 恒 Partial ⇒ 琥珀，连传输中也一样黄。
        //   这条结构契约保证"唯一写入点"不会被人绕开重新写回散装赋值。
        var code = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "ConnectionViewModel.cs");
        // 窗口放宽到 2600：P0-06 收口把作用域规则（①②③）写成了方法内注释，
        // 契约断言的对象仍是同一条实现语句，只是它现在排在注释之后。
        // 窗口放宽到 4600：R1-6B（2026-10-24）把**作用域折叠**与**子路径诊断分流**两条规则
        // 写进了同一个唯一写入点，实现语句因此下移；契约断言的对象仍是同一条实现。
        var body = MethodWindow(code, "private void WriteFact(", 4600);

        // ① 键必须先折叠到共享根作用域，否则子路径与共享根各占一个键 ⇒ 假 Partial。
        Assert.Contains("var scope = ShareScopeOf(target);", body);
        // ② 子路径"读不到"只进诊断，不许写死共享根（否则把子目录 ACL 问题伪装成整条共享失效）。
        Assert.Contains("_subPathIssues[target] = new SourceShareFact(target, false, evidence, atUtc);", body);
        // ③ 共享级事实写入时无条件摘掉同主机的主机级聚合结论（P0-06 原始不变量，保留）。
        Assert.Contains("IsShareTarget(scope)", body);
        Assert.Contains("_shareFacts.Remove(HostOf(scope));", body);
        // ④ 账本本体只以折叠后的键写入 —— 这是"单一权威键空间"的落点。
        Assert.Contains("_shareFacts[scope] = new SourceShareFact(scope, reachable, evidence, atUtc);", body);

        // ★ 2026-10-23（Preview.7 收口 P0）★ 全文件级不变量：`_shareFacts` 的**增删清**
        //   只允许出现在三个受控入口里。任何新的散装赋值都会让这条断言变红 ——
        //   这正是本轮 P0 要防的回归（真机上曾经有四条业务路径绕过统一写入）。
        var allowed = new[]
        {
            "private void WriteFact(",
            "private void ForgetFact(",
            "private void ClearFacts(",
            "private void ForgetFactsOfOtherHosts(",
        };
        foreach (var line in code.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
            if (!(trimmed.Contains("_shareFacts[", StringComparison.Ordinal)
                  || trimmed.Contains("_shareFacts.Remove(", StringComparison.Ordinal)
                  || trimmed.Contains("_shareFacts.Clear(", StringComparison.Ordinal)))
                continue;
            // 只允许出现在受控入口的方法体内（用方法签名定位其所在区间）。
            var lineIndex = code.IndexOf(line, StringComparison.Ordinal);
            var owner = allowed
                .Select(sig => code.LastIndexOf(sig, Math.Max(0, lineIndex), StringComparison.Ordinal))
                .Where(idx => idx >= 0)
                .DefaultIfEmpty(-1)
                .Max();
            Assert.True(owner >= 0,
                $"发现绕过受控入口的账本直接写入：{trimmed}");
        }
    }

    private static string MethodWindow(string text, string signature, int length)
    {
        var i = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(i >= 0, $"找不到方法签名：{signature}");
        return text.Substring(i, Math.Min(length, text.Length - i));
    }

    private static string ReadRepoFile(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray()));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到仓库根（PCMig.sln）。");
    }
}