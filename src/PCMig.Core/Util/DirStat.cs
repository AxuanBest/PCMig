namespace PCMig.Core.Util;

/// <summary>
/// 目录统计：与 robocopy /E 的语义对齐 —— 包含隐藏/系统文件，跳过重解析点（junction/symlink）防环。
/// 同时检测云占位符（OneDrive 等, FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000）。
/// </summary>
public static class DirStat
{
    public const FileAttributes CloudPlaceholderAttr = (FileAttributes)0x00400000;

    public sealed class Stat
    {
        public long Bytes;
        public long Files;
        public long Dirs;
        public bool HasLargeFiles;
        public bool HasCloudPlaceholders;
        public bool Incomplete;             // 有目录/文件无权访问
        public long LargeFileThresholdBytes;
    }

    private static EnumerationOptions Options => new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        MatchCasing = MatchCasing.PlatformDefault
    };

    public static Stat Measure(string root, long largeFileThresholdBytes,
        ISet<string>? excludeDirs = null, ISet<string>? excludeFiles = null,
        Action<string>? onInaccessible = null)
    {
        var stat = new Stat { LargeFileThresholdBytes = largeFileThresholdBytes };
        if (!Directory.Exists(root)) { stat.Incomplete = true; return stat; }

        // 迭代遍历（显式栈）：目录深度无递归栈限制，任意深目录结构不会爆栈
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos(); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                stat.Incomplete = true; onInaccessible?.Invoke(dir); continue;
            }

            foreach (var e in entries)
            {
                try
                {
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue; // 与 EnumerationOptions 双保险
                    if (e is FileInfo fi)
                    {
                        if (excludeFiles != null && excludeFiles.Contains(fi.Name)) continue;
                        stat.Files++;
                        stat.Bytes += fi.Length;
                        if (fi.Length >= largeFileThresholdBytes) stat.HasLargeFiles = true;
                        if ((fi.Attributes & CloudPlaceholderAttr) == CloudPlaceholderAttr) stat.HasCloudPlaceholders = true;
                    }
                    else if (e is DirectoryInfo di)
                    {
                        if (excludeDirs != null && excludeDirs.Contains(di.Name)) continue;
                        stat.Dirs++;
                        pending.Push(di.FullName);
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    stat.Incomplete = true; onInaccessible?.Invoke(e.FullName);
                }
            }
        }

        return stat;
    }

    /// <summary>枚举第一级子目录（迁移对象划分依据）。root 本身的散落文件由调用方另行处理。</summary>
    public static List<string> TopLevelDirs(string root, ISet<string>? excludeDirs = null)
    {
        var list = new List<string>();
        if (!Directory.Exists(root)) return list;
        foreach (var d in Directory.EnumerateDirectories(root))
        {
            try
            {
                var attr = File.GetAttributes(d);
                if ((attr & FileAttributes.ReparsePoint) != 0) continue; // 不把 junction 当对象
                var name = Path.GetFileName(d.TrimEnd(Path.DirectorySeparatorChar));
                if (excludeDirs != null && name != null && excludeDirs.Contains(name)) continue;
                list.Add(d);
            }
            catch { /* skip */ }
        }
        return list.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool RootHasLooseFiles(string root)
    {
        try { return Directory.EnumerateFiles(root).Any(); }
        catch { return false; }
    }
}
