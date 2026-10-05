using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PCMig.Core.Diagnostics;
using PCMig.Core.Models;
using PCMig.Core.Native;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using Serilog;

namespace PCMig.Core.Preflight;

/// <summary>
/// Preflight：在任何字节移动之前，把"能不能迁"检查清楚。
/// 直拉拓扑的关键检查：SMB 445 可达 → IPC$ 凭据 → 共享枚举 → 源路径可读 → 目标盘空间。
/// </summary>
public sealed class PreflightChecker
{
    private readonly ILogger _log;
    public PreflightChecker(ILogger log) { _log = log.ForContext<PreflightChecker>(); }

    /// <summary>
    /// 探测 TCP 445。电脑名会先解析成地址表，逐个地址各给 2 秒短超时（不共享总预算），任一通即算通。
    /// 返回 IsLiteral=false 表示用户填的是电脑名——这种情况下即使探测全失败也不该判死：
    /// Windows 重定向器可以用 NetBIOS 名或其它地址连上，真正该看的是后面「源路径可读」。
    /// </summary>
    private static async Task<(bool Ok, string Detail, bool IsLiteral)> ProbeSmb445Async(string host)
    {
        var diagnostics = CoreDiagnostics.Sink.Publisher;
        DiagnosticContext Context() => CoreDiagnostics.ContextFor("PreflightChecker");
        void Publish(EventDescriptor descriptor, IDiagnosticPayload payload, DiagnosticLevel level,
            DiagnosticOutcome outcome, int? socketError = null)
        {
            try
            {
                if (!diagnostics.IsEnabledFor(descriptor)) return;
                diagnostics.TryPublish(new DiagnosticEventDraft(
                    descriptor, Context(), payload, Level: level, Outcome: outcome,
                    SocketError: socketError, ErrorDomain: socketError is null ? ErrorDomain.None : ErrorDomain.Socket));
            }
            catch (Exception) { /* 观察失败绝不影响预检 */ }
        }

        var isLiteral = IPAddress.TryParse(host, out var literal);
        // 主机名/IP 属 Personal：只留会话内令牌，绝不写明文。
        var hostAlias = CoreDiagnostics.Sink.Token(host);

        Publish(NetEvents.ProbeStarted, new NetProbeStartedPayload(hostAlias, isLiteral, 0),
            DiagnosticLevel.Information, DiagnosticOutcome.Started);

        var addrs = new List<IPAddress>();
        if (isLiteral) addrs.Add(literal!);
        else
        {
            var dnsWatch = Stopwatch.StartNew();
            try
            {
                foreach (var a in await Dns.GetHostAddressesAsync(host).ConfigureAwait(false))
                    if (a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                        addrs.Add(a);
                dnsWatch.Stop();
                Publish(NetEvents.DnsResolved, new NetDnsResultPayload(true, addrs.Count, dnsWatch.ElapsedMilliseconds, null),
                    DiagnosticLevel.Information, DiagnosticOutcome.Succeeded);
            }
            catch (Exception ex)
            {
                dnsWatch.Stop();
                Publish(NetEvents.DnsFailed, new NetDnsResultPayload(false, 0, dnsWatch.ElapsedMilliseconds, ex.GetType().Name),
                    DiagnosticLevel.Warning, DiagnosticOutcome.Failed);
                return (false, $"电脑名「{host}」用 DNS 解析失败（{ex.Message}）；改由 SMB 直连与源路径可读性判定（Windows 可用 NetBIOS 名连上）。", false);
            }
            if (addrs.Count == 0)
            {
                Publish(NetEvents.DnsFailed, new NetDnsResultPayload(false, 0, dnsWatch.ElapsedMilliseconds, "no-addresses"),
                    DiagnosticLevel.Warning, DiagnosticOutcome.Failed);
                return (false, $"电脑名「{host}」没有解析到任何地址；改由 SMB 直连与源路径可读性判定。", false);
            }
        }

        var tried = new List<string>();
        var addressIndex = 0;
        foreach (var a in addrs.Distinct())
        {
            var index = addressIndex++;
            var tcpWatch = Stopwatch.StartNew();
            try
            {
                using var tcp = new TcpClient(a.AddressFamily);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await tcp.ConnectAsync(a, 445, cts.Token).ConfigureAwait(false);
                tcpWatch.Stop();
                if (tcp.Connected)
                {
                    Publish(NetEvents.TcpProbeAttempt, new NetTcpProbePayload(index, tcpWatch.ElapsedMilliseconds, true),
                        DiagnosticLevel.Debug, DiagnosticOutcome.Succeeded);
                    return (true, isLiteral ? $"TCP 445 连接成功（{a}）" : $"TCP 445 连接成功（{host} → {a}）", isLiteral);
                }
                Publish(NetEvents.TcpProbeAttempt, new NetTcpProbePayload(index, tcpWatch.ElapsedMilliseconds, false),
                    DiagnosticLevel.Debug, DiagnosticOutcome.Failed);
            }
            catch (Exception ex)
            {
                tcpWatch.Stop();
                var socketError = ex is SocketException se ? se.SocketErrorCode : (SocketError?)null;
                Publish(NetEvents.TcpProbeFailed, new NetTcpProbePayload(index, tcpWatch.ElapsedMilliseconds, false),
                    DiagnosticLevel.Warning, DiagnosticOutcome.Failed, socketError is null ? null : (int)socketError.Value);
                tried.Add($"{a} 不通({ex.GetType().Name})");
            }
        }

        var tail = isLiteral
            ? "。目标未开机 / IP 错误 / 防火墙未放行文件共享——注意这里要填旧电脑的 IP 或电脑名，不是用户名。"
            : "。不阻断：电脑名可能由 NetBIOS/重定向器解析到别的地址，只要下面的「源路径」可读就能迁移。";
        return (false,
            $"TCP 445 探测未通：{host}" + (isLiteral ? "" : $"（DNS 解析到 {string.Join("、", addrs)}）") +
            "，逐个地址尝试：" + string.Join("；", tried) + tail, isLiteral);
    }

    /// <param name="benchmark">true = 连接后做一次链路吞吐基准（向第一个可写源路径写读 64MB 测试文件），建立「该环境正常速度」参照。</param>
    public async Task<PreflightReport> RunAsync(string host, string? user, string? password,
        IReadOnlyList<string> sourcePaths, string? targetRoot, long estimatedBytes = 0, bool benchmark = false)
    {
        var report = new PreflightReport { Host = host };
        _log.Information("Preflight 开始: host={Host}, sources={Count}", host, sourcePaths.Count);
        try
        {
            var diagnostics = CoreDiagnostics.Sink.Publisher;
            if (diagnostics.IsEnabledFor(PreflightEvents.PreflightStarted))
            {
                diagnostics.TryPublish(new DiagnosticEventDraft(
                    PreflightEvents.PreflightStarted,
                    CoreDiagnostics.ContextFor("PreflightChecker"),
                    new PflSummaryPayload(false, 0, 0, 0),
                    Level: DiagnosticLevel.Information,
                    Outcome: DiagnosticOutcome.Started));
            }
        }
        catch (Exception) { /* 观察失败绝不影响预检 */ }

        // 1. SMB 445 端口可达（比 ping 更真实：很多环境禁 ICMP）
        //    坑（公司环境实测）：电脑名经 DNS 可能解析到多个地址，其中一个是该机旧 IP（已失效）。
        //    原来只下一个 5 秒总预算，第一个失效地址就把预算耗光 → 明明 \\电脑名\共享 能正常读写，预检却报“无法连接 TCP 445”。
        //    现在逐个地址各给短超时，任一地址通即算通；用电脑名连不上不再提前收工，改由下面「SMB 会话 + 源路径可读」判定。
        var (smbOk, smbDetail, hostIsIp) = await ProbeSmb445Async(host);
        Add(report, "SMB 445 端口可达", smbOk, smbOk || !hostIsIp ? "Info" : "Error", smbDetail);
        if (!smbOk && hostIsIp) return Finish(report);

        // 2. IPC$ 凭据会话（失败不再立即阻断：对方可能是不导出 IPC$ 的精简/第三方 SMB 服务，
        //    或本机已有可用连接——最终以「源路径是否可读」为准。公司首测实测：\\IP\d 可读但 IPC$ 报 67）
        var sessionOk = false;
        Exception? sessionErr = null;
        var enumTried = false;
        try
        {
            using var s = NetworkShare.Connect(host, user, password, _log);
            sessionOk = true;
            // ★ 缺陷 O-B22c-1 ★ 1219/85 冲突时 Connect 会"复用本机现有连接、本次不使用输入的凭据"。
            // 过去这条事实只进日志，界面照旧显示「已连接」⇒ 用户以为凭据被验证过了。
            // 现在把它变成预检事实 + 一条可见检查项，并明确"凭据未被使用"。
            var reused = NetworkShare.LastConnectReusedExistingConnection;
            report.CredentialReused = reused;
            if (reused)
                Add(report, "IPC$ 凭据会话", false, "Warning",
                    $"本机已有到 {host} 的连接，本次**复用了那条现有连接，你输入的账号没有被使用**" +
                    "（Windows 错误 1219：同一台服务器同时只允许一套凭据）。" +
                    $"若要改用你输入的账号：先在命令提示符执行 net use \\\\{host}\\ /delete（或关掉所有访问该机的资源管理器窗口）再点「连接并列出共享」。" +
                    "在此之前，下面列出的共享/失败信息反映的是**现有连接那套身份**的可访问范围，不能证明你输入的账号可用。");
            else
                Add(report, "IPC$ 凭据会话", true, "Info", $"已连接（user={user ?? "<当前用户>"}）");

            // 3. 共享枚举（需要远程管理员权限，且依赖 IPC$）
            enumTried = true;
            try
            {
                report.Shares = NetworkShare.EnumShares(host);
                Add(report, "共享枚举", true, "Info", $"发现 {report.Shares.Count} 个磁盘共享");
            }
            catch (Exception ex)
            {
                Add(report, "共享枚举", false, "Warning",
                    ex.Message + "；尝试直接探测常见管理共享…");
            }
        }
        catch (Exception ex)
        {
            sessionErr = ex;
            _log.Information("IPC$ 会话未建立，转入直连探测模式: {Msg}", ex.Message);
        }

        // 2.5 IPC$ 失败但给了显式凭据且有可参考的源路径 → 直连源共享验证凭据并建立会话
        var directProbeOk = false;
        if (!sessionOk && !string.IsNullOrEmpty(user) && sourcePaths.Count > 0)
        {
            try
            {
                NetworkShare.ConnectForTransfer(host, user, password, sourcePaths, _log)?.Dispose();
                directProbeOk = true;
                Add(report, "直连共享凭据会话", true, "Info",
                    "IPC$ 不可用，已改为直连源共享建立/验证会话（凭据可用）");
            }
            catch (Exception ex2)
            {
                Add(report, "直连共享凭据会话", false, "Warning", "IPC$ 与直连共享均失败：" + ex2.Message);
            }
        }

        // 3b. 管理共享直接探测（不依赖 IPC$/枚举：本机现有连接可能已够用；枚举失败或没枚举过都探）
        if (report.Shares.Count == 0)
        {
            foreach (var name in new[] { "C$", "D$", "E$", "F$" })
            {
                var unc = "\\\\" + host + "\\" + name;
                try
                {
                    if (Directory.Exists(unc))
                        report.Shares.Add(new ShareInfo { Name = name, UncPath = unc, IsAdminShare = true, Remark = "（直接探测发现）" });
                }
                catch { /* 单个探测失败跳过 */ }
            }
            if (report.Shares.Count > 0)
                Add(report, "管理共享探测", true, "Info",
                    (enumTried ? "枚举被拦截，但" : "IPC$ 不可用，") + "直接探测到 " + report.Shares.Count + " 个可用共享：" + string.Join("、", report.Shares.Select(s => s.Name)));
        }

        // 4. 源路径可读（真正的放行闸门：迁移只需要源可读，IPC$/枚举只是发现手段）
        var allSourceOk = sourcePaths.Count > 0;
        var accessibleCount = 0;
        var unavailableCount = 0;
        foreach (var src in sourcePaths)
        {
            var ok = false; string detail;
            try
            {
                ok = Directory.Exists(src);
                detail = ok ? "路径存在且可访问" : "路径不存在或不可访问（检查共享名/权限）";
            }
            catch (Exception ex) { detail = ex.Message; }
            Add(report, $"源路径 {src}", ok, "Error", detail);
            if (ok) accessibleCount++; else unavailableCount++;
            allSourceOk &= ok;
        }

        // ★ 源路径可读是真正的放行闸门：把"几个可读/几个不可读"变成可离线复核的事实 ★
        try
        {
            var diagnostics = CoreDiagnostics.Sink.Publisher;
            if (diagnostics.IsEnabledFor(PreflightEvents.SourcePathProbeResult))
            {
                diagnostics.TryPublish(new DiagnosticEventDraft(
                    PreflightEvents.SourcePathProbeResult,
                    CoreDiagnostics.ContextFor("PreflightChecker"),
                    new PflSourceProbePayload(sourcePaths.Count, accessibleCount, unavailableCount),
                    Outcome: unavailableCount == 0 ? DiagnosticOutcome.Succeeded : DiagnosticOutcome.Failed));
            }
        }
        catch (Exception) { /* 观察失败绝不影响预检 */ }

        // 关键协议事实（公司域环境实测发现）：SMB 先 SESSION_SETUP（认证）后 TREE_CONNECT（找共享）。
        // 显式凭据 + 错误 67（找不到网络名）= 认证已通过、仅对方不导出 IPC$——凭据本身是有效的！
        // 若不区分这一点，域账号用户会被错误地挡在门外，被迫"先去资源管理器连一次"（真实踩坑）。
        //
        // ★ 缺陷 A-05（假成功）：错误 67 只是"半条证据"，必须另有**独立旁证**才允许据此放行 ★
        // 旧实现只要「给了显式凭据 + 错误 67」就判 ipcAuthProven ⇒ IPC$ 检查降级为 Warning+pass=true ⇒
        // overallPass=true ⇒ 界面直接显示「已连接 <主机>」，而共享列表为空、该主机其实根本不存在。
        // 实测触发：同一不可达主机的首次尝试拿到 64、重试（负缓存后）拿到 67 ⇒ 第一次失败、第二次"成功"。
        //
        // ★ 缺陷 O-B22c-1 修正（本批）★：旁证集合里**必须去掉「TCP 445 端口可达」**——
        //   端口可通只证明"有 SMB 服务在监听"，完全不证明"认证成功"。
        //   B22c 场景正是如此：源机 445 通、域控 10.77.0.10 不可达、凭据无法完成域校验，
        //   旧口径把 smbOk 当旁证 ⇒ 照样放行 ⇒ 界面宣「已连接」+「发现 0 个共享」（假连接）。
        //   真正能证明认证的只有三种结果：直连数据共享成功 / 共享枚举或管理共享探测成功 /
        //   源路径**真的读得到**（读得到即认证与授权都过了）。TCP 端口不在其列。
        var authProofs = new List<string>();
        if (directProbeOk) authProofs.Add("直连源共享成功");
        if (report.Shares.Count > 0) authProofs.Add("共享枚举或管理共享探测成功");
        if (allSourceOk) authProofs.Add("源路径可读");
        var ipcAuthProven = !sessionOk && !string.IsNullOrEmpty(user)
                            && ExtractWin32Error(sessionErr) == 67
                            && authProofs.Count > 0;
        if (!sessionOk && ExtractWin32Error(sessionErr) == 67 && authProofs.Count == 0)
            _log.Warning("IPC$ 错误 67 缺少认证旁证（TCP 端口可达不算证据；直连共享/枚举/源路径均未成功），不放行: host={Host}", host);

        // ★ 缺陷 O-B22c-1 ★ 用户输入的那套凭据是否已被独立证实（界面据此区分
        //   「对方确实没有共享」与「凭据根本没验证过」；复用现有连接不算证实）。
        report.CredentialVerified = (sessionOk && !report.CredentialReused)
                                    || directProbeOk || report.Shares.Count > 0 || allSourceOk;

        // IPC$ 失败的最终定性（此刻源路径与共享探测结果都已在手，判断才准确）
        if (!sessionOk)
        {
            if (ipcAuthProven)
                Add(report, "IPC$ 凭据会话", true, "Warning",
                    $"对方不导出 IPC$ 管理共享（错误 67），无法自动列出共享；但已有独立证据表明凭据与链路可用（{string.Join("、", authProofs)}）。请在②区「共享名」手动输入（如 d）再点「＋添加共享」，或在①直接粘贴完整共享路径。");
            else if (allSourceOk || report.Shares.Count > 0)
                Add(report, "IPC$ 凭据会话", false, "Warning",
                    sessionErr!.Message + "；但源路径/共享可直接访问，不影响迁移。");
            else
            {
                // ★ 缺陷 O-B22c-1 ★ 这里必须说清"凭据没有被证实"——不能让用户把
                //   「发现 0 个共享」读成「密码对了只是没共享」。文案给出可执行的下一步。
                var why = ExtractWin32Error(sessionErr) == 67
                    ? "对方不导出 IPC$ 管理共享，而且没有任何其他证据能证明认证成功" +
                      "（TCP 端口可通只说明有 SMB 服务在监听，不等于这套账号密码是对的）。"
                    : "IPC$ 会话没有建立起来。";
                Add(report, "IPC$ 凭据会话", false, "Error",
                    sessionErr!.Message.TrimEnd('。') + "。" +
                    "目前**无法确认这套账号密码是否可用**：" + why +
                    "如果该账号需要域控校验，域控不可达时同样会失败，请先确认域控在线；" +
                    "也可以先在②区「共享名」手动输入一个你确定存在的共享（如 d）再点「＋添加共享」——" +
                    "那一步会真的建立数据共享会话，成功即证明凭据可用；" +
                    "或直接在①粘贴完整共享路径（如 \\\\IP\\D 或 \\\\IP\\D$）绕过共享枚举。");
            }
        }

        // 4b. 可选链路吞吐基准：向第一个可访问源写读测试文件（IPC$ 不可用时只要有可访问共享也照常测）。
        //     用途：建立「这条链路的正常速度」基准——正式迁移明显慢于此值时，优先怀疑安全软件实时扫描。
        if (benchmark)
        {
            var benchRoot = sourcePaths.FirstOrDefault(p => { try { return Directory.Exists(p); } catch { return false; } })
                // 纯连接场景（还没有选定源路径）：用枚举到的第一个磁盘共享
                ?? report.Shares.FirstOrDefault(s => { try { return Directory.Exists(s.UncPath); } catch { return false; } })?.UncPath;
            if (benchRoot == null)
            {
                Add(report, "链路吞吐基准", false, "Warning", "没有可访问的源路径，跳过测速");
            }
            else
            {
                var tmp = Path.Combine(benchRoot, $".pcmig-bench-{Guid.NewGuid():N}.tmp");
                try
                {
                    // 迁移方向 = 从源「读」。先写一个 32MB 测试文件（写速度仅作参考），
                    // 然后循环读 ~2 秒测读吞吐——这才是迁移速度的真实基准。
                    const int fileMb = 32;
                    var buf = new byte[8 * 1024 * 1024];
                    new Random(42).NextBytes(buf);
                    var sw = Stopwatch.StartNew();
                    await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, buf.Length))
                    {
                        for (var i = 0; i < fileMb / 8; i++) await fs.WriteAsync(buf);
                    }
                    sw.Stop();
                    var writeMBs = fileMb / sw.Elapsed.TotalSeconds;
                    long read = 0;
                    sw.Restart();
                    while (sw.Elapsed.TotalSeconds < 2.0)
                    {
                        await using var fs = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.None,
                            buf.Length, FileOptions.SequentialScan);
                        int n;
                        while ((n = await fs.ReadAsync(buf)) > 0) read += n;
                    }
                    sw.Stop();
                    var readMBs = read / 1048576.0 / sw.Elapsed.TotalSeconds;
                    var totalMb = (int)(read / 1048576);
                    var tooSlow = readMBs < 10;
                    Add(report, "链路吞吐基准", !tooSlow, tooSlow ? "Warning" : "Info",
                        $"读（迁移方向）{readMBs:0.#} MB/s / 写 {writeMBs:0.#} MB/s（测试文件 {fileMb}MB，实读 {totalMb}MB，源 {benchRoot}）。" +
                        (tooSlow ? "读速低于 10 MB/s：链路本身很慢，或被安全软件拦截扫描，建议先排查再迁移。"
                                 : "正式迁移速度明显低于此读速时，优先怀疑安全软件实时扫描。"));
                    _log.Information("吞吐基准: 读 {R:0.#} MB/s, 写 {W:0.#} MB/s @ {Root}", readMBs, writeMBs, benchRoot);

                    // ★ 链路基准只在用户显式开启时存在；诊断**不会**主动跑它 ★
                    try
                    {
                        var diagnostics = CoreDiagnostics.Sink.Publisher;
                        if (diagnostics.IsEnabledFor(PreflightEvents.BenchmarkCompleted))
                        {
                            diagnostics.TryPublish(new DiagnosticEventDraft(
                                PreflightEvents.BenchmarkCompleted,
                                CoreDiagnostics.ContextFor("PreflightChecker"),
                                new PflBenchmarkPayload(readMBs, writeMBs, tooSlow),
                                Level: tooSlow ? DiagnosticLevel.Warning : DiagnosticLevel.Information,
                                Outcome: DiagnosticOutcome.Succeeded));
                        }
                    }
                    catch (Exception) { /* 观察失败绝不影响预检 */ }
                }
                catch (Exception ex)
                {
                    Add(report, "链路吞吐基准", false, "Warning", $"测速失败（共享可能只读）：{ex.Message}");
                }
                finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 清理失败不阻断 */ } }
            }
        }

        // 5. 目标盘：文件系统 + 空间
        if (!string.IsNullOrWhiteSpace(targetRoot))
        {
            try
            {
                var fullPath = Path.GetFullPath(targetRoot);
                var driveRoot = Path.GetPathRoot(fullPath)!;
                var drive = new DriveInfo(driveRoot);

                // 5a. 文件系统硬性约束：FAT32 单文件 ≤4GB、单目录 65534 个目录项（长文件名占多项，
                //     实测约 21844 个文件即触顶，报"错误 82 无法创建目录或文件"）。NTFS/ReFS/exFAT 无此限制。
                var fs = drive.DriveFormat; // NTFS / FAT32 / exFAT / ReFS ...
                if (fs.Equals("FAT32", StringComparison.OrdinalIgnoreCase))
                    Add(report, $"目标盘文件系统 {fs}", false, "Error",
                        $"{driveRoot} 是 FAT32：单文件不能超过 4GB，且单个文件夹最多容纳约 2 万个文件（目录项上限）。" +
                        "请把目标改到 NTFS/exFAT 盘，或先将该盘格式化为 NTFS/exFAT（格式化会清空该盘数据）。");
                else if (fs.Equals("exFAT", StringComparison.OrdinalIgnoreCase) || fs.Equals("FAT", StringComparison.OrdinalIgnoreCase) || fs.Equals("FAT16", StringComparison.OrdinalIgnoreCase))
                    Add(report, $"目标盘文件系统 {fs}", true, "Warning",
                        $"{driveRoot} 是 {fs}：不支持 NTFS 权限/硬链接/加密属性，迁移将仅保留文件内容与时间戳。");
                else
                    Add(report, $"目标盘文件系统", true, "Info", $"{driveRoot} 文件系统 {fs}");

                // 5b. 空间
                if (estimatedBytes > 0)
                {
                    var need = (long)(estimatedBytes * 1.05); // 5% 余量
                    var ok = drive.AvailableFreeSpace >= need;
                    Add(report, $"目标盘 {driveRoot} 可用空间", ok, ok ? "Info" : "Error",
                        $"可用 {Util.Format.Bytes(drive.AvailableFreeSpace)} / 需要 {Util.Format.Bytes(need)}（含5%余量）");
                }
                else
                {
                    Add(report, $"目标盘 {driveRoot}", true, "Info", $"可用 {Util.Format.Bytes(drive.AvailableFreeSpace)}");
                }

                // ★ 目标卷事实（卷类型 + 空间，都是数字，不含路径明文）★
                try
                {
                    var diagnostics = CoreDiagnostics.Sink.Publisher;
                    if (diagnostics.IsEnabledFor(PreflightEvents.TargetVolumeObserved))
                    {
                        var need = estimatedBytes > 0 ? (long)(estimatedBytes * 1.05) : 0;
                        diagnostics.TryPublish(new DiagnosticEventDraft(
                            PreflightEvents.TargetVolumeObserved,
                            CoreDiagnostics.ContextFor("PreflightChecker"),
                            new PflTargetVolumePayload(
                                drive.DriveType.ToString(),
                                drive.AvailableFreeSpace / (1024 * 1024),
                                drive.TotalSize / (1024 * 1024),
                                need == 0 || drive.AvailableFreeSpace >= need),
                            Outcome: DiagnosticOutcome.Succeeded));
                    }
                }
                catch (Exception) { /* 观察失败绝不影响预检 */ }
            }
            catch (Exception ex)
            {
                // UNC（\\服务器\共享）目标不在产品契约内：本产品是"在新电脑运行、写入本机盘"的直拉模式。
                // 此前这里直接透传 DriveInfo 的异常文案（"Drive name must be a root directory…"），
                // 用户看不懂、也看不出"UNC 不被支持"。改为显式说明，让 CLI 与 GUI 拿到同一条清晰消息。
                var raw = (targetRoot ?? string.Empty).Trim();
                var isUnc = IsUncTarget(raw);
                if (isUnc)
                {
                    Add(report, "目标盘检查", false, "Error",
                        $"不支持把网络路径当作目标位置：{raw}。" +
                        "本工具在新电脑上运行、把数据写到本机磁盘，因此目标必须是本机盘符路径（如 D:\\迁移目标）。" +
                        "若目标是另一台机器的共享，请在那台机器上就地运行本工具，或先把共享映射为本地盘再试。");
                }
                else
                {
                    // ★ 缺陷 A-06：这里曾直接透传 .NET 原文（实测 `Could not find the drive 'Z:\'.
                    // The drive might not be ready or might not be mapped.`）——非中文、不给下一步，
                    // 属 §八「UI 也是一等被测对象」里的不合格提示。现在先独立判定"该盘符在本机是否存在"，
                    // 再给中文可执行文案，原始技术细节降级到括号内保留（排障仍可用）。
                    var exists = false;
                    try
                    {
                        var root = Path.GetPathRoot(raw);
                        exists = !string.IsNullOrEmpty(root) && Directory.Exists(root);
                    }
                    catch { /* 探测失败按"不存在"处理 */ }

                    var reason = exists
                        ? "目标路径不可用（磁盘存在，但该路径不能作为迁移目标）"
                        : "本机找不到这个盘符或路径";
                    Add(report, "目标盘检查", false, "Error",
                        $"{reason}：{raw}。" +
                        "请点「浏览」选择本机上真实存在的磁盘路径（例如 D:\\迁移目标），或先在「此电脑」里确认该盘已插入/已联机。" +
                        $"（技术细节：{ex.Message}）");
                }
            }
        }

        // 6. 本机 robocopy 能力（执行机轴：能力由本机 OS 决定）
        try
        {
            var rb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "robocopy.exe");
            var ver = FileVersionInfo.GetVersionInfo(rb);
            var osOk = Environment.OSVersion.Version >= new Version(6, 2); // Win8+ 才有 /MT
            Add(report, "Robocopy 可用性", File.Exists(rb) && osOk, osOk ? "Info" : "Warning",
                $"robocopy {ver.FileVersion}，OS {Environment.OSVersion.Version}，/MT 多线程：{(osOk ? "支持" : "不支持（Win7执行机）")}");
        }
        catch (Exception ex) { Add(report, "Robocopy 可用性", false, "Error", ex.Message); }

        return Finish(report);
    }

    /// <summary>从 IOException/Win32Exception 中提取 Win32 错误码（NetworkShare 消息格式为 "（错误 N）"）。</summary>
    /// <summary>
    /// 判断目标位置是否为 UNC（网络）路径。
    /// 产品契约：本工具在新电脑上运行、把数据写到本机磁盘，目标必须是本机盘符路径；
    /// UNC 目标不在契约内。抽成纯函数是为了让该契约可被单元测试直接覆盖（不依赖网络）。
    /// </summary>
    internal static bool IsUncTarget(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return false;
        return rawPath.Trim().StartsWith(@"\\", StringComparison.Ordinal);
    }


    internal static int ExtractWin32Error(Exception? ex)
    {
        if (ex is System.ComponentModel.Win32Exception w) return w.NativeErrorCode;
        if (ex == null) return -1;
        var m = System.Text.RegularExpressions.Regex.Match(ex.Message, @"错误\s*(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : -1;
    }

    private static void Add(PreflightReport r, string name, bool pass, string severity, string detail)
    {
        r.Checks.Add(new PreflightCheck { Name = name, Pass = pass, Severity = severity, Detail = detail });
        PublishCheck(name, severity, pass);
    }

    /// <summary>
    /// 检查项名称 → **稳定 checkCode**（规则只依赖 checkCode，绝不依赖中文检查名）。
    /// 集中映射而不是改 22 个 Add 调用点：调用点继续只提供展示名，映射由本表负责，
    /// 并有单元测试保证"已知检查名不得落到 uncoded"。
    /// </summary>
    internal static string CheckCodeFor(string name) => name switch
    {
        var n when n.StartsWith("SMB 445", StringComparison.Ordinal) => "Smb445",
        var n when n.StartsWith("IPC$", StringComparison.Ordinal) => "IpcSession",
        var n when n.StartsWith("直连共享凭据", StringComparison.Ordinal) => "DirectShareSession",
        var n when n.StartsWith("共享枚举", StringComparison.Ordinal) => "ShareEnumeration",
        var n when n.StartsWith("管理共享探测", StringComparison.Ordinal) => "AdminShareProbe",
        var n when n.StartsWith("源路径", StringComparison.Ordinal) => "SourcePath",
        var n when n.StartsWith("链路吞吐基准", StringComparison.Ordinal) => "Benchmark",
        var n when n.StartsWith("目标盘文件系统", StringComparison.Ordinal) => "TargetFilesystem",
        var n when n.Contains("可用空间", StringComparison.Ordinal) => "TargetSpace",
        var n when n.StartsWith("目标盘", StringComparison.Ordinal) => "TargetVolume",
        var n when n.StartsWith("Robocopy", StringComparison.Ordinal) => "Robocopy",
        _ => "uncoded",
    };

    private static void PublishCheck(string name, string severity, bool pass)
    {
        try
        {
            var diagnostics = CoreDiagnostics.Sink.Publisher;
            if (!diagnostics.IsEnabledFor(PreflightEvents.CheckCompleted)) return;
            diagnostics.TryPublish(new DiagnosticEventDraft(
                PreflightEvents.CheckCompleted,
                CoreDiagnostics.ContextFor("PreflightChecker"),
                new PflCheckPayload(CheckCodeFor(name), severity, pass),
                Level: severity == "Error" ? DiagnosticLevel.Error : severity == "Warning" ? DiagnosticLevel.Warning : DiagnosticLevel.Information,
                Outcome: pass ? DiagnosticOutcome.Succeeded : DiagnosticOutcome.Failed));
        }
        catch (Exception) { /* 观察失败绝不影响预检 */ }
    }

    private PreflightReport Finish(PreflightReport r)
    {
        r.OverallPass = r.Checks.All(c => c.Pass || c.Severity != "Error");
        _log.Information("Preflight 结束: {Result}（{Checks} 项检查）", r.OverallPass ? "通过" : "存在阻断项", r.Checks.Count);

        // ★ 结论级观察（预检是"能不能迁"的唯一闸门，其结论必须可离线复核）★
        try
        {
            var diagnostics = CoreDiagnostics.Sink.Publisher;
            if (diagnostics.IsEnabledFor(PreflightEvents.PreflightCompleted))
            {
                diagnostics.TryPublish(new DiagnosticEventDraft(
                    PreflightEvents.PreflightCompleted,
                    CoreDiagnostics.ContextFor("PreflightChecker"),
                    new PflSummaryPayload(
                        r.OverallPass,
                        r.Checks.Count,
                        r.Checks.Count(c => c.Severity == "Error"),
                        r.Checks.Count(c => c.Severity == "Warning")),
                    Outcome: r.OverallPass ? DiagnosticOutcome.Succeeded : DiagnosticOutcome.Failed));
            }
        }
        catch (Exception) { /* 观察失败绝不影响预检 */ }

        return r;
    }
}
