namespace PCMig.Diagnostics;

/// <summary>
/// 字节预算：Channel 原生只限**条数**，而事件大小差异很大（一条带堆栈摘要的失败
/// 与一条生命周期事件差一个数量级）。因此条数之外必须有独立的字节闸门。
/// 每次成功的 reserve 都必须恰好 release 一次（入队失败、淘汰、消费完成三条路径）。
/// </summary>
public sealed class ByteBudget
{
    private readonly long _capacity;
    private long _used;

    public ByteBudget(long capacity) => _capacity = capacity < 0 ? 0 : capacity;

    public long Capacity => _capacity;

    public long Used => Interlocked.Read(ref _used);

    /// <summary>尝试预留；不足则返回 false（调用方必须计入丢弃，绝不等待）。</summary>
    public bool TryReserve(long bytes)
    {
        if (bytes <= 0) bytes = 1;
        if (bytes > _capacity) return false;

        while (true)
        {
            var current = Interlocked.Read(ref _used);
            var next = current + bytes;
            if (next > _capacity) return false;
            if (Interlocked.CompareExchange(ref _used, next, current) == current) return true;
        }
    }

    public void Release(long bytes)
    {
        if (bytes <= 0) return;
        Interlocked.Add(ref _used, -bytes);
        // 防御：不允许出现负数（重复 release 是 bug，但绝不能把预算"充爆"）。
        if (Interlocked.Read(ref _used) < 0) Interlocked.Exchange(ref _used, 0);
    }

    public double Utilization => _capacity == 0 ? 0 : Math.Min(1.0, Interlocked.Read(ref _used) / (double)_capacity);
}