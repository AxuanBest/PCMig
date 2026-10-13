using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ P0-06(4) ★ 崩溃路径的**回归闸门**。
///
/// 现场症状："点击停止时 UI 卡住或无响应；偶发程序崩溃"，但 ZIP 里没有 WER /
/// Application Hang 1002 / 转储 ⇒ 不能凭用户描述指认某个函数。因此在改行为之前，
/// 先把"会直接结束进程的路径"逐条钉死：
///
///   1) UI 层所有 <c>async void</c> 事件处理器都必须自带 catch（漏出去 = 无调用者可接住 = 崩溃）；
///      这条用**枚举 + 花括号配平**实现，新增处理器漏兜底会被直接判失败（防未来回归）。
///   2) 后台异常两条出口必须挂着：TaskScheduler.UnobservedTaskException、AppDomain.UnhandledException。
///   3) 兜底出口不得只"吞"不"留证据"：必须同时写日志与诊断事实，并给用户一个明确结果。
/// </summary>
public class P0CrashPathGuardTests
{
    [Fact]
    public void C1_UI层所有async_void处理器都必须自带catch()
    {
        var root = RepoRoot();
        var dir = Path.Combine(root, "src", "PCMig.WinUI");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(text, @"async\s+void\s+([A-Za-z_]\w*)\s*\("))
            {
                var name = m.Groups[1].Value;
                var open = text.IndexOf('{', m.Index + m.Length);
                if (open < 0) continue;
                var depth = 0;
                var j = open;
                while (j < text.Length)
                {
                    if (text[j] == '{') depth++;
                    else if (text[j] == '}')
                    {
                        depth--;
                        if (depth == 0) break;
                    }
                    j++;
                }
                if (j >= text.Length) continue;
                var body = text.Substring(open, j - open + 1);
                if (!System.Text.RegularExpressions.Regex.IsMatch(body, @"catch\s*\("))
                    offenders.Add($"{Path.GetFileName(file)}::{name}");
            }
        }

        Assert.True(offenders.Count == 0,
            "以下 async void 处理器没有 catch —— 它们抛出的异常没有调用者能接住，会直接结束进程：" +
            string.Join("、", offenders));
    }

    [Fact]
    public void C2_兜底出口必须记录而不是静默吞掉_且给用户明确结果()
    {
        var guard = ReadRepoFile(@"src\PCMig.WinUI\Diagnostics\UiFaultGuard.cs");

        // 记录：日志 + 诊断事实各一份（"错误与失败证据不得丢"）
        Assert.Contains("Log.Error(ex, \"界面操作异常（{Where}）\", where)", guard, StringComparison.Ordinal);
        Assert.Contains("DiagnosticBootstrap.RecordUnhandledException(ex, where)", guard, StringComparison.Ordinal);
        // 呈现：调用方拿到一个面向用户的结果句，而不是"界面毫无反应"
        Assert.Contains("surface(", guard, StringComparison.Ordinal);
        // 自身绝不抛
        Assert.Contains("catch { /* 日志失败不得外溢 */ }", guard, StringComparison.Ordinal);
        Assert.Contains("catch { /* 呈现失败也不得外溢 */ }", guard, StringComparison.Ordinal);
    }

    [Fact]
    public void C3_后台异常两条出口必须挂着()
    {
        var app = ReadRepoFile(@"src\PCMig.WinUI\App.xaml.cs");

        Assert.Contains("TaskScheduler.UnobservedTaskException +=", app, StringComparison.Ordinal);
        Assert.Contains("AppDomain.CurrentDomain.UnhandledException +=", app, StringComparison.Ordinal);
        // 两条都必须留证据（日志 + 诊断事实），不是挂个空壳
        Assert.Contains("Log(\"UnobservedTaskException\", e.Exception)", app, StringComparison.Ordinal);
        Assert.Contains("DiagnosticBootstrap.RecordUnhandledException(e.Exception, \"UnobservedTaskException\")", app, StringComparison.Ordinal);
        Assert.Contains("Log(\"AppDomainUnhandled\", e.ExceptionObject as Exception,", app, StringComparison.Ordinal);
        Assert.Contains("RecordUnhandledException(ex, \"AppDomainUnhandled\")", app, StringComparison.Ordinal);
        // 既有策略不变：XAML UnhandledException 仍然"记录但不吞"（不设 Handled）
        Assert.DoesNotContain("e.Handled = true", app, StringComparison.Ordinal);
    }

    [Fact]
    public void C4_底栏四个按钮必须各自兜底_且停止时间线不得留在未收口()
    {
        var shell = ReadRepoFile(@"src\PCMig.WinUI\MainWindow.xaml.cs");

        foreach (var handler in new[]
                 {
                     "MainWindow.FooterStart_Click", "MainWindow.FooterPause_Click",
                     "MainWindow.FooterStop_Click", "MainWindow.FooterResume_Click",
                 })
            Assert.Contains($"UiFaultGuard.Report(\"{handler}\", ex);", shell, StringComparison.Ordinal);

        // 停止：异常收场必须把时间线如实收口（不能停"未收口"上冒充"还在收尾"）
        Assert.Contains("Session.LastStopTimeline?.Complete(\"stop-threw\", ex.GetType().Name);", shell, StringComparison.Ordinal);
        // 兜底收口自身不得抛，也不得改业务状态
        var helper = MethodWindow(shell, "private void SafeCompleteFooterAction(string phase, Exception ex)", 600);
        Assert.Contains("Session.AppendLog(\"ERROR\"", helper, StringComparison.Ordinal);
        Assert.Contains("PushFooter();", helper, StringComparison.Ordinal);
        Assert.Contains("catch { /* 留痕失败不得外溢 */ }", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void C5_连接处理器不得再把漏网异常rethrow进崩溃路径()
    {
        var page = ReadRepoFile(@"src\PCMig.WinUI\Views\Step1ConnectPage.xaml.cs");
        var window = MethodWindow(page, "private async void Connect_Click(object sender, RoutedEventArgs e)", 3000);

        // 旧写法 catch 里 `throw;` ⇒ async void ⇒ 进程结束；现在必须如实收口为 Failed。
        // （按**整行**判定，避免把解释这个坑的注释文字本身当成代码命中。）
        var codeLines = window.Split('\n').Select(l => l.Trim()).ToArray();
        Assert.DoesNotContain("throw;", codeLines);
        Assert.Contains("UiFaultGuard.Report(\"Step1ConnectPage.Connect_Click\", ex);", window, StringComparison.Ordinal);
        Assert.Contains("trace.Complete(DiagnosticOutcome.Failed, \"connect-threw\");", window, StringComparison.Ordinal);
    }

    [Fact]
    public void C6_导出诊断包不得只有finally没有catch()
    {
        var panel = ReadRepoFile(@"src\PCMig.WinUI\Views\DiagnosticCenterPanel.xaml.cs");
        var window = MethodWindow(panel, "private async void Export_Click(object sender, RoutedEventArgs e)", 4000);

        Assert.Contains("catch (Exception ex)", window, StringComparison.Ordinal);
        Assert.Contains("finally", window, StringComparison.Ordinal);
        // catch 必须在 finally 之前（先兜住异常，再统一清状态）
        Assert.True(window.IndexOf("catch (Exception ex)", StringComparison.Ordinal)
                    < window.IndexOf("finally", StringComparison.Ordinal),
            "catch 必须在 finally 之前，否则异常直接飞出 async void");
        Assert.Contains("UiFaultGuard.Report(\"DiagnosticCenterPanel.Export_Click\", ex);", window, StringComparison.Ordinal);
    }

    [Fact]
    public void C7_对话框弹窗失败不得崩进程()
    {
        var page = ReadRepoFile(@"src\PCMig.WinUI\Views\Step4ResultPage.xaml.cs");
        var window = MethodWindow(page, "private async void OnFailDetailClick(object sender, RoutedEventArgs e)", 2000);

        // WinUI ContentDialog 同一时刻只允许一个；ShowAsync 抛出必须被兜住
        Assert.Contains("await dialog.ShowAsync();", window, StringComparison.Ordinal);
        Assert.Contains("UiFaultGuard.Report(\"Step4ResultPage.OnFailDetailClick\", ex,", window, StringComparison.Ordinal);
    }

    private static string MethodWindow(string text, string signature, int length)
    {
        var at = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"源码里找不到 {signature}");
        return text.Substring(at, Math.Min(length, text.Length - at));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PCMig.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadRepoFile(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative));
}