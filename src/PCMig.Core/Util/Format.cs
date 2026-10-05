namespace PCMig.Core.Util;

public static class Format
{
    // UI Closure（2026-10-05，用户人工验收项）：面向普通 Windows 用户的显示单位改回 KB / MB / GB / TB / PB。
    // 口径仍是 1024 进制除法（与 Windows 资源管理器、任务管理器的显示习惯一致：它们同样以 1024 除却标 KB/MB/GB），
    // 只有**标签**变化，没有任何计算依赖标签；内部真值始终是精确 Bytes。
    // 唯一 User-Facing Formatter 就是本类（无第二套实现）；诊断证据域（DiagnosticCenterViewModel 的 KiB、
    // PreflightChecker 事件 payload 的 /1024/1024）刻意**不走**本 formatter，保持原样不改事件语义。
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(long bytes)
    {
        if (bytes < 0) return "-" + Bytes(-bytes);
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} {Units[u]}" : $"{v:0.##} {Units[u]}";
    }

    public static string Speed(double bytesPerSec) => Bytes((long)Math.Max(0, bytesPerSec)) + "/s";

    public static string Eta(double seconds)
    {
        // FIX BATCH 4（§7.4）：短窗口样本不足 / 暂停 / 已完成 ⇒ 一律显示 "—"，不显示上一条旧 ETA（真机上
        // "暂停了还挂着 1 小时 23 分"比没有 ETA 更误导）。"估算中…" 这种含糊文案也不再使用。
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "—";
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1) return $"约 {ts.Hours} 小时 {ts.Minutes} 分";
        if (ts.TotalMinutes >= 1) return $"约 {ts.Minutes} 分 {ts.Seconds} 秒";
        return $"约 {ts.Seconds} 秒";
    }

    public static string Percent(double p) => $"{p:0.0}%";
}
