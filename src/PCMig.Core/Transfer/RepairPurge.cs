using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Util;
using Serilog;

namespace PCMig.Core.Transfer;

/// <summary>
/// 「强制覆盖」修复的前置步骤：让目标侧的同名文件**消失**，逼 robocopy 重新从共享拉取。
///
/// 为什么必须让目标那份消失（实测结论，2026-09-15 实验）：
///   robocopy 对"文件名 + 大小 + 时间戳"都相同的文件一律判为"相同"并跳过，
///   而 /IS、/IT、/IS /IT 在本机 robocopy 版本上一样跳过（日志字节行"复制=0"）；
///   只有目标那份不在了，字节行才变成"复制=该文件大小"，内容才会真正被源覆盖。
///   而"内容坏了但大小/时间没变"正是验证/L2 抽样才发现的场景。
///
/// ★ P0-03 ★ 但"消失"**不等于"删除"**（2026-10-12 数据安全复核）：
///   旧实现是直接删掉目标同名文件，删除与"复制成功落地"之间存在无保障窗口——
///   源共享断网、用户停止、目标盘满、文件被占用，只要窗口内复制没落地，目标旧数据就彻底没了。
///   现在改为 <b>同目录改名隔离</b>（<see cref="RepairQuarantine"/>）：robocopy 看到的仍然是"文件不存在"，
///   触发效果与删除完全一致，但旧数据始终躺在磁盘上，可被收尾判定丢弃、也可被恢复通道改名回去。
///   本文件内**不存在任何删除调用**（由 P0RepairGateTests 的源码契约守住）。
///
/// 安全性（只处理"这次传输确实会拷回来"的文件，口径一字未改）：
///   · 排除规则（/XD /XF，含安全屏蔽名单）命中的不删（robocopy 不会拷回来）；
///   · 云占位符（Skip 策略）不删；
///   · 重解析点（junction/symlink）不删（/XJ 不拷）；
///   · 大文件（≥ 阈值）只在计划标记了可续传通道（/Z）时才处理，否则删了没人拷回来；
///   · 目标侧不存在的文件不用处理（本来就要新拷）。
///   另外新增一条**空间预检**：隔离期间旧副本不释放空间，若目标盘可用空间 < 需要隔离的总字节数，
///   本轮直接放弃隔离（<see cref="PurgeResult.BlockedNoSpace"/>），绝不为了"修一下"先把旧数据置于险地。
/// </summary>
public static class RepairPurge
{
    /// <summary>
    /// 强制覆盖的隔离结果。
    /// ★ D6.1 §16 ★ <see cref="Failed"/>：改名失败原先只写 Debug 日志 ⇒ "修复后仍不一致"变得无法解释，
    /// 这里**只加计数**，隔离决策与业务口径一字未改。
    /// ★ P0-03 ★ <see cref="BlockedNoSpace"/>：空间不足时本轮不隔离（旧数据原样保留），由调用方如实上报。
    /// </summary>
    public readonly record struct PurgeResult(long Files, long Bytes, long SkippedLarge, long Failed, bool BlockedNoSpace);

    /// <summary>用于空间预检的可用字节探测；默认按目标卷的实际可用空间，读不到时返回 <see cref="long.MaxValue"/>（不因探测失败阻断修复）。测试可覆写。</summary>
    internal static Func<string, long> FreeSpaceProbe = AvailableFreeBytes;

    internal static long AvailableFreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return long.MaxValue;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return long.MaxValue; }
    }

    /// <param name="quarantineLedgerPath">旧副本账本路径（Job 目录下的 quarantine.jsonl）；为 null 时只隔离、不记账。</param>
    public static PurgeResult PurgeTargetCopies(
        PlannedObject obj, MigrationOptions opt, MigrationMatrix matrix, ILogger log,
        string? quarantineLedgerPath = null)
    {
        var exclDirs = new HashSet<string>(matrix.ExcludedDirectoryNames, StringComparer.OrdinalIgnoreCase);
        var exclFileMatch = FilePatternMatcher.Build(
            matrix.ExcludedFileNames.Concat(matrix.SecurityBlockedFileNames));
        var skipPlaceholders = string.Equals(matrix.CloudPlaceholderPolicy, "Skip", StringComparison.OrdinalIgnoreCase);
        var thresholdMb = matrix.LargeFileThresholdMB > 0 ? matrix.LargeFileThresholdMB : opt.LargeFileThresholdMB;
        var thresholdBytes = (long)Math.Max(thresholdMb, 1) * 1024 * 1024;

        long files = 0, bytes = 0, skippedLarge = 0, failed = 0;

        // ── 1) 先枚举"本次传输确实会拷回、且目标侧已存在"的文件（与旧实现同一口径）──
        var candidates = new List<(string Rel, string Dst, string Src, long SrcLen, long OldLen)>();
        long totalOldBytes = 0;
        foreach (var (rel, size) in EnumerateSource(obj, exclDirs, exclFileMatch, skipPlaceholders))
        {
            // 大文件走 /Z 通道：该通道只在计划标记了 UseRestartablePass 时才执行
            if (size >= thresholdBytes && !obj.UseRestartablePass) { skippedLarge++; continue; }

            var dst = Path.Combine(obj.TargetPath, rel);
            long oldLen;
            try
            {
                if (!File.Exists(dst)) continue;
                oldLen = new FileInfo(dst).Length;
            }
            catch { continue; }

            candidates.Add((rel, dst, Path.Combine(obj.SourcePath, rel), size, oldLen));
            totalOldBytes += oldLen;
        }

        if (candidates.Count == 0) return new PurgeResult(0, 0, skippedLarge, 0, false);

        // ── 2) 空间预检：隔离期间旧副本**不释放**空间，必须确认目标卷放得下"旧副本 + 新副本" ──
        var free = FreeSpaceProbe(obj.TargetPath);
        if (free < totalOldBytes)
        {
            log.Warning("强制覆盖：对象 {Id} 放弃隔离 —— 目标卷可用 {Free}，安全替换需要额外保留 {Need}（旧副本在确认新副本落地前不释放空间）",
                obj.ObjectId, Format.Bytes(free), Format.Bytes(totalOldBytes));
            return new PurgeResult(0, 0, skippedLarge, 0, true);
        }

        // ── 3) 逐个**改名隔离**（不是删除）──
        var entries = new List<RepairQuarantine.Entry>();
        var now = DateTime.UtcNow;
        foreach (var c in candidates)
        {
            if (RepairQuarantine.TryQuarantine(c.Dst, c.Src, c.SrcLen, c.OldLen, obj.ObjectId, now, log, out var entry))
            {
                files++;
                bytes += c.OldLen;
                entries.Add(entry);
            }
            else
            {
                // 改不掉（被占用/权限等）不致命：该文件仍由后续 robocopy 按大小/时间差异决定是否重拷，
                // 且**旧数据原样留在原地**——但必须计数（否则"隔离失败"在诊断里完全不可见）。
                failed++;
            }
        }

        if (entries.Count > 0 && quarantineLedgerPath != null)
            RepairQuarantine.Append(quarantineLedgerPath, entries, log);

        return new PurgeResult(files, bytes, skippedLarge, failed, false);
    }

    /// <summary>枚举"本次传输会拷贝"的源文件（相对路径 + 字节数），与 robocopy 的排除口径一致。</summary>
    private static IEnumerable<(string Rel, long Size)> EnumerateSource(
        PlannedObject obj, HashSet<string> exclDirs, Func<string, bool> exclFileMatch, bool skipPlaceholders)
    {
        var root = obj.SourcePath;

        // 文件清单对象（只拷清单内文件）
        if (obj.FileList != null)
        {
            foreach (var name in obj.FileList)
            {
                if (exclFileMatch(name)) continue;
                var p = Path.Combine(root, name);
                long len;
                try
                {
                    if (!File.Exists(p)) continue;
                    var fi = new FileInfo(p);
                    if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    len = fi.Length;
                }
                catch { continue; }
                yield return (name, len);
            }
            yield break;
        }

        if (!Directory.Exists(root)) yield break;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos(); }
            catch { continue; }

            foreach (var e in entries)
            {
                // 注意：C# 不允许在带 catch 的 try 里 yield，故先在 try 内取值、出了 try 再返回
                string? rel = null; long len = 0;
                try
                {
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (e is DirectoryInfo di)
                    {
                        if (exclDirs.Contains(di.Name)) continue;
                        pending.Push(di.FullName);
                        continue;
                    }
                    if (e is not FileInfo fi) continue;
                    if (exclFileMatch(fi.Name)) continue;
                    if (skipPlaceholders && ((fi.Attributes & DirStat.CloudPlaceholderAttr) == DirStat.CloudPlaceholderAttr
                        || (fi.Attributes & FileAttributes.Offline) == FileAttributes.Offline)) continue;
                    // RootFiles 对象只处理根层文件
                    // ★ P0-01 ★ 与 Verifier 共用同一套根归属比较（PathNorm）。
                    //   旧写法对"源根带尾随分隔符"的场景会把根层文件全部漏掉（且与验证侧结论不一致）。
                    if (!PathNorm.BelongsToObjectRoot(obj, root, fi.FullName))
                        continue;
                    rel = Path.GetRelativePath(root, fi.FullName);
                    len = fi.Length;
                }
                catch { /* 单文件失败跳过 */ }
                if (rel != null) yield return (rel, len);
            }
        }
    }
}