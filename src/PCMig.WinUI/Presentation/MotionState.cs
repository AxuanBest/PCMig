using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace PCMig.WinUI.Presentation;

/// <summary>
/// Motion 防重入（`IsTransitioning`）。
///
/// 语义（与"吞点击"明确区分）：
///   • **导航/面板状态一律照常切换**——第二次点击永远生效，绝不因为"正在动画"而丢掉；
///   • 被拦下的只是**重入的那一次入场动画**：调用方据此跳过一次重放，避免动画叠加/抖动；
///   • 按 key 独立判定（页面入场与面板入场互不牵连），key 的上一次过渡未结束前，
///     同 key 的新入场请求会被记为一次 suppress。
///
/// 只属于 UI 层：不读也不写任何业务状态，不碰 ViewModel / Command / 绑定 / 存档。
///
/// 诊断开关：仅当环境变量 `PCMIG_MOTION_TRACE=1` 时，把 begin/suppress/end 追加到
/// `%TEMP%\pcmig-motion-trace.log`；不设该变量时完全无副作用（不建文件、不做 IO）。
/// </summary>
internal sealed class MotionState
{
    private static readonly MotionState Instance = new();

    public static MotionState Current => Instance;

    private readonly Dictionary<string, DispatcherQueueTimer> _timers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);

    private long _suppressed;
    private bool _disposed;
    private static bool _traceChecked;
    private static bool _traceEnabled;

    /// <summary>是否有任意 key 的过渡仍在进行中。</summary>
    public bool IsTransitioning => _inFlight.Count > 0;

    /// <summary>累计被拦下的重入入场次数（诊断用）。</summary>
    public long SuppressedReentries => Interlocked.Read(ref _suppressed);

    /// <summary>
    /// 申请开始一次入场动画。
    /// 返回 true＝本次可以重放动画；false＝同 key 的过渡尚未结束，本次应**跳过动画**
    /// （但仍然照常完成状态切换）。
    /// </summary>
    public bool TryBegin(string key, TimeSpan window)
    {
        if (window <= TimeSpan.Zero) window = TimeSpan.FromMilliseconds(200);

        // 关闭流程已经释放过：不再创建任何 thread-pool 支撑的定时器，
        // 直接放行（此时也不会有真正的动画需要防重入）。
        if (_disposed) return true;

        if (_inFlight.Contains(key))
        {
            Interlocked.Increment(ref _suppressed);
            Trace("SUPPRESS", key, "in-flight");
            return false;
        }

        _inFlight.Add(key);
        Trace("BEGIN", key, window.TotalMilliseconds.ToString("F0"));

        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            // 拿不到 UI 队列（例如非 UI 线程）：不做计时，直接放行，避免永久卡在"过渡中"。
            _inFlight.Remove(key);
            return true;
        }

        var timer = queue.CreateTimer();
        timer.Interval = window;
        timer.IsRepeating = false;
        // 回调内自带 timer 引用，不依赖字典查找——Dispose 之后字典可能已清空，
        // 若仍去查字典会让定时器无法 Stop（本缺陷的原始成因就属于这一类"释放后再依赖"）。
        timer.Tick += (_, _) =>
        {
            _inFlight.Remove(key);
            timer.Stop();
            Trace("END", key, "released");
        };
        _timers[key] = timer;
        timer.Start();
        return true;
    }

    /// <summary>立即释放某个 key（例如面板被直接关掉时），保证不会卡在过渡中。</summary>
    public void Release(string key)
    {
        if (_inFlight.Remove(key))
        {
            if (_timers.TryGetValue(key, out var t)) t.Stop();
            Trace("END", key, "manual");
        }
    }

    /// <summary>
    /// 关闭流程调用：停止并释放所有 <see cref="DispatcherQueueTimer"/>。
    ///
    /// 为什么必须有这个：`DispatcherQueueTimer` 是 thread-pool 支撑的 WinRT 对象。
    /// Windows App SDK 在进程退出时会在 `Microsoft.UI.Xaml.dll` 的
    /// `DllMain(DLL_PROCESS_DETACH) → DeinitializeDll → ThreadPoolService::ReleaseFactories`
    /// 路径上释放其缓存的线程池激活工厂；若这些对象仍被持有，会命中已卸载模块而崩溃
    /// （实测：`Microsoft.UI.Xaml.dll+0x7F9880` 读 NULL `+0x58`，6/6 转储指纹一致）。
    /// 在窗口关闭时先停止并断开全部定时器，可让框架侧的释放路径无事可做。
    ///
    /// 只做释放，不改任何业务状态；可重复调用。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var pair in _timers)
        {
            try { pair.Value.Stop(); } catch { /* 关闭期任何异常都不能外溢 */ }
        }
        _timers.Clear();
        _inFlight.Clear();
        Trace("DISPOSE", "all", "timers released");
    }

    private static void Trace(string verb, string key, string detail)
    {
        if (!_traceChecked)
        {
            _traceChecked = true;
            _traceEnabled = string.Equals(Environment.GetEnvironmentVariable("PCMIG_MOTION_TRACE"), "1", StringComparison.Ordinal);
        }

        if (!_traceEnabled) return;

        try
        {
            var path = Path.Combine(Path.GetTempPath(), "pcmig-motion-trace.log");
            File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + " " + verb + " " + key + " " + detail + Environment.NewLine);
        }
        catch
        {
            // 诊断通道永远不能影响 UI。
        }
    }
}
