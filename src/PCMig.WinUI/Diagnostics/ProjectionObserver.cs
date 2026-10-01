using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;

namespace PCMig.WinUI.Diagnostics;

/// <summary>
/// L2 控件投影只读回读（方案 §15/M 题）。
///
/// 为什么需要它：PCMig 有大量"代码直推"（PushFooter / PushState 直接写控件），
/// 因此 VM 属性变了**不等于**控件真的显示了。本类在**真实推送完成之后**读取关键控件的
/// 实际值，并把"读到了什么 + 它对应哪个 source 版本"作为投影证据落盘。
///
/// 诚实边界：
///   · 同写者自 ack 只能证明"赋值/读回"，**不证明屏幕像素**（那属于 L3 显式视觉验证）；
///   · 本类不在后台线程读控件（调用点必须已在 UI 线程）；
///   · Secret 控件（口令框）**永不采集**，连长度都不记。
/// </summary>
public static class ProjectionObserver
{
    /// <summary>
/// 读一个**真实控件**的状态并落盘投影证据（D6.1 §13）。
///
/// 与旧实现的区别（旧实现是"自己告诉自己"）：
///   · 旧：`ReadEnabled(..., isEnabled: _vm.Shares.Count > 0, matchesSource: true, ...)` ——
///     **期望值直接当成实际值**，`matchesSource` 硬编码为 true ⇒ 永远不可能检出投影陈旧；
///   · 新：实际值从**控件本身**读（IsEnabled / Visibility / IsLoaded），期望值来自投影承诺，
///     由 <see cref="ProjectionReadbackPolicy"/> 比较；页面没挂载 ⇒ `page-unavailable`（未观察到）。
/// </summary>
/// <param name="control">真实控件；null ⇒ 视为页面未挂载（未观察到，不是 mismatch）。</param>
/// <param name="expectedEnabled">投影承诺的可用性（null = 本次不比较该字段）。</param>
/// <param name="expectedVisibility">投影承诺的可见性（"Visible"/"Collapsed"；null = 不比较）。</param>
/// <param name="sourceObservationVersion">期望值来自哪一次状态推送（**当前代际**，由调用侧自增）。</param>
public static void ReadControl(
        FrameworkElement? control,
        string controlId,
        string component,
        bool? expectedEnabled,
        string? expectedVisibility,
        long sourceObservationVersion,
        long? projectionVersion = null)
    {
        try
        {
            var sink = global::PCMig.Core.Diagnostics.CoreDiagnostics.Sink;
            var publisher = sink.Publisher;
            if (!publisher.IsEnabledFor(UiEvents.ProjectionReadback)) return;

            var pageMounted = control is { IsLoaded: true };
            var actualEnabled = control is Control c && c.IsEnabled;
            var actualVisibility = control?.Visibility.ToString() ?? "Unavailable";

            var decision = ProjectionReadbackPolicy.Evaluate(
                pageMounted, actualEnabled, actualVisibility, expectedEnabled, expectedVisibility);

            var context = (ActionScope.CurrentActionId is { } actionId
                    ? sink.Root(component).WithAction(actionId)
                    : sink.Root(component))
                .WithControl(controlId);

            publisher.TryPublish(new DiagnosticEventDraft(
                UiEvents.ProjectionReadback,
                context,
                new UiProjectionReadbackPayload(
                    actualEnabled,
                    actualVisibility,
                    sourceObservationVersion,
                    projectionVersion,
                    Generation: sourceObservationVersion,
                    MatchesSource: decision.Matches)
                {
                    PageMounted = decision.PageMounted,
                    Observed = decision.Observed,
                    ExpectedEnabled = expectedEnabled,
                    ExpectedVisibility = expectedVisibility,
                    ReasonCode = decision.ReasonCode,
                },
                Level: decision.Matches ? DiagnosticLevel.Debug : DiagnosticLevel.Warning,
                Outcome: !decision.PageMounted ? DiagnosticOutcome.Unknown
                    : decision.Matches ? DiagnosticOutcome.Succeeded
                    : DiagnosticOutcome.Failed,
                ProjectionVersion: projectionVersion ?? sourceObservationVersion,
                StateOwner: StateOwner.View));
        }
        catch (Exception)
        {
            // 观察失败绝不影响界面。
        }
    }

    /// <summary>
    /// 读某个状态归属（Core/VM/View/StoredState）的关键状态位并落盘。
    /// 用于"三层是否一致"的判定（UI_STATE_CONTRADICTION / UI_PROJECTION_STALE 的输入）。
    /// </summary>
    public static void ReadState(
        string stateOwnerName,
        string component,
        string? phase,
        bool isRunning,
        bool isPaused,
        bool canStart,
        bool canPause,
        bool canStop,
        bool canResume)
    {
        try
        {
            var sink = global::PCMig.Core.Diagnostics.CoreDiagnostics.Sink;
            var publisher = sink.Publisher;
            if (!publisher.IsEnabledFor(UiEvents.StateObserved)) return;

            var owner = stateOwnerName switch
            {
                "Core" => StateOwner.Core,
                "Vm" => StateOwner.Vm,
                "View" => StateOwner.View,
                "StoredState" => StateOwner.StoredState,
                _ => StateOwner.Unknown,
            };

            publisher.TryPublish(new DiagnosticEventDraft(
                UiEvents.StateObserved,
                sink.Root(component),
                new UiStateObservedPayload(stateOwnerName, phase, isRunning, isPaused, canStart, canPause, canStop, canResume),
                Level: DiagnosticLevel.Debug,
                Outcome: DiagnosticOutcome.Succeeded,
                Phase: phase,
                StateOwner: owner));
        }
        catch (Exception)
        {
            // 观察失败绝不影响界面。
        }
    }
}