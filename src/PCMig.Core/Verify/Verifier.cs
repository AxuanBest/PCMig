using System.Security.Cryptography;
using PCMig.Core.Diagnostics;
using PCMig.Core.Jobs;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Transfer;   // ★ v0.5.4 Preview.2 ★ RepairQuarantine.IsQuarantinedOldCopy：校验侧排除隔离旧副本
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

    // ★ 2026-10-11（验证报告返修）★ 证据记录**有界**：报告要能审计，但绝不能让百万级任务的
    //   验证报告把内存/JSON/浏览器吃穿。超限只计数并如实标注（用户执行令 §03/§05 的"展示上限/记录上限"）。
    private const int MaxFileDiffRecords = 500;
    private const int MaxEnumerationFailureSamples = 200;

    public Verifier(JobContext ctx, MigrationMatrix matrix, ILogger log)
    {
        _ctx = ctx;
        // 任务级自定义排除规则（超大数据模式）与传输/扫描同口径
        _matrix = matrix.WithExtraExclusions(ctx.Definition.CustomExclusions, log);
        _log = log.ForContext<Verifier>();
    }

    /// <param name="progress">人类可读的一行进度（沿用不改：控制台与既有界面都读它）。</param>
    /// <param name="ct">取消令牌（真取消：<c>ThrowIfCancellationRequested</c> 在对象之间检查）。</param>
    /// <param name="detail">
    /// ★ P1-12 ★ 结构化真实进度（验收 R9）。只报**已算完**的累计量，不做任何估算；
    /// 界面没收到快照时必须显示"等待引擎回报"，不得凭时间自己编百分比。
    /// 投递失败只记 Debug 日志，绝不影响验证结论。
    /// </param>
    public async Task<VerifyReport> RunAsync(VerifyLevel level, IProgress<string>? progress = null,
        CancellationToken ct = default, IProgress<VerifyProgressSnapshot>? detail = null)
    {
        var plan = _ctx.Plan ?? throw new InvalidOperationException("plan.json 不存在");
        var report = new VerifyReport { JobId = _ctx.JobId, Level = level };
        // ★ 2026-10-11 ★ 本轮验证的证据档案（运行号 / 起止时刻 / 环境 / 口径）。
        //   只记录**引擎当时真的知道**的事实：主机、目标根、用户显式选择、映射规则都来自 job.json，
        //   排除名单来自计划；拿不到的一律留空 ⇒ 报告写"未采集"，不推导、不编造。
        var runClock = System.Diagnostics.Stopwatch.StartNew();
        report.VerifyRunId = $"{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}"[..24];
        report.StartedUtc = DateTime.UtcNow;
        report.RunStatus = "completed";          // 异常/取消时不会落盘报告 ⇒ 落盘的一定是跑完的一轮
        report.ToolVersion = typeof(Verifier).Assembly.GetName().Version?.ToString() ?? "未知";
        report.SourceHost = _ctx.Definition.SourceHost;
        report.TargetRoot = _ctx.Definition.TargetRoot;
        report.SelectedPaths = _ctx.Definition.Sources.Select(s => s.Path).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        report.MappingRule = _ctx.Definition.Options.SelectedFolderMapping;
        report.ExcludedByPolicy = plan.ExcludedByPolicy?.ToList() ?? new List<string>();
        // 阶段耗时：按对象**真实累计**（Note 写明口径），不做任何估算。
        double targetStatMs = 0, sourceStatMs = 0, compareMs = 0, hashMs = 0;
        long hashSampleCount = 0;
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

        // ★ P1-12 ★ 结构化进度的唯一真值来源：已完成的 `report.Objects` + 真实总对象数。
        var detailClock = System.Diagnostics.Stopwatch.StartNew();
        var seenObjects = 0;
        void ReportDetail(string objectId, string? objectPath, string stage, int decidedObjects)
        {
            if (detail is null) return;
            int ok = 0, mismatch = 0, unknown = 0, empty = 0;
            long filesChecked = 0, bytesChecked = 0;
            foreach (var decided in report.Objects)
            {
                switch (decided.Status)
                {
                    case "OK": ok++; break;
                    case "MISMATCH": mismatch++; break;
                    case "UNKNOWN": unknown++; break;
                    case "EMPTY": empty++; break;
                }
                filesChecked += decided.SourceFiles;
                bytesChecked += decided.SourceBytes;
            }
            try
            {
                detail.Report(new VerifyProgressSnapshot(
                    _ctx.JobId, decidedObjects, plan.Objects.Count, objectId, objectPath, stage,
                    filesChecked, bytesChecked, ok, mismatch, unknown, empty,
                    detailClock.Elapsed.TotalMilliseconds, DateTime.UtcNow));
            }
            catch (Exception ex)
            {
                // ★ 纪律 ★ 进度通道是"只读投影"：界面端出问题绝不能把验证本身拖下水（同 P0-06 兜底口径）。
                _log.Debug(ex, "验证进度快照投递失败（不影响验证结论）");
            }
        }

        foreach (var obj in plan.Objects)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"验证 {obj.ObjectId} …");
            seenObjects++;
            ReportDetail(obj.ObjectId, obj.SourcePath, "start", seenObjects);

            var result = new ObjectVerifyResult { ObjectId = obj.ObjectId };
            var objContext = _ctx.Diagnostics.WithObject(obj.ObjectId);
            // 目标侧先枚举（清单用于快速比对）；源侧只有在目标侧没溢出时才收集清单
            PublishVerify(VerifyEvents.TargetStatStarted, new VrfSideStatPayload("target", 0, 0, false, 0, 0),
                DiagnosticLevel.Debug, DiagnosticOutcome.Accepted, objContext);
            var dstSw = System.Diagnostics.Stopwatch.StartNew();
            SideStat dstStat;
            try
            {
                dstStat = await Task.Run(() => EnumerateSide(obj.TargetPath, obj, exclDirs, exclFileMatch, skipPlaceholders, true, "target", ct), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PublishVerify(VerifyEvents.TargetStatFailed, new VrfFailurePayload("target-stat", ex.GetType().Name, 1),
                    DiagnosticLevel.Warning, DiagnosticOutcome.Failed, objContext);
                throw;
            }
            dstSw.Stop();
            targetStatMs += dstSw.Elapsed.TotalMilliseconds;
            PublishSideStat(VerifyEvents.TargetStatSucceeded, "target", dstStat, dstSw.ElapsedMilliseconds, objContext);

            PublishVerify(VerifyEvents.SourceStatStarted, new VrfSideStatPayload("source", 0, 0, false, 0, 0),
                DiagnosticLevel.Debug, DiagnosticOutcome.Accepted, objContext);
            var srcSw = System.Diagnostics.Stopwatch.StartNew();
            SideStat srcStat;
            try
            {
                srcStat = await Task.Run(() => EnumerateSide(obj.SourcePath, obj, exclDirs, exclFileMatch, skipPlaceholders, !dstStat.Overflow, "source", ct), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PublishVerify(VerifyEvents.SourceStatFailed, new VrfFailurePayload("source-stat", ex.GetType().Name, 1),
                    DiagnosticLevel.Warning, DiagnosticOutcome.Failed, objContext);
                throw;
            }
            srcSw.Stop();
            sourceStatMs += srcSw.Elapsed.TotalMilliseconds;
            PublishSideStat(VerifyEvents.SourceStatSucceeded, "source", srcStat, srcSw.ElapsedMilliseconds, objContext);

            evidenceEnumerationErrors += srcStat.EnumerationErrors + dstStat.EnumerationErrors;
            evidenceAnyOverflow |= srcStat.Overflow || dstStat.Overflow;

            result.SourceFiles = srcStat.Files; result.SourceBytes = srcStat.Bytes;
            result.TargetFiles = dstStat.Files; result.TargetBytes = dstStat.Bytes;
            result.CountMatch = srcStat.Files == dstStat.Files;
            result.BytesMatch = srcStat.Bytes == dstStat.Bytes;

            // ★ P0-02 ★ 先记录"这次到底核对到了什么"，再决定结论。
            result.PlannedFiles = obj.EstimatedFiles;
            result.PlannedBytes = obj.EstimatedBytes;
            result.EnumerationSkipped = srcStat.EnumerationErrors + dstStat.EnumerationErrors;
            // ★ 2026-10-11 ★ 枚举覆盖状态要**可报告**：跳过过任何条目 ⇒ incomplete，
            //   此时该对象既不能说通过、也不能把没出现的文件说成真实缺失（用户执行令 §数据契约 2）。
            result.EnumerationStatus = result.EnumerationSkipped == 0 ? "complete" : "incomplete";
            MergeEnumerationFailures(report, srcStat);
            MergeEnumerationFailures(report, dstStat);
            result.SourceUnavailable = !srcStat.RootExists;
            result.TargetUnavailable = !dstStat.RootExists && srcStat.Files > 0;
            result.BothSidesEmpty = srcStat.Files == 0 && dstStat.Files == 0
                                    && srcStat.Bytes == 0 && dstStat.Bytes == 0;

            var samplePercent = Math.Clamp(_ctx.Definition.Options.SampleHashPercent, 1, 100);
            List<(string Relative, long Length)> hashSamples;

            var compareSw = System.Diagnostics.Stopwatch.StartNew();
            if (!srcStat.Overflow && !dstStat.Overflow)
            {
                // 清单模式：内存内比对（目标侧用 HashSet，O(n) 不 O(n²)）
                var dstSet = new HashSet<string>(dstStat.RelativePaths, StringComparer.OrdinalIgnoreCase);
                var srcSet = new HashSet<string>(srcStat.RelativePaths, StringComparer.OrdinalIgnoreCase);
                var srcLen = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var (rel, len) in srcStat.FilesForHash) srcLen[rel] = len;
                var dstLen = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var (rel, len) in dstStat.FilesForHash) dstLen[rel] = len;

                foreach (var rel in srcStat.RelativePaths)
                    if (!dstSet.Contains(rel))
                    {
                        if (result.MissingSamples.Count < MaxMissingSamples) result.MissingSamples.Add(rel);
                        // ★ 2026-10-11 ★ 文件级差异证据：确认缺失（目标侧不存在 ⇒ -1，不是 0 字节）。
                        AddFileDiff(report, obj, "missing-on-target", rel,
                            srcLen.TryGetValue(rel, out var sl0) ? sl0 : -1, -1,
                            "L1 枚举比较：源侧有、目标侧没有");
                    }

                // ★ P0-02 ★ 目标多出来的文件必须被单独陈述为「目标额外」，不能混进「缺失」，
                //   否则现场 10/12 那类「源 10、目标 12、MissingSamples 为空」只能显示成一句矛盾的 MISMATCH。
                foreach (var rel in dstStat.RelativePaths)
                    if (!srcSet.Contains(rel))
                    {
                        if (result.ExtraOnTarget.Count < MaxMissingSamples) result.ExtraOnTarget.Add(rel);
                        AddFileDiff(report, obj, "extra-on-target", rel, -1,
                            dstLen.TryGetValue(rel, out var dl0) ? dl0 : -1,
                            "L1 枚举比较：目标侧有、源侧没有（本工具不自动清理）");
                    }

                // ★ P0-02 ★ 数量与总字节都相同、内容/长度却不同的兜底：逐相对路径比长度。
                foreach (var (rel, len) in dstStat.FilesForHash)
                {
                    if (!srcLen.TryGetValue(rel, out var sl) || sl == len) continue;
                    result.SizeMismatched++;
                    if (result.SizeMismatchSamples.Count < MaxMissingSamples)
                        result.SizeMismatchSamples.Add($"{rel}（源 {sl} / 目标 {len}）");
                    AddFileDiff(report, obj, "size-mismatch", rel, sl, len,
                        "长度比较：两侧都存在但字节数不同");
                }

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
                hashSamples = await Task.Run(() => StreamSourcePass(obj, result, report, samplePercent, exclDirs, exclFileMatch, skipPlaceholders, ct), ct);

                PublishVerify(VerifyEvents.SampleSelected,
                    new VrfHashSamplePayload("stream", -1, hashSamples.Count, samplePercent),
                    DiagnosticLevel.Debug, DiagnosticOutcome.Succeeded, objContext);
            }
            compareSw.Stop();
            compareMs += compareSw.Elapsed.TotalMilliseconds;

            report.SampleHashPercent = samplePercent;   // 本轮**实际生效**的抽样百分比（L1 也会记录配置值，报告注明未执行）
            if (level >= VerifyLevel.L2_SampleHash)
            {
                report.HashAlgorithm = "SHA-256";
                PublishVerify(VerifyEvents.HashStarted,
                    new VrfHashSamplePayload("samples", hashSamples.Count, hashSamples.Count, samplePercent),
                    DiagnosticLevel.Debug, DiagnosticOutcome.Accepted, objContext);

                var hashFailed = 0L;
                var hashSw = System.Diagnostics.Stopwatch.StartNew();
                foreach (var f in hashSamples)
                {
                    ct.ThrowIfCancellationRequested();
                    var srcFile = Path.Combine(obj.SourcePath, f.Relative);
                    var dstFile = Path.Combine(obj.TargetPath, f.Relative);
                    result.HashSampled++;
                    try
                    {
                        if (!File.Exists(dstFile))
                        {
                            result.HashMismatched++;
                            // ★ 2026-10-11 ★ 抽样时才发现目标侧不存在：这是**真的抽样过**的文件级证据。
                            AddFileDiff(report, obj, "missing-on-target", f.Relative, f.Length, -1,
                                "L2 抽样：目标侧不存在");
                            continue;
                        }
                        var h1 = await HashAsync(srcFile, ct);
                        var h2 = await HashAsync(dstFile, ct);
                        if (!string.Equals(h1, h2, StringComparison.OrdinalIgnoreCase))
                        {
                            result.HashMismatched++;
                            AddFileDiff(report, obj, "hash-mismatch", f.Relative, f.Length, f.Length,
                                "SHA-256 双向核对", detail: $"源 {h1} / 目标 {h2}");
                        }
                    }
                    catch (Exception ex)
                    {
                        hashFailed++;
                        _log.Warning(ex, "哈希失败 {File}", srcFile);
                        // ★ 哈希本身失败 ≠ mismatch ★ 先如实记一条"读不出来"的文件级证据（含 Win32 码）。
                        AddFileDiff(report, obj, "unreadable", f.Relative, f.Length, f.Length,
                            "SHA-256 读取失败（该文件没有内容级结论）",
                            win32: $"Win32 {ex.HResult & 0xFFFF}", detail: ex.Message);
                        // ★ 哈希本身失败 ≠ mismatch ★ 现有业务口径不变，只把事实记下来。
                        PublishVerify(VerifyEvents.HashFailed,
                            new VrfFailurePayload("hash", ex.GetType().Name, 1),
                            DiagnosticLevel.Warning, DiagnosticOutcome.Failed, objContext);
                    }
                }
                hashSw.Stop();
                hashMs += hashSw.Elapsed.TotalMilliseconds;
                hashSampleCount += result.HashSampled;

                PublishVerify(VerifyEvents.HashSucceeded,
                    new VrfHashResultPayload(result.HashSampled, result.HashMismatched, hashFailed),
                    DiagnosticLevel.Debug, DiagnosticOutcome.Succeeded, objContext);

                result.HashReadFailed = hashFailed;

                if (result.HashMismatched > 0)
                    PublishVerify(VerifyEvents.Mismatch,
                        new VrfMismatchPayload("hash-mismatch", result.HashMismatched,
                            result.MissingSamples.Count > 0 ? null : null),
                        DiagnosticLevel.Warning, DiagnosticOutcome.Failed, objContext);
            }

            // 缺文件（清单/流式两种模式都会收集）与计数不一致也各记一条（只陈述事实）。
            // ★ F-1（2026-10-13）★ missing-on-target **不在**这里发：本对象的结论还没定，
            //   "这些路径到底算不算确证缺失"要等四态判定之后才知道（见下方 MissingConfirmed 门控）。
            if (srcStat.Files != dstStat.Files)
                PublishVerify(VerifyEvents.Mismatch,
                    new VrfMismatchPayload("count-mismatch", Math.Abs(srcStat.Files - dstStat.Files), null),
                    DiagnosticLevel.Warning, DiagnosticOutcome.Failed, objContext);
            if (srcStat.Bytes != dstStat.Bytes)
                PublishVerify(VerifyEvents.Mismatch,
                    new VrfMismatchPayload("bytes-mismatch", Math.Abs(srcStat.Bytes - dstStat.Bytes), null),
                    DiagnosticLevel.Warning, DiagnosticOutcome.Failed, objContext);

            if (result.ExtraOnTarget.Count > 0)
                PublishVerify(VerifyEvents.Mismatch,
                    new VrfMismatchPayload("extra-on-target", result.ExtraOnTarget.Count, null),
                    DiagnosticLevel.Warning, DiagnosticOutcome.Failed, objContext);
            if (result.SizeMismatched > 0)
                PublishVerify(VerifyEvents.Mismatch,
                    new VrfMismatchPayload("size-mismatch", result.SizeMismatched, null),
                    DiagnosticLevel.Warning, DiagnosticOutcome.Failed, objContext);

            // ═══════════ ★ P0-02 ★ 结论四态判定（顺序即优先级） ═══════════
            //   不能确认  >  确认差异  >  通过。旧实现在这里只有一句话：
            //   `Status => CountMatch && BytesMatch && HashMismatched == 0 ? "OK" : "MISMATCH"`，
            //   于是「源不可达」「枚举被跳过」「两边 0/0」全部塌缩成 OK 或被说成缺失。
            var anyDiff = !result.CountMatch || !result.BytesMatch
                          || result.HashMismatched > 0
                          || result.MissingSamples.Count > 0
                          || result.ExtraOnTarget.Count > 0
                          || result.SizeMismatched > 0;
            var planExpectsData = obj.EstimatedFiles > 0 || obj.EstimatedBytes > 0;

            if (result.SourceUnavailable)
            {
                result.Status = "UNKNOWN"; result.UnknownReason = "source-unavailable";
            }
            else if (result.TargetUnavailable)
            {
                result.Status = "UNKNOWN"; result.UnknownReason = "target-unavailable";
            }
            else if (result.BothSidesEmpty && planExpectsData)
            {
                // 计划里有数据，实测两侧都是 0 ⇒ 证据不完整（旧实现返回 OK，是"0/0 也是通过"的入口）
                result.Status = "UNKNOWN"; result.UnknownReason = "both-sides-empty-against-plan";
            }
            else if (result.EnumerationSkipped > 0)
            {
                // 权限/竞态/瞬态 IO 跳过了条目 ⇒ 统计不完整，不得声称通过
                result.Status = "UNKNOWN"; result.UnknownReason = "enumeration-incomplete";
            }
            else if (result.BothSidesEmpty)
            {
                // 计划本身就预期为空：合法，且不冒充"核对过内容"
                result.Status = "EMPTY";
            }
            else if (anyDiff)
            {
                result.Status = "MISMATCH";
            }
            else if (result.HashReadFailed > 0)
            {
                // ★ E-1（2026-10-13 定点修复）★ 内容级核对被要求了、但目标/源文件**读不出来**
                //   （`diffKind=unreadable`，Win32 32/33 等）⇒ 证据不完整，**绝不允许判 OK**：
                //   真机证据：目标文件无法读取时对象仍为 OK、`overallPass=true`、退出码 0 —— 那是
                //   "无法核实内容"被当成了"验证通过"。这里与"哈希不一致（MISMATCH）"严格区分开。
                result.Status = "UNKNOWN"; result.UnknownReason = "hash-unreadable";
            }
            else
            {
                result.Status = "OK";
            }

            // ★ F-1 ★ MissingSamples 只在"两侧都读到了、确实比对出差异（MISMATCH）"时才算**确证缺失**；
            //   UNKNOWN 场景下它们只是"读不到那一侧"的副产物，不得被任何出口说成"缺失"。
            result.MissingConfirmed = result.Status == "MISMATCH" && result.MissingSamples.Count > 0;

            // ★ F-1 ★ 结论已定 ⇒ 这时才允许把"缺文件"作为**确证缺失证据**发出去
            //   （对象是 UNKNOWN 时同样会把源侧文件记进 MissingSamples：把它们发成"缺失"就是假缺失的来源）。
            if (result.MissingConfirmed)
                PublishVerify(VerifyEvents.Mismatch,
                    new VrfMismatchPayload("missing-on-target", result.MissingSamples.Count, null),
                    DiagnosticLevel.Warning, DiagnosticOutcome.Failed, objContext);

            // ★ P0-01(4) / P0-03 ★ 只有**确认差异**才允许进入有破坏性副作用的修复分支。
            //   UNKNOWN / EMPTY 一律不放行：现场 27.44 GB 与 869 B 的目标文件正是被"假缺失"删掉的。
            result.AllowsDestructiveRepair = result.Status == "MISMATCH";

            if (result.Status == "UNKNOWN")
            {
                PublishVerify(VerifyEvents.Mismatch,
                    new VrfMismatchPayload("unverifiable:" + result.UnknownReason, 1, null),
                    DiagnosticLevel.Warning, DiagnosticOutcome.Unknown, objContext);
            }
            else if (result.Status == "EMPTY")
            {
                PublishVerify(VerifyEvents.Mismatch,
                    new VrfMismatchPayload("empty-both-sides-planned-empty", 0, null),
                    DiagnosticLevel.Information, DiagnosticOutcome.Unknown, objContext);
            }

            report.Objects.Add(result);
            // ★ P1-12 ★ 结论已出 ⇒ 发一条 done 快照（计数来自刚加入的真实结果，ObjectIndex = 已出结论数）
            ReportDetail(obj.ObjectId, obj.SourcePath, "done", report.Objects.Count);
            _log.Information("验证 {Id}: {Status}（文件 {SF}/{TF}, 字节 {SB}/{TB}{Hash}{Extra}{Unknown}）",
                obj.ObjectId, result.Status, result.SourceFiles, result.TargetFiles,
                Format.Bytes(result.SourceBytes), Format.Bytes(result.TargetBytes),
                level >= VerifyLevel.L2_SampleHash ? $", 哈希抽样 {result.HashSampled} 项, 不一致 {result.HashMismatched}" : "",
                result.ExtraOnTarget.Count > 0 ? $", 目标额外 {result.ExtraOnTarget.Count} 项" : "",
                result.UnknownReason != null ? $", 原因 {result.UnknownReason}" : "");
        }

        // ★ P0-02 ★ 只有 OK 与「计划与实测都为空」的 EMPTY 计入通过；
        //   UNKNOWN（源/目标不可达、枚举被跳过、与计划矛盾的 0/0）绝不算通过（G2）。
        report.OverallPass = report.Objects.All(o => o.Status is "OK" or "EMPTY");
        var unknownCount = report.Objects.Count(o => o.Status == "UNKNOWN");
        var emptyCount = report.Objects.Count(o => o.Status == "EMPTY");

        // ★ D6.1 §16 观察点 ★ 完成统计 + **统计完整性**：
        //   "0/0 也是 OK" 这类形态必须能被诊断单独指出（业务 OverallPass 不动）。
        //   ★ 2026-10-11 ★ 这段必须发生在 `_ctx.SaveVerify(report)` **之前**：验证报告要如实写
        //   "这份统计有多完整"，落盘之后再算就永远写不进报告里（用户执行令 §02/§08）。
        var statsCompleteness = evidenceEnumerationErrors == 0 && !evidenceAnyOverflow ? "Complete" : "Partial";
        var note = evidenceEnumerationErrors > 0
            ? $"枚举期有 {evidenceEnumerationErrors} 个条目被跳过（权限/竞态等），统计可能不完整"
            : evidenceAnyOverflow ? "部分对象文件量超清单上限，已切流式核对（结果仍精确，统计口径不同）"
            : "枚举期无跳过、无流式降级";
        // ★ P0-02 ★ 把"无法确认"的对象单独点出来，别让它在总数里消失
        if (unknownCount > 0) statsCompleteness = "Partial";
        if (unknownCount > 0 || emptyCount > 0)
            note += $"；无法确认 {unknownCount} 个对象，计划与实测均为空 {emptyCount} 个对象";

        // ★ 2026-10-11（验证报告返修）★ 把"这一轮到底发生了什么"写进报告本身：
        //   统计完整性、真实耗时、阶段表（累计值，不估算）、证据截断情况。
        //   全部来自本轮实测；任何"没采集到"的东西一律留空 ⇒ 报告写"未采集"，绝不编造。
        report.EvidenceCompleteness = statsCompleteness;
        report.EvidenceNote = note;
        if (report.FileDiffsTruncated)
            report.EvidenceNote += $"；文件级差异只记录了前 {MaxFileDiffRecords} 条（完整数量见逐对象计数字段）";
        if (report.EnumerationFailuresTruncated)
            report.EvidenceNote += $"；枚举跳过明细只记录了前 {MaxEnumerationFailureSamples} 条";
        report.FinishedUtc = DateTime.UtcNow;
        runClock.Stop();
        report.ElapsedMs = runClock.Elapsed.TotalMilliseconds;
        report.RunStatus = "completed";
        report.Stages.Clear();
        report.Stages.Add(new VerifyStageRecord
        {
            Stage = "目标侧枚举（文件数/字节数/清单）", ElapsedMs = targetStatMs,
            CoveredObjects = report.Objects.Count, Note = "按对象累计真实耗时",
        });
        report.Stages.Add(new VerifyStageRecord
        {
            Stage = "源侧枚举（文件数/字节数/清单）", ElapsedMs = sourceStatMs,
            CoveredObjects = report.Objects.Count, Note = "按对象累计真实耗时",
        });
        report.Stages.Add(new VerifyStageRecord
        {
            Stage = "计数/字节对账与差异分类", ElapsedMs = compareMs,
            CoveredObjects = report.Objects.Count, Note = "按对象累计真实耗时（含超大对象的流式核对）",
        });
        report.Stages.Add(new VerifyStageRecord
        {
            Stage = "内容级抽样与 SHA-256", ElapsedMs = hashMs, CoveredObjects = hashSampleCount,
            Note = level >= VerifyLevel.L2_SampleHash
                ? $"抽样 {report.SampleHashPercent}%（按对象累计真实耗时；覆盖率见逐对象明细）"
                : "本轮为 L1 只对账数量/字节，**未执行**内容级抽样",
        });

        _ctx.SaveVerify(report);

        // ★ R2 接手修复 · 缺陷 A ★ 校验结论回流 job-state：
        //   在此之前 MissingObjects 从未被生产代码赋值，导致
        //   "迁移报成功、校验发现目标少文件" 依然会被判成可信完成。
        //   顺序必须在 SaveVerify 之后 —— 先有落盘证据，再有状态变更。
        VerifyReconciliation.Apply(_ctx, report);

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

        // ★ P0-02 ★ 日志里也不能把"没核对成"写成"存在不一致"：现场用日志复盘时，
        //   这两种情形的处置方式完全不同（一个去查差异、一个先恢复源/目标的可达性）。
        _log.Information("验证结束: {Result}（差异 {Mismatch} 个对象 / 无法确认 {Unknown} 个对象 / 无可核对数据 {Empty} 个对象）",
            report.OverallPass ? "全部通过" : "未通过",
            report.Objects.Count(o => o.Status == "MISMATCH"),
            report.Objects.Count(o => o.Status == "UNKNOWN"),
            report.Objects.Count(o => o.Status == "EMPTY"));
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
        /// ★ P0-02 ★ 被枚举的那一侧「根路径是否真的存在/可达」。
        /// 0 文件 0 字节有两种完全不同的成因：真的空目录，和**根本没能访问**。
        /// 旧实现两者都算 OK，于是「源 14 / 目标 0」之外还留下「0/0 也是通过」的假阳性通道。
        /// </summary>
        public bool RootExists;

        /// <summary>
        /// ★ D6.1 §16 ★ 枚举期被跳过的条目数（权限/竞态/瞬态 IO）。
        /// 只**计数**：既不改遍历逻辑，也不改变任何跳过决策；但没有它，
        /// "全部 OK" 就无法与"其实很多项根本没枚举到"区分。
        /// </summary>
        public long EnumerationErrors;

        /// <summary>
        /// ★ 2026-10-11（验证报告返修）★ 枚举期被跳过条目的**有界明细**（侧别/路径/类型/错误文本/时刻）。
        /// 旧实现只有计数 ⇒ 报告只能说"跳过了 3 个"，说不出"跳过了哪三个"。
        /// </summary>
        public List<EnumerationFailure> Failures { get; } = new();

        public List<string> RelativePaths { get; } = new();
        public List<(string Relative, long Length)> FilesForHash { get; } = new();
    }

    /// <summary>
    /// 把某一侧枚举期的跳过明细并入报告（有界；超限只置截断标志，绝不无界增长）。
    /// </summary>
    private static void MergeEnumerationFailures(VerifyReport report, SideStat stat)
    {
        foreach (var f in stat.Failures)
        {
            if (report.EnumerationFailures.Count >= MaxEnumerationFailureSamples)
            {
                report.EnumerationFailuresTruncated = true;
                return;
            }
            report.EnumerationFailures.Add(f);
        }
    }

    /// <summary>
    /// ★ 2026-10-11（验证报告返修）★ 记录一条**文件级差异**的完整证据（有界）。
    /// 只有引擎真的逐文件比对过才调用：相对路径、两侧绝对路径、两侧真实长度（-1 = 该侧不存在，
    /// 与"0 字节"严格区分）、证据来源，以及确实拿到的 Win32 错误码。
    /// </summary>
    private static void AddFileDiff(VerifyReport report, PlannedObject obj, string kind, string relative,
        long sourceLength, long targetLength, string evidence, string? win32 = null, string? detail = null)
    {
        if (report.FileDiffs.Count >= MaxFileDiffRecords)
        {
            report.FileDiffsTruncated = true;
            return;
        }
        report.FileDiffs.Add(new FileDiffRecord
        {
            ObjectId = obj.ObjectId,
            DiffKind = kind,
            RelativePath = relative,
            SourcePath = SafeCombine(obj.SourcePath, relative),
            TargetPath = SafeCombine(obj.TargetPath, relative),
            SourceLength = sourceLength,
            TargetLength = targetLength,
            Evidence = evidence,
            Win32Code = win32,
            Detail = detail,
        });
    }

    /// <summary>拼路径但不因畸形相对路径抛异常（报告生成绝不能因为一条脏路径整份失败）。</summary>
    private static string SafeCombine(string root, string relative)
    {
        try { return Path.Combine(root, relative); }
        catch { return root + "\\" + relative; }
    }

    private SideStat EnumerateSide(string root, PlannedObject obj,
        HashSet<string> exclDirs, Func<string, bool> exclFileMatch, bool skipPlaceholders, bool collectLists,
        string side, CancellationToken ct = default)
    {
        var stat = new SideStat();

        // ★ 2026-10-11 ★ 记录一条"这里被跳过了"的明细（有界）。只记录，不改变任何跳过决策。
        void Skipped(string path, string kind, Exception? ex)
        {
            stat.EnumerationErrors++;
            if (stat.Failures.Count >= MaxEnumerationFailureSamples) return;
            var win32 = ex is null ? null : $"Win32 {ex.HResult & 0xFFFF}";
            stat.Failures.Add(new EnumerationFailure
            {
                Side = side,
                Path = path,
                Kind = kind,
                Message = (ex is null ? "" : ex.Message) + (win32 is null ? "" : $"（{win32}）"),
                AtUtc = DateTime.UtcNow,
            });
        }

        // 文件清单对象：只核对清单内的文件（策略排除名依然生效）
        if (obj.FileList != null)
        {
            try { stat.RootExists = Directory.Exists(root); } catch { stat.RootExists = false; }
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
                catch (Exception ex) { Skipped(path, "file", ex); }   // ★ D6.1 §16 ★ 只计数，不改跳过决策
            }
            return stat;
        }

        try { stat.RootExists = Directory.Exists(root); } catch { stat.RootExists = false; }
        if (!stat.RootExists) return stat;

        // 迭代遍历（显式栈）：目录深度无递归栈限制
        var pending = new Stack<string>();
        pending.Push(root);
        long entrySeen = 0;   // ★ 2026-10-12 ★ 目录内条目计数，用于有界的取消检查
        while (pending.Count > 0)
        {
            // ★ P0-02(4)：枚举本身也要能被取消（旧实现只在对象之间检查 ct，单个巨型对象不可中断）
            if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos(); }
            catch (Exception ex) { Skipped(dir, "directory", ex); continue; }   // ★ D6.1 §16 ★ 只计数（目录级跳过）
            foreach (var e in entries)
            {
                // ★ 2026-10-12 ★ 巨型单目录也要能被取消：实测 800,000 个文件的平铺目录，
                // 旧实现在目录循环之间才检查 ct ⇒ 点「取消验证」要等整个目录枚举完（约 4.4 s）才生效。
                if (ShouldCheckCancel(++entrySeen)) ct.ThrowIfCancellationRequested();
                try
                {
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (e is FileInfo fi)
                    {
                        if (exclFileMatch(fi.Name)) continue;
                        // ★ v0.5.4 Preview.2（2026-10-16 真机 JOB-20261016-150319-e738）★
                        //   「强制覆盖」隔离出来的旧副本 <原名>.pcmig-old-<时间戳> 留在**原目录**里，
                        //   它既不是源侧数据、也不是目标侧"多出来的用户文件"。若不排除，校验会把
                        //   PCMig 自己产生的隔离物报成 "extra-on-target 多余文件"，制造"目标有脏数据"
                        //   的假象（真机 object-000010 的两趟 attempt 恰好各有 9 个这样的副本）。
                        //   两侧都排除：源侧理论上不会出现这种名字，但口径必须对称，不留单侧漏洞。
                        if (RepairQuarantine.IsQuarantinedOldCopy(fi.Name)) continue;
                        if (skipPlaceholders && ((fi.Attributes & DirStat.CloudPlaceholderAttr) == DirStat.CloudPlaceholderAttr
                            || (fi.Attributes & FileAttributes.Offline) == FileAttributes.Offline)) continue;
                        // RootFiles 对象只统计根层文件（★ P0-01：统一经 PathNorm 比较，卷根 D:\ 不再被裁成 D:）
                        if (!PathNorm.BelongsToObjectRoot(obj, root, fi.FullName))
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
                catch (Exception ex) { Skipped(e.FullName, e is DirectoryInfo ? "directory" : "file", ex); }      // ★ D6.1 §16 ★ 只计数，不改跳过决策
            }
        }
        return stat;
    }

    /// <summary>流式过源：逐文件核对目标存在性（缺失样例）+ 确定性收集哈希样本，全程不存全量清单。</summary>
    private List<(string Relative, long Length)> StreamSourcePass(PlannedObject obj, ObjectVerifyResult result,
        VerifyReport report, int samplePercent, HashSet<string> exclDirs, Func<string, bool> exclFileMatch,
        bool skipPlaceholders, CancellationToken ct)
    {
        var samples = new List<(string Relative, long Length)>();
        var pending = new Stack<string>();
        pending.Push(obj.SourcePath);
        long entrySeen = 0;   // ★ 2026-10-12 ★ 目录内条目计数，用于有界的取消检查
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos(); }
            catch (Exception ex)
            {
                // ★ 2026-10-11 ★ 流式模式也要能说"目录没枚举到"（旧实现静默 continue）。
                result.EnumerationSkipped++;
                if (report.EnumerationFailures.Count < MaxEnumerationFailureSamples)
                    report.EnumerationFailures.Add(new EnumerationFailure
                    {
                        Side = "source", Path = dir, Kind = "directory",
                        Message = ex.Message + $"（Win32 {ex.HResult & 0xFFFF}）", AtUtc = DateTime.UtcNow,
                    });
                else report.EnumerationFailuresTruncated = true;
                continue;
            }
            foreach (var e in entries)
            {
                // ★ 2026-10-12 ★ 与计数遍同一口径：目录内每 CancelCheckInterval 个条目检查一次取消。
                if (ShouldCheckCancel(++entrySeen)) ct.ThrowIfCancellationRequested();
                try
                {
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (e is FileInfo fi)
                    {
                        if (exclFileMatch(fi.Name)) continue;
                        // ★ v0.5.4 Preview.2（2026-10-16 真机 JOB-20261016-150319-e738）★
                        //   「强制覆盖」隔离出来的旧副本 <原名>.pcmig-old-<时间戳> 留在**原目录**里，
                        //   它既不是源侧数据、也不是目标侧"多出来的用户文件"。若不排除，校验会把
                        //   PCMig 自己产生的隔离物报成 "extra-on-target 多余文件"，制造"目标有脏数据"
                        //   的假象（真机 object-000010 的两趟 attempt 恰好各有 9 个这样的副本）。
                        //   两侧都排除：源侧理论上不会出现这种名字，但口径必须对称，不留单侧漏洞。
                        if (RepairQuarantine.IsQuarantinedOldCopy(fi.Name)) continue;
                        if (skipPlaceholders && ((fi.Attributes & DirStat.CloudPlaceholderAttr) == DirStat.CloudPlaceholderAttr
                            || (fi.Attributes & FileAttributes.Offline) == FileAttributes.Offline)) continue;
                        if (!PathNorm.BelongsToObjectRoot(obj, obj.SourcePath, fi.FullName))
                            continue;
                        var rel = Path.GetRelativePath(obj.SourcePath, fi.FullName);
                        var dst = Path.Combine(obj.TargetPath, rel);
                        bool exists;
                        try { exists = File.Exists(dst); } catch { exists = false; }
                        if (!exists)
                        {
                            if (result.MissingSamples.Count < MaxMissingSamples)
                                result.MissingSamples.Add(rel);
                            // ★ 2026-10-11 ★ 流式模式同样是**真的逐文件查过** ⇒ 记完整差异证据。
                            AddFileDiff(report, obj, "missing-on-target", rel, fi.Length, -1,
                                "流式核对：源侧有、目标侧没有");
                        }
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
                catch (Exception ex)
                {
                    // ★ 2026-10-11 ★ 单文件失败不再静默：计数 + 有界明细。跳过决策本身不变。
                    result.EnumerationSkipped++;
                    if (report.EnumerationFailures.Count >= MaxEnumerationFailureSamples)
                        report.EnumerationFailuresTruncated = true;
                    else
                        report.EnumerationFailures.Add(new EnumerationFailure
                        {
                            Side = "source", Path = e.FullName,
                            Kind = e is DirectoryInfo ? "directory" : "file",
                            Message = ex.Message + $"（Win32 {ex.HResult & 0xFFFF}）", AtUtc = DateTime.UtcNow,
                        });
                }
            }
        }
        return samples;
    }

    /// <summary>
    /// 目录内取消检查的粒度（条目数）。
    /// ★ 2026-10-12 实测 ★ 平铺单目录 800,000 个文件时，枚举一个目录要 4.4 s；
    /// 旧实现在目录循环之间才检查 ct ⇒ 点「取消验证」要等整个目录枚举完才生效。
    /// 每 4096 个条目检查一次：对枚举吞吐无可见影响（一次 token 读），取消延迟降到毫秒级。
    /// </summary>
    internal const int CancelCheckInterval = 4096;

    /// <summary>目录内条目计数是否到了取消检查点（真值 = 计数非零且为粒度整数倍）。</summary>
    internal static bool ShouldCheckCancel(long entriesSeen) =>
        entriesSeen > 0 && entriesSeen % CancelCheckInterval == 0;

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
        // ★ P0-02(4)：只读校验不应因别的进程持有写句柄而误判「读取被拒」。
        //   FileShare.Read 会让「他人可写打开」的文件直接抛 IOException ⇒ 旧行为把它算进 hashFailed。
        //   改为 ReadWrite|Delete：我们只读，不改变任何共享语义；真正的锁（独占）仍会失败并被如实记录。
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1 << 20, true);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash);
    }
}
