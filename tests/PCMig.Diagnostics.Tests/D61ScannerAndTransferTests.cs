using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PCMig.Core.Diagnostics;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Scan;
using PCMig.Diagnostics;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using PCMig.Diagnostics.Abstractions.Serialization;
using Serilog;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D6.1 §16（Core 插桩覆盖：**Scanner** 与 **Transfer 暂停/重试**）收口测试。
/// 用**真实 SourceScanner** 在真实临时目录上跑一次扫描，断言 FS 证据链与统计口径。
/// </summary>
[Collection(DiagnosticsAmbientCollection.Name)]
public sealed class D61ScannerAndTransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-fs-" + Guid.NewGuid().ToString("N")[..8]);

    public D61ScannerAndTransferTests()
    {
        Directory.CreateDirectory(_root);
        // 造一棵小树：2 个一级目录 × 少量文件（扫描只做真实枚举，不做任何额外访问）
        for (var d = 1; d <= 2; d++)
        {
            var dir = Path.Combine(_root, "src", $"data{d}");
            Directory.CreateDirectory(dir);
            for (var f = 0; f < 3; f++)
                File.WriteAllText(Path.Combine(dir, $"f{f}.bin"), new string('x', 100 * d));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* 句柄延迟释放 */ }
    }

    private static readonly ILogger Silent = new LoggerConfiguration().CreateLogger();

    [Fact]
    public async Task RealScannerEmitsStartAndCompletionEvidenceWithHonestCompleteness()
    {
        var runtime = DiagnosticRuntime.Start(new DiagnosticRuntimeOptions
        {
            StorageRoot = Path.Combine(_root, "diag"),
            InitialMode = CaptureMode.Operational,
            AppVersion = "d61-test",
            ShutdownBudgetMs = 5000,
        }, out _);

        JobDefinition job;
        using (CoreDiagnostics.Install(runtime))
        {
            var source = Path.Combine(_root, "src");
            job = new JobDefinition
            {
                JobId = "JOB-FS-1",
                SourceHost = "synthetic-host",
                TargetRoot = Path.Combine(_root, "dst"),
                Sources = new List<SourceSpec> { new() { Path = source, Kind = ObjectKind.DataVolume } },
                CreatedBy = "test",
            };

            var observed = await new SourceScanner(Silent).ScanAsync(job, MigrationMatrix.Load(null, Silent));

            // 业务侧照旧：确实枚举到了对象与文件（观察不改变扫描结果）。
            Assert.NotEmpty(observed.Objects);
            Assert.True(observed.TotalFiles >= 6);
            Assert.True(observed.TotalBytes > 0);
        }

        D2TestSupport.Shutdown(runtime);
        var payloads = ReadPayloads(runtime);

        var started = Assert.Single(payloads.OfType<FsScanPayload>(), p => p.Completeness == "Started");
        Assert.Equal("whole", started.Mode);

        var completed = Assert.Single(payloads.OfType<FsScanPayload>(), p => p.Completeness != "Started");
        Assert.Equal("Complete", completed.Completeness);          // 无不可访问位置 ⇒ 完整
        Assert.Equal(0, completed.Inaccessible);
        Assert.Equal(0, completed.IncompleteObjects);
        Assert.True(completed.Files >= 6 && completed.Bytes > 0);
        // 策略条数必须如实带上（回答"哪些规则影响了这次扫描"）。
        Assert.True(completed.ExcludedDirRules > 0 || completed.ExcludedFileRules > 0);

        D2TestSupport.Dispose(runtime);
    }

    /// <summary>Transfer 的暂停/重试观察必须挂在**既有**判定点上（源码级契约）。</summary>
    [Fact]
    public void TransferPauseAndRetryObservationsAreWiredAtExistingDecisionPoints()
    {
        var orchestrator = ReadRepo("src", "PCMig.Core", "Transfer", "TransferOrchestrator.cs");

        Assert.Contains("TransferEvents.RetryScheduled", orchestrator, StringComparison.Ordinal);
        Assert.Contains("TransferEvents.RetryStarted", orchestrator, StringComparison.Ordinal);
        Assert.Contains("TransferEvents.PauseObserved", orchestrator, StringComparison.Ordinal);
        Assert.Contains("TransferEvents.Resumed", orchestrator, StringComparison.Ordinal);

        // 既有决策点必须仍在（观察不得改变语义）。
        Assert.Contains("var backoff = TimeSpan.FromSeconds(10.0 * attempt);", orchestrator, StringComparison.Ordinal);
        Assert.Contains("state.Phase = JobPhase.Paused;", orchestrator, StringComparison.Ordinal);
        Assert.Contains("while (File.Exists(_ctx.PauseRequestPath))", orchestrator, StringComparison.Ordinal);

        // 暂停等待时长必须单独可见（暂停不计入速率分母，T01 口径）。
        Assert.Contains("waitedMs", ReadRepo("src", "PCMig.Diagnostics.Abstractions", "Payloads", "TrnPayloads.cs"), StringComparison.Ordinal);
    }

    private static List<IDiagnosticPayload> ReadPayloads(DiagnosticRuntime runtime)
    {
        var dir = Path.Combine(runtime.Store!.SessionDir, "events");
        var payloads = new List<IDiagnosticPayload>();
        foreach (var file in Directory.GetFiles(dir, "*.jsonl"))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                if (DiagnosticEventJson.TryParse(line, out var evt, out _) && evt?.Payload is not null)
                    payloads.Add(evt.Payload);
            }
        }
        return payloads;
    }

    private static string ReadRepo(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln")))
            {
                var path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
                Assert.True(File.Exists(path), "缺少文件：" + path);
                return File.ReadAllText(path);
            }
        }
        throw new InvalidOperationException("找不到仓库根");
    }
}