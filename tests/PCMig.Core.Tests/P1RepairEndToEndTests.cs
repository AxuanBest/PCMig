using System.Text;
using PCMig.Core.Jobs;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using PCMig.Core.Verify;
using Serilog;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ D2 样本 D + 数据门禁 G3 ★ 强制覆盖（修复）在**真实 robocopy + 真实文件系统**上的端到端语义。
///
/// 与 <see cref="RobocopyEndToEndTests"/> 的分工：那个只验证 RobocopyRunner 的参数拼装；
/// 这里跑的是完整的 <see cref="TransferOrchestrator"/> 修复链路（证据闸门 → 隔离旧副本 → 真实重拷 →
/// 收尾判定 → 真实复验），也就是"用户点修复"这条路真正会走的东西。
///
/// 三条断言各自守一件事：
///   ① 修复成功时必须**真的换成源的内容**，且复验必须给 OK（不是"完成 100% 却复验不过"）；
///   ② 目标文件被独占占用时，旧数据一个字节都不许消失，且结局必须如实报失败；
///   ③ 源不可达（断网/源消失）时，旧数据必须仍在盘上，并能经恢复通道原样放回。
/// </summary>
public class P1RepairEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-rpr-e2e-" + Guid.NewGuid().ToString("N")[..8]);
    private static readonly ILogger Log = Serilog.Core.Logger.None;

    public P1RepairEndToEndTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* 文件句柄延迟释放 */ }
    }

    // ───────────────────────── ① 修复成功的真实闭环 ─────────────────────────

    [Fact]
    public async Task D_强制覆盖隔离旧副本_修复后真实复验必须给OK()
    {
        const string oldText = "TARGET-OLD-CONTENT-0123456789";
        const string newText = "SOURCE-NEW-CONTENT-abcdefghij";
        var (ctx, obj) = Arrange("J-rpr-ok", newText, oldText);
        WriteMismatchEvidence(ctx, obj.ObjectId);

        var phase = await Run(ctx);

        Assert.Equal(JobPhase.Completed, phase);

        // ① 真实修复：目标侧现在就是源的内容
        Assert.Equal(newText, File.ReadAllText(Path.Combine(obj.TargetPath, "shared.txt")));

        // ② 收尾判定认得"可信新副本" ⇒ 旧副本被丢弃，不在目标侧留垃圾
        Assert.Empty(Directory.GetFiles(obj.TargetPath, "*" + RepairQuarantine.Suffix + "*"));
        Assert.Empty(RepairQuarantine.Load(RepairQuarantine.LedgerPath(ctx.JobDir), Log));

        // ③ 修复后必须能通过**真实复验**（P0-04：修复 100% 与复验通过是两件事，这里两件都要成立）
        var report = await new Verifier(ctx, new MigrationMatrix(), Log).RunAsync(VerifyLevel.L1_CountSize);
        var verified = Assert.Single(report.Objects);
        Assert.Equal("OK", verified.Status);
        Assert.True(report.OverallPass, "修复后复验必须通过；不通过说明修复链路本身有问题");
    }

    // ───────────────────── ② 目标文件被独占占用 ─────────────────────

    [Fact]
    public async Task D_目标文件被独占占用_旧数据不得消失且必须如实失败()
    {
        const string oldText = "TARGET-OLD-LOCKED-CONTENT";
        var (ctx, obj) = Arrange("J-rpr-lock", "SOURCE-NEW-CONTENT", oldText);
        WriteMismatchEvidence(ctx, obj.ObjectId);
        var targetFile = Path.Combine(obj.TargetPath, "shared.txt");

        JobPhase phase;
        await using (var hold = new FileStream(targetFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            phase = await Run(ctx);

            // ① 数据安全：占用导致挪不动 ⇒ 原位旧数据必须完好（一个字节都不许消失）
            hold.Position = 0;
            var buf = new byte[hold.Length];
            _ = hold.Read(buf, 0, buf.Length);
            Assert.Equal(oldText, Encoding.UTF8.GetString(buf));
        }

        // ② 账本里不许有"已隔离"的假记录
        Assert.Empty(RepairQuarantine.Load(RepairQuarantine.LedgerPath(ctx.JobDir), Log));

        // ③ 诚实：占用导致本轮没能修复，绝不能报"完成"
        Assert.NotEqual(JobPhase.Completed, phase);

        // ④ 释放占用后旧数据仍在原位（不是被删掉等重拷）
        Assert.Equal(oldText, File.ReadAllText(targetFile));
    }

    // ───────────────────── ③ 源不可达（断网等价物） ─────────────────────

    [Fact]
    public async Task D_源不可达_目标旧数据必须仍在盘上且可原样恢复()
    {
        const string oldText = "TARGET-OLD-MUST-SURVIVE";
        var (ctx, obj) = Arrange("J-rpr-nosrc", "SOURCE-NEW-CONTENT", oldText);
        WriteMismatchEvidence(ctx, obj.ObjectId);
        var targetFile = Path.Combine(obj.TargetPath, "shared.txt");

        // 源断网的真实等价物：计划已经生成，此刻源侧不可达
        Directory.Delete(obj.SourcePath, recursive: true);

        var phase = await Run(ctx);
        Assert.NotEqual(JobPhase.Completed, phase);

        // ① 数据安全不变量：旧内容必须还找得到（原位，或隔离副本里）——绝不允许"两边都没有"
        var (content, quarantined) = ReadOldContent(obj.TargetPath, "shared.txt");
        Assert.NotNull(content);
        Assert.Equal(oldText, content);

        // ② 本轮实证的机制：源不可达时工具**连目标文件都没碰**（账本无条目、文件仍在原位）。
        //    这条比"可恢复"更严：可恢复是兜底，最好的是根本不动目标侧数据。
        var ledger = RepairQuarantine.LedgerPath(ctx.JobDir);
        Assert.False(quarantined, "源不可达时不该把目标旧数据挪走（本用例实测：应在碰目标文件之前就失败）");
        Assert.Empty(RepairQuarantine.Load(ledger, Log));

        // ③ 兜底通道仍然可用：即使真有隔离记录，恢复也能原样放回（这里是"没有可恢复项"）
        var restore = RepairQuarantine.RestoreHeld(ledger, Log);
        Assert.Equal(0, restore.Restored);
        Assert.Equal(oldText, File.ReadAllText(targetFile));
    }

    // ─────────────────────────────── 夹具 ───────────────────────────────

    private (JobContext Ctx, PlannedObject Obj) Arrange(string jobId, string sourceText, string? targetText)
    {
        var src = Path.Combine(_root, jobId, "source");
        var dst = Path.Combine(_root, jobId, "target", "obj1");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "shared.txt"), sourceText);
        Directory.CreateDirectory(dst);
        if (targetText != null) File.WriteAllText(Path.Combine(dst, "shared.txt"), targetText);

        var obj = new PlannedObject
        {
            ObjectId = "object-000001",
            Kind = ObjectKind.DataVolume,
            SourcePath = src,
            TargetPath = dst,
            EstimatedBytes = new FileInfo(Path.Combine(src, "shared.txt")).Length,
            EstimatedFiles = 1
        };
        var ctx = new JobContext
        {
            JobDir = Path.Combine(_root, jobId, "job"),
            Definition = new JobDefinition { JobId = jobId, TargetRoot = Path.Combine(_root, jobId, "target") }
        };
        ctx.EnsureDirs();
        ctx.SaveDefinition();
        ctx.SavePlan(new MigrationPlan
        {
            JobId = jobId,
            Objects = new List<PlannedObject> { obj },
            TotalBytes = obj.EstimatedBytes
        });
        return (ctx, obj);
    }

    /// <summary>预置一份"确认差异"的验证报告：这是 RepairEvidenceGate 放行强制覆盖的唯一依据。</summary>
    private static void WriteMismatchEvidence(JobContext ctx, string objectId)
        => ctx.SaveVerify(new VerifyReport
        {
            JobId = ctx.JobId,
            VerifiedUtc = DateTime.UtcNow,
            Level = VerifyLevel.L1_CountSize,
            OverallPass = false,
            RunStatus = "completed",
            ToolVersion = typeof(RepairEvidenceGate).Assembly.GetName().Version?.ToString(),
            Objects = new List<ObjectVerifyResult>
            {
                new()
                {
                    ObjectId = objectId,
                    Status = "MISMATCH",
                    AllowsDestructiveRepair = true,
                    MissingSamples = new List<string> { "shared.txt" }
                }
            }
        });

    /// <summary>假设 4：真实 worker（生产 <see cref="RobocopyRunner"/>），只打开"强制覆盖"这一个开关。</summary>
    private static Task<JobPhase> Run(JobContext ctx)
        => new TransferOrchestrator(ctx, new MigrationMatrix(), Log) { ForceOverwriteFromSource = true }.RunAsync();

    /// <summary>旧内容此刻在哪：原位，还是隔离副本里。</summary>
    private static (string? Content, bool Quarantined) ReadOldContent(string dir, string fileName)
    {
        var original = Path.Combine(dir, fileName);
        if (File.Exists(original)) return (File.ReadAllText(original), false);
        var copy = Directory.GetFiles(dir, fileName + RepairQuarantine.Suffix + "*").FirstOrDefault();
        return copy is null ? (null, false) : (File.ReadAllText(copy), true);
    }
}