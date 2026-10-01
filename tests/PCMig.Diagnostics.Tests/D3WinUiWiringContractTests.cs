using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D3a 契约：WinUI 侧的连接垂直切片接线（**源码级契约**）。
///
/// 为什么是源码级：WinUI 需要 net8.0-windows + Windows App SDK，不能塞进本测试项目
/// （会污染 testhost 配置，且该工程不在 sln 里）。因此这里锁的是"接线存在 + 边界未被破坏"，
/// 真实渲染/交互另由真机截图闭环覆盖（D5）。
/// </summary>
public sealed class D3WinUiWiringContractTests
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

    [Fact]
    public void BootstrapStartsEarlyAndIsFailOpen()
    {
        var app = ReadWinUi("App.xaml.cs");
        var bootstrap = ReadWinUi("Diagnostics", "DiagnosticBootstrap.cs");

        // 尽早启动：在创建 MainWindow 之前。
        var startIndex = app.IndexOf("DiagnosticBootstrap.Start(", StringComparison.Ordinal);
        var windowIndex = app.IndexOf("MainWindowInstance = new MainWindow();", StringComparison.Ordinal);
        Assert.True(startIndex > 0, "App.OnLaunched 必须启动诊断");
        Assert.True(windowIndex > startIndex, "诊断必须在创建主窗口之前启动");

        // fail-open：装配失败不得抛给启动路径。
        Assert.Contains("catch (Exception)", bootstrap, StringComparison.Ordinal);
        Assert.Contains("保持 NoOp", bootstrap, StringComparison.Ordinal);

        // 未处理异常：记录但**不吞**（既有策略不变）。
        Assert.Contains("RecordUnhandledException", app, StringComparison.Ordinal);
        Assert.DoesNotContain("e.Handled = true", app, StringComparison.Ordinal);
    }

    [Fact]
    public void ShutdownIsBoundedAndOrderedBeforeProcessExit()
    {
        var mw = ReadWinUi("MainWindow.xaml.cs");
        var bootstrap = ReadWinUi("Diagnostics", "DiagnosticBootstrap.cs");

        var stopUi = mw.IndexOf("Session.StopUiRefresh();", StringComparison.Ordinal);
        var diag = mw.IndexOf("DiagnosticBootstrap.Shutdown();", StringComparison.Ordinal);
        var exit = mw.IndexOf("Environment.Exit(0);", StringComparison.Ordinal);

        Assert.True(stopUi > 0 && diag > 0 && exit > 0);
        Assert.True(stopUi < diag, "诊断收尾必须排在 StopUiRefresh 之后（不再产生新的 UI 事件）");
        Assert.True(diag < exit, "诊断收尾必须排在 Environment.Exit(0) 之前");
        Assert.Contains("TimeSpan.FromMilliseconds", bootstrap, StringComparison.Ordinal);
        Assert.Contains("ShutdownIncomplete", bootstrap, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectSlicePublishesTheWholeActionChainAndOnlyObservesTheExistingGuard()
    {
        var page = ReadWinUi("Views", "Step1ConnectPage.xaml.cs");
        var vm = ReadWinUi("Presentation", "ConnectionViewModel.cs");

        // ① 动作链的每个环节都必须有观察点。
        // ★ D6.1 §13 更新 ★ 投影回读从 `ReadEnabled(期望值当实际值, matchesSource:true)` 改为
        //   `ReadControl(真实控件, expected..., 代际)` ⇒ 这里同步要求新形状（**加强**而非削弱：
        //   还额外要求它必须读真实控件、必须给期望值与代际）。
        foreach (var token in new[]
                 {
                     "ActionTrace.Begin(ActionKinds.Connect", "ControlIds.Step1Connect",
                     ".Eligibility(", ".Started()", ".Expect(", ".Confirm(", ".Complete(",
                     ".Reject(", ".Fault(",
                     "ProjectionObserver.ReadControl(", "ControlIds.Step1EmptySharesHint",
                     "expectedEnabled:", "expectedVisibility:", "sourceObservationVersion:",
                 })
        {
            Assert.Contains(token, page, StringComparison.Ordinal);
        }

        // ② 只观察**原有** guard：既有判据必须仍在（没有被"为了诊断"改写或重复调用）。
        Assert.Contains("_vm is { IsConnecting: false }", page, StringComparison.Ordinal);
        Assert.Contains("var permitted = _vm is { IsConnecting: false };", page, StringComparison.Ordinal);

        // ③ VM 侧用 UI 无关路径发布（本文件会被既有测试项目按源码链入 ⇒ 不得依赖 WinUI 诊断层）。
        Assert.Contains("PCMig.Core.Diagnostics.CoreDiagnostics.PublishUiAction(", vm, StringComparison.Ordinal);
        Assert.DoesNotContain("PCMig.WinUI.Diagnostics", vm, StringComparison.Ordinal);

        // ④ 口令绝不出现在任何诊断调用里（连长度都不记）。
        Assert.DoesNotContain("ProjectionObserver.ReadControl(\n                PasswordInput", page, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectionObserver.ReadEnabled(", page, StringComparison.Ordinal);
        Assert.DoesNotContain("PasswordInput.Password,", page.Replace("await _vm.ConnectAsync(PasswordInput.Password);", string.Empty), StringComparison.Ordinal);
        Assert.DoesNotContain("Token(PasswordInput", page, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectionObserverRefusesToReadSecretControls()
    {
        var observer = ReadWinUi("Diagnostics", "ProjectionObserver.cs");

        // 只接受"控件 ID + 布尔/枚举状态"，没有任何接收文本/口令的入口。
        Assert.DoesNotContain("PasswordBox", observer, StringComparison.Ordinal);
        Assert.DoesNotContain(".Password", observer, StringComparison.Ordinal);
        Assert.Contains("不证明屏幕像素", observer, StringComparison.Ordinal);   // L2 的诚实边界必须写在代码里
    }

    [Fact]
    public void ControlIdsAreStableTokensNotDisplayText()
    {
        var ids = ReadWinUi("Diagnostics", "ControlIds.cs");

        foreach (var id in new[]
                 {
                     "\"Step1.Connect\"", "\"Step1.AddShare\"", "\"Step1.PasswordInput\"",
                     "\"Shell.Transfer.Start\"", "\"Shell.Transfer.Pause\"", "\"Shell.Transfer.Stop\"",
                     "\"Shell.Transfer.Resume\"", "\"Shell.Tool.Diagnostics\"",
                 })
        {
            Assert.Contains(id, ids, StringComparison.Ordinal);
        }

        // 不允许中文（显示文字）成为控件 ID。
        Assert.DoesNotContain("= \"连接", ids, StringComparison.Ordinal);
        Assert.DoesNotContain("= \"暂停", ids, StringComparison.Ordinal);
    }

    [Fact]
    public void DescriptorEventsAreUsedInsteadOfRawStringsInNewUiCode()
    {
        var page = ReadWinUi("Views", "Step1ConnectPage.xaml.cs");
        var trace = ReadWinUi("Diagnostics", "ActionTrace.cs");

        // 事件必须来自 catalog 的强类型描述符，不得拼 "UI-001" 这类字面量。
        Assert.Contains("UiEvents.UserActionObserved", trace, StringComparison.Ordinal);
        Assert.Contains("UiEvents.CommandEligibilityEvaluated", trace, StringComparison.Ordinal);
        Assert.Contains("UiEvents.ActionCompleted", trace, StringComparison.Ordinal);
        Assert.DoesNotContain("\"UI-001\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("\"UI-001\"", trace, StringComparison.Ordinal);
    }
}