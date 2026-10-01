using System;
using System.IO;
using System.Text;
using Microsoft.UI.Xaml;
using PCMig.WinUI.Diagnostics;

namespace PCMig.WinUI;

public partial class App : Application
{
    public static Window? MainWindowInstance { get; private set; }

    private static readonly string LogPath =
        Path.Combine(AppContext.BaseDirectory, "poc-startup.log");

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            // 既有策略不变：**记录但不吞**（不设置 Handled），也不改变任何启动/退出语义。
            Log("UnhandledException", e.Exception);
            // 诊断侧只补一条最小事实（类型/错误码/帧摘要），不保存异常原文。
            DiagnosticBootstrap.RecordUnhandledException(e.Exception, "UnhandledException");
        };

        // ── 仅诊断用：first-chance 异常记录（PCMIG_DIAG=1 时才挂）──────────────────
        // 背景：收尾阶段出现 COMException 0x80040111（Windows.ApplicationModel.LimitedAccessFeatures），
        // 但 UnhandledException 拿到的 ToString() **不含托管调用栈**，无法定位抛出点。
        // first-chance 钩子在异常首次抛出时就能拿到完整 StackTrace，是定位这类问题的唯一低成本手段。
        // 正常运行（未设 PCMIG_DIAG）时完全不挂，零开销、零行为变化。
        if (Environment.GetEnvironmentVariable("PCMIG_DIAG") == "1")
        {
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
                Log("FirstChance:" + e.Exception.GetType().Name, e.Exception);
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // ★ 诊断尽早启动 ★ 早于任何 VM/Core 观察点被调用；失败一律降级（绝不阻止应用启动）。
        DiagnosticBootstrap.Start(AppVersionForDiagnostics());

        try
        {
            Log("OnLaunched", null, "start");
            MainWindowInstance = new MainWindow();
            MainWindowInstance.Activate();
            Log("OnLaunched", null, "activated");
        }
        catch (Exception ex)
        {
            Log("OnLaunched", ex);
            DiagnosticBootstrap.RecordUnhandledException(ex, "OnLaunched");
            throw;
        }
    }

    /// <summary>
    /// 诊断会话里记录的应用版本。目前取程序集版本（WinUI 工程未声明 &lt;Version&gt;，属已知缺口）；
    /// 真正的构建标识随打包流程在 D6 提供。
    /// </summary>
    private static string AppVersionForDiagnostics()
        => typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown";

    private static void Log(string where, Exception? ex, string? note = null)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" [").Append(where).Append(']');
            if (note != null) sb.Append(' ').Append(note);
            if (ex != null) sb.AppendLine().Append(ex.ToString());
            File.AppendAllText(LogPath, sb.AppendLine().ToString(), new UTF8Encoding(false));
        }
        catch { }
    }
}