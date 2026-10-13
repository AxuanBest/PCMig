using PCMig.Core.Models;

namespace PCMig.Core.Util;

/// <summary>
/// 路径规范化与「根层归属」比较的唯一真值。
///
/// 为什么必须集中一处（P0-01 根因）：
///   旧代码在 Verifier.EnumerateSide / Verifier.StreamSourcePass / RepairPurge.EnumerateSource
///   各自写了一份 <c>Path.GetDirectoryName(file) == root.TrimEnd('\\')</c>。
///   当 root 是卷根 <c>D:\</c> 时：
///     · Path.GetDirectoryName(@"D:\a.pst")  ⇒ <c>D:\</c>
///     · <c>"D:\".TrimEnd('\\')</c>          ⇒ <c>D:</c>
///   两者永不相等 ⇒ 卷根对象（RootFiles）的**全部根层文件被静默忽略**。
///   现场后果：Preview object-000018 与 Stable object-000004 都报「源 14 / 目标 0」「源 1 / 目标 0」，
///   强制修复据此删掉了目标侧同名文件（27.44 GB / 869 B）——假缺失驱动了真实删除。
///   注意 RepairPurge 之所以「能删」（当时的实现口径；★ P0-03，2026-10-12 起已改为改名隔离）
///   而 Verifier「源侧能数到」，正是因为源根是 UNC <c>\\host\D</c>（无尾随分隔符），
///   目标的卷根 <c>D:\</c> 才触发该缺陷：同一份比较逻辑在不同 root 形态下结论相反。
///
/// 归一规则（字符串比较用，不做 IO）：
///   · 分隔符统一为 '\'，去掉全部尾随分隔符（卷根 <c>D:\</c> ⇒ <c>D:</c>）；
///   · UNC 根 <c>\\host\D\</c> ⇒ <c>\\host\D</c>；
///   · 比较一律 OrdinalIgnoreCase（Windows 文件系统语义）。
/// </summary>
public static class PathNorm
{
    /// <summary>归一化用于**比较**的路径字符串。不访问文件系统，不解析相对路径。</summary>
    public static string ForCompare(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var s = path.Replace('/', '\\');
        // 去掉尾随分隔符；"\\" 这类退化输入保留一个反斜杠
        var trimmed = s.TrimEnd('\\');
        return trimmed.Length == 0 ? "\\" : trimmed;
    }

    /// <summary>两个路径（目录）是否指向同一位置。</summary>
    public static bool SameDirectory(string? a, string? b)
        => string.Equals(ForCompare(a), ForCompare(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="fileFullPath"/> 是否**直接**位于 <paramref name="root"/> 之下（根层文件）。
    /// 卷根、UNC 根、普通目录、尾随分隔符有无、大小写差异都必须给出同一结论。
    /// 取不到父目录（异常/非法路径）时返回 false —— 宁可漏判，不可把深层文件误判成根层。
    /// </summary>
    public static bool IsRootLevelFile(string? fileFullPath, string? root)
    {
        if (string.IsNullOrEmpty(fileFullPath) || string.IsNullOrEmpty(root)) return false;
        string? parent;
        try { parent = Path.GetDirectoryName(fileFullPath); }
        catch { return false; }
        return parent != null && SameDirectory(parent, root);
    }

    /// <summary>
    /// RootFiles（根目录散落文件）语义下的条目归属判定：只有直接位于 <paramref name="enumeratedRoot"/>
    /// 之下的文件才算数。非 RootFiles 对象一律返回 true（不启用根层限制）。
    /// </summary>
    public static bool BelongsToObjectRoot(PlannedObject obj, string enumeratedRoot, string fileFullPath)
        => obj.Kind != ObjectKind.RootFiles || IsRootLevelFile(fileFullPath, enumeratedRoot);

    /// <summary>
    /// <paramref name="child"/> 是否位于 <paramref name="parent"/> 之内（**不含**自身）。
    /// 用于 ★ P1-07(3) ★ 的自拷贝/路径穿越保护：目标落在源之内（或反之）时必须拦住。
    /// </summary>
    public static bool IsInside(string? parent, string? child)
    {
        var p = ForCompare(parent);
        var c = ForCompare(child);
        if (p.Length == 0 || c.Length == 0) return false;
        if (SameDirectory(p, c)) return false;                 // 自身不算"之内"
        var prefix = p.EndsWith("\\", StringComparison.Ordinal) ? p : p + "\\";
        return c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>两个目录是否重叠（同一位置，或一方位于另一方之内）。</summary>
    public static bool Overlaps(string? a, string? b)
        => SameDirectory(a, b) || IsInside(a, b) || IsInside(b, a);
}