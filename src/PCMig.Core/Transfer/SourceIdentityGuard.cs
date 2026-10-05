using PCMig.Core.Models;
using PCMig.Core.Native;

namespace PCMig.Core.Transfer;

/// <summary>一条"源身份与计划基线不一致"的发现。</summary>
public sealed record SourceIdentityFinding(string ObjectId, string SourcePath, string? Expected, string? Actual);

/// <summary>
/// 源身份守卫（缺陷 B12b4：同名共享换底 ⇒ 从非源数据继续取数并宣称成功）。
///
/// 职责只有一个：把「计划时的源身份基线」与「此刻实际的源身份」比一遍，并对不一致的对象
/// 给出可执行的中止结论。**不碰网络、不碰文件系统** —— 取值通过注入的委托完成，
/// 这样判定语义可以被单元测试完整覆盖。
///
/// 判定纪律（宁可不判，不可误判）：
///  · 对象没有基线（旧 plan.json 字段为空）⇒ 跳过，不算不符（向后兼容）；
///  · 此刻取不到身份 ⇒ 跳过，不算不符（不能因为"探测失败"就阻断用户的正常续传）；
///  · 只有"基线有值 且 此刻有值 且 两者不同"才判为不符。
/// </summary>
public static class SourceIdentityGuard
{
    /// <param name="objects">计划中的对象（含计划时记录的源身份基线）。</param>
    /// <param name="capture">取此刻身份指纹（生产用 <c>SourceIdentity.Capture</c>；单测注入假实现）。</param>
    public static List<SourceIdentityFinding> Evaluate(
        IReadOnlyList<PlannedObject> objects, Func<string, string?> capture)
    {
        var findings = new List<SourceIdentityFinding>();
        if (objects is null) return findings;
        foreach (var o in objects)
        {
            var expected = o.SourceIdentity;
            if (string.IsNullOrEmpty(expected)) continue;
            var actual = capture(o.SourcePath);
            if (string.IsNullOrEmpty(actual)) continue;
            // 按分量比较：两边都提供的判据（文件 ID / 共享物理路径）任一不同即判不符。
            if (SourceIdentity.IdentityChanged(expected, actual))
                findings.Add(new SourceIdentityFinding(o.ObjectId, o.SourcePath, expected, actual));
        }
        return findings;
    }

    /// <summary>
    /// 中止文案：必须同时说清「发生了什么」「为什么停」「用户下一步做什么」，
    /// 并且明确"本次没有继续写入目标端"，避免用户以为失败但数据已乱。
    /// </summary>
    public static string BuildAbortMessage(IReadOnlyList<SourceIdentityFinding> findings)
    {
        var lines = new List<string>
        {
            "源身份校验未通过：本次任务记录的那个源位置**已经不是原来那份数据**了" +
            "（典型的同名换底：共享名没变，但共享背后的目录/磁盘被换成了别处）。",
            "为避免把你没打算迁移的数据写进目标端，PCMig 已**拒绝继续传输，本次没有复制任何文件**。",
            "差异明细（对象 · 源路径 · 计划时身份 → 当前身份）："
        };
        foreach (var f in findings.Take(8))
            lines.Add($"  · {f.ObjectId} · {f.SourcePath} · {f.Expected} → {f.Actual}");
        if (findings.Count > 8) lines.Add($"  · …另有 {findings.Count - 8} 个对象同样不一致");

        lines.Add("下一步：请到旧电脑上确认该共享现在指向的目录是不是原数据盘" +
                  "（例如共享 D 是否仍指向 D:\\，而不是被重建到了别的目录）；" +
                  "确认无误后**新建一个迁移任务**重新扫描生成计划；" +
                  "若确实需要迁移新指向的数据，请用新任务明确选择它，不要沿用旧任务的续传。");
        return string.Join(Environment.NewLine, lines);
    }
}