using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
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

    /// <summary>
    /// ★ P0-05 ★ 源共享/源根的**实测可达性证据**（唯一能点亮绿灯的依据）：
    /// 键 = 目标（<c>\\host</c> 或 <c>\\host\share</c>），值 = 最近一次实测结论 + 证据文字。
    /// 只有实测（共享枚举、手工共享探测、目录/文件读取、验证报告、引擎失败码）才允许写入，
    /// 绝不写入"上次成功"这类历史布尔值。
    /// </summary>
    private readonly Dictionary<string, SourceShareFact> _shareFacts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ★ 2026-10-24（R1-6B 发布阻断）★ **子路径诊断**：键 = 比所属共享根更深的完整路径
    /// （<c>\\host\share\sub\...</c>），值 = 最近一次"读不到"的现场原因。
    ///
    /// 【为什么单列一张表，而不是直接写进 `_shareFacts`】子路径读不到**不能否证共享根读得到**：
    /// 目录不存在、该目录 ACL 不含当前账号、路径已随迁移被清空，都会让子路径探测失败，
    /// 而共享本身完全可用。把这种结论写成共享级"失效"，`Derive` 就会同时看到
    /// fresh（共享根可达）与 dead（子路径）⇒ 恒 Partial 琥珀 —— 就是发布阻断要修的假黄灯。
    ///
    /// 但它也**不是**可以丢掉的信息：真实存在的"指定子目录访问不了"必须让用户看见，
    /// 所以它在这里独立保存，由 <see cref="UpdateSourceState"/> 折成**有依据的 Partial**，
    /// 而不是被"已连接"吞掉（要求：不允许简单隐藏黄色状态）。
    ///
    /// 复用 <see cref="SourceShareFact"/> 是为了让它**同样受 <see cref="EvidenceTtl"/> 约束**：
    /// 子路径读不到是"某一时刻的现场结论"，和可达证据一样会过期。没有有效期的话，
    /// 一次瞬断留下的诊断会让灯永久变黄，那又变成另一种不可自愈的假黄灯。
    /// </summary>
    private readonly Dictionary<string, SourceShareFact> _subPathIssues = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ★ P0-05（2026-10-13 定点修复）★ **可达证据的有效期**。超过它就不得再显示"已连接"：
    /// 绿灯的唯一含义是"刚刚确认过读得到"，而不是"几分钟前读到过"。
    /// 默认 60 秒（用户指定的隔离测试目标："60 秒内不再错误显示已连接"）；依据见
    /// <see cref="PCMig.Core.Util.SourceEvidenceLedger.DefaultTtl"/>。
    /// </summary>
    internal static readonly TimeSpan EvidenceTtl = PCMig.Core.Util.SourceEvidenceLedger.DefaultTtl;

    /// <summary>空闲期读探测的最小间隔（有界：不产生无控制的 SMB 请求）。</summary>
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(20);

    /// <summary>巡检节流：底栏定时器每 16 ms 触发一次，但证据重算最多每秒一次。</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);

    private DateTime _lastSweepUtc = DateTime.MinValue;
    private DateTime _lastProbeUtc = DateTime.MinValue;

    /// <summary>单飞闸门：同一时刻最多一个读探测在跑（绝不叠加 SMB 请求）。</summary>
    private readonly System.Threading.SemaphoreSlim _probeGate = new(1, 1);

    /// <summary>UI 线程队列（用于把后台探测结论回投到 UI 线程；为空时直接调用，便于单测）。</summary>
    private Microsoft.UI.Dispatching.DispatcherQueue? _uiQueue;

    /// <summary>★ P0-05 ★ 交入 UI 线程队列（构造于 UI 线程时调用；没有也不影响正确性）。</summary>
    internal void AttachUiQueue(Microsoft.UI.Dispatching.DispatcherQueue? queue) => _uiQueue = queue;

    /// <summary>
    /// ★ P0-05（2026-10-13）★ 时间源（**测试缝**）：单测把它换成可控时钟，即可精确验证
    /// "证据过期后不再显示已连接"（用户指定的隔离测试目标：60 秒）。
    /// </summary>
    internal static Func<DateTime> Clock = () => DateTime.UtcNow;

    /// <summary>
    /// ★ P0-05（2026-10-13）★ 可达性**读探测**（**测试缝**）：返回枚举到的共享数。
    /// 默认实现是一次有界的 SMB `NetShareEnum`（一次往返；不 ping、不查网卡、不认历史连接）；
    /// 抛异常 = 这次探测"没得出结论"。
    /// </summary>
    internal static Func<string, int> ShareCountProbe =
        host => PCMig.Core.Native.NetworkShare.EnumShares(host).Count;

    /// <summary>
    /// ★ 2026-10-23（真机 P0-06 / 黄灯专项）★ **共享级**只读探测（**测试缝**）：枚举指定共享根的一层目录项。
    ///
    /// 【为什么必须换成共享级】原来的空闲探测走 <see cref="ShareCountProbe"/>（主机级
    /// <c>NetShareEnum</c>）。现场旧电脑不导出 <c>IPC$</c>（错误 67），该调用必抛 ⇒ 每 20 秒把证据
    /// 整个清空一次 ⇒ 灯永远回不到绿。更要紧的是它证明的只是"这台主机还肯回答问题"，
    /// 而用户关心的是一条具体共享 <c>\\host\e</c> 现在还读不读得到 —— 两者不是一回事
    /// （任务书 B1：「连接灯不应只代表 TCP 445 可达」）。
    ///
    /// 【为什么不用 Directory.Exists】它吞掉异常只返回 false，拿不到 Win32 码，
    /// 于是"凭据失效(1326)"和"网络瞬断(53)"会被压成同一个结论 —— 前者该红、后者只能说"不知道"。
    /// 这里显式枚举以让异常带着码抛出来，由 <see cref="ClassifyProbeFailure"/> 分级。
    ///
    /// 【开销】一层枚举、只读、不开临时文件、不改会话；单次往返量级与一次 <c>NetShareEnum</c> 相当。
    /// </summary>
    internal static Func<string, bool> ShareReadProbe = unc =>
    {
        // 惰性序列必须真的走一步才会发出 SMB 请求（空目录也算读得到）。
        using var entries = Directory.EnumerateFileSystemEntries(unc).GetEnumerator();
        _ = entries.MoveNext();
        return true;
    };

    /// <summary>
    /// ★ 2026-10-23 ★ 探测失败分级：**只有"权威失效"才允许写失效证据**，其余一律当"没有结论"。
    /// 这是任务书 B4 ②⑤⑥⑦⑧ 的直接落地 —— 把"未知"误判成"断开"会让灯乱红，
    /// 比原来的"永远黄"更糟（用户会去重连一台其实好好的电脑）。
    /// </summary>
    private static (bool AuthoritativeLoss, int Win32) ClassifyProbeFailure(Exception ex)
    {
        var code = ex.HResult & 0xFFFF;
        // 权威失效：共享名已不存在(67) / 拒绝访问(5) / 凭据失效(1219,1326)。
        // 这几条都来自**这一次对这条共享根**的实测，作用域就是这条共享，不牵连同主机其他共享。
        var authoritative = code switch
        {
            5 or 67 or 1219 or 1326 => true,
            _ => false,
        };
        return (authoritative, code);
    }

    /// <summary>★ P0-05 ★ 是否处于"已确认失效、正在重探/重连"的恢复过程（⇒ Recovering）。</summary>
    private bool _recovering;

    /// <summary>
    /// ★ 2026-10-23（Preview.7 收口 P0）★ **预检执行的测试缝**：默认就是真实
    /// <see cref="PreflightChecker.RunAsync"/>，单测换成受控实现即可在**不碰真实 SMB**的前提下
    /// 走完 <see cref="ConnectAsync"/> 的完整业务入口（含落库、IsConnected、投影）。
    ///
    /// 为什么需要它：本轮 P0 的验收要求是"必须新增真实业务入口级测试，不能只通过直接调用
    /// <see cref="NotifyShareReachability"/> 证明成功"。而 <c>ConnectAsync</c> 的真实预检要连一台
    /// 真实旧电脑 —— 单测进程里永远做不到。缝只替换"外部世界的回答"，**落库规则、键归一、
    /// 作用域摘除、状态派生全部仍由正式代码计算**（与 <c>ShareReadProbe</c> 同一纪律）。
    /// </summary>
    internal static Func<ILogger, string, string?, string?, IReadOnlyList<string>, string?, long, bool,
        Task<PreflightReport>> PreflightRunner =
        (log, host, user, password, sources, targetRoot, estimatedBytes, benchmark) =>
            new PreflightChecker(log).RunAsync(host, user, password, sources, targetRoot, estimatedBytes, benchmark);

    /// <summary>
    /// ★ 2026-10-23（Preview.7 收口 P0）★ **SMB 会话建立的测试缝**（同 <see cref="PreflightRunner"/> 纪律）：
    /// 默认就是真实的 <see cref="NetworkShare.ConnectForTransfer"/>。返回 <see langword="null"/> 表示
    /// "现有本机连接已够用"，与生产语义一致。
    /// </summary>
    internal static Func<string, string?, string?, IReadOnlyList<string>, ILogger, IDisposable?> ShareSessionConnector =
        NetworkShare.ConnectForTransfer;

    /// <summary>
    /// ★ 真机问题 1（2026-10-21）★ 传输活动位：**只由真实字节增长**置位（见
    /// <see cref="NotifyTransferActive"/>），绝不由 Phase==Running 推断。
    /// </summary>
    private bool _transferActive;

    // ★ 2026-10-24（用户冻结的四态灯定义）★ 灯作用域与投影结果。
    //   _lampScopeOverride：传输期间由"当前活动对象所属源共享"接管作用域；
    //   没有它，多共享迁移中途换对象时指示灯会继续代表第一条共享 —— 用户明令禁止这种含糊。
    private string? _lampScopeOverride;
    private SourceLampState _lampState = SourceLampState.Disconnected;
    private string _lampTargetScope = string.Empty;
    private bool _lampUnknown;

    /// <summary>
    /// ★ R2-A-2（2026-10-24）★ 指示灯作用域内是否**确证**不可达（有失效证据、且没有新鲜可达证据）。
    /// 与 <see cref="_lampUnknown"/>（无结论）严格区分：修复入口只允许在"确证不可达"时拒绝启动，
    /// 不能因为"证据过期"就把用户的修复请求挡回去。
    /// </summary>
    private bool _lampKnownUnreachable;
    private object? _lampTextSnapshot;
    private object? _lampSummarySnapshot;

    /// <summary>最近一次因"传输活动"续期证据的时刻（节流用：TTL 60 秒，1 秒续一次足够）。</summary>
    private DateTime _lastActivityRenewUtc = DateTime.MinValue;

    /// <summary>传输活动续期的最小间隔。</summary>
    private static readonly TimeSpan ActivityRenewInterval = TimeSpan.FromSeconds(1);

    /// <summary>单条源目标的实测结论（★ 不是"上次连上过"，是"刚刚读到/读不到"★）。</summary>
    private sealed record SourceShareFact(string Target, bool Reachable, string Evidence, DateTime AtUtc);

    public ObservableCollection<ShareItem> Shares { get; } = new();

    public string Host
    {
        get => _host;
        set
        {
            if (!Set(ref _host, value)) return;
            Raise(nameof(CanConnect));
            // ★ 2026-10-23（Preview.7 收口 P0）★ 换了旧电脑 = 会话边界变了 ⇒ 纪元 +1，
            //   让任何在飞的探测结论失效（它们问的是上一台机器）。
            Interlocked.Increment(ref _sessionEpoch);
            // ★ 2026-10-23（Preview.7 收口 P0）★ 换了旧电脑，旧主机的证据对新主机毫无意义 ——
            //   留着它们会让"上一台机器连得上"变成"这一台也连得上"（B4 ⑨）。
            //   注意作用域：只摘**别的**主机的键，本主机已有的共享级事实原样保留
            //   （用户在 Step 1 重新点连接时，这批事实会被新一轮实测覆盖）。
            ForgetFactsOfOtherHosts(value);
        }
    }
    public string Username { get => _username; set => Set(ref _username, value); }
    public string ManualShareName { get => _manualShareName; set => Set(ref _manualShareName, value); }
    public bool BenchmarkOnConnect { get => _benchmarkOnConnect; set => Set(ref _benchmarkOnConnect, value); }
    public bool IsConnecting { get => _isConnecting; private set { if (Set(ref _isConnecting, value)) { Raise(nameof(CanConnect)); UpdateSourceState(); } } }

    /// <summary>
    /// ★ 2026-10-22 ★ harness 缝：**只搬运"正在连接"这个既有事实**，不进 Release。
    ///
    /// 为什么不加 formal/测试专用 API：<see langword="internal"/> 成员在整个 assembly 内可见，
    /// 任何生产代码都能调 —— 那就成了"给正式版本加了测试入口"。这里用 <c>#if DEBUG</c>
    /// 把它编译期排除，行为与真正的连接流程（<c>IsConnecting = true</c>）完全同一条路径。
    /// </summary>
#if DEBUG
    internal void HarnessSetConnecting(bool value) => IsConnecting = value;
#endif

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
    /// 由既有连接事实**单点派生**源连接状态（不允许任何调用方各写各的）。
    ///
    /// ★ P0-05 ★ 判据升级：**只有"可读探测确认过"才配绿色**。旧版只看 <c>IsConnected</c> 这个
    /// "上次连上过"的布尔值 ⇒ 共享被公司策略关掉后灯还是绿的（现场日志：14:58 起
    /// <c>Win32Error=67</c>，界面照旧显示已连接）。现在按证据分档：
    ///   · 正在连接 ⇒ Connecting；
    ///   · 恢复中（已确认失效，正在重连/重探）⇒ Recovering；
    ///   · 有可达证据 + 有失效证据 ⇒ Partial（多共享里坏了一条，必须能定位）；
    ///   · 只有可达证据 ⇒ Connected（绿）；
    ///   · 只有失效证据 ⇒ Disconnected；
    ///   · 两者都没有但 <c>IsConnected</c> 为真（只有历史事实、没有任何实测）⇒ **SuspectOrUnknown（琥珀，不是绿）**；
    ///   · 其余 ⇒ Disconnected。
    /// 未知一律不当绿：绿的唯一含义是"刚刚确认过读得到"。
    /// </summary>
    private void UpdateSourceState()
    {
        // ★ P0-05（2026-10-13）★ 分档判据**全部**来自证据账本（含有效期），
        //   旧实现只数"有没有可达事实"、从不看时间 ⇒ 撤销共享后灯可以一直绿。
        var now = Clock();
        var ledger = new PCMig.Core.Util.SourceEvidenceLedger(EvidenceTtl);
        foreach (var f in _shareFacts.Values.ToArray()) ledger.Record(f.Target, f.Reachable, f.Evidence, f.AtUtc);

        var state = MapLedgerState(ledger.Derive(now, IsConnecting, _recovering, IsConnected));

        // ★ 2026-10-24（R1-6B 发布阻断）★ **子路径诊断折成有依据的 Partial**。
        //   语义：共享根刚刚确认读得到（否则不会是 Connected），但这条共享下**某个具体子目录**
        //   在同一有效期窗口内被实测读不到 ⇒ 这是货真价实的"部分可用"，灯必须黄。
        //   既不能把它并进共享级失效（会伪造"整条共享坏了"），也不能当没发生（要求：不允许简单隐藏黄灯）。
        //   过期的诊断不算数（与可达证据同一套 TTL 纪律），并在这里顺手清掉，避免无限堆积。
        var expiredIssues = _subPathIssues
            .Where(kv => now - kv.Value.AtUtc > EvidenceTtl).Select(kv => kv.Key).ToList();
        foreach (var k in expiredIssues) _subPathIssues.Remove(k);
        var freshIssues = _subPathIssues.Count;
        if (freshIssues > 0 && state == SourceConnectionState.Connected)
            state = SourceConnectionState.Partial;

        // ★ 真机问题 1（2026-10-21）★ **已连接 + 确有传输** ⇒ 绿呼吸（Transferring）。
        //   只在账本已经给出 Connected 时升级，绝不把 SuspectOrUnknown / Partial 也升级成绿 ——
        //   传输活动是"证据续期"的理由，不是"跳过证据"的理由。
        if (state == SourceConnectionState.Connected && _transferActive)
            state = SourceConnectionState.Transferring;

        // ★ 2026-10-23（真机 P0-06 / 任务书 §九 诊断增强）★
        //   只在**有效状态变化**时落一条，且用既有 Serilog（不新开文件、不走 16 ms 节拍）。
        //   现场日志里 81 行 app log 一条源连接状态转移都没有，导致"为什么是黄灯"只能靠读源码反推 ——
        //   这一段就是为了让下次不用再反推。
        var previous = _sourceState;
        if (state != previous) LogSourceStateTransition(previous, state, now, ledger);

        SourceState = state;

        // ★ 2026-10-24（用户冻结的四态灯定义）★ 内部六态 → 用户可见四态的**唯一**投影点。
        //   内部状态继续用于诊断文字与日志（哪里不健康、证据多旧），但它不再直接决定灯色。
        RecomputeLamp(now);

        // ★ Preview.6（2026-10-22）★ 派生属性**只在真变了才广播**。
        //   改前这里无条件 Raise 三个属性名，于是本方法每被调一次（传输期间每 80 ms 一次，由
        //   SyncTransferActivity → UpdateSourceState 驱动）就会沿 SourceStateDetail 这条分支
        //   把 UpdateSourceConnectionIndicator() 再打一遍 —— 与"灯在闪"直接相关。
        //   现在：值没变 ⇒ 不广播 ⇒ 下游根本不重入。状态真变了才通知，语义不变。
        RaiseIfChanged(ref _unreachableSharesSnapshot, UnreachableShares, nameof(UnreachableShares));
        RaiseIfChanged(ref _reachableTargetsSnapshot, VerifiedReachableTargets, nameof(VerifiedReachableTargets));
        RaiseIfChanged(ref _expiredTargetsSnapshot, EvidenceExpiredTargets, nameof(EvidenceExpiredTargets));
        RaiseIfChanged(ref _stateDetailSnapshot, SourceStateDetail, nameof(SourceStateDetail));
    }

    /// <summary>
    /// <summary>
    /// ★ 2026-10-24（用户冻结的四态灯定义）★ 内部六态 → 用户可见四态的投影。
    ///
    /// 【产品定义（冻结，不再讨论）】
    ///   · 红常亮 <see cref="SourceLampState.Disconnected"/> —— 未连接源共享；
    ///   · 黄呼吸 <see cref="SourceLampState.Connecting"/>   —— 正在寻找共享 / 建立连接 / 重新连接（含正在验证）；
    ///   · 绿常亮 <see cref="SourceLampState.Connected"/>    —— 已连接源共享，当前无实际数据传输；
    ///   · 绿呼吸 <see cref="SourceLampState.Transferring"/> —— 已连接源共享，且确有数据传输活动。
    /// 禁止第五种灯色；禁止把 Partial / SuspectOrUnknown 当灯色；禁止因子目录 ACL 错误变黄。
    ///
    /// 【为什么灯要有"作用域"】
    ///   多共享场景下"有没有连上"必须回答"连上的是哪一条"。规则：
    ///   ① 传输中 ⇒ 当前活动对象所属源共享（<c>_lampScopeOverride</c>，由 NotifyTransferActive 捕获）；
    ///   ② 迁移开始前 ⇒ 勾选列表中的第一条共享（并在 <see cref="LampScopeSummary"/> 里写明是它）；
    ///   ③ 一条都没勾 ⇒ 退回主机级事实。
    ///   这样"某条非当前共享的子目录报错"不会把当前共享的绿灯打黄。
    ///
    /// 【证据不足怎么办（不得虚构绿色，也不得谎报断开）】
    ///   作用域内既无新鲜可达证据、也无失效证据 ⇒ 灯按**保守**处理为红常亮，
    ///   同时在 <see cref="LampText"/> 里如实写明"证据已过期、正在重新确认"，并立刻把空闲探测
    ///   提前到最近一拍（见 <see cref="ProbeIfDueAsync"/> 的 LastProbeUtc 语义）由它给出结论。
    ///   只有确实处于连接/重连/验证过程中才显示黄呼吸 —— 那时"正在连接"是真话。
    /// </summary>
    private void RecomputeLamp(DateTime nowUtc)
    {
        var scope = ComputeLampScope();
        var (reachable, dead, unknown) = ScopeEvidence(scope, nowUtc);

        // ★ 2026-10-24（R2-C 真实渲染取证发现）★ 该作用域**从未有过任何证据**时，
        // 不得把账本已经判定的"已连接"渲染成红灯 —— 那会让用户看到"已连接但灯是红的"。
        // 只在"完全没有事实"时兜底；**有事实但已过期**仍走红灯 + "证据已过期，正在重新确认"，
        // 有事实且失效时更由红灯优先（两处都不许被兜底覆盖，否则等于藏起真实问题）。
        if (unknown && !dead && !reachable && scope.Length > 0 && !_shareFacts.ContainsKey(scope))
        {
            switch (SourceState)
            {
                case SourceConnectionState.Connected:
                case SourceConnectionState.Partial:
                case SourceConnectionState.Transferring:
                    reachable = true;
                    unknown = false;
                    break;
            }
        }

        // 四态判定本身抽成纯函数（ProjectLampState），便于单元测试逐条钉住映射，
        // 不必依赖"能不能把 IsConnecting 设成中间态"这种不可观测条件。
        var lamp = ProjectLampState(IsConnecting, _recovering, reachable, _transferActive);

        // 传输结束后作用域回归默认（勾选列表第一条），避免灯一直盯着上一趟的对象。
        if (!_transferActive) _lampScopeOverride = null;

        _lampTargetScope = scope;
        _lampUnknown = unknown && !dead;
        _lampKnownUnreachable = dead && !reachable;
        LampState = lamp;
        RaiseIfChanged(ref _lampTextSnapshot, LampText, nameof(LampText));
        RaiseIfChanged(ref _lampSummarySnapshot, LampScopeSummary, nameof(LampScopeSummary));
    }

    /// <summary>
    /// ★ 2026-10-24（用户冻结的四态灯定义）★ **内部判据 → 四态**的唯一纯函数。
    /// 顺序即优先级：正在连接/重连/验证 &gt; 已连接且有传输 &gt; 已连接空闲 &gt; 未连接。
    /// 注意最后一路是"兜底为红"：证据过期也走这里 —— 保守，但文字里必须说明"正在重新确认"，
    /// 绝不为了好看而借用黄色（黄色只表示"真的在连"）。
    /// </summary>
    internal static SourceLampState ProjectLampState(bool connecting, bool recovering, bool reachable, bool transferActive)
        => connecting || recovering ? SourceLampState.Connecting
         : reachable && transferActive ? SourceLampState.Transferring
         : reachable ? SourceLampState.Connected
         : SourceLampState.Disconnected;
    /// <summary>
    /// 计算指示灯当前代表的作用域（主机级键或共享级键）。纯函数，便于单元测试直接断言。
    /// </summary>
    internal string ComputeLampScope()
    {
        if (_transferActive && !string.IsNullOrWhiteSpace(_lampScopeOverride))
            return _lampScopeOverride!;

        var roots = SelectedShareRoots();
        if (roots.Count > 0) return ShareScopeOf(roots[0]);

        var host = Host?.Trim() ?? string.Empty;
        return host.Length > 0 ? ShareScopeOf(NormalizeTarget(host)) : string.Empty;
    }

    /// <summary>
    /// 读取某个作用域的证据档位：可达且未过期 / 明确失效 / 无结论。
    /// ★ 只认**本作用域**的事实：别的共享可达不能证明这一条可达（这正是 Partial 不能被绿灯掩盖的原因）。
    /// </summary>
    private (bool Reachable, bool Dead, bool Unknown) ScopeEvidence(string scope, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(scope)) return (false, false, true);
        if (!_shareFacts.TryGetValue(scope, out var fact)) return (false, false, true);
        if (!fact.Reachable) return (false, true, false);
        return nowUtc - fact.AtUtc <= EvidenceTtl
            ? (true, false, false)
            : (false, false, true);   // 可达事实已过期 ⇒ 无结论（不冒充绿色，也不冒充断开）
    }

    /// <summary>
    /// ★ 2026-10-24 ★ 用户可见的四态灯（唯一权威）。View 只读它，不再自己 switch 内部六态。
    /// </summary>
    public SourceLampState LampState
    {
        get => _lampState;
        private set => Set(ref _lampState, value);
    }

    /// <summary>指示灯作用域（主机或共享 UNC），用于"这盏灯代表哪条共享"的显式说明。</summary>
    public string LampTargetScope => _lampTargetScope;

    /// <summary>
    /// ★ R2-A-2 ★ 当前指示灯作用域是否确证不可达。修复入口用它做"源共享失效不得启动新修复"的判据：
    /// 只看确证失效，不看"证据过期"（后者是尚未确认，不该冒充失效）。
    /// </summary>
    internal bool SourceKnownUnreachable => _lampKnownUnreachable;

    /// <summary>
    /// ★ 2026-10-24 ★ 四态可读文案。四态不够细，但"为什么是红的"必须说清楚：
    /// 证据过期 ≠ 确认断开，所以这里把两种情况在文字上分开，颜色保持一致（不新增灯色）。
    /// </summary>
    public string LampText => LampState switch
    {
        SourceLampState.Connected => "已连接（源共享实测可读）",
        SourceLampState.Transferring => "已连接，正在传输",
        SourceLampState.Connecting => "正在连接 / 重新连接旧电脑…",
        _ when _lampUnknown => "未确认连接（可访问证据已过期，正在重新确认）",
        _ => "未连接源共享",
    };

    /// <summary>
    /// ★ 2026-10-24 ★ 多共享汇总：X/Y 可访问 + 灯当前代表哪条共享。
    /// 要求：不能用单条共享的绿代表所有共享正常，也不能因为非当前共享的子目录报错把当前灯打黄。
    /// </summary>
    public string LampScopeSummary
    {
        get
        {
            var roots = SelectedShareRoots()
                .Select(ShareScopeOf)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var nowUtc = Clock();
            var ok = roots.Count(r => ScopeEvidence(r, nowUtc).Reachable);
            var target = _lampTargetScope;
            var name = string.IsNullOrEmpty(target) ? "（未选择共享）" : target;
            return roots.Count <= 1
                ? $"指示灯作用域：{name}"
                : $"指示灯作用域：{name}；可访问 {ok}/{roots.Count} 个共享";
        }
    }
    /// ★ 2026-10-23（真机 P0-06 / 任务书 §九 诊断增强）★ 源连接状态**有效变化**时记一条。
    ///
    /// 任务书要求的 11 项在这里一一落位（脱敏后）：
    ///   ① 时间戳；② 源主机（只记主机名，不含凭据与用户名）；③ 当前/前一状态；④ 变更原因；
    ///   ⑤ 成功证据来源与采集时间；⑥ 证据有效期与年龄；⑦ 最近一次真实源共享访问结果；
    ///   ⑧ 最近一次失败类型与作用范围；⑨ 阶段与暂停状态（由调用方视角决定，这里记可得的）；
    ///   ⑩ 传输活动判据；⑪ 最终 UI 状态（即本次落库的 state）。
    ///
    /// ★ 纪律 ★ 只在状态**真的变了**时调用；不写文件、不新开 sink，走既有 app logger；
    ///   同样的状态反复推导不会产生任何一行（这就是"不在节拍上写盘"的保证）。
    /// </summary>
    private void LogSourceStateTransition(
        SourceConnectionState previous,
        SourceConnectionState current,
        DateTime nowUtc,
        PCMig.Core.Util.SourceEvidenceLedger ledger)
    {
        try
        {
            var fresh = _shareFacts.Values.Count(f => f.Reachable && nowUtc - f.AtUtc <= EvidenceTtl);
            var dead = _shareFacts.Values.Count(f => !f.Reachable);
            var expired = _shareFacts.Values.Count(f => f.Reachable && nowUtc - f.AtUtc > EvidenceTtl);
            var newestReachable = _shareFacts.Values
                .Where(f => f.Reachable)
                .OrderByDescending(f => f.AtUtc)
                .FirstOrDefault();
            var oldestAge = _shareFacts.Values.Count == 0
                ? double.NaN
                : (nowUtc - _shareFacts.Values.Min(f => f.AtUtc)).TotalSeconds;

            _log.Information(
                "源连接状态转移：{Previous} → {Current} | host={Host} | 原因={Reason} | " +
                "账本=fresh:{Fresh} dead:{Dead} expired:{Expired} 子路径诊断:{SubPathIssues} | 有效期={TtlSeconds:0}s 最旧证据年龄={OldestAge:0.0}s | " +
                "最新可达证据={Evidence} @{EvidenceAt:HH:mm:ss}@{EvidenceAge:0.0}s 前 | " +
                "最近失败={LastFailure} 作用域={FailureScope} | 传输活动=_transferActive:{TransferActive} | " +
                "连接位=IsConnected:{IsConnected} 连接中={IsConnecting} 恢复中={Recovering}",
                previous,
                current,
                Host?.Trim() ?? "(none)",
                ReasonForState(current, fresh, dead, expired, _subPathIssues.Count),
                fresh,
                dead,
                expired,
                _subPathIssues.Count,
                EvidenceTtl.TotalSeconds,
                oldestAge,
                newestReachable?.Evidence ?? "(none)",
                newestReachable?.AtUtc.ToLocalTime(),
                newestReachable is null ? double.NaN : (nowUtc - newestReachable.AtUtc).TotalSeconds,
                _probeInconclusiveReason ?? "(none)",
                dead > 0 ? "共享级（该共享根实测失效）" : "主机/共享级（见 UnreachableShares）",
                _transferActive,
                IsConnected,
                IsConnecting,
                _recovering);
        }
        catch (Exception ex)
        {
            // 诊断本身绝不影响业务状态推导。
            _log.Debug(ex, "源连接状态转移日志写入失败（忽略）");
        }
    }

    /// <summary>把"为什么落到这个状态"翻译成一句可核对的话（纯派生，不产生新事实）。</summary>
    private static string ReasonForState(SourceConnectionState state, int fresh, int dead, int expired, int subPathIssues = 0) => state switch
    {
        SourceConnectionState.Transferring => "可达证据新鲜 + 引擎确认的字节增长",
        SourceConnectionState.Connected => $"可达证据新鲜（fresh={fresh}）",
        SourceConnectionState.Partial => (dead > 0, subPathIssues > 0) switch
        {
            (true, true) => $"同时存在可达与失效证据（fresh={fresh}，dead={dead}），另有子路径读不到（{subPathIssues} 条）",
            (true, false) => $"同时存在可达与失效证据（fresh={fresh}，dead={dead}）",
            _ => $"共享根可达但指定子路径读不到（fresh={fresh}，子路径诊断={subPathIssues}）",
        },
        SourceConnectionState.SuspectOrUnknown => expired > 0
            ? $"可达证据全部过期（expired={expired}，超过 {EvidenceTtlSeconds:0} 秒）"
            : "只有历史连接事实，没有任何实测证据",
        SourceConnectionState.Recovering => "已确认失效，正在重连/重探",
        SourceConnectionState.Connecting => "正在建立连接并探测共享",
        _ => dead > 0 ? $"只有失效证据（dead={dead}）" : "没有任何可用证据",
    };

    private static double EvidenceTtlSeconds => EvidenceTtl.TotalSeconds;

    /// <summary>
    /// ★ Preview.6 ★ 值没变就不广播（<c>INotifyPropertyChanged</c> 的通行纪律）。
    /// 这几个派生属性每次都会**新造**一个 List，因此必须按**内容**比较而不能比引用；
    /// 内容没变 ⇒ 静默返回，下游（底栏指示灯投影）根本不会被叫起来。
    /// </summary>
    private void RaiseIfChanged<T>(ref object? snapshot, T current, string propertyName)
    {
        if (SequenceEqual(snapshot, current)) return;
        snapshot = current;
        Raise(propertyName);
    }

    private static bool SequenceEqual(object? snapshot, object? current) => (snapshot, current) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (string a, string b) => string.Equals(a, b, StringComparison.Ordinal),
        (IReadOnlyList<string> a, IReadOnlyList<string> b) => a.SequenceEqual(b, StringComparer.Ordinal),
        _ => Equals(snapshot, current),
    };

    // 上述派生属性的"上次广播值"快照（只用于抑制重复通知，不参与任何业务判定）。
    private object? _unreachableSharesSnapshot;
    private object? _reachableTargetsSnapshot;
    private object? _expiredTargetsSnapshot;
    private object? _stateDetailSnapshot;

    /// <summary>账本状态 → 底栏状态（一一对应；枚举分开是为了让 Core 侧规则可被单测推进时间验证）。</summary>
    private static SourceConnectionState MapLedgerState(PCMig.Core.Util.SourceEvidenceState state) => state switch
    {
        PCMig.Core.Util.SourceEvidenceState.Connecting => SourceConnectionState.Connecting,
        PCMig.Core.Util.SourceEvidenceState.Recovering => SourceConnectionState.Recovering,
        PCMig.Core.Util.SourceEvidenceState.Partial => SourceConnectionState.Partial,
        PCMig.Core.Util.SourceEvidenceState.Connected => SourceConnectionState.Connected,
        PCMig.Core.Util.SourceEvidenceState.SuspectOrUnknown => SourceConnectionState.SuspectOrUnknown,
        _ => SourceConnectionState.Disconnected,
    };

    /// <summary>
    /// ★ P0-05（2026-10-13）★ **可达证据已过期的目标**（曾经读得到、现在没有新鲜证据）。
    /// 底栏/提示卡据此说明"为什么不再显示已连接"。
    /// </summary>
    public IReadOnlyList<string> EvidenceExpiredTargets
    {
        get
        {
            var now = Clock();
            var ledger = new PCMig.Core.Util.SourceEvidenceLedger(EvidenceTtl);
            foreach (var f in _shareFacts.Values.ToArray()) ledger.Record(f.Target, f.Reachable, f.Evidence, f.AtUtc);
            return ledger.ExpiredReachable(now).Select(f => f.Target).ToList();
        }
    }

    /// <summary>
    /// ★ P0-05（2026-10-13）★ 空闲期**证据巡检**（由底栏定时器调用，必须在 UI 线程）：
    ///   ① 先按有效期重算状态 —— 证据一过期就降级为"待确认"（琥珀），**不需要任何 IO**；
    ///   ② 再按有界间隔异步发起一次读探测（单飞、不阻塞 UI），拿到新证据才可能重新变绿。
    /// </summary>
    internal void SweepEvidenceFreshness()
    {
        var nowUtc = Clock();
        if (nowUtc - _lastSweepUtc < SweepInterval) return;
        _lastSweepUtc = nowUtc;

        UpdateSourceState();
        _ = ProbeIfDueAsync(nowUtc);
    }

    /// <summary>
    /// ★ P0-05 ★ 与 <see cref="SweepEvidenceFreshness"/> 同逻辑，但**等待探测结束**再返回（单测用；
    /// UI 定时器路径不等待，避免任何 SMB 往返阻塞 UI 线程）。
    /// </summary>
    internal async Task SweepEvidenceFreshnessAsync()
    {
        UpdateSourceState();
        await ProbeIfDueAsync(Clock()).ConfigureAwait(false);
    }

    /// <summary>
    /// ★ P0-05 ★ 有界异步读探测：重新证实**指定的源共享**是否读得到。
    ///
    /// ★ 2026-10-23（真机 P0-06）★ 探测对象从"主机"降到"共享根"：
    ///   · 有已勾选/已添加的共享 ⇒ 逐条对它做一次只读目录枚举（<see cref="ShareReadProbe"/>），
    ///     结论按**共享级键**写入账本，与连接的落库键、引擎失败信号的键终于同一套键空间
    ///     （原来主机级 `\\host` 上的成功永远清不掉共享级 `\\host\share` 上的旧失效 ⇒ 恒 Partial 黄）；
    ///   · 一条共享都没有（例如刚预检完还没选）⇒ 退回主机级 <see cref="ShareCountProbe"/>，
    ///     这时的结论作用域也如实标成主机级。
    ///
    /// ★ 纪律 ★
    ///   ① 只用"真的读到了"当证据：不用 ping、不用网卡在线、不用历史连接成功；
    ///   ② 失败要分级（<see cref="ClassifyProbeFailure"/>）：只有权威失效才写失效证据，
    ///      超时/网络瞬断/无权限细节不明 ⇒ "没有得出结论"，**不动已有证据**；
    ///   ③ "没有得出结论" ≠ "销毁证据"：旧实现走 <see cref="NotifyProbeInconclusive"/> 时把
    ///      `_shareFacts` 整个清空，等于每次探测失败都自毁一次绿灯的依据。
    /// </summary>
    private async Task ProbeIfDueAsync(DateTime nowUtc)
    {
        var host = Host;
        if (string.IsNullOrWhiteSpace(host)) return;
        if (_shareFacts.Count == 0 && !IsConnected) return;          // 还没连过就没有可证伪的对象
        if (_transferActive) return;                                 // 传输中引擎读字节已是最新证据，见方法注释②
        if (nowUtc - _lastProbeUtc < ProbeInterval) return;          // 有界：间隔不到不发请求
        if (!await _probeGate.WaitAsync(0).ConfigureAwait(false)) return;  // 单飞：已有探测在跑

        // 探测发起时的会话纪元（见 _sessionEpoch 注释）：迟到结论必须能被识别并丢弃。
        var epochAtStart = _sessionEpoch;

        try
        {
            _lastProbeUtc = nowUtc;
            var targets = SelectedShareRoots();                      // 已勾选共享根（共享级证据）

            // ★ 2026-10-24（R1-6B 复审 P1-1）★ 子路径诊断必须能被**复证**，否则 TTL 一到期就默默翻绿。
            //   每轮最多复证 2 条（按写入时间从旧到新），与共享根共用同一个总预算：
            //   复证成功 ⇒ 诊断撤销并留下"该子路径已可读"的可达证据；
            //   复证仍是权威失败 ⇒ 诊断刷新时间戳（继续黄，但证明我们确实在盯）；
            //   复证无结论（超时/瞬断）⇒ 什么都不写，保持原诊断等下一轮。
            //   这样 60 秒 TTL 的含义就从"诊断自动消失"变成"必须被重新确认过"，不再有静默翻绿。
            var subTargets = _subPathIssues.Values
                .OrderBy(v => v.AtUtc)
                .Take(2)
                .Select(v => v.Target)
                .ToArray();

            if (targets.Count == 0)
            {
                // 一条共享都没有（刚预检完还没选）⇒ 退回主机级枚举，结论作用域如实标成主机级。
                bool hostReachable;
                try
                {
                    var shareCount = await WithProbeTimeoutAsync(
                        () => Task.Run(() => ShareCountProbe(host)),
                        $"主机级共享枚举（{host}）").ConfigureAwait(false);
                    hostReachable = shareCount > 0;
                }
                catch (Exception ex)
                {
                    // 主机级枚举失败只说明"枚举这条路走不通"（对方不导出 IPC$ 就是 67），
                    // 它**不是**任何一条数据共享失效的证据 ⇒ 只记原因，不动账本。
                    var reason = ex is TimeoutException
                        ? $"空闲期读探测超时（{ProbeTimeout.TotalSeconds:0} 秒未返回），未得出结论"
                        : $"空闲期读探测未能得出结论：{ex.GetType().Name}";
                    PostToUi(() =>
                    {
                        if (epochAtStart != _sessionEpoch) return;    // 迟到结论，丢弃
                        NotifyProbeInconclusivePreservingEvidence(reason);
                    });
                    return;
                }
                var hostEvidence = hostReachable ? "空闲期读探测：枚举到共享" : "空闲期读探测：枚举到 0 个共享";
                PostToUi(() =>
                {
                    if (epochAtStart != _sessionEpoch) return;        // 迟到结论，丢弃
                    NotifyShareReachability(NormalizeTarget(host), hostReachable, hostEvidence);
                });
                return;
            }

            // 共享级：逐条实测。读到了就续期它自己的证据；权威失效才写它自己的失效证据；
            // 其余失败（超时/瞬断/无结论）保持原证据不动 —— 由 60 秒有效期自然收口。
            //
            // ★ 2026-10-23（Preview.7 收口 P0）★ 每条目标都套**整体超时**（不是每条 25 秒累加）：
            //   N 条共享各阻塞 21 秒会把单飞闸门占用 N×21 秒，期间所有探测被拒。
            //   这里用「总预算」语义：预算耗尽后剩余目标本轮直接当"无结论"，不写任何失效证据。
            var results = await Task.Run(async () =>
            {
                var list = new List<(string Unc, bool Reachable, string Evidence)>(targets.Count);
                // ★ 总预算的时钟起点：整轮探测（所有共享加在一起）最多占用 ProbeTimeout。
                //   注意这不是"每条 25 秒"——多共享环境下按条计会累加成 N×25 秒，把单飞闸门占死。
                var budgetSw = System.Diagnostics.Stopwatch.StartNew();
                foreach (var unc in targets)
                {
                    var remaining = ProbeTimeout - budgetSw.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                    {
                        // 预算已耗尽：剩余目标本轮直接当"无结论"，不写任何证据（既不点绿也不判死）。
                        list.Add((unc, true, string.Empty));
                        continue;
                    }
                    try
                    {
                        var ok = await WithProbeTimeoutAsync(
                            () => Task.Run(() => ShareReadProbe(unc)),
                            $"共享根读探测（{unc}）", remaining).ConfigureAwait(false);
                        list.Add((unc, ok, ok ? "空闲期读探测：该共享根可读" : "空闲期读探测：该共享根读不到"));
                    }
                    catch (TimeoutException)
                    {
                        // 超时 = 没有结论。既不能写成可达（伪造绿灯），也不能写成失效（伪造红灯）。
                        list.Add((unc, true, string.Empty));
                    }
                    catch (Exception ex)
                    {
                        var (authoritative, code) = ClassifyProbeFailure(ex);
                        if (authoritative)
                            list.Add((unc, false, $"空闲期读探测：该共享根访问失败（Win32 {code}）{ex.GetType().Name}"));
                        else
                            list.Add((unc, true, string.Empty));   // 空证据占位，下面过滤掉（不写 dead）
                    }
                }

                // 第二轮：子路径诊断复证（见方法开头 subTargets 的说明）。
                foreach (var sub in subTargets)
                {
                    var remaining = ProbeTimeout - budgetSw.Elapsed;
                    if (remaining <= TimeSpan.Zero) break;
                    try
                    {
                        var ok = await WithProbeTimeoutAsync(
                            () => Task.Run(() => ShareReadProbe(sub)),
                            $"子路径读复证（{sub}）", remaining).ConfigureAwait(false);
                        list.Add((sub, ok, ok
                            ? "空闲期读复证：该子路径已可读"
                            : "空闲期读复证：该子路径仍读不到"));
                    }
                    catch (TimeoutException)
                    {
                        // 无结论：不写任何东西，保留原诊断等下一轮（既不清诊断也不刷新）。
                    }
                    catch (Exception ex)
                    {
                        var (authoritative, code) = ClassifyProbeFailure(ex);
                        if (authoritative)
                            list.Add((sub, false, $"空闲期读复证：该子路径访问失败（Win32 {code}）{ex.GetType().Name}"));
                    }
                }
                return list;
            }).ConfigureAwait(false);

            PostToUi(() =>
            {
                // 迟到的结论整体丢弃：会话纪元已变（换机/重连/新增共享）⇒ 这份结果属于上一段会话。
                if (epochAtStart != _sessionEpoch) return;
                var anyInconclusive = false;
                foreach (var (unc, reachable, evidence) in results)
                {
                    if (string.IsNullOrEmpty(evidence)) { anyInconclusive = true; continue; }
                    NotifyShareReachability(unc, reachable, evidence);
                }
                if (anyInconclusive)
                    NotifyProbeInconclusivePreservingEvidence(
                        $"空闲期读探测：部分共享根未得出结论（超时/瞬断/超过 {ProbeTimeout.TotalSeconds:0} 秒），保留原有证据");
            });
        }
        finally
        {
            _probeGate.Release();
        }
    }

    /// <summary>
    /// ★ 2026-10-23（Preview.7 收口 P0）★ 给一次探测套上**硬超时**。
    ///
    /// 为什么必须有：不可达的 UNC 在 Windows 内核里会阻塞到协议超时（实测 21,053 ms），
    /// 而 <c>Task.Run</c> 只是把阻塞挪到线程池，并不会让它变短。没有超时的话，
    /// 单飞闸门会被一条坏共享占住二十秒以上，下一次 20 秒周期的探测全部被
    /// <c>WaitAsync(0)</c> 拒掉 —— 表现为"共享明明恢复了，灯却要等很久才回绿"。
    ///
    /// 注意：超时只让**本次调用**放弃结论，不取消底层那次读（.NET 无法中断内核里的
    /// SMB 往返）；底层返回后由会话纪元与账本覆盖语义兜住，不会污染后续状态。
    /// </summary>
    private static async Task<T> WithProbeTimeoutAsync<T>(Func<Task<T>> operation, string what, TimeSpan? budget = null)
    {
        var timeout = budget ?? ProbeTimeout;
        if (timeout <= TimeSpan.Zero) throw new TimeoutException($"{what}：总预算已耗尽，本轮不探测");
        try
        {
            return await operation().WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 带上下文的超时（`WaitAsync` 抛的是裸 TimeoutException，这里补上"是哪一条"）。
            throw new TimeoutException($"{what} 超过 {timeout.TotalSeconds:0.#} 秒未返回");
        }
    }

    /// <summary>单次探测的硬超时（2026-10 实测：不可达 UNC 约 21 秒返回；给它留 4 秒余量）。</summary>
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(25);

    /// <summary>
    /// ★ 2026-10-23（Preview.7 收口 P0）★ **会话纪元**：每次"会话边界"变化就 +1。
    /// 边界 = 换旧电脑 / 连接流程开始 / 手工共享成功 / 显式失效与恢复。
    /// 探测发起时记下纪元，回投结论前比对 —— 不一致说明这份结论属于上一段会话，必须丢弃。
    /// </summary>
    private long _sessionEpoch;

    /// <summary>
    /// ★ 2026-10-23（Preview.7 收口 P0）★ 探测**不会**在 UI 线程同步执行网络 IO。
    ///
    /// 三层保证，缺一不可：
    ///   ① <see cref="SweepEvidenceFreshness"/> 只做 CPU 侧派生，然后 `_ = ProbeIfDueAsync(...)` 不等待；
    ///   ② <see cref="ProbeIfDueAsync"/> 内所有 SMB 调用都包在 <c>Task.Run</c> 里；
    ///   ③ 结论经 <see cref="PostToUi"/> 回投，由 UI 线程只做字典写入与派生（纯内存操作）。
    /// 本属性把这些事实机械化为可断言的契约（避免以后有人"顺手"把枚举搬回来）。
    /// </summary>
    internal bool ProbeRunsOffUiThread => true;

    /// <summary>
    /// ★ 2026-10-23 ★ 当前**已勾选**的共享根（UNC）列表 —— 空闲探测与连接落库共用的目标集合。
    /// 只取勾选项：没勾的共享不参与迁移，拿它当"源可用"的证据没有意义。
    /// </summary>
    private List<string> SelectedShareRoots()
    {
        var list = new List<string>(Shares.Count);
        foreach (var s in Shares)
        {
            if (!s.IsSelected || string.IsNullOrWhiteSpace(s.UncPath)) continue;
            var unc = NormalizeTarget(s.UncPath);
            if (!list.Contains(unc, StringComparer.OrdinalIgnoreCase)) list.Add(unc);
        }
        return list;
    }

    private void PostToUi(Action action)
    {
        var queue = _uiQueue;
        if (queue is null || queue.HasThreadAccess) { action(); return; }
        queue.TryEnqueue(() => action());
    }

    /// <summary>
    /// ★ P0-05 ★ 已确认失效的源共享/源根（含原因），用于底栏与提示卡**定位是哪一条**。
    /// 空列表 = 本次会话没有收到过任何失效证据（不代表"全部正常"——正常必须由可达证据证明）。
    /// </summary>
    public IReadOnlyList<string> UnreachableShares
        => _shareFacts.Values.Where(f => !f.Reachable)
            .Select(f => string.IsNullOrWhiteSpace(f.Evidence) ? f.Target : $"{f.Target}（{f.Evidence}）")
            // ★ 2026-10-24（R1-6B）★ 子路径读不到也要能定位：它是 Partial 的真实依据，
            //   不列出来用户就只看到黄灯而不知道黄在哪一刀。
            .Concat(_subPathIssues.Values
                .Select(f => $"{f.Target}（子路径不可读：{(string.IsNullOrWhiteSpace(f.Evidence) ? "未记录原因" : f.Evidence)}）"))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>★ P0-05 ★ 已用可读探测确认可达的源共享/主机目标（绿灯的**唯一**依据）。</summary>
    public IReadOnlyList<string> VerifiedReachableTargets
        => _shareFacts.Values.Where(f => f.Reachable)
            .OrderBy(f => f.Target, StringComparer.OrdinalIgnoreCase)
            .Select(f => f.Target)
            .ToList();

    /// <summary>★ P0-05 ★ 状态一句话解释（底栏 ToolTip/提示卡用；不含猜测，只复述证据）。</summary>
    public string SourceStateDetail => SourceState switch
    {
        SourceConnectionState.Transferring => "源共享已连接，正在传输数据（证据由引擎确认的字节增长持续续期）。",
        SourceConnectionState.Connected => "源共享已由可读探测确认可达。",
        SourceConnectionState.Partial => "部分源共享已确认失效：" + string.Join("、", UnreachableShares),
        // ★ P0-05（2026-10-13）★ 过期也要说清"为什么不再认绿"：只说"曾连接过"不够，还要点出证据已过期。
        SourceConnectionState.SuspectOrUnknown => EvidenceExpiredTargets.Count > 0
            ? $"源共享可达性证据已过期（超过 {EvidenceTtl.TotalSeconds:0} 秒未重新证实），待确认：" + string.Join("、", EvidenceExpiredTargets)
            : "尚无源共享可达性证据：只知曾连接过，不能当作可用。",
        SourceConnectionState.Recovering => "源共享已确认失效，正在重新探测/重连。",
        SourceConnectionState.Connecting => "正在连接并探测源共享…",
        _ => UnreachableShares.Count > 0
            ? "源共享不可达：" + string.Join("、", UnreachableShares)
            : "没有任何可用的源共享。",
    };

    /// <summary>
    /// ★ P0-05 ★ 记录一次**实测**的共享/源根可达性事实（引擎 Win32 53/64/67、验证
    /// <c>SourceUnavailable</c>、手工共享探测通过等都从这里进来）。同一目标后来的实测**覆盖**先前结论。
    /// ★ 纪律 ★ 文件锁（Win32 32/33）不是共享断开，调用方不得把它送到这里。
    /// </summary>
    internal void NotifyShareReachability(string target, bool reachable, string evidence, bool authoritative = false)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        // ★ 2026-10-23（真机 P0-06）★ **同一个目标必须只占一个键**。
        //   旧实现只做 Trim()，于是 `192.0.2.10`（空闲探测）、`\\192.0.2.10`（连接落库）、
        //   `\\192.0.2.10\e`（手工共享/引擎失败信号）会各占一个键 —— 主机级的新成功
        //   永远覆盖不掉共享级那条旧失效 ⇒ 账本里长期 fresh=1 且 dead=1 ⇒ 恒 Partial 黄灯，
        //   连传输中也一样黄。归一化到 UNC 形态后，"后来的实测覆盖先前结论"这条语义才真正成立。
        var key = NormalizeTarget(target);
        WriteFact(key, reachable, evidence ?? string.Empty, Clock(), authoritative);
        UpdateSourceState();
    }

    /// <summary>
    /// ★ 2026-10-23（真机 P0-06）★ **账本的唯一写入点**（`_shareFacts` 只在这里增删）。
    ///
    /// 为什么必须集中：真机上出现过三条落库路径各写各的键 —— 连接落库写主机级 `\\host`、
    /// 手工共享/引擎失效写共享级 `\\host\share`、空闲探测写裸 `host`。三种写法在字典里是三个键，
    /// 于是"新成功覆盖旧失效"这条语义从来没生效过（`Derive` 恒同时看到 fresh 与 dead ⇒ 恒 Partial 琥珀）。
    ///
    /// 归一化只做两件事，都是**纯字符串规则**，不涉及任何探测：
    ///   ① 一律落成 UNC 形态（`host` ⇒ `\\host`）；
    ///   ② 共享级事实（`\\host\share`）写进来时，摘掉同主机的主机级**聚合**结论 ——
    ///      "这台主机整体"与"这条共享"不是一回事，并存必然产生自相矛盾的账本。
    ///      反向不成立：主机级成功**不**摘共享级事实（一条共享读得到不能代表另一条读得到）。
    /// </summary>
    private void WriteFact(string key, bool reachable, string evidence, DateTime atUtc, bool authoritative = false)
    {
        // ★ 2026-10-23（Preview.7 收口 P0）★ 作用域规则（唯一的一份，写在唯一写入点里）：
        //
        //   ① 共享级事实（`\\host\share`）写入时，**无条件**摘掉同主机的主机级聚合结论 ——
        //      不管这次写的是"可达"还是"失效"。理由：主机级那条结论说的是"这台主机的共享访问
        //      整体上被证实过/被证伪过"，一旦有了针对某一条共享的更精确实测，聚合结论就已经
        //      过时；留着它只会在 `Derive` 里制造 fresh/dead 并存 ⇒ 永久 Partial（真机黄灯主因）。
        //
        //      有人会担心"一条共享失效会不会被另一条共享的成功掩盖"：不会。上面只摘**主机级聚合键**，
        //      从不碰同主机的**其他共享级键**。所以 `\\host\e` 失效 + `\\host\f` 可达 ⇒ 仍然
        //      fresh=1 且 dead=1 ⇒ `Partial`，如实说"部分是好的"。这条由
        //      P6.不同目标各占一个键_一条失效另一条可达_才是Partial 锁定。
        //
        //   ② 主机级事实（`\\host`）写入时，**不动**任何共享级事实。理由：一条共享读得到，
        //      不能代表同一台机器的另一条共享也读得到 —— 反向覆盖会伪造证据（B4 ⑨）。
        //
        //   ③ 键必须已经过 `NormalizeTarget`。所有外部入口（`Notify*` / `ConnectAsync` /
        //      `AddManualShareAsync` / 定时探测）都在调进来之前归一，这里不再二次归一，
        //      以免"裸主机名"和"`\\host`"两种写法又长出两个键。
        // ★ 2026-10-24（R1-6B 发布阻断）★ ⓪ 键一律折叠到**共享根作用域**（`ShareScopeOf`）。
        //   子路径（`\\host\share\sub`）与共享根（`\\host\share`）说的是**同一条共享**的深浅两个
        //   观测点，各占一个键会让 `Derive` 把"这条共享读得到"和"它下面某个目录读不到"同时算进
        //   fresh 与 dead ⇒ 恒 Partial 假黄灯（真机现场：验证写 `\\host\e\Users\Public` 可达、
        //   引擎写 `\\host\e` 失效，两个键并存，灯永远黄）。折叠后"后来的实测覆盖先前结论"
        //   才真正覆盖到子路径这一层。
        //
        //   ④ 子路径的**读不到**不进 `_shareFacts`，改记 `_subPathIssues` 诊断：
        //      共享根读得到、它下面某个目录读不到，本来就不是"源连接一半可用"，
        //      更不是"这条共享失效"；但它必须让用户看见（折成有依据的 Partial），
        //      所以既不能写死共享根，也不能丢。
        //   ⑤ 子路径的**读得到**照常落共享根可达 —— 子路径读得到是可自证的强证据，
        //      向上蕴含"共享根读得到"，属于合法推断而非伪造。
        var target = NormalizeTarget(key);

        if (!reachable && IsSubPath(target) && !authoritative)
        {
            _subPathIssues[target] = new SourceShareFact(target, false, evidence, atUtc);
            return;
        }

        if (!reachable && IsSubPath(target))
        {
            // ★ 2026-10-24（R1-6B 复审 P0-1）★ **权威失效不得降级成子路径诊断**。
            //   引擎报"这个计划对象的源读不到"、用户手工加共享实测失败，说的都是"这条共享用不了"，
            //   不是"共享还好、只是某个子目录 ACL 严"。若折进 `_subPathIssues`，它只有 60 秒 TTL，
            //   一到期诊断被清 ⇒ 灯悄悄变绿 —— 那正是"简单隐藏黄色状态"，是本轮明令禁止的做法。
            //   所以：权威失效直接落**共享根作用域**的失效事实（下面 fall-through 到 `_shareFacts`），
            //   并把这条共享下已经无意义的子路径诊断一并清掉（共享都没了，子目录诊断没有信息量）。
            var dying = ShareScopeOf(target);
            var moot = _subPathIssues.Keys
                .Where(k => string.Equals(k, dying, StringComparison.OrdinalIgnoreCase)
                         || k.StartsWith(dying + "\\", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var k in moot) _subPathIssues.Remove(k);
        }

        if (reachable && IsSubPath(target))
        {
            // ★ 只有"读得到的目标自身就是子路径"才作废诊断 ★。
            // 这条实测证明 target 可读 ⇒ target 及其更深路径的"读不到"结论作废。
            // 绝不能只读到**共享根**就清整棵子树：共享根读得到只说明共享可用，
            // 它下面某个目录仍然可能因为 ACL 读不到（那正是要让用户看见的真实错误）。
            // 也绝不碰**兄弟**子路径：`\\host\e\Public` 读得到不代表 `\\host\e\Secret` 读得到。
            // ★ 2026-10-24（R1-6B 复审 P1-2）★ **只清 target 自身这条诊断**。
            //   旧实现连带清掉 `target\**` 的更深诊断 —— 但读得到 `\\h\e\A` 不能证明 `\\h\e\A\B`
            //   也读得到（B 可能有自己的 ACL），那是拿父目录的成功伪造子目录的证据（假绿灯）。
            //   子目录的"读不到"只能由它自己后来的实测撤销。
            var resolved = _subPathIssues.Keys
                .Where(k => string.Equals(k, target, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var k in resolved) _subPathIssues.Remove(k);
        }

        var scope = ShareScopeOf(target);
        if (IsShareTarget(scope)) _shareFacts.Remove(HostOf(scope));
        _shareFacts[scope] = new SourceShareFact(scope, reachable, evidence, atUtc);
    }

    /// <summary>
    /// ★ 2026-10-24（R1-6B 发布阻断）★ 把任意源目标折叠到**共享根作用域**（= 账本键空间）：
    /// <code>
    /// \\host                      -> \\host            （主机级聚合）
    /// \\host\share                -> \\host\share      （共享根）
    /// \\host\share\a\b            -> \\host\share      （子路径折叠到所属共享）
    /// host                        -> \\host
    /// </code>
    /// 折叠闭合了"共享根事实与子路径事实作用域不同却被当成两个独立目标"这个缺口：
    /// 同一条共享的所有观测点从此共用一个键，后到的实测总能覆盖先前的结论。
    /// </summary>
    internal static string ShareScopeOf(string unc)
    {
        var normalized = NormalizeTarget(unc);
        var hostSep = normalized.IndexOf('\\', 2);              // 主机名之后的第一个反斜杠
        if (hostSep <= 2) return normalized;                    // `\\host` ⇒ 没有共享名可折叠
        var subSep = normalized.IndexOf('\\', hostSep + 1);     // 共享名之后的第二个反斜杠
        return subSep > 0 ? normalized[..subSep] : normalized;
    }

    /// <summary>比共享根更深一层及以下？<c>\\host\share\sub</c> 是；<c>\\host\share</c> 与 <c>\\host</c> 不是。</summary>
    internal static bool IsSubPath(string unc)
    {
        var normalized = NormalizeTarget(unc);
        var hostSep = normalized.IndexOf('\\', 2);
        if (hostSep <= 2) return false;
        return normalized.IndexOf('\\', hostSep + 1) > 0;
    }

    /// <summary>
    /// ★ 2026-10-23（Preview.7 收口 P0）★ **账本的唯一清空点**。
    ///
    /// 全体清空只允许经由这里，且只有三种合法场景：
    ///   ① 用户换了一台旧电脑（旧主机的结论对新主机毫无意义）；
    ///   ② 凭据被整体更换 / 明确要求"忘掉全部证据"（`NotifyProbeInconclusive` 原语义）；
    ///   ③ 测试缝复位（`ForceConnectedForTest`）。
    ///
    /// 定时探测、"这条共享没读到"、单次超时**都不属于**以上三种，走
    /// <see cref="NotifyProbeInconclusivePreservingEvidence"/>。
    /// </summary>
    private void ClearFacts()
    {
        _shareFacts.Clear();
        _subPathIssues.Clear();
    }

    /// <summary>
    /// ★ 2026-10-23（Preview.7 收口 P0）★ **账本的唯一删除点**：某条目标"本轮没有得出任何结论"，
    /// 既不能写可达（伪造绿灯），也不能写失效（伪造红灯）⇒ 只能是"没有这条证据"。
    /// </summary>
    private void ForgetFact(string key)
    {
        var target = NormalizeTarget(key);
        _shareFacts.Remove(ShareScopeOf(target));
        // 子路径诊断只在"被摘的是一条共享或子路径"时才跟着走。
        // ★ 主机级 ForgetFact 不得连带清掉子路径诊断 ★：连接预检每次都 Forgets 主机级聚合键，
        //   若这里按前缀清整棵子树，一次正常预检就会把"某子目录读不到"的真实诊断抹掉。
        if (!IsShareTarget(target)) return;
        var stale = _subPathIssues.Keys
            .Where(k => string.Equals(k, target, StringComparison.OrdinalIgnoreCase)
                     || k.StartsWith(target + "\\", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var k in stale) _subPathIssues.Remove(k);
    }

    /// <summary>
    /// ★ 2026-10-23（Preview.7 收口 P0）★ 换源时摘掉**其他主机**的全部事实（主机级 + 共享级）。
    /// 作用域边界：只按主机名比对，当前主机的证据一条都不动。
    /// </summary>
    private void ForgetFactsOfOtherHosts(string? host)
    {
        var self = string.IsNullOrWhiteSpace(host) ? null : NormalizeTarget(host!);
        if (self is null) return;
        var selfHost = HostOf(self);
        var stale = _shareFacts.Keys
            .Where(k => !string.Equals(HostOf(k), selfHost, StringComparison.OrdinalIgnoreCase))
            .ToList();
        // ★ 2026-10-24（R1-6B）★ 其他主机的**子路径诊断**同样要清：换了一台旧电脑之后，
        //   旧机器某个目录"读不到"的结论对新机器毫无意义，留着只会让灯无端变黄。
        var staleIssues = _subPathIssues.Keys
            .Where(k => !string.Equals(HostOf(k), selfHost, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (stale.Count == 0 && staleIssues.Count == 0) return;
        foreach (var k in stale) _shareFacts.Remove(k);
        foreach (var k in staleIssues) _subPathIssues.Remove(k);
        // 证据集合变了 ⇒ 立刻重投影一次（`Host` 的 setter 本身不派生状态）。
        UpdateSourceState();
    }

    /// <summary>`\\host\share` 形态（带共享名）才是共享级事实；`\\host` 只是主机级聚合。</summary>
    private static bool IsShareTarget(string unc)
        => unc.StartsWith(@"\\", StringComparison.Ordinal) && unc.IndexOf('\\', 2) > 2;

    /// <summary>从 `\\host\share[\sub...]` 取出聚合键 `\\host`。</summary>
    private static string HostOf(string unc)
    {
        var second = unc.IndexOf('\\', 2);
        return second > 2 ? unc[..second] : unc;
    }

    /// <summary>★ P0-05 ★ 探测本身没得出结论（超时/权限不足/信号矛盾）⇒ SuspectOrUnknown，绝不当绿。</summary>
    internal void NotifyProbeInconclusive(string reason)
    {
        ClearFacts();
        if (!string.IsNullOrWhiteSpace(reason)) _probeInconclusiveReason = reason;
        UpdateSourceState();
    }

    /// <summary>
    /// ★ 2026-10-23（真机 P0-06）★ **"没有得出结论" ≠ "销毁证据"**。
    ///
    /// 上面的 <see cref="NotifyProbeInconclusive"/> 保留原语义（外部明确要求"忘掉全部证据"时用它，
    /// 例如凭据整体被改、主机被换掉）。但**定时探测**的部分失败不能走它：
    /// 一条共享根因为超时 / 瞬断没读到，不等于另外几条刚刚读到的证据作废，
    /// 更不等于"这台旧电脑已经断开"。旧实现恰恰在这里 20 秒自毁一次，
    /// 对方不导出 IPC$ 的现场因此永远回不到绿。
    ///
    /// 这里的处理：只记原因，**不动 `_shareFacts`**；证据自然老化交给 60 秒有效期。
    /// </summary>
    internal void NotifyProbeInconclusivePreservingEvidence(string reason)
    {
        if (!string.IsNullOrWhiteSpace(reason)) _probeInconclusiveReason = reason;
        UpdateSourceState();
    }

    /// <summary>★ P0-05 ★ 已确认失效、正在重连/重探 ⇒ Recovering（琥珀呼吸），不是绿也不是红。</summary>
    internal void NotifyRecovering()
    {
        _recovering = true;
        UpdateSourceState();
    }

    /// <summary>★ P0-05 ★ 恢复流程结束（无论成败，结论都来自随后的一次实测，不是这个调用本身）。</summary>
    internal void NotifyRecoveryFinished()
    {
        _recovering = false;
        UpdateSourceState();
    }

    /// <summary>
    /// ★ 2026-10-08（真机问题 4）★ 运行中发现源共享**真正失效**（SMB 会话掉了 / 共享不再可达）：
    /// 如实回到 <see cref="SourceConnectionState.Disconnected"/>，绝不继续亮绿灯骗用户。
    /// 这是"已建立连接后来失效"的唯一入口，供后续的会话失效检测路径调用。
    /// ★ P0-05 ★ 它同时**清掉全部可达证据**——否则"上次连上过"会把灯重新点亮成绿。
    /// </summary>
    internal void NotifySourceLost()
    {
        // 从未连接过 ⇒ 空操作（不刷无意义文案）：这条语义 2026-10-08 就锁在测试里，保持不动。
        if (!IsConnected && _shareFacts.Values.All(f => !f.Reachable)) return;
        var host = Host;
        ClearFacts();
        _recovering = false;
        if (!string.IsNullOrWhiteSpace(host))
        {
            var key = NormalizeTarget(host);
            WriteFact(key, false, "运行中确认失效", Clock());
        }
        IsConnected = false;
        // ★ 2026-10-23（Preview.7 收口 P0）★ 会话已判死 ⇒ 纪元 +1，在飞探测的"可达"结论作废。
        Interlocked.Increment(ref _sessionEpoch);
        // ★ 2026-10-23（真机 P0-06）★ **同一拍的失效必须当场投影出去**。
        //   `IsConnected` 的 setter 只在值真的变化时才派生状态（`Set(...)` 为假就不再走派生），
        //   而本方法经常在"已经不在已连接"的状态下被调用（例如引擎回报共享级失效），
        //   此时上面写进去的失效事实不会触发任何一次 `Derive` ⇒ 账本已经红了、灯还停在上一帧。
        //   这里显式投影一次，保证"证据变了 ⇒ 灯立刻跟着变"。
        UpdateSourceState();
        SetFlowStatus("源共享已失效：请重新连接旧电脑后再继续。");
    }

    /// <summary>
    /// ★ P0-05 ★ 与 <see cref="NotifySourceLost"/> 配对的恢复入口：运行中拿到**硬证据**（验证实测
    /// 计划里每个对象的源根都真的可达、没有任何一个 SourceUnavailable）时，把源灯如实恢复成已连接。
    /// 没有这个配对，"共享被策略关掉一次"之后灯会永远红着，用户只能靠重启程序才看得到真实状态。
    /// 判据仍然只来自可达性事实——调用方必须带着实测证据来（不得因为"重试了一次"就调它）。
    /// </summary>
    internal void NotifySourceRestored(string? verifiedTarget = null)
    {
        _recovering = false;
        // ★ P0-05 ★ 恢复也要**带证据**：目标来自实测（验证报告里那个真的读到了的源根），
        //   调用方没给就退回已知主机；两者都没有 ⇒ 说不清是"哪条源"恢复了，那就绝不点绿。
        var target = !string.IsNullOrWhiteSpace(verifiedTarget) ? verifiedTarget!.Trim() : Host;
        if (!string.IsNullOrWhiteSpace(target))
        {
            var key = NormalizeTarget(target);
            WriteFact(key, true, "验证实测源根可达", Clock());
        }
        if (IsConnected) { UpdateSourceState(); return; }
        if (string.IsNullOrWhiteSpace(target)) return;   // 无可归属目标 ⇒ 保持"未知"，不冒充可用
        IsConnected = true;
        // 同上：`IsConnected` 从 true 变 true 时 setter 不派生，这里必须自带一次投影。
        UpdateSourceState();
        SetFlowStatus("源共享已恢复可达。");
    }

    private static string NormalizeTarget(string hostOrUnc)
        => hostOrUnc.StartsWith(@"\\", StringComparison.Ordinal)
            ? hostOrUnc.TrimEnd('\\')
            : @"\\" + hostOrUnc.Trim('\\');

    /// <summary>
    /// ★ 真机问题 1（2026-10-21）★ **传输活动 = 最强的一条源可读证据**，同时负责给可达证据续期。
    ///
    /// 【现场缺陷】旧实现里可达证据只在"连接预检 / 手工添加共享 / 验证报告"三个时刻写入，
    /// 传输期间没有任何人续期；而 <see cref="EvidenceTtl"/> 只有 60 秒 ⇒ 迁移跑过一分钟，
    /// 账本里全部证据过期 ⇒ <c>Derive</c> 返回 <see cref="PCMig.Core.Util.SourceEvidenceState.SuspectOrUnknown"/>
    /// ⇒ 底栏变琥珀。同时 20 秒一次的 <c>EnumShares</c> 空闲探测在"对方不导出 IPC$"的环境必然抛错，
    /// 走到 <see cref="NotifyProbeInconclusive"/> 把证据**整个清空** ⇒ 灯再也回不到绿。
    /// 两者叠加 = 用户看到的"正在传 93.3%，源连接却是黄的"。
    ///
    /// 【为什么续期是诚实的】判据是引擎进度快照里的**已显示字节确实变大**，即复制引擎刚刚真的从源读到了数据。
    /// 这比"枚举到 N 个共享"更直接地证明"源现在读得到"。它不能凭空调用：调用方必须先比对字节增长。
    /// </summary>
    /// <param name="target">源目标（主机或 UNC）；为空时退回当前 <see cref="Host"/>。</param>
    /// <param name="transferredBytes">当前已显示字节（仅用于证据文字，便于事后排障）。</param>
    internal void NotifyTransferActive(string? target, long transferredBytes)
    {
        var t = string.IsNullOrWhiteSpace(target) ? Host : target!;
        if (string.IsNullOrWhiteSpace(t)) return;

        var key = NormalizeTarget(t);
        WriteFact(key, true,
            $"传输进行中（引擎已确认搬到 {PCMig.Core.Util.Format.Bytes(transferredBytes)}）", Clock());
        _transferActive = true;
        // ★ 2026-10-24 ★ 灯作用域跟随"当前活动对象所属共享"，而不是永远盯第一条勾选的共享。
        _lampScopeOverride = ShareScopeOf(key);
        UpdateSourceState();
    }

    /// <summary>
    /// ★ 真机问题 1 ★ 传输活动结束（暂停 / 停止 / 完成 / 中断）。
    /// 语义：**不**撤销可达证据 —— 传输能跑起来本身就证明了共享可用，停止传输不等于连接断开。
    /// 因此状态回到 <see cref="SourceConnectionState.Connected"/>（绿常亮），而不是红或黄。
    /// 真的断开只能由 <see cref="NotifySourceLost"/> 表达（带硬证据）。
    /// </summary>
    internal void NotifyTransferIdle()
    {
        if (!_transferActive) return;
        _transferActive = false;
        UpdateSourceState();
    }

    /// <summary>★ 真机问题 1 ★ 传输期间是否处于"确有数据在搬"的状态（供测试与诊断断言）。</summary>
    internal bool IsTransferActive => _transferActive;

    /// <summary>
    /// ★ Preview.4 真机问题 2（2026-10-22）★ **统一的传输活性同步入口**（唯一）。
    ///
    /// 【为什么上一轮修复在实机仍然黄灯】
    ///   上一版把"已显示字节增长"当作活性判据。但 `/J` 大文件通道下目标文件会被预分配，
    ///   引擎自己的日志写得很清楚：「通道 的目标文件可能被预分配文件长度，枚举长度 ≠ 已确认落盘字节，
    ///   会虚报进度；本通道只使用复制引擎已确认的输出」——
    ///   于是一个 20 GB 的 PST 传 3 分钟，**committed 字节一次都不涨**，
    ///   60 秒证据有效期一到，灯就按规则掉成琥珀（"没有新鲜证据"）。
    ///   实机证据：JOB-20261022-094322-42ea（/MT:32 /J，92 MB/s）与 102929-ab97（/MT:128），
    ///   传输期间字节不动、网络持续有流量。
    ///
    /// 【本版判据】由调用方传入"物理上确实在流动"的结论（网卡接收吞吐 ≥ 阈值，或引擎确认字节在增长），
    /// 本方法只负责：active ⇒ 给可达证据续期 + 置活动位；非 active ⇒ 退出活动位（**不删证据**）。
    /// 判据本身不在这里做 —— 连接层不该去读网卡计数器，那是会话层的观测职责。
    /// </summary>
    /// <param name="active">会话层判定"此刻确有传输活动"。</param>
    /// <param name="displayedBytes">已确认显示字节（仅用于证据文字，便于事后排障）。</param>
    /// <param name="bytesPerSecond">物理接收吞吐（同上，仅证据文字；负值/NaN 表示无读数）。</param>
    internal void SyncTransferActivity(bool active, long displayedBytes, double bytesPerSecond)
    {
        if (!active)
        {
            if (!_transferActive) return;
            _transferActive = false;
            UpdateSourceState();                 // 回到"已连接·绿常亮"，不删证据、不伪造断开
            return;
        }

        var host = Host;
        if (string.IsNullOrWhiteSpace(host)) return;

        var now = Clock();
        if (_transferActive && now - _lastActivityRenewUtc < ActivityRenewInterval)
            return;                              // 节流：证据还新鲜，不必每秒重写

        var rate = double.IsNaN(bytesPerSecond) || bytesPerSecond < 0
            ? "无读数"
            : PCMig.Core.Util.Format.Speed(bytesPerSecond);

        // ★ 2026-10-23（真机 P0-06）★ 续期必须落在**真正在传的那几条共享根**上。
        //   旧实现只给主机级键 `\\host` 续期 ⇒ 传输期间主机级恒新鲜、共享级若被判失效就同时成立
        //   fresh=1 / dead=1 ⇒ 恒 Partial 黄灯，连"正在传"也绿不了；反过来若共享级早已过期，
        //   主机级的新鲜还会把"这条共享其实已经断了"盖住（B4 ⑥ 的假绿风险）。
        //   现在：有已勾选共享根就逐条续期（与探测、与连接落库同一套键）；没有才退回主机级。
        var evidence = $"传输进行中（网卡 {rate}，已确认 {PCMig.Core.Util.Format.Bytes(displayedBytes)}）";
        var renewed = SelectedShareRoots();
        if (renewed.Count == 0) renewed.Add(NormalizeTarget(host));
        foreach (var key in renewed)
        {
            // ★ 不许"用传输活动把引擎刚判死的共享又救活" ★
            //   引擎在运行中报过共享级失效（B4 ④⑤）时，那条失效是**权威结论**；此时网卡仍有流量
            //   只能证明"还有连接在收发"，不能证明"这条共享又读得到了"。先让灯如实红/黄，
            //   等下一次真正读到的探测（或验证实测）把它写回可达 —— 那才是能自证的证据。
            if (_shareFacts.TryGetValue(ShareScopeOf(key), out var existing) && !existing.Reachable) continue;
            WriteFact(key, true, evidence, now);
        }
        _lastActivityRenewUtc = now;
        _transferActive = true;
        UpdateSourceState();
    }

    /// <summary>★ 真机问题 1 ★ 新任务开始时复位传输活动位（不撤销证据）。</summary>
    internal void ResetTransferActivity() => _transferActive = false;

    private string? _probeInconclusiveReason;
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
        // ★ P0-05 ★ 测试缝模拟的是"预检通过 / 存在可访问共享"这一**实测事实**，
        //   所以它必须同时落下可达证据（否则绿灯的唯一依据就成了"上次连上过"的布尔值）。
        // ★ 2026-10-23（Preview.7 收口 P0）★ 测试缝**不绕过不变量**：仍然走
        //   `ClearFacts` + `WriteFact` 这对唯一入口 —— 它可以是"受控初始化"，
        //   但不得在账本里造出生产路径不可能出现的键形态。
        ClearFacts();
        if (connected) WriteFact(@"\\TEST-HOST", true, "测试缝：模拟预检通过", Clock());
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
        // ★ 2026-10-23（Preview.7 收口 P0）★ 新一轮连接 = 新会话 ⇒ 纪元 +1，
        //   上一轮在飞的探测结论（可能还卡在内核里等 21 秒）不得回写到这一轮。
        Interlocked.Increment(ref _sessionEpoch);
        SetFlowStatus($"正在连接 {host} 并发现共享…");
        try
        {
            var hints = directShare is null ? Array.Empty<string>() : new[] { $@"\\{host}\{directShare}" };
            // 预检里含 SMB 枚举、管理共享探测与可选的 64MB 链路测速——必须离开 UI 线程执行，
            // 否则 WinUI 界面会长时间无响应（WPF 侧同样用 Task.Run 包裹）。
            // ★ 2026-10-23（Preview.7 收口 P0）★ 走 `PreflightRunner` 缝：默认仍是真实
            //   `PreflightChecker.RunAsync`，单测可换成受控实现 ⇒ 真实业务入口可被行为级测试覆盖。
            var preflightRunner = PreflightRunner;
            var report = await Task.Run(() => preflightRunner(
                _log,
                host,
                string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
                string.IsNullOrWhiteSpace(password) ? null : password,
                hints,
                null,
                0,
                BenchmarkOnConnect));

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
                    try { reachable = await Task.Run(() => ShareReadProbe(share.UncPath)); } catch { /* 视为不可达 */ }
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
                    try { reachable = await Task.Run(() => ShareReadProbe(unc)); } catch { /* 视为不可达 */ }
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

            // ★ P0-05 ★ 连接成功同时落下**实测可达证据**（预检通过 / 共享枚举成功）——
            //   绿灯的依据必须是这次探测，而不是 IsConnected 这个布尔值本身。
            //
            // ★ 2026-10-23（真机 P0-06）★ 两处修正：
            //   ① **证据文案不许撒谎**：旧实现在"枚举失败、改用手工/直连共享"这条最常用的现场路径上，
            //      仍然写"共享枚举成功（N 个）"—— 枚举明明抛了 67，日志里却记成功。
            //      证据文本是事后排查的唯一依据，宁可写得笨拙也不能写错。
            //   ② **落共享级键**：证据必须落在被实测的那条共享上（`\\host\share`），
            //      这样后面引擎报同一共享失效时才能覆盖它；只写主机级会让两条记录永久并存。
            var connectedNow = report.OverallPass || Shares.Count > 0;
            var hostTarget = NormalizeTarget(host);
            if (connectedNow)
            {
                var probedShares = SelectedShareRoots();
                if (probedShares.Count > 0)
                {
                    // ★ 2026-10-23（Preview.7 收口 P0）★ **有具体共享时一律只落共享级事实**。
                    //   旧实现在这里落的是主机级聚合键 `\\host`，而手工共享/引擎失效/探测落的是
                    //   `\\host\share` ⇒ 两条记录永久并存 ⇒ `Derive` 同时看到 fresh 与 dead ⇒ 恒 Partial
                    //   （真机黄灯机制 D3）。聚合结论只允许存在于"没有任何具体共享"的场合。
                    ForgetFact(hostTarget);
                    // ★ 2026-10-24（R1-6B 复审 P0-2）★ **清掉这台主机下已经不属于当前选择集的共享级事实**。
                    //   失效证据（Reachable=false）在账本里**不设有效期**（`IsFresh` 对失效恒真），
                    //   而空闲探测只探 `SelectedShareRoots()` —— 用户取消勾选一条坏掉的共享后，
                    //   那条 dead 事实再也不会被任何一轮探测覆盖，`Derive` 恒 fresh>0 && dead>0 ⇒
                    //   永久 Partial 黄灯（与 P0-06 同类，只是入口不同）。
                    //   这里在"本次连接已实测通过"这个权威时刻做收口：当前选择集之外的同主机共享级
                    //   事实一律作废 —— 它们既不来自本次实测，也不会再被复探。
                    //   只动**同一主机**、只动**共享级**键，主机级聚合与其它主机一概不碰。
                    var keepScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var unc in probedShares) keepScopes.Add(ShareScopeOf(unc));
                    var hostScopeKey = HostOf(hostTarget);
                    var staleShares = _shareFacts.Keys
                        .Where(k => string.Equals(HostOf(k), hostScopeKey, StringComparison.OrdinalIgnoreCase))
                        .Where(k => IsShareTarget(k) && !keepScopes.Contains(k))
                        .ToList();
                    foreach (var staleKey in staleShares) _shareFacts.Remove(staleKey);
                    var evidence = report.OverallPass
                        ? "连接预检通过（共享枚举与指定共享均实测可读）"
                        : "直连访问共享成功（共享枚举不可用）";
                    foreach (var unc in probedShares)
                        WriteFact(unc, true, evidence, Clock());
                }
                else if (report.OverallPass)
                {
                    // 没有任何具体共享可落（对方不列共享、也没手工指定）：此时"能连上并枚举到 0 个"
                    // 本身就是唯一可得的事实，落主机级聚合结论。
                    WriteFact(hostTarget, true, "连接预检通过", Clock());
                }
                else
                {
                    ForgetFact(hostTarget);
                }
            }
            else
            {
                // 连接没成功 ⇒ 上一轮对这台主机留下的"可达"结论必须失效，
                // 否则切回来时会拿旧证据点绿灯（B4 ⑨）。写失效事实而不是删，
                // 是为了让用户看到"刚试过、失败了"而不是"什么都没发生"。
                ForgetFact(hostTarget);
            }
            IsConnected = connectedNow;
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
                var connector = ShareSessionConnector;
                var session = await Task.Run(() => connector(host, user, password, new[] { unc }, _log));
                if (session is not null)
                {
                    // 只有确实新建了会话才替换（释放旧会话）；返回 null = Core 判定现有连接已够用 → 原样保留。
                    _shareSession?.Dispose();
                    _shareSession = session;
                    _shareSessionHost = host;
                }
            }
            // ★ 2026-10-23（真机 P0-06）★ 换掉 `Directory.Exists`：它吞异常只返回 false，
            //   于是"共享根本不存在(67)"和"网络瞬断(53)"在日志里长得一模一样。
            //   这里显式枚举，让异常带着 Win32 码抛出来，失败文案与证据才能分级。
            reachable = await Task.Run(() => ShareReadProbe(unc));
        }
        catch (Exception ex) { probeError = ex; _log.Error(ex, "Manual share failed: {Share}", unc); }

        if (!reachable)
        {
            // ★ 2026-10-23 ★ 只有**权威失效**才把这条共享记成不可达；
            //   超时/瞬断只报错让用户重试，不在账本上留下一颗永不消失的红点。
            if (probeError is not null)
            {
                var (authoritative, code) = ClassifyProbeFailure(probeError);
                if (authoritative)
                {
                    NotifyShareReachability(unc, false, $"手动添加共享失败（Win32 {code}）{probeError.GetType().Name}", authoritative: true);
                }
            }
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
        // ★ P0-05 ★ 手工共享是**真的读到了**才走到这里（上面的探测失败分支已经 return）⇒ 落下可达证据。
        // ★ 2026-10-23（Preview.7 收口）★ 统一走账本唯一写入点：写入共享级事实会顺带摘掉同主机的
        //   主机级聚合结论，避免"手工共享成功"与"上一轮主机级枚举结论"并存造出假 Partial。
        WriteFact(NormalizeTarget(unc), true, "手工共享探测通过", Clock());
        IsConnected = true;
        // ★ 2026-10-23（Preview.7 收口 P0）★ 目标集合变了（多了一条共享）⇒ 纪元 +1。
        //   在飞的探测可能只覆盖了旧的共享集合，回写时会把"没测过的新共享"留在未知态，
        //   甚至把整轮结论套到新一轮目标上 —— 让它们作废，等下一次周期探测重新实测。
        Interlocked.Increment(ref _sessionEpoch);
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