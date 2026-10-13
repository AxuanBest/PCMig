using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PCMig.Core.Jobs;
using PCMig.Core.Logging;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Verify;
using Serilog;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ E-1 / F-1（2026-10-13 定点修复）★ 验证真值与措辞契约。
///
/// 【E-1 缺陷回顾（真机证据，D2/D4）】目标文件**无法读取**时（`diffKind=unreadable`、
/// `hashReadFailed=1`、`Win32 32`），对象仍然判 `OK`、`overallPass=true`、退出码 0 ——
/// "无法核实内容"被当成了"验证通过"。这是验证可信度问题，不只是文案问题。
///
/// 【F-1 缺陷回顾（真机证据，D4）】`target-unavailable` 的对象在 CLI 控制台被逐条打成
/// 「缺失: a.bin」，而同一对象的报告卡片上写着「无法确认不是缺失」—— 两个出口自相矛盾，
/// 现场"假缺失"话术（进而"照着缺失去删目标文件"）就是这么来的。
/// </summary>
[Collection("A5-jobdir-isolation")]
public sealed class P0VerifyUnreadableTests : IDisposable
{
    private static readonly ILogger Log = new LoggerConfiguration().CreateLogger();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pcmig-e1f1-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousJobsRoot;

    public P0VerifyUnreadableTests()
    {
        _previousJobsRoot = Environment.GetEnvironmentVariable("PCMIG_JOBS");
        Environment.SetEnvironmentVariable("PCMIG_JOBS", Path.Combine(_root, "Jobs"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PCMIG_JOBS", _previousJobsRoot);
        try { Directory.Delete(_root, true); } catch { /* 清理失败不影响结论 */ }
    }

    // ── E-1：读不出来 ⇒ 不得判 OK，也不得整体宣称通过 ───────────────────────────

    [Fact]
    public async Task E1_目标文件读不出来_必须无法确认而不是通过()
    {
        var (src, tgt) = MakePair(8);
        var locks = LockAll(tgt);
        try
        {
            var r = await VerifyObjectAsync(src, tgt, VerifyLevel.L2_SampleHash);

            Assert.True(r.HashSampled > 0, "样本必须真的抽到（否则这条用例没测到读取失败路径）");
            Assert.True(r.HashReadFailed > 0, "目标文件被独占 ⇒ 必须记下读取失败");
            Assert.Equal("UNKNOWN", r.Status);                       // ★ 旧实现这里是 OK
            Assert.Equal("hash-unreadable", r.UnknownReason);
            Assert.NotEqual("MISMATCH", r.Status);                   // 读不出来 ≠ 内容不一致
            Assert.False(r.AllowsDestructiveRepair);                 // 更不得放行破坏性修复
        }
        finally { UnlockAll(locks); }
    }

    [Fact]
    public async Task E1_读不出来时整体报告不得宣称通过()
    {
        var (src, tgt) = MakePair(4);
        var locks = LockAll(tgt);
        try
        {
            var report = await VerifyAsync(src, tgt, VerifyLevel.L2_SampleHash);
            Assert.False(report.OverallPass);
            Assert.Equal("completed", report.RunStatus);
        }
        finally { UnlockAll(locks); }
    }

    [Fact]
    public async Task E1_对照_全部可读时仍然必须判通过()
    {
        var (src, tgt) = MakePair(8);

        var r = await VerifyObjectAsync(src, tgt, VerifyLevel.L2_SampleHash);

        Assert.Equal(0, r.HashReadFailed);
        Assert.Equal("OK", r.Status);
        Assert.True(r.MissingConfirmed == false);
    }

    // ── F-1：确证缺失 vs 无法确认 ───────────────────────────────────────────────

    [Fact]
    public async Task F1_真删掉一个目标文件_MissingConfirmed必须为真()
    {
        var (src, tgt) = MakePair(6);
        File.Delete(Path.Combine(tgt, "f3.bin"));

        var r = await VerifyObjectAsync(src, tgt, VerifyLevel.L1_CountSize);

        Assert.Equal("MISMATCH", r.Status);
        Assert.NotEmpty(r.MissingSamples);
        Assert.True(r.MissingConfirmed, "两侧都读到了、确实比对出差异 ⇒ 这才是确证缺失");
    }

    [Fact]
    public async Task F1_目标根不可达时_收集到的路径不得被当作确证缺失()
    {
        var src = NewDir("f1-src");
        for (var i = 0; i < 3; i++) WriteFile(src, $"f{i}.bin", 32);

        // 目标根指向不存在的目录 ⇒ target-unavailable（读不到那一侧）
        var r = await VerifyObjectAsync(src, Path.Combine(_root, "no-such-target"), VerifyLevel.L1_CountSize);

        Assert.Equal("UNKNOWN", r.Status);
        Assert.Equal("target-unavailable", r.UnknownReason);
        Assert.False(r.MissingConfirmed);                 // ★ 关键：这些路径不是"缺失证据"
        Assert.False(r.AllowsDestructiveRepair);
    }

    [Fact]
    public async Task F1_源不可达时同样不得自称有确证缺失()
    {
        var tgt = NewDir("f1-tgt2");
        for (var i = 0; i < 3; i++) WriteFile(tgt, $"f{i}.bin", 32);

        var r = await VerifyObjectAsync(Path.Combine(_root, "no-such-source"), tgt, VerifyLevel.L1_CountSize);

        Assert.Equal("UNKNOWN", r.Status);
        Assert.Equal("source-unavailable", r.UnknownReason);
        Assert.False(r.MissingConfirmed);
    }

    // ── 措辞契约：所有人类可读出口都必须按 MissingConfirmed 分档 ─────────────────

    [Fact]
    public void F1_四个出口都按MissingConfirmed门控用词()
    {
        var cli = ReadRepoFile("src", "PCMig.Cli", "Program.cs");
        Assert.Contains("o.MissingConfirmed ? $\"     缺失: {m}\"", cli);
        Assert.Contains("无法确认（未核实，不得当作缺失）", cli);
        // 旧写法（无条件打"缺失"）不得复活
        Assert.DoesNotContain("Console.WriteLine($\"     缺失: {m}\")", cli);

        var migrationReport = ReadRepoFile("src", "PCMig.Core", "Report", "ReportGenerator.cs");
        Assert.Contains("o.MissingConfirmed", migrationReport);

        var verifyReport = ReadRepoFile("src", "PCMig.Core", "Report", "VerifyReportGenerator.cs");
        Assert.Contains("o.MissingConfirmed", verifyReport);

        var sessionVm = ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MigrationSessionViewModel.cs");
        Assert.Contains("r.MissingConfirmed", sessionVm);

        var panel = ReadRepoFile("src", "PCMig.WinUI", "Views", "VerificationProgressPanel.xaml.cs");
        Assert.Contains("o.MissingConfirmed", panel);
    }

    [Fact]
    public void E1_验证器里unreadable分支必须先于OK且带原因码()
    {
        var code = ReadRepoFile("src", "PCMig.Core", "Verify", "Verifier.cs");
        var idxUnknown = code.IndexOf("hash-unreadable", StringComparison.Ordinal);
        var idxMissingConfirmed = code.IndexOf("result.MissingConfirmed = result.Status == \"MISMATCH\"", StringComparison.Ordinal);

        Assert.True(idxUnknown > 0, "必须存在 hash-unreadable 分支");
        Assert.True(idxMissingConfirmed > 0, "必须写入 MissingConfirmed");
        Assert.True(idxUnknown < idxMissingConfirmed, "unreadable 判定必须发生在结论落定之前");
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private (string Src, string Tgt) MakePair(int files)
    {
        var src = NewDir("pair-src-" + Guid.NewGuid().ToString("N")[..6]);
        var tgt = NewDir("pair-tgt-" + Guid.NewGuid().ToString("N")[..6]);
        for (var i = 0; i < files; i++)
        {
            WriteFile(src, $"f{i}.bin", 64);
            WriteFile(tgt, $"f{i}.bin", 64);
        }
        return (src, tgt);
    }

    /// <summary>把目标侧全部文件以"独占"方式打开 ⇒ 验证器读它们必然失败（真机现场的同型形态）。</summary>
    private static List<FileStream> LockAll(string dir)
    {
        var handles = new List<FileStream>();
        foreach (var f in Directory.GetFiles(dir))
            handles.Add(new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None));
        return handles;
    }

    private static void UnlockAll(List<FileStream> handles)
    {
        foreach (var h in handles) h.Dispose();
    }

    private string NewDir(string name)
    {
        var p = Path.Combine(_root, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private static void WriteFile(string dir, string name, int bytes)
        => File.WriteAllBytes(Path.Combine(dir, name), new byte[bytes]);

    private JobContext NewJob(string src, string tgt)
    {
        var def = new JobDefinition
        {
            JobId = "JOB-E1-" + Guid.NewGuid().ToString("N")[..8],
            SourceHost = "SRC-PC",
            TargetRoot = NewDir("target-root"),
            Sources = { new SourceSpec { Path = @"\\SRC-PC\D", Kind = ObjectKind.DataVolume } },
            Options = new MigrationOptions { SampleHashPercent = 100 },   // 确定性抽到全部文件
        };
        var ctx = new JobManager(Log).Create(def);
        ctx.SavePlan(new MigrationPlan
        {
            JobId = "plan",
            TotalBytes = 64 * 8,
            Objects =
            {
                new PlannedObject
                {
                    ObjectId = "object-000001",
                    Kind = ObjectKind.DataVolume,
                    SourcePath = src,
                    TargetPath = tgt,
                    EstimatedFiles = 8,
                    EstimatedBytes = 64 * 8,
                },
            },
        });
        return ctx;
    }

    private async Task<ObjectVerifyResult> VerifyObjectAsync(string src, string tgt, VerifyLevel level)
    {
        var report = await VerifyAsync(src, tgt, level);
        return Assert.Single(report.Objects);
    }

    private async Task<VerifyReport> VerifyAsync(string src, string tgt, VerifyLevel level)
        => await new Verifier(NewJob(src, tgt), new MigrationMatrix(), Log).RunAsync(level);

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
