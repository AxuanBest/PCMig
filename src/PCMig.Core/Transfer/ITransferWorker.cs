using PCMig.Core.Matrix;
using PCMig.Core.Models;
using PCMig.Diagnostics.Abstractions;

namespace PCMig.Core.Transfer;

/// <summary>
/// 传输 worker 抽象（Trust-Critical Recovery FIX BATCH 1 引入）。
///
/// 生产实现只有 <see cref="RobocopyRunner"/>（真实 robocopy 进程 + 进程树强杀）。
/// 抽出接口的唯一目的是给"**暂停真的生效了吗**"这件事一个可测的接缝：
/// 旧实现里暂停只在"对象边界"检查，而 28.5 GB 的单对象内部复制期间没有任何检查点 ⇒
/// 8/8 次点击、8/8 请求写入成功、0 次引擎确认、传输从未停止。
/// 新语义要求引擎必须能 ①随时打断当前 worker ②判断 worker 是否真的不再运行。
/// 接口上的这两个能力就是这两件事，缺一不可（<see cref="KillCurrent"/> 曾长期无返回值也无状态查询，
/// 调用方因此无法区分"已经停了"和"杀了个空"）。
/// </summary>
public interface ITransferWorker
{
    /// <summary>对象级诊断上下文（每次尝试由编排器刷新）。</summary>
    DiagnosticContext Diagnostics { get; set; }

    /// <summary>当前是否还有 worker 在跑（进程已退出 / 从未启动 ⇒ false）。暂停达成的权威判据。</summary>
    bool HasRunningWorker { get; }

    /// <summary>终止当前 worker（含整个进程树）。可重复调用；无 worker 时是空操作。</summary>
    void KillCurrent();

    /// <summary>跑一趟复制。</summary>
    Task<RobocopyRunResult> RunPassAsync(
        string src,
        string dst,
        MigrationOptions opt,
        MigrationMatrix matrix,
        PassKind pass,
        string unicodeLogPath,
        CancellationToken ct,
        IReadOnlyList<string>? fileList = null,
        bool restartableLarge = false);

    /// <summary>
    /// ★ FIX BATCH 4（P1-1）★ 当前 worker 进程累计从源读出的字节数（内核 I/O 计数器）。
    ///
    /// /Z 可续传通道会预分配目标文件长度，robocopy stdout 又是块缓冲 ⇒ 该通道上"对象内部的连续进度"
    /// 没有任何别的可信来源，界面因此整段停在对象起点（真机：网络 35+ MB/s、界面 0 B，直到 28.5 GB
    /// 对象整体结束才跳）。进程 I/O 计数器是这条通道上唯一连续的观测。
    ///
    /// 语义边界：**只用于显示**，绝不进入回执/校验真值；累计值（含重试重复读）由调用方按趟重开基线并封顶。
    /// 无 worker / 进程已退出 / 无权限读取 ⇒ 返回 false（调用方保留上一次确认值，不得臆造）。
    /// </summary>
    bool TryGetWorkerReadBytes(out long bytes);
}