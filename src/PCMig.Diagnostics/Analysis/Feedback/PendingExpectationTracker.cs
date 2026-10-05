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

/// <summary>
/// ★ FIX BATCH 3 / §6 ★ **动作未兑现**的判定结果。
///
/// 它与 <see cref="ExpectationTimeout"/> 是两件事，绝不能混：
///   · Timeout  = "还没有观察到"（事实：等的东西没来；可能只是慢）；
///   · Failure  = "用户要求了业务动作、请求也已受理，但**业务效果从未达成**"
///                （引擎自己承认停不住，或承诺的期限已过）。
/// 后者必须让健康 verdict 降级、开出事件卡——这正是真机事故里"8/8 次暂停点击、0 次引擎确认、
/// 事件卡 0、健康 healthy"缺的那条通路。
/// </summary>
public sealed record ExpectationFailure(
    PendingExpectation Expectation,
    ExpectedStep Step,
    EventRef TriggerRef,
    string TriggerEventName,
    long ObservedAfterMs);

/// <summary>跟踪器统计（有界性与落后都要可见）。</summary>
public readonly record struct ExpectationTrackerStats(
    long Begun,
    long Satisfied,
    long TimedOut,
    long ClosedByRejection,
    long ClosedByTerminal,
    long DroppedByCap,
    int Pending,
    long UnsupportedVersionSkipped,
    long ClosedByFailure = 0);

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
///   · ★ FIX BATCH 3 ★ **受理 ≠ 兑现**：Failure 类步骤命中即"业务效果未达成"⇒
///     关闭期望为失败并产出 <see cref="ExpectationFailure"/>（走 <see cref="OnFailure"/>）；
///   · 采集有损时超时结论必须降级（由规则侧的 RequiresCompleteEvidence 机制保证）。
/// </summary>
public sealed class PendingExpectationTracker
{
    public const int MaxPending = 128;

    private readonly FeedbackContractRegistry _contracts;
    private readonly Action<ExpectationTimeout> _onTimeout;
    private readonly Dictionary<Guid, PendingExpectation> _pending = new();

    /// <summary>
    /// ★ D6.3 §7「自检健康」★ 单把锁保护 <see cref="_pending"/> 及其计数。
    ///
    /// 为什么必须有：<see cref="Observe"/> 在 **analyzer 消费线程**上跑，
    /// <see cref="Tick"/> 在 **维护定时器线程**上跑，收尾时还会从关停路径调 <see cref="CloseAll"/>。
    /// 原来三者都在裸改 `Dictionary` —— 并发写字典会把它变成环或撕裂内部状态，
    /// 之后每一次 `foreach` 都可能自旋不返回，**整个维护循环就这样被拖死**
    /// （审计 P1-2：诊断自己坏掉时却仍然报 Healthy + EvidenceComplete）。
    ///
    /// 纪律：临界区只做集合与计数，**绝不在锁内调用 <see cref="_onTimeout"/>**
    /// （回调会落到规则引擎去开事件卡，锁内调用等于把锁借给外部代码）。
    /// </summary>
    private readonly object _gate = new();

    private long _begun;
    private long _satisfied;
    private long _timedOut;
    private long _closedByRejection;
    private long _closedByTerminal;
    private long _closedByFailure;
    private long _droppedByCap;
    private long _unsupportedVersionSkipped;

    /// <summary>
    /// ★ D6.3 §11 ★ 所有契约步骤指名的事件名（用于快速判定"这个事件有没有可能满足某一步"）。
    /// 没有它就得对每个事件遍历所有 pending × 所有步骤，纯属浪费。
    /// </summary>
    private readonly HashSet<string> _stepEventNames = new(StringComparer.Ordinal);

    public PendingExpectationTracker(FeedbackContractRegistry contracts, Action<ExpectationTimeout> onTimeout)
    {
        _contracts = contracts;
        _onTimeout = onTimeout;
        foreach (var contract in contracts.All)
            foreach (var step in contract.Steps)
                foreach (var expected in step.ExpectedEventNames)
                    _stepEventNames.Add(expected);
    }

    public int PendingCount
    {
        get { lock (_gate) return _pending.Count; }
    }

    /// <summary>
    /// ★ R-2 收口（第二轮：两处无计数静默吞异常）★ 跟踪器**内部**故障的回声通道。
    ///
    /// 为什么必须有：<see cref="Observe"/> 的 catch 原来是一个**空体** —— 事件被 analyzer 消费了
    /// 却没有被解释："该开的期望"与"该关的期望"一起消失，而健康计数（SinkFaults / MaintenanceFaults /
    /// RuleFaults）看不出任何异常，会话仍会宣称 Healthy + EvidenceComplete。
    /// **容错（不许拖垮 analyzer）不等于无痕**：这里把故障交给运行时接进健康通道。
    /// </summary>
    public Action<string>? OnFault { get; set; }

    /// <summary>
    /// ★ FIX BATCH 3 / §6 ★ "动作未兑现"的回声通道（由 runtime 接进健康 verdict + 事件卡）。
    ///
    /// 与 <see cref="OnFault"/> 的区别：OnFault 是**跟踪器自己坏了**；这里是**产品没兑现承诺**
    /// —— 跟踪器工作完全正常，是它**如实报告**了"用户要求的效果没有发生"。
    /// 回调只在锁外派发，且绝不级联抛出。
    /// </summary>
    public Action<ExpectationFailure>? OnFailure { get; set; }

    /// <summary>观察一个事件（由 analyzer 收件箱调用；**绝不抛**）。</summary>
    public void Observe(in DiagnosticEvent evt)
    {
        List<ExpectationFailure>? fired = null;
        try
        {
            lock (_gate) ObserveCore(in evt, ref fired);
        }
        catch (Exception ex)
        {
            // 跟踪失败绝不影响诊断管线与业务 —— 但必须留痕，绝不静默。
            ReportFault("tracker-observe:" + ex.GetType().Name);
        }

        // ★ 回调在锁外派发 ★（与 Tick 同一纪律：不把锁借给外部代码）。
        if (fired is not null)
            foreach (var failure in fired) DispatchFailure(failure);
    }

    /// <summary>派发一次"未兑现"（**绝不抛**：健康通道自己坏掉时不在这里级联）。</summary>
    private void DispatchFailure(ExpectationFailure failure)
    {
        try
        {
            OnFailure?.Invoke(failure);
        }
        catch (Exception)
        {
            // 健康通道自身异常：不再级联。
        }
    }

    /// <summary>
    /// 上报一次跟踪器内部故障。**绝不抛**：健康通道自己坏掉时不在这里级联
    /// （与 <see cref="RuleEngine"/> 同一口径：已被隔离的故障不再制造第二个故障）。
    /// </summary>
    private void ReportFault(string reason)
    {
        try
        {
            OnFault?.Invoke(reason);
        }
        catch (Exception)
        {
            // 健康通道自身异常：不再级联。
        }
    }

    private void ObserveCore(in DiagnosticEvent evt, ref List<ExpectationFailure>? fired)
    {
        // ★ D6.3 §6.5 ★ 版本门与规则引擎**同一条**：不受支持的版本只配被保存，
        //   不配被当前契约解释。少了这道门，一个 `eventVersion=999` 的
        //   "动作被观察"事件就能凭空开出一条期望，最后变成"用户没反馈"的假事件卡。
        if (evt.VersionUnsupported)
        {
            Interlocked.Increment(ref _unsupportedVersionSkipped);
            return;
        }

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

        // ─────────── ② 带 actionId 的事件：精确对应到"这一次动作" ───────────
        //
        // ★ D6.3 §11 关联策略（真实世界校准，审计/实机复验缺陷 D63-18）★
        //
        // UI/VM 层事件带 actionId，可以精确落到某一次用户动作上。
        // 但 Core 侧的业务事件由后台线程发布（JobManager / robocopy 泵 / 预检线程），
        // **按设计不带 actionId** —— 而它们恰恰是 Deferred / ExternalWait 步骤指名要等的证据
        // （TRN.JobRunStarted、TRN.ObjectStarted、TRN.JobRunCompleted、TRN.PauseObserved、
        //   RBC.ProcessStarted、PFL.PreflightCompleted …）。
        //
        // 原来的写法是"事件没有 actionId 就 return"，于是这些步骤**永远不可能被满足**：
        // 只要动作在预算内没走到终止事件，就会凭空产出"用户没看到反馈"的假结论。
        // 实机证据：暂停中的 Start 在 20s 后开出
        //   UI_FEEDBACK_MISSING|…|start.deferred（evidenceIncomplete=false、Confidence=Medium），
        // 而 TRN.JobRunStarted 就在点击后 12ms 落盘、TechnicalSummary 里 satisfied 只有 start.immediate。
        //
        // 现在的口径（只放松"无 actionId"这一种情况，**不放松**终止事件与错配）：
        //   · 带 actionId 的事件：必须命中本次动作，否则丢弃（绝不错配给别的动作）；
        //   · 不带 actionId 的事件：只按**事件名**满足当前仍然打开、且契约里指名了该事件的期望。
        if (evt.ActionId is { } currentAction)
        {
            if (!_pending.TryGetValue(currentAction, out var pending)) return;
            if (pending.IsClosed) return;

            // 终止事件 ⇒ 关掉期望（合法终点；不产出任何"缺反馈"结论）。
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

            SatisfySteps(pending, in evt, name, ref fired);
            // ★ FIX BATCH 3 ★ 兑现失败（例如 TRN-023 PauseFailed 带了 actionId ⇒ UI 自己转发）
            //   必须**先收集再移除**：在遍历期间改 _pending 会让枚举器失效。
            if (pending.IsClosed) _pending.Remove(currentAction);
            return;
        }

        // ─────────── ③ 不带 actionId 的业务事件：按事件名满足仍然打开的期望 ───────────
        if (_pending.Count == 0 || !_stepEventNames.Contains(name)) return;
        List<Guid>? failed = null;
        foreach (var (actionId, pending) in _pending)
        {
            if (pending.IsClosed) continue;
            SatisfySteps(pending, in evt, name, ref fired);
            if (pending.IsClosed) (failed ??= new List<Guid>()).Add(actionId);
        }
        if (failed is not null)
            foreach (var id in failed) _pending.Remove(id);
    }

    /// <summary>
    /// 用事件满足待定期望里"指名了该事件"的步骤。
    /// 只增 <see cref="PendingExpectation.Satisfied"/> 与计数（调用方持锁、负责移除）。
    ///
    /// ★ FIX BATCH 3 / §6 ★ <see cref="ExpectationKind.Failure"/> 类步骤是**失败终点**：
    /// 它命中意味着"业务效果从未达成"被引擎自己证实 ⇒ 立即把该期望**关成失败**
    /// （而不是像普通步骤那样记一笔"满足"）。调用方随后把它从 _pending 移除。
    /// </summary>
    private void SatisfySteps(
        PendingExpectation pending, in DiagnosticEvent evt, string name, ref List<ExpectationFailure>? fired)
    {
        if (!_contracts.TryGet(pending.ActionKind, out var contractForSteps)) return;
        foreach (var step in contractForSteps.Steps)
        {
            if (pending.Satisfied.ContainsKey(step.ExpectationId)) continue;
            if (!step.ExpectedEventNames.Contains(name, StringComparer.Ordinal)) continue;

            if (step.Kind == ExpectationKind.Failure)
            {
                pending.IsClosed = true;
                pending.CloseReason = "failed:" + step.ExpectationId;
                _closedByFailure++;
                (fired ??= new List<ExpectationFailure>()).Add(new ExpectationFailure(
                    pending, step, evt.Ref, name,
                    DiagnosticClock.TicksToMs(evt.MonotonicTimestamp - pending.OpenedMonotonic)));
                return;
            }

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
        List<ExpectationTimeout>? fired = null;

        lock (_gate)
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
                    // ★ FIX BATCH 3 ★ 失败终点步骤"没发生"是**正常**的（没有失败当然没有失败事件）：
                    //   它不得参与超时判定，否则每次暂停都会多出一条纯噪音的"缺反馈"。
                    //   它的**相反**情形（该失败却没等到失败事件）由 pause.external 的
                    //   DeadlineBreachIsFailure 超时来表达。
                    if (step.Kind == ExpectationKind.Failure) continue;
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
                    (fired ??= new List<ExpectationTimeout>()).Add(new ExpectationTimeout(
                        pending, step, elapsedMs - budget, acceptanceWatermark, evidenceComplete, lossEpoch));
                }
            }

            if (remove is not null)
                foreach (var id in remove) _pending.Remove(id);
        }

        // ★ 回调在锁外派发 ★：规则引擎的事不能拿着这把锁办。
        if (fired is not null)
            foreach (var timeout in fired) _onTimeout(timeout);
    }

    /// <summary>会话收尾：把所有仍待满足的期望标记为"会话结束"（**不**产出缺反馈结论）。</summary>
    public void CloseAll(string reasonCode)
    {
        lock (_gate)
        {
            foreach (var pending in _pending.Values)
            {
                pending.IsClosed = true;
                pending.CloseReason = reasonCode;
            }
            _pending.Clear();
        }
    }

    public ExpectationTrackerStats Stats()
    {
        lock (_gate)
        {
            return new ExpectationTrackerStats(
                _begun, _satisfied, _timedOut, _closedByRejection, _closedByTerminal, _droppedByCap, _pending.Count,
                _unsupportedVersionSkipped, _closedByFailure);
        }
    }
}