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

    /// <summary>状态文件写入遇到可恢复错误时的告警出口（可空，接日志用）。</summary>
    public static Action<string>? OnWarning { get; set; }

    // ---- 写入退避 + 冷却窗口（D1 回归遗留）----
    // 背景：外部程序长期独占 job-state.json 时，每次状态写都要把"退避重试 8 次"走完（约 1.12 s），
    // 官方回归实测 46 次写把 run 从 6.2 s 拖到 57.3 s（9 倍）。
    // 冷却窗口按【目标文件】分别记账：只有"被占用的那个文件"进入 30 秒冷却（冷却期内每次只做 1 次
    // 快速尝试，一旦占用解除立刻自愈）；回执等其他文件的正常写入不受任何影响。
    // （第一版把冷却记在进程全局上，结果每次回执写入成功都会把 job-state.json 的冷却清掉，
    //  实测只把 59.5 s 降到 34.3 s —— 缺陷就在这里，故改为按文件记账。）
    // "绝不因为状态文件写不进而中断任务"的原则完全不变（Receipt 仍是唯一权威）。
    private const int FullAttempts = 8;      // 正常：40ms 递增退避共 8 次（约 1.12 s）
    private const int CooldownAttempts = 1;  // 冷却期：只做 1 次尝试（够用且几乎零代价）
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    private sealed class WriteGate
    {
        public DateTime CooldownUntilUtc = DateTime.MinValue;
        public int SkipsInARow;
        public DateTime LastSkipWarnUtc = DateTime.MinValue;
    }

    // 只为"确实写失败过"的文件建账，成功即销账，长期运行不会积累
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, WriteGate> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    private static bool InCooldown(string path)
    {
        if (!Gates.TryGetValue(path, out var g)) return false;
        lock (g) return DateTime.UtcNow < g.CooldownUntilUtc;
    }

    /// <summary>写入成功：该文件退出冷却、销账，并在"从占用中恢复"时如实报一次（自愈可见）。</summary>
    private static void NoteWriteOk(string path)
    {
        if (!Gates.TryGetValue(path, out var g)) return;
        string? recovered = null;
        lock (g)
        {
            if (g.SkipsInARow > 0)
                recovered = $"状态文件已恢复写入（外部占用已解除，此前连续跳过 {g.SkipsInARow} 次）: {path}";
        }
        Gates.TryRemove(path, out _);
        if (recovered != null) OnWarning?.Invoke(recovered);
    }

    /// <summary>本次写入被跳过：该文件进入冷却窗口，并返回需要吐出去的告警（冷却期内不逐条刷屏）。</summary>
    private static string? NoteSkip(string path, Exception ex, bool wasInCooldown)
    {
        var g = Gates.GetOrAdd(path, _ => new WriteGate());
        lock (g)
        {
            g.SkipsInARow++;
            g.CooldownUntilUtc = DateTime.UtcNow + Cooldown;
            if (wasInCooldown && DateTime.UtcNow - g.LastSkipWarnUtc < Cooldown) return null; // 冷却期内不重复刷屏
            g.LastSkipWarnUtc = DateTime.UtcNow;
            return $"状态文件被外部占用，本次写入跳过（不影响任务正确性，以 Receipt 为准）: {path}（{ex.GetType().Name}）" +
                   $"；已进入 {Cooldown.TotalSeconds:0} 秒冷却窗口（该文件在窗口内不再逐次重试），" +
                   "任务速度不会因此被拖垮，外部占用解除后会自动恢复写入。";
        }
    }

    public static void WriteAtomic<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        var existed = File.Exists(path);
        try
        {
            var json = JsonSerializer.Serialize(value, Options);
            File.WriteAllText(tmp, json);
            // 外部程序（杀毒、备份、用户用编辑器看着）可能正打开着目标文件，此时覆盖会被拒绝。
            // 退避重试；仍失败时：如果目标本来就有（视图类状态），只告警、不中断任务——
            // Receipt 才是权威，job-state.json 只是视图；如果是首次创建，如实抛出。
            // 冷却期内只做 1 次尝试：长期占用者不再让任务"每次状态写都等 1.12 秒"。
            var inCooldown = InCooldown(path);
            var maxAttempts = inCooldown ? CooldownAttempts : FullAttempts;
            for (var i = 0; ; i++)
            {
                try { File.Move(tmp, path, overwrite: true); NoteWriteOk(path); return; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (i >= maxAttempts - 1)
                    {
                        if (existed)
                        {
                            var msg = NoteSkip(path, ex, inCooldown);
                            if (msg != null) OnWarning?.Invoke(msg);
                            return;
                        }
                        throw;
                    }
                    Thread.Sleep(40 * (i + 1));
                }
            }
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
