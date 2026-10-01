using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;

namespace PCMig.Diagnostics.Analysis.Rules;

/// <summary>
/// PERSISTENCE_WRITE_FAILED：存档/回执的真实写入失败或被跳过。
///
/// 为什么它值得成为第一批规则：**"SaveReceipt() 返回了"与"回执真的落盘了"是两件事**
/// （目标已存在且被占用时 WriteAtomic 只告警并返回）。这条规则把两者分开，并给出严重度差异：
/// Receipt/Plan 写失败 = 数据可靠性风险（Error）；JobState 视图写失败 = 可恢复（Warning）。
/// </summary>
public sealed class PersistenceWriteFailureRule : IDiagnosticRule
{
    private static readonly string[] Codes =
    {
        PersistenceEvents.WriteFailed.Name,
        PersistenceEvents.WriteSkipped.Name,
    };

    public string RuleId => "PERSISTENCE_WRITE_FAILED";
    public int Version => 1;

    // 这是**正向观测**（我们确实看到了失败），不依赖"没看到什么"，因此不要求证据完整。
    public bool RequiresCompleteEvidence => false;

    public IReadOnlyCollection<string> WatchedEventCodes => Codes;

    public RuleOutcome? Evaluate(in DiagnosticEvent evt, RuleContext context)
    {
        var payload = evt.Payload as PstWritePayload;
        var artifactKind = payload?.ArtifactKind ?? "Unknown";
        var stage = payload?.Stage ?? "unknown";
        var reason = payload?.ReasonCode ?? evt.ExceptionType ?? "unknown";
        var skipped = evt.Descriptor.Name == PersistenceEvents.WriteSkipped.Name;

        // 工件风险分级：Receipt / Plan 直接关系数据正确性；JobState 只是视图。
        var critical = artifactKind is "Receipt" or "Plan";
        var severity = critical ? DiagnosticLevel.Error : DiagnosticLevel.Warning;

        var incidentId = "PERSISTENCE_WRITE_FAILED|" + artifactKind + "|" + (evt.JobId ?? "-");
        var symptom = skipped ? "PERSISTENCE_WRITE_SKIPPED" : "PERSISTENCE_WRITE_FAILED";

        var incident = new Incident(
            incidentId, RuleId, Version, symptom, severity,
            evt.TimestampUtc, evt.TimestampUtc,
            jobId: evt.JobId, objectId: evt.ObjectId, operationId: evt.OperationId, runGeneration: evt.RunGeneration);

        var update = new IncidentUpdate
        {
            AtUtc = evt.TimestampUtc,
            Fact = new IncidentEvidence(evt.Ref, evt.Descriptor.Name, IncidentEvidence.Fact),
            Severity = severity,
            Confidence = ConfidenceBand.ConfirmedObservation,
            ConfidenceRationale = skipped
                ? "直接观测：目标文件已存在且覆盖被拒绝 ⇒ 写入被跳过（不是成功）"
                : "直接观测：写入在 " + stage + " 阶段抛出 " + reason,
            BreakPoint = "Persistence." + stage,
            EvidenceIncomplete = !context.EvidenceComplete,
            LossEpoch = context.LossEpoch == 0 ? null : context.LossEpoch,
            UserFacingSummary = critical
                ? $"任务存档（{artifactKind}）未能写入：数据可靠性表述必须以回执为准，请检查目标目录是否可写/被占用。"
                : $"状态视图（{artifactKind}）未能写入：不影响已迁移数据，但进度显示可能不准。",
            TechnicalSummary = $"artifactKind={artifactKind} stage={stage} destinationExisted={payload?.DestinationExisted} reason={reason}",
            Candidates = new[]
            {
                new IncidentCandidate("file-locked-by-external-process",
                    "外部程序（杀毒/备份/编辑器）占用目标文件时，覆盖会被拒绝", ConfidenceBand.Medium, Refuted: false),
                new IncidentCandidate("permission-denied",
                    "目标目录权限不足或文件只读", ConfidenceBand.Medium, Refuted: false),
                new IncidentCandidate("disk-full",
                    "目标盘空间不足（写入临时文件或 rename 失败）", ConfidenceBand.Low, Refuted: evt.Win32Error is not (112 or 39)),
                new IncidentCandidate("transient-io",
                    "暂时性 IO 抖动；冷却窗口后会自动恢复（既有自愈逻辑）",
                    ConfidenceBand.Low, Refuted: !skipped),
            },
            SuggestedChecks = new[]
            {
                "确认 Job 目录（含 receipts 子目录）当前可写、未被其它程序锁定",
                critical ? "核对回执目录：缺失的回执意味着该对象进度只能由 job-state.json 推断" : "核对 job-state.json 的 LastUpdateUtc 是否长时间未更新",
                "若是空间问题，释放目标盘空间后 resume（已完成对象不会重传）",
            },
        };

        incident.Merge(update);
        return new RuleOutcome(incident, IsNew: true, ShouldTriggerFlight: severity >= DiagnosticLevel.Error);
    }
}

/// <summary>
/// DIAGNOSTICS_DEGRADED：诊断系统**自己**已经降级（丢事件、存储失败、分析滞后）。
///
/// 这条规则的特殊性：它必须在"证据已经不完整"的情况下**继续工作**（它本身就是关于不完整的），
/// 因此 RequiresCompleteEvidence = false；并且它必须是 ConfirmedObservation —— 这些计数是我们直接量的。
/// 它同时是**其他所有"缺事件"规则的置信度开关**。
/// </summary>
public sealed class DiagnosticsDegradedRule : IDiagnosticRule
{
    private static readonly string[] Codes =
    {
        DiagnosticsEvents.EventsDropped.Name,
        DiagnosticsEvents.CriticalLost.Name,
        DiagnosticsEvents.StorageFailed.Name,
        DiagnosticsEvents.AnalyzerLagged.Name,
        DiagnosticsEvents.Backpressure.Name,
    };

    public string RuleId => "DIAGNOSTICS_DEGRADED";
    public int Version => 1;
    public bool RequiresCompleteEvidence => false;
    public IReadOnlyCollection<string> WatchedEventCodes => Codes;

    public RuleOutcome? Evaluate(in DiagnosticEvent evt, RuleContext context)
    {
        var isCritical = evt.Descriptor.Name == DiagnosticsEvents.CriticalLost.Name;
        var isStorage = evt.Descriptor.Name == DiagnosticsEvents.StorageFailed.Name;
        var severity = isCritical || isStorage ? DiagnosticLevel.Error : DiagnosticLevel.Warning;

        var incidentId = "DIAGNOSTICS_DEGRADED|" + evt.Descriptor.Code;
        var incident = new Incident(
            incidentId, RuleId, Version, evt.Descriptor.Name, severity, evt.TimestampUtc, evt.TimestampUtc,
            runGeneration: evt.RunGeneration);

        var detail = evt.Payload switch
        {
            DiaEventsDroppedPayload d => $"branch={d.Branch} class={d.DeliveryClass} count={d.Count} reason={d.ReasonCode}",
            DiaCriticalLostPayload c => $"count={c.Count} reason={c.ReasonCode} sticky={c.Sticky}",
            DiaStorageFailedPayload s => $"storage={s.StorageKind} reason={s.ReasonCode}",
            DiaAnalyzerLaggedPayload a => $"pending={a.PendingCount} lagMs={a.LagMs}",
            DiaBackpressurePayload b => $"branch={b.Branch} depth={b.QueueDepth}/{b.Capacity}",
            _ => evt.Descriptor.Name,
        };

        var update = new IncidentUpdate
        {
            AtUtc = evt.TimestampUtc,
            Fact = new IncidentEvidence(evt.Ref, evt.Descriptor.Name, IncidentEvidence.Fact),
            Severity = severity,
            Confidence = ConfidenceBand.ConfirmedObservation,
            ConfidenceRationale = "诊断系统自身量出的计数（不是推断）：lossEpoch=" + context.LossEpoch,
            BreakPoint = "Diagnostics.Collection",
            EvidenceIncomplete = true,     // 自降级即意味着证据不再完整
            LossEpoch = context.LossEpoch == 0 ? null : context.LossEpoch,
            UserFacingSummary = isCritical
                ? "诊断系统丢失了关键事件：本次会话的证据不再完整，缺事件类结论一律降级为「不可判定」。"
                : "诊断系统出现降级（丢事件/存储失败/分析滞后）：结论仍然可用，但「没有观察到」不等于「没有发生」。",
            TechnicalSummary = detail,
            Candidates = new[]
            {
                new IncidentCandidate("event-storm", "事件量超过有界队列预算（允许丢，但已记账）", ConfidenceBand.Medium, Refuted: false),
                new IncidentCandidate("slow-storage", "诊断写入变慢（磁盘繁忙/被占用）", ConfidenceBand.Medium, Refuted: !isStorage),
                new IncidentCandidate("analyzer-behind", "分析器消费落后于生产（UI/规则繁忙）", ConfidenceBand.Medium, Refuted: true),
            },
            SuggestedChecks = new[]
            {
                "查看诊断健康面板的 QueueDepth / Dropped / LossEpoch",
                "如需完整证据，改为更窄范围（单 Job/单 Action）重跑并降低事件量",
            },
        };

        incident.Merge(update);
        return new RuleOutcome(incident, IsNew: true, ShouldTriggerFlight: false);
    }
}

/// <summary>
/// ACCESS_DENIED：直接观测到权限/访问被拒（Win32 5 / UnauthorizedAccess）。
/// 纯正向规则：**只**依据错误域里的明确取值，不把 HResult 低位当 Win32 用。
/// </summary>
public sealed class AccessDeniedRule : IDiagnosticRule
{
    public string RuleId => "ACCESS_DENIED";
    public int Version => 1;
    public bool RequiresCompleteEvidence => false;

    public IReadOnlyCollection<string> WatchedEventCodes { get; } = new[]
    {
        NetEvents.SmbConnectFailed.Name,
        FsEvents.FileReadFailure.Name,
        FsEvents.EnumerateError.Name,
        FsEvents.StatFailure.Name,
        FsEvents.TargetStatFailure.Name,
        RobocopyEvents.ErrorLinesAggregated.Name,
        TransferEvents.SpaceAbortRequested.Name,
        UiEvents.ActionFaulted.Name,
        PersistenceEvents.WriteFailed.Name,
    };

    public RuleOutcome? Evaluate(in DiagnosticEvent evt, RuleContext context)
    {
        if (!IsAccessDenied(in evt)) return null;

        var incidentId = "ACCESS_DENIED|" + evt.Descriptor.Code + "|" + (evt.JobId ?? "-") + "|" + (evt.ObjectId ?? "-");
        var incident = new Incident(
            incidentId, RuleId, Version, "ACCESS_DENIED", DiagnosticLevel.Error, evt.TimestampUtc, evt.TimestampUtc,
            jobId: evt.JobId, objectId: evt.ObjectId, operationId: evt.OperationId, runGeneration: evt.RunGeneration);

        var update = new IncidentUpdate
        {
            AtUtc = evt.TimestampUtc,
            Fact = new IncidentEvidence(evt.Ref, evt.Descriptor.Name, IncidentEvidence.Fact),
            Severity = DiagnosticLevel.Error,
            Confidence = ConfidenceBand.High,
            ConfidenceRationale = "错误域明确：errorDomain=" + evt.ErrorDomain + " win32=" + (evt.Win32Error?.ToString() ?? "-") +
                                  " exception=" + (evt.ExceptionType ?? "-"),
            BreakPoint = evt.Descriptor.Category + "." + evt.Component,
            EvidenceIncomplete = !context.EvidenceComplete,
            LossEpoch = context.LossEpoch == 0 ? null : context.LossEpoch,
            UserFacingSummary = "访问被拒绝：源或目标的权限不足（这不是网络断开，也不是数据丢失）。",
            TechnicalSummary = "code=" + (evt.Win32Error?.ToString() ?? "unknown") +
                               " api=" + (evt.Component ?? "-") + " phase=" + (evt.Phase ?? "-"),
            Candidates = new[]
            {
                new IncidentCandidate("source-share-permission", "源共享/目录不允许当前账号读取", ConfidenceBand.Medium, Refuted: false),
                new IncidentCandidate("target-permission", "目标目录不允许写入（含只读属性/继承被关闭）", ConfidenceBand.Medium, Refuted: false),
                new IncidentCandidate("antivirus-block", "安全软件拦截了访问（在基准速率正常时更可疑）", ConfidenceBand.Low, Refuted: false),
                new IncidentCandidate("credential-not-applied",
                    "凭据未生效（需要显式建立会话）", ConfidenceBand.Low, Refuted: evt.ErrorDomain != ErrorDomain.Win32),
            },
            SuggestedChecks = new[]
            {
                "用同一账号在资源管理器里打开该共享/目录，确认可读写",
                "确认目标目录未被其它程序占用、未设只读",
                "如需凭据，先在 Step 1 建立会话再重试（诊断不会自动重连）",
            },
        };

        incident.Merge(update);
        return new RuleOutcome(incident, IsNew: true, ShouldTriggerFlight: false);
    }

    /// <summary>只有明确的权限错误才算（5=拒绝访问，65=网络访问被拒，1326=凭据无效）。</summary>
    private static bool IsAccessDenied(in DiagnosticEvent evt)
    {
        if (evt.ErrorDomain == ErrorDomain.Win32 && evt.Win32Error is 5 or 65 or 1326) return true;
        if (evt.ExceptionType is "UnauthorizedAccessException" or "SecurityException") return true;
        return false;
    }
}

/// <summary>
/// TARGET_SPACE_EXHAUSTED：目标盘写满（112=磁盘空间不足，39=磁盘已满）。
/// 与"任务被中断"必须区分：这是**可恢复失败**（释放空间 → resume）。
/// </summary>
public sealed class TargetSpaceExhaustedRule : IDiagnosticRule
{
    public string RuleId => "TARGET_SPACE_EXHAUSTED";
    public int Version => 1;
    public bool RequiresCompleteEvidence => false;

    public IReadOnlyCollection<string> WatchedEventCodes { get; } = new[]
    {
        TransferEvents.SpaceAbortRequested.Name,
        RobocopyEvents.ErrorLinesAggregated.Name,
        RobocopyEvents.ProcessExited.Name,
        PersistenceEvents.WriteFailed.Name,
        UiEvents.ActionFaulted.Name,
    };

    public RuleOutcome? Evaluate(in DiagnosticEvent evt, RuleContext context)
    {
        var code = evt.Win32Error;
        var spaceCode = code is 112 or 39;
        // robocopy 退出码 8 只是"有文件失败"，必须配合错误域/错误码才有意义（不猜）。
        if (!spaceCode) return null;

        var incidentId = "TARGET_SPACE_EXHAUSTED|" + (evt.JobId ?? "-");
        var incident = new Incident(
            incidentId, RuleId, Version, "TARGET_SPACE_EXHAUSTED", DiagnosticLevel.Error,
            evt.TimestampUtc, evt.TimestampUtc,
            jobId: evt.JobId, objectId: evt.ObjectId, operationId: evt.OperationId, runGeneration: evt.RunGeneration);

        var update = new IncidentUpdate
        {
            AtUtc = evt.TimestampUtc,
            Fact = new IncidentEvidence(evt.Ref, evt.Descriptor.Name, IncidentEvidence.Fact),
            Severity = DiagnosticLevel.Error,
            Confidence = ConfidenceBand.High,
            ConfidenceRationale = "目标写失败的错误码是空间类（win32=" + code + "），且来自目标侧写入阶段",
            BreakPoint = evt.Descriptor.Category + "." + evt.Component,
            EvidenceIncomplete = !context.EvidenceComplete,
            LossEpoch = context.LossEpoch == 0 ? null : context.LossEpoch,
            UserFacingSummary = "目标磁盘空间不足：本次运行已提前停止（可恢复）。释放空间后点「恢复任务」，已完成对象不会重传。",
            TechnicalSummary = "win32=" + code + " phase=" + (evt.Phase ?? "-") + " outcome=" + (evt.Outcome?.ToString() ?? "-"),
            Candidates = new[]
            {
                new IncidentCandidate("disk-full", "目标盘确实已满", ConfidenceBand.High, Refuted: false),
                new IncidentCandidate("quota-exhausted", "卷配额/用户配额用尽（盘还有空间）", ConfidenceBand.Medium, Refuted: false),
                new IncidentCandidate("fat32-directory-limit",
                    "FAT32 单目录条目上限（错误 82 家族，通常伴随「无法创建」）", ConfidenceBand.Low, Refuted: true),
            },
            SuggestedChecks = new[]
            {
                "确认目标盘剩余空间与卷配额（预检的容量快照可能已过期）",
                "清理目标盘或改用更大容量/更高文件系统（NTFS/exFAT）的盘",
                "释放空间后 resume：已完成对象会跳过，只补差异",
            },
        };

        incident.Merge(update);
        return new RuleOutcome(incident, IsNew: true, ShouldTriggerFlight: true);
    }
}

/// <summary>
/// FILE_LOCKED：共享冲突/锁冲突（Win32 32 = ERROR_SHARING_VIOLATION，33 = ERROR_LOCK_VIOLATION）。
/// 与 ACCESS_DENIED 分开：**权限不足**和**被占用**是两件不同的事，处置方式也不同。
/// </summary>
public sealed class FileLockedRule : IDiagnosticRule
{
    public string RuleId => "FILE_LOCKED";
    public int Version => 1;
    public bool RequiresCompleteEvidence => false;

    public IReadOnlyCollection<string> WatchedEventCodes { get; } = new[]
    {
        RobocopyEvents.ErrorLinesAggregated.Name,
        RobocopyEvents.ProcessExited.Name,
        FsEvents.FileReadFailure.Name,
        FsEvents.StatFailure.Name,
        FsEvents.TargetStatFailure.Name,
        PersistenceEvents.WriteFailed.Name,
        PersistenceEvents.WriteSkipped.Name,
    };

    public RuleOutcome? Evaluate(in DiagnosticEvent evt, RuleContext context)
    {
        var code = evt.Win32Error;
        if (code is not (32 or 33)) return null;

        var incidentId = "FILE_LOCKED|" + (evt.JobId ?? "-") + "|" + (evt.ObjectId ?? "-");
        var incident = new Incident(
            incidentId, RuleId, Version, "FILE_LOCKED", DiagnosticLevel.Warning,
            evt.TimestampUtc, evt.TimestampUtc,
            jobId: evt.JobId, objectId: evt.ObjectId, operationId: evt.OperationId, runGeneration: evt.RunGeneration);

        var update = new IncidentUpdate
        {
            AtUtc = evt.TimestampUtc,
            Fact = new IncidentEvidence(evt.Ref, evt.Descriptor.Name, IncidentEvidence.Fact),
            Severity = DiagnosticLevel.Warning,
            Confidence = ConfidenceBand.High,
            ConfidenceRationale = "错误域明确指向共享/锁冲突（win32=" + code + "）",
            BreakPoint = evt.Descriptor.Category + "." + evt.Component,
            EvidenceIncomplete = !context.EvidenceComplete,
            LossEpoch = context.LossEpoch == 0 ? null : context.LossEpoch,
            UserFacingSummary = "部分文件被其它程序占用（不是权限问题，也不是网络问题）：robocopy 已按 /R /W 重试后跳过。",
            TechnicalSummary = "win32=" + code + " phase=" + (evt.Phase ?? "-") + " stage=" + ((evt.Payload as PstWritePayload)?.Stage ?? "-"),
            Candidates = new[]
            {
                new IncidentCandidate("file-in-use-by-app", "文件正被应用打开（Office/数据库/邮箱文件最常见）", ConfidenceBand.High, Refuted: false),
                new IncidentCandidate("antivirus-scan-hold", "安全软件正在扫描该文件并短暂持有句柄", ConfidenceBand.Medium, Refuted: false),
                new IncidentCandidate("backup-agent-hold", "备份代理正在读取该文件", ConfidenceBand.Low, Refuted: false),
                new IncidentCandidate("permission-denied", "其实是权限问题（错误域应为 5，而不是 32/33）", ConfidenceBand.Low, Refuted: true, RefutationReason: "本次观测到的错误码是共享/锁冲突族"),
            },
            SuggestedChecks = new[]
            {
                "确认该文件是否被应用占用；必要时先关闭占用程序，再点「尝试修复」",
                "被占用文件不影响其余数据：其余对象照常完成",
            },
        };

        incident.Merge(update);
        return new RuleOutcome(incident, IsNew: true, ShouldTriggerFlight: false);
    }
}

/// <summary>
/// PREVIOUS_SESSION_UNCLEAN：上次会话**未确认正常关闭**。
///
/// ★ 这条规则存在的意义就是"拒绝过度断言" ★ 用户强杀、断电、系统关机、诊断写失败都会是这个形态，
/// 因此它**绝不能说"上次崩溃了"**，只能给出候选与下一步检查。
/// </summary>
public sealed class PreviousSessionUncleanRule : IDiagnosticRule
{
    public string RuleId => "PREVIOUS_SESSION_UNCLEAN";
    public int Version => 1;
    public bool RequiresCompleteEvidence => false;

    public IReadOnlyCollection<string> WatchedEventCodes { get; } = new[]
    {
        DiagnosticsEvents.PreviousSessionUnclean.Name,
    };

    public RuleOutcome? Evaluate(in DiagnosticEvent evt, RuleContext context)
    {
        var payload = evt.Payload as DiaPreviousSessionUncleanPayload;
        var reason = payload?.ReasonCode ?? "unknown";

        var incident = new Incident(
            "PREVIOUS_SESSION_UNCLEAN|" + (payload?.PreviousSessionId.ToString("N") ?? "unknown"),
            RuleId, Version, "PREVIOUS_SESSION_UNCLEAN", DiagnosticLevel.Warning,
            evt.TimestampUtc, evt.TimestampUtc, runGeneration: evt.RunGeneration);

        var update = new IncidentUpdate
        {
            AtUtc = evt.TimestampUtc,
            Fact = new IncidentEvidence(evt.Ref, evt.Descriptor.Name, IncidentEvidence.Fact),
            Severity = DiagnosticLevel.Warning,
            Confidence = ConfidenceBand.Medium,
            ConfidenceRationale = "直接观测：上一次会话没有 clean marker（" + reason + "），但**这不足以判断原因**",
            BreakPoint = "Diagnostics.SessionBoundary",
            EvidenceIncomplete = !context.EvidenceComplete,
            LossEpoch = context.LossEpoch == 0 ? null : context.LossEpoch,
            UserFacingSummary = "上一次运行没有确认正常关闭：这不等于崩溃（也可能是强杀/断电/关机/诊断写失败）。",
            TechnicalSummary = "reason=" + reason +
                               " lastSequence=" + (payload?.LastKnownSequence?.ToString() ?? "-") +
                               " tailCompleteLines=" + (payload?.TailCompleteLines.ToString() ?? "-") +
                               " markerWriteFailed=" + (payload?.MarkerWriteFailed.ToString() ?? "-"),
            Candidates = new[]
            {
                new IncidentCandidate("user-terminated", "用户强制结束进程（任务管理器/结束任务）", ConfidenceBand.Medium, Refuted: false),
                new IncidentCandidate("power-loss-or-shutdown", "断电或系统关机", ConfidenceBand.Medium, Refuted: false),
                new IncidentCandidate("process-crash", "进程崩溃（需外部转储/事件日志佐证，本系统不下此结论）", ConfidenceBand.Low, Refuted: false),
                new IncidentCandidate("diagnostics-write-failure", "诊断自己没写成功 clean marker（存储不可用）", ConfidenceBand.Low, Refuted: payload?.MarkerWriteFailed == false),
            },
            SuggestedChecks = new[]
            {
                "若对上次运行的数据完整性有疑问，先执行「验证」再决定是否「尝试修复」",
                "上一次运行已写入的 Receipt 仍然有效：resume 会按回执重建进度",
                "查看 Windows 事件日志/转储可进一步区分崩溃与强杀（本工具不做断言）",
            },
        };

        incident.Merge(update);
        return new RuleOutcome(incident, IsNew: true, ShouldTriggerFlight: false);
    }
}

/// <summary>
/// ROBOCOPY_UNEXPECTED_EXIT：子进程以失败码退出（>=8）。
/// 只陈述"退出码事实"，原因交给其它规则（权限/空间/锁）去挂候选。
/// </summary>
public sealed class RobocopyUnexpectedExitRule : IDiagnosticRule
{
    public string RuleId => "ROBOCOPY_UNEXPECTED_EXIT";
    public int Version => 1;
    public bool RequiresCompleteEvidence => false;

    public IReadOnlyCollection<string> WatchedEventCodes { get; } = new[]
    {
        RobocopyEvents.ProcessExited.Name,
    };

    public RuleOutcome? Evaluate(in DiagnosticEvent evt, RuleContext context)
    {
        var code = evt.RobocopyExitCode;
        if (code is null) return null;

        // 0–7 是成功位掩码；被我们强杀的是 -1/Canceled（不属"非预期退出"）。
        var killed = evt.Outcome == DiagnosticOutcome.Canceled || code < 0;
        if (killed) return null;
        if (code < 8) return null;

        var incidentId = "ROBOCOPY_UNEXPECTED_EXIT|" + (evt.JobId ?? "-") + "|" + (evt.ObjectId ?? "-") + "|" + (evt.Pass ?? "-");
        var incident = new Incident(
            incidentId, RuleId, Version, "ROBOCOPY_UNEXPECTED_EXIT", DiagnosticLevel.Error,
            evt.TimestampUtc, evt.TimestampUtc,
            jobId: evt.JobId, objectId: evt.ObjectId, operationId: evt.OperationId, runGeneration: evt.RunGeneration);

        var update = new IncidentUpdate
        {
            AtUtc = evt.TimestampUtc,
            Fact = new IncidentEvidence(evt.Ref, evt.Descriptor.Name, IncidentEvidence.Fact),
            Severity = DiagnosticLevel.Error,
            Confidence = ConfidenceBand.High,
            ConfidenceRationale = "直接观测：robocopy 以 " + code + " 退出（>=8 表示存在失败位）",
            BreakPoint = "Robocopy.Exit",
            EvidenceIncomplete = !context.EvidenceComplete,
            LossEpoch = context.LossEpoch == 0 ? null : context.LossEpoch,
            UserFacingSummary = "复制进程报告了失败（退出码 " + code + "）：robocopy 已按 /R /W 重试到上限，跳过失败文件继续其余数据。",
            TechnicalSummary = "exitCode=" + code + " pass=" + (evt.Pass ?? "-") + " attempt=" + (evt.Attempt?.ToString() ?? "-") +
                               " durationMs=" + (evt.DurationMs?.ToString() ?? "-"),
            Candidates = new[]
            {
                new IncidentCandidate("files-locked", "部分文件被占用（退出位 8 家族）", ConfidenceBand.Medium, Refuted: false),
                new IncidentCandidate("partial-access-denied", "部分文件权限不足", ConfidenceBand.Medium, Refuted: false),
                new IncidentCandidate("unexpected-kill",
                    "被外部力量终止（任务管理器/关机）—— 需要与 KillRequested 对照才能确认",
                    ConfidenceBand.Low, Refuted: false),
            },
            SuggestedChecks = new[]
            {
                "看同一对象的错误聚合（RBC.ErrorLinesAggregated）里的错误码分布",
                "确认失败文件是否被其它程序占用；必要时先关闭占用程序再「尝试修复」",
            },
        };

        incident.Merge(update);
        return new RuleOutcome(incident, IsNew: true, ShouldTriggerFlight: false);
    }
}