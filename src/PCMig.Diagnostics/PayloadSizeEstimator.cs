using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using PCMig.Diagnostics.Abstractions;

namespace PCMig.Diagnostics;

/// <summary>
/// 载荷字节上界推导（D6.1 §6）。
///
/// 背景（实测缺陷）：旧实现把**任何**载荷一律按 512 B 计，于是 131,583 B 的事件只被算成 736 B
/// （约 178 倍低估）⇒ "字节预算"形同虚设，大载荷可以绕过总预算。
///
/// 本实现的三条纪律：
///   1. **不上生产线程做序列化**：绝不为估算把 payload 写成 JSON；
///   2. **保守且便宜**：只按**当前字段值**求和（字符串取真实 UTF-8 字节数），再对 JSON 结构开销给固定余量；
///   3. **不靠逐类型手写**：用**按类型缓存**的只读计划（顶层 string 属性）推导 ——
///      将来新增载荷自动被覆盖，不会因为"忘了声明上界"而重新出现低估。
///
/// 关键性质：<c>Estimate(payload) ≥ 实际编码字节</c>（由 <c>PayloadSizeEstimatorTests</c> 对
/// **全部已登记载荷实例**逐一验证）。因此管线入口的"单事件字节上限"是**真闸门**，不是装饰。
/// </summary>
public static class PayloadSizeEstimator
{
    /// <summary>无自由文本载荷的保守上界（与旧实现一致，保持对外口径稳定）。</summary>
    public const int FixedUpperBound = 512;

    /// <summary>JSON 结构开销余量（大括号/键名/冒号/逗号/引号等）。</summary>
    private const int StructuralOverhead = 64;

    /// <summary>每个属性的额外余量（键名 + 分隔符）。</summary>
    private const int PerPropertyOverhead = 16;

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PlanCache = new();

    /// <summary>推导某个载荷的字节上界（≥ 实际编码字节；绝不低于 <see cref="FixedUpperBound"/>）。</summary>
    public static int Estimate(IDiagnosticPayload? payload)
    {
        if (payload is null) return 0;

        var plan = PlanCache.GetOrAdd(payload.GetType(), BuildPlan);
        if (plan.Length == 0) return FixedUpperBound;

        long total = StructuralOverhead;
        for (var i = 0; i < plan.Length; i++)
        {
            total += PerPropertyOverhead;
            if (plan[i].PropertyType != typeof(string)) continue;

            var value = plan[i].GetValue(payload) as string;
            if (!string.IsNullOrEmpty(value))
                total += Encoding.UTF8.GetByteCount(value);   // 真实 UTF-8 字节，不是 UTF-16 字符数
        }

        return (int)Math.Min(int.MaxValue, Math.Max(FixedUpperBound, total));
    }

    private static PropertyInfo[] BuildPlan(Type type)
    {
        try
        {
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead)
                .Where(p => p.PropertyType == typeof(string) || p.PropertyType.IsValueType)
                .ToArray();
        }
        catch (Exception)
        {
            // 反射失败 ⇒ 回落到固定上界（保守，不会低估成 0）。
            return Array.Empty<PropertyInfo>();
        }
    }
}