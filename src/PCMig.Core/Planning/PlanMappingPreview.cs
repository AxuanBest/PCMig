using PCMig.Core.Matrix;
using PCMig.Core.Models;

namespace PCMig.Core.Planning;

/// <summary>
/// ★ P1-07(2) ★ 计划评审用的「源绝对路径 → 最终目标绝对路径」预览。
///
/// 产品要求：在真正开始传输**之前**，至少要能让用户看清几组映射，尤其是
/// 用户资料目录、多个共享、RootFiles、不同驱动器、目标已存在同名文件、源与目标相同、子目录重名。
/// 这里的每一行都直接来自 plan.json 的 <see cref="PlannedObject.TargetPath"/>——
/// 也就是 Robocopy Dest / Verifier / Repair / Resume / Report 共用的那一份映射快照，不做二次推导。
/// </summary>
public sealed record MappingPreviewRow(
    string ObjectId,
    string Kind,
    long EstimatedFiles,
    long EstimatedBytes,
    string SourcePath,
    string TargetPath,
    string Note);

public static class PlanMappingPreview
{
    /// <summary>预览默认最多展示多少行（UI 只展示几组即可，清单本身仍完整保存在 plan.json）。</summary>
    public const int DefaultMaxRows = 40;

    /// <param name="plan">已生成的计划（唯一真值）。</param>
    /// <param name="targetRoot">目标根，仅用于判断"是否逃出目标根"这类提示。</param>
    /// <param name="probeTarget">
    /// 是否探测目标目录是否已存在（会在 UI 线程外做一次目录存在性检查）。
    /// 测试里默认关闭以保持确定性；界面显示时打开。
    /// </param>
    public static IReadOnlyList<MappingPreviewRow> Build(
        MigrationPlan plan, string targetRoot, bool probeTarget = false, int maxRows = DefaultMaxRows)
    {
        var rows = new List<MappingPreviewRow>();
        if (plan?.Objects is null) return rows;

        foreach (var o in plan.Objects.Take(Math.Max(1, maxRows)))
        {
            var notes = new List<string>();

            if (o.Kind == ObjectKind.RootFiles)
                notes.Add("盘符/共享根层的散落文件（只取第一层）");

            if (Util.PathNorm.Overlaps(o.TargetPath, o.SourcePath))
                notes.Add("源与目标位置重叠：本计划不执行该对象，需人工确认");

            // 来源撞车被加前缀、或映射被回退 —— 审计行里带着 ObjectId，直接抬到预览行上。
            var audit = plan.ExcludedByPolicy
                .FirstOrDefault(l => l.Contains(o.ObjectId, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(audit)) notes.Add(audit);

            if (!string.IsNullOrWhiteSpace(targetRoot)
                && !Util.PathNorm.SameDirectory(o.TargetPath, targetRoot)
                && !Util.PathNorm.IsInside(targetRoot, o.TargetPath))
                notes.Add("目标不在目标根之内");

            if (probeTarget)
            {
                try
                {
                    if (Directory.Exists(o.TargetPath)) notes.Add("目标目录已存在（合并写入，同名文件按 robocopy 规则处理）");
                }
                catch (Exception ex)
                {
                    notes.Add("目标存在性未知: " + ex.GetType().Name);
                }
            }

            rows.Add(new MappingPreviewRow(
                o.ObjectId, o.Kind.ToString(), o.EstimatedFiles, o.EstimatedBytes,
                o.SourcePath, o.TargetPath, string.Join("；", notes)));
        }

        return rows;
    }
}