namespace PCMig.Diagnostics;

/// <summary>
/// ★ D6.3 §3 ★ 连续确认水位（contiguous watermark）。
///
/// 为什么不能用 <c>max(sequence)</c>：
///   序号由 <see cref="DiagnosticHub"/> 在**过滤之后**分配，一次分配即代表"这条证据已被接受、
///   应当落盘"。于是"最大已写序号 = 3"完全可能对应实际只有 {1,3} 的情形 —— 2 丢了、或者还在路上。
///   用 max 判断完整性，会把"中间有个洞"说成"完整覆盖到 3"（这是 D6.2 之后独立审计确认的
///   第一类可信度缺陷：**假 Complete**）。
///
/// 三个终态：
///   · <b>Written</b> —— 已落盘（writer 确认写入）；
///   · <b>Dropped</b> —— 已知丢弃（丢失台账有账、有原因码，可被范围化解释）；
///   · <b>Unknown</b> —— 既没落盘、也没入账 ⇒ 真正的**缺口**。
///
/// 水位只允许**连续**前进：结算的序号若正好是"水位 + 1"就前进，并继续吞掉后续连续已结算的序号；
/// 否则只登记进"已结算但被洞卡住"的集合。于是 [1,3] 时 <see cref="Contiguous"/> 只能到 1，
/// <see cref="UnknownCount"/> 显式给出 1 个洞，绝不会声称覆盖到 3。
///
/// 有界：被洞卡住的序号集合有硬上限（<see cref="BeyondCapacity"/>）；一旦溢出，
/// <see cref="TrackingOverflowed"/> 置位，调用方**不得**再声称完整（宁可说不知道）。
/// </summary>
public sealed class SequenceLedger
{
    /// <summary>"已结算但被洞卡住"的序号集合上限（有界，防极端乱序把内存吃掉）。</summary>
    public const int BeyondCapacity = 8192;

    private readonly object _gate = new();
    private readonly HashSet<long> _beyond = new();

    private long _contiguous;
    private long _settledMax;
    private long _settled;
    private long _written;
    private long _dropped;
    private int _overflowed;

    /// <summary>连续水位：序号 1..N **全部**已结算（落盘或已入账丢弃）。</summary>
    public long Contiguous => Interlocked.Read(ref _contiguous);

    /// <summary>
    /// 已结算的最大序号（旧 WrittenWatermark 语义）。**只许用于展示/比较**，
    /// 绝不可用作"完整覆盖到哪里"的判据（那正是 D6.3 要修的缺陷）。
    /// </summary>
    public long SettledMax => Interlocked.Read(ref _settledMax);

    /// <summary>已结算（落盘 + 已入账丢弃）的总条数。</summary>
    public long SettledCount => Interlocked.Read(ref _settled);

    /// <summary>其中已落盘的条数。</summary>
    public long WrittenCount => Interlocked.Read(ref _written);

    /// <summary>其中已入账丢弃的条数（不是"缺口"——它们在丢失台账里）。</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>跟踪集合溢出：完整性与缺口个数都不再可信 ⇒ 必须降级、不得声称完整。</summary>
    public bool TrackingOverflowed => Volatile.Read(ref _overflowed) != 0;

    /// <summary>连续水位之上、已结算最大序号之内的**未知**序号个数（= 已知缺口数）。</summary>
    public long UnknownCount
    {
        get
        {
            lock (_gate)
            {
                var span = _settledMax - _contiguous;
                if (span <= 0) return 0;
                var unknown = span - _beyond.Count;
                return unknown < 0 ? 0 : unknown;
            }
        }
    }

    /// <summary>第一个未知序号（= 连续水位 + 1）。无缺口时它就是"下一个应当结算的序号"。</summary>
    public long FirstUnknown => Contiguous + 1;

    /// <summary>是否存在未解释的缺口（溢出时也返回 true —— 说不清就是说不清）。</summary>
    public bool HasUnknownGap => TrackingOverflowed || UnknownCount > 0;

    /// <summary>某序号已落盘。</summary>
    public void SettleWritten(long sequence) => Settle(sequence, written: true);

    /// <summary>某序号已知丢弃（丢失台账已记）。</summary>
    public void SettleDropped(long sequence) => Settle(sequence, written: false);

    private void Settle(long sequence, bool written)
    {
        // 0 = "非事件行"的家族级占位序号（事件卡/家族记账），不参与事件水位。
        if (sequence <= 0) return;

        lock (_gate)
        {
            // 水位之下：重复结算或迟到结算，忽略（幂等）。
            if (sequence <= _contiguous) return;

            Interlocked.Increment(ref _settled);
            if (written) Interlocked.Increment(ref _written);
            else Interlocked.Increment(ref _dropped);

            if (sequence > _settledMax) _settledMax = sequence;

            // 正好接上水位 ⇒ 前进，并继续吞掉后面已经连续结算的序号。
            if (sequence == _contiguous + 1)
            {
                _contiguous = sequence;
                while (true)
                {
                    var next = _contiguous + 1;
                    if (!_beyond.Remove(next)) break;
                    _contiguous = next;
                }

                return;
            }

            // 前面还有洞：先记着，等洞补上再连续前进。
            if (_beyond.Count >= BeyondCapacity)
            {
                Volatile.Write(ref _overflowed, 1);
                return;
            }

            _beyond.Add(sequence);
        }
    }
}