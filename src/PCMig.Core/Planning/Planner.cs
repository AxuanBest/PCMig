using PCMig.Core.Matrix;
using PCMig.Core.Models;
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
                FileList = obj.FileList
            });
        }

        // 记录策略排除（写进 plan 供审计；真正执行由 robocopy /XD /XF 完成）
        plan.ExcludedByPolicy.AddRange(matrix.ExcludedDirectoryNames.Select(n => $"目录: {n}"));
        plan.ExcludedByPolicy.AddRange(matrix.ExcludedFileNames.Select(n => $"文件: {n}"));
        plan.ExcludedByPolicy.AddRange(matrix.SecurityBlockedFileNames.Select(n => $"凭据(永禁): {n}"));

        plan.TotalBytes = plan.Objects.Sum(o => o.EstimatedBytes);

        _log.Information("计划生成: {Objects} 个对象, 总计 {Bytes}, 大文件阈值 {Threshold}",
            plan.Objects.Count, Util.Format.Bytes(plan.TotalBytes), Util.Format.Bytes(threshold));
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
