using System;
using System.IO;
using System.Linq;
using System.Text;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using Serilog;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ v0.5.5（2026-10-24 AUD-01 收口）★ 旧副本删除安全的**文件级**证明。
///
/// 核心不变量：
///   **任何被隔离的旧副本，只有在新副本获得足够的文件级完整性证据之后，才允许删除。**
///
/// v0.5.4 及更早的缺陷（AUD-01）：
///   · 旧实现把"目标长度 == 源长度"当作删除依据；而 robocopy <c>/Z</c> 会把目标**预分配**成源长度，
///     复制中断后目标长度恰好等于源长度、内容却只写了前一小段 ⇒ 长度判据给出错误的"可信"结论；
///   · 超过 1 MiB 的文件直接"退化为只比长度"，等于大文件永远没有内容证据；
///   · 校验预算耗尽 / 源不可达 / 哈希算不出来时同样**返回 true**（"判不了就当成可信"），
///     与安全方向完全相反；
///   · <see cref="RepairQuarantine.Settle"/> 不看本次运行是否成功，任务失败/取消后照样可能丢弃旧副本；
///   · 账本写入失败只记 Warning 就继续，形成"文件已移动、账本无记录"的不可追踪孤儿。
///
/// 本文件用**真实文件**逐条证明修复后的行为。所有数据都在临时目录内，不接触任何真实用户数据。
/// </summary>
public sealed class P0RepairQuarantineSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-qrs-" + Guid.NewGuid().ToString("N")[..8]);

    public P0RepairQuarantineSafetyTests() => Directory.CreateDirectory(_root);

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
            ObjectId = "obj-safety",
            Kind = ObjectKind.DataVolume,
            SourcePath = src,
            TargetPath = dst,
        }, src, dst);
    }

    private RepairPurge.PurgeResult Purge(PlannedObject obj, string? ledger = null, string? runId = null)
    {
        var matrix = MigrationMatrix.Load(null, Silent);
        return RepairPurge.PurgeTargetCopies(
            obj, new MigrationOptions { LargeFileThresholdMB = 512 }, matrix, Silent,
            ledger ?? Ledger, runId);
    }

    private static string? OldCopy(string dstDir, string name) =>
        Directory.GetFiles(dstDir, name + RepairQuarantine.Suffix + "*").SingleOrDefault();

    /// <summary>写一个"长度与 <paramref name="len"/> 完全相同、内容全零"的文件（模拟 /Z 预分配 + 中断）。</summary>
    private static void WriteZeros(string path, long len)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.SetLength(len);
    }

    // ══════════════════════════════════════════════════════════════════
    //  ① 大文件长度与源相等，但内容全零 ⇒ 必须保留旧副本
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void 长度相等但内容全零的新副本不得销毁旧副本()
    {
        var (obj, src, dst) = MakeTrees("big.bin");
        var srcPath = Path.Combine(src, "big.bin");
        File.WriteAllBytes(srcPath, Encoding.UTF8.GetBytes("REAL-CONTENT-0123456789"));
        var srcLen = new FileInfo(srcPath).Length;

        var oldBytes = File.ReadAllBytes(Path.Combine(dst, "big.bin"));

        var purge = Purge(obj);
        Assert.Equal(1, purge.Files);
        var old = OldCopy(dst, "big.bin");
        Assert.NotNull(old);

        // robocopy /Z 预分配 + 中断：目标长度恰好等于源长度，内容却是全零
        WriteZeros(Path.Combine(dst, "big.bin"), srcLen);
        Assert.Equal(srcLen, new FileInfo(Path.Combine(dst, "big.bin")).Length);

        var settle = RepairQuarantine.Settle(Ledger, Silent, RepairQuarantine.SettleGate.TrustedCompletion());

        Assert.Equal(0, settle.Dropped);
        Assert.Equal(1, settle.Held);
        Assert.Equal(1, settle.PendingUnverified);
        Assert.True(File.Exists(old), "长度相等但内容全零 ⇒ 旧副本必须保留");
        Assert.Equal(oldBytes, File.ReadAllBytes(old!));
    }

    // ══════════════════════════════════════════════════════════════════
    //  ② 长度相等，但只有前部内容正确 ⇒ 必须保留旧副本
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void 长度相等但只有前部内容正确的新副本不得销毁旧副本()
    {
        var (obj, src, dst) = MakeTrees("part.bin");
        var srcPath = Path.Combine(src, "part.bin");
        var payload = Encoding.UTF8.GetBytes("HEAD-CORRECT|TAIL-CORRECT|");
        File.WriteAllBytes(srcPath, payload);

        Purge(obj);
        var old = OldCopy(dst, "part.bin");
        Assert.NotNull(old);

        // 前 8 字节与源一致，其余为 0（真实的"写了开头就断了"）
        var partial = new byte[payload.Length];
        Array.Copy(payload, partial, 8);
        File.WriteAllBytes(Path.Combine(dst, "part.bin"), partial);

        var settle = RepairQuarantine.Settle(Ledger, Silent, RepairQuarantine.SettleGate.TrustedCompletion());

        Assert.Equal(0, settle.Dropped);
        Assert.Equal(1, settle.Held);
        Assert.True(File.Exists(old));
    }

    // ══════════════════════════════════════════════════════════════════
    //  ③ 完整复制成功且内容一致 ⇒ 允许丢弃（安全收紧没有变成"永不删除"）
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void 内容完全一致的新副本才允许丢弃旧副本()
    {
        var (obj, src, dst) = MakeTrees("ok.bin");
        Purge(obj);
        var old = OldCopy(dst, "ok.bin");
        Assert.NotNull(old);

        File.Copy(Path.Combine(src, "ok.bin"), Path.Combine(dst, "ok.bin"), overwrite: true);

        var settle = RepairQuarantine.Settle(Ledger, Silent, RepairQuarantine.SettleGate.TrustedCompletion());

        Assert.Equal(1, settle.Dropped);
        Assert.Equal(0, settle.Held);
        Assert.Equal(0, settle.PendingUnverified);
        Assert.False(File.Exists(old), "内容证据充分 ⇒ 这时才允许丢弃");
        Assert.Empty(RepairQuarantine.Load(Ledger, Silent));
    }

    // ══════════════════════════════════════════════════════════════════
    //  ④ 文件复制失败后进入任务收尾 ⇒ 一个都不丢弃
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void 运行未完整成功时即使内容一致也不丢弃任何旧副本()
    {
        var (obj, src, dst) = MakeTrees("a.bin", "b.bin");
        Purge(obj);
        Assert.Equal(2, RepairQuarantine.Load(Ledger, Silent).Count);

        // 内容确实一致（但本次运行整体失败：例如另一个对象断了）
        File.Copy(Path.Combine(src, "a.bin"), Path.Combine(dst, "a.bin"), overwrite: true);
        File.Copy(Path.Combine(src, "b.bin"), Path.Combine(dst, "b.bin"), overwrite: true);

        var settle = RepairQuarantine.Settle(Ledger, Silent,
            new RepairQuarantine.SettleGate(CompletionTrustworthy: false, FailedObjects: 0, MissingObjects: 0, RunId: null));

        Assert.Equal(0, settle.Dropped);
        Assert.Equal(2, settle.Held);
        Assert.Equal(2, RepairQuarantine.Load(Ledger, Silent).Count);
        Assert.NotNull(OldCopy(dst, "a.bin"));
        Assert.NotNull(OldCopy(dst, "b.bin"));
    }

    // ══════════════════════════════════════════════════════════════════
    //  ⑤ CompletedWithErrors（有失败对象）⇒ 一个都不丢弃
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void 有失败对象时即使内容一致也不丢弃任何旧副本()
    {
        var (obj, src, dst) = MakeTrees("a.bin");
        Purge(obj);
        File.Copy(Path.Combine(src, "a.bin"), Path.Combine(dst, "a.bin"), overwrite: true);

        var settle = RepairQuarantine.Settle(Ledger, Silent,
            new RepairQuarantine.SettleGate(CompletionTrustworthy: true, FailedObjects: 1, MissingObjects: 0, RunId: null));

        Assert.Equal(0, settle.Dropped);
        Assert.Equal(1, settle.Held);
        Assert.NotNull(OldCopy(dst, "a.bin"));
    }

    // ══════════════════════════════════════════════════════════════════
    //  ⑤b 校验期确证"源有目标无"（MissingObjects > 0）⇒ 一个都不丢弃
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void 校验期确证缺失时即使内容一致也不丢弃任何旧副本()
    {
        var (obj, src, dst) = MakeTrees("a.bin");
        Purge(obj);
        File.Copy(Path.Combine(src, "a.bin"), Path.Combine(dst, "a.bin"), overwrite: true);

        var settle = RepairQuarantine.Settle(Ledger, Silent,
            new RepairQuarantine.SettleGate(CompletionTrustworthy: true, FailedObjects: 0, MissingObjects: 3, RunId: null));

        Assert.Equal(0, settle.Dropped);
        Assert.Equal(1, settle.Held);
    }

    // ══════════════════════════════════════════════════════════════════
    //  ⑥ 暂停 ⇒ 阶段不是 Completed，收尾一律保留
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void 暂停收尾时保留全部旧副本()
    {
        var (obj, src, dst) = MakeTrees("a.bin");
        Purge(obj);
        File.Copy(Path.Combine(src, "a.bin"), Path.Combine(dst, "a.bin"), overwrite: true);

        // Paused 阶段 ⇒ CompletionTrustworthy = false
        var settle = RepairQuarantine.Settle(Ledger, Silent,
            new RepairQuarantine.SettleGate(CompletionTrustworthy: false, FailedObjects: 0, MissingObjects: 0, RunId: "run-x"));

        Assert.Equal(0, settle.Dropped);
        Assert.Equal(1, settle.Held);
        Assert.NotNull(OldCopy(dst, "a.bin"));
    }

    // ══════════════════════════════════════════════════════════════════
    //  ⑦ 源共享不可达 ⇒ 拿不到内容证据 ⇒ 保留
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void 源不可达时即使目标长度一致也保留旧副本()
    {
        var (obj, src, dst) = MakeTrees("a.bin");
        Purge(obj);
        var old = OldCopy(dst, "a.bin");
        Assert.NotNull(old);

        // 目标侧长度正确（拷了一份），但源文件已不可读（模拟共享断链）
        File.Copy(Path.Combine(src, "a.bin"), Path.Combine(dst, "a.bin"), overwrite: true);
        File.Delete(Path.Combine(src, "a.bin"));

        var settle = RepairQuarantine.Settle(Ledger, Silent, RepairQuarantine.SettleGate.TrustedCompletion());

        Assert.Equal(0, settle.Dropped);
        Assert.Equal(1, settle.Held);
        Assert.Equal(1, settle.PendingUnverified);
        Assert.True(File.Exists(old));
    }

    // ══════════════════════════════════════════════════════════════════
    //  ⑦b 超过可校验上限 ⇒ 不"退化为只比长度"，而是保留并报待确认
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void 超过可校验上限时保留旧副本而不是退化为长度判据()
    {
        var original = RepairQuarantine.MaxVerifiableBytes;
        try
        {
            RepairQuarantine.MaxVerifiableBytes = 4;   // 任何文件都"太大"

            var (obj, src, dst) = MakeTrees("a.bin");
            Purge(obj);
            var old = OldCopy(dst, "a.bin");
            File.Copy(Path.Combine(src, "a.bin"), Path.Combine(dst, "a.bin"), overwrite: true);

            var settle = RepairQuarantine.Settle(Ledger, Silent, RepairQuarantine.SettleGate.TrustedCompletion());

            Assert.Equal(0, settle.Dropped);
            Assert.Equal(1, settle.Held);
            Assert.Equal(1, settle.PendingUnverified);
            Assert.True(File.Exists(old));
        }
        finally
        {
            RepairQuarantine.MaxVerifiableBytes = original;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  ⑦c 校验预算耗尽 ⇒ 保留，不退化
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void 内容校验预算耗尽时保留旧副本()
    {
        var original = RepairQuarantine.HashBudgetBytes;
        try
        {
            RepairQuarantine.HashBudgetBytes = 0;

            var (obj, src, dst) = MakeTrees("a.bin");
            Purge(obj);
            File.Copy(Path.Combine(src, "a.bin"), Path.Combine(dst, "a.bin"), overwrite: true);

            var settle = RepairQuarantine.Settle(Ledger, Silent, RepairQuarantine.SettleGate.TrustedCompletion());

            Assert.Equal(0, settle.Dropped);
            Assert.Equal(1, settle.Held);
            Assert.Equal(1, settle.PendingUnverified);
        }
        finally
        {
            RepairQuarantine.HashBudgetBytes = original;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  ⑧ 隔离账本写入失败 ⇒ 完全不隔离，且不产生任何孤儿
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void 账本不可写时完全不隔离且不留下孤儿()
    {
        var (obj, _, dst) = MakeTrees("a.bin", "b.bin");

        // 让账本路径的父目录是一个**文件** ⇒ EnsureLedgerWritable 必然失败
        var blocker = Path.Combine(_root, "blocked");
        File.WriteAllText(blocker, "not a directory");
        var badLedger = Path.Combine(blocker, "quarantine.jsonl");

        var purge = Purge(obj, badLedger, "run-1");

        Assert.True(purge.BlockedLedgerUnwritable, "账本不可写 ⇒ 必须明确阻断，而不是改名后记账失败");
        Assert.Equal(0, purge.Files);
        Assert.Equal(0, purge.Failed);

        // 旧数据一个都没动：没有 .pcmig-old-* 文件，原文件仍在
        Assert.True(File.Exists(Path.Combine(dst, "a.bin")));
        Assert.True(File.Exists(Path.Combine(dst, "b.bin")));
        Assert.Null(OldCopy(dst, "a.bin"));
        Assert.Null(OldCopy(dst, "b.bin"));
    }

    // ══════════════════════════════════════════════════════════════════
    //  ⑨ 历史旧账本与本次运行混合 ⇒ 无内容证据的历史条目不得被"本次成功"清理
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void 历史账本条目没有内容证据时不得被本次运行清理()
    {
        var (obj, _, dst) = MakeTrees("a.bin");

        // 伪造一条"上一次运行"留下的账本记录：目标侧根本没重新落地
        var historical = new RepairQuarantine.Entry(
            ObjectId: "obj-safety",
            TargetPath: Path.Combine(dst, "a.bin"),
            QuarantinePath: Path.Combine(dst, "a.bin" + RepairQuarantine.Suffix + "20260101000000"),
            SourcePath: Path.Combine(_root, "src", "a.bin"),
            SourceLength: 9,
            OldLength: 9,
            QuarantinedUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))
        { RunId = "old-run" };

        Directory.CreateDirectory(Path.GetDirectoryName(Ledger)!);
        RepairQuarantine.Append(Ledger, new[] { historical }, Silent);
        File.WriteAllText(historical.QuarantinePath, "OLD-a.bin");

        // 本次运行完整成功、但目标侧没有这个文件 ⇒ 没有证据 ⇒ 必须保留
        var settle = RepairQuarantine.Settle(Ledger, Silent,
            RepairQuarantine.SettleGate.TrustedCompletion("new-run"));

        Assert.Equal(0, settle.Dropped);
        Assert.Equal(1, settle.Held);
        Assert.True(File.Exists(historical.QuarantinePath), "历史旧副本不得因为本次运行成功就消失");
    }

    // ══════════════════════════════════════════════════════════════════
    //  ⑨b 账本条目带运行标识，可追溯"本次 / 历史"
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void 账本条目写入本次运行标识()
    {
        var (obj, _, _) = MakeTrees("a.bin");
        Purge(obj, Ledger, "run-20261024");

        var entries = RepairQuarantine.Load(Ledger, Silent);
        Assert.Single(entries);
        Assert.Equal("run-20261024", entries[0].RunId);
    }

    // ══════════════════════════════════════════════════════════════════
    //  ⑨c 孤儿识别：账本里没有记录的旧副本会被报告，且不会被自动删除
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void 账本外的旧副本被识别为孤儿且不被自动删除()
    {
        var (obj, _, dst) = MakeTrees("a.bin");
        Purge(obj);

        // 手工制造一个"账本里没有记录"的旧副本（后缀必须是 14 位数字时间戳，见 IsQuarantinedOldCopy）
        var orphan = Path.Combine(dst, "ghost.bin" + RepairQuarantine.Suffix + "20260101000000");
        File.WriteAllText(orphan, "ORPHAN");

        var orphans = RepairQuarantine.FindOrphans(dst, Ledger, Silent);

        Assert.Single(orphans);
        Assert.Equal(orphan, orphans[0]);
        Assert.True(File.Exists(orphan), "孤儿只报告，绝不自动删除");
    }

    // ══════════════════════════════════════════════════════════════════
    //  ⑩ 恢复通道：目标已被占用时绝不覆盖用户文件
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void 恢复旧副本时不覆盖已存在的新文件()
    {
        var (obj, _, dst) = MakeTrees("a.bin");
        Purge(obj);
        var old = OldCopy(dst, "a.bin");
        Assert.NotNull(old);

        File.WriteAllText(Path.Combine(dst, "a.bin"), "USER-NEW-FILE");

        var restore = RepairQuarantine.RestoreHeld(Ledger, Silent);

        Assert.Equal(0, restore.Restored);
        Assert.Equal(1, restore.SkippedExisting);
        Assert.Equal("USER-NEW-FILE", File.ReadAllText(Path.Combine(dst, "a.bin")));
        Assert.True(File.Exists(old), "跳过恢复 ⇒ 旧副本仍保留");
    }
}
