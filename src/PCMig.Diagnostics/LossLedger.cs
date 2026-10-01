using PCMig.Diagnostics.Abstractions;

namespace PCMig.Diagnostics;

/// <summary>
/// 丢失类别（D6.1 §4）。**"保留策略"与"故障丢失"必须分开**：
///   · <see cref="RetentionEviction"/>：内存环按容量淘汰旧事件 —— 这是**正常保留策略**，
///     不是故障，**不得**计入 CriticalLost，也**不得**污染证据完整性；
///   · <see cref="Coalesced"/>：队列 DropOldest 合并掉的旧条目（可见，但不等于"没观察到"）；
///   · <see cref="VerboseDrop"/> / <see cref="OperationalLoss"/> / <see cref="CriticalLoss"/>：
///     按**被丢事件的投递类**区分，只有同级或更高级的证据才受影响；
///   · <see cref="AnalyzerLoss"/> / <see cref="WriterLoss"/>：按**分支**区分的消费侧丢失；
///   · <see cref="SamplingSkipped"/> / <see cref="FlightOverwrite"/>：采样跳过与飞行环覆盖。
/// </summary>
public enum LossKind
{
    Unknown = 0,
    RetentionEviction = 1,
    Coalesced = 2,
    VerboseDrop = 3,
    OperationalLoss = 4,
    CriticalLoss = 5,
    AnalyzerLoss = 6,
    WriterLoss = 7,
    SamplingSkipped = 8,
    FlightOverwrite = 9,
}

/// <summary>单个分支的丢弃聚合（按 DeliveryClass 分开计数，口径互不混淆）。</summary>
public readonly record struct LossRecord(
    string Branch,
    DeliveryClass DeliveryClass,
    long Dropped,
    long Evicted,
    long FirstSequence,
    long LastSequence,
    string ReasonCode,
    LossKind Kind)
{
    public long Total => Dropped + Evicted;

    /// <summary>是否属于"故障性丢失"（保留策略/合并/采样不算故障）。</summary>
    public bool IsFault => Kind is LossKind.VerboseDrop or LossKind.OperationalLoss or LossKind.CriticalLoss
        or LossKind.AnalyzerLoss or LossKind.WriterLoss;
}

/// <summary>丢失台账快照。</summary>
public readonly record struct LossLedgerSnapshot(
    long Epoch,
    long TotalDropped,
    long TotalEvicted,
    long CriticalLost,
    bool StickyCriticalLost,
    IReadOnlyList<LossRecord> Records)
{
    /// <summary>按投递类的**分档**世代：只有同级或更高级证据的丢失才会推进它。</summary>
    public long EpochFor(DeliveryClass atLeast) => atLeast switch
    {
        DeliveryClass.Verbose => Epoch,
        DeliveryClass.DurableCritical => CriticalEpoch,
        _ => OperationalEpoch,
    };

    public long VerboseEpoch { get; init; }

    public long OperationalEpoch { get; init; }

    public long CriticalEpoch { get; init; }
}

/// <summary>
/// 丢失台账：诊断系统**自己的**损失必须精确、可见、可引用（方案 §16 / D6.1 §4）。
///
/// 铁律（D6.1 修正后）：
///   · 绝不静默丢弃：每一次丢弃都进台账（含类别），并推进对应档位的世代；
///   · **保留策略不是丢失**：环覆盖（RetentionEviction）与队列合并（Coalesced）**不**推进
///     Operational/Critical 世代，**不**置 sticky critical；
///   · **只有真的丢了 DurableCritical 事件**才置 sticky critical；
///   · 证据完整性按**投递类**与**分支**分别判断 ⇒ 一次 Verbose 丢弃不会让
///     依赖 Operational 证据的规则（持久化/传输/退出码…）全部变成 Inconclusive。
/// </summary>
public sealed class LossLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LossRecord> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _branchEpoch = new(StringComparer.Ordinal);

    private long _epoch;                 // 全局（= Verbose 档）
    private long _epochOperational;
    private long _epochCritical;
    private long _totalDropped;
    private long _totalEvicted;
    private long _criticalLost;
    private long _stickyCriticalLost;
    private long _retentionEvictions;
    private long _coalescedDrops;

    /// <summary>全局丢失世代（任何丢失都会 +1）。**规则不应直接用它判完整性**，用分档世代。</summary>
    public long Epoch => Interlocked.Read(ref _epoch);

    public bool HasLoss => Interlocked.Read(ref _epoch) > 0;

    /// <summary>是否发生过**故障性**丢失（保留策略/合并/采样不算）。</summary>
    public bool HasFaultLoss
    {
        get
        {
            lock (_gate)
            {
                foreach (var record in _records.Values)
                    if (record.IsFault) return true;
                return false;
            }
        }
    }

    /// <summary>DurableCritical 是否发生过丢失（sticky，会话内不复位）。保留策略不置它。</summary>
    public bool StickyCriticalLost => Interlocked.Read(ref _stickyCriticalLost) > 0;

    /// <summary>按投递类分档的世代（只有该级或更高级证据被丢才推进）。</summary>
    public long EpochFor(DeliveryClass atLeast) => atLeast switch
    {
        DeliveryClass.Verbose => Interlocked.Read(ref _epoch),
        DeliveryClass.DurableCritical => Interlocked.Read(ref _epochCritical),
        _ => Interlocked.Read(ref _epochOperational),
    };

    /// <summary>某个分支自己的丢失世代（例如"analyzer 收件箱丢过东西"）。</summary>
    public long BranchEpoch(string branch)
    {
        lock (_gate)
        {
            return _branchEpoch.TryGetValue(branch, out var value) ? value : 0;
        }
    }

    /// <summary>保留性淘汰总数（**仅**内存环按容量覆盖）——不是丢失，但必须可见。</summary>
    public long RetentionEvictions => Interlocked.Read(ref _retentionEvictions);

    /// <summary>队列合并（DropOldest）总数：条目已入队后被合并掉 ⇒ 该分支证据不完整，但不是故障。</summary>
    public long CoalescedDrops => Interlocked.Read(ref _coalescedDrops);

    /// <summary>
    /// 记录一次丢弃。<paramref name="evicted"/> = true 表示被 DropOldest/环覆盖淘汰的旧条目。
    /// 类别由**被丢事件的投递类 + 分支 + 原因**共同决定（见 <see cref="LossKind"/>）。
    /// </summary>
    public void RecordDrop(string branch, DeliveryClass deliveryClass, long sequence, string reasonCode, bool evicted)
    {
        var kind = Classify(branch, deliveryClass, reasonCode, evicted);

        lock (_gate)
        {
            var key = branch + "|" + deliveryClass;
            var current = _records.TryGetValue(key, out var existing)
                ? existing
                : new LossRecord(branch, deliveryClass, 0, 0, sequence, sequence, reasonCode, kind);

            var dropped = evicted ? current.Dropped : current.Dropped + 1;
            var evictedCount = evicted ? current.Evicted + 1 : current.Evicted;
            var first = current.FirstSequence == 0 ? sequence : Math.Min(current.FirstSequence, sequence);
            var last = Math.Max(current.LastSequence, sequence);

            _records[key] = current with
            {
                Dropped = dropped,
                Evicted = evictedCount,
                FirstSequence = first,
                LastSequence = last,
                ReasonCode = reasonCode,
                Kind = kind,
            };

            if (evicted) Interlocked.Increment(ref _totalEvicted);
            else Interlocked.Increment(ref _totalDropped);

            // ★ 分档推进：只有"该级或更高级"的证据真的被丢，才推进该档世代 ★
            //
            // ★ D6.1 §4 关键区分 ★
            //   · FlightOverwrite（内存环按容量淘汰）：**纯保留策略** —— 不推进任何世代、
            //     不计 CriticalLost、不计分支世代。它是"环只保最近一段"，不是"系统丢了证据"。
            //   · Coalesced（队列 DropOldest 合并）：条目**已被接受**后又被合并掉 ⇒ 该分支的
            //     证据确实不完整（可见、要降级），但它不是故障，也不置 sticky critical。
            //   · 其余（Verbose/Operational/Critical 丢失、analyzer/writer 丢失）：按档推进。
            if (kind == LossKind.FlightOverwrite)
            {
                Interlocked.Increment(ref _retentionEvictions);
                return;
            }

            Interlocked.Increment(ref _epoch);                                  // Verbose 档
            if (deliveryClass != DeliveryClass.Verbose)
                Interlocked.Increment(ref _epochOperational);                   // Operational 档

            if (kind == LossKind.Coalesced) Interlocked.Increment(ref _coalescedDrops);

            if (deliveryClass == DeliveryClass.DurableCritical && kind != LossKind.Coalesced)
            {
                Interlocked.Increment(ref _criticalLost);
                Interlocked.Increment(ref _epochCritical);
                Interlocked.Exchange(ref _stickyCriticalLost, 1);
            }

            _branchEpoch[branch] = (_branchEpoch.TryGetValue(branch, out var be) ? be : 0) + 1;
        }
    }

    private static LossKind Classify(string branch, DeliveryClass deliveryClass, string reasonCode, bool evicted)
    {
        if (string.Equals(reasonCode, "ring-overwritten", StringComparison.Ordinal)) return LossKind.FlightOverwrite;
        if (evicted) return LossKind.Coalesced;
        if (string.Equals(reasonCode, "sampling-skipped", StringComparison.Ordinal)) return LossKind.SamplingSkipped;

        if (string.Equals(branch, DiagnosticBranches.Writer, StringComparison.Ordinal)) return LossKind.WriterLoss;
        if (string.Equals(branch, DiagnosticBranches.Analyzer, StringComparison.Ordinal)) return LossKind.AnalyzerLoss;

        return deliveryClass switch
        {
            DeliveryClass.Verbose => LossKind.VerboseDrop,
            DeliveryClass.DurableCritical => LossKind.CriticalLoss,
            _ => LossKind.OperationalLoss,
        };
    }

    public LossLedgerSnapshot Snapshot()
    {
        lock (_gate)
        {
            var records = _records.Values.OrderBy(r => r.Branch, StringComparer.Ordinal).ThenBy(r => r.DeliveryClass).ToArray();
            return new LossLedgerSnapshot(
                Interlocked.Read(ref _epoch),
                Interlocked.Read(ref _totalDropped),
                Interlocked.Read(ref _totalEvicted),
                Interlocked.Read(ref _criticalLost),
                StickyCriticalLost,
                records)
            {
                VerboseEpoch = Interlocked.Read(ref _epoch),
                OperationalEpoch = Interlocked.Read(ref _epochOperational),
                CriticalEpoch = Interlocked.Read(ref _epochCritical),
            };
        }
    }
}