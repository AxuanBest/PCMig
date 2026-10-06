using System;
using System.Collections.Generic;
using System.IO;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using PCMig.Core.Util;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// GROUP D · D-FIX-BATCH 回归（把"数据最终正确性"这一类判定语义钉进单测）。
///
/// 【为什么存在】GROUP D 的这几个缺陷和 GROUP B 一样：编译过、界面不报、只有真机故障现场才暴露，
/// 而它们全都指向同一件事——**产品给出的判定必须与磁盘上的真实结果一致**。所以这里断言的是判定语义本身：
///
///  · <b>F1（Medium／假成功）</b>：robocopy 退出码 7 = 位1(有文件复制) + 位2(目标有额外条目) + 位4(**不匹配**)。
///    旧实现把 &lt;8 一律当成功 ⇒ "目标已有同名异型节点（D13）/重解析点（D28）导致源条目根本没复制过去"
///    这件事在回执里是 <c>Success=true / status=completed / errorClass=none</c>，界面上是「迁移完成」。
///    修复 = 不匹配位不再算成功（位2 保持成功：产品不用 /MIR、/PURGE，目标独有数据不该被判失败），
///    且不匹配必须由**退出码**直接给出中文结论（日志文本解析不到时也不能漏报）。
///  · <b>F2（Low/Medium／记录）</b>：修复重拷是同一对象的第二趟，旧实现每趟都从 attempt=1 记起，
///    作业记录里两趟完全一样 ⇒ 事后无法回答"修复到底跑了没有、跑了几趟"。
///  · <b>F4（Medium／假进度）</b>：失败尝试里记进去的字节会留在分子上，且进度夹到 100%
///    ⇒ D02 真机底栏出现 100.0% / 4.02 GB（计划只有 1.02 GB）这种"看起来快完成了"的假进度。
///  · <b>F5（Medium/Low／可操作性）</b>：任务状态目录不可写时，界面把英文 .NET 原始异常串
///    （含内部临时文件名 .json.tmp-xxxx）直接抛给用户，既没说哪个存储不可写，也没说怎么办。
///
/// 纯函数与静态契约，不触网、不起 UI、不写磁盘。
/// </summary>
public sealed class GroupDFixBatchTests
{
    // ───────────────────────── F1：退出码位掩码 ─────────────────────────

    [Theory]
    [InlineData(0)]   // 无事可做（源与目标一致）
    [InlineData(1)]   // 有文件被复制
    [InlineData(2)]   // 目标有额外条目（目标独有文件，产品不删）——必须仍是成功
    [InlineData(3)]   // 1|2：复制了 + 目标有额外条目
    public void F1_ExitCode_LowBitsWithoutMismatch_AreSuccess(int exitCode)
    {
        Assert.True(RobocopyRunner.IsSuccess(exitCode));
        Assert.False(RobocopyRunner.HasMismatch(exitCode));
    }

    [Theory]
    [InlineData(4)]    // 只有不匹配
    [InlineData(5)]    // 1|4
    [InlineData(6)]    // 2|4
    [InlineData(7)]    // 1|2|4  ← D13/D28 真机现场就是它
    [InlineData(8)]    // 有文件失败
    [InlineData(9)]    // 1|8
    [InlineData(12)]   // 4|8
    [InlineData(16)]   // 严重错误
    [InlineData(-1)]   // 被强杀（绝不允许算成功）
    public void F1_ExitCode_MismatchFailedFatalOrKilled_AreNotSuccess(int exitCode)
    {
        Assert.False(RobocopyRunner.IsSuccess(exitCode));
    }

    [Theory]
    [InlineData(4), InlineData(5), InlineData(6), InlineData(7), InlineData(12)]
    public void F1_HasMismatch_IsSetByBit4(int exitCode) => Assert.True(RobocopyRunner.HasMismatch(exitCode));

    [Theory]
    [InlineData(0), InlineData(1), InlineData(2), InlineData(3), InlineData(8), InlineData(9), InlineData(-1)]
    public void F1_HasMismatch_IsNotSetWithoutBit4(int exitCode) => Assert.False(RobocopyRunner.HasMismatch(exitCode));

    [Fact]
    public void F1_MismatchDetail_TellsTheUserWhatHappenedAndWhatToDo()
    {
        var text = RobocopyRunner.MismatchDetail(7);
        Assert.Contains("不匹配", text);
        Assert.Contains("同名", text);
        Assert.Contains("恢复任务", text);          // 必须给出可执行动作，而不是只报错
        Assert.DoesNotContain("成功", text);        // 不许出现"成功"
        Assert.DoesNotContain("is denied", text);
    }

    [Theory]
    [InlineData(4), InlineData(5), InlineData(6), InlineData(7)]
    public void F1_ExitCodeText_StopsCallingMismatchSuccess(int exitCode)
    {
        var text = ErrorTranslator.ExitCodeText(exitCode);
        Assert.DoesNotContain("成功", text);
        Assert.Contains("不匹配", text);
    }

    [Fact]
    public void F1_DetailFor_PrefersMismatchConclusionAndKeepsTheFailureSummary()
    {
        // 无失败行（类型冲突在 robocopy 输出里没有 ERROR 码）：仍必须由退出码给出结论
        var bare = TransferOrchestrator.DetailFor(new RobocopyRunResult(4, false, false, TimeSpan.Zero, null));
        Assert.NotNull(bare);
        Assert.Contains("不匹配", bare!);

        // 有失败行：不匹配结论在前，失败汇总在后，两者都不能丢
        var lines = new List<string> { "错误 32 (0x00000020) 正在复制文件 D:\\x\\a.bin" };
        var both = TransferOrchestrator.DetailFor(new RobocopyRunResult(12, false, false, TimeSpan.Zero, null, lines));
        Assert.NotNull(both);
        Assert.Contains("不匹配", both!);
        Assert.Contains("文件复制失败", both!);
        Assert.Contains("32", both!);
    }

    [Fact]
    public void F1_DetailFor_WithoutMismatch_KeepsOldBehaviour()
    {
        var lines = new List<string> { "错误 112 (0x00000070) 正在复制文件 D:\\x\\big.bin" };
        var text = TransferOrchestrator.DetailFor(new RobocopyRunResult(8, false, false, TimeSpan.Zero, null, lines));
        Assert.NotNull(text);
        Assert.Contains("磁盘空间不足", text!);
        Assert.DoesNotContain("不匹配", text!);
    }

    // ───────────────────────── F1：错误分类 ─────────────────────────

    [Fact]
    public void F1_Classify_MismatchIsPermanentEvenWhenTextLooksTransient()
    {
        // 类型冲突是静态事实：重试不可能自愈，不能被日志里的"网络/semantics"等字样误判成可恢复
        var r = new RobocopyRunResult(7, false, false, TimeSpan.Zero,
            "ERROR 59 网络意外错误 semantics 超时");
        Assert.Equal(ErrorClass.Permanent, TransferOrchestrator.Classify(r));
    }

    [Theory]
    [InlineData(8, "错误 112 (0x00000070) 目标磁盘空间不足")]   // 盘满 → 可恢复
    [InlineData(9, "ERROR 53 找不到网络路径")]                   // 网络 → 可恢复
    public void F1_Classify_KeepsTransientClassification(int exitCode, string line)
    {
        var r = new RobocopyRunResult(exitCode, false, false, TimeSpan.Zero, line, new List<string> { line });
        Assert.Equal(ErrorClass.Transient, TransferOrchestrator.Classify(r));
    }

    [Fact]
    public void F1_Classify_OtherwisePermanent()
    {
        var r = new RobocopyRunResult(8, false, false, TimeSpan.Zero, "错误 5 权限不足");
        Assert.Equal(ErrorClass.Permanent, TransferOrchestrator.Classify(r));
    }

    // ───────────────────────── F4：进度百分比 ─────────────────────────

    [Fact]
    public void F4_Percent_NeverShowsHundredWhileRunning()
    {
        var s = new JobState
        {
            Phase = JobPhase.Running,
            TotalObjects = 1,
            CompletedObjects = 0,
            TotalBytes = 1024,
            CompletedBytes = 4096,        // 记账领先真实落盘（D02 现场：分子 > 分母）
        };
        Assert.Equal(99.9, TransferOrchestrator.Percent(s));
        Assert.NotEqual(100.0, TransferOrchestrator.Percent(s));
    }

    [Theory]
    [InlineData(JobPhase.Completed)]
    [InlineData(JobPhase.CompletedWithErrors)]
    public void F4_Percent_OnlyReachesHundredWhenTheJobIsSettled(JobPhase phase)
    {
        var s = new JobState
        {
            Phase = phase, TotalObjects = 1, CompletedObjects = 1,
            TotalBytes = 1024, CompletedBytes = 1024,
        };
        Assert.Equal(100.0, TransferOrchestrator.Percent(s));
    }

    [Fact]
    public void F4_Percent_FallsBackToObjectCountWhenBytesUnknown()
    {
        var s = new JobState
        {
            Phase = JobPhase.Running, TotalBytes = 0, TotalObjects = 10, CompletedObjects = 5,
        };
        Assert.Equal(50.0, TransferOrchestrator.Percent(s));
    }

    // ───────────────────────── F5：存储写失败的人话 ─────────────────────────

    /// <summary>D03 真机原文（LAB-DST01：任务状态目录被拒绝写入）。</summary>
    private const string D03Raw =
        "Access to the path '<本机程序数据目录>\\Jobs\\JOB-20261004-082429-630b\\receipts\\" +
        "object-000005-20261004002454.json.tmp-22f1abab' is denied.";

    [Fact]
    public void F5_StatePathDenied_SaysWhereWhatAndHow_AndLeaksNoInternals()
    {
        var ui = TransferFailureTranslator.Explain(new UnauthorizedAccessException(D03Raw));

        Assert.Contains("任务状态目录", ui);                       // 哪个存储：不是笼统一句"传输异常"
        Assert.Contains(@"<本机程序数据目录>\Jobs\JOB-20261004-082429-630b\receipts", ui);
        Assert.Contains("没有写入权限", ui);
        Assert.Contains("已复制到目标的数据不会重传", ui);          // 用户最关心的：数据有没有白拷
        Assert.Contains("恢复任务", ui);                           // 怎么办

        Assert.DoesNotContain("is denied", ui);                   // 不许把英文原文抛给用户
        Assert.DoesNotContain(".tmp-", ui);                       // 不许泄漏内部临时文件名
        Assert.DoesNotContain("System.", ui);
        Assert.DoesNotContain("Exception", ui);
    }

    [Fact]
    public void F5_LogPathDenied_NamesTheLogStore()
    {
        var ui = TransferFailureTranslator.Explain(new UnauthorizedAccessException(
            "Access to the path '<本机程序数据目录>\\Logs\\pcmig-20261004.log' is denied."));
        Assert.Contains("日志目录", ui);
        Assert.DoesNotContain("is denied", ui);
    }

    [Fact]
    public void F5_DiskFull_IsActionable()
    {
        var ui = TransferFailureTranslator.Explain(new IOException("There is not enough space on the disk. (错误码 112)"));
        Assert.Contains("磁盘空间不足", ui);
        Assert.Contains("恢复任务", ui);
        Assert.DoesNotContain("not enough space", ui);
    }

    [Fact]
    public void F5_FileInUse_IsActionable()
    {
        var ui = TransferFailureTranslator.Explain(new IOException(
            "The process cannot access the file because it is being used by another process."));
        Assert.Contains("文件被占用", ui);
        Assert.Contains("恢复任务", ui);
    }

    [Fact]
    public void F5_UnknownFailure_KeepsAChineseReasonAndDoesNotEchoRawEnglish()
    {
        var ui = TransferFailureTranslator.Explain(new InvalidOperationException("Object reference not set to an instance of an object."));
        Assert.Contains("未预期的错误", ui);
        Assert.Contains("InvalidOperationException", ui);          // 类型名留给可诊断性
        Assert.DoesNotContain("Object reference", ui);             // 原始英文串不外抛
        Assert.Contains("日志", ui);
    }

    [Fact]
    public void F5_Explain_IsNotNullEvenWhenMessageIsEmpty()
    {
        var ui = TransferFailureTranslator.Explain(new Exception());
        Assert.False(string.IsNullOrWhiteSpace(ui));
    }

    [Fact]
    public void F5_ExtractPath_HandlesQuotesAndAbsence()
    {
        Assert.Equal(@"C:\a\b.json", TransferFailureTranslator.ExtractPath("Access to the path 'C:\\a\\b.json' is denied."));
        Assert.Null(TransferFailureTranslator.ExtractPath("no path here"));
        Assert.Null(TransferFailureTranslator.ExtractPath(null));
        Assert.Null(TransferFailureTranslator.ExtractPath(""));
    }

    [Fact]
    public void F5_Where_WithoutPath_StillSaysSomethingUseful()
    {
        var text = TransferFailureTranslator.Where(null);
        Assert.Contains("迁移状态/日志目录", text);
    }
}

/// <summary>
/// F11 回归（GROUP D 完整回归实测：状态行出现物理上不可能的「本次平均 341.8 TB/s」）。
///
/// 【现场】重试风暴里每一拍都会重开"本次尝试"的进度基线（F4 修复），重开那一拍
/// dt 只有几微秒，而 effBytes 里可能已经含 /Z 在途大文件的整段长度（可达 1 GiB）；
/// 照旧直接相除就把 1e14 B/s 量级写进 EMA。偏偏那一拍 CumulativeSpeed 因
/// completedBytes 被清零而返回 0，显示层回退到这个被污染的 EMA，用户于是看到 341.8 TB/s。
///
/// 【契约】速率样本只在窗口 ≥0.5 秒时产生（与 CumulativeSpeed 的最小窗口一致）；
/// 窗口过小不是"不记录"，而是"不采样"——进度单调性、停滞检测、已传字节都不受影响。
/// </summary>
public sealed class GroupDF11SpeedWindowTests
{
    private const long OneGiB = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(0.00001, OneGiB, false)]   // 事故现场：微秒窗口 + 1 GiB 在途长度
    [InlineData(0.0, 4096L, false)]
    [InlineData(-0.5, 4096L, false)]
    [InlineData(0.499, 4096L, false)]
    [InlineData(0.5, 4096L, true)]
    [InlineData(30.0, 4096L, true)]
    [InlineData(2.0, 0L, false)]           // 没有字节增长就没有速率
    [InlineData(2.0, -1L, false)]          // 回退不算增长
    public void F11_OnlySamplesWhenTheWindowIsLargeEnough(double dt, long delta, bool expected)
        => Assert.Equal(expected, TransferOrchestrator.ShouldPushSpeedSample(dt, delta));

    [Fact]
    public void F11_TheOldUnguardedDivision_IsPhysicallyImpossible()
    {
        // 把缺陷本身钉住：这组参数若照旧直接相除，得到的是 1e14 B/s 量级（≈100 TB/s）。
        var unguarded = OneGiB / 0.00001;
        Assert.True(unguarded > 1e13, "dt≈1e-5s、Δ=1GiB 就是 341.8 TB/s 这一类的来源");

        // 同一个 1 GiB，在允许的最小窗口（0.5 秒）下最多也只有 2 GiB/s 量级：物理可达。
        Assert.False(TransferOrchestrator.ShouldPushSpeedSample(0.00001, OneGiB));
        Assert.True(OneGiB / 0.5 < 2.2e9);
    }

    [Fact]
    public void F11_MinimumWindowMatchesTheCumulativeSpeedWindow()
    {
        // 显示字段 shown 与 EMA 兜底共用同一个 0.5 秒口径：小于该窗口既不产样本、也不产平均值。
        Assert.False(TransferOrchestrator.ShouldPushSpeedSample(0.49, 4096L));
        Assert.True(TransferOrchestrator.ShouldPushSpeedSample(0.50, 4096L));
    }
}

/// <summary>
/// F12（D01 r4 真机现场，2026-10-04，风险面「启动前磁盘空间不足」）：
/// 现场事实：计划 1.02 GB / 目标盘只剩 723.95 MB；1 GB 的 D26\big.bin 每次都因错误码 112 失败；
/// 回执、job-state 与磁盘真相三方一致 = 20,484,397 B / 20023 个文件；可底栏在失败期间从 1.4% 跳到
/// 98.1% 并钉在 99.9% / 1.02 GB。根因两条：
///   ① 入账键 ≠ 回冲键：/MT 重试行把 "正在重试..." 追在文件名后，解析出的路径带着这段后缀，
///      于是那个失败文件预记的 1.0737 GB 永远扣不掉（串行 /Z 尝试里是裸文件名，也进不了这张表）；
///   ② /Z（可续传）会先给目标文件预分配最终长度，stat 长度被当成已落盘字节——同一文件对
///      "回退枚举"早已写死同样的结论，却漏掉了自己这个 stat 读。
/// 【契约】robocopy 输出解析必须产出"干净路径"，入账与回冲必须共用同一处解析；
/// 可续传通道不得采信目标文件长度。这三条是"进度不许谎报"的可验证下限。
/// </summary>
public sealed class GroupDF12ProgressTruthTests
{
    private const string CleanPath = @"\\LAB-SRC01\D\D-Cases\D26\big.bin";
    private const string RetryErrorLine =
        @"2026/10/04 11:30:21 错误 112 (0x00000070) 正在复制文件 \\LAB-SRC01\D\D-Cases\D26\big.bin";

    [Fact]
    public void F12_RetryMarkerIsNotPartOfTheFileName()
    {
        Assert.Equal(CleanPath, RobocopyRunner.NormalizeFileName(CleanPath + " 正在重试..."));
        Assert.Equal(@"big.bin", RobocopyRunner.NormalizeFileName("big.bin 正在重试..."));
        Assert.Equal(@"big.bin", RobocopyRunner.NormalizeFileName("big.bin Retrying..."));
        Assert.Equal(@"big.bin", RobocopyRunner.NormalizeFileName("big.bin 正在重试"));
    }

    [Fact]
    public void F12_CleanNamesArePassedThroughUntouched()
    {
        Assert.Equal(CleanPath, RobocopyRunner.NormalizeFileName(CleanPath));
        Assert.Equal(@"big.bin", RobocopyRunner.NormalizeFileName("big.bin"));
        Assert.Equal(string.Empty, RobocopyRunner.NormalizeFileName("   "));
        // 只剥 robocopy 自己加在**末尾**的重试标记；文件名中间出现同样文字必须原样保留。
        Assert.Equal(@"a正在重试b.bin", RobocopyRunner.NormalizeFileName("a正在重试b.bin"));
    }

    [Fact]
    public void F12_CreditKeyAndRetractKeyAreTheSameString()
    {
        // 这就是根因本身：入账时用的名字（重试行，带后缀）必须与回冲时用的名字（错误行，干净路径）相等。
        var credited = RobocopyRunner.NormalizeFileName(CleanPath + " 正在重试...");
        Assert.True(RobocopyRunner.TryParseFileErrorLine(RetryErrorLine, out var code, out var srcPath));
        Assert.Equal("112", code);
        Assert.Equal(credited, srcPath);
    }

    [Fact]
    public void F12_NonErrorLinesAreNotMistakenForFileErrors()
    {
        Assert.False(RobocopyRunner.TryParseFileErrorLine("新文件  1.0 g  " + CleanPath, out _, out _));
        Assert.False(RobocopyRunner.TryParseFileErrorLine(
            @"2026/10/04 11:30:16 错误 3 (0x00000003) 正在创建目录 \\LAB-SRC01\D\D-Cases\D27\", out _, out _));
    }

    [Fact]
    public void F12_TargetFileLengthIsNeverConfirmedBytes()
    {
        // /Z 与 /J 都会先给目标文件预分配最终长度（D01 现场：1.0 g 的文件 6 秒内就"看起来传完了"），
        // 所以两条通道都不许把目标文件长度当作已确认落盘字节。
        Assert.False(RobocopyRunner.TrustsTargetStatForProgress(restartableSerialPass: true));
        Assert.False(RobocopyRunner.TrustsTargetStatForProgress(restartableSerialPass: false));
    }

    [Fact]
    public void F12f_AnnouncementIsCreditedOnlyOncePerFileAndNeverAfterAFailure()
    {
        // 第一次"新文件"行：入账
        Assert.True(RobocopyRunner.ShouldCreditAnnouncement(creditedThisPass: false, failedThisPass: false));
        // robocopy 内部重试又报了一次同一个文件（"正在重试..."）：不得重复入账
        Assert.False(RobocopyRunner.ShouldCreditAnnouncement(creditedThisPass: true, failedThisPass: false));
        // 已经报过错误码 112 的文件：即使错行已经把预记字节回冲掉（键被移除），重试行也不得再入账
        Assert.False(RobocopyRunner.ShouldCreditAnnouncement(creditedThisPass: false, failedThisPass: true));
        Assert.False(RobocopyRunner.ShouldCreditAnnouncement(creditedThisPass: true, failedThisPass: true));
    }
}

/// <summary>
/// F13（D03 真机现场，2026-10-04，Final Critical Matrix 相 D）：**准备阶段**的失败文案
/// 把原始 .NET 英文异常直接抛给用户：
///   `准备失败：Access to the path '<本机程序数据目录>\Jobs\JOB-20261004-125525-e73a\receipts' is denied.`
/// F5 只修了传输路径（TransferOrchestrator 的写回执失败），Prepare 路径（MigrationSessionViewModel
/// 的 catch）漏掉了——同一个产品面家族的同类缺陷。
///
/// 【契约】准备阶段的失败文案：①中文、可执行（说清哪个位置不可写 + 怎么办）；
/// ②不出现英文原始串 / 内部临时文件名 / .NET 类型名；
/// ③**不得照搬传输路径的话术**——此刻没有任务也没有已复制数据，说"任务已停止 / 已复制到目标的数据不会重传"
///   是另一种不诚实。
/// </summary>
public sealed class GroupDF13PrepareMessageTests
{
    private const string D03PrepareRaw =
        "Access to the path '<本机程序数据目录>\\Jobs\\JOB-20261004-125525-e73a\\receipts' is denied.";

    [Fact]
    public void F13_PrepareDenied_SaysWhereAndWhatToDo()
    {
        var ui = TransferFailureTranslator.ExplainPrepare(new UnauthorizedAccessException(D03PrepareRaw));

        Assert.Contains("任务状态目录", ui);
        // 位置说到"哪个任务的状态目录"即可：Where() 取的是该路径的父目录（D03 真机那条引号里本身就是
        // …\JOB-…\receipts 目录，再往上才是任务目录），所以断言到 JOB 目录这一层，不要求尾部 \receipts。
        Assert.Contains(@"<本机程序数据目录>\Jobs\JOB-20261004-125525-e73a", ui);
        Assert.Contains("没有写入权限", ui);
        Assert.Contains("准备", ui);                       // 怎么办：重新点「准备」

        Assert.DoesNotContain("is denied", ui);            // 不许把英文原文抛给用户
        Assert.DoesNotContain("Access to the path", ui);
        Assert.DoesNotContain("Exception", ui);
        Assert.DoesNotContain("System.", ui);
    }

    [Fact]
    public void F13_PrepareMessageDoesNotClaimThatATaskWasStopped()
    {
        // 传输路径的话术（"任务已停止 / 已复制到目标的数据不会重传"）在准备阶段是不成立的：
        // 那时既没有任务、也没有任何已复制数据。照搬就是换一种谎报。
        var ui = TransferFailureTranslator.ExplainPrepare(new UnauthorizedAccessException(D03PrepareRaw));
        Assert.DoesNotContain("任务已停止", ui);
        Assert.DoesNotContain("不会重传", ui);
        Assert.DoesNotContain("恢复任务", ui);
        Assert.Contains("没有开始传输", ui);
    }

    [Fact]
    public void F13_PrepareDiskFullAndSharingAreActionable()
    {
        var full = TransferFailureTranslator.ExplainPrepare(
            new IOException("There is not enough space on the disk. (错误码 112)"));
        Assert.Contains("磁盘空间不足", full);
        Assert.Contains("准备", full);
        Assert.DoesNotContain("not enough space", full);

        var busy = TransferFailureTranslator.ExplainPrepare(
            new IOException("The process cannot access the file because it is being used by another process."));
        Assert.Contains("文件被占用", busy);
        Assert.Contains("准备", busy);
    }

    [Fact]
    public void F13_PrepareUnknownFailure_KeepsAChineseReasonAndNoRawEnglish()
    {
        var ui = TransferFailureTranslator.ExplainPrepare(new InvalidOperationException("Object reference not set to an instance of an object."));
        Assert.Contains("未预期的错误", ui);
        Assert.Contains("InvalidOperationException", ui);   // 类型名留给可诊断性
        Assert.DoesNotContain("Object reference", ui);      // 原始英文串不外抛
        Assert.Contains("日志", ui);
    }

    [Fact]
    public void F13_PrepareMessageIsNeverEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(TransferFailureTranslator.ExplainPrepare(new Exception())));
        Assert.False(string.IsNullOrWhiteSpace(TransferFailureTranslator.ExplainPrepare(
            new UnauthorizedAccessException())));
    }
}