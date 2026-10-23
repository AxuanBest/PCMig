using PCMig.Core.Diagnostics;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ v0.5.4 Preview.2 真机修复回归（2026-10-16，用户任务令 §七 Navigation 组 + §五）★
///
/// 用户令 §五 的四条硬要求：
///   ① Step1 **完全不变**（连接成功绝不自动跳 Step2）；
///   ② Step2 点「开始迁移」⇒ 第一动作立即进 Step3（迁移后台继续）；
///   ③ 只有**真正完成**才自动 Step3 → Step4；
///   ④ 「真正完成」判据 = Phase==Completed ∧ 所有对象完成 ∧ failedObjects==0
///      ∧ missingObjects==0 ∧ completedBytes==totalBytes ∧ Percent==100，
///      **不能只判 percent >= 100**，且「100%」是完成条件的结果而不是触发条件。
///
/// 本组把③④钉死：跳转判据与导航原因码都必须是"可信完成"，而不是百分比。
/// </summary>
public class Preview2NavigationTests
{
    private static JobState State(JobPhase phase, int totalObj, int doneObj, int failed, long totalBytes, long doneBytes, int missing = 0)
        => new()
        {
            Phase = phase,
            TotalObjects = totalObj,
            CompletedObjects = doneObj,
            FailedObjects = failed,
            MissingObjects = missing,
            TotalBytes = totalBytes,
            CompletedBytes = doneBytes,
        };

    // ─────────────────────────────────────────────────────────────────────
    // §七-Navigation-③ true completion ⇒ may auto-advance to Step4
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>真完成的唯一形态：六项全成立 ⇒ 判据为真 ⇒ 允许自动进入 Step4。</summary>
    [Fact]
    public void PT2_N01_TrueCompletion_IsTrustworthy()
    {
        var s = State(JobPhase.Completed, 10, 10, 0, 119_491_120_745, 119_491_120_745);
        Assert.True(TransferOrchestrator.IsTrustworthyCompletion(s));
    }

    /// <summary>本机冒烟测试那种小规模真成功（3 对象 16 文件全 MATCH）同样为真。</summary>
    [Fact]
    public void PT2_N02_SmallFullSuccess_IsTrustworthy()
    {
        var s = State(JobPhase.Completed, 3, 3, 0, 34_967_176, 34_967_176);
        Assert.True(TransferOrchestrator.IsTrustworthyCompletion(s));
    }

    // ─────────────────────────────────────────────────────────────────────
    // §七-Navigation-④ CompletedWithErrors ⇒ 不跳 Step4
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 真机核心反例：即便 completedBytes 被旧口径抬到等于 totalBytes、Percent 显示 100，
    /// 只要 Phase 是 CompletedWithErrors 且有失败对象，**绝不允许**自动跳 Step4。
    /// 用户必须留在 Step3 看到"未完整完成"的真实状态。
    /// </summary>
    [Fact]
    public void PT2_N03_RealDeviceCompletedWithErrors_MustNotAdvance()
    {
        var s = State(JobPhase.CompletedWithErrors, 10, 9, 1, 119_491_120_745, 119_491_120_745);

        Assert.False(TransferOrchestrator.IsTrustworthyCompletion(s),
            "真机终态绝不能被判为完成 —— 否则用户看不到失败就被送到了 Step4。");
        // 同时确认旧口径确实会给出 100%（这正是危险所在）。
        Assert.True(TransferOrchestrator.Percent(s) < 100.0);
    }

    // ─────────────────────────────────────────────────────────────────────
    // §七-Navigation-⑤ Missing ⇒ 不跳 Step4
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 迁移期"成功"但校验期发现目标侧缺文件的情形：FailedObjects 可能为 0，MissingObjects &gt; 0。
    /// 这种"看起来完成、实际缺件"的形态**更不能**自动跳 Step4。
    /// </summary>
    [Fact]
    public void PT2_N04_MissingObjects_MustNotAdvance()
    {
        var s = State(JobPhase.Completed, 10, 10, 0, 1000, 1000, missing: 1);

        Assert.False(TransferOrchestrator.IsTrustworthyCompletion(s));
    }

    // ─────────────────────────────────────────────────────────────────────
    // §七-Navigation-⑥ 99.9% ⇒ 不跳 Step4
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>字节没闭环（哪怕只差 1 B）⇒ 不跳。防止"四舍五入到 100 就开始跳"这类口径。</summary>
    [Fact]
    public void PT2_N05_BytesOffByOne_MustNotAdvance()
    {
        var s = State(JobPhase.Completed, 10, 10, 0, 1000, 999);
        Assert.False(TransferOrchestrator.IsTrustworthyCompletion(s));
    }

    /// <summary>运行中（尚未收尾）绝不可判为完成 —— 真机上用户在 Step3 看着的就是这个阶段。</summary>
    [Fact]
    public void PT2_N06_Running_MustNotAdvance()
    {
        var s = State(JobPhase.Running, 10, 9, 0, 119_491_120_745, 118_000_000_000);
        Assert.False(TransferOrchestrator.IsTrustworthyCompletion(s));
    }

    /// <summary>暂停中同样不跳（Phase==Paused 不是 Completed）。</summary>
    [Fact]
    public void PT2_N07_Paused_MustNotAdvance()
    {
        var s = State(JobPhase.Paused, 10, 9, 0, 1000, 900);
        Assert.False(TransferOrchestrator.IsTrustworthyCompletion(s));
    }

    // ─────────────────────────────────────────────────────────────────────
    // 导航原因码：自动跳转必须可取证、且与"用户自己翻页"可区分
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 本次新增的两个原因码必须进入**封闭集合**，否则会被 Normalize 降级成 unspecified，
    /// 取证时看不出"是业务动作把用户带过去的"还是"用户自己翻的页"。
    /// </summary>
    [Fact]
    public void PT2_N08_NewReasonCodes_AreRegisteredInClosedSet()
    {
        Assert.Contains(NavigationReasons.TransferStarted, NavigationReasons.All);
        Assert.Contains(NavigationReasons.TransferCompleted, NavigationReasons.All);
    }

    [Fact]
    public void PT2_N09_NewReasonCodes_SurviveNormalize()
    {
        Assert.Equal(NavigationReasons.TransferStarted, NavigationReasons.Normalize(NavigationReasons.TransferStarted));
        Assert.Equal(NavigationReasons.TransferCompleted, NavigationReasons.Normalize(NavigationReasons.TransferCompleted));
    }

    /// <summary>自动跳转不得与用户翻页混为一谈（这两组必须互不相同）。</summary>
    [Fact]
    public void PT2_N10_AutoAdvanceIsDistinctFromUserNavigation()
    {
        Assert.NotEqual(NavigationReasons.TransferStarted, NavigationReasons.FooterNext);
        Assert.NotEqual(NavigationReasons.TransferStarted, NavigationReasons.KeyboardNext);
        Assert.NotEqual(NavigationReasons.TransferCompleted, NavigationReasons.FooterNext);
        Assert.NotEqual(NavigationReasons.TransferCompleted, NavigationReasons.KeyboardNext);
        Assert.NotEqual(NavigationReasons.TransferCompleted, NavigationReasons.RailClick);
    }

    /// <summary>未知原因码仍被降级为 unspecified（不因为新增了两条就放松未知处理）。</summary>
    [Fact]
    public void PT2_N11_UnknownReason_StillNormalizesToUnspecified()
    {
        Assert.Equal(NavigationReasons.Unspecified, NavigationReasons.Normalize("something-made-up"));
        Assert.Equal(NavigationReasons.Unspecified, NavigationReasons.Normalize(""));
        Assert.Equal(NavigationReasons.Unspecified, NavigationReasons.Normalize(null));
    }

    /// <summary>原因码集合不得出现重复（重复会让契约测试与文档失真）。</summary>
    [Fact]
    public void PT2_N12_ReasonSetHasNoDuplicates()
    {
        var all = NavigationReasons.All;
        Assert.Equal(all.Count, System.Linq.Enumerable.Distinct(all).Count());
        Assert.DoesNotContain(all, r => string.IsNullOrWhiteSpace(r));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Step1 行为不变（用户令 §五 ①）：本组没有任何"连接成功即跳页"的原因码
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 守卫用例：导航原因码集合里**不存在**任何"连接成功"语义的自动跳转原因。
    /// Step1 ⇒ Step2 只能由用户动作（rail-click / keyboard-* / footer-next / qa-probe）触发。
    /// </summary>
    [Fact]
    public void PT2_N13_NoConnectDrivenAutoAdvanceReasonExists()
    {
        Assert.DoesNotContain(NavigationReasons.All,
            r => r.Contains("connect", System.StringComparison.OrdinalIgnoreCase));
    }
}
