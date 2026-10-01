using PCMig.Core.Jobs;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.State;
using PCMig.Core.Transfer;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>恢复时进度必须由 Completed Receipt 重建，不能盲信陈旧 job-state.json。</summary>
public class ResumeProgressReconstructionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-resume-" + Guid.NewGuid().ToString("N")[..8]);

    public ResumeProgressReconstructionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* 文件句柄延迟释放 */ } }

    [Fact]
    public async Task RunAsync_StaleStateWithCompletedReceipts_RebuildsFirstProgressAndFinalState()
    {
        var ctx = CreateContext("J-rebuild", ("object-1", 100), ("object-2", 200));
        ctx.SaveState(new JobState { JobId = ctx.JobId, Phase = JobPhase.Interrupted, TotalObjects = 99, CompletedObjects = 0, TotalBytes = 999, CompletedBytes = 1 });
        WriteReceipt(ctx, "old-object-1.json", "object-1", ObjectStatus.Completed, 100, DateTime.UnixEpoch.AddMinutes(1));
        WriteReceipt(ctx, "old-object-2.json", "object-2", ObjectStatus.Completed, 200, DateTime.UnixEpoch.AddMinutes(2));

        var snapshots = new List<ProgressSnapshot>();
        var phase = await RunAsync(ctx, snapshots);
        var state = ctx.LoadStateOrNew();

        Assert.Equal(JobPhase.Completed, phase);
        Assert.Equal(2, snapshots[0].CompletedObjects);
        Assert.Equal(300, snapshots[0].CompletedBytes);
        Assert.Equal(2, state.CompletedObjects);
        Assert.Equal(300, state.CompletedBytes);
        Assert.Equal(JobPhase.Completed, state.Phase);
    }

    [Fact]
    public async Task RunAsync_MultipleCompletedReceiptsForSameObject_UsesLatestReceipt()
    {
        var ctx = CreateContext("J-latest", ("object-1", 250));
        ctx.SaveState(new JobState { JobId = ctx.JobId, CompletedObjects = 42, CompletedBytes = 42 });
        WriteReceipt(ctx, "a-old.json", "object-1", ObjectStatus.Completed, 100, DateTime.UnixEpoch.AddMinutes(1));
        WriteReceipt(ctx, "z-new.json", "object-1", ObjectStatus.Completed, 250, DateTime.UnixEpoch.AddMinutes(2));

        var snapshots = new List<ProgressSnapshot>();
        await RunAsync(ctx, snapshots);

        Assert.Equal(1, snapshots[0].CompletedObjects);
        Assert.Equal(250, snapshots[0].CompletedBytes);
        Assert.Equal(250, ctx.LoadStateOrNew().CompletedBytes);
    }

    [Fact]
    public async Task RunAsync_FailedAndInterruptedReceipts_DoNotCountAsCompleted()
    {
        var ctx = CreateContext("J-incomplete", ("object-1", 100), ("object-2", 200), ("object-3", 300));
        ctx.SaveState(new JobState { JobId = ctx.JobId, CompletedObjects = 3, CompletedBytes = 600 });
        WriteReceipt(ctx, "completed.json", "object-1", ObjectStatus.Completed, 100, DateTime.UnixEpoch.AddMinutes(1));
        WriteReceipt(ctx, "failed.json", "object-2", ObjectStatus.Failed, 200, DateTime.UnixEpoch.AddMinutes(2));
        WriteReceipt(ctx, "interrupted.json", "object-3", ObjectStatus.Interrupted, 300, DateTime.UnixEpoch.AddMinutes(3));
        ctx.RequestPause(immediate: false);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        var snapshots = new List<ProgressSnapshot>();
        var phase = await RunAsync(ctx, snapshots, cancel.Token);
        var state = ctx.LoadStateOrNew();

        Assert.Equal(JobPhase.Paused, phase);
        Assert.Equal(1, snapshots[0].CompletedObjects);
        Assert.Equal(100, snapshots[0].CompletedBytes);
        Assert.Equal(1, state.CompletedObjects);
        Assert.Equal(100, state.CompletedBytes);
    }

    private JobContext CreateContext(string jobId, params (string Id, long Bytes)[] objects)
    {
        var ctx = new JobContext
        {
            JobDir = Path.Combine(_root, jobId),
            Definition = new JobDefinition { JobId = jobId, TargetRoot = Path.Combine(_root, "target", jobId) },
            Plan = new MigrationPlan
            {
                JobId = jobId,
                TotalBytes = objects.Sum(x => x.Bytes),
                Objects = objects.Select(x => new PlannedObject
                {
                    ObjectId = x.Id,
                    SourcePath = Path.Combine(_root, "source", x.Id),
                    TargetPath = Path.Combine(_root, "target", jobId, x.Id),
                    EstimatedBytes = x.Bytes
                }).ToList()
            }
        };
        ctx.EnsureDirs();
        return ctx;
    }

    private static void WriteReceipt(JobContext ctx, string fileName, string objectId, ObjectStatus status, long bytes, DateTime completedUtc)
        => JsonStateStore.WriteAtomic(Path.Combine(ctx.ReceiptsDir, fileName), new ObjectReceipt
        {
            ObjectId = objectId,
            Status = status,
            TargetBytes = bytes,
            CompletedUtc = completedUtc
        });

    private static Task<JobPhase> RunAsync(JobContext ctx, List<ProgressSnapshot> snapshots, CancellationToken ct = default)
        => new TransferOrchestrator(ctx, new MigrationMatrix(), Serilog.Core.Logger.None)
            .RunAsync(new Progress<ProgressSnapshot>(snapshots.Add), ct);
}
