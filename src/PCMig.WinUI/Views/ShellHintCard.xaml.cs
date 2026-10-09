using System;
using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PCMig.WinUI.Presentation;
using Windows.Foundation;

namespace PCMig.WinUI.Views;

/// <summary>
/// 侧栏底部提示卡（Shell 层组件，四个 Step 共用同一份外观）。
///
/// ★ FIX BATCH 6（指令 §9）：状态消息路由 ★
///   本卡只做**通道 → 文本/可见性**的投影，不拼任何业务状态：
///     OperationalStatus    → <see cref="HintOperationalText"/>（空则显示「就绪」）
///     CurrentObjectStatus  → <see cref="HintObjectText"/>（空则折叠）
///     UserHint             → <see cref="HintUserText"/>（空则折叠）
///     ErrorSummary         → <see cref="HintErrorText"/>（空则折叠；状态行的图标/文案同步变化）
///   SecurityHint 按 §9 的映射**不进本卡**，它落在 Step 2 的「连接与安全提示」卡。
///
/// ★ UI Closure 2026-10-05 返修（P0：文字堆叠 / 假滚动条）★
///   1) 入场动画**只走 Composition 的 Translation 通道**（<see cref="MotionDirector.PlayLineEntrance"/>）。
///      旧实现写 <c>Visual.Offset</c> —— Offset 是"相对父 Visual 的位置属性"，会把 XAML 已经排好的
///      行位置整体塌向父容器原点，真机表现正是"文字全堆到卡片顶部、互相重叠、内容越多越乱"。
///      本文件从此**禁止**出现 <c>visual.Offset = ...</c> 与 <c>StartAnimation("Offset")</c>（有契约测试守着）。
///   2) 卡片高度由 <see cref="ApplyAdaptiveHeight"/>（唯一写入点）按"内容自然高 Clamp 到 [176, 可用上界]"
    ///      一次性决定；<b>没有</b>「ScrollViewer.SizeChanged → 逐帧动画 Height → 尺寸再变」的自激链。
    ///      2026-10-08 起内容装不下时不再用省略号丢字，而是长到上界后交给滚动条（Q5）。
///   3) 入场动画升级为**语义级**：只有"行从折叠变可见""对象真正切换""流程语义句变化（带 600 ms 节流）"
///      才播一次；同一对象的每秒数值刷新、同一句话重复抵达**绝不重播位移**。
/// </summary>
public sealed partial class ShellHintCard : UserControl
{
    private MigrationSessionViewModel? _session;

    /// <summary>
    /// 逐行动画策略（语义级，不看"字符串是否不同"就播）：
    /// <list type="bullet">
    ///   <item><see cref="AppearOnly"/>：只在折叠→可见时播一次（UserHint / ErrorSummary）。</item>
    ///   <item><see cref="ObjectSwitch"/>：折叠→可见、或**首行的对象标识真正切换**时播一次；
    ///         同一对象的后续刷新只换文本（CurrentObjectStatus 是每秒刷新的对象信息）。</item>
    ///   <item><see cref="FlowStatus"/>：流程/阶段语义句变化时播一次，带最小间隔节流（OperationalStatus）。</item>
    /// </list>
    /// </summary>
    private enum LineEntrancePolicy
    {
        AppearOnly,
        ObjectSwitch,
        FlowStatus,
    }

    /// <summary>同一元素两次入场位移之间的最小间隔（毫秒）：比它更密的文本变化只换文本、不重播位移。</summary>
    private const int MinLineEntranceIntervalMs = 600;

    /// <summary>单行动画状态：最小间隔节流 + 语义键（用于判定"对象是否真的切换"）。</summary>
    private sealed class LineMotionState
    {
        public long LastEntranceTicks;
        public string SemanticKey = string.Empty;
    }

    private readonly Dictionary<TextBlock, LineMotionState> _lineMotion = new();

    /// <summary>本卡订阅的通道属性（只有这四条会刷新本卡；进度类高频属性不订阅）。</summary>
    private static readonly string[] WatchedProperties =
    [
        nameof(MigrationSessionViewModel.OperationalStatus),
        nameof(MigrationSessionViewModel.UserHint),
        nameof(MigrationSessionViewModel.ErrorSummary),
        nameof(MigrationSessionViewModel.CurrentObjectStatus),
    ];

    public ShellHintCard()
    {
        InitializeComponent();
        // ★ 2026-10-08 ★ 这里**不**订阅 HintScroll.SizeChanged 去驱动 Height：
        //   那条链会形成 "内容变 → SizeChanged → 改 Height → 可用尺寸再变 → SizeChanged" 的自激。
        //   改成订阅**内容面板**的 SizeChanged（只作为"内容可能变了"的触发信号），
        //   真正的测量与写高被推迟到下一拍（RequestAdaptiveHeightUpdate）且只写一次终值。
        _uiQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        HintContentPanel.SizeChanged += (_, _) => RequestAdaptiveHeightUpdate();
        Loaded += (_, _) => RequestAdaptiveHeightUpdate();
        Apply(null);
    }

    /// <summary>接上会话（MainWindow 在会话就绪后调用一次；重复调用只换源，不叠加订阅）。</summary>
    public void Attach(MigrationSessionViewModel session)
    {
        if (ReferenceEquals(_session, session)) return;
        if (_session is not null) _session.PropertyChanged -= OnSessionPropertyChanged;
        _session = session;
        _session.PropertyChanged += OnSessionPropertyChanged;
        Apply(_session);
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 只处理通道属性：Percent/ProgressText 等每秒变化多次的属性一律忽略（避免无意义刷新）。
        foreach (var name in WatchedProperties)
        {
            if (string.Equals(name, e.PropertyName, StringComparison.Ordinal))
            {
                Apply(_session);
                return;
            }
        }
    }

    private void Apply(MigrationSessionViewModel? session)
    {
        var operational = session?.OperationalStatus ?? string.Empty;
        var currentObject = session?.CurrentObjectStatus ?? string.Empty;
        var userHint = session?.UserHint ?? string.Empty;
        var errorSummary = session?.ErrorSummary ?? string.Empty;

        SetLine(HintOperationalText, string.IsNullOrWhiteSpace(operational) ? "就绪" : operational,
            LineEntrancePolicy.FlowStatus);
        SetLine(HintObjectText, currentObject, LineEntrancePolicy.ObjectSwitch);
        SetLine(HintUserText, userHint, LineEntrancePolicy.AppearOnly);
        SetLine(HintErrorText, errorSummary, LineEntrancePolicy.AppearOnly);

        // 状态行：错误摘要通道决定它是"暂无报错"还是"有需要处理的项"（不是硬编码的假绿）。
        var hasError = !string.IsNullOrWhiteSpace(errorSummary);
        HintStatusText.Text = hasError ? "有需要处理的项（见上）" : "暂无报错";
        var brushKey = hasError ? "WarningBrush" : "SuccessBrush";
        if (Application.Current.Resources.TryGetValue(brushKey, out var brush) && brush is Brush b)
        {
            HintStatusIcon.Foreground = b;
        }

        // 四个通道的文本/可见性都投影完了 ⇒ 请求一次自适应高度重算（在下一拍统一测量 + 写高 + 播过渡）。
        RequestAdaptiveHeightUpdate();
    }

    // ────────── 2026-10-08 真机返修：提示卡自适应展开（用户指令 Q1–Q6）──────────

    /// <summary>
    /// ★ 2026-10-08（用户指令 §3：提示卡自适应展开）★ 下发"可用高度上界"。
    ///
    /// 语义变化（相对 2026-10-05 的"固定尺寸"口径）：
    ///   · 旧口径：高度恒为 token 176，内容超了就用 <c>MaxLines + CharacterEllipsis</c> 丢字（用户已判为缺陷）；
    ///   · 新口径：176 是**下界/默认高**，上界 = 本方法的参数（侧栏高 − Step4 导航高 − 标准段间距 12），
    ///     真实高度 = Clamp(内容自然所需高, 176, 上界)，由 <see cref="ApplyAdaptiveHeight"/> 一次性写入。
    ///
    /// 上界的来源与几何含义（保证 Q4「绝不覆盖 Step4 / 底栏 / 其它 Shell UI」）：
    ///   调用方 <c>MainWindow.UpdateHintCardHeight</c> 传入 `侧栏高 − StepNav(Step4「结果与校验」导航) 高 − 12`，
    ///   而 12 正是本卡自身 <c>Margin="0,12,0,0"</c>。因此即便卡片顶到上界，
    ///   顶边也只是贴在 Step4 底部下方一个标准段间距处 —— 不可能越过 Step4。
    ///
    /// 传 0 / NaN / ±∞ / 负数 = "尚未测量到可用空间" ⇒ 上界视为无穷（只用 176 下界），
    /// 装配早期绝不把卡片压成 0 高。
    /// 本方法**只记录上界并请求一次重算**，不直接写 <c>Height</c>：高度始终只有
    /// <see cref="ApplyAdaptiveHeight"/> 一个写入点。
    /// </summary>
    public void SetAvailableHeight(double availableHeight)
    {
        // 未测量（NaN/∞/≤0）⇒ 上界记为无穷：卡片可以按内容长，但仍有 176 下界。
        var measured = !double.IsNaN(availableHeight) && !double.IsInfinity(availableHeight) && availableHeight > 0d;
        _availableHeight = measured ? availableHeight : double.MaxValue;
        RequestAdaptiveHeightUpdate();
    }

    /// <summary>把"测量 + 写高 + 播动画"推迟到下一拍：避免在布局回合内 Measure 造成的递归布局。</summary>
    private void RequestAdaptiveHeightUpdate()
    {
        if (_heightUpdatePending) return;

        var queue = _uiQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            // 极早期（关联对象尚未就绪）：本拍直接算一次，下一拍由 Loaded / SizeChanged 再来。
            ApplyAdaptiveHeight();
            return;
        }

        _uiQueue = queue;
        _heightUpdatePending = true;
        if (!queue.TryEnqueue(() =>
            {
                _heightUpdatePending = false;
                ApplyAdaptiveHeight();
            }))
        {
            _heightUpdatePending = false;
            ApplyAdaptiveHeight();
        }
    }

    /// <summary>
    /// 高度唯一写入点。算法（Q1–Q5）：
    /// <code>
    ///   chrome   = 卡片实测高 − 正文实测高（标题/状态/分隔线/署名/Padding；测不到退 96）
    ///   desired  = chrome + 正文在"当前容器宽"下的自然高（不限行、不省略）
    ///   min      = ResolveFixedHeight()               // = 176（PCMigHintCardHeight token）
    ///   ceiling  = Clamp(_availableHeight, min, +∞)   // 未测量时为 +∞
    ///   target   = Clamp(desired, min, ceiling)
    /// </code>
    /// 关键纪律（用户明确禁止的两种做法都不出现）：
    ///   · **不是**逐帧 <c>Height += x</c>：这里只在"内容需要的高度真的变了"时写**一次**终值；
    ///   · **不是** UI 线程逐帧手算：写完之后所有视觉过渡都交给
    ///     <see cref="MotionDirector.PlayHintCardReveal"/> / <see cref="MotionDirector.PlayHintCardCollapse"/>
    ///     在 Composition 层完成（Clip 揭示 + 正文 opacity/translation），不触发 Measure/Arrange。
    /// 抖动抑制（Q1）：差值 &lt; <see cref="HeightChangeThreshold"/> 时**完全不动**，
    /// 因此"多一行字"绝不会引起小幅高度变化。
    /// </summary>
    private void ApplyAdaptiveHeight()
    {
        var min = ResolveFixedHeight();
        var ceiling = _availableHeight < min ? min : _availableHeight;
        var target = Clamp(MeasureDesiredSurfaceHeight(), min, ceiling);

        // 判据基准与"动画基数"同源：收缩在途时布局还停在旧高（高度是收尾才写的），基准只能取 ActualHeight；
        // 其余情况以最后一次目标高为基准。
        var actual = HintCardSurface.ActualHeight;
        var current = _surfaceTransitionActive && actual > 0d
            ? actual
            : (_targetSurfaceHeight > 0d ? _targetSurfaceHeight : actual);

        // 内容变化小于阈值就完全不动作（不能每出现一行字就小幅变高）。
        if (current > 0d && Math.Abs(target - current) < HeightChangeThreshold) return;

        var oldHeight = current > 0d ? current : target;

        // 收缩在途时的新收缩请求：就地合并到最新目标（收尾写高时读的就是它），不再叠一段动画。
        if (_surfaceTransitionActive && target < oldHeight)
        {
            _targetSurfaceHeight = target;
            return;
        }

        _pendingMotionOldHeight = oldHeight;
        _targetSurfaceHeight = target;

        // ★ 写入时机（本次返修）★
        //   增长：先把布局写到位，动画只负责把"多出来的顶部"揭示出来（掩码能掩盖变大）。
        //   收缩：**不在这里写**，交给动画收尾 —— 掩码不能重建已经消失的像素，先写矮会让掩码算错、
        //        动画终点只剩底部一条，收尾摘掩码时整卡弹回（这就是真机看到的"闪动"）。
        var shrinking = target < oldHeight;
        if (!shrinking || !IsLoaded || HintCardSurface.XamlRoot is null)
        {
            ApplySurfaceHeight(target);
        }

        if (IsLoaded) StartPendingMotion();
    }

    /// <summary>
    /// <b>唯一的高度写入点</b>：全文件只允许出现这一处给卡片写高度，且必须由
    /// <see cref="ApplyAdaptiveHeight"/>（立即写）或动画收尾回调（收缩时延迟写）调用。
    /// 冻结的源码契约测试（<c>ShellHintCardMotionContractTests</c> / <c>ShellHintCardLayoutTests</c>）锁定了
    /// "写入语句恰好一处"，所以任何新路径都必须走这里，绝不再复制一句写入到别处。
    /// </summary>
    private void ApplySurfaceHeight(double target)
    {
        HintCardSurface.Height = target;
        _surfaceTransitionActive = false;
    }

    /// <summary>播放本拍记录下来的展开/缩回视觉过渡（Reduced Motion 时 MotionDirector 内部直接落终态）。</summary>
    private void StartPendingMotion()
    {
        // 卡片尚未入树时没有 Composition Visual，直接跳过（视觉无过渡而已）。
        if (HintCardSurface.XamlRoot is null) return;

        // 动画基数与判据同源：都取"目标高"，绝不再读 ActualHeight（它要等布局跑完才更新）。
        var oldHeight = _pendingMotionOldHeight;
        var newHeight = _targetSurfaceHeight;
        if (double.IsNaN(newHeight) || double.IsInfinity(newHeight) || newHeight <= 0d) return;

        var delta = newHeight - oldHeight;
        if (Math.Abs(delta) < HeightChangeThreshold)
        {
            ApplySurfaceHeight(newHeight);
            return;
        }

        if (delta > 0d)
        {
            // 增长：高度已由 ApplyAdaptiveHeight 写到位，这里只负责揭示动画。
            MotionDirector.PlayHintCardReveal(HintCardSurface, oldHeight, newHeight, HintContentPanel);
        }
        else
        {
            // 收缩：高度**交给动画收尾写**（applyTargetHeight 回调读最新目标，因此途中合并也不会写错）。
            _surfaceTransitionActive = true;
            MotionDirector.PlayHintCardCollapse(HintCardSurface, oldHeight, newHeight, HintContentPanel,
                applyTargetHeight: () => ApplySurfaceHeight(_targetSurfaceHeight));
        }
    }

    /// <summary>
    /// 正文自然所需高 + 固定区高。测量对象是 <see cref="HintContentPanel"/>（四个通道 TextBlock 的容器）：
    /// 以"当前容器宽"、不限高做一次同步 Measure，取 DesiredSize —— 这正是"不省略时要占多高"。
    /// 宽度优先用 <c>HintScroll.ActualWidth</c>（已扣除滚动条占位），避免"滚动条出现 → 宽度变 → 高度再变"
    /// 的来回振荡；宽度还测不到时用保守兜底宽度（量出的高度偏高 ⇒ 只会更保守，不会低估）。
    /// Measure 是同步纯测量，在布局回合之外（下一拍）调用，不会与框架 Arrange 冲突。
    /// </summary>
    private double MeasureDesiredSurfaceHeight()
    {
        var width = HintScroll.ActualWidth;
        if (width <= 1d) width = HintContentPanel.ActualWidth;
        if (width <= 1d) width = FallbackMeasureWidth;

        HintContentPanel.Measure(new Size(width, double.PositiveInfinity));
        var contentHeight = HintContentPanel.DesiredSize.Height;

        // 固定区 = 实测卡片高 − 实测正文视口高（标题/状态/分隔线/署名 + Padding）；测不到就用兜底。
        var chrome = HintCardSurface.ActualHeight - HintScroll.ActualHeight;
        if (chrome <= 0d || double.IsNaN(chrome)) chrome = FallbackChromeHeight;

        var desired = chrome + contentHeight;
        return double.IsNaN(desired) ? FallbackFixedSurfaceHeight : desired;
    }

    /// <summary>三值夹取（min ≤ 返回值 ≤ max）。</summary>
    private static double Clamp(double value, double min, double max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    /// <summary>默认/下界高（DIP）：与 XAML 的 <c>Height="{StaticResource PCMigHintCardHeight}"</c> 同一 token 来源。</summary>
    private static double ResolveFixedHeight()
    {
        try
        {
            if (Application.Current?.Resources is { } resources
                && resources.TryGetValue("PCMigHintCardHeight", out var value)
                && value is double height && height > 0d) return height;
        }
        catch { /* 资源不可用时用兜底常量，绝不让提示卡消失 */ }
        return FallbackFixedSurfaceHeight;
    }

    /// <summary>token 读取失败时的兜底（与 <c>Materials.xaml</c> 的 PCMigHintCardHeight 当前值保持一致）。</summary>
    private const double FallbackFixedSurfaceHeight = 176d;

    /// <summary>
    /// 固定区兜底高度（DIP）：标题行 + 状态行 + 分隔线 + 署名 + 上下 Padding。
    /// 正常路径用**实测**的 <c>HintCardSurface.ActualHeight − HintScroll.ActualHeight</c>，
    /// 只有实测尚不可用（首帧）才退回这个常量。
    /// </summary>
    private const double FallbackChromeHeight = 96d;

    /// <summary>正文自然高测量的兜底宽度（DIP）：宽度还测不到时用的保守值，量出的高度偏高 ⇒ 只会更保守。</summary>
    private const double FallbackMeasureWidth = 220d;

    /// <summary>
    /// 改变高度的最小差值（DIP）：小于它不动 —— 这是 Q1「不能每出现一行字就小幅度变化高度」的落点。
    /// 只有内容真的需要跨过一整行（或明显跨档）时才换高度。
    /// </summary>
    private const double HeightChangeThreshold = 1d;

    /// <summary>MainWindow 下发的"可用高度"上界（DIP）。未测量到时为 <see cref="double.MaxValue"/>。</summary>
    private double _availableHeight = double.MaxValue;

    /// <summary>下一拍是否已排队一次自适应高度重算（去重，防止同一帧排队多次）。</summary>
    private bool _heightUpdatePending;

    /// <summary>待播的缩回/展开动画的"旧高度"（<see cref="ApplyAdaptiveHeight"/> 写入，同拍由 <see cref="StartPendingMotion"/> 消费）。</summary>
    private double _pendingMotionOldHeight;

    /// <summary>
    /// 最近一次**目标高**（DIP）。判据基准必须与动画基数同源：写高之后 <c>ActualHeight</c> 要等本帧布局
    /// 跑完才更新，用它当基准会把"旧高"当新高 ⇒ 同一布局回合的两次请求以过期基数重播一遍完整动画（抽搐）。
    /// </summary>
    private double _targetSurfaceHeight;

    /// <summary>
    /// 收缩过渡在途（高度尚未写入，等动画收尾）：期间新的"再收缩"请求只就地更新 <see cref="_targetSurfaceHeight"/>，
    /// 不再叠一段新动画（动画途中重播 = 抽搐）。增长方向不需要它 —— 增长是立即写高的。
    /// </summary>
    private bool _surfaceTransitionActive;

    /// <summary>本控件的 UI 线程调度器（把"测量 + 写高 + 播动画"推迟到布局回合之外，避免递归布局）。</summary>
    private Microsoft.UI.Dispatching.DispatcherQueue? _uiQueue;

    /// <summary>
    /// 通道文本 → 行文本/可见性，并按**语义级策略**决定是否播一次入场动效。
    /// 纪律：不再把"字符串不同"直接等价为"应该播动画"——高频刷新（CurrentObjectStatus 每秒更新、
    /// 同一句话重复抵达）只换文本、不动位置；折叠时把残留的位移/透明度状态清干净，下次出现从终态开始。
    /// </summary>
    private void SetLine(TextBlock target, string? text, LineEntrancePolicy policy)
    {
        var next = text ?? string.Empty;
        var textChanged = !string.Equals(target.Text, next, StringComparison.Ordinal);
        if (textChanged) target.Text = next;

        var shouldShow = !string.IsNullOrWhiteSpace(next);
        var isShown = target.Visibility == Visibility.Visible;
        var becameVisible = shouldShow && !isShown;

        if (shouldShow != isShown)
        {
            target.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!shouldShow)
        {
            // 折叠即归零：绝不把 Translation/Opacity 的中间态留给下一次显示（否则下次"出现"是半透明的）。
            MotionDirector.ResetLineEntrance(target);
            GetLineState(target).LastEntranceTicks = 0;
            return;
        }

        if (!textChanged && !becameVisible) return;

        switch (policy)
        {
            case LineEntrancePolicy.AppearOnly:
                if (becameVisible) PlayLineEntrance(target);
                break;

            case LineEntrancePolicy.ObjectSwitch:
            {
                var state = GetLineState(target);
                var key = FirstLine(next);
                var switched = !string.Equals(state.SemanticKey, key, StringComparison.Ordinal);
                state.SemanticKey = key;
                if (becameVisible || switched) PlayLineEntrance(target);
                break;
            }

            case LineEntrancePolicy.FlowStatus:
                // 流程语义句：内容变了才播（textChanged 已在上面判定），并受最小间隔节流保护。
                PlayLineEntrance(target);
                break;
        }
    }

    /// <summary>
    /// 按最小间隔节流后播放一次单行入场（Translation + Opacity，全在 Composition 层）。
    /// 节流的理由：OperationalStatus 在真实传输中会随快照刷新（同一阶段里的动态句），
    /// 没有节流就会变成"每个采样周期重播一次 170 ms 位移" —— 那正是返修前看到的抖动来源。
    /// </summary>
    private void PlayLineEntrance(TextBlock target)
    {
        var state = GetLineState(target);
        var now = Environment.TickCount64;
        if (now - state.LastEntranceTicks < MinLineEntranceIntervalMs) return;
        state.LastEntranceTicks = now;
        MotionDirector.PlayLineEntrance(target);
    }

    private LineMotionState GetLineState(TextBlock target)
    {
        if (!_lineMotion.TryGetValue(target, out var state))
        {
            state = new LineMotionState();
            _lineMotion[target] = state;
        }
        return state;
    }

    /// <summary>取首行作为**对象语义键**：CurrentObjectStatus 的首行形如「当前对象 &lt;id&gt;」，
    /// 首行不变 ⇒ 还是同一个对象（路径/字节刷新不算新对象），首行变了 ⇒ 对象真的切换了。</summary>
    private static string FirstLine(string text)
    {
        var index = text.IndexOf('\n');
        if (index < 0) return text.Trim();
        return text[..index].TrimEnd('\r').Trim();
    }
}