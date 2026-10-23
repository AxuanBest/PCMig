using System;
using System.IO;
using System.Text;
using PCMig.Core.Jobs;
using PCMig.Core.Models;
using PCMig.Core.Report;
using Serilog.Core;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ v0.5.4 Preview.2 真机修复回归（2026-10-16，用户任务令 §七 Report 组 + §三）★
///
/// 真机现场（JOB-20261016-150319-e738）的报告必须能让用户**第一眼**知道
/// 「哪个盘 → 哪个文件夹 → 哪个文件 → 什么原因」：
///   sourceHost 192.0.2.26 / source \\192.0.2.26\d / targetRoot D:\
///   object-000010（RootFiles 通道）status=MISMATCH / sourceFiles 10 / targetFiles 9
///   fileDiffs[0]: diffKind=missing-on-target, relativePath=DumpStack.log,
///                 sourcePath=\\192.0.2.26\d\DumpStack.log, targetPath=D:\DumpStack.log,
///                 sourceLength=8192, targetLength=-1,
///                 evidence="L1 枚举比较：源侧有、目标侧没有"
///   迁移侧 receipts: robocopyExitCodeBulk=9, errorClass=permanent, 错误码 5 (Access Denied)
///
/// 用户令 §三 的五条硬要求（本组逐条断言）：
///   ① 迁移过程失败 ② Step4 对账发现缺失 ③ 内容级 SHA256 是否实际执行
///   ④ 数量/字节对账是否通过 ⑤ 最终整体是否可称为完整
///   且**不得**把 L1 数量/字节对账说成 SHA256 内容验证；
///   没有证据的原因必须写「原因尚未确定」，不得猜。
/// </summary>
public class Preview2ReportPlainLanguageTests : IDisposable
{
    private readonly string _dir;

    public Preview2ReportPlainLanguageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pcmig-r2-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>按真机 JOB-20261016-150319-e738 的形状构造 Job（10 对象，object-000010 缺 DumpStack.log）。</summary>
    private string BuildRealDeviceReport(bool withWin32Code, bool withFileDiff = true)
    {
        var ctx = new JobContext
        {
            JobDir = _dir,
            Definition = new JobDefinition
            {
                JobId = "JOB-20261016-150319-e738",
                SourceHost = "192.0.2.26",
                SourceUser = "administrator",
                TargetRoot = @"D:\",
                CreatedBy = "Administrator",
            },
        };
        Directory.CreateDirectory(ctx.ReceiptsDir);

        var plan = new MigrationPlan { JobId = ctx.Definition!.JobId };
        // 其余 9 个对象（真机同形，此处只保留必要字段，命名与真机一致）。
        string[] others = { "i863", "Chrome", "d969", "Creo 1.0", "proe_standard", "ProgramFilesFolder", "ptc", "ShareCache", "原电脑文件" };
        for (int i = 0; i < others.Length; i++)
        {
            plan.Objects.Add(new PlannedObject
            {
                ObjectId = $"object-{i + 1:000000}",
                Kind = ObjectKind.DataVolume,
                SourcePath = $@"\\192.0.2.26\d\{others[i]}",
                TargetPath = $@"D:\{others[i]}",
                EstimatedBytes = 1_000_000 + i,
                EstimatedFiles = 10 + i,
            });
        }
        plan.Objects.Add(new PlannedObject
        {
            ObjectId = "object-000010",
            Kind = ObjectKind.RootFiles,
            SourcePath = @"\\192.0.2.26\d",
            TargetPath = @"D:\",
            EstimatedBytes = 1_991_006_608,
            EstimatedFiles = 10,
            UseRestartablePass = true,
        });
        ctx.SavePlan(plan);

        var verify = new VerifyReport
        {
            JobId = ctx.Definition.JobId,
            Level = VerifyLevel.L1_CountSize,
            OverallPass = false,
            RunStatus = "completed",
            VerifyRunId = "20261016T074414-0e2d5b12",
            SourceHost = "192.0.2.26",
            TargetRoot = @"D:\",
            ToolVersion = "0.5.4.0",
            SampleHashPercent = 1,
        };
        verify.SelectedPaths.Add(@"\\192.0.2.26\d");

        for (int i = 0; i < others.Length; i++)
        {
            verify.Objects.Add(new ObjectVerifyResult
            {
                ObjectId = $"object-{i + 1:000000}",
                Status = "OK",
                SourceFiles = 10 + i,
                TargetFiles = 10 + i,
                SourceBytes = 1_000_000 + i,
                TargetBytes = 1_000_000 + i,
                CountMatch = true,
                BytesMatch = true,
            });
        }
        verify.Objects.Add(new ObjectVerifyResult
        {
            ObjectId = "object-000010",
            Status = "MISMATCH",
            SourceFiles = 10,
            TargetFiles = 9,
            SourceBytes = 1_991_006_608,
            TargetBytes = 1_990_998_416,
            CountMatch = false,
            BytesMatch = false,
            MissingConfirmed = true,
            AllowsDestructiveRepair = true,
        });
        verify.Objects[^1].MissingSamples.Add("DumpStack.log");

        if (withFileDiff)
        {
            verify.FileDiffs.Add(new FileDiffRecord
            {
                ObjectId = "object-000010",
                DiffKind = "missing-on-target",
                RelativePath = "DumpStack.log",
                SourcePath = @"\\192.0.2.26\d\DumpStack.log",
                TargetPath = @"D:\DumpStack.log",
                SourceLength = 8192,
                TargetLength = -1,
                Evidence = "L1 枚举比较：源侧有、目标侧没有",
                // 真机 verify-report.json 里 Win32Code 为 null（校验器只做枚举比较，拿不到迁移期的错误码）。
                Win32Code = withWin32Code ? "Win32 5 ERROR_ACCESS_DENIED" : null,
                Detail = withWin32Code ? null : null,
            });
        }

        ctx.SaveVerify(verify);
        return new VerifyReportGenerator(ctx, Logger.None).Generate()!;
    }

    private static string ReadReport(string path) => File.ReadAllText(path, Encoding.UTF8);

    // ─────────────────────────────────────────────────────────────────────
    // §七-Report-① 报告开头直接指出文件名 / 源 / 目标 / 大小 / 原因
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>报告必须真的生成出来（不是空报告）。</summary>
    [Fact]
    public void PT2_R01_ReportIsGenerated()
    {
        var html = ReadReport(BuildRealDeviceReport(withWin32Code: true));
        Assert.Contains("01 · 结论（人话）", html);
    }

    /// <summary>首屏必须点名具体文件名 —— 用户不能只看到 object-000010 与 MISMATCH。</summary>
    [Fact]
    public void PT2_R02_HumanConclusion_NamesTheMissingFile()
    {
        var html = ReadReport(BuildRealDeviceReport(withWin32Code: true));

        Assert.Contains("DumpStack.log", html);
        // 必须能看出"没有完整完成"，而不是一句冷冰冰的 MISMATCH。
        Assert.Contains("没有完整完成", html);
    }

    /// <summary>首屏必须给出源位置与目标位置（用户第一眼知道动的是哪个盘/哪个共享）。</summary>
    [Fact]
    public void PT2_R03_HumanConclusion_ShowsBothEndpoints()
    {
        var html = ReadReport(BuildRealDeviceReport(withWin32Code: true));

        Assert.Contains(@"\\192.0.2.26\d", html);
        Assert.Contains(@"D:\", html);
    }

    /// <summary>文件大小必须呈现（8,192 字节 / 8 KB），让用户知道缺的是个多大的文件。</summary>
    [Fact]
    public void PT2_R04_HumanConclusion_ShowsFileSize()
    {
        var html = ReadReport(BuildRealDeviceReport(withWin32Code: true));

        Assert.Contains("8,192", html);
    }

    /// <summary>差异类型必须翻译成人话（「源有目标无」这类），不是只丢一个英文枚举值。</summary>
    [Fact]
    public void PT2_R05_HumanConclusion_TranslatesDiffKind()
    {
        var html = ReadReport(BuildRealDeviceReport(withWin32Code: true));

        // DiffKindTag 必须把 missing-on-target 译成人话。
        Assert.DoesNotContain(">missing-on-target<", html);
    }

    // ─────────────────────────────────────────────────────────────────────
    // §七-Report-② Error 5 / Access Denied 必须正确呈现
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>报告里有错误码时，必须原样呈现，并标为「已确定」。</summary>
    [Fact]
    public void PT2_R06_Win32Code_IsShownAndMarkedCertain()
    {
        var html = ReadReport(BuildRealDeviceReport(withWin32Code: true));

        Assert.Contains("ERROR_ACCESS_DENIED", html);
        Assert.Contains("已确定", html);
    }

    // ─────────────────────────────────────────────────────────────────────
    // §七-Report-③ 无原因证据时不得猜测
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 真机 verify-report.json 里 fileDiffs[0] 的 Win32Code 是 **null**
    /// （校验器只做枚举比较，错误码 5 在迁移侧的 robocopy 日志与 receipt 里）。
    /// 此时报告**必须**写「原因尚未确定」，绝不能猜一个"权限不足"看起来合理的理由。
    /// </summary>
    [Fact]
    public void PT2_R07_NoEvidence_MustSayReasonUndetermined()
    {
        var html = ReadReport(BuildRealDeviceReport(withWin32Code: false));

        Assert.Contains("原因尚未确定", html);
    }

    /// <summary>即使没有错误码，也必须仍然点名文件（诚实：不知道原因 ≠ 不知道是哪个文件）。</summary>
    [Fact]
    public void PT2_R08_NoEvidence_StillNamesTheFile()
    {
        var html = ReadReport(BuildRealDeviceReport(withWin32Code: false));

        Assert.Contains("DumpStack.log", html);
        Assert.Contains("原因尚未确定", html);
    }

    /// <summary>有对象不通过但**没有任何文件级记录**时，必须如实说"不知道是哪个文件"，不得编造。</summary>
    [Fact]
    public void PT2_R09_NoFileDiffRecord_AdmitsItDoesNotKnowWhichFile()
    {
        var html = ReadReport(BuildRealDeviceReport(withWin32Code: true, withFileDiff: false));

        Assert.Contains("没有记录到具体是哪个文件", html);
    }

    // ─────────────────────────────────────────────────────────────────────
    // §七-Report-④ L1 count/size 验证不能写成 SHA256 已验证（五个问题分别回答）
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>用户令 §三要求报告明确区分五件事 —— 这一节必须存在。</summary>
    [Fact]
    public void PT2_R10_FiveQuestions_AreAnsweredSeparately()
    {
        var html = ReadReport(BuildRealDeviceReport(withWin32Code: true));

        Assert.Contains("五个问题分别回答", html);
        Assert.Contains("迁移过程是否失败", html);
        Assert.Contains("本次对账是否发现缺失", html);
        Assert.Contains("内容级 SHA-256 是否实际执行", html);
        Assert.Contains("数量/字节对账是否通过", html);
        Assert.Contains("最终整体是否可以称为", html);
    }

    /// <summary>
    /// L1 报告**绝不允许**让用户以为做过内容哈希：
    /// 必须明说「本轮未执行」内容级抽样，且不得出现"SHA-256 已验证/一致"这类断言。
    /// </summary>
    [Fact]
    public void PT2_R11_L1Report_MustNotClaimContentHashWasDone()
    {
        var html = ReadReport(BuildRealDeviceReport(withWin32Code: true));

        Assert.Contains("本轮未执行", html);
        // 采样数为 0 时不得出现任何"哈希一致/内容一致"的正面结论。
        Assert.DoesNotContain("内容级核对通过", html);
        Assert.DoesNotContain("哈希一致", html);
        Assert.DoesNotContain("逐字节相同", html);
    }

    /// <summary>L1 的通过句只能声称"文件数量与总字节"，并明确写出"没有做全文件内容哈希"。</summary>
    [Fact]
    public void PT2_R12_L1Verdict_OnlyClaimsCountAndBytes()
    {
        // 造一份 L1 全通过的报告作对照。
        var ctx = new JobContext
        {
            JobDir = Path.Combine(_dir, "pass"),
            Definition = new JobDefinition { JobId = "JOB-L1-PASS", SourceHost = "PC", TargetRoot = @"E:\" },
        };
        Directory.CreateDirectory(ctx.ReceiptsDir);
        ctx.SavePlan(new MigrationPlan { JobId = "JOB-L1-PASS" });
        var v = new VerifyReport
        {
            JobId = "JOB-L1-PASS", Level = VerifyLevel.L1_CountSize,
            OverallPass = true, RunStatus = "completed", ToolVersion = "0.5.4.0",
            SourceHost = "PC", TargetRoot = @"E:\",
        };
        v.Objects.Add(new ObjectVerifyResult
        {
            ObjectId = "object-000001", Status = "OK",
            SourceFiles = 5, TargetFiles = 5, SourceBytes = 500, TargetBytes = 500,
            CountMatch = true, BytesMatch = true,
        });
        ctx.SaveVerify(v);

        var html = ReadReport(new VerifyReportGenerator(ctx, Logger.None).Generate()!);

        Assert.Contains("文件数量与总字节", html);
        Assert.Contains("没有", html);
        Assert.Contains("全文件内容哈希", html);
    }

    /// <summary>⑤ 最终整体是否完整：有缺失时必须明确写「不可以」。</summary>
    [Fact]
    public void PT2_R13_FinalCompleteness_MustBeNo()
    {
        var html = ReadReport(BuildRealDeviceReport(withWin32Code: true));

        Assert.Contains("不可以", html);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 不回归：既有的「查看完整原因」失败清单（源/目标/完整路径/错误码/时间/重试历史）
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>05 表（文件级差异清单）必须仍在，且带完整路径列 —— 用户令 §三 明确要求不许回退。</summary>
    [Fact]
    public void PT2_R14_FileDiffTable_StillPresent()
    {
        var html = ReadReport(BuildRealDeviceReport(withWin32Code: true));

        Assert.Contains(@"\\192.0.2.26\d\DumpStack.log", html);
        Assert.Contains(@"D:\DumpStack.log", html);
    }

    /// <summary>报告里必须能追溯到迁移侧证据（09 表 / receipts），不把两处事实混为一谈。</summary>
    [Fact]
    public void PT2_R15_MigrationEvidenceCrossReference_StillPresent()
    {
        var html = ReadReport(BuildRealDeviceReport(withWin32Code: true));

        Assert.Contains("迁移报告", html);
    }
}
