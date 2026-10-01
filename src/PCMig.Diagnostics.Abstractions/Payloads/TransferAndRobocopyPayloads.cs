using System.Text.Json;

namespace PCMig.Diagnostics.Abstractions.Payloads;

// ────────────────────────── RBC：robocopy 子进程 ──────────────────────────
// 纪律：参数只记**结构化白名单摘要**（通道/重试/文件数），源与目标路径一律走 PathRef，绝不整条命令行。

/// <summary>
/// 子进程身份与通道（PID + 进程启动时间共同构成身份，只看 PID 会被复用骗到）。
/// ★ 字段全是**稳定 token**（Bulk/RootFiles/Large、mt/mt+j/z-restartable），不含中文显示文字 ★
/// —— 规则只允许依赖稳定 token（方案 §7：中文文案不得作为业务判断依据）。
/// </summary>
public sealed record RbcProcessPayload(
    int ProcessId,
    string ProcessIdentity,
    string Channel,
    string ChannelMode,
    string ArgumentSummary) : IDiagnosticPayload
{
    public const string Name = "RbcProcess";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteNumber("processId", ProcessId);
        w.WriteString("processIdentity", ProcessIdentity);
        w.WriteString("channel", Channel);
        w.WriteString("channelMode", ChannelMode);
        w.WriteString("argumentSummary", ArgumentSummary);
    }
}

/// <summary>终止请求/结果（**KillRequested ≠ 已确认终止**，两者必须分开）。</summary>
public sealed record RbcKillPayload(string Reason, bool Succeeded) : IDiagnosticPayload
{
    public const string Name = "RbcKill";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteString("reason", Reason);
        w.WriteBoolean("succeeded", Succeeded);
    }
}

/// <summary>
/// 错误行聚合（**只记计数与去重码数**，不记原文：robocopy 错误行含完整路径）。
/// 有界保留是既有业务行为（MaxErrorLines），这里把"到底有多少行、是否被截断"变成事实。
/// </summary>
public sealed record RbcErrorAggregatePayload(
    int ErrorLineCount,
    bool Truncated,
    int DistinctErrorCodes) : IDiagnosticPayload
{
    public const string Name = "RbcErrorAggregate";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteNumber("errorLineCount", ErrorLineCount);
        w.WriteBoolean("truncated", Truncated);
        w.WriteNumber("distinctErrorCodes", DistinctErrorCodes);
    }
}

/// <summary>文件行采样的计数（每 pass 只采样有限条，其余只计数——2.1M 文件不能逐条建事件）。</summary>
public sealed record RbcFileSampleSummaryPayload(int Sampled, long TotalObserved, bool SamplingActive) : IDiagnosticPayload
{
    public const string Name = "RbcFileSampleSummary";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteNumber("sampled", Sampled);
        w.WriteNumber("totalObserved", TotalObserved);
        w.WriteBoolean("samplingActive", SamplingActive);
    }
}

// ────────────────────────── TRN：对象级结果 ──────────────────────────

/// <summary>
/// 对象级结果：两个通道的退出码**分开**记录（Bulk=-1 表示未执行该通道），
/// 加字节/文件数与错误分类。规则的输入必须是这些结构化字段，而不是解析日志文本。
/// </summary>
public sealed record TrnObjectPayload(
    string Kind,
    long TargetBytes,
    long TargetFiles,
    int ExitCodeBulk,
    int ExitCodeLarge,
    string ErrorClass,
    int Attempt) : IDiagnosticPayload
{
    public const string Name = "TrnObject";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteString("kind", Kind);
        w.WriteNumber("targetBytes", TargetBytes);
        w.WriteNumber("targetFiles", TargetFiles);
        w.WriteNumber("exitCodeBulk", ExitCodeBulk);
        w.WriteNumber("exitCodeLarge", ExitCodeLarge);
        w.WriteString("errorClass", ErrorClass);
        w.WriteNumber("attempt", Attempt);
    }
}

/// <summary>
/// 进度观测：**必须标明字节口径**（approx/raw/stat/receipt）与是否估算——
/// 否则"进度卡住"这类结论会把估算值当成事实（方案 §6 的明确要求）。
/// </summary>
public sealed record TrnProgressPayload(
    string ByteSource,
    long CompletedBytes,
    long TotalBytes,
    int FilesAttempted,
    double BytesPerSecond,
    bool Estimated) : IDiagnosticPayload
{
    public const string Name = "TrnProgress";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteString("byteSource", ByteSource);
        w.WriteNumber("completedBytes", CompletedBytes);
        w.WriteNumber("totalBytes", TotalBytes);
        w.WriteNumber("filesAttempted", FilesAttempted);
        w.WriteNumber("bytesPerSecond", Math.Round(BytesPerSecond, 1));
        w.WriteBoolean("estimated", Estimated);
    }
}