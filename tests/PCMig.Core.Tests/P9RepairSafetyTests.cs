// ============================================================================
//  ★ R2-A-1 / R2-A-2 回归 ★ 修复入口的数据安全边界
// ============================================================================
//
//  ── 问题（v0.5.4 最终收口任务令 §R2-A，2026-10-24）──────────────────────────
//  CollectRepairTargets 曾用 `Status != "OK"` 当修复选择器，把四种完全不同的结论
//  压成同一句"要修"：
//     · OK      —— 本就不该进来；
//     · EMPTY   —— 没有可核对的数据（源侧无待复制文件），却被当成"不一致"去重拷；
//     · UNKNOWN —— 源/目标不可达、枚举不全、两侧 0/0 与计划矛盾，被当成"不一致"
//                  直接强制覆盖（"测不出来"被当成"确实不同"）；
//     · MISMATCH—— 两侧都数得清且确实不一致，这才是唯一有证据的修复依据。
//  现场真实案例：OK 28 / EMPTY 10 / MISMATCH 1 / UNKNOWN 1 ⇒ 旧逻辑会重拷 12 个对象，
//  其中 11 个没有任何"确认差异"的依据。
//
//  同一轮还缺一道门：源共享确证失效时，修复仍会启动，等于"一个环境错误 → 对目标侧
//  同名文件整体强制覆盖"。现场教训见 RepairGate 的注释。
//
//  ── 本文件钉死的契约 ─────────────────────────────────────────────────────
//    R2A1-1 OK 不进目标；
//    R2A1-2 EMPTY 不进目标，单独归类为"无可核对数据"；
//    R2A1-3 UNKNOWN（及其它未定结论）不进目标，且要求先重新核验；
//    R2A1-4 只有 MISMATCH 与"失败/中断回执"进目标；
//    R2A1-5 结论不明的对象即使带失败回执也不覆盖（按更安全的一侧处理）；
//    R2A1-6 现场比例复现：OK 28 / EMPTY 10 / MISMATCH 1 / UNKNOWN 1 ⇒ 目标恰 1 个。
//    R2A2-1 源共享**确证**不可达 ⇒ 修复被拒绝，且不产生任何状态/回执改动；
//    R2A2-2 证据过期（尚未确认）不算确证不可达 ⇒ 不得用这条理由拒绝用户修复。
//
//  纪律：不注入假 Verifier、不伪造验证结论；验证报告由夹具按真实结构落盘。
//  传输引擎只在"必须走到重拷"的用例里替换为不联网的假实现。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PCMig.Core.Jobs;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using PCMig.Diagnostics.Abstractions;
using PCMig.WinUI.Presentation;
using Serilog;
using Xunit;

namespace PCMig.Core.Tests;

[Collection("A5-jobdir-isolation")]
public sealed class P9RepairSafetyTests : IDisposable
{
    private readonly string _sandbox;
    private readonly ILogger _log;
    private readonly string? _previousJobsRoot;
    private readonly Func<DateTime> _savedClock = ConnectionViewModel.Clock;
    private readonly Func<string, bool> _savedReadProbe = ConnectionViewModel.ShareReadProbe;
    private readonly Func<string, int> _savedCountProbe = ConnectionViewModel.ShareCountProbe;

    public P9RepairSafetyTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "pcmig-r2a-repair", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
        _previousJobsRoot = Environment.GetEnvironmentVariable("PCMIG_JOBS");
        Environment.SetEnvironmentVariable("PCMIG_JOBS", Path.Combine(_sandbox, "Jobs"));
        _log = new LoggerConfiguration().CreateLogger();

        // 确定性探针：本轮用例不触网。任何真实探测都视为"无结论"。
        ConnectionViewModel.ShareReadProbe = _ => throw new TimeoutException("R2-A 夹具：探测无结论");
        ConnectionViewModel.ShareCountProbe = _ => throw new TimeoutException("R2-A 夹具：探测无结论");
    }

    public void Dispose()
    {
        ConnectionViewModel.Clock = _savedClock;
        ConnectionViewModel.ShareReadProbe = _savedReadProbe;
        ConnectionViewModel.ShareCountProbe = _savedCountProbe;
        Environment.SetEnvironmentVariable("PCMIG_JOBS", _previousJobsRoot);
        try { Directory.Delete(_sandbox, recursive: true); } catch { /* 清理失败不影响结论 */ }
    }

    // ───────────────────────── 夹具 ─────────────────────────

    /// <summary>
    /// 造一个"已跑完一轮、有失败回执"的任务，并按需落一份验证报告。
    /// 目标侧预置与源同内容的文件：需要重拷的用例里，重拷后真实复验也会是 OK。
    /// </summary>
    private JobContext Arrange(
        string jobId,
        (string Id, string Status, string? Reason)[]? verifyObjects = null,
        bool failedReceipt = true)
    {
        var src = Path.Combine(_sandbox, jobId, "source");
        var dst = Path.Combine(_sandbox, jobId, "target", "obj1");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        var srcFile = Path.Combine(src, "shared.txt");
        File.WriteAllText(srcFile, "P9-REPAIR-SAFETY-PAYLOAD");
        File.WriteAllText(Path.Combine(dst, "shared.txt"), "P9-REPAIR-SAFETY-PAYLOAD");
        var size = new FileInfo(srcFile).Length;

        var obj = new PlannedObject
        {
            ObjectId = "object-000001",
            Kind = ObjectKind.DataVolume,
            SourcePath = src,
            TargetPath = dst,
            EstimatedBytes = size,
            EstimatedFiles = 1,
        };

        var ctx = new JobContext
        {
            JobDir = Path.Combine(_sandbox, jobId, "job"),
            Definition = new JobDefinition { JobId = jobId, TargetRoot = Path.Combine(_sandbox, jobId, "target") },
        };
        ctx.EnsureDirs();
        ctx.SaveDefinition();
        ctx.SavePlan(new MigrationPlan
        {
            JobId = jobId,
            Objects = new List<PlannedObject> { obj },
            TotalBytes = size,
        });
        ctx.SaveState(new JobState
        {
            JobId = jobId,
            Phase = failedReceipt ? JobPhase.CompletedWithErrors : JobPhase.Completed,
            TotalObjects = 1,
            CompletedObjects = failedReceipt ? 0 : 1,
            FailedObjects = failedReceipt ? 1 : 0,
            TotalBytes = size,
            CompletedBytes = failedReceipt ? 0 : size,
            Percent = failedReceipt ? 0 : 100,
        });
        if (failedReceipt)
        {
            ctx.SaveReceipt(new ObjectReceipt
            {
                ObjectId = obj.ObjectId,
                Kind = ObjectKind.DataVolume,
                SourcePath = src,
                TargetPath = dst,
                Status = ObjectStatus.Failed,
                ErrorDetail = "P9 夹具：上一轮失败，制造「需要修复」的前置条件",
                RobocopyExitCodeBulk = 8,
                CompletedUtc = DateTime.UtcNow,
            });
        }

        if (verifyObjects is { Length: > 0 })
        {
            var report = new VerifyReport
            {
                JobId = jobId,
                VerifiedUtc = DateTime.UtcNow,
                Level = VerifyLevel.L1_CountSize,
                OverallPass = false,
            };
            foreach (var (id, status, reason) in verifyObjects)
            {
                report.Objects.Add(new ObjectVerifyResult
                {
                    ObjectId = id,
                    Status = status,
                    UnknownReason = reason,
                    SourceFiles = 1,
                    TargetFiles = 1,
                    SourceBytes = size,
                    TargetBytes = size,
                });
            }
            ctx.SaveVerify(report);
        }
        return ctx;
    }

    private static async Task WithInlineSyncContext(Func<Task> body)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        try { await body(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private sealed class InlineSyncContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    // ══════════════════ R2A1-1 OK 不进目标 ══════════════════

    [Fact]
    public async Task R2A1_1_一致的对象不得进入修复目标()
    {
        var ctx = Arrange("JOB-R2A-OK",
            verifyObjects: new (string, string, string?)[] { ("object-000001", "OK", null) },
            failedReceipt: false);
        var vm = new MigrationSessionViewModel(null, null, _log);
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        var sel = vm.CollectRepairSelection();

        Assert.Empty(sel.Targets);
        Assert.Equal(1, sel.SkippedOk);
        Assert.Empty(sel.RequireReverify);
        Assert.Empty(sel.SkippedNoData);
    }

    // ══════════════════ R2A1-2 EMPTY 不进目标 ══════════════════

    [Fact]
    public async Task R2A1_2_无可核对数据的对象不得进入修复目标()
    {
        var ctx = Arrange("JOB-R2A-EMPTY",
            verifyObjects: new (string, string, string?)[] { ("object-000001", "EMPTY", null) },
            failedReceipt: false);
        var vm = new MigrationSessionViewModel(null, null, _log);
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        var sel = vm.CollectRepairSelection();

        Assert.Empty(sel.Targets);                       // 关键：没东西可核 ⇒ 不该重拷
        Assert.Equal("object-000001", Assert.Single(sel.SkippedNoData));
        Assert.Equal(0, sel.BeforeMismatch);             // EMPTY 过去被算成 mismatch，现在不算
    }

    // ══════════════════ R2A1-3 UNKNOWN 要求先重新核验 ══════════════════

    [Theory]
    [InlineData("UNKNOWN", "source-unavailable")]
    [InlineData("UNKNOWN", "enumeration-incomplete")]
    [InlineData("UNKNOWN", "both-sides-empty-against-plan")]
    [InlineData("CANCELED", null)]
    public async Task R2A1_3_结论不明的对象不得进入修复目标_必须要求重新核验(string status, string? reason)
    {
        var ctx = Arrange("JOB-R2A-UNKNOWN-" + status + "-" + (reason ?? "none"),
            verifyObjects: new (string, string, string?)[] { ("object-000001", status, reason) },
            failedReceipt: false);
        var vm = new MigrationSessionViewModel(null, null, _log);
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        var sel = vm.CollectRepairSelection();

        Assert.Empty(sel.Targets);
        Assert.Equal("object-000001", Assert.Single(sel.RequireReverify));
    }

    // ══════════════════ R2A1-4 MISMATCH 与失败回执仍进目标 ══════════════════

    [Fact]
    public async Task R2A1_4_确认差异与失败回执仍是修复目标()
    {
        var ctx = Arrange("JOB-R2A-MISMATCH",
            verifyObjects: new (string, string, string?)[] { ("object-000001", "MISMATCH", null), ("object-000002", "OK", null) },
            failedReceipt: false);
        var vm = new MigrationSessionViewModel(null, null, _log);
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        var sel = vm.CollectRepairSelection();

        Assert.Equal("object-000001", Assert.Single(sel.Targets));
        Assert.Equal(1, sel.BeforeMismatch);
        Assert.Equal(1, sel.SkippedOk);
    }

    [Fact]
    public async Task R2A1_4b_失败回执的对象即使没有验证报告也应修复()
    {
        var ctx = Arrange("JOB-R2A-FAILED-RECEIPT", verifyObjects: null, failedReceipt: true);
        var vm = new MigrationSessionViewModel(null, null, _log);
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        var sel = vm.CollectRepairSelection();

        Assert.Equal("object-000001", Assert.Single(sel.Targets));
        Assert.Equal(0, sel.BeforeMismatch);   // 没有验证报告 ⇒ 没有"确认差异"计数
    }

    // ══════════════════ R2A1-5 更安全的一侧优先 ══════════════════

    [Fact]
    public async Task R2A1_5_结论不明的对象即使带失败回执也不得被覆盖()
    {
        var ctx = Arrange("JOB-R2A-UNKNOWN-PLUS-FAILED",
            verifyObjects: new (string, string, string?)[] { ("object-000001", "UNKNOWN", "target-unavailable") },
            failedReceipt: true);
        var vm = new MigrationSessionViewModel(null, null, _log);
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        var sel = vm.CollectRepairSelection();

        Assert.Empty(sel.Targets);                        // 有失败回执，但结论不明 ⇒ 不覆盖
        Assert.Equal("object-000001", Assert.Single(sel.RequireReverify));
    }

    // ══════════════════ R2A1-6 现场比例复现 ══════════════════

    [Fact]
    public async Task R2A1_6_现场案例_OK28_EMPTY10_MISMATCH1_UNKNOWN1_只修一个对象()
    {
        var objs = new List<(string, string, string?)>();
        for (var i = 0; i < 28; i++) objs.Add(($"object-ok-{i:00}", "OK", null));
        for (var i = 0; i < 10; i++) objs.Add(($"object-empty-{i:00}", "EMPTY", null));
        objs.Add(("object-mismatch", "MISMATCH", null));
        objs.Add(("object-unknown", "UNKNOWN", "source-unavailable"));

        var ctx = Arrange("JOB-R2A-REAL-CASE", verifyObjects: objs.ToArray(), failedReceipt: false);
        var vm = new MigrationSessionViewModel(null, null, _log);
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        var sel = vm.CollectRepairSelection();

        Assert.Equal(28, sel.SkippedOk);
        Assert.Equal(10, sel.SkippedNoData.Count);
        Assert.Single(sel.RequireReverify);
        Assert.Equal("object-mismatch", Assert.Single(sel.Targets));   // 旧逻辑这里会是 12 个
        Assert.Equal(1, sel.BeforeMismatch);
    }

    // ══════════════════ R2A2-1 源确证不可达 ⇒ 拒绝修复 ══════════════════

    [Fact]
    public async Task R2A2_1_源共享确证不可达时修复必须被拒绝且不做任何改动()
    {
        var ctx = Arrange("JOB-R2A2-DEAD",
            verifyObjects: new (string, string, string?)[] { ("object-000001", "MISMATCH", null) },
            failedReceipt: true);

        var conn = new ConnectionViewModel { Host = "192.0.2.10" };
        // 权威失效事实：确证不可达（不是"证据过期"）
        conn.NotifyShareReachability("192.0.2.10", false, "共享已不可访问", authoritative: true);
        Assert.True(conn.SourceKnownUnreachable, "夹具前提：必须是确证不可达");

        var vm = new MigrationSessionViewModel(conn, null, _log);
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        var stateBefore = ctx.LoadStateOrNew();
        var receiptsBefore = ctx.LoadReceipts(_log).Count;
        var engineCalled = false;
        vm.TransferRunner = (c, hooks, progress, ct, only, force) =>
        {
            engineCalled = true;
            return Task.FromResult(JobPhase.Completed);
        };

        await WithInlineSyncContext(() => vm.RepairAsync(forceOverwrite: true, password: null));

        Assert.False(engineCalled, "源共享确证不可达时不得启动任何重拷");
        Assert.Equal(DiagnosticOutcome.Rejected, vm.LastRepairOutcome.Outcome);
        Assert.Equal("repair-source-unreachable", vm.LastRepairOutcome.ReasonCode);
        Assert.Equal(stateBefore.Phase, ctx.LoadStateOrNew().Phase);
        Assert.Equal(receiptsBefore, ctx.LoadReceipts(_log).Count);
    }

    // ══════════════════ R2A2-2 证据过期 ≠ 确证不可达 ══════════════════

    [Fact]
    public async Task R2A2_2_证据过期不得被当成确证不可达而拒绝修复()
    {
        var ctx = Arrange("JOB-R2A2-EXPIRED",
            verifyObjects: new (string, string, string?)[] { ("object-000001", "MISMATCH", null) },
            failedReceipt: false);

        var now = new DateTime(2026, 10, 24, 3, 0, 0, DateTimeKind.Utc);
        ConnectionViewModel.Clock = () => now;

        var conn = new ConnectionViewModel { Host = "192.0.2.10" };
        conn.NotifyShareReachability("192.0.2.10", true, "验证实测源根可达");
        Assert.False(conn.SourceKnownUnreachable, "夹具前提：此刻是可达");

        // 推进到 TTL 之后：可达事实过期 ⇒ 无结论，但**不是**确证失效
        now = now.AddSeconds(ConnectionViewModel.EvidenceTtl.TotalSeconds + 5);
        conn.SweepEvidenceFreshness();
        Assert.False(conn.SourceKnownUnreachable, "证据过期只能算「未确认」，不得冒充确证不可达");
        Assert.Equal(SourceLampState.Disconnected, conn.LampState);
        Assert.Contains("证据已过期", conn.LampText);

        var vm = new MigrationSessionViewModel(conn, null, _log);
        Assert.True(await vm.AdoptExistingJobAsync(ctx.JobDir));

        var engineCalled = false;
        vm.TransferRunner = (c, hooks, progress, ct, only, force) =>
        {
            engineCalled = true;
            // 如实落一个完成态，避免自动复验因缺回执而待定
            c.SaveState(new JobState
            {
                JobId = c.JobId,
                Phase = JobPhase.Completed,
                TotalObjects = 1,
                CompletedObjects = 1,
                FailedObjects = 0,
                TotalBytes = 0,
                CompletedBytes = 0,
                Percent = 100,
                LastUpdateUtc = DateTime.UtcNow,
            });
            return Task.FromResult(JobPhase.Completed);
        };

        await WithInlineSyncContext(() => vm.RepairAsync(forceOverwrite: true, password: null));

        Assert.True(engineCalled, "证据过期不属于确证不可达：不得据此拒绝用户的修复请求");
        Assert.NotEqual("repair-source-unreachable", vm.LastRepairOutcome.ReasonCode);
    }
}