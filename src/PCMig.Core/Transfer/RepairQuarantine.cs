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
///   · 传输收尾时 <see cref="Settle"/> 判定"新副本是否可信落地"：**只有**目标侧重新出现、长度等于源长度
///     **且**能与源文件比对出相同的 SHA256 才丢弃旧副本；长度一致但拿不到内容证据（源不可达、超过校验上限、
///     校验预算不足、运行未完整成功）一律**保留**旧副本并计数，绝不提前销毁；
///     ★ v0.5.5 ★ 长度相等**不再是**任何情况下的充分证据：robocopy <c>/Z</c> 会把目标预分配成源长度，
///     中断后目标长度恰好等于源长度而内容只写了前一小段（本机已实测）。
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

    /// <summary>
    /// ★ v0.5.5 ★ 单个文件可做内容校验的字节上限。超过此上限**不再退化为"只比长度"**，
    /// 而是判定"无法确认内容" ⇒ 保留旧副本并报告「待确认」。测试可覆写。
    /// </summary>
    internal static long MaxVerifiableBytes = 2L * 1024 * 1024 * 1024;   // 2 GiB

    /// <summary>
    /// ★ v0.5.5 ★ 一次收尾里用于内容校验的总字节预算。预算耗尽后**不再退化为"只比长度"**，
    /// 而是把剩余条目全部按「待确认」保留。测试可覆写。
    /// </summary>
    internal static long HashBudgetBytes = 8L * 1024 * 1024 * 1024;      // 8 GiB

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
        DateTime QuarantinedUtc)
    {
        /// <summary>
        /// ★ v0.5.5 ★ 产生这条记录的运行标识，用于把「本次运行的隔离」与「历史账本」区分开并可追溯。
        /// 旧账本没有该字段 ⇒ 反序列化为 null（完全兼容）；null 条目同样受「必须有内容证据」的约束，
        /// 绝不会因为"本次运行成功"就被无依据清理。
        /// </summary>
        public string? RunId { get; init; }
    }

    /// <summary>
    /// 收尾结果：丢弃了多少旧副本、保留了多少（仍需人工/后续处理）、保留字节数、账本里已找不到的条目数、
    /// 以及其中因**无法确认内容**而保留（待确认）的条目数。
    /// </summary>
    public readonly record struct SettleOutcome(int Dropped, int Held, long HeldBytes, int Missing, int PendingUnverified);

    /// <summary>
    /// ★ v0.5.5 ★ 收尾闸门：只有"本次运行确实完整成功"时才允许丢弃任何旧副本。
    /// <see cref="CompletionTrustworthy"/> 为 false、或 <see cref="FailedObjects"/> / <see cref="MissingObjects"/>
    /// 非 0 时，<see cref="Settle"/> 一律**不丢弃**任何旧副本（复制中断 / 失败 / 取消 / 断链 ⇒ 全部保留）。
    /// </summary>
    public readonly record struct SettleGate(bool CompletionTrustworthy, int FailedObjects, int MissingObjects, string? RunId)
    {
        /// <summary>明确已知本次运行完整成功时的便捷构造（单元测试与单次运行场景）。</summary>
        public static SettleGate TrustedCompletion(string? runId = null) => new(true, 0, 0, runId);
    }

    /// <summary>恢复结果：改名回去了多少、因目标已存在而跳过多少、文件已不在而无法恢复多少、改名失败多少。</summary>
    public readonly record struct RestoreOutcome(int Restored, int SkippedExisting, int Missing, int Failed);

    public static string LedgerPath(string jobDir) => Path.Combine(jobDir, LedgerFileName);

    /// <summary>
    /// 判断一个文件名是否是本工具隔离出来的「旧副本」（<c>&lt;原名&gt;.pcmig-old-&lt;yyyyMMddHHmmss&gt;[-序号]</c>）。
    /// <para>
    /// ★ v0.5.4 Preview.2 ★ 存在的理由：旧副本留在**原目录**里，任何"全递归统计目标目录字节"的
    /// 代码都会把它当成传输产物重复计入。进度结算必须能识别并排除它。这里按**结构**判定
    /// （后缀 + 14 位时间戳，可选 <c>-序号</c>），不做字典匹配。
    /// </para>
    /// </summary>
    public static bool IsQuarantinedOldCopy(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        var idx = fileName.LastIndexOf(Suffix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        // 后缀必须带内容（".pcmig-old-" 之前是本来的业务文件名）
        if (idx == 0) return false;
        var tail = fileName.AsSpan(idx + Suffix.Length);
        // 时间戳：恰好 14 位数字
        if (tail.Length < 14) return false;
        for (var i = 0; i < 14; i++) { if (!char.IsAsciiDigit(tail[i])) return false; }
        // 之后必须是结束，或 "-<序号>"
        if (tail.Length == 14) return true;
        if (tail[14] != '-') return false;
        if (tail.Length == 15) return false;
        for (var i = 15; i < tail.Length; i++) { if (!char.IsAsciiDigit(tail[i])) return false; }
        return true;
    }

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

    /// <summary>
    /// 追加账本（JSONL）。★ v0.5.5 ★ 返回是否成功——调用方**必须**在写入失败时撤销本次改名，
    /// 否则会形成"文件已被移动、账本里却没有记录"的不可追踪孤儿（旧实现只记 Warning 就继续，属缺陷）。
    /// </summary>
    public static bool Append(string ledgerPath, IEnumerable<Entry> entries, ILogger log)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);
            using var fs = new FileStream(ledgerPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var sw = new StreamWriter(fs);
            foreach (var e in entries) sw.WriteLine(JsonSerializer.Serialize(e, Json));
            sw.Flush();
            fs.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "强制覆盖：旧副本账本写入失败（{Path}）——本次隔离必须回滚，否则会留下不可追踪的孤儿文件",
                ledgerPath);
            return false;
        }
    }

    /// <summary>
    /// ★ v0.5.5 ★ 隔离前的账本可写预检。不可写时**不允许开始任何改名**——
    /// 否则改名成功而记账失败会留下孤儿。返回 false 表示本轮必须放弃隔离（旧文件原样保留）。
    /// </summary>
    public static bool EnsureLedgerWritable(string ledgerPath, ILogger log)
    {
        try
        {
            var dir = Path.GetDirectoryName(ledgerPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var fs = new FileStream(ledgerPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            fs.Flush();
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "强制覆盖：旧副本账本不可写（{Path}），本轮不执行任何隔离（旧文件原样保留）", ledgerPath);
            return false;
        }
    }

    /// <summary>
    /// ★ v0.5.5 ★ 扫描目标根下「账本里没有记录」的旧副本（孤儿），用于如实报告。**只读，不删除任何文件**。
    /// 孤儿不会被自动清理：无法确认归属时一律保留，交由人工确认。
    /// </summary>
    public static List<string> FindOrphans(string root, string ledgerPath, ILogger log)
    {
        var result = new List<string>();
        try
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return result;
            var known = new HashSet<string>(
                Load(ledgerPath, log).Select(x => x.QuarantinePath),
                StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (!IsQuarantinedOldCopy(Path.GetFileName(f))) continue;
                if (known.Contains(f)) continue;
                result.Add(f);
            }
            if (result.Count > 0)
                log.Warning("强制覆盖：发现 {N} 个账本中没有记录的旧副本（孤儿），它们不会被自动删除，请人工确认后处理",
                    result.Count);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "强制覆盖：孤儿旧副本扫描失败 {Root}", root);
        }
        return result;
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
    ///   · 新副本可信（目标存在 + 长度等于源长度 + **与源文件的 SHA256 一致**）⇒ 丢弃旧副本；
    ///   · 其余情况（目标没出现 / 长度不符 / 源不可达 / 超过校验上限 / 预算不足 / 哈希不一致 / 运行未完整成功）
    ///     ⇒ **保留**旧副本，计入 Held，并在 PendingUnverified 里如实计数。
    /// <para>
    /// ★ v0.5.5 ★ 收尾闸门：<paramref name="gate"/> 表明本次运行是否完整成功。
    /// 运行失败 / 有失败对象 / 校验期确证缺失时，**一个旧副本都不丢弃**（复制中断、失败、取消、断链一律保留）。
    /// </para>
    /// </summary>
    public static SettleOutcome Settle(string ledgerPath, ILogger log, SettleGate gate)
    {
        var entries = Load(ledgerPath, log);
        if (entries.Count == 0) return new SettleOutcome(0, 0, 0, 0, 0);

        // ★ v0.5.5 ★ 运行未完整成功 ⇒ 一个都不许丢。
        if (!gate.CompletionTrustworthy || gate.FailedObjects > 0 || gate.MissingObjects > 0)
        {
            long blockedBytes = 0;
            foreach (var e in entries)
                if (SafeExists(e.QuarantinePath)) blockedBytes += SafeLength(e.QuarantinePath);
            log.Warning(
                "强制覆盖：本次运行未完整成功（trustworthy={T} failed={F} missing={M}），{N} 个旧副本全部保留（{Bytes}），账本 {Path}",
                gate.CompletionTrustworthy, gate.FailedObjects, gate.MissingObjects,
                entries.Count, Util.Format.Bytes(blockedBytes), ledgerPath);
            return new SettleOutcome(0, entries.Count, blockedBytes, 0, entries.Count);
        }

        var kept = new List<Entry>();
        var dropped = 0;
        var missing = 0;
        var pending = 0;
        long heldBytes = 0;
        var hashBudget = HashBudgetBytes;

        foreach (var e in entries)
        {
            var oldExists = SafeExists(e.QuarantinePath);
            if (ReplacementIsTrusted(e, ref hashBudget, log, out var reason))
            {
                if (!oldExists) { dropped++; continue; }
                if (TryDeleteOldCopy(e.QuarantinePath, log)) dropped++;
                else { kept.Add(e); heldBytes += SafeLength(e.QuarantinePath); }
            }
            else
            {
                pending++;
                if (oldExists) heldBytes += SafeLength(e.QuarantinePath);
                else missing++;
                kept.Add(e);
                log.Information("强制覆盖：保留旧副本（原因：{Reason}）目标 {Path}", reason, e.TargetPath);
            }
        }

        Save(ledgerPath, kept, log);
        if (kept.Count > 0)
            log.Warning("强制覆盖：{Held} 个旧副本未被可信新副本取代，已保留在目标目录（{Bytes}），账本 {Path}",
                kept.Count, Util.Format.Bytes(heldBytes), ledgerPath);
        return new SettleOutcome(dropped, kept.Count, heldBytes, missing, pending);
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

    /// <summary>
    /// ★ v0.5.5 ★「新副本是否可信到可以销毁旧副本」——**只有拿到文件级内容证据才返回 true**。
    /// 长度相等、任务完成、robocopy 退出码都不再单独构成证据；任何一项无法确认都返回 false（保留旧副本），
    /// 并通过 <paramref name="reason"/> 说明原因（写入日志，供"待确认"报告使用）。
    /// </summary>
    private static bool ReplacementIsTrusted(Entry e, ref long hashBudget, ILogger log, out string reason)
    {
        reason = "";
        try
        {
            var fi = new FileInfo(e.TargetPath);
            if (!fi.Exists) { reason = "目标侧新副本不存在"; return false; }
            // robocopy 会保留源文件时间戳，所以**不能**用"目标时间比隔离时刻新"来判定。
            // 长度一致只是必要条件，**绝不是充分条件**：/Z 预分配会让中断后的目标长度恰好等于源长度。
            if (fi.Length != e.SourceLength) { reason = $"长度不符（目标 {fi.Length} / 源 {e.SourceLength}）"; return false; }
            if (fi.Length > MaxVerifiableBytes)
            {
                reason = $"文件超过可校验上限 {Util.Format.Bytes(MaxVerifiableBytes)}，无法确认内容";
                return false;
            }
            if (hashBudget < fi.Length) { reason = "内容校验预算不足，无法确认内容"; return false; }
            if (!SafeExists(e.SourcePath)) { reason = "源文件不可达，无法确认内容"; return false; }

            var srcHash = SafeHash(e.SourcePath);
            var dstHash = SafeHash(e.TargetPath);
            hashBudget -= fi.Length;

            if (srcHash == null) { reason = "源文件哈希计算失败，无法确认内容"; return false; }
            if (dstHash == null) { reason = "目标文件哈希计算失败，无法确认内容"; return false; }
            if (!string.Equals(srcHash, dstHash, StringComparison.OrdinalIgnoreCase))
            {
                reason = "内容哈希与源不一致";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "强制覆盖：新副本可信度判定失败 {Path}（按不安全一侧处理：保留旧副本）", e.TargetPath);
            reason = "判定过程异常";
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