using PCMig.Diagnostics.Abstractions;

namespace PCMig.Diagnostics;

/// <summary>
/// 诊断中心的 viewer 缓存：**有界**、只保留最近事件，供界面读取（方案 §23/§27）。
///
/// 纪律：
///   · 写入来自 viewer 收件箱的消费者线程，读取来自 UI 线程 ⇒ 短临界区；
///   · 满了就丢最旧并计数（显示缓存允许覆盖，但**覆盖计数必须可见**，不能假装"界面就是全部"）；
///   · 界面卡死只会让本缓存被覆盖，**绝不反向阻塞** writer/analyzer（它们各有队列）。
/// </summary>
public sealed class ViewerEventCache
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Queue<DiagnosticEvent> _events;
    private long _droppedOldest;

    public ViewerEventCache(int capacity)
    {
        _capacity = Math.Max(1, capacity);
        _events = new Queue<DiagnosticEvent>(_capacity);
    }

    public int Capacity => _capacity;

    public long DroppedOldest
    {
        get { lock (_gate) return _droppedOldest; }
    }

    public void Add(DiagnosticEvent evt)
    {
        lock (_gate)
        {
            _events.Enqueue(evt);
            while (_events.Count > _capacity)
            {
                _events.Dequeue();
                _droppedOldest++;
            }
        }
    }

    /// <summary>取最近 <paramref name="max"/> 条（时间正序）。调用方在 UI 线程使用返回的数组。</summary>
    public DiagnosticEvent[] Snapshot(int max = int.MaxValue)
    {
        lock (_gate)
        {
            var take = Math.Min(max, _events.Count);
            var array = new DiagnosticEvent[take];
            var skip = _events.Count - take;
            var i = 0;
            foreach (var evt in _events)
            {
                if (skip-- > 0) continue;
                array[i++] = evt;
            }
            return array;
        }
    }

    public int Count
    {
        get { lock (_gate) return _events.Count; }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _events.Clear();
        }
    }
}