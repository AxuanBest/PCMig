using PCMig.Core.Models;
using PCMig.Core.Transfer;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ P0-03 ★ 破坏性修复闸门的回归测试。
///
/// 为什么必须单独守住这条判据：它决定"会不会删用户数据"。
/// 现场证据（原始日志实测）：Preview object-000018 的 14 个根层文件（29.46 GB）与
/// Stable a632 object-000004 的目标文件，都是被"假缺失/两侧 0 比 0"这类**没有确认差异**的
/// 验证结论触发的强制覆盖删掉的。这里把"什么情况下允许删"钉成一张表。
/// </summary>
public sealed class P0RepairGateTests
{
    private static ObjectVerifyResult Verified(string status, bool allows, string? unknownReason = null)
        => new()
        {
            ObjectId = "object-000001",
            Status = status,
            AllowsDestructiveRepair = allows,
            UnknownReason = unknownReason,
        };

    [Fact]
    public void P0_03_没有验证记录时闸门必须挡住删除()
    {
        // 老 Job 没有 verify-report.json：不得因为"没证据"就当作可以删。
        Assert.False(RepairGate.AllowsDestructiveRepair(null, out var code, out var status));
        Assert.Equal(RepairGate.NoEvidenceCode, code);
        Assert.Equal("无验证记录", status);
    }

    [Theory]
    [InlineData("source-unavailable")]
    [InlineData("target-unavailable")]
    [InlineData("both-sides-empty-against-plan")]
    [InlineData("enumeration-incomplete")]
    public void P0_03_UNKNOWN一律挡住并带上具体原因(string reason)
    {
        // 验证侧对 UNKNOWN 从不置 AllowsDestructiveRepair；这里再钉一次闸门自身的行为。
        Assert.False(RepairGate.AllowsDestructiveRepair(Verified("UNKNOWN", false, reason), out var code, out var status));
        Assert.Equal("unverifiable:" + reason, code);
        Assert.Equal("UNKNOWN", status);
        Assert.StartsWith("unverifiable:", code, StringComparison.Ordinal);
    }

    [Fact]
    public void P0_03_UNKNOWN缺原因也不放行()
    {
        Assert.False(RepairGate.AllowsDestructiveRepair(Verified("UNKNOWN", false), out var code, out _));
        Assert.Equal("unverifiable:unknown", code);
    }

    [Theory]
    [InlineData("OK")]
    [InlineData("EMPTY")]
    [InlineData("CANCELED")]
    [InlineData("MISMATCH")]
    public void P0_03_状态放行标志未置位时一律挡住(string status)
    {
        // MISMATCH 但验证侧没有置位（例如未来某条路径忘了置位）⇒ 仍然挡。
        Assert.False(RepairGate.AllowsDestructiveRepair(Verified(status, false), out var code, out var echoed));
        Assert.Equal(RepairGate.NotConfirmedCode, code);
        Assert.Equal(status, echoed);
    }

    [Fact]
    public void P0_03_只有确认差异才放行()
    {
        Assert.True(RepairGate.AllowsDestructiveRepair(Verified("MISMATCH", true), out var code, out var status));
        Assert.Equal(RepairGate.ConfirmedMismatchCode, code);
        Assert.Equal("MISMATCH", status);
    }

    [Fact]
    public void P0_03_放行标志与状态矛盾时按更安全的一侧()
    {
        // 安全带：状态说"无法确认"，却有人把放行标志置成 true ⇒ 必须仍然挡住。
        var vr = Verified("UNKNOWN", true, "enumeration-incomplete");
        Assert.False(RepairGate.AllowsDestructiveRepair(vr, out var code, out _));
        Assert.Equal("unverifiable:enumeration-incomplete", code);
    }

    [Fact]
    public void P0_03_状态空间里只有MISMATCH可能放行()
    {
        var statuses = new[] { "OK", "MISMATCH", "UNKNOWN", "EMPTY", "CANCELED", "", "随便写的" };
        foreach (var s in statuses)
        {
            foreach (var flag in new[] { true, false })
            {
                var allowed = RepairGate.AllowsDestructiveRepair(Verified(s, flag), out _, out _);
                var expected = s == "MISMATCH" && flag;
                Assert.True(allowed == expected,
                    $"状态「{s}」+ 放行标志 {flag} 的闸门结论应当是 {expected}，实际 {allowed}");
            }
        }
    }

    // ---- 接线契约（源码文本）：闸门真的挡在删除之前，且判据只有一处 ----

    [Fact]
    public void P0_03_编排器把判据委托给RepairGate且删除发生在放行之后()
    {
        var orchestrator = ReadFile("src", "PCMig.Core", "Transfer", "TransferOrchestrator.cs");
        var gate = ReadFile("src", "PCMig.Core", "Transfer", "RepairGate.cs");

        // 判据本体独立可测
        Assert.Contains("internal static class RepairGate", gate, StringComparison.Ordinal);
        Assert.Contains("public static bool AllowsDestructiveRepair(ObjectVerifyResult? vr, out string code, out string status)", gate, StringComparison.Ordinal);

        // 编排器只取"本对象的那条结论"，判据不再在编排器里另写一套
        Assert.Contains("RepairGate.AllowsDestructiveRepair(", orchestrator, StringComparison.Ordinal);
        // ★ D1-c ★ 报告级证据闸门（本 Job / 跑完 / 新于计划 / 同一构建版本）必须先过，且独立成类
        Assert.Contains("RepairEvidenceGate.Allows(report, _ctx, out code, out status)", orchestrator, StringComparison.Ordinal);
        Assert.Contains("report?.Objects.FirstOrDefault(o => o.ObjectId == obj.ObjectId)", orchestrator, StringComparison.Ordinal);
        Assert.DoesNotContain("code = \"not-confirmed-mismatch\";", orchestrator, StringComparison.Ordinal);

        // 强制覆盖必须先过闸门，且只有被挡时才有 repair-blocked 诊断（顺序 = 判据在删除之前）
        var guardAt = orchestrator.IndexOf("ForceOverwriteFromSource && !PurgeGateAllows(", StringComparison.Ordinal);
        var purgeAt = orchestrator.IndexOf("RepairPurge.PurgeTargetCopies(", StringComparison.Ordinal);
        Assert.True(guardAt > 0, "强制覆盖必须先过 PurgeGateAllows 闸门");
        Assert.True(purgeAt > 0, "找不到净化调用点");
        Assert.True(guardAt < purgeAt, "闸门必须在删除之前");
        Assert.Contains("\"repair-blocked:\" + gateCode", orchestrator, StringComparison.Ordinal);

        // 全仓只有这一处删除调用点（判据不被旁路）
        Assert.Equal(1, CountOccurrences(orchestrator, "RepairPurge.PurgeTargetCopies("));
    }

    [Fact]
    public void P0_03_净化只隔离目标侧且不碰源与重解析点()
    {
        var purge = ReadFile("src", "PCMig.Core", "Transfer", "RepairPurge.cs");

        Assert.Contains("var dst = Path.Combine(obj.TargetPath, rel);", purge, StringComparison.Ordinal);
        // ★ P0-03（2026-10-12 数据安全复核）★ 本判据已随修复升级：
        //   **净化路径里不得再有任何删除**——目标侧同名文件改为同目录改名隔离（RepairQuarantine），
        //   旧副本在"新副本被确认可信"之前始终留在磁盘上，且可经账本恢复。
        //   旧契约 `Assert.Contains("File.Delete(dst);", purge)` 断言的正是被判定为风险的行为，已废弃。
        Assert.DoesNotContain("File.Delete", purge, StringComparison.Ordinal);
        Assert.Contains("RepairQuarantine.TryQuarantine(", purge, StringComparison.Ordinal);
        // 盘满时宁可本轮不修，也不让旧数据先消失（空间不足 ⇒ 一个文件都不动）
        Assert.Contains("BlockedNoSpace", purge, StringComparison.Ordinal);
        // 不得删除源侧任何东西（源是只读共享；也绝不允许把"删源"写进修复路径）
        Assert.DoesNotContain("File.Delete(obj.SourcePath", purge, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Delete(obj.SourcePath", purge, StringComparison.Ordinal);
        // 重解析点/云占位符/排除项/无续传通道的大文件：一律不隔离（隔离了没人拷回来）
        Assert.Contains("FileAttributes.ReparsePoint", purge, StringComparison.Ordinal);
        Assert.Contains("skippedLarge++", purge, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
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