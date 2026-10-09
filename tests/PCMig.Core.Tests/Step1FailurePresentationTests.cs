using System;
using System.IO;
using System.Linq;
using PCMig.WinUI.Presentation;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ 2026-10-08（真机问题 1）★ Step 1「连接失败提示文本越界」的修复契约。
///
/// 缺陷回顾：状态行原来是一个**裸 TextBlock** 坐在 `FormGrid` 第 4 行（`Height="Auto"`）。
/// `TextWrapping="Wrap"` 只保证换行、**不保证不撑高** —— 一行 Core/Win32/SMB 长异常
/// （IPC$ / 1219 / 67 / 1327 那一整段）会把 Auto 行拉到几百 DIP，FormCard 随之上长，
/// 压住并穿过下方 SharesCard（真机实测整个 Step 1 布局被破坏）。
///
/// 修复后的两条纪律（本文件逐条锁死）：
///   ① 页面**只呈现用户可读摘要 + 一句可执行建议**；完整原文只进日志；
///   ② 整块包在**有明确 MaxHeight 的 ScrollViewer** 里 ⇒ 超出即在容器内滚动，绝不越界、绝不覆盖其它控件。
/// </summary>
public class Step1FailurePresentationTests
{
    // ── 行为级：长失败原文被压成"摘要 + 建议" ───────────────────────────────────

    [Fact]
    public void SummarizeFailure_KeepsOnlyTheFirstLine()
    {
        var ex = new IOException("第一行：系统发生 1326 错误——用户名或密码不正确。\r\n第二行：不该出现在页面上。\r\n第三行：同样不该出现。");

        var (summary, _) = ConnectionViewModel.SummarizeFailure("无法连接", @"\\192.168.1.9\E", ex);

        Assert.Contains("无法连接", summary);
        Assert.Contains("第一行", summary);
        Assert.DoesNotContain("第二行", summary);
        Assert.DoesNotContain("第三行", summary);
    }

    [Fact]
    public void SummarizeFailure_TruncatesVeryLongMessages()
    {
        var ex = new IOException(new string('长', 600));

        var (summary, _) = ConnectionViewModel.SummarizeFailure("无法连接", @"\\H\E", ex);

        Assert.Contains("…", summary);
        // 摘要 = headline + target + 换行 + ≤180 字符正文；600 字符原文绝不允许整段进页面。
        Assert.True(summary.Length <= 181 + "无法连接 \\\\H\\E".Length + 2,
            $"摘要长度 {summary.Length} 超出上限——长原文又整段灌进 Step 1 了");
        Assert.True(summary.Length < ex.Message.Length);
    }

    [Fact]
    public void SummarizeFailure_EmptyMessage_StillGivesHeadlineAndAdvice()
    {
        var (summary, advice) = ConnectionViewModel.SummarizeFailure("无法访问", @"\\H\E", new IOException(string.Empty));

        Assert.Contains("无法访问", summary);
        Assert.Contains(@"\\H\E", summary);
        Assert.False(string.IsNullOrWhiteSpace(advice));
    }

    [Theory]
    [InlineData("系统发生 1326 错误", "用户名与密码")]
    [InlineData("登录失败: 未知的用户名或错误密码。", "用户名与密码")]
    [InlineData("不允许一个用户使用一个以上用户名与一个服务器或共享资源的多重连接。", "断开")]
    [InlineData("系统发生 53 错误。找不到网络路径。", "文件共享")]
    [InlineData("对路径的访问被拒绝。Access is denied.", "权限")]
    [InlineData("操作超时", "超时")]
    [InlineData("完全不认识的错误码 0xDEADBEEF", "共享名、账户密码和共享权限")]
    public void SummarizeFailure_ClassifiesByKeyword_AndNeverGuesses(string message, string expectedFragment)
    {
        var (_, advice) = ConnectionViewModel.SummarizeFailure("无法连接", @"\\H\E", new IOException(message));
        Assert.Contains(expectedFragment, advice);
    }

    // ── 契约级：状态行必须有界（不会撑破 Step 1 布局） ─────────────────────────

    [Fact]
    public void Step1_StatusLineLivesInABoundedScrollHost()
    {
        var xaml = ReadRepoFile("src", "PCMig.WinUI", "Views", "Step1ConnectPage.xaml");

        // 有明确高度上限的滚动宿主 —— 文本永远在容器内。
        Assert.Contains("x:Name=\"StatusScroll\"", xaml);
        Assert.Contains("MaxHeight=\"88\"", xaml);
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", xaml);
        Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", xaml);

        // 摘要 + 建议分两层，且都绑到 VM 的对应通道。
        Assert.Contains("x:Name=\"StatusAdviceText\"", xaml);
        Assert.Contains("{Binding InlineAdvice, Mode=OneWay}", xaml);

        // 既有契约不丢：状态文本仍是 Step1.StatusText，仍换行（换行 + 上限 = 不越界）。
        Assert.Contains("AutomationProperties.AutomationId=\"Step1.StatusText\"", xaml);

        // 顺序不变量：宿主必须在文本之前 —— 谁把 ScrollViewer 去掉、把裸 TextBlock 放回 Auto 行，这里立刻红。
        var host = xaml.IndexOf("x:Name=\"StatusScroll\"", StringComparison.Ordinal);
        var line = xaml.IndexOf("x:Name=\"StatusLineText\"", StringComparison.Ordinal);
        Assert.True(host >= 0 && line > host, "状态文本不再位于有界滚动宿主内部（越界缺陷会复发）");
    }

    [Fact]
    public void Step1_PageNeverBindsRawExceptionTextIntoTheForm()
    {
        var vm = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "ConnectionViewModel.cs");

        // 完整异常原文必须仍进日志（页面只拿摘要）。
        Assert.Contains("_log.Error(ex", vm);
        // 失败路径必须走 SummarizeFailure（否则长原文又直灌 InlineNote）。
        Assert.Contains("SummarizeFailure(\"无法连接\", host, ex)", vm);
        Assert.Contains("SummarizeFailure(\"无法访问\", unc, probeError)", vm);
        Assert.DoesNotContain("SetInlineNote($\"连接失败：{ex.Message}\")", vm);
    }

    // ── 行为级（2026-10-08 真机第二次复验）：预检未通过、但**没有抛异常**的那条路径 ──────
    //
    // 真机实测：连接一个不存在的电脑时，PreflightChecker 不抛异常，而是返回一份带 Error 检查项的
    // PreflightReport；那一步的 Error.Detail 原文（用户机器上实测 400+ 字，含 ①② 区指引）被直接
    // 当作状态句写进 FlowStatus ⇒ 涌进 178 DIP 宽的 Shell 提示卡，把卡片撑到上限并让 Step 1 的
    // 专用错误区（StatusScroll）空置。修复＝把这条路径也拆成「摘要 + 建议」并只让短结论进提示卡。

    /// <summary>真机抓到的原样 Detail（来自 C:\ProgramData\PCMig\Logs\app-20261008.log，只把主机名泛化）。</summary>
    private const string RealMachineDetail =
        "无法连接 \\\\NO-SUCH-PC-PCMIG-TEST\\IPC$（错误 64）：指定的网络名不再可用。" +
        "目前**无法确认这套账号密码是否可用**：IPC$ 会话没有建立起来。如果该账号需要域控校验，" +
        "域控不可达时同样会失败，请先确认域控在线；也可以先在②区「共享名」手动输入一个你确定存在的共享（如 d）" +
        "再点「＋添加共享」——那一步会真的建立数据共享会话，成功即证明凭据可用；" +
        "或直接在①粘贴完整共享路径（如 \\\\IP\\D 或 \\\\IP\\D$）绕过共享枚举。";

    [Fact]
    public void SummarizeReportFailure_SplitsRealMachineDetailIntoSentenceAndAdvice()
    {
        var (summary, advice) = ConnectionViewModel.SummarizeReportFailure("NO-SUCH-PC-PCMIG-TEST", RealMachineDetail);

        // 摘要 = 第一句（真机 400+ 字 Detail 的第一句只有 ~40 字），绝不整段上屏。
        Assert.EndsWith("。", summary);
        Assert.Contains("指定的网络名不再可用。", summary);
        Assert.DoesNotContain("绕过共享枚举", summary);
        Assert.True(summary.Length <= 170, $"摘要长度 {summary.Length} 超出上限——长 Detail 又整段进提示卡了");
        Assert.True(summary.Length < RealMachineDetail.Length / 4, "摘要没有比 Detail 短一个数量级");

        // 建议 = 其余原文（含 ①② 区指引），完整保留、不截断。
        Assert.Contains("绕过共享枚举", advice);
        Assert.Contains("无法确认这套账号密码是否可用", advice);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SummarizeReportFailure_NoDetail_StillGivesHeadlineAndGenericAdvice(string? detail)
    {
        var (summary, advice) = ConnectionViewModel.SummarizeReportFailure("H", detail);

        Assert.Contains("H", summary);
        Assert.False(string.IsNullOrWhiteSpace(advice));
    }

    [Fact]
    public void SummarizeReportFailure_SingleLongSentence_Truncates()
    {
        var (summary, advice) = ConnectionViewModel.SummarizeReportFailure("H", new string('长', 400));

        Assert.Contains("…", summary);
        Assert.True(summary.Length <= 161, $"单句超长时摘要仍有 {summary.Length} 字符");
        Assert.False(string.IsNullOrWhiteSpace(advice));
    }

    [Fact]
    public void SummarizeReportFailure_ShortDetail_FallsBackToKeywordAdvice()
    {
        // Detail 只有一句、没有后续建议 ⇒ 建议由关键词归类补上（绝不返回空建议、绝不猜）。
        var (summary, advice) = ConnectionViewModel.SummarizeReportFailure("H", "系统发生 1326 错误：登录失败。");

        Assert.Contains("1326", summary);
        Assert.Contains("用户名与密码", advice);
    }

    [Fact]
    public void ConnectAsync_RoutesReportFailureToTheHintCardChannels_NotTheForm()
    {
        var vm = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "ConnectionViewModel.cs");

        // 分流判定必须与 BuildConnectStatus 的三分支**同一口径**
        // （真机实测：OverallPass 为 true 但 CredentialVerified 为 false 时也会落到兜底分支）。
        Assert.Contains("var credentialProven = report.OverallPass && report.CredentialVerified;", vm);
        Assert.Contains("var failedToConfirm = !report.CredentialReused && Shares.Count == 0 && !credentialProven;", vm);

        // ★ 2026-10-08 真机复验·返修 R1（口径反转）★
        //   完整用户可读说明进**左侧提示卡三通道**：发生了什么 / 错误摘要 / 下一步怎么做。
        Assert.Contains("SummarizeReportFailure(host, detail)", vm);
        Assert.Contains("SetFlowFailure($\"未能连接 {host}。\", failSummary, failAdvice);", vm);
        Assert.Contains("private void SetFlowFailure(string operational, string errorSummary, string userHint)", vm);
        Assert.Contains("FlowErrorSummary = errorSummary;", vm);
        Assert.Contains("FlowUserHint = userHint;", vm);

        // 失败文本**不再**写进 Step 1 表单（用户明确禁止表单与提示卡两处重复）。
        Assert.DoesNotContain("SetInlineNote(failSummary", vm);
        Assert.DoesNotContain("SetInlineNote(shareFailSummary", vm);

        // 完整 Detail 原文只进日志。
        Assert.Contains("detail={Detail}", vm);
    }

    [Fact]
    public void HintCardChannels_AreDrivenByConnectionSemanticChannels()
    {
        // R1 能成立的前提：提示卡三个通道都由连接层的语义通道驱动 ——
        //   OperationalStatus ← ConnectionViewModel.FlowStatus（发生了什么）；
        //   UserHint          ← FlowUserHint（用户下一步怎么做）；
        //   ErrorSummary      ← FlowErrorSummary（必要的错误摘要）。
        // 表单通道（SetInlineNote → InlineNote / InlineAdvice）绝不污染提示卡。
        var session = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MigrationSessionViewModel.cs");
        Assert.Contains("nameof(ConnectionViewModel.FlowStatus)", session);
        Assert.Contains("nameof(ConnectionViewModel.FlowUserHint)", session);
        Assert.Contains("nameof(ConnectionViewModel.FlowErrorSummary)", session);
        Assert.Contains("UserHint = _connection?.FlowUserHint ?? string.Empty;", session);
        Assert.Contains("ErrorSummary = _connection?.FlowErrorSummary ?? string.Empty;", session);

        var card = ReadRepoFile("src", "PCMig.WinUI", "Views", "ShellHintCard.xaml.cs");
        Assert.Contains("nameof(MigrationSessionViewModel.OperationalStatus)", card);
        Assert.Contains("HintOperationalText", card);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

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