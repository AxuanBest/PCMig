using System.ComponentModel;
using System.Runtime.InteropServices;
using PCMig.Core.Models;

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
            // 已存在会话：强制断开后用显式凭据重试一次
            _ = WNetCancelConnection2(ipc, 0, true);
            err = WNetAddConnection2(nr, string.IsNullOrEmpty(password) ? null : password, user, 0);
        }

        if (!explicitCreds && err is ERROR_ALREADY_ASSIGNED or ERROR_SESSION_CREDENTIAL_CONFLICT)
        {
            // 空凭据且已有会话：复用（当前身份之前已在别处连通过这台机器）
            log?.Information("复用现有会话 {Share}", ipc);
            return new ShareSession(ipc);
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
                ERROR_BAD_NETPATH => "网络路径不可达（检查 IP/电脑名、目标是否开机、防火墙是否放行文件和打印机共享 SMB-In）",
                ERROR_EXTENDED_ERROR => "网络扩展错误（可能 SMB 协议协商失败）",
                _ => new Win32Exception(err).Message
            };
            log?.Warning("连接 {Share} 失败: Win32Error={Code} {Hint}", ipc, err, hint);
            throw new IOException($"无法连接 {ipc}（错误 {err}）：{hint}");
        }

        log?.Information("已建立会话 {Share} (user={User})", ipc, explicitCreds ? user : "<当前 Windows 身份>");
        return new ShareSession(ipc);
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

        return merged.OrderBy(s => s.IsAdminShare).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
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
        private readonly string _path;
        public ShareSession(string path) { _path = path; }
        public void Dispose()
        {
            try { _ = WNetCancelConnection2(_path, 0, true); } catch { /* ignore */ }
        }
    }
}
