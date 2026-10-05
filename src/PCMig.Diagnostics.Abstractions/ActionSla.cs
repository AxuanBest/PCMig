namespace PCMig.Diagnostics.Abstractions;

/// <summary>
/// ★ FIX BATCH 3 / §6 ★ 用户动作的 SLA 契约（**契约层，唯一真值**）。
///
/// 为什么这些常量住在契约层而不是 PCMig.Core：
///   · PCMig.Diagnostics 只引用 Abstractions（架构硬约束：诊断不得反向依赖业务层），
///     而诊断必须用**和引擎同一套**期限来判"业务效果是否在承诺时间内达成"；
///   · 若两边各写一份常量，就会出现"引擎按 10 s 判失败、诊断按 1 小时判正常"的假绿
///     （这正是 200+ GB 真机事故里"暂停从没生效、诊断却是 0 事件卡 + healthy"的成因之一）。
///
/// 纪律：引擎判据、诊断判据、UI 文案判据必须是**同一个**数字。任何一侧想改口径，
/// 只能改这里（Core 的 PauseSla 与 WinUI 的等待预算都直接引用本类型）。
/// </summary>
public static class ActionSla
{
    /// <summary>暂停必须在此时间内**真正停住**（正常 SLA）：5 秒。</summary>
    public const int PauseAchieveSeconds = 5;

    /// <summary>超过此时限仍停不住即为**硬失败**：10 秒。此后引擎必须自报 PauseFailed。</summary>
    public const int PauseHardFailSeconds = 10;

    /// <summary>
    /// 诊断侧的兑现宽限：引擎自己会在 <see cref="PauseHardFailSeconds"/> 内给出结论
    /// （PauseObserved 或 PauseFailed），再给诊断一点余量，用于覆盖"连 PauseFailed 都没来"
    /// 的沉默故障（进程被杀、引擎卡死）。
    /// </summary>
    public const int PauseFulfillmentGraceMs = 2_000;

    /// <summary>
    /// 暂停承诺的兑现期限（毫秒）= 硬失败 SLA + 诊断宽限。超过它仍未见"兑现"或"失败"，
    /// 诊断必须判定"用户动作未兑现"（降级 verdict + 事件卡），而不是"还没等到"。
    /// </summary>
    public const int PauseFulfillmentDeadlineMs = PauseHardFailSeconds * 1000 + PauseFulfillmentGraceMs;

    /// <summary>
    /// 恢复动作的兑现期限（最长等待时间）。恢复的达标真值 = 引擎离开 Paused（回到 Running）。
    /// 就地恢复只需请求文件被清除后引擎走出暂停循环，通常在一秒内；这里给 30 s 天花板。
    /// </summary>
    public const int ResumeSettleDeadlineMs = 30_000;

    /// <summary>
    /// 停止动作的兑现期限（最长等待时间）。停止 = 取消令牌 + 终止当前 worker，
    /// 达标真值 = 运行真的收尾（不再 Running），大对象被杀后通常几秒内收尾；这里给 60 s 天花板。
    /// </summary>
    public const int StopSettleDeadlineMs = 60_000;
}