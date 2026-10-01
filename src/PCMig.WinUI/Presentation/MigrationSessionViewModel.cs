using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using PCMig.Core.Jobs;
using PCMig.Core.Logging;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Native;
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
    public SessionObjectRow(string objectId, string sourcePath, string sizeText)
    {
        ObjectId = objectId;
        SourcePath = sourcePath;
        SizeText = sizeText;
    }

    public string ObjectId { get; }
    public string SourcePath { get; }
    public string SizeText { get; }

    private string _statusText = "待传输";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
}

/// <summary>失败 / 不一致清单行（Step3 左栏「提示」面板 + Step4「报告清单」用）。</summary>
public sealed class SessionFailItem
{
    public SessionFailItem(string title, string detail, bool objectLevel = false)
    {
        Title = title;
        Detail = detail;
        ObjectLevel = objectLevel;
        CreatedAt = DateTime.Now;   // ★ 真实产生时刻（Step4 报告清单「时间」列的唯一来源，不摆示例时间）
    }

    public string Title { get; }
    public string Detail { get; }
    /// <summary>true = 对象级失败（必须逐条红色列出：对象号 + 路径 + 退出码译文）。</summary>
    public bool ObjectLevel { get; }
    public string DisplayText => $"{Title}｜{Detail}";

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
///     ReportGenerator.Generate）一律 Task.Run 包裹；已是纯异步的 Core 方法（ScanAsync/RunAsync/
///     Verifier.RunAsync）直接 await（与 WPF MainViewModel 同口径）。
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
                }
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
    /// 副作用（预期）：未勾选的共享同样会以“全不勾”出现在 Step 2 目录树中。
    /// </summary>
    public void SyncTreeRootsFromShares()
    {
        var conn = _connection;
        if (conn is null) return;
        Tree.SyncRoots(conn.Shares.Select(s => new ShareRootSpec(s.UncPath, s.IsSelected)));
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
        StatusMessage = "已切换为「新建任务」：填好目标路径、在目录树勾选内容后点「预检并生成计划」。";
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

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set { if (Set(ref _isRunning, value)) RaiseDerived(); }
    }

    private bool _isPaused;
    public bool IsPaused
    {
        get => _isPaused;
        private set { if (Set(ref _isPaused, value)) RaiseDerived(); }
    }

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
    /// <summary>给用户看的一句话状态（所有动作的结论都落在这里，不静默失败）。</summary>
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }

    public string JobIdText => Ctx?.JobId ?? "（尚无任务）";

    /// <summary>有计划（且计划里有对象）才谈得上执行/验证。</summary>
    public bool HasPlan => Ctx?.Plan is { Objects.Count: > 0 };
    public bool HasJob => Ctx is not null;

    /// <summary>可"开始迁移"：不在跑、有计划、有目标根。</summary>
    public bool CanStart => !IsRunning && HasPlan && !string.IsNullOrWhiteSpace(TargetRoot);

    private static readonly JobPhase[] ResumablePhases =
        [JobPhase.Running, JobPhase.Paused, JobPhase.Interrupted, JobPhase.AwaitingReview, JobPhase.CompletedWithErrors];

    /// <summary>可"恢复任务"（阶段集合与 Core 的 JobManager.ResumablePhases 同口径）。</summary>
    public bool CanResume => !IsRunning && Ctx is not null && ResumablePhases.Contains(Phase);

    private bool _hasVerifyReport;
    /// <summary>可"尝试修复"：有任务且（有验证报告 / 有失败对象 / 上次完成但有错）。</summary>
    public bool CanRepair => !IsRunning && Ctx is not null
        && (_hasVerifyReport || FailedObjects > 0 || Phase is JobPhase.CompletedWithErrors or JobPhase.Interrupted);

    /// <summary>可暂停（运行中且未暂停）。</summary>
    public bool CanPause => IsRunning && !IsPaused;
    /// <summary>可停止（运行中）。</summary>
    public bool CanStop => IsRunning;

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

    private string _engineSpeedText = "—";
    public string EngineSpeedText { get => _engineSpeedText; private set => Set(ref _engineSpeedText, value); }

    private string _etaText = "—";
    public string EtaText { get => _etaText; private set => Set(ref _etaText, value); }

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
            var bad = report.Objects.Where(o => o.Status != "OK").Select(o => o.ObjectId).ToList();
            return $"✘ 存在不一致对象（{DescribeLevel(level)}）：{string.Join("、", bad)}。详见报告。";
        }

        var sampled = report.Objects.Sum(o => o.HashSampled);
        if (level < VerifyLevel.L2_SampleHash)
            return $"✔ {L1ResultText}";

        if (sampled == 0)
            return $"✔ {L1ResultText}｜⚠ {NoHashSampleText}";

        return $"✔ {L1ResultText}；本次另有 {sampled} 个文件做了 SHA-256 双向内容核对" +
               "（抽样通过不等于全部文件内容一致，未被抽到的文件仍只做过数量/字节核对）。";
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
    /// <returns>true = 计划已生成（HasPlan）或已载入可续传任务。</returns>
    public async Task<bool> PrepareAsync(
        JobDefinition definition,
        string? password,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        Func<Task<bool>>? confirmIncompleteScan = null,
        ResumeDecision resumeDecision = ResumeDecision.Unspecified)
    {
        if (IsRunning)
        {
            StatusMessage = "迁移正在进行中：请先「暂停」或「停止」，再重新预检。";
            return false;
        }
        ArgumentNullException.ThrowIfNull(definition);

        // ---- 源侧：勾选的共享（整盘） + 目录树勾选（customs 恒生效，见 ExpertModeIsWiredInPhaseA）----
        var wholeShares = definition.Sources.Select(s => s.Path).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        var customs = definition.CustomSelections;
        if (wholeShares.Count == 0 && customs.Count == 0)
        {
            StatusMessage = "请先在 Step 1 勾选要迁移的共享，或在目录树里勾选要迁移的内容：" +
                            "勾共享=整盘迁移；展开共享可精确勾选目录或单个文件。";
            return false;
        }
        if (string.IsNullOrWhiteSpace(definition.TargetRoot)) { StatusMessage = "请先指定目标路径。"; return false; }

        // ---- 目标路径前置校验（与 WPF PrepareAsync 686-704 同口径，傻瓜操作防线）----
        if (definition.TargetRoot.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            StatusMessage = $"目标路径包含非法字符（不能含 \" < > | 等）：{definition.TargetRoot}";
            return false;
        }
        try
        {
            var fullTarget = Path.GetFullPath(definition.TargetRoot.Trim());
            if (File.Exists(fullTarget))
            {
                StatusMessage = $"目标位置已存在一个同名文件而不是文件夹：{fullTarget}。请换一个目标目录。";
                return false;
            }
        }
        catch (Exception ex) { StatusMessage = $"目标路径无效：{ex.Message}"; return false; }

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
        catch (OperationCanceledException) { StatusMessage = "已取消。"; return false; }
        catch (Exception ex) { _log.Warning(ex, "未完成任务检测失败"); }

        if (resumeDecision == ResumeDecision.ResumeExisting)
        {
            if (dup is null)
            {
                StatusMessage = "你选择了继续上次任务，但当前源/目标下没有找到未完成的任务：请重新「预检并生成计划」新建一个。";
                return false;
            }
            _log.Information("用户选择续传既有任务 {JobId}（未新建）", dup.JobId);
            await AdoptExistingJobAsync(dup.JobDir, ct);
            StatusMessage = $"已载入未完成任务 {dup.JobId}（{dup.PhaseText}）：" +
                            (dup.StateUnreliable ? "状态文件损坏，进度以回执为准。" : $"已传 {dup.Percent:0.0}%。") +
                            "点「恢复任务」从断点继续（已完成的对象不会重传）；若要重新生成计划，请再点一次「预检并生成计划」。";
            return true;
        }

        if (dup is not null && resumeDecision == ResumeDecision.Unspecified)
        {
            // 页面没问过用户 → **绝不悄悄新建第二个任务**（同一份数据跑两条任务会在目标目录里互相打架）。
            // 交互由页面包提供（ContentDialog）：页面问完用户后带 CreateNew / ResumeExisting 重新调用本方法。
            PendingResumeCandidate = dup;
            StatusMessage = $"该源电脑和目标路径下已有未完成任务：{dup.JobId}（{dup.PhaseText}" +
                            (dup.StateUnreliable ? "，状态文件损坏，进度以回执为准" : $"，已传 {dup.Percent:0.0}%") +
                            "）。请选择「继续上次任务（已完成部分不重传）」或「创建全新任务」。";
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

        StatusMessage = "正在预检…";
        // ★ A.5（P1-4）：预检 = 进入"新任务"上下文。先把上一个任务的实时投影、验证投影与日志投影
        //   整批复位（否则上一轮的失败清单/当前对象/上一次验证结论会跟着新任务一起显示）。
        //   落在下面两行日志之前，保证日志面板里剩下的都是**本次预检**的真实行。
        ResetForJobSwitch();
        // 真实动作日志（Step4 实时日志面板）：这一条描述的是**正在真实执行**的预检项，不是示例文案。
        Log("INFO", "开始预检：由 Core 的 PreflightChecker 实测源可达性 / 目标空间 / 权限 / 同名冲突");
        Log("INFO", $"预检目标：源 {definition.SourceHost}｜目标 {definition.TargetRoot}｜线程 {ResolveThreads(SelectedMt)}");
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
                StatusMessage = $"预检未通过（{bad.Count} 项）：{bad.FirstOrDefault()?.Detail ?? "详见失败清单"}";
                Log("ERROR", $"预检未通过（{bad.Count} 项）：{bad.FirstOrDefault()?.Detail ?? "详见失败清单"}");
                return false;
            }

            var warns = pre.Checks.Where(c => !c.Pass && c.Severity == "Warning").ToList();
            StatusMessage = warns.Count > 0
                ? $"预检通过（{warns.Count} 项提醒不阻断：{warns[0].Name} —— {warns[0].Detail}）正在扫描源数据…"
                : "预检通过，正在扫描源数据（大容量磁盘可能需要几分钟）…";
            Log("INFO", warns.Count > 0
                ? $"预检通过（{warns.Count} 项提醒不阻断），开始扫描源数据…"
                : "预检通过，开始扫描源数据…");

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
                        StatusMessage = $"已拦下：扫描存在 {gate.Count} 处不可访问的目录/文件，**未生成可执行计划**（左栏已逐条列出）。" +
                                        "请修好权限/网络后重新「预检并生成计划」；确知风险仍要继续，请在确认框里选「我已知风险，仍要继续」。";
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
            StatusMessage = $"计划已生成（Job {ctx.JobId}）：{plan.Objects.Count} 个对象，共 {Format.Bytes(plan.TotalBytes)}。" +
                            (observed.Warnings.Count > 0 ? $" ⚠ {observed.Warnings[0]}" : string.Empty);
            StatusMessage += SpaceGuardNote(definition.TargetRoot, plan.TotalBytes);
            StatusMessage += LockAndEfsNote(observed);
            // ★ 真实计数（原先 UI 上那句「计划生成完成：对象 0 个，合计 0 B」是硬编码假数据，已删除）：
            //   这里写的就是本次 Planner 真实产出的对象数与计划字节数。
            Log("INFO", $"计划生成完成：对象 {plan.Objects.Count} 个，合计 {Format.Bytes(plan.TotalBytes)}（Job {ctx.JobId}）。");
            // 文件数只有扫描后才知道：超大规模时明确告知“全程流式、无文件数上限”
            if (observed.TotalFiles >= 5_000_000)
                StatusMessage += $"（文件量 {observed.TotalFiles:N0}，属超大规模——本任务传输/验证全程流式处理，无文件数上限）";
            HasPlanRaise();
            await RefreshExistingJobsAsync(null, ct);
            return true;
        }
        catch (OperationCanceledException) { StatusMessage = "已取消。"; return false; }
        catch (Exception ex)
        {
            StatusMessage = $"准备失败：{ex.Message}";
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
            parts.Add($"检测到 {observed.LockRiskFiles} 个 Outlook 数据文件（.pst/.ost）：旧电脑上 Outlook 若正在运行，这些文件会被锁定导致迁移失败，建议先让旧电脑用户关闭 Outlook");
        if (observed.EncryptedFiles > 0)
            parts.Add($"检测到 {observed.EncryptedFiles} 个 EFS 加密文件：内容可以正常复制，但在新机上将失去加密保护（明文可读）");
        _log.Warning("迁移前提醒：锁风险 {Lock} 个 / EFS 加密 {Enc} 个", observed.LockRiskFiles, observed.EncryptedFiles);
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
            StatusMessage = "尚无任务：不能开始迁移（请先在 Step 2 生成计划）。";
            LastRunOutcome = ActionOutcomePolicy.ForRun(new RunBusinessResult(Phase, "run-no-task", null));
            return Task.CompletedTask;
        }
        if (ctx.Plan is null || ctx.Plan.Objects.Count == 0)
        {
            StatusMessage = "计划里没有任何对象：不能开始迁移。";
            LastRunOutcome = ActionOutcomePolicy.ForRun(new RunBusinessResult(Phase, "run-no-plan-objects", null));
            return Task.CompletedTask;
        }
        return RunTransferCoreAsync(ctx, password, onlyObjectIds: null, forceRecopy: false, progress, ct);
    }

    /// <summary>
    /// 恢复任务：载入既有任务（可选）后继续。**沿用 job.json 里的线程值**，绝不改写任务定义。
    /// </summary>
    public Task ResumeAsync(string? password, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var ctx = Ctx;
        if (ctx is null)
        {
            StatusMessage = "请先选择要恢复的任务（Step 2/4 的未完成任务列表）。";
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
        Log("INFO", $"用户确认恢复任务 {ctx.JobId}（{PhaseText}）：已清除暂停请求，沿用 job.json 里的原线程值续传。");
        return RunTransferCoreAsync(ctx, password, onlyObjectIds: null, forceRecopy: false, progress, ct);
    }

    /// <summary>暂停：Cooperative（当前对象跑完停）/ Immediate（立即终止 robocopy）。</summary>
    public Task PauseAsync(bool immediate = false)
    {
        var ctx = Ctx;
        if (ctx is null || !IsRunning)
        {
            StatusMessage = "当前没有正在进行的迁移，无需暂停。";
            // 没有在跑的运行 ⇒ 暂停请求不可能被受理：如实 Rejected（不写 Succeeded）。
            LastPauseOutcome = ActionOutcomePolicy.ForRequest(honored: false, "pause-requested", "pause-not-running");
            return Task.CompletedTask;
        }
        try
        {
            // ★ A.5（P1-5）★ 用户即时操作的反馈属"绝对不可被节流延迟的量"：
            //   先 Flush 掉累积的批量行，再写状态（状态句/按钮态在下一帧即变，不等到 50ms 节拍）。
            FlushPendingNow();
            // ★ D6.1 §16 ★ 暂停请求（意图）——真正的写入结果由 Core 的 PauseRequestWriteResult 记录。
            PublishRepair(TransferEvents.PauseRequested,
                new TrnPauseObservedPayload(immediate ? "Immediate" : "Cooperative", null),
                DiagnosticLevel.Information, DiagnosticOutcome.Accepted);
            ctx.RequestPause(immediate);
            IsPaused = true;
            StatusMessage = immediate
                ? "已请求立即暂停：引擎正在终止当前 robocopy…"
                : "已请求暂停：当前对象跑完即停（已完成的对象不会重传）。";
            // D6.3 §11：请求已受理；**真停不停是异步的**（Core 的 PauseObserved），所以只写 Accepted。
            LastPauseOutcome = ActionOutcomePolicy.ForRequest(honored: true, "pause-requested", "pause-not-running");
        }
        catch (Exception ex)
        {
            StatusMessage = $"暂停请求写入失败：{ex.Message}";
            _log.Warning(ex, "Pause 失败");
            LastPauseOutcome = new ActionOutcomeDecision(DiagnosticOutcome.Failed, "pause-request-failed");
        }
        return Task.CompletedTask;
    }

    /// <summary>停止本次运行（取消令牌；已拷入的部分全部保留，可续传）。</summary>
    public Task StopAsync()
    {
        // D6.3 §11：本次停止请求有没有"在跑的运行"可取消（业务事实，来自既有 IsRunning）。
        var running = IsRunning;
        try
        {
            // ★ A.5（P1-5）★ 停止同属"不可被节流延迟的量"：先 Flush 再写状态。
            FlushPendingNow();
            // ★ D6.1 §16 ★ 停止请求（意图）——结果由 Core 的 StopObserved 记录。
            PublishRepair(TransferEvents.StopRequested,
                new TrnPauseObservedPayload("Cancel", null),
                DiagnosticLevel.Information, DiagnosticOutcome.Accepted);
            _cts?.Cancel();
            StatusMessage = "已请求停止：已拷入的部分全部保留，可点「恢复任务」续传。";
            _log.Information("WinUI 会话停止请求");
            // 有在跑的运行 ⇒ 取消请求已投递（Accepted）；没有 ⇒ 什么都没停成，如实 Skipped。
            LastStopOutcome = running
                ? ActionOutcomePolicy.ForRequest(honored: true, "stop-requested", "stop-no-running-run")
                : new ActionOutcomeDecision(DiagnosticOutcome.Skipped, "stop-no-running-run");
        }
        catch (Exception ex)
        {
            StatusMessage = $"停止请求失败：{ex.Message}";
            LastStopOutcome = new ActionOutcomeDecision(DiagnosticOutcome.Failed, "stop-request-failed");
        }
        return Task.CompletedTask;
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
        CancellationToken ct)
    {
        if (IsRunning)
        {
            StatusMessage = "已有一次运行在进行中。";
            LastRunOutcome = ActionOutcomePolicy.ForRun(new RunBusinessResult(Phase, "run-already-running", null));
            return;
        }

        // ---- 扫描残缺闸门（v0.3.8）：未确认不允许开跑（Core 的权威判定，不重写规则）----
        var scanGate = ScanGate.Inspect(ctx);
        if (scanGate.Blocks)
        {
            foreach (var p in scanGate.Paths.Take(12)) AddFail("扫描不可访问", p);
            StatusMessage = "已拦下：扫描存在不可访问位置。\n" + ScanGate.BuildBlockMessage(scanGate, cli: false);
            LastRunOutcome = ActionOutcomePolicy.ForRun(new RunBusinessResult(Phase, "run-scan-gate-blocked", null));
            return;
        }

        // 暂停/停止后的进程收尾有数秒窗口，恢复撞锁是实测过的坑——给 30 秒宽限（与 WPF 同口径）
        using var jobLock = JobLock.TryAcquire(ctx, TimeSpan.FromSeconds(30), out var lockReason);
        if (jobLock is null)
        {
            StatusMessage = lockReason;
            LastRunOutcome = ActionOutcomePolicy.ForRun(new RunBusinessResult(Phase, "run-job-lock-busy", null));
            return;
        }

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
                StatusMessage = $"连接失败：{ex.Message}";
                _log.Error(ex, "WinUI 传输前连接失败");
                LastRunOutcome = ActionOutcomePolicy.ForRun(new RunBusinessResult(Phase, null, "run-smb-connect-failed"));
                CleanupRun(session);
                return;
            }
        }
        else if (!string.IsNullOrEmpty(effUser))
        {
            // 不硬拦：Windows 可能还缓存着连接；失败原因由引擎照实报（与 WPF Repair 口径一致）
            StatusMessage = $"该任务使用账号 {effUser}：本次未提供密码，若 Windows 仍缓存着连接可正常继续，否则会以共享不可用失败。";
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
            StatusMessage = "已取消（可续传）：已拷入的部分保留，点「恢复任务」继续。";
        }
        catch (Exception ex)
        {
            runFaulted = true;
            StatusMessage = $"传输异常：{ex.Message}";
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
        IsPaused = Phase == JobPhase.Paused;
        _lastCompletedCount = -1;
        RaiseDerived();
    }

    private async Task FinishRunAsync(JobContext ctx, JobPhase phase, IReadOnlyCollection<string>? onlyObjectIds, CancellationToken ct)
    {
        // ★ A.5（P1-5）★ **必须先 Flush 再写结束态**：结束态（Percent/StatusMessage/IsFinished）
        //   属于"绝对不可被节流延迟的量"，而它之前累积的批量引擎行必须**先**上屏，
        //   否则日志尾部顺序会颠倒（动作行在前、它之前的引擎行在后）。
        FlushPendingNow();

        var end = ctx.LoadStateOrNew();
        var spaceShort = end.LastError?.Contains("空间不足") == true;
        Phase = phase;
        // ★ A.5（P1-3）：**只有真正的 Completed 才是 100%**。
        //   CompletedWithErrors 可能对应"目标盘满 / 网络故障 / 大量失败 / 被迫中断"——实际只传了一部分
        //   （例：计划 320GB、实际 80GB）。此时 UI 显示 100% 会让用户以为传完了，是产品诚实性缺陷。
        //   IsFinished（流程是否结束）继续包含两者（流程语义），但**百分比必须区分**。
        var completed = phase == JobPhase.Completed;
        var finished = completed || phase == JobPhase.CompletedWithErrors;
        IsFinished = finished;
        if (completed) Percent = 100.0;
        else if (finished) Percent = ActualPercent(end);
        DataLabel = finished ? "实际落盘 / 计划" : "已传 / 计划";
        BalanceText = $"计划 {Format.Bytes(end.TotalBytes)}　·　实际落盘 {Format.Bytes(end.CompletedBytes)}";

        StatusMessage = phase switch
        {
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
                "失败清单已逐条列出「对象号 + 路径 + 退出码译文」；请打开报告核对，处理后点「恢复任务」只补差异。",
            JobPhase.Paused =>
                $"‖ 已暂停：已完成 {end.CompletedObjects}/{end.TotalObjects} 个对象，剩余 {Math.Max(0, 100.0 - end.Percent):0.#}% 未传——点「恢复任务」续传。",
            JobPhase.Interrupted => "⏸ 已中断（可续传）：点「恢复任务」继续。",
            JobPhase.Failed => $"✘ 迁移失败：{end.LastError ?? "详见日志"}",
            _ => $"阶段结束：{phase}",
        };

        if (onlyObjectIds is { Count: > 0 })
        {
            // 定向修复路径：重拷结束后自动复验（与 WPF RepairAsync 同口径）
            IsRepairing = false;
            StatusMessage = $"重拷结束（{phase}），正在重新校验…";
            Log("INFO", $"重拷结束（{phase}）：正在重新校验（基础一致性检查）…");
            await VerifyAsync(VerifyLevel.L1_CountSize, null, ct);
        }
        await RefreshExistingJobsAsync(null, ct);
    }

    // ────────────────────────── 方法：验证 / 修复 ──────────────────────────

    /// <summary>
    /// 验证（默认 L1）。**必须先通过闸门**（无计划 / 0 对象 / 源不可达一律拒绝）。
    /// 结论文案统一由 <see cref="DescribeVerifyOutcome"/> 产出：L1 绝不说成"完整性验证通过"。
    /// </summary>
    public async Task VerifyAsync(VerifyLevel level = VerifyLevel.L1_CountSize, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var ctx = Ctx;
        if (ctx is null)
        {
            StatusMessage = "尚无任务：不能验证。";
            Log("WARN", "验证被拒绝：尚无任务。");
            LastVerifyOutcome = ActionOutcomePolicy.ForVerify(new VerifyBusinessResult(false, false, "verify-no-task", false, false));
            return;
        }
        if (IsRunning)
        {
            StatusMessage = "迁移/修复进行中：请先结束运行再验证。";
            Log("WARN", "验证被拒绝：迁移/修复进行中。");
            LastVerifyOutcome = ActionOutcomePolicy.ForVerify(new VerifyBusinessResult(false, false, "verify-already-running", false, false));
            return;
        }

        var gate = await EvaluateVerifyGateAsync(ct);
        if (!gate.CanVerify)
        {
            AddFail("验证被拒绝", gate.Reason);
            StatusMessage = gate.Reason;
            Log("WARN", "验证被闸门拒绝：" + gate.Reason);
            _log.Warning("验证被闸门拒绝: {Reason}", gate.Reason);
            LastVerifyOutcome = ActionOutcomePolicy.ForVerify(new VerifyBusinessResult(false, false, "verify-gate-blocked", false, false));
            return;
        }

        Phase = JobPhase.Verifying;
        StatusMessage = $"正在验证（{DescribeLevel(level)}）…";
        Log("INFO", $"开始验证：{DescribeLevel(level)}（闸门通过：有计划且源路径可达）");
        VerifyReport? report = null;
        var canceled = false;
        var faulted = false;
        try
        {
            var jobLog = LogBootstrap.CreateJobLogger(ctx.JobDir, ctx.JobId);
            var matrix = MigrationMatrix.Load(null, jobLog);
            var verifier = new Verifier(ctx, matrix, jobLog);
            report = await verifier.RunAsync(level, progress, ct);
            ctx.SaveVerify(report);
            _hasVerifyReport = true;
            TotalHashSampled = report.Objects.Sum(o => o.HashSampled);
            LastVerifyResultText = DescribeVerifyOutcome(report, level);
            StatusMessage = LastVerifyResultText;
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
        }
        catch (OperationCanceledException) { canceled = true; StatusMessage = "验证已取消。"; }
        catch (Exception ex)
        {
            faulted = true;
            StatusMessage = $"验证失败：{ex.Message}";
            Log("ERROR", $"验证失败：{ex.Message}");
            _log.Error(ex, "WinUI 验证失败");
        }

        // D6.3 §11：验证的实际业务结果 = **既有验证报告的结构化结论**（OverallPass / 对象数），
        // 绝不用"VerifyAsync 返回了"冒充成功（审计 P1-4）。
        LastVerifyOutcome = ActionOutcomePolicy.ForVerify(new VerifyBusinessResult(
            Executed: report is { Objects.Count: > 0 },
            OverallPass: report?.OverallPass == true,
            BlockedReason: null,
            Canceled: canceled,
            Faulted: faulted));
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
            StatusMessage = "尚无任务：不能修复。";
            Log("WARN", "尝试修复被拒绝：尚无任务。");
            LastRepairOutcome = ActionOutcomePolicy.ForRepair(new RepairBusinessResult(0, 0, 0, "repair-no-task"));
            return;
        }
        if (IsRunning)
        {
            StatusMessage = "已有一次运行在进行中。";
            Log("WARN", "尝试修复被拒绝：已有一次运行在进行中。");
            LastRepairOutcome = ActionOutcomePolicy.ForRepair(new RepairBusinessResult(0, 0, 0, "repair-already-running"));
            return;
        }

        var targets = CollectRepairTargets(out var beforeMismatch);
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
            StatusMessage = "没有需要修复的对象：上次校验（若有）全部一致，也没有失败对象。";
            Log("INFO", "尝试修复：没有需要修复的对象（上次校验全部一致且无失败对象）——未启动重拷。");
            LastRepairOutcome = ActionOutcomePolicy.ForRepair(new RepairBusinessResult(0, 0, 0, null));
            return;
        }
        PublishRepair(RepairEvents.RepairTargetsCollected,
            new RprTargetsPayload(targets.Count, beforeMismatch, Math.Max(0, targets.Count - beforeMismatch), plannedObjects),
            DiagnosticLevel.Information, DiagnosticOutcome.Succeeded);
        StatusMessage = (beforeMismatch > 0 ? $"修复前 {beforeMismatch} 个对象不一致" : $"修复前有 {targets.Count} 个对象需要重拷")
            + (forceOverwrite ? "，将从共享强制覆盖同名文件" : "，按增量补差异");
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
    public List<string> CollectRepairTargets(out int beforeMismatch)
    {
        var ids = new List<string>();
        beforeMismatch = 0;
        var ctx = Ctx;
        if (ctx is null) return ids;

        var verify = ctx.LoadVerify();
        if (verify is not null)
        {
            var bad = verify.Objects.Where(o => o.Status != "OK").ToList();
            beforeMismatch = bad.Count;
            ids.AddRange(bad.Select(o => o.ObjectId));
        }

        var receipts = ctx.LoadReceipts(_log);
        var latest = receipts
            .GroupBy(r => r.ObjectId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(r => r.CompletedUtc).Last());
        foreach (var r in latest)
            if (r.Status is ObjectStatus.Failed or ObjectStatus.CompletedWithErrors or ObjectStatus.Interrupted)
                ids.Add(r.ObjectId);

        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>生成并打开报告（Core 的 ReportGenerator，唯一权威报告源）。</summary>
    public async Task OpenReportAsync(CancellationToken ct = default)
    {
        var ctx = Ctx;
        if (ctx is null) { StatusMessage = "尚无任务：没有可生成的报告。"; Log("WARN", "打开报告被拒绝：尚无任务（没有可生成的报告）。"); return; }
        try
        {
            var jobLog = LogBootstrap.CreateJobLogger(ctx.JobDir, ctx.JobId);
            var path = await Task.Run(() => new ReportGenerator(ctx, jobLog).Generate(), ct);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            StatusMessage = $"报告已生成并打开：{path}";
            Log("INFO", $"报告已生成并打开：{path}");
        }
        catch (Exception ex)
        {
            StatusMessage = $"报告生成失败：{ex.Message}";
            Log("ERROR", $"报告生成失败：{ex.Message}");
            _log.Error(ex, "WinUI 报告生成失败");
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
            StatusMessage = "尚无目标路径：没有可打开的目标文件夹。";
            Log("WARN", "打开目标文件夹被拒绝：尚未指定目标路径。");
            return;
        }
        try
        {
            if (!Directory.Exists(target))
            {
                StatusMessage = $"目标文件夹不存在（可能已被移动或删除）：{target}";
                Log("WARN", $"打开目标文件夹被拒绝：目录不存在 {target}");
                return;
            }
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            StatusMessage = $"已打开目标文件夹：{target}";
            Log("INFO", $"已打开目标文件夹：{target}");
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开目标文件夹失败：{ex.Message}";
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
            StatusMessage = $"已打开日志目录：{LogBootstrap.AppLogDir}";
            Log("INFO", $"已打开日志目录：{LogBootstrap.AppLogDir}");
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开日志目录失败：{ex.Message}";
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
        if (string.IsNullOrWhiteSpace(host)) { StatusMessage = "尚未连接旧电脑：无法检测未完成任务。"; Log("WARN", "未完成任务检测被跳过：尚未连接旧电脑。"); return false; }
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
                StatusMessage = $"检测到未完成的迁移任务：{desc}。已载入，点「恢复任务」可从中断处继续（已完成的对象不会重传）。";
            }
            else
            {
                StatusMessage = $"检测到未完成的迁移任务：{desc}。请先结束当前任务，再决定是否续传该任务。";
            }
            Log("WARN", $"检测到未完成的迁移任务（未自动恢复）：{desc}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "断点提醒检测失败");
            StatusMessage = $"未完成任务检测失败：{ex.Message}";
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
            var completed = Phase == JobPhase.Completed;
            Percent = completed
                ? 100.0
                : unreliable
                    ? 0            // 状态损坏：照旧不冒充比例（下方文案已说明"以回执为准"），但绝不写 100%
                    : Phase == JobPhase.CompletedWithErrors ? ActualPercent(st) : st.Percent;
            DataLabel = Phase is JobPhase.Completed or JobPhase.CompletedWithErrors ? "实际落盘 / 计划" : "已传 / 计划";
            PlanBytesText = Format.Bytes(st.TotalBytes);
            ActualBytesText = Format.Bytes(st.CompletedBytes);
            ProgressText = unreliable ? "状态损坏，待由回执重建" : $"{Format.Bytes(st.CompletedBytes)} / {Format.Bytes(st.TotalBytes)}";
            ObjectText = unreliable ? "未知（以回执为准）" : $"{st.CompletedObjects}/{st.TotalObjects}";
            BalanceText = $"计划 {Format.Bytes(st.TotalBytes)}　·　实际落盘 {Format.Bytes(st.CompletedBytes)}";
            // ★ A.5（P1-4）：失败对象数也按**本任务存档**重建（改前会留着上一个任务/上一轮运行的内存值）。
            FailedObjects = unreliable ? 0 : st.FailedObjects;
            // 暂停态同样按本任务的真实 Phase 重建（不是"清成 false"，而是与本任务存档一致）。
            IsPaused = Phase == JobPhase.Paused;
            _hasVerifyReport = ctx.LoadVerify() is not null;
            TotalHashSampled = 0;   // 旧报告里的抽样数在加载时不重算（避免把历史数据当本次证据）
            RaiseDerived();

            StatusMessage = unreliable
                ? "⚠ 状态文件 job-state.json 损坏或缺失，进度无法显示（故意不写 0.0%，以免被当成「一点没传」）。" +
                  "点「恢复任务」会按回执重建进度，已完成的对象不会重传。"
                // ★ A.5（P1-3）：载入的旧任务若是 CompletedWithErrors，必须明说"没传完 + 实际比例 + 可恢复"。
                : Phase == JobPhase.CompletedWithErrors
                    ? $"已载入任务 {ctx.JobId}（{PhaseText}）：{ObjectText} 个对象，实际完成 {ActualPercent(st):0.#}%" +
                      $"（{Format.Bytes(st.CompletedBytes)} / {Format.Bytes(st.TotalBytes)}）——**没有全部传完**，" +
                      "点「尝试修复」或「恢复任务」补齐（已完成的对象不会重传）。"
                    : $"已载入任务 {ctx.JobId}（{PhaseText}）：{ObjectText} 个对象，计划 {PlanBytesText}。";
            Log("INFO", $"已载入任务 {ctx.JobId}（{PhaseText}）：{ObjectText} 个对象，计划 {PlanBytesText}" +
                        (unreliable ? "；job-state.json 损坏或缺失，进度以回执为准。" : "。"));
            await RefreshRowsFromReceiptsAsync(ct);
            await RefreshExistingJobsAsync(null, ct);
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"载入任务失败：{ex.Message}";
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

    /// <summary>把引擎的进度快照翻译成显示位（唯一入口；所有 UI 更新都在 UI 线程）。</summary>
    private void ApplySnapshot(ProgressSnapshot s)
    {
        // ★ A.5（P1-3）：进度条只有在**真 Completed** 时才走满；CompletedWithErrors 用引擎报的实际比例。
        var finished = s.Phase is JobPhase.Completed or JobPhase.CompletedWithErrors;
        Phase = s.Phase;
        IsPaused = s.Phase == JobPhase.Paused;
        IsFinished = finished;
        DataLabel = finished ? "实际落盘 / 计划" : "已传 / 计划";
        Percent = s.Phase == JobPhase.Completed ? 100.0 : s.Percent;
        ProgressText = $"{Format.Bytes(s.CompletedBytes)} / {Format.Bytes(s.TotalBytes)}";
        PlanBytesText = Format.Bytes(s.TotalBytes);
        ActualBytesText = Format.Bytes(s.CompletedBytes);
        BalanceText = $"计划 {Format.Bytes(s.TotalBytes)}　·　实际落盘 {Format.Bytes(s.CompletedBytes)}";
        EngineSpeedText = s.BytesPerSecond > 0 ? Format.Speed(s.BytesPerSecond) : "—";
        EtaText = double.IsNaN(s.EtaSeconds) ? "—" : Format.Eta(s.EtaSeconds);
        ObjectText = $"{s.CompletedObjects}/{s.TotalObjects}" + (s.FailedObjects > 0 ? $"（失败 {s.FailedObjects}）" : "");
        FailedObjects = s.FailedObjects;
        CurrentObjectPath = s.CurrentObjectPath ?? string.Empty;
        StallSeconds = s.StallSeconds;

        StatusMessage = s.Phase == JobPhase.Paused
            ? $"‖ 已暂停：已完成 {s.CompletedObjects}/{s.TotalObjects} 个对象，剩余 {Math.Max(0, 100.0 - s.Percent):0.#}% 未传" +
              $"（{Format.Bytes(Math.Max(0, s.TotalBytes - s.CompletedBytes))}）——点「恢复任务」续传（已完成的对象不会重传）。"
            // ★ A.5（P1-3）：引擎给 CompletedWithErrors 的结束语**不再原样透传**（引擎文案可能像"完成"）。
            //   这里统一改写成"未完整完成 + 实际比例 + 可恢复/可修复"，与 FinishRunAsync 同一口径。
            : s.Phase == JobPhase.CompletedWithErrors
                ? $"◐ 迁移**未完整完成**（存在失败对象，实际完成 {s.Percent:0.#}%：" +
                  $"{Format.Bytes(s.CompletedBytes)} / {Format.Bytes(s.TotalBytes)}）。" +
                  "失败清单已逐条列出；点「尝试修复」或「恢复任务」处理（已完成的对象不会重传）。"
                : s.Message;

        // 当前对象 + 停滞提示（"界面长时间不动"是用户判断卡死的直接原因）
        if (!string.IsNullOrEmpty(s.CurrentObjectPath) && s.Phase == JobPhase.Running)
        {
            CurrentObjectDetail = $"当前对象：{s.CurrentObjectPath}，已传 {Format.Bytes(s.CurrentObjectDoneBytes)}" +
                (s.CurrentObjectTotalBytes > 0 ? $"/{Format.Bytes(s.CurrentObjectTotalBytes)}" : string.Empty);
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
        }

        if (s.CompletedObjects != _lastCompletedCount)
        {
            _lastCompletedCount = s.CompletedObjects;
            _ = RefreshRowsFromReceiptsAsync();   // 回执读取是 IO：后台跑，UI 更新回队列
        }
        ApplyObjectRowStatus(s);
    }

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
                        objectLevel: true);
                }
            }
        });
    }

    /// <summary>把计划对象填进清单（RootFiles 给人话标签；体积未知时写"传输时实测"）。</summary>
    private void FillObjectsFromPlan(MigrationPlan? plan)
    {
        Objects.Clear();
        if (plan is null) return;
        foreach (var o in plan.Objects)
        {
            Objects.Add(new SessionObjectRow(
                o.ObjectId,
                o.Kind == ObjectKind.RootFiles ? $"{o.SourcePath}　［根目录散落文件］" : o.SourcePath,
                o.EstimatedBytes < 0 ? "传输时实测" : Format.Bytes(o.EstimatedBytes)));
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
            row.StatusText = r.Status == "OK" ? "✔ 数量/字节一致" : "✘ 不一致（数量或字节不符）";
            if (r.Status != "OK")
            {
                AddFail($"✘ 校验不一致　{r.ObjectId}",
                    $"源 {r.SourceFiles} 个文件 / {Format.Bytes(r.SourceBytes)}　→　目标 {r.TargetFiles} 个文件 / {Format.Bytes(r.TargetBytes)}",
                    objectLevel: true, verifyDerived: true);
            }
        }
        if (!report.OverallPass && report.Objects.Any(o => o.HashMismatched > 0))
            AddFail("内容级抽样不一致", $"{report.Objects.Sum(o => o.HashMismatched)} 个抽样文件 SHA-256 不匹配（内容确实不同）。",
                verifyDerived: true);
    }

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
        LiveFiles.Clear();
        CurrentFileText = "—";
        CurrentObjectPath = string.Empty;
        CurrentObjectDetail = string.Empty;
        StallHintText = string.Empty;
        StallSeconds = 0;
        EngineSpeedText = "—";
        EtaText = "—";
        _lastCompletedCount = -1;

        // ★ A.5（P1-5）★ 上一轮遗留的待刷新数据与丢弃计数一并作废：
        //   否则它们会冒充"本轮的真实状态"，而丢弃计数更是会跨轮累加成不诚实的数字。
        _batcher.Clear();
        _shownOmittedFiles = 0;
        _shownOmittedLogs = 0;
        _omittedHintLine = null;
        _flushCount = 0;
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
        IsPaused = false;
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
    private void AddFail(string title, string detail, bool objectLevel = false, bool verifyDerived = false)
    {
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
        FailItems.Add(new SessionFailItem(title, detail, objectLevel));

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
    }

    /// <summary>
    /// 停止节拍并断开回调（幂等、绝不外抛）。**唯一的停止点**：<see cref="CleanupRun"/>、
    /// 任务切换（<see cref="ResetForJobSwitch"/>）与窗口关闭（<see cref="StopUiRefresh"/>）都走这里。
    /// </summary>
    private void StopFlushTimer()
    {
        var pump = _flushPump;
        if (pump is null) return;
        try { pump.Stop(); } catch { /* 关闭期异常不得外溢 */ }
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