using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// WinUI Step 1 静态契约回归（真源码 / 真 csproj 扫描）。
///
/// 【为什么存在】
/// WinUI 3 外壳（<c>src\PCMig.WinUI</c>）与既有 WPF GUI 并行存在，Step 1 只做
/// "连接旧电脑 + 发现共享"。这段时间最容易出的两类静默退化是：
///   1. 外壳反向长出一个"第二套引擎"——把 Robocopy / TransferOrchestrator 直接
///      拖进 ViewModel，于是 WPF 与 WinUI 两条 UI 各有一套迁移逻辑，行为必然分叉；
///   2. 项目文件漂移——WinUI 项目多挂一个 ProjectReference（例如顺手引用 PCMig.Gui），
///      或者 unpackaged/self-contained 三项开关被改回默认，发布版立刻变成
///      "装不上 Windows App Runtime 就启动失败"，而本机开发机察觉不到。
/// 这两类缺陷**编译都过、单测都不报、只有真机才暴露**，因此必须用静态契约扫描拦住。
///
/// 【运行级别：L0 静态契约扫描】
/// 本用例只读源码树文本，不加载 WinUI 运行时（WinUI 需要 net8.0-windows +
/// Windows App SDK，塞进本测试项目会污染已有 109 个用例的 testhost 配置）。
/// 因此它证明的是"契约声明存在"，不是"UI 真的渲染出来了"——真机可见性另由
/// 手工截图闭环覆盖。
///
/// ⚠ 本文件是测试自身，允许书写被禁止的反例字符串（如 Robocopy）；
///   SourceTreeHygieneTests 的裸转义扫描已按路径跳过 Tests 目录。
/// </summary>
public sealed class WinUiStep1ContractTests
{
    /// <summary>从测试程序集目录向上定位含 PCMig.sln 的仓库根（与既有测试同款定位模式）。</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
        }
        throw new InvalidOperationException("找不到含 PCMig.sln 的仓库根：" + AppContext.BaseDirectory);
    }

    private static string Read(string root, params string[] parts)
    {
        var path = Path.Combine(new[] { root }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), "缺少契约文件：" + path);
        return File.ReadAllText(path);
    }

    /// <summary>去掉全部空白并小写化：让契约断言不受换行/缩进/大小写排版影响。</summary>
    private static string Flatten(string text) =>
        Regex.Replace(text, @"\s+", string.Empty).ToLowerInvariant();

    /// <summary>取出 csproj 里每一个 &lt;ProjectReference&gt; 的 Include 值。</summary>
    private static IReadOnlyList<string> ExtractProjectReferences(string csproj)
    {
        var includes = new List<string>();
        foreach (Match tag in Regex.Matches(csproj, @"<ProjectReference\b[^>]*>"))
        {
            var include = Regex.Match(tag.Value, @"Include\s*=\s*""([^""]+)""");
            includes.Add(include.Success ? include.Groups[1].Value : tag.Value);
        }
        return includes;
    }

    private static void AssertAllPresent(string text, string label, params string[] tokens)
    {
        var missing = tokens
            .Where(t => !text.Contains(t, StringComparison.Ordinal))
            .Select(t => label + " 缺 " + t)
            .ToList();
        Assert.True(missing.Count == 0,
            "Step 1 契约声明的载体缺失：\n  " + string.Join("\n  ", missing));
    }

    private static void AssertNonePresent(string text, string label, params string[] tokens)
    {
        var found = tokens
            .Where(t => text.Contains(t, StringComparison.OrdinalIgnoreCase))
            .Select(t => label + " 出现禁止项 " + t)
            .ToList();
        Assert.True(found.Count == 0,
            "Step 1 边界被越界引用 / 重新实现：\n  " + string.Join("\n  ", found));
    }

    /// <summary>
    /// [L0] WinUI 外壳**只**允许引用 PCMig.Core 一个项目。
    /// 断言的是"ProjectReference 条数 == 1"，而不是"恰好出现了 PCMig.Core 字样"——
    /// 否则多挂一个 PCMig.Gui 也会照样通过（这正是要拦的退火形态）。
    /// </summary>
    [Fact]
    public void WinUiProject_ReferencesCoreOnly()
    {
        var csproj = Read(FindRepoRoot(), "src", "PCMig.WinUI", "PCMig.WinUI.csproj");
        var refs = ExtractProjectReferences(csproj);

        Assert.True(refs.Count == 1,
            "PCMig.WinUI.csproj 必须恰好有 1 个 ProjectReference（只指向 PCMig.Core），实际 "
            + refs.Count + " 个：\n  " + string.Join("\n  ", refs));
        Assert.Contains("PCMig.Core.csproj", refs[0], StringComparison.Ordinal);
        Assert.DoesNotContain("PCMig.Gui", refs[0], StringComparison.OrdinalIgnoreCase);

        // 不得自引用（自引用会让 WinUI 项目被自己再编译一遍）
        Assert.DoesNotContain(refs, r => r.Contains("PCMig.WinUI", StringComparison.OrdinalIgnoreCase));

        // 兜底：整份 csproj 不得出现任何其它 PCMig 子项目（含 None/Link 等旁路写法）
        AssertNonePresent(csproj, "PCMig.WinUI.csproj", "PCMig.Gui", "PCMig.Cli");
    }

    /// <summary>
    /// [L0] Step 1 外壳必须是"免 MSIX 安装 + 自带 Windows App SDK 运行时"的形态：
    ///   · UseWinUI                  —— 否则根本不是 WinUI 3 项目；
    ///   · WindowsPackageType=None   —— unpackaged，免商店身份，可直接双击 exe；
    ///   · WindowsAppSDKSelfContained—— 终端用户无需预装 Windows App Runtime。
    /// 三项任一被改回默认，都会在生产机上表现为"启动即失败"，故锁定。
    /// </summary>
    [Fact]
    public void WinUiProject_IsUnpackagedSelfContainedWinUI()
    {
        var csproj = Read(FindRepoRoot(), "src", "PCMig.WinUI", "PCMig.WinUI.csproj");
        var flat = Flatten(csproj);

        AssertAllPresent(flat, "PCMig.WinUI.csproj（已去空白/小写）",
            "<usewinui>true</usewinui>",
            "<windowspackagetype>none</windowspackagetype>",
            "<windowsappsdkselfcontained>true</windowsappsdkselfcontained>");
    }

    /// <summary>
    /// [L0] ConnectionViewModel 只能**投影** Core 的能力：
    ///   · PreflightChecker            —— 连接/发现共享的唯一实现（Core 侧）；
    ///   · NetworkShare.ConnectForTransfer —— 手动共享探测的凭据会话。
    /// 它不得自己实现 SMB/扫描/规划/传输。
    /// </summary>
    [Fact]
    public void ConnectionViewModel_ProjectsCorePreflightAndNetworkShare()
    {
        var vm = Read(FindRepoRoot(), "src", "PCMig.WinUI", "Presentation", "ConnectionViewModel.cs");

        AssertAllPresent(vm, "ConnectionViewModel.cs",
            "PreflightChecker",
            "NetworkShare.ConnectForTransfer",
            "PCMig.Core.Preflight",
            "PCMig.Core.Native",
            "checker.RunAsync(",
            "benchmark: BenchmarkOnConnect");
    }

    /// <summary>
    /// [L0] 反向护栏：外壳里不得出现 Robocopy / TransferOrchestrator。
    /// 迁移引擎必须始终只有 Core 一套；WinUI 只是它的投影层。
    /// </summary>
    [Fact]
    public void ConnectionViewModel_DoesNotReimplementTransferEngine()
    {
        var vm = Read(FindRepoRoot(), "src", "PCMig.WinUI", "Presentation", "ConnectionViewModel.cs");

        AssertNonePresent(vm, "ConnectionViewModel.cs", "Robocopy", "TransferOrchestrator");
    }

    /// <summary>
    /// [L0] MainWindow.xaml 必须提供 Step 1 的完整可操作面：
    /// 三个输入/开关绑定（Host / Username / BenchmarkOnConnect）、
    /// 密码框（PasswordInput）、共享列表（Shares）、手动共享名（ManualShareName）
    /// 与三个点击处理器（Connect_Click / AddShare_Click / NextStep_Click）。
    /// 少任何一个，界面都会"看着还在、点了没反应"——XAML 缺处理器不会编译报错，
    /// 直到运行时 x:Bind/事件解析失败。
    /// </summary>
    [Fact]
    public void MainWindowXaml_ExposesStep1ControlsAndHandlers()
    {
        var xaml = Read(FindRepoRoot(), "src", "PCMig.WinUI", "MainWindow.xaml");

        AssertAllPresent(xaml, "MainWindow.xaml",
            "Host", "Username", "PasswordInput", "BenchmarkOnConnect", "Shares", "ManualShareName",
            "Connect_Click", "AddShare_Click", "NextStep_Click");

        // 更强的绑定形态：确认上面这些名字确实接在 ViewModel 上（不是残留在文案里）
        AssertAllPresent(xaml, "MainWindow.xaml 绑定",
            "ViewModel.Host", "ViewModel.Username", "ViewModel.BenchmarkOnConnect",
            "ViewModel.Shares", "ViewModel.ManualShareName");
    }

    /// <summary>
    /// [L0] Core 必须保持"纯 .NET 8 类库"：不得反向依赖任何 UI 技术栈
    /// （WPF 的 UseWPF / PresentationFramework / WindowsBase，
    ///   WinUI 的 UseWinUI / Microsoft.WindowsAppSDK / Microsoft.WinUI，
    ///   以及 net8.0-windows TFM 或 GUI 项目引用）。
    /// </summary>
    [Fact]
    public void CoreProject_RemainsFreeOfWpfAndWinUi()
    {
        var core = Read(FindRepoRoot(), "src", "PCMig.Core", "PCMig.Core.csproj");

        AssertNonePresent(core, "PCMig.Core.csproj",
            "UseWPF", "UseWinUI", "PresentationFramework", "WindowsBase",
            "Microsoft.WindowsAppSDK", "Microsoft.WinUI", "net8.0-windows",
            "PCMig.Gui", "PCMig.WinUI");

        // 正向锁定：Core 仍是 net8.0（无 -windows 后缀）
        AssertAllPresent(Flatten(core), "PCMig.Core.csproj（已去空白/小写）",
            "<targetframework>net8.0</targetframework>");
    }
}