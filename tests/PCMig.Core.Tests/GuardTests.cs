using System.ComponentModel;
using PCMig.Core.Preflight;
using PCMig.Core.Transfer;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// v0.4.5 暗雷回归：IsSpaceErrorText 的正则曾有 "\s"→"s"、"\b"→0x08 的转义丢失，
/// 编译通过但永远匹配不上。这些用例是看门狗——正则再退化，这里必须红。
/// </summary>
public class SpaceErrorGuardTests
{
    [Theory]
    [InlineData("错误 112 (0x00000070) 正在复制文件 D:\\big.iso", true)]
    [InlineData("ERROR 112 (0x00000070) copying file", true)]
    [InlineData("错误 39 (0x00000027) 正在复制文件 \\\\h\\s\\a.txt", true)]
    [InlineData("0x00000070", true)]
    [InlineData("磁盘空间不足", true)]
    [InlineData("磁盘已满", true)]
    [InlineData("insufficient disk space", true)]
    [InlineData("ERROR 32 (0x00000020) 正在复制文件 D:\\a.txt", false)]
    [InlineData("错误 5 (0x00000005) 正在复制文件 D:\\a.txt", false)]
    [InlineData("ERROR 123", false)]
    public void IsSpaceErrorText_ClassifiesRealRobocopyLines(string line, bool expected)
        => Assert.Equal(expected, TransferOrchestrator.IsSpaceErrorText(line));

    [Theory]
    [InlineData("112", true)]
    [InlineData("39", true)]
    [InlineData("0x00000070", true)]
    [InlineData("0x70", true)]
    [InlineData("0x27", true)]
    [InlineData("5", false)]
    [InlineData(null, false)]
    public void IsSpaceErrorCode_MatchesKnownCodes(string? code, bool expected)
        => Assert.Equal(expected, TransferOrchestrator.IsSpaceErrorCode(code));

    /// <summary>PreflightChecker.ExtractWin32Error 的消息兜底分支（"（错误 N）"格式）。</summary>
    [Fact]
    public void ExtractWin32Error_ReadsFallbackMessage()
        => Assert.Equal(67, PreflightChecker.ExtractWin32Error(new Exception("连接失败（错误 67）")));

    [Fact]
    public void ExtractWin32Error_ReadsWin32ExceptionType()
        => Assert.Equal(5, PreflightChecker.ExtractWin32Error(new Win32Exception(5)));

    [Fact]
    public void ExtractWin32Error_ReturnsMinusOneForNull()
        => Assert.Equal(-1, PreflightChecker.ExtractWin32Error(null));
}
