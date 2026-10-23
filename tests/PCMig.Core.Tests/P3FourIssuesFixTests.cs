using System;
using System.IO;
using System.Collections.Generic;
using PCMig.Core.Models;
using PCMig.Core.Preflight;
using PCMig.WinUI.Controls.ImmersiveProgress;
using PCMig.WinUI.Presentation;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ v0.5.4 Preview.3 真机四项体验问题（2026-10-21 任务令）★ 回归契约。
///
/// 现场证据来源：真机日志（实机日志1/2.zip，私有证据，未随仓库分发），
/// 任务 JOB-20261021-100258-a478（20/20 完成，截图时 93.3%）、JOB-20261021-113312-48f4
/// （host=192.0.2.11，overallPass=true / errorCount=1 / warningCount=2，
///  object-000005 源 \\192.0.2.11\f\mail = 110,913,894,400 B / 6 个文件）。
///
/// 本文件的纪律：**行为级断言优先**。只有"圆形光晕已从渲染路径删除"这类无法在本机
/// 无 GUI 环境下运行验证的视觉结构，才用源码结构断言，并在注释里标明这是结构契约、
/// 不是运行期像素证据（运行期证据在复验包的 PROGRESS-MOTION-EVIDENCE 里如实标注状态）。
/// </summary>
[Collection("A5-jobdir-isolation")]
public sealed class P3SourceLampTransferTests : IDisposable
{
    private readonly Func<DateTime> _originalClock = ConnectionViewModel.Clock;
    private readonly Func<string, int> _originalProbe = ConnectionViewModel.ShareCountProbe;
    private DateTime _now = new(2026, 10, 21, 10, 0, 0, DateTimeKind.Utc);

    public P3SourceLampTransferTests()
    {
        ConnectionViewModel.Clock = () => _now;
    }

    public void Dispose()
    {
        // 时钟与探测都是**进程级**测试缝，必须复位，否则会污染其它测试类。
        ConnectionViewModel.Clock = _originalClock;
        ConnectionViewModel.ShareCountProbe = _originalProbe;
    }

    // ══════════════════════════════════════════════════════════════════════
    // 问题 1：迁移正在进行时，源连接灯仍显示黄色
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void 问题1_已连接且有真实字节增长_灯必须是绿呼吸而不是黄()
    {
        using var vm = new ConnectionViewModel { Host = "SRC-PC" };
        Assert.Equal(SourceConnectionState.Disconnected, vm.SourceState);   // 红灯

        vm.ForceConnectedForTest(true);                                     // 预检通过 = 实测可达
        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);      // 绿灯常亮

        vm.NotifyTransferActive(null, 4_000_000_000);                       // 引擎确认字节在增长

        Assert.Equal(SourceConnectionState.Transferring, vm.SourceState);   // ★ 绿呼吸，不是黄 ★
        Assert.Contains("正在传输", vm.SourceStateDetail);
    }

    [Fact]
    public void 问题1_暂停后必须回到绿色常亮_而不是黄或红()
    {
        using var vm = new ConnectionViewModel { Host = "SRC-PC" };
        vm.ForceConnectedForTest(true);
        vm.NotifyTransferActive(null, 1_000);

        vm.NotifyTransferIdle();                                            // 暂停 / 停止 / 完成

        Assert.Equal(SourceConnectionState.Connected, vm.SourceState);      // ★ 暂停 ≠ 断开 ★
        Assert.False(vm.IsTransferActive);
    }

    [Fact]
    public void 问题1_连续传输五分钟_证据必须被真实字节增长持续续期_不得掉成琥珀()
    {
        using var vm = new ConnectionViewModel { Host = "SRC-PC" };
        vm.ForceConnectedForTest(true);
        vm.NotifyTransferActive(null, 0);

        // 每 30 秒一次真实增长，共 5 分钟（远超 60 秒证据有效期）。
        for (var i = 1; i <= 10; i++)
        {
            _now = _now.AddSeconds(30);
            vm.NotifyTransferActive(null, 1_000_000L * i);
        }

        Assert.Equal(SourceConnectionState.Transferring, vm.SourceState);
    }

    [Fact]
    public async System.Threading.Tasks.Task 问题1_传输中即使IPC共享枚举失败_也不得把灯打成黄()
    {
        // 现场环境：对方不导出 IPC$（错误 67），`EnumShares` 必然抛错。
        // 旧实现在传输期间每 20 秒仍会探测 ⇒ 抛错 ⇒ NotifyProbeInconclusive 清空全部证据 ⇒ 灯变琥珀。
        // 本用例按现场真实节奏：传输持续进行（每 30 秒都有新的字节增长），巡检同时发生。
        ConnectionViewModel.ShareCountProbe = _ => throw new InvalidOperationException("错误 67（找不到网络名）");

        using var vm = new ConnectionViewModel { Host = "192.0.2.11" };
        vm.ForceConnectedForTest(true);
        vm.NotifyTransferActive(null, 5_000_000);

        for (var i = 1; i <= 6; i++)          // 3 分钟，每 30 秒一次真实增长 + 一次证据巡检
        {
            _now = _now.AddSeconds(30);
            vm.NotifyTransferActive(null, 5_000_000L * (i + 1));
            await vm.SweepEvidenceFreshnessAsync();
        }

        Assert.Equal(SourceConnectionState.Transferring, vm.SourceState);    // ★ 传输中不受 IPC$ 探测影响 ★
    }

    [Fact]
    public async System.Threading.Tasks.Task 问题1_传输停止且证据过期后_不得继续伪造绿色()
    {
        // 反向对照：证明"续期"是真续期（靠字节增长），不是把灯焊死成绿。
        ConnectionViewModel.ShareCountProbe = _ => throw new InvalidOperationException("错误 67（找不到网络名）");

        using var vm = new ConnectionViewModel { Host = "192.0.2.11" };
        vm.ForceConnectedForTest(true);
        vm.NotifyTransferActive(null, 5_000_000);
        Assert.Equal(SourceConnectionState.Transferring, vm.SourceState);

        vm.NotifyTransferIdle();                                            // 传输结束
        _now = _now.AddSeconds(61);                                         // 超过 60 秒有效期
        await vm.SweepEvidenceFreshnessAsync();                             // 空闲探测失败（无 IPC$）

        Assert.NotEqual(SourceConnectionState.Connected, vm.SourceState);
        Assert.NotEqual(SourceConnectionState.Transferring, vm.SourceState);
    }

    [Fact]
    public void 问题1_绿呼吸只能由已连接升级而来_不得跳过证据()
    {
        // 结构性安全带：Transferring 的派生条件必须是"账本已经给出 Connected"。
        var code = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "ConnectionViewModel.cs");
        var body = MethodWindow(code, "private void UpdateSourceState()", 1800);

        Assert.Contains("if (state == SourceConnectionState.Connected && _transferActive)", body);
        Assert.Contains("state = SourceConnectionState.Transferring;", body);
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

/// <summary>
/// ★ 问题 2 ★ 预检结论与计数一致性。
/// 现场证据：preflight.json 中 <c>overallPass=true</c> 同时 <c>errorCount=1</c>、<c>warningCount=2</c>。
/// </summary>
public sealed class P3PreflightCountTests
{
    [Fact]
    public void 问题2_现场组合_通过项不得计入错误数()
    {
        // 逐条照抄 JOB-20261021-113312-48f4/preflight.json 的三条关键检查。
        var checks = new List<PreflightCheck>
        {
            // ③ 源路径：pass=true，severity=Error（"一旦失败就阻断"）—— 它不是当前问题。
            new() { Name = @"源路径 \\192.0.2.11\f", Pass = true, Severity = "Error", Detail = "路径存在且可访问" },
            // ④ IPC$：pass=true，severity=Warning（对方不导出 IPC$，但已由直连共享证明凭据可用）。
            new() { Name = "IPC$ 凭据会话", Pass = true, Severity = "Warning", Detail = "对方不导出 IPC$ 管理共享（错误 67）…" },
            // ⑤ 链路吞吐基准：pass=false，severity=Warning（唯一**真实的**提醒）。
            new() { Name = "链路吞吐基准", Pass = false, Severity = "Warning", Detail = "测速未完成…" },
        };

        var (blocking, notices) = PreflightChecker.CountFailures(checks);

        Assert.Equal(0, blocking);      // ★ 旧实现这里会得到 1 ⇒「通过」与「1 个错误」同框 ★
        Assert.Equal(1, notices);       // ★ 旧实现这里会得到 2（把已通过的 IPC$ 也算进提醒）★
    }

    [Fact]
    public void 问题2_真正失败的阻断项必须计入错误数()
    {
        var checks = new List<PreflightCheck>
        {
            new() { Name = @"源路径 \\host\d", Pass = false, Severity = "Error", Detail = "路径不存在" },
            new() { Name = "目标盘检查", Pass = false, Severity = "Error", Detail = "找不到盘符" },
            new() { Name = "Robocopy 可用性", Pass = true, Severity = "Info", Detail = "ok" },
        };

        var (blocking, notices) = PreflightChecker.CountFailures(checks);

        Assert.Equal(2, blocking);
        Assert.Equal(0, notices);
    }

    [Fact]
    public void 问题2_测速失败必须翻译成不影响读取源文件_而不是Access_is_denied()
    {
        var reason = PreflightChecker.DescribeBenchmarkFailure(
            new UnauthorizedAccessException("Access to the path '\\\\192.0.2.11\\f\\.pcmig-bench-x.tmp' is denied."));

        Assert.Contains("不允许创建测速文件", reason);
        Assert.DoesNotContain("denied", reason);        // 英文原文只允许出现在"技术细节"括号里
    }

    [Fact]
    public void 问题2_检查结论文案_通过且有提醒时必须说清提醒数()
    {
        Assert.Equal("检查通过 · 1 条提醒", MigrationSessionViewModel.DescribeCheckSummary(true, 1));
        Assert.Equal("检查通过 · 无提醒", MigrationSessionViewModel.DescribeCheckSummary(true, 0));
        // 未通过时由 ErrorSummary 承担，避免两处说同一件事。
        Assert.Equal(string.Empty, MigrationSessionViewModel.DescribeCheckSummary(false, 3));
    }

    [Fact]
    public void 问题2_提示卡状态行不得把有提醒的预检显示成暂无报错()
    {
        var card = ReadRepoFile("src", "PCMig.WinUI", "Views", "ShellHintCard.xaml.cs");
        var body = MethodWindow(card, "private void Apply(MigrationSessionViewModel? session)", 2400);

        Assert.Contains("session?.CheckSummary", body);
        Assert.Contains("session?.CheckNoticeCount", body);
        // 有提醒时状态行用警示色（旧实现只有 hasError 一个判据 ⇒ 会显示绿色"暂无报错"）。
        Assert.Contains("hasError || notices > 0 ? \"WarningBrush\" : \"SuccessBrush\"", body);
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

/// <summary>
/// ★ 问题 3 ★「正在复制的文件」面板在真实传输中不得显示空状态。
/// 现场证据：object-000005（\\192.0.2.11\f\mail，110,913,894,400 B，6 个文件，
/// 多个 17~20 GB PST，/J 大文件通道）传输时文件列表为空，面板显示「当前没有正在复制的文件。」
/// </summary>
public sealed class P3ActiveFilePanelTests
{
    [Theory]
    [InlineData(@"\\192.0.2.11\f\mail", "mail")]
    [InlineData(@"\\192.0.2.11\f", "f")]
    [InlineData(@"\\192.0.2.11\f\", "f")]
    [InlineData(@"D:\Target\Users", "Users")]
    [InlineData("", "")]
    [InlineData(@"\\", "")]
    public void 问题3_对象名只取路径最后一段(string input, string expected)
        => Assert.Equal(expected, MigrationSessionViewModel.DescribeObjectName(input));

    [Fact]
    public void 问题3_面板必须按真实对象级真值切换_而不是只看文件列表()
    {
        var page = ReadRepoFile("src", "PCMig.WinUI", "Views", "Step3ProgressPage.xaml.cs");
        var body = MethodWindow(page, "private void PushState()", 6000);

        // ① 判定必须同时看"文件列表为空""正在运行""有当前对象真值"三件事。
        Assert.Contains("var objectLevelActive = !hasFiles && session.IsRunning && session.HasCurrentObjectDetail;", body);
        // ② 活动态标题必须是"正在传输对象：xxx"，而不是"当前没有正在复制的文件"。
        Assert.Contains("正在传输对象：", body);
        // ③ 对象级字节进度来自引擎快照（不是从历史完成日志挑一个文件冒充）。
        Assert.Contains("session.ActiveObjectProgressText", body);
        // ④ 不得为了让面板显示文件名去改传输参数（任务令 §6.1 硬约束）。
        Assert.DoesNotContain("/MT:", body);
        Assert.DoesNotContain("LargeFileThreshold", body);
    }

    [Fact]
    public void 问题3_对象级真值必须来自进度快照且运行结束即清空()
    {
        var vm = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MigrationSessionViewModel.cs");
        var body = MethodWindow(vm, "private void ApplySnapshot(ProgressSnapshot s)", 12000);

        Assert.Contains("ActiveObjectName = DescribeObjectName(s.CurrentObjectPath);", body);
        Assert.Contains("ActiveObjectProgressText = s.CurrentObjectTotalBytes > 0", body);
        // 非运行态必须清空：暂停/停止/完成后不得保留上一轮对象冒充"正在传输"。
        Assert.Contains("ActiveObjectName = string.Empty;", body);
        Assert.Contains("ActiveObjectProgressText = string.Empty;", body);
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

/// <summary>
/// ★ 问题 4 ★ 进度条前沿：不得再出现"孤立的圆形模糊光晕"。
///
/// 说明：本机无法离屏运行 Win2D 渲染管线，因此这里断言的是**参数契约与绘制结构**
/// （圆形光斑是否已被删除、光带是否被填充胶囊裁剪），不是像素级运行期证据。
/// 运行期视觉证据须在真实 GUI 会话采集，状态如实标注在复验包的趋势证据里。
/// </summary>
public sealed class P3ProgressHeadEdgeTests
{
    [Fact]
    public void 问题4_前沿光带必须贴合条厚且方向明确()
    {
        // 生产厚度 token = 12 DIP（Themes/Materials.xaml PCMigImmersiveProgressThickness）。
        const double thickness = 12d;

        var tail = ImmersiveProgressParameters.HeadEdgeTailLength;
        Assert.InRange(tail, 18d, 28d);
        Assert.True(tail > thickness, "拖尾必须长于条厚，否则读成一块方形补丁。");
        Assert.True(ImmersiveProgressParameters.HeadEdgePeakInset < tail,
            "峰值内缩必须小于拖尾长度，否则渐变会被压没。");
        Assert.True(ImmersiveProgressParameters.HeadEdgeCoreTailLength < tail,
            "内芯必须比外层短，才有层次（否则只是一片均匀光）。");
        Assert.True(ImmersiveProgressParameters.HeadEdgeOpacity > ImmersiveProgressParameters.HeadEdgeCoreOpacity,
            "外层应比内芯淡。");
        Assert.Equal(5, ImmersiveProgressParameters.HeadEdgeVerticalStripes);
    }

    [Fact]
    public void 问题4_圆形椭圆光斑必须已从渲染路径删除()
    {
        var code = ReadRepoFile("src", "PCMig.WinUI", "Controls", "ImmersiveProgress",
            "ImmersiveTransferProgressRenderer.cs");

        // 旧实现的三层同心椭圆（24×8 / 15×6 / 7×4.5）—— 就是"孤立圆形模糊光晕"的来源。
        Assert.DoesNotContain("HeadHaloOuterRadiusX", code);
        Assert.DoesNotContain("HeadHaloBloomRadiusX", code);
        Assert.DoesNotContain("HaloRadialStops", code);

        var body = MethodWindow(code, "private bool DrawHead(", 3200);
        Assert.DoesNotContain("FillEllipse", body);                 // 椭圆/圆形光斑不得回来
        Assert.Contains("CreateLayer(1f, capsule)", body);           // 光带必须裁进填充胶囊
        Assert.Contains("HeadEdgeStops", code);                     // 非对称渐变剖面
        Assert.Contains("HeadEdgeTailLength", body);
    }

    [Fact]
    public void 问题4_没有胶囊几何时宁可不画_也不得让光带越过实际进度()
    {
        var code = ReadRepoFile("src", "PCMig.WinUI", "Controls", "ImmersiveProgress",
            "ImmersiveTransferProgressRenderer.cs");
        var body = MethodWindow(code, "private bool DrawHead(", 3200);

        Assert.Contains("if (capsule is null) return false;", body);
        // 右端必须由 HeadX 决定（而不是固定偏移），否则低进度时光带会跑到未完成轨道上。
        var edge = MethodWindow(code, "private static void DrawHeadEdge(", 1600);
        Assert.Contains("var head = (float)m.HeadX;", edge);
        Assert.Contains("var right = Math.Max(0.5f, head - (float)ImmersiveProgressParameters.HeadEdgePeakInset);", edge);
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
