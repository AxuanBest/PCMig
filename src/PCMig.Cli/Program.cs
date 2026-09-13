using PCMig.Core.Jobs;
using PCMig.Core.Logging;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Native;
using PCMig.Core.Planning;
using PCMig.Core.Preflight;
using PCMig.Core.Report;
using PCMig.Core.Scan;
using PCMig.Core.State;
using PCMig.Core.Transfer;
using PCMig.Core.Util;
using PCMig.Core.Verify;
using Serilog;

namespace PCMig.Cli;

/// <summary>
/// PCMig 命令行入口。子命令：preflight / new / run / pause / resume / status / verify / report / list / quick
/// </summary>
internal static class Program
{
    private static ILogger _log = Serilog.Core.Logger.None;
    private static readonly CancellationTokenSource AppCts = new();

    private static async Task<int> Main(string[] args)
    {
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; AppCts.Cancel(); };

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        var cmd = args[0].ToLowerInvariant();
        var opt = ParseOptions(args.Skip(1).ToArray());

        if (opt.TryGetValue("jobs", out var jobsDir))
            Environment.SetEnvironmentVariable("PCMIG_JOBS", jobsDir);

        _log = LogBootstrap.CreateAppLogger(console: true, level:
            opt.ContainsKey("quiet") ? Serilog.Events.LogEventLevel.Warning : Serilog.Events.LogEventLevel.Information);
        Log.Logger = _log;

        try
        {
            return cmd switch
            {
                "preflight" => await CmdPreflight(opt),
                "new" => await CmdNew(opt),
                "run" => await CmdRun(opt),
                "pause" => CmdPause(opt, immediate: false),
                "stop" => CmdPause(opt, immediate: true),
                "resume" => await CmdResume(opt),
                "status" => await CmdStatus(opt),
                "verify" => await CmdVerify(opt),
                "report" => CmdReport(opt),
                "list" => CmdList(),
                "quick" => await CmdQuick(opt),
                // 彩蛋：开发者署名（不进 usage，输入 pcmig credit 触发）
                "credit" or "credits" or "author" => Credit(),
                _ => Fail($"未知命令: {cmd}")
            };
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\n已取消（Ctrl+C）。已完成的对象已写入 Receipt，可用 resume 续传。");
            _log.Warning("用户取消（Ctrl+C）");
            return 2;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "命令执行失败: {Cmd}", cmd);
            Console.WriteLine($"\n✘ 失败：{ex.Message}");
            Console.WriteLine($"  详细日志：{LogBootstrap.AppLogDir}");
            return 1;
        }
        finally
        {
            await Serilog.Log.CloseAndFlushAsync();
        }
    }

    // ----------------------------------------------------------

    /// <summary>从 UNC 路径推导共享根：\\host\share\x\y → \\host\share。</summary>
    private static string? DeriveShareRoot(string unc)
    {
        var parts = unc.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : null;
    }

    /// <summary>支持 --host 直接给 \\IP\共享名：拆出主机；sharePart 为共享名（可能为 null）。</summary>
    private static string NormalizeHost(string raw, out string? sharePart)
    {
        sharePart = null;
        var h = raw.Trim();
        if (h.StartsWith(@"\\"))
        {
            var parts = h.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            h = parts.Length > 0 ? parts[0] : h;
            if (parts.Length > 1) sharePart = parts[1];
        }
        return h;
    }

    private static async Task<int> CmdPreflight(Dictionary<string, string> opt)
    {
        var host = NormalizeHost(Required(opt, "host"), out var sharePart);
        var (user, pass) = GetCredentials(opt);
        var sources = opt.TryGetValue("source", out var s)
            ? s.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList()
            : sharePart != null ? new List<string> { $@"\\{host}\{sharePart}" } : new List<string>();
        var target = opt.GetValueOrDefault("target");

        var checker = new PreflightChecker(_log);
        var report = await checker.RunAsync(host, user, pass, sources, target);

        Console.WriteLine($"\n===== Preflight: {host} =====");
        foreach (var c in report.Checks)
        {
            var mark = c.Pass ? "✔" : c.Severity == "Warning" ? "⚠" : "✘";
            Console.WriteLine($" {mark} {c.Name}: {c.Detail}");
        }
        if (report.Shares.Count > 0)
        {
            Console.WriteLine("\n发现的磁盘共享:");
            foreach (var sh in report.Shares)
                Console.WriteLine($"  {sh.UncPath,-40} {(sh.IsAdminShare ? "[管理共享]" : "")} {sh.Remark}");
        }
        Console.WriteLine($"\n总体: {(report.OverallPass ? "通过，可以迁移" : "存在阻断项，请先解决 ✘ 项")}");
        return report.OverallPass ? 0 : 1;
    }

    private static async Task<int> CmdNew(Dictionary<string, string> opt)
    {
        var host = NormalizeHost(Required(opt, "host"), out var sharePart);
        var target = Required(opt, "target");
        var hasPaths = opt.TryGetValue("paths", out var pathsRaw) && !string.IsNullOrWhiteSpace(pathsRaw);
        var sources = opt.TryGetValue("source", out var sv)
            ? sv.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList()
            : sharePart != null
                ? new List<string> { $@"\\{host}\{sharePart}" }
                : hasPaths
                    ? pathsRaw!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(DeriveShareRoot).Where(r => r != null).Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(r => r!).ToList()
                    : throw new ArgumentException("缺少必填参数: --source \\\\主机\\共享名（或在 --host 里直接写 \\\\IP\\共享名）");
        var (user, pass) = GetCredentials(opt);
        var kind = ParseKind(opt.GetValueOrDefault("kind") ?? "data");

        var def = new JobDefinition
        {
            SourceHost = host,
            SourceUser = user,
            TargetRoot = target,
            Sources = sources.Select(s => new SourceSpec { Path = s, Kind = kind }).ToList(),
            CreatedBy = $"{Environment.UserDomainName}\\{Environment.UserName}",
            Notes = opt.GetValueOrDefault("notes") ?? ""
        };
        if (opt.TryGetValue("paths", out var paths))
            def.CustomSelections = paths.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        ApplyCustomExclusions(def, opt);
        ApplyOptions(def.Options, opt);

        var (rc, runNow) = await PrepareJobAsync(def, user, pass, opt);
        if (!runNow) return rc;
        return await CmdRun(new Dictionary<string, string>(opt) { ["job"] = _lastCreatedJobId! });
    }

    private static async Task<int> CmdQuick(Dictionary<string, string> opt)
    {
        // quick = preflight + new + run 一条龙
        var host = NormalizeHost(Required(opt, "host"), out var sharePart);
        var target = Required(opt, "target");
        var (user, pass) = GetCredentials(opt);

        _log.Information("quick 模式: {Host} → {Target}", host, target);
        var checker = new PreflightChecker(_log);

        List<string> sources;
        string? quickPaths = null;
        if (opt.TryGetValue("paths", out var qpaths) && !string.IsNullOrWhiteSpace(qpaths))
        {
            quickPaths = qpaths;
            sources = qpaths.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(DeriveShareRoot).Where(r => r != null).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(r => r!).ToList();
        }
        else if (opt.TryGetValue("source", out var s))
        {
            sources = s.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        else if (sharePart != null)
        {
            sources = new List<string> { $@"\\{host}\{sharePart}" };
        }
        else
        {
            var pre = await checker.RunAsync(host, user, pass, [], target);
            if (!pre.OverallPass)
            {
                Console.WriteLine("✘ Preflight 未通过，无法继续。详见上方检查项与日志。");
                return 1;
            }
            // 默认策略：所有非管理共享；若没有则退回 D$、E$
            sources = pre.Shares.Where(sh => !sh.IsAdminShare).Select(sh => sh.UncPath).ToList();
            if (sources.Count == 0)
                sources = pre.Shares.Where(sh => sh.Name is "D$" or "E$" or "F$").Select(sh => sh.UncPath).ToList();
            if (sources.Count == 0)
            {
                Console.WriteLine("未发现可迁移的磁盘共享。请用 --source \\\\HOST\\D$ 显式指定。");
                return 1;
            }
        }

        Console.WriteLine($"迁移源: {string.Join(", ", sources)}");
        var def = new JobDefinition
        {
            SourceHost = host,
            SourceUser = user,
            TargetRoot = target,
            Sources = sources.Select(x => new SourceSpec { Path = x, Kind = ObjectKind.DataVolume }).ToList(),
            CreatedBy = $"{Environment.UserDomainName}\\{Environment.UserName}",
            Notes = "quick"
        };
        if (quickPaths != null)
            def.CustomSelections = quickPaths.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        ApplyCustomExclusions(def, opt);
        ApplyOptions(def.Options, opt);

        var (rc, runNow) = await PrepareJobAsync(def, user, pass, opt);
        if (!runNow) return rc;
        var jobId = _lastCreatedJobId!;
        Console.WriteLine($"\n开始传输 {jobId} …（暂停：pcmig pause --job {jobId}；查看：pcmig status --job {jobId}）\n");
        return await CmdRun(new Dictionary<string, string>(opt) { ["job"] = jobId });
    }

    /// <summary>彩蛋：开发者署名。刻意不进 usage——懂的人自然会输入。</summary>
    private static int Credit()
    {
        Console.WriteLine();
        Console.WriteLine("  ╔══════════════════════════════════════════╗");
        Console.WriteLine("  ║   PCMig — 企业内网 Windows 迁移工具       ║");
        Console.WriteLine("  ║                                          ║");
        Console.WriteLine("  ║   设计 & 开发: 郑子轩 (Axuanbest)         ║");
        Console.WriteLine("  ║   © 2026 郑子轩. All rights reserved.    ║");
        Console.WriteLine("  ║                                          ║");
        Console.WriteLine("  ║   每一行代码，都值得被认真对待。          ║");
        Console.WriteLine("  ╚══════════════════════════════════════════╝");
        Console.WriteLine();
        _log.Debug("credit: crafted by 郑子轩 (Axuanbest)");
        return 0;
    }

    private static string? _lastCreatedJobId;

    /// <summary>new/quick 共用：preflight → scan → plan →（可选）人工确认。返回 (退出码, 是否立即执行)。</summary>
    private static async Task<(int code, bool runNow)> PrepareJobAsync(JobDefinition def, string? user, string? pass,
        Dictionary<string, string> opt)
    {
        // ---- 目标路径前置校验：是文件/含非法字符 → 明确阻断（傻瓜操作防线）----
        if (string.IsNullOrWhiteSpace(def.TargetRoot))
        {
            Console.WriteLine("✘ 目标路径不能为空。");
            return (1, false);
        }
        if (def.TargetRoot.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            Console.WriteLine($"✘ 目标路径包含非法字符: {def.TargetRoot}");
            return (1, false);
        }
        try
        {
            var full = Path.GetFullPath(def.TargetRoot);
            if (File.Exists(full))
            {
                Console.WriteLine($"✘ 目标位置已存在一个同名文件而不是文件夹: {full}。请换一个目标目录。");
                return (1, false);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✘ 目标路径无效: {ex.Message}");
            return (1, false);
        }

        // ---- 断点提醒：同主机+同目标存在未完成任务（且无存活进程持有）→ 主动询问续传 ----
        var jmPre = new JobManager(_log);
        var unfinished = jmPre.FindUnfinished(def.SourceHost, def.TargetRoot);
        if (unfinished.Count > 0)
        {
            var u = unfinished[0];
            Console.WriteLine($"⚠ 检测到未完成的迁移任务: {u.JobId}（{u.Phase}，已传 {u.Percent:0.0}%）");
            var resume = opt.ContainsKey("yes"); // --yes 模式下自动续传
            if (!resume && !Console.IsInputRedirected)
            {
                Console.Write("从中断处继续上次任务? [Y/n] ");
                var a = Console.ReadLine()?.Trim();
                resume = string.IsNullOrEmpty(a) || a.Equals("y", StringComparison.OrdinalIgnoreCase);
            }
            if (resume)
            {
                Console.WriteLine($"继续任务 {u.JobId} …");
                var rcResume = await CmdResume(new Dictionary<string, string>(opt) { ["job"] = u.JobId });
                return (rcResume, false);
            }
            Console.WriteLine("将创建新任务（旧任务保留，随时可用 resume 恢复）");
        }

        var jm = new JobManager(_log);
        var ctx = jm.Create(def);
        _lastCreatedJobId = ctx.JobId;
        var jobLog = LogBootstrap.CreateJobLogger(ctx.JobDir, ctx.JobId);

        // Preflight（把结论存入 Job 存档）
        var checker = new PreflightChecker(jobLog);
        var pre = await checker.RunAsync(def.SourceHost, user, pass,
            def.Sources.Where(x => x.Enabled).Select(x => x.Path).ToList(), def.TargetRoot);
        ctx.SavePreflight(pre);
        if (!pre.OverallPass)
        {
            var st = ctx.LoadStateOrNew(); st.Phase = JobPhase.Failed; st.LastError = "Preflight 未通过"; ctx.SaveState(st);
            Console.WriteLine($"✘ Preflight 未通过（{ctx.JobId}）。修正后可重新运行 pcmig run --job {ctx.JobId}");
            return (1, false);
        }

        // Scan
        var matrix = MigrationMatrix.Load(opt.GetValueOrDefault("matrix"), jobLog)
            .WithExtraExclusions(def.CustomExclusions, jobLog);
        var scanner = new SourceScanner(jobLog);
        Console.WriteLine("扫描源数据中…");
        var observed = await scanner.ScanAsync(def, matrix,
            new Progress<string>(m => Console.Write($"\r  {m,-80}")), AppCts.Token);
        Console.WriteLine();
        ctx.SaveObserved(observed);
        foreach (var w in observed.Warnings) Console.WriteLine($"⚠ {w}");

        // Plan
        var planner = new Planner(jobLog);
        var plan = planner.CreatePlan(def, observed, matrix);
        ctx.SavePlan(plan);

        var st2 = ctx.LoadStateOrNew(); st2.Phase = JobPhase.AwaitingReview;
        st2.TotalObjects = plan.Objects.Count; st2.TotalBytes = plan.TotalBytes; ctx.SaveState(st2);

        // ---- 目标盘空间守护：计划总量超过可用空间时阻断（--yes 仅告警不阻断）----
        try
        {
            var targetFull = Path.GetFullPath(def.TargetRoot);
            var root = Path.GetPathRoot(targetFull);
            if (root != null && plan.TotalBytes > 0)
            {
                var free = new DriveInfo(root).AvailableFreeSpace;
                if (free < plan.TotalBytes)
                {
                    var msg = $"目标盘 {root} 可用空间不足：需要 {Format.Bytes(plan.TotalBytes)}，可用 {Format.Bytes(free)}";
                    if (!opt.ContainsKey("yes"))
                    {
                        Console.WriteLine($"✘ {msg}。已保留计划；清理空间或更换目标后执行 pcmig run --job {ctx.JobId}");
                        var stFail = ctx.LoadStateOrNew(); stFail.Phase = JobPhase.AwaitingReview;
                        stFail.LastError = msg; ctx.SaveState(stFail);
                        return (1, false);
                    }
                    Console.WriteLine($"⚠ {msg}（--yes 模式，仍继续；传输可能中途失败）");
                }
            }
        }
        catch { /* 空间检查失败不阻断 */ }

        Console.WriteLine($"\n===== 迁移计划 {ctx.JobId} =====");
        Console.WriteLine($" 对象数: {plan.Objects.Count}    总量: {Format.Bytes(plan.TotalBytes)}");
        foreach (var o in plan.Objects.Take(20))
            Console.WriteLine($"  {o.ObjectId}  {Format.Bytes(o.EstimatedBytes),12}  {o.SourcePath} → {o.TargetPath}{(o.UseRestartablePass ? " [含大文件/Z通道]" : "")}");
        if (plan.Objects.Count > 20) Console.WriteLine($"  … 其余 {plan.Objects.Count - 20} 个对象略");
        Console.WriteLine($" 策略排除: {plan.ExcludedByPolicy.Count} 条（见 plan.json）");
        Console.WriteLine($" Job 目录: {ctx.JobDir}");

        if (!opt.ContainsKey("yes"))
        {
            Console.Write("\n确认执行? [y/N] ");
            var key = Console.ReadLine()?.Trim();
            if (!string.Equals(key, "y", StringComparison.OrdinalIgnoreCase))
            {
            Console.WriteLine($"已保留计划。执行 pcmig run --job {ctx.JobId}");
                return (0, false);
            }
        }
        return (0, true);
    }

    private static async Task<int> CmdRun(Dictionary<string, string> opt)
    {
        var jm = new JobManager(_log);
        var ctx = jm.Open(Required(opt, "job"));
        using var jobLock = JobLock.TryAcquire(ctx, out var lockReason);
        if (jobLock == null)
        {
            Console.WriteLine($"✘ {lockReason}");
            return 4;
        }
        ctx.ClearPauseRequest();
        var jobLog = LogBootstrap.CreateJobLogger(ctx.JobDir, ctx.JobId);
        var matrix = MigrationMatrix.Load(opt.GetValueOrDefault("matrix"), jobLog);

        // ---- 任务带账号时显式建立 SMB 会话（不依赖 Windows 恰好缓存着会话）----
        IDisposable? session = null;
        if (!string.IsNullOrEmpty(ctx.Definition.SourceUser))
        {
            var pass = opt.GetValueOrDefault("password");
            if (pass == null && !Console.IsInputRedirected)
            {
                Console.Write($"密码 ({ctx.Definition.SourceUser}，直接回车=尝试已缓存的 Windows 会话): ");
                pass = ReadPassword();
                Console.WriteLine();
            }
            if (!string.IsNullOrEmpty(pass))
            {
                try { session = NetworkShare.Connect(ctx.Definition.SourceHost, ctx.Definition.SourceUser, pass, jobLog); }
                catch (Exception ex) { Console.WriteLine($"✘ 连接失败: {ex.Message}"); return 4; }
            }
        }

        var orchestrator = new TransferOrchestrator(ctx, matrix, jobLog);
        var progress = new Progress<ProgressSnapshot>(s =>
        {
            try
            {
                if (Console.IsOutputRedirected)
                {
                    Console.WriteLine($"{s.Percent,5:0.0}%  {Format.Bytes(s.CompletedBytes)}/{Format.Bytes(s.TotalBytes)}  {Format.Speed(s.BytesPerSecond)}  [{s.Message}]");
                    return;
                }
                var w = Math.Max(40, Console.BufferWidth - 1);
                var bar = ProgressBar(s.Percent, 30);
                var line = $"\r{bar} {s.Percent,5:0.0}%  {Format.Bytes(s.CompletedBytes)}/{Format.Bytes(s.TotalBytes)}" +
                           $"  {Format.Speed(s.BytesPerSecond)}  ETA {Format.Eta(s.EtaSeconds)}  [{s.Message}]";
                Console.Write(line.Length > w ? line[..w] : line.PadRight(w));
            }
            catch { /* 进度渲染失败不影响传输 */ }
        });

        var phase = await orchestrator.RunAsync(progress, AppCts.Token);
        session?.Dispose();
        Console.WriteLine();
        Console.WriteLine(phase switch
        {
            JobPhase.Completed => $"✔ 迁移完成。生成报告: pcmig report --job {ctx.JobId}；验证: pcmig verify --job {ctx.JobId}",
            JobPhase.CompletedWithErrors => $"◐ 完成但有失败对象，请查看报告: pcmig report --job {ctx.JobId}",
            JobPhase.Paused => $"‖ 已暂停。恢复: pcmig resume --job {ctx.JobId}",
            JobPhase.Interrupted => $"⏸ 已中断（可续传）。恢复: pcmig resume --job {ctx.JobId}",
            _ => $"阶段结束: {phase}"
        });
        return phase is JobPhase.Completed or JobPhase.CompletedWithErrors ? 0 : 3;
    }

    private static int CmdPause(Dictionary<string, string> opt, bool immediate)
    {
        var jm = new JobManager(_log);
        var ctx = jm.Open(Required(opt, "job"));
        ctx.RequestPause(immediate);
        Console.WriteLine(immediate
                ? $"已请求立即暂停 {ctx.JobId}（当前 robocopy 将被终止，已传部分保留，可续传）"
                : $"已请求协作式暂停 {ctx.JobId}（当前对象传完后停止）");
        return 0;
    }

    private static async Task<int> CmdResume(Dictionary<string, string> opt)
    {
        var jm = new JobManager(_log);
        if (!opt.ContainsKey("job"))
        {
            // 免参数 resume：取最近的未完成且未被占用的任务
            var latest = jm.ListAll()
                .Where(j => j.Phase is JobPhase.Running or JobPhase.Paused or JobPhase.Interrupted
                    or JobPhase.AwaitingReview or JobPhase.CompletedWithErrors)
                .Where(j => !JobManager.IsLocked(j.JobDir))
                .FirstOrDefault();
            if (latest == null)
            {
                Console.WriteLine("没有可恢复的未完成任务。用 pcmig-cli list 查看全部。");
                return 1;
            }
            Console.WriteLine($"自动选择最近的未完成任务: {latest.JobId}（{latest.Phase}）");
            opt = new Dictionary<string, string>(opt) { ["job"] = latest.JobId };
        }
        var ctx = jm.Open(Required(opt, "job"));
        ctx.ClearPauseRequest();
        Console.WriteLine($"恢复 {ctx.JobId}…");
        return await CmdRun(opt);
    }

    private static async Task<int> CmdStatus(Dictionary<string, string> opt)
    {
        var jm = new JobManager(_log);
        var ctx = jm.Open(Required(opt, "job"));
        var watch = opt.ContainsKey("watch");
        do
        {
            var s = ctx.LoadStateOrNew();
            Console.WriteLine($"Job {s.JobId}  阶段: {s.Phase}  进度: {s.Percent:0.0}%  " +
                $"对象 {s.CompletedObjects}/{s.TotalObjects}（失败 {s.FailedObjects}）" +
                $"{Format.Bytes(s.CompletedBytes)}/{Format.Bytes(s.TotalBytes)}  {Format.Speed(s.BytesPerSecond)}  " +
                $"更新于 {s.LastUpdateUtc.ToLocalTime():HH:mm:ss}");
            if (!watch) break;
            await Task.Delay(2000, AppCts.Token);
        } while (true);
        return 0;
    }

    private static async Task<int> CmdVerify(Dictionary<string, string> opt)
    {
        var jm = new JobManager(_log);
        var ctx = jm.Open(Required(opt, "job"));
        var jobLog = LogBootstrap.CreateJobLogger(ctx.JobDir, ctx.JobId);
        var matrix = MigrationMatrix.Load(opt.GetValueOrDefault("matrix"), jobLog);
        var level = opt.TryGetValue("level", out var lv) && lv == "2"
            ? VerifyLevel.L2_SampleHash : VerifyLevel.L1_CountSize;

        var verifier = new Verifier(ctx, matrix, jobLog);
        Console.WriteLine($"验证中（{level}）…");
        var report = await verifier.RunAsync(level, new Progress<string>(m => Console.Write($"\r  {m,-60}")), AppCts.Token);
        Console.WriteLine();
        foreach (var o in report.Objects)
        {
            var mark = o.Status == "OK" ? "✔" : "✘";
            Console.WriteLine($" {mark} {o.ObjectId}: 文件 {o.TargetFiles}/{o.SourceFiles}, 字节 {Format.Bytes(o.TargetBytes)}/{Format.Bytes(o.SourceBytes)}" +
                    (o.HashSampled > 0 ? $", 哈希 {o.HashSampled} 项/不一致 {o.HashMismatched}" : ""));
            foreach (var m in o.MissingSamples.Take(5)) Console.WriteLine($"     缺失: {m}");
        }
        Console.WriteLine(report.OverallPass ? "\n✔ 全部通过" : "\n✘ 存在不一致，详见 verify-report.json");
        return report.OverallPass ? 0 : 1;
    }

    private static int CmdReport(Dictionary<string, string> opt)
    {
        var jm = new JobManager(_log);
        var ctx = jm.Open(Required(opt, "job"));
        var jobLog = LogBootstrap.CreateJobLogger(ctx.JobDir, ctx.JobId);
        var path = new ReportGenerator(ctx, jobLog).Generate();
        Console.WriteLine($"报告: {path}");
        if (opt.ContainsKey("open")) System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        return 0;
    }

    private static int CmdList()
    {
        var jm = new JobManager(_log);
        var jobs = jm.ListAll();
        if (jobs.Count == 0) { Console.WriteLine($"没有 Job（目录 {JobManager.JobsRoot}）"); return 0; }
        Console.WriteLine($"{"JobId",-28} {"阶段",-18} {"进度",7} {"源",-18} {"目标",-26} 更新时间");
        foreach (var j in jobs)
            Console.WriteLine($"{j.JobId,-28} {j.Phase,-18} {j.Percent,6:0.0}% {j.SourceHost,-18} {j.TargetRoot,-26} {j.LastUpdateUtc.ToLocalTime():MM-dd HH:mm}");
        return 0;
    }

    // ----------------------------------------------------------

    private static (string? user, string? pass) GetCredentials(Dictionary<string, string> opt)
    {
        var user = opt.GetValueOrDefault("user");
        var pass = opt.GetValueOrDefault("password");
        if (user != null && pass == null)
        {
            if (Console.IsInputRedirected)
            {
                // 非交互环境（脚本/管道）：无法弹密码提示，按空密码处理并明示
                Console.WriteLine("(非交互环境：未提供 --password，按空密码处理)");
                pass = "";
            }
            else
            {
                Console.Write($"密码 ({user}，直接回车=空密码): ");
                pass = ReadPassword();
                Console.WriteLine();
            }
        }
        return (user, pass);
    }

    private static string ReadPassword()
    {
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace && sb.Length > 0) { sb.Length--; Console.Write("\b \b"); }
            else if (!char.IsControl(key.KeyChar)) { sb.Append(key.KeyChar); Console.Write('*'); }
        }
        return sb.ToString();
    }

    private static ObjectKind ParseKind(string k) => k.ToLowerInvariant() switch
    {
        "data" => ObjectKind.DataVolume,
        "profile" => ObjectKind.UserProfile,
        "appstate" => ObjectKind.AppState,
        _ => ObjectKind.DataVolume
    };

    /// <summary>--xd/--xf 任务级排除规则（超大数据模式；分号分隔多项）。扫描/传输/验证同口径。</summary>
    private static void ApplyCustomExclusions(JobDefinition def, Dictionary<string, string> opt)
    {
        var excl = new List<string>();
        if (opt.TryGetValue("xd", out var xd))
            excl.AddRange(xd.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => "/XD " + s));
        if (opt.TryGetValue("xf", out var xf))
            excl.AddRange(xf.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => "/XF " + s));
        if (excl.Count > 0) def.CustomExclusions = excl;
    }

    private static void ApplyOptions(MigrationOptions o, Dictionary<string, string> opt)
    {
        if (opt.TryGetValue("threads", out var t) && int.TryParse(t, out var n))
            o.Threads = Math.Clamp(n, 1, 128); // 落库即钳制，job.json 与实际执行一致
        if (opt.TryGetValue("largemb", out var lm) && int.TryParse(lm, out var mb)) o.LargeFileThresholdMB = mb;
        if (opt.TryGetValue("noverify", out _)) o.VerifyLevel = VerifyLevel.None;
    }

    private static string ProgressBar(double percent, int width)
    {
        var filled = (int)Math.Round(Math.Clamp(percent, 0, 100) / 100.0 * width);
        return "[" + new string('█', filled) + new string('░', width - filled) + "]";
    }

    private static string Required(Dictionary<string, string> opt, string key)
        => opt.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v : throw new ArgumentException($"缺少必需参数 --{key}");

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--")) continue;
            var key = a[2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                dict[key] = args[++i];
            else
                dict[key] = "true"; // 开关型参数
        }
        return dict;
    }

    private static int Fail(string msg) { Console.WriteLine(msg); PrintUsage(); return 1; }

    private static void PrintUsage()
    {
        Console.WriteLine("""
PCMig —— 企业内网 Windows 数据/环境迁移（直拉模式：在新电脑上运行，从旧电脑拉取）
用法:
  pcmig preflight --host <IP或电脑名> [--source \\HOST\D$] [--user U] [--password P]
  pcmig new       --host <H> --source <\\H\D$[;\\H\C$\Users\x]> --target <D:\目录> [--kind data|profile] [--threads 16] [--yes]
  pcmig quick     --host <H> --target <D:\目录> [--source \\H\D$] [--user U]   预检+扫描+计划+传输 一条龙
  pcmig run       --job <JOB-ID>
  pcmig pause     --job <JOB-ID>            协作式暂停（当前对象传完停止）
  pcmig stop      --job <JOB-ID>            立即暂停（终止当前 robocopy，可续传）
  pcmig resume    [--job <JOB-ID>]          恢复任务（不带 --job 自动选最近的未完成任务）
  pcmig status    --job <JOB-ID> [--watch]
  pcmig verify    --job <JOB-ID> [--level 2]
  pcmig report    --job <JOB-ID> [--open]
  pcmig list

全局参数: --jobs <目录> 覆盖 Job 根目录；--matrix <yaml> 指定策略矩阵；--quiet
性能/排除: --threads <8|16|32|64|128>  robocopy /MT 线程数（默认 16，随任务存档，续传沿用）
           --xd "目录1;目录2"           排除目录（/XD，超大数据模式同规则）
           --xf "*.tmp;*.iso"           排除文件（/XF，支持通配符）
示例:
  pcmig quick --host 192.168.1.50 --target D:\ --user OLD-PC\Administrator
""");
    }
}

