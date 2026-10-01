using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using Serilog;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D6.1 §16（Core 插桩覆盖：**Repair**）收口测试。
///
/// 重点：强制覆盖的净化聚合里，**删除失败原先完全不可见**（只写 Debug 日志）。
/// 这里用**真实的被占用文件**制造删除失败，证明它现在被计数、被语义化。
/// </summary>
public sealed class D61RepairInstrumentationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-rpr-" + Guid.NewGuid().ToString("N")[..8]);

    public D61RepairInstrumentationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* 句柄延迟释放 */ }
    }

    private static readonly ILogger Silent = new LoggerConfiguration().CreateLogger();

    private (PlannedObject Obj, string Src, string Dst) MakeTrees(int files, params string[] lockedNames)
    {
        var src = Path.Combine(_root, "src");
        var dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);

        for (var i = 0; i < files; i++)
        {
            var name = $"f{i:000}.bin";
            File.WriteAllText(Path.Combine(src, name), "payload-" + i);
            File.WriteAllText(Path.Combine(dst, name), "payload-" + i);
        }

        return (new PlannedObject
        {
            ObjectId = "obj-rpr",
            Kind = ObjectKind.DataVolume,
            SourcePath = src,
            TargetPath = dst,
        }, src, dst);
    }

    private static MigrationMatrix Matrix() => MigrationMatrix.Load(null, Silent);

    [Fact]
    public void PurgeCountsDeletedSkippedAndFailedSeparately()
    {
        var (obj, _, dst) = MakeTrees(files: 5);

        // 制造一个**真实**的删除失败：目标侧文件被独占持有。
        var lockedPath = Path.Combine(dst, "f000.bin");
        using var holder = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var options = new MigrationOptions { LargeFileThresholdMB = 512 };
        var result = RepairPurge.PurgeTargetCopies(obj, options, Matrix(), Silent);

        Assert.Equal(4, result.Files);          // 另外 4 个确实删掉了
        Assert.Equal(1, result.Failed);         // ★ 被占用的那一个必须计入失败（原先只写 Debug 日志）
        Assert.Equal(0, result.SkippedLarge);
        Assert.True(result.Bytes > 0);

        // 删除成功的那 4 个真的不在了；被占用的那个仍在（未假装删除）。
        Assert.False(File.Exists(Path.Combine(dst, "f001.bin")));
        Assert.True(File.Exists(lockedPath));
    }

    [Fact]
    public void LargeFilesWithoutRestartablePassAreSkippedNotDeleted()
    {
        var (obj, _, dst) = MakeTrees(files: 1);                       // f000.bin：小文件（会被删）
        // 阈值语义（实现口径）：thresholdBytes = max(阈值MB, 1) × 1MiB ⇒ 下限是 1 MiB，
        // 所以"小文件"永远不可能命中大文件分支 —— 必须造一个真的 ≥1 MiB 的文件。
        var large = Path.Combine(dst, "big.bin");
        File.WriteAllBytes(large, new byte[1200 * 1024]);
        File.WriteAllBytes(Path.Combine(obj.SourcePath, "big.bin"), new byte[1200 * 1024]);

        obj.UseRestartablePass = false;                            // 未标记 /Z 通道 ⇒ 大文件必须跳过不删
        var options = new MigrationOptions { LargeFileThresholdMB = 1 };

        // ★ 阈值优先取**矩阵**的值（实现口径：matrix 优先于 options），默认 512 MiB；
        //   因此这里显式把矩阵阈值调小，才能在不写 512 MiB 文件的前提下命中"大文件"分支。
        var matrix = MigrationMatrix.Load(null, Silent);
        matrix.LargeFileThresholdMB = 1;

        var result = RepairPurge.PurgeTargetCopies(obj, options, matrix, Silent);

        Assert.Equal(1, result.SkippedLarge);       // ★ 大文件被跳过（删了就没人拷回来）
        Assert.Equal(1, result.Files);              // 小文件正常删除
        Assert.Equal(0, result.Failed);
        Assert.True(File.Exists(large), "跳过的大文件不得被删除");
        Assert.False(File.Exists(Path.Combine(dst, "f000.bin")));
    }

    /// <summary>净化事件必须带**失败数**，且失败时另发一条 Warning 级 PurgeFailed。</summary>
    [Fact]
    public void PurgePayloadCarriesTheFailureCountAndReason()
    {
        var ok = new RprPurgePayload(4, 4096, 1, 0, "force-overwrite");
        Assert.Equal("RprPurge", ok.PayloadName);

        var failed = new RprPurgePayload(4, 4096, 1, 3, "delete-failed");
        var json = WritePayloadJson(failed);
        Assert.Contains("\"failed\":3", json, StringComparison.Ordinal);
        Assert.Contains("\"skippedLarge\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"reasonCode\":\"delete-failed\"", json, StringComparison.Ordinal);

        // 事件定义：PurgeFailed 必须是 Warning 级、PurgeAttempted 是 Information 级。
        Assert.Equal(DiagnosticLevel.Warning, RepairEvents.PurgeFailed.Level);
        Assert.Equal(DiagnosticLevel.Information, RepairEvents.PurgeAttempted.Level);
    }

    /// <summary>修复链路必须有 production 生产者与调用点（源码级契约）。</summary>
    [Fact]
    public void RepairChainHasProductionProducersAndCallSites()
    {
        var orchestrator = ReadFile("src", "PCMig.Core", "Transfer", "TransferOrchestrator.cs");
        var purge = ReadFile("src", "PCMig.Core", "Transfer", "RepairPurge.cs");
        var vm = ReadFile("src", "PCMig.WinUI", "Presentation", "MigrationSessionViewModel.cs");

        // 净化：聚合里必须有失败计数，并且两个事件都在调用点被发布。
        Assert.Contains("PurgedResult", purge.Replace("PurgeResult", "PurgedResult"), StringComparison.Ordinal); // 结构存在（record）
        Assert.Contains("failed++", purge, StringComparison.Ordinal);
        Assert.Contains("RepairEvents.PurgeAttempted", orchestrator, StringComparison.Ordinal);
        Assert.Contains("RepairEvents.PurgeFailed", orchestrator, StringComparison.Ordinal);

        // 修复链路：请求 / 目标来源 / 无目标 / 完成
        Assert.Contains("RepairEvents.RepairRequested", vm, StringComparison.Ordinal);
        Assert.Contains("RepairEvents.RepairTargetsCollected", vm, StringComparison.Ordinal);
        Assert.Contains("RepairEvents.RepairNoTargets", vm, StringComparison.Ordinal);
        Assert.Contains("RepairEvents.RepairCompleted", vm, StringComparison.Ordinal);

        // 观察不得改变修复决策：原有 targets 计算与 forceOverwrite 语义必须仍在。
        Assert.Contains("public List<string> CollectRepairTargets(out int beforeMismatch)", vm, StringComparison.Ordinal);
        Assert.Contains("RunTransferCoreAsync(ctx, password, targets, forceOverwrite, progress, ct)", vm, StringComparison.Ordinal);
    }

    private static string WritePayloadJson(IDiagnosticPayload payload)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            payload.WriteJson(writer);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string ReadFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln")))
            {
                var path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
                Assert.True(File.Exists(path), "缺少文件：" + path);
                return File.ReadAllText(path);
            }
        }
        throw new InvalidOperationException("找不到仓库根");
    }
}