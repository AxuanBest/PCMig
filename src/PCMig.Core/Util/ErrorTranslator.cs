namespace PCMig.Core.Util;

/// <summary>把 robocopy/系统错误码翻译成人话：ShortReason（一句话原因，给 GUI 报错区）与 Advice（处理建议，给报告）。</summary>
public static class ErrorTranslator
{
    /// <summary>一句话原因。入参可以是错误码（"32"）或含错误码的整行。</summary>
    public static string ShortReason(string? err)
    {
        if (string.IsNullOrWhiteSpace(err)) return "未知错误";
        if (err.Contains("1326")) return "登录失败（账号/密码问题）";
        if (err.Contains("错误 32") || err.Contains(" 32 ") || err.Contains("0x00000020")) return "文件被占用";
        if (err.Contains("0x00000035") || err.Contains("错误 53") || err.Contains(" 53 ")) return "网络中断";
        if (err.Contains("0x00000005") || err.Contains("错误 5") || err.Contains(" 5 ")) return "权限不足";
        if (err.Contains("0x00000040") || err.Contains("错误 64")) return "网络不可用";
        if (err.Contains("0x000004CF") || err.Contains("错误 1231")) return "旧电脑不可达";
        var m = System.Text.RegularExpressions.Regex.Match(err, @"(?:错误|ERROR)\s+(\d+)");
        return m.Success ? $"错误 {m.Groups[1].Value}" : "复制失败";
    }

    /// <summary>处理建议（用于 HTML 报告）。</summary>
    public static string Advice(string? err)
    {
        if (string.IsNullOrWhiteSpace(err)) return "";
        if (err.Contains("1326")) return "登录失败：用户名或密码不对，或该账号密码为空被 Windows 策略禁止网络登录。处理：在工具中输入旧电脑正确的账号密码后点「恢复任务」。";
        if (err.Contains("错误 32") || err.Contains("0x00000020")) return "文件被占用：旧电脑上有程序正开着它。处理：在旧电脑上关闭对应程序（如 Outlook）后点「恢复任务」，只补未传文件。";
        if (err.Contains("0x00000035") || err.Contains("错误 53")) return "网络中断：旧电脑离线、断网或重启。处理：确认旧电脑在线后点「恢复任务」，已完成部分不会重传。";
        if (err.Contains("错误 5 ") || err.Contains("0x00000005")) return "权限不足。处理：使用旧电脑的管理员账号（Win7 注意用内置 Administrator 或域管理员）。";
        if (err.Contains("0x00000040") || err.Contains("错误 64")) return "网络名称不可用（断网瞬间）。处理：网络恢复后点「恢复任务」。";
        if (err.Contains("0x000004CF") || err.Contains("错误 1231")) return "旧电脑不可达（关机/断线/IP 变化）。处理：确认旧电脑在线后点「恢复任务」。";
        return "";
    }
}
