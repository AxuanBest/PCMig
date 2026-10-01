using System.Diagnostics;

namespace PCMig.Diagnostics;

/// <summary>
/// ActivitySource 单例（方案 §18）。**跨度名称**是语义（Action/Connection/Transfer…），
/// 不为每个操作创建新的 ActivitySource（官方最佳实践：ActivitySource 昂贵且应复用）。
///
/// 关键边界：
///   · 没有 listener 时 <c>StartActivity</c> 会返回 null ⇒ 调用点必须容忍 Activity 为 null；
///   · TraceId/SpanId 只是**补充**关联，PCMig 自己的 ActionId/OperationId/JobId 才是业务身份；
///   · 不引入 OTel SDK/Collector：这里只提供 API 接入点。
/// </summary>
public static class DiagnosticActivities
{
    public const string Version = "1.0.0";

    public static readonly ActivitySource Core = new("PCMig.Core", Version);
    public static readonly ActivitySource Ui = new("PCMig.UI", Version);
    public static readonly ActivitySource Diagnostics = new("PCMig.Diagnostics", Version);

    /// <summary>当前 Activity 的 W3C 标识（无 Activity ⇒ 全 null，**不伪造**）。</summary>
    public static (string? TraceId, string? SpanId, string? ParentSpanId) Current()
    {
        var activity = Activity.Current;
        if (activity is null) return (null, null, null);

        var traceId = activity.TraceId.ToHexString();
        var spanId = activity.SpanId.ToHexString();
        var parent = activity.ParentSpanId.ToHexString();
        // 默认（全 0）表示没有父：不要写出一个假的父 span id。
        if (string.IsNullOrEmpty(traceId) || traceId == "00000000000000000000000000000000") traceId = null;
        if (string.IsNullOrEmpty(spanId) || spanId == "0000000000000000") spanId = null;
        if (string.IsNullOrEmpty(parent) || parent == "0000000000000000") parent = null;
        return (traceId, spanId, parent);
    }

    /// <summary>
    /// 开始一个跨度。无 listener 时返回 null（**这是合法结果**，调用点不得假定非 null）。
    /// 只应在确实有语义的边界处调用（不要在每文件/每轮 poll 上建 span）。
    /// </summary>
    public static Activity? Start(ActivitySource source, string spanName, ActivityKind kind = ActivityKind.Internal)
        => source.StartActivity(spanName, kind);
}