namespace PCMig.WinUI.Presentation;

/// <summary>
/// ★ FIX BATCH 6（指令 §9）★ 状态消息的**语义通道**。
///
/// 背景（真机 P2-G/H）：整会话此前只有一根 <c>StatusMessage</c>，业务运行状态、用户操作引导、
/// 安全提示、错误摘要、当前对象状态全部挤在同一个字符串里，并被绑定到右侧执行卡——
/// 结果是长状态句把执行卡无限撑高，而左侧「提示」面板与「连接与安全提示」卡却只显示冻结文案。
///
/// 现在按语义分成五条通道，唯一写入者仍是 <see cref="MigrationSessionViewModel"/>
/// （Engine → Session/ViewModel → 语义属性 → UI 面板；UI 不自己拼业务状态）：
/// <list type="bullet">
///   <item><see cref="OperationalStatus"/> 运行/流程状态（引擎与流程的真值）→ 左侧提示面板</item>
///   <item><see cref="UserHint"/> 用户下一步该做什么（可执行引导）→ 左侧提示面板</item>
///   <item><see cref="SecurityHint"/> 凭据 / 共享 / 安全相关提示 → 「连接与安全提示」卡</item>
///   <item><see cref="ErrorSummary"/> 失败、被拦下、异常的人类可读摘要 → 左侧提示面板</item>
///   <item><see cref="CurrentObjectStatus"/> 当前对象（引擎真值，来自 ProgressSnapshot）→ 左侧提示面板</item>
/// </list>
/// </summary>
public enum StatusChannel
{
    /// <summary>运行/流程状态：引擎与流程的真值（正在预检 / 计划已生成 / 正在暂停 / 已暂停 …）。</summary>
    OperationalStatus = 0,

    /// <summary>用户操作引导：下一步该做什么（请先… / 不能… / 尚无…）。</summary>
    UserHint = 1,

    /// <summary>安全相关：凭据、账号、共享可用性。</summary>
    SecurityHint = 2,

    /// <summary>错误摘要：失败 / 被拦下 / 异常的人类可读说明。</summary>
    ErrorSummary = 3,

    /// <summary>当前对象状态：由引擎进度快照携带的真实对象标识与路径。</summary>
    CurrentObjectStatus = 4,
}