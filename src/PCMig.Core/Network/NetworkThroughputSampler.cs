using System.Net;
using PCMig.Core.Native;

namespace PCMig.Core.Network;

/// <summary>
/// 网卡探针抽象：<b>只</b>负责"选接口 / 读当前计数器"，把 P/Invoke 关在门外。
/// 【为什么要有这层】纯计算要与真实网卡解耦，否则"计数器重置 / Δt=0 / 无基线"这些边界根本没法单测。
/// </summary>
public interface INetworkInterfaceProbe
{
    /// <summary>按目标地址（可能多条，来自主机名解析）选出本机对应网卡；失败返回 <c>IsUsable == false</c>。</summary>
    InterfaceSelection SelectInterface(IReadOnlyCollection<IPAddress>? destinations, Serilog.ILogger? log = null);

    /// <summary>读指定接口的行信息（含 <c>InOctets</c>）；失败返回 false + 中文理由，不抛异常。</summary>
    bool TryReadRow(uint interfaceIndex, out InterfaceRowInfo row, out string reason);
}

/// <summary>默认探针：包装 Windows IP Helper（<see cref="NetworkInterfaceSelector"/> + <see cref="IpHelperIfEntry"/>）。</summary>
public sealed class IpHelperNetworkProbe : INetworkInterfaceProbe
{
    /// <summary>无状态，可共享单例。</summary>
    public static IpHelperNetworkProbe Instance { get; } = new();

    /// <inheritdoc />
    public InterfaceSelection SelectInterface(IReadOnlyCollection<IPAddress>? destinations, Serilog.ILogger? log = null)
        => NetworkInterfaceSelector.Select(destinations, log);

    /// <inheritdoc />
    public bool TryReadRow(uint interfaceIndex, out InterfaceRowInfo row, out string reason)
        => IpHelperIfEntry.TryReadRow(interfaceIndex, out row, out reason);
}

/// <summary>采样快照（不可变，可跨线程安全读取）。数值用 <see cref="double.NaN"/> 表示"不适用 ⇒ 界面显示 '—'"。</summary>
public sealed record NetworkThroughputSnapshot(
    DateTime TimestampUtc,
    NetworkLinkState State,
    bool Paused,
    uint InterfaceIndex,
    string InterfaceDescription,
    string InterfaceAlias,
    ulong ReceiveLinkSpeed,
    double SpeedBytesPerSecond,
    double EmaBytesPerSecond,
    double StableBytesPerSecond,
    double RawBytesPerSecond,
    bool HasSufficientSamples,
    int StableSampleCount,
    double StableWindowSeconds,
    long RemainingLogicalBytes,
    double EtaSeconds,
    string SpeedText,
    string EtaText,
    string Reason,
    NetworkSampleOutcome LastOutcome,
    long ResetsObserved,
    string SelectionReason)
{
    /// <summary>是否有速度读数（false ⇒ 界面显示 "—"）。</summary>
    public bool HasSpeed => !double.IsNaN(SpeedBytesPerSecond);

    /// <summary>是否有 ETA（false ⇒ 界面显示 "—"）。</summary>
    public bool HasEta => !double.IsNaN(EtaSeconds);

    /// <summary>启动前/未开始的占位快照：状态"连接中"，速度与 ETA 都是 "—"。</summary>
    public static NetworkThroughputSnapshot Initial(string reason) => new(
        DateTime.UtcNow,
        NetworkLinkState.Connecting,
        false,
        0, "", "", 0,
        double.NaN, double.NaN, double.NaN, double.NaN,
        false, 0, 0, 0,
        double.NaN, "—", "—",
        reason,
        NetworkSampleOutcome.InterfaceUnavailable, 0, "");
}

/// <summary>
/// 真实网卡接收吞吐采样器：定时读 <c>MIB_IF_ROW2.InOctets</c>，喂给 <see cref="NetworkThroughputEstimator"/>，
/// 产出一份不可变的 <see cref="NetworkThroughputSnapshot"/> 供界面实时速度与 ETA 使用。
///
/// <para>
/// 【为什么不能再用 ProgressTruth 的速率】旧口径是"本轮新增**逻辑完成**字节 ÷ 本轮有效运行时间"：
/// 目标端已存在的文件被 Robocopy 快速 Skip 时，逻辑字节飞快进入完成口径（35 GB 已存在、只检查 0.2 秒 ⇒
/// 逻辑上"完成"35 GB），但这 35 GB 根本没再经过网卡；SMB/文件缓存也让 Preflight 测速虚高。
/// 所以本采样器读的是**网卡计数器**（真实接收吞吐），并且**单向只读**：
/// 它绝不参与 Receipt / Progress Truth / CompletedBytes / Verifier，绝不会反过来污染这些口径
/// （剩余逻辑字节只作为 ETA 的被除数从外部传入，采样器不回写任何进度）。
/// </para>
///
/// <para>线程模型：定时器回调跑在线程池线程上，回调内**只**做 P/Invoke 读 + 纯计算 + 原子换引用，
/// 不碰任何 UI 对象、不弹窗、不阻塞（界面线程只读 <see cref="Snapshot"/>，读到的是一份不可变记录）。</para>
///
/// <para>容错：定时器回调整体包 try/catch——后台线程上抛出的异常会直接杀进程，这里一律降级为"不可用"快照。</para>
/// </summary>
public sealed class NetworkThroughputSampler : IDisposable
{
    /// <summary>默认采样间隔 500 ms：与界面 0.5 s 刷新一致，够快也不至于让计数器量化误差放大。</summary>
    public static readonly TimeSpan DefaultSampleInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>选接口的重试间隔（秒）：选不出/链路断开时按时重试，等用户把网络连上。</summary>
    public const double ReselectIntervalSeconds = 15.0;

    private readonly object _gate = new();
    private readonly INetworkInterfaceProbe _probe;
    private readonly TimeProvider _time;
    private readonly Serilog.ILogger? _log;
    private readonly NetworkThroughputEstimator _estimator;

    private IReadOnlyCollection<IPAddress>? _destinations;
    private InterfaceSelection _selection = InterfaceSelection.No(
        InterfaceSelectionOutcome.NoEndpoint, "尚未开始：还没有目标地址");
    private DateTime _lastSampleUtc;
    private DateTime _lastSelectUtc;
    /// <summary>最近一次采样的**具体**理由（选路/读行/样本结局）。与显示口径分开存，
    /// 这样 <c>RefreshDisplayLocked</c> 反复刷新时理由不会被反复拼接、越滚越长。</summary>
    private string _lastSpecificReason = "";
    private uint _lastLoggedInterfaceIndex;
    private bool _paused;
    private long _remainingLogicalBytes;

    /// <summary>
    /// ★ 2026-10-08 Preview.2 ★ 任务是否已进入终态（完成 / 完成但有错 / 中断 / 失败 / 取消）。
    /// 由上层在每次快照落位时用<b>已应用到界面的 Phase</b> 判定后单向传入；采样器只据此改显示口径，
    /// 绝不回写任何进度真值（进度真值仍只属于 Progress Truth / 回执）。
    /// </summary>
    private bool _terminal;

    private Timer? _timer;
    private bool _disposed;
    private volatile NetworkThroughputSnapshot _snapshot;

    /// <summary>构造。<paramref name="destinations"/> 是**旧电脑**的地址（IPv4/IPv6 字面量或主机名解析结果）。</summary>
    public NetworkThroughputSampler(
        IReadOnlyCollection<IPAddress>? destinations,
        INetworkInterfaceProbe? probe = null,
        TimeProvider? timeProvider = null,
        Serilog.ILogger? log = null,
        TimeSpan? sampleInterval = null,
        NetworkThroughputEstimator? estimator = null)
    {
        _destinations = destinations;
        _probe = probe ?? IpHelperNetworkProbe.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _log = log;
        _estimator = estimator ?? new NetworkThroughputEstimator();
        SampleInterval = sampleInterval is { TotalMilliseconds: > 0 } interval ? interval : DefaultSampleInterval;
        _lastSpecificReason = "采样器已创建，尚未开始采样";
        _snapshot = NetworkThroughputSnapshot.Initial("采样器已创建，尚未开始采样（速度与 ETA 都显示“—”）");
    }

    /// <summary>采样间隔。</summary>
    public TimeSpan SampleInterval { get; }

    /// <summary>最近一次快照（不可变；界面线程可直接读，无需加锁）。</summary>
    public NetworkThroughputSnapshot Snapshot => _snapshot;

    /// <summary>当前选中的接口（可能不可用）。</summary>
    public InterfaceSelection Selection
    {
        get { lock (_gate) return _selection; }
    }

    /// <summary>是否正在周期采样。</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _timer is not null; }
    }

    /// <summary>打开周期采样（幂等）。第一次回调只建立字节基线。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _timer is not null) return;
            _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, SampleInterval);
            _log?.Debug("网卡吞吐采样已启动：间隔 {Interval} ms", SampleInterval.TotalMilliseconds);
        }
    }

    /// <summary>停止周期采样（幂等；保留已有基线，重新 Start 时会继续用旧基线）。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            var timer = _timer;
            _timer = null;
            timer?.Dispose();
        }
    }

    /// <summary>停止并释放。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            var timer = _timer;
            _timer = null;
            timer?.Dispose();
        }
    }

    /// <summary>更新目标地址（例如 DNS 解析完成、或用户改了目标）并立即重选接口；不启动采样。</summary>
    public void UpdateDestinations(IReadOnlyCollection<IPAddress>? destinations)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _destinations = destinations;
            _selection = _probe.SelectInterface(_destinations, _log);
            _lastSelectUtc = _time.GetUtcNow().UtcDateTime;
            // 换目标可能换网卡；估计器会在下次采样时因索引变化自动重建基线。
            _snapshot = BuildSnapshot(_time.GetUtcNow().UtcDateTime, StateForSelection(_selection, default, false), default, false, _selection.Reason, NetworkSampleOutcome.InterfaceUnavailable);
        }
    }

    /// <summary>标记暂停/恢复：暂停 ⇒ 速度按 0 显示、ETA 显示"—"（立刻生效，不等下一次采样）。</summary>
    public void MarkPaused(bool paused)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _paused = paused;
            RefreshDisplayLocked();
        }
    }

    /// <summary>
    /// ★ 2026-10-08 Preview.2 ★ 标记任务是否进入**终态**（完成 / 完成但有错 / 中断 / 失败 / 取消）。
    /// 与 <see cref="MarkPaused"/> 同一个理由、同一条显示分支：终态下速度按 0 显示、ETA 一律 "—"。
    /// 为什么必须由上层显式告知：采样器只看得到网卡计数器，看不到任务阶段；真机上"任务已结束但底栏
    /// 还在预计剩余 8 小时"就是这条信息缺失造成的（结束态 remainingLogicalBytes 冻结在 &gt;0）。
    /// </summary>
    public void MarkTerminal(bool terminal)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _terminal = terminal;
            RefreshDisplayLocked();
        }
    }

    /// <summary>
    /// ★ 2026-10-08 Preview.2 ★ 从终态**恢复观测**（用户点「恢复任务」/重试，任务重新回到运行时调用）：
    /// 清掉终态标记并**丢弃旧窗口与基线**。丢窗口是必须的 —— 停摆期间网卡还在收背景流量，
    /// 那些样本会把"停摆期间的均值"当成传输速度（恢复后头几拍速度/ETA 全是假的）。
    /// </summary>
    public void ResumeObservation()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _terminal = false;
            _estimator.Reset();
            RefreshDisplayLocked();
        }
    }

    /// <summary>
    /// 传入**剩余逻辑字节**（= 计划 − 已完成，由上层从 Progress Truth 换算）。
    /// 【注意】这是**单向输入**：采样器只把它当 ETA 的被除数，绝不回写任何进度口径。
    /// </summary>
    public void SetRemainingLogicalBytes(long remainingLogicalBytes)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _remainingLogicalBytes = remainingLogicalBytes < 0 ? 0 : remainingLogicalBytes;
            RefreshDisplayLocked();
        }
    }

    /// <summary>
    /// 立即采样一次并返回新快照（同时供测试使用：注入假探针 + 假时钟即可完全确定性驱动）。
    /// 任何内部异常都降级为"不可用"快照，绝不向外抛。
    /// </summary>
    public NetworkThroughputSnapshot Tick()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed) return _snapshot;
                return TickLocked(_time.GetUtcNow().UtcDateTime);
            }
        }
        catch (Exception ex)
        {
            // 定时器回调上不能抛异常（会直接杀进程）；这里把失败如实写进快照。
            var reason = $"网卡吞吐采样异常（{ex.GetType().Name}）：{ex.Message}（已降级为不可用，不影响迁移）";
            try { _log?.Warning("{Reason}", reason); } catch { /* 日志自身失败也不再抛 */ }
            var fallback = _snapshot with
            {
                State = NetworkLinkState.Unavailable,
                SpeedBytesPerSecond = double.NaN,
                SpeedText = "—",
                EtaSeconds = double.NaN,
                EtaText = "—",
                Reason = reason,
            };
            _snapshot = fallback;
            return fallback;
        }
    }

    private NetworkThroughputSnapshot TickLocked(DateTime nowUtc)
    {
        var dt = _lastSampleUtc == default ? 0 : (nowUtc - _lastSampleUtc).TotalSeconds;
        _lastSampleUtc = nowUtc;

        // 选接口：第一次必选；不可用时按间隔重试（用户可能后来才把网络连上）。
        if (!_selection.IsUsable || (nowUtc - _lastSelectUtc).TotalSeconds >= ReselectIntervalSeconds)
        {
            _selection = _probe.SelectInterface(_destinations, _log);
            _lastSelectUtc = nowUtc;
            if (_selection.IsUsable && _selection.InterfaceIndex != _lastLoggedInterfaceIndex)
            {
                _lastLoggedInterfaceIndex = _selection.InterfaceIndex;
                _log?.Information("网卡吞吐观测已选定网卡：{Reason}", _selection.Reason);
            }
        }

        var selection = _selection;
        NetworkSampleOutcome outcome;
        string reason;
        InterfaceRowInfo row = default;
        var hasRow = false;

        if (!selection.IsUsable)
        {
            outcome = _estimator.ObserveUnavailable(selection.Reason, dt).Outcome;
            reason = selection.Reason;
        }
        else if (_probe.TryReadRow(selection.InterfaceIndex, out row, out var rowReason))
        {
            hasRow = true;
            outcome = _estimator.Observe(selection.InterfaceIndex, row.InOctets, dt).Outcome;
            reason = outcome == NetworkSampleOutcome.Ok
                ? $"接口 #{selection.InterfaceIndex}（{row.Alias}）真实接收吞吐采样正常"
                : DescribeOutcome(outcome);
        }
        else
        {
            outcome = _estimator.ObserveUnavailable(rowReason, dt).Outcome;
            reason = rowReason;
        }

        var state = StateForSelection(selection, row, hasRow);
        return Store(BuildSnapshot(nowUtc, state, row, hasRow, reason, outcome));
    }

    /// <summary>无效样本的中文理由（无效样本必填：界面要能说清"为什么这一次没有速率"）。</summary>
    private static string DescribeOutcome(NetworkSampleOutcome outcome) => outcome switch
    {
        NetworkSampleOutcome.NoBaseline => "首次采样：只建立基线，本条不产生速率",
        NetworkSampleOutcome.TooSoon => "两次采样间隔过小：本次不推进基线（字节留到下一次一起算）",
        NetworkSampleOutcome.ClockWentBackwards => "系统时钟回退或时间戳无效：本次不推进基线",
        NetworkSampleOutcome.CounterReset => "网卡计数器已重置：本次样本作废并重建基线",
        NetworkSampleOutcome.StaleGap => "距上次有效采样过久（休眠/长时间不可用）：重新开始窗口",
        NetworkSampleOutcome.InterfaceUnavailable => "接口不可用：读不到网卡计数器",
        _ => "网卡吞吐采样：无速率产出",
    };

    /// <summary>根据"是否选到接口 + 是否读到行 + 行里的 OperStatus/MediaConnectState"判定显示状态。</summary>
    private static NetworkLinkState StateForSelection(InterfaceSelection selection, in InterfaceRowInfo row, bool hasRow)
    {
        if (!selection.IsUsable)
        {
            // 还在连（可能过一会儿就连上）与根本不可观测，要分开——界面文案不同。
            return selection.Outcome switch
            {
                InterfaceSelectionOutcome.NoEndpoint => NetworkLinkState.Connecting,
                InterfaceSelectionOutcome.NoMatchingInterface => NetworkLinkState.Connecting,
                InterfaceSelectionOutcome.RoutingFailed => NetworkLinkState.Connecting,
                _ => NetworkLinkState.Unavailable,
            };
        }
        if (!hasRow) return NetworkLinkState.Unavailable;
        return row.IsOperational ? NetworkLinkState.Connected : NetworkLinkState.Disconnected;
    }

    private NetworkThroughputSnapshot BuildSnapshot(
        DateTime nowUtc,
        NetworkLinkState state,
        in InterfaceRowInfo row,
        bool hasRow,
        string reason,
        NetworkSampleOutcome outcome)
    {
        _lastSpecificReason = reason;
        var display = NetworkDisplayMetrics.Compute(
            state,
            _paused,
            _estimator.HasReading,
            _estimator.EmaBytesPerSecond,
            _estimator.HasSufficientSamples,
            _estimator.StableBytesPerSecond,
            _remainingLogicalBytes,
            _terminal);

        return new NetworkThroughputSnapshot(
            nowUtc,
            state,
            _paused,
            hasRow ? row.InterfaceIndex : 0,
            hasRow ? row.Description : "",
            hasRow ? row.Alias : "",
            hasRow ? row.ReceiveLinkSpeed : 0,
            display.SpeedBytesPerSecond,
            _estimator.EmaBytesPerSecond,
            _estimator.StableBytesPerSecond,
            double.NaN, // raw 瞬时值不进快照（界面只该看 EMA；瞬时值留给日志/诊断）
            _estimator.HasSufficientSamples,
            _estimator.StableSampleCount,
            _estimator.StableWindowSeconds,
            _remainingLogicalBytes,
            display.EtaSeconds,
            display.SpeedText,
            display.EtaText,
            // 理由要同时说清两件事：①这一次的采样/选路具体发生了什么（_lastSpecificReason，例如"读不到行信息"/"同子网选不出"），
            // ②界面为什么显示"—"（display.Reason）。只留后者的话，界面与日志里只剩笼统的"不可用"，
            // 真机上出问题（选错网卡、P/Invoke 失败、计数器重置）时无从下手排查。
            ComposeReason(display),
            outcome,
            _estimator.ResetsObserved,
            _selection.Reason);
    }

    /// <summary>暂停/剩余字节变化时立刻刷新显示（不采样，只重算纯函数部分）。</summary>
    private void RefreshDisplayLocked()
    {
        var last = _snapshot;
        var state = last.State;
        if (!_selection.IsUsable && state == NetworkLinkState.Connected) state = NetworkLinkState.Connecting;

        var display = NetworkDisplayMetrics.Compute(
            state,
            _paused,
            _estimator.HasReading,
            _estimator.EmaBytesPerSecond,
            _estimator.HasSufficientSamples,
            _estimator.StableBytesPerSecond,
            _remainingLogicalBytes,
            _terminal);

        _snapshot = last with
        {
            Paused = _paused,
            RemainingLogicalBytes = _remainingLogicalBytes,
            SpeedBytesPerSecond = display.SpeedBytesPerSecond,
            SpeedText = display.SpeedText,
            EtaSeconds = display.EtaSeconds,
            EtaText = display.EtaText,
            Reason = ComposeReason(display),
        };
    }

    /// <summary>
    /// 拼装快照理由：有速率时只报具体原因；没有速率（界面显示"—"）时把"界面为什么显示—"也带上。
    /// 用独立字段存具体原因而不是读取上一条快照的 Reason，避免刷新多次后理由无限拼接。
    /// </summary>
    private string ComposeReason(NetworkDisplayResult display) =>
        display.HasSpeed || _lastSpecificReason.Length == 0
            ? _lastSpecificReason
            : $"{_lastSpecificReason}；{display.Reason}";

    private NetworkThroughputSnapshot Store(NetworkThroughputSnapshot snapshot)
    {
        _snapshot = snapshot;
        return snapshot;
    }
}