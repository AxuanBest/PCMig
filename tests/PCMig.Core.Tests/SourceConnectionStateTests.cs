using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using PCMig.WinUI.Presentation;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ 2026-10-08（真机问题 4）★ 底栏「源连接」三态指示器契约。
///
/// 缺陷回顾：底栏那颗点原来是 `Fill="{StaticResource SuccessBrush}"` 硬编码 —— 无论有没有连上旧电脑、
/// 有没有可访问的共享，它永远是绿的，等于没有信息量（唯一说得通的解释是"Windows 有没有互联网"，
/// 而那不是 PCMig 该表达的事）。它现在表达的唯一事实是：**PCMig 到旧电脑的 SMB 源共享是否可用**。
///
/// 本文件锁死三件事：
///   ① 状态源单点：`SourceConnectionState` 由 `ConnectionViewModel` 拥有并派生，View 只投影、不猜；
///   ② 语义：未连接=红常亮 / 正在连接与恢复中=琥珀呼吸 / **部分可用与未知=琥珀常亮** / 已连接（实测可读）=绿常亮；
///      ping 通、能上网、网卡 UP 都不得变绿；
///      ★ P0-05 ★ 绿灯只能由"可读探测证据"推出：`IsConnected` 这个"上次连上过"的布尔值不再单独点绿。
///   ③ 呼吸走 Composition（不是 DispatcherTimer 改颜色）：不写死毫秒，token 在 Motion.xaml。
///
/// 为什么测试能直接 `new ConnectionViewModel()`：它在 `PCMig.Core.Tests.csproj` 的
/// `A5LinkedPresentationSources` 里被链入（零 WinUI 依赖）；三态枚举 SourceConnectionState.cs 同批链入。
/// </summary>
public class SourceConnectionStateTests
{
    // ── 行为级：状态源由连接事实单点派生 ─────────────────────────────────────────

    [Fact]
    public void Initial_IsDisconnected_NotGreen()
    {
        using var vm = new ConnectionViewModel();
        Assert.Equal(SourceConnectionState.Disconnected, vm.SourceState);
        Assert.False(vm.IsConnected);
        Assert.False(vm.IsConnecting);
    }

    [Fact]
    public void ConnectedFact_DrivesConnectedState_AndBackToDisconnected()
    {
        using var vm = new ConnectionViewModel();
        vm.ForceConnectedForTest(true);                       // 仅测试缝：模拟"预检通过 / 已有可访问共享"
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);
        vm.ForceConnectedForTest(false);
        Assert.Equal(SourceConnectionState.Disconnected, vm.SourceState);
    }

    [Fact]
    public void SourceLost_ReturnsToDisconnected_AndTellsUserHonestly()
    {
        using var vm = new ConnectionViewModel();
        vm.ForceConnectedForTest(true);
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);

        vm.NotifySourceLost();                                 // 运行中 SMB 会话掉了 / 共享不再可达

        Assert.Equal(SourceConnectionState.Disconnected, vm.SourceState);
        Assert.Contains("源共享已失效", vm.FlowStatus);
    }

    [Fact]
    public void SourceLost_WhenNeverConnected_IsNoOp()
    {
        using var vm = new ConnectionViewModel();
        vm.NotifySourceLost();
        Assert.Equal(SourceConnectionState.Disconnected, vm.SourceState);
        Assert.Equal(string.Empty, vm.FlowStatus);
    }

    [Fact]
    public void SourceState_NotifiesPropertyChanged_SoTheIndicatorCanProject()
    {
        using var vm = new ConnectionViewModel();
        var raised = new System.Collections.Generic.List<string>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        vm.ForceConnectedForTest(true);

        Assert.Contains(nameof(ConnectionViewModel.SourceState), raised);
    }

    // ── P0-05：灯必须由"实测证据"驱动，而不是"上次连上过"的布尔值 ────────────────

    [Fact]
    public void P05_ConnectedBooleanWithoutEvidence_IsUnknownNotGreen()
    {
        using var vm = new ConnectionViewModel();
        vm.ForceConnectedForTest(true);                        // 预检通过 ⇒ 有可达证据 ⇒ 绿
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);

        vm.NotifyProbeInconclusive("重探超时，未得出结论");      // 探测没结论 ⇒ 可达证据作废

        Assert.True(vm.IsConnected, "IsConnected 这个布尔值仍在（它代表'会话可能还在'）");
        Assert.Equal(SourceConnectionState.SuspectOrUnknown, vm.SourceState);   // ★ 未知绝不呈现为绿
        Assert.Contains("不能当作可用", vm.SourceStateDetail);
    }

    [Fact]
    public void P05_OneShareFailsAmongMany_IsPartialAndLocatable()
    {
        using var vm = new ConnectionViewModel();
        vm.ForceConnectedForTest(true);
        vm.NotifyShareReachability(@"\\SRC-PC\share-b", false, "Win32 67：网络名不再可用");

        Assert.Equal(SourceConnectionState.Partial, vm.SourceState);
        var failing = Assert.Single(vm.UnreachableShares);
        Assert.Contains(@"\\SRC-PC\share-b", failing);          // 多共享里能定位是哪一条
        Assert.Contains("67", failing);                        // 且带着证据文字
        Assert.NotEmpty(vm.VerifiedReachableTargets);           // 另一条仍可达
    }

    [Fact]
    public void P05_FileLockMustNotReachTheLamp_AndRecoveringIsNotGreen()
    {
        // ★ P0-05 ★ 需求明令：Win32 32/33（文件被占用/锁定）**不能当作整个共享断开**。
        //   灯的唯一入口是 NotifyShareReachability —— 契约就写在它身上，这里锁住这句话，
        //   防止后来有人把"某个文件被锁"当成"共享挂了"顺手送进来。
        var code = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "ConnectionViewModel.cs");
                // ★ 2026-10-24（R1-6B 复审）★ 用**前缀**匹配签名：该方法后来加了可选参数
        //   `bool authoritative = false`（权威失效不得降级成子路径诊断），
        //   把整串写死会让"加了参数"被误报成"入口被改名"。契约要锁的是"这个入口还在且只有它一个"。
        var sig = code.IndexOf("internal void NotifyShareReachability(string target, bool reachable, string evidence", StringComparison.Ordinal);
        Assert.True(sig > 0, "找不到 NotifyShareReachability（灯的唯一入口被改名了？）");
        var funnel = code.Substring(Math.Max(0, sig - 420), Math.Min(1100, code.Length - Math.Max(0, sig - 420)));
        Assert.Contains("32", funnel);
        Assert.Contains("33", funnel);
        Assert.Contains("不得", funnel);

        using var vm = new ConnectionViewModel();
        vm.ForceConnectedForTest(true);
        vm.NotifyRecovering();
        Assert.Equal(SourceConnectionState.Recovering, vm.SourceState);   // 重试中：琥珀，既不是绿也不是红
        vm.NotifyRecoveryFinished();
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);    // 结束后仍按最后一份证据分档
    }

    // ── 契约：状态源纪律（View 不猜） ───────────────────────────────────────────

    [Fact]
    public void C1_SourceStateIsDerivedInExactlyOnePlace_FromConnectionFactsOnly()
    {
        var code = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "ConnectionViewModel.cs");

        // 两个连接事实的 setter 都必须同步派生（少一个就会出现"绿灯停在旧状态"）。
        Assert.Contains("private set { if (Set(ref _isConnecting, value)) { Raise(nameof(CanConnect)); UpdateSourceState(); } }", code);
        Assert.Contains("private set { if (Set(ref _isConnected, value)) UpdateSourceState(); }", code);

        // ★ P0-05（2026-10-13 定点修复）★ 六态分档语义**改由证据账本判**（顺序即优先级不变）：
        //   正在连接 ⇒ Connecting；已确认失效正在重探 ⇒ Recovering；
        //   既有新鲜可达又有确认失效 ⇒ Partial；有新鲜可达证据 ⇒ Connected；
        //   只有确认失效证据 ⇒ Disconnected；只有过期证据/只知"曾连接过" ⇒ SuspectOrUnknown（★未知绝不进绿灯★）。
        //   为什么改成账本：旧写法只数"有没有可达事实"、**从不比对时间**，于是撤销共享 600 秒后灯还绿
        //   （真机实测）。判据必须带时间维度，所以这一层搬进了 Core 的 SourceEvidenceLedger（可被单测推进时间）。
        Assert.Contains("SourceEvidenceLedger", code);
        Assert.Contains("ledger.Derive(now, IsConnecting, _recovering, IsConnected)", code);
        Assert.Contains("PCMig.Core.Util.SourceEvidenceState.Connecting => SourceConnectionState.Connecting", code);
        Assert.Contains("PCMig.Core.Util.SourceEvidenceState.Recovering => SourceConnectionState.Recovering", code);
        Assert.Contains("PCMig.Core.Util.SourceEvidenceState.Partial => SourceConnectionState.Partial", code);
        Assert.Contains("PCMig.Core.Util.SourceEvidenceState.Connected => SourceConnectionState.Connected", code);
        Assert.Contains("PCMig.Core.Util.SourceEvidenceState.SuspectOrUnknown => SourceConnectionState.SuspectOrUnknown", code);
        Assert.Contains("_ => SourceConnectionState.Disconnected,", code);

        // ★ P0-05 缺陷本体 ★ 绿灯必须由**新鲜的可达实测证据**推出，
        //   不得再出现 `IsConnected ⇒ Connected` 这种"上次连上过就是绿的"派生 —— 它一旦回来，本测试立刻红。
        Assert.DoesNotContain(": IsConnected ? SourceConnectionState.Connected", code);
        //   旧缺陷写法（只数数量、不看时间）也不得复活。
        Assert.DoesNotContain("var reachable = _shareFacts.Values.Count(f => f.Reachable);", code);

        // 有效期规则本身锁在账本里：可达证据过了 TTL 就不再算数。
        var ledger = ReadRepoFile("src", "PCMig.Core", "Util", "SourceEvidenceLedger.cs");
        Assert.Contains("var age = nowUtc - fact.AtUtc;", ledger);
        Assert.Contains("return age <= Ttl;", ledger);
        Assert.Contains("ExpiredReachable", ledger);
        Assert.Contains("TimeSpan.FromSeconds(60)", ledger);

        // 判据不得来自 ICMP / 网卡 / 互联网：这个文件里根本没有这些概念
        // （绿 = SMB 源共享可用，不是"能 ping 通"、不是"网卡 UP"、不是"能上网"）。
        Assert.DoesNotContain("ICMP", code);
        Assert.DoesNotContain("System.Net.NetworkInformation", code);
        Assert.DoesNotContain("NetworkListManager", code);
    }

    [Fact]
    public void C2_IndicatorProjectsFourLampStatesFromThemeTokens_NotHardcodedColors()
    {
        var code = ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml.cs");

        Assert.Contains("private const string SourceStateConnectedBrushKey = \"SuccessBrush\";", code);
        Assert.Contains("private const string SourceStateConnectingBrushKey = \"WarningBrush\";", code);
        Assert.Contains("private const string SourceStateDisconnectedBrushKey = \"DangerBrush\";", code);

        var body = MethodWindow(code, "private void UpdateSourceConnectionIndicator()", 5200);

        // 颜色取自主题 Token（此处不写死 #RRGGBB），且颜色不是唯一通道：状态名必须可读。
        Assert.Contains("SourceStateDot.Fill = brush;", body);
        Assert.Contains("源连接：已连接", body);
        Assert.Contains("源连接：正在连接", body);
        Assert.Contains("源连接：未连接", body);
        // ★ 2026-10-24（用户冻结的四态灯定义）★ 用户可见**只有四态**：
        //   红常亮 / 黄呼吸 / 绿常亮 / 绿呼吸。内部 Partial、SuspectOrUnknown、Recovering
        //   一律不得再出现在灯色分支里 —— 它们要么按"灯作用域"回到绿或红，要么只活在文字与诊断里。
        //   （这条不是放松：它同时禁止了第五种灯色，比原来更严。）
        Assert.Contains("SourceLampState.Connected => SourceStateConnectedBrushKey", body);
        Assert.Contains("SourceLampState.Transferring => SourceStateConnectedBrushKey", body);
        Assert.Contains("SourceLampState.Connecting => SourceStateConnectingBrushKey", body);
        Assert.Contains("_ => SourceStateDisconnectedBrushKey", body);
        Assert.DoesNotContain("SourceConnectionState.Partial =>", body);
        Assert.DoesNotContain("SourceConnectionState.SuspectOrUnknown =>", body);
        Assert.DoesNotContain("SourceConnectionState.Recovering =>", body);
        Assert.DoesNotContain("SourceConnectionState.Connecting =>", body);
        // ★ 2026-10-24 ★ 一个点说不清"哪条共享坏了、这盏灯代表谁"：无障碍说明与悬停提示必须
        //   同时带上"作用域 + X/Y"与诊断详情（四态不新增视觉通道，这么多信息只能走文字通道）。
        Assert.Contains("AutomationProperties.SetHelpText(SourceStateDot, lampHelp)", body);
        Assert.Contains("ToolTipService.SetToolTip(SourceStateDot, lampHelp)", body);
        Assert.Contains("ViewModel.LampScopeSummary", body);
        Assert.Contains("ViewModel.SourceStateDetail", body);
        Assert.DoesNotContain("#", body.Replace("#x", string.Empty));
    }

    [Fact]
    public void C3_LampBreathesOnlyWhileWorking_AndItIsComposition_NotDispatcherTimer()
    {
        var window = ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml.cs");
        var body = MethodWindow(window, "private void UpdateSourceConnectionIndicator()", 5200);

        // 只有"正在连接"与"已确认失效、正在重探"呼吸；Partial / SuspectOrUnknown 是静止结论，常亮。
        // ★ 真机问题 1（2026-10-21）★ 传输中必须是**绿色呼吸**（已连接 + 有真实字节增长），
        //   因此 Transferring 也进呼吸分支 —— 但它的画刷走 SuccessBrush，不是 WarningBrush。
        // ★ 2026-10-24（四态定义）★ 只有"黄呼吸=正在连接"与"绿呼吸=已连接且确实在传输"会呼吸。
        Assert.Contains("if (state is SourceLampState.Connecting or SourceLampState.Transferring)", body);
        Assert.Contains("SourceLampState.Transferring => SourceStateConnectedBrushKey", body);
        Assert.Contains("MotionDirector.PlaySourceBreathing(SourceStateDot);", body);
        Assert.Contains("else MotionDirector.ResetSourceBreathing(SourceStateDot);", body);
        // 用户明令：呼吸不得用 DispatcherTimer 改颜色（那会变成生硬的开/关闪烁）。
        Assert.DoesNotContain("DispatcherTimer", body);

        var motion = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MotionDirector.cs");
        // 窗口必须覆盖到 CreateSineBreath 内部（当前实测偏移 2748，留足余量避免注释增删后脆弱）。
        var breath = MethodWindow(motion, "public static void PlaySourceBreathing(FrameworkElement? element)", 4200);

        Assert.Contains("ElementCompositionPreview.GetElementVisual(element)", breath);
        Assert.Contains("visual.StartAnimation(\"Opacity\", breath);", breath);
        Assert.Contains("breath.IterationBehavior = AnimationIterationBehavior.Forever;", breath);
        Assert.Contains("if (!SystemAnimationsEnabled)", breath);          // Reduced Motion ⇒ 常亮，不呼吸
        Assert.DoesNotContain("DispatcherTimer", breath);

        // 周期 / 最低透明度来自 Motion.xaml token（调用点不写死数值）。
        Assert.Contains("SourceBreathDurationKey", breath);
        Assert.Contains("SourceBreathMinOpacityKey", breath);

        // ★ Preview.6（2026-10-22 真机视频）★ 呼吸参数：用户指定 1.8~2.4 s 一个完整周期、本体不得明显消失。
        var xaml = ReadRepoFile("src", "PCMig.WinUI", "Themes", "Motion.xaml");
        Assert.Contains("<Duration x:Key=\"PCMigMotionSourceBreathDuration\">0:0:2.0</Duration>", xaml);
        Assert.Contains("<x:Double x:Key=\"PCMigMotionSourceBreathMinOpacity\">0.45</x:Double>", xaml);
    }

    // ★ Preview.6（2026-10-22 真机视频 20261022-0737-46）★ 绿色活动态"定频闪灭"的根因回归。
    //
    // 根因不是"周期短"，而是：底栏 80 ms 节拍 + 每次 PushFooter 都会调 UpdateSourceConnectionIndicator，
    // 旧 PlaySourceBreathing 每次都 StopAnimation + StartAnimation，且关键帧 0f 起点是最低亮度
    // ⇒ 时间线每 80 ms 被打回 t=0 ⇒ 圆点永远停在暗部，肉眼就是"一盏定频闪灭的灯"。
    // 下面断言锁死修复后的不变量（纯源码契约测试：WinUI 控件在 Core.Tests 里无法实例化）。
    [Fact]
    public void C5_BreathIsIdempotent_NoPhaseResetOnRepeatedTicks()
    {
        var motion = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MotionDirector.cs");

        // ① 幂等闸门必须存在于 PlaySourceBreathing 里，并且在 StopAnimation **之前**就返回。
        var breath = MethodWindow(motion, "public static void PlaySourceBreathing(FrameworkElement? element)", 4200);
        var gateReturn = breath.IndexOf("active == signature) return;", StringComparison.Ordinal);
        var stop = breath.IndexOf("visual.StopAnimation(\"Opacity\");", StringComparison.Ordinal);
        Assert.True(gateReturn > 0, "PlaySourceBreathing 缺少幂等闸门：重复调用会重置相位（真机定频闪灭的直接原因）");
        Assert.True(stop > 0, "PlaySourceBreathing 找不到 StopAnimation");
        Assert.True(gateReturn < stop, "幂等闸门必须在 StopAnimation 之前返回，否则仍会每次重建动画");

        // ② 幂等状态必须挂在元素上且随元素回收（ConditionalWeakTable，不累积、不泄漏）。
        Assert.Contains("ConditionalWeakTable<FrameworkElement, BreathSignature>", motion);
        Assert.Contains("ActiveBreaths.AddOrUpdate(element, signature);", motion);

        // ③ 呼吸曲线必须是正弦型（端点速度为零），且**起点亮度是 1.0**（切换瞬间不得突然变暗）。
        var sine = MethodWindow(motion, "private static ScalarKeyFrameAnimation CreateSineBreath(", 2000);
        Assert.Contains("Math.Cos(2.0 * Math.PI * t)", sine);
        Assert.Contains("breath.InsertKeyFrame(0f, 1f);", sine);
        Assert.Contains("AnimationIterationBehavior.Forever", sine);

        // ④ 常亮复位也必须幂等：本来就没在呼吸且已不透明 ⇒ 空操作。
        var reset = MethodWindow(motion, "public static void ResetSourceBreathing(FrameworkElement? element)", 1400);
        Assert.Contains("ActiveBreaths.Remove(element);", reset);
        Assert.Contains("if (!wasBreathing && element.Opacity >= 1.0) return;", reset);
    }

    [Fact]
    public void C6_IndicatorProjectionAndStateNotifiesAreIdempotent()
    {
        // 信号源侧：状态没变 ⇒ 不广播（否则底栏灯每 80 ms 被叫醒一次）。
        var conn = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "ConnectionViewModel.cs");
        var update = MethodWindow(conn, "private void UpdateSourceState()", 4200);
        Assert.Contains("RaiseIfChanged(ref _stateDetailSnapshot, SourceStateDetail, nameof(SourceStateDetail));", update);
        Assert.DoesNotContain("Raise(nameof(SourceStateDetail));", update);
        Assert.DoesNotContain("Raise(nameof(UnreachableShares));", update);
        Assert.DoesNotContain("Raise(nameof(VerifiedReachableTargets));", update);
        // 派生属性每次新建 List ⇒ 必须按内容比较，不能比引用。
        Assert.Contains("(IReadOnlyList<string> a, IReadOnlyList<string> b) => a.SequenceEqual(b, StringComparer.Ordinal)", conn);

        // 消费侧：投影本身也必须幂等，且闸门在"设画刷 / 进 MotionDirector"之前返回。
        var window = ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml.cs");
        var body = MethodWindow(window, "private void UpdateSourceConnectionIndicator()", 5200);
        var gate = body.IndexOf("_sourceIndicatorProjection is { } previous", StringComparison.Ordinal);
        var fill = body.IndexOf("SourceStateDot.Fill = brush;", StringComparison.Ordinal);
        Assert.True(gate > 0, "UpdateSourceConnectionIndicator 缺少幂等闸门");
        Assert.True(fill > 0, "UpdateSourceConnectionIndicator 找不到画刷赋值");
        Assert.True(gate < fill, "幂等闸门必须在画刷赋值之前返回");
        Assert.Contains("previous.State == state", body);
        Assert.Contains("string.Equals(previous.Detail, detail, StringComparison.Ordinal)", body);
    }

    [Fact]
    public void C7_StateSemanticsUnchanged_OnlyTransferringStaysAnimatedGreen()
    {
        var window = ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml.cs");
        var body = MethodWindow(window, "private void UpdateSourceConnectionIndicator()", 5200);
        // ★ 2026-10-24（四态定义）★ 绿呼吸只属于 Transferring；Connected 绿常亮；Connecting 琥珀呼吸。
        Assert.Contains("SourceLampState.Transferring => SourceStateConnectedBrushKey", body);
        Assert.Contains("SourceLampState.Connected => SourceStateConnectedBrushKey", body);
        Assert.Contains("SourceLampState.Connecting => SourceStateConnectingBrushKey", body);
        Assert.Contains("if (state is SourceLampState.Connecting or SourceLampState.Transferring)", body);
        // 暂停可立即熄呼吸（只允许"退出活动位"），亮呼吸仍只能由呈现节拍依据网卡/引擎证据决定。
        var session = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MigrationSessionViewModel.cs");
        var flag = MethodWindow(session, "private void SyncTransferActivityFlag(bool active)", 900);
        Assert.Contains("if (active) return;", flag);
        Assert.Contains("SyncTransferActivity(false,", flag);
    }

    [Fact]
    public void C4_BottomBarDotHasNameAndSafeDefault_AndOldHardcodedGreenIsGone()
    {
        var xaml = ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml");
        var i = xaml.IndexOf("FooterNetPanel", StringComparison.Ordinal);
        Assert.True(i > 0, "底栏找不到 FooterNetPanel（源连接组被删了？）");
        var footer = xaml.Substring(i, 640);

        // 文案改成"源连接"；点有了 x:Name（否则没有任何投影入口）；初始色是**红**而不是绿。
        Assert.Contains("Text=\"源连接\"", footer);
        Assert.Contains("<Ellipse x:Name=\"SourceStateDot\" Width=\"10\" Height=\"10\"", footer);
        Assert.Contains("Fill=\"{StaticResource DangerBrush}\"", footer);

        // ★ 2026-10-08 真机复验（返修 R3）★ 圆点必须放在**定尺寸容器里居中**、外层不再用固定宽：
        //   用户已用真机截图证明「源连接」标签变长后，外层固定宽会把 9~10 DIP 的状态圆点裁成非圆。
        //   现在：容器 12×12 居中 + 外层 Auto 宽（由底栏 Auto 列自适应）⇒ 三态与三档 DPI 都是完整圆。
        Assert.Contains("<Grid MinWidth=\"12\" Width=\"12\" Height=\"12\" VerticalAlignment=\"Center\">", footer);
        // ★ 2026-10-08 真机像素复验（返修 R3 二轮）★ 只有"12×12 容器居中"还不够：
        //   真机像素轮廓显示圆点被压成 7×10（横向少约 3 DIP），原因是底栏第 7 列是 Auto 列，
        //   当星列（进度轨道）被其内容撑住、总宽吃紧时，第 7 列会被压到内容所需宽度以下 ——
        //   而圆点容器是这一组的**最后一个**子元素，于是右边缘先被裁掉。
        //   现在给"整组"与"容器"各加 MinWidth 保底：这是结构性保障（Auto 列不会小于子元素 MinWidth），
        //   不是把固定宽度从 66 猜成 70 —— 组宽仍由内容驱动，只是不允许被压扁。
        Assert.Contains("Grid.Column=\"7\" Orientation=\"Horizontal\" Spacing=\"7\" VerticalAlignment=\"Center\" MinWidth=\"64\"", footer);
        Assert.Contains("HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\" Fill=\"{StaticResource DangerBrush}\"", footer);
        Assert.DoesNotContain("<StackPanel x:Name=\"FooterNetPanel\" Grid.Column=\"7\" Width=", xaml);

        // 旧缺陷字面量：无名绿点。它一旦回来，这个测试立刻红。
        Assert.DoesNotContain("<Ellipse Width=\"9\" Height=\"9\" Fill=\"{StaticResource SuccessBrush}\"/>", xaml);
    }

    [Fact]
    public void C5_IndicatorIsRecomputedOnSessionChanges_NotOnlyOnce()
    {
        var code = ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml.cs");
        var count = System.Text.RegularExpressions.Regex.Matches(code, "UpdateSourceConnectionIndicator\\(\\);").Count;
        Assert.True(count >= 3, $"UpdateSourceConnectionIndicator 的调用点只有 {count} 处（应 ≥3：VM 属性变化 + 窗口初始化 + 底栏推送）");
        Assert.Contains("Session.PropertyChanged", code);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    /// <summary>截取某个方法签名之后的窗口（够断言方法体即可，不追求精确配平）。</summary>
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