using System.Diagnostics;
using System.Text;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Native;
using Serilog;

namespace PCMig.Core.Transfer;

public enum PassKind { Bulk, Large, RootFiles }

public sealed record RobocopyRunResult(int ExitCode, bool Success, bool Killed, TimeSpan Duration, string? LastErrorLine,
    IReadOnlyList<string>? ErrorLines = null);

/// <summary>
/// Robocopy 执行器。
/// 队列分工（矩阵级决策）：
///   Bulk  通道：/MT 多线程 + /MAXFILESIZE（大文件先跳过，保证海量小文件吞吐）
///   Large 通道：/Z 可续传 + /MINFILESIZE（单线程逐个大文件，断网/断电后可从文件内部续传）
///   —— /Z 与 /MT 不同 pass 使用，规避两者组合的不确定性。
/// 退出码：位掩码，&lt; 8 为成功；&gt;= 8 存在失败项。
/// </summary>
public sealed class RobocopyRunner
{
    private readonly ILogger _log;
    private Process? _current;
    private volatile bool _killedByUs;   // KillCurrent 标记：进程被我们强杀（退出码 -1 时的权威判据）

    public event Action<string>? OutputLine;

    /// <summary>
    /// 每开始复制一个文件触发（解析 robocopy 输出行）：参数=源文件完整路径、近似字节数。
    /// 供编排层做 O(1) 实时进度，免去对目标目录的反复全量枚举（超大磁盘的关键）。
    /// </summary>
    public event Action<string, long>? FileCopied;

    // robocopy 输出结构：目录行 "              3\t\\\\host\\share\\dir\\"；文件行 "\t    新文件    \t\t 669.5 m\t文件名"
    private static readonly System.Text.RegularExpressions.Regex s_dirLineRx = new(
        @"^\s*\d+\s+(?<dir>\\\\.+)$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex s_fileLineRx = new(
        @"^\s*(?<tag>新文件|已更改|较新|较旧|New File|Changed|Newer|Older)\s+(?<size>[\d\.,]+)\s*(?<unit>[bkmgB]?)\s+(?<name>.+?)\s*$",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private volatile string? _currentDir;

    /// <summary>robocopy 体积列（"669.5 m" / "2.0 g" / 裸字节）→ 近似字节数（仅供进度展示）。</summary>
    private static long ParseRoboSize(string num, string unit)
    {
        num = num.Replace(",", "");
        if (!double.TryParse(num, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v)) return 0;
        return unit.ToLowerInvariant() switch
        {
            "k" => (long)(v * 1024),
            "m" => (long)(v * 1048576),
            "g" => (long)(v * 1073741824),
            _ => (long)v
        };
    }

    static RobocopyRunner()
    {
        // 使 GBK/OEM 代码页可用（robocopy 控制台输出为本地化编码）
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public RobocopyRunner(ILogger log) { _log = log.ForContext<RobocopyRunner>(); }

    /// <summary>退出码 &lt; 8 为成功；必须排除负数（-1 = 进程被强杀，绝不能算成功）。</summary>
    public static bool IsSuccess(int exitCode) => exitCode >= 0 && exitCode < 8;

    /// <summary>命令行安全长度上限（Win32 命令行极限 32767，留足余量）。</summary>
    private const int MaxArgsLength = 30000;

    /// <summary>
    /// 把参数包装成 CreateProcess / CommandLineToArgvW 能正确还原的形式（必须成对加倍反斜杠）。
    ///
    /// 生产事故根因（v0.3.8，JOB-20260917-143154-dc9d）：旧实现是 <c>"' + path + '"'</c>，
    /// 当路径以反斜杠结尾时（目标盘根 "D:\"、UNC 共享根 "\\host\share\"），
    /// 结尾的 <c>\"</c> 被解析成"转义后的引号" → 引号不闭合 → **后面所有参数被吞进这一个路径里**。
    /// 实测现象：robocopy 报 <c>ExitCode=16 无效参数 #2:"D:" *.* /LEV:1 /COPY:DAT …</c>，
    /// 源盘根目录的散落文件一个都没传过去（对象 object-000007 100% 失败）。
    /// 规则：引号前与字符串末尾的反斜杠必须成对加倍（<c>"D:\"</c> → 还原为 <c>D:\</c>）。
    /// </summary>
    public static string QuoteArg(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        sb.Append('"');
        var backslashes = 0;
        foreach (var c in s)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') { sb.Append('\\', backslashes * 2 + 1).Append('"'); backslashes = 0; continue; }
            sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2);   // 结尾反斜杠加倍：\"D:\" → \"D:\\"
        sb.Append('"');
        return sb.ToString();
    }

    // ---- 大文件通道模式（v0.3.8）----
    public const string LargeChannelAuto = "auto";
    public const string LargeChannelRestartable = "restartable";
    public const string LargeChannelMultiThreaded = "multithreaded";

    /// <summary>
    /// 大文件通道决策：
    ///   auto（默认）= /MT + /J（无缓冲、多线程；实测吞吐 ≈ 旧 /Z 单线程的 3 倍），
    ///                 对象级重试（attempt ≥ 2）时自动退回 /Z —— 续传优先，断网/断电后能从文件内部续上；
    ///   restartable = 恒用 /Z（文件内部可续传，代价是单线程、缓冲 I/O）；
    ///   multithreaded = 恒用 /MT + /J。
    /// 开关落 job.json（options.largeChannelMode），续传沿用同一取舍。
    /// </summary>
    public static bool UseRestartableZ(MigrationOptions opt, PassKind pass, int attempt)
    {
        if (pass != PassKind.Large) return false;
        var mode = (opt.LargeChannelMode ?? LargeChannelAuto).Trim().ToLowerInvariant();
        return mode switch
        {
            LargeChannelRestartable or "z" or "resume" => true,
            LargeChannelMultiThreaded or "mt" or "j" => false,
            _ => attempt > 1
        };
    }

    /// <summary>本 pass 实际使用的通道文案（写日志/回执，事后审计"这一版到底用了哪个通道"）。</summary>
    public static string ChannelText(PassKind pass, bool restartableZ, bool unbufferedJ = false) => pass switch
    {
        PassKind.Bulk => "/MT 多线程",
        PassKind.RootFiles => "/MT 根目录散落文件",
        PassKind.Large => restartableZ
            ? "/Z 可续传（单线程）"
            : (unbufferedJ ? "/MT + /J 无缓冲多线程（推荐于 SMB）" : "/MT 多线程（本机链路，不加 /J）"),
        _ => pass.ToString()
    };

    /// <summary>路径是否落在网络上（UNC 或映射盘）。</summary>
    public static bool IsNetworkPath(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return false;
        if (p.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        try
        {
            var root = Path.GetPathRoot(p);
            if (string.IsNullOrEmpty(root)) return false;
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch { return false; }
    }

    /// <summary>
    /// 大文件通道是否额外加 /J（无缓冲 I/O）。
    /// 依据（v0.3.8 实测）：/J 的意义在 SMB 高延迟链路上——不占用系统缓存、不做二次缓冲，
    /// 生产事故场景（\\10.0.15.25\d → D:\）正是这种链路，所以"只要有任一端是网络路径"就用 /J。
    /// 但 e/d 同机 NVMe 的 A/B 实测显示 /J 会让本地吞吐掉到 /Z 的 0.5~0.6 倍（绕过写合并、
    /// 每个 1MB 直接落盘）：6GB 单文件 /Z 3.8~6.1s vs /MT+/J 7.6~8.0s。
    /// 所以 auto 模式下本地链路只用 /MT、不加 /J；显式 multithreaded 模式仍然强制 /J。
    /// </summary>
    public static bool UseUnbufferedJ(string src, string dst, MigrationOptions opt)
    {
        var mode = (opt.LargeChannelMode ?? LargeChannelAuto).Trim().ToLowerInvariant();
        if (mode is LargeChannelMultiThreaded or "mt" or "j") return true;
        if (mode is LargeChannelRestartable or "z" or "resume") return false;
        return IsNetworkPath(src) || IsNetworkPath(dst);
    }

    public string BuildArguments(string src, string dst, MigrationOptions opt, MigrationMatrix matrix,
        PassKind pass, string unicodeLogPath, IReadOnlyList<string>? fileList = null,
        bool restartableLarge = false)
    {
        var sb = new StringBuilder();
        // 逐个用 QuoteArg：绝不能直接拼 '"' + path + '"'（尾反斜杠会让引号失效，见 QuoteArg 注释）
        sb.Append(QuoteArg(src)).Append(' ').Append(QuoteArg(dst));

        if (fileList != null)
        {
            foreach (var f in fileList) sb.Append(' ').Append(QuoteArg(f));
            // 显式文件清单：不递归（无 /E），大小分流仍由 /MAX /MIN 控制
        }
        else if (pass == PassKind.RootFiles)
        {
            sb.Append(" *.* /LEV:1");
        }
        else
        {
            sb.Append(" /E");
        }

        sb.Append(" /COPY:DAT /DCOPY:T /XJ");
        // 注：想让目标侧"大小+时间相同"的文件被源覆盖，/IS /IT 无济于事（实测字节行"复制=0"），
        // 唯一可靠办法是先删掉目标那份（见 RepairPurge），故此处不再拼接任何强制覆盖开关。
        sb.Append($" /R:{opt.RetryCount} /W:{opt.RetryWaitSec}");

        var thresholdBytes = (long)(matrix.LargeFileThresholdMB > 0 ? matrix.LargeFileThresholdMB : opt.LargeFileThresholdMB) * 1024 * 1024;
        if (pass == PassKind.Bulk)
        {
            sb.Append($" /MT:{Math.Clamp(opt.Threads, 1, 128)}");
            if (opt.SplitLargeFiles) sb.Append($" /MAX:{thresholdBytes - 1}");   // robocopy 用 /MAX 字节数（无 /MAXFILESIZE）
        }
        else if (pass == PassKind.Large)
        {
            // v0.3.8：默认 /MT + /J —— 旧版恒用 /Z（单线程 + 缓冲 I/O），真实生产实测吞吐只有
            // 用户手工 robocopy /MT:32 的 1/3（30 MB/s vs 90–137 MB/s）。
            // /J = 无缓冲 I/O（大文件与 SMB 场景推荐，不污染系统缓存）；
            // /Z 仍保留为"续传优先"通道（restartable 模式，或 auto 模式下对象级重试时自动退回）。
            if (restartableLarge)
                sb.Append($" /Z /MIN:{thresholdBytes}");
            else
            {
                sb.Append($" /MT:{Math.Clamp(opt.Threads, 1, 128)}");
                if (UseUnbufferedJ(src, dst, opt)) sb.Append(" /J");
                sb.Append($" /MIN:{thresholdBytes}");
            }
        }

        // 排除项也一律走 QuoteArg：条目里出现反斜杠结尾（如 "目录\"）时同样会让引号失效
        foreach (var d in matrix.ExcludedDirectoryNames) sb.Append($" /XD {QuoteArg(d)}");
        foreach (var f in matrix.ExcludedFileNames.Concat(matrix.SecurityBlockedFileNames)) sb.Append($" /XF {QuoteArg(f)}");
        // 云占位符 Skip 策略：排除带 Offline 属性的文件（OneDrive Files-On-Demand 占位符），
        // 避免 SMB 读取触发云端回源下载（可能把几百 GB 云数据拖到链路上）。
        if (matrix.CloudPlaceholderPolicy.Equals("Skip", StringComparison.OrdinalIgnoreCase))
            sb.Append(" /XA:O");

        // /NP 关百分比噪声；/TEE 双写；/UNILOG+ 追加写 Unicode 日志（中文环境不丢字）
        sb.Append($" /NP /TEE /UNILOG+:{QuoteArg(unicodeLogPath)}");
        return sb.ToString();
    }

    public async Task<RobocopyRunResult> RunPassAsync(string src, string dst, MigrationOptions opt,
        MigrationMatrix matrix, PassKind pass, string unicodeLogPath, CancellationToken ct,
        IReadOnlyList<string>? fileList = null, bool restartableLarge = false)
    {
        // 文件清单过长时按命令行长度分批执行，聚合结果
        if (fileList != null && fileList.Count > 0)
        {
            var batches = ChunkFileList(src, dst, opt, matrix, pass, fileList, restartableLarge);
            if (batches.Count > 1)
                _log.Information("文件清单 {Count} 项，按命令行长度拆为 {Batches} 批执行", fileList.Count, batches.Count);
            var swAll = Stopwatch.StartNew();
            var worst = 0; string? lastErr = null; var killed = false;
            foreach (var batch in batches)
            {
                var r = await RunSingleAsync(src, dst, opt, matrix, pass, unicodeLogPath, ct, batch, restartableLarge);
                if (r.Killed) { killed = true; break; }
                if (r.ExitCode > worst) { worst = r.ExitCode; lastErr = r.LastErrorLine; }
            }
            swAll.Stop();
            return new RobocopyRunResult(killed ? -1 : worst, !killed && IsSuccess(worst), killed, swAll.Elapsed, lastErr);
        }

        return await RunSingleAsync(src, dst, opt, matrix, pass, unicodeLogPath, ct, null, restartableLarge);
    }

    /// <summary>错误行最多保留多少条（防止亿级文件失败时吃光内存）。</summary>
    private const int MaxErrorLines = 500;

    private static readonly System.Text.RegularExpressions.Regex s_errCodeRx = new(
        @"(?:错误|ERROR)\s+(?<code>\d+)|0x(?<hex>[0-9A-Fa-f]{8})",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 把 robocopy 的一堆失败行汇总成"人话原因"：多少文件失败、主因是什么、举几个例子。
    /// 实测（公司环境）：SystemCache 里 132 个被应用占用的 *.tmp 让整个对象判失败，
    /// 而旧版本 GUI 只显示"复制失败"四个字，用户完全不知道是哪个文件、什么原因。
    /// </summary>
    public static string? SummarizeFailures(int exitCode, IReadOnlyList<string>? errorLines)
    {
        if (errorLines == null || errorLines.Count == 0) return null;
        // 同一文件会被 robocopy 记"初次失败 + 每次重试"多行（/R:2 → 3 行），必须按路径去重，
        // 否则"6 个文件失败"其实是 2 个文件各失败 3 次——实测踩中（T3 用例）。
        var byCode = new Dictionary<int, int>();      // 错误码 → 去重后的文件数
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var samples = new List<string>();
        var attempts = 0;
        foreach (var line in errorLines)
        {
            var m = s_errCodeRx.Match(line);
            if (!m.Success) continue;                 // 过滤 "错误: 超过重试限制。" 这类无码汇总行
            attempts++;
            var code = m.Groups["code"].Success
                ? int.Parse(m.Groups["code"].Value)
                : Convert.ToInt32(m.Groups["hex"].Value, 16);
            var key = ExtractErrorPath(line) ?? line.Trim();
            if (!seen.Add(key)) continue;             // 同一目标的重试不再重复计数
            byCode[code] = byCode.TryGetValue(code, out var c) ? c + 1 : 1;
            if (samples.Count < 3) samples.Add(ShortenPath(key));
        }
        if (byCode.Count == 0) return null;

        var top = byCode.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).ToList();
        var main = PCMig.Core.Util.ErrorTranslator.ReasonForCode(top[0].Key);
        var dist = string.Join("、", top.Take(3).Select(kv => $"{kv.Key}×{kv.Value}"));
        var total = byCode.Values.Sum();
        var sb = new StringBuilder();
        sb.Append(total).Append(" 个文件复制失败：").Append(main).Append('（');
        sb.Append(top.Count == 1 ? $"错误码 {top[0].Key}" : $"错误码分布 {dist}").Append('）');
        if (attempts > total) sb.Append("，含重试共 ").Append(attempts).Append(" 次失败");
        if (samples.Count > 0) sb.Append("；例如：").Append(string.Join("、", samples));
        sb.Append("。robocopy 退出码 ").Append(exitCode)
          .Append("（位掩码：8=有文件失败，1=有文件成功），已达重试上限，跳过这些文件继续其余数据。");
        return sb.ToString();
    }

    /// <summary>从 robocopy 错误行里抠出出错路径（跳过本地化消息，兼容 UNC 与盘符，允许路径含空格）。</summary>
    private static string? ExtractErrorPath(string line)
    {
        var hex = line.IndexOf("0x", StringComparison.Ordinal);
        var tail = hex >= 0 ? line[hex..] : line;
        var unc = tail.IndexOf(@"\\", StringComparison.Ordinal);
        var drive = System.Text.RegularExpressions.Regex.Match(tail, @"[A-Za-z]:\\");
        int start = -1;
        if (unc >= 0 && (!drive.Success || unc < drive.Index)) start = unc;
        else if (drive.Success) start = drive.Index;
        if (start < 0) return null;
        var p = tail[start..].Trim().TrimEnd('.', '。', ')', '）');
        return p.Length > 0 ? p : null;
    }

    /// <summary>UNC 路径去掉 \\主机\共享 前缀便于阅读；过长则截断。</summary>
    private static string ShortenPath(string path)
    {
        var s = path;
        if (s.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = s.Split('\\');
            if (parts.Length > 4) s = string.Join("\\", parts.Skip(4));
        }
        return s.Length > 80 ? s[..80] + "…" : s;
    }

    /// <summary>按累积命令行长度分批（中文名按 UTF-16 计 2 字节，近似保守）。</summary>
    private List<List<string>> ChunkFileList(string src, string dst, MigrationOptions opt,
        MigrationMatrix matrix, PassKind pass, IReadOnlyList<string> fileList, bool restartableLarge)
    {
        var batches = new List<List<string>>();
        var baseLen = BuildArguments(src, dst, opt, matrix, pass, "x.log", (IReadOnlyList<string>?)null, restartableLarge).Length;
        var current = new List<string>();
        var currentLen = baseLen;
        foreach (var f in fileList)
        {
            var add = f.Length * 2 + 4;
            if (currentLen + add > MaxArgsLength && current.Count > 0)
            {
                batches.Add(current);
                current = new List<string>();
                currentLen = baseLen;
            }
            current.Add(f);
            currentLen += add;
        }
        if (current.Count > 0) batches.Add(current);
        return batches;
    }

    private async Task<RobocopyRunResult> RunSingleAsync(string src, string dst, MigrationOptions opt,
        MigrationMatrix matrix, PassKind pass, string unicodeLogPath, CancellationToken ct,
        IReadOnlyList<string>? fileList, bool restartableLarge)
    {
        var args = BuildArguments(src, dst, opt, matrix, pass, unicodeLogPath, fileList, restartableLarge);
        _log.Information("robocopy 启动 [{Pass}/{Channel}]: robocopy {Args}", pass,
            ChannelText(pass, restartableLarge, UseUnbufferedJ(src, dst, opt)), args);

        var psi = new ProcessStartInfo("robocopy.exe", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // robocopy 输出跟随系统 OEM 代码页（中文系统为 GB936），与 Console 当前编码无关
            StandardOutputEncoding = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
            StandardErrorEncoding = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
        };

        var sw = Stopwatch.StartNew();
        string? lastErrorLine = null;
        var errorLines = new List<string>();   // 全部失败行（上限防爆内存），供"具体原因"汇总

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _killedByUs = false;
        _current = proc;
        try
        {
            proc.Start();
            ProcessJobGuard.Assign(proc, _log); // 主进程死亡时由 OS 连带终止，杜绝孤儿 robocopy

            var stdoutTask = PumpAsync(proc.StandardOutput, line =>
            {
                var trimmed = line.TrimEnd();
                if (trimmed.Length == 0) return;
                if (trimmed.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Contains("错误", StringComparison.Ordinal))
                {
                    lastErrorLine = trimmed;
                    if (errorLines.Count < MaxErrorLines) errorLines.Add(trimmed);
                }
                // 进度解析：目录行记忆“当前目录”；文件行（新文件/已更改…）触发 FileCopied
                var dm = s_dirLineRx.Match(trimmed);
                if (dm.Success)
                {
                    _currentDir = dm.Groups["dir"].Value.TrimEnd('\\') + "\\";
                }
                else
                {
                    var fm = s_fileLineRx.Match(trimmed);
                    if (fm.Success)
                    {
                        // /MT 模式文件行直接含完整路径；非 /MT 模式只有裸文件名，需拼当前目录
                        var name = fm.Groups["name"].Value.Trim();
                        string? full = name.StartsWith(@"\\", StringComparison.Ordinal) ||
                                       (name.Length > 2 && name[1] == ':')
                            ? name
                            : _currentDir != null ? _currentDir + name : null;
                        if (full != null)
                        {
                            try { FileCopied?.Invoke(full, ParseRoboSize(fm.Groups["size"].Value, fm.Groups["unit"].Value)); }
                            catch { /* 进度事件永不影响传输 */ }
                        }
                    }
                }
                try { OutputLine?.Invoke(trimmed); } catch { /* UI 订阅方异常绝不能杀死传输泵 */ }
                _log.Debug("robocopy| {Line}", trimmed);
            });
            var stderrTask = PumpAsync(proc.StandardError, line =>
            {
                var trimmed = line.TrimEnd();
                if (trimmed.Length == 0) return;
                lastErrorLine = trimmed;
                try { OutputLine?.Invoke(trimmed); } catch { /* 同上 */ }
                _log.Warning("robocopy stderr| {Line}", trimmed);
            });

            await using var reg = ct.Register(() =>
            {
                try { KillCurrent(); } catch { /* ignore */ }
            });

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

            sw.Stop();
            var code = proc.ExitCode;
            // 被强杀的进程退出码为 -1：以 _killedByUs / 负码为准，绝不能落入 IsSuccess
            var killed = _killedByUs || code < 0;
            var success = !killed && IsSuccess(code);
            _log.Information("robocopy 退出 [{Pass}]: ExitCode={Code} (0x{CodeHex:X2}) Success={Success} Killed={Killed} 耗时 {Elapsed}",
                pass, code, code, success, killed, sw.Elapsed);
            return new RobocopyRunResult(code, success, Killed: killed, sw.Elapsed, lastErrorLine, errorLines);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            _log.Warning("robocopy 被取消 [{Pass}]（进程已终止，可后续续传）", pass);
            return new RobocopyRunResult(-1, false, Killed: true, sw.Elapsed, "canceled");
        }
        finally { _current = null; }
    }

    /// <summary>立即终止当前 robocopy（Immediate 暂停 / 取消）。robocopy 增量语义保证下次运行自动续接。</summary>
    public void KillCurrent()
    {
        var p = _current;
        if (p == null || p.HasExited) return;
        _killedByUs = true;
        _log.Warning("强制终止 robocopy (PID {Pid})", p.Id);
        try { p.Kill(entireProcessTree: true); } catch (Exception ex) { _log.Warning(ex, "终止 robocopy 失败"); }
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                onLine(line);
        }
        catch { /* 进程被杀死时管道会断开，属正常 */ }
    }
}
