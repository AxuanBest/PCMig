using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Serialization;

namespace PCMig.Diagnostics;

/// <summary>封段结果。</summary>
public readonly record struct WriterFinalizeResult(
    bool Success,
    int SealedSegments,
    bool FlushAcknowledged,
    string? FailureReason);

/// <summary>
/// 导出截止点结果（D6.1 §2.5）：把"当前活动段"就地封存后，如实回答
/// "封到哪一段、写到哪个序号、刷盘有没有确认、封段成没成功"。
/// </summary>
public readonly record struct ExportSealResult(
    bool Success,
    long WrittenWatermark,
    string? SealedSegmentPath,
    int SealedSegmentCount,
    bool FlushAcknowledged);

/// <summary>
/// JSONL 分段单写者（方案 §15/§16）。
///
/// 职责边界：
///   · **只有它**写 events 段文件（单写者 ⇒ 不需要跨线程文件锁）；
///   · 序列化用复用的 <see cref="ArrayBufferWriter{T}"/> + <see cref="Utf8JsonWriter"/>，
///     不产生"每条事件一个 MemoryStream + 一个 string"的垃圾；
///   · 存储故障 → 自身健康降级 + 台账记丢 + **有界**退避（绝不无限重试、绝不抛给生产线程）；
///   · 只有真的 flush 成功才更新"最后成功落盘时间"（不拿"调用过 flush"冒充"已落盘"）。
/// </summary>
public sealed class JsonlSegmentWriter
{
    private readonly DiagnosticSessionStore _store;
    private readonly DiagnosticRuntimeOptions _options;
    private readonly DiagnosticHealth _health;
    private readonly LossLedger _loss;
    private readonly DiagnosticMeters? _meters;
    private readonly string _family;
    private readonly string _familyDir;
    private readonly List<SegmentManifest> _sealed = new();

    /// <summary>
    /// ★ 单写者闸门（D6.1 §2.5）★ 本类只允许一个**逻辑**写者，但有三条调用路径：
    /// 收件箱消费者、周期 flush（timer 线程）、导出/关闭时的封段。它们必须互相串行，
    /// 否则会出现"timer 正在 Flush 而消费者正在写"这类交错（旧实现就存在这个竞态）。
    /// 锁内只做内存写与文件写，**不做**序列化以外的等待（保持有界）。
    /// </summary>
    private readonly object _writeGate = new();

    private readonly ArrayBufferWriter<byte> _buffer = new(64 * 1024);
    private Utf8JsonWriter? _json;
    private FileStream? _stream;
    private int _segmentIndex;
    private string? _activePath;
    private long _activeBytes;
    private long _activeEvents;
    private long _activeFirstSequence;
    private long _activeLastSequence;
    private DateTimeOffset _activeFirstUtc;
    private DateTimeOffset _activeLastUtc;
    private long _lastFlushMonotonic;
    private long _degradedUntilMonotonic;

    /// <summary>★ D6.1 §19 ★ 尚未 flush 的字节数（配合 WriterBatchMaxBytes 的批刷闸门）。</summary>
    private long _unflushedBytes;
    private long _recoveredFromDegradation;

    public JsonlSegmentWriter(
        DiagnosticSessionStore store,
        DiagnosticRuntimeOptions options,
        DiagnosticHealth health,
        LossLedger loss,
        DiagnosticMeters? meters = null,
        string family = "events")
    {
        _store = store;
        _options = options;
        _health = health;
        _loss = loss;
        _meters = meters;
        _family = family;
        _familyDir = family == "events" ? store.EventsDir : Path.Combine(store.SessionDir, family);
        _segmentIndex = NextSegmentIndex(_familyDir, family);
    }

    public string Family => _family;

    public IReadOnlyList<SegmentManifest> SealedSegments => _sealed;

    public string? ActiveSegmentPath => _activePath;

    public long ActiveBytes => _activeBytes;

    /// <summary>本 writer 的接收水位（已写盘的最后一个序号）。</summary>
    public long WrittenWatermark => Interlocked.Read(ref _writtenWatermark);
    private long _writtenWatermark;

    /// <summary>分支消费者入口。**绝不抛**（异常在这里被转成健康计数 + 台账）。</summary>
    public ValueTask ConsumeAsync(BranchItem item, CancellationToken ct)
    {
        var evt = item.Event!;

        if (IsDegradedNow)
        {
            RecordDrop(evt, "storage-degraded");
            return default;
        }

        lock (_writeGate)
        {
            try
            {
            EnsureOpen();
            _buffer.Clear();
            _json!.Reset(_buffer);
            DiagnosticEventJson.Write(_json, evt);
            _json.Flush();

            var span = _buffer.WrittenSpan;
            _stream!.Write(span);
            _stream.WriteByte((byte)'\n');

            var written = span.Length + 1;
            _activeBytes += written;
            _unflushedBytes += written;
            _activeEvents++;
            if (_activeFirstSequence == 0) _activeFirstSequence = evt.Sequence;
            _activeLastSequence = evt.Sequence;
            if (_activeFirstUtc == default) _activeFirstUtc = evt.TimestampUtc;
            _activeLastUtc = evt.TimestampUtc;

            _health.IncWritten();
            _health.AddWrittenBytes(written);
            _meters?.OnWritten();
            Interlocked.Exchange(ref _writtenWatermark, evt.Sequence);

            var latencyMs = DiagnosticClock.TicksToMs(Stopwatch.GetTimestamp() - evt.MonotonicTimestamp);
            if (latencyMs >= 0)
            {
                _health.RecordWriterLatency(latencyMs);
                _meters?.RecordWriterLatency(latencyMs);
            }

            if (_health.StorageDegraded && Interlocked.Read(ref _recoveredFromDegradation) == 0)
            {
                Interlocked.Exchange(ref _recoveredFromDegradation, 1);
                _health.MarkStorageRecovered();
            }

            FlushIfDueLocked(evt.Delivery == DeliveryClass.DurableCritical);

            // ★ D6.1 §19 ★ 批大小**真的生效**（旧实现没有任何消费者）：
            //   未刷字节达到 WriterBatchMaxBytes 就做一次普通 flush，减少系统调用；
            //   时间闸门（WriterFlushIntervalMs）仍然照旧生效，二者取先到者。
            if (_unflushedBytes >= _options.WriterBatchMaxBytes) FlushLocked(durable: false);
            if (_activeBytes >= _options.SegmentMaxBytes) SealActiveLocked(partial: false);
            }
            catch (Exception ex)
            {
                HandleStorageFailure(ex, evt);
            }
        }

        return default;
    }

    /// <summary>
    /// 追加一条**已经成型**的 JSON 行（事件卡 incidents.jsonl 用）。
    /// 与事件写入共用同一套滚动/刷盘/清单逻辑 ⇒ 口径一致；调用方必须保证"只有一个写者"
    /// （事件写者与事件卡写者是两个不同的 writer 实例、写不同文件）。
    /// </summary>
    public void AppendRawLine(string json)
    {
        if (string.IsNullOrEmpty(json)) return;
        if (IsDegradedNow) return;

        lock (_writeGate)
        {
            try
            {
            EnsureOpen();
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            _stream!.Write(bytes);
            _stream.WriteByte((byte)'\n');
            _activeBytes += bytes.Length + 1;
            _activeEvents++;
            if (_activeFirstUtc == default) _activeFirstUtc = DateTimeOffset.UtcNow;
            _activeLastUtc = DateTimeOffset.UtcNow;
            _health.AddWrittenBytes(bytes.Length + 1);

            FlushIfDueLocked(durable: true);
            if (_activeBytes >= _options.SegmentMaxBytes) SealActiveLocked(partial: false);
            }
            catch (Exception ex)
            {
                HandleStorageFailure(ex, null);
            }
        }
    }

    /// <summary>
    /// ★ D6.1 §2.5 导出截止点 ★ 把当前活动段**就地封存**（bounded：一次 flush-to-disk + 一个清单），
    /// 使"用户点导出前最后几十秒"的证据真正进入已封段集合。之后下一次写入会自动开新段。
    ///
    /// 与收件箱消费者共用同一把写入闸门 ⇒ 不会与正在进行的写/刷盘交错。
    /// **不暂停迁移、不锁 Job**：只在 writer 自己的写路径上加一次有界停顿。
    /// </summary>
    public ExportSealResult SealForExport()
    {
        lock (_writeGate)
        {
            if (_stream is null || _activePath is null)
            {
                // 没有活动段（还没写过东西 / 已被封）：也算成功，覆盖水位就是已落盘水位。
                return new ExportSealResult(true, Interlocked.Read(ref _writtenWatermark), null, _sealed.Count, true);
            }

            var path = _activePath;
            var ok = SealActiveLocked(partial: false);
            return new ExportSealResult(ok, Interlocked.Read(ref _writtenWatermark), path, _sealed.Count, ok);
        }
    }

    /// <summary>周期 flush（timer 调用；空闲时也要收口，否则丢失窗口无限大）。</summary>
    public void FlushIfDue(bool durable)
    {
        lock (_writeGate)
        {
            FlushIfDueLocked(durable);
        }
    }

    private void FlushIfDueLocked(bool durable)
    {
        if (_stream is null) return;
        var now = Stopwatch.GetTimestamp();
        var elapsedMs = DiagnosticClock.TicksToMs(now - _lastFlushMonotonic);
        if (!durable && elapsedMs < _options.WriterFlushIntervalMs) return;

        FlushLocked(durable);
    }

    /// <summary>
    /// 立即 flush（**不**看时间闸门）。
    /// ★ D6.1 §19 ★ 批大小闸门必须走这条路径：第一版我误用了"带时间闸门"的 flush，
    /// 于是把 WriterFlushIntervalMs 调大后批闸门就再也不生效了 —— 正是本审计要防的那类"假接线"。
    /// </summary>
    private void FlushLocked(bool durable)
    {
        if (_stream is null) return;

        var flushStart = Stopwatch.GetTimestamp();
        try
        {
            // durable ⇒ 一直刷到磁盘（Flush(true)）；普通 ⇒ 只到 OS。
            _stream.Flush(flushToDisk: durable);
            _lastFlushMonotonic = Stopwatch.GetTimestamp();
            var flushMs = DiagnosticClock.TicksToMs(_lastFlushMonotonic - flushStart);
            _health.RecordFlushLatency(flushMs);
            _meters?.RecordFlushLatency(flushMs);
            _health.MarkWrittenAndFlushed(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Interlocked.Exchange(ref _unflushedBytes, 0);
        }
        catch (Exception ex)
        {
            // ★ D6.1 §9 ★ 事件卡这类"非事件行"没有 DiagnosticEvent 可记账，
            //   但**写失败绝不允许静默**：按本 writer 的家族记一条台账（incident ⇒ incident 分支）。
            _loss.RecordDrop(
                _family == "incidents" ? DiagnosticBranches.Incident : DiagnosticBranches.Writer,
                DeliveryClass.Operational, 0,
                "line-write-failed:" + ex.GetType().Name, evicted: false);
            HandleStorageFailure(ex, null);
        }
    }

    /// <summary>封掉当前活动段并写清单（正常关闭 / 滚动 / 导出截止点都走这里）。</summary>
    public bool SealActive(bool partial)
    {
        lock (_writeGate)
        {
            return SealActiveLocked(partial);
        }
    }

    private bool SealActiveLocked(bool partial)
    {
        if (_stream is null || _activePath is null) return true;

        var ok = true;
        try
        {
            _stream.Flush(flushToDisk: true);
        }
        catch (Exception ex)
        {
            ok = false;
            HandleStorageFailure(ex, null);
        }

        try
        {
            _stream.Dispose();
        }
        catch (Exception)
        {
            ok = false;
        }
        _stream = null;

        if (_activeEvents > 0 || File.Exists(_activePath))
        {
            var manifest = SegmentRecovery.BuildManifest(
                _family, _activePath, _activeEvents, _activeFirstSequence, _activeLastSequence,
                _activeFirstUtc == default ? DateTimeOffset.UtcNow : _activeFirstUtc,
                _activeLastUtc == default ? DateTimeOffset.UtcNow : _activeLastUtc,
                partial, corruptLines: 0);

            try
            {
                _store.WriteSegmentManifest(_activePath, manifest);
                _sealed.Add(manifest);
                _health.IncRotation();
                _meters?.RecordFlushLatency(0);
            }
            catch (Exception ex)
            {
                ok = false;
                HandleStorageFailure(ex, null);
            }
        }

        _activePath = null;
        _activeBytes = 0;
        _activeEvents = 0;
        _activeFirstSequence = 0;
        _activeLastSequence = 0;
        _activeFirstUtc = default;
        _activeLastUtc = default;
        _segmentIndex++;
        return ok;
    }

    /// <summary>
    /// 关闭时把"最终状态事件"写进**新段**（单写者纪律不变：还是这个 writer、这些文件）。
    /// 这是唯一允许在分支停止之后再写一次的路径，写入量固定为 1–2 行。
    /// </summary>
    public WriterFinalizeResult AppendFinal(IReadOnlyList<DiagnosticEvent> finalEvents)
    {
        if (finalEvents.Count == 0) return new WriterFinalizeResult(true, _sealed.Count, true, null);

        lock (_writeGate)
        {
            try
            {
            EnsureOpen();
            foreach (var evt in finalEvents)
            {
                _buffer.Clear();
                _json!.Reset(_buffer);
                DiagnosticEventJson.Write(_json, evt);
                _json.Flush();
                var span = _buffer.WrittenSpan;
                _stream!.Write(span);
                _stream.WriteByte((byte)'\n');
                _activeBytes += span.Length + 1;
                _activeEvents++;
                if (_activeFirstSequence == 0) _activeFirstSequence = evt.Sequence;
                _activeLastSequence = evt.Sequence;
                if (_activeFirstUtc == default) _activeFirstUtc = evt.TimestampUtc;
                _activeLastUtc = evt.TimestampUtc;
                _health.IncWritten();
                _health.AddWrittenBytes(span.Length + 1);
                Interlocked.Exchange(ref _writtenWatermark, evt.Sequence);
            }

            var sealedOk = SealActiveLocked(partial: false);
                return new WriterFinalizeResult(sealedOk, _sealed.Count, sealedOk && !_health.StorageDegraded, sealedOk ? null : "seal-failed");
            }
            catch (Exception ex)
            {
                HandleStorageFailure(ex, null);
                return new WriterFinalizeResult(false, _sealed.Count, false, ex.GetType().Name);
            }
        }
    }

    /// <summary>退避窗口是否已过（timer 用它决定是否可以重新尝试写盘）。</summary>
    public bool IsDegradedNow => Stopwatch.GetTimestamp() < Interlocked.Read(ref _degradedUntilMonotonic);

    private void EnsureOpen()
    {
        if (_stream is not null) return;

        Directory.CreateDirectory(_familyDir);
        _activePath = Path.Combine(_familyDir, $"{_family}-{_segmentIndex:0000}.jsonl");
        _stream = new FileStream(_activePath, FileMode.Append, FileAccess.Write, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.None);
        _json ??= new Utf8JsonWriter(_buffer, new JsonWriterOptions { Indented = false });
        _json.Reset(_buffer);
        _lastFlushMonotonic = Stopwatch.GetTimestamp();
    }

    private void HandleStorageFailure(Exception ex, DiagnosticEvent? evt)
    {
        var reason = ex.GetType().Name;
        _health.MarkStorageDegraded(reason);
        _meters?.OnStorageFailure();

        Interlocked.Exchange(ref _recoveredFromDegradation, 0);
        var backoffTicks = (long)(_options.StorageRetryBackoffMs / 1000.0 * Stopwatch.Frequency);
        Interlocked.Exchange(ref _degradedUntilMonotonic, Stopwatch.GetTimestamp() + backoffTicks);

        try { _stream?.Dispose(); } catch { /* 关闭失败无所谓，接着走降级 */ }
        _stream = null;

        if (evt is not null) RecordDrop(evt, "storage-failed:" + reason);
    }

    private void RecordDrop(DiagnosticEvent evt, string reason)
    {
        _loss.RecordDrop(DiagnosticBranches.Writer, evt.Delivery, evt.Sequence, reason, evicted: false);
        _meters?.OnDropped(DiagnosticBranches.Writer, evt.Delivery.ToString());
        if (evt.Delivery == DeliveryClass.DurableCritical) _meters?.OnCriticalLost();
    }

    private static int NextSegmentIndex(string dir, string family)
    {
        try
        {
            if (!Directory.Exists(dir)) return 1;
            var max = 0;
            foreach (var file in Directory.EnumerateFiles(dir, $"{family}-*.jsonl"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var dash = name.LastIndexOf('-');
                if (dash < 0) continue;
                if (int.TryParse(name.Substring(dash + 1), out var index) && index > max) max = index;
            }
            return max + 1;
        }
        catch (Exception)
        {
            return 1;
        }
    }
}