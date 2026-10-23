// ============================================================================
//  ★ R2 · 缺陷 G 回归 ★ 安全提示通道必须有真实落点
// ============================================================================
//
//  ── 问题（R2 接手审计 · 缺陷 G，2026-10-17）─────────────────────────────────
//  `MigrationSessionViewModel` 仍在写 SecurityHint（如「该任务使用账号 X：本次未提供
//  密码，若 Windows 仍缓存着连接可正常继续，否则会以共享不可用失败」），StatusChannel
//  枚举也仍在，但 Step2 的「连接与安全提示」卡在 Issue 6（2026-10-14 真机验收）被
//  按用户指示移除 —— 于是这条通道**有值、有语义、没有任何 UI 落点**：
//  用户永远看不到它。
//
//  更糟的是，当时的测试不是"没覆盖"，而是**主动保护了丢弃行为**：
//      Batch6StatusRoutingTests.SR08 里
//          Assert.DoesNotContain("SecurityHintText", step2Cs);
//          Assert.DoesNotContain("." + nameof(MigrationSessionViewModel.SecurityHint), hintCs);
//  这两条断言把"提示被扔掉"钉成了正确行为。测试保护错误行为，比没有测试更危险。
//
//  ── 本文件钉死的契约 ─────────────────────────────────────────────────────────
//     G1 通道仍由 VM 唯一写入（写入者纪律不变，SR-10 不动）；
//     G2 通道必须有**可寻址的 UI 落点**，且落点读取的是同一条通道；
//     G3 落点必须在**四页共用的提示卡**里 —— 不能重新加回 Step2 右栏那张已判定溢出
//        的卡（否则真机 200% DPI 下右栏重新溢出 62.5 DIP）；
//     G4 测试不得再以"没有落点"为通过条件（这条本身要被断言）。
// ============================================================================

using System;
using System.IO;
using System.Text.RegularExpressions;
using PCMig.WinUI.Presentation;
using Xunit;

namespace PCMig.Core.Tests;

public class R2DefectGStatusChannelLandingTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadRepoFile(string relative)
        => File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// 剥掉注释，只留代码 —— 供"不得出现某类型名"这类断言使用。
    ///
    /// 【实现已统一到 <see cref="SourceStripper"/> —— 不要在本文件里另写一份】
    /// 这里曾经是每个测试文件各写一份的块注释正则，而块注释正则会**失控**：
    /// 文档注释里出现字面量 `/*` 极其常见（例如「禁止定义任何 *Orchestrator/*Runner 类型」
    /// 这句注释里就有 `/*Runner`），正则把它当成块注释开头后会一路吃到下一个 `*/`，
    /// 把中间的**全部可执行代码**静默删掉。实测事故：`MigrationSessionViewModel.cs`
    /// 原文 240,758 B 被吞到只剩 90,929 B（丢 62.2%），而本文件的禁词断言当时之所以"通过"，
    /// 正是因为被吞掉的那 107 KB 里含有它要找的名字 —— 断言看着在守，其实全瞎。
    /// 现在统一走状态机实现，并有 <see cref="R2SourceStripperTests"/> 直接盯着它。
    /// </summary>
    private static string StripComments(string text) => SourceStripper.StripComments(text);

    // ══════════════════════ G1 通道仍然由 VM 唯一写入 ══════════════════════

    [Fact]
    public void G01_ChannelIsStillWrittenByTheViewModelOnly()
    {
        Assert.Contains(StatusChannel.SecurityHint, Enum.GetValues<StatusChannel>());

        var vm = ReadRepoFile("src/PCMig.WinUI/Presentation/MigrationSessionViewModel.cs");
        Assert.Contains("public string SecurityHint", vm);
        Assert.Contains("SetSecurityHint(", vm);
        // 私有 setter：外部只能通过语义写入者改（避免任意控件直写通道）。
        Assert.Contains("private set => Set(ref _securityHint, value);", vm);
    }

    // ══════════════════════ G2 通道必须有真实 UI 落点 ══════════════════════

    [Fact]
    public void G02_ChannelHasAnAddressableUiLanding()
    {
        var cardXaml = ReadRepoFile("src/PCMig.WinUI/Views/ShellHintCard.xaml");
        var cardCs = StripComments(ReadRepoFile("src/PCMig.WinUI/Views/ShellHintCard.xaml.cs"));

        // 落点控件必须存在且可寻址（有 x:Name，才能被写、被查、被 UI 自动化看到）。
        Assert.Contains("x:Name=\"HintSecurityText\"", cardXaml);

        // 落点必须真的读这条通道 —— 只加控件不接线等于摆设。
        Assert.Contains(nameof(MigrationSessionViewModel.SecurityHint), cardCs);
        Assert.Contains("HintSecurityText", cardCs);
    }

    // ══════════════════════ G3 落点位置：共用提示卡，不是已判溢出的右栏卡 ══════════════════════

    [Fact]
    public void G03_LandingStaysInSharedHintCard_NotBackInStep2RightColumn()
    {
        var cardXaml = ReadRepoFile("src/PCMig.WinUI/Views/ShellHintCard.xaml");
        Assert.Contains("HintSecurityText", cardXaml);

        // Step2 右栏那张卡是 Issue 6 判定为右栏溢出 62.5 DIP 的直接原因，不得复活。
        var step2Xaml = ReadRepoFile("src/PCMig.WinUI/Views/Step2SelectDataPage.xaml");
        var step2Cs = StripComments(ReadRepoFile("src/PCMig.WinUI/Views/Step2SelectDataPage.xaml.cs"));
        Assert.DoesNotContain("x:Name=\"SecurityHintText\"", step2Xaml);
        Assert.DoesNotContain("SecurityHintText", step2Cs);
        Assert.DoesNotContain("连接与安全提示</TextBlock>", step2Xaml);

        // 提示卡必须在 Shell 层被四页共用（同一会话），不是某页私有一份。
        var shell = ReadRepoFile("src/PCMig.WinUI/MainWindow.xaml.cs");
        Assert.Contains("HintCard.Attach(Session);", shell);
    }

    // ══════════════════════ G4 测试不得再保护"丢弃" ══════════════════════

    [Fact]
    public void G04_NoTestStillAssertsTheHintIsDropped()
    {
        // ★ 这条用例是"元测试"：防的是把「提示被丢弃」写成通过条件的历史错误再次发生。
        //   历史错误长这样（Issue 6 之后被写进 Batch6StatusRoutingTests.SR08）：
        //       Assert.DoesNotContain("." + nameof(MigrationSessionViewModel.SecurityHint), hintCs);
        //   「提示卡不读安全通道」被当成正确行为 —— 于是缺陷 G 有了测试背书。
        //
        //   ⚠ 注意区分：`Assert.DoesNotContain("SecurityHintText", step2Cs)` **不在**禁止之列。
        //   那条断言守的是「Step2 不得引用已被删除的控件」（否则编译期/运行期必然出错），
        //   是正确的回归护栏，不能被本用例误伤。
        var routing = StripComments(ReadRepoFile("tests/PCMig.Core.Tests/Batch6StatusRoutingTests.cs"));

        Assert.DoesNotContain("Assert.DoesNotContain(\".\" + nameof(MigrationSessionViewModel.SecurityHint), hintCs)", routing);
        // 反向确认：提示卡现在必须读这条通道（否则等于又回到"有值无落点"）。
        Assert.Contains("Assert.Contains(nameof(MigrationSessionViewModel.SecurityHint), hintCs);", routing);
    }

    // ══════════════════════ G5 通道值真的能抵达提示卡 ══════════════════════

    [Fact]
    public void G05_HintCardWatchesTheSecurityChannel()
    {
        // 提示卡只处理 WatchedProperties 里的属性；若安全通道不在其中，
        // 控件写了也永不刷新（这正是"有落点却仍看不到"的经典形态）。
        var cardCs = StripComments(ReadRepoFile("src/PCMig.WinUI/Views/ShellHintCard.xaml.cs"));

        var watched = Regex.Match(cardCs, @"WatchedProperties\s*=\s*\[(?<body>.*?)\]", RegexOptions.Singleline);
        Assert.True(watched.Success, "找不到 WatchedProperties 数组 —— 提示卡的刷新通道清单已被改写，请重新核对本用例。");
        Assert.Contains(nameof(MigrationSessionViewModel.SecurityHint), watched.Groups["body"].Value);
    }
}
