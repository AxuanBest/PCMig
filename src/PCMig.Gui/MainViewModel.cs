using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
using PCMig.Core.Verify;
using Serilog;

namespace PCMig.Gui;

/// <summary>左侧导轨的一个步骤。</summary>
public sealed record StepItem(string Number, string Title, string Subtitle);

public sealed class ShareRow : ViewModelBase
{
    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public required string Name { get; init; }
    public required string UncPath { get; init; }
    public required string Remark { get; init; }
    public required string TypeText { get; init; }
}

public sealed class ObjectRow : ViewModelBase
{
    private string _status = "待传输";
    public required string ObjectId { get; init; }
    public required string SourcePath { get; init; }
    public required string SizeText { get; init; }
    public string Status { get => _status; set => Set(ref _status, value); }
}

public sealed class ExistingJobItem
{
    public required string JobId { get; init; }
    public required string JobDir { get; init; }
    public required string DisplayText { get; init; }
    public override string ToString() => DisplayText;
}

/// <summary>报错区一行：什么位置的什么文件 + 一句话原因。</summary>
public sealed class FailRow : ViewModelBase
{
    private string _time = "";
    private string _reason = "";
    public required string Location { get; init; }
    public string Time { get => _time; set => Set(ref _time, value); }
    public string Reason { get => _reason; set => Set(ref _reason, value); }

    /// <summary>
    /// v0.3.8（缺陷 3）：对象级失败（整条对象失败，例如"退出码 16 = 一个文件都没传"）
    /// 必须比单文件失败更刺眼 —— 界面按此把它们渲染成红色加粗。
    /// </summary>
    public bool IsObjectLevel { get; set; }
}

public sealed class MainViewModel : ViewModelBase
{
    private readonly Serilog.ILogger _appLog;
    private readonly MemorySink _memorySink = new(800);
    private readonly StringBuilder _logBuffer = new();
    private JobContext? _ctx;
    private CancellationTokenSource? _runCts;
    private int _lastCompletedCount = -1;
    private string? _lastPassword;   // 连接成功后暂存（仅内存），供开始/恢复传输时建立 SMB 会话
    private bool _bigDirHintShown;   // 超大目录建议提示只弹一次

    // ---- 目录浏览状态 ----
    private int _browseVersion;
    private MigrationMatrix? _matrixCache;

    // ---- 报错区状态（按位置去重）----
    private readonly Dictionary<string, FailRow> _failIndex = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxFailRows = 500;

    // ---- 网卡实时吞吐（与任务管理器同源：网络接口计数器 RX+TX 差分，1 秒采样）----
    private System.Windows.Threading.DispatcherTimer? _nicTimer;
    private long _nicLastTotal;
    private DateTime _lastProgressUtc = DateTime.MinValue;   // 引擎最近一次上报进度的时间（据此判断"是否真的在传"）
    private readonly Queue<double> _nicHistory = new();
    private const int NicWindowSeconds = 60;          // 时间轴窗口（秒）
    private const double NicSparkWidth = 116;         // 与 XAML 内 Polyline 尺寸保持一致
    private const double NicSparkHeight = 20;

    /// <summary>测得的实时网速（本机全部活动网卡的 RX+TX 合计）。</summary>
    private string _nicSpeedText = "—";
    public string NicSpeedText { get => _nicSpeedText; set => Set(ref _nicSpeedText, value); }

    /// <summary>折线点集（时间轴波形，右侧为最新）。</summary>
    public PointCollection NicSparkPoints { get; } = new();

    /// <summary>波形悬浮说明（含峰值）。</summary>
    private string _nicSparkTip = "网卡实时吞吐（本机全部活动网卡 收+发 合计，含其它程序流量）：迁移开始后显示，右端为最新";
    public string NicSparkTip { get => _nicSparkTip; set => Set(ref _nicSparkTip, value); }

    /// <summary>本次任务自身实测速率（引擎按落盘字节算），与网卡吞吐区分显示。</summary>
    private string _engineSpeedText = "—";
    public string EngineSpeedText { get => _engineSpeedText; set => Set(ref _engineSpeedText, value); }

    // ---- 实时文件流（正在复制的文件，robocopy 控制台既视感）----
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _liveQueue = new();
    private System.Windows.Threading.DispatcherTimer? _liveTimer;
    private const int MaxLiveRows = 150;   // 面板只保留最近 150 行（老的滚出）
    private const int LiveDrainCap = 300;  // 每次刷新最多渲染 300 条（约每秒 1200 个文件的显示能力，超出折叠为摘要行）

    public MainViewModel()
    {
        _appLog = LogBootstrap.CreateAppLogger(console: false, memorySink: _memorySink);
        Log.Logger = _appLog;
        _memorySink.LineEmitted += line =>
            Application.Current?.Dispatcher.BeginInvoke(() => AppendLog(line));

        ConnectCommand = new RelayCommand(p => ConnectAsync(p as PasswordBox));
        AddManualShareCommand = new RelayCommand(p => AddManualShareAsync(p));
        BrowseTargetCommand = new RelayCommand(_ => BrowseTarget());
        PrepareCommand = new RelayCommand(p => PrepareAsync(p as PasswordBox));
        StartCommand = new RelayCommand(_ => RunTransferAsync(), _ => CanStart);
            PauseCommand = new RelayCommand(_ => { _ctx?.RequestPause(false); StatusMessage = "已请求协作式暂停（当前对象传完后停止）"; });
            StopCommand = new RelayCommand(_ => { _ctx?.RequestPause(true); StatusMessage = "已请求立即停止（终止当前 robocopy，已传部分保留）"; });
        ResumeCommand = new RelayCommand(_ => ResumeAsync(), _ => CanResume);
        VerifyCommand = new RelayCommand(_ => VerifyAsync(), _ => HasJob);
        RepairCommand = new RelayCommand(_ => RepairAsync(), _ => HasJob && !IsRunning);
        ReportCommand = new RelayCommand(_ => ReportAndOpen(), _ => HasJob);
        OpenTargetCommand = new RelayCommand(_ => OpenTargetFolder(), _ => HasJob);
        GoStepCommand = new RelayCommand(p =>
        {
            if (int.TryParse(p?.ToString(), out var s) && s >= 0 && s <= 3) CurrentStep = s;
        });

        RefreshExistingJobs();
        StartNicMonitor();   // 网速显示改用真实网卡吞吐（任务管理器同源），常驻秒级采样

        // ---- 左导航栏「提示」面板：标题条数与空态跟着报错集合走 ----
        FailItems.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ErrorHeader));
            OnPropertyChanged(nameof(HasErrors));
        };

            AppendLog("PCMig 已启动。输入旧电脑 IP/电脑名，点「连接并列出共享」开始。");
            _appLog.Information("GUI 启动，日志目录 {Dir}", LogBootstrap.AppLogDir);
            _appLog.Debug("crafted by 郑子轩 (Axuanbest)"); // 彩蛋：只落在文件日志，不进面板
    }

    // ---------------- 属性 ----------------

    private string _host = "";
    public string Host
    {
        get => _host;
        set { if (Set(ref _host, value)) OnPropertyChanged(nameof(SourceSummary)); }
    }

    private string _username = "";
    public string Username { get => _username; set => Set(ref _username, value); }

    private string _targetRoot = "";
    public string TargetRoot
    {
        get => _targetRoot;
        set { if (Set(ref _targetRoot, value)) OnPropertyChanged(nameof(TargetSummary)); }
    }

    private string _manualShareName = "";
    /// <summary>手动输入的共享名（对方不开放共享枚举/无 IPC$ 时使用，如 d 或 D$）。</summary>
    public string ManualShareName { get => _manualShareName; set => Set(ref _manualShareName, value); }

    /// <summary>左侧步骤导轨的条目（点击直接切页，SelectedIndex 与 CurrentStep 双向绑定）。</summary>
    public ObservableCollection<StepItem> StepItems { get; } = new()
    {
        new("①", "连接源电脑", "填 IP 与账号，列出共享"),
        new("②", "选择数据与目标", "勾选要迁移的内容"),
        new("③", "迁移进度", "实时进度、文件流与报错"),
        new("④", "结果与校验", "完整性校验、报告、异常清单")
    };

    private int _currentStep;
    /// <summary>当前步骤页：0=连接 1=选择 2=迁移 3=结果。</summary>
    public int CurrentStep
    {
        get => _currentStep;
        set
        {
            if (!Set(ref _currentStep, value)) return;
            OnPropertyChanged(nameof(IsStepConnect));
            OnPropertyChanged(nameof(IsStepSelect));
            OnPropertyChanged(nameof(IsStepTransfer));
            OnPropertyChanged(nameof(IsStepResult));
        }
    }

    public bool IsStepConnect => CurrentStep == 0;
    public bool IsStepSelect => CurrentStep == 1;
    public bool IsStepTransfer => CurrentStep == 2;
    public bool IsStepResult => CurrentStep == 3;

    /// <summary>标题栏左侧显示版本号。</summary>
    public string VersionText { get; } =
        "v" + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0");

    /// <summary>标题栏/状态条上的源电脑摘要。</summary>
    public string SourceSummary => string.IsNullOrWhiteSpace(Host) ? "未指定旧电脑" : Host;

    /// <summary>标题栏/状态条上的目标摘要。</summary>
    public string TargetSummary => string.IsNullOrWhiteSpace(TargetRoot) ? "未指定目标" : TargetRoot;

    public ObservableCollection<ShareRow> Shares { get; } = new();
    public ObservableCollection<ObjectRow> Objects { get; } = new();
    public ObservableCollection<ExistingJobItem> ExistingJobs { get; } = new();
    public ObservableCollection<DirNode> DirTree { get; } = new();
    public ObservableCollection<FailRow> FailItems { get; } = new();

    /// <summary>robocopy /MT 线程数选项（bulk 通道）。</summary>
    public ObservableCollection<int> MtOptions { get; } = new() { 8, 16, 32, 64, 128 };

    private int _selectedMt = 16;
    /// <summary>选定的线程数（默认 16）。</summary>
    public int SelectedMt { get => _selectedMt; set => Set(ref _selectedMt, value); }

    private bool _benchmarkOnConnect = true;
    /// <summary>连接/预检时做链路吞吐基准（向源共享写读 64MB 测试文件），建立「该环境正常速度」参照——正式迁移明显慢于此值时优先怀疑安全软件实时扫描。</summary>
    public bool BenchmarkOnConnect { get => _benchmarkOnConnect; set => Set(ref _benchmarkOnConnect, value); }

    // ---- 超大数据模式：目录树 ↔ 排除规则输入 ----
    private bool _expertMode;
    /// <summary>超大数据模式：② 区从目录树切换为「共享勾选 + 排除规则输入」。</summary>
    public bool ExpertMode { get => _expertMode; set => Set(ref _expertMode, value); }

    private string _expertExclusions = "";
    /// <summary>排除规则文本（每行一条：/XD 目录 或 /XF 文件；裸词按目录；# 注释）。</summary>
    public string ExpertExclusions { get => _expertExclusions; set => Set(ref _expertExclusions, value); }

    private ExistingJobItem? _selectedExistingJob;
    public ExistingJobItem? SelectedExistingJob
    {
        get => _selectedExistingJob;
        set
        {
            if (!Set(ref _selectedExistingJob, value)) return;
            OnPropertyChanged(nameof(CanResume));
            if (value != null) PreviewExistingJob(value.JobDir);
        }
    }

    private double _percent;
    public double Percent { get => _percent; set => Set(ref _percent, value); }

    // ---- 「尝试修复」独立进度：修复不再和迁移共用一条进度条（用户反馈：混在一起看不清）----
    private bool _isRepairing;
    /// <summary>本次修复已完成的（修复范围内的）对象集合——修复进度只统计这些，不用整个任务的累计数。</summary>
    private readonly HashSet<string> _repairDone = new HashSet<string>();
    public bool IsRepairing
    {
        get => _isRepairing;
        set { if (Set(ref _isRepairing, value)) { OnPropertyChanged(nameof(RepairVisibility)); OnPropertyChanged(nameof(MigrationProgressVisibility)); } }
    }
    public System.Windows.Visibility RepairVisibility =>
        _isRepairing ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    /// <summary>迁移那条进度（大数字 + 大进度条）在修复期间隐藏：同一版块只保留一条进度条，避免"两条同时在跑"的误读。</summary>
    public System.Windows.Visibility MigrationProgressVisibility =>
        _isRepairing ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    private double _repairPercent;
    public double RepairPercent { get => _repairPercent; set => Set(ref _repairPercent, value); }
    private string _repairText = "";
    public string RepairText { get => _repairText; set => Set(ref _repairText, value); }
    private string _repairObjectText = "";
    public string RepairObjectText { get => _repairObjectText; set => Set(ref _repairObjectText, value); }

    private string _progressText = "0 B / 0 B";
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }


    private string _etaText = "—";
    public string EtaText { get => _etaText; set => Set(ref _etaText, value); }

    private string _objectText = "—";
    public string ObjectText { get => _objectText; set => Set(ref _objectText, value); }

    // ---- 计划量 / 实际落盘量对账（结束后进度条走满，数据差多少由这三个数字说清）----
    private string _planBytesText = "—";
    public string PlanBytesText { get => _planBytesText; set => Set(ref _planBytesText, value); }

    private string _actualBytesText = "—";
    public string ActualBytesText { get => _actualBytesText; set => Set(ref _actualBytesText, value); }

    private string _balanceText = "";
    public string BalanceText { get => _balanceText; set => Set(ref _balanceText, value); }

    private bool _hasShortfall;
    public bool HasShortfall { get => _hasShortfall; set => Set(ref _hasShortfall, value); }

    private bool _isFinished;                     // 本次传输是否已结束（进度条满格 + 绿色）
    public bool IsFinished
    {
        get => _isFinished;
        set { if (Set(ref _isFinished, value)) OnPropertyChanged(nameof(IsRunning)); }
    }

    private string _dataLabel = "已传 / 计划";
    public string DataLabel { get => _dataLabel; set => Set(ref _dataLabel, value); }

    private int _failedObjects;
    public int FailedObjects { get => _failedObjects; set => Set(ref _failedObjects, value); }

    private string _statusMessage = "就绪";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    private bool _repairForceOverwrite = true;
    /// <summary>修复时是否强制从共享覆盖（/IS /IT）。默认开：修的是"验证发现不一致"的文件，宁可重拉也不要跳过。</summary>
    public bool RepairForceOverwrite { get => _repairForceOverwrite; set => Set(ref _repairForceOverwrite, value); }

    /// <summary>左导航栏「提示」面板标题（带条数）。</summary>
    // ---- v0.3.8（缺陷 3）：失败/暂停/停滞三类"刺眼提示" ----

    /// <summary>失败对象条数（整条对象失败，与"失败文件"区分）。</summary>
    public int FailedObjectRows => FailItems.Where(r => r.IsObjectLevel).Count();

    /// <summary>「当前对象：<路径>，已传 A/B」——进度长时间不动时用户最需要这一行。</summary>
    private string _currentObjectDetail = "";
    public string CurrentObjectDetail { get => _currentObjectDetail; set => Set(ref _currentObjectDetail, value); }

    /// <summary>当前对象行是否有内容（驱动界面显隐；XAML 里不再为它单独造一个字符串转换器）。</summary>
    private bool _hasCurrentObjectDetail;
    public bool HasCurrentObjectDetail { get => _hasCurrentObjectDetail; set => Set(ref _hasCurrentObjectDetail, value); }

    /// <summary>停滞提示（"大文件可能数分钟无进度变化"等），停止传输时清空。</summary>
    private string _stallHintText = "";
    public string StallHintText { get => _stallHintText; set => Set(ref _stallHintText, value); }

    private bool _hasStallHint;
    public bool HasStallHint { get => _hasStallHint; set => Set(ref _hasStallHint, value); }

    /// <summary>有对象失败时置 true：驱动界面把失败区标题/边线变红，避免"失败了却像还在跑"。</summary>
    private bool _hasObjectFailures;
    public bool HasObjectFailures { get => _hasObjectFailures; set => Set(ref _hasObjectFailures, value); }

    public string ErrorHeader
    {
        get
        {
            if (FailItems.Count == 0) return "提示";
            var obj = FailedObjectRows;
            return obj > 0 ? $"提示（{FailItems.Count}）· 其中失败对象 {obj} 个 ✘" : $"提示（{FailItems.Count}）";
        }
    }
    /// <summary>是否有报错条目（决定面板显示清单还是空态）。</summary>
    public bool HasErrors => FailItems.Count > 0;

    private string _logText = "";
    public string LogText { get => _logText; set => Set(ref _logText, value); }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (Set(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanResume));
                OnPropertyChanged(nameof(CanRepair));
            }
        }
    }

    private bool _planReady;
    public bool PlanReady
    {
        get => _planReady;
        set { if (Set(ref _planReady, value)) OnPropertyChanged(nameof(CanStart)); }
    }

    public bool CanStart => PlanReady && !IsRunning && _ctx != null;
    public bool CanResume => !IsRunning && (SelectedExistingJob != null || _ctx != null);
    public bool HasJob => _ctx != null && !IsRunning;

    /// <summary>「尝试修复」按钮可用性：有任务且当前没在跑。是否真有可修对象在点击时判定并给说明。</summary>
    public bool CanRepair => _ctx != null && !IsRunning;

    // ---------------- 命令 ----------------

    public RelayCommand ConnectCommand { get; }
    public RelayCommand AddManualShareCommand { get; }
    public RelayCommand BrowseTargetCommand { get; }
    public RelayCommand PrepareCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand VerifyCommand { get; }
    /// <summary>「尝试修复」：验证发现不一致后按对象重拷差异并自动复验。</summary>
    public RelayCommand RepairCommand { get; }
    public RelayCommand ReportCommand { get; }
    public RelayCommand OpenTargetCommand { get; }
    public RelayCommand GoStepCommand { get; }

    private async Task ConnectAsync(PasswordBox? pwdBox)
    {
        if (string.IsNullOrWhiteSpace(Host)) { StatusMessage = "请先输入旧电脑的 IP 或电脑名"; return; }

        // 支持直接粘贴 \\IP\共享名（如同资源管理器运行框）：自动拆出主机与共享
        var host = Host.Trim();
        string? directShare = null;
        if (host.StartsWith(@"\\"))
        {
            var parts = host.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) { StatusMessage = "路径格式不对，应为 \\\\IP\\共享名 或 IP"; return; }
            host = parts[0];
            if (parts.Length > 1) directShare = parts[1];
            Host = host; // 回填规范化主机名
        }

        StatusMessage = $"正在连接 {host} …";
        try
        {
            _lastPassword = pwdBox?.Password; // 暂存供传输阶段建会话
            var checker = new PreflightChecker(_appLog);
            // 粘贴了 \\IP\共享名 时，把它作为源路径提示传给预检——无 IPC$ 的目标靠它完成直连验证
            var hintSources = directShare != null
                ? new List<string> { $@"\\{host}\{directShare}" }
                : new List<string>();
            var report = await Task.Run(() => checker.RunAsync(host,
                string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
                string.IsNullOrWhiteSpace(pwdBox?.Password) ? null : pwdBox!.Password, hintSources, null,
                benchmark: BenchmarkOnConnect));

            // 重新连接：把共享建成树根节点（勾选=整盘，展开=精确到目录/文件；级联三态）
            _browseVersion++; // 使旧的懒加载结果作废
            Shares.Clear();
            DirTree.Clear();
            foreach (var sh in report.Shares)
            {
                // 同盘共享已被枚举层合并：如 E（共享）与 E$（管理共享）同指 E:\ 时只剩一个节点
                var typeText = sh.IsAdminShare ? "管理共享" : "共享";
                var aliasNote = sh.Aliases.Count > 0 ? $"，同盘 {string.Join("、", sh.Aliases)} 已合并" : "";
                var row = new ShareRow
                {
                    IsSelected = !sh.IsAdminShare || sh.Name is "D$" or "E$",
                    Name = sh.Name, UncPath = sh.UncPath, Remark = sh.Remark,
                    TypeText = typeText + aliasNote
                };
                Shares.Add(row);
                var root = new DirNode
                {
                    Name = $"{sh.Name}（{typeText}{aliasNote}）",
                    FullPath = sh.UncPath,
                    IsShareRoot = true
                };
                // 根勾选态 ↔ ShareRow.IsSelected（仅“全勾”才算整盘迁移）
                root.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(DirNode.IsChecked))
                        row.IsSelected = root.IsChecked == true;
                };
                root.AddDummy();
                root.SetCheckedSilent(row.IsSelected);
                DirTree.Add(root);
            }

            var fails = report.Checks.Where(c => !c.Pass && c.Severity == "Error").ToList();
            StatusMessage = report.OverallPass
                ? $"连接成功，发现 {report.Shares.Count} 个共享。勾选共享=整盘迁移；点 ▶ 展开可精确勾选目录/文件。"
                : $"连接存在问题：{fails.FirstOrDefault()?.Detail ?? "见日志"}";

            // ---- 超大规模自动检测（零枚举）：用 GetDiskFreeSpaceEx 读各共享已用字节数，
            //      任一共享已用 ≥2TB 即自动开启超大数据模式（用户可手动取消）----
            try
            {
                var thresholdBytes = (long)Math.Max(1, MigrationMatrix.Load(null, _appLog).ExpertModeAutoThresholdGB) * 1024 * 1024 * 1024;
                long maxUsed = 0; string? maxShare = null;
                foreach (var sh in report.Shares)
                {
                    var unc = sh.UncPath.TrimEnd('\\') + "\\";
                    if (NativeMethods.GetDiskFreeSpaceEx(unc, out _, out var total, out var free) && total > 0)
                    {
                        var used = (long)(total - free);
                        if (used > maxUsed) { maxUsed = used; maxShare = sh.Name; }
                    }
                }
                if (maxUsed >= thresholdBytes && !ExpertMode)
                {
                    ExpertMode = true;
                    StatusMessage = $"检测到共享 {maxShare} 已用 {Format.Bytes(maxUsed)}，属超大规模——已自动开启「超大数据模式」（排除规则输入；不需要可手动取消）。";
                    _appLog.Information("共享 {Share} 已用 {Used} ≥ 阈值 {Th}，自动开启超大数据模式", maxShare, Format.Bytes(maxUsed), Format.Bytes(thresholdBytes));
                }
            }
            catch { /* 容量探测失败不影响连接 */ }

            // 直接粘贴 \\IP\共享名 的情况：自动勾选并展开该共享
            if (directShare != null)
            {
                var match = Shares.FirstOrDefault(s => s.Name.Equals(directShare, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    match.IsSelected = true;
                    var root = DirTree.FirstOrDefault(n => n.FullPath.Equals(match.UncPath, StringComparison.OrdinalIgnoreCase));
                    if (root != null)
                    {
                        root.IsChecked = true;
                        await EnsureChildrenAsync(root);
                        root.IsExpanded = true;
                    }
                    StatusMessage = $"已定位到共享 {match.UncPath}，可展开目录树精确选择";
                }
                else
                {
                    // 枚举列表里没有（或枚举根本失败：对方无 IPC$）——直接探测该共享是否可访问
                    var unc = $"\\\\{host}\\{directShare}";
                    var reachable = false;
                    try { reachable = await Task.Run(() => Directory.Exists(unc)); } catch { /* 视为不可达 */ }
                    if (reachable)
                    {
                        var root = AddShareNode(directShare, unc, "共享（手动指定，对方未开放枚举）", true);
                        root.IsChecked = true;
                        await EnsureChildrenAsync(root);
                        root.IsExpanded = true;
                        StatusMessage = $"自动列出共享失败，但已直接定位到 {unc}（可展开精确勾选）。这正是无 IPC$ 环境的推荐用法。";
                        _appLog.Information("手动指定共享直连成功: {Unc}", unc);
                    }
                    else
                    {
                        StatusMessage += $"（注意：未发现名为 {directShare} 的共享，且直接访问 {unc} 失败——请确认共享名拼写、对方权限，以及是否已在资源管理器里用凭据连过该机）";
                    }
                }
            }
            else if (Shares.Count == 0)
            {
                // 一个共享都没列出来（多为对方无 IPC$/枚举被拦截）：给出明确出路
                StatusMessage += " 未能自动列出共享——在②区顶部「共享名」框输入旧电脑上的共享名（如 d 或 D$）点「＋添加共享」；共享名就是资源管理器地址栏 \\\\IP\\ 后面的那个名字。";
            }
            if (string.IsNullOrWhiteSpace(TargetRoot))
                TargetRoot = $"D:\\Migrated\\{Host.Trim()}";
            RefreshExistingJobs();

            // ---- 断点提醒：该主机有未完成任务 → 主动弹窗询问续传 ----
            await PromptResumeIfAnyAsync(null);

            // 连接成功且已有共享 → 直接进「选择数据与目标」页，少点一次
            if (Shares.Count > 0 && CurrentStep == 0) CurrentStep = 1;
        }
        catch (Exception ex)
        {
            StatusMessage = $"连接失败：{ex.Message}";
            _appLog.Error(ex, "连接失败 {Host}", Host);
        }
    }

    /// <summary>把共享建成树根节点（勾选=整盘，展开=精确到目录/文件；级联三态；根勾选态同步到 Shares 行）。</summary>
    private DirNode AddShareNode(string name, string unc, string typeText, bool isSelected)
    {
        var row = new ShareRow
        {
            IsSelected = isSelected,
            Name = name, UncPath = unc, Remark = "",
            TypeText = typeText
        };
        Shares.Add(row);
        var root = new DirNode
        {
            Name = $"{name}（{typeText}）",
            FullPath = unc,
            IsShareRoot = true
        };
        root.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DirNode.IsChecked))
                row.IsSelected = root.IsChecked == true;
        };
        root.AddDummy();
        root.SetCheckedSilent(isSelected);
        DirTree.Add(root);
        return root;
    }

    /// <summary>手动添加共享：对方不开放共享枚举（无 IPC$）时，输入共享名直接探测定位并勾选。</summary>
    private async Task AddManualShareAsync(object? param = null)
    {
        var host = Host?.Trim() ?? "";
        // 主机框可能粘着 \\IP\共享：拆出主机
        if (host.StartsWith(@"\\"))
        {
            var ps = host.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (ps.Length > 0) host = ps[0];
        }
        if (string.IsNullOrEmpty(host)) { StatusMessage = "请先在①输入旧电脑的 IP 或电脑名"; return; }

        var name = ManualShareName?.Trim().Trim('\\') ?? "";
        // 共享名框里也允许粘 \\IP\共享
        if (name.StartsWith(@"\\"))
        {
            var ps = name.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            name = ps.Length >= 2 ? ps[1] : (ps.Length == 1 ? ps[0] : "");
        }
        if (string.IsNullOrEmpty(name)) { StatusMessage = "请输入共享名（旧电脑上共享出来的名字，如 d 或 D$）"; return; }

        var unc = $@"\\{host}\{name}";
        if (Shares.Any(s => s.UncPath.Equals(unc, StringComparison.OrdinalIgnoreCase)))
        { StatusMessage = $"{unc} 已在列表中"; return; }

        // 若①填了凭据，先显式建到该共享的会话（SMB 协议：能连上即证明凭据有效）——
        // 不再依赖"先去资源管理器连一次"。刻意不 Dispose：WNet 会话保持到退出，供目录浏览与传输复用。
        var pwdBox = param as System.Windows.Controls.PasswordBox;
        var credUser = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim();
        var credPwd = string.IsNullOrWhiteSpace(pwdBox?.Password) ? null : pwdBox!.Password;

        StatusMessage = $"正在探测 {unc} …";
        bool reachable; string? probeErr = null;
        if (credUser != null)
        {
            try
            {
                reachable = await Task.Run(() =>
                {
                    NetworkShare.ConnectForTransfer(host, credUser, credPwd, new List<string> { unc }, _appLog);
                    try { return Directory.Exists(unc); } catch { return false; }
                });
                if (reachable) { _lastPassword = credPwd; _appLog.Information("手动添加共享（显式凭据建会话成功）: {Unc}", unc); }
            }
            catch (Exception ex) { reachable = false; probeErr = ex.Message; }
        }
        else
        {
            reachable = await Task.Run(() => { try { return Directory.Exists(unc); } catch { return false; } });
        }
        if (!reachable)
        {
            StatusMessage = probeErr != null
                ? $"连接 {unc} 失败：{probeErr}"
                : $"访问 {unc} 失败：① 核对共享名拼写（就是资源管理器地址栏 \\{host}\\ 后面的那个名字）；② 若需要凭据，请先在①填用户名密码再点「＋添加共享」。";
            return;
        }

        var rootNode = AddShareNode(name, unc, "手动指定", true);
        rootNode.IsChecked = true;
        await EnsureChildrenAsync(rootNode);
        rootNode.IsExpanded = true;
        ManualShareName = "";
        StatusMessage = $"已添加并勾选 {unc}，可展开目录树精确选择。";
        _appLog.Information("手动添加共享成功: {Unc}", unc);
    }

    private void BrowseTarget()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择目标目录（新电脑接收数据的位置）" };
        if (dlg.ShowDialog() == true) TargetRoot = dlg.FolderName;
    }

    private async Task PrepareAsync(PasswordBox? pwdBox)
    {
        // 稳定性：迁移进行中不允许重新预检——会换掉 _ctx、重写 Job 状态，属于自伤操作
        if (IsRunning)
        {
            StatusMessage = "迁移正在进行中：请先「暂停」或「停止」，再重新预检。";
            return;
        }

        var wholeShares = Shares.Where(s => s.IsSelected).Select(s => s.UncPath).ToList();
        var customs = ExpertMode ? new List<string>() : CollectCustomSelections();
        if (wholeShares.Count == 0 && customs.Count == 0)
        {
            StatusMessage = ExpertMode
                ? "请在上方勾选要迁移的共享（整盘）"
                : "请在目录树勾选要迁移的内容：勾共享=整盘迁移；展开共享可精确勾选目录或单个文件";
            return;
        }
        if (string.IsNullOrWhiteSpace(TargetRoot)) { StatusMessage = "请填写目标路径"; return; }
        // ---- 目标路径前置校验：含非法字符/已存在同名文件 → 明确阻断（傻瓜操作防线）----
        if (TargetRoot.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            StatusMessage = $"目标路径包含非法字符（不能含 \" < > | 等）：{TargetRoot}";
            return;
        }
        try
        {
            var fullTarget = Path.GetFullPath(TargetRoot.Trim());
            if (File.Exists(fullTarget))
            {
                StatusMessage = $"目标位置已存在一个同名文件而不是文件夹：{fullTarget}。请换一个目标目录。";
                return;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"目标路径无效：{ex.Message}";
            return;
        }

        // ---- 同主机+同目标已有未完成任务 → 弹窗询问续传或新建 ----
        try
        {
            var dup = new JobManager(_appLog).FindUnfinished(Host.Trim(), TargetRoot.Trim())
                .FirstOrDefault(x => File.Exists(Path.Combine(x.JobDir, "plan.json")));
            if (dup != null)
            {
                // 必须指定 Owner：不指定时该 MessageBox 会禁用本线程所有窗口，却可能显示在主窗口后面，
                // 用户看到的现象就是“所有按钮都点不动了”——这正是本轮实测反馈里最迷惑人的一条。
                var r = AppDialog.Show(Application.Current?.MainWindow,
                    $"该源电脑和目标路径下已有未完成的任务：\n\n任务：{dup.JobId}\n进度：{(dup.StateUnreliable ? "状态文件损坏，未知（以回执为准）" : $"已传 {dup.Percent:0.0}%")}\n状态：{dup.PhaseText}\n\n[是] 继续上次任务（已完成部分不重传）\n[否] 创建全新任务",
                    "发现未完成任务", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r == MessageBoxResult.Yes)
                {
                    await ResumeExistingJobAsync(dup);
                    return;
                }
            }
        }
        catch (Exception ex) { _appLog.Warning(ex, "未完成任务检测失败"); }

        StatusMessage = "正在预检…";
        PlanReady = false;
        try
        {
            var sources = wholeShares.Select(s => new SourceSpec { Path = s, Kind = ObjectKind.DataVolume }).ToList();
            // 自定义选择涉及的共享根也登记进 Sources（供 Planner 做相对路径映射与 Preflight 检查）
            foreach (var root in customs.Select(FindShareRootOf).Where(r => r != null).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!sources.Any(s => string.Equals(s.Path, root, StringComparison.OrdinalIgnoreCase)))
                    sources.Add(new SourceSpec { Path = root!, Kind = ObjectKind.DataVolume });

            var def = new JobDefinition
            {
                SourceHost = Host.Trim(),
                SourceUser = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
                TargetRoot = TargetRoot.Trim(),
                Sources = sources,
                CustomSelections = customs,
                CreatedBy = $"{Environment.UserDomainName}\\{Environment.UserName}"
            };
            def.Options.Threads = SelectedMt; // robocopy /MT 线程数（存入 job.json，续传沿用）
            // 超大数据模式：排除规则入任务（扫描/传输/验证/续传同口径）
            if (ExpertMode)
                def.CustomExclusions = ExpertExclusions
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(l => !l.StartsWith('#'))
                    .ToList();

            var jm = new JobManager(_appLog);
            _ctx = jm.Create(def);
            var jobLog = LogBootstrap.CreateJobLogger(_ctx.JobDir, _ctx.JobId, _memorySink);

            var checker = new PreflightChecker(jobLog);
            var pre = await Task.Run(() => checker.RunAsync(def.SourceHost, def.SourceUser,
                string.IsNullOrWhiteSpace(pwdBox?.Password) ? null : pwdBox!.Password,
                sources.Select(s => s.Path).ToList(), def.TargetRoot, benchmark: BenchmarkOnConnect));
            _ctx.SavePreflight(pre);
            if (!pre.OverallPass)
            {
                // 失败明细逐条进左栏「提示」面板（一次看全，不用猜是哪一项没过）；
                // 带上时间戳：连续两次失败文字不同，界面才会刷新，用户能看出“确实又跑了一次”。
                var bad = pre.Checks.Where(c => !c.Pass && c.Severity == "Error").ToList();
                foreach (var c in bad.Take(8)) AddFailRow("预检 · " + c.Name, c.Detail);
                StatusMessage = $"预检未通过（{DateTime.Now:HH:mm:ss}，{bad.Count} 项不通过）：{(bad.FirstOrDefault()?.Detail ?? "详见左栏「提示」")}";
                return;
            }

            // 通过：清掉上一次预检留下的提示行（避免“已经过了还挂着旧报错”）
            var stale = FailItems.Where(r => r.Location.StartsWith("预检 · ", StringComparison.Ordinal)).ToList();
            foreach (var row in stale) { FailItems.Remove(row); _failIndex.Remove(row.Location); }

            var warns = pre.Checks.Where(c => !c.Pass && c.Severity == "Warning").ToList();
            StatusMessage = warns.Count > 0
                ? $"预检通过（{warns.Count} 项提醒不阻断：{warns[0].Name} —— {warns[0].Detail}）正在扫描源数据（大容量磁盘可能需要几分钟）…"
                : "预检通过，正在扫描源数据（大容量磁盘可能需要几分钟）…";
            var matrix = MigrationMatrix.Load(null, jobLog).WithExtraExclusions(def.CustomExclusions, jobLog);
            var scanner = new SourceScanner(jobLog);
            var observed = await scanner.ScanAsync(def, matrix,
                new Progress<string>(m => StatusMessage = m));

            _ctx.SaveObserved(observed);

            // ---- 扫描残缺闸门（v0.3.8，缺陷 4）：默认不允许在“扫描残缺”状态下生成可执行计划 ----
            if (!ConfirmIncompleteScan(_ctx))
            {
                _appLog.Warning("扫描存在不可访问位置，用户选择先不迁移（Job {JobId}）", _ctx.JobId);
                return;
            }

            var planner = new Planner(jobLog);
            var plan = planner.CreatePlan(def, observed, matrix);
            _ctx.SavePlan(plan);
            var st = _ctx.LoadStateOrNew();
            st.Phase = JobPhase.AwaitingReview;
            st.TotalObjects = plan.Objects.Count; st.TotalBytes = plan.TotalBytes;
            _ctx.SaveState(st);

            // ---- 迁移前主动提醒：高危锁文件 / EFS 加密（把「失败后报错」前移为「开始前预防」）----
            if (observed.LockRiskFiles > 0 || observed.EncryptedFiles > 0)
            {
                var warnText = "";
                if (observed.LockRiskFiles > 0)
                    warnText += $"检测到 {observed.LockRiskFiles} 个 Outlook 数据文件（.pst/.ost）。\n如果旧电脑上 Outlook 正在运行，这些文件会被锁定导致迁移失败。\n建议：先让旧电脑用户关闭 Outlook。\n\n";
                if (observed.EncryptedFiles > 0)
                    warnText += $"检测到 {observed.EncryptedFiles} 个 EFS 加密文件。\n内容可以正常复制，但在新机上将失去加密保护（明文可读）。\n\n";
                var r = AppDialog.Show(Application.Current?.MainWindow, warnText.TrimEnd() + "\n\n仍要继续吗？",
                    "迁移前提醒", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (r != MessageBoxResult.Yes)
                {
                    StatusMessage = "已取消。处理好上述文件后重新「预检并生成计划」。";
                    return;
                }
            }

            // ---- 目标盘空间守护：计划总量超过可用空间时弹窗确认 ----
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(def.TargetRoot));
                if (root != null && plan.TotalBytes > 0)
                {
                    var free = new DriveInfo(root).AvailableFreeSpace;
                    if (free < plan.TotalBytes)
                    {
                        var r = AppDialog.Show(Application.Current?.MainWindow,
                            $"目标盘 {root} 剩余空间不足（新建任务前置校验）：\n需要 {Format.Bytes(plan.TotalBytes)}，可用 {Format.Bytes(free)}。\n\n仍要继续吗？（写满时会记失败对象；释放空间后再点「恢复任务」即可补齐）",
                            "空间不足", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                        if (r != MessageBoxResult.Yes)
                        {
                            StatusMessage = "已取消：目标盘空间不足。清理空间或更换目标后重新「预检并生成计划」。";
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 静默吞掉会让用户以为"空间已经校验过"（T07-O2）——如实说明本次没做前置判断
                StatusMessage = $"⚠ 目标盘剩余空间预检未能完成（{ex.Message}）：本次不做空间前置判断，写满时会记失败对象，释放空间后可点「恢复任务」补齐。";
                _appLog.Warning(ex, "目标盘空间预检失败");
            }

            Objects.Clear();
            foreach (var o in plan.Objects)
                Objects.Add(ToRow(o));

            ProgressText = $"0 B / {Format.Bytes(plan.TotalBytes)}";
            ObjectText = $"{plan.Objects.Count}";
            PlanBytesText = Format.Bytes(plan.TotalBytes);
            ActualBytesText = "0 B";
            HasShortfall = false;
            IsFinished = false;
            DataLabel = "已传 / 计划";
            BalanceText = $"计划 {Format.Bytes(plan.TotalBytes)}　·　实际落盘 0 B";
            StatusMessage = $"计划已生成（Job { _ctx.JobId }）：{plan.Objects.Count} 个对象，共 {Format.Bytes(plan.TotalBytes)}。" +
                (observed.Warnings.Count > 0 ? $" ⚠ {observed.Warnings[0]}" : "点击「开始迁移」。");
            // 文件数只有扫描后才知道：超大规模时明确告知"全程流式、无文件数上限"
            if (observed.TotalFiles >= 5_000_000)
                StatusMessage += $"（文件量 {observed.TotalFiles:N0}，属超大规模——本任务传输/验证全程流式处理，无文件数上限）";
            PlanReady = true;
            OnPropertyChanged(nameof(HasJob));
                OnPropertyChanged(nameof(CanRepair));
            RefreshExistingJobs();
            CurrentStep = 2;   // 计划就绪 → 直接进「迁移进度」页
        }
        catch (Exception ex)
        {
            StatusMessage = $"准备失败：{ex.Message}";
            _appLog.Error(ex, "Prepare 失败");
        }
    }

    /// <summary>对象行：RootFiles（根目录散落文件）给人话标签，体积扫描未知时显示"传输时实测"。</summary>
    private static ObjectRow ToRow(PlannedObject o) => new()
    {
        ObjectId = o.ObjectId,
        SourcePath = o.Kind == ObjectKind.RootFiles ? $"{o.SourcePath}　［根目录散落文件］" : o.SourcePath,
        SizeText = o.EstimatedBytes < 0 ? "传输时实测" : Format.Bytes(o.EstimatedBytes)
    };

    private async Task RunTransferAsync()
    {
        CurrentStep = 2;   // 进「迁移进度」页
        await RunTransferCoreAsync();
    }

    private async Task RunTransferCoreAsync()
    {
        if (_ctx == null) return;
        await RunCoreAsync(_ctx);
    }

    private async Task ResumeAsync()
    {
        CurrentStep = 2;   // 进「迁移进度」页
        try
        {
            if (_ctx == null && SelectedExistingJob != null)
            {
                var jm = new JobManager(_appLog);
                _ctx = jm.Open(SelectedExistingJob.JobDir);
                Host = _ctx.Definition.SourceHost;
                TargetRoot = _ctx.Definition.TargetRoot;
                Objects.Clear();
                foreach (var o in _ctx.Plan?.Objects ?? Enumerable.Empty<PlannedObject>())
                    Objects.Add(ToRow(o));
                RefreshRowsFromReceipts();
            }
            if (_ctx == null) { StatusMessage = "请选择要恢复的任务"; return; }
            _ctx.ClearPauseRequest();
            await RunCoreAsync(_ctx);
        }
        catch (Exception ex) { StatusMessage = $"恢复失败：{ex.Message}"; _appLog.Error(ex, "Resume 失败"); }
    }

    private async Task RunCoreAsync(JobContext ctx)
    {
        // 暂停/停止后的进程收尾有数秒窗口，恢复撞锁是实测过的坑——给 30 秒宽限重试
        using var jobLock = JobLock.TryAcquire(ctx, TimeSpan.FromSeconds(30), out var lockReason);
        if (jobLock == null) { StatusMessage = lockReason; return; }

        IsRunning = true;
        PlanReady = false;
        OnPropertyChanged(nameof(HasJob));
                OnPropertyChanged(nameof(CanRepair));
        _runCts = new CancellationTokenSource();
        var jobLog = LogBootstrap.CreateJobLogger(ctx.JobDir, ctx.JobId, _memorySink);
        var matrix = MigrationMatrix.Load(null, jobLog);
        var orchestrator = new TransferOrchestrator(ctx, matrix, jobLog);
        orchestrator.OutputLine += OnTransferLine;
        orchestrator.FileCopied += OnLiveFileCopied;   // 实时文件流
        orchestrator.TransferNotice += OnTransferNotice; // 引擎主动提示（速度异常等）
        FailItems.Clear();
        _failIndex.Clear();
        RefreshFailHeader();
        CurrentObjectDetail = "";
        HasCurrentObjectDetail = false;
        StallHintText = "";
        HasStallHint = false;

        // ---- 扫描残缺闸门（v0.3.8，缺陷 4）：扫描时有不可访问目录 → 默认不允许开跑 ----
        if (!ConfirmIncompleteScan(ctx)) return;

        // ---- 传输前显式建立 SMB 会话（不依赖 Windows 恰好还缓存着上次的会话）----
        var effUser = !string.IsNullOrWhiteSpace(Username) ? Username.Trim() : ctx.Definition.SourceUser;
        IDisposable? session = null;
        if (!string.IsNullOrEmpty(effUser))
        {
            if (string.IsNullOrEmpty(_lastPassword))
            {
                StatusMessage = $"该任务使用账号 {effUser}。请先在上方输入密码，再点「开始迁移 / 恢复任务」。" +
                    "（不输凭据仅在 Windows 还缓存着该连接时可行，缓存约 15 分钟过期）";
                IsRunning = false; PlanReady = true;
                OnPropertyChanged(nameof(HasJob));
                OnPropertyChanged(nameof(CanRepair));
                return;
            }
            try
            {
                // IPC$ 不通的目标（精简/第三方 SMB）会自动退为直连源共享；已有可用连接时靠现有连接跑
                session = NetworkShare.ConnectForTransfer(ctx.Definition.SourceHost, effUser, _lastPassword,
                    ctx.Definition.Sources.Select(s => s.Path).ToList(), jobLog);
            }
            catch (Exception ex)
            {
                StatusMessage = $"连接失败：{ex.Message}";
                IsRunning = false; PlanReady = true;
                OnPropertyChanged(nameof(HasJob));
                OnPropertyChanged(nameof(CanRepair));
                return;
            }
        }

        var progress = new Progress<ProgressSnapshot>(s =>
        {
            // 传输结束（含"有失败"）即 100%：进度表示"这次传输结束了"，
            // 差多少数据由「计划 / 实际落盘」说明，不再让进度条因为有失败项卡在 79%。
            var finished = s.Phase is JobPhase.Completed or JobPhase.CompletedWithErrors;
            IsFinished = finished;
            DataLabel = finished ? "实际落盘 / 计划" : "已传 / 计划";
            Percent = finished ? 100.0 : s.Percent;
            ProgressText = $"{Format.Bytes(s.CompletedBytes)} / {Format.Bytes(s.TotalBytes)}";
            PlanBytesText = Format.Bytes(s.TotalBytes);
            ActualBytesText = Format.Bytes(s.CompletedBytes);
            _lastProgressUtc = DateTime.UtcNow;
            EngineSpeedText = s.BytesPerSecond > 0 ? Format.Speed(s.BytesPerSecond) : "—";
            EtaText = !double.IsNaN(s.EtaSeconds) ? Format.Eta(s.EtaSeconds) : "—";
            ObjectText = $"{s.CompletedObjects}/{s.TotalObjects}" + (s.FailedObjects > 0 ? $"（失败 {s.FailedObjects}）" : "");
            FailedObjects = s.FailedObjects;

            // ---- v0.3.8（缺陷 3）：暂停必须说清"完成了多少对象、还剩多少没传、怎么续传" ----
            if (s.Phase == JobPhase.Paused)
            {
                var remainPercent = Math.Max(0, 100.0 - s.Percent);
                StatusMessage = $"‖ 已暂停：已完成 {s.CompletedObjects}/{s.TotalObjects} 个对象，剩余 {remainPercent:0.#}% 未传" +
                    $"（{Format.Bytes(Math.Max(0, s.TotalBytes - s.CompletedBytes))}）——可用「恢复任务」续传（已完成的对象不会重传）。";
            }
            else
            {
                StatusMessage = s.Message;
            }

            // ---- 当前对象 + 停滞提示（"界面长时间不动"是用户判断卡死的直接原因）----
            if (!string.IsNullOrEmpty(s.CurrentObjectPath) && s.Phase == JobPhase.Running)
            {
                CurrentObjectDetail = $"当前对象：{s.CurrentObjectPath}，已传 {Format.Bytes(s.CurrentObjectDoneBytes)}" +
                    (s.CurrentObjectTotalBytes > 0 ? $"/{Format.Bytes(s.CurrentObjectTotalBytes)}" : "");
                HasCurrentObjectDetail = true;
                var stalled = s.StallSeconds >= TransferOrchestrator.StallWarnSeconds;
                StallHintText = stalled
                    ? $"⏳ 已 {s.StallSeconds:0} 秒没有新的落盘字节（不是卡死：引擎仍在等待源端/正在写入）。" +
                      (s.CurrentObjectHasLargeFile ? "大文件可能数分钟无进度变化，请勿终止。" : "若持续过久请查看日志面板。")
                    : s.CurrentObjectHasLargeFile
                        ? "该对象含大文件（≥分流阈值）：复制期间可能数分钟无进度变化，属正常，请勿终止。"
                        : "";
                HasStallHint = StallHintText.Length > 0;
            }
            if (s.CompletedObjects != _lastCompletedCount)
            {
                _lastCompletedCount = s.CompletedObjects;
                RefreshRowsFromReceipts();
            }
            ApplyObjectRowStatus(s);
        });

        StartLiveFeed(); // 会话已建立、即将开跑：启动实时文件流节拍器
        try
        {
            var phase = await orchestrator.RunAsync(progress, _runCts.Token);
            var endState = ctx.LoadStateOrNew();
            var spaceShort = endState.LastError?.Contains("空间不足") == true;
            StatusMessage = phase switch
            {
                JobPhase.Completed => "✔ 迁移完成！建议点「验证」确认数据一致，然后「打开报告」。",
                JobPhase.CompletedWithErrors when spaceShort =>
                    "◐ 目标磁盘空间不足，本次运行已提前停止（避免对每个文件反复重试）。" +
                    "请释放目标盘空间后点「恢复任务」——已完成的对象不会重传。",
                JobPhase.CompletedWithErrors => "◐ 完成但有失败对象（左栏已红色逐条列出「对象号 + 路径 + 退出码译文」），请打开报告核对；修好后点「恢复任务」只补差异，已完成的对象不会重传。",
                JobPhase.Paused => "‖ 已暂停：已完成 " + endState.CompletedObjects + "/" + endState.TotalObjects + " 个对象，剩余 " +
                    Math.Max(0, 100.0 - endState.Percent).ToString("0.#") + "% 未传（" +
                    Format.Bytes(Math.Max(0, endState.TotalBytes - endState.CompletedBytes)) +
                    "）——点「恢复任务」续传，已完成的对象不会重传。",
                JobPhase.Interrupted => "⏸ 已中断（可续传）。点「恢复任务」继续。",
                JobPhase.Failed => $"✘ 迁移失败：{ctx.LoadStateOrNew().LastError ?? "详见日志"}",
                _ => $"阶段结束：{phase}"
            };

            // 结束态：进度条走满，并把"实际落盘量"按目标侧实测补齐
            // （失败对象 robocopy 已经拷进去的那部分也算实际落盘；不补测就会显示成
            //   "少传了 8.6GB"，而真相是只缺了几十个文件）
            var done = phase is JobPhase.Completed or JobPhase.CompletedWithErrors;
            IsFinished = done;
            if (done) Percent = 100.0;
            DataLabel = done ? "实际落盘 / 计划" : "已传 / 计划";
            var planBytes = PlanBytesOf();
            ApplyDataBalance(ComputeActualBytes(_ctx, measureMissing: false), planBytes);
            if (done)
            {
                var measured = await Task.Run(() => ComputeActualBytes(_ctx, measureMissing: true));
                ApplyDataBalance(measured, planBytes);
            }
        }
        catch (OperationCanceledException) { StatusMessage = "已取消（可续传）。"; }
        catch (Exception ex) { StatusMessage = $"传输异常：{ex.Message}"; _appLog.Error(ex, "传输异常"); }
        finally
        {
            orchestrator.OutputLine -= OnTransferLine;
            orchestrator.FileCopied -= OnLiveFileCopied;
            orchestrator.TransferNotice -= OnTransferNotice;
            StopLiveFeed();
            session?.Dispose();
            CurrentObjectDetail = "";
            HasCurrentObjectDetail = false;
            StallHintText = "";
            HasStallHint = false;
            IsRunning = false;
            PlanReady = true;
            _lastCompletedCount = -1;
            RefreshRowsFromReceipts();
            RefreshExistingJobs();
            OnPropertyChanged(nameof(HasJob));
                OnPropertyChanged(nameof(CanRepair));
        }
    }

    // ══════════════════ 计划量 / 实际落盘量对账 ══════════════════

    /// <summary>
    /// 选中「已有任务」时把该任务的对账数字显示出来：结束过的任务（含"有失败"）进度条直接走满，
    /// 并给出 计划 / 实际落盘 两个数据量。轻量部分同步算，失败对象的补测放后台，不卡界面。
    /// </summary>
    private void PreviewExistingJob(string jobDir)
    {
        try
        {
            var ctx = new JobManager(_appLog).Open(jobDir);
            var st = ctx.LoadStateOrNew();
            // 顶栏"源 → 目标"跟着选中的任务走（空着才回填，不覆盖用户手输的新任务参数）
            if (string.IsNullOrWhiteSpace(Host)) Host = ctx.Definition.SourceHost;
            if (string.IsNullOrWhiteSpace(TargetRoot)) TargetRoot = ctx.Definition.TargetRoot;
            // 视图不可信（T14/O6）：状态文件损坏/缺失时，这里的 0.0% 与"对象 0/0"会把用户和脚本
            // 一起带偏（上一轮实测：其实已传 45%，界面写 0.0%）。这时明确写"待由回执重建"。
            var unreliable = ctx.StateViewUnreliable;
            var liveHolder = JobManager.IsLocked(ctx.JobDir);
            var stale = !unreliable && PhaseView.IsStale(st.Phase, liveHolder, ctx.HasReceipts);
            var done = st.Phase is JobPhase.Completed or JobPhase.CompletedWithErrors;
            IsFinished = done;
            Percent = done ? 100.0 : (unreliable ? 0 : (st.TotalBytes > 0 ? st.Percent : 0));
            DataLabel = done ? "实际落盘 / 计划" : "已传 / 计划";
            if (unreliable)
            {
                ProgressText = "状态损坏，待由回执重建";
                ObjectText = "未知（以回执为准）";
                StatusMessage = "⚠ 状态文件 job-state.json 损坏或缺失，进度无法显示（故意不写 0.0%，以免被当成「一点没传」）。" +
                                "点「恢复任务」会按回执重建进度，已完成的对象不会重传。";
            }
            else
            {
                if (st.TotalBytes > 0)
                    ProgressText = $"{Format.Bytes(st.CompletedBytes)} / {Format.Bytes(st.TotalBytes)}";
                ObjectText = $"{st.CompletedObjects}/{st.TotalObjects}" + (st.FailedObjects > 0 ? $"（失败 {st.FailedObjects}）" : "");
                if (stale)
                    StatusMessage = $"⏸ 状态文件里记的是 {(st.Phase == JobPhase.Running ? "Running" : "AwaitingReview")}，" +
                                    "但没有任何进程在跑（job.lock 无人持有）——按「已中断（可续传）」显示，点「恢复任务」即可继续。";
            }
            FailedObjects = unreliable ? 0 : st.FailedObjects;
            if (st.BytesPerSecond > 0) EngineSpeedText = Format.Speed(st.BytesPerSecond);

            // 选中已有任务时，左栏「提示」也跟着切到这个任务的失败明细（选中任务即可见失败清单）
            if (!IsRunning)
            {
                FailItems.Clear();
                _failIndex.Clear();
                RefreshFailHeader();
                var latest = ctx.LoadReceipts(_appLog)
                    .GroupBy(r => r.ObjectId, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderBy(r => r.CompletedUtc).Last())
                    .Where(r => r.Status is ObjectStatus.Failed or ObjectStatus.CompletedWithErrors or ObjectStatus.Interrupted)
                    .OrderBy(r => r.ObjectId);
                foreach (var r in latest)
                {
                    var loc = FriendlyPath(r.TargetPath);
                    // v0.3.8：失败对象必须“对象号 + 路径 + 退出码译文”逐条可见（16 = 严重错误，一个文件都没传）
                    if (r.Status == ObjectStatus.Failed)
                        AddFailRow($"✘ 对象失败　{r.ObjectId}　{loc}",
                            ErrorTranslator.FailureHeadline(r.ObjectId, loc, r.RobocopyExitCodeBulk, r.RobocopyExitCodeLarge) +
                            "｜" + ErrorTranslator.ShortReason(r.ErrorDetail), objectLevel: true);
                    else
                        AddFailRow(loc, r.ErrorDetail ?? $"{r.Status}（{r.ObjectId}）");
                }
            }

            var plan = st.TotalBytes > 0 ? st.TotalBytes : (ctx.Plan?.TotalBytes ?? 0);
            ApplyDataBalance(ComputeActualBytes(ctx, measureMissing: false), plan);
            if (done && plan > 0)
            {
                var ctx2 = ctx;
                _ = Task.Run(() =>
                {
                    var measured = ComputeActualBytes(ctx2, measureMissing: true);
                    var disp = System.Windows.Application.Current?.Dispatcher;
                    if (disp != null) disp.Invoke(() => ApplyDataBalance(measured, plan));
                });
            }
        }
        catch (Exception ex) { _appLog.Warning(ex, "载入已有任务对账预览失败"); }
    }

    /// <summary>
    /// 扫描残缺闸门（v0.3.8，缺陷 4）：扫描期存在不可访问目录时，默认**不允许**开始迁移。
    /// 返回 false = 用户选择“先修好再迁移”（不进入传输）。用户显式确认时把结论写进 job.json，续传不再拦。
    /// 只决定“是否放行”，不改任何扫描/传输逻辑。
    /// </summary>
    private bool ConfirmIncompleteScan(JobContext ctx)
    {
        var gate = ScanGate.Inspect(ctx);
        if (!gate.HasIncomplete) return true;

        // 明细同时喂进左栏报错区：弹窗可能被关掉，清单不会
        foreach (var p in gate.Paths.Take(20))
            AddFailRow("扫描不可访问 · " + (p.Split('｜').FirstOrDefault() ?? p), p);
        if (gate.Acknowledged) return true;

        var r = AppDialog.Show(Application.Current?.MainWindow,
            ScanGate.BuildBlockMessage(gate, cli: false),
            "扫描不完整：默认不允许开始迁移", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (r != MessageBoxResult.Yes)
        {
            StatusMessage = $"已阻止开始：扫描存在 {gate.Count} 处不可访问的目录/文件（左栏已逐条列出）。" +
                            "请修好权限/网络后重新「预检并生成计划」；确知风险仍要继续，请再点一次「开始迁移」并在弹窗选「是」。";
            _appLog.Warning("扫描残缺闸门拦截：{Count} 处不可访问，用户选择先不迁移", gate.Count);
            return false;
        }

        ctx.Definition.AllowIncompleteScan = true;
        ctx.SaveDefinition();
        _appLog.Warning("用户显式确认：扫描存在 {Count} 处不可访问位置，仍继续迁移（job.json allowIncompleteScan=true）", gate.Count);
        AppendLog($"[警告] 你已确认在扫描不完整（{gate.Count} 处不可访问）的情况下继续迁移；这些位置的数据不会被复制。");
        return true;
    }

    /// <summary>计划总字节：优先取任务状态里记录的（与进度条同源），退化为计划文件。</summary>
    private long PlanBytesOf()
    {
        try
        {
            var st = _ctx?.LoadStateOrNew();
            if (st != null && st.TotalBytes > 0) return st.TotalBytes;
        }
        catch { /* 状态文件损坏时退化为计划文件 */ }
        return _ctx?.Plan?.TotalBytes ?? 0;
    }

    /// <summary>
    /// 实际落盘字节 = 各对象目标侧实测字节之和（取每个对象最新回执）。
    /// 失败对象的回执没有实测值，measureMissing=true 时就地枚举其目标目录补上——
    /// 只读枚举，可在后台线程调用。这样"实际"含 robocopy 已拷进去的部分，
    /// 与"计划"的差额才是真正没落盘的数据。
    /// </summary>
    private long ComputeActualBytes(JobContext? ctx, bool measureMissing)
    {
        if (ctx == null) return 0;
        var latest = new Dictionary<string, ObjectReceipt>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var r in ctx.LoadReceipts(_appLog))
                if (!latest.TryGetValue(r.ObjectId, out var cur) || r.CompletedUtc >= cur.CompletedUtc)
                    latest[r.ObjectId] = r;
        }
        catch (Exception ex) { _appLog.Warning(ex, "读取回执用于数据量对账失败"); }

        long actual = 0;
        foreach (var o in ctx.Plan?.Objects ?? Enumerable.Empty<PlannedObject>())
        {
            if (!latest.TryGetValue(o.ObjectId, out var r)) continue;
            var bytes = r.TargetBytes;
            if (bytes <= 0)
            {
                // 失败对象回执没记实测值：优先用这次任务自己的 robocopy 汇总（总数-失败），
                // 它反映"本次任务到底落了多少"，且不受事后清理目标目录影响；
                // 解析不出来再退化为现场枚举目标目录。
                bytes = LandedFromRoboLog(ctx, o.ObjectId);
                if (bytes <= 0 && measureMissing && !string.IsNullOrWhiteSpace(o.TargetPath) && Directory.Exists(o.TargetPath))
                {
                    try { bytes = DirStat.Measure(o.TargetPath, long.MaxValue).Bytes; }
                    catch (Exception ex) { _appLog.Warning(ex, "补测目标目录 {Path} 失败", o.TargetPath); bytes = 0; }
                }
            }
            if (bytes > 0) actual += bytes;
        }
        return actual;
    }

    private static readonly Regex s_byteSumRx =
        new(@"(\d+(?:\.\d+)?)\s*([kmgt]?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 失败对象的"实际落盘"：读该对象 robocopy 日志（/UNILOG 追加写，UTF-16）最后一个字节汇总行，
    /// 取 总数 − 失败 = 已落盘（复制 + 跳过已存在）。用任务自身的证据，事后清理目标目录也不影响口径。
    /// </summary>
    private long LandedFromRoboLog(JobContext ctx, string objectId)
    {
        try
        {
            if (!Directory.Exists(ctx.RoboLogsDir)) return 0;
            var file = Directory.GetFiles(ctx.RoboLogsDir, objectId + "*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (file == null) return 0;
            // robocopy 汇总块结构固定：目录 / 文件 / 字节 三行，每行恰好 6 个数值；
            // 末尾的单行（如"已结束: 2026年9月15日 12:06:12"）也恰好 6 个数字，所以不能取"最后一个"，
            // 必须找连续 6 数值行构成的块，取第三行 = 字节行。这样不依赖日志编码
            // （/TEE 重定向出来的是本地代码页、无 BOM），也不依赖中英文界面。
            var tail = File.ReadLines(file, Encoding.UTF8).TakeLast(80).ToList();
            var bestStart = -1;
            var bestLen = 0;
            var curStart = 0;
            var curLen = 0;
            for (var i = 0; i < tail.Count; i++)
            {
                if (s_byteSumRx.Matches(tail[i]).Count == 6)
                {
                    if (curLen == 0) curStart = i;
                    curLen++;
                    if (curLen > bestLen) { bestLen = curLen; bestStart = curStart; }
                }
                else curLen = 0;
            }
            if (bestLen < 3 || bestStart + 2 >= tail.Count) return 0;
            var hit = tail[bestStart + 2];
            var m = s_byteSumRx.Matches(hit);
            var total = ScaleBytes(m[0]);
            var failed = ScaleBytes(m[4]);
            return Math.Max(0, total - failed);
        }
        catch (Exception ex) { _appLog.Warning(ex, "解析 robocopy 字节汇总失败: {ObjectId}", objectId); return 0; }
    }

    private static long ScaleBytes(Match m)
    {
        var v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var mult = m.Groups[2].Value.ToLowerInvariant() switch
        {
            "k" => 1024d,
            "m" => 1024d * 1024,
            "g" => 1024d * 1024 * 1024,
            "t" => 1024d * 1024 * 1024 * 1024,
            _ => 1d
        };
        return (long)(v * mult);
    }

    /// <summary>UI 线程：把对账结果写入界面。</summary>
    private void ApplyDataBalance(long actual, long plan)
    {
        PlanBytesText = plan > 0 ? Format.Bytes(plan) : "—";
        ActualBytesText = Format.Bytes(actual);
        // 0.1% 或 32MB 以内算口径噪声（过滤规则/占位符/长路径差异），超出就是真有数据没落盘
        var tolerance = Math.Max(plan / 1000, 32L * 1024 * 1024);
        HasShortfall = plan > 0 && actual + tolerance < plan;
        var failNote = _failedObjects > 0 ? $"（{_failedObjects} 个对象未完成，明细见报错清单）" : "";
        BalanceText = plan <= 0
            ? ""
            : HasShortfall
                ? $"计划 {Format.Bytes(plan)}　·　实际落盘 {Format.Bytes(actual)}　·　还差 {Format.Bytes(plan - actual)}{failNote}"
                : actual > plan + tolerance
                    ? $"计划 {Format.Bytes(plan)}　·　实际落盘 {Format.Bytes(actual)}（多于计划：该任务计划量按当时口径统计）"
                    : $"计划 {Format.Bytes(plan)}　·　实际落盘 {Format.Bytes(actual)}　·　数据已全部到位";
    }

    // ══════════════════ 报错区（失败文件一目了然） ═══════════════════

    private static readonly System.Text.RegularExpressions.Regex s_errLineRx =
        new(@"(?:错误|ERROR)\s+(?<code>\d+)\s+\(0x[0-9A-Fa-f]{8}\)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // ══════════════════ 实时文件流（"正在复制"可视化） ══════════════════

    public ObservableCollection<string> LiveFiles { get; } = new();

    private string _currentFileText = "（传输开始后，这里实时滚动正在复制的文件）";
    public string CurrentFileText { get => _currentFileText; set => Set(ref _currentFileText, value); }

    /// <summary>robocopy 泵线程回调：只入队（极廉价），UI 线程按 250ms 节拍批量渲染。</summary>
    private void OnLiveFileCopied(string path, long approxBytes)
    {
        // 显示成相对短路径：\\host\共享\目录\文件 → 共享\目录\文件
        var s = path;
        if (s.StartsWith(@"\\"))
        {
            var i = s.IndexOf('\\', 2);
            if (i > 0) s = s[(i + 1)..];
        }
        _liveQueue.Enqueue(s);
    }

    /// <summary>本机全部活动网卡的累计字节数（RX+TX）。网卡被禁用/重连时计数会回绕，调用方按 0 处理。</summary>
    private static long ReadNicTotalBytes()
    {
        long total = 0;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                var t = ni.NetworkInterfaceType;
                if (t == NetworkInterfaceType.Loopback || t == NetworkInterfaceType.Tunnel) continue;
                try
                {
                    var st = ni.GetIPStatistics();
                    total += st.BytesReceived + st.BytesSent;
                }
                catch { /* 个别虚拟网卡不支持统计 */ }
            }
        }
        catch { /* 枚举失败不影响主流程 */ }
        return total;
    }

    /// <summary>启动网卡吞吐采样（1 秒一拍，常驻；成本极低）。</summary>
    private void StartNicMonitor()
    {
        _nicLastTotal = ReadNicTotalBytes();
        _nicTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _nicTimer.Tick -= NicTimer_Tick;
        _nicTimer.Tick += NicTimer_Tick;
        _nicTimer.Start();
    }

    private void NicTimer_Tick(object? sender, EventArgs e)
    {
        var now = ReadNicTotalBytes();
        var delta = now - _nicLastTotal;
        _nicLastTotal = now;

        // 只在真正"在传"时显示：本机网卡吞吐包含其它程序（浏览器/更新/office）的流量，
        // 没在迁移（空闲/暂停/已结束）时数字仍会跳动，那不是迁移速度，只会让人误解 → 显示"—"并清空波形。
        // 判据用"引擎最近 6 秒内是否上报过进度"，这样暂停时也会自动静默。
        if (DateTime.UtcNow - _lastProgressUtc > TimeSpan.FromSeconds(6))
        {
            if (_nicHistory.Count > 0) _nicHistory.Clear();
            if (NicSparkPoints.Count > 0) NicSparkPoints.Clear();
            if (NicSpeedText != "—") NicSpeedText = "—";
            NicSparkTip = "网卡实时吞吐：开始迁移后显示（含本机其它程序的流量；「任务实测」才是本次迁移自己的速率）";
            return;
        }

        var bps = delta > 0 ? delta : 0;             // 计数器回绕/重置 → 记 0，不显示负数
        NicSpeedText = Format.Speed(bps);
        _nicHistory.Enqueue(bps);
        while (_nicHistory.Count > NicWindowSeconds) _nicHistory.Dequeue();
        RebuildNicSpark();
    }

    /// <summary>把最近 60 秒的采样画成折线：X 为时间轴（右端最新），Y 按窗口峰值归一化。</summary>
    private void RebuildNicSpark()
    {
        var arr = _nicHistory.ToArray();
        if (arr.Length == 0) return;
        var peak = Math.Max(arr.Max(), 1);
        var step = NicSparkWidth / (NicWindowSeconds - 1);
        var shift = NicWindowSeconds - arr.Length;    // 不足 60 秒时靠右生长
        NicSparkPoints.Clear();
        for (var i = 0; i < arr.Length; i++)
        {
            var y = NicSparkHeight - 1 - arr[i] / peak * (NicSparkHeight - 3);
            NicSparkPoints.Add(new Point((shift + i) * step, Math.Clamp(y, 0, NicSparkHeight)));
        }
        NicSparkTip = $"网卡实时吞吐（全部活动网卡 收+发 合计，每秒采样，右端最新）\n最近 {arr.Length} 秒峰值 {Format.Speed(peak)}";
    }

    private void StartLiveFeed()
    {
        LiveFiles.Clear();
        CurrentFileText = "正在复制…";
        _liveTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _liveTimer.Tick -= LiveTimer_Tick;
        _liveTimer.Tick += LiveTimer_Tick;
        _liveTimer.Start();
    }

    private void StopLiveFeed()
    {
        _liveTimer?.Stop();
        LiveTimer_Tick(null, EventArgs.Empty); // 排空残余
    }

    private void LiveTimer_Tick(object? sender, EventArgs e)
    {
        if (_liveQueue.IsEmpty) return;
        var drained = 0; string? last = null;
        var skipped = 0;
        while (_liveQueue.TryDequeue(out var item))
        {
            if (drained < LiveDrainCap) { LiveFiles.Add(item); drained++; }
            else skipped++;
            last = item;
        }
        if (skipped > 0)
            LiveFiles.Add($"…（流速 {drained + skipped} 个/250ms，中间 {skipped} 条从略）");
        while (LiveFiles.Count > MaxLiveRows) LiveFiles.RemoveAt(0);
        if (last != null) CurrentFileText = $"▶ {last}";
    }

    /// <summary>引擎主动提示（速度异常等）：进日志面板 + 状态栏。同样来自后台线程，需封送。</summary>
    private void OnTransferNotice(string msg)
    {
        void Apply() { AppendLog($"[提示] {msg}"); StatusMessage = msg; }
        if (Application.Current?.Dispatcher.CheckAccess() == true) Apply();
        else Application.Current?.Dispatcher.Invoke(Apply);
    }

    /// <summary>robocopy 行级输出：含错误码且带 UNC 路径的行 → 报错区。
    /// 注意：该行来自 robocopy 泵线程（后台线程），必须封送到 UI 线程再改 ObservableCollection。</summary>
    private void OnTransferLine(string line)
    {
        var m = s_errLineRx.Match(line);
        if (!m.Success) return;
        var p = line.IndexOf(@"\\", StringComparison.Ordinal);
        if (p < 0) return;
        var path = line[p..].Trim();
        if (path.Length < 4) return;
        var app = System.Windows.Application.Current;
        if (app == null || app.Dispatcher.CheckAccess()) AddFailItem(path, m.Groups["code"].Value);
        else app.Dispatcher.BeginInvoke(() => AddFailItem(path, m.Groups["code"].Value));
    }

    /// <summary>添加/更新一条报错（按位置去重，重复失败只更新时间）。</summary>
    private void AddFailItem(string uncPath, string errText)
    {
        var loc = FriendlyPath(uncPath);
        var reason = ErrorTranslator.ShortReason(errText);
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_failIndex.TryGetValue(loc, out var row))
            {
                row.Time = DateTime.Now.ToString("HH:mm:ss");
                row.Reason = reason;
                return;
            }
            if (FailItems.Count >= MaxFailRows) return;
            var r = new FailRow { Location = loc, Time = DateTime.Now.ToString("HH:mm:ss"), Reason = reason };
            _failIndex[loc] = r;
            FailItems.Add(r);
            RefreshFailHeader();
        });
    }

    /// <summary>报错区派生显示（条数、失败对象数）在增删行之后必须显式通知，否则界面会停在旧文案。</summary>
    private void RefreshFailHeader()
    {
        OnPropertyChanged(nameof(ErrorHeader));
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(FailedObjectRows));
        HasObjectFailures = FailedObjectRows > 0;
    }

    /// <summary>原样添加一条报错（用于把 Receipt 里已是"人话"的失败明细直接展示，不再做错误码翻译）。</summary>
    /// <param name="objectLevel">true = 整条对象失败（界面红色加粗），false = 单文件级/提示级。</param>
    private void AddFailRow(string location, string reason, bool objectLevel = false)
    {
        if (string.IsNullOrWhiteSpace(location)) location = "（未知位置）";
        void Apply()
        {
            if (_failIndex.TryGetValue(location, out var row)) { row.Reason = reason; return; }
            if (FailItems.Count >= MaxFailRows) return;
            var r = new FailRow
            {
                Location = location,
                Time = DateTime.Now.ToString("HH:mm:ss"),
                Reason = reason,
                IsObjectLevel = objectLevel
            };
            _failIndex[location] = r;
            FailItems.Add(r);
            RefreshFailHeader();
        }
        var app = System.Windows.Application.Current;
        if (app == null || app.Dispatcher.CheckAccess()) Apply();
        else app.Dispatcher.BeginInvoke(Apply);
    }

    /// <summary>UNC 路径人性化：\\host\E$\x → E:\x；\\host\E\x → E\x。</summary>
    private static string FriendlyPath(string unc)
    {
        var m = System.Text.RegularExpressions.Regex.Match(unc, @"^\\\\[^\\]+\\(?<s>[A-Za-z])\$\\?(?<r>.*)$");
        if (m.Success) return $"{m.Groups["s"].Value.ToUpperInvariant()}:\\{m.Groups["r"].Value}";
        m = System.Text.RegularExpressions.Regex.Match(unc, @"^\\\\[^\\]+\\(?<s>[^\\]+)\\?(?<r>.*)$");
        if (m.Success) return $"{m.Groups["s"].Value}\\{m.Groups["r"].Value}";
        return unc;
    }

    /// <summary>
    /// 「对象明细」的实时状态。原来只在"完成对象数变化"时刷新，于是①正在传输的对象一直停在初始值
    /// ②暂停后行状态变「中断」、恢复后完成数还没变，就一直显示中断。现在按引擎的逐对象上报直接改行。
    /// </summary>
    private void ApplyObjectRowStatus(ProgressSnapshot s)
    {
        // 开跑（首跑或恢复）：先把上一轮遗留的中间态复位，避免"恢复后仍显示中断"
        if (s.Message.StartsWith("开始/继续传输", StringComparison.Ordinal))
        {
            foreach (var row in Objects)
                if (row.Status == "→ 传输中" || row.Status.StartsWith("‖ 中断", StringComparison.Ordinal))
                    row.Status = "待传输";
            return;
        }
        // 引擎报"开始对象 X"：立刻把该对象标成传输中
        if (!string.IsNullOrEmpty(s.CurrentObjectId) && s.Message.StartsWith("开始对象", StringComparison.Ordinal))
        {
            foreach (var row in Objects)
                if (string.Equals(row.ObjectId, s.CurrentObjectId, StringComparison.OrdinalIgnoreCase)) { row.Status = "→ 传输中"; break; }
            return;
        }
        // 暂停/中断：正在传的行标成可续传的中断态
        if (s.Phase is JobPhase.Paused or JobPhase.Interrupted)
        {
            foreach (var row in Objects)
                if (row.Status == "→ 传输中") row.Status = "‖ 中断（可续传）";
        }
    }

    private void RefreshRowsFromReceipts()
    {
        if (_ctx == null) return;
        var receipts = _ctx.LoadReceipts();
        var latest = receipts.GroupBy(r => r.ObjectId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.CompletedUtc).Last(), StringComparer.OrdinalIgnoreCase);
        foreach (var row in Objects)
        {
            if (latest.TryGetValue(row.ObjectId, out var r))
            {
                row.Status = r.Status switch
                {
                    ObjectStatus.Completed => $"✔ 完成（{Format.Bytes(r.TargetBytes)}）",
                    ObjectStatus.CompletedWithErrors => "◐ 完成(有错误)",
                    ObjectStatus.Failed => "✘ 失败",
                    ObjectStatus.Interrupted => "‖ 中断",
                    _ => r.Status.ToString()
                };
                // 失败/有错误的对象：把错误明细也喂进报错区（路径在 ErrorDetail 里）
                if (r.Status is ObjectStatus.Failed or ObjectStatus.CompletedWithErrors
                    && !string.IsNullOrWhiteSpace(r.ErrorDetail))
                    OnTransferLine(r.ErrorDetail);

                // v0.3.8（缺陷 3）：对象级失败必须"对象号 + 路径 + 退出码译文"逐条红色列出。
                // 生产事故：5/7 完成、2 个失败、35.9%，界面却像"还在跑"，用户以为卡死并手动终止。
                if (r.Status == ObjectStatus.Failed)
                    AddFailRow(
                        $"✘ 对象失败　{r.ObjectId}　{FriendlyPath(r.TargetPath)}",
                        ErrorTranslator.FailureHeadline(r.ObjectId, FriendlyPath(r.TargetPath),
                            r.RobocopyExitCodeBulk, r.RobocopyExitCodeLarge) +
                        "｜" + ErrorTranslator.ShortReason(r.ErrorDetail),
                        objectLevel: true);
            }
        }
    }

    private async Task VerifyAsync()
    {
        if (_ctx == null) return;
        CurrentStep = 3;   // 进「结果与校验」页
            StatusMessage = "正在验证（L1 文件数/字节对账）…";
        try
        {
            var jobLog = LogBootstrap.CreateJobLogger(_ctx.JobDir, _ctx.JobId, _memorySink);
            var matrix = MigrationMatrix.Load(null, jobLog);
            var verifier = new Verifier(_ctx, matrix, jobLog);
            var report = await verifier.RunAsync(VerifyLevel.L1_CountSize,
                new Progress<string>(m => StatusMessage = m));
            StatusMessage = report.OverallPass
                ? "✔ 验证通过：源与目标文件数/字节数全部一致。"
                : $"✘ 存在不一致对象：{string.Join("、", report.Objects.Where(o => o.Status != "OK").Select(o => o.ObjectId))}。详见报告。";
        }
        catch (Exception ex) { StatusMessage = $"验证失败：{ex.Message}"; }
    }

    // ═════════════════ 尝试修复（验证不一致 → 定向重拷 → 自动复验） ══════════════════

    /// <summary>
    /// 收集需要修复的对象：优先用上次验证报告里"不一致"的对象；
    /// 再补上 Receipt 里失败/有错误/中断的对象（即使没验证过，这些也该重拷）。
    /// </summary>
    private List<string> CollectRepairTargets(out int beforeMismatch)
    {
        var ids = new List<string>();
        beforeMismatch = 0;
        if (_ctx == null) return ids;

        var verify = _ctx.LoadVerify();
        if (verify != null)
        {
            var bad = verify.Objects.Where(o => o.Status != "OK").ToList();
            beforeMismatch = bad.Count;
            ids.AddRange(bad.Select(o => o.ObjectId));
        }

        var receipts = _ctx.LoadReceipts(_appLog);
        var latest = receipts
            .GroupBy(r => r.ObjectId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(r => r.CompletedUtc).Last());
        foreach (var r in latest)
            if (r.Status is ObjectStatus.Failed or ObjectStatus.CompletedWithErrors or ObjectStatus.Interrupted)
                ids.Add(r.ObjectId);

        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 「尝试修复」：把不一致/失败的对象定向重拷（robocopy 增量，只补差异），随后自动重新校验，
    /// 并把"修复前后不一致对象数"直接报出来。修复范围不盲目全量重传。
    /// </summary>
    private async Task RepairAsync()
    {
        if (_ctx == null || IsRunning) return;
        var ctx = _ctx;

        var targets = CollectRepairTargets(out var beforeMismatch);
            _repairDone.Clear();   // 每次修复单独统计，避免上一轮的完成数累积进来
        if (targets.Count == 0)
        {
            StatusMessage = "没有需要修复的对象：上次校验（若有）全部一致，也没有失败对象。";
            return;
        }

        using var jobLock = JobLock.TryAcquire(ctx, TimeSpan.FromSeconds(30), out var lockReason);
        if (jobLock == null) { StatusMessage = lockReason; return; }

        var beforeText = (beforeMismatch > 0
            ? $"修复前 {beforeMismatch} 个对象不一致"
            : $"修复前有 {targets.Count} 个对象需要重拷")
            + (RepairForceOverwrite ? "，已从共享强制覆盖" : "，按增量补差异");

        IsRunning = true;
        _runCts = new CancellationTokenSource();
        var jobLog = LogBootstrap.CreateJobLogger(ctx.JobDir, ctx.JobId, _memorySink);
        var matrix = MigrationMatrix.Load(null, jobLog);
        var orchestrator = new TransferOrchestrator(ctx, matrix, jobLog);
        orchestrator.OutputLine += OnTransferLine;
        orchestrator.FileCopied += OnLiveFileCopied;
        orchestrator.TransferNotice += OnTransferNotice;
        // 强制覆盖：修复时默认从共享把同名文件重新拉一遍覆盖本地（/IS /IT），
        // 而不是"看起来一样就跳过"——否则大小/时间相同但内容坏掉的文件永远修不回来。
        orchestrator.ForceOverwriteFromSource = RepairForceOverwrite;

        // 从源重拷：尽量复用现有 SMB 会话；没密码时不硬拦（Windows 可能还缓存着连接），失败原因照实报
        var effUser = !string.IsNullOrWhiteSpace(Username) ? Username.Trim() : ctx.Definition.SourceUser;
        IDisposable? session = null;
        if (!string.IsNullOrEmpty(effUser))
        {
            if (string.IsNullOrEmpty(_lastPassword))
            {
                AppendLog($"[提示] 修复未使用凭据连接（账号 {effUser}）：若 Windows 仍缓存着与该电脑的连接即可正常重拷，" +
                          "否则会以共享不可用失败，此时请在上方输入密码后重试。");
            }
            else
            {
                try
                {
                    session = NetworkShare.ConnectForTransfer(ctx.Definition.SourceHost, effUser, _lastPassword,
                        ctx.Definition.Sources.Select(s => s.Path).ToList(), jobLog);
                }
                catch (Exception ex)
                {
                    StatusMessage = $"连接失败：{ex.Message}";
                    IsRunning = false;
                    return;
                }
            }
        }

        var progress = new Progress<ProgressSnapshot>(s =>
        {
            var finished = s.Phase is JobPhase.Completed or JobPhase.CompletedWithErrors;
            IsFinished = finished;
            DataLabel = finished ? "实际落盘 / 计划" : "已传 / 计划";
            Percent = finished ? 100.0 : s.Percent;
            ProgressText = $"{Format.Bytes(s.CompletedBytes)} / {Format.Bytes(s.TotalBytes)}";
            PlanBytesText = Format.Bytes(s.TotalBytes);
            ActualBytesText = Format.Bytes(s.CompletedBytes);
            _lastProgressUtc = DateTime.UtcNow;
            EngineSpeedText = s.BytesPerSecond > 0 ? Format.Speed(s.BytesPerSecond) : "—";
            EtaText = !double.IsNaN(s.EtaSeconds) ? Format.Eta(s.EtaSeconds) : "—";
            ObjectText = $"{s.CompletedObjects}/{s.TotalObjects}" + (s.FailedObjects > 0 ? $"（失败 {s.FailedObjects}）" : "");
            FailedObjects = s.FailedObjects;
            StatusMessage = $"修复中：{s.Message}";
            // 修复有自己的一条进度条与计数，迁移那条不动
            IsRepairing = true;
            // 只统计"修复范围"内的对象：完成数从引擎消息里累计，进度随对象完成动态前进
            var doneMatch = System.Text.RegularExpressions.Regex.Match(s.Message ?? "", @"(object-\d+)");
            if (doneMatch.Success && (s.Message ?? "").IndexOf("Completed", StringComparison.Ordinal) >= 0)
                _repairDone.Add(doneMatch.Groups[1].Value);
            var repairTotal = targets.Count;
            var repairDone = _repairDone.Count;
            RepairPercent = repairTotal > 0 ? Math.Min(100.0, repairDone * 100.0 / repairTotal) : (finished ? 100.0 : 0.0);
            RepairText = "本次修复范围 " + repairTotal + " 个对象 · 当前：" +
                (string.IsNullOrEmpty(s.CurrentObjectPath) ? (s.CurrentObjectId ?? "—") : s.CurrentObjectPath);
            RepairObjectText = "已完成 " + repairDone + "/" + repairTotal;
            if (s.CompletedObjects != _lastCompletedCount)
            {
                _lastCompletedCount = s.CompletedObjects;
                RefreshRowsFromReceipts();
            }
            ApplyObjectRowStatus(s);
        });

        StartLiveFeed();
        try
        {
            var phase = await orchestrator.RunAsync(progress, _runCts.Token, targets, forceRecopy: true);
            IsRepairing = false;   // 重拷结束 → 收起修复进度条（随后是重新校验阶段）
            StatusMessage = $"重拷结束（{phase}{(RepairForceOverwrite ? "，已强制覆盖" : "，仅补差异")}），正在重新校验…";
            CurrentStep = 3;

            var report = await new Verifier(ctx, matrix, jobLog).RunAsync(VerifyLevel.L1_CountSize,
                new Progress<string>(m => StatusMessage = m));
            var badIds = report.Objects.Where(o => o.Status != "OK").Select(o => o.ObjectId).ToList();

            var measured = await Task.Run(() => ComputeActualBytes(ctx, measureMissing: true));
            ApplyDataBalance(measured, PlanBytesOf());
            RefreshRowsFromReceipts();

            StatusMessage = badIds.Count == 0
                ? $"✔ 修复完成并复验通过：{beforeText} → 现在 0 个（已重拷 {targets.Count} 个对象的差异）。"
                : $"⚠ 修复后仍有 {badIds.Count} 个对象不一致（{beforeText}，已重拷 {targets.Count} 个）：" +
                  $"{string.Join("、", badIds)}。常见原因：旧电脑上文件仍被程序占用、权限不足、源不可读；" +
                  "处理后（例如关掉占用程序）可再点一次「尝试修复」。";
            CurrentStep = 3;
        }
        catch (OperationCanceledException) { StatusMessage = "修复已取消（已拷入的部分保留）。"; }
        catch (Exception ex) { StatusMessage = $"修复失败：{ex.Message}"; _appLog.Error(ex, "修复异常"); }
        finally
        {
            orchestrator.OutputLine -= OnTransferLine;
            orchestrator.FileCopied -= OnLiveFileCopied;
            orchestrator.TransferNotice -= OnTransferNotice;
            StopLiveFeed();
            session?.Dispose();
            IsRunning = false;
            _lastCompletedCount = -1;
            RefreshRowsFromReceipts();
            RefreshExistingJobs();
            OnPropertyChanged(nameof(HasJob));
        }
    }

    /// <summary>
    /// 打开目标文件夹。顺带做"可见性守卫"：robocopy 会把源盘根的 Hidden+System 属性写到目标根目录上，
    /// 导致迁移完在资源管理器里"找不到文件夹，但空间确实被占用"（实测踩中）。
    /// </summary>
    private void OpenTargetFolder()
    {
        try
        {
            var root = _ctx?.Definition.TargetRoot ?? TargetRoot;
            if (string.IsNullOrWhiteSpace(root)) { StatusMessage = "尚未指定目标文件夹。"; return; }
            if (!Directory.Exists(root)) { StatusMessage = $"目标文件夹还不存在：{root}（先点「预检并生成计划」或「开始迁移」）"; return; }
            if (TargetRootGuard.EnsureVisible(root, _appLog))
                StatusMessage = $"目标文件夹原先被标记为隐藏（源盘根属性所致），已自动解除：{root}";
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { StatusMessage = $"打开目标文件夹失败：{ex.Message}"; }
    }

    private void ReportAndOpen()
    {
        if (_ctx == null) return;
        try
        {
            var jobLog = LogBootstrap.CreateJobLogger(_ctx.JobDir, _ctx.JobId, _memorySink);
            var path = new ReportGenerator(_ctx, jobLog).Generate();
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { StatusMessage = $"报告生成失败：{ex.Message}"; }
    }

    private void RefreshExistingJobs()
    {
        try
        {
            var jm = new JobManager(_appLog);
            ExistingJobs.Clear();
            foreach (var j in jm.ListAll())
                ExistingJobs.Add(new ExistingJobItem
                {
                    JobId = j.JobId, JobDir = j.JobDir,
                    // 陈旧 running/awaitingReview 按"已中断（可续传）"显示；状态损坏时进度不可信也说清楚
                    DisplayText = $"{j.JobId}  [{j.PhaseText}]  {j.SourceHost} → {j.TargetRoot}{j.DisplayNote}"
                });
        }
            catch { /* 首次运行无目录 */ }
    }

    private void AppendLog(string line)
    {
        _logBuffer.AppendLine(line);
        if (_logBuffer.Length > 64 * 1024) _logBuffer.Remove(0, _logBuffer.Length / 2);
        LogText = _logBuffer.ToString();
    }

    public void OnClosing()
    {
        try { _nicTimer?.Stop(); } catch { }
        try { _ctx?.RequestPause(true); } catch { }
        try { _runCts?.Cancel(); } catch { }
        _appLog.Information("GUI 退出");
        Serilog.Log.CloseAndFlush();
    }

    // ══════════════════ 目录树浏览 / 精细选择 ══════════════════

    /// <summary>懒加载节点的子内容：子目录（DirNode）+ 一级文件（FileRow）挂在同一棵树上。</summary>
    public async Task EnsureChildrenAsync(DirNode node, int? version = null)
    {
        if (node.ChildrenLoaded || string.IsNullOrEmpty(node.FullPath)) return;
        node.ChildrenLoaded = true; // 防重入
        var v = version ?? _browseVersion;
        try
        {
            _matrixCache ??= MigrationMatrix.Load(null, _appLog);
            var exclDirs = new HashSet<string>(_matrixCache.ExcludedDirectoryNames, StringComparer.OrdinalIgnoreCase);
            var exclFiles = new HashSet<string>(
                _matrixCache.ExcludedFileNames.Concat(_matrixCache.SecurityBlockedFileNames), StringComparer.OrdinalIgnoreCase);
            var path = node.FullPath;
            var (dirs, files) = await Task.Run(() =>
            {
                var dlist = new List<string>();
                var flist = new List<(string Name, long Size)>();
                try
                {
                    foreach (var d in Directory.EnumerateDirectories(path))
                    {
                        try
                        {
                            var attr = File.GetAttributes(d);
                            if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                            var name = Path.GetFileName(d.TrimEnd('\\'));
                            if (name == null || exclDirs.Contains(name)) continue;
                            dlist.Add(d);
                        }
                        catch { /* 单目录跳过 */ }
                    }
                }
                catch { /* 目录整体不可访问 */ }
                try
                {
                    foreach (var f in Directory.EnumerateFiles(path))
                    {
                        try
                        {
                            var fi = new FileInfo(f);
                            if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            if (exclFiles.Contains(fi.Name)) continue;
                            flist.Add((fi.Name, fi.Length));
                        }
                        catch { /* 单文件跳过 */ }
                    }
                }
                catch { /* 文件枚举失败不致命 */ }
                return (dlist.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                        flist.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList());
            });
            if (v != _browseVersion) return; // 已重新连接，丢弃过期结果
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var inherit = node.IsChecked != false; // 父为“勾/半勾” → 子默认勾（半勾=只排除了亲手取消的项）
                node.Children.Clear();
                foreach (var d in dirs)
                {
                    var child = new DirNode { Name = Path.GetFileName(d.TrimEnd('\\')) ?? d, FullPath = d, Parent = node };
                    child.AddDummy();
                    child.SetCheckedSilent(inherit);
                    node.Children.Add(child);
                }
                // 单目录文件显示上限（超大数据保护）：勾选目录本身就包含全部文件，列表仅为可视化
                const int MaxFilesShownPerDir = 5000;
                var shown = files.Count <= MaxFilesShownPerDir ? files : files.Take(MaxFilesShownPerDir).ToList();
                foreach (var f in shown)
                {
                    var row = new FileRow { Name = f.Name, DirPath = node.FullPath, Size = f.Size, Parent = node };
                    row.SetCheckedSilent(inherit);
                    node.Children.Add(row);
                }
                if (files.Count > shown.Count)
                    node.Children.Add(new DirNode
                    {
                        Name = $"…（共 {files.Count} 个文件，仅显示前 {shown.Count} 个；勾选目录即包含全部）",
                        FullPath = ""
                    });
                // 单目录文件特别多（图片库等）：提示可改用超大数据模式（一次性提示，不刷屏）
                if (files.Count > 20000 && !_bigDirHintShown)
                {
                    _bigDirHintShown = true;
                    StatusMessage = $"目录「{node.Name}」含 {files.Count:N0} 个文件——此类规模建议开启「超大数据模式」（② 区右上角开关），用排除规则代替目录树。";
                }
            });
        }
        catch (Exception ex) { _appLog.Debug(ex, "加载子内容失败 {Path}", node.FullPath); }
    }

    /// <summary>
    /// 汇总用户选择（级联三态语义）：
    /// 根“全勾”=整盘（走 wholeShares）；根“半勾”=走子树：全勾目录收目录，半勾目录继续下钻，勾的文件收文件。
    /// 目录折叠到最上层；文件仅当其目录未被整体勾选时生效。
    /// </summary>
    private List<string> CollectCustomSelections()
    {
        var dirs = new List<string>();
        var files = new List<string>();
        foreach (var root in DirTree)
        {
            if (root.IsChecked != null) continue; // 全勾走整盘；全不勾跳过
            WalkPartial(root, dirs, files);
        }
        var collapsed = dirs
            .Where(d => !dirs.Any(p => p != d && d.StartsWith(p.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var result = new List<string>(collapsed);
        foreach (var f in files)
        {
            var dir = f[..f.LastIndexOf('\\')];
            if (collapsed.Any(d => dir.Equals(d.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                || dir.StartsWith(d.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(f);
        }
        return result;
    }

    /// <summary>递归收集半勾节点下的有效选择。</summary>
    private static void WalkPartial(DirNode node, List<string> dirs, List<string> files)
    {
        foreach (var c in node.Children)
        {
            switch (c)
            {
                case DirNode d when d.FullPath.Length == 0: break; // 占位节点
                case DirNode d when d.IsChecked == true: dirs.Add(d.FullPath); break;
                case DirNode d when d.IsChecked == null: WalkPartial(d, dirs, files); break;
                case FileRow f when f.IsChecked: files.Add(f.DirPath.TrimEnd('\\') + "\\" + f.Name); break;
            }
        }
    }

    private string? FindShareRootOf(string path)
        => Shares.FirstOrDefault(s => path.StartsWith(s.UncPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
            || path.Equals(s.UncPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))?.UncPath;

    // ══════════════════ 断点提醒 ══════════════════

    /// <summary>检测该主机（可选目标路径）的未完成任务，弹窗询问续传。targetRoot 为 null 表示不限目标。</summary>
    private async Task PromptResumeIfAnyAsync(string? targetRoot)
    {
        try
        {
            var u = new JobManager(_appLog).FindUnfinished(Host.Trim(), targetRoot)
                .FirstOrDefault(x => File.Exists(Path.Combine(x.JobDir, "plan.json")));
            if (u == null) return;
            // 同前：不指定 Owner 的 MessageBox 会禁用整窗却可能藏在后面，让人以为“按钮坏了”。
            // 顺带在左栏提示面板写一行，万一窗口被挡住也知道该做什么。
            StatusMessage = "检测到上次未完成的任务：请在对话框里选「是」继续（已完成部分不重传）或「否」跳过。若没看到对话框，请点任务栏里的 PCMig。";
            var r = AppDialog.Show(Application.Current?.MainWindow,
                $"检测到上次的迁移任务未完成：\n\n任务：{u.JobId}\n源：{u.SourceHost}\n目标：{u.TargetRoot}\n进度：{(u.StateUnreliable ? "状态文件损坏，未知（以回执为准）" : $"已传 {u.Percent:0.0}%")}\n状态：{u.PhaseText}\n\n是否从中断处继续？已完成的部分不会重传。",
                "发现未完成的迁移任务", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r == MessageBoxResult.Yes)
                await ResumeExistingJobAsync(u);
        }
        catch (Exception ex) { _appLog.Warning(ex, "断点提醒检测失败"); }
    }

    private async Task ResumeExistingJobAsync(JobSummary summary)
    {
        var jm = new JobManager(_appLog);
        _ctx = jm.Open(summary.JobDir);
        TargetRoot = _ctx.Definition.TargetRoot;
        // 续传时自动回填任务当时用的账号（密码仍需用户确认/输入）
        if (string.IsNullOrWhiteSpace(Username) && !string.IsNullOrEmpty(_ctx.Definition.SourceUser))
            Username = _ctx.Definition.SourceUser;
        Objects.Clear();
        foreach (var o in _ctx.Plan?.Objects ?? Enumerable.Empty<PlannedObject>())
            Objects.Add(ToRow(o));
        RefreshRowsFromReceipts();
        PlanReady = true;
        OnPropertyChanged(nameof(HasJob));
                OnPropertyChanged(nameof(CanRepair));
        StatusMessage = $"已载入任务 {_ctx.JobId}，从中断处继续…";
        _ctx.ClearPauseRequest();
        await RunCoreAsync(_ctx);
    }
}

