using System.Text;
using PCMig.Diagnostics.Abstractions;

namespace PCMig.Diagnostics;

/// <summary>
/// 事件字节估算（D6.1 §6 修正版）。**故意仍然是估算**：为了预算闸门而做的廉价求和，
/// **不做序列化**（序列化只发生在 writer 分支的消费侧，绝不在生产线程上做）。
///
/// 与旧实现的差别（这是实测出的 178 倍低估的来源）：
///   · 字符串按**真实 UTF-8 字节数**计，不再用 `string.Length`（UTF-16 码元数）；
///   · payload 不再一律按 512 B：由 <see cref="PayloadSizeEstimator"/> 按**当前字段值**推导上界，
///     因此大载荷无法绕过字节预算；
///   · path/令牌等仍给固定余量，保持保守。
/// </summary>
public static class EventSizeEstimator
{
    private const int EnvelopeBase = 224;

    public static int Estimate(in DiagnosticEvent e)
    {
        var size = EnvelopeBase;
        size += Bytes(e.Message);
        size += Bytes(e.JobId);
        size += Bytes(e.ObjectId);
        size += Bytes(e.Component);
        size += Bytes(e.Phase);
        size += Bytes(e.ExceptionType);
        size += Bytes(e.CorrelationId);
        size += Bytes(e.Pass);
        size += Bytes(e.ControlId);
        size += Bytes(e.SchemaVersion);
        if (e.Path is not null) size += Bytes(e.Path.PathToken) + Bytes(e.Path.RootAlias) + 48;
        if (e.Payload is not null) size += PayloadSizeEstimator.Estimate(e.Payload);
        if (e.UnknownTokens is { Count: > 0 })
        {
            for (var i = 0; i < e.UnknownTokens.Count; i++) size += Bytes(e.UnknownTokens[i]);
        }
        return size;
    }

    /// <summary>真实 UTF-8 字节数（不是 UTF-16 码元数）。</summary>
    private static int Bytes(string? value) =>
        string.IsNullOrEmpty(value) ? 0 : Encoding.UTF8.GetByteCount(value);
}