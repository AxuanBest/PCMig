using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// ★ D6.3 §8「Flight Recorder 真话」★ 审计 P1-3 的红灯：
///
///   ① 窗口"到没到截止时刻"与"有没有丢"被当成一件事 —— post 超容量/超预算丢掉的窗口，
///      清单里仍然写 `partial: false`（"完整"这个字的谎）；触发 3 条、post 容量只有 2 条就是最小反例。
///   ② 窗口在 pre 快照**还没复制完**时就被登记为可封存对象 ⇒ 并发封存/落盘能读到半个 pre，
///      甚至与复制线程争用同一个列表（"published half-initialized"）。
///   ③ 冻结在事件流里**不留痕迹**：`DIA.RingTriggered` / `DIA.RingSealed` 在目录里定义了却从没人发
///      ⇒ 窗口文件躺在磁盘上，读事件的人不知道发生过冻结，更不知道这份窗口有没有保住。
///
/// 判据同时覆盖**内部事实**（`FlightSealFacts`）与**外部真话**（窗口文件旁的清单 JSON），
/// 因为审计抓的正是"内部觉得没事、写出去的东西在说谎"。
/// </summary>
public sealed class D63FlightTruthTests
{
    private static FlightRecorder NewRecorder(
        out DiagnosticSessionStore store,
        out string root,
        Action<DiagnosticRuntimeOptions>? configure = null)
    {
        root = D2TestSupport.NewTempRoot();
        store = new DiagnosticSessionStore(root, TestEvents.FixedSessionId);
        store.EnsureCreated();
        var options = D2TestSupport.Options(root);
        options.RingEventCapacity = 2;
        options.RingByteBudget = 64 * 1024;
        options.FlightPostWindowMs = 0;          // 触发后立刻到期 ⇒ 测试不用 sleep 就能封存
        options.FlightTriggerCooldownMs = 1;
        options.FlightWindowPayloadByteBudget = 8L * 1024 * 1024;
        configure?.Invoke(options);
        return new FlightRecorder(store, options, new DiagnosticHealth(), new LossLedger());
    }

    // ─────────── Fixture 3：post 丢了东西 ⇒ 不许再说"完整" ───────────

    [Fact]
    public void Fixture3_PostWindowThatDroppedEventsCannotStillClaimComplete()
    {
        var recorder = NewRecorder(out var store, out var root);
        try
        {
            recorder.Observe(D2TestSupport.Event(1));
            recorder.Observe(D2TestSupport.Event(2));

            Assert.True(recorder.Trigger("rule:D63_FLIGHT", "d63").Accepted);

            // post 容量 = 环长度 = 2：发 3 条 ⇒ 第 3 条**必然**被丢（这正是审计举例的最小反例）。
            recorder.Observe(D2TestSupport.Event(3));
            recorder.Observe(D2TestSupport.Event(4));
            recorder.Observe(D2TestSupport.Event(5));

            var facts = recorder.Tick(checkpointEnabled: false);

            var seal = Assert.Single(facts);
            Assert.Equal(1, seal.DroppedEvents);
            Assert.False(seal.PostWindowComplete,
                "post 已经丢了事件，却仍宣称窗口完整（这就是 partial 说谎的源头）");
            Assert.True(seal.Persisted);

            // ★ 外部真话：清单里必须写 partial=true。
            var manifest = ReadManifest(Directory.GetFiles(store.FlightDir, "window-*.jsonl").Single());
            Assert.True(manifest.GetProperty("partial").GetBoolean(),
                "post 丢了事件，窗口清单却仍写 partial=false —— \"完整\"这个字在说谎");

            // 丢掉的 post 事件也必须进丢失台账（诊断自己不许悄悄少记）。
            Assert.True(recorder.Stats().DroppedEvents >= 1);
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    [Fact]
    public void CleanPostWindowStillReportsComplete()
    {
        var recorder = NewRecorder(out var store, out var root);
        try
        {
            recorder.Observe(D2TestSupport.Event(1));
            Assert.True(recorder.Trigger("rule:D63_FLIGHT", "d63").Accepted);

            recorder.Observe(D2TestSupport.Event(2));   // post 只发 1 条，容量 2 ⇒ 没丢

            var seal = Assert.Single(recorder.Tick(checkpointEnabled: false));
            Assert.Equal(0, seal.DroppedEvents);
            Assert.True(seal.PostWindowComplete, "一条都没丢的窗口不得被标成不完整");

            var manifest = ReadManifest(Directory.GetFiles(store.FlightDir, "window-*.jsonl").Single());
            Assert.False(manifest.GetProperty("partial").GetBoolean());
            Assert.Equal(2, manifest.GetProperty("eventCount").GetInt64());
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    // ─────────── ② 未就绪的窗口不得被封存/落盘 ───────────

    [Fact]
    public void WindowIsNotSealableBeforeItsPreSnapshotIsFilled()
    {
        // 关停路径的封存**必须**等到 pre 就绪；这里用"刚触发就立刻封存"来压这条路径。
        var recorder = NewRecorder(out var store, out var root, o => o.FlightPostWindowMs = 60_000);
        try
        {
            for (var i = 1; i <= 3; i++) recorder.Observe(D2TestSupport.Event(i));
            Assert.True(recorder.Trigger("rule:D63_FLIGHT", "d63").Accepted);

            var seals = recorder.SealOpenWindows();

            var seal = Assert.Single(seals);
            Assert.False(seal.PostWindowComplete, "关停时封存的窗口 post 必然不完整，必须如实标记");

            var file = Directory.GetFiles(store.FlightDir, "window-*.jsonl").Single();
            var lines = File.ReadAllLines(file).Where(l => l.Trim().Length > 0).ToArray();

            // 环容量 2 ⇒ pre 快照正好是这 2 条：一条不少，也绝不允许落出"半个 pre"。
            Assert.Equal(2, lines.Length);
            Assert.All(lines, l => Assert.True(l.Contains("\"sequence\"", StringComparison.Ordinal), "pre 快照里的每一行都必须是完整事件"));

            var manifest = ReadManifest(file);
            Assert.True(manifest.GetProperty("partial").GetBoolean());
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    // ─────────── ③ 冻结必须在事件流里留痕 ───────────

    [Fact]
    public void RingTriggeredAndRingSealedAreReallyPublished()
    {
        var root = D2TestSupport.NewTempRoot();
        var runtime = DiagnosticRuntime.Start(new DiagnosticRuntimeOptions
        {
            StorageRoot = Path.Combine(root, "Diagnostics"),
            InitialMode = CaptureMode.Operational,
            AppVersion = "d63-test",
            ShutdownBudgetMs = 5000,
            WriterFlushIntervalMs = 1,
            RingEventCapacity = 64,
            FlightPostWindowMs = 200,     // 维护节拍 250ms ⇒ 一两拍之内就会封存
            // ★ D6.3 ★ 下面这几条失败事件会让规则引擎**自动**开事件卡并自动冻结窗口
            //   （DiagnosticRuntime.cs:320-321：IncidentLifecycle.Opened 且 Severity ≥ Error ⇒ TriggerFlight）。
            //   自动窗口会占用并发上限（默认 FlightMaxPinnedWindows = 2）并与 1ms 冷却抢位，
            //   于是"手动触发被拒"变成时序抽奖（实测全量套件里偶发 Assert.True(outcome.Accepted) 失败）。
            //   本测试要验的是"一次真实触发必须在事件流里留痕"，不是配额策略 ⇒ 给它足够的配额与零冷却。
            FlightMaxPinnedWindows = 8,
            FlightTriggerCooldownMs = 0,
        }, out _);
        Assert.NotNull(runtime.Store);

        var eventsDir = Path.Combine(runtime.Store!.SessionDir, "events");

        try
        {
            for (var i = 0; i < 5; i++)
            {
                runtime.Publisher.TryPublish(new DiagnosticEventDraft(
                    PersistenceEvents.WriteFailed,
                    DiagnosticContext.Root(runtime.SessionId, "D63-Flight").WithJob("JOB-D63-FLIGHT"),
                    new Abstractions.Payloads.PstWritePayload("Receipt", "Move", true, "d63"),
                    Outcome: DiagnosticOutcome.Failed,
                    ErrorDomain: ErrorDomain.Managed,
                    ExceptionType: "IOException",
                    Message: "d63-flight"));
            }

            // 自己发的事件必须先真的落到盘上，否则后面的"没看见冻结痕迹"分不清是谁的问题。
            Assert.True(D2TestSupport.WaitUntil(() => ReadEventLines(eventsDir).Count >= 5, 10_000),
                $"连测试自己发的事件都没落盘：{Describe(eventsDir)}");

            var outcome = runtime.TriggerFlight("rule:D63_FLIGHT", "d63");
            Assert.True(outcome.Accepted, $"触发被拒：{outcome.ReasonCode}（merged={outcome.Merged}）");
            Assert.NotNull(outcome.TriggerId);

            // ① 触发必须在事件流里留痕。
            Assert.True(D2TestSupport.WaitUntil(
                () => ReadEventLines(eventsDir).Any(l => l.Contains("DIA.RingTriggered", StringComparison.Ordinal)), 10_000),
                $"冻结没有在事件流里留下 DIA.RingTriggered：{Describe(eventsDir)}");

            // ② 封存也必须留痕，且要说清"保住了多少"（只认这一次触发的窗口，别被自动触发的窗口蒙过去）。
            Assert.True(D2TestSupport.WaitUntil(
                () => ReadEventLines(eventsDir).Any(l => l.Contains("DIA.RingSealed", StringComparison.Ordinal)
                                                        && l.Contains(outcome.TriggerId!, StringComparison.Ordinal)), 15_000),
                $"这次冻结的窗口没有留下 DIA.RingSealed：{Describe(eventsDir)}");

            var triggered = ReadEventLines(eventsDir)
                .First(l => l.Contains("DIA.RingTriggered", StringComparison.Ordinal)
                            && l.Contains(outcome.TriggerId!, StringComparison.Ordinal));
            using var triggeredJson = JsonDocument.Parse(triggered);
            var triggeredPayload = triggeredJson.RootElement.GetProperty("payload");
            Assert.Equal(outcome.TriggerId, triggeredPayload.GetProperty("triggerId").GetString());
            Assert.Equal("rule:D63_FLIGHT", triggeredPayload.GetProperty("triggerCode").GetString());
            Assert.Equal(200, triggeredPayload.GetProperty("maxPostWindowMs").GetInt32());

            var sealedLine = ReadEventLines(eventsDir)
                .First(l => l.Contains("DIA.RingSealed", StringComparison.Ordinal)
                            && l.Contains(outcome.TriggerId!, StringComparison.Ordinal));
            using var sealedJson = JsonDocument.Parse(sealedLine);
            var sealedPayload = sealedJson.RootElement.GetProperty("payload");
            Assert.Equal(outcome.TriggerId, sealedPayload.GetProperty("triggerId").GetString());
            Assert.True(sealedPayload.GetProperty("postWindowComplete").GetBoolean(),
                "这个窗口一条都没丢，postWindowComplete 必须为 true");
            Assert.True(sealedPayload.GetProperty("actualCoverageMs").GetInt32() >= 0);

            // ③ 窗口文件真的在盘上，且清单与事实一致。
            var windowFile = Path.Combine(runtime.Store.SessionDir, "flight", $"window-{outcome.TriggerId}.jsonl");
            Assert.True(File.Exists(windowFile), $"窗口文件不在盘上：{Describe(Path.Combine(runtime.Store.SessionDir, "flight"))}");
            Assert.False(ReadManifest(windowFile).GetProperty("partial").GetBoolean());
        }
        finally
        {
            D2TestSupport.Shutdown(runtime);
            D2TestSupport.Dispose(runtime);
            D2TestSupport.Cleanup(root);
        }
    }

    // ───────────────────────────────── 助手 ─────────────────────────────────

    private static JsonElement ReadManifest(string segmentPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(segmentPath + ".manifest.json"));
        return document.RootElement.Clone();
    }

    private static List<string> ReadEventLines(string eventsDir)
    {
        var lines = new List<string>();
        if (!Directory.Exists(eventsDir)) return lines;
        foreach (var file in Directory.EnumerateFiles(eventsDir, "events-*.jsonl").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                    if (line.Trim().Length > 0) lines.Add(line);
            }
            catch (IOException) { /* 正在写入的段：这一轮读不到下一轮再读 */ }
        }

        return lines;
    }

    private static string Describe(string dir)
    {
        if (!Directory.Exists(dir)) return dir + " (不存在)";

        var parts = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.Ordinal))
        {
            var codes = new List<string>();
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    var at = line.IndexOf("\"eventName\":\"", StringComparison.Ordinal);
                    if (at < 0) continue;

                    var start = at + "\"eventName\":\"".Length;
                    var end = line.IndexOf('"', start);
                    if (end > start) codes.Add(line.Substring(start, end - start));
                }
            }
            catch (IOException) { parts.Add(Path.GetFileName(file) + "[读不到]"); continue; }

            parts.Add(Path.GetFileName(file) + "[" + codes.Count + ":" + string.Join("|", codes) + "]");
        }

        return parts.Count == 0 ? dir + " (空)" : string.Join(", ", parts);
    }
}