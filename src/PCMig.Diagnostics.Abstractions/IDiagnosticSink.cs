namespace PCMig.Diagnostics.Abstractions;

/// <summary>
/// 生产代码看到的**唯一**诊断入口。它把三件生产者真正需要的能力打包在一起：
///   ① 发布（<see cref="Publisher"/>）；
///   ② 拿到本会话的根上下文（<see cref="Root"/>：生产者不认识 SessionId，只有运行时候知道）；
///   ③ 造脱敏路径/令牌（<see cref="PathRef"/> / <see cref="Token"/>：脱敏策略属于 runtime，
///      绝不允许 Core/WinUI 自己拼一个明文路径塞进事件）。
///
/// 依赖方向（方案 §29）：Core / WinUI 只依赖这个接口 + 契约层，**不认识运行时**。
/// 未装配时一律是 <see cref="NoOpDiagnosticSink"/>：IsEnabled=false、零分配、绝不抛。
/// </summary>
public interface IDiagnosticSink
{
    /// <summary>本会话 ID（未装配时为 Guid.Empty）。</summary>
    Guid SessionId { get; }

    IDiagnosticPublisher Publisher { get; }

    /// <summary>本会话的根上下文（component 是稳定短名，例如 JsonStateStore）。</summary>
    DiagnosticContext Root(string? component = null);

    /// <summary>路径 → 脱敏引用（不做文件系统访问）。</summary>
    PathRef PathRef(string? path, PathRole role, string? rootAlias = null);

    /// <summary>任意可识别字符串（主机名/IP/用户名）→ 会话内稳定令牌。</summary>
    string Token(string? value);
}

/// <summary>关闭态 sink：诊断未装配时的默认实现。</summary>
public sealed class NoOpDiagnosticSink : IDiagnosticSink
{
    public static readonly NoOpDiagnosticSink Instance = new();

    private NoOpDiagnosticSink() { }

    public Guid SessionId => Guid.Empty;

    public IDiagnosticPublisher Publisher => NoOpDiagnosticPublisher.Instance;

    public DiagnosticContext Root(string? component = null) =>
        DiagnosticContext.Root(Guid.Empty, component);

    // ★ 注意 ★ 方法名与类型同名 ⇒ 必须写全限定名，否则编译器把 PathRef 解析成这个方法本身。
    public PathRef PathRef(string? path, PathRole role, string? rootAlias = null)
        => PCMig.Diagnostics.Abstractions.PathRef.Unavailable(role);

    public string Token(string? value) => "[noop]";
}