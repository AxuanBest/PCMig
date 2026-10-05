namespace PCMig.WinUI.Controls.ImmersiveProgress;

/// <summary>
/// ★ Round-3（执行书 §12）★ 控件的**动画状态**：Band 相位、粒子池、Ripple 池、dt、随机种子、
/// 上一次进度位置。它只表达"时间与光学状态"，**不含任何业务真值**（Value/Percent 由控件 DP 提供）。
///
/// 语义边界（§17 / §18 / R28）：
///   · 本类不知道 Robocopy / SMB / 回执 / Verifier / 作业状态 / 日志 / 源路径 / 目标路径；
///   · 它唯一的外部输入是"Head 现在的 X"与"是否允许活动"；
///   · Progress Head = Fact（由 Value 决定）、Push Band / 粒子 / Ripple = Activity（由本类决定）。
/// </summary>
internal sealed class ImmersiveProgressAnimationState
{
    private readonly ImmersiveProgressParticlePool _particles;
    private readonly ImmersiveProgressRipple[] _ripples;
    private readonly Random _random;

    private double _bandPhaseSeconds;
    private double _haloStrength;
    private double _rippleCooldown;
    private float _lastHeadX;
    private bool _primed;

    public ImmersiveProgressAnimationState(int particleCapacity = ImmersiveProgressParameters.ParticlePool,
        int rippleCapacity = ImmersiveProgressParameters.RippleMax, int seed = 20261005)
    {
        _particles = new ImmersiveProgressParticlePool(particleCapacity, seed);
        _ripples = new ImmersiveProgressRipple[rippleCapacity > 0 ? rippleCapacity : 1];
        _random = new Random(seed + 977);
    }

    /// <summary>粒子池（Renderer 只读遍历）。</summary>
    public ImmersiveProgressParticlePool Particles => _particles;

    /// <summary>Ripple 池（Renderer 只读遍历）。</summary>
    public ReadOnlySpan<ImmersiveProgressRipple> Ripples => _ripples;

    /// <summary>Push Band 当前相位（秒，0 ~ BandCycleSeconds）。</summary>
    public double BandPhaseSeconds => _bandPhaseSeconds;

    /// <summary>Push Band 中心相对 Head 的偏移（DIP，负值 = 在 Head 左侧）。</summary>
    public float BandOffsetFromHead { get; private set; }

    /// <summary>Push Band 当前不透明度系数（活动段内非零；静止段为 0 ⇒ 一次只有一道）。</summary>
    public float BandOpacity { get; private set; }

    /// <summary>Halo 当前强度（0~1，用于淡入淡出）。</summary>
    public float HaloStrength => (float)_haloStrength;

    /// <summary>当前存活粒子数（诊断 / 验收 Z 列）。</summary>
    public int ActiveParticleCount => _particles.ActiveCount;

    /// <summary>
    /// 存活粒子的横向范围（DIP）。**验收判据 ⑨**：粒子必须留在 Fill Capsule 内（即 `0 <= minX` 且 `maxX <= progressWidth`）。
    /// 没有存活粒子时返回 false。
    /// </summary>
    public bool TryParticleExtent(out float minX, out float maxX) => _particles.TryActiveExtent(out minX, out maxX);

    /// <summary>当前存活 Ripple 数（诊断 / 验收 Z 列）。</summary>
    public int ActiveRippleCount
    {
        get
        {
            var count = 0;
            for (var i = 0; i < _ripples.Length; i++)
            {
                if (_ripples[i].Alive) count++;
            }
            return count;
        }
    }

    /// <summary>总帧数（诊断：确认帧循环真的在跑）。</summary>
    public long TickCount { get; private set; }

    /// <summary>上一帧的 Head X（诊断用；证明 Head 位置确实与 <c>Value</c> 一致地变化）。</summary>
    public float LastHeadX => _lastHeadX;

    /// <summary>累计推进时间（秒，诊断用；只统计真正收到 dt 的帧）。</summary>
    public double ElapsedSeconds { get; private set; }

    /// <summary>复位（换任务）。</summary>
    public void Reset()
    {
        _particles.Clear();
        for (var i = 0; i < _ripples.Length; i++) _ripples[i].Alive = false;
        _bandPhaseSeconds = 0d;
        _haloStrength = 0d;
        _rippleCooldown = 0d;
        _primed = false;
        TickCount = 0;
        ElapsedSeconds = 0d;
    }

    /// <summary>
    /// 推进一帧。所有输入都是"呈现态"的量，没有任何业务真值。
    /// </summary>
    /// <param name="rawDt">本帧真实间隔（秒；内部会夹到 MaxStepSeconds）。</param>
    /// <param name="headX">Head 绝对 X（DIP）。</param>
    /// <param name="progressWidth">已完成进度宽度（DIP）。</param>
    /// <param name="thickness">胶囊厚度（DIP）。</param>
    /// <param name="state">业务状态机（只决定"装饰是否活动"，不参与任何数值计算）。</param>
    /// <param name="quality">特效档位。</param>
    /// <param name="reducedMotion">是否要求降低动效。</param>
    public void Update(double rawDt, float headX, float progressWidth, float thickness,
        ImmersiveProgressState state, ImmersiveEffectsQuality quality, bool reducedMotion)
    {
        var dt = rawDt;
        if (dt < 0d) dt = 0d;
        else if (dt > ImmersiveProgressParameters.MaxStepSeconds) dt = ImmersiveProgressParameters.MaxStepSeconds;

        TickCount++;
        ElapsedSeconds += dt;
        if (!_primed)
        {
            _primed = true;
            _lastHeadX = headX;
        }

        var bandEnabled = ImmersiveProgressParameters.BandEnabled(state, reducedMotion);
        var particlesEnabled = ImmersiveProgressParameters.ParticlesEnabled(state, reducedMotion);
        var haloScale = ImmersiveProgressParameters.HaloScale(state, quality);
        // ★ 执行书 R35（2026-10-05 视觉第二轮）★ Paused / Interrupted 是"停住但活着"：
        //   事实（Head / Percent / Bytes）由调用方冻结，这里**只**降速降亮，绝不把材质熄灭。
        var bandSpeed = ImmersiveProgressParameters.BandSpeedScale(state);
        var bandBrightness = ImmersiveProgressParameters.BandBrightnessScale(state);
        var particleSpeed = ImmersiveProgressParameters.ParticleSpeedScale(state);
        var particleCount = ImmersiveProgressParameters.ParticleCountScale(state);

        // ── Push Band：一次只有一道；周期内"活动段 + 静止段"──────────────────────
        if (bandEnabled && dt > 0d)
        {
            _bandPhaseSeconds += dt * bandSpeed;
            if (_bandPhaseSeconds >= ImmersiveProgressParameters.BandCycleSeconds)
            {
                _bandPhaseSeconds -= ImmersiveProgressParameters.BandCycleSeconds;
            }

            var active = ImmersiveProgressParameters.BandActiveSeconds;
            if (_bandPhaseSeconds < active)
            {
                // x(t) = sin(t * PI/2)，t ∈ [0,1] ⇒ 前段快、中段连续减速、接近 Head 柔和收尾。
                // 严格单调逼近 Head、**没有**线性匀速、**没有**分段切换、**没有**突然减速。
                var t = _bandPhaseSeconds / active;
                var eased = Math.Sin(t * Math.PI / 2d);
                BandOffsetFromHead = -(float)((1d - eased) * ImmersiveProgressParameters.BandTravelLength);
                // 两端各 8% 淡入淡出，避免"啪"地出现/消失；★ R35 ★ 再乘状态亮度倍率
                var edge = Math.Min(1d, Math.Min(t / 0.08d, (1d - t) / 0.08d));
                var edgeScaled = (edge < 0d ? 0d : edge) * bandBrightness;
                BandOpacity = (float)edgeScaled;
            }
            else
            {
                BandOpacity = 0f;   // 静止段：Band 完全不画 ⇒ 任何时刻最多一道
            }
        }
        else
        {
            BandOpacity = 0f;
        }

        var bandCenterX = headX + BandOffsetFromHead;

        // ── 粒子：固定池、稀疏、只在进度空间内 ─────────────────────────────────
        if (particlesEnabled)
        {
            // ★ R35 ★ Paused / Interrupted 时数量与流速都减半（而不是熄灭）
            var activeTarget = (int)Math.Round(ImmersiveProgressParameters.ActiveParticles(quality) * particleCount);
            if (particleCount > 0d && activeTarget < 1) activeTarget = 1;   // 保留最低活性（"还活着"）
            _particles.Update((float)(dt * particleSpeed), headX, progressWidth, thickness,
                activeTarget, allowSpawn: true, bandCenterX);
        }
        else
        {
            // 关闭时快速淡出（把寿命推到尾部），而不是瞬间消失
            _particles.Update((float)(dt * 4d), headX, progressWidth, thickness, 0, allowSpawn: false, bandCenterX);
            // ★ R35 ★ 只有真正"结束/失败"的状态才清空粒子。Interrupted（可续传）不再清空 ——
            //   它现在是 enabled 状态，这里列出它只是防止将来被误加回 Clear 名单。
            if (state is ImmersiveProgressState.Idle
                or ImmersiveProgressState.Failed or ImmersiveProgressState.Completed)
            {
                _particles.Clear();
            }
        }

        // ── Ripple：只由 Band↔粒子耦合触发，且必须通过冷却 ───────────────────────
        UpdateRipples(dt, quality);

        // ── Halo：向目标强度指数靠近（关闭时也平滑淡出）──────────────────────────
        var haloTarget = reducedMotion ? 0d : haloScale;
        var haloAlpha = 1d - Math.Exp(-dt / Math.Max(0.001d, ImmersiveProgressParameters.HaloFadeSeconds));
        _haloStrength += (haloTarget - _haloStrength) * haloAlpha;
        if (_haloStrength < 0.001d) _haloStrength = 0d;

        _lastHeadX = headX;
    }

    private void UpdateRipples(double dt, ImmersiveEffectsQuality quality)
    {
        var maxRipples = ImmersiveProgressParameters.ActiveRipples(quality);

        if (_rippleCooldown > 0d) _rippleCooldown -= dt;

        for (var i = 0; i < _ripples.Length; i++)
        {
            ref var r = ref _ripples[i];
            if (!r.Alive) continue;
            r.Life += (float)dt;
            if (r.Life >= r.MaxLife) r.Alive = false;
        }

        if (maxRipples <= 0) return;

        var alive = 0;
        for (var i = 0; i < _ripples.Length; i++)
        {
            if (_ripples[i].Alive) alive++;
        }

        if (alive >= maxRipples || _rippleCooldown > 0d) return;

        // 找一个"正被 Band 强烈照亮"的粒子（influence 超阈值）作为 Ripple 原点
        var span = _particles.Items;
        for (var i = 0; i < span.Length; i++)
        {
            ref readonly var p = ref span[i];
            if (!p.Alive || p.Influence < (float)ImmersiveProgressParameters.RippleInfluenceThreshold) continue;

            for (var k = 0; k < _ripples.Length; k++)
            {
                ref var slot = ref _ripples[k];
                if (slot.Alive) continue;
                slot.X = p.X;
                slot.Y = p.Y;
                slot.Life = 0f;
                slot.MaxLife = (float)(ImmersiveProgressParameters.RippleMinLife
                    + _random.NextDouble() * (ImmersiveProgressParameters.RippleMaxLife - ImmersiveProgressParameters.RippleMinLife));
                slot.Alive = true;
                _rippleCooldown = ImmersiveProgressParameters.RippleCooldownSeconds;
                return;
            }
        }
    }
}