using System;
using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PCMig.WinUI.Presentation;

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
///   2) 卡片高度回到 **XAML 自然布局**（Auto + MinHeight + MaxHeight），不再有
///      「ScrollViewer.SizeChanged → 动画 Height → 尺寸再变」的自激链，也不再出现
///      "上界 130 却把 MaxHeight 抬到 160"这种自我突破 ⇒ 内容装得下就没有滚动条。
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
        // ★ 返修 ★ 这里**不再**订阅 HintScroll.SizeChanged 去驱动 Height 动画：
        //   那条链会形成 "内容变 → SizeChanged → 动画 Height → 可用尺寸再变 → SizeChanged" 的自激，
        //   并让 ScrollViewer 反复重判是否出现滚动条。卡片高度现在完全交给 XAML 自然布局
        //   （Auto + MinHeight + MaxHeight），内容装得下就不会有滚动条。
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
    }

    // ────────── UI Closure 2026-10-05（用户指令 UI-07；返修后口径）──────────

    /// <summary>
    /// ★ UI-07（用户指令，返修后口径）★ 下发"卡片最大高度"。调用方（MainWindow.UpdateHintCardBounds）按
    /// 「侧栏高度 − 步骤导航高度 − 标准段间距（本卡 Margin.Top = 12）」算出，保证卡片顶边永远
    /// 落在 Step4（步骤导航）底部 + 标准段间距之下 ⇒ 不碰、不压、不穿 Step4。
    /// 传 0 / NaN / ±∞ / 负数表示"尚未测量到上界"⇒ 不设限（装配早期绝不把卡片压成 0 高）。
    ///
    /// **返修要点（真实安全上界 &gt; 理想最小高度）**：上界就是物理天花板，卡片的 <c>MaxHeight</c>
    /// 严格等于它，**不得**再被"下界 160"反向抬回 160 —— 真实可用空间只有 130 时把 MaxHeight 设成 160
    /// 等于自己突破自己（卡片顶进 Step4）。160 只作为**理想最小高度**：仅在空间足够时才生效
    /// （<c>MinHeight = min(160, ceiling)</c>），空间不足时宁可卡内滚动。
    /// </summary>
    public void SetMaxSurfaceHeight(double maxHeight)
    {
        if (double.IsNaN(maxHeight) || double.IsInfinity(maxHeight) || maxHeight <= 0d)
        {
            HintCardSurface.MaxHeight = double.PositiveInfinity;
            HintCardSurface.MinHeight = IdealMinSurfaceHeight;
            return;
        }

        var safeCeiling = maxHeight;                                  // 物理天花板：真实可用空间
        HintCardSurface.MaxHeight = safeCeiling;                       // 绝不抬高（旧实现 max(ceiling,160) 已废弃）
        HintCardSurface.MinHeight = Math.Min(IdealMinSurfaceHeight, safeCeiling);
        // 卡片高度保持 Auto：由内容与上面两个约束自然决定，不再用动画去"钉死"一个像素高度。
    }

    /// <summary>理想最小高度：保住"标题行 + 一行状态 + 署名行"（仅在真实空间足够时生效）。</summary>
    private const double IdealMinSurfaceHeight = 160d;

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