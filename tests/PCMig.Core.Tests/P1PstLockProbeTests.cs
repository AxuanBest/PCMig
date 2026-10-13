using PCMig.Core.Jobs;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Scan;
using PCMig.Core.Transfer;
using PCMig.Core.Util;
using Serilog;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ P1-08 / R8 ★ .pst/.ost 的"占用"判定：**扩展名只是候选，是否被锁必须实测**。
///
/// 现场背景：旧文案只看扩展名就断言"这些文件会被锁定导致迁移失败"，而归档用的 PST 根本不归
/// Outlook 打开——用户被告知"会失败"，实际一直迁得好好的（假警报）。
/// R8 的验收口径就是这两条分开：**未锁定 PST 可迁** vs **被独占的才报 Win32 32/33**。
/// </summary>
public sealed class P1PstLockProbeTests : IDisposable
{
    private static readonly ILogger Log = new LoggerConfiguration().CreateLogger();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-p1pst-" + Guid.NewGuid().ToString("N"));

    public P1PstLockProbeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* 清理失败不影响结论 */ }
    }

    private string NewDir(params string[] parts)
    {
        var p = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(p);
        return p;
    }

    private string NewPst(string dir, string name = "archive.pst", int bytes = 64)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, new byte[bytes]);
        return p;
    }

    // ---------- 探测本身 ----------

    [Fact]
    public void P1_08_未被锁定的PST可以读()
    {
        var pst = NewPst(NewDir("plain"));
        var r = LockProbe.Probe(pst);

        Assert.Equal(LockProbe.LockState.Readable, r.State);
        Assert.True(r.IsReadable);
        Assert.False(r.IsLocked);
        Assert.Equal(0, r.Win32Code);
    }

    [Fact]
    public void P1_08_被他人以共享方式打开仍算可迁移()
    {
        var pst = NewPst(NewDir("shared"));
        // Outlook 正常工作时其实是以共享方式持有数据文件的：这不等于"打不开"。
        using var holder = new FileStream(pst, FileMode.Open, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);

        var r = LockProbe.Probe(pst);
        Assert.Equal(LockProbe.LockState.Readable, r.State);
    }

    [Fact]
    public void P1_08_被独占打开才判被占用并带Win32共享冲突码()
    {
        var pst = NewPst(NewDir("locked"));
        using var holder = new FileStream(pst, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var r = LockProbe.Probe(pst);
        Assert.Equal(LockProbe.LockState.ExclusivelyLocked, r.State);
        Assert.Equal(32, r.Win32Code);                       // ERROR_SHARING_VIOLATION
        Assert.True(ShareLossSignal.IsFileLockWin32(r.Win32Code));
        Assert.False(ShareLossSignal.IsShareLossWin32(r.Win32Code));   // ★ 文件锁不是共享断开 ★
        Assert.Contains("独占", r.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void P1_08_文件不存在是缺失而不是被占用()
    {
        var r = LockProbe.Probe(Path.Combine(_root, "nope", "archive.pst"));
        Assert.Equal(LockProbe.LockState.Missing, r.State);
        Assert.False(r.IsLocked);
    }

    [Fact]
    public void P1_08_权限不足是另一件事不得说成被占用()
    {
        // 打开一个**目录**：Windows 返回访问被拒绝（不是共享冲突）。
        var r = LockProbe.Probe(NewDir("a-directory"));
        Assert.Equal(LockProbe.LockState.AccessDenied, r.State);
        Assert.False(r.IsLocked);
        Assert.False(ShareLossSignal.IsFileLockWin32(r.Win32Code));
    }

    // ---------- DirStat 记账 ----------

    [Fact]
    public void P1_08_候选数与实测独占数必须分开()
    {
        var dir = NewDir("counts");
        NewPst(dir, "archive.pst");
        NewPst(dir, "cache.ost");
        File.WriteAllText(Path.Combine(dir, "readme.txt"), "x");

        var stat = DirStat.Measure(dir, 512L * 1024 * 1024);

        Assert.Equal(3, stat.Files);
        Assert.Equal(2, stat.LockRiskFiles);        // 按扩展名的候选
        Assert.Equal(0, stat.LockedRiskFiles);      // 实测：一个都没被独占
    }

    [Fact]
    public void P1_08_真被独占的PST计入实测独占数()
    {
        var dir = NewDir("locked-count");
        var pst = NewPst(dir);
        using var holder = new FileStream(pst, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var stat = DirStat.Measure(dir, 512L * 1024 * 1024);
        Assert.Equal(1, stat.LockRiskFiles);
        Assert.Equal(1, stat.LockedRiskFiles);
    }

    [Fact]
    public void P1_08_关掉实测就只剩候选数()
    {
        var dir = NewDir("no-probe");
        var pst = NewPst(dir);
        using var holder = new FileStream(pst, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var stat = DirStat.Measure(dir, 512L * 1024 * 1024, probeLockRisk: false);
        Assert.Equal(1, stat.LockRiskFiles);
        Assert.Equal(0, stat.LockedRiskFiles);      // 没实测 ⇒ 不得凭空说"被占用"
    }

    [Fact]
    public void P1_08_实测有上限且候选数照记()
    {
        var dir = NewDir("cap");
        var holders = new List<FileStream>();
        try
        {
            for (var i = 0; i < DirStat.MaxLockProbes + 1; i++)
            {
                var pst = NewPst(dir, $"box{i:00}.pst");
                holders.Add(new FileStream(pst, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            }

            var stat = DirStat.Measure(dir, 512L * 1024 * 1024);

            Assert.Equal(DirStat.MaxLockProbes + 1, stat.LockRiskFiles);      // 候选全记
            Assert.Equal(DirStat.MaxLockProbes, stat.LockedRiskFiles);        // 实测到上限就停
        }
        finally
        {
            foreach (var h in holders) h.Dispose();
        }
    }

    // ---------- 扫描告警文案（行为，不只看源码文本） ----------

    [Fact]
    public async Task P1_08_未锁定的PST告警必须说可以正常迁移()
    {
        var source = NewDir("src-unlocked");
        NewPst(NewDir(source, "mail"), "archive.pst");

        var observed = await ScanAsync(source);

        Assert.Equal(1, observed.LockRiskFiles);
        Assert.Equal(0, observed.LockedRiskFiles);
        Assert.Contains(observed.Warnings, w => w.Contains("可以正常迁移", StringComparison.Ordinal));
        // 旧文案：一律断言"会被锁定导致迁移失败" —— 未锁定时不得再出现
        Assert.DoesNotContain(observed.Warnings, w => w.Contains("会失败", StringComparison.Ordinal) && !w.Contains("只有", StringComparison.Ordinal));
    }

    [Fact]
    public async Task P1_08_被独占的PST告警必须说清打不开()
    {
        var source = NewDir("src-locked");
        var pst = NewPst(source, "root.pst");   // 源根散落文件（另一条记账路径）
        using var holder = new FileStream(pst, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var observed = await ScanAsync(source);

        Assert.Equal(1, observed.LockRiskFiles);
        Assert.Equal(1, observed.LockedRiskFiles);
        Assert.Contains(observed.Warnings, w => w.Contains("实测当前打不开", StringComparison.Ordinal));
        Assert.Contains(observed.Warnings, w => w.Contains("32/33", StringComparison.Ordinal));
    }

    private async Task<ObservedState> ScanAsync(string source)
    {
        var job = new JobDefinition
        {
            JobId = "JOB-P1-08",
            SourceHost = "SRC-PC",
            TargetRoot = NewDir("dst"),
            Sources = { new SourceSpec { Path = source, Kind = ObjectKind.DataVolume } },
            CreatedBy = "test",
        };
        return await new SourceScanner(Log).ScanAsync(job, MigrationMatrix.Load(null, Log));
    }

    // ---------- 界面/文档口径（源码文本契约） ----------

    [Theory]
    [InlineData("src", "PCMig.Core", "Scan", "SourceScanner.cs")]
    [InlineData("src", "PCMig.WinUI", "Presentation", "MigrationSessionViewModel.cs")]
    [InlineData("src", "PCMig.Gui", "MainViewModel.cs")]
    public void P1_08_界面与扫描器不得再按扩展名断言会失败(params string[] parts)
    {
        var text = ReadFile(parts);

        Assert.Contains("LockedRiskFiles", text, StringComparison.Ordinal);

        // 只看**代码**：注释里允许保留"旧文案是什么"的说明（那正是这次修复的记录）。
        var code = string.Join("\n", text
            .Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        // 旧口径（只看扩展名就说"会被锁定导致迁移失败"）必须已经从**实际文案**里消失。
        Assert.DoesNotContain("它们会被锁定导致复制失败", code, StringComparison.Ordinal);
        Assert.DoesNotContain("这些文件会被锁定导致迁移失败", code, StringComparison.Ordinal);
        Assert.DoesNotContain("这些文件会被锁定导致复制失败", code, StringComparison.Ordinal);
    }

    private static string ReadFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln")))
            {
                var path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
                Assert.True(File.Exists(path), "缺少文件：" + path);
                return File.ReadAllText(path);
            }
        }
        throw new InvalidOperationException("找不到仓库根");
    }
}