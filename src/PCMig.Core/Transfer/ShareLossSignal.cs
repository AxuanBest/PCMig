using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PCMig.Core.Transfer;

/// <summary>
/// ★ P0-05（引擎侧结构化信号）★ 把"失败文本/退出码"翻译成会话层唯一听得懂的问题：
/// **这是不是"源共享整体不可用"，还是一个文件本身的问题？**
///
/// 为什么要单独一个类：现场（2026-10-10）共享被公司策略自动关闭，robocopy 一路报
/// <c>错误 67</c>，但界面底栏的源连接灯照旧是绿的 —— 因为**没有任何一层**把"引擎看见的网络错误"
/// 说出来。灯只认 <see cref="SourceShareFact"/> 形式的可达性证据，所以引擎必须给出结论化信号。
///
/// 语义边界（需求明令）：
///   · 网络/共享级错误 ⇒ <b>是</b>共享断开：53 找不到网络路径、55 网络资源不再可用、59 意外网络错误、
///     64 网络名不再可用、67 找不到网络名、1219 凭据冲突、1231 网络不可达、1236 本地连接被中断、
///     1326 登录失败/凭据已被撤销（策略关闭共享后重认证就是这个码）；
///   · 文件锁 32（共享冲突/他人占用）与 33（区域锁定）⇒ <b>不是</b>共享断开：那是**单个文件**的事，
///     把它当成"整条共享挂了"会把灯和用户的判断一起带偏（需求点名禁止）。
/// </summary>
public static class ShareLossSignal
{
    /// <summary>Win32 错误码是否属于"共享/网络级不可用"（⇒ 可点亮源失效）。</summary>
    /// <remarks>
    /// ★ Preview.4 真机（2026-10-22）★ 补入 <c>6</c>（ERROR_INVALID_HANDLE）：
    /// 现场 <c>JOB-20261022-102929-ab97</c> 的失败构成是「错误码分布 67×378、6×120」，
    /// 6 与 67 成对出现、描述的是同一件事（SMB 会话失效后 robocopy 拿到无效句柄继续访问树连接），
    /// 都属于"整条共享读不到"，不是单文件问题。个别真·句柄错误不会连续出现 24 次，
    /// 因此不会误触发断链熔断（熔断判据见 <c>TransferOrchestrator.ShareLossAbortAfter</c>）。
    /// </remarks>
    public static bool IsShareLossWin32(int code) => code switch
    {
        6 or 53 or 55 or 59 or 64 or 67 or 1219 or 1231 or 1236 or 1326 => true,
        _ => false,
    };

    /// <summary>Win32 错误码是否只是"某个文件/字节区被锁"（★ 绝不可当作共享断开 ★）。</summary>
    public static bool IsFileLockWin32(int code) => code is 32 or 33;

    private static readonly Regex s_codeRx = new(
        @"(?:错误|ERROR|error)\s*[:：]?\s*(?<code>\d{1,5})\b|0x(?<hex>[0-9A-Fa-f]{8})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] s_networkWords =
    [
        "网络名", "网络路径", "网络资源", "网络不可达", "网络连接", "找不到网络",
        "network name", "network path", "network resource", "network is unreachable",
        "network connection", "the specified network",
    ];

    /// <summary>
    /// 在任意一节失败文本里找**共享级**证据。返回 true 时给出错误码与一句可直接给用户看的证据文字。
    /// 文本里同时出现文件锁码（32/33）与网络码时，以网络码为准（网络码才是"共享怎么了"的答案）。
    /// </summary>
    public static bool TryMatchShareLossText(string? text, out int win32Code, out string evidence)
    {
        win32Code = 0;
        evidence = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var codes = new List<int>();
        foreach (Match m in s_codeRx.Matches(text))
        {
            if (m.Groups["hex"].Success)
            {
                if (int.TryParse(m.Groups["hex"].Value, System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var hex)) codes.Add(hex);
                continue;
            }
            if (int.TryParse(m.Groups["code"].Value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var dec)) codes.Add(dec);
        }

        var hit = codes.FirstOrDefault(IsShareLossWin32);
        if (hit == 0)
        {
            // 没有一个网络码：只有"网络名/网络路径…"这样的词也要认（有些 robocopy 版本只写文字）。
            var byWord = s_networkWords.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
            if (!byWord) return false;
            win32Code = 67;                                // 文字命中 ⇒ 按现场最常见的 67 归类（证据里说清是文字判据）
            evidence = "失败文本提到网络/共享不可用（文字判据）";
            return true;
        }

        win32Code = hit;
        evidence = $"Win32 {hit}（{ReasonFor(hit)}）";
        return true;
    }

    /// <summary>对一次 robocopy 运行的结果做结论化判定（末行 + 全部失败行都算证据）。</summary>
    public static bool TryMatchShareLoss(RobocopyRunResult r, out int win32Code, out string evidence)
    {
        win32Code = 0;
        evidence = string.Empty;
        if (TryMatchShareLossText(r.LastErrorLine, out var c1, out var e1))
        {
            win32Code = c1; evidence = $"robocopy 退出码 {r.ExitCode}：{e1}";
            return true;
        }
        foreach (var line in r.ErrorLines ?? [])
        {
            if (!TryMatchShareLossText(line, out var c2, out var e2)) continue;
            win32Code = c2; evidence = $"robocopy 退出码 {r.ExitCode}：{e2}";
            return true;
        }
        return false;
    }

    /// <summary>错误码的简短中文解释（只做展示，不参与判定）。</summary>
    public static string ReasonFor(int code) => code switch
    {
        53 => "找不到网络路径",
        55 => "网络资源不再可用",
        59 => "意外的网络错误",
        64 => "网络名不再可用",
        67 => "找不到网络名（共享已被关闭或改名）",
        1219 => "凭据冲突（同一服务器不允许两套凭据）",
        1231 => "网络不可达",
        1236 => "本地连接被中断",
        1326 => "登录失败（账号或密码已失效/策略拒绝）",
        32 => "文件被其它程序占用",
        33 => "文件区域被锁定",
        _ => "未归类错误",
    };
}