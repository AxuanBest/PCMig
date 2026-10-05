using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PCMig.Core.Models;
using PCMig.Core.Native;
using PCMig.Core.Transfer;
using PCMig.WinUI.Presentation;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// GROUP B · B-FIX-BATCH 回归（三个已确认产品缺陷的判定语义）。
///
/// 【为什么存在】这三个缺陷的共同特征是：**编译过、单测不报、只有真机才暴露**，
/// 且都属于"界面说的与事实不符"这一类产品诚实性缺陷，所以必须把**判定语义本身**钉进单测：
///
///  · <b>B12b4（High／数据完整性·假成功）</b>：同名共享被换底后，续传继续从非源数据取数
///    并宣称「迁移完成 100%」。修复 = 计划期固化源身份指纹 + 每次开跑前比对，不一致即拒绝继续。
///  · <b>O-B22c-1（Medium／消息与可诊断性）</b>：一句「已连接 X，发现 0 个共享」把三种
///    完全不同的结果（真 0 共享 / 复用了本机现有连接而输入凭据被忽略 / 凭据压根没验证过）
///    混成一句。修复 = 三态判定 + 分态文案。
///  · <b>O-B03-2（Low／显示）</b>：权限被拒的子目录在目标端留下同名空目录，结果页必须说明。
///
/// 这里只断言**纯函数与静态契约**，不触网、不启动 UI。
/// </summary>
public sealed class GroupBFixBatchTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
        }
        throw new InvalidOperationException("找不到含 PCMig.sln 的仓库根：" + AppContext.BaseDirectory);
    }

    private static string ReadSource(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray()));

    private static PlannedObject Obj(string id, string path, string? identity) =>
        new() { ObjectId = id, SourcePath = path, SourceIdentity = identity };

    // ─────────────────────────── B12b4：源身份守卫 ───────────────────────────

    [Fact]
    public void B12b4_IdentityMatch_NoFinding()
    {
        var objs = new[] { Obj("object-000001", @"\\SRC\D", "fsid:AAAA1111-0000000100000002") };
        var found = SourceIdentityGuard.Evaluate(objs, _ => "fsid:AAAA1111-0000000100000002");
        Assert.Empty(found);
    }

    [Fact]
    public void B12b4_SameNameSwappedBacking_IsDetected()
    {
        // B12b4 的真实反例：共享名与 UNC 前缀完全没变，只有背后的目录/卷换了 ⇒ 指纹必须不同。
        var objs = new[] { Obj("object-000001", @"\\LAB-SRC01\D", "fsid:AAAA1111-00000001000000AA") };
        var found = SourceIdentityGuard.Evaluate(objs, _ => "fsid:BBBB2222-00000001000000BB");
        var f = Assert.Single(found);
        Assert.Equal("object-000001", f.ObjectId);
        Assert.Equal(@"\\LAB-SRC01\D", f.SourcePath);
        Assert.Equal("fsid:AAAA1111-00000001000000AA", f.Expected);
        Assert.Equal("fsid:BBBB2222-00000001000000BB", f.Actual);
    }

    [Fact]
    public void B12b4_NoBaseline_IsNotAFinding()
    {
        // 旧 plan.json 没有该字段（向后兼容）：不得据此阻断既有任务的续传。
        var objs = new[] { Obj("object-000001", @"\\SRC\D", null), Obj("object-000002", @"\\SRC\D", "") };
        Assert.Empty(SourceIdentityGuard.Evaluate(objs, _ => "fsid:NEW"));
    }

    [Fact]
    public void B12b4_UnreadableNow_IsNotAFinding()
    {
        // 取不到指纹（权限/服务不可用）不能当成"身份变了"——宁可不判，不可误判。
        var objs = new[] { Obj("object-000001", @"\\SRC\D", "fsid:AAAA") };
        Assert.Empty(SourceIdentityGuard.Evaluate(objs, _ => null));
    }

    [Fact]
    public void B12b4_MixedObjects_OnlyChangedOnesReported()
    {
        var objs = new[]
        {
            Obj("object-000001", @"\\SRC\D", "fsid:same"),
            Obj("object-000002", @"\\SRC\E", "fsid:old"),
            Obj("object-000003", @"\\SRC\F", null),
        };
        var found = SourceIdentityGuard.Evaluate(objs, p => p.EndsWith("\\E") ? "fsid:new" : "fsid:same");
        var f = Assert.Single(found);
        Assert.Equal("object-000002", f.ObjectId);
    }

    [Fact]
    public void B12b4_AbortMessage_SaysWhatHappened_WhyStopped_NextStep()
    {
        var findings = new List<SourceIdentityFinding>
        {
            new("object-000001", @"\\LAB-SRC01\D", "fsid:A", "fsid:B")
        };
        var msg = SourceIdentityGuard.BuildAbortMessage(findings);

        Assert.Contains("源身份校验未通过", msg);
        Assert.Contains("没有复制任何文件", msg);            // 必须明确"没继续写目标端"
        Assert.Contains(@"\\LAB-SRC01\D", msg);              // 指出是哪个源
        Assert.Contains("fsid:A", msg);
        Assert.Contains("fsid:B", msg);
        Assert.Contains("新建", msg);                        // 可执行的下一步
        Assert.Contains("共享", msg);
    }

    [Fact]
    public void B12b4_Capture_IsStablePerDirectory_AndDiffersAcrossDirectories()
    {
        var a = Path.Combine(Path.GetTempPath(), "pcmig-fixbatch-a-" + Guid.NewGuid().ToString("N"));
        var b = Path.Combine(Path.GetTempPath(), "pcmig-fixbatch-b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        try
        {
            var ia = SourceIdentity.Capture(a);
            var ia2 = SourceIdentity.Capture(a);
            var ib = SourceIdentity.Capture(b);

            Assert.NotNull(ia);
            Assert.StartsWith("fsid:", ia);
            Assert.Equal(ia, ia2);                 // 同一目录稳定
            Assert.NotEqual(ia, ib);               // 不同目录必然不同

            // 取不到的情形一律 null（绝不返回"看起来像基线"的假值）
            Assert.Null(SourceIdentity.Capture(Path.Combine(a, "does-not-exist-" + Guid.NewGuid().ToString("N"))));
            Assert.Null(SourceIdentity.Capture(null));
            Assert.Null(SourceIdentity.Capture("   "));
        }
        finally
        {
            try { Directory.Delete(a, true); } catch { /* 清理失败不影响断言 */ }
            try { Directory.Delete(b, true); } catch { /* 同上 */ }
        }
    }

    [Fact]
    public void B12b4_IdentityChanged_ComparesPerComponent_AndNeverGuesses()
    {
        // 两个分量：fsid（文件 ID）与 sharepath（共享物理路径）。只有两边都提供的分量参与判定。
        Assert.False(SourceIdentity.IdentityChanged("fsid:A;sharepath:D:\\", "fsid:A;sharepath:D:\\"));
        // 共享物理路径变了（同名换底的核心症状：共享名没变、背后目录换了）
        Assert.True(SourceIdentity.IdentityChanged("fsid:A;sharepath:D:\\", "fsid:A;sharepath:C:\\B12B4OTHERDIR"));
        // 文件 ID 变了
        Assert.True(SourceIdentity.IdentityChanged("fsid:A;sharepath:D:\\", "fsid:B;sharepath:D:\\"));
        // 某一边缺分量 ⇒ 该分量不参与（宁可少判，不可误判）
        Assert.False(SourceIdentity.IdentityChanged("fsid:A", "sharepath:C:\\"));
        Assert.False(SourceIdentity.IdentityChanged("fsid:A;sharepath:D:\\", "fsid:A"));
        Assert.False(SourceIdentity.IdentityChanged(null, "fsid:B"));
        Assert.False(SourceIdentity.IdentityChanged("fsid:A", null));
        // 归一化：大小写与尾部反斜杠不得造成假不一致
        Assert.Equal(SourceIdentity.NormalizeSharePath("D:\\"), SourceIdentity.NormalizeSharePath("d:"));
    }

    [Fact]
    public void B12b4_IdentityFormat_IsStable()
    {
        // 实测样例：LAB-SRC01 的 D（vol 5E88E8CC88E8A3AD）与 D$ 指向同一物理路径 ⇒ 指纹必须一致。
        Assert.Equal("fsid:5E88E8CC88E8A3AD:00000000000000000005000000000005",
            SourceIdentity.Format(0x5E88E8CC88E8A3ADUL, 0x0000000000000000UL, 0x0005000000000005UL));
        // C$ 的卷序列号不同 ⇒ 指纹必然不同（不同盘不能被当成同一个源）。
        Assert.NotEqual(
            SourceIdentity.Format(0x5E88E8CC88E8A3ADUL, 0UL, 5UL),
            SourceIdentity.Format(0xF68AA0C68AA08529UL, 0UL, 5UL));
        Assert.Equal("（未取得）", SourceIdentity.Describe(null));
        Assert.Equal("fsid:X", SourceIdentity.Describe("fsid:X"));
    }

    // ─────────────────────────── O-B22c-1：Step 1 三态文案 ───────────────────────────

    private static PreflightReport Report(bool overallPass, bool verified, bool reused, params PreflightCheck[] checks)
    {
        var r = new PreflightReport { Host = "LAB-SRC01", OverallPass = overallPass };
        r.CredentialVerified = verified;
        r.CredentialReused = reused;
        r.Checks.AddRange(checks);
        return r;
    }

    [Fact]
    public void OB22c1_NormalConnect_WithShares_ReportsCount()
    {
        var r = Report(true, verified: true, reused: false);
        var s = ConnectionViewModel.BuildConnectStatus("LAB-SRC01", r, 4);
        Assert.Contains("已连接 LAB-SRC01", s);
        Assert.Contains("发现 4 个共享", s);
    }

    [Fact]
    public void OB22c1_VerifiedButZeroShares_MustNotClaimConnectionProblem()
    {
        var r = Report(true, verified: true, reused: false);
        var s = ConnectionViewModel.BuildConnectStatus("LAB-SRC01", r, 0);
        Assert.Contains("凭据已验证", s);
        Assert.Contains("没有列出任何共享", s);
        Assert.Contains("＋添加共享", s);              // 必须给可执行出路
    }

    [Fact]
    public void OB22c1_ReusedConnection_ZeroShares_MustSayCredentialsWereNotUsed()
    {
        // B22c run3 的真实链路：本机已有连接 ⇒ Windows 1219 ⇒ 产品复用旧连接，
        // 用户输入的账号根本没被使用，而界面过去只说「已连接…发现 0 个共享」。
        var r = Report(true, verified: false, reused: true);
        var s = ConnectionViewModel.BuildConnectStatus("LAB-SRC01", r, 0);
        Assert.Contains("你输入的账号没有被使用", s);
        Assert.Contains("1219", s);
        Assert.Contains(@"net use \\LAB-SRC01\ /delete", s);
        Assert.DoesNotContain("已连接 LAB-SRC01，发现 0 个共享", s);
    }

    [Fact]
    public void OB22c1_ReusedConnection_WithShares_StillDisclosesReuse()
    {
        var r = Report(true, verified: false, reused: true);
        var s = ConnectionViewModel.BuildConnectStatus("LAB-SRC01", r, 4);
        Assert.Contains("4 个共享", s);
        Assert.Contains("复用了本机已有的连接", s);
        Assert.Contains("你输入的账号没有被使用", s);
    }

    [Fact]
    public void OB22c1_UnverifiedCredentials_ZeroShares_NeverSaysConnected()
    {
        // 域控不可达 / 凭据无法校验：界面必须报"无法确认凭据"，不得宣称已连接。
        var r = Report(false, verified: false, reused: false,
            new PreflightCheck { Name = "IPC$ 凭据会话", Pass = false, Severity = "Error", Detail = "无法连接 \\\\LAB-SRC01\\IPC$（错误 67）：…目前无法确认这套账号密码是否可用。" });
        var s = ConnectionViewModel.BuildConnectStatus("LAB-SRC01", r, 0);
        Assert.Contains("无法确认这套账号密码是否可用", s);
        Assert.DoesNotContain("已连接 LAB-SRC01，发现", s);
    }

    [Fact]
    public void OB22c1_NeverEmitsTheAmbiguousZeroShareSentence_WhenCredentialIsNotVerified()
    {
        // 反例矩阵：只要凭据没被证实，"已连接 X，发现 0 个共享"这句话一个都不许出现。
        foreach (var overallPass in new[] { true, false })
        foreach (var reused in new[] { true, false })
        {
            var r = Report(overallPass, verified: false, reused: reused,
                new PreflightCheck { Name = "IPC$ 凭据会话", Pass = false, Severity = "Error", Detail = "细节" });
            var s = ConnectionViewModel.BuildConnectStatus("LAB-SRC01", r, 0);
            Assert.DoesNotContain("发现 0 个共享", s);
        }
    }

    [Fact]
    public void OB22c1_CredentialFlags_DefaultToFalse()
    {
        var r = new PreflightReport();
        Assert.False(r.CredentialVerified);
        Assert.False(r.CredentialReused);
    }

    // ─────────────────────────── 静态契约（防止口径被改回去） ───────────────────────────

    [Fact]
    public void A05_AuthCorroboration_MustNotTreatTcpPortAsCredentialProof()
    {
        var src = ReadSource("src", "PCMig.Core", "Preflight", "PreflightChecker.cs");
        // TCP 445 端口可达**不是**认证证据（B22c：源机 445 通、域控不可达、凭据无法校验）。
        Assert.DoesNotContain("var ipcHasCorroboration = smbOk", src);
        Assert.Contains("authProofs", src);
        Assert.Contains("report.CredentialVerified", src);
    }

    [Fact]
    public void OB03_2_ResultStatus_ExplainsEmptyShellDirectory()
    {
        var src = ReadSource("src", "PCMig.WinUI", "Presentation", "MigrationSessionViewModel.cs");
        Assert.Contains("同名空目录", src);
    }

    /// <summary>
    /// B12b4 的第一版修复（只在开跑前校验）在真机定向复验中被证明**不够**：
    /// 换底发生在首轮传输飞行中时，robocopy 仍按 UNC 路径继续取数，冒名文件照样落盘
    /// （TV-B12b4 tv1 实测目标端出现只在冒名树里存在的 env-check-downloads.txt）。
    /// 因此守护必须同时挂在文件复制回调上（传输期间周期性复验），并把结果记为 Failed。
    /// </summary>
    [Fact]
    public void B12b4_InFlightGuard_MustBeWiredIntoTransferLoop_NotOnlyAtEntry()
    {
        var src = ReadSource("src", "PCMig.Core", "Transfer", "TransferOrchestrator.cs");

        // 对象边界校验
        Assert.Contains("GuardActiveObjectIdentity(force: true)", src);
        // 飞行中周期性复验必须挂在"每复制一个文件"的回调上
        Assert.Contains("GuardActiveObjectIdentity();", src);
        // 检测到变化必须真正终止当前 robocopy
        Assert.Contains("_runner.KillCurrent()", src);
        // 收尾必须走 Failed 分支（不能并进普通"已中断/可续传"口径，否则用户会以为只是断了网）
        Assert.Contains("else if (_identityLost)", src);
        Assert.Contains("源身份在传输过程中发生变化", src);
    }

    [Fact]
    public void B12b4_IdentityGuardInterval_MustBeSmallEnoughToBoundTheExposureWindow()
    {
        var src = ReadSource("src", "PCMig.Core", "Transfer", "TransferOrchestrator.cs");
        Assert.Contains("IdentityGuardInterval", src);
        // 检测窗口必须远小于一次高速复制的时间量级（B12b4 实测整轮传输仅约 2 秒）
        Assert.Contains("IdentityGuardInterval = TimeSpan.FromSeconds(2)", src);
    }
}