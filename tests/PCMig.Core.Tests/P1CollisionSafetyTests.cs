using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Planning;
using Serilog;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ P1-08 / D1-d（2026-10-12）★ 多源目标碰撞的**数据安全**回归。
///
/// 现场背景：d950 → D970 的目标里多出一层同名目录，用户看不出两个来源的同名内容到底落在了哪。
/// 计划是唯一的映射真值（Robocopy / 验证 / 修复 / 续传 / 报告共用），所以这里钉住三条：
///  ①两个**不同来源**映射到同一目标目录 ⇒ 必须按来源前缀分开，绝不静默合并；
///  ②计划里所有非 RootFiles 对象的目标路径两两不同（可辨识）；
///  ③"选中文件夹内容直达目标根"与"根目录散落文件落目标根"是两个**不同来源**：
///     它们同时落目标根时，必须退回"保留文件夹本身"，否则同名文件会静默互相覆盖且事后无法分辨。
/// </summary>
public sealed class P1CollisionSafetyTests : IDisposable
{
    private static readonly ILogger Log = new LoggerConfiguration().CreateLogger();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pcmig-p1col-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* 清理失败不影响结论 */ }
    }

    private string NewDir(string name)
    {
        var p = Path.Combine(_root, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private static JobDefinition Job(string targetRoot, params (string Path, ObjectKind Kind)[] sources)
    {
        var job = new JobDefinition { JobId = "JOB-P1COL-" + Guid.NewGuid().ToString("N")[..8], TargetRoot = targetRoot };
        foreach (var s in sources) job.Sources.Add(new SourceSpec { Path = s.Path, Kind = s.Kind });
        return job;
    }

    private static ObservedState Observed(params ScannedObject[] objects)
    {
        var o = new ObservedState { JobId = "observed" };
        o.Objects.AddRange(objects);
        return o;
    }

    private static ScannedObject Obj(string id, ObjectKind kind, string sourcePath, long bytes = 1000, long files = 3)
        => new() { ObjectId = id, Kind = kind, SourcePath = sourcePath, Bytes = bytes, Files = files };

    private static MigrationPlan PlanOf(JobDefinition job, ObservedState observed)
        => new Planner(Log).CreatePlan(job, observed, new MigrationMatrix());

    // ══════════════════════════════════════════════════════════════════════
    // ① 不同来源撞同一目标目录 ⇒ 按来源前缀分开 + 留审计
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void P1_08_A_两个共享的同名文件夹不得写同一目标路径()
    {
        var target = NewDir("target-root");
        var job = Job(target,
            (@"\\SRC-A\Users", ObjectKind.UserProfile),
            (@"\\SRC-B\Users", ObjectKind.UserProfile));
        var plan = PlanOf(job, Observed(
            Obj("object-000001", ObjectKind.UserProfile, @"\\SRC-A\Users\d970"),
            Obj("object-000002", ObjectKind.UserProfile, @"\\SRC-B\Users\d970")));

        var a = plan.Objects.Single(o => o.ObjectId == "object-000001");
        var b = plan.Objects.Single(o => o.ObjectId == "object-000002");

        Assert.NotEqual(a.TargetPath, b.TargetPath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(target, "SRC-A_Users", "d970"), a.TargetPath);
        Assert.Equal(Path.Combine(target, "SRC-B_Users", "d970"), b.TargetPath);
        Assert.Contains(plan.ExcludedByPolicy, l => l.Contains("撞到同一目标目录") && l.Contains("object-000001"));
        Assert.Contains(plan.ExcludedByPolicy, l => l.Contains("撞到同一目标目录") && l.Contains("object-000002"));
    }

    // ══════════════════════════════════════════════════════════════════════
    // ② 计划里非 RootFiles 对象的目标路径两两不同（可辨识）
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void P1_08_B_计划里非RootFiles对象的目标路径必须两两不同()
    {
        var target = NewDir("target-root");
        var job = Job(target,
            (@"\\SRC-A\Users", ObjectKind.UserProfile),
            (@"\\SRC-B\Users", ObjectKind.UserProfile),
            (@"\\SRC-C\D", ObjectKind.DataVolume));
        var rootFiles = Obj("object-000009", ObjectKind.RootFiles, @"\\SRC-C\D", 500, 2);
        rootFiles.FileList = new List<string> { "a.pst", "b.xlsx" };

        var plan = PlanOf(job, Observed(
            Obj("object-000001", ObjectKind.UserProfile, @"\\SRC-A\Users\d970"),
            Obj("object-000002", ObjectKind.UserProfile, @"\\SRC-B\Users\d970"),
            Obj("object-000003", ObjectKind.UserProfile, @"\\SRC-A\Users\ops"),
            Obj("object-000004", ObjectKind.DataVolume, @"\\SRC-C\D\素材"),
            rootFiles));

        var nonRoot = plan.Objects.Where(o => o.Kind != ObjectKind.RootFiles).ToList();
        Assert.Equal(nonRoot.Count,
            nonRoot.Select(o => o.TargetPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(plan.Objects.Count, plan.Objects.Select(o => o.ObjectId).Distinct().Count());
        Assert.Equal(target, plan.Objects.Single(o => o.Kind == ObjectKind.RootFiles).TargetPath);
    }

    // ══════════════════════════════════════════════════════════════════════
    // ③ 内容直达 + 根目录散落文件：两个不同来源，不许同时落目标根
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void P1_08_C_内容直达对象与根目录散落文件不许同时落目标根()
    {
        var target = NewDir("target-root");
        var job = Job(target, (@"\\SRC-PC\Users", ObjectKind.UserProfile));
        job.CustomSelections.Add(@"\\SRC-PC\Users\d970");   // 单独选中 d970 ⇒ 默认内容直达

        var rootFiles = Obj("object-000018", ObjectKind.RootFiles, @"\\SRC-PC\Users", 4096, 3);
        rootFiles.FileList = new List<string> { "desktop.ini", "notes.txt", "a.pst" };

        var plan = PlanOf(job, Observed(
            Obj("object-000001", ObjectKind.UserProfile, @"\\SRC-PC\Users\d970"),
            rootFiles));

        var d970 = plan.Objects.Single(o => o.ObjectId == "object-000001");
        var rf = plan.Objects.Single(o => o.ObjectId == "object-000018");

        Assert.Equal(target, d970.TargetPath);                          // 用户显式选择的内容直达：不动（P1-07 语义）
        Assert.NotEqual(target, rf.TargetPath);                         // ★ 散落文件不得与它同落目标根
        Assert.Equal(Path.Combine(target, "SRC-PC_Users"), rf.TargetPath);   // 按来源前缀落子目录
        Assert.Contains(plan.ExcludedByPolicy,
            l => l.Contains("object-000018") && l.Contains("根目录散落文件") && l.Contains("object-000001"));
    }

    // ══════════════════════════════════════════════════════════════════════
    // ④ 同源根的整盘对象与散落文件：语义不变（不是"多源撞车"）
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void P1_08_D_同源根的整盘对象与散落文件仍同落目标根()
    {
        var target = NewDir("target-root");
        var job = Job(target, (@"\\SRC-PC\D", ObjectKind.DataVolume));
        var rootFiles = Obj("object-000018", ObjectKind.RootFiles, @"\\SRC-PC\D", 500, 2);
        rootFiles.FileList = new List<string> { "a.pst" };

        var plan = PlanOf(job, Observed(
            Obj("object-000001", ObjectKind.DataVolume, @"\\SRC-PC\D"),
            rootFiles));

        Assert.All(plan.Objects, o => Assert.Equal(target, o.TargetPath));
        Assert.DoesNotContain(plan.ExcludedByPolicy, l => l.Contains("根目录散落文件"));
    }
}