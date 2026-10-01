using PCMig.Diagnostics.Abstractions;

namespace PCMig.Diagnostics;

/// <summary>
/// 诊断运行时候选配置。**全部数值都是候选初始参数**（方案 §25.2）：
/// 未经 D7 基准测试前不得对外宣称任何一项是"产品保证"或"已验证开销"。
/// </summary>
public sealed class DiagnosticRuntimeOptions
{
    /// <summary>诊断数据根目录；null ⇒ 用 %LOCALAPPDATA%\PCMig\Diagnostics（当前用户权限，不要求管理员）。</summary>
    public string? StorageRoot { get; set; }

    /// <summary>启动模式。默认 Operational（Observation 常开但只记语义事实）。</summary>
    public CaptureMode InitialMode { get; set; } = CaptureMode.Operational;

    // ---- 有界摄入（三档）----

    /// <summary>Operational 分支队列容量（事件条数）。</summary>
    public int OperationalQueueCapacity { get; set; } = 4096;

    /// <summary>Operational 分支字节预算（payload/字段总量的软上限）。</summary>
    public long OperationalByteBudget { get; set; } = 8L * 1024 * 1024;

    /// <summary>DurableCritical 专用 reserve 容量（与 Operational 分开，保证罕见关键事实有位置）。</summary>
    public int CriticalReserveCapacity { get; set; } = 256;

    /// <summary>Verbose 分支队列容量。</summary>
    public int VerboseQueueCapacity { get; set; } = 8192;

    /// <summary>Verbose 分支字节预算。</summary>
    public long VerboseByteBudget { get; set; } = 16L * 1024 * 1024;

    /// <summary>Verbose 分支的淘汰策略：true = DropOldest（计入 Coalesced），false = 直接拒收（计入 Dropped）。</summary>
    public bool VerboseEvictsOldest { get; set; } = true;

    // ---- 消费侧 ----

    /// <summary>
    /// 线上分析器待处理上限（超过则记录 AnalyzerLag 并降级缺事件判定）。
    ///
    /// ★ D6.1 §4/§5 实证调整 ★ 原值 1024 在"真实量级突发"下会成为**最窄的一环**：
    /// 实测 2000 条瞬时突发在负载较高的机器上会出现 `analyzer/Operational queue-full` 丢失
    /// （丢失台账可见，不是静默）。分析器丢失会让反馈类规则降级，因此把它提高到与
    /// ingress/writer 同一量级（4096），使"一次真实突发"能完整排空。
    /// </summary>
    public int AnalyzerMaxPending { get; set; } = 4096;

    /// <summary>
    /// 诊断中心 viewer **显示缓存**条数上限：界面展示窗口，同时是规则证据解析视野
    /// （`DiagnosticRuntime.ResolveEvidenceFromCache` 从这里解析 `EventRef`，找不到 ⇒ 规则必须按
    /// "证据不可得"处理）。满了丢最旧并计数（<see cref="ViewerEventCache.DroppedOldest"/>）。
    ///
    /// ★ 它**不是** viewer 收件箱容量——两者此前共用同一个数值 2000（见 <see cref="ViewerQueueCapacity"/>）。
    /// </summary>
    public int ViewerMaxEvents { get; set; } = 2000;

    /// <summary>
    /// viewer 收件箱（fan-out → viewer 消费者）队列容量。
    ///
    /// ★ D6.3 实测缺陷 ★ 旧实现把收件箱容量直接写成 <see cref="ViewerMaxEvents"/>（= 2000），
    /// 而"一次真实量级突发"就是 2000 条 + 1 条会话开始事件 = 2001 条 ⇒
    /// 在机器有负载、viewer 泵线程来不及排空时**必然**出现 `viewer/Operational queue-full`：
    /// 实测 `lossEpoch=1`、`EvidenceComplete=false`（台账 `viewer/Operational queue-full x1`）。
    /// 这与 D6.1 修 analyzer 的情形完全同类（原 1024 太窄 ⇒ 提高到与其他 Operational 分支同量级），
    /// 故此处同样给收件箱独立容量，让"显示视野 2000"与"收件箱容量"解耦：
    /// 收件箱不再是那条最窄的环，而显示缓存仍是它本来的有界视野。
    /// </summary>
    public int ViewerQueueCapacity { get; set; } = 4096;

    /// <summary>单条事件写入前的字符串字段上限（超出即截断并标 Truncated）。</summary>
    public int MaxMessageLength { get; set; } = 256;

    /// <summary>
    /// ★ D6.1 §6 ★ 单条事件的字节硬上限（默认 64 KiB）。超过即在**管线入口拒收**并记账
    /// （`payload-too-large`）——大载荷只能被拒/被摘要，**不得绕过总预算**。
    /// </summary>
    public int MaxEventBytes { get; set; } = 64 * 1024;

    // ---- 持久化 ----

    /// <summary>单个事件段文件滚动阈值。</summary>
    public long SegmentMaxBytes { get; set; } = 16L * 1024 * 1024;

    /// <summary>诊断数据总配额（超出后按最旧密封段清理；活跃段/租约段优先保留）。</summary>
    public long TotalQuotaBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>保留天数上限（与容量同时生效，取先到者）。</summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>writer 批处理上限（写入缓冲达到该值即落盘）。</summary>
    public int WriterBatchMaxBytes { get; set; } = 64 * 1024;

    /// <summary>writer 周期 flush 间隔（DurableCritical 组另走更短路径）。</summary>
    public int WriterFlushIntervalMs { get; set; } = 250;

    /// <summary>单次存储故障后的重试退避上限（有界，不无限重试）。</summary>
    public int StorageRetryBackoffMs { get; set; } = 2000;

    // ---- Flight Recorder ----

    /// <summary>内存环字节预算（触发前窗口的实际上限由它决定，不是秒数）。</summary>
    public long RingByteBudget { get; set; } = 32L * 1024 * 1024;

    /// <summary>内存环事件条数上限。</summary>
    public int RingEventCapacity { get; set; } = 65536;

    /// <summary>触发后继续采集的窗口（毫秒）。</summary>
    public int FlightPostWindowMs { get; set; } = 15_000;

    /// <summary>同时冻结的窗口数上限（防错误风暴无限 pin）。</summary>
    public int FlightMaxPinnedWindows { get; set; } = 2;

    /// <summary>
    /// ★ D6.1 §7 ★ 已封/已落盘窗口的**元数据**保留上限（轻量：引用 + 清单 + 位置 + 计数）。
    /// 超出即淘汰最旧的元数据（它已落盘，需要时从磁盘读）。
    /// </summary>
    public int FlightMaxFinishedWindows { get; set; } = 16;

    /// <summary>
    /// ★ D6.1 §7 ★ 所有窗口**未落盘事件载荷**的内存字节预算。超出即停止新增窗口（记 reason），
    /// 保证"错误风暴"下内存不会随触发次数线性增长。
    /// </summary>
    public long FlightWindowPayloadByteBudget { get; set; } = 8L * 1024 * 1024;

    /// <summary>相邻触发的合并冷却窗口（毫秒）。</summary>
    public int FlightTriggerCooldownMs { get; set; } = 2_000;

    /// <summary>低频磁盘检查点间隔（毫秒）；仅 Flight/Deep 模式启用。</summary>
    public int CheckpointIntervalMs { get; set; } = 2_000;

    /// <summary>检查点总字节上限（超过即停止检查点并记 CoverageChanged）。</summary>
    public long CheckpointMaxBytes { get; set; } = 8L * 1024 * 1024;

    // ---- 关闭 ----

    /// <summary>关闭时授予诊断 drain 的预算（毫秒）；到期即如实记 ShutdownIncomplete。</summary>
    public int ShutdownBudgetMs { get; set; } = 1_500;

    // ★ D6.1 §19 已删除 ★ 原 `FailOpenToNoOp` 配置项**没有任何消费者**：
    //   fail-open 在本实现里是**无条件**的（装配失败即降级为"只有内存分支"，见 DiagnosticRuntime.Start），
    //   把开关留着只会让人以为"可以关掉 fail-open"——那是假的。如需真开关，必须先有真实实现再登记。

    /// <summary>应用版本（写进 session 元数据；由装配根注入，契约层不认识产品版本）。</summary>
    public string AppVersion { get; set; } = "unknown";

    /// <summary>构建标识（可选：提交号/构建号，便于离线包对齐）。</summary>
    public string? BuildId { get; set; }

    public DiagnosticRuntimeOptions Clone() => (DiagnosticRuntimeOptions)MemberwiseClone();
}