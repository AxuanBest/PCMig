using PCMig.Core.Jobs;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Report;
using PCMig.Core.Verify;
using Serilog;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ m03985 执行令 §UI-4 / §HTML 00–10 / T06·T07·T11·T12 ★ 验证报告（本地自包含 HTML）的行为契约。
///
/// 这份报告是**业务事实**，所以这里守的不是"有没有 HTML"，而是：
///   ① 四个必须能做出来的样本：通过 / 存在差异 / 无法确认（源不可达）/ 取消 —— 每种都要如实措辞；
///   ② 不编造：没有采集到的字段写"未采集"，不把差异样例条数当"缺失总数"，旧报告也读得开；
///   ③ L1 ≠ 内容哈希（不得宣称全文件一致），"无法确认" ≠ "缺失"，"修复结束" ≠ "已通过"；
///   ④ 大量对象（413 个）不会一次把 DOM 撑爆，且首屏之外的行可按需展开；
///   ⑤ HTML 转义：中文路径、UNC、含 &amp;&lt;&gt;"' 的文件名不得注入；
///   ⑥ 连续两轮的运行号可区分，文件名不互相覆盖（原子落盘，不留 .tmp）。
/// </summary>
[Collection("A5-jobdir-isolation")]
public sealed class P1VerifyReportTests : IDisposable
{
    private static readonly ILogger Log = new LoggerConfiguration().CreateLogger();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pcmig-p1verifyreport-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousJobsRoot;

    public P1VerifyReportTests()
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
            JobId = "JOB-P1-12R-" + Guid.NewGuid().ToString("N")[..8],
            SourceHost = "SRC-PC",
            TargetRoot = NewDir("target-root"),
            Sources = { new SourceSpec { Path = @"\\SRC-PC\D", Kind = ObjectKind.DataVolume } },
            Options = new MigrationOptions(),
        };
        var ctx = new JobManager(Log).Create(def);
        ctx.SavePlan(plan);
        return ctx;
    }

    private static MigrationPlan PlanOf(params PlannedObject[] objects)
        => new()
        {
            JobId = "plan",
            TotalBytes = objects.Sum(o => o.EstimatedBytes),
            Objects = new List<PlannedObject>(objects),
        };

    private static string Generate(JobContext ctx)
    {
        var path = new VerifyReportGenerator(ctx, Log).Generate();
        Assert.False(string.IsNullOrWhiteSpace(path), "生成器必须产出报告文件路径");
        Assert.True(File.Exists(path), "报告文件必须真的落在磁盘上：" + path);
        return path!;
    }

    // ── ① 没有结论就不生成报告（不编造空报告） ─────────────────────────────

    [Fact]
    public void P1_12_没有验证报告时不生成任何文件()
    {
        var src = NewDir("src-none");
        WriteFile(src, "a.txt", 10);
        var ctx = NewJob(PlanOf(new PlannedObject
        {
            ObjectId = "object-000001", Kind = ObjectKind.DataVolume,
            SourcePath = src, TargetPath = NewDir("tgt-none"), EstimatedBytes = 10, EstimatedFiles = 1,
        }));

        var path = new VerifyReportGenerator(ctx, Log).Generate();

        Assert.Null(path);
        Assert.False(File.Exists(ctx.VerifyPath), "生成器不得顺手造一份 verify-report.json");
        if (Directory.Exists(ctx.ReportDir))
            Assert.Empty(Directory.GetFiles(ctx.ReportDir, "verify-report-*.html"));
    }

    // ── ② 通过样本：L1 语义必须写清"不等于内容哈希" ─────────────────────────

    [Fact]
    public async Task P1_12_通过样本_写清L1口径且章节与表格齐全()
    {
        var srcA = NewDir("src-ok");
        var tgtA = NewDir("tgt-ok");
        WriteFile(srcA, "a1.txt", 100);
        WriteFile(srcA, "a2.txt", 200);
        WriteFile(tgtA, "a1.txt", 100);
        WriteFile(tgtA, "a2.txt", 200);
        var ctx = NewJob(PlanOf(new PlannedObject
        {
            ObjectId = "object-000001", Kind = ObjectKind.DataVolume,
            SourcePath = srcA, TargetPath = tgtA, EstimatedBytes = 300, EstimatedFiles = 2,
        }));

        var report = await new Verifier(ctx, new MigrationMatrix(), Log)
            .RunAsync(VerifyLevel.L1_CountSize, null, CancellationToken.None, null);
        ctx.SaveVerify(report);
        Assert.True(report.OverallPass);

        var html = File.ReadAllText(Generate(ctx));

        // 00–09 章节都在（执行令要求结构化，而不是一坨文字）
        foreach (var section in new[] { "00 ·", "01 ·", "02 ·", "03 ·", "04 ·", "05 ·", "06 ·", "07 ·", "08 ·", "09 ·" })
            Assert.Contains(section, html);

        // 至少七张真实表格（T06）
        Assert.True(CountOf(html, "<table") >= 7, "验证报告至少应有 7 张结构化表格，实际：" + CountOf(html, "<table"));

        // L1 语义：通过也只说数量/字节，不说内容一致
        Assert.Contains("文件数/字节对账通过", html);
        Assert.Contains("没有", html);
        Assert.Contains("内容哈希", html);
        Assert.Contains("不等于", html);

        // 逐对象表里有这个真实对象（数据来自引擎，不是摆样子）
        Assert.Contains("object-000001", html);
        Assert.Contains(srcA, html);

        // 运行证据：本轮真的写进了运行号与阶段耗时
        Assert.Contains(report.VerifyRunId!, html);
        Assert.Contains("目标侧枚举", html);

        // 与迁移报告严格区分：验证报告不得宣称"迁移成功"
        Assert.DoesNotContain("迁移成功", html);

        // 自包含、离线：不引用任何外部资源
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
        Assert.DoesNotContain("<script src", html);
    }

    // ── ③ 差异样本：确证缺失 / 目标多余 / 大小不同 各自可区分且不夸大 ────────

    [Fact]
    public void P1_12_差异样本_四类差异可区分且不把样例当总数()
    {
        var ctx = NewJob(PlanOf(new PlannedObject
        {
            ObjectId = "object-000002", Kind = ObjectKind.DataVolume,
            SourcePath = @"\\SRC-PC\D\docs", TargetPath = @"C:\Users\example-user\docs",
            EstimatedBytes = 4096, EstimatedFiles = 3,
        }));
        var report = new VerifyReport
        {
            JobId = ctx.JobId,
            VerifiedUtc = DateTime.UtcNow,
            Level = VerifyLevel.L1_CountSize,
            OverallPass = false,
            EvidenceCompleteness = "Complete",
            SourceHost = "SRC-PC",
            TargetRoot = ctx.Definition.TargetRoot,
            VerifyRunId = "20261012T000000-abcdef",
            StartedUtc = DateTime.UtcNow.AddSeconds(-5),
            FinishedUtc = DateTime.UtcNow,
            ElapsedMs = 5123,
            RunStatus = "completed",
            ToolVersion = "0.5.3.0",
            Objects =
            {
                new ObjectVerifyResult
                {
                    ObjectId = "object-000002", Status = "MISMATCH",
                    SourceFiles = 3, TargetFiles = 4, CountMatch = false,
                    SourceBytes = 4096, TargetBytes = 8192, BytesMatch = false,
                    SizeMismatched = 1, PlannedFiles = 3, PlannedBytes = 4096,
                    MissingSamples = { "报表.xlsx", "说明 & 备注.txt" },
                    ExtraOnTarget = { @"C:\Users\example-user\docs\多余.txt" },
                    SizeMismatchSamples = { "报表.xlsx" },
                },
            },
            FileDiffs =
            {
                new FileDiffRecord { ObjectId = "object-000002", DiffKind = "missing-on-target", RelativePath = @"报表.xlsx", SourcePath = @"\\SRC-PC\D\docs\报表.xlsx", TargetPath = @"C:\Users\example-user\docs\报表.xlsx", SourceLength = 2048, TargetLength = -1, Evidence = "L1 枚举比较" },
                new FileDiffRecord { ObjectId = "object-000002", DiffKind = "extra-on-target", RelativePath = @"多余.txt", SourcePath = @"（源侧不存在）", TargetPath = @"C:\Users\example-user\docs\多余.txt", SourceLength = -1, TargetLength = 4096, Evidence = "L1 枚举比较" },
                new FileDiffRecord { ObjectId = "object-000002", DiffKind = "size-mismatch", RelativePath = @"报表.xlsx", SourcePath = @"\\SRC-PC\D\docs\报表.xlsx", TargetPath = @"C:\Users\example-user\docs\报表.xlsx", SourceLength = 2048, TargetLength = 1024, Evidence = "长度比较" },
            },
            Stages =
            {
                new VerifyStageRecord { Stage = "源侧枚举", ElapsedMs = 40, CoveredObjects = 1, Note = "按对象累计真实耗时" },
                new VerifyStageRecord { Stage = "计数/字节对账与差异分类", ElapsedMs = 12, CoveredObjects = 1, Note = "按对象累计真实耗时" },
            },
        };
        ctx.SaveVerify(report);

        var html = File.ReadAllText(Generate(ctx));

        Assert.Contains("确证缺失", html);
        Assert.Contains("目标多余", html);
        Assert.Contains("大小不同", html);
        Assert.Contains("不自动清理", html);

        // 样例 ≠ 总数：必须把"这只是样例"讲明白，且不得出现编造出来的精确缺失总数
        Assert.Contains("样例", html);
        Assert.Contains("不是缺失总数", html);
        Assert.DoesNotContain("缺失总数 2", html);
        Assert.DoesNotContain("缺失 2 个文件", html);

        // "修复操作结束"绝不等于"已通过"
        Assert.Contains("修复操作结束", html);
        Assert.Contains("复验通过", html);
    }

    // ── ④ 无法确认样本：不是缺失、也不算通过 ────────────────────────────────

    [Fact]
    public void P1_12_源不可达样本_写成无法确认而不是缺失或通过()
    {
        var ctx = NewJob(PlanOf(new PlannedObject
        {
            ObjectId = "object-000003", Kind = ObjectKind.DataVolume,
            SourcePath = @"\\SRC-PC\D\gone", TargetPath = @"C:\Users\example-user\gone",
            EstimatedBytes = 1000, EstimatedFiles = 5,
        }));
        var report = new VerifyReport
        {
            JobId = ctx.JobId,
            VerifiedUtc = DateTime.UtcNow,
            Level = VerifyLevel.L1_CountSize,
            OverallPass = false,
            EvidenceCompleteness = "Partial",
            EvidenceNote = "有对象无法确认（源侧不可达）",
            RunStatus = "completed",
            Objects =
            {
                new ObjectVerifyResult
                {
                    ObjectId = "object-000003", Status = "UNKNOWN",
                    UnknownReason = "source-unavailable", SourceUnavailable = true,
                    SourceFiles = 0, TargetFiles = 0, SourceBytes = 0, TargetBytes = 0,
                    PlannedFiles = 5, PlannedBytes = 1000,
                    EnumerationStatus = "incomplete", EnumerationSkipped = 2,
                },
            },
            EnumerationFailures =
            {
                new EnumerationFailure { Side = "source", Path = @"\\SRC-PC\D\gone", Kind = "directory", Message = "Win32 53 找不到网络路径", AtUtc = DateTime.UtcNow },
            },
        };
        ctx.SaveVerify(report);

        var html = File.ReadAllText(Generate(ctx));

        Assert.Contains("无法确认", html);
        Assert.Contains("源侧根路径不可达", html);
        Assert.Contains("不是缺失", html);
        Assert.Contains("既不算通过", html);
        Assert.Contains("Win32 53", html);
        Assert.Contains("枚举", html);
        Assert.Contains("不完整", html);
        Assert.DoesNotContain("总体结论：通过", html);
        Assert.Contains("部分（统计可能不完整）", html);
    }

    // ── ⑤ 取消样本：没有结论，不得显示成通过 ────────────────────────────────

    [Fact]
    public void P1_12_取消样本_明确没有结论且标注运行状态()
    {
        var ctx = NewJob(PlanOf(new PlannedObject
        {
            ObjectId = "object-000004", Kind = ObjectKind.DataVolume,
            SourcePath = @"\\SRC-PC\D\bulk", TargetPath = @"C:\Users\example-user\bulk",
            EstimatedBytes = 100000, EstimatedFiles = 50,
        }));
        var report = new VerifyReport
        {
            JobId = ctx.JobId,
            VerifiedUtc = DateTime.UtcNow,
            Level = VerifyLevel.L1_CountSize,
            OverallPass = false,
            RunStatus = "canceled",
            EvidenceCompleteness = "Partial",
            EvidenceNote = "用户取消：只有部分对象的结论",
            Objects =
            {
                new ObjectVerifyResult { ObjectId = "object-000004", Status = "CANCELED" },
            },
        };
        ctx.SaveVerify(report);

        var html = File.ReadAllText(Generate(ctx));

        Assert.Contains("已取消", html);
        Assert.Contains("取消不等于通过", html);
        Assert.Contains("canceled", html);       // 运行状态如实标注
        Assert.Contains("未通过", html);          // 取消不得被渲染成通过
    }

    // ── ⑤b 取消且一个对象都没核对到时，仍要出「诚实的部分证据报告」 ──────────

    [Fact]
    public void P1_12_取消且没有任何逐对象结论时仍出诚实的部分证据报告()
    {
        var ctx = NewJob(PlanOf(new PlannedObject
        {
            ObjectId = "object-000004", Kind = ObjectKind.DataVolume,
            SourcePath = @"\\SRC-PC\D\bulk", TargetPath = @"C:\Users\example-user\bulk",
            EstimatedBytes = 100000, EstimatedFiles = 50,
        }));
        ctx.SaveVerify(new VerifyReport
        {
            JobId = ctx.JobId,
            VerifiedUtc = DateTime.UtcNow,
            Level = VerifyLevel.L1_CountSize,
            OverallPass = false,
            RunStatus = "canceled",
            EvidenceCompleteness = "Partial",
            EvidenceNote = "用户取消：引擎在对象之间安全退出，不产出逐对象结论",
            Objects = { },
        });

        var html = File.ReadAllText(Generate(ctx));

        Assert.Contains("未完成（本次运行已取消 / 未跑完）", html);
        Assert.Contains("取消不等于通过", html);
        Assert.Contains("未通过（本次运行已取消 / 未跑完：没有完整结论）", html);
        Assert.Contains("空表不等于通过", html);          // 04 空表必须解释，不能留白当通过
        Assert.Contains("没有产出任何逐对象结论", html);
        Assert.DoesNotContain("对账通过", html);            // 取消绝不能被渲染成通过
        Assert.DoesNotContain("迁移成功", html);
    }

    /// <summary>没有取消/失败标记的空报告仍然不出：那才是真·空报告（不编造文件）。</summary>
    [Fact]
    public void P1_12_没有任何逐对象结论且非取消失败时仍然不生成空报告()
    {
        var ctx = NewJob(PlanOf(new PlannedObject
        {
            ObjectId = "object-000004", Kind = ObjectKind.DataVolume,
            SourcePath = @"\\SRC-PC\D\bulk", TargetPath = @"C:\Users\example-user\bulk",
            EstimatedBytes = 100000, EstimatedFiles = 50,
        }));
        ctx.SaveVerify(new VerifyReport
        {
            JobId = ctx.JobId,
            VerifiedUtc = DateTime.UtcNow,
            Level = VerifyLevel.L1_CountSize,
            OverallPass = false,
            RunStatus = "completed",
            Objects = { },
        });

        var path = new VerifyReportGenerator(ctx, Log).Generate();

        Assert.Null(path);
        if (Directory.Exists(ctx.ReportDir))
            Assert.Empty(Directory.GetFiles(ctx.ReportDir, "verify-report-*.html"));
    }

    // ── ⑥ 转义：中文、UNC、特殊字符不注入 ──────────────────────────────────

    [Fact]
    public void P1_12_报告转义中文UNC与特殊字符不注入()
    {
        const string nasty = "财报 <2026> & \"预算\" 'v1'.xlsx";
        var ctx = NewJob(PlanOf(new PlannedObject
        {
            ObjectId = "object-000005", Kind = ObjectKind.RootFiles,
            SourcePath = @"\\SRC-PC\D\财务&审计", TargetPath = @"C:\Users\example-user\财务&审计",
            EstimatedBytes = 10, EstimatedFiles = 1,
        }));
        var report = new VerifyReport
        {
            JobId = ctx.JobId,
            VerifiedUtc = DateTime.UtcNow,
            Level = VerifyLevel.L1_CountSize,
            OverallPass = false,
            Objects =
            {
                new ObjectVerifyResult
                {
                    ObjectId = "object-000005", Status = "MISMATCH",
                    SourceFiles = 1, TargetFiles = 0, MissingSamples = { nasty },
                },
            },
            FileDiffs =
            {
                new FileDiffRecord
                {
                    ObjectId = "object-000005", DiffKind = "missing-on-target", RelativePath = nasty,
                    SourcePath = @"\\SRC-PC\D\财务&审计\" + nasty, TargetPath = @"C:\Users\example-user\财务&审计\" + nasty,
                    SourceLength = 10, TargetLength = -1, Evidence = "L1 枚举比较",
                    Detail = "测试 <script>alert(1)</script> 注入",
                },
            },
        };
        ctx.SaveVerify(report);

        var html = File.ReadAllText(Generate(ctx));

        Assert.Contains("财务&amp;审计", html);
        Assert.Contains("&lt;2026&gt;", html);
        Assert.Contains("&quot;", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
        // 中文必须原样出现（不是 \uXXXX 转义）
        Assert.Contains("存在差异", html);
    }

    // ── ⑦ 大量对象：首屏有上限、其余可按需展开、生成时间有界 ──────────────────

    [Fact]
    public void P1_12_413个对象的报告首屏有上限且生成时间有界()
    {
        var ctx = NewJob(PlanOf(new PlannedObject
        {
            ObjectId = "object-000001", Kind = ObjectKind.DataVolume,
            SourcePath = @"\\SRC-PC\D\huge", TargetPath = @"C:\Users\example-user\huge",
            EstimatedBytes = 1, EstimatedFiles = 413,
        }));
        var report = new VerifyReport
        {
            JobId = ctx.JobId,
            VerifiedUtc = DateTime.UtcNow,
            Level = VerifyLevel.L1_CountSize,
            OverallPass = false,
            RunStatus = "completed",
            VerifyRunId = "20261012T010101-huge01",
        };
        for (var i = 1; i <= 413; i++)
            report.Objects.Add(new ObjectVerifyResult
            {
                ObjectId = $"object-{i:000000}",
                Status = i <= 13 ? "OK" : "MISMATCH",
                SourceFiles = 10, TargetFiles = i <= 13 ? 10 : 9,
                MissingSamples = i <= 13 ? new List<string>() : new List<string> { $"文件{i:0000}.bin" },
            });
        ctx.SaveVerify(report);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var html = File.ReadAllText(Generate(ctx));
        sw.Stop();

        Assert.Equal(413, CountOf(html, "data-obj=\"object-"));
        // 首屏之外的 213 行带 pcmig-more ⇒ 浏览器不会一次性成型上千行
        Assert.Equal(413 - VerifyReportGenerator.ObjectRowInitialLimit, CountOf(html, "class=\"pcmig-more\""));
        Assert.Contains("显示更多", html);
        Assert.True(sw.Elapsed.TotalSeconds < 20, $"413 个对象的报告生成过慢：{sw.Elapsed.TotalSeconds:0.0} 秒");
    }

    // ── ⑧ 旧报告（没有新字段）不得被编造：写"未采集" ─────────────────────────

    [Fact]
    public void P1_12_旧报告缺字段一律写未采集而不是编造()
    {
        var ctx = NewJob(PlanOf(new PlannedObject
        {
            ObjectId = "object-000006", Kind = ObjectKind.DataVolume,
            SourcePath = @"\\SRC-PC\D\legacy", TargetPath = @"C:\Users\example-user\legacy",
            EstimatedBytes = 10, EstimatedFiles = 1,
        }));
        ctx.SaveVerify(new VerifyReport
        {
            JobId = ctx.JobId,
            VerifiedUtc = DateTime.UtcNow,
            Level = VerifyLevel.L1_CountSize,
            OverallPass = true,
            Objects = { new ObjectVerifyResult { ObjectId = "object-000006", Status = "OK", SourceFiles = 1, TargetFiles = 1, SourceBytes = 10, TargetBytes = 10 } },
        });

        var html = File.ReadAllText(Generate(ctx));

        Assert.Contains("未采集", html);
        Assert.Contains("旧报告没有运行号", html);
        Assert.Contains("这份报告由旧版本引擎产生", html);
        Assert.Contains("本轮未执行", html);
    }

    // ── ⑨ 连续两轮：运行号可区分、文件名不覆盖、不留 .tmp ────────────────────

    [Fact]
    public void P1_12_连续两轮报告可区分运行号且原子落盘不留临时文件()
    {
        var ctx = NewJob(PlanOf(new PlannedObject
        {
            ObjectId = "object-000007", Kind = ObjectKind.DataVolume,
            SourcePath = @"\\SRC-PC\D\run", TargetPath = @"C:\Users\example-user\run",
            EstimatedBytes = 10, EstimatedFiles = 1,
        }));

        VerifyReport Make(string runId) => new()
        {
            JobId = ctx.JobId, VerifiedUtc = DateTime.UtcNow, Level = VerifyLevel.L1_CountSize,
            OverallPass = true, VerifyRunId = runId, RunStatus = "completed",
            Objects = { new ObjectVerifyResult { ObjectId = "object-000007", Status = "OK", SourceFiles = 1, TargetFiles = 1, SourceBytes = 10, TargetBytes = 10 } },
        };

        ctx.SaveVerify(Make("20261012T020202-first1"));
        var first = Generate(ctx);
        ctx.SaveVerify(Make("20261012T030303-second"));
        var second = Generate(ctx);

        Assert.NotEqual(first, second);
        Assert.Contains("first1", File.ReadAllText(first));
        Assert.Contains("second", File.ReadAllText(second));
        Assert.DoesNotContain("second", File.ReadAllText(first));
        Assert.Empty(Directory.GetFiles(ctx.ReportDir, "*.tmp"));
        // 最新一轮的报告就是最后一次生成出来的那份（"打开验证报告"按数据层最新快照生成，不会误开旧版）
        Assert.True(File.GetLastWriteTimeUtc(second) >= File.GetLastWriteTimeUtc(first));
    }

    // ── ⑩ 接线契约：面板按钮 + 页面处理器 + 迁移报告语义不串 ─────────────────

    [Fact]
    public void P1_12_面板按钮与页面处理器必须接上且不冒充迁移报告()
    {
        var panelXaml = File.ReadAllText(RepoPath("src", "PCMig.WinUI", "Views", "VerificationProgressPanel.xaml"));
        var panelCode = File.ReadAllText(RepoPath("src", "PCMig.WinUI", "Views", "VerificationProgressPanel.xaml.cs"));
        var step4 = File.ReadAllText(RepoPath("src", "PCMig.WinUI", "Views", "Step4ResultPage.xaml.cs"));
        var vm = File.ReadAllText(RepoPath("src", "PCMig.WinUI", "Presentation", "MigrationSessionViewModel.cs"));

        Assert.Contains("Shell.Panel.Verification.OpenReportButton", panelXaml);
        Assert.Contains("Content=\"打开验证报告\"", panelXaml);
        // 面板自己不生成、不打开文件：只回调页面层
        Assert.Contains("_onOpenReport", panelCode);
        Assert.DoesNotContain("Process.Start", panelCode);
        // 页面层把会话的验证报告入口接上（迁移报告入口保持独立）
        Assert.Contains("panel.SetOpenReportHandler(() => session.OpenVerifyReportAsync())", step4);
        Assert.Contains("public async Task<VerifyReportOpenResult> OpenVerifyReportAsync", vm);
        Assert.Contains("new VerifyReportGenerator(ctx, jobLog).Generate()", vm);
        Assert.Contains("new ReportGenerator(ctx, jobLog).Generate()", vm);
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static string RepoPath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }
}