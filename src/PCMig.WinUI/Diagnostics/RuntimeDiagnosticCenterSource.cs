using System;
using System.Collections.Generic;
using System.Linq;
using PCMig.Diagnostics;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Analysis;
using PCMig.Diagnostics.Analysis.Feedback;
using PCMig.WinUI.Presentation;

namespace PCMig.WinUI.Diagnostics;

/// <summary>
/// 把诊断运行时适配成诊断中心的数据源。
///
/// 纪律：
///   · **运行时不存在（未启动/已降级/已关闭）时也要能工作**：返回"不可用 + 中性快照"，
///     界面显示"尚未采集"，绝不抛异常、绝不让 Shell 因为诊断缺失而起不来；
///   · 每次读取都**重新解析** <see cref="DiagnosticBootstrap.Runtime"/>（它可能在启动后才建立），
///     不缓存过期引用；
///   · 只暴露只读快照 + 请求采集模式。**没有任何业务动作**。
/// </summary>
public sealed class RuntimeDiagnosticCenterSource : IDiagnosticCenterSource
{
    private static readonly DiagnosticHealthSnapshot Empty = new(
        EventsProduced: 0, EventsAccepted: 0, EventsFiltered: 0, EventsWritten: 0,
        EventsDropped: 0, EventsEvicted: 0, CriticalLost: 0, StickyCriticalLost: false, LossEpoch: 0,
        PublishFaults: 0, SerializationFailures: 0, StorageFailures: 0, SinkFaults: 0,
        IngressCriticalDepth: 0, IngressOperationalDepth: 0, IngressVerboseDepth: 0,
        QueueDepthWriter: 0, QueueDepthAnalyzer: 0, QueueDepthViewer: 0,
        RingBytes: 0, RingEvents: 0, AnalyzerPending: 0, AnalyzerLagMs: 0, UiPending: 0,
        WriterLatencyMs: 0, FlushLatencyMs: 0, LastSuccessfulFlushUnixMs: 0, LastWriteUnixMs: 0,
        WrittenBytes: 0, Rotations: 0, StorageDegraded: false, LastStorageReason: null);

    private DiagnosticRuntime? Runtime => DiagnosticBootstrap.Runtime;

    public bool IsAvailable => Runtime is not null;

    public Guid SessionId => Runtime?.SessionId ?? Guid.Empty;

    public string ModeName => Runtime?.Mode.ToString() ?? "Off";

    public string? StorageRoot => Runtime?.Store?.SessionDir;

    public DiagnosticHealthSnapshot GetHealth() => Runtime?.GetHealthSnapshot() ?? Empty;

    public IReadOnlyList<Incident> GetIncidents()
        => Runtime is { } runtime ? runtime.ActiveIncidents.ToArray() : Array.Empty<Incident>();

    public DiagnosticEvent[] GetRecentEvents(int max)
    {
        var runtime = Runtime;
        if (runtime is null) return Array.Empty<DiagnosticEvent>();
        return runtime.TryGetViewerSnapshot(out var events, max) ? events : Array.Empty<DiagnosticEvent>();
    }

    public FlightStats? GetFlight() => Runtime?.FlightStatistics();

    public RuleEngineStats? GetRules() => Runtime?.RuleEngineStatistics();

    public ExpectationTrackerStats? GetExpectations() => Runtime?.ExpectationStatistics();

    public void RequestMode(CaptureMode mode, string reasonCode) => Runtime?.SetMode(mode, reasonCode);
}