using PCMig.WinUI.Controls.ImmersiveProgress;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ Round-3 PHASE D（执行书 §11 / §18 / §26）★ 装饰层状态机的**数字**契约。
/// <para>
/// 这一层承载的是产品的第二句语义：<b>Progress Head = Fact，Push Band / 粒子 / Ripple = Activity</b>。
/// 它必须能在"Head 一动不动"时单独证明"任务仍在工作"，也必须能在 Paused/Failed 时**立刻收干净**。
/// 装饰层永远不得参与进度语义（§17 / R28）——所以这里没有一条断言碰 Value / Percent / Bytes。
/// </para>
/// </summary>
public sealed class ImmersiveProgressAnimationStateTests
{
    private const float Dt = 1f / 60f;          // 60 Hz
    private const float Thickness = 12f;
    private const float HostWidth = 1010f;

    private static ImmersiveProgressAnimationState NewState()
        => new(ImmersiveProgressParameters.ParticlePool, ImmersiveProgressParameters.RippleMax, seed: 20261005);

    private static void Tick(ImmersiveProgressAnimationState s, int frames, float headX, float progressWidth,
        ImmersiveProgressState state = ImmersiveProgressState.Running,
        ImmersiveEffectsQuality quality = ImmersiveEffectsQuality.High,
        bool reducedMotion = false)
    {
        for (var i = 0; i < frames; i++)
        {
            s.Update(Dt, headX, progressWidth, Thickness, state, quality, reducedMotion);
        }
    }

    /// <summary>判据 ⑦⑧ + §11：任何时刻最多一道 Push Band，且静止段必须**完全不画**。</summary>
    [Fact]
    public void PushBand_IsAtMostOne_AndFullyOffDuringTheRestWindow()
    {
        var s = NewState();
        var activeFrames = 0;
        var totalFrames = 400;   // 400/60 ≈ 6.7 s ≈ 5 个周期

        for (var i = 0; i < totalFrames; i++)
        {
            s.Update(Dt, 300f, 300f, Thickness, ImmersiveProgressState.Running,
                ImmersiveEffectsQuality.High, reducedMotion: false);

            if (s.BandPhaseSeconds >= ImmersiveProgressParameters.BandActiveSeconds)
            {
                // 静止段：不透明度必须精确为 0 ⇒ 屏幕上不可能同时出现两道
                Assert.Equal(0f, s.BandOpacity);
            }
            else if (s.BandOpacity > 0f)
            {
                activeFrames++;
            }
        }

        var expectedActive = totalFrames * ImmersiveProgressParameters.BandActiveSeconds
                             / ImmersiveProgressParameters.BandCycleSeconds;
        Assert.True(Math.Abs(activeFrames - expectedActive) <= totalFrames * 0.10,
            $"活动段帧数 {activeFrames} 与周期比例推算的 {expectedActive:0} 相差过大（周期曲线被改动了？）");
    }

    /// <summary>
    /// 判据 ⑦ + §11：一个周期内 Band 必须**严格单调地**逼近 Head（不允许来回摆），
    /// 且**连续减速**（sin 缓动：每帧位移量只减不增），不得线性匀速、不得分段切换。
    /// <para>
    /// 注意口径：单调性与减速只在**同一个周期内**成立 —— 跨周期比较会把"新周期的起步速度"
    /// 误判成"忽然加速"。所以这里按周期分段校验，而不是把整段时间的 Δ 串成一条序列。
    /// </para>
    /// </summary>
    [Fact]
    public void PushBand_ApproachesHeadMonotonically_WithContinuousDeceleration()
    {
        var s = NewState();
        var validatedWindows = 0;
        List<float>? deltas = null;      // 当前周期内的逐帧位移
        List<float>? distances = null;   // 当前周期内的逐帧距离
        var previous = float.NaN;

        for (var i = 0; i < 300; i++)
        {
            s.Update(Dt, 400f, 400f, Thickness, ImmersiveProgressState.Running,
                ImmersiveEffectsQuality.High, reducedMotion: false);

            var inActiveWindow = s.BandPhaseSeconds < ImmersiveProgressParameters.BandActiveSeconds;
            if (!inActiveWindow)
            {
                // 活动段结束：结算本周期
                if (deltas is { Count: > 20 })
                {
                    AssertWindow(deltas, distances!);
                    validatedWindows++;
                }
                deltas = null;
                distances = null;
                previous = float.NaN;
                continue;
            }

            var distance = Math.Abs(s.BandOffsetFromHead);
            if (deltas is null)
            {
                // 新周期第一帧：只建立基准
                deltas = new List<float>();
                distances = new List<float>();
                previous = distance;
                continue;
            }

            // 单调逼近：距离只减不增（容差 1e-3 覆盖 float 舍入）
            Assert.True(distance <= previous + 1e-3f,
                $"Band 在周期内远离了 Head：{previous:0.###} → {distance:0.###}");
            deltas.Add(previous - distance);
            distances!.Add(distance);
            previous = distance;
        }

        // 最后一帧可能停在活动段中间（未结算）⇒ 也校验它
        if (deltas is { Count: > 20 })
        {
            AssertWindow(deltas, distances!);
            validatedWindows++;
        }

        Assert.True(validatedWindows >= 3,
            $"必须校验到至少 3 个完整周期才有说服力（实际 {validatedWindows}）");
    }

    /// <summary>一个活动段内部的曲线纪律：位移量只减不增（连续减速），且收尾显著柔和。</summary>
    private static void AssertWindow(List<float> deltas, List<float> distances)
    {
        for (var i = 1; i < deltas.Count; i++)
        {
            Assert.True(deltas[i] <= deltas[i - 1] + 1e-3f,
                $"Band 在周期中段忽然加速：Δ[{i - 1}]={deltas[i - 1]:0.#####} → Δ[{i}]={deltas[i]:0.#####}");
        }

        Assert.True(deltas[0] > 0f, "活动段必须有位移（否则 Band 根本没在走）");
        Assert.True(deltas[^1] < deltas[0] * 0.5f,
            $"Band 收尾不够柔和：首帧 Δ={deltas[0]:0.####}，末帧 Δ={deltas[^1]:0.####}");
        Assert.True(distances[0] > distances[^1], "Band 必须真的向 Head 靠近");
    }

    /// <summary>§11 + 判据 ⑧：Ripple 稀疏且有硬上限；Reduced 档必须完全关闭。</summary>
    [Fact]
    public void Ripples_NeverExceedCap_AndAreOffInReducedQuality()
    {
        Assert.Equal(0, ImmersiveProgressParameters.ActiveRipples(ImmersiveEffectsQuality.Reduced));
        Assert.True(ImmersiveProgressParameters.ActiveRipples(ImmersiveEffectsQuality.Balanced)
                    <= ImmersiveProgressParameters.RippleMax);
        Assert.True(ImmersiveProgressParameters.ActiveRipples(ImmersiveEffectsQuality.High)
                    <= ImmersiveProgressParameters.RippleMax);

        var high = NewState();
        var maxSeen = 0;
        for (var i = 0; i < 600; i++)
        {
            high.Update(Dt, HostWidth * 0.6f, HostWidth * 0.6f, Thickness, ImmersiveProgressState.Running,
                ImmersiveEffectsQuality.High, reducedMotion: false);
            maxSeen = Math.Max(maxSeen, high.ActiveRippleCount);
        }

        Assert.True(maxSeen <= ImmersiveProgressParameters.RippleMax,
            $"High 档 Ripple 数 {maxSeen} 超过上限 {ImmersiveProgressParameters.RippleMax}");
        Assert.True(maxSeen >= 1, "本用例必须真的观测到 Ripple（否则上限断言等于空转）");

        var reduced = NewState();
        for (var i = 0; i < 600; i++)
        {
            reduced.Update(Dt, HostWidth * 0.6f, HostWidth * 0.6f, Thickness, ImmersiveProgressState.Running,
                ImmersiveEffectsQuality.Reduced, reducedMotion: false);
            Assert.Equal(0, reduced.ActiveRippleCount);
        }
    }

    /// <summary>
    /// 判据 ⑩⑪ + §19 + ★ R35 ★：
    /// **真正终止**的状态（Failed / Completed）必须把装饰层收干净；
    /// 而 **Paused / Interrupted 是"停住但活着"**（用户明示：Pause ≠ Failed ≠ Dead，
    /// 且"Pause 时 Particle 还在 / Push Band 还在 / Halo 还在"），因此它们**不在**本用例里，
    /// 由 <see cref="RecoverableStates_KeepMaterialAlive_ButWeaker"/> 单独锁"降强度保留"。
    /// 口径从"所有非 Running 都熄灭"收紧为"只有终止态才熄灭"——是按用户执行书 §11/R35 的正确化，不是放宽。
    /// </summary>
    [Theory]
    [InlineData(ImmersiveProgressState.Failed)]
    [InlineData(ImmersiveProgressState.Completed)]
    public void TerminalStates_StopAllDecorations(ImmersiveProgressState state)
    {
        var s = NewState();
        Tick(s, frames: 120, headX: 600f, progressWidth: 600f);   // 先跑起来，让池子装满
        Assert.True(s.ActiveParticleCount > 0, "前置条件：Running 时必须有粒子，否则本用例测不到「停下来」这件事");

        Tick(s, frames: 240, headX: 600f, progressWidth: 600f, state: state);   // 4 秒

        Assert.Equal(0f, s.BandOpacity);
        Assert.Equal(0, s.ActiveParticleCount);
        Assert.Equal(0, s.ActiveRippleCount);
    }

    /// <summary>
    /// ★ R35（用户执行书 §11 "Pause = 冻结事实，不是熄灭材质"）★
    /// Paused / Interrupted 下：**事实冻结**（本用例不动 headX），但材质**必须继续活着**：
    ///   · Push Band 仍在周期运行（BandOpacity 出现过 &gt; 0）；
    ///   · 粒子仍在（ActiveParticleCount &gt; 0），且数量不高于 Running（降强度）；
    ///   · Halo 强度保持在高位（不再是被熄灭的 0.22 级别）。
    /// </summary>
    [Theory]
    [InlineData(ImmersiveProgressState.Paused)]
    [InlineData(ImmersiveProgressState.Interrupted)]
    public void RecoverableStates_KeepMaterialAlive_ButWeaker(ImmersiveProgressState state)
    {
        var s = NewState();
        Tick(s, frames: 120, headX: 600f, progressWidth: 600f);
        var runningParticles = s.ActiveParticleCount;
        Assert.True(runningParticles > 0, "前置条件：Running 时必须有粒子");

        var sawBand = false;
        for (var i = 0; i < 240; i++)   // 4 秒
        {
            s.Update(Dt, 600f, 600f, Thickness, state, ImmersiveEffectsQuality.High, reducedMotion: false);
            if (s.BandOpacity > 0.01f) sawBand = true;
        }

        Assert.True(sawBand, "R35：Paused/Interrupted 时 Push Band 必须仍在跑（降速降亮，而不是熄灭）");
        Assert.True(s.ActiveParticleCount > 0, "R35：Paused/Interrupted 时粒子必须仍在");
        Assert.True(s.ActiveParticleCount <= runningParticles, "R35：Paused/Interrupted 的粒子数不得高于 Running（必须降强度）");
        Assert.True(s.HaloStrength >= 0.35f, $"R35：Paused/Interrupted 的 Halo 必须保留（实测 {s.HaloStrength:0.###}）");
    }

    /// <summary>
    /// ★ 判据 ⑫ + §18（本轮最重要的一条语义）★ 真值不动时：Head 静止，但 Band 继续跑、粒子仍在、
    /// 光照仍然活着 —— 因为那表达的是"任务还在工作"，而不是"进度又涨了"。
    /// </summary>
    [Fact]
    public void Holding_HeadStaysStill_WhileActivityContinues()
    {
        var s = NewState();
        const float frozenHeadX = 477.729f;   // 47.3% × 1010，真机 holding 行实测值

        Tick(s, frames: 60, headX: frozenHeadX, progressWidth: frozenHeadX, state: ImmersiveProgressState.Holding);
        var bandPhases = new HashSet<double>();
        var opacitySeen = new HashSet<float>();
        var particleMin = float.MaxValue;
        var particleMax = float.MinValue;

        for (var i = 0; i < 96; i++)   // 1.6 s：与执行书 §18 "8 秒没有新字节"同一语义的短窗
        {
            s.Update(Dt, frozenHeadX, frozenHeadX, Thickness, ImmersiveProgressState.Holding,
                ImmersiveEffectsQuality.High, reducedMotion: false);
            bandPhases.Add(Math.Round(s.BandPhaseSeconds, 4));
            opacitySeen.Add(MathF.Round(s.BandOpacity, 3));
            if (s.TryParticleExtent(out var minX, out var maxX))
            {
                particleMin = Math.Min(particleMin, minX);
                particleMax = Math.Max(particleMax, maxX);
            }
        }

        Assert.Equal(frozenHeadX, s.LastHeadX, 3);           // Head 一毫米没动（Fact 不变）
        Assert.True(bandPhases.Count > 20, "Holding 时 Push Band 必须继续走相位（Activity 继续）");
        Assert.True(opacitySeen.Any(o => o > 0f), "Holding 时 Push Band 必须仍然可见");
        Assert.True(s.ActiveParticleCount > 0, "Holding 时粒子必须仍在（材质还在动）");
        Assert.True(particleMin >= -0.001f && particleMax <= frozenHeadX + 0.001f,
            $"Holding 时粒子越出胶囊：[{particleMin:0.###}, {particleMax:0.###}]，胶囊宽 {frozenHeadX:0.###}");
    }

    /// <summary>§20 + R29：降低动效**只**关装饰，不改任何业务数值语义（本层没有业务字段，故只断言装饰）。</summary>
    [Fact]
    public void ReducedMotion_TurnsDecorationsOff()
    {
        var s = NewState();
        Tick(s, frames: 240, headX: 500f, progressWidth: 500f,
            state: ImmersiveProgressState.Running, reducedMotion: true);

        Assert.Equal(0f, s.BandOpacity);
        Assert.Equal(0f, s.HaloStrength);
        Assert.Equal(0, s.ActiveRippleCount);
        Assert.Equal(0, s.ActiveParticleCount);
    }

    /// <summary>§14：帧循环容错 —— dt 为 0 / 负 / 巨大（窗口恢复）时都不得把动画推爆。</summary>
    [Fact]
    public void Update_ClampsHostileDeltaTimes()
    {
        var s = NewState();
        s.Update(0d, 500f, 500f, Thickness, ImmersiveProgressState.Running, ImmersiveEffectsQuality.High, false);
        s.Update(-5d, 500f, 500f, Thickness, ImmersiveProgressState.Running, ImmersiveEffectsQuality.High, false);
        s.Update(3.7d, 500f, 500f, Thickness, ImmersiveProgressState.Running, ImmersiveEffectsQuality.High, false);

        Assert.False(double.IsNaN(s.BandPhaseSeconds), "相位被 NaN 污染");
        Assert.True(s.BandPhaseSeconds >= 0d && s.BandPhaseSeconds < ImmersiveProgressParameters.BandCycleSeconds);
        Assert.True(s.ElapsedSeconds <= ImmersiveProgressParameters.MaxStepSeconds * 3d + 1e-6,
            $"巨大 dt 未被夹住：ElapsedSeconds={s.ElapsedSeconds:0.###}（会把动画一步推完）");
    }
}