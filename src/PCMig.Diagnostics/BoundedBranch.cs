using System.Diagnostics;
using System.Threading.Channels;
using PCMig.Diagnostics.Abstractions;

namespace PCMig.Diagnostics;

/// <summary>
/// 分支内的条目：事件 + 字节估算（预算必须与事件一起流动，否则 release 会算错）。
///
/// ★ D6.1 §9 ★ <see cref="Event"/> 可为 null、<see cref="RawJson"/> 可承载"非事件"载荷：
/// 事件卡落盘队列传的是**已序列化好的一行 JSON**，它没有对应的规范事件，
/// 但必须复用同一套有界队列/丢失台账（不另造一套会计）。
/// </summary>
public readonly record struct BranchItem(DiagnosticEvent? Event, int Bytes, string? RawJson = null)
{
    /// <summary>投递类：无事件时按 Operational 记账（事件卡是 Operational 级证据）。</summary>
    public DeliveryClass Delivery => Event?.Delivery ?? DeliveryClass.Operational;

    public long Sequence => Event?.Sequence ?? 0;
}

/// <summary>分支统计快照。</summary>
public readonly record struct BranchStats(
    string Name,
    long Enqueued,
    long Processed,
    long DroppedFull,
    long DroppedBudget,
    long Evicted,
    long SinkFaults,
    long Depth)
{
    /// <summary>
    /// ★ D6.1 §9 诊断 ★ 消费者 pump 是否仍在运行。
    /// "队列里有东西但没人取"必须能被分辨：是**没被调度/已退出**，还是**正在处理很慢**。
    /// </summary>
    public bool PumpAlive { get; init; }
}

/// <summary>
/// 一个有界消费分支（writer / analyzer / viewer 各一个）。
///
/// 为什么是**每消费者一个队列**而不是"多个 reader 抢一个 channel"：
/// 竞争读不是广播 —— 谁抢到谁消费，另一个消费者就永远看不到那条事件。
/// 显式 fan-out 才能同时满足"writer 挂了 analyzer 仍能工作""viewer 卡死绝不拖 writer"。
///
/// 生产者侧约定（方案 §16）：
///   · 只调 <see cref="TryAccept"/>，**永不等待**：不 await WriteAsync、不 flush、不碰 UI；
///   · 条数闸门 + 字节闸门都通过才入队；任一不过即入台账并返回 false；
///   · 消费者的异常被吞进 SinkFaults（诊断不得因某个 sink 抛异常而崩）。
/// </summary>
public sealed class BoundedBranch : IAsyncDisposable
{
    private readonly Channel<BranchItem> _channel;
    private readonly ByteBudget _budget;
    private readonly LossLedger _loss;
    private readonly DiagnosticHealth _health;
    private readonly Func<BranchItem, CancellationToken, ValueTask> _consumer;
    private readonly bool _evictsOldest;
    private readonly string _name;

    /// <summary>关闭放弃该分支剩余条目时，按哪一个投递类记账（Verbose 分支记 Verbose）。</summary>
    private readonly DeliveryClass _drainLossClass;

    private long _enqueued;
    private long _processed;
    private long _droppedFull;
    private long _droppedBudget;
    private long _evicted;
    private long _sinkFaults;
    private long _consumerCancelled;
    private Task? _pump;
    private CancellationTokenSource? _cts;

    public BoundedBranch(
        string name,
        int capacity,
        long byteBudget,
        bool evictsOldest,
        LossLedger loss,
        DiagnosticHealth health,
        Func<BranchItem, CancellationToken, ValueTask> consumer,
        DeliveryClass drainLossClass = DeliveryClass.Operational)
    {
        _name = name;
        _evictsOldest = evictsOldest;
        _drainLossClass = drainLossClass;
        _loss = loss;
        _health = health;
        _consumer = consumer;
        _budget = new ByteBudget(byteBudget);

        var options = new BoundedChannelOptions(Math.Max(1, capacity))
        {
            SingleReader = true,          // 每分支只有一个消费者
            SingleWriter = false,         // 任意生产线程都可能 TryAccept
            AllowSynchronousContinuations = false, // 消费者续体绝不在生产线程上跑
            FullMode = evictsOldest ? BoundedChannelFullMode.DropOldest : BoundedChannelFullMode.Wait,
        };

        // DropOldest 模式下 TryWrite 即使"成功"也可能淘汰了一条旧事件 ⇒ 用 itemDropped 精确计数。
        _channel = Channel.CreateBounded<BranchItem>(options, OnItemDropped);
    }

    public string Name => _name;

    /// <summary>
    /// 当前队列深度 = **成功入队** − 已处理 − 已被淘汰。
    /// ★ D6.1 §5 修正 ★ 旧实现还额外减去了 `DroppedFull`/`DroppedBudget`，
    /// 但这两种条目**从未成功入队**（`_enqueued` 不包含它们）⇒ 被减两次 ⇒ 深度变负数。
    /// 同时用 `Math.Max(0, ...)` 兜底，保证界面永远不会显示负队列。
    /// </summary>
    public long Depth => Math.Max(0,
        Interlocked.Read(ref _enqueued) - Interlocked.Read(ref _processed) - Interlocked.Read(ref _evicted));

    /// <summary>消费者 pump 是否仍在运行（已启动且未结束）。</summary>
    public bool IsPumpRunning => _pump is { IsCompleted: false };

    /// <summary>生产线程唯一入口：不阻塞、不抛。</summary>
    public bool TryAccept(in BranchItem item)
    {
        // ① 字节闸门（Channel 只管条数，不管大小）。
        if (!_budget.TryReserve(item.Bytes))
        {
            Interlocked.Increment(ref _droppedBudget);
            _loss.RecordDrop(_name, item.Delivery, item.Sequence, "byte-budget", evicted: false);
            return false;
        }

        // ② 条数闸门。
        if (!_channel.Writer.TryWrite(item))
        {
            _budget.Release(item.Bytes);
            Interlocked.Increment(ref _droppedFull);
            _loss.RecordDrop(_name, item.Delivery, item.Sequence,
                _evictsOldest ? "evict-failed" : "queue-full", evicted: false);
            return false;
        }

        Interlocked.Increment(ref _enqueued);
        _health.SetQueueDepth(_name, Depth);
        return true;
    }

    /// <summary>DropOldest 淘汰回调：被淘汰条目的字节必须在这里释放，并计入 Evicted（Coalesced 口径）。</summary>
    private void OnItemDropped(BranchItem dropped)
    {
        _budget.Release(dropped.Bytes);
        Interlocked.Increment(ref _evicted);
        _loss.RecordDrop(_name, dropped.Delivery, dropped.Sequence, "evicted-oldest", evicted: true);
    }

    public void Start(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pump = Task.Run(() => PumpAsync(_cts.Token));
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var reader = _channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var item))
                {
                    try
                    {
                        await _consumer(item, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // ★ 消费者被取消（关闭预算到期）：该条目**没有**被真正处理完，
                        //   只从通道里取出来了。计数上仍要推进（否则 Depth 会永久偏高），
                        //   但必须单独记一笔"取消"，这样 Drained 才是诚实的。
                        Interlocked.Increment(ref _consumerCancelled);
                        throw;
                    }
                    catch (Exception)
                    {
                        // 单个 sink 抛异常不得杀死分支（更不得影响业务）。
                        Interlocked.Increment(ref _sinkFaults);
                        _health.IncSinkFault();
                    }
                    finally
                    {
                        _budget.Release(item.Bytes);
                        Interlocked.Increment(ref _processed);
                        _health.SetQueueDepth(_name, Depth);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* 正常停止 */ }
        catch (Exception)
        {
            Interlocked.Increment(ref _sinkFaults);
            _health.IncSinkFault();
        }
    }

    /// <summary>
    /// 完成**摄入端**（不再接受新条目），但**不等待**排空 —— 供有序关闭使用：
    /// 先完成 ingress，再依次有界排空 ingress → 收件箱 → writer（D6.1 §3）。
    /// </summary>
    public void CompleteIngest() => _channel.Writer.TryComplete();

    /// <summary>
    /// 停止：先完成写入端，再在给定预算内尽力排空（超时即如实放弃剩余条目并记账）。
    /// **不无限等待**：关闭路径绝不能被诊断拖住（方案 §16/§21）。
    ///
    /// ★ D6.1 §3 修正 ★ 旧实现只在"预算到期"分支里计算 abandoned：
    /// 若 pump 因**外部取消**提前退出（旧实现里关闭会连带取消所有分支），
    /// `finished == _pump` 成立、abandoned 记 0 ⇒ 明明还有条目没处理却报"排空成功"，
    /// 于是 clean marker 落下（假 clean）。现在无论 pump 怎么结束，都按**真实剩余条数**记账。
    /// </summary>
    public async Task<BranchDrainResult> StopAsync(TimeSpan budget)
    {
        _channel.Writer.TryComplete();
        var abandoned = 0;
        var started = Stopwatch.GetTimestamp();

        if (_pump is not null)
        {
            var elapsed = TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency);
            var remaining = budget - elapsed;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

            var finished = await Task.WhenAny(_pump, Task.Delay(remaining)).ConfigureAwait(false);
            if (finished != _pump)
            {
                // 预算到期：取消 pump；剩余条目在下面统一按真实深度记账。
                try { _cts?.Cancel(); } catch (Exception) { /* ignore */ }
            }
        }

        // 真实剩余 = 已入队 − 已处理 − 已丢/已淘汰（Depth 已是这个语义，且保证非负）。
        abandoned = (int)Math.Min(int.MaxValue, Math.Max(0, Depth));
        if (abandoned > 0)
        {
            _loss.RecordDrop(_name, _drainLossClass, 0, "shutdown-budget-exhausted", evicted: false);
            Interlocked.Add(ref _droppedFull, 0);      // 语义说明：这条不是"满丢弃"，而是关闭放弃
        }

        var drained = (int)Math.Min(int.MaxValue,
            Math.Max(0, Interlocked.Read(ref _processed) - Interlocked.Read(ref _consumerCancelled)));
        return new BranchDrainResult(_name, drained, abandoned);
    }

    public BranchStats Stats() => new(
        _name,
        Interlocked.Read(ref _enqueued),
        Interlocked.Read(ref _processed),
        Interlocked.Read(ref _droppedFull),
        Interlocked.Read(ref _droppedBudget),
        Interlocked.Read(ref _evicted),
        Interlocked.Read(ref _sinkFaults),
        Depth)
    {
        PumpAlive = IsPumpRunning,
    };

    public double BudgetUtilization => _budget.Utilization;

    public async ValueTask DisposeAsync()
    {
        _cts?.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }
}

/// <summary>分支停止结果（用于如实报告"排空了多少、放弃了多少"）。</summary>
public readonly record struct BranchDrainResult(string Branch, int Drained, int Abandoned);