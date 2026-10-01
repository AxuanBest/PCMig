using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using PCMig.Diagnostics;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using Xunit;
using Xunit.Abstractions;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// ★ D6.3 Fixture 17（D63-M）★ **定向并发基准**：内存环接近满 + 另一线程正在触发飞行窗口时，
/// 生产者（`TryPublish` 热路径）的延迟分布 p50 / p95 / p99 / max 各是多少。
///
/// 为什么必须有：D6.1 曾把 `RingEventCapacity` 当"触发前窗口的容量"，而触发要**复制**整环，
/// 复制与生产者在同一把门上 ⇒ "环越满、触发越慢"这件事只有实测才知道。
/// 本基准只做**本机、本进程、本会话**的局部证据（不宣称任何产品保证——方案 §25.2：
/// 未经 D7 基准测试前，所有数值都只是候选参数）。
///
/// 判据故意宽松：它是"不得爆炸"的物理边界（p99 ≤ 50 ms、max ≤ 250 ms），不是性能承诺。
/// 同时校验一条诚实性配对：**有真实丢弃就必须同时自称证据不完整**（不得只丢不说）。
/// </summary>
public sealed class D63FlightLatencyBenchmarkTests : IDisposable
{
    private const int SampleCount = 20_000;
    private const double BenchmarkSeconds = 2.0;
    private const int WarmupSamples = 200;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-d63-bench-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ITestOutputHelper _output;

    public D63FlightLatencyBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* 句柄延迟释放 */ }
    }

    [Fact]
    public void Fixture17_ProducerLatencyStaysBoundedWhileFlightWindowsAreTriggeredConcurrently()
    {
        var options = new DiagnosticRuntimeOptions
        {
            StorageRoot = _root,
            InitialMode = CaptureMode.Operational,
            AppVersion = "d63-bench",
            // 环刻意开小：持续生产时它**始终接近满**（复制整环的代价才会暴露出来）。
            RingEventCapacity = 512,
            RingByteBudget = 4L * 1024 * 1024,
            // 触发要真的被接受，才能测到"正在触发/封窗"的生产者延迟。
            FlightMaxPinnedWindows = 8,
            FlightTriggerCooldownMs = 0,
            FlightPostWindowMs = 50,
            WriterFlushIntervalMs = 10,
            // 收件箱与分析器开大，让基准测的是环 + 触发，而不是被队列容量拒收。
            ViewerQueueCapacity = 65_536,
            OperationalQueueCapacity = 65_536,
            AnalyzerMaxPending = 65_536,
            ShutdownBudgetMs = 5_000,
        };

        var runtime = DiagnosticRuntime.Start(options, out var degraded);
        Assert.Null(degraded);

        try
        {
            var samples = new List<long>(600_000);
            var stop = new CancellationTokenSource();
            var accepted = 0;
            var rejected = 0;
            var triggerSamples = new System.Collections.Concurrent.ConcurrentQueue<double>();

            // 触发线程：与被测的生产者真正并发（不是"先触发完再生产"）。
            var triggerThread = new Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var start = Stopwatch.GetTimestamp();
                    var outcome = runtime.TriggerFlight("bench", "d63-m");
                    triggerSamples.Enqueue((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
                    if (outcome.Accepted) Interlocked.Increment(ref accepted);
                    else Interlocked.Increment(ref rejected);
                    Thread.Sleep(1);
                }
            })
            { IsBackground = true, Name = "d63-bench-trigger" };
            triggerThread.Start();

            var context = DiagnosticContext.Root(runtime.SessionId, "D63Bench");
            var runStart = Stopwatch.GetTimestamp();
            // 按**时长**采样（不是固定条数）：真实量级是"持续生产"，固定 2 万条只需 58 ms，
            // 触发线程来不及跑够次数（实测只有 4 次接受）——那样测不到"正在触发/封窗"的并发。
            while (samples.Count < 600_000
                   && (Stopwatch.GetTimestamp() - runStart) / (double)Stopwatch.Frequency < BenchmarkSeconds)
            {
                var start = Stopwatch.GetTimestamp();
                runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                    TransferEvents.JobRunStarted,
                    context.WithJob("D63-BENCH-" + (samples.Count % 64))));
                var elapsed = Stopwatch.GetTimestamp() - start;
                samples.Add(elapsed);
            }
            var runSeconds = (Stopwatch.GetTimestamp() - runStart) / (double)Stopwatch.Frequency;

            stop.Cancel();
            triggerThread.Join(5_000);

            // 预热样本丢弃（首个段文件创建、JIT、线程池起步都在这段里）。
            var measured = samples.Skip(WarmupSamples)
                .Select(t => t * 1000.0 / Stopwatch.Frequency)
                .OrderBy(x => x)
                .ToArray();

            double Pct(double p) => measured[Math.Min(measured.Length - 1, (int)Math.Ceiling(p * measured.Length) - 1)];
            var p50 = Pct(0.50);
            var p95 = Pct(0.95);
            var p99 = Pct(0.99);
            var max = measured[^1];
            var mean = measured.Average();

            var health = runtime.GetHealthSnapshot();
            var loss = runtime.Loss.Snapshot();

            var report = new StringBuilder();
            report.AppendLine("D6.3 飞行窗口触发并发基准（本机/本进程/本会话局部证据，非产品保证）");
            report.AppendLine("when=" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:sszzz", CultureInfo.InvariantCulture));
            report.AppendLine("samples=" + samples.Count + " (warmup dropped=" + WarmupSamples + ") producerRunSeconds=" +
                              runSeconds.ToString("F3", CultureInfo.InvariantCulture));
            report.AppendLine("ringEventCapacity=" + options.RingEventCapacity + " ringByteBudget=" + options.RingByteBudget);
            report.AppendLine("flightMaxPinnedWindows=" + options.FlightMaxPinnedWindows +
                              " flightPostWindowMs=" + options.FlightPostWindowMs +
                              " cooldownMs=" + options.FlightTriggerCooldownMs);
            report.AppendLine("trigger attempts=" + (accepted + rejected) + " accepted=" + accepted + " rejected=" + rejected);
            var triggerMs = triggerSamples.ToArray().OrderBy(x => x).ToArray();
            if (triggerMs.Length > 0)
            {
                double TriggerPct(double p) => triggerMs[Math.Min(triggerMs.Length - 1, (int)Math.Ceiling(p * triggerMs.Length) - 1)];
                report.AppendLine("trigger call latency ms (证据，不设阈值：封窗等待是有界设计): p50=" +
                                  TriggerPct(0.50).ToString("F3", CultureInfo.InvariantCulture) +
                                  " p95=" + TriggerPct(0.95).ToString("F3", CultureInfo.InvariantCulture) +
                                  " max=" + TriggerPct(1.0).ToString("F3", CultureInfo.InvariantCulture) +
                                  " mean=" + triggerMs.Average().ToString("F3", CultureInfo.InvariantCulture));
            }
            report.AppendLine("producer latency ms: p50=" + p50.ToString("F3", CultureInfo.InvariantCulture) +
                              " p95=" + p95.ToString("F3", CultureInfo.InvariantCulture) +
                              " p99=" + p99.ToString("F3", CultureInfo.InvariantCulture) +
                              " max=" + max.ToString("F3", CultureInfo.InvariantCulture) +
                              " mean=" + mean.ToString("F3", CultureInfo.InvariantCulture));
            report.AppendLine("health: degraded=" + health.IsDegraded + " evidenceComplete=" + health.EvidenceComplete +
                              " lossEpoch=" + health.LossEpoch + " dropped=" + health.EventsDropped +
                              " evicted=" + health.EventsEvicted + " produced=" + health.EventsProduced +
                              " written=" + health.EventsWritten);
            if (loss.Records.Count > 0)
                report.AppendLine("loss records=" + string.Join(", ",
                    loss.Records.Select(r => r.Branch + "/" + r.DeliveryClass + " " + r.ReasonCode + " x" + r.Total)));

            var text = report.ToString();
            _output.WriteLine(text);
            var evidencePath = Path.Combine(Path.GetTempPath(), "pcmig-d63-flight-bench.txt");
            File.WriteAllText(evidencePath, text, new UTF8Encoding(false));
            _output.WriteLine("evidence=" + evidencePath);

            // ── 判据（物理边界，不是性能承诺）──
            Assert.True(accepted > 0, "整个基准期间一次触发都没被接受，延迟数据无意义（rejected=" + rejected + "）");
            Assert.True(p99 <= 50.0, "生产者 p99 延迟超出物理边界 50 ms：" + p99.ToString("F3", CultureInfo.InvariantCulture) + " ms\n" + text);
            Assert.True(max <= 250.0, "生产者 max 延迟超出物理边界 250 ms：" + max.ToString("F3", CultureInfo.InvariantCulture) + " ms\n" + text);

            // ── 诚实性配对：丢了就必须承认不完整（不得只丢不说）──
            if (health.EventsDropped > 0)
                Assert.False(health.EvidenceComplete,
                    "有真实丢弃却仍自称证据完整：" + text);
        }
        finally
        {
            D2TestSupport.Shutdown(runtime);
            D2TestSupport.Dispose(runtime);
        }
    }
}