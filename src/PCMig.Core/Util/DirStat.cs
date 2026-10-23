using PCMig.Core.Transfer;

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
        public long LockRiskFiles;          // .pst/.ost 等打开即锁的高危文件（Outlook 数据文件）
        /// <summary>★ P1-08 ★ 上面那些候选里，**实测**当前打不开（Win32 32/33 独占）的个数。
        /// 旧实现只有"按扩展名的候选数"，于是"归档 PST 也从没被 Outlook 打开过"也被说成"会被锁定导致迁移失败"。</summary>
        public long LockedRiskFiles;
        public long EncryptedFiles;         // EFS 加密文件（复制到新机后以明文存储，原加密保护丢失）
    }

    private static EnumerationOptions Options => new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        MatchCasing = MatchCasing.PlatformDefault
    };

    /// <summary>★ P1-08 ★ 扫描时最多**实测**这么多个锁风险候选（PST 通常 1~3 个；上限防"目录里几千个 .pst"拖慢扫描）。</summary>
    public const int MaxLockProbes = 20;

    /// <summary>
    /// .pst/.ost ⇒ Outlook 数据文件**候选**。
    /// ★ 扩展名只是候选判据 ★ 是否真的会被锁必须由 <see cref="LockProbe"/> 实测（归档 PST 从不被 Outlook 打开）。
    /// </summary>
    public static bool IsOutlookDataFile(string name)
        => name.EndsWith(".pst", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".ost", StringComparison.OrdinalIgnoreCase);

    /// <summary>锁风险记账：候选计数 +（可选）实测是否真被独占。</summary>
    private static void AccountLockRisk(Stat stat, FileInfo fi, bool probeLockRisk)
    {
        if (!IsOutlookDataFile(fi.Name)) return;
        stat.LockRiskFiles++;
        if (!probeLockRisk || stat.LockRiskFiles > MaxLockProbes) return;
        if (LockProbe.Probe(fi.FullName).IsLocked) stat.LockedRiskFiles++;
    }

    /// <param name="excludeFiles">支持 * ? 通配符（与 robocopy /XF 同语义）。</param>
    /// <param name="skipPlaceholders">true = 云占位符不计入统计（矩阵 cloudPlaceholderPolicy: Skip 时与 /XA:O 同口径）。</param>
    /// <param name="onInaccessible">不可访问的目录/文件回调：参数 =（路径, 原因）。v0.3.8 起带原因，
    /// 便于"扫描残缺"闸门把"哪个路径、为什么"如实报给用户（旧实现只置 Incomplete 标志）。</param>
    /// <param name="probeLockRisk">★ P1-08 ★ true = 对 .pst/.ost 候选**实测**是否真被独占（最多 <see cref="MaxLockProbes"/> 个）。
    /// 关掉它就只剩"按扩展名的候选数"，不得据此断言"会被锁定"。</param>
    public static Stat Measure(string root, long largeFileThresholdBytes,
        ISet<string>? excludeDirs = null, IEnumerable<string>? excludeFiles = null,
        Action<string, string>? onInaccessible = null, bool skipPlaceholders = false,
        bool probeLockRisk = true,
        bool skipQuarantineOldCopies = false)
    {
        var stat = new Stat { LargeFileThresholdBytes = largeFileThresholdBytes };
        if (!Directory.Exists(root)) { stat.Incomplete = true; return stat; }
        var fileMatch = excludeFiles == null ? (Func<string, bool>?)null : FilePatternMatcher.Build(excludeFiles);

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
                stat.Incomplete = true; onInaccessible?.Invoke(dir, Reason(ex)); continue;
            }

            foreach (var e in entries)
            {
                try
                {
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue; // 与 EnumerationOptions 双保险
                    if (e is FileInfo fi)
                    {
                        if (fileMatch != null && fileMatch(fi.Name)) continue;
                        // ★ v0.5.4 Preview.2 进度真值修复（2026-10-15）★
                        //   「强制覆盖」会把目标侧同名旧文件**改名为 <原名>.pcmig-old-<时间戳>**
                        //   留在**同一个目录**里（见 RepairQuarantine）。它不是本轮传输的产物，
                        //   却会被全递归统计当成"已落盘字节"计入对象结算量。
                        //   真机证据（JOB-20261014-143502-b5dc / object-000004）：
                        //     计划 66.089 GB 的对象，结算实测 111.536 GB；
                        //     同一 robocopy 末块的「其他」列恰为 55.777 GB（= 被隔离的旧副本），
                        //     而 55.758 GB(新副本) + 55.777 GB(旧副本) = 111.535 GB ≈ 实测值。
                        //   ⇒ 旧副本被重复计入一次，使 TargetBytes 虚高约一倍。
                        //   默认 false：其它调用方（扫描/预检/验证）口径不变，只有进度结算显式打开。
                        if (skipQuarantineOldCopies && RepairQuarantine.IsQuarantinedOldCopy(fi.Name)) continue;
                        var attr = fi.Attributes;
                        // 云占位符：RECALL_ON_DATA_ACCESS 或 OFFLINE（OneDrive Files-On-Demand 两者兼有）
                        var isPlaceholder = (attr & CloudPlaceholderAttr) == CloudPlaceholderAttr
                            || (attr & FileAttributes.Offline) == FileAttributes.Offline;
                        if (isPlaceholder)
                        {
                            stat.HasCloudPlaceholders = true;
                            if (skipPlaceholders) continue;   // Skip 策略：不计入（robocopy /XA:O 同口径）
                        }
                        stat.Files++;
                        stat.Bytes += fi.Length;
                        if (fi.Length >= largeFileThresholdBytes) stat.HasLargeFiles = true;
                        if ((attr & FileAttributes.Encrypted) == FileAttributes.Encrypted) stat.EncryptedFiles++;
                        // ★ P1-08 ★ 候选计数 +（可选）实测独占：扩展名 ≠ 被占用
                        AccountLockRisk(stat, fi, probeLockRisk);
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
                    stat.Incomplete = true; onInaccessible?.Invoke(e.FullName, Reason(ex));
                }
            }
        }

        return stat;
    }

    /// <summary>
    /// 只统计根目录下的一级文件（不递归），与 robocopy `*.* /LEV:1` 同口径。
    /// 存在的理由：源根散落文件曾经硬编码 Bytes=-1（"通常很小"的假设），
    /// 实测踩中——用户 D 盘根目录有 14.2GB 的 素材.zip，导致计划总量漏计、
    /// 进度显示成 "32.8 GB/26.8 GB、100%"的假象。
    /// </summary>
    public static Stat MeasureLooseFiles(string root, long largeFileThresholdBytes,
        IEnumerable<string>? excludeFiles = null, bool skipPlaceholders = false,
        bool probeLockRisk = true)
    {
        var stat = new Stat { LargeFileThresholdBytes = largeFileThresholdBytes };
        if (!Directory.Exists(root)) { stat.Incomplete = true; return stat; }
        var fileMatch = excludeFiles == null ? (Func<string, bool>?)null : FilePatternMatcher.Build(excludeFiles);
        IEnumerable<FileInfo> files;
        try { files = new DirectoryInfo(root).EnumerateFiles(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        { stat.Incomplete = true; return stat; }

        foreach (var fi in files)
        {
            try
            {
                if (fileMatch != null && fileMatch(fi.Name)) continue;
                var attr = fi.Attributes;
                var isPlaceholder = (attr & CloudPlaceholderAttr) == CloudPlaceholderAttr
                    || (attr & FileAttributes.Offline) == FileAttributes.Offline;
                if (isPlaceholder)
                {
                    stat.HasCloudPlaceholders = true;
                    if (skipPlaceholders) continue;
                }
                stat.Files++;
                stat.Bytes += fi.Length;
                if (fi.Length >= largeFileThresholdBytes) stat.HasLargeFiles = true;
                if ((attr & FileAttributes.Encrypted) == FileAttributes.Encrypted) stat.EncryptedFiles++;
                // ★ P1-08 ★ 候选计数 +（可选）实测独占：扩展名 ≠ 被占用
                AccountLockRisk(stat, fi, probeLockRisk);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            { stat.Incomplete = true; }
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

    /// <summary>不可访问原因的一句话说明（给用户看，不是异常堆栈）。</summary>
    public static string Reason(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "访问被拒绝（权限不足）",
        System.Security.SecurityException => "安全策略禁止访问",
        IOException => $"I/O 错误：{ex.Message}",
        _ => ex.Message
    };

    public static bool RootHasLooseFiles(string root)
    {
        try { return Directory.EnumerateFiles(root).Any(); }
        catch { return false; }
    }
}
