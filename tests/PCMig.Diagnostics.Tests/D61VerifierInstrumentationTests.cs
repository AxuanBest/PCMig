using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PCMig.Core.Diagnostics;
using PCMig.Core.Jobs;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Verify;
using PCMig.Diagnostics;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using PCMig.Diagnostics.Abstractions.Serialization;
using Serilog;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D6.1 §16（Core 插桩覆盖：**Verifier**）收口测试。
///
/// 用**真实 Verifier** 在真实临时目录上跑一次验证，然后断言它产出了应有的 VRF 证据：
///   VerifyStarted / PlanValidated / 两侧 Stat / SampleSelected / HashStarted / HashSucceeded /
///   Mismatch / StatsCompletenessObserved / Completed。
/// 同时验证**Observer 边界**：诊断开关对 OverallPass 与报告本身没有任何影响。
/// </summary>
[Collection(DiagnosticsAmbientCollection.Name)]
public sealed class D61VerifierInstrumentationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-vrf-" + Guid.NewGuid().ToString("N")[..8]);

    public D61VerifierInstrumentationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* 句柄延迟释放 */ }
    }

    private static readonly ILogger Silent = new LoggerConfiguration().CreateLogger();

    /// <summary>建一个 JobContext（Job 目录落在临时目录，**绝不碰** ProgramData 里的真实任务）。</summary>
    private JobContext NewContext(string tag, string source, string target, int samplePercent)
    {
        var jobDir = Path.Combine(_root, tag);
        var definition = new JobDefinition
        {
            JobId = "JOB-D61-" + tag,
            SourceHost = "synthetic-host",
            TargetRoot = target,
            Sources = new List<SourceSpec> { new() { Path = source, Kind = ObjectKind.DataVolume } },
            CreatedBy = "test",
        };
        definition.Options.SampleHashPercent = samplePercent;

        var ctx = new JobContext { JobDir = jobDir, Definition = definition };
        ctx.EnsureDirs();
        // JobId 是只读属性（由 Definition 派生）⇒ 只读它，不赋值。
        ctx.Diagnostics = CoreDiagnostics.ContextFor("test", ctx.JobId);
        ctx.Plan = new MigrationPlan
        {
            Objects = new List<PlannedObject>
            {
                new() { ObjectId = "obj-1", SourcePath = source, TargetPath = target, Kind = ObjectKind.DataVolume },
            },
        };
        return ctx;
    }

    private static (string Src, string Dst) MakeTrees(string root, int files, bool dropOneOnTarget, bool corruptOne)
    {
        var src = Path.Combine(root, "src");
        var dst = Path.Combine(root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);

        for (var i = 0; i < files; i++)
        {
            var name = $"f{i:000}.bin";
            File.WriteAllText(Path.Combine(src, name), "payload-" + i);
            if (dropOneOnTarget && i == 0) continue;                       // 目标少一个 ⇒ missing
            File.WriteAllText(Path.Combine(dst, name),
                corruptOne && i == 1 ? "CORRUPTED" : "payload-" + i);     // 内容不同 ⇒ hash mismatch
        }

        return (src, dst);
    }

    [Fact]
    public async Task RealVerifierEmitsTheWholeVrfEvidenceChain()
    {
        var (src, dst) = MakeTrees(_root, files: 40, dropOneOnTarget: false, corruptOne: false);
        var ctx = NewContext("job", src, dst, samplePercent: 100);

        // ★ 用 **Deep** 模式跑：逐对象/逐文件的细节事件（两侧 Stat、抽样、Hash）是 Verbose 类，
        //   按设计只在 Deep 下采集（Operational 只保留"异常与结论"）。
        var runtime = DiagnosticRuntime.Start(new DiagnosticRuntimeOptions
        {
            StorageRoot = Path.Combine(_root, "diag"),
            InitialMode = CaptureMode.Deep,
            AppVersion = "d61-test",
            AnalyzerMaxPending = 4096,
            ShutdownBudgetMs = 5000,
        }, out _);
        Assert.NotNull(runtime.Store);

        using (CoreDiagnostics.Install(runtime))
        {
            var verifier = new Verifier(ctx, MigrationMatrix.Load(null, Silent), Silent);
            var report = await verifier.RunAsync(VerifyLevel.L2_SampleHash);

            Assert.NotNull(report);
            Assert.True(report.OverallPass, "两侧内容一致时应判定通过（本断言同时证明观察没有改变业务结果）");
        }

        D2TestSupport.Shutdown(runtime);
        var codes = ReadEventNames(runtime);

        foreach (var expected in new[]
                 {
                     VerifyEvents.VerifyStarted, VerifyEvents.PlanValidated,
                     VerifyEvents.TargetStatStarted, VerifyEvents.TargetStatSucceeded,
                     VerifyEvents.SourceStatStarted, VerifyEvents.SourceStatSucceeded,
                     VerifyEvents.SampleSelected, VerifyEvents.HashStarted, VerifyEvents.HashSucceeded,
                     VerifyEvents.StatsCompletenessObserved, VerifyEvents.Completed,
                 })
        {
            Assert.True(codes.Contains(expected.Name), $"验证器没有产出 {expected.Name}（实际：{string.Join(",", codes.Distinct())}）");
        }

        // 无异常场景下不应出现 mismatch/failure 类证据（否则是误报）。
        Assert.DoesNotContain(VerifyEvents.Mismatch.Name, codes);
        Assert.DoesNotContain(VerifyEvents.HashFailed.Name, codes);

        // 统计完整性：本次枚举无跳过、无流式降级 ⇒ Complete。
        var completeness = Assert.Single(ReadPayloads(runtime).OfType<VrfStatsCompletenessPayload>());
        Assert.Equal("Complete", completeness.Status);

        D2TestSupport.Dispose(runtime);
    }

    /// <summary>
    /// Operational（默认）模式下的证据链：**异常与结论**必须可见，逐对象细节按设计不采集。
    /// </summary>
    [Fact]
    public async Task OperationalModeKeepsTheConclusionEvidenceWithoutPerObjectDetail()
    {
        var (src, dst) = MakeTrees(_root, files: 20, dropOneOnTarget: false, corruptOne: false);
        var ctx = NewContext("job-op", src, dst, samplePercent: 50);

        var runtime = DiagnosticRuntime.Start(new DiagnosticRuntimeOptions
        {
            StorageRoot = Path.Combine(_root, "diag-op"),
            InitialMode = CaptureMode.Operational,
            AppVersion = "d61-test",
            AnalyzerMaxPending = 4096,
            ShutdownBudgetMs = 5000,
        }, out _);

        using (CoreDiagnostics.Install(runtime))
        {
            var verifier = new Verifier(ctx, MigrationMatrix.Load(null, Silent), Silent);
            await verifier.RunAsync(VerifyLevel.L2_SampleHash);
        }

        D2TestSupport.Shutdown(runtime);
        var codes = ReadEventNames(runtime);

        Assert.Contains(VerifyEvents.VerifyStarted.Name, codes);
        Assert.Contains(VerifyEvents.PlanValidated.Name, codes);
        Assert.Contains(VerifyEvents.Completed.Name, codes);
        Assert.Contains(VerifyEvents.StatsCompletenessObserved.Name, codes);
        // 逐对象细节是 Verbose ⇒ 默认模式**不**采集（这是有界性设计，不是缺陷）。
        Assert.DoesNotContain(VerifyEvents.TargetStatStarted.Name, codes);
        Assert.DoesNotContain(VerifyEvents.HashStarted.Name, codes);

        D2TestSupport.Dispose(runtime);
    }

    [Fact]
    public async Task MissingFileAndContentMismatchAreBothReportedAsEvidence()
    {
        var (src, dst) = MakeTrees(_root, files: 40, dropOneOnTarget: true, corruptOne: true);
        var ctx = NewContext("job2", src, dst, samplePercent: 100);

        var runtime = DiagnosticRuntime.Start(new DiagnosticRuntimeOptions
        {
            StorageRoot = Path.Combine(_root, "diag2"),
            InitialMode = CaptureMode.Operational,
            AppVersion = "d61-test",
            AnalyzerMaxPending = 4096,
            ShutdownBudgetMs = 5000,
        }, out _);
        Assert.NotNull(runtime.Store);

        VerifyReport report;
        using (CoreDiagnostics.Install(runtime))
        {
            var verifier = new Verifier(ctx, MigrationMatrix.Load(null, Silent), Silent);
            report = await verifier.RunAsync(VerifyLevel.L2_SampleHash);
        }

        D2TestSupport.Shutdown(runtime);
        var payloads = ReadPayloads(runtime);
        var codes = ReadEventNames(runtime);

        // 业务侧照旧：目标少一个文件 ⇒ 报告不通过（观察不改变结论）。
        Assert.False(report.OverallPass);

        // 诊断侧必须把两类问题**分开**说清（missing 与 hash-mismatch 不是一回事）。
        var mismatchKinds = payloads.OfType<VrfMismatchPayload>().Select(p => p.Kind).ToList();
        Assert.True(mismatchKinds.Contains("missing-on-target"),
            $"缺少 missing-on-target 证据；实际 codes=[{string.Join(",", codes.Distinct())}] " +
            $"payloads=[{string.Join(",", payloads.Select(p => p.PayloadName).Distinct())}]");
        Assert.Contains("hash-mismatch", mismatchKinds);

        var completed = Assert.Single(payloads.OfType<VrfCompletedPayload>());
        Assert.False(completed.OverallPass);
        Assert.True(completed.MissingTotal > 0);
        Assert.True(completed.HashMismatched > 0);

        D2TestSupport.Dispose(runtime);
    }

    // ────────────────────────── helpers ──────────────────────────

    private static List<string> ReadEventNames(DiagnosticRuntime runtime)
        => ReadEvents(runtime).Select(e => e.Descriptor.Name).ToList();

    private static List<DiagnosticEvent> ReadEvents(DiagnosticRuntime runtime)
    {
        var dir = Path.Combine(runtime.Store!.SessionDir, "events");
        var events = new List<DiagnosticEvent>();
        foreach (var file in Directory.GetFiles(dir, "*.jsonl"))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                if (DiagnosticEventJson.TryParse(line, out var evt, out _) && evt is not null) events.Add(evt);
            }
        }
        return events;
    }

    private static List<IDiagnosticPayload> ReadPayloads(DiagnosticRuntime runtime)
        => ReadEvents(runtime).Where(e => e.Payload is not null).Select(e => e.Payload!).ToList();
}