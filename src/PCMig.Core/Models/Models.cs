using System.Text.Json.Serialization;

namespace PCMig.Core.Models;

// ============================================================
// PCMig 核心数据契约（Schema）
// 设计原则：一切状态可序列化、可审计、可重建。
//   - JobDefinition  (job.json)        —— 任务定义，创建后不可变
//   - ObservedState  (observed-state.json) —— 扫描结果
//   - MigrationPlan  (plan.json)       —— 计划，人工审核后的执行依据
//   - JobState       (job-state.json)  —— 运行态，原子写，损坏时可由 Receipt 重建
//   - ObjectReceipt  (receipts/*.json) —— 每对象完成凭证，只追加不改写
// ============================================================

public enum JobPhase
{
    Created,
    Preflight,
    Scanning,
    Planned,
    AwaitingReview,
    Running,
    Paused,
    Verifying,
    Completed,
    CompletedWithErrors,
    Interrupted,
    Failed,
    Canceled
}

public enum ObjectStatus { Pending, Running, Completed, CompletedWithErrors, Skipped, Interrupted, Failed }

public enum ErrorClass { None, Transient, Permanent, Policy }

/// <summary>迁移对象类别。DataVolume=数据盘(Data Plane)；UserProfile=用户目录；AppState=应用状态。</summary>
public enum ObjectKind { DataVolume, UserProfile, AppState, RootFiles }

public enum VerifyLevel { None = 0, L1_CountSize = 1, L2_SampleHash = 2 }

// ------------------------------------------------------------

/// <summary>job.json —— 任务定义。创建后视为不可变。</summary>
public sealed class JobDefinition
{
    public string SchemaVersion { get; set; } = "1.0";
    public string JobId { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string SourceHost { get; set; } = "";
    public string? SourceUser { get; set; }          // 仅记录用户名，永不持久化密码
    public List<SourceSpec> Sources { get; set; } = new();

    /// <summary>
    /// 显式选择（GUI 目录树勾选 / CLI --paths）：目录或文件的完整 UNC 路径。
    /// 非空时启用 Custom 模式：只迁移这些路径，Sources 仅用于路径映射参考。
    /// </summary>
    public List<string> CustomSelections { get; set; } = new();

    /// <summary>
    /// 超大数据模式：用户自定义排除规则（/XD 目录 或 /XF 文件，每行一条；裸词按目录处理）。
    /// 持久化在 job.json，扫描/传输/验证/续传全程同口径生效。
    /// </summary>
    public List<string> CustomExclusions { get; set; } = new();

    public string TargetRoot { get; set; } = "";     // 本地目标根，例如 D:\Migrated\OLD-PC

    /// <summary>
    /// true = 用户已显式确认“扫描存在不可访问目录，仍要继续”（v0.3.8 扫描残缺闸门）。
    /// false（默认）= 存在不可访问目录时不允许开始迁移——这些目录里的文件既不在计划内、也不会被复制，
    /// 事后再看报告只会看到“完成”（真实事故里 Prepare 就是被一次意外的网络 IOException 打掉的）。
    /// 确认结果落 job.json，续传沿用，便于审计“当时是谁放行的”。
    /// </summary>
    public bool AllowIncompleteScan { get; set; }

    public MigrationOptions Options { get; set; } = new();
    public string CreatedBy { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed class SourceSpec
{
    public string Path { get; set; } = "";           // UNC: \\OLD-PC\D$ 或共享名路径
    public ObjectKind Kind { get; set; } = ObjectKind.DataVolume;
    public bool Enabled { get; set; } = true;
}

public sealed class MigrationOptions
{
    /// <summary>大于该值的文件走 /Z 单线程可续传队列；其余走 /MT 多线程队列。</summary>
    public int LargeFileThresholdMB { get; set; } = 512;
    public int Threads { get; set; } = 16;
    public int RetryCount { get; set; } = 2;         // robocopy /R
    public int RetryWaitSec { get; set; } = 5;       // robocopy /W
    public int MaxObjectRetries { get; set; } = 3;   // 对象级重试（Transient 错误）
    public VerifyLevel VerifyLevel { get; set; } = VerifyLevel.L1_CountSize;
    public int SampleHashPercent { get; set; } = 1;  // L2 抽样比例
    public bool SplitLargeFiles { get; set; } = true;

    /// <summary>
    /// 大文件通道（v0.3.8）：auto（默认）= /MT + /J 无缓冲多线程，对象级重试时自动退回 /Z；
    /// restartable = 恒用 /Z（文件内部可续传，单线程）；multithreaded = 恒用 /MT + /J。
    /// 旧版恒用 /Z 单线程，真实生产实测吞吐只有手工 robocopy /MT:32 的 1/3（30 vs 90–137 MB/s）。
    /// </summary>
    public string LargeChannelMode { get; set; } = "auto";
}

// ------------------------------------------------------------

/// <summary>observed-state.json —— 扫描结果。</summary>
public sealed class ObservedState
{
    public string SchemaVersion { get; set; } = "1.0";
    public string JobId { get; set; } = "";
    public DateTime ScannedUtc { get; set; } = DateTime.UtcNow;
    public List<ScannedObject> Objects { get; set; } = new();
    public long TotalBytes { get; set; }
    public long TotalFiles { get; set; }
    public long LockRiskFiles { get; set; }          // 全源 .pst/.ost 合计（迁移前提醒用户关 Outlook）
    public long EncryptedFiles { get; set; }         // 全源 EFS 加密合计
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// 扫描时不可访问的目录/文件（v0.3.8）：每条 = "对象 object-000003｜\\主机\共享\目录｜原因"。
    /// 旧版只把 Incomplete 置 true 并在日志里拼一句"[部分不可访问×N]"，没有任何地方拦人。
    /// </summary>
    public List<string> InaccessiblePaths { get; set; } = new();

    /// <summary>是否存在不可访问目录（任一对象残缺，或已记录明细）。</summary>
    [JsonIgnore]
    public bool ScanIncomplete => InaccessiblePaths.Count > 0 || Objects.Any(o => o.ScanIncomplete);
}

public sealed class ScannedObject
{
    public string ObjectId { get; set; } = "";       // object-000001
    public ObjectKind Kind { get; set; }
    public string SourcePath { get; set; } = "";
    public long Bytes { get; set; }
    public long Files { get; set; }
    public long Dirs { get; set; }
    public bool HasLargeFiles { get; set; }
    public bool HasCloudPlaceholders { get; set; }   // OneDrive 等占位符
    public bool ScanIncomplete { get; set; }         // 部分目录不可访问
    public long LockRiskFiles { get; set; }          // .pst/.ost 打开即锁的高危文件数
    public long EncryptedFiles { get; set; }         // EFS 加密文件数
    /// <summary>非空 = 文件清单对象：SourcePath 目录下仅这些文件。</summary>
    public List<string>? FileList { get; set; }
}

// ------------------------------------------------------------

/// <summary>plan.json —— 执行计划（Planner 输出，人工审核后执行）。</summary>
public sealed class MigrationPlan
{
    public string SchemaVersion { get; set; } = "1.0";
    public string JobId { get; set; } = "";
    public DateTime PlannedUtc { get; set; } = DateTime.UtcNow;
    public List<PlannedObject> Objects { get; set; } = new();
    public long TotalBytes { get; set; }
    public List<string> ExcludedByPolicy { get; set; } = new();
}

public sealed class PlannedObject
{
    public string ObjectId { get; set; } = "";
    public ObjectKind Kind { get; set; }
    public string SourcePath { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public long EstimatedBytes { get; set; }
    public long EstimatedFiles { get; set; }
    public bool UseRestartablePass { get; set; }     // 是否需要第二遍 /Z 大文件队列
    /// <summary>非空 = 文件清单对象：只传 SourcePath 目录下的这些文件（文件名列表）。</summary>
    public List<string>? FileList { get; set; }

    /// <summary>
    /// ★ 缺陷 B12b4（数据完整性 / 假成功）★ 计划生成时该对象**源根的身份指纹**
    /// （卷序列号 + 目录文件 ID，取不到时退化为服务端共享物理路径）。
    ///
    /// 为什么必须有：共享被撤销后**以同名重建到别的目录**时，共享名与 UNC 前缀完全不变，
    /// 产品只看名字就会"同名即同源"地继续取数——B12b4 实测冒名数据到达目标端而界面宣称
    /// 「✔ 迁移完成 100%」。续传（以及任何一次开跑）前用同一算法重新取指纹比对，
    /// 不一致即拒绝继续，堵住这条假成功链。
    /// 旧 plan（字段为 null）不参与校验，保持向后兼容。
    /// </summary>
    public string? SourceIdentity { get; set; }
}

// ------------------------------------------------------------

/// <summary>
/// 暂停状态机（Trust-Critical Recovery FIX BATCH 1）。
/// 旧实现用单个 <c>bool IsPaused</c> 承担"请求 → 正在停 → 真的停了 → 停不下来"全过程，
/// 于是"写入 pause.request 成功"被当成了"迁移已暂停"——真机上 8/8 次点击都没停住，UI 却报已暂停。
/// 本枚举只表达**引擎侧的真实进度**，请求文件是否存在不能替代它。
/// </summary>
public enum PauseState
{
    /// <summary>没有暂停这件事（未请求 / 已恢复 / 已结束）。</summary>
    None = 0,
    /// <summary>已读到暂停请求，正在尝试停止传输（SLA 计时中）。</summary>
    Pausing = 1,
    /// <summary>传输**真的停了**：worker 已退出，目标字节不再增长，当前对象未完成。</summary>
    Paused = 2,
    /// <summary>请求已受理，但在硬失败 SLA（<see cref="Transfer.PauseSla.HardFailSeconds"/> s）内没能停住——迁移仍在进行。</summary>
    PauseFailed = 3,
}

/// <summary>暂停的最终结果（Diagnostics 行动兑现判定用：请求 vs 业务效果必须分开）。</summary>
public enum PauseOutcomeKind
{
    None = 0,
    /// <summary>暂停达成（worker 已停止）。</summary>
    Achieved = 1,
    /// <summary>暂停失败（worker 拒绝停止）——绝不是"已暂停"。</summary>
    Failed = 2,
}

// ------------------------------------------------------------

/// <summary>job-state.json —— 运行态。始终原子写（tmp+rename）。损坏时可由 receipts 重建。</summary>
public sealed class JobState
{
    public string SchemaVersion { get; set; } = "1.0";
    public string JobId { get; set; } = "";
    public JobPhase Phase { get; set; } = JobPhase.Created;
    public string? CurrentObjectId { get; set; }
    public int TotalObjects { get; set; }
    public int CompletedObjects { get; set; }
    public int FailedObjects { get; set; }
    public long TotalBytes { get; set; }
    public long CompletedBytes { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Percent { get; set; }
    public double BytesPerSecond { get; set; }
    public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;
    public string? LastError { get; set; }

    // ---- 暂停真值（Trust-Critical Recovery FIX BATCH 1）----
    // 契约：这些字段只在"存在暂停这件事"时被写入（默认值不落盘，旧 job-state.json 完全兼容）；
    //       它们记录的是**引擎侧的真实结果**，不是"请求文件写成功了"。Diagnostics 与 UI 都从这里取值。
    /// <summary>暂停状态机当前档位。默认 None 不落盘（保持旧存档形状）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PauseState PauseState { get; set; } = PauseState.None;
    /// <summary>请求文件里的模式原文（Cooperative / Immediate）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PauseRequestMode { get; set; }
    /// <summary>引擎读到暂停请求的时刻（UTC）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? PauseRequestedUtc { get; set; }
    /// <summary>**真的停下来**的时刻（UTC）。没达成就必须为 null——绝不用请求时刻冒充。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? PauseAchievedUtc { get; set; }
    /// <summary>从请求到（未）达成的耗时毫秒。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? PauseElapsedMs { get; set; }
    /// <summary>暂停结果。默认 None 不落盘。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PauseOutcomeKind PauseOutcome { get; set; } = PauseOutcomeKind.None;
    /// <summary>PauseFailed 的可解释原因（迁移仍在进行）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PauseFailureReason { get; set; }
}

// ------------------------------------------------------------

/// <summary>receipts/object-NNNNNN.json —— 不可变迁移凭证。每个对象完成后追加一个。</summary>
public sealed class ObjectReceipt
{
    public string SchemaVersion { get; set; } = "1.0";
    public string ObjectId { get; set; } = "";
    public ObjectKind Kind { get; set; }
    public string SourcePath { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime CompletedUtc { get; set; }
    public ObjectStatus Status { get; set; }
    // 目标侧字节。两种语义：Completed=盘上实测（枚举长度）；Interrupted=本趟运行时可信
    // checkpoint（TransferOrchestrator.CaptureInterruptedConfirmedProgress）——后者在 /Z 可续传
    // 通道下**不等于**目标文件逻辑长度，因为 robocopy 会先预分配最终大小。
    public long TargetBytes { get; set; }
    // 目标侧文件数。Interrupted 结算时为 0：文件计数没有可靠的运行时来源，不猜。
    public long TargetFiles { get; set; }
    public int RobocopyExitCodeBulk { get; set; } = -1;
    public int RobocopyExitCodeLarge { get; set; } = -1;  // -1 = 未执行该 pass
    public ErrorClass ErrorClass { get; set; } = ErrorClass.None;
    public string? ErrorDetail { get; set; }
    public int Attempt { get; set; } = 1;
}

// ------------------------------------------------------------

/// <summary>preflight 结果。</summary>
public sealed class PreflightReport
{
    public string Host { get; set; } = "";
    public DateTime CheckedUtc { get; set; } = DateTime.UtcNow;
    public bool OverallPass { get; set; }
    public List<PreflightCheck> Checks { get; set; } = new();
    public List<ShareInfo> Shares { get; set; } = new();

    /// <summary>
    /// ★ 缺陷 O-B22c-1 ★ 用户输入的那套凭据**是否已被独立证实可用**
    /// （IPC$ 会话用输入凭据建立成功 / 直连源共享成功 / 源路径真的读得到 / 共享枚举成功）。
    ///
    /// 为什么必须与 <see cref="OverallPass"/> 分开：预检可以在"凭据没被证实"的情况下通过
    /// （例如对方不导出 IPC$ 只报 67），此时界面若只说「已连接 + 发现 0 个共享」，
    /// 用户会把"没验证"读成"验证过了"。凡是要宣称"已连接"的地方都必须先看这个字段。
    /// </summary>
    public bool CredentialVerified { get; set; }

    /// <summary>
    /// ★ 缺陷 O-B22c-1 ★ 本次连接**复用了本机已有的 SMB 连接**（1219/85 冲突），
    /// 即用户输入的账号**没有被使用**。界面必须如实告知，禁止把它呈现成"凭据正确"。
    /// </summary>
    public bool CredentialReused { get; set; }
}

public sealed class PreflightCheck
{
    public string Name { get; set; } = "";
    public bool Pass { get; set; }
    public string Severity { get; set; } = "Error";  // Error | Warning | Info
    public string Detail { get; set; } = "";
}

public sealed class ShareInfo
{
    public string Name { get; set; } = "";
    public string Remark { get; set; } = "";
    public bool IsAdminShare { get; set; }
    public string UncPath { get; set; } = "";
    /// <summary>共享对应的物理本地路径（如 E:\）。SHARE_INFO_2 可得；取不到时为空。</summary>
    public string LocalPath { get; set; } = "";
    /// <summary>与本共享指向同一物理路径、已被合并的其他共享名（如 E 与 E$ 同盘时保留 E，E$ 记入此列）。</summary>
    public List<string> Aliases { get; set; } = new();
}

// ------------------------------------------------------------

/// <summary>verify 结果。</summary>
public sealed class VerifyReport
{
    public string JobId { get; set; } = "";
    public DateTime VerifiedUtc { get; set; } = DateTime.UtcNow;
    public VerifyLevel Level { get; set; }
    public bool OverallPass { get; set; }
    public List<ObjectVerifyResult> Objects { get; set; } = new();
}

public sealed class ObjectVerifyResult
{
    public string ObjectId { get; set; } = "";
    public long SourceFiles { get; set; }
    public long TargetFiles { get; set; }
    public long SourceBytes { get; set; }
    public long TargetBytes { get; set; }
    public bool CountMatch { get; set; }
    public bool BytesMatch { get; set; }
    public int HashSampled { get; set; }
    public int HashMismatched { get; set; }
    public List<string> MissingSamples { get; set; } = new(); // 最多记录前 50 条
    public string Status => CountMatch && BytesMatch && HashMismatched == 0 ? "OK" : "MISMATCH";
}
