using System.Globalization;

namespace PCMig.Diagnostics.Abstractions;

/// <summary>
/// 诊断身份 ID。统一用 128-bit 随机 Guid（v4）：SessionId / ActionId / OperationId 同一机制。
/// **不要**用 4 位短 ID 当唯一键（显示可缩写，判定必须用全量）。
/// </summary>
public static class DiagnosticId
{
    public static Guid NewId() => Guid.NewGuid();

    public static Guid NewSessionId() => Guid.NewGuid();

    public static Guid NewActionId() => Guid.NewGuid();

    public static Guid NewOperationId() => Guid.NewGuid();

    /// <summary>落盘/比较用格式（32 位十六进制，无花括号）。</summary>
    public static string Format(Guid id) => id.ToString("N");

    /// <summary>只用于界面/日志展示的缩写（**不得**作为唯一键）。</summary>
    public static string Short(Guid id) => id.ToString("N").Substring(0, 8);

    public static bool TryParse(string? text, out Guid id) => Guid.TryParse(text, out id);
}

/// <summary>
/// 事件实例引用。事件**类型** ID（NET-001）不是证据；证据必须指向某一次具体事件
/// <c>(SessionId, Sequence)</c>。Incident 的 EvidenceRefs 一律用这个类型。
/// </summary>
public readonly record struct EventRef(Guid SessionId, long Sequence)
{
    public bool IsNone => SessionId == Guid.Empty || Sequence <= 0;

    public override string ToString() =>
        DiagnosticId.Short(SessionId) + "#" + Sequence.ToString(CultureInfo.InvariantCulture);
}