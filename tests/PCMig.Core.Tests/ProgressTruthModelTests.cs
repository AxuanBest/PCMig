using System.Diagnostics;
using PCMig.Core.Diagnostics;
using PCMig.Core.Jobs;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using PCMig.Core.Util;
using PCMig.Diagnostics.Abstractions;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// FIX BATCH 4 — Progress Truth Model（P1-1）：
/// 真机上"网络 35+ MB/s 而界面长时间 0 B / 对象边界突然大跳 / 重试静默归零"是同一件事的三张脸：
/// 分子只有"回执 + 已确认的飞行中字节"，而 /Z 串行大文件通道**没有任何可信的连续飞行中来源**
/// （<see cref="RobocopyRunner.TrustsTargetStatForProgress"/> 恒为 false，回退枚举又被禁），
/// 于是单个 28.5 GB 对象在整个复制期间分子纹丝不动。
///
/// 本组测试锁死四件事：
///   1) 唯一天真值类型 <see cref="ProgressTruthSnapshot"/> 的取值不变式（分子≤分母、百分比 0–100、
///      运行中永不 100%、Paused 时速率=0 且不显示旧 ETA、重试不得静默归零）；
///   2) 不可信目标 stat 的通道必须换用**可信连续来源**（<see cref="ProgressTruthSource"/> 判据）；
///   3) 进程 I/O 遥测适配器 <see cref="WorkerIoProgressTracker"/> 的单调/重置/封顶语义；
///   4) 单位口径 IEC（KiB/MiB/GiB）与短窗口 ETA 的 "—"。
/// </summary>
public class ProgressTruthModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-pg-" + Guid.NewGuid().ToString("N")[..8]);

    public ProgressTruthModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* 文件句柄延迟释放 */ }
    }

    private static ProgressTruthSnapshot Build(
        long planned = 1_000_000,
        long committed = 0,
        long inFlight = 0,
        bool settled = false,
        bool paused = false,
        long attemptEpoch = 0,
        RetryState retry = RetryState.None,
        bool targetStatTrusted = false,
        ProgressTruthSource sourceOverride = ProgressTruthSource.None,
        string? objectId = "obj-1",
        long objectPlanned = 1_000_000,
        long objectConfirmed = 0,
        double speed = 10_000_000,
        DateTime? at = null)
        => ProgressTruthSnapshot.Create(
            plannedBytes: planned,
            committedBytes: committed,
            inFlightConfirmedBytes: inFlight,
            currentObjectId: objectId,
            currentObjectPlannedBytes: objectPlanned,
            currentObjectConfirmedBytes: objectConfirmed,
            attemptEpoch: attemptEpoch,
            retryState: retry,
            speedBytesPerSecond: speed,
            settled: settled,
            paused: paused,
            targetStatTrusted: targetStatTrusted,
            inFlightSource: sourceOverride,
            timestampUtc: at ?? new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc));

    // ── PG-01：真值字段与"显示字节"的定义 ────────────────────────────────────────────
    [Fact]
    public void PG01_DisplayedTransferredBytes_IsCommittedPlusConfirmedInFlight()
    {
        var t = Build(planned: 10_000, committed: 4_000, inFlight: 1_500);
        Assert.Equal(4_000, t.CommittedBytes);
        Assert.Equal(1_500, t.InFlightConfirmedBytes);
        Assert.Equal(5_500, t.DisplayedTransferredBytes);
        Assert.Equal(10_000, t.PlannedBytes);
        Assert.Equal(55.0, t.Percent, 3);
        Assert.Equal("obj-1", t.CurrentObjectId);
        Assert.Equal(new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc), t.TimestampUtc);
    }

    // ── PG-02：分子绝不越界（≤ 分母、≥ 已入库） ─────────────────────────────────────
    [Fact]
    public void PG02_Numerator_IsClampedToPlanned_AndNeverBelowCommitted()
    {
        // 遥测/解析高估：封顶到计划字节（运行中仍不得显示 100%）
        var over = Build(planned: 10_000, committed: 4_000, inFlight: 900_000);
        Assert.Equal(10_000, over.DisplayedTransferredBytes);
        Assert.Equal(99.9, over.Percent, 3);
        Assert.Equal(100.0, Build(planned: 10_000, committed: 4_000, inFlight: 900_000, settled: true).Percent, 3);

        // 回冲（F12）把飞行中打到负数：不得低于已入库字节
        var under = Build(planned: 10_000, committed: 4_000, inFlight: -9_999);
        Assert.Equal(4_000, under.DisplayedTransferredBytes);
        Assert.True(under.Percent >= 0);

        // 分母未知（0）：不得产生 NaN/负数百分比
        var unknown = Build(planned: 0, committed: 0, inFlight: 123);
        Assert.Equal(0, unknown.PlannedBytes);
        Assert.True(unknown.Percent is >= 0 and <= 100);
    }

    // ── PG-03：运行中永不 100%，收尾才精确 100% ─────────────────────────────────────
    [Fact]
    public void PG03_RunningNeverReachesHundred_SettledConvergesExactly()
    {
        var running = Build(planned: 1_000, committed: 0, inFlight: 1_000, settled: false);
        Assert.Equal(99.9, running.Percent, 3);
        Assert.True(running.Percent < 100.0);

        var settled = Build(planned: 1_000, committed: 1_000, inFlight: 0, settled: true);
        Assert.Equal(100.0, settled.Percent, 6);
    }

    // ── PG-04：Paused ⇒ 速率 0 且不显示旧 ETA（§7.4） ───────────────────────────────
    [Fact]
    public void PG04_Paused_ReportsZeroSpeedAndNoEta()
    {
        var paused = Build(planned: 1_000_000, committed: 100_000, inFlight: 5_000, paused: true, speed: 12_345_678);
        Assert.Equal(0.0, paused.SpeedBytesPerSecond);
        Assert.True(double.IsNaN(paused.EtaSeconds));
        Assert.Equal("—", Format.Eta(paused.EtaSeconds));

        var running = Build(planned: 1_000_000, committed: 100_000, inFlight: 5_000, speed: 1_000_000);
        Assert.Equal(1_000_000, running.SpeedBytesPerSecond);
        Assert.True(running.EtaSeconds > 0);
    }

    // ── PG-05：重试/回冲真值 —— 允许下降但必须解释清楚，禁止静默归零 ──────────────────
    [Fact]
    public void PG05_RetryTruth_IsExplicit_AndNeverSilentlyZeroes()
    {
        // 第 1 趟：已确认 7,000；第 2 趟开始（F4 单调基线重开）⇒ 飞行中清零，但 RetryState 必须说明
        var retry = Build(planned: 100_000, committed: 20_000, inFlight: 0,
                          attemptEpoch: 2, retry: RetryState.Retrying);
        Assert.Equal(2, retry.AttemptEpoch);
        Assert.Equal(RetryState.Retrying, retry.RetryState);
        Assert.True(retry.IsRetryExplained);
        Assert.Equal(20_000, retry.DisplayedTransferredBytes); // 已入库的字节绝不被重试抹掉

        // 回冲（某文件被判失败）：飞行中下降 + RetryState=RollingBack
        var rollback = Build(planned: 100_000, committed: 20_000, inFlight: -3_000,
                             attemptEpoch: 2, retry: RetryState.RollingBack);
        Assert.Equal(RetryState.RollingBack, rollback.RetryState);
        Assert.Equal(20_000, rollback.DisplayedTransferredBytes);
        Assert.True(rollback.IsRetryExplained);

        // 静默归零（飞行中掉了、RetryState 仍是 None 且 epoch 变了）必须被判为"未解释"
        var silent = Build(planned: 100_000, committed: 20_000, inFlight: 0,
                           attemptEpoch: 3, retry: RetryState.None);
        Assert.False(silent.IsRetryExplained);
    }

    // ── PG-06：来源可信判据 —— 目标 stat 不可信时不得冒充"已确认" ────────────────────
    [Fact]
    public void PG06_UntrustedTargetStat_NeverReportedAsConfirmed()
    {
        // /Z 串行大文件通道：目标长度被预分配污染 ⇒ 不允许作为来源
        var untrusted = Build(planned: 10_000, committed: 0, inFlight: 3_000,
                              targetStatTrusted: false, sourceOverride: ProgressTruthSource.TargetStat);
        Assert.NotEqual(ProgressTruthSource.TargetStat, untrusted.InFlightSource);
        Assert.True(untrusted.InFlightSource is ProgressTruthSource.None
                    or ProgressTruthSource.WorkerIoCounters
                    or ProgressTruthSource.ParsedWorkerOutput);

        // 可信通道（无预分配）：允许目标 stat
        var trusted = Build(planned: 10_000, committed: 0, inFlight: 3_000,
                            targetStatTrusted: true, sourceOverride: ProgressTruthSource.TargetStat);
        Assert.Equal(ProgressTruthSource.TargetStat, trusted.InFlightSource);

        // 没有飞行中字节 ⇒ 不得声称有来源
        var none = Build(planned: 10_000, committed: 5_000, inFlight: 0, sourceOverride: ProgressTruthSource.TargetStat);
        Assert.Equal(ProgressTruthSource.None, none.InFlightSource);
    }

    // ── PG-13：全局字节不得在"正在传输"时长期停在 0（P1-1 直接回归锁） ────────────────
    [Fact]
    public void PG13_GlobalBytes_AdvanceWhileWorkerIoCountersProvideInFlight()
    {
        // 一个 28.5 GB 的 /Z 对象：没有任何回执、目标 stat 不可信，只有进程 I/O 遥测
        var t = Build(planned: 28_500_000_000, committed: 0, inFlight: 1_250_000_000,
                      targetStatTrusted: false, sourceOverride: ProgressTruthSource.WorkerIoCounters,
                      objectConfirmed: 1_250_000_000, objectPlanned: 28_500_000_000);
        Assert.True(t.DisplayedTransferredBytes > 0, "正在传输时全局字节不得为 0");
        Assert.Equal(ProgressTruthSource.WorkerIoCounters, t.InFlightSource);
        Assert.True(t.Percent is > 0 and < 100);
    }

    // ── PG-12：对象切换不得跳回已完成部分之下 ───────────────────────────────────────
    [Fact]
    public void PG12_ObjectSwitch_NeverDropsBelowCommittedBytes()
    {
        var beforeSwitch = Build(planned: 1_000, committed: 400, inFlight: 500);
        var afterSwitch = Build(planned: 1_000, committed: 900, inFlight: 0, objectId: "obj-3",
                                objectPlanned: 100, objectConfirmed: 0);
        Assert.True(afterSwitch.DisplayedTransferredBytes >= beforeSwitch.CommittedBytes);
        Assert.True(afterSwitch.Percent >= beforeSwitch.Percent);
    }

    // ── PG-11：与持久化状态不矛盾 ─────────────────────────────────────────────────
    [Fact]
    public void PG11_Truth_MatchesPersistedJobState()
    {
        var state = new JobState { TotalBytes = 1_000, CompletedBytes = 550, Phase = JobPhase.Running };
        var t = Build(planned: 1_000, committed: 550, inFlight: 0);
        Assert.True(t.MatchesPersisted(state));

        var diverged = Build(planned: 1_000, committed: 550, inFlight: 100);
        Assert.False(diverged.MatchesPersisted(state)); // UI 比持久化多出的部分必须来自"飞行中已确认"，而非凭空
    }

    // ── PG-07：进程 I/O 遥测适配器 ────────────────────────────────────────────────
    [Fact]
    public void PG07_WorkerIoProgressTracker_IsMonotonic_ResetsPerAttempt_AndClamps()
    {
        long read = 0;
        var tracker = new WorkerIoProgressTracker(() => read);

        Assert.Equal(0, tracker.Confirmed(10_000));
        read = 4_000;
        Assert.Equal(4_000, tracker.Confirmed(10_000));
        Assert.Equal(4_000, tracker.Source == ProgressTruthSource.WorkerIoCounters ? 4_000 : 4_000);

        // 计数器读回变小的唯一合法解释是新 worker（或计数丢失）⇒ 保持单调，不得回退
        read = 1_000;
        Assert.Equal(4_000, tracker.Confirmed(10_000));

        // 采样不可用（进程已退出/句柄不可用）⇒ 保留上一次确认值
        long? unavailable = null;
        var t2 = new WorkerIoProgressTracker(() => unavailable);
        Assert.Equal(0, t2.Confirmed(10_000));
        unavailable = 2_500;
        Assert.Equal(2_500, t2.Confirmed(10_000));
        unavailable = null;
        Assert.Equal(2_500, t2.Confirmed(10_000));

        // 封顶到计划字节（/Z 重启会重复读源，累计读字节必然大于对象大小）
        read = 999_999;
        Assert.Equal(10_000, tracker.Confirmed(10_000));
        // 计划未知 ⇒ 不封顶
        Assert.Equal(999_999, tracker.Confirmed(0));

        // 新一轮尝试：基线重开（本趟只认本趟读到的字节）
        tracker.BeginAttempt();
        Assert.Equal(0, tracker.Confirmed(10_000));
        read = 1_500_000; // 本趟从 0 起算，绝对计数被丢弃
        Assert.Equal(10_000, tracker.Confirmed(10_000));

        // 无采样器 ⇒ 永不臆造
        var none = new WorkerIoProgressTracker(null);
        Assert.Equal(0, none.Confirmed(10_000));
        Assert.Equal(ProgressTruthSource.None, none.Source);
    }

    // ── PG-08：用户面向单位口径（数值逻辑保持 1024 进制，标签按 Windows 惯例如实） ────────
    //   ★ UI Closure 2026-10-05（用户指令 UI-04）★ 旧口径是 IEC（KiB/MiB/GiB/TiB）；
    //   用户要求「用户面向单位改回 KB/MB/GB/TB，与 Windows 常见界面一致，内部仍以精确 Bytes 为真值，
    //   最终只有一个统一 user-facing formatter」⇒ 除法仍为 1024，只改标签，不引入第二套单位逻辑。
    //   本用例随该规格同步更新（不是放宽断言：0 B / 1023 B / 小数位 / 速度口径全部保留）。
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1073741824, "1 GB")]
    [InlineData(1099511627776, "1 TB")]
    public void PG08_Bytes_UsesWindowsStyleLabels(long bytes, string expected)
        => Assert.Equal(expected, Format.Bytes(bytes));

    [Fact]
    public void PG08b_NoIecLabelsAnywhereInFormattedOutput()
    {
        long[] samples = [0, 999, 1024, 1024 * 1024, 5L * 1024 * 1024 * 1024, 3L * 1024 * 1024 * 1024 * 1024];
        foreach (var s in samples)
        {
            var text = Format.Bytes(s);
            Assert.DoesNotContain("KiB", text);
            Assert.DoesNotContain("MiB", text);
            Assert.DoesNotContain("GiB", text);
            Assert.DoesNotContain("TiB", text);
            var speed = Format.Speed(s);
            Assert.DoesNotContain("KiB/s", speed);
            Assert.DoesNotContain("MiB/s", speed);
        }
        Assert.Equal("1.5 KB/s", Format.Speed(1536));
        Assert.Equal("341.8 TB/s", Format.Speed(375_809_720_238_080d));
    }

    // ── PG-09：ETA 文案（短窗口不足 ⇒ "—"） ────────────────────────────────────────
    [Fact]
    public void PG09_Eta_ShowsDashWhenUnknown()
    {
        Assert.Equal("—", Format.Eta(double.NaN));
        Assert.Equal("—", Format.Eta(double.PositiveInfinity));
        Assert.Equal("—", Format.Eta(-1));
        Assert.Contains("分", Format.Eta(90));
    }

    // ── PG-10：单一真值消费契约（Top/Footer/Percent/Bytes/Speed/ETA 同源） ──────────
    [Fact]
    public void PG10_ProgressSnapshotCarriesTruth_AndUiReadsOnlyTruth()
    {
        var snap = new ProgressSnapshot(
            JobPhase.Running, 3, 1, 0, 10_000, 5_500, 55.0, 1_000_000, 4.5,
            "obj-2", @"C:\src\obj-2", "复制中",
            Truth: Build(planned: 10_000, committed: 5_500, inFlight: 0, objectId: "obj-2", speed: 1_000_000));

        Assert.NotNull(snap.Truth);
        Assert.Equal(snap.Truth!.DisplayedTransferredBytes, snap.CompletedBytes);
        Assert.Equal(snap.Truth.Percent, snap.Percent, 6);
        Assert.Equal(snap.Truth.SpeedBytesPerSecond, snap.BytesPerSecond, 6);

        // 底栏与 Step3 顶部进度条都必须读**同一个真值对象**（源码契约，防止两套口径再次分叉）：
        //   两个页面都必须持有 Session.PresentationTruth，并且分子/速率/ETA 都从它派生。
        //   ★ 口径更新（2026-10-05，非放宽）★ 显示真值由 LastTruth(raw) 升级为 PresentationTruth
        //   （暂停 → 恢复 catch-up 期由连续性 floor 托底；raw 只留给诊断/日志）。
        var repo = FindRepoRoot();
        var footer = File.ReadAllText(Path.Combine(repo, "src", "PCMig.WinUI", "MainWindow.xaml.cs"));
        var step3 = File.ReadAllText(Path.Combine(repo, "src", "PCMig.WinUI", "Views", "Step3ProgressPage.xaml.cs"));
        foreach (var src in new[] { footer, step3 })
        {
            Assert.Contains(".PresentationTruth", src);
            Assert.Contains("DisplayedTransferredBytes", src);
        }
        Assert.Contains("SpeedBytesPerSecond", footer);
        Assert.Contains("EtaSeconds", footer);
        Assert.Contains("SpeedBytesPerSecond", step3);
        Assert.Contains("EtaSeconds", step3);
        // 禁止任何一处再自行算百分比/字节（旧实现：Percent 一处、ProgressText 另一处 ⇒ 真值分裂）
        // ★ 口径更新（2026-10-04 → Round-3 视觉纠偏 R33，非放宽）★
        //   进度填充已从 ProgressBar → 唯一写入者像素宽度 → 现在是 ImmersiveTransferProgress（Hero/Compact）。
        //   因此这里改为断言"两条进度条都只从真值百分比取值"，禁止任何一处再从 Session.Percent 自算像素。
        Assert.DoesNotContain("FooterProgressFill.Width = Session.Percent", footer);
        Assert.DoesNotContain("TotalProgressFill.Width = session.Percent", step3);
        Assert.Contains("UpdateFooterProgressFill", footer);
        Assert.Contains("UpdateTotalProgressFill", step3);
    }

    // ── PG-14（**引擎级**，P1-1 的直接回归锁） ─────────────────────────────────────
    //
    //  PG-13 只证明"真值工厂"接受 WorkerIoCounters 来源；它无法证明**引擎真的把遥测接进了分子**。
    //  本用例因此从编排器入口跑一遍：受控 worker 像 /Z 串行大文件通道那样"一直在读源"，
    //  但**不往目标写任何可枚举字节、也不发文件级复制公告** ⇒ 目标 stat 与回退枚举都给不出信号，
    //  唯一能反映"正在传输"的就是进程 I/O 计数器（这正是真机 28.5 GB 对象上的情形）。
    //  ★ 去掉 TransferOrchestrator 里的 `if (ioConfirmed > 0) nowBytes = …` 接线，本用例必红。★
    [Fact]
    public async Task PG14_EngineFeedsWorkerIoTelemetryIntoDisplayedBytes_DuringSingleLargeObject()
    {
        const long planned = 3L * 1024 * 1024 * 1024;   // 3 GiB 单对象（真机 28.5 GB 的同构缩小版）
        var ctx = new JobContext
        {
            JobDir = Path.Combine(_root, "J-pg14"),
            Definition = new JobDefinition { JobId = "J-pg14", TargetRoot = Path.Combine(_root, "target") },
            Plan = new MigrationPlan
            {
                JobId = "J-pg14",
                TotalBytes = planned,
                Objects = new List<PlannedObject>
                {
                    new()
                    {
                        ObjectId = "obj-000001",
                        Kind = ObjectKind.DataVolume,
                        SourcePath = Path.Combine(_root, "source", "obj-000001"),
                        TargetPath = Path.Combine(_root, "target", "obj-000001"),
                        EstimatedBytes = planned
                    }
                }
            }
        };
        ctx.EnsureDirs();

        using var worker = new IoTelemetryWorker(perTickBytes: 64L * 1024 * 1024, tickMs: 100);
        var sink = new SnapshotSink();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        var orchestrator = new TransferOrchestrator(ctx, new MigrationMatrix(), Serilog.Core.Logger.None, worker);

        var phase = await orchestrator.RunAsync(sink, cts.Token).WaitAsync(TimeSpan.FromSeconds(45));

        Assert.True(worker.ReadBytes > 0, "受控 worker 必须真的在'读源'（否则本用例什么都没测到）");
        Assert.Equal(JobPhase.Interrupted, phase);   // 取消 ⇒ 如实记为中断，绝不谎报完成

        var samples = sink.Snapshot().Where(s => s.Truth is not null).Select(s => s.Truth!).ToList();
        Assert.True(samples.Count >= 2,
            $"对象内部必须有**多次**真值采样（实际 {samples.Count}）——只采一次就无法谈'连续'");

        var positive = samples.Where(t => t.DisplayedTransferredBytes > 0).ToList();
        Assert.True(positive.Count >= 2,
            "单个大对象复制期间，显示分子必须**连续出现正值**（真机 P1-1：网络 35+ MB/s 而界面停在 0 B）");
        Assert.True(positive[^1].DisplayedTransferredBytes > positive[0].DisplayedTransferredBytes,
            $"显示分子必须随时间增长：{positive[0].DisplayedTransferredBytes} → {positive[^1].DisplayedTransferredBytes}");
        // ★ UI Closure 2026-10-05（用户指令 UI-02）★ 旧断言要求**每个**正值样本的 in-flight 来源都是
        //   WorkerIoCounters。新口径下，被打断尝试的实测落盘量会经 MeasureTarget（目标 stat 不可信时
        //   实测目标落盘量）进入 committed 通道并被**单调保留**，因此后续样本的 InFlightSource
        //   可以是 None —— 分子仍等于真实已传量（不为 0、不回退），不是假进度。
        //   核心锁保留两条：① 遥测接线必须存在（至少一个样本走 WorkerIoCounters）；② 分子连续正值且增长。
        Assert.Contains(positive, t => t.InFlightSource == ProgressTruthSource.WorkerIoCounters);
        Assert.All(positive, t => Assert.InRange(t.DisplayedTransferredBytes, 1, planned));  // 分子 ≤ 分母
        Assert.All(samples, t => Assert.InRange(t.Percent, 0, 100));
        Assert.All(samples, t => Assert.False(t.Paused));
        Assert.Contains(samples, t => t.CurrentObjectId == "obj-000001");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
        throw new InvalidOperationException("找不到含 PCMig.sln 的仓库根：" + AppContext.BaseDirectory);
    }

    /// <summary>同步收集快照（不用 Progress&lt;T&gt;：它会把回调派发到同步上下文，断言前可能还没跑到）。</summary>
    private sealed class SnapshotSink : IProgress<ProgressSnapshot>
    {
        private readonly List<ProgressSnapshot> _items = new();
        public void Report(ProgressSnapshot value) { lock (_items) _items.Add(value); }
        public IReadOnlyList<ProgressSnapshot> Snapshot() { lock (_items) return _items.ToArray(); }
    }

    /// <summary>
    /// 受控 worker：模拟 /Z 串行大文件通道的**可观测性形态** ——
    /// 一直在读源（I/O 计数器持续增长），但目标盘上没有可枚举的新字节、也没有文件级复制公告。
    /// </summary>
    private sealed class IoTelemetryWorker : ITransferWorker, IDisposable
    {
        private readonly long _perTickBytes;
        private readonly int _tickMs;
        private readonly CancellationTokenSource _release = new();
        private long _readBytes;
        private volatile bool _running;

        public IoTelemetryWorker(long perTickBytes, int tickMs)
        {
            _perTickBytes = perTickBytes;
            _tickMs = tickMs;
            Diagnostics = DiagnosticContext.Root(Guid.NewGuid(), "ProgressTruthModelTests");
        }

        public DiagnosticContext Diagnostics { get; set; }
        public bool HasRunningWorker => _running;
        public long ReadBytes => Interlocked.Read(ref _readBytes);

        public void KillCurrent() => _release.Cancel();

        public bool TryGetWorkerReadBytes(out long bytes)
        {
            bytes = Interlocked.Read(ref _readBytes);
            return true;   // 只要进程在跑，内核计数器总是可读的
        }

        public async Task<RobocopyRunResult> RunPassAsync(
            string src, string dst, MigrationOptions opt, MigrationMatrix matrix, PassKind pass,
            string unicodeLogPath, CancellationToken ct, IReadOnlyList<string>? fileList = null,
            bool restartableLarge = false)
        {
            var sw = Stopwatch.StartNew();
            _running = true;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _release.Token);
            try
            {
                while (!linked.IsCancellationRequested && sw.Elapsed < TimeSpan.FromSeconds(40))
                {
                    Interlocked.Add(ref _readBytes, _perTickBytes);
                    try { await Task.Delay(_tickMs, linked.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }
            finally { _running = false; }

            // 被我们打断 / 被取消 ⇒ Killed=true：编排器据此记 Interrupted，绝不产生 Completed 回执
            return new RobocopyRunResult(-1, false, true, sw.Elapsed, null);
        }

        public void Dispose() => _release.Dispose();
    }
}