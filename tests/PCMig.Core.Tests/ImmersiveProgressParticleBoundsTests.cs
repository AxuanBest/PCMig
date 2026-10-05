using PCMig.WinUI.Controls.ImmersiveProgress;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ Round-3 PHASE D（执行书 §11 / §26 判据 ⑨）★ 粒子必须永远留在 Progress Fill Capsule 内。
/// <para>
/// 本测试锁死两个**真机 timeline.csv 实测到的**缺陷（2026-10-05，
/// <c>E:\PCMigLab\Staging\phI-static\probe\timeline.csv</c>）：
/// ① 进度 0% 时仍有 14 枚粒子存活（19 行越界）—— 此时**根本不存在填充胶囊**，粒子无处可落；
/// ② <c>progressWidth=26.233</c> 时 <c>particleMinX=-1.163</c> —— 粒子横向越过了胶囊左端。
/// </para>
/// <para>
/// 语义边界：粒子只表达"数据材质在流动"，**不表达任何进度**（§17 / R28）。
/// 所以这些断言只碰几何，不碰 Value / Percent / Bytes。
/// </para>
/// </summary>
public sealed class ImmersiveProgressParticleBoundsTests
{
    private const float Dt = 1f / 60f;
    private const float Thickness = 12f;      // PCMigImmersiveProgressThickness
    private const int HighActive = 14;        // ParticleActiveHigh

    /// <summary>推进若干帧（默认 90 帧 = 1.5 s，足够让"延迟出生"的粒子真正可见）。</summary>
    private static void Run(ImmersiveProgressParticlePool pool, int frames, float headX, float progressWidth,
        int targetActive = HighActive, bool allowSpawn = true)
    {
        for (var i = 0; i < frames; i++)
        {
            pool.Update(Dt, headX, progressWidth, Thickness, targetActive, allowSpawn, headX);
        }
    }

    /// <summary>胶囊容得下粒子的最小宽度（由参数表推导，不写死魔法数）。</summary>
    private static float MinUsableCapsuleWidth()
    {
        var maxRadius = Math.Min((float)ImmersiveProgressParameters.ParticleMaxRadius, Thickness * 0.30f);
        var extent = maxRadius * (1f + (float)ImmersiveProgressParameters.ParticleRadiusGain);
        return 2f * extent + maxRadius;
    }

    [Fact]
    public void ZeroProgress_HasNoParticles_BecauseThereIsNoCapsule()
    {
        var pool = new ImmersiveProgressParticlePool(ImmersiveProgressParameters.ParticlePool);

        Run(pool, frames: 120, headX: 0f, progressWidth: 0f);

        Assert.Equal(0, pool.ActiveCount);
        Assert.False(pool.TryActiveExtent(out _, out _));
    }

    [Fact]
    public void RealMachineNarrowCapsule_KeepsParticlesInsideBounds()
    {
        // 真机 t=852 step=t10 的那一行：pw=26.233 时曾出现 pMin=-1.163。
        const float progressWidth = 26.233f;
        var pool = new ImmersiveProgressParticlePool(ImmersiveProgressParameters.ParticlePool);

        var sawParticle = false;
        for (var frame = 0; frame < 240; frame++)
        {
            pool.Update(Dt, progressWidth, progressWidth, Thickness, HighActive, allowSpawn: true, progressWidth);
            if (!pool.TryActiveExtent(out var minX, out var maxX)) continue;
            sawParticle = true;
            Assert.True(minX >= -0.001f, $"粒子越过胶囊左端：minX={minX:0.###}（pw={progressWidth}）");
            Assert.True(maxX <= progressWidth + 0.001f, $"粒子越过胶囊右端：maxX={maxX:0.###}（pw={progressWidth}）");
        }

        Assert.True(sawParticle, "26.233 DIP 的胶囊足以容纳粒子，本用例必须真的观测到粒子（否则断言等于空转）");
    }

    [Fact]
    public void SweepAcrossProgressWidths_ParticlesNeverLeaveTheCapsule()
    {
        var pool = new ImmersiveProgressParticlePool(ImmersiveProgressParameters.ParticlePool);
        var usable = MinUsableCapsuleWidth();
        var observedAt = new List<float>();

        // 0 → 1010 DIP（生产 Hero 宽度），含 0 / 极小 / 窄胶囊 / 正常各段
        foreach (var progressWidth in new[] { 0f, 1f, 3f, 4.4f, 5f, 8f, 12f, 26.233f, 64f, 200f, 477.729f, 1010f })
        {
            pool.Clear();
            for (var frame = 0; frame < 200; frame++)
            {
                pool.Update(Dt, progressWidth, progressWidth, Thickness, HighActive, allowSpawn: true, progressWidth);
                if (!pool.TryActiveExtent(out var minX, out var maxX)) continue;
                Assert.True(minX >= -0.001f,
                    $"pw={progressWidth} 时粒子越过左端：minX={minX:0.###}");
                Assert.True(maxX <= progressWidth + 0.001f,
                    $"pw={progressWidth} 时粒子越过右端：maxX={maxX:0.###}");

                // 胶囊容不下粒子时，一枚都不许存在
                if (progressWidth < usable)
                {
                    Assert.Fail($"pw={progressWidth} < 可容纳粒子的最小宽度 {usable:0.###}，却仍有粒子存活");
                }
                observedAt.Add(progressWidth);
            }
        }

        Assert.Contains(1010f, observedAt);
    }

    [Fact]
    public void WideCapsule_StillSpawnsParticles_SoTheFixDoesNotJustSuppressEverything()
    {
        var pool = new ImmersiveProgressParticlePool(ImmersiveProgressParameters.ParticlePool);

        Run(pool, frames: 60, headX: 200f, progressWidth: 200f);

        Assert.True(pool.ActiveCount > 0, "正常胶囊下必须有粒子，否则 §9『材质在流动』的表达就丢了");
        Assert.True(pool.ActiveCount <= HighActive);
    }

    [Fact]
    public void WhenSpawningIsNotAllowed_PoolDrainsToZero()
    {
        var pool = new ImmersiveProgressParticlePool(ImmersiveProgressParameters.ParticlePool);
        Run(pool, frames: 60, headX: 200f, progressWidth: 200f);
        Assert.True(pool.ActiveCount > 0);

        // Paused / Failed：不再生成，且旧粒子必须在寿命内自然消失（不允许"永远悬着"）
        Run(pool, frames: 200, headX: 200f, progressWidth: 200f, allowSpawn: false);

        Assert.Equal(0, pool.ActiveCount);
    }
}