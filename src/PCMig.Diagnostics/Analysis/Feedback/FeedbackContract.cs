using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;

namespace PCMig.Diagnostics.Analysis.Feedback;

/// <summary>
/// 一条期望的类别。**它决定超时口径**，因此不是装饰性字段：
///   · Immediate —— 用户点完必须马上看到的东西（按钮态/状态句）；
///   · Deferred —— 允许异步但必须有结果（域层受理/对象活动）；
///   · ExternalWait —— 依赖外部世界，**不允许短超时**（SMB/DNS/对象边界/robocopy 退避）；
///   · Optional —— 可以有也可以没有（例如空闲期的额外提示）；
///   · Failure —— 失败路径的期望（拒绝/异常）——它出现即为合法终点。
/// </summary>
public enum ExpectationKind
{
    Immediate = 0,
    Deferred = 1,
    ExternalWait = 2,
    Optional = 3,
    Failure = 4,
}

/// <summary>
/// 一条期望：等哪个稳定事件、属于哪一类、允许多久。
///
/// ★ FIX BATCH 3 ★ <paramref name="DeadlineBreachIsFailure"/>：这条期望是不是**信任关键**的
/// —— 即"超期"到底意味着"还没等到"（普通 ExternalWait）还是"用户要求了业务动作、但业务效果
/// 从未达成"（信任缺口）。后者必须让诊断降级、开出事件卡，不能再以"还在等"糊过去。
/// 默认 false ⇒ 既有契约字面量不受影响。
/// </summary>
public sealed record ExpectedStep(
    string ExpectationId,
    ExpectationKind Kind,
    IReadOnlyList<string> ExpectedEventNames,
    string Description,
    bool DeadlineBreachIsFailure = false);

/// <summary>
/// 一个动作的反馈契约（**只描述"应当观察到什么"**，不能执行命令、不能改任何业务状态）。
/// </summary>
public sealed record FeedbackContract(
    string ContractId,
    string ActionKind,
    int Version,
    IReadOnlyList<string> TerminalEventNames,
    IReadOnlyList<ExpectedStep> Steps,
    int ImmediateTimeoutMs,
    int DeferredTimeoutMs,
    int ExternalWaitTimeoutMs);

/// <summary>
/// 契约注册表（方案 §10）。**ActionKind 是稳定 token**，与 WinUI 的 ActionKinds 常量一一对应。
///
/// 超时口径的纪律：
///   · 绝不用"统一 100ms 判所有操作失败"（合作式暂停要等对象边界，可能几分钟；
///     robocopy 的 /R /W 退避本身就要几十秒）；
///   · ExternalWait 的默认预算刻意放得很宽（分钟级），且**只报告"还没观察到"**，
///     不报告"失败了"——**除非**该步骤被标为 <c>DeadlineBreachIsFailure</c>（信任关键步骤：
///     超期即"用户要求了业务动作、业务效果从未达成"，必须判失败，见 FIX BATCH 3 §6）；
///   · 具体毫秒值是**候选配置**，未经真实延迟分布校准前不宣称 SLA。
/// </summary>
public sealed class FeedbackContractRegistry
{
    private readonly Dictionary<string, FeedbackContract> _byActionKind = new(StringComparer.Ordinal);

    public FeedbackContractRegistry(IEnumerable<FeedbackContract> contracts)
    {
        foreach (var contract in contracts)
        {
            if (_byActionKind.ContainsKey(contract.ActionKind))
                throw new InvalidOperationException("ActionKind 的反馈契约重复：" + contract.ActionKind);
            _byActionKind[contract.ActionKind] = contract;
        }
    }

    public int Count => _byActionKind.Count;

    public bool TryGet(string actionKind, out FeedbackContract contract) =>
        _byActionKind.TryGetValue(actionKind, out contract!);

    public IReadOnlyCollection<FeedbackContract> All => _byActionKind.Values;

    public static FeedbackContractRegistry CreateDefault() => new(new[]
    {
        new FeedbackContract("connect.v1", "Connect", 1,
            new[] { UiEvents.ActionCompleted.Name, UiEvents.ActionRejected.Name, UiEvents.ActionFaulted.Name },
            new[]
            {
                new ExpectedStep("connect.immediate", ExpectationKind.Immediate,
                    new[] { UiEvents.CommandStarted.Name }, "动作进入处理路径"),
                new ExpectedStep("connect.deferred", ExpectationKind.Deferred,
                    new[] { PreflightEvents.PreflightStarted.Name, NetEvents.ProbeStarted.Name },
                    "预检/网络探测开始"),
                new ExpectedStep("connect.external", ExpectationKind.ExternalWait,
                    new[] { PreflightEvents.PreflightCompleted.Name, UiEvents.ProjectionChanged.Name },
                    "预检结束并产生可见结果"),
            },
            ImmediateTimeoutMs: 1_500,
            DeferredTimeoutMs: 10_000,
            ExternalWaitTimeoutMs: 180_000),

        new FeedbackContract("prepare.v1", "Prepare", 1,
            new[] { UiEvents.ActionCompleted.Name, UiEvents.ActionRejected.Name, UiEvents.ActionFaulted.Name },
            new[]
            {
                new ExpectedStep("prepare.immediate", ExpectationKind.Immediate,
                    new[] { UiEvents.CommandStarted.Name }, "动作进入处理路径"),
                new ExpectedStep("prepare.external", ExpectationKind.ExternalWait,
                    new[] { PlanEvents.PlanCreated.Name, PlanEvents.PlanEmpty.Name }, "扫描与计划产出"),
            },
            ImmediateTimeoutMs: 1_500,
            DeferredTimeoutMs: 30_000,
            ExternalWaitTimeoutMs: 600_000),

        new FeedbackContract("transfer.v1", "Start", 1,
            new[] { UiEvents.ActionCompleted.Name, UiEvents.ActionRejected.Name, UiEvents.ActionFaulted.Name },
            new[]
            {
                new ExpectedStep("start.immediate", ExpectationKind.Immediate,
                    new[] { UiEvents.CommandStarted.Name }, "动作进入处理路径"),
                new ExpectedStep("start.deferred", ExpectationKind.Deferred,
                    new[] { TransferEvents.JobRunStarted.Name }, "传输运行开始"),
                new ExpectedStep("start.external", ExpectationKind.ExternalWait,
                    new[] { TransferEvents.ObjectStarted.Name, TransferEvents.JobRunCompleted.Name }, "对象活动或运行结束"),
            },
            ImmediateTimeoutMs: 1_500,
            DeferredTimeoutMs: 20_000,
            ExternalWaitTimeoutMs: 300_000),

        new FeedbackContract("resume.v1", "Resume", 1,
            new[] { UiEvents.ActionCompleted.Name, UiEvents.ActionRejected.Name, UiEvents.ActionFaulted.Name },
            new[]
            {
                new ExpectedStep("resume.immediate", ExpectationKind.Immediate,
                    new[] { TransferEvents.PauseRequestCleared.Name, UiEvents.CommandStarted.Name }, "清除暂停请求"),
                new ExpectedStep("resume.external", ExpectationKind.ExternalWait,
                    new[] { TransferEvents.JobRunStarted.Name, TransferEvents.Resumed.Name }, "运行重新开始"),
            },
            ImmediateTimeoutMs: 2_000,
            DeferredTimeoutMs: 20_000,
            ExternalWaitTimeoutMs: 300_000),

        // ★ FIX BATCH 3 ★ pause.v1 → pause.v2：
        //   v1 只表达"请求被写下 + 边界到达"，且外部等待预算 3_600_000 ms（1 小时）⇒
        //   "请求受理成功、引擎从未停住"这条真实故障在诊断里永远是"还在等"，健康 verdict 仍是 healthy。
        //   v2 把**业务效果**写成可判定的期望：兑现判据 = 引擎真的停住（TRN-011/TRN-013），
        //   失败终点 = 引擎自报停不住（TRN-023），期限 = 引擎硬失败 SLA + 诊断宽限（同一个 ActionSla 真值）。
        //   R-002：TRN-012 PauseBoundaryReached 全仓没有发射点（死事件），不再作为兑现判据。
        new FeedbackContract("pause.v2", "Pause", 2,
            new[] { UiEvents.ActionCompleted.Name, UiEvents.ActionRejected.Name, UiEvents.ActionFaulted.Name },
            new[]
            {
                // ① 受理：请求文件确实写下去了。**它成立绝不等于暂停已生效。**
                new ExpectedStep("pause.immediate", ExpectationKind.Immediate,
                    new[] { TransferEvents.PauseRequestWriteResult.Name }, "暂停请求文件写入结果"),
                // ② 兑现：引擎真的把 worker 停住（这才是"已暂停"）。
                new ExpectedStep("pause.external", ExpectationKind.ExternalWait,
                    new[] { TransferEvents.PauseObserved.Name, TransferEvents.Paused.Name },
                    "引擎真的停住（达成暂停）",
                    DeadlineBreachIsFailure: true),
                // ③ 失败终点：引擎在 SLA 内停不住并如实自报 —— 一出现就是失败结论。
                new ExpectedStep("pause.failure", ExpectationKind.Failure,
                    new[] { TransferEvents.PauseFailed.Name },
                    "引擎在硬失败 SLA 内停不住（业务效果未达成）"),
            },
            ImmediateTimeoutMs: 2_000,
            // 档位排序（External ≥ Deferred ≥ Immediate）必须成立：暂停没有 deferred 步骤，
            // 这个值只是天花板，取在兑现期限之内（旧值 20_000 > 12_000 会破坏排序不变式）。
            DeferredTimeoutMs: 10_000,
            ExternalWaitTimeoutMs: ActionSla.PauseFulfillmentDeadlineMs),

        new FeedbackContract("stop.v1", "Stop", 1,
            new[] { UiEvents.ActionCompleted.Name, UiEvents.ActionFaulted.Name },
            new[]
            {
                new ExpectedStep("stop.immediate", ExpectationKind.Immediate,
                    new[] { UiEvents.CommandStarted.Name }, "停止请求被受理"),
                new ExpectedStep("stop.external", ExpectationKind.ExternalWait,
                    new[] { TransferEvents.StopObserved.Name, TransferEvents.JobRunCompleted.Name,
                            RobocopyEvents.ProcessExited.Name },
                    "运行收尾"),
            },
            ImmediateTimeoutMs: 1_500,
            DeferredTimeoutMs: 20_000,
            ExternalWaitTimeoutMs: 120_000),

        new FeedbackContract("verify.v1", "Verify", 1,
            new[] { UiEvents.ActionCompleted.Name, UiEvents.ActionRejected.Name, UiEvents.ActionFaulted.Name },
            new[]
            {
                new ExpectedStep("verify.immediate", ExpectationKind.Immediate,
                    new[] { VerifyEvents.VerifyStarted.Name }, "验证开始"),
                new ExpectedStep("verify.external", ExpectationKind.ExternalWait,
                    new[] { VerifyEvents.Completed.Name }, "验证完成（含证据完整性）"),
            },
            ImmediateTimeoutMs: 2_000,
            DeferredTimeoutMs: 30_000,
            ExternalWaitTimeoutMs: 3_600_000),

        new FeedbackContract("repair.v1", "Repair", 1,
            new[] { UiEvents.ActionCompleted.Name, UiEvents.ActionRejected.Name, UiEvents.ActionFaulted.Name },
            new[]
            {
                new ExpectedStep("repair.immediate", ExpectationKind.Immediate,
                    new[] { UiEvents.CommandStarted.Name }, "动作进入处理路径"),
                new ExpectedStep("repair.external", ExpectationKind.ExternalWait,
                    new[] { RepairEvents.RepairTargetsCollected.Name, RepairEvents.RepairNoTargets.Name,
                            RobocopyEvents.ProcessStarted.Name },
                    "修复目标收集或重拷开始"),
            },
            ImmediateTimeoutMs: 2_000,
            DeferredTimeoutMs: 30_000,
            ExternalWaitTimeoutMs: 3_600_000),

        new FeedbackContract("export.v1", "Export", 1,
            new[] { UiEvents.ActionCompleted.Name, UiEvents.ActionFaulted.Name },
            new[]
            {
                new ExpectedStep("export.immediate", ExpectationKind.Immediate,
                    new[] { DiagnosticsEvents.ExportStarted.Name }, "导出开始"),
                new ExpectedStep("export.external", ExpectationKind.ExternalWait,
                    new[] { DiagnosticsEvents.ExportCompleted.Name, DiagnosticsEvents.ExportFailed.Name }, "导出结束"),
            },
            ImmediateTimeoutMs: 2_000,
            DeferredTimeoutMs: 30_000,
            ExternalWaitTimeoutMs: 600_000),
    });
}