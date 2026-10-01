using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using PCMig.Core.Diagnostics;
using PCMig.Core.Jobs;
using PCMig.Core.Models;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// ★ D6.3 §11 缺口③红灯：领域真话（Domain truthfulness）★
///
/// 实机缺陷（链路 5 复验）：用户点「恢复任务」，诊断写 `TRN.PauseRequestCleared { succeeded: true }`
/// —— 而**当时那个 job 的 `pause.request` 根本不存在**（没有任何暂停请求可清）。
/// 旧实现 `ClearPauseRequest()` 无论文件在不在都硬写 `Succeeded`，payload 里也没有任何
/// "当时是否真的存在 / 是否真的删掉了" 的事实：
///
///   `try { if (File.Exists(PauseRequestPath)) File.Delete(PauseRequestPath); } catch { }`
///   `PublishPauseRequest(immediate: false, succeeded: true, reasonCode: null, PauseRequestCleared: true);`
///
/// 后果：复核者无法据包断言"清了一个真实存在的暂停请求"，也无法区分
///   "清掉了" / "本来就没有" / "想删但删不掉（被占用/权限）" 这三种**完全不同的现场**。
///
/// 修复后（业务语义不变：不抛异常、不引入新流转，文件该删还是删）：
///   · 文件不存在 ⇒ `Skipped` + `requestExisted: false` + `deleted: false` + reasonCode `pause-request-absent`；
///   · 文件存在且删除成功 ⇒ `Succeeded` + `requestExisted: true` + `deleted: true`；
///   · 文件存在但删除失败 ⇒ `Failed` + `requestExisted: true` + `deleted: false` + reasonCode 为异常类型名；
///   · "写暂停请求"这类**不涉及删除**的事件 ⇒ 两个字段**不写**（不适用 ≠ false，绝不用 false 冒充）。
///
/// 这些断言只看**会话 JSONL 落盘的那一行**与 typed payload —— 即"诊断对外说的话"本身。
/// </summary>
[Collection(DiagnosticsAmbientCollection.Name)]
public sealed class D63PauseRequestTruthTests
{
    private const string ClearedClauseAbsent = "pause-request-absent";

    private static DiagnosticRuntime StartRuntime(string root, out IDisposable scope)
    {
        var runtime = DiagnosticRuntime.Start(D2TestSupport.Options(root), out _);
        scope = CoreDiagnostics.Install(runtime);
        return runtime;
    }

    private static JobContext NewContext(string root, string jobId)
    {
        var ctx = new JobContext
        {
            JobDir = Path.Combine(root, jobId),
            Definition = new JobDefinition { JobId = jobId },
            Diagnostics = CoreDiagnostics.ContextFor("JobContext", jobId),
        };
        ctx.EnsureDirs();
        return ctx;
    }

    private static DiagnosticEvent[] WaitForEvents(DiagnosticRuntime runtime, string eventName, int atLeast = 1)
    {
        Assert.True(D2TestSupport.WaitUntil(() =>
        {
            runtime.TryGetViewerSnapshot(out var snapshot);
            return snapshot.Count(e => e.Descriptor.Name == eventName) >= atLeast;
        }, 10_000), $"没有观察到 {eventName}");

        runtime.TryGetViewerSnapshot(out var events);
        return events.Where(e => e.Descriptor.Name == eventName).ToArray();
    }

    /// <summary>等会话 JSONL 里出现某个事件，并返回它**落盘的那一行**（诊断对外说的话本身）。</summary>
    private static string WaitForJsonLine(string root, string eventName, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        string? found = null;
        while (found is null && Environment.TickCount64 < deadline)
        {
            foreach (var file in Directory.EnumerateFiles(root, "events-*.jsonl", SearchOption.AllDirectories))
            {
                foreach (var line in ReadLinesShared(file))
                {
                    if (!line.Contains($"\"{eventName}\"", StringComparison.Ordinal)) continue;
                    found = line;
                    break;
                }
                if (found is not null) break;
            }

            if (found is null) Thread.Sleep(50);
        }

        Assert.True(found is not null, $"会话 JSONL 里没有找到 {eventName} 的落盘行");
        return found!;
    }

    /// <summary>与写线程共享读取（与 SegmentRecovery 同口径：只读 + 共享读写/删除）。</summary>
    private static string[] ReadLinesShared(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static string Compact(string jsonLine) => jsonLine.Replace(" ", string.Empty, StringComparison.Ordinal);

    // ───────── ① 本来就没有暂停请求：不许说"清掉了" ─────────

    [Fact]
    public void ClearingAPauseRequestThatNeverExistedMustNotClaimSuccess()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var runtime = StartRuntime(root, out var scope);
            try
            {
                var ctx = NewContext(root, "JOB-NOTHING-TO-CLEAR");
                Assert.False(File.Exists(ctx.PauseRequestPath));

                // 业务语义：没有任何暂停请求时清一次，不抛异常、不凭空造文件。
                ctx.ClearPauseRequest();
                Assert.False(File.Exists(ctx.PauseRequestPath));

                var cleared = WaitForEvents(runtime, TransferEvents.PauseRequestCleared.Name);
                var payload = Assert.IsType<TrnPauseRequestPayload>(cleared[0].Payload);

                // ★ 红灯本体：旧实现这里恒为 Succeeded，且 payload 里没有任何"是否真的存在"的事实。
                Assert.Equal(DiagnosticOutcome.Skipped, cleared[0].Outcome);
                Assert.False(payload.RequestExisted, "没有任何暂停请求可清时，不许声称当时存在过");
                Assert.False(payload.Deleted, "根本没有文件，不许声称删掉了");
                Assert.Equal(ClearedClauseAbsent, payload.ReasonCode);

                // 落盘的那一行必须同口径（观察面不许比内存对象更乐观）。
                var line = Compact(WaitForJsonLine(root, TransferEvents.PauseRequestCleared.Name));
                Assert.Contains("\"succeeded\":true", line, StringComparison.Ordinal);
                Assert.Contains("\"requestExisted\":false", line, StringComparison.Ordinal);
                Assert.Contains("\"deleted\":false", line, StringComparison.Ordinal);
                Assert.Contains(ClearedClauseAbsent, line, StringComparison.Ordinal);
            }
            finally
            {
                scope.Dispose();
                D2TestSupport.Shutdown(runtime);
                D2TestSupport.Dispose(runtime);
            }
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    // ───────── ② 真的清掉了一个暂停请求：必须留下"存在过 + 删掉了" ─────────

    [Fact]
    public void ClearingARealPauseRequestReportsThatItExistedAndWasDeleted()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var runtime = StartRuntime(root, out var scope);
            try
            {
                var ctx = NewContext(root, "JOB-REAL-CLEAR");
                ctx.RequestPause(immediate: true);
                Assert.True(File.Exists(ctx.PauseRequestPath));

                ctx.ClearPauseRequest();
                Assert.False(File.Exists(ctx.PauseRequestPath));

                var cleared = WaitForEvents(runtime, TransferEvents.PauseRequestCleared.Name);
                var payload = Assert.IsType<TrnPauseRequestPayload>(cleared[0].Payload);

                Assert.Equal(DiagnosticOutcome.Succeeded, cleared[0].Outcome);
                Assert.True(payload.RequestExisted, "文件当时确实存在，必须如实登记");
                Assert.True(payload.Deleted, "删除确实成功，必须如实登记");
                Assert.Null(payload.ReasonCode);

                var line = Compact(WaitForJsonLine(root, TransferEvents.PauseRequestCleared.Name));
                Assert.Contains("\"requestExisted\":true", line, StringComparison.Ordinal);
                Assert.Contains("\"deleted\":true", line, StringComparison.Ordinal);
            }
            finally
            {
                scope.Dispose();
                D2TestSupport.Shutdown(runtime);
                D2TestSupport.Dispose(runtime);
            }
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    // ───────── ③ 文件存在却删不掉：必须说 Failed，不许说清了 ─────────

    [Fact]
    public void ClearThatCannotDeleteMustReportFailureNotSuccess()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var runtime = StartRuntime(root, out var scope);
            try
            {
                var ctx = NewContext(root, "JOB-LOCKED-CLEAR");
                ctx.RequestPause(immediate: false);
                Assert.True(File.Exists(ctx.PauseRequestPath));

                // 故障注入：独占持有该文件（不共享删除）⇒ File.Delete 必然共享冲突失败。
                using (var hold = new FileStream(ctx.PauseRequestPath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Assert.True(File.Exists(ctx.PauseRequestPath), "故障注入前提：文件在独占句柄下仍必须被看得见");

                    ctx.ClearPauseRequest();          // 业务语义：不抛，吞掉失败
                    Assert.True(File.Exists(ctx.PauseRequestPath), "删不掉时文件必须原样留着（不许假装清掉）");

                    var cleared = WaitForEvents(runtime, TransferEvents.PauseRequestCleared.Name);
                    var payload = Assert.IsType<TrnPauseRequestPayload>(cleared[0].Payload);

                    Assert.Equal(DiagnosticOutcome.Failed, cleared[0].Outcome);
                    Assert.True(payload.RequestExisted, "文件当时确实存在");
                    Assert.False(payload.Deleted, "删除失败了，不许说删掉了");
                    Assert.False(string.IsNullOrEmpty(payload.ReasonCode), "删除失败必须给出原因（异常类型名）");

                    var line = Compact(WaitForJsonLine(root, TransferEvents.PauseRequestCleared.Name));
                    Assert.Contains("\"requestExisted\":true", line, StringComparison.Ordinal);
                    Assert.Contains("\"deleted\":false", line, StringComparison.Ordinal);
                }
            }
            finally
            {
                scope.Dispose();
                D2TestSupport.Shutdown(runtime);
                D2TestSupport.Dispose(runtime);
            }
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    // ───────── ④ 不涉及删除的事件不许写这两个字段（不适用 ≠ false） ─────────

    [Fact]
    public void WriteResultPayloadLeavesDeletionFactsUnsetInsteadOfFalse()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var runtime = StartRuntime(root, out var scope);
            try
            {
                var ctx = NewContext(root, "JOB-WRITE-ONLY");
                ctx.RequestPause(immediate: true);

                var requested = WaitForEvents(runtime, TransferEvents.PauseRequestWriteResult.Name);
                var payload = Assert.IsType<TrnPauseRequestPayload>(requested[0].Payload);

                Assert.True(payload.Immediate);
                Assert.True(payload.Succeeded);
                Assert.Null(payload.RequestExisted);
                Assert.Null(payload.Deleted);

                var line = Compact(WaitForJsonLine(root, TransferEvents.PauseRequestWriteResult.Name));
                Assert.DoesNotContain("requestExisted", line, StringComparison.Ordinal);
                Assert.DoesNotContain("\"deleted\"", line, StringComparison.Ordinal);
            }
            finally
            {
                scope.Dispose();
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