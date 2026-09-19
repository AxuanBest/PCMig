using System.ComponentModel;
using PCMig.Core.Preflight;
using PCMig.Core.Transfer;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// 真实日志样本回归。
///
/// 与 GuardTests 的区别：GuardTests 的输入是按 robocopy 已知输出格式【构造】的，
/// 而本文件的输入逐字摘自 docs/测试报告-公司环境.md 里记录的真机日志，
/// 用来回答"修复后的函数在真实文本上是否真的能工作"这个问题。
///
/// 来源标注写在每个用例上，便于回溯核对。
/// </summary>
public class RealWorldLogSampleTests
{
    // ---- 来源：测试报告 L82（T4 共享不存在，2026-09-15 真机）----
    // 原文：报错："源共享连接失败：\\10.0.15.23\nosuchshare_xyz（错误 67）…到旧电脑运行 net share 确认"
    [Fact]
    public void ExtractWin32Error_RealSharedMissingMessage_Returns67()
    {
        var real = new Exception(@"源共享连接失败：\\10.0.15.23\nosuchshare_xyz（错误 67）请到旧电脑运行 net share 确认");
        Assert.Equal(67, PreflightChecker.ExtractWin32Error(real));
    }

    // ---- 来源：测试报告 L112（真机日志原文片段）----
    // 原文：真机日志里那行 `错误 67 … 正在复制目标目录属性 D:\新建文件夹\` 就是它
    [Fact]
    public void ExtractWin32Error_RealRobocopyLine_Returns67()
    {
        var real = new Exception(@"2026/09/15 12:34:01 错误 67 (0x00000043) 正在复制目标目录属性 D:\新建文件夹\");
        Assert.Equal(67, PreflightChecker.ExtractWin32Error(real));
    }

    // ---- 来源：测试报告 L1136（v0.3.7 的 IPC$ 刷屏原文）----
    // 边界记录：这行是程序【自身输出的 Serilog 日志】（前缀 [WRN]），
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

    // ---- 真实 robocopy 行：与 TransferOrchestrator 内部既有正则同等格式 ----
    // 说明：磁盘满(112/39)的真实原始日志未随测试报告留存，
    // 故此处的行按 robocopy 实际输出格式书写，并以同文件 L68 长期生效的正则为格式佐证。
    [Theory]
    [InlineData(@"2026/09/15 12:34:01 错误 112 (0x00000070) 正在复制文件 D:\新建文件夹\big.iso", true)]
    [InlineData(@"2026/09/15 12:34:01 错误 39 (0x00000027) 正在复制文件 D:\新建文件夹\a.txt", true)]
    public void IsSpaceErrorText_RealFormatRobocopyLines(string line, bool expected)
        => Assert.Equal(expected, TransferOrchestrator.IsSpaceErrorText(line));

    // 反向：真实的非空间错误行不得被误判为磁盘满
    [Theory]
    [InlineData(@"2026/09/15 12:34:01 错误 5 (0x00000005) 正在复制文件 D:\a.txt", false)]
    [InlineData(@"2026/09/15 12:34:01 错误 32 (0x00000020) 正在复制文件 D:\b.txt", false)]
    [InlineData(@"2026/09/15 12:34:01 错误 67 (0x00000043) 正在复制目标目录属性 D:\新建文件夹\", false)]
    public void IsSpaceErrorText_RealNonSpaceErrors_ReturnFalse(string line, bool expected)
        => Assert.Equal(expected, TransferOrchestrator.IsSpaceErrorText(line));
}