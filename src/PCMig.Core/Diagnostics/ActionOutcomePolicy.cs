using PCMig.Core.Models;
using PCMig.Diagnostics.Abstractions;

namespace PCMig.Core.Diagnostics;

// ============================================================
// D6.3 §11 / 审计 P1-4：**动作终点结果必须来自实际业务结果**。
//
// 缺陷原文：页面在 `await session.XxxAsync()` 之后直接写
// `trace.Complete(DiagnosticOutcome.Succeeded, "xxx-returned")` —— 把"方法返回了"
// 当成"业务成功了"。停止/修复/验证都可能什么都没做成，事件却写 Succeeded。
//
// 收口口径（本文件是**唯一判定出口**，纯函数、可单测）：
//   · 页面/VM 只负责"取既有事实 → 调这里 → 落 UI.ActionCompleted/ActionRejected"，
//     不允许任何调用点再自己写死 Succeeded；
//   · 事实不足 ⇒ 写 Unknown（"没有证据"就是没有证据），绝不用成功兜底；
//   · 本文件**不参与任何业务分支**（§22 Observer Boundary）：只把已经发生的业务结果翻译成诊断口径。
// ============================================================

/// <summary>验证动作的实际业务事实（由 VM 从既有分支如实填写，不新增判定）。</summary>
/// <param name="Executed">验证真的跑了（报告里有对象）；计划为空 ⇒ 未执行。</param>
/// <param name="OverallPass">既有验证报告的结构化结论 <c>VerifyReport.OverallPass</c>。</param>
/// <param name="BlockedReason">被既有闸门/前置条件拦下的原因码（非 null ⇒ 拒绝）。</param>
/// <param name="Canceled">被取消（既有 OperationCanceledException 分支）。</param>
/// <param name="Faulted">抛异常（既有 catch（Exception）分支）。</param>
public readonly record struct VerifyBusinessResult(
    bool Executed,
    bool OverallPass,
    string? BlockedReason,
    bool Canceled,
    bool Faulted);

/// <summary>修复动作的实际业务事实（目标数 + 既有回执统计，不新增成功/失败判定）。</summary>
public readonly record struct RepairBusinessResult(
    int TargetCount,
    int CompletedCount,
    int FailedCount,
    string? BlockedReason);

/// <summary>传输动作（开始/恢复）的实际业务事实（既有 JobPhase + 前置拦下/失败原因码）。</summary>
public readonly record struct RunBusinessResult(
    JobPhase Phase,
    string? BlockedReason,
    string? FailedReason);

/// <summary>动作终点结果：结果 + 原因码（原因码进 UI.ActionCompleted 的 phase 字段）。</summary>
public readonly record struct ActionOutcomeDecision(DiagnosticOutcome Outcome, string ReasonCode);

/// <summary>D6.3 §11：业务结果 → 动作终点结果的唯一判定出口。</summary>
public static class ActionOutcomePolicy
{
    /// <summary>尚未观察到任何业务结果：既不成功也不失败，如实写 Unknown。</summary>
    public static ActionOutcomeDecision NotRun { get; } =
        new(DiagnosticOutcome.Unknown, "not-run");

    /// <summary>验证：只有报告真的跑了且 <c>OverallPass</c> 才是 Succeeded。</summary>
    public static ActionOutcomeDecision ForVerify(VerifyBusinessResult r)
    {
        if (r.Faulted) return new(DiagnosticOutcome.Failed, "verify-faulted");
        if (r.Canceled) return new(DiagnosticOutcome.Canceled, "verify-canceled");
        if (r.BlockedReason is { Length: > 0 } blocked) return new(DiagnosticOutcome.Rejected, blocked);
        if (!r.Executed) return new(DiagnosticOutcome.Skipped, "verify-not-executed");
        return r.OverallPass
            ? new(DiagnosticOutcome.Succeeded, "verify-pass")
            : new(DiagnosticOutcome.Failed, "verify-mismatch");
    }

    /// <summary>修复：只看既有回执。有失败 ⇒ Failed；一个回执都没有 ⇒ Unknown（不敢说成功）。</summary>
    public static ActionOutcomeDecision ForRepair(RepairBusinessResult r)
    {
        if (r.BlockedReason is { Length: > 0 } blocked) return new(DiagnosticOutcome.Rejected, blocked);
        if (r.TargetCount <= 0) return new(DiagnosticOutcome.Skipped, "repair-no-targets");
        if (r.FailedCount > 0) return new(DiagnosticOutcome.Failed, "repair-partial");
        if (r.CompletedCount > 0) return new(DiagnosticOutcome.Succeeded, "repair-completed");
        return new(DiagnosticOutcome.Unknown, "repair-no-receipts");
    }

    /// <summary>开始/恢复：以既有 JobPhase 为准；被拦下/失败/暂停/中断都不叫 Succeeded。</summary>
    public static ActionOutcomeDecision ForRun(RunBusinessResult r)
    {
        if (r.FailedReason is { Length: > 0 } failed) return new(DiagnosticOutcome.Failed, failed);
        if (r.BlockedReason is { Length: > 0 } blocked) return new(DiagnosticOutcome.Rejected, blocked);
        return r.Phase switch
        {
            JobPhase.Completed => new(DiagnosticOutcome.Succeeded, "run-completed"),
            JobPhase.CompletedWithErrors => new(DiagnosticOutcome.Failed, "run-completed-with-errors"),
            // 暂停/中断都只是"运行停在这里"：请求被受理，但业务没有完成。
            JobPhase.Paused => new(DiagnosticOutcome.Accepted, "run-paused"),
            JobPhase.Interrupted => new(DiagnosticOutcome.Canceled, "run-interrupted"),
            JobPhase.Canceled => new(DiagnosticOutcome.Canceled, "run-canceled"),
            JobPhase.Failed => new(DiagnosticOutcome.Failed, "run-failed"),
            _ => new(DiagnosticOutcome.Unknown, "run-unknown-phase"),
        };
    }

    /// <summary>
    /// 请求类动作（暂停/停止）：**只有请求真的被受理**才是 Accepted；没有在跑的运行 ⇒ Rejected。
    /// 真停不停是异步的（由 Core 的 Pause/StopObserved 记录），所以这里绝不写 Succeeded。
    /// </summary>
    public static ActionOutcomeDecision ForRequest(bool honored, string honoredReason, string rejectedReason) =>
        honored ? new(DiagnosticOutcome.Accepted, honoredReason) : new(DiagnosticOutcome.Rejected, rejectedReason);
}