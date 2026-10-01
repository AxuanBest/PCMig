using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;

namespace PCMig.Diagnostics.Analysis.Feedback;

/// <summary>一条待满足的期望（有界对象，含截止时刻与已满足的步骤集合）。</summary>
public sealed class PendingExpectation
{
    public required Guid ActionId { get; init; }
    public required string ActionKind { get; init; }
    public required string ContractId { get; init; }
    public required int ContractVersion { get; init; }
    public required string ControlId { get; init; }
    public required long RunGeneration { get; init; }

    /// <summary>动作被观察到的那一刻（单调计数，用于到期判定）。</summary>
    public required long OpenedMonotonic { get; init; }

    public required DateTimeOffset OpenedUtc { get; init; }

    /// <summary>事件实例引用：这条期望是"从哪一次动作"开始的（证据必须可引用）。</summary>
    public required EventRef OpenedFrom { get; init; }

    /// <summary>已满足的步骤 → 满足它的证据事件。</summary>
    public Dictionary<string, EventRef> Satisfied { get; } = new(StringComparer.Ordinal);

    /// <summary>已判定为"没等到"的步骤（只判一次，避免每 tick 重复开卡）。</summary>
    public HashSet<string> TimedOut { get; } = new(StringComparer.Ordinal);

    public bool IsClosed { get; set; }

    /// <summary>闭合原因（rejected/faulted/completed/terminal-observed）。</summary>
    public string? CloseReason { get; set; }

    public bool AllSatisfied(FeedbackContract contract) =>
        contract.Steps.Where(s => s.Kind != ExpectationKind.Optional)
            .All(s => Satisfied.ContainsKey(s.ExpectationId));
}

/// <summary>超时判定结果：交给反馈规则变成事件卡（**不在这里直接下结论**）。</summary>
public sealed record ExpectationTimeout(
    PendingExpectation Expectation,
    ExpectedStep Step,
    long OverdueMs,
    long AcceptanceWatermark,
    bool EvidenceComplete,
    long LossEpoch);

/// <summary>跟踪器统计（有界性与落后都要可见）。</summary>
public readonly record struct ExpectationTrackerStats(
    long Begun,
    long Satisfied,
    long TimedOut,
    long ClosedByRejection,
    long ClosedByTerminal,
    long DroppedByCap,
    int Pending);

/// <summary>
/// 待满足期望跟踪器（方案 §10）。
///
/// 设计纪律：
///   · **单个 scheduler**：由 runtime 的定时器统一调 <see cref="Tick"/>，
///     **绝不为每个期望创建 Timer**；pending 有上限，到期判定只做一次；
///   · 只从事件流观察（`UI.UserActionObserved` 开、终止事件关、其它事件满足步骤），
///     不认识任何业务对象，也**不会**去改 CanResume/暂停状态；
///   · 超时只产出 <see cref="ExpectationTimeout"/>，**由规则决定**怎么落成事件卡
///     （事实与结论分离）；
///   · 采集有损时超时结论必须降级（由规则侧的 RequiresCompleteEvidence 机制保证）。
/// </summary>
public sealed class PendingExpectationTracker
{
    public const int MaxPending = 128;

    private readonly FeedbackContractRegistry _contracts;
    private readonly Action<ExpectationTimeout> _onTimeout;
    private readonly Dictionary<Guid, PendingExpectation> _pending = new();

    private long _begun;
    private long _satisfied;
    private long _timedOut;
    private long _closedByRejection;
    private long _closedByTerminal;
    private long _droppedByCap;

    public PendingExpectationTracker(FeedbackContractRegistry contracts, Action<ExpectationTimeout> onTimeout)
    {
        _contracts = contracts;
        _onTimeout = onTimeout;
    }

    public int PendingCount => _pending.Count;

    /// <summary>观察一个事件（由 analyzer 收件箱调用；**绝不抛**）。</summary>
    public void Observe(in DiagnosticEvent evt)
    {
        try
        {
            ObserveCore(in evt);
        }
        catch (Exception)
        {
            // 跟踪失败绝不影响诊断管线与业务。
        }
    }

    private void ObserveCore(in DiagnosticEvent evt)
    {
        var name = evt.Descriptor.Name;

        // ① 动作被观察到 ⇒ 按契约开一条期望。
        if (name == UiEvents.UserActionObserved.Name)
        {
            var actionKind = (evt.Payload as Abstractions.Payloads.UiActionPayload)?.ActionKind;
            if (actionKind is null || !_contracts.TryGet(actionKind, out var contract)) return;
            if (evt.ActionId is not { } actionId) return;

            if (_pending.Count >= MaxPending)
            {
                _droppedByCap++;
                return;
            }

            _pending[actionId] = new PendingExpectation
            {
                ActionId = actionId,
                ActionKind = actionKind,
                ContractId = contract.ContractId,
                ContractVersion = contract.Version,
                ControlId = evt.ControlId ?? "unknown",
                RunGeneration = evt.RunGeneration ?? 0,
                OpenedMonotonic = evt.MonotonicTimestamp,
                OpenedUtc = evt.TimestampUtc,
                OpenedFrom = evt.Ref,
            };
            _begun++;
            return;
        }

        if (evt.ActionId is not { } currentAction) return;
        if (!_pending.TryGetValue(currentAction, out var pending)) return;
        if (pending.IsClosed) return;

        // ② 终止事件 ⇒ 关掉期望（合法终点；不产出任何"缺反馈"结论）。
        if (_contracts.TryGet(pending.ActionKind, out var activeContract)
            && activeContract.TerminalEventNames.Contains(name, StringComparer.Ordinal))
        {
            pending.IsClosed = true;
            pending.CloseReason = name == UiEvents.ActionRejected.Name ? "rejected"
                : name == UiEvents.ActionFaulted.Name ? "faulted" : "completed";
            if (pending.CloseReason == "rejected") _closedByRejection++;
            else _closedByTerminal++;
            _pending.Remove(currentAction);
            return;
        }

        // ③ 其它事件 ⇒ 满足对应步骤。
        if (!_contracts.TryGet(pending.ActionKind, out var contractForSteps)) return;
        foreach (var step in contractForSteps.Steps)
        {
            if (pending.Satisfied.ContainsKey(step.ExpectationId)) continue;
            if (!step.ExpectedEventNames.Contains(name, StringComparer.Ordinal)) continue;
            pending.Satisfied[step.ExpectationId] = evt.Ref;
            _satisfied++;
        }
    }

    /// <summary>
    /// **唯一**的到期判定入口（由 runtime 定时器调用，建议 250ms 节拍）。
    /// 只用单调时钟（跨机/跨会话不可比的 UTC 不参与判定）。
    /// </summary>
    public void Tick(long nowMonotonic, bool evidenceComplete, long lossEpoch, long acceptanceWatermark)
    {
        if (_pending.Count == 0) return;

        List<Guid>? remove = null;
        foreach (var (actionId, pending) in _pending)
        {
            if (pending.IsClosed) { (remove ??= new List<Guid>()).Add(actionId); continue; }
            if (!_contracts.TryGet(pending.ActionKind, out var contract)) continue;

            var elapsedMs = DiagnosticClock.TicksToMs(nowMonotonic - pending.OpenedMonotonic);

            foreach (var step in contract.Steps)
            {
                if (step.Kind == ExpectationKind.Optional) continue;
                if (pending.Satisfied.ContainsKey(step.ExpectationId)) continue;
                if (pending.TimedOut.Contains(step.ExpectationId)) continue;

                var budget = step.Kind switch
                {
                    ExpectationKind.Immediate => contract.ImmediateTimeoutMs,
                    ExpectationKind.Deferred => contract.DeferredTimeoutMs,
                    _ => contract.ExternalWaitTimeoutMs,
                };
                if (elapsedMs < budget) continue;

                pending.TimedOut.Add(step.ExpectationId);
                _timedOut++;
                _onTimeout(new ExpectationTimeout(
                    pending, step, elapsedMs - budget, acceptanceWatermark, evidenceComplete, lossEpoch));
            }
        }

        if (remove is not null)
            foreach (var id in remove) _pending.Remove(id);
    }

    /// <summary>会话收尾：把所有仍待满足的期望标记为"会话结束"（**不**产出缺反馈结论）。</summary>
    public void CloseAll(string reasonCode)
    {
        foreach (var pending in _pending.Values)
        {
            pending.IsClosed = true;
            pending.CloseReason = reasonCode;
        }
        _pending.Clear();
    }

    public ExpectationTrackerStats Stats() => new(
        _begun, _satisfied, _timedOut, _closedByRejection, _closedByTerminal, _droppedByCap, _pending.Count);
}