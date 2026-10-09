// ============================================================================
//  P1-4（Preview.2）：Step4 失败原因「查看完整原因」的行为级回归
// ============================================================================
//
//  真机现场（JOB-20261008-140340-6310）：
//    · object-000001 robocopy 退出码 9（= 1 有文件被复制 + 8 有文件复制失败），
//      但那个"失败 8"来自**汇总表的目录级失败 1**，日志里一条文件级错误行都没有；
//    · 回执里记的 ErrorDetail 其实是一条普通『新文件』状态行（被旧版当成了失败原因）；
//    · Step4 列表里只显示 ErrorTranslator.ShortReason 的 100 字符截断版 + CharacterEllipsis，
//      长中文路径读不全，用户无从判断到底哪一条没复制成。
//
//  本文件锁两件事：
//    ① 「完整原因」文本真的把**未截断原文 + 退出码位含义 + robocopy 汇总表 + 诚实说明 + 路径**带上，
//       且只对未成功的三种状态（Failed / CompletedWithErrors / Interrupted）生成；
//    ② 这个文本是**附加**的：列表里的 Detail 仍是截断版（不因为新增全文而把列表撑爆），
//       页面用真实派生布尔 CanShowDetail 决定是否给「查看完整原因」按钮。
//
//  纪律（与 A5PresentationRegressionTests 同一套）：
//    · 作业目录走进程级环境变量 PCMIG_JOBS 指向本测试的临时沙盒，**绝不触碰 %ProgramData% 真实存档**；
//      因为改的是进程级变量，本类与 A5 两个类共用 [Collection("A5-jobdir-isolation")] 串行执行；
//    · 不发生任何真实网络/robocopy 动作：这里只走「载入已有任务（回执重建）」这条只读路径；
//    · 断言的都是产品源码的真实行为（Presentation 源码被链入本测试项目编译，
//      TestOnlyDispatcherQueueShim 让 Post 走产品里真实存在的"无队列即直通"分支）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PCMig.Core.Jobs;
using PCMig.Core.Models;
using PCMig.Core.State;
using PCMig.WinUI.Presentation;
using Serilog;
using Xunit;

namespace PCMig.Core.Tests;

[Collection("A5-jobdir-isolation")]
public sealed class Step4FailureDetailTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string? _previousJobsRoot;
    private readonly ILogger _log;

    public Step4FailureDetailTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "pcmig-step4-detail-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_sandbox);

        _previousJobsRoot = Environment.GetEnvironmentVariable("PCMIG_JOBS");
        Environment.SetEnvironmentVariable("PCMIG_JOBS", Path.Combine(_sandbox, "Jobs"));

        _log = new LoggerConfiguration().CreateLogger();   // 无 sink：不写 %ProgramData% 日志
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PCMIG_JOBS", _previousJobsRoot);
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    // ────────────────────────── 真机形状的现场数据 ──────────────────────────

    /// <summary>回执里记的那条"原因"——真机上它是一条普通『新文件』状态行，不是失败原因。</summary>
    private const string RecordedReason =
        @"\\SRC-PC\E\PCMigLab\Evidence\tv06-error67-text-r1-tv06-error67-text.txt";

    /// <summary>真机 object-000001 日志的形状：汇总表（目录 失败 1）+ 一条普通『新文件』状态行，没有任何错误行。</summary>
    private const string RealShapedLog =
        "------------------------------------------------------------------------------\n" +
        "               总数          复制        跳过       不匹配       失败         其他\n" +
        "   目录:         1505          1505           0           0           1           0\n" +
        "   文件:        27835         27816          19           0           0           0\n" +
        "   字节:       89.307g       3.945g      85.361g           0           0           0\n" +
        "   时间:  0:31:07   0:01:02                       0:00:00   0:00:02\n" +
        "\n" +
        "\t    新文件    \t\t    9934\t" + RecordedReason + "\n";

    /// <summary>建一个真机形状的任务：两个对象（一个 Failed/exit 9、一个 Interrupted）、状态 CompletedWithErrors。</summary>
    private JobContext CreateRealShapedJob()
    {
        var def = new JobDefinition
        {
            JobId = "JOB-STEP4-DETAIL",
            SourceHost = "SRC-PC",
            SourceUser = @"SRC-PC\User",
            TargetRoot = @"E:\",
            CreatedBy = "User",
            Sources = { new SourceSpec { Path = @"\\SRC-PC\E\PCMigLab", Kind = ObjectKind.DataVolume } },
        };
        var ctx = new JobManager(_log).Create(def);

        var plan = new MigrationPlan { JobId = def.JobId };
        plan.Objects.Add(new PlannedObject
        {
            ObjectId = "object-000001",
            Kind = ObjectKind.DataVolume,
            SourcePath = @"\\SRC-PC\E\PCMigLab",
            TargetPath = @"E:\PCMigLab",
            EstimatedBytes = 95_893_239_032,
            EstimatedFiles = 27835,
            UseRestartablePass = true,
        });
        plan.Objects.Add(new PlannedObject
        {
            ObjectId = "object-000003",
            Kind = ObjectKind.DataVolume,
            SourcePath = @"\\SRC-PC\E\系统ISO和Vm安装包",
            TargetPath = @"E:\系统ISO和Vm安装包",
            EstimatedBytes = 35_280_617_760,
            EstimatedFiles = 17,
            UseRestartablePass = true,
        });
        ctx.SavePlan(plan);
        ctx.SaveState(new JobState
        {
            JobId = def.JobId,
            Phase = JobPhase.CompletedWithErrors,
            TotalObjects = 3,
            CompletedObjects = 2,
            FailedObjects = 1,
            TotalBytes = 161_811_681_804,
            CompletedBytes = 70_155_088_396,
            Percent = 43.356,
        });

        var started = new DateTime(2026, 10, 8, 6, 5, 9, DateTimeKind.Utc);
        Save(ctx, "object-000001-20261008060509", new ObjectReceipt
        {
            ObjectId = "object-000001",
            Kind = ObjectKind.DataVolume,
            SourcePath = @"\\SRC-PC\E\PCMigLab",
            TargetPath = @"E:\PCMigLab",
            StartedUtc = started,
            CompletedUtc = started.AddMinutes(65),
            Status = ObjectStatus.Failed,
            TargetBytes = 4_236_645_624,
            TargetFiles = 27816,
            RobocopyExitCodeBulk = 9,          // 1 + 8
            RobocopyExitCodeLarge = -1,
            ErrorClass = ErrorClass.Permanent,
            ErrorDetail = "\t    新文件    \t\t    9934\t" + RecordedReason,
            Attempt = 1,
        });
        Save(ctx, "object-000003-20261008060600", new ObjectReceipt
        {
            ObjectId = "object-000003",
            Kind = ObjectKind.DataVolume,
            SourcePath = @"\\SRC-PC\E\系统ISO和Vm安装包",
            TargetPath = @"E:\系统ISO和Vm安装包",
            StartedUtc = started,
            CompletedUtc = started.AddMinutes(1),
            Status = ObjectStatus.Interrupted,
            TargetBytes = 4_023_386_115,
            TargetFiles = 0,
            RobocopyExitCodeBulk = 1,
            RobocopyExitCodeLarge = -1,
            ErrorClass = ErrorClass.None,
            ErrorDetail = "interrupted",
            Attempt = 1,
        });

        // 只给 object-000001 写日志：object-000003 保持"日志缺失"，正好覆盖诚实说明的另一条分支。
        Directory.CreateDirectory(ctx.RoboLogsDir);
        File.WriteAllText(Path.Combine(ctx.RoboLogsDir, "object-000001.log"), RealShapedLog, new UTF8Encoding(false));
        return ctx;
    }

    private static void Save(JobContext ctx, string fileName, ObjectReceipt receipt)
        => JsonStateStore.WriteAtomic(Path.Combine(ctx.ReceiptsDir, fileName + ".json"), receipt);

    private MigrationSessionViewModel NewSession() => new(null, null, _log);

    /// <summary>找出某对象的对象级失败项（按完整原因里的 ObjectId 定位，避免依赖清单顺序）。</summary>
    private static SessionFailItem FailItemOf(MigrationSessionViewModel vm, string objectId)
        => Assert.Single(vm.FailItems, f => f.FullText is not null && f.FullText.Contains("对象：" + objectId));

    // ══════════ 行为级：完整原因真的生成 ══════════

    [Fact]
    public async Task Step4_ObjectFailure_CarriesTheWholeReasonAndOffersTheDetailButton()
    {
        var ctx = CreateRealShapedJob();
        var vm = NewSession();
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        var item = FailItemOf(vm, "object-000001");

        Assert.True(item.CanShowDetail);                                  // ⇒ 页面会显示「查看完整原因」
        Assert.Contains("✘ 对象失败", item.Title);

        // ★ 列表里的 Detail 仍是**截断版**：新增全文不允许把列表也撑成长文。
        Assert.DoesNotContain("robocopy 日志证据", item.Detail);
        Assert.NotEqual(item.FullText, item.Detail);
        Assert.DoesNotContain(item.FullText!, item.Detail);

        var full = item.FullText!;
        // ① 回执原文**未截断**（真机上被 ShortReason 砍掉的就是这段长中文路径）
        Assert.Contains("── 任务记录的原因（回执原文，未截断）──", full);
        Assert.Contains("tv06-error67-text-r1-tv06-error67-text.txt", full);
        // ② 退出码位含义（旧版只有"位掩码 9"）
        Assert.Contains("Bulk 退出码：9（位掩码 9", full);
        Assert.Contains("1=有文件被复制", full);
        Assert.Contains("8=", full);
        Assert.Contains("Large 退出码：未执行（该通道没跑）", full);
        // ③ robocopy 汇总表原文（失败位真正的来源；1505 只存在于日志里，回执没有这个字段）
        Assert.Contains("── robocopy 日志证据 ──", full);
        Assert.Contains("汇总表（日志原文", full);
        Assert.Contains("1505", full);
        // ④ 诚实说明 + 对账（那条『新文件』行不是失败原因）
        Assert.Contains("没有一条文件级错误行", full);
        Assert.Contains("不能把它当失败原因看", full);
        // ⑤ 落点路径：用户能直接去翻现场
        Assert.Contains("完整日志：", full);
        Assert.Contains("任务目录：", full);
        Assert.Contains("报告目录：", full);
    }

    [Fact]
    public async Task Step4_InterruptedObject_AlsoOpensTheWholeReasonAndSaysTheLogIsMissing()
    {
        var ctx = CreateRealShapedJob();
        var vm = NewSession();
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        var item = FailItemOf(vm, "object-000003");

        Assert.True(item.CanShowDetail);                                  // 中断对象同样能看全文
        Assert.Contains("‖ 对象中断", item.Title);

        var full = item.FullText!;
        Assert.Contains("已中断（等待恢复）", full);
        Assert.Contains("Bulk 退出码：1（", full);
        Assert.Contains("Large 退出码：未执行（该通道没跑）", full);
        Assert.Contains("日志文件不存在", full);                            // 该对象没有日志 ⇒ 如实说明，不编造
    }

    [Fact]
    public async Task Step4_SuccessfulObject_GetsNoFailItemAtAll()
    {
        // 「完整原因」绝不能给成功对象也生成一条失败项（否则 Step4 会凭空多出"失败"）。
        var ctx = CreateRealShapedJob();
        Save(ctx, "object-000002-20261008061200", new ObjectReceipt
        {
            ObjectId = "object-000002",
            Kind = ObjectKind.DataVolume,
            SourcePath = @"\\SRC-PC\E\Project",
            TargetPath = @"E:\Project",
            Status = ObjectStatus.Completed,
            TargetBytes = 30_637_825_012,
            TargetFiles = 57777,
            RobocopyExitCodeBulk = 1,
            Attempt = 1,
        });

        var vm = NewSession();
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        Assert.DoesNotContain(vm.FailItems, f => f.FullText is not null && f.FullText.Contains("对象：object-000002"));
    }

    // ══════════ 行为级：按钮的可见性判据 ══════════

    [Fact]
    public void FailItem_ShowsTheDetailButtonOnlyWhenThereIsAFullReason()
    {
        var plain = new SessionFailItem("引擎输出", "一句话");
        Assert.Null(plain.FullText);
        Assert.False(plain.CanShowDetail);                                // 没有全文 ⇒ 不显示按钮

        var blank = new SessionFailItem("对象失败", "一句话", objectLevel: true, fullText: "   ");
        Assert.False(blank.CanShowDetail);                                // 只有空白 ⇒ 同样不显示

        var rich = new SessionFailItem("对象失败", "一句话", objectLevel: true, fullText: "完整原因");
        Assert.True(rich.CanShowDetail);
    }

    // ══════════ 契约级：页面接线（XAML 的可见性绑定 + code-behind 的弹窗） ══════════

    [Fact]
    public void Step4Page_BindsTheButtonToCanShowDetail_AndOpensAReadOnlyDialog()
    {
        var xaml = ReadRepoFile("src", "PCMig.WinUI", "Views", "Step4ResultPage.xaml");
        Assert.Contains("查看完整原因", xaml);
        Assert.Contains("CanShowDetail", xaml);                            // 可见性必须绑真实派生布尔
        Assert.Contains("Click=\"OnFailDetailClick\"", xaml);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", xaml);       // 列表本身仍是截断显示

        var code = ReadRepoFile("src", "PCMig.WinUI", "Views", "Step4ResultPage.xaml.cs");
        Assert.Contains("private async void OnFailDetailClick(", code);
        Assert.Contains("ContentDialog", code);
        Assert.Contains("item.FullText", code);
        Assert.Contains("IsTextSelectionEnabled = true", code);            // 排障要能把文本贴进工单
    }

    // ────────────────────────── 仓库根定位（与其他契约测试同一套） ──────────────────────────

    private static string ReadRepoFile(params string[] parts)
    {
        var path = Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"文件不存在：{path}");
        return File.ReadAllText(path);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到含 PCMig.sln 的仓库根：" + AppContext.BaseDirectory);
    }
}