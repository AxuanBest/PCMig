// ============================================================================
//  UiBatchBuffer —— A.5（P1-5）UI 节流的**纯逻辑**部分
// ============================================================================
//  ★ 文件纪律（违反会撞坏测试链入，见 tests\PCMig.Core.Tests\PCMig.Core.Tests.csproj）★
//    · 本文件**只允许** using System.*；不得引用任何 WinUI / DispatcherQueue /
//      ObservableCollection / XAML 类型；
//    · 它被 §A5LinkedPresentationSources 链入 tests\PCMig.Core.Tests（net8.0）编译，
//      因此它的行为可以被**真正的行为级单测**覆盖 —— 而不是只做源码文本扫描。
//    · 节拍（DispatcherQueueTimer）与 UI 应用不在本文件：节拍在 Presentation\UiFlushPump.cs
//      （不链入测试），UI 应用在 MigrationSessionViewModel。
//
//  为什么必须是"生产者侧入库即限容"：
//    只有当 UI 线程被卡住时，内存里才会堆积待刷新的字符串。若等刷新时再截断，
//    3 秒的长帧就能在内存里堆下十万条字符串 —— 那比工作项风暴更危险。
//
//  为什么用 Queue 而不是 List：
//    溢出时要丢**最旧**一条。List.RemoveAt(0) 是 O(n) 位移；按 630 万次入队 × 200 元素
//    估算约 12 亿次搬移，不可接受。Queue.Dequeue() 是 O(1)。
//
//  为什么用锁而不是 ConcurrentQueue：
//    临界区只有"入队 + 计数 + 溢出出队"，无阻塞 IO、无嵌套锁。用 ConcurrentQueue 反而要
//    额外处理"无锁下如何原子地判容量并丢最旧"。锁的实现更短、更容易证明正确。
//    （若日后测出 20 个 robocopy 线程抢锁的竞争，再换 ConcurrentQueue + 单消费者计数。）
// ============================================================================

using System;
using System.Collections.Generic;

namespace PCMig.WinUI.Presentation;

/// <summary>实时日志行的级别（只允许这三种；与既有 <c>Log(level, text)</c> 的口径一致）。</summary>
public enum UiLogLevel
{
    Info,
    Warn,
    Error,
}

/// <summary>
/// UI 刷新节拍泵的**最小契约**（纯接口，零 WinUI 依赖 ⇒ 与 <see cref="UiBatchBuffer{TSnapshot}"/>
/// 同住本链入文件）。
///
/// 存在这个接口的唯一目的：让 <see cref="MigrationSessionViewModel"/> 只依赖本接口、
/// **不引用任何 WinUI 定时器类型**，从而它仍可被 tests\PCMig.Core.Tests 链入编译并做行为级测试。
/// 真实实现（持 <c>DispatcherQueueTimer</c>）在 UiFlushPump.cs —— 那个文件**不链入测试**。
/// 测试可以用假实现注入，验证"D2：timer 在哪些时机被 Start/Stop"的生命周期契约。
/// </summary>
public interface IUiFlushPump
{
    /// <summary>开始（或继续）滴答。幂等：已经在跑就什么也不做。</summary>
    void Start();

    /// <summary>停止滴答并断开回调（幂等、可重复调用、绝不外抛）。关闭与收尾路径专用。</summary>
    void Stop();

    /// <summary>是否正在滴答（供诊断与契约断言使用）。</summary>
    bool IsRunning { get; }

    /// <summary>节拍间隔（诊断用）。</summary>
    TimeSpan Interval { get; }
}

/// <summary>一条待上屏的日志行（级别 + 真实文本）。</summary>
public readonly record struct UiLogEntry(UiLogLevel Level, string Text);

/// <summary>
/// 一次 Drain 取出的批次。所有集合都是**已经与缓冲区解耦**的独立快照（锁外可用）。
/// </summary>
/// <typeparam name="TSnapshot">进度快照类型（生产为 PCMig.Core 的 ProgressSnapshot）。</typeparam>
public sealed class UiBatch<TSnapshot> where TSnapshot : class
{
    // ⚠ **本类没有"静态空批次"**：丢弃计数是**运行内累计**，必须原样随每次 Drain 返回。
    // 早期实现让"内容全空"直接返回一个计数为 0 的静态 Empty，会把累计值清成 0 —— 那是漏报
    // （违反"不得假装完整"）。空集合只共享 NoFiles / NoLogs 两个单例。

    /// <summary>本次要追加进文件流的行（条数 ≤ 文件容量）。为空集合，不是 null。</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>
    /// 「最近一个文件」的显示文本。**不受容量丢弃影响**：即使它的那一行已被容量挤掉，
    /// 这个值也必须保留 —— 否则界面上"正在复制的文件"会停在一个更早的值上（正确性缺陷）。
    /// </summary>
    public string? LatestFileText { get; }

    /// <summary>本次要追加进实时日志的行（条数 ≤ 日志容量）。为空集合，不是 null。</summary>
    public IReadOnlyList<UiLogEntry> Logs { get; }

    /// <summary>本次是否携带进度快照（最新值胜出）。</summary>
    public bool HasSnapshot { get; }

    /// <summary>最新进度快照（<see cref="HasSnapshot"/> 为 false 时为 null）。</summary>
    public TSnapshot? Snapshot { get; }

    /// <summary>累计被丢弃的文件行数（**运行内累计**，不是本次刷新丢弃数；Clear 时归零）。</summary>
    public int DroppedFiles { get; }

    /// <summary>累计被丢弃的日志行数（同上）。</summary>
    public int DroppedLogs { get; }

    /// <summary>本次批次是否什么也没有（Tick 判活用；**不含**丢弃计数的判定）。</summary>
    public bool IsEmpty => Files.Count == 0 && Logs.Count == 0 && !HasSnapshot && LatestFileText is null;

    /// <summary>常驻空集合单例（避免每次空 Drain 都分配）。</summary>
    internal static readonly IReadOnlyList<string> NoFiles = Array.Empty<string>();
    internal static readonly IReadOnlyList<UiLogEntry> NoLogs = Array.Empty<UiLogEntry>();

    internal UiBatch(
        IReadOnlyList<string> files,
        string? latestFileText,
        IReadOnlyList<UiLogEntry> logs,
        bool hasSnapshot,
        TSnapshot? snapshot,
        int droppedFiles,
        int droppedLogs)
    {
        Files = files;
        LatestFileText = latestFileText;
        Logs = logs;
        HasSnapshot = hasSnapshot;
        Snapshot = snapshot;
        DroppedFiles = droppedFiles;
        DroppedLogs = droppedLogs;
    }
}

/// <summary>
/// 高频 UI 流的合并缓冲（多生产者线程安全 / 单消费者在 UI 线程）。
///
/// 分工：
///   · 生产者（引擎 stdout 泵、stderr 泵、进度轮询线程）调用 <see cref="EnqueueFile"/> /
///     <see cref="EnqueueLog"/> / <see cref="SetLatestSnapshot"/>；
///   · 消费者（UI 线程的节拍回调）调用 <see cref="Drain"/> 取走一批并应用。
///
/// Drain 的关键纪律：**锁内只做 O(1) 引用交换，绝不持锁应用 UI**。
/// </summary>
public sealed class UiBatchBuffer<TSnapshot> where TSnapshot : class
{
    private readonly object _gate = new();
    private readonly int _fileCapacity;
    private readonly int _logCapacity;

    private Queue<string> _pendingFiles;
    private Queue<UiLogEntry> _pendingLogs;
    private string? _latestFileText;
    private TSnapshot? _latestSnapshot;
    private int _droppedFiles;
    private int _droppedLogs;

    /// <param name="fileCapacity">文件流缓冲容量（超出丢最旧一条，并累加丢弃计数）。</param>
    /// <param name="logCapacity">日志缓冲容量（同上）。</param>
    public UiBatchBuffer(int fileCapacity, int logCapacity)
    {
        if (fileCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(fileCapacity));
        if (logCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(logCapacity));
        _fileCapacity = fileCapacity;
        _logCapacity = logCapacity;
        _pendingFiles = new Queue<string>(fileCapacity);
        _pendingLogs = new Queue<UiLogEntry>(logCapacity);
    }

    /// <summary>文件流上限（UI 集合维持同一上限，避免"缓冲 200 / 集合 200"两套口径漂移）。</summary>
    public int FileCapacity => _fileCapacity;

    /// <summary>日志上限。</summary>
    public int LogCapacity => _logCapacity;

    /// <summary>是否还有待上屏的数据（Tick 判活用；任务已结束且无残留 ⇒ timer 自行停掉且不碰 UI）。</summary>
    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _pendingFiles.Count > 0
                    || _pendingLogs.Count > 0
                    || _latestFileText is not null
                    || _latestSnapshot is not null;
            }
        }
    }

    /// <summary>累计丢弃的文件行数（运行内累计）。</summary>
    public int DroppedFiles { get { lock (_gate) { return _droppedFiles; } } }

    /// <summary>累计丢弃的日志行数（运行内累计）。</summary>
    public int DroppedLogs { get { lock (_gate) { return _droppedLogs; } } }

    /// <summary>生产者：入队一个"已复制的文件"行。入库即限容（溢出丢最旧 + 计数）。</summary>
    public void EnqueueFile(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_gate)
        {
            // 「最近一个」无条件覆盖：它不能被容量丢掉（否则界面停在更早的值上）。
            _latestFileText = text;
            if (_pendingFiles.Count >= _fileCapacity)
            {
                _pendingFiles.Dequeue();
                _droppedFiles++;
            }
            _pendingFiles.Enqueue(text);
        }
    }

    /// <summary>生产者：入队一条日志行。入库即限容。</summary>
    public void EnqueueLog(UiLogLevel level, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_gate)
        {
            if (_pendingLogs.Count >= _logCapacity)
            {
                _pendingLogs.Dequeue();
                _droppedLogs++;
            }
            _pendingLogs.Enqueue(new UiLogEntry(level, text));
        }
    }

    /// <summary>生产者：写入"最新进度快照"槽位（最新值胜出，旧值被覆盖、不计入丢弃）。</summary>
    public void SetLatestSnapshot(TSnapshot snapshot)
    {
        if (snapshot is null) return;
        lock (_gate) { _latestSnapshot = snapshot; }
    }

    /// <summary>
    /// 消费者（**只在 UI 线程**调用）：取走并清空当前累积。
    /// 锁内只做 O(1) 引用交换；丢弃计数**不清零**（它是运行内累计，只有 <see cref="Clear"/> 归零）。
    /// </summary>
    public UiBatch<TSnapshot> Drain()
    {
        Queue<string> f;
        Queue<UiLogEntry> l;
        string? latestFile;
        TSnapshot? snapshot;
        int droppedFiles;
        int droppedLogs;

        lock (_gate)
        {
            // ★ 锁内**只**做 O(1) 引用交换（生产者最多被阻塞几十纳秒）★
            f = _pendingFiles;
            l = _pendingLogs;
            _pendingFiles = new Queue<string>(_fileCapacity);
            _pendingLogs = new Queue<UiLogEntry>(_logCapacity);

            latestFile = _latestFileText;
            _latestFileText = null;
            snapshot = _latestSnapshot;
            _latestSnapshot = null;
            droppedFiles = _droppedFiles;
            droppedLogs = _droppedLogs;
        }

        // ★ 出锁之后**才**做 O(n) 的物化（n ≤ 容量上限 200）与 UI 应用 ★
        //   锁内绝不 ToArray、绝不碰 ObservableCollection —— 这是 R5"锁竞争拖慢传输"的防线。
        var files = f.Count == 0 ? (IReadOnlyList<string>)UiBatch<TSnapshot>.NoFiles : f.ToArray();
        var logs = l.Count == 0 ? (IReadOnlyList<UiLogEntry>)UiBatch<TSnapshot>.NoLogs : l.ToArray();

        // ⚠ 即使内容全空也**必须**返回真实累计计数（不能返回计数为 0 的静态单例）：
        //   丢弃计数是运行内累计，清成 0 就是"漏报"。
        return new UiBatch<TSnapshot>(files, latestFile, logs, snapshot is not null, snapshot, droppedFiles, droppedLogs);
    }

    /// <summary>
    /// 运行开始 / 任务切换：清空缓冲**并把丢弃计数归零**（新的一轮运行重新开始计数，
    /// 否则界面上会显示上一轮遗留的"另有 N 行"，那是不诚实的数字）。
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _pendingFiles.Clear();
            _pendingLogs.Clear();
            _latestFileText = null;
            _latestSnapshot = null;
            _droppedFiles = 0;
            _droppedLogs = 0;
        }
    }
}