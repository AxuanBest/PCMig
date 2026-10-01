using System;
using System.IO;
using System.Linq;
using PCMig.Diagnostics;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// ★ D6.3 Fixture 15 ★ 「上次会话未确认正常关闭」这条证据必须**读得到**，读不到时必须**如实说**。
///
/// 实测缺陷（D6.3 收口期发现）：恢复扫描用 File.ReadAllBytes 打开活动段（FileAccess.Read +
/// FileShare.Read）。共享检查是双向的 —— 它拒绝其它写句柄，于是只要**上一段会话仍然存活**、
/// 或者上一次进程还没退净，读段就抛 IOException "being used by another process"；
/// 而 DiagnosticSessionStore 把任何异常吞成 null，与"没有上一段会话"不可区分 ⇒
/// 证据里连这条都没有，界面看起来一切正常。这就是「证据缺失却说没事」。
/// </summary>
public class D63PreviousSessionScanTests
{
    private static string? LatestSegment(string sessionDir)
    {
        var eventsDir = Path.Combine(sessionDir, "events");
        if (!Directory.Exists(eventsDir)) return null;
        return Directory.EnumerateFiles(eventsDir, "*.jsonl")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault(f => new FileInfo(f).Length > 0);
    }

    /// <summary>等 writer 真的把字节落到盘上（不只看计数器），并等到它不再写。</summary>
    private static bool WaitForSegmentOnDisk(DiagnosticRuntime runtime)
    {
        Assert.NotNull(runtime.Store);
        if (!D2TestSupport.WaitUntil(() => LatestSegment(runtime.Store!.SessionDir) is not null, 10_000)) return false;
        var quiet = runtime.Health.EventsWritten;
        for (var i = 0; i < 20; i++)
        {
            System.Threading.Thread.Sleep(25);
            if (runtime.Health.EventsWritten == quiet) return true;
            quiet = runtime.Health.EventsWritten;
        }
        return true;
    }

    /// <summary>上一段会话仍持着活动段的写句柄时，恢复扫描必须仍然读得到（不得独占）。</summary>
    [Fact]
    public void Fixture15_ASegmentHeldOpenByALivingSessionCanStillBeRecovered()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var first = DiagnosticRuntime.Start(D2TestSupport.Options(root), out _);
            first.Publisher.TryPublish(new DiagnosticEventDraft(
                TransferEvents.JobRunStarted, DiagnosticContext.Root(first.SessionId, "t")));
            Assert.True(D2TestSupport.WaitUntil(() => first.Health.EventsWritten >= 1));
            Assert.NotNull(first.Store);
            Assert.True(WaitForSegmentOnDisk(first),
                "上一段会话没有在盘上留下活动段（非空）");

            var active = LatestSegment(first.Store!.SessionDir)!;

            // 第一段会话**仍然存活**（不 Shutdown），writer 可能正持着这个段的写句柄。
            var recovery = SegmentRecovery.RecoverActive(active, truncate: false);
            Assert.True(recovery.FileExisted, "活动段没有被识别为已恢复");
            Assert.True(recovery.CompleteLines >= 1, $"上一段会话的完整行数为 {recovery.CompleteLines}");

            var previous = DiagnosticSessionStore.FindPreviousUncleanSession(
                root, Guid.NewGuid(), currentProcessIsOnlyInstance: true, out var failure);
            Assert.True(failure is null, $"判定本不该失败，却给出 {failure}");
            Assert.True(previous is not null, "上一段会话（仍存活且没有 clean marker）必须能被判定出来");
            Assert.Equal("no-clean-marker", previous!.Value.ReasonCode);
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    /// <summary>端到端：下一段会话必须真的把「上次未确认正常关闭」发出来（不是只在盘上可读）。</summary>
    [Fact]
    public void Fixture15_TheNextSessionReallyReportsTheStillRunningPreviousSession()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var first = DiagnosticRuntime.Start(D2TestSupport.Options(root), out _);
            first.Publisher.TryPublish(new DiagnosticEventDraft(
                TransferEvents.JobRunStarted, DiagnosticContext.Root(first.SessionId, "t")));
            Assert.True(D2TestSupport.WaitUntil(() => first.Health.EventsWritten >= 1));
            Assert.NotNull(first.Store);
            Assert.True(WaitForSegmentOnDisk(first),
                "上一段会话没有在盘上留下活动段（非空）");
            // 故意不 Shutdown：不写 clean marker，且 writer 可能仍持句柄。

            var second = DiagnosticRuntime.Start(D2TestSupport.Options(root), out _);
            try
            {
                var seen = Array.Empty<string>();
                Assert.True(D2TestSupport.WaitUntil(() =>
                {
                    if (!second.TryGetViewerSnapshot(out var events)) return false;
                    seen = events.Select(e => e.Descriptor.Name).ToArray();
                    return seen.Contains(DiagnosticsEvents.PreviousSessionUnclean.Name);
                }, 20_000),
                    $"启动时必须报出「上次未确认正常关闭」；viewer 实际有 {seen.Length} 条：{string.Join(",", seen)}");

                Assert.True(second.TryGetViewerSnapshot(out var all));
                var evt = all.First(e => e.Descriptor.Name == DiagnosticsEvents.PreviousSessionUnclean.Name);
                var payload = Assert.IsType<DiaPreviousSessionUncleanPayload>(evt.Payload);
                Assert.Equal("no-clean-marker", payload.ReasonCode);
                Assert.Equal(first.SessionId, payload.PreviousSessionId);
            }
            finally
            {
                D2TestSupport.Shutdown(second);
                D2TestSupport.Dispose(second);
            }
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    /// <summary>
    /// 红灯夹具：判定**真的失败**（另一个句柄独占该段）时，必须给出失败原因码，
    /// 绝不静默等同"没有上一段会话"。
    /// </summary>
    [Fact]
    public void Fixture15_AScanThatCannotReadIsReportedAndNeverSilentlyMeansNothing()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var first = DiagnosticRuntime.Start(D2TestSupport.Options(root), out _);
            first.Publisher.TryPublish(new DiagnosticEventDraft(
                TransferEvents.JobRunStarted, DiagnosticContext.Root(first.SessionId, "t")));
            Assert.True(D2TestSupport.WaitUntil(() => first.Health.EventsWritten >= 1));
            Assert.NotNull(first.Store);
            Assert.True(WaitForSegmentOnDisk(first), "上一段会话没有在盘上留下活动段（非空）");

            // 让"扫最新段"这一步注定读不出来：造一个更新、且被本测试独占的段文件。
            //（不能用 FileShare.None 去锁会话自己的活动段 —— writer 正持着它，本测试自己也拿不到句柄。）
            var eventsDir = Path.Combine(first.Store!.SessionDir, "events");
            var blocked = Path.Combine(eventsDir, "events-9999.jsonl");
            File.WriteAllText(blocked, "{\"not\":\"a real event\"}" + Environment.NewLine);
            File.SetLastWriteTimeUtc(blocked, DateTime.UtcNow.AddSeconds(5));

            string? failure;
            PreviousSessionInfo? previous;
            using (var blocker = new FileStream(blocked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                previous = DiagnosticSessionStore.FindPreviousUncleanSession(
                    root, Guid.NewGuid(), currentProcessIsOnlyInstance: true, out failure);

                Assert.True(previous is null, "段读不出来时不该编造一个判定结果");
                Assert.True(failure is not null && failure.StartsWith("previous-session-scan-failed:", StringComparison.Ordinal),
                    $"判定失败必须给出原因码，实际是 {failure ?? "(null：被静默吞掉了)"}");
                Assert.Contains("IOException", failure!, StringComparison.Ordinal);

                // 端到端：下一段会话也必须把"判定失败"如实说出来，而不是沉默。
                var second = DiagnosticRuntime.Start(D2TestSupport.Options(root), out _);
                try
                {
                    var reported = string.Empty;
                    Assert.True(D2TestSupport.WaitUntil(() =>
                    {
                        if (!second.TryGetViewerSnapshot(out var events)) return false;
                        var evt = events.FirstOrDefault(e =>
                            e.Descriptor.Name == DiagnosticsEvents.PreviousSessionUnclean.Name);
                        if (evt is null) return false;
                        reported = (evt.Payload as DiaPreviousSessionUncleanPayload)?.ReasonCode ?? "(no-payload)";
                        return true;
                    }, 20_000),
                        "判定失败时启动必须说一句「上次未确认正常关闭」并带上失败原因，而不是什么都不发");

                    Assert.StartsWith("previous-session-scan-failed:", reported, StringComparison.Ordinal);
                }
                finally
                {
                    D2TestSupport.Shutdown(second);
                    D2TestSupport.Dispose(second);
                }
            }
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }
}