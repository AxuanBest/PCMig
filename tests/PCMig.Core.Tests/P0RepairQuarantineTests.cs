using System;
using System.IO;
using System.Linq;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using Serilog;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ P0-03（2026-10-12 数据安全复核）★ 强制覆盖的"非破坏性"证明。
///
/// 旧实现是 `File.Delete(dst)` 再让 robocopy 重拷：删掉之后如果源共享断网、用户点了停止、
/// 目标盘写满、或者文件被别的进程锁住，旧数据就已经**不可恢复地**消失了。
/// 新实现改为同目录改名隔离：触发 robocopy 重拷的效果与删除等价，但旧副本始终留在磁盘上，
/// 只有"新副本被确认可信"（目标重新出现、长度等于源长度**且**与源 SHA256 一致）才丢弃。
/// ★ v0.5.5 ★ 长度相等不再是充分证据（/Z 预分配会让中断后的目标长度恰好等于源长度）；
/// 拿不到内容证据时一律保留旧副本并计入「待确认」，收尾闸门不通过时一个都不丢弃。
///
/// 本文件用**真实文件/真实句柄**证明下列性质，而不是只做字符串检查：
///   ① 可信新副本落地 ⇒ 才丢弃旧副本；
///   ② 源不可达 / 迁移未落地 ⇒ 旧副本保留，且能逐字节还原回原路径；
///   ③ 半截落地（长度不符）⇒ 不得丢弃旧副本；
///   ④ 同名同长度但内容不同 ⇒ 不得丢弃旧副本；
///   ⑤ 目标盘空间不足 ⇒ 本轮一个文件都不动（宁可这次不修）；
///   ⑥ 文件被独占占用 ⇒ 只计失败，旧数据仍在；
///   ⑦ 账本里出现坏行 ⇒ 不影响其余记录。
/// </summary>
public sealed class P0RepairQuarantineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-qrn-" + Guid.NewGuid().ToString("N")[..8]);

    public P0RepairQuarantineTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* 句柄延迟释放 */ }
    }

    private static readonly ILogger Silent = new LoggerConfiguration().CreateLogger();

    private string Ledger => Path.Combine(_root, "quarantine.jsonl");

    private (PlannedObject Obj, string Src, string Dst) MakeTrees(params string[] names)
    {
        var src = Path.Combine(_root, "src");
        var dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        foreach (var n in names)
        {
            File.WriteAllText(Path.Combine(src, n), "SRC-" + n);
            File.WriteAllText(Path.Combine(dst, n), "OLD-" + n);
        }

        return (new PlannedObject
        {
            ObjectId = "obj-qrn",
            Kind = ObjectKind.DataVolume,
            SourcePath = src,
            TargetPath = dst,
        }, src, dst);
    }

    private RepairPurge.PurgeResult Purge(PlannedObject obj)
    {
        var matrix = MigrationMatrix.Load(null, Silent);
        return RepairPurge.PurgeTargetCopies(obj, new MigrationOptions { LargeFileThresholdMB = 512 }, matrix, Silent, Ledger);
    }

    /// <summary>目标目录里那个名字的旧副本（`.pcmig-old-*`）；没有则 null。</summary>
    private static string? OldCopy(string dstDir, string name) =>
        Directory.GetFiles(dstDir, name + RepairQuarantine.Suffix + "*").SingleOrDefault();

    // ---- ① 只有可信新副本落地，才允许丢弃旧副本 ----

    [Fact]
    public void 可信新副本落地后才丢弃旧副本()
    {
        var (obj, src, dst) = MakeTrees("a.bin");

        var purge = Purge(obj);
        Assert.Equal(1, purge.Files);
        Assert.Equal(0, purge.Failed);
        Assert.False(purge.BlockedNoSpace);
        Assert.False(File.Exists(Path.Combine(dst, "a.bin")), "目标侧同名文件必须不再挡路（否则 robocopy 会跳过）");
        var old = OldCopy(dst, "a.bin");
        Assert.NotNull(old);

        // 模拟 robocopy 重新拉取成功：目标侧重新出现，且长度等于源长度
        File.Copy(Path.Combine(src, "a.bin"), Path.Combine(dst, "a.bin"));

        var settle = RepairQuarantine.Settle(Ledger, Silent, RepairQuarantine.SettleGate.TrustedCompletion());
        Assert.Equal(1, settle.Dropped);
        Assert.Equal(0, settle.Held);
        Assert.False(File.Exists(old), "新副本已被确认可信 ⇒ 旧副本这时才允许丢弃");
        Assert.Equal(File.ReadAllText(Path.Combine(src, "a.bin")), File.ReadAllText(Path.Combine(dst, "a.bin")));
        Assert.Empty(RepairQuarantine.Load(Ledger, Silent));
    }

    // ---- ② P0-03 的核心：断网 / 停止 / 崩溃后旧数据必须还在，并且能放回去 ----

    [Fact]
    public void 源不可达且新副本未落地时旧数据仍可原样恢复()
    {
        var (obj, src, dst) = MakeTrees("a.bin", "b.bin");
        var oldA = File.ReadAllBytes(Path.Combine(dst, "a.bin"));
        var oldB = File.ReadAllBytes(Path.Combine(dst, "b.bin"));

        var purge = Purge(obj);
        Assert.Equal(2, purge.Files);

        // 源共享断网 / 用户点击停止：目标侧什么都没落地
        var settle = RepairQuarantine.Settle(Ledger, Silent, RepairQuarantine.SettleGate.TrustedCompletion());
        Assert.Equal(0, settle.Dropped);
        Assert.Equal(2, settle.Held);
        Assert.True(settle.HeldBytes > 0);

        // 恢复通道：不依赖任何内存状态，只凭账本把旧副本放回原位
        var restore = RepairQuarantine.RestoreHeld(Ledger, Silent);
        Assert.Equal(2, restore.Restored);
        Assert.Equal(0, restore.Failed);
        Assert.Equal(oldA, File.ReadAllBytes(Path.Combine(dst, "a.bin")));
        Assert.Equal(oldB, File.ReadAllBytes(Path.Combine(dst, "b.bin")));
        Assert.Empty(RepairQuarantine.Load(Ledger, Silent));
        Assert.Null(OldCopy(dst, "a.bin"));
    }

    // ---- ③ 半截落地（盘满 / 传输中途失败）不得丢弃旧副本 ----

    [Fact]
    public void 新副本长度不符时保留旧副本()
    {
        var (obj, _, dst) = MakeTrees("a.bin");
        var oldBytes = File.ReadAllBytes(Path.Combine(dst, "a.bin"));

        Purge(obj);
        var old = OldCopy(dst, "a.bin");
        Assert.NotNull(old);

        // robocopy 只写了一部分就失败（例如目标盘满）：长度不等于源长度
        File.WriteAllText(Path.Combine(dst, "a.bin"), "PART");

        var settle = RepairQuarantine.Settle(Ledger, Silent, RepairQuarantine.SettleGate.TrustedCompletion());
        Assert.Equal(0, settle.Dropped);
        Assert.Equal(1, settle.Held);
        Assert.True(File.Exists(old), "新副本长度不符 ⇒ 旧副本必须留在磁盘上");
        Assert.Equal(oldBytes, File.ReadAllBytes(old));
    }

    // ---- ④ 同名同长度但内容不同：长度判据不够时用哈希兜底 ----

    [Fact]
    public void 同名同长度但内容不同的新副本不得销毁旧副本()
    {
        var (obj, src, dst) = MakeTrees("a.bin");              // 源 "SRC-a.bin" 与旧 "OLD-a.bin" 同长度（9 字节）
        Assert.Equal(File.ReadAllBytes(Path.Combine(src, "a.bin")).Length,
                     File.ReadAllBytes(Path.Combine(dst, "a.bin")).Length);

        Purge(obj);
        // 目标侧出现同长度的**其它内容**文件（例如别的东西占了坑 / 写错了源）
        File.WriteAllText(Path.Combine(dst, "a.bin"), "XXX-a.bin");

        var settle = RepairQuarantine.Settle(Ledger, Silent, RepairQuarantine.SettleGate.TrustedCompletion());
        Assert.Equal(0, settle.Dropped);
        Assert.Equal(1, settle.Held);
        Assert.NotNull(OldCopy(dst, "a.bin"));
    }

    // ---- ⑤ 盘满：宁可本轮不修，也不让旧数据先消失 ----

    [Fact]
    public void 目标盘空间不足时本轮不隔离任何文件()
    {
        var (obj, _, dst) = MakeTrees("a.bin", "b.bin");
        var original = RepairPurge.FreeSpaceProbe;
        try
        {
            RepairPurge.FreeSpaceProbe = _ => 0;               // 目标卷可用空间 = 0

            var purge = Purge(obj);
            Assert.True(purge.BlockedNoSpace);
            Assert.Equal(0, purge.Files);
            Assert.Equal(0, purge.Bytes);

            // 一个文件都没被动过
            Assert.True(File.Exists(Path.Combine(dst, "a.bin")));
            Assert.True(File.Exists(Path.Combine(dst, "b.bin")));
            Assert.Null(OldCopy(dst, "a.bin"));
            Assert.Empty(RepairQuarantine.Load(Ledger, Silent));
        }
        finally
        {
            RepairPurge.FreeSpaceProbe = original;
        }
    }

    // ---- ⑥ 被独占占用：只计失败，不丢数据 ----

    [Fact]
    public void 被独占占用的文件隔离失败只计数不丢数据()
    {
        var (obj, _, dst) = MakeTrees("a.bin", "b.bin");
        var locked = Path.Combine(dst, "a.bin");
        using var holder = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var purge = Purge(obj);
        Assert.Equal(1, purge.Files);
        Assert.Equal(1, purge.Failed);                          // 失败被如实计数（不再只写 Debug 日志）
        Assert.True(File.Exists(locked), "被占用的旧文件仍在（未假装隔离成功）");
        Assert.NotNull(OldCopy(dst, "b.bin"));
    }

    // ---- ⑦ 账本容错：坏行不许牵连好记录 ----

    [Fact]
    public void 账本存在无法解析的行不影响其余记录()
    {
        var (obj, _, _) = MakeTrees("a.bin", "b.bin");
        Purge(obj);

        File.AppendAllText(Ledger, "{ 这不是合法 JSON" + Environment.NewLine);
        File.AppendAllText(Ledger, "" + Environment.NewLine);

        var entries = RepairQuarantine.Load(Ledger, Silent);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Contains(RepairQuarantine.Suffix, e.QuarantinePath, StringComparison.Ordinal));
    }

    // ---- 恢复绝不覆盖已经重新落地的目标文件 ----

    [Fact]
    public void 恢复时不覆盖已经重新落地的目标文件()
    {
        var (obj, _, dst) = MakeTrees("a.bin");
        Purge(obj);
        File.WriteAllText(Path.Combine(dst, "a.bin"), "NEW-CONTENT");

        var restore = RepairQuarantine.RestoreHeld(Ledger, Silent);
        Assert.Equal(0, restore.Restored);
        Assert.Equal(1, restore.SkippedExisting);
        Assert.Equal("NEW-CONTENT", File.ReadAllText(Path.Combine(dst, "a.bin")));
        Assert.Single(RepairQuarantine.Load(Ledger, Silent));   // 账本条目保留，未凭空消失
        Assert.NotNull(OldCopy(dst, "a.bin"));
    }
}