using System;
using System.IO;
using PCMig.Core.Jobs;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ D1-c（P0-03 配套，2026-10-12）★ 强制修复的**报告级**证据闸门。
///
/// 对象级判据（<see cref="RepairGate"/>）只说"这个对象确认不一致"，但它不回答：
/// 这份报告是不是本任务的？那一轮验证跑完了吗？是对着**当前这份计划**验的吗？是**这个构建**产出的吗？
/// 这些说不清时，同样不许动目标数据。本文件逐一证明这些情况都会被挡住，且证据齐全时正常放行。
/// </summary>
public sealed class P0RepairEvidenceGateTests : IDisposable
{
    private readonly string _jobDir = Path.Combine(Path.GetTempPath(), "pcmig-evg-" + Guid.NewGuid().ToString("N")[..8]);

    public P0RepairEvidenceGateTests() => Directory.CreateDirectory(_jobDir);

    public void Dispose()
    {
        try { Directory.Delete(_jobDir, true); } catch (Exception) { /* 句柄延迟释放 */ }
    }

    private JobContext Ctx(string jobId = "JOB-EVG") => new()
    {
        JobDir = _jobDir,
        Definition = new JobDefinition { JobId = jobId },
    };

    private static string? CurrentVersion => typeof(RepairEvidenceGate).Assembly.GetName().Version?.ToString();

    private static VerifyReport Report(string jobId = "JOB-EVG") => new()
    {
        JobId = jobId,
        VerifiedUtc = DateTime.UtcNow,
        RunStatus = "completed",
        ToolVersion = CurrentVersion,
    };

    private string PlanPath => Path.Combine(_jobDir, "plan.json");

    // ---- 没有报告：不许动目标数据（老 Job 也一样） ----

    [Fact]
    public void 没有验证报告一律挡住()
    {
        Assert.False(RepairEvidenceGate.Allows(null, Ctx(), out var code, out var status));
        Assert.Equal(RepairEvidenceGate.NoReportCode, code);
        Assert.False(string.IsNullOrWhiteSpace(status));
    }

    // ---- 证据必须属于本任务 ----

    [Fact]
    public void 报告属于别的任务时挡住()
    {
        Assert.False(RepairEvidenceGate.Allows(Report("JOB-OTHER"), Ctx(), out var code, out _));
        Assert.Equal(RepairEvidenceGate.ForeignJobCode, code);
    }

    // ---- 那一轮验证必须真的跑完 ----

    [Theory]
    [InlineData("canceled")]
    [InlineData("failed")]
    [InlineData("partial")]
    public void 那一轮验证没跑完时挡住(string runStatus)
    {
        var report = Report();
        report.RunStatus = runStatus;
        Assert.False(RepairEvidenceGate.Allows(report, Ctx(), out var code, out _));
        Assert.Equal(RepairEvidenceGate.IncompleteRunCode, code);
    }

    // ---- 必须是对着"当前这份计划"验的 ----

    [Fact]
    public void 报告早于当前计划时挡住()
    {
        File.WriteAllText(PlanPath, "{}");
        var planWrittenUtc = File.GetLastWriteTimeUtc(PlanPath);

        var report = Report();
        report.VerifiedUtc = planWrittenUtc.AddSeconds(-10);      // 先验证、后重新生成计划

        Assert.False(RepairEvidenceGate.Allows(report, Ctx(), out var code, out var status));
        Assert.Equal(RepairEvidenceGate.StalePlanCode, code);
        Assert.Contains("早于当前计划", status, StringComparison.Ordinal);
    }

    [Fact]
    public void 计划晚于报告但此前已重新验证时放行()
    {
        File.WriteAllText(PlanPath, "{}");

        var report = Report();
        report.VerifiedUtc = DateTime.UtcNow.AddSeconds(1);        // 报告新于计划（正常顺序）

        Assert.True(RepairEvidenceGate.Allows(report, Ctx(), out var code, out _));
        Assert.Equal(RepairEvidenceGate.EvidenceOkCode, code);
    }

    // ---- 必须是同一个构建产出的证据 ----

    [Fact]
    public void 报告来自其它构建版本时挡住()
    {
        var report = Report();
        report.ToolVersion = "0.0.0.1-other-build";

        Assert.False(RepairEvidenceGate.Allows(report, Ctx(), out var code, out var status));
        Assert.Equal(RepairEvidenceGate.VersionMismatchCode, code);
        Assert.Contains("构建版本", status, StringComparison.Ordinal);
    }

    // ---- 证据齐全：放行（对象级判据仍会继续把关） ----

    [Fact]
    public void 证据齐全且新于计划时放行()
    {
        File.WriteAllText(PlanPath, "{}");
        var report = Report();
        report.VerifiedUtc = File.GetLastWriteTimeUtc(PlanPath).AddSeconds(5);

        Assert.True(RepairEvidenceGate.Allows(report, Ctx(), out var code, out _));
        Assert.Equal(RepairEvidenceGate.EvidenceOkCode, code);
    }

    // ---- 旧报告缺字段时既不放松、也不凭空造相反结论（交给对象级判据） ----

    [Fact]
    public void 旧报告缺少运行状态与版本字段时不在此处挡()
    {
        var report = Report();
        report.RunStatus = null;        // 旧报告没有这些字段
        report.ToolVersion = null;
        report.JobId = "";

        Assert.True(RepairEvidenceGate.Allows(report, Ctx(), out var code, out _));
        Assert.Equal(RepairEvidenceGate.EvidenceOkCode, code);
    }
}