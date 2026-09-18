using PCMig.Core.Jobs;
using PCMig.Core.Models;
using PCMig.Core.State;

namespace PCMig.Core.Util;

/// <summary>
/// 扫描残缺闸门（v0.3.8，缺陷 4：扫描残缺静默通过）。
///
/// 生产事故：扫描期一次意外的网络 IOException 打掉了 Prepare，DirStat 只把 stat.Incomplete=true
/// 然后 continue，SourceScanner 只把它写进 ScanIncomplete 并在日志里拼一句"[部分不可访问×N]"，
/// 没有任何地方拦人 —— 结果是"少扫的数据既不在计划里、也不会被复制"，
/// 而事后所有报告都会显示"迁移完成"。
///
/// 现在的规则：扫描存在不可访问位置时，必须把"多少处、哪些路径、为什么"醒目报出，
/// 并且**默认不允许开始迁移**；要么修好后重扫，要么用户显式确认（CLI: --allow-incomplete-scan，
/// 界面：弹窗确认），确认结果落 job.json（allowIncompleteScan=true）供续传沿用与审计。
/// </summary>
public static class ScanGate
{
    /// <param name="Count">不可访问位置数量。</param>
    /// <param name="Paths">明细：对象 id｜路径｜原因。</param>
    /// <param name="Acknowledged">用户是否已显式确认"知风险仍继续"。</param>
    public sealed record Report(int Count, IReadOnlyList<string> Paths, bool Acknowledged)
    {
        public bool HasIncomplete => Count > 0;
        /// <summary>true = 存在残缺且未确认 → 必须拦住，不允许开始迁移。</summary>
        public bool Blocks => Count > 0 && !Acknowledged;
    }

    public static Report Inspect(ObservedState? observed, bool acknowledged)
    {
        if (observed == null) return new Report(0, Array.Empty<string>(), acknowledged);
        if (observed.InaccessiblePaths.Count > 0)
            return new Report(observed.InaccessiblePaths.Count, observed.InaccessiblePaths, acknowledged);
        // 兼容旧存档（v0.3.7 之前只置 ScanIncomplete、不记明细）：同样拦，但如实说明明细缺失
        var legacy = observed.Objects.Where(o => o.ScanIncomplete)
            .Select(o => $"{o.ObjectId}｜{o.SourcePath}｜部分目录/文件不可访问（旧存档未记录明细，建议重新扫描）")
            .ToList();
        return new Report(legacy.Count, legacy, acknowledged);
    }

    /// <summary>从任务存档里取扫描结果做判定（observed-state.json 缺失 = 没扫过，不拦）。</summary>
    public static Report Inspect(JobContext ctx)
        => Inspect(JsonStateStore.TryRead<ObservedState>(ctx.ObservedStatePath, out var o) ? o : null,
            ctx.Definition.AllowIncompleteScan);

    /// <summary>
    /// 统一文案（CLI 与界面同一口径）。cli=false 时结尾用"是否仍要继续"的问句（界面弹窗用），
    /// cli=true 时给出命令行恢复路径。
    /// </summary>
    public static string BuildBlockMessage(Report r, bool cli)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("扫描存在不可访问的目录/文件：共 ").Append(r.Count).Append(" 处。默认不允许在此状态下开始迁移。").AppendLine();
        sb.AppendLine();
        sb.AppendLine("为什么必须拦：这些位置里的文件既不在计划内、也不会被复制；");
        sb.AppendLine("事后报告只会显示“完成”，漏掉的数据要等用户自己发现（真实事故里就是这样漏了 78GB）。");
        sb.AppendLine();
        sb.AppendLine("具体位置（对象｜路径｜原因）：");
        foreach (var p in r.Paths.Take(12)) sb.Append("  · ").AppendLine(p);
        if (r.Count > 12) sb.Append("  … 其余 ").Append(r.Count - 12).AppendLine(" 处见任务目录的 observed-state.json");
        sb.AppendLine();
        if (cli)
        {
            sb.AppendLine("处理办法（二选一）：");
            sb.AppendLine("  ① 推荐：修好权限/网络后重新预检并扫描（pcmig new 或删除任务目录后重建）；");
            sb.AppendLine("  ② 确知风险仍要继续：加 --allow-incomplete-scan 重跑（会写入任务定义，续传不再拦）。");
        }
        else
        {
            sb.AppendLine("处理办法：[否] 先不迁移（推荐，修好权限/网络后重新「预检并生成计划」）；");
            sb.Append("[是] 我已知风险，仍要继续（会写入任务定义，后续续传不再拦）。");
        }
        return sb.ToString();
    }
}
