using System;
using System.Linq;
using PCMig.Diagnostics;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using PCMig.Diagnostics.Analysis;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// ★ D6.3 Fixture 16（D-Q2 / D63-L）★ 「证据完整」这句话必须由**真实运行时**在真实故障下自己降级，
/// 而不是只在单测里手工构造一个 DTO 说它会降级。
///
/// 都用真实 <see cref="DiagnosticRuntime"/>：事件走真实管道、规则走真实规则引擎、
/// 结论从 <c>runtime.ActiveIncidents</c> 读出来（那是产品自己产生的卡片）。
/// 三类真实故障：① 消费者吞掉事件（SinkFaults，真实计数器）；② 真实覆盖丢失（viewer 队列溢出）；
/// ③ 存储降级由既有 D63SelfHealth 覆盖，这里不重复。
///
/// 审计 P1-2 的原形：`SinkFaults=1` 却 `EvidenceComplete=true` —— 规则上下文过去只看覆盖、
/// 不看诊断自身健康，于是"证据链自己坏了"也会被说成证据完整。
/// </summary>
public class D63InconclusiveRuntimeTests
{
    private const string AccessDeniedRuleId = "ACCESS_DENIED";

    /// <summary>
    /// ACCESS_DENIED 规则的真实触发条件：它**监听**的事件码之一（`AccessDeniedRule.WatchedEventCodes`
    /// 含 `PSE.Persistence.WriteFailed`，不含 `TRN.JobRunStarted`）+ 明确错误域 win32=5（单条即可）。
    /// </summary>
    private static DiagnosticEventDraft AccessDeniedTrigger(DiagnosticRuntime runtime, string jobId) =>
        new(PersistenceEvents.WriteFailed,
            DiagnosticContext.Root(runtime.SessionId, "DiagnosticRuntime")
                .WithJob(jobId),
            new PstWritePayload("Receipt", "Move", true, "UnauthorizedAccessException"),
            Outcome: DiagnosticOutcome.Failed,
            ErrorDomain: ErrorDomain.Win32,
            Win32Error: 5,
            Message: "access denied (d63 fixture)");

    /// <summary>
    /// 按 <c>JobId + RuleId</c> 取卡片：同一条 `PSE.Persistence.WriteFailed` 也会命中
    /// `PERSISTENCE_WRITE_FAILED` 规则，只按 JobId 取会拿到那张卡（首轮就是这么错的）。
    /// </summary>
    private static Incident? WaitForIncident(DiagnosticRuntime runtime, string jobId, string ruleId)
    {
        Assert.True(D2TestSupport.WaitUntil(() =>
                runtime.ActiveIncidents.Any(i => i.JobId == jobId && i.RuleId == ruleId), 15_000),
            "真实运行时没有产出该规则的事件卡；实际规则=" +
            string.Join(",", runtime.ActiveIncidents.Select(i => i.RuleId).Distinct()));
        return runtime.ActiveIncidents.FirstOrDefault(i => i.JobId == jobId && i.RuleId == ruleId);
    }

    /// <summary>对照：健康时同一触发器产出的卡片**不得**自称证据不完整（不是恒为 true）。</summary>
    [Fact]
    public void Fixture16_ControlIncidentOnAHealthyRuntimeIsNotMarkedIncomplete()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var runtime = DiagnosticRuntime.Start(D2TestSupport.Options(root), out _);
            try
            {
                Assert.True(runtime.GetHealthSnapshot().EvidenceComplete, "健康运行时健康快照应为证据完整");

                runtime.Publisher.TryPublish(AccessDeniedTrigger(runtime, "D63-FIX16-OK"));
                var incident = WaitForIncident(runtime, "D63-FIX16-OK", AccessDeniedRuleId);

                Assert.NotNull(incident);
                Assert.False(incident!.EvidenceIncomplete, "健康运行时不该把证据说成不完整");
                Assert.Null(incident.LossEpoch);
            }
            finally
            {
                D2TestSupport.Shutdown(runtime);
                D2TestSupport.Dispose(runtime);
            }
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    /// <summary>真实 SinkFault（消费者吞掉一条事件）：卡片必须自己降级为证据不完整。</summary>
    [Fact]
    public void Fixture16_ARealSinkFaultMakesTheProducedIncidentReportIncompleteEvidence()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var runtime = DiagnosticRuntime.Start(D2TestSupport.Options(root), out _);
            try
            {
                // 与"某个消费者把事件吞掉"完全同一个计数器（BoundedBranch 的调用点即 IncSinkFault）。
                runtime.Health.IncSinkFault();
                var degraded = runtime.GetHealthSnapshot();
                Assert.False(degraded.EvidenceComplete, "吞掉事件后不得仍自称证据完整");
                Assert.True(degraded.IsDegraded);

                runtime.Publisher.TryPublish(AccessDeniedTrigger(runtime, "D63-FIX16-SINK"));
                var incident = WaitForIncident(runtime, "D63-FIX16-SINK", AccessDeniedRuleId);

                Assert.NotNull(incident);
                Assert.True(incident!.EvidenceIncomplete,
                    "诊断自己吞掉了证据却把结论说成证据完整（审计 P1-2 原形）");
            }
            finally
            {
                D2TestSupport.Shutdown(runtime);
                D2TestSupport.Dispose(runtime);
            }
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    /// <summary>真实覆盖丢失（viewer 收件箱溢出）：卡片必须带上丢失世代并降级。</summary>
    [Fact]
    public void Fixture16_ARealCoverageLossMarksTheProducedIncidentIncompleteWithAnEpoch()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var options = D2TestSupport.Options(root);
            options.ViewerQueueCapacity = 1;
            var runtime = DiagnosticRuntime.Start(options, out _);
            try
            {
                // 制造一次**真实**丢失：viewer 支路容量 1，消费者跟不上就整条丢（并进丢失台账）。
                var attempt = 0;
                while (runtime.GetHealthSnapshot().LossEpoch == 0 && attempt < 40)
                {
                    attempt++;
                    for (var i = 0; i < 200; i++)
                    {
                        runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                            TransferEvents.JobRunStarted,
                            DiagnosticContext.Root(runtime.SessionId, "D63-FIX16-BURST")
                                .WithJob("D63-FIX16-BURST-" + attempt + "-" + i)));
                    }
                    System.Threading.Thread.Sleep(20);
                }

                var health = runtime.GetHealthSnapshot();
                Assert.True(health.LossEpoch > 0,
                    "没能制造出真实丢失（produced=" + health.EventsProduced +
                    " dropped=" + health.EventsDropped + "）");
                Assert.False(health.EvidenceComplete, "有真实丢失时不得自称证据完整");

                runtime.Publisher.TryPublish(AccessDeniedTrigger(runtime, "D63-FIX16-LOSS"));
                var incident = WaitForIncident(runtime, "D63-FIX16-LOSS", AccessDeniedRuleId);

                Assert.NotNull(incident);
                Assert.True(incident!.EvidenceIncomplete, "有真实丢失时卡片必须自降级");
                Assert.True(incident.LossEpoch is > 0,
                    "卡片必须带上丢失世代，实际 " + (incident.LossEpoch?.ToString() ?? "(null)"));
            }
            finally
            {
                D2TestSupport.Shutdown(runtime);
                D2TestSupport.Dispose(runtime);
            }
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }
}