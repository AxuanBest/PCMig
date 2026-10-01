using System.Text.Json;

namespace PCMig.Diagnostics.Abstractions.Payloads;

/// <summary>APP.ProcessStarted：进程启动身份（诊断 session 的第一条事实）。</summary>
public sealed record AppProcessStartedPayload(
    string AppVersion,
    string RuntimeVersion,
    string OsVersion,
    int ProcessId,
    DateTimeOffset ProcessStartUtc,
    string Architecture,
    string? BuildId) : IDiagnosticPayload
{
    public const string Name = "AppProcessStarted";

    public string PayloadName => Name;

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteString("appVersion", AppVersion);
        writer.WriteString("runtimeVersion", RuntimeVersion);
        writer.WriteString("osVersion", OsVersion);
        writer.WriteNumber("processId", ProcessId);
        writer.WriteString("processStartUtc", ProcessStartUtc);
        writer.WriteString("architecture", Architecture);
        PayloadJson.WriteStringOrNull(writer, "buildId", BuildId);
    }
}

/// <summary>APP.EnvironmentCaptured：环境指纹（最小必要，不含用户名/IP 明文）。</summary>
public sealed record AppEnvironmentCapturedPayload(
    string OsVersion,
    string Architecture,
    int ProcessorCount,
    long TotalPhysicalMemoryMb,
    string MachineAlias,
    DateTimeOffset CapturedUtc) : IDiagnosticPayload
{
    public const string Name = "AppEnvironmentCaptured";

    public string PayloadName => Name;

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteString("osVersion", OsVersion);
        writer.WriteString("architecture", Architecture);
        writer.WriteNumber("processorCount", ProcessorCount);
        writer.WriteNumber("totalPhysicalMemoryMb", TotalPhysicalMemoryMb);
        writer.WriteString("machineAlias", MachineAlias);
        writer.WriteString("capturedUtc", CapturedUtc);
    }
}

/// <summary>
/// APP.UnhandledException：未处理异常的最小事实。
/// **不保存** Exception.ToString()/Message 原文（可能含路径/参数/Secret），只保留类型/错误码/规范化帧摘要。
/// </summary>
public sealed record AppUnhandledExceptionPayload(
    string ExceptionType,
    int? HResult,
    string? Phase,
    bool HandledByApp,
    string StackFrameSummary,
    bool StackTruncated) : IDiagnosticPayload
{
    public const string Name = "AppUnhandledException";

    public string PayloadName => Name;

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteString("exceptionType", ExceptionType);
        PayloadJson.WriteNumberOrNull(writer, "hresult", HResult);
        PayloadJson.WriteStringOrNull(writer, "phase", Phase);
        writer.WriteBoolean("handledByApp", HandledByApp);
        writer.WriteString("stackFrameSummary", StackFrameSummary);
        writer.WriteBoolean("stackTruncated", StackTruncated);
    }
}

/// <summary>APP.Closing：关闭开始（结果由 DIA.SessionCleanShutdown / ShutdownIncomplete 如实报告）。</summary>
public sealed record AppClosingPayload(
    string ReasonCode,
    bool UiRefreshStopped,
    bool DiagnosticsDrainRequested) : IDiagnosticPayload
{
    public const string Name = "AppClosing";

    public string PayloadName => Name;

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteString("reasonCode", ReasonCode);
        writer.WriteBoolean("uiRefreshStopped", UiRefreshStopped);
        writer.WriteBoolean("diagnosticsDrainRequested", DiagnosticsDrainRequested);
    }
}