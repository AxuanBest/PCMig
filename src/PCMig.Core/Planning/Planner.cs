using PCMig.Core.Diagnostics;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Native;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using Serilog;

namespace PCMig.Core.Planning;

/// <summary>
/// Planner：ObservedState + Matrix + Options → plan.json。
/// 职责：对象排序（DataVolume 优先）、目标路径映射、策略排除、大文件双通道决策。
/// </summary>
public sealed class Planner
{
    private readonly ILogger _log;
    public Planner(ILogger log) { _log = log.ForContext<Planner>(); }

    public MigrationPlan CreatePlan(JobDefinition job, ObservedState observed, MigrationMatrix matrix)
    {
        var plan = new MigrationPlan { JobId = job.JobId };
        var opt = job.Options;
        var threshold = (long)(matrix.LargeFileThresholdMB > 0 ? matrix.LargeFileThresholdMB : opt.LargeFileThresholdMB) * 1024 * 1024;

        // ★ D6.1 §16 观察点 ★ 计划产出的输入规模（只读既有输入，不做任何额外扫描）。
        CoreDiagnostics.PublishCore(
            PlanEvents.PlanRequested,
            new PlnPlanRequestedPayload(
                job.Sources.Count(s => s.Enabled), observed.Objects.Count,
                opt.SplitLargeFiles, job.CustomSelections.Count > 0),
            CoreDiagnostics.ContextFor("Planner", job.JobId),
            DiagnosticLevel.Information, DiagnosticOutcome.Accepted, "Planner");

        // ★ D6.1 §16 ★ 选择快照摘要（只报规模：源根/自定义选择/启用源/树勾选节点数）。
        var treeSelectedNodes = observed.Objects.Sum(o => o.FileList?.Count ?? 0);
        CoreDiagnostics.PublishCore(
            PlanEvents.SelectionSnapshot,
            new PlnSelectionPayload(job.Sources.Count, job.CustomSelections.Count,
                job.Sources.Count(s => s.Enabled), treeSelectedNodes),
            CoreDiagnostics.ContextFor("Planner", job.JobId),
            DiagnosticLevel.Information, DiagnosticOutcome.Succeeded, "Planner");
        var objectsWithoutSourceRoot = 0;
        var restartableObjects = 0;

        // 排序策略：先 DataVolume，后 UserProfile/AppState；同类内大对象在前（尽早开始大头传输）
        var ordered = observed.Objects
            .OrderBy(o => KindRank(o.Kind))
            .ThenByDescending(o => Math.Max(o.Bytes, 0))
            .ToList();

        foreach (var obj in ordered)
        {
            var name = obj.Kind == ObjectKind.RootFiles
                ? "(根目录文件)"
                : Path.GetFileName(obj.SourcePath.TrimEnd('\\', '/')) ?? obj.SourcePath;

            // 目标映射：保留源根的相对结构
            var sourceRoot = FindSourceRoot(job, obj.SourcePath);
            var relative = obj.Kind == ObjectKind.RootFiles || sourceRoot == null
                ? name
                : Path.GetRelativePath(sourceRoot, obj.SourcePath);

            // ★ P1-07(3) ★ 绝对路径混入保护：相对路径若带根（跨盘/跨共享，即源根不是它的祖先），
            //   Path.Combine 会**整体替换**目标根（例如 C:\Users\D970 直接逃出 TargetRoot）⇒ 退化为按名称映射。
            if (Path.IsPathRooted(relative)) relative = name;

            // ═══════════ ★ P1-07 ★ 目录选择的目标映射语义 ═══════════
            //   旧行为：一律按「源根相对结构」映射，于是选中 \\host\Users\d970 会得到
            //   C:\Users\D970\d970（多嵌一层同名目录）——现场 a632/be58 实测就是这一层 d970。
            //   新行为：**单个**自定义目录选择默认「内容直达」——所选文件夹的内部内容直接落在 TargetRoot。
            //   多选时不做内容直达（不同来源的同名文件会静默合并且不可辨识）。
            var contentDirect = !string.Equals(job.Options.SelectedFolderMapping,
                Models.MigrationOptions.SelectedFolderMappingKeepFolder, StringComparison.OrdinalIgnoreCase);
            var singleSelection = job.CustomSelections.Count == 1;
            var isSelectionItself = singleSelection
                && obj.Kind != ObjectKind.RootFiles
                && Util.PathNorm.SameDirectory(job.CustomSelections[0], obj.SourcePath);
            if (contentDirect && isSelectionItself) relative = ".";

            var target = obj.Kind == ObjectKind.RootFiles || relative == "."
                ? job.TargetRoot
                : Path.Combine(job.TargetRoot, relative);

            // ★ P1-07(3) ★ 映射保护：目标与源同一位置、或互相包含（自拷贝/路径穿越）时不得按原样执行。
            //   回退到「保留文件夹」映射并**留审计**；若回退后仍然重叠，只记录"需人工确认"，由
            //   P0-02 的四态结论兜底（绝不因为映射可疑就悄悄声称成功）。
            var mappingUnsafe = false;
            if (obj.Kind != ObjectKind.RootFiles && Util.PathNorm.Overlaps(target, obj.SourcePath))
            {
                var fallbackRelative = sourceRoot == null ? name : Path.GetRelativePath(sourceRoot, obj.SourcePath);
                var fallbackTarget = fallbackRelative == "." ? job.TargetRoot : Path.Combine(job.TargetRoot, fallbackRelative);
                if (!Util.PathNorm.Overlaps(fallbackTarget, obj.SourcePath))
                {
                    plan.ExcludedByPolicy.Add(
                        $"映射保护: {obj.ObjectId} 目标与源重叠，已回退为保留文件夹映射 -> {fallbackTarget}");
                    target = fallbackTarget;
                }
                else
                {
                    // 回退后仍重叠 ⇒ 本计划**不排这个对象**（改为人工确认），绝不排一个「自己拷自己」的执行项。
                    plan.ExcludedByPolicy.Add(
                        $"映射保护: {obj.ObjectId} 源与目标位置重叠，本计划不排此对象（需人工确认）: {obj.SourcePath}");
                    mappingUnsafe = true;
                }
            }
            if (mappingUnsafe) continue;

            plan.Objects.Add(new PlannedObject
            {
                ObjectId = obj.ObjectId,
                Kind = obj.Kind,
                SourcePath = obj.SourcePath,
                TargetPath = target,
                EstimatedBytes = Math.Max(obj.Bytes, 0),
                EstimatedFiles = Math.Max(obj.Files, 0),
                UseRestartablePass = opt.SplitLargeFiles && obj.HasLargeFiles,
                FileList = obj.FileList,
                // ★ 缺陷 B12b4 ★ 固化"计划时这份数据在哪"的文件系统级身份指纹（卷序列号 + 目录文件 ID）。
                // 之后每次开跑（含续传）都会重新取一次比对；共享被同名换底时指纹必然变化 ⇒ 拒绝续传。
                // 取不到就是 null（无基线，不参与校验），不会因为探测失败而阻断正常迁移。
                SourceIdentity = SourceIdentity.Capture(obj.SourcePath, _log)
            });

            // 只统计（不参与映射决策）：有多少对象定位不到源根、多少走 /Z 可续传通道。
            if (sourceRoot is null) objectsWithoutSourceRoot++;
            if (opt.SplitLargeFiles && obj.HasLargeFiles) restartableObjects++;
        }

        // ★ P1-07(1) ★ 多选/多源的可辨识映射：不同的源若映射到**同一个目标目录**，绝不静默合并/覆盖，
        //   各自加上可辨识的来源前缀（UNC ⇒ 主机_共享，本地盘 ⇒ 盘符），并在计划里留审计。
        DisambiguateCollidingTargets(plan, job);

        // 记录策略排除（写进 plan 供审计；真正执行由 robocopy /XD /XF 完成）
        plan.ExcludedByPolicy.AddRange(matrix.ExcludedDirectoryNames.Select(n => $"目录: {n}"));
        plan.ExcludedByPolicy.AddRange(matrix.ExcludedFileNames.Select(n => $"文件: {n}"));
        plan.ExcludedByPolicy.AddRange(matrix.SecurityBlockedFileNames.Select(n => $"凭据(永禁): {n}"));

        plan.TotalBytes = plan.Objects.Sum(o => o.EstimatedBytes);

        _log.Information("计划生成: {Objects} 个对象, 总计 {Bytes}, 大文件阈值 {Threshold}",
            plan.Objects.Count, Util.Format.Bytes(plan.TotalBytes), Util.Format.Bytes(threshold));

        // ★ D6.1 §16 观察点 ★ 产出摘要或"空计划"事实（业务返回值一字未改）。
        var planContext = CoreDiagnostics.ContextFor("Planner", job.JobId);
        if (plan.Objects.Count == 0)
        {
            CoreDiagnostics.PublishCore(
                PlanEvents.PlanEmpty,
                new PlnPlanEmptyPayload(
                    observed.Objects.Count, job.Sources.Count(s => s.Enabled),
                    observed.Objects.Count == 0 ? "no-observed-objects" : "all-objects-filtered"),
                planContext, DiagnosticLevel.Warning, DiagnosticOutcome.Skipped, "Planner");
        }
        else
        {
            CoreDiagnostics.PublishCore(
                PlanEvents.PlanCreated,
                new PlnPlanCreatedPayload(
                    plan.Objects.Count, plan.TotalBytes,
                    plan.Objects.Count == 0 ? 0 : plan.Objects.Max(o => o.EstimatedBytes),
                    plan.ExcludedByPolicy.Count, objectsWithoutSourceRoot, restartableObjects),
                planContext, DiagnosticLevel.Information, DiagnosticOutcome.Succeeded, "Planner");
        }

        return plan;
    }

    private static int KindRank(ObjectKind k) => k switch
    {
        ObjectKind.DataVolume => 0,
        ObjectKind.UserProfile => 1,
        ObjectKind.AppState => 2,
        ObjectKind.RootFiles => 3,
        _ => 9
    };

    /// <summary>
    /// ★ P1-07(1) ★ 目标目录撞车保护：**不同源**（SourcePath 不同）映射到同一个目标目录时，
    /// 直接执行会把两个来源的同名文件静默合并/覆盖，且事后无法分辨哪个文件来自哪里。
    /// 这里为撞车组各自加上来源前缀（UNC ⇒ 主机_共享，本地盘 ⇒ 盘符）并留下审计行。
    /// 同一个 SourcePath 的重复对象不算撞车（不改变语义）。
    /// </summary>
    private static void DisambiguateCollidingTargets(MigrationPlan plan, JobDefinition job)
    {
        // RootFiles（盘符/共享根层的散落文件）落在目标根本身是**设计如此**，不算"不同来源撞车"：
        // 它们与"选中文件夹的内容直达目标根"本来就要合在一起（用户就是这么选的）。
        // ★ D1-d / P1-08 ★ 但"根层散落文件"与"**另一个来源**的内容直达对象"同时落目标根时，
        //   同名文件会在目标根这一层静默互相覆盖，且事后无法分辨哪个文件来自哪里。
        //   ⇒ 保持**用户显式选择**的那个对象的内容直达映射不动，把**自动扫描得出**的散落文件
        //     落到目标根下带来源前缀的子目录里，并留审计行（谁都不丢，谁也不覆盖谁）。
        //   同一个来源根不算撞车（例如整盘对象 + 该根自己的散落文件），语义保持原样。
        var contentDirectRoots = plan.Objects
            .Where(o => o.Kind != ObjectKind.RootFiles && Util.PathNorm.SameDirectory(o.TargetPath, job.TargetRoot))
            .ToList();

        if (contentDirectRoots.Count > 0)
        {
            var rootFilesAtRoot = plan.Objects
                .Where(o => o.Kind == ObjectKind.RootFiles && Util.PathNorm.SameDirectory(o.TargetPath, job.TargetRoot))
                .ToList();

            foreach (var rf in rootFilesAtRoot)
            {
                var rfSource = Util.PathNorm.ForCompare(rf.SourcePath);
                var foreign = contentDirectRoots.FirstOrDefault(o =>
                    !string.Equals(Util.PathNorm.ForCompare(o.SourcePath), rfSource, StringComparison.OrdinalIgnoreCase));

                if (foreign is null) continue;   // 同源根 ⇒ 不是多源撞车

                var prefix = SourceRootLabel(FindSourceRoot(job, rf.SourcePath));
                var newTarget = Path.Combine(job.TargetRoot, prefix);
                plan.ExcludedByPolicy.Add(
                    $"映射保护: {rf.ObjectId} 根目录散落文件与另一来源的内容直达对象（{foreign.ObjectId}）" +
                    $"同落目标根，已按来源前缀 '{prefix}' 区分 -> {newTarget}");
                rf.TargetPath = newTarget;
            }
        }

        var colliding = plan.Objects
            .Where(o => o.Kind != ObjectKind.RootFiles)
            .GroupBy(o => Util.PathNorm.ForCompare(o.TargetPath), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => Util.PathNorm.ForCompare(x.SourcePath))
                         .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .ToList();

        foreach (var group in colliding)
        {
            foreach (var obj in group)
            {
                var prefix = SourceRootLabel(FindSourceRoot(job, obj.SourcePath));
                string relative;
                try { relative = Path.GetRelativePath(job.TargetRoot, obj.TargetPath); }
                catch { continue; }
                if (Path.IsPathRooted(relative))
                    relative = Path.GetFileName(obj.SourcePath.TrimEnd('\\', '/'));

                var newTarget = relative == "." || relative.Length == 0
                    ? Path.Combine(job.TargetRoot, prefix)
                    : Path.Combine(job.TargetRoot, prefix, relative);
                plan.ExcludedByPolicy.Add(
                    $"映射保护: {obj.ObjectId} 与其它来源撞到同一目标目录 {obj.TargetPath}，" +
                    $"已按来源前缀 '{prefix}' 区分 -> {newTarget}");
                obj.TargetPath = newTarget;
            }
        }
    }

    /// <summary>来源根的可辨识标签（只用于拼路径，故做非法字符净化）：UNC ⇒ 主机_共享，本地盘 ⇒ 盘符。</summary>
    private static string SourceRootLabel(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return "src";
        var r = Util.PathNorm.ForCompare(root);
        if (r.StartsWith("\\\\", StringComparison.Ordinal))
        {
            var parts = r.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) return Sanitize(parts[0] + "_" + parts[1]);
            if (parts.Length == 1) return Sanitize(parts[0]);
        }
        var leaf = Path.GetFileName(r);
        return Sanitize(leaf.Length > 0 ? leaf : r.Replace(":", ""));
    }

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length == 0 ? "src" : s;
    }

    private static string? FindSourceRoot(JobDefinition job, string objectPath)
    {
        // 对象路径 = 某源根的直接子路径；RootFiles 对象的 SourcePath 即源根本身
        foreach (var s in job.Sources.Where(x => x.Enabled))
        {
            if (objectPath.Equals(s.Path, StringComparison.OrdinalIgnoreCase)) return s.Path;
            if (objectPath.StartsWith(s.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return s.Path;
        }
        return null;
    }
}
