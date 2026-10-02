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
    /// <summary>
    /// ★ D6.3 §7 ★ 维护调度器是否仍在运行。
    ///
    /// 为什么必须可见：维护循环（期望到期判定、刷盘、飞行窗口、保留、丢失发布）原来只有**一个**
    /// try/catch 包住整个循环体，任何一步抛异常都会让循环**整体退出**，此后：
    /// 没人再判超时、没人再刷盘、没人再发布损耗事件 —— 而健康计数看起来一切正常，
    /// 于是诊断会对着一个"已经半死"的会话继续宣称 Healthy + EvidenceComplete（审计 P1-2）。
    /// 单个子任务失败只记 <see cref="MaintenanceFaults"/> 并如实写原因；整体退出才置 false。
    /// </summary>
    public bool MaintenanceAlive { get; init; } = true;

    /// <summary>最近一次**完整成功**的维护节拍（Unix ms；0 = 未跑过或已停止）。</summary>
    public long LastSuccessfulMaintenanceUnixMs { get; init; }

    /// <summary>被隔离的维护子任务失败次数（单步失败不拖死循环，但必须可见）。</summary>
    public long MaintenanceFaults { get; init; }

    /// <summary>
    /// ★ D6.3 §7 ★ 健康快照的**唯一**投影点。
    ///
    /// 为什么必须集中：审计发现"自检自己坏掉了，快照却仍然报健康"这类谎话，
    /// 根因就是判据与投影各写各的。运行时与回归测试现在都走这一处 ——
    /// 以后新增任何"自己坏掉"的事实，只可能漏一次，不可能漏两处。
    /// </summary>
    public static DiagnosticHealthSnapshot Capture(DiagnosticHealth health, LossLedgerSnapshot loss) => new(
        health.EventsProduced,
        health.EventsAccepted,
        health.EventsFiltered,
        health.EventsWritten,
        loss.TotalDropped,
        loss.TotalEvicted,
        loss.CriticalLost,
        loss.StickyCriticalLost,
        loss.Epoch,
        health.PublishFaults,
        health.SerializationFailures,
        health.StorageFailures,
        health.SinkFaults,
        health.IngressCriticalDepth,
        health.IngressOperationalDepth,
        health.IngressVerboseDepth,
        health.WriterDepth,
        health.AnalyzerDepth,
        health.ViewerDepth,
        health.RingBytes,
        health.RingEvents,
        health.AnalyzerPending,
        health.AnalyzerLagMs,
        health.UiPending,
        health.WriterLatencyMs,
        health.FlushLatencyMs,
        health.LastSuccessfulFlushUnixMs,
        health.LastWriteUnixMs,
        health.WrittenBytes,
        health.Rotations,
        health.StorageDegraded,
        health.LastStorageReason)
    {
        MaintenanceAlive = health.MaintenanceAlive,
        LastSuccessfulMaintenanceUnixMs = health.LastSuccessfulMaintenanceUnixMs,
        MaintenanceFaults = health.MaintenanceFaults,
        LastMaintenanceFaultReason = health.LastMaintenanceFaultReason,
        RetentionEvictions = loss.RetentionEvictionCount,
        RuleFaults = health.RuleFaults,
        LastRuleFaultReason = health.LastRuleFaultReason,
    };

    /// <summary>
    /// ★ D6.3 §9（审计 P2-1）★ 保留性淘汰（内存环按容量覆盖）：**可见，但不算丢失**。
    ///
    /// 收口前这一件事有两份互相矛盾的账：<c>EvidenceCoverage</c> 说"保留策略不是丢失"，
    /// 而健康快照把它算进 <see cref="EventsEvicted"/> ⇒ <see cref="EvidenceComplete"/> 直接为假。
    /// 现在两份账目唯一：本属性只出现在这里（以及导出里的 <c>retentionEvictions</c>），
    /// 既不进 <see cref="IsDegraded"/>，也不进 <see cref="EvidenceComplete"/>。
    /// 窗口级"环只保了最近一段"的诚实口径由飞行窗口自己的 <c>partial</c> 承担。
    /// </summary>
    public long RetentionEvictions { get; init; }

    /// <summary>最近一次维护失败的位置与原因（只用于解释"哪里坏了"）。</summary>
    public string? LastMaintenanceFaultReason { get; init; }

    /// <summary>
    /// ★ R-2 收口（D6.3 剩余风险关闭轮）★ 分析器/规则**内部**故障次数。
    ///
    /// 原来这项只存在于导出快照的 <c>rules.faults</c> 与诊断中心计数器里：规则每条都抛异常时，
    /// 包仍然写 <c>degraded:false</c> + <c>evidenceComplete:true</c> + <c>Complete</c>，
    /// 与"Complete 只能在没有任何未声明丢失、且判读真的发生过时才允许说"直接冲突。
    /// 异常被隔离是**不许拖垮 writer**，不是"等于没发生"：规则没跑成 ⇒ 它负责的那类事实没被判读。
    ///
    /// ★ R-2 收口（第二轮：两处无计数静默吞异常）★ 覆盖面明确为**整条分析链**，
    /// 不只是规则本身：期望跟踪器 <c>Observe</c> 的失败（<c>tracker-observe:*</c>）与
    /// 超时结论生成失败（<c>expectation-timeout:*</c>）也进同一本账 ——
    /// 它们同样意味着"某一类判定根本没发生"。原因串始终标明确切位置。
    /// </summary>
    public long RuleFaults { get; init; }

    /// <summary>最近一次规则内部故障的位置与原因（如 <c>rule:ACCESS_DENIED:NullReferenceException</c>）。</summary>
    public string? LastRuleFaultReason { get; init; }

    /// <summary>
    /// 不健康 = 有 DurableCritical 丢失、有存储故障、有任何真实丢弃/合并（证据不完整）、
    /// 有 sink 故障（某个消费者把事件吞了）、或维护调度器已经半死/已退出。
    /// <para>
    /// ★ D6.3 §9 ★ 保留性淘汰（<see cref="RetentionEvictions"/>：内存环按容量覆盖）**不**让健康降级 ——
    /// 它可见、可导出，但不是故障；否则"环只保最近一段"会被读成"诊断系统坏了"。
    /// </para>
    /// </summary>
    public bool IsDegraded => StickyCriticalLost || StorageDegraded
                              || EventsDropped > 0 || EventsEvicted > 0 || CriticalLost > 0
                              || SinkFaults > 0 || MaintenanceFaults > 0 || !MaintenanceAlive
                              || RuleFaults > 0;   // ★ R-2 ★ 规则内部故障也是"自己坏掉了"

    /// <summary>
    /// 证据完整性：任何丢失都让"缺事件"类规则必须降置信度；
    /// **自身故障同样算账** —— 消费者把一条事件吞掉（SinkFaults）等于那条证据不存在，
    /// 维护调度器退出等于后续的判定根本没发生，这两者都不许再宣称"证据完整"。
    ///
    /// ★ D6.3 §9 ★ 保留性淘汰（<see cref="RetentionEvictions"/>）**不在**判据里：
    /// 它是"环只保最近一段"的正常保留策略，不是"系统丢了证据"（与 EvidenceCoverage 同口径）。
    /// </summary>
    public bool EvidenceComplete => LossEpoch == 0 && EventsDropped == 0 && EventsEvicted == 0
                                    && SinkFaults == 0 && MaintenanceAlive
                                    && RuleFaults == 0;   // ★ R-2 ★ 规则没判读成功 ⇒ 不许宣称证据完整
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

    // ★ D6.3 §7 ★ 维护调度器的存活与失败必须是**一等事实**（原来只有一个全局 sink 计数）。
    private long _maintenanceFaults;
    private long _lastSuccessfulMaintenanceUnixMs;
    private string? _lastMaintenanceFaultReason;
    private long _maintenanceAlive = 1;

    // ★ R-2 ★ 分析器/规则自身故障（同样是一等事实：被隔离 ≠ 没发生）。
    private long _ruleFaults;
    private string? _lastRuleFaultReason;

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

    // ─────────────── ★ R-2 分析器/规则自身故障 ───────────────

    /// <summary>规则内部故障次数（隔离计数 + 健康可见，两处同源）。</summary>
    public long RuleFaults => Interlocked.Read(ref _ruleFaults);

    /// <summary>最近一次规则内部故障的位置与原因。</summary>
    public string? LastRuleFaultReason => Volatile.Read(ref _lastRuleFaultReason);

    /// <summary>
    /// 记录一次规则内部故障。**不做去重也不做上限**：次数本身就是"这件事发生过多少次"的事实，
    /// 有界性由 <see cref="Analysis.RuleEngine"/> 的规则数与收件箱容量保证。
    /// </summary>
    public void MarkRuleFault(string reason)
    {
        Interlocked.Increment(ref _ruleFaults);
        Volatile.Write(ref _lastRuleFaultReason, reason);
    }

    // ─────────────── ★ D6.3 §7 维护调度器自身 ───────────────

    /// <summary>维护调度器是否仍在跑（false = 已整体退出，后续判定不会再发生）。</summary>
    public bool MaintenanceAlive => Interlocked.Read(ref _maintenanceAlive) != 0;

    /// <summary>被隔离的维护子任务失败次数。</summary>
    public long MaintenanceFaults => Interlocked.Read(ref _maintenanceFaults);

    /// <summary>最近一次**完整成功**的维护节拍（Unix ms）。</summary>
    public long LastSuccessfulMaintenanceUnixMs => Interlocked.Read(ref _lastSuccessfulMaintenanceUnixMs);

    /// <summary>最近一次维护失败的位置与原因。</summary>
    public string? LastMaintenanceFaultReason => Volatile.Read(ref _lastMaintenanceFaultReason);

    /// <summary>一个维护节拍完整跑完（含所有子任务）才允许调用。</summary>
    public void MarkMaintenanceSucceeded(long unixMs)
        => Interlocked.Exchange(ref _lastSuccessfulMaintenanceUnixMs, unixMs);

    /// <summary>
    /// 某个维护子任务抛异常：**只隔离这一步**，记下位置与原因供解释，绝不因此让整个调度退出。
    /// </summary>
    public void MarkMaintenanceFault(string stage, Exception exception)
    {
        Interlocked.Increment(ref _maintenanceFaults);
        Volatile.Write(ref _lastMaintenanceFaultReason, stage + ":" + exception.GetType().Name);
    }

    /// <summary>维护调度器真的退出了（此时它再也不会推进任何判定）。</summary>
    public void MarkMaintenanceStopped(string reason)
    {
        Interlocked.Exchange(ref _maintenanceAlive, 0);
        Volatile.Write(ref _lastMaintenanceFaultReason, reason);
    }

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