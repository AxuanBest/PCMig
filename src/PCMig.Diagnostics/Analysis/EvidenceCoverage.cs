using PCMig.Diagnostics.Abstractions;

namespace PCMig.Diagnostics.Analysis;

/// <summary>
/// 证据覆盖（D6.1 §4）：**按投递类与分支分别回答"这段证据完整吗"**。
///
/// 为什么不能再用一个全局 Epoch：
///   · 一次 **Verbose** 丢弃（调试细节）与一次 **DurableCritical** 丢失（关键事实）后果完全不同；
///   · 用全局 Epoch 会让"丢了一条 Verbose"把所有依赖 Operational 证据的规则
///     （持久化写失败、Robocopy 退出码、传输对象结果…）全部降级为 Inconclusive —— 这是**过度降级**，
///     和"不该下确定结论时硬下结论"一样有害（只是方向相反）。
///
/// 规则只对自己依赖的那一档/那一分支负责：`RequiredDeliveryClass` + `RequiredBranch`。
/// </summary>
public sealed class EvidenceCoverage
{
    private readonly LossLedger _loss;

    public EvidenceCoverage(LossLedger loss) => _loss = loss ?? throw new ArgumentNullException(nameof(loss));

    /// <summary>该档（含更高档）证据是否**没有任何丢失**。</summary>
    public bool IsCompleteFor(DeliveryClass atLeast) => _loss.EpochFor(atLeast) == 0;

    /// <summary>该分支是否**没有任何丢失**。</summary>
    public bool IsCompleteForBranch(string branch) => _loss.BranchEpoch(branch) == 0;

    /// <summary>该档的证据世代（放进 Incident 的 LossEpoch，便于离线解释）。</summary>
    public long EpochFor(DeliveryClass atLeast) => _loss.EpochFor(atLeast);

    /// <summary>全局世代（仅用于展示"本会话一共损失过几次"，不作为规则判据）。</summary>
    public long GlobalEpoch => _loss.Epoch;

    public bool HasFaultLoss => _loss.HasFaultLoss;

    /// <summary>保留性淘汰（环覆盖/队列合并）——不是丢失，但要在界面上说清。</summary>
    public long RetentionEvictions => _loss.RetentionEvictions;

    /// <summary>
    /// 按规则声明的依赖判断完整性：投递类（必选）× 分支（可选，例如反馈规则依赖 analyzer 收件箱）。
    /// </summary>
    public bool IsCompleteForRule(IDiagnosticRule rule)
    {
        if (!IsCompleteFor(rule.RequiredDeliveryClass)) return false;
        return rule.RequiredBranch is not { Length: > 0 } branch || IsCompleteForBranch(branch);
    }
}