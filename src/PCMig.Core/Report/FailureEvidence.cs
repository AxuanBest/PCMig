// ★ PCMig v0.5.3 Preview.2 ★ 失败证据采集（只读、有界、诚实）
//
// 真机现场（JOB-20261008-140340-6310，2026-10-08）：
//   object-000001 计划 95.9 GB / 27835 文件，robocopy 退出码 **9**（1=有文件被复制 + 8=有文件复制失败），
//   而 HTML 报告里的"错误"列**空白**、Step4 的失败原因只有一条被截断的普通行，用户完全看不懂为什么失败；
//   object-000003 被标成 interrupted（bulk exit 1、large -1）却连报告的对象清单都没进。
//
// 本文件只做一件事：**从已生成的 robocopy 日志里，把真正有证据价值的行按白名单抠出来**，
// 供报告与界面展示。三条硬约束（改动时不得违反）：
//   ① 只读：绝不写回执、绝不改 job-state.json、绝不改 Progress Truth（进度真值另有唯一来源）；
//   ② 有界：单对象最多保留 MaxLinesPerObject 行，`File.ReadLines` 流式读取，亿级失败也不会吃光内存；
//   ③ 诚实：**没有证据就说没有**。旧实现拿 `errorLines` 里的任意一行当"错误详情"，于是把一条
//      "新文件 … tv06-error67-text-r1-tv06-error67-text.txt" 当成了失败原因——这比空白更有害。
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PCMig.Core.Transfer;

namespace PCMig.Core.Report;

/// <summary>
/// 一条失败证据行。<paramref name="Raw"/> 是剪裁后的原文（供人工核对），
/// <paramref name="Code"/> 是错误码（可能为空，例如汇总表行），<paramref name="Path"/> 是能抠出来的路径（可能为空）。
/// </summary>
public sealed record FailureEvidenceLine(string Kind, string Code, string Path, string Raw);

/// <summary>错误码直方图（码 → 去重后的出现次数）。</summary>
public sealed record FailureEvidenceCode(string Code, int Count);

/// <summary>
/// 单个迁移对象的失败证据（采集结果）。字段全部来自磁盘上已有文件，纯读；
/// <see cref="TotalLines"/> / <see cref="ErrorLineCount"/> 用来区分"日志里根本没有错误行"和"有但被截断"。
/// </summary>
public sealed record FailureEvidence(
    string ObjectId,
    string LogFileName,
    bool LogFound,
    long LogBytes,
    int TotalLines,
    int ErrorLineCount,
    bool Truncated,
    int DroppedLines,
    IReadOnlyList<FailureEvidenceLine> Lines,
    IReadOnlyList<FailureEvidenceCode> Codes,
    string EncodingLabel,
    string? ReadError,
    string? HintPath,
    int HintMentions,
    IReadOnlyList<FailureEvidenceLine> HintLines)
{
    /// <summary>
    /// 是否有<b>文件级</b>证据行可展示。注意：汇总表行不算 —— 真机上"日志里一条错误行都没有、
    /// 只有汇总表的『失败 1』"是常态，那种情况必须走 <see cref="NoEvidenceText"/> 的诚实说明，
    /// 不能因为汇总表里有行就假装有证据。
    /// </summary>
    public bool HasEvidence => ErrorLineCount > 0;

    /// <summary>读取失败（权限/占用）时的说明；正常为 null。</summary>
    public bool HasReadError => !string.IsNullOrEmpty(ReadError);

    /// <summary>
    /// 任务记录里给出的"原因路径"在日志里出现过 ⇒ 可以据此判断它到底是不是错误行。
    /// 真机教训：回执里那条"原因"是 robocopy 的『新文件』状态行，被当成失败原因展示，纯属误导。
    /// </summary>
    public bool HintAppearsInLog => HintMentions > 0;

    /// <summary>该路径在日志里出现、但没有任何一条是错误行 ⇒ 那条"原因"没有证据效力（报告必须这么写）。</summary>
    public bool HintIsNotAnError => HintMentions > 0 && Lines.All(l => !string.Equals(l.Path, HintPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 没有白名单行时给用户看的**诚实**说明。绝不编造示例、绝不把普通行当失败原因。
    /// </summary>
    public string NoEvidenceText()
    {
        if (!LogFound)
        {
            return "这条通道的 robocopy 日志文件不存在（任务可能还没跑到该通道，或日志已被清理），"
                + "因此无法列举单文件级证据。";
        }

        if (HasReadError)
        {
            return $"robocopy 日志存在但读不出来（{ReadError}），因此无法列举单文件级证据。";
        }

        var head = $"这份 robocopy 日志共 {TotalLines:N0} 行，其中没有一条文件级错误行。";
        return head
            + "退出码里的失败位来自 robocopy 汇总表的『失败』计数（多为目录级失败），"
            + "单文件级原因没有被写进日志 —— 所以这里不再举例子（旧版本会随手抓一条普通行当原因，那是误导）。"
            + "要看现场请直接打开该日志：任务目录下的 logs\\robocopy\\。";
    }
}

/// <summary>
/// 失败证据采集器：只读解析 <c>logs\robocopy\{objectId}.log</c>。
/// 所有解析都建立在<b>白名单正则</b>上（复用 <see cref="RobocopyRunner"/> 里同一套文件级错误解析，
/// 保证"报告里说的"和"入账时用的"是同一段代码产出的）。
/// </summary>
public static class FailureEvidenceCollector
{
    /// <summary>单对象最多保留多少条证据行（与 <see cref="RobocopyRunner"/> 的 500 行上限同量级，只用于展示）。</summary>
    public const int MaxLinesPerObject = 200;

    /// <summary>汇总表最多保留多少行（robocopy 的 总数/复制/跳过/不匹配/失败 表格）。</summary>
    private const int MaxSummaryRows = 12;

    /// <summary>单行原文剪裁上限（防御被撑爆的日志行）。</summary>
    private const int MaxLineChars = 400;

    /// <summary>与"任务记录里的原因路径"相关的上下文行最多保留多少条。</summary>
    private const int MaxHintLines = 5;

    /// <summary>嗅探编码时最多读多少字节。</summary>
    private const int SniffBytes = 64 * 1024;

    /// <summary>robocopy 汇总表表头（中文/英文）。命中后紧接着的若干行就是"失败 N"的出处。</summary>
    private static readonly Regex s_summaryHeaderRx = new(
        @"(?:总数|总计|Total)\D+(?:复制|Copied)\D+(?:跳过|Skipped)\D+(?:不匹配|Mismatch)\D+(?:失败|FAILED)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>带错误码但不带文件路径的行（例如"错误: 超过重试限制。"之外的单码行）。</summary>
    private static readonly Regex s_codeOnlyRx = new(
        @"(?:错误|ERROR)\s+(?<code>\d+)|0x(?<hex>[0-9A-Fa-f]{8})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>疑似凭据的行一律不进报告（报告是给用户看的，可能被转发）。</summary>
    private static readonly Regex s_secretRx = new(
        @"(?:password|passwd|pwd\s*=|密码|凭据|credential|/user:)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// v0.5.3：疑似含凭据的文本判定。报告与界面**共用同一口径** —— 两处各自写正则必然漏掉一处。
    /// 命中者一律不进报告 / 不进"复制详情"文本。
    /// </summary>
    public static bool LooksSensitive(string? text) => !string.IsNullOrEmpty(text) && s_secretRx.IsMatch(text);

    static FailureEvidenceCollector()
    {
        // 使 GBK/OEM 代码页可用（真机 robocopy 日志实测是 GBK/CP936，不是 UTF-8，也不是 /UNILOG 的 UTF-16）
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>按对象 id 找 robocopy 日志；找不到返回 null（调用方据此走"无证据"分支，不抛异常）。</summary>
    public static string? ResolveLogPath(string? robocopyLogsDir, string objectId)
    {
        if (string.IsNullOrWhiteSpace(robocopyLogsDir) || string.IsNullOrWhiteSpace(objectId)) return null;

        var direct = Path.Combine(robocopyLogsDir, objectId + ".log");
        if (File.Exists(direct)) return direct;

        // 大小写/后缀差异也认（历史作业里出现过同秒重名，名字可能不是精确的 "{objectId}.log"）
        try
        {
            foreach (var candidate in Directory.EnumerateFiles(robocopyLogsDir, "*.log"))
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(candidate), objectId, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }
        catch (Exception)
        {
            // 目录不存在 / 无权限：没有证据就说没有，绝不抛给上层（报告生成不能因为缺日志而失败）
        }

        return null;
    }

    /// <summary>
    /// 采集一个对象的失败证据。<paramref name="maxLines"/> 只影响"保留多少行"，
    /// <see cref="FailureEvidence.TotalLines"/> 与 <see cref="FailureEvidence.ErrorLineCount"/> 始终是真实全量计数。
    /// </summary>
    /// <param name="hintPath">
    /// 可选的"任务记录里给出的原因路径"（回执 ErrorDetail 里抠出来的那条）。采集器会在日志里找它、
    /// 如实报告它出现几次、出现的是什么行 —— 真机上那条路径在日志里全是『新文件』状态行，
    /// 靠这个字段报告才能理直气壮地说"这条原因不是错误行"。
    /// </param>
    public static FailureEvidence Collect(
        string? robocopyLogsDir,
        string objectId,
        string? hintPath = null,
        int maxLines = MaxLinesPerObject)
    {
        var logPath = ResolveLogPath(robocopyLogsDir, objectId);
        if (logPath is null)
        {
            return new FailureEvidence(objectId, objectId + ".log", false, 0, 0, 0, false, 0,
                Array.Empty<FailureEvidenceLine>(), Array.Empty<FailureEvidenceCode>(), "未知", null,
                hintPath, 0, Array.Empty<FailureEvidenceLine>());
        }

        long logBytes = 0;
        try { logBytes = new FileInfo(logPath).Length; } catch (Exception) { /* 读不到长度不影响证据 */ }

        var (encoding, encodingLabel) = SniffEncoding(logPath);
        var lines = new List<FailureEvidenceLine>();
        var hintLines = new List<FailureEvidenceLine>();
        var hintMentions = 0;
        var needle = HintNeedle(hintPath);
        var codeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // 同一路径同一码的重试行只算一次
        var totalLines = 0;
        var errorLineCount = 0;
        var dropped = 0;
        var truncated = false;
        var summaryRemaining = 0;
        string? readError = null;

        void Add(string kind, string code, string path, string raw)
        {
            if (lines.Count >= maxLines)
            {
                truncated = true;
                dropped++;
                return;
            }

            if (s_secretRx.IsMatch(raw))
            {
                dropped++;   // 疑似凭据：不展示（也不计入 truncated，避免让用户以为只是没显示全）
                return;
            }

            lines.Add(new FailureEvidenceLine(kind, code, Clip(path), Clip(raw)));
        }

        void Bump(string code)
        {
            if (code.Length == 0) return;
            codeCounts[code] = codeCounts.TryGetValue(code, out var c) ? c + 1 : 1;
        }

        try
        {
            foreach (var rawLine in File.ReadLines(logPath, encoding))
            {
                totalLines++;
                var line = rawLine.TrimEnd();

                // "任务记录里的原因路径"逐行对账：只统计、只留少量上下文，不影响下面的白名单判定。
                if (needle.Length > 0 && line.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    hintMentions++;   // 计数如实：这条路径在日志里确实出现过几次
                    if (hintLines.Count < MaxHintLines)
                    {
                        // v0.5.3：hint 上下文行走的是 hintLines，不经过 Add() —— 疑似凭据必须在这里也拦住，
                        // 否则同一条带 /user: 或 password 的行会从这条支路漏进报告。内容不留，计数照记。
                        if (s_secretRx.IsMatch(line)) dropped++;
                        else hintLines.Add(new FailureEvidenceLine("hint", "", hintPath ?? string.Empty, Clip(line)));
                    }
                }

                if (summaryRemaining > 0)
                {
                    if (line.Length == 0) summaryRemaining = 0;
                    else { Add("summary", "", "", line); summaryRemaining--; }
                    continue;
                }

                if (s_summaryHeaderRx.IsMatch(line))
                {
                    // 汇总表是"退出码 9 但没有任何错误行"这类现场的**唯一**证据来源：失败计数就在这里。
                    summaryRemaining = MaxSummaryRows;
                    Add("summary", "", "", line);
                    continue;
                }

                if (RobocopyRunner.TryParseFileErrorLine(line, out var fileCode, out var sourcePath))
                {
                    errorLineCount++;
                    if (seenKeys.Add(fileCode + "|" + sourcePath)) Bump(fileCode);
                    Add("file", fileCode, sourcePath, line);
                    continue;
                }

                var m = s_codeOnlyRx.Match(line);
                if (m.Success && !IsRetryNoise(line))
                {
                    var code = m.Groups["code"].Success
                        ? m.Groups["code"].Value
                        : Convert.ToInt32(m.Groups["hex"].Value, 16).ToString(CultureInfo.InvariantCulture);
                    var path = RobocopyRunner.ExtractErrorPath(line) ?? string.Empty;
                    errorLineCount++;
                    if (seenKeys.Add(code + "|" + path)) Bump(code);
                    Add("code", code, path, line);
                }
            }
        }
        catch (Exception ex)
        {
            readError = ex.GetType().Name + ": " + ex.Message;
        }

        var codes = codeCounts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new FailureEvidenceCode(kv.Key, kv.Value))
            .ToList();

        return new FailureEvidence(
            objectId,
            Path.GetFileName(logPath),
            true,
            logBytes,
            totalLines,
            errorLineCount,
            truncated,
            dropped,
            lines,
            codes,
            encodingLabel,
            readError,
            hintPath,
            hintMentions,
            hintLines);
    }

    /// <summary>
    /// 取"原因路径"的文件名部分当检索词（真机上回执里的路径可能被截断或是 UNC，按全路径匹配会漏）。
    /// 太短（&lt;4 字符）就不搜：那种词会命中一堆无关行，反而制造噪声。
    /// </summary>
    private static string HintNeedle(string? hintPath)
    {
        if (string.IsNullOrWhiteSpace(hintPath)) return string.Empty;
        var tail = hintPath.Trim().TrimEnd('。', '.', ')', '）');
        var cut = tail.LastIndexOf('\\');
        if (cut >= 0 && cut + 1 < tail.Length) tail = tail[(cut + 1)..];
        return tail.Length >= 4 ? tail : string.Empty;
    }

    /// <summary>"正在重试…"这类行只说明在重试，不是失败原因（错误码来自被重试的那条文件级错误行）。</summary>
    private static bool IsRetryNoise(string line)
        => line.Contains("正在重试", StringComparison.Ordinal) || line.Contains("Retrying", StringComparison.OrdinalIgnoreCase);

    private static string Clip(string s)
        => s.Length <= MaxLineChars ? s : s[..MaxLineChars] + "…";

    /// <summary>
    /// 嗅探日志编码。真机实测：<c>/UNILOG+</c> 生成的日志**不是** UTF-16，而是 GBK/CP936 单字节文本，
    /// 所以必须按字节判断，不能假设；判断不出来时退回系统 OEM 代码页（中文系统即 936）。
    /// </summary>
    private static (Encoding Encoding, string Label) SniffEncoding(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[SniffBytes];
            var read = fs.Read(head, 0, head.Length);

            if (read >= 2 && head[0] == 0xFF && head[1] == 0xFE) return (Encoding.Unicode, "utf-16le");
            if (read >= 2 && head[0] == 0xFE && head[1] == 0xFF) return (Encoding.BigEndianUnicode, "utf-16be");
            if (read >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) return (new UTF8Encoding(false), "utf-8(bom)");

            // 无 BOM：先严格试 UTF-8；注意样本末尾可能切断一个多字节字符，所以最多回退 3 个字节再试一次。
            var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
            for (var trim = 0; trim <= 3; trim++)
            {
                var len = read - trim;
                if (len <= 0) break;
                try
                {
                    strict.GetString(head, 0, len);
                    return (new UTF8Encoding(false), "utf-8");
                }
                catch (DecoderFallbackException)
                {
                    // 换更短的样本再试
                }
            }
        }
        catch (Exception)
        {
            // 读不出样本（占用/权限）⇒ 交给下面的 OEM 兜底
        }

        var oem = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
        try
        {
            return (Encoding.GetEncoding(oem), "oem-" + oem.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception)
        {
            return (Encoding.UTF8, "utf-8(兜底)");
        }
    }
}