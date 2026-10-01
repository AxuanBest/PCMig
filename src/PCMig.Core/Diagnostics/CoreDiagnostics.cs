using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;

namespace PCMig.Core.Diagnostics;

/// <summary>
/// Core 侧诊断接入点（**装配根显式安装，Core 默认 NoOp**）。
///
/// 为什么是"环境式"接入而不是给每个类加构造参数：
///   · Core 的多数类型由业务代码直接 new（Planner/Verifier/RobocopyRunner/…），
///     JsonStateStore 还是静态类；逐个改构造签名会把一次"只观察"的改动放大成大规模重构；
///   · 方案 §29 明确："Core 默认 NoOp 用于旧 shell 兼容，装配根显式提供 publisher"；
///   · 风险由两条纪律兜住：① 未安装 = <see cref="NoOpDiagnosticSink"/>（零分配、绝不抛）；
///     ② 安装只发生在装配根（WinUI App 启动 / 测试），Core 自己永远不去"找"诊断系统。
///
/// 纪律：Core 绝不允许用"全局当前任务"猜上下文；需要 Job/Object 归属的地方必须显式传入不可变 context。
/// </summary>
public static class CoreDiagnostics
{
    private static IDiagnosticSink _sink = NoOpDiagnosticSink.Instance;

    /// <summary>当前 sink（未安装时为 NoOp）。</summary>
    public static IDiagnosticSink Sink => Volatile.Read(ref _sink);

    /// <summary>诊断是否处于采集状态（热路径先判这个再构造载荷）。</summary>
    public static bool Enabled => Sink.Publisher.IsEnabled;

    /// <summary>安装 sink 并返回"还原"句柄（用于测试与装配根的受限作用域）。</summary>
    public static IDisposable Install(IDiagnosticSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var previous = Interlocked.Exchange(ref _sink, sink);
        return new Scope(previous);
    }

    /// <summary>还原为 NoOp（测试清理用）。</summary>
    public static void Reset() => Interlocked.Exchange(ref _sink, NoOpDiagnosticSink.Instance);

    /// <summary>取本会话的根上下文（component 用稳定短名）。</summary>
    public static DiagnosticContext ContextFor(string component) => Sink.Root(component);

    /// <summary>取本会话的根上下文并附带 Job 归属（**显式传入，不猜全局状态**）。</summary>
    public static DiagnosticContext ContextFor(string component, string? jobId) =>
        jobId is null ? Sink.Root(component) : Sink.Root(component).WithJob(jobId);

    /// <summary>
    /// 发布一条"用户动作链"事件（UI-00x）。**UI 无关**：Presentation 层可以直接用
    /// （它会被既有测试项目按文件链入编译，因此不能依赖 WinUI 侧类型）。
    ///
    /// 自动继承 <see cref="ActionScope"/> 里的 ActionId/ControlId ⇒ 页面、VM、Core 的观察点
    /// 共用同一个动作身份，因果链才能重建。
    /// </summary>
    public static void PublishUiAction(
        EventDescriptor descriptor,
        IDiagnosticPayload? payload,
        DiagnosticOutcome outcome,
        DiagnosticLevel level,
        string component,
        string? exceptionType = null,
        int? hresult = null,
        int? durationMs = null,
        string? phase = null)
    {
        try
        {
            var sink = Sink;
            var publisher = sink.Publisher;
            if (!publisher.IsEnabledFor(descriptor)) return;

            var context = sink.Root(component);
            if (ActionScope.Current is { } scope)
                context = context.WithAction(scope.ActionId).WithControl(scope.ControlId);

            publisher.TryPublish(new DiagnosticEventDraft(
                descriptor,
                context,
                payload,
                Level: level,
                Outcome: outcome,
                ExceptionType: exceptionType,
                HResult: hresult,
                ErrorDomain: hresult is null ? ErrorDomain.None : ErrorDomain.Managed,
                DurationMs: durationMs,
                Phase: phase,
                StateOwner: StateOwner.Vm));
        }
        catch (Exception)
        {
            // 观察失败绝不影响业务。
        }
    }

    /// <summary>
    /// 发布一条 **Core 侧**观察事件（D6.1 §16 的通用入口）。
    ///
    /// 与 <see cref="PublishUiAction"/> 的区别：StateOwner 默认是 Core（证据来自业务层而不是 VM），
    /// 但同样自动继承 <see cref="ActionScope"/> 的动作身份，使"UI 动作 → Core 执行"能连成一条链。
    /// 纪律不变：纯观察，异常一律吞掉，绝不影响业务。
    /// </summary>
    public static void PublishCore(
        EventDescriptor descriptor,
        IDiagnosticPayload? payload,
        DiagnosticContext context,
        DiagnosticLevel level,
        DiagnosticOutcome outcome,
        string component,
        StateOwner stateOwner = StateOwner.Core,
        string? phase = null,
        string? exceptionType = null,
        int? win32Error = null,
        ErrorDomain errorDomain = ErrorDomain.None,
        PathRef? path = null)
    {
        try
        {
            var sink = Sink;
            var publisher = sink.Publisher;
            if (!publisher.IsEnabledFor(descriptor)) return;

            var effective = context.WithComponent(component);
            if (ActionScope.Current is { } scope)
                effective = effective.WithAction(scope.ActionId).WithControl(scope.ControlId);

            publisher.TryPublish(new DiagnosticEventDraft(
                descriptor,
                effective,
                payload,
                Level: level,
                Outcome: outcome,
                ExceptionType: exceptionType,
                Win32Error: win32Error,
                ErrorDomain: errorDomain,
                Phase: phase,
                Path: path,
                StateOwner: stateOwner));
        }
        catch (Exception)
        {
            // 观察失败绝不影响业务。
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly IDiagnosticSink _previous;
        private int _disposed;

        public Scope(IDiagnosticSink previous) => _previous = previous;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            Interlocked.Exchange(ref _sink, _previous);
        }
    }
}