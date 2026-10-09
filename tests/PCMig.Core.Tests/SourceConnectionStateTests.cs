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
///   ② 三态语义：未连接=红常亮 / 正在连接=琥珀呼吸 / 已连接=绿常亮；ping 通、能上网、网卡 UP 都不得变绿；
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

    // ── 契约：状态源纪律（View 不猜） ───────────────────────────────────────────

    [Fact]
    public void C1_SourceStateIsDerivedInExactlyOnePlace_FromConnectionFactsOnly()
    {
        var code = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "ConnectionViewModel.cs");

        // 两个连接事实的 setter 都必须同步派生（少一个就会出现"绿灯停在旧状态"）。
        Assert.Contains("private set { if (Set(ref _isConnecting, value)) { Raise(nameof(CanConnect)); UpdateSourceState(); } }", code);
        Assert.Contains("private set { if (Set(ref _isConnected, value)) UpdateSourceState(); }", code);

        // 三分支语义：正在连接 ⇒ Connecting；已连接（预检通过 / 至少一个共享可访问）⇒ Connected；否则 Disconnected。
        Assert.Contains("SourceState = IsConnecting ? SourceConnectionState.Connecting", code);
        Assert.Contains(": IsConnected ? SourceConnectionState.Connected", code);
        Assert.Contains(": SourceConnectionState.Disconnected;", code);

        // 判据不得来自 ICMP / 网卡 / 互联网：这个文件里根本没有这些概念
        // （绿 = SMB 源共享可用，不是"能 ping 通"、不是"网卡 UP"、不是"能上网"）。
        Assert.DoesNotContain("ICMP", code);
        Assert.DoesNotContain("System.Net.NetworkInformation", code);
        Assert.DoesNotContain("NetworkListManager", code);
    }

    [Fact]
    public void C2_IndicatorProjectsThreeStatesFromThemeTokens_NotHardcodedColors()
    {
        var code = ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml.cs");

        Assert.Contains("private const string SourceStateConnectedBrushKey = \"SuccessBrush\";", code);
        Assert.Contains("private const string SourceStateConnectingBrushKey = \"WarningBrush\";", code);
        Assert.Contains("private const string SourceStateDisconnectedBrushKey = \"DangerBrush\";", code);

        var body = MethodWindow(code, "private void UpdateSourceConnectionIndicator()", 1800);

        // 颜色取自主题 Token（此处不写死 #RRGGBB），且颜色不是唯一通道：状态名必须可读。
        Assert.Contains("SourceStateDot.Fill = brush;", body);
        Assert.Contains("源连接：已连接", body);
        Assert.Contains("源连接：正在连接", body);
        Assert.Contains("源连接：未连接", body);
        Assert.DoesNotContain("#", body.Replace("#x", string.Empty));
    }

    [Fact]
    public void C3_OnlyConnectingBreathes_AndItIsComposition_NotDispatcherTimer()
    {
        var window = ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml.cs");
        var body = MethodWindow(window, "private void UpdateSourceConnectionIndicator()", 1800);

        Assert.Contains("if (state == SourceConnectionState.Connecting) MotionDirector.PlaySourceBreathing(SourceStateDot);", body);
        Assert.Contains("else MotionDirector.ResetSourceBreathing(SourceStateDot);", body);
        // 用户明令：呼吸不得用 DispatcherTimer 改颜色（那会变成生硬的开/关闪烁）。
        Assert.DoesNotContain("DispatcherTimer", body);

        var motion = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MotionDirector.cs");
        var breath = MethodWindow(motion, "public static void PlaySourceBreathing(FrameworkElement? element)", 2200);

        Assert.Contains("ElementCompositionPreview.GetElementVisual(element)", breath);
        Assert.Contains("visual.StartAnimation(\"Opacity\", breath);", breath);
        Assert.Contains("breath.IterationBehavior = AnimationIterationBehavior.Forever;", breath);
        Assert.Contains("if (!SystemAnimationsEnabled)", breath);          // Reduced Motion ⇒ 常亮，不呼吸
        Assert.DoesNotContain("DispatcherTimer", breath);

        // 周期 / 最低透明度来自 Motion.xaml token（调用点不写死数值）。
        Assert.Contains("SourceBreathDurationKey", breath);
        Assert.Contains("SourceBreathMinOpacityKey", breath);

        var xaml = ReadRepoFile("src", "PCMig.WinUI", "Themes", "Motion.xaml");
        Assert.Contains("<Duration x:Key=\"PCMigMotionSourceBreathDuration\">0:0:1.05</Duration>", xaml);
        Assert.Contains("<x:Double x:Key=\"PCMigMotionSourceBreathMinOpacity\">0.35</x:Double>", xaml);
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