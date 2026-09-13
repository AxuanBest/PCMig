using System.Diagnostics;
using System.Text;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Native;
using Serilog;

namespace PCMig.Core.Transfer;

public enum PassKind { Bulk, Large, RootFiles }

public sealed record RobocopyRunResult(int ExitCode, bool Success, bool Killed, TimeSpan Duration, string? LastErrorLine);

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

    public string BuildArguments(string src, string dst, MigrationOptions opt, MigrationMatrix matrix,
        PassKind pass, string unicodeLogPath, IReadOnlyList<string>? fileList = null)
    {
        var sb = new StringBuilder();
        sb.Append('"').Append(src).Append("\" \"").Append(dst).Append('"');

        if (fileList != null)
        {
            foreach (var f in fileList) sb.Append(" \"").Append(f).Append('"');
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
        sb.Append($" /R:{opt.RetryCount} /W:{opt.RetryWaitSec}");

        var thresholdBytes = (long)(matrix.LargeFileThresholdMB > 0 ? matrix.LargeFileThresholdMB : opt.LargeFileThresholdMB) * 1024 * 1024;
        if (pass == PassKind.Bulk)
        {
            sb.Append($" /MT:{Math.Clamp(opt.Threads, 1, 128)}");
            if (opt.SplitLargeFiles) sb.Append($" /MAX:{thresholdBytes - 1}");   // robocopy 用 /MAX 字节数（无 /MAXFILESIZE）
        }
        else if (pass == PassKind.Large)
        {
            sb.Append($" /Z /MIN:{thresholdBytes}");                              // /Z 与 /MT 刻意不同 pass
        }

        foreach (var d in matrix.ExcludedDirectoryNames) sb.Append($" /XD \"{d}\"");
        foreach (var f in matrix.ExcludedFileNames.Concat(matrix.SecurityBlockedFileNames)) sb.Append($" /XF \"{f}\"");

        // /NP 关百分比噪声；/TEE 双写；/UNILOG+ 追加写 Unicode 日志（中文环境不丢字）
        sb.Append($" /NP /TEE /UNILOG+:\"{unicodeLogPath}\"");
        return sb.ToString();
    }

    public async Task<RobocopyRunResult> RunPassAsync(string src, string dst, MigrationOptions opt,
        MigrationMatrix matrix, PassKind pass, string unicodeLogPath, CancellationToken ct,
        IReadOnlyList<string>? fileList = null)
    {
        // 文件清单过长时按命令行长度分批执行，聚合结果
        if (fileList != null && fileList.Count > 0)
        {
            var batches = ChunkFileList(src, dst, opt, matrix, pass, fileList);
            if (batches.Count > 1)
                _log.Information("文件清单 {Count} 项，按命令行长度拆为 {Batches} 批执行", fileList.Count, batches.Count);
            var swAll = Stopwatch.StartNew();
            var worst = 0; string? lastErr = null; var killed = false;
            foreach (var batch in batches)
            {
                var r = await RunSingleAsync(src, dst, opt, matrix, pass, unicodeLogPath, ct, batch);
                if (r.Killed) { killed = true; break; }
                if (r.ExitCode > worst) { worst = r.ExitCode; lastErr = r.LastErrorLine; }
            }
            swAll.Stop();
            return new RobocopyRunResult(killed ? -1 : worst, !killed && IsSuccess(worst), killed, swAll.Elapsed, lastErr);
        }

        return await RunSingleAsync(src, dst, opt, matrix, pass, unicodeLogPath, ct, null);
    }

    /// <summary>按累积命令行长度分批（中文名按 UTF-16 计 2 字节，近似保守）。</summary>
    private List<List<string>> ChunkFileList(string src, string dst, MigrationOptions opt,
        MigrationMatrix matrix, PassKind pass, IReadOnlyList<string> fileList)
    {
        var batches = new List<List<string>>();
        var baseLen = BuildArguments(src, dst, opt, matrix, pass, "x.log", (IReadOnlyList<string>?)null).Length;
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
        IReadOnlyList<string>? fileList)
    {
        var args = BuildArguments(src, dst, opt, matrix, pass, unicodeLogPath, fileList);
        _log.Information("robocopy 启动 [{Pass}]: robocopy {Args}", pass, args);

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
                    trimmed.Contains("错误", StringComparison.Ordinal)) lastErrorLine = trimmed;
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
            return new RobocopyRunResult(code, success, Killed: killed, sw.Elapsed, lastErrorLine);
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
