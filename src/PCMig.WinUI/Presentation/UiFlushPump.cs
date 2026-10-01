// ============================================================================
//  UiFlushPump —— A.5（P1-5）UI 节流的**薄泵**：只做节拍与 Drain
// ============================================================================
//  ★ 本文件**故意不链入** tests\PCMig.Core.Tests（见 PCMig.Core.Tests.csproj 的
//    §A5LinkedPresentationSources）：它引用 Microsoft.UI.Dispatching.DispatcherQueueTimer，
//    而测试项目的 shim 只替身了 DispatcherQueue。把 timer 关在本文件里，VM 自身就一个
//    WinUI 定时器类型都不引用 ⇒ 测试项目仍能链入 VM 编译并做行为级测试。
//    （因此**纯接口** IUiFlushPump 定义在可链入的 UiBatchBuffer.cs 里，不在这里。）
//
//  两条成文纪律（本项目已踩过的坑，必须照抄）：
//    1) **必须持强引用**：DispatcherQueueTimer 若只被弱引用/局部变量持有会被 GC 回收，
//       回调永不触发（依据 MotionDirector.cs:645-648、InteractionFeedback.cs:328、
//       PageTransitionCoordinator.cs:22）。此处的引用是实例字段 _timer（且回调闭包也自持）。
//    2) **必须在进程退出前 Stop**：DispatcherQueueTimer 是 thread-pool 支撑的 WinRT 对象，
//       进程退出时 Windows App SDK 会在 Microsoft.UI.Xaml.dll 的
//       DllMain(DLL_PROCESS_DETACH) → DeinitializeDll → ThreadPoolService::ReleaseFactories
//       路径上释放其缓存的线程池激活工厂；若对象仍被持有会命中已卸载模块而崩溃
//       （实测 Microsoft.UI.Xaml.dll+0x7F9880 读 NULL +0x58，7/7 转储指纹一致）。
//       依据 MotionState.cs:103-108、MainWindow.xaml.cs:198-216。
//    3) 回调内**自持 timer 引用**，不在回调里依赖字段查找（MotionState.cs:77-78 的既定做法：
//       Dispose 之后字段可能已清空，再去查字段会导致 Stop 不掉）。
//
//  明确禁止的替代做法：Task.Run(async () => { while (true) { await Task.Delay(50); … } })
//    自转循环 —— 它没有与 UI 线程绑定的停止路径，任务结束后会变成**幽灵刷新源**。
// ============================================================================

using System;
using System.IO;
using Microsoft.UI.Dispatching;

namespace PCMig.WinUI.Presentation;

/// <summary>
/// 基于 <see cref="DispatcherQueueTimer"/> 的固定节拍泵（50ms = 20Hz）。
///
/// 为什么必须是固定节拍而不是"按需 TryEnqueue + Stopwatch 判定"：
///   · 只按 Stopwatch 判定**没有后边沿** ⇒ 最后一次事件之后若不再有事件（例如复制完最后一个
///     文件后进入数十秒停滞），那批数据**永远不会上屏**（"最近一个文件"停在旧值 = 正确性缺陷）；
///   · 只用一个"已排队"合并标志**不限制速率** ⇒ 刷新项自我重排会连续占满 UI 线程，洪峰时
///     照样饿死输入与绘制（"界面卡住"的观感不变）。
///   固定节拍同时给出**频率上界（20Hz）**与**尾边保证**。
/// </summary>
public sealed class DispatcherQueueUiFlushPump : IUiFlushPump
{
    /// <summary>节拍间隔：50ms（20Hz）。依据见 docs\A5-节流与竞态修复设计.md §2.1。</summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(50);

    private readonly object _gate = new();
    private readonly DispatcherQueue _queue;
    private readonly Func<bool> _onTick;          // 返回 true = 还有待处理数据（继续滴答）

    private DispatcherQueueTimer? _timer;         // ★ 强引用（纪律 1）★
    private bool _stopped;

    /// <param name="queue">UI 线程队列（构造时已确认非 null）。</param>
    /// <param name="onTick">节拍回调：Drain + 应用 UI；返回是否仍需继续滴答。</param>
    public DispatcherQueueUiFlushPump(DispatcherQueue queue, Func<bool> onTick)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _onTick = onTick ?? throw new ArgumentNullException(nameof(onTick));
    }

    public TimeSpan Interval => FlushInterval;

    public bool IsRunning
    {
        get { lock (_gate) { return _timer is { IsRunning: true }; } }
    }

    /// <summary>
    /// 建立并启动 timer（**只在 UI 线程调用**）。幂等：已建立就只保证它在跑。
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_stopped) return;

            if (_timer is null)
            {
                var timer = _queue.CreateTimer();
                timer.Interval = FlushInterval;
                timer.IsRepeating = true;
                // ★ 回调自持 timer 引用（纪律 3）：不在回调里依赖字段查找。
                timer.Tick += (s, _) =>
                {
                    var keep = true;
                    try
                    {
                        keep = _onTick();
                    }
                    catch
                    {
                        // 节拍回调里的异常绝不允许外溢（否则会打断 DispatcherQueue 的派发）。
                        keep = false;
                    }

                    if (!keep)
                    {
                        try { s.Stop(); } catch { /* 关闭期异常不得外溢 */ }
                    }
                };
                _timer = timer;   // ★ 强引用（纪律 1）
            }

            if (!_timer.IsRunning) _timer.Start();
        }
    }

    /// <summary>
    /// 停止并断开（幂等；绝不外抛）。任务结束 / 取消 / 异常 / 窗口关闭都会走这里。
    /// 若当前不在 UI 线程，则经 <c>TryEnqueue</c> 回 UI 线程再停（保守做法：
    /// 设计文档 §7 第 3 条明确"DispatcherQueueTimer.Start/Stop 在非 UI 线程是否线程安全未经实测"）。
    /// </summary>
    public void Stop()
    {
        DispatcherQueueTimer? timer;
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            timer = _timer;
            _timer = null;      // 先摘字段：即使 Stop 抛异常也不会留下"还持有 timer"的错觉
        }

        if (timer is null) return;

        if (_queue.HasThreadAccess)
        {
            try { timer.Stop(); } catch { /* 关闭期异常不得外溢 */ }
        }
        else
        {
            try
            {
                _queue.TryEnqueue(() => { try { timer.Stop(); } catch { /* 同上 */ } });
            }
            catch { /* 队列已关（关窗期）：本就无需再停 */ }
        }
    }
}