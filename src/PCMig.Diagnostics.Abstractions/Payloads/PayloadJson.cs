using System.Text.Json;

namespace PCMig.Diagnostics.Abstractions.Payloads;

/// <summary>
/// payload 序列化的共享小工具：统一"null ⇒ 不写该属性"的口径，
/// 避免每个 payload 各写一套 null 判断（也避免写出 null 值污染 JSONL）。
/// </summary>
public static class PayloadJson
{
    public static void WriteStringOrNull(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) return;
        writer.WriteString(name, value);
    }

    public static void WriteNumberOrNull(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is null) return;
        writer.WriteNumber(name, value.Value);
    }

    public static void WriteNumberOrNull(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is null) return;
        writer.WriteNumber(name, value.Value);
    }

    public static void WriteBoolOrNull(Utf8JsonWriter writer, string name, bool? value)
    {
        if (value is null) return;
        writer.WriteBoolean(name, value.Value);
    }

    // ---- 读取侧（宽容：缺字段取默认值，未知字段忽略；**不抛**）----

    public static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    public static string StrOr(JsonElement element, string name, string fallback) => Str(element, name) ?? fallback;

    public static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : null;

    public static int IntOr(JsonElement element, string name, int fallback) => Int(element, name) ?? fallback;

    public static long? Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v) ? v : null;

    public static long LongOr(JsonElement element, string name, long fallback) => Long(element, name) ?? fallback;

    public static bool? Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? p.GetBoolean()
            : null;

    public static bool BoolOr(JsonElement element, string name, bool fallback) => Bool(element, name) ?? fallback;

    public static double? Double(JsonElement element, string name) =>
        element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var v) ? v : null;

    public static double DoubleOr(JsonElement element, string name, double fallback) => Double(element, name) ?? fallback;

    public static DateTimeOffset? Time(JsonElement element, string name) =>
        DateTimeOffset.TryParse(Str(element, name), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var v) ? v : null;

    public static Guid? GuidOf(JsonElement element, string name) =>
        Guid.TryParse(Str(element, name), out var v) ? v : null;

    public static Guid GuidOrEmpty(JsonElement element, string name) => GuidOf(element, name) ?? Guid.Empty;

    /// <summary>枚举 token 读取：只接受已定义的枚举名，**未知 token 返回 null 交给调用方保留 raw**。</summary>
    public static TEnum? Enum<TEnum>(JsonElement element, string name) where TEnum : struct, Enum
    {
        var text = Str(element, name);
        if (string.IsNullOrEmpty(text)) return null;
        return System.Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsed) ? parsed : null;
    }

    public static TEnum EnumOr<TEnum>(JsonElement element, string name, TEnum fallback) where TEnum : struct, Enum =>
        Enum<TEnum>(element, name) ?? fallback;

    // ---- 嵌套结构：PathRef（多个 payload 共用，避免每处重复 8 行读写）----

    public static void WritePath(Utf8JsonWriter writer, string name, PathRef path)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteString("role", path.Role.ToString());
        writer.WriteString("rootKind", path.RootKind);
        WriteStringOrNull(writer, "rootAlias", path.RootAlias);
        writer.WriteString("pathToken", path.PathToken);
        WriteStringOrNull(writer, "keyId", path.KeyId);
        WriteStringOrNull(writer, "scope", path.Scope);
        WriteStringOrNull(writer, "extensionClass", path.ExtensionClass);
        WriteNumberOrNull(writer, "depthBucket", path.DepthBucket);
        writer.WriteEndObject();
    }

    public static PathRef? ReadPath(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Object) return null;
        return new PathRef
        {
            Role = EnumOr(p, "role", PathRole.Unknown),
            RootKind = StrOr(p, "rootKind", "Unknown"),
            RootAlias = Str(p, "rootAlias"),
            PathToken = StrOr(p, "pathToken", "[unavailable]"),
            KeyId = Str(p, "keyId"),
            Scope = Str(p, "scope"),
            ExtensionClass = Str(p, "extensionClass"),
            DepthBucket = Int(p, "depthBucket"),
        };
    }
}