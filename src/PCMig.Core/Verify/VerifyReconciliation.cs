using PCMig.Core.Jobs;
using PCMig.Core.Models;

namespace PCMig.Core.Verify;

/// <summary>
/// ★ R2 接手修复 · 缺陷 A ★ 校验结论回流：把「校验期发现的真实缺失」写进 job-state。
///
/// ── 为什么需要这个类 ─────────────────────────────────────────────────────────
/// <see cref="JobState.MissingObjects"/> 的设计意图（见 <see cref="JobState"/> 上的注释）是
/// 「迁移报成功、校验却发现目标少了文件」时唯一的把关项，<c>TransferOrchestrator.IsTrustworthyCompletion</c>
/// 也把它当成六项判据之一。但在本类出现之前，**没有任何生产代码给它赋过值**，
/// 于是那一项恒为 0：六项判据里最关键的一道防线实际是空的。
///
/// 本类把「校验报告 → job-state.MissingObjects」这一段补上，并明确规定：
///   • 只有 <see cref="ObjectVerifyResult.MissingConfirmed"/> 为 true 的对象才计数。
///     UNKNOWN（源/目标不可达、枚举不全）时比对过程会把每个源文件都塞进 MissingSamples，
///     那些**不是**缺失证据 —— 拿它们计数就是重演 F-1 的"假缺失"事故。
///   • 每轮校验都要**重算并覆盖**（含归零），不允许旧计数残留成永久假红。
///
/// 调用点（顺序有意义，先落盘报告再改状态）：
///   ① <c>Verifier.RunAsync</c> 内 <c>_ctx.SaveVerify(report)</c> 之后；
///   ② 外壳 <c>MigrationSessionViewModel.VerifyAsync</c> 拿到报告后（用于刷新"可信完成"）。
/// 开跑的复位在 <c>TransferOrchestrator.RunAsync</c>（每轮换新现场，旧结论作废）。
/// </summary>
public static class VerifyReconciliation
{
    /// <summary>
    /// 数出「已被确证源有目标无」的对象个数。
    /// ★ 判据只能是 <see cref="ObjectVerifyResult.MissingConfirmed"/>，
    /// 不能用 <c>MissingSamples.Count &gt; 0</c> —— UNKNOWN 时它也会被填满。
    /// </summary>
    public static int CountMissingObjects(VerifyReport? report)
    {
        if (report?.Objects is not { Count: > 0 } objects) return 0;

        var n = 0;
        foreach (var o in objects)
        {
            if (o is null) continue;
            if (o.MissingConfirmed) n++;
        }
        return n;
    }

    /// <summary>
    /// 把本轮校验结论写回 job-state，返回写入的缺失对象数。
    ///
    /// 覆盖而非累加：每份 <see cref="VerifyReport"/> 都是对**当前**现场的一次完整对账，
    /// 上一轮的计数描述的是上一轮的现场，必须被本轮结论替换（含替换成 0）。
    /// </summary>
    public static int Apply(JobContext ctx, VerifyReport? report)
    {
        var n = CountMissingObjects(report);
        var state = ctx.LoadStateOrNew();
        if (state.MissingObjects == n) return n;

        state.MissingObjects = n;
        // ★ R2 接手修复 · 缺陷 K ★ 必须走**校验结论所有者**的写入口：
        //   普通 SaveState 只写视图字段，会把磁盘上的旧值原样带回来 ⇒ 本方法刚算出的结论
        //   （尤其是"确证缺失 = 0，旧计数清掉"这个方向的修正）根本落不了盘。
        ctx.SaveStateWithVerifyConclusion(state);
        return n;
    }
}
