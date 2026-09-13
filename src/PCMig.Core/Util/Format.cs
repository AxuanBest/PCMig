namespace PCMig.Core.Util;

public static class Format
{
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
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "估算中…";
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1) return $"约 {ts.Hours} 小时 {ts.Minutes} 分";
        if (ts.TotalMinutes >= 1) return $"约 {ts.Minutes} 分 {ts.Seconds} 秒";
        return $"约 {ts.Seconds} 秒";
    }

    public static string Percent(double p) => $"{p:0.0}%";
}
