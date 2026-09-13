using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCMig.Core.State;

/// <summary>
/// JSON 状态存取。铁律：<b>所有状态文件一律 写临时文件 + 原子 rename，只追加不原地改写</b>。
/// 即使断电，最坏情况是留下一个 .tmp 残件，旧版本状态仍然完整可读。
/// </summary>
public static class JsonStateStore
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }

    public static void WriteAtomic<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            var json = JsonSerializer.Serialize(value, Options);
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 残件不影响正确性 */ }
        }
    }

    public static T Read<T>(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, Options)
               ?? throw new InvalidDataException($"状态文件反序列化为空: {path}");
    }

    public static bool TryRead<T>(string path, out T? value)
    {
        value = default;
        try { value = Read<T>(path); return true; }
        catch { return false; }
    }

    /// <summary>读取 Receipt 目录下全部凭证（用于状态重建 / 恢复）。损坏的单条凭证跳过并记录。</summary>
    public static List<T> ReadAllReceipts<T>(string receiptsDir, Action<string>? onCorrupt = null)
    {
        var list = new List<T>();
        if (!Directory.Exists(receiptsDir)) return list;
        foreach (var f in Directory.EnumerateFiles(receiptsDir, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            try { list.Add(Read<T>(f)); }
            catch (Exception ex) { onCorrupt?.Invoke($"{f}: {ex.Message}"); }
        }
        return list;
    }
}
