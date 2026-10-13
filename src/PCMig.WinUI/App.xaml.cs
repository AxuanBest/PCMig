using System;
using System.IO;
using System.Text;
using Microsoft.UI.Xaml;
using PCMig.Core.Diagnostics;
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

        // ── ★ P1-10 ★ 启动分段证据最早入口 ─────────────────────────────────────────
        // 用户口径里的"首启慢"此前只是主观描述：既没有"进程创建"这一刻的基线，也没有分段。
        // 这里在最前面开始计时，后续每一段（app-ctor / onlaunched / window-created /
        // window-activated / shell-loaded / interactive）都落到 startup-timeline.jsonl。
        // 纪律：只记事实，不改启动路径；写失败自己吞（见 StartupTimeline），绝不阻断启动。
        StartupTimeline.Begin("PCMig.WinUI");
        StartupTimeline.Mark("app-ctor");

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

        // ── ★ P0-06(4) ★ 后台异常的两条出口：失败证据不得无声丢失 ──────────────────
        // 现场症状是"偶发崩溃"，但 ZIP 里既没有 WER/Application Hang 1002，也没有转储，
        // 因此**不能**把用户描述归因到某个函数。这里先保证两件事：
        //   1) 被丢弃的 Task 异常（没人 await 的后台任务）不再无声消失；
        //   2) 非 UI 线程上的未处理异常也留下一条可读事实（其它两类异常在别处已经记）。
        // 纪律：只**记录**事实，不改任何业务结论、不改变崩溃语义（不设立"假装没发生"的吞口）。
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("UnobservedTaskException", e.Exception);
            // ★ 同步落盘一份证据 ★（应用日志是异步队列，进程即将结束的最后几条可能还在队列里）。
            CrashEvidence.Write("task-unobserved", e.Exception?.ToString() ?? "（无异常对象）", "UnobservedTaskException");
            DiagnosticBootstrap.RecordUnhandledException(e.Exception, "UnobservedTaskException");
            // 观察一次的语义 = "这条事实已经被记下来了"；.NET 的既定行为是把它记为已观察，
            // 避免同一条异常在 GC 之后被当作致命错误再抛一次（证据已留，不构成静默吞掉）。
            e.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log("AppDomainUnhandled", e.ExceptionObject as Exception,
                e.IsTerminating ? "IsTerminating=true" : "IsTerminating=false");
            // ★ 也可能是 CLR 结束进程前的最后一刻 ★：证据必须写完即落盘（含 IsTerminating 事实）。
            CrashEvidence.Write(
                "appdomain-unhandled",
                (e.ExceptionObject as Exception)?.ToString() ?? e.ExceptionObject?.ToString() ?? "（无异常对象）",
                e.IsTerminating ? "AppDomainUnhandled(IsTerminating=true)" : "AppDomainUnhandled(IsTerminating=false)");
            if (e.ExceptionObject is Exception ex)
                DiagnosticBootstrap.RecordUnhandledException(ex, "AppDomainUnhandled");
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupTimeline.Mark("onlaunched");

        // ★ 诊断尽早启动 ★ 早于任何 VM/Core 观察点被调用；失败一律降级（绝不阻止应用启动）。
        DiagnosticBootstrap.Start(AppVersionForDiagnostics());
        StartupTimeline.Mark("diagnostics-started");   // ★ P1-10 ★ 诊断启动本身花了多少（慢启动的头号嫌疑之一）

        try
        {
            Log("OnLaunched", null, "start");
            MainWindowInstance = new MainWindow();
            StartupTimeline.Mark("window-created");
            MainWindowInstance.Activate();
            StartupTimeline.Mark("window-activated");
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