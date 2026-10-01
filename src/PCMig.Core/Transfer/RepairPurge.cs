using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Util;
using Serilog;

namespace PCMig.Core.Transfer;

/// <summary>
/// 「强制覆盖」修复的前置步骤：把目标侧与源同名的文件先删掉，逼 robocopy 重新从共享拉取。
///
/// 为什么必须删（实测结论，2026-09-15 实验）：
///   robocopy 对"文件名 + 大小 + 时间戳"都相同的文件一律判为"相同"并跳过，
///   而 /IS、/IT、/IS /IT 在本机 robocopy 版本上一样跳过（日志字节行"复制=0"）；
///   只有先删掉目标那份，字节行才变成"复制=该文件大小"，内容才会真正被源覆盖。
///   而"内容坏了但大小/时间没变"正是验证/L2 抽样才发现的场景。
///
/// 安全性：只删"这次传输确实会拷贝的文件"——
///   · 排除规则（/XD /XF，含安全屏蔽名单）命中的不删（robocopy 不会拷回来）；
///   · 云占位符（Skip 策略）不删；
///   · 重解析点（junction/symlink）不删（/XJ 不拷）；
///   · 大文件（≥ 阈值）只在计划标记了可续传通道（/Z）时才删，否则删了没人拷回来；
///   · 目标侧不存在的文件不删（本来就要新拷）。
/// </summary>
public static class RepairPurge
{
    /// <summary>
    /// 强制覆盖的删除结果。
    /// ★ D6.1 §16 ★ 新增 <see cref="Failed"/>：删除失败原先只写 Debug 日志 ⇒
    /// "修复后仍不一致"变得无法解释。这里**只加计数**，删除决策与业务口径一字未改。
    /// </summary>
    public readonly record struct PurgeResult(long Files, long Bytes, long SkippedLarge, long Failed);

    public static PurgeResult PurgeTargetCopies(
        PlannedObject obj, MigrationOptions opt, MigrationMatrix matrix, ILogger log)
    {
        var exclDirs = new HashSet<string>(matrix.ExcludedDirectoryNames, StringComparer.OrdinalIgnoreCase);
        var exclFileMatch = FilePatternMatcher.Build(
            matrix.ExcludedFileNames.Concat(matrix.SecurityBlockedFileNames));
        var skipPlaceholders = string.Equals(matrix.CloudPlaceholderPolicy, "Skip", StringComparison.OrdinalIgnoreCase);
        var thresholdMb = matrix.LargeFileThresholdMB > 0 ? matrix.LargeFileThresholdMB : opt.LargeFileThresholdMB;
        var thresholdBytes = (long)Math.Max(thresholdMb, 1) * 1024 * 1024;

        long files = 0, bytes = 0, skippedLarge = 0, failed = 0;
        foreach (var (rel, size) in EnumerateSource(obj, exclDirs, exclFileMatch, skipPlaceholders))
        {
            // 大文件走 /Z 通道：该通道只在计划标记了 UseRestartablePass 时才执行
            if (size >= thresholdBytes && !obj.UseRestartablePass) { skippedLarge++; continue; }

            var dst = Path.Combine(obj.TargetPath, rel);
            try
            {
                if (!File.Exists(dst)) continue;
                var len = new FileInfo(dst).Length;
                File.Delete(dst);
                files++; bytes += len;
            }
            catch (Exception ex)
            {
                // 删不掉（被占用等）不致命：该文件仍由后续 robocopy 按大小/时间差异决定是否重拷
                // ★ 但必须**计数**（否则"删失败"在诊断里完全不可见）。
                failed++;
                log.Debug(ex, "强制覆盖：删除目标侧文件失败 {Path}", dst);
            }
        }
        return new PurgeResult(files, bytes, skippedLarge, failed);
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
                    if (obj.Kind == ObjectKind.RootFiles &&
                        !string.Equals(Path.GetDirectoryName(fi.FullName), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
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
