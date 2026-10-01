using PCMig.Diagnostics.Abstractions;

namespace PCMig.Diagnostics;

/// <summary>fan-out 统计（每个消费者各记一份，用于证明"互不拖累"）。</summary>
public readonly record struct FanOutStats(
    long ItemsRouted,
    long WriterAccepted,
    long WriterRejected,
    long AnalyzerAccepted,
    long AnalyzerRejected,
    long ViewerAccepted,
    long ViewerRejected,
    long VerboseSkippedForAnalyzer);

/// <summary>
/// 显式 fan-out 阶段（方案 §5/§16）。
///
/// 它是**摄入队列的消费者**，也是**三个消费者收件箱的生产者**：
/// <code>
/// hub → ingress[critical|operational|verbose] → fan-out（本类，后台）
///                                                  ├→ writer 收件箱
///                                                  ├→ analyzer 收件箱
///                                                  └→ viewer 收件箱
/// </code>
///
/// 为什么必须有这一层：
///   · 两个消费者读同一个 Channel 是**竞争消费**而不是广播 ⇒ 谁抢到谁消费，另一个永远看不到；
///   · 显式 fan-out 之后，writer 挂住只影响 writer 收件箱，analyzer / viewer 照常推进；
///   · 每一跳都各自记账（谁丢了多少），不会出现"writer 成功所以规则证据完整"的假象。
///
/// 本阶段的投递**不做任何阻塞**：对三个收件箱都只 TryAccept。
/// </summary>
public sealed class FanOutStage
{
    private readonly BoundedBranch? _writer;
    private readonly BoundedBranch? _analyzer;
    private readonly BoundedBranch? _viewer;
    private readonly DiagnosticMeters? _meters;
    private readonly Func<CaptureMode>? _modeProvider;

    private long _routed;
    private long _writerAccepted;
    private long _writerRejected;
    private long _analyzerAccepted;
    private long _analyzerRejected;
    private long _viewerAccepted;
    private long _viewerRejected;
    private long _verboseSkipped;

    public FanOutStage(BoundedBranch? writer, BoundedBranch? analyzer, BoundedBranch? viewer,
        DiagnosticMeters? meters = null, Func<CaptureMode>? modeProvider = null)
    {
        _writer = writer;
        _analyzer = analyzer;
        _viewer = viewer;
        _meters = meters;
        _modeProvider = modeProvider;
    }

    /// <summary>摄入分支的消费者入口。返回 ValueTask 以匹配分支泵签名（本实现同步完成）。</summary>
    public ValueTask RouteAsync(BranchItem item, CancellationToken ct)
    {
        Interlocked.Increment(ref _routed);

        if (_writer is not null)
        {
            if (_writer.TryAccept(in item)) Interlocked.Increment(ref _writerAccepted);
            else Interlocked.Increment(ref _writerRejected);
        }

        // Verbose 只进 writer（它"允许被丢"，不该占用规则/显示的预算）——
        // **例外**：Deep 模式存在的意义就是"现在把每一行都给我看"，因此 Deep 下额外投给 viewer
        // （viewer 缓存本身有界且覆盖计数可见，卡死也绝不反压 writer/analyzer）。
        var deep = _modeProvider?.Invoke() == CaptureMode.Deep;
        if (item.Delivery == DeliveryClass.Verbose && !deep)
        {
            Interlocked.Increment(ref _verboseSkipped);
            return default;
        }

        if (item.Delivery != DeliveryClass.Verbose && _analyzer is not null)
        {
            if (_analyzer.TryAccept(in item)) Interlocked.Increment(ref _analyzerAccepted);
            else Interlocked.Increment(ref _analyzerRejected);
        }

        if (_viewer is not null)
        {
            if (_viewer.TryAccept(in item)) Interlocked.Increment(ref _viewerAccepted);
            else Interlocked.Increment(ref _viewerRejected);
        }

        _ = _meters;
        return default;
    }

    public FanOutStats Stats() => new(
        Interlocked.Read(ref _routed),
        Interlocked.Read(ref _writerAccepted),
        Interlocked.Read(ref _writerRejected),
        Interlocked.Read(ref _analyzerAccepted),
        Interlocked.Read(ref _analyzerRejected),
        Interlocked.Read(ref _viewerAccepted),
        Interlocked.Read(ref _viewerRejected),
        Interlocked.Read(ref _verboseSkipped));
}