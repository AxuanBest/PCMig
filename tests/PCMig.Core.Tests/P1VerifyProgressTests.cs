using PCMig.Core.Jobs;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Verify;
using Serilog;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ P1-12 / 验收 R9 ★ 验证的**结构化真实进度**。
///
/// 现场问题：「验证无独立二级窗口与真实进度」——界面拿不到任何真实计数，只能转圈。
/// 本文件守住三件事：
///   ① 快照里的每个数字都必须来自**已经算完**的对象（累计和），不许估算；
///   ② 进度通道是只读投影：它出问题（甚至抛异常）绝不影响验证结论；
///   ③ 取消必须是真的取消（对象之间生效），且**不落**一份假的 verify-report.json。
/// </summary>
[Collection("A5-jobdir-isolation")]
public sealed class P1VerifyProgressTests : IDisposable
{
    private static readonly ILogger Log = new LoggerConfiguration().CreateLogger();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pcmig-p1verifyprogress-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousJobsRoot;

    public P1VerifyProgressTests()
    {
        _previousJobsRoot = Environment.GetEnvironmentVariable("PCMIG_JOBS");
        Environment.SetEnvironmentVariable("PCMIG_JOBS", Path.Combine(_root, "Jobs"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PCMIG_JOBS", _previousJobsRoot);
        try { Directory.Delete(_root, true); } catch { /* 清理失败不影响结论 */ }
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    private string NewDir(string name)
    {
        var p = Path.Combine(_root, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private static void WriteFile(string dir, string name, int bytes)
        => File.WriteAllBytes(Path.Combine(dir, name), new byte[bytes]);

    private JobContext NewJob(MigrationPlan plan)
    {
        var def = new JobDefinition
        {
            JobId = "JOB-P1-12-" + Guid.NewGuid().ToString("N")[..8],
            SourceHost = "SRC-PC",
            TargetRoot = NewDir("target-root"),
            Sources = { new SourceSpec { Path = @"\\SRC-PC\D", Kind = ObjectKind.DataVolume } },
            Options = new MigrationOptions(),
        };
        var ctx = new JobManager(Log).Create(def);
        ctx.SavePlan(plan);
        return ctx;
    }

    /// <summary>造两个目录对象：第 1 个两侧一致（OK），第 2 个目标缺文件（MISMATCH）。</summary>
    private (JobContext Ctx, MigrationPlan Plan) TwoObjectJob()
    {
        var srcA = NewDir("src-a");
        var tgtA = NewDir("tgt-a");
        WriteFile(srcA, "a1.txt", 100);
        WriteFile(srcA, "a2.txt", 200);
        WriteFile(tgtA, "a1.txt", 100);
        WriteFile(tgtA, "a2.txt", 200);

        var srcB = NewDir("src-b");
        var tgtB = NewDir("tgt-b");
        WriteFile(srcB, "b1.txt", 300);

        var plan = new MigrationPlan
        {
            JobId = "plan",
            TotalBytes = 600,
            Objects =
            {
                new PlannedObject { ObjectId = "object-000001", Kind = ObjectKind.DataVolume, SourcePath = srcA, TargetPath = tgtA, EstimatedBytes = 300, EstimatedFiles = 2 },
                new PlannedObject { ObjectId = "object-000002", Kind = ObjectKind.DataVolume, SourcePath = srcB, TargetPath = tgtB, EstimatedBytes = 300, EstimatedFiles = 1 },
            },
        };
        return (NewJob(plan), plan);
    }

    private static Task<VerifyReport> RunAsync(JobContext ctx, IProgress<string>? progress = null,
        CancellationToken ct = default, IProgress<VerifyProgressSnapshot>? detail = null)
        => new Verifier(ctx, new MigrationMatrix(), Log).RunAsync(VerifyLevel.L1_CountSize, progress, ct, detail);

    /// <summary>线程安全的快照收集器（引擎可能在后台线程投递）。</summary>
    private sealed class Collector : IProgress<VerifyProgressSnapshot>
    {
        private readonly object _gate = new();
        private readonly List<VerifyProgressSnapshot> _items = new();
        public void Report(VerifyProgressSnapshot value) { lock (_gate) _items.Add(value); }
        public IReadOnlyList<VerifyProgressSnapshot> Items { get { lock (_gate) return _items.ToArray(); } }
    }

    private sealed class ThrowingProgress : IProgress<VerifyProgressSnapshot>
    {
        public void Report(VerifyProgressSnapshot value) => throw new InvalidOperationException("界面端故意抛错");
    }

    // ══════════════════════════════════════════════════════════════════════
    // ① 只报已算完的真实计数
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task P1_12_每个对象起止各发一条快照且计数只来自已算完的对象()
    {
        var (ctx, plan) = TwoObjectJob();
        var collector = new Collector();

        var report = await RunAsync(ctx, detail: collector);
        var shots = collector.Items;

        Assert.Equal(plan.Objects.Count * 2, shots.Count); // 2 个对象 × (start + done)

        // 第一条 start：什么都还没核对完 ⇒ 累计数必须是 0（不许估算成"大概一半"）
        Assert.Equal("start", shots[0].Stage);
        Assert.Equal(1, shots[0].ObjectIndex);
        Assert.Equal(2, shots[0].ObjectCount);
        Assert.Equal("object-000001", shots[0].ObjectId);
        Assert.Equal(0, shots[0].FilesChecked);
        Assert.Equal(0, shots[0].BytesChecked);
        Assert.Equal(0, shots[0].DecidedObjects);
        // ★ 实测修正（2026-10-11）★ 百分比只按**已出结论**的对象算：start 快照一个都没出结论 ⇒ 0.0%
        //   旧实现用"当前是第几个对象" ⇒ 首条就是 50%（单对象计划甚至是 100%）＝ 伪造进度。
        Assert.Equal(0.0, shots[0].Percent);
        Assert.Equal(50.0, shots[1].Percent); // 第一个对象出结论 ⇒ 1/2 = 50.0%
        Assert.Contains("正在核对 object-000001", shots[0].Describe());

        // 第一条 done：真实累计量必须逐位等于报告里第 1 个对象自己的数
        var first = report.Objects[0];
        var doneFirst = shots[1];
        Assert.Equal("done", doneFirst.Stage);
        Assert.Equal(1, doneFirst.ObjectIndex);
        Assert.Equal(1, doneFirst.DecidedObjects);
        Assert.Equal(first.SourceFiles, doneFirst.FilesChecked);
        Assert.Equal(first.SourceBytes, doneFirst.BytesChecked);
        Assert.Equal(first.Status == "OK" ? 1 : 0, doneFirst.OkObjects);

        // 末条 done：收尾数与整份报告的自洽
        var last = shots[^1];
        Assert.Equal("done", last.Stage);
        Assert.Equal(plan.Objects.Count, last.ObjectIndex);
        Assert.Equal(plan.Objects.Count, last.DecidedObjects);
        Assert.Equal(100.0, last.Percent);
        Assert.Equal(report.Objects.Sum(o => o.SourceFiles), last.FilesChecked);
        Assert.Equal(report.Objects.Sum(o => o.SourceBytes), last.BytesChecked);
        Assert.Equal(report.Objects.Count(o => o.Status == "OK"), last.OkObjects);
        Assert.Equal(report.Objects.Count(o => o.Status == "MISMATCH"), last.MismatchObjects);
        Assert.Equal(report.Objects.Count(o => o.Status == "UNKNOWN"), last.UnknownObjects);
        Assert.Equal(report.Objects.Count(o => o.Status == "EMPTY"), last.EmptyObjects);
        Assert.Contains($"已完成 {plan.Objects.Count}/{plan.Objects.Count} 个对象", last.Describe());

        // 单调性：任何一条快照都不许让累计数倒退
        long files = 0, bytes = 0;
        var decided = 0;
        foreach (var s in shots)
        {
            Assert.True(s.FilesChecked >= files, $"FilesChecked 倒退：{s.FilesChecked} < {files}");
            Assert.True(s.BytesChecked >= bytes, $"BytesChecked 倒退：{s.BytesChecked} < {bytes}");
            Assert.True(s.DecidedObjects >= decided, $"已出结论数倒退：{s.DecidedObjects} < {decided}");
            files = s.FilesChecked;
            bytes = s.BytesChecked;
            decided = s.DecidedObjects;
        }

        // 现场对象（源有数据、目标缺）必须是真实 MISMATCH，且这条结论独立于进度通道
        Assert.Equal("MISMATCH", report.Objects[1].Status);
    }

    [Fact]
    public void P1_12_快照类型不含任何估算字段()
    {
        var names = typeof(VerifyProgressSnapshot).GetProperties().Select(p => p.Name).ToArray();
        foreach (var name in names)
        {
            Assert.DoesNotContain("Estimate", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Eta", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Remaining", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Forecast", name, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("FilesChecked", names);
        Assert.Contains("BytesChecked", names);

        // 对象数为 0 时不得出现除零或"假装 100%"
        var zero = new VerifyProgressSnapshot("JOB", 0, 0, "", null, "start", 0, 0, 0, 0, 0, 0, 0, DateTime.UtcNow);
        Assert.Equal(0.0, zero.Percent);
        Assert.Equal(0, zero.DecidedObjects);
    }

    [Fact]
    public void P1_12_单对象计划在出结论之前绝不能显示百分之百()
    {
        // 真实 UI 证据暴露的缺陷（2026-10-11）：计划只有 1 个对象时，start 快照按"当前是第 1 个对象"
        // 算出 100.0%，用户在一个文件都还没核对时就看到 100%（取消时也挂着这个 100%）。
        var start = new VerifyProgressSnapshot("JOB", 1, 1, "object-000001", @"\\SRC-PC\D", "start", 0, 0, 0, 0, 0, 0, 0, DateTime.UtcNow);
        Assert.Equal(0, start.DecidedObjects);
        Assert.Equal(0.0, start.Percent);

        // 唯一那个对象真出结论后才允许到 100%
        var done = start with { Stage = "done", FilesChecked = 14, BytesChecked = 4096, OkObjects = 1 };
        Assert.Equal(1, done.DecidedObjects);
        Assert.Equal(100.0, done.Percent);

        // 两个对象时：start 0% → 第一个出结论 50% → 收尾 100%
        var s1 = new VerifyProgressSnapshot("JOB", 1, 2, "object-000001", null, "start", 0, 0, 0, 0, 0, 0, 0, DateTime.UtcNow);
        var d1 = s1 with { Stage = "done", MismatchObjects = 1 };
        var s2 = d1 with { Stage = "start", ObjectIndex = 2, ObjectId = "object-000002" };
        var d2 = s2 with { Stage = "done", MismatchObjects = 1, OkObjects = 1 };
        Assert.Equal(new[] { 0.0, 50.0, 50.0, 100.0 }, new[] { s1.Percent, d1.Percent, s2.Percent, d2.Percent });
    }

    // ══════════════════════════════════════════════════════════════════════
    // ② 只读投影：进度通道出问题不能影响验证本身
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task P1_12_进度投递抛异常时验证照常出结论并落盘()
    {
        var (ctx, _) = TwoObjectJob();

        var report = await RunAsync(ctx, detail: new ThrowingProgress());

        Assert.Equal(2, report.Objects.Count);
        Assert.Equal("OK", report.Objects[0].Status);
        Assert.Equal("MISMATCH", report.Objects[1].Status);
        Assert.False(report.OverallPass); // 有 MISMATCH ⇒ 总体不通过（判定与进度无关）
        Assert.True(File.Exists(Path.Combine(ctx.JobDir, "verify-report.json")),
            "进度通道抛异常不得中断验证的落盘");
    }

    [Fact]
    public async Task P1_12_带快照与不带快照的判定结果逐位一致()
    {
        var (ctxA, _) = TwoObjectJob();
        var withoutDetail = await RunAsync(ctxA);

        var (ctxB, _) = TwoObjectJob();
        var withDetail = await RunAsync(ctxB, detail: new Collector());

        Assert.Equal(withoutDetail.OverallPass, withDetail.OverallPass);
        Assert.Equal(withoutDetail.Objects.Count, withDetail.Objects.Count);
        for (var i = 0; i < withDetail.Objects.Count; i++)
        {
            Assert.Equal(withoutDetail.Objects[i].Status, withDetail.Objects[i].Status);
            Assert.Equal(withoutDetail.Objects[i].SourceFiles, withDetail.Objects[i].SourceFiles);
            Assert.Equal(withoutDetail.Objects[i].TargetFiles, withDetail.Objects[i].TargetFiles);
            Assert.Equal(withoutDetail.Objects[i].AllowsDestructiveRepair, withDetail.Objects[i].AllowsDestructiveRepair);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // ③ 取消是真的取消（R9「取消有效」的引擎侧契约）
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task P1_12_取消后抛出取消且不落假的验证报告()
    {
        var (ctx, _) = TwoObjectJob();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var collector = new Collector();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunAsync(ctx, ct: cts.Token, detail: collector));

        Assert.False(File.Exists(Path.Combine(ctx.JobDir, "verify-report.json")),
            "被取消的验证绝不能留下报告：「已核对完」是不能伪造的结论");
        Assert.Empty(collector.Items); // 一个对象都没开始 ⇒ 一条 done 都没有
    }

    [Fact]
    public async Task P1_12_取消发生在对象之间时已出的结论不会被写成通过()
    {
        var (ctx, _) = TwoObjectJob();
        using var cts = new CancellationTokenSource();
        var collector = new Collector();
        // 第一条 start 快照一到就同步取消 ⇒ 第 2 个对象开始前必须退出
        var cancelOnFirst = new CancelOnFirstStart(collector, cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunAsync(ctx, ct: cts.Token, detail: cancelOnFirst));

        Assert.NotEmpty(collector.Items);
        Assert.False(File.Exists(Path.Combine(ctx.JobDir, "verify-report.json")));
    }

    /// <summary>收到第一条 start 快照就同步取消（不用 <c>Progress&lt;T&gt;</c>：它会把回调排队，取消会晚到）。</summary>
    private sealed class CancelOnFirstStart : IProgress<VerifyProgressSnapshot>
    {
        private readonly Collector _sink;
        private readonly CancellationTokenSource _cts;
        private int _fired;
        public CancelOnFirstStart(Collector sink, CancellationTokenSource cts) { _sink = sink; _cts = cts; }

        public void Report(VerifyProgressSnapshot value)
        {
            _sink.Report(value);
            if (value.Stage == "start" && Interlocked.Exchange(ref _fired, 1) == 0) _cts.Cancel();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // ④ 引擎接线契约（文本级：改坏了必须红）
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void P1_12_引擎接线必须把结构化快照接到对象循环上()
    {
        var source = Read("src", "PCMig.Core", "Verify", "Verifier.cs");

        Assert.Contains("IProgress<VerifyProgressSnapshot>? detail = null", source);
        Assert.Contains("detail.Report(new VerifyProgressSnapshot(", source);
        Assert.Contains("_log.Debug(ex, \"验证进度快照投递失败（不影响验证结论）\");", source);
        Assert.Contains("ReportDetail(obj.ObjectId, obj.SourcePath, \"start\", seenObjects);", source);
        Assert.Contains("ReportDetail(obj.ObjectId, obj.SourcePath, \"done\", report.Objects.Count);", source);
        // 旧的一行文本进度不能因为新增通道被删（控制台与既有界面还在读它）
        Assert.Contains("progress?.Report($\"验证 {obj.ObjectId} …\");", source);
        // 取消点仍然在对象之间
        Assert.Contains("ct.ThrowIfCancellationRequested();", source);
    }

    private static string Read(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
    }
}