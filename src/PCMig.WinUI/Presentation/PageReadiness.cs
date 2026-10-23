using System;
using System.ComponentModel;
using System.IO;
using PCMig.Core.Models;
// ★ v0.5.4 Preview.2（P1-A）★ 诚实状态句要读引擎真值的"连续字节来源"枚举
//   ProgressTruthSource（InFlightSource）——它定义在 Core.Transfer 下。
using PCMig.Core.Transfer;

namespace PCMig.WinUI.Presentation;

/// <summary>
/// 页面就绪状态的**薄适配层**（WinUI Presentation 内部）。
///
/// 架构与职责边界（必须遵守）：
///     WinUI View  →  本适配层  →  ① 已有的 ConnectionViewModel（Step 1：连接/共享）
///                                 ② MigrationSessionViewModel（Step 2/3/4：计划/进度/验证/报告）
///   本类**只做投影**：把这两个状态源翻译成各页要显示的文案与布尔量，
///   不含任何迁移 / Robocopy / 扫描 / 规划 / 校验规则，也不新建第二套状态机。
///   Core 绝不反向依赖本层。
///
/// 为什么需要它：四页都要回答同一个问题——"这一步现在处于什么状态"（Empty / Not Ready / Waiting / Ready）。
/// 如果每个页面各自去读 ConnectionViewModel 并各写一套文案判断，就会出现四份口径；
/// 集中在这里之后，改一处四页同变。
///
/// 注意：**页面可见性不由本层决定**。导航可用性与业务可用性是分开的：
/// 无论就绪与否，Step2/3/4 都可以被打开并显示 Not Ready / Waiting 状态。
/// </summary>
public sealed class PageReadiness : ObservableObject
{
    private readonly ConnectionViewModel _connection;

    /// <summary>
    /// 迁移会话状态源（可选）。为 null 时本层退回"只看连接状态"的旧口径（向后兼容既有调用方）。
    /// 非 null 时 Step3/Step4/WorkspaceHint 一律由**会话的真实状态**判定，不再有半真/纯假的文案。
    /// </summary>
    private readonly MigrationSessionViewModel? _session;

    public PageReadiness(ConnectionViewModel connection, MigrationSessionViewModel? session = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _session = session;
        _connection.PropertyChanged += OnConnectionChanged;
        _connection.Shares.CollectionChanged += (_, _) => Refresh();
        if (_session is not null) _session.PropertyChanged += OnSessionChanged;
        Refresh();
    }

    /// <summary>
    /// 会话侧只订阅**状态类**属性名（不是全部）：Percent / ProgressText 之类在传输期间每秒变化多次，
    /// 若一并触发 Refresh() 会把四个页面的文案广播刷成高频风暴。状态类属性变化才需要重算页面文案。
    /// </summary>
    private static readonly string[] SessionWatchedProperties =
    [
        nameof(MigrationSessionViewModel.Phase),
        nameof(MigrationSessionViewModel.HasJob),
        nameof(MigrationSessionViewModel.HasPlan),
        nameof(MigrationSessionViewModel.IsRunning),
        nameof(MigrationSessionViewModel.IsPaused),
        nameof(MigrationSessionViewModel.TargetRoot),
        nameof(MigrationSessionViewModel.StatusMessage),
        nameof(MigrationSessionViewModel.LastVerifyResultText),
        nameof(MigrationSessionViewModel.CanVerifyNow),
        nameof(MigrationSessionViewModel.FailedObjects),
        // ★ v0.5.4 Preview.2（P1-A 返修，2026-10-15）★
        //   A 类诚实状态句的判据来自 LastTruth.InFlightSource，但 LastTruth 每帧都变
        //   （ApplySnapshot:3784），**不能**直接订阅 —— 那会把四页文案刷成高频风暴
        //   （见本类上方 L48-51 的既有纪律）。改订阅它的**低频派生位** HasTrustedInflightSource：
        //   该属性仅在"有可信来源 ⇄ 无可信来源"真正翻转时才发通知，传输期间同一状态不发。
        nameof(MigrationSessionViewModel.HasTrustedInflightSource),
    ];

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null) { Refresh(); return; }
        foreach (var watched in SessionWatchedProperties)
        {
            if (string.Equals(watched, e.PropertyName, StringComparison.Ordinal)) { Refresh(); return; }
        }
    }

    private void OnConnectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        Diag($"ConnectionViewModel.PropertyChanged: {e.PropertyName} ; Host=[{_connection.Host}] ; Username=[{_connection.Username}] ; CanConnect={_connection.CanConnect} ; Benchmark={_connection.BenchmarkOnConnect} ; ManualShare=[{_connection.ManualShareName}]");
        if (e.PropertyName is nameof(ConnectionViewModel.IsConnected)
            or nameof(ConnectionViewModel.Host)
            or nameof(ConnectionViewModel.IsConnecting)
            or nameof(ConnectionViewModel.DeviceSummary))
        {
            Refresh();
        }
    }

    /// <summary>
    /// 仅诊断用（PCMIG_DIAG=1）：把 ViewModel 的属性变更落到 exe 旁的 poc-twoway.log。
    /// 用途：用**真实鼠标键盘**输入后，日志里若出现该值，即直接证明 TwoWay 绑定把值写回了 ViewModel，
    /// 不必依赖 UIA 注入（UIA 注入会让被测应用崩溃）或按钮可用性这类间接推断。
    /// 放在本适配层（而不是冻结的 ConnectionViewModel）里，避免为了取证去改冻结代码。
    /// </summary>
    private static void Diag(string message)
    {
        if (Environment.GetEnvironmentVariable("PCMIG_DIAG") != "1") return;
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "poc-twoway.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 诊断失败绝不影响功能
        }
    }

    private void Refresh()
    {
        Raise(nameof(IsConnected));
        Raise(nameof(ShareCount));
        Raise(nameof(HasShares));
        Raise(nameof(SourceSummary));
        Raise(nameof(Step2StateText));
        Raise(nameof(Step2EmptyTitle));
        Raise(nameof(Step2EmptyBody));
        Raise(nameof(Step3StateText));
        Raise(nameof(Step4StateText));
        Raise(nameof(WorkspaceHint));
    }

    public bool IsConnected => _connection.IsConnected;
    public bool HasShares => _connection.Shares.Count > 0;
    public int ShareCount => _connection.Shares.Count;

    /// <summary>顶部链路胶囊用到的源侧摘要（与 Shell 的源状态胶囊同一来源，口径一致）。</summary>
    public string SourceSummary => IsConnected ? $"已连接 {_connection.Host.Trim()}" : "未指定旧电脑";

    // ── Step 2 ───────────────────────────────────────────────────────────────
    public string Step2StateText => IsConnected
        ? (HasShares ? $"已加载 {ShareCount} 个共享，可开始勾选内容。" : "已连接，但对方没有列出任何共享。")
        : "尚未连接旧电脑。";

    public string Step2EmptyTitle => IsConnected ? "还没有可迁移的内容。" : "请连接旧电脑后加载目录";

    public string Step2EmptyBody => IsConnected
        ? "对方已连接但没有列出共享；可在 Step 1 用「+ 添加共享」手动输入共享名（例如 D$）。"
        : "连接成功后将在这里展示可选择的文件与文件夹，支持按需勾选或选择整个共享。";

    // ── Step 3 / Step 4 ──────────────────────────────────────────────────────
    // 说明（本包变更）：原先这两处是"半真 / 纯假"文案（只看 IsConnected），
    // 现在改为由 MigrationSessionViewModel 的**真实状态**判定；session 为 null 时退回旧口径。
    // ── ★ v0.5.4 Preview.2（Round3 审查返修，2026-10-15）★ ──────────────────────
    //   【返修原因】原实现把分支顺序写成本属性里的一条长三元链，其顺序为
    //       … IsRunning && IsPaused / **IsRunning（无条件）** / Completed… / (A 类缺源句) / …
    //   其中 A 类诚实缺源句要求 IsRunning==true，而该条件已被更早的"无条件 IsRunning"分支
    //   完全覆盖 ⇒ **该分支永远不可达，界面上一次也不会显示**；而 HonestActivityStateTests
    //   的 13 个用例全绿，因为它们只测纯函数，测不到调用点的条件顺序。
    //
    //   【修法】把**分支顺序本身**也移进 Core 的纯选择器 Step3StateSelector.Select()，
    //   本属性只负责"采集标量 + 调用 + 返回"。这样：
    //     · 顺序缺陷可以在现有 xUnit 里被**直接断言**（下一步会新增 PageReadinessStep3BranchTests）；
    //     · 生产代码与测试**共用同一个选择器**，不存在"测的是副本"；
    //     · WinUI 侧不再持有第二份分支口径（本类头顶注释的既有纪律）。
    public string Step3StateText => Step3StateSelector.Select(new Step3StateSelector.Step3Input(
        HasSession: _session is not null,
        IsConnected: IsConnected,
        IsRunning: _session?.IsRunning ?? false,
        IsPaused: _session?.IsPaused ?? false,
        Phase: _session?.Phase ?? default,
        HasTruthFrame: _session?.LastTruth is not null,
        HasTrustedInflightSource: _session?.HasTrustedInflightSource ?? false,
        ObjectText: _session?.ObjectText ?? string.Empty,
        ProgressText: _session?.ProgressText ?? string.Empty,
        Percent: _session?.Percent ?? 0.0,
        FailedObjects: _session?.FailedObjects ?? 0,
        PhaseText: _session?.PhaseText ?? string.Empty,
        PlanBytesText: _session?.PlanBytesText ?? string.Empty,
        CanStart: _session?.CanStart ?? false,
        HasPlan: _session?.HasPlan ?? false,
        HasJob: _session?.HasJob ?? false,
        JobIdText: _session?.JobIdText ?? string.Empty));

    public string Step4StateText => _session is null
        ? (IsConnected
            ? "已就绪：迁移完成后可在此做基础一致性检查并查看报告。"
            : "尚无校验结果（需要先完成迁移）。")
        : !string.IsNullOrEmpty(_session.LastVerifyResultText)
            ? _session.LastVerifyResultText
        // ── A.5（P1-3）：同样拆开两态（"已完成" vs "未完整完成"）──
        : _session.Phase == JobPhase.Completed
            ? "迁移已完成，可在本页做基础一致性检查并打开报告。" + MigrationSessionViewModel.NoOverclaimNote
        : _session.Phase == JobPhase.CompletedWithErrors
            ? $"迁移**未完整完成**（实际完成 {_session.Percent:0.0}%，有失败对象）：请先看本页失败清单，" +
              "可在本页做基础一致性检查并打开报告，再点「尝试修复」或「恢复任务」补齐。" +
              MigrationSessionViewModel.NoOverclaimNote
        : _session.HasJob
            ? "尚无校验结果：可先「验证」（需有计划且源可访问），或直接「打开报告」。" +
              (_session.CanVerifyNow ? string.Empty : "　（" + _session.VerifyBlockedReason + "）")
            : "尚无校验结果（需要先完成 Step 1 连接与 Step 2 计划，并完成一次迁移）。";

    /// <summary>工作区通用提示（四个页面共用一句话，避免各页各写）。目标侧改为读会话的真实目标根。</summary>
    public string WorkspaceHint => $"源：{SourceSummary}｜目标：{SessionTargetText}";

    /// <summary>
    /// 目标侧显示文案：唯一来源是会话的 TargetRoot（未指定时如实写"未指定目标"，
    /// 不再像改动前那样硬编码）。会话缺失时退回"未指定目标"。
    /// </summary>
    private string SessionTargetText =>
        _session is null || string.IsNullOrWhiteSpace(_session.TargetRoot) ? "未指定目标" : _session.TargetRoot;
}