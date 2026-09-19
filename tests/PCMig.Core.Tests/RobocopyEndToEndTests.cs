using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// 真 robocopy 端到端（本地临时目录，秒级）。这是全项目性价比最高的两个测试：
/// 用真实 CreateProcess 验证 BuildArguments + QuoteArg 的全链路，纯函数测试替代不了。
/// </summary>
public class RobocopyEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-e2e-" + Guid.NewGuid().ToString("N")[..8]);

    public RobocopyEndToEndTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* 文件句柄延迟释放 */ } }

    /// <summary>
    /// 78GB 生产事故回归（JOB-20260917-143154-dc9d）：源/目标根带尾反斜杠时，
    /// 旧 QuoteArg 把 "D:\" 变 "D:" → robocopy Exit=16，源盘根散落文件一个都没传。
    /// 此用例必须永远绿。
    /// </summary>
    [Fact]
    public async Task TrailingBackslashRootFiles_PassCopiesAllLooseFiles()
    {
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "根散落.txt"), "a");
        File.WriteAllText(Path.Combine(src, "带 空格和#井号.txt"), "b");

        var runner = new RobocopyRunner(Serilog.Core.Logger.None);
        var result = await runner.RunPassAsync(
            src + Path.DirectorySeparatorChar,                      // 源根带尾反斜杠（事故触发条件）
            Path.Combine(_root, "dst") + Path.DirectorySeparatorChar, // 目标根带尾反斜杠（事故触发条件）
            new MigrationOptions(), new MigrationMatrix(), PassKind.RootFiles,
            Path.Combine(_root, "robo.log"), CancellationToken.None);

        Assert.True(result.Success, $"Exit={result.ExitCode}，最后一行：{result.LastErrorLine}");
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_root, "dst")).Length);
    }

    /// <summary>验证 /XF 排除名单经 QuoteArg 拼接后依然与扫描/验证同口径（含通配符 ~$*）。</summary>
    [Fact]
    public async Task BulkPass_RespectsPolicyExclusions()
    {
        var src = Path.Combine(_root, "src2");
        var dst = Path.Combine(_root, "dst2");
        Directory.CreateDirectory(Path.Combine(src, "子目录"));
        File.WriteAllText(Path.Combine(src, "keep.txt"), "1");
        File.WriteAllText(Path.Combine(src, "子目录", "保留.docx"), "2");
        File.WriteAllText(Path.Combine(src, "desktop.ini"), "策略排除");
        File.WriteAllText(Path.Combine(src, "~$报价.docx"), "Office 锁文件");

        var runner = new RobocopyRunner(Serilog.Core.Logger.None);
        var result = await runner.RunPassAsync(src, dst, new MigrationOptions(),
            new MigrationMatrix(), PassKind.Bulk, Path.Combine(_root, "robo2.log"), CancellationToken.None);

        Assert.True(result.Success, $"Exit={result.ExitCode}，最后一行：{result.LastErrorLine}");
        Assert.True(File.Exists(Path.Combine(dst, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(dst, "子目录", "保留.docx")));
        Assert.False(File.Exists(Path.Combine(dst, "desktop.ini")), "desktop.ini 属矩阵排除名单，不得复制");
        Assert.False(File.Exists(Path.Combine(dst, "~$报价.docx")), "~$* 通配排除，不得复制");
    }
}
