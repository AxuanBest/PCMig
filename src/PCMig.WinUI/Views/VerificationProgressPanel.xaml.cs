using System.Collections.ObjectModel;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PCMig.Core.Models;
using PCMig.Core.Util;
using PCMig.Core.Verify;
using PCMig.WinUI.Presentation;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace PCMig.WinUI.Views;

/// <summary>验证工具面板的展示状态；仅投影真实生命周期，绝不作为验证业务判据。</summary>
public enum VerificationPanelState
{
    Idle, PreviousResult, CheckingPreflight, Running, Cancelling,
    CompletedPassed, CompletedWithDifferences, Unverifiable, Canceled, Faulted
}

/// <summary>
/// ★ PMML 返修（2026-10-11）★ 验证完整性的**应用内工具浮层**代码后置（替代原独立二级窗口）。
///
/// 职责边界（刻意收窄，与旧 VerifyDetailWindow 一致）：
///   · 本面板**不执行验证**、**不做任何判定**，只显示引擎推过来的真实快照与最终报告；
///   · 「取消验证」不自己取消任何东西，只把"用户请求取消"交给页面层注册的处理器
///     （页面持有 CancellationTokenSource ⇒ 引擎的 ct 才被真正取消）；
///   · 「关闭」只是收起观察窗（由 Shell 播反向 morph 后折叠），**不等于取消**：验证继续在后台跑，
///     再打开时显示同一会话的真实状态；
///   · 所有 UI 写入都走 <see cref="DispatcherQueue"/>：快照来自引擎后台线程。
///
/// 本面板不做动画、不做定位、不改 MaxHeight —— 这三件事全部由 Shell 的
/// FluidZoomTransitionCoordinator + TryPlacePanel（TAOP）负责。
/// </summary>
public sealed partial class VerificationProgressPanel : UserControl
{
    private readonly ObservableCollection<VerificationProgressRow> _rows = new();
    private Action? _onCancelRequested;
    private Func<Task>? _onStartRequested;
    private Func<Task<VerifyReportOpenResult>>? _onOpenReport;
    private bool _snapshotSeen;
    private bool _startRequested;
    private bool _cancelRequested;
    private bool _finished;

    /// <summary>当前真实展示状态，供 UIA/取证读取；业务真值仍为 VM 的报告和结果。</summary>
    public VerificationPanelState State { get; private set; } = VerificationPanelState.Idle;

    public VerificationProgressPanel()
    {
        InitializeComponent();
        ObjectList.ItemsSource = _rows;
        SubtitleText.Text = "";
    }

    /// <summary>面板已被打开/复用时请求 Shell 收起它（Shell 负责反向 morph 与折叠）。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>本轮是否已经收到过**至少一条**真实快照（没收到就不该有百分比）。</summary>
    public bool SnapshotSeen => _snapshotSeen;

    /// <summary>取消按钮是否已被点过（只请求一次）。</summary>
    public bool CancelRequested => _cancelRequested;

    /// <summary>本轮是否已经收尾（收尾后取消按钮不可再点）。</summary>
    public bool Finished => _finished;

    /// <summary>注册"用户点了取消"的处理器。页面层据此取消真正的 CancellationTokenSource。</summary>
    public void SetCancelHandler(Action handler) => _onCancelRequested = handler;

    /// <summary>注册唯一的显式开始入口；打开面板本身绝不触发验证或 IO。</summary>
    public void SetStartHandler(Func<Task> handler) => _onStartRequested = handler;

    /// <summary>
    /// ★ m03985 §UI-C ★ 注册"打开验证报告"的处理器：页面层负责生成/打开本地 HTML，并把真实结果回报，
    /// 面板只显示结果（成功文案、或"已生成本机路径但浏览器打不开"的可复制路径、或"还没有结论"）。
    /// </summary>
    public void SetOpenReportHandler(Func<Task<VerifyReportOpenResult>> handler) => _onOpenReport = handler;

    /// <summary>装载作业级文案（来自会话，不臆造）。</summary>
    public void Attach(string jobIdText, string levelText)
    {
        SubtitleText.Text = string.IsNullOrWhiteSpace(levelText)
            ? jobIdText
            : $"{levelText}　·　{jobIdText}";
        ToolTipService.SetToolTip(SubtitleText, SubtitleText.Text);
        // 面板会被跨作业复用 ⇒ 装载新作业时"打开验证报告"必须先回到"还没有结论"，
        // 否则用户会以为上一轮的验证报告属于这一轮（执行令 T12：不得误开旧版）。
        SetReportAvailability(false, "还没有验证报告：在本页完成一次验证后即可打开。");
    }

    /// <summary>
    /// 控制「打开验证报告」的可用状态并如实解释原因（禁用时不能让用户猜为什么点不动）。
    /// </summary>
    private void SetReportAvailability(bool enabled, string explanation)
    {
        OpenReportButton.IsEnabled = enabled;
        ReportExplanation = explanation;
        FooterHintText.Text = explanation;
        ToolTipService.SetToolTip(OpenReportButton, enabled
            ? explanation + "（用系统默认浏览器打开本地 HTML，可离线查看）"
            : explanation);
    }

    /// <summary>最近一次对「打开验证报告」可用性的解释（供 UIA/取证读取，不是判定依据）。</summary>
    public string ReportExplanation { get; private set; } = "";

    /// <summary>焦点落点：Shell 打开面板后优先落在唯一显式开始入口。</summary>
    public void FocusFirstElement() => StartVerifyButton.Focus(FocusState.Programmatic);

    /// <summary>只显示可开始状态；不做闸门、验证或任何 IO。</summary>
    public void ShowIdle()
    {
        OnUi(() =>
        {
            State = VerificationPanelState.Idle;
            _snapshotSeen = false;
            _startRequested = false;
            _cancelRequested = false;
            _finished = false;
            StartVerifyButton.IsEnabled = true;
            StartVerifyButton.Content = "开始验证";
            CancelButton.IsEnabled = false;
            CancelButton.Content = "取消验证";
            ObjectProgressBar.Visibility = Visibility.Collapsed;
            PercentText.Text = "—";
            OutcomeText.Text = "尚未开始验证";
            StatusLineText.Text = "点击“开始验证”后才会检查前置条件并执行 L1 文件数与字节对账。";
            StageDetailText.Text = "打开面板不会启动验证，也不会访问源或目标。";
            SetReportAvailability(false, "还没有验证报告：点击“开始验证”并完成核对后即可打开。");
        });
    }

    // ────────────────────────── 引擎 → 界面（真实数据） ──────────────────────────

    /// <summary>新一轮验证开始：清空上一轮投影，回到"等待引擎回报"（**不编百分比**）。</summary>
    public void BeginRun()
    {
        OnUi(() =>
        {
            State = VerificationPanelState.Running;
            _snapshotSeen = false;
            _startRequested = true;
            _cancelRequested = false;
            _finished = false;
            StartVerifyButton.IsEnabled = false;
            StartVerifyButton.Content = "验证中…";
            _rows.Clear();
            ObjectsEmptyText.Visibility = Visibility.Visible;

            ObjectProgressBar.Visibility = Visibility.Visible;
            ObjectProgressBar.IsIndeterminate = true;   // ★ 分母/进度未知 ⇒ 不定进度，不伪造
            ObjectProgressBar.Value = 0;
            PercentText.Text = "—";
            ObjectCountText.Text = "无";
            FilesCheckedText.Text = "无";
            BytesCheckedText.Text = "无";
            ElapsedText.Text = "无";

            OkCountText.Text = "0";
            MismatchCountText.Text = "0";
            UnknownCountText.Text = "0";
            EmptyCountText.Text = "0";

            OutcomeText.Text = "等待引擎回报…";
            StatusLineText.Text = "已请求验证：真实计数由引擎逐个对象回报，界面不估算、不预测。";
            StageDetailText.Text = "";
            CancelButton.IsEnabled = true;
            CancelButton.Content = "取消验证";
            CopySummaryButton.IsEnabled = true;
            SetReportAvailability(false, "验证进行中：检查过程走完后才能打开验证报告（进度 100% 只表示检查走完）。");
        });
    }

    /// <summary>
    /// ★ PMML 返修（2026-10-11，实测得出）★ 前置闸门阶段的**立即反馈**。
    ///
    /// 实测教训：旧写法把面板打开与 <see cref="BeginRun"/> 放在 `await EvaluateVerifyGateAsync()` **之后**，
    /// 结果点击「验证完整性」后约 2 s 面板才出现、取消按钮才可用（闸门要在后台实测源根可达性）。
    /// 现在面板与取消按钮在点击的同一拍就就位，闸门阶段如实显示"正在实测"，不编造任何计数。
    /// </summary>
    public void BeginPreflight()
    {
        OnUi(() =>
        {
            State = VerificationPanelState.CheckingPreflight;
            OutcomeText.Text = "正在检查前置条件…";
            StatusLineText.Text = "正在实测源根可达性（前置闸门）：源侧读不到时「目标一致」无法成立，此时的通过是假通过。";
            StageDetailText.Text = "";
        });
    }

    /// <summary>
    /// 前置闸门拒绝（没有开始任何核对，也没有取消）：如实说"未开始"，不显示成失败、更不显示成通过。
    /// </summary>
    public void FinishBlocked(string reason)
    {
        OnUi(() =>
        {
            State = VerificationPanelState.Unverifiable;
            _finished = true;
            _startRequested = false;
            StartVerifyButton.IsEnabled = true;
            StartVerifyButton.Content = "重新验证";
            CancelButton.IsEnabled = false;
            CancelButton.Content = "取消验证";
            ObjectProgressBar.Visibility = Visibility.Collapsed;
            PercentText.Text = "无";
            OutcomeText.Text = "总体结论：未开始（前置闸门拒绝）";
            StatusLineText.Text = reason;
            StageDetailText.Text = "前置条件不满足：没有执行任何核对（既不算通过，也不算失败）。";
            SetReportAvailability(false, "本次未开始核对（前置闸门拒绝）：没有验证报告可打开。");
        });
    }

    /// <summary>应用一条结构化快照。没有快照时界面保持"等待引擎回报"，不编百分比。</summary>
    public void ApplySnapshot(VerifyProgressSnapshot s)
    {
        OnUi(() =>
        {
            _snapshotSeen = true;

            ObjectProgressBar.Visibility = Visibility.Visible;
            ObjectProgressBar.IsIndeterminate = false;
            ObjectProgressBar.Value = s.Percent;
            PercentText.Text = s.Percent.ToString("0.0") + "%";
            ObjectCountText.Text = $"{s.ObjectIndex}/{s.ObjectCount}";
            FilesCheckedText.Text = s.FilesChecked.ToString("N0");
            BytesCheckedText.Text = Format.Bytes(s.BytesChecked);
            ElapsedText.Text = FormatDuration(s.ElapsedMs);

            OkCountText.Text = s.OkObjects.ToString("N0");
            MismatchCountText.Text = s.MismatchObjects.ToString("N0");
            UnknownCountText.Text = s.UnknownObjects.ToString("N0");
            EmptyCountText.Text = s.EmptyObjects.ToString("N0");

            OutcomeText.Text = "正在核对（引擎实测累计值）";
            StatusLineText.Text = s.Describe();
            StageDetailText.Text =
                $"已出结论：通过 {s.OkObjects}　存在差异 {s.MismatchObjects}　无法确认 {s.UnknownObjects}　无可核对数据 {s.EmptyObjects}";

            // 每出一个结论就补一行（start 快照不带结论，不占行）；最终报告到达时整表重建。
            if (s.Stage == "done")
            {
                if (_rows.Count == 0 || _rows[^1].ObjectId != s.ObjectId)
                    _rows.Add(VerificationProgressRow.Provisional(s.ObjectId, s.ObjectPath ?? ""));
                ObjectsEmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        });
    }

    /// <summary>应用引擎的一行人类可读进度（既有 IProgress&lt;string&gt; 通道，原样显示）。</summary>
    public void ApplyLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        OnUi(() => StatusLineText.Text = line);
    }

    /// <summary>
    /// 收尾：把**最终报告**里的四态结论逐对象搬到清单上（报告是唯一真值来源）。
    /// <paramref name="report"/> 为 null（取消 / 故障）时明确说明"没有报告"，绝不显示成通过。
    /// <paramref name="sourcePaths"/> 为 ObjectId → 源路径的映射（来自计划，取不到就显示空，不臆造）。
    /// </summary>
    public void FinishWith(VerifyReport? report, string outcomeText, bool canceled,
        IReadOnlyDictionary<string, string>? sourcePaths = null)
    {
        OnUi(() =>
        {
            _finished = true;
            _startRequested = false;
            StartVerifyButton.IsEnabled = true;
            StartVerifyButton.Content = "重新验证";
            CancelButton.IsEnabled = false;
            CancelButton.Content = canceled ? "已取消" : "取消验证";

            if (report is null)
            {
                State = canceled ? VerificationPanelState.Canceled : VerificationPanelState.Faulted;
                OutcomeText.Text = canceled ? "总体结论：已取消（没有结论）" : "总体结论：没有报告";
                StatusLineText.Text = canceled
                    ? "验证已被取消：没有结论（取消不等于通过）。"
                    : "验证未完成：没有报告。";
                // ★ 实测修正（2026-10-11）★ 取消/无报告 ⇒ **没有结论就没有完成度**：不显示百分比、收起进度条，
                //   否则用户会看到"已取消"旁边还挂着上次快照的百分比（1 个对象的计划在 start 快照时曾显示 100.0%）。
                //   "已核对文件/字节/已用时间"是真实发生过的事实，有快照就如实保留。
                ObjectProgressBar.Visibility = Visibility.Collapsed;
                ObjectProgressBar.IsIndeterminate = false;
                PercentText.Text = canceled ? "—" : "无";
                if (!_snapshotSeen)
                {
                    ObjectCountText.Text = "无";
                    FilesCheckedText.Text = "无";
                    BytesCheckedText.Text = "无";
                    ElapsedText.Text = "无";
                }
                else if (canceled)
                {
                    StageDetailText.Text = _rows.Count == 0
                        ? "取消前没有对象出过结论。"
                        : $"取消前已完成核对的对象 {_rows.Count} 个（这些结果没有最终报告，不能当作通过）。";
                }
                FooterHintText.Text = "关闭 ≠ 取消：收起面板后验证状态不变，可再次打开查看。";
                SetReportAvailability(false, canceled
                    ? "本次验证已取消：没有结论，所以不生成验证报告（取消不等于通过）。关闭 ≠ 取消。"
                    : "本次验证未完成：没有报告，所以没有可打开的验证报告。关闭 ≠ 取消。");
                CopySummaryButton.IsEnabled = true;
                return;
            }

            _rows.Clear();
            foreach (var o in report.Objects)
            {
                var path = sourcePaths is not null && sourcePaths.TryGetValue(o.ObjectId, out var mapped) ? mapped : "";
                _rows.Add(VerificationProgressRow.FromResult(o, path));
            }
            ObjectsEmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            ObjectProgressBar.Visibility = Visibility.Visible;
            ObjectProgressBar.IsIndeterminate = false;
            ObjectProgressBar.Value = 100;
            PercentText.Text = "100.0%";
            ObjectCountText.Text = $"{report.Objects.Count}/{report.Objects.Count}";
            FilesCheckedText.Text = report.Objects.Sum(o => o.SourceFiles).ToString("N0");
            BytesCheckedText.Text = Format.Bytes(report.Objects.Sum(o => o.SourceBytes));

            int ok = report.Objects.Count(o => o.Status == "OK");
            int mismatch = report.Objects.Count(o => o.Status == "MISMATCH");
            int unknown = report.Objects.Count(o => o.Status == "UNKNOWN");
            int empty = report.Objects.Count(o => o.Status == "EMPTY");
            OkCountText.Text = ok.ToString("N0");
            MismatchCountText.Text = mismatch.ToString("N0");
            UnknownCountText.Text = unknown.ToString("N0");
            EmptyCountText.Text = empty.ToString("N0");

            State = report.OverallPass
                ? VerificationPanelState.CompletedPassed
                : VerificationPanelState.CompletedWithDifferences;
            OutcomeText.Text = report.OverallPass
                ? "总体结论：通过（文件数与总字节一致）"
                : "总体结论：未通过";
            StatusLineText.Text = outcomeText;
            StageDetailText.Text =
                $"总体结论：{(report.OverallPass ? "通过" : "未通过")}　·　" +
                $"通过 {ok}　存在差异 {mismatch}　无法确认 {unknown}　无可核对数据 {empty}" +
                "　·　L1 只覆盖文件数量与总字节，不等于逐文件 SHA256 全量一致";
            FooterHintText.Text = "关闭 ≠ 取消：收起面板后验证状态不变，可再次打开查看。";
            SetReportAvailability(true,
                "验证报告可打开：内容是**本次已完成验证**的真实证据（L1 文件数与字节对账）。" +
                "关闭 ≠ 取消：收起面板后状态不变。");
            CopySummaryButton.IsEnabled = true;
        });
    }

    /// <summary>恢复同一作业的既有报告投影；不重新验证、不读取磁盘。</summary>
    public void ShowPreviousResult(VerifyReport report, string outcomeText,
        IReadOnlyDictionary<string, string>? sourcePaths = null)
    {
        FinishWith(report, outcomeText, canceled: false, sourcePaths: sourcePaths);
        OnUi(() => State = VerificationPanelState.PreviousResult);
    }

    /// <summary>当前面板状态的**纯文本摘要**（供复制与取证；只由真实投影字段拼成）。</summary>
    public string SummaryForCopy()
    {
        var sb = new StringBuilder();
        sb.AppendLine("PCMig 验证完整性（L1 文件数与字节对账）");
        sb.AppendLine(SubtitleText.Text);
        sb.AppendLine(OutcomeText.Text);
        sb.AppendLine($"进度={PercentText.Text}　对象={ObjectCountText.Text}　文件={FilesCheckedText.Text}　字节={BytesCheckedText.Text}　用时={ElapsedText.Text}");
        sb.AppendLine($"通过={OkCountText.Text}　存在差异={MismatchCountText.Text}　无法确认={UnknownCountText.Text}　无可核对数据={EmptyCountText.Text}");
        sb.AppendLine(StatusLineText.Text);
        foreach (var row in _rows) sb.AppendLine(row.CopyText);
        return sb.ToString();
    }

    // ────────────────────────── 交互 ──────────────────────────

    private async void StartVerifyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_startRequested || _onStartRequested is null) return;
        _startRequested = true;
        StartVerifyButton.IsEnabled = false;
        StartVerifyButton.Content = "验证中…";
        try { await _onStartRequested(); }
        catch (Exception)
        {
            // 页面层会投影真实故障；这里仅恢复可重试入口，不能伪造结论。
            _startRequested = false;
            StartVerifyButton.IsEnabled = true;
            StartVerifyButton.Content = "重新验证";
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_finished || _cancelRequested) return;
        _cancelRequested = true;
        State = VerificationPanelState.Cancelling;
        // 立刻给出真实反馈：按钮不再可点、文案改成"正在取消…"，但**不改写任何结论**
        CancelButton.IsEnabled = false;
        CancelButton.Content = "正在取消…";
        StatusLineText.Text = "已请求取消：正在等待引擎在对象之间安全退出（不会留下假的验证报告）。";
        try { _onCancelRequested?.Invoke(); }
        catch { /* 页面层已不在：按钮状态保持，不影响验证本身 */ }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => RequestClose();

    private void ClosePanelButton_Click(object sender, RoutedEventArgs e) => RequestClose();

    private void RequestClose()
    {
        try { CloseRequested?.Invoke(this, EventArgs.Empty); }
        catch { /* Shell 已不在：面板保持可见，不影响验证 */ }
    }

    private void CopyRowButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is VerificationProgressRow row) CopyToClipboard(row.CopyText);
    }

    private void CopySummaryButton_Click(object sender, RoutedEventArgs e) => CopyToClipboard(SummaryForCopy());

    /// <summary>
    /// ★ m03985 §UI-C/§UI-D ★ 打开验证报告：把"生成 + 用系统默认浏览器打开本地 HTML"交给页面层注册的处理器。
    /// 三条纪律：① 面板自己不做文件操作、不上传、不改任务状态；
    /// ② 生成/打开失败时把**本机路径**留在底栏（可选中复制），绝不假装成功；
    /// ③ 处理期间禁用按钮，避免连点生成多份报告。
    /// </summary>
    private async void OpenReportButton_Click(object sender, RoutedEventArgs e)
    {
        var handler = _onOpenReport;
        if (handler is null)
        {
            SetReportAvailability(false, "界面还没有接上验证报告生成器：请回到「结果与校验」页再试一次。");
            return;
        }

        OpenReportButton.IsEnabled = false;
        FooterHintText.Text = "正在生成本地验证报告 HTML…（只写本机任务目录，不联网、不上传）";
        try
        {
            var result = await handler();
            FooterHintText.Text = result.Message;
            ReportExplanation = result.Message;
            // 只要本机确实有一份报告文件，就允许再点一次（例如浏览器没起来、用户想重开）。
            OpenReportButton.IsEnabled = result.Path is not null && System.IO.File.Exists(result.Path);
            if (result.Opened)
                StatusLineText.Text = "验证报告已用系统默认浏览器打开（本地 HTML，可离线查看）。";
            else
                StageDetailText.Text = "报告打开未成功：底栏给出了本机路径，可手动打开或复制。";
        }
        catch (Exception ex)
        {
            var message = $"打开验证报告时出错：{ex.Message}。验证结论本身不受影响，可在本机报告目录中手动打开。";
            SetReportAvailability(true, message);
            _ = ex;
        }
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text ?? "");
            Clipboard.SetContent(package);
        }
        catch { /* 剪贴板失败绝不影响验证与面板 */ }
    }

    // ────────────────────────── 内部 ──────────────────────────

    private void OnUi(Action action)
    {
        var queue = DispatcherQueue;
        if (queue is null) return;
        if (queue.HasThreadAccess) { action(); return; }
        queue.TryEnqueue(() =>
        {
            try { action(); }
            catch { /* 界面刷新失败绝不影响验证（只读投影） */ }
        });
    }

    private static string FormatDuration(double ms)
    {
        if (ms < 1000) return ms.ToString("0") + " ms";
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.TotalMinutes >= 1
            ? $"{(int)ts.TotalMinutes} 分 {ts.Seconds} 秒"
            : $"{ts.TotalSeconds:0.0} 秒";
    }
}

/// <summary>
/// 对象结论行（只读快照）。四态结论、两侧对账、差异样例、结论原因与证据都在这里，
/// 全部由 <see cref="ObjectVerifyResult"/> 的真实字段拼成；面板不新增任何判定。
/// </summary>
public sealed class VerificationProgressRow
{
    private static readonly Color SuccessColor = Color.FromArgb(0xFF, 0x16, 0xA3, 0x6E);
    private static readonly Color DangerColor = Color.FromArgb(0xFF, 0xD6, 0x47, 0x58);
    private static readonly Color WarningColor = Color.FromArgb(0xFF, 0xD9, 0x95, 0x18);
    private static readonly Color MutedColor = Color.FromArgb(0xFF, 0x71, 0x86, 0xA4);

    public string ObjectId { get; init; } = "";
    public string PathShort { get; init; } = "";
    public string PathFull { get; init; } = "";
    public string StatusText { get; init; } = "";
    public Brush StatusBackground { get; init; } = Chip(MutedColor);
    public Brush StatusForeground { get; init; } = new SolidColorBrush(MutedColor);
    public string CountsText { get; init; } = "";
    public string CountsDetailText { get; init; } = "";
    public string DiffText { get; init; } = "";
    public string ReasonText { get; init; } = "";
    public string EvidenceText { get; init; } = "";
    public string CopyText { get; init; } = "";

    private static SolidColorBrush Chip(Color c) => new(Color.FromArgb(0x24, c.R, c.G, c.B));

    private static Brush Foreground(string status) => new SolidColorBrush(status switch
    {
        "OK" => SuccessColor,
        "MISMATCH" => DangerColor,
        "UNKNOWN" => WarningColor,
        _ => MutedColor
    });

    /// <summary>运行中"已经出过结论但报告还没到"的占位行（不声明通过/失败）。</summary>
    public static VerificationProgressRow Provisional(string objectId, string path) => new()
    {
        ObjectId = objectId,
        PathShort = path,
        PathFull = path,
        StatusText = "已核对",
        CountsText = "",
        CountsDetailText = "结论以最终报告为准（当前只证明引擎已经核对完这个对象）。",
        CopyText = $"{objectId}\t{path}\t已核对（无最终报告）"
    };

    /// <summary>由最终报告的对象结论构造行（报告的字段是唯一真值来源）。</summary>
    public static VerificationProgressRow FromResult(ObjectVerifyResult o, string sourcePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"对象：{o.ObjectId}");
        sb.AppendLine($"源位置：{(string.IsNullOrWhiteSpace(sourcePath) ? "（计划里没有该对象的源路径）" : sourcePath)}");
        sb.AppendLine($"结论：{o.StatusText}（{o.Status}）");
        sb.AppendLine($"源：{o.SourceFiles:N0} 文件 / {Format.Bytes(o.SourceBytes)}");
        sb.AppendLine($"目标：{o.TargetFiles:N0} 文件 / {Format.Bytes(o.TargetBytes)}");
        if (o.PlannedFiles > 0 || o.PlannedBytes > 0)
            sb.AppendLine($"计划：{o.PlannedFiles:N0} 文件 / {Format.Bytes(o.PlannedBytes)}");
        if (!string.IsNullOrWhiteSpace(o.UnknownReason)) sb.AppendLine($"原因码：{o.UnknownReason}");

        var diffs = new List<string>();
        if (o.SourceUnavailable) diffs.Add("来源不可达：源根打不开（共享被关闭 / 未挂载 / 不存在），本次没有核对任何数据。");
        if (o.TargetUnavailable) diffs.Add("目标不可达：目标根打不开，不能据此判缺失。");
        if (o.EnumerationSkipped > 0) diffs.Add($"枚举不完整：有 {o.EnumerationSkipped} 个条目被跳过（权限被拒或瞬态 IO 失败），统计不完整。");
        // ★ F-1（2026-10-13 定点修复）★ 只有确证缺失（MISMATCH）才写"确认缺失"；其余说"无法确认"。
        if (o.MissingSamples.Count > 0)
            diffs.Add(o.MissingConfirmed
                ? $"确认缺失 {o.MissingSamples.Count} 个（源有目标无）：{Join(o.MissingSamples)}"
                : $"无法确认（未核实，**不得当作缺失**）{o.MissingSamples.Count} 个：{Join(o.MissingSamples)}");
        if (o.ExtraOnTarget.Count > 0) diffs.Add($"目标多余 {o.ExtraOnTarget.Count} 个（目标有源无，**不自动清理**）：{Join(o.ExtraOnTarget)}");
        if (o.SizeMismatched > 0) diffs.Add($"大小不一致 {o.SizeMismatched:N0} 个：{Join(o.SizeMismatchSamples)}");
        if (o.HashMismatched > 0) diffs.Add($"内容哈希不一致 {o.HashMismatched} 个（抽样 {o.HashSampled} 个）。");
        if (o.HashReadFailed > 0) diffs.Add($"哈希样本读取失败 {o.HashReadFailed} 个（≠ 内容不一致：可能是共享冲突 / 权限 / 源中断）。");
        if (o.BothSidesEmpty) diffs.Add("两侧都是 0 文件 0 字节。");
        if (diffs.Count == 0) diffs.Add("未发现数量、字节、缺失或多余层面的差异。");

        var reason = o.Status switch
        {
            "OK" => "结论原因：源与目标的文件数、总字节一致（L1 只到这里；内容级一致性未在此层级核对）。",
            "MISMATCH" => "结论原因：存在可复述的结构差异（见上）。" + (o.AllowsDestructiveRepair ? "允许进入破坏性修复（仅确认差异才放行）。" : ""),
            "UNKNOWN" => "结论原因：无法确认 —— 不满足核对前提，界面与报告都不会说成通过。",
            "EMPTY" => "结论原因：计划与实测都没有数据可核对。",
            "CANCELED" => "结论原因：用户取消，没有结论。",
            _ => "结论原因：未分类状态。"
        };

        return new VerificationProgressRow
        {
            ObjectId = o.ObjectId,
            PathShort = sourcePath,
            PathFull = $"源位置（完整）：{(string.IsNullOrWhiteSpace(sourcePath) ? "（缺失）" : sourcePath)}",
            StatusText = o.StatusText,
            StatusBackground = Chip(o.Status switch
            {
                "OK" => SuccessColor,
                "MISMATCH" => DangerColor,
                "UNKNOWN" => WarningColor,
                _ => MutedColor
            }),
            StatusForeground = Foreground(o.Status),
            CountsText = $"{o.SourceFiles:N0} / {o.TargetFiles:N0} 文件",
            CountsDetailText =
                $"两侧对账：源 {o.SourceFiles:N0} 文件 / {Format.Bytes(o.SourceBytes)}　·　" +
                $"目标 {o.TargetFiles:N0} 文件 / {Format.Bytes(o.TargetBytes)}" +
                (o.PlannedFiles > 0 || o.PlannedBytes > 0 ? $"　·　计划 {o.PlannedFiles:N0} 文件 / {Format.Bytes(o.PlannedBytes)}" : ""),
            DiffText = string.Join("\n", diffs),
            ReasonText = reason,
            EvidenceText = sb.ToString().TrimEnd(),
            CopyText = sb.ToString().TrimEnd() + "\n" + string.Join("\n", diffs) + "\n" + reason
        };
    }

    private static string Join(List<string> samples)
    {
        const int max = 8;
        var head = string.Join("、", samples.Take(max));
        return samples.Count > max ? head + $"…（共 {samples.Count} 条，样例上限 50）" : head;
    }
}