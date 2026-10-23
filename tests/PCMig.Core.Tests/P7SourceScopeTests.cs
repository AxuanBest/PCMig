using System;
using System.Linq;
using System.Threading.Tasks;
using PCMig.Core.Util;
using PCMig.WinUI.Presentation;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ 2026-10-24（R1-6B 发布阻断修复）★ **源连接证据的作用域（scope）契约**。
///
/// 【被修复的缺陷】`_shareFacts` 的键空间原来按"调用方写进来的字符串"分家：
///   · 连接预检 / 手工共享 / 空闲探测写共享根 `\\host\share`；
///   · 验证恢复写 `o.SourcePath`，也就是**计划里的完整源路径** `\\host\share\Users\Public`；
///   · 引擎运行中报失效也写完整源路径。
/// 于是同一条 SMB 共享在账本里长出多个互不覆盖的键，`SourceEvidenceLedger.Derive`
/// 只数 `fresh` 与 `dead` 的**条数**、完全不知道这些键之间的层次关系 ⇒
/// 「共享根读得到」与「它下面某个目录读不到」并存 ⇒ `Partial` 琥珀，
/// 而且因为键不同，新的成功永远覆盖不掉旧的失效 ⇒ **不自愈的假黄灯**。
///
/// 【修复的不变量】（本文件逐条锁死）
///   ① 键一律折叠到**共享根作用域**：`\\host\share\sub` ⇒ `\\host\share`；
///   ② 子路径**读不到**不否证共享根 —— 它只进子路径诊断，并折成"有依据的 Partial"，
///      既要让用户看见（不许简单隐藏黄灯），也不许冒充"整条共享失效"（不许假红）；
///   ③ 子路径**读得到**向上蕴含共享根读得到 —— 这是可自证的强证据，必须能覆盖先前的失效；
///   ④ 各条共享互不掩盖：`\\host\e` 失效 + `\\host\f` 可达 ⇒ 如实 Partial；
///   ⑤ 子路径诊断同样受 <see cref="SourceEvidenceLedger.DefaultTtl"/> 约束 —— 历史结论不得
///      永久毒化当前状态（否则就是把假黄灯换了个地方）。
/// </summary>
[Collection("A5-jobdir-isolation")]
public sealed class P7SourceScopeTests : IDisposable
{
    private readonly Func<DateTime> _originalClock = ConnectionViewModel.Clock;
    private readonly Func<string, bool> _originalShareReadProbe = ConnectionViewModel.ShareReadProbe;

    private DateTime _now = new(2026, 10, 24, 10, 0, 0, DateTimeKind.Utc);

    public P7SourceScopeTests()
    {
        ConnectionViewModel.Clock = () => _now;
        ConnectionViewModel.ShareReadProbe = _ => true;
    }

    public void Dispose()
    {
        // 两个都是**进程级**测试缝，必须逐次复位，否则污染同集合内的其它测试类。
        ConnectionViewModel.Clock = _originalClock;
        ConnectionViewModel.ShareReadProbe = _originalShareReadProbe;
    }

    private ConnectionViewModel NewVm(string host = "192.0.2.10") => new() { Host = host };

    private static void AddSelectedShare(ConnectionViewModel vm, string name, string unc)
        => vm.Shares.Add(new ShareItem { Name = name, UncPath = unc, Kind = "共享（手动指定）", IsSelected = true });

    // ───────────────────────────────────────────────────────────────────────────
    // ① 作用域折叠：纯字符串规则（不过网络、不依赖环境）
    // ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("192.0.2.10", @"\\192.0.2.10")]
    [InlineData(@"\\192.0.2.10", @"\\192.0.2.10")]
    [InlineData(@"\\192.0.2.10\", @"\\192.0.2.10")]
    [InlineData(@"\\192.0.2.10\e", @"\\192.0.2.10\e")]
    [InlineData(@"\\192.0.2.10\e\", @"\\192.0.2.10\e")]
    [InlineData(@"\\192.0.2.10\e\Users", @"\\192.0.2.10\e")]
    [InlineData(@"\\192.0.2.10\e\Users\Public", @"\\192.0.2.10\e")]
    [InlineData(@"\\192.0.2.10\e\Users\Public\Desktop\a.txt", @"\\192.0.2.10\e")]
    public void 作用域_任意深度子路径都折叠到共享根(string input, string expected)
        => Assert.Equal(expected, ConnectionViewModel.ShareScopeOf(input));

    [Theory]
    [InlineData(@"\\192.0.2.10", false)]
    [InlineData(@"\\192.0.2.10\e", false)]
    [InlineData(@"\\192.0.2.10\e\Users", true)]
    [InlineData(@"\\192.0.2.10\e\Users\Public", true)]
    public void 作用域_子路径判定只认共享名之后还有段(string input, bool expected)
        => Assert.Equal(expected, ConnectionViewModel.IsSubPath(input));

    // ───────────────────────────────────────────────────────────────────────────
    // ② 子路径读不到：不许否证共享根（不许假红），但必须看得见（不许假绿）
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void 子路径不可达_不写死共享根_折成有依据的Partial()
    {
        using var vm = NewVm();
        AddSelectedShare(vm, "e", @"\\192.0.2.10\e");
        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "手工共享探测通过");
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);

        // 计划里某个对象落在子目录：引擎/验证在这一层读不到。
        vm.NotifyShareReachability(@"\\192.0.2.10\e\Users\Public", false, "Win32 5 拒绝访问");

        // ① 不许假绿：真实的子目录访问错误必须让用户看见。
        Assert.Equal(SourceConnectionState.Partial, vm.SourceState);
        // ② 不许假红：共享根本身仍然是实测可达的（主机级聚合键没被常量成失效）。
        Assert.Single(vm.VerifiedReachableTargets);
        Assert.Equal(@"\\192.0.2.10\e", vm.VerifiedReachableTargets[0]);
        // ③ 必须能定位是哪一刀：诊断里要看得到具体子路径与原因。
        Assert.Single(vm.UnreachableShares);
        Assert.Contains(@"\\192.0.2.10\e\Users\Public", vm.UnreachableShares[0]);
        Assert.Contains("子路径不可读", vm.UnreachableShares[0]);
    }

    [Fact]
    public async Task 历史子路径不可达_超过有效期后回到已连接_不再永久毒化()
    {
        using var vm = NewVm();
        AddSelectedShare(vm, "e", @"\\192.0.2.10\e");
        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "手工共享探测通过");
        vm.NotifyShareReachability(@"\\192.0.2.10\e\Users\Public", false, "Win32 5 拒绝访问");
        Assert.Equal(SourceConnectionState.Partial, vm.SourceState);

        // 子路径诊断与可达证据同一套 TTL：过了 60 秒就不再算"当前"结论。
        _now = _now.AddSeconds(61);
        await vm.SweepEvidenceFreshnessAsync();

        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);
        Assert.Empty(vm.UnreachableShares);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // ③ 子路径读得到：向上蕴含共享根读得到，必须能覆盖先前的失效
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void 子路径可达_覆盖共享根失效_消除错误Partial()
    {
        using var vm = NewVm();
        AddSelectedShare(vm, "e", @"\\192.0.2.10\e");
        vm.NotifyShareReachability(@"\\192.0.2.10\e", false, "运行中确认失效");
        Assert.Equal(SourceConnectionState.Disconnected, vm.SourceState);

        // 验证实测到计划对象的源路径（可能是子目录）真的读得到 ⇒ 共享必然读得到。
        vm.NotifyShareReachability(@"\\192.0.2.10\e\Users\Public", true, "验证实测源根可达");

        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);
        Assert.Empty(vm.UnreachableShares);
        Assert.Single(vm.VerifiedReachableTargets);          // 只有共享根一个键，不是两个
        Assert.Equal(@"\\192.0.2.10\e", vm.VerifiedReachableTargets[0]);
    }

    [Fact]
    public void 共享根可达_子路径可达_共用一个键_不会长出两条证据()
    {
        using var vm = NewVm();
        AddSelectedShare(vm, "e", @"\\192.0.2.10\e");

        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "手工共享探测通过");
        vm.NotifyShareReachability(@"\\192.0.2.10\e\Users\Public", true, "验证实测源根可达");
        vm.NotifyShareReachability(@"\\192.0.2.10\e\Users\Public\Desktop", true, "验证实测源根可达");

        Assert.Single(vm.VerifiedReachableTargets);
        Assert.Equal(@"\\192.0.2.10\e", vm.VerifiedReachableTargets[0]);
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // ④ 多条共享之间不许互相掩盖
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void 多共享_一条失效另一条可达_才是真的有依据的Partial()
    {
        using var vm = NewVm();
        AddSelectedShare(vm, "e", @"\\192.0.2.10\e");
        AddSelectedShare(vm, "f", @"\\192.0.2.10\f");

        vm.NotifyShareReachability(@"\\192.0.2.10\e", false, "运行中确认失效");
        vm.NotifyShareReachability(@"\\192.0.2.10\f", true, "手工共享探测通过");

        Assert.Equal(SourceConnectionState.Partial, vm.SourceState);
        Assert.Single(vm.UnreachableShares);
        Assert.Contains(@"\\192.0.2.10\e", vm.UnreachableShares[0]);
    }

    [Fact]
    public void 多共享_子路径失效只影响自己那条共享的诊断定位()
    {
        using var vm = NewVm();
        AddSelectedShare(vm, "e", @"\\192.0.2.10\e");
        AddSelectedShare(vm, "f", @"\\192.0.2.10\f");

        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "手工共享探测通过");
        vm.NotifyShareReachability(@"\\192.0.2.10\f", true, "手工共享探测通过");
        vm.NotifyShareReachability(@"\\192.0.2.10\e\Users\Public", false, "Win32 5 拒绝访问");

        Assert.Equal(SourceConnectionState.Partial, vm.SourceState);
        Assert.Equal(2, vm.VerifiedReachableTargets.Count);   // 两条共享根都还可达
        Assert.Single(vm.UnreachableShares);
        Assert.Contains(@"\\192.0.2.10\e\Users\Public", vm.UnreachableShares[0]);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // ⑤ 清理边界：换主机 / 真正断连 / 只清自己子树
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void 换旧电脑_旧主机的子路径诊断一并清掉()
    {
        using var vm = NewVm();
        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "手工共享探测通过");
        vm.NotifyShareReachability(@"\\192.0.2.10\e\Users\Public", false, "Win32 5 拒绝访问");
        Assert.Equal(SourceConnectionState.Partial, vm.SourceState);

        vm.Host = "192.0.2.31";          // 真的换了旧电脑

        Assert.Empty(vm.UnreachableShares);
        Assert.Equal(SourceConnectionState.Disconnected, vm.SourceState);
    }

    [Fact]
    public void 真正断连_清空全部证据_包括子路径诊断()
    {
        using var vm = NewVm();
        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "手工共享探测通过");
        vm.NotifyShareReachability(@"\\192.0.2.10\e\Users\Public", false, "Win32 5 拒绝访问");

        vm.NotifySourceLost();

        // 真正断连后账本里只允许剩下"这台主机整体已失效"这一条权威结论；
        // 子路径诊断必须一并清掉（否则换源/重连后会拿旧机器的目录结论说话）。
        Assert.DoesNotContain(vm.UnreachableShares, s => s.Contains(@"\Users\Public"));
        Assert.Equal(SourceConnectionState.Disconnected, vm.SourceState);
    }

    [Fact]
    public void 子路径转好_只清自己这条诊断_不清更深也不清兄弟()
    {
        using var vm = NewVm();
        AddSelectedShare(vm, "e", @"\\192.0.2.10\e");
        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "手工共享探测通过");
        vm.NotifyShareReachability(@"\\192.0.2.10\e\Users\Public", false, "Win32 5 拒绝访问");
        vm.NotifyShareReachability(@"\\192.0.2.10\e\Users\Public\Desktop", false, "Win32 5 拒绝访问");
        vm.NotifyShareReachability(@"\\192.0.2.10\e\Secret", false, "Win32 5 拒绝访问");
        Assert.Equal(3, vm.UnreachableShares.Count);

        // Public 这一条读得到 ⇒ **只**撤销 Public 自己的诊断。
        // 更深的 Public\Desktop 仍然留黄（父目录可读证明不了子目录可读，那是拿父目录的成功伪造证据），
        // 兄弟 Secret 也必须留着。这条是 R1-6B 复审 P1-2 的回归锁。
        vm.NotifyShareReachability(@"\\192.0.2.10\e\Users\Public", true, "验证实测源根可达");

        Assert.Equal(2, vm.UnreachableShares.Count);
        Assert.Contains(vm.UnreachableShares, s => s.Contains(@"\Users\Public\Desktop"));
        Assert.Contains(vm.UnreachableShares, s => s.Contains(@"\e\Secret"));
    }

    [Fact]
    public void 权威子路径失效_必须是共享级失效_不得降级成60秒后自行消失的诊断()
    {
        using var vm = NewVm();
        AddSelectedShare(vm, "e", @"\\192.0.2.10\e");
        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "手工共享探测通过");
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);

        // 引擎/用户实测报的是"这个计划对象的源读不到"，说的是整条共享用不了，
        // 不是"共享还好、只是某个子目录 ACL 严" ⇒ 必须落成共享级失效（红），
        // 不能降级进 _subPathIssues —— 那东西 60 秒后会被清掉，等于静默翻绿。
        vm.NotifyShareReachability(@"\\192.0.2.10\e\Users\Public", false, "Win32 53 找不到网络路径", authoritative: true);

        Assert.Equal(SourceConnectionState.Disconnected, vm.SourceState);
        Assert.Contains(vm.UnreachableShares, s => s.Contains(@"\\192.0.2.10\e"));
        Assert.DoesNotContain(vm.UnreachableShares, s => s.Contains(@"子路径不可读"));

        // 过了两个有效期仍然是红：TTL 只收口"可达"证据，不收口权威失效。
        _now = _now.AddSeconds(2 * 61);
        vm.SweepEvidenceFreshness();
        Assert.Equal(SourceConnectionState.Disconnected, vm.SourceState);
    }

    [Fact]
    public void 权威子路径失效_同共享下的子路径诊断一并作废_避免重复计黄()
    {
        using var vm = NewVm();
        AddSelectedShare(vm, "e", @"\\192.0.2.10\e");
        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "手工共享探测通过");
        vm.NotifyShareReachability(@"\\192.0.2.10\e\Secret", false, "Win32 5 拒绝访问");
        Assert.Equal(SourceConnectionState.Partial, vm.SourceState);

        vm.NotifyShareReachability(@"\\192.0.2.10\e\Deep", false, "Win32 53 找不到网络路径", authoritative: true);

        // 整条共享已经失效，子目录诊断没有信息量，不能再拿它重复说"部分可用"。
        Assert.Equal(SourceConnectionState.Disconnected, vm.SourceState);
        Assert.DoesNotContain(vm.UnreachableShares, s => s.Contains(@"子路径不可读"));
    }

    [Fact]
    public async Task 换选共享后重连_旧共享的失效事实必须被清掉_不得永久Partial()
    {
        // ★ R1-6B 复审 P0-2 的回归锁 ★
        // 账本里"失效"事实不设有效期，而空闲探测只探当前勾选的共享 —— 一条被取消勾选的坏共享
        // 其 dead 事实**永远不会**再被任何一轮探测覆盖，Derive 恒 fresh>0 && dead>0 ⇒ 永久黄灯。
        // 收口点选在"连接实测通过"这个权威时刻：当前选择集之外的同主机共享级事实一律作废。
        ConnectionViewModel.PreflightRunner = (_, host, _, _, _, _, _, _) =>
            Task.FromResult(TwoSharesReport(host, "e", "f"));
        ConnectionViewModel.ShareReadProbe = _ => true;

        using var vm = NewVm();
        await vm.ConnectAsync(password: null);
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);

        // 共享 f 被删掉了（67 是权威失效）⇒ 黄灯，且用户能定位到 f。
        ConnectionViewModel.ShareReadProbe = unc =>
            unc.EndsWith(@"\f", StringComparison.OrdinalIgnoreCase)
                ? throw new IOException("找不到网络名", unchecked((int)0x80070043))
                : true;
        _now = _now.AddSeconds(30);
        await vm.SweepEvidenceFreshnessAsync();
        Assert.Equal(SourceConnectionState.Partial, vm.SourceState);
        Assert.Contains(vm.UnreachableShares, s => s.Contains(@"\f"));

        // 用户把坏掉的 f 取消勾选，只留 e，重连一次。
        foreach (var s in vm.Shares) s.IsSelected = s.Name == "e";
        ConnectionViewModel.ShareReadProbe = _ => true;
        await vm.ConnectAsync(password: null);

        // f 已不在本次实测范围、也不会再被复探 ⇒ 它的失效事实必须作废，否则灯永久黄。
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);
        Assert.Empty(vm.UnreachableShares);
    }

    private static PCMig.Core.Models.PreflightReport TwoSharesReport(string host, params string[] shareNames)
    {
        var report = new PCMig.Core.Models.PreflightReport
        {
            Host = host,
            OverallPass = true,
            CredentialVerified = true,
            Checks =
            {
                new PCMig.Core.Models.PreflightCheck { Name = "SMB 445", Pass = true, Severity = "Error", Detail = "445 可达" },
            },
        };
        foreach (var name in shareNames)
        {
            var unc = $@"\\{host}\{name}";
            report.Checks.Add(new PCMig.Core.Models.PreflightCheck { Name = "源共享 " + unc, Pass = true, Severity = "Error", Detail = "存在且可访问" });
            report.Shares.Add(new PCMig.Core.Models.ShareInfo { Name = name, UncPath = unc, Remark = "数据共享", IsAdminShare = false });
        }
        return report;
    }
}