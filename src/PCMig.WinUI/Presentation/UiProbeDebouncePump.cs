// ============================================================================
//  UiProbeDebouncePump —— A.5（P2-6）「未完成任务探测」的**一次性去抖计时器**
// ============================================================================
//  ★ 本文件**故意不链入** tests\PCMig.Core.Tests（见 PCMig.Core.Tests.csproj 的
//    §A5LinkedPresentationSources，与 UiFlushPump.cs 同款分层）：它引用
//    Microsoft.UI.Dispatching.DispatcherQueueTimer，而测试项目的 shim 只替身了 DispatcherQueue。
//    把 timer 关在本文件里，被链入的 MigrationSessionViewModel.cs 就一个 WinUI 定时器类型都不引用
//    ⇒ 测试项目仍能链入 VM 编译并做行为级测试（单测用 UnfinishedProbeDebouncePumpFactoryForTest 注入假泵）。
//
//  三条成文纪律（与 UiFlushPump.cs 同源，逐条必须照抄）：
//    1) **必须持强引用**：DispatcherQueueTimer 若只被弱引用/局部变量持有会被 GC 回收，
//       回调永不触发（依据 MotionDirector.cs:645-648、InteractionFeedback.cs:328、
//       PageTransitionCoordinator.cs:22）。此处的引用是实例字段 _timer（回调闭包也自持）。
//    2) **必须在进程退出前 Stop**：DispatcherQueueTimer 是 thread-pool 支撑的 WinRT 对象，
//       进程退出时 Windows App SDK 会在 Microsoft.UI.Xaml.dll 的
//       DllMain(DLL_PROCESS_DETACH) → DeinitializeDll → ThreadPoolService::ReleaseFactories
//       路径上释放其缓存的线程池激活工厂；若对象仍被持有会命中已卸载模块而崩溃
//       （实测 Microsoft.UI.Xaml.dll+0x7F9880 读 NULL +0x58，7/7 转储指纹一致）。
//       ⇒ 停止折进 MigrationSessionViewModel.StopUiRefresh()（**没有第二个公开出口**）。
//    3) 回调内**自持 timer 引用**，不在回调里依赖字段查找（MotionState.cs:77-78 的既定做法）。
//
//  与节拍泵（UiFlushPump.cs）的三点差别（所以没有复用同一个类）：
//    · IsRepeating = **false**（一次性；到期即自然停），并且**可被 Arm() 反复重置**
//      （用户每敲一个字符就重置窗口 —— 这是 debounce 的定义）；
//    · Stop() 是**永久**停止（关闭路径专用语义：之后 Arm() 必须无效，绝不让计时器在退出瞬间复活）；
//    · 多一个 Disarm()（取消尚未到期的窗口：连接建立走"立即分支"时先取消排队中的窗口，避免重复探测）。
//
//  明确禁止的替代做法：Task.Delay(400).ContinueWith(...) / Task.Run(async while(true)…) ——
//    它们没有与 UI 线程绑定的停止路径（关闭期会变成幽灵刷新源），也没有"谁负责写 UI 状态"的归属。
// ============================================================================

using System;
using Microsoft.UI.Dispatching;

namespace PCMig.WinUI.Presentation;

/// <summary>
/// 基于 <see cref="DispatcherQueueTimer"/> 的**一次性去抖**计时器（400ms，实现见
/// <see cref="IUnfinishedProbeDebouncePump"/> 的语义约定）。
///
/// 线程纪律：只在 UI 线程 <see cref="Arm"/>/<see cref="Disarm"/>（调用方是会话的会话属性变更处理，
/// 必定在 UI 线程）；<see cref="Stop"/> 允许在关闭路径任意线程被调用（内部走 <c>TryEnqueue</c> 保守处理）。
/// </summary>
public sealed class DispatcherQueueProbeDebouncePump : IUnfinishedProbeDebouncePump
{
    private readonly object _gate = new();
    private readonly DispatcherQueue _queue;
    private readonly Action _onElapsed;
    private readonly TimeSpan _interval;

    private DispatcherQueueTimer? _timer;         // ★ 强引用（纪律 1）★
    private bool _stopped;

    /// <param name="queue">UI 线程队列（构造时已确认非 null）。</param>
    /// <param name="onElapsed">去抖到期回调（一次性；绝不允许异常外溢）。</param>
    /// <param name="interval">去抖窗口；默认 <see cref="MigrationSessionViewModel.UnfinishedProbeDebounceInterval"/>（400ms）。</param>
    public DispatcherQueueProbeDebouncePump(DispatcherQueue queue, Action onElapsed, TimeSpan? interval = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _onElapsed = onElapsed ?? throw new ArgumentNullException(nameof(onElapsed));
        _interval = interval ?? MigrationSessionViewModel.UnfinishedProbeDebounceInterval;
    }

    public TimeSpan Interval => _interval;

    public bool IsArmed
    {
        get { lock (_gate) { return !_stopped && _timer is { IsRunning: true }; } }
    }

    /// <summary>起/重置去抖窗口（幂等：已在计时就先停再起，窗口从当前时刻重新计起）。</summary>
    public void Arm()
    {
        lock (_gate)
        {
            if (_stopped) return;   // 关闭路径已走：绝不让计时器复活

            if (_timer is null)
            {
                var timer = _queue.CreateTimer();
                timer.Interval = _interval;
                timer.IsRepeating = false;   // 一次性：到期即自然停；下一次 Arm() 再 Start
                // ★ 回调自持 timer 引用（纪律 3）：不在回调里依赖字段查找。
                timer.Tick += (s, _) =>
                {
                    try { s.Stop(); } catch { /* 关闭期异常不得外溢 */ }
                    try { _onElapsed(); }
                    catch { /* 去抖回调里的异常绝不允许外溢（会打断 DispatcherQueue 的派发） */ }
                };
                _timer = timer;   // ★ 强引用（纪律 1）
            }

            if (_timer.IsRunning) _timer.Stop();   // 重置窗口：先停再起
            _timer.Start();
        }
    }

    /// <summary>取消尚未到期的窗口（no-op 安全；不影响已经发起的探测）。</summary>
    public void Disarm()
    {
        DispatcherQueueTimer? timer;
        lock (_gate) { timer = _stopped ? null : _timer; }
        if (timer is null) return;
        try { if (timer.IsRunning) timer.Stop(); } catch { /* 取消失败不得影响调用方 */ }
    }

    /// <summary>
    /// 永久停止并断开（幂等；绝不外抛）。窗口关闭路径专用：之后 <see cref="Arm"/> 一律无效。
    /// 若当前不在 UI 线程，则经 <c>TryEnqueue</c> 回 UI 线程再停（D2 的保守做法：
    /// DispatcherQueueTimer.Start/Stop 在非 UI 线程是否线程安全未经实测）。
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