using PCMig.Core.Diagnostics;
using PCMig.Core.Logging;
using Serilog;

namespace PCMig.WinUI.Diagnostics;

/// <summary>
/// ★ P0-06(4) ★ 界面事件处理器（<c>async void</c>）的统一兜底出口。
///
/// 为什么必须有：<c>async void</c> 里抛出的异常**没有调用者可以接住**，它直接落到
/// <see cref="App"/> 的 XAML UnhandledException 上 ⇒ 进程结束。现场"点停止时偶发崩溃"
/// 与"服务端/网络异常路径"正好经过这些处理器，而它们当中多数当时没有任何 try/catch。
///
/// 纪律（写死在这里，别在调用点上重新解释）：
///   · **记录，不吞**：日志（app-*.log）+ 诊断事实各留一份；绝不让"操作失败"变成静默无事发生；
///   · **面向用户呈现**：调用方给出 surface 时，把失败写进该页面自己的状态行/日志区，
///     用户看到"这次操作失败了 + 证据已留下"，而不是界面毫无反应；
///   · 本类型自身**绝不抛**（它一旦抛，就又变成新的崩溃源）。
/// </summary>
internal static class UiFaultGuard
{
    private static readonly ILogger Log = LogBootstrap.CreateAppLogger(console: false);

    /// <param name="where">标识用的处理器名（如 "Step2SelectDataPage.Stop_Click"）。</param>
    /// <param name="ex">被兜住的异常。</param>
    /// <param name="surface">面向用户的呈现出口（通常是该页面的状态行 / 会话日志）；可为 null。</param>
    public static void Report(string where, Exception ex, Action<string>? surface = null)
    {
        try { Log.Error(ex, "界面操作异常（{Where}）", where); } catch { /* 日志失败不得外溢 */ }
        // ★ 同步落盘一份 ★：应用日志走异步队列，崩溃前后最后几条可能来不及落盘；
        // 这条通道写完即 fsync，进程随后被结束也留得下证据。
        try { CrashEvidence.Write("ui-exception", ex.ToString(), where); } catch { /* 证据通道失败不影响业务 */ }
        try { DiagnosticBootstrap.RecordUnhandledException(ex, where); } catch { /* 同上 */ }

        if (surface is null) return;
        try
        {
            surface($"{where} 执行失败：{ex.Message}——失败证据已写入日志与诊断包，可到「诊断中心」导出。");
        }
        catch { /* 呈现失败也不得外溢 */ }
    }
}