namespace PCMig.Core.Util;

/// <summary>
/// 提示卡（ShellHintCard）本拍应该做什么：**唯一的高度/动效决策点**。
///
/// 为什么把它从 WinUI 的 code-behind 里抽出来（P1-09 / R10）：
///   · 真机上"提示卡抽搐 / 闪动 / 动画播不完整"的全部根因都是**决策**层面的竞争
///     （收缩在途又来了新请求、同一布局回合以过期基数重播、没有可视树却要播动画……），
///     而不是 Composition 动画本身；
///   · WinUI 3 无法在无头环境量布局，所以"30 次变化不闪"这类要求过去只能靠源码文本断言。
///     决策逻辑一旦是**平台无关的纯函数**，就可以在测试里真的把 30 次变化跑一遍：
///     证明"同一目标连来 30 次只动一次""±1 DIP 抖动一次都不动""收缩在途只合并不叠动画"。
///
/// 纪律（与 ShellHintCard 的冻结契约同源，任何一条被破坏都会在真机上表现为闪动）：
///   1) **没有可视树就没有动画**：<c>canAnimate=false</c> 时一律落终态（SettleOnly）；
///   2) **收缩在途只合并**：新目标仍然更矮时只更新目标（MergeIntoInFlightCollapse），绝不叠第二段动画；
///   3) **只在真的需要跨过一整行时才动**：差值小于 <paramref name="threshold"/> 一律 None；
///   4) **方向决定动作**：变高才 Reveal、变矮才 Collapse，二者永不互串。
/// </summary>
public enum HintCardMotionAction
{
    /// <summary>什么都不做（差值过小、目标无效、或已在该高度）。</summary>
    None,

    /// <summary>收缩过渡在途，本次请求只是把目标并到最新值（不写高、不播第二段动画）。</summary>
    MergeIntoInFlightCollapse,

    /// <summary>直接写终态高、不播动效（无可视树、或差值不足以成动画）。</summary>
    SettleOnly,

    /// <summary>高度先写到位，再播"揭示"过渡（变高）。</summary>
    Reveal,

    /// <summary>高度**交给动画收尾再写**，本拍只播"缩回"过渡（变矮）。</summary>
    Collapse,
}

/// <summary>一次决策的结果：做什么、以什么为动画基数、目标是多高。</summary>
public readonly record struct HintCardMotionDecision(HintCardMotionAction Action, double OldHeight, double Target)
{
    /// <summary>本拍是否需要写高度（Collapse 是收尾回调里写，所以不算"本拍写"）。</summary>
    public bool WritesHeightNow => Action is HintCardMotionAction.SettleOnly or HintCardMotionAction.Reveal;

    /// <summary>本拍是否需要播一段过渡动画。</summary>
    public bool PlaysMotion => Action is HintCardMotionAction.Reveal or HintCardMotionAction.Collapse;
}

/// <summary>
/// 纯决策函数（无 WinUI 依赖，因此可被无头测试反复驱动）。
/// 调用方负责：测量目标高、提供"当前基准高"、告知收缩是否在途。
/// </summary>
public static class HintCardMotionPolicy
{
    /// <summary>
    /// 决定本拍的动作。
    /// </summary>
    /// <param name="target">本拍算出的目标高（DIP，调用方已夹到 [下界, 上界]）。</param>
    /// <param name="current">
    /// 判据基准高（DIP）：收缩在途时是 <c>ActualHeight</c>（布局还停在旧高），其余情况是"最后一次目标高"。
    /// ≤0 表示还没量到过高度（首帧）。
    /// </param>
    /// <param name="collapseInFlight">是否有一段"缩回"过渡尚未收尾（高度还没写下去）。</param>
    /// <param name="threshold">成动画的最小差值（DIP）。</param>
    /// <param name="canAnimate">是否具备播放动画的条件（已入树且能取到 XamlRoot）。</param>
    public static HintCardMotionDecision Decide(
        double target, double current, bool collapseInFlight, double threshold, bool canAnimate)
    {
        if (double.IsNaN(target) || double.IsInfinity(target) || target <= 0d)
        {
            return new HintCardMotionDecision(HintCardMotionAction.None, 0d, 0d);
        }

        // 规则 3：差值过小 ⇒ 完全不动（"多一行字"绝不允许引起高度抖动）。
        if (current > 0d && Math.Abs(target - current) < threshold)
        {
            return new HintCardMotionDecision(HintCardMotionAction.None, current, current);
        }

        var oldHeight = current > 0d ? current : target;   // 首帧：基数就是目标（等于"只写一次终值"）

        // 规则 2：收缩在途 + 这次还是更矮 ⇒ 只合并目标，绝不叠动画。
        if (collapseInFlight && target < oldHeight)
        {
            return new HintCardMotionDecision(HintCardMotionAction.MergeIntoInFlightCollapse, oldHeight, target);
        }

        var delta = target - oldHeight;

        // 规则 1：没有可视树 ⇒ 只落终态；差值不足以成动画 ⇒ 同样只落终态。
        if (!canAnimate || Math.Abs(delta) < threshold)
        {
            return new HintCardMotionDecision(HintCardMotionAction.SettleOnly, oldHeight, target);
        }

        // 规则 4：方向决定动作（变高 = 先写高再揭示；变矮 = 收尾再写高）。
        return delta > 0d
            ? new HintCardMotionDecision(HintCardMotionAction.Reveal, oldHeight, target)
            : new HintCardMotionDecision(HintCardMotionAction.Collapse, oldHeight, target);
    }
}