namespace PCMig.Core.Transfer;

/// <summary>
/// ★ v0.5.4 Preview.2（P1-A，2026-10-15）★ A 类"运行中但无可确认中途字节"的**纯逻辑判据与文案**。
///
/// 为什么放在 Core（而不是留在 WinUI 的 PageReadiness 里）：
///   仓库的测试项目只有 Core / Diagnostics / Cli 三个，没有 WinUI 测试宿主。把判据与文案抽成
///   纯函数，就能在现有 xUnit 项目里**直接断言文案本身**，而不必为了可测性去打破 WinUI 分层
///   或让测试项目引用 XAML 表示层。UI 侧只做"取结果 + 赋给 TextBlock"。
///
/// 证据强度纪律（本类型的核心约束，改文案前必须读）：
///   · 本类型**只能**陈述"当前没有已确认的中途字节"这一事实；
///   · **不得**由"缺数据"反推"仍在传输"，**不得**断言"不是卡死"，**不得**保证"一定会结算" ——
///     对象可能失败、被中断或验证不通过，这些都不在本判据的可知范围内；
///   · 判据只读引擎已确认的来源位，**不**引入计时器、插值、网卡速率换算或任何伪造增长。
/// </summary>
public static class HonestActivityState
{
    /// <summary>
    /// 是否应当显示"诚实缺源"状态句。
    /// </summary>
    /// <param name="isRunning">会话是否处于运行中（由调用方从业务状态取得）。</param>
    /// <param name="isPaused">是否已暂停（暂停有独立文案，必须优先）。</param>
    /// <param name="hasTruthFrame">是否收到过至少一帧真值（未收到 ⇒ 不臆造状态）。</param>
    /// <param name="hasTrustedInflightSource">
    /// 本帧是否存在**已确认的中途字节来源**（如进程 I/O 计数器）。true ⇒ 不得显示缺源文案。</param>
    /// <returns>true 表示应显示缺源文案；false 表示退回常规文案。</returns>
    public static bool ShouldShowUnavailableProgress(
        bool isRunning,
        bool isPaused,
        bool hasTruthFrame,
        bool hasTrustedInflightSource)
    {
        if (!isRunning) return false;             // 已中断/已完成/失败/取消 ⇒ 绝不说"运行中"
        if (isPaused) return false;               // 暂停有专属文案，优先级更高
        if (!hasTruthFrame) return false;         // 从未收到真值 ⇒ 不臆造
        return !hasTrustedInflightSource;         // 有可信来源 ⇒ 不显示缺源
    }

    /// <summary>
    /// 生成诚实状态句。
    ///
    /// 措辞刻意保持"不超过证据强度"：
    ///   · 主句只声明"运行中 + 暂无可确认的中途字节进度"；
    ///   · 括注解释**为什么**读不到（/Z、/J 会预分配目标长度），这是机制事实；
    ///   · 结尾明确把判断权交回用户（"请结合活动状态或诊断信息确认"），
    ///     并说明进度"可能"在下次可信结算后更新 —— 用"可能"而非"一定"。
    /// </summary>
    /// <param name="objectText">"已完成/总数（失败 N）"形式的对象计数文本。</param>
    public static string BuildUnavailableProgressText(string objectText)
        => $"任务状态为运行中，但当前对象暂无可确认的中途字节进度：{objectText} 个对象已登记。" +
           "（大文件与无缓冲通道会先预分配目标长度，中途读不到已确认字节。）" +
           "仅凭进度条无法判断实际传输是否仍在进行，请结合活动状态或诊断信息确认；" +
           "进度可能在下一次可信结算后更新。";
}
