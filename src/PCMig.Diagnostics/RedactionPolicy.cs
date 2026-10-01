using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PCMig.Diagnostics.Abstractions;

namespace PCMig.Diagnostics;

/// <summary>
/// 脱敏策略：**入口侧**就把可识别信息变成令牌/别名，而不是等导出时再"洗日志"（方案 §20）。
///
/// 纪律：
///   · Secret 走另一条路 —— 它在类型与目录两层就被禁止进入 DiagnosticEvent，本策略不负责"洗 Secret"；
///   · Personal（用户名/主机/IP/完整路径/文件名）默认转成 HMAC 令牌与别名；
///   · HMAC-SHA256 用**每会话随机密钥**，不用无盐 SHA256（对可猜 IP/路径等于没脱敏）；
///   · 任何内部异常 ⇒ 返回占位符并让调用方计数，**绝不回退明文**；
///   · 首版密钥只存在内存（不落盘）：跨会话关联需要持久化密钥时，必须另走 DPAPI 保护的受限状态（D6 决策）。
/// </summary>
public sealed class RedactionPolicy
{
    private const int TokenLength = 22;
    private const int MaxExtensionLength = 12;

    private static readonly string[] SecretKeys =
    {
        "password", "passwd", "pwd", "secret", "token", "credential", "credentials",
        "apikey", "api_key", "authorization", "sharekey", "accesskey",
    };

    private static readonly Dictionary<string, string> ExtensionClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        // document
        ["txt"] = "document", ["doc"] = "document", ["docx"] = "document", ["pdf"] = "document",
        ["xls"] = "document", ["xlsx"] = "document", ["ppt"] = "document", ["pptx"] = "document",
        ["csv"] = "document", ["md"] = "document", ["rtf"] = "document", ["odt"] = "document",
        // archive
        ["zip"] = "archive", ["7z"] = "archive", ["rar"] = "archive", ["tar"] = "archive",
        ["gz"] = "archive", ["bz2"] = "archive", ["xz"] = "archive", ["cab"] = "archive",
        // image / video / audio
        ["png"] = "image", ["jpg"] = "image", ["jpeg"] = "image", ["gif"] = "image", ["bmp"] = "image",
        ["webp"] = "image", ["svg"] = "image", ["heic"] = "image", ["tif"] = "image", ["tiff"] = "image",
        ["mp4"] = "video", ["mov"] = "video", ["avi"] = "video", ["mkv"] = "video", ["wmv"] = "video",
        ["mp3"] = "audio", ["wav"] = "audio", ["flac"] = "audio", ["m4a"] = "audio", ["aac"] = "audio",
        // code / data
        ["cs"] = "code", ["js"] = "code", ["ts"] = "code", ["py"] = "code", ["java"] = "code",
        ["go"] = "code", ["rs"] = "code", ["c"] = "code", ["cpp"] = "code", ["h"] = "code",
        ["json"] = "code", ["xml"] = "code", ["yml"] = "code", ["yaml"] = "code", ["sql"] = "code",
        ["ps1"] = "code", ["sh"] = "code", ["bat"] = "code", ["cmd"] = "code",
        ["db"] = "data", ["sqlite"] = "data", ["mdb"] = "data", ["dat"] = "data", ["bin"] = "data",
        ["iso"] = "data", ["vhd"] = "data", ["vhdx"] = "data", ["img"] = "data", ["pst"] = "data",
        ["ost"] = "data", ["eml"] = "data", ["msg"] = "data", ["lnk"] = "data", ["exe"] = "data",
        ["dll"] = "data", ["msi"] = "data", ["sys"] = "data", ["ini"] = "data", ["log"] = "data",
    };

    private readonly byte[] _key;

    private RedactionPolicy(string keyId, byte[] key)
    {
        KeyId = keyId;
        _key = key;
    }

    /// <summary>本策略所用密钥的标识（导出会用新 key ⇒ 新 KeyId；**密钥本身永不外流**）。</summary>
    public string KeyId { get; }

    /// <summary>为一次诊断会话创建随机密钥（密钥只存内存）。</summary>
    public static RedactionPolicy CreateForSession(string? keyId = null) =>
        new(keyId ?? "s" + Guid.NewGuid().ToString("N").Substring(0, 8), RandomNumberGenerator.GetBytes(32));

    /// <summary>用给定密钥创建（仅供测试与将来的受控支持会话使用）。</summary>
    public static RedactionPolicy Create(string keyId, byte[] key) => new(keyId, key);

    /// <summary>把任意可识别字符串（路径/主机/IP/用户名）转成稳定令牌：同会话同输入 ⇒ 同令牌。</summary>
    public string Token(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "[empty]";
        try
        {
            var normalized = NormalizeForToken(value).ToLowerInvariant();
            var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(normalized));
            return ToBase64Url(mac).Substring(0, TokenLength);
        }
        catch (Exception)
        {
            // 绝不回退明文：宁可给占位符，也不能把原始值写进诊断数据。
            return "[token-failed]";
        }
    }

    /// <summary>路径 → 脱敏引用（不做任何文件系统访问，不 ResolveLink）。</summary>
    public PathRef CreatePathRef(string? path, PathRole role, string? rootAlias = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return PathRef.Unavailable(role);
        var rootKind = RootKindOf(path);
        var alias = rootAlias ?? DefaultAlias(role);
        try
        {
            var normalized = NormalizePath(path);
            return new PathRef
            {
                Role = role,
                RootKind = rootKind,
                RootAlias = alias,
                PathToken = Token(normalized),
                KeyId = KeyId,
                Scope = "session",
                ExtensionClass = ExtensionClassOf(normalized),
                DepthBucket = DepthBucketOf(normalized),
            };
        }
        catch (Exception)
        {
            return PathRef.TokenUnavailable(role, rootKind, alias);
        }
    }

    /// <summary>
    /// 文本字段的尽力清洗：截断 + 路径掩码 + 键值式秘密掩码。
    /// **这不是 Secret 的防线**（Secret 必须在入口就不存在），只是纵深防御：任何文本字段
    /// 仍可能夹带路径或 "password=..."，这里把它们变成占位符。
    /// </summary>
    public string? SanitizeText(string? text, int maxLength = 256)
    {
        if (string.IsNullOrEmpty(text)) return text;
        try
        {
            var working = text.Length > maxLength * 2 ? text.Substring(0, maxLength * 2) : text;
            working = MaskPaths(working);
            working = MaskSecretAssignments(working);
            return working.Length > maxLength ? working.Substring(0, maxLength) : working;
        }
        catch (Exception)
        {
            return "[text-unavailable]";
        }
    }

    /// <summary>扩展名粗分类（扩展名本身也可能敏感 ⇒ 只给粗类别）。</summary>
    public static string ExtensionClassOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "none";
        var dot = path.LastIndexOf('.');
        if (dot < 0 || dot == path.Length - 1) return "none";
        var ext = path.Substring(dot + 1).TrimEnd('\\', '/');
        if (ext.Length == 0 || ext.Length > MaxExtensionLength) return "other";
        return ExtensionClasses.TryGetValue(ext, out var cls) ? cls : "other";
    }

    /// <summary>目录深度桶（避免暴露真实层级）。</summary>
    public static int DepthBucketOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return 0;
        var depth = 0;
        for (var i = 0; i < path.Length; i++)
            if (path[i] == '\\' || path[i] == '/') depth++;
        if (depth <= 2) return 2;
        if (depth <= 4) return 4;
        if (depth <= 6) return 6;
        return 8;
    }

    /// <summary>根类型分类（不做 DNS/网络访问）。</summary>
    public static string RootKindOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Unknown";
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return "UncExtended";
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return "DriveExtended";
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return "Unc";
        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':') return "Drive";
        if (path.StartsWith("~", StringComparison.Ordinal)) return "Relative";
        return "Unknown";
    }

    private static string DefaultAlias(PathRole role) => role switch
    {
        PathRole.Source => "src",
        PathRole.Target => "dst",
        PathRole.JobDir => "job",
        PathRole.ReceiptDir => "job",
        PathRole.LogFile => "job",
        PathRole.TempFile => "tmp",
        PathRole.ChildProcessImage => "child",
        PathRole.WorkingDirectory => "cwd",
        PathRole.ExportOutput => "export",
        _ => "unknown",
    };

    /// <summary>令牌归一化：切片符统一、折叠重复分隔符、去尾部分隔符（大小写另在调用处折叠）。</summary>
    private static string NormalizeForToken(string value)
    {
        var s = value.Trim().Replace('/', '\\');
        var sb = new StringBuilder(s.Length);
        var leadingUnc = s.StartsWith(@"\\", StringComparison.Ordinal);
        if (leadingUnc) sb.Append(@"\\");
        var start = leadingUnc ? 2 : 0;
        var lastWasSep = false;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\')
            {
                if (lastWasSep) continue;
                lastWasSep = true;
            }
            else lastWasSep = false;
            sb.Append(c);
        }
        var result = sb.ToString();
        if (result.Length > 3 && (result.EndsWith("\\", StringComparison.Ordinal)))
            result = result.TrimEnd('\\');
        return result;
    }

    private static string NormalizePath(string value) => NormalizeForToken(value);

    private static string ToBase64Url(byte[] data)
    {
        var s = Convert.ToBase64String(data);
        return s.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    // ────────────────────────── 文本掩码（无正则，确定性） ──────────────────────────

    private static string MaskPaths(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (TryMatchPath(text, i, out var length))
            {
                sb.Append("[path]");
                i += length;
                continue;
            }
            sb.Append(text[i]);
            i++;
        }
        return sb.ToString();
    }

    private static bool TryMatchPath(string s, int i, out int length)
    {
        length = 0;
        var isPathStart = false;

        // X:\ 或 X:/
        if (i + 2 < s.Length && char.IsLetter(s[i]) && s[i + 1] == ':' && (s[i + 2] == '\\' || s[i + 2] == '/'))
            isPathStart = true;
        // \\server\share
        else if (i + 3 < s.Length && s[i] == '\\' && s[i + 1] == '\\' && s[i + 2] != '\\' && !char.IsWhiteSpace(s[i + 2]))
            isPathStart = true;

        if (!isPathStart) return false;

        var j = i;
        while (j < s.Length && !IsPathTerminator(s[j])) j++;
        length = j - i;
        return length > 2;
    }

    private static bool IsPathTerminator(char c) =>
        char.IsWhiteSpace(c) || c == '"' || c == '\'' || c == ',' || c == ';' || c == ')' || c == '('
        || c == '<' || c == '>' || c == '|' || c == '。' || c == '，' || c == '、';

    private static string MaskSecretAssignments(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var matched = false;
            foreach (var key in SecretKeys)
            {
                if (!MatchesAt(text, i, key)) continue;
                var afterKey = i + key.Length;
                if (!TryMatchAssignment(text, afterKey, out var valueStart)) continue;

                // 键名与分隔符保留（键名本身不是秘密），值替换掉。
                sb.Append(text, i, valueStart - i);
                sb.Append("[redacted]");
                i = valueStart;
                while (i < text.Length && !IsValueTerminator(text[i])) i++;
                matched = true;
                break;
            }
            if (matched) continue;
            sb.Append(text[i]);
            i++;
        }
        return sb.ToString();
    }

    private static bool MatchesAt(string text, int index, string token)
    {
        if (index + token.Length > text.Length) return false;
        return string.Compare(text, index, token, 0, token.Length, StringComparison.OrdinalIgnoreCase) == 0;
    }

    /// <summary>匹配 key 之后的 "=" / ":" / " = " / "=&quot; 形式，返回值的起始位置。</summary>
    private static bool TryMatchAssignment(string text, int index, out int valueStart)
    {
        valueStart = index;
        var i = index;
        while (i < text.Length && text[i] == ' ') i++;
        if (i >= text.Length || (text[i] != '=' && text[i] != ':')) return false;
        i++;
        while (i < text.Length && (text[i] == ' ' || text[i] == '"' || text[i] == '\'')) i++;
        valueStart = i;
        return true;
    }

    private static bool IsValueTerminator(char c) =>
        char.IsWhiteSpace(c) || c == '&' || c == ',' || c == ';' || c == '"' || c == '\'' || c == ')' || c == '}' || c == '>';
}