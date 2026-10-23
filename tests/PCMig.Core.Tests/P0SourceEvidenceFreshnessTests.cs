using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PCMig.Core.Util;
using PCMig.WinUI.Presentation;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ P0-05（2026-10-13 定点修复）★ 源共享**可达性证据的新鲜度**契约。
///
/// 缺陷回顾（真机实测）：共享被公司策略撤销后，底栏源灯 **600 秒全程绿色**，
/// 迁移 6 个对象全部永久失败后仍然绿。根因 = `ConnectionViewModel.UpdateSourceState`
/// 只数"有没有可达事实"、**从不比对事实时间**，且空闲期没有任何探测或过期机制。
///
/// 本文件锁死修复后的规则：
///   ① 绿灯只能由**新鲜**的可读探测证据推出（有效期默认 60 秒，到期即降级为"待确认"琥珀）；
///   ② 只有"重新读到"才能恢复绿色——历史连接事实、ping 通、网卡在线都不行；
///   ③ 探测本身没得出结论 ⇒ 待确认（琥珀），既不冒充可达也不冒充已断开；
///   ④ 巡检真的挂在底栏呈现节拍上（不是只写了个没人调用的方法）。
///
/// ★ 2026-10-15 偶发失败定点修复 ★ 本类是 `ConnectionViewModel.Clock` / `ShareCountProbe`
/// 这两个**全测试进程共享**静态测试缝的改写者（见 <see cref="WithSeamsAsync"/>），
/// 因此必须与所有读取该时钟的测试类串行执行。真实观测到的失败：
/// `SourceConnectionStateTests.P05_...` 得到 `Actual: SuspectOrUnknown`
/// （证据被盖上正常时间戳，但读取时时钟已被本类推进 61 秒 ⇒ 过期）。
///
/// 这里挂 `A5-jobdir-isolation` 而不是新集合，是为了让**三个**相关类同集合：
///   · 本类（改写时钟，零 IO，挂哪个集合都安全）；
///   · `SourceConnectionStateTests`（读时钟，P05 失败者，已从无集合改为挂本集合）；
///   · `P0SourceLampTests`（读时钟，且必须留在本集合以继续享有 `PCMIG_JOBS` 进程级隔离）。
/// 集合契约见 <see cref="JobDirIsolationCollection"/>。
/// </summary>
[Collection("A5-jobdir-isolation")]
public class P0SourceEvidenceFreshnessTests
{
    private static readonly DateTime T0 = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>把两个测试缝（时钟 / 读探测）换上替身并保证用完还原，避免污染其它测试类。</summary>
    private static async Task WithSeamsAsync(DateTime start, Func<string, int> probe, Func<ConnectionViewModel, Task> body)
    {
        var savedClock = ConnectionViewModel.Clock;
        var savedProbe = ConnectionViewModel.ShareCountProbe;
        var savedReadProbe = ConnectionViewModel.ShareReadProbe;
        ConnectionViewModel.Clock = () => start;
        ConnectionViewModel.ShareCountProbe = probe;
        // ★ 2026-10-24（R1-6A）★ 共享级读探测也必须换成确定性替身。
        //   本类的语义是"共享级读探测没有得出结论"，它只该由 ShareCountProbe 这一条缝表达；
        //   把 ShareReadProbe 留成真实实现等于把"是否碰巧选中了共享"变成环境依赖 ——
        //   一旦某条用例将来加了 IsSelected 共享，这里就会真的去摸 UNC 路径，
        //   在没有 \\host 的机器上变成不确定结果。恒 true 只保证"不触网"，不参与任何断言。
        ConnectionViewModel.ShareReadProbe = _ => true;
        try
        {
            using var vm = new ConnectionViewModel();
            await body(vm).ConfigureAwait(false);
        }
        finally
        {
            ConnectionViewModel.Clock = savedClock;
            ConnectionViewModel.ShareCountProbe = savedProbe;
            ConnectionViewModel.ShareReadProbe = savedReadProbe;
        }
    }

    // ── ① 有效期：绿灯到期必须熄灭 ──────────────────────────────────────────────

    [Fact]
    public void Ttl_IsSixtySeconds_AsRequiredByOrder()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), ConnectionViewModel.EvidenceTtl);
        Assert.Equal(TimeSpan.FromSeconds(60), SourceEvidenceLedger.DefaultTtl);
    }

    [Fact]
    public async Task FreshEvidence_ShowsConnected()
    {
        await WithSeamsAsync(T0, _ => throw new InvalidOperationException("本用例不应触发探测"), async vm =>
        {
            vm.Host = "TEST-HOST";
            vm.ForceConnectedForTest(true);

            Assert.Equal(SourceConnectionState.Connected, vm.SourceState);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task EvidenceExpired_AfterSixtySeconds_NoLongerShowsConnected()
    {
        // 用户指定的隔离测试目标：60 秒内不再错误显示"已连接"。
        await WithSeamsAsync(T0, _ => throw new InvalidOperationException("没得出结论"), async vm =>
        {
            vm.Host = "TEST-HOST";
            vm.ForceConnectedForTest(true);
            Assert.Equal(SourceConnectionState.Connected, vm.SourceState);

            ConnectionViewModel.Clock = () => T0.AddSeconds(61);
            await vm.SweepEvidenceFreshnessAsync();

            Assert.NotEqual(SourceConnectionState.Connected, vm.SourceState);
            Assert.Equal(SourceConnectionState.SuspectOrUnknown, vm.SourceState);
        });
    }

    [Fact]
    public void TtlBoundary_SixtySecondsStillFresh_OneMillisecondMoreIsNot()
    {
        var ledger = new SourceEvidenceLedger();
        ledger.Record(@"\\h\share", true, "读到过", T0);

        Assert.Equal(SourceEvidenceState.Connected, ledger.Derive(T0.AddSeconds(60), false, false, false));
        Assert.Equal(SourceEvidenceState.SuspectOrUnknown, ledger.Derive(T0.AddSeconds(60).AddMilliseconds(1), false, false, false));
    }

    [Fact]
    public async Task ExpiredEvidenceWithoutProbeTarget_IsSuspectNotConnected()
    {
        // Host 为空（没有可探测对象）时也不得继续显示已连接。
        await WithSeamsAsync(T0, _ => 7, async vm =>
        {
            vm.ForceConnectedForTest(true);
            Assert.Equal(SourceConnectionState.Connected, vm.SourceState);

            ConnectionViewModel.Clock = () => T0.AddMinutes(30);
            await vm.SweepEvidenceFreshnessAsync();

            Assert.Equal(SourceConnectionState.SuspectOrUnknown, vm.SourceState);
        });
    }

    // ── ② 只有新的有效读探测证据才能恢复绿色 ────────────────────────────────────

    [Fact]
    public async Task ProbeFindsShares_RestoresGreen()
    {
        await WithSeamsAsync(T0, _ => 3, async vm =>
        {
            vm.Host = "TEST-HOST";
            vm.ForceConnectedForTest(true);

            ConnectionViewModel.Clock = () => T0.AddMinutes(10);   // 证据早已过期
            await vm.SweepEvidenceFreshnessAsync();

            Assert.Equal(SourceConnectionState.Connected, vm.SourceState);
            // 绿灯文案必须仍然强调"由可读探测确认"（不是"上次连上过"）
            Assert.Contains("可读探测", vm.SourceStateDetail);
        });
    }

    [Fact]
    public async Task ProbeFindsNothing_ShowsDisconnected()
    {
        await WithSeamsAsync(T0, _ => 0, async vm =>
        {
            vm.Host = "TEST-HOST";
            vm.ForceConnectedForTest(true);

            ConnectionViewModel.Clock = () => T0.AddMinutes(10);
            await vm.SweepEvidenceFreshnessAsync();

            Assert.Equal(SourceConnectionState.Disconnected, vm.SourceState);
        });
    }

    [Fact]
    public async Task ProbeInconclusive_IsSuspect_NeitherGreenNorRed()
    {
        await WithSeamsAsync(T0, _ => throw new IOException("探测超时"), async vm =>
        {
            vm.Host = "TEST-HOST";
            vm.ForceConnectedForTest(true);

            ConnectionViewModel.Clock = () => T0.AddMinutes(10);
            await vm.SweepEvidenceFreshnessAsync();

            Assert.Equal(SourceConnectionState.SuspectOrUnknown, vm.SourceState);
            Assert.NotEqual(SourceConnectionState.Connected, vm.SourceState);
            Assert.NotEqual(SourceConnectionState.Disconnected, vm.SourceState);
        });
    }

    // ── ③ 账本（纯逻辑）分档 ────────────────────────────────────────────────────

    [Fact]
    public void UnreachableFact_DoesNotExpire_AndKeepsLampRed()
    {
        var ledger = new SourceEvidenceLedger();
        ledger.Record(@"\\h\share", false, "确认失效", T0);

        Assert.Equal(SourceEvidenceState.Disconnected, ledger.Derive(T0.AddDays(1), false, false, false));
    }

    [Fact]
    public void FreshPlusDead_IsPartial()
    {
        var ledger = new SourceEvidenceLedger();
        ledger.Record(@"\\h\a", true, "刚刚读到", T0);
        ledger.Record(@"\\h\b", false, "确认失效", T0);

        Assert.Equal(SourceEvidenceState.Partial, ledger.Derive(T0.AddSeconds(5), false, false, false));
    }

    [Fact]
    public void HistoricalConnectionOnly_IsSuspectNeverGreen()
    {
        var ledger = new SourceEvidenceLedger();
        Assert.Equal(SourceEvidenceState.SuspectOrUnknown, ledger.Derive(T0, false, false, historicalConnected: true));
        Assert.Equal(SourceEvidenceState.Disconnected, ledger.Derive(T0, false, false, historicalConnected: false));
    }

    [Fact]
    public void TargetsNeedingProbe_IsBoundedAndOldestFirst()
    {
        var ledger = new SourceEvidenceLedger();
        ledger.Record(@"\\h\a", true, "旧", T0.AddMinutes(-30));
        ledger.Record(@"\\h\b", true, "较新", T0.AddMinutes(-10));
        ledger.Record(@"\\h\c", false, "确认失效", T0.AddMinutes(-20));
        ledger.Record(@"\\h\d", true, "刚刚读到", T0);            // 新鲜 ⇒ 不需要探测

        var picked = ledger.TargetsNeedingProbe(T0, maxTargets: 2);

        Assert.Equal(2, picked.Count);
        Assert.Equal(@"\\h\a", picked[0].Target);                 // 最旧优先
        Assert.DoesNotContain(picked, f => f.Target == @"\\h\d"); // 新鲜的不探测
    }

    // ── ④ 接线契约：不能被悄悄改回"只数数量"的旧逻辑 ────────────────────────────

    [Fact]
    public void ConnectionViewModel_UsesLedgerAndSweep_NotTheOldCountOnlyRule()
    {
        var code = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "ConnectionViewModel.cs");

        Assert.Contains("SourceEvidenceLedger", code);
        Assert.Contains("Derive(", code);
        Assert.Contains("SweepEvidenceFreshness", code);
        Assert.Contains("EvidenceTtl", code);
        // 旧缺陷写法：只按"有没有可达事实"分档，从不看时间
        Assert.DoesNotContain("var reachable = _shareFacts.Values.Count(f => f.Reachable);", code);
        // 纪律：不得用 ping / 网卡在线 / 历史连接成功当"共享可读"的证据
        Assert.DoesNotContain("Ping", code);
        Assert.DoesNotContain("NetworkInterface", code);
    }

    [Fact]
    public void FooterPresentationTick_DrivesTheFreshnessSweep()
    {
        var main = ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml.cs");
        var window = MethodWindow(main, "private void RefreshFooterPresentation()", 1800);

        Assert.Contains("ViewModel.SweepEvidenceFreshness()", window);
        // ★ Preview.4 真机问题 2（2026-10-22）★ 活性同步必须**先于**证据巡检：
        //   反了的话巡检会先按"证据已过期"把灯降成琥珀，续期发生在之后，灯照样黄一帧甚至更久。
        Assert.Contains("ViewModel.SyncTransferActivity(", window);
        var syncAt = window.IndexOf("ViewModel.SyncTransferActivity(", StringComparison.Ordinal);
        var sweepAt = window.IndexOf("ViewModel.SweepEvidenceFreshness()", StringComparison.Ordinal);
        Assert.True(syncAt >= 0 && sweepAt > syncAt, "必须先把传输活性同步进证据账本，再跑证据巡检。");
        // 探测结论要能回到 UI 线程（否则跨线程改属性）
        Assert.Contains("AttachUiQueue", main);
    }

    [Fact]
    public void ProbeIsBounded_SingleFlightAndIntervalGuarded()
    {
        var code = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "ConnectionViewModel.cs");
                // 窗口 1200 → 1800：方法里新增了"子路径诊断复证"这一段（R1-6B 复审 P1-1），
        // 它是与共享根探测并列的第二次 Task.Run，窗口太窄会把 `Task.Run` 挤出视野。
        var probe = MethodWindow(code, "private async Task ProbeIfDueAsync", 1800);

        Assert.Contains("_probeGate.WaitAsync(0)", probe);        // 单飞：不叠加 SMB 请求
        Assert.Contains("ProbeInterval", probe);                  // 有界间隔
        Assert.Contains("Task.Run", probe);                       // 不阻塞 UI 线程
        Assert.Contains("ShareCountProbe", probe);                // 只认"真的读到了"
    }

    // ── helpers ────────────────────────────────────────────────────────────────

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
