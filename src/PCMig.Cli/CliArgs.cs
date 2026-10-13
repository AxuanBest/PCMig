using System;
using System.Collections.Generic;
using System.Linq;

namespace PCMig.Cli;

/// <summary>
/// ★ 危险行为修复（D7 参数契约）★ 命令行参数解析与**业务调用前的**校验。
///
/// 修复的三条真实事故：
///   1. <c>pcmig resume --help</c> —— <c>--help</c> 被静默丢弃，CLI 真的执行了恢复；
///   2. <c>pcmig list --jobs=&lt;dir&gt;</c> —— 等号写法不被识别，静默回落到默认 Job 根；
///   3. 任何未知参数都被静默忽略后**照样执行实际操作**。
///
/// 本类只做"把命令行变成一个字典 + 判定合法性"这件事，**没有任何副作用**：
/// 不碰文件、不碰环境变量、不打日志、不分发命令。顺序由 <c>Program.Main</c> 保证：
/// 解析 → 校验（白名单 + 位置参数）→ help → 设置 PCMIG_JOBS → 分发。
/// </summary>
internal static class CliArgs
{
    /// <summary>
    /// **合法选项白名单** —— 唯一来源是 <c>Program.cs</c> 里真实存在的读取点
    /// （<c>opt["x"]</c> / <c>opt.TryGetValue("x")</c> / <c>opt.ContainsKey("x")</c> / <c>GetValueOrDefault("x")</c> /
    /// <c>Required(opt, "x")</c>）。新增选项必须同时在此登记，否则会被当作未知参数拒绝。
    /// 不收录：<c>help</c> / <c>h</c>（帮助开关，单独识别）、<c>-h</c>（单横线写法同样单独识别）。
    /// </summary>
    internal static readonly IReadOnlyList<string> KnownOptions = new[]
    {
        // 全局 / 基础设施
        "jobs", "quiet", "matrix",
        // 连接与凭据
        "host", "user", "password", "source", "target",
        // new / quick 的任务定义
        "paths", "kind", "notes", "yes",
        // 排除与性能（扫描/传输/验证同口径）
        "xd", "xf", "threads", "largemb", "large-channel", "noverify",
        // 闸门与开关
        "bench", "allow-incomplete-scan",
        // 各只读命令
        "job", "watch", "level", "open",
    };

    /// <summary>帮助开关：选项名 help/h，或任意位置的单横线 -h / 双横线 --help。</summary>
    internal static readonly IReadOnlyList<string> HelpTokens = new[] { "--help", "-h", "help", "h" };

    /// <summary>从不读取裸位置参数的子命令：多给一个裸参数就是用法错误（保守策略，见位置参数策略文档）。</summary>
    internal static readonly IReadOnlyList<string> CommandsWithoutPositionals = new[]
    {
        "preflight", "new", "run", "pause", "stop", "resume", "status", "verify", "report",
        "verify-report", "vreport", "repair-restore", "restore-quarantine", "list", "changelog",
        "log", "history", "quick", "credit", "credits", "author",
    };

    private static readonly HashSet<string> KnownSet =
        new(KnownOptions, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> NoPositionalSet =
        new(CommandsWithoutPositionals, StringComparer.OrdinalIgnoreCase);

    /// <summary>解析结果：选项字典 + 裸位置参数 + 未知选项（全部在业务调用前判定完毕）。</summary>
    internal sealed class CliArgsParseResult
    {
        internal CliArgsParseResult(
            Dictionary<string, string> options,
            IReadOnlyList<string> positionals,
            IReadOnlyList<string> unknownOptions,
            bool helpRequested,
            string? syntaxError)
        {
            Options = options;
            Positionals = positionals;
            UnknownOptions = unknownOptions;
            HelpRequested = helpRequested;
            SyntaxError = syntaxError;
        }

        /// <summary>选项字典（键大小写不敏感，与旧 <c>ParseOptions</c> 行为一致）。</summary>
        public Dictionary<string, string> Options { get; }

        /// <summary>不属于任何 <c>--key</c> 的裸位置参数（旧实现把它们静默丢弃了）。</summary>
        public IReadOnlyList<string> Positionals { get; }

        /// <summary>不在白名单里的 <c>--xxx</c> 选项名（含 <c>--</c>，如 <c>--nope</c>）。</summary>
        public IReadOnlyList<string> UnknownOptions { get; }

        /// <summary>命令行请求了帮助（<c>--help</c> / <c>-h</c> / <c>help</c> / <c>h</c>，出现在何处都算）。</summary>
        public bool HelpRequested { get; }

        /// <summary>语法级错误（如 <c>--jobs</c> 缺值）；非 null 时必须阻断，不得进入业务分支。</summary>
        public string? SyntaxError { get; }
    }

    /// <summary>
    /// 切分命令行。
    /// 规则与旧实现保持一致并补上等号写法：
    /// <list type="bullet">
    /// <item><c>--key=value</c> 按**第一个** '=' 切分，值里允许再出现 '='（如路径、URL）；</item>
    /// <item><c>--key value</c> 只在紧跟的 token **不以 <c>--</c> 开头**时消费它作为值 —— 这是旧
    ///       <c>ParseOptions</c> 的判据，必须保留：否则 <c>resume --quiet --help</c> 里的
    ///       <c>--help</c> 会被 <c>--quiet</c> 吃掉，又变成了"想看帮助却真的恢复"的老事故；</item>
    /// <item>最后没有跟值的 <c>--key</c> 记作开关，值为 "true"；</item>
    /// <item>以单个 '-' 开头但不是 <c>-h</c> 的 token（如 <c>-x</c>）按未知选项拒绝；</item>
    /// <item>其它裸 token 收进位置参数（旧实现直接丢弃），由位置参数策略判定是否合法。</item>
    /// </list>
    /// </summary>
    internal static CliArgsParseResult Parse(IReadOnlyList<string> args)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var positionals = new List<string>();
        var unknown = new List<string>();
        var help = false;
        string? syntaxError = null;

        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];

            if (string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase))
            {
                help = true;
                continue;
            }

            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                // 单横线但不是 -h：当作未知选项报错，而不是静默丢掉
                if (a.StartsWith("-", StringComparison.Ordinal) && a.Length > 1)
                {
                    unknown.Add(a);
                    continue;
                }
                if (a.Length > 0) positionals.Add(a);
                continue;
            }

            var body = a[2..];
            if (body.Length == 0) { positionals.Add(a); continue; } // 裸 "--" 按位置参数看待，不做吞并

            string key;
            string value;
            var eq = body.IndexOf('=');
            if (eq >= 0)
            {
                key = body[..eq];
                value = body[(eq + 1)..];
            }
            else
            {
                key = body;
                // 旧判据：后一个 token 以 "--" 开头就不消费（开关型），否则消费它作为值
                value = i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[++i]
                    : "true";
            }

            if (IsHelpKey(key))
            {
                // --help=false / --help=0 视为显式关闭，避免脚本里用布尔变量拼命令行时被误判成"要帮助"
                if (!IsFalse(value)) help = true;
                continue;
            }

            if (string.Equals(key, "jobs", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(value))
                syntaxError = "参数 --jobs 缺少值（正确用法：--jobs <目录> 或 --jobs=<目录>）";

            dict[key] = value;

            if (!KnownSet.Contains(key)) unknown.Add("--" + key);
        }

        return new CliArgsParseResult(dict, positionals, unknown, help, syntaxError);
    }

    private static bool IsHelpKey(string key)
        => string.Equals(key, "help", StringComparison.OrdinalIgnoreCase)
           || string.Equals(key, "h", StringComparison.OrdinalIgnoreCase);

    private static bool IsFalse(string v)
        => string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)
           || string.Equals(v, "0", StringComparison.Ordinal)
           || string.Equals(v, "no", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 位置参数策略（保守）：只对**从不读取裸位置参数**的子命令判定"多余裸参数是错误"；
    /// 结论不清的命令（不在 <see cref="NoPositionalSet"/> 里的未知命令）不在此处置，
    /// 由调用方回落到既有的 `未知命令: xxx` 分支。
    /// </summary>
    internal static string? ValidatePositionals(string command, IReadOnlyList<string> positionals)
    {
        if (positionals.Count == 0) return null;
        if (string.IsNullOrEmpty(command)) return null;
        if (!NoPositionalSet.Contains(command)) return null; // 未知命令交给既有分支，不在这里下结论
        return $"多余的位置参数: {string.Join(" ", positionals)}（{command} 不接受位置参数）";
    }

    /// <summary>是否是被识别的子命令（不区分大小写，与分发用的小写名一致）。</summary>
    internal static bool IsKnownCommand(string command)
        => command is "preflight" or "new" or "run" or "pause" or "stop" or "resume" or "status"
            or "verify" or "report" or "verify-report" or "vreport" or "repair-restore"
            or "restore-quarantine" or "list" or "changelog" or "log" or "history" or "quick"
            or "credit" or "credits" or "author";

    /// <summary>该子命令的用法正文（与 <c>Program.PrintUsage</c> 同一份文字，避免两处漂移）。</summary>
    internal static string CommandUsage(string command) => command switch
    {
        "preflight" => """
  pcmig preflight --host <IP或电脑名> [--source \\HOST\D$] [--user U] [--password P] [--bench 链路测速]
      预检：连通性、凭据、磁盘空间、可选链路测速。只读，不写目标。
""",
        "new" => """
  pcmig new       --host <H> --source <\\H\D$[;\\H\C$\Users\x]> --target <D:\目录>
                  [--kind data|profile] [--threads 16] [--yes] [--paths "..."] [--notes "..."]
      创建任务：预检 → 扫描 → 生成计划。加 --yes 跳过确认。
""",
        "quick" => """
  pcmig quick     --host <H> --target <D:\目录> [--source \\H\D$] [--user U] [--yes]
      一条龙：预检 + 扫描 + 计划 + 传输。会真正开始迁移。
""",
        "run" => """
  pcmig run       --job <JOB-ID> [--password P] [--matrix <yaml>] [--allow-incomplete-scan]
      执行任务（会真正开始迁移）。已经完成的对象不会重传。
""",
        "pause" => """
  pcmig pause     --job <JOB-ID>
      协作式暂停：当前对象传完后停止，已传部分保留，可 resume 续传。
""",
        "stop" => """
  pcmig stop      --job <JOB-ID>
      立即暂停：终止当前 robocopy，已传部分保留，可 resume 续传。
""",
        "resume" => """
  pcmig resume    [--job <JOB-ID>] [--password P] [--matrix <yaml>] [--allow-incomplete-scan]
      恢复任务。**不带 --job 时自动选最近的未完成任务并立即开始迁移**（有副作用）。
      拿不准会动哪个任务时，请先用 pcmig list 查看，再显式写 --job。
""",
        "status" => """
  pcmig status    --job <JOB-ID> [--watch]
      查看任务状态与未完成对象清单。只读。
""",
        "verify" => """
  pcmig verify    --job <JOB-ID> [--level 2] [--matrix <yaml>]
      核对目标与源的 文件数/字节数（--level 2 追加确定性抽样 SHA-256）。只读源码、只写本任务目录。
""",
        "report" => """
  pcmig report    --job <JOB-ID> [--open]
      生成迁移报告（传了什么，本地 HTML）。--open 会调用默认浏览器打开。
""",
        "verify-report" or "vreport" => """
  pcmig verify-report --job <JOB-ID> [--open]
      生成验证报告（核对到了什么，本地 HTML）。没有验证结果时如实说明，不生成空报告。
""",
        "repair-restore" or "restore-quarantine" => """
  pcmig repair-restore --job <JOB-ID>
      把强制覆盖期间保留的旧副本（.pcmig-old-*）改名回原路径。目标侧已存在时一律跳过。
""",
        "list" => """
  pcmig list
      列出全部任务（只读）。不接受位置参数。
""",
        "changelog" or "log" or "history" => """
  pcmig changelog
      查看从 v0.1.0 起的所有版本更新内容（同安装目录的 更新日志.txt）。
""",
        "credit" or "credits" or "author" => """
  pcmig credit
      开发者署名。
""",
        _ => "",
    };

    /// <summary>全量用法（未知命令时给出），与 <c>Program.PrintUsage</c> 同源思路：这里只做兜底导流。</summary>
    internal static string GlobalUsageHint =>
        "用法: 运行 pcmig --help 查看全部子命令，或 pcmig <子命令> --help 查看该子命令用法。";
}