using System;
using System.Collections.Generic;
using PCMig.WinUI.Presentation;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ Round-2 2026-10-05（§3.2 / §3.3 / §3.4 / §3.6）★ 进度**呈现**时间线的行为契约。
///
/// 为什么需要这组测试：用户的最新视频证明"代码里调用了 StartAnimation + distinctEdges > N"并不等于
/// "用户看到的是连续推进" —— 真值离散地大跳（24.8% → 47.3% → 62.3%）时，旧的固定 0.40 s 补间只会
/// 让条"短促地追一下"，人眼仍是一帧一帧跳。这里断言的是**可被人眼感知的性质**：
///   ① 视觉只落后于已确认真值、永不超出（不预测、不外推、不自爬）；
///   ② 大台阶按 delta 拉长补间（25% 跳变 ≈0.8 s、47% 跳变封顶 1.2 s）；
///   ③ 落后期间**大多数采样帧都在移动**，而不是"动一下、冻一段"；
///   ④ 中途到达新目标时从**当前视觉位置**接管（既不 snap 也不从旧目标重启）；
///   ⑤ 暂停 / 停止冻结视觉且不再推进；⑥ 只有引擎真完成才收敛到 100%。
/// </summary>
public class ProgressPresentationCoordinatorTests
{
    /// <summary>与用户真机同一量级：176.9 GB。</summary>
    private static readonly long Planned = 176_900_000_000L;

    private static long BytesFor(double percent) => (long)Math.Round(Planned * percent / 100d);

    private static DateTime T0 => new(2026, 10, 5, 3, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void RealVideoTargetSequence_IsSmoothedWithoutPrediction()
    {
        // 用户视频里的真值序列：0 → 24.8%（≈52.0 s）→ 47.3%（≈52.2 s）→ 62.3%（≈54.9 s）
        var c = new ProgressPresentationCoordinator();
        c.Reset("JOB-video", "test");
        var t0 = T0;

        c.ApplyTruth(0d, 0L, Planned, settled: false, jobId: "JOB-video", utcNow: t0);
        Assert.Equal(0d, c.Advance(t0), 3);

        c.ApplyTruth(24.8d, BytesFor(24.8d), Planned, false, "JOB-video", t0.AddMilliseconds(200));
        c.ApplyTruth(47.3d, BytesFor(47.3d), Planned, false, "JOB-video", t0.AddMilliseconds(400));

        // ★ Round-3 PHASE B ★ 采样步长改为**渲染帧**（16 ms ≈ 60 Hz）：新实现是连续指数滤波器，
        //   dt 被夹到 1/30 s，按 80 ms 采样等于每次只走 1/3 帧的时间 ⇒ 会人为测出"落后"，不再是真机行为。
        var samples = new List<double>();
        var confirmed = new List<double>();
        for (var ms = 400; ms <= 4200; ms += 16)
        {
            var now = t0.AddMilliseconds(ms);
            var confirmedNow = 47.3d;
            if (ms == 3200)
            {
                c.ApplyTruth(62.3d, BytesFor(62.3d), Planned, false, "JOB-video", now);
                confirmedNow = 62.3d;
            }
            var v = c.Advance(now);
            samples.Add(v);
            confirmed.Add(confirmedNow);
            Assert.True(v <= 62.3d + 1e-6, $"视觉 {v:0.###}% 超出已确认真值 62.3%（出现预测）");
        }

        for (var i = 1; i < samples.Count; i++)
            Assert.True(samples[i] >= samples[i - 1] - 1e-6,
                $"第 {i} 个采样出现视觉回退：{samples[i - 1]:0.###} → {samples[i]:0.###}");

        for (var i = 1; i < samples.Count; i++)
            Assert.True(samples[i] - samples[i - 1] <= 15d,
                $"单帧跳变 {samples[i] - samples[i - 1]:0.###} 点 > 15 点（人眼会看成跳帧）");

        // 判据（§3.6）：**只有当视觉仍落后于已知真值时**才要求"大多数采样帧都在动"；
        // 追平之后允许停住 —— 绝不许为了凑帧数而伪造运动（那是假进度）。
        var behind = 0;
        var behindMoving = 0;
        for (var i = 1; i < samples.Count; i++)
        {
            if (confirmed[i] - samples[i] > 0.05d)
            {
                behind++;
                if (samples[i] - samples[i - 1] > 1e-6) behindMoving++;
            }
        }
        Assert.True(behind > 10, $"本序列本应存在明显的追赶窗口，实际只有 {behind} 个落后帧（测试失去意义）");
        Assert.True(behindMoving >= (int)Math.Ceiling(behind * 0.9),
            $"落后帧 {behind} 个，但只有 {behindMoving} 个在移动 —— 仍是\"动一下冻一段\"，不是连续推进");

        Assert.Equal(62.3d, samples[^1], 1);
    }

    /// <summary>
    /// ★ Round-3 PHASE B（§15）★ 目标改变**不得重启速度** —— 这是"抽动"的根源判据。
    /// 旧实现每来一个目标就重算一次时长并从当前视觉重新插值：位置连续但**速度在目标改变处出现断点**
    /// （先快后慢、甚至先停一下再动）。新实现只改目标，逐帧步长必须保持连续：
    /// 目标变大 ⇒ 步长变大（差距变大）；目标只小幅前进 ⇒ 步长不得塌到 0 附近再重新爬。
    /// </summary>
    [Fact]
    public void TargetChangeDoesNotRestartMotionSpeed()
    {
        var c = new ProgressPresentationCoordinator();
        c.Reset("JOB-1", "test");
        var t0 = T0;
        const double frameMs = 16d;

        c.ApplyTruth(20d, BytesFor(20d), Planned, false, "JOB-1", t0);

        // 先跑 20 帧，让视觉在追赶中（速度处于"加速段"）
        var prev = c.Advance(t0);
        var steps = new List<double>();
        for (var f = 1; f <= 20; f++)
        {
            var v = c.Advance(t0.AddMilliseconds(f * frameMs));
            steps.Add(v - prev);
            prev = v;
        }
        var stepBefore = steps[^1];
        Assert.True(stepBefore > 0d, "追赶期间每帧都必须在动（否则就是伪造的静止）");

        // 目标在运动中途变化：+18 点
        var tSwitch = t0.AddMilliseconds(20 * frameMs);
        c.ApplyTruth(38d, BytesFor(38d), Planned, false, "JOB-1", tSwitch);

        var vSwitch = c.Advance(tSwitch);
        Assert.Equal(vSwitch, prev, 9);   // 同一时刻再取一拍：dt = 0，视觉不得凭空运动
        var vNext = c.Advance(tSwitch.AddMilliseconds(frameMs));   // 目标变化后的**第一个有时间的帧**
        var stepAtSwitch = vNext - vSwitch;
        Assert.True(stepAtSwitch >= 0d, "目标变化的那一帧不得回退");
        Assert.True(stepAtSwitch >= stepBefore * 0.5d,
            $"目标变化处出现速度断层：变化前每帧 {stepBefore:0.####} 点、变化首帧只有 {stepAtSwitch:0.####} 点");
        Assert.True(stepAtSwitch > stepBefore,
            "目标变大后首帧步长应当**变大**（差距变大），变小说明又回到\"一段动画结束再开一段\"");

        // 后续必须单调、且不得越过新真值
        var v2 = c.Advance(tSwitch.AddMilliseconds(2 * frameMs));
        Assert.True(v2 > vSwitch, "目标变化后必须继续前进");
        Assert.True(v2 <= 38d + 1e-6, "仍不得超过已确认真值");

        // 追上以后必须停住（不许为了"看起来在动"自爬）
        for (var f = 0; f < 600; f++) v2 = c.Advance(tSwitch.AddMilliseconds((f + 2) * frameMs));
        Assert.Equal(38d, v2, 3);
        Assert.False(c.IsAnimating, "追上真值后必须停止运动");
    }

    [Fact]
    public void VisualNeverExceedsConfirmedTarget()
    {
        var c = new ProgressPresentationCoordinator();
        c.Reset("JOB-1", "test");
        var t = T0;

        foreach (var (percent, ms) in new[] { (5d, 0), (12d, 240), (12d, 480), (31d, 720), (31d, 2000), (64d, 2200) })
        {
            var now = t.AddMilliseconds(ms);
            c.ApplyTruth(percent, BytesFor(percent), Planned, false, "JOB-1", now);
            var visual = c.Advance(now);
            Assert.True(visual <= percent + 1e-6, $"视觉 {visual:0.###}% > 已确认真值 {percent:0.###}%");
            Assert.True(visual >= 0d, "视觉不得为负");
        }
    }

    [Fact]
    public void RetargetStartsFromCurrentVisual()
    {
        var c = new ProgressPresentationCoordinator();
        c.Reset("JOB-1", "test");
        var t0 = T0;

        c.ApplyTruth(24.8d, BytesFor(24.8d), Planned, false, "JOB-1", t0);
        c.Advance(t0);   // ★ 首拍只建立时钟基准（dt 无参照）⇒ 先"起表"，再测真实帧行为
        // 连续指数滤波器跑 400 ms（dt 夹到 1/30 s 后约 0.283τ·每拍）⇒ 视觉应在 (0, 24.8) 之间
        var midway = c.Advance(t0.AddMilliseconds(400));
        Assert.True(midway > 0d && midway < 24.8d, $"中途视觉应在 (0, 24.8) 之间，实际 {midway:0.###}");

        // 新目标在动画未结束时到达：必须从**当前视觉位置**接管，既不 snap 到 24.8 也不跳到 47.3
        var tnew = t0.AddMilliseconds(400);
        c.ApplyTruth(47.3d, BytesFor(47.3d), Planned, false, "JOB-1", tnew);
        var justAfter = c.Advance(tnew);
        Assert.True(justAfter >= midway - 1e-6, "接管时视觉不得回退");
        Assert.True(justAfter < 47.3d, "接管时不得瞬间跳到新目标（那是跳帧）");

        // 继续推进后必须单调走向新目标
        var later = c.Advance(tnew.AddMilliseconds(900));
        Assert.True(later > justAfter, "接管后必须继续前进");
        Assert.True(later <= 47.3d + 1e-6, "仍不得超过已确认真值");
    }

    [Fact]
    public void PercentTextAndBarStaySynchronized()
    {
        // §3.3：大号百分比文本与进度条必须消费**同一个** VisualPercent —— 本测试模拟两个消费端各自换算，
        // 并断言它们在每个采样点上一致（差 ≤ 0.5 个百分点 ⇒ 屏幕上不可能出现"数字跳了、条还在追"）。
        const double trackWidth = 1010d;   // 与真机 TotalProgressHost 实测宽度同量级
        var c = new ProgressPresentationCoordinator();
        c.Reset("JOB-1", "test");
        var t0 = T0;
        c.ApplyTruth(0d, 0L, Planned, false, "JOB-1", t0);

        foreach (var (percent, ms) in new[] { (24.8d, 200), (47.3d, 400), (62.3d, 3200) })
        {
            var now = t0.AddMilliseconds(ms);
            c.ApplyTruth(percent, BytesFor(percent), Planned, false, "JOB-1", now);

            for (var k = 0; k <= 60; k++)
            {
                var sampleAt = now.AddMilliseconds(k * 16);
                var visual = c.Advance(sampleAt);

                var percentTextValue = Math.Round(visual, 1);                    // TotalPercentText.Text = $"{visual:0.0}%"
                var barValue = Math.Round(trackWidth * visual / 100d, 1) / trackWidth * 100d;  // 条：width = track*visual/100
                Assert.True(Math.Abs(percentTextValue - barValue) <= 0.5d,
                    $"文本 {percentTextValue:0.##}% 与条 {barValue:0.##}% 相差超过 0.5 点");
            }
        }
    }

    [Fact]
    public void PauseStopFreezeMotion()
    {
        var c = new ProgressPresentationCoordinator();
        c.Reset("JOB-1", "test");
        var t0 = T0;

        c.ApplyTruth(30d, BytesFor(30d), Planned, false, "JOB-1", t0);
        c.Advance(t0);   // 先起表（首拍无 dt 参照）
        var before = c.Advance(t0.AddMilliseconds(400));
        Assert.True(before > 0d);

        // 用户按下暂停 / 停止的那一刻：冻结到**用户已经看到的显示真值**上（这里 30%）。
        // 注意语义：冻结值来自 ContinuationDisplayState 的高水位，若当前视觉还没追上它，就必须抬上去 ——
        // 因为用户屏幕上的数字已经是 30%，条停在 12.6% 反而是一种新的"数字与条不一致"。
        var tFreeze = t0.AddMilliseconds(400);
        c.Freeze(30d, tFreeze, "pause-requested");
        Assert.Equal(ProgressVisualMode.Frozen, c.Mode);

        // 冻结后：即使时间继续走、真值又被引擎按已完成回执重建为更低值，视觉也绝不动、绝不回退
        c.ApplyTruth(0d, 0L, Planned, false, "JOB-1", tFreeze.AddMilliseconds(500));
        var afterFreeze = c.Advance(tFreeze.AddMilliseconds(2000));
        Assert.Equal(30d, afterFreeze, 6);
        Assert.True(afterFreeze >= before - 1e-6, "冻结不得让视觉比冻结前更低");
        Assert.False(c.IsAnimating, "冻结后不得再有补间动画");

        // Resume：解除冻结，视觉**停在原处**（不归零），随后由真值驱动继续前进
        c.ResumeLive(tFreeze.AddMilliseconds(3000), "resume-requested");
        var atResume = c.Advance(tFreeze.AddMilliseconds(3000));
        Assert.Equal(afterFreeze, atResume, 6);
        c.ApplyTruth(31d, BytesFor(31d), Planned, false, "JOB-1", tFreeze.AddMilliseconds(3100));
        var later = c.Advance(tFreeze.AddMilliseconds(3600));
        Assert.True(later >= atResume - 1e-6, "Resume 后视觉不得倒退");
    }

    [Fact]
    public void CompletionSettlesTo100OnlyAfterCompleted()
    {
        var c = new ProgressPresentationCoordinator();
        c.Reset("JOB-1", "test");
        var t0 = T0;

        // 运行中：真值 99.9%（RunningPercentCeiling）⇒ 视觉最多 99.9，绝不自爬到 100
        c.ApplyTruth(99.9d, BytesFor(99.9d), Planned, false, "JOB-1", t0);
        var running = c.Advance(t0.AddMilliseconds(1500));
        Assert.True(running <= 99.9d + 1e-6, $"普通运行就冲到 100%（{running:0.###}）= 伪造完成");
        Assert.NotEqual(ProgressVisualMode.Completed, c.Mode);

        // 引擎真完成：允许一次收敛到 100（连续滤波器按帧收敛，60 帧 = 1 秒足够走到 99.9%+）
        var tDone = t0.AddMilliseconds(2000);
        c.ApplyTruth(100d, Planned, Planned, settled: true, jobId: "JOB-1", utcNow: tDone);
        var settled = c.Advance(tDone);
        for (var f = 1; f <= 90; f++) settled = c.Advance(tDone.AddMilliseconds(f * 16));

        // ★ Round-3 PHASE D（验收判据 ⑬）★ 必须是**确切**的 100，而不是 99.999…
        // 为什么不能只断 3 位小数：指数滤波永远只渐近逼近，Completed 尾态会永远停在 99.99999…
        // —— 那样"真正完成后才到 100%"就不是一个可验证的数字，只是一句看起来对的话。
        // 贴合由 ProgressPresentationCoordinator.CompletedSnapEpsilon(0.05) 保证：
        // 它吃掉的最后一小段小于一位小数显示分辨率的一半 ⇒ 屏幕上看到的数字不会因此跳变。
        Assert.Equal(100d, settled);
        Assert.Equal(ProgressVisualMode.Completed, c.Mode);
    }
}