namespace PCMig.Diagnostics.Abstractions;

/// <summary>
/// 当前用户语义动作的作用域（**UI 无关**，因此 Presentation 层也能用）。
///
/// 为什么放在契约层：动作链要跨"页面 → VM → Core"三层共用同一个 ActionId，
/// 而 Presentation 源码会被既有测试项目**按文件链入**编译（不得依赖 WinUI/诊断运行时）。
/// 因此这个"当前动作"的载体必须是 UI 无关的：WinUI 侧由 ActionTrace 进入作用域，
/// VM/Core 侧的观察点只读它的 ActionId/ControlId。
///
/// 纪律（方案 §8）：
///   · AsyncLocal 只是**便捷作用域**，不是唯一事实源 —— 跨线程长期任务必须显式传 context；
///   · 它只携带身份（ActionId/ControlId/Component），不携带任何业务状态；
///   · 观察点即使拿不到作用域也必须能工作（退化为组件级上下文）。
/// </summary>
public static class ActionScope
{
    private static readonly AsyncLocal<Frame?> Ambient = new();

    public static Frame? Current => Ambient.Value;

    /// <summary>进入一个动作作用域；Dispose 即离开（嵌套时还原到上一层）。</summary>
    public static IDisposable Enter(Guid actionId, string? controlId, string component)
    {
        var previous = Ambient.Value;
        var frame = new Frame(actionId, controlId, component);
        Ambient.Value = frame;
        return new Scope(previous);
    }

    /// <summary>当前动作身份（拿不到就返回 null）。</summary>
    public static Guid? CurrentActionId => Ambient.Value?.ActionId;

    public static string? CurrentControlId => Ambient.Value?.ControlId;

    public sealed class Frame
    {
        internal Frame(Guid actionId, string? controlId, string component)
        {
            ActionId = actionId;
            ControlId = controlId;
            Component = component;
        }

        public Guid ActionId { get; }

        public string? ControlId { get; }

        public string Component { get; }
    }

    private sealed class Scope : IDisposable
    {
        private readonly Frame? _previous;
        private int _disposed;

        public Scope(Frame? previous) => _previous = previous;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            Ambient.Value = _previous;
        }
    }
}