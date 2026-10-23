using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.UI.Dispatching;
using PCMig.Core.Jobs;
using PCMig.Core.Logging;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Native;
using PCMig.Core.Network;
using PCMig.Core.Planning;
using PCMig.Core.Preflight;
using PCMig.Core.Report;
using PCMig.Core.Scan;
using PCMig.Core.Transfer;
using PCMig.Core.Util;
using PCMig.Core.Diagnostics;
using PCMig.Core.Verify;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using Serilog;

namespace PCMig.WinUI.Presentation;

/// <summary>Step3 对象清单行：ObjectId + 状态文案（状态由 Receipt / 进度快照驱动）。</summary>
public sealed class SessionObjectRow : ObservableObject
{
    public SessionObjectRow(string objectId, string sourcePath, string sizeText, string? targetPath = null)
    {
        ObjectId = objectId;
        SourcePath = sourcePath;
        SizeText = sizeText;
        TargetPath = targetPath ?? "";
    }

    public string ObjectId { get; }
    public string SourcePath { get; }
    public string SizeText { get; }

    /// <summary>
    /// ★ P1-07(2) ★ 该对象的最终目标绝对路径 —— 直接取 plan.json 里那条**映射快照**的原值，不在这里重算。
    /// 计划生成之后就把「源 → 目标」摆给用户看：多嵌一层同名目录 / 是否内容直达，在开始复制**之前**就可辨认，
    /// 而不是等目标盘上真的长出多余目录才发现。空串 = 计划没给目标路径（不臆造）。
    /// </summary>
    public string TargetPath { get; }

    private string _statusText = "待传输";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
}

/// <summary>失败 / 不一致清单行（Step3 左栏「提示」面板 + Step4「报告清单」用）。</summary>
public sealed class SessionFailItem
{
    public SessionFailItem(string title, string detail, bool objectLevel = false, string? fullText = null)
    {
        Title = title;
        Detail = detail;
        ObjectLevel = objectLevel;
        FullText = fullText;
        CreatedAt = DateTime.Now;   // ★ 真实产生时刻（Step4 报告清单「时间」列的唯一来源，不摆示例时间）
    }

    public string Title { get; }
    public string Detail { get; }
    /// <summary>true = 对象级失败（必须逐条红色列出：对象号 + 路径 + 退出码译文）。</summary>
    public bool ObjectLevel { get; }
    public string DisplayText => $"{Title}｜{Detail}";

    /// <summary>
    /// 该条失败的**完整原因**（未截断；含回执原文、退出码位含义、robocopy 日志证据摘要与日志/报告路径）。
    /// 列表里显示的 <see cref="Detail"/> 是 <c>ErrorTranslator.ShortReason</c> 的 100 字符截断版，
    /// 真机教训（v0.5.3 Preview.2）：150.7 GB 那次失败的真因（目录级失败，只在汇总表里）在列表里根本读不出来。
    /// null = 没有更多可看的内容（提示级条目）。
    /// </summary>
    public string? FullText { get; }

    /// <summary>Step4 报告清单是否为这一行显示「查看完整原因」（没有 <see cref="FullText"/> 就不显示，避免空按钮）。</summary>
    public bool CanShowDetail => !string.IsNullOrWhiteSpace(FullText);

    /// <summary>本条失败/不一致项**真实产生**的时刻（本地时区；页面只显示，不参与任何业务判定）。</summary>
    public DateTime CreatedAt { get; }

    /// <summary>Step4 报告清单「时间」列文案（真实时刻，格式与日志一致）。</summary>
    public string TimeText => CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>非对象级 = 提示级（Step4 报告清单圆点：对象级用危险色，提示级用警告色）。</summary>
    public bool IsNotice => !ObjectLevel;
}

/// <summary>
/// Step4「实时日志」面板的一行：级别 + 真实文本 + 真实时刻。
/// 数据来源只有两处，都是真实事件：① Core 引擎的每行输出；② 本次会话的真实动作结论（验证/修复/报告/载入…）。
/// **不允许**在本类型里出现任何示例/占位文案。
/// </summary>
public sealed class SessionLogLine
{
    public SessionLogLine(string level, string text)
    {
        Level = level;
        Text = text;
        At = DateTime.Now;
    }

    /// <summary>日志级别（"INFO" / "WARN" / "ERROR"）——决定 DataTemplate 里显示哪一枚前缀标签。</summary>
    public string Level { get; }
    public string Text { get; }
    public DateTime At { get; }
    public string TimeText => At.ToString("HH:mm:ss");

    public bool IsInfo => string.Equals(Level, "INFO", StringComparison.Ordinal);
    public bool IsWarn => string.Equals(Level, "WARN", StringComparison.Ordinal);
    public bool IsError => string.Equals(Level, "ERROR", StringComparison.Ordinal);
}

/// <summary>验证前置闸门的判定结果（阶段 A 硬约束：这些规则必须在界面上拦住人）。</summary>
/// <param name="CanVerify">true 才允许调用 Core 的 Verifier。</param>
/// <param name="Reason">不允许时的明确原因（直接可显示给用户）。</param>
/// <param name="SourceReachable">源根目录是否实测可达（false 也是拒绝原因之一）。</param>
public sealed record VerifyGateResult(bool CanVerify, string Reason, bool SourceReachable = false)
{
    public static VerifyGateResult Pass { get; } = new(true, "", true);
}

/// <summary>
/// 传输引擎的事件钩子：把本层的三个投影回调交给注入方（仅测试缝使用）。
/// 语义与 Core 传输编排器暴露的三个事件一一对应（行输出 / 每文件复制 / 引擎提示）。
/// 生产路径不经过本类型 —— 默认直接用 Core 引擎并直接订阅其事件。
/// </summary>
public sealed record TransferEngineHooks(
    Action<string>? OutputLine,
    Action<string, long>? FileCopied,
    Action<string>? Notice);

/// <summary>
/// 传输执行**测试缝**的委托签名。
///
/// 契约现状（2026-09-28 主控修正后）：
///   <c>tests\PCMig.Core.Tests\WinUiDpiContractTests.Shell_StaysFreeOfSecondEngine</c> 已改为
///   「禁止 PCMig.Gui / RobocopyRunner，且禁止在 WinUI 里**定义**任何 *Orchestrator/*Runner 类型」，
///   也就是**允许使用 Core 的唯一引擎**、只禁"复制第二套引擎"。
///   因此本委托**不再是生产必需的间接层**：生产默认路径直接构造 Core 引擎，
///   本委托只保留为单测注入假实现的缝（见 MigrationSessionViewModel.TransferRunner）。
/// 参数/返回值与 Core 传输引擎的运行入口逐项对齐（含 onlyObjectIds / forceRecopy 语义）。
/// </summary>
public delegate Task<JobPhase> TransferRunnerDelegate(
    JobContext ctx,
    TransferEngineHooks hooks,
    IProgress<ProgressSnapshot> progress,
    CancellationToken ct,
    IReadOnlyCollection<string>? onlyObjectIds,
    bool forceRecopy);

/// <summary>
/// 迁移会话的**唯一状态源**（Step1 连接之外的全部业务状态）。
///
/// 职责边界（与既有结构的关系）：
///     Step1 连接/共享  →  既有的 <see cref="ConnectionViewModel"/>（本类只投影它的 Host/Username/IsConnected，不重造连接逻辑）
///     Step2/3/4 迁移   →  **本类**（唯一状态源）→ 装配到 PageReadiness 与各页面
///     迁移引擎        →  PCMig.Core（JobManager / PreflightChecker / SourceScanner / Planner /
///                        TransferOrchestrator / Verifier / ReportGenerator），本类不重写任何迁移规则
///
/// 线程纪律（硬约束）：
///   · 一切跨线程 UI 更新经 <see cref="Post"/>（DispatcherQueue.TryEnqueue）；
///   · 阻塞式 Core 调用（JobManager.Create/Open/ListAll、MigrationMatrix.Load、NetworkShare 连接、
///     ReportGenerator.Generate）一律 Task.Run 包裹；已是纯异步的 Core 方法（ScanAsync/RunAsync）直接 await。
///     ★ 例外（实测得出，2026-10-11）★ Verifier.RunAsync **必须** Task.Run 包裹：它在对象循环开始前是
///     同步枚举（96,000 文件实测 769 ms），直接 await 会把 UI 线程占住到枚举结束——验证面板来不及
///     渲染、"取消验证"点不到（实测窗口比验证结束还晚 1.3 s 才出现）。
///
/// ★ 线程数定案（用户授权）★
///   · 写进 job.json 的**永远是解析后的真实整数**：自动 ⇒ 16（Core 默认，Models.cs:91）、4 ⇒ 4、8 ⇒ 8、16 ⇒ 16…；
///   · 下拉档位完整 7 档：自动（推荐）(哨兵 0) / 4 / 8 / 16 / 32 / 64 / 128；
///     **16 是独立档位**，与「自动」并列（哨兵用 0，所以自动不会被显示成 16，16 也不会被显示成「自动」）；
///   · 不新增 Auto 枚举、不改字段结构、不改存档语义；
///   · 恢复既有任务时 **job.json 里的 Options.Threads 是绝对权威**：即使旧任务是 16/32/64/128
///     而 UI 下拉没有对应项，也**绝不覆写**，而是把原值动态追加进下拉并以只读口吻显示；
///     Resume 继续使用原线程值（本类在恢复路径上**从不**写 Options.Threads）。
/// </summary>
public sealed class MigrationSessionViewModel : ObservableObject
{
    // ────────────────────────── 常量 / 静态文案 ──────────────────────────

    /// <summary>「自动」解析后的真实线程数（Core 的默认值，Models.cs:91）。</summary>
    public const int AutoThreadsResolved = 16;

    /// <summary>实时文件流的滚动上限（超过就丢最旧的一条，避免长时间迁移把内存撑爆）。</summary>
    public const int LiveFileCapacity = 200;

    /// <summary>UI 节拍的固定间隔（50ms = 20Hz）。与 <see cref="DispatcherQueueUiFlushPump.FlushInterval"/> 同源。</summary>
    public static readonly TimeSpan UiFlushInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// 进度停滞告警阈值（秒）。直接引用 Core 唯一引擎的同一常量（`public const`），
    /// 杜绝两处口径漂移——本层不再保留复制版。
    /// </summary>
    public const double StallWarnSeconds = TransferOrchestrator.StallWarnSeconds;

    /// <summary>
    /// ★ L1 结论的唯一正确说法（用户指定硬约束）★
    /// L1 永远**不得**显示成「完整性验证通过」。
    /// </summary>
    public const string L1ResultText = "基础一致性检查通过：文件数量和总字节一致，尚未进行内容级完整性核对";

    /// <summary>HashSampled == 0 时若展示 L2 结论，必须附带这句话。</summary>
    public const string NoHashSampleText = "未完成内容级抽样，不代表内容一致";

    /// <summary>写进代码的纪律说明：Core 返回 OK 只代表 L1 口径的"数量/字节一致"，不得扩大解释成"数据完整"。</summary>
    public const string NoOverclaimNote =
        "引擎返回 OK 只等于「文件数与总字节一致」，不等于内容一致：不得据此向用户宣称“数据完整”。";

    // ────────────────────────── 依赖 ──────────────────────────

    private readonly ConnectionViewModel? _connection;
    private readonly DispatcherQueue? _queue;
    private readonly ILogger _log;

    /// <summary>
    /// ★ P0-06(1) ★ UI 心跳监视器（后台采样）：判"界面未响应"的事实来源。
    /// 只在一次运行期间（节拍存活期间）判定——没有节拍就不该拿"没有心跳"当卡住。
    /// </summary>
    private readonly UiStallWatchdog _uiStallWatchdog;

    // ════════════════════════ A.5（P1-5）UI 节流：缓冲 + 薄泵 ════════════════════════

    /// <summary>
    /// 高频 UI 流的合并缓冲（纯逻辑、可被单测行为级覆盖，见 <see cref="UiBatchBuffer{TSnapshot}"/>）。
    /// 生产者线程（引擎 stdout/stderr 泵、进度轮询）入队；UI 线程按 50ms 节拍 Drain 并应用。
    /// </summary>
    private readonly UiBatchBuffer<ProgressSnapshot> _batcher;

    /// <summary>
    /// 节拍薄泵（持 <c>DispatcherQueueTimer</c>）。**仅在生产/离屏 UI 队列存在时构造**；
    /// <see cref="ReadonlyUiThrottleDisabled"/> 为 true 时恒为 null（完全直通）。
    /// 它被**惰性**创建（首次开跑时），避免"没在跑却有个 timer 在滴答"。
    /// </summary>
    private IUiFlushPump? _flushPump;

    /// <summary>
    /// 薄泵工厂（测试缝）：生产恒走 <see cref="CreateProductionFlushPump"/>。
    /// </summary>
    private Func<Func<bool>, IUiFlushPump>? _flushPumpFactory;

    /// <summary>
    /// ★ 不可节流判定（读一次；<c>PCMIG_UI_THROTTLE=0</c> 是保命回退开关）★
    ///
    /// 为 true 的场景（任一命中即完全直通，退回逐条 <c>Post</c> 的旧语义）：
    ///   · <c>_queue is null</c>：离屏构造 / 单测 / 非 UI 线程构造（<c>GetForCurrentThread()</c> 返回 null）
    ///     ⇒ 建不出 <c>DispatcherQueueTimer</c>，且这些路径依赖"直通即可用"；
    ///   · <c>PCMIG_UI_THROTTLE=0</c>：给"两类独立证据"（有/无节流的行为对照）留手段，
    ///     与 <c>PCMIG_UNIFORM_HOST=0</c> / <c>PCMIG_CLASSIC_UI=1</c> 的既有回退风格一致。
    ///
    /// 注意：**即使节流开启，下面"不可延迟的量"也一律走 <see cref="PostImmediate"/>**（先 Flush 再应用），
    /// 所以这个开关只影响"是否合并高频流"，不影响任何安全边界。
    /// </summary>
    private readonly bool _uiThrottleDisabled;

    /// <summary>★ 仅测试缝 ★：强制"节流可用"判定（见 <see cref="EnableUiThrottleForTest"/>）。</summary>
    private bool _uiThrottleForcedForTest;

    /// <summary>节流是否**不可用**（= 完全直通）。测试缝可覆盖。</summary>
    private bool UiThrottleUnavailable => _uiThrottleDisabled && !_uiThrottleForcedForTest;

    /// <summary>最近一次**已应用**的阶段（供"阶段一变就立即整体应用"的判定，兼防 Paused→Running 吞掉暂停态）。</summary>
    private JobPhase _lastAppliedPhase = JobPhase.Created;

    /// <summary>诊断（<c>PCMIG_UI_FLUSH_TRACE=1</c>）：本次运行累计完成的 flush 次数。</summary>
    private int _flushCount;

    /// <summary>
    /// </summary>
    /// <param name="connection">Step1 的连接状态源（只读投影 Host/Username/IsConnected/BenchmarkOnConnect）。可为 null（离屏构造/测试）。</param>
    /// <param name="dispatcherQueue">UI 线程队列；默认取当前线程（页面/窗口构造时应已在 UI 线程）。</param>
    /// <param name="logger">应用级日志；默认与 ConnectionViewModel 同一份 app-*.log。</param>
    public MigrationSessionViewModel(
        ConnectionViewModel? connection = null,
        DispatcherQueue? dispatcherQueue = null,
        ILogger? logger = null)
    {
        _connection = connection;
        _queue = dispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
        _log = logger ?? LogBootstrap.CreateAppLogger(console: false);

        // ★ A.5（P1-5）降级判定（只读一次）★：拿不到 UI 队列 ⇒ 建不出 DispatcherQueueTimer，
        //   一律完全直通（离屏/单测路径必须继续"直通即可用"）；PCMIG_UI_THROTTLE=0 ⇒ 保命回退。
        _uiThrottleDisabled =
            _queue is null
            || string.Equals(Environment.GetEnvironmentVariable("PCMIG_UI_THROTTLE"), "0", StringComparison.Ordinal);
        _batcher = new UiBatchBuffer<ProgressSnapshot>(LiveFileCapacity, LogLineCapacity);

        // ★ P0-06(1)/(4) ★ UI 心跳监视器：把"点击停止时界面卡住"从用户口述变成可读事实。
        //   心跳由 UI 线程的节拍（DrainOnTick）与用户操作（点停止）打；采样在**后台线程**，
        //   因此界面真的卡住时它仍能观察沉默并立刻落一条证据（见 UiStallWatchdog 的类注释）。
        //   纪律：它绝不改业务结论（不停止、不取消、不改状态），只写事实；回调异常只计数。
        _uiStallWatchdog = new UiStallWatchdog(onStall: OnUiStallFact);
        if (_uiThrottleDisabled)
            _log.Information("UI 节流未启用（完全直通）：queue={HasQueue}，PCMIG_UI_THROTTLE={Throttle}",
                _queue is not null, Environment.GetEnvironmentVariable("PCMIG_UI_THROTTLE") ?? "(unset)");

        Tree = new DirectoryTreeViewModel(_queue, _log);
        Tree.RootCheckedChanged += OnTreeRootCheckedChanged;

        if (_connection is not null)
        {
            _connection.PropertyChanged += (_, e) =>
            {
                // 连接侧只影响"能不能开始"的表层判定，不复制它的任何连接逻辑。
                if (e.PropertyName is nameof(ConnectionViewModel.Host)
                    or nameof(ConnectionViewModel.Username)
                    or nameof(ConnectionViewModel.IsConnected))
                {
                    Raise(nameof(Host));
                    Raise(nameof(Username));
                    Raise(nameof(IsSourceConnected));
                    Raise(nameof(SourceSummary));
                    Raise(nameof(CanStart));

                    // ★ 2026-10-08（真机问题 5）★ 源一旦连上就启动网卡吞吐观测（幂等；地址解析在后台）。
                    var conn = _connection;
                    if (conn is not null && conn.IsConnected) EnsureNetworkThroughput();
                }

                // ★ UI Closure 2026-10-05（用户指令 UI-10：状态消息路由）★
                //   连接流程的播报（正在连接…／发现共享结论／正在探测…／已添加并勾选…）统一路由到
                //   Shell 左侧提示卡（OperationalStatus 通道），表单内只留字段级／错误级文字。
                //   为什么不直接路由 ConnectionViewModel.Status：Status 里混着"请先输入 IP"这类字段校验，
                //   进提示卡会与"流程状态"语义混淆；连接侧另有专用 FlowStatus 通道。
                if (e.PropertyName is nameof(ConnectionViewModel.FlowStatus))
                {
                    var flow = _connection.FlowStatus;
                    if (!string.IsNullOrWhiteSpace(flow)) SetOperational(flow);
                }

                // ★ 2026-10-08 真机复验（返修 R1）★ 连接失败的用户引导与错误摘要也进左侧提示卡。
                //   连接侧不再写表单（Step 1 表单只留字段级短校验），所以这两条通道必须一起投影，
                //   否则失败说明就"哪儿都不显示"。
                //   为什么**直接写属性**而不走 SetChannel：SetChannel 会连带覆写聚合句 StatusMessage
                //   与执行卡短句 ExecutionStatus —— 空值投影时会把刚落位的失败结论一起擦掉。
                //   这里只写通道本身，空值即折叠该行（ShellHintCard 按空值折叠）。
                if (e.PropertyName is nameof(ConnectionViewModel.FlowUserHint))
                    UserHint = _connection?.FlowUserHint ?? string.Empty;

                if (e.PropertyName is nameof(ConnectionViewModel.FlowErrorSummary))
                    ErrorSummary = _connection?.FlowErrorSummary ?? string.Empty;
            };
            _connection.Shares.CollectionChanged += (_, _) => Raise(nameof(SourceShareCount));
        }
    }

    // ────────────────────────── 输入状态 ──────────────────────────

    private string _targetRoot = string.Empty;
    /// <summary>本地目标根（例如 D:\Migrated\OLD-PC）。Step2 由页面写入。</summary>
    public string TargetRoot
    {
        get => _targetRoot;
        set
        {
            if (!Set(ref _targetRoot, value ?? string.Empty)) return;
            Raise(nameof(TargetSummary));
            Raise(nameof(CanStart));
            Raise(nameof(WorkspaceHint));
        }
    }

    /// <summary>
    /// 「自动（推荐）」在下拉里的哨兵值（**不是**写盘值）：解析后仍是
    /// <see cref="AutoThreadsResolved"/>=16，见 <see cref="ResolveThreads"/>。
    /// 用 0 而非 16 当哨兵，是为了让 16 能作为**独立档位**与「自动」并列出现。
    /// </summary>
    public const int AutoThreadsSentinel = 0;

    /// <summary>
    /// robocopy bulk 通道的 /MT 线程数下拉选项（完整档位）：
    /// 「自动（推荐）」(=<see cref="AutoThreadsSentinel"/>) / 4 / 8 / 16 / 32 / 64 / 128。
    /// </summary>
    public ObservableCollection<int> MtOptions { get; } = new() { AutoThreadsSentinel, 4, 8, 16, 32, 64, 128 };

    private int _selectedMt = AutoThreadsSentinel;
    /// <summary>
    /// 选定的线程数。**写入 job.json 的永远是解析后的真实整数**（自动 = 16）。
    /// 用户改动本值只影响"新建任务"；恢复既有任务时本值由 <see cref="PinThreadsFromJob"/> 锁定为任务原值。
    /// </summary>
    public int SelectedMt
    {
        get => _selectedMt;
        set
        {
            if (!Set(ref _selectedMt, value)) return;
            _mtValuePinnedByJob = false;   // 用户显式选择 = 解除"任务原值"显示锁
            Raise(nameof(MtText));
            Raise(nameof(ResolvedThreads));
            Raise(nameof(ThreadsDisclaimer));
        }
    }

    private bool _mtValuePinnedByJob;
    /// <summary>true = 当前显示的是既有任务里保存的线程值（下拉里没有对应项时动态追加，绝不改写）。</summary>
    public bool MtValuePinnedByJob => _mtValuePinnedByJob;

    /// <summary>解析后的真实线程数（自动 ⇒ 16；非法值兜底 16）。</summary>
    public int ResolvedThreads => ResolveThreads(_selectedMt);

    /// <summary>把"自动 / 0 / 负数"解析成真实整数（写盘口径），与 Core 默认值一致。</summary>
    public static int ResolveThreads(int selectedMt) => selectedMt > 0 ? selectedMt : AutoThreadsResolved;

    /// <summary>运行时会真正使用的线程数：有任务时**以 job.json 为准**（续传沿用原值）。</summary>
    public int ThreadsForRun
    {
        get
        {
            var saved = Ctx?.Definition.Options.Threads ?? 0;
            return saved > 0 ? saved : ResolvedThreads;
        }
    }

    public string MtText => _mtValuePinnedByJob ? $"{_selectedMt}（任务原值，续传沿用）" : MtTextFor(_selectedMt);

    // ═══════════════ ★ P1-07(1) ★ 目录映射语义的界面选择 ═══════════════
    //   产品语义（用户裁决口径）：
    //     contentDirect（默认）= 复制**所选文件夹的内部内容**到 TargetRoot —— 选 \\host\Users\d970
    //       就在 C:\Users\D970 根下直接出现文件与原有下级目录，不再额外套一层 d970（现场 33.72GB 的错层来源）。
    //     keepFolder          = 保留**所选文件夹本身**（旧行为，用户明确要时才用）。
    //   真实落盘位置只有一处：job.json 的 Options.SelectedFolderMapping（预检时从本属性写入新定义）；
    //   恢复既有任务一律以旧 job.json 的原值为准，本类在恢复路径上**从不**覆写它。
    private string _selectedFolderMapping = MigrationOptions.SelectedFolderMappingContentDirect;

    /// <summary>目录映射语义（<see cref="MigrationOptions.SelectedFolderMappingContentDirect"/> / ...KeepFolder）。</summary>
    public string SelectedFolderMapping
    {
        get => _selectedFolderMapping;
        set
        {
            var v = string.Equals(value, MigrationOptions.SelectedFolderMappingKeepFolder, StringComparison.OrdinalIgnoreCase)
                ? MigrationOptions.SelectedFolderMappingKeepFolder
                : MigrationOptions.SelectedFolderMappingContentDirect;
            if (Set(ref _selectedFolderMapping, v)) Raise(nameof(FolderMappingText));
        }
    }

    /// <summary>供 Step2 下拉使用的序号：0 = 内容直达（推荐），1 = 保留所选文件夹本身。</summary>
    public int FolderMappingUiIndex
    {
        get => SelectedFolderMapping == MigrationOptions.SelectedFolderMappingKeepFolder ? 1 : 0;
        set => SelectedFolderMapping = value == 1
            ? MigrationOptions.SelectedFolderMappingKeepFolder
            : MigrationOptions.SelectedFolderMappingContentDirect;
    }

    /// <summary>下拉项文案（页面回读时用同一套字面，避免"界面显示 B、写盘是 A"）。</summary>
    public string FolderMappingText => SelectedFolderMapping == MigrationOptions.SelectedFolderMappingKeepFolder
        ? "保留所选文件夹本身"
        : "复制所选文件夹的内容（推荐）";

    /// <summary>
    /// 线程数下拉项的显示文案（页面用）：
    ///   · <see cref="AutoThreadsSentinel"/>（0）⇒「自动（推荐）」（写盘仍是 16）；
    ///   · 16 是**独立档位**，显示成数字 "16"，不再与「自动」合并；
    ///   · 其余档位（4/8/32/64/128…）显示成数字。
    /// 既有任务的**原值**（例如 32/64/128）会原样显示成数字并在其后标注“任务原值”，
    /// 既不改写任务、也不假装它等于某个预设项。
    /// </summary>
    public string MtTextFor(int threads)
    {
        if (threads <= 0) return "自动（推荐）";
        return _mtValuePinnedByJob && threads == _selectedMt ? $"{threads}（任务原值）" : threads.ToString();
    }

    /// <summary>
    /// 把下拉文案还原成线程数（页面用）：空 /「自动…」⇒ <see cref="AutoThreadsSentinel"/>（0）。
    /// 无法解析时也返回 0（= 自动），由 <see cref="ResolveThreads"/> 解析成 16。
    /// </summary>
    public int MtFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return AutoThreadsSentinel;
        if (text.StartsWith("自动", StringComparison.Ordinal)) return AutoThreadsSentinel;
        var head = text.Split('（')[0].Trim();
        return int.TryParse(head, out var v) ? v : AutoThreadsSentinel;
    }

    public string ThreadsDisclaimer => _mtValuePinnedByJob
        ? "该任务创建时用的是 job.json 里保存的线程值，续传一律沿用原值（不会被界面改写）。"
        : $"写入 job.json 的线程数：{ResolvedThreads}（「自动」= Core 默认 {AutoThreadsResolved}）。";

    private bool _expertMode;
    /// <summary>超大数据模式（排除规则输入）开关；Step2 写入，Prepare 时落进 job.json。</summary>
    public bool ExpertMode { get => _expertMode; set => Set(ref _expertMode, value); }

    /// <summary>
    /// ★ 用户 ExpertMode 决议（方案 B，阶段 A）★
    /// Step2 的「超大数数据模式」开关**本阶段不接线**（保持 IsEnabled=False），
    /// 因此 <see cref="ExpertMode"/> 恒为 false，<c>customs</c> 恒为目录树勾选结果——
    /// 也就是恒走旧实现 <c>ExpertMode=false</c> 的那条路径。
    /// 本属性把该决议写进**可执行代码**（不只是注释），避免日后有人顺手把它接上。
    /// </summary>
    public bool ExpertModeIsWiredInPhaseA => false;

    /// <summary>
    /// Step 2 的目录树状态源（懒加载真实读盘 + 三态勾选 + <c>CollectCustomSelections()</c>）。
    /// 由本会话持有唯一实例，页面与 <see cref="PrepareAsync"/> 读同一棵树——**绝不各建一份**。
    /// </summary>
    public DirectoryTreeViewModel Tree { get; }

    /// <summary>
    /// 目录树根 ↔ Step 1 共享行的**单向联动**（同旧 WPF `MainViewModel.cs:582-586` 的
    /// `root.PropertyChanged` → `row.IsSelected = root.IsChecked == true`）：
    /// 根全勾 ⇒ 共享行选中（走 <c>wholeShares</c> 整盘迁移）；
    /// 根半勾 / 全不勾 ⇒ 共享行取消（半勾走 <c>customs</c> 子树；全不勾则完全排除）。
    ///
    /// 若不做这条联动，用户在 Step 2 取消共享根勾选后 <c>wholeShares</c> 仍会包含该共享，
    /// 表现为“取消被静默忽略、整盘照传”——这是旧实现刻意避免的语义，必须照搬。
    /// </summary>
    private void OnTreeRootCheckedChanged(DirNode node)
    {
        var conn = _connection;
        if (conn is null) return;
        foreach (var s in conn.Shares)
        {
            if (s.UncPath.TrimEnd('\\').Equals(node.FullPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                var want = node.IsChecked == true;
                if (s.IsSelected != want) s.IsSelected = want;
                break;
            }
        }
    }

    /// <summary>
    /// 按 Step 1 的**共享列表**刷新目录树根（页面在共享列表/勾选变化时调用）。
    /// 树根是**懒加载**的：这里只建根与占位子节点，真正的读盘发生在用户展开时。
    ///
    /// ★ A.5（P0-2）★ 输入改为**全部共享**（不再只传 <c>IsSelected</c> 的），
    /// 因为“只传已勾选”会与树根→共享行的单向联动成环并把整棵根删掉：
    ///   ①用户取消共享根下一个子目录 ⇒ 根 <c>IsChecked</c>: true → null（半选）
    ///   ②<see cref="DirectoryTreeViewModel.RootCheckedChanged"/> → <see cref="OnTreeRootCheckedChanged"/>
    ///   ③`want = IsChecked == true` ⇒ false ⇒ `s.IsSelected = false`
    ///   ④页面 <c>OnShareItemChanged</c> → 立刻回调本方法
    ///   ⑤若这里只传已勾选 ⇒ 该共享不在 wanted 里
    ///   ⑥<see cref="DirectoryTreeViewModel.SyncRoots(IEnumerable{ShareRootSpec})"/> 删根 ⇒ 用户刚做的精确选择随之消失。
    /// 现在根的存在只由“共享是否还在共享列表里”决定，勾选态（全勾/半勾/全不勾）只由 <c>IsChecked</c> 表达
    /// ——与旧 WPF 一致（旧 `DirTree` 只在连接时构建一次，`Gui\MainViewModel.cs:461-475` / `:576-589`）。
    /// 副作用（本轮已按用户要求关闭）：~~未勾选的共享同样会以“全不勾”出现在 Step 2 目录树中~~。
    ///
    /// ★ 2026-10-05（执行书 §2.2）★ 用户明确要求：**Step 1 只选 H，Step 2 顶层就只能有 H；
    /// 未选择的共享不得进入 Step 2**。因此建根条件由"共享是否在列表里"收紧为
    /// "**当前已勾选** 或 **该根已被用户在 Step 2 实际使用过**"：
    ///   · 前者满足"只选 H 就只显示 H"；
    ///   · 后者保住 A.5 修掉的防回路语义（用户取消一个子目录 ⇒ 根半选 ⇒ 共享行被联动置 false，
    ///     此时根**必须**保留，否则用户刚做的精确选择会连同整棵根一起消失）——
    ///     详见 <see cref="DirectoryTreeViewModel.IsUserEngagedRoot"/> 的判据说明。
    /// </summary>
    public void SyncTreeRootsFromShares()
    {
        var conn = _connection;
        if (conn is null) return;

        // ★ 2026-10-05（执行书 §2.2）★ 只把"当前勾选"或"已被用户在 Step 2 实际使用过"的共享建根。
        //   注意仍要传 `s.IsSelected` 作为根的初始勾选态（未勾选却保留的根以"全不勾"呈现，
        //   与旧 WPF 一致），勾选态的跟随规则由 SyncRoots 内部处理。
        var specs = conn.Shares
            .Where(s => !string.IsNullOrWhiteSpace(s.UncPath))
            .Where(s => s.IsSelected || Tree.IsUserEngagedRoot(s.UncPath))
            .Select(s => new ShareRootSpec(s.UncPath, s.IsSelected))
            .ToList();
        Tree.SyncRoots(specs);
    }

    /// <summary>
    /// 选「新建任务」：清掉当前任务与计划（**不动作任何存档**）。
    /// 硬要求 2 的口径同样适用：清掉之后 <see cref="HasPlan"/> / <see cref="CanStart"/> 立即为 false，
    /// 界面不会在“没有计划”的状态下允许开始迁移。
    /// </summary>
    public void ClearCurrentJobSelection()
    {
        if (_ctx is null) return;
        _ctx = null;
        Raise(nameof(Ctx));
        Raise(nameof(HasJob));
        Raise(nameof(JobIdText));
        Raise(nameof(ThreadsForRun));
        // ★ A.5（P1-4）：「新建任务」= 换任务，一次性复位全部跨任务残留
        //   （失败清单 + 去重索引、实时文件流、当前对象/文件、上一次验证结论、上一次 Job 的日志投影）。
        ResetForJobSwitch();
        Percent = 0;
        IsFinished = false;
        ObjectText = "0/0";
        ProgressText = "0 B / 0 B";
        PlanBytesText = "0 B";
        ActualBytesText = "0 B";
        DataLabel = "已传 / 计划";
        BalanceText = "计划 0 B　·　实际落盘 0 B";
        SetOperational("已切换为「新建任务」：填好目标路径、在目录树勾选内容后点「预检并生成计划」。");
        HasPlanReset();
        Raise(nameof(CanRepair));
        RaiseDerived();
    }

    /// <summary>与既有 ConnectionViewModel 协作的只读投影（不重复造连接状态）。</summary>
    public string Host => _connection?.Host?.Trim() ?? string.Empty;
    public string Username => _connection?.Username?.Trim() ?? string.Empty;
    public bool IsSourceConnected => _connection?.IsConnected ?? false;
    public int SourceShareCount => _connection?.Shares.Count ?? 0;
    public string SourceSummary => IsSourceConnected ? $"已连接 {Host}" : "未指定旧电脑";
    public string TargetSummary => string.IsNullOrWhiteSpace(TargetRoot) ? "未指定目标" : TargetRoot;

    // ────────────────────────── 运行状态 ──────────────────────────

    private JobContext? _ctx;
    /// <summary>当前任务上下文（null = 还没有任务）。</summary>
    public JobContext? Ctx
    {
        get => _ctx;
        private set
        {
            if (!Set(ref _ctx, value)) return;
            Raise(nameof(HasJob));
            Raise(nameof(HasPlan));
            Raise(nameof(JobIdText));
            Raise(nameof(ThreadsForRun));
            RaiseDerived();
        }
    }

    private JobPhase _phase = JobPhase.Created;
    public JobPhase Phase
    {
        get => _phase;
        private set
        {
            if (!Set(ref _phase, value)) return;
            // ★ A.5（P1-5）★ 记录"已应用的阶段"：它是"阶段一变就立即整体应用"的判定基准，
            //   兼防同一节拍窗口内 Paused→Running 时"最新值胜出"把暂停态整段吞掉。
            _lastAppliedPhase = value;
            Raise(nameof(PhaseText));
            RaiseDerived();
        }
    }

    public string PhaseText => Phase switch
    {
        JobPhase.Created => "已创建（尚未预检）",
        JobPhase.Preflight => "预检中",
        JobPhase.Scanning => "扫描源数据中",
        JobPhase.Planned => "计划已生成",
        JobPhase.AwaitingReview => "计划已生成，等待执行",
        JobPhase.Running => "正在传输",
        JobPhase.Paused => "已暂停（可续传）",
        JobPhase.Verifying => "正在验证",
        JobPhase.Completed => "已完成",
        // ★ A.5（P1-3）：不得说成“完成”。CompletedWithErrors 可能只传了一部分（目标盘满/网络故障/
        //   大量失败/被迫中断），阶段名必须让用户一眼看出“没传完、可恢复”。
        JobPhase.CompletedWithErrors => "未完整完成（存在失败对象，可恢复）",
        JobPhase.Interrupted => "已中断（可续传）",
        JobPhase.Failed => "失败",
        JobPhase.Canceled => "已取消",
        _ => Phase.ToString(),
    };

    private double _percent;
    public double Percent { get => _percent; private set => Set(ref _percent, value); }

    /// <summary>
    /// ★ v0.5.4 Preview.2 真机修复（2026-10-16，用户任务令 §五）★
    /// **可信完成**：迁移是否真的可以称为"完成"，用于 Step3 → Step4 的自动前进。
    ///
    /// 为什么必须由这个属性统一回答：用户令明确禁止任何调用点自己拼
    /// <c>percent &gt;= 100</c>。真机 JOB-20261016-150319-e738 正是被这种口径害的——
    /// 10 个对象完成 9 个、object-000010 因 DumpStack.log 被源侧拒读而 Failed、
    /// 验证报告里 MISMATCH，界面却因为 completedBytes 被抹平而显示 100%。
    /// 若自动前进只看百分比，用户会被直接送到 Step4，而 Step3 的真实失败态一闪而过。
    ///
    /// 本属性**唯一**取自 Core 的 <see cref="TransferOrchestrator.IsTrustworthyCompletion"/>，
    /// 六项判据（Phase==Completed ∧ TotalObjects&gt;0 ∧ FailedObjects==0 ∧ MissingObjects==0
    /// ∧ CompletedObjects==TotalObjects ∧ CompletedBytes==TotalBytes）缺一不可。
    /// 任何一项不成立——CompletedWithErrors / 有失败对象 / 有缺失对象 / 字节没闭环——
    /// 都返回 false，用户留在 Step3 看到真实状态。
    /// 计算发生在 <see cref="FinishRunAsync"/>（唯一拿到真实 JobState 的收尾点）。
    /// </summary>
    private bool _trustworthyCompletion;

    /// <summary>
    /// 最近一次从真实 JobState 读到的「校验期确证目标缺失」对象数。
    /// 只服务于 <see cref="SnapshotSaysTrustworthyCompletion"/>（快照里没有这个字段），
    /// 真正写 <see cref="TrustworthyCompletion"/> 的唯一出口仍是 <see cref="ApplyTrustworthyCompletion"/>。
    /// </summary>
    private int _knownMissingObjects;
    public bool TrustworthyCompletion
    {
        get => _trustworthyCompletion;
        private set => Set(ref _trustworthyCompletion, value);
    }

    /// <summary>
    /// 按真实 JobState 重算 <see cref="TrustworthyCompletion"/>（唯一写入点）。
    /// 未拿到状态（null）时一律 false —— 拿不到证据就不许说"完成"。
    /// </summary>
    private void ApplyTrustworthyCompletion(JobState? s)
    {
        // ★ R2 接手修复 · S-4 滞后一帧 ★
        //   快照通路（ApplySnapshot）拿不到 JobState（它只有引擎快照里的对象数/字节），
        //   却必须回答"这一帧允不允许显示 100%"。旧实现直接读上面那个属性 —— 那是**上一次**
        //   按落盘状态算出来的结论，于是"收尾那一刻的 Completed 快照"用着"运行中"的旧值，
        //   明明字节闭环也显示 99.9%，要等收尾路径再写一次才变 100（真机上表现为进度条卡在 99.9%）。
        //   这里把"最近一次从真实状态读到的确证缺失数"记下来，供快照级判据复用：
        //   快照自身能被证明闭环，但校验期已确证目标少文件时仍不许 100%。
        _knownMissingObjects = s?.MissingObjects ?? 0;
        TrustworthyCompletion = s is not null && TransferOrchestrator.IsTrustworthyCompletion(s);
    }

    /// <summary>
    /// ★ R2 接手修复 · S-4 滞后一帧 ★ 用**快照自身携带的数据**判"这一帧可不可信"，而不是复用上一帧的结论。
    ///
    /// 判据与 Core <see cref="TransferOrchestrator.IsTrustworthyCompletion"/> 逐项对齐，唯一差别是
    /// <c>MissingObjects</c> 不在 <see cref="ProgressSnapshot"/> 里，改用最近一次从真实 JobState 读到的值
    /// （<see cref="_knownMissingObjects"/>）—— 传输期它恒为 0（开跑时会复位），
    /// 校验期确证缺失后由 <see cref="ApplyVerifyConclusion"/> 走落盘口径重算，两条通路互相校验而非各算一套。
    /// </summary>
    private bool SnapshotSaysTrustworthyCompletion(ProgressSnapshot s)
        => s.Phase == JobPhase.Completed
           && s.TotalObjects > 0
           && s.FailedObjects == 0
           && _knownMissingObjects == 0
           && s.CompletedObjects >= s.TotalObjects
           && s.CompletedBytes == s.TotalBytes;

    /// <summary>
    /// ★ R2 接手修复 · 缺陷 H（S-1/S-2/S-3/S-4 合并处置）★
    /// <b>「进度 100%」的唯一放行口径</b>：先按 Core 判据重算可信完成，只有可信才允许写 100.0；
    /// 不可信时一律取真实落盘比例并封顶 <see cref="TransferOrchestrator.SettledPercentCapPublic"/>（99.9）。
    ///
    /// 为什么必须收敛到一个方法：修复前"写 100%"在外壳里有四条互不相干的通路
    /// （载入历史任务 / 修复轮复验通过 / 校验发现缺失 / 进度快照兜底），
    /// 其中三条只看 <c>Phase == Completed</c> 或 <c>report.OverallPass</c>，与 Core 的
    /// <see cref="TransferOrchestrator.IsTrustworthyCompletion"/> 是两个互不校验的维度 ——
    /// 结果就是"进度条满了、内部却认为不可信、页面也不自动前进"这种自相矛盾的画面。
    /// 收敛后只要有人再新增一条写 100% 的通路而不经此方法，就会立刻暴露在测试里。
    /// </summary>
    /// <param name="s">真实的 JobState（拿不到就传 null：null 一律不可信，绝不猜完成）。</param>
    private void ApplySettledCompletion(JobState? s)
    {
        ApplyTrustworthyCompletion(s);
        if (TrustworthyCompletion)
        {
            PublishPercent(100.0, allowVisualSnap: true);
            return;
        }
        if (s is null) return;
        PublishPercent(Math.Min(ActualPercent(s), TransferOrchestrator.SettledPercentCapPublic), allowVisualSnap: true);
    }

    /// <summary>
    /// ★ R2 接手修复 · 缺陷 H（S-3）★ 校验结论落地到界面的**唯一**入口：
    /// 先把「确证缺失」写进 job-state（<see cref="VerifyReconciliation.Apply"/>），
    /// 再按同一口径重算可信完成**与百分比**。
    ///
    /// 抽成方法而不是写在 <c>VerifyAsync</c> 里，是为了让单测能直接钉住这条契约
    /// （测试项目以链接源码方式编译本文件，属同一程序集）。校验才是最硬的证据：
    /// 它一旦发现目标确实少文件，界面就不允许继续停在 100%。
    /// </summary>
    internal void ApplyVerifyConclusion(JobContext ctx, VerifyReport? report)
    {
        // 顺序不可颠倒：先把「确证缺失」写进 job-state（有落盘证据），再据此重算界面结论。
        VerifyReconciliation.Apply(ctx, report);
        ApplySettledCompletion(ctx.LoadStateOrNew());
    }

    /// <summary>
    /// ★ R2 接手修复 · 缺陷 J（2026-10-17）★ **写百分比的唯一出口**。
    ///
    /// 缺陷原文（独立代码审查 🔴-2 实测）：界面上三处显示百分比的地方读的都是
    /// <c>truth?.Percent ?? Session.Percent</c> ——
    ///   · <c>MainWindow.PushFooter</c>（底栏）；
    ///   · <c>Step3ProgressPage.PushState</c>（页内大号百分比）；
    ///   · 两处呈现节拍（<c>RefreshFooterPresentation</c> / <c>AdvancePresentationTick</c>）。
    /// 也就是说：**只要 PresentationTruth 非空，<see cref="Percent"/> 根本不会被读**。
    /// 于是缺陷 H（S-3）"校验确证缺失后把 Percent 压回 99.9"这条路，对任何已经收到过
    /// 进度快照的任务**完全无效** —— 用户仍然看到 100%（来自那份旧真值），
    /// 正是任务令明令禁止的「有文件缺失但进度 100%」。
    ///
    /// 收口方式：任何写百分比的通路都必须经这里，由它保证
    /// 「<see cref="PresentationTruth"/>.Percent 与 <see cref="Percent"/> 不会一个 100 一个 99.9」。
    /// 只向下压、不向上抬：真值仍然只由引擎快照产生，本方法绝不凭空抬高显示。
    /// </summary>
    /// <param name="percent">已经过可信完成闸门裁决的百分比。</param>
    /// <param name="allowVisualSnap">
    /// 是否允许把**呈现层**（80 ms 节拍的视觉值）一并压下来。
    /// 只在"结论被更强的证据改小"时传 true（校验确证缺失、载入历史不可信任务、收尾降级）；
    /// 传输过程中的每一帧快照必须传 false —— 那段时间视觉值由
    /// <see cref="ProgressPresentationCoordinator.ApplyTruth"/> 的单调推进负责，
    /// 随手 SnapTo 会破坏"暂停/恢复期间显示不倒退"的连续性契约。
    /// </param>
    private void PublishPercent(double percent, bool allowVisualSnap = false)
    {
        Percent = percent;
        // 只压不抬：旧真值若比裁决结果更乐观，必须跟着降下来（否则 UI 读它、看不到降级）。
        if (_presentationTruth is not null && _presentationTruth.Percent > percent)
            PresentationTruth = _presentationTruth with { Percent = percent };
        // 呈现层的视觉百分比只由 ApplyTruth（单调）产生，本身只会往上走；
        // 校验期把结论压下来时，若不同步 SnapTo，底栏与 Step3 的那个大号数字
        // 仍然会停在旧的 100% —— 于是"进度条 100% + 校验确证缺失"同屏。
        // 只在**向下压**时动用显式回退（向上仍由引擎真值驱动，绝不由本方法抬高）。
        if (allowVisualSnap && _presentation.VisualPercent > percent)
            _presentation.SnapTo(percent, DateTime.UtcNow, "percent-lowered");
    }

    /// <summary>
    /// ★ R2 接手修复 · 缺陷 K ★ 「正在校验」的独立闸门位。
    ///
    /// <para>
    /// 缺陷原文：<c>VerifyAsync</c> 全程**不置** <c>IsRunning</c>（那是传输引擎的位），而
    /// <c>CanStart</c> / <c>CanRepair</c> / <c>CanResume</c> 三个判据又**只看** <c>IsRunning</c>，
    /// 于是校验在飞时「开始迁移」「尝试修复」「恢复任务」三个按钮仍然亮着：
    /// 用户一点，<c>TransferOrchestrator</c> 就带着它**开跑时载入的那份内存快照**开始周期性
    /// <c>SaveState</c>，把校验刚确证的 <c>MissingObjects</c> 整体覆盖回 0 ——
    /// 「校验发现目标少文件」这条结论当场消失，界面重新变成 100% + 已完成。
    /// </para>
    /// <para>
    /// 反向的不对称必须补齐：<c>VerifyAsync</c> 一直有 <c>if (IsRunning &amp;&amp; !allowDuringRun)</c>
    /// 守卫（运行中不许校验），而「校验中不许运行」这条路**从来没被拦过**。
    /// 本属性就是那条路，语义与 <c>IsRunning</c> 对等：两条链任一在飞，另一条必须进不来。
    /// </para>
    /// </summary>
    private bool _isVerifying;
    public bool IsVerifying
    {
        get => _isVerifying;
        private set
        {
            if (!Set(ref _isVerifying, value)) return;
            RaiseDerived();
        }
    }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!Set(ref _isRunning, value)) return;
            RaiseDerived();
            if (value) return;
            // ★ P0-06 ★ 引擎真值说"不再运行" ⇒ 这一次停止请求到此收尾：
            //   时间线记下此刻（点击→引擎收尾的毫秒数），退出"正在停止…"进行时，
            //   并把状态句从"正在停止…"换成收尾结论（否则那句进行时会永远留在界面上）。
            _lastStopTimeline?.Mark("engine-settled");
            var wasStopping = IsStopping;
            IsStopping = false;
            if (wasStopping)
                SetOperational("已停止：本轮已收尾，已拷入的部分全部保留，可点「恢复任务」续传。");
        }
    }

    // ── ★ P0-06 ★ 停止是"请求 → 兑现"两段，中间这段是**可见的进行时** ──────────────────────
    //   现场症状：点了停止之后界面像死了一样（控件不响应、看不到任何进行时反馈）。
    //   下面两个成员让"已请求停止、引擎仍在收尾"成为一等状态：
    //     · IsStopping      → 底栏按钮文案变"正在停止…"、按钮进入 busy（不可重复按）；
    //     · LastStopTimeline → 分段 Stopwatch 事实（点击→反馈→Flush→取消投递→引擎收尾→可再操作）。
    //   它们只表达"进行时"，**绝不**把"请求已发出"冒充"已经停下"——停下仍只认引擎真值 IsRunning。
    private bool _isStopping;
    /// <summary>已经请求停止、引擎尚未收尾（界面必须显示"正在停止…"）。</summary>
    public bool IsStopping
    {
        get => _isStopping;
        private set { if (Set(ref _isStopping, value)) RaiseDerived(); }
    }

    private StopTimeline? _lastStopTimeline;
    /// <summary>最近一次停止请求的分段计时（诊断/报告取证用；没有停止过时为 null）。</summary>
    public StopTimeline? LastStopTimeline
    {
        get => _lastStopTimeline;
        private set => Set(ref _lastStopTimeline, value);
    }

    /// <summary>停止按钮文案（进行时真值 ⇒ 文案，不反过来）。</summary>
    public string StopButtonText => IsStopping ? "正在停止…" : "停止";

    // ── ★ P0-06(1)/(4) ★ 界面未响应的实测证据（不是靠用户口述，也不是靠猜） ────────────────
    //   现场只有一句"点击停止时 UI 卡住、偶发崩溃"，ZIP 里没有 WER / Application Hang 1002 / 转储，
    //   于是"卡了多久、最后还能操作什么"完全没有证据。UiStallWatchdog 在后台采样 UI 心跳，
    //   把这一段变成可读事实：卡住当时就落一条，恢复时再补一条总时长。
    //   纪律：这些属性只**读出事实**，不参与任何业务判定。
    /// <summary>已记录的界面未响应事实（检测 + 恢复，按发生顺序；没有则空）。</summary>
    public IReadOnlyList<UiStallFact> UiStalls => _uiStallWatchdog.Stalls;

    /// <summary>最严重的一次界面未响应（从未发生时为 null）。</summary>
    public UiStallFact? WorstUiStall => _uiStallWatchdog.Worst;

    /// <summary>距离 UI 线程最后一次心跳过去了多少毫秒（诊断/报告取证用）。</summary>
    public long UiSilenceMs => _uiStallWatchdog.SinceLastHeartbeatMs;

    /// <summary>
    /// 检测到（或恢复到）界面未响应时的唯一出口：**只写日志事实**，不改任务状态、不取消、不停止。
    /// 运行在后台线程 ⇒ 不得触任何 UI 对象，也不得抛（异常只计在 Watchdog 的 CallbackFailures 里）。
    /// </summary>
    private void OnUiStallFact(UiStallFact fact)
    {
        if (fact.Recovered)
        {
            _log.Warning("界面已恢复响应：本次未响应共 {StallMs} ms（最后一次心跳在监视器启动后 {LastBeatMs} ms，来自 {Context}）",
                fact.TotalStallMs, fact.LastHeartbeatMs, fact.LastHeartbeatContext ?? "(未知)");
            return;
        }

        // ★ 卡住**当时**就落证据：应用若被强杀/崩溃，这条日志是唯一留下的现场。
        _log.Warning("界面未响应：距最后一次心跳已 {StallMs} ms 没有滴答（最后一次心跳在监视器启动后 {LastBeatMs} ms，来自 {Context}）——正在进行的迁移不会被本监视器改变",
            fact.StallMs, fact.LastHeartbeatMs, fact.LastHeartbeatContext ?? "(未知)");
    }

    // ─────────────── 暂停真值（FIX BATCH 2：Single Source of Truth = 引擎）───────────────
    //
    // 旧实现（真机证伪）：footer Pause 点击后**立刻**写 IsPaused = true、LastPauseOutcome = Accepted，
    // 然后下一次快照 `IsPaused = s.Phase == JobPhase.Paused` 再把它覆盖回去 —— 用户因此可以反复点击，
    // 界面一会儿"已暂停"一会儿"进行中"，而传输从来没有停过。
    // 现在：UI 只允许**映射**引擎真值（PauseState / PauseRequestedAt / PauseAchievedAt / PauseOutcome /
    // PauseFailureReason，随每个 ProgressSnapshot 送达），本地只补"请求已写入、引擎尚未表态"这一小段，
    // 且必须显示为「正在暂停…」——绝不允许再出现乐观的"已暂停"。

    private PauseUiState _pauseUiState = PauseUiState.Idle;
    /// <summary>UI 侧暂停状态机（派生自引擎真值；唯一转换入口见 <see cref="ApplyPauseTruth"/>）。</summary>
    public PauseUiState PauseUiState
    {
        get => _pauseUiState;
        private set { if (Set(ref _pauseUiState, value)) RaiseDerived(); }
    }

    private PauseState _enginePauseState = PauseState.None;
    private PauseOutcomeKind _enginePauseOutcome = PauseOutcomeKind.None;
    private DateTime? _enginePauseRequestedAt;
    private DateTime? _enginePauseAchievedAt;
    private string? _enginePauseFailureReason;
    /// <summary>本地事实：暂停请求文件已写入成功、引擎尚未表态（"正在暂停…"的唯一合法来源）。</summary>
    private bool _pauseRequestWritten;
    /// <summary>本地事实：用户重新点击暂停的时点（用于识别引擎上一轮的旧失败结论，不重复显示）。</summary>
    private DateTime? _pauseRetryAfterUtc;

    /// <summary>引擎真值：暂停状态机（None / Pausing / Paused / PauseFailed）。</summary>
    public PauseState EnginePauseState => _enginePauseState;
    /// <summary>引擎真值：暂停的最终业务结果（None / Achieved / Failed）。</summary>
    public PauseOutcomeKind EnginePauseOutcome => _enginePauseOutcome;
    /// <summary>引擎真值：本次暂停请求被引擎处理的时刻（UTC）。</summary>
    public DateTime? PauseRequestedAt => _enginePauseRequestedAt;
    /// <summary>引擎真值：worker 真的停住、暂停达成的时刻（UTC）。未达成为 null。</summary>
    public DateTime? PauseAchievedAt => _enginePauseAchievedAt;
    /// <summary>引擎真值：暂停失败原因（仅 Failed 时有值）。</summary>
    public string? PauseFailureReason => _enginePauseFailureReason;

    /// <summary>
    /// ★ FIX BATCH 3 / §6（信任关键）★ 等**引擎真值**落定后再让 UI 发出动作终点事件。
    ///
    /// 为什么必须有：诊断的动作兑现判定要求"请求受理"与"业务效果"分开。如果 UI 在点击瞬间
    /// （或只在请求写入之后）就关掉动作，跟踪器会立刻以 "completed" 收口 —— 「请求写入成功」
    /// 就冒充了「迁移已暂停」，这正是真机上"事件卡 0 / 健康 healthy / 传输从未停止"的成因。
    ///
    /// 预算 = 引擎硬失败 SLA（10 s）+ 诊断宽限（2 s）+ 3 s 传输往返余量；超时即如实返回
    /// false（调用方记 Unknown，不假装成功）。**不改变任何迁移行为**，只延迟 UI 的终点事件。
    ///
    /// ★ FIX BATCH 7（RECOVERY GATE case02 现场发现）★ 旧实现写的是
    ///   `while (_enginePauseState == PauseState.Pausing && …)`：**要求"暂停中"这个中间态已经到达**才会等待。
    ///   而引擎真值（PauseState）只在每 2 s 的快照里送一次，点击瞬间它还是 None/上一轮的状态 ⇒ 循环一次都不进，
    ///   方法立刻返回 false ⇒ footer 把一次**真实达成**的暂停记成 `Unknown / pause-unsettled`，
    ///   pause.v2 期望永远没有 FeedbackConfirmed（实测 case02：引擎 0.36 s 就 TRN-011 停住了，UI 却在 17 ms 后收口 Unknown）。
    ///   现在改成"等它**落定**"：Paused（成功）/ PauseFailed（失败）任一到达即收口；运行已收尾且引擎侧没有任何暂停态
    ///   ⇒ 暂停不可能达成，提前如实返回 false。
    /// </summary>
    public async Task<bool> WaitForPauseSettledAsync(CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ActionSla.PauseFulfillmentDeadlineMs + 3_000);
        while (DateTime.UtcNow < deadline)
        {
            if (_enginePauseState is PauseState.Paused or PauseState.PauseFailed) break;
            // 引擎侧没有任何暂停态、而这一次运行已经收尾 ⇒ 暂停不会达成了（例如它自己先跑完了），
            // 如实返回 false，不去凑满预算。
            if (!IsRunning && _enginePauseState == PauseState.None) break;
            try { await Task.Delay(50, ct); }
            catch (OperationCanceledException) { break; }
        }
        return _enginePauseState == PauseState.Paused;
    }

    /// <summary>
    /// ★ FIX BATCH 3 / §6.4（同类审计）★ 恢复的达标真值 = 引擎**离开 Paused**并回到运行。
    /// 与暂停同理：若在 ResumeAsync 返回处就发终点事件，"暂停请求文件被清除成功"就冒充了
    /// "迁移已恢复"。就地恢复时 Phase 会从 Paused 变回 Running；从已中断状态恢复则是一次新运行。
    /// 超时如实返回 false（调用方记 Unknown），**不改变迁移行为**。
    ///
    /// ★ FIX BATCH 7（RECOVERY GATE case03 现场发现）★ 旧实现把"起点是不是已暂停"写成
    ///   `var wasPaused = Phase == JobPhase.Paused;` —— 在**进入本方法的那一刻**判断。
    ///   而 ResumeAsync 从"已暂停且这一次运行已收尾"恢复时走的是一次**新运行**并且会 await 整轮运行，
    ///   于是本方法被调用时 Phase 早已跑过 Running 变成 Completed/Failed ⇒ wasPaused 当场变 false，
    ///   判据退化成 `IsRunning || Phase == Running`（运行已结束 ⇒ 恒 false）⇒ 一次**真实成功**的恢复
    ///   被记成 Unknown / resume-unsettled。实测 case03b：00:47:03.576 点恢复 → 00:47:12.178 运行真的完成，
    ///   而 footer 在 00:47:42.253 才以 `resume-unsettled / dur=38676 ms`（= 8.6 s 运行 + 30 s 空等）收口，
    ///   resume.v1 也因此永远拿不到 FeedbackConfirmed。
    ///   现在"起点阶段"由 <see cref="ResumeAsync"/> 在**发起恢复时**记下（`_resumeOriginPhase`），
    ///   本方法只负责判断"引擎有没有真的离开那个起点"。
    /// </summary>
    public async Task<bool> WaitForResumeSettledAsync(CancellationToken ct = default)
    {
        var origin = _resumeOriginPhase ?? Phase;
        var deadline = DateTime.UtcNow.AddMilliseconds(ActionSla.ResumeSettleDeadlineMs);
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                if (ResumeSettled(origin)) return true;
                try { await Task.Delay(50, ct); }
                catch (OperationCanceledException) { break; }
            }
            return ResumeSettled(origin);
        }
        finally { _resumeOriginPhase = null; }
    }

    /// <summary>恢复是否**真的发生**（唯一判据入口，绝不由"命令返回"或"请求文件被清除"代替）。</summary>
    private bool ResumeSettled(JobPhase origin)
    {
        // ① 从「已暂停」恢复：达标真值 = 引擎**离开暂停**。Running / Completed / CompletedWithErrors /
        //    Interrupted / Failed 全都是"真的继续跑过"的事实（后续状态由各自的真值面板如实呈现），
        //    而"停在 Paused"恰恰是"恢复没生效"。
        if (origin == JobPhase.Paused) return Phase != JobPhase.Paused;
        // ② 从其它可续传阶段恢复：运行已经开始，或阶段已从起点推进（例如恢复后这一轮直接跑完了）。
        return IsRunning || Phase != origin;
    }

    /// <summary>
    /// ★ FIX BATCH 3 / §6.4（同类审计）★ 停止的达标真值 = 运行**真的收尾**（不再 Running）。
    /// StopAsync 只发取消令牌就返回（`_cts?.Cancel()` 之后立即 CompletedTask），因此"命令返回"
    /// 绝不等于"已经停下" —— 这里等到引擎把运行收尾，超时如实返回 false。
    /// </summary>
    public async Task<bool> WaitForStopSettledAsync(CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ActionSla.StopSettleDeadlineMs);
        while (IsRunning && DateTime.UtcNow < deadline)
        {
            try { await Task.Delay(50, ct); }
            catch (OperationCanceledException) { break; }
        }
        var settled = !IsRunning;
        if (settled)
        {
            _lastStopTimeline?.Mark("settle-observed");
            return true;
        }
        // ★ P0-06(2) ★ 超过 SLA 仍未收尾：绝不谎称"已停止"，也绝不把界面装成"已经好了"——
        //   如实告诉用户引擎还在清理/等网络 IO（点击→此刻已过去多少毫秒也是事实），
        //   并且不解除 IsStopping（按钮继续显示"正在停止…"，不给出第二次取消的机会）。
        _lastStopTimeline?.Mark("settle-timeout");
        var waited = _lastStopTimeline?.TotalMs ?? ActionSla.StopSettleDeadlineMs;
        SetOperational($"停止尚未落定：引擎仍在清理或等待网络 I/O（已等待 {waited} ms）——尚未真正停止，请不要关机；其它控件仍可操作。");
        _log.Warning("停止在 SLA 内未落定，仍在收尾（已等待 {Waited} ms）", waited);
        return false;
    }

    /// <summary>是否**真的**已暂停（引擎已确认 worker 停止）。不是"点了暂停按钮"。</summary>
    public bool IsPaused => PauseUiState == PauseUiState.Paused;
    /// <summary>正在暂停：请求已写入（或引擎已受理），**尚未**确认停住。界面必须显示"正在暂停…"。</summary>
    public bool IsPausing => PauseUiState == PauseUiState.Pausing;
    /// <summary>暂停失败：引擎在硬失败 SLA 内没能停住 worker，**迁移仍在进行**（必须明说）。</summary>
    public bool IsPauseFailed => PauseUiState == PauseUiState.PauseFailed;

    private bool _isRepairing;
    /// <summary>「尝试修复」独立进行中（修复有自己的一条进度条，不与迁移混用）。</summary>
    public bool IsRepairing
    {
        get => _isRepairing;
        private set { if (Set(ref _isRepairing, value)) RaiseDerived(); }
    }

    private bool _isFinished;
    /// <summary>
    /// 本次传输/修复**流程**是否已结束（流程语义：Completed 与 CompletedWithErrors 都算结束）。
    /// ★ A.5（P1-3）：它**只表示"流程结束"，绝不表示"进度已满"**——百分比另由
    /// <see cref="Percent"/> 表达：只有真正的 <see cref="JobPhase.Completed"/> 才允许 100%，
    /// CompletedWithErrors 一律显示 Core 的实际完成比例（见 <see cref="ActualPercent"/>）。
    /// </summary>
    public bool IsFinished { get => _isFinished; private set => Set(ref _isFinished, value); }

    private string _statusMessage = "等待 Step 1 连接旧电脑；连接完成并在 Step 2 选定内容后，这里会给出可执行的下一步。";
    /// <summary>给用户看的一句话状态（所有动作的结论都落在这里，不静默失败）。
    /// ★ FIX BATCH 6（§9）★ 它现在是**聚合句**：总是等于最近一次写入通道的完整文本；
    /// 分流后的语义通道见 <see cref="OperationalStatus"/> 等五个属性，UI 面板按通道取用。</summary>
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }

    // ── ★ FIX BATCH 6（指令 §9）：状态消息路由 ────────────────────────────────
    //   唯一写入者仍是本 ViewModel：所有状态句经 SetXxx/AppendXxx 进入对应语义通道，
    //   UI 只读通道（禁止在 View 里拼业务状态）。
    //     · OperationalStatus   运行/流程状态        → 左侧提示面板
    //     · UserHint            用户操作引导          → 左侧提示面板
    //     · SecurityHint        凭据/账号/共享安全    → 「连接与安全提示」卡
    //     · ErrorSummary        失败/拦下/异常摘要    → 左侧提示面板
    //     · CurrentObjectStatus 当前对象（引擎真值）  → 左侧提示面板
    //   ExecutionStatus 是**右侧执行卡专用短句**：截到第一句并限长，长文本一律归左侧面板，
    //   从而不再让一根字符串把执行卡无限撑高（§9）。

    /// <summary>右侧执行卡短句的字符上限；超出即截断（完整文本仍在 <see cref="StatusMessage"/> 与左侧面板）。</summary>
    public const int ExecutionStatusMaxChars = 96;

    private string _operationalStatus = string.Empty;
    /// <summary>运行/流程状态（引擎与流程真值）。</summary>
    public string OperationalStatus { get => _operationalStatus; private set => Set(ref _operationalStatus, value); }

    private string _userHint = string.Empty;
    /// <summary>用户操作引导（下一步该做什么）。</summary>
    public string UserHint { get => _userHint; private set => Set(ref _userHint, value); }

    private string _securityHint = string.Empty;
    /// <summary>安全相关提示（凭据 / 账号 / 共享可用性）。</summary>
    public string SecurityHint { get => _securityHint; private set => Set(ref _securityHint, value); }

    private string _errorSummary = string.Empty;
    /// <summary>错误摘要（失败 / 被拦下 / 异常）。</summary>
    public string ErrorSummary { get => _errorSummary; private set => Set(ref _errorSummary, value); }

    private string _currentObjectStatus = string.Empty;
    /// <summary>当前对象状态（来自引擎的进度快照，不是 UI 自己推的）。</summary>
    public string CurrentObjectStatus { get => _currentObjectStatus; private set => Set(ref _currentObjectStatus, value); }

    private string _checkSummary = string.Empty;
    /// <summary>
    /// ★ 真机问题 2（2026-10-21）★ **检查结论**通道：只回答"检查过没有、通过没通过、有几条非阻断提醒"。
    ///
    /// 为什么必须是独立通道、且**不走** <see cref="SetChannel"/>：
    ///   · <see cref="SetChannel"/> 会同步覆盖 <c>StatusMessage</c> 与右侧执行卡短句 —— 而"检查结论"
    ///     是一个持续存在的结论，不该顶掉"正在扫描/正在传输"这类阶段句；
    ///   · 任务令 §5.1 要求三个维度分开：操作阶段（OperationalStatus）、检查结论（本属性）、
    ///     问题级别（ErrorSummary / 本属性的提醒数）。旧实现把它们拼成一句长句，
    ///     于是出现「预检通过 + Access is denied + 正在扫描 + 暂无报错」四种声音同屏。
    /// </summary>
    public string CheckSummary { get => _checkSummary; private set => Set(ref _checkSummary, value); }

    private int _checkNoticeCount;
    /// <summary>当前实际存在的**非阻断提醒**条数（= 未通过的 Warning 检查项；通过的检查永不计入）。</summary>
    public int CheckNoticeCount { get => _checkNoticeCount; private set => Set(ref _checkNoticeCount, value); }

    /// <summary>写入检查结论（唯一入口）。<paramref name="notices"/> 只统计**实际失败**的非阻断提醒。</summary>
    private void SetCheckSummary(string? summary, int notices)
    {
        CheckNoticeCount = notices < 0 ? 0 : notices;
        CheckSummary = summary ?? string.Empty;
    }

    /// <summary>
    /// ★ Preview.4 真机问题 1（2026-10-22）★ 预检写下的 UserHint 是**行动号召**（"可以开始迁移"），
    /// 它只在"尚未开始迁移"时成立。一旦进入实质阶段（Running）必须撤下，否则会与迁移状态长期并存。
    ///
    /// 边界（不得扩大）：
    ///   · 只清 UserHint（行动号召），**不清** <see cref="CheckSummary"/>（结论 + 提醒数）——
    ///     预检发现的历史提醒因此不会被删除，仍可在状态行与日志里核对；
    ///   · 不清 ErrorSummary / SecurityHint：那两路与"要不要开始"无关；
    ///   · 幂等：没写过预检提示时什么也不做（不影响暂停/恢复/重试等其它入口写入的 UserHint）。
    /// </summary>
    private void RetirePreflightCallToAction()
    {
        if (!_preflightHintActive) return;
        _preflightHintActive = false;
        // 直接写通道属性，**不走** SetChannel：SetChannel 会顺带把 StatusMessage / 执行卡短句
        // 改成这条文本（清空就变成空串），而那两处应当继续显示当前阶段句。
        UserHint = string.Empty;
    }

    /// <summary>预检阶段是否写下过"可以开始迁移"这类行动号召（只有它需要被实质阶段撤下）。</summary>
    private bool _preflightHintActive;

    /// <summary>
    /// 检查结论 → 一句话（供状态行/提示卡）。语义固定：
    ///   · 有阻断错误 ⇒ 由 <see cref="ErrorSummary"/> 承担，本方法返回空（避免两处说同一件事）；
    ///   · 通过 + N 条提醒 ⇒「检查通过 · N 条提醒」；
    ///   · 通过 + 0 条提醒 ⇒「检查通过 · 无提醒」。
    /// 纯函数 ⇒ 可被单元测试直接覆盖。
    /// </summary>
    internal static string DescribeCheckSummary(bool overallPass, int notices)
        => !overallPass ? string.Empty
            : notices > 0 ? $"检查通过 · {notices} 条提醒"
            : "检查通过 · 无提醒";

    private string _executionStatus = "等待 Step 1 连接旧电脑；连接完成并在 Step 2 选定内容后，这里会给出可执行的下一步。";
    /// <summary>右侧执行卡显示的**短句**（第一句 + 限长）；细节在左侧提示面板。</summary>
    public string ExecutionStatus { get => _executionStatus; private set => Set(ref _executionStatus, value); }

    /// <summary>把状态句写入指定语义通道（同时更新聚合句与执行卡短句）。
    /// 例外：<see cref="StatusChannel.CurrentObjectStatus"/> 是**每秒刷新的对象信息**，
    /// 不覆盖聚合句/执行卡短句（否则运行中每帧都会把运行状态句顶掉）。</summary>
    private void SetChannel(StatusChannel channel, string? text)
    {
        text ??= string.Empty;
        switch (channel)
        {
            case StatusChannel.OperationalStatus: OperationalStatus = text; break;
            case StatusChannel.UserHint: UserHint = text; break;
            case StatusChannel.SecurityHint: SecurityHint = text; break;
            case StatusChannel.ErrorSummary: ErrorSummary = text; break;
            case StatusChannel.CurrentObjectStatus: CurrentObjectStatus = text; break;
        }
        if (channel == StatusChannel.CurrentObjectStatus) return;
        _statusMessage = text;
        Raise(nameof(StatusMessage));
        ExecutionStatus = Shorten(text);
    }

    /// <summary>向已有通道句追加说明（空间守护 / 锁与 EFS / 超大规模等），不新开通道。</summary>
    private void AppendChannel(StatusChannel channel, string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var current = channel switch
        {
            StatusChannel.OperationalStatus => OperationalStatus,
            StatusChannel.UserHint => UserHint,
            StatusChannel.SecurityHint => SecurityHint,
            StatusChannel.ErrorSummary => ErrorSummary,
            _ => CurrentObjectStatus,
        };
        SetChannel(channel, current + text);
    }

    private void SetOperational(string? text) => SetChannel(StatusChannel.OperationalStatus, text);
    private void SetUserHint(string? text) => SetChannel(StatusChannel.UserHint, text);
    private void SetSecurityHint(string? text) => SetChannel(StatusChannel.SecurityHint, text);
    private void SetErrorSummary(string? text) => SetChannel(StatusChannel.ErrorSummary, text);
    private void AppendOperational(string? text) => AppendChannel(StatusChannel.OperationalStatus, text);

    /// <summary>执行卡短句：压平换行、截到第一句、超长截断（左侧面板仍显示完整文本）。</summary>
    internal static string Shorten(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
        var cut = flat.IndexOf('。');
        if (cut >= 0) flat = flat[..(cut + 1)];
        if (flat.Length > ExecutionStatusMaxChars) flat = flat[..ExecutionStatusMaxChars].TrimEnd() + "…";
        return flat;
    }

    public string JobIdText => Ctx?.JobId ?? "（尚无任务）";

    /// <summary>有计划（且计划里有对象）才谈得上执行/验证。</summary>
    public bool HasPlan => Ctx?.Plan is { Objects.Count: > 0 };
    public bool HasJob => Ctx is not null;

    /// <summary>可"开始迁移"：不在跑、有计划、有目标根。</summary>
    public bool CanStart => !IsRunning && !IsVerifying && HasPlan && !string.IsNullOrWhiteSpace(TargetRoot);

    private static readonly JobPhase[] ResumablePhases =
        [JobPhase.Running, JobPhase.Paused, JobPhase.Interrupted, JobPhase.AwaitingReview, JobPhase.CompletedWithErrors];

    /// <summary>可"恢复任务"（阶段集合与 Core 的 JobManager.ResumablePhases 同口径）。
    /// ★ FIX BATCH 2 ★ 两条独立通路：
    ///   ① 引擎**已确认暂停**（PauseUiState == Paused）⇒ 恢复必须可用，**即使那一次运行还活着**
    ///      （暂停达成于对象边界时，编排器会原地等请求文件消失，IsRunning 仍为 true）；
    ///   ② 运行已结束、阶段可续传（Interrupted / CompletedWithErrors …）⇒ 启动新一次运行。
    ///   "正在暂停中"一律禁用：此刻用户的两次相反意图会叠在一起。</summary>
    public bool CanResume => Ctx is not null
        && !IsVerifying
        && PauseUiState != PauseUiState.Pausing
        && (PauseUiState == PauseUiState.Paused || (!IsRunning && ResumablePhases.Contains(Phase)));

    private bool _hasVerifyReport;
    /// <summary>可"尝试修复"：有任务且（有验证报告 / 有失败对象 / 上次完成但有错）。</summary>
    public bool CanRepair => !IsRunning && !IsVerifying && Ctx is not null
        && (_hasVerifyReport || FailedObjects > 0 || Phase is JobPhase.CompletedWithErrors or JobPhase.Interrupted);

    /// <summary>可暂停（FIX BATCH 2 按钮矩阵）：运行中、且不在"已暂停 / 正在暂停"两个状态下。
    /// 暂停失败时**重新可用**（文案变成「重试暂停」）——用户必须有一条重试的路。</summary>
    public bool CanPause => IsRunning && PauseUiState is PauseUiState.Idle or PauseUiState.PauseFailed;
    /// <summary>可停止（运行中）。"正在暂停中"也允许停止：用户仍需要一条能真正结束的路。</summary>
    public bool CanStop => IsRunning && !IsStopping;

    /// <summary>暂停按钮文案（状态机真值 ⇒ 文案，不反过来）。</summary>
    public string PauseButtonText => PauseUiState switch
    {
        PauseUiState.Pausing => "正在暂停…",
        PauseUiState.PauseFailed => "重试暂停",
        PauseUiState.Paused => "已暂停",
        _ => "暂停",
    };

    /// <summary>
    /// 暂停状态的**唯一权威显示句**（BATCH 6 状态路由把它送进左栏运行状态主通道）。
    /// 三句话分别对应三种真实情况，绝不含糊：
    ///   · 正在暂停…（请求已受理，引擎尚未确认停住）
    ///   · 已暂停（引擎确认 worker 停止；含达成时刻）
    ///   · 暂停失败，迁移仍在进行（硬失败 SLA 内没停住）
    /// </summary>
    public string PauseStateText => PauseUiState switch
    {
        PauseUiState.Pausing => "⏳ 正在暂停…（请求已送达引擎，正在中止当前对象的传输）",
        PauseUiState.Paused when _enginePauseAchievedAt is { } at =>
            $"‖ 已暂停（{at.ToLocalTime():HH:mm:ss} 达成）：传输已停止，当前对象未完成，点「恢复任务」继续。",
        PauseUiState.Paused => "‖ 已暂停：传输已停止，当前对象未完成，点「恢复任务」继续。",
        PauseUiState.PauseFailed =>
            "⚠ 暂停失败，迁移仍在进行：" + (_enginePauseFailureReason ?? "引擎未能在 10 秒内停止当前对象的传输") +
            "。可点「重试暂停」，或点「停止」。",
        PauseUiState.Resuming => "▶ 正在恢复…（已清除暂停请求，正在重建传输）",
        _ => string.Empty,
    };

    // ────────────────────────── 显示位（Step3 的真实来源）──────────────────────────

    private string _progressText = "0 B / 0 B";
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }

    private string _objectText = "0/0";
    public string ObjectText { get => _objectText; private set => Set(ref _objectText, value); }

    private string _planBytesText = "0 B";
    public string PlanBytesText { get => _planBytesText; private set => Set(ref _planBytesText, value); }

    private string _actualBytesText = "0 B";
    public string ActualBytesText { get => _actualBytesText; private set => Set(ref _actualBytesText, value); }

    private string _dataLabel = "已传 / 计划";
    public string DataLabel { get => _dataLabel; private set => Set(ref _dataLabel, value); }

    // ── ★ P0-04 ★ 进度 vs 结果的语义分离 ────────────────────────────────────────────
    // 现场症状："修复的进度条跑到 100%，可最后验证还是失败" —— 用户据此以为"已经修好了"。
    // 根因不是算术错，而是**一个百分比在讲两件事**：它测的是"重拷/传输干了多少活"，
    // 却被读成"这次修复成功了"。因此这里把语义显式拆开：
    //   · Percent / DataLabel  → 只表达"重拷（传输）干了多少"，标签必须写清它测的是什么；
    //   · ProgressNeedsAttention → 复验没过时的**醒目位**（百分比/进度条由页面按它变色）；
    //   · StatusMessage        → 唯一说"结果"的地方，且必须写明"100% 只代表重拷完成"。
    // 只有"修复重拷完 + 复验通过"才算这次修复达成；复验未通过时进度宁可显示 100% 也不许说成功。
    private bool _progressNeedsAttention;
    /// <summary>本次修复的近况是否为"需要用户注意"（复验未通过）。页面据此把百分比/进度条转为警示色。</summary>
    public bool ProgressNeedsAttention
    {
        get => _progressNeedsAttention;
        private set { if (Set(ref _progressNeedsAttention, value)) Raise(nameof(ProgressNeedsAttention)); }
    }

    /// <summary>
    /// ★ P0-04 ★ 收尾态是否已封版：一旦写出终局结论，**后到的旧快照一律不许再改显示位**
    /// （现场"100% 之后又跳回 87%"、"结果页结论被旧的运行中快照覆盖"都由这类过期回调造成）。
    /// 封版只由"新一次运行/换任务"解除（见 <c>ResetRunProjection</c>）。
    /// </summary>
    private bool _progressSealed;

    private int _postFinishSnapshotsIgnored;
    /// <summary>收尾后被拒绝应用的过期快照条数（诊断用；非 0 说明确有旧回调在追，绝不是"没发生"）。</summary>
    public int PostFinishSnapshotsIgnored => _postFinishSnapshotsIgnored;

    private string _engineSpeedText = "—";
    public string EngineSpeedText { get => _engineSpeedText; private set => Set(ref _engineSpeedText, value); }

    private string _etaText = "—";
    public string EtaText { get => _etaText; private set => Set(ref _etaText, value); }

    // ────────────────────────── ★ 2026-10-08（真机问题 5）★ 真实网卡接收吞吐 ──────────────────────────
    //
    // 【为什么必须与进度真值分开】旧口径 CumulativeSpeed = "本轮新增**逻辑完成**字节 ÷ 有效运行时间"：
    //   目标端已存在的文件被 Robocopy 快速 Skip（逻辑上瞬间完成）时**根本没经过网卡**，于是真机上
    //   出现数百 MB/s ~ GB/s（32 MB 预检文件 2 秒读完 ⇒ 约 4 GB/s）这类物理上不可能的值。
    //   本块是**物理观测**：读真实网卡的 InOctets 计数器增量，只服务"界面实时速度 + ETA"。
    // 【边界（不可越界）】Progress Truth / CompletedBytes / Receipt / Verifier 一概不读本块结果，
    //   本块也绝不回写它们 —— 两者是并行指标，不是替代关系。
    private NetworkThroughputSampler? _networkThroughput;

    /// <summary>
    /// ★ 2026-10-08 Preview.2 ★ 上一次喂给采样器的**终态**标记。用途只有一件：识别"终态 → 运行"的恢复沿，
    /// 在那条沿上丢掉停摆期间攒下的采样窗口（否则恢复后头几拍会把背景流量均值当传输速度）。
    /// </summary>
    private bool _networkTerminal;

    private static readonly NetworkThroughputSnapshot NetworkThroughputPlaceholder =
        NetworkThroughputSnapshot.Initial("网卡吞吐观测尚未启动");

    /// <summary>当前网卡接收吞吐快照（唯一来源：周期采样真实网卡计数器；未启动 ⇒ 连接中且显示 "—"）。</summary>
    public NetworkThroughputSnapshot NetworkThroughput
        => _networkThroughput?.Snapshot ?? NetworkThroughputPlaceholder;

    /// <summary>实时速度文本（"—" / "0 B/s" / 真实速率）。底栏与 Step3 统计卡共用。</summary>
    public string NetworkSpeedText => NetworkThroughput.SpeedText;

    /// <summary>平滑 ETA 文本（"—" 或剩余逻辑字节 ÷ 平滑网络吞吐）。</summary>
    public string NetworkEtaText => NetworkThroughput.EtaText;

    /// <summary>
    /// ★ Preview.4 真机问题 2（2026-10-22）★ 判定"确实在传输"的网卡速率下限（256 KB/s）。
    /// 取值理由：低于这个量级只可能是背景流量/ARP/心跳，不足以证明"迁移正在搬数据"。
    /// </summary>
    public const double TransferActivityFloorBytesPerSecond = 256 * 1024;

    /// <summary>最近一帧快照里，已确认显示字节是否比高水位更大（小文件通道的活性证据）。</summary>
    private bool _bytesAdvancing;

    /// <summary>当前网卡接收速率（无读数 ⇒ NaN；调用方必须自己判 NaN）。</summary>
    public double NetworkReceiveBytesPerSecond
    {
        get
        {
            var snap = NetworkThroughput;
            if (snap.Paused) return double.NaN;
            if (snap.HasSpeed && !double.IsNaN(snap.EmaBytesPerSecond)) return snap.EmaBytesPerSecond;
            return snap.StableBytesPerSecond;
        }
    }

    /// <summary>
    /// ★ Preview.4 真机问题 2（2026-10-22）★ **底栏源连接灯"绿呼吸"的唯一判据**。
    ///
    /// 【上一版为什么实机仍然黄】判据是"已显示字节增长"，但 `/J` 大文件通道下目标文件被预分配，
    /// 引擎日志明写「本通道只使用 robocopy 已确认的输出」⇒ 一个 20 GB 文件传 3 分钟，
    /// committed 字节一次都不涨 ⇒ 60 秒证据过期 ⇒ 琥珀。实机：JOB-20261022-094322-42ea 92 MB/s 仍在黄灯。
    ///
    /// 【本版】两条证据**任一成立**即可：
    ///   ① 物理观测：网卡接收吞吐 ≥ <see cref="TransferActivityFloorBytesPerSecond"/>（数据真的在流）；
    ///   ② 引擎确认：已显示字节比本趟高水位更大（小文件通道）。
    /// 反向条件（全部是否决项，保证不伪造）：非 Running / 正在暂停 / 已暂停 / 正在停止 / 采样器自身标记暂停。
    /// </summary>
    public bool HasLiveTransferActivity
    {
        get
        {
            if (Phase != JobPhase.Running) return false;
            if (PauseUiState is PauseUiState.Pausing or PauseUiState.Paused) return false;
            if (IsStopping) return false;

            var rate = NetworkReceiveBytesPerSecond;
            if (!double.IsNaN(rate) && rate >= TransferActivityFloorBytesPerSecond) return true;

            // 网卡无读数时退到引擎字节判据；两者都不成立 ⇒ 不亮呼吸灯（宁可不绿也不伪造）。
            return _bytesAdvancing;
        }
    }


    /// <summary>
    /// 惰性启动网卡吞吐观测（幂等）。目标地址解析**放后台线程**（绝不阻塞 UI）；
    /// 解析不到就保持"连接中 ⇒ —"，绝不编造数字。
    /// </summary>
    private void EnsureNetworkThroughput()
    {
        if (_networkThroughput is not null) return;
        var host = Host;
        if (string.IsNullOrWhiteSpace(host)) return;

        var sampler = new NetworkThroughputSampler(null, log: _log);
        _networkThroughput = sampler;
        sampler.Start();
        _log.Information("网卡吞吐观测已启动：目标 {Host}", host);

        _ = Task.Run(() =>
        {
            try
            {
                var addresses = Dns.GetHostAddresses(host)
                    .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    .ToArray();
                sampler.UpdateDestinations(addresses);
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "网卡吞吐：解析目标主机 {Host} 失败（保持不可用）", host);
            }
        });
    }

    /// <summary>
    /// 把"暂停 / 剩余逻辑字节 / 是否终态"喂给采样器（ETA 的被除数**只**来自进度真值，单向输入）。
    /// 终态（完成 / 完成但有错 / 中断 / 失败 / 取消）必须显式告知采样器：任务结束时剩余逻辑字节会冻结在
    /// &gt;0，而采样器只看得到网卡计数器 —— 不告知就会一直算出"预计剩余若干小时"这种假 ETA（真机问题 6）。
    /// 线程纪律：本方法只写采样器的输入字段，读快照由 UI 线程做。
    /// </summary>
    private void SyncNetworkThroughputInputs(bool paused, long remainingLogicalBytes, bool terminal)
    {
        var sampler = _networkThroughput;
        if (sampler is null) return;

        // "终态 → 运行"的恢复沿（用户点「恢复任务」/重试）：丢掉停摆期间的窗口与基线。
        if (_networkTerminal && !terminal) sampler.ResumeObservation();

        sampler.MarkPaused(paused);
        sampler.SetRemainingLogicalBytes(remainingLogicalBytes);
        sampler.MarkTerminal(terminal);
        _networkTerminal = terminal;
    }

    /// <summary>
    /// 终态判定（**唯一**口径）：这些阶段之后不存在"还要多久"，速度与 ETA 一律按终态显示。
    /// 与 <c>ResumablePhases</c> 的口径刻意分开：AwaitingReview / Paused 都还能继续跑，不算终态。
    /// </summary>
    private static bool IsTerminalPhase(JobPhase phase)
        => phase is JobPhase.Completed
            or JobPhase.CompletedWithErrors
            or JobPhase.Interrupted
            or JobPhase.Failed
            or JobPhase.Canceled;

    /// <summary>
    /// ★ FIX BATCH 4（§7.1）★ 引擎最后一次送来的进度真值（顶层/底栏进度条、百分比、字节、速率、ETA
    /// 的唯一来源）。null = 尚无引擎快照（启动前/测试路径）。
    /// </summary>
    private ProgressTruthSnapshot? _lastTruth;
    public ProgressTruthSnapshot? LastTruth { get => _lastTruth; private set => Set(ref _lastTruth, value); }

    /// <summary>
    /// ★ Round-2 2026-10-05（§2.1）★ **UI 显示真值**（顶栏/底栏/百分比/字节的唯一来源）。
    /// 与 <see cref="LastTruth"/> 的唯一区别：暂停 / 停止 / 恢复的正常连续性窗口内可能被
    /// <see cref="ContinuationDisplayState"/> 抬到"用户已看到的高水位"（引擎 raw 追上后自动解除）；
    /// <see cref="LastTruth"/> 永远保留引擎 raw 真值供诊断与解释，二者绝不互相污染。
    /// </summary>
    private ProgressTruthSnapshot? _presentationTruth;
    public ProgressTruthSnapshot? PresentationTruth { get => _presentationTruth; private set => Set(ref _presentationTruth, value); }

    // ── ★ Round-2 2026-10-05（§2.1）★ 显示连续性状态机（说明见文件末尾 ContinuationDisplayState）──
    //   唯一写入者：ApplySnapshot（Apply）、PauseAsync/StopAsync（用户动作**之前**捕获检查点）、
    //   ResumeAsync（进入追赶）、RunTransferCoreAsync（非恢复的运行 ⇒ Clear）。
    private readonly ContinuationDisplayState _continuation = new();
    /// <summary>最近一次快照里的"已完成对象数"（检查点用；只读镜像，**不是**业务权威）。</summary>
    private int _completedObjectsSeen;

    /// <summary>★ 只读诊断 ★ 显示连续性是否生效（冻结/追赶中 = true；日志据此解释"显示为何高于引擎 raw"）。</summary>
    public bool ContinuationActive => _continuation.IsActive;
    /// <summary>★ 只读诊断 ★ 当前高水位（用户已经看到的进度分子）。</summary>
    public long ContinuationHighWaterBytes => _continuation.HighWaterBytes;
    /// <summary>★ 只读诊断 ★ 当前连续性模式（Live / Frozen / CatchingUp）。</summary>
    internal ContinuationMode ContinuationDisplayMode => _continuation.Mode;

    // ── ★ v0.5.4 Preview.2（P1-A 返修，2026-10-15）★ 飞行中字节"有无可信来源"的低频派生位 ──
    //   存在的理由（必须读）：Step 3 的诚实状态句需要知道"本刻有没有可信的中途字节来源"，
    //   但它的原始判据 LastTruth 每帧都变（ApplySnapshot:3784）⇒ 若直接订阅 LastTruth，
    //   按 PageReadiness L48-51 的既有纪律会把四个页面的文案广播刷成高频风暴。
    //
    //   因此这里只暴露一个**只在真正翻转时通知**的派生位：传输期间（状态一直是"无来源"或
    //   一直是"有来源"）它**不发通知**，只有从一种解释翻到另一种时才对。Change() 是朴素的
    //   相等判断，不引入任何计时器/插值/速率换算。
    //
    //   语义边界：true 表示"本帧存在已确认的中途字节来源"（如 WorkerIoCounters 进程 I/O 计数器）
    //   ⇒ UI **不得**再显示"暂无可确认进度"。判据与 ProgressTruthSnapshot.Create:120-122 同源。
    private bool _hasTrustedInflightSource;
    /// <summary>
    /// ★ v0.5.4 Preview.2（P1-A）★ 本帧是否存在**已确认的中途字节来源**。
    /// false 时只说明"本刻缺数据"，**不**蕴含"仍在传输"或"不是卡死"。
    /// </summary>
    public bool HasTrustedInflightSource => _hasTrustedInflightSource;

    /// <summary>
    /// 由 <see cref="ApplySnapshot"/> 调用，把真值帧换算成上面的低频派生位。
    /// 只在翻转时发通知（传输期间同一状态不发）⇒ 不会造成文案刷新风暴。
    /// 快照缺失（LastTruth 为 null）时**保守取 false**，UI 据此退回原有文案，不臆造状态。
    /// </summary>
    private void SyncInflightSourceFlag()
    {
        var truth = _lastTruth;
        var has = truth is not null
                  && truth.InFlightSource != ProgressTruthSource.None
                  && truth.InFlightConfirmedBytes > 0;
        if (_hasTrustedInflightSource == has) return;
        _hasTrustedInflightSource = has;
        Raise(nameof(HasTrustedInflightSource));
    }

    // ── ★ Round-2 2026-10-05（§3.2 / §3.3）★ 进度**呈现**时间线 ──
    //   唯一写入者：ApplySnapshot（ApplyTruth / Freeze）、PauseAsync·StopAsync（经 CaptureContinuationCheckpoint
    //   冻结）、ResumeAsync（ResumeLive）、RunTransferCoreAsync（Reset）。
    //   唯一读取者：Step3ProgressPage / MainWindow 的 80 ms 呈现定时器（AdvancePresentation）——
    //   大号百分比文本与蓝色进度条都从这里取同一个视觉比例。
    private readonly ProgressPresentationCoordinator _presentation = new();

    /// <summary>★ 只读 ★ UI 以 10~20 Hz 推进共享呈现时间线并取回视觉百分比（数字与进度条共用的唯一值）。</summary>
    public double AdvancePresentation(DateTime utcNow) => _presentation.Advance(utcNow);

    /// <summary>★ 只读 ★ 当前视觉百分比（调用 <see cref="AdvancePresentation"/> 之后的值）。</summary>
    public double PresentationVisualPercent => _presentation.VisualPercent;

    /// <summary>★ 只读 ★ 当前视觉分子（按视觉百分比折算，用于让字节文本与百分比同源）。</summary>
    public long PresentationVisualBytes => _presentation.VisualBytes;

    /// <summary>★ 只读 ★ 视觉落后"已确认显示真值"的百分点（&gt;0 表示正在追赶，应当逐帧移动）。</summary>
    public double PresentationLagPercent => _presentation.LagPercent;

    /// <summary>★ 只读 ★ 视觉是否落后于已知真值（落后期间必须"大多数采样帧都在动"，不许长时间静止）。</summary>
    public bool PresentationIsLagging => _presentation.IsLagging;

    /// <summary>★ 只读诊断 ★ 呈现时间线内部状态描述。</summary>
    public string PresentationDescribe => _presentation.Describe();

    /// <summary>★ 只读 ★ 最近一次呈现补间的时长（秒）—— 供进度条按**同一曲线**补间，数字与条因此同源同步。</summary>
    public double PresentationSpanSeconds => _presentation.LastDurationSeconds;

    private int _failedObjects;
    public int FailedObjects
    {
        get => _failedObjects;
        private set { if (Set(ref _failedObjects, value)) Raise(nameof(CanRepair)); }
    }

    private string _currentObjectPath = string.Empty;
    public string CurrentObjectPath { get => _currentObjectPath; private set => Set(ref _currentObjectPath, value); }

    private double _stallSeconds;
    public double StallSeconds { get => _stallSeconds; private set => Set(ref _stallSeconds, value); }

    private string _currentFileText = "—";
    /// <summary>最近开始复制的文件（实时文件流的"当前一条"）。</summary>
    public string CurrentFileText { get => _currentFileText; private set => Set(ref _currentFileText, value); }

    private string _currentObjectDetail = string.Empty;
    public string CurrentObjectDetail { get => _currentObjectDetail; private set => Set(ref _currentObjectDetail, value); }
    public bool HasCurrentObjectDetail => !string.IsNullOrEmpty(CurrentObjectDetail);

    private string _activeObjectName = string.Empty;
    /// <summary>
    /// ★ 真机问题 3（2026-10-21）★ 当前正在传输的**对象名**（= 当前对象路径的最后一段）。
    ///
    /// 为什么需要它：大文件通道（<c>/J /MT /NP</c>）下一个 17~20 GB 的 PST 要传几十分钟，
    /// 而 <see cref="LiveFiles"/> 只在**文件复制完成**时才追加一条 ⇒ 此期间文件流列表恒为空。
    /// Step3 右栏过去只认 <c>LiveFiles.Count</c>，于是显示「当前没有正在复制的文件。」——
    /// 与事实（正在传输 103 GiB 对象）正面冲突。对象名 + 对象级字节进度是**诚实且已有**的替代证据。
    /// </summary>
    public string ActiveObjectName { get => _activeObjectName; private set => Set(ref _activeObjectName, value); }

    private string _activeObjectProgressText = string.Empty;
    /// <summary>
    /// ★ 真机问题 3 ★ 当前对象的**真实字节进度**（已传 / 对象总量），来自引擎进度快照的
    /// <c>CurrentObjectDoneBytes / CurrentObjectTotalBytes</c>，不是从文件列表反推的。
    /// </summary>
    public string ActiveObjectProgressText { get => _activeObjectProgressText; private set => Set(ref _activeObjectProgressText, value); }

    private string _stallHintText = string.Empty;
    public string StallHintText { get => _stallHintText; private set => Set(ref _stallHintText, value); }
    public bool HasStallHint => StallHintText.Length > 0;

    private string _balanceText = "计划 0 B　·　实际落盘 0 B";
    /// <summary>计划量 / 实际落盘量对账（失败对象已拷进去的部分也算落盘，避免显示成"少传了 N GB"）。</summary>
    public string BalanceText { get => _balanceText; private set => Set(ref _balanceText, value); }

    /// <summary>工作区通用提示（PageReadiness.WorkspaceHint 的数据来源，避免各页各写）。</summary>
    public string WorkspaceHint => $"源：{SourceSummary}｜目标：{TargetSummary}";

    // ────────────────────────── 集合 ──────────────────────────

    /// <summary>计划对象清单（对象号 + 状态）。</summary>
    public ObservableCollection<SessionObjectRow> Objects { get; } = new();

    /// <summary>正在复制的文件（滚动窗口，上限 <see cref="LiveFileCapacity"/> 条）。</summary>
    public ObservableCollection<string> LiveFiles { get; } = new();

    /// <summary>失败 / 不一致清单（对象级失败逐条列出）。</summary>
    public ObservableCollection<SessionFailItem> FailItems { get; } = new();

    /// <summary>已有任务列表（Step2/4 的"未完成任务"区域用）。</summary>
    public ObservableCollection<JobSummary> ExistingJobs { get; } = new();

    private JobSummary? _pendingResumeCandidate;

    /// <summary>
    /// 最近一次检测到、尚未被用户确认续传的任务。
    ///
    /// ★ A.5（P2-6）★ 本属性**必须**发通知（改前是无 <c>Raise</c> 的自动属性）：
    /// 读取点只有 Step4（<c>ToolbarResume.IsEnabled</c> 与点击取候选两处，见 Step4ResultPage.xaml.cs）；
    /// 改前 Shell 只能靠在探测之后手工调一次 <c>PageResult.RefreshFromSession()</c> 来补刷新，
    /// 一旦"旧探测结果覆盖 PendingResumeCandidate"，那一次手工刷新就把「恢复任务」指到了错任务上。
    ///
    /// ⚠ 为什么不能只依赖 <see cref="UnfinishedProbeText"/> 的通知：它的 <c>Set(...)</c> 在**文案没变时不 Raise**，
    /// 于是"文案相同、候选不同"（同一 host 两次探测返回不同 JobSummary 实例）这种情形下按钮刷不到。
    /// 这里按**引用**判等：Core 每次 <c>FindUnfinished</c> 都返回新实例 ⇒ 候选一有新结论就必然通知。
    /// </summary>
    public JobSummary? PendingResumeCandidate
    {
        get => _pendingResumeCandidate;
        private set
        {
            if (ReferenceEquals(_pendingResumeCandidate, value)) return;
            _pendingResumeCandidate = value;
            Raise(nameof(PendingResumeCandidate));
        }
    }

    /// <summary>实时日志行的滚动上限（超过就丢最旧的一条，避免长时间运行把内存撑爆）。</summary>
    public const int LogLineCapacity = 200;

    /// <summary>
    /// 「实时日志」面板（Step4）的数据源：每条都是**真实事件**——Core 引擎的每行输出 + 本次会话的真实动作结论。
    /// 只在 UI 线程增删（<see cref="Log"/> 一律经 <see cref="Post"/> 排队），ListView 自己跟随，页面不缓存副本。
    /// </summary>
    public ObservableCollection<SessionLogLine> LogLines { get; } = new();

    /// <summary>
    /// 应用级日志目录的**真实路径**（唯一来源：Core 的 <see cref="LogBootstrap.AppLogDir"/>，
    /// 实测解析为 %ProgramData%\PCMig\Logs）。页面「打开日志目录」按钮用它，不另抄一份路径。
    /// </summary>
    public static string AppLogDir => LogBootstrap.AppLogDir;

    private string _unfinishedProbeText = string.Empty;
    /// <summary>启动探测「未完成任务」的**如实结论**（未连接 / 未匹配到 / 匹配到几个，原文照说，不做推测）。</summary>
    public string UnfinishedProbeText { get => _unfinishedProbeText; private set => Set(ref _unfinishedProbeText, value); }

    // ────────────────────────── 传输执行：测试缝（生产路径不使用）──────────────────────────

    private TransferRunnerDelegate? _transferRunner;

    /// <summary>
    /// ★ 仅测试缝 ★ —— 传输执行的假实现注入点。
    ///
    /// 边界（不可越界）：
    ///   · **默认（null）= 生产路径，直接使用 Core 的唯一传输引擎**（见 RunTransferCoreAsync 的引擎构造块）；
    ///   · 非 null 只允许**单元测试**注入假实现，用来在无网络/无磁盘环境下验证会话层的
    ///     进度投影与状态机；**装配层（MainWindow/App/页面）绝不可设置本属性**，
    ///     否则就会出现"测试缝变成第二条生产路径"的静默退化。
    /// 注入实现若要复现真实行为，需自己把 <see cref="TransferEngineHooks"/> 挂到引擎事件上。
    /// </summary>
    public TransferRunnerDelegate? TransferRunner
    {
        get => _transferRunner;
        set
        {
            _transferRunner = value;
            Raise(nameof(IsUsingInjectedTestRunner));
            _log.Warning("TransferRunner 测试缝被赋值（仅测试允许；生产路径必须为 null）：{Kind}",
                value is null ? "已清空（回到 Core 引擎）" : value.GetType().Name);
        }
    }

    /// <summary>
    /// 是否处于"测试缝"模式（即注入了假实现）。**生产路径恒为 false**；
    /// true 表示本次会话不会走 Core 引擎，仅供单测使用。
    /// </summary>
    public bool IsUsingInjectedTestRunner => _transferRunner is not null;

    /// <summary>
    /// 是否"完全直通"（未启用 UI 节流）。**生产默认 false（节流开启）**。
    /// true 只有两种来源：① <c>_queue is null</c>（离屏构造/单测/非 UI 线程）；
    /// ② <c>PCMIG_UI_THROTTLE=0</c>（保命回退开关）。
    /// </summary>
    public bool ReadonlyUiThrottleDisabled => _uiThrottleDisabled;

    /// <summary>节流实际生效中（= 薄泵已建立且仍在跑）。用于运行期诊断与契约断言。</summary>
    public bool IsUiThrottleActive => _flushPump is { IsRunning: true };

    /// <summary>本次运行累计的 flush 次数（<c>PCMIG_UI_FLUSH_TRACE=1</c> 时同时落 %TEMP% 日志）。</summary>
    public int UiFlushCount => _flushCount;

    /// <summary>
    /// ★ 仅测试缝 ★ —— 薄泵工厂注入点（生产恒为 null ⇒ 走真实 <see cref="DispatcherQueueUiFlushPump"/>）。
    ///
    /// 边界与 <see cref="TransferRunner"/> 同一纪律：**装配层（MainWindow/App/页面）绝不可设置本属性**；
    /// 它只让单测在没有 WinUI 队列的条件下验证"timer 在哪些时机被 Start/Stop"的生命周期契约（D2）。
    /// </summary>
    public Func<Func<bool>, IUiFlushPump>? UiFlushPumpFactoryForTest
    {
        get => _flushPumpFactory;
        set
        {
            _flushPumpFactory = value;
            _log.Warning("UiFlushPumpFactoryForTest 测试缝被赋值（仅测试允许；生产路径必须为 null）");
        }
    }

    /// <summary>
    /// ★ 仅测试缝 ★ —— 强制"节流开启"判定，让单测能在没有 UI 队列的环境下**真的走批量分支**
    /// （覆盖"高频流被合并、Tick 才上屏"这条生产主路径，而不是只测直通分支）。
    ///
    /// 边界与 <see cref="TransferRunner"/> 同一纪律：**装配层绝不可调用**；只允许单测在
    /// 已经注入 <see cref="UiFlushPumpFactoryForTest"/> 假泵之后调用。
    /// 线程安全：本属性只应在启动任何引擎之前设置一次。
    /// </summary>
    public void EnableUiThrottleForTest()
    {
        _uiThrottleForcedForTest = true;
        _log.Warning("EnableUiThrottleForTest 测试缝被调用（仅测试允许；生产路径绝不调用）");
    }

    /// <summary>
    /// 薄泵的唯一生产构造点（**两条明确通路，没有第三条**）：
    ///   ① <see cref="UiFlushPumpFactoryForTest"/> —— 单测注入假泵（覆盖 D2 生命周期契约）；
    ///   ② <see cref="ProductionFlushPumpFactory"/> —— 生产外壳在启动时注入真实泵。
    ///
    /// 为什么必须是"注入"而不是这里 `new DispatcherQueueUiFlushPump(...)`：本 VM 文件被**链入**
    /// tests\PCMig.Core.Tests 编译，而该测试项目**没有 DispatcherQueueTimer 替身**
    /// （见 TestOnlyDispatcherQueueShim.cs）⇒ 本文件在任何编译单元里都不得出现 WinUI 定时器类型名。
    /// 真实实现在**不链入**的 UiFlushPump.cs；装配在 MainWindow 构造里（InjectProductionFlushPump）。
    /// </summary>
    private IUiFlushPump CreateFlushPump(Func<bool> onTick)
    {
        var testFactory = _flushPumpFactory;
        if (testFactory is not null) return testFactory(onTick);

        var production = ProductionFlushPumpFactory;
        if (production is not null) return production(onTick);

        throw new InvalidOperationException(
            "节拍薄泵没有装配：生产外壳（MainWindow 构造）必须给 ProductionFlushPumpFactory 赋值，" +
            "单测请用 UiFlushPumpFactoryForTest 注入假泵。");
    }

    /// <summary>
    /// ★ 生产装配点（唯一）★ —— 由 UI 外壳（MainWindow）在启动时注入真实的节拍泵工厂。
    ///
    /// 为什么是静态字段：它是**窗口级常量**（每个进程只有一套真实 DispatcherQueueTimer 装配），
    /// 不是每个会话一份的可变状态。而且这样能让**被链入测试的 VM 文件**不出现 WinUI 定时器类型名
    /// —— 注入方（MainWindow，不链入）持真实类型。
    /// **测试不得设置本属性**（测试用 <see cref="UiFlushPumpFactoryForTest"/>）。
    /// </summary>
    internal static Func<Func<bool>, IUiFlushPump>? ProductionFlushPumpFactory { get; set; }

    // ────────────────────────── Verifier 展示约束（阶段 A 硬约束的判定辅助）──────────────────────────

    /// <summary>
    /// 纯内存判定：有计划且计划里有对象才允许验证（不做任何 IO，属性可安全读取）。
    /// 完整判定（含源根可达性实测）见 <see cref="EvaluateVerifyGateAsync"/>。
    /// </summary>
    public bool CanVerifyNow => HasPlan;

    /// <summary>不允许验证时的明确原因（纯内存口径；源可达性需调 <see cref="EvaluateVerifyGateAsync"/>）。</summary>
    public string VerifyBlockedReason => CanVerifyNow
        ? string.Empty
        : Ctx is null
            ? "尚无任务：不能验证（请先完成 Step 1 连接与 Step 2 选择，生成计划）。"
            : "拒绝验证：计划里没有任何对象（plan.Objects.Count == 0）——0 个对象的“全部一致”没有任何意义。";

    private int _totalHashSampled;
    /// <summary>最近一次验证报告里实际完成的内容级抽样文件数（0 = 没做内容级核对）。</summary>
    public int TotalHashSampled { get => _totalHashSampled; private set { if (Set(ref _totalHashSampled, value)) Raise(nameof(HasContentLevelEvidence)); } }

    /// <summary>true 才有"内容级"证据；false 时任何文案都不得暗示内容一致。</summary>
    public bool HasContentLevelEvidence => TotalHashSampled > 0;

    private string _lastVerifyResultText = string.Empty;
    /// <summary>最近一次验证的结论文案（已按 L1 口径措辞，绝不说成"完整性验证通过"）。</summary>
    public string LastVerifyResultText { get => _lastVerifyResultText; private set => Set(ref _lastVerifyResultText, value); }

    private string _lastVerifyHintText = string.Empty;
    /// <summary>
    /// ★ UX-2（2026-10-11 用户现场观察）★ 左侧提示框专用的一句话结论：**只讲验证状态、结论与差异数量**，
    /// 一个对象编号都不列。完整结论见 <see cref="LastVerifyResultText"/>，逐对象明细见验证报告。
    /// 背景：现场一次 1000 个对象的验证把全部编号塞进提示框，提示框被铺满、真正要看的结论被淹掉。
    /// </summary>
    public string LastVerifyHintText { get => _lastVerifyHintText; private set => Set(ref _lastVerifyHintText, value); }

    /// <summary>★ UX-1 ★ 结论文案里最多列出的对象编号个数（超过则只给"如 … 等 N 个"）。</summary>
    public const int MaxObjectIdsInSummary = 3;

    /// <summary>
    /// ★ UX-2 ★ 提示框口径的一句话结论：状态 + 四态数量，不列对象编号。
    /// 措辞沿用 <see cref="L1ResultText"/> 的 L1 口径，绝不把数量/字节对账说成内容一致。
    /// </summary>
    public static string DescribeVerifyHint(VerifyReport report, VerifyLevel level)
    {
        var ok = report.Objects.Count(o => o.Status == "OK");
        var mismatch = report.Objects.Count(o => o.Status == "MISMATCH");
        var unknown = report.Objects.Count(o => o.Status == "UNKNOWN");
        var empty = report.Objects.Count(o => o.Status == "EMPTY");
        var canceled = report.Objects.Count(o => o.Status == "CANCELED");
        // ★ 用户口径（2026-10-11 现场）：提示卡只给一句话，例：
        //   「L1 对账完成：通过 1，差异 413，无法确认 0。点「打开验证报告」查看详细清单。」
        //   一个 object-xxxxx 都不出现；超出 4 态之外的状态只在非零时追加，长度可控。
        var counts = new List<string> { $"通过 {ok}", $"差异 {mismatch}", $"无法确认 {unknown}" };
        if (empty > 0) counts.Add($"无可核对数据 {empty}");
        if (canceled > 0) counts.Add($"已取消 {canceled}");
        var verb = level >= VerifyLevel.L2_SampleHash ? "L2 抽样哈希核对" : "L1 对账";
        var head = report.OverallPass ? $"{verb}完成" : $"{verb}未通过";
        // L1 通过时必须带上口径限定语（T07）：数量/字节一致不等于逐文件内容一致。
        var caveat = report.OverallPass && level < VerifyLevel.L2_SampleHash
            ? "（只核对文件数量与总字节，不等于内容一致）"
            : string.Empty;
        return $"{head}：{string.Join("，", counts)}。点「打开验证报告」查看详细清单。{caveat}";
    }

    /// <summary>
    /// 验证前置闸门（含**源根可达性实测**，IO 在后台线程）：
    ///   ① 无任务 / 无计划 → 拒绝；
    ///   ② plan.Objects.Count == 0 → 拒绝；
    ///   ③ 任一 obj.SourcePath 不可达 → 拒绝（源不可读时"目标一致"无法成立）。
    /// 通过后才允许调用 Core 的 Verifier。
    /// </summary>
    public async Task<VerifyGateResult> EvaluateVerifyGateAsync(CancellationToken ct = default)
    {
        var ctx = Ctx;
        if (ctx is null)
            return new VerifyGateResult(false, "尚无任务：不能验证（请先完成 Step 1 连接与 Step 2 选择，生成计划）。");
        var plan = ctx.Plan;
        if (plan is null || plan.Objects.Count == 0)
            return new VerifyGateResult(false, "拒绝验证：计划里没有任何对象（plan.Objects.Count == 0）——0 个对象的“全部一致”没有任何意义。");

        List<string> unreachable;
        try
        {
            unreachable = await Task.Run(() => plan.Objects
                .Where(o => !SourcePathReachable(o.SourcePath))
                .Select(o => o.ObjectId)
                .ToList(), ct);
        }
        catch (OperationCanceledException) { return new VerifyGateResult(false, "验证已取消。"); }

        if (unreachable.Count > 0)
        {
            var sample = string.Join("、", unreachable.Take(5)) + (unreachable.Count > 5 ? $" 等 {unreachable.Count} 个" : "");
            // ★ P0-05 ★ 这里是"源共享真的不在"的硬证据（不是猜测）：闸门刚刚逐个对象实测过可达性。
            //   旧版只拒绝验证、不动底栏源灯 ⇒ 共享被策略关闭后灯还是绿的，用户看到"已连接"却怎么都验证不过。
            _connection?.NotifySourceLost();
            return new VerifyGateResult(false,
                $"拒绝验证：源根目录不可访问（{sample}）。源侧读不到时「目标一致」无法成立，" +
                "此时的“通过”是假通过——请先在 Step 1 恢复与旧电脑的连接（必要时重新输入凭据）后重试。",
                SourceReachable: false);
        }
        return VerifyGateResult.Pass;
    }

    /// <summary>源路径可达性（目录或文件；异常一律视为不可达）。</summary>
    private static bool SourcePathReachable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return Directory.Exists(path) || File.Exists(path); }
        catch { return false; }
    }

    /// <summary>
    /// ★ 真机问题 3 ★ 对象显示名 = 对象路径最后一段（兼容 UNC 与尾随分隔符）。
    /// 纯函数 ⇒ 可单测（不依赖任何真实路径存在，只做字符串处理）。
    /// 例：<c>\\192.0.2.11\f\mail</c> → <c>mail</c>；<c>\\host\f\</c> → <c>f</c>。
    /// </summary>
    internal static string DescribeObjectName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var trimmed = path.TrimEnd('\\', '/');
        if (trimmed.Length == 0) return string.Empty;
        var idx = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        return idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
    }

    /// <summary>
    /// ★ 验证结论文案的唯一出口（硬约束）★
    ///   · L1 通过 → 只能是 <see cref="L1ResultText"/>，**绝不**显示成"完整性验证通过"；
    ///   · L2 展示而 HashSampled == 0 → 必须附 <see cref="NoHashSampleText"/>；
    ///   · 无论 Core 返回什么，都不得扩大解释成"数据完整"。
    /// </summary>
    public static string DescribeVerifyOutcome(VerifyReport report, VerifyLevel level)
    {
        if (report.Objects.Count == 0) return "验证未执行：计划里没有任何对象。";
        if (!report.OverallPass)
        {
            // ★ P0-02 ★ 不能把所有非 OK 一律说成"存在不一致对象"：真正的差异与"根本没核对成"
            //   （源/目标不可达、枚举不完整）是两件事，处置方式相反 —— 前者去查差异，后者先恢复可达性。
            //   现场正是这句话把 10/12（数量不同、样本为空）与真缺失混成了一种说法，故按状态如实分组。
            // ★ UX-1（2026-10-11 用户现场观察）★ 这里**不再把全部对象编号拼进结论行**：
            //   现场 1000 个对象（object-000001…object-001000）的编号把"结果与校验"正文与左侧提示框全部铺满，
            //   真正要看的结论（几个差异、几个没核对成）反而看不见了。现在只给数量 + 少量示例，
            //   逐对象明细 / 差异文件清单 / 错误原因 / 建议操作一律落到验证报告（VerifyReportGenerator）里。
            var mismatch = report.Objects.Where(o => o.Status == "MISMATCH").ToList();
            var unknown = report.Objects.Where(o => o.Status == "UNKNOWN").ToList();
            var others = report.Objects
                .Where(o => o.Status is not ("OK" or "MISMATCH" or "UNKNOWN"))
                .Select(o => $"{o.ObjectId}（{o.StatusText}）").ToList();
            var parts = new List<string>();
            if (mismatch.Count > 0) parts.Add($"存在差异 {mismatch.Count} 个对象{IdSample(mismatch.Select(o => o.ObjectId))}");
            if (unknown.Count > 0)
                parts.Add($"无法确认 {unknown.Count} 个对象{IdSample(unknown.Select(o => o.ObjectId))}" +
                          "（源或目标不可达 / 枚举不完整，属「没核对成」而非「不一致」）");
            if (others.Count > 0) parts.Add($"其他 {others.Count} 个对象{IdSample(others)}");
            return $"✘ 验证未通过（{DescribeLevel(level)}）：{string.Join("；", parts)}。" +
                   "逐对象明细、差异文件清单与建议操作见验证报告。";
        }

        var sampled = report.Objects.Sum(o => o.HashSampled);
        if (level < VerifyLevel.L2_SampleHash)
            return $"✔ {L1ResultText}";

        if (sampled == 0)
            return $"✔ {L1ResultText}｜⚠ {NoHashSampleText}";

        return $"✔ {L1ResultText}；本次另有 {sampled} 个文件做了 SHA-256 双向内容核对" +
               "（抽样通过不等于全部文件内容一致，未被抽到的文件仍只做过数量/字节核对）。";
    }

    /// <summary>
    /// ★ UX-1 ★ 结论文案里的对象编号示例：≤ <see cref="MaxObjectIdsInSummary"/> 个时全列，
    ///   超过时只给"如 … 等 N 个"，**绝不**把成百上千个编号拼进一句话。
    /// </summary>
    private static string IdSample(IEnumerable<string> ids)
    {
        var list = ids.ToList();
        if (list.Count == 0) return string.Empty;
        if (list.Count <= MaxObjectIdsInSummary) return "（" + string.Join("、", list) + "）";
        return "（如 " + string.Join("、", list.Take(MaxObjectIdsInSummary)) + $" 等 {list.Count} 个）";
    }

    /// <summary>验证层级的人话名称（界面不要直接显示枚举名）。</summary>
    public static string DescribeLevel(VerifyLevel level) => level switch
    {
        VerifyLevel.L2_SampleHash => "L2 抽样哈希",
        VerifyLevel.L1_CountSize => "L1 文件数/字节对账",
        _ => "未指定层级",
    };

    // ────────────────────────── 方法：Step2 准备 ──────────────────────────

    /// <summary>
    /// 「同主机+同目标已有未完成任务」时用户的决定（与旧 WPF 706-725 的 Yes/No 弹窗一一对应）。
    /// </summary>
    public enum ResumeDecision
    {
        /// <summary>尚未决定：由 <see cref="MigrationSessionViewModel.PrepareAsync"/> 检测后询问（页面注入的回调）。</summary>
        Unspecified = 0,
        /// <summary>创建全新任务（**不改动**既有任务的任何存档文件）。</summary>
        CreateNew = 1,
        /// <summary>续传既有任务（不新建；沿用 job.json 里的线程原值）。</summary>
        ResumeExisting = 2,
    }

    /// <summary>
    /// 预检 → 扫描 → 生成计划（写入 job.json / preflight.json / observed-state.json / plan.json）。
    /// <para>
    /// <paramref name="skipPreflight"/>=true 时为「不预检开始迁移」模式：**只跳过 PreflightChecker
    /// 那一段检查**（不实例化、不写 preflight.json、Phase 不进 Preflight），
    /// 其余环节——Job 创建、源扫描、扫描残缺闸门、Planner 生成计划——一步不少，
    /// 语句与正常模式完全相同。跳过的结论以 <see cref="JobDefinition.PreflightSkipped"/>
    /// 落 job.json，并在任务日志里留一条 WARN 事实。
    /// </para>
    /// **本方法不含任何对话框**：需要用户确认的两处（扫描残缺闸门 / 发现未完成任务）走注入的回调，
    /// 回调由页面用 ContentDialog 实现（ContentDialog 需要 XamlRoot，属页面层职责）。
    /// </summary>
    /// <param name="definition">任务定义（页面组装：Host/User/TargetRoot/Sources/CustomSelections）。</param>
    /// <param name="password">本次预检用的口令（只作为参数存在，不落任何存档）。</param>
    /// <param name="progress">扫描进度文案回调。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="confirmIncompleteScan">
    /// 扫描残缺闸门的确认回调（同旧 WPF <c>ConfirmIncompleteScan</c> 语义，**不可简化**）：
    /// 传 null 时按“用户未确认”处理 = 拦住、不生成可执行计划；返回 true 才把
    /// <c>allowIncompleteScan=true</c> 写进 job.json 并继续。
    /// </param>
    /// <param name="resumeDecision">
    /// 页面已经问过用户时的决定（同旧 WPF 706-725 的 Yes/No 弹窗）：
    /// <see cref="ResumeDecision.Unspecified"/>（默认）= 本方法自己检测并拦下来问
    /// （没有回调可问时**不新建任务**，只写原因，避免悄悄生成重复任务）；
    /// <see cref="ResumeDecision.CreateNew"/> = 用户选了“创建全新任务”，照旧新建；
    /// <see cref="ResumeDecision.ResumeExisting"/> = 用户选了“继续上次任务”，载入既有任务（不新建）。
    /// </param>
    /// <param name="skipPreflight">
    /// true = 跳过 <c>PreflightChecker</c>（v0.5.4-preview.3「不预检开始迁移」）。
    /// **不是**绕过引擎：扫描/闸门/计划/回执/验证照常，见方法 summary。
    /// </param>
    /// <returns>true = 计划已生成（HasPlan）或已载入可续传任务。</returns>
    public async Task<bool> PrepareAsync(
        JobDefinition definition,
        string? password,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        Func<Task<bool>>? confirmIncompleteScan = null,
        ResumeDecision resumeDecision = ResumeDecision.Unspecified,
        bool skipPreflight = false)
    {
        if (IsRunning)
        {
            SetUserHint("迁移正在进行中：请先「暂停」或「停止」，再重新预检。");
            return false;
        }
        ArgumentNullException.ThrowIfNull(definition);

        // ---- 源侧：勾选的共享（整盘） + 目录树勾选（customs 恒生效，见 ExpertModeIsWiredInPhaseA）----
        var wholeShares = definition.Sources.Select(s => s.Path).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        var customs = definition.CustomSelections;
        if (wholeShares.Count == 0 && customs.Count == 0)
        {
            SetUserHint("请先在 Step 1 勾选要迁移的共享，或在目录树里勾选要迁移的内容：" +
                            "勾共享=整盘迁移；展开共享可精确勾选目录或单个文件。");
            return false;
        }
        if (string.IsNullOrWhiteSpace(definition.TargetRoot)) { SetUserHint("请先指定目标路径。"); return false; }

        // ---- 目标路径前置校验（与 WPF PrepareAsync 686-704 同口径，傻瓜操作防线）----
        if (definition.TargetRoot.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            SetErrorSummary($"目标路径包含非法字符（不能含 \" < > | 等）：{definition.TargetRoot}");
            return false;
        }
        try
        {
            var fullTarget = Path.GetFullPath(definition.TargetRoot.Trim());
            if (File.Exists(fullTarget))
            {
                SetUserHint($"目标位置已存在一个同名文件而不是文件夹：{fullTarget}。请换一个目标目录。");
                return false;
            }
        }
        catch (Exception ex) { SetErrorSummary($"目标路径无效：{ex.Message}"); return false; }

        // ---- 同主机+同目标已有未完成任务 → 询问续传或新建（与 WPF 706-725 同口径）----
        JobSummary? dup = null;
        try
        {
            var host = definition.SourceHost.Trim();
            var target = definition.TargetRoot.Trim();
            var found = await Task.Run(() => new JobManager(_log).FindUnfinished(host, target), ct);
            dup = found.FirstOrDefault(x => File.Exists(Path.Combine(x.JobDir, "plan.json")));
            // 当前任务本身就是那个任务（例如刚由「恢复任务」载入）→ 不算“另一个未完成任务”，
            // 重新预检就是刷新它的计划，属正常操作，不再二次询问。
            if (dup is not null && Ctx is not null
                && string.Equals(dup.JobId, Ctx.JobId, StringComparison.OrdinalIgnoreCase)) dup = null;
        }
        catch (OperationCanceledException) { SetOperational("已取消。"); return false; }
        catch (Exception ex) { _log.Warning(ex, "未完成任务检测失败"); }

        if (resumeDecision == ResumeDecision.ResumeExisting)
        {
            if (dup is null)
            {
                SetUserHint("你选择了继续上次任务，但当前源/目标下没有找到未完成的任务：请重新「预检并生成计划」新建一个。");
                return false;
            }
            _log.Information("用户选择续传既有任务 {JobId}（未新建）", dup.JobId);
            await AdoptExistingJobAsync(dup.JobDir, ct);
            SetOperational($"已载入未完成任务 {dup.JobId}（{dup.PhaseText}）：" +
                            (dup.StateUnreliable ? "状态文件损坏，进度以回执为准。" : $"已传 {dup.Percent:0.0}%。") +
                            "点「恢复任务」从断点继续（已完成的对象不会重传）；若要重新生成计划，请再点一次「预检并生成计划」。");
            return true;
        }

        if (dup is not null && resumeDecision == ResumeDecision.Unspecified)
        {
            // 页面没问过用户 → **绝不悄悄新建第二个任务**（同一份数据跑两条任务会在目标目录里互相打架）。
            // 交互由页面包提供（ContentDialog）：页面问完用户后带 CreateNew / ResumeExisting 重新调用本方法。
            PendingResumeCandidate = dup;
            SetOperational($"该源电脑和目标路径下已有未完成任务：{dup.JobId}（{dup.PhaseText}" +
                            (dup.StateUnreliable ? "，状态文件损坏，进度以回执为准" : $"，已传 {dup.Percent:0.0}%") +
                            "）。请选择「继续上次任务（已完成部分不重传）」或「创建全新任务」。");
            _log.Information("检测到未完成任务 {JobId}，等待用户在界面上决定续传或新建（未创建任何新任务）", dup.JobId);
            return false;
        }

        if (dup is not null)
        {
            // 用户已明确选择“创建全新任务”：**照旧新建**，不动既有任务的任何文件。
            _log.Information("未完成任务 {JobId} 存在，用户选择新建任务（既有任务存档未改动）", dup.JobId);
        }

        // ★ 线程数定案：写进 job.json 的永远是解析后的真实整数（自动 ⇒ 16；4 ⇒ 4；8 ⇒ 8）
        definition.Options.Threads = ResolveThreads(SelectedMt);
        // ★ P1-07(1) ★ 目录映射语义随新任务一起落盘（job.json 的 Options.SelectedFolderMapping）。
        //   必须在这里定案——Planner 生成计划时就按它决定"内容直达 / 保留文件夹"；
        //   之后 Robocopy Dest / Verifier / Repair / Report 全部读计划里的目标路径，不再各自重算。
        //   恢复既有任务不走本行（续传以旧 job.json 的原值为准，绝不改写历史作业的目标路径）。
        definition.Options.SelectedFolderMapping = SelectedFolderMapping;
        // ★ v0.5.4-preview.3「不预检开始迁移」★ 把"跳过预检"这件事本身写进 job.json 作为审计事实
        //   （必须在 jm.Create 之前设好，Create 会把整份 definition 原子写入 job.json）。
        //   注意：这里**不写 preflight.json**——该文件缺失即"未执行预检"，
        //   ReportGenerator 的 Preflight 附录因此整段不出现，不会被读成"预检通过"。
        definition.PreflightSkipped = skipPreflight;

        SetOperational(skipPreflight ? "不预检模式：正在准备任务…" : "正在预检…");
        // ★ A.5（P1-4）：预检 = 进入"新任务"上下文。先把上一个任务的实时投影、验证投影与日志投影
        //   整批复位（否则上一轮的失败清单/当前对象/上一次验证结论会跟着新任务一起显示）。
        //   落在下面两行日志之前，保证日志面板里剩下的都是**本次预检**的真实行。
        ResetForJobSwitch();
        // 真实动作日志（Step4 实时日志面板）：这两条描述的是**正在真实执行**的动作，不是示例文案。
        if (skipPreflight)
        {
            Log("WARN", "本次跳过预检（用户选择「不预检开始迁移」）：未执行源可达性 / 凭据 / 共享枚举 / 目标空间 / 权限 / 同名冲突检查");
            Log("INFO", $"任务目标：源 {definition.SourceHost}｜目标 {definition.TargetRoot}｜线程 {ResolveThreads(SelectedMt)}");
        }
        else
        {
            Log("INFO", "开始预检：由 Core 的 PreflightChecker 实测源可达性 / 目标空间 / 权限 / 同名冲突");
            Log("INFO", $"预检目标：源 {definition.SourceHost}｜目标 {definition.TargetRoot}｜线程 {ResolveThreads(SelectedMt)}");
        }
        // ★ 硬要求 2：新任务开始生成计划时，**必须**先把“可开始迁移”的依据清干净。
        //   若只清对象清单而 Ctx 仍指向旧任务，HasPlan 会继续为 true（计划为空也显示可以开始）。
        //   故这里连同 Ctx 一起清掉：在下一次计划成功写入之前，HasPlan / CanStart 恒为 false。
        _ctx = null;
        Raise(nameof(Ctx));
        Raise(nameof(HasJob));
        Raise(nameof(JobIdText));
        Raise(nameof(ThreadsForRun));
        Raise(nameof(CanRepair));
        RaiseDerived();
        HasPlanReset();
        try
        {
            var jm = new JobManager(_log);
            var ctx = await Task.Run(() => jm.Create(definition), ct);   // 阻塞 IO（建目录 + 原子写 job.json）
            var jobLog = LogBootstrap.CreateJobLogger(ctx.JobDir, ctx.JobId);
            if (skipPreflight)
            {
                // ★ v0.5.4-preview.3「不预检开始迁移」★ 整个 PreflightChecker 段被跳过：
                //   · 不实例化 checker、不调 RunAsync、**不写 preflight.json**；
                //   · Phase 不进 Preflight，直接进 Scanning；
                //   · 用户已被告知的风险（源不可读 / 目标不可写 / 同名冲突）会在扫描与传输阶段
                //     以真实错误呈现，由既有的失败清单 / 回执 / Step4 报告记录——不在这里伪造"通过"。
                //   下面**不放在 if 块里**的扫描、扫描残缺闸门、Planner、state 落盘一步不少。
                Phase = JobPhase.Scanning;
                SetOperational("已跳过预检，正在扫描源数据（大容量磁盘可能需要几分钟）…");
                // ★ 把「未执行预检」写进**落盘**日志：Log(level,text) 只往 Step4 实时日志面板的内存集合里追加，
                //   不经过 Serilog；job.json 的 PreflightSkipped 是任务证据，但事后翻 logs\job-*.log 也应能查到
                //   （任务令 §四：未执行预检这一事实必须保留在日志或任务证据中）。jobLog 到这一行才可用。
                _log.Warning("本次任务跳过预检（用户选择「不预检开始迁移」）：未执行源可达性 / 凭据 / 共享枚举 / 目标空间 / 权限 / 同名冲突检查｜源 {Host}｜目标 {Target}",
                    definition.SourceHost, definition.TargetRoot);
                jobLog.Warning("本次跳过预检（用户选择「不预检开始迁移」）：未执行源可达性 / 凭据 / 共享枚举 / 目标空间 / 权限 / 同名冲突检查；job.json preflightSkipped=true，本次不写 preflight.json");
                Log("INFO", "已跳过预检，开始扫描源数据（Job 已创建，job.json preflightSkipped=true）…");
            }
            else
            {
                Phase = JobPhase.Preflight;

                var checker = new PreflightChecker(jobLog);
                var sourcePaths = definition.Sources.Select(s => s.Path).ToList();
                var pre = await Task.Run(() => checker.RunAsync(
                    definition.SourceHost,
                    definition.SourceUser,
                    string.IsNullOrWhiteSpace(password) ? null : password,
                    sourcePaths,
                    definition.TargetRoot,
                    estimatedBytes: 0,
                    benchmark: _connection?.BenchmarkOnConnect ?? false), ct);
                ctx.SavePreflight(pre);

                if (!pre.OverallPass)
                {
                    var bad = pre.Checks.Where(c => !c.Pass && c.Severity == "Error").ToList();
                    foreach (var c in bad.Take(8)) AddFail("预检 · " + c.Name, c.Detail);
                    // ★ 缺陷 A-01a：旧文案只贴第一条 Detail（"路径不存在或不可访问（检查共享名/权限）"）——
                    // 既不说**是哪条路径/哪项检查**，也不给下一步，用户不知道要去掉哪个勾。
                    // 现在按「检查名（原因）」列出失败项，并给一句可执行的下一步。
                    // 观察 O-A19-4：原写法用「检查名（Detail）」包一层括号，而 Detail 自身常带括号
                    // （如目标盘「…（技术细节：…）」），用户会看到「（…（…））」嵌套。
                    // 改为「检查名：Detail」并用 SubTitle 分隔，同时去掉 Detail 末尾句号避免「。。」。
                    var named = string.Join("；", bad.Take(3).Select(c => $"{c.Name}：{c.Detail.TrimEnd('。')}"));
                    var next = bad.Any(c => c.Name.StartsWith("源路径", StringComparison.Ordinal))
                        ? "请回到第 1 步取消勾选读不到的那条共享（或换成对方开放的共享名后重试）。"
                        : bad.Any(c => c.Name.Contains("目标盘", StringComparison.Ordinal))
                            ? "请在右侧「目标根目录」改成这台电脑上真实存在的磁盘路径（例如 D:\\迁移目标），再点「预检并生成计划」。"
                            : "请按上面的失败项提示修正后，再点「预检并生成计划」。";
                    // 预检未通过 ⇒ 检查结论通道必须同步清空（否则上一次「检查通过」会残留在状态行上）。
                    SetCheckSummary(string.Empty, 0);
                    SetErrorSummary($"预检未通过（{bad.Count} 项）：{named}。{next}");
                    Log("ERROR", $"预检未通过（{bad.Count} 项）：{named}");
                    return false;
                }

                var warns = pre.Checks.Where(c => !c.Pass && c.Severity == "Warning").ToList();
                // ★ 真机问题 2（2026-10-21）★ 三个维度分开落位，不再拼成一句长句。
                //   旧实现：「预检通过（1 项提醒不阻断：链路吞吐基准 —— 测速失败（共享可能只读）：
                //            Access to the path '\\\\192.0.2.11\\f\\.pcmig-bench-….tmp' is denied.）正在扫描源数据…」
                //   这一句同时说了"通过""失败""Access is denied""正在扫描"，用户无法判断当前到底能不能继续。
                //   现在：
                //     · 操作阶段  → 主提示只留「正在扫描源数据…」（当前最重要的一件事）；
                //     · 检查结论  → 状态行「检查通过 · N 条提醒」；
                //     · 问题级别  → 提醒明细进辅助区（UserHint），原始英文已在 PreflightChecker 降级为技术细节。
                SetOperational("正在扫描源数据（大容量磁盘可能需要几分钟）…");
                SetUserHint(warns.Count > 0
                    ? "检查通过，可以开始迁移。非阻断提醒：" +
                      string.Join("；", warns.Take(3).Select(c => $"{c.Name} —— {c.Detail.TrimEnd('。')}"))
                    : "检查通过，可以开始迁移。");
                // ★ Preview.4 真机问题 1 ★ 标记"这条行动号召等迁移开始就要撤下"。
                _preflightHintActive = true;
                SetCheckSummary(DescribeCheckSummary(true, warns.Count), warns.Count);
                Log("INFO", warns.Count > 0
                    ? $"预检通过（{warns.Count} 条非阻断提醒：{string.Join("、", warns.Select(c => c.Name))}），开始扫描源数据…"
                    : "预检通过（无提醒），开始扫描源数据…");
                foreach (var w in warns.Take(8))
                    Log("WARN", $"预检提醒 · {w.Name}：{w.Detail}");
            }

            Phase = JobPhase.Scanning;
            var matrix = MigrationMatrix.Load(null, jobLog).WithExtraExclusions(definition.CustomExclusions, jobLog);
            var scanner = new SourceScanner(jobLog);
            var observed = await scanner.ScanAsync(definition, matrix, progress, ct);
            ctx.SaveObserved(observed);

            // ══════════ 扫描残缺闸门（v0.3.8，缺陷 4）══════════
            // 语义**一字不改**地照搬旧 WPF ConfirmIncompleteScan（L1178-1205）：
            //   ① 有残缺 → 明细逐条进失败清单（弹窗可能被关掉，清单不会）；
            //   ② 已确认过（job.json allowIncompleteScan=true）→ 直接放行；
            //   ③ 未确认 → 必须由用户在界面上显式确认，否则**不生成可执行计划**；
            //   ④ 用户确认 → 把结论写进 job.json（SaveDefinition）并记日志，续传不再拦。
            var gate = ScanGate.Inspect(observed, definition.AllowIncompleteScan);
            if (gate.HasIncomplete)
            {
                foreach (var p in gate.Paths.Take(20))
                    AddFail("扫描不可访问 · " + (p.Split('｜').FirstOrDefault() ?? p), p);

                if (!gate.Acknowledged)
                {
                    var ok = confirmIncompleteScan is not null && await confirmIncompleteScan();
                    if (!ok)
                    {
                        _log.Warning("扫描残缺闸门拦截：{Count} 处不可访问，未生成可执行计划（Job {JobId}）", gate.Count, ctx.JobId);
                        SetErrorSummary($"已拦下：扫描存在 {gate.Count} 处不可访问的目录/文件，**未生成可执行计划**（左栏已逐条列出）。" +
                                        "请修好权限/网络后重新「预检并生成计划」；确知风险仍要继续，请在确认框里选「我已知风险，仍要继续」。");
                        return false;
                    }
                    definition.AllowIncompleteScan = true;
                    ctx.SaveDefinition();
                    _log.Warning("用户显式确认：扫描存在 {Count} 处不可访问位置，仍继续迁移（job.json allowIncompleteScan=true）", gate.Count);
                }
            }

            var plan = new Planner(jobLog).CreatePlan(definition, observed, matrix);
            ctx.SavePlan(plan);
            var st = ctx.LoadStateOrNew();
            st.Phase = JobPhase.AwaitingReview;
            st.TotalObjects = plan.Objects.Count;
            st.TotalBytes = plan.TotalBytes;
            ctx.SaveState(st);

            Ctx = ctx;
            Phase = JobPhase.AwaitingReview;
            FillObjectsFromPlan(plan);
            Percent = 0;
            IsFinished = false;
            DataLabel = "已传 / 计划";
            ProgressText = $"0 B / {Format.Bytes(plan.TotalBytes)}";
            PlanBytesText = Format.Bytes(plan.TotalBytes);
            ActualBytesText = "0 B";
            ObjectText = $"{plan.Objects.Count}";
            BalanceText = $"计划 {Format.Bytes(plan.TotalBytes)}　·　实际落盘 0 B";
            SetOperational($"计划已生成（Job {ctx.JobId}）：{plan.Objects.Count} 个对象，共 {Format.Bytes(plan.TotalBytes)}。" +
                            (observed.Warnings.Count > 0 ? $" ⚠ {observed.Warnings[0]}" : string.Empty));
            AppendOperational(SpaceGuardNote(definition.TargetRoot, plan.TotalBytes));
            AppendOperational(LockAndEfsNote(observed));
            // ★ 真实计数（原先 UI 上那句「计划生成完成：对象 0 个，合计 0 B」是硬编码假数据，已删除）：
            //   这里写的就是本次 Planner 真实产出的对象数与计划字节数。
            Log("INFO", $"计划生成完成：对象 {plan.Objects.Count} 个，合计 {Format.Bytes(plan.TotalBytes)}（Job {ctx.JobId}）。");
            // 文件数只有扫描后才知道：超大规模时明确告知“全程流式、无文件数上限”
            if (observed.TotalFiles >= 5_000_000)
                AppendOperational($"（文件量 {observed.TotalFiles:N0}，属超大规模——本任务传输/验证全程流式处理，无文件数上限）");
            HasPlanRaise();
            await RefreshExistingJobsAsync(null, ct);
            return true;
        }
        catch (OperationCanceledException) { SetOperational("已取消。"); return false; }
        catch (Exception ex)
        {
            // F13：准备阶段同样不许把英文原始异常串抛到界面上（F5 只修了传输路径）。
            //   实测原文：`准备失败：Access to the path '…\JOB-…\receipts' is denied.`
            //   这里走"准备阶段"专用翻译：此刻没有任务、也没有已复制数据，所以不说"任务已停止/数据不会重传"。
            SetErrorSummary("准备失败：" + PCMig.Core.Util.TransferFailureTranslator.ExplainPrepare(ex));
            _log.Error(ex, "WinUI Prepare 失败");
            return false;
        }
    }

    /// <summary>
    /// 迁移前提醒（**非阻断**版）：高危锁文件 / EFS 加密的实测计数写进状态文案并记日志。
    /// 旧 WPF 在这一步弹 Yes/No 询问（L803-818）；本阶段按要求把“弹窗留给后续对话框包”，
    /// 但**事实一条不少**地照实报出（不静默吞掉），用户可在开始迁移前自行处理。
    /// </summary>
    private string LockAndEfsNote(ObservedState observed)
    {
        if (observed.LockRiskFiles <= 0 && observed.EncryptedFiles <= 0) return string.Empty;
        var parts = new List<string>();
        if (observed.LockRiskFiles > 0)
        {
            // ★ P1-08 / R8 ★ 候选数 ≠ 被占用数：只有**实测**打不开（Win32 32/33）的才说"会失败"；
            //   其余明确说"可现在都是能读的、可正常迁移"，不再制造假警报（归档 PST 从不被 Outlook 打开）。
            parts.Add(observed.LockedRiskFiles > 0
                ? $"检测到 {observed.LockRiskFiles} 个 Outlook 数据文件（.pst/.ost）：其中 {observed.LockedRiskFiles} 个实测当前打不开（被其他程序独占），这些会被跳过或失败，请先在旧电脑上关闭占用程序"
                : $"检测到 {observed.LockRiskFiles} 个 Outlook 数据文件（.pst/.ost）：实测当前都能正常读取，可以正常迁移（只有迁移期间旧电脑上正打开它们才会失败）");
        }
        if (observed.EncryptedFiles > 0)
            parts.Add($"检测到 {observed.EncryptedFiles} 个 EFS 加密文件：内容可以正常复制，但在新机上将失去加密保护（明文可读）");
        _log.Warning("迁移前提醒：锁风险候选 {Lock} 个（实测独占 {Locked} 个）/ EFS 加密 {Enc} 个",
            observed.LockRiskFiles, observed.LockedRiskFiles, observed.EncryptedFiles);
        return " ⚠ " + string.Join("；", parts) + "。";
    }

    /// <summary>目标盘空间守护的**非阻断**版本：空间不足时把事实写进状态文案（弹窗确认由后续页面包接入）。</summary>
    private static string SpaceGuardNote(string targetRoot, long planBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(targetRoot));
            if (root is null || planBytes <= 0) return string.Empty;
            var free = new DriveInfo(root).AvailableFreeSpace;
            return free < planBytes
                ? $" ⚠ 目标盘 {root} 剩余空间不足：需要 {Format.Bytes(planBytes)}，可用 {Format.Bytes(free)}（写满时会记失败对象，释放空间后可「恢复任务」补齐）。"
                : string.Empty;
        }
        catch
        {
            return " ⚠ 目标盘剩余空间预检未能完成：本次不做空间前置判断，写满时会记失败对象。";
        }
    }

    // ─────────── D6.3 §11：动作的**实际业务结果**（纯观察态，不参与任何业务分支）───────────
    // 审计 P1-4：页面原先在 `await XxxAsync()` 之后直接写 Succeeded（"方法返回了"当成功）。
    // 这里由 VM 在各**既有分支**如实记下事实，页面只读这里的结果落 UI.ActionCompleted；
    // 判定口径统一在 PCMig.Core.Diagnostics.ActionOutcomePolicy（可单测）。

    /// <summary>最近一次验证的实际业务结果（初值 = 未观察到 ⇒ Unknown）。</summary>
    public ActionOutcomeDecision LastVerifyOutcome { get; private set; } = ActionOutcomePolicy.NotRun;

    /// <summary>最近一次修复的实际业务结果（初值 = 未观察到 ⇒ Unknown）。</summary>
    public ActionOutcomeDecision LastRepairOutcome { get; private set; } = ActionOutcomePolicy.NotRun;

    /// <summary>最近一次开始/恢复迁移的实际业务结果（初值 = 未观察到 ⇒ Unknown）。</summary>
    public ActionOutcomeDecision LastRunOutcome { get; private set; } = ActionOutcomePolicy.NotRun;

    /// <summary>最近一次暂停请求的实际业务结果（初值 = 未观察到 ⇒ Unknown）。</summary>
    public ActionOutcomeDecision LastPauseOutcome { get; private set; } = ActionOutcomePolicy.NotRun;

    /// <summary>最近一次停止请求的实际业务结果（初值 = 未观察到 ⇒ Unknown）。</summary>
    public ActionOutcomeDecision LastStopOutcome { get; private set; } = ActionOutcomePolicy.NotRun;

    // ────────────────────────── 方法：执行 / 暂停 / 停止 / 恢复 ──────────────────────────

    /// <summary>开始迁移（当前任务）。只跑已完成的计划；缺计划时如实拒绝。</summary>
    public Task RunAsync(string? password, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var ctx = Ctx;
        if (ctx is null)
        {
            SetUserHint("尚无任务：不能开始迁移（请先在 Step 2 生成计划）。");
            LastRunOutcome = ActionOutcomePolicy.ForRun(new RunBusinessResult(Phase, "run-no-task", null));
            return Task.CompletedTask;
        }
        if (ctx.Plan is null || ctx.Plan.Objects.Count == 0)
        {
            SetUserHint("计划里没有任何对象：不能开始迁移。");
            LastRunOutcome = ActionOutcomePolicy.ForRun(new RunBusinessResult(Phase, "run-no-plan-objects", null));
            return Task.CompletedTask;
        }
        return RunTransferCoreAsync(ctx, password, onlyObjectIds: null, forceRecopy: false, progress, ct);
    }

    /// <summary>
    /// ★ FIX BATCH 7 ★ 用户点「恢复」那一刻的阶段（<see cref="ResumeAsync"/> 写入）。
    /// null ⇒ 没有发生过恢复动作，等待判据退回"用当前阶段当起点"。
    /// </summary>
    private JobPhase? _resumeOriginPhase;

    /// <summary>
    /// 恢复任务：载入既有任务（可选）后继续。**沿用 job.json 里的线程值**，绝不改写任务定义。
    /// </summary>
    /// <remarks>
    /// ★ FIX BATCH 7 ★ <see cref="_resumeOriginPhase"/> 由本方法写入：它记录"用户是在哪个阶段点的恢复"，
    /// 是 <see cref="WaitForResumeSettledAsync"/> 唯一的起点真值（详见那里的现场证据）。
    /// </remarks>
    public Task ResumeAsync(string? password, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var ctx = Ctx;
        if (ctx is null)
        {
            SetUserHint("请先选择要恢复的任务（Step 2/4 的未完成任务列表）。");
            Log("WARN", "恢复任务被拒绝：尚未选择要恢复的任务。");
            LastRunOutcome = ActionOutcomePolicy.ForRun(new RunBusinessResult(Phase, "run-no-task", null));
            return Task.CompletedTask;
        }
        // ★ A.5（P1-5）★ 恢复同属"不可被节流延迟的量"：先 Flush（让"用户确认恢复"这行动作
        //   排在它之前的批量引擎行之后），再由 RunTransferCoreAsync 重新启泵。
        FlushPendingNow();
        // ★ D6.1 §16 观察点 ★ 用户**意图**（与 Core 侧的 PauseRequestCleared/Resumed 结果互补）。
        PublishRepair(TransferEvents.ResumeRequested,
            new TrnPauseObservedPayload("Cooperative", null),
            DiagnosticLevel.Information, DiagnosticOutcome.Accepted);
        ctx.ClearPauseRequest();
        // ★ FIX BATCH 2 ★ 恢复是"用户意图"，本轮起界面进入「正在恢复…」：暂停请求文件已清除，
        //   但引擎要等下一轮运行才重新开始传 ⇒ 此刻若继续显示"已暂停"就是假话，若显示"进行中"也是假话。
        _pauseRequestWritten = false;
        _pauseRetryAfterUtc = null;
        PauseUiState = PauseUiState.Resuming;
        // ★ FIX BATCH 7 ★ 恢复的"起点阶段"必须在**发起恢复时**记下（旧实现是在等待时才判断，
        //   而那时整轮运行可能已经跑完 ⇒ 判据恒 false，一次真实成功的恢复被记成 resume-unsettled）。
        _resumeOriginPhase = Phase;
        // ★ Round-2 2026-10-05（§2.1-6）★ 恢复前把显示连续性切到**追赶**模式：显示停在高水位，
        //   引擎 raw 追上后自动解除（→ Live）。高水位来源 = 之前的暂停/停止检查点 + 当前显示真值，
        //   只用引擎真值，绝不凭空造数；它只抬高 UI 显示，不参与 skip / verify / receipt / success。
        var resumeCandidate = Math.Max(
            _continuation.HighWaterBytes,
            Math.Max(LastTruth?.DisplayedTransferredBytes ?? 0L, PresentationTruth?.DisplayedTransferredBytes ?? 0L));
        if (resumeCandidate > 0)
        {
            _continuation.CaptureCheckpoint(ctx.JobId, resumeCandidate,
                Math.Max(_continuation.HighWaterPercent, Math.Max(LastTruth?.Percent ?? 0d, Percent)),
                _completedObjectsSeen, "resume-requested");
        }
        if (_continuation.BeginCatchUp(ctx.JobId))
        {
            _log.Information(
                "Resume 显示连续性：进入追赶模式 job={JobId} highWaterBytes={Bytes} —— 仅用于显示，不代表对象已完成（引擎真值追上后自动解除）",
                ctx.JobId, _continuation.HighWaterBytes);
        }
        // ★ Round-2（§3.2）★ 呈现时间线解除冻结：视觉**停在原处**（不归零、不重播），
        //   后续由已确认的显示真值驱动前进；引擎 raw 追上高水位后自然不再滞后。
        _presentation.ResumeLive(DateTime.UtcNow, "resume-requested");
        _log.Information("呈现时间线恢复跟随：job={JobId} visualPercent={Visual:0.###}（从用户已看到的进度继续）",
            ctx.JobId, _presentation.VisualPercent);
        Log("INFO", $"用户确认恢复任务 {ctx.JobId}（{PhaseText}）：已清除暂停请求，沿用 job.json 里的原线程值续传。");
        // ★ FIX BATCH 2 ★ 原地恢复：引擎那一次运行**还活着**（暂停达成于对象边界时，编排器正原地等
        //   请求文件消失）⇒ 请求刚被清除，引擎会在至多一个轮询周期后自己继续。
        //   这里**绝不能**再启动第二次运行：RunTransferCoreAsync 的 re-entry 守卫虽然拦得住，
        //   但会把"正在恢复…"改写成"已有一次运行在进行中"，用户看到的就是一次没说清的点击。
        if (IsRunning)
        {
            SetOperational("▶ 已恢复：引擎正在从暂停点继续传输（未重传已完成对象）。");
            return Task.CompletedTask;
        }
        // ★ UI Closure 2026-10-05（§2 P0）★ 这一次运行**是**"用户发起的恢复" ⇒ 携带显示连续性 floor。
        return RunTransferCoreAsync(ctx, password, onlyObjectIds: null, forceRecopy: false, progress, ct, resumeContinuity: true);
    }

    /// <summary>
    /// 暂停：请求引擎**立即中止当前对象的传输**（FIX BATCH 1 新语义：两种模式都不再"等当前对象传完"）。
    ///
    /// ★ FIX BATCH 2（去乐观谎报）★ 本方法只做两件**诚实**的事：
    ///   ① 把"用户要暂停"写进请求文件（写入结果如实报告）；
    ///   ② 把界面推进到「正在暂停…」——**不是**「已暂停」。
    /// 真正的"已暂停"只能由引擎真值推进（`ProgressSnapshot.PauseState == Paused`，见 <see cref="ApplyPauseTruth"/>）。
    /// 旧实现在这里写 `IsPaused = true` + `LastPauseOutcome = Accepted`，真机上因此出现
    /// "界面写着已暂停、传输从未停止"（JOB-20261004-171522-b785，8/8 次点击、0 次引擎确认）。
    /// </summary>
    public Task PauseAsync(bool immediate = false)
    {
        var ctx = Ctx;
        if (ctx is null || !IsRunning)
        {
            SetUserHint("当前没有正在进行的迁移，无需暂停。");
            // 没有在跑的运行 ⇒ 暂停请求不可能被受理：如实 Rejected（不写 Succeeded）。
            LastPauseOutcome = ActionOutcomePolicy.ForRequest(honored: false, "pause-requested", "pause-not-running");
            return Task.CompletedTask;
        }
        try
        {
            // ★ A.5（P1-5）★ 用户即时操作的反馈属"绝对不可被节流延迟的量"：
            //   先 Flush 掉累积的批量行，再写状态（状态句/按钮态在下一帧即变，不等到 50ms 节拍）。
            FlushPendingNow();
            // ★ Round-2 2026-10-05（§2.1-2）★ 用户点暂停**之前**先捕获显示检查点：必须早于写请求文件，
            //   否则引擎可能在同一个节拍里把 raw 收尾成 committed-only 值，检查点就会偏低。
            CaptureContinuationCheckpoint("pause-requested");
            // 重试标记必须**先于**写请求文件设置：引擎的 PauseRequestedAt 由它自己在本轮尝试里打时间戳，
            // 只有"重试时点 <= 引擎本轮时间戳"才说明引擎报的是**新一轮**结论（否则会被当成上一轮的旧结论）。
            _pauseRetryAfterUtc = DateTime.UtcNow;
            // ★ D6.1 §16 ★ 暂停请求（意图）——真正的写入结果由 Core 的 PauseRequestWriteResult 记录。
            PublishRepair(TransferEvents.PauseRequested,
                new TrnPauseObservedPayload(immediate ? "Immediate" : "Cooperative", null),
                DiagnosticLevel.Information, DiagnosticOutcome.Accepted);
            ctx.RequestPause(immediate);
            // ① 本地事实：请求已写入成功（引擎尚未表态）⇒ 界面只能说"正在暂停…"
            _pauseRequestWritten = true;
            PauseUiState = PauseUiState.Pausing;
            SetOperational("⏳ 正在暂停…已请求引擎中止当前对象的传输" +
                "（不再等当前对象跑完；已完成的对象不会重传）。");
            // D6.3 §11：这里能证明的只有"请求已受理"；**真停不停是异步的**（引擎会给出 PauseObserved/PauseFailed）。
            LastPauseOutcome = ActionOutcomePolicy.ForRequest(honored: true, "pause-requested", "pause-not-running");
        }
        catch (Exception ex)
        {
            SetErrorSummary($"暂停请求写入失败：{ex.Message}");
            _log.Warning(ex, "Pause 失败");
            _pauseRequestWritten = false;
            _pauseRetryAfterUtc = null;
            PauseUiState = PauseUiState.Idle;
            LastPauseOutcome = new ActionOutcomeDecision(DiagnosticOutcome.Failed, "pause-request-failed");
        }
        return Task.CompletedTask;
    }

    /// <summary>停止本次运行（取消令牌；已拷入的部分全部保留，可续传）。</summary>
    public Task StopAsync()
    {
        // D6.3 §11：本次停止请求有没有"在跑的运行"可取消（业务事实，来自既有 IsRunning）。
        var running = IsRunning;
        // ★ P0-06(1)/(2) ★ 用户操作本身就是"界面此刻可用"的最强证据：先打一拍心跳，
        //   这样事后能精确回答"最后一次可用交互发生在什么时候、是什么操作"。
        _uiStallWatchdog.Heartbeat("stop-click");
        // ★ P0-06(1) ★ 分段计时从"用户点击"这一刻开始（第一次请求建时间线，重入不重建）。
        var timeline = _lastStopTimeline;
        if (timeline is null || timeline.IsComplete)
        {
            timeline = new StopTimeline("user-stop");
            LastStopTimeline = timeline;
        }
        else
        {
            // ★ P0-06(2) ★ 重复按停止：绝不能开启第二个取消/第二段收尾，只如实记一笔。
            timeline.Mark("reentry-ignored");
        }
        try
        {
            if (IsStopping)
            {
                // 进行中：幂等返回（不重投取消、不重写状态），并把事实留给时间线。
                timeline.Mark("already-stopping");
                _log.Information("WinUI 会话停止请求被忽略（已在停止进行中）");
                return Task.CompletedTask;
            }

            // ★ P0-06(2) ★ 立即反馈必须先于任何可能耗时的工作：按钮文案/使能状态与状态句
            //   在 Flush 之前就落到 UI（现场"点了没反应"就是这里缺一段可见进行时）。
            IsStopping = true;
            timeline.Mark("feedback-shown");

            // ★ A.5（P1-5）★ 停止同属"不可被节流延迟的量"：先 Flush 再写状态。
            FlushPendingNow();
            timeline.Mark("flush-done");
            // ★ Round-2 2026-10-05（§2.1-4/5）★ 用户点停止**之前**先捕获显示检查点：停止是一次
            //   "可恢复的中断"，引擎随后会把这一轮收尾成 Interrupted 并从 committed 回执重建真值
            //   （真机视频证据：24.8% 在点击「停止」后**立刻**掉到 0.0%）——
            //   检查点让显示冻结在高水位，直到引擎 raw 追上来（绝不改回执、不改 skip/verify）。
            CaptureContinuationCheckpoint("user-stop");
            // ★ D6.1 §16 ★ 停止请求（意图）——结果由 Core 的 StopObserved 记录。
            PublishRepair(TransferEvents.StopRequested,
                new TrnPauseObservedPayload("Cancel", null),
                DiagnosticLevel.Information, DiagnosticOutcome.Accepted);
            _cts?.Cancel();
            timeline.Mark("cancel-delivered");
            SetOperational("正在停止…：已请求引擎停止，已拷入的部分全部保留，可点「恢复任务」续传。");
            _log.Information("WinUI 会话停止请求");
            _log.Information("{StopTimeline}", timeline.ToSummary());
            // 有在跑的运行 ⇒ 取消请求已投递（Accepted）；没有 ⇒ 什么都没停成，如实 Skipped。
            LastStopOutcome = running
                ? ActionOutcomePolicy.ForRequest(honored: true, "stop-requested", "stop-no-running-run")
                : new ActionOutcomeDecision(DiagnosticOutcome.Skipped, "stop-no-running-run");
            if (!running)
            {
                // 没有在跑的运行 ⇒ 不存在"引擎收尾"，进行时立即结束（否则按钮会永远卡在"正在停止…"），
                // 并且状态句必须改口：此刻什么都没停成，不能留着"正在停止…"骗人。
                timeline.Complete("stop-no-running-run");
                IsStopping = false;
                SetOperational("未在运行：没有可停止的迁移，未更改任何文件。");
            }
        }
        catch (Exception ex)
        {
            timeline.Complete("stop-failed", ex.Message);
            IsStopping = false;
            SetErrorSummary($"停止请求失败：{ex.Message}");
            LastStopOutcome = new ActionOutcomeDecision(DiagnosticOutcome.Failed, "stop-request-failed");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// ★ Round-2 2026-10-05（§2.1-2/4）★ 在**用户动作之前**把"用户此刻已经看到的进度"冻结成检查点。
    /// 只读显示真值（<see cref="PresentationTruth"/> → <see cref="LastTruth"/>），绝不凭空造数：
    /// 显示分子 ≤ 0 时什么都不做。它只影响显示连续性，**绝不**写回 committed / 回执 / skip 判定。
    /// </summary>
    private void CaptureContinuationCheckpoint(string reason)
    {
        try
        {
            var displayed = PresentationTruth?.DisplayedTransferredBytes
                            ?? LastTruth?.DisplayedTransferredBytes
                            ?? 0L;
            var percent = PresentationTruth?.Percent ?? LastTruth?.Percent ?? Percent;
            if (_continuation.CaptureCheckpoint(Ctx?.JobId, displayed, percent, _completedObjectsSeen, reason))
            {
                _log.Information(
                    "显示连续性检查点：reason={Reason} job={JobId} highWaterBytes={Bytes} percent={Percent:0.###} completedObjects={Objects}",
                    reason, Ctx?.JobId, _continuation.HighWaterBytes, _continuation.HighWaterPercent,
                    _continuation.HighWaterCompletedObjects);
            }

            if (reason is "pause-requested" or "user-stop")
            {
                // ★ Round-3 PHASE A（§4.5）★ 记下"动作边界基线"：用户点下暂停/停止那一刻的可信分子。
                //   之后引擎 raw 若在没有可信 I/O 增量支撑下比它高出一大截（P0：/Z 预分配被当成已传
                //   字节 ⇒ 30 GB → 41.96 GB），必须能报出来 —— 只查倒退是不够的。
                _continuation.MarkActionCheckpoint(displayed);
                // ★ Round-2（§3.2）★ 用户按下暂停/停止的**那一刻**视觉就必须冻在他当前看到的值上：
                //   引擎接着会把相位推到 Paused / Interrupted，并只按"已完成回执"重建分子；
                //   若不在这一刻冻住，界面就会先掉到 0.0% 再等恢复（用户视频 43.7 s → 44.0 s 的现象）。
                _presentation.Freeze(percent, DateTime.UtcNow, reason);
                _log.Information(
                    "呈现时间线已冻结：reason={Reason} visualPercent={Visual:0.###}（用户已看到的进度保持不变）",
                    reason, _presentation.VisualPercent);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "显示连续性检查点捕获失败（已吞掉，不影响显示与业务）");
        }
    }

    /// <summary>
    /// 核心传输路径（Run / Resume / Repair 共用一份实现，避免三套口径）。
    /// **默认（生产）路径 = Core 的唯一引擎**（见下方 engine 构造块）；不含对话框：
    /// 扫描残缺闸门未确认 → 拒绝开跑并把原因写在 StatusMessage。
    /// </summary>
    private async Task RunTransferCoreAsync(
        JobContext ctx,
        string? password,
        IReadOnlyCollection<string>? onlyObjectIds,
        bool forceRecopy,
        IProgress<string>? progress,
        CancellationToken ct,
        bool resumeContinuity = false)
    {
        if (IsRunning)
        {
            SetOperational("已有一次运行在进行中。");
            LastRunOutcome = ActionOutcomePolicy.ForRun(new RunBusinessResult(Phase, "run-already-running", null));
            return;
        }

        // ★ R2 接手修复 · 缺陷 K ★ 「校验中不许开跑」——与 VerifyAsync 里那条「运行中不许校验」
        //   完全对称。少了这一条，校验在飞时点「开始」会让传输引擎带着开跑那刻的内存快照周期性
        //   SaveState，把校验刚写下的 MissingObjects 覆盖回 0（缺陷 K 的原始现场）。
        if (IsVerifying)
        {
            SetOperational("正在验证中：请等验证结束后再开始迁移（此刻开跑会覆盖校验结论）。");
            LastRunOutcome = ActionOutcomePolicy.ForRun(new RunBusinessResult(Phase, "run-verify-in-progress", null));
            return;
        }

        // ★ Round-2 2026-10-05（§2.1-8）★ **只有"用户发起的恢复"**才保留显示连续性：
        //   重新开始（RunAsync）与修复（RepairAsync）都是新语义，新任务必须从 0 开始显示 ⇒ 显式解除。
        //   同时把 raw 回退基线复位（换了一轮运行的 raw 序列不可比）。
        if (!resumeContinuity && _continuation.Clear("new-run"))
            _log.Information("显示连续性已解除（本轮不是恢复：reason=new-run）");
        _continuation.ResetRawTracking(ctx.JobId);
        if (!resumeContinuity)
        {
            // ★ Round-2（§3.2 / §2.1-8）★ 全新运行 / 修复 = 新语义 ⇒ 呈现时间线一并归零，
            //   视觉从 0 开始（这是唯一允许"视觉回退到 0"的路径，且用户明确点了"开始/修复"）。
            _presentation.Reset(ctx.JobId, "new-run");
        }

        // ---- 扫描残缺闸门（v0.3.8）：未确认不允许开跑（Core 的权威判定，不重写规则）----
        var scanGate = ScanGate.Inspect(ctx);
        if (scanGate.Blocks)
        {
            foreach (var p in scanGate.Paths.Take(12)) AddFail("扫描不可访问", p);
            SetErrorSummary("已拦下：扫描存在不可访问位置。\n" + ScanGate.BuildBlockMessage(scanGate, cli: false));
            LastRunOutcome = ActionOutcomePolicy.ForRun(new RunBusinessResult(Phase, "run-scan-gate-blocked", null));
            return;
        }

        // 暂停/停止后的进程收尾有数秒窗口，恢复撞锁是实测过的坑——给 30 秒宽限（与 WPF 同口径）
        using var jobLock = JobLock.TryAcquire(ctx, TimeSpan.FromSeconds(30), out var lockReason);
        if (jobLock is null)
        {
            SetErrorSummary(lockReason);
            LastRunOutcome = ActionOutcomePolicy.ForRun(new RunBusinessResult(Phase, "run-job-lock-busy", null));
            return;
        }

        // ★ Preview.4 真机问题 1（2026-10-22）★ 实质阶段开始 —— 预检的"行动号召"到此必须失效。
        //   实机：预检通过后点了开始迁移、robocopy 已经在跑，提示区仍显示
        //   「检查通过，可以开始迁移。非阻断提醒：…」，与「正在传输」并存，用户以为还停在预检。
        //   根因：UserHint 是一条**一次性写入后无人回收**的通道（整轮只有预检与少数前置校验会写）。
        RetirePreflightCallToAction();
        IsRunning = true;
        IsFinished = false;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var hooks = new TransferEngineHooks(OnOutputLine, OnFileCopied, OnTransferNotice);

        // ★ A.5（P1-4）：Run / Resume / Repair **共用**的开跑前复位——上一轮的失败清单（含去重索引）、
        //   实时文件流、当前对象/文件、速率、停滞提示全部作废；收尾时（本方法 finally）由回执
        //   （Receipt，唯一权威）重建行状态与失败清单，所以复位 ≠ 清空事实。
        if (forceRecopy) IsRepairing = true;
        ResetRunProjection();

        // ★ A.5（P1-5/D2）★ 运行开始 = 启泵（唯一启动点）。位置在 ResetRunProjection() 之后：
        //   复位已经把缓冲与丢弃计数清干净，节拍从这里开始只会看到本轮的数据。
        StartFlushTimer();

        // ══════════════ 引擎执行：默认 = Core 唯一引擎 ══════════════
        // ★ 边界（必须遵守）：**默认路径必须是 Core 的唯一传输引擎；注入仅供测试**。
        //   · _transferRunner == null（生产恒为此）→ 直接构造 Core 的传输编排器，
        //     与 WPF MainViewModel.RunCoreAsync（L933-936 构造 + 三事件订阅 / L978 进度 / L1071-1073 收尾取消订阅）
        //     和 RepairAsync（L1701-1707 含 ForceOverwriteFromSource）逐项同口径；
        //   · _transferRunner != null  → **只有单测**在注入假实现，用来在无网络/无磁盘的条件下验证
        //     会话层的进度投影与状态机；绝不可作为生产装配路径（装配层不得设置它）。
        var jobLog = LogBootstrap.CreateJobLogger(ctx.JobDir, ctx.JobId);
        var matrix = MigrationMatrix.Load(null, jobLog);
        TransferOrchestrator? orchestrator = null;
        Func<IProgress<ProgressSnapshot>, CancellationToken, Task<JobPhase>> executeEngine;
        if (_transferRunner is null)
        {
            orchestrator = new TransferOrchestrator(ctx, matrix, jobLog)
            {
                ForceOverwriteFromSource = forceRecopy,
            };
            orchestrator.OutputLine += OnOutputLine;
            orchestrator.FileCopied += OnFileCopied;
            orchestrator.TransferNotice += OnTransferNotice;
            var engine = orchestrator;
            executeEngine = (snapshot, token) => engine.RunAsync(snapshot, token, onlyObjectIds, forceRecopy);
        }
        else
        {
            var injected = _transferRunner;
            executeEngine = (snapshot, token) => injected(ctx, hooks, snapshot, token, onlyObjectIds, forceRecopy);
        }

        // ---- 传输前显式建立 SMB 会话（不依赖 Windows 恰好还缓存着上次会话）----
        var effUser = !string.IsNullOrWhiteSpace(Username) ? Username : ctx.Definition.SourceUser;
        IDisposable? session = null;
        if (!string.IsNullOrEmpty(effUser) && !string.IsNullOrEmpty(password))
        {
            try
            {
                var sources = ctx.Definition.Sources.Select(s => s.Path).ToList();
                var pwd = password;
                var user = effUser;
                session = await Task.Run(() => NetworkShare.ConnectForTransfer(
                    ctx.Definition.SourceHost, user, pwd, sources), _cts.Token);
            }
            catch (Exception ex)
            {
                SetErrorSummary($"连接失败：{ex.Message}");
                _log.Error(ex, "WinUI 传输前连接失败");
                LastRunOutcome = ActionOutcomePolicy.ForRun(new RunBusinessResult(Phase, null, "run-smb-connect-failed"));
                CleanupRun(session);
                return;
            }
        }
        else if (!string.IsNullOrEmpty(effUser))
        {
            // 不硬拦：Windows 可能还缓存着连接；失败原因由引擎照实报（与 WPF Repair 口径一致）
            SetSecurityHint($"该任务使用账号 {effUser}：本次未提供密码，若 Windows 仍缓存着连接可正常继续，否则会以共享不可用失败。");
        }

        // ★ A.5（P1-5）★ 快照走 **VM 自己的同步 IProgress 实现**（不再用 `Progress<T>`）：
        //   · 省掉每个快照 1 个 Dispatcher 工作项；
        //   · 快照可以在引擎线程直接落到"最新值槽位"（最新值胜出），由 50ms 节拍上屏。
        //   ⚠ 高危点：Core 调用处 `progress?.Report(...)`（TransferOrchestrator.cs:820）**没有 try/catch**，
        //     而 `Progress<T>` 是**异步投递**所以永不外抛。换成同步实现后，Report 里任何异常都会
        //     直接杀死传输泵 ⇒ SnapshotSink.Report **必须自带 try/catch 且绝不上抛**。
        var snapshotProgress = new SnapshotSink(OnSnapshotReported);
        var runFaulted = false;
        try
        {
            Phase = JobPhase.Running;
            var phase = await executeEngine(snapshotProgress, _cts.Token);
            await FinishRunAsync(ctx, phase, onlyObjectIds, ct);
        }
        catch (OperationCanceledException)
        {
            Phase = JobPhase.Interrupted;
            SetOperational("已取消（可续传）：已拷入的部分保留，点「恢复任务」继续。");
        }
        catch (Exception ex)
        {
            runFaulted = true;
            // F5：不再把英文原始异常串（含内部临时文件名）直接抛到界面上；译成"哪个存储不可写/任务什么状态/怎么办"，
            //      原始异常（含堆栈）照旧进日志。
            SetErrorSummary(PCMig.Core.Util.TransferFailureTranslator.Explain(ex));
            _log.Error(ex, "WinUI 传输异常");
        }
        finally
        {
            // ★ A.5（P1-5）★ **先 Flush 再收尾**：否则最后一批引擎日志会与"阶段结束"类动作行
            //   顺序颠倒，或者那批日志**永远不上屏**（R1 尾边丢失）。
            FlushPendingNow();

            // 收尾取消订阅（与 WPF RunCoreAsync L1071-1073 同口径）：引擎对象不跨运行存活
            if (orchestrator is not null)
            {
                orchestrator.OutputLine -= OnOutputLine;
                orchestrator.FileCopied -= OnFileCopied;
                orchestrator.TransferNotice -= OnTransferNotice;
            }
            CleanupRun(session);
            await RefreshRowsFromReceiptsAsync();
        }

        // D6.3 §11：开始/恢复的实际业务结果 = 既有 JobPhase（Completed/CompletedWithErrors/
        // Paused/Interrupted/Failed）+ 是否抛异常。**"方法返回了"不是成功**。
        LastRunOutcome = ActionOutcomePolicy.ForRun(new RunBusinessResult(
            Phase, BlockedReason: null, FailedReason: runFaulted ? "run-faulted" : null));
    }

    /// <summary>运行收尾：只做资源与状态复位（引擎事件已在 finally 里取消订阅）。</summary>
    private void CleanupRun(IDisposable? session)
    {
        // ★ A.5（D2）★ **唯一的 timer 停止点**：任务结束 / 取消 / 异常 / 收尾都汇聚到这里。
        //   放在最前：先掐掉节拍源，后续任何状态复位都不会再被一次在途 Tick 覆盖。
        //   幂等（StopFlushTimer 内判 null），重复调用无副作用。
        StopFlushTimer();

        try { session?.Dispose(); } catch { /* 释放失败不影响收尾 */ }
        _cts?.Dispose();
        _cts = null;
        IsRunning = false;
        IsRepairing = false;
        // ★ FIX BATCH 2 ★ 收尾时的暂停真值（不再 `IsPaused = Phase == Paused` 一句了事）：
        //   · 引擎确认过 PauseFailed ⇒ **保留失败态**：FinishRunAsync 必须把"那次暂停其实没成功"如实写进状态句，
        //     绝不能因为"运行结束了"就把它忘掉（否则用户永远不知道自己的暂停请求落空了）；
        //   · 阶段确实是 Paused（引擎/persisted 真值）⇒ 保留"已暂停"，用户还需要看到它才能点恢复；
        //   · 其余（运行中途结束、请求还挂着但从未达成）⇒ 回到"没有暂停在进行"，并清掉本地请求标记。
        if (PauseUiState != PauseUiState.PauseFailed)
        {
            _pauseRequestWritten = false;
            _pauseRetryAfterUtc = null;
            PauseUiState = Phase == JobPhase.Paused ? PauseUiState.Paused : PauseUiState.Idle;
        }
        _lastCompletedCount = -1;
        // ★ FIX BATCH 6（§9）★ 运行收尾 ⇒ 引擎不再有"当前对象"：对象通道必须清空，
        //   否则左侧提示面板会残留最后一次快照的对象名（"运行都结束了还写着正在传某个对象"）。
        SetChannel(StatusChannel.CurrentObjectStatus, string.Empty);
        RaiseDerived();
    }

    /// <summary>
    /// ★ P0-05 ★ 引擎侧结构化信号 → 会话层源灯：把"本次运行结束时的错误文本"翻译成共享可达性事实。
    ///
    /// 为什么放在收尾而不是传输中：错误文本的**权威副本**是作业状态里的
    /// <see cref="JobState.LastError"/>（引擎每趟运行自己写入），会话层读到它时结论已经稳定，
    /// 不会因为一行被覆盖的流式输出而误判。灯只认证据：这里落下的是"某条源根不可读 + 原因"。
    /// 计划里没有源根时（计划都读不出来）退化为 <see cref="ConnectionViewModel.NotifySourceLost"/>。
    /// </summary>
    private void ApplyShareLossSignalFromRunEnd(JobContext ctx, JobState end)
    {
        if (_connection is null) return;
        if (!ShareLossSignal.TryMatchShareLossText(end.LastError, out _, out var evidence)) return;

        var roots = ctx.Plan?.Objects
            .Select(o => o.SourcePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        if (roots.Count == 0)
        {
            _connection.NotifySourceLost();
        }
        else
        {
            foreach (var root in roots) _connection.NotifyShareReachability(root!, false, evidence, authoritative: true);
        }
        Log("WARN", $"引擎报告共享级错误（{evidence}）：已把源连接灯拉回真实状态（不再显示已连接）。");
    }

    private async Task FinishRunAsync(JobContext ctx, JobPhase phase, IReadOnlyCollection<string>? onlyObjectIds, CancellationToken ct)
    {
        // ★ A.5（P1-5）★ **必须先 Flush 再写结束态**：结束态（Percent/StatusMessage/IsFinished）
        //   属于"绝对不可被节流延迟的量"，而它之前累积的批量引擎行必须**先**上屏，
        //   否则日志尾部顺序会颠倒（动作行在前、它之前的引擎行在后）。
        FlushPendingNow();

        var end = ctx.LoadStateOrNew();
        var spaceShort = end.LastError?.Contains("空间不足") == true;

        // ★ P0-05（引擎侧结构化信号）★ 本次运行的收尾错误若是**共享级**错误
        //（Win32 53/55/59/64/67/1219/1231/1236/1326），立刻把底栏源灯拉回真相：
        // 现场共享被公司策略关闭后，引擎一路报"错误 67"，旧版界面却照旧显示"已连接"。
        // 文件锁（32/33）在此被明确排除——那是单个文件被占用，不是共享断开。
        ApplyShareLossSignalFromRunEnd(ctx, end);
        Phase = phase;
        // ★ A.5（P1-3）：**只有真正的 Completed 才是 100%**。
        //   CompletedWithErrors 可能对应"目标盘满 / 网络故障 / 大量失败 / 被迫中断"——实际只传了一部分
        //   （例：计划 320GB、实际 80GB）。此时 UI 显示 100% 会让用户以为传完了，是产品诚实性缺陷。
        //   IsFinished（流程是否结束）继续包含两者（流程语义），但**百分比必须区分**。
        var completed = phase == JobPhase.Completed;
        var finished = completed || phase == JobPhase.CompletedWithErrors;
        // ★ P0-04 ★ 本次运行是不是"定向修复重拷"（onlyObjectIds 非空）：它决定 DataLabel 与结束句的措辞。
        //   修复轮的百分比量的是**重拷完成度**，与"迁移完成度"不是同一件事——标签必须写清它测的是什么，
        //   否则用户看到 100% 就会以为"修好了"（这正是现场症状）。
        var isRepairRun = onlyObjectIds is { Count: > 0 };
        IsFinished = finished;
        // ★ v0.5.4 Preview.2 真机修复（2026-10-16，用户任务令 §五）★
        //   可信完成判据在此**唯一**落定（拿到真实 JobState 的地方只有这里）。
        //   注意它**不**参与上面的 Percent 显示逻辑（Percent 另有其诚实口径），
        //   只回答"能不能把用户自动送到 Step4"这一个问题。
        ApplyTrustworthyCompletion(end);
        // ★ R2 接手修复 · 缺陷 B ★ 外壳不能再无条件写 100%：
        //   旧写法 `if (completed) Percent = 100.0;` 只认引擎返回的 phase，比 Core 的可信完成判据宽得多
        //   （不看字节闭环、不看校验期确证缺失），于是会出现「进度条 100% + 已完成 + 不跳 Step4」。
        //   现在唯一放行条件是自己的 TrustworthyCompletion —— 它与 Core 用同一个出口，
        //   因此「显示 100%」与「可信完成」结构上不可能再不一致。
        //   ⚠ 不可信时退回 ActualPercent（实测落盘比例），绝不显示 100%，也绝不因失败而抹成 0。
        //   ★ 缺陷 J ★ 必须经 PublishPercent：界面上三处读数都是 `truth?.Percent ?? Session.Percent`，
        //   只改 Percent 会被那份旧真值盖掉（详见 PublishPercent 的说明）。
        //   ★ 缺陷 J ★ 收尾是"更强的证据改小结论"的时机（校验期确证缺失会把 100% 压回 99.9），
        //   因此这里允许把 80 ms 节拍的视觉值一并压下去，否则底栏大号数字仍停在旧值。
        if (TrustworthyCompletion) PublishPercent(100.0, allowVisualSnap: true);
        else if (finished) PublishPercent(Math.Min(ActualPercent(end), TransferOrchestrator.SettledPercentCapPublic), allowVisualSnap: true);
        DataLabel = isRepairRun
            ? (finished ? "修复重拷 / 计划（待复验）" : "已传 / 计划")
            : (finished ? "实际落盘 / 计划" : "已传 / 计划");
        BalanceText = $"计划 {Format.Bytes(end.TotalBytes)}　·　实际落盘 {Format.Bytes(end.CompletedBytes)}";

        SetOperational(phase switch
        {
            // ★ P0-04 ★ 修复轮的收尾**绝不允许**出现"✔ 迁移完成！"：修复是否成功由紧随其后的**复验结论**决定，
            //   而"重拷完成"只说明活干完了。措辞故意留出结论位，并明说"重拷完成 ≠ 修复成功"。
            JobPhase.Completed when isRepairRun =>
                "◇ 本次修复的重拷已完成，正在按基础一致性复验…（重拷完成 ≠ 修复成功，结论见本条后续文字）",
            JobPhase.CompletedWithErrors when isRepairRun =>
                $"◐ 本次修复的重拷**未完整完成**（{(spaceShort ? "目标磁盘空间不足，已提前停止" : "存在失败对象")}，" +
                $"重拷完成 {ActualPercent(end):0.#}%）：修复尚未达成，正在复验…",
            JobPhase.Completed => "✔ 迁移完成！建议点「验证」确认一致性，然后「打开报告」。",
            // 两条分支都必须明确"未完整完成"，并给出实际完成比例与可恢复路径；
            // 任何让人以为"全部完成"的措辞都不允许（A.5/P1-3）。
            JobPhase.CompletedWithErrors when spaceShort =>
                $"◐ 迁移**未完整完成**（目标磁盘空间不足，本次运行已提前停止，避免对每个文件反复重试）：" +
                $"实际完成 {ActualPercent(end):0.#}%（{Format.Bytes(end.CompletedBytes)} / {Format.Bytes(end.TotalBytes)}）。" +
                "释放空间后点「恢复任务」，已完成的对象不会重传。",
            JobPhase.CompletedWithErrors =>
                $"◐ 迁移**未完整完成**（存在失败对象，实际完成 {ActualPercent(end):0.#}%：" +
                $"{Format.Bytes(end.CompletedBytes)} / {Format.Bytes(end.TotalBytes)}）。" +
                "失败清单已逐条列出「对象号 + 路径 + 退出码译文」；请打开报告核对，处理后点「恢复任务」只补差异。" +
                // ★ 观察项 O-B03-2（Low，已确认）★ 权限被拒的子目录会在目标端留下**同名空目录**：
                //   目录结构看着"像已经迁过去了"，点开却是空的。不构成数据损坏（失败项已在清单里），
                //   但人工核对时会被误导，所以必须在结果页说清楚，不能只靠用户自己发现。
                "　另请注意：**因权限被拒而没能读取的子目录，目标端可能只留下一个同名空目录（里面没有任何文件）**——" +
                "看到空目录不等于已迁移，请以本页失败清单为准。",
            // ★ UI Closure 2026-10-05（用户指令 UI-02）★ 暂停文案与"保留真实进度"同口径：
            //   百分比取自**未归零**的真值，并同时给出剩余字节 —— 避免出现"剩余 100% 未传"却已有
            //   已传字节的自相矛盾表述（暂停打断第一个大对象时最容易触发）。
            JobPhase.Paused =>
                $"‖ 已暂停：已完成 {end.CompletedObjects}/{end.TotalObjects} 个对象，剩余 {Format.Bytes(Math.Max(0, end.TotalBytes - end.CompletedBytes))}（{Math.Max(0, 100.0 - end.Percent):0.#}%）未传——点「恢复任务」续传。",
            JobPhase.Interrupted => "⏸ 已中断（可续传）：点「恢复任务」继续。",
            JobPhase.Failed => $"✘ 迁移失败：{end.LastError ?? "详见日志"}",
            _ => $"阶段结束：{phase}",
        });

        // ★ P0-04 ★ **封版**：终局结论已经写出（Phase / IsFinished / Percent / DataLabel / StatusMessage）。
        //   从这里开始，任何"还在路上"的旧进度快照都不许再改显示位——现场"结果页已给出失败结论，
        //   底栏却又跳回运行中的百分比 / 结果被旧的运行中快照覆盖"就是这类过期回调造成的。
        //   下面的修复复验结论、错误摘要都是**直接写入**（不受封版影响）；解除封版只发生在
        //   "新一次运行 / 换任务"（ResetRunProjection）。
        _progressSealed = true;

        // ★ FIX BATCH 2 ★ 收尾时绝不"忘记"一次未达成的暂停：用户的暂停请求失败过、而迁移后来跑完了，
        //   这件事必须留在状态句里（Diagnostics 侧的 expectation FAILED + incident 由 BATCH 3 承担）。
        //   旧实现里这个信息只活到下一次快照为止，用户永远不知道自己那次暂停其实落空了。
        if (_enginePauseState == PauseState.PauseFailed && phase != JobPhase.Paused)
        {
            SetErrorSummary("⚠ 本次运行中有一次暂停请求**没有达成**（" +
                (_enginePauseFailureReason ?? "引擎未能在硬失败 SLA 内停止当前对象的传输") +
                "）；下面的结束状态是它之后的真实结果。" + Environment.NewLine + StatusMessage);
        }

        if (isRepairRun)
        {
            // 定向修复路径：重拷结束后自动复验（与 WPF RepairAsync 同口径）
            IsRepairing = false;
            var repairCount = onlyObjectIds!.Count;
            SetOperational($"重拷结束（{phase}），正在重新校验…");
            Log("INFO", $"重拷结束（{phase}）：正在重新校验（基础一致性检查）…");
            await VerifyAsync(VerifyLevel.L1_CountSize, null, ct, null, allowDuringRun: true);

            // ── ★ P0-04 ★ 这次修复到底算不算成功，由**复验结论**说了算，不由"重拷跑了多少"说了算 ──────
            //   现场症状："修复的进度跑到 100%，可最后验证还是失败"，用户据此以为已经修好了。
            //   所以这里必须用刚写入的复验报告（ctx.LoadVerify() = 上一步 SaveVerify 的同一份）判定，
            //   并把结论明确写进状态句：
            //     · 通过   → 修复达成，此时百分比 100% 名实相符；
            //     · 未通过 → 进度**可能确实**停在 100%（重拷的活干完了），但结论必须明说"未达成"、
            //                并写明"100% 只表示重拷完成度，不代表一致性达成"，同时置起
            //                ProgressNeedsAttention 让页面把百分比/进度条转为警示色。
            //   读报告失败时**按未通过处理**——不许把"读不到结论"当成"修好了"。
            VerifyReport? repairVerify = null;
            try { repairVerify = ctx.LoadVerify(); }
            catch (Exception ex) { _log.Warning(ex, "读取复验报告失败：本次修复的结论按未通过处理（不假装成功）"); }
            var verifyPassed = repairVerify?.OverallPass == true;
            var mismatchCount = repairVerify?.Objects.Count(o => o.Status == "MISMATCH") ?? 0;
            var unknownCount = repairVerify?.Objects.Count(o => o.Status == "UNKNOWN") ?? 0;
            // ★ F2b（D16 实测：点「修复」重拷一趟后界面没有任何新提示，用户看不出修复做了什么）★
            //   复验文案会把上面那句"重拷结束"整个覆盖掉，而"这次修复重拷了哪些对象"是用户唯一能拿到的
            //   修复记录（作业记录里的 attempt 现在会递增，但界面必须自己说清楚）。两者合成一句，不丢信息。
            var repairScope = repairCount == 1 ? "1 个对象" : $"{repairCount} 个对象";
            ProgressNeedsAttention = !verifyPassed;
            DataLabel = verifyPassed ? "修复重拷 / 计划（复验通过）" : "修复重拷 / 计划（复验未通过）";
            // ★ R2 接手修复 · 缺陷 H（S-2）★ 修复轮的"100%"也必须过同一道闸门。
            //   改前这里只判 `verifyPassed`（= 复验报告 OverallPass），而 OverallPass 只说明
            //   "这一轮复验没有发现差异"，与"字节账是否闭环"是两个互不校验的维度；
            //   历史任务里存在 OverallPass=true 但 completedBytes != totalBytes 的形态。
            var repairState = ctx.LoadStateOrNew();
            ApplySettledCompletion(repairState);
            if (verifyPassed)
            {
                SetOperational(TrustworthyCompletion
                    ? $"✔ 修复完成（本次重拷 {repairScope}，进度 100%）→ {StatusMessage}"
                    : $"✔ 复验通过（本次重拷 {repairScope}，进度 {Percent:0.0}%）→ {StatusMessage}");
                Log("INFO", $"修复完成：本次重拷 {repairScope} + 复验通过（结论见状态栏）。");
            }
            else
            {
                var counts = unknownCount > 0
                    ? $"差异 {mismatchCount} 个对象 / 无法确认 {unknownCount} 个对象"
                    : $"差异 {mismatchCount} 个对象";
                SetOperational(
                    $"✘ **本次修复未达成**：重拷 {repairScope} 已完成（进度百分比只表示**重拷完成度**，" +
                    $"**不代表一致性已达成**），但复验未通过（{counts}）→ {StatusMessage}");
                Log("WARN", $"修复未达成：本次重拷 {repairScope} 完成，但复验未通过（{counts}）" +
                    "——进度条到 100% 不代表修好了；请按失败清单处理后再次点「修复」。");
            }
            Log("INFO", $"本次修复重拷范围：{repairScope}（复验结论见上方状态栏）");
        }
        await RefreshExistingJobsAsync(null, ct);

        // ★ C04B-1（2026-10-04 真机：LAB-DST01 硬断电 → 恢复 → 把那个被中断的任务续传完成）★
        //   Step4 的「未完成任务检测」那句是**进入本页时的探测快照**，不会自己过期。
        //   现场同屏同时出现两句话：
        //     ①「迁移已完成，可在本页做基础一致性检查并打开报告。…」
        //     ②「检测到未完成的迁移任务：任务 JOB-…｜…｜Interrupted。点「恢复任务」可从断点继续…」
        //   用户会被 ② 误导为"还要再恢复一次"。任务一结束就重新探测一次，让这句话与真实状态一致
        //   （探测自带世代号，旧的在途结果不会反向覆盖；未连接时探测入口本来就不做 IO）。
        if (IsSourceConnected) RunUnfinishedProbeNow();
    }

    // ────────────────────────── 方法：验证 / 修复 ──────────────────────────

    /// <summary>
    /// 验证（默认 L1）。**必须先通过闸门**（无计划 / 0 对象 / 源不可达一律拒绝）。
    /// 结论文案统一由 <see cref="DescribeVerifyOutcome"/> 产出：L1 绝不说成"完整性验证通过"。
    /// </summary>
    /// <param name="detail">
    /// ★ P1-12 ★ 结构化真实进度（验收 R9）：验证面板用它显示真实计数。
    /// 纯只读投影——不参与任何判定，投递失败/抛异常都不会影响验证结论。
    /// </param>
    /// <param name="allowDuringRun">
    /// ★ R2 接手修复 · 缺陷 I（2026-10-17）★ **只有修复轮自动复验可以传 true**。
    /// <para>
    /// 缺陷原文：修复（Repair）走的是 <c>RunAsync</c> 那条主路径，<c>IsRunning = true</c> 在
    /// <c>RunTransferCoreAsync</c> 开头置起、要等 <c>finally</c> 里的 <c>CleanupRun</c> 才复位；
    /// 而 <c>FinishRunAsync</c> 的 <c>isRepairRun</c> 分支就在这中间调 <c>VerifyAsync</c>。
    /// 于是那句 <c>if (IsRunning) { ... return; }</c> 会把**修复轮的自动复验整个拒掉**：
    /// 复验从未真的跑过，<c>ctx.LoadVerify()</c> 读到的是上一轮的旧报告（或 null），
    /// 于是"修复到底成没成"永远判成未通过，而界面上看到的却是重拷已 100%。
    /// <para>
    /// 语义边界：用户手点「验证」时 <c>IsRunning</c> 为 true 必须继续拒绝（运行中核对目标没有意义）；
    /// 修复轮的自动复验发生在**重拷已经结束之后**，此时拒绝它的理由并不存在。
    /// 故这里不放宽判据，只是为"引擎自己发起的、时点在重拷之后"的复验开一条显式通道，
    /// 并且要求调用方逐字写明 <c>allowDuringRun: true</c> —— 让后来的人一眼看出这是刻意为之。
    /// </para>
    /// </param>
    public async Task VerifyAsync(VerifyLevel level = VerifyLevel.L1_CountSize, IProgress<string>? progress = null, CancellationToken ct = default, IProgress<VerifyProgressSnapshot>? detail = null, bool allowDuringRun = false)
    {
        // ★ R2 接手修复 · 缺陷 K ★ 闸门位必须在**走进校验体的第一刻**就置起来，
        //   而且必须覆盖所有提前返回（无任务 / 运行中拒绝 / 闸门拒绝），故用 try/finally 包住主体：
        //   漏掉任何一条 return 路径，界面就会永久停在「正在验证」，三个按钮再也点不动。
        var wasVerifying = IsVerifying;
        IsVerifying = true;
        try
        {
            await VerifyCoreAsync(level, progress, ct, detail, allowDuringRun).ConfigureAwait(true);
        }
        finally
        {
            IsVerifying = wasVerifying;
        }
    }

    private async Task VerifyCoreAsync(VerifyLevel level = VerifyLevel.L1_CountSize, IProgress<string>? progress = null, CancellationToken ct = default, IProgress<VerifyProgressSnapshot>? detail = null, bool allowDuringRun = false)
    {
        LastVerifyReport = null;   // 每次开跑先清掉上一次的报告：界面绝不能把旧结论当本次结果展示
        // ★ K5（2026-10-12 实测缺口）★ 验证动作原先只写界面日志（Log → 本页日志卡），**不进 app 日志**：
        //   现场用 %ProgramData%\PCMig\Logs\app-*.log 定位「点击验证 → 验证开始/结束」时 grep「验证」零命中，
        //   只能靠 verify-report.json 的 mtime 反推时刻。用户在远程排障时读的是 app 日志，因此三处
        //   （开始 / 结束 / 取消或失败）都要留可定位的证据，并带上级别、任务号、对象数与真实耗时。
        var verifyClock = System.Diagnostics.Stopwatch.StartNew();
        var ctx = Ctx;
        if (ctx is null)
        {
            SetUserHint("尚无任务：不能验证。");
            Log("WARN", "验证被拒绝：尚无任务。");
            _log.Warning("验证被拒绝（未开始核对）：尚无任务。级别={Level}", DescribeLevel(level));
            LastVerifyOutcome = ActionOutcomePolicy.ForVerify(new VerifyBusinessResult(false, false, "verify-no-task", false, false));
            return;
        }
        if (IsRunning && !allowDuringRun)
        {
            SetUserHint("迁移/修复进行中：请先结束运行再验证。");
            Log("WARN", "验证被拒绝：迁移/修复进行中。");
            _log.Warning("验证被拒绝（未开始核对）：迁移/修复进行中。任务={JobId}，级别={Level}",
                ctx.JobId, DescribeLevel(level));
            LastVerifyOutcome = ActionOutcomePolicy.ForVerify(new VerifyBusinessResult(false, false, "verify-already-running", false, false));
            return;
        }

        var gate = await EvaluateVerifyGateAsync(ct);
        if (!gate.CanVerify)
        {
            AddFail("验证被拒绝", gate.Reason);
            SetErrorSummary(gate.Reason);
            Log("WARN", "验证被闸门拒绝：" + gate.Reason);
            _log.Warning("验证被闸门拒绝（未开始核对）：任务={JobId}，级别={Level}，已用 {Elapsed} ms，原因={Reason}",
                ctx.JobId, DescribeLevel(level), verifyClock.ElapsedMilliseconds, gate.Reason);
            LastVerifyOutcome = ActionOutcomePolicy.ForVerify(new VerifyBusinessResult(false, false, "verify-gate-blocked", false, false));
            return;
        }

        Phase = JobPhase.Verifying;
        SetOperational($"正在验证（{DescribeLevel(level)}）…");
        Log("INFO", $"开始验证：{DescribeLevel(level)}（闸门通过：有计划且源路径可达）");
        _log.Information("验证开始：级别={Level}，任务={JobId}，计划对象数={ObjectCount}（闸门已实测源根可达）",
            DescribeLevel(level), ctx.JobId, ctx.Plan?.Objects.Count ?? 0);
        VerifyReport? report = null;
        var canceled = false;
        var faulted = false;
        try
        {
            var jobLog = LogBootstrap.CreateJobLogger(ctx.JobDir, ctx.JobId);
            var matrix = MigrationMatrix.Load(null, jobLog);
            var verifier = new Verifier(ctx, matrix, jobLog);
            // ★ P1-12 实测修复（2026-10-11，真实 UI 证据）★
            //    旧写法 `await verifier.RunAsync(...)` 直接跑在 UI 线程上：Verifier 在对象循环开始前是
            //    **同步枚举**（本机实测 12,000 文件约 94 ms、96,000 文件约 769 ms，网络共享只会更慢），
            //    于是 UI 线程被占住，"验证完整性"点下去后第二窗口要等枚举结束才渲染得出来：
            //    实测窗口在点击后 2066 ms 才出现，而验证本身只用了 769 ms ⇒ 取消按钮从"还没出现"直接
            //    跳到"已结束、已禁用"，用户根本没有取消的机会（R9 要求取消有效）。
            //    按本类既有的线程纪律（阻塞式 Core 调用一律 Task.Run 包裹）放到后台线程；
            //    progress/detail 都是 UI 线程上创建的 Progress<T>，回调仍会 marshal 回 UI 线程。
            report = await Task.Run(() => verifier.RunAsync(level, progress, ct, detail), ct);
            ctx.SaveVerify(report);
            // ★ R2 接手修复 · 缺陷 A ★ 校验结论回流 job-state，并立刻重算"可信完成"：
            //   在此之前 VerifyAsync 从不刷新 TrustworthyCompletion，界面一直拿着**迁移期**的结论，
            //   于是"迁移说完成、校验发现目标少文件"这件事既写不进状态、也改不动界面与自动跳转。
            // ★ R2 接手修复 · 缺陷 H（S-3）★ 这里还必须**连百分比一起重算**：
            //   改前只刷了 TrustworthyCompletion 而没动 Percent ⇒ 用户点「验证」发现目标少文件后，
            //   「进度 100%」与「校验确证缺失、不可信完成」会同时留在界面上（Step3 与底栏都读 Percent），
            //   而 _progressSealed 之后没有任何快照能再改它。校验是比迁移更强的证据，必须能把它压下去。
            ApplyVerifyConclusion(ctx, report);
            // ★ P0-05 ★ 验证报告里每个对象的 SourceUnavailable 都是"源根当时真的读不到"的实测结论，
            //   是整个程序里关于源共享是否还在的**最硬证据**；据此把底栏源灯拉回真实状态。
            SyncSourceLampFromVerify(report);
            _hasVerifyReport = true;
            TotalHashSampled = report.Objects.Sum(o => o.HashSampled);
            LastVerifyResultText = DescribeVerifyOutcome(report, level);
            // ★ UX-2（用户现场观察）★ 左侧提示框只放一句话结论（状态 + 四态数量，不列对象编号）；
            //   完整结论（含少量示例编号）留在本页结论行，逐对象明细落到验证报告。
            LastVerifyHintText = DescribeVerifyHint(report, level);
            SetOperational(LastVerifyHintText);
            // 结论行：文案只能来自 DescribeVerifyOutcome（L1 绝不说成"完整性验证通过"）。
            Log("INFO", "验证结论：" + LastVerifyResultText);
            // 内容级证据行：抽样数为 0 时必须明确说"不代表内容一致"（NoHashSampleText）。
            Log(HasContentLevelEvidence ? "INFO" : "WARN",
                HasContentLevelEvidence
                    ? $"内容级抽样：本次抽样 {TotalHashSampled} 个文件做了 SHA-256 双向核对（抽样通过 ≠ 全部文件内容一致）。"
                    : $"内容级抽样：{NoHashSampleText}（本次抽样文件数 {TotalHashSampled}）。");
            ApplyVerifyToRows(report);
            Raise(nameof(CanRepair));
            await RefreshExistingJobsAsync(null, ct);
            _log.Information(
                "验证结束：任务={JobId}，级别={Level}，运行号={RunId}，耗时={Elapsed} ms，对象数={Count}，" +
                "总体结论={OverallPass}（通过 {Ok} 存在差异 {Mismatch} 无法确认 {Unknown} 无可核对数据 {Empty}）",
                ctx.JobId, DescribeLevel(level), report.VerifyRunId, verifyClock.ElapsedMilliseconds, report.Objects.Count,
                report.OverallPass,
                report.Objects.Count(o => o.Status == "OK"),
                report.Objects.Count(o => o.Status == "MISMATCH"),
                report.Objects.Count(o => o.Status == "UNKNOWN"),
                report.Objects.Count(o => o.Status == "EMPTY"));
        }
        catch (OperationCanceledException)
        {
            canceled = true;
            // ★ UX-2 ★ 提示卡同样只给一句话；取消**不是**通过，也不产生结论。
            LastVerifyHintText = "验证已取消：没有结论（取消不等于通过）。重新验证后才能判断。";
            SetOperational(LastVerifyHintText);
            Log("WARN", "验证已取消：没有结论（取消不等于通过）。");
            _log.Warning("验证已取消：没有结论（取消不等于通过）。任务={JobId}，级别={Level}，已用 {Elapsed} ms，" +
                         "未写出任何验证报告（取消不产生报告）",
                ctx.JobId, DescribeLevel(level), verifyClock.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            faulted = true;
            LastVerifyHintText = $"验证失败：没有结论（{ex.Message}）。详见下方错误摘要与本页日志。";
            SetOperational(LastVerifyHintText);
            SetErrorSummary($"验证失败：{ex.Message}");
            Log("ERROR", $"验证失败：{ex.Message}");
            _log.Error(ex, "WinUI 验证失败：任务={JobId}，级别={Level}，已用 {Elapsed} ms",
                ctx.JobId, DescribeLevel(level), verifyClock.ElapsedMilliseconds);
        }

        // D6.3 §11：验证的实际业务结果 = **既有验证报告的结构化结论**（OverallPass / 对象数），
        // 绝不用"VerifyAsync 返回了"冒充成功（审计 P1-4）。
        LastVerifyOutcome = ActionOutcomePolicy.ForVerify(new VerifyBusinessResult(
            Executed: report is { Objects.Count: > 0 },
            OverallPass: report?.OverallPass == true,
            BlockedReason: null,
            Canceled: canceled,
            Faulted: faulted));
        // ★ P1-12 ★ 报告是二级窗口「对象结论」清单的唯一数据源；取消/故障时保持 null（界面据此说"没有报告"）。
        LastVerifyReport = report;
    }

    /// <summary>
    /// ★ P1-12 ★ 最近一次验证的报告（唯一真值来源，供验证面板逐对象展示四态结论）。
    /// 每次验证开跑时先置 null、仅在真的拿到报告后赋值：取消或故障时**不会**留下上一次的旧结论。
    /// </summary>
    public VerifyReport? LastVerifyReport { get; private set; }

    /// <summary>
    /// ★ P0-05 ★ 用一次真实验证的结论同步底栏源灯（唯一判据 = 实测可达性，不做任何推测）：
    ///   · 任一计划对象 <see cref="ObjectVerifyResult.SourceUnavailable"/> ⇒ 源根读不到 ⇒ 灯打回未连接；
    ///   · 计划非空且每个对象都实测可达（无一个 SourceUnavailable）⇒ 灯如实恢复成已连接。
    /// 两条都只在**有实测证据**时出手：验证被取消 / 没有报告 / 计划为空一律不动灯（不猜、不美化）。
    /// </summary>
    private void SyncSourceLampFromVerify(VerifyReport report)
    {
        if (_connection is null || report.Objects.Count == 0) return;
        if (report.Objects.Any(o => o.SourceUnavailable))
        {
            _connection.NotifySourceLost();
            return;
        }
        // ★ P0-05 ★ 恢复要带**实测证据的目标**：把逐个对象的源根实测结论送进灯的证据表
            //（多共享场景下"哪一条恢复了"要能定位），再把状态收敛回"已连接"。
            // 源根路径来自**计划**（验证报告按 ObjectId 对应，报告本身不带路径）。
            var roots = Ctx?.Plan?.Objects.ToDictionary(o => o.ObjectId, o => o.SourcePath, StringComparer.Ordinal);
            var verifiedRoots = new List<string>();
            foreach (var o in report.Objects)
                if (roots is not null && roots.TryGetValue(o.ObjectId, out var sp) && !string.IsNullOrWhiteSpace(sp))
                    verifiedRoots.Add(sp);
            foreach (var sp in verifiedRoots.Distinct(StringComparer.OrdinalIgnoreCase))
                _connection.NotifyShareReachability(sp, true, "验证实测源根可达");
            _connection.NotifySourceRestored(verifiedRoots.FirstOrDefault());
    }

    /// <summary>
    /// 尝试修复：定向重拷（不一致 / 失败 / 中断的对象）→ 自动复验。
    /// 修复范围不盲目全量重传；robocopy 增量特性保证只补差异（user 要求：可从源强制覆盖）。
    /// </summary>
    public async Task RepairAsync(bool forceOverwrite = true, string? password = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var ctx = Ctx;
        if (ctx is null)
        {
            SetUserHint("尚无任务：不能修复。");
            Log("WARN", "尝试修复被拒绝：尚无任务。");
            LastRepairOutcome = ActionOutcomePolicy.ForRepair(new RepairBusinessResult(0, 0, 0, "repair-no-task"));
            return;
        }
        if (IsRunning)
        {
            SetOperational("已有一次运行在进行中。");
            Log("WARN", "尝试修复被拒绝：已有一次运行在进行中。");
            LastRepairOutcome = ActionOutcomePolicy.ForRepair(new RepairBusinessResult(0, 0, 0, "repair-already-running"));
            return;
        }

        // ★ R2-A-2（2026-10-24，v0.5.4 数据安全收口）★ 源共享**确证**失效时不得启动新修复。
        //   现场教训是"一个环境错误被当成差异，然后对目标侧同名文件全部强制覆盖"。
        //   只拦"确证不可达"：证据过期/尚未确认不拦 —— 那只是还没测出来，不该把合理的修复请求挡回去。
        if (_connection is { SourceKnownUnreachable: true })
        {
            SetUserHint("源共享当前不可访问：请先恢复连接再修复。本次未做任何改动。");
            Log("WARN", "尝试修复被拒绝：源共享确证不可访问（不启动重拷，避免对目标做无依据覆盖）。");
            LastRepairOutcome = ActionOutcomePolicy.ForRepair(new RepairBusinessResult(0, 0, 0, "repair-source-unreachable"));
            return;
        }

        var selection = CollectRepairSelection();
        var targets = selection.Targets;
        var beforeMismatch = selection.BeforeMismatch;
        var plannedObjects = ctx.Plan?.Objects.Count ?? 0;

        // ★ D6.1 §16 观察点 ★ 修复链路的证据（请求 → 目标来源 → 无目标/完成）。
        //   全部只陈述事实：不改 targets 计算、不改 forceOverwrite、不改任何后续分支。
        PublishRepair(RepairEvents.RepairRequested,
            new RprRepairRequestedPayload(forceOverwrite, targets.Count), DiagnosticLevel.Information,
            DiagnosticOutcome.Accepted);

        if (targets.Count == 0)
        {
            PublishRepair(RepairEvents.RepairNoTargets,
                new RprTargetsPayload(0, beforeMismatch, 0, plannedObjects), DiagnosticLevel.Information,
                DiagnosticOutcome.Skipped);
            if (selection.RequireReverify.Count > 0)
            {
                // UNKNOWN / 验到一半被打断：先重新核验，不许直接覆盖 —— 覆盖的依据必须是"确认差异"。
                var reverifyMsg = $"有 {selection.RequireReverify.Count} 个对象上次核验结论不明（无法确认是否一致）："
                                 + "请先重新核验，再决定是否修复。本次未做任何改动。";
                SetOperational(reverifyMsg);
                Log("WARN", "尝试修复被拒绝：" + reverifyMsg);
                LastRepairOutcome = ActionOutcomePolicy.ForRepair(new RepairBusinessResult(0, 0, 0, "repair-requires-reverify"));
                return;
            }
            if (selection.SkippedNoData.Count > 0)
            {
                var noDataMsg = $"有 {selection.SkippedNoData.Count} 个对象没有可核对的数据（源侧无待复制文件），"
                               + "既没发现差异也没有失败记录：不启动重拷。";
                SetOperational(noDataMsg);
                Log("INFO", "尝试修复：跳过 " + selection.SkippedNoData.Count + " 个无可核对数据的对象，未启动重拷。");
                LastRepairOutcome = ActionOutcomePolicy.ForRepair(new RepairBusinessResult(0, 0, 0, null));
                return;
            }
            SetOperational("没有需要修复的对象：上次校验（若有）全部一致，也没有失败对象。");
            Log("INFO", "尝试修复：没有需要修复的对象（上次校验全部一致且无失败对象）——未启动重拷。");
            LastRepairOutcome = ActionOutcomePolicy.ForRepair(new RepairBusinessResult(0, 0, 0, null));
            return;
        }
        PublishRepair(RepairEvents.RepairTargetsCollected,
            new RprTargetsPayload(targets.Count, beforeMismatch, Math.Max(0, targets.Count - beforeMismatch), plannedObjects),
            DiagnosticLevel.Information, DiagnosticOutcome.Succeeded);
        SetOperational((beforeMismatch > 0 ? $"修复前 {beforeMismatch} 个对象不一致" : $"修复前有 {targets.Count} 个对象需要重拷")
            + (forceOverwrite ? "，将从共享强制覆盖同名文件" : "，按增量补差异"));
        Log("INFO", $"开始修复：{targets.Count} 个对象需要重拷"
            + (beforeMismatch > 0 ? $"（修复前 {beforeMismatch} 个对象不一致）" : string.Empty)
            + (forceOverwrite ? "，强制覆盖同名文件" : "，按增量补差异"));
        await RunTransferCoreAsync(ctx, password, targets, forceOverwrite, progress, ct);

        // ★ D6.1 §16 ★ 修复结束：只**读既有回执**做统计，不新增成功/失败判定。
        // D6.3 §11：这一次的终点结果 = 同一份回执事实（有失败就不是 Succeeded）。
        var okCount = 0;
        var failCount = 0;
        ActionOutcomeDecision? repairDecision = null;
        try
        {
            var receipts = ctx.LoadReceipts(_log)
                .GroupBy(r => r.ObjectId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(r => r.CompletedUtc).Last())
                .Where(r => targets.Contains(r.ObjectId, StringComparer.OrdinalIgnoreCase))
                .ToList();
            okCount = receipts.Count(r => r.Status == ObjectStatus.Completed);
            failCount = Math.Max(0, receipts.Count - okCount);
            repairDecision = ActionOutcomePolicy.ForRepair(new RepairBusinessResult(targets.Count, okCount, failCount, null));
            PublishRepair(RepairEvents.RepairCompleted,
                new RprCompletedPayload(targets.Count, okCount, failCount, forceOverwrite),
                repairDecision.Value.Outcome == DiagnosticOutcome.Succeeded ? DiagnosticLevel.Information : DiagnosticLevel.Warning,
                repairDecision.Value.Outcome);
        }
        catch (Exception)
        {
            // 观察失败绝不影响修复结果。
        }
        LastRepairOutcome = repairDecision
            ?? ActionOutcomePolicy.ForRepair(new RepairBusinessResult(targets.Count, okCount, failCount, null));
    }

    /// <summary>D6.1 §16：修复链路观察（纯观察，异常吞掉，自动继承动作链）。</summary>
    private static void PublishRepair(
        PCMig.Diagnostics.Abstractions.EventDescriptor descriptor,
        PCMig.Diagnostics.Abstractions.IDiagnosticPayload payload,
        PCMig.Diagnostics.Abstractions.DiagnosticLevel level,
        PCMig.Diagnostics.Abstractions.DiagnosticOutcome outcome)
        => PCMig.Core.Diagnostics.CoreDiagnostics.PublishUiAction(
            descriptor, payload, outcome, level, component: "MigrationSessionViewModel");

    /// <summary>
    /// 需要修复的对象：优先用上次验证报告里"不一致"的对象，再补 Receipt 里失败/有错误/中断的对象。
    /// </summary>
    /// <summary>
    /// ★ R2-A-1（2026-10-24，v0.5.4 数据安全收口）★ 修复目标的**分类结果**。
    ///
    /// 旧实现用 <c>Status != "OK"</c> 当选择器，把四种完全不同的状态混成一句"要修"：
    ///   · EMPTY（没有可核对的数据）→ 被当成不一致去重拷，等于凭"测不出来"动目标文件；
    ///   · UNKNOWN（源/目标不可达、枚举不全、两侧 0/0）→ 被当成不一致直接强制覆盖；
    ///   · MISMATCH（两侧都数得清且确实不一致）→ 这才是唯一有证据的修复依据；
    ///   · OK → 本就不该进来。
    /// 现场真实案例 OK 28 / EMPTY 10 / MISMATCH 1 / UNKNOWN 1：旧逻辑会把 12 个对象都拿去重拷，
    /// 其中 10 个是"没东西可核"，1 个是"根本没核对成"。
    ///
    /// 现在的规则（只认证据，不放宽也不过度扩张）：
    ///   · OK             → 不修；
    ///   · EMPTY          → 不修（没有待复制文件，不存在"差异"）；
    ///   · MISMATCH       → 修（这是唯一"确认差异"的来源）；
    ///   · UNKNOWN / 其它 → 不修，要求**先重新核验**（结论不明时不许覆盖目标）；
    ///   · Receipt 里 Failed / CompletedWithErrors / Interrupted → 修（这些是"上次没传成"，与是否一致无关）；
    ///     但若同一对象在验证里结论不明，仍按 UNKNOWN 处理（更安全的一侧优先）。
    /// 目标侧的 ExtraOnTarget（目标多出来的文件）任何情况下都不在这里删除。
    /// </summary>
    public sealed record RepairSelection(
        List<string> Targets,
        int BeforeMismatch,
        List<string> RequireReverify,
        List<string> SkippedNoData,
        int SkippedOk);

    /// <summary>★ R2-A-1 ★ 修复目标的分类选择。纯读盘，不做任何写操作。</summary>
    public RepairSelection CollectRepairSelection()
    {
        var targets = new List<string>();
        var reverify = new List<string>();
        var noData = new List<string>();
        var okCount = 0;
        var mismatch = 0;

        var ctx = Ctx;
        if (ctx is null) return new RepairSelection(targets, 0, reverify, noData, 0);

        var verify = ctx.LoadVerify();
        if (verify is not null)
        {
            foreach (var o in verify.Objects)
            {
                switch (o.Status)
                {
                    case "OK":
                        okCount++;
                        break;
                    case "MISMATCH":
                        mismatch++;
                        targets.Add(o.ObjectId);
                        break;
                    case "EMPTY":
                        noData.Add(o.ObjectId);
                        break;
                    default:
                        // UNKNOWN / CANCELED / 其它未知结论：先重新核验，不许拿它当覆盖依据。
                        reverify.Add(o.ObjectId);
                        break;
                }
            }
        }

        var receipts = ctx.LoadReceipts(_log);
        var latest = receipts
            .GroupBy(r => r.ObjectId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(r => r.CompletedUtc).Last());
        foreach (var r in latest)
            if (r.Status is ObjectStatus.Failed or ObjectStatus.CompletedWithErrors or ObjectStatus.Interrupted)
                if (!reverify.Contains(r.ObjectId, StringComparer.OrdinalIgnoreCase))
                    targets.Add(r.ObjectId);

        return new RepairSelection(
            targets.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            mismatch,
            reverify.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            noData.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            okCount);
    }

    /// <summary>兼容入口：只返回"有证据支持重拷"的对象（语义已按 R2-A-1 收窄）。</summary>
    public List<string> CollectRepairTargets(out int beforeMismatch)
    {
        var selection = CollectRepairSelection();
        beforeMismatch = selection.BeforeMismatch;
        return selection.Targets;
    }

    /// <summary>生成并打开报告（Core 的 ReportGenerator，唯一权威报告源）。</summary>
    public async Task OpenReportAsync(CancellationToken ct = default)
    {
        var ctx = Ctx;
        if (ctx is null) { SetUserHint("尚无任务：没有可生成的报告。"); Log("WARN", "打开报告被拒绝：尚无任务（没有可生成的报告）。"); return; }
        try
        {
            var jobLog = LogBootstrap.CreateJobLogger(ctx.JobDir, ctx.JobId);
            var path = await Task.Run(() => new ReportGenerator(ctx, jobLog).Generate(), ct);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            SetOperational($"报告已生成并打开：{path}");
            Log("INFO", $"报告已生成并打开：{path}");
        }
        catch (Exception ex)
        {
            SetErrorSummary($"报告生成失败：{ex.Message}");
            Log("ERROR", $"报告生成失败：{ex.Message}");
            _log.Error(ex, "WinUI 报告生成失败");
        }
    }

    /// <summary>
    /// ★ m03985 执行令 §UI-C/§UI-D + §数据采集契约 4 ★ 生成并打开**验证报告**（与迁移报告严格分开）：
    ///   · 迁移报告讲「传了什么」（<see cref="OpenReportAsync"/>）；本方法讲「核对到了什么、没核对成什么」；
    ///   · 没有验证报告、或本轮还没出结论 ⇒ 如实拒绝并解释（**不生成空报告、不打开旧报告冒充本轮结果**）；
    ///   · 报告基于最近一次**已完成**验证的落盘数据（JobId + VerifyRunId + Level + 验证时刻），
    ///     正在执行的验证不会被当成已完成；
    ///   · 生成在后台线程（不占 UI 线程）、报告器自己原子落盘；打开失败时返回本机路径供复制，绝不假称成功。
    /// </summary>
    public async Task<VerifyReportOpenResult> OpenVerifyReportAsync(CancellationToken ct = default)
    {
        var ctx = Ctx;
        if (ctx is null)
        {
            const string noJob = "尚无任务：没有可生成的验证报告。";
            SetUserHint(noJob);
            Log("WARN", noJob);
            return new VerifyReportOpenResult(false, null, noJob);
        }

        // ★ 只认「已经出过结论」的验证：正在跑的验证（或跑完但被判为取消/无报告）都不能当成本轮结果。
        if (LastVerifyReport is null)
        {
            const string noResult = "还没有验证报告：请先在本页点击「验证完整性」，完成后即可打开。";
            SetUserHint(noResult);
            Log("WARN", "打开验证报告被拒绝：本次会话还没有已完成验证的结论。");
            return new VerifyReportOpenResult(false, null, noResult);
        }

        try
        {
            var jobLog = LogBootstrap.CreateJobLogger(ctx.JobDir, ctx.JobId);
            var path = await Task.Run(() => new VerifyReportGenerator(ctx, jobLog).Generate(), ct);
            if (path is null)
            {
                const string empty = "验证报告未能生成：本机没有可用的验证数据（结论不受影响，详见本页日志）。";
                SetErrorSummary(empty);
                Log("ERROR", empty);
                return new VerifyReportOpenResult(false, null, empty);
            }

            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                var openFailed = $"验证报告已生成，但用系统默认浏览器打开失败（{ex.Message}）。" +
                                 $"可复制本机路径手动打开：{path}";
                SetErrorSummary(openFailed);
                Log("ERROR", openFailed);
                return new VerifyReportOpenResult(false, path, openFailed);
            }

            SetOperational($"验证报告已生成并打开：{path}");
            Log("INFO", $"验证报告已生成并打开：{path}");
            return new VerifyReportOpenResult(true, path, $"验证报告已打开（本机文件，可离线查看）：{path}");
        }
        catch (OperationCanceledException)
        {
            const string canceled = "已取消打开验证报告（没有生成文件）。";
            Log("WARN", canceled);
            return new VerifyReportOpenResult(false, null, canceled);
        }
        catch (Exception ex)
        {
            var failed = $"验证报告生成失败：{ex.Message}（验证结论不受影响）";
            SetErrorSummary(failed);
            Log("ERROR", failed);
            _log.Error(ex, "WinUI 验证报告生成失败");
            return new VerifyReportOpenResult(false, null, failed);
        }
    }

    /// <summary>
    /// 打开**目标文件夹**（资源管理器）。目标根取自当前任务的 job.json（权威），无任务时退回界面上填的目标根；
    /// 两者都没有 / 目录不存在 ⇒ 如实拒绝（不打开一个不存在的路径，也不假装成功）。
    /// </summary>
    public void OpenTargetFolder()
    {
        var target = Ctx?.Definition.TargetRoot;
        if (string.IsNullOrWhiteSpace(target)) target = TargetRoot;
        if (string.IsNullOrWhiteSpace(target))
        {
            SetUserHint("尚无目标路径：没有可打开的目标文件夹。");
            Log("WARN", "打开目标文件夹被拒绝：尚未指定目标路径。");
            return;
        }
        try
        {
            if (!Directory.Exists(target))
            {
                SetErrorSummary($"目标文件夹不存在（可能已被移动或删除）：{target}");
                Log("WARN", $"打开目标文件夹被拒绝：目录不存在 {target}");
                return;
            }
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            SetOperational($"已打开目标文件夹：{target}");
            Log("INFO", $"已打开目标文件夹：{target}");
        }
        catch (Exception ex)
        {
            SetErrorSummary($"打开目标文件夹失败：{ex.Message}");
            Log("ERROR", $"打开目标文件夹失败：{ex.Message}");
            _log.Error(ex, "WinUI 打开目标文件夹失败: {Target}", target);
        }
    }

    /// <summary>
    /// 打开**应用级日志目录**（资源管理器）：路径唯一来源是 Core 的 <see cref="LogBootstrap.AppLogDir"/>
    /// （实测 = %ProgramData%\PCMig\Logs）。旧 WPF 无对应命令，属本包新增的纯导航动作，不碰任何业务状态；
    /// 目录不存在时先创建（Core 的 logger 本身也是按需创建该目录），保证按钮不会"点了没反应"。
    /// </summary>
    public void OpenLogDirectory()
    {
        try
        {
            Directory.CreateDirectory(LogBootstrap.AppLogDir);
            Process.Start(new ProcessStartInfo(LogBootstrap.AppLogDir) { UseShellExecute = true });
            SetOperational($"已打开日志目录：{LogBootstrap.AppLogDir}");
            Log("INFO", $"已打开日志目录：{LogBootstrap.AppLogDir}");
        }
        catch (Exception ex)
        {
            SetErrorSummary($"打开日志目录失败：{ex.Message}");
            Log("ERROR", $"打开日志目录失败：{ex.Message}");
            _log.Error(ex, "WinUI 打开日志目录失败: {Dir}", LogBootstrap.AppLogDir);
        }
    }

    // ────────────────────────── 方法：任务列表 / 断点提醒 ──────────────────────────

    /// <summary>刷新"已有任务"列表（JobManager.ListAll；阶段文案用 Core 的 PhaseView，口径与 CLI 一致）。</summary>
    public async Task RefreshExistingJobsAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        try
        {
            var list = await Task.Run(() => new JobManager(_log).ListAll(), ct);
            Post(() =>
            {
                ExistingJobs.Clear();
                foreach (var j in list) ExistingJobs.Add(j);
            });
        }
        catch (OperationCanceledException) { /* 取消不算失败 */ }
        catch (Exception ex)
        {
            _log.Warning(ex, "刷新已有任务列表失败");
        }
    }

    /// <summary>
    /// 断点提醒：检测该源（可选目标）下未完成的任务。
    /// **本方法不弹任何对话框**（ContentDialog 需要 XamlRoot，属于页面层职责）：
    ///   · 找到候选 → 记录到 <see cref="PendingResumeCandidate"/>，当前无任务时自动载入（<see cref="AdoptExistingJobAsync"/>），
    ///     但**不自动开跑**；界面确认后由页面调用 <see cref="ResumeAsync"/>。
    ///   · 返回 false = 没有可续传任务（或检测失败，原因写在 StatusMessage）。
    ///
    /// ★ A.5（P2-6）主控裁决（notes §15.3）★ **本方法在 WinUI 壳里当前没有任何调用点**
    /// （全仓 grep 只命中本定义；src\PCMig.Gui 里那个同名方法是旧 WPF 壳自己的私有方法，与本类无关）。
    /// 因此它**不参与输入风暴**、也**不额外加世代门** —— 无调用方 ⇒ 不存在与"Host 逐字符探测"的并发写。
    /// 保留它是因为它是产品 API（用户未要求删）；若将来接线调用它，**必须**补上同一套世代门
    /// （它同样是 <see cref="PendingResumeCandidate"/> 的写者）。
    /// </summary>
    /// <returns>true = 发现未完成任务且已载入为当前任务（等界面确认）。</returns>
    public async Task<bool> PromptResumeIfAnyAsync(string? targetRoot, CancellationToken ct = default)
    {
        var host = Host;
        if (string.IsNullOrWhiteSpace(host)) { SetUserHint("尚未连接旧电脑：无法检测未完成任务。"); Log("WARN", "未完成任务检测被跳过：尚未连接旧电脑。"); return false; }
        try
        {
            var target = string.IsNullOrWhiteSpace(targetRoot) ? TargetRoot : targetRoot;
            var found = await Task.Run(() => new JobManager(_log).FindUnfinished(
                host, string.IsNullOrWhiteSpace(target) ? null : target), ct);
            var unfinished = found.FirstOrDefault(x => File.Exists(Path.Combine(x.JobDir, "plan.json")));
            if (unfinished is null)
            {
                Log("INFO", "未完成任务检测：未匹配到可续传任务。");
                return false;
            }

            PendingResumeCandidate = unfinished;
            var desc = $"任务 {unfinished.JobId}｜源 {unfinished.SourceHost}｜目标 {unfinished.TargetRoot}｜" +
                       (unfinished.StateUnreliable ? "状态文件损坏，进度未知（以回执为准）" : $"已传 {unfinished.Percent:0.0}%") +
                       $"｜{unfinished.PhaseText}";
            if (found.Count > 1)
                Log("WARN", $"检测到 {found.Count} 个未完成任务候选（Core 按最近更新时间降序返回；本层沿用旧实现规则：取第一个带 plan.json 的）：" +
                            string.Join("、", found.Select(x => x.JobId)));
            if (Ctx is null)
            {
                await AdoptExistingJobAsync(unfinished.JobDir, ct);
                SetOperational($"检测到未完成的迁移任务：{desc}。已载入，点「恢复任务」可从中断处继续（已完成的对象不会重传）。");
            }
            else
            {
                SetUserHint($"检测到未完成的迁移任务：{desc}。请先结束当前任务，再决定是否续传该任务。");
            }
            Log("WARN", $"检测到未完成的迁移任务（未自动恢复）：{desc}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "断点提醒检测失败");
            SetErrorSummary($"未完成任务检测失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// ★ 应用启动时的「未完成任务」主动探测（用户要求：重开工具后必须**主动调用真实的**
    /// <c>JobManager.FindUnfinished()</c>，而不是靠任何假状态）★
    ///
    /// 安全规则（一条都不能省）：
    ///   · **绝不自动恢复**：本方法不调用 <see cref="ResumeAsync"/>，不写任何存档，也不开跑；
    ///     恢复必须由用户在本页点「恢复任务」明确确认；
    ///   · 本方法**也不自动载入**任务（不调 <see cref="AdoptExistingJobAsync"/>）：只记录候选
    ///     <see cref="PendingResumeCandidate"/> 并把真实结论写进 <see cref="UnfinishedProbeText"/>；
    ///   · Host 为空（Step 1 尚未连接旧电脑）时，Core 的 FindUnfinished 按源电脑**精确匹配**，
    ///     必然返回 0 条 —— 这是"**无法匹配**"，不是"没有未完成任务"：两种情况在文案里分开说，
    ///     绝不谎报结论；连接成功后 Shell 会再探测一次（见 MainWindow 的会话属性订阅）。
    /// </summary>
    /// <returns>Core 真实返回的未完成任务列表（可能为空；已按 Core 的最近更新时间降序）。</returns>
    public async Task<IReadOnlyList<JobSummary>> FindUnfinishedOnStartupAsync(CancellationToken ct = default)
    {
        // ★ A.5（P2-6）★ 本方法**公开签名逐字不变**（启动一次性探测 + 多处单测直接调用它），
        //   但实现收成薄壳：登记为"最新一次探测请求"后，走同一条**带世代门**的探测核心
        //   （见 ProbeUnfinishedCoreAsync / IsProbeResultCurrent）。
        //   为什么它也必须过门：它同样是写 PendingResumeCandidate 的路径之一，
        //   而"启动 Loaded 探测"与"输入风暴去抖到期后的探测"可能并发在途。
        return await BeginUnfinishedProbe(ct);
    }

    // ══════════════════ A.5（P2-6）未完成任务探测：连接门 + 去抖 + 世代门 ══════════════════
    //
    //  为什么这一整块放在 Session 而不是 Shell（设计 §3.2）：
    //    Shell 只能挡住"发起"，挡不住"旧结果回来"——写 UnfinishedProbeText / PendingResumeCandidate
    //    的地方在 Session 内部，所以门控必须与写状态同处一地。
    //
    //  三件套（设计 §3.1，缺一不可）：
    //    ① 连接门：未连接 ⇒ **不做任何 IO**（改前每敲一个 Host 字符就可能跑一次
    //       全量任务目录扫描 + 每任务一次独占锁探测，见设计 §1.2 B5）；
    //    ② 去抖 400ms：把"发起"合并到最后一次输入之后；IsSourceConnected **false→true 走立即分支**
    //       （用户按下"连接"之后的真实时刻，不该被去抖延迟，R9）；
    //    ③ 世代 token + Host 字符串快照：返回后**先判门再写状态**，两者都过才写（R11）。

    /// <summary>未连接时的**如实结论**文案（去抖到期分支与探测核心共用同一份，绝不各写一套）。</summary>
    internal const string UnfinishedProbeNotConnectedText =
        "尚未连接旧电脑：未完成任务检测需按源电脑精确匹配，本次无法匹配" +
        "（这不等于「没有未完成任务」；连上旧电脑后会自动再检测一次）。";

    /// <summary>去抖窗口（设计 §3.1 建议 300–600ms；固定 400ms）。</summary>
    public static readonly TimeSpan UnfinishedProbeDebounceInterval = TimeSpan.FromMilliseconds(400);

    private IUnfinishedProbeDebouncePump? _probeDebouncePump;
    private Func<Action, IUnfinishedProbeDebouncePump>? _probeDebouncePumpFactory;
    private Func<string, string?, IReadOnlyList<JobSummary>>? _unfinishedProbeForTest;

    /// <summary>世代计数（只经 Interlocked/Volatile 访问：离屏路径下续体可能不在同一线程）。</summary>
    private int _probeGeneration;

    /// <summary>当前在途请求的取消源（新请求取消旧请求的 <c>Task.Run</c>；关窗时取消全部）。</summary>
    private CancellationTokenSource? _probeCts;

    /// <summary>上一次被请求探测时看到的连接态（用于识别 <c>false→true</c> 跳变 ⇒ 立即分支，R9）。</summary>
    private bool _lastConnectedForProbe;

    /// <summary>最近一次探测的 Task（★ 仅测试缝用 ★：让单测能确定性 await "这次探测已结束"，不靠 sleep）。</summary>
    private Task? _lastProbeTask;

    /// <summary>
    /// ★ 仅测试缝 ★ —— 最近一次探测的内部 Task（**只读诊断**，生产代码从不读它）。
    ///
    /// 用途：单测需要"等到这一次探测确实已经把状态写完（或确实已经走了丢弃分支）"才能断言，
    /// 而这只能由**确定的完成信号**给出——有了它就不需要 30 秒 <c>Thread.Sleep</c> 轮询、
    /// 也不需要自定义 SynchronizationContext 去扣续体（那套已证明会抖动）。
    /// 它不改变任何生产行为：只在一个 Task 被创建时记一次引用。
    /// </summary>
    internal Task? LastProbeTaskForTest => _lastProbeTask;

    /// <summary>
    /// ★ 仅测试缝 ★ —— 「未完成任务」探测的假实现注入点。
    ///
    /// 边界（与 <see cref="TransferRunner"/> / <see cref="UiFlushPumpFactoryForTest"/> 同一纪律）：
    ///   · **默认（null）= 生产路径**，直接使用 Core 的 <c>JobManager.FindUnfinished</c>
    ///     （见 <see cref="FindUnfinishedCore"/> 的唯一二选一分支）；
    ///   · 非 null 只允许**单元测试**注入，用来在无磁盘/无网络环境下确定性地控制"探测何时返回、
    ///     返回什么候选"（竞态回归需要"第一次探测确定性地挂起"）；
    ///   · **装配层（MainWindow/App/页面）绝不可设置本属性**，否则测试缝就变成第二条生产路径。
    /// </summary>
    public Func<string, string?, IReadOnlyList<JobSummary>>? UnfinishedProbeForTest
    {
        get => _unfinishedProbeForTest;
        set
        {
            _unfinishedProbeForTest = value;
            _log.Warning("UnfinishedProbeForTest 测试缝被赋值（仅测试允许；生产路径必须为 null）：{Kind}",
                value is null ? "已清空（回到 Core 的 JobManager）" : value.GetType().Name);
        }
    }

    /// <summary>
    /// ★ 仅测试缝 ★ —— 去抖计时器的假实现注入点（生产恒为 null ⇒ 走装配层注入的真实
    /// <c>DispatcherQueueProbeDebouncePump</c>）。
    ///
    /// 边界：**装配层（MainWindow/App/页面）绝不可设置本属性**。它让单测不必等待 400ms 真实时间，
    /// 从而"输入风暴只发起一次探测""未连接不做 IO"这类契约可以**完全确定性地**断言（无 sleep）。
    /// </summary>
    public Func<Action, IUnfinishedProbeDebouncePump>? UnfinishedProbeDebouncePumpFactoryForTest
    {
        get => _probeDebouncePumpFactory;
        set
        {
            _probeDebouncePumpFactory = value;
            _log.Warning("UnfinishedProbeDebouncePumpFactoryForTest 测试缝被赋值（仅测试允许；生产路径必须为 null）");
        }
    }

    /// <summary>
    /// ★ 生产装配点（唯一）★ —— 由 UI 外壳（MainWindow）在启动时注入真实的去抖计时器工厂。
    ///
    /// 与 <see cref="ProductionFlushPumpFactory"/> 同一形态与同一理由：它持 <c>DispatcherQueueTimer</c>，
    /// 而本文件被**链入** tests\PCMig.Core.Tests 编译（该测试项目没有 timer 替身）
    /// ⇒ 被链入的文件里不得出现 WinUI 定时器类型名。**测试不得设置本属性**。
    /// </summary>
    internal static Func<Action, IUnfinishedProbeDebouncePump>? ProductionProbeDebouncePumpFactory { get; set; }

    /// <summary>
    /// 请求一次「未完成任务」探测（**同步、无返回值、绝不外抛** —— Shell 侧因此不再需要 <c>async void</c>）。
    ///
    /// 行为（设计 §3.4）：
    ///   · 连接态 <c>false→true</c> ⇒ **立即**发起探测（不去抖，R9）；
    ///   · 其余情况 ⇒ 起/重置 400ms 去抖计时器；到期后再判连接门：
    ///       未连接 ⇒ **不做任何 IO**，只把结论写成"尚未连接 ⇒ 无法匹配（≠ 没有未完成任务）"；
    ///       已连接 ⇒ 以**到期那一刻**的 Host 为 key 发起探测；
    ///   · 已走关闭路径（<see cref="_uiRefreshStopped"/>）⇒ 什么都不做（绝不让计时器在退出瞬间复活，R14）。
    /// </summary>
    public void RequestUnfinishedProbe()
    {
        try
        {
            if (_uiRefreshStopped) return;

            var connected = IsSourceConnected;
            var justConnected = connected && !_lastConnectedForProbe;
            _lastConnectedForProbe = connected;

            if (justConnected)
            {
                // 立即分支：连接刚建立。先取消可能还在排队的去抖窗口，避免紧接着再来一次重复探测。
                try { _probeDebouncePump?.Disarm(); } catch { /* 取消失败不得影响本次探测 */ }
                RunUnfinishedProbeNow();
                return;
            }

            EnsureProbeDebouncePump().Arm();
        }
        catch (Exception ex)
        {
            // R13：这是从 PropertyChanged 分发里被调用的**同步**入口，任何异常外溢都会打断属性分发。
            _log.Warning(ex, "未完成任务探测请求失败（已吞掉，不影响 Shell）");
        }
    }

    /// <summary>去抖到期：先判连接门，再决定"只写如实文案"还是"发起真实探测"。</summary>
    private void OnUnfinishedProbeDebounceElapsed()
    {
        try
        {
            if (_uiRefreshStopped) return;

            if (!IsSourceConnected)
            {
                // 连接门：未连接 ⇒ 不做任何 IO（这正是"每个字符一次全量任务目录扫描"的治理点）。
                // 但仍要如实回报"无法匹配（≠ 没有未完成任务）"，绝不静默 —— 只在文案真变时才写，
                // 避免无意义的 PropertyChanged 风暴。计时器回调在 UI 线程（DispatcherQueueTimer）。
                if (!string.Equals(_unfinishedProbeText, UnfinishedProbeNotConnectedText, StringComparison.Ordinal))
                {
                    UnfinishedProbeText = UnfinishedProbeNotConnectedText;
                    Log("WARN", "未完成任务检测：尚未连接旧电脑，Core 的 FindUnfinished 按源电脑精确匹配——本次未执行有效匹配（不是「无未完成任务」的结论）。");
                }
                return;
            }

            RunUnfinishedProbeNow();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "未完成任务探测去抖到期处理失败（已吞掉，不影响 Shell）");
        }
    }

    /// <summary>发起一次真实探测（fire-and-forget；异常一律在 <see cref="ProbeUnfinishedCoreAsync"/> 内消化）。</summary>
    private void RunUnfinishedProbeNow() => _ = BeginUnfinishedProbe(default);

    /// <summary>
    /// 登记一次新的探测请求：递增世代（作废旧结果）+ 取消上一个在途请求 + 记录 host 快照，然后发起。
    /// </summary>
    private Task<IReadOnlyList<JobSummary>> BeginUnfinishedProbe(CancellationToken externalCt)
    {
        var key = Host;
        var token = Interlocked.Increment(ref _probeGeneration);

        // 外部给了可取消的 token 就尊重它；否则用本次请求自己的取消源（这样新请求能取消旧请求）。
        CancellationToken ct;
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _probeCts, cts);
        if (previous is not null)
        {
            // JobManager 内部不可中断，但 Task.Run(..., ct) 至少不会为已过期的请求启动新工作。
            try { previous.Cancel(); } catch { /* 取消已结束的请求不得影响新请求 */ }
            // 刻意**不** Dispose：在途的 Task.Run 仍可能持有它的 CancellationTokenRegistration，
            // Dispose 会让注册抛 ObjectDisposedException（比泄漏一个无 timer 的 CTS 更糟）。
        }
        ct = externalCt.CanBeCanceled ? externalCt : cts.Token;

        var task = ProbeUnfinishedCoreAsync(token, key, ct);
        _lastProbeTask = task;      // ★ 仅测试缝用（生产只写不读）
        return task;
    }

    /// <summary>
    /// 探测核心：真实读盘 → **先判门**（世代 token + Host 快照 + 未走关闭路径）→ 才写状态。
    /// 异常路径同样先判门（否则旧请求的报错会覆盖新状态，设计 §3.3 第 7 点）。
    /// </summary>
    private async Task<IReadOnlyList<JobSummary>> ProbeUnfinishedCoreAsync(int token, string key, CancellationToken ct)
    {
        var target = string.IsNullOrWhiteSpace(TargetRoot) ? null : TargetRoot;
        IReadOnlyList<JobSummary> found;
        try
        {
            found = await Task.Run(() => FindUnfinishedCore(key, target), ct);
        }
        catch (OperationCanceledException) { return Array.Empty<JobSummary>(); }
        catch (Exception ex)
        {
            if (IsProbeResultCurrent(token, key))
            {
                UnfinishedProbeText = $"未完成任务检测失败：{ex.Message}";
                Log("ERROR", $"未完成任务检测失败：{ex.Message}");
            }
            return Array.Empty<JobSummary>();
        }

        // ★ 先判门再写状态（设计 §3.3 第 5 步）★：世代 token **且** Host 快照两者都过才允许写。
        //   这一条是"旧结果不得覆盖新状态"的唯一执行点 —— 少了它，一次过期探测返回后就会把
        //   新探测的文案与候选（Step4「恢复任务」指向的任务）整个改写掉。
        if (!IsProbeResultCurrent(token, key)) return found;

        try { return ApplyProbeResult(key, found); }
        catch (Exception ex)
        {
            // fire-and-forget 路径：状态写入阶段的异常绝不允许变成"未观测的 Task 异常"。
            _log.Warning(ex, "未完成任务探测结果应用失败（已吞掉，不影响 Shell）");
            return found;
        }
    }

    /// <summary>
    /// "旧结果不得覆盖新状态"的唯一判定：世代 token **且** Host 字符串快照**且**本会话未走关闭路径。
    /// 三者都通过才允许写状态（设计 §3.3）。异常路径与成功路径**都必须**先过这里。
    /// </summary>
    private bool IsProbeResultCurrent(int token, string key)
        => !_uiRefreshStopped
           && token == Volatile.Read(ref _probeGeneration)
           && string.Equals(key, Host, StringComparison.Ordinal);

    /// <summary>把探测结果写进状态（**调用方必须已过门**；此处不再重复判门）。</summary>
    private IReadOnlyList<JobSummary> ApplyProbeResult(string key, IReadOnlyList<JobSummary> found)
    {
        // 选择规则**沿用旧实现**（Core 按最近更新时间降序返回 → 取第一个带 plan.json 的）：
        // 本包不发明"自动选最新一个"之类的新规则，也不因为多个候选就擅自恢复任何一个。
        PendingResumeCandidate = found.FirstOrDefault(x => File.Exists(Path.Combine(x.JobDir, "plan.json")));

        if (found.Count > 1)
            Log("WARN", $"未完成任务检测：Core 返回 {found.Count} 个候选（按最近更新时间降序）——" +
                        string.Join("、", found.Select(x => x.JobId)));

        var target = string.IsNullOrWhiteSpace(TargetRoot) ? null : TargetRoot;
        if (string.IsNullOrWhiteSpace(key))
        {
            UnfinishedProbeText = UnfinishedProbeNotConnectedText;
            Log("WARN", "未完成任务检测：尚未连接旧电脑，Core 的 FindUnfinished 按源电脑精确匹配——本次未执行有效匹配（不是「无未完成任务」的结论）。");
            return found;
        }

        var scope = $"源 {key}" + (target is null ? string.Empty : $"｜目标 {target}");
        if (PendingResumeCandidate is null)
        {
            UnfinishedProbeText = $"未完成任务检测（{scope}）：未匹配到可续传任务。";
            Log("INFO", $"未完成任务检测（{scope}）：未匹配到可续传任务。");
            return found;
        }

        var candidate = PendingResumeCandidate;
        var desc = $"任务 {candidate.JobId}｜源 {candidate.SourceHost}｜目标 {candidate.TargetRoot}｜" +
                   (candidate.StateUnreliable ? "状态文件损坏，进度未知（以回执为准）" : $"已传 {candidate.Percent:0.0}%") +
                   $"｜{candidate.PhaseText}";
        UnfinishedProbeText = $"检测到未完成的迁移任务：{desc}。点「恢复任务」可从断点继续（已完成的对象不会重传）" +
                              "——不会自动恢复，需要你确认。";
        Log("WARN", $"检测到未完成的迁移任务（未自动恢复，等待用户确认）：{desc}");
        return found;
    }

    /// <summary>
    /// 探测实现的**唯一二选一**：测试缝注入 ⇒ 用它；生产 ⇒ Core 的 <c>JobManager.FindUnfinished</c>。
    /// （绝不在旁边再加一条并行路径——那正是"测试缝变成第二条生产路径"的静默退化。）
    /// </summary>
    private IReadOnlyList<JobSummary> FindUnfinishedCore(string host, string? target)
    {
        var seam = _unfinishedProbeForTest;
        return seam is not null ? seam(host, target) : new JobManager(_log).FindUnfinished(host, target);
    }

    /// <summary>去抖计时器的唯一生产/测试装配点（两条明确通路，没有第三条）。</summary>
    private IUnfinishedProbeDebouncePump EnsureProbeDebouncePump()
    {
        var existing = _probeDebouncePump;
        if (existing is not null) return existing;

        var testFactory = _probeDebouncePumpFactory;
        if (testFactory is not null) return _probeDebouncePump = testFactory(OnUnfinishedProbeDebounceElapsed);

        var production = ProductionProbeDebouncePumpFactory;
        if (production is not null) return _probeDebouncePump = production(OnUnfinishedProbeDebounceElapsed);

        throw new InvalidOperationException(
            "未完成任务探测的去抖计时器没有装配：生产外壳（MainWindow 构造）必须给 ProductionProbeDebouncePumpFactory 赋值，" +
            "单测请用 UnfinishedProbeDebouncePumpFactoryForTest 注入假泵。");
    }

    /// <summary>停掉去抖计时器（**只由 <see cref="StopUiRefresh"/> 这一个公开出口调用**，禁止再开第二个出口）。</summary>
    private void StopProbeDebouncePump()
    {
        var pump = _probeDebouncePump;
        if (pump is null) return;
        try { pump.Stop(); } catch { /* 关闭期异常不得外溢 */ }
    }

    /// <summary>
    /// 预检链的第一步（页面用）：按“同主机 + 同目标 + 有 plan.json”查未完成任务。
    /// 与旧 WPF PrepareAsync L706-725 的检测条件**同口径**（`FindUnfinished(...).FirstOrDefault(有 plan.json)`）。
    /// 只检测、不弹窗、不写任何存档；页面拿到结果后决定是问用户还是直接带
    /// <see cref="ResumeDecision"/> 去调 <see cref="PrepareAsync"/>。
    /// 当前任务本身就是该任务时返回 null（重新预检=刷新它的计划，不该被当成“另一个任务”）。
    /// </summary>
    public async Task<JobSummary?> FindUnfinishedForPrepareAsync(string host, string targetRoot, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(targetRoot)) return null;
        try
        {
            var found = await Task.Run(() => new JobManager(_log).FindUnfinished(host.Trim(), targetRoot.Trim()), ct);
            var dup = found.FirstOrDefault(x => File.Exists(Path.Combine(x.JobDir, "plan.json")));
            if (dup is not null && Ctx is not null
                && string.Equals(dup.JobId, Ctx.JobId, StringComparison.OrdinalIgnoreCase)) return null;
            return dup;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { _log.Warning(ex, "未完成任务检测失败"); return null; }
    }

    /// <summary>
    /// 载入既有任务为当前任务（回填目标根、对象清单、进度视图、线程原值）。
    /// **绝不改写任务定义**（Resume 必须沿用 job.json 里的线程值与源/目标）。
    /// </summary>
    public async Task<bool> AdoptExistingJobAsync(string jobDirOrId, CancellationToken ct = default)
    {
        try
        {
            var ctx = await Task.Run(() => new JobManager(_log).Open(jobDirOrId), ct);
            Ctx = ctx;
            TargetRoot = ctx.Definition.TargetRoot;

            // ★ A.5（P1-4）：载入另一个任务 = 换任务。先把上一个任务的实时投影（失败清单 + 去重索引、
            //   实时文件流、当前对象/文件）、验证投影（结论/抽样数/报告标志）与日志投影全部复位，
            //   再**由回执（Receipt，唯一权威）重建**行状态与失败清单（见下方的 RefreshRowsFromReceiptsAsync）。
            //   复位不等于清成空白：重建之后清单与本任务的真实存档一致。
            ResetForJobSwitch();

            // ★ 恢复兼容：以 job.json 的 Options.Threads 为绝对权威（16/32/64/128 也不覆写）
            PinThreadsFromJob(ctx.Definition.Options.Threads);

            FillObjectsFromPlan(ctx.Plan);
            var st = ctx.LoadStateOrNew();
            var liveHolder = JobManager.IsLocked(ctx.JobDir);
            var stale = PhaseView.IsStale(st.Phase, liveHolder, ctx.HasReceipts);
            Phase = stale ? PhaseView.Effective(st.Phase, liveHolder, ctx.HasReceipts) : st.Phase;
            var unreliable = ctx.StateViewUnreliable;
            // ★ A.5（P1-3）：**只有真 Completed 才可以强制 100%**。载入一个 CompletedWithErrors 的旧任务时
            //   必须显示它真实传到哪（JobState 的 CompletedBytes/TotalBytes），否则用户会以为已经传完了。
            // ★ R2 接手修复 · 缺陷 H（S-1）★ 载入历史任务也必须走与收尾同一个放行口径。
            //   改前这里只判 `Phase == JobPhase.Completed` 就直接写 100%，且整条 Adopt 路径
            //   **从不重算 TrustworthyCompletion** ⇒ 打开一份"已完成但字节不闭环"的历史任务后，
            //   界面显示 100% 而内部认为不可信、页面也不会自动前进（真实存档里有这种任务）。
            //   现在：先按 Core 判据重算可信完成，只有可信才允许 100.0。
            ApplyTrustworthyCompletion(st);
            //   ★ 缺陷 J ★ 换任务时视觉值可能残留上一个任务的 100%（Adopt 不复位 _presentation），
            //   必须一并压到本任务的真实水平，因此三处都允许视觉回退。
            if (unreliable)
                PublishPercent(0, allowVisualSnap: true);  // 状态损坏：照旧不冒充比例（下方文案已说明"以回执为准"），但绝不写 100%
            else if (TrustworthyCompletion)
                PublishPercent(100.0, allowVisualSnap: true);
            else
                PublishPercent(Math.Min(ActualPercent(st), TransferOrchestrator.SettledPercentCapPublic), allowVisualSnap: true);
            DataLabel = Phase is JobPhase.Completed or JobPhase.CompletedWithErrors ? "实际落盘 / 计划" : "已传 / 计划";
            PlanBytesText = Format.Bytes(st.TotalBytes);
            ActualBytesText = Format.Bytes(st.CompletedBytes);
            ProgressText = unreliable ? "状态损坏，待由回执重建" : $"{Format.Bytes(st.CompletedBytes)} / {Format.Bytes(st.TotalBytes)}";
            ObjectText = unreliable ? "未知（以回执为准）" : $"{st.CompletedObjects}/{st.TotalObjects}";
            BalanceText = $"计划 {Format.Bytes(st.TotalBytes)}　·　实际落盘 {Format.Bytes(st.CompletedBytes)}";
            // ★ A.5（P1-4）：失败对象数也按**本任务存档**重建（改前会留着上一个任务/上一轮运行的内存值）。
            FailedObjects = unreliable ? 0 : st.FailedObjects;
            // ★ FIX BATCH 2 ★ 暂停态按**本任务存档的暂停真值**重建：不只是"Phase 是不是 Paused"，
            //   还要能读出"当时请求过暂停、有没有达成、失败原因是什么"——App 崩溃/关机后重启，
            //   这些字段仍然在 job-state.json 里（FIX BATCH 1 持久化），界面必须把它们说清楚。
            _pauseRequestWritten = false;
            _pauseRetryAfterUtc = null;
            ApplyPauseTruth(st.PauseState, st.PauseRequestedUtc, st.PauseAchievedUtc, st.PauseOutcome, st.PauseFailureReason);
            if (PauseUiState == PauseUiState.Idle && Phase == JobPhase.Paused) PauseUiState = PauseUiState.Paused;
            _hasVerifyReport = ctx.LoadVerify() is not null;
            TotalHashSampled = 0;   // 旧报告里的抽样数在加载时不重算（避免把历史数据当本次证据）
            RaiseDerived();

            // ★ FIX BATCH 6（§9）★ 这条三叉必须**按语义分流**，不能整句只写一个通道：
            //   · job-state.json 损坏 ⇒ 真问题 ⇒ ErrorSummary；
            //   · 载入成功（含 CompletedWithErrors 的"没传完"警告）⇒ 运行状态 ⇒ OperationalStatus。
            //   （此前一条 SetErrorSummary 包住整句，导致"已载入任务…"这种正常信息也被当成报错红字。）
            if (unreliable)
            {
                SetErrorSummary("⚠ 状态文件 job-state.json 损坏或缺失，进度无法显示（故意不写 0.0%，以免被当成「一点没传」）。" +
                                "点「恢复任务」会按回执重建进度，已完成的对象不会重传。");
            }
            else if (Phase == JobPhase.CompletedWithErrors)
            {
                // ★ A.5（P1-3）：载入的旧任务若是 CompletedWithErrors，必须明说"没传完 + 实际比例 + 可恢复"。
                SetOperational($"已载入任务 {ctx.JobId}（{PhaseText}）：{ObjectText} 个对象，实际完成 {ActualPercent(st):0.#}%" +
                               $"（{Format.Bytes(st.CompletedBytes)} / {Format.Bytes(st.TotalBytes)}）——**没有全部传完**，" +
                               "点「尝试修复」或「恢复任务」补齐（已完成的对象不会重传）。");
            }
            else
            {
                SetOperational($"已载入任务 {ctx.JobId}（{PhaseText}）：{ObjectText} 个对象，计划 {PlanBytesText}。");
            }
            Log("INFO", $"已载入任务 {ctx.JobId}（{PhaseText}）：{ObjectText} 个对象，计划 {PlanBytesText}" +
                        (unreliable ? "；job-state.json 损坏或缺失，进度以回执为准。" : "。"));
            await RefreshRowsFromReceiptsAsync(ct);
            await RefreshExistingJobsAsync(null, ct);
            return true;
        }
        catch (Exception ex)
        {
            SetErrorSummary($"载入任务失败：{ex.Message}");
            _log.Error(ex, "载入既有任务失败: {Job}", jobDirOrId);
            return false;
        }
    }

    /// <summary>
    /// ★ 恢复兼容（用户指定）★
    /// 把 job.json 里保存的线程值设为当前显示值：**即使下拉没有该选项也动态追加，绝不覆写**。
    /// </summary>
    public void PinThreadsFromJob(int savedThreads)
    {
        var saved = savedThreads > 0 ? savedThreads : AutoThreadsResolved;
        if (!MtOptions.Contains(saved))
        {
            // 升序插入，保持下拉顺序自然（16/32/64/128 这类旧值原样出现在列表里）
            var idx = 0;
            while (idx < MtOptions.Count && MtOptions[idx] < saved) idx++;
            MtOptions.Insert(idx, saved);
        }
        _selectedMt = saved;
        _mtValuePinnedByJob = true;
        Raise(nameof(SelectedMt));
        Raise(nameof(MtText));
        Raise(nameof(MtValuePinnedByJob));
        Raise(nameof(ResolvedThreads));
        Raise(nameof(ThreadsForRun));
        Raise(nameof(ThreadsDisclaimer));
    }

    // ────────────────────────── 内部：进度 / 行状态 / 集合维护 ──────────────────────────

    private CancellationTokenSource? _cts;
    private int _lastCompletedCount = -1;

    /// <summary>
    /// ★ 真机问题 1（2026-10-21）★ 传输字节高水位（只用于判定"是否真的在增长"）：
    /// 底栏源连接灯的"绿呼吸"必须由**真实字节增长**驱动，不能只看 Phase==Running ——
    /// 运行中完全可能长时间零增长（等待源端、写盘阻塞），那时不该呼吸（那是假活动）。
    /// 用高水位而不是"上一次值"：进度回退（引擎重建 committed 回退）不得被误判成新活动。
    /// </summary>
    private long _transferBytesHighWater;

    /// <summary>
    /// A.5（P1-3）：**实际完成比例**的唯一算法——只有 <see cref="JobPhase.Completed"/> 才允许显示 100%。
    /// 公式与 Core <c>TransferOrchestrator.Percent(JobState)</c> **逐字同口径**（先按字节、再按对象数），
    /// 保证 UI 永远不会比引擎"多算"完成度；两者都无数据时退回 job-state.json 里存的 Percent。
    /// </summary>
    private static double ActualPercent(JobState st)
    {
        if (st.TotalBytes > 0) return Math.Clamp(st.CompletedBytes * 100.0 / st.TotalBytes, 0.0, 100.0);
        if (st.TotalObjects > 0) return Math.Clamp(st.CompletedObjects * 100.0 / st.TotalObjects, 0.0, 100.0);
        return Math.Clamp(st.Percent, 0.0, 100.0);
    }

    /// <summary>
    /// ★ A.5（P1-5）★ 进度快照的同步接收器（替换原来的 <c>Progress&lt;ProgressSnapshot&gt;</c>）。
    ///
    /// ⚠ **高危点（必须保留这里的 try/catch）**：Core 的调用点
    /// <c>TransferOrchestrator.cs:820 progress?.Report(new ProgressSnapshot(…))</c> **没有任何 try/catch**，
    /// 而 <c>Progress&lt;T&gt;</c> 是**异步投递**（把回调 Post 到构造时捕获的同步上下文）所以永不外抛。
    /// 本类换成**同步**实现后，<see cref="Report"/> 里任何异常都会沿调用栈回到传输泵内部
    /// ⇒ **一个 UI 异常就会杀死整个传输**。因此这里必须兜住并只记日志。
    /// （对照：Core 对 <c>OutputLine</c>/<c>FileCopied</c> 自己是有 try/catch 的 —— RobocopyRunner.cs:415/420。）
    /// </summary>
    private sealed class SnapshotSink : IProgress<ProgressSnapshot>
    {
        private readonly Action<ProgressSnapshot> _onReport;

        public SnapshotSink(Action<ProgressSnapshot> onReport) => _onReport = onReport;

        public void Report(ProgressSnapshot value)
        {
            // 绝不外抛：否则杀死传输泵（见类型注释）。
            try { _onReport(value); }
            catch { /* 异常只计入应用日志/被忽略，绝不回到引擎调用栈 */ }
        }
    }

    /// <summary>
    /// 引擎线程上报一个进度快照（<see cref="SnapshotSink"/> 的唯一落点）。
    ///
    /// ★ 安全边界 ★：**阶段一变就立即整体应用**（不走缓冲）。理由不只是 50ms 延迟，更是**防错序**——
    /// 同一节拍窗口内若先到 Paused 再到 Running，"最新值胜出"会把暂停态整段跳过，界面上就看不出
    /// 曾经暂停过。其余数值位走"最新值胜出"+50ms 节拍（Core 本身只有 ~0.5Hz，没有任何可感延迟）。
    /// </summary>
    private void OnSnapshotReported(ProgressSnapshot s)
    {
        if (UiThrottleUnavailable) { ApplySnapshot(s); return; }

        if (s.Phase != Phase)
        {
            // 阶段切换：立即通道（顺带 Flush 掉此前累积的批量行，保证顺序）。
            PostImmediate(() => ApplySnapshot(s));
            return;
        }

        _batcher.SetLatestSnapshot(s);
    }

    /// <summary>
    /// 暂停 UI 状态机的**唯一**转换入口（FIX BATCH 2）。
    ///
    /// 输入是引擎真值（随每个 <see cref="ProgressSnapshot"/> 送达），本地只有两个变量：
    ///   · <c>_pauseRequestWritten</c>：请求文件刚写入成功、引擎还没看到 ⇒ 允许显示"正在暂停…"（诚实地说"正在试"）。
    ///   · <c>_pauseRetryAfterUtc</c>：用户重新点击暂停的时点 ⇒ 用它区分"引擎对上一轮的旧失败结论"（不重复显示）。
    ///
    /// 三条硬纪律：
    ///   ① **只有引擎**能把它推进到 Paused / PauseFailed；UI 自己最多推到 Pausing。
    ///   ② 引擎报 PauseFailed 就是"暂停失败，迁移仍在进行"——绝不能被下一次 Running 快照无声抹掉。
    ///   ③ 引擎报 Paused 之后，本地"已写入请求"的标记作废（真值接管），避免恢复后残留"正在暂停…"。
    /// </summary>
    private void ApplyPauseTruth(PauseState engineState, DateTime? requestedAt, DateTime? achievedAt,
        PauseOutcomeKind outcome, string? failureReason)
    {
        _enginePauseState = engineState;
        _enginePauseRequestedAt = requestedAt;
        _enginePauseAchievedAt = achievedAt;
        _enginePauseOutcome = outcome;
        _enginePauseFailureReason = failureReason;

        // 引擎给的失败结论是否属于"用户已经重新点击过"的那一轮：是则视为过期（界面继续显示"正在暂停…"）。
        var staleFailure = engineState == PauseState.PauseFailed && _pauseRetryAfterUtc is { } retryAfter
            && (requestedAt is null || requestedAt < retryAfter);
        // 新一轮结论已到 ⇒ 重试标记作废（否则它会一直把后续失败判成"旧结论"）。
        if (!staleFailure && _pauseRetryAfterUtc is { } fresh && requestedAt is not null && requestedAt >= fresh)
            _pauseRetryAfterUtc = null;

        PauseUiState next;
        if (staleFailure)
        {
            next = PauseUiState.Pausing;
        }
        else
        {
            switch (engineState)
            {
                case PauseState.Paused:
                    _pauseRequestWritten = false;   // 引擎接管真值
                    next = PauseUiState.Paused;
                    break;
                case PauseState.PauseFailed:
                    _pauseRequestWritten = false;   // 引擎说它停不住 ⇒ 界面不再自称"正在暂停"
                    next = PauseUiState.PauseFailed;
                    break;
                case PauseState.Pausing:
                    next = PauseUiState.Pausing;
                    break;
                default:
                    // 引擎侧没有暂停态：若本地请求刚写入、引擎尚未看到，仍如实显示"正在暂停…"；
                    // 否则就是"没有暂停在进行"（也把 Resuming 收束回 Idle）。
                    next = _pauseRequestWritten ? PauseUiState.Pausing : PauseUiState.Idle;
                    break;
            }
        }

        PauseUiState = next;

        // ★ 2026-10-08（真机问题 5）★ 暂停真值一变就同步网卡吞吐的暂停标志：
        //   暂停 ⇒ 界面必须**立刻**显示 0 B/s / ETA "—"，不能等下一次进度快照（引擎暂停后可能不再推送）。
        _networkThroughput?.MarkPaused(PauseUiState is PauseUiState.Pausing or PauseUiState.Paused);

        // 暂停的业务结果只以引擎为准（D6.3：请求受理 = Accepted；真的停住 = Succeeded；停不住 = Failed）。
        if (engineState == PauseState.Paused)
            LastPauseOutcome = new ActionOutcomeDecision(DiagnosticOutcome.Succeeded, "pause-achieved");
        else if (engineState == PauseState.PauseFailed)
            LastPauseOutcome = new ActionOutcomeDecision(DiagnosticOutcome.Failed, "pause-failed");

        // ★ Preview.6（2026-10-22）★ 暂停真值一变就把"此刻还有没有数据在搬"同步到连接层：
        //   用户在需求里明确要求"暂停后立刻停止呼吸、恢复绿色常亮"，而不是等下一次 80 ms 呈现节拍
        //   （那次 Tick 本来也会经 HasLiveTransferActivity 得到 false，这里只是不给它留延迟窗口）。
        //   ★ 只搬 Phase / PauseUiState / IsStopping 这三个**既有**事实，不新增任何判定，
        //     也不撤销可达证据（不会把绿变成黄/红）。
        SyncTransferActivityFlag(Phase == JobPhase.Running
            && next is not (PauseUiState.Pausing or PauseUiState.Paused)
            && !IsStopping);
    }

    /// <summary>
    /// ★ Preview.6 ★ 把会话层的活性结论推给连接层（唯一入口在 MainWindow 的呈现节拍，这里只做**立即**补推）。
    /// 判据与 <see cref="HasLiveTransferActivity"/> 的反向否决项逐一对应（非 Running / 暂停中 / 已暂停 / 正在停止），
    /// 方向**只允许"退出活动位"**：本方法绝不把灯点成"正在传输"，亮呼吸仍必须由呈现节拍的网络证据决定。
    /// </summary>
    private void SyncTransferActivityFlag(bool active)
    {
        try
        {
            if (active) return;                                  // 点亮方向只走呈现节拍，避免绕过网卡证据
            _connection?.SyncTransferActivity(false,
                PresentationTruth?.DisplayedTransferredBytes ?? 0L, double.NaN);
        }
        catch { /* 灯是装饰性投影：任何异常都不得影响暂停状态机 */ }
    }

    /// <summary>把引擎的进度快照翻译成显示位（唯一入口；所有 UI 更新都在 UI 线程）。</summary>
    private void ApplySnapshot(ProgressSnapshot s)
    {
        // ★ P0-04 ★ **收尾封版**：终局结论（Percent / DataLabel / StatusMessage / IsFinished）已经写出后，
        //   任何后到的旧快照都不许再改显示位——现场"结果页已给出失败结论，底栏却又跳回运行中百分比"
        //   就是这类过期回调造成的。这里只**拒绝**应用并留一条计数与日志（拒绝本身也要有证据，
        //   不能是静默丢弃）；解除封版只发生在"新一次运行 / 换任务"（ResetRunProjection）。
        if (_progressSealed)
        {
            _postFinishSnapshotsIgnored++;
            _log.Debug(
                "收尾后忽略过期进度快照：job={JobId} 快照 phase={Phase} percent={Percent:0.###}（终局结论保持不变）",
                Ctx?.JobId, s.Phase, s.Percent);
            return;
        }

        // ★ A.5（P1-3）：进度条只有在**真 Completed** 时才走满；CompletedWithErrors 用引擎报的实际比例。
        var finished = s.Phase is JobPhase.Completed or JobPhase.CompletedWithErrors;
        Phase = s.Phase;
        // ★ FIX BATCH 2 ★ 唯一转换入口：暂停真值只来自引擎（含 PauseState/PauseRequestedAt/PauseAchievedAt/
        //   PauseOutcome/PauseFailureReason），UI 绝不自己反推、也绝不被"点击"直接标成已暂停。
        ApplyPauseTruth(s.PauseState, s.PauseRequestedAt, s.PauseAchievedAt, s.PauseOutcome, s.PauseFailureReason);
        IsFinished = finished;
        DataLabel = finished ? "实际落盘 / 计划" : "已传 / 计划";
        // ★ FIX BATCH 4（§7.1）★ 进度真值唯一来源：引擎随快照送来的 ProgressTruthSnapshot。
        //   下面的百分比/字节/速率/ETA 全部由它派生（顶栏与底栏因此天然同源）；Truth 为 null 只出现在
        //   测试或兼容路径，此时按快照原始字段如实显示，绝不自己另算一套。
        // ★ Round-2 2026-10-05（§2）★ 真值分工（连续性只在**显示层**，业务权威仍是 Completed 回执）：
        //   · LastTruth       = 引擎 raw 真值（诊断/解释用，永不改写）；
        //   · PresentationTruth = 供 UI 显示的真值（顶栏/底栏/百分比/字节/速率/ETA 的唯一来源）——
        //     在"暂停 / 停止 / 恢复"期间被高水位托底（绝不倒退，也绝不被当成对象已完成），
        //     引擎 raw 追平高水位后自动解除（原因码见 ContinuationDisplayState）。
        var rawTruth = s.Truth;
        //  ★ Round-2 2026-10-05（§2.1）★ 连续性状态机：把引擎 raw 真值翻译成"显示用"真值。
        //    冻结/追赶期内显示停在高水位（raw 追上即自动解除）；只有显式回滚 / 换任务 / 真完成才允许下降。
        var continuity = _continuation.Apply(rawTruth, s.Phase, Ctx?.JobId);
        RecordProgressTransition(s, rawTruth, continuity);
        LastTruth = rawTruth;
        // ★ v0.5.4 Preview.2（P1-A）★ 同步"有无可信中途来源"的低频派生位。
        //   放在 LastTruth 赋值之后、消费端读取之前：只在真正翻转时发一次通知。
        SyncInflightSourceFlag();
        var displayTruth = continuity.Effective;
        // ★ 真机复验返修（2026-10-05 run2）★ UI 消费端（顶栏 Step3ProgressPage.PushState /
        //   底栏 MainWindow.PushFooter）读的是**显示真值**，不是 raw：
        //   真机证据 —— run2 恢复后 raw 重建为 0（committed 回执尚未重建），floor 已把显示真值托在
        //   10.7 GB，但两处消费端仍在读 LastTruth(raw) ⇒ 用户看到 99.9% → 0.0% 的倒退。
        PresentationTruth = displayTruth;
        // ★ Round-2 2026-10-05（§3.2）★ 推进**呈现**时间线：数字与进度条共用的视觉目标在这里被设定。
        UpdatePresentationTimeline(s.Phase, displayTruth, s.TotalBytes);
        // ★ R2 接手修复 · 缺陷 H（S-4）★ 兜底通路也不能凭空给 100%。
        //   改前本行有两重绕过：① displayTruth.Percent 在 plannedBytes<=0 且 settled 时直接给 100
        //   （settled 还包含 CompletedWithErrors）；② truth 为 null 时按 Phase==Completed 写 100。
        //   现在：可信完成才允许 100.0，否则取真值/计划比例并封顶 99.9。
        // ★ R2 接手修复 · S-4 滞后一帧（第二轮）★ 判据必须取自**本帧快照自身**：
        //   旧实现只看 TrustworthyCompletion，而它是上一次按落盘状态算出的结论 ——
        //   收尾那一帧的 Completed 快照拿到的仍是"运行中"的旧值，于是字节已闭环也只显示 99.9%，
        //   要等收尾路径再写一次才变 100（真机表现为进度条卡在 99.9%）。
        // ★ 缺陷 J ★ 这里写的是**本帧**的快照百分比，正是 UI 要读的那份真值；
        //   必须与 PresentationTruth.Percent 保持一致（PublishPercent 负责同步）。
        PublishPercent(SnapshotSaysTrustworthyCompletion(s) || TrustworthyCompletion
            ? 100.0
            : Math.Min(displayTruth?.Percent ?? s.Percent, TransferOrchestrator.SettledPercentCapPublic));
        var displayedBytes = displayTruth?.DisplayedTransferredBytes ?? s.CompletedBytes;
        // ★ Preview.4 真机问题 2（2026-10-22）★ 活性证据改为"两条任一"，并**不再**由本方法直接驱动灯：
        //   灯的唯一同步入口是 MainWindow 的呈现节拍（ConnectionViewModel.SyncTransferActivity），
        //   因为只有那里能同时看到"网卡在不在收数据"。
        //   这里只维护字节判据（小文件通道有效；/J 大文件通道下 committed 不动是正常的，见
        //   「通道的目标文件可能被预分配文件长度…本通道只使用 robocopy 已确认的输出」）。
        if (displayedBytes > _transferBytesHighWater)
        {
            _transferBytesHighWater = displayedBytes;
            _bytesAdvancing = s.Phase == JobPhase.Running;
        }
        else if (s.Phase != JobPhase.Running)
        {
            _bytesAdvancing = false;
        }
        var plannedBytes = displayTruth?.PlannedBytes ?? s.TotalBytes;
        ProgressText = $"{Format.Bytes(displayedBytes)} / {Format.Bytes(plannedBytes)}";
        PlanBytesText = Format.Bytes(plannedBytes);
        ActualBytesText = Format.Bytes(displayedBytes);
        BalanceText = $"计划 {Format.Bytes(plannedBytes)}　·　实际落盘 {Format.Bytes(displayedBytes)}";
        var truthSpeed = displayTruth?.SpeedBytesPerSecond ?? s.BytesPerSecond;
        var truthEta = displayTruth?.EtaSeconds ?? s.EtaSeconds;
        EngineSpeedText = truthSpeed > 0 ? Format.Speed(truthSpeed) : "—";
        EtaText = double.IsNaN(truthEta) ? "—" : Format.Eta(truthEta);
        // ★ 2026-10-08（真机问题 5）★ 界面实时速度/ETA 改由**真实网卡接收吞吐**提供
        //   （Progress Truth 的速率仍是"逻辑完成口径"，只作诊断/回执用途，不再驱动底栏与 Step3）。
        //   这里只喂两个单向输入：暂停标志 + 剩余逻辑字节（= max(0, 计划 − 已显示)，与 ETA 被除数同源）。
        var remainingLogicalBytes = plannedBytes > displayedBytes ? plannedBytes - displayedBytes : 0;
        SyncNetworkThroughputInputs(
            PauseUiState is PauseUiState.Pausing or PauseUiState.Paused,
            remainingLogicalBytes,
            IsTerminalPhase(s.Phase));
        // 检查点用的"已完成对象数"镜像（只读；业务权威仍是 Completed 回执与 s.CompletedObjects）。
        _completedObjectsSeen = s.CompletedObjects;
        ObjectText = $"{s.CompletedObjects}/{s.TotalObjects}" + (s.FailedObjects > 0 ? $"（失败 {s.FailedObjects}）" : "");
        FailedObjects = s.FailedObjects;
        CurrentObjectPath = s.CurrentObjectPath ?? string.Empty;
        StallSeconds = s.StallSeconds;

        // ★ FIX BATCH 2 ★ 暂停/恢复进行中的状态句**必须**以暂停真值为准：
        //   引擎还没确认停住时，绝不允许下面那行 `s.Message`（普通运行文案）把「正在暂停…」顶掉——
        //   这正是旧实现"点击后一会儿又变回进行中"的假象来源之一。
        if (PauseUiState is PauseUiState.Pausing or PauseUiState.PauseFailed)
        {
            SetOperational(PauseStateText);
        }
        else
        {
            SetOperational(s.Phase == JobPhase.Paused
                ? $"‖ 已暂停：已完成 {s.CompletedObjects}/{s.TotalObjects} 个对象，剩余 {Math.Max(0, 100.0 - Percent):0.#}% 未传" +
                  $"（{Format.Bytes(Math.Max(0, plannedBytes - displayedBytes))}）——点「恢复任务」续传（已完成的对象不会重传）。"
                // ★ A.5（P1-3）：引擎给 CompletedWithErrors 的结束语**不再原样透传**（引擎文案可能像"完成"）。
                //   这里统一改写成"未完整完成 + 实际比例 + 可恢复/可修复"，与 FinishRunAsync 同一口径。
                : s.Phase == JobPhase.CompletedWithErrors
                    ? $"◐ 迁移**未完整完成**（存在失败对象，实际完成 {s.Percent:0.#}%：" +
                      $"{Format.Bytes(s.CompletedBytes)} / {Format.Bytes(s.TotalBytes)}）。" +
                      "失败清单已逐条列出；点「尝试修复」或「恢复任务」处理（已完成的对象不会重传）。"
                    : s.Message);
        }

        // 当前对象 + 停滞提示（"界面长时间不动"是用户判断卡死的直接原因）
        if (!string.IsNullOrEmpty(s.CurrentObjectPath) && s.Phase == JobPhase.Running)
        {
            CurrentObjectDetail = $"当前对象：{s.CurrentObjectPath}，已传 {Format.Bytes(s.CurrentObjectDoneBytes)}" +
                (s.CurrentObjectTotalBytes > 0 ? $"/{Format.Bytes(s.CurrentObjectTotalBytes)}" : string.Empty);
            // ★ 真机问题 3（2026-10-21）★ 对象级真值：文件级事件缺席时右栏靠它说清"在传什么、传了多少"。
            ActiveObjectName = DescribeObjectName(s.CurrentObjectPath);
            ActiveObjectProgressText = s.CurrentObjectTotalBytes > 0
                ? $"{Format.Bytes(s.CurrentObjectDoneBytes)} / {Format.Bytes(s.CurrentObjectTotalBytes)}"
                : Format.Bytes(s.CurrentObjectDoneBytes);
            var stalled = s.StallSeconds >= StallWarnSeconds;
            StallHintText = stalled
                ? $"⏳ 已 {s.StallSeconds:0} 秒没有新的落盘字节（不是卡死：引擎仍在等待源端/正在写入）。" +
                  (s.CurrentObjectHasLargeFile ? "大文件可能数分钟无进度变化，请勿终止。" : "若持续过久请查看日志面板。")
                : s.CurrentObjectHasLargeFile
                    ? "该对象含大文件（≥分流阈值）：复制期间可能数分钟无进度变化，属正常，请勿终止。"
                    : string.Empty;
        }
        else
        {
            CurrentObjectDetail = string.Empty;
            StallHintText = string.Empty;
            // ★ 真机问题 3 ★ 运行结束/暂停即清空对象级活动真值：绝不能把上一轮的对象留在面板上
            //   冒充"正在传输"（任务令 §6.2：暂停、停止、完成后不得保留过期活动信息）。
            ActiveObjectName = string.Empty;
            ActiveObjectProgressText = string.Empty;
        }

        // ★ FIX BATCH 6（§9）★ 当前对象通道：只由**引擎真值**写入（对象 id 来自快照，
        //   不是 UI 从路径反推），供左侧提示面板显示"现在在传什么"。运行结束即清空。
        SetChannel(StatusChannel.CurrentObjectStatus, s.Phase == JobPhase.Running && !string.IsNullOrEmpty(s.CurrentObjectId)
            ? $"当前对象 {s.CurrentObjectId}" + (string.IsNullOrEmpty(s.CurrentObjectPath) ? string.Empty : $"\n{s.CurrentObjectPath}")
            : string.Empty);

        if (s.CompletedObjects != _lastCompletedCount)
        {
            _lastCompletedCount = s.CompletedObjects;
            _ = RefreshRowsFromReceiptsAsync();   // 回执读取是 IO：后台跑，UI 更新回队列
        }
        ApplyObjectRowStatus(s);
    }

    // ── ★ Round-2 2026-10-05（§5 / §7 最小可诊断性）★ 进度真值过渡的结构化记录 ──────────────
    //   纪律：只在"阶段改变 / Pause-Stop-Resume / 用户动作边界 / 恢复后前若干样本 / InFlightSource 改变 /
    //   AttemptEpoch 改变 / RetryState 改变 / progress 向后 / 单次跳变超阈值"时记录
    //   （**绝不** 5 Hz 全量写盘）；
    //   它只记录，绝不改业务行为、绝不改显示值（显示值由 ContinuationDisplayState 决定）。
    private JobPhase? _transitionPhase;
    private ProgressTruthSource? _transitionSource;
    private long _transitionEpoch = -1;
    private RetryState? _transitionRetry;
    private long _transitionDisplayedBytes = -1;
    private double _transitionPercent;
    private int _resumeSampleCount;
    private PauseUiState _transitionPause = PauseUiState.Idle;

    /// <summary>单次跳变阈值：max(8 MiB, 计划量的 2%)。</summary>
    private static long TransitionJumpThreshold(long plannedBytes)
        => Math.Max(8L * 1024 * 1024, plannedBytes > 0 ? (long)(plannedBytes * 0.02) : 0L);

    /// <summary>
    /// ★ Round-2 2026-10-05（§3.2）★ 把一拍**显示真值**交给呈现时间线，并按相位决定冻结 / 解冻。
    /// 本方法只决定"视觉应该画到哪里"，不参与任何业务判定：
    ///   已确认显示真值（<c>ContinuationDisplayState</c> 的 effective）是视觉的**上限**；
    ///   视觉落后时由 80 ms 呈现定时器逐帧追平（§3.3 的"大号百分比与进度条同源"）。
    /// </summary>
    private void UpdatePresentationTimeline(JobPhase phase, ProgressTruthSnapshot? displayTruth, long fallbackPlannedBytes)
    {
        try
        {
            var utc = DateTime.UtcNow;
            var settled = phase is JobPhase.Completed or JobPhase.CompletedWithErrors;
            var percent = displayTruth?.Percent ?? (settled ? 100d : 0d);
            var bytes = displayTruth?.DisplayedTransferredBytes ?? 0L;
            var planned = displayTruth?.PlannedBytes ?? fallbackPlannedBytes;

            _presentation.ApplyTruth(percent, bytes, planned, settled, Ctx?.JobId, utc);

            if (settled)
            {
                // 真完成：允许一次到 100% 的收敛补间（只有引擎真的完成才进入这条分支）
                _presentation.ResumeLive(utc, "settled");
            }
            else if (phase == JobPhase.Running)
            {
                _presentation.ResumeLive(utc, "running");
            }
            else if (phase is not (JobPhase.Created or JobPhase.Planned))
            {
                // 暂停 / 停止 / 中断 / 失败 / 待复核 / 校验中：视觉冻结在用户已看到的值上
                _presentation.Freeze(percent, utc, "phase-" + phase);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "呈现时间线更新失败（已吞掉，不影响进度真值）");
        }
    }

    /// <summary>
    /// ★ UI Closure 2026-10-05（§5）★ 把进度过渡写成**结构化单行日志**（落盘走 Serilog；未解释回退同时上
    /// UI 日志面板）。对"未解释的进度回退"给出 <c>UnexpectedProgressRegression</c>（Error 级）。
    /// </summary>
    private void RecordProgressTransition(ProgressSnapshot s, ProgressTruthSnapshot? rawTruth, ContinuationOutcome continuity)
    {
        try
        {
            var raw = rawTruth?.DisplayedTransferredBytes ?? s.CompletedBytes;
            var planned = rawTruth?.PlannedBytes ?? s.TotalBytes;
            var percentNow = rawTruth?.Percent ?? s.Percent;

            var phaseChanged = _transitionPhase != s.Phase;
            var sourceChanged = rawTruth is not null && _transitionSource != rawTruth.InFlightSource;
            var epochChanged = rawTruth is not null && _transitionEpoch != rawTruth.AttemptEpoch;
            var retryChanged = rawTruth is not null && _transitionRetry != rawTruth.RetryState;
            var pauseChanged = _transitionPause != PauseUiState;
            var jumped = _transitionDisplayedBytes >= 0
                && Math.Abs(raw - _transitionDisplayedBytes) >= TransitionJumpThreshold(planned);
            var regressed = continuity.RawRegressionBytes > 0;
            // ★ Round-3 PHASE A（§4.5）★ 无解释"前跳"检测，与回退检测并列（缺一不可）。
            //   只在用户按下 Pause/Stop 之后才有意义（动作边界基线已设置）；trustedIoDeltaBytes 传 0：
            //   此刻引擎已停止上报，任何"新增字节"都没有证据支撑 —— 这正是 /Z 预分配污染的签名
            //   （暂停前 30 GB → 暂停后 41.96 GB）。上一轮的判据只查倒退，于是把它判成了"通过"。
            //   若该对象确实刚刚完成，本行会与 Completed 回执同时出现，人工核对即可判为误报，不会静默。
            var leapForward = _continuation.TrackRawForwardLeap(raw, planned, 0L);
            var resumeWindow = _continuation.IsActive && _resumeSampleCount < 10;

            if (phaseChanged || sourceChanged || epochChanged || retryChanged || pauseChanged
                || jumped || regressed || leapForward || resumeWindow || continuity.ClearedThisCall)
            {
                var jobId = Ctx?.JobId ?? "-";
                var previousBytes = _transitionDisplayedBytes < 0
                    ? "n/a"
                    : _transitionDisplayedBytes.ToString(CultureInfo.InvariantCulture);
                var line = string.Create(CultureInfo.InvariantCulture,
                    $"ProgressTruthTransition job={jobId} phase={s.Phase} previousDisplayedBytes={previousBytes} " +
                    $"newDisplayedBytes={raw} previousPercent={_transitionPercent:0.###} newPercent={percentNow:0.###} " +
                    $"committed={rawTruth?.CommittedBytes ?? -1} inFlight={rawTruth?.InFlightConfirmedBytes ?? -1} " +
                    $"source={rawTruth?.InFlightSource.ToString() ?? "n/a"} object={rawTruth?.CurrentObjectId ?? "-"} " +
                    $"epoch={rawTruth?.AttemptEpoch ?? -1} retry={rawTruth?.RetryState.ToString() ?? "n/a"} " +
                    $"pause={PauseUiState} continuation={continuity.Mode} highWaterBytes={continuity.HighWaterBytes} " +
                    $"continuationCleared={continuity.ClearedThisCall} clearReason={continuity.ClearReason} " +
                    $"rawRegressionBytes={continuity.RawRegressionBytes} " +
                    $"rawForwardLeapBytes={_continuation.LastRawForwardLeapBytes} " +
                    $"rawForwardLeapPercent={_continuation.LastRawForwardLeapPercent:0.###} " +
                    $"actionCheckpointBytes={_continuation.ForwardLeapBaselineBytes} utc={DateTime.UtcNow:O}");

                if (regressed && !ProgressRegressionExplained(rawTruth, s.Phase, continuity))
                {
                    _log.Error("UnexpectedProgressRegression（进度出现未解释回退）：{ProgressTransition}", line);
                    Log("ERROR", "进度出现未解释回退（UnexpectedProgressRegression）：" + line);
                }
                else if (leapForward)
                {
                    // ★ Round-3 PHASE A ★ 无解释前跳：暂停/停止边界之后，引擎 raw 比用户看到的可信量
                    //   高出一大截，且没有任何可信 I/O 增量支撑。典型成因 = /Z 预分配长度被当成已传字节
                    //   （P0）。诊断必须显式点出可能成因，避免下一轮又被"零倒退"骗过去。
                    _log.Error("UnexpectedProgressLeapForward（进度出现无证据前跳，疑似 /Z 预分配污染）：{ProgressTransition}", line);
                    Log("ERROR", "进度出现无证据前跳（UnexpectedProgressLeapForward，疑似 /Z 预分配污染）：" + line);
                }
                else
                {
                    _log.Information("{ProgressTransition}", line);
                }
            }

            _transitionPhase = s.Phase;
            _transitionSource = rawTruth?.InFlightSource;
            _transitionEpoch = rawTruth?.AttemptEpoch ?? -1;
            _transitionRetry = rawTruth?.RetryState;
            _transitionDisplayedBytes = raw;
            _transitionPercent = percentNow;
            _transitionPause = PauseUiState;
            _resumeSampleCount = _continuation.IsActive ? _resumeSampleCount + 1 : 0;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "进度过渡记录失败（已吞掉，不影响显示与业务）");
        }
    }

    /// <summary>回退是否有解释：本轮刚解除 floor / 显式回滚或重试轮次 / 停在暂停或中断态。</summary>
    private static bool ProgressRegressionExplained(ProgressTruthSnapshot? rawTruth, JobPhase phase, ContinuationOutcome continuity)
        => continuity.ClearedThisCall
           || (rawTruth is not null && rawTruth.RetryState != RetryState.None)
           || phase is JobPhase.Paused or JobPhase.Interrupted or JobPhase.Canceled
              or JobPhase.Completed or JobPhase.CompletedWithErrors;

    /// <summary>按进度快照标注"当前对象"的行状态。</summary>
    private void ApplyObjectRowStatus(ProgressSnapshot s)
    {
        if (string.IsNullOrEmpty(s.CurrentObjectId)) return;
        var row = Objects.FirstOrDefault(o => string.Equals(o.ObjectId, s.CurrentObjectId, StringComparison.OrdinalIgnoreCase));
        if (row is null) return;
        // 文案收敛依据（JobPhase 取值见 PCMig.Core/Models/Models.cs:15-30；本方法只在
        // ApplyProgressSnapshot 里、且快照带真实 CurrentObjectId 时被调用）：
        //   Running / Verifying  —— 该对象确实在跑（Verifying 是对象收尾校验，块仍在处理），⇒「传输中」；
        //   其余一切非运行态（Created/Preflight/Scanning/Planned/AwaitingReview/Paused/Completed/
        //   CompletedWithErrors/Interrupted/Failed/Canceled）—— 对象都不在跑 ⇒ 不得冒充"传输中"。
        // 这些行最终由回执权威（Receipt，见 RefreshRowsFromReceiptsAsync）逐对象改写；
        // 这里只做"当前对象"的实时标注，故非运行态保留中性词「处理中」，不写「待传输」以免误导
        // （「待传输」只用于从未开始的初始状态，见 StatusText 字段初值）。
        row.StatusText = s.Phase switch
        {
            JobPhase.Running or JobPhase.Verifying => $"传输中（{Format.Bytes(s.CurrentObjectDoneBytes)}" +
                                (s.CurrentObjectTotalBytes > 0 ? $"/{Format.Bytes(s.CurrentObjectTotalBytes)}" : string.Empty) + "）",
            JobPhase.Paused => "‖ 已暂停",
            _ => "处理中",
        };
    }

    /// <summary>从回执重建每行的权威状态（Receipt 是唯一权威，job-state.json 只是视图）。</summary>
    private async Task RefreshRowsFromReceiptsAsync(CancellationToken ct = default)
    {
        var ctx = Ctx;
        if (ctx is null) return;
        List<ObjectReceipt> receipts;
        try { receipts = await Task.Run(() => ctx.LoadReceipts(_log), ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _log.Warning(ex, "读取回执失败"); return; }

        var latest = receipts
            .GroupBy(r => r.ObjectId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.CompletedUtc).Last(), StringComparer.OrdinalIgnoreCase);

        // ★ P1-4（Preview.2）★ 列表里的「原因」是 ErrorTranslator.ShortReason 的 100 字符截断版，
        //   真机那次失败的真因（目录级失败，只写在 robocopy 汇总表里）在列表里读不出来。
        //   这里为每条**未成功**回执预生成「完整原因」（回执原文 + 退出码位含义 + 日志证据摘要 + 路径指引），
        //   由 Step4 的「查看完整原因」弹窗展示。读日志是磁盘 I/O ⇒ 必须放在后台线程做。
        Dictionary<string, string> fullTexts;
        try { fullTexts = await Task.Run(() => BuildFullReasonTexts(ctx, latest.Values), ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _log.Warning(ex, "生成失败原因详情失败（列表仍会显示截断原因，不影响任何判定）");
            fullTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        Post(() =>
        {
            foreach (var row in Objects)
            {
                if (!latest.TryGetValue(row.ObjectId, out var r)) continue;
                row.StatusText = r.Status switch
                {
                    ObjectStatus.Completed => $"✔ 已传输（{Format.Bytes(r.TargetBytes)}）",
                    ObjectStatus.CompletedWithErrors => "◐ 未完整完成（有错误）",
                    ObjectStatus.Failed => "✘ 失败",
                    ObjectStatus.Interrupted => "‖ 已中断（等待恢复）",
                    ObjectStatus.Skipped => "跳过",
                    _ => r.Status.ToString(),
                };

                // ★ A.5（P1-4）★ 失败清单必须覆盖**三种未成功状态**：Failed / CompletedWithErrors / Interrupted。
                //   改前只对 Failed 调用 AddFail ⇒ 后两种对象在 Step 4「报告清单」里凭空消失
                //   （旧 WPF MainViewModel L1605/L1611 对 Failed 与 CompletedWithErrors 都处理）。
                //   回执缺 ErrorDetail 时也要给出**可读的真实说明**，不允许静默留空。
                if (r.Status is ObjectStatus.Failed or ObjectStatus.CompletedWithErrors or ObjectStatus.Interrupted)
                {
                    var headline = r.Status switch
                    {
                        ObjectStatus.Failed => "✘ 对象失败",
                        ObjectStatus.CompletedWithErrors => "◐ 对象未完整完成",
                        _ => "‖ 对象中断",
                    };
                    var reason = !string.IsNullOrWhiteSpace(r.ErrorDetail)
                        ? ErrorTranslator.ShortReason(r.ErrorDetail)
                        : r.Status switch
                        {
                            ObjectStatus.Failed => "回执未记录错误明细（可能是引擎被终止或错误未被记录）：请打开报告/任务日志核对后「尝试修复」。",
                            ObjectStatus.CompletedWithErrors => "回执未记录错误明细：该对象有文件未复制成功，属未完整完成，可「尝试修复」重拷差异。",
                            _ => "该对象未跑完（已拷入的部分保留），可「恢复任务」或「尝试修复」续传。",
                        };
                    AddFail($"{headline}　{r.ObjectId}　{r.TargetPath}",
                        ErrorTranslator.FailureHeadline(r.ObjectId, r.TargetPath, r.RobocopyExitCodeBulk, r.RobocopyExitCodeLarge)
                        + "｜" + reason,
                        objectLevel: true,
                        fullText: fullTexts.TryGetValue(r.ObjectId, out var full) ? full : null);
                }
            }
        });
    }

    /// <summary>
    /// 为每条**未成功**回执生成「完整原因」文本（Step4「查看完整原因」弹窗用）。**只读**：不参与任何判定、
    /// 不写回执、不改 job-state。内容 = 退出码位含义 + 回执原文（未截断）+ robocopy 日志证据摘要
    /// （汇总表原文 / 文件级错误行 / 无证据时的诚实说明）+ 日志与报告的绝对路径。
    /// </summary>
    private static Dictionary<string, string> BuildFullReasonTexts(JobContext ctx, IEnumerable<ObjectReceipt> receipts)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in receipts)
        {
            if (r.Status is not (ObjectStatus.Failed or ObjectStatus.CompletedWithErrors or ObjectStatus.Interrupted)) continue;

            var sb = new StringBuilder();
            sb.AppendLine(ErrorTranslator.FailureHeadline(r.ObjectId, r.TargetPath, r.RobocopyExitCodeBulk, r.RobocopyExitCodeLarge));
            sb.AppendLine($"对象：{r.ObjectId}　第 {r.Attempt} 趟　状态：{StatusWord(r.Status)}　错误类别：{r.ErrorClass}");
            sb.AppendLine($"源：{r.SourcePath}");
            sb.AppendLine($"目标：{r.TargetPath}（本趟已落盘 {Format.Bytes(r.TargetBytes)} / {r.TargetFiles} 个文件）");
            sb.AppendLine($"Bulk 退出码：{ExitCodePhrase(r.RobocopyExitCodeBulk)}");
            sb.AppendLine($"Large 退出码：{ExitCodePhrase(r.RobocopyExitCodeLarge)}");
            sb.AppendLine();
            sb.AppendLine("── 任务记录的原因（回执原文，未截断）──");
            // v0.5.3：与 HTML 报告共用同一脱敏口径（FailureEvidenceCollector.LooksSensitive）。
            sb.AppendLine(string.IsNullOrWhiteSpace(r.ErrorDetail)
                ? "（回执未记录错误明细）"
                : FailureEvidenceCollector.LooksSensitive(r.ErrorDetail) ? "（该行疑似含凭据，已隐去）" : r.ErrorDetail);
            sb.AppendLine();

            try
            {
                var ev = FailureEvidenceCollector.Collect(ctx.RoboLogsDir, r.ObjectId, r.ErrorDetail);
                sb.AppendLine("── robocopy 日志证据 ──");
                sb.AppendLine($"日志：{ev.LogFileName}（{(ev.LogFound ? Format.Bytes(ev.LogBytes) : "缺失")}，{ev.TotalLines} 行，编码 {ev.EncodingLabel}）");

                var summary = ev.Lines.Where(l => l.Kind == "summary").Select(l => l.Raw).ToList();
                if (summary.Count > 0)
                {
                    sb.AppendLine("汇总表（日志原文；看「失败」那一列，非 0 就是这一趟真没复制成的条目数）：");
                    foreach (var line in summary) sb.AppendLine("    " + line);
                }

                if (ev.ErrorLineCount > 0)
                {
                    sb.AppendLine($"文件级错误行（共 {ev.ErrorLineCount} 条，此处最多列 20 条；完整原文见下面的日志路径）：");
                    foreach (var l in ev.Lines.Where(l => l.Kind != "summary").Take(20))
                        sb.AppendLine($"    {(l.Code.Length > 0 ? "[" + l.Code + "] " : "")}{l.Path}");
                }
                else
                {
                    sb.AppendLine(ev.NoEvidenceText());
                }

                if (ev.HintAppearsInLog && ev.HintIsNotAnError)
                {
                    sb.AppendLine();
                    sb.AppendLine($"注意：回执里给的这条原因在日志里出现了 {ev.HintMentions} 次，但都是普通状态行（不是错误行）"
                        + "——不能把它当失败原因看。");
                }
            }
            catch (Exception ex)
            {
                // 证据读不出来也要有话说；绝不因此改动列表里已有的事实。
                sb.AppendLine($"（日志证据读取失败：{ex.Message}）");
            }

            sb.AppendLine();
            sb.AppendLine($"完整日志：{Path.Combine(ctx.RoboLogsDir, r.ObjectId + ".log")}");
            sb.AppendLine($"任务目录：{ctx.JobDir}");
            sb.AppendLine($"报告目录：{ctx.ReportDir}");

            map[r.ObjectId] = sb.ToString();
        }
        return map;
    }

    private static string StatusWord(ObjectStatus s) => s switch
    {
        ObjectStatus.Failed => "失败",
        ObjectStatus.CompletedWithErrors => "未完整完成（有错误）",
        ObjectStatus.Interrupted => "已中断（等待恢复）",
        _ => s.ToString(),
    };

    private static string ExitCodePhrase(int code)
        => code < 0 ? "未执行（该通道没跑）" : $"{code}（{ErrorTranslator.ExitCodeText(code)}）";

    /// <summary>把计划对象填进清单（RootFiles 给人话标签；体积未知时写"传输时实测"）。
    /// ★ P1-07(2) ★ 同时带上计划里的目标绝对路径，让评审期就能看到真实映射。</summary>
    private void FillObjectsFromPlan(MigrationPlan? plan)
    {
        Objects.Clear();
        if (plan is null) return;
        foreach (var o in plan.Objects)
        {
            Objects.Add(new SessionObjectRow(
                o.ObjectId,
                o.Kind == ObjectKind.RootFiles ? $"{o.SourcePath}　［根目录散落文件］" : o.SourcePath,
                o.EstimatedBytes < 0 ? "传输时实测" : Format.Bytes(o.EstimatedBytes),
                o.TargetPath));
        }
    }

    /// <summary>把验证报告的不一致对象回写到清单（与 CLR 报告同一事实）。</summary>
    private void ApplyVerifyToRows(VerifyReport report)
    {
        // ★ A.5（P1-4）：先把**上一次验证**派生的条目撤掉（修复后重新验证时，旧结论不能继续挂着）。
        ClearVerifyDerivedFails();
        foreach (var r in report.Objects)
        {
            var row = Objects.FirstOrDefault(o => string.Equals(o.ObjectId, r.ObjectId, StringComparison.OrdinalIgnoreCase));
            if (row is null) continue;
            // ★ P0-02 ★ 不再二值判断（旧代码 r.Status == "OK" ? "一致" : "不一致"）：现场三种完全不同的
            //   状态（目标真的多文件 / 根层统计缺陷 / NTUSER 真锁未到）被压成同一句话。现在逐态陈述。
            row.StatusText = VerifyStatusWord(r);
            if (r.Status != "OK")
            {
                var detail = $"源 {r.SourceFiles} 个文件 / {Format.Bytes(r.SourceBytes)}　→　目标 {r.TargetFiles} 个文件 / {Format.Bytes(r.TargetBytes)}";
                if (r.SourceUnavailable) detail += "　［源根不可达］";
                if (r.TargetUnavailable) detail += "　［目标根不可达］";
                // ★ F-1（2026-10-13 定点修复）★ 只有确证缺失才写"缺失样例"；其余（源/目标不可达、
                //   枚举不全）一律"无法确认（未核实）"，避免用户在不可确认状态下按"缺失"去删自己目标盘上的文件。
                if (r.MissingSamples.Count > 0)
                    detail += (r.MissingConfirmed ? "　缺失样例：" : "　无法确认（未核实，不得当作缺失）样例：") +
                              string.Join("、", r.MissingSamples.Take(3)) +
                              (r.MissingSamples.Count > 3 ? $" 等 {r.MissingSamples.Count} 项" : "");
                if (r.ExtraOnTarget.Count > 0)
                    detail += $"　目标多出：{string.Join("、", r.ExtraOnTarget.Take(3))}" +
                              (r.ExtraOnTarget.Count > 3 ? $" 等 {r.ExtraOnTarget.Count} 项" : "") + "（非缺失，不自动清理）";
                if (r.SizeMismatched > 0) detail += $"　同名不同长度：{r.SizeMismatched} 个";
                if (r.HashReadFailed > 0) detail += $"　抽样读取失败：{r.HashReadFailed} 个（≠ 内容不同）";
                if (r.EnumerationSkipped > 0) detail += $"　枚举跳过：{r.EnumerationSkipped} 个（统计不完整）";
                AddFail($"✘ 校验{r.StatusText}　{r.ObjectId}", detail, objectLevel: true, verifyDerived: true);
            }
        }
        if (!report.OverallPass && report.Objects.Any(o => o.HashMismatched > 0))
            AddFail("内容级抽样不一致", $"{report.Objects.Sum(o => o.HashMismatched)} 个抽样文件 SHA-256 不匹配（内容确实不同）。",
                verifyDerived: true);
    }

    /// <summary>
    /// ★ P0-02 ★ 逐对象的验证结论用词（与 <c>ObjectVerifyResult.StatusText</c> 同一口径）。
    /// 「无法确认」和「无可核对数据」都**不是**通过：前者是源不可达/枚举被跳过/两侧都空但计划预期有数据，
    /// 后者是计划与实测都为空。只有 OK 才是核对过的通过。
    /// </summary>
    private static string VerifyStatusWord(ObjectVerifyResult r) => r.Status switch
    {
        "OK" => "✔ 通过（数量/字节一致）",
        "MISMATCH" => "✘ 存在差异",
        "UNKNOWN" => "？ 无法确认",
        "EMPTY" => "○ 无可核对数据（两侧为空）",
        "CANCELED" => "… 已取消",
        _ => r.Status,
    };

    private readonly HashSet<string> _failIndex = new(StringComparer.Ordinal);

    /// <summary>「由上一次验证报告派生」的失败清单条目 key（重新验证时先整批撤掉，避免旧结论残留）。</summary>
    private readonly HashSet<string> _verifyFailKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// ★ A.5（P1-4）★ 清空失败清单的**唯一入口**。
    /// 原先只有 <c>FailItems.Clear()</c>，去重索引 <see cref="_failIndex"/> 从不清 ⇒ Clear 之后
    /// 索引仍然认为"这条失败已经显示过"，同一失败项**永远无法再显示**（Repair / 载入任务后失败清单空白）。
    /// 今后任何清空都必须走这里，禁止直接调 <c>FailItems.Clear()</c>。
    /// </summary>
    private void ClearFailItems()
    {
        FailItems.Clear();
        _failIndex.Clear();
        _verifyFailKeys.Clear();
        // ★ A.5（D1）：丢弃计数与"另有 N 条"提示行必须同步归零/撤掉，
        //   否则上一轮的 N 会留在面板里冒充本轮的事实（不诚实）。
        _failDropped = 0;
        UpdateOmittedHint(ref _shownOmittedFails, 0, "条失败记录", "完整失败清单见任务目录的报告/日志");
    }

    /// <summary>
    /// 撤掉"上一次验证报告派生"的失败条目（只撤验证派生的，不碰回执派生的真实失败）。
    /// 用途：修复后重新验证时，上一轮写下的「校验不一致」不能继续挂在清单上（那时它已经一致了）。
    /// </summary>
    private void ClearVerifyDerivedFails()
    {
        if (_verifyFailKeys.Count == 0) return;
        for (var i = FailItems.Count - 1; i >= 0; i--)
        {
            var key = $"{FailItems[i].Title}|{FailItems[i].Detail}";
            if (!_verifyFailKeys.Contains(key)) continue;
            _failIndex.Remove(key);
            FailItems.RemoveAt(i);
        }
        _verifyFailKeys.Clear();
    }

    /// <summary>
    /// ★ A.5（P1-4）★ 复位「上一次运行」的实时投影：失败清单（含去重索引）、实时文件流、
    /// 当前对象 / 当前文件 / 停滞提示、速率与 ETA。
    ///
    /// Run / Resume / Repair 开跑前与"换任务"（新建 / 载入另一个任务）前都必须调用：
    /// 否则上一轮的条目会继续冒充"本轮的真实状态"。
    /// **复位 ≠ 清空事实**：收尾时（<c>RunTransferCoreAsync</c> 的 finally）会由回执（Receipt，唯一权威）
    /// 重建行状态与失败清单，不留下空白。
    /// </summary>
    private void ResetRunProjection()
    {
        ClearFailItems();
        // ★ 真机问题 2 ★ 新一次运行 = 上一轮的检查结论作废（否则状态行会沿用上一轮的「检查通过 · N 条提醒」）。
        //   注意：预检在本方法之后才跑，跑完会重新写入，所以这里清空不会丢失本轮结论。
        SetCheckSummary(string.Empty, 0);
        // ★ 真机问题 1 ★ 新一次运行 = 上一条传输的字节高水位作废（否则本轮从 0 开始会被当成"没有增长"）。
        _transferBytesHighWater = 0;
        LiveFiles.Clear();
        CurrentFileText = "—";
        CurrentObjectPath = string.Empty;
        CurrentObjectDetail = string.Empty;
        StallHintText = string.Empty;
        StallSeconds = 0;
        EngineSpeedText = "—";
        EtaText = "—";
        _lastCompletedCount = -1;
        // ★ v0.5.4 Preview.2 真机修复（2026-10-16，用户任务令 §五）★
        //   新一次运行 / 换任务 = 上一轮的"可信完成"结论立即作废。
        //   否则上一轮真完成的 true 会残留到本轮：用户在 Step2 点「开始迁移」被送到 Step3 后
        //   可能被旧结论直接推到 Step4（而本轮才刚开始跑）。拿不到证据就不许说完成。
        TrustworthyCompletion = false;

        // ★ P0-04 ★ 新一次运行（Run / Resume / Repair）或换任务 = **解除收尾封版**：
        //   否则上一轮的终局结论会把本轮的进度显示位锁死。同时清掉上一轮的"复验需注意"与过期计数。
        _progressSealed = false;
        ProgressNeedsAttention = false;
        _postFinishSnapshotsIgnored = 0;

        // ★ A.5（P1-5）★ 上一轮遗留的待刷新数据与丢弃计数一并作废：
        //   否则它们会冒充"本轮的真实状态"，而丢弃计数更是会跨轮累加成不诚实的数字。
        _batcher.Clear();
        _shownOmittedFiles = 0;
        _shownOmittedLogs = 0;
        _omittedHintLine = null;
        _flushCount = 0;

        // ★ D-01 修复（R2 · 2026-10-17）★ 上一轮的"有无可信中途来源"结论同样必须作废。
        //
        //   缺陷（接手审计 A 批 · D）：本方法复位了 _lastCompletedCount / TrustworthyCompletion /
        //   _progressSealed / ProgressNeedsAttention / _postFinishSnapshotsIgnored / _batcher 与全部
        //   丢弃计数，**唯独漏了 _hasTrustedInflightSource**。而 SyncInflightSourceFlag() 的第一句是
        //   `if (_hasTrustedInflightSource == has) return;`（本文件 :1299）——
        //   于是新一次运行的首帧如果算出的 has 恰好等于上一轮残留值（典型：上一轮失败停在"有来源"，
        //   本轮开头尚无数据、算出来也是 false 之外的同一值），这个派生位就会被短路而不发通知，
        //   Step3 的状态句会短暂沿用上一轮的活动结论 —— 正是"拿不到证据却说有活动"的反面。
        //
        //   复位点必须与 _lastTruth 的作废同源：本方法由 RunTransferCoreAsync 在开跑前调用，
        //   而 _lastTruth 在下一帧 ApplySnapshot 里被覆盖。这里直接置 false（"本刻没有已确认来源"），
        //   不 Raise —— 该派生位的通知纪律是"只在真正翻转时发一次"，由下一次
        //   SyncInflightSourceFlag() 决定是否需要通知；在这里额外 Raise 反而会多一次无用刷新。
        _hasTrustedInflightSource = false;
    }

    /// <summary>
    /// ★ A.5（P1-4）★ 复位「上一次验证」绑定的量：验证结论文案、内容级抽样数
    /// （<see cref="HasContentLevelEvidence"/> 随之派生为 false）、验证报告存在标志。
    /// 换任务（新建 / 载入另一个任务）时必须调用——上一个任务的结论绝不能留给下一个任务看。
    /// </summary>
    private void ResetVerifyProjection()
    {
        ClearVerifyDerivedFails();
        LastVerifyResultText = string.Empty;
        LastVerifyHintText = string.Empty;
        TotalHashSampled = 0;
        _hasVerifyReport = false;
    }

    /// <summary>
    /// ★ A.5（P1-4）★ 换任务（新建 / 载入另一个任务）时的整体复位：
    /// 实时投影 + 验证投影 + **上一次 Job 的日志投影**（LogLines 属于上一个任务，不能接着用）。
    /// </summary>
    private void ResetForJobSwitch()
    {
        // ★ A.5（D2/P1-5）★ 顺序纪律：**先停节拍**（不再有新的批量应用进来），
        //   再清缓冲（上一轮的待刷新数据与丢弃计数作废），最后才清集合。
        //   若反过来，一个在途的 Tick 可能把上一轮的数据写进**已经清空**的新任务界面。
        StopFlushTimer();
        _batcher.Clear();
        _shownOmittedFiles = 0;
        _shownOmittedLogs = 0;
        _omittedHintLine = null;

        ResetRunProjection();
        ResetVerifyProjection();
        LogLines.Clear();
        FailedObjects = 0;
        IsRepairing = false;
        // ★ FIX BATCH 2 ★ 换成新任务 ⇒ 上一个任务的暂停真值必须整体清空（否则新任务界面上会残留
        //   "已暂停 / 暂停失败"这类属于**上一个任务**的结论）。
        _pauseRequestWritten = false;
        _pauseRetryAfterUtc = null;
        ApplyPauseTruth(PauseState.None, null, null, PauseOutcomeKind.None, null);
        _lastCompletedCount = -1;
    }

    /// <summary>
    /// 失败清单去重追加（同一位置只留一条）。
    ///
    /// ★ A.5（D1）★ **容量上限 + 诚实丢弃计数**：
    ///   · 改前 <see cref="FailItems"/> 与 <see cref="_failIndex"/> **完全没有上限** ——
    ///     <see cref="OnOutputLine"/> 对每行含「错误」/「ERROR」的引擎行都调本方法，
    ///     大规模失败（目标盘满 / 杀软拦截）会产生**百万级** SessionFailItem + 无界 HashSet；
    ///   · 现在：清单超过 <see cref="FailItemCapacity"/> 后只计数不再新增，且**如实显示**
    ///     「…另有 N 条失败记录未在显示窗口内列出」（N 来自真实计数，绝不假装完整）；
    ///   · 去重索引本身也有增长闸门 <see cref="FailIndexCapacity"/>：达上限后不再新增键。
    ///     此时**必须按"已显示过"返回**（不再累加丢弃计数），否则同一条会重复把计数刷爆 ——
    ///     那才是"虚报"。这条口径写在这里，避免后人误改成"未命中就计数"。
    ///
    /// 从未被容量挤掉的条目其索引仍然保留（含被丢弃的），所以"丢弃计数"始终是
    /// **不同的失败条目数**，而不是"重复出现次数"。
    /// </summary>
    private void AddFail(string title, string detail, bool objectLevel = false, bool verifyDerived = false, string? fullText = null)
    {
        // ★ 线程纪律（场景 H 根因修复 / 2026-10-06）★
        //   本方法会改 FailItems（ObservableCollection）、_failIndex、_failDropped —— 三者都只能在 UI 线程动。
        //   而它确有**引擎线程**调用路径：OnOutputLine（引擎逐行输出的错误行）与
        //   UiThrottleUnavailable 时 ApplyOutputLine 的直通分支。
        //   缺陷实录：WinUI 上「跨线程改 ObservableCollection」在 FailItems.Add 处抛 COMException，
        //   异常沿 OutputLine 多播委托链冒泡，被 RobocopyRunner 的
        //   `try { OutputLine?.Invoke(trimmed); } catch { }` 静默吞掉 ⇒ 排在「转发 UI」之后的
        //   OnRunnerErrorLine **永不执行** ⇒ 盘满熔断（T07）与两条回冲路径全部失效 ⇒
        //   真机出现 completedBytes=42 GiB / percent=99.9% / 对象 0/N 的永久虚报（场景 H）。
        //   修复：统一投递到 UI 队列。Post 在已是 UI 线程时**直通**（零开销），不改变任何既有语义。
        //   注：错误行是低频流（非错误行不入失败清单），且重复行会在下面的 _failIndex 处立即返回，
        //   故此投递不构成 P1-5 关注的「每行一个工作项」问题（A8 断言只约束 OnOutputLine 本体，
        //   本方法的引擎线程投递不会在 OnOutputLine 里产生任何 Post 文本）。
        if (_queue is not null && !_queue.HasThreadAccess)
        {
            Post(() => AddFail(title, detail, objectLevel, verifyDerived, fullText));
            return;
        }

        var key = $"{title}|{detail}";

        if (_failIndex.Contains(key)) return;          // 已出现过（含"已显示"与"因超限被丢弃"）

        if (_failIndex.Count >= FailIndexCapacity) return;   // 索引增长闸门：视为已显示过，不虚报
        _failIndex.Add(key);

        if (FailItems.Count >= FailItemCapacity)
        {
            _failDropped++;                            // 真实累计：这条确实因容量上限没能显示
            if (verifyDerived) _verifyFailKeys.Add(key);
            return;
        }

        if (verifyDerived) _verifyFailKeys.Add(key);
        FailItems.Add(new SessionFailItem(title, detail, objectLevel, fullText));

        // ★ 线程纪律 ★ 这里**故意不**直接刷新「…另有 N 条」提示行：
        //   本方法可能从**引擎线程**调用（OnOutputLine / OnTransferNotice 路径），而提示行是
        //   LogLines 集合里的一行 —— 跨线程改 ObservableCollection 违反硬约束，而且 AddFail
        //   在"引擎输出行"路径上每次调用都会真的发生。
        //   提示行的刷新统一由 UI 线程负责：① 节拍 Tick 的 ApplyBatch（每 50ms）；
        //   ② 立即通道（PostImmediate → FlushPendingNow → ApplyBatch）。
        //   两者都会带 `_failDropped` 的真实值，所以提示既不会漏，也不会在错误线程上出现。
    }

    // ────────────────────────── 内部：引擎事件 / 线程纪律 ──────────────────────────

    // ★ A.5（P1-5）：引擎输出行是**三类高频流之一** ⇒ 入批量缓冲，由 50ms 节拍合并上屏。
    //   每行输出只产生 0 个工作项（旧写法是 2 个：本方法的 Post + Log 的 Post）。
    //   注意：`isError` 的行**允许随批量在 ≤50ms 内出现**（通用失败通知仍走立即通道）——
    //   理由是那可一次出现上千条，"每条立即"等于把洪峰搬回 UI 线程；口径详见
    //   docs\A5-节流与竞态修复设计.md §2.3 第 4 条。
    private void OnOutputLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (UiThrottleUnavailable) { ApplyOutputLine(line); return; }
        var isError = IsEngineErrorLine(line);
        _batcher.EnqueueLog(isError ? UiLogLevel.Error : UiLogLevel.Info, "引擎：" + line);
        if (isError) AddFail("引擎输出", line);   // 失败清单去重索引本身即闸门；不额外 Post
    }

    /// <summary>引擎输出行是否被判为错误行（唯一判据，改口径只改这里）。</summary>
    private static bool IsEngineErrorLine(string line) =>
        line.Contains("错误", StringComparison.Ordinal) || line.Contains("ERROR", StringComparison.OrdinalIgnoreCase);

    /// <summary>应用一条引擎输出行（UI 线程；节流关闭时的直通路径与批量路径共用）。</summary>
    private void ApplyOutputLine(string line)
    {
        var isError = IsEngineErrorLine(line);
        if (isError) AddFail("引擎输出", line);
        AppendLogLine(isError ? "ERROR" : "INFO", "引擎：" + line);
    }

    // ★ A.5（P1-5）：文件流是三类高频流之一 ⇒ 入批量缓冲。
    //   每文件只产生 0 个工作项（旧写法是 1 个）；「最近一个文件」在缓冲里无条件覆盖，
    //   即使它的行已被 200 容量挤掉也不会让界面停在更早的值上。
    private void OnFileCopied(string path, long bytes)
    {
        var text = $"{Format.Bytes(bytes)}　{path}";
        if (UiThrottleUnavailable) { ApplyFileCopied(text); return; }
        _batcher.EnqueueFile(text);
    }

    /// <summary>应用一个"已复制的文件"（UI 线程；直通与批量路径共用）。</summary>
    private void ApplyFileCopied(string text)
    {
        CurrentFileText = text;
        LiveFiles.Add(text);
        while (LiveFiles.Count > LiveFileCapacity) LiveFiles.RemoveAt(0);
    }

    // ★ A.5（P1-5）：引擎提示是**单发失败通知** ⇒ 走立即通道（不可被节流延迟的量）。
    private void OnTransferNotice(string notice) => PostImmediate(() => AddFail("引擎提示", notice));

    /// <summary>跨线程 UI 更新唯一入口（硬约束：必须 DispatcherQueue.TryEnqueue）。</summary>
    private void Post(Action action)
    {
        if (_queue is null || _queue.HasThreadAccess) { action(); return; }
        if (!_queue.TryEnqueue(() => action())) { /* 队列已关（关窗期）：丢弃这次 UI 更新，不影响业务 */ }
    }

    /// <summary>
    /// ★ A.5（P1-5）立即通道（唯一入口）★：**先排空缓冲，再应用**。
    ///
    /// 为什么必须先 Flush：批量行（引擎逐行输出）与立即行（"阶段结束"/暂停/停止/失败通知）
    /// 若不做这个顺序保证，就会出现"动作行先上屏、它之前的引擎行后上屏"的顺序颠倒，
    /// 或者最后一批引擎日志**永远不上屏**（R1 尾边丢失）。
    ///
    /// 这里天然**不会递归**：FlushPendingNow 内部只做批量应用，绝不调用 PostImmediate。
    /// </summary>
    private void PostImmediate(Action apply) => Post(() => { FlushPendingNow(); apply(); });

    /// <summary>
    /// 追加一条**真实**日志行（Step4「实时日志」面板的唯一写入入口）。
    /// level 只允许 INFO / WARN / ERROR；文本一律来自真实事件或异常消息，**绝不写示例句**。
    ///
    /// ★ A.5（P1-5）★ 走 <see cref="PostImmediate"/>（先 Flush 再应用）：调用点约 30 处一行未改，
    ///   自动获得"批量引擎行在前、动作结论在后"的顺序语义。低频路径，代价可忽略。
    /// </summary>
    private void Log(string level, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        PostImmediate(() => AppendLogLine(level, text));
    }

    /// <summary>应用一条日志行到集合（UI 线程；维持既有 200 上限）。</summary>
    private void AppendLogLine(string level, string text)
    {
        LogLines.Add(new SessionLogLine(level, text));
        while (LogLines.Count > LogLineCapacity) LogLines.RemoveAt(0);
    }

    // ────────────────────────── A.5（P1-5）：Drain / 节拍 / 生命周期 ──────────────────────────

    /// <summary>
    /// 排空缓冲并立即应用到 UI（**只在 UI 线程调用**）。
    ///
    /// 纪律：<see cref="UiBatchBuffer{TSnapshot}.Drain"/> 锁内只做 O(1) 引用交换；
    /// 本方法在 Drain 返回后（已出锁）才碰 ObservableCollection。
    /// </summary>
    private void FlushPendingNow()
    {
        if (UiThrottleUnavailable) return;
        ApplyBatch(_batcher.Drain());
    }

    /// <summary>把一批缓冲数据应用到 UI 集合（UI 线程；Drain 已出锁）。</summary>
    private void ApplyBatch(UiBatch<ProgressSnapshot> batch)
    {
        if (batch.IsEmpty && batch.DroppedLogs == _shownOmittedLogs && batch.DroppedFiles == _shownOmittedFiles)
            return;   // 空批且丢弃计数没变 ⇒ 一个集合事件都不产生（20Hz 空转零开销）

        foreach (var f in batch.Files) ApplyFileCopied(f);

        // 「最近一个文件」必须被应用：即使它的行已被容量挤掉（否则界面停在更早的值上）。
        if (batch.LatestFileText is not null) CurrentFileText = batch.LatestFileText;

        foreach (var e in batch.Logs)
            AppendLogLine(e.Level switch
            {
                UiLogLevel.Error => "ERROR",
                UiLogLevel.Warn => "WARN",
                _ => "INFO",
            }, e.Text);

        EnsureOmittedHints(batch.DroppedFiles, batch.DroppedLogs);

        if (batch.HasSnapshot && batch.Snapshot is not null) ApplySnapshot(batch.Snapshot);
    }

    /// <summary>
    /// 节拍回调（UI 线程，每 50ms 一次）。
    /// 返回 true = 还有待处理数据（继续滴答）；false = 薄泵自行 Stop。
    ///
    /// **幽灵刷新防线 1**：任务已结束且无残留数据 ⇒ 自行停止，并且**不写任何 UI**。
    /// </summary>
    private bool DrainOnTick()
    {
        // ★ P0-06(1) ★ 先打心跳：只要这一拍真的在 UI 线程上跑到了，就是"界面还活着"的证据。
        //   放在任何 return 之前——节拍自行停止（无待处理数据）时那最后一拍也必须算数。
        _uiStallWatchdog.Heartbeat("tick");
        if (!IsRunning && !_batcher.HasPending) return false;
        var batch = _batcher.Drain();
        ApplyBatch(batch);
        _flushCount++;
        // 诊断（PCMIG_UI_FLUSH_TRACE=1）：只传纯 BCL 标量；写盘细节在 UiFlushTrace（可链入、零 WinUI 依赖）。
        UiFlushTrace.Write($"flush #{_flushCount} batch={batch.Files.Count + batch.Logs.Count} " +
                           $"droppedFiles={batch.DroppedFiles} droppedLogs={batch.DroppedLogs}");
        return IsRunning || _batcher.HasPending;
    }

    /// <summary>开始节拍（幂等）。只在"运行开始"与"恢复运行"时调用。</summary>
    private void StartFlushTimer()
    {
        if (UiThrottleUnavailable) return;
        if (_uiRefreshStopped) return;   // 已走关闭路径：绝不让节拍复活（幽灵刷新防线 4）
        _flushPump ??= CreateFlushPump(DrainOnTick);
        _flushPump.Start();
        // ★ P0-06(1) ★ 节拍存活 = "UI 线程承诺每 50ms 响一次"，此时才允许判"未响应"。
        _uiStallWatchdog.Start();
    }

    /// <summary>
    /// 停止节拍并断开回调（幂等、绝不外抛）。**唯一的停止点**：<see cref="CleanupRun"/>、
    /// 任务切换（<see cref="ResetForJobSwitch"/>）与窗口关闭（<see cref="StopUiRefresh"/>）都走这里。
    /// </summary>
    private void StopFlushTimer()
    {
        // ★ P0-06(1) ★ 节拍停了就不再判"未响应"（这里没有心跳不是卡住，是没人要求滴答）；
        //   已记录的卡住事实一律保留，供报告与诊断读取。
        _uiStallWatchdog.Stop();
        var pump = _flushPump;
        if (pump is null) return;
        try { pump.Stop(); } catch { /* 关闭期异常不得外溢 */ }
        // ★ C-C08-1 修复（真机实证，2026-10-04）★ 停泵之后必须**丢弃实例**：
        //   生产泵 DispatcherQueueUiFlushPump.Stop() 是**终态**（_stopped=true 后 Start() 直接 return，
        //   见 UiFlushPump.cs:75-108），而本字段是 `??=` 复用的。旧实现只调 Stop 不清字段，
        //   于是同会话「停止 → 立即恢复」时 run-2 的 StartFlushTimer 拿到的是同一个**死泵**：
        //   快照只进 _batcher 的最新值槽位、**再没有任何节拍来排空**，底栏进度就冻结在
        //   「0.0% / 0 B」上，直到某次显式 FlushPendingNow（点停止/收尾）才跳到真实值
        //   ——真机 C08 reg2/diag2 在已传 19.59 GB(=40.1%) 时界面仍显示 0 B，属真值分裂。
        //   关闭路径不受影响：StopUiRefresh 末尾置 _uiRefreshStopped=true，
        //   StartFlushTimer 的第一道闸门就是它，绝不让节拍在关窗后复活。
        _flushPump = null;
    }

    /// <summary>
    /// ★ A.5（D2）★ 关闭路径的公开出口：停止一切 UI 刷新节拍。
    ///
    /// 为什么必须有：<c>DispatcherQueueTimer</c> 是 thread-pool 支撑的 WinRT 对象。进程退出时
    /// Windows App SDK 会在 <c>Microsoft.UI.Xaml.dll</c> 的
    /// <c>DllMain(DLL_PROCESS_DETACH) → DeinitializeDll → ThreadPoolService::ReleaseFactories</c>
    /// 路径上释放其缓存的线程池激活工厂；若这些对象仍被持有会命中已卸载模块而崩溃
    /// （实测 <c>Microsoft.UI.Xaml.dll+0x7F9880</c> 读 NULL <c>+0x58</c>，7/7 转储指纹一致）。
    /// 依据 <c>MotionState.cs</c> 的 Dispose 段与 <c>MainWindow.xaml.cs</c> 的关闭路由注释。
    ///
    /// **必须在 <c>Environment.Exit(0)</c> 之前调用**（<c>MainWindow.ShutdownAndExit</c> 里已接线，
    /// 并有静态契约断言锁住，防"C2 那种设计了却从不接线"）。
    ///
    /// ★ A.5（P2-6）★ 本方法是**唯一**的 UI 刷新/计时器停止出口：UI 节拍（P1-5）与
    /// 未完成任务探测的去抖计时器（P2-6）都折在这里停 —— **不存在** <c>StopUnfinishedProbeTimer()</c>
    /// 之类的第二个公开出口。
    /// 幂等、绝不外抛；只停刷新，不改任何业务状态、不写存档。
    /// </summary>
    public void StopUiRefresh()
    {
        StopFlushTimer();

        // ★ A.5（P2-6）★ 未完成任务探测的去抖计时器**折进同一个出口**（设计 §3.4 明令：
        //   与 UI 节拍共用 StopUiRefresh，**禁止**再开一个 StopUnfinishedProbeTimer() 之类的公开出口
        //   —— 两个出口就是"C2 同款：机制两套、接线一套"的温床）。
        //   它同样是 DispatcherQueueTimer（thread-pool 支撑的 WinRT 对象），必须在 Exit 之前停掉。
        StopProbeDebouncePump();

        // 关闭后不再让任何**在途探测**把状态写进正在拆卸的界面（幽灵刷新防线，与 D2 同源）。
        try { _probeCts?.Cancel(); } catch { /* 关闭期异常不得外溢 */ }

        // 停止后不再让任何节拍复活（包括迟到的 StartFlushTimer / RequestUnfinishedProbe 调用）。
        _uiRefreshStopped = true;
    }

    private bool _uiRefreshStopped;

    // ────────────────────────── A.5（D1）：失败清单容量 + 诚实丢弃计数 ──────────────────────────

    /// <summary>失败/不一致清单的容量上限（复用日志窗口口径，避免两套数字）。</summary>
    public const int FailItemCapacity = LogLineCapacity;

    /// <summary>去重索引自身的增长闸门（超出后不再新增键；重复项按"已显示过"处理，防止无界增长）。</summary>
    private const int FailIndexCapacity = 50_000;

    /// <summary>
    /// 「…另有 N 条未显示」提示行在日志里的**唯一前缀**（方案 P：零 XAML 改动，
    /// 把提示作为**真实一行**插进既有 Step4 实时日志面板）。
    /// </summary>
    public const string OmittedHintPrefix = "…另有 ";

    /// <summary>因容量上限而未能进入失败清单的条目数（**真实累计**，不虚报不漏报）。</summary>
    private int _failDropped;

    /// <summary>已在提示行里向用户展示过的计数（只在**计数变化**时更新那一行）。</summary>
    private int _shownOmittedFails;
    private int _shownOmittedLogs;
    private int _shownOmittedFiles;
    private SessionLogLine? _omittedHintLine;

    private void EnsureOmittedHints(int droppedFiles, int droppedLogs)
    {
        UpdateOmittedHint(ref _shownOmittedFails, _failDropped, "条失败记录",
            "完整失败清单见任务目录的报告/日志");
        UpdateOmittedHint(ref _shownOmittedLogs, droppedLogs, "行较早输出",
            $"完整日志见任务目录：{Ctx?.JobDir ?? AppLogDir}");
        UpdateOmittedHint(ref _shownOmittedFiles, droppedFiles, "个较早文件",
            "列表只保留最近 " + LiveFileCapacity + " 个");
    }

    /// <summary>
    /// 把「…另有 N 条未显示」写成日志面板里的**真实一行**（计数为 0 时不显示任何行）。
    /// 计数变化时**先撤掉上一行再插新行**：文案里的 N 必须与真实计数一致（不许显示静态/陈旧数字）。
    ///
    /// ★ 线程纪律 ★ 本方法会改 <see cref="LogLines"/>，因此**只允许在 UI 线程调用**：
    /// 调用点只有 <see cref="ApplyBatch"/>（由节拍 Tick 或立即通道 FlushPendingNow 进入）
    /// 与 <see cref="ClearFailItems"/>（复位路径）。绝不可从引擎线程调用。
    /// </summary>
    private void UpdateOmittedHint(ref int shown, int actual, string unit, string tail)
    {
        if (shown == actual) return;
        shown = actual;

        if (_omittedHintLine is not null)
        {
            var idx = LogLines.IndexOf(_omittedHintLine);
            if (idx >= 0) LogLines.RemoveAt(idx);
            _omittedHintLine = null;
        }

        if (actual <= 0) return;

        var line = new SessionLogLine("WARN",
            $"{OmittedHintPrefix}{actual} {unit}未在显示窗口内列出（面板窗口有限）；{tail}。");
        _omittedHintLine = line;
        LogLines.Add(line);
        while (LogLines.Count > LogLineCapacity) LogLines.RemoveAt(0);
    }

    /// <summary>供页面层写入一条真实日志行（与内部同一入口，不新建第二套日志集合）。</summary>
    public void AppendLog(string text) => Log("INFO", text);

    /// <summary>供页面层按级别写入一条真实日志行（level 只允许 INFO / WARN / ERROR）。</summary>
    public void AppendLog(string level, string text) => Log(level, text);

    /// <summary>广播全部派生属性（状态类属性变化后调用；Percent 这类高频值不在此列）。</summary>
    private void RaiseDerived()
    {
        Raise(nameof(PhaseText));
        Raise(nameof(CanStart));
        Raise(nameof(CanResume));
        Raise(nameof(CanRepair));
        Raise(nameof(CanPause));
        Raise(nameof(CanStop));
        // ★ FIX BATCH 2 ★ 暂停真值的派生位（按钮矩阵与状态句都从这里广播；数字区不在此列）
        Raise(nameof(IsPaused));
        Raise(nameof(IsPausing));
        Raise(nameof(IsPauseFailed));
        Raise(nameof(PauseButtonText));
        Raise(nameof(PauseStateText));
        Raise(nameof(EnginePauseState));
        Raise(nameof(EnginePauseOutcome));
        Raise(nameof(PauseRequestedAt));
        Raise(nameof(PauseAchievedAt));
        Raise(nameof(PauseFailureReason));
        Raise(nameof(CanVerifyNow));
        Raise(nameof(VerifyBlockedReason));
        Raise(nameof(HasJob));
        Raise(nameof(HasPlan));
        Raise(nameof(ThreadsForRun));
        Raise(nameof(JobIdText));
    }

    /// <summary>准备新任务前复位"计划"相关状态（不动作任何存档）。</summary>
    private void HasPlanReset()
    {
        Objects.Clear();
        HasPlanRaise();
    }

    private void HasPlanRaise()
    {
        Raise(nameof(HasPlan));
        Raise(nameof(CanStart));
        Raise(nameof(CanVerifyNow));
        Raise(nameof(VerifyBlockedReason));
    }
#if DEBUG
    // ═══════════════════════════════════════════════════════════════════════════
    // ★ 2026-10-22 · 开发者 UI Harness 缝合点（★ 整块仅在 Debug 编译单元存在 ★）★
    //
    // 存在理由（Preview.6 本机 UI 实测发现的问题）：
    //   Harness 直接 `SyncTransferActivity(true)` 把灯点成"正在传输"，但真实呈现节拍每
    //   16 ms 会用 `HasLiveTransferActivity` 重算一次并把它打回 false —— 因为本机既没有
    //   真实网卡吞吐、也没有可信字节增长。上轮日志实证：
    //       T4 step2 sync -> TransferActive=True State=Transferring
    //       probe [T4-a]  SourceState=Connected TransferActive=False   ← 800 ms 后被打回
    //
    // 正确做法不是"压住刷新"，而是**补齐判据需要的真实输入**：
    //   · 网卡吞吐：把生产用的默认探针换成 Debug 测试数据源（计数器按真实时间线性增长）；
    //     采样、EMA、下限比较全部仍由**正式代码**计算，判据 `HasLiveTransferActivity` 一字未改。
    //   · 阶段/暂停：把引擎真值推到它本来只能从快照里读到的字段（`_enginePauseState` /
    //     `ApplyPauseTruth`），走的是**正式**路径，连"暂停即熄灯"的同步逻辑也照样触发。
    //
    // 边界：这些方法不存在于 Release；不接受外部参数控制判据；不产生任何伪造的进度、
    //       字节或回执事实；只搬运"输入条件"。
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>【仅 Debug】把网卡吞吐采样器的探针换成测试数据源（幂等；采样器不存在则先启动）。</summary>
    internal string HarnessInstallThroughputProbe(PCMig.Core.Network.INetworkInterfaceProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var diag = $"Host='{Host}' before={(_networkThroughput is null ? "null" : "exists")}";
        EnsureNetworkThroughput();
        diag += $" after={(_networkThroughput is null ? "null" : "exists")}";
        if (_networkThroughput is null)
            diag += " REASON=Host 为空，生产逻辑不会创建采样器";
        else
        {
            _networkThroughput.ReplaceProbe(probe);
            var sel = _networkThroughput.Selection;
            diag += $" probeIdx={sel.InterfaceIndex} usable={sel.IsUsable} run={_networkThroughput.IsRunning}";
        }
        return diag;
    }

    /// <summary>【仅 Debug】把网卡吞吐采样器还回生产默认探针（真实网卡）。</summary>
    internal void HarnessRestoreThroughputProbe()
        => _networkThroughput?.ReplaceProbe(null);

    /// <summary>【仅 Debug】立即确保网卡吞吐观测已启动（生产入口在"IsConnected 变化"事件里）。</summary>
    internal void HarnessEnsureThroughput() => EnsureNetworkThroughput();

    /// <summary>
    /// 【仅 Debug】采样器状态诊断串（给实测日志用；只读，不改任何状态）。
    /// </summary>
    internal string HarnessThroughputDiagnostic()
    {
        var s = _networkThroughput;
        if (s is null) return "null(未启动)";
        var sel = s.Selection;
        return $"run={s.IsRunning} idx={sel.InterfaceIndex} usable={sel.IsUsable} paused={s.Snapshot.Paused} state={s.Snapshot.State}";
    }

    /// <summary>【仅 Debug】施加引擎阶段真值（正式路径 <c>ApplySnapshot</c> 里是 <c>Phase = s.Phase;</c>）。</summary>
    internal void HarnessSetPhase(JobPhase phase)
    {
        Phase = phase;
        Raise(nameof(Phase));
    }

    /// <summary>
    /// 【仅 Debug】施加引擎暂停真值。走**正式**方法 <see cref="ApplyPauseTruth"/>，
    /// 于是 "暂停 ⇒ 立刻熄灯" 的同步逻辑与 UI 状态全部照常执行。
    /// </summary>
    internal void HarnessSetPauseTruth(PauseState state)
    {
        var now = state == PauseState.None ? (DateTime?)null : DateTime.UtcNow;
        ApplyPauseTruth(state, now, now, PauseOutcomeKind.None, null);
    }
#endif
}
/// <summary>
/// 「未完成任务」探测的**去抖计时器抽象**（P2-6）。
///
/// 为什么接口住在这个文件里、而实现住别处（与 P1-5 的 <c>IUiFlushPump</c>/<c>DispatcherQueueUiFlushPump</c> 同款分层）：
///   · 本文件被**链入** tests\PCMig.Core.Tests 编译，而该测试项目**没有 DispatcherQueueTimer 替身**
///     ⇒ 被链入的文件里不得出现 WinUI 定时器类型名；
///   · 因此这里只放"声明里不含 WinUI 类型"的接口，真实实现放在**不链入**的
///     <c>Presentation\UiProbeDebouncePump.cs</c>，由装配层（MainWindow）注入工厂。
///
/// 语义约定：
///   · <see cref="Arm"/>：起/重置一次性去抖窗口（每次调用都把窗口从当前时刻重新计起）；
///   · <see cref="Disarm"/>：取消尚未到期的窗口（不影响已发起的探测）；
///   · <see cref="Stop"/>：**永久**停止（关闭路径专用；之后 <see cref="Arm"/> 必须无效）；
///   · 实现必须持**强引用**并在进程退出前可停（DispatcherQueueTimer 的两条既定纪律）。
/// </summary>
public interface IUnfinishedProbeDebouncePump
{
    /// <summary>起或重置去抖窗口（到期后回调装配时给的那个 <c>Action</c>）。</summary>
    void Arm();

    /// <summary>取消尚未到期的窗口（已到期/未起过 ⇒ no-op）。</summary>
    void Disarm();

    /// <summary>永久停止（关闭路径；幂等、绝不外抛）。</summary>
    void Stop();

    /// <summary>是否有一个尚未到期的窗口在计时（诊断/契约用）。</summary>
    bool IsArmed { get; }
}

/// <summary>
/// UI 侧暂停状态机（FIX BATCH 2）。
///
/// 它与引擎真值 <see cref="PauseState"/> **不是**一回事，差在"请求已发出但引擎还没表态"这一段：
///   · <see cref="Idle"/>       —— 没有暂停在进行（正常运行/未运行）。
///   · <see cref="Pausing"/>    —— 请求已写入（或引擎已受理），**尚未**确认 worker 停住 ⇒ 只能显示"正在暂停…"。
///   · <see cref="Paused"/>     —— 引擎确认 worker 已停止、暂停达成 ⇒ 才允许显示"已暂停"，Resume 才可用。
///   · <see cref="PauseFailed"/>—— 引擎在硬失败 SLA 内没能停住 ⇒ 必须显示"暂停失败，迁移仍在进行"。
///   · <see cref="Resuming"/>   —— 用户已确认恢复、请求文件已清除，正在重建传输。
///
/// 纪律：**只有引擎能把它推进到 Paused / PauseFailed**；UI 自己最多推到 Pausing（诚实地说"正在试"）。
/// </summary>
public enum PauseUiState
{
    /// <summary>没有暂停在进行。</summary>
    Idle = 0,
    /// <summary>正在暂停…（请求已发出/已受理，引擎尚未确认停住）。</summary>
    Pausing = 1,
    /// <summary>已暂停（引擎确认停住）。</summary>
    Paused = 2,
    /// <summary>暂停失败，迁移仍在进行。</summary>
    PauseFailed = 3,
    /// <summary>正在恢复…。</summary>
    Resuming = 4,
}

// ══════════════════════════════════════════════════════════════════════════════
// ★ Round-2 2026-10-05（§2 P0）★ 进度显示连续性状态机
//   （真机视频证据：24.8% 点「停止」→ 立刻 0.0%；暂停 42.9% → 恢复 24.8% → 回 42.9%）★
// ══════════════════════════════════════════════════════════════════════════════
/// <summary>
/// 进度**显示连续性**状态机 —— **纯逻辑、零 WinUI 依赖**（因此可被
/// <c>tests\PCMig.Core.Tests\PCMig.Core.Tests.csproj</c> 源码链入并直接断言行为，
/// 见 <c>ContinuationDisplayStateTests</c> / <c>ResumeProgressContinuityTests</c>）。
///
/// 【它解决什么】用户**已经看到**过的进度，在任何"可恢复的中断"（暂停 / 停止 / 恢复）期间
///   不得无解释倒退。Round-2 用户视频证据：
///   · 43.7 s 显示 24.8%（43.95 GB / 176.9 GB），鼠标在底栏「停止」，点击后 **44.0 s 立刻变成 0.0% / 0 B**；
///   · 52.0 s 才跳回 24.8%，紧接着 47.3%；54.9 s → 62.3%。
///   根因不在 UI 控件：Stop → Core 把这一轮收尾成 <c>Interrupted</c>，并从 committed 回执重建真值
///   （<c>TransferOrchestrator.RunAsync</c> 的 <c>baseBytes</c>），而停止前用户看到的分子里含 in-flight
///   已确认字节（<c>PollProgressAsync</c> 的 <c>state.CompletedBytes = truth.DisplayedTransferredBytes</c>）
///   ⇒ raw 一收尾就低于"用户看到的"；旧实现又只在 <c>Paused</c> 态记候选，并把 <c>Interrupted</c>
///   当成"解除条件" ⇒ 显示被清成 committed-only 值 = 用户看到的 0.0%。
///
/// 【三种模式】<see cref="ContinuationMode"/>：
///   · <c>Live</c>       —— 无冻结：显示完全跟随引擎 raw 真值（正常运行时、以及 raw 追平高水位之后）。
///   · <c>Frozen</c>     —— 用户暂停/停止之后冻结在高水位：显示 = max(raw, 高水位)，绝不倒退；
///                        暂停态即使 raw 已等于高水位也**保持冻结**（恢复时 raw 会被重建为更低）。
///   · <c>CatchingUp</c> —— 用户已点恢复：显示停在高水位，直到引擎 raw 追平 ⇒ 自动转 Live。
///
/// 【分层纪律（绝不可越界）】
///   · ① Committed / Durable Progress —— 权威 = Completed Receipt，决定 skip / CompletedObjects / 最终判定；
///        **绝不经过本类型**（本类型不写回执、不动 job-state、不参与 skip / verify / success，
///        也绝不把 Interrupted 对象标成 Completed）。
///   · ② Continuation Display State —— 本类型：只抬高 UI 侧显示分子与百分比，**不代表对象已完成**。
///
/// 【何时允许显示下降（都必须带原因码）】
///   job-changed（换任务 ⇒ 新任务从 0 开始）/ explicit-rollback（RetryState.RollingBack）/
///   phase-Completed|CompletedWithErrors（真结算，跟随 100%）/ truth-caught-up（raw 追平，转 Live）。
///   ★ 注意 ★ RetryState.Retrying **不是**可解释的回退：恢复后的正常 catch-up 会短暂进入 Retrying
///   （真机复验 run2 证据）。★ 注意 ★ Interrupted / Failed / AwaitingReview **不是**解除条件 ——
///   它们正是需要冻结的"可恢复中断"，这就是本轮修的 P0。
///
/// 【它同时负责】raw（引擎真值）样本间的回退检测（<see cref="LastRawRegressionBytes"/>）——
///   与冻结无关：即使从未捕获过高水位，raw 真值倒退也必须被记录（§5 / §7）。
/// </summary>
internal sealed class ContinuationDisplayState
{
    /// <summary>容差：raw 显示值下降超过这个字节数才算"回退"（避免同一真值的抖动被当成回退）。</summary>
    public const long RegressionToleranceBytes = 1;

    private long _highWaterBytes;
    private double _highWaterPercent;
    private int _highWaterCompletedObjects;
    private string? _jobId;
    private ContinuationMode _mode = ContinuationMode.Live;
    private long? _lastRawDisplayedBytes;
    private string? _lastRawJobId;

    /// <summary>是否处于冻结/追赶（true = 显示值可能高于引擎 raw；只读诊断）。</summary>
    public bool IsActive => _mode != ContinuationMode.Live;
    /// <summary>高水位字节数 = 用户已经看到过的最大进度分子（只读诊断）。</summary>
    public long HighWaterBytes => _highWaterBytes;
    /// <summary>高水位百分比（只读诊断）。</summary>
    public double HighWaterPercent => _highWaterPercent;
    /// <summary>捕获检查点时的"已完成对象数"（只读诊断：用于解释暂停文案，**不是**业务权威）。</summary>
    public int HighWaterCompletedObjects => _highWaterCompletedObjects;
    /// <summary>当前模式。</summary>
    public ContinuationMode Mode => _mode;
    /// <summary>高水位绑定的任务 id（只读诊断）。</summary>
    public string? JobId => _jobId;
    /// <summary>最近一次模式/检查点变更的原因码（诊断用）。</summary>
    public string LastReason { get; private set; } = string.Empty;
    /// <summary>最近一次解除的原因码（空 = 从未解除）。</summary>
    public string LastClearReason { get; private set; } = string.Empty;
    /// <summary>最近一次 <see cref="Apply"/> 观察到的 raw 回退字节数（0 = 未回退）。</summary>
    public long LastRawRegressionBytes { get; private set; }

    // ── Round-3 PHASE A（2026-10-05，§4.5）：无解释"前跳"检测 ──
    // 只查倒退是不够的：P0 那个 bug 的表现恰恰相反 —— 暂停瞬间 raw 从 30 GB 跳到 41.96 GB
    //   （/Z 预分配被当成已传字节），"零倒退"的旧判据反而把它判成通过。所以必须同时检测
    //   "没有可信 Worker I/O 增量支撑的大幅前跳"。
    /// <summary>前跳字节容差（避免正常抖动误报）。</summary>
    public const long ForwardLeapToleranceBytes = 1;
    /// <summary>前跳百分点阈值（Round-3 §4.5：+10 个百分点）。</summary>
    public const double ForwardLeapPercentThreshold = 10d;
    /// <summary>前跳绝对字节下限：<c>max(1 GiB, PlannedBytes * 0.10)</c> 的固定部分。</summary>
    public const long ForwardLeapFloorBytes = 1L << 30;
    /// <summary>触发"检查点以来无解释前跳"的判定比例（PlannedBytes 的 10%）。</summary>
    public const double ForwardLeapPlannedRatio = 0.10d;
    /// <summary>
    /// 时间豁免用的可解释速率上界。真机实测本机 C:→E: 为 2.7~5 GB/s，
    /// 取 8 GiB/s 作保守上界：只要"检查点以来的经过时间 × 该上界"能解释掉这段增量，
    /// 就不算无解释前跳（真机 9.0% → 34.3% / 10.4 GB / ~2.5 s 即属此类真实传输）。
    /// </summary>
    public const double ForwardLeapRateCeilingBytesPerSecond = 8d * 1024d * 1024d * 1024d;

    private long _forwardLeapBaselineBytes = -1;
    private DateTime _forwardLeapCheckpointUtc = DateTime.MinValue;

    /// <summary>最近一次判定的无解释前跳字节数（0 = 未前跳；只读诊断）。</summary>
    public long LastRawForwardLeapBytes { get; private set; }
    /// <summary>最近一次判定的无解释前跳百分点（0 = 未前跳；只读诊断）。</summary>
    public double LastRawForwardLeapPercent { get; private set; }
    /// <summary>动作边界基线（用户点 Pause/Stop 那一刻的可信分子；-1 = 未设置）。</summary>
    public long ForwardLeapBaselineBytes => _forwardLeapBaselineBytes;

    /// <summary>
    /// 在**用户动作之前**（Pause / Stop / Resume 的点击路径里）捕获显示检查点：
    /// 候选 = 用户此刻已经看到的显示分子。候选 ≤ 0 时不标记（绝不凭空造高水位）。
    /// 只升不降：更低候选不会覆盖已有高水位。返回本次是否发生变化。
    /// </summary>
    public bool CaptureCheckpoint(string? jobId, long displayedBytes, double percent, int completedObjects, string reason)
    {
        LastReason = reason;
        if (displayedBytes <= 0) return false;
        var changed = false;
        if (displayedBytes > _highWaterBytes) { _highWaterBytes = displayedBytes; changed = true; }
        if (percent > _highWaterPercent) { _highWaterPercent = percent; changed = true; }
        if (completedObjects > _highWaterCompletedObjects) { _highWaterCompletedObjects = completedObjects; changed = true; }
        if (string.IsNullOrEmpty(_jobId)) _jobId = jobId;
        if (_mode != ContinuationMode.Frozen) { _mode = ContinuationMode.Frozen; changed = true; }
        if (changed) LastClearReason = string.Empty;
        return changed;
    }

    /// <summary>
    /// 用户已点「恢复任务」⇒ 进入追赶模式：显示停在高水位，直到引擎 raw 追平。
    /// 没有高水位（≤ 0）时保持 Live（没什么可追赶，绝不凭空托底）。返回是否进入追赶。
    /// </summary>
    public bool BeginCatchUp(string? jobId)
    {
        if (_highWaterBytes <= 0) return false;
        if (string.IsNullOrEmpty(_jobId)) _jobId = jobId;
        _mode = ContinuationMode.CatchingUp;
        LastReason = "catch-up";
        return true;
    }

    /// <summary>解除连续性（原因码必填）。返回是否真的从"生效"变为"解除"。</summary>
    public bool Clear(string reason)
    {
        LastClearReason = reason;
        LastReason = reason;
        var wasActive = IsActive;
        _mode = ContinuationMode.Live;
        _highWaterBytes = 0;
        _highWaterPercent = 0;
        _highWaterCompletedObjects = 0;
        _jobId = null;
        // Round-3 PHASE A：解除连续性 ⇒ 动作边界基线也必须失效（新任务/新一轮不该继承旧基线，
        //   否则第一次采样就会被判成"前跳"）。
        _forwardLeapBaselineBytes = -1;
        _forwardLeapCheckpointUtc = DateTime.MinValue;
        LastRawForwardLeapBytes = 0;
        LastRawForwardLeapPercent = 0d;
        return wasActive;
    }

    /// <summary>把 raw 样本跟踪复位到指定任务（跨任务/重新开始时用），并清掉回退基线。</summary>
    public void ResetRawTracking(string? jobId)
    {
        _lastRawDisplayedBytes = null;
        _lastRawJobId = jobId;
        LastRawRegressionBytes = 0;
        _forwardLeapBaselineBytes = -1;
        _forwardLeapCheckpointUtc = DateTime.MinValue;
        LastRawForwardLeapBytes = 0;
        LastRawForwardLeapPercent = 0d;
    }

    /// <summary>
    /// 把引擎 raw 真值翻译成"显示用"真值：处于冻结/追赶模式且 raw 尚未追平时抬高显示分子；
    /// 命中任一解除条件则**完全跟随** raw（并回报原因码）。
    /// 规则顺序（①…⑧）刻意固定，避免"先冻结还是先解除"的歧义；详细理由见类注释。
    /// </summary>
    public ContinuationOutcome Apply(ProgressTruthSnapshot? truth, JobPhase phase, string? currentJobId)
    {
        LastRawRegressionBytes = TrackRawRegression(truth is null ? null : currentJobId, truth?.DisplayedTransferredBytes);

        if (truth is null)
            return new ContinuationOutcome(null, _mode, false, string.Empty, LastRawRegressionBytes, 0, _highWaterBytes);

        var rawDisplayed = truth.DisplayedTransferredBytes;

        // ① 换了任务 ⇒ 旧高水位对新任务毫无意义（新任务显示必须从 0 重新长）。
        if (!string.IsNullOrEmpty(_jobId) && !string.IsNullOrEmpty(currentJobId)
            && !string.Equals(_jobId, currentJobId, StringComparison.OrdinalIgnoreCase))
            return ClearAndFollow(truth, "job-changed");

        // ② 显式回滚（引擎自己说要退）⇒ 允许显示下降，但必须带原因码（§2.1 第 7 条）。
        if (truth.RetryState == RetryState.RollingBack)
            return ClearAndFollow(truth, "explicit-rollback");

        // ③ 真结算 ⇒ 跟随引擎（只有 Completed 才是 100%）。
        if (phase is JobPhase.Completed or JobPhase.CompletedWithErrors)
            return ClearAndFollow(truth, "phase-" + phase);

        // ④ 正常前进时把高水位往上推（只有 Running/Paused 才算"用户正在看到它涨"）。
        if (phase is JobPhase.Running or JobPhase.Paused && rawDisplayed > _highWaterBytes)
            PromoteHighWater(rawDisplayed, truth.Percent, currentJobId);

        // ⑤ 暂停态：冻结（即使 raw 已等于高水位也保持冻结 —— 恢复时 raw 会被重建为更低）。
        if (phase == JobPhase.Paused)
        {
            if (_mode == ContinuationMode.Live && _highWaterBytes > 0)
            {
                _mode = ContinuationMode.Frozen;
                LastReason = "paused";
            }
            return Freeze(truth, rawDisplayed);
        }

        // ⑥ 其它一切相位（Stop 后的 Interrupted / Failed / Canceled / AwaitingReview，
        //    以及尚未进入传输的 Created/Preflight/Scanning/Planned/Verifying）：一律冻结。
        //    ★ Round-2 P0 ★ 用户点「停止」后显示必须停在检查点，绝不掉回 committed-only 值；
        //    真机视频证据：43.7 s 24.8% → 点停止 → 44.0 s 0.0%（旧实现把 Interrupted 当解除条件）。
        if (phase is not (JobPhase.Running or JobPhase.Paused))
        {
            if (_mode == ContinuationMode.Live && _highWaterBytes > 0)
            {
                _mode = ContinuationMode.Frozen;
                LastReason = "phase-" + phase;
            }
            return Freeze(truth, rawDisplayed);
        }

        // ⑦ Running 且 raw 追平/超过高水位：
        //    · CatchingUp（Resume 之后的追赶）⇒ 解除，显示完全跟随引擎真值（§2.1「追平后自动解除」）。
        //    · Frozen（Pause / Stop 之后的冻结）⇒ **不解除**，高水位继续保留。
        //      ★ Round-2 真机缺陷（run F，2026-10-05 04:38:24.383 → .394）★ 旧写法对 Frozen 也解除，
        //      于是"Stop 后引擎 raw 先反超（42 GB 在本机 6 秒内传完，raw 直接到 99.9%）再被重建为 0"
        //      时高水位已经丢成 0（真机日志 highWaterBytes=0），显示就再无托底 ——
        //      正是用户视频里 24.8% → 0.0% 的路径。
        //      冻结期显示仍跟随 raw（Freeze 只抬高、不压低，见 4157 行的 lifted <= rawDisplayed 早退），
        //      所以保留高水位不会让数字"卡住不涨"，只是把托底留给之后的 raw 回落。
        //    ★ 注意 ★ RetryState.Retrying 不是解除条件：恢复后的正常 catch-up 会短暂进入 Retrying
        //    （真机复验 run2 证据：旧代码的 retry-restart 曾在 20:33:48.498 清掉 floor）。
        //    冻结的出口只有：换任务（①）／显式回滚（②）／真结算（③）／Resume 后追平（⑦ CatchingUp）。
        if (rawDisplayed >= _highWaterBytes)
        {
            if (_mode == ContinuationMode.CatchingUp)
                return ClearAndFollow(truth, "truth-caught-up");
            return new ContinuationOutcome(truth, _mode, false, string.Empty, LastRawRegressionBytes, rawDisplayed, _highWaterBytes);
        }

        // ⑧ Running 且 raw 仍低于高水位：Live ⇒ 照常跟随（说明从未冻结）；否则冻结在检查点。
        return _mode == ContinuationMode.Live
            ? new ContinuationOutcome(truth, _mode, false, string.Empty, LastRawRegressionBytes, rawDisplayed, _highWaterBytes)
            : Freeze(truth, rawDisplayed);
    }

    /// <summary>把高水位推到 raw 当前位置（只由 Running/Paused 的正常前进调用）。</summary>
    private void PromoteHighWater(long rawDisplayedBytes, double rawPercent, string? currentJobId)
    {
        _highWaterBytes = rawDisplayedBytes;
        if (rawPercent > _highWaterPercent) _highWaterPercent = rawPercent;
        if (string.IsNullOrEmpty(_jobId)) _jobId = currentJobId;
    }

    /// <summary>
    /// 冻结：显示分子取 max(raw, 高水位)。**只抬高显示，不动真值语义** ——
    /// 返回的 Effective 只供 UI 显示；LastTruth（raw）仍原样保留给诊断（§7）。
    /// </summary>
    private ContinuationOutcome Freeze(ProgressTruthSnapshot truth, long rawDisplayed)
    {
        var lifted = truth.PlannedBytes > 0 ? Math.Min(_highWaterBytes, truth.PlannedBytes) : _highWaterBytes;
        if (lifted <= rawDisplayed)
            return new ContinuationOutcome(truth, _mode, false, string.Empty, LastRawRegressionBytes, rawDisplayed, _highWaterBytes);

        var percent = truth.PlannedBytes > 0
            ? Math.Min(ProgressTruthSnapshot.RunningPercentCeiling, lifted * 100.0 / truth.PlannedBytes)
            : truth.Percent;
        if (percent < truth.Percent) percent = truth.Percent;

        var effective = truth with { DisplayedTransferredBytes = lifted, Percent = percent };
        return new ContinuationOutcome(effective, _mode, false, string.Empty, LastRawRegressionBytes, rawDisplayed, _highWaterBytes);
    }

    private ContinuationOutcome ClearAndFollow(ProgressTruthSnapshot truth, string reason)
    {
        var highWater = _highWaterBytes;
        Clear(reason);
        return new ContinuationOutcome(truth, ContinuationMode.Live, true, reason, LastRawRegressionBytes, truth.DisplayedTransferredBytes, highWater);
    }

    private long TrackRawRegression(string? jobId, long? rawDisplayed)
    {
        if (rawDisplayed is null) return 0;
        var prev = _lastRawDisplayedBytes;
        var sameJob = string.Equals(_lastRawJobId, jobId, StringComparison.Ordinal);
        _lastRawDisplayedBytes = rawDisplayed.Value;
        _lastRawJobId = jobId;
        if (prev is null || !sameJob) return 0;
        var delta = prev.Value - rawDisplayed.Value;
        return delta > RegressionToleranceBytes ? delta : 0;
    }

    /// <summary>
    /// 记录"动作边界基线"：用户点 Pause / Stop 那一刻**已经看到**的可信分子。
    /// <para>
    /// 之后引擎 raw 真值若在没有可信 Worker I/O 增量支撑的情况下比它高出一大截，
    /// 就是 P0 那个 <c>/Z</c> 预分配污染的签名（暂停前 30 GB → 暂停后 41.96 GB）。
    /// </para>
    /// </summary>
    public void MarkActionCheckpoint(long displayedBytes)
    {
        _forwardLeapBaselineBytes = displayedBytes > 0 ? displayedBytes : 0;
        _forwardLeapCheckpointUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// 判定"检查点以来的无解释前跳"。判定条件（Round-3 §4.5，两条满足其一）：
    /// <c>leap &gt;= max(1 GiB, PlannedBytes * 10%)</c> 或 <c>leapPercent &gt;= 10</c>。
    /// <para>
    /// <paramref name="trustedIoDeltaBytes"/> = 自动作边界以来**引擎自己认可**的可信 I/O 增量；
    /// 调用方拿不到证据时必须传 0（宁可报出来让人核对，也不静默放过 —— 这就是上一轮"零倒退⇒通过"
    /// 判据的教训）。返回 true 表示本次判定为无解释前跳。
    /// </para>
    /// <para>
    /// ★ 时间豁免（Round-3 PHASE A 真机校准，已写入 PMML R24 补充）★
    /// 真机实测：暂停前 9.0% / 3.97 GB → 暂停后 34.3% / 14.4 GB，跨度约 2.5 秒。
    /// 这 **是真实传输**（本机 C:→E: 实测 2.7~5 GB/s，2.5 秒传 10.4 GB 完全合理），不是污染。
    /// 因此"比动作检查点高 10 个百分点"绝不能单独定罪：先要扣掉"检查点之后经过的时间里，
    /// 以本机可解释速率上界最多能传多少"，只有**扣不掉的那部分**才算无解释跳变。
    /// 而真正的污染签名（72% → 99.9% / 41.96 GB）发生在一瞬间，任何速率都解释不了 ⇒ 仍会被抓住。
    /// </para>
    /// </summary>
    public bool TrackRawForwardLeap(long? rawDisplayed, long plannedBytes, long trustedIoDeltaBytes)
    {
        if (rawDisplayed is null || _forwardLeapBaselineBytes < 0)
        {
            LastRawForwardLeapBytes = 0;
            LastRawForwardLeapPercent = 0d;
            return false;
        }
        var credited = Math.Max(0L, trustedIoDeltaBytes);
        var leap = rawDisplayed.Value - _forwardLeapBaselineBytes - credited;
        if (leap <= ForwardLeapToleranceBytes)
        {
            LastRawForwardLeapBytes = 0;
            LastRawForwardLeapPercent = 0d;
            return false;
        }
        if (_forwardLeapCheckpointUtc != DateTime.MinValue)
        {
            var elapsedSeconds = (DateTime.UtcNow - _forwardLeapCheckpointUtc).TotalSeconds;
            if (elapsedSeconds > 0d)
            {
                var maxPlausible = (long)(elapsedSeconds * ForwardLeapRateCeilingBytesPerSecond);
                if (leap <= maxPlausible)
                {
                    LastRawForwardLeapBytes = 0;
                    LastRawForwardLeapPercent = 0d;
                    return false;
                }
                leap -= maxPlausible;
            }
        }
        var percent = plannedBytes > 0 ? leap * 100d / plannedBytes : 0d;
        var floor = Math.Max(ForwardLeapFloorBytes, (long)(plannedBytes * ForwardLeapPlannedRatio));
        var suspicious = leap >= floor || percent >= ForwardLeapPercentThreshold;
        LastRawForwardLeapBytes = suspicious ? leap : 0;
        LastRawForwardLeapPercent = suspicious ? percent : 0d;
        return suspicious;
    }

}

/// <summary>
/// <see cref="ContinuationDisplayState"/> 的三种模式（Round-2 §2.1）。
/// **显示层专用**，与引擎的 <see cref="JobPhase"/> / <see cref="RetryState"/> 无关。
/// </summary>
internal enum ContinuationMode
{
    /// <summary>无冻结：显示完全跟随引擎 raw 真值（正常运行时，以及 raw 追平高水位之后）。</summary>
    Live = 0,
    /// <summary>用户暂停/停止之后：显示冻结在检查点高水位，绝不倒退。</summary>
    Frozen = 1,
    /// <summary>用户已点恢复：显示停在高水位，直到引擎 raw 追平 ⇒ 自动转 Live。</summary>
    CatchingUp = 2,
}

/// <summary>
/// <see cref="ContinuationDisplayState.Apply"/> 的判定结果。
/// <paramref name="Effective"/> = 供 UI 显示的真值（可能被高水位抬高）；它**不是**业务完成度。
/// <paramref name="RawDisplayedBytes"/> = 引擎 raw 真值原样（诊断 / 回退检测用，永不被抬高）。
/// </summary>
internal readonly record struct ContinuationOutcome(
    ProgressTruthSnapshot? Effective,
    ContinuationMode Mode,
    bool ClearedThisCall,
    string ClearReason,
    long RawRegressionBytes,
    long RawDisplayedBytes,
    long HighWaterBytes);

/// <summary>
/// ★ m03985 执行令 §UI-C/§UI-D ★ 「打开验证报告」的真实结果 —— 界面只按这三样事实说话：
///   · <see cref="Opened"/>：系统默认浏览器是否**真的**把本地 HTML 打开了；
///   · <see cref="Path"/>：本机报告文件路径（非 null 就说明文件确实存在）；
///   · <see cref="Message"/>：直接展示给用户的一行如实说明（失败原因 / 可复制路径 / 成功文案）。
///
/// 存在的理由：旧写法把「调用没抛异常」当成「打开成功」，浏览器关联坏了也会显示成功；
/// 这里让"生成成功但打开失败"成为一个**可区分**的结果，界面据此把本机路径交给用户，而不是假称成功。
/// </summary>
/// <param name="Opened">用系统默认浏览器打开成功（true）或失败（false）。</param>
/// <param name="Path">本机验证报告 HTML 的绝对路径；null 表示没有生成任何文件。</param>
/// <param name="Message">给用户看的一行说明（不含口令、令牌等敏感信息）。</param>
public sealed record VerifyReportOpenResult(bool Opened, string? Path, string Message);
