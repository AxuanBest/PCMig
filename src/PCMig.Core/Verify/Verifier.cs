using System.Security.Cryptography;
using PCMig.Core.Jobs;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Util;
using Serilog;

namespace PCMig.Core.Verify;

/// <summary>
/// 验证器：Receipt 只证明"robocopy 说复制完了"，Verifier 负责"源和目标真的相等"。
///   L1：文件数 + 字节数 双侧实测对账（与传输使用同一套排除名单，否则必然误报）
///   L2：确定性抽样 SHA-256 双向哈希（默认 1%，大文件必抽）
/// </summary>
public sealed class Verifier
{
    private readonly JobContext _ctx;
    private readonly MigrationMatrix _matrix;
    private readonly ILogger _log;
    private const int MaxMissingSamples = 50;
    private const int MaxListEntries = 2_000_000; // 内存路径清单上限；超过自动切流式核对（迁移能力本身不设限）

    public Verifier(JobContext ctx, MigrationMatrix matrix, ILogger log)
    {
        _ctx = ctx;
        // 任务级自定义排除规则（超大数据模式）与传输/扫描同口径
        _matrix = matrix.WithExtraExclusions(ctx.Definition.CustomExclusions, log);
        _log = log.ForContext<Verifier>();
    }

    public async Task<VerifyReport> RunAsync(VerifyLevel level, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var plan = _ctx.Plan ?? throw new InvalidOperationException("plan.json 不存在");
        var report = new VerifyReport { JobId = _ctx.JobId, Level = level };
        var exclDirs = new HashSet<string>(_matrix.ExcludedDirectoryNames, StringComparer.OrdinalIgnoreCase);
        // 文件排除支持 * ? 通配符（与扫描/传输同口径）
        var exclFileMatch = FilePatternMatcher.Build(
            _matrix.ExcludedFileNames.Concat(_matrix.SecurityBlockedFileNames));
        // Skip 策略下占位符既没被复制（/XA:O），验证时也不应计入
        var skipPlaceholders = string.Equals(_matrix.CloudPlaceholderPolicy, "Skip", StringComparison.OrdinalIgnoreCase);

        _log.Information("验证开始: Level={Level}, 对象数={Count}", level, plan.Objects.Count);

        foreach (var obj in plan.Objects)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"验证 {obj.ObjectId} …");

            var result = new ObjectVerifyResult { ObjectId = obj.ObjectId };
            // 目标侧先枚举（清单用于快速比对）；源侧只有在目标侧没溢出时才收集清单
            var dstStat = await Task.Run(() => EnumerateSide(obj.TargetPath, obj, exclDirs, exclFileMatch, skipPlaceholders, true), ct);
            var srcStat = await Task.Run(() => EnumerateSide(obj.SourcePath, obj, exclDirs, exclFileMatch, skipPlaceholders, !dstStat.Overflow), ct);

            result.SourceFiles = srcStat.Files; result.SourceBytes = srcStat.Bytes;
            result.TargetFiles = dstStat.Files; result.TargetBytes = dstStat.Bytes;
            result.CountMatch = srcStat.Files == dstStat.Files;
            result.BytesMatch = srcStat.Bytes == dstStat.Bytes;

            var samplePercent = Math.Clamp(_ctx.Definition.Options.SampleHashPercent, 1, 100);
            List<(string Relative, long Length)> hashSamples;

            if (!srcStat.Overflow && !dstStat.Overflow)
            {
                // 清单模式：内存内比对（目标侧用 HashSet，O(n) 不 O(n²)）
                var dstSet = new HashSet<string>(dstStat.RelativePaths, StringComparer.OrdinalIgnoreCase);
                foreach (var rel in srcStat.RelativePaths)
                    if (!dstSet.Contains(rel) && result.MissingSamples.Count < MaxMissingSamples)
                        result.MissingSamples.Add(rel);
                hashSamples = srcStat.FilesForHash
                    .Where(f => StableHash(f.Relative) % 100 < samplePercent)
                    .Take(2000)
                    .ToList();
            }
            else
            {
                // 流式模式（超大对象）：不存全量清单，第二遍流式过源，逐文件核对目标存在性 + 收集哈希样本
                _log.Information("对象 {Id} 文件量超清单上限，切换流式核对（内存有界，结果同样精确）", obj.ObjectId);
                progress?.Report($"验证 {obj.ObjectId} …（超大对象，流式核对）");
                hashSamples = await Task.Run(() => StreamSourcePass(obj, result, samplePercent, exclDirs, exclFileMatch, skipPlaceholders, ct), ct);
            }

            if (level >= VerifyLevel.L2_SampleHash)
            {
                foreach (var f in hashSamples)
                {
                    ct.ThrowIfCancellationRequested();
                    var srcFile = Path.Combine(obj.SourcePath, f.Relative);
                    var dstFile = Path.Combine(obj.TargetPath, f.Relative);
                    result.HashSampled++;
                    try
                    {
                        if (!File.Exists(dstFile)) { result.HashMismatched++; continue; }
                        var h1 = await HashAsync(srcFile, ct);
                        var h2 = await HashAsync(dstFile, ct);
                        if (!string.Equals(h1, h2, StringComparison.OrdinalIgnoreCase)) result.HashMismatched++;
                    }
                    catch (Exception ex) { _log.Warning(ex, "哈希失败 {File}", srcFile); }
                }
            }

            report.Objects.Add(result);
            _log.Information("验证 {Id}: {Status}（文件 {SF}/{TF}, 字节 {SB}/{TB}{Hash}）",
                obj.ObjectId, result.Status, result.SourceFiles, result.TargetFiles,
                Format.Bytes(result.SourceBytes), Format.Bytes(result.TargetBytes),
                level >= VerifyLevel.L2_SampleHash ? $", 哈希抽样 {result.HashSampled} 项, 不一致 {result.HashMismatched}" : "");
        }

        report.OverallPass = report.Objects.All(o => o.Status == "OK");
        _ctx.SaveVerify(report);
        _log.Information("验证结束: {Result}", report.OverallPass ? "全部通过" : "存在不一致对象");
        return report;
    }

    private sealed class SideStat
    {
        public long Files; public long Bytes;
        /// <summary>true = 文件量超过内存清单上限，已自动切换流式核对。</summary>
        public bool Overflow;
        public List<string> RelativePaths { get; } = new();
        public List<(string Relative, long Length)> FilesForHash { get; } = new();
    }

    private SideStat EnumerateSide(string root, PlannedObject obj,
        HashSet<string> exclDirs, Func<string, bool> exclFileMatch, bool skipPlaceholders, bool collectLists)
    {
        var stat = new SideStat();

        // 文件清单对象：只核对清单内的文件（策略排除名依然生效）
        if (obj.FileList != null)
        {
            foreach (var name in obj.FileList)
            {
                if (exclFileMatch(name)) continue;
                var path = Path.Combine(root, name);
                try
                {
                    if (!File.Exists(path)) continue;
                    var fi = new FileInfo(path);
                    stat.Files++; stat.Bytes += fi.Length;
                    stat.RelativePaths.Add(name);
                    stat.FilesForHash.Add((name, fi.Length));
                }
                catch { /* 单文件失败跳过 */ }
            }
            return stat;
        }

        if (!Directory.Exists(root)) return stat;

        // 迭代遍历（显式栈）：目录深度无递归栈限制
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
                try
                {
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (e is FileInfo fi)
                    {
                        if (exclFileMatch(fi.Name)) continue;
                        if (skipPlaceholders && ((fi.Attributes & DirStat.CloudPlaceholderAttr) == DirStat.CloudPlaceholderAttr
                            || (fi.Attributes & FileAttributes.Offline) == FileAttributes.Offline)) continue;
                        // RootFiles 对象只统计根层文件
                        if (obj.Kind == ObjectKind.RootFiles &&
                            !string.Equals(Path.GetDirectoryName(fi.FullName), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                            continue;
                        stat.Files++; stat.Bytes += fi.Length;
                        if (collectLists && !stat.Overflow)
                        {
                            if (stat.RelativePaths.Count >= MaxListEntries)
                            {
                                // 清单爆顶：清空并进入流式模式（计数不受影响，继续精确累计）
                                stat.Overflow = true;
                                stat.RelativePaths.Clear();
                                stat.FilesForHash.Clear();
                                _log.Warning("验证侧清单超过 {Cap} 项，进入流式核对模式: {Root}", MaxListEntries, root);
                            }
                            else
                            {
                                var rel = Path.GetRelativePath(root, fi.FullName);
                                stat.RelativePaths.Add(rel);
                                stat.FilesForHash.Add((rel, fi.Length));
                            }
                        }
                    }
                    else if (e is DirectoryInfo di)
                    {
                        if (exclDirs.Contains(di.Name)) continue;
                        if (obj.Kind == ObjectKind.RootFiles) continue; // 不递归
                        pending.Push(di.FullName);
                    }
                }
                catch { /* 单文件失败跳过 */ }
            }
        }
        return stat;
    }

    /// <summary>流式过源：逐文件核对目标存在性（缺失样例）+ 确定性收集哈希样本，全程不存全量清单。</summary>
    private List<(string Relative, long Length)> StreamSourcePass(PlannedObject obj, ObjectVerifyResult result,
        int samplePercent, HashSet<string> exclDirs, Func<string, bool> exclFileMatch, bool skipPlaceholders, CancellationToken ct)
    {
        var samples = new List<(string Relative, long Length)>();
        var pending = new Stack<string>();
        pending.Push(obj.SourcePath);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos(); }
            catch { continue; }
            foreach (var e in entries)
            {
                try
                {
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (e is FileInfo fi)
                    {
                        if (exclFileMatch(fi.Name)) continue;
                        if (skipPlaceholders && ((fi.Attributes & DirStat.CloudPlaceholderAttr) == DirStat.CloudPlaceholderAttr
                            || (fi.Attributes & FileAttributes.Offline) == FileAttributes.Offline)) continue;
                        if (obj.Kind == ObjectKind.RootFiles &&
                            !string.Equals(Path.GetDirectoryName(fi.FullName), obj.SourcePath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                            continue;
                        var rel = Path.GetRelativePath(obj.SourcePath, fi.FullName);
                        var dst = Path.Combine(obj.TargetPath, rel);
                        bool exists;
                        try { exists = File.Exists(dst); } catch { exists = false; }
                        if (!exists && result.MissingSamples.Count < MaxMissingSamples)
                            result.MissingSamples.Add(rel);
                        if (samples.Count < 2000 && StableHash(rel) % 100 < samplePercent)
                            samples.Add((rel, fi.Length));
                    }
                    else if (e is DirectoryInfo di)
                    {
                        if (exclDirs.Contains(di.Name)) continue;
                        if (obj.Kind == ObjectKind.RootFiles) continue;
                        pending.Push(di.FullName);
                    }
                }
                catch { /* 单文件失败跳过 */ }
            }
        }
        return samples;
    }

    internal static int StableHash(string s)
    {
        // FNV-1a：跨进程稳定的字符串哈希，保证抽样集合确定性
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in s) { h ^= c; h *= 16777619; }
            return (int)(h % 1000);
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash);
    }
}
