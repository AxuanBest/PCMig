using System;
using System.Threading;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;

namespace PCMig.WinUI.Diagnostics;

/// <summary>
/// 一次用户语义动作的追踪器：把 UI 侧能观测到的事实串成一条链
/// （Observed → Eligibility → Started → DomainAccepted → FeedbackExpected/Confirmed → Completed，
/// 允许 Rejected/Faulted 提前终止）。
///
/// 设计要点（方案 §9）：
///   · 动作实例身份 = ActionId（128-bit），所有相关事件共用它 ⇒ 因果链可重建；
///   · 通过 **AsyncLocal 便捷作用域**让 VM/Core 侧的观察点拿到同一个 ActionId，
///     从而不必给既有方法加参数（签名改动 = 冻结范围风险）；
///     但 AsyncLocal **不是唯一事实源**：跨线程长期任务仍必须显式传 context（D3b 起如此做）。
///   · 全部发布都**非抛**：诊断失败绝不影响交互。
/// </summary>
public sealed class ActionTrace : IDisposable
{
    private readonly IDiagnosticSink _sink;
    private readonly DiagnosticContext _context;
    private readonly EventRef? _causation;
    private readonly IDisposable _scope;
    private long _startedMonotonic;
    private int _completed;

    private ActionTrace(IDiagnosticSink sink, DiagnosticContext context, EventRef? causation, IDisposable scope)
    {
        _sink = sink;
        _context = context;
        _causation = causation;
        _scope = scope;
    }

    /// <summary>
    /// 当前作用域内的动作 ID（VM/Core 观察点用它对齐同一个动作）。
    /// 载体是 UI 无关的 <see cref="ActionScope"/>，因此 Presentation 层也能读到。
    /// </summary>
    public static Guid? CurrentActionId => ActionScope.CurrentActionId;

    /// <summary>动作类型（稳定 token）。</summary>
    public string ActionKind { get; private init; } = "Unknown";

    public Guid ActionId => _context.ActionId ?? Guid.Empty;

    /// <summary>
    /// 观察到一个用户动作并开启追踪作用域。**只记录"观察到动作"这一事实**：
    /// 它不证明输入已送达控件（那是 Deep/测试意图的职责）。
    /// </summary>
    public static ActionTrace Begin(string actionKind, string controlId, string source, string component)
    {
        var sink = global::PCMig.Core.Diagnostics.CoreDiagnostics.Sink;
        var actionId = DiagnosticId.NewActionId();
        var context = sink.Root(component)
            .WithControl(controlId)
            .WithAction(actionId);

        var scope = ActionScope.Enter(actionId, controlId, component);
        var trace = new ActionTrace(sink, context, causation: null, scope) { ActionKind = actionKind };
        trace._startedMonotonic = System.Diagnostics.Stopwatch.GetTimestamp();

        trace.Publish(UiEvents.UserActionObserved, new UiActionPayload(actionKind, source),
            DiagnosticLevel.Information, DiagnosticOutcome.Started);
        return trace;
    }

    /// <summary>原有 guard/CanExecute 的**既有判断结果**（不为诊断多跑一次判据）。</summary>
    public void Eligibility(bool allowed, string reasonCode)
        => Publish(UiEvents.CommandEligibilityEvaluated, new UiEligibilityPayload(allowed, reasonCode),
            allowed ? DiagnosticLevel.Information : DiagnosticLevel.Warning,
            allowed ? DiagnosticOutcome.Accepted : DiagnosticOutcome.Rejected);

    /// <summary>动作真正进入处理路径（handler 开始执行）。</summary>
    public void Started()
        => Publish(UiEvents.CommandStarted, new UiActionPayload(ActionKind, "handler"),
            DiagnosticLevel.Information, DiagnosticOutcome.Started);

    /// <summary>按契约应当出现的反馈（**期望**，不是事实）。</summary>
    public void Expect(string contractId, string expectation)
        => Publish(UiEvents.FeedbackExpected, new UiFeedbackPayload(contractId, expectation, null),
            DiagnosticLevel.Information, DiagnosticOutcome.Started);

    /// <summary>反馈已确认（契约闭环）。</summary>
    public void Confirm(string contractId, string expectation)
    {
        var observedAfterMs = DiagnosticClockTicksToMs(System.Diagnostics.Stopwatch.GetTimestamp() - _startedMonotonic);
        Publish(UiEvents.FeedbackConfirmed, new UiFeedbackPayload(contractId, expectation, observedAfterMs),
            DiagnosticLevel.Information, DiagnosticOutcome.Succeeded);
    }

    /// <summary>动作被合法拒绝（带稳定 reasonCode）。</summary>
    public void Reject(string reasonCode, string component = "Ui")
    {
        Publish(UiEvents.ActionRejected, new UiEligibilityPayload(false, reasonCode),
            DiagnosticLevel.Warning, DiagnosticOutcome.Rejected, component);
        Interlocked.Exchange(ref _completed, 1);
    }

    /// <summary>动作失败（异常文本不落入事件；只留类型/错误码）。</summary>
    public void Fault(Exception exception, string phase, string component = "Ui")
    {
        Publish(UiEvents.ActionFaulted, null, DiagnosticLevel.Error, DiagnosticOutcome.Failed, component,
            exceptionType: exception.GetType().Name, hresult: exception.HResult, phase: phase);
        Interlocked.Exchange(ref _completed, 1);
    }

    /// <summary>动作真正结束（**以实际结果分支为准**，绝不用"Task 返回了"冒充成功）。</summary>
    public void Complete(DiagnosticOutcome outcome, string? phase = null)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 1) return;
        var durationMs = DiagnosticClockTicksToMs(System.Diagnostics.Stopwatch.GetTimestamp() - _startedMonotonic);
        Publish(UiEvents.ActionCompleted, new UiActionPayload(ActionKind, "handler"),
            outcome == DiagnosticOutcome.Succeeded ? DiagnosticLevel.Information : DiagnosticLevel.Warning,
            outcome, phase: phase, durationMs: durationMs);
    }

    /// <summary>VM 状态被写入（投影来源变化）。</summary>
    public void ProjectionChanged(string stateOwnerName, int changedFieldCount, string component = "Vm")
        => Publish(UiEvents.ProjectionChanged, new UiProjectionChangedPayload(stateOwnerName, changedFieldCount),
            DiagnosticLevel.Debug, DiagnosticOutcome.Succeeded, component);

    /// <summary>自身 health 的降级（例如 DispatcherQueue 拒绝入队）。</summary>
    public static void DispatchRejected(string reasonCode, string targetKind, string component)
    {
        var sink = global::PCMig.Core.Diagnostics.CoreDiagnostics.Sink;
        var publisher = sink.Publisher;
        try
        {
            if (!publisher.IsEnabledFor(UiEvents.DispatchRejected)) return;
            publisher.TryPublish(new DiagnosticEventDraft(
                UiEvents.DispatchRejected,
                sink.Root(component),
                new UiDispatchRejectedPayload(reasonCode, targetKind),
                Level: DiagnosticLevel.Warning,
                Outcome: DiagnosticOutcome.Rejected));
        }
        catch (Exception) { /* 观察失败绝不影响交互 */ }
    }

    public void Dispose()
    {
        _scope.Dispose();
        // 动作结束时若从未落过终点，补一条"未确认结束"的事实（而不是假装成功）。
        if (Volatile.Read(ref _completed) == 0)
            Complete(DiagnosticOutcome.Unknown, "no-explicit-terminal");
    }

    private void Publish(
        EventDescriptor descriptor,
        IDiagnosticPayload? payload,
        DiagnosticLevel level,
        DiagnosticOutcome outcome,
        string? component = null,
        string? exceptionType = null,
        int? hresult = null,
        string? phase = null,
        int? durationMs = null)
    {
        try
        {
            var context = component is null ? _context : _context.WithComponent(component);
            var publisher = _sink.Publisher;
            if (!publisher.IsEnabledFor(descriptor)) return;

            publisher.TryPublish(new DiagnosticEventDraft(
                descriptor,
                context,
                payload,
                Level: level,
                Outcome: outcome,
                Causation: _causation,
                ExceptionType: exceptionType,
                HResult: hresult,
                ErrorDomain: hresult is null ? ErrorDomain.None : ErrorDomain.Managed,
                DurationMs: durationMs,
                Phase: phase,
                StateOwner: StateOwner.Vm));
        }
        catch (Exception)
        {
            // 诊断绝不影响交互。
        }
    }

    private static int DiagnosticClockTicksToMs(long ticks)
        => (int)Math.Min(int.MaxValue, Math.Max(0, ticks * 1000L / System.Diagnostics.Stopwatch.Frequency));
}