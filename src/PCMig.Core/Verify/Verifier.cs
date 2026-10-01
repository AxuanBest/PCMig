using System.Security.Cryptography;
using PCMig.Core.Diagnostics;
using PCMig.Core.Jobs;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Util;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
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

        // ★ D6.1 §16 观察点 ★ 只记录既有事实：级别、对象数、抽样百分比、计划是否每个对象都有源/目标路径。
        //   **不做**任何业务校验、不因为观察而改变验证行为。
        var allObjectsHavePaths = plan.Objects.All(o =>
            !string.IsNullOrWhiteSpace(o.SourcePath) && !string.IsNullOrWhiteSpace(o.TargetPath));
        var samplePercentForEvidence = Math.Clamp(_ctx.Definition.Options.SampleHashPercent, 1, 100);
        PublishVerify(VerifyEvents.VerifyStarted,
            new VrfVerifyStartedPayload(level.ToString(), plan.Objects.Count, samplePercentForEvidence),
            DiagnosticLevel.Information, DiagnosticOutcome.Succeeded);
        PublishVerify(VerifyEvents.PlanValidated,
            new VrfPlanValidatedPayload(plan.Objects.Count, allObjectsHavePaths, "job-plan"),
            DiagnosticLevel.Information, allObjectsHavePaths ? DiagnosticOutcome.Succeeded : DiagnosticOutcome.Unknown);

        var evidenceEnumerationErrors = 0L;
        var evidenceAnyOverflow = false;

        foreach (var obj in plan.Objects)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"验证 {obj.ObjectId} …");

            var result = new ObjectVerifyResult { ObjectId = obj.ObjectId };
            var objContext = _ctx.Diagnostics.WithObject(obj.ObjectId);
            // 目标侧先枚举（清单用于快速比对）；源侧只有在目标侧没溢出时才收集清单
            PublishVerify(VerifyEvents.TargetStatStarted, new VrfSideStatPayload("target", 0, 0, false, 0, 0),
                DiagnosticLevel.Debug, DiagnosticOutcome.Accepted, objContext);
            var dstSw = System.Diagnostics.Stopwatch.StartNew();
            SideStat dstStat;
            try
            {
                dstStat = await Task.Run(() => EnumerateSide(obj.TargetPath, obj, exclDirs, exclFileMatch, skipPlaceholders, true), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PublishVerify(VerifyEvents.TargetStatFailed, new VrfFailurePayload("target-stat", ex.GetType().Name, 1),
                    DiagnosticLevel.Warning, DiagnosticOutcome.Failed, objContext);
                throw;
            }
            dstSw.Stop();
            PublishSideStat(VerifyEvents.TargetStatSucceeded, "target", dstStat, dstSw.ElapsedMilliseconds, objContext);

            PublishVerify(VerifyEvents.SourceStatStarted, new VrfSideStatPayload("source", 0, 0, false, 0, 0),
                DiagnosticLevel.Debug, DiagnosticOutcome.Accepted, objContext);
            var srcSw = System.Diagnostics.Stopwatch.StartNew();
            SideStat srcStat;
            try
            {
                srcStat = await Task.Run(() => EnumerateSide(obj.SourcePath, obj, exclDirs, exclFileMatch, skipPlaceholders, !dstStat.Overflow), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PublishVerify(VerifyEvents.SourceStatFailed, new VrfFailurePayload("source-stat", ex.GetType().Name, 1),
                    DiagnosticLevel.Warning, DiagnosticOutcome.Failed, objContext);
                throw;
            }
            srcSw.Stop();
            PublishSideStat(VerifyEvents.SourceStatSucceeded, "source", srcStat, srcSw.ElapsedMilliseconds, objContext);

            evidenceEnumerationErrors += srcStat.EnumerationErrors + dstStat.EnumerationErrors;
            evidenceAnyOverflow |= srcStat.Overflow || dstStat.Overflow;

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

                // ★ D6.1 §16 ★ 抽样规模（清单模式）：只陈述"候选多少、抽了多少、按几%抽"。
                PublishVerify(VerifyEvents.SampleSelected,
                    new VrfHashSamplePayload("list", srcStat.FilesForHash.Count, hashSamples.Count, samplePercent),
                    DiagnosticLevel.Debug, DiagnosticOutcome.Succeeded, objContext);
            }
            else
            {
                // 流式模式（超大对象）：不存全量清单，第二遍流式过源，逐文件核对目标存在性 + 收集哈希样本
                _log.Information("对象 {Id} 文件量超清单上限，切换流式核对（内存有界，结果同样精确）", obj.ObjectId);
                progress?.Report($"验证 {obj.ObjectId} …（超大对象，流式核对）");
                hashSamples = await Task.Run(() => StreamSourcePass(obj, result, samplePercent, exclDirs, exclFileMatch, skipPlaceholders, ct), ct);

                PublishVerify(VerifyEvents.SampleSelected,
                    new VrfHashSamplePayload("stream", -1, hashSamples.Count, samplePercent),
                    DiagnosticLevel.Debug, DiagnosticOutcome.Succeeded, objContext);
            }

            if (level >= VerifyLevel.L2_SampleHash)
            {
                PublishVerify(VerifyEvents.HashStarted,
                    new VrfHashSamplePayload("samples", hashSamples.Count, hashSamples.Count, samplePercent),
                    DiagnosticLevel.Debug, DiagnosticOutcome.Accepted, objContext);

                var hashFailed = 0L;
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
                    catch (Exception ex)
                    {
                        hashFailed++;
                        _log.Warning(ex, "哈希失败 {File}", srcFile);
                        // ★ 哈希本身失败 ≠ mismatch ★ 现有业务口径不变，只把事实记下来。
                        PublishVerify(VerifyEvents.HashFailed,
                            new VrfFailurePayload("hash", ex.GetType().Name, 1),
                            DiagnosticLevel.Warning, DiagnosticOutcome.Failed, objContext);
                    }
                }

                PublishVerify(VerifyEvents.HashSucceeded,
                    new VrfHashResultPayload(result.HashSampled, result.HashMismatched, hashFailed),
                    DiagnosticLevel.Debug, DiagnosticOutcome.Succeeded, objContext);

                if (result.HashMismatched > 0)
                    PublishVerify(VerifyEvents.Mismatch,
                        new VrfMismatchPayload("hash-mismatch", result.HashMismatched,
                            result.MissingSamples.Count > 0 ? null : null),
                        DiagnosticLevel.Warning, DiagnosticOutcome.Failed, objContext);
            }

            // 缺文件（清单/流式两种模式都会收集）与计数不一致也各记一条（只陈述事实）。
            if (result.MissingSamples.Count > 0)
                PublishVerify(VerifyEvents.Mismatch,
                    new VrfMismatchPayload("missing-on-target", result.MissingSamples.Count, null),
                    DiagnosticLevel.Warning, DiagnosticOutcome.Failed, objContext);
            if (srcStat.Files != dstStat.Files)
                PublishVerify(VerifyEvents.Mismatch,
                    new VrfMismatchPayload("count-mismatch", Math.Abs(srcStat.Files - dstStat.Files), null),
                    DiagnosticLevel.Warning, DiagnosticOutcome.Failed, objContext);
            if (srcStat.Bytes != dstStat.Bytes)
                PublishVerify(VerifyEvents.Mismatch,
                    new VrfMismatchPayload("bytes-mismatch", Math.Abs(srcStat.Bytes - dstStat.Bytes), null),
                    DiagnosticLevel.Warning, DiagnosticOutcome.Failed, objContext);

            report.Objects.Add(result);
            _log.Information("验证 {Id}: {Status}（文件 {SF}/{TF}, 字节 {SB}/{TB}{Hash}）",
                obj.ObjectId, result.Status, result.SourceFiles, result.TargetFiles,
                Format.Bytes(result.SourceBytes), Format.Bytes(result.TargetBytes),
                level >= VerifyLevel.L2_SampleHash ? $", 哈希抽样 {result.HashSampled} 项, 不一致 {result.HashMismatched}" : "");
        }

        report.OverallPass = report.Objects.All(o => o.Status == "OK");
        _ctx.SaveVerify(report);

        // ★ D6.1 §16 观察点 ★ 完成统计 + **统计完整性**：
        //   "0/0 也是 OK" 这类形态必须能被诊断单独指出（业务 OverallPass 不动）。
        var statsCompleteness = evidenceEnumerationErrors == 0 && !evidenceAnyOverflow ? "Complete"
            : evidenceEnumerationErrors > 0 ? "Partial"
            : "Partial";
        var note = evidenceEnumerationErrors > 0
            ? $"枚举期有 {evidenceEnumerationErrors} 个条目被跳过（权限/竞态等），统计可能不完整"
            : evidenceAnyOverflow ? "部分对象文件量超清单上限，已切流式核对（结果仍精确，统计口径不同）"
            : "枚举期无跳过、无流式降级";

        PublishVerify(VerifyEvents.StatsCompletenessObserved,
            new VrfStatsCompletenessPayload(statsCompleteness, evidenceEnumerationErrors, evidenceAnyOverflow, note),
            statsCompleteness == "Complete" ? DiagnosticLevel.Information : DiagnosticLevel.Warning,
            statsCompleteness == "Complete" ? DiagnosticOutcome.Succeeded : DiagnosticOutcome.Unknown);

        PublishVerify(VerifyEvents.Completed,
            new VrfCompletedPayload(
                report.Objects.Count,
                report.OverallPass,
                report.Objects.Sum(o => o.HashSampled),
                report.Objects.Sum(o => o.HashMismatched),
                report.Objects.Sum(o => o.MissingSamples.Count),
                statsCompleteness),
            DiagnosticLevel.Information,
            report.OverallPass ? DiagnosticOutcome.Succeeded : DiagnosticOutcome.Failed);

        _log.Information("验证结束: {Result}", report.OverallPass ? "全部通过" : "存在不一致对象");
        return report;
    }

    /// <summary>
    /// 发布一条验证观测（D6.1 §16）。**只观察**：任何失败都被吞掉（绝不改变验证行为），
    /// 并自动继承动作链上下文（验证动作的 ActionId）。
    /// </summary>
    private void PublishVerify(
        EventDescriptor descriptor,
        IDiagnosticPayload? payload,
        DiagnosticLevel level,
        DiagnosticOutcome outcome,
        DiagnosticContext? context = null)
        => CoreDiagnostics.PublishCore(
            descriptor, payload, context ?? _ctx.Diagnostics, level, outcome,
            component: "Verifier", stateOwner: StateOwner.Core);

    private void PublishSideStat(
        EventDescriptor descriptor, string role, SideStat stat, long elapsedMs, DiagnosticContext context)
    {
        PublishVerify(descriptor,
            new VrfSideStatPayload(role, stat.Files, stat.Bytes, stat.Overflow, stat.EnumerationErrors, elapsedMs),
            DiagnosticLevel.Debug, DiagnosticOutcome.Succeeded, context);

        // ★ D6.1 §16 ★ 枚举期**真的跳过了条目** ⇒ 按既有计数补一条文件系统证据
        //   （规则在等 FS.StatFailure / FS.TargetStatFailure；不发出等于那些触发路径是死的）。
        //   语义：目标侧枚举跳过 ⇒ TargetStatFailure；源侧 ⇒ StatFailure。只陈述事实。
        if (stat.EnumerationErrors > 0)
        {
            PublishVerify(
                role == "target" ? FsEvents.TargetStatFailure : FsEvents.StatFailure,
                new FsFailPayload(role + "-verify-enumeration", stat.EnumerationErrors,
                    "entries-skipped-during-enumeration"),
                DiagnosticLevel.Warning, DiagnosticOutcome.Unknown, context);
        }
    }

    private sealed class SideStat
    {
        public long Files; public long Bytes;
        /// <summary>true = 文件量超过内存清单上限，已自动切换流式核对。</summary>
        public bool Overflow;

        /// <summary>
        /// ★ D6.1 §16 ★ 枚举期被跳过的条目数（权限/竞态/瞬态 IO）。
        /// 只**计数**：既不改遍历逻辑，也不改变任何跳过决策；但没有它，
        /// "全部 OK" 就无法与"其实很多项根本没枚举到"区分。
        /// </summary>
        public long EnumerationErrors;

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
                catch { stat.EnumerationErrors++; }      // ★ D6.1 §16 ★ 只计数，不改跳过决策
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
            catch { stat.EnumerationErrors++; continue; }   // ★ D6.1 §16 ★ 只计数（目录级跳过）
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
                catch { stat.EnumerationErrors++; }      // ★ D6.1 §16 ★ 只计数，不改跳过决策
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
