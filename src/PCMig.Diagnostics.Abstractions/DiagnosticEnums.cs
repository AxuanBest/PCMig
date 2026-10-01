namespace PCMig.Diagnostics.Abstractions;

/// <summary>
/// 诊断级别。**与 DeliveryClass 正交**：级别描述影响，投递类描述存储/持久性承诺。
/// </summary>
public enum DiagnosticLevel
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5,
}

/// <summary>
/// 事件族。枚举数值 **同时决定 EventId 段**（见 <see cref="DiagnosticCategoryExtensions.EventIdBase"/>）：
/// 每族占 1000 号段，族内序号 1..999。数值一旦发布不得改动（改动等于让已发布 EventId 漂移）。
/// </summary>
public enum DiagnosticCategory
{
    /// <summary>未知/外部事件（反序列化时无法匹配 catalog 的原始 EventId）。规则必须跳过此类。</summary>
    Unknown = 0,
    App = 1,
    Ui = 2,
    Net = 3,
    Fs = 4,
    Plan = 5,
    Preflight = 6,
    Transfer = 7,
    Robocopy = 8,
    Persistence = 9,
    Verify = 10,
    Repair = 11,
    Diagnostics = 12,
}

/// <summary>
/// 投递类。**与 DiagnosticLevel 不同**：这是"这条事件值不值得为它在故障下保留"的承诺等级。
/// 承诺上限见架构方案 §16：Verbose 可丢、Operational 可查水位、DurableCritical 只在 flush ack 后称已落盘。
/// </summary>
public enum DeliveryClass
{
    /// <summary>高频/低价值：允许降采样与丢弃，但丢弃必须准确计数。</summary>
    Verbose = 0,

    /// <summary>生命周期/失败事实：走有界队列 + 独立 reserve，丢失必须可见。</summary>
    Operational = 1,

    /// <summary>罕见且与数据可靠性相关：专用 reserve + 显式 flush ack，仍**不承诺**断电零丢。</summary>
    DurableCritical = 2,
}

/// <summary>这条证据有多硬：Direct=直接观测；Derived=由其它观测推导；Estimated=近似；Legacy=历史解析。</summary>
public enum EvidenceQuality
{
    Unknown = 0,
    Direct = 1,
    Derived = 2,
    Estimated = 3,
    Legacy = 4,
}

/// <summary>采集模式。Deep 只做限时输入观测，不做系统级 trace。</summary>
public enum CaptureMode
{
    Off = 0,
    Operational = 1,
    Flight = 2,
    Deep = 3,
}

/// <summary>
/// 数据分类。**Secret 永远不得进入 DiagnosticEvent**（在 <see cref="EventDescriptor.Define"/> 里强制）。
/// </summary>
public enum PrivacyClassification
{
    Public = 0,
    Personal = 1,
    Secret = 2,
}

/// <summary>
/// 事件结果枚举。**不是 JobPhase**：它描述"这一个观察点的结果"，不描述任务状态机。
/// </summary>
public enum DiagnosticOutcome
{
    Started = 0,
    Accepted = 1,
    Rejected = 2,
    Succeeded = 3,
    Failed = 4,
    Canceled = 5,
    Skipped = 6,
    Unknown = 7,
}

/// <summary>状态归属：同一个业务概念在 Core / VM / View / 存档里的取值必须能区分开（投影缺失判定的前提）。</summary>
public enum StateOwner
{
    Unknown = 0,
    Core = 1,
    Vm = 2,
    View = 3,
    StoredState = 4,
    Process = 5,
    External = 6,
}

/// <summary>路径角色的诊断口径（配合 <see cref="PathRef"/>，默认已脱敏）。</summary>
public enum PathRole
{
    Unknown = 0,
    Source = 1,
    Target = 2,
    JobDir = 3,
    ReceiptDir = 4,
    LogFile = 5,
    TempFile = 6,
    ChildProcessImage = 7,
    WorkingDirectory = 8,
    ExportOutput = 9,
}

/// <summary>
/// 错误域分离：同一个数字在不同域里含义完全不同（例如 5 可以是 Win32 ERROR_ACCESS_DENIED，
/// 也可以只是某个 HResult 的低位）。**不允许**把 HResult 低 16 位当 Win32 用。
/// </summary>
public enum ErrorDomain
{
    None = 0,
    Managed = 1,
    HResult = 2,
    Win32 = 3,
    Socket = 4,
    RobocopyExitCode = 5,
    Io = 6,
}

/// <summary>EventId 段与代码前缀的唯一来源（杜绝手工写 "UI-001" 这类字面量漂移）。</summary>
public static class DiagnosticCategoryExtensions
{
    /// <summary>族前缀：EventId 段 1000*category，代码形如 UI-001。</summary>
    public static string Prefix(this DiagnosticCategory category) => category switch
    {
        DiagnosticCategory.App => "APP",
        DiagnosticCategory.Ui => "UI",
        DiagnosticCategory.Net => "NET",
        DiagnosticCategory.Fs => "FS",
        DiagnosticCategory.Plan => "PLN",
        DiagnosticCategory.Preflight => "PFL",
        DiagnosticCategory.Transfer => "TRN",
        DiagnosticCategory.Robocopy => "RBC",
        DiagnosticCategory.Persistence => "PST",
        DiagnosticCategory.Verify => "VRF",
        DiagnosticCategory.Repair => "RPR",
        DiagnosticCategory.Diagnostics => "DIA",
        _ => "UNK",
    };

    /// <summary>族 EventId 基数（Unknown 族返回 0，其 EventId 由原始值直接携带）。</summary>
    public static int EventIdBase(this DiagnosticCategory category) =>
        category == DiagnosticCategory.Unknown ? 0 : (int)category * 1000;
}

/// <summary>
/// Robocopy 通道的稳定 token。Core 的 <c>PassKind</c> 是业务枚举，契约层不认识它，
/// 因此只在这里固定字符串常量，避免调用点随手写 "Bulk"/"bulk" 两种拼法。
/// </summary>
public static class DiagnosticPass
{
    public const string Bulk = "Bulk";
    public const string RootFiles = "RootFiles";
    public const string Large = "Large";
}