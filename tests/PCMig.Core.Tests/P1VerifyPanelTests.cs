using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ P1-12 / 验收 R9 / PMML 返修（2026-10-11）★ 验证完整性**应用内面板**的源码契约测试。
///
/// 历史：这里原本守的是"独立的验证二级窗口"（`VerifyDetailWindow`）。PMML 一致性返修后，
/// 验证进度改为**应用内工具浮层**（`VerificationProgressPanel`），与诊断中心同一 Utility Surface 家族：
/// 位置由 TAOP（TryPlacePanel）按真实锚点算、开合由 FluidZoomTransitionCoordinator 播正反向 morph。
/// 本文件因此同时守住"旧窗口确实已经不在了"这条回归线。
///
/// 说明（为什么放在 Core 测试工程里）：WinUI 的界面不能在无头测试进程里真的显示出来，
/// 因此这里只守住"结构必须存在、纪律必须被写进源码"这一层；**真实渲染/操作证据**由
/// 复验包里的实测记录（面板出现、真实计数、点取消真的取消）承担 —— 两者不许互相冒充。
/// 本文件守住的核心不变量：
///   ① 没有快照就不许出现"看起来在跑"的百分比（BeginRun ⇒ 不定进度 + "—"）；
///   ② 对象结论必须来自报告的四态 StatusText，不许退回二值 ✔/✘；
///   ③ 取消路径不得把界面写成"通过"，且"收起面板 ≠ 取消验证"；
///   ④ 面板自己不许做动画/定位/改 MaxHeight（那是 Shell 的职责）；
///   ⑤ 面板开不出来不许阻断验证（Step4 必须容 null）。
/// </summary>
public sealed class P1VerifyPanelTests
{
    private static string PanelXaml => Read("src", "PCMig.WinUI", "Views", "VerificationProgressPanel.xaml");
    private static string PanelCode => Read("src", "PCMig.WinUI", "Views", "VerificationProgressPanel.xaml.cs");
    private static string PageCode => Read("src", "PCMig.WinUI", "Views", "Step4ResultPage.xaml.cs");
    private static string ShellXaml => Read("src", "PCMig.WinUI", "MainWindow.xaml");
    private static string ShellCode => Read("src", "PCMig.WinUI", "MainWindow.xaml.cs");
    private static string VmCode => Read("src", "PCMig.WinUI", "Presentation", "MigrationSessionViewModel.cs");

    [Fact]
    public void P1_12_验证进度是应用内面板而不是独立二级窗口()
    {
        Assert.Contains("x:Class=\"PCMig.WinUI.Views.VerificationProgressPanel\"", PanelXaml);
        Assert.Contains("<UserControl", PanelXaml);
        Assert.Contains("AutomationProperties.AutomationId=\"Shell.Panel.Verification\"", PanelXaml);

        // 与诊断中心完全同源的 Utility Surface 几何/材质（不新造画刷、不新造光源）
        Assert.Contains("PCMigUtilityPanelMaterial80", PanelXaml);
        Assert.Contains("PCMigDAELBrushQuiet", PanelXaml);
        Assert.Contains("CornerRadius=\"18\"", PanelXaml);
        Assert.Contains("Translation=\"0,0,24\"", PanelXaml);
        Assert.Contains("Width=\"820\" MaxHeight=\"720\"", PanelXaml);

        // 它**不是**窗口：没有 Window 元素、不自己调 AppWindow
        Assert.DoesNotContain("<Window", PanelXaml);
        Assert.DoesNotContain("AppWindow", PanelCode);

        // ★ 回归线：旧独立二级窗口必须真的被删掉（不许留下孤儿文件让两条路径并存）★
        Assert.False(File.Exists(Absolute("src", "PCMig.WinUI", "Views", "VerifyDetailWindow.xaml")),
            "旧的独立验证窗口 XAML 仍然存在：PMML 返修后验证进度只能有面板这一条路径。");
        Assert.False(File.Exists(Absolute("src", "PCMig.WinUI", "Views", "VerifyDetailWindow.xaml.cs")),
            "旧的独立验证窗口代码后置仍然存在：PMML 返修后验证进度只能有面板这一条路径。");

        // 真实的四个计数器 + 结论清单 + 取消/关闭按钮
        Assert.Contains("x:Name=\"ObjectCountText\"", PanelXaml);
        Assert.Contains("x:Name=\"FilesCheckedText\"", PanelXaml);
        Assert.Contains("x:Name=\"BytesCheckedText\"", PanelXaml);
        Assert.Contains("x:Name=\"ElapsedText\"", PanelXaml);
        Assert.Contains("x:Name=\"ObjectList\"", PanelXaml);
        Assert.Contains("x:Name=\"CancelButton\"", PanelXaml);
        Assert.Contains("Click=\"CancelButton_Click\"", PanelXaml);
        Assert.Contains("x:Name=\"ClosePanelButton\"", PanelXaml);
    }

    [Fact]
    public void P1_12_没有引擎快照之前不许显示任何百分比()
    {
        // ★ 初始文案只许是"等待引擎回报"，不许预填 0/0（0 会被读成"已经核对完了 0 个"）★
        Assert.Contains("Text=\"等待引擎回报…\"", PanelXaml);
        Assert.Contains("Text=\"—\"", PanelXaml);

        var begin = Segment(PanelCode, "public void BeginRun()", "\n    }");
        Assert.Contains("ObjectProgressBar.IsIndeterminate = true;", begin);
        Assert.Contains("PercentText.Text = \"—\";", begin);
        Assert.Contains("ObjectCountText.Text = \"无\";", begin);
        Assert.Contains("界面不估算、不预测。", begin);

        // L1 的语义边界必须写在界面上（不许让用户以为这是逐文件 SHA256 全量一致）
        Assert.Contains("L1 只核对文件数量与总字节", PanelXaml);
    }

    [Fact]
    public void P1_12_面板只显示引擎给的真实数据且跨线程安全()
    {
        Assert.Contains("public void ApplySnapshot(VerifyProgressSnapshot s)", PanelCode);
        Assert.Contains("ObjectProgressBar.Value = s.Percent;", PanelCode);
        Assert.Contains("PercentText.Text = s.Percent.ToString(\"0.0\") + \"%\";", PanelCode);
        Assert.Contains("StatusLineText.Text = s.Describe();", PanelCode);
        Assert.Contains("public void ApplyLine(string line)", PanelCode);

        // 快照来自引擎后台线程 ⇒ 必须走 DispatcherQueue，且界面刷新失败不许影响验证
        Assert.Contains("queue.TryEnqueue", PanelCode);
        Assert.Contains("catch { /* 界面刷新失败绝不影响验证（只读投影） */ }", PanelCode);
        Assert.Contains("_snapshotSeen = true;", PanelCode);

        // 任何"估算/预计"的字段或文案都不许出现在面板里
        Assert.DoesNotContain("预计剩余", PanelCode);
        Assert.DoesNotContain("Timer", PanelCode);
        Assert.DoesNotContain("Random", PanelCode);
    }

    [Fact]
    public void P1_12_对象结论用四态文案且差异分类可复述()
    {
        Assert.Contains("StatusText = o.StatusText", PanelCode);
        Assert.DoesNotContain("\"✔", PanelCode);
        Assert.DoesNotContain("\"✘", PanelCode);
        Assert.Contains("public string StatusText", Read("src", "PCMig.Core", "Models", "Models.cs"));

        // 四态 + 结构化差异分类（确认缺失 / 目标多余 / 大小 / 哈希 / 两不可达）
        Assert.Contains("确认缺失", PanelCode);
        Assert.Contains("目标多余", PanelCode);
        Assert.Contains("不自动清理", PanelCode);
        Assert.Contains("UnknownReason", PanelCode);
        Assert.Contains("AllowsDestructiveRepair", PanelCode);
    }

    [Fact]
    public void P1_12_取消只请求取消且绝不把界面写成通过()
    {
        // 取消处理器由页面层注册（面板自己不持有 CTS ⇒ 无法"假取消"）
        Assert.Contains("public void SetCancelHandler(Action handler)", PanelCode);
        Assert.Contains("_onCancelRequested?.Invoke();", PanelCode);
        Assert.Contains("CancelButton.Content = \"正在取消…\";", PanelCode);
        Assert.Contains("if (_finished || _cancelRequested) return;", PanelCode);

        // 取消/无报告 ⇒ 明确"没有结论"，绝不含糊成通过
        Assert.Contains("验证已被取消：没有结论（取消不等于通过）。", PanelCode);
        Assert.Contains("总体结论：已取消（没有结论）", PanelCode);
        Assert.Contains("总体结论：没有报告", PanelCode);
        Assert.Contains("不能当作通过", PanelCode);
        // 取消后不许再显示百分比
        Assert.Contains("PercentText.Text = canceled ? \"—\" : \"无\";", PanelCode);
    }

    [Fact]
    public void P1_12_面板不做动画与定位且关闭不等于取消()
    {
        // 面板自己不许播动画/定位（那是 Shell 的 FluidZoom + TAOP 的职责）
        Assert.DoesNotContain("Storyboard", PanelCode);
        Assert.DoesNotContain("Begin()", PanelCode);
        Assert.DoesNotContain("Canvas.SetLeft", PanelCode);

        // 关闭只发事件（由 Shell 播反向 morph 后折叠）
        Assert.Contains("public event EventHandler? CloseRequested;", PanelCode);
        Assert.Contains("CloseRequested?.Invoke(this, EventArgs.Empty);", PanelCode);
        Assert.Contains("关闭 ≠ 取消：收起面板后验证状态不变，可再次打开查看。", PanelCode);
    }

    [Fact]
    public void P1_12_打开面板与显式开始验证必须严格分离()
    {
        var open = Segment(PageCode, "private void ToolbarVerify_Click", "private async Task StartVerificationAsync");
        Assert.Contains("EnsureVerifyPanel(session)", open);
        Assert.DoesNotContain("EvaluateVerifyGateAsync", open);
        Assert.DoesNotContain("VerifyAsync", open);
        Assert.DoesNotContain("BeginRun", open);
        Assert.DoesNotContain("BeginPreflight", open);

        var start = Segment(PageCode, "private async Task StartVerificationAsync", "private VerificationProgressPanel? EnsureVerifyPanel");
        Assert.Contains("if (_verifyCts is not null) return;", start);
        Assert.Contains("panel.BeginRun();", start);
        Assert.Contains("panel.BeginPreflight();", start);
        Assert.Contains("await session.VerifyAsync(VerifyLevel.L1_CountSize, progress, cts.Token, detail);", start);
        Assert.DoesNotContain("EvaluateVerifyGateAsync", start); // VM 内唯一闸门，避免打开或开始时双重 IO

        Assert.Contains("x:Name=\"StartVerifyButton\"", PanelXaml);
        Assert.Contains("Click=\"StartVerifyButton_Click\"", PanelXaml);
        Assert.Contains("Shell.Panel.Verification.StartButton", PanelXaml);
        Assert.Contains("Grid.Column=\"2\"", PanelXaml);
        Assert.Contains("public void SetStartHandler(Func<Task> handler)", PanelCode);
        Assert.Contains("if (_startRequested || _onStartRequested is null) return;", PanelCode);
        Assert.Contains("public void ShowIdle()", PanelCode);
        Assert.Contains("打开面板不会启动验证，也不会访问源或目标。", PanelCode);
        Assert.Contains("public void BeginPreflight()", PanelCode);
        Assert.Contains("OutcomeText.Text = \"正在检查前置条件…\";", PanelCode);
        Assert.Contains("public void FinishBlocked(string reason)", PanelCode);
        Assert.Contains("OutcomeText.Text = \"总体结论：未开始（前置闸门拒绝）\";", PanelCode);
    }

    [Fact]
    public void P1_12_Step4页必须只打开面板且可恢复已有结果()
    {
        Assert.Contains("EnsureVerifyPanel", PageCode);
        Assert.Contains("private VerificationProgressPanel? EnsureVerifyPanel(", PageCode);
        Assert.Contains("if (App.MainWindowInstance is not MainWindow shell) return null;", PageCode);
        Assert.Contains("shell.ShowVerificationPanel(ToolbarVerify);", PageCode);
        Assert.Contains("panel.SetStartHandler(() => StartVerificationAsync(session, panel));", PageCode);
        Assert.Contains("if (session.LastVerifyReport is not null)", PageCode);
        Assert.Contains("panel.ShowPreviousResult(session.LastVerifyReport, session.LastVerifyResultText, SourcePathMap(session));", PageCode);

        // 严禁退回旧窗口路径（★ 断言前必须剥掉注释：源码里解释历史的注释会提到旧类型名 ★）
        var pageCodeOnly = CodeOnly(PageCode);
        Assert.DoesNotContain("VerifyDetailWindow", pageCodeOnly);
        Assert.DoesNotContain("EnsureVerifyWindow", pageCodeOnly);
        Assert.DoesNotContain("new Window()", pageCodeOnly);
    }

    [Fact]
    public void P1_12_Shell必须用既有TAOP与FluidZoom编排面板()
    {
        // XAML：与另外三个工具浮层并列的同一个根 Grid 子元素（不新建 overlay 层、不自己定位）
        Assert.Contains("<views:VerificationProgressPanel x:Name=\"VerificationProgressPanel\"", ShellXaml);
        Assert.Contains("Visibility=\"Collapsed\"", ShellXaml);

        Assert.Contains("public VerificationProgressPanel VerificationPanel => VerificationProgressPanel;", ShellCode);
        Assert.Contains("public void ShowVerificationPanel(FrameworkElement? anchor)", ShellCode);
        Assert.Contains("VerificationProgressPanel.CloseRequested += (_, _) => ClosePanel(VerificationProgressPanel);", ShellCode);
        Assert.Contains("private const double VerificationPanelWidth = 820;", ShellCode);
        // 定位必须走既有 TryPlacePanel（TAOP），且 Resize 后也要按真实锚点重算
        Assert.Contains("TryPlacePanel(VerificationProgressPanel,", ShellCode);
        Assert.Contains("_verificationAnchor", ShellCode);
        // 互斥面板（other.Visibility 不能是 null）
        Assert.Contains("private FrameworkElement OtherVisibleToolPanel(FrameworkElement self)", ShellCode);
        Assert.DoesNotContain("new VerifyDetailWindow", CodeOnly(ShellCode));
    }

    [Fact]
    public void P1_12_验证必须离开UI线程跑且进度来自结构化快照()
    {
        // ★ 实测得出（96,000 文件）：Verifier.RunAsync 在对象循环前是同步枚举，
        //   直接 await 会把 UI 线程占住到枚举结束 —— 面板来不及出现、取消按钮永远点不到 ★
        Assert.Contains("report = await Task.Run(() => verifier.RunAsync(level, progress, ct, detail), ct);", VmCode);
        Assert.DoesNotContain("report = await verifier.RunAsync(level, progress, ct, detail);", VmCode);
        Assert.Contains("验证面板来不及", VmCode);
    }

    /// <summary>
    /// ★ 教训（2026-10-11，第二次踩到）★ 负向源码断言必须**先剥掉注释**：
    /// 解释历史/纪律的注释里会提到被禁的旧类型名与旧写法，直接 DoesNotContain 会把"记录历史的注释"误判成违规。
    /// 局限：按行处理，字符串字面量里的 `//` 也会被截断 —— 本文件只用于"某写法不许再出现"这类断言，够用。
    /// </summary>
    private static string CodeOnly(string source)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var idx = lines[i].IndexOf("//", StringComparison.Ordinal);
            if (idx >= 0) lines[i] = lines[i][..idx];
        }
        return string.Join("\n", lines);
    }

    private static string Segment(string text, string from, string to)
    {
        var i = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(i >= 0, $"没找到起点：{from}");
        var j = text.IndexOf(to, i, StringComparison.Ordinal);
        Assert.True(j > i, $"没找到终点：{to}");
        return text[i..(j + to.Length)];
    }

    private static string Absolute(params string[] parts) => Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray());

    private static string Read(params string[] parts)
    {
        var path = Absolute(parts);
        Assert.True(File.Exists(path), "缺少文件：" + path);
        return File.ReadAllText(path);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}