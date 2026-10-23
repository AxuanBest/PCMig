// ============================================================================
//  ★ R2 · 缺陷 F 回归 ★ 预览隔离失败闸门：用户必须真的看到错误，然后才退出
// ============================================================================
//
//  ── 为什么必须存在（R2 接手审计 · 缺陷 F，2026-10-17）──────────────────────
//  `App.xaml.cs` 的预览隔离闸门（OnLaunched 最前面）在隔离不可用时必须"拦住 + 说清楚 + 退出"。
//  旧实现的写法是：
//      var dialog = new ContentDialog { ..., XamlRoot = MainWindowInstance?.Content?.XamlRoot };
//      _ = dialog.ShowAsync();          // ① 发射后不管（异步，没有任何等待）
//      finally { Environment.Exit(2); }  // ② 立刻执行，进程当场结束
//  两个各自都能单独致命的点叠在一起：
//    ① `MainWindowInstance` 此刻**必然为 null**（该闸门早于 `new MainWindow()`），
//       于是 `XamlRoot` 为 null，ContentDialog 根本建不起来 ⇒ 抛异常 ⇒ 被 catch 吞掉；
//    ② 即便对话框建得起来，`ShowAsync()` 抛出去就返回了，`Environment.Exit(2)` 紧接着执行，
//       用户看到的是"双击没反应 / 闪一下"，而不是"预览版数据隔离不可用"这句该看到的话。
//  ⇒ 真机后果：用户与支持人员都拿不到原因，只能反复重装，问题不可诊断。
//
//  ── 本文件钉死的四条纪律 ────────────────────────────────────────────────────
//  R2F-1  闸门位置：必须早于 MainWindow 创建、早于诊断启动、早于任何 Core 数据根解析；
//  R2F-2  呈现方式：必须用**不依赖 XAML 树**的同步可控手段（Win32 MessageBoxW），
//         不得再出现 `ShowAsync()` 发射后不管，也不得依赖 `XamlRoot`；
//  R2F-3  顺序：**先等用户确认（或超时）再 Exit** —— Exit 只能出现在等待之后；
//  R2F-4  超时兜底也必须留证据（写 preview-isolation-blocked.log），不得静默退出。
// ============================================================================

using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PCMig.Core.Tests;

public class R2DefectFIsolationDialogTests
{
    private const string AppRelative = "src/PCMig.WinUI/App.xaml.cs";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
        throw new InvalidOperationException("找不到含 PCMig.sln 的仓库根：" + AppContext.BaseDirectory);
    }

    private static string ReadRepoFile(string relative)
        => File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>去掉注释行与行尾注释（注释里提到旧写法不算违规）。</summary>
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

    /// <summary>取某个方法的方法体（按花括号配平；跳过调用点）。</summary>
    private static string MethodBody(string text, string methodName)
    {
        foreach (Match sig in Regex.Matches(text, @"\b" + methodName + @"\s*\("))
        {
            var open = text.IndexOf('{', sig.Index + sig.Length);
            if (open < 0) continue;
            var between = text.Substring(sig.Index + sig.Length, open - (sig.Index + sig.Length));
            if (between.Contains(';')) continue;

            var depth = 0;
            for (var j = open; j < text.Length; j++)
            {
                if (text[j] == '{') depth++;
                else if (text[j] == '}')
                {
                    depth--;
                    if (depth == 0) return text.Substring(open, j - open + 1);
                }
            }
            Assert.Fail($"App.xaml.cs 的 {methodName} 花括号不配平。");
        }
        Assert.Fail($"App.xaml.cs 里找不到方法体 {methodName}（只找到调用点）。");
        return string.Empty;
    }

    // ══════════════════════════ R2F-1 ══════════════════════════

    /// <summary>闸门必须排在"建窗口 / 起诊断 / 任何 Core 数据根解析"之前（Fail Closed 且不写错地方）。</summary>
    [Fact]
    public void R2F01_IsolationGate_RunsBeforeAnyCoreOrWindowInitialization()
    {
        var code = StripComments(ReadRepoFile(AppRelative));

        var gate = code.IndexOf("PreviewIsolation.Failure is { } isolationFailure", StringComparison.Ordinal);
        var dialog = code.IndexOf("ShowIsolationFailureAndExit(", StringComparison.Ordinal);
        var diagnostics = code.IndexOf("DiagnosticBootstrap.Start(", StringComparison.Ordinal);
        var window = code.IndexOf("new MainWindow()", StringComparison.Ordinal);

        Assert.True(gate > 0, "App.xaml.cs 找不到预览隔离闸门（PreviewIsolation.Failure is { } isolationFailure）。");
        Assert.True(dialog > gate, "ShowIsolationFailureAndExit 必须在闸门判据之后被调用。");
        Assert.True(diagnostics > gate, "隔离闸门必须早于 DiagnosticBootstrap.Start —— 否则已经开始写数据根。");
        Assert.True(window > gate, "隔离闸门必须早于 new MainWindow()。");
        Assert.True(dialog < diagnostics, "必须先处理隔离失败（含向用户呈现），不得先起诊断。");
        Assert.True(dialog < window, "必须先处理隔离失败（含向用户呈现），不得先建窗口。");
    }

    // ══════════════════════════ R2F-2 ══════════════════════════

    /// <summary>不得再用发射后不管的 ContentDialog，也不得依赖此刻必然为 null 的 XamlRoot。</summary>
    [Fact]
    public void R2F02_MustNotUseFireAndForgetContentDialog_OrXamlRoot()
    {
        var code = StripComments(ReadRepoFile(AppRelative));

        Assert.DoesNotContain("ShowAsync()", code, StringComparison.Ordinal);
        Assert.DoesNotContain("XamlRoot", code, StringComparison.Ordinal);

        var body = StripComments(MethodBody(code, "ShowIsolationFailureAndExit"));
        Assert.DoesNotContain("ShowAsync", body, StringComparison.Ordinal);
        Assert.DoesNotContain("XamlRoot", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ContentDialog", body, StringComparison.Ordinal);

        // 呈现手段必须是与 XAML 树无关的 Win32 同步模态框。
        Assert.Contains("MessageBoxW", body, StringComparison.Ordinal);
        Assert.Contains("user32.dll", code, StringComparison.Ordinal);
    }

    // ══════════════════════════ R2F-3 ══════════════════════════

    /// <summary>必须先等用户确认/超时，再 Environment.Exit —— Exit 出现在等待之后，且只出现一次。</summary>
    [Fact]
    public void R2F03_ExitOnlyAfterWaitingForUser()
    {
        var body = StripComments(MethodBody(StripComments(ReadRepoFile(AppRelative)), "ShowIsolationFailureAndExit"));

        var wait = body.IndexOf(".Wait(", StringComparison.Ordinal);
        var exit = body.IndexOf("Environment.Exit(", StringComparison.Ordinal);

        Assert.True(wait > 0, "ShowIsolationFailureAndExit 必须同步等待用户确认（例如 shown.Wait(超时)），否则用户看不到任何提示。");
        Assert.True(exit > wait, "Environment.Exit 必须排在等待之后 —— 排在前面就等于对话框永远来不及显示（缺陷 F 的原始形态）。");
        Assert.Single(Regex.Matches(body, @"Environment\.Exit\("));

        // 等待必须有上界（无人值守时不得永久挂住），且上界是一个明确的常数值。
        Assert.Matches(new Regex(@"Wait\(\s*[A-Za-z_]\w*\s*\)"), body);
        Assert.Matches(new Regex(@"(const\s+int|const\s+double|TimeSpan)\s+\w*Wait\w*"), body);
    }

    // ══════════════════════════ R2F-4 ══════════════════════════

    /// <summary>超时兜底也必须留证据（写 preview-isolation-blocked.log），不得静默退出。</summary>
    [Fact]
    public void R2F04_TimeoutPath_StillLeavesEvidence()
    {
        var app = StripComments(ReadRepoFile(AppRelative));
        var body = StripComments(MethodBody(app, "ShowIsolationFailureAndExit"));

        Assert.Contains("preview-isolation-blocked.log", app, StringComparison.Ordinal);
        Assert.Contains("preview-isolation-blocked.log", body, StringComparison.Ordinal);
        Assert.Contains("AppendAllText", body, StringComparison.Ordinal);

        // 拦截日志本身要写在闸门处（哪怕对话框都起不来也有证据）。
        var gateIndex = app.IndexOf("PreviewIsolation.Failure is { } isolationFailure", StringComparison.Ordinal);
        var blockLogIndex = app.IndexOf("preview-isolation-blocked.log", StringComparison.Ordinal);
        Assert.True(blockLogIndex > gateIndex, "闸门处必须先把失败原因落盘，再尝试向用户呈现。");
    }
}
