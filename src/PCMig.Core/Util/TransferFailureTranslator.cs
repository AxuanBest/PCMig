using System.Text.RegularExpressions;

namespace PCMig.Core.Util;

/// <summary>
/// 迁移过程中"存储写失败"的人话翻译（GROUP D F5）。
/// 背景（D03 实测）：任务状态/日志目录被拒绝写入时，产品把人话换成了英文原始异常串
/// ——"传输异常：Access to the path 'C:\ProgramData\PCMig\Jobs\JOB-…\receipts\object-000005-20261004002454.json.tmp-22f1abab' is denied."
/// 这既没说是哪一类存储不可写、也没说任务处于什么状态、更没告诉用户怎么办，还泄漏了内部临时文件名。
/// 本翻译器只产出中文可操作结论；原始异常（含堆栈）仍由调用方照常写日志，不丢诊断信息。
/// </summary>
public static class TransferFailureTranslator
{
    private static readonly Regex QuotedPath = new(@"'([^']+)'", RegexOptions.Compiled);

    /// <summary>
    /// 异常 → 面向用户的中文结论（含"哪个位置不可写 / 任务现在什么状态 / 怎么办"）。
    /// 不做字符串拼接之外的副作用，纯函数，便于单测。
    /// </summary>
    public static string Explain(Exception ex)
    {
        var raw = ex.Message ?? "";
        var path = ExtractPath(raw);

        if (ex is UnauthorizedAccessException || IsDenied(raw))
            return DeniedMessage(path, ex);

        if (ex is IOException io)
        {
            if (IsDiskFull(raw))
                return "目标磁盘空间不足：迁移已停止，进度与回执没有保存。清理目标盘空间后点「恢复任务」继续，已复制的数据不会重传。";
            if (IsSharingViolation(raw))
                return $"文件被占用（错误码 32）：{Where(path)}正被其他程序打开，本次没有写入。关闭占用程序后点「恢复任务」继续，已复制的数据不会重传。";
            return $"读写失败（磁盘或网络 I/O 错误）：迁移已停止，进度与回执没有保存。检查目标盘与共享是否可用后点「恢复任务」继续。{Where(path)}";
        }

        // 兜底：不把英文原始串和内部临时文件名抛给用户，原因类别给中文，细节留给日志。
        return $"迁移中断：发生了未预期的错误（{ex.GetType().Name}），任务已停止。原始信息已写入日志，请打开日志核对。{Where(path)}";
    }

    /// <summary>
    /// 准备阶段（尚未开始传输）的失败翻译（GROUP D F13）。
    /// 背景（D03 真机实测 2026-10-04，Final Critical Matrix 相 D）：任务状态目录在 Prepare 阶段就不可写时，
    /// 界面把原始 .NET 串 `准备失败：Access to the path '…\JOB-…\receipts' is denied.` 直接抛给用户——
    /// 与 F5 已修好的传输路径（<see cref="Explain"/>）不一致，属同类缺陷的另一条产品面。
    /// 与 <see cref="Explain"/> 的**语义差别**：此刻既没有任务也没有已复制数据，
    /// 所以不说"任务已停止 / 已复制到目标的数据不会重传"，只回答"哪个位置不可写 / 现在什么状态 / 怎么办"。
    /// </summary>
    public static string ExplainPrepare(Exception ex)
    {
        var raw = ex.Message ?? "";
        var path = ExtractPath(raw);

        if (ex is UnauthorizedAccessException || IsDenied(raw))
            return $"无法写入{Where(path).TrimEnd('。')}：当前账号没有写入权限（Windows 拒绝访问）。"
                 + "本次没有开始传输，也没有往目标写入任何数据——给该账号补上这个位置的写权限后重新点「准备」即可。";

        if (ex is IOException)
        {
            if (IsDiskFull(raw))
                return "磁盘空间不足：本次没有开始传输，也没有往目标写入任何数据。清理空间后重新点「准备」。";
            if (IsSharingViolation(raw))
                return $"文件被占用（错误码 32）：{Where(path)}正被其他程序打开。关闭占用程序后重新点「准备」。";
            return "读写失败（磁盘或网络 I/O 错误）：本次没有开始传输。"
                 + $"检查目标盘与共享是否可用后重新点「准备」。{Where(path)}";
        }

        return $"发生了未预期的错误（{ex.GetType().Name}）：本次没有开始传输，也没有往目标写入任何数据。"
             + $"原始信息已写入日志，请打开日志核对。{Where(path)}";
    }

    /// <summary>从 'xxx' 里取出路径；取不到返回 null。</summary>
    public static string? ExtractPath(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var m = QuotedPath.Match(message);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// 位置描述：**不暴露内部临时文件名**（D03 那条里 .json.tmp-22f1abab 属内部实现细节），
    /// 只说到目录 + 这个目录在产品里是什么用途。
    /// </summary>
    public static string Where(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "迁移状态/日志目录不可写。";
        string dir;
        try
        {
            dir = Path.GetDirectoryName(path) ?? path;
        }
        catch (ArgumentException)
        {
            dir = path;   // 路径本身非法（极少）：原样说出，不再解析
        }
        var kind = Classify(dir);
        return kind is null ? $"位置 {dir} 不可写。" : $"{kind}（{dir}）不可写。";
    }

    private static string DeniedMessage(string? path, Exception ex)
    {
        var where = Where(path);
        var what = ex is UnauthorizedAccessException ? "没有写入权限（Windows 拒绝访问）" : "被拒绝写入";
        return $"无法写入{where.TrimEnd('。')}：当前账号{what}。任务已停止。"
             + "注意：数据盘本身仍可写，已复制到目标的数据不会重传——给该账号补上这个位置的写权限后点「恢复任务」只补差异即可。";
    }

    /// <summary>目录用途识别（命中产品自己的两个存储路径时给出明确名称）。</summary>
    private static string? Classify(string dir)
    {
        if (dir.Contains(@"\Jobs", StringComparison.OrdinalIgnoreCase)) return "任务状态目录";
        if (dir.Contains(@"\Logs", StringComparison.OrdinalIgnoreCase)) return "日志目录";
        return null;
    }

    private static bool IsDenied(string raw)
        => raw.Contains("is denied", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("拒绝访问", StringComparison.Ordinal)
        || raw.Contains("Access to the path", StringComparison.OrdinalIgnoreCase);

    private static bool IsDiskFull(string raw)
        => raw.Contains("112", StringComparison.Ordinal)
        || raw.Contains("disk full", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("磁盘空间不足", StringComparison.Ordinal)
        || raw.Contains("磁盘已满", StringComparison.Ordinal)
        || raw.Contains("not enough space", StringComparison.OrdinalIgnoreCase);

    private static bool IsSharingViolation(string raw)
        => raw.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("错误码 32", StringComparison.Ordinal)
        || raw.Contains("(32)", StringComparison.Ordinal);
}