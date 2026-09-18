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

        // 状态落盘告警出口（v0.3.5）：状态文件被外部程序独占占用、本次写入被跳过时，
        // 此前 OnWarning 全仓库无人赋值 → 整条链路静默。这里接到既有日志设施上，
        // 同时写一份 stderr，便于脚本/批处理捕获（--quiet 下仍然可见，因为是 Warning 级）。
        PCMig.Core.State.JsonStateStore.OnWarning = msg =>
        {
            _log.Warning("状态告警: {Message}", msg);
            try { Console.Error.WriteLine("[WRN] " + msg); } catch { /* stderr 不可写不影响任务 */ }
        };

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
                "changelog" or "log" or "history" => CmdChangelog(),
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
        var report = await checker.RunAsync(host, user, pass, sources, target, benchmark: opt.ContainsKey("bench"));

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
        Console.WriteLine("  ║   郑子轩 个人制作                         ║");
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

        // ---- 扫描残缺闸门（v0.3.8）：存在不可访问目录时默认不允许开始迁移 ----
        var gate = ScanGate.Inspect(observed, def.AllowIncompleteScan);
        if (gate.HasIncomplete)
        {
            if (opt.ContainsKey("allow-incomplete-scan"))
            {
                def.AllowIncompleteScan = true;
                ctx.SaveDefinition();
                Console.WriteLine($"⚠ 已按 --allow-incomplete-scan 显式确认：{gate.Count} 处不可访问位置仍要继续迁移（已写入 job.json）。");
                _log.Warning("用户显式确认：扫描存在 {Count} 处不可访问位置，仍继续迁移", gate.Count);
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine(ScanGate.BuildBlockMessage(gate, cli: true));
                var stGate = ctx.LoadStateOrNew();
                stGate.Phase = JobPhase.AwaitingReview;
                stGate.LastError = $"扫描存在 {gate.Count} 处不可访问位置，默认拦截（需修好或 --allow-incomplete-scan 显式确认）";
                ctx.SaveState(stGate);
                Console.WriteLine($" Job 目录: {ctx.JobDir}");
                return (3, false);
            }
        }

        // Plan
        var planner = new Planner(jobLog);
        var plan = planner.CreatePlan(def, observed, matrix);
        ctx.SavePlan(plan);

        // 空计划 = 用户选定的路径全部不可达（全部被跳过）→ 当场失败，绝不进入传输（防"✔ 迁移完成 0B"错觉）
        if (plan.Objects.Count == 0)
        {
            var stEmpty = ctx.LoadStateOrNew(); stEmpty.Phase = JobPhase.Failed;
            stEmpty.LastError = "计划为空：所有选定路径均不存在或不可访问";
            ctx.SaveState(stEmpty);
            Console.WriteLine("✘ 计划为空：所有选定路径均不存在或不可访问（见上方 ⚠ 提示），没有任何数据被迁移。请核对源路径/共享名后重试。");
            return (1, false);
        }

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
                    var msg = $"目标盘 {root} 剩余空间不足（新建任务前置校验）：需要 {Format.Bytes(plan.TotalBytes)}，可用 {Format.Bytes(free)}";
                    if (!opt.ContainsKey("yes"))
                    {
                        Console.WriteLine($"✘ {msg}。已保留计划；清理空间或更换目标后执行 pcmig run --job {ctx.JobId}");
                        Console.WriteLine("   （写满后每个受影响对象都会如实记成失败、释放空间后 resume 可补齐——但能提前拦住就不该等到写满。）");
                        var stFail = ctx.LoadStateOrNew(); stFail.Phase = JobPhase.AwaitingReview;
                        stFail.LastError = msg; ctx.SaveState(stFail);
                        return (1, false);
                    }
                    Console.WriteLine($"⚠ {msg}（--yes 模式，仍继续；写满时会记失败对象，释放空间后 resume 可补齐）");
                }
                else
                {
                    Console.WriteLine($"目标盘 {root} 剩余空间预检：可用 {Format.Bytes(free)} / 计划 {Format.Bytes(plan.TotalBytes)}，充足。");
                }
            }
        }
        catch (Exception ex)
        {
            // 原来这里静默吞掉：检查失败时用户根本不知道"剩余空间没校验过"（T07-O2）
            Console.WriteLine($"⚠ 目标盘剩余空间预检未能完成（{ex.Message}）：本次不做空间前置判断，" +
                              "写满时仍会如实记录失败对象，释放空间后 resume 可补齐。");
            _log.Warning(ex, "目标盘空间预检失败");
        }

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
        // 暂停/停止后的进程收尾有数秒窗口，resume 撞锁是实测过的坑——给 30 秒宽限重试
        using var jobLock = JobLock.TryAcquire(ctx, TimeSpan.FromSeconds(30), out var lockReason);
        if (jobLock == null)
        {
            Console.WriteLine($"✘ {lockReason}");
            return 4;
        }
        ctx.ClearPauseRequest();
        var jobLog = LogBootstrap.CreateJobLogger(ctx.JobDir, ctx.JobId);
        var matrix = MigrationMatrix.Load(opt.GetValueOrDefault("matrix"), jobLog);

        // ---- 扫描残缺闸门（v0.3.8）：扫描时不可访问的位置会让数据"静默漏传"，默认不允许开跑。
        //      显式确认（--allow-incomplete-scan，或任务里已记 allowIncompleteScan=true）才放行。----
        var gate = ScanGate.Inspect(ctx);
        if (gate.Blocks)
        {
            if (opt.ContainsKey("allow-incomplete-scan"))
            {
                ctx.Definition.AllowIncompleteScan = true;
                ctx.SaveDefinition();
                Console.WriteLine($"⚠ 已按 --allow-incomplete-scan 显式确认：{gate.Count} 处不可访问位置仍要继续（已写入 job.json）。");
                _log.Warning("用户显式确认：扫描存在 {Count} 处不可访问位置，仍继续迁移", gate.Count);
            }
            else
            {
                Console.WriteLine(ScanGate.BuildBlockMessage(gate, cli: true));
                return 5;
            }
        }

        // ---- 开跑前的目标盘剩余空间告警（T07-O2）：算的是"还差多少字节"而不是计划总量，
        //      续传/恢复场景不会被"计划总量"吓到；只告警不阻断——释放空间后 resume 才是既定恢复路径。
        try
        {
            if (ctx.Plan is { TotalBytes: > 0 } planPre)
            {
                var doneBytes = ctx.LoadReceipts(_log)
                    .Where(r => r.Status == ObjectStatus.Completed)
                    .GroupBy(r => r.ObjectId)
                    .Select(g => g.OrderBy(r => r.CompletedUtc).Last())
                    .Sum(r => r.TargetBytes);
                var remaining = Math.Max(0, planPre.TotalBytes - doneBytes);
                var root = Path.GetPathRoot(Path.GetFullPath(ctx.Definition.TargetRoot));
                // 只对本地盘做前置判断；UNC/网络目标不在这里猜（不打印任何东西，避免误报）
                if (root != null && root.Length >= 2 && root[1] == ':')
                {
                    var free = new DriveInfo(root).AvailableFreeSpace;
                    if (remaining > free)
                        Console.WriteLine($"⚠ 目标盘 {root} 剩余空间不足：还需传输 {Format.Bytes(remaining)}，可用 {Format.Bytes(free)}。" +
                            "继续跑会在写满后失败（失败对象都记在回执里），释放空间后执行 resume 可补齐。" +
                            "建议先清理目标盘空间或更换目标盘。");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠ 目标盘剩余空间预检未能完成（{ex.Message}）：继续迁移，写满时会如实记录失败对象。");
            _log.Warning(ex, "run 前目标盘空间预检失败");
        }

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
                try
                {
                    session = NetworkShare.ConnectForTransfer(ctx.Definition.SourceHost, ctx.Definition.SourceUser, pass,
                        ctx.Definition.Sources.Select(s => s.Path).ToList(), jobLog);
                }
                catch (Exception ex) { Console.WriteLine($"✘ 连接失败: {ex.Message}"); return 4; }
            }
        }

        var orchestrator = new TransferOrchestrator(ctx, matrix, jobLog);
        orchestrator.TransferNotice += m => Console.WriteLine($"[提示] {m}");
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
        var finalState = ctx.LoadStateOrNew();
        var spaceShort = finalState.LastError?.Contains("空间不足") == true;
        Console.WriteLine(phase switch
        {
            JobPhase.Completed => $"✔ 迁移完成。生成报告: pcmig report --job {ctx.JobId}；验证: pcmig verify --job {ctx.JobId}",
            JobPhase.CompletedWithErrors when spaceShort =>
                $"◐ 目标磁盘空间不足，本次运行已提前停止（避免对每个文件反复重试）。" +
                $"释放目标盘空间后恢复: pcmig resume --job {ctx.JobId}（已完成对象不会重传）",
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
            Console.WriteLine($"自动选择最近的未完成任务: {latest.JobId}（{latest.PhaseText}）");
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
        // 视图不可信（T14/O6）：损坏（非法 JSON / 空文件）或被删除且回执还在 → 进度是"未知"，
        // 绝不能显示 0.0%／对象 0——脚本只看数字会把"其实传了 45%"读成"一点没传"。
        var unreliable = ctx.StateViewUnreliable;
        if (unreliable)
            Console.WriteLine(ctx.StateFileCorrupted
                ? "⚠ 状态文件 job-state.json 损坏无法解析：进度未知（待由回执重建），下面不显示 0.0% 这类会误导脚本的数字；回执与验证结果不受影响。"
                : "⚠ 状态文件 job-state.json 已缺失（但回执还在，说明任务确实跑过）：进度未知（待由回执重建）；回执与验证结果不受影响。");
        do
        {
            var s = ctx.LoadStateOrNew();
            var liveHolder = JobManager.IsLocked(ctx.JobDir);
            var stale = !unreliable && PhaseView.IsStale(s.Phase, liveHolder, ctx.HasReceipts);
            var phaseText = unreliable
                ? "未知（状态文件损坏/缺失，待由回执重建）"
                : PhaseView.Describe(s.Phase, liveHolder, ctx.HasReceipts);
            Console.WriteLine(unreliable
                ? $"Job {s.JobId}  阶段: {phaseText}  进度: 状态损坏，待由回执重建（本行故意不显示 0.0%）  " +
                  $"对象: 未知（以回执为准，run/resume 会按回执重建）  更新于 {s.LastUpdateUtc.ToLocalTime():HH:mm:ss}"
                : $"Job {s.JobId}  阶段: {phaseText}  进度: {s.Percent:0.0}%  " +
                  $"对象 {s.CompletedObjects}/{s.TotalObjects}（失败 {s.FailedObjects}）" +
                  $"{Format.Bytes(s.CompletedBytes)}/{Format.Bytes(s.TotalBytes)}  {Format.Speed(s.BytesPerSecond)}  " +
                  $"更新于 {s.LastUpdateUtc.ToLocalTime():HH:mm:ss}");
            if (stale)
                Console.WriteLine("  ⏸ 陈旧状态：状态文件说在跑，但没有任何进程持有 job.lock（进程已退出/被强杀）。" +
                                  "按「中断」处理，可直接 resume —— 已完成对象不会重传。");
            // 失败/未完成对象清单 + 具体原因（只报"失败 N"等于没说——用户要的是"哪个对象、为什么"）
            try
            {
                var receipts = ctx.LoadReceipts(_log)
                    .GroupBy(r => r.ObjectId)
                    .Select(g => g.OrderByDescending(r => r.CompletedUtc).First())
                    .Where(r => r.Status is ObjectStatus.Failed or ObjectStatus.Interrupted
                        or ObjectStatus.CompletedWithErrors)
                    .OrderBy(r => r.ObjectId)
                    .ToList();
                if (receipts.Count > 0)
                {
                    Console.WriteLine($"\n未完成/有错误的对象 {receipts.Count} 个：");
                    foreach (var r in receipts)
                    {
                        var mark = r.Status == ObjectStatus.CompletedWithErrors ? "⚠" : "✘";
                        Console.WriteLine($"  {mark} {r.ObjectId}  {r.SourcePath}");
                        Console.WriteLine($"     {r.Status}｜{PCMig.Core.Util.ErrorTranslator.ShortReason(r.ErrorDetail)}");
                        Console.WriteLine($"     退出码译文：{PCMig.Core.Util.ErrorTranslator.ExitCodeText(r.RobocopyExitCodeBulk >= 0 ? r.RobocopyExitCodeBulk : -1)}" +
                                          $"（Bulk={r.RobocopyExitCodeBulk} Large={r.RobocopyExitCodeLarge}）｜错误分类 {r.ErrorClass}");
                        if (r.RobocopyExitCodeLarge > 0)
                            Console.WriteLine($"     大文件通道退出码译文：{PCMig.Core.Util.ErrorTranslator.ExitCodeText(r.RobocopyExitCodeLarge)}");
                    }
                }
            }
            catch (Exception ex) { _log.Warning(ex, "读取回执清单失败"); }
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
        // ---- 抽样 0 项必须说清楚（T01）：L2 是抽样校验，一项都没抽到时"全部通过"只等于
        //      "文件数/字节数对账通过"，绝不能让人误以为做过内容级核对（脚本也会把 0 失败读成已全量校验）。
        var sampledTotal = report.Objects.Sum(o => o.HashSampled);
        var mismatchTotal = report.Objects.Sum(o => o.HashMismatched);
        if (level == VerifyLevel.L2_SampleHash)
        {
            if (sampledTotal == 0)
            {
                Console.WriteLine();
                Console.WriteLine("⚠ 未抽样任何对象：本次内容级（SHA-256）校验一个文件都没有抽到。");
                Console.WriteLine("   本次「全部通过」只代表文件数/字节数对账一致，不代表已做过内容级核对。");
                Console.WriteLine($"   原因：L2 是确定性抽样（抽样键 = StableHash(相对路径) % 100 < {Math.Clamp(ctx.Definition.Options.SampleHashPercent, 1, 100)}，每对象上限 2000 项），" +
                                  "对象文件数少、或恰好没有文件落进抽样桶时，抽样数就是 0。");
                Console.WriteLine("   建议：文件数较少时改用逐文件全量核对（tools\\stability-test.ps1 的三方 SHA-256 核对），" +
                                  "或调大 job.json 的 sampleHashPercent 后重跑 pcmig verify --job <ID> --level 2。");
            }
            else
            {
                Console.WriteLine($"\n内容级抽样：共抽 {sampledTotal} 项，哈希不一致 {mismatchTotal} 项。");
            }
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
        Console.WriteLine($"{"JobId",-28} {"阶段",-30} {"进度",9} {"源",-18} {"目标",-26} 更新时间");
        foreach (var j in jobs)
        {
            // 陈旧 running（无存活持有者）与状态损坏在这里也如实说，别让脚本把 running / 0.0% 当真
            var phase = j.StateUnreliable ? "未知（状态损坏，待由回执重建）" : j.PhaseText;
            var progress = j.StateUnreliable ? "未知" : $"{j.Percent:0.0}%";
            Console.WriteLine($"{j.JobId,-28} {phase,-30} {progress,9} {j.SourceHost,-18} {j.TargetRoot,-26} {j.LastUpdateUtc.ToLocalTime():MM-dd HH:mm}");
        }
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
        if (opt.TryGetValue("large-channel", out var lc) && !string.IsNullOrWhiteSpace(lc))
            o.LargeChannelMode = lc.Trim().ToLowerInvariant();
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

    /// <summary>
    /// 更新日志：优先读程序目录（或当前目录）下的 更新日志.txt —— 这样用户改了文本也能立刻看到；
    /// 找不到就用随程序内置的那份（嵌入资源）。
    /// </summary>
    private static int CmdChangelog()
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            try
            {
                var p = Path.Combine(dir, "更新日志.txt");
                if (File.Exists(p)) { Console.WriteLine(File.ReadAllText(p, System.Text.Encoding.UTF8)); return 0; }
            }
            catch { /* 目录不可读就继续找下一个 */ }
        }

        using var s = typeof(Program).Assembly.GetManifestResourceStream("PCMig.Cli.CHANGELOG.md");
        if (s == null) { Console.WriteLine("未找到更新日志（程序目录下缺少 更新日志.txt，且内置副本不可用）。"); return 1; }
        using var r = new StreamReader(s, System.Text.Encoding.UTF8);
        Console.WriteLine(r.ReadToEnd());
        return 0;
    }

    private static int Fail(string msg) { Console.WriteLine(msg); PrintUsage(); return 1; }

    private static void PrintUsage()
    {
        Console.WriteLine("""
PCMig —— 企业内网 Windows 数据/环境迁移（直拉模式：在新电脑上运行，从旧电脑拉取）
用法:
  pcmig preflight --host <IP或电脑名> [--source \\HOST\D$] [--user U] [--password P] [--bench 链路测速]
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
  pcmig changelog                           查看从 v0.1.0 起的所有版本更新内容（同安装目录的 更新日志.txt）

全局参数: --jobs <目录> 覆盖 Job 根目录；--matrix <yaml> 指定策略矩阵；--quiet
性能/排除: --threads <8|16|32|64|128>  robocopy /MT 线程数（默认 16，随任务存档，续传沿用）
           --xd "目录1;目录2"           排除目录（/XD，超大数据模式同规则）
           --xf "*.tmp;*.iso"           排除文件（/XF，支持通配符）
           --largemb <MB>               大文件分流阈值（默认 512MB；≥该值走大文件通道）
           --large-channel <auto|restartable|multithreaded>
                                       大文件通道：auto=默认 /MT+/J（快，重试时自动退 /Z）；
                                       restartable=恒用 /Z（文件内部可续传，单线程）；multithreaded=恒用 /MT+/J
           --allow-incomplete-scan      扫描存在不可访问目录时，显式确认仍要继续（默认拦截）
示例:
  pcmig quick --host 192.168.1.50 --target D:\ --user OLD-PC\Administrator
""");
    }
}

