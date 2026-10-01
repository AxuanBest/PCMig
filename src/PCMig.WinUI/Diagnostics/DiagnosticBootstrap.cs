using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PCMig.Core.Diagnostics;
using PCMig.Diagnostics;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;

namespace PCMig.WinUI.Diagnostics;

/// <summary>
/// 诊断系统在 WinUI 外壳里的装配根（方案 §5/§21/§32-D3）。
///
/// 纪律：
///   · **尽早启动**：早于任何 VM/Core 观察点被调用（App.OnLaunched 第一件事）；
///   · **fail-open**：存储不可用 ⇒ 只跑内存分支，界面照常；启动本身绝不抛、绝不阻止应用启动；
///   · **绝不改动既有生命周期语义**：关闭路径仍在 Session.StopUiRefresh() 之后、
///     Environment.Exit(0) 之前做**有界**诊断收尾；不设 Environment.Exit 之外的退出方式；
///   · 未处理异常仍然"记录但不吞"（不设置 Handled）。
/// </summary>
public static class DiagnosticBootstrap
{
    private static DiagnosticRuntime? _runtime;
    private static IDisposable? _coreScope;
    private static int _started;

    /// <summary>当前运行时（未启动或降级时为 null；UI 用它读自身 health）。</summary>
    public static DiagnosticRuntime? Runtime => Volatile.Read(ref _runtime);

    /// <summary>诊断是否可用（存储可用 ⇒ 可落盘；否则只有内存分支）。</summary>
    public static bool IsAvailable => Runtime is not null;

    /// <summary>启动诊断（幂等；任何失败都降级返回，绝不抛给调用方）。</summary>
    public static void Start(string appVersion, string? buildId = null)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;

        try
        {
            var options = new DiagnosticRuntimeOptions
            {
                // 默认 Operational：只记语义事实与会话生命周期；Verbose 需要用户显式开 Deep。
                InitialMode = CaptureMode.Operational,
                AppVersion = appVersion,
                BuildId = buildId,
            };

            var runtime = DiagnosticRuntime.Start(options, out var degradedReason);
            if (runtime is null) return;

            // 把运行时装成 Core / WinUI 的 sink（Core 默认是 NoOp，这里显式安装）。
            _coreScope = CoreDiagnostics.Install(runtime);
            Volatile.Write(ref _runtime, runtime);

            PublishAppStarted(runtime);

            if (degradedReason is not null)
            {
                // 降级事实必须可见：界面会显示"诊断存储不可用，仅内存采集"。
                runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                    DiagnosticsEvents.StorageFailed,
                    DiagnosticContext.Root(runtime.SessionId, "DiagnosticBootstrap"),
                    new DiaStorageFailedPayload("session-store", degradedReason, null, EmergencyFallbackActive: false),
                    Level: DiagnosticLevel.Error,
                    Outcome: DiagnosticOutcome.Failed));
            }
        }
        catch (Exception)
        {
            // 装配失败 ⇒ 保持 NoOp（CoreDiagnostics 默认就是 NoOp）：产品照常运行。
            Volatile.Write(ref _runtime, null);
        }
    }

    private static void PublishAppStarted(DiagnosticRuntime runtime)
    {
        try
        {
            var processStart = DateTimeOffset.UtcNow;
            try { using var p = System.Diagnostics.Process.GetCurrentProcess(); processStart = p.StartTime; }
            catch (Exception) { /* 取不到就用当前时间，不伪装精确 */ }

            runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                AppEvents.ProcessStarted,
                DiagnosticContext.Root(runtime.SessionId, "DiagnosticBootstrap"),
                new AppProcessStartedPayload(
                    runtime.Options.AppVersion,
                    Environment.Version.ToString(),
                    Environment.OSVersion.VersionString,
                    Environment.ProcessId,
                    processStart,
                    RuntimeInformation.ProcessArchitecture.ToString(),
                    runtime.Options.BuildId)));

            runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                AppEvents.EnvironmentCaptured,
                DiagnosticContext.Root(runtime.SessionId, "DiagnosticBootstrap"),
                new AppEnvironmentCapturedPayload(
                    Environment.OSVersion.VersionString,
                    RuntimeInformation.ProcessArchitecture.ToString(),
                    Environment.ProcessorCount,
                    GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024),
                    MachineAlias: "[redacted-host]",       // 机器名属 Personal：默认不给明文
                    CapturedUtc: DateTimeOffset.UtcNow)));
        }
        catch (Exception)
        {
            // 观察失败绝不影响启动。
        }
    }

    /// <summary>
    /// 未处理异常的最小记录（**不设置 Handled**：既有策略是照实让进程走既有路径）。
    /// 不保存 Exception.ToString()/Message 原文，只留类型/错误码/规范化帧摘要。
    /// </summary>
    public static void RecordUnhandledException(Exception? exception, string phase)
    {
        var runtime = Runtime;
        if (runtime is null || exception is null) return;

        try
        {
            runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                AppEvents.UnhandledException,
                DiagnosticContext.Root(runtime.SessionId, "App").WithComponent("App"),
                new AppUnhandledExceptionPayload(
                    exception.GetType().FullName ?? exception.GetType().Name,
                    exception.HResult,
                    phase,
                    HandledByApp: false,
                    StackFrameSummary: SummarizeFrames(exception),
                    StackTruncated: true),
                Level: DiagnosticLevel.Critical,
                Outcome: DiagnosticOutcome.Failed,
                ExceptionType: exception.GetType().Name,
                HResult: exception.HResult,
                ErrorDomain: ErrorDomain.Managed,
                Phase: phase));
        }
        catch (Exception)
        {
            // 崩溃路径上绝不因为诊断再抛。
        }
    }

    /// <summary>只保留"类型名.方法名"层级的帧摘要（去路径、去参数、限长度）。</summary>
    private static string SummarizeFrames(Exception exception)
    {
        try
        {
            var trace = exception.StackTrace;
            if (string.IsNullOrEmpty(trace)) return "[no-stack]";

            var lines = trace.Split('\n');
            var builder = new System.Text.StringBuilder(256);
            for (var i = 0; i < lines.Length && i < 5; i++)
            {
                var line = lines[i].Trim();
                var inIndex = line.IndexOf(" in ", StringComparison.Ordinal);
                if (inIndex > 0) line = line.Substring(0, inIndex);   // 去掉 " in C:\path\file.cs:line N"
                if (line.Length == 0) continue;
                if (builder.Length > 0) builder.Append(" <- ");
                builder.Append(line.StartsWith("at ", StringComparison.Ordinal) ? line.Substring(3) : line);
            }
            var summary = builder.ToString();
            return summary.Length > 512 ? summary.Substring(0, 512) : summary;
        }
        catch (Exception)
        {
            return "[stack-unavailable]";
        }
    }

    /// <summary>
    /// 关闭时的有界收尾：停止采集、排空、封段、写 clean marker。
    /// **绝不超过预算**；预算到期就如实记 ShutdownIncomplete（不假装全部保存）。
    /// </summary>
    public static DiagnosticShutdownReport? Shutdown(TimeSpan? budget = null)
    {
        var runtime = Runtime;
        if (runtime is null) return null;

        try
        {
            // 先把"关闭开始"这一事实记下来（它本身也走会话内的收尾路径）。
            runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                AppEvents.Closing,
                DiagnosticContext.Root(runtime.SessionId, "App"),
                new AppClosingPayload("app-close", UiRefreshStopped: true, DiagnosticsDrainRequested: true)));

            var report = runtime.ShutdownAsync(budget ?? TimeSpan.FromMilliseconds(runtime.Options.ShutdownBudgetMs))
                .GetAwaiter().GetResult();
            return report;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            try { _coreScope?.Dispose(); } catch (Exception) { /* ignore */ }
            Volatile.Write(ref _runtime, null);
        }
    }

    /// <summary>测试/工具用：恢复 Core 的 NoOp sink（不影响产品路径）。</summary>
    internal static void ResetForTest()
    {
        try { _coreScope?.Dispose(); } catch (Exception) { /* ignore */ }
        _coreScope = null;
        Volatile.Write(ref _runtime, null);
        Interlocked.Exchange(ref _started, 0);
    }
}