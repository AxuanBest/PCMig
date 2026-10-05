using PCMig.Diagnostics.Abstractions;

namespace PCMig.Core.Transfer;

/// <summary>
/// 暂停的 SLA（Trust-Critical Recovery FIX BATCH 1，§4）。
///
/// 为什么必须是"秒级常量"而不是"够用就行"：
///   · 旧 Diagnostics 的 pause.v1 契约里，外部等待上限是 <c>ExternalWaitTimeoutMs = 3_600_000</c>（1 小时），
///     于是"暂停请求已写入"可以在 1 小时内被当成"仍在等待"，永远不会变成失败——真机上 8/8 次点击、0 次引擎确认，
///     事件卡仍是 0、健康仍是 healthy。
///   · SLA 必须**同时**是引擎判据、Diagnostics 判据和 UI 文案判据（同一个真值），
///     否则"UI 说正在暂停 / 诊断说健康 / 引擎还在全速传"三者会再次分叉。
///
/// 数值口径：
///   · <see cref="AchieveSeconds"/> = 5 s：正常路径必须在此之内达成"worker 已停止"。
///   · <see cref="HardFailSeconds"/> = 10 s：到此仍未停住 ⇒ 如实报 PauseFailed（迁移仍在进行），绝不谎称已暂停。
///   · <see cref="RequestPollMs"/> = 250 ms：请求轮询间隔（1 s 太钝，按钮点下去的反馈会明显迟滞）。
///   · <see cref="QuiescentConfirmMs"/> = 5 s：验收用的"目标字节静止"观察窗口（数据安全判据，不是引擎判据）。
///
/// ★ FIX BATCH 3 ★：秒级数值本身搬到契约层 <see cref="ActionSla"/>（诊断不得反向依赖 Core，
/// 但诊断必须用同一套期限判"兑现"）。这里只做**引用**，不再保存第二份数字——单点真值。
/// </summary>
public static class PauseSla
{
    /// <summary>正常达成窗口（秒）。</summary>
    public const int AchieveSeconds = ActionSla.PauseAchieveSeconds;

    /// <summary>硬失败窗口（秒）：超过即 PauseFailed。</summary>
    public const int HardFailSeconds = ActionSla.PauseHardFailSeconds;

    /// <summary>pause.request 轮询间隔（毫秒）。</summary>
    public const int RequestPollMs = 250;

    /// <summary>"目标字节连续静止"的确认窗口（毫秒）——Pause 用例的数据安全判据。</summary>
    public const int QuiescentConfirmMs = 5_000;

    public static readonly TimeSpan Achieve = TimeSpan.FromSeconds(AchieveSeconds);
    public static readonly TimeSpan HardFail = TimeSpan.FromSeconds(HardFailSeconds);
    public static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(RequestPollMs);
    public static readonly TimeSpan Quiescent = TimeSpan.FromMilliseconds(QuiescentConfirmMs);
}