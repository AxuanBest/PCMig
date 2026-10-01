using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D2 契约：JSONL 分段存储、封段清单、崩溃恢复、存储故障降级、保留/配额。
/// </summary>
public sealed class D2StorageTests
{
    [Fact]
    public void WriterWritesCompleteJsonLinesAndSealsManifest()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var store = new DiagnosticSessionStore(root, TestEvents.FixedSessionId);
            store.EnsureCreated();
            var health = new DiagnosticHealth();
            var loss = new LossLedger();
            var writer = new JsonlSegmentWriter(store, D2TestSupport.Options(root), health, loss);

            for (var i = 1; i <= 5; i++)
                D2TestSupport.Write(writer, D2TestSupport.Event(i));

            var activePath = writer.ActiveSegmentPath;
            Assert.NotNull(activePath);
            Assert.True(File.Exists(activePath));

            // 先封段：封段之后文件不再有写句柄，读取行为也就与生产一致
            // （导出/离线读取走的都是"已封段"路径，不依赖 Windows 对活动写句柄的共享语义）。
            Assert.True(writer.SealActive(partial: false));
            Assert.Equal(5, health.EventsWritten);

            // 每行都必须是完整可解析的 JSON（写完即"行完整"是 JSONL 的核心承诺）。
            var lines = File.ReadAllLines(activePath!).Where(l => l.Length > 0).ToArray();
            Assert.Equal(5, lines.Length);
            foreach (var line in lines)
                Assert.True(Abstractions.Serialization.DiagnosticEventJson.TryParse(line, out _, out var error), error);

            var manifestPath = activePath + ".manifest.json";
            Assert.True(File.Exists(manifestPath), "封段必须写清单");
            var manifest = writer.SealedSegments.Single();
            Assert.Equal(5, manifest.EventCount);
            Assert.Equal(1, manifest.FirstSequence);
            Assert.Equal(5, manifest.LastSequence);
            Assert.False(manifest.Partial);
            Assert.Equal(64, manifest.Sha256.Length);
            Assert.Equal(new FileInfo(activePath!).Length, manifest.Length);
            Assert.Equal(SegmentRecovery.ComputeSha256(activePath!), manifest.Sha256);
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    [Fact]
    public void WriterRollsWhenSegmentLimitReached()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var store = new DiagnosticSessionStore(root, TestEvents.FixedSessionId);
            store.EnsureCreated();
            var options = D2TestSupport.Options(root);
            options.SegmentMaxBytes = 1; // 每条事件都超过阈值 ⇒ 每条后都滚动
            var writer = new JsonlSegmentWriter(store, options, new DiagnosticHealth(), new LossLedger());

            for (var i = 1; i <= 3; i++)
                D2TestSupport.Write(writer, D2TestSupport.Event(i));

            Assert.Equal(3, writer.SealedSegments.Count);
            var files = Directory.GetFiles(store.EventsDir, "events-*.jsonl");
            Assert.Equal(3, files.Length);
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    [Fact]
    public void RecoveryTruncatesIncompleteTailAndCountsCorruptLines()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var store = new DiagnosticSessionStore(root, TestEvents.FixedSessionId);
            store.EnsureCreated();
            var writer = new JsonlSegmentWriter(store, D2TestSupport.Options(root), new DiagnosticHealth(), new LossLedger());
            for (var i = 1; i <= 2; i++)
                D2TestSupport.Write(writer, D2TestSupport.Event(i));
            writer.SealActive(partial: false);

            var file = Directory.GetFiles(store.EventsDir, "events-*.jsonl").Single();

            // ① 模拟"写一半断电"：追加一条**没有换行**的不完整行。
            using (var fs = new FileStream(file, FileMode.Append, FileAccess.Write))
                fs.Write(Encoding.UTF8.GetBytes("{\"schemaVersion\":\"1.0\",\"sessionId\":\"aaa"));

            var dryRun = SegmentRecovery.RecoverActive(file, truncate: false);
            Assert.Equal(2, dryRun.CompleteLines);
            Assert.True(dryRun.TruncatedTailBytes > 0, "不完整末行必须被识别出来");
            Assert.Equal(0, dryRun.CorruptLines);

            var lengthBefore = new FileInfo(file).Length;
            var recovered = SegmentRecovery.RecoverActive(file, truncate: true);
            Assert.True(new FileInfo(file).Length < lengthBefore, "截断后文件应当变小");
            Assert.Equal(0, SegmentRecovery.RecoverActive(file, truncate: false).TruncatedTailBytes);

            // ② 完整但解析不了的行：必须被计成 CorruptLines，且**不吞掉、不假装不存在**。
            using (var fs = new FileStream(file, FileMode.Append, FileAccess.Write))
                fs.Write(Encoding.UTF8.GetBytes("this-is-not-json\n"));

            var withCorrupt = SegmentRecovery.RecoverActive(file, truncate: false);
            Assert.Equal(3, withCorrupt.CompleteLines);
            Assert.Equal(1, withCorrupt.CorruptLines);
            Assert.Equal(2, withCorrupt.LastSequence);
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    [Fact]
    public void StorageFailureDegradesQuietlyAndIsRecorded()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var store = new DiagnosticSessionStore(root, TestEvents.FixedSessionId);
            store.EnsureCreated();

            // 把 events 目录位置占成一个**文件** ⇒ 打开段必然失败。
            Directory.Delete(store.EventsDir, recursive: true);
            File.WriteAllText(store.EventsDir, "not a directory");

            var health = new DiagnosticHealth();
            var loss = new LossLedger();
            var writer = new JsonlSegmentWriter(store, D2TestSupport.Options(root), health, loss);

            // 绝不抛（生产线程不得被诊断故障影响）。
            for (var i = 1; i <= 3; i++)
                D2TestSupport.Write(writer, D2TestSupport.Event(i, PersistenceEvents.WriteFailed,
                    DeliveryClass.DurableCritical));

            Assert.True(health.StorageDegraded, "存储故障必须让自身健康降级");
            Assert.NotNull(health.LastStorageReason);
            Assert.True(loss.Snapshot().TotalDropped >= 1, "写不进去就是丢了：必须进台账");
            Assert.True(loss.StickyCriticalLost, "DurableCritical 写失败是 sticky 丢失");
            Assert.Equal(0, health.EventsWritten);
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    [Fact]
    public void SessionStoreLayoutAndMetadataRoundtrip()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var store = new DiagnosticSessionStore(root, TestEvents.FixedSessionId);
            store.EnsureCreated();
            store.WriteCatalog();
            store.WriteSessionMetadata(new DiagnosticSessionMetadata
            {
                SessionId = DiagnosticId.Format(TestEvents.FixedSessionId),
                StartedUtc = DateTimeOffset.UtcNow,
                MonotonicFrequency = DiagnosticClock.Frequency,
                StartedMonotonicTicks = 12345,
                AppVersion = "0.5.0",
                RuntimeVersion = "8.0.0",
                OsVersion = "Windows",
                Architecture = "X64",
                ProcessId = 4242,
                ProcessIdentity = "pid:4242",
                InitialMode = "Operational",
                CatalogVersion = EventCatalog.CatalogVersion,
                CatalogHash = EventCatalog.CatalogHash,
                EventCatalogCount = EventCatalog.Count,
                RedactionKeyId = "k-test",
                StorageRoot = root,
            });

            Assert.True(File.Exists(store.SessionFilePath));
            Assert.True(File.Exists(store.CatalogFilePath));
            foreach (var dir in new[] { store.EventsDir, store.MetricsDir, store.IncidentsDir, store.FlightDir, store.SnapshotsDir })
                Assert.True(Directory.Exists(dir), "缺少目录：" + dir);

            var metadata = DiagnosticSessionStore.TryReadSessionMetadata(store.SessionFilePath);
            Assert.NotNull(metadata);
            Assert.Equal("0.5.0", metadata!.AppVersion);
            Assert.Equal(EventCatalog.CatalogHash, metadata.CatalogHash);

            var catalogText = File.ReadAllText(store.CatalogFilePath);
            Assert.Contains(EventCatalog.CatalogHash, catalogText, StringComparison.Ordinal);
            Assert.Contains("UI-001", catalogText, StringComparison.Ordinal);
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    [Fact]
    public void PreviousSessionWithoutCleanMarkerIsReportedAsUnconfirmed()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var previousId = Guid.NewGuid();
            var store = new DiagnosticSessionStore(root, previousId);
            store.EnsureCreated();
            store.WriteSessionMetadata(new DiagnosticSessionMetadata
            {
                SessionId = DiagnosticId.Format(previousId),
                StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                MonotonicFrequency = DiagnosticClock.Frequency,
                StartedMonotonicTicks = 1,
                AppVersion = "0.5.0",
                RuntimeVersion = "8.0.0",
                OsVersion = "Windows",
                Architecture = "X64",
                ProcessId = 111,
                ProcessIdentity = "pid:111",
                InitialMode = "Operational",
                CatalogVersion = EventCatalog.CatalogVersion,
                CatalogHash = EventCatalog.CatalogHash,
                EventCatalogCount = EventCatalog.Count,
                RedactionKeyId = "k",
                StorageRoot = root,
            });

            var currentId = Guid.NewGuid();
            var found = DiagnosticSessionStore.FindPreviousUncleanSession(root, currentId);
            Assert.NotNull(found);
            Assert.Equal("no-clean-marker", found!.Value.ReasonCode);
            Assert.Equal(previousId, found.Value.SessionId);

            // 写上 clean marker 之后就不该再报"未确认正常关闭"。
            store.WriteCleanShutdownMarker(new DiagnosticCleanShutdownMarker
            {
                SessionId = DiagnosticId.Format(previousId),
                ShutdownUtc = DateTimeOffset.UtcNow,
                DrainedEvents = 10,
                SealedSegments = 1,
                FlushAcknowledged = true,
                LastSequence = 10,
            });

            Assert.Null(DiagnosticSessionStore.FindPreviousUncleanSession(root, currentId));
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }

    [Fact]
    public void RetentionDeletesOldestSealedSegmentsButKeepsPinnedAndActive()
    {
        var root = D2TestSupport.NewTempRoot();
        try
        {
            var store = new DiagnosticSessionStore(root, TestEvents.FixedSessionId);
            store.EnsureCreated();

            var sealedPaths = new List<string>();
            for (var i = 1; i <= 4; i++)
            {
                var path = Path.Combine(store.EventsDir, $"events-{i:0000}.jsonl");
                File.WriteAllText(path, new string('x', 1024));
                DiagnosticSessionStore.WriteManifestFor(path, new SegmentManifest
                {
                    Family = "events",
                    FileName = Path.GetFileName(path),
                    Length = 1024,
                    Sha256 = "0",
                    EventCount = 1,
                    FirstSequence = i,
                    LastSequence = i,
                    FirstTimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
                    LastTimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
                    Partial = false,
                    CorruptLines = 0,
                    SealedUtc = DateTimeOffset.UtcNow.ToString("O"),
                });
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10 + i)); // 旧→新
                sealedPaths.Add(path);
            }

            // 活动段（没有清单）绝不能被清理。
            var activePath = Path.Combine(store.EventsDir, "events-0099.jsonl");
            File.WriteAllText(activePath, "active");

            var options = D2TestSupport.Options(root);
            options.TotalQuotaBytes = 2_500; // 4×1KB + active ⇒ 需要删掉最旧的
            var pinned = sealedPaths[0];     // 最旧的那一段被"租约"钉住

            var result = RetentionManager.Enforce(store, options, new[] { pinned });

            Assert.True(result.DeletedFiles > 0);
            Assert.True(File.Exists(pinned), "被 pin 的段不得删除");
            Assert.True(File.Exists(activePath), "活动段（无清单）不得删除");
            Assert.False(File.Exists(sealedPaths[1]), "最旧的未 pin 段应当被删除");
        }
        finally
        {
            D2TestSupport.Cleanup(root);
        }
    }
}