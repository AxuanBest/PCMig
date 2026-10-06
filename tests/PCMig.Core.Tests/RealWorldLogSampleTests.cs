using System.ComponentModel;
using PCMig.Core.Preflight;
using PCMig.Core.Transfer;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// 真实日志样本回归。
///
/// 与 GuardTests 的区别：GuardTests 的输入是按 robocopy 已知输出格式【构造】的，
/// 而本文件的输入逐字摘自**真实运行的 robocopy 原始日志**，
/// 用来回答"修复后的函数在真实文本上是否真的能工作"这个问题。
///
/// 【证据来源变更 2026-09-19】
/// 此前本节自述"磁盘满(112/39)的真实原始日志未随测试报告留存"。
/// **该结论已被推翻**：真实原文一直存在于
///   C:\ProgramData\PCMig\Jobs\&lt;JobId&gt;\logs\robocopy\*.log
/// 编码为 **GBK / CP936 / 无 BOM**（用 UTF-8 读会 100% 漏检）。
/// 已按原样抄录进本文件，并镜像存档到
///   J:\pcmig-lab\fixtures\robocopy\error-*.txt（GBK 原件 + README.encoding.txt）
///
/// 【分区标注】本文件内每个用例都显式标注：
///   [真实] = 逐字摘录自真实运行日志
///   [构造] = 按已知格式书写（保留作格式佐证，不得当作真实证据）
/// </summary>
public class RealWorldLogSampleTests
{
    // ============================================================
    // 一、ExtractWin32Error（PreflightChecker）—— 预检侧
    // ============================================================

    // ---- [真实] 来源：测试报告 L82（T4 共享不存在，2026-09-15 真机）----
    // 原文：报错："源共享连接失败：\\192.0.2.23\nosuchshare_xyz（错误 67）…到旧电脑运行 net share 确认"
    [Fact]
    public void ExtractWin32Error_RealSharedMissingMessage_Returns67()
    {
        var real = new Exception(@"源共享连接失败：\\192.0.2.23\nosuchshare_xyz（错误 67）请到旧电脑运行 net share 确认");
        Assert.Equal(67, PreflightChecker.ExtractWin32Error(real));
    }

    // ---- [真实] 来源：测试报告 L112（真机日志原文片段）----
    // 原文：真机日志里那行 `错误 67 … 正在复制目标目录属性 D:\新建文件夹\` 就是它
    [Fact]
    public void ExtractWin32Error_RealRobocopyLine_Returns67()
    {
        var real = new Exception(@"2026/09/15 12:34:01 错误 67 (0x00000043) 正在复制目标目录属性 D:\新建文件夹\");
        Assert.Equal(67, PreflightChecker.ExtractWin32Error(real));
    }

    // ---- [真实·边界] 来源：测试报告 L1136（v0.3.7 的 IPC$ 刷屏原文）----
    // 这行是程序【自身输出的 Serilog 日志】（前缀 [WRN]），
    // 不是 PreflightChecker 要解析的 Exception.Message。它不含"（错误 N）"格式，
    // 因此 ExtractWin32Error 返回 -1 是【正确行为】。
    // 本用例存在的意义：防止以后有人把日志格式误当成解析输入，
    // 也防止有人为此"顺手放宽"正则而引入误判。
    [Fact]
    public void ExtractWin32Error_SelfEmittedLogLine_ReturnsMinusOne_ByDesign()
    {
        var ownLog = @"连接 \\PCMIG-NO-SUCH-HOST\IPC$ 失败: Win32Error=67 找不到网络名。";
        Assert.Equal(-1, PreflightChecker.ExtractWin32Error(new Exception(ownLog)));
    }

    // ============================================================
    // 二、IsSpaceErrorText（TransferOrchestrator）—— 磁盘满文本兜底
    //     正例：真实磁盘满日志（v0.4.6 修复的那处正则的看门狗）
    // ============================================================

    // ---- [真实] 来源：JOB-20260914-002001-5b64 / object-000001.log ----
    // 逐字原文（GBK 解码后）：
    //   2026/09/14 00:22:00 错误 112 (0x00000070) 正在复制文件 \\192.0.2.131\E$\迁移全量测试\大文件\大文件-2.bin
    //   磁盘空间不足。
    //   正在等待 5 秒... 正在重试...
    [Fact]
    public void IsSpaceErrorText_RealDiskFull112Line_ReturnsTrue()
    {
        var real = @"2026/09/14 00:22:00 错误 112 (0x00000070) 正在复制文件 \\192.0.2.131\E$\迁移全量测试\大文件\大文件-2.bin";
        Assert.True(TransferOrchestrator.IsSpaceErrorText(real));
    }

    // 真实日志中紧随错误行之后的**中文描述行**也必须能独立命中
    // （robocopy 的措辞是"磁盘空间不足。"，兜底正则必须覆盖它）
    [Fact]
    public void IsSpaceErrorText_RealDiskFull112DescriptionLine_ReturnsTrue()
    {
        Assert.True(TransferOrchestrator.IsSpaceErrorText(@"磁盘空间不足。"));
    }

    // ---- [构造] 错误 39 的真实原文未在历史日志中出现（已穷尽扫描 21 个 job）----
    // 保留作格式佐证：39 = ERROR_HANDLE_DISK_FULL，与 112 走同一判断分支。
    [Fact]
    public void IsSpaceErrorText_Constructed39Line_ReturnsTrue()
        => Assert.True(TransferOrchestrator.IsSpaceErrorText(
            @"2026/09/15 12:34:01 错误 39 (0x00000027) 正在复制文件 D:\新建文件夹\a.txt"));

    // ============================================================
    // 三、IsSpaceErrorText 反例集 —— 真实非空间错误【绝不可】被判成磁盘满
    //     这一节是"错误码误判"回归闸；全部为 [真实] 逐字原文
    // ============================================================

    // ---- [真实] 来源：JOB-20260913-130046-e486 / object-000001.log ----
    //   2026/09/13 13:03:21 错误 82 (0x00000052) 正在复制文件 \\192.0.2.131\E\迁移全量测试\海量小文件\pic_021845.jpg
    //   无法创建目录或文件。
    [Fact]
    public void IsSpaceErrorText_RealError82Line_ReturnsFalse()
    {
        var real = @"2026/09/13 13:03:21 错误 82 (0x00000052) 正在复制文件 \\192.0.2.131\E\迁移全量测试\海量小文件\pic_021845.jpg";
        Assert.False(TransferOrchestrator.IsSpaceErrorText(real));
    }

    [Fact]
    public void IsSpaceErrorText_RealError82DescriptionLine_ReturnsFalse()
        => Assert.False(TransferOrchestrator.IsSpaceErrorText(@"无法创建目录或文件。"));

    // ---- [真实] 来源：JOB-20260914-002001-5b64（网络意外错误 59）----
    [Fact]
    public void IsSpaceErrorText_RealError59Line_ReturnsFalse()
    {
        var real = @"2026/09/14 00:20:46 错误 59 (0x0000003B) 正在复制文件 \\192.0.2.131\E$\迁移全量测试\海量小文件\pic_002133.jpg";
        Assert.False(TransferOrchestrator.IsSpaceErrorText(real));
    }

    // ---- [真实] 来源：JOB-20260914-002001-5b64（系统找不到指定的文件 2）----
    [Fact]
    public void IsSpaceErrorText_RealError2Line_ReturnsFalse()
    {
        var real = @"2026/09/14 00:20:46 错误 2 (0x00000002) 正在复制文件 \\192.0.2.131\E$\迁移全量测试\海量小文件\pic_002131.jpg";
        Assert.False(TransferOrchestrator.IsSpaceErrorText(real));
    }

    // ---- [真实] robocopy 的重试提示行，绝不可被当成磁盘满 ----
    [Fact]
    public void IsSpaceErrorText_RealRetryNoticeLine_ReturnsFalse()
        => Assert.False(TransferOrchestrator.IsSpaceErrorText(@"正在等待 5 秒... 正在重试..."));

    // ---- [真实] robocopy 的"超过重试限制"收尾行（错误 82 场景里真实出现 33849 次）----
    [Fact]
    public void IsSpaceErrorText_RealRetryLimitLine_ReturnsFalse()
        => Assert.False(TransferOrchestrator.IsSpaceErrorText(@"错误: 超过重试限制。"));

    // ---- [真实] 错误 67 的 robocopy 行（目标目录属性形态，与文件复制形态不同）----
    [Fact]
    public void IsSpaceErrorText_RealError67DirAttrLine_ReturnsFalse()
        => Assert.False(TransferOrchestrator.IsSpaceErrorText(
            @"2026/09/15 12:34:01 错误 67 (0x00000043) 正在复制目标目录属性 D:\新建文件夹\"));

    // ---- [构造] 5 / 32：真实原文未在任何历史日志中出现（已穷尽扫描）----
    [Theory]
    [InlineData(@"2026/09/15 12:34:01 错误 5 (0x00000005) 正在复制文件 D:\a.txt")]
    [InlineData(@"2026/09/15 12:34:01 错误 32 (0x00000020) 正在复制文件 D:\b.txt")]
    public void IsSpaceErrorText_ConstructedNonSpaceErrors_ReturnFalse(string line)
        => Assert.False(TransferOrchestrator.IsSpaceErrorText(line));

    // ============================================================
    // 四、错误码判据（IsSpaceErrorCode）—— 与 L68 熔断正则配套
    // ============================================================

    // 熔断只认这两种码；任何其它码都必须返回 false，否则会把网络错误误熔断
    [Theory]
    [InlineData("112", true)]
    [InlineData("39", true)]
    [InlineData("0x00000070", true)]
    [InlineData("0x00000027", true)]
    [InlineData("82", false)]   // [真实] 错误 82 出现过 33849 次，绝不可熔断
    [InlineData("59", false)]   // [真实] 网络意外错误，属 Transient 而非盘满
    [InlineData("2", false)]    // [真实] 文件未找到
    [InlineData("5", false)]    // 拒绝访问
    [InlineData("67", false)]   // 找不到网络名
    public void IsSpaceErrorCode_MatchesOnlyDiskFullCodes(string code, bool expected)
        => Assert.Equal(expected, TransferOrchestrator.IsSpaceErrorCode(code));
}