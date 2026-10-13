using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Serilog;

namespace PCMig.Core.Native;

/// <summary>
/// 源对象身份指纹（缺陷 B12b4：同名共享被换底导致的"假成功"）。
///
/// 背景（Route A · B12b4 实证）：源共享 D 在复制途中被撤销，随后**以同名 D** 重建到
/// 另一个目录（同结构、同文件名、异内容）。对客户端而言共享名与 UNC 前缀完全没变，
/// 产品便按"同名即同源"继续续传，最终界面宣称「✔ 迁移完成 100.0%」，而目标端实测含有
/// 只存在于冒名树里的文件名与正文 ⇒ 数据完整性缺陷。
///
/// 修复思路：计划生成时给每个对象的**源根**取一枚文件系统级指纹并写进 plan.json；
/// 之后每一次开跑（含续传 / 定向修复）前重新取一次比对，不一致就拒绝继续传输。
///
/// 指纹取自 **FILE_ID_INFO**（服务端真实的卷序列号 + 128 位文件 ID；SMB2 原生支持）。
/// 为什么不用 <c>GetFileInformationByHandle</c>：实测（LAB-DST01 → LAB-SRC01）它对
/// UNC 返回的是**重定向器占位值**——`\\LAB-SRC01\D` 与 `\\LAB-SRC01\C$` 都返回
/// 卷序列号 0、FileIndex 5，无法区分不同盘。FILE_ID_INFO 则给出
/// `D` 与 `D$`（同物理路径）完全一致、`C$` 卷序列号不同、子目录各不相同，跨调用稳定。
///
/// 纪律：
///  · 取不到指纹一律返回 null ⇒ 调用方必须把 null 当作"没有基线/不判定"，**绝不**当成"匹配"；
///  · 只读属性（FILE_READ_ATTRIBUTES），不改动源端任何数据。
/// </summary>
public static class SourceIdentity
{
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;

    /// <summary>目录句柄必须带这个标志（否则 CreateFile 打不开目录）。</summary>
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    /// <summary>FILE_INFO_BY_HANDLE_CLASS.FileIdInfo</summary>
    private const int FileIdInfo = 18;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ID_INFO
    {
        public ulong VolumeSerialNumber;
        public ulong IdLow;
        public ulong IdHigh;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle hFile, int fileInformationClass,
        out FILE_ID_INFO lpFileInformation, int dwBufferSize);

    /// <summary>
    /// 取 <paramref name="path"/>（本地路径或 UNC 源根/源路径）的身份指纹。
    /// 返回**分量串**（可含一项或两项，分号分隔）：
    ///   · <c>fsid:&lt;卷序列号&gt;:&lt;128位文件ID&gt;</c> —— 目录/卷级身份；
    ///   · <c>sharepath:&lt;服务端共享物理路径&gt;</c> —— 共享名背后的真实目录（如 <c>D:\</c>）。
    /// 两项都取不到返回 null。
    ///
    /// **为什么要两项**：TV-B12b4 tv2 实测——共享被同名换底后，飞行中只靠 fsid 未能触发
    /// （robocopy /MT+/J 饱和数据面时，对远端目录再开一个句柄可能失败 ⇒ 只能"不判定"）。
    /// 共享物理路径走的是 srvsvc RPC（与数据面独立），在数据面饱和时依然可用，
    /// 因此两项互补：任一项在计划期与复验期都可用，就能判定；比对时按分量逐项比较。
    /// </summary>
    public static string? Capture(string? path, ILogger? log = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        // ★ P0-06 ★ 指纹探测必须有界：CreateFileW 与 srvsvc RPC 都是**不可取消的阻塞调用**，
        //   指向已撤销的共享/离线主机时每个对象实测阻塞约 16s（15 个对象 = 4 分钟界面卡死、
        //   停止按钮点不动的直接成因）。这里做两件事：
        //   ① UNC 先花 ≤1.2s 探一次 445 是否有人应答（结果按主机短缓存，同主机多对象不再重探）；
        //   ② 两项指纹整体加 3s 预算，超预算就**如实"本次不判定"**（绝不拿旧值/猜测冒充身份）。
        //   语义不软化：不判定在调用方仍按"无法判定更改"处理，只把"卡死"换成"快速说不判定"。
        if (!HostAnswering(path!, log)) return null;

        Task<string?> probe;
        try { probe = Task.Run(() => CaptureCore(path!, log)); }
        catch (Exception ex) { log?.Debug(ex, "源身份指纹：{Path} 探测调度异常", path); return null; }

        if (!probe.Wait(ProbeBudgetMs))
        {
            log?.Warning("源身份指纹：{Path} 探测超过 {Budget}ms 预算（网络卡顿 / 数据面饱和），本次不判定",
                path, ProbeBudgetMs);
            return null;
        }
        try { return probe.Result; }
        catch (Exception ex) { log?.Debug(ex, "源身份指纹：{Path} 探测异常", path); return null; }
    }

    /// <summary>两项指纹的实际取值（无超时保护，只在 <see cref="Capture"/> 的预算内被调用）。</summary>
    private static string? CaptureCore(string path, ILogger? log)
    {
        var parts = new List<string>(2);
        var fid = TryFileId(path, log);
        if (fid is not null) parts.Add(fid);
        var sp = TrySharePath(path, log);
        if (sp is not null) parts.Add(sp);
        if (parts.Count == 0)
        {
            log?.Warning("源身份指纹：{Path} 两种判据都取不到（文件 ID 与共享物理路径均不可用），本次不判定", path);
            return null;
        }
        return string.Join(";", parts);
    }

    /// <summary>指纹探测的整体预算（毫秒）。超过即"本次不判定"，不再让界面等着阻塞调用返回。</summary>
    private const int ProbeBudgetMs = 3000;

    /// <summary>UNC 主机"445 有没有人应答"的探测预算（毫秒）。</summary>
    private const int HostProbeMs = 1200;

    /// <summary>可达结论缓存时长：可达 3s（足够覆盖同一次的多个对象），不可达 5s（避免反复等同一个死主机）。</summary>
    private static readonly ConcurrentDictionary<string, DateTime> HostReachableUntil = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTime> HostUnreachableUntil = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ★ P0-06 ★ UNC 源主机的"是否有人应答 SMB"快速探测（不替代真正的指纹，只用来**避免无谓地**
    /// 掉进 16s 的阻塞调用）。本地路径（含盘符根）直接放行 —— 本地调用不会走网络超时。
    /// 结论只缓存很短时间且**只用于省掉重复探测**，从不作为"共享可用"的对外结论。
    /// </summary>
    private static bool HostAnswering(string path, ILogger? log)
    {
        if (!path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        var segs = path.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length < 2) return true;
        var host = segs[0];
        var now = DateTime.UtcNow;
        if (HostReachableUntil.TryGetValue(host, out var r) && r > now) return true;
        if (HostUnreachableUntil.TryGetValue(host, out var u) && u > now)
        {
            log?.Debug("源身份指纹：{Host} 刚刚（{Sec}s 内）已探明不可达，本次不判定（省掉重复阻塞）",
                host, HostProbeMs / 1000);
            return false;
        }

        var answered = false;
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, 445);
            answered = connect.Wait(HostProbeMs) && client.Connected;
        }
        catch (Exception ex) { log?.Debug(ex, "源身份指纹：{Host}:445 探测异常", host); answered = false; }

        if (answered)
        {
            HostReachableUntil[host] = now.AddSeconds(3);
            HostUnreachableUntil.TryRemove(host, out _);
        }
        else
        {
            HostUnreachableUntil[host] = now.AddSeconds(5);
            HostReachableUntil.TryRemove(host, out _);
            log?.Warning("源身份指纹：{Host}:445 在 {Ms}ms 内无人应答（共享已撤销 / 主机离线），本次不判定",
                host, HostProbeMs);
        }
        return answered;
    }

    /// <summary>目录/卷级身份（FILE_ID_INFO：服务端真实卷序列号 + 128 位文件 ID）。</summary>
    private static string? TryFileId(string? path, ILogger? log)
    {
        try
        {
            using var h = CreateFileW(path!, FILE_READ_ATTRIBUTES,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (h.IsInvalid)
            {
                log?.Debug("源身份指纹：无法打开 {Path}（Win32={Code}）", path, Marshal.GetLastWin32Error());
                return null;
            }
            if (!GetFileInformationByHandleEx(h, FileIdInfo, out var fi, Marshal.SizeOf<FILE_ID_INFO>()))
            {
                log?.Debug("源身份指纹：FILE_ID_INFO 查询失败 {Path}（Win32={Code}）", path, Marshal.GetLastWin32Error());
                return null;
            }
            if (fi.VolumeSerialNumber == 0 && fi.IdLow == 0 && fi.IdHigh == 0) return null;
            return Format(fi.VolumeSerialNumber, fi.IdHigh, fi.IdLow);
        }
        catch (Exception ex)
        {
            log?.Debug(ex, "源身份指纹：{Path} FILE_ID_INFO 取值异常", path);
            return null;
        }
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
    private static extern int NetShareGetInfo(string serverName, string netName, int level, out IntPtr bufPtr);

    [DllImport("Netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    /// <summary>
    /// 共享名背后的服务端物理路径（SHARE_INFO_2.path）。仅对 UNC 且带共享名的路径有效；
    /// 拿不到（非 Windows SMB / 权限不足 / 本地路径）返回 null。
    /// </summary>
    private static string? TrySharePath(string path, ILogger? log)
    {
        if (!path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        var segs = path.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length < 2) return null;
        var server = segs[0];
        var share = segs[1];
        IntPtr buf = IntPtr.Zero;
        try
        {
            var rc = NetShareGetInfo(server, share, 2, out buf);
            if (rc != 0 || buf == IntPtr.Zero)
            {
                log?.Debug("源身份指纹：NetShareGetInfo({Server},{Share}) 返回 {Rc}", server, share, rc);
                return null;
            }
            var si = Marshal.PtrToStructure<SHARE_INFO_2>(buf);
            var p = si.shi2_path;
            if (string.IsNullOrWhiteSpace(p)) return null;
            return "sharepath:" + NormalizeSharePath(p);
        }
        catch (Exception ex)
        {
            log?.Debug(ex, "源身份指纹：共享物理路径取值异常 {Path}", path);
            return null;
        }
        finally
        {
            if (buf != IntPtr.Zero) { try { _ = NetApiBufferFree(buf); } catch { /* 释放失败不影响判定 */ } }
        }
    }

    /// <summary>共享物理路径归一化：去尾部反斜杠 + 统一大小写（避免 `D:\` 与 `d:\` 被判成不同）。</summary>
    internal static string NormalizeSharePath(string raw) =>
        raw.Trim().TrimEnd('\\').ToUpperInvariant();

    /// <summary>纯格式化（单测可直接覆盖）。</summary>
    internal static string Format(ulong volumeSerial, ulong idHigh, ulong idLow) =>
        $"fsid:{volumeSerial:X16}:{idHigh:X16}{idLow:X16}";

    /// <summary>
    /// 按分量比较两个指纹串：**只有两边都提供的分量**参与判定。
    /// 这样"某一项这次取不到"不会造成误判（宁可少判，不可误判）。
    /// </summary>
    internal static bool IdentityChanged(string? expected, string? actual)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual)) return false;
        var e = Split(expected);
        var a = Split(actual);
        if (e.FileId is not null && a.FileId is not null &&
            !string.Equals(e.FileId, a.FileId, StringComparison.Ordinal)) return true;
        if (e.SharePath is not null && a.SharePath is not null &&
            !string.Equals(e.SharePath, a.SharePath, StringComparison.Ordinal)) return true;
        return false;
    }

    private static (string? FileId, string? SharePath) Split(string identity)
    {
        string? fid = null, sp = null;
        foreach (var seg in identity.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg.StartsWith("fsid:", StringComparison.Ordinal)) fid = seg;
            else if (seg.StartsWith("sharepath:", StringComparison.Ordinal)) sp = seg;
        }
        return (fid, sp);
    }

    /// <summary>人可读的差异描述（用于日志/界面）。</summary>
    internal static string Describe(string? identity) =>
        string.IsNullOrEmpty(identity) ? "（未取得）" : identity;
}