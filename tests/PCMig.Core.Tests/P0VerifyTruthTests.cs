using PCMig.Core.Jobs;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Util;
using PCMig.Core.Verify;
using Serilog;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ P0-01 / P0-02 / P0-01(4) ★ 验证「真假」的回归测试。
///
/// 现场背景（原始日志实测）：
///   · Preview object-000018：源 14 文件 / 目标 0 文件（27.44 GB），报「缺失」→ 强制修复删掉目标侧 14 个文件 → 复核仍「缺失」；
///   · Stable  object-000004：源 1 文件 / 目标 0 文件（869 B），同样被删；
///   · 同一批日志里还有大量「两侧 0/0」被写成 OK（10/12、多文件等）。
/// 三种完全不同的状态被压成同一个词，修复动作据此删了真实数据。这些测试把它们分开守住。
/// </summary>
[Collection("A5-jobdir-isolation")]
public sealed class P0VerifyTruthTests : IDisposable
{
    private static readonly ILogger Log = new LoggerConfiguration().CreateLogger();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pcmig-p0verify-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousJobsRoot;

    public P0VerifyTruthTests()
    {
        // ★ 作业目录隔离（与 A5 / PU / Step4 / StopChain / P0SourceLamp 同 collection ⇒ 串行）★
        //   本类用 JobManager.Create 造任务、再落 plan/state；不隔离就会写进真实的
        //   %ProgramData%\PCMig\Jobs（污染真实存档），且当别的测试类临时改写**进程级**
        //   环境变量 PCMIG_JOBS 时，Create 与后续 SavePlan/SaveState 可能落在不同目录上。
        _previousJobsRoot = Environment.GetEnvironmentVariable("PCMIG_JOBS");
        Environment.SetEnvironmentVariable("PCMIG_JOBS", Path.Combine(_root, "Jobs"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PCMIG_JOBS", _previousJobsRoot);
        try { Directory.Delete(_root, true); } catch { /* 清理失败不影响结论 */ }
    }

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
            JobId = "JOB-P0-" + Guid.NewGuid().ToString("N")[..8],
            SourceHost = "SRC-PC",
            TargetRoot = NewDir("target-root"),
            Sources = { new SourceSpec { Path = @"\\SRC-PC\D", Kind = ObjectKind.DataVolume } },
            Options = new MigrationOptions(),
        };
        var ctx = new JobManager(Log).Create(def);
        ctx.SavePlan(plan);
        return ctx;
    }

    private static Task<VerifyReport> VerifyAsync(JobContext ctx)
        => new Verifier(ctx, new MigrationMatrix(), Log).RunAsync(VerifyLevel.L1_CountSize);

    private static MigrationPlan Plan(PlannedObject obj) => new()
    {
        JobId = "plan",
        TotalBytes = obj.EstimatedBytes,
        Objects = { obj },
    };

    // ══════════════════════════════════════════════════════════════════════
    // P0-01 根因：卷根 D:\ 被 TrimEnd('\\') 裁成 D:，与 Path.GetDirectoryName 永不相等
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>旧表达式原样保留在测试里：它对新实现必须给出相反结论（RED→GREEN 的自证）。</summary>
    private static bool LegacyIsInRoot(string root, string fullPath)
        => string.Equals(Path.GetDirectoryName(fullPath), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task P0_01_卷根归属_旧表达式漏判而新归一化判对()
    {
        const string volumeRoot = @"D:\";
        const string rootLevelFile = @"D:\bookmarks_2022_1_9.html";

        // RED 证据：这正是 object-000018「目标 0 个文件」的成因
        Assert.False(LegacyIsInRoot(volumeRoot, rootLevelFile),
            "旧表达式若在此返回 true，说明 P0-01 的根因描述需要重新核对");

        // GREEN：卷根、尾随分隔符、UNC 根、大小写都必须给出同一结论
        Assert.True(PathNorm.IsRootLevelFile(rootLevelFile, volumeRoot));
        Assert.True(PathNorm.IsRootLevelFile(@"D:\Outlook data.pst", "D:"));
        Assert.True(PathNorm.IsRootLevelFile(@"d:\BOOKMARKS_2022_1_9.HTML", @"D:\"));
        Assert.True(PathNorm.IsRootLevelFile(@"\\SRC-PC\D\Outlook data.pst", @"\\SRC-PC\D\"));
        Assert.True(PathNorm.IsRootLevelFile(@"\\SRC-PC\D\a.txt", @"\\SRC-PC\D"));

        // 深层文件绝不能算根层（宁可漏判，不可误判）
        Assert.False(PathNorm.IsRootLevelFile(@"D:\Users\D970\a.txt", volumeRoot));
        Assert.False(PathNorm.IsRootLevelFile(@"\\SRC-PC\D\Users\a.txt", @"\\SRC-PC\D"));
    }

    [Fact]
    public async Task P0_01_根层对象_十四文件两侧齐全必须判为通过()
    {
        var src = NewDir("roots-src");
        var tgt = NewDir("roots-tgt");
        string[] names =
        {
            "bookmarks_2022_1_9.html", "Outlook data.pst", "PDS新系统.lnk", "a.bin",
            "b.bin", "c.bin", "d.bin", "e.bin", "f.bin", "g.bin", "h.bin", "i.bin", "j.bin", "k.bin",
        };
        for (var i = 0; i < names.Length; i++)
        {
            WriteFile(src, names[i], 10 + i);
            WriteFile(tgt, names[i], 10 + i);
        }
        // 子目录内容不是「根层文件」：RootFiles 对象不递归，两侧都放了不同文件也不得影响结论
        WriteFile(NewDir("roots-src/sub"), "deep.bin", 999);
        WriteFile(NewDir("roots-tgt/sub"), "deep-other.bin", 1);

        var obj = new PlannedObject
        {
            ObjectId = "object-000018",
            Kind = ObjectKind.RootFiles,
            SourcePath = src,                 // 目录根（无尾随分隔符）
            TargetPath = tgt + "\\",          // 现场形态：目标卷根带尾随分隔符
            EstimatedFiles = 14,
            EstimatedBytes = 10 + 11 + 12 + 13 + 14 + 15 + 16 + 17 + 18 + 19 + 20 + 21 + 22 + 23,
        };

        var report = await VerifyAsync(NewJob(Plan(obj)));
        var r = Assert.Single(report.Objects);

        Assert.Equal(14, r.SourceFiles);
        Assert.Equal(14, r.TargetFiles);
        Assert.Equal("OK", r.Status);
        Assert.True(report.OverallPass);
        Assert.Empty(r.MissingSamples);
        Assert.Empty(r.ExtraOnTarget);
    }

    // ══════════════════════════════════════════════════════════════════════
    // P0-02 四态：确认差异 / 目标额外 / 无法确认 / 计划本身为空
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task P0_02_目标多两个文件_必须判差异并点出目标额外项()
    {
        var src = NewDir("extra-src");
        var tgt = NewDir("extra-tgt");
        for (var i = 0; i < 10; i++) { WriteFile(src, $"f{i}.bin", 100); WriteFile(tgt, $"f{i}.bin", 100); }
        WriteFile(tgt, "extra1.bin", 5);
        WriteFile(tgt, "extra2.bin", 5);

        var obj = new PlannedObject
        {
            ObjectId = "object-000001",
            Kind = ObjectKind.DataVolume,
            SourcePath = src,
            TargetPath = tgt,
            EstimatedFiles = 10,
            EstimatedBytes = 1000,
        };

        var report = await VerifyAsync(NewJob(Plan(obj)));
        var r = Assert.Single(report.Objects);

        Assert.Equal("MISMATCH", r.Status);
        Assert.False(r.CountMatch);
        // 现场 10/12 曾被写成「缺失」：目标多出来的东西不是缺失，且默认不自动清理
        Assert.Equal(2, r.ExtraOnTarget.Count);
        Assert.Empty(r.MissingSamples);
        Assert.False(report.OverallPass);
        // 确认差异 ⇒ 允许进入破坏性修复（这是闸门的唯一放行条件）
        Assert.True(r.AllowsDestructiveRepair);
    }

    [Fact]
    public async Task P0_02_确实少一个文件_判确认差异且放行破坏性修复()
    {
        var src = NewDir("miss-src");
        var tgt = NewDir("miss-tgt");
        foreach (var n in new[] { "a.bin", "b.bin", "c.bin" }) { WriteFile(src, n, 7); }
        foreach (var n in new[] { "a.bin", "b.bin" }) { WriteFile(tgt, n, 7); }

        var obj = new PlannedObject
        {
            ObjectId = "object-000002",
            Kind = ObjectKind.DataVolume,
            SourcePath = src,
            TargetPath = tgt,
            EstimatedFiles = 3,
            EstimatedBytes = 21,
        };

        var report = await VerifyAsync(NewJob(Plan(obj)));
        var r = Assert.Single(report.Objects);

        Assert.Equal("MISMATCH", r.Status);
        Assert.Contains("c.bin", r.MissingSamples);
        Assert.True(r.AllowsDestructiveRepair);
        Assert.False(report.OverallPass);
    }

    [Fact]
    public async Task P0_02_源根不可达_判无法确认且禁止破坏性修复()
    {
        var tgt = NewDir("noSrc-tgt");
        foreach (var n in new[] { "a.bin", "b.bin", "c.bin" }) { WriteFile(tgt, n, 7); }

        var obj = new PlannedObject
        {
            ObjectId = "object-000003",
            Kind = ObjectKind.DataVolume,
            SourcePath = Path.Combine(_root, "does-not-exist"),   // 共享已被策略关闭 / 未挂载
            TargetPath = tgt,
            EstimatedFiles = 3,
            EstimatedBytes = 21,
        };

        var report = await VerifyAsync(NewJob(Plan(obj)));
        var r = Assert.Single(report.Objects);

        Assert.Equal("UNKNOWN", r.Status);
        Assert.Equal("source-unavailable", r.UnknownReason);
        Assert.False(report.OverallPass);
        // ★ P0-01(4) ★ 证据不完整 ⇒ 绝不允许进入任何会删目标文件的分支
        Assert.False(r.AllowsDestructiveRepair);
    }

    [Fact]
    public async Task P0_02_计划非空但两侧零文件_判无法确认而不是通过()
    {
        var src = NewDir("empty-src");
        var tgt = NewDir("empty-tgt");

        var obj = new PlannedObject
        {
            ObjectId = "object-000004",
            Kind = ObjectKind.DataVolume,
            SourcePath = src,
            TargetPath = tgt,
            EstimatedFiles = 14,          // 计划说有 14 个文件
            EstimatedBytes = 29461740616, // 27.44 GB
        };

        var report = await VerifyAsync(NewJob(Plan(obj)));
        var r = Assert.Single(report.Objects);

        // 旧实现：CountMatch && BytesMatch ⇒ OK。这正是「0/0 也是通过」的入口
        Assert.Equal("UNKNOWN", r.Status);
        Assert.Equal("both-sides-empty-against-plan", r.UnknownReason);
        Assert.False(report.OverallPass);
        Assert.False(r.AllowsDestructiveRepair);
    }

    [Fact]
    public async Task P0_02_计划本身为空且两侧为空_记为无可核对数据而不是通过()
    {
        var src = NewDir("plannedEmpty-src");
        var tgt = NewDir("plannedEmpty-tgt");

        var obj = new PlannedObject
        {
            ObjectId = "object-000005",
            Kind = ObjectKind.DataVolume,
            SourcePath = src,
            TargetPath = tgt,
            EstimatedFiles = 0,
            EstimatedBytes = 0,
        };

        var report = await VerifyAsync(NewJob(Plan(obj)));
        var r = Assert.Single(report.Objects);

        Assert.Equal("EMPTY", r.Status);
        Assert.Equal("无可核对数据", r.StatusText);
        Assert.False(r.AllowsDestructiveRepair);
        // 合法空对象不算失败（但也不冒充"核对过内容"）
        Assert.True(report.OverallPass);
    }
}