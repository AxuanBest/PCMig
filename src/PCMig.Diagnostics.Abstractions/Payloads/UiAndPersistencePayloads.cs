using System.Text.Json;

namespace PCMig.Diagnostics.Abstractions.Payloads;

// ────────────────────────── UI：动作链 + 反馈契约 + 投影观测 ──────────────────────────

/// <summary>
/// UI 语义动作的动作类型与来源。ActionKind 是稳定 token（不是显示文字），
/// 例如 Connect / Prepare / Start / Pause / Resume / Stop / Verify / Repair / Export / Navigate。
/// </summary>
public sealed record UiActionPayload(string ActionKind, string Source) : IDiagnosticPayload
{
    public const string Name = "UiAction";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteString("actionKind", ActionKind);
        w.WriteString("source", Source);
    }
}

/// <summary>原有 guard/CanExecute 判断的结果（只观察已执行的结果，不为诊断多调用一次判据）。</summary>
public sealed record UiEligibilityPayload(bool Allowed, string ReasonCode) : IDiagnosticPayload
{
    public const string Name = "UiEligibility";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteBoolean("allowed", Allowed);
        w.WriteString("reasonCode", ReasonCode);
    }
}

/// <summary>反馈期望/确认（契约 ID + 期望类别 + 观察延迟）。</summary>
public sealed record UiFeedbackPayload(string ContractId, string Expectation, long? ObservedAfterMs) : IDiagnosticPayload
{
    public const string Name = "UiFeedback";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteString("contractId", ContractId);
        w.WriteString("expectation", Expectation);
        PayloadJson.WriteNumberOrNull(w, "observedAfterMs", ObservedAfterMs);
    }
}

/// <summary>VM 状态被写入（投影来源发生变化）。</summary>
public sealed record UiProjectionChangedPayload(string StateOwnerName, int ChangedFieldCount) : IDiagnosticPayload
{
    public const string Name = "UiProjectionChanged";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteString("stateOwnerName", StateOwnerName);
        w.WriteNumber("changedFieldCount", ChangedFieldCount);
    }
}

/// <summary>
/// L2 控件投影回读：在真实 PushFooter/PushState 之后读取关键控件值。
/// **同写者自ack 只能证明"赋值/读回"，不证明屏幕像素**（那属于 L3 显式视觉验证）。
/// </summary>
public sealed record UiProjectionReadbackPayload(
    bool IsEnabled,
    string? Visibility,
    long ObservationVersion,
    long? ProjectionVersion,
    long Generation,
    bool MatchesSource) : IDiagnosticPayload
{
    public const string Name = "UiProjectionReadback";
    public string PayloadName => Name;

    /// <summary>★ D6.1 §13 ★ 页面是否**真的挂载**（未挂载 ⇒ 结论是"未观察到"，不是 mismatch）。</summary>
    public bool PageMounted { get; init; } = true;

    /// <summary>是否真的读到了控件状态（false ⇒ 本次没有可用读数）。</summary>
    public bool Observed { get; init; } = true;

    /// <summary>投影承诺的期望值（来自 VM/契约），与控件实际值比较才有 mismatch 语义。</summary>
    public bool? ExpectedEnabled { get; init; }

    public string? ExpectedVisibility { get; init; }

    /// <summary>稳定原因 token（match / page-unavailable / enabled-mismatch / visibility-mismatch）。</summary>
    public string? ReasonCode { get; init; }

    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteBoolean("isEnabled", IsEnabled);
        PayloadJson.WriteStringOrNull(w, "visibility", Visibility);
        w.WriteNumber("observationVersion", ObservationVersion);
        PayloadJson.WriteNumberOrNull(w, "projectionVersion", ProjectionVersion);
        w.WriteNumber("generation", Generation);
        w.WriteBoolean("matchesSource", MatchesSource);
        w.WriteBoolean("pageMounted", PageMounted);
        w.WriteBoolean("observed", Observed);
        PayloadJson.WriteBoolOrNull(w, "expectedEnabled", ExpectedEnabled);
        PayloadJson.WriteStringOrNull(w, "expectedVisibility", ExpectedVisibility);
        PayloadJson.WriteStringOrNull(w, "reasonCode", ReasonCode);
    }
}

/// <summary>某个状态归属（Core/VM/View/StoredState）的快照观测——用于区分"三层是否一致"。</summary>
public sealed record UiStateObservedPayload(
    string StateOwnerName,
    string? Phase,
    bool IsRunning,
    bool IsPaused,
    bool CanStart,
    bool CanPause,
    bool CanStop,
    bool CanResume) : IDiagnosticPayload
{
    public const string Name = "UiStateObserved";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteString("stateOwnerName", StateOwnerName);
        PayloadJson.WriteStringOrNull(w, "phase", Phase);
        w.WriteBoolean("isRunning", IsRunning);
        w.WriteBoolean("isPaused", IsPaused);
        w.WriteBoolean("canStart", CanStart);
        w.WriteBoolean("canPause", CanPause);
        w.WriteBoolean("canStop", CanStop);
        w.WriteBoolean("canResume", CanResume);
    }
}

/// <summary>DispatcherQueue.TryEnqueue 被拒绝（关窗期正常，其他时候是风险）。</summary>
public sealed record UiDispatchRejectedPayload(string ReasonCode, string TargetKind) : IDiagnosticPayload
{
    public const string Name = "UiDispatchRejected";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteString("reasonCode", ReasonCode);
        w.WriteString("targetKind", TargetKind);
    }
}

/// <summary>
/// UI.InputObserved：Deep Trace 下的**有限**输入观测（方案 §9）。
///
/// ★ 只记"哪一类输入落在哪个稳定控件上、它当时是否可用/可见" ★
///   · **不记**文本/按键内容、IME、剪贴板、坐标、鼠标轨迹；
///   · **不写** Handled / Focus / Capture，也不改任何路由行为（纯观察）；
///   · 只在 Deep 打开且**限时**窗口内产生，且对同一控件的重复输入做去重计数。
/// </summary>
public sealed record UiInputObservedPayload(
    string InputKind,
    bool IsEnabled,
    bool IsVisible,
    int SuppressedDuplicates,
    int DroppedSensitive = 0) : IDiagnosticPayload
{
    public const string Name = "UiInputObserved";
    public string PayloadName => Name;

    /// <summary>DroppedSensitive：因命中敏感来源（PasswordBox／密码类 ControlId）而**整体丢弃**的输入条数。
    /// 只记**条数**，绝不记被丢弃的来源身份或按键身份。</summary>
    public void WriteJson(Utf8JsonWriter w)
    {
        // InputKind 必须是 InputCategories 里的固定类别 token（例如 key-tab / key-directional），
        // **永不**是 "key:A" 这种带按键身份的字符串（D6.1 §1）。
        w.WriteString("inputKind", InputKind);
        w.WriteBoolean("isEnabled", IsEnabled);
        w.WriteBoolean("isVisible", IsVisible);
        w.WriteNumber("suppressedDuplicates", SuppressedDuplicates);
        w.WriteNumber("droppedSensitive", DroppedSensitive);
    }
}

// ────────────────────────── PST：存档/回执的真实写入阶段 ──────────────────────────

/// <summary>
/// 写入阶段事实。ArtifactKind 是稳定 token（JobState / Plan / Receipt / VerifyReport / PauseRequest），
/// Stage 是 WriteAtomic 内部真实阶段（Serialize / TempWrite / Move / Skipped / Failed）。
/// ★ "SaveReceipt() 返回了" ≠ "回执已持久化" —— 这个载荷就是用来分开这两件事的。
/// </summary>
public sealed record PstWritePayload(string ArtifactKind, string Stage, bool DestinationExisted, string? ReasonCode) : IDiagnosticPayload
{
    public const string Name = "PstWrite";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteString("artifactKind", ArtifactKind);
        w.WriteString("stage", Stage);
        w.WriteBoolean("destinationExisted", DestinationExisted);
        PayloadJson.WriteStringOrNull(w, "reasonCode", ReasonCode);
    }
}

/// <summary>读取失败（TryRead 的失败原因目前被吞掉，这里把它变成可观察事实）。</summary>
public sealed record PstReadFailurePayload(string ArtifactKind, string ReasonCode) : IDiagnosticPayload
{
    public const string Name = "PstReadFailure";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteString("artifactKind", ArtifactKind);
        w.WriteString("reasonCode", ReasonCode);
    }
}

// ────────────────────────── TRN：暂停/恢复请求文件（只观察，不代改业务） ──────────────────────────

/// <summary>暂停请求写入/清除的结果（"点了暂停没反应"的第一手事实）。</summary>
public sealed record TrnPauseRequestPayload(bool Immediate, bool Succeeded, string? ReasonCode) : IDiagnosticPayload
{
    public const string Name = "TrnPauseRequest";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteBoolean("immediate", Immediate);
        w.WriteBoolean("succeeded", Succeeded);
        PayloadJson.WriteStringOrNull(w, "reasonCode", ReasonCode);
    }
}