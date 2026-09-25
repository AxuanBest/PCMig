using System.Collections.ObjectModel;
using System.IO;
using PCMig.Core.Logging;
using PCMig.Core.Native;
using PCMig.Core.Preflight;
using Serilog;

namespace PCMig.WinUI.Presentation;

/// <summary>
/// Step 1 WinUI adapter. It projects Core's PreflightChecker result and never
/// reimplements SMB/scan/planner/transfer logic. Password is received only for
/// the current call and is not stored in this object.
/// </summary>
public sealed class ConnectionViewModel : ObservableObject, IDisposable
{
    // 应用级日志（%ProgramData%\PCMig\Logs\app-*.log / app-*.jsonl），与 WPF/CLI 同一份诊断日志。
    // console:false —— GUI 进程没有控制台。刻意不 Dispose：见 Dispose() 注释。
    private readonly ILogger _log = LogBootstrap.CreateAppLogger(console: false);

    private IDisposable? _shareSession;
    private string _shareSessionHost = string.Empty;
    private string _host = string.Empty;
    private string _username = string.Empty;
    private string _manualShareName = string.Empty;
    private string _status = "输入旧电脑信息后开始连接。凭据只用于当前会话，不会写入任务文件。";
    private bool _benchmarkOnConnect = true;
    private bool _isConnecting;
    private bool _isConnected;

    public ObservableCollection<ShareItem> Shares { get; } = new();

    public string Host
    {
        get => _host;
        set { if (Set(ref _host, value)) Raise(nameof(CanConnect)); }
    }
    public string Username { get => _username; set => Set(ref _username, value); }
    public string ManualShareName { get => _manualShareName; set => Set(ref _manualShareName, value); }
    public bool BenchmarkOnConnect { get => _benchmarkOnConnect; set => Set(ref _benchmarkOnConnect, value); }
    public bool IsConnecting { get => _isConnecting; private set { if (Set(ref _isConnecting, value)) Raise(nameof(CanConnect)); } }
    public bool IsConnected { get => _isConnected; private set => Set(ref _isConnected, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool CanConnect => !IsConnecting && !string.IsNullOrWhiteSpace(Host);
    public string DeviceSummary => IsConnected ? $"已连接 {Host.Trim()}" : "尚未连接旧电脑";

    public async Task ConnectAsync(string? password)
    {
        var original = Host?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(original)) { Status = "请先输入旧电脑的 IP 或电脑名。"; return; }

        // 主机框允许直接粘贴 \\IP\共享名；共享名作为源路径提示交给预检（无 IPC$ 的目标靠它完成直连验证）。
        SplitHostAndShare(original, out var host, out var directShare);
        if (string.IsNullOrWhiteSpace(host)) { Status = "路径格式不正确，应为 \\IP\\共享名 或 IP。"; return; }
        if (!host.Equals(original, StringComparison.Ordinal)) Host = host; // 回填规范化主机名

        // 会话语义：只有连到“不同主机”时才清理旧会话；同一主机重连保持现有 SMB 会话（凭据/连接可复用）。
        if (_shareSession is not null && !_shareSessionHost.Trim().Equals(host, StringComparison.OrdinalIgnoreCase))
        {
            _shareSession.Dispose();
            _shareSession = null;
            _shareSessionHost = string.Empty;
        }

        IsConnecting = true;
        Status = $"正在连接 {host} 并发现共享…";
        try
        {
            var checker = new PreflightChecker(_log);
            var hints = directShare is null ? Array.Empty<string>() : new[] { $@"\\{host}\{directShare}" };
            // 预检里含 SMB 枚举、管理共享探测与可选的 64MB 链路测速——必须离开 UI 线程执行，
            // 否则 WinUI 界面会长时间无响应（WPF 侧同样用 Task.Run 包裹）。
            var report = await Task.Run(() => checker.RunAsync(
                host,
                string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
                string.IsNullOrWhiteSpace(password) ? null : password,
                hints,
                null,
                benchmark: BenchmarkOnConnect));

            Shares.Clear();
            foreach (var share in report.Shares)
            {
                var kind = share.IsAdminShare ? "管理共享" : "共享";
                var aliases = share.Aliases.Count > 0 ? $" · 已合并 {string.Join("、", share.Aliases)}" : string.Empty;
                Shares.Add(new ShareItem
                {
                    Name = share.Name,
                    UncPath = share.UncPath,
                    Kind = kind,
                    Remark = share.Remark + aliases,
                    IsSelected = !share.IsAdminShare || share.Name is "D$" or "E$"
                });
            }

            Status = report.OverallPass || Shares.Count > 0
                ? $"已连接 {host}，发现 {Shares.Count} 个共享。选择共享后可进入下一步。"
                : report.Checks.FirstOrDefault(c => !c.Pass && c.Severity == "Error")?.Detail ?? "连接未通过预检，请检查网络、共享与凭据。";

            // 直接粘贴 \\IP\共享名（对齐 WPF MainViewModel 507-543 语义）：
            // 命中枚举结果就勾选；枚举里没有（对方无 IPC$/枚举被拦）就直接探测该共享是否可访问。
            if (directShare is not null)
            {
                var match = Shares.FirstOrDefault(s => s.Name.Equals(directShare, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    match.IsSelected = true;
                    Status = $"已定位到共享 {match.UncPath}，已勾选，可进入下一步。";
                }
                else
                {
                    var unc = $@"\\{host}\{directShare}";
                    var reachable = false;
                    try { reachable = await Task.Run(() => Directory.Exists(unc)); } catch { /* 视为不可达 */ }
                    if (reachable)
                    {
                        Shares.Add(new ShareItem
                        {
                            Name = directShare,
                            UncPath = unc,
                            Kind = "共享（手动指定）",
                            Remark = "对方未开放枚举，直连探测成功",
                            IsSelected = true
                        });
                        Status = $"自动列出共享失败，但已直接定位并勾选 {unc}。这正是无 IPC$ 环境的推荐用法。";
                        _log.Information("手动指定共享直连成功: {Unc}", unc);
                    }
                    else
                    {
                        Status += $"（注意：未发现名为 {directShare} 的共享，且直接访问 {unc} 失败——请确认共享名拼写、对方权限，以及是否已在资源管理器里用凭据连过该机；也可在“手动共享名”框输入 {directShare} 点“添加共享”）";
                        _log.Warning("粘贴共享 {Unc} 既未出现在枚举结果中，直连探测也失败", unc);
                    }
                }
            }

            IsConnected = report.OverallPass || Shares.Count > 0;
        }
        catch (Exception ex)
        {
            IsConnected = false;
            Status = $"连接失败：{ex.Message}";
            _log.Error(ex, "WinUI Step 1 connection failed: {Host}", host);
        }
        finally { IsConnecting = false; }
    }

    public async Task AddManualShareAsync(string? password)
    {
        // 主机框可能粘着 \\IP\共享：先拆出主机（与 WPF AddManualShareAsync 596-603 一致）。
        SplitHostAndShare(Host?.Trim() ?? string.Empty, out var host, out _);
        if (string.IsNullOrWhiteSpace(host)) { Status = "请先输入旧电脑的 IP 或电脑名。"; return; }

        // 共享名框也允许直接粘 \\IP\共享名：只取共享名，避免拼出 \\IP\IP\d。
        var name = ExtractShareName(ManualShareName);
        if (string.IsNullOrEmpty(name)) { Status = "请输入共享名，例如 d、D$ 或 Users。"; return; }

        var unc = $@"\\{host}\{name}";
        if (Shares.Any(s => s.UncPath.Equals(unc, StringComparison.OrdinalIgnoreCase))) { Status = $"{unc} 已在共享列表中。"; return; }

        Status = $"正在探测 {unc}…";
        var reachable = false;
        string? probeError = null;
        var user = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim();
        try
        {
            if (user is not null)
            {
                // 有显式凭据 → 先为该共享建会话（SMB 协议：能连上即证明凭据有效），
                // 不再依赖“先去资源管理器连一次”。刻意不 Dispose 旧会话。
                var session = await Task.Run(() => NetworkShare.ConnectForTransfer(host, user, password, new[] { unc }, _log));
                if (session is not null)
                {
                    // 只有确实新建了会话才替换（释放旧会话）；返回 null = Core 判定现有连接已够用 → 原样保留。
                    _shareSession?.Dispose();
                    _shareSession = session;
                    _shareSessionHost = host;
                }
            }
            reachable = await Task.Run(() => { try { return Directory.Exists(unc); } catch { return false; } });
        }
        catch (Exception ex) { probeError = ex.Message; _log.Error(ex, "Manual share failed: {Share}", unc); }

        if (!reachable)
        {
            Status = probeError is not null
                ? $"连接 {unc} 失败：{probeError}（请核对共享名：就是资源管理器地址栏 \\{host}\\ 后面的那个名字；若需要凭据，请填好用户名密码再点“添加共享”）"
                : $"访问 {unc} 失败。请确认共享名、网络和凭据（就是资源管理器地址栏 \\{host}\\ 后面的那个名字）。";
            return;
        }

        Shares.Add(new ShareItem { Name = name, UncPath = unc, Kind = "手动指定", IsSelected = true });
        ManualShareName = string.Empty;
        IsConnected = true;
        Status = $"已添加并勾选 {unc}。";
        _log.Information("手动添加共享成功: {Unc}", unc);
    }

    public void NotifyNextStepUnavailable() =>
        Status = IsConnected
            ? "Step 1 已完成连接与共享发现；“选择数据与目标”将在下一阶段接入同一 Core 计划能力。"
            : "请先连接旧电脑并发现共享，再进入下一步。";

    /// <summary>拆分“主机 + 可选共享名”；主机框里粘贴 \\IP\共享名 时兼容。</summary>
    private static void SplitHostAndShare(string raw, out string host, out string? share)
    {
        host = raw.Trim();
        share = null;
        if (!host.StartsWith(@"\\", StringComparison.Ordinal)) return;
        var parts = host.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) { host = string.Empty; return; }
        host = parts[0];
        if (parts.Length > 1) share = parts[1];
    }

    /// <summary>从共享名输入提取纯共享名：兼容直接粘贴 \\IP\共享名（只取共享名部分）。</summary>
    private static string ExtractShareName(string? input)
    {
        var text = input?.Trim() ?? string.Empty;
        if (text.Length == 0) return string.Empty;
        if (text.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = text.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? parts[1] : (parts.Length == 1 ? parts[0] : string.Empty);
        }
        return text.Trim('\\');
    }

    public void Dispose()
    {
        _shareSession?.Dispose();
        _shareSession = null;
        _shareSessionHost = string.Empty;
        // 应用级 logger 刻意不 Dispose：它写入 %ProgramData%\PCMig\Logs 的共享日志文件，
        // 释放会截断异步写盘缓冲、并可能影响同进程其他组件继续写日志。
        // 口令只作为方法参数在调用栈上存在，本类不持有任何口令字段（不持久化）。
    }
}