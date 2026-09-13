using System.Collections.Concurrent;
using PCMig.Core.Models;
using PCMig.Core.Util;
using Serilog;

namespace PCMig.Core.Scan;

/// <summary>
/// Scanner：把源划分成"对象级"迁移单元（源根下每个一级目录 = 一个对象，散落文件归并为 RootFiles 对象），
/// 并实测每个对象的字节数/文件数/大文件标记/云占位符标记。
/// 扫描结果是 Planner 的唯一输入。
/// </summary>
public sealed class SourceScanner
{
    private readonly ILogger _log;
    public SourceScanner(ILogger log) { _log = log.ForContext<SourceScanner>(); }

    public async Task<ObservedState> ScanAsync(JobDefinition job, Matrix.MigrationMatrix matrix,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var threshold = (long)job.Options.LargeFileThresholdMB * 1024 * 1024;
        // 扫描与传输/验证使用同一套排除名单，三方口径一致才不会误报
        var exclDirs = new HashSet<string>(matrix.ExcludedDirectoryNames, StringComparer.OrdinalIgnoreCase);
        var exclFiles = new HashSet<string>(
            matrix.ExcludedFileNames.Concat(matrix.SecurityBlockedFileNames), StringComparer.OrdinalIgnoreCase);
        var observed = new ObservedState { JobId = job.JobId };
        var seq = 0;

        // ---- Custom 模式：用户显式勾选的目录/文件清单 ----
        if (job.CustomSelections.Count > 0)
        {
            _log.Information("Custom 模式：{Count} 个显式选择路径", job.CustomSelections.Count);
            var fileGroups = new Dictionary<string, List<(string Name, long Length)>>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in job.CustomSelections)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (Directory.Exists(path))
                    {
                        var id = $"object-{Interlocked.Increment(ref seq):000000}";
                        progress?.Report($"扫描 {path} …");
                        var stat = await Task.Run(() => DirStat.Measure(path, threshold, exclDirs, exclFiles), ct);
                        observed.Objects.Add(new ScannedObject
                        {
                            ObjectId = id, Kind = ObjectKind.DataVolume, SourcePath = path,
                            Bytes = stat.Bytes, Files = stat.Files, Dirs = stat.Dirs,
                            HasLargeFiles = stat.HasLargeFiles,
                            HasCloudPlaceholders = stat.HasCloudPlaceholders,
                            ScanIncomplete = stat.Incomplete
                        });
                        _log.Information("对象 {Id}: {Path} = {Bytes} / {Files} 文件（显式选择目录）",
                            id, path, Format.Bytes(stat.Bytes), stat.Files);
                    }
                    else if (File.Exists(path))
                    {
                        var parent = Path.GetDirectoryName(path)!;
                        var name = Path.GetFileName(path);
                        if (exclFiles.Contains(name)) { _log.Information("策略排除，跳过文件: {Path}", path); continue; }
                        var len = new FileInfo(path).Length;
                        if (!fileGroups.TryGetValue(parent, out var list)) fileGroups[parent] = list = new();
                        list.Add((name, len));
                    }
                    else
                    {
                        observed.Warnings.Add($"选择的路径不存在，已跳过: {path}");
                        _log.Warning("选择的路径不存在: {Path}", path);
                    }
                }
                catch (Exception ex)
                {
                    observed.Warnings.Add($"路径访问失败 {path}: {ex.Message}");
                    _log.Warning(ex, "路径访问失败: {Path}", path);
                }
            }

            foreach (var (parent, files) in fileGroups)
            {
                var id = $"object-{Interlocked.Increment(ref seq):000000}";
                var bytes = files.Sum(f => f.Length);
                observed.Objects.Add(new ScannedObject
                {
                    ObjectId = id, Kind = ObjectKind.DataVolume, SourcePath = parent,
                    Bytes = bytes, Files = files.Count, Dirs = 0,
                    HasLargeFiles = files.Any(f => f.Length >= threshold),
                    FileList = files.Select(f => f.Name).ToList()
                });
                _log.Information("对象 {Id}: {Path} 下 {Count} 个显式选择文件 = {Bytes}",
                    id, parent, files.Count, Format.Bytes(bytes));
            }

            observed.TotalBytes = observed.Objects.Where(o => o.Bytes > 0).Sum(o => o.Bytes);
            observed.TotalFiles = observed.Objects.Where(o => o.Files > 0).Sum(o => o.Files);
            _log.Information("扫描完成(Custom): {Objects} 个对象, 共 {Bytes} / {Files} 文件",
                observed.Objects.Count, Format.Bytes(observed.TotalBytes), observed.TotalFiles);
            return observed;
        }

        // ---- Whole 模式：源根 → 一级目录拆对象 ----
        foreach (var source in job.Sources.Where(s => s.Enabled))
        {
            ct.ThrowIfCancellationRequested();
            _log.Information("扫描源: {Path} (Kind={Kind})", source.Path, source.Kind);

            if (!Directory.Exists(source.Path))
            {
                observed.Warnings.Add($"源路径不可访问，已跳过: {source.Path}");
                _log.Warning("源路径不可访问: {Path}", source.Path);
                continue;
            }

            var dirs = DirStat.TopLevelDirs(source.Path, exclDirs);
            _log.Information("{Path} 下发现 {Count} 个一级目录对象", source.Path, dirs.Count);

            var objects = new ConcurrentBag<ScannedObject>();

            await Parallel.ForEachAsync(dirs,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                async (dir, token) =>
                {
                    var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
                    var id = $"object-{Interlocked.Increment(ref seq):000000}";
                    progress?.Report($"扫描 {name} …");
                    var inaccessible = 0;
                    var stat = await Task.Run(() => DirStat.Measure(dir, threshold, exclDirs, exclFiles,
                        _ => Interlocked.Increment(ref inaccessible)), token);
                    var obj = new ScannedObject
                    {
                        ObjectId = id,
                        Kind = source.Kind,
                        SourcePath = dir,
                        Bytes = stat.Bytes,
                        Files = stat.Files,
                        Dirs = stat.Dirs,
                        HasLargeFiles = stat.HasLargeFiles,
                        HasCloudPlaceholders = stat.HasCloudPlaceholders,
                        ScanIncomplete = stat.Incomplete
                    };
                    objects.Add(obj);
                    _log.Information("对象 {Id}: {Path} = {Bytes} / {Files} 文件{Flags}",
                        id, dir, Format.Bytes(stat.Bytes), stat.Files,
                        (stat.HasLargeFiles ? " [大文件]" : "") + (stat.HasCloudPlaceholders ? " [云占位符]" : "") +
                        (stat.Incomplete ? $" [部分不可访问×{inaccessible}]" : ""));
                });

            foreach (var o in objects.OrderBy(x => x.SourcePath, StringComparer.OrdinalIgnoreCase))
                observed.Objects.Add(o);

            // 源根散落文件 → 一个 RootFiles 对象
            if (DirStat.RootHasLooseFiles(source.Path))
            {
                var id = $"object-{Interlocked.Increment(ref seq):000000}";
                observed.Objects.Add(new ScannedObject
                {
                    ObjectId = id,
                    Kind = ObjectKind.RootFiles,
                    SourcePath = source.Path,  // 特殊：以根路径+仅一级文件表达
                    Bytes = -1, Files = -1     // 根散落文件体积通常很小，传输时以 robocopy 实测
                });
                _log.Information("对象 {Id}: {Path} 根目录散落文件 (RootFiles)", id, source.Path);
            }

            if (observed.Objects.Any(o => o.HasCloudPlaceholders))
                observed.Warnings.Add("检测到云占位符（OneDrive 等）：这些文件在源端未实际下载。建议改为迁移云同步配置并让新机重新同步，详见矩阵 cloudPlaceholder 策略。");
        }

        observed.TotalBytes = observed.Objects.Where(o => o.Bytes > 0).Sum(o => o.Bytes);
        observed.TotalFiles = observed.Objects.Where(o => o.Files > 0).Sum(o => o.Files);
        _log.Information("扫描完成: {Objects} 个对象, 共 {Bytes} / {Files} 文件",
            observed.Objects.Count, Format.Bytes(observed.TotalBytes), observed.TotalFiles);
        return observed;
    }
}
