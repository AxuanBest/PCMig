// ============================================================================
//  UiFlushTrace —— A.5（P1-5）UI 节流的诊断出口
// ============================================================================
//  ★ 本文件**只 using System.***（零 WinUI 依赖）⇒ 与 UiBatchBuffer.cs 一样可被链入测试。
//    为什么它是独立文件而不是塞进 UiFlushPump.cs：UiFlushPump.cs 持有
//    DispatcherQueueTimer，**不能链入**测试项目；而 MigrationSessionViewModel.cs 需要
//    调用诊断入口，它本身是被链入的 ⇒ 诊断入口必须住在可链入的文件里。
//
//  开关：PCMIG_UI_FLUSH_TRACE=1 ⇒ 把每次 flush 的条数/丢弃数写
//        %TEMP%\pcmig-ui-flush-trace.log（照抄 MotionState.Trace 的成文做法）。
//        未开启时零开销。
// ============================================================================

using System;
using System.IO;

namespace PCMig.WinUI.Presentation;

/// <summary>节流诊断（默认关闭；<c>PCMIG_UI_FLUSH_TRACE=1</c> 打开）。</summary>
internal static class UiFlushTrace
{
    private static bool _checked;
    private static bool _enabled;

    /// <summary>开关只读一次（与既有 PCMIG_* 开关同风格）。</summary>
    public static bool Enabled
    {
        get
        {
            if (_checked) return _enabled;
            _checked = true;
            _enabled = string.Equals(Environment.GetEnvironmentVariable("PCMIG_UI_FLUSH_TRACE"), "1", StringComparison.Ordinal);
            return _enabled;
        }
    }

    /// <summary>写一行诊断（未开启时零开销；任何失败都不得影响 UI）。</summary>
    public static void Write(string line)
    {
        if (!Enabled) return;
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "pcmig-ui-flush-trace.log");
            File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine);
        }
        catch
        {
            // 诊断通道永远不能影响 UI。
        }
    }
}