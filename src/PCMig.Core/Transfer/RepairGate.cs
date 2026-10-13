using PCMig.Core.Models;

namespace PCMig.Core.Transfer;

/// <summary>
/// ★ P0-01(4) / P0-03 ★ 破坏性修复的**单一判据**：只有"确认差异"的验证结论才允许改动目标侧同名文件。
/// （★ P0-03，2026-10-12 ★ 放行后的动作已从"删除"改为"改名隔离"，见 <see cref="RepairPurge"/>／<see cref="RepairQuarantine"/>；
///  本判据的语义随之收窄为"是否允许把目标那份挪开"，判据本身一字未改。）
///
/// 为什么把它单独抽出来：这条判据决定"会不会动用户数据"，必须能被独立测试、且不被任何调用点
/// 另写一套。现场证据（Preview object-000018 的 14 个根层文件 29.46 GB、Stable a632 object-000004）
/// 都是被"假缺失"结论触发的强制覆盖动过的。
///
/// 判据（只增不减）：
///   · 没有验证记录（老 Job、或还没跑验证）⇒ 挡（无证据不得改动目标文件）
///   · UNKNOWN（源/目标不可达、枚举被跳过、与计划矛盾的 0/0）⇒ 挡，并带上具体原因
///   · EMPTY（无可核对数据）、CANCELED（没验完）⇒ 挡
///   · 只有 MISMATCH（两侧都数得清、且确实不一致）⇒ 放行
///
/// 安全带：即便某条未来路径把 <see cref="ObjectVerifyResult.AllowsDestructiveRepair"/> 置为 true，
/// 只要 <see cref="ObjectVerifyResult.Status"/> 不是 MISMATCH，这里**仍然挡**——放行标志与状态
/// 自相矛盾时按更安全的一侧处理。
/// </summary>
internal static class RepairGate
{
    /// <summary>没有验证记录（老 Job 没有 verify-report.json 也必须挡住删除）。</summary>
    public const string NoEvidenceCode = "no-verify-evidence";

    /// <summary>确认差异 ⇒ 放行删除。</summary>
    public const string ConfirmedMismatchCode = "confirmed-mismatch";

    /// <summary>有验证结论、但不是"确认差异"（OK / EMPTY / CANCELED 等）⇒ 挡。</summary>
    public const string NotConfirmedCode = "not-confirmed-mismatch";

    public static bool AllowsDestructiveRepair(ObjectVerifyResult? vr, out string code, out string status)
    {
        if (vr is null)
        {
            code = NoEvidenceCode;
            status = "无验证记录";
            return false;
        }

        status = vr.Status;

        // ★ 唯一放行路径 ★：状态本身是"确认差异"，且验证侧也已据此置位放行标志。
        if (vr.Status == "MISMATCH" && vr.AllowsDestructiveRepair)
        {
            code = ConfirmedMismatchCode;
            return true;
        }

        code = vr.Status == "UNKNOWN"
            ? "unverifiable:" + (vr.UnknownReason ?? "unknown")
            : NotConfirmedCode;
        return false;
    }
}