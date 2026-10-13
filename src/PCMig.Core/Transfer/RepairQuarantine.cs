using System.Security.Cryptography;
using System.Text.Json;
using Serilog;

namespace PCMig.Core.Transfer;

/// <summary>
/// ★ P0-03 ★ 「强制覆盖」的**非破坏性替换**账本与收尾。
///
/// 背景（现场证据）：强制覆盖修复原先走 <c>File.Delete(目标同名文件)</c> → 再让 robocopy 从共享重新拉取。
/// 删除与"重新拉取成功"之间存在一个**没有任何保障的窗口**：源共享断网、用户停止、目标盘满、文件被占用，
/// 只要窗口内复制没落地，目标那份旧数据就**凭空消失**了，而这是用户唯一的数据副本之一。
///
/// 现在的做法（只改"怎么让 robocopy 重拷"，不改任何业务判定）：
///   · 目标侧同名文件**改名为同目录下的 <c>&lt;原名&gt;.pcmig-old-&lt;时间戳&gt;</c>**（同卷 rename，原子且不消耗空间）；
///     robocopy 看不到原文件 ⇒ 与旧的"先删"完全等价的触发效果；
///   · 改名结果逐条写进 Job 目录的 <c>quarantine.jsonl</c>（旧路径/新路径/源路径/源长度/旧长度/时刻）；
///   · 传输收尾时 <see cref="Settle"/> 判定"新副本是否可信落地"：**只有**目标侧重新出现且长度等于源长度
///     （小文件再按内容哈希复核）才丢弃旧副本；否则**保留**旧副本并计数，绝不提前销毁；
///   · 任何时候都能用 <see cref="RestoreHeld"/> 把仍未消除的旧副本改名回原路径（断网/停止/崩溃后的恢复通道）。
///
/// 判据方向始终是"更安全的一侧"：判定不了就保留、保留不了就恢复、恢复不了就如实报错。
/// </summary>
public static class RepairQuarantine
{
    /// <summary>旧副本文件名后缀（追加在原名之后，因此不会与任何真实业务文件名冲突）。</summary>
    public const string Suffix = ".pcmig-old-";

    /// <summary>账本文件名（位于 Job 目录下）。</summary>
    public const string LedgerFileName = "quarantine.jsonl";

    /// <summary>内容哈希复核的字节上限：只对"重新落地的那个文件"小于此值时额外比对内容（成本有界）。</summary>
    private const long MaxHashBytes = 1024 * 1024;

    /// <summary>一次收尾里用于内容复核的总字节预算（超过就退化为"只比长度"）。</summary>
    private const long HashBudgetBytes = 32L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>一条"旧副本被改名到哪去了"的记录。足够**离线**恢复，不依赖任何内存状态。</summary>
    public sealed record Entry(
        string ObjectId,
        string TargetPath,
        string QuarantinePath,
        string SourcePath,
        long SourceLength,
        long OldLength,
        DateTime QuarantinedUtc);

    /// <summary>收尾结果：丢弃了多少旧副本、保留了多少（仍需人工/后续处理）、保留字节数、账本里已找不到的条目数。</summary>
    public readonly record struct SettleOutcome(int Dropped, int Held, long HeldBytes, int Missing);

    /// <summary>恢复结果：改名回去了多少、因目标已存在而跳过多少、文件已不在而无法恢复多少、改名失败多少。</summary>
    public readonly record struct RestoreOutcome(int Restored, int SkippedExisting, int Missing, int Failed);

    public static string LedgerPath(string jobDir) => Path.Combine(jobDir, LedgerFileName);

    /// <summary>
    /// 把目标侧同名文件改名成"旧副本"。
    /// 失败（被独占占用、权限不足、同卷限制等）一律返回 false —— **调用方必须把它计为失败**，绝不能当成删除成功。
    /// </summary>
    public static bool TryQuarantine(string dst, string src, long sourceLength, long oldLength,
        string objectId, DateTime utcNow, ILogger log, out Entry entry)
    {
        entry = new Entry(objectId, dst, string.Empty, src, sourceLength, oldLength, utcNow);
        try
        {
            var quarantine = BuildQuarantinePath(dst, utcNow);
            File.Move(dst, quarantine);
            entry = entry with { QuarantinePath = quarantine };
            return true;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "强制覆盖：隔离（改名）目标侧文件失败 {Path}", dst);
            return false;
        }
    }

    /// <summary>同目录下唯一的旧副本路径（时间戳 + 必要时序号，绝不覆盖已存在的旧副本）。</summary>
    public static string BuildQuarantinePath(string dst, DateTime utcNow)
    {
        var stamp = utcNow.ToString("yyyyMMddHHmmss");
        var candidate = dst + Suffix + stamp;
        for (var i = 1; i <= 999 && File.Exists(candidate); i++) candidate = dst + Suffix + stamp + "-" + i;
        return candidate;
    }

    /// <summary>追加账本（JSONL）。账本写不进去**不影响**修复本身，但会记一条 Warning：证据缺失必须可见。</summary>
    public static void Append(string ledgerPath, IEnumerable<Entry> entries, ILogger log)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);
            using var fs = new FileStream(ledgerPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var sw = new StreamWriter(fs);
            foreach (var e in entries) sw.WriteLine(JsonSerializer.Serialize(e, Json));
            sw.Flush();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "强制覆盖：旧副本账本写入失败（{Path}）——本次隔离的旧副本仍然在磁盘上，但缺少账本记录",
                ledgerPath);
        }
    }

    /// <summary>读取账本；单行损坏只跳过该行（历史/半截写入不能让整份证据失效）。</summary>
    public static List<Entry> Load(string ledgerPath, ILogger? log = null)
    {
        var list = new List<Entry>();
        try
        {
            if (!File.Exists(ledgerPath)) return list;
            foreach (var line in File.ReadLines(ledgerPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var e = JsonSerializer.Deserialize<Entry>(line, Json);
                    if (e != null && !string.IsNullOrEmpty(e.TargetPath)) list.Add(e);
                }
                catch (Exception ex)
                {
                    log?.Warning(ex, "强制覆盖：旧副本账本存在无法解析的行，已跳过");
                }
            }
        }
        catch (Exception ex)
        {
            log?.Warning(ex, "强制覆盖：旧副本账本读取失败 {Path}", ledgerPath);
        }
        return list;
    }

    /// <summary>
    /// 传输收尾：判定每个旧副本是否已被"可信的新副本"取代。
    ///   · 新副本可信（目标存在 + 长度等于源长度 + 小文件内容哈希一致）⇒ 丢弃旧副本；
    ///   · 其余情况（目标没出现 / 长度不符 / 哈希不一致 / 判定不了）⇒ **保留**旧副本，计入 Held。
    /// </summary>
    public static SettleOutcome Settle(string ledgerPath, ILogger log)
    {
        var entries = Load(ledgerPath, log);
        if (entries.Count == 0) return new SettleOutcome(0, 0, 0, 0);

        var kept = new List<Entry>();
        var dropped = 0;
        var missing = 0;
        long heldBytes = 0;
        var hashBudget = HashBudgetBytes;

        foreach (var e in entries)
        {
            var oldExists = SafeExists(e.QuarantinePath);
            if (ReplacementIsTrusted(e, ref hashBudget, log))
            {
                if (!oldExists) { dropped++; continue; }
                if (TryDeleteOldCopy(e.QuarantinePath, log)) dropped++;
                else { kept.Add(e); heldBytes += SafeLength(e.QuarantinePath); }
            }
            else
            {
                if (oldExists) heldBytes += SafeLength(e.QuarantinePath);
                else missing++;
                kept.Add(e);
            }
        }

        Save(ledgerPath, kept, log);
        if (kept.Count > 0)
            log.Warning("强制覆盖：{Held} 个旧副本未被可信新副本取代，已保留在目标目录（{Bytes}），账本 {Path}",
                kept.Count, Util.Format.Bytes(heldBytes), ledgerPath);
        return new SettleOutcome(dropped, kept.Count, heldBytes, missing);
    }

    /// <summary>
    /// 恢复通道：把仍然保留的旧副本改名回原路径。
    /// 目标已存在时**不覆盖**（跳过并保留账本条目）——恢复动作绝不能制造新的覆盖风险。
    /// </summary>
    public static RestoreOutcome RestoreHeld(string ledgerPath, ILogger log)
    {
        var entries = Load(ledgerPath, log);
        var kept = new List<Entry>();
        int restored = 0, skippedExisting = 0, missing = 0, failed = 0;

        foreach (var e in entries)
        {
            if (!SafeExists(e.QuarantinePath)) { missing++; continue; }
            if (SafeExists(e.TargetPath)) { skippedExisting++; kept.Add(e); continue; }
            try
            {
                File.Move(e.QuarantinePath, e.TargetPath);
                restored++;
                log.Information("强制覆盖：旧副本已恢复回原路径 {Path}", e.TargetPath);
            }
            catch (Exception ex)
            {
                failed++;
                kept.Add(e);
                log.Error(ex, "强制覆盖：旧副本恢复失败 {Path}", e.QuarantinePath);
            }
        }

        Save(ledgerPath, kept, log);
        return new RestoreOutcome(restored, skippedExisting, missing, failed);
    }

    /// <summary>"新副本是否可信到可以销毁旧副本"——判不了就返回 false（保留旧副本）。</summary>
    private static bool ReplacementIsTrusted(Entry e, ref long hashBudget, ILogger log)
    {
        try
        {
            var fi = new FileInfo(e.TargetPath);
            if (!fi.Exists) return false;
            // robocopy 会保留源文件时间戳，所以**不能**用"目标时间比隔离时刻新"来判定；
            // 长度与源一致是"从源拷回来且完整"的第一手证据，长度不符（半截/盘满）一律保留旧副本。
            if (fi.Length != e.SourceLength) return false;
            if (fi.Length > MaxHashBytes || hashBudget < fi.Length) return true;   // 大文件退化为长度判据
            if (!SafeExists(e.SourcePath)) return true;                            // 源已不可达：长度判据即止
            var srcHash = SafeHash(e.SourcePath);
            var dstHash = SafeHash(e.TargetPath);
            hashBudget -= fi.Length;
            if (srcHash == null || dstHash == null) return true;                   // 读不到就退化为长度判据
            return string.Equals(srcHash, dstHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "强制覆盖：新副本可信度判定失败 {Path}（按不安全一侧处理：保留旧副本）", e.TargetPath);
            return false;
        }
    }

    private static string? SafeHash(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(fs));
        }
        catch { return null; }
    }

    private static bool SafeExists(string path)
    {
        try { return !string.IsNullOrEmpty(path) && File.Exists(path); } catch { return false; }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static bool TryDeleteOldCopy(string path, ILogger log)
    {
        try { File.Delete(path); return true; }
        catch (Exception ex) { log.Warning(ex, "强制覆盖：丢弃旧副本失败，将保留并稍后重试 {Path}", path); return false; }
    }

    private static void Save(string ledgerPath, List<Entry> entries, ILogger log)
    {
        try
        {
            var tmp = ledgerPath + ".tmp";
            File.WriteAllLines(tmp, entries.Select(x => JsonSerializer.Serialize(x, Json)));
            File.Move(tmp, ledgerPath, overwrite: true);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "强制覆盖：旧副本账本回写失败 {Path}", ledgerPath);
        }
    }
}