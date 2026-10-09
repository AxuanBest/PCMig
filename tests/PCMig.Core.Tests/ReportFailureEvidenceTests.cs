using System.Globalization;
using System.Text;
using PCMig.Core.Jobs;
using PCMig.Core.Models;
using PCMig.Core.Report;
using PCMig.Core.State;
using PCMig.Core.Util;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// 失败证据（P1 域）的行为级回归。全部围绕真机 JOB-20261008-140340-6310 的现场形状：
///   · robocopy 退出码 9（= 1 有文件被复制 + 8 有文件复制失败），旧文案只说"位掩码 9"；
///   · 三个 robocopy 日志里**一条文件级错误行都没有**，失败位来自汇总表的"失败 1"（目录级）；
///   · 回执里那条"原因"其实是一条普通『新文件』状态行，旧报告把它当失败原因贴出来；
///   · object-000003 被标 interrupted，旧报告口径（只认 Failed/CompletedWithErrors）把它整段漏掉。
/// 这些断言锁的就是"报告必须诚实"这条产品口径，而不是某段实现的形状。
/// </summary>
public class ReportFailureEvidenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pcmig-report-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _robo;

    public ReportFailureEvidenceTests()
    {
        _robo = Path.Combine(_dir, "logs", "robocopy");
        Directory.CreateDirectory(_robo);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 独占句柄可能延迟释放 */ }
    }

    private void WriteLog(string objectId, string content, Encoding? encoding = null)
        => File.WriteAllText(Path.Combine(_robo, objectId + ".log"), content, encoding ?? new UTF8Encoding(false));

    /// <summary>真机 object-000001 日志的形状：汇总表（失败 1）+ 一条普通『新文件』状态行，没有任何错误行。</summary>
    private const string RealShapedLog =
        "------------------------------------------------------------------------------\n" +
        "               总数          复制        跳过       不匹配       失败         其他\n" +
        "   目录:         1505          1505           0           0           1           0\n" +
        "   文件:        27835         27816          19           0           0           0\n" +
        "   字节:       89.307g       3.945g      85.361g           0           0           0\n" +
        "   时间:  0:31:07   0:01:02                       0:00:00   0:00:02\n" +
        "\n" +
        "\t    新文件    \t\t    9934\t\\\\SRC-PC\\E\\PCMigLab\\Evidence\\tv06-error67-text-r1-tv06-error67-text.txt\n";

    private const string RecordedReason =
        @"\\SRC-PC\E\PCMigLab\Evidence\tv06-error67-text-r1-tv06-error67-text.txt";

    // ══════════ 退出码文案（D4-1） ══════════

    [Fact]
    public void ExitCodeText_NineSpellsOutBothBitsInsteadOfDroppingToBitmaskNine()
    {
        var text = ErrorTranslator.ExitCodeText(9);

        Assert.Contains("位掩码 9", text);
        Assert.Contains("1=有文件被复制", text);
        Assert.Contains("8=", text);
        Assert.Contains("+", text);
    }

    [Fact]
    public void ExitCodeText_KeepsDedicatedWordingAndDecomposesOnlyTheOthers()
    {
        Assert.DoesNotContain("位掩码", ErrorTranslator.ExitCodeText(8));
        Assert.DoesNotContain("位掩码", ErrorTranslator.ExitCodeText(16));

        var combined = ErrorTranslator.ExitCodeText(24);      // 8 + 16
        Assert.Contains("位掩码 24", combined);
        Assert.Contains("8=", combined);
        Assert.Contains("16=", combined);
    }

    [Fact]
    public void ExitCodeText_ShowsUnknownBitsSoDiagnosisStaysPossible()
    {
        Assert.Contains("未知位 64", ErrorTranslator.ExitCodeText(9 | 64));   // 64 不是 robocopy 定义的位
    }

    // ══════════ 采集器：诚实、有界、只读 ══════════

    [Fact]
    public void Collector_CapturesTheSummaryTableWhenNoFileLevelErrorLineExists()
    {
        WriteLog("object-000001", RealShapedLog);

        var ev = FailureEvidenceCollector.Collect(_robo, "object-000001");

        Assert.True(ev.LogFound);
        Assert.Equal(0, ev.ErrorLineCount);                                   // 日志里真的没有错误行
        Assert.Contains(ev.Lines, l => l.Kind == "summary" && l.Raw.Contains("目录:"));
        Assert.Contains("失败", string.Join("\n", ev.Lines.Select(l => l.Raw)));  // 唯一证据在这张表里
        Assert.DoesNotContain(ev.Lines, l => l.Kind is "file" or "code");
    }

    [Fact]
    public void Collector_SaysSoHonestlyInsteadOfBorrowingAPlainLineAsTheReason()
    {
        WriteLog("object-000001", RealShapedLog);

        var text = FailureEvidenceCollector.Collect(_robo, "object-000001").NoEvidenceText();

        Assert.Contains("没有一条文件级错误行", text);
        Assert.Contains("汇总表", text);
        Assert.Contains("不再举例子", text);
    }

    [Fact]
    public void Collector_MissingLogIsReportedAsMissingAndNeverThrows()
    {
        var ev = FailureEvidenceCollector.Collect(_robo, "object-000009");

        Assert.False(ev.LogFound);
        Assert.Equal(0, ev.TotalLines);
        Assert.Null(ev.ReadError);
        Assert.Contains("日志文件不存在", ev.NoEvidenceText());
    }

    [Fact]
    public void Collector_ReconcilesTheRecordedReasonAgainstTheLog()
    {
        WriteLog("object-000001", RealShapedLog);

        var ev = FailureEvidenceCollector.Collect(_robo, "object-000001", RecordedReason);

        Assert.True(ev.HintAppearsInLog);
        Assert.True(ev.HintMentions >= 1);
        Assert.True(ev.HintIsNotAnError);                 // 它在日志里是普通状态行 ⇒ 不是失败原因
        Assert.Contains(ev.HintLines, l => l.Raw.Contains("新文件"));
    }

    [Fact]
    public void Collector_KeepsTheWholeLongChinesePathWithoutEllipsis()
    {
        var longPath = @"\\SRC-PC\E\系统ISO和Vm安装包\一个很长很长的中文子目录名字再来一段" +
                       @"\另一个同样很长的中文子目录名称\还有第三层中文目录名\final-file-name-should-survive-1234567890.iso";
        WriteLog("object-000003", "\t错误 32 (0x00000020) 正在复制文件 " + longPath + "\r\n");

        var ev = FailureEvidenceCollector.Collect(_robo, "object-000003");
        var line = Assert.Single(ev.Lines, l => l.Kind == "file");

        Assert.Equal("32", line.Code);
        Assert.Equal(longPath, line.Path);                // 旧实现 80 字符截断会在这里失败
        Assert.DoesNotContain("…", line.Path);
        Assert.True(longPath.Length > 80);
    }

    [Fact]
    public void Collector_DropsCredentialLookingLinesButStillCountsThem()
    {
        WriteLog("object-000005", "\t错误 5 (0x00000005) 正在复制文件 \\\\OLD-PC\\D$\\密码备份.txt\r\n");

        var ev = FailureEvidenceCollector.Collect(_robo, "object-000005");

        Assert.Equal(1, ev.ErrorLineCount);               // 真实计数如实
        Assert.DoesNotContain(ev.Lines, l => l.Raw.Contains("密码"));   // 但内容绝不进报告
        Assert.Equal(1, ev.DroppedLines);                 // 被丢弃这件事依然可审计
    }

    [Fact]
    public void Collector_DedupesRetryRowsInTheHistogramButKeepsTheRealCount()
    {
        var line = "\t错误 32 (0x00000020) 正在复制文件 \\\\OLD-PC\\D$\\tmp\\locked.docx\r\n";
        WriteLog("object-000002", line + "\t正在重试...\r\n" + line);

        var ev = FailureEvidenceCollector.Collect(_robo, "object-000002");
        var code = Assert.Single(ev.Codes);

        Assert.Equal(2, ev.ErrorLineCount);
        Assert.Equal("32", code.Code);
        Assert.Equal(1, code.Count);                      // 同一路径同一码的重试行只算一次
    }

    [Fact]
    public void Collector_ReadsGbkLogLikeTheRealMachine()
    {
        if (CultureInfo.CurrentCulture.TextInfo.OEMCodePage != 936)
        {
            // 真机是中文 Windows（OEM 936）。换到别的代码页上，"GBK 字节 + OEM 兜底"这个组合本身不成立，
            // 断言会变成对测试机环境的断言 —— 那没有意义。
            return;
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        WriteLog("object-000004",
            "\t错误 67 (0x00000043) 正在复制文件 \\\\旧电脑\\D$\\中文目录\\报表.xlsx\r\n",
            Encoding.GetEncoding(936));

        var ev = FailureEvidenceCollector.Collect(_robo, "object-000004");
        var line = Assert.Single(ev.Lines, l => l.Kind == "file");

        Assert.Equal(1, ev.ErrorLineCount);
        Assert.Equal("67", Assert.Single(ev.Codes).Code);
        Assert.Contains("中文目录", line.Path);            // 没有按 OEM 代码页解出来的话，这里会是乱码
    }

    // ══════════ 报告端到端（真机形状） ══════════

    private JobContext BuildRealShapedJob()
    {
        var ctx = new JobContext
        {
            JobDir = _dir,
            Definition = new JobDefinition
            {
                JobId = "JOB-T",
                SourceHost = "SRC-PC",
                SourceUser = @"SRC-PC\User",
                TargetRoot = @"E:\",
                CreatedBy = "User"
            }
        };
        Directory.CreateDirectory(ctx.ReceiptsDir);

        var plan = new MigrationPlan { JobId = "JOB-T" };
        plan.Objects.Add(new PlannedObject
        {
            ObjectId = "object-000001",
            Kind = ObjectKind.DataVolume,
            SourcePath = @"\\SRC-PC\E\PCMigLab",
            TargetPath = @"E:\PCMigLab",
            EstimatedBytes = 95_893_239_032,
            EstimatedFiles = 27835,
            UseRestartablePass = true
        });
        plan.Objects.Add(new PlannedObject
        {
            ObjectId = "object-000003",
            Kind = ObjectKind.DataVolume,
            SourcePath = @"\\SRC-PC\E\系统ISO和Vm安装包",
            TargetPath = @"E:\系统ISO和Vm安装包",
            EstimatedBytes = 35_280_617_760,
            EstimatedFiles = 17,
            UseRestartablePass = true
        });
        ctx.SavePlan(plan);
        ctx.SaveState(new JobState
        {
            JobId = "JOB-T",
            Phase = JobPhase.CompletedWithErrors,
            TotalObjects = 3,
            CompletedObjects = 2,
            FailedObjects = 1,
            TotalBytes = 161_811_681_804,
            CompletedBytes = 70_155_088_396,
            Percent = 43.356
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
            RobocopyExitCodeBulk = 9,
            ErrorClass = ErrorClass.Permanent,
            ErrorDetail = "\t    新文件    \t\t    9934\t" + RecordedReason,
            Attempt = 1
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
            Attempt = 1
        });
        return ctx;
    }

    private static void Save(JobContext ctx, string fileName, ObjectReceipt receipt)
        => JsonStateStore.WriteAtomic(Path.Combine(ctx.ReceiptsDir, fileName + ".json"), receipt);

    private string GenerateRealShapedHtml()
    {
        var ctx = BuildRealShapedJob();
        WriteLog("object-000001", RealShapedLog);
        // 注意：ReportGenerator.Generate() 的返回值是**报告文件路径**，不是 HTML 文本。
        var path = new ReportGenerator(ctx, Serilog.Core.Logger.None).Generate();
        return File.ReadAllText(path, Encoding.UTF8);
    }

    [Fact]
    public void Report_ExplainsExitNineAndNoLongerDropsTheInterruptedObject()
    {
        var html = GenerateRealShapedHtml();

        Assert.Contains("位掩码 9", html);
        Assert.Contains("1=有文件被复制", html);
        Assert.Contains("id='ev-object-000001'", html);
        Assert.Contains("id='ev-object-000003'", html);      // 中断对象必须进报告（旧口径漏掉它）
        Assert.Contains("系统ISO和Vm安装包", html);
        Assert.Contains("未经日志核实", html);
        Assert.Contains("它不是错误行", html);
    }

    [Fact]
    public void Report_ErrorColumnShowsVerifiableExitCodeAndNeverPastesThePlainStatusLine()
    {
        var html = GenerateRealShapedHtml();

        var table = html[html.IndexOf("<h2>对象明细</h2>", StringComparison.Ordinal)..];
        table = table[..table.IndexOf("<h2>用户须知", StringComparison.Ordinal)];

        Assert.Contains("位掩码 9", table);                   // 可核实的退出码含义
        Assert.Contains("#ev-object-000001", table);          // 指向证据区的深链
        Assert.DoesNotContain("新文件", table);               // 旧版把这条普通行贴在"错误"列
    }

    [Fact]
    public void Report_StatesPlainlyWhenTheLogHasNoPerFileEvidence()
    {
        var html = GenerateRealShapedHtml();

        Assert.Contains("没有一条文件级错误行", html);
        Assert.Contains("不再举例子", html);
    }

    // ---- 平均速度口径（P1-B：报告里的"平均速度"语义要准确） ----

    /// <summary>
    /// 两趟各传 10 s，中间隔了 100 s 的空白（等待审阅/暂停/被强杀后等待续传）。
    /// 实际在传时间 = 20 s ⇒ 20 MiB / 20 s = 1 MB/s。
    /// 旧口径拿 最早开始→最晚完成（120 s）当分母，只会得到 ~170 KB/s 这种"把空白算成传输"的数字。
    /// </summary>
    [Fact]
    public void Report_AverageSpeedDividesByRealTransferTimeNotByWallClockSpan()
    {
        var ctx = BuildTwoPassJobWithIdleGap();
        var path = new ReportGenerator(ctx, Serilog.Core.Logger.None).Generate();
        var html = File.ReadAllText(path, Encoding.UTF8);

        Assert.Contains("平均传输速度", html);
        Assert.Contains("1 MB/s", html);                       // 20 MiB ÷ 20 s（并集口径）
        Assert.Contains("不是网卡实测速率", html);               // 口径必须自己说清楚，不然用户会拿去对任务管理器
        Assert.Contains("任务跨度", html);                       // 墙钟跨度如实另列，不藏起来
        Assert.Contains("2 分钟", html);                        // 10 + 100 + 10 = 120 s
    }

    [Fact]
    public void Report_OverlappingPassesAreCountedOnceInTheAverageSpeed()
    {
        var ctx = BuildOverlappingJob();
        var path = new ReportGenerator(ctx, Serilog.Core.Logger.None).Generate();
        var html = File.ReadAllText(path, Encoding.UTF8);

        // 两个对象各跑 60 s 且完全重叠 ⇒ 在传时间 60 s（不是 120 s），4 MiB/60 s ≈ 68.27 KB/s
        Assert.Contains("68.27 KB/s", html);
        Assert.DoesNotContain("34.13 KB/s", html);           // 旧口径把重叠的两趟各算一遍
    }

    private JobContext BuildTwoPassJobWithIdleGap()
    {
        var ctx = NewSpeedJob(20_971_520);
        var t0 = new DateTime(2026, 10, 8, 6, 0, 0, DateTimeKind.Utc);
        SaveSpeedReceipt(ctx, "object-000001-pass-a", "object-000001", t0, t0.AddSeconds(10));
        SaveSpeedReceipt(ctx, "object-000001-pass-b", "object-000001", t0.AddSeconds(110), t0.AddSeconds(120));
        return ctx;
    }

    private JobContext BuildOverlappingJob()
    {
        var ctx = NewSpeedJob(4_194_304);
        var t0 = new DateTime(2026, 10, 8, 6, 0, 0, DateTimeKind.Utc);
        SaveSpeedReceipt(ctx, "object-000001-x", "object-000001", t0, t0.AddSeconds(60));
        SaveSpeedReceipt(ctx, "object-000002-x", "object-000002", t0.AddSeconds(5), t0.AddSeconds(60));
        return ctx;
    }

    private JobContext NewSpeedJob(long completedBytes)
    {
        var ctx = new JobContext
        {
            JobDir = _dir,
            Definition = new JobDefinition
            {
                JobId = "JOB-S",
                SourceHost = "SRC-PC",
                TargetRoot = @"E:\",
                CreatedBy = "User"
            }
        };
        Directory.CreateDirectory(ctx.ReceiptsDir);
        var plan = new MigrationPlan { JobId = "JOB-S" };
        plan.Objects.Add(new PlannedObject
        {
            ObjectId = "object-000001",
            Kind = ObjectKind.DataVolume,
            SourcePath = @"\\SRC-PC\E\a",
            TargetPath = @"E:\a",
            EstimatedBytes = completedBytes,
            EstimatedFiles = 2
        });
        ctx.SavePlan(plan);
        ctx.SaveState(new JobState
        {
            JobId = "JOB-S",
            Phase = JobPhase.Completed,
            TotalObjects = 1,
            CompletedObjects = 1,
            FailedObjects = 0,
            TotalBytes = completedBytes,
            CompletedBytes = completedBytes,
            Percent = 100
        });
        return ctx;
    }

    private static void SaveSpeedReceipt(JobContext ctx, string fileName, string objectId, DateTime start, DateTime end)
        => Save(ctx, fileName, new ObjectReceipt
        {
            ObjectId = objectId,
            Kind = ObjectKind.DataVolume,
            SourcePath = @"\\SRC-PC\E\a",
            TargetPath = @"E:\a",
            StartedUtc = start,
            CompletedUtc = end,
            Status = ObjectStatus.Completed,
            TargetBytes = 1_048_576,
            TargetFiles = 1,
            RobocopyExitCodeBulk = 1,
            Attempt = 1
        });

    // ══════════ 发版前收口（v0.5.3 Stable）：不许未经验证的保证 + 脱敏闸门必须封住 hint 分支 ══════════

    /// <summary>
    /// 旧结论区在"只看 incomplete、不看 verify"的分支里就写着"数据没有丢——已传部分都在"，
    /// 那是一条**报告替用户下的数据安全结论**：没跑过一致性核对也照样宣告安全。
    /// 现在报告只陈述回执与日志事实，把结论留给「验证」。
    /// </summary>
    [Fact]
    public void Report_NeverClaimsTheDataIsSafeBeforeTheUserVerifies()
    {
        var html = GenerateRealShapedHtml();

        Assert.DoesNotContain("数据没有丢", html);
        Assert.Contains("本报告未做数据一致性核对", html);
        Assert.Contains("再点「验证」确认", html);

        // 页脚同样不许把报告说成验收/审计依据
        Assert.DoesNotContain("可作为验收与审计依据", html);
        Assert.Contains("不代替「验证」的数据一致性结论", html);
    }

    /// <summary>
    /// 表单校验/回执原文里带 <c>/user:</c>、<c>password</c> 这类片段时，报告与界面共享的判定必须命中，
    /// 并且**采集器在 hint 上下文分支上也要先过闸门**（旧实现只有 Add() 过闸门，hint 行走的是另一个列表）。
    /// </summary>
    [Fact]
    public void Collector_DropsSecretLookingLinesEvenWhenTheyContainTheRecordedReason()
    {
        WriteLog("object-000001",
            "   目录:         1505          1505           0           0           1           0\n" +
            "\t    新文件    \t\t    9934\t" + RecordedReason + @" /user:SRC-PC\example-user" + "\n");

        var ev = FailureEvidenceCollector.Collect(_robo, "object-000001", RecordedReason);

        Assert.Empty(ev.HintLines);                 // 命中了原因路径，但疑似含凭据 ⇒ 内容一律不留
        Assert.Equal(1, ev.HintMentions);           // 计数仍如实（这条路径确实在日志里出现过一次）
        Assert.True(ev.DroppedLines >= 1);          // 被丢弃这件事依然可审计
        Assert.DoesNotContain("example-user", string.Join("\n", ev.Lines.Concat(ev.HintLines).Select(l => l.Raw)));
    }

    [Fact]
    public void LooksSensitive_IsTheSingleRuleSharedByReportAndUiExits()
    {
        Assert.True(FailureEvidenceCollector.LooksSensitive(@"net use \\SRC-PC\E /user:admin"));
        Assert.True(FailureEvidenceCollector.LooksSensitive("password=***"));
        Assert.True(FailureEvidenceCollector.LooksSensitive("密码：***"));

        Assert.False(FailureEvidenceCollector.LooksSensitive(RecordedReason));
        Assert.False(FailureEvidenceCollector.LooksSensitive(null));
        Assert.False(FailureEvidenceCollector.LooksSensitive(""));
    }

    [Fact]
    public void Report_RedactsAReceiptReasonThatLooksLikeCredentials()
    {
        var ctx = BuildRealShapedJob();
        Save(ctx, "object-000001-20261008060510", new ObjectReceipt
        {
            ObjectId = "object-000001",
            Kind = ObjectKind.DataVolume,
            SourcePath = @"\\SRC-PC\E\PCMigLab",
            TargetPath = @"E:\PCMigLab",
            StartedUtc = new DateTime(2026, 10, 8, 8, 59, 0, DateTimeKind.Utc),
            CompletedUtc = new DateTime(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc),
            Status = ObjectStatus.Failed,
            RobocopyExitCodeBulk = 9,
            ErrorClass = ErrorClass.Permanent,
            ErrorDetail = @"net use \\SRC-PC\E /user:example-user",
            Attempt = 2
        });

        var html = File.ReadAllText(new ReportGenerator(ctx, Serilog.Core.Logger.None).Generate(), Encoding.UTF8);

        Assert.Contains("（该行疑似含凭据，已隐去）", html);
        Assert.DoesNotContain("example-user", html);
    }
}