using System;

namespace PCMig.WinUI.Presentation;

/// <summary>
/// ★ 2026-10-24（用户冻结的四态灯产品定义）★ **用户可见的源连接指示灯只有这四种视觉状态**：
///
///   · <see cref="Disconnected"/>  —— 红常亮：未连接源共享；
///   · <see cref="Connecting"/>    —— 黄呼吸：正在寻找共享、建立连接或重新连接（含"正在验证"）；
///   · <see cref="Connected"/>     —— 绿常亮：已连接源共享，当前没有实际数据传输；
///   · <see cref="Transferring"/>  —— 绿呼吸：已连接源共享，且确有数据传输活动。
///
/// ★ 纪律 ★ 这是**投影层**的枚举，不是诊断枚举：
///   · 禁止增加第五种颜色或灯光状态；
///   · 禁止把内部诊断状态（<see cref="SourceConnectionState"/> 的 Partial / SuspectOrUnknown）直接当灯色；
///   · 禁止因"某个子目录 ACL 读不到"把已经连上的源共享显示成黄色 —— 子路径错误留在扫描/预检/结果页/日志；
///   · 真正断开、连接中、已连接、正在传输一律由实测证据判定，不得伪造绿色。
/// 内部状态 → 四态的映射集中在 <c>ConnectionViewModel.RecomputeLamp</c> 一处，View 只做投影。
/// </summary>
public enum SourceLampState
{
    /// <summary>红常亮：未连接源共享（含"证据过期、尚未确认"的保守表达）。</summary>
    Disconnected = 0,

    /// <summary>黄呼吸：正在连接 / 重新连接 / 正在验证。</summary>
    Connecting = 1,

    /// <summary>绿常亮：已连接源共享，当前无数据传输。</summary>
    Connected = 2,

    /// <summary>绿呼吸：已连接源共享，且确有数据传输活动。</summary>
    Transferring = 3,
}