using System;
using System.IO;
using System.Linq;
using PCMig.Core.Jobs;
using PCMig.Core.Models;
using Serilog;
using Serilog.Core;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// C03 真机发现（2026-10-04，LAB 三 VM）——**硬断电后的"旧任务状态"必须仍然可见**。
///
/// 现场事实（证据：E:\PCMigLab\Evidence\RouteA\Group-C\C03\run11 + ...\C04\run3）：
///   · 传输进行中（产品自己的 job-state.json 记着 Phase=running、Percent=4.44%、CompletedBytes=2332925189）
///     宿主机硬断电 ⇒ PCMig / robocopy / 本次状态写入一起被杀。
///   · 恢复后 job-state.json 变成**等长全 0 字节**（NTFS 只保住了长度，数据页未落盘 —— 产品没有
///     FlushFileBuffers，这一点产品自己已经按 "状态文件损坏" 处理，不算缺陷）。
///   · 但 UI 的"未完成任务检测"报 **"未匹配到可续传任务"**，而目标盘上真实躺着 4599 个文件 /
///     2 328 760 910 B 的半成品，用户既看不到"被中断的旧任务"，也拿不到 Resume / Verify / Repair。
///
/// 根因（Core）：<see cref="JobManager.ListAll"/> 在 job-state.json 读不出来时回退成
/// `new JobState { JobId = ... }`，其 <see cref="JobPhase"/> 是枚举默认值 <c>Created</c>；
/// 而 <see cref="JobManager.FindUnfinished"/> 先用 <c>ResumablePhases.Contains(j.Phase)</c> 过滤
/// ⇒ 损坏任务**在进入 UI 之前就被丢掉**，于是 UI 里那段本来正确的
/// "状态文件损坏，进度未知（以回执为准）"（<c>MigrationSessionViewModel.ApplyProbeResult</c>）
/// 永远不可达。
///
/// 本文件是**复现证据**：修之前第一条必红，修之后两条都绿。第二条是防"改过头"的反向锁：
/// 状态文件根本不存在（从未写过状态）时**不得**被当成可续传任务。
/// </summary>
[Collection("A5-jobdir-isolation")]
public sealed class C03PowerLossJobStateTests : IDisposable
{
    private const string Host = "C03-POWERLOSS-HOST";

    private readonly string _sandbox;
    private readonly ILogger _log;
    private readonly string? _previousJobsRoot;

    public C03PowerLossJobStateTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "pcmig-c03-powerloss", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);

        // 作业目录隔离：JobManager.JobsRoot 每次都读该环境变量。
        _previousJobsRoot = Environment.GetEnvironmentVariable("PCMIG_JOBS");
        Environment.SetEnvironmentVariable("PCMIG_JOBS", Path.Combine(_sandbox, "Jobs"));

        _log = new LoggerConfiguration().CreateLogger();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PCMIG_JOBS", _previousJobsRoot);
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    /// <summary>造一个真实任务：job.json + plan.json + job-state.json（Running，已传一部分）。</summary>
    private JobContext CreateRunningJob(string jobId)
    {
        var target = Path.Combine(_sandbox, "target", jobId);
        Directory.CreateDirectory(target);

        var def = new JobDefinition
        {
            JobId = jobId,
            SourceHost = Host,
            TargetRoot = target.TrimEnd(Path.DirectorySeparatorChar),
            Sources = { new SourceSpec { Path = @"\\" + Host + @"\D$", Kind = ObjectKind.DataVolume } },
            Options = new MigrationOptions { Threads = 16 },
        };
        var ctx = new JobManager(_log).Create(def);
        ctx.SavePlan(new MigrationPlan
        {
            JobId = jobId,
            TotalBytes = 1024,
            Objects =
            {
                new PlannedObject
                {
                    ObjectId = "object-000001",
                    Kind = ObjectKind.DataVolume,
                    SourcePath = @"\\" + Host + @"\D$",
                    TargetPath = target,
                    EstimatedBytes = 1024,
                },
            },
        });
        ctx.SaveState(new JobState
        {
            JobId = jobId,
            Phase = JobPhase.Running,
            TotalObjects = 1,
            TotalBytes = 1024,
            CompletedBytes = 512,
            Percent = 50.0,
        });
        return ctx;
    }

    private static string StatePath(JobContext ctx) => Path.Combine(ctx.JobDir, "job-state.json");

    /// <summary>
    /// ★ C03 复现 ★ 硬断电把 job-state.json 的数据页带走（等长全 0 字节）之后，
    /// 这个"被中断的旧任务"必须仍然出现在未完成任务里，并且必须被标成状态不可信。
    /// 修复前：FindUnfinished 返回空（Phase 回退成 Created 被 ResumablePhases 过滤掉）⇒ 红。
    /// </summary>
    [Fact]
    public void FindUnfinished_StillSurfacesJobAfterPowerLossZeroedItsStateFile()
    {
        var ctx = CreateRunningJob("JOB-C03-POWERLOSS");
        var statePath = StatePath(ctx);
        var originalLength = new FileInfo(statePath).Length;
        Assert.True(originalLength > 0, "夹具异常：job-state.json 应当有内容");

        // 现场表现：长度被保住，内容整片变成 0x00（数据页从未落盘）。
        File.WriteAllBytes(statePath, new byte[originalLength]);
        Assert.Equal(originalLength, new FileInfo(statePath).Length);

        var found = new JobManager(_log).FindUnfinished(Host, null);

        var job = Assert.Single(found);
        Assert.Equal("JOB-C03-POWERLOSS", job.JobId);
        Assert.True(job.StateUnreliable, "状态文件损坏的任务必须被标成 StateUnreliable（UI 靠它显示「进度未知」）");
        Assert.False(job.LiveHolder, "断电后 job.lock 无人持有，必须按可续传呈现");
    }

    /// <summary>
    /// 反向锁（防改过头）：状态文件**根本不存在** ⇒ 这从来不是一个被中断的传输，
    /// 不得被当成可续传任务报给用户。
    /// </summary>
    [Fact]
    public void FindUnfinished_DoesNotSurfaceJobWhoseStateFileNeverExisted()
    {
        var ctx = CreateRunningJob("JOB-C03-NOSTATE");
        File.Delete(StatePath(ctx));

        var found = new JobManager(_log).FindUnfinished(Host, null);

        Assert.Empty(found);
    }
}