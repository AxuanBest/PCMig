namespace PCMig.Core.Transfer;

/// <summary>
/// ★ FIX BATCH 4（P1-1）★ worker 进程 I/O 计数器的进度适配器。
///
/// 为什么需要它：/Z 可续传通道会先给目标文件预分配最终长度（<see cref="RobocopyRunner.TrustsTargetStatForProgress"/>
/// 因此恒为 false），回退枚举也被禁用，robocopy 的 stdout 又是块缓冲 ⇒ 单个 28.5 GB 对象在整个复制
/// 期间**没有任何连续的进度信号**，界面只能停在对象起点（真机现象："网络 35+ MB/s、界面 0 B"）。
///
/// 进程 I/O 计数器（内核为 robocopy 进程累计的读源字节）不受块缓冲影响，是这条通道上唯一连续的
/// 观测源。它的语义边界必须守住：
/// <list type="bullet">
///   <item>**只用于显示**，绝不进入回执（<c>ObjectReceipt.TargetBytes</c>）或校验真值；</item>
///   <item>计数是累计值 ⇒ 每趟尝试都必须 <see cref="BeginAttempt"/> 重开基线（本趟只认本趟读到的）；</item>
///   <item>累计值天然包含"重启/重试重复读过的字节" ⇒ 必须封顶到对象的计划字节；</item>
///   <item>采样读回变小（worker 换了/句柄失效）时保持单调，绝不回退；采样不可用则保留上一次确认值。</item>
/// </list>
/// </summary>
public sealed class WorkerIoProgressTracker
{
    private readonly Func<long?>? _sampleReadBytes;
    private long _baseline;
    private long _confirmed;
    private bool _baselineTaken;

    /// <param name="sampleReadBytes">读取 worker 进程累计读源字节；null（无采样器/进程不存在/无权限）表示不可用。</param>
    public WorkerIoProgressTracker(Func<long?>? sampleReadBytes) => _sampleReadBytes = sampleReadBytes;

    /// <summary>本适配器提供的是不是进程 I/O 遥测（无采样器 ⇒ <see cref="ProgressTruthSource.None"/>）。</summary>
    public ProgressTruthSource Source
        => _sampleReadBytes is null ? ProgressTruthSource.None : ProgressTruthSource.WorkerIoCounters;

    /// <summary>新一轮尝试（或新 worker）开始：以当前计数为基线，本趟从 0 起算。</summary>
    public void BeginAttempt()
    {
        _baseline = TrySample(out var v) ? v : 0;
        _confirmed = 0;
        _baselineTaken = true;
    }

    /// <summary>本趟已确认的读源字节（单调、封顶到 <paramref name="plannedBytes"/>；0 = 尚未确认任何字节）。</summary>
    public long Confirmed(long plannedBytes)
    {
        if (_sampleReadBytes is null) return 0;
        if (!_baselineTaken) BeginAttempt();
        if (TrySample(out var sample))
        {
            var delta = sample - _baseline;
            if (delta > _confirmed)
            {
                var candidate = plannedBytes > 0 ? Math.Min(delta, plannedBytes) : delta;
                if (candidate > _confirmed) _confirmed = candidate;
            }
        }
        return _confirmed;
    }

    private bool TrySample(out long value)
    {
        value = 0;
        if (_sampleReadBytes is null) return false;
        try
        {
            var v = _sampleReadBytes();
            if (v is null || v < 0) return false;
            value = v.Value;
            return true;
        }
        catch
        {
            return false;   // 采样失败不是错误：进度只是"暂时无法确认"，不得让传输受影响
        }
    }
}