using System;
using System.Collections.Generic;
using System.Linq;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ v0.5.4 Preview.2（Round3 审查返修，2026-10-15）★ Step 3 状态文案**真实分支选择**回归。
///
/// 【本文件要解决的审查意见】
/// 审查指出：<c>HonestActivityStateTests</c> 的 13 个用例<b>只证明纯函数正确</b>，
/// <b>不证明</b> <c>PageReadiness.Step3StateText</c> 的条件顺序正确 —— 事实上返修前
/// 真实页面上 A 类缺源句的分支<b>不可达</b>（被更早的"无条件 IsRunning"覆盖），
/// 13 个用例却全绿。因此本轮把<b>分支顺序本身</b>也移进 Core 的
/// <see cref="Step3StateSelector.Select"/>，生产代码（PageReadiness）与测试
/// <b>共用同一个选择器</b>——这里断言的就是页面实际会走的那条链，不是副本。
///
/// 【与纯函数测试的关系】
///   · <c>HonestActivityStateTests</c>：证明"判据与文案"本身正确（保留，不删）；
///   · <b>本文件</b>：证明"这些判据在真实分支顺序里<b>真的被求值到了</b>"。
///   两者都通过，才允许声称 UI CODE VERIFIED。
///
/// 【运行级别：L1 逻辑级（真实选择器）+ L0 源码契约】
///   断言的是真实选择器的返回值；另外用源码契约锁住 PageReadiness <b>确实调用了它</b>
///   （否则有人可以把调用点改回内联链，测试仍绿 —— 这正是本次缺陷的成因）。
/// </summary>
public sealed class PageReadinessStep3BranchTests
{
    // ── 构造输入的小工具（只填关心的字段，其余给安全的默认值）────────────────
    private static Step3StateSelector.Step3Input Input(
        bool hasSession = true,
        bool isConnected = true,
        bool isRunning = false,
        bool isPaused = false,
        JobPhase phase = JobPhase.Created,
        bool hasTruthFrame = false,
        bool hasTrustedInflightSource = false,
        string objectText = "3/8",
        string progressText = "1.2 GB / 3.4 GB",
        double percent = 35.0,
        int failedObjects = 0,
        string phaseText = "运行中",
        string planBytesText = "3.4 GB",
        bool canStart = true,
        bool hasPlan = true,
        bool hasJob = true,
        string jobIdText = "JOB-TEST") =>
        new(hasSession, isConnected, isRunning, isPaused, phase,
            hasTruthFrame, hasTrustedInflightSource,
            objectText, progressText, percent, failedObjects,
            phaseText, planBytesText, canStart, hasPlan, hasJob, jobIdText);

    /// <summary>A 类缺源句的可识别片段（判据要求：必须来自 Core 的同一常量，不另抄一份）。</summary>
    private static string UnavailableFragment =>
        HonestActivityState.BuildUnavailableProgressText("3/8");

    // ══════════════════════════════════════════════════════════════════════════
    // 1. Running + 无来源 + 有真值帧 ⇒ 必须真的输出缺源句（返修前此路不可达）
    // ══════════════════════════════════════════════════════════════════════════
    [Fact]
    public void R1_Running_NoSource_WithTruthFrame_ActuallyReturnsUnavailableText()
    {
        var text = Step3StateSelector.Select(Input(
            isRunning: true, isPaused: false,
            hasTruthFrame: true, hasTrustedInflightSource: false));

        Assert.Contains("暂无可确认的中途字节进度", text, StringComparison.Ordinal);
        Assert.Equal(UnavailableFragment, text);
    }

    // 补充：逐字段确认它与"常规进行中"确实不是同一句（防止把两句写成一样）
    [Fact]
    public void R1b_UnavailableText_IsNotTheOrdinaryRunningText()
    {
        var unavailable = Step3StateSelector.Select(Input(
            isRunning: true, hasTruthFrame: true, hasTrustedInflightSource: false));
        var ordinary = Step3StateSelector.Select(Input(
            isRunning: true, hasTruthFrame: true, hasTrustedInflightSource: true));

        Assert.NotEqual(ordinary, unavailable);
        Assert.Contains("迁移进行中", ordinary, StringComparison.Ordinal);
        Assert.DoesNotContain("暂无可确认的中途字节进度", ordinary, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 2. Running + 有可信来源 ⇒ 不出现缺源文案
    // ══════════════════════════════════════════════════════════════════════════
    [Fact]
    public void R2_Running_WithTrustedSource_NeverShowsUnavailableText()
    {
        var text = Step3StateSelector.Select(Input(
            isRunning: true, hasTruthFrame: true, hasTrustedInflightSource: true));

        Assert.DoesNotContain("暂无可确认的中途字节进度", text, StringComparison.Ordinal);
        Assert.Contains("迁移进行中", text, StringComparison.Ordinal);
        Assert.Contains("35.0%", text, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 3. Running + 无真值帧 ⇒ 不得臆造"进度来源缺失"
    // ══════════════════════════════════════════════════════════════════════════
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void R3_Running_NoTruthFrame_NeverInventsUnavailableState(bool hasTrustedSource)
    {
        var text = Step3StateSelector.Select(Input(
            isRunning: true, hasTruthFrame: false, hasTrustedInflightSource: hasTrustedSource));

        Assert.DoesNotContain("暂无可确认的中途字节进度", text, StringComparison.Ordinal);
        Assert.Contains("迁移进行中", text, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 4. Paused ⇒ 暂停优先于"运行中"，也优先于"缺源"
    //    ⚠ InlineData 的参数顺序必须与下方签名严格一致：(isRunning, isPaused, hasTruthFrame, hasTrustedSource)
    //      此前写成 (isRunning, hasTrustedSource, hasTruthFrame, isPaused) 导致 3 个用例的数据被错位解读
    //      ——这正是"用例名字对了不等于数据对了"，已按签名顺序改正。
    // ══════════════════════════════════════════════════════════════════════════
    [Theory]
    // 运行中 + 已暂停 + 有真值帧 + 无来源 ⇒ 暂停优先（不得落到缺源句）
    [InlineData(true, true, true, false)]
    // IsRunning 已落回 false + Paused + 无真值帧 + 无来源 ⇒ 仍走 Phase==Paused 的"已暂停"
    [InlineData(false, true, false, false)]
    // 运行中 + 已暂停 + 有真值帧 + 有来源 ⇒ 仍是"已暂停"
    [InlineData(true, true, true, true)]
    public void R4_Paused_TakesPrecedenceOverRunningAndUnavailable(
        bool isRunning, bool isPaused, bool hasTruthFrame, bool hasTrustedSource)
    {
        var text = Step3StateSelector.Select(Input(
            isRunning: isRunning, isPaused: isPaused,
            phase: JobPhase.Paused,
            hasTruthFrame: hasTruthFrame, hasTrustedInflightSource: hasTrustedSource));

        Assert.StartsWith("已暂停：", text, StringComparison.Ordinal);
        Assert.DoesNotContain("暂无可确认的中途字节进度", text, StringComparison.Ordinal);
        Assert.DoesNotContain("迁移进行中", text, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 5. Interrupted / Canceled / Completed / CompletedWithErrors ⇒ 专属状态优先
    //    关键：即使 (IsRunning=false) 而某处若误把 IsRunning 当 true，也必须在
    //    这些阶段下拿到专属文案（本轮修的就是"被运行中覆盖"）
    // ══════════════════════════════════════════════════════════════════════════
    [Fact]
    public void R5a_Interrupted_TakesPrecedence()
    {
        var text = Step3StateSelector.Select(Input(
            isRunning: false, phase: JobPhase.Interrupted,
            hasTruthFrame: true, hasTrustedInflightSource: false));

        Assert.StartsWith("已中断（可续传）：", text, StringComparison.Ordinal);
        Assert.Contains("恢复任务", text, StringComparison.Ordinal);
        Assert.DoesNotContain("迁移进行中", text, StringComparison.Ordinal);
        Assert.DoesNotContain("暂无可确认的中途字节进度", text, StringComparison.Ordinal);
    }

    [Fact]
    public void R5b_Canceled_TakesPrecedence_AndNeverSuggestsResume()
    {
        var text = Step3StateSelector.Select(Input(
            isRunning: false, phase: JobPhase.Canceled));

        Assert.StartsWith("已取消：", text, StringComparison.Ordinal);
        // Canceled 的 CanResume=false ⇒ 绝不能引导"恢复任务"（既有纪律）
        Assert.DoesNotContain("恢复任务", text, StringComparison.Ordinal);
        Assert.DoesNotContain("迁移进行中", text, StringComparison.Ordinal);
    }

    [Fact]
    public void R5c_Completed_TakesPrecedence()
    {
        var text = Step3StateSelector.Select(Input(
            isRunning: false, phase: JobPhase.Completed, phaseText: "已完成"));

        Assert.StartsWith("迁移已完成", text, StringComparison.Ordinal);
        Assert.DoesNotContain("迁移进行中", text, StringComparison.Ordinal);
        Assert.DoesNotContain("暂无可确认的中途字节进度", text, StringComparison.Ordinal);
    }

    [Fact]
    public void R5d_CompletedWithErrors_TakesPrecedence_AndStaysHonest()
    {
        var text = Step3StateSelector.Select(Input(
            isRunning: false, phase: JobPhase.CompletedWithErrors,
            phaseText: "完成但有错误", failedObjects: 1));

        Assert.StartsWith("迁移**未完整完成**", text, StringComparison.Ordinal);
        Assert.DoesNotContain("迁移进行中", text, StringComparison.Ordinal);
        Assert.DoesNotContain("暂无可确认的中途字节进度", text, StringComparison.Ordinal);
    }

    // ★ 反例核心：这四个阶段下，即使把 IsRunning 强行置 true（模拟"被运行中覆盖"的场景），
    //   Completed / CompletedWithErrors 仍必须走专属句 —— 因为真实世界里 IsRunning 可能
    //   因为引擎收尾时序与 Phase 短暂不一致。这里锁定"专属状态优先"的顺序契约。
    [Theory]
    [InlineData(JobPhase.Completed, "迁移已完成")]
    [InlineData(JobPhase.CompletedWithErrors, "迁移**未完整完成**")]
    public void R5e_TerminalPhase_WinsEvenIfIsRunningStillTrue(JobPhase phase, string expectedPrefix)
    {
        var text = Step3StateSelector.Select(Input(
            isRunning: true, isPaused: false, phase: phase,
            hasTruthFrame: true, hasTrustedInflightSource: false));

        Assert.StartsWith(expectedPrefix, text, StringComparison.Ordinal);
        Assert.DoesNotContain("暂无可确认的中途字节进度", text, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 6. 切换往返：有来源 ⇄ 无来源 必须使文案重新求值（不是被固化）
    // ══════════════════════════════════════════════════════════════════════════
    [Fact]
    public void R6_TogglingSource_FliptextBackAndForth()
    {
        var noSource = Input(isRunning: true, hasTruthFrame: true, hasTrustedInflightSource: false);
        var withSource = Input(isRunning: true, hasTruthFrame: true, hasTrustedInflightSource: true);

        var t1 = Step3StateSelector.Select(noSource);
        var t2 = Step3StateSelector.Select(withSource);
        var t3 = Step3StateSelector.Select(noSource);
        var t4 = Step3StateSelector.Select(withSource);

        Assert.Equal(t1, t3);                       // 无来源 ⇒ 缺源句（稳定）
        Assert.Equal(t2, t4);                       // 有来源 ⇒ 常规句（稳定）
        Assert.NotEqual(t1, t2);                    // 两者确实不同
        Assert.Contains("暂无可确认的中途字节进度", t3, StringComparison.Ordinal);
        Assert.DoesNotContain("暂无可确认的中途字节进度", t4, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 7. 不得凭"缺源"就断言"不是卡死"/"仍在传输"
    //    这是 HonestActivityState 的证据强度纪律，在真实分支输出上再验一次
    // ══════════════════════════════════════════════════════════════════════════
    [Fact]
    public void R7_UnavailableText_MustNotOverclaimOnTheRealBranch()
    {
        var text = Step3StateSelector.Select(Input(
            isRunning: true, hasTruthFrame: true, hasTrustedInflightSource: false));

        // 禁止的过度断言（正反两种写法都要挡）
        Assert.DoesNotContain("不是卡死", text, StringComparison.Ordinal);
        Assert.DoesNotContain("没有卡死", text, StringComparison.Ordinal);
        Assert.DoesNotContain("仍在传输", text, StringComparison.Ordinal);
        Assert.DoesNotContain("正在传输", text, StringComparison.Ordinal);
        Assert.DoesNotContain("一定会", text, StringComparison.Ordinal);
        Assert.DoesNotContain("保证", text, StringComparison.Ordinal);
        // 必须把判断权交回用户
        Assert.Contains("请结合活动状态或诊断信息确认", text, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 8. 会话缺失 / 计划 / 任务 / 兜底 分支保持原样（防回归）
    // ══════════════════════════════════════════════════════════════════════════
    [Fact]
    public void R8a_NoSession_FallsBackToGuidance()
    {
        Assert.Equal(Step3StateSelector.NoSessionConnectedText,
            Step3StateSelector.Select(Input(hasSession: false, isConnected: true)));
        Assert.Equal(Step3StateSelector.NoSessionDisconnectedText,
            Step3StateSelector.Select(Input(hasSession: false, isConnected: false)));
    }

    [Fact]
    public void R8b_IdleWithPlan_ShowsPlanReady()
    {
        var text = Step3StateSelector.Select(Input(
            isRunning: false, phase: JobPhase.Created, hasPlan: true, canStart: true));

        Assert.StartsWith("计划已就绪：", text, StringComparison.Ordinal);
    }

    [Fact]
    public void R8c_IdleWithJobNoPlan_ShowsJobLoaded()
    {
        var text = Step3StateSelector.Select(Input(
            isRunning: false, phase: JobPhase.Created, hasPlan: false, hasJob: true));

        Assert.StartsWith("任务 JOB-TEST 已载入", text, StringComparison.Ordinal);
    }

    [Fact]
    public void R8d_NothingAtAll_ShowsFallback()
    {
        Assert.Equal(Step3StateSelector.NothingYetText,
            Step3StateSelector.Select(Input(
                isRunning: false, phase: JobPhase.Created, hasPlan: false, hasJob: false)));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 9. 纯函数性：同输入同输出，无隐藏状态
    // ══════════════════════════════════════════════════════════════════════════
    [Fact]
    public void R9_Selector_IsPure()
    {
        var input = Input(isRunning: true, hasTruthFrame: true, hasTrustedInflightSource: false);
        Assert.Equal(Step3StateSelector.Select(input), Step3StateSelector.Select(input));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 10. ★ 源码契约：PageReadiness 必须**调用**这个选择器，不得把分支链改回内联
    //      （本次缺陷的成因正是"调用点自有一套顺序"；纯逻辑测试拦不住它）
    // ══════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// 只截取 <c>Step3StateText</c> 属性体（到下一个属性声明或文件末为止）。
    ///
    /// 【为什么必须限定范围】R10 的负向断言最初扫的是<b>整个文件</b>，结果误报到
    /// <c>Step4StateText</c> —— Step 4 出于自己的理由也使用「迁移**未完整完成**」这句
    /// （它要引导用户去本页看失败清单）。那处内联<b>不是</b>本次缺陷，也不该被本测试禁止。
    /// 契约断言必须只覆盖被修的那一个属性，否则会变成"为了绿而改无关代码"。
    /// </summary>
    private static string ReadStep3StateTextBody()
    {
        var src = ReadPageReadiness();
        var marker = "public string Step3StateText";
        var start = src.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "PageReadiness.cs 里找不到 Step3StateText 属性声明");

        // 属性体结束于下一个 "public string Step4StateText"（本文件里紧随其后）
        var next = src.IndexOf("public string Step4StateText", start, StringComparison.Ordinal);
        Assert.True(next > start, "找不到 Step3StateText 的结束边界（Step4StateText）");

        return src.Substring(start, next - start);
    }

    [Fact]
    public void R10_PageReadiness_MustDelegateToTheSharedSelector()
    {
        var src = ReadPageReadiness();

        Assert.Contains("Step3StateSelector.Select(", src, StringComparison.Ordinal);
        Assert.Contains("Step3StateSelector.Step3Input", src, StringComparison.Ordinal);

        // 负向：Step3StateText 体内不得再自行拼装这些分支文案（否则内联链又回来了）。
        //   范围**只限该属性**，不扫全文件（见 ReadStep3StateTextBody 的说明）。
        var body = ReadStep3StateTextBody();
        foreach (var forbidden in new[]
                 {
                     "已中断（可续传）：",     // Interrupted 分支
                     "迁移已完成（",           // Completed 分支
                     "已取消：",               // Canceled 分支
                     "计划已就绪：",           // HasPlan 分支
                     "已暂停：",               // Paused 分支
                     "任务状态为运行中",       // 缺源句内联
                 })
        {
            Assert.False(body.Contains(forbidden, StringComparison.Ordinal),
                $"Step3StateText 不应再内联分支文案「{forbidden}」——应交给 Step3StateSelector；" +
                "内联即意味着分支顺序又会与测试脱钩。");
        }
    }

    /// <summary>源码契约：PageReadiness 不得再保留已被证明不可达的私有判据。</summary>
    [Fact]
    public void R11_PageReadiness_MustNotKeepDeadUnreachableHelper()
    {
        var src = ReadPageReadiness();
        Assert.False(src.Contains("IsProgressBytesUnavailable(", StringComparison.Ordinal),
            "旧的私有判据已随不可达分支一并移入 Step3StateSelector，不应残留。");
    }

    private static string ReadPageReadiness()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(
                dir.FullName, "src", "PCMig.WinUI", "Presentation", "PageReadiness.cs");
            if (System.IO.File.Exists(candidate)) return System.IO.File.ReadAllText(candidate);
        }
        throw new InvalidOperationException(
            "找不到 PageReadiness.cs（自 " + AppContext.BaseDirectory + " 向上 12 层未命中）");
    }
}
