using System;
using System.IO;
using PCMig.Core.Transfer;
using PCMig.WinUI.Presentation;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ Preview.4 实机三项问题（2026-10-22 任务令）★ 回归契约。
///
/// 现场证据：真机日志 `log.zip`（私有证据，未随仓库分发）
///   · 数据根 `PCMig-Preview4`（预览隔离生效）；
///   · `JOB-20261022-094322-42ea`：/MT:32 /J，8 对象、55.6 GB、92.5 MB/s、1 个对象失败；
///   · `JOB-20261022-102929-ab97`：/MT:128，`\\192.0.2.12\e\临时` 15,143 文件里 7,753 失败
///     （错误码分布 67×378、6×120），23 分 32 秒只搬 13.985 GB ⇒ 9.9 MB/s；
///   · 原生 robocopy 对照：36.259 GB / 15:19 = 39.5 MB/s、0 文件失败。
///
/// 纪律：行为级断言优先；只有"WinUI 页面无法在测试程序集里实例化"的部分才用源码结构契约，
/// 并在注释里写明这是结构契约、不是运行期证据。
/// </summary>
[Collection("A5-jobdir-isolation")]
public sealed class P5SourceLampPhysicsTests : IDisposable
{
    private readonly Func<DateTime> _originalClock = ConnectionViewModel.Clock;
    private DateTime _now = new(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc);

    public P5SourceLampPhysicsTests() => ConnectionViewModel.Clock = () => _now;

    public void Dispose() => ConnectionViewModel.Clock = _originalClock;

    // ══════════════════════════════════════════════════════════════════════
    // 问题 2：/J 大文件通道下 committed 字节不动，灯仍必须是绿呼吸
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void 问题2_有物理吞吐但字节不增长_灯仍必须是绿呼吸()
    {
        using var vm = new ConnectionViewModel { Host = "192.0.2.12" };
        vm.ForceConnectedForTest(true);
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);

        // 模拟 /J 大文件通道：48 MB/s 的网卡流量，但"已确认落盘字节"停在同一个值。
        const long frozenBytes = 13_985_000_000;
        const double realRate = 48d * 1024 * 1024;
        for (var i = 0; i < 12; i++)          // 两分钟，远超 60 秒证据有效期
        {
            _now = _now.AddSeconds(10);
            vm.SyncTransferActivity(true, frozenBytes, realRate);
        }

        Assert.Equal(SourceConnectionState.Transferring, vm.SourceState);   // ★ 绿呼吸，不是黄 ★
        Assert.True(vm.IsTransferActive);
    }

    [Fact]
    public void 问题2_字节不涨且无吞吐_材料耗尽的活性必须掉下来_不得伪造绿()
    {
        // 反向对照：传输停住（没有流量、没有新字节）时，60 秒后必须掉成琥珀。
        using var vm = new ConnectionViewModel { Host = "192.0.2.12" };
        vm.ForceConnectedForTest(true);
        vm.SyncTransferActivity(true, 1_000_000, 10 * 1024 * 1024);
        Assert.Equal(SourceConnectionState.Transferring, vm.SourceState);

        vm.SyncTransferActivity(false, 1_000_000, double.NaN);   // 会话层判定"没有活动"
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);   // 回到绿常亮，不是红

        _now = _now.AddSeconds(61);
        vm.SweepEvidenceFreshness();
        Assert.NotEqual(SourceConnectionState.Transferring, vm.SourceState);
    }

    [Fact]
    public void 问题2_暂停后必须是绿色常亮_不是黄也不是红()
    {
        using var vm = new ConnectionViewModel { Host = "192.0.2.12" };
        vm.ForceConnectedForTest(true);
        vm.SyncTransferActivity(true, 100, 40 * 1024 * 1024);
        Assert.Equal(SourceConnectionState.Transferring, vm.SourceState);

        vm.SyncTransferActivity(false, 100, 0);        // 暂停 ⇒ 会话层不再报活动
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);
    }

    [Fact]
    public void 问题2_活性判据必须包含网卡物理吞吐且被暂停否决()
    {
        var code = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MigrationSessionViewModel.cs");
        var body = MethodWindow(code, "public bool HasLiveTransferActivity", 1800);

        // ① 物理观测优先：网卡接收吞吐达到阈值即算"在传"（/J 大文件通道唯一可用的证据）
        Assert.Contains("NetworkReceiveBytesPerSecond", body);
        Assert.Contains("TransferActivityFloorBytesPerSecond", body);
        // ② 引擎字节判据只能是兜底（小文件通道），不能是唯一判据
        Assert.Contains("_bytesAdvancing", body);
        // ③ 否决项齐全：非 Running / 正在暂停 / 已暂停 / 正在停止
        Assert.Contains("Phase != JobPhase.Running", body);
        Assert.Contains("PauseUiState.Pausing or PauseUiState.Paused", body);
        Assert.Contains("IsStopping", body);
    }

    [Fact]
    public void 问题2_阈值必须是能排除背景流量的量级()
    {
        // 256 KB/s：低于它只可能是 ARP/心跳/系统更新，不足以证明迁移在搬数据。
        Assert.Equal(256 * 1024, MigrationSessionViewModel.TransferActivityFloorBytesPerSecond);
    }

    private static string MethodWindow(string text, string signature, int length)
    {
        var i = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(i >= 0, $"找不到方法签名：{signature}");
        return text.Substring(i, Math.Min(length, text.Length - i));
    }

    private static string ReadRepoFile(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray()));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到仓库根（PCMig.sln）。");
    }
}

/// <summary>★ 问题 1 ★ 预检结论的"行动号召"必须在实质阶段撤下。</summary>
public sealed class P5PreflightHintLifecycleTests
{
    [Fact]
    public void 问题1_迁移开始前必须撤下可以开始迁移的行动号召()
    {
        var code = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MigrationSessionViewModel.cs");

        // ① 预检成功分支必须标记"这条行动号召待撤下"
        var preflight = MethodWindow(code, "SetOperational(\"正在扫描源数据（大容量磁盘可能需要几分钟）…\");", 1600);
        Assert.Contains("_preflightHintActive = true;", preflight);

        // ② 撤下动作必须发生在 IsRunning = true 之前（实质阶段入口）
        var run = MethodWindow(code, "// ★ Preview.4 真机问题 1（2026-10-22）★ 实质阶段开始", 600);
        Assert.Contains("RetirePreflightCallToAction();", run);
        var retireAt = code.IndexOf("RetirePreflightCallToAction();", StringComparison.Ordinal);
        var runningAt = code.IndexOf("IsRunning = true;", StringComparison.Ordinal);
        Assert.True(retireAt > 0 && runningAt > retireAt, "必须在实质阶段开始前撤下预检行动号召。");
    }

    [Fact]
    public void 问题1_撤下只清行动号召_不得删除检查结论与提醒()
    {
        var code = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MigrationSessionViewModel.cs");
        var body = MethodWindow(code, "private void RetirePreflightCallToAction()", 900);

        Assert.Contains("UserHint = string.Empty;", body);
        // 结论通道（CheckSummary / CheckNoticeCount）必须保留：预检发现的历史提醒是诊断证据。
        Assert.DoesNotContain("SetCheckSummary", body);
        Assert.DoesNotContain("CheckNoticeCount =", body);
        // 不得顺手清错误摘要或安全提示（那是另外两路语义）
        Assert.DoesNotContain("ErrorSummary =", body);
        Assert.DoesNotContain("SecurityHint =", body);
        // 幂等：没写过预检提示时什么都不做
        Assert.Contains("if (!_preflightHintActive) return;", body);
    }

    [Fact]
    public void 问题1_检查结论文案本身保持不变()
    {
        // 结论是静态事实（检查过没有、几条提醒），不该随阶段变化 —— 变的只是"行动号召"。
        Assert.Equal("检查通过 · 1 条提醒", MigrationSessionViewModel.DescribeCheckSummary(true, 1));
        Assert.Equal("检查通过 · 无提醒", MigrationSessionViewModel.DescribeCheckSummary(true, 0));
        Assert.Equal(string.Empty, MigrationSessionViewModel.DescribeCheckSummary(false, 2));
    }

    private static string MethodWindow(string text, string signature, int length)
    {
        var i = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(i >= 0, $"找不到方法签名：{signature}");
        return text.Substring(i, Math.Min(length, text.Length - i));
    }

    private static string ReadRepoFile(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray()));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到仓库根（PCMig.sln）。");
    }
}

/// <summary>★ 问题 3 ★ 断链止损与失败计数真实性。</summary>
public sealed class P5ShareLossAbortTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-p5-" + Guid.NewGuid().ToString("N"));

    public P5ShareLossAbortTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    // ── 断链判据 ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(67, true)]    // 找不到网络名（现场 378 次）
    [InlineData(6, true)]     // 网络名不再可用（现场 120 次）
    [InlineData(59, true)]    // 意外网络错误
    [InlineData(64, true)]    // 网络名不再可用
    [InlineData(53, true)]    // 找不到网络路径
    [InlineData(32, false)]   // 文件被占用 —— 绝不能当成共享断开
    [InlineData(33, false)]   // 区域锁定
    [InlineData(5, false)]    // 拒绝访问（权限）
    public void 问题3_只有共享级错误才算断链(int code, bool expected)
        => Assert.Equal(expected, ShareLossSignal.IsShareLossWin32(code));

    [Fact]
    public void 问题3_熔断阈值必须能排除个别坏文件又不至于空转太久()
    {
        // 24 个连续共享级错误 ≈ 2 分钟空转（/R:2 /W:5 下每个约 10 秒），
        // 既明显高于"个别文件恰好不可读"，又远低于现场的 7,753 个。
        Assert.Equal(24, TransferOrchestrator.ShareLossAbortAfter);
    }

    [Fact]
    public void 问题3_熔断必须区分主动中止与用户暂停()
    {
        var code = ReadRepoFile("src", "PCMig.Core", "Transfer", "TransferOrchestrator.cs");

        // ① 主动中止后不能直接 return null（那会让对象被当成"用户中断"停在原地）
        var body = MethodWindow(code, "if (result.Killed)", 1200);
        Assert.Contains("if (_shareLossCode == null) return null;", body);
        Assert.Contains("continue;", body);          // 按 Transient 重试

        // ② 连续计数必须在"非共享级错误"出现时清零（否则累计值会把个别坏文件算成断链）
        Assert.Contains("Interlocked.Exchange(ref _shareLossStreak, 0);", code);

        // ③ 每趟尝试都要复位（新进程会重建树连接，上一趟的断链结论不能把本趟判死）
        var attempt = MethodWindow(code, "for (var attempt = 1; attempt <= maxAttempts; attempt++)", 700);
        Assert.Contains("_shareLossCode = null;", attempt);
    }

    // ── 失败计数真实性 ───────────────────────────────────────────────────────

    [Fact]
    public void 问题3_能从robocopy日志尾部读出真实失败文件数()
    {
        var log = Path.Combine(_root, "object.log");
        File.WriteAllText(log,
            "------------------------------------------------------------------------------\r\n\r\n" +
            "                  总数        复制        跳过       不匹配        失败        其他\r\n" +
            "       目录:        71        71         0         0        18         0\r\n" +
            "       文件:     15143      7384         6         0      7753         0\r\n" +
            "       字节:  50.674 g  13.985 g   2.584 g         0  34.104 g         0\r\n" +
            "\r\n       速度:           10,720,491 字节/秒。\r\n");

        var stat = RobocopyRunner.TryReadLogSummary(log);

        Assert.NotNull(stat);
        Assert.Equal(15143, stat!.Value.TotalFiles);
        Assert.Equal(7384, stat.Value.CopiedFiles);
        Assert.Equal(7753, stat.Value.FailedFiles);   // ★ 现场被说成 498，真实是 7753 ★
        Assert.Equal(0, stat.Value.MismatchFiles);
    }

    [Fact]
    public void 问题3_日志读不出统计时必须返回null而不是零()
    {
        var log = Path.Combine(_root, "empty.log");
        File.WriteAllText(log, "not a robocopy log\r\n");
        Assert.Null(RobocopyRunner.TryReadLogSummary(log));
        Assert.Null(RobocopyRunner.TryReadLogSummary(Path.Combine(_root, "missing.log")));
        Assert.Null(RobocopyRunner.TryReadLogSummary(null));
    }

    [Fact]
    public void 问题3_错误行被截断时文案必须说明数字不是全部()
    {
        var lines = new System.Collections.Generic.List<string>();
        for (var i = 0; i < 500; i++)
            lines.Add($@"2026/10/22 10:30:00 错误 67 (0x00000043) 正在复制文件 \\192.0.2.12\e\临时\f{i}.jpg");

        var text = RobocopyRunner.SummarizeFailures(9, lines, errorLinesTruncated: true);

        Assert.NotNull(text);
        Assert.Contains("采样上限", text);
        Assert.Contains("以 robocopy 统计为准", text);
    }

    [Fact]
    public void 问题3_未截断时不加多余说明()
    {
        var lines = new System.Collections.Generic.List<string>
        {
            @"错误 32 (0x00000020) 正在复制文件 \\h\d\a.txt",
        };
        var text = RobocopyRunner.SummarizeFailures(8, lines, errorLinesTruncated: false);
        Assert.NotNull(text);
        Assert.DoesNotContain("采样上限", text);
    }

    private static string MethodWindow(string text, string signature, int length)
    {
        var i = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(i >= 0, $"找不到方法签名：{signature}");
        return text.Substring(i, Math.Min(length, text.Length - i));
    }

    private static string ReadRepoFile(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray()));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到仓库根（PCMig.sln）。");
    }
}
