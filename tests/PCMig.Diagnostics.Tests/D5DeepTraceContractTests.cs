using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D5 剩余子项契约：Deep Trace 的**有限 Routed 输入观测**（方案 §9 / 用户 §13）。
///
/// 这里把"允许做什么 / 绝不允许做什么"逐条翻成机器判据（源码级，因为 WinUI 不能在本测试项目加载）：
///   · 必须 `handledEventsToo: true`（否则子控件处理过的事件根本到不了根，"点了没反应"就无从判断）；
///   · **只**记输入类别 + 稳定 ControlId + 可用/可见 + 抑制计数；
///   · **不**写 Handled / 不取焦点 / 不捕获指针 / 不改路由；
///   · **不**用全局钩子、**不**记坐标；
///   · **限时**（到期自动停）+ **限流**（同控件重复输入只累加计数）；
///   · 停止时**注销全部处理器**；关窗时也注销。
/// </summary>
public sealed class D5DeepTraceContractTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
        }
        throw new InvalidOperationException("找不到仓库根：" + AppContext.BaseDirectory);
    }

    private static string ReadWinUi(params string[] parts)
    {
        var path = Path.Combine(new[] { Root(), "src", "PCMig.WinUI" }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), "缺少文件：" + path);
        return File.ReadAllText(path);
    }

    /// <summary>去掉行注释：契约针对**代码**，不是文档注释里的举例。</summary>
    private static string StripCode(string text) =>
        string.Join("\n", text.Split('\n').Select(line =>
        {
            var i = line.IndexOf("//", StringComparison.Ordinal);
            return i >= 0 ? line.Substring(0, i) : line;
        }));

    private static string Observer => ReadWinUi("Diagnostics", "DeepTraceInputObserver.cs");
    private static string PanelCode => ReadWinUi("Views", "DiagnosticCenterPanel.xaml.cs");
    private static string MainWindowCode => ReadWinUi("MainWindow.xaml.cs");

    [Fact]
    public void ObserverRegistersHandledEventsTooOnTheAppsOwnRoot()
    {
        var code = StripCode(Observer);

        // handledEventsToo: true 是"能看到已被处理事件"的唯一手段（方案 §9 明确要求）。
        Assert.Equal(3, code.Split("handledEventsToo: true").Length - 1);
        Assert.Contains("UIElement.PointerPressedEvent", code, StringComparison.Ordinal);
        Assert.Contains("UIElement.TappedEvent", code, StringComparison.Ordinal);
        Assert.Contains("UIElement.KeyDownEvent", code, StringComparison.Ordinal);

        // 只挂在本应用自己的元素上（不是系统级监控）。
        Assert.DoesNotContain("SetWindowsHookEx", code, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAsyncKeyState", code, StringComparison.Ordinal);
        Assert.DoesNotContain("LowLevel", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ObserverNeverAltersRoutingFocusOrCapture()
    {
        var code = StripCode(Observer);

        foreach (var forbidden in new[]
                 {
                     "Handled = true", "e.Handled", ".Handled=", "CapturePointer", "ReleasePointerCapture",
                     "Focus(FocusState", "FocusManager", "KeyDown +=", "PreviewKeyDown",
                 })
        {
            Assert.DoesNotContain(forbidden, code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ObserverRecordsOnlyCategoryAndStableControlId()
    {
        var code = StripCode(Observer);

        // 稳定 ID 只来自 AutomationProperties（绝不退回显示文字/序号/坐标）。
        Assert.Contains("AutomationProperties.GetAutomationId", code, StringComparison.Ordinal);
        Assert.DoesNotContain("GetCursorPos", code, StringComparison.Ordinal);
        Assert.DoesNotContain("PointerPoint", code, StringComparison.Ordinal);

        // ★ D6.1 §1 更正 ★ 原先这里写的是 `Assert.DoesNotContain("e.Key.ToString", code)`——
        //   而真实缺陷的写法是 `"key:" + e.Key`（根本没有 `.ToString()`）⇒ **该断言恒真、抓不到缺陷**。
        //   现在改为断言"不再存在把按键身份拼进类别的形状"，并把真正的行为验证放到
        //   D61PrivacyInputObservationTests（合成 A/B/3/PasswordBox → 读真实 JSONL 与导出 ZIP）。
        Assert.DoesNotContain("\"key:\"", code, StringComparison.Ordinal);
        Assert.DoesNotContain("+ e.Key", code, StringComparison.Ordinal);
        Assert.Contains("InputObservationPolicy.ClassifyKey", code, StringComparison.Ordinal);

        // 不做文本采集：不触碰 TextBox/PasswordBox 的内容。
        Assert.DoesNotContain(".Text", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Clipboard", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ObserverIsTimeBoxedAndRateLimited()
    {
        var code = StripCode(Observer);

        Assert.Contains("DefaultMaxDuration", code, StringComparison.Ordinal);
        Assert.Contains("_deadlineUtc", code, StringComparison.Ordinal);
        Assert.Contains("DuplicateWindow", code, StringComparison.Ordinal);
        Assert.Contains("Suppressed", code, StringComparison.Ordinal);

        // 到期自动停（不需要用户记得关）——Tick 里必须有 Stop()。
        var tickIndex = code.IndexOf("_timer.Tick +=", StringComparison.Ordinal);
        Assert.True(tickIndex > 0);
        var tickBody = code.Substring(tickIndex, Math.Min(400, code.Length - tickIndex));
        Assert.Contains("Stop()", tickBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ObserverUnregistersEveryHandlerOnStop()
    {
        var code = StripCode(Observer);
        Assert.Equal(3, code.Split("RemoveHandler").Length - 1);
        Assert.Contains("_registered.Clear()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ToggleOrderSetsCaptureModeBeforeArmingObservation()
    {
        var panel = StripCode(PanelCode);

        // 顺序有语义：先切采集模式（Deep ⇒ Verbose 才会被接受），再让 Shell 注册观测。
        var modeIndex = panel.IndexOf("RequestDeepTrace(enabled)", StringComparison.Ordinal);
        var eventIndex = panel.IndexOf("DeepTraceToggled?.Invoke", StringComparison.Ordinal);
        Assert.True(modeIndex > 0 && eventIndex > modeIndex, "必须先切采集模式再注册输入观测");

        var mw = StripCode(MainWindowCode);
        Assert.Contains("DiagnosticCenterPanel.DeepTraceToggled += (_, enabled) => SetDeepTraceInputObservation(enabled)",
            mw, StringComparison.Ordinal);
        Assert.Contains("_deepTraceObserver.Start(root);", mw, StringComparison.Ordinal);
        Assert.Contains("_deepTraceObserver.RegisterOverlay(DiagnosticCenterPanel);", mw, StringComparison.Ordinal);
        Assert.Contains("Content is not FrameworkElement root", mw, StringComparison.Ordinal);

        // 关窗时必须注销（否则会往已卸载的树上留处理器）。
        var shutdownIndex = mw.IndexOf("private void ShutdownAndExit()", StringComparison.Ordinal);
        Assert.True(shutdownIndex > 0);
        var shutdownBody = mw.Substring(shutdownIndex, Math.Min(1600, mw.Length - shutdownIndex));
        Assert.Contains("_deepTraceObserver?.Stop();", shutdownBody, StringComparison.Ordinal);

        // 关窗注销必须排在诊断收尾之前（先停止产生新证据，再封段）。
        var stopIndex = shutdownBody.IndexOf("_deepTraceObserver?.Stop();", StringComparison.Ordinal);
        var diagIndex = shutdownBody.IndexOf("DiagnosticBootstrap.Shutdown();", StringComparison.Ordinal);
        Assert.True(stopIndex < diagIndex, "注销输入观测必须早于诊断收尾");
    }

    [Fact]
    public void InputObservedIsVerboseSoItIsOnlyCollectedInDeep()
    {
        // 这条事件必须是 Verbose：Operational 模式下会被过滤（不打扰日常使用），
        // 只有用户显式开 Deep 才会被接受——与"Deep 才允许观察 Routed Input"的边界一致。
        Assert.Equal(Abstractions.DeliveryClass.Verbose, Abstractions.Events.UiEvents.InputObserved.Delivery);
        Assert.Equal("UiInputObserved", Abstractions.Events.UiEvents.InputObserved.PayloadName);
    }
}