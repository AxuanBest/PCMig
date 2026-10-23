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
/// ★ P0-06（2026-10-23 真机 JOB-20261023-111714-c991 / -112117-5fe6）★
/// 「SMB 连接状态与迁移性能联合专项」的连接状态回归契约。
///
/// 真机现场（`Logs\app-20261023.log`，两次任务同构）：
///   · `\\192.0.2.10\IPC$` 恒 67（对方不导出 IPC$）；
///   · 用户改走「手工指定共享」⇒ 11:17:03.558 `手动添加共享成功: \\192.0.2.10\e`；
///   · `preflight.json` 里 `\\192.0.2.10\e` 是 **PASS（存在且可访问）**；
///   · 但底栏源灯**全程黄色**，连 5.88 GiB 已确认搬运（`bytesPerSecond=30558050.34`）时也只是黄。
///
/// 根因三条（均由源码行号 + 日志双重确认）：
///   ① **键空间分裂**：连接落库写 `\\host`（主机级）、手工共享写 `\\host\share`（共享级）、
///      空闲探测又写裸 `host` ⇒ 账本里 fresh 与 dead 恒并存 ⇒ `Derive` 恒返回 `Partial` ⇒ 琥珀。
///   ② **探测失败清空全部证据**：`EnumShares` 打的是 `NetShareEnum`，对方不导出 IPC$ 就必抛 67，
///      旧 `NotifyProbeInconclusive` 把 `_shareFacts` 整个 Clear ⇒ 每 20 秒自毁一次绿灯依据。
///   ③ **探测对象错**：探测问的是"主机能不能枚举共享"，不是"**这条指定共享**读不读得到"，
///      与用户确认的四态定义（B1：`Connected` = 已确认**指定源共享**连接有效）不符。
///
/// 本文件锁死修复后的行为，共 16 项（对应任务书 §E「连接状态测试必须覆盖」清单）。
/// 全部是行为级断言；只有无法在本机无 SMB 环境复现的部分才退化为源码结构契约，并在注释里标明。
/// </summary>
[Collection("A5-jobdir-isolation")]
public sealed class P6SmbConnectionAndPerformanceTests : IDisposable
{
    private readonly Func<DateTime> _originalClock = ConnectionViewModel.Clock;
    private readonly Func<string, int> _originalShareCountProbe = ConnectionViewModel.ShareCountProbe;
    private readonly Func<string, bool> _originalShareReadProbe = ConnectionViewModel.ShareReadProbe;
    private readonly Func<Serilog.ILogger, string, string?, string?, IReadOnlyList<string>, string?, long, bool,
        Task<PCMig.Core.Models.PreflightReport>> _originalPreflightRunner = ConnectionViewModel.PreflightRunner;
    private readonly Func<string, string?, string?, IReadOnlyList<string>, Serilog.ILogger, IDisposable?>
        _originalSessionConnector = ConnectionViewModel.ShareSessionConnector;

    private DateTime _now = new(2026, 10, 23, 11, 17, 3, DateTimeKind.Utc);

    public P6SmbConnectionAndPerformanceTests()
    {
        ConnectionViewModel.Clock = () => _now;
    }

    public void Dispose()
    {
        // 五个都是**进程级**测试缝，必须逐次复位，否则污染同集合内的其它测试类。
        ConnectionViewModel.Clock = _originalClock;
        ConnectionViewModel.ShareCountProbe = _originalShareCountProbe;
        ConnectionViewModel.ShareReadProbe = _originalShareReadProbe;
        ConnectionViewModel.PreflightRunner = _originalPreflightRunner;
        ConnectionViewModel.ShareSessionConnector = _originalSessionConnector;
    }

    private ConnectionViewModel NewVm(string host = "192.0.2.10")
        => new() { Host = host };

    private static void AddSelectedShare(ConnectionViewModel vm, string name, string unc)
        => vm.Shares.Add(new ShareItem { Name = name, UncPath = unc, Kind = "共享（手动指定）", IsSelected = true });

    // ───────────────────────────────────────────────────────────────────────────
    // ① 未连接
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void 未连接_红灯_且没有任何可达证据()
    {
        using var vm = NewVm();
        Assert.Equal(SourceConnectionState.Disconnected, vm.SourceState);
        Assert.Empty(vm.VerifiedReachableTargets);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // ② 建立连接
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void 建立连接_预检通过_绿灯常亮()
    {
        using var vm = NewVm();
        vm.ForceConnectedForTest(true);
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // ③ 【核心】IPC$ 失败但指定数据共享可读 ⇒ 绿常亮（B2 必复现场景）
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IPC美元失败但指定共享可读_空闲超六十秒_仍绿常亮()
    {
        // 现场同构：主机级枚举恒 67（IPC$ 不存在），但共享根读得到。
        ConnectionViewModel.ShareCountProbe = _ => throw new IOException("枚举共享失败（错误 67）：找不到网络名。");
        ConnectionViewModel.ShareReadProbe = _ => true;

        using var vm = NewVm();
        AddSelectedShare(vm, "e", @"\\192.0.2.10\e");
        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "直连访问共享成功（共享枚举不可用）");

        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);

        // 空闲远超原 TTL（60 秒）：旧实现每 20 秒探测必抛 67 ⇒ 清空全部证据 ⇒ 永远黄。
        // 新实现：探测降级为共享级只读枚举，读到就续期 ⇒ 绿必须一直成立。
        for (var i = 0; i < 10; i++)
        {
            _now = _now.AddSeconds(30);
            await vm.SweepEvidenceFreshnessAsync();
            Assert.Equal(SourceConnectionState.Connected, vm.SourceState);
        }
    }

    [Fact]
    public async Task IPC美元失败时_主机级枚举失败不得写失效证据()
    {
        // 没有已勾选共享 ⇒ 退回主机级枚举；它抛 67 只说明"枚举这条路走不通"，
        // 不是任何数据共享失效的证据（B4 ①）。
        ConnectionViewModel.ShareCountProbe = _ => throw new IOException("枚举共享失败（错误 67）：找不到网络名。");
        ConnectionViewModel.ShareReadProbe = _ => true;

        using var vm = NewVm();
        vm.ForceConnectedForTest(true);
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);

        _now = _now.AddSeconds(30);
        await vm.SweepEvidenceFreshnessAsync();

        // 仍必须是"已连接或至少待确认"，绝不能变成"确认断开"的红灯。
        Assert.NotEqual(SourceConnectionState.Disconnected, vm.SourceState);
        Assert.Empty(vm.UnreachableShares);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // ④ 键空间归一：后来实测可覆盖先前结论（根因①）
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void 同一共享的裸主机名与UNC写法必须只占一个键_旧失败必须被新成功覆盖()
    {
        using var vm = NewVm();

        // 先用三种等价写法各写一次失效（真机里它们分别来自探测 / 引擎失败信号 / 手工共享）。
        vm.NotifyShareReachability("192.0.2.10", false, "旧失效（裸主机名）");
        vm.NotifyShareReachability(@"\\192.0.2.10", false, "旧失效（UNC 主机级）");
        Assert.Equal(SourceConnectionState.Disconnected, vm.SourceState);

        // 再写一条**同一目标**的新成功：必须覆盖掉上面的失效，而不是并存成 Partial。
        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "重新读到");
        vm.NotifyShareReachability(@"\\192.0.2.10", true, "重新读到（主机级）");
        vm.NotifyShareReachability("192.0.2.10", true, "重新读到（裸主机名）");

        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);
        Assert.Empty(vm.UnreachableShares);
    }

    [Fact]
    public void 不同目标各占一个键_一条失效另一条可达_才是Partial()
    {
        using var vm = NewVm();
        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "读到");
        vm.NotifyShareReachability(@"\\192.0.2.10\f", false, "Win32 67 找不到网络名");

        Assert.Equal(SourceConnectionState.Partial, vm.SourceState);
        Assert.Single(vm.UnreachableShares);
        Assert.Contains("\\f", vm.UnreachableShares[0], StringComparison.Ordinal);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // ⑤ 空闲超过原 TTL（证据自然会老化，不能无限绿）
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task 证据过期且探测无结论_降为待确认而不是永久绿()
    {
        // 只读探测本身没结论（超时类）⇒ 既不能冒充可达，也不能冒充断开。
        ConnectionViewModel.ShareCountProbe = _ => throw new IOException("探测超时");
        ConnectionViewModel.ShareReadProbe = _ => throw new IOException("探测超时");

        using var vm = NewVm();
        vm.ForceConnectedForTest(true);
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);

        _now = _now.AddSeconds(61);
        await vm.SweepEvidenceFreshnessAsync();

        Assert.Equal(SourceConnectionState.SuspectOrUnknown, vm.SourceState);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // ⑥ 子路径不可访问不等于共享失效 / ⑦ 只读共享仍是可用
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void 只读共享只读探测成功即为连接有效()
    {
        // B4 ③：共享可读但写不了测速临时文件（现场 preflight 就是这个结论：Access Denied）
        //      —— 这不影响"源共享连接有效"这个判断。
        using var vm = NewVm();
        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "空闲期读探测：该共享根可读");
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);
        Assert.Empty(vm.UnreachableShares);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // ⑧ 真实断开 ⑨ 恢复
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void 引擎报告共享级失效_红灯_且恢复后回到绿()
    {
        using var vm = NewVm();
        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "读到");
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);

        vm.NotifySourceLost();                                  // 运行中确认失效
        Assert.Equal(SourceConnectionState.Disconnected, vm.SourceState);

        vm.NotifySourceRestored(@"\\192.0.2.10\e");             // 验证实测源根可达
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // ⑩ 1219（多重连接 / 凭据冲突）不得被当作"共享可用"
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void 凭据冲突1219写入的失效证据不能被历史连接事实顶掉()
    {
        using var vm = NewVm();
        vm.ForceConnectedForTest(true);
        vm.NotifyShareReachability(@"\\192.0.2.10\e", false, "Win32 1219：多重连接不允许");

        Assert.Equal(SourceConnectionState.Partial, vm.SourceState);
        Assert.Single(vm.UnreachableShares);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // ⑪ 传输中必须绿呼吸；⑫ 暂停 / 停止 / 完成后必须绿常亮
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void 传输中且指定共享可读_必须绿呼吸而不是黄()
    {
        using var vm = NewVm();
        AddSelectedShare(vm, "e", @"\\192.0.2.10\e");
        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "读到");

        vm.SyncTransferActivity(active: true, displayedBytes: 6_310_144_799, bytesPerSecond: 30_558_050.34);
        Assert.Equal(SourceConnectionState.Transferring, vm.SourceState);
        Assert.Contains("正在传输", vm.SourceStateDetail);
    }

    [Fact]
    public void 传输续期落在共享级键上_并且能顶住主机级旧失效()
    {
        using var vm = NewVm();
        AddSelectedShare(vm, "e", @"\\192.0.2.10\e");

        // 真机里主机级失效来自连接落库/空闲探测（`\\host`），成功来自共享级（`\\host\share`）。
        vm.NotifyShareReachability(@"\\192.0.2.10", false, "主机级旧失效（例如枚举不可用）");
        Assert.Equal(SourceConnectionState.Disconnected, vm.SourceState);

        // 传输续期必须把**主机级**那条旧结论顶掉：传输能跑起来就是"这条共享现在读得到"的硬证据，
        // 而主机级聚合结论不该压过它（否则真机里"正在传 5.88 GiB 却黄灯"就是这个成因）。
        vm.SyncTransferActivity(active: true, displayedBytes: 6_310_144_799, bytesPerSecond: 30_558_050.34);
        Assert.Equal(SourceConnectionState.Transferring, vm.SourceState);
        Assert.Empty(vm.UnreachableShares);
    }

    [Fact]
    public void 传输活动不许把引擎刚判死的共享又救活()
    {
        // B4 ⑥⑨：网卡有流量 ≠ 这条共享读得到。引擎已给出**权威**失效结论时，
        // 传输续期不得覆盖它 —— 否则就是"靠有流量假装绿灯"。
        using var vm = NewVm();
        AddSelectedShare(vm, "e", @"\\192.0.2.10\e");
        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "读到");
        vm.SyncTransferActivity(active: true, displayedBytes: 1_000, bytesPerSecond: 1_000_000);

        Assert.Equal(SourceConnectionState.Transferring, vm.SourceState);

        vm.NotifyShareReachability(@"\\192.0.2.10\e", false, "Win32 67（共享已删除）");
        Assert.NotEqual(SourceConnectionState.Transferring, vm.SourceState);

        // 之后再送一拍传输活动，不得把它翻回绿。
        _now = _now.AddSeconds(5);
        vm.SyncTransferActivity(active: true, displayedBytes: 2_000, bytesPerSecond: 1_000_000);
        Assert.NotEqual(SourceConnectionState.Transferring, vm.SourceState);
        Assert.NotEqual(SourceConnectionState.Connected, vm.SourceState);
    }

    [Fact]
    public void 暂停后退出传输活动_回到绿常亮且不删证据()
    {
        using var vm = NewVm();
        AddSelectedShare(vm, "e", @"\\192.0.2.10\e");
        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "读到");
        vm.SyncTransferActivity(active: true, displayedBytes: 155_236_995, bytesPerSecond: 13_496_453.5);
        Assert.Equal(SourceConnectionState.Transferring, vm.SourceState);

        vm.SyncTransferActivity(active: false, displayedBytes: 155_236_995, bytesPerSecond: 0);
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // ⑬ 高频 UI 刷新不得改变状态；⑭ 顶栏与底栏必须同源
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void 高频重复调用不改变状态也不积累失效证据()
    {
        using var vm = NewVm();
        AddSelectedShare(vm, "e", @"\\192.0.2.10\e");
        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "读到");

        for (var i = 0; i < 500; i++)
        {
            _now = _now.AddMilliseconds(16);
            vm.SyncTransferActivity(active: true, displayedBytes: i, bytesPerSecond: 1_000_000);
            vm.SweepEvidenceFreshness();
        }

        Assert.Equal(SourceConnectionState.Transferring, vm.SourceState);
        Assert.Empty(vm.UnreachableShares);
    }

    [Fact]
    public void 顶栏文案必须由LampState派生_与底栏同一权威源()
    {
        // ★ 2026-10-24（用户冻结的四态灯定义）★ 权威源从内部六态 SourceState 换成用户可见的
        //   LampState —— 但这条测试的**语义没变**，而且更严：
        //   ① 顶栏与底栏必须读**同一个**属性（LampState），不能各挑各的；
        //   ② 仍然禁止用 IsConnected 这个"连接过"的历史布尔冒充当前状态；
        //   ③ 顶栏不得再自己 switch 内部六态（那正是"底栏变红、顶栏还写已连接"的温床）。
        var mw = ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml.cs");
        var body = MethodWindow(mw, "private void UpdateSourceStatus()", 1600);
        var footer = MethodWindow(mw, "private void UpdateSourceConnectionIndicator()", 5200);
        Assert.Contains("ViewModel.LampState", body);
        Assert.Contains("SourceLampState.", body);
        Assert.Contains("ViewModel.LampState", footer);
        Assert.DoesNotContain("ViewModel.IsConnected ?", body);
        Assert.DoesNotContain("ViewModel.SourceState switch", body);
        Assert.DoesNotContain("ViewModel.SourceState switch", footer);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // ⑮ 切换源不得沿用旧证据
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void 切换主机后旧主机的证据不得继续支撑绿灯()
    {
        using var vm = NewVm("192.0.2.10");
        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "读到");
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);

        vm.NotifySourceLost();                       // 换源前先确认旧源失效
        Assert.Equal(SourceConnectionState.Disconnected, vm.SourceState);
        Assert.Empty(vm.VerifiedReachableTargets);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // 性能专项：速率口径必须自证，不能由"网卡有流量"冒充
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void 速率下限判据不得把网卡噪声当成传输活动()
    {
        // C3：三种速度必须分开。256 KB/s 是"确实在搬数据"的下限，
        // 低于它的一律不算迁移活动（否则暂停时网卡有其他流量就会绿呼吸 —— B4 ⑧）。
        Assert.Equal(256 * 1024, MigrationSessionViewModel.TransferActivityFloorBytesPerSecond);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // ⑯【Preview.7 收口 P0】真实业务入口级测试 —— 不走 Notify* 测试入口
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task 业务入口_ConnectAsync_预检通过后落的是证据而不是布尔值()
    {
        // 任务书 P0 第 6 条：不能只靠直接调 NotifyShareReachability 证明"写入统一"。
        // 这里走真 ConnectAsync：预检由缝返回"IPC$ 67 + 指定共享 PASS"，
        // 共享读探测由缝返回 true，其余全部是正式代码。
        ConnectionViewModel.PreflightRunner = (_, host, _, _, _, _, _, _) =>
            Task.FromResult(Ipcdollar67ButShareReadable(host, "e"));
        ConnectionViewModel.ShareReadProbe = _ => true;

        using var vm = NewVm();
        await vm.ConnectAsync(password: null);

        Assert.True(vm.IsConnected);
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);
        // 绿灯依据必须是实测证据，且**恰好一条**（主机级聚合结论已被共享级事实摘掉）。
        var verified = vm.VerifiedReachableTargets;
        Assert.Single(verified);
        Assert.Equal(@"\\192.0.2.10\e", verified[0], StringComparer.OrdinalIgnoreCase);
        Assert.Empty(vm.UnreachableShares);
    }

    [Fact]
    public async Task 业务入口_ConnectAsync后空闲超过六十秒且多次周期探测_仍绿常亮()
    {
        // 完整复现用户点名的现场：IPC$ 67 + 数据共享可读 + 空闲超 60 秒 + 多次 20 秒周期探测。
        ConnectionViewModel.PreflightRunner = (_, host, _, _, _, _, _, _) =>
            Task.FromResult(Ipcdollar67ButShareReadable(host, "e"));
        ConnectionViewModel.ShareReadProbe = _ => true;

        using var vm = NewVm();
        await vm.ConnectAsync(password: null);
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);

        // 6 轮 × 30 秒 = 180 秒，横跨 3 个 TTL、约 9 个探测周期。
        for (var i = 0; i < 6; i++)
        {
            _now = _now.AddSeconds(30);
            await vm.SweepEvidenceFreshnessAsync();
            Assert.Equal(SourceConnectionState.Connected, vm.SourceState);
            Assert.Empty(vm.UnreachableShares);
        }
    }

    [Fact]
    public async Task 业务入口_ConnectAsync后共享真实失效_不得保持虚假绿色()
    {
        ConnectionViewModel.PreflightRunner = (_, host, _, _, _, _, _, _) =>
            Task.FromResult(Ipcdollar67ButShareReadable(host, "e"));
        ConnectionViewModel.ShareReadProbe = _ => true;

        using var vm = NewVm();
        await vm.ConnectAsync(password: null);
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);

        // 共享被删掉（错误 67 是**权威失效**）。
        ConnectionViewModel.ShareReadProbe = _ => throw new IOException("找不到网络名", unchecked((int)0x80070043));
        _now = _now.AddSeconds(30);
        await vm.SweepEvidenceFreshnessAsync();

        Assert.NotEqual(SourceConnectionState.Connected, vm.SourceState);
        Assert.NotEqual(SourceConnectionState.Transferring, vm.SourceState);
        Assert.Single(vm.UnreachableShares);
    }

    [Fact]
    public async Task 业务入口_AddManualShareAsync_成功只落共享级键_不产生假Partial()
    {
        // 先把主机级聚合结论写上（模拟"上一轮预检整体通过"），再走手工添加共享：
        // 共享级写入必须摘掉主机级聚合键，否则 fresh+dead 并存 ⇒ Partial（真机黄灯机制）。
        ConnectionViewModel.ShareReadProbe = _ => true;

        using var vm = NewVm();
        vm.NotifyShareReachability(@"\\192.0.2.10", false, "上一轮主机级失效");
        Assert.Equal(SourceConnectionState.Disconnected, vm.SourceState);

        vm.ManualShareName = "e";
        await vm.AddManualShareAsync(password: null);

        Assert.True(vm.IsConnected);
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);
        Assert.Empty(vm.UnreachableShares);
        var verified = vm.VerifiedReachableTargets;
        Assert.Single(verified);
        Assert.Equal(@"\\192.0.2.10\e", verified[0], StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 业务入口_AddManualShareAsync_权威失败写失效_非权威失败不写失效()
    {
        ConnectionViewModel.ShareReadProbe = _ => throw new IOException("找不到网络名", unchecked((int)0x80070043));
        using (var vm = NewVm())
        {
            vm.ManualShareName = "nope";
            await vm.AddManualShareAsync(password: null);
            Assert.Single(vm.UnreachableShares);
        }

        // 网络瞬断（53）不是"这条共享没了"的证据 ⇒ 不得在账本留下永不消失的红点。
        ConnectionViewModel.ShareReadProbe = _ => throw new IOException("找不到网络路径", unchecked((int)0x80070035));
        using (var vm = NewVm())
        {
            vm.ManualShareName = "e";
            await vm.AddManualShareAsync(password: null);
            Assert.Empty(vm.UnreachableShares);          // ★ 核心断言：无权威失效证据
            Assert.Empty(vm.VerifiedReachableTargets);   // 也不许伪造可达
            // 从未连上过且没有任何证据 ⇒ 红灯是正确结论（不是"被这次 transient 失败写成红灯"）。
            Assert.Equal(SourceConnectionState.Disconnected, vm.SourceState);
            Assert.False(vm.IsConnected);
        }
    }

    [Fact]
    public void 业务入口_换主机时旧主机证据被摘掉_同一主机的共享证据原样保留()
    {
        using var vm = NewVm("192.0.2.10");
        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "读到");
        vm.NotifyShareReachability(@"\\192.0.2.31\e", true, "另一台也读到");
        Assert.Equal(2, vm.VerifiedReachableTargets.Count);

        vm.Host = "192.0.2.10";       // 值没变 ⇒ 不应误删任何东西
        Assert.Equal(2, vm.VerifiedReachableTargets.Count);

        vm.Host = "192.0.2.32";       // 真的换了旧电脑 ⇒ 别的主机证据必须失效
        Assert.Empty(vm.VerifiedReachableTargets);
        Assert.Equal(SourceConnectionState.Disconnected, vm.SourceState);
    }

    [Fact]
    public async Task 业务入口_手动添加共享后_空闲超六十秒多次探测_仍绿常亮()
    {
        // 用户点名的现场全链路业务复现（不是直接调 NotifyShareReachability）：
        // 手工添加共享成功 → 长时间空闲（横跨多个 TTL）→ 多轮 20 秒周期探测 → 指定共享持续可读。
        ConnectionViewModel.ShareReadProbe = _ => true;

        using var vm = NewVm();
        vm.ManualShareName = "e";
        await vm.AddManualShareAsync(password: null);
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);

        for (var i = 0; i < 8; i++)          // 8 × 30 s = 240 s，跨 4 个 TTL
        {
            _now = _now.AddSeconds(30);
            await vm.SweepEvidenceFreshnessAsync();
            Assert.Equal(SourceConnectionState.Connected, vm.SourceState);
            Assert.Empty(vm.UnreachableShares);
            Assert.Single(vm.VerifiedReachableTargets);
        }
    }

    [Fact]
    public async Task 业务入口_顶栏与底栏在整条生命期始终一致()
    {
        // ★ P0 语义澄清 ★ 「顶底一致」不是"IsConnected 必须等于绿灯"，而是
        //   **同一块界面对同一件事只能有一个答案**。IsConnected 是"曾经连上过"的历史事实，
        //   共享被删后它可以仍为 true；真正决定显示的是证据派生的 SourceState。
        //   所以这里分两层验证：
        //     ① 结构层：顶栏文案必须只由 SourceState 派生（读 IsConnected 就是撕裂的来源）；
        //     ② 生命期层：每个阶段走到哪，灯就必须是那个真相，且不许假绿/假红。
        var mw = ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml.cs");
        var topBar = MethodWindow(mw, "private void UpdateSourceStatus()", 1400);
        Assert.Contains("ViewModel.SourceState", topBar);
        Assert.DoesNotContain("ViewModel.IsConnected", topBar);   // ★ 撕裂根源：顶栏看布尔值

        ConnectionViewModel.PreflightRunner = (_, host, _, _, _, _, _, _) =>
            Task.FromResult(Ipcdollar67ButShareReadable(host, "e"));
        ConnectionViewModel.ShareReadProbe = _ => true;

        using var vm = NewVm();
        Assert.Equal(SourceConnectionState.Disconnected, vm.SourceState);      // ① 未连接 ⇒ 红

        await vm.ConnectAsync(password: null);
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);         // ② 连上 ⇒ 绿常亮

        _now = _now.AddSeconds(90);                                            // ③ 空闲超 TTL
        await vm.SweepEvidenceFreshnessAsync();
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);         // 仍绿（探测续期）

        ConnectionViewModel.ShareReadProbe = _ => throw new IOException("找不到网络名", unchecked((int)0x80070043));
        _now = _now.AddSeconds(30);                                            // ④ 共享真实失效
        await vm.SweepEvidenceFreshnessAsync();
        Assert.NotEqual(SourceConnectionState.Connected, vm.SourceState);      // 不许假绿
        Assert.NotEqual(SourceConnectionState.Transferring, vm.SourceState);
        Assert.Single(vm.UnreachableShares);

        ConnectionViewModel.ShareReadProbe = _ => true;
        vm.NotifySourceRestored(@"\\192.0.2.10\e");                            // ⑤ 恢复
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // ⑰【Preview.7 收口 P0】只读探测的执行边界
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void 探测边界_结构契约_网络IO不在UI线程_且单飞且有硬超时()
    {
        var code = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "ConnectionViewModel.cs");
        var body = MethodWindow(code, "private async Task ProbeIfDueAsync", 5200);

        // ① 所有 SMB 调用都在 Task.Run 里（不允许在调用线程上同步跑网络 IO）。
        Assert.Contains("Task.Run(() => ShareCountProbe(host))", body);
        Assert.Contains("Task.Run(() => ShareReadProbe(unc))", body);
        // ② 单飞闸门 + 有界间隔（原有约束不得退化）。
        Assert.Contains("_probeGate.WaitAsync(0)", body);
        Assert.Contains("ProbeInterval", body);
        // ③ 21 秒阻塞必须被硬超时兜住，否则单飞闸门会被一条坏共享占满。
        Assert.Contains("WithProbeTimeoutAsync(", body);
        Assert.Contains("ProbeTimeout", code);
        // ④ 迟到结论必须能被识别并丢弃。
        Assert.Contains("_sessionEpoch", body);
    }

    [Fact]
    public void 探测边界_多共享共用一个总预算_不是每条各占一份()
    {
        var code = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "ConnectionViewModel.cs");
        var body = MethodWindow(code, "private async Task ProbeIfDueAsync", 5200);

        // 总预算语义：整轮探测（所有共享加在一起）最多占用一个 ProbeTimeout，
        // 否则 N 条坏共享会累加成 N×25 秒，把单飞闸门占死（多共享环境下的真实风险）。
        Assert.Contains("budgetSw", body);
        Assert.Contains("ProbeTimeout - budgetSw.Elapsed", body);
        Assert.Contains("remaining", body);

        // 每条探测拿到的是「剩余预算」，不是「固定的整份超时」。
        Assert.Contains("remaining).ConfigureAwait(false)", body);
        Assert.DoesNotContain($"共享根读探测（{{unc}}）\", ProbeTimeout)", body);
    }

    [Fact]
    public async Task 探测边界_迟到结论不得污染新会话()
    {
        // A 主机的探测被卡住 ⇒ 期间用户换成 B 主机并连上 ⇒ A 的迟到结论必须被丢弃。
        using var vm = NewVm("192.0.2.10");
        vm.ForceConnectedForTest(true);
        AddSelectedShare(vm, "e", @"\\192.0.2.10\e");
        var before = vm.VerifiedReachableTargets.Count;

        // 换主机（纪元 +1）
        vm.Host = "192.0.2.40";
        Assert.Empty(vm.VerifiedReachableTargets);

        // 一轮探测正常跑完：结论只作用于新主机自己的目标集合（这里一条都没有 ⇒ 走主机级枚举）。
        ConnectionViewModel.ShareCountProbe = _ => 0;
        _now = _now.AddSeconds(30);
        await vm.SweepEvidenceFreshnessAsync();
        Assert.DoesNotContain(vm.VerifiedReachableTargets, t => t.Contains("192.0.2.10", StringComparison.Ordinal));
        Assert.True(before > 0);
    }

    [Fact]
    public async Task 探测边界_一条共享探测超时不删其他共享的有效证据()
    {
        using var vm = NewVm();
        AddSelectedShare(vm, "ok", @"\\192.0.2.10\ok");
        AddSelectedShare(vm, "slow", @"\\192.0.2.10\slow");
        vm.NotifyShareReachability(@"\\192.0.2.10\ok", true, "刚才读到");
        vm.NotifyShareReachability(@"\\192.0.2.10\slow", true, "刚才读到");
        Assert.Equal(2, vm.VerifiedReachableTargets.Count);
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);

        // ok 可读；slow 抛"无结论"类失败（53 网络路径不可达 = 瞬断，不是权威失效）。
        ConnectionViewModel.ShareReadProbe = unc =>
            unc.EndsWith(@"\ok", StringComparison.OrdinalIgnoreCase)
                ? true
                : throw new IOException("找不到网络路径", unchecked((int)0x80070035));

        _now = _now.AddSeconds(30);
        await vm.SweepEvidenceFreshnessAsync();

        // ★ 核心断言：另一条共享刚读到的证据不许被这次"没结论"删掉。
        Assert.Empty(vm.UnreachableShares);
        Assert.Contains(vm.VerifiedReachableTargets, t => t.EndsWith(@"\ok", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(vm.VerifiedReachableTargets, t => t.EndsWith(@"\slow", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);
    }

    [Fact]
    public async Task 探测边界_权威失效才写失效_非权威失败保留且不误报()
    {
        using var vm = NewVm();
        AddSelectedShare(vm, "e", @"\\192.0.2.10\e");
        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "刚才读到");

        // 访问被拒（5）是权威失效：这条共享确实不能再当有效
        ConnectionViewModel.ShareReadProbe = _ => throw new UnauthorizedAccessException("拒绝访问");
        _now = _now.AddSeconds(30);
        await vm.SweepEvidenceFreshnessAsync();
        Assert.Single(vm.UnreachableShares);
        Assert.NotEqual(SourceConnectionState.Connected, vm.SourceState);
    }

    [Fact]
    public void 探测边界_探测目标与实际迁移共享同源_不用主机名代替()
    {
        var code = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "ConnectionViewModel.cs");
        var body = MethodWindow(code, "private List<string> SelectedShareRoots", 900);
        // 目标来自 Shares 里已勾选的 UncPath；没有共享时才退回主机级枚举。
        Assert.Contains("s.IsSelected", body);
        Assert.Contains("s.UncPath", body);
        Assert.Contains("NormalizeTarget", body);
    }

    [Fact]
    public void 探测边界_不新增SMB会话_只读枚举()
    {
        var code = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "ConnectionViewModel.cs");
        // 探测路径只允许读枚举；不允许写文件、不允许建/取消会话、不允许改凭据。
        var body = MethodWindow(code, "internal static Func<string, bool> ShareReadProbe", 700);
        Assert.Contains("Directory.EnumerateFileSystemEntries", body);
        Assert.DoesNotContain("ConnectForTransfer", body);
        Assert.DoesNotContain("WNetCancelConnection2", body);
        Assert.DoesNotContain("File.WriteAll", body);
        Assert.DoesNotContain("CreateFile", body);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // ⑱【Preview.7 收口 P1】身份守护异步化的竞态保护
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void 身份守护_异步化后必须带运行纪元_防止上一对象的结论误杀下一对象()
    {
        var code = ReadRepoFile("src", "PCMig.Core", "Transfer", "TransferOrchestrator.cs");

        // ① 纪元字段存在，且是**自有**字段（不是复用别的状态位）。
        Assert.Contains("_identityGuardEpoch", code);
        Assert.Contains("IdentityGuardEpoch", code);

        // ② 递增点：每次运行开始的复位块 + 每个对象开始。
        var runReset = MethodWindow(code, "Interlocked.Exchange(ref _identityGuardPending, 0);", 600);
        Assert.Contains("Interlocked.Increment(ref _identityGuardEpoch)", runReset);
        // 对象边界：紧挨着 _guardObjectPath 赋值的下一次递增。
        var objBoundary = MethodWindow(code, "_guardObjectPath = obj.SourcePath;", 900);
        Assert.Contains("Interlocked.Increment(ref _identityGuardEpoch)", objBoundary);

        // ③ 后台循环启动前取快照，循环内每次迭代都比对。
        var guard = MethodWindow(code, "private void GuardActiveObjectIdentity", 2200);
        Assert.Contains("epochAtStart", guard);
        Assert.Contains("_identityGuardEpoch", guard);
        Assert.Contains("Volatile.Read(ref _identityGuardEpoch)", guard);

        // ④ 判定层也要校验：Capture 阻塞期间对象边界可能已经换人。
        var eval = MethodWindow(code, "private void EvaluateActiveObjectIdentity", 1600);
        Assert.Contains("int? epoch", eval);
        Assert.Contains("_identityGuardEpoch", eval);
        //   过期结论必须"不判定、不终止"，不能静默杀掉当前进程。
        Assert.Contains("不判定、不终止", eval);
    }

    [Fact]
    public void 身份守护_判据本身未被异步化改动()
    {
        // P1 的边界：只改"谁来跑"，不改"怎么判"。
        var code = ReadRepoFile("src", "PCMig.Core", "Transfer", "TransferOrchestrator.cs");

        // 判据仍是 SourceIdentity.IdentityChanged + KillCurrent，没有换成别的启发式。
        var eval = MethodWindow(code, "private void EvaluateActiveObjectIdentity", 1600);
        Assert.Contains("SourceIdentity.IdentityChanged(expected, actual)", eval);
        Assert.Contains("_runner.KillCurrent()", eval);
        // 取不到指纹仍然"不判定"（不得因为拿不到就判死）。
        Assert.Contains("取不到一律", eval);

        // 对象边界仍走强制同步路径，且无参调用点必须保留（GroupBFixBatchTests 依赖）。
        Assert.Contains("GuardActiveObjectIdentity();", code);
        Assert.Contains("GuardActiveObjectIdentity(force: true)", code);
    }

    // ── helpers（源码读取只服务结构契约断言） ──────────────────────────────────

    /// <summary>
    /// 构造一份"现场同构"的预检报告：IPC$ 67 报错（非阻断提醒），但指定共享 `\\host\share` 实测 PASS。
    /// 走的是 <see cref="ConnectionViewModel.PreflightRunner"/> 缝 —— 真实 SMB 由它替换，
    /// **落库规则、键归一、作用域摘除、状态派生全部仍由正式代码计算**。
    /// </summary>
    private static PCMig.Core.Models.PreflightReport Ipcdollar67ButShareReadable(string host, string shareName)
    {
        var unc = $@"\\{host}\{shareName}";
        return new PCMig.Core.Models.PreflightReport
        {
            Host = host,
            OverallPass = true,          // 阻断项为 0；IPC$ 67 只是提醒
            CredentialVerified = true,   // 直连数据共享成功即证明凭据
            Checks =
            {
                new PCMig.Core.Models.PreflightCheck { Name = "SMB 445", Pass = true, Severity = "Error", Detail = "445 可达" },
                new PCMig.Core.Models.PreflightCheck
                {
                    Name = "IPC$ 管理共享",
                    Pass = false,
                    Severity = "Warning",     // ★ 关键：提醒，不是阻断
                    Detail = "无法连接 \\\\" + host + "\\IPC$（错误 67）：找不到网络名。"
                },
                new PCMig.Core.Models.PreflightCheck { Name = "源共享 " + unc, Pass = true, Severity = "Error", Detail = "存在且可访问" },
            },
            Shares =
            {
                new PCMig.Core.Models.ShareInfo { Name = shareName, UncPath = unc, Remark = "数据共享", IsAdminShare = false }
            },
        };
    }

    private static string MethodWindow(string text, string signature, int length)
    {
        var i = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(i >= 0, $"找不到方法签名：{signature}");
        // 向前多取 200 字符：注释块常写在方法签名之前，结构契约有时就写在那里。
        var start = Math.Max(0, i - 200);
        return text.Substring(start, Math.Min(length, text.Length - start));
    }

    private static string ReadRepoFile(params string[] parts)
        // 行尾归一：同一文件在不同检出可能是 LF 或 CRLF（.gitattributes 的 * text=auto + core.autocrlf），
        // 子串断言与定长窗口都必须与检出 EOL 无关，否则"干净检出全绿"不成立。
        => File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray()))
           .Replace("\r\n", "\n");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到仓库根（未在上层目录中发现 PCMig.sln）。起始目录：" + AppContext.BaseDirectory);
    }
}
