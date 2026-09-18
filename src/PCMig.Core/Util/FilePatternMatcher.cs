using System.Text.RegularExpressions;

namespace PCMig.Core.Util;

/// <summary>
/// 文件名匹配：精确名（不区分大小写）或 * ? 通配符（如 "~$*"、"*.tmp"）。
/// 与 robocopy /XF 的通配语义对齐 —— 扫描/传输/验证三方必须使用同一个匹配器，否则口径不一致必然误报。
/// </summary>
public static class FilePatternMatcher
{
    private static readonly char[] s_wildcards = ['*', '?'];

    /// <summary>构建匹配函数。规则通常少于 50 条，线性扫描 + 正则缓存即可。</summary>
    public static Func<string, bool> Build(IEnumerable<string> patterns)
    {
        var arr = patterns as string[] ?? patterns.ToArray();
        var cache = new Dictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);
        return name =>
        {
            foreach (var p in arr)
            {
                if (p.IndexOfAny(s_wildcards) >= 0)
                {
                    if (!cache.TryGetValue(p, out var rx))
                    {
                        rx = new Regex("^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
                        cache[p] = rx;
                    }
                    if (rx.IsMatch(name)) return true;
                }
                else if (string.Equals(name, p, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        };
    }
}
