using PCMig.Core.Jobs;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Planning;
using PCMig.Core.Report;
using PCMig.Core.Scan;
using PCMig.Core.State;
using PCMig.Core.Transfer;
using PCMig.Core.Util;
using Serilog;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// v0.5.4-preview.3「不预检开始迁移」的**运行期**回归（不是源码文本断言）。
///
/// 这些用例回答的是任务令里最关键的三个问题：
///   ① 跳过预检后，任务存档里到底还有没有"预检通过"的假证据？（不得有：preflight.json 不存在）
///   ② 跳过预检还能不能真把文件搬过去、回执与可信完成判据是否照常？（能：真实 robocopy）
///   ③ 复制失败时会不会因为"没预检"就被算成完成？（不会：IsTrustworthyCompletion 恒 false）
///
/// 覆盖边界（如实写下来，不假装覆盖更多）：
///   · 本文件测的是 **Core 执行链**（JobManager → SourceScanner → ScanGate → Planner →
///     TransferOrchestrator → ReportGenerator），也就是"不预检"与"预检"两条路真正分叉之后
///     共用的那一段；
///   · WinUI 页面层（Step2 三个按钮的顺序、二次确认对话框、连点防重入）需要交互桌面，
///     不在本文件覆盖范围内，由真机 UIA 验收覆盖。
/// </summary>
[Collection("A5-jobdir-isolation")]
public sealed class Preview3SkipPreflightTests : IDisposable
{
    private static readonly ILogger Log = new LoggerConfiguration().CreateLogger();

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pcmig-skippreflight-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string? _previousJobsRoot;

    public Preview3SkipPreflightTests()
    {
        _previousJobsRoot = Environment.GetEnvironmentVariable("PCMIG_JOBS");
        Environment.SetEnvironmentVariable("PCMIG_JOBS", Path.Combine(_root, "Jobs"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PCMIG_JOBS", _previousJobsRoot);
        try { Directory.Delete(_root, true); } catch { /* 句柄延迟释放不影响结论 */ }
    }

    // ══════════════════════════════════════════════════════════════════════
    // ① 存档事实：跳过预检 ⇒ 没有 preflight.json，但 job.json 记得这件事
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void 跳过预检的任务存档不得含预检记录且job_json留下审计事实()
    {
        var (src, dst) = MakeSourceAndTarget();
        var def = NewDefinition(src, dst, preflightSkipped: true);

        var ctx = new JobManager(Log).Create(def);

        Assert.False(File.Exists(ctx.PreflightPath),
            "跳过预检就不得写出 preflight.json —— 该文件缺失是『未执行预检』唯一可信的信号；" +
            "凭空写一份等于把『没做』伪造成『做了』。");

        var jobJson = Path.Combine(ctx.JobDir, "job.json");
        Assert.True(File.Exists(jobJson), "job.json 必须存在（任务定义是要落盘的）");
        Assert.True(JsonStateStore.TryRead<JobDefinition>(jobJson, out var back), "job.json 必须可读回");
        Assert.NotNull(back);
        Assert.True(back!.PreflightSkipped, "审计事实必须落盘：事后能看出这条任务当时没跑预检");
    }

    /// <summary>对照组：正常（跑了预检）的任务存档里**有** preflight.json —— 证明上面的断言判别得出真假。</summary>
    [Fact]
    public void 对照_跑了预检的任务存档里有preflight_json()
    {
        var (src, dst) = MakeSourceAndTarget();
        var def = NewDefinition(src, dst, preflightSkipped: false);
        var ctx = new JobManager(Log).Create(def);

        Assert.False(File.Exists(ctx.PreflightPath), "刚创建、还没跑预检时同样不该有 preflight.json");
        Assert.False(ctx.Definition.PreflightSkipped, "默认不是『已跳过』");

        ctx.SavePreflight(new PreflightReport { OverallPass = true });
        Assert.True(File.Exists(ctx.PreflightPath), "PreflightChecker 跑完后才写入 preflight.json");
    }

    // ══════════════════════════════════════════════════════════════════════
    // ② 真实端到端：跳过预检 → 扫描 → 计划 → 真实 robocopy → 回执/可信完成
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task 跳过预检后真实扫描计划与复制全链路必须完成且可信()
    {
        var (src, dst) = MakeSourceAndTarget();
        var def = NewDefinition(src, dst, preflightSkipped: true);
        var matrix = MigrationMatrix.Load(null, Log);

        var ctx = new JobManager(Log).Create(def);
        var observed = await new SourceScanner(Log).ScanAsync(def, matrix);
        ctx.SaveObserved(observed);
        Assert.False(observed.ScanIncomplete, "本地临时目录必须扫描完整（残缺会被下面的闸门拦住）");

        // 扫描残缺闸门：跳过预检**不跳过**它。
        var gate = ScanGate.Inspect(observed, def.AllowIncompleteScan);
        Assert.False(gate.Blocks, "扫描完整时闸门不得拦截");

        var plan = new Planner(Log).CreatePlan(def, observed, matrix);
        ctx.SavePlan(plan);
        Assert.NotEmpty(plan.Objects);

        var st = ctx.LoadStateOrNew();
        st.Phase = JobPhase.AwaitingReview;
        st.TotalObjects = plan.Objects.Count;
        st.TotalBytes = plan.TotalBytes;
        ctx.SaveState(st);

        await new TransferOrchestrator(ctx, matrix, Log).RunAsync();

        // 真实文件必须落地（含中文名、子目录、空目录）。
        var landed = Directory.GetFiles(dst, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("根散落 中文.txt", landed);
        Assert.Contains("深层文件.bin", landed);
        Assert.Contains(Directory.GetDirectories(dst, "*", SearchOption.AllDirectories),
            d => Path.GetFileName(d) == "空目录");

        var final = ctx.LoadStateOrNew();
        Assert.True(TransferOrchestrator.IsTrustworthyCompletion(final),
            $"真实复制成功后应当达到可信完成：phase={final.Phase} " +
            $"completed={final.CompletedObjects}/{final.TotalObjects} failed={final.FailedObjects} " +
            $"missing={final.MissingObjects} bytes={final.CompletedBytes}/{final.TotalBytes}");

        // 回执与报告照常产出（不预检 = 没跳过证据链）。
        var receipts = Directory.Exists(ctx.ReceiptsDir) ? Directory.GetFiles(ctx.ReceiptsDir) : [];
        Assert.NotEmpty(receipts);
    }

    // ══════════════════════════════════════════════════════════════════════
    // ③ 复制失败不得伪装成完成（跳过预检 ≠ 放宽完成判据）
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task 跳过预检时复制失败也不得算作可信完成()
    {
        var (_, dst) = MakeSourceAndTarget();
        var def = NewDefinition(Path.Combine(_root, "no-such-source"), dst, preflightSkipped: true);
        // 源换成一个不存在的主机：robocopy 会立即失败（这是 P2TransferTruthTests 已验证过的手法）。
        def.Sources.Clear();
        def.Sources.Add(new SourceSpec { Path = @"\\pcmig-no-such-host-xyz\D", Kind = ObjectKind.DataVolume });
        // 不重试：不可达主机必须尽快收口，避免测试等 10 秒以上。
        def.Options.RetryCount = 0;
        def.Options.RetryWaitSec = 0;

        var ctx = new JobManager(Log).Create(def);
        var plan = new MigrationPlan
        {
            JobId = def.JobId,
            TotalBytes = 1,
            Objects =
            {
                new PlannedObject
                {
                    ObjectId = "object-000001",
                    Kind = ObjectKind.DataVolume,
                    SourcePath = @"\\pcmig-no-such-host-xyz\D",
                    TargetPath = Path.Combine(dst, "object-000001"),
                    EstimatedFiles = 1,
                    EstimatedBytes = 1,
                },
            },
        };
        ctx.SavePlan(plan);

        var st = ctx.LoadStateOrNew();
        st.Phase = JobPhase.AwaitingReview;
        st.TotalObjects = 1;
        st.TotalBytes = 1;
        ctx.SaveState(st);

        await new TransferOrchestrator(ctx, new MigrationMatrix(), Log).RunAsync();

        var final = ctx.LoadStateOrNew();
        Assert.False(TransferOrchestrator.IsTrustworthyCompletion(final),
            "源不可达 + 跳过预检，绝不能因为『字节进度走完』就被判成可信完成");
        Assert.True(final.FailedObjects > 0 || final.MissingObjects > 0,
            $"失败/缺失必须被真实记账：failed={final.FailedObjects} missing={final.MissingObjects}");
        Assert.NotEqual(JobPhase.Completed, final.Phase);
    }

    // ══════════════════════════════════════════════════════════════════════
    // ④ 报告：必须明说"未执行预检"，绝不出现"预检通过"
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void 跳过预检的报告明说未执行预检且不含预检检查记录附录()
    {
        var (src, dst) = MakeSourceAndTarget();
        var def = NewDefinition(src, dst, preflightSkipped: true);
        var ctx = new JobManager(Log).Create(def);
        ctx.SavePlan(new MigrationPlan { JobId = def.JobId, TotalBytes = 0 });
        var st = ctx.LoadStateOrNew();
        st.Phase = JobPhase.Completed;
        st.TotalObjects = 0;
        ctx.SaveState(st);

        var path = new ReportGenerator(ctx, Log).Generate();
        var html = File.ReadAllText(path);

        Assert.Contains("本次任务未执行预检", html);
        Assert.Contains("没有运行", html);
        Assert.DoesNotContain("Preflight 检查记录", html);
        // 报告里不得出现"预检通过"这种把没做说成做了的表述。
        Assert.DoesNotContain("预检通过", html);
    }

    // ══════════════════════════════════════════════════════════════════════
    // ⑤ 扫描残缺闸门本身不因"跳过预检"失效
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void 扫描残缺闸门在跳过预检路径上仍然拦截()
    {
        var observed = new ObservedState { JobId = "JOB-X" };
        observed.InaccessiblePaths.Add(@"object-000001｜\\HOST\D$\私有｜拒绝访问");

        var unacknowledged = ScanGate.Inspect(observed, acknowledged: false);
        Assert.True(unacknowledged.HasIncomplete);
        Assert.True(unacknowledged.Blocks, "未确认的残缺必须拦住，跳过预检不改变这条");

        var acknowledged = ScanGate.Inspect(observed, acknowledged: true);
        Assert.True(acknowledged.HasIncomplete);
        Assert.False(acknowledged.Blocks, "用户显式确认后才放行（结论落在 job.json 供续传沿用）");
    }

    // ══════════════════════════════════════════════════════════════════════
    // 辅助
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>造一棵"真实形状"的源树：中文文件名、子目录、空目录、二进制文件。</summary>
    private (string Source, string Target) MakeSourceAndTarget()
    {
        var src = Path.Combine(_root, "src-" + Guid.NewGuid().ToString("N")[..6]);
        var dst = Path.Combine(_root, "dst-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(src, "子目录"));
        Directory.CreateDirectory(Path.Combine(src, "子目录", "更深一层"));
        Directory.CreateDirectory(Path.Combine(src, "空目录"));
        Directory.CreateDirectory(dst);

        File.WriteAllText(Path.Combine(src, "根散落 中文.txt"), "根层文件");
        File.WriteAllBytes(Path.Combine(src, "子目录", "更深一层", "深层文件.bin"), new byte[] { 1, 2, 3, 4, 5 });
        return (src, dst);
    }

    private JobDefinition NewDefinition(string source, string target, bool preflightSkipped) => new()
    {
        JobId = "JOB-P3SP-" + Guid.NewGuid().ToString("N")[..8],
        SourceHost = "LOCAL-TEST",
        TargetRoot = target,
        Sources = { new SourceSpec { Path = source, Kind = ObjectKind.DataVolume } },
        Options = new MigrationOptions(),
        CreatedBy = "Preview3SkipPreflightTests",
        PreflightSkipped = preflightSkipped,
    };
}
