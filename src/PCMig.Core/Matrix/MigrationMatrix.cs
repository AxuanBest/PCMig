using Serilog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PCMig.Core.Matrix;

/// <summary>
/// 迁移策略矩阵（matrix/migration-matrix.yaml 的运行时表示）。
/// V1 先落地"排除策略 + 大文件策略 + 云占位符策略"；应用 Recipe 体系在此预留扩展点。
/// </summary>
public sealed class MigrationMatrix
{
    public string SchemaVersion { get; set; } = "1.0";

    /// <summary>全局排除的目录名（不区分大小写），将作为 robocopy /XD 传入。</summary>
    public List<string> ExcludedDirectoryNames { get; set; } = new()
    {
        "$RECYCLE.BIN", "RECYCLER", "System Volume Information", "Recovery", "Config.Msi",
        "$Windows.~BT", "$Windows.~WS", "Windows.old", "PerfLogs"
    };

    /// <summary>全局排除的文件名（不区分大小写），将作为 robocopy /XF 传入。</summary>
    public List<string> ExcludedFileNames { get; set; } = new()
    {
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log.tmp", "desktop.ini", "Thumbs.db",
        "~$*", ".~lock*#"  // Office/WPS/LibreOffice 瞬态锁文件（通配符，与 robocopy /XF 同语义）
    };

    /// <summary>凭据/安全类内容永不迁移（Policy 级，双重保险：既不复制也不写入目标）。</summary>
    public List<string> SecurityBlockedFileNames { get; set; } = new()
    {
        // 浏览器登录态/Cookie 均为 DPAPI 绑定源机+源用户，搬过去也解不开，且属于凭据边界
        "Login Data", "Login Data-journal", "Cookies", "Cookies-journal", "Web Data", "Web Data-journal"
    };

    /// <summary>云占位符策略：Warn=记录警告并继续（默认）；Skip=不复制（robocopy /XA:O 排除脱机文件，扫描/验证同口径剔除）。</summary>
    public string CloudPlaceholderPolicy { get; set; } = "Warn";

    public int LargeFileThresholdMB { get; set; } = 512;

    /// <summary>GUI 超大数据模式自动开启阈值（GB，按共享已用字节数判断，默认 2048=2TB）。</summary>
    public int ExpertModeAutoThresholdGB { get; set; } = 2048;

    /// <summary>
    /// 叠加任务级自定义排除规则（超大数据模式），返回新实例，原矩阵不变。
    /// 语法：/XD 目录名或路径；/XF 文件名或通配符；裸词按目录名处理。忽略空行与 # 注释。
    /// </summary>
    public MigrationMatrix WithExtraExclusions(IReadOnlyList<string>? rules, ILogger? log = null)
    {
        if (rules == null || rules.Count == 0) return this;
        var dirs = new List<string>(ExcludedDirectoryNames);
        var files = new List<string>(ExcludedFileNames);
        var added = 0;
        foreach (var raw in rules)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("/XF", StringComparison.OrdinalIgnoreCase))
            {
                var v = line[3..].Trim().Trim('"');
                if (v.Length > 0 && !files.Contains(v, StringComparer.OrdinalIgnoreCase)) { files.Add(v); added++; }
            }
            else if (line.StartsWith("/XD", StringComparison.OrdinalIgnoreCase))
            {
                var v = line[3..].Trim().Trim('"');
                if (v.Length > 0 && !dirs.Contains(v, StringComparer.OrdinalIgnoreCase)) { dirs.Add(v); added++; }
            }
            else
            {
                var v = line.Trim('"');
                if (!dirs.Contains(v, StringComparer.OrdinalIgnoreCase)) { dirs.Add(v); added++; }
            }
        }
        if (added == 0) return this;
        log?.Information("叠加任务级排除规则 {Count} 条（目录 {D} / 文件 {F}）", added, dirs.Count, files.Count);
        return new MigrationMatrix
        {
            SchemaVersion = SchemaVersion,
            ExcludedDirectoryNames = dirs,
            ExcludedFileNames = files,
            SecurityBlockedFileNames = SecurityBlockedFileNames,
            CloudPlaceholderPolicy = CloudPlaceholderPolicy,
            LargeFileThresholdMB = LargeFileThresholdMB,
            ExpertModeAutoThresholdGB = ExpertModeAutoThresholdGB
        };
    }

    public static MigrationMatrix Load(string? explicitPath, ILogger? log = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath)) candidates.Add(explicitPath);
        // 便携/安装目录优先，其次程序数据目录
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "matrix", "migration-matrix.yaml"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PCMig", "matrix", "migration-matrix.yaml"));

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var yaml = File.ReadAllText(path);
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                var matrix = deserializer.Deserialize<MigrationMatrix>(yaml) ?? new MigrationMatrix();
                log?.Information("已加载迁移矩阵: {Path}", path);
                return matrix;
            }
            catch (Exception ex)
            {
                log?.Warning(ex, "迁移矩阵解析失败，使用内置默认值: {Path}", path);
                return new MigrationMatrix();
            }
        }

        log?.Warning("未找到 migration-matrix.yaml，使用内置默认策略");
        return new MigrationMatrix();
    }
}
