namespace PCMig.Core.Util;

/// <summary>把 robocopy/系统错误码翻译成人话：ShortReason（一句话原因，给 GUI 报错区）与 Advice（处理建议，给报告）。</summary>
public static class ErrorTranslator
{
    /// <summary>错误码 → 一句话原因（码表集中一处，避免各处字样匹配不一致导致"复制失败"这类无信息结论）。</summary>
    public static string ReasonForCode(int code) => code switch
    {
        2 => "文件未找到（可能已被删除）",
        5 => "权限不足",
        6 => "句柄无效（文件已被删除或独占打开）",
        19 => "介质写保护（目标盘不可写）",
        32 => "文件被占用（旧电脑上有程序正开着它）",
        33 => "文件被占用（另一进程锁定了文件的一部分）",
        39 => "目标磁盘空间不足",
        53 => "网络路径不可达（网络中断）",
        59 => "网络意外错误（旧电脑可能重启）",
        64 => "网络名不可用（断网瞬间）",
        67 => "找不到网络名（共享已关闭/改名，或对方无此共享）",
        82 => "无法创建目录或文件（目标盘格式限制）",
        112 => "目标磁盘空间不足",
        1231 => "旧电脑不可达（关机/断线/IP 变化）",
        1219 => "连接冲突（本机已用别的凭据连着旧电脑）",
        1326 => "登录失败（账号/密码问题）",
        1707 => "网络地址无效（共享枚举被拦截）",
        16 => "严重错误（命令无效/一个文件都没传）",
        8 => "有文件复制失败",
        4 => "源与目标存在不匹配项",
        _ => $"错误 {code}"
    };

    /// <summary>
    /// robocopy 退出码（位掩码）→ 人话。给界面"失败对象逐条列出"用（v0.3.8）：
    /// 生产事故里对象 object-000007 拿到的是 16（严重错误），旧界面只显示" 失败"，
    /// 用户完全看不出"命令行/路径无效、一个文件都没传"这一层含义。
    /// </summary>
    public static string ExitCodeText(int code) => code switch
    {
        -1 => "未执行/被终止（未产出结果）",
        0 => "成功（无需复制）",
        1 => "成功（有文件被复制）",
        2 => "成功（目标有额外文件）",
        3 => "成功（1+2）",
        4 => "存在不匹配条目（目标已有同名但类型不同的条目，这些条目未被复制）",
        5 => "有文件被复制，但存在不匹配条目（部分条目未被复制）",
        6 => "目标有额外条目，且存在不匹配条目（部分条目未被复制）",
        7 => "部分文件被复制，但存在不匹配条目（部分条目未被复制）",
        8 => "有文件复制失败",
        16 => "严重错误：命令/路径无效，一个文件都没传",
        _ => $"位掩码 {code}"
    };

    /// <summary>失败对象一行话（对象号 + 路径 + 退出码译文 + 原因），GUI 报错区与 CLI 共用同一口径。</summary>
    public static string FailureHeadline(string objectId, string targetPath, int exitCode, int largeExitCode)
    {
        var code = exitCode >= 0 ? $"退出码 {exitCode}（{ExitCodeText(exitCode)}）" : "未产出退出码";
        var extra = largeExitCode >= 0 ? $"；Large 通道退出码 {largeExitCode}（{ExitCodeText(largeExitCode)}）" : "";
        return $"{objectId}　{targetPath}　{code}{extra}";
    }

    /// <summary>一句话原因。入参可以是错误码（"32"）、含错误码的整行，或多文件失败汇总（"132 个文件复制失败：…"）。</summary>
    public static string ShortReason(string? err)
    {
        if (string.IsNullOrWhiteSpace(err)) return "未知错误";
        // GUI 实时报错行的入参是裸错误码（如 "32"）——必须走码表，否则会原样显示数字
        if (int.TryParse(err.Trim(), out var bareCode)) return ReasonForCode(bareCode);
        // 多文件失败汇总（RobocopyRunner.SummarizeFailures 产出）：直接给出"多少文件 + 主因"
        var sum = System.Text.RegularExpressions.Regex.Match(err,
            @"^\s*(?<n>\d+)\s*个文件复制失败\s*[:：]\s*(?<why>[^（(；;。]+)");
        if (sum.Success)
            return $"{sum.Groups["n"].Value} 个文件复制失败：{sum.Groups["why"].Value.Trim()}";
        if (err.Contains("1326")) return "登录失败（账号/密码问题）";
        if (err.Contains("1311") || err.Contains("0x0000051F")) return "域不可用（域账号无法验证）";
        if (err.Contains("错误 67") || err.Contains("0x00000043") || err.Contains("Win32Error=67")) return "找不到网络名（电脑名/IP 错误或对方无此共享）";
        if (err.Contains("错误 59") || err.Contains("0x0000003B")) return "网络意外错误（旧电脑可能重启）";
        if (err.Contains("1219")) return "连接冲突（本机已用别的凭据连着旧电脑）";
        if (err.Contains("1707")) return "网络地址无效（共享枚举被拦截）";
        if (err.Contains("错误 82") || err.Contains("0x00000052")) return "无法创建目录或文件（目标盘格式限制）";
        if (err.Contains("错误 112") || err.Contains("0x00000070")) return "目标磁盘空间不足";
        if (err.Contains("错误 32") || err.Contains(" 32 ") || err.Contains("0x00000020")) return "文件被占用";
        if (err.Contains("0x00000035") || err.Contains("错误 53") || err.Contains(" 53 ")) return "网络中断";
        if (err.Contains("0x00000005") || err.Contains("错误 5") || err.Contains(" 5 ")) return "权限不足";
        if (err.Contains("0x00000040") || err.Contains("错误 64")) return "网络不可用";
        if (err.Contains("0x000004CF") || err.Contains("错误 1231")) return "旧电脑不可达";
        if (err.Contains("超过重试限制")) return "文件反复复制失败（达到重试上限，多为文件被占用或权限不足）";
        var m = System.Text.RegularExpressions.Regex.Match(err, @"(?:错误|ERROR)\s+(\d+)");
        if (m.Success) return ReasonForCode(int.Parse(m.Groups[1].Value));
        // 兜底：宁可显示原始报错行，也绝不返回"复制失败"这种零信息结论
        var t = err.Trim().Replace("\r", " ").Replace("\n", " ");
        if (t.Length > 100) t = t[..100] + "…";
        return t.Length > 0 ? t : "未知错误（详见任务日志）";
    }

    /// <summary>处理建议（用于 HTML 报告）。</summary>
    public static string Advice(string? err)
    {
        if (string.IsNullOrWhiteSpace(err)) return "";
        if (err.Contains("1326")) return "登录失败：用户名或密码不对，或该账号密码为空被 Windows 策略禁止网络登录。处理：在工具中输入旧电脑正确的账号密码后点「恢复任务」。";
        if (err.Contains("1311") || err.Contains("0x0000051F")) return "域不可用：域账号无法被验证（域控不可达，或新电脑当前不在公司域网络里）。处理：改用旧电脑的本地账号（格式：电脑名\\用户名，如 OLDPC\\Administrator），或确认网络能联系到域控后重试。";
        if (err.Contains("错误 67") || err.Contains("0x00000043") || err.Contains("Win32Error=67")) return "找不到网络名：① 电脑名/IP 输错（注意别把用户名当电脑名）；② 对方共享名拼写错误；③ 对方是精简 SMB 服务、没有 IPC$（不影响迁移，PCMig 会自动改用直连共享）。处理：核对名称后点「恢复任务」。";
        if (err.Contains("错误 59") || err.Contains("0x0000003B")) return "网络意外错误：旧电脑重启或网络抖动。处理：确认旧电脑在线后点「恢复任务」，已完成部分不会重传。";
        if (err.Contains("1219")) return "连接冲突：本机已有到旧电脑的连接（资源管理器窗口/映射盘，且凭据不同）。处理：关掉那些窗口后点「恢复任务」（PCMig 连接时会自动清理冲突，重试一般即可）。";
        if (err.Contains("1707")) return "共享枚举被拦截。处理：可直接在主机框粘贴具体共享路径（如 \\\\IP\\C$）点连接。";
        if (err.Contains("错误 82") || err.Contains("0x00000052")) return "无法创建目录或文件：目标盘可能是 FAT32（单个文件夹约 2 万个文件封顶）。处理：把目标改到 NTFS/exFAT 盘后点「恢复任务」。";
        if (err.Contains("错误 112") || err.Contains("0x00000070")) return "目标磁盘空间不足。处理：清理目标盘或更换目标后点「恢复任务」，已传部分不会重传。";
        if (err.Contains("错误 32") || err.Contains("0x00000020")) return "文件被占用：旧电脑上有程序正开着它。处理：在旧电脑上关闭对应程序（如 Outlook）后点「恢复任务」，只补未传文件。";
        if (err.Contains("0x00000035") || err.Contains("错误 53")) return "网络中断：旧电脑离线、断网或重启。处理：确认旧电脑在线后点「恢复任务」，已完成部分不会重传。";
        if (err.Contains("错误 5 ") || err.Contains("0x00000005")) return "权限不足。处理：使用旧电脑的管理员账号（Win7 注意用内置 Administrator 或域管理员）。";
        if (err.Contains("0x00000040") || err.Contains("错误 64")) return "网络名称不可用（断网瞬间）。处理：网络恢复后点「恢复任务」。";
        if (err.Contains("0x000004CF") || err.Contains("错误 1231")) return "旧电脑不可达（关机/断线/IP 变化）。处理：确认旧电脑在线后点「恢复任务」。";
        return "";
    }
}
