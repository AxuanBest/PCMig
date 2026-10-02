using System.Diagnostics;
using PCMig.Diagnostics.Abstractions;

namespace PCMig.Diagnostics;

/// <summary>触发结果（合并/超限都必须有明确理由，不能静默丢弃触发）。</summary>
public readonly record struct FlightTriggerOutcome(
    bool Accepted,
    string? TriggerId,
    bool Merged,
    string ReasonCode);

/// <summary>Flight Recorder 统计。</summary>
public readonly record struct FlightStats(
    long ObservedEvents,
    long RingEvents,
    long RingBytes,
    long OverwrittenEvents,
    long DroppedEvents,
    int OpenWindows,
    int SealedWindows,
    int PersistedWindows,
    int FailedWindows,
    int MergedTriggers,
    int RejectedTriggers,
    long CheckpointBytes,
    int CheckpointSegments)
{
    /// <summary>★ D6.1 §7 ★ 仍驻留内存的窗口事件条数（落盘后应为 0）。</summary>
    public long RetainedWindowEvents { get; init; }

    public long RetainedWindowPayloadBytes { get; init; }

    public long ReleasedWindowPayloadBytes { get; init; }

    public long TrimmedFinishedWindows { get; init; }
}

/// <summary>
/// 一个窗口封存后的**事实**（供上层如实发布 `DIA.RingSealed`；记录者不自己写事件）。
/// ★ D6.3 §8 ★ 审计 P1-3：窗口"封存"与"完整"必须分开说 —— 到了截止时刻不等于一条都没丢。
/// </summary>
public readonly record struct FlightSealFacts(
    string TriggerId,
    string TriggerCode,
    int ActualCoverageMs,
    long OverwrittenEvents,
    long DroppedEvents,
    bool PostWindowComplete,
    bool Persisted,
    int CheckpointSegments);

/// <summary>一个冻结窗口（pre = 触发前的环快照；post = 触发后继续采集的部分）。</summary>
internal sealed class FlightWindow
{
    public required string TriggerId { get; init; }
    public required string TriggerCode { get; init; }
    public required long OpenedMonotonic { get; init; }
    public required long CloseAtMonotonic { get; init; }
    public required List<DiagnosticEvent> Pre { get; init; }
    public List<DiagnosticEvent> Post { get; } = new();
    public int MergedTriggers { get; set; }
    public bool Sealed { get; set; }
    public bool PostComplete { get; set; }
    public long DroppedInPost { get; set; }
    public long OverwrittenAtTrigger { get; init; }

    /// <summary>
    /// ★ D6.3 §8 ★ pre 快照已填充完毕（`Trigger` 在**锁外**复制，复制完成才置位，置位与发布在锁内）。
    /// 未就绪的窗口**不得**被封存/落盘：否则会读到只填了一半的 pre，甚至在复制进行中把列表清空。
    /// </summary>
    public bool Ready { get; set; }

    /// <summary>窗口从打开到封存的真实时长（毫秒）—— 实际覆盖多少就写多少，不承诺固定秒数。</summary>
    public int ActualCoverageMs { get; set; }

    /// <summary>封存时的检查点段数（如实呈现"kill 后能恢复多少"）。</summary>
    public int CheckpointSegmentsAtSeal { get; set; }

    // ── 落盘后的**轻量元数据**（D6.1 §7）：事件列表必须被释放，只留这些。 ──
    public string? PersistedPath { get; set; }
    public long PersistedBytes { get; set; }
    public int PersistedEventCount { get; set; }
    public string? PersistFailure { get; set; }
    public bool PayloadReleased { get; set; }

    /// <summary>释放事件载荷（落盘成功后调用）。引用、清单与位置都保留 ⇒ 仍可引用与导出。</summary>
    public void ReleasePayload()
    {
        PersistedEventCount = Pre.Count + Post.Count;
        Pre.Clear();
        Pre.TrimExcess();
        Post.Clear();
        Post.TrimExcess();
        PayloadReleased = true;
    }

    /// <summary>当前仍驻留内存的事件条数（有界性测试直接读它）。</summary>
    public int RetainedEventCount => Pre.Count + Post.Count;
}

/// <summary>
/// Flight Recorder（方案 §12）：有界内存环 + 触发前后窗口 + 低频有界磁盘检查点。
///
/// 诚实边界（必须在实现里守住，也在 UI/导出里说清）：
///   · 环是**容量**上限，不是"前 N 秒"承诺：突发流量下实际覆盖时间会更短；
///   · 只用内存救不了 kill -9：能恢复的只有已落盘的检查点与已刷 Operational；
///   · 触发有合并、冷却、并发上限：错误风暴不会生成几百个窗口，但会**记录被合并/拒绝的次数**；
///   · 检查点超预算即停止并记 CoverageChanged（不无限堆内存、不无限写盘）。
/// </summary>
public sealed class FlightRecorder
{
    private readonly object _gate = new();
    private readonly DiagnosticRuntimeOptions _options;
    private readonly DiagnosticHealth _health;
    private readonly LossLedger _loss;
    private readonly string _flightDir;

    // ★ D6.1 §8 ★ 环数组可**交换**（不再 readonly）：触发时把"当前环"整块钉住并换上新环，
//   复制快照放到锁外做 ⇒ 生产线程（Observe）不再被"复制整个环"阻塞。
    private DiagnosticEvent?[] _ring;
    private int[] _ringSizes;
    private int _ringWrite;
    private int _ringCount;
    private long _ringBytes;
    private long _overwritten;
    private long _observed;
    private long _dropped;

    private readonly List<FlightWindow> _windows = new();
    private readonly List<(FlightWindow Window, bool Persisted, string? Failure)> _finished = new();
    private int _windowSeq;
    private int _merged;
    private int _rejected;
    private long _lastTriggerMonotonic;

    private long _checkpointBytes;
    private long _lastCheckpointSequence;
    private long _lastCheckpointMonotonic;
    private int _checkpointSegments;
    private bool _checkpointStopped;

    /// <summary>当前**驻留内存**的窗口事件载荷字节（未落盘的那些）。</summary>
    private long _retainedWindowPayloadBytes;
    private long _releasedWindowPayloadBytes;
    private long _trimmedFinishedWindows;

    /// <summary>窗口载荷的粗略字节数（按事件估算求和，够用于预算闸门）。</summary>
    private static long WindowPayloadBytes(FlightWindow window)
    {
        long total = 0;
        foreach (var evt in window.Pre) total += EventSizeEstimator.Estimate(in evt);
        foreach (var evt in window.Post) total += EventSizeEstimator.Estimate(in evt);
        return total;
    }

    /// <summary>
    /// ★ R-1 收口（D6.3 剩余风险关闭轮）★ 本会话是否发生过"飞行证据不完整"的事实：
    /// post 事件被丢（超容量 / 超载荷预算）、窗口未封口就落盘（关停封存）、窗口落盘失败、
    /// 未就绪窗口被跳过。只增不减的只读聚合 —— 导出侧据此登记 `flightEvidenceIncomplete`
    /// 与 blocker `flight-evidence-incomplete`，避免"包说 Complete、包内窗口清单却写 partial"。
    /// </summary>
    private volatile bool _evidenceIncomplete;

    /// <summary>本次会话的飞行证据是否已确认不完整（一旦为真就永远为真）。</summary>
    public bool EvidenceIncomplete => _evidenceIncomplete;

    public FlightRecorder(DiagnosticSessionStore store, DiagnosticRuntimeOptions options, DiagnosticHealth health, LossLedger loss)
    {
        _options = options;
        _health = health;
        _loss = loss;
        _flightDir = store.FlightDir;
        _ring = new DiagnosticEvent?[Math.Max(1, options.RingEventCapacity)];
        _ringSizes = new int[_ring.Length];
    }

    /// <summary>生产线程入口：**有界、非阻塞**（短临界区，不做 IO、不序列化）。</summary>
    public void Observe(in DiagnosticEvent evt)
    {
        var size = EventSizeEstimator.Estimate(in evt);

        lock (_gate)
        {
            _observed++;

            if (size > _options.RingByteBudget)
            {
                // 单条事件大于整个环预算：记账并丢弃（不能让它把环清空）。
                _dropped++;
                _loss.RecordDrop(DiagnosticBranches.Flight, evt.Delivery, evt.Sequence, "event-larger-than-ring", evicted: false);
                return;
            }

            while (_ringBytes + size > _options.RingByteBudget && _ringCount > 0)
                EvictOldestLocked();

            var slot = _ringWrite % _ring.Length;
            if (_ring[slot] is not null)
            {
                _ringBytes -= _ringSizes[slot];
                _overwritten++;
                EvictReportLocked(_ring[slot]!);
            }

            _ring[slot] = evt;
            _ringSizes[slot] = size;
            _ringBytes += size;
            _ringWrite++;
            if (_ringCount < _ring.Length) _ringCount++;

            foreach (var window in _windows)
            {
                if (window.Sealed) continue;
                if (window.Post.Count >= _ring.Length)
                {
                    window.DroppedInPost++;
                    _dropped++;
                    // ★ R-1 收口 ★ post 丢包与环覆盖不是一回事：它让**这次触发的证据**出现空洞，
                    //   必须进丢失台账（→ LossLedger → Health 降级）并留下"证据不完整"的事实。
                    _evidenceIncomplete = true;
                    _loss.RecordDrop(DiagnosticBranches.Flight, evt.Delivery, evt.Sequence, "post-window-capacity", evicted: false);
                    continue;
                }

                // ★ D6.1 §7 ★ post 增长也必须守**载荷总预算**：否则风暴下驻留量还会缓慢越界。
                if (Interlocked.Read(ref _retainedWindowPayloadBytes) + size > _options.FlightWindowPayloadByteBudget)
                {
                    window.DroppedInPost++;
                    _dropped++;
                    _evidenceIncomplete = true;
                    _loss.RecordDrop(DiagnosticBranches.Flight, evt.Delivery, evt.Sequence, "post-window-budget", evicted: false);
                    continue;
                }

                window.Post.Add(evt);
                Interlocked.Add(ref _retainedWindowPayloadBytes, size);
            }

            _health.SetRingUsage(_ringBytes, _ringCount);
        }
    }

    private void EvictOldestLocked()
    {
        var oldestIndex = (_ringWrite - _ringCount + _ring.Length * 2) % _ring.Length;
        var evicted = _ring[oldestIndex];
        if (evicted is null) { _ringCount = 0; return; }
        _ringBytes -= _ringSizes[oldestIndex];
        _ring[oldestIndex] = null;
        _ringSizes[oldestIndex] = 0;
        _ringCount--;
        _overwritten++;
        EvictReportLocked(evicted);
    }

    private void EvictReportLocked(DiagnosticEvent evicted)
    {
        // 环覆盖属于 retention（正常保留策略），与"队列丢弃"口径分开记账，但都要可见。
        _loss.RecordDrop(DiagnosticBranches.Flight, evicted.Delivery, evicted.Sequence, "ring-overwritten", evicted: true);
    }

    /// <summary>
    /// 触发一次冻结。返回理由：合并/超出并发上限都必须能被上层如实呈现。
    /// </summary>
    public FlightTriggerOutcome Trigger(string triggerCode, string reasonCode)
    {
        (FlightWindow Window, DiagnosticEvent?[] Ring, int Count, int Write)? pending = null;

        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            var openWindows = _windows.Count(w => !w.Sealed);

            if (openWindows >= _options.FlightMaxPinnedWindows)
            {
                _rejected++;
                _loss.RecordDrop(DiagnosticBranches.Flight, DeliveryClass.Operational, 0, "trigger-quota-exceeded", evicted: false);
                return new FlightTriggerOutcome(false, null, false, "trigger-quota-exceeded");
            }

            var cooldownTicks = (long)(_options.FlightTriggerCooldownMs / 1000.0 * Stopwatch.Frequency);
            var lastOpen = _windows.LastOrDefault(w => !w.Sealed);
            if (lastOpen is not null && now - lastOpen.OpenedMonotonic < cooldownTicks)
            {
                _merged++;
                lastOpen.MergedTriggers++;
                return new FlightTriggerOutcome(false, lastOpen.TriggerId, true, "merged-with-recent-trigger");
            }

            var postTicks = (long)(_options.FlightPostWindowMs / 1000.0 * Stopwatch.Frequency);

            // ★ D6.1 §8 ★ 短锁：锁内只做两件有界的事 ——
            //   ① 用已维护的字节数**预判**这次快照的规模（避免"先检查再加入"造成超额）；
            //   ② 钉住当前环并换上新环（O(1)）。
            //   真正的复制（O(环大小)）放到**锁外**：换下的旧数组不再有写入者 ⇒ 仍是精确快照。
            //   代价：每次触发分配一个新环数组（受冷却/并发上限约束，次数有界）。
            var prospectivePreBytes = _ringBytes;
            if (Interlocked.Read(ref _retainedWindowPayloadBytes) + prospectivePreBytes > _options.FlightWindowPayloadByteBudget)
            {
                _rejected++;
                _loss.RecordDrop(DiagnosticBranches.Flight, DeliveryClass.Operational, 0, "window-payload-budget-exhausted", evicted: false);
                return new FlightTriggerOutcome(false, null, false, "window-payload-budget-exhausted");
            }

            var pinnedRing = _ring;
            var pinnedCount = _ringCount;
            var pinnedWrite = _ringWrite;

            _ring = new DiagnosticEvent?[pinnedRing.Length];
            _ringSizes = new int[pinnedRing.Length];
            _ringCount = 0;
            _ringWrite = 0;
            _ringBytes = 0;
            _health.SetRingUsage(0, 0);

            var window = new FlightWindow
            {
                // ★ 文件名安全 ★ 规则 ID 里常有 ':'（例如 rule:UI_FEEDBACK_MISSING），
                //   直接拼进文件名会得到非法路径 ⇒ 窗口永远落不了盘（实测踩过）。
                //   原始 triggerCode 仍原样保留在 DIA.RingTriggered 载荷里，语义不丢。
                TriggerId = $"{++_windowSeq:0000}-{SanitizeForFileName(triggerCode)}",
                TriggerCode = triggerCode,
                OpenedMonotonic = now,
                CloseAtMonotonic = now + postTicks,
                Pre = new List<DiagnosticEvent>(pinnedCount),      // 先在锁内占位，内容在**锁外**填
                OverwrittenAtTrigger = _overwritten,
            };

            _windows.Add(window);
            _lastTriggerMonotonic = now;
            pending = (window, pinnedRing, pinnedCount, pinnedWrite);
        }   // ← 锁在这里结束

        // ★ 锁外复制 ★ 旧数组已被换下，不再有写入者 ⇒ 这次复制是精确快照，
        //   而且**不占用生产线程的锁**（这正是 §8 要的：触发延迟不随环内容线性阻塞 Observe）。
        if (pending is { } work)
        {
            FillPre(work.Window, work.Ring, work.Count, work.Write);

            // ★ D6.3 §8 ★ 就绪与记账一起在锁内完成：
            //   只有置位 Ready 之后，这个窗口才允许被封存/落盘（否则会落出半个 pre）。
            lock (_gate)
            {
                Interlocked.Add(ref _retainedWindowPayloadBytes, WindowPayloadBytes(work.Window));
                work.Window.Ready = true;
                Monitor.PulseAll(_gate);   // 关停路径可能正在等"所有窗口就绪"
            }

            return new FlightTriggerOutcome(true, work.Window.TriggerId, false, reasonCode);
        }

        return new FlightTriggerOutcome(false, null, false, "no-window-created");
    }

    /// <summary>把钉住的环内容填进窗口的 pre 列表（锁外执行）。</summary>
    private static void FillPre(FlightWindow window, DiagnosticEvent?[] ring, int count, int write)
    {
        for (var i = 0; i < count; i++)
        {
            var index = (write - count + i + ring.Length * 2) % ring.Length;
            if (ring[index] is { } evt) window.Pre.Add(evt);
        }
    }

    /// <summary>把任意触发码压成合法文件名片段（只留字母/数字/'-'/'_'/'.'，最多 48 字符）。</summary>
    private static string SanitizeForFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "trigger";
        var sb = new System.Text.StringBuilder(Math.Min(value.Length, 48));
        foreach (var c in value)
        {
            if (sb.Length >= 48) break;
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        }
        return sb.Length == 0 ? "trigger" : sb.ToString();
    }

    /// <summary>
    /// timer 入口：关闭到期窗口并落盘；同时做低频检查点。
    /// 返回本次真正封存掉的窗口事实（上层据此如实发布 `DIA.RingSealed`）。
    /// </summary>
    public IReadOnlyList<FlightSealFacts> Tick(bool checkpointEnabled)
    {
        List<FlightWindow> toPersist = new();
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            foreach (var window in _windows)
            {
                if (window.Sealed) continue;

                // ★ D6.3 §8 ★ 未就绪（pre 还在锁外复制）的窗口不得封存：
                //   否则会落出"半个 pre"的窗口，且与复制线程争用同一个列表。
                if (!window.Ready) continue;
                if (now < window.CloseAtMonotonic) continue;
                window.Sealed = true;
                window.PostComplete = true;
                toPersist.Add(window);
            }

            // ★ D6.1 §7 ★ 已到期的窗口从"活动集合"移出（旧实现永久留在 _windows ⇒ 无界增长）。
            foreach (var window in toPersist)
            {
                _finished.Add((window, false, null));
                _windows.Remove(window);
            }
        }

        var facts = new List<FlightSealFacts>(toPersist.Count);
        foreach (var window in toPersist) facts.Add(PersistWindow(window));

        // ★ D6.1 §19 ★ 检查点间隔**真的生效**（旧实现每次 Tick 都写 ⇒ 配置项形同虚设）。
        if (checkpointEnabled && !_checkpointStopped && CheckpointDue()) WriteCheckpoint();

        return facts;
    }

    /// <summary>检查点是否到期（按配置的间隔；单调时钟，首轮立即到期）。</summary>
    private bool CheckpointDue()
    {
        var intervalTicks = (long)(_options.CheckpointIntervalMs / 1000.0 * Stopwatch.Frequency);
        if (intervalTicks <= 0) return true;

        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastCheckpointMonotonic);
        if (last != 0 && now - last < intervalTicks) return false;

        Interlocked.Exchange(ref _lastCheckpointMonotonic, now);
        return true;
    }

    /// <summary>关闭时把仍打开的窗口封存（post 不完整 ⇒ 如实标记）。返回被封存的事实。</summary>
    public IReadOnlyList<FlightSealFacts> SealOpenWindows()
    {
        List<FlightWindow> toPersist = new();
        lock (_gate)
        {
            // ★ D6.3 §8 ★ 有界等待 pre 复制完成（复制在锁外做，正常只有微秒级；
            //   等不到就**不**封存这个窗口 —— 宁可在丢失台账里留痕，也不落出半个 pre）。
            var deadline = Environment.TickCount64 + 500;
            while (_windows.Any(w => !w.Ready) && Environment.TickCount64 < deadline)
                Monitor.Wait(_gate, 25);

            var notReady = 0;
            foreach (var window in _windows)
            {
                if (window.Sealed) continue;
                if (!window.Ready) { notReady++; continue; }
                window.Sealed = true;
                window.PostComplete = false;
                toPersist.Add(window);
            }

            foreach (var window in toPersist)
            {
                _finished.Add((window, false, "sealed-at-shutdown"));
                _windows.Remove(window);
            }

            if (notReady > 0)
            {
                _evidenceIncomplete = true;
                _loss.RecordDrop(DiagnosticBranches.Flight, DeliveryClass.Operational, 0, "seal-skipped-pre-copy-in-flight", evicted: false);
            }
        }

        var facts = new List<FlightSealFacts>(toPersist.Count);
        foreach (var window in toPersist) facts.Add(PersistWindow(window));
        return facts;
    }

    private FlightSealFacts PersistWindow(FlightWindow window)
    {
        string? failure = null;
        var coverageMs = DiagnosticClock.TicksToMs(Stopwatch.GetTimestamp() - window.OpenedMonotonic);
        window.ActualCoverageMs = (int)Math.Min(int.MaxValue, coverageMs);
        window.CheckpointSegmentsAtSeal = _checkpointSegments;

        // ★ D6.3 §8 ★ `partial` 的真相：到没到截止时刻（PostComplete）与**有没有丢**是两件事。
        //   审计 P1-3：post 超容量/超预算被丢掉的窗口原来仍写 `partial: false` —— 那就是"完整"这个字的谎。
        var complete = window.PostComplete && window.DroppedInPost == 0;

        // ★ R-1 收口 ★ "不完整"必须能传到导出侧：关停封存（未封口）、post 丢包都算缺损。
        if (!complete) _evidenceIncomplete = true;

        try
        {
            Directory.CreateDirectory(_flightDir);
            var path = Path.Combine(_flightDir, $"window-{window.TriggerId}.jsonl");
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                foreach (var evt in window.Pre) writer.WriteLine(Abstractions.Serialization.DiagnosticEventJson.ToJsonLine(evt));
                foreach (var evt in window.Post) writer.WriteLine(Abstractions.Serialization.DiagnosticEventJson.ToJsonLine(evt));
                writer.Flush();
                stream.Flush(true);
            }

            var manifest = SegmentRecovery.BuildManifest(
                "flight", path, window.Pre.Count + window.Post.Count,
                window.Pre.Count > 0 ? window.Pre[0].Sequence : 0,
                window.Post.Count > 0 ? window.Post[^1].Sequence : (window.Pre.Count > 0 ? window.Pre[^1].Sequence : 0),
                window.Pre.Count > 0 ? window.Pre[0].TimestampUtc : DateTimeOffset.UtcNow,
                window.Post.Count > 0 ? window.Post[^1].TimestampUtc : DateTimeOffset.UtcNow,
                partial: !complete, corruptLines: 0);
            DiagnosticSessionStore.WriteManifestFor(path, manifest);

            // ★ D6.1 §7 ★ 记住"落盘在哪、多大、几条"——释放载荷之后这些是唯一的引用信息。
            window.PersistedPath = path;
            window.PersistedBytes = new FileInfo(path).Length;
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name;
            window.PersistFailure = failure;
            _evidenceIncomplete = true;
            _loss.RecordDrop(DiagnosticBranches.Flight, DeliveryClass.Operational, 0, "flight-persist-failed", evicted: false);
        }

        lock (_gate)
        {
            var released = WindowPayloadBytes(window);
            window.ReleasePayload();                       // ★ 落盘后释放大对象（只留轻量元数据）★
            _releasedWindowPayloadBytes += released;
            Interlocked.Add(ref _retainedWindowPayloadBytes, -released);

            for (var i = 0; i < _finished.Count; i++)
            {
                if (ReferenceEquals(_finished[i].Window, window))
                {
                    _finished[i] = (window, failure is null, failure);
                    break;
                }
            }

            // ★ D6.1 §7 ★ 已封窗口的元数据也要有上限：超出即淘汰最旧的（它已经在磁盘上）。
            while (_finished.Count > Math.Max(1, _options.FlightMaxFinishedWindows))
            {
                _finished.RemoveAt(0);
                _trimmedFinishedWindows++;
            }
        }

        return new FlightSealFacts(
            window.TriggerId,
            window.TriggerCode,
            window.ActualCoverageMs,
            window.OverwrittenAtTrigger,
            window.DroppedInPost,
            complete,
            failure is null,
            window.CheckpointSegmentsAtSeal);
    }

    /// <summary>低频检查点：把环里尚未检查点化的事件追加到一个有界文件（kill 后能恢复的就这些）。</summary>
    private void WriteCheckpoint()
    {
        List<DiagnosticEvent> pending;
        lock (_gate)
        {
            pending = new List<DiagnosticEvent>();
            for (var i = 0; i < _ringCount; i++)
            {
                var index = (_ringWrite - _ringCount + i + _ring.Length * 2) % _ring.Length;
                var evt = _ring[index];
                if (evt is not null && evt.Sequence > _lastCheckpointSequence) pending.Add(evt);
            }
            if (pending.Count == 0) return;
            _lastCheckpointSequence = pending[^1].Sequence;
        }

        try
        {
            Directory.CreateDirectory(_flightDir);
            var path = Path.Combine(_flightDir, "checkpoint-active.jsonl");
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 64 * 1024);
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            foreach (var evt in pending)
            {
                var line = Abstractions.Serialization.DiagnosticEventJson.ToJsonLine(evt);
                writer.WriteLine(line);
                _checkpointBytes += line.Length + 1;

                // 硬上限：这一批也不能无限写（预算耗尽即停，不积压）。
                if (_checkpointBytes >= _options.CheckpointMaxBytes) break;
            }
            writer.Flush();
            stream.Flush(true);
            _checkpointSegments++;

            if (_checkpointBytes >= _options.CheckpointMaxBytes)
            {
                _checkpointStopped = true;
                _loss.RecordDrop(DiagnosticBranches.Flight, DeliveryClass.Operational, 0, "checkpoint-budget-exhausted", evicted: false);
            }
        }
        catch (Exception)
        {
            _checkpointStopped = true;
            _loss.RecordDrop(DiagnosticBranches.Flight, DeliveryClass.Operational, 0, "checkpoint-write-failed", evicted: false);
        }
    }

    public FlightStats Stats()
    {
        lock (_gate)
        {
            return new FlightStats(
                _observed,
                _ringCount,
                _ringBytes,
                _overwritten,
                _dropped,
                _windows.Count(w => !w.Sealed),
                _finished.Count,
                _finished.Count(f => f.Persisted),
                _finished.Count(f => !f.Persisted),
                _merged,
                _rejected,
                _checkpointBytes,
                _checkpointSegments)
            {
                RetainedWindowEvents = _windows.Sum(w => w.RetainedEventCount) + _finished.Sum(f => f.Window.RetainedEventCount),
                RetainedWindowPayloadBytes = Interlocked.Read(ref _retainedWindowPayloadBytes),
                ReleasedWindowPayloadBytes = _releasedWindowPayloadBytes,
                TrimmedFinishedWindows = _trimmedFinishedWindows,
            };
        }
    }

    /// <summary>已封存窗口的落盘文件路径（导出时按租约引用）。</summary>
    public IReadOnlyList<string> PersistedWindowPaths()
    {
        lock (_gate)
        {
            return _finished.Where(f => f.Persisted)
                .Select(f => Path.Combine(_flightDir, $"window-{f.Window.TriggerId}.jsonl"))
                .ToArray();
        }
    }
}