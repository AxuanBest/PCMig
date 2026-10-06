using System.ComponentModel;
using System.Runtime.InteropServices;
using PCMig.Core.Diagnostics;
using PCMig.Core.Models;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;

namespace PCMig.Core.Native;

/// <summary>
/// SMB 网络共享访问：
///  - Connect：用指定凭据建立到 \\host\IPC$ 的会话（WNetAddConnection2）
///  - EnumShares：远程枚举共享（NetShareEnum，需要对方机器上的管理员权限）
/// </summary>
public static class NetworkShare
{
    private const int RESOURCETYPE_DISK = 0x00000001;
    private const uint STYPE_DISKTREE = 0x00000000;
    private const uint STYPE_SPECIAL = 0x80000000;
    private const int NERR_Success = 0;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_LOGON_FAILURE = 1326;
    private const int ERROR_BAD_NETPATH = 53;
    private const int ERROR_EXTENDED_ERROR = 1208;
    private const int ERROR_SESSION_CREDENTIAL_CONFLICT = 1219;
    private const int ERROR_ALREADY_ASSIGNED = 85;
    private const int ERROR_BAD_NET_NAME = 67;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class NETRESOURCE
    {
        public int dwScope = 0;
        public int dwType = RESOURCETYPE_DISK;
        public int dwDisplayType = 0;
        public int dwUsage = 0;
        public string? lpLocalName = null;
        public string lpRemoteName = "";
        public string? lpComment = null;
        public string? lpProvider = null;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetAddConnection2(NETRESOURCE netResource, string? password, string? username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetOpenEnum(int dwScope, int dwType, int dwUsage, IntPtr lpNetResource, out IntPtr lphEnum);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetEnumResource(IntPtr hEnum, ref int lpcCount, IntPtr lpBuffer, ref int lpBufferSize);

    [DllImport("mpr.dll")]
    private static extern int WNetCloseEnum(IntPtr hEnum);

    /// <summary>列出本机当前到指定服务器的全部现有连接（资源管理器/映射盘/之前连过的）。</summary>
    private static List<string> ExistingConnectionsTo(string host)
    {
        var list = new List<string>();
        var prefix = $"\\\\{host}\\";
        const int RESOURCE_CONNECTED = 1;
        if (WNetOpenEnum(RESOURCE_CONNECTED, RESOURCETYPE_DISK, 0, IntPtr.Zero, out var hEnum) != NERR_Success)
            return list;
        try
        {
            var bufSize = 16 * 1024;
            var buf = Marshal.AllocHGlobal(bufSize);
            try
            {
                while (true)
                {
                    var count = -1;
                    var size = bufSize;
                    if (WNetEnumResource(hEnum, ref count, buf, ref size) != NERR_Success || count <= 0) break;
                    var itemSize = Marshal.SizeOf<NETRESOURCE>();
                    for (var i = 0; i < count; i++)
                    {
                        var nr = Marshal.PtrToStructure<NETRESOURCE>(buf + i * itemSize);
                        if (nr?.lpRemoteName != null &&
                            nr.lpRemoteName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            list.Add(nr.lpRemoteName);
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { _ = WNetCloseEnum(hEnum); }
        return list;
    }

    /// <summary>
    /// 断开到指定服务器的【全部】现有会话连接。
    /// 1219 冲突的根源往往不只是 IPC$——资源管理器可能还占着 \\host\Users 之类的连接，
    /// 只删 IPC$ 解决不了，必须把本会话到该机的所有连接清掉。
    /// </summary>
    private static void CancelAllConnectionsTo(string host)
    {
        foreach (var remote in ExistingConnectionsTo(host))
            _ = WNetCancelConnection2(remote, 0, true);
    }

    /// <summary>本机当前是否已有到指定服务器的连接（可复用时不必再握手）。</summary>
    public static bool HasExistingConnection(string host) => ExistingConnectionsTo(host).Count > 0;

    // ---- 缺陷 O-B22c-1（产品诚实性：1219 复用必须可被上层看见）----
    // 1219/85 冲突时产品会"复用本机现有连接、本次不使用用户输入的凭据"，
    // 但这条事实过去只写进日志，界面照样显示「已连接 <主机>」——
    // 用户以为密码输对了，实际那套凭据根本没被使用（B22c run3 实证）。
    // 这里把该事实暴露给调用方（预检 → 界面），让上层能如实告知。
    //
    // [ThreadStatic]：Connect 是同步 P/Invoke 调用，一次预检在同一条线程上完成；
    // 用线程局部状态避免并发用例互相污染（不用静态共享字段）。
    [ThreadStatic] private static bool t_lastConnectReusedInput;

    /// <summary>
    /// 最近一次 <see cref="Connect"/>（同线程）是否走了"复用本机现有连接、未使用输入凭据"分支。
    /// true = 当前会话不是用用户输入的账号建立的 ⇒ 上层**不得**据此宣称"凭据已通过验证"。
    /// </summary>
    public static bool LastConnectReusedExistingConnection => t_lastConnectReusedInput;

    // ---- 失败提示去重（v0.3.8，缺陷 5）----
    // 生产事故日志里 "连接 \SRC-PC-2IPC$ 失败: Win32Error=67" 在几分钟内反复出现
    // （预检一次 + 传输一次 + 直连回退各打一遍），用户以为"出了很多不同的错"。
    // 同一主机 + 同一错误码只完整打印一次，重复降到 Debug，并在第 2/10/100… 次明确写"已重复 N 次"。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> s_connNoticeCounts = new();

    internal static void LogConnectFailureOnce(Serilog.ILogger? log, string what, string host, int code, string hint)
    {
        if (log == null) return;
        var key = what + "|" + host + "|" + code;
        var n = s_connNoticeCounts.AddOrUpdate(key, 1, (_, c) => c + 1);
        if (n == 1)
        {
            log.Warning("{What} 失败: Win32Error={Code} {Hint}", what, code, hint);
            return;
        }
        if (n is 2 or 10 or 100 or 1000)
        {
            log.Warning("{What} 再次失败（同一主机同一错误码已出现 {Count} 次，相同的提示不再重复刷屏）: Win32Error={Code}",
                what, n, code);
            return;
        }
        log.Debug("{What} 失败（第 {Count} 次，已折叠）: Win32Error={Code}", what, n, code);
    }

    /// <summary>从 UNC 路径提取共享根：\\host\share\a\b → \\host\share；不是 UNC 返回 null。</summary>
    public static string? ShareRootOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith(@"\\")) return null;
        var parts = path.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHARE_INFO_1
    {
        public string shi1_netname;
        public uint shi1_type;
        public string shi1_remark;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHARE_INFO_2
    {
        public string shi2_netname;
        public uint shi2_type;
        public string shi2_remark;
        public uint shi2_permissions;
        public uint shi2_max_uses;
        public uint shi2_current_uses;
        public string shi2_path;
        public string shi2_passwd;
    }

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareEnum(string serverName, int level, out IntPtr bufPtr,
        int prefMaxLen, out int entriesRead, out int totalEntries, ref int resumeHandle);

    [DllImport("Netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    /// <summary>
    /// 建立到 \\host\IPC$ 的会话。
    /// user 为空 = 用当前 Windows 身份直连（与资源管理器打开 \\host 行为一致）；
    /// 已有会话时直接复用，不会强行断开。失败抛出带中文说明的异常。
    /// </summary>
    public static IDisposable Connect(string host, string? user, string? password, Serilog.ILogger? log = null)
    {
        var ipc = $@"\\{host}\IPC$";
        var explicitCreds = !string.IsNullOrEmpty(user);
        var hostToken = CoreDiagnostics.Sink.Token(host);
        t_lastConnectReusedInput = false; // O-B22c-1：本次调用尚未复用（每次 Connect 独立判定）

        // ★ D6.1 §16 观察点 ★ 只记"用哪套凭据语义"，**绝不**记用户名/口令（连长度都不记）。
        PublishSession(NetEvents.SmbSessionConnectStarted, ipc, hostToken, explicitCreds,
            reused: false, win32: 0, reason: "connect-requested", level: DiagnosticLevel.Information);

        // 只有显式凭据才清理旧会话（避免 1219 冲突）；空凭据时保留现有会话，
        // 让用户在资源管理器里已打通的连接可以直接复用（等同 \ip\盘符 的行为）。
        if (explicitCreds)
        {
            try { _ = WNetCancelConnection2(ipc, 0, true); } catch { /* ignore */ }
        }

        var nr = new NETRESOURCE { lpRemoteName = ipc };
        var err = WNetAddConnection2(nr, string.IsNullOrEmpty(password) ? null : password,
            explicitCreds ? user : null, 0);

        if (explicitCreds && err is ERROR_ALREADY_ASSIGNED or ERROR_SESSION_CREDENTIAL_CONFLICT)
        {
            // 1219/85 冲突：本机已有到该服务器的其他连接（资源管理器窗口/映射盘/之前用别的账号连过）。
            // 公司首测教训：绝不能先杀现有连接再重试——目标可能没有 IPC$，重试必败，
            // 结果是把用户在资源管理器里打通的可用会话白白毁掉。有现存连接就直接复用。
            if (HasExistingConnection(host))
            {
                log?.Warning("凭据与现有连接冲突（{Code}）——复用现有连接（本次未使用输入的凭据）。如需强制改用输入的凭据：先 net use \\\\{Host}\\ /delete 再重试", err, host);
                PublishSession(NetEvents.SmbSessionReused, ipc, hostToken, explicitCreds,
                    reused: true, win32: err, reason: "credential-conflict-reuse", level: DiagnosticLevel.Information);
                t_lastConnectReusedInput = true; // O-B22c-1：显式凭据被忽略，上层必须如实告知
                return new ShareSession(null);
            }
            // 无现存连接却报冲突（残留句柄）→ 清理后重试一次
            log?.Information("报冲突（{Code}）但无现存连接，清理残留句柄后重试", err);
            CancelAllConnectionsTo(host);
            err = WNetAddConnection2(nr, string.IsNullOrEmpty(password) ? null : password, user, 0);
        }

        if (!explicitCreds && err is ERROR_ALREADY_ASSIGNED or ERROR_SESSION_CREDENTIAL_CONFLICT)
        {
            // 空凭据且已有会话：复用（当前身份之前已在别处连通过这台机器）
            log?.Information("复用现有会话 {Share}", ipc);
            PublishSession(NetEvents.SmbSessionReused, ipc, hostToken, explicitCreds,
                reused: true, win32: err, reason: "existing-session", level: DiagnosticLevel.Information);
            return new ShareSession(ipc);
        }

        // 对方 SMB 不导出 IPC$（错误 67；精简/第三方 SMB 服务首测实测出现）：
        // 只要本机已有到该机的连接，数据共享照样可能可用——复用现有连接，不算失败。
        if (!explicitCreds && err is ERROR_BAD_NET_NAME or ERROR_BAD_NETPATH && HasExistingConnection(host))
        {
            log?.Information("对方无 IPC$（错误 {Code}），复用现有到 {Host} 的连接", err, host);
            return new ShareSession(null); // 空会话：Dispose 不动现有连接
        }

        if (err != NERR_Success)
        {
            var hint = err switch
            {
                ERROR_ACCESS_DENIED when !explicitCreds =>
                    "以当前 Windows 身份连接被拒绝。请在用户名/密码框输入旧电脑上的管理员账号" +
                    "（Win7 目标注意：需用内置 Administrator 或域管理员）",
                ERROR_LOGON_FAILURE when !explicitCreds =>
                    "对方不认识当前 Windows 账号。请输入旧电脑上的用户名和密码后重试",
                ERROR_ACCESS_DENIED => "访问被拒绝（对 Win7 目标：非内置 Administrator 的本地管理员账号会被 UAC 远程令牌过滤拦截，请使用域管理员或内置 Administrator）",
                ERROR_LOGON_FAILURE when explicitCreds && string.IsNullOrEmpty(password) =>
                    "登录失败。可能原因：① 该账号在旧电脑上不存在（用户名要填旧电脑上的账号）；② 该账号密码为空——" +
                    "Windows 默认安全策略禁止空密码账号进行网络登录（含 SMB），请给该账号设置密码；③ 该账号有密码但没填",
                ERROR_LOGON_FAILURE => "用户名或密码错误（用户名需是旧电脑上的账号，格式：电脑名\\用户名 或 域\\用户名）",
                ERROR_SESSION_CREDENTIAL_CONFLICT =>
                    "与现有连接冲突（错误 1219）：这台电脑正用另一组凭据连着旧电脑（最常见：资源管理器开着 \\\\旧电脑 的窗口，且已自动重连）。" +
                    "PCMig 已自动清理冲突连接；若仍看到此错误，请关闭所有访问旧电脑的资源管理器窗口和映射驱动器后重试",
                ERROR_BAD_NETPATH => "网络路径不可达（检查 IP/电脑名、目标是否开机、防火墙是否放行文件和打印机共享 SMB-In）",
                // v0.3.8（缺陷 5）：错误 67 说清"接下来会怎么做、凭据语义有没有变"，
                // 生产事故日志里只有 "Win32Error=67 找不到网络名"，看不出这是"不影响迁移、将改直连共享"。
                ERROR_BAD_NET_NAME => "找不到网络名（对方不导出 IPC$ 管理共享；精简/第三方 SMB 服务常见，NAS 也常见）。" +
                    $"这不等于迁移会失败：PCMig 接着会改为【直连你要迁移的数据共享】（例如 \\\\{host}\\d$）建立会话，" +
                    "仍然使用你输入的这一组账号密码；区别只是不再先建立 IPC$ 会话——" +
                    "因此“实际用哪套凭据”以数据共享那次连接为准（若本机已有到该机的其他连接且凭据不同，会出现 1219 冲突）。" +
                    "下一步：若随后仍连不上，说明对方确实没有可用的数据共享或凭据不对——请在②区「共享名」手动输入对方开放的共享（例如 d）再点「＋添加共享」，" +
                    "或核对①里的用户名/密码是不是旧电脑上的那组账号。",
                ERROR_EXTENDED_ERROR => "网络扩展错误（可能 SMB 协议协商失败）",
                _ => new Win32Exception(err).Message
            };
            LogConnectFailureOnce(log, $"连接 {ipc}", host, err, hint);
            PublishSession(NetEvents.SmbConnectFailed, ipc, hostToken, explicitCreds,
                reused: false, win32: err, reason: "wnet-add-connection-failed", level: DiagnosticLevel.Error);
            throw new IOException($"无法连接 {ipc}（错误 {err}）：{hint}");
        }

        log?.Information("已建立会话 {Share} (user={User})", ipc, explicitCreds ? user : "<当前 Windows 身份>");
        PublishSession(NetEvents.SmbSessionConnected, ipc, hostToken, explicitCreds,
            reused: false, win32: 0, reason: "session-established", level: DiagnosticLevel.Information);
        return new ShareSession(ipc);
    }

    /// <summary>
    /// D6.1 §16：会话观察（纯观察）。**不传用户名/口令**，只传凭据语义与原生错误码；
    /// host 只以令牌出现在 envelope 的 PathRef 里（Personal 级）。
    /// </summary>
    private static void PublishSession(
        PCMig.Diagnostics.Abstractions.EventDescriptor descriptor, string remotePath, string hostToken,
        bool explicitCreds, bool reused, int win32, string reason, DiagnosticLevel level)
        => CoreDiagnostics.PublishCore(
            descriptor,
            new NetSmbSessionPayload("WNetAddConnection2", reused, 0, win32 == 0)
            {
                Win32Error = win32,
                UsedExplicitCreds = explicitCreds,
                ReasonCode = reason,
            },
            CoreDiagnostics.ContextFor("NetworkShare"),
            level,
            win32 == 0 ? DiagnosticOutcome.Succeeded : DiagnosticOutcome.Failed,
            "NetworkShare",
            path: CoreDiagnostics.Sink.PathRef(remotePath, PathRole.Source, hostToken));

    /// <summary>
    /// 为传输建立会话：优先走 IPC$；若对方不导出 IPC$（错误 67/53）但给了显式凭据，
    /// 退而直接连接任务的源共享本身（很多精简 SMB 服务只导出数据共享）；
    /// 仍不行但所有源路径此刻都可访问时，返回 null 表示"靠本机现有连接即可"，由调用方继续。
    /// 彻底无法建立才抛异常。
    /// </summary>
    public static IDisposable? ConnectForTransfer(string host, string? user, string? password,
        IReadOnlyList<string> sources, Serilog.ILogger? log = null)
    {
        try
        {
            return Connect(host, user, password, log);
        }
        catch (Exception ipcEx)
        {
            // 只完整说明一次"接下来怎么办"（v0.3.8）：IPC$ 只是管理共享，连不上不影响数据共享直连，
            // 但会改变"凭据用在哪个资源上"的语义——必须说清楚，且不要每次刷屏。
            LogConnectFailureOnce(log, $"IPC$ 会话（{host}）", host, 0,
                ipcEx.Message + "　处理：无需操作，PCMig 会自动改为直连源数据共享；只影响凭据建立方式，不影响迁移能力。");
            var directFails = new List<string>();
            if (!string.IsNullOrEmpty(user))
            {
                foreach (var src in sources)
                {
                    var root = ShareRootOf(src);
                    if (root == null) continue;
                    try
                    {
                        var nr = new NETRESOURCE { lpRemoteName = root };
                        var err = WNetAddConnection2(nr, string.IsNullOrEmpty(password) ? null : password, user, 0);
                        if (err is ERROR_SESSION_CREDENTIAL_CONFLICT or ERROR_ALREADY_ASSIGNED)
                        {
                            // 有现存连接：源路径可访问就靠它跑（不打断资源管理器里正在用的会话）；
                            // 源也不可达（现存连接凭据很可能已失效）才清理重建——这是最后手段。
                            var ambientOk = sources.Count > 0 && sources.All(s => { try { return Directory.Exists(s); } catch { return false; } });
                            if (ambientOk)
                            {
                                log?.Warning("直连冲突（{Code}）但源路径均可访问，复用现有连接继续", err);
                                return null;
                            }
                            log?.Warning("直连冲突且源不可达，清理到 {Host} 的全部连接后用输入凭据重建", host);
                            CancelAllConnectionsTo(host);
                            err = WNetAddConnection2(nr, string.IsNullOrEmpty(password) ? null : password, user, 0);
                        }
                        if (err == NERR_Success)
                        {
                            log?.Information("已改为直连共享 {Share} 建立会话（user={User}）", root, user);
                            return new ShareSession(root);
                        }
                        LogConnectFailureOnce(log, $"直连共享 {root}", host, err, "该共享直连失败（共享可能已关闭/改名，或凭据不对）");
                        directFails.Add($"{root}（错误 {err}）");
                    }
                    catch (Exception ex) { log?.Warning(ex, "直连共享 {Share} 异常", root); directFails.Add($"{root}（{ex.Message}）"); }
                }
            }
            // 最后的机会：所有源路径此刻已可访问（现有连接/凭据缓存）→ 靠现有连接跑
            if (sources.Count > 0 && sources.All(s => { try { return Directory.Exists(s); } catch { return false; } }))
            {
                log?.Warning("源路径当前均可访问，依赖本机现有连接继续传输");
                return null;
            }
            // 直连也失败时，把"哪个共享、什么错误"放前面（IPC$ 的错只是陪衬）——
            // 实测案例：对方域策略刷新关掉了 D$，恢复时报的却是 IPC$ 的 67，误导排查方向。
            if (directFails.Count > 0)
                throw new IOException("源共享连接失败：" + string.Join("；", directFails) +
                    "。对方共享可能已被关闭/改名或凭据失效——到旧电脑上运行 net share 确认共享还在（若共享名变了，可联系我们改任务源，或重新建任务选新共享），然后再点「恢复任务」。底层细节：" + ipcEx.Message);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ipcEx).Throw();
            throw; // 不可达：让编译器满意
        }
    }

    /// <summary>
    /// 枚举远程主机的磁盘共享（需要远程管理员权限）。
    /// 优先用 SHARE_INFO_2 拿到物理路径，把"指向同一磁盘"的共享合并成一个条目
    /// （例如管理员共享 E$ 与手工共享 E 实为同一盘，避免重复迁移）。
    /// 非管理员账号拿不到 level 2 时回退 level 1（无物理路径、不合并）。
    /// </summary>
    public static List<ShareInfo> EnumShares(string host)
    {
        var result = TryEnumLevel2(host) ?? EnumLevel1(host);

        // ---- 同物理路径合并：E 与 E$ 同盘 → 只保留一个（优先非管理共享，兼容性更好）----
        var merged = result
            .GroupBy(s => string.IsNullOrWhiteSpace(s.LocalPath)
                ? "?" + s.Name  // 无物理路径时按名字独立成组，不盲合
                : s.LocalPath.TrimEnd('\\').ToUpperInvariant())
            .Select(g =>
            {
                var pick = g.OrderBy(s => s.IsAdminShare)
                            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                            .First();
                pick.Aliases = g.Where(s => !ReferenceEquals(s, pick))
                                .Select(s => s.Name).ToList();
                return pick;
            });

        var final = merged.OrderBy(s => s.IsAdminShare).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

        // ★ D6.1 §16 观察点 ★ 枚举结果（计数 + 成败），**不写共享清单内容**。
        CoreDiagnostics.PublishCore(
            final.Count > 0 ? NetEvents.ShareResolved : NetEvents.ShareEnumerationFailed,
            new NetShareDiscoveryPayload(final.Count > 0, final.Count, final.Count(s => s.IsAdminShare) > 0)
            {
                AdminShareCount = final.Count(s => s.IsAdminShare),
                Level = final.Count > 0 ? "level2-or-level1" : "empty-or-denied",
            },
            CoreDiagnostics.ContextFor("NetworkShare"),
            final.Count > 0 ? DiagnosticLevel.Information : DiagnosticLevel.Warning,
            final.Count > 0 ? DiagnosticOutcome.Succeeded : DiagnosticOutcome.Unknown,
            "NetworkShare",
            path: CoreDiagnostics.Sink.PathRef($@"\\{host}", PathRole.Source, CoreDiagnostics.Sink.Token(host)));

        return final;
    }

    private static List<ShareInfo>? TryEnumLevel2(string host)
    {
        var resume = 0;
        var err = NetShareEnum(host, 2, out var buf, -1, out var read, out var total, ref resume);
        if (err != NERR_Success) return null; // 多半是权限不够（非管理员），静默回退 level 1

        var result = new List<ShareInfo>();
        try
        {
            var itemSize = Marshal.SizeOf<SHARE_INFO_2>();
            for (var i = 0; i < read; i++)
            {
                var item = Marshal.PtrToStructure<SHARE_INFO_2>(buf + i * itemSize);
                var type = item.shi2_type & 0x7FFFFFFF;
                if (type != STYPE_DISKTREE) continue;
                result.Add(new ShareInfo
                {
                    Name = item.shi2_netname,
                    Remark = item.shi2_remark ?? "",
                    IsAdminShare = item.shi2_netname.EndsWith('$'),
                    UncPath = $@"\\{host}\{item.shi2_netname}",
                    LocalPath = item.shi2_path ?? ""
                });
            }
        }
        finally { NetApiBufferFree(buf); }
        return result;
    }

    private static List<ShareInfo> EnumLevel1(string host)
    {
        var result = new List<ShareInfo>();
        var resume = 0;
        var err = NetShareEnum(host, 1, out var buf, -1, out var read, out var total, ref resume);
        if (err != NERR_Success)
            throw new IOException($"枚举共享失败（错误 {err}）：{new Win32Exception(err).Message}。需要目标机管理员权限。若要跳过枚举，可直接手工输入共享路径，如 \\\\{host}\\D$");

        try
        {
            var itemSize = Marshal.SizeOf<SHARE_INFO_1>();
            for (var i = 0; i < read; i++)
            {
                var item = Marshal.PtrToStructure<SHARE_INFO_1>(buf + i * itemSize);
                var type = item.shi1_type & 0x7FFFFFFF; // 低 28 位是类型
                if (type != STYPE_DISKTREE) continue;  // 只要磁盘共享（排除打印机/IPC）
                var isAdmin = item.shi1_netname.EndsWith('$');
                result.Add(new ShareInfo
                {
                    Name = item.shi1_netname,
                    Remark = item.shi1_remark ?? "",
                    IsAdminShare = isAdmin,
                    UncPath = $@"\\{host}\{item.shi1_netname}"
                });
            }
        }
        finally { NetApiBufferFree(buf); }
        return result;
    }

    private sealed class ShareSession : IDisposable
    {
        private readonly string? _path;
        public ShareSession(string? path) { _path = path; }
        public void Dispose()
        {
            if (_path == null) return; // 空会话（复用现有连接）：不断开别人的连接
            try { _ = WNetCancelConnection2(_path, 0, true); } catch { /* ignore */ }
        }
    }
}
