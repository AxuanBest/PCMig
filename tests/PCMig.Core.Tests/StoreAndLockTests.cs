using PCMig.Core.Jobs;
using PCMig.Core.Models;
using PCMig.Core.State;
using PCMig.Core.Util;
using PCMig.Core.Verify;
using Xunit;

namespace PCMig.Core.Tests;

public class StoreAndLockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pcmig-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public StoreAndLockTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* 独占句柄可能延迟释放 */ } }

    [Fact]
    public void WriteAtomic_RoundtripsCamelCaseJsonAndCleansTmp()
    {
        var path = Path.Combine(_dir, "state.json");
        JsonStateStore.WriteAtomic(path, new JobState { JobId = "J1" });

        Assert.True(File.Exists(path));
        Assert.False(Directory.GetFiles(_dir, "*.tmp-*").Any(), "原子写成功后不应残留 .tmp");
        Assert.True(JsonStateStore.TryRead<JobState>(path, out var state));
        Assert.Equal("J1", state!.JobId);
        Assert.Equal(JobPhase.Created, state.Phase);
    }

    /// <summary>D1 事故回归：目标被外部程序独占时——跳过写入、发告警、不中断任务、解锁后自愈。</summary>
    [Fact]
    public void WriteAtomic_SkipsWithWarningWhenTargetLocked_ThenRecovers()
    {
        var path = Path.Combine(_dir, "locked.json");
        File.WriteAllText(path, "old");
        string? warning = null;
        JsonStateStore.OnWarning = w => warning = w;

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            JsonStateStore.WriteAtomic(path, new JobState { JobId = "J2" });   // 绝不能抛

        Assert.NotNull(warning);
        Assert.Contains("冷却窗口", warning);
        JsonStateStore.OnWarning = null;

        JsonStateStore.WriteAtomic(path, new JobState { JobId = "J3" });       // 解锁后自愈
        Assert.True(JsonStateStore.TryRead<JobState>(path, out var s) && s!.JobId == "J3");
    }

    [Fact]
    public void ReadAllReceipts_SkipsCorruptFilesAndReportsThem()
    {
        var dir = Path.Combine(_dir, "receipts");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.json"), """{"objectId":"o1","status":"completed"}""");
        File.WriteAllText(Path.Combine(dir, "b.json"), "{ 这不是json");
        File.WriteAllText(Path.Combine(dir, "c.json"), """{"objectId":"o2"}""");

        var corrupt = new List<string>();
        var list = JsonStateStore.ReadAllReceipts<ObjectReceipt>(dir, c => corrupt.Add(c));

        Assert.Equal(2, list.Count);
        Assert.Single(corrupt);
    }

    [Fact]
    public void JobLock_ExcludesSecondHolder_ReleasesOnDispose()
    {
        var jobDir = Path.Combine(_dir, "JOB-T");
        Directory.CreateDirectory(jobDir);
        var ctx = new JobContext { JobDir = jobDir, Definition = new JobDefinition { JobId = "JOB-T" } };

        using (var first = JobLock.TryAcquire(ctx, out _))
        {
            Assert.NotNull(first);
            Assert.Null(JobLock.TryAcquire(ctx, TimeSpan.Zero, out var reason));
            Assert.Contains("正被另一个进程持有", reason);
        }
        Assert.NotNull(JobLock.TryAcquire(ctx, out _));    // 释放后可再拿（进程死亡自动释放的语义）
    }

    [Fact]
    public void FilePatternMatcher_MatchesWildcardsAndExactNamesCaseInsensitive()
    {
        var match = FilePatternMatcher.Build(new[] { "~$*", "*.tmp", "desktop.ini" });
        Assert.True(match("备忘录.tmp"));
        Assert.True(match("~$文档.docx"));
        Assert.True(match("DESKTOP.INI"));
        Assert.False(match("notes.txt"));
        Assert.False(match("tmpfile"));
    }

    [Fact]
    public void StableHash_IsDeterministicAndBounded()
    {
        var a = Verifier.StableHash("object-000001\\数据\\报告.docx");
        var b = Verifier.StableHash("object-000001\\数据\\报告.docx");
        Assert.Equal(a, b);
        Assert.InRange(a, 0, 999);
    }
}
