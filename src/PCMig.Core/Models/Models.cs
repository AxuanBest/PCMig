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
    public List<string> Warnings { get; set; } = new();
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
    public long TargetBytes { get; set; }            // 目标侧实际落盘字节（枚举实测）
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
