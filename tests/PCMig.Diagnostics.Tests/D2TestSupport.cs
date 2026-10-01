using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PCMig.Diagnostics.Abstractions;

namespace PCMig.Diagnostics.Tests;

/// <summary>D2 测试支撑（临时目录、等待助手、事件构造）。</summary>
internal static class D2TestSupport
{
    public static string NewTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "pcmig-diag-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static void Cleanup(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (Exception) { /* 测试清理失败不影响判定 */ }
    }

    /// <summary>轮询等待（有超时，失败时返回 false 而不是挂死）。</summary>
    public static bool WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    /// <summary>
    /// 造一条合成事件。**默认投递类是 Operational**（正常流量）：
    /// 早期版本默认用了 Verbose 事件（HealthSummary），导致 fan-out 正确地跳过
    /// analyzer/viewer 时被误读成"隔离失效"——默认值必须是"普通事件"，Verbose 要显式指定。
    /// </summary>
    public static DiagnosticEvent Event(
        long sequence,
        EventDescriptor? descriptor = null,
        DeliveryClass? delivery = null,
        IDiagnosticPayload? payload = null,
        string? message = null,
        long monotonic = 0)
    {
        var d = descriptor ?? Abstractions.Events.TransferEvents.ObjectCompleted;
        return new DiagnosticEvent
        {
            SchemaVersion = DiagnosticEvent.CurrentSchemaVersion,
            Descriptor = d,
            SessionId = TestEvents.FixedSessionId,
            Sequence = sequence,
            TimestampUtc = new DateTimeOffset(2026, 9, 30, 6, 31, 32, TimeSpan.Zero).AddMilliseconds(sequence),
            MonotonicTimestamp = monotonic,
            Level = d.Level,
            Delivery = delivery ?? d.Delivery,
            EvidenceQuality = EvidenceQuality.Direct,
            CaptureMode = CaptureMode.Operational,
            Message = message,
            Payload = payload,
        };
    }

    public static DiagnosticRuntimeOptions Options(string? storageRoot, CaptureMode mode = CaptureMode.Operational) => new()
    {
        StorageRoot = storageRoot,
        InitialMode = mode,
        ShutdownBudgetMs = 800,
        WriterFlushIntervalMs = 1,
        AppVersion = "test",
    };

    // ────────────────────────── 同步等待收尾（测试专用） ──────────────────────────
    //
    // 为什么集中在这里，而不是把每个测试都改成 async：
    //   · 这些收尾调用几乎都在 finally 里（测试方法保持同步更直观，且断言不需要 await）；
    //   · 死锁风险来自"被测代码捕获调用方 SynchronizationContext"——诊断运行时内部
    //     **全部** await 都带 ConfigureAwait(false)（BoundedBranch / DiagnosticRuntime / 泵），
    //     因此续体只在线程池上跑，同步等待不存在 xUnit1031 所担心的回环；
    //   · 集中一处也比 38 个散落的 GetAwaiter().GetResult() 更容易审查。

    public static DiagnosticShutdownReport Shutdown(DiagnosticRuntime runtime, int? budgetMs = null)
        => runtime.ShutdownAsync(budgetMs is null ? null : TimeSpan.FromMilliseconds(budgetMs.Value))
            .GetAwaiter().GetResult();

    public static void Dispose(DiagnosticRuntime runtime)
        => runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();

    public static void Stop(BoundedBranch branch, int budgetMs)
        => branch.StopAsync(TimeSpan.FromMilliseconds(budgetMs)).GetAwaiter().GetResult();

    public static void Route(FanOutStage fanOut, BranchItem item)
        => fanOut.RouteAsync(item, CancellationToken.None).GetAwaiter().GetResult();

    public static void Write(JsonlSegmentWriter writer, DiagnosticEvent evt, int bytes = 200)
        => writer.ConsumeAsync(new BranchItem(evt, bytes), CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>测试专用：同步等待一个 Task 的结果（集中一处，避免散落的 sync-over-async）。</summary>
    public static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();

    /// <summary>
    /// 读取一个**正在被 writer 持有**的段文件（测试专用）。
    /// 必须显式指定 FileShare.ReadWrite：writer 以 FileShare.Read 打开，且它持有写权限，
    /// 因此读取方必须允许写共享，否则会撞上"文件被另一进程占用"（D2 实测踩过）。
    /// </summary>
    public static string[] ReadAllLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd()
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToArray();
    }
}