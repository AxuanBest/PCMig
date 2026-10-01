namespace PCMig.Diagnostics;

/// <summary>
/// 自身健康快照（供 UI/导出读取；**不经过 logger**，避免"诊断日志诊断自己"的递归）。
/// 由 <see cref="DiagnosticRuntime.GetHealthSnapshot"/> 汇总健康计数 + 丢失台账 + 分支统计后产生。
/// </summary>
public readonly record struct DiagnosticHealthSnapshot(
    long EventsProduced,
    long EventsAccepted,
    long EventsFiltered,
    long EventsWritten,
    long EventsDropped,
    long EventsEvicted,
    long CriticalLost,
    bool StickyCriticalLost,
    long LossEpoch,
    long PublishFaults,
    long SerializationFailures,
    long StorageFailures,
    long SinkFaults,
    long IngressCriticalDepth,
    long IngressOperationalDepth,
    long IngressVerboseDepth,
    long QueueDepthWriter,
    long QueueDepthAnalyzer,
    long QueueDepthViewer,
    long RingBytes,
    long RingEvents,
    long AnalyzerPending,
    long AnalyzerLagMs,
    long UiPending,
    long WriterLatencyMs,
    long FlushLatencyMs,
    long LastSuccessfulFlushUnixMs,
    long LastWriteUnixMs,
    long WrittenBytes,
    long Rotations,
    bool StorageDegraded,
    string? LastStorageReason)
{
    /// <summary>不健康 = 有 DurableCritical 丢失、有存储故障、或有任何丢弃/替换（证据不完整）。</summary>
    public bool IsDegraded => StickyCriticalLost || StorageDegraded
                              || EventsDropped > 0 || EventsEvicted > 0 || CriticalLost > 0;

    /// <summary>证据完整性：任何丢失都让"缺事件"类规则必须降置信度。</summary>
    public bool EvidenceComplete => LossEpoch == 0 && EventsDropped == 0 && EventsEvicted == 0;
}

/// <summary>
/// 诊断系统自身的健康计数。**独立于 logger**：任何一条计数都不通过普通日志写回诊断系统，
/// 否则 writer 故障时会在故障路径上再触发 logging（递归风暴）。
/// 全部字段用 Interlocked/Volatile：读侧无锁、写侧不阻塞生产线程。
/// </summary>
public sealed class DiagnosticHealth
{
    private long _produced;
    private long _accepted;
    private long _filtered;
    private long _written;
    private long _publishFaults;
    private long _serializationFailures;
    private long _storageFailures;
    private long _sinkFaults;
    private long _payloadRejected;
    private long _depthIngressCritical;
    private long _depthIngressOperational;
    private long _depthIngressVerbose;
    private long _depthWriter;
    private long _depthAnalyzer;
    private long _depthViewer;
    private long _ringBytes;
    private long _ringEvents;
    private long _analyzerPending;
    private long _analyzerLagMs;
    private long _uiPending;
    private long _writerLatencyMs;
    private long _flushLatencyMs;
    private long _lastSuccessfulFlushUnixMs;
    private long _lastWriteUnixMs;
    private long _writtenBytes;
    private long _rotations;
    private long _storageDegraded;
    private string? _lastStorageReason;

    public long EventsProduced => Interlocked.Read(ref _produced);
    public long EventsAccepted => Interlocked.Read(ref _accepted);
    public long EventsFiltered => Interlocked.Read(ref _filtered);
    public long EventsWritten => Interlocked.Read(ref _written);
    public long PublishFaults => Interlocked.Read(ref _publishFaults);
    public long SerializationFailures => Interlocked.Read(ref _serializationFailures);
    public long StorageFailures => Interlocked.Read(ref _storageFailures);
    public long SinkFaults => Interlocked.Read(ref _sinkFaults);
    public long RingBytes => Interlocked.Read(ref _ringBytes);
    public long RingEvents => Interlocked.Read(ref _ringEvents);
    public long AnalyzerPending => Interlocked.Read(ref _analyzerPending);
    public long AnalyzerLagMs => Interlocked.Read(ref _analyzerLagMs);
    public long UiPending => Interlocked.Read(ref _uiPending);
    public long WriterLatencyMs => Interlocked.Read(ref _writerLatencyMs);
    public long FlushLatencyMs => Interlocked.Read(ref _flushLatencyMs);
    public long LastSuccessfulFlushUnixMs => Interlocked.Read(ref _lastSuccessfulFlushUnixMs);
    public long LastWriteUnixMs => Interlocked.Read(ref _lastWriteUnixMs);
    public long WrittenBytes => Interlocked.Read(ref _writtenBytes);
    public long Rotations => Interlocked.Read(ref _rotations);
    public bool StorageDegraded => Interlocked.Read(ref _storageDegraded) != 0;
    public string? LastStorageReason => Volatile.Read(ref _lastStorageReason);

    public void IncProduced() => Interlocked.Increment(ref _produced);
    public void IncAccepted() => Interlocked.Increment(ref _accepted);
    public void AddFiltered(long n = 1) => Interlocked.Add(ref _filtered, n);

    /// <summary>★ D6.1 §6 ★ 因超过单事件字节上限而被拒收的条数（可见，不静默）。</summary>
    public void IncPayloadRejected() => Interlocked.Increment(ref _payloadRejected);

    public long PayloadRejected => Interlocked.Read(ref _payloadRejected);
    public void IncWritten() => Interlocked.Increment(ref _written);
    public void IncPublishFault() => Interlocked.Increment(ref _publishFaults);
    public void IncSerializationFailure() => Interlocked.Increment(ref _serializationFailures);
    public void IncSinkFault() => Interlocked.Increment(ref _sinkFaults);
    public void IncRotation() => Interlocked.Increment(ref _rotations);
    public void AddWrittenBytes(long bytes) => Interlocked.Add(ref _writtenBytes, bytes);

    public void SetQueueDepth(string branch, long depth)
    {
        switch (branch)
        {
            case DiagnosticBranches.IngressCritical: Interlocked.Exchange(ref _depthIngressCritical, depth); break;
            case DiagnosticBranches.IngressOperational: Interlocked.Exchange(ref _depthIngressOperational, depth); break;
            case DiagnosticBranches.IngressVerbose: Interlocked.Exchange(ref _depthIngressVerbose, depth); break;
            case DiagnosticBranches.Writer: Interlocked.Exchange(ref _depthWriter, depth); break;
            case DiagnosticBranches.Analyzer: Interlocked.Exchange(ref _depthAnalyzer, depth); break;
            case DiagnosticBranches.Viewer: Interlocked.Exchange(ref _depthViewer, depth); break;
        }
    }

    public long QueueDepth(string branch) => branch switch
    {
        DiagnosticBranches.IngressCritical => Interlocked.Read(ref _depthIngressCritical),
        DiagnosticBranches.IngressOperational => Interlocked.Read(ref _depthIngressOperational),
        DiagnosticBranches.IngressVerbose => Interlocked.Read(ref _depthIngressVerbose),
        DiagnosticBranches.Writer => Interlocked.Read(ref _depthWriter),
        DiagnosticBranches.Analyzer => Interlocked.Read(ref _depthAnalyzer),
        DiagnosticBranches.Viewer => Interlocked.Read(ref _depthViewer),
        _ => 0,
    };

    public void SetRingUsage(long bytes, long events)
    {
        Interlocked.Exchange(ref _ringBytes, bytes);
        Interlocked.Exchange(ref _ringEvents, events);
    }

    public void SetAnalyzerPending(long pending) => Interlocked.Exchange(ref _analyzerPending, pending);
    public void SetAnalyzerLagMs(long lagMs) => Interlocked.Exchange(ref _analyzerLagMs, lagMs);
    public void SetUiPending(long pending) => Interlocked.Exchange(ref _uiPending, pending);
    public void RecordWriterLatency(long ms) => Interlocked.Exchange(ref _writerLatencyMs, ms);
    public void RecordFlushLatency(long ms) => Interlocked.Exchange(ref _flushLatencyMs, ms);

    /// <summary>记"写入/刷盘成功"的挂钟时刻（只有真的成功才允许调用）。</summary>
    public void MarkWrittenAndFlushed(long unixMs)
    {
        Interlocked.Exchange(ref _lastWriteUnixMs, unixMs);
        Interlocked.Exchange(ref _lastSuccessfulFlushUnixMs, unixMs);
    }

    public void MarkStorageDegraded(string reason)
    {
        Interlocked.Increment(ref _storageFailures);
        Interlocked.Exchange(ref _storageDegraded, 1);
        Volatile.Write(ref _lastStorageReason, reason);
    }

    public void MarkStorageRecovered() => Interlocked.Exchange(ref _storageDegraded, 0);

    public long IngressCriticalDepth => Interlocked.Read(ref _depthIngressCritical);
    public long IngressOperationalDepth => Interlocked.Read(ref _depthIngressOperational);
    public long IngressVerboseDepth => Interlocked.Read(ref _depthIngressVerbose);
    public long WriterDepth => Interlocked.Read(ref _depthWriter);
    public long AnalyzerDepth => Interlocked.Read(ref _depthAnalyzer);
    public long ViewerDepth => Interlocked.Read(ref _depthViewer);
}

/// <summary>分支名常量（禁止调用点自创字符串，否则台账/健康/指标三处口径会分叉）。</summary>
public static class DiagnosticBranches
{
    public const string IngressCritical = "ingress-critical";
    public const string IngressOperational = "ingress-operational";
    public const string IngressVerbose = "ingress-verbose";
    public const string Writer = "writer";
    public const string Analyzer = "analyzer";
    public const string Viewer = "viewer";
    public const string Flight = "flight";

    /// <summary>★ D6.1 §9 ★ 事件卡落盘分支（analyzer 不再直接写盘）。</summary>
    public const string Incident = "incident";

    public static readonly string[] All =
    {
        IngressCritical, IngressOperational, IngressVerbose, Writer, Analyzer, Viewer,
    };
}