namespace PCMig.Diagnostics.Abstractions;

/// <summary>
/// 生产线程唯一的发布入口。纪律（架构方案 §16 / §29）：
///   · <see cref="TryPublish"/> **只报"是否被接受"**，不做等待：不写盘、不序列化、不碰 UI、不等 analyzer；
///   · **绝不抛异常**：任何内部故障必须转成"未接受 + 自身 health 计数"，业务路径不得因诊断失败而改变；
///   · 热路径先用 <see cref="IsEnabledFor"/> 判断再构造 payload（OFF 路径必须零 payload 构造）。
/// </summary>
public interface IDiagnosticPublisher
{
    /// <summary>是否处于任何采集模式（Off ⇒ 恒 false）。</summary>
    bool IsEnabled { get; }

    CaptureMode Mode { get; }

    /// <summary>按级别/投递类/模式判断这条事件是否会被接受（用于"先判断再构造 payload"）。</summary>
    bool IsEnabledFor(EventDescriptor descriptor);

    /// <summary>
    /// 发布一条事件。返回 false = 未被接受（诊断关闭、被过滤、队列满、已降级）。
    /// **不接受不等于业务失败**；调用点只有在需要精确 loss 口径时才使用返回值。
    /// </summary>
    bool TryPublish(in DiagnosticEventDraft draft);
}

/// <summary>
/// 关闭态发布器：诊断系统未装配时的默认实现（Core/WinUI 旧 shell 全部走它）。
/// 行为契约：IsEnabled=false、Mode=Off、TryPublish 恒 false、任何输入都不抛。
/// </summary>
public sealed class NoOpDiagnosticPublisher : IDiagnosticPublisher
{
    public static readonly NoOpDiagnosticPublisher Instance = new();

    private NoOpDiagnosticPublisher() { }

    public bool IsEnabled => false;

    public CaptureMode Mode => CaptureMode.Off;

    public bool IsEnabledFor(EventDescriptor descriptor) => false;

    public bool TryPublish(in DiagnosticEventDraft draft) => false;
}