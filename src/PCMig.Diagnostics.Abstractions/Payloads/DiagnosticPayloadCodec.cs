using System.Text.Json;
using PCMig.Diagnostics.Abstractions.Payloads;

namespace PCMig.Diagnostics.Abstractions.Payloads;

/// <summary>payload 读取委托（回放/离线读取路径；写路径见 <see cref="IDiagnosticPayload.WriteJson"/>）。</summary>
public delegate IDiagnosticPayload PayloadReader(JsonElement element);

/// <summary>
/// 强类型 payload 注册表。**不用反射**：每个 payload 显式登记读写两侧，
/// 因此"payload 名 ↔ 类型 ↔ JSON"三者的一致性可以在 D1 就用单元测试锁死。
///
/// 契约：
///   · 每个 EventDescriptor.PayloadName 非 null 的事件，必须在此登记（测试强制）；
///   · 登记但没有任何事件引用的 payload 视为可疑（测试强制）；
///   · 读取失败**绝不抛出**（回放/离线路径必须能被损坏行容忍）。
/// </summary>
public static class DiagnosticPayloadCodec
{
    private static readonly Dictionary<string, PayloadReader> Readers = BuildReaders();

    public static IReadOnlyCollection<string> KnownPayloadNames => Readers.Keys;

    public static bool IsKnown(string payloadName) => Readers.ContainsKey(payloadName);

    public static bool TryRead(string payloadName, JsonElement element, out IDiagnosticPayload? payload)
    {
        payload = null;
        if (!Readers.TryGetValue(payloadName, out var reader)) return false;
        try
        {
            payload = reader(element);
            return true;
        }
        catch (Exception)
        {
            // 宽容：损坏/未知形状 ⇒ 当作"这条 payload 读不出来"，不抛、不伪造字段。
            payload = null;
            return false;
        }
    }

    private static Dictionary<string, PayloadReader> BuildReaders() => new(StringComparer.Ordinal)
    {
        [AppProcessStartedPayload.Name] = ReadAppProcessStarted,
        [AppEnvironmentCapturedPayload.Name] = ReadAppEnvironmentCaptured,
        [AppUnhandledExceptionPayload.Name] = ReadAppUnhandledException,
        [AppClosingPayload.Name] = ReadAppClosing,
        [DiaSessionStartedPayload.Name] = ReadDiaSessionStarted,
        [DiaSessionCleanShutdownPayload.Name] = ReadDiaSessionCleanShutdown,
        [DiaPreviousSessionUncleanPayload.Name] = ReadDiaPreviousSessionUnclean,
        [DiaModeChangedPayload.Name] = ReadDiaModeChanged,
        [DiaBackpressurePayload.Name] = ReadDiaBackpressure,
        [DiaEventsDroppedPayload.Name] = ReadDiaEventsDropped,
        [DiaCriticalLostPayload.Name] = ReadDiaCriticalLost,
        [DiaStorageFailedPayload.Name] = ReadDiaStorageFailed,
        [DiaStorageRecoveredPayload.Name] = ReadDiaStorageRecovered,
        [DiaSerializationFailedPayload.Name] = ReadDiaSerializationFailed,
        [DiaAnalyzerLaggedPayload.Name] = ReadDiaAnalyzerLagged,
        [DiaSnapshotUnavailablePayload.Name] = ReadDiaSnapshotUnavailable,
        [DiaCoverageChangedPayload.Name] = ReadDiaCoverageChanged,
        [DiaShutdownIncompletePayload.Name] = ReadDiaShutdownIncomplete,
        [DiaRingTriggeredPayload.Name] = ReadDiaRingTriggered,
        [DiaRingSealedPayload.Name] = ReadDiaRingSealed,
        [DiaHealthSummaryPayload.Name] = ReadDiaHealthSummary,
        [DiaClockAnchorAdjustedPayload.Name] = ReadDiaClockAnchorAdjusted,
        [DiaIncidentPayload.Name] = ReadDiaIncident,
        [NetProbeStartedPayload.Name] = ReadNetProbeStarted,
        [NetDnsResultPayload.Name] = ReadNetDnsResult,
        [NetTcpProbePayload.Name] = ReadNetTcpProbe,
        [NetSmbSessionPayload.Name] = ReadNetSmbSession,
        [NetShareDiscoveryPayload.Name] = ReadNetShareDiscovery,
        [NetCredentialProofPayload.Name] = ReadNetCredentialProof,
        [PflCheckPayload.Name] = ReadPflCheck,
        [PflSourceProbePayload.Name] = ReadPflSourceProbe,
        [PflTargetVolumePayload.Name] = ReadPflTargetVolume,
        [PflBenchmarkPayload.Name] = ReadPflBenchmark,
        [PflSummaryPayload.Name] = ReadPflSummary,
        [UiActionPayload.Name] = ReadUiAction,
        [UiEligibilityPayload.Name] = ReadUiEligibility,
        [UiFeedbackPayload.Name] = ReadUiFeedback,
        [UiProjectionChangedPayload.Name] = ReadUiProjectionChanged,
        [UiProjectionReadbackPayload.Name] = ReadUiProjectionReadback,
        [UiStateObservedPayload.Name] = ReadUiStateObserved,
        [UiDispatchRejectedPayload.Name] = ReadUiDispatchRejected,
        [UiInputObservedPayload.Name] = ReadUiInputObserved,
        [PstWritePayload.Name] = ReadPstWrite,
        [PstReadFailurePayload.Name] = ReadPstReadFailure,
        [TrnPauseRequestPayload.Name] = ReadTrnPauseRequest,
        [RbcProcessPayload.Name] = ReadRbcProcess,
        [RbcKillPayload.Name] = ReadRbcKill,
        [RbcErrorAggregatePayload.Name] = ReadRbcErrorAggregate,
        [RbcFileSampleSummaryPayload.Name] = ReadRbcFileSampleSummary,
        // ★ D6.1 §16 ★ VRF 族（验证器证据）
        [VrfVerifyStartedPayload.Name] = ReadVrfVerifyStarted,
        [VrfPlanValidatedPayload.Name] = ReadVrfPlanValidated,
        [VrfSideStatPayload.Name] = ReadVrfSideStat,
        [VrfHashSamplePayload.Name] = ReadVrfHashSample,
        [VrfHashResultPayload.Name] = ReadVrfHashResult,
        [VrfMismatchPayload.Name] = ReadVrfMismatch,
        [VrfFailurePayload.Name] = ReadVrfFailure,
        [VrfCompletedPayload.Name] = ReadVrfCompleted,
        [VrfStatsCompletenessPayload.Name] = ReadVrfStatsCompleteness,
        // ★ D6.1 §16 ★ RPR 族（修复证据）
        [RprRepairRequestedPayload.Name] = ReadRprRepairRequested,
        [RprTargetsPayload.Name] = ReadRprTargets,
        [RprPurgePayload.Name] = ReadRprPurge,
        [RprCompletedPayload.Name] = ReadRprCompleted,
        // ★ D6.1 §16 ★ PLN / NET 族
        [FsScanPayload.Name] = ReadFsScan,
        [FsFailPayload.Name] = ReadFsFail,
        [PlnSelectionPayload.Name] = ReadPlnSelection,
        [TrnRetryPayload.Name] = ReadTrnRetry,
        [TrnPauseObservedPayload.Name] = ReadTrnPauseObserved,
        [PlnPlanRequestedPayload.Name] = ReadPlnPlanRequested,
        [PlnPlanCreatedPayload.Name] = ReadPlnPlanCreated,
        [PlnPlanEmptyPayload.Name] = ReadPlnPlanEmpty,
        [TrnObjectPayload.Name] = ReadTrnObject,
        [TrnProgressPayload.Name] = ReadTrnProgress,
    };

    // ────────────────────────── RBC / TRN ──────────────────────────

    private static IDiagnosticPayload ReadRbcProcess(JsonElement e) => new RbcProcessPayload(
        PayloadJson.IntOr(e, "processId", 0),
        PayloadJson.StrOr(e, "processIdentity", "unknown"),
        PayloadJson.StrOr(e, "channel", "unknown"),
        PayloadJson.StrOr(e, "channelMode", "unknown"),
        PayloadJson.StrOr(e, "argumentSummary", string.Empty));

    private static IDiagnosticPayload ReadRbcKill(JsonElement e) => new RbcKillPayload(
        PayloadJson.StrOr(e, "reason", "unknown"),
        PayloadJson.BoolOr(e, "succeeded", false));

    private static IDiagnosticPayload ReadRbcErrorAggregate(JsonElement e) => new RbcErrorAggregatePayload(
        PayloadJson.IntOr(e, "errorLineCount", 0),
        PayloadJson.BoolOr(e, "truncated", false),
        PayloadJson.IntOr(e, "distinctErrorCodes", 0));

    private static IDiagnosticPayload ReadRbcFileSampleSummary(JsonElement e) => new RbcFileSampleSummaryPayload(
        PayloadJson.IntOr(e, "sampled", 0),
        PayloadJson.LongOr(e, "totalObserved", 0),
        PayloadJson.BoolOr(e, "samplingActive", false));

    // ────────────────────────── VRF 族（D6.1 §16）──────────────────────────

    private static IDiagnosticPayload ReadVrfVerifyStarted(JsonElement e) => new VrfVerifyStartedPayload(
        PayloadJson.StrOr(e, "level", "unknown"),
        PayloadJson.IntOr(e, "objectCount", 0),
        PayloadJson.IntOr(e, "sampleHashPercent", 0));

    private static IDiagnosticPayload ReadVrfPlanValidated(JsonElement e) => new VrfPlanValidatedPayload(
        PayloadJson.IntOr(e, "objectCount", 0),
        PayloadJson.BoolOr(e, "allObjectsHavePaths", false),
        PayloadJson.StrOr(e, "planSource", "unknown"));

    private static IDiagnosticPayload ReadVrfSideStat(JsonElement e) => new VrfSideStatPayload(
        PayloadJson.StrOr(e, "role", "unknown"),
        PayloadJson.LongOr(e, "files", 0),
        PayloadJson.LongOr(e, "bytes", 0),
        PayloadJson.BoolOr(e, "overflow", false),
        PayloadJson.LongOr(e, "enumerationErrors", 0),
        PayloadJson.LongOr(e, "elapsedMs", 0));

    private static IDiagnosticPayload ReadVrfHashSample(JsonElement e) => new VrfHashSamplePayload(
        PayloadJson.StrOr(e, "mode", "unknown"),
        PayloadJson.IntOr(e, "candidateFiles", 0),
        PayloadJson.IntOr(e, "sampleCount", 0),
        PayloadJson.IntOr(e, "samplePercent", 0));

    private static IDiagnosticPayload ReadVrfHashResult(JsonElement e) => new VrfHashResultPayload(
        PayloadJson.LongOr(e, "sampled", 0),
        PayloadJson.LongOr(e, "mismatched", 0),
        PayloadJson.LongOr(e, "failed", 0));

    private static IDiagnosticPayload ReadVrfMismatch(JsonElement e) => new VrfMismatchPayload(
        PayloadJson.StrOr(e, "kind", "unknown"),
        PayloadJson.LongOr(e, "count", 0),
        PayloadJson.Str(e, "firstSampleToken"));

    private static IDiagnosticPayload ReadVrfFailure(JsonElement e) => new VrfFailurePayload(
        PayloadJson.StrOr(e, "stage", "unknown"),
        PayloadJson.StrOr(e, "reasonCode", "unknown"),
        PayloadJson.LongOr(e, "count", 0));

    private static IDiagnosticPayload ReadVrfCompleted(JsonElement e) => new VrfCompletedPayload(
        PayloadJson.IntOr(e, "objectCount", 0),
        PayloadJson.BoolOr(e, "overallPass", false),
        PayloadJson.LongOr(e, "hashSampled", 0),
        PayloadJson.LongOr(e, "hashMismatched", 0),
        PayloadJson.LongOr(e, "missingTotal", 0),
        PayloadJson.StrOr(e, "statsCompleteness", "Unknown"));

    private static IDiagnosticPayload ReadVrfStatsCompleteness(JsonElement e) => new VrfStatsCompletenessPayload(
        PayloadJson.StrOr(e, "status", "Unknown"),
        PayloadJson.LongOr(e, "enumerationErrors", 0),
        PayloadJson.BoolOr(e, "anyOverflow", false),
        PayloadJson.StrOr(e, "note", string.Empty));

    // ────────────────────────── RPR 族（D6.1 §16）──────────────────────────

    private static IDiagnosticPayload ReadRprRepairRequested(JsonElement e) => new RprRepairRequestedPayload(
        PayloadJson.BoolOr(e, "forceOverwrite", false),
        PayloadJson.IntOr(e, "requestedObjects", 0));

    private static IDiagnosticPayload ReadRprTargets(JsonElement e) => new RprTargetsPayload(
        PayloadJson.IntOr(e, "targetCount", 0),
        PayloadJson.IntOr(e, "beforeMismatch", 0),
        PayloadJson.IntOr(e, "failedReceiptObjects", 0),
        PayloadJson.IntOr(e, "plannedObjects", 0));

    private static IDiagnosticPayload ReadRprPurge(JsonElement e) => new RprPurgePayload(
        PayloadJson.LongOr(e, "files", 0),
        PayloadJson.LongOr(e, "bytes", 0),
        PayloadJson.LongOr(e, "skippedLarge", 0),
        PayloadJson.LongOr(e, "failed", 0),
        PayloadJson.StrOr(e, "reasonCode", "unknown"));

    private static IDiagnosticPayload ReadRprCompleted(JsonElement e) => new RprCompletedPayload(
        PayloadJson.IntOr(e, "objects", 0),
        PayloadJson.IntOr(e, "okObjects", 0),
        PayloadJson.IntOr(e, "failedObjects", 0),
        PayloadJson.BoolOr(e, "forceOverwrite", false));

    // ────────────────────────── PLN / NET 族（D6.1 §16）──────────────────────────

    private static IDiagnosticPayload ReadTrnRetry(JsonElement e) => new TrnRetryPayload(
        PayloadJson.StrOr(e, "pass", "unknown"),
        PayloadJson.IntOr(e, "attempt", 0),
        PayloadJson.IntOr(e, "maxAttempts", 0),
        PayloadJson.LongOr(e, "backoffMs", 0),
        PayloadJson.Str(e, "errorClass"));

    private static IDiagnosticPayload ReadTrnPauseObserved(JsonElement e) => new TrnPauseObservedPayload(
        PayloadJson.StrOr(e, "mode", "unknown"),
        PayloadJson.Str(e, "objectId"),
        PayloadJson.LongOr(e, "waitedMs", 0));
    private static IDiagnosticPayload ReadFsFail(JsonElement e) => new FsFailPayload(
        PayloadJson.StrOr(e, "stage", "unknown"),
        PayloadJson.LongOr(e, "count", 0),
        PayloadJson.StrOr(e, "reasonCode", "unknown"));

    private static IDiagnosticPayload ReadPlnSelection(JsonElement e) => new PlnSelectionPayload(
        PayloadJson.IntOr(e, "sourceRoots", 0),
        PayloadJson.IntOr(e, "customSelections", 0),
        PayloadJson.IntOr(e, "enabledSources", 0),
        PayloadJson.IntOr(e, "treeSelectedNodes", 0));
    private static IDiagnosticPayload ReadFsScan(JsonElement e) => new FsScanPayload(
        PayloadJson.StrOr(e, "mode", "unknown"),
        PayloadJson.IntOr(e, "objects", 0),
        PayloadJson.LongOr(e, "files", 0),
        PayloadJson.LongOr(e, "bytes", 0),
        PayloadJson.LongOr(e, "inaccessible", 0),
        PayloadJson.LongOr(e, "incompleteObjects", 0),
        PayloadJson.LongOr(e, "lockRiskFiles", 0),
        PayloadJson.LongOr(e, "encryptedFiles", 0),
        PayloadJson.IntOr(e, "excludedDirRules", 0),
        PayloadJson.IntOr(e, "excludedFileRules", 0),
        PayloadJson.IntOr(e, "securityBlockedRules", 0),
        PayloadJson.LongOr(e, "elapsedMs", 0),
        PayloadJson.StrOr(e, "completeness", "Unknown"));
    private static IDiagnosticPayload ReadPlnPlanRequested(JsonElement e) => new PlnPlanRequestedPayload(
        PayloadJson.IntOr(e, "sources", 0),
        PayloadJson.IntOr(e, "observedObjects", 0),
        PayloadJson.BoolOr(e, "splitLargeFiles", false),
        PayloadJson.BoolOr(e, "customSelectionMode", false));

    private static IDiagnosticPayload ReadPlnPlanCreated(JsonElement e) => new PlnPlanCreatedPayload(
        PayloadJson.IntOr(e, "objects", 0),
        PayloadJson.LongOr(e, "totalBytes", 0),
        PayloadJson.LongOr(e, "largestObjectBytes", 0),
        PayloadJson.IntOr(e, "excludedRules", 0),
        PayloadJson.IntOr(e, "objectsWithoutSourceRoot", 0),
        PayloadJson.IntOr(e, "restartablePassObjects", 0));

    private static IDiagnosticPayload ReadPlnPlanEmpty(JsonElement e) => new PlnPlanEmptyPayload(
        PayloadJson.IntOr(e, "observedObjects", 0),
        PayloadJson.IntOr(e, "sources", 0),
        PayloadJson.StrOr(e, "reasonCode", "unknown"));

    private static IDiagnosticPayload ReadTrnObject(JsonElement e) => new TrnObjectPayload(
        PayloadJson.StrOr(e, "kind", "unknown"),
        PayloadJson.LongOr(e, "targetBytes", 0),
        PayloadJson.LongOr(e, "targetFiles", 0),
        PayloadJson.IntOr(e, "exitCodeBulk", -1),
        PayloadJson.IntOr(e, "exitCodeLarge", -1),
        PayloadJson.StrOr(e, "errorClass", "None"),
        PayloadJson.IntOr(e, "attempt", 1));

    private static IDiagnosticPayload ReadTrnProgress(JsonElement e) => new TrnProgressPayload(
        PayloadJson.StrOr(e, "byteSource", "unknown"),
        PayloadJson.LongOr(e, "completedBytes", 0),
        PayloadJson.LongOr(e, "totalBytes", 0),
        PayloadJson.IntOr(e, "filesAttempted", 0),
        PayloadJson.DoubleOr(e, "bytesPerSecond", 0),
        PayloadJson.BoolOr(e, "estimated", false));

    // ────────────────────────── NET / PFL ──────────────────────────

    private static IDiagnosticPayload ReadNetProbeStarted(JsonElement e) => new NetProbeStartedPayload(
        PayloadJson.StrOr(e, "targetAlias", "unknown"),
        PayloadJson.BoolOr(e, "isLiteralAddress", false),
        PayloadJson.IntOr(e, "sourcePathCount", 0));

    private static IDiagnosticPayload ReadNetDnsResult(JsonElement e) => new NetDnsResultPayload(
        PayloadJson.BoolOr(e, "resolved", false),
        PayloadJson.IntOr(e, "addressCount", 0),
        PayloadJson.LongOr(e, "elapsedMs", 0),
        PayloadJson.Str(e, "failureKind"));

    private static IDiagnosticPayload ReadNetTcpProbe(JsonElement e) => new NetTcpProbePayload(
        PayloadJson.IntOr(e, "addressIndex", 0),
        PayloadJson.LongOr(e, "elapsedMs", 0),
        PayloadJson.BoolOr(e, "succeeded", false));

    private static IDiagnosticPayload ReadNetSmbSession(JsonElement e) => new NetSmbSessionPayload(
        PayloadJson.StrOr(e, "api", "unknown"),
        PayloadJson.BoolOr(e, "reused", false),
        PayloadJson.LongOr(e, "elapsedMs", 0),
        PayloadJson.BoolOr(e, "succeeded", false))
    {
        Win32Error = PayloadJson.IntOr(e, "win32Error", 0),
        UsedExplicitCreds = PayloadJson.BoolOr(e, "usedExplicitCreds", false),
        ReasonCode = PayloadJson.Str(e, "reasonCode"),
    };

    private static IDiagnosticPayload ReadNetShareDiscovery(JsonElement e) => new NetShareDiscoveryPayload(
        PayloadJson.BoolOr(e, "enumerationSucceeded", false),
        PayloadJson.IntOr(e, "shareCount", 0),
        PayloadJson.BoolOr(e, "adminSharesProbed", false))
    {
        AdminShareCount = PayloadJson.IntOr(e, "adminShareCount", 0),
        Level = PayloadJson.Str(e, "level"),
    };

    private static IDiagnosticPayload ReadNetCredentialProof(JsonElement e) => new NetCredentialProofPayload(
        PayloadJson.StrOr(e, "proofKind", "unknown"));

    private static IDiagnosticPayload ReadPflCheck(JsonElement e) => new PflCheckPayload(
        PayloadJson.StrOr(e, "checkCode", "unknown"),
        PayloadJson.StrOr(e, "severity", "Info"),
        PayloadJson.BoolOr(e, "pass", false));

    private static IDiagnosticPayload ReadPflSourceProbe(JsonElement e) => new PflSourceProbePayload(
        PayloadJson.IntOr(e, "sourceCount", 0),
        PayloadJson.IntOr(e, "accessibleCount", 0),
        PayloadJson.IntOr(e, "unavailableCount", 0));

    private static IDiagnosticPayload ReadPflTargetVolume(JsonElement e) => new PflTargetVolumePayload(
        PayloadJson.StrOr(e, "volumeKind", "unknown"),
        PayloadJson.LongOr(e, "freeBytesMb", 0),
        PayloadJson.LongOr(e, "totalBytesMb", 0),
        PayloadJson.BoolOr(e, "spaceSufficient", false));

    private static IDiagnosticPayload ReadPflBenchmark(JsonElement e) => new PflBenchmarkPayload(
        PayloadJson.DoubleOr(e, "readMBs", 0),
        PayloadJson.DoubleOr(e, "writeMBs", 0),
        PayloadJson.BoolOr(e, "tooSlow", false));

    private static IDiagnosticPayload ReadPflSummary(JsonElement e) => new PflSummaryPayload(
        PayloadJson.BoolOr(e, "overallPass", false),
        PayloadJson.IntOr(e, "checkCount", 0),
        PayloadJson.IntOr(e, "errorCount", 0),
        PayloadJson.IntOr(e, "warningCount", 0));

    // ────────────────────────── UI / PST / TRN ──────────────────────────

    private static IDiagnosticPayload ReadUiAction(JsonElement e) => new UiActionPayload(
        PayloadJson.StrOr(e, "actionKind", "unknown"),
        PayloadJson.StrOr(e, "source", "unknown"));

    private static IDiagnosticPayload ReadUiEligibility(JsonElement e) => new UiEligibilityPayload(
        PayloadJson.BoolOr(e, "allowed", false),
        PayloadJson.StrOr(e, "reasonCode", "unknown"));

    private static IDiagnosticPayload ReadUiFeedback(JsonElement e) => new UiFeedbackPayload(
        PayloadJson.StrOr(e, "contractId", "unknown"),
        PayloadJson.StrOr(e, "expectation", "unknown"),
        PayloadJson.Long(e, "observedAfterMs"));

    private static IDiagnosticPayload ReadUiProjectionChanged(JsonElement e) => new UiProjectionChangedPayload(
        PayloadJson.StrOr(e, "stateOwnerName", "unknown"),
        PayloadJson.IntOr(e, "changedFieldCount", 0));

    private static IDiagnosticPayload ReadUiProjectionReadback(JsonElement e) => new UiProjectionReadbackPayload(
        PayloadJson.BoolOr(e, "isEnabled", false),
        PayloadJson.Str(e, "visibility"),
        PayloadJson.LongOr(e, "observationVersion", 0),
        PayloadJson.Long(e, "projectionVersion"),
        PayloadJson.LongOr(e, "generation", 0),
        PayloadJson.BoolOr(e, "matchesSource", false));

    private static IDiagnosticPayload ReadUiStateObserved(JsonElement e) => new UiStateObservedPayload(
        PayloadJson.StrOr(e, "stateOwnerName", "unknown"),
        PayloadJson.Str(e, "phase"),
        PayloadJson.BoolOr(e, "isRunning", false),
        PayloadJson.BoolOr(e, "isPaused", false),
        PayloadJson.BoolOr(e, "canStart", false),
        PayloadJson.BoolOr(e, "canPause", false),
        PayloadJson.BoolOr(e, "canStop", false),
        PayloadJson.BoolOr(e, "canResume", false));

    private static IDiagnosticPayload ReadUiDispatchRejected(JsonElement e) => new UiDispatchRejectedPayload(
        PayloadJson.StrOr(e, "reasonCode", "unknown"),
        PayloadJson.StrOr(e, "targetKind", "unknown"));

    private static IDiagnosticPayload ReadUiInputObserved(JsonElement e) => new UiInputObservedPayload(
        PayloadJson.StrOr(e, "inputKind", "unknown"),
        PayloadJson.BoolOr(e, "isEnabled", false),
        PayloadJson.BoolOr(e, "isVisible", false),
        PayloadJson.IntOr(e, "suppressedDuplicates", 0),
        PayloadJson.IntOr(e, "droppedSensitive", 0));

    private static IDiagnosticPayload ReadPstWrite(JsonElement e) => new PstWritePayload(
        PayloadJson.StrOr(e, "artifactKind", "unknown"),
        PayloadJson.StrOr(e, "stage", "unknown"),
        PayloadJson.BoolOr(e, "destinationExisted", false),
        PayloadJson.Str(e, "reasonCode"));

    private static IDiagnosticPayload ReadPstReadFailure(JsonElement e) => new PstReadFailurePayload(
        PayloadJson.StrOr(e, "artifactKind", "unknown"),
        PayloadJson.StrOr(e, "reasonCode", "unknown"));

    private static IDiagnosticPayload ReadTrnPauseRequest(JsonElement e) => new TrnPauseRequestPayload(
        PayloadJson.BoolOr(e, "immediate", false),
        PayloadJson.BoolOr(e, "succeeded", false),
        PayloadJson.Str(e, "reasonCode"));

    // ────────────────────────── APP ──────────────────────────

    private static IDiagnosticPayload ReadAppProcessStarted(JsonElement e) => new AppProcessStartedPayload(
        PayloadJson.StrOr(e, "appVersion", "unknown"),
        PayloadJson.StrOr(e, "runtimeVersion", "unknown"),
        PayloadJson.StrOr(e, "osVersion", "unknown"),
        PayloadJson.IntOr(e, "processId", 0),
        PayloadJson.Time(e, "processStartUtc") ?? default,
        PayloadJson.StrOr(e, "architecture", "unknown"),
        PayloadJson.Str(e, "buildId"));

    private static IDiagnosticPayload ReadAppEnvironmentCaptured(JsonElement e) => new AppEnvironmentCapturedPayload(
        PayloadJson.StrOr(e, "osVersion", "unknown"),
        PayloadJson.StrOr(e, "architecture", "unknown"),
        PayloadJson.IntOr(e, "processorCount", 0),
        PayloadJson.LongOr(e, "totalPhysicalMemoryMb", 0),
        PayloadJson.StrOr(e, "machineAlias", "unknown"),
        PayloadJson.Time(e, "capturedUtc") ?? default);

    private static IDiagnosticPayload ReadAppUnhandledException(JsonElement e) => new AppUnhandledExceptionPayload(
        PayloadJson.StrOr(e, "exceptionType", "unknown"),
        PayloadJson.Int(e, "hresult"),
        PayloadJson.Str(e, "phase"),
        PayloadJson.BoolOr(e, "handledByApp", false),
        PayloadJson.StrOr(e, "stackFrameSummary", string.Empty),
        PayloadJson.BoolOr(e, "stackTruncated", false));

    private static IDiagnosticPayload ReadAppClosing(JsonElement e) => new AppClosingPayload(
        PayloadJson.StrOr(e, "reasonCode", "unknown"),
        PayloadJson.BoolOr(e, "uiRefreshStopped", false),
        PayloadJson.BoolOr(e, "diagnosticsDrainRequested", false));

    // ────────────────────────── DIA ──────────────────────────

    private static IDiagnosticPayload ReadDiaSessionStarted(JsonElement e) => new DiaSessionStartedPayload(
        PayloadJson.StrOr(e, "appVersion", "unknown"),
        PayloadJson.StrOr(e, "runtimeVersion", "unknown"),
        PayloadJson.StrOr(e, "osVersion", "unknown"),
        PayloadJson.IntOr(e, "processId", 0),
        PayloadJson.Time(e, "processStartUtc") ?? default,
        PayloadJson.StrOr(e, "processIdentity", "unknown"),
        PayloadJson.EnumOr(e, "initialMode", CaptureMode.Off),
        PayloadJson.StrOr(e, "catalogVersion", "unknown"),
        PayloadJson.StrOr(e, "catalogHash", "unknown"),
        PayloadJson.LongOr(e, "monotonicFrequency", 0),
        PayloadJson.StrOr(e, "architecture", "unknown"),
        PayloadJson.StrOr(e, "storageRootToken", "unknown"));

    private static IDiagnosticPayload ReadDiaSessionCleanShutdown(JsonElement e) => new DiaSessionCleanShutdownPayload(
        PayloadJson.LongOr(e, "drainedEvents", 0),
        PayloadJson.BoolOr(e, "flushAcknowledged", false),
        PayloadJson.IntOr(e, "segmentsSealed", 0),
        PayloadJson.BoolOr(e, "cleanMarkerWritten", false));

    private static IDiagnosticPayload ReadDiaPreviousSessionUnclean(JsonElement e) => new DiaPreviousSessionUncleanPayload(
        PayloadJson.GuidOrEmpty(e, "previousSessionId"),
        PayloadJson.StrOr(e, "reasonCode", "unknown"),
        PayloadJson.Long(e, "lastKnownSequence"),
        PayloadJson.IntOr(e, "tailCompleteLines", 0),
        PayloadJson.BoolOr(e, "markerWriteFailed", false));

    private static IDiagnosticPayload ReadDiaModeChanged(JsonElement e) => new DiaModeChangedPayload(
        PayloadJson.EnumOr(e, "from", CaptureMode.Off),
        PayloadJson.EnumOr(e, "to", CaptureMode.Off),
        PayloadJson.StrOr(e, "reasonCode", "unknown"));

    private static IDiagnosticPayload ReadDiaBackpressure(JsonElement e) => new DiaBackpressurePayload(
        PayloadJson.StrOr(e, "branch", "unknown"),
        PayloadJson.EnumOr(e, "deliveryClass", DeliveryClass.Operational),
        PayloadJson.IntOr(e, "queueDepth", 0),
        PayloadJson.IntOr(e, "capacity", 0),
        PayloadJson.LongOr(e, "droppedTotal", 0));

    private static IDiagnosticPayload ReadDiaEventsDropped(JsonElement e) => new DiaEventsDroppedPayload(
        PayloadJson.StrOr(e, "branch", "unknown"),
        PayloadJson.EnumOr(e, "deliveryClass", DeliveryClass.Operational),
        PayloadJson.LongOr(e, "count", 0),
        PayloadJson.Long(e, "firstSequence"),
        PayloadJson.Long(e, "lastSequence"),
        PayloadJson.StrOr(e, "reasonCode", "unknown"));

    private static IDiagnosticPayload ReadDiaCriticalLost(JsonElement e) => new DiaCriticalLostPayload(
        PayloadJson.LongOr(e, "count", 0),
        PayloadJson.StrOr(e, "reasonCode", "unknown"),
        PayloadJson.BoolOr(e, "sticky", false));

    private static IDiagnosticPayload ReadDiaStorageFailed(JsonElement e) => new DiaStorageFailedPayload(
        PayloadJson.StrOr(e, "storageKind", "unknown"),
        PayloadJson.StrOr(e, "reasonCode", "unknown"),
        PayloadJson.Int(e, "win32Error"),
        PayloadJson.BoolOr(e, "emergencyFallbackActive", false));

    private static IDiagnosticPayload ReadDiaStorageRecovered(JsonElement e) => new DiaStorageRecoveredPayload(
        PayloadJson.StrOr(e, "storageKind", "unknown"),
        PayloadJson.LongOr(e, "outageMs", 0));

    private static IDiagnosticPayload ReadDiaSerializationFailed(JsonElement e) => new DiaSerializationFailedPayload(
        PayloadJson.StrOr(e, "eventCode", "unknown"),
        PayloadJson.StrOr(e, "reasonCode", "unknown"),
        PayloadJson.Str(e, "failedPayloadName"));

    private static IDiagnosticPayload ReadDiaAnalyzerLagged(JsonElement e) => new DiaAnalyzerLaggedPayload(
        PayloadJson.IntOr(e, "pendingCount", 0),
        PayloadJson.LongOr(e, "lagMs", 0));

    private static IDiagnosticPayload ReadDiaSnapshotUnavailable(JsonElement e) => new DiaSnapshotUnavailablePayload(
        PayloadJson.StrOr(e, "snapshotKind", "unknown"),
        PayloadJson.StrOr(e, "reasonCode", "unknown"),
        PayloadJson.IntOr(e, "timeoutMs", 0));

    private static IDiagnosticPayload ReadDiaCoverageChanged(JsonElement e) => new DiaCoverageChangedPayload(
        PayloadJson.EnumOr(e, "mode", CaptureMode.Off),
        PayloadJson.StrOr(e, "reasonCode", "unknown"),
        PayloadJson.Time(e, "coverageStartUtc"),
        PayloadJson.Time(e, "coverageEndUtc"));

    private static IDiagnosticPayload ReadDiaShutdownIncomplete(JsonElement e) => new DiaShutdownIncompletePayload(
        PayloadJson.IntOr(e, "pendingBranches", 0),
        PayloadJson.LongOr(e, "drainedEvents", 0),
        PayloadJson.LongOr(e, "pendingEvents", 0),
        PayloadJson.IntOr(e, "budgetMs", 0));

    private static IDiagnosticPayload ReadDiaRingTriggered(JsonElement e) => new DiaRingTriggeredPayload(
        PayloadJson.StrOr(e, "triggerId", "unknown"),
        PayloadJson.StrOr(e, "triggerCode", "unknown"),
        PayloadJson.IntOr(e, "requestedPreWindowMs", 0),
        PayloadJson.IntOr(e, "maxPostWindowMs", 0));

    private static IDiagnosticPayload ReadDiaRingSealed(JsonElement e) => new DiaRingSealedPayload(
        PayloadJson.StrOr(e, "triggerId", "unknown"),
        PayloadJson.IntOr(e, "actualCoverageMs", 0),
        PayloadJson.LongOr(e, "overwrittenEvents", 0),
        PayloadJson.LongOr(e, "droppedEvents", 0),
        PayloadJson.BoolOr(e, "postWindowComplete", false),
        PayloadJson.IntOr(e, "checkpointCount", 0));

    private static IDiagnosticPayload ReadDiaHealthSummary(JsonElement e) => new DiaHealthSummaryPayload(
        PayloadJson.LongOr(e, "produced", 0),
        PayloadJson.LongOr(e, "accepted", 0),
        PayloadJson.LongOr(e, "written", 0),
        PayloadJson.LongOr(e, "dropped", 0),
        PayloadJson.LongOr(e, "evicted", 0),
        PayloadJson.LongOr(e, "criticalLost", 0),
        PayloadJson.BoolOr(e, "storageDegraded", false),
        PayloadJson.LongOr(e, "lossEpoch", 0));

    private static IDiagnosticPayload ReadDiaClockAnchorAdjusted(JsonElement e) => new DiaClockAnchorAdjustedPayload(
        PayloadJson.LongOr(e, "jumpCount", 0),
        PayloadJson.StrOr(e, "direction", "unknown"),
        PayloadJson.LongOr(e, "deltaMs", 0));

    private static IDiagnosticPayload ReadDiaIncident(JsonElement e) => new DiaIncidentPayload(
        PayloadJson.StrOr(e, "incidentId", "unknown"),
        PayloadJson.StrOr(e, "ruleId", "unknown"),
        PayloadJson.IntOr(e, "ruleVersion", 0),
        PayloadJson.StrOr(e, "status", "Open"),
        PayloadJson.StrOr(e, "severity", "Warning"),
        PayloadJson.StrOr(e, "symptomCode", "unknown"),
        PayloadJson.StrOr(e, "confidence", "Unknown"),
        PayloadJson.BoolOr(e, "evidenceIncomplete", true),
        PayloadJson.IntOr(e, "revision", 0),
        PayloadJson.Str(e, "breakPoint"));
}