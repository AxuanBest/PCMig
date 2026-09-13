using System.Diagnostics;
using System.Net.Sockets;
using PCMig.Core.Models;
using PCMig.Core.Native;
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

    public async Task<PreflightReport> RunAsync(string host, string? user, string? password,
        IReadOnlyList<string> sourcePaths, string? targetRoot, long estimatedBytes = 0)
    {
        var report = new PreflightReport { Host = host };
        _log.Information("Preflight 开始: host={Host}, sources={Count}", host, sourcePaths.Count);

        // 1. SMB 445 端口可达（比 ping 更真实：很多环境禁 ICMP）
        var smbOk = false;
        try
        {
            using var tcp = new TcpClient();
            var connect = tcp.ConnectAsync(host, 445);
            var done = await Task.WhenAny(connect, Task.Delay(5000));
            smbOk = done == connect && tcp.Connected;
        }
        catch { smbOk = false; }
        Add(report, "SMB 445 端口可达", smbOk, "Error",
            smbOk ? "TCP 445 连接成功" : "无法连接 TCP 445：目标未开机/IP错误/防火墙未放行文件共享");

        if (!smbOk) return Finish(report);

        // 2. IPC$ 凭据会话
        var sessionOk = false;
        try
        {
            using var s = NetworkShare.Connect(host, user, password, _log);
            sessionOk = true;
            Add(report, "IPC$ 凭据会话", true, "Info", $"已连接（user={user ?? "<当前用户>"}）");

            // 3. 共享枚举（需要远程管理员权限）
            try
            {
                report.Shares = NetworkShare.EnumShares(host);
                Add(report, "共享枚举", true, "Info", $"发现 {report.Shares.Count} 个磁盘共享");
            }
            catch (Exception ex)
            {
                Add(report, "共享枚举", false, "Warning",
                    ex.Message + "；可手工输入管理共享路径（如 \\\\" + host + "\\D$）继续");
            }
        }
        catch (Exception ex)
        {
            Add(report, "IPC$ 凭据会话", false, "Error", ex.Message);
        }

        if (!sessionOk) return Finish(report);

        // 4. 源路径可读
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
            }
            catch (Exception ex) { Add(report, "目标盘检查", false, "Error", ex.Message); }
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

    private static void Add(PreflightReport r, string name, bool pass, string severity, string detail)
        => r.Checks.Add(new PreflightCheck { Name = name, Pass = pass, Severity = severity, Detail = detail });

    private PreflightReport Finish(PreflightReport r)
    {
        r.OverallPass = r.Checks.All(c => c.Pass || c.Severity != "Error");
        _log.Information("Preflight 结束: {Result}（{Checks} 项检查）", r.OverallPass ? "通过" : "存在阻断项", r.Checks.Count);
        return r;
    }
}
