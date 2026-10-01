namespace PCMig.Diagnostics.Abstractions;

/// <summary>
/// 脱敏后的路径引用。**契约层只定义形状，不做脱敏**：把原始路径变成 PathRef 是 runtime
/// 的 RedactionPolicy 的职责（HMAC + alias），调用点不得自己拼一个 "C:\Users\x\..." 塞进来。
///
/// 默认字段含义（架构方案 §20）：
///   Role          路径在业务里的角色（源/目标/任务目录/子进程镜像…）
///   RootKind      根的类型（"Unc" / "Drive" / "LocalAppData" / "Unknown"）
///   RootAlias     根的稳定别名（例如 "src" / "dst" / "job"），不是真实机器名
///   PathToken     HMAC 令牌或占位符（**不是**明文，也不是无盐 SHA256）
///   KeyId/Scope   令牌所用密钥的标识与作用域（导出时换新 key ⇒ 新 KeyId）
///   ExtensionClass 扩展名粗分类（扩展名本身也可能敏感，故按白名单粗分）
///   DepthBucket   目录深度桶（避免暴露真实层级）
/// </summary>
public sealed record PathRef
{
    public required PathRole Role { get; init; }

    public required string RootKind { get; init; }

    public string? RootAlias { get; init; }

    public required string PathToken { get; init; }

    public string? KeyId { get; init; }

    public string? Scope { get; init; }

    public string? ExtensionClass { get; init; }

    public int? DepthBucket { get; init; }

    /// <summary>取不到路径事实时使用（例如异常里没有路径）。</summary>
    public static PathRef Unavailable(PathRole role) => new()
    {
        Role = role,
        RootKind = "Unknown",
        PathToken = "[unavailable]",
    };

    /// <summary>已知根但未能脱敏（策略缺失/redactor 失败）时的显式占位——**绝不回退明文**。</summary>
    public static PathRef TokenUnavailable(PathRole role, string rootKind, string? rootAlias = null) => new()
    {
        Role = role,
        RootKind = rootKind,
        RootAlias = rootAlias,
        PathToken = "[token-unavailable]",
    };
}