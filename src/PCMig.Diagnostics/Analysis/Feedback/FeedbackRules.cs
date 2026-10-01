using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;

namespace PCMig.Diagnostics.Analysis.Feedback;

/// <summary>
/// 反馈类规则的输入（由跟踪器的超时判定产生）。
/// 与事件驱动规则不同：它**没有触发事件**，只有"期望 + 没等到"这一事实。
/// </summary>
public sealed record FeedbackTimeoutInput(ExpectationTimeout Timeout);

/// <summary>
/// UI_COMMAND_NOT_DISPATCHED：动作被观察到、准入也通过了，但**处理路径始终没有开始**。
///
/// 它回答的是"点击没反应发生在哪一步"里最靠前的那一段。诚实边界：
///   · 它不断言具体源码缺陷，只指出**断点位置**与候选（guard 分支/接线/调度）；
///   · 采集有损时降级为 Inconclusive（"没记到" ≠ "没发生"）。
/// </summary>
public sealed class UiCommandNotDispatchedRule
{
    public const string Id = "UI_COMMAND_NOT_DISPATCHED";
    public const string Symptom = "UI_COMMAND_NOT_DISPATCHED";

    /// <summary>该规则只关心 Immediate 类期望（"进入处理路径"）。</summary>
    public static bool Handles(ExpectationTimeout timeout) =>
        timeout.Step.ExpectationId.EndsWith(".immediate", StringComparison.Ordinal);

    /// <summary>事件卡身份的唯一来源（结案时也要用它，避免两处拼字符串拼错）。</summary>
    public static string IncidentIdFor(PendingExpectation pending) =>
        Id + "|" + DiagnosticId.Format(pending.ActionId) + "|g" + pending.RunGeneration;

    public Incident Create(in ExpectationTimeout timeout)
    {
        var pending = timeout.Expectation;
        var incident = new Incident(
            incidentId: IncidentIdFor(pending),
            ruleId: Id,
            ruleVersion: 1,
            symptomCode: Symptom,
            severity: DiagnosticLevel.Warning,
            firstSeenUtc: pending.OpenedUtc,
            lastSeenUtc: pending.OpenedUtc,
            actionId: pending.ActionId,
            runGeneration: pending.RunGeneration > 0 ? pending.RunGeneration : null);

        var update = new IncidentUpdate
        {
            AtUtc = pending.OpenedUtc,
            Fact = new IncidentEvidence(pending.OpenedFrom, UiEvents.UserActionObserved.Name, IncidentEvidence.Fact),
            Severity = DiagnosticLevel.Warning,
            // ★ 这是一条"缺事件"结论 ⇒ 采集有损时必须敢说不知道 ★
            Status = timeout.EvidenceComplete ? null : IncidentStatus.Inconclusive,
            Confidence = timeout.EvidenceComplete ? ConfidenceBand.Medium : ConfidenceBand.Unknown,
            ConfidenceRationale = timeout.EvidenceComplete
                ? "动作已被观察到，但 " + timeout.OverdueMs + "ms 内没有出现「进入处理路径」的证据"
                : "采集不完整（lossEpoch=" + timeout.LossEpoch + "）：无法区分「没派发」与「没记到」",
            BreakPoint = "Ui.ActionDispatch",
            EvidenceIncomplete = !timeout.EvidenceComplete,
            LossEpoch = timeout.LossEpoch == 0 ? null : timeout.LossEpoch,
            Missing = timeout.EvidenceComplete ? Array.Empty<MissingEvidence>() : new[]
            {
                new MissingEvidence(pending.ContractId, string.Join(",", timeout.Step.ExpectedEventNames),
                    "evidence-loss", timeout.AcceptanceWatermark, false),
            },
            UserFacingSummary = timeout.EvidenceComplete
                ? "这个动作似乎没有进入处理路径：界面可能停在「点了没反应」的状态。"
                : "这一步的证据不完整：无法判断是「没派发」还是「没记到」。",
            TechnicalSummary = "actionKind=" + pending.ActionKind + " controlId=" + pending.ControlId +
                               " contract=" + pending.ContractId + "v" + pending.ContractVersion +
                               " waitedMs=" + timeout.OverdueMs,
            Candidates = new[]
            {
                new IncidentCandidate("guard-blocked", "既有 guard/CanExecute 判据把动作挡下了（但按契约应有拒绝反馈）", ConfidenceBand.Medium, Refuted: false),
                new IncidentCandidate("handler-not-wired", "点击处理器没有接到该方法（XAML 事件未挂/名称不匹配）", ConfidenceBand.Medium, Refuted: false),
                new IncidentCandidate("dispatcher-saturated", "UI 线程繁忙，入队的工作项尚未执行", ConfidenceBand.Low, Refuted: false),
                new IncidentCandidate("collection-loss", "其实已经发生，只是这条证据没被采集到（采集不完整时优先怀疑）", ConfidenceBand.Low, Refuted: timeout.EvidenceComplete),
            },
            SuggestedChecks = new[]
            {
                "再点一次并观察界面是否有任何状态变化（按钮置灰/状态句）",
                "确认窗口获得焦点、控件未被遮挡（Deep Trace 可确认输入是否到达窗口）",
                "若采集不完整（LossEpoch>0），本结论不可作为「没反应」的证据",
            },
        };

        incident.Merge(update);
        return incident;
    }
}

/// <summary>
/// UI_FEEDBACK_MISSING：动作已进入处理路径，但**按契约应当出现的反馈始终没出现**。
///
/// 与上一条的区别（很重要）：上一条断的是"动作没被派发"，这一条断的是
/// "动作执行了、但用户看不到结果"——两者的候选原因与修法完全不同。
/// </summary>
public sealed class UiFeedbackMissingRule
{
    public const string Id = "UI_FEEDBACK_MISSING";
    public const string Symptom = "UI_FEEDBACK_MISSING";

    public static bool Handles(ExpectationTimeout timeout) =>
        !timeout.Step.ExpectationId.EndsWith(".immediate", StringComparison.Ordinal);

    /// <summary>事件卡身份的唯一来源（结案时复用）。</summary>
    public static string IncidentIdFor(PendingExpectation pending, ExpectedStep step) =>
        Id + "|" + DiagnosticId.Format(pending.ActionId) + "|" + step.ExpectationId + "|g" + pending.RunGeneration;

    public Incident Create(in ExpectationTimeout timeout)
    {
        var pending = timeout.Expectation;
        var incident = new Incident(
            incidentId: IncidentIdFor(pending, timeout.Step),
            ruleId: Id,
            ruleVersion: 1,
            symptomCode: Symptom,
            severity: DiagnosticLevel.Warning,
            firstSeenUtc: pending.OpenedUtc,
            lastSeenUtc: pending.OpenedUtc,
            actionId: pending.ActionId,
            runGeneration: pending.RunGeneration > 0 ? pending.RunGeneration : null);

        // "期待"本身也是证据：说清"按契约该出现什么"。
        var expected = string.Join(",", timeout.Step.ExpectedEventNames);
        var hasAnySatisfied = pending.Satisfied.Count > 0;

        var update = new IncidentUpdate
        {
            AtUtc = pending.OpenedUtc,
            Fact = new IncidentEvidence(pending.OpenedFrom, UiEvents.UserActionObserved.Name, IncidentEvidence.Fact),
            Severity = DiagnosticLevel.Warning,
            // 缺事件结论 ⇒ 采集有损时降级为 Inconclusive。
            Status = timeout.EvidenceComplete ? null : IncidentStatus.Inconclusive,
            Confidence = !timeout.EvidenceComplete ? ConfidenceBand.Unknown
                : hasAnySatisfied ? ConfidenceBand.Medium
                : ConfidenceBand.Low,
            ConfidenceRationale = !timeout.EvidenceComplete
                ? "采集不完整（lossEpoch=" + timeout.LossEpoch + "）：无法区分「反馈没出现」与「没记到」"
                : "契约 " + pending.ContractId + " 要求观察到 " + expected +
                  "；已超出预算 " + timeout.OverdueMs + "ms" +
                  (hasAnySatisfied ? "（动作链前段已满足）" : "（动作链前段也未观察到）"),
            BreakPoint = "Ui.Feedback." + timeout.Step.ExpectationId,
            EvidenceIncomplete = !timeout.EvidenceComplete,
            LossEpoch = timeout.LossEpoch == 0 ? null : timeout.LossEpoch,
            Missing = timeout.EvidenceComplete ? Array.Empty<MissingEvidence>() : new[]
            {
                new MissingEvidence(pending.ContractId, expected, "evidence-loss", timeout.AcceptanceWatermark, false),
            },
            UserFacingSummary = !timeout.EvidenceComplete
                ? "这一步的证据不完整：无法判断反馈是「没出现」还是「没记到」。"
                : timeout.Step.Kind == ExpectationKind.ExternalWait
                    ? "这一步依赖外部世界（网络/磁盘/对象边界），目前只是「还没等到」，不一定是失败。"
                    : "动作在执行，但按预期应当出现的反馈没有出现：界面可能一直停在「看不到结果」的状态。",
            TechnicalSummary = "expectation=" + timeout.Step.ExpectationId + " kind=" + timeout.Step.Kind +
                               " expected=" + expected + " waitedMs=" + timeout.OverdueMs +
                               " satisfied=" + string.Join(";", pending.Satisfied.Keys),
            Candidates = timeout.Step.Kind == ExpectationKind.ExternalWait
                ? new[]
                {
                    new IncidentCandidate("external-slow", "外部等待本来就慢（DNS/IPC$/robocopy 退避/对象边界）", ConfidenceBand.Medium, Refuted: false),
                    new IncidentCandidate("external-stalled", "外部调用卡住（无超时的 IO/句柄等待）", ConfidenceBand.Low, Refuted: false),
                    new IncidentCandidate("collection-loss", "事件没被采集到", ConfidenceBand.Low, Refuted: timeout.EvidenceComplete),
                }
                : new[]
                {
                    new IncidentCandidate("projection-not-pushed", "状态已更新但控件没被真正推送（代码直推遗漏/在错误线程）", ConfidenceBand.Medium, Refuted: false),
                    new IncidentCandidate("binding-path-broken", "绑定路径与属性名不一致（界面永远不刷新）", ConfidenceBand.Medium, Refuted: false),
                    new IncidentCandidate("feedback-deferred-by-batching", "UI 批量节流把这次更新合并掉了（终态反馈不应当被合并）", ConfidenceBand.Low, Refuted: false),
                },
            SuggestedChecks = new[]
            {
                "对照诊断中心的时间线：动作链走到哪一步就断了",
                "确认该动作的终态反馈没有走「批量/可丢弃」通道（终态必须走立即通道）",
                "若为外部等待，先看指标页的链路速度与进程状态，再判断是否真的卡住",
            },
        };

        incident.Merge(update);
        return incident;
    }
}