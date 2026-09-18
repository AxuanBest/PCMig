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

    /// <summary>
    /// 扫描残缺告警（v0.3.8）：旧版只在对象日志里拼一句"[部分不可访问×N]"，没有任何地方拦人，
    /// 于是"少扫了几百 GB"会一路走到"迁移完成"。这里把"多少处、哪些路径、为什么"写成显式告警。
    /// </summary>
    private static void AddIncompleteScanWarning(ObservedState observed)
    {
        if (observed.InaccessiblePaths.Count == 0) return;
        var sample = observed.InaccessiblePaths.Take(5).Select(s => "　　· " + s);
        observed.Warnings.Add(
            $"⚠ 扫描存在不可访问的目录/文件（{observed.InaccessiblePaths.Count} 处）：" +
            "这些位置里的数据既不在计划内、也不会被复制。默认不允许在此状态下开始迁移——" +
            "请先修好权限/网络后重新扫描；确知风险仍要继续，必须显式确认（CLI: --allow-incomplete-scan；界面会弹窗）。" +
            "前几处：\n" + string.Join("\n", sample) +
            (observed.InaccessiblePaths.Count > 5 ? $"\n　　… 其余 {observed.InaccessiblePaths.Count - 5} 处见 observed-state.json" : ""));
    }

    public async Task<ObservedState> ScanAsync(JobDefinition job, Matrix.MigrationMatrix matrix,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var threshold = (long)job.Options.LargeFileThresholdMB * 1024 * 1024;
        // 扫描与传输/验证使用同一套排除名单，三方口径一致才不会误报
        var exclDirs = new HashSet<string>(matrix.ExcludedDirectoryNames, StringComparer.OrdinalIgnoreCase);
        var exclFiles = new HashSet<string>(
            matrix.ExcludedFileNames.Concat(matrix.SecurityBlockedFileNames), StringComparer.OrdinalIgnoreCase);
        var exclFileMatch = FilePatternMatcher.Build(exclFiles);
        var skipPlaceholders = string.Equals(matrix.CloudPlaceholderPolicy, "Skip", StringComparison.OrdinalIgnoreCase);
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
                        // 不可访问的位置必须带"哪条路径 + 为什么"记下来：扫描残缺会静默漏数据（v0.3.8 闸门）
                        var inacc = new System.Collections.Concurrent.ConcurrentBag<string>();
                        var stat = await Task.Run(() => DirStat.Measure(path, threshold, exclDirs, exclFiles,
                            (p, why) => inacc.Add($"{id}｜{p}｜{why}"), skipPlaceholders), ct);
                        foreach (var s in inacc) observed.InaccessiblePaths.Add(s);
                        observed.Objects.Add(new ScannedObject
                        {
                            ObjectId = id, Kind = ObjectKind.DataVolume, SourcePath = path,
                            Bytes = stat.Bytes, Files = stat.Files, Dirs = stat.Dirs,
                            HasLargeFiles = stat.HasLargeFiles,
                            HasCloudPlaceholders = stat.HasCloudPlaceholders,
                            ScanIncomplete = stat.Incomplete,
                            LockRiskFiles = stat.LockRiskFiles,
                            EncryptedFiles = stat.EncryptedFiles
                        });
                        _log.Information("对象 {Id}: {Path} = {Bytes} / {Files} 文件（显式选择目录）",
                            id, path, Format.Bytes(stat.Bytes), stat.Files);
                    }
                    else if (File.Exists(path))
                    {
                        var parent = Path.GetDirectoryName(path)!;
                        var name = Path.GetFileName(path);
                        if (exclFileMatch(name)) { _log.Information("策略排除，跳过文件: {Path}", path); continue; }
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
            observed.LockRiskFiles = observed.Objects.Sum(o => o.LockRiskFiles);
            observed.EncryptedFiles = observed.Objects.Sum(o => o.EncryptedFiles);
            if (observed.LockRiskFiles > 0)
                observed.Warnings.Add($"检测到 {observed.LockRiskFiles} 个 Outlook 数据文件(.pst/.ost)：若旧电脑 Outlook 正在运行，它们会被锁定导致复制失败。建议迁移前让旧电脑用户关闭 Outlook。");
            if (observed.EncryptedFiles > 0)
                observed.Warnings.Add($"检测到 {observed.EncryptedFiles} 个 EFS 加密文件：内容可以正常复制，但在新机上将失去加密保护（明文可读）。如合规要求加密，请在新机上重新加密。");
            AddIncompleteScanWarning(observed);
            _log.Information("扫描完成(Custom): {Objects} 个对象, 共 {Bytes} / {Files} 文件（锁风险 {Lock} / EFS {Enc} / 不可访问 {Inacc}）",
                observed.Objects.Count, Format.Bytes(observed.TotalBytes), observed.TotalFiles,
                observed.LockRiskFiles, observed.EncryptedFiles, observed.InaccessiblePaths.Count);
            if (observed.InaccessiblePaths.Count > 0)
                _log.Warning("扫描存在不可访问位置 {Count} 处（默认拦截迁移，需修好或显式确认）: {First}",
                    observed.InaccessiblePaths.Count, observed.InaccessiblePaths.First());
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
            var inaccessibleAll = new ConcurrentBag<string>();

            await Parallel.ForEachAsync(dirs,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                async (dir, token) =>
                {
                    var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
                    var id = $"object-{Interlocked.Increment(ref seq):000000}";
                    progress?.Report($"扫描 {name} …");
                    var inaccessible = 0;
                    var inacc = new System.Collections.Concurrent.ConcurrentBag<string>();
                    var stat = await Task.Run(() => DirStat.Measure(dir, threshold, exclDirs, exclFiles,
                        (p, why) => { Interlocked.Increment(ref inaccessible); inacc.Add($"{id}｜{p}｜{why}"); },
                        skipPlaceholders), token);
                    foreach (var s in inacc) inaccessibleAll.Add(s);
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
                        ScanIncomplete = stat.Incomplete,
                        LockRiskFiles = stat.LockRiskFiles,
                        EncryptedFiles = stat.EncryptedFiles
                    };
                    objects.Add(obj);
                    _log.Information("对象 {Id}: {Path} = {Bytes} / {Files} 文件{Flags}",
                        id, dir, Format.Bytes(stat.Bytes), stat.Files,
                        (stat.HasLargeFiles ? " [大文件]" : "") + (stat.HasCloudPlaceholders ? " [云占位符]" : "") +
                        (stat.Incomplete ? $" [部分不可访问×{inaccessible}]" : ""));
                });

            foreach (var o in objects.OrderBy(x => x.SourcePath, StringComparer.OrdinalIgnoreCase))
                observed.Objects.Add(o);
            foreach (var s in inaccessibleAll.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                observed.InaccessiblePaths.Add(s);

            // 源根散落文件 → 一个 RootFiles 对象
            if (DirStat.RootHasLooseFiles(source.Path))
            {
                var id = $"object-{Interlocked.Increment(ref seq):000000}";
                // 必须实测：旧实现写死 Bytes=-1 赌"根散落文件很小"，实测踩中 D 盘根目录 14.2GB 的素材.zip，
                // 结果是计划总量漏计、进度显示 32.8GB/26.8GB(100%) 的假象。
                var loose = DirStat.MeasureLooseFiles(source.Path, threshold, exclFiles, skipPlaceholders);
                observed.Objects.Add(new ScannedObject
                {
                    ObjectId = id,
                    Kind = ObjectKind.RootFiles,
                    SourcePath = source.Path,  // 特殊：以根路径+仅一级文件表达
                    Bytes = loose.Bytes,
                    Files = loose.Files,
                    HasLargeFiles = loose.HasLargeFiles,
                    HasCloudPlaceholders = loose.HasCloudPlaceholders,
                    ScanIncomplete = loose.Incomplete,
                    LockRiskFiles = loose.LockRiskFiles,
                    EncryptedFiles = loose.EncryptedFiles
                });
                if (loose.Incomplete)
                    observed.InaccessiblePaths.Add($"{id}｜{source.Path}｜根目录散落文件枚举失败（权限/网络）");
                _log.Information("对象 {Id}: {Path} 根目录散落文件 (RootFiles) = {Bytes} / {Files} 文件",
                    id, source.Path, Format.Bytes(loose.Bytes), loose.Files);
            }

            if (observed.Objects.Any(o => o.HasCloudPlaceholders))
                observed.Warnings.Add(skipPlaceholders
                    ? "检测到云占位符（OneDrive 等）：已按矩阵策略 Skip 跳过（不复制、不触发云回源下载），新机登录账号后会自动同步。"
                    : "检测到云占位符（OneDrive 等）：这些文件在源端未实际下载，复制会触发云端回源下载（可能拖慢网络）。建议把矩阵 cloudPlaceholderPolicy 改为 Skip 跳过它们，新机登录账号后自动同步。");
        }

        observed.TotalBytes = observed.Objects.Where(o => o.Bytes > 0).Sum(o => o.Bytes);
        observed.TotalFiles = observed.Objects.Where(o => o.Files > 0).Sum(o => o.Files);
        observed.LockRiskFiles = observed.Objects.Sum(o => o.LockRiskFiles);
        observed.EncryptedFiles = observed.Objects.Sum(o => o.EncryptedFiles);
        AddIncompleteScanWarning(observed);
        if (observed.LockRiskFiles > 0)
            observed.Warnings.Add($"检测到 {observed.LockRiskFiles} 个 Outlook 数据文件(.pst/.ost)：若旧电脑 Outlook 正在运行，它们会被锁定导致复制失败。建议迁移前让旧电脑用户关闭 Outlook。");
        if (observed.EncryptedFiles > 0)
            observed.Warnings.Add($"检测到 {observed.EncryptedFiles} 个 EFS 加密文件：内容可以正常复制，但在新机上将失去加密保护（明文可读）。如合规要求加密，请在新机上重新加密。");
        _log.Information("扫描完成: {Objects} 个对象, 共 {Bytes} / {Files} 文件（锁风险 {Lock} / EFS {Enc} / 不可访问 {Inacc}）",
            observed.Objects.Count, Format.Bytes(observed.TotalBytes), observed.TotalFiles,
            observed.LockRiskFiles, observed.EncryptedFiles, observed.InaccessiblePaths.Count);
        if (observed.InaccessiblePaths.Count > 0)
            _log.Warning("扫描存在不可访问位置 {Count} 处（默认拦截迁移，需修好或显式确认）: {First}",
                observed.InaccessiblePaths.Count, observed.InaccessiblePaths.First());
        return observed;
    }
}
