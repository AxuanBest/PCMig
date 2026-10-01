using System.Text.Json;

namespace PCMig.Diagnostics.Abstractions;

/// <summary>
/// 强类型事件载荷。规则**只能**依赖它（typed payload + EventId），
/// 绝不允许依赖中文 Message 文本（"if (message.Contains(\"网络断开\"))" 是明令禁止的形态）。
///
/// 设计约定：
///   · 实现类型一律是 sealed record，字段扁平、有界、白名单；
///   · 不携带任意 object、委托、Exception 对象、VM/凭据对象；
///   · 序列化显式手写（<see cref="WriteJson"/>），不依赖反射，便于控制分配与字段顺序；
///   · 读取实现放在 runtime 侧的 codec 注册表，按 <see cref="PayloadName"/> 匹配。
/// </summary>
public interface IDiagnosticPayload
{
    /// <summary>稳定载荷名（与 EventDescriptor.PayloadName、codec 注册名三者必须一致）。</summary>
    string PayloadName { get; }

    /// <summary>把载荷写进事件 JSON 的 payload 对象（调用方已开启对象）。</summary>
    void WriteJson(Utf8JsonWriter writer);
}