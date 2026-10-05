namespace PCMig.WinUI.Controls.ImmersiveProgress;

/// <summary>
/// ★ Round-3 视觉纠偏（PMML-R33 Hero / Compact Material Family）★ 同一个控件的两种**尺寸变体**。
///
/// 它们**共用**同一套 Capsule 几何语义、同一个 Track-Space Chroma Field、同一个 Head 体积光场、
/// 同一个 <c>VisualProgress</c>。差异**只允许**在：几何尺寸 token 与装饰层强度/开关。
/// 绝不允许 Compact 退回旧的"固定蓝 Rectangle"路线。
/// </summary>
public enum ImmersiveProgressVariant
{
    /// <summary>Step3 主进度条：16 DIP 槽 / 12 DIP 材质 / 半径 6，装饰全开。</summary>
    Hero = 0,

    /// <summary>底栏轻量条：14 DIP 槽 / 10 DIP 材质 / 半径 5。保留 Capsule、Chroma、Head Halo 与单道 Push Band（强度约 Hero 的 55~65%）；关闭粒子与 Ripple（不抢主视觉）。</summary>
    Compact = 1,
}

/// <summary>
/// ★ Round-3（执行书 §19）★ PCMig Immersive Transfer Progress 的**业务状态机**。
/// 状态只表达"引擎/任务此刻处于什么阶段"，装饰层据此决定画什么；
/// 反过来任何装饰层都**不得**修改 Value / Maximum / Percent / 回执 / 作业状态。
/// </summary>
public enum ImmersiveProgressState
{
    /// <summary>尚未开始（没有任务）。Head 在 0，无动态。</summary>
    Idle = 0,

    /// <summary>正在准备（校验、生成计划）。Head 在 0，可有极弱活性。</summary>
    Preparing = 1,

    /// <summary>正在传输：Head 追 VisualProgress，Band/粒子/Ripple/Halo 全开。</summary>
    Running = 2,

    /// <summary>
    /// 任务活着但真值暂时不动（网络停滞 / 大文件长时间无新确认字节）。
    /// Head 必须**固定**不动，Band 继续周期运行、粒子低速、Ripple 偶发 —— 表达"后台仍在工作"。
    /// </summary>
    Holding = 3,

    /// <summary>正在暂停（用户已点暂停，引擎尚未确认）：Head 保持可信值，Band 减速渐弱。</summary>
    Pausing = 4,

    /// <summary>已暂停：Head 冻结，Band 停，粒子淡出，Halo 降低。</summary>
    Paused = 5,

    /// <summary>正在停止（用户已点停止，引擎尚未确认）：与 Pausing 类似但更快收尾。</summary>
    Stopping = 6,

    /// <summary>已中断（可续传）：Head 冻结在可信高水位，装饰停止，等待 Resume。</summary>
    Interrupted = 7,

    /// <summary>正在校验（不再有"传输"语义）：不画 Band，最多留很弱 Halo。</summary>
    Verifying = 8,

    /// <summary>已完成：只有引擎真的 Completed 后才允许到 100%，短暂 settle 后所有动态收尾。</summary>
    Completed = 9,

    /// <summary>有警告但仍在推进（例如个别对象重试）：与 Running 相同但 Halo 偏暖。</summary>
    Warning = 10,

    /// <summary>已失败：Band 停、粒子淡出、Head 保持最后可信值，切错误语义。</summary>
    Failed = 11,
}

/// <summary>
/// ★ Round-3（执行书 §20）★ 特效质量档。PCMig 会跑在办公机 / 核显 / VM / Horizon / RDP 上，
/// **不能按独显设计**：档位只改变装饰的数量与强度，**绝不改变任何业务值**。
/// </summary>
public enum ImmersiveEffectsQuality
{
    /// <summary>高质量：粒子 12~14、Ripple 开、PushBand 双层、Halo Full。桌面独显默认。</summary>
    High = 0,

    /// <summary>均衡：粒子 8~10、Ripple ≤2、PushBand 双层但更弱、Halo Medium。远程会话/核显默认。</summary>
    Balanced = 1,

    /// <summary>精简：粒子 3~5、Ripple 关、PushBand 保留（它是"活着"的唯一信号）、Halo Low、不用重 Blur。</summary>
    Reduced = 2,
}

/// <summary>
/// ★ Round-3（执行书 §10 / §11 / §22）★ 全部视觉参数**唯一登记处**。
/// 铁律：Renderer / 控件 / 动画状态里**不允许**出现散落的魔法数字；
/// 任何想调参的人都只应该改这里（或按 §10 改 XAML token）。
/// 颜色不在这里 —— 颜色必须来自 Theme Resource（§22），由控件解析后传入 Renderer。
/// </summary>
internal static class ImmersiveProgressParameters
{
    // ── §10 尺寸 token（XAML 侧同名 token 见 Themes\Materials.xaml）────────────────
    /// <summary>布局槽高（DIP）：控件在布局里占这么高，上下各 2 DIP 是"呼吸空间"。</summary>
    public const double HostHeight = 16d;

    /// <summary>真正动态材质的厚度（DIP）。</summary>
    public const double Thickness = 12d;

    /// <summary>端帽圆角（DIP）= Thickness/2 ⇒ 细长胶囊。</summary>
    public const double Radius = 6d;

    // ── ★ Round-3 视觉纠偏（R33）★ Compact 变体几何 token ──────────────────────
    /// <summary>底栏 Compact 布局槽高（DIP）。★ 执行书 §5.2 定为 12。★</summary>
    public const double CompactHostHeight = 12d;

    /// <summary>底栏 Compact 材质厚度（DIP）。</summary>
    public const double CompactThickness = 10d;

    /// <summary>底栏 Compact 端帽圆角（DIP）= CompactThickness/2 ⇒ 仍是真胶囊。</summary>
    public const double CompactRadius = 5d;

    /// <summary>Compact 的 Push Band 强度相对 Hero 的比例（0.50~0.65）：Footer 不抢主视觉。</summary>
    public const double CompactBandScale = 0.58d;

    /// <summary>Compact 是否绘制粒子（R33：关闭 —— Footer 不是主视觉）。</summary>
    public const bool CompactParticlesEnabled = false;

    /// <summary>Compact 是否绘制 Ripple（R33：关闭）。</summary>
    public const bool CompactRipplesEnabled = false;

    /// <summary>取某个变体的布局槽高（DIP）。</summary>
    public static double HostHeightFor(ImmersiveProgressVariant variant)
        => variant == ImmersiveProgressVariant.Compact ? CompactHostHeight : HostHeight;

    /// <summary>取某个变体的材质厚度（DIP）。</summary>
    public static double ThicknessFor(ImmersiveProgressVariant variant)
        => variant == ImmersiveProgressVariant.Compact ? CompactThickness : Thickness;

    /// <summary>取某个变体的端帽圆角（DIP）。</summary>
    public static double RadiusFor(ImmersiveProgressVariant variant)
        => variant == ImmersiveProgressVariant.Compact ? CompactRadius : Radius;

    // ── §11 Push Band（一次只能有一道；柔软双层光压；连续减速）────────────────────
    /// <summary>外层光压的水平高斯口径（DIP）。</summary>
    public const double BandSigmaX = 30d;

    /// <summary>亮芯的水平高斯口径（DIP）。</summary>
    public const double BandCoreSigmaX = 10d;

    /// <summary>外层光压的垂直口径（DIP）。</summary>
    public const double BandOuterSigmaY = 5.5d;

    /// <summary>外层光压峰值不透明度。</summary>
    public const double BandOuterOpacity = 0.24d;

    /// <summary>亮芯峰值不透明度。</summary>
    public const double BandCoreOpacity = 0.20d;

    /// <summary>一个完整周期（秒）：1.20~1.45 s。</summary>
    public const double BandCycleSeconds = 1.32d;

    /// <summary>周期内的活动段（秒）：0.90~1.05 s；其余为静止段，保证"一次只有一道"。</summary>
    public const double BandActiveSeconds = 0.96d;

    /// <summary>Band 起点相对 Head 的后退距离（DIP）：从后方向 Head 推进。</summary>
    public const double BandTravelLength = 150d;

    // ── ★ Round-3 视觉纠偏（R34 Volumetric Head Halo）★ Head 体积光场 ──────────────
    // 纪律：Head 的活动高光**只能**由连续衰减的局部体积光场表达。
    // 禁止可辨识的 Stroke / Rim / Outline 作为主要 Head Glow —— 这就是本轮修掉的"一圈白线"。

    /// <summary>光场中心相对 Head 的**向后**偏移（DIP）：3~6，让光从材质内部溢出来。</summary>
    public const double HeadHaloCenterBackOffset = 4d;

    /// <summary>外层体积光的横向半径（DIP）：20~30。</summary>
    public const double HeadHaloOuterRadiusX = 24d;

    /// <summary>外层体积光的纵向半径（DIP）：7~9（略大于半厚度 ⇒ 允许轻微 bloom 越出胶囊）。</summary>
    public const double HeadHaloOuterRadiusY = 8d;

    /// <summary>外层体积光峰值不透明度：0.10~0.18。</summary>
    public const double HeadHaloOuterOpacity = 0.16d;

    /// <summary>内层体积光的横向半径（DIP）：8~14。</summary>
    public const double HeadHaloInnerRadiusX = 11d;

    /// <summary>内层体积光的纵向半径（DIP）：4~6。</summary>
    public const double HeadHaloInnerRadiusY = 5d;

    /// <summary>内层体积光峰值不透明度：0.15~0.25。</summary>
    public const double HeadHaloInnerOpacity = 0.22d;

    // ── ★ 执行书 §9.2（2026-10-05 视觉第二轮）★ Head Halo 至少三层 ────────────────
    //   Layer A = Arc-attached Inner Glow（贴 Head 圆弧、纯白、三层里最亮、范围最小）
    //   Layer B = Soft Outer Bloom（向 Head 后方/上方/下方扩散，连续衰减、无边界）
    //   Layer C = Cyan Support Glow（很轻的青色，用于与 Chroma 材质融合，即上面的 Outer 层）
    //   目的：让光"沿圆弧附着并向外融化"，而不是一块能看出边界的椭圆贴纸。

    /// <summary>Layer A（白芯）横向半径（DIP）：6~9。</summary>
    public const double HeadHaloArcRadiusX = 7d;

    /// <summary>Layer A（白芯）纵向半径（DIP）：4~5.5。</summary>
    public const double HeadHaloArcRadiusY = 4.5d;

    /// <summary>Layer A 峰值不透明度：0.22~0.32（三层里最亮）。</summary>
    public const double HeadHaloArcOpacity = 0.28d;

    /// <summary>Layer A 中心相对 Head 的后退偏移（DIP）：1.5~3（比外层更贴近圆弧）。</summary>
    public const double HeadHaloArcBackOffset = 2d;

    /// <summary>Layer B（柔光团）横向半径（DIP）：13~18。</summary>
    public const double HeadHaloBloomRadiusX = 15d;

    /// <summary>Layer B（柔光团）纵向半径（DIP）：5~7。</summary>
    public const double HeadHaloBloomRadiusY = 6d;

    /// <summary>Layer B 峰值不透明度：0.10~0.18。</summary>
    public const double HeadHaloBloomOpacity = 0.14d;

    /// <summary>Layer B 中心相对 Head 的后退偏移（DIP）：3~5。</summary>
    public const double HeadHaloBloomBackOffset = 3.5d;

    // ── ★ Round-3 视觉纠偏（R31/R32）★ 几何与材质开关 ─────────────────────────
    /// <summary>胶囊几何重建阈值（DIP）：进度宽度变化超过它才重建 Geometry（既不每帧分配，也不留错几何）。</summary>
    public const double GeometryRebuildThreshold = 0.5d;

    /// <summary>Fill 纵向材质修饰的额外不透明度（0~0.16）。色彩主导必须来自横向 Chroma Field。</summary>
    public const double FillShadeOpacity = 0.12d;

    // ── §11 粒子（属于 Progress Space；固定池；生产默认 ≤14）──────────────────────
    /// <summary>粒子池容量（固定数组，绝不在每帧创建对象）。</summary>
    public const int ParticlePool = 16;

    /// <summary>High 档活动粒子数上限：12~14（**不要 24~28**）。</summary>
    public const int ParticleActiveHigh = 14;

    /// <summary>Balanced 档活动粒子数上限。</summary>
    public const int ParticleActiveBalanced = 9;

    /// <summary>Reduced 档活动粒子数上限。</summary>
    public const int ParticleActiveReduced = 4;

    public const double ParticleMinRadius = 0.55d;
    public const double ParticleMaxRadius = 1.20d;
    public const double ParticleMinLife = 0.8d;
    public const double ParticleMaxLife = 1.35d;

    /// <summary>粒子位置跟随 Head 前进的比例（0.18~0.28）：Head 前进时会"轻微拖动"粒子。</summary>
    public const double ParticleFollowFactor = 0.23d;

    /// <summary>生成区间长度下限（DIP）= max(64, ProgressWidth * 0.18)。</summary>
    public const double ParticleTrailFloor = 64d;

    /// <summary>生成区间长度相对进度宽度比例。</summary>
    public const double ParticleTrailRatio = 0.18d;

    /// <summary>生成区间相对 Head 的最小留白（DIP）：粒子不得贴着 Head 诞生。</summary>
    public const double ParticleHeadGap = 6d;

    // ── §11 Ripple（极稀疏反馈，绝不能成为主视觉）────────────────────────────────
    public const int RippleMax = 4;
    public const double RippleStartRadius = 1.5d;
    public const double RippleEndRadius = 4.5d;
    public const double RippleMinLife = 0.18d;
    public const double RippleMaxLife = 0.26d;
    public const double RipplePeakOpacity = 0.20d;

    /// <summary>同一位置触发 Ripple 的最小冷却（秒）。</summary>
    public const double RippleCooldownSeconds = 0.22d;

    // ── §11 Band ↔ 粒子光学耦合 ────────────────────────────────────────────────
    /// <summary>耦合高斯口径（DIP）：14~22。</summary>
    public const double BandInfluenceSigma = 18d;

    /// <summary>受 Band 影响时亮度增加比例：+0.25~0.35 * influence。</summary>
    public const double ParticleBrightnessGain = 0.30d;

    /// <summary>受 Band 影响时半径放大比例：1 + 0.25~0.45 * influence。</summary>
    public const double ParticleRadiusGain = 0.35d;

    /// <summary>触发 Ripple 所需的最小 influence。</summary>
    public const double RippleInfluenceThreshold = 0.55d;

    // ── §15 视觉时间线（与 ProgressPresentationCoordinator 同一套常数）────────────
    /// <summary>指数滤波器增益（每秒）。必须与呈现协调器的 <c>VisualK</c> 一致。</summary>
    public const double VisualK = 10d;

    /// <summary>单拍时间步长上限（秒）= 1/30，防止窗口恢复后一步跳完。</summary>
    public const double MaxStepSeconds = 1d / 30d;

    // ── 杂项 ─────────────────────────────────────────────────────────────────
    /// <summary>粒子生成位置的随机纵向抖动比例（相对半径）。</summary>
    public const double SpawnJitter = 0.35d;

    /// <summary>Halo 从无到有的最短响应时长（秒）。</summary>
    public const double HaloFadeSeconds = 0.18d;

    /// <summary>Paused 之后粒子完全淡出的时长（秒）：200~400 ms。</summary>
    public const double PauseFadeSeconds = 0.28d;

    /// <summary>当前档位下的活动粒子上限。</summary>
    public static int ActiveParticles(ImmersiveEffectsQuality quality)
        => quality switch
        {
            ImmersiveEffectsQuality.High => ParticleActiveHigh,
            ImmersiveEffectsQuality.Balanced => ParticleActiveBalanced,
            _ => ParticleActiveReduced,
        };

    /// <summary>当前档位下 Ripple 的同时上限（Reduced 档直接关闭）。</summary>
    public static int ActiveRipples(ImmersiveEffectsQuality quality)
        => quality switch
        {
            ImmersiveEffectsQuality.High => RippleMax,
            ImmersiveEffectsQuality.Balanced => 2,
            _ => 0,
        };

    /// <summary>
    /// Push Band 是否运行。
    /// ★ 执行书 R35（2026-10-05 视觉第二轮）★ Paused / Interrupted 也要**继续跑**：
    ///   Pause ≠ Failed ≠ Dead —— 它表示"任务暂时停住但依然可恢复、依然活着"，
    ///   因此事实（Head / Percent / Bytes）冻结，但材质活性必须保留（只是降速降亮）。
    ///   Reduced 档仍保留 Band（它是"任务活着"的唯一信号）。
    /// </summary>
    public static bool BandEnabled(ImmersiveProgressState state, bool reducedMotion)
    {
        if (reducedMotion) return false;
        return state is ImmersiveProgressState.Running
            or ImmersiveProgressState.Holding
            or ImmersiveProgressState.Warning
            or ImmersiveProgressState.Preparing
            or ImmersiveProgressState.Paused           // ★ R35 ★
            or ImmersiveProgressState.Interrupted;     // ★ R35 ★
    }

    /// <summary>
    /// 粒子是否运行。
    /// ★ R35 ★ Paused / Interrupted 保留粒子（数量与速度按 <see cref="ParticleCountScale"/> /
    /// <see cref="ParticleSpeedScale"/> 降到约一半），Failed / Completed 才真正熄灭。
    /// </summary>
    public static bool ParticlesEnabled(ImmersiveProgressState state, bool reducedMotion)
        => !reducedMotion && (state is ImmersiveProgressState.Running
            or ImmersiveProgressState.Holding
            or ImmersiveProgressState.Warning
            or ImmersiveProgressState.Pausing
            or ImmersiveProgressState.Paused            // ★ R35 ★
            or ImmersiveProgressState.Interrupted);     // ★ R35 ★

    /// <summary>★ R35 ★ 可恢复的"停住但活着"状态（事实冻结、材质保留）。</summary>
    public static bool IsRecoverableAlive(ImmersiveProgressState state)
        => state is ImmersiveProgressState.Paused or ImmersiveProgressState.Interrupted;

    /// <summary>★ R35 ★ Push Band 推进速度倍率：Paused 降到约 0.5（等效周期拉长约 2 倍）。</summary>
    public static double BandSpeedScale(ImmersiveProgressState state)
        => state switch
        {
            ImmersiveProgressState.Paused or ImmersiveProgressState.Interrupted => 0.5d,
            ImmersiveProgressState.Pausing or ImmersiveProgressState.Stopping => 0.75d,
            ImmersiveProgressState.Completed => 0.6d,
            _ => 1d,
        };

    /// <summary>★ R35 ★ Push Band 亮度倍率：Paused 降到约 0.55。</summary>
    public static double BandBrightnessScale(ImmersiveProgressState state)
        => state switch
        {
            ImmersiveProgressState.Paused or ImmersiveProgressState.Interrupted => 0.55d,
            ImmersiveProgressState.Pausing or ImmersiveProgressState.Stopping => 0.7d,
            ImmersiveProgressState.Completed => 0.45d,
            _ => 1d,
        };

    /// <summary>★ R35 ★ 粒子数量倍率：Paused 降到约 0.5；Failed / Completed 归零（熄灭）。</summary>
    public static double ParticleCountScale(ImmersiveProgressState state)
        => state switch
        {
            ImmersiveProgressState.Paused or ImmersiveProgressState.Interrupted => 0.5d,
            ImmersiveProgressState.Pausing => 0.6d,
            ImmersiveProgressState.Completed or ImmersiveProgressState.Failed => 0d,
            _ => 1d,
        };

    /// <summary>★ R35 ★ 粒子时间流速倍率：Paused 降到约 0.5（看起来更慢、更"休眠"）。</summary>
    public static double ParticleSpeedScale(ImmersiveProgressState state)
        => state switch
        {
            ImmersiveProgressState.Paused or ImmersiveProgressState.Interrupted => 0.5d,
            ImmersiveProgressState.Pausing => 0.7d,
            _ => 1d,
        };

    /// <summary>Head 是否允许前进（其余状态一律冻结 —— §18 Progress Head = Fact）。</summary>
    public static bool HeadAdvances(ImmersiveProgressState state)
        => state is ImmersiveProgressState.Running
            or ImmersiveProgressState.Warning
            or ImmersiveProgressState.Completed;

    /// <summary>Halo 强度系数（0 = 完全关闭）。</summary>
    public static double HaloScale(ImmersiveProgressState state, ImmersiveEffectsQuality quality)
    {
        var baseScale = quality switch
        {
            ImmersiveEffectsQuality.High => 1.0d,
            ImmersiveEffectsQuality.Balanced => 0.7d,
            _ => 0.4d,
        };
        var stateScale = state switch
        {
            ImmersiveProgressState.Running or ImmersiveProgressState.Warning => 1.0d,
            ImmersiveProgressState.Holding => 0.85d,
            ImmersiveProgressState.Preparing => 0.5d,
            // ★ R35 ★ Paused/Interrupted 是"停住但活着"：Halo 必须保留（只是略降），
            //   从原来的 0.22 提到 0.60 —— 之前那个值在视觉上等于把材质熄灭了。
            ImmersiveProgressState.Paused or ImmersiveProgressState.Interrupted => 0.60d,
            ImmersiveProgressState.Pausing or ImmersiveProgressState.Stopping => 0.70d,
            ImmersiveProgressState.Verifying => 0.30d,
            ImmersiveProgressState.Completed => 0.30d,
            ImmersiveProgressState.Failed => 0.15d,
            _ => 0.0d,
        };
        return baseScale * stateScale;
    }
}

// ★ Round-3 PHASE D（可测试性分层）★
// 调色板记录 ImmersiveProgressPalette 已移到 ImmersiveTransferProgressRenderer.cs ——
// 它带 Windows.UI.Color（WinRT 类型），留在本文件会让本文件无法被 net8.0 的测试项目链入编译。
// 本文件因此保持**纯 System.***（常量 + 纯函数）⇒ 粒子池的边界可由真实数字回归测试锁死
// （tests\PCMig.Core.Tests\ImmersiveProgressParticleBoundsTests.cs），而不是只做源码文本契约。