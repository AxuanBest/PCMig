using PCMig.Core.Transfer;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ P0-05 ★ 引擎侧结构化信号契约：把失败文本归成"共享整体不可用" vs "单个文件被锁"，二者绝不可混。
///
/// 现场依据（2026-10-10）：共享被公司策略自动关闭，robocopy 持续报 <c>错误 67</c>（退出码 16），
/// 而连接灯仍是绿的 —— 引擎看得见的问题没有任何一层说出来。本文件锁住翻译规则。
/// </summary>
public class P0ShareLossSignalTests
{
    // ── 网络/共享级 ⇒ 是共享断开 ──────────────────────────────────────────────

    [Theory]
    [InlineData(53)] [InlineData(55)] [InlineData(59)] [InlineData(64)] [InlineData(67)]
    [InlineData(1219)] [InlineData(1231)] [InlineData(1236)] [InlineData(1326)]
    public void NetworkCodesAreShareLoss(int code) => Assert.True(ShareLossSignal.IsShareLossWin32(code));

    [Fact]
    public void ChineseRobocopyLine_Error67_IsRecognized()
    {
        const string line = "2026/10/10 15:04:11 错误 67 (0x00000043) 正在复制文件 \\\\SRC-PC\\D\\a.pst";
        Assert.True(ShareLossSignal.TryMatchShareLossText(line, out var code, out var evidence));
        Assert.Equal(67, code);
        Assert.Contains("67", evidence);
    }

    [Fact]
    public void EnglishRobocopyLine_Error64_IsRecognized()
    {
        const string line = "ERROR 64 (0x00000040) Copying File \\\\HOST\\share\\x.dat";
        Assert.True(ShareLossSignal.TryMatchShareLossText(line, out var code, out _));
        Assert.Equal(64, code);
    }

    [Fact]
    public void PasswordRevoked_1326_IsShareLoss_NotAFileProblem()
    {
        Assert.True(ShareLossSignal.TryMatchShareLossText("错误 1326 登录失败：用户名或密码不正确。", out var code, out _));
        Assert.Equal(1326, code);
    }

    // ── 文件锁 ⇒ 不是共享断开（需求点名禁止） ────────────────────────────────

    [Fact]
    public void SharingViolation_32_IsNotShareLoss()
    {
        const string line = "错误 32 (0x00000020) 正在复制文件 C:\\Users\\X\\NTUSER.DAT";
        Assert.False(ShareLossSignal.TryMatchShareLossText(line, out _, out _));
        Assert.True(ShareLossSignal.IsFileLockWin32(32));
        Assert.False(ShareLossSignal.IsShareLossWin32(32));
    }

    [Fact]
    public void LockViolation_33_IsNotShareLoss()
    {
        Assert.False(ShareLossSignal.TryMatchShareLossText("错误 33 (0x00000021) 正在复制文件 a.pst", out _, out _));
        Assert.True(ShareLossSignal.IsFileLockWin32(33));
        Assert.False(ShareLossSignal.IsShareLossWin32(33));
    }

    [Fact]
    public void FileLockAndNetworkCodeTogether_IsShareLoss_NetworkWins()
    {
        const string line = "错误 32 正在复制文件 a.dat；错误 67 找不到网络名 \\\\HOST\\share";
        Assert.True(ShareLossSignal.TryMatchShareLossText(line, out var code, out _));
        Assert.Equal(67, code);                                   // 网络码才是"共享怎么了"的答案
    }

    // ── 无证据就不许猜 ────────────────────────────────────────────────────────

    [Fact]
    public void FatalExitCodeWithoutAnyText_IsNotReportedAsShareLoss()
    {
        var r = new RobocopyRunResult(ExitCode: 16, Success: false, Killed: false,
            Duration: System.TimeSpan.FromSeconds(1), LastErrorLine: null, ErrorLines: []);
        Assert.False(ShareLossSignal.TryMatchShareLoss(r, out _, out _));   // 退位码本身不等于"共享断开"
    }

    [Fact]
    public void RobocopyResult_ScansLastLineAndAllErrorLines()
    {
        var r = new RobocopyRunResult(ExitCode: 16, Success: false, Killed: false,
            Duration: System.TimeSpan.FromSeconds(2), LastErrorLine: "错误 32 (0x00000020) 正在复制文件 b.dat",
            ErrorLines: ["错误 32 (0x00000020) 正在复制文件 b.dat", "错误 67 (0x00000043) 正在复制文件 c.dat"]);
        Assert.True(ShareLossSignal.TryMatchShareLoss(r, out var code, out var evidence));
        Assert.Equal(67, code);
        Assert.Contains("16", evidence);                          // 证据里带上退出码，便于与现场日志对齐
    }

    [Fact]
    public void KilledRunWithoutErrorText_IsNeverShareLoss()
    {
        var r = new RobocopyRunResult(ExitCode: -1, Success: false, Killed: true,
            Duration: System.TimeSpan.FromSeconds(1), LastErrorLine: null, ErrorLines: null);
        Assert.False(ShareLossSignal.TryMatchShareLoss(r, out _, out _));
    }
}