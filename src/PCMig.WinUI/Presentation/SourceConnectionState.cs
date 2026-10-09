namespace PCMig.WinUI.Presentation;

/// <summary>
/// PCMig 当前**源共享连接状态**（★ 2026-10-08 真机问题 4 ★）。
///
/// 【为什么需要它】改前底栏那颗点是硬编码的 <c>Fill="{StaticResource SuccessBrush}"</c> ——
/// 无论有没有连上旧电脑、有没有可访问的共享，它永远是绿的，等于没有信息量。
/// 它当时唯一说得通的解释是"Windows 有没有互联网"，而那不是 PCMig 该表达的事。
/// 它现在表达的唯一事实是：**PCMig 到旧电脑的 SMB 源共享是否可用**（不是 ICMP、不是网卡 UP、不是能上网）。
///
/// 【状态源纪律】本枚举由 <c>ConnectionViewModel</c> 拥有并**单点派生**（见其 <c>UpdateSourceState</c>），
/// View 只投影、绝不自己猜（原有缺陷的根因就是"View 里写死了一个颜色"）。
/// </summary>
public enum SourceConnectionState
{
    /// <summary>
    /// 未连接 / 连接失败 / 运行中源失效 / 没有任何已验证可访问的源共享。
    /// 表现：底栏指示灯**常亮红色，不闪烁**。
    /// </summary>
    Disconnected,

    /// <summary>
    /// 正在连接 / 正在探测：点击"连接并列出共享"、WNet 建立 SMB 会话、枚举共享、验证手工共享。
    /// 表现：底栏指示灯**琥珀色柔和呼吸**（Composition 动画，不是生硬的开/关闪烁）。
    /// </summary>
    Connecting,

    /// <summary>
    /// 至少存在一个**当前已验证可访问**的源共享。
    /// 表现：底栏指示灯**稳定常亮绿色**。
    /// </summary>
    Connected,
}