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
    /// [L0] WinUI 外壳只允许引用**获准的**项目：PCMig.Core + 诊断（运行时/契约层）。
    ///
    /// ★ D3 有意修订（已在 docs\诊断系统实施-阶段证据.md 登记）★
    /// 原测试断言"ProjectReference 条数 == 1（只指向 PCMig.Core）"，其**真实意图**是
    /// "外壳不得长出第二套迁移引擎 / 不得反向依赖 WPF 外壳"。诊断中心落地后外壳必须能装配
    /// 诊断运行时，因此改为**显式允许清单**；上述意图由保留的禁止项（PCMig.Gui / PCMig.Cli /
    /// 自引用）继续保证，并另有 ConnectionViewModel 的引擎反向护栏单独覆盖。
    /// </summary>
    [Fact]
    public void WinUiProject_ReferencesOnlySanctionedProjects()
    {
        var csproj = Read(FindRepoRoot(), "src", "PCMig.WinUI", "PCMig.WinUI.csproj");
        var refs = ExtractProjectReferences(csproj);

        string[] allowed = { "PCMig.Core.csproj", "PCMig.Diagnostics.csproj", "PCMig.Diagnostics.Abstractions.csproj" };
        var unexpected = refs
            .Where(r => !allowed.Any(a => r.Contains(a, StringComparison.Ordinal)))
            .ToArray();
        Assert.True(unexpected.Length == 0,
            "PCMig.WinUI.csproj 出现未获准的 ProjectReference：\n  " + string.Join("\n  ", unexpected));

        Assert.Contains(refs, r => r.Contains("PCMig.Core.csproj", StringComparison.Ordinal));
        Assert.Contains(refs, r => r.Contains("PCMig.Diagnostics.csproj", StringComparison.Ordinal));

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
    /// 与页面内的点击处理器（Connect_Click / AddShare_Click / NextStep_Click）。
    /// 少任何一个，界面都会"看着还在、点了没反应"——XAML 缺处理器不会编译报错，
    /// 直到运行时 x:Bind/事件解析失败。
    ///
    /// A29（20260928，用户人工标注项）：用户明确要求删除四个页面右下角重复出现的
    /// 「上一步 / 下一步」按钮组，原话是「这两个按钮在四个功能板块中都很多余 可以去除，
    /// 这样旁边的空间才可以更高效的利用」。MainWindow.xaml 里的该按钮组（连同
    /// PrevStepButton / NextStepButton 与两个 Click 处理器）已按指令删除，
    /// 因此 Shell 侧的契约断言**不再要求** PrevStep_Click / NextStep_Click。
    /// 导航能力没有减少：左侧栏四张卡与 Ctrl+1..4 / Ctrl+Tab 键盘路径全部保留。
    /// A51（20260928，用户新决策）：Shell 层以统一 Navigation Footer 的形式**恢复了**
    /// 「上一步 / 下一步」区（MainWindow.xaml 的 WorkspaceShell 新增第 5 行 Auto + x:Bind Nav.IsXxxCurrent），
    /// Step 1 页面内那个同名按钮随即成为重复项，已删除（见下方页面侧 AssertNonePresent）。
    /// </summary>
    [Fact]
    public void MainWindowXaml_ExposesStep1ControlsAndHandlers()
    {
        // 视图组件化之后（四页面 Shell 阶段），Step 1 的可操作面**从 MainWindow.xaml 搬到了
        // Views\Step1ConnectPage.xaml**；MainWindow 只保留 Shell 装配与导航。
        // 因此断言拆成两半：Shell 侧看"确实挂上了 Step 1 页"，页面侧看"可操作面完整"。
        var shell = Read(FindRepoRoot(), "src", "PCMig.WinUI", "MainWindow.xaml");
        AssertAllPresent(shell, "MainWindow.xaml（Shell）",
            "StepNavigationControl", "ShellHintCard", "Step1ConnectPage", "x:Bind Nav.");
        // A29：Shell 侧不再有上一步/下一步按钮，改为断言它们确实**已不存在**
        // （防止有人把冗余按钮又加回来）。
        AssertNonePresent(shell, "MainWindow.xaml（Shell）",
            "PrevStepButton", "NextStepButton", "PrevStep_Click");

        var page = Read(FindRepoRoot(), "src", "PCMig.WinUI", "Views", "Step1ConnectPage.xaml");
        AssertAllPresent(page, "Views\\Step1ConnectPage.xaml",
            "Host", "Username", "PasswordInput", "BenchmarkOnConnect", "Shares", "ManualShareName",
            "Connect_Click", "AddShare_Click");
        // A51（20260928，用户截图发现 bug）：页内自带的「下一步：选择要迁移的内容 →」
        // （Click="NextStep_Click"）与 Shell 层统一 Navigation Footer 渲染出的同名按钮上下叠着、
        // 重复出现两遍。按用户指令 + 设计口径「导航 Footer 只在 Shell 一份，四个页面都不再各自放按钮」，
        // 该按钮与其唯一处理器 NextStep_Click（只调 NotifyNextStepUnavailable，不导航）已一并删除。
        // 这里改为断言它**确实已不存在**，防止冗余按钮再被加回来
        //（与上面 Shell 侧 AssertNonePresent 同一口径）。
        AssertNonePresent(page, "Views\\Step1ConnectPage.xaml",
            "NextStep_Click", "下一步：选择要迁移的内容");

        // 更强的绑定形态：确认这些名字确实接在 ViewModel 上（不是残留在文案里）。
        // 页面用**经典 {Binding}** 读 Shell 注入的 DataContext（x:Bind 的嵌套路径在
        // UserControl 上不会随后置注入重算，实测渲染为空，故不用）。
        // 断言 token 刻意**不带结尾引号**：文件里这些属性名后面紧跟的是逗号或大括号
        // （例如 IsOn="{Binding BenchmarkOnConnect, Mode=TwoWay}"）——带引号会永远断言失败。
        // 这个坑本轮实际踩过：PowerShell 独立核对说"字符串存在"，测试却报缺。
        AssertAllPresent(page, "Views\\Step1ConnectPage.xaml 绑定",
            "{Binding Host", "{Binding Username", "{Binding BenchmarkOnConnect",
            "{Binding Shares}", "{Binding ManualShareName");
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