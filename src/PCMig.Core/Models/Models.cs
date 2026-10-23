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

    /// <summary>
    /// true = 用户显式选择「不预检开始迁移」（v0.5.4-preview.3）：跳过 <c>PreflightChecker</c> 的
    /// 源可达性 / 凭据 / 共享枚举 / 目标空间 / 权限 / 同名冲突检查。
    /// <para>
    /// 审计事实，落 job.json，续传沿用：事后能看出"这条任务当时没跑预检"。
    /// **不写 preflight.json**——该文件缺失 = 未执行预检，
    /// <c>ReportGenerator</c> 的「Preflight 检查记录」附录整段不出现，
    /// 因此绝不会把"没执行"显示成"预检通过"。
    /// </para>
    /// <para>
    /// 跳过预检**不等于**绕过执行引擎：Job 创建、源扫描、扫描残缺闸门、Planner 生成计划、
    /// 回执、失败统计与 Step4 验证全部照常走同一条代码路径。
    /// </para>
    /// </summary>
    public bool PreflightSkipped { get; set; }

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

        /// <summary>
        /// ★ P1-07 ★ 「目录树选中文件夹」的目标映射语义。**唯一真值是写进 plan.json 的 TargetPath**：
        /// 一旦计划生成，Robocopy Dest / Verifier / Repair / Resume / Report 全部读同一个 plan，
        /// 因此本选项只影响**新计划**的生成，绝不改变已存在 Job 的映射（旧 Job 按旧映射恢复）。
        ///   contentDirect（默认）= 复制所选文件夹**内部内容**到 TargetRoot：
        ///                          选 \\host\Users\d970 ⇒ 内容直接落在 C:\Users\D970（不再多嵌一层 d970）；
        ///   keepFolder          = 保留所选文件夹**本身**：内容落在 C:\Users\D970\d970。
        /// 仅对「自定义选择（Custom 模式）」且**只有一个**选择时生效；多选一律按源根相对结构映射
        /// （保持可辨识，避免不同来源的同名文件静默合并/覆盖）。
        /// </summary>
        public string SelectedFolderMapping { get; set; } = "contentDirect";

        /// <summary>contentDirect 的取值常量（避免各处手写字符串）。</summary>
        public const string SelectedFolderMappingContentDirect = "contentDirect";

        /// <summary>keepFolder 的取值常量。</summary>
        public const string SelectedFolderMappingKeepFolder = "keepFolder";
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
    /// <summary>★ P1-08 ★ 其中**实测**当前被独占打不开（Win32 32/33）的个数。候选数 ≠ 被占用数。</summary>
    public long LockedRiskFiles { get; set; }
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
    /// <summary>★ P1-08 ★ 其中实测当前被独占打不开（Win32 32/33）的个数。</summary>
    public long LockedRiskFiles { get; set; }
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
    /// <summary>
    /// ★ v0.5.4 Preview.2（2026-10-16 真机修复）★ 校验阶段确证「源有目标无」的对象数。
    ///
    /// 为什么必须与 <see cref="FailedObjects"/> 分开：两者是**不同时点、不同证据**的两件事——
    /// FailedObjects 来自迁移期回执（robocopy 退出码/错误分类），MissingObjects 来自校验期枚举
    /// 对账（verify-report 的 MISMATCH + MissingConfirmed）。真机上二者恰好指同一对象
    /// （object-000010），但存在「迁移报成功、校验却发现目标少了文件」的真实情形，
    /// 那种情况 FailedObjects=0 而 MissingObjects&gt;0，**绝不能算完成**。
    /// 默认 0 不落盘，旧 job-state.json 完全兼容。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int MissingObjects { get; set; }
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

    // ═══════════════ ★ 2026-10-11（验证报告返修，用户执行令 00–10）★ ═══════════════
    // 逐次验证的"证据档案"。**全部可选**：旧 verify-report.json 反序列化后这些字段保持
    // null / 空集合，报告如实写"未采集"，绝不推导、绝不编造（用户执行令 §02/§06/§08）。

    /// <summary>本次验证的唯一运行号（同一 Job 连续验证两次时可区分轮次）。旧报告为 null。</summary>
    public string? VerifyRunId { get; set; }

    /// <summary>本次验证的起止时刻（UTC）与总耗时（毫秒）。旧报告为 null ⇒ 报告写"未采集"。</summary>
    public DateTime? StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public double? ElapsedMs { get; set; }

    /// <summary>产生本报告的构建版本（如实取程序集版本；不编造日期号）。</summary>
    public string? ToolVersion { get; set; }

    /// <summary>内容级核对使用的算法名（L2 为 "SHA-256"；L1 未做任何内容级核对 ⇒ null）。</summary>
    public string? HashAlgorithm { get; set; }

    /// <summary>本次实际生效的抽样百分比（1–100）。L1 未抽样 ⇒ 仍记录配置值并注明未执行。</summary>
    public int? SampleHashPercent { get; set; }

    /// <summary>统计完整性：Complete / Partial（与诊断事件 StatsCompletenessObserved 同一判据）。</summary>
    public string? EvidenceCompleteness { get; set; }

    /// <summary>统计完整性的自然语言说明（枚举被跳过多少项、是否有流式降级等）。</summary>
    public string? EvidenceNote { get; set; }

    /// <summary>环境档案：源主机、目标根、用户显式选择、映射规则、策略排除名单快照。</summary>
    public string? SourceHost { get; set; }
    public string? TargetRoot { get; set; }
    public List<string> SelectedPaths { get; set; } = new();
    public string? MappingRule { get; set; }
    public List<string> ExcludedByPolicy { get; set; } = new();

    /// <summary>阶段耗时表（真实累计：源侧枚举 / 目标侧枚举 / 对账 / 内容级抽样）。旧报告为空。</summary>
    public List<VerifyStageRecord> Stages { get; set; } = new();

    /// <summary>
    /// 文件级差异记录（有界：<c>MaxFileDiffRecords</c> 条；超限只计数并置 <see cref="FileDiffsTruncated"/>）。
    /// **只有真的逐文件核对过才写**；哈希不一致只知总数、引擎未记录文件名时不得编造条目。
    /// </summary>
    public List<FileDiffRecord> FileDiffs { get; set; } = new();
    public bool FileDiffsTruncated { get; set; }

    /// <summary>枚举期被跳过的条目明细（有界；含侧别、路径、类型、错误文本与时刻）。</summary>
    public List<EnumerationFailure> EnumerationFailures { get; set; } = new();
    public bool EnumerationFailuresTruncated { get; set; }

    /// <summary>
    /// 本轮验证的运行状态：completed / canceled / failed / partial。
    /// 引擎正常跑完写 "completed"；旧报告为 null ⇒ 报告按"未记录"陈述（不假装知道）。
    /// </summary>
    public string? RunStatus { get; set; }
}

/// <summary>一个验证阶段的真实耗时记录（按对象累计，Note 里写明口径）。</summary>
public sealed class VerifyStageRecord
{
    public string Stage { get; set; } = "";
    public double ElapsedMs { get; set; }
    /// <summary>该阶段覆盖的对象数（0 = 不按对象计）。</summary>
    public long CoveredObjects { get; set; }
    /// <summary>口径说明（如"按对象累计""未执行"），不许留空让读者猜。</summary>
    public string? Note { get; set; }
}

/// <summary>
/// 一条文件级差异（有界记录）。字段都来自引擎**当时真的读过**的东西：
/// 相对路径、源/目标绝对路径、两侧长度、证据来源、以及（确实有的话）Win32 错误码。
/// </summary>
public sealed class FileDiffRecord
{
    public string ObjectId { get; set; } = "";
    /// <summary>差异类型：missing-on-target / extra-on-target / size-mismatch / hash-mismatch / unreadable。</summary>
    public string DiffKind { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string TargetPath { get; set; } = "";
    /// <summary>源侧长度；-1 = 该侧不存在（不是 0 字节）。</summary>
    public long SourceLength { get; set; } = -1;
    /// <summary>目标侧长度；-1 = 该侧不存在（不是 0 字节）。</summary>
    public long TargetLength { get; set; } = -1;
    /// <summary>证据来源（如"L1 枚举比较""长度比较""SHA-256 双向核对"）。</summary>
    public string? Evidence { get; set; }
    /// <summary>确实拿到的 Win32 错误码文本（如 "Win32 32 ERROR_SHARING_VIOLATION"）；没有就 null，不编造。</summary>
    public string? Win32Code { get; set; }
    /// <summary>补充明细（例如两侧哈希值；内容级证据）。</summary>
    public string? Detail { get; set; }
}

/// <summary>枚举期被跳过的一条记录（阶段、路径、类型、错误文本、时刻）。</summary>
public sealed class EnumerationFailure
{
    /// <summary>source / target。</summary>
    public string Side { get; set; } = "";
    /// <summary>被跳过条目的路径（目录或文件；引擎给出的是当时在遍历的那一层）。</summary>
    public string Path { get; set; } = "";
    /// <summary>directory / file。</summary>
    public string Kind { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
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

    /// <summary>
    /// ★ F-1（2026-10-13 定点修复）★ <see cref="MissingSamples"/> 里的路径是否**已被确证**"源有目标无"。
    ///
    /// 为什么单列一个布尔：当对象是 UNKNOWN（源/目标不可达、枚举不全、两侧 0/0 与计划矛盾）时，
    /// 比对过程会把"读不到的一侧"当成"什么都没有"，于是**每一个源文件都会被记进 MissingSamples**。
    /// 那些路径**不是**缺失证据（实测证明：`target-unavailable` 的对象在控制台被逐条打成
    /// 「缺失: a.bin」，与报告卡片上「无法确认不是缺失」自相矛盾 —— 现场"假缺失"话术的来源）。
    /// 因此：只有**结论为 MISMATCH**（= 两侧都读到了、确实比对出差异）时，MissingSamples 才算"确证缺失"。
    /// 所有人类可读出口（CLI 控制台 / 报告 HTML / 状态摘要 / UI 差异面板）都必须按本字段决定用词。
    /// </summary>
    public bool MissingConfirmed { get; set; }

    // ═══════════════ P0-02：结构化差异与四态结论 ═══════════════
    // 旧实现只有 OK / MISMATCH 两态，且把「枚举被跳过」「源不可达」「两边 0/0」都算成 OK。
    // 现场三种完全不同的状态因此被压成同一个词：10/12（目标多文件）、根目录 14/0（统计缺陷）、
    // NTUSER 真锁未到目标（真实缺失）。下面把它们分开陈述。

    /// <summary>目标侧存在、源侧没有的相对路径（最多 50 条）。**这不是缺失**，默认也不自动清理。</summary>
    public List<string> ExtraOnTarget { get; set; } = new();

    /// <summary>两侧都存在但长度不同的相对路径样例（最多 50 条）。「数量/字节相同也可能内容不同」的兜底。</summary>
    public List<string> SizeMismatchSamples { get; set; } = new();

    /// <summary>长度不一致的文件总数（超出样例上限时仍准确计数）。</summary>
    public long SizeMismatched { get; set; }

    /// <summary>哈希样本读取失败数（≠ 内容不一致：可能是共享冲突/权限/源中断）。</summary>
    public long HashReadFailed { get; set; }

    /// <summary>枚举期被跳过的条目数（权限/竞态/瞬态 IO）。&gt; 0 ⇒ 统计不完整，不得声称 OK。</summary>
    public long EnumerationSkipped { get; set; }

    /// <summary>源根不可达（不存在/未挂载/共享已被策略关闭）。</summary>
    public bool SourceUnavailable { get; set; }

    /// <summary>目标根不可达。</summary>
    public bool TargetUnavailable { get; set; }

    /// <summary>两侧都是 0 文件 0 字节。只有计划本身预期为空时才是「真的空」。</summary>
    public bool BothSidesEmpty { get; set; }

    /// <summary>计划预期（判断「空」是否合理、以及验证是否偏离计划规模的依据）。</summary>
    public long PlannedFiles { get; set; }
    public long PlannedBytes { get; set; }

    /// <summary>UNKNOWN 的原因码（如 source-unavailable / enumeration-incomplete / both-sides-empty）。</summary>
    public string? UnknownReason { get; set; }

    /// <summary>
    /// ★ 2026-10-11（验证报告返修）★ 本对象的枚举覆盖状态：
    ///   <c>complete</c> = 两侧枚举都完整；<c>incomplete</c> = 有条目被跳过，
    ///   此时该对象**既不能说通过、也不能把没出现的文件说成真实缺失**。
    /// 旧报告没有这个字段 ⇒ 读取后为 null，报告按 <see cref="EnumerationSkipped"/> 推导。
    /// </summary>
    public string? EnumerationStatus { get; set; }

    /// <summary>
    /// 业务结论：OK / MISMATCH / UNKNOWN / EMPTY / CANCELED。
    /// UNKNOWN 与 EMPTY 都不等于"核对过内容"；<see cref="VerifyReport.OverallPass"/> 只认 OK 与"计划与实测都为空"的 EMPTY。
    /// </summary>
    public string Status { get; set; } = "OK";

    /// <summary>是否可安全进入破坏性修复（只有确认差异才可以）。P0-01(4)/P0-03 的闸门判据。</summary>
    public bool AllowsDestructiveRepair { get; set; }

    public string StatusText => Status switch
    {
        "OK" => "通过",
        "MISMATCH" => "存在差异",
        "UNKNOWN" => "无法确认",
        "EMPTY" => "无可核对数据",
        "CANCELED" => "已取消",
        _ => Status
    };
}
