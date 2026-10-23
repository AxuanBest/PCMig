using PCMig.Core.Models;

namespace PCMig.Core.Transfer;

/// <summary>
/// ★ v0.5.4 Preview.2（Round3 审查返修，2026-10-15）★ Step 3 状态文案的**分支选择器**（纯函数）。
///
/// 【为什么存在 —— 这是一个真实缺陷的修复】
/// 返修前，分支选择写在 WinUI 的 <c>PageReadiness.Step3StateText</c> 里，而该属性是一条长三元链，
/// 顺序为：
/// <code>
///   … : _session.IsRunning &amp;&amp; _session.IsPaused      // ← A
///     : _session.IsRunning                               // ← B ★ 无条件 IsRunning
///     : … Completed / CompletedWithErrors / Paused / Interrupted / Canceled …
///     : _session.IsRunning &amp;&amp; !_session.IsPaused &amp;&amp; IsProgressBytesUnavailable(...) // ← C
///     : HasPlan / HasJob / 兜底
/// </code>
/// <b>分支 C 永远不可达</b>：它要求 <c>IsRunning == true</c>，而该条件已被更早的分支 B 完全覆盖
/// （B 只要 IsRunning 为真就直接返回"迁移进行中"）。于是 A 类诚实状态句在实际界面上
/// <b>一次也不会显示</b>，而 <c>HonestActivityStateTests</c> 的 13 个用例全绿 —— 因为它们只测纯函数，
/// 测不到调用点的<em>条件顺序</em>。这正是"测试通过 ≠ 功能生效"的典型样本。
///
/// 【本类型的职责与边界】
///   · 只做**分支选择与文案拼装**，不读文件系统、不读时钟、不做插值、不发明百分比；
///   · 输入是调用方从真实会话状态取来的**标量快照**（见 <see cref="Step3Input"/>），
///     因此可以在无 WinUI 宿主的 xUnit 里直接断言**真实选择结果**；
///   · UI 侧（PageReadiness）只做"采集标量 + 调用本选择器 + 赋值"，
///     <b>不得</b>再自行维护一份分支顺序 —— 否则又会出现"两份口径"。
///
/// 【优先级契约（返修后，按此顺序短路）】
///   1. 会话缺失 ⇒ 引导态（就绪 / 尚未开始），不臆造迁移状态；
///   2. <b>非正常运行态优先</b>：Paused / Interrupted / Canceled / Completed / CompletedWithErrors
///      一律走它们各自的真实文案，<b>不得</b>被"运行中"或"缺源"覆盖；暂停另有一条覆盖
///      （<c>IsRunning &amp;&amp; IsPaused</c>，"已暂停"不是"迁移进行中"）；
///   3. 真正运行中 <b>且有真值帧但无可信在途字节来源</b> ⇒ 诚实缺源句
///      （<see cref="HonestActivityState.BuildUnavailableProgressText"/>）；
///   4. 真正运行中且有可信来源 ⇒ 常规"迁移进行中"；
///   5. 没有真值帧 ⇒ 沿用常规"迁移进行中"，<b>不得</b>凭缺帧臆造"进度来源缺失"；
///   6. 计划 / 任务 / 兜底。
/// </summary>
public static class Step3StateSelector
{
    /// <summary>
    /// Step 3 状态文案选择所需的**全部输入**（标量快照）。
    ///
    /// 刻意只接受标量而不接受整个 ViewModel：这样选择器保持纯净、可测，
    /// 且 WinUI 侧无法偷偷从 ViewModel 再读一个未登记的字段来改变分支。
    /// </summary>
    /// <param name="HasSession">是否存在迁移会话（false ⇒ 走引导态）。</param>
    /// <param name="IsConnected">Step 1 是否已连接（仅 <paramref name="HasSession"/> 为 false 时使用）。</param>
    /// <param name="IsRunning">引擎真值：会话是否运行中。</param>
    /// <param name="IsPaused">是否已暂停。</param>
    /// <param name="Phase">任务阶段（Completed / CompletedWithErrors / Paused / Interrupted / Canceled 等）。</param>
    /// <param name="HasTruthFrame">是否已收到至少一帧真值（<c>LastTruth is not null</c>）。</param>
    /// <param name="HasTrustedInflightSource">
    /// 本帧是否存在**已确认的中途字节来源**（来源非 None 且已确认字节 &gt; 0）。</param>
    /// <param name="ObjectText">"已完成/总数（失败 N）"形式的对象计数文本。</param>
    /// <param name="ProgressText">进度文本（如 "1.2 GB / 3.4 GB"）。</param>
    /// <param name="Percent">百分比（仅用于显示；本选择器不改写它）。</param>
    /// <param name="FailedObjects">失败对象数。</param>
    /// <param name="PhaseText">阶段显示文本。</param>
    /// <param name="PlanBytesText">计划总量文本（HasPlan 分支使用）。</param>
    /// <param name="CanStart">当前是否可点「开始迁移」。</param>
    /// <param name="HasPlan">是否已有可执行计划。</param>
    /// <param name="HasJob">是否已载入任务。</param>
    /// <param name="JobIdText">任务号显示文本。</param>
    public readonly record struct Step3Input(
        bool HasSession,
        bool IsConnected,
        bool IsRunning,
        bool IsPaused,
        JobPhase Phase,
        bool HasTruthFrame,
        bool HasTrustedInflightSource,
        string ObjectText,
        string ProgressText,
        double Percent,
        int FailedObjects,
        string PhaseText,
        string PlanBytesText,
        bool CanStart,
        bool HasPlan,
        bool HasJob,
        string JobIdText);

    /// <summary>会话缺失时的引导文案（原实现内联在 PageReadiness，此处原样迁入）。</summary>
    public const string NoSessionConnectedText = "已就绪：可在 Step 2 勾选内容后开始迁移。";

    /// <summary>会话缺失且未连接时的引导文案。</summary>
    public const string NoSessionDisconnectedText = "尚未开始迁移（需要先完成 Step 1 连接与 Step 2 选择）。";

    /// <summary>既无计划也无任务时的兜底文案。</summary>
    public const string NothingYetText = "尚未开始迁移（需要先完成 Step 1 连接与 Step 2 选择并生成计划）。";

    /// <summary>
    /// 选出 Step 3 应当显示的状态文案。纯函数：同输入必得同输出，无隐藏状态。
    /// 优先级契约见类型注释。
    /// </summary>
    public static string Select(Step3Input s)
    {
        // ── 1. 会话缺失 ⇒ 引导态 ─────────────────────────────────────────────
        if (!s.HasSession)
            return s.IsConnected ? NoSessionConnectedText : NoSessionDisconnectedText;

        // ── 2a. 运行中且已暂停 ⇒ "已暂停"优先于"迁移进行中" ──────────────────
        //      （暂停时 IsRunning 仍为 true，但语义不是"正在进行"）
        if (s.IsRunning && s.IsPaused)
            return $"已暂停：{s.ObjectText} 个对象，{s.ProgressText}——点「恢复任务」可续传（已完成的对象不会重传）。";

        // ── 2b. 专属状态优先：一律压过"运行中"与"缺源" ───────────────────────
        //      ★ 这里**不**用 `if (!s.IsRunning)` 做门 —— 本轮返修中曾这样写，被 R5e 用例当场抓住：
        //        引擎收尾存在 IsRunning 与 Phase 短暂不一致的窗口（Phase 已是 Completed /
        //        CompletedWithErrors，IsRunning 尚未落回 false）。若用 !IsRunning 当门，这段窗口
        //        就会落到"运行中"分支，把**已经结束且带着失败**的任务显示成"迁移进行中"，
        //        甚至叠加缺源句 —— 与"终止态必须用自己的真实文案"直接冲突。
        //      改为**无条件**先看 Phase：终态与暂停态压过运行态判断。
        //
        //      四态可续传性不同，引导必须不同：
        //        · Paused      ⇒ CanResume=true  → 引导「恢复任务」
        //        · Interrupted ⇒ CanResume=true  → 引导「恢复任务」
        //        · Canceled    ⇒ CanResume=false → **绝不能**引导"恢复任务"
        if (s.Phase == JobPhase.Completed)
            return $"迁移已完成（{s.PhaseText}）：{s.ProgressText}。可到 Step 4 做基础一致性检查并查看报告。";

        if (s.Phase == JobPhase.CompletedWithErrors)
            return $"迁移**未完整完成**（{s.PhaseText}）：实际完成 {s.Percent:0.0}%（{s.ProgressText}）。" +
                   "有失败对象未补齐，可到 Step 4 看失败清单、点「尝试修复」或「恢复任务」补齐（已完成的对象不会重传）。";

        if (s.Phase == JobPhase.Paused)
            return $"已暂停：{s.ObjectText} 个对象，{s.ProgressText}——点「恢复任务」可续传（已完成的对象不会重传）。";

        if (s.Phase == JobPhase.Interrupted)
            return $"已中断（可续传）：{s.ObjectText} 个对象，{s.ProgressText}——点「恢复任务」续传，已完成的对象不会重传。";

        if (s.Phase == JobPhase.Canceled)
            return $"已取消：{s.ObjectText} 个对象，{s.ProgressText}。可回 Step 2 重新「开始迁移」。";

        // ── 3. ★ 真正运行中：先判"有没有可信在途字节来源"，再决定常规句还是诚实缺源句 ──
        //      这里不再有"无条件的 IsRunning 常规句"挡在前面 —— 那正是返修前的缺陷。
        if (s.IsRunning)
        {
            var unavailable = HonestActivityState.ShouldShowUnavailableProgress(
                isRunning: s.IsRunning,
                isPaused: s.IsPaused,
                hasTruthFrame: s.HasTruthFrame,
                hasTrustedInflightSource: s.HasTrustedInflightSource);

            if (unavailable)
                return HonestActivityState.BuildUnavailableProgressText(s.ObjectText);

            // 有可信来源，或**尚未收到任何真值帧** ⇒ 常规"迁移进行中"。
            //   没有真值帧时**不得**臆造"进度来源缺失"（判据要求 hasTruthFrame）。
            return $"迁移进行中：{s.ProgressText}（{s.Percent:0.0}%），对象 {s.ObjectText}" +
                   (s.FailedObjects > 0 ? $"，失败 {s.FailedObjects} 个。" : "。");
        }

        // ── 6. 非运行、无专属阶段 ⇒ 计划 / 任务 / 兜底 ────────────────────────
        if (s.HasPlan)
            return $"计划已就绪：{s.ObjectText} 个对象，共 {s.PlanBytesText}。" +
                   (s.CanStart ? "点「开始迁移」即可执行。" : "请先在 Step 2 指定目标路径。");

        if (s.HasJob)
            return $"任务 {s.JobIdText} 已载入（{s.PhaseText}），但还没有可执行的计划：请回 Step 2 预检并生成计划。";

        return NothingYetText;
    }
}
