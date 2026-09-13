using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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
    private string _status = "待连接";
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
        BrowseTargetCommand = new RelayCommand(_ => BrowseTarget());
        PrepareCommand = new RelayCommand(p => PrepareAsync(p as PasswordBox));
        StartCommand = new RelayCommand(_ => RunTransferAsync(), _ => CanStart);
            PauseCommand = new RelayCommand(_ => { _ctx?.RequestPause(false); StatusMessage = "已请求协作式暂停（当前对象传完后停止）"; });
            StopCommand = new RelayCommand(_ => { _ctx?.RequestPause(true); StatusMessage = "已请求立即停止（终止当前 robocopy，已传部分保留）"; });
        ResumeCommand = new RelayCommand(_ => ResumeAsync(), _ => CanResume);
        VerifyCommand = new RelayCommand(_ => VerifyAsync(), _ => HasJob);
        ReportCommand = new RelayCommand(_ => ReportAndOpen(), _ => HasJob);

        RefreshExistingJobs();
            AppendLog("PCMig 已启动。输入旧电脑 IP/电脑名，点「连接并列出共享」开始。");
            _appLog.Information("GUI 启动，日志目录 {Dir}", LogBootstrap.AppLogDir);
            _appLog.Debug("crafted by 郑子轩 (Axuanbest)"); // 彩蛋：只落在文件日志，不进面板
    }

    // ---------------- 属性 ----------------

    private string _host = "";
    public string Host { get => _host; set => Set(ref _host, value); }

    private string _username = "";
    public string Username { get => _username; set => Set(ref _username, value); }

    private string _targetRoot = "";
    public string TargetRoot { get => _targetRoot; set => Set(ref _targetRoot, value); }

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
        set { if (Set(ref _selectedExistingJob, value)) OnPropertyChanged(nameof(CanResume)); }
    }

    private double _percent;
    public double Percent { get => _percent; set => Set(ref _percent, value); }

    private string _progressText = "0 B / 0 B";
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }

    private string _speedText = "";
    public string SpeedText { get => _speedText; set => Set(ref _speedText, value); }

    private string _etaText = "";
    public string EtaText { get => _etaText; set => Set(ref _etaText, value); }

    private string _objectText = "";
    public string ObjectText { get => _objectText; set => Set(ref _objectText, value); }

    private string _statusMessage = "就绪";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

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

    // ---------------- 命令 ----------------

    public RelayCommand ConnectCommand { get; }
    public RelayCommand BrowseTargetCommand { get; }
    public RelayCommand PrepareCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand VerifyCommand { get; }
    public RelayCommand ReportCommand { get; }

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
            var report = await Task.Run(() => checker.RunAsync(host,
                string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
                string.IsNullOrWhiteSpace(pwdBox?.Password) ? null : pwdBox!.Password, [], null));

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
                    StatusMessage += $"（注意：未发现名为 {directShare} 的共享，请确认拼写）";
                }
            }
            if (string.IsNullOrWhiteSpace(TargetRoot))
                TargetRoot = $"D:\\Migrated\\{Host.Trim()}";
            RefreshExistingJobs();

            // ---- 断点提醒：该主机有未完成任务 → 主动弹窗询问续传 ----
            await PromptResumeIfAnyAsync(null);
        }
        catch (Exception ex)
        {
            StatusMessage = $"连接失败：{ex.Message}";
            _appLog.Error(ex, "连接失败 {Host}", Host);
        }
    }

    private void BrowseTarget()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择目标目录（新电脑接收数据的位置）" };
        if (dlg.ShowDialog() == true) TargetRoot = dlg.FolderName;
    }

    private async Task PrepareAsync(PasswordBox? pwdBox)
    {
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
                var r = MessageBox.Show(
                    $"该源电脑和目标路径下已有未完成的任务：\n\n任务：{dup.JobId}\n进度：已传 {dup.Percent:0.0}%\n状态：{dup.Phase}\n\n[是] 继续上次任务（已完成部分不重传）\n[否] 创建全新任务",
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
                sources.Select(s => s.Path).ToList(), def.TargetRoot));
            _ctx.SavePreflight(pre);
            if (!pre.OverallPass)
            {
                StatusMessage = $"预检未通过：{pre.Checks.First(c => !c.Pass && c.Severity == "Error").Detail}";
                return;
            }

            StatusMessage = "预检通过，正在扫描源数据（大容量磁盘可能需要几分钟）…";
            var matrix = MigrationMatrix.Load(null, jobLog).WithExtraExclusions(def.CustomExclusions, jobLog);
            var scanner = new SourceScanner(jobLog);
            var observed = await scanner.ScanAsync(def, matrix,
                new Progress<string>(m => StatusMessage = m));

            _ctx.SaveObserved(observed);
            var planner = new Planner(jobLog);
            var plan = planner.CreatePlan(def, observed, matrix);
            _ctx.SavePlan(plan);
            var st = _ctx.LoadStateOrNew();
            st.Phase = JobPhase.AwaitingReview;
            st.TotalObjects = plan.Objects.Count; st.TotalBytes = plan.TotalBytes;
            _ctx.SaveState(st);

            // ---- 目标盘空间守护：计划总量超过可用空间时弹窗确认 ----
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(def.TargetRoot));
                if (root != null && plan.TotalBytes > 0)
                {
                    var free = new DriveInfo(root).AvailableFreeSpace;
                    if (free < plan.TotalBytes)
                    {
                        var r = MessageBox.Show(
                            $"目标盘 {root} 可用空间不足：\n需要 {Format.Bytes(plan.TotalBytes)}，可用 {Format.Bytes(free)}。\n\n仍要继续吗？（可能中途失败；失败后可清理空间再点「恢复任务」）",
                            "空间不足", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                        if (r != MessageBoxResult.Yes)
                        {
                            StatusMessage = "已取消：目标盘空间不足。清理空间或更换目标后重新「预检并生成计划」。";
                            return;
                        }
                    }
                }
            }
            catch { /* 空间检查失败不阻断 */ }

            Objects.Clear();
            foreach (var o in plan.Objects)
                Objects.Add(ToRow(o));

            ProgressText = $"0 B / {Format.Bytes(plan.TotalBytes)}";
            ObjectText = $"{plan.Objects.Count} 个对象";
            StatusMessage = $"计划已生成（Job { _ctx.JobId }）：{plan.Objects.Count} 个对象，共 {Format.Bytes(plan.TotalBytes)}。" +
                (observed.Warnings.Count > 0 ? $" ⚠ {observed.Warnings[0]}" : "点击「开始迁移」。");
            // 文件数只有扫描后才知道：超大规模时明确告知"全程流式、无文件数上限"
            if (observed.TotalFiles >= 5_000_000)
                StatusMessage += $"（文件量 {observed.TotalFiles:N0}，属超大规模——本任务传输/验证全程流式处理，无文件数上限）";
            PlanReady = true;
            OnPropertyChanged(nameof(HasJob));
            RefreshExistingJobs();
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
        if (_ctx == null) return;
        await RunCoreAsync(_ctx);
    }

    private async Task ResumeAsync()
    {
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
        using var jobLock = JobLock.TryAcquire(ctx, out var lockReason);
        if (jobLock == null) { StatusMessage = lockReason; return; }

        IsRunning = true;
        PlanReady = false;
        OnPropertyChanged(nameof(HasJob));
        _runCts = new CancellationTokenSource();
        var jobLog = LogBootstrap.CreateJobLogger(ctx.JobDir, ctx.JobId, _memorySink);
        var matrix = MigrationMatrix.Load(null, jobLog);
        var orchestrator = new TransferOrchestrator(ctx, matrix, jobLog);
        orchestrator.OutputLine += OnTransferLine;
        orchestrator.FileCopied += OnLiveFileCopied;   // 实时文件流
        FailItems.Clear();
        _failIndex.Clear();

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
                return;
            }
            try
            {
                session = NetworkShare.Connect(ctx.Definition.SourceHost, effUser, _lastPassword, jobLog);
            }
            catch (Exception ex)
            {
                StatusMessage = $"连接失败：{ex.Message}";
                IsRunning = false; PlanReady = true;
                OnPropertyChanged(nameof(HasJob));
                return;
            }
        }

        var progress = new Progress<ProgressSnapshot>(s =>
        {
            Percent = s.Percent;
            ProgressText = $"{Format.Bytes(s.CompletedBytes)} / {Format.Bytes(s.TotalBytes)}   {s.Percent:0.0}%";
            SpeedText = s.BytesPerSecond > 0 ? $"速度 {Format.Speed(s.BytesPerSecond)}" : "";
            EtaText = !double.IsNaN(s.EtaSeconds) ? $"剩余 {Format.Eta(s.EtaSeconds)}" : "";
            ObjectText = $"{s.CompletedObjects}/{s.TotalObjects} 对象" + (s.FailedObjects > 0 ? $"（失败 {s.FailedObjects}）" : "");
            StatusMessage = s.Message;
            if (s.CompletedObjects != _lastCompletedCount)
            {
                _lastCompletedCount = s.CompletedObjects;
                RefreshRowsFromReceipts();
            }
        });

        StartLiveFeed(); // 会话已建立、即将开跑：启动实时文件流节拍器
        try
        {
            var phase = await orchestrator.RunAsync(progress, _runCts.Token);
            StatusMessage = phase switch
            {
                JobPhase.Completed => "✔ 迁移完成！建议点「验证」确认数据一致，然后「打开报告」。",
                JobPhase.CompletedWithErrors => "◐ 完成但有失败对象，请打开报告查看明细。",
                JobPhase.Paused => "‖ 已暂停。点「恢复任务」继续。",
                JobPhase.Interrupted => "⏸ 已中断（可续传）。点「恢复任务」继续。",
                _ => $"阶段结束：{phase}"
            };
        }
        catch (OperationCanceledException) { StatusMessage = "已取消（可续传）。"; }
        catch (Exception ex) { StatusMessage = $"传输异常：{ex.Message}"; _appLog.Error(ex, "传输异常"); }
        finally
        {
            orchestrator.OutputLine -= OnTransferLine;
            orchestrator.FileCopied -= OnLiveFileCopied;
            StopLiveFeed();
            session?.Dispose();
            IsRunning = false;
            PlanReady = true;
            _lastCompletedCount = -1;
            RefreshRowsFromReceipts();
            RefreshExistingJobs();
            OnPropertyChanged(nameof(HasJob));
        }
    }

    // ══════════════════ 报错区（失败文件一目了然） ══════════════════

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
        });
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
            }
        }
    }

    private async Task VerifyAsync()
    {
        if (_ctx == null) return;
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
                    DisplayText = $"{j.JobId}  [{j.Phase}]  {j.SourceHost} → {j.TargetRoot}"
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
            var r = MessageBox.Show(
                $"检测到上次的迁移任务未完成：\n\n任务：{u.JobId}\n源：{u.SourceHost}\n目标：{u.TargetRoot}\n进度：已传 {u.Percent:0.0}%\n状态：{u.Phase}\n\n是否从中断处继续？已完成的部分不会重传。",
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
        StatusMessage = $"已载入任务 {_ctx.JobId}，从中断处继续…";
        _ctx.ClearPauseRequest();
        await RunCoreAsync(_ctx);
    }
}

