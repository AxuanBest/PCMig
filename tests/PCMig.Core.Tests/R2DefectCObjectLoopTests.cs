// ============================================================================
//  ★ R2 · 缺陷 C 回归 ★ 对象循环的中断通道：真实路径只有一个
// ============================================================================
//
//  ── 为什么必须存在（R2 接手审计 · 缺陷 C，2026-10-17）──────────────────────
//  对象循环里原本有这一句（旧 `TransferOrchestrator.cs:590`）：
//      if (receipt == null) { interrupted = true; break; }   // 注释：取消/Immediate 暂停导致中断
//  但 `RunOneObjectAsync` 的**每一个**出口都 `return receipt` 或 `return Mark*Failed(receipt)`，
//  方法体里根本没有 `return null`：
//      · 取消在 `RunPassWithRetryAsync` 里被吸收成 `if (result.Killed) return null;`，
//        到 `RunOneObjectAsync` 这一层已经被 `MarkInterrupted(...)` 转成
//        `ObjectStatus.Interrupted` 的回执（回执仍然存在、仍然落盘）；
//      · 真实的中断判定在循环下游：`if (receipt.Status == ObjectStatus.Interrupted) { interrupted = true; break; }`。
//  ⇒ 那句 null 判定是**不可达的死分支**，而它带着的注释会让后来人相信"存在第二条中断通道"，
//    更糟的是：任何人若照这条注释给 `RunOneObjectAsync` 增加 `return null`（比如"顺手"把某个
//    catch 改成 return null），就会**跳过 SaveReceipt 与对象级事件**——这正是 NEW-01 的形态
//    （回执不落盘、TRN-004 缺席、job-state 停在 running）。死分支不只是冗余，它是错误改动的邀请。
//
//  ── 本文件钉死的三条纪律 ────────────────────────────────────────────────────
//  R2C-1  `RunOneObjectAsync` 的返回类型不得为可空，方法体内不得出现 `return null`
//         （中断是回执的**状态**，不是一个空引用）；
//  R2C-2  中断的判定必须由 `receipt.Status == ObjectStatus.Interrupted` 承担，
//         且 `SaveReceipt` 必须在任何状态判定**之前**无条件执行；
//  R2C-3  行为层反证：对象中途被取消时，RunAsync 必须返回已结算阶段、
//         回执必须落盘且状态为 Interrupted —— 证明"中断也有回执"这条契约是真的成立。
// ============================================================================

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PCMig.Core.Jobs;
using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using Serilog.Core;
using Xunit;

namespace PCMig.Core.Tests;

public class R2DefectCObjectLoopTests
{
    private const string OrchestratorRelative = "src/PCMig.Core/Transfer/TransferOrchestrator.cs";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
        throw new InvalidOperationException("找不到含 PCMig.sln 的仓库根：" + AppContext.BaseDirectory);
    }

    private static string ReadRepoFile(string relative)
        => File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>去掉注释（注释里描述旧写法/旧行号不算违规）。</summary>
    /// <summary>
    /// 剥掉注释，只留代码 —— 供"不得出现某类型名"这类断言使用。
    ///
    /// 【实现已统一到 <see cref="SourceStripper"/> —— 不要在本文件里另写一份】
    /// 这里曾经是每个测试文件各写一份的块注释正则，而块注释正则会**失控**：
    /// 文档注释里出现字面量 `/*` 极其常见（例如「禁止定义任何 *Orchestrator/*Runner 类型」
    /// 这句注释里就有 `/*Runner`），正则把它当成块注释开头后会一路吃到下一个 `*/`，
    /// 把中间的**全部可执行代码**静默删掉。实测事故：`MigrationSessionViewModel.cs`
    /// 原文 240,758 B 被吞到只剩 90,929 B（丢 62.2%），而本文件的禁词断言当时之所以"通过"，
    /// 正是因为被吞掉的那 107 KB 里含有它要找的名字 —— 断言看着在守，其实全瞎。
    /// 现在统一走状态机实现，并有 <see cref="R2SourceStripperTests"/> 直接盯着它。
    /// </summary>
    private static string StripComments(string text) => SourceStripper.StripComments(text);

    /// <summary>按花括号配平取方法体；跳过调用点（`await RunOneObjectAsync(...);` 结尾是 `);`）。</summary>
    private static string MethodBody(string text, string methodName)
    {
        foreach (Match sig in Regex.Matches(text, @"\b" + methodName + @"\s*\("))
        {
            var open = text.IndexOf('{', sig.Index + sig.Length);
            if (open < 0) continue;
            var between = text.Substring(sig.Index + sig.Length, open - (sig.Index + sig.Length));
            if (between.Contains(';')) continue;

            var depth = 0;
            for (var j = open; j < text.Length; j++)
            {
                if (text[j] == '{') depth++;
                else if (text[j] == '}')
                {
                    depth--;
                    if (depth == 0) return text.Substring(open, j - open + 1);
                }
            }
            Assert.Fail($"TransferOrchestrator.cs 的 {methodName} 花括号不配平。");
        }
        Assert.Fail($"TransferOrchestrator.cs 里找不到方法体 {methodName}（只找到调用点）。");
        return string.Empty;
    }

    // ══════════════════════════ R2C-1 ══════════════════════════

    [Fact]
    public void R2C01_RunOneObjectAsync_MustNotReturnNull()
    {
        var code = StripComments(ReadRepoFile(OrchestratorRelative));

        // 返回类型不得可空（历史形态：Task<ObjectReceipt?>）。
        Assert.DoesNotMatch(new Regex(@"Task<ObjectReceipt\?>"), code);
        Assert.Matches(new Regex(@"private\s+async\s+Task<ObjectReceipt>\s+RunOneObjectAsync\s*\("), code);

        var body = StripComments(MethodBody(code, "RunOneObjectAsync"));
        Assert.DoesNotMatch(new Regex(@"return\s+null\s*;"), body);
    }

    // ══════════════════════════ R2C-2 ══════════════════════════

    [Fact]
    public void R2C02_ObjectLoop_SavesReceiptBeforeAnyStatusBranch_AndUsesStatusForInterrupt()
    {
        var code = StripComments(ReadRepoFile(OrchestratorRelative));

        // 真实中断通道：由回执状态判定。
        Assert.Contains("if (receipt.Status == ObjectStatus.Interrupted)", code, StringComparison.Ordinal);

        // SaveReceipt 必须先于状态判定（回执落盘不得依赖对象成败）。
        var call = code.IndexOf("await RunOneObjectAsync(", StringComparison.Ordinal);
        Assert.True(call > 0, "找不到对 RunOneObjectAsync 的调用点。");
        var save = code.IndexOf("_ctx.SaveReceipt(receipt)", call, StringComparison.Ordinal);
        var interruptBranch = code.IndexOf("if (receipt.Status == ObjectStatus.Interrupted)", call, StringComparison.Ordinal);

        Assert.True(save > call, "SaveReceipt 必须紧跟在 RunOneObjectAsync 之后。");
        Assert.True(interruptBranch > save, "中断判定必须排在 SaveReceipt 之后 —— 否则被中断对象不会有回执。");

        // 两条循环内 break 前的收尾段必须仍然按状态判定（不得被人改成 null 判定）。
        var loopTail = code.Substring(save, Math.Min(2600, code.Length - save));
        Assert.DoesNotContain("receipt == null", loopTail, StringComparison.Ordinal);
    }

    // ══════════════════════════ R2C-3 ══════════════════════════

    /// <summary>永不返回的 worker：复刻 robocopy 被取消时的契约（吸收取消 ⇒ Killed=true）。</summary>
    private sealed class HangingWorker : ITransferWorker
    {
        public PCMig.Diagnostics.Abstractions.DiagnosticContext Diagnostics { get; set; }
            = PCMig.Diagnostics.Abstractions.DiagnosticContext.Root(Guid.Empty);
        public bool HasRunningWorker => true;
        public void KillCurrent() { }
        public bool TryGetWorkerReadBytes(out long bytes) { bytes = 0; return false; }

        public async Task<RobocopyRunResult> RunPassAsync(
            string src, string dst, MigrationOptions opt, MigrationMatrix matrix,
            PassKind pass, string unicodeLogPath, CancellationToken ct,
            IReadOnlyList<string>? fileList = null, bool restartableLarge = false)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 与 RobocopyRunner.cs 取消吸收语义一致：返回 Killed=true 的结果，而不是抛出去。
                return new RobocopyRunResult(-1, false, Killed: true, TimeSpan.FromMilliseconds(200), "canceled");
            }
            throw new InvalidOperationException("不可达");
        }
    }

    [Fact]
    public async Task R2C03_CancelledObject_StillPersistsInterruptedReceipt_AndSettles()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcmig-r2-defect-c", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string jobId = "J-r2c";
            var sourceDir = Path.Combine(root, "source");
            Directory.CreateDirectory(sourceDir);
            File.WriteAllBytes(Path.Combine(sourceDir, "src.bin"), new byte[16]);

            var ctx = new JobContext
            {
                JobDir = Path.Combine(root, jobId),
                Definition = new JobDefinition { JobId = jobId, TargetRoot = Path.Combine(root, "target", jobId) },
                Plan = new MigrationPlan
                {
                    JobId = jobId,
                    TotalBytes = 4096,
                    Objects =
                    {
                        new PlannedObject
                        {
                            ObjectId = "object-000001",
                            Kind = ObjectKind.DataVolume,
                            SourcePath = sourceDir,
                            TargetPath = Path.Combine(root, "target", jobId, "one"),
                            EstimatedBytes = 4096,
                        },
                    },
                },
            };
            ctx.EnsureDirs();

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            var orch = new TransferOrchestrator(ctx, new MigrationMatrix(), Logger.None, new HangingWorker());

            var phase = await orch.RunAsync(null, cts.Token).WaitAsync(TimeSpan.FromSeconds(30));

            // ① 引擎必须真的返回（不挂起），且离开 Running；
            Assert.NotEqual(JobPhase.Running, phase);

            // ② 被中断的对象**必须有回执**（缺陷 C 的反面：不得因为"中断"就没有回执）；
            var receipts = ctx.LoadReceipts(Logger.None);
            var one = receipts.LastOrDefault(r => r.ObjectId == "object-000001");
            Assert.NotNull(one);
            Assert.Equal(ObjectStatus.Interrupted, one!.Status);

            // ③ job-state 也必须已经写下来（不得停在 running）。
            var state = ctx.LoadStateOrNew();
            Assert.NotEqual(JobPhase.Running, state.Phase);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
