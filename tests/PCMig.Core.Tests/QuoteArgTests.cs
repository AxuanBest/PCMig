using PCMig.Core.Transfer;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// QuoteArg 的行为规格（78GB 生产事故回归，JOB-20260917-143154-dc9d）：
/// 尾部反斜杠必须成对加倍，否则 "D:\" 被解析成转义引号 → 引号不闭合 → 后续参数全被吞。
/// 期望值统一用普通字符串书写（\" = 引号，\\\\ = 两个反斜杠），不与 verbatim 转义纠缠。
/// </summary>
public class QuoteArgTests
{
    [Theory]
    [InlineData("D:\\",           "\"D:\\\\\"")]
    [InlineData("\\\\host\\share", "\"\\\\host\\share\"")]
    [InlineData("C:\\Program Files\\App", "\"C:\\Program Files\\App\"")]
    [InlineData("D:\\数据 目录\\文件.txt", "\"D:\\数据 目录\\文件.txt\"")]
    [InlineData("\\\\192.0.2.25\\d\\", "\"\\\\192.0.2.25\\d\\\\\"")]
    public void QuoteArg_HandlesTrailingAndInternalBackslashes(string input, string expected)
        => Assert.Equal(expected, RobocopyRunner.QuoteArg(input));

    [Fact]
    public void QuoteArg_EscapesEmbeddedQuotes()
        => Assert.Equal("\"a\\\"b\"", RobocopyRunner.QuoteArg("a\"b"));

    [Fact]
    public void QuoteArg_PlainPath_TakesNoEscapes()
        => Assert.Equal("\"simple\"", RobocopyRunner.QuoteArg("simple"));
}
