namespace PCMig.WinUI.Controls.ImmersiveProgress;

/// <summary>
/// ★ Round-3（执行书 §11 / §14）★ 一枚数据材质粒子。
/// <b>struct + 固定数组池</b>：每帧绝不 <c>new</c> 粒子、绝不 <c>List.Add/Remove</c> 抖动。
/// 粒子属于 <b>Progress Space</b>（坐标以进度胶囊为参照），不是屏幕空间乱飞。
/// </summary>
internal struct ImmersiveProgressParticle
{
    /// <summary>水平位置（DIP，相对胶囊左端）。</summary>
    public float X;

    /// <summary>垂直位置（DIP，相对胶囊垂直中心，正 = 向下）。</summary>
    public float Y;

    /// <summary>基础半径（DIP）。</summary>
    public float Radius;

    /// <summary>已存活时长（秒）。</summary>
    public float Life;

    /// <summary>总寿命（秒）：0.8~1.35。</summary>
    public float MaxLife;

    /// <summary>生成时的纵向相位（用于极缓的上下漂移，避免粒子排成一条线）。</summary>
    public float DriftPhase;

    /// <summary>当前帧受 Push Band 影响的程度（0~1）：只影响亮度与半径，不影响进度语义。</summary>
    public float Influence;

    /// <summary>是否在池中存活。</summary>
    public bool Alive;

    /// <summary>归一化寿命（0 = 刚生成，1 = 即将消失）。</summary>
    public readonly float NormalizedLife => MaxLife > 0f ? Math.Clamp(Life / MaxLife, 0f, 1f) : 0f;

    /// <summary>淡入淡出系数（出生 15% 淡入、死亡 30% 淡出）⇒ 不会"啪"地出现或消失。</summary>
    public readonly float Fade
    {
        get
        {
            var t = NormalizedLife;
            const float fadeIn = 0.15f;
            const float fadeOut = 0.30f;
            var a = t < fadeIn ? t / fadeIn : 1f;
            var b = t > 1f - fadeOut ? (1f - t) / fadeOut : 1f;
            var value = a < b ? a : b;
            return value < 0f ? 0f : value;
        }
    }

    /// <summary>当前绘制半径（含 Band 耦合放大：1 + 0.35 * influence）。</summary>
    public readonly float DrawRadius
        => Radius * (1f + (float)ImmersiveProgressParameters.ParticleRadiusGain * Influence);
}

/// <summary>
/// ★ Round-3（执行书 §11 / §14 / §25）★ 一枚极淡 Ripple（Push Band 经过粒子时的局部光学反馈）。
/// 「偶尔能看到」而不是持续水波：同时 ≤ 3~4、寿命 180~260 ms、峰值不透明度 ≤ 0.20。
/// </summary>
internal struct ImmersiveProgressRipple
{
    public float X;
    public float Y;
    public float Life;
    public float MaxLife;
    public bool Alive;
}

/// <summary>
/// ★ Round-3（执行书 §11 / §14 / §25）★ 固定容量的粒子池。
/// 语义边界：本类**只**表达"数据材质在流动"，它没有、也不允许有任何进度语义 ——
/// 没有任何 <c>Value / Percent / Bytes</c> 字段，也读不到业务对象（§17 / R28）。
/// </summary>
internal sealed class ImmersiveProgressParticlePool
{
    private readonly ImmersiveProgressParticle[] _items;
    private readonly Random _random;

    public ImmersiveProgressParticlePool(int capacity, int seed = 20261005)
    {
        _items = new ImmersiveProgressParticle[capacity > 0 ? capacity : 1];
        _random = new Random(seed);
    }

    /// <summary>池容量（固定）。</summary>
    public int Capacity => _items.Length;

    /// <summary>只读访问（Renderer 遍历用；返回 span 以避免任何分配）。</summary>
    public ReadOnlySpan<ImmersiveProgressParticle> Items => _items;

    /// <summary>当前存活数量（诊断 / 验收用）。只统计**已出生可见**的粒子（负寿命 = 延迟出生，不算）。</summary>
    public int ActiveCount
    {
        get
        {
            var count = 0;
            for (var i = 0; i < _items.Length; i++)
            {
                if (_items[i].Alive && _items[i].Life >= 0f) count++;
            }
            return count;
        }
    }

    /// <summary>全部清空（暂停 / 失败 / 换任务）。</summary>
    public void Clear()
    {
        for (var i = 0; i < _items.Length; i++) _items[i].Alive = false;
    }

    /// <summary>
    /// 当前存活粒子的横向范围（DIP）。**验收判据 ⑨ 用**：粒子必须始终留在 Progress Fill Capsule 内，
    /// 不允许跑到未完成区域或轨道之外。没有存活粒子时返回 false（此时无范围可言）。
    /// </summary>
    public bool TryActiveExtent(out float minX, out float maxX)
    {
        minX = float.MaxValue;
        maxX = float.MinValue;
        var any = false;
        for (var i = 0; i < _items.Length; i++)
        {
            ref readonly var p = ref _items[i];
            if (!p.Alive || p.Life < 0f) continue;
            any = true;
            var r = p.DrawRadius;
            var left = p.X - r;
            var right = p.X + r;
            if (left < minX) minX = left;
            if (right > maxX) maxX = right;
        }
        return any;
    }

    /// <summary>
    /// 推进一帧。纯数学，无分配。
    /// </summary>
    /// <param name="dt">帧间隔（秒，已夹住）。</param>
    /// <param name="headX">Head 的绝对 X（DIP）。</param>
    /// <param name="progressWidth">已完成的进度宽度（DIP）。</param>
    /// <param name="thickness">胶囊厚度（DIP）。</param>
    /// <param name="targetActive">目标存活数量（由特效档位决定）。</param>
    /// <param name="allowSpawn">是否允许生成（Running/Holding 才允许）。</param>
    /// <param name="bandCenterX">Push Band 中心 X（DIP）；用于光学耦合。</param>
    public void Update(float dt, float headX, float progressWidth, float thickness, int targetActive,
        bool allowSpawn, float bandCenterX)
    {
        // ★ Round-3 PHASE D 验收判据 ⑨ 修正 ★
        // 粒子只能落在 Progress Fill Capsule 里。胶囊边界必须按**最大可能绘制半径**预留：
        // DrawRadius = Radius * (1 + ParticleRadiusGain * Influence)，取上界 maxRadius * (1 + gain)
        // 才能保证 X ± DrawRadius 恒在 [0, progressWidth] 内（真机曾出现 pMin=-1.163 与 pw=0 时仍有 14 枚粒子）。
        var maxRadius = Math.Min((float)ImmersiveProgressParameters.ParticleMaxRadius, thickness * 0.30f);
        var extentRadius = maxRadius * (1f + (float)ImmersiveProgressParameters.ParticleRadiusGain);
        var capsuleLeft = extentRadius;
        var capsuleRight = progressWidth - extentRadius;

        // 连一枚最小粒子都放不下 ⇒ 当前**不存在**可承载粒子的胶囊，一个都不许存活（0% 时必须有 0 枚）。
        if (maxRadius <= 0f || capsuleRight - capsuleLeft < maxRadius)
        {
            Clear();
            return;
        }

        var trailLength = Math.Max((float)ImmersiveProgressParameters.ParticleTrailFloor,
            progressWidth * (float)ImmersiveProgressParameters.ParticleTrailRatio);
        var spawnEnd = Math.Min(headX - (float)ImmersiveProgressParameters.ParticleHeadGap, capsuleRight);
        var spawnStart = Math.Max(Math.Max(0f, headX - trailLength), capsuleLeft);
        var spawnWindow = spawnEnd - spawnStart;

        var alive = 0;
        for (var i = 0; i < _items.Length; i++)
        {
            ref var p = ref _items[i];
            if (!p.Alive) continue;

            p.Life += dt;
            if (p.Life >= p.MaxLife || spawnWindow <= 0f)
            {
                p.Alive = false;
                continue;
            }

            // Head 前进会"轻微拖动"粒子：粒子不完全静止在进度空间里，但绝不因此表达额外进度
            p.X += (headX - p.X) * (float)ImmersiveProgressParameters.ParticleFollowFactor * dt;
            p.Y = MathF.Sin(p.DriftPhase + p.Life * 1.7f) * (thickness * 0.22f);

            var distance = MathF.Abs(p.X - bandCenterX);
            var sigma = (float)ImmersiveProgressParameters.BandInfluenceSigma;
            p.Influence = (float)Math.Exp(-(distance * distance) / (2f * sigma * sigma));

            // 夹进出生窗口（其本身已被胶囊边界夹过）⇒ 存活粒子的绘制范围永不越界
            if (p.X > spawnEnd) p.X = spawnEnd;
            if (p.X < spawnStart) p.X = spawnStart;
            alive++;
        }

        if (!allowSpawn) return;

        // 补齐到目标数量：只在"出生区间"里生成，且必须落在胶囊内
        var wanted = Math.Clamp(targetActive, 0, _items.Length);
        for (var i = 0; i < _items.Length && alive < wanted; i++)
        {
            ref var p = ref _items[i];
            if (p.Alive) continue;

            var x = spawnStart + (float)_random.NextDouble() * spawnWindow;
            var minRadius = Math.Min((float)ImmersiveProgressParameters.ParticleMinRadius, maxRadius);
            p.X = x;
            p.Y = 0f;
            p.Radius = minRadius + (float)_random.NextDouble() * Math.Max(0f, maxRadius - minRadius);
            p.Life = -(float)_random.NextDouble() * 0.20f;   // 负寿命 = 延迟出生，避免同帧齐闪
            p.MaxLife = (float)(ImmersiveProgressParameters.ParticleMinLife
                + _random.NextDouble() * (ImmersiveProgressParameters.ParticleMaxLife - ImmersiveProgressParameters.ParticleMinLife));
            p.DriftPhase = (float)(_random.NextDouble() * Math.PI * 2d);
            p.Influence = 0f;
            p.Alive = true;
            alive++;
        }
    }
}