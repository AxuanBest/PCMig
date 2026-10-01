using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PCMig.Diagnostics.Abstractions.Events;

namespace PCMig.Diagnostics.Abstractions;

/// <summary>
/// 稳定事件目录（EventCatalog）。它是"事件类型"的唯一事实源：
///   · 启动时把 <see cref="CatalogVersion"/>/<see cref="CatalogHash"/> 写进 session.json 与 catalog.json，
///     使离线诊断包能证明"这份事件是按哪一版 catalog 解释的"；
///   · 提供 code / EventId / name 三种查找（规则用强类型字段引用，不用字符串查找）；
///   · EventId 唯一性在静态构造里**立即校验**（重复登记是开发期错误，宁可启动即炸，也不静默串号）。
/// </summary>
public static class EventCatalog
{
    /// <summary>目录协议版本（与 envelope SchemaVersion 独立：这里描述事件集合本身）。</summary>
    public const string CatalogVersion = "1.0";

    /// <summary>
    /// 已废弃且**永久不得复用**的 EventId。将来删除事件时把编号移到这里（只增不减），
    /// 由单元测试保证它不与在用编号相交。
    ///
    /// ★ 声明顺序有语义 ★ 静态字段初始化按**文本顺序**执行：本字段必须排在
    /// <see cref="Descriptors"/> 之前，否则 BuildAll() 读到的是 null（实测踩过：
    /// TypeInitializationException → Intersect(second: null)）。
    /// </summary>
    private static readonly int[] Retired = Array.Empty<int>();

    private static readonly EventDescriptor[] Descriptors = BuildAll();

    private static readonly Dictionary<string, EventDescriptor> ByCodeIndex =
        Descriptors.ToDictionary(d => d.Code, StringComparer.Ordinal);

    private static readonly Dictionary<int, EventDescriptor> ByEventIdIndex =
        Descriptors.ToDictionary(d => d.EventId);

    private static readonly Dictionary<string, EventDescriptor> ByNameIndex =
        Descriptors.ToDictionary(d => d.Name, StringComparer.Ordinal);

    /// <summary>全部已登记事件，按 EventId 升序（顺序稳定 ⇒ CatalogHash 稳定）。</summary>
    public static IReadOnlyList<EventDescriptor> All => Descriptors;

    public static int Count => Descriptors.Length;

    public static IReadOnlyList<int> RetiredEventIds => Retired;

    /// <summary>catalog 内容哈希（sha256 hex，小写）：行格式 CODE|EVENTID|NAME|VERSION|CATEGORY|LEVEL|DELIVERY|PRIVACY|PAYLOAD。</summary>
    public static string CatalogHash { get; } = ComputeHash();

    public static bool TryGetByCode(string code, out EventDescriptor descriptor)
    {
        if (ByCodeIndex.TryGetValue(code, out var found))
        {
            descriptor = found;
            return true;
        }
        descriptor = null!;
        return false;
    }

    public static bool TryGetByEventId(int eventId, out EventDescriptor descriptor)
    {
        if (ByEventIdIndex.TryGetValue(eventId, out var found))
        {
            descriptor = found;
            return true;
        }
        descriptor = null!;
        return false;
    }

    public static bool TryGetByName(string name, out EventDescriptor descriptor)
    {
        if (ByNameIndex.TryGetValue(name, out var found))
        {
            descriptor = found;
            return true;
        }
        descriptor = null!;
        return false;
    }

    /// <summary>按 code 取（只在启动/配置路径使用；热路径一律用强类型字段）。</summary>
    public static EventDescriptor GetByCode(string code) =>
        TryGetByCode(code, out var d) ? d : throw new KeyNotFoundException("catalog 里没有事件代码：" + code);

    private static EventDescriptor[] BuildAll()
    {
        var all = new List<EventDescriptor>();
        all.AddRange(AppEvents.All);
        all.AddRange(UiEvents.All);
        all.AddRange(NetEvents.All);
        all.AddRange(FsEvents.All);
        all.AddRange(PlanEvents.All);
        all.AddRange(PreflightEvents.All);
        all.AddRange(TransferEvents.All);
        all.AddRange(RobocopyEvents.All);
        all.AddRange(PersistenceEvents.All);
        all.AddRange(VerifyEvents.All);
        all.AddRange(RepairEvents.All);
        all.AddRange(DiagnosticsEvents.All);

        var ordered = all.OrderBy(d => d.EventId).ToArray();

        // EventId / Code / Name 三者都必须唯一。重复 = 有人在两处登记了同一编号，
        // 静默通过的话会让规则把两件不同的事当成同一件。
        var duplicateIds = ordered.GroupBy(d => d.EventId).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        if (duplicateIds.Length > 0)
            throw new InvalidOperationException("EventCatalog 出现重复 EventId：" + string.Join(",", duplicateIds));

        var duplicateCodes = ordered.GroupBy(d => d.Code, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        if (duplicateCodes.Length > 0)
            throw new InvalidOperationException("EventCatalog 出现重复 Code：" + string.Join(",", duplicateCodes));

        var duplicateNames = ordered.GroupBy(d => d.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        if (duplicateNames.Length > 0)
            throw new InvalidOperationException("EventCatalog 出现重复 Name：" + string.Join(",", duplicateNames));

        var reused = ordered.Select(d => d.EventId).Intersect(Retired).ToArray();
        if (reused.Length > 0)
            throw new InvalidOperationException("EventId 被复用（已废弃编号不得再用）：" + string.Join(",", reused));

        return ordered;
    }

    private static string ComputeHash()
    {
        var sb = new StringBuilder();
        foreach (var d in Descriptors)
        {
            sb.Append(d.Code).Append('|')
              .Append(d.EventId.ToString(CultureInfo.InvariantCulture)).Append('|')
              .Append(d.Name).Append('|')
              .Append(d.Version.ToString(CultureInfo.InvariantCulture)).Append('|')
              .Append(d.Category).Append('|')
              .Append(d.Level).Append('|')
              .Append(d.Delivery).Append('|')
              .Append(d.Privacy).Append('|')
              .Append(d.PayloadName ?? "-")
              .Append('\n');
        }
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}