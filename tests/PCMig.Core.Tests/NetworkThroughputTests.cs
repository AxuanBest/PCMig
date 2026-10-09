using System.Net;
using System.Runtime.InteropServices;
using PCMig.Core.Native;
using PCMig.Core.Network;
using PCMig.Core.Util;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// 真实网卡接收吞吐观测的回归测试。
///
/// <para>背景（必须记住的"为什么"）：界面此前用 <c>ProgressTruthSnapshot.SpeedBytesPerSecond</c>，
/// 它是"本轮新增**逻辑完成**字节 ÷ 本轮有效运行时间"。目标端已存在的文件被 Robocopy 快速 Skip 时，
/// 逻辑字节飞快进入完成口径（35 GB 已存在、只检查 0.2 秒 ⇒ 逻辑上"完成"35 GB），但这些字节根本没再经过网卡；
/// SMB/文件缓存又让 Preflight 测速虚高。所以真实吞吐必须来自网卡计数器，且**绝不能反过来污染**
/// Receipt / Progress Truth / CompletedBytes / Verifier。</para>
///
/// <para>本文件的用例全部不依赖真实网卡：估计器与显示口径是纯计算；采样器用假探针（<see cref="INetworkInterfaceProbe"/>）
/// 与假时钟（<see cref="TimeProvider"/>）驱动，因此"计数器回绕 / Δt=0 / 无基线 / 样本不足 / 暂停 / 断开"都可确定性复现。</para>
/// </summary>
public class NetworkThroughputEstimatorTests
{
    private const uint Nic = 7u;

    [Fact]
    public void FirstSample_OnlyEstablishesBaseline_NoRate()
    {
        var est = new NetworkThroughputEstimator();

        var r = est.Observe(Nic, 1_000UL, 0);

        Assert.Equal(NetworkSampleOutcome.NoBaseline, r.Outcome);
        Assert.False(r.HasReading);
        Assert.True(double.IsNaN(r.RawBytesPerSecond));
        Assert.True(est.HasBaseline);
        Assert.Equal(Nic, est.BaselineInterfaceIndex);
        Assert.Equal(1_000UL, est.LastOctets);
        Assert.Contains("基线", r.Reason);
    }

    [Fact]
    public void SecondSample_ProducesRawAndEmaAndStable()
    {
        var est = new NetworkThroughputEstimator();
        est.Observe(Nic, 1_000UL, 0);

        var r = est.Observe(Nic, 1_001_000UL, 1.0);

        Assert.Equal(NetworkSampleOutcome.Ok, r.Outcome);
        Assert.Equal(1_000_000UL, r.DeltaOctets);
        Assert.Equal(1_000_000.0, r.RawBytesPerSecond, 6);
        Assert.Equal(1_000_000.0, r.EmaBytesPerSecond, 6);
        Assert.Equal(1_000_000.0, r.StableBytesPerSecond, 6);
        Assert.True(est.HasReading);
    }

    [Fact]
    public void TooSmallDelta_KeepsBaselineAndLosesNoBytes()
    {
        var est = new NetworkThroughputEstimator();
        est.Observe(Nic, 1_000UL, 0);

        var tooSoon = est.Observe(Nic, 501_000UL, 0.1);

        Assert.Equal(NetworkSampleOutcome.TooSoon, tooSoon.Outcome);
        Assert.True(double.IsNaN(tooSoon.RawBytesPerSecond));
        Assert.False(est.HasReading);
        Assert.Equal(1_000UL, est.LastOctets); // 基线未推进：这些字节不会被丢掉

        // 下一次更长间隔把两段字节一起算：500_000 字节 ÷ 1.1 s（证明"字节不丢"）。
        var ok = est.Observe(Nic, 501_000UL, 1.1);

        Assert.Equal(NetworkSampleOutcome.Ok, ok.Outcome);
        Assert.Equal(500_000UL, ok.DeltaOctets);
        Assert.Equal(500_000.0 / 1.1, ok.RawBytesPerSecond, 6);
    }

    [Fact]
    public void ZeroDelta_ProducesNoRateAndNoSpike()
    {
        var est = new NetworkThroughputEstimator();
        est.Observe(Nic, 1_000UL, 0);
        est.Observe(Nic, 1_001_000UL, 1.0);
        var emaBefore = est.EmaBytesPerSecond;

        var r = est.Observe(Nic, 999_000_000UL, 0); // 同一时刻重复采样但计数器暴涨

        Assert.Equal(NetworkSampleOutcome.ClockWentBackwards, r.Outcome);
        Assert.True(double.IsNaN(r.RawBytesPerSecond));
        Assert.Equal(emaBefore, est.EmaBytesPerSecond, 6); // 平滑值不受污染
        Assert.Equal(1_001_000UL, est.LastOctets);          // 基线未推进
    }

    [Fact]
    public void NegativeDelta_IsTreatedAsClockBackwards()
    {
        var est = new NetworkThroughputEstimator();
        est.Observe(Nic, 1_000UL, 0);

        var r = est.Observe(Nic, 2_000_000UL, -3.0);

        Assert.Equal(NetworkSampleOutcome.ClockWentBackwards, r.Outcome);
        Assert.True(double.IsNaN(r.RawBytesPerSecond));
        Assert.True(est.HasBaseline); // 保留基线，等下一个有效间隔
    }

    [Fact]
    public void NaNDelta_IsTreatedAsClockBackwards()
    {
        var est = new NetworkThroughputEstimator();
        est.Observe(Nic, 1_000UL, 0);

        var r = est.Observe(Nic, 2_000_000UL, double.NaN);

        Assert.Equal(NetworkSampleOutcome.ClockWentBackwards, r.Outcome);
        Assert.True(double.IsNaN(r.RawBytesPerSecond));
    }

    [Fact]
    public void CounterWraparound_IsDiscardedInsteadOfHugeSpike()
    {
        var est = new NetworkThroughputEstimator();
        est.Observe(Nic, 5_000_000_000UL, 0);
        est.Observe(Nic, 5_001_000_000UL, 1.0);

        // 网卡被禁用/重插/驱动重启：计数器从很小的值重新开始。
        var reset = est.Observe(Nic, 1_024UL, 1.0);

        Assert.Equal(NetworkSampleOutcome.CounterReset, reset.Outcome);
        Assert.True(double.IsNaN(reset.RawBytesPerSecond));
        Assert.True(double.IsNaN(reset.StableBytesPerSecond));
        Assert.False(est.HasReading);
        Assert.Equal(1L, est.ResetsObserved);
        Assert.Equal(1_024UL, est.LastOctets); // 以回退后的值重建基线
        Assert.Contains("重置", reset.Reason);

        // 之后恢复正常：速率只由新基线之后的新增量决定，不掺旧计数器的天文数字。
        var ok = est.Observe(Nic, 1_024UL + 2_000_000UL, 1.0);

        Assert.Equal(NetworkSampleOutcome.Ok, ok.Outcome);
        Assert.Equal(2_000_000.0, ok.RawBytesPerSecond, 6);
        Assert.True(ok.RawBytesPerSecond < 1e9); // 远不是"回绕相减"会得到的垃圾尖峰
    }

    [Fact]
    public void ZeroInterfaceIndex_IsUnavailable()
    {
        var est = new NetworkThroughputEstimator();

        var r = est.Observe(0u, 1_000_000UL, 1.0);

        Assert.Equal(NetworkSampleOutcome.InterfaceUnavailable, r.Outcome);
        Assert.True(double.IsNaN(r.RawBytesPerSecond));
        Assert.False(est.HasBaseline);
    }

    [Fact]
    public void InterfaceChange_RebuildsBaselineInsteadOfSubtractingDifferentCounters()
    {
        var est = new NetworkThroughputEstimator();
        est.Observe(7u, 1_000UL, 0);
        est.Observe(7u, 1_001_000UL, 1.0);

        // 切到另一块网卡（例如用户换了路径）：两块网卡的计数器不同源，必须重建基线。
        var r = est.Observe(9u, 88_888_888UL, 1.0);

        Assert.Equal(NetworkSampleOutcome.NoBaseline, r.Outcome);
        Assert.Equal(9u, est.BaselineInterfaceIndex);
        Assert.True(double.IsNaN(est.EmaBytesPerSecond));
        Assert.Equal(0, est.StableSampleCount);
    }

    [Fact]
    public void StaleGap_DoesNotAverageAcrossTheHole()
    {
        var est = new NetworkThroughputEstimator();
        est.Observe(Nic, 1_000UL, 0);
        est.Observe(Nic, 1_001_000UL, 1.0);

        var r = est.Observe(Nic, 601_001_000UL, 30.0); // 休眠 30 s 后醒来

        Assert.Equal(NetworkSampleOutcome.StaleGap, r.Outcome);
        Assert.False(est.HasReading);
        Assert.Equal(0, est.StableSampleCount);
        Assert.True(double.IsNaN(r.StableBytesPerSecond));
    }

    [Fact]
    public void UnavailableObservation_KeepsBaselineAndLastEma()
    {
        var est = new NetworkThroughputEstimator();
        est.Observe(Nic, 1_000UL, 0);
        est.Observe(Nic, 1_001_000UL, 1.0);
        var ema = est.EmaBytesPerSecond;

        var r = est.ObserveUnavailable("网卡被拔出，读不到行信息", 1.0);

        Assert.Equal(NetworkSampleOutcome.InterfaceUnavailable, r.Outcome);
        Assert.True(double.IsNaN(r.RawBytesPerSecond));
        Assert.Equal(ema, est.EmaBytesPerSecond, 6);
        Assert.True(est.HasBaseline);
        Assert.Equal(1_001_000UL, est.LastOctets); // 不可用期间不推进基线：下一段增量不会丢

        // 恢复后，Δ 覆盖整段间隔，速率仍然正确（2 s 里收了 2 MB ⇒ 1 MB/s）。
        var ok = est.Observe(Nic, 3_001_000UL, 2.0);
        Assert.Equal(NetworkSampleOutcome.Ok, ok.Outcome);
        Assert.Equal(1_000_000.0, ok.RawBytesPerSecond, 6);
    }

    [Fact]
    public void Ema_SmoothsJitterAndStaysBetweenRawValues()
    {
        var est = new NetworkThroughputEstimator();
        est.Observe(Nic, 0UL, 0);
        var octets = 0UL;

        // 交替 0 / 2 MB/s（Δt=0.5 s ⇒ 每次 0 或 1_000_000 字节）：EMA 应被压在两者之间，而不是跟着跳。
        for (var i = 0; i < 20; i++)
        {
            octets += i % 2 == 0 ? 0UL : 1_000_000UL;
            est.Observe(Nic, octets, 0.5);
        }

        Assert.True(est.EmaBytesPerSecond > 800_000, $"EMA 过低：{est.EmaBytesPerSecond}");
        Assert.True(est.EmaBytesPerSecond < 1_200_000, $"EMA 过高：{est.EmaBytesPerSecond}");
    }

    [Fact]
    public void StableWindow_IsBounded_SoEtaFollowsRecentThroughput()
    {
        var est = new NetworkThroughputEstimator();
        var octets = 0UL;
        est.Observe(Nic, 0UL, 0);

        // 阶段 A：10 s 的 1 MB/s
        for (var i = 0; i < 20; i++)
        {
            octets += 500_000UL;
            est.Observe(Nic, octets, 0.5);
        }
        // 阶段 B：4 s 的 4 MB/s
        for (var i = 0; i < 8; i++)
        {
            octets += 2_000_000UL;
            est.Observe(Nic, octets, 0.5);
        }

        // 窗口上限 8 s：稳定值应偏向"最近的 4 MB/s"，不能被 14 s 的累计平均拖回去（那会得到 ~1.86 MB/s）。
        Assert.True(est.StableWindowSeconds <= 9.0, $"窗口未被裁剪：{est.StableWindowSeconds}");
        Assert.True(est.StableBytesPerSecond > 2_000_000, $"稳定吞吐过低：{est.StableBytesPerSecond}");
        Assert.True(est.StableBytesPerSecond < 3_000_000, $"稳定吞吐过高：{est.StableBytesPerSecond}");

        // EMA 是 1~2 秒口径，应比 8 秒窗口更快贴近 4 MB/s。
        Assert.True(est.EmaBytesPerSecond > 3_500_000, $"EMA 反应过慢：{est.EmaBytesPerSecond}");
        Assert.True(est.EmaBytesPerSecond > est.StableBytesPerSecond);
    }

    [Fact]
    public void SufficientSamples_RequiresBothCountAndTimeCoverage()
    {
        var est = new NetworkThroughputEstimator();
        var octets = 0UL;
        est.Observe(Nic, 0UL, 0);

        octets += 500_000UL;
        est.Observe(Nic, octets, 0.5);
        octets += 500_000UL;
        est.Observe(Nic, octets, 0.5);

        Assert.False(est.HasSufficientSamples); // 只有 2 个样本、1.0 s

        for (var i = 0; i < 4; i++)
        {
            octets += 500_000UL;
            est.Observe(Nic, octets, 0.5);
        }

        Assert.True(est.HasSufficientSamples); // 6 个样本、3.0 s
        Assert.Equal(6, est.StableSampleCount);
        Assert.Equal(3.0, est.StableWindowSeconds, 6);
    }

    [Fact]
    public void Reset_ClearsBaselineWindowAndEma()
    {
        var est = new NetworkThroughputEstimator();
        var octets = 0UL;
        est.Observe(Nic, 0UL, 0);
        for (var i = 0; i < 8; i++)
        {
            octets += 1_000_000UL;
            est.Observe(Nic, octets, 0.5);
        }
        Assert.True(est.HasReading);

        est.Reset();

        Assert.False(est.HasBaseline);
        Assert.True(double.IsNaN(est.EmaBytesPerSecond));
        Assert.True(double.IsNaN(est.StableBytesPerSecond));
        Assert.Equal(0, est.StableSampleCount);
        Assert.False(est.HasSufficientSamples);
    }
}

/// <summary>显示规则映射表（纯函数）与单位换算是本文件最该被锁死的部分：改错一个字，界面就会骗人。</summary>
public class NetworkDisplayMetricsTests
{
    private const double OneMegabytePerSecond = 1_048_576.0;

    [Theory]
    [InlineData(NetworkLinkState.Connecting)]
    [InlineData(NetworkLinkState.Disconnected)]
    [InlineData(NetworkLinkState.Unavailable)]
    public void NoLink_ShowsDashesForBoth(NetworkLinkState state)
    {
        var r = NetworkDisplayMetrics.Compute(
            state, paused: false, hasReading: true, emaBytesPerSecond: 5_000_000,
            hasSufficientSamples: true, stableBytesPerSecond: 5_000_000, remainingLogicalBytes: 1_000_000_000);

        Assert.False(r.HasSpeed);
        Assert.Equal("—", r.SpeedText);
        Assert.False(r.HasEta);
        Assert.Equal("—", r.EtaText);
        Assert.False(string.IsNullOrWhiteSpace(r.Reason));
    }

    [Fact]
    public void ConnectedWithoutAnySample_ShowsDashNotZero()
    {
        var r = NetworkDisplayMetrics.Compute(
            NetworkLinkState.Connected, paused: false, hasReading: false, emaBytesPerSecond: double.NaN,
            hasSufficientSamples: false, stableBytesPerSecond: double.NaN, remainingLogicalBytes: 10);

        Assert.False(r.HasSpeed);
        Assert.Equal("—", r.SpeedText); // 还没采到样本 ⇒ "—"，不是 0（0 会被读成"网络卡住了"）
        Assert.Equal("—", r.EtaText);
    }

    [Fact]
    public void ConnectedButIdle_ShowsZeroBytesPerSecondAndDashEta()
    {
        var r = NetworkDisplayMetrics.Compute(
            NetworkLinkState.Connected, paused: false, hasReading: true, emaBytesPerSecond: 0,
            hasSufficientSamples: false, stableBytesPerSecond: 0, remainingLogicalBytes: 1_000);

        Assert.True(r.HasSpeed);
        Assert.Equal(0.0, r.SpeedBytesPerSecond, 6);
        Assert.Equal("0 B/s", r.SpeedText);
        Assert.False(r.HasEta);
        Assert.Equal("—", r.EtaText);
    }

    [Fact]
    public void Paused_ShowsZeroSpeedAndDashEta()
    {
        var r = NetworkDisplayMetrics.Compute(
            NetworkLinkState.Connected, paused: true, hasReading: true, emaBytesPerSecond: 9_000_000,
            hasSufficientSamples: true, stableBytesPerSecond: 9_000_000, remainingLogicalBytes: 1_000_000);

        Assert.True(r.Paused);
        Assert.Equal(0.0, r.SpeedBytesPerSecond, 6);
        Assert.Equal("0 B/s", r.SpeedText);
        Assert.False(r.HasEta);
        Assert.Equal("—", r.EtaText);
        Assert.Contains("暂停", r.Reason);
    }

    [Fact]
    public void InsufficientSamples_ShowsSpeedButDashEta()
    {
        var r = NetworkDisplayMetrics.Compute(
            NetworkLinkState.Connected, paused: false, hasReading: true, emaBytesPerSecond: 3_000_000,
            hasSufficientSamples: false, stableBytesPerSecond: 3_000_000, remainingLogicalBytes: 900_000_000);

        Assert.True(r.HasSpeed);
        Assert.Equal(Format.Speed(3_000_000.0), r.SpeedText);
        Assert.False(r.HasEta);
        Assert.Equal("—", r.EtaText);
    }

    [Fact]
    public void SufficientSamples_UsesRemainingBytesDividedByStableThroughput()
    {
        const long remaining = 1_000_000_000L;      // 1_000_000_000 逻辑字节
        const double stable = 5_000_000.0;          // 稳定窗口 5 MB/s

        var r = NetworkDisplayMetrics.Compute(
            NetworkLinkState.Connected, paused: false, hasReading: true, emaBytesPerSecond: 6_000_000,
            hasSufficientSamples: true, stableBytesPerSecond: stable, remainingLogicalBytes: remaining);

        Assert.True(r.HasSpeed);
        Assert.Equal(Format.Speed(6_000_000.0), r.SpeedText);   // 速度用 EMA（1~2 秒口径）
        Assert.True(r.HasEta);
        Assert.Equal(200.0, r.EtaSeconds, 6);                   // 1e9 / 5e6 = 200 s（ETA 用稳定窗口，不用 EMA）
        Assert.Equal(Format.Eta(200.0), r.EtaText);
    }

    [Fact]
    public void ZeroStableThroughput_ShowsDashEta()
    {
        var r = NetworkDisplayMetrics.Compute(
            NetworkLinkState.Connected, paused: false, hasReading: true, emaBytesPerSecond: 0,
            hasSufficientSamples: true, stableBytesPerSecond: 0, remainingLogicalBytes: 1_000_000);

        Assert.Equal("0 B/s", r.SpeedText);
        Assert.False(r.HasEta);
        Assert.Equal("—", r.EtaText);
        Assert.Contains("没有接收流量", r.Reason);
    }

    [Fact]
    public void NoRemainingBytes_ShowsDashEta()
    {
        var r = NetworkDisplayMetrics.Compute(
            NetworkLinkState.Connected, paused: false, hasReading: true, emaBytesPerSecond: 1_000_000,
            hasSufficientSamples: true, stableBytesPerSecond: 1_000_000, remainingLogicalBytes: 0);

        Assert.False(r.HasEta);
        Assert.Equal("—", r.EtaText);
    }

    [Fact]
    public void SpeedText_CarriesTheSameUnitAsTheFrozenFormatter()
    {
        var r = NetworkDisplayMetrics.Compute(
            NetworkLinkState.Connected, paused: false, hasReading: true, emaBytesPerSecond: 112.5 * OneMegabytePerSecond,
            hasSufficientSamples: false, stableBytesPerSecond: double.NaN, remainingLogicalBytes: 1);

        // 界面 MB/s 就是仓库冻结的 Format.Speed 口径（1024 进制、标签写 KB/MB/GB），本类型不得另起一套。
        Assert.Equal(Format.Speed(112.5 * OneMegabytePerSecond), r.SpeedText);
        Assert.EndsWith("/s", r.SpeedText);
        Assert.StartsWith("112.5 MB/s", r.SpeedText);
    }

    [Fact]
    public void UnitConversion_MatchesTaskManagerRelationship()
    {
        // 任务管理器显示 Mbps（十进制），界面显示 MB/s（1024 进制）：900 Mbps ≈ 112.5 MB/s（十进制口径）。
        Assert.Equal(112_500_000.0, NetworkDisplayMetrics.MegabitsPerSecondToBytesPerSecond(900.0), 6);
        Assert.Equal(112.5, NetworkDisplayMetrics.MegabitsPerSecondToBytesPerSecond(900.0) / 1_000_000.0, 6);
        Assert.Equal(900.0, NetworkDisplayMetrics.BytesPerSecondToMegabitsPerSecond(112_500_000.0), 6);

        // 换成界面标签口径（1024 进制）：112.5 MB/s（十进制）≈ 107.29 MB/s（界面显示）。
        Assert.Equal(107.28836059570312, NetworkDisplayMetrics.BytesPerSecondToDisplayMegabytesPerSecond(112_500_000.0), 6);
    }

    [Theory]
    [InlineData(NetworkLinkState.Connected, "已连接")]
    [InlineData(NetworkLinkState.Connecting, "连接中")]
    [InlineData(NetworkLinkState.Disconnected, "已断开")]
    [InlineData(NetworkLinkState.Unavailable, "不可用")]
    public void Describe_GivesChineseLabel(NetworkLinkState state, string expected)
        => Assert.Equal(expected, NetworkDisplayMetrics.Describe(state));
}

/// <summary>采样器：用假探针 + 假时钟确定性驱动（不碰真实网卡、不起真实定时器）。</summary>
public class NetworkThroughputSamplerTests
{
    private const uint Nic = 7u;

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(double seconds) => _now = _now.AddSeconds(seconds);
    }

    private sealed class FakeProbe : INetworkInterfaceProbe
    {
        public InterfaceSelection Selection { get; set; } = new(
            InterfaceSelectionOutcome.Selected, Nic, "Intel(R) Ethernet I219-V", "以太网", 6u, 1_000_000_000UL,
            "测试桩：已按同子网前缀选定网卡", "AddressPrefix");

        public ulong InOctets { get; set; }
        public bool RowReadable { get; set; } = true;
        public string RowFailureReason { get; set; } = "测试桩：读不到行信息";
        public bool Operational { get; set; } = true;
        public int SelectCalls { get; private set; }

        public InterfaceSelection SelectInterface(IReadOnlyCollection<IPAddress>? destinations, Serilog.ILogger? log = null)
        {
            SelectCalls++;
            return Selection;
        }

        public bool TryReadRow(uint interfaceIndex, out InterfaceRowInfo row, out string reason)
        {
            row = new InterfaceRowInfo(
                Nic, "Intel(R) Ethernet I219-V", "以太网",
                Operational ? 1u : 2u, Operational ? 1u : 0u, 6u, 1_000_000_000UL, InOctets, 0UL);
            if (!RowReadable)
            {
                reason = RowFailureReason;
                return false;
            }
            reason = "";
            return true;
        }
    }

    private static IPAddress[] Target() => new[] { IPAddress.Parse("192.168.1.50") };

    [Fact]
    public void Tick_ProducesRealReceiveThroughputFromCounterDelta()
    {
        var time = new FakeTimeProvider();
        var probe = new FakeProbe { InOctets = 10_000UL };
        using var sampler = new NetworkThroughputSampler(Target(), probe, time, null, TimeSpan.FromMilliseconds(500));

        var first = sampler.Tick();

        Assert.Equal(NetworkSampleOutcome.NoBaseline, first.LastOutcome);
        Assert.Equal("—", first.SpeedText);
        Assert.Equal("—", first.EtaText);
        Assert.Equal(TimeSpan.FromMilliseconds(500), sampler.SampleInterval);

        time.Advance(1.0);
        probe.InOctets = 10_000UL + 2_000_000UL;
        var second = sampler.Tick();

        Assert.Equal(NetworkLinkState.Connected, second.State);
        Assert.Equal(Nic, second.InterfaceIndex);
        Assert.Equal("以太网", second.InterfaceAlias);
        Assert.Equal(2_000_000.0, second.SpeedBytesPerSecond, 6);
        Assert.Equal(Format.Speed(2_000_000.0), second.SpeedText);
        Assert.Equal(0L, second.ResetsObserved);
    }

    [Fact]
    public void Tick_AfterEnoughStableSeconds_FillsEtaFromRemainingLogicalBytes()
    {
        var time = new FakeTimeProvider();
        var probe = new FakeProbe { InOctets = 0UL };
        using var sampler = new NetworkThroughputSampler(Target(), probe, time, null, TimeSpan.FromMilliseconds(500));

        sampler.Tick(); // 基线
        for (var i = 0; i < 6; i++)
        {
            time.Advance(1.0);
            probe.InOctets += 5_000_000UL;
            sampler.Tick();
        }

        Assert.True(sampler.Snapshot.HasSufficientSamples);

        sampler.SetRemainingLogicalBytes(500_000_000L); // 剩余逻辑字节 = 计划 − 已完成，由上层从 Progress Truth 传入
        var snap = sampler.Snapshot;

        Assert.Equal(5_000_000.0, snap.StableBytesPerSecond, 3);
        Assert.True(snap.HasEta);
        Assert.Equal(100.0, snap.EtaSeconds, 3);
        Assert.Equal(Format.Eta(100.0), snap.EtaText);
    }

    [Fact]
    public void Pause_ImmediatelyShowsZeroSpeedAndDashEta()
    {
        var time = new FakeTimeProvider();
        var probe = new FakeProbe { InOctets = 0UL };
        using var sampler = new NetworkThroughputSampler(Target(), probe, time, null, TimeSpan.FromMilliseconds(500));

        sampler.Tick();
        time.Advance(1.0);
        probe.InOctets += 3_000_000UL;
        sampler.Tick();
        Assert.True(sampler.Snapshot.HasSpeed);

        sampler.MarkPaused(true); // 不等下一次采样，立刻生效
        var paused = sampler.Snapshot;

        Assert.True(paused.Paused);
        Assert.Equal("0 B/s", paused.SpeedText);
        Assert.Equal("—", paused.EtaText);

        sampler.MarkPaused(false);
        var resumed = sampler.Snapshot;

        Assert.False(resumed.Paused);
        Assert.True(resumed.HasSpeed);
        Assert.NotEqual("0 B/s", resumed.SpeedText);
    }

    [Fact]
    public void DisconnectedLink_ShowsDashes()
    {
        var time = new FakeTimeProvider();
        var probe = new FakeProbe { InOctets = 1_000UL, Operational = false };
        using var sampler = new NetworkThroughputSampler(Target(), probe, time, null, TimeSpan.FromMilliseconds(500));

        var snap = sampler.Tick();

        Assert.Equal(NetworkLinkState.Disconnected, snap.State);
        Assert.Equal("—", snap.SpeedText);
        Assert.Equal("—", snap.EtaText);
    }

    [Fact]
    public void UnusableSelection_ReportsConnectingWithoutFabricatingNumbers()
    {
        var time = new FakeTimeProvider();
        var probe = new FakeProbe
        {
            Selection = new InterfaceSelection(
                InterfaceSelectionOutcome.NoMatchingInterface, 0u, "", "", 0u, 0UL,
                "本机没有接口地址与目标同子网，系统选路也失败", ""),
        };
        using var sampler = new NetworkThroughputSampler(Target(), probe, time, null, TimeSpan.FromMilliseconds(500));

        var snap = sampler.Tick();

        Assert.Equal(NetworkLinkState.Connecting, snap.State);
        Assert.Equal(0u, snap.InterfaceIndex);
        Assert.Equal("—", snap.SpeedText);
        Assert.Equal("—", snap.EtaText);
        Assert.Contains("同子网", snap.Reason);            // 具体原因：选不出真实网卡
        Assert.Contains("正在建立网卡观测", snap.Reason);    // 显示口径：为什么是"—"
        Assert.Equal("本机没有接口地址与目标同子网，系统选路也失败", snap.SelectionReason);
    }

    [Fact]
    public void PlatformUnavailableSelection_ReportsUnavailable()
    {
        var time = new FakeTimeProvider();
        var probe = new FakeProbe
        {
            Selection = new InterfaceSelection(
                InterfaceSelectionOutcome.PlatformUnavailable, 0u, "", "", 0u, 0UL,
                "本机没有 iphlpapi.dll（非 Windows 平台）：网卡吞吐观测不可用", ""),
        };
        using var sampler = new NetworkThroughputSampler(Target(), probe, time, null, TimeSpan.FromMilliseconds(500));

        var snap = sampler.Tick();

        Assert.Equal(NetworkLinkState.Unavailable, snap.State);
        Assert.Equal("—", snap.SpeedText);
        Assert.Contains("iphlpapi", snap.Reason);           // 具体原因：平台不支持
        Assert.Contains("网卡吞吐观测不可用", snap.Reason);   // 显示口径：为什么是"—"
        Assert.Equal(InterfaceSelectionOutcome.PlatformUnavailable, sampler.Selection.Outcome);
    }

    [Fact]
    public void RowReadFailure_IsReportedAndNeverThrows()
    {
        var time = new FakeTimeProvider();
        var probe = new FakeProbe { RowReadable = false };
        using var sampler = new NetworkThroughputSampler(Target(), probe, time, null, TimeSpan.FromMilliseconds(500));

        var snap = sampler.Tick();

        Assert.Equal(NetworkLinkState.Unavailable, snap.State);
        Assert.Equal("—", snap.SpeedText);
        Assert.Equal(NetworkSampleOutcome.InterfaceUnavailable, snap.LastOutcome);
        Assert.Contains("读不到行信息", snap.Reason);
    }

    [Fact]
    public void UnusableSelection_IsRetriedOnNextTick()
    {
        var time = new FakeTimeProvider();
        var probe = new FakeProbe
        {
            Selection = new InterfaceSelection(
                InterfaceSelectionOutcome.RoutingFailed, 0u, "", "", 0u, 0UL, "网络不可达：可能还没连上旧电脑", ""),
            InOctets = 1_000UL,
        };
        using var sampler = new NetworkThroughputSampler(Target(), probe, time, null, TimeSpan.FromMilliseconds(500));

        Assert.Equal(NetworkLinkState.Connecting, sampler.Tick().State);

        // 用户把网络连上了：下一次采样会重新选接口（不能一次选不出就永久放弃）。
        probe.Selection = new InterfaceSelection(
            InterfaceSelectionOutcome.Selected, Nic, "Intel(R) Ethernet I219-V", "以太网", 6u, 1_000_000_000UL,
            "测试桩：重试后选定网卡", "BestInterface");
        time.Advance(1.0);
        var snap = sampler.Tick();

        Assert.True(probe.SelectCalls >= 2);
        Assert.Equal(NetworkLinkState.Connected, snap.State);
    }

    [Fact]
    public void CounterResetDuringSampling_DropsOneSampleThenRecovers()
    {
        var time = new FakeTimeProvider();
        var probe = new FakeProbe { InOctets = 9_000_000_000UL };
        using var sampler = new NetworkThroughputSampler(Target(), probe, time, null, TimeSpan.FromMilliseconds(500));

        sampler.Tick();
        time.Advance(1.0);
        probe.InOctets += 4_000_000UL;
        Assert.Equal(NetworkSampleOutcome.Ok, sampler.Tick().LastOutcome);

        time.Advance(1.0);
        probe.InOctets = 512UL; // 计数器回退
        var reset = sampler.Tick();

        Assert.Equal(NetworkSampleOutcome.CounterReset, reset.LastOutcome);
        Assert.Equal("—", reset.SpeedText);
        Assert.Equal(1L, reset.ResetsObserved);

        time.Advance(1.0);
        probe.InOctets += 1_500_000UL;
        var recovered = sampler.Tick();

        Assert.Equal(NetworkSampleOutcome.Ok, recovered.LastOutcome);
        Assert.Equal(1_500_000.0, recovered.SpeedBytesPerSecond, 6);
    }

    [Fact]
    public void StaleGapDuringSampling_ResetsWindowInsteadOfAveragingAcrossSleep()
    {
        var time = new FakeTimeProvider();
        var probe = new FakeProbe { InOctets = 0UL };
        using var sampler = new NetworkThroughputSampler(Target(), probe, time, null, TimeSpan.FromMilliseconds(500));

        sampler.Tick();
        for (var i = 0; i < 6; i++)
        {
            time.Advance(1.0);
            probe.InOctets += 5_000_000UL;
            sampler.Tick();
        }
        Assert.True(sampler.Snapshot.HasSufficientSamples);

        time.Advance(600.0); // 休眠 10 分钟
        probe.InOctets += 1_000_000UL;
        var snap = sampler.Tick();

        Assert.Equal(NetworkSampleOutcome.StaleGap, snap.LastOutcome);
        Assert.False(snap.HasSufficientSamples);
        Assert.Equal("—", snap.EtaText);
    }

    [Fact]
    public void UpdateDestinations_ReselectsInterfaceAndDropsStaleNumbers()
    {
        var time = new FakeTimeProvider();
        var probe = new FakeProbe { InOctets = 0UL };
        using var sampler = new NetworkThroughputSampler(Target(), probe, time, null, TimeSpan.FromMilliseconds(500));

        sampler.Tick();
        time.Advance(1.0);
        probe.InOctets += 1_000_000UL;
        sampler.Tick();
        Assert.True(sampler.Snapshot.HasSpeed);

        probe.Selection = new InterfaceSelection(
            InterfaceSelectionOutcome.Selected, 9u, "Intel(R) Wi-Fi 6 AX201", "WLAN", 71u, 866_000_000UL,
            "测试桩：换成无线网卡", "BestInterface");
        sampler.UpdateDestinations(new[] { IPAddress.Parse("192.168.9.9") });

        var snap = sampler.Snapshot;

        Assert.Equal(9u, sampler.Selection.InterfaceIndex);
        Assert.False(snap.HasSpeed); // 换了网卡，旧读数不再代表真实吞吐
        Assert.Equal("—", snap.SpeedText);
    }

    [Fact]
    public void StartStopDispose_AreIdempotentAndDoNotThrow()
    {
        var time = new FakeTimeProvider();
        var probe = new FakeProbe();
        var sampler = new NetworkThroughputSampler(Target(), probe, time, null, TimeSpan.FromMilliseconds(50));

        Assert.False(sampler.IsRunning);

        sampler.Start();
        Assert.True(sampler.IsRunning);
        sampler.Start(); // 幂等
        Assert.True(sampler.IsRunning);

        sampler.Stop();
        Assert.False(sampler.IsRunning);
        sampler.Stop(); // 幂等

        sampler.Dispose();
        sampler.Dispose(); // 幂等
        Assert.False(sampler.IsRunning);

        // 释放后再采样也只能拿到快照，不得抛异常（后台线程抛异常会杀进程）。
        Assert.NotNull(sampler.Tick());
    }

    [Fact]
    public void InitialSnapshot_ShowsDashes()
    {
        var time = new FakeTimeProvider();
        var probe = new FakeProbe();
        using var sampler = new NetworkThroughputSampler(Target(), probe, time, null, TimeSpan.FromMilliseconds(500));

        var snap = sampler.Snapshot;

        Assert.Equal(NetworkLinkState.Connecting, snap.State);
        Assert.Equal("—", snap.SpeedText);
        Assert.Equal("—", snap.EtaText);
        Assert.False(snap.HasSpeed);
        Assert.False(snap.HasEta);
    }
}

/// <summary>P/Invoke 层的可测部分：MIB_IF_ROW2 布局自检 + 接口选择器的纯函数部分（不依赖真实网卡）。</summary>
public class NetworkInterfaceSelectorTests
{
    [Fact]
    public void MibIfRow2Layout_MatchesDocumentedOffsets()
    {
        // 字段错位会读到垃圾值（本任务最大的技术风险）：这里把偏移量钉死成 ifmib.h 的文档值。
        Assert.Equal(IpHelperIfEntry.DocumentedRowSizeBytes, Marshal.SizeOf<IpHelperIfEntry.MIB_IF_ROW2>());
        Assert.Equal(
            IpHelperIfEntry.OffsetOfInterfaceIndex,
            Marshal.OffsetOf<IpHelperIfEntry.MIB_IF_ROW2>(nameof(IpHelperIfEntry.MIB_IF_ROW2.InterfaceIndex)).ToInt64());
        Assert.Equal(
            IpHelperIfEntry.OffsetOfPhysicalAddressLength,
            Marshal.OffsetOf<IpHelperIfEntry.MIB_IF_ROW2>(nameof(IpHelperIfEntry.MIB_IF_ROW2.PhysicalAddressLength)).ToInt64());
        Assert.Equal(
            IpHelperIfEntry.OffsetOfInOctets,
            Marshal.OffsetOf<IpHelperIfEntry.MIB_IF_ROW2>(nameof(IpHelperIfEntry.MIB_IF_ROW2.InOctets)).ToInt64());

        // 生产代码自己的自检也必须通过，否则所有读数都会按"不可用"处理。
        Assert.True(IpHelperIfEntry.LayoutSelfCheckPassed, IpHelperIfEntry.LayoutSelfCheckDetail);
    }

    [Fact]
    public void TryReadRow_RejectsZeroIndexWithReasonInsteadOfThrowing()
    {
        var ok = IpHelperIfEntry.TryReadRow(0u, out var row, out var reason);

        Assert.False(ok);
        Assert.Equal(default, row);
        Assert.Contains("索引为 0", reason);
    }

    [Fact]
    public void TryReadInOctets_UnknownIndexFailsGracefully()
    {
        // 999999 几乎不可能存在；不管选得到选不到都不该抛异常，失败必须给出中文理由。
        var ok = IpHelperIfEntry.TryReadInOctets(999_999u, out _, out var reason);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void Select_NullDestination_IsUnavailable()
    {
        // 显式转型：Select 有「单个地址」与「多个候选地址」两个重载，裸 null 会被判为二义调用。
        var r = NetworkInterfaceSelector.Select((IPAddress?)null);

        Assert.False(r.IsUsable);
        Assert.Equal(InterfaceSelectionOutcome.NoEndpoint, r.Outcome);
        Assert.Equal(0u, r.InterfaceIndex);
        Assert.False(string.IsNullOrWhiteSpace(r.Reason));
    }

    [Fact]
    public void Select_LoopbackTarget_IsUnavailable()
    {
        var r = NetworkInterfaceSelector.Select(IPAddress.Loopback);

        Assert.False(r.IsUsable);
        Assert.Equal(InterfaceSelectionOutcome.Loopback, r.Outcome);
    }

    [Fact]
    public void Select_IPv6LinkLocalWithoutScope_IsUnavailable()
    {
        // fe80:: 不带 %接口：多条 fe80::/64 路由时系统任挑一条 ⇒ 必须如实报不可用，不许冒充。
        var r = NetworkInterfaceSelector.Select(IPAddress.Parse("fe80::1"));

        Assert.False(r.IsUsable);
        Assert.Equal(InterfaceSelectionOutcome.LinkLocalWithoutScope, r.Outcome);
        Assert.Contains("scope", r.Reason);
    }

    [Fact]
    public void Select_MultipleCandidates_AllFailuresAreReported()
    {
        var r = NetworkInterfaceSelector.Select(new[] { IPAddress.Loopback, IPAddress.IPv6Loopback });

        Assert.False(r.IsUsable);
        Assert.Contains("已尝试", r.Reason);
    }

    [Fact]
    public void Select_EmptyCandidates_IsUnavailable()
    {
        var r = NetworkInterfaceSelector.Select(Array.Empty<IPAddress>());

        Assert.False(r.IsUsable);
        Assert.Equal(InterfaceSelectionOutcome.NoEndpoint, r.Outcome);
    }

    [Fact]
    public void Select_RealTarget_NeverThrowsAndNeverFabricatesIndex()
    {
        // 192.0.2.0/24 是 TEST-NET-1：本机不会有它的同子网地址。选不出来必须报不可用，
        // 选得出来（系统默认路由）则索引必须非 0。两种结局都不允许抛异常。
        var r = NetworkInterfaceSelector.Select(IPAddress.Parse("192.0.2.5"));

        Assert.False(string.IsNullOrWhiteSpace(r.Reason));
        if (r.IsUsable) Assert.NotEqual(0u, r.InterfaceIndex);
        else Assert.Equal(0u, r.InterfaceIndex);
    }

    [Theory]
    [InlineData("192.168.1.10", "192.168.1.20", 24, true)]
    [InlineData("192.168.1.10", "192.168.2.20", 24, false)]
    [InlineData("10.1.2.3", "10.1.9.9", 16, true)]
    [InlineData("10.1.2.3", "10.2.9.9", 16, false)]
    [InlineData("192.168.1.10", "8.8.8.8", 0, true)]
    [InlineData("fe80::1", "fe80::abcd", 64, true)]
    [InlineData("2001:db8:1::1", "2001:db8:2::1", 48, false)]
    [InlineData("2001:db8:1::1", "2001:db8:1:9::1", 48, true)]
    public void SameSubnet_MatchesByPrefixBits(string a, string b, int prefixLength, bool expected)
        => Assert.Equal(
            expected,
            NetworkInterfaceSelector.SameSubnet(IPAddress.Parse(a).GetAddressBytes(), IPAddress.Parse(b).GetAddressBytes(), prefixLength));

    [Fact]
    public void SameSubnet_RejectsMismatchedFamilies()
        => Assert.False(NetworkInterfaceSelector.SameSubnet(
            IPAddress.Parse("192.168.1.1").GetAddressBytes(),
            IPAddress.Parse("fe80::1").GetAddressBytes(),
            64));
}

/// <summary>
/// ★ 2026-10-08 Preview.2（真机问题 6）★ 终态口径回归测试。
///
/// <para>真机事实（JOB-20261008-140340-6310）：任务已经 <c>completedWithErrors</c>（3 个对象 2 完成 1 失败），
/// 界面却一直挂着"预计剩余 8 小时"级别的 ETA。原因是任务结束时剩余逻辑字节会**冻结在 &gt;0**
/// （计划字节 &gt; 已落盘字节），而采样器只看得到网卡计数器 —— 只要网卡还有任意背景接收流量，
/// 旧实现就永远满足 ETA 的全部前置条件，于是持续算出假数字。</para>
///
/// <para>本类锁死三件事：① 终态 ⇒ 速度 "0 B/s"、ETA "—"，理由里说清为什么；
/// ② 终态闸门是**纯显示**，绝不回写任何进度真值（剩余逻辑字节原样保留、暂停标记不受影响）；
/// ③ 只有"终态 → 运行"的恢复沿（<see cref="NetworkThroughputSampler.ResumeObservation"/>）才丢窗口与基线 ——
/// 因为停摆期间攒下的样本会把背景流量的均值当成"传输速度"，而短暂停不丢（那是刻意的对比）。</para>
/// </summary>
public class NetworkTerminalPhaseTests
{
    private const uint Nic = 7u;

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(double seconds) => _now = _now.AddSeconds(seconds);
    }

    private sealed class FakeProbe : INetworkInterfaceProbe
    {
        public InterfaceSelection Selection { get; set; } = new(
            InterfaceSelectionOutcome.Selected, Nic, "Intel(R) Ethernet I219-V", "以太网", 6u, 1_000_000_000UL,
            "测试桩：已按同子网前缀选定网卡", "AddressPrefix");

        public ulong InOctets { get; set; }
        public bool RowReadable { get; set; } = true;
        public string RowFailureReason { get; set; } = "测试桩：读不到行信息";
        public bool Operational { get; set; } = true;
        public int SelectCalls { get; private set; }

        public InterfaceSelection SelectInterface(IReadOnlyCollection<IPAddress>? destinations, Serilog.ILogger? log = null)
        {
            SelectCalls++;
            return Selection;
        }

        public bool TryReadRow(uint interfaceIndex, out InterfaceRowInfo row, out string reason)
        {
            row = new InterfaceRowInfo(
                Nic, "Intel(R) Ethernet I219-V", "以太网",
                Operational ? 1u : 2u, Operational ? 1u : 0u, 6u, 1_000_000_000UL, InOctets, 0UL);
            if (!RowReadable)
            {
                reason = RowFailureReason;
                return false;
            }

            reason = "";
            return true;
        }
    }

    private static IPAddress[] Target() => new[] { IPAddress.Parse("192.168.1.50") };

    /// <summary>把采样器喂到"稳定吞吐 5 MB/s ＋ 剩余 500 MB ⇒ ETA 100 秒"的可用状态（调用方负责 Dispose）。</summary>
    private static NetworkThroughputSampler FeedStableObservation(FakeTimeProvider time, FakeProbe probe)
    {
        var sampler = new NetworkThroughputSampler(Target(), probe, time, null, TimeSpan.FromMilliseconds(500));
        sampler.Tick();
        for (var i = 0; i < 6; i++)
        {
            time.Advance(1.0);
            probe.InOctets += 5_000_000UL;
            sampler.Tick();
        }

        sampler.SetRemainingLogicalBytes(500_000_000L);
        return sampler;
    }

    [Fact]
    public void Terminal_CompletedWithErrors_ShowsZeroSpeedAndDashEta()
    {
        // 真机量级：object-000001 计划 95.9 GB、已落盘 4.24 GB ⇒ 终态下剩余逻辑字节仍是一百多 GB。
        var r = NetworkDisplayMetrics.Compute(
            NetworkLinkState.Connected, paused: false, hasReading: true, emaBytesPerSecond: 9_000_000,
            hasSufficientSamples: true, stableBytesPerSecond: 9_000_000,
            remainingLogicalBytes: 150_700_000_000L, terminal: true);

        Assert.True(r.HasSpeed);
        Assert.Equal(0.0, r.SpeedBytesPerSecond, 6);   // 任务已结束 ⇒ "还在跑 9 MB/s" 是假的
        Assert.Equal("0 B/s", r.SpeedText);
        Assert.False(r.Paused);                        // 终态不是暂停（暂停语义另有分支在管）
        Assert.False(r.HasEta);
        Assert.Equal("—", r.EtaText);
        Assert.Contains("任务已结束", r.Reason);
    }

    [Fact]
    public void LowBackgroundTraffic_WhileRunning_StillProducesHugeEta_OnPurpose()
    {
        // 任务**还在跑**时，剩余 150.7 GB ÷ 背景流量 100 KB/s = 17 天。难看但没错：
        // 它如实反映"按现在的网卡接收速率还要这么久"。所以闸门必须来自任务阶段，
        // 不能靠"ETA 太大就藏起来"这种阈值猜测 —— 那会把真正的慢链路也一起藏掉。
        var running = NetworkDisplayMetrics.Compute(
            NetworkLinkState.Connected, paused: false, hasReading: true, emaBytesPerSecond: 100_000,
            hasSufficientSamples: true, stableBytesPerSecond: 100_000, remainingLogicalBytes: 150_700_000_000L);

        Assert.True(running.HasEta);
        Assert.Equal(1_507_000.0, running.EtaSeconds, 3);

        var terminal = NetworkDisplayMetrics.Compute(
            NetworkLinkState.Connected, paused: false, hasReading: true, emaBytesPerSecond: 100_000,
            hasSufficientSamples: true, stableBytesPerSecond: 100_000,
            remainingLogicalBytes: 150_700_000_000L, terminal: true);

        Assert.False(terminal.HasEta);
        Assert.Equal("—", terminal.EtaText);
    }

    [Fact]
    public void Terminal_Interrupted_DropsRunningEtaAndKeepsItDropped()
    {
        var time = new FakeTimeProvider();
        var probe = new FakeProbe { InOctets = 0UL };
        using var sampler = FeedStableObservation(time, probe);

        Assert.True(sampler.Snapshot.HasEta);   // 前提：终态之前确实有"预计剩余 100 秒"
        Assert.Equal(100.0, sampler.Snapshot.EtaSeconds, 3);

        sampler.MarkTerminal(true);             // = 已应用到界面的 Phase 是 Interrupted / CompletedWithErrors …
        Assert.Equal("0 B/s", sampler.Snapshot.SpeedText);
        Assert.Equal("—", sampler.Snapshot.EtaText);

        // 关键：终态之后采样器**仍在跑**（500 ms 计时器不该停 —— 网卡观测是独立的物理观测），
        // 但背景流量继续进账也绝不能让它再冒出 ETA。
        for (var i = 0; i < 6; i++)
        {
            time.Advance(1.0);
            probe.InOctets += 900_000UL;
            sampler.Tick();
        }

        Assert.Equal("—", sampler.Snapshot.EtaText);
        Assert.Equal("0 B/s", sampler.Snapshot.SpeedText);
    }

    [Fact]
    public void TerminalGate_IsDisplayOnly_AndReversibleWithoutDroppingTheWindow()
    {
        var time = new FakeTimeProvider();
        var probe = new FakeProbe { InOctets = 0UL };
        using var sampler = FeedStableObservation(time, probe);

        sampler.MarkTerminal(true);

        // 纯显示闸门：不动剩余逻辑字节、不动暂停标记、也不清采样窗口（终态不是"重置观测"）。
        Assert.Equal(500_000_000L, sampler.Snapshot.RemainingLogicalBytes);
        Assert.False(sampler.Snapshot.Paused);
        Assert.Equal("—", sampler.Snapshot.EtaText);

        // 反向：只撤掉终端标记（窗口还在）⇒ 立刻回到真实口径，不必等新样本。
        // 这一条同时证明"窗口被保留"：100 秒这个数只能来自终态之前那个 5 MB/s 的稳定窗口。
        sampler.MarkTerminal(false);
        Assert.True(sampler.Snapshot.HasEta);
        Assert.Equal(100.0, sampler.Snapshot.EtaSeconds, 3);
        Assert.Equal(Format.Speed(5_000_000.0), sampler.Snapshot.SpeedText);
    }

    [Fact]
    public void Terminal_ThenResumeObservation_RebuildsWindowInsteadOfReusingIt()
    {
        var time = new FakeTimeProvider();
        var probe = new FakeProbe { InOctets = 0UL };
        using var sampler = FeedStableObservation(time, probe);
        sampler.MarkTerminal(true);

        // 停摆期间网卡还在收背景流量（真机上完全可能：SMB 重连、别的程序在下载）。
        for (var i = 0; i < 6; i++)
        {
            time.Advance(1.0);
            probe.InOctets += 20_000UL;   // 20 KB/s，与迁移无关
            sampler.Tick();
        }

        sampler.ResumeObservation();       // = 用户点「恢复任务」，任务重新回到 Running

        Assert.Equal("—", sampler.Snapshot.SpeedText);         // 窗口已丢 ⇒ 没有读数，不是 0
        Assert.Equal("—", sampler.Snapshot.EtaText);

        // 恢复后第一个样本只建立基线 ⇒ 仍旧"—"，并且 HasSufficientSamples 明确回到 false
        // （这个字段由 Tick 落位，所以必须在采样后断言 —— 它证明的是"窗口真的被丢了"，不只是"显示成—"）。
        time.Advance(1.0);
        probe.InOctets += 5_000_000UL;
        sampler.Tick();
        Assert.Equal("—", sampler.Snapshot.SpeedText);
        Assert.False(sampler.Snapshot.HasSufficientSamples);

        for (var i = 0; i < 5; i++)
        {
            time.Advance(1.0);
            probe.InOctets += 5_000_000UL;
            sampler.Tick();
        }

        Assert.Equal(Format.Speed(5_000_000.0), sampler.Snapshot.SpeedText);
        Assert.True(sampler.Snapshot.HasSufficientSamples);
    }

    [Fact]
    public void Pause_DoesNotDropTheWindow_ResumeObservationDoes()
    {
        var time = new FakeTimeProvider();
        var probe = new FakeProbe { InOctets = 0UL };
        using var sampler = FeedStableObservation(time, probe);

        sampler.MarkPaused(true);
        Assert.Equal("0 B/s", sampler.Snapshot.SpeedText);
        Assert.Equal("—", sampler.Snapshot.EtaText);
        Assert.True(sampler.Snapshot.Paused);

        // 暂停**不**丢窗口（与 ResumeObservation 刻意不同）：撤掉暂停后同一个窗口立刻给出 100 秒，
        // 不需要重新攒 3 秒样本 ⇒ 短暂停（用户点一下再点回来）不会把 ETA 变成"—"。
        sampler.MarkPaused(false);
        Assert.True(sampler.Snapshot.HasEta);
        Assert.Equal(100.0, sampler.Snapshot.EtaSeconds, 3);
    }
}