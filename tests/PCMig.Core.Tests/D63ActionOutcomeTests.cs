using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PCMig.Core.Diagnostics;
using PCMig.Core.Models;
using PCMig.Diagnostics.Abstractions;
using PCMig.WinUI.Presentation;
using Serilog;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// D6.3 §11 / 审计 P1-4 的**红灯 Fixture 6**：UI 动作的终点结果必须来自实际业务结果，
/// 绝不允许把"方法返回了"写成 Succeeded。
///
/// 分两层：
///   · **策略层**（<see cref="ActionOutcomePolicy"/>，纯函数，本测试项目真调用）——
///     验证/修复/开始/暂停/停止的每一种事实组合都必须落到正确的终点结果；
///   · **会话层**（<c>MigrationSessionViewModel</c>，经 csproj 的 A5LinkedPresentationSources
///     按文件链入本测试项目 ⇒ 能真 new 出来）—— 没有任何任务的会话里，
///     Run/Verify/Repair/Stop/Pause **一律不得**报 Succeeded（改回旧写法这条会红）。
///
/// 为什么必须有这一条：收口前 10 个调用点都写死
/// <c>trace.Complete(DiagnosticOutcome.Succeeded, "xxx-returned")</c>，
/// 于是"点了停止但根本没在跑"、"点了修复但没有需要修复的对象"也会被记成成功 ——
/// 这正是 D6.2 复核判定 Stage B **NOT READY** 的五词之一（Complete/Succeeded）。
/// </summary>
public sealed class D63ActionOutcomeTests
{
    // ── 策略层：验证 ──

    /// <summary>
    /// Fixture 6 的核心：拒绝 / 取消 / 失败 / 成功 **四种必须是四种不同的结果**，
    /// 且成功只能来自真实的 <c>OverallPass</c>。
    /// </summary>
    [Fact]
    public void Fixture6_VerifyOutcomesDifferAndSuccessNeedsARealPass()
    {
        var rejected = ActionOutcomePolicy.ForVerify(new VerifyBusinessResult(false, false, "verify-gate-blocked", false, false));
        var canceled = ActionOutcomePolicy.ForVerify(new VerifyBusinessResult(false, false, null, true, false));
        var faulted = ActionOutcomePolicy.ForVerify(new VerifyBusinessResult(false, false, null, false, true));
        var mismatch = ActionOutcomePolicy.ForVerify(new VerifyBusinessResult(true, false, null, false, false));
        var notExecuted = ActionOutcomePolicy.ForVerify(new VerifyBusinessResult(false, false, null, false, false));
        var passed = ActionOutcomePolicy.ForVerify(new VerifyBusinessResult(true, true, null, false, false));

        Assert.Equal(DiagnosticOutcome.Rejected, rejected.Outcome);
        Assert.Equal("verify-gate-blocked", rejected.ReasonCode);
        Assert.Equal(DiagnosticOutcome.Canceled, canceled.Outcome);
        Assert.Equal("verify-canceled", canceled.ReasonCode);
        Assert.Equal(DiagnosticOutcome.Failed, faulted.Outcome);
        Assert.Equal("verify-faulted", faulted.ReasonCode);
        Assert.Equal(DiagnosticOutcome.Failed, mismatch.Outcome);
        Assert.Equal("verify-mismatch", mismatch.ReasonCode);
        Assert.Equal(DiagnosticOutcome.Skipped, notExecuted.Outcome);
        Assert.Equal("verify-not-executed", notExecuted.ReasonCode);
        Assert.Equal(DiagnosticOutcome.Succeeded, passed.Outcome);
        Assert.Equal("verify-pass", passed.ReasonCode);

        // 四词不得塌成一个：拒绝/取消/失败/成功必须是四种结果（"全都 Succeeded"这条断言会红）。
        var outcomes = new[] { rejected, canceled, faulted, passed }.Select(d => d.Outcome).Distinct().ToList();
        Assert.Equal(4, outcomes.Count);
        foreach (var d in new[] { rejected, canceled, faulted, mismatch, notExecuted })
            Assert.NotEqual(DiagnosticOutcome.Succeeded, d.Outcome);
    }

    // ── 策略层：修复 ──

    /// <summary>修复：只有真的拿到了完成回执才是 Succeeded；有失败就不是；没有回执就 Unknown。</summary>
    [Fact]
    public void Fixture6_RepairOnlySucceedsFromRealReceipts()
    {
        var noTargets = ActionOutcomePolicy.ForRepair(new RepairBusinessResult(0, 0, 0, null));
        var partial = ActionOutcomePolicy.ForRepair(new RepairBusinessResult(3, 2, 1, null));
        var allOk = ActionOutcomePolicy.ForRepair(new RepairBusinessResult(3, 3, 0, null));
        var noReceipts = ActionOutcomePolicy.ForRepair(new RepairBusinessResult(3, 0, 0, null));
        var blocked = ActionOutcomePolicy.ForRepair(new RepairBusinessResult(0, 0, 0, "repair-no-task"));

        Assert.Equal(DiagnosticOutcome.Skipped, noTargets.Outcome);
        Assert.Equal("repair-no-targets", noTargets.ReasonCode);
        Assert.Equal(DiagnosticOutcome.Failed, partial.Outcome);
        Assert.Equal("repair-partial", partial.ReasonCode);
        Assert.Equal(DiagnosticOutcome.Succeeded, allOk.Outcome);
        Assert.Equal("repair-completed", allOk.ReasonCode);
        Assert.Equal(DiagnosticOutcome.Unknown, noReceipts.Outcome);
        Assert.Equal("repair-no-receipts", noReceipts.ReasonCode);
        Assert.Equal(DiagnosticOutcome.Rejected, blocked.Outcome);

        foreach (var d in new[] { noTargets, partial, noReceipts, blocked })
            Assert.NotEqual(DiagnosticOutcome.Succeeded, d.Outcome);
    }

    // ── 策略层：开始 / 恢复 ──

    /// <summary>开始/恢复：以既有 JobPhase 为准 —— 有错完成、暂停、中断都不是 Succeeded。</summary>
    [Fact]
    public void Fixture6_RunPhaseIsTheOnlyTruth()
    {
        Assert.Equal(DiagnosticOutcome.Succeeded, Run(JobPhase.Completed).Outcome);
        Assert.Equal(DiagnosticOutcome.Failed, Run(JobPhase.CompletedWithErrors).Outcome);
        Assert.Equal(DiagnosticOutcome.Accepted, Run(JobPhase.Paused).Outcome);
        Assert.Equal(DiagnosticOutcome.Canceled, Run(JobPhase.Interrupted).Outcome);
        Assert.Equal(DiagnosticOutcome.Canceled, Run(JobPhase.Canceled).Outcome);
        Assert.Equal(DiagnosticOutcome.Failed, Run(JobPhase.Failed).Outcome);
        Assert.Equal(DiagnosticOutcome.Unknown, Run(JobPhase.Created).Outcome);
        Assert.Equal(DiagnosticOutcome.Rejected,
            ActionOutcomePolicy.ForRun(new RunBusinessResult(JobPhase.Created, "run-scan-gate-blocked", null)).Outcome);
        Assert.Equal(DiagnosticOutcome.Failed,
            ActionOutcomePolicy.ForRun(new RunBusinessResult(JobPhase.Completed, null, "run-faulted")).Outcome);

        static ActionOutcomeDecision Run(JobPhase phase) =>
            ActionOutcomePolicy.ForRun(new RunBusinessResult(phase, null, null));
    }

    // ── 策略层：请求类动作（暂停 / 停止）──

    /// <summary>暂停/停止是**请求**语义：永不 Succeeded；未受理就是 Rejected。</summary>
    [Fact]
    public void Fixture6_PauseAndStopAreRequestsNeverSuccess()
    {
        var accepted = ActionOutcomePolicy.ForRequest(honored: true, "stop-requested", "stop-no-running-run");
        var rejected = ActionOutcomePolicy.ForRequest(honored: false, "stop-requested", "stop-no-running-run");

        Assert.Equal(DiagnosticOutcome.Accepted, accepted.Outcome);
        Assert.Equal("stop-requested", accepted.ReasonCode);
        Assert.Equal(DiagnosticOutcome.Rejected, rejected.Outcome);
        Assert.Equal("stop-no-running-run", rejected.ReasonCode);
        Assert.NotEqual(DiagnosticOutcome.Succeeded, accepted.Outcome);
        Assert.NotEqual(DiagnosticOutcome.Succeeded, rejected.Outcome);

        Assert.Equal(DiagnosticOutcome.Unknown, ActionOutcomePolicy.NotRun.Outcome);
        Assert.Equal("not-run", ActionOutcomePolicy.NotRun.ReasonCode);
    }

    // ── 会话层：真实 VM 分支（无任何任务）──

    /// <summary>
    /// 没有任何任务的会话（真实 <c>MigrationSessionViewModel</c>）里，五个动作**没有一个**能报 Succeeded。
    /// 这条直接打在旧缺陷上：旧写法在这五个分支里全部写
    /// <c>Complete(DiagnosticOutcome.Succeeded, "verify-returned" / "repair-returned" / "footer-*-returned")</c>。
    /// </summary>
    [Fact]
    public async Task Fixture6_SessionWithoutAnyTaskCannotReportSucceeded()
    {
        var vm = new MigrationSessionViewModel(null, null, new LoggerConfiguration().CreateLogger());

        Assert.Equal(ActionOutcomePolicy.NotRun.Outcome, vm.LastRunOutcome.Outcome);
        Assert.Equal(ActionOutcomePolicy.NotRun.Outcome, vm.LastVerifyOutcome.Outcome);

        await vm.RunAsync(null);
        Assert.Equal(new ActionOutcomeDecision(DiagnosticOutcome.Rejected, "run-no-task"), vm.LastRunOutcome);

        await vm.VerifyAsync();
        Assert.Equal(new ActionOutcomeDecision(DiagnosticOutcome.Rejected, "verify-no-task"), vm.LastVerifyOutcome);

        await vm.RepairAsync();
        Assert.Equal(new ActionOutcomeDecision(DiagnosticOutcome.Rejected, "repair-no-task"), vm.LastRepairOutcome);

        await vm.PauseAsync();
        Assert.Equal(new ActionOutcomeDecision(DiagnosticOutcome.Rejected, "pause-not-running"), vm.LastPauseOutcome);

        await vm.StopAsync();
        Assert.Equal(new ActionOutcomeDecision(DiagnosticOutcome.Skipped, "stop-no-running-run"), vm.LastStopOutcome);

        var outcomes = new[]
        {
            vm.LastRunOutcome, vm.LastVerifyOutcome, vm.LastRepairOutcome, vm.LastPauseOutcome, vm.LastStopOutcome,
        };
        foreach (var d in outcomes)
        {
            Assert.NotEqual(DiagnosticOutcome.Succeeded, d.Outcome);
            Assert.False(string.IsNullOrWhiteSpace(d.ReasonCode));
        }
        Assert.DoesNotContain("returned", string.Join('|', outcomes.Select(d => d.ReasonCode)), StringComparison.Ordinal);
    }

    // ── 源码契约：旧写法（"方法返回了 ⇒ Succeeded"）不得复活 ──

    /// <summary>
    /// 收口前 10 个调用点的 phase 串都是 <c>"xxx-returned"</c>。它们是"方法返回即成功"的化石：
    /// 只要再出现一次，就说明有人把动作终点又写死成 Succeeded 了。
    /// 同时锁定这些文件确实走了统一出口 <c>trace.Finish(...)</c>。
    /// </summary>
    [Fact]
    public void Fixture6_NoCallSiteClaimsSuccessFromAMethodReturn()
    {
        var winUi = Path.Combine(FindRepoRoot(), "src", "PCMig.WinUI");
        Assert.True(Directory.Exists(winUi), "缺少 WinUI 源码目录：" + winUi);

        var files = Directory.GetFiles(winUi, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("obj", ""), StringComparison.OrdinalIgnoreCase)
                     && !f.Contains(Path.Combine("bin", ""), StringComparison.OrdinalIgnoreCase))
            .ToList();

        // "xxx-returned" 只出现在旧缺陷点；出现即红灯。
        var offenders = new List<string>();
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            foreach (var probe in new[]
                     {
                         "\"verify-returned\"", "\"repair-returned\"", "\"run-returned\"",
                         "\"resume-returned\"", "\"pause-request-returned\"", "\"stop-request-returned\"",
                         "\"footer-start-returned\"", "\"footer-pause-returned\"",
                         "\"footer-stop-returned\"", "\"footer-resume-returned\"",
                     })
            {
                if (text.Contains(probe, StringComparison.Ordinal))
                    offenders.Add(Path.GetFileName(f) + " → " + probe);
            }
        }
        Assert.True(offenders.Count == 0, "仍有调用点把\"方法返回了\"当成功：" + string.Join("；", offenders));

        // 统一出口必须存在，且三个曾出错的文件都必须用它。
        var tracePath = Path.Combine(winUi, "Diagnostics", "ActionTrace.cs");
        Assert.Contains("public void Finish(PCMig.Core.Diagnostics.ActionOutcomeDecision decision", File.ReadAllText(tracePath), StringComparison.Ordinal);
        foreach (var rel in new[]
                 {
                     Path.Combine("Views", "Step2SelectDataPage.xaml.cs"),
                     Path.Combine("Views", "Step4ResultPage.xaml.cs"),
                     "MainWindow.xaml.cs",
                 })
        {
            var text = File.ReadAllText(Path.Combine(winUi, rel));
            Assert.Contains("trace.Finish(", text, StringComparison.Ordinal);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到仓库根（未在上级目录中发现 PCMig.sln）。");
    }
}