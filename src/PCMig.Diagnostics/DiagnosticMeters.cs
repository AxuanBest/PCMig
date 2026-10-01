using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PCMig.Diagnostics;

/// <summary>
/// 指标（方案 §17）：用 BCL 的 <c>System.Diagnostics.Metrics.Meter</c>，不引入 OTel SDK。
///
/// 纪律：
///   · **低基数 tag**：只允许 branch / deliveryClass / category 这类有限集合；
///     JobId / ObjectId / Path / ActionId **禁止**成为长期指标维度（否则 series 爆炸）；
///   · listener 回调可能在记录线程上运行 ⇒ 只做累加/聚合，**绝不做 IO**；
///   · 计数与速率分开：近似值（Estimate）不混进 Counter，避免把估算当成事实。
/// </summary>
public sealed class DiagnosticMeters : IDisposable
{
    public const string MeterName = "PCMig.Diagnostics";

    private readonly Meter _meter;
    private readonly Counter<long> _produced;
    private readonly Counter<long> _accepted;
    private readonly Counter<long> _filtered;
    private readonly Counter<long> _dropped;
    private readonly Counter<long> _evicted;
    private readonly Counter<long> _written;
    private readonly Counter<long> _criticalLost;
    private readonly Counter<long> _storageFailures;
    private readonly Counter<long> _serializationFailures;
    private readonly Histogram<double> _writerLatencyMs;
    private readonly Histogram<double> _flushLatencyMs;
    private readonly Histogram<double> _analyzerLagMs;
    private readonly DiagnosticHealth _health;
    private readonly LossLedger _loss;

    public DiagnosticMeters(DiagnosticHealth health, LossLedger loss)
    {
        _health = health;
        _loss = loss;
        _meter = new Meter(MeterName, DiagnosticActivities.Version);

        _produced = _meter.CreateCounter<long>("pcmig.diagnostics.events.produced", "events", "进入发布路径的事件数");
        _accepted = _meter.CreateCounter<long>("pcmig.diagnostics.events.accepted", "events", "至少被一个分支收下的事件数");
        _filtered = _meter.CreateCounter<long>("pcmig.diagnostics.events.filtered", "events", "被模式/级别策略过滤的事件数");
        _dropped = _meter.CreateCounter<long>("pcmig.diagnostics.events.dropped", "events", "因容量/预算被丢弃的事件数");
        _evicted = _meter.CreateCounter<long>("pcmig.diagnostics.events.evicted", "events", "被 DropOldest 淘汰的事件数");
        _written = _meter.CreateCounter<long>("pcmig.diagnostics.events.written", "events", "成功落盘的事件数");
        _criticalLost = _meter.CreateCounter<long>("pcmig.diagnostics.critical.lost", "events", "DurableCritical 丢失数");
        _storageFailures = _meter.CreateCounter<long>("pcmig.diagnostics.storage.failures", "failures", "诊断自身存储故障次数");
        _serializationFailures = _meter.CreateCounter<long>("pcmig.diagnostics.serialization.failures", "failures", "序列化失败次数");

        _writerLatencyMs = _meter.CreateHistogram<double>("pcmig.diagnostics.writer.latency", "ms", "事件从产生到写入的延迟");
        _flushLatencyMs = _meter.CreateHistogram<double>("pcmig.diagnostics.flush.latency", "ms", "批量刷盘耗时");
        _analyzerLagMs = _meter.CreateHistogram<double>("pcmig.diagnostics.analyzer.lag", "ms", "事件从产生到分析器读到的延迟");

        // 只用有限集合的 tag 值（分支 3 个、投递类 3 个）。
        _meter.CreateObservableGauge("pcmig.diagnostics.queue.depth", QueueDepthMeasurements, "events", "各分支队列深度");
        _meter.CreateObservableGauge("pcmig.diagnostics.ring.bytes", () => _health.RingBytes, "bytes", "Flight 环占用字节");
        _meter.CreateObservableGauge("pcmig.diagnostics.loss.epoch", () => _loss.Epoch, "epoch", "丢失世代（>0 表示证据不完整）");
        _meter.CreateObservableGauge("pcmig.diagnostics.storage.degraded", () => _health.StorageDegraded ? 1 : 0, "flag", "诊断存储是否降级");
    }

    private IEnumerable<Measurement<long>> QueueDepthMeasurements()
    {
        yield return new Measurement<long>(_health.IngressCriticalDepth, new TagList { { "branch", DiagnosticBranches.IngressCritical } });
        yield return new Measurement<long>(_health.IngressOperationalDepth, new TagList { { "branch", DiagnosticBranches.IngressOperational } });
        yield return new Measurement<long>(_health.IngressVerboseDepth, new TagList { { "branch", DiagnosticBranches.IngressVerbose } });
        yield return new Measurement<long>(_health.WriterDepth, new TagList { { "branch", DiagnosticBranches.Writer } });
        yield return new Measurement<long>(_health.AnalyzerDepth, new TagList { { "branch", DiagnosticBranches.Analyzer } });
        yield return new Measurement<long>(_health.ViewerDepth, new TagList { { "branch", DiagnosticBranches.Viewer } });
    }

    internal void OnProduced() => _produced.Add(1);

    internal void OnAccepted(string deliveryClass)
    {
        _accepted.Add(1);
        _ = deliveryClass;
    }

    internal void OnFiltered() => _filtered.Add(1);

    internal void OnDropped(string branch, string deliveryClass)
        => _dropped.Add(1, new TagList { { "branch", branch }, { "deliveryClass", deliveryClass } });

    internal void OnEvicted(string branch) => _evicted.Add(1, new TagList { { "branch", branch } });

    internal void OnWritten() => _written.Add(1);

    internal void OnCriticalLost() => _criticalLost.Add(1);

    internal void OnStorageFailure() => _storageFailures.Add(1);

    internal void OnSerializationFailure() => _serializationFailures.Add(1);

    internal void RecordWriterLatency(double ms) => _writerLatencyMs.Record(ms);

    internal void RecordFlushLatency(double ms) => _flushLatencyMs.Record(ms);

    internal void RecordAnalyzerLag(double ms) => _analyzerLagMs.Record(ms);

    public void Dispose() => _meter.Dispose();
}