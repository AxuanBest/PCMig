using PCMig.Core.Transfer;
using Xunit;

namespace PCMig.Core.Tests;

public class RoboParsingTests
{
    [Theory]
    [InlineData("669.5", "m", 669.5 * 1048576)]
    [InlineData("2.0", "g", 2147483648)]
    [InlineData("512", "", 512)]
    [InlineData("1,024", "", 1024)]
    public void ParseRoboSize_ParsesLocalizedUnits(string num, string unit, long expected)
        => Assert.Equal(expected, RobocopyRunner.ParseRoboSize(num, unit));

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]    // 2 = 目标有额外条目（产品不 /MIR、/PURGE：目标独有数据不该判失败）
    [InlineData(3, true)]
    [InlineData(8, false)]
    [InlineData(16, false)]
    [InlineData(-1, false)]
    // ★ 语义变更（GROUP D · F1，2026-10-04 真机 D13/D28）★
    //   旧契约：< 8 即成功 ⇒ 退出码 7（位1 复制 + 位2 额外 + 位4 **不匹配**）被记成
    //   Success=true / status=completed / errorClass=none，界面显示「迁移完成」；
    //   而真实情况是"目标已有同名异型节点（源是文件、目标是同名目录）或重解析点"，
    //   这些源条目**根本没复制到目标**——事后只有手动 L1 Verify 才暴露。
    //   新契约：不匹配位不再算成功，6/7（含不匹配的 5/7 组合）同样判失败并给出中文结论；
    //   位 2（目标有额外条目）保持成功语义不变。
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    public void IsSuccess_ExitCodeSemantics(int code, bool expected)
        => Assert.Equal(expected, RobocopyRunner.IsSuccess(code));

    /// <summary>T3 用例回归：同一文件 /R:2 会记"初次失败+每次重试"多行，必须按路径去重。</summary>
    [Fact]
    public void SummarizeFailures_DedupsRetriesOfSameFile()
    {
        var lines = new[]
        {
            "错误 32 (0x00000020) 正在复制文件 \\\\h\\s\\a.tmp",
            "错误 32 (0x00000020) 正在复制文件 \\\\h\\s\\a.tmp",
            "错误 32 (0x00000020) 正在复制文件 \\\\h\\s\\a.tmp",
            "错误 5 (0x00000005) 正在复制文件 \\\\h\\s\\b.dat",
        };
        var summary = RobocopyRunner.SummarizeFailures(8, lines);

        Assert.NotNull(summary);
        Assert.Contains("2 个文件复制失败", summary);
        Assert.Contains("32×1", summary);
        Assert.Contains("含重试共 4 次失败", summary);
    }

    [Theory]
    [InlineData("错误 5 (0x00000005) 正在访问 \\\\srv\\d$\\a b\\c.txt。", "\\\\srv\\d$\\a b\\c.txt")]
    [InlineData("ERROR 32 (0x00000020) copying D:\\x\\y.bin", "D:\\x\\y.bin")]
    public void ExtractErrorPath_FindsUncAndDrivePaths(string line, string expected)
        => Assert.Equal(expected, RobocopyRunner.ExtractErrorPath(line));
}
