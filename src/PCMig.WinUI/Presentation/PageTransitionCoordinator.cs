using System;
using Microsoft.UI.Xaml;

namespace PCMig.WinUI.Presentation;

/// <summary>
/// 四页导航的**纯 Presentation 过渡协调器**。
///
/// U57 口径：过渡必须是 **Full-Viewport Vertical Push**（整页纵向推入）——
///   · 旧页与新页在动画期间同时存在，像两张完整页面上下拼接；
///   · Forward：旧页 Y 0 → −视口高、新页 Y +视口高 → 0；Backward 反向；
///   · 中间两页各占视口一半 ⇒ 中界线清晰可见；主体是整页位移，**不做 Fade**；
///   · 位移严格限制在 Workspace 内容视口内（由 PageViewport 的 Clip 保证）。
///
/// U58 修复（用户实测：点得快时两页重叠 / 有时一片空白）—— 三个根因，逐条封死：
///   1. **收尾跨代打断**：旧一代 Push 的收尾（ScopedBatch.Completed / 定时兜底）会无条件把
///      incoming 的 Translation 归零并 StopAnimation；若新一代已经接管该元素，就会当场打断新动画，
///      页面停在视口外（空白）或停在半途（与另一页重叠）。
///      ⇒ 引入**代次**：收尾回调只有在"自己仍是最新一次过渡"时才允许碰元素。
///   2. **残留的过渡期可见性**：上一代可能把某页强制设回 Visible，而只靠 x:Bind 不足以保证它一定
///      被折叠。⇒ 每次导航都**把非目标页强制收干净**（折叠 + 吸附终态），不依赖绑定。
///   3. **定时兜底被 GC 回收**：`DispatcherQueueTimer` 没有强引用会被回收，兜底永不触发。
///      ⇒ 在 <see cref="MotionDirector"/> 里持有强引用列表。
///
/// 另外：防重入窗口（MainWindow 的 MotionWindow）已与整页 Push 时长对齐；落在窗口内的连续导航
/// 走"不播中间态、只吸附终态"的静默路径 —— 快速连点时表现为干净的直接切换，绝不重叠。
///
/// 纪律：只动 <c>Visibility</c> / Composition 的 Translation；**绝不**创建、销毁或重建任何页面 /
/// ViewModel / 绑定 —— 本类没有任何"重建"能力。
/// </summary>
internal sealed class PageTransitionCoordinator
{
    private readonly Func<StepKind, FrameworkElement?> _pageForStep;
    private readonly Func<double> _viewportHeight;

    /// <summary>过渡代次：每次导航 +1；旧代的收尾回调据此放弃对元素的操作权。</summary>
    private int _generation;

    /// <summary>当前正在播 Push 的"旧页"；被更新的导航接管时在这里被立即吸附并折叠。</summary>
    private FrameworkElement? _pendingOutgoing;

    public PageTransitionCoordinator(Func<StepKind, FrameworkElement?> pageForStep, Func<double> viewportHeight)
    {
        _pageForStep = pageForStep ?? throw new ArgumentNullException(nameof(pageForStep));
        _viewportHeight = viewportHeight ?? throw new ArgumentNullException(nameof(viewportHeight));
    }

    /// <summary>在 <c>Nav.Changed</c>（页面可见性已按导航状态刷新之后）调用。</summary>
    /// <param name="from">旧页</param>
    /// <param name="to">新页</param>
    /// <param name="direction">+1 前进 / -1 后退（跨级导航同样只播一遍完整 Push）</param>
    /// <param name="allowAnimation">
    /// 历史参数，**U59 起不再用于"抑制动画"**：早先用防重入窗口抑制快速连点的动画，代价是
    /// "点得快时动画直接消失"（用户实测反馈）。现在改为**接管式过渡** —— 每一段 Push 都从页面
    /// 当前实际位置接续（见 <see cref="MotionDirector.PlayPagePush"/>），因此任何速度下动画都在跑。
    /// 保留该参数只为将来可能的"完全禁用动效"开关。
    /// </param>
    public void OnNavigated(StepKind from, StepKind to, int direction, bool allowAnimation)
    {
        // 本代编号：必须在做任何事之前取，回调里据它判断自己是否仍是"最新一代"。
        var generation = ++_generation;
        _pendingOutgoing = null;   // 旧代从此失去操作权（旧回调会因代次不匹配直接返回）

        var incoming = _pageForStep(to);
        if (incoming is null) return;
        var outgoing = _pageForStep(from);
        var height = _viewportHeight();

        if (outgoing is null || ReferenceEquals(outgoing, incoming) || !allowAnimation
            || !MotionDirector.SystemAnimationsEnabled || height <= 1.0)
        {
            // 静默路径：不播中间态。把两页吸附终态，并把**除目标页以外的所有页面强制折叠** ——
            // 只靠 x:Bind 不足以保证上一代强制 Visible 过的页面一定被折叠（会重叠）。
            MotionDirector.ResetTransitionState(outgoing);
            MotionDirector.ResetTransitionState(incoming);
            CollapseAllExcept(to, alsoKeepVisible: null);
            return;
        }

        // 过渡期把旧页压回可见（绑定认为它该折叠；动画结束我们再折叠回来）。
        outgoing.Visibility = Visibility.Visible;
        _pendingOutgoing = outgoing;
        CollapseAllExcept(to, alsoKeepVisible: outgoing);

        MotionDirector.PlayPagePush(outgoing, incoming, direction, height, () =>
        {
            // U58：**代次校验**。旧代的收尾绝不允许再碰元素 —— 否则会打断新一代已接管的动画。
            if (generation != _generation) return;

            MotionDirector.ResetTransitionState(outgoing);
            MotionDirector.ResetTransitionState(incoming);
            outgoing.Visibility = Visibility.Collapsed;
            _pendingOutgoing = null;
        });
    }

    /// <summary>
    /// 把"除 <paramref name="keepKind"/> 与 <paramref name="alsoKeepVisible"/> 之外"的页面一律折叠并
    /// 吸附终态。这是防重叠的**兜底闸门**：任何来源的残留可见性都会在每次导航时被收干净。
    /// </summary>
    private void CollapseAllExcept(StepKind keepKind, FrameworkElement? alsoKeepVisible)
    {
        foreach (StepKind kind in Enum.GetValues(typeof(StepKind)))
        {
            if (kind == keepKind) continue;
            var page = _pageForStep(kind);
            if (page is null || ReferenceEquals(page, alsoKeepVisible)) continue;
            if (page.Visibility != Visibility.Visible) continue;
            MotionDirector.ResetTransitionState(page);
            page.Visibility = Visibility.Collapsed;
        }
    }
}