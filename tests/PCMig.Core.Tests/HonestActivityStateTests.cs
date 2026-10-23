using PCMig.Core.Transfer;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// v0.5.4 Preview.2 — P1-A 诚实状态句的**定向验收**（2026-10-15 返修）。
///
/// 背景（用户点名的验收缺口）：上一轮的 PT2_A1..A4 验证的是
/// <see cref="ProgressTruthSnapshot"/> / <see cref="WorkerIoProgressTracker"/> 的真值契约，
/// **没有**断言 UI 状态句本身，也没有覆盖相位优先级。因此"4 项测试通过"不等于"文案正确"。
///
/// 本组测试直接断言 <see cref="HonestActivityState"/> 的判据与**文案原文**，覆盖：
///   A. 运行中/未暂停/无来源 ⇒ 出现谨慎文案，且**不含**任何超出证据强度的断言；
///   B. 有可信来源 ⇒ 不误触发缺源文案；
///   C. 从未收到真值帧 ⇒ 不臆造缺源状态；
///   D. 暂停/中断/完成/失败 ⇒ 绝不显示"运行中"文案；
///   E. 文案随对象计数变化 ⇒ 证明文本是**派生**的（绑定层据此刷新）。
///
/// 分层说明：判据与文案是 Core 里的纯函数（<see cref="HonestActivityState"/>），
/// 因此可在现有 xUnit 项目直接断言；WinUI 侧只做"取结果 + 赋值"。
/// **不**为了可测性让测试项目引用 XAML 表示层。
/// </summary>
public class HonestActivityStateTests
{
    private const string ObjectText = "3/8（失败 1）";

    // ───────────────────────── A：出现谨慎文案，且不超出证据强度 ─────────────────────────

    /// <summary>
    /// A1：运行中 + 未暂停 + 有真值帧 + 无可信来源 ⇒ 应显示缺源文案。
    /// </summary>
    [Fact]
    public void A1_Running_PauseFalse_NoTrustedSource_ShowsUnavailableText()
    {
        var show = HonestActivityState.ShouldShowUnavailableProgress(
            isRunning: true, isPaused: false, hasTruthFrame: true, hasTrustedInflightSource: false);

        Assert.True(show, "运行中且无可信来源时应显示诚实缺源文案");
    }

    /// <summary>
    /// A2：文案必须**包含**的事实性内容（缺源 + 机制解释 + 把判断权交回用户）。
    /// </summary>
    [Fact]
    public void A2_Text_ContainsRequiredFacts()
    {
        var text = HonestActivityState.BuildUnavailableProgressText(ObjectText);

        Assert.Contains("运行中", text);
        Assert.Contains("暂无可确认的中途字节进度", text);
        Assert.Contains(ObjectText, text);                       // 对象计数如实带出
        Assert.Contains("预分配目标长度", text);                 // 机制解释（/Z /J 会预分配）
        Assert.Contains("请结合活动状态或诊断信息确认", text);   // 明确交回判断权
        Assert.Contains("可能", text);                           // "可能"更新，不是"一定"
    }

    /// <summary>
    /// A3（**核心反例**）：文案**不得**包含超出证据强度的肯定性声明。
    /// 这是上一轮被用户点名纠正的问题（"属正常现象，不是卡死" / "会一次性结算"）。
    /// </summary>
    [Fact]
    public void A3_Text_MustNotOverclaim()
    {
        var text = HonestActivityState.BuildUnavailableProgressText(ObjectText);

        // 1) 不得断言"不是卡死" —— 缺数据不能证明程序正常
        Assert.DoesNotContain("不是卡死", text);
        Assert.DoesNotContain("属正常现象", text);
        // 2) 不得无条件保证"一定会结算" —— 对象可能失败/中断/验证不通过
        Assert.DoesNotContain("一次性结算", text);
        Assert.DoesNotContain("保证", text);
        // 3) 不得由缺数据反推"仍在传输"
        Assert.DoesNotContain("仍在传输", text);
        Assert.DoesNotContain("正在传输", text);
        // 4) 不得给出任何伪造的增长承诺或速率换算
        Assert.DoesNotContain("MB/s", text);
        Assert.DoesNotContain("%", text);
    }

    // ───────────────────────── B：有可信来源 ⇒ 不误触发 ─────────────────────────

    /// <summary>
    /// B1：有可信中途来源（如进程 I/O 计数器）⇒ **不得**显示缺源文案。
    /// </summary>
    [Theory]
    [InlineData(true, false, true, true)]     // 运行中 + 有来源
    [InlineData(true, false, true, false)]    // 运行中 + 无来源 ⇒ 这个是唯一应显示的
    public void B1_TrustedSource_SuppressesUnavailableText(bool running, bool paused, bool hasFrame, bool hasSource)
    {
        var show = HonestActivityState.ShouldShowUnavailableProgress(running, paused, hasFrame, hasSource);

        if (hasSource)
        {
            Assert.False(show, "存在可信中途来源时绝不能显示『暂无中途来源』");
        }
        else
        {
            Assert.True(show);
        }
    }

    // ───────────────────────── C：从未收到真值帧 ⇒ 不臆造 ─────────────────────────

    /// <summary>
    /// C1：连一帧真值都没收到（启动前 / 测试路径）⇒ 不推断缺源状态。
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void C1_NoTruthFrame_NeverInventsState(bool running, bool paused)
    {
        var show = HonestActivityState.ShouldShowUnavailableProgress(
            isRunning: running, isPaused: paused, hasTruthFrame: false, hasTrustedInflightSource: false);

        Assert.False(show, "没有真值帧时不得臆造缺源状态");
    }

    // ───────────────────────── D：相位优先级 ─────────────────────────

    /// <summary>
    /// D1：非运行态（中断/完成/失败/取消）⇒ 绝不说"任务正在运行"。
    /// 这些相位各有专属文案（见 PageReadiness.Step3StateText 的四态区分）。
    /// </summary>
    [Theory]
    [InlineData(false, false)]   // 已中断/完成/失败/取消
    [InlineData(false, true)]
    public void D1_NotRunning_NeverShowsRunningText(bool running, bool paused)
    {
        var show = HonestActivityState.ShouldShowUnavailableProgress(
            isRunning: running, isPaused: paused, hasTruthFrame: true, hasTrustedInflightSource: false);

        Assert.False(show, "非运行态不得显示『任务状态为运行中』");
    }

    /// <summary>
    /// D2：已暂停 ⇒ 缺源文案必须让位给暂停专属文案（暂停有明确的"点恢复"引导）。
    /// </summary>
    [Fact]
    public void D2_Paused_TakesPrecedenceOverUnavailableText()
    {
        var show = HonestActivityState.ShouldShowUnavailableProgress(
            isRunning: true, isPaused: true, hasTruthFrame: true, hasTrustedInflightSource: false);

        Assert.False(show, "暂停时不得显示运行中的缺源文案");
    }

    // ───────────────────────── E：文案是派生的（绑定层刷新依据）─────────────────────────

    /// <summary>
    /// E1：文案随对象计数变化 ⇒ 它是**派生值**而非常量；
    /// 配合 PageReadiness 订阅的低频派生位 <c>HasTrustedInflightSource</c>，
    /// 保证值变化能被 PropertyChanged 传导到 TextBlock。
    /// </summary>
    [Fact]
    public void E1_Text_IsDerivedFromObjectCount()
    {
        var a = HonestActivityState.BuildUnavailableProgressText("1/8");
        var b = HonestActivityState.BuildUnavailableProgressText("5/8");

        Assert.NotEqual(a, b);
        Assert.Contains("1/8", a);
        Assert.Contains("5/8", b);
    }

    /// <summary>
    /// E2：判据本身是纯函数 —— 同样的输入恒得同样输出（无隐藏计时器/随机/全局状态）。
    /// 这条锁死"不得引入计时器插值或随机增长"。
    /// </summary>
    [Fact]
    public void E2_Predicate_IsPure_NoHiddenState()
    {
        for (var i = 0; i < 50; i++)
        {
            Assert.True(HonestActivityState.ShouldShowUnavailableProgress(true, false, true, false));
            Assert.False(HonestActivityState.ShouldShowUnavailableProgress(true, false, true, true));
        }

        // 文案同样纯：同一入参恒等
        Assert.Equal(
            HonestActivityState.BuildUnavailableProgressText("2/8"),
            HonestActivityState.BuildUnavailableProgressText("2/8"));
    }
}
