using System;
using System.IO;
using System.Linq;
using System.Threading;
using PCMig.Core.Diagnostics;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D3b 契约：robocopy 子进程与对象级结果的真观察（**真 robocopy 进程**，本地临时目录，秒级）。
///
/// 与既有 `RobocopyEndToEndTests` 的关系：那个测试证明"参数拼对了、文件真过去了"；
/// 本文件证明"**诊断看得见这件事，且不泄露路径/不改变行为**"。
/// </summary>
[Collection(DiagnosticsAmbientCollection.Name)]
public sealed class D3bTransferInstrumentationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-d3b-" + Guid.NewGuid().ToString("N")[..8]);

    public D3bTransferInstrumentationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* 句柄延迟释放 */ }
    }

    private DiagnosticRuntime StartDeep(out IDisposable scope)
    {
        var runtime = DiagnosticRuntime.Start(D2TestSupport.Options(_root, CaptureMode.Deep), out _);
        scope = CoreDiagnostics.Install(runtime);
        return runtime;
    }

    private static DiagnosticEvent[] WaitFor(DiagnosticRuntime runtime, string eventName, int atLeast = 1)
    {
        Assert.True(D2TestSupport.WaitUntil(() =>
        {
            runtime.TryGetViewerSnapshot(out var snapshot);
            return snapshot.Count(e => e.Descriptor.Name == eventName) >= atLeast;
        }, 15_000), $"没有观察到 {eventName}");

        runtime.TryGetViewerSnapshot(out var all);
        return all.Where(e => e.Descriptor.Name == eventName).ToArray();
    }

    [Fact]
    public async Task SuccessfulPassEmitsProcessIdentityExitCodeAndStructuredArgsOnly()
    {
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "a.txt"), "a");
        File.WriteAllText(Path.Combine(src, "sub", "b.txt"), "b");

        var runtime = StartDeep(out var scope);
        try
        {
            var runner = new RobocopyRunner(Serilog.Core.Logger.None);
            var result = await runner.RunPassAsync(
                src,
                Path.Combine(_root, "dst"),
                new MigrationOptions(), new MigrationMatrix(), PassKind.Bulk,
                Path.Combine(_root, "robo.log"), CancellationToken.None);

            Assert.True(result.Success, $"Exit={result.ExitCode}");

            // 起进程前后都有事实。
            WaitFor(runtime, RobocopyEvents.ProcessStartRequested.Name);
            var started = WaitFor(runtime, RobocopyEvents.ProcessStarted.Name);
            var payload = Assert.IsType<RbcProcessPayload>(started[0].Payload);

            Assert.True(payload.ProcessId > 0);
            Assert.StartsWith("pid:", payload.ProcessIdentity, StringComparison.Ordinal);
            Assert.Contains(payload.ProcessId.ToString(), payload.ProcessIdentity, StringComparison.Ordinal);
            // ★ 稳定 token，不是中文显示文字 ★（规则只依赖 token）
            Assert.Equal(DiagnosticPass.Bulk, payload.Channel);
            Assert.Equal("mt", payload.ChannelMode);
            Assert.Contains("/R:", payload.ArgumentSummary, StringComparison.Ordinal);
            // 摘要里不得出现任何中文显示文字（避免"看着像判据"）。
            Assert.DoesNotContain("多线程", payload.ArgumentSummary, StringComparison.Ordinal);

            // ★ 隐私：参数摘要绝不含源/目标路径（也不含任何盘符形态）★
            Assert.DoesNotContain(src, payload.ArgumentSummary, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(_root, payload.ArgumentSummary, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(":\\", payload.ArgumentSummary, StringComparison.Ordinal);

            // 退出码：0–7 是成功位掩码（这里传了 2 个文件 ⇒ 通常是 1）。
            var exited = WaitFor(runtime, RobocopyEvents.ProcessExited.Name);
            Assert.Equal(result.ExitCode, exited[0].RobocopyExitCode);
            Assert.Equal(DiagnosticOutcome.Succeeded, exited[0].Outcome);
            Assert.NotNull(exited[0].DurationMs);
            Assert.Equal(DiagnosticPass.Bulk, exited[0].Pass);

            WaitFor(runtime, RobocopyEvents.PassCompleted.Name);

            // 文件行采样摘要：观察到 2 行，未触发采样上限。
            var output = WaitFor(runtime, RobocopyEvents.OutputObserved.Name);
            var sample = Assert.IsType<RbcFileSampleSummaryPayload>(output[0].Payload);
            Assert.True(sample.TotalObserved >= 2, $"应观察到至少 2 个文件行，实际 {sample.TotalObserved}");
            Assert.False(sample.SamplingActive);
        }
        finally
        {
            scope.Dispose();
            D2TestSupport.Shutdown(runtime);
            D2TestSupport.Dispose(runtime);
        }
    }

    [Fact]
    public async Task FailedPassIsReportedAsFailureWithExitCode()
    {
        // 源目录不存在 ⇒ robocopy 必然失败（典型 Exit=16）。
        var missing = Path.Combine(_root, "nope");
        var runtime = StartDeep(out var scope);
        try
        {
            var runner = new RobocopyRunner(Serilog.Core.Logger.None);
            var result = await runner.RunPassAsync(
                missing, Path.Combine(_root, "dst2"), new MigrationOptions(), new MigrationMatrix(),
                PassKind.Bulk, Path.Combine(_root, "robo2.log"), CancellationToken.None);

            Assert.False(result.Success);

            var exited = WaitFor(runtime, RobocopyEvents.ProcessExited.Name);
            Assert.Equal(result.ExitCode, exited[0].RobocopyExitCode);
            Assert.Equal(DiagnosticOutcome.Failed, exited[0].Outcome);
            Assert.True(exited[0].RobocopyExitCode >= 8, $"失败退出码应 >= 8，实际 {exited[0].RobocopyExitCode}");
        }
        finally
        {
            scope.Dispose();
            D2TestSupport.Shutdown(runtime);
            D2TestSupport.Dispose(runtime);
        }
    }

    [Fact]
    public async Task RunnerContextIsCarriedIntoSubprocessEvents()
    {
        var src = Path.Combine(_root, "src3");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "c.txt"), "c");

        var runtime = StartDeep(out var scope);
        try
        {
            var runner = new RobocopyRunner(Serilog.Core.Logger.None)
            {
                // 编排器就是这么注入的：对象/尝试/通道身份必须出现在子进程事件上。
                Diagnostics = CoreDiagnostics.ContextFor("TransferOrchestrator", "JOB-D3B")
                    .WithObject("obj-000042").WithAttempt(2).WithPass(DiagnosticPass.Bulk),
            };

            var result = await runner.RunPassAsync(src, Path.Combine(_root, "dst3"), new MigrationOptions(),
                new MigrationMatrix(), PassKind.Bulk, Path.Combine(_root, "robo3.log"), CancellationToken.None);
            Assert.True(result.Success);

            var started = WaitFor(runtime, RobocopyEvents.ProcessStarted.Name);
            Assert.Equal("JOB-D3B", started[0].JobId);
            Assert.Equal("obj-000042", started[0].ObjectId);
            Assert.Equal(2, started[0].Attempt);
            Assert.Equal(DiagnosticPass.Bulk, started[0].Pass);
        }
        finally
        {
            scope.Dispose();
            D2TestSupport.Shutdown(runtime);
            D2TestSupport.Dispose(runtime);
        }
    }

    /// <summary>编排器的接线是源码级契约（跑真实迁移需要完整 plan/JobContext，属于后续阶段的集成测试）。</summary>
    [Fact]
    public void OrchestratorWiringIsPresentAndUsesTypedPayloads()
    {
        var root = FindRepoRoot();
        var text = File.ReadAllText(Path.Combine(root, "src", "PCMig.Core", "Transfer", "TransferOrchestrator.cs"));

        foreach (var token in new[]
                 {
                     "TransferEvents.JobRunStarted", "TransferEvents.JobRunCompleted",
                     "TransferEvents.ObjectStarted", "TransferEvents.ObjectCompleted", "TransferEvents.ObjectInterrupted",
                     "TransferEvents.SpaceAbortRequested", "TransferEvents.Paused", "TransferEvents.StopObserved",
                     "new TrnObjectPayload(", "StateOwner.Core",
                     "_runner.Diagnostics = _ctx.Diagnostics.WithObject(obj.ObjectId).WithAttempt(attempt)",
                     "观察失败绝不影响传输",
                 })
        {
            Assert.Contains(token, text, StringComparison.Ordinal);
        }
    }

    /// <summary>★ Observer 边界 ★ 装了诊断跑真 robocopy，产物文件必须与不装时完全一致。</summary>
    [Fact]
    public async Task RobocopyOutputIsIdenticalWithAndWithoutDiagnostics()
    {
        var src = Path.Combine(_root, "srcEq");
        Directory.CreateDirectory(src);
        for (var i = 0; i < 5; i++) File.WriteAllText(Path.Combine(src, $"f{i}.txt"), "content-" + i);

        // OFF
        var offDst = Path.Combine(_root, "dstOff");
        var offRunner = new RobocopyRunner(Serilog.Core.Logger.None);
        var offResult = await offRunner.RunPassAsync(src, offDst, new MigrationOptions(), new MigrationMatrix(),
            PassKind.Bulk, Path.Combine(_root, "roboOff.log"), CancellationToken.None);

        // ON
        var runtime = StartDeep(out var scope);
        try
        {
            var onDst = Path.Combine(_root, "dstOn");
            var onRunner = new RobocopyRunner(Serilog.Core.Logger.None);
            var onResult = await onRunner.RunPassAsync(src, onDst, new MigrationOptions(), new MigrationMatrix(),
                PassKind.Bulk, Path.Combine(_root, "roboOn.log"), CancellationToken.None);

            Assert.Equal(offResult.ExitCode, onResult.ExitCode);
            Assert.Equal(offResult.Success, onResult.Success);

            var offFiles = Directory.GetFiles(offDst).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            var onFiles = Directory.GetFiles(onDst).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            Assert.Equal(offFiles.Length, onFiles.Length);
            for (var i = 0; i < offFiles.Length; i++)
            {
                Assert.Equal(Path.GetFileName(offFiles[i]), Path.GetFileName(onFiles[i]));
                Assert.Equal(File.ReadAllBytes(offFiles[i]), File.ReadAllBytes(onFiles[i]));
            }

            Assert.True(runtime.Health.EventsWritten > 0, "ON 时应当确实产生了诊断事件");
        }
        finally
        {
            scope.Dispose();
            D2TestSupport.Shutdown(runtime);
            D2TestSupport.Dispose(runtime);
        }
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