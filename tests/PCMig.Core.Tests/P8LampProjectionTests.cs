using System;
using System.IO;
using System.Linq;
using PCMig.Core.Util;
using PCMig.WinUI.Presentation;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ 2026-10-24（用户冻结的四态灯产品定义）★ **源连接指示灯的投影契约**。
///
/// 【冻结定义（不再讨论）】用户可见只有四态：
///   ① 红常亮 —— 未连接源共享；
///   ② 黄呼吸 —— 正在寻找共享 / 建立连接 / 重新连接（含正在验证）；
///   ③ 绿常亮 —— 已连接源共享，当前无数据传输；
///   ④ 绿呼吸 —— 已连接源共享，且确有数据传输活动。
/// 禁止第五种灯色；禁止把内部诊断枚举（Partial / SuspectOrUnknown / Recovering）直接当灯色；
/// 禁止因子目录 ACL 错误把已连上的共享显示成黄色；不得伪造绿色。
///
/// 【本文件锁死的不变量】
///   · 四态映射是纯函数，逐条列全（含"证据过期不得冒充绿、也不得冒充黄"）；
///   · 灯有**作用域**：传输中跟活动对象所属共享，迁移前是勾选列表第一条；
///   · 多共享互不掩盖：非目标共享坏了不改当前灯色，但汇总里必须看得见 X/Y；
///   · 子路径 ACL 错误：灯保持绿，错误完整留在诊断通道（UnreachableShares）；
///   · 内部六态仍在，供日志与文字使用 —— 两套通道不得互相冒充。
/// </summary>
[Collection("A5-jobdir-isolation")]
public sealed class P8LampProjectionTests : IDisposable
{
    private readonly Func<DateTime> _originalClock = ConnectionViewModel.Clock;
    private readonly Func<string, bool> _originalShareReadProbe = ConnectionViewModel.ShareReadProbe;

    private DateTime _now = new(2026, 10, 24, 10, 0, 0, DateTimeKind.Utc);

    public P8LampProjectionTests()
    {
        ConnectionViewModel.Clock = () => _now;
        ConnectionViewModel.ShareReadProbe = _ => true;
    }

    public void Dispose()
    {
        ConnectionViewModel.Clock = _originalClock;
        ConnectionViewModel.ShareReadProbe = _originalShareReadProbe;
    }

    private ConnectionViewModel NewVm(string host = "192.0.2.10") => new() { Host = host };

    private static void AddSelectedShare(ConnectionViewModel vm, string name, string unc)
        => vm.Shares.Add(new ShareItem { Name = name, UncPath = unc, Kind = "共享（手动指定）", IsSelected = true });

    // ───────────────────────────────────────────────────────────────────────────
    // ① 四态映射：纯函数，逐条列全（含优先级）
    // ───────────────────────────────────────────────────────────────────────────

    [Theory]
    // 正在连接 / 正在重连（含正在验证）⇒ 黄呼吸，优先级最高
    [InlineData(true, false, false, false, SourceLampState.Connecting)]
    [InlineData(true, false, true, true, SourceLampState.Connecting)]
    [InlineData(false, true, false, false, SourceLampState.Connecting)]
    [InlineData(false, true, true, false, SourceLampState.Connecting)]
    // 已连接 + 确有传输 ⇒ 绿呼吸
    [InlineData(false, false, true, true, SourceLampState.Transferring)]
    // 已连接 + 空闲 ⇒ 绿常亮
    [InlineData(false, false, true, false, SourceLampState.Connected)]
    // 其余 ⇒ 红常亮（含"没有新鲜证据"这种保守兜底）
    [InlineData(false, false, false, false, SourceLampState.Disconnected)]
    [InlineData(false, false, false, true, SourceLampState.Disconnected)]
    public void L1_四态映射纯函数_逐条列全(
        bool connecting, bool recovering, bool reachable, bool transferActive, SourceLampState expected)
        => Assert.Equal(expected, ConnectionViewModel.ProjectLampState(connecting, recovering, reachable, transferActive));

    [Fact]
    public void L2_四态枚举只有四个成员_不允许第五种灯色()
    {
        var values = Enum.GetValues<SourceLampState>();
        Assert.Equal(4, values.Length);
        Assert.Equal(
            new[] { "Disconnected", "Connecting", "Connected", "Transferring" },
            values.Select(v => v.ToString()).ToArray());
    }

    // ───────────────────────────────────────────────────────────────────────────
    // ② 证据驱动：绿只能来自"本作用域的实测可读证据"
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void L3_共享根实测可读_绿常亮()
    {
        var vm = NewVm();
        AddSelectedShare(vm, "e", @"\\192.0.2.10\e");
        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "预检实测可读");

        Assert.Equal(SourceLampState.Connected, vm.LampState);
        Assert.Contains("已连接", vm.LampText);
    }

    [Fact]
    public void L4_可读且有传输活动_绿呼吸()
    {
        var vm = NewVm();
        AddSelectedShare(vm, "e", @"\\192.0.2.10\e");
        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "预检实测可读");
        vm.NotifyTransferActive(@"\\192.0.2.10\e", 1024);

        Assert.Equal(SourceLampState.Transferring, vm.LampState);

        // 传输停止 ≠ 断开：回到绿常亮，且不删证据。
        vm.NotifyTransferIdle();
        Assert.Equal(SourceLampState.Connected, vm.LampState);
    }

    [Fact]
    public void L5_有传输活动但没有可读证据_不得伪造绿色()
    {
        var vm = NewVm();
        AddSelectedShare(vm, "e", @"\\192.0.2.10\e");
        vm.NotifyTransferActive(@"\\192.0.2.10\e", 4096);
        // NotifyTransferActive 会为被传输的目标写入可达证据（传输本身就证明读到过），
        // 因此这里先把它改成"明确失效"，验证"活动位不能把失效洗成绿"。
        vm.NotifyShareReachability(@"\\192.0.2.10\e", false, "Win32 67：找不到网络名", authoritative: true);

        Assert.Equal(SourceLampState.Disconnected, vm.LampState);
    }

    [Fact]
    public void L6_可达证据过期_红常亮且文字说明正在重新确认()
    {
        var vm = NewVm();
        AddSelectedShare(vm, "e", @"\\192.0.2.10\e");
        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "预检实测可读");
        Assert.Equal(SourceLampState.Connected, vm.LampState);

        // 超过一个 TTL（60 s）：证据过期 ⇒ 不再是"已连接"，但也不能说"确认断开"。
        //
        // ★ 确定性 ★ `SweepEvidenceFreshness` 会顺手发起一次空闲探测（fire-and-forget）。若探针返回
        //   "可读"，它会立刻补一条新证据把灯刷回绿色 —— 与断言形成竞态（A3 树重负载下确实偶发红过）。
        //   本用例要钉的是"过期证据不得冒充绿"，所以这里让探针给出**无结论**（超时），
        //   与真实环境里"探测不出结论"的路径一致：既不写可达、也不写失效。
        ConnectionViewModel.ShareReadProbe = _ => throw new TimeoutException("探测无结论（测试注入）");
        _now = _now.AddSeconds(SourceEvidenceLedger.DefaultTtl.TotalSeconds + 1);
        vm.SweepEvidenceFreshness();

        Assert.Equal(SourceLampState.Disconnected, vm.LampState);
        Assert.Contains("证据已过期", vm.LampText);   // 颜色只有四态，语义必须靠文字说清
        Assert.DoesNotContain("已连接", vm.LampText);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // ③ 子路径 ACL 错误：灯保持绿，错误不得隐藏
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void L7_子路径ACL读不到_灯保持绿常亮_错误仍完整保留在诊断里()
    {
        var vm = NewVm();
        AddSharedAndScope(vm, @"\\192.0.2.10\e");
        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "共享根实测可读");

        // 共享根读得到，但某个子目录无权限 —— 这是"文件级问题"，不是"源连接问题"。
        vm.NotifyShareReachability(@"\\192.0.2.10\e\Users\Public", false, "Win32 5：拒绝访问");

        Assert.Equal(SourceLampState.Connected, vm.LampState);          // 灯：绿
        Assert.Equal(SourceConnectionState.Partial, vm.SourceState);    // 内部：仍是"部分可用"
        Assert.Contains(vm.UnreachableShares, s => s.Contains(@"Users\Public")); // 诊断：不隐藏
    }

    private static void AddSharedAndScope(ConnectionViewModel vm, string unc)
    {
        AddSelectedShare(vm, "e", unc);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // ④ 多共享：灯有作用域，互不掩盖
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void L8_目标共享可读而另一条失效_灯仍绿且汇总显示1比2()
    {
        var vm = NewVm();
        AddSelectedShare(vm, "e", @"\\192.0.2.10\e");
        AddSelectedShare(vm, "f", @"\\192.0.2.10\f");

        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "共享根实测可读");
        vm.NotifyShareReachability(@"\\192.0.2.10\f", false, "Win32 67：找不到网络名", authoritative: true);

        // 灯代表 e（勾选列表第一条）⇒ 绿；但 f 的失效绝不能被掩掉 ⇒ 汇总必须写 1/2。
        Assert.Equal(SourceLampState.Connected, vm.LampState);
        Assert.Equal(SourceConnectionState.Partial, vm.SourceState);
        Assert.Contains("1/2", vm.LampScopeSummary);
    }

    [Fact]
    public void L9_目标共享失效而另一条可读_灯必须红不能借别人的绿()
    {
        var vm = NewVm();
        AddSelectedShare(vm, "e", @"\\192.0.2.10\e");
        AddSelectedShare(vm, "f", @"\\192.0.2.10\f");

        vm.NotifyShareReachability(@"\\192.0.2.10\e", false, "Win32 67：找不到网络名", authoritative: true);
        vm.NotifyShareReachability(@"\\192.0.2.10\f", true, "共享根实测可读");

        Assert.Equal(SourceLampState.Disconnected, vm.LampState);
        Assert.Contains("1/2", vm.LampScopeSummary);
    }

    [Fact]
    public void L10_灯作用域随活动对象切换_传输结束回归默认()
    {
        var vm = NewVm();
        AddSelectedShare(vm, "e", @"\\192.0.2.10\e");
        AddSelectedShare(vm, "f", @"\\192.0.2.10\f");
        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "共享根实测可读");
        vm.NotifyShareReachability(@"\\192.0.2.10\f", true, "共享根实测可读");

        Assert.Equal(@"\\192.0.2.10\e", vm.ComputeLampScope());   // 迁移前 = 第一条勾选

        vm.NotifyTransferActive(@"\\192.0.2.10\f\Users", 2048);   // 子路径也要折叠到共享根
        Assert.Equal(@"\\192.0.2.10\f", vm.ComputeLampScope());
        Assert.Equal(SourceLampState.Transferring, vm.LampState);

        vm.NotifyTransferIdle();
        Assert.Equal(@"\\192.0.2.10\e", vm.ComputeLampScope());
    }

    [Fact]
    public void L11_非目标共享的子路径错误不得把当前共享的绿灯打黄()
    {
        var vm = NewVm();
        AddSelectedShare(vm, "e", @"\\192.0.2.10\e");
        AddSelectedShare(vm, "f", @"\\192.0.2.10\f");
        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "共享根实测可读");
        vm.NotifyShareReachability(@"\\192.0.2.10\f", true, "共享根实测可读");

        // f 下的子目录报错：它与"当前指示灯代表的 e"无关。
        vm.NotifyShareReachability(@"\\192.0.2.10\f\Users\Public", false, "Win32 5：拒绝访问");

        Assert.Equal(SourceLampState.Connected, vm.LampState);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // ⑤ View 只投影四态（契约）：顶栏与底栏同源，且不再 switch 内部六态
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void L12_顶栏与底栏都读LampState_不再各自switch内部六态()
    {
        var window = ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml.cs");
        var head = MethodWindow(window, "private void UpdateSourceStatus()", 1200);
        var footer = MethodWindow(window, "private void UpdateSourceConnectionIndicator()", 5200);

        Assert.Contains("ViewModel.LampState", head);
        Assert.Contains("ViewModel.LampState", footer);
        Assert.DoesNotContain("ViewModel.SourceState switch", head);
        Assert.DoesNotContain("ViewModel.SourceState switch", footer);
        // 诊断通道仍然保留（"为什么是红的"必须能查到）。
        Assert.Contains("ViewModel.SourceStateDetail", footer);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // 辅助
    // ───────────────────────────────────────────────────────────────────────────

    private static string MethodWindow(string code, string signature, int maxLength)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"源码中找不到 {signature}");
        var length = Math.Min(maxLength, code.Length - start);
        return code.Substring(start, length);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PCMig.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"仓库文件不存在：{path}");
        return File.ReadAllText(path);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // ⑤ R2-C 真实渲染取证发现：作用域"从未有过证据"不得把已连接渲染成红灯
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void L13_作用域没有任何证据但账本已判已连接_不得显示红灯()
    {
        // 现场来源：R2-C 用真实 WinUI 帧采集时发现 `state T3` 下
        //   SourceState=Connected 而 LampState=Disconnected / Fill=#D64758（红），
        // 即"已连接但灯是红的"。成因：账本只有主机级证据（连接预检通过、尚未产生共享级探测），
        // 而灯的作用域是共享根 `\\host\e`，查不到事实就被当成"未连接"。
        var vm = NewVm();
        AddSelectedShare(vm, "e", @"\\192.0.2.10\e");

        // 只写主机级事实：模拟"预检通过 + 该共享还没被单独探测"。
        vm.NotifySourceRestored(@"\\192.0.2.10");

        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);
        Assert.Equal(SourceLampState.Connected, vm.LampState);   // 修复前这里是 Disconnected（红）
        Assert.Contains("已连接", vm.LampText);
    }

    [Fact]
    public void L14_作用域有事实但已过期_兜底不得把过期洗成绿灯()
    {
        // 与 L13 成对：兜底只允许在"完全没有事实"时生效。
        var vm = NewVm();
        AddSelectedShare(vm, "e", @"\\192.0.2.10\e");
        vm.NotifySourceRestored(@"\\192.0.2.10");
        vm.NotifyShareReachability(@"\\192.0.2.10\e", true, "预检实测可读");
        Assert.Equal(SourceLampState.Connected, vm.LampState);

        ConnectionViewModel.ShareReadProbe = _ => throw new TimeoutException("探测无结论（测试注入）");
        _now = _now.AddSeconds(SourceEvidenceLedger.DefaultTtl.TotalSeconds + 1);
        vm.SweepEvidenceFreshness();

        Assert.Equal(SourceLampState.Disconnected, vm.LampState);
        Assert.Contains("证据已过期", vm.LampText);
    }
}