using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;

namespace PCMig.WinUI.Diagnostics;

/// <summary>
/// Deep Trace 的**有限 Routed 输入观测**（方案 §9 / D6.1 §1）。
///
/// 本类只是一个**薄适配器**：真正"什么能记、什么必须丢"的策略在契约层的
/// <see cref="InputObservationPolicy"/> 里（纯函数，可被行为测试直接验证，不依赖 WinUI）。
///
/// 允许做 / 绝不允许做：
///   · ✅ 只在本应用自己的 XAML 根上 `AddHandler(..., handledEventsToo: true)`；
///   · ✅ 只记：**语义类别**（`InputCategories` 白名单）+ 稳定 ControlId + 可用/可见 + 抑制计数；
///   · ❌ **永不记按键身份**：`OnKeyDown` 只把 VK 码交给 `InputObservationPolicy.ClassifyKey`，
///        得到的只是类别 token（`key-tab`/`key-directional`…）；绝无 `"key:" + e.Key` 这种写法；
///   · ❌ **敏感来源整体丢弃**：祖先链里出现 PasswordBox／密码类 ControlId ⇒ 不解析 ID、不发布事件，
///        只累加"被丢弃条数"（不记长度/文本/IME/剪贴板，也不看其它祖先 AutomationId 绕过）；
///   · ❌ 不写 `Handled`/`Focus`/`Capture`、不用全局钩子、不记坐标轨迹；
///   · ⏱ **限时**：到期自动停；用户关闭 Deep 立即停；关窗立即停；
///   · 🔢 **限流**：同一控件+同一类别在抑制窗口内只累加计数（字典有上限，绝不无限增长）。
/// </summary>
public sealed class DeepTraceInputObserver
{
    /// <summary>Deep Trace 的默认最长观测时长（候选值，不是 SLA；到期自动停止）。</summary>
    public static readonly TimeSpan DefaultMaxDuration = TimeSpan.FromSeconds(60);

    /// <summary>同一控件+类别的重复输入抑制窗口（防点风暴把事件流打满）。</summary>
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromMilliseconds(150);

    /// <summary>去重字典的条目上限（超出即清空重来：有界，绝不随会话长度增长）。</summary>
    public const int MaxDedupeEntries = 256;

    private readonly IDiagnosticPublisher _publisher;
    private readonly Func<string?> _rootOwnerComponent;

    private readonly List<FrameworkElement> _registered = new();
    private readonly Dictionary<string, (DateTime LastUtc, int Suppressed)> _lastByControl = new(StringComparer.Ordinal);

    /// <summary>复用的祖先链缓冲（UI 线程单线程访问；稳态零分配）。</summary>
    private readonly List<InputSourceNode> _ancestry = new(InputObservationPolicy.MaxAncestryDepth);

    private DispatcherQueueTimer? _timer;
    private DateTime _startedUtc;
    private DateTime _deadlineUtc;
    private bool _isActive;
    private long _observed;
    private long _droppedSensitive;

    public DeepTraceInputObserver(IDiagnosticPublisher publisher, string rootOwnerComponent = "Shell")
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _rootOwnerComponent = () => rootOwnerComponent;
    }

    public bool IsActive => _isActive;

    /// <summary>已观察到的输入条数（用于界面/诊断回显）。</summary>
    public long ObservedCount => _observed;

    /// <summary>因命中敏感来源而丢弃的输入条数（只记条数，不记身份）。</summary>
    public long SensitiveDroppedCount => _droppedSensitive;

    /// <summary>剩余时间（未激活时为 TimeSpan.Zero）。</summary>
    public TimeSpan Remaining => _isActive ? _deadlineUtc - DateTime.UtcNow : TimeSpan.Zero;

    /// <summary>启动观测（幂等；已激活时先停再起，避免重复注册）。</summary>
    public void Start(FrameworkElement root, TimeSpan? maxDuration = null)
    {
        if (root is null) return;
        Stop();

        var duration = maxDuration ?? DefaultMaxDuration;
        if (duration <= TimeSpan.Zero) return;

        _startedUtc = DateTime.UtcNow;
        _deadlineUtc = _startedUtc + duration;
        _lastByControl.Clear();
        _observed = 0;
        _droppedSensitive = 0;

        Register(root);
        _isActive = true;
        StartTimer();

        // 如实记录"观测开始"（含范围与时限，不假装能看全系统输入）。
        Publish(InputCategories.LifecycleStarted, true, true, 0);
    }

    /// <summary>停止观测并**注销全部处理器**（幂等、绝不外抛）。</summary>
    public void Stop()
    {
        if (!_isActive && _registered.Count == 0 && _timer is null) return;
        _isActive = false;

        foreach (var element in _registered)
        {
            try
            {
                element.RemoveHandler(UIElement.PointerPressedEvent, (PointerEventHandler)OnPointerPressed);
                element.RemoveHandler(UIElement.TappedEvent, (TappedEventHandler)OnTapped);
                element.RemoveHandler(UIElement.KeyDownEvent, (KeyEventHandler)OnKeyDown);
            }
            catch (Exception)
            {
                // 元素已卸载等情形：注销失败不影响正确性（对象随之释放）。
            }
        }
        _registered.Clear();

        if (_timer is not null)
        {
            try { _timer.Stop(); } catch (Exception) { /* 停止失败不影响结论 */ }
            _timer = null;
        }

        Publish(InputCategories.LifecycleStopped, true, true, (int)Math.Min(int.MaxValue, _observed));
    }

    private void Register(FrameworkElement element)
    {
        // handledEventsToo: true 是**必须**的：子控件（按钮等）通常会把 PointerPressed 标记为已处理，
        // 否则根上永远收不到 —— 那样"点了没反应"就无从判断输入到底有没有到达。
        element.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerPressed), handledEventsToo: true);
        element.AddHandler(UIElement.TappedEvent, new TappedEventHandler(OnTapped), handledEventsToo: true);
        element.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), handledEventsToo: true);
        _registered.Add(element);
    }

    /// <summary>为某个浮层单独注册（浮层是独立生命周期，按方案要求分别登记）。</summary>
    public void RegisterOverlay(FrameworkElement overlay)
    {
        if (!_isActive || overlay is null) return;
        if (_registered.Contains(overlay)) return;
        Register(overlay);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        => Observe(InputCategories.PointerPressed, e.OriginalSource as DependencyObject);

    private void OnTapped(object sender, TappedRoutedEventArgs e)
        => Observe(InputCategories.Tapped, e.OriginalSource as DependencyObject);

    /// <summary>★ D6.1 §1 ★ 只传 **VK 码**给契约层做类别归类，本方法内**不存在**任何按键身份字符串。</summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
        => Observe(InputObservationPolicy.ClassifyKey((int)e.Key), e.OriginalSource as DependencyObject);

    /// <summary>
    /// 记录一次输入。**只读**：不设置 Handled、不取焦点、不捕获指针、不改任何状态。
    /// 类别必须来自 <see cref="InputCategories"/> 白名单；敏感来源在**解析任何 ID 之前**就整条丢弃。
    /// </summary>
    private void Observe(string category, DependencyObject? source)
    {
        if (!_isActive) return;
        if (DateTime.UtcNow > _deadlineUtc) { Stop(); return; }

        try
        {
            // 白名单兜底：类别不在固定集合里 ⇒ 宁可不记（防未来有人再塞进带身份的字符串）。
            if (!InputCategories.IsKnown(category)) return;

            BuildAncestry(source, out var isEnabled, out var isVisible);

            // ★ 敏感来源：整条丢弃（连 ControlId 都不解析、不发布）★
            if (InputObservationPolicy.IsSensitiveSource(_ancestry))
            {
                _droppedSensitive++;
                return;
            }

            var controlId = InputObservationPolicy.ResolveControlId(_ancestry);
            var dedupeKey = controlId is null ? category : controlId + "|" + category;

            var now = DateTime.UtcNow;
            var suppressed = 0;
            if (_lastByControl.TryGetValue(dedupeKey, out var last))
            {
                if (now - last.LastUtc < DuplicateWindow)
                {
                    // 抑制窗口内：只累加计数，不逐次建事件（防点风暴）。
                    _lastByControl[dedupeKey] = (last.LastUtc, last.Suppressed + 1);
                    return;
                }
                suppressed = last.Suppressed;
                _lastByControl[dedupeKey] = (now, 0);
            }
            else
            {
                if (_lastByControl.Count >= MaxDedupeEntries) _lastByControl.Clear();   // 有界
                _lastByControl[dedupeKey] = (now, 0);
            }

            _observed++;
            Publish(category, isEnabled, isVisible, suppressed, controlId);
        }
        catch (Exception)
        {
            // 观测绝不影响交互。
        }
    }

    /// <summary>
    /// 构建祖先链（叶子 → 根，**有界深度**），同时解析最近可用/可见状态。
    /// 只读取类型名与稳定 AutomationId；**不读任何文本内容**。
    /// </summary>
    private void BuildAncestry(DependencyObject? source, out bool isEnabled, out bool isVisible)
    {
        _ancestry.Clear();
        isEnabled = true;
        isVisible = true;

        var node = source;
        for (var depth = 0; node is not null && depth < InputObservationPolicy.MaxAncestryDepth; depth++)
        {
            if (node is FrameworkElement fe)
            {
                _ancestry.Add(new InputSourceNode(
                    fe.GetType().Name,
                    AutomationProperties.GetAutomationId(fe)));

                if (depth == 0)
                {
                    // IsEnabled 在 Control 上（FrameworkElement 没有该属性）⇒ 非控件元素按"可用"处理。
                    isEnabled = fe is Control control ? control.IsEnabled : true;
                    isVisible = fe.Visibility == Visibility.Visible;
                }
            }

            node = VisualTreeHelper.GetParent(node);
        }
    }

    private void StartTimer()
    {
        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null) return;

        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) =>
        {
            if (!_isActive) { _timer?.Stop(); return; }
            if (DateTime.UtcNow > _deadlineUtc) Stop();      // 到期自动停（不需要用户记得关）
        };
        _timer.Start();
    }

    private void Publish(string category, bool isEnabled, bool isVisible, int suppressed, string? controlId = null)
    {
        try
        {
            var descriptor = UiEvents.InputObserved;
            if (!_publisher.IsEnabledFor(descriptor)) return;

            _publisher.TryPublish(new DiagnosticEventDraft(
                descriptor,
                global::PCMig.Core.Diagnostics.CoreDiagnostics.Sink.Root(_rootOwnerComponent())
                    .WithControl(controlId ?? "unknown"),
                new UiInputObservedPayload(
                    category, isEnabled, isVisible, suppressed,
                    (int)Math.Min(int.MaxValue, _droppedSensitive)),
                Level: DiagnosticLevel.Debug,
                Outcome: DiagnosticOutcome.Succeeded,
                StateOwner: StateOwner.View));
        }
        catch (Exception)
        {
            // 观测绝不影响交互。
        }
    }
}