using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D6.1 §17 + §18 收口测试（源码级契约，因为 WinUI 不能在本测试项目加载）：
///   §17 诊断面板的**生命周期收口**必须在所有关闭路径上生效
///       （点入口关 / 点 × 关 / 与材质调节或更新日志互切 / 关窗）；
///   §18 导出必须**不在 UI 线程**上做 seal/cutoff/脱敏/压缩/哈希，且运行中可取消。
/// </summary>
public sealed class D61PanelLifecycleAndAsyncExportTests
{
    private static string ReadWinUi(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln")))
            {
                var path = Path.Combine(new[] { dir.FullName, "src", "PCMig.WinUI" }.Concat(parts).ToArray());
                Assert.True(File.Exists(path), "缺少文件：" + path);
                return File.ReadAllText(path);
            }
        }
        throw new InvalidOperationException("找不到仓库根");
    }

    // ────────────────────────── §17 生命周期 ──────────────────────────

    [Fact]
    public void EveryPanelClosePathStopsTheRefreshAndSubscription()
    {
        var main = ReadWinUi("MainWindow.xaml.cs");

        // 统一收口点必须存在，并且**在折叠可见性的同一处**被调用（覆盖入口/×/互切三条路径）。
        Assert.Contains("private void StopPanelRefresh(FrameworkElement panel)", main, StringComparison.Ordinal);
        Assert.Contains("StopPanelRefresh(panel);", main, StringComparison.Ordinal);
        Assert.Contains("DiagnosticCenterPanel.StopRefresh();", main, StringComparison.Ordinal);

        var afterClosed = main.IndexOf("void AfterClosed()", StringComparison.Ordinal);
        Assert.True(afterClosed > 0, "找不到浮层关闭后的收口回调");
        var body = main.Substring(afterClosed, Math.Min(700, main.Length - afterClosed));
        Assert.Contains("panel.Visibility = Visibility.Collapsed;", body, StringComparison.Ordinal);
        Assert.Contains("StopPanelRefresh(panel);", body, StringComparison.Ordinal);   // ★ 与折叠同一处 ★

        // 关窗路径也必须停（面板可能还开着）。
        var shutdown = main.IndexOf("private void ShutdownAndExit()", StringComparison.Ordinal);
        Assert.True(shutdown > 0);
        var shutdownBody = main.Substring(shutdown, Math.Min(2200, main.Length - shutdown));
        Assert.Contains("StopPanelRefresh(DiagnosticCenterPanel);", shutdownBody, StringComparison.Ordinal);

        // 关闭顺序：停表 → 诊断收尾（先不再产生 UI 读取，再封段）。
        var stopRefresh = shutdownBody.IndexOf("StopPanelRefresh(DiagnosticCenterPanel);", StringComparison.Ordinal);
        var diagShutdown = shutdownBody.IndexOf("DiagnosticBootstrap.Shutdown();", StringComparison.Ordinal);
        Assert.True(stopRefresh < diagShutdown, "停刷新必须早于诊断收尾");
    }

    [Fact]
    public void PanelRefreshIsRestartableOnReopenAndDoesNotTouchBackgroundCollection()
    {
        var panel = ReadWinUi("Views", "DiagnosticCenterPanel.xaml.cs");

        // 打开时重新起表（Attach 里），关闭时停表。
        Assert.Contains("public void Attach(DiagnosticCenterViewModel viewModel)", panel, StringComparison.Ordinal);
        Assert.Contains("StartTimer();", panel, StringComparison.Ordinal);
        Assert.Contains("public void StopRefresh()", panel, StringComparison.Ordinal);

        // 面板只碰 UI 侧：不得去启停 writer/analyzer/flight（后台采集不受 UI 生命周期影响）。
        foreach (var forbidden in new[] { "ShutdownAsync", "StopBranchAsync", "FlightRecorder", "JsonlSegmentWriter" })
            Assert.DoesNotContain(forbidden, panel, StringComparison.Ordinal);
    }

    // ────────────────────────── §18 异步导出 ──────────────────────────

    [Fact]
    public void ExportRunsOffTheUiThreadAndIsCancellable()
    {
        var main = ReadWinUi("MainWindow.xaml.cs");

        // 导出必须在 Task.Run 里做（cutoff/封段/脱敏/压缩/哈希都不占 UI 线程）。
        var handlerIndex = main.IndexOf("ExportDiagnosticPackageAsync(", StringComparison.Ordinal);
        Assert.True(handlerIndex > 0);
        var handlerBody = main.Substring(handlerIndex, Math.Min(3000, main.Length - handlerIndex));
        Assert.Contains("Task.Run(() =>", handlerBody, StringComparison.Ordinal);
        Assert.Contains("PrepareExportCutoff();", handlerBody, StringComparison.Ordinal);
        Assert.Contains(".Export(request, ct)", handlerBody, StringComparison.Ordinal);
        // 取消必须被如实回显（不假装成功）。
        Assert.Contains("catch (OperationCanceledException)", handlerBody, StringComparison.Ordinal);
        Assert.Contains("\"canceled\"", handlerBody, StringComparison.Ordinal);

        // 面板：运行中再点一次 = 取消（不新增控件），并且状态行给出提示。
        var panel = ReadWinUi("Views", "DiagnosticCenterPanel.xaml.cs");
        Assert.Contains("private CancellationTokenSource? _exportCts;", panel, StringComparison.Ordinal);
        Assert.Contains("_exportCts.Cancel()", panel, StringComparison.Ordinal);
        Assert.Contains("_vm.ExportAsync(exportCts.Token)", panel, StringComparison.Ordinal);
        Assert.Contains("可取消", panel, StringComparison.Ordinal);

        // 旧缺陷形状必须消失：不得再在 UI 线程同步跑完再 Task.FromResult。
        Assert.DoesNotContain("Task.FromResult(\n                new DiagnosticExportResult(outcome.Succeeded", main, StringComparison.Ordinal);
    }
}