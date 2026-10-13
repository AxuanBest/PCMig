using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PCMig.Core.Util;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ P1-09 / R10 ★ 提示卡"30 次变化不闪 + 高度永不越界"的**行为**测试。
///
/// 与 <c>ShellHintCardMotionContractTests</c> / <c>ShellHintCardLayoutTests</c> 的分工：
///   · 那两份是**源码文本契约**（本机无头环境量不了 WinUI 布局，只能锁结构不许回退）；
///   · 这份跑的是 <see cref="HintCardMotionPolicy"/> —— 平台无关的纯决策函数，
///     因此可以真的把"同一目标连来 30 次""±1 DIP 抖动 30 次""收缩在途连续来 5 个更矮目标"
///     这些真机闪动的成因跑一遍，并断言**不变量**（不闪的判据）而不是断字面量。
///
/// 反闪动不变量（每条都对应一次真机返修结论）：
///   ① 同一目标重复抵达 ⇒ 只动一次、且不重播动画；
///   ② 小于阈值的抖动 ⇒ 一次都不动（"多一行字"绝不引起高度抖动）；
///   ③ 收缩在途的新收缩 ⇒ 只合并目标，绝不叠第二段动画；
///   ④ 两次动画之间必须已经落过一次高度（不存在"动画套动画"）；
///   ⑤ 高度永远落在 [176, 上界] 内，且最终收敛到最后一个目标；
///   ⑥ 没有可视树（未入树 / 无 XamlRoot）⇒ 只落终态，绝不播动画。
/// </summary>
public sealed class P1HintCardMotionTests
{
    private const double Min = 176d;        // = PCMigHintCardHeight token（下界）
    private const double Ceiling = 420d;    // 模拟 MainWindow 下发的上界
    private const double Threshold = 1d;    // = ShellHintCard.HeightChangeThreshold

    /// <summary>被测决策的唯一入口（与 ShellHintCard 调用签名保持一致）。</summary>
    private static HintCardMotionDecision Decide(double target, double current, bool collapseInFlight, bool canAnimate = true)
        => HintCardMotionPolicy.Decide(target, current, collapseInFlight, Threshold, canAnimate);

    /// <summary>
    /// 模拟卡片的执行侧：把决策翻译成"当前基准高 / 在途状态 / 写高次数 / 动画次数"。
    /// 与 ShellHintCard.ApplyAdaptiveHeight + ApplySurfaceHeight + StartPendingMotion 同语义。
    /// </summary>
    private sealed class Card
    {
        public double Height;              // 布局实际高（收缩在途时仍是旧高）
        public double Target;              // 最后一次目标高
        public bool CollapseInFlight;
        public int Writes;                 // 写高次数
        public int Motions;                // 起步的动画段数
        public readonly List<(HintCardMotionAction Action, int WritesBefore, int WritesAfter, int Motions)> Log = new();

        public void Feed(double desired, bool canAnimate = true)
        {
            var target = Math.Min(Math.Max(desired, Min), Ceiling);   // 与卡片同源的夹取
            var current = CollapseInFlight && Height > 0d ? Height : (Target > 0d ? Target : Height);
            var d = HintCardMotionPolicy.Decide(target, current, CollapseInFlight, Threshold, canAnimate);
            var writesBefore = Writes;

            switch (d.Action)
            {
                case HintCardMotionAction.None:
                    break;
                case HintCardMotionAction.MergeIntoInFlightCollapse:
                    Target = d.Target;
                    break;
                case HintCardMotionAction.SettleOnly:
                    Target = d.Target;
                    Height = d.Target;
                    CollapseInFlight = false;
                    Writes++;
                    break;
                case HintCardMotionAction.Reveal:
                    Target = d.Target;
                    Height = d.Target;
                    CollapseInFlight = false;
                    Writes++;
                    Motions++;
                    break;
                case HintCardMotionAction.Collapse:
                    Target = d.Target;
                    Height = d.OldHeight;      // 高度收尾才写
                    CollapseInFlight = true;
                    Motions++;
                    break;
            }

            Log.Add((d.Action, writesBefore, Writes, Motions));
        }

        /// <summary>动画收尾：写终态高（真机上由 applyTargetHeight 回调触发）。</summary>
        public void SettleCollapse()
        {
            if (!CollapseInFlight) return;
            Height = Target;
            CollapseInFlight = false;
            Writes++;
        }
    }

    // ── ① 同一目标连来 30 次 ─────────────────────────────────────────────────────
    [Fact]
    public void R10_同一目标连来三十次只动一次且不重播动画()
    {
        var card = new Card();
        for (var i = 0; i < 30; i++) card.Feed(260d);

        Assert.Equal(1, card.Writes);          // 首帧落一次终值，其余 29 次完全不动
        Assert.Equal(0, card.Motions);         // 首次出现不播动画（首帧播动画本身就是"闪一下"）
        Assert.Equal(260d, card.Height);
        Assert.Equal(29, card.Log.Count(l => l.Action == HintCardMotionAction.None));
    }

    // ── ② ±1 DIP 抖动：一次都不动 ───────────────────────────────────────────────
    [Fact]
    public void R10_三十次小幅抖动绝不改变高度()
    {
        var card = new Card();
        card.Feed(260d);
        var writesAfterFirst = card.Writes;
        var motionsAfterFirst = card.Motions;

        for (var i = 0; i < 30; i++) card.Feed(260d + (i % 2 == 0 ? 0.4d : -0.4d));

        Assert.Equal(writesAfterFirst, card.Writes);
        Assert.Equal(motionsAfterFirst, card.Motions);
        Assert.Equal(260d, card.Height);
        Assert.All(card.Log.Skip(1), l => Assert.Equal(HintCardMotionAction.None, l.Action));
    }

    // ── ③ 收缩在途：只合并，绝不叠动画 ──────────────────────────────────────────
    [Fact]
    public void R10_收缩在途连来五个更矮目标只合并不叠动画()
    {
        var card = new Card();
        card.Feed(400d);                 // 先到 400
        card.Feed(300d);                 // 收缩开始
        Assert.True(card.CollapseInFlight);

        var motionsBefore = card.Motions;
        foreach (var desired in new[] { 290d, 260d, 220d, 200d, 176d })
        {
            card.Feed(desired);
            Assert.Equal(HintCardMotionAction.MergeIntoInFlightCollapse, card.Log[^1].Action);
        }

        Assert.Equal(motionsBefore, card.Motions);   // 途中一段动画都没新起
        Assert.Equal(176d, card.Target);             // 目标已并到最新
        Assert.Equal(400d, card.Height);             // 高度仍未写（等收尾）

        card.SettleCollapse();
        Assert.Equal(176d, card.Height);             // 收尾只写一次，一次到位
    }

    // ── ④ 两次动画之间必须落过一次高度（不存在动画套动画）────────────────────────
    [Fact]
    public void R10_两次动画之间必须已经写过高()
    {
        var card = new Card();
        // 反复"变高 → 变矮"，中途不主动收尾（让卡片自己按语义处理）。
        foreach (var desired in new[] { 400d, 200d, 400d, 200d, 400d, 200d })
        {
            card.Feed(desired);
            card.SettleCollapse();
        }

        var motionEvents = card.Log
            .Where(l => l.Action is HintCardMotionAction.Reveal or HintCardMotionAction.Collapse)
            .ToList();

        Assert.True(motionEvents.Count >= 4, "本次序列应当真的起了多段动画");
        for (var i = 1; i < motionEvents.Count; i++)
        {
            Assert.Equal(motionEvents[i - 1].Motions + 1, motionEvents[i].Motions);   // 每次只新起一段
            if (motionEvents[i - 1].Action == HintCardMotionAction.Collapse)
            {
                // 收缩是"收尾才写高"的动画：它落定之前绝不允许下一段动画起手。
                var writesInBetween = motionEvents[i].WritesBefore - motionEvents[i - 1].WritesAfter;
                Assert.True(writesInBetween >= 1,
                    "第 " + i + " 段动画起手时上一段收缩还没落高（期间写高次数 " + writesInBetween + "）——这就是动画套动画的闪动");
            }
        }
        Assert.Equal(card.Target, card.Height);
    }

    // ── ⑤ 高度永不越界 + 收敛到最后一个目标 ────────────────────────────────────
    [Fact]
    public void R10_三十次变化的高度永不越界且收敛到最后目标()
    {
        var card = new Card();
        var desiredSequence = new List<double>();
        // 30 步：越界请求（要把卡片压成 0 / 撑到无穷）+ 真实档位 + 抖动。
        for (var i = 0; i < 30; i++)
        {
            desiredSequence.Add((i % 5) switch
            {
                0 => 40d,                                        // 远低于下界
                1 => 1_000d,                                     // 远高于上界
                2 => 300d,
                3 => 176d,
                _ => 260d + (i % 2 == 0 ? 0.3d : -0.3d),         // 抖动
            });
        }

        foreach (var desired in desiredSequence)
        {
            card.Feed(desired);
            card.SettleCollapse();
            Assert.InRange(card.Height, Min, Ceiling);
            Assert.InRange(card.Target, Min, Ceiling);
        }

        Assert.Equal(Math.Min(Math.Max(desiredSequence[^1], Min), Ceiling), card.Height);
        Assert.True(card.Writes <= card.Log.Count(l => l.Action != HintCardMotionAction.None),
            "写高次数不得超过「需要动」的决策次数");
    }

    // ── ⑥ 没有可视树 ⇒ 只落终态 ────────────────────────────────────────────────
    [Fact]
    public void R10_没有可视树时只落终态绝不播动画()
    {
        var grow = Decide(300d, 200d, collapseInFlight: false, canAnimate: false);
        Assert.Equal(HintCardMotionAction.SettleOnly, grow.Action);
        Assert.True(grow.WritesHeightNow);
        Assert.False(grow.PlaysMotion);

        var shrink = Decide(180d, 300d, collapseInFlight: false, canAnimate: false);
        Assert.Equal(HintCardMotionAction.SettleOnly, shrink.Action);
        Assert.Equal(180d, shrink.Target);
    }

    // ── ⑦ 方向决定动作，二者永不互串 ──────────────────────────────────────────
    [Fact]
    public void R10_方向决定动作且永不同时出现()
    {
        var up = Decide(320d, 240d, collapseInFlight: false);
        Assert.Equal(HintCardMotionAction.Reveal, up.Action);
        Assert.True(up.OldHeight < up.Target);

        var down = Decide(240d, 320d, collapseInFlight: false);
        Assert.Equal(HintCardMotionAction.Collapse, down.Action);
        Assert.True(down.OldHeight > down.Target);
        Assert.False(down.WritesHeightNow);         // 收缩：本拍绝不写高
        Assert.False(up.Action == down.Action);
    }

    // ── ⑧ 无效目标 ⇒ 什么都不做 ────────────────────────────────────────────────
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0d)]
    [InlineData(-5d)]
    public void R10_无效目标什么都不做(double target)
    {
        var d = Decide(target, 240d, collapseInFlight: false);
        Assert.Equal(HintCardMotionAction.None, d.Action);
        Assert.False(d.WritesHeightNow);
        Assert.False(d.PlaysMotion);
        Assert.Equal(0d, d.Target);
    }

    // ── ⑨ 首帧：没量到过高就只写一次终值，不播动画 ─────────────────────────────
    [Fact]
    public void R10_首帧只落终值不播动画()
    {
        var d = Decide(300d, 0d, collapseInFlight: false);
        Assert.Equal(HintCardMotionAction.SettleOnly, d.Action);
        Assert.Equal(300d, d.Target);
        Assert.Equal(300d, d.OldHeight);   // 基数=目标 ⇒ 差值 0 ⇒ 不会"从 0 长出来"
    }

    // ── ⑩ 接线契约：UI 层只按决策分派，决策逻辑只有一处 ────────────────────────
    [Fact]
    public void R10_界面层只按决策分派且决策逻辑唯一()
    {
        var card = ReadRepoFile("src", "PCMig.WinUI", "Views", "ShellHintCard.xaml.cs");

        Assert.Contains("HintCardMotionPolicy.Decide(", card);
        Assert.Contains("case HintCardMotionAction.None:", card);
        Assert.Contains("case HintCardMotionAction.MergeIntoInFlightCollapse:", card);
        Assert.Contains("if (decision.WritesHeightNow) ApplySurfaceHeight(decision.Target);", card);
        Assert.Contains("if (decision.PlaysMotion) StartPendingMotion();", card);

        // 旧的内联方向判定不许回来（那是"两处真值"的开始）。
        Assert.DoesNotContain("var shrinking = target < oldHeight;", card);
        Assert.DoesNotContain("if (_surfaceTransitionActive && target < oldHeight)", card);
        Assert.DoesNotContain("if (!shrinking || !IsLoaded", card);

        // 决策函数必须**平台无关**（否则无头测试根本跑不了它，等于又回到只能断源码文本）。
        // ★ 先剥注释再断言 ★ P1-08 教训：注释里描述"什么叫 XamlRoot 可用"不算依赖 WinUI。
        var policy = ReadRepoFile("src", "PCMig.Core", "Util", "HintCardMotionPolicy.cs");
        var policyCode = StripComments(policy);
        Assert.DoesNotContain("Microsoft.UI", policyCode);
        Assert.DoesNotContain("Windows.Foundation", policyCode);
        Assert.DoesNotContain("XamlRoot", policyCode);
        Assert.DoesNotContain("DispatcherQueue", policyCode);
        Assert.Contains("public static HintCardMotionDecision Decide(", policy);
    }

    // ── 辅助 ────────────────────────────────────────────────────────────────────
    /// <summary>
    /// 剥掉行注释（含 XML 文档注释）后再断言。★ P1-08 教训 ★：注释里记录历史文案/解释平台概念
    /// 不是违规，直接对原文断言会把"如实记录"误判成缺陷。
    /// </summary>
    private static string StripComments(string code)
        => string.Join("\n", code.Split('\n').Select(line =>
        {
            var i = line.IndexOf("//", StringComparison.Ordinal);
            return i >= 0 ? line[..i] : line;
        }));

    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln")))
            {
                var path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
                Assert.True(File.Exists(path), "缺少文件：" + path);
                return File.ReadAllText(path);
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到仓库根");
    }
}