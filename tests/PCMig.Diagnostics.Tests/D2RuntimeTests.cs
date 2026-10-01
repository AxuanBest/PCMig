using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D2 契约：运行时端到端（装配、模式过滤、落盘、viewer 缓存、关闭语义、fail-open）。
/// </summary>
public sealed class D2RuntimeTests
{
    [Fact]
    public void RuntimeWritesEventsToSegmentsAndReportsHealth()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var runtime = DiagnosticRuntime.Start(D2TestSupport.Options(root), out var degraded);
            Assert.Null(degraded);
            Assert.True(runtime.IsStorageAvailable);

            try
            {
                for (var i = 0; i < 20; i++)
                {
                    runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                        TransferEvents.ObjectCompleted,
                        DiagnosticContext.Root(runtime.SessionId, "Test").WithJob("JOB-1").WithObject("obj-" + i),
                        Outcome: DiagnosticOutcome.Succeeded));
                }

                Assert.True(D2TestSupport.WaitUntil(() => runtime.Health.EventsWritten >= 20),
                    $"事件应当被写入，实际 {runtime.Health.EventsWritten}");

                var health = runtime.GetHealthSnapshot();
                // 启动本身也会产生事件（DIA.SessionStarted），因此这里断言"至少"而不是精确相等。
                Assert.True(health.EventsProduced >= 20, $"produced={health.EventsProduced}");
                Assert.True(health.EventsAccepted >= 20, $"accepted={health.EventsAccepted}");
                Assert.True(health.EventsWritten >= 20);
                Assert.True(health.EvidenceComplete, "正常路径不应有任何丢失");

                // 会话元数据在启动时就已落盘。
                Assert.True(File.Exists(runtime.Store!.SessionFilePath));
                Assert.Equal(EventCatalog.CatalogHash, DiagnosticSessionStore.TryReadSessionMetadata(runtime.Store.SessionFilePath)!.CatalogHash);
            }
            finally
            {
                var report = D2TestSupport.Shutdown(runtime);
                Assert.True(report.CleanShutdown, "正常关闭应当是 clean：" + report.FailureReason);
                Assert.True(report.CleanMarkerWritten);
                D2TestSupport.Dispose(runtime);
            }

            var sessionDir = Directory.GetDirectories(Path.Combine(root)).Single();
            Assert.True(File.Exists(Path.Combine(sessionDir, "clean-shutdown.marker")));
            Assert.True(Directory.GetFiles(Path.Combine(sessionDir, "events"), "events-*.jsonl").Length >= 1);
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    [Fact]
    public void OffModeFiltersEverythingButStaysNonThrowing()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var runtime = DiagnosticRuntime.Start(D2TestSupport.Options(root, CaptureMode.Off), out _);
            try
            {
                var baseline = runtime.GetHealthSnapshot().EventsFiltered;

                foreach (var descriptor in EventCatalog.All)
                {
                    var accepted = runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                        descriptor, DiagnosticContext.Root(runtime.SessionId, "Off")));
                    Assert.False(accepted);
                }

                var health = runtime.GetHealthSnapshot();
                Assert.Equal(0, health.EventsProduced);
                Assert.Equal(EventCatalog.Count, health.EventsFiltered - baseline);
                Assert.Equal(0, health.EventsWritten);

                runtime.SetMode(CaptureMode.Operational, "test-enable");
                Assert.True(runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                    TransferEvents.JobRunStarted, DiagnosticContext.Root(runtime.SessionId, "On"))));
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

    [Fact]
    public void VerboseIsOnlyAcceptedInDeepMode()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var runtime = DiagnosticRuntime.Start(D2TestSupport.Options(root), out _);
            try
            {
                var verbose = RobocopyEvents.FileAttemptObserved;
                Assert.Equal(DeliveryClass.Verbose, verbose.Delivery);

                Assert.False(runtime.Publisher.IsEnabledFor(verbose));
                Assert.True(runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                    RobocopyEvents.ProcessStarted, DiagnosticContext.Root(runtime.SessionId, "t"))));

                runtime.SetMode(CaptureMode.Deep, "user-deep");
                Assert.True(runtime.Publisher.IsEnabledFor(verbose));
                Assert.True(runtime.Publisher.TryPublish(new DiagnosticEventDraft(verbose, DiagnosticContext.Root(runtime.SessionId, "t"))));
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

    [Fact]
    public void AnalyzerSinkAndViewerCacheReceiveEvents()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var runtime = DiagnosticRuntime.Start(D2TestSupport.Options(root), out _);
            var analyzerSeen = 0;
            runtime.SetAnalyzerSink(_ => { Interlocked.Increment(ref analyzerSeen); return default; });

            try
            {
                for (var i = 0; i < 10; i++)
                {
                    runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                        TransferEvents.ObjectStarted,
                        DiagnosticContext.Root(runtime.SessionId, "t").WithObject("obj-" + i)));
                }

                Assert.True(D2TestSupport.WaitUntil(() => Volatile.Read(ref analyzerSeen) >= 10),
                    $"analyzer sink 应收到事件，实际 {Volatile.Read(ref analyzerSeen)}");

                Assert.True(D2TestSupport.WaitUntil(() => runtime.TryGetViewerSnapshot(out var events) && events.Length >= 10));
                Assert.True(runtime.TryGetViewerSnapshot(out var snapshot, 5));
                Assert.Equal(5, snapshot.Length);
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

    [Fact]
    public void StorageUnavailableFailsOpenToMemoryOnlyPipeline()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            // 用一个**文件**当存储根目录 ⇒ 目录创建必然失败。
            var blocker = Path.Combine(root, "blocked");
            File.WriteAllText(blocker, "x");

            var options = D2TestSupport.Options(blocker);
            var runtime = DiagnosticRuntime.Start(options, out var degraded);

            try
            {
                Assert.False(runtime.IsStorageAvailable);
                Assert.NotNull(degraded);
                Assert.Null(runtime.Store);
                Assert.Null(runtime.FlightStatistics()); // 没有存储就没有 Flight 落盘

                // 仍然可用：内存分支照常工作，且绝不抛。
                Assert.True(runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                    TransferEvents.JobRunStarted, DiagnosticContext.Root(runtime.SessionId, "t"))));

                Assert.True(runtime.GetHealthSnapshot().StorageDegraded);
                Assert.Equal(0, runtime.Health.EventsWritten);
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

    [Fact]
    public void SecondStartupReportsPreviousSessionUncleanWhenMarkerMissing()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            // 第一段会话：启动但**不做 clean shutdown**（模拟强杀/断电形态）。
            var first = DiagnosticRuntime.Start(D2TestSupport.Options(root), out _);
            first.Publisher.TryPublish(new DiagnosticEventDraft(
                TransferEvents.JobRunStarted, DiagnosticContext.Root(first.SessionId, "t")));
            Assert.True(D2TestSupport.WaitUntil(() => first.Health.EventsWritten >= 1));
            // ★ D6.3 ★ 第二段会话只在**启动那一刻**扫一次存储根（PublishPreviousSessionUncleanIfAny）；
            //   若那一刻上一段会话目录还是"空骨架"，DiagnosticSessionStore.cs:242-243 会有意把它判为
            //   "不是证据" ⇒ 本测试的等待永远等不到（全量套件里表现为等待 5s 后偶发失败）。
            //   所以先确认盘上真的留下了证据文件，而不只是计数器变了，再启动第二段会话。
            Assert.NotNull(first.Store);
            var firstEventsDir = Path.Combine(first.Store!.SessionDir, "events");
            Assert.True(D2TestSupport.WaitUntil(() =>
                    Directory.Exists(firstEventsDir) &&
                    Directory.EnumerateFiles(firstEventsDir, "*.jsonl").Any(f => new FileInfo(f).Length > 0), 10_000),
                "上一段会话没有在盘上留下任何事件证据，第二段会话无从判断它是否干净关闭");
            // 故意不调用 ShutdownAsync：不写 clean marker。

            // 第二段会话：应当报"上次未确认正常关闭"，且**不得**断言崩溃。
            var second = DiagnosticRuntime.Start(D2TestSupport.Options(root), out _);
            try
            {
                // ★ D6.3 ★ 失败时必须能看出"到底看见了什么"（空快照/没有这条事件是两种不同的病），
                //   并且给足有界等待：并发的真实 runtime 一多，启动期这条上报会被调度推迟。
                var seen = Array.Empty<string>();
                Assert.True(D2TestSupport.WaitUntil(() =>
                {
                    if (!second.TryGetViewerSnapshot(out var events)) return false;
                    seen = events.Select(e => e.Descriptor.Name).ToArray();
                    return seen.Contains(DiagnosticsEvents.PreviousSessionUnclean.Name);
                }, 20_000),
                    $"启动时必须报出「上次未确认正常关闭」；viewer 实际有 {seen.Length} 条：{string.Join(",", seen)}");
            }
            finally
            {
                D2TestSupport.Shutdown(second);
                D2TestSupport.Dispose(second);
            }
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    [Fact]
    public void PublishedEventsCarryStampedIdentityAndRedactedFields()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var runtime = DiagnosticRuntime.Start(D2TestSupport.Options(root), out _);
            try
            {
                const string canary = "Passw0rd=canary";
                runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                    NetEvents.SmbConnectFailed,
                    DiagnosticContext.Root(runtime.SessionId, "ConnectionViewModel").WithControl("Step1.Connect"),
                    new DiaStorageFailedPayload("x", "y", 53, false),
                    Outcome: DiagnosticOutcome.Failed,
                    ErrorDomain: ErrorDomain.Win32,
                    Win32Error: 53,
                    Message: $"连接失败 password={canary} path {@"C:\Users\someone\secret"}"));

                // ★ 等待条件必须针对**这一条**事件 ★ 只等"≥1 条"会被启动期事件（DIA.SessionStarted）提前满足，
//   于是偶发地取到错误的那条（实测约 1/3 概率失败：flaky 的根因就在这里）。
                Assert.True(D2TestSupport.WaitUntil(() =>
                {
                    runtime.TryGetViewerSnapshot(out var snapshot);
                    return snapshot.Any(e => e.Descriptor.Name == NetEvents.SmbConnectFailed.Name);
                }, 10_000), "没有观察到 NET.SmbConnectFailed");
                runtime.TryGetViewerSnapshot(out var events);
                var evt = events.Last(e => e.Descriptor.Name == NetEvents.SmbConnectFailed.Name);

                Assert.Equal(runtime.SessionId, evt.SessionId);
                Assert.True(evt.Sequence > 0);
                Assert.Equal(CaptureMode.Operational, evt.CaptureMode);
                Assert.Equal(StateOwner.Unknown, evt.StateOwner ?? StateOwner.Unknown);
                Assert.Equal(53, evt.Win32Error);
                Assert.Equal(ErrorDomain.Win32, evt.ErrorDomain);
                Assert.DoesNotContain(canary, evt.Message ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain("someone", evt.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                Assert.Null(evt.JobId);                       // 未提供 Job 上下文 ⇒ 不得凭空编一个
                Assert.Equal("Step1.Connect", evt.ControlId);
                Assert.Equal("ConnectionViewModel", evt.Component);
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

    [Fact]
    public void ShutdownIsIdempotentAndBounded()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var runtime = DiagnosticRuntime.Start(D2TestSupport.Options(root), out _);
            var first = D2TestSupport.Shutdown(runtime, 500);
            var second = D2TestSupport.Shutdown(runtime, 500);

            Assert.True(first.CleanShutdown);
            Assert.False(second.CleanShutdown);
            Assert.Equal("already-shutdown", second.FailureReason);

            D2TestSupport.Dispose(runtime);
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }
}