namespace PCMig.Diagnostics.Abstractions;

/// <summary>
/// 事件类型描述符：**EventId 是永久契约**。
///
/// 纪律（架构方案 §7）：
///   · EventId 一旦发布**禁止复用**（废弃只能留 tombstone，见 EventCatalog.RetiredEventIds）；
///   · Code / EventId / Name 三者 1:1，由 <see cref="Define"/> 统一计算，杜绝手写字面量漂移；
///   · payload 变化必须升 <see cref="Version"/>；语义变化优先新增 EventId 而不是复用旧的；
///   · Privacy=Secret 直接拒绝（Secret 永远不得进入 DiagnosticEvent）。
/// </summary>
public sealed record EventDescriptor
{
    public required DiagnosticCategory Category { get; init; }

    /// <summary>族内序号 1..999。</summary>
    public required int Ordinal { get; init; }

    /// <summary>稳定代码，例如 UI-001。</summary>
    public required string Code { get; init; }

    /// <summary>稳定整数 ID，例如 2001（= 族基数 2000 + 序号 1）。</summary>
    public required int EventId { get; init; }

    /// <summary>稳定英文名，例如 UI.UserActionObserved。**不是**给人看的中文文案。</summary>
    public required string Name { get; init; }

    /// <summary>该事件 payload 的版本（payload 形状变化时递增）。</summary>
    public required int Version { get; init; }

    public required DiagnosticLevel Level { get; init; }

    public required DeliveryClass Delivery { get; init; }

    public required PrivacyClassification Privacy { get; init; }

    /// <summary>强类型 payload 名（与 codec 注册名一致）；null = 该事件当前无 payload（尚未实现的观察点在后续阶段补齐）。</summary>
    public string? PayloadName { get; init; }

    /// <summary>false = 反序列化时未能在 catalog 中匹配（未知事件）：可展示、可透传，但规则必须跳过。</summary>
    public bool IsKnown => Category != DiagnosticCategory.Unknown;

    public const string UnknownCode = "UNK-000";

    /// <summary>登记一个新的（已知）事件类型。参数非法时**立即抛出**（这是开发期错误，不是运行期路径）。</summary>
    public static EventDescriptor Define(
        DiagnosticCategory category,
        int ordinal,
        string name,
        DiagnosticLevel level,
        DeliveryClass delivery,
        PrivacyClassification privacy,
        string? payloadName = null,
        int version = 1)
    {
        if (category == DiagnosticCategory.Unknown)
            throw new ArgumentException("已知事件不得使用 Unknown 族（它专供反序列化未匹配事件）", nameof(category));
        if (ordinal is < 1 or > 999)
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "族内序号必须在 1..999");
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("事件名不得为空", nameof(name));
        if (privacy == PrivacyClassification.Secret)
            throw new ArgumentException(
                "Secret 分类的事件不得登记：Secret 永远不得进入 DiagnosticEvent（入口即阻断，不做事后清洗）", nameof(privacy));
        if (version < 1)
            throw new ArgumentOutOfRangeException(nameof(version), version, "payload 版本必须 >= 1");
        if (payloadName is not null && string.IsNullOrWhiteSpace(payloadName))
            throw new ArgumentException("payloadName 要么为 null，要么是非空稳定名", nameof(payloadName));

        var prefix = category.Prefix();
        if (!name.StartsWith(prefix + ".", StringComparison.Ordinal))
            throw new ArgumentException($"事件名必须以 \"{prefix}.\" 开头（族前缀与代码段必须一致），实际：{name}", nameof(name));

        return new EventDescriptor
        {
            Category = category,
            Ordinal = ordinal,
            Code = prefix + "-" + ordinal.ToString("000", System.Globalization.CultureInfo.InvariantCulture),
            EventId = category.EventIdBase() + ordinal,
            Name = name,
            Version = version,
            Level = level,
            Delivery = delivery,
            Privacy = privacy,
            PayloadName = payloadName,
        };
    }

    /// <summary>
    /// 未知事件（反序列化时 catalog 里没有这个 EventId）：**保留原始 id/name，不伪造归属**。
    /// 规则必须跳过 IsKnown=false 的事件，绝不允许拿它当"已匹配版本"的证据。
    /// </summary>
    public static EventDescriptor Unknown(int eventId, string? code, string? name) => new()
    {
        Category = DiagnosticCategory.Unknown,
        Ordinal = 0,
        Code = string.IsNullOrWhiteSpace(code) ? UnknownCode : code!,
        EventId = eventId,
        Name = string.IsNullOrWhiteSpace(name) ? "UNK.Unknown" : name!,
        Version = 0,
        Level = DiagnosticLevel.Information,
        Delivery = DeliveryClass.Verbose,
        Privacy = PrivacyClassification.Public,
        PayloadName = null,
    };
}