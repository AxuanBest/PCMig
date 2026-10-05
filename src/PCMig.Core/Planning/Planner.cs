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
            var target = obj.Kind == ObjectKind.RootFiles || relative == "."
                ? job.TargetRoot
                : Path.Combine(job.TargetRoot, relative);

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
