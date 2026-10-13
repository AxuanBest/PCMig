using PCMig.Core.Jobs;
using PCMig.Core.Models;

namespace PCMig.Core.Transfer;

/// <summary>
/// ★ D1-c（P0-03 配套，2026-10-12）★ 强制修复的**报告级**证据闸门：
/// 确认"这份验证结论"真的属于本次修复、且够新——不只看对象级状态。
///
/// <see cref="RepairGate"/> 只回答"这个对象的结论是什么"；本类回答"这份报告能不能当本次修复的证据"：
///   · 报告必须存在（没有 ⇒ 挡；老 Job 没有 verify-report.json 一律挡）；
///   · 报告必须属于**本 Job**（JobId 对不上 ⇒ 挡，避免跨 Job 串证据）；
///   · 报告必须是**跑完的一轮**（canceled / failed / partial ⇒ 挡；引擎正常跑完才写 completed）；
///   · 报告必须**新于当前计划**（计划重新生成后又拿旧报告做强制修复 ⇒ 挡，要求先重新验证）；
///   · 产生报告的**构建版本**必须与当前运行版本一致（版本不同 ⇒ 挡，要求先重新验证）。
///
/// 判据只增不减、且**缺信息不放松**：任何一项说不清，都按"不许动目标数据"处理。
/// 但"旧报告没有这些字段"（null）不等于"结论相反"——这类字段缺失时交给 <see cref="RepairGate"/>
/// 的对象级判据继续把关，不在这里凭空造结论。
/// </summary>
internal static class RepairEvidenceGate
{
    /// <summary>本 Job 没有验证报告（尚未验证，或报告文件丢失/损坏）。</summary>
    public const string NoReportCode = "no-verify-report";

    /// <summary>报告记录的 JobId 与本 Job 不一致（拿错了证据）。</summary>
    public const string ForeignJobCode = "verify-report-other-job";

    /// <summary>报告记录的那一轮验证没跑完（不是 completed）。</summary>
    public const string IncompleteRunCode = "verify-run-incomplete";

    /// <summary>报告早于当前计划（计划重新生成后没有重新验证）。</summary>
    public const string StalePlanCode = "verify-report-stale-vs-plan";

    /// <summary>报告由别的构建版本产生。</summary>
    public const string VersionMismatchCode = "verify-tool-version-mismatch";

    /// <summary>证据可用（仍须再过对象级闸门 <see cref="RepairGate"/>）。</summary>
    public const string EvidenceOkCode = "evidence-ok";

    /// <summary>同一次运行内"先写计划、后写报告"的写盘顺序容差（秒）。</summary>
    private const int FreshnessToleranceSeconds = 1;

    public static bool Allows(VerifyReport? report, JobContext ctx, out string code, out string status)
    {
        if (report is null)
        {
            code = NoReportCode;
            status = "无验证报告";
            return false;
        }

        if (!string.IsNullOrEmpty(report.JobId) &&
            !string.Equals(report.JobId, ctx.JobId, StringComparison.OrdinalIgnoreCase))
        {
            code = ForeignJobCode;
            status = $"证据属于别的任务（报告 {report.JobId} / 当前 {ctx.JobId}）";
            return false;
        }

        if (report.RunStatus is not null &&
            !string.Equals(report.RunStatus, "completed", StringComparison.OrdinalIgnoreCase))
        {
            code = IncompleteRunCode;
            status = $"那一轮验证没跑完（{report.RunStatus}）";
            return false;
        }

        // 计划重新生成后才验证过，才允许据此修复。
        // 读不到计划文件的写入时刻时不在这里挡：对象级闸门仍然有效，且不因 IO 失败放松判据。
        try
        {
            if (File.Exists(ctx.PlanPath))
            {
                var planWrittenUtc = File.GetLastWriteTimeUtc(ctx.PlanPath);
                if (report.VerifiedUtc.AddSeconds(FreshnessToleranceSeconds) < planWrittenUtc)
                {
                    code = StalePlanCode;
                    status = $"验证报告早于当前计划（计划 {planWrittenUtc:u} / 报告 {report.VerifiedUtc:u}）";
                    return false;
                }
            }
        }
        catch (Exception)
        {
            // 时间戳读不出来不是"证据可用"的理由，但也不是伪造相反结论的理由 ⇒ 继续走对象级判据。
        }

        if (!string.IsNullOrEmpty(report.ToolVersion))
        {
            var current = typeof(RepairEvidenceGate).Assembly.GetName().Version?.ToString();
            if (!string.IsNullOrEmpty(current) &&
                !string.Equals(report.ToolVersion, current, StringComparison.OrdinalIgnoreCase))
            {
                code = VersionMismatchCode;
                status = $"验证报告来自其它构建版本（报告 {report.ToolVersion} / 当前 {current}）";
                return false;
            }
        }

        code = EvidenceOkCode;
        status = "证据可用";
        return true;
    }
}