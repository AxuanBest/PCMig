using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using PCMig.Diagnostics;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using PCMig.Diagnostics.Analysis;
using PCMig.Diagnostics.Analysis.Feedback;
using PCMig.Diagnostics.Export;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// ★ R-2 收口（第二轮：两处**无计数**静默吞异常）★
///
/// 上一轮（R-2）把"规则引擎内部故障"接进了健康通道，但同一本账里还留着两处**空 catch**：
///
///   站 A <c>Analysis/Feedback/PendingExpectationTracker.cs</c>：<c>Observe</c> 吞掉跟踪器内部异常。
///        事件被 analyzer 消费了却**没有被解释** ⇒ 该开的期望与该关的期望一起消失，
///        而 SinkFaults / MaintenanceFaults / RuleFaults 全为 0 ⇒ 会话仍宣称 Healthy + EvidenceComplete。
///   站 B <c>DiagnosticRuntime.cs</c>：<c>HandleExpectationTimeout</c> 吞掉"超时结论生成"内部异常。
///        那条"没反应"的证据根本没被写成事件卡；而且内层 catch **抢在维护隔离之前**吞掉异常，
///        连 <c>MaintenanceFaults</c> 都拿不到这条事实。
///
/// 修复口径（用户口径）：**异常可以被容错，但不能无痕** —— 两处都进已有可审计渠道
/// <see cref="DiagnosticHealthSnapshot.RuleFaults"/>（原因串标明确切位置），
/// 于是它们同时让 <see cref="DiagnosticHealthSnapshot.IsDegraded"/> 为真、
/// 让 <see cref="DiagnosticHealthSnapshot.EvidenceComplete"/> 为假。
///
/// 本文件把这三件事变成规格：① 跟踪器故障必须回声；② 运行时两条线真的接上健康通道；
/// ③ 故障之后导出包不许写 Complete / packageEvidenceComplete=true。
/// </summary>
public sealed class D63SilentFaultClosureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-d63-silent-" + Guid.NewGuid().ToString("N")[..8]);

    public D63SilentFaultClosureTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* 句柄延迟释放 */ }
    }

    // ───────────────────────── 事件构造（只用真实形态） ─────────────────────────

    private static DiagnosticEvent ActionObserved(Guid actionId, string actionKind, long sequence)
        => new()
        {
            SchemaVersion = DiagnosticEvent.CurrentSchemaVersion,
            Descriptor = UiEvents.UserActionObserved,
            SessionId = TestEvents.FixedSessionId,
            Sequence = sequence,
            TimestampUtc = new DateTimeOffset(2026, 10, 2, 11, 30, 0, TimeSpan.Zero).AddSeconds(sequence),
            MonotonicTimestamp = sequence,
            Level = DiagnosticLevel.Information,
            Delivery = DeliveryClass.Operational,
            EvidenceQuality = EvidenceQuality.Direct,
            CaptureMode = CaptureMode.Operational,
            ActionId = actionId,
            ControlId = "Step2.Prepare",
            Payload = new UiActionPayload(actionKind, "click"),
        };

    private static DiagnosticEvent SameActionEvent(Guid actionId, EventDescriptor descriptor, long sequence)
        => new()
        {
            SchemaVersion = DiagnosticEvent.CurrentSchemaVersion,
            Descriptor = descriptor,
            SessionId = TestEvents.FixedSessionId,
            Sequence = sequence,
            TimestampUtc = new DateTimeOffset(2026, 10, 2, 11, 30, 0, TimeSpan.Zero).AddSeconds(sequence),
            MonotonicTimestamp = sequence,
            Level = descriptor.Level,
            Delivery = descriptor.Delivery,
            EvidenceQuality = EvidenceQuality.Direct,
            CaptureMode = CaptureMode.Operational,
            ActionId = actionId,
        };

    // ─────────── 站 A：跟踪器内部故障必须回声（修复前「faults 为空」⇒ 红灯） ───────────

    [Fact]
    public void R6_TrackerInternalFaultMustEchoInsteadOfVanishing()
    {
        // 退化契约：TerminalEventNames 故意为 null（Steps 保持合法，否则构造函数就炸）
        // ⇒ 让真实代码路径**必然**抛。这是唯一需要的"故障注入"：不打桩生产代码，
        //   只喂它一条自相矛盾的契约。
        var contracts = new FeedbackContractRegistry(new[]
        {
            new FeedbackContract("r6-degenerate.v1", "R6Degenerate", 1, null!,
                new[] { new ExpectedStep("r6.immediate", ExpectationKind.Immediate,
                    new[] { UiEvents.CommandStarted.Name }, "动作进入处理路径") },
                100, 200, 300),
        });
        var tracker = new PendingExpectationTracker(contracts, _ => { });
        var faults = new List<string>();
        tracker.OnFault = faults.Add;

        var actionId = Guid.NewGuid();

        // ① 开一条期望（这条路径不碰 Steps/TerminalEventNames ⇒ 不抛）。
        tracker.Observe(ActionObserved(actionId, "R6Degenerate", 1));
        Assert.Equal(1, tracker.PendingCount);
        Assert.Empty(faults);

        // ② 同一 actionId 的非终止事件 ⇒ 必然走到 TerminalEventNames.Contains ⇒ 抛 ⇒ 必须留痕。
        tracker.Observe(SameActionEvent(actionId, UiEvents.CommandStarted, 2));
        Assert.Single(faults);
        Assert.StartsWith("tracker-observe:", faults[0], StringComparison.Ordinal);

        // ③ 容错口径不变：跟踪器没有死，下一条事件照常被处理（计数本身就是"发生过几次"的事实）。
        tracker.Observe(SameActionEvent(actionId, UiEvents.CommandStarted, 3));
        Assert.Equal(2, faults.Count);

        // 正对照：换一条**完好**契约时，同样的事件序列不得产生任何故障回声。
        var healthy = new PendingExpectationTracker(
            new FeedbackContractRegistry(new[]
            {
                new FeedbackContract("r6-healthy.v1", "R6Healthy", 1,
                    new[] { UiEvents.ActionCompleted.Name },
                    new[] { new ExpectedStep("r6.immediate", ExpectationKind.Immediate,
                        new[] { UiEvents.CommandStarted.Name }, "动作进入处理路径") },
                    100, 200, 300),
            }),
            _ => { });
        var healthyFaults = new List<string>();
        healthy.OnFault = healthyFaults.Add;
        healthy.Observe(ActionObserved(actionId, "R6Healthy", 1));
        healthy.Observe(SameActionEvent(actionId, UiEvents.CommandStarted, 2));
        Assert.Empty(healthyFaults);
    }

    // ─────────── 站 A/B：运行时两条线必须真的接到健康通道，并禁止 Complete ───────────

    [Fact]
    public void R7_RuntimeMustWireBothSilentPathsIntoHealthAndForbidCompleteEvidence()
    {
        var runtime = DiagnosticRuntime.Start(new DiagnosticRuntimeOptions
        {
            StorageRoot = Path.Combine(_root, "silent", "Diagnostics"),
            InitialMode = CaptureMode.Operational,
            AppVersion = "d63-test",
            ShutdownBudgetMs = 5000,
            WriterFlushIntervalMs = 1,
        }, out _);
        Assert.NotNull(runtime.Store);

        try
        {
            // 正对照：故障之前必须"健康 + 证据完整"。
            var clean = runtime.GetHealthSnapshot();
            Assert.False(clean.IsDegraded, "干净会话不得谎报降级");
            Assert.True(clean.EvidenceComplete, "干净会话不得谎报证据不完整");
            Assert.Equal(0, clean.RuleFaults);

            // ① 跟踪器故障通道必须接到诊断自身健康（未接线时是 null ⇒ 红灯）。
            var trackerField = typeof(DiagnosticRuntime)
                .GetField("_expectations", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(trackerField);
            var tracker = (PendingExpectationTracker)trackerField!.GetValue(runtime)!;
            var trackerHook = tracker.OnFault;
            Assert.True(trackerHook is not null,
                "PendingExpectationTracker.OnFault 没有接到诊断自身健康：跟踪器内部故障仍会无痕");
            trackerHook!("tracker-observe:NullReferenceException");

            var afterTrackerFault = runtime.GetHealthSnapshot();
            Assert.Equal(1, afterTrackerFault.RuleFaults);
            Assert.Equal("tracker-observe:NullReferenceException", afterTrackerFault.LastRuleFaultReason);
            Assert.True(afterTrackerFault.IsDegraded, "跟踪器内部故障必须让健康降级");
            Assert.False(afterTrackerFault.EvidenceComplete, "事件被消费却没被解释 ⇒ 不许宣称证据完整");

            // ② 超时结论生成失败（站 B 的 catch）同样不许静默。
            //    注入方式：直接给生产方法一条**自相矛盾**的超时（Step=null）⇒ Handles() 必然抛 ⇒ 走 catch。
            var handler = typeof(DiagnosticRuntime)
                .GetMethod("HandleExpectationTimeout", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(handler);
            handler!.Invoke(runtime, new object[] { new ExpectationTimeout(null!, null!, 0, 0, true, 0) });

            var afterTimeoutFault = runtime.GetHealthSnapshot();
            Assert.Equal(2, afterTimeoutFault.RuleFaults);
            Assert.StartsWith("expectation-timeout:", afterTimeoutFault.LastRuleFaultReason, StringComparison.Ordinal);
            Assert.True(afterTimeoutFault.IsDegraded, "超时结论生成失败必须让健康降级");
            Assert.False(afterTimeoutFault.EvidenceComplete, "那条「没反应」的证据没写下来 ⇒ 不许宣称证据完整");

            // ③ 同一本账还要走到包里：不许 Complete，也不许 packageEvidenceComplete=true。
            var outcome = new DiagnosticPackageExporter().Export(new DiagnosticExportRequest
            {
                SessionDir = runtime.Store!.SessionDir,
                OutputDirectory = Path.Combine(_root, "silent", "out"),
                Health = afterTimeoutFault,
                Cutoff = runtime.PrepareExportCutoff(TimeSpan.FromSeconds(3)),
                IncludeFlightWindows = false,
            });
            Assert.True(outcome.Succeeded, outcome.FailureReason);

            using var zip = System.IO.Compression.ZipFile.OpenRead(outcome.ZipPath!);
            var entry = zip.GetEntry("summary.json");
            Assert.NotNull(entry);
            using var reader = new StreamReader(entry!.Open(), System.Text.Encoding.UTF8);
            var summary = System.Text.Json.JsonDocument.Parse(reader.ReadToEnd()).RootElement.Clone();
            Assert.NotEqual("Complete", summary.GetProperty("evidenceCompleteness").GetString());
            Assert.False(summary.GetProperty("packageEvidenceComplete").GetBoolean());
            Assert.True(summary.GetProperty("collectionHealth").GetProperty("degraded").GetBoolean(),
                "分析链内部故障必须出现在包的 collectionHealth.degraded 里");
        }
        finally
        {
            D2TestSupport.Shutdown(runtime);
        }
    }
}
