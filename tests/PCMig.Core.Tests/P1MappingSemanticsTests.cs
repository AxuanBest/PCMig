using System.Text.Json;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Planning;
using PCMig.Core.Transfer;
using Serilog;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ P1-07 ★ 目录选择的目标映射语义回归测试。
///
/// 现场背景（原始日志实测）：用户在目录树里勾选 <c>\\192.0.2.35\Users\d970</c>、目标根 <c>C:\Users\D970</c>，
/// plan.json 却把目标写成 <c>C:\Users\D970\d970</c>（多嵌一层同名目录）；同一现场 a632/be58 两个 Job 都是这样。
/// 这里把「内容直达 / 保留文件夹本身 / 多选可辨识 / RootFiles / 绝对路径混入 / 自拷贝」逐条钉住。
/// </summary>
public sealed class P1MappingSemanticsTests : IDisposable
{
    private static readonly ILogger Log = new LoggerConfiguration().CreateLogger();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pcmig-p1map-" + Guid.NewGuid().ToString("N"));

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
        var job = new JobDefinition { JobId = "JOB-P1-" + Guid.NewGuid().ToString("N")[..8], TargetRoot = targetRoot };
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
    // (1)(2) 单个选中目录：内容直达 vs 保留文件夹本身
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>默认语义（contentDirect）：选中 d970 后，内容直接落在目标根，不再多嵌一层 d970。</summary>
    [Fact]
    public void P1_07_A_SingleSelectedFolder_ContentLandsDirectlyOnTargetRoot()
    {
        var target = NewDir("target-root");
        var job = Job(target, (@"\\SRC-PC\Users", ObjectKind.UserProfile));
        job.CustomSelections.Add(@"\\SRC-PC\Users\d970");
        var plan = PlanOf(job, Observed(Obj("object-000001", ObjectKind.UserProfile, @"\\SRC-PC\Users\d970")));

        var planned = Assert.Single(plan.Objects);
        Assert.Equal(target, planned.TargetPath);

        // 旧行为自证：源根相对映射会多嵌一层同名目录，正是现场 plan.json 里的 C:\Users\D970\d970。
        var legacyNested = Path.Combine(target, "d970");
        Assert.NotEqual(legacyNested, planned.TargetPath);
    }

    /// <summary>可选语义（keepFolder）：显式要求「保留所选文件夹本身」时才嵌一层。</summary>
    [Fact]
    public void P1_07_B_KeepFolderMode_PreservesSelectedFolderItself()
    {
        var target = NewDir("target-root");
        var job = Job(target, (@"\\SRC-PC\Users", ObjectKind.UserProfile));
        job.Options.SelectedFolderMapping = MigrationOptions.SelectedFolderMappingKeepFolder;
        job.CustomSelections.Add(@"\\SRC-PC\Users\d970");
        var plan = PlanOf(job, Observed(Obj("object-000001", ObjectKind.UserProfile, @"\\SRC-PC\Users\d970")));

        var planned = Assert.Single(plan.Objects);
        Assert.Equal(Path.Combine(target, "d970"), planned.TargetPath);
    }

    /// <summary>选中源根本身时，源根内部的目录结构必须保留（只去掉被选中的那一段）。</summary>
    [Fact]
    public void P1_07_B2_SelectingTheShareRoot_KeepsInnerStructure()
    {
        var target = NewDir("target-root");
        var job = Job(target, (@"\\SRC-PC\Users", ObjectKind.UserProfile));
        job.CustomSelections.Add(@"\\SRC-PC\Users");
        var plan = PlanOf(job, Observed(Obj("object-000001", ObjectKind.UserProfile, @"\\SRC-PC\Users\d970")));

        var planned = Assert.Single(plan.Objects);
        Assert.Equal(Path.Combine(target, "d970"), planned.TargetPath);
    }

    /// <summary>选中的目录比对象更深（对象 = 源根本身）时，内容直达仍然成立：直接落在目标根。</summary>
    [Fact]
    public void P1_07_B3_SelectedFolderDeeperThanObject_IsStillContentDirect()
    {
        var target = NewDir("target-root");
        var job = Job(target, (@"\\SRC-PC\Users", ObjectKind.UserProfile));
        job.CustomSelections.Add(@"\\SRC-PC\Users\d970");
        var plan = PlanOf(job, Observed(Obj("object-000001", ObjectKind.UserProfile, @"\\SRC-PC\Users\d970")));

        Assert.Equal(target, Assert.Single(plan.Objects).TargetPath);
    }

    // ══════════════════════════════════════════════════════════════════════
    // (1) 多选：不同来源绝不静默合并到同一目标
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>两个不同共享下同名目录被同时选中时，目标必须可辨识（不同源不落进同一个目录）。</summary>
    [Fact]
    public void P1_07_C_MultiSelection_DifferentSharesWithSameLeafName_AreDistinguishable()
    {
        var target = NewDir("target-root");
        var job = Job(target,
            (@"\\SRC-A\Users", ObjectKind.UserProfile),
            (@"\\SRC-B\Users", ObjectKind.UserProfile));
        job.CustomSelections.Add(@"\\SRC-A\Users\d970");
        job.CustomSelections.Add(@"\\SRC-B\Users\d970");
        var plan = PlanOf(job, Observed(
            Obj("object-000001", ObjectKind.UserProfile, @"\\SRC-A\Users\d970"),
            Obj("object-000002", ObjectKind.UserProfile, @"\\SRC-B\Users\d970")));

        var t1 = plan.Objects.Single(o => o.ObjectId == "object-000001").TargetPath;
        var t2 = plan.Objects.Single(o => o.ObjectId == "object-000002").TargetPath;

        Assert.NotEqual(t1, t2, StringComparer.OrdinalIgnoreCase);
        Assert.StartsWith(target + Path.DirectorySeparatorChar, t1, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(target + Path.DirectorySeparatorChar, t2, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(plan.ExcludedByPolicy, l => l.Contains("映射保护") && l.Contains("撞到同一目标目录"));
    }

    /// <summary>同一个共享下的多个选择，按源根相对结构映射（不额外加来源前缀）。</summary>
    [Fact]
    public void P1_07_C2_MultiSelection_SameShare_KeepsPlainRelativeMapping()
    {
        var target = NewDir("target-root");
        var job = Job(target, (@"\\SRC-PC\Users", ObjectKind.UserProfile));
        job.CustomSelections.Add(@"\\SRC-PC\Users\d970");
        job.CustomSelections.Add(@"\\SRC-PC\Users\opsx");
        var plan = PlanOf(job, Observed(
            Obj("object-000001", ObjectKind.UserProfile, @"\\SRC-PC\Users\d970"),
            Obj("object-000002", ObjectKind.UserProfile, @"\\SRC-PC\Users\opsx")));

        Assert.Equal(Path.Combine(target, "d970"), plan.Objects.Single(o => o.ObjectId == "object-000001").TargetPath);
        Assert.Equal(Path.Combine(target, "opsx"), plan.Objects.Single(o => o.ObjectId == "object-000002").TargetPath);
    }

    // ══════════════════════════════════════════════════════════════════════
    // RootFiles / 源根对象：语义不得被 P1-07 改动
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>盘符根散落文件（RootFiles）永远落在目标根（现场 D:\ 的 14 个文件）。</summary>
    [Fact]
    public void P1_07_D_RootFiles_StillLandOnTargetRoot()
    {
        var target = NewDir("target-root");
        var job = Job(target, (@"\\SRC-PC\D", ObjectKind.DataVolume));
        var rootFiles = Obj("object-000018", ObjectKind.RootFiles, @"\\SRC-PC\D", 29461740616, 14);
        rootFiles.FileList = new List<string> { "a.pst", "b.xlsx" };
        var plan = PlanOf(job, Observed(rootFiles));

        Assert.Equal(target, Assert.Single(plan.Objects).TargetPath);
    }

    /// <summary>整盘/整共享对象（SourcePath == 源根）映射到目标根，保持原语义。</summary>
    [Fact]
    public void P1_07_E_SourceRootObject_Unchanged()
    {
        var target = NewDir("target-root");
        var job = Job(target, (@"\\SRC-PC\D", ObjectKind.DataVolume));
        var plan = PlanOf(job, Observed(Obj("object-000001", ObjectKind.DataVolume, @"\\SRC-PC\D")));

        Assert.Equal(target, Assert.Single(plan.Objects).TargetPath);
    }

    // ══════════════════════════════════════════════════════════════════════
    // (3) 路径穿越 / 绝对路径混入 / 源与目标同一位置
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>源根不是选中路径的祖先时（跨盘/跨共享），Path.Combine 不得整体替换目标根。</summary>
    [Fact]
    public void P1_07_F_AbsolutePathInjection_StaysUnderTargetRoot()
    {
        var target = NewDir("target-root");
        var job = Job(target, (@"\\SRC-PC\Users", ObjectKind.UserProfile));
        job.CustomSelections.Add(@"C:\Other\D970");
        var plan = PlanOf(job, Observed(Obj("object-000001", ObjectKind.UserProfile, @"C:\Other\D970")));

        var planned = Assert.Single(plan.Objects);
        // 单独选中、且就是该对象本身 ⇒ 内容直达目标根；关键是不许"整体替换目标根"逃到 C:\Other\D970。
        Assert.Equal(target, planned.TargetPath);
        Assert.NotEqual(@"C:\Other\D970", planned.TargetPath);
    }

    /// <summary>跨盘/跨共享且**不是**选中的那个对象：按名称落到目标根下，绝不逃出目标根。</summary>
    [Fact]
    public void P1_07_F2_ObjectOutsideAnySourceRoot_FallsBackToNameUnderTargetRoot()
    {
        var target = NewDir("target-root");
        var job = Job(target, (@"\\SRC-PC\Users", ObjectKind.UserProfile));
        var plan = PlanOf(job, Observed(Obj("object-000001", ObjectKind.AppState, @"C:\Other\D970")));

        var planned = Assert.Single(plan.Objects);
        Assert.Equal(Path.Combine(target, "D970"), planned.TargetPath);
    }

    /// <summary>源与目标同一位置（自己拷自己）：本计划不得排出该对象，必须留审计并被人工确认。</summary>
    [Fact]
    public void P1_07_G_SelfCopy_IsNotPlanned_AndIsAudited()
    {
        var target = NewDir("target-root");
        var job = Job(target, (@"\\SRC-PC\Users", ObjectKind.UserProfile));
        job.CustomSelections.Add(target);
        var plan = PlanOf(job, Observed(Obj("object-000001", ObjectKind.UserProfile, target)));

        Assert.Empty(plan.Objects);
        Assert.Contains(plan.ExcludedByPolicy, l => l.Contains("映射保护") && l.Contains("重叠"));
    }

    // ══════════════════════════════════════════════════════════════════════
    // (3)(4) 映射快照：plan.json 是唯一真值（Resume/Verify/Repair 都读它）
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>目标路径必须经得起 plan.json 序列化往返（后续 Resume/Verify/Repair 只认这份快照）。</summary>
    [Fact]
    public void P1_07_H_TargetPath_SurvivesPlanJsonRoundTrip()
    {
        var target = NewDir("target-root");
        var job = Job(target, (@"\\SRC-PC\Users", ObjectKind.UserProfile));
        job.CustomSelections.Add(@"\\SRC-PC\Users\d970");
        var plan = PlanOf(job, Observed(Obj("object-000001", ObjectKind.UserProfile, @"\\SRC-PC\Users\d970")));

        var json = JsonSerializer.Serialize(plan);
        var back = JsonSerializer.Deserialize<MigrationPlan>(json);

        Assert.NotNull(back);
        Assert.Equal(plan.Objects[0].TargetPath, back!.Objects[0].TargetPath);
    }

    /// <summary>(3) Robocopy 实际 Dest 必须逐字来自计划快照，不允许执行处重新推导目标路径。</summary>
    [Fact]
    public void P1_07_J_RobocopyDestination_IsThePlanSnapshot()
    {
        var target = NewDir("target-root");
        var job = Job(target, (@"\\SRC-PC\Users", ObjectKind.UserProfile));
        job.CustomSelections.Add(@"\\SRC-PC\Users\d970");
        var plan = PlanOf(job, Observed(Obj("object-000001", ObjectKind.UserProfile, @"\\SRC-PC\Users\d970")));

        var obj = Assert.Single(plan.Objects);
        var runner = new RobocopyRunner(Log);
        var args = runner.BuildArguments(obj.SourcePath, obj.TargetPath, job.Options, new MigrationMatrix(),
            PassKind.Bulk, Path.Combine(target, "robocopy.log"));

        Assert.StartsWith(RobocopyRunner.QuoteArg(obj.SourcePath) + " " + RobocopyRunner.QuoteArg(obj.TargetPath), args);
        Assert.DoesNotContain(RobocopyRunner.QuoteArg(Path.Combine(target, "d970")), args);
        Assert.Equal(target, obj.TargetPath);
    }

    /// <summary>(2) 开工前的「源 → 目标」预览：内容直达、RootFiles 都要看得见，且逐字来自计划快照。</summary>
    [Fact]
    public void P1_07_K_PreviewTable_ShowsSourceToTargetFromThePlanSnapshot()
    {
        var target = NewDir("target-root");
        var job = Job(target, (@"\\SRC-PC\D", ObjectKind.DataVolume));
        job.CustomSelections.Add(@"\\SRC-PC\D\d970");
        var rootFiles = Obj("object-000018", ObjectKind.RootFiles, @"\\SRC-PC\D", 29461740616, 14);
        var plan = PlanOf(job, Observed(rootFiles, Obj("object-000001", ObjectKind.UserProfile, @"\\SRC-PC\D\d970")));

        var rows = PlanMappingPreview.Build(plan, target);
        Assert.Equal(2, rows.Count);

        var selected = rows.Single(r => r.ObjectId == "object-000001");
        Assert.Equal(target, selected.TargetPath);                      // 内容直达，不再多嵌一层 d970
        Assert.Equal(@"\\SRC-PC\D\d970", selected.SourcePath);

        var rootRow = rows.Single(r => r.ObjectId == "object-000018");
        // ★ D1-d / P1-08 ★ 散落文件与"选中文件夹内容直达目标根"是两个不同来源：只把自动扫描得出的
        //   散落文件挪进带来源前缀的子目录，用户显式选择的内容直达映射保持不变。
        Assert.Equal(Path.Combine(target, "SRC-PC_D"), rootRow.TargetPath);
        Assert.Contains("根层", rootRow.Note);
    }

    /// <summary>PathNorm 的「之内/重叠」判定（映射保护的基础，卷根与 UNC 根都必须给出同一结论）。</summary>
    [Fact]
    public void P1_07_I_PathNorm_InsideAndOverlap_HandleVolumeAndUncRoots()
    {
        Assert.True(Util.PathNorm.IsInside(@"D:\", @"D:\sub"));
        Assert.False(Util.PathNorm.IsInside(@"D:\", @"D:\"));
        Assert.True(Util.PathNorm.IsInside(@"\\SRC-PC\D", @"\\SRC-PC\D\sub\file.txt"));
        Assert.False(Util.PathNorm.IsInside(@"\\SRC-PC\D", @"\\SRC-PC\D2"));  // 前缀相同但不同目录
        Assert.True(Util.PathNorm.Overlaps(@"D:\", @"D:\"));
        Assert.False(Util.PathNorm.Overlaps(@"D:\a", @"E:\a"));
    }
}