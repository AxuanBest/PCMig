using PCMig.Core.Jobs;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using PCMig.Core.Util;
using PCMig.Core.Verify;
using Serilog;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ P2-14 遗留项回归测试 ★（Robocopy 退出码位掩码 / 目标根可见性 / 云占位符与重解析点口径 / 源不可达不得卡死）
///
/// 这几项在整改以前只有"实现存在"没有"断言守着"：现场出现过
///   · robocopy 退出码 67 被读成成功（对象报完成、errorClass=none，但条目其实没落到目标）；
///   · 整盘迁移后目标根被 robocopy 带上源盘根的 Hidden+System（资源管理器里"看不见但空间已占"）；
///   · 云占位符/重解析点被当普通文件核对（把"没下载的占位符"算成缺失）；
///   · 指向不存在主机的共享让验证长时间卡住（而不是尽快给出"无法确认"）。
/// 本文件把这些口径逐条钉住，防止回归。
/// </summary>
[Collection("A5-jobdir-isolation")]
public sealed class P2TransferTruthTests : IDisposable
{
    private static readonly ILogger Log = new LoggerConfiguration().CreateLogger();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pcmig-p2transfer-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousJobsRoot;

    public P2TransferTruthTests()
    {
        _previousJobsRoot = Environment.GetEnvironmentVariable("PCMIG_JOBS");
        Environment.SetEnvironmentVariable("PCMIG_JOBS", Path.Combine(_root, "Jobs"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PCMIG_JOBS", _previousJobsRoot);
        try { Directory.Delete(_root, true); } catch { /* 清理失败不影响结论 */ }
    }

    private string NewDir(params string[] parts)
    {
        var p = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(p);
        return p;
    }

    // ══════════════════════════════════════════════════════════════════════
    // ① Robocopy 退出码是位掩码，不是"顺序等级"
    // ══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0)]   // 无变化
    [InlineData(1)]   // 有文件被复制
    [InlineData(2)]   // 目标有额外条目（产品从不 /MIR、/PURGE ⇒ 这是正常现象）
    [InlineData(3)]   // 1|2
    public void P2_14_只有不含不匹配位的低位退出码才算成功(int code)
        => Assert.True(RobocopyRunner.IsSuccess(code), $"退出码 {code} 应当算成功（位掩码 {code & 7}）");

    [Theory]
    [InlineData(4)]   // 不匹配：目标已有同名但类型不同的条目，robocopy 不会放上去
    [InlineData(5)]   // 1|4
    [InlineData(6)]   // 2|4
    [InlineData(7)]   // 1|2|4
    public void P2_14_含不匹配位4的退出码绝不算成功(int code)
    {
        Assert.False(RobocopyRunner.IsSuccess(code), $"退出码 {code} 含位 4（不匹配），不能算成功");
        Assert.True(RobocopyRunner.HasMismatch(code));
    }

    [Theory]
    [InlineData(8)]    // 有复制失败
    [InlineData(9)]
    [InlineData(16)]   // 严重错误（参数非法等）
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(67)]   // ★ 现场踩过的值：64|2|1 ⇒ 不得读成成功
    [InlineData(255)]
    [InlineData(-1)]   // 被我们强杀的进程 ⇒ 绝不能算成功
    public void P2_14_失败位与强杀退出码绝不算成功(int code)
        => Assert.False(RobocopyRunner.IsSuccess(code), $"退出码 {code} 不能算成功");

    [Fact]
    public void P2_14_位掩码常量与文档一致且不匹配说明可读()
    {
        Assert.Equal(1, RobocopyRunner.ExitMaskCopied);
        Assert.Equal(2, RobocopyRunner.ExitMaskExtra);
        Assert.Equal(4, RobocopyRunner.ExitMaskMismatch);
        Assert.Equal(8, RobocopyRunner.ExitMaskFailed);
        Assert.Equal(16, RobocopyRunner.ExitMaskFatal);

        var detail = RobocopyRunner.MismatchDetail(4);
        Assert.Contains("没有复制到目标", detail);
        Assert.Contains("同名但类型不同", detail);
        Assert.Contains("已复制的数据不会丢", detail);
    }

    [Fact]
    public void P2_14_运行结果的成功判定必须排除强杀()
    {
        var body = MethodBody(Source("src", "PCMig.Core", "Transfer", "RobocopyRunner.cs"),
            "Task<RobocopyRunResult> RunSingleAsync");
        Assert.Contains("var success = !killed && IsSuccess(code);", body);
        Assert.Contains("exitCode: code", body);
    }

    // ══════════════════════════════════════════════════════════════════════
    // ② 目标根目录可见性（robocopy 会把"源盘根"的 Hidden+System 写到目标根上）
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void P2_14_目标根带隐藏或系统属性时必须被解除且第二次不再报修正()
    {
        var dir = NewDir("visible-root");
        File.SetAttributes(dir, File.GetAttributes(dir) | FileAttributes.Hidden | FileAttributes.System);

        Assert.True(TargetRootGuard.EnsureVisible(dir, Log), "带 Hidden|System 的目标根必须被修正");

        var after = File.GetAttributes(dir);
        Assert.Equal(0, (int)(after & (FileAttributes.Hidden | FileAttributes.System)));
        Assert.False(TargetRootGuard.EnsureVisible(dir, Log), "已经干净时不应反复报「修正」");
    }

    [Fact]
    public void P2_14_空参数与不可写位置都不得抛异常()
    {
        Assert.False(TargetRootGuard.EnsureVisible(null));
        Assert.False(TargetRootGuard.EnsureVisible("   "));

        // 不存在的盘：必须如实返回 false，不把异常抛给调用方（迁移主流程不能被它打断）
        Assert.False(TargetRootGuard.EnsureVisible(@"Z:\pcmig-no-such-drive\root", Log));
    }

    [Fact]
    public void P2_14_目标根不存在时会被创建并保持可见()
    {
        var deep = Path.Combine(_root, "a", "b", "c");
        Assert.False(TargetRootGuard.EnsureVisible(deep, Log), "新建目录本来就不带隐藏位 ⇒ 不需要修正");
        Assert.True(Directory.Exists(deep));
        Assert.Equal(0, (int)(File.GetAttributes(deep) & (FileAttributes.Hidden | FileAttributes.System)));
    }

    [Fact]
    public void P2_14_涉及目标根的传输通道必须接上可见性保护()
    {
        var body = Source("src", "PCMig.Core", "Transfer", "TransferOrchestrator.cs");
        var count = Occurrences(body, "TargetRootGuard.EnsureVisible(job.TargetRoot, _log)");
        Assert.True(count >= 3, $"开始/根层对象/收尾都应当兜一次，实际 {count} 处");
        Assert.Contains("收尾再兜一次", body);
    }

    // ══════════════════════════════════════════════════════════════════════
    // ③ 云占位符与重解析点：口径来自矩阵策略，且枚举时跳过重解析点
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void P2_14_云占位符属性常量与默认策略不变()
    {
        Assert.Equal((FileAttributes)0x00400000, DirStat.CloudPlaceholderAttr);
        Assert.Equal("Warn", new MigrationMatrix().CloudPlaceholderPolicy);
    }

    [Fact]
    public void P2_14_四个消费方都按矩阵策略决定是否跳过占位符()
    {
        // Skip 策略必须同时进入扫描/复制/修复/验证四条路，否则会出现
        // "扫描时不算它、复制时跳过它、验证时又当缺失"的三方打架。
        Assert.Contains("AttributesToSkip = FileAttributes.ReparsePoint",
            Source("src", "PCMig.Core", "Util", "DirStat.cs"));
        Assert.Contains("string.Equals(matrix.CloudPlaceholderPolicy, \"Skip\"",
            Source("src", "PCMig.Core", "Scan", "SourceScanner.cs"));
        Assert.Contains("matrix.CloudPlaceholderPolicy.Equals(\"Skip\"",
            Source("src", "PCMig.Core", "Transfer", "RobocopyRunner.cs"));
        Assert.Contains("string.Equals(matrix.CloudPlaceholderPolicy, \"Skip\"",
            Source("src", "PCMig.Core", "Transfer", "RepairPurge.cs"));
        Assert.Contains("string.Equals(_matrix.CloudPlaceholderPolicy, \"Skip\"",
            Source("src", "PCMig.Core", "Verify", "Verifier.cs"));
    }

    [Fact]
    public void P2_14_验证与修复的枚举都跳过重解析点()
    {
        // 重解析点（junction / 符号链接）不当作数据对象：跟随它会把同一份数据核对两遍，
        // 甚至把目标侧的链接当成"缺失"或"多余"。
        var verifier = CodeOnly(Source("src", "PCMig.Core", "Verify", "Verifier.cs"));
        Assert.True(Occurrences(verifier, "(e.Attributes & FileAttributes.ReparsePoint) != 0") >= 2,
            "计数遍与流式过源两条枚举都要跳过重解析点");

        var repair = CodeOnly(Source("src", "PCMig.Core", "Transfer", "RepairPurge.cs"));
        Assert.Contains("(fi.Attributes & FileAttributes.ReparsePoint) != 0", repair);
        Assert.Contains("(e.Attributes & FileAttributes.ReparsePoint) != 0", repair);
    }

    // ══════════════════════════════════════════════════════════════════════
    // ④ 源共享不可达：尽快给出"无法确认"，且绝不卡住
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task P2_14_源共享不可达时验证快速收口为无法确认且不放行破坏性修复()
    {
        var target = NewDir("target-unreachable");
        var plan = new MigrationPlan
        {
            JobId = "plan",
            TotalBytes = 0,
            Objects =
            {
                new PlannedObject
                {
                    ObjectId = "object-000001",
                    Kind = ObjectKind.DataVolume,
                    SourcePath = @"\\pcmig-no-such-host-xyz\D",
                    TargetPath = target,
                    EstimatedFiles = 1,
                    EstimatedBytes = 1,
                },
            },
        };

        var def = new JobDefinition
        {
            JobId = "JOB-P2-" + Guid.NewGuid().ToString("N")[..8],
            SourceHost = "pcmig-no-such-host-xyz",
            TargetRoot = target,
            Sources = { new SourceSpec { Path = @"\\pcmig-no-such-host-xyz\D", Kind = ObjectKind.DataVolume } },
            Options = new MigrationOptions(),
        };
        var ctx = new JobManager(Log).Create(def);
        ctx.SavePlan(plan);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var report = await new Verifier(ctx, new MigrationMatrix(), Log)
            .RunAsync(VerifyLevel.L1_CountSize, progress: null, ct: cts.Token, detail: null);
        sw.Stop();

        var o = Assert.Single(report.Objects);
        Assert.Equal("UNKNOWN", o.Status);
        Assert.Equal("source-unavailable", o.UnknownReason);
        Assert.True(o.SourceUnavailable);
        Assert.False(o.AllowsDestructiveRepair, "无法确认绝不允许修复去删东西");
        Assert.False(report.OverallPass, "源不可达绝不等于通过");

        // 快速收口的边界：不存在的主机名必须在很短的时间内失败完事，
        // 不允许出现"验证一直挂着不动"（现场曾每个对象阻塞约 16 s）。
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60),
            $"源不可达的验证耗时 {sw.Elapsed.TotalSeconds:0.0} s，超出可接受范围");
    }

    // ══════════════════════════════════════════════════════════════════════
    // ⑤ P2-13 「传输完成」与「完整性通过」是两件事，措辞不得互相冒充
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void P2_13_传输完成的措辞不得宣称完整性通过且必须指向验证()
    {
        // 迁移报告：结论分支只陈述"迁移完成"，并且明说自己不做一致性核对。
        var report = Source("src", "PCMig.Core", "Report", "ReportGenerator.cs");
        Assert.Contains("建议回到工具点「验证」做一次数据一致性核对", report);
        Assert.Contains("本报告未做数据一致性核对", report);
        Assert.Contains("不代替「验证」的数据一致性结论", report);
        Assert.DoesNotContain("完整性通过", report);
        Assert.DoesNotContain("数据没有丢", CodeOnly(report));

        // 结果页：完成态只给"建议点验证"的下一步，不宣布数据一致。
        var vm = Source("src", "PCMig.WinUI", "Presentation", "MigrationSessionViewModel.cs");
        Assert.Contains("JobPhase.Completed => \"✔ 迁移完成！建议点「验证」确认一致性，然后「打开报告」。\"", vm);

        // 验证报告：L1 通过必须自带"不等于内容一致"的限定语（反过来也成立：验证报告不许说迁移成功）。
        var verifyReport = Source("src", "PCMig.Core", "Report", "VerifyReportGenerator.cs");
        Assert.Contains("不等于", verifyReport);
        Assert.DoesNotContain("迁移成功", CodeOnly(verifyReport));
    }

    // ══════════════════════════════════════════════════════════════════════

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PCMig.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Source(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    /// <summary>剥掉整行注释后再做"某写法不许出现"的断言（本项目已两次栽在注释上）。</summary>
    private static string CodeOnly(string text)
        => string.Join('\n', text.Split('\n').Select(l =>
        {
            var i = l.IndexOf("//", StringComparison.Ordinal);
            return i >= 0 ? l[..i] : l;
        }));

    private static string MethodBody(string text, string methodName)
    {
        var start = text.IndexOf(methodName, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到方法 {methodName}");
        // 方法体的开括号必须独占一行（缩进对齐），否则会命中参数里的 lambda / 初始化器里的花括号，
        // 截出一段完全不相干的代码（本文件首版就这么错了：拿到的是 "{ killed = true; break; }"）。
        var brace = text.IndexOf("\n    {", start, StringComparison.Ordinal);
        if (brace < 0) brace = text.IndexOf('{', start);
        brace = text.IndexOf('{', brace >= 0 ? brace : start);
        Assert.True(brace > 0, $"{methodName} 找不到方法体");
        var depth = 0;
        for (var i = brace; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0) return text[brace..(i + 1)];
            }
        }
        Assert.Fail($"{methodName} 方法体没有闭合");
        return string.Empty;
    }

    private static int Occurrences(string text, string needle)
    {
        var n = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }
}