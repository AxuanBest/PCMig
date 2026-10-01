using System.Text.Json;

namespace PCMig.Diagnostics.Abstractions.Payloads;

/// <summary>
/// TRN 重试证据（D6.1 §16）：第几次尝试、上限多少、退避多久、稳定错误分类。
/// 只陈述事实，**不改变**重试次数、退避算法或通道选择（/MT → /Z 的决策一字未动）。
/// </summary>
public sealed record TrnRetryPayload(
    string Pass,
    int Attempt,
    int MaxAttempts,
    long BackoffMs,
    string? ErrorClass = null) : IDiagnosticPayload
{
    public const string Name = "TrnRetry";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteString("pass", Pass);
        w.WriteNumber("attempt", Attempt);
        w.WriteNumber("maxAttempts", MaxAttempts);
        w.WriteNumber("backoffMs", BackoffMs);
        PayloadJson.WriteStringOrNull(w, "errorClass", ErrorClass);
    }
}

/// <summary>
/// TRN 暂停/恢复观察：模式（Immediate/Cooperative）、当时的对象、**真实等待时长**。
/// 暂停等待不计入速率分母（T01 口径），所以这个时长必须单独可见而不是混进"传得慢"。
/// </summary>
public sealed record TrnPauseObservedPayload(
    string Mode,
    string? ObjectId,
    long WaitedMs = 0) : IDiagnosticPayload
{
    public const string Name = "TrnPauseObserved";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteString("mode", Mode);
        PayloadJson.WriteStringOrNull(w, "objectId", ObjectId);
        w.WriteNumber("waitedMs", WaitedMs);
    }
}
