using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PCMig.Diagnostics.Abstractions;

namespace PCMig.Diagnostics;

/// <summary>会话元数据（session.json）。</summary>
public sealed record DiagnosticSessionMetadata
{
    public string SchemaVersion { get; init; } = "1.0";
    public required string SessionId { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public required long MonotonicFrequency { get; init; }
    public required long StartedMonotonicTicks { get; init; }
    public required string AppVersion { get; init; }
    public string? BuildId { get; init; }
    public required string RuntimeVersion { get; init; }
    public required string OsVersion { get; init; }
    public required string Architecture { get; init; }
    public required int ProcessId { get; init; }
    public required string ProcessIdentity { get; init; }
    public required string InitialMode { get; init; }
    public required string CatalogVersion { get; init; }
    public required string CatalogHash { get; init; }
    public required int EventCatalogCount { get; init; }
    public required string RedactionKeyId { get; init; }
    public required string StorageRoot { get; init; }
}

/// <summary>正常关闭标记（clean-shutdown.marker）。**只有真的排空并封段成功才允许写**。</summary>
public sealed record DiagnosticCleanShutdownMarker
{
    public string SchemaVersion { get; init; } = "1.0";
    public required string SessionId { get; init; }
    public required DateTimeOffset ShutdownUtc { get; init; }
    public required long DrainedEvents { get; init; }
    public required int SealedSegments { get; init; }
    public required bool FlushAcknowledged { get; init; }
    public required long LastSequence { get; init; }

    /// <summary>
    /// ★ D6.1 §3 ★ drain 成功 ≠ 本次会话从未丢失证据。
    /// 这里把**投递类分档的证据丢失世代**与是否存在故障性丢失一并写下来，
    /// 让"clean"只表示"关闭过程完整"，不再被误读成"证据完整"。
    /// </summary>
    public long EvidenceLossEpoch { get; init; }

    public bool AnyFaultLoss { get; init; }

    /// <summary>保留性淘汰（环覆盖/队列合并）——不是丢失，但可见。</summary>
    public long RetentionEvictions { get; init; }

    /// <summary>
    /// ★ D6.3 §7 ★ 关闭那一刻，诊断**自己**坏过多少次。
    /// 消费者吞掉事件 = 那条证据不存在；维护步骤抛异常 = 那一秒的判定没发生。
    /// 这些不是"业务没说清楚"，而是"记录者本身有故障"，所以必须与 clean 一起写下来。
    /// </summary>
    public long SinkFaults { get; init; }

    public long MaintenanceFaults { get; init; }
}

/// <summary>上次会话未确认正常关闭时得到的信息（**不等于"崩溃"**）。</summary>
public readonly record struct PreviousSessionInfo(
    Guid SessionId,
    string ReasonCode,
    DateTimeOffset? StartedUtc,
    long? LastKnownSequence,
    bool MarkerWriteFailed,
    int TailCompleteLines);

/// <summary>段封存清单（events-0001.manifest.json）。</summary>
public sealed record SegmentManifest
{
    public string SchemaVersion { get; init; } = "1.0";
    public required string Family { get; init; }
    public required string FileName { get; init; }
    public required long Length { get; init; }
    public required string Sha256 { get; init; }
    public required long EventCount { get; init; }
    public required long FirstSequence { get; init; }
    public required long LastSequence { get; init; }
    public required string FirstTimestampUtc { get; init; }
    public required string LastTimestampUtc { get; init; }
    public required bool Partial { get; init; }
    public required long CorruptLines { get; init; }
    public required string SealedUtc { get; init; }
}

/// <summary>活动段恢复结果（崩溃后重新打开时使用）。</summary>
public readonly record struct SegmentRecoveryResult(
    bool FileExisted,
    long CompleteLines,
    long TruncatedTailBytes,
    long CorruptLines,
    long LastSequence);

/// <summary>
/// 诊断存储布局与会话元数据（方案 §15/§21）。目录：
/// <code>
/// &lt;root&gt;\&lt;sessionId&gt;\
///     session.json          clean-shutdown.marker
///     catalog.json
///     events\events-0001.jsonl (+ .manifest.json)
///     metrics\  incidents\  flight\  snapshots\
/// </code>
/// 默认根目录 %LOCALAPPDATA%\PCMig\Diagnostics（当前用户权限，不要求管理员）。
/// </summary>
public sealed class DiagnosticSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public DiagnosticSessionStore(string root, Guid sessionId)
    {
        Root = root;
        SessionId = sessionId;
        SessionDir = Path.Combine(root, DiagnosticId.Format(sessionId));
        EventsDir = Path.Combine(SessionDir, "events");
        MetricsDir = Path.Combine(SessionDir, "metrics");
        IncidentsDir = Path.Combine(SessionDir, "incidents");
        FlightDir = Path.Combine(SessionDir, "flight");
        SnapshotsDir = Path.Combine(SessionDir, "snapshots");
        SessionFilePath = Path.Combine(SessionDir, "session.json");
        CatalogFilePath = Path.Combine(SessionDir, "catalog.json");
        CleanShutdownMarkerPath = Path.Combine(SessionDir, "clean-shutdown.marker");
    }

    public string Root { get; }
    public Guid SessionId { get; }
    public string SessionDir { get; }
    public string EventsDir { get; }
    public string MetricsDir { get; }
    public string IncidentsDir { get; }
    public string FlightDir { get; }
    public string SnapshotsDir { get; }
    public string SessionFilePath { get; }
    public string CatalogFilePath { get; }
    public string CleanShutdownMarkerPath { get; }

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCMig", "Diagnostics");

    /// <summary>创建目录结构（可能抛 IOException/UnauthorizedAccess ⇒ 由调用方 fail-open）。</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(SessionDir);
        Directory.CreateDirectory(EventsDir);
        Directory.CreateDirectory(MetricsDir);
        Directory.CreateDirectory(IncidentsDir);
        Directory.CreateDirectory(FlightDir);
        Directory.CreateDirectory(SnapshotsDir);
    }

    public string SegmentPath(string family, int index) =>
        Path.Combine(family == "events" ? EventsDir : Path.Combine(SessionDir, family), $"{family}-{index:0000}.jsonl");

    public string ManifestPath(string segmentPath) => segmentPath + ".manifest.json";

    public void WriteSessionMetadata(DiagnosticSessionMetadata metadata) => AtomicJson.Write(SessionFilePath, metadata, JsonOptions);

    public void WriteCatalog()
    {
        var descriptors = EventCatalog.All.Select(d => new
        {
            d.Code,
            d.EventId,
            d.Name,
            d.Version,
            Category = d.Category.ToString(),
            Level = d.Level.ToString(),
            Delivery = d.Delivery.ToString(),
            Privacy = d.Privacy.ToString(),
            PayloadName = d.PayloadName,
        }).ToArray();

        AtomicJson.Write(CatalogFilePath, new
        {
            schemaVersion = "1.0",
            catalogVersion = EventCatalog.CatalogVersion,
            catalogHash = EventCatalog.CatalogHash,
            eventCount = descriptors.Length,
            retainedEventIds = EventCatalog.RetiredEventIds,
            events = descriptors,
        }, JsonOptions);
    }

    public void WriteCleanShutdownMarker(DiagnosticCleanShutdownMarker marker) =>
        AtomicJson.Write(CleanShutdownMarkerPath, marker, JsonOptions);

    public bool CleanShutdownMarkerExists() => File.Exists(CleanShutdownMarkerPath);

    public void WriteSegmentManifest(string segmentPath, SegmentManifest manifest) =>
        WriteManifestFor(segmentPath, manifest);

    /// <summary>给任意段文件写清单（Flight 窗口文件也用同一形状；路径由调用方保证在诊断根内）。</summary>
    public static void WriteManifestFor(string segmentPath, SegmentManifest manifest) =>
        AtomicJson.Write(segmentPath + ".manifest.json", manifest, JsonOptions);

    public static DiagnosticSessionMetadata? TryReadSessionMetadata(string sessionFilePath)
    {
        try
        {
            if (!File.Exists(sessionFilePath)) return null;
            return JsonSerializer.Deserialize<DiagnosticSessionMetadata>(File.ReadAllText(sessionFilePath), JsonOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 找"上一次未确认正常关闭"的会话。判据顺序（方案 §21）：
    ///   ① 有 session.json 但**没有** clean marker ⇒ 未确认正常关闭；
    ///   ② 有 active 段且末行不完整 ⇒ 记为 tail 截断；
    ///   ③ session.json 本身读不出来 ⇒ 记为元数据损坏（不是"崩溃"结论）。
    /// 明确**不**回答"是不是崩溃"：用户强杀、断电、系统关机、诊断写失败都会是这个形态。
    /// </summary>
    public static PreviousSessionInfo? FindPreviousUncleanSession(string root, Guid currentSessionId, bool currentProcessIsOnlyInstance = true)
        => FindPreviousUncleanSession(root, currentSessionId, currentProcessIsOnlyInstance, out _);

    /// <summary>
    /// 同 <see cref="FindPreviousUncleanSession(string, Guid, bool)"/>，但把"**判定本身失败了**"
    /// 与"没有上一段会话"区分开：
    ///   · 返回 null 且 <paramref name="scanFailureReason"/> 为 null ⇒ 确实没有需要报告的上一段会话；
    ///   · 返回 null 且 <paramref name="scanFailureReason"/> 非 null ⇒ **读不出来**（例如另一实例仍持有
    ///     段文件句柄），此时调用方**不得**当作"上次会话一切正常"，必须如实说明判定失败。
    /// ★ D6.3 ★ 原实现把任何异常吞成 null，等于"证据读不到 ⇒ 什么都不说"（实测缺陷）。
    /// </summary>
    public static PreviousSessionInfo? FindPreviousUncleanSession(
        string root,
        Guid currentSessionId,
        bool currentProcessIsOnlyInstance,
        out string? scanFailureReason)
    {
        scanFailureReason = null;
        try
        {
            if (!Directory.Exists(root)) return null;
            var candidates = new List<(DateTimeOffset Started, string Dir, DiagnosticSessionMetadata? Meta, bool HasMarker, bool MetaCorrupt)>();

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (!Guid.TryParseExact(name, "N", out var id)) continue;
                if (id == currentSessionId) continue;

                var sessionFile = Path.Combine(dir, "session.json");
                var meta = TryReadSessionMetadata(sessionFile);
                var hasMarker = File.Exists(Path.Combine(dir, "clean-shutdown.marker"));
                var metaCorrupt = meta is null && File.Exists(sessionFile);
                if (meta is null && !metaCorrupt && !hasMarker && !Directory.EnumerateFileSystemEntries(dir).Any())
                    continue; // 空目录（上一次启动失败留下的骨架）：不是证据

                candidates.Add((meta?.StartedUtc ?? DateTimeOffset.MinValue, dir, meta, hasMarker, metaCorrupt));
            }

            // 只看最近的一个候选（更早的未确认关闭会让"上次"指错会话）。
            var newest = candidates.OrderByDescending(c => c.Started).FirstOrDefault();
            if (newest.Dir is null) return null;
            if (newest.HasMarker && !newest.MetaCorrupt) return null;

            var reason = newest.MetaCorrupt ? "session-metadata-corrupt"
                : newest.HasMarker ? "marker-present" // 理论不可达（上面已 return）
                : "no-clean-marker";

            var tailComplete = 0;
            if (newest.Meta is not null)
            {
                var eventsDir = Path.Combine(newest.Dir, "events");
                if (Directory.Exists(eventsDir))
                {
                    var active = Directory.EnumerateFiles(eventsDir, "*.jsonl")
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();
                    if (active is not null)
                    {
                        var recovery = SegmentRecovery.RecoverActive(active, truncate: false);
                        tailComplete = (int)Math.Min(int.MaxValue, recovery.CompleteLines);
                    }
                }
            }

            _ = currentProcessIsOnlyInstance; // 多实例判定由调用方提供（PID+启动时间），这里不做进程探测

            return new PreviousSessionInfo(
                newest.Meta is not null && Guid.TryParseExact(newest.Meta.SessionId, "N", out var parsed) ? parsed : Guid.Empty,
                reason,
                newest.Meta?.StartedUtc,
                null,
                MarkerWriteFailed: false,
                TailCompleteLines: tailComplete);
        }
        catch (Exception ex)
        {
            // ★ D6.3 ★ 不静默：如实记下"判定失败"的原因码，交给调用方发布
            //（异常类型足够定位，不带任何路径）。
            scanFailureReason = "previous-session-scan-failed:" + ex.GetType().Name;
            return null;
        }
    }
}

/// <summary>
/// 原子 JSON 写（临时文件 + rename）。**与 Core 的 JsonStateStore 无关**：
/// 诊断运行时不得引用 Core，也不得复用它的语义（那是业务状态存储）。
/// 注意：rename 只保证"要么旧要么新"，**不保证掉电后一定落盘**。
/// </summary>
internal static class AtomicJson
{
    public static void Write<T>(string path, T value, JsonSerializerOptions options)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        try
        {
            var json = JsonSerializer.Serialize(value, options);
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 残件不影响正确性 */ }
        }
    }
}