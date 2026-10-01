namespace PCMig.Diagnostics;

/// <summary>清理结果（用于如实报告"删了多少、还剩多少、配额是否仍然超"）。</summary>
public readonly record struct RetentionResult(
    int DeletedFiles,
    long DeletedBytes,
    long KeptBytes,
    bool QuotaExhausted);

/// <summary>
/// 保留/配额管理（方案 §15）。硬约束：
///   · 只在诊断根目录内操作（前缀校验 + 拒绝 reparse 穿越）；
///   · 只删**已封段**（有 manifest 的 .jsonl）；活动段、租约段（pinned）永不删；
///   · 总配额最终生效：优先停止新写入/导出，再删最旧段；仍超则如实标 QuotaExhausted；
///   · 清空不了不是"没关系"：必须在健康里可见。
/// </summary>
public static class RetentionManager
{
    public static RetentionResult Enforce(
        DiagnosticSessionStore store,
        DiagnosticRuntimeOptions options,
        IReadOnlyCollection<string>? pinnedPaths = null)
    {
        var root = Path.GetFullPath(store.Root);
        var pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (pinnedPaths is not null)
            foreach (var p in pinnedPaths) pinned.Add(Path.GetFullPath(p));

        var sealedFiles = new List<(string Path, string ManifestPath, long Length, DateTime LastWriteUtc)>();
        long total = 0;

        foreach (var file in EnumerateConfined(root))
        {
            long length;
            DateTime lastWrite;
            try
            {
                var info = new FileInfo(file);
                length = info.Length;
                lastWrite = info.LastWriteTimeUtc;
            }
            catch (Exception) { continue; }

            total += length;

            var manifest = file + ".manifest.json";
            if (file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) && File.Exists(manifest))
                sealedFiles.Add((file, manifest, length, lastWrite));
        }

        var deletedFiles = 0;
        long deletedBytes = 0;
        var ordered = sealedFiles.OrderBy(f => f.LastWriteUtc).ToList();

        // ① 配额：删到配额的 90% 以下（留一点回旋，避免每轮都触发删除）。
        if (total > options.TotalQuotaBytes)
        {
            var target = (long)(options.TotalQuotaBytes * 0.9);
            foreach (var candidate in ordered)
            {
                if (total <= target) break;
                if (pinned.Contains(candidate.Path)) continue;
                if (TryDelete(candidate.Path, candidate.ManifestPath, ref deletedFiles, ref deletedBytes))
                    total -= candidate.Length;
            }
        }

        // ② 年龄：超过保留天数的密封段一并清理。
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, options.RetentionDays));
        foreach (var candidate in ordered)
        {
            if (candidate.LastWriteUtc >= cutoff) continue;
            if (pinned.Contains(candidate.Path)) continue;
            if (!File.Exists(candidate.Path)) continue;
            if (TryDelete(candidate.Path, candidate.ManifestPath, ref deletedFiles, ref deletedBytes))
                total -= candidate.Length;
        }

        return new RetentionResult(deletedFiles, deletedBytes, Math.Max(0, total), total > options.TotalQuotaBytes);
    }

    private static bool TryDelete(string path, string manifestPath, ref int deletedFiles, ref long deletedBytes)
    {
        try
        {
            // 拒绝 reparse：不跟随链接删除别处的东西。
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReparsePoint) != 0) return false;

            var length = new FileInfo(path).Length;
            File.Delete(path);
            deletedFiles++;
            deletedBytes += length;

            try { if (File.Exists(manifestPath)) File.Delete(manifestPath); } catch (Exception) { /* 清单残留无害 */ }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>只枚举诊断根目录内的普通文件；跳过 reparse point（防穿越）。</summary>
    private static IEnumerable<string> EnumerateConfined(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            string[] files;
            string[] subdirs;
            try
            {
                files = Directory.GetFiles(dir);
                subdirs = Directory.GetDirectories(dir);
            }
            catch (Exception) { continue; }

            foreach (var file in files) yield return file;

            foreach (var sub in subdirs)
            {
                try
                {
                    var attrs = File.GetAttributes(sub);
                    if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch (Exception) { continue; }
                pending.Push(sub);
            }
        }
    }
}