using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using Xunit;
using Xunit.Abstractions;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D6.3 追加小包（viewer 收件箱容量与显示视野解耦）。
///
/// **实测缺陷（不是推测）**：`D2ProbeTests.PipelineDrainsRealisticBurstWithoutLoss` 在全量套件里
/// 偶发失败，丢失台账给出唯一答案：
/// `lossEpoch=1 dropped=1 evicted=0 records=[viewer/Operational queue-full x1]`
/// ——旧实现把 viewer 收件箱容量直接写成 `ViewerMaxEvents`（= 2000），而"一次真实量级突发"
/// 就是 2000 条 + 1 条会话开始事件 = **2001 条**，于是只要 viewer 泵线程在有负载的机器上
/// 来不及排空，这条最窄的环**必然**挤掉一条：`EvidenceComplete=false`。
///
/// 本文件把两类事实钉住：
///   ① 结构事实：收件箱容量与显示视野**不是同一个数**（默认收件箱 > 显示视野）；
///   ② 行为事实：默认配置下 2001 条突发零丢失、不出现任何 viewer 分支丢弃。
///
/// 注意：这**不是**"放宽断言"。队列满仍然算丢失、仍然进丢失台账与 lossEpoch
/// （该语义由 WP A / WP F 的既有测试钉住）；这里修的是"显示视野恰好等于突发规模"这个结构性缺口。
/// </summary>
public sealed class D63ViewerQueueCliffTests
{
    private readonly ITestOutputHelper _output;

    public D63ViewerQueueCliffTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Fixture14_ViewerInboxCapacityIsNotTheDisplayHorizon()
    {
        var defaults = new DiagnosticRuntimeOptions();

        Assert.True(defaults.ViewerQueueCapacity > defaults.ViewerMaxEvents,
            "viewer 收件箱容量必须严格大于显示视野，否则'一次真实量级突发'会挤掉显示视野之外的证据：" +
            $"ViewerQueueCapacity={defaults.ViewerQueueCapacity} ViewerMaxEvents={defaults.ViewerMaxEvents}");

        // 收件箱必须由独立配置项驱动，而不是继续复用显示视野（否则改显示窗口会顺带改证据通道）。
        var runtimeSrc = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "PCMig.Diagnostics", "DiagnosticRuntime.cs"));
        Assert.Contains("_options.ViewerQueueCapacity", runtimeSrc);

        var cacheSites = System.Text.RegularExpressions.Regex.Matches(
            runtimeSrc, "new ViewerEventCache\\(options\\.ViewerMaxEvents\\)").Count;
        Assert.True(cacheSites == 1,
            "显示缓存必须仍然由 ViewerMaxEvents 决定（恰好 1 处），实际 " + cacheSites);
    }

    [Fact]
    public void Fixture14_RealisticBurstCannotOverflowTheViewerInboxAtDefaultSettings()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            // 刻意**不动**任何容量：这里测的就是出厂默认配置在真实突发下的表现
            // （D6.1 修 analyzer 时同样是靠默认值立场，而不是在测试里改大容量）。
            var options = D2TestSupport.Options(root);
            var runtime = DiagnosticRuntime.Start(options, out _);

            var total = options.ViewerMaxEvents + 1; // 恰好越过"显示视野"一条
            try
            {
                var ctx = DiagnosticContext.Root(runtime.SessionId, "ViewerCliff").WithJob("JOB-VIEWER");
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < total; i++)
                {
                    runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                        TransferEvents.ObjectCompleted, ctx.WithObject("obj-" + (i % 500)),
                        Outcome: DiagnosticOutcome.Succeeded));
                }

                Assert.True(D2TestSupport.WaitUntil(() => runtime.Health.EventsWritten >= total, 20_000),
                    $"应有 {total} 条落盘，实际 {runtime.Health.EventsWritten}");
                sw.Stop();

                var health = runtime.GetHealthSnapshot();
                var records = runtime.Loss.Snapshot().Records
                    .Select(r => $"{r.Branch}/{r.DeliveryClass} {r.ReasonCode} x{r.Total}")
                    .ToArray();

                _output.WriteLine($"默认配置突发 {total} 条：{sw.Elapsed.TotalMilliseconds:F0} ms 全部落盘");
                _output.WriteLine($"viewer: 收件箱={options.ViewerQueueCapacity} 显示视野={options.ViewerMaxEvents}");
                _output.WriteLine($"health: produced={health.EventsProduced} written={health.EventsWritten} " +
                                  $"dropped={health.EventsDropped} evicted={health.EventsEvicted} lossEpoch={health.LossEpoch}");

                Assert.True(health.EvidenceComplete,
                    "越过显示视野一条的突发不允许让证据变成不完整（丢的是显示缓存之外的队列位）：" +
                    $"lossEpoch={health.LossEpoch} dropped={health.EventsDropped} " +
                    $"records=[{string.Join("; ", records)}]");

                Assert.DoesNotContain(records, r => r.StartsWith("viewer/", StringComparison.Ordinal));
            }
            finally
            {
                D2TestSupport.Shutdown(runtime);
                D2TestSupport.Dispose(runtime);
            }
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
        throw new InvalidOperationException("找不到仓库根");
    }
}