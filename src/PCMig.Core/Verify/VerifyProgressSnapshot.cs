// ★ PCMig v0.5.3 Preview.2 ★ 验证进度的**结构化**真值（P1-12 / 验收 R9）
//
// 起因：验证此前只有一个 `IProgress<string>` 通道（"验证 object-000003 …" 这样的文本），
// 因此界面**无法**显示真实计数，只能给一个转圈动画 —— 「验证无独立二级窗口与真实进度」。
//
// 本类型只承载**已发生的事实**：已完成的对象数、累计核对过的源侧文件数/字节数、各状态计数。
// 三条硬约束：
//   ① 所有计数都来自已经算完的 <c>ObjectVerifyResult</c>（累计和），**不含任何估算/外推**；
//   ② 没有快照到达时，界面必须显示"等待引擎回报"，不得凭时间自己编百分比；
//   ③ 快照只读，绝不参与任何判定（判定唯一来源仍是 VerifyReport.Objects 的 Status）。
namespace PCMig.Core.Verify;

/// <summary>
/// 一次验证进度快照。<paramref name="Stage"/> 为 <c>start</c>（开始核对某对象）或 <c>done</c>（该对象已出结论）。
/// </summary>
/// <param name="JobId">任务号（界面对上任务用）。</param>
/// <param name="ObjectIndex">当前对象序号（1 起；<c>done</c> 时表示"第几个已完成"）。</param>
/// <param name="ObjectCount">计划对象总数（真实值，来自 plan.json）。</param>
/// <param name="ObjectId">当前对象号（object-000001 …）。</param>
/// <param name="ObjectPath">当前对象的源路径（给界面显示"正在核对谁"）。</param>
/// <param name="Stage">start / done。</param>
/// <param name="FilesChecked">**已完成**核对的源侧文件数累计（真实累计，不预估）。</param>
/// <param name="BytesChecked">**已完成**核对的源侧字节数累计。</param>
/// <param name="OkObjects">已判定通过的对象数。</param>
/// <param name="MismatchObjects">已判定存在差异的对象数。</param>
/// <param name="UnknownObjects">已判定无法确认的对象数。</param>
/// <param name="EmptyObjects">已判定两侧都无可核对数据的对象数。</param>
/// <param name="ElapsedMs">本次验证已用时（毫秒）。</param>
/// <param name="AtUtc">快照产生时刻（UTC）。</param>
public readonly record struct VerifyProgressSnapshot(
    string JobId,
    int ObjectIndex,
    int ObjectCount,
    string ObjectId,
    string? ObjectPath,
    string Stage,
    long FilesChecked,
    long BytesChecked,
    int OkObjects,
    int MismatchObjects,
    int UnknownObjects,
    int EmptyObjects,
    double ElapsedMs,
    DateTime AtUtc)
{
    /// <summary>
    /// 已完成比例：按**已出结论**的对象数算，绝不用"当前是第几个对象"代替。
    ///
    /// ★ 实测缺陷（2026-10-11，真实 UI 证据）★ 旧实现是 <c>ObjectIndex / ObjectCount</c>：
    /// 计划只有 1 个对象时，<c>start</c> 快照的 ObjectIndex=1 ⇒ 界面在**一个文件都还没核对**时
    /// 就显示 100.0%，正是"伪造进度"（验收 G5/R9 禁止）。改为按四态计数之和计算后，
    /// <c>start</c> 快照恒为 0.0%，只有真正出结论的对象才推进百分比。
    /// </summary>
    public double Percent => ObjectCount <= 0 ? 0 : Math.Round(100.0 * DecidedObjects / ObjectCount, 1);

    /// <summary>已出结论的对象总数（四个状态计数之和；应当等于 <see cref="ObjectIndex"/> 的 done 值）。</summary>
    public int DecidedObjects => OkObjects + MismatchObjects + UnknownObjects + EmptyObjects;

    /// <summary>给界面用的一行真实描述（不含任何"看起来在跑"的假进度）。</summary>
    public string Describe() => Stage == "done"
        ? $"已完成 {ObjectIndex}/{ObjectCount} 个对象：通过 {OkObjects}、存在差异 {MismatchObjects}、无法确认 {UnknownObjects}、无可核对数据 {EmptyObjects}"
        : $"正在核对 {ObjectId}（第 {ObjectIndex}/{ObjectCount} 个）{(string.IsNullOrWhiteSpace(ObjectPath) ? "" : "：" + ObjectPath)}";
}