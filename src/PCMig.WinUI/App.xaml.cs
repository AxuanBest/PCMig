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

        // ── ★ 2026-10-15（Preview.2 隔离整改）★ 预览隔离 Fail Closed 闸门 ────────────
        // 必须**早于** MainWindow / 任何 Core 调用（尤其早于日志初始化与任务库打开），
        // 否则已经写进正式数据根再报错就晚了。
        // 语义：只有"预览产物身份 + 隔离不可用"才拦截；正式产物下 Failure 恒为 null ⇒ 本闸门不介入。
        if (PCMig.Core.Util.PreviewIsolation.Failure is { } isolationFailure)
        {
            StartupTimeline.Mark("preview-isolation-failed");
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "preview-isolation-blocked.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " 已阻止启动：" + isolationFailure + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { /* 日志写不进去也要拦住 */ }

            ShowIsolationFailureAndExit(isolationFailure);
            return;
        }

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
            Presentation.MotionDirector.MarkShellInteractive();   // ★ 回归修复：此后读到的动画偏好 false 才算用户真实选择
            Log("OnLaunched", null, "activated");
#if DEBUG
            // ★ Preview.6 · 仅 Debug ★ 开发者 UI 实测驱动：未设环境变量时立即返回，对正常启动零影响。
            if (MainWindowInstance is MainWindow harnessTarget) Harness.UiHarness.Start(harnessTarget);
#endif
        }
        catch (Exception ex)
        {
            Log("OnLaunched", ex);
            DiagnosticBootstrap.RecordUnhandledException(ex, "OnLaunched");
            throw;
        }
    }

    /// <summary>
    /// 预览隔离不可用时，**不进入主界面**：弹一个说明对话框后退出。
    ///
    /// 为什么不能只写日志后继续启动：那样预览版会带着"无隔离"状态跑下去，
    /// 在打开任务库/写日志的第一步就落进 <c>%ProgramData%\PCMig\Jobs</c> /
    /// <c>%ProgramData%\PCMig\Logs</c>，或把诊断会话写进
    /// <c>%LocalAppData%\PCMig\Diagnostics</c> —— 正是本整改要根除的污染。宁可不让它启动。
    /// 也**绝不**回退到默认数据根（那是静默污染，比拒绝启动更危险）。
    ///
    /// ── ★ F-01 修复（R2 · 2026-10-17）★ ────────────────────────────────────────
    /// 旧实现用 `ContentDialog.ShowAsync()` 发射后不管，紧接着在 finally 里 `Environment.Exit(2)`：
    ///   · `ShowAsync()` 是**异步**的，返回时对话框还没显示，Exit 已经把进程干掉
    ///     ⇒ 用户只看到窗口一闪、程序消失，**看不到任何错误说明**；
    ///   · 更糟的是本闸门位于 `new MainWindow()` **之前**（App.xaml.cs:99 vs :110），
    ///     此时 `MainWindowInstance` 还是 null ⇒ `XamlRoot = null`，
    ///     ContentDialog 根本**建不起来**（会抛 InvalidOperationException 被 catch 吞掉）。
    ///     也就是说旧代码在这个调用点上 100% 走的是 catch → Exit 路径，对话框一次都没显示过。
    ///
    /// 现在改用 Win32 `MessageBoxW`：同步阻塞、不需要 XamlRoot、不依赖 WinUI 的窗口树，
    /// 在创建主窗口之前也能正常显示。它跑在专用 STA 线程上，主线程带超时等待：
    ///   · 用户在超时前点了「退出」⇒ 立刻 Exit(2)；
    ///   · 到超时（无人值守 / 无交互桌面）⇒ 同样 Exit(2)，并在隔离日志里记一条，
    ///     绝不让"没人点按钮"变成"进程永远挂着"。
    /// 无论哪条路径，退出码都是 2，且**绝不**回退正式数据根。
    /// </summary>
    private static void ShowIsolationFailureAndExit(string detail)
    {
        const int MessageBoxWaitMs = 300_000;   // 5 分钟：足够真人读完点确定，又不至于无人值守时永久挂住

        var body = detail + Environment.NewLine + Environment.NewLine +
                   "处理建议：使用包含 preview-isolation.json 的完整预览安装包重新安装。" + Environment.NewLine +
                   "为保护正式版数据，本程序不会在该状态下继续运行，也不会回退到正式数据目录。";

        try
        {
            var shown = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                try
                {
                    // MB_OK | MB_ICONERROR | MB_SETFOREGROUND | MB_TOPMOST | MB_TASKMODAL
                    MessageBoxW(IntPtr.Zero, body, "预览版数据隔离不可用，已阻止启动",
                                0x00000000u | 0x00000010u | 0x00010000u | 0x00040000u | 0x00002000u);
                }
                catch
                {
                    // 连 MessageBox 都起不来（例如会话无交互桌面）：仍然要退出。
                }
                finally
                {
                    shown.Set();
                }
            })
            {
                IsBackground = true,
                Name = "PCMIG-isolation-failure-dialog",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            if (!shown.Wait(MessageBoxWaitMs))
            {
                // 无人值守 / 无交互桌面：用户看不到提示，但**仍然必须退出**。
                // 这条痕迹写进与启动闸门同一个 log，便于事后判断"是超时退出的"。
                try
                {
                    File.AppendAllText(
                        Path.Combine(AppContext.BaseDirectory, "preview-isolation-blocked.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                        " 提示窗口等待 " + (MessageBoxWaitMs / 1000) + " 秒无人确认，按拒绝启动处理并退出（退出码 2）。" +
                        Environment.NewLine,
                        new UTF8Encoding(false));
                }
                catch { /* 日志写不进去也要退出 */ }
            }
        }
        catch
        {
            // 线程都起不来：仍然必须退出，不能继续跑。
        }
        finally
        {
            Environment.Exit(2);
        }
    }

    // ★ F-01 ★ 预览隔离失败提示用；不依赖 WinUI 窗口树，创建主窗口之前也能显示。
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = false)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

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