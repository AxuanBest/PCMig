using System.IO;
using System.Text;
using System.Windows;
using PCMig.Core.Logging;

namespace PCMig.Gui;

/// <summary>
/// 全局异常兜底（稳定性第一道闸）：界面线程、后台线程、未观察任务里的任何异常，都必须
/// ① 写进应用日志 ② 单独写一份 crash-*.log ③ 给用户一个看得懂、能直接转发的提示。
/// 绝不允许出现“点了没反应、什么都不说”的情况——那类问题排查成本最高。
/// </summary>
public partial class App : Application
{
    private static Serilog.ILogger? _log;

    /// <summary>给界面代码（如顶栏按钮）取的全局日志出口；未初始化时为 null，调用方一律用 ?. 。</summary>
    internal static Serilog.ILogger? Log => _log;

    /// <summary>
    /// 主题开关（纯 UI，不碰任何业务）：
    /// ① 经典回退：环境变量 PCMIG_CLASSIC_UI=1 或 exe 旁 classic-ui.flag → Theme/Classic.xaml
    /// ② 深色主题：环境变量 PCMIG_THEME=dark（不区分大小写）或 exe 旁 dark-ui.flag → Theme/Glass.Dark.xaml
    /// 三个字典同键不同值，所以切换只是换字典源，所有引用零改动。经典优先于深色。
    /// </summary>
    private void ApplyThemeChoice()
    {
        try
        {
            bool classic = Environment.GetEnvironmentVariable("PCMIG_CLASSIC_UI") == "1"
                || File.Exists(Path.Combine(AppContext.BaseDirectory, "classic-ui.flag"));
            bool dark = !classic && (string.Equals(Environment.GetEnvironmentVariable("PCMIG_THEME"), "dark",
                    StringComparison.OrdinalIgnoreCase)
                || File.Exists(Path.Combine(AppContext.BaseDirectory, "dark-ui.flag")));
            if (!classic && !dark) return;

            string target = classic ? "Theme/Classic.xaml" : "Theme/Glass.Dark.xaml";
            void Swap(System.Windows.ResourceDictionary d)
            {
                if (d.Source?.OriginalString?.Contains("Glass.xaml") == true)
                    d.Source = new Uri("pack://application:,,,/PCMig;component/" + target);
                foreach (var inner in d.MergedDictionaries) Swap(inner);
            }
            Swap(Resources);
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplyThemeChoice();   // 视觉回退开关（纯 UI，不碰业务）
        try { _log = LogBootstrap.CreateAppLogger(console: false); } catch { _log = null; }

        // 状态落盘告警出口（v0.3.5）：状态文件被外部占用、本次写入被跳过时进应用日志（不再静默）。
        // 用完整类型名，避免与其它命名空间引入 using 冲突；不改变任何业务逻辑。
        PCMig.Core.State.JsonStateStore.OnWarning = msg =>
        {
            try { _log?.Warning("状态告警: {Message}", msg); } catch { }
        };

        // 命令内部异常（RelayCommand 是 async void，漏出去会直接崩）
        RelayCommand.OnError = (name, ex) => Report("界面命令：" + name, ex, fatal: false);

        DispatcherUnhandledException += (_, args) =>
        {
            Report("界面线程", args.Exception, fatal: false);
            args.Handled = true;   // 能救就救：界面继续可用，用户看完提示还能继续操作
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Report("未观察的任务", args.Exception, fatal: false);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Report("后台线程", args.ExceptionObject as Exception, fatal: true);
    }

    private static void Report(string where, Exception? ex, bool fatal)
    {
        try { _log?.Error(ex, "未处理异常（{Where}）", where); } catch { }

        string? crashFile = null;
        try
        {
            Directory.CreateDirectory(LogBootstrap.AppLogDir);
            crashFile = Path.Combine(LogBootstrap.AppLogDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(crashFile,
                $"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n位置：{where}\r\n致命：{fatal}\r\n\r\n{ex}\r\n",
                new UTF8Encoding(true));
        }
        catch { }

        try
        {
            var tail = fatal
                ? "程序可能即将退出。已完成的数据不受影响，重新打开后可用「恢复任务」从中断处继续（已传部分不会重传）。"
                : "本次操作已中止，已完成的数据不受影响，可以直接重试。";
            AppDialog.Show(Current?.MainWindow,
                $"PCMig 遇到一个未处理的错误。\n\n位置：{where}\n错误：{ex?.GetType().Name} {ex?.Message}\n\n{tail}\n\n" +
                (crashFile != null ? "崩溃记录：" + crashFile + "\n" : "") +
                "应用日志目录：" + LogBootstrap.AppLogDir + "\n\n请把上面这个目录（或那份崩溃记录）发给我们，可直接定位。",
                "PCMig 遇到一个未处理的错误", MessageBoxButton.OK,
                fatal ? MessageBoxImage.Error : MessageBoxImage.Warning);
        }
        catch { }
    }
}
