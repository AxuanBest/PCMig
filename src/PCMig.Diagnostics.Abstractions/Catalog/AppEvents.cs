namespace PCMig.Diagnostics.Abstractions.Events;

/// <summary>APP 族（1000 段）：进程级生命周期与未处理异常。</summary>
public static class AppEvents
{
    /// <summary>进程启动（诊断 session 建立的第一条事实）。</summary>
    public static readonly EventDescriptor ProcessStarted = EventDescriptor.Define(
        DiagnosticCategory.App, 1, "APP.ProcessStarted",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Personal,
        "AppProcessStarted");

    /// <summary>环境指纹（应用/运行时/OS 版本、架构、进程身份）。</summary>
    public static readonly EventDescriptor EnvironmentCaptured = EventDescriptor.Define(
        DiagnosticCategory.App, 2, "APP.EnvironmentCaptured",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Personal,
        "AppEnvironmentCaptured");

    /// <summary>未处理异常（记录但**不吞**：现有 WinUI 策略不设置 Handled，本系统同样不改）。</summary>
    public static readonly EventDescriptor UnhandledException = EventDescriptor.Define(
        DiagnosticCategory.App, 3, "APP.UnhandledException",
        DiagnosticLevel.Critical, DeliveryClass.DurableCritical, PrivacyClassification.Personal,
        "AppUnhandledException");

    /// <summary>正常关闭开始（flush 结果另由 DIA.SessionCleanShutdown 报告）。</summary>
    public static readonly EventDescriptor Closing = EventDescriptor.Define(
        DiagnosticCategory.App, 4, "APP.Closing",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public,
        "AppClosing");

    internal static readonly EventDescriptor[] All = { ProcessStarted, EnvironmentCaptured, UnhandledException, Closing };
}