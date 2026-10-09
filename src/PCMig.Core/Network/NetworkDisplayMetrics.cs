using PCMig.Core.Util;

namespace PCMig.Core.Network;

/// <summary>网卡链路的显示状态（由采样器根据"接口是否选得出来 + 行信息里的 OperStatus/MediaConnectState"判定）。</summary>
public enum NetworkLinkState
{
    /// <summary>还在定位/等待第一个样本（例如刚启动、还没连上旧电脑）。</summary>
    Connecting = 0,
    /// <summary>已选定网卡且链路已连接。</summary>
    Connected,
    /// <summary>网卡存在但链路断开（网线拔了 / 无线没连 / 对端关机）。</summary>
    Disconnected,
    /// <summary>观测不可用（选不出网卡 / 读不到行信息 / 非 Windows / 布局自检未通过）。</summary>
    Unavailable,
}

/// <summary>显示口径的计算结果（纯数据）。数值用 <see cref="double.NaN"/> 表示"不适用 ⇒ 显示 '—'"。</summary>
/// <param name="State">链路状态。</param>
/// <param name="Paused">是否暂停。</param>
/// <param name="SpeedBytesPerSecond">界面速度数值（真实网卡接收吞吐的轻度 EMA）；<see cref="double.NaN"/> 表示无读数。</param>
/// <param name="SpeedText">速度文本（"B/s、KB/s、MB/s…" 1024 进制，或 "—"）。</param>
/// <param name="EtaSeconds">ETA 秒数（可非常大，未截断）；<see cref="double.NaN"/> 表示不适用。</param>
/// <param name="EtaText">ETA 文本（"约 X 分 Y 秒" 或 "—"）。</param>
/// <param name="Reason">中文理由：为什么显示这个值 / 为什么是"—"，便于界面提示与排障。</param>
public readonly record struct NetworkDisplayResult(
    NetworkLinkState State,
    bool Paused,
    double SpeedBytesPerSecond,
    string SpeedText,
    double EtaSeconds,
    string EtaText,
    string Reason)
{
    /// <summary>是否有速度读数（false ⇒ 显示的是 "—"，不是 0）。</summary>
    public bool HasSpeed => !double.IsNaN(SpeedBytesPerSecond);

    /// <summary>是否有 ETA（false ⇒ 显示的是 "—"）。</summary>
    public bool HasEta => !double.IsNaN(EtaSeconds);
}

/// <summary>
/// 纯函数：给定「链路状态 + 是否暂停 + 样本情况 + 剩余逻辑字节」⇒ 速度数值/文本 + ETA 秒数/文本。
///
/// <para>
/// 【为什么单独一层】显示规则必须可单测：Connecting/Disconnected ⇒ "—"；已连接但暂无传输 ⇒ "0 B/s" + ETA "—"；
/// 样本不足 ⇒ ETA "—"；暂停 ⇒ 0 B/s + ETA "—"；稳定采样后 ⇒ 真实网卡吞吐 + 平滑 ETA。
/// 这些规则放进界面就没法单测，也容易被"顺手改成 0"。
/// </para>
///
/// <para>
/// 【单位口径（必须明确）】速度文本复用全仓库唯一的 user-facing formatter <see cref="Format.Speed"/>：
/// 它按 <b>1024 进制</b>换算但标签写 KB/MB/GB，所以界面上的 "MB/s" 实际是 MiB/s
/// （例如 112.5 MB/s 的十进制 Mbps 对照值 = 900 Mbps，见 <see cref="MegabitsPerSecondToBytesPerSecond"/>）。
/// 真实网卡吞吐只以 <b>字节/秒</b> 参与计算与显示，**不**做 Mbps 换算（Windows 任务管理器显示 Mbps，
/// 对照关系：Mbps ÷ 8 ≈ MB/s，例 900 Mbps ≈ 112.5 MB/s）。
/// </para>
///
/// <para>本类型是纯函数，无状态、无 P/Invoke、不碰 Receipt / Progress Truth / CompletedBytes / Verifier。</para>
/// </summary>
public static class NetworkDisplayMetrics
{
    /// <summary>1 字节 = 8 bit（换算用）。</summary>
    public const double BitsPerByte = 8.0;

    /// <summary>十进制 Mbps 换算因子（Windows 任务管理器口径：1 Mbps = 1,000,000 bit/s）。</summary>
    public const double BitsPerMegabit = 1_000_000.0;

    /// <summary>1 MiB 的字节数（PCMig 界面 MB 标签的实际进制）。</summary>
    public const double BytesPerMebibyte = 1024.0 * 1024.0;

    /// <summary>Mbps（十进制，任务管理器口径）⇒ 字节/秒。例：900 Mbps ≈ 112.5 MB/s。</summary>
    public static double MegabitsPerSecondToBytesPerSecond(double megabitsPerSecond)
        => megabitsPerSecond * BitsPerMegabit / BitsPerByte;

    /// <summary>字节/秒 ⇒ Mbps（十进制，任务管理器口径）。</summary>
    public static double BytesPerSecondToMegabitsPerSecond(double bytesPerSecond)
        => bytesPerSecond * BitsPerByte / BitsPerMegabit;

    /// <summary>字节/秒 ⇒ 界面 MB/s 数值（1024 进制，与 <see cref="Format.Speed"/> 的标签一致）。</summary>
    public static double BytesPerSecondToDisplayMegabytesPerSecond(double bytesPerSecond)
        => bytesPerSecond / BytesPerMebibyte;

    /// <summary>
    /// 计算显示口径。
    /// </summary>
    /// <param name="state">链路状态（不可用/连接中/已断开 ⇒ 无读数）。</param>
    /// <param name="paused">任务是否暂停（暂停 ⇒ 速度 0 B/s、ETA "—"）。</param>
    /// <param name="hasReading">是否已采到至少一个有效样本（第一次采样之前没有读数可言）。</param>
    /// <param name="emaBytesPerSecond">真实接收吞吐的轻度 EMA（界面速度口径，约 1~2 秒）。</param>
    /// <param name="hasSufficientSamples">稳定窗口样本是否充足（不足 ⇒ ETA "—"）。</param>
    /// <param name="stableBytesPerSecond">稳定窗口吞吐（ETA 口径，约 5~10 秒）。</param>
    /// <param name="remainingLogicalBytes">剩余**逻辑**字节（= 计划字节 − 已完成字节，由上层从 Progress Truth 换算；只做 ETA 的被除数）。</param>
    /// <param name="terminal">
    /// ★ 2026-10-08 Preview.2 ★ 任务是否已进入**终态**（完成 / 完成但有错 / 中断 / 失败 / 取消）。
    /// 缺省 false 表示"仍在跑"，老调用点语义不变。终态下速度按 0 显示、ETA 一律 "—"。
    /// </param>
    public static NetworkDisplayResult Compute(
        NetworkLinkState state,
        bool paused,
        bool hasReading,
        double emaBytesPerSecond,
        bool hasSufficientSamples,
        double stableBytesPerSecond,
        long remainingLogicalBytes,
        bool terminal = false)
    {
        // ① 没有链路就没有真实吞吐可读：一律 "—"（不是 0——0 会被误读成"网络卡住了"）。
        if (state != NetworkLinkState.Connected)
        {
            var why = state switch
            {
                NetworkLinkState.Connecting => "正在建立网卡观测（还没定位到实际走的那块网卡 / 还没连上旧电脑）：速度与 ETA 都显示“—”",
                NetworkLinkState.Disconnected => "网卡链路已断开（网线拔了 / 无线未连 / 对端关机）：没有真实接收流量，速度与 ETA 都显示“—”",
                _ => "网卡吞吐观测不可用（选不出接口 / 读不到行信息 / 系统不支持）：速度与 ETA 都显示“—”",
            };
            return new NetworkDisplayResult(state, paused, double.NaN, "—", double.NaN, "—", why);
        }

        // ② 任务已进入终态（真机问题：任务结束后底栏还在"预计剩余 8 小时"）★ 2026-10-08 Preview.2 ★
        //   终态时剩余逻辑字节会**冻结在 >0**（"完成但有错"= 计划字节 > 已落盘字节；"中断"= 只剩部分），
        //   而网卡只要还有任意背景接收流量，旧实现就照样满足 ETA 的全部前置条件 ⇒ 巨大且持续变化的假 ETA。
        //   速度按 0 显示（任务已结束，"还在跑"是错的），ETA 显示 "—"（没有"还要多久"这回事）。
        if (terminal)
        {
            return new NetworkDisplayResult(
                state, false, 0, Format.Speed(0), double.NaN, "—",
                "任务已结束：真实网卡接收吞吐按 0 显示，ETA 显示“—”（结束时剩余逻辑字节冻结在 >0，" +
                "再拿它除以背景流量就会算出“预计剩余若干小时”这种假数字，所以终态一律不显示 ETA）");
        }

        // ③ 暂停：链路还在，但此刻没有接收流量（真实吞吐就是 0），ETA 不适用。
        if (paused)
        {
            return new NetworkDisplayResult(
                state, true, 0, Format.Speed(0), double.NaN, "—",
                "已暂停：真实网卡接收吞吐按 0 显示，ETA 显示“—”（暂停后仍挂着旧 ETA 比没有更误导）");
        }

        // ④ 已连接但还没采到第一个样本：没有读数 ⇒ "—"（此后 EMA 一旦建立，即使为 0 也显示 "0 B/s"）。
        if (!hasReading || double.IsNaN(emaBytesPerSecond))
        {
            return new NetworkDisplayResult(
                state, false, double.NaN, "—", double.NaN, "—",
                "已连接，但还没有拿到有效的网卡计数器样本（首次采样只建立基线）：速度与 ETA 都显示“—”");
        }

        var speed = Math.Max(0.0, emaBytesPerSecond);
        var speedText = Format.Speed(speed);

        // ④ ETA：只用稳定窗口吞吐（不用抖动的瞬时/EMA 值），且必须样本充足。
        if (!hasSufficientSamples || double.IsNaN(stableBytesPerSecond))
        {
            return new NetworkDisplayResult(
                state, false, speed, speedText, double.NaN, "—",
                $"速度已可显示（真实接收吞吐 EMA {speed:0} B/s），但样本还不足（需要多个样本且覆盖数秒）：ETA 显示“—”");
        }

        if (stableBytesPerSecond <= 0)
        {
            return new NetworkDisplayResult(
                state, false, speed, speedText, double.NaN, "—",
                "已连接但当前没有接收流量（稳定窗口吞吐为 0）：无法推算 ETA，显示“—”");
        }

        if (remainingLogicalBytes <= 0)
        {
            return new NetworkDisplayResult(
                state, false, speed, speedText, double.NaN, "—",
                "已无剩余逻辑字节（进度已完成）：ETA 不适用，显示“—”");
        }

        // ETA = 剩余逻辑字节 ÷ 稳定窗口接收吞吐（任务书指定公式）。
        var eta = remainingLogicalBytes / stableBytesPerSecond;
        return new NetworkDisplayResult(
            state, false, speed, speedText, eta, Format.Eta(eta),
            $"ETA = 剩余 {remainingLogicalBytes} 逻辑字节 ÷ 稳定窗口 {stableBytesPerSecond:0} B/s = {eta:0.#} s" +
            "（注意：Skip 掉的字节不经过网卡，所以 ETA 用真实网卡吞吐比用逻辑完成字节更接近现实）");
    }

    /// <summary>状态的简短中文名（给界面/日志用）。</summary>
    public static string Describe(NetworkLinkState state) => state switch
    {
        NetworkLinkState.Connected => "已连接",
        NetworkLinkState.Connecting => "连接中",
        NetworkLinkState.Disconnected => "已断开",
        _ => "不可用",
    };
}