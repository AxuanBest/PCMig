using System;
using System.IO;
using System.Linq;
using System.Threading;
using PCMig.Core.Diagnostics;
using PCMig.Core.Jobs;
using PCMig.Core.Models;
using PCMig.Core.Preflight;
using PCMig.Core.State;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D3a 契约：Core 侧观察点（连接/持久化/暂停请求）。
///
/// 两类断言缺一不可：
///   ① **有观察**：装了 sink 之后，真实内部阶段（Move 成功 / 目标已存在被跳过 / 文件损坏）都能变成
///      带 typed payload 的事件；
///   ② **零行为改变**：同一操作在 ON 与 OFF 下产生的**文件内容与返回/抛出行为完全一致**。
/// </summary>
[Collection(DiagnosticsAmbientCollection.Name)]
public sealed class D3CoreInstrumentationTests
{
    /// <summary>
    /// 这些用例跑在 **Deep** 模式：PST.WriteStarted 这类"每次写入的起始阶段"属 Verbose
    /// （Operational 模式下会被过滤，这是有意的——它只是细节，不是结论）。
    /// Deep 模式下 Verbose 会额外进入 viewer（见 FanOutStage），因此可以逐项断言。
    /// </summary>
    private static DiagnosticRuntime StartRuntime(string root, out IDisposable scope)
    {
        var runtime = DiagnosticRuntime.Start(D2TestSupport.Options(root, CaptureMode.Deep), out _);
        scope = CoreDiagnostics.Install(runtime);
        return runtime;
    }

    private static DiagnosticEvent[] Events(DiagnosticRuntime runtime, string? eventName = null)
    {
        Assert.True(D2TestSupport.WaitUntil(() => runtime.TryGetViewerSnapshot(out var e) && e.Length > 0 || eventName is null));
        runtime.TryGetViewerSnapshot(out var events);
        return eventName is null ? events : events.Where(e => e.Descriptor.Name == eventName).ToArray();
    }

    private static DiagnosticEvent[] WaitForEvents(DiagnosticRuntime runtime, string eventName, int atLeast = 1)
    {
        Assert.True(D2TestSupport.WaitUntil(() =>
        {
            runtime.TryGetViewerSnapshot(out var snapshot);
            return snapshot.Count(e => e.Descriptor.Name == eventName) >= atLeast;
        }, 10_000), $"没有观察到 {eventName}");

        runtime.TryGetViewerSnapshot(out var events);
        return events.Where(e => e.Descriptor.Name == eventName).ToArray();
    }

    [Fact]
    public void WriteAtomicEmitsStartedAndSucceededWithArtifactKind()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var runtime = StartRuntime(root, out var scope);
            try
            {
                var path = Path.Combine(root, "store", "job-state.json");
                JsonStateStore.WriteAtomic(path, new JobState { JobId = "JOB-1" }, "JobState",
                    CoreDiagnostics.ContextFor("Test", "JOB-1"));

                Assert.True(File.Exists(path));

                var started = WaitForEvents(runtime, PersistenceEvents.WriteStarted.Name);
                var succeeded = WaitForEvents(runtime, PersistenceEvents.WriteSucceeded.Name);

                var payload = Assert.IsType<PstWritePayload>(succeeded[0].Payload);
                Assert.Equal("JobState", payload.ArtifactKind);
                Assert.Equal("Move", payload.Stage);
                Assert.False(payload.DestinationExisted);          // 首次创建
                Assert.Equal(DiagnosticOutcome.Succeeded, succeeded[0].Outcome);

                var beginPayload = Assert.IsType<PstWritePayload>(started[0].Payload);
                Assert.Equal("Begin", beginPayload.Stage);
                Assert.Equal("JOB-1", started[0].JobId);
            }
            finally
            {
                scope.Dispose();
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
    public void JobContextReceiptWriteCarriesObjectScopeAndReceiptKind()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var runtime = StartRuntime(root, out var scope);
            try
            {
                var ctx = new JobContext
                {
                    JobDir = Path.Combine(root, "job"),
                    Definition = new JobDefinition { JobId = "JOB-R" },
                    Diagnostics = CoreDiagnostics.ContextFor("JobContext", "JOB-R"),
                };
                ctx.EnsureDirs();
                ctx.SaveReceipt(new ObjectReceipt { ObjectId = "obj-000007", Status = ObjectStatus.Completed });

                var succeeded = WaitForEvents(runtime, PersistenceEvents.WriteSucceeded.Name);
                var receiptEvent = succeeded.Last(e => (e.Payload as PstWritePayload)?.ArtifactKind == "Receipt");
                Assert.Equal("JOB-R", receiptEvent.JobId);
                Assert.Equal("obj-000007", receiptEvent.ObjectId);
            }
            finally
            {
                scope.Dispose();
                D2TestSupport.Shutdown(runtime);
                D2TestSupport.Dispose(runtime);
            }
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    /// <summary>
    /// ★ 本系统的核心动机之一 ★ 目标已存在且写不进时，WriteAtomic **只告警并返回**。
    /// 那必须被记成 WriteSkipped（不是成功、也不是失败），否则"回执/状态到底落盘没有"永远说不清。
    /// </summary>
    [Fact]
    public void SkippedWriteIsReportedAsSkippedNotAsSuccess()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var runtime = StartRuntime(root, out var scope);
            try
            {
                var dir = Path.Combine(root, "skip");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "job-state.json");
                File.WriteAllText(path, "{\"jobId\":\"OLD\"}");

                // 用独占句柄把目标占住 ⇒ Move 覆盖必然失败 ⇒ 走"已存在 ⇒ 告警返回"分支。
                using (var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    JsonStateStore.WriteAtomic(path, new JobState { JobId = "NEW" }, "JobState");
                }

                var skipped = WaitForEvents(runtime, PersistenceEvents.WriteSkipped.Name);
                var payload = Assert.IsType<PstWritePayload>(skipped[0].Payload);
                Assert.Equal("Move", payload.Stage);
                Assert.True(payload.DestinationExisted);
                Assert.NotNull(payload.ReasonCode);
                Assert.Equal(DiagnosticOutcome.Skipped, skipped[0].Outcome);

                // 业务语义不变：旧值仍在，且没有抛异常。
                Assert.Contains("OLD", File.ReadAllText(path), StringComparison.Ordinal);
            }
            finally
            {
                scope.Dispose();
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
    public void CorruptExistingFileIsReportedAsReadFailure()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var runtime = StartRuntime(root, out var scope);
            try
            {
                var path = Path.Combine(root, "store", "verify-report.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "{ this is not json");

                Assert.False(JsonStateStore.TryRead<VerifyReport>(path, out _));

                var failed = WaitForEvents(runtime, PersistenceEvents.ReadFailed.Name);
                var payload = Assert.IsType<PstReadFailurePayload>(failed[0].Payload);
                Assert.Equal("VerifyReport", payload.ArtifactKind);
                Assert.Equal(DiagnosticOutcome.Failed, failed[0].Outcome);

                // 不存在的文件是正常首次运行：不得被当成失败刷屏。
                var absent = Path.Combine(root, "store", "absent.json");
                Assert.False(JsonStateStore.TryRead<VerifyReport>(absent, out _));
                runtime.TryGetViewerSnapshot(out var all);
                Assert.Single(all, e => e.Descriptor.Name == PersistenceEvents.ReadFailed.Name);
            }
            finally
            {
                scope.Dispose();
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
    public void CorruptReceiptIsReportedAndStillSkipped()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var runtime = StartRuntime(root, out var scope);
            try
            {
                var receipts = Path.Combine(root, "receipts");
                Directory.CreateDirectory(receipts);
                File.WriteAllText(Path.Combine(receipts, "obj-1.json"), "{broken");
                File.WriteAllText(Path.Combine(receipts, "obj-2.json"),
                    "{\"objectId\":\"obj-2\",\"status\":\"completed\"}");

                var corruptMessages = 0;
                var list = JsonStateStore.ReadAllReceipts<ObjectReceipt>(receipts, _ => corruptMessages++);

                Assert.Single(list);                 // 业务语义不变：损坏的被跳过
                Assert.Equal(1, corruptMessages);
                var corrupt = WaitForEvents(runtime, PersistenceEvents.ReceiptCorrupt.Name);
                Assert.Equal(DiagnosticOutcome.Skipped, corrupt[0].Outcome);
            }
            finally
            {
                scope.Dispose();
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
    public void PauseRequestWriteAndClearAreObserved()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var runtime = StartRuntime(root, out var scope);
            try
            {
                var ctx = new JobContext
                {
                    JobDir = Path.Combine(root, "job"),
                    Definition = new JobDefinition { JobId = "JOB-P" },
                    Diagnostics = CoreDiagnostics.ContextFor("JobContext", "JOB-P"),
                };
                ctx.EnsureDirs();

                ctx.RequestPause(immediate: true);
                Assert.True(File.Exists(ctx.PauseRequestPath));
                Assert.Equal("Immediate", File.ReadAllText(ctx.PauseRequestPath));

                var requested = WaitForEvents(runtime, TransferEvents.PauseRequestWriteResult.Name);
                var payload = Assert.IsType<TrnPauseRequestPayload>(requested[0].Payload);
                Assert.True(payload.Immediate);
                Assert.True(payload.Succeeded);
                Assert.Equal(DiagnosticOutcome.Succeeded, requested[0].Outcome);
                // 暂停请求文件的**路径**也要可追踪（脱敏引用，无明文）。
                Assert.NotNull(requested[0].Path);
                Assert.Equal(PathRole.JobDir, requested[0].Path!.Role);

                ctx.ClearPauseRequest();
                Assert.False(File.Exists(ctx.PauseRequestPath));
                var cleared = WaitForEvents(runtime, TransferEvents.PauseRequestCleared.Name);
                Assert.Equal("JOB-P", cleared[0].JobId);
            }
            finally
            {
                scope.Dispose();
                D2TestSupport.Shutdown(runtime);
                D2TestSupport.Dispose(runtime);
            }
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    /// <summary>★ observer 边界的核心证明 ★ 装着诊断跑一遍，产物字节必须与不装时完全一致。</summary>
    [Fact]
    public void PersistenceOutputIsByteIdenticalWithAndWithoutDiagnostics()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var state = new JobState { JobId = "JOB-EQ", Phase = JobPhase.Running, TotalObjects = 3, CompletedObjects = 1 };

            // OFF
            var offPath = Path.Combine(root, "off", "job-state.json");
            JsonStateStore.WriteAtomic(offPath, state, "JobState");
            var offBytes = File.ReadAllBytes(offPath);

            // ON
            var runtime = StartRuntime(root, out var scope);
            try
            {
                var onPath = Path.Combine(root, "on", "job-state.json");
                JsonStateStore.WriteAtomic(onPath, state, "JobState");
                var onBytes = File.ReadAllBytes(onPath);

                Assert.Equal(offBytes.Length, onBytes.Length);
                Assert.Equal(offBytes, onBytes);
                Assert.True(runtime.Health.EventsWritten > 0, "ON 时应当确实产生了诊断事件");
            }
            finally
            {
                scope.Dispose();
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
    public void CheckCodesAreStableAndNeverUncodedForKnownChecks()
    {
        // 检查名 → checkCode 的稳定映射（规则只依赖 code，绝不依赖中文名）。
        Assert.Equal("Smb445", PreflightChecker.CheckCodeFor("SMB 445 端口可达"));
        Assert.Equal("IpcSession", PreflightChecker.CheckCodeFor("IPC$ 凭据会话"));
        Assert.Equal("DirectShareSession", PreflightChecker.CheckCodeFor("直连共享凭据会话"));
        Assert.Equal("ShareEnumeration", PreflightChecker.CheckCodeFor("共享枚举"));
        Assert.Equal("AdminShareProbe", PreflightChecker.CheckCodeFor("管理共享探测"));
        Assert.Equal("SourcePath", PreflightChecker.CheckCodeFor(@"源路径 \\host\d"));
        Assert.Equal("Benchmark", PreflightChecker.CheckCodeFor("链路吞吐基准"));
        Assert.Equal("TargetFilesystem", PreflightChecker.CheckCodeFor("目标盘文件系统 FAT32"));
        Assert.Equal("TargetSpace", PreflightChecker.CheckCodeFor(@"目标盘 D:\ 可用空间"));
        Assert.Equal("TargetVolume", PreflightChecker.CheckCodeFor("目标盘检查"));
        Assert.Equal("Robocopy", PreflightChecker.CheckCodeFor("Robocopy 可用性"));

        // 未知检查名回落为 uncoded（有测试兜底 ⇒ 新增检查项必须同时补映射）。
        Assert.Equal("uncoded", PreflightChecker.CheckCodeFor("某个将来才加的检查"));
    }

    /// <summary>
    /// 静态接线契约（**不联网**）：确认预检里那些观察点真的被接到了发布器上。
    /// 行为理由：这些事件只在真实 SMB/DNS 环境里才会产生，单测不能去连网络；
    /// 因此用源码接线断言证明"接线存在且只用 typed payload"。
    /// </summary>
    [Fact]
    public void PreflightInstrumentationIsWiredWithoutNetworkAccess()
    {
        var root = FindRepoRoot();
        var text = File.ReadAllText(Path.Combine(root, "src", "PCMig.Core", "Preflight", "PreflightChecker.cs"));

        foreach (var token in new[]
                 {
                     "NetEvents.ProbeStarted", "NetEvents.DnsResolved", "NetEvents.DnsFailed",
                     "NetEvents.TcpProbeAttempt", "NetEvents.TcpProbeFailed",
                     "PreflightEvents.PreflightStarted", "PreflightEvents.CheckCompleted",
                     "PreflightEvents.SourcePathProbeResult", "PreflightEvents.TargetVolumeObserved",
                     "PreflightEvents.BenchmarkCompleted", "PreflightEvents.PreflightCompleted",
                     "PflCheckPayload", "PflSourceProbePayload", "PflTargetVolumePayload",
                     "CoreDiagnostics.Sink.Token(",       // 主机名必须别名化，不得写明文
                 })
        {
            Assert.Contains(token, text, StringComparison.Ordinal);
        }

        // 观察失败绝不外抛：每个观察点都必须包在 try/catch 里。
        Assert.Contains("/* 观察失败绝不影响预检 */", text, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
        }
        throw new InvalidOperationException("找不到仓库根：" + AppContext.BaseDirectory);
    }
}