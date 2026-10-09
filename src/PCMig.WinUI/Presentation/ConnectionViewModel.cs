using System.Collections.ObjectModel;
using System.IO;
using PCMig.Core.Logging;
using PCMig.Core.Models;
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
    private string _flowStatus = string.Empty;
    private string _inlineNote = string.Empty;
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
    public bool IsConnecting { get => _isConnecting; private set { if (Set(ref _isConnecting, value)) { Raise(nameof(CanConnect)); UpdateSourceState(); } } }

    /// <summary>
    /// ★ 2026-10-08（真机问题 4）★ <c>IsConnected</c> 一变就同步派生 <see cref="SourceState"/>：
    /// 单点派生保证"底栏指示灯"永远跟着**真实连接事实**走，不会有第二条各写各的路径。
    /// </summary>
    public bool IsConnected { get => _isConnected; private set { if (Set(ref _isConnected, value)) UpdateSourceState(); } }

    private SourceConnectionState _sourceState = SourceConnectionState.Disconnected;

    /// <summary>
    /// 源共享连接状态（底栏"源连接"指示器的**唯一业务事实来源**；View 只投影）。
    ///
    /// 状态转移（与用户指定的状态机一一对应）：
    ///   · 初始 / 连接失败 / 运行中源失效          ⇒ <see cref="SourceConnectionState.Disconnected"/>；
    ///   · 点"连接并列出共享""添加共享"（探测中）  ⇒ <see cref="SourceConnectionState.Connecting"/>；
    ///   · 预检通过或至少一个共享可访问 / 手工共享验证成功 ⇒ <see cref="SourceConnectionState.Connected"/>。
    /// 判据**只来自连接与共享可达性**：ping 通、能上网、网卡 UP 都不得让它变绿。
    /// </summary>
    public SourceConnectionState SourceState
    {
        get => _sourceState;
        private set => Set(ref _sourceState, value);
    }

    /// <summary>
    /// 由既有连接事实**单点派生**源连接状态（不允许任何调用方各写各的）：
    /// 正在连接 ⇒ Connecting；已连接（= 预检通过或存在可访问共享）⇒ Connected；否则 Disconnected。
    /// </summary>
    private void UpdateSourceState()
        => SourceState = IsConnecting ? SourceConnectionState.Connecting
            : IsConnected ? SourceConnectionState.Connected
            : SourceConnectionState.Disconnected;

    /// <summary>
    /// ★ 2026-10-08（真机问题 4）★ 运行中发现源共享**真正失效**（SMB 会话掉了 / 共享不再可达）：
    /// 如实回到 <see cref="SourceConnectionState.Disconnected"/>，绝不继续亮绿灯骗用户。
    /// 这是"已建立连接后来失效"的唯一入口，供后续的会话失效检测路径调用。
    /// </summary>
    internal void NotifySourceLost()
    {
        if (!IsConnected) return;
        IsConnected = false;
        SetFlowStatus("源共享已失效：请重新连接旧电脑后再继续。");
    }
    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>
    /// ★ UI Closure 2026-10-05（用户指令 UI-10：状态消息路由）★
    /// 流程级状态（正在连接…／发现共享结论／正在探测…／已添加并勾选…）的**专用通道**，
    /// 只由 <see cref="SetFlowStatus"/> 写入，由 Shell 左侧提示卡（OperationalStatus 通道）显示。
    /// <para>
    /// 为什么要与 <see cref="Status"/> 分开：<c>Status</c> 同时承载字段级校验（"请先输入 IP"）与流程播报，
    /// 而契约要求"表单内只留字段校验错误／必须依附字段的即时提示／极短局部说明，流程状态走提示卡通道"。
    /// 两者混用会让同一句流程播报既挤在表单里、又说不清该看哪里。
    /// </para>
    /// </summary>
    public string FlowStatus { get => _flowStatus; private set => Set(ref _flowStatus, value); }

    /// <summary>
    /// 表单内局部说明（字段校验错误／必须依附字段的即时提示／极短结论），只由 <see cref="SetInlineNote"/> 写入，
    /// 显示在 Step 1 表单的 <c>StatusLineText</c>（AutomationId <c>Step1.StatusText</c>）。
    /// </summary>
    public string InlineNote { get => _inlineNote; private set => Set(ref _inlineNote, value); }

    private string _inlineAdvice = string.Empty;

    /// <summary>
    /// 表单内**操作建议**（第二行，次要色），只由 <see cref="SetInlineNote(string, string)"/> 写入。
    /// ★ 2026-10-08（真机问题 1）★ 与 <see cref="InlineNote"/> 分开的原因：失败提示必须把
    /// "发生了什么"与"现在该怎么做"分层，而 Core/Win32/SMB 的长原文既不该挤进摘要、也不该把 Step 1 布局撑破。
    /// </summary>
    public string InlineAdvice { get => _inlineAdvice; private set => Set(ref _inlineAdvice, value); }

    private string _flowUserHint = string.Empty;

    /// <summary>
    /// ★ 2026-10-08 真机复验（返修 R1）★ 连接失败的**用户下一步**通道，路由到 Shell 提示卡的 UserHint 行。
    /// <para>与 <see cref="FlowStatus"/> 同一个目的地（左侧「提示」卡），但语义分工：
    /// FlowStatus = 发生了什么（OperationalStatus）；本属性 = 用户下一步怎么做（UserHint）；
    /// <see cref="FlowErrorSummary"/> = 必要的错误摘要（ErrorSummary）。</para>
    /// <para>为什么不写进表单：用户口径是"完整用户提示必须进入左侧 ShellHintCard"，
    /// Step 1 表单只留字段级短校验（主机名/共享名/密码格式）。</para>
    /// </summary>
    public string FlowUserHint { get => _flowUserHint; private set => Set(ref _flowUserHint, value); }

    private string _flowErrorSummary = string.Empty;

    /// <summary>★ 2026-10-08 真机复验（返修 R1）★ 连接失败的**错误摘要**通道（Shell 提示卡 ErrorSummary 行，警示色）。</summary>
    public string FlowErrorSummary { get => _flowErrorSummary; private set => Set(ref _flowErrorSummary, value); }

    /// <summary>流程级播报：同时写兼容字段 <see cref="Status"/>（顶栏 ToolTip 等既有消费点），并进提示卡通道。</summary>
    private void SetFlowStatus(string text)
    {
        Status = text;
        FlowStatus = text;
        // 普通流程播报 = 状态翻篇：上一次失败的"下一步引导"与"错误摘要"必须同时作废，
        // 否则提示卡会残留旧错误的建议行（新状态却配着旧建议，用户会被带偏）。
        FlowUserHint = string.Empty;
        FlowErrorSummary = string.Empty;
    }

    /// <summary>
    /// ★ 2026-10-08 真机复验（返修 R1）★ 连接/共享失败的**三通道一次落位**（全部进左侧 Shell 提示卡）。
    /// <para>operational → OperationalStatus（发生了什么）；errorSummary → ErrorSummary（必要的错误摘要）；
    /// userHint → UserHint（用户下一步怎么做）。长原文由调用方写日志，本方法只收已经压过的文本。</para>
    /// <para>纪律：本方法**不写** <see cref="InlineNote"/>/<see cref="InlineAdvice"/>，并顺手清空它们 ——
    /// 保证同一套失败文本不会在表单与提示卡同时出现（用户明确禁止两处重复）。</para>
    /// </summary>
    private void SetFlowFailure(string operational, string errorSummary, string userHint)
    {
        Status = operational;
        FlowStatus = operational;
        FlowErrorSummary = errorSummary;
        FlowUserHint = userHint;
        InlineNote = string.Empty;
        InlineAdvice = string.Empty;
    }

    /// <summary>字段级／错误级说明：同时写兼容字段 <see cref="Status"/>，但**不进**提示卡。</summary>
    private void SetInlineNote(string text) { Status = text; InlineNote = text; InlineAdvice = string.Empty; }

    /// <summary>带操作建议的字段级／错误级说明（摘要 + 建议两层；见 <see cref="InlineAdvice"/>）。</summary>
    private void SetInlineNote(string text, string advice) { Status = text; InlineNote = text; InlineAdvice = advice; }

    /// <summary>
    /// ★ 2026-10-08（真机问题 1）★ 把 Core / Win32 / SMB 的长失败原文压成**用户可读摘要 + 一句可执行建议**。
    ///
    /// 纪律（用户指定）：
    ///   · 摘要只取异常消息的**第一行**并截断到 180 字符 —— 页面只呈现"发生了什么 + 该怎么做"；
    ///   · 完整原文（含 Win32Error / IPC$ / 1219 / 67 / 1327 等）**只进日志**，绝不铺满 Step 1 主界面；
    ///   · 错误归类只做**关键词**匹配，不解析错误码语义：认不出来就给通用建议，绝不猜。
    /// </summary>
    internal static (string Summary, string Advice) SummarizeFailure(string headline, string target, Exception ex)
    {
        var first = FirstLineOf(ex.Message);
        if (first.Length > 180) first = string.Concat(first.AsSpan(0, 180), "…");
        var text = headline + " " + target;
        if (!string.IsNullOrWhiteSpace(first)) text = text + Environment.NewLine + first;
        return (text, AdviceFor(first));
    }

    private static string FirstLineOf(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return string.Empty;
        var idx = message.IndexOfAny(new[] { '\r', '\n' });
        return (idx >= 0 ? message[..idx] : message).Trim();
    }

    /// <summary>
    /// 预检**未抛异常但未通过**时的「摘要 + 建议」拆分（真机问题 1 的第二条失败路径）。
    /// <para>
    /// 与 <see cref="SummarizeFailure"/> 的差别：那条路径手上有一个 <see cref="Exception"/>；
    /// 这条路径手上只有 <c>PreflightReport</c> 里 Error 检查项的 <c>Detail</c> ——
    /// 真机实测该 Detail 可达 400+ 字（含 ①② 区指引与多条建议），
    /// 直接当状态句上屏就会把小尺寸容器撑爆，因此这里只取**第一句**作摘要，
    /// 其余原文作为建议放进表单内的有界滚动区，完整 Detail 由调用方写日志。
    /// </para>
    /// </summary>
    internal static (string Summary, string Advice) SummarizeReportFailure(string host, string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return ($"未能连接 {host}。", "请检查网络、共享名与账号密码后重试，或在②区手动输入共享名直接探测。");
        }

        var firstLine = FirstLineOf(detail);
        var sentenceEnd = firstLine.IndexOf('。');
        var headRaw = sentenceEnd >= 0 ? firstLine[..(sentenceEnd + 1)] : firstLine;
        var head = headRaw.Length > 160 ? string.Concat(headRaw.AsSpan(0, 160), "…") : headRaw;

        var rest = detail.Length > headRaw.Length ? detail[headRaw.Length..].Trim() : string.Empty;
        if (rest.Length == 0) rest = AdviceFor(firstLine);
        return (head, rest);
    }

    private static string AdviceFor(string message)
    {
        if (Has(message, "1326") || Has(message, "1327") || Has(message, "登录失败") || Has(message, "密码"))
            return "请核对用户名与密码（域账号写成 电脑名\\用户名），然后重试。";
        if (Has(message, "1219") || Has(message, "多重连接"))
            return "该旧电脑上已存在一个不同凭据的连接：先在资源管理器里断开它，或改用同一账号。";
        if (Has(message, "53") || Has(message, "67") || Has(message, "找不到网络"))
            return "请确认电脑名/IP 与网络可达，并让对方开启文件共享（防火墙允许 SMB）。";
        if (Has(message, "拒绝访问") || Has(message, "Access is denied") || Has(message, "0x5"))
            return "该账号没有访问权限：请换用有权限的账号，或在对方检查共享与 NTFS 权限。";
        if (Has(message, "超时") || Has(message, "timeout"))
            return "连接超时：请确认旧电脑开机且与本机在同一网络，然后重试。";
        return "未能读取该共享：请确认共享名、账户密码和共享权限。";
    }

    private static bool Has(string text, string token) => text.Contains(token, StringComparison.OrdinalIgnoreCase);
    public bool CanConnect => !IsConnecting && !string.IsNullOrWhiteSpace(Host);
    public string DeviceSummary => IsConnected ? $"已连接 {Host.Trim()}" : "尚未连接旧电脑";

    /// <summary>
    /// ★ 仅测试缝 ★ —— 强制"连接态"投影（生产路径**只有** <see cref="ConnectAsync"/> /
    /// <see cref="AddManualShareAsync"/> 会改 <c>IsConnected</c>）。
    ///
    /// 为什么必须有：<c>IsConnected</c> 的 setter 是私有的，且只有**真实 SMB 预检成功**才会置 true；
    /// 单测进程里不可能有旧电脑可连 ⇒ 「已连接 ⇒ 允许发起未完成任务探测」这条主路径
    /// （P2-6 的连接门 / 立即分支 / 世代门）无法被任何行为级测试触达。
    ///
    /// 边界（与 <c>TransferRunner</c> 等测试缝同一纪律）：**装配层（MainWindow/App/页面）绝不可调用**；
    /// 只允许单元测试调用，且调用会留下 Warning 日志便于事后甄别"测试缝被生产路径误用"。
    /// </summary>
    internal void ForceConnectedForTest(bool connected)
    {
        IsConnected = connected;
        _log.Warning("ForceConnectedForTest 测试缝被调用（仅测试允许；生产路径绝不调用）：{Connected}", connected);
    }

    public async Task ConnectAsync(string? password)
    {
        var original = Host?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(original))
        {
            SetInlineNote("请先输入旧电脑的 IP 或电脑名。");
            // 观察：动作被**合法拒绝**（原判据不变；仅记录既有结果）。
            ReportUiAction(PCMig.Diagnostics.Abstractions.Events.UiEvents.ActionRejected,
                new PCMig.Diagnostics.Abstractions.Payloads.UiEligibilityPayload(false, "empty-host"),
                PCMig.Diagnostics.Abstractions.DiagnosticOutcome.Rejected,
                PCMig.Diagnostics.Abstractions.DiagnosticLevel.Warning);
            return;
        }

        // 主机框允许直接粘贴 \\IP\共享名；共享名作为源路径提示交给预检（无 IPC$ 的目标靠它完成直连验证）。
        SplitHostAndShare(original, out var host, out var directShare);
        if (string.IsNullOrWhiteSpace(host))
        {
            SetInlineNote("路径格式不正确，应为 \\IP\\共享名 或 IP。");
            ReportUiAction(PCMig.Diagnostics.Abstractions.Events.UiEvents.ActionRejected,
                new PCMig.Diagnostics.Abstractions.Payloads.UiEligibilityPayload(false, "invalid-host-path"),
                PCMig.Diagnostics.Abstractions.DiagnosticOutcome.Rejected,
                PCMig.Diagnostics.Abstractions.DiagnosticLevel.Warning);
            return;
        }
        if (!host.Equals(original, StringComparison.Ordinal)) Host = host; // 回填规范化主机名

        // ★ 缺陷 A-04（同族第二处入口）：粘贴 `\\主机\D:` / `\\主机\D$\` 时也要规范化 + 校验 ★
        // 否则畸形共享名会被当作 sourcePaths 提示传给预检（预检判"可访问"），最终在计划扫描阶段抛异常。
        if (directShare is not null)
        {
            var normalized = ExtractShareName(directShare);
            if (IsValidShareName(normalized)) directShare = normalized;
            else
            {
                _log.Warning("忽略非法粘贴共享名: raw={Raw} host={Host}", directShare, host);
                directShare = null;
            }
        }

        // 会话语义：只有连到“不同主机”时才清理旧会话；同一主机重连保持现有 SMB 会话（凭据/连接可复用）。
        if (_shareSession is not null && !_shareSessionHost.Trim().Equals(host, StringComparison.OrdinalIgnoreCase))
        {
            _shareSession.Dispose();
            _shareSession = null;
            _shareSessionHost = string.Empty;
        }

        IsConnecting = true;
        SetFlowStatus($"正在连接 {host} 并发现共享…");
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
            // ★ 缺陷 A-01：管理共享默认勾选前必须先确认"当前凭据真的读得到" ★
            // 旧行为是"管理共享且名为 D$/E$ ⇒ 默认勾选"。在没有开放该管理共享的普通用户环境下，
            // 用户什么都不改直接点下一步 ⇒ 预检必然出现 `源路径 \\主机\D$` Error ⇒ 阻断，
            // 而失败文案还不指名是哪条路径（A-01a）——等于"默认状态走不通、且用户不知道要改什么"。
            // 现在：非管理共享保持原有默认勾选语义；管理共享先做一次可访问性探测，
            // 只有确实可读才默认勾选，不可读的改为默认不勾 + 备注写明原因（用户仍可手动勾选）。
            foreach (var share in report.Shares)
            {
                var kind = share.IsAdminShare ? "管理共享" : "共享";
                var aliases = share.Aliases.Count > 0 ? $" · 已合并 {string.Join("、", share.Aliases)}" : string.Empty;
                var remark = share.Remark + aliases;
                var defaultSelected = !share.IsAdminShare;

                if (share.IsAdminShare && share.Name is "D$" or "E$")
                {
                    var reachable = false;
                    try { reachable = await Task.Run(() => Directory.Exists(share.UncPath)); } catch { /* 视为不可达 */ }
                    defaultSelected = reachable;
                    remark += reachable
                        ? " · 当前凭据可访问，已默认勾选"
                        : " · 当前凭据不可访问，故默认未勾选；要迁移请用对方开放的共享（或让管理员授权）";
                    _log.Information("管理共享默认勾选判定: {Unc} reachable={Reachable}", share.UncPath, reachable);
                }

                Shares.Add(new ShareItem
                {
                    Name = share.Name,
                    UncPath = share.UncPath,
                    Kind = kind,
                    Remark = remark,
                    IsSelected = defaultSelected
                });
            }

            // ★ 缺陷 O-B22c-1 ★ 一句「已连接 X，发现 0 个共享」曾把三种完全不同的结果混成一种。
            // 结论文案统一由 BuildConnectStatus 产出（纯函数，三种分支均有单测）。
            // ★ 2026-10-08 真机复验（用户问题 1 → 返修 R1：口径反转）★ 失败结论进**左侧提示卡三通道**：
            //   ① 用户明确要求：完整用户可读说明必须显示在左侧 Shell「提示」卡里 ——
            //      OperationalStatus = 发生了什么、UserHint = 用户下一步怎么做、ErrorSummary = 必要的错误摘要；
            //   ② Step 1 表单只留**字段级**短校验（主机名/共享名/密码格式），不再承载连接失败说明；
            //   ③ 预检检查项的 Detail 原文（真机实测 400+ 字，含 Win32 / IPC$ / 67 / 1219 / 1327）**只进日志**；
            //   ④ 同一套失败文本绝不在表单与提示卡同时出现（用户明确禁止两处重复）。
            // 判定与 BuildConnectStatus 的三分支**同一口径**：既没复用现有连接、又没列出任何共享、
            // 也没能证明凭据有效 ⇒ 结论就是「没能确认连接」（对应 BuildConnectStatus 的兜底分支）。
            // 真机实测该兜底分支返回的是检查项 Detail 原文（400+ 字）—— 那正是把提示卡撑爆的来源。
            var credentialProven = report.OverallPass && report.CredentialVerified;
            var failedToConfirm = !report.CredentialReused && Shares.Count == 0 && !credentialProven;
            if (failedToConfirm)
            {
                var detail = report.Checks.FirstOrDefault(c => !c.Pass && c.Severity == "Error")?.Detail;
                _log.Warning("Step 1 连接失败（预检未通过，非异常路径）: host={Host} detail={Detail}", host, detail);
                var (failSummary, failAdvice) = SummarizeReportFailure(host, detail);
                SetFlowFailure($"未能连接 {host}。", failSummary, failAdvice);
            }
            else
            {
                SetFlowStatus(BuildConnectStatus(host, report, Shares.Count));
            }

            // 直接粘贴 \\IP\共享名（对齐 WPF MainViewModel 507-543 语义）：
            // 命中枚举结果就勾选；枚举里没有（对方无 IPC$/枚举被拦）就直接探测该共享是否可访问。
            if (directShare is not null)
            {
                var match = Shares.FirstOrDefault(s => s.Name.Equals(directShare, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    match.IsSelected = true;
                    SetFlowStatus($"已定位到共享 {match.UncPath}，已勾选，可进入下一步。");
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
                        SetFlowStatus($"自动列出共享失败，但已直接定位并勾选 {unc}。这正是无 IPC$ 环境的推荐用法。");
                        _log.Information("手动指定共享直连成功: {Unc}", unc);
                    }
                    else
                    {
                        SetFlowFailure($"未发现共享 {directShare}，且直接访问 {unc} 也失败。", $"直接访问 {unc} 未成功。",
                            "请确认共享名拼写与对方权限，或先在资源管理器里用凭据连过该机；也可在“手动共享名”框输入后点“添加共享”。");
                        _log.Warning("粘贴共享 {Unc} 既未出现在枚举结果中，直连探测也失败", unc);
                    }
                }
            }

            IsConnected = report.OverallPass || Shares.Count > 0;
            // 观察：VM 状态确实被写入（投影来源变化）。
            ReportUiAction(PCMig.Diagnostics.Abstractions.Events.UiEvents.ProjectionChanged,
                new PCMig.Diagnostics.Abstractions.Payloads.UiProjectionChangedPayload("Vm", 2),
                PCMig.Diagnostics.Abstractions.DiagnosticOutcome.Succeeded,
                PCMig.Diagnostics.Abstractions.DiagnosticLevel.Debug);
        }
        catch (Exception ex)
        {
            IsConnected = false;
            // ★ 2026-10-08（真机问题 1）★ 只把"摘要 + 建议"给页面；完整异常原文交给下一行的日志。
            var (failSummary, failAdvice) = SummarizeFailure("无法连接", host, ex);
            SetFlowFailure($"连接 {host} 时发生错误。", failSummary, failAdvice);
            _log.Error(ex, "WinUI Step 1 connection failed: {Host}", host);
            // 观察：域层动作失败（只记类型/错误码，不落异常原文）。
            ReportUiAction(PCMig.Diagnostics.Abstractions.Events.UiEvents.ActionFaulted,
                null,
                PCMig.Diagnostics.Abstractions.DiagnosticOutcome.Failed,
                PCMig.Diagnostics.Abstractions.DiagnosticLevel.Error,
                exceptionType: ex.GetType().Name,
                hresult: ex.HResult,
                phase: "connect-preflight");
        }
        finally { IsConnecting = false; }
    }

    /// <summary>
    /// 观察用薄封装：按**UI 无关**路径发布"用户动作链"事件，并自动继承当前动作身份
    /// （<c>ActionScope</c>）。放在这里而不是 WinUI 诊断层，是因为本文件会被既有测试项目
    /// 按源码链入编译（不得依赖 WinUI 类型与诊断运行时）。
    /// </summary>
    private static void ReportUiAction(
        PCMig.Diagnostics.Abstractions.EventDescriptor descriptor,
        PCMig.Diagnostics.Abstractions.IDiagnosticPayload? payload,
        PCMig.Diagnostics.Abstractions.DiagnosticOutcome outcome,
        PCMig.Diagnostics.Abstractions.DiagnosticLevel level,
        string? exceptionType = null,
        int? hresult = null,
        string? phase = null)
        => PCMig.Core.Diagnostics.CoreDiagnostics.PublishUiAction(
            descriptor, payload, outcome, level, "ConnectionViewModel", exceptionType, hresult, durationMs: null, phase);

    public async Task AddManualShareAsync(string? password)
    {
        // 主机框可能粘着 \\IP\共享：先拆出主机（与 WPF AddManualShareAsync 596-603 一致）。
        SplitHostAndShare(Host?.Trim() ?? string.Empty, out var host, out _);
        if (string.IsNullOrWhiteSpace(host)) { SetInlineNote("请先输入旧电脑的 IP 或电脑名。"); return; }

        // 共享名框也允许直接粘 \\IP\共享名：只取共享名，避免拼出 \\IP\IP\d。
        var name = ExtractShareName(ManualShareName);
        if (string.IsNullOrEmpty(name)) { SetInlineNote("请输入共享名，例如 d、D$ 或 Users。"); return; }

        // ★ 缺陷 A-04：非法共享名必须在拼 UNC / 探测网络之前拦下 ★
        // 走过这里的残留字符（如 `D:`）会被拼成 `\\主机\D:`：预检把它判成"路径存在且可访问"，
        // 而「预检并生成计划」的扫描阶段直接抛 IOException（DirStat.TopLevelDirs），用户看到的是
        // "准备失败：文件名、目录名或卷标语法不正确"。所以这里必须给出可执行的纠正文案并终止。
        if (!IsValidShareName(name))
        {
            SetInlineNote($"共享名 “{name}” 含非法字符（\\ / : * ? \" < > |）。请只填 \\\\{host}\\ 后面那一段，例如 d、D$、Users。");
            _log.Warning("拒绝非法共享名: input={Input} normalized={Name} host={Host}", ManualShareName, name, host);
            return;
        }

        var unc = $@"\\{host}\{name}";
        if (Shares.Any(s => s.UncPath.Equals(unc, StringComparison.OrdinalIgnoreCase))) { SetInlineNote($"{unc} 已在共享列表中。"); return; }

        SetFlowStatus($"正在探测 {unc}…");
        var reachable = false;
        Exception? probeError = null;
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
        catch (Exception ex) { probeError = ex; _log.Error(ex, "Manual share failed: {Share}", unc); }

        if (!reachable)
        {
            // ★ 2026-10-08（真机问题 1）★ 同连接失败口径：页面只给"摘要 + 建议"，
            //   完整异常原文进上面的日志（改前这里直接把 ex.Message 铺进去，长文案同样会撑破布局）。
            if (probeError is not null)
            {
                var (shareFailSummary, shareFailAdvice) = SummarizeFailure("无法访问", unc, probeError);
                SetFlowFailure($"访问 {unc} 失败。", shareFailSummary,
                    "请核对共享名（就是资源管理器地址栏 \\\\" + host + "\\ 后面的那一段）；若需要凭据，请填好用户名密码再点“添加共享”。" + shareFailAdvice);
            }
            else
            {
                SetFlowFailure($"访问 {unc} 失败。", $"未能访问 {unc}。",
                    $"请确认共享名、网络和凭据（就是资源管理器地址栏 \\\\{host}\\ 后面的那个名字）。");
            }
            return;
        }

        Shares.Add(new ShareItem { Name = name, UncPath = unc, Kind = "手动指定", IsSelected = true });
        ManualShareName = string.Empty;
        IsConnected = true;
        SetFlowStatus($"已添加并勾选 {unc}。");
        _log.Information("手动添加共享成功: {Unc}", unc);
    }

    public void NotifyNextStepUnavailable()
    {
        // ★ 返修 R1 ★ 阶段/流程说明一律走左侧提示卡；表单内只保留字段级短校验。
        SetInlineNote(string.Empty);
        SetFlowStatus(IsConnected
            ? "Step 1 已完成连接与共享发现；“选择数据与目标”将在下一阶段接入同一 Core 计划能力。"
            : "请先连接旧电脑并发现共享，再进入下一步。");
    }

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
            return parts.Length >= 2 ? NormalizeShareName(parts[1]) : (parts.Length == 1 ? NormalizeShareName(parts[0]) : string.Empty);
        }
        return NormalizeShareName(text);
    }

    /// <summary>
    /// 共享名规范化（缺陷 A-04）：用户常把「盘符写法」直接填进来（`D:` / `D:\`）。
    /// 旧实现只 <c>Trim('\\')</c>，于是 `D:` 拼成畸形 UNC `\\主机\D:`——它能骗过预检"源路径 pass"，
    /// 却在「预检并生成计划」的扫描阶段抛 `IOException: 文件名、目录名或卷标语法不正确`（DirStat.TopLevelDirs）。
    /// 现在统一去掉首尾分隔符与尾部冒号，得到真正的共享名（`D:`/`D:\`/`\\主机\D` → `D`）。
    /// </summary>
    private static string NormalizeShareName(string? raw) =>
        (raw ?? string.Empty).Trim().Trim('\\').TrimEnd(':').Trim('\\').Trim();

    /// <summary>
    /// 共享名合法性校验（缺陷 A-04）：Windows 共享名不允许 `\ / : * ? " &lt; &gt; |`。
    /// 非法输入必须**在拼 UNC / 探测网络之前**被拒绝，并给出可执行文案，绝不能进到计划生成阶段再炸。
    /// </summary>
    private static bool IsValidShareName(string name) =>
        name.Length > 0 && name.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) < 0;

    /// <summary>
    /// Step 1 连接结论文案（缺陷 O-B22c-1）。**必须区分三种看起来一样的结果**：
    ///   ① 用输入凭据建立了会话并列出共享 —— 正常的「已连接，发现 N 个共享」；
    ///   ② 复用了本机已有连接（Windows 1219：同一服务器只允许一套凭据）—— 用户输入的账号
    ///      **根本没有被使用**，界面必须明说，且不得把它呈现成"凭据正确"；
    ///   ③ 凭据根本没被证实（域控不可达 / 认证失败 / 对方不导出 IPC$ 且无旁证）—— 报
    ///      「无法确认这套账号密码」，**不允许**再说「已连接」。
    ///
    /// 抽成纯静态函数是为了让三种分支都能被单元测试直接覆盖（不依赖网络、不依赖 UI）。
    /// </summary>
    internal static string BuildConnectStatus(string host, PreflightReport report, int shareCount)
    {
        var reuseAdvice =
            $"若要改用你输入的账号：先在命令提示符执行 net use \\\\{host}\\ /delete" +
            "（或关掉所有访问该机的资源管理器窗口），再点「连接并列出共享」。";

        if (report.CredentialReused)
            return shareCount > 0
                ? $"已连上 {host}，发现 {shareCount} 个共享——注意：本次**复用了本机已有的连接，你输入的账号没有被使用**" +
                  $"（Windows 错误 1219：同一台服务器同时只允许一套凭据）。{reuseAdvice}"
                : $"已连上 {host}，但**你输入的账号没有被使用**：本机已有到该机的连接，Windows 拒绝再用第二套凭据" +
                  $"（错误 1219），产品复用的是那条现有连接，而它没有列出任何共享。{reuseAdvice}" +
                  "也可以先在②区「共享名」手动输入一个共享名（如 d）点「＋添加共享」试试。";

        if (shareCount > 0)
            return $"已连接 {host}，发现 {shareCount} 个共享。选择共享后可进入下一步。";

        if (report.OverallPass && report.CredentialVerified)
            return $"已连接 {host}（凭据已验证），但对方没有列出任何共享：可能是对方不开放共享枚举，或这个账号没有可读共享。" +
                   "请在②区「共享名」手动输入对方开放的共享名（例如 d、Users）再点「＋添加共享」，或在①粘贴完整共享路径。";

        var err = report.Checks.FirstOrDefault(c => !c.Pass && c.Severity == "Error")?.Detail;
        return err ?? $"未能确认与 {host} 的连接与凭据：请检查网络、共享名与账号密码后重试" +
                      "（可在②区手动输入共享名，那一步会真的建立数据共享会话并验证凭据）。";
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