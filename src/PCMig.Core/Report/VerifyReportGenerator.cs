using System.Globalization;
using System.Text;
using PCMig.Core.Jobs;
using PCMig.Core.Models;
using PCMig.Core.State;
using PCMig.Core.Util;
using Serilog;

namespace PCMig.Core.Report;

/// <summary>
/// ★ UX-3 / UX-4 / m03985 执行令 ★ 验证报告：自包含、离线、可用系统默认浏览器打开的 HTML。
///
/// 与迁移报告的关系（★ UX-5 ★）：
///   · 视觉统一：共用 <see cref="ReportStyle.Css"/> 与同一套组件类名 ⇒ 两份报告像同一产品的产物；
///   · 事实各自记录：迁移报告讲「传了什么/失败在哪」，本报告讲「核对到了什么/没核对成什么」，
///     互不冒充——本报告**绝不**把 L1（文件数量与总字节对账）说成「完整内容哈希验证」。
///
/// 章节（m03985 执行令 00–10）：
///   00 页眉 / 01 人话结论 / 02 概览统计 / 03 环境档案 / 04 逐对象结果表 /
///   05 文件级差异表 / 06 验证阶段表 / 07 错误与建议 / 08 统计口径与限制（含字段来源表）/
///   09 关联迁移证据 / 10 视觉与导航（目录、可折叠、表头吸附、按需展示）。
///
/// ★ 铁律：报告只写**本次实测到的事实**。没有采集到的字段一律写「未采集」，
///   不推导、不编造、不把样例条数当总数（用户执行令 §02/§05/§08）。
/// </summary>
public sealed class VerifyReportGenerator
{
    /// <summary>引擎侧每个对象的差异样例上限（与 <c>Verifier.MaxMissingSamples</c> 一致；超出只计数列样例）。</summary>
    public const int EngineSampleLimit = 50;

    /// <summary>逐对象表首屏渲染行数（其余行按需展开，避免上千行 DOM 一次成型导致浏览器卡顿）。</summary>
    public const int ObjectRowInitialLimit = 200;

    /// <summary>环境档案里「用户显式选择」最多内联铺多少条（其余如实计数）。</summary>
    public const int SelectionListInlineLimit = 50;

    private readonly JobContext _ctx;
    private readonly ILogger _log;

    public VerifyReportGenerator(JobContext ctx, ILogger log)
    {
        _ctx = ctx;
        _log = log.ForContext<VerifyReportGenerator>();
    }

    /// <summary>
    /// 生成验证报告并返回文件路径。
    /// **没有验证报告时不编造一份**：返回 null（界面如实说「还没有验证报告，请先完成一次验证」）。
    /// 生成失败同样返回 null（调用方把本地路径/原因显示给用户，不假称打开成功）。
    /// </summary>
    public string? Generate()
    {
        var verify = _ctx.LoadVerify();
        if (verify is null)
        {
            _log.Warning("没有验证报告可生成（{Path}）：不生成空报告", _ctx.VerifyPath);
            return null;
        }

        // ★ 取消 / 失败的运行允许出「诚实的部分证据报告」（执行令数据契约：中断也必须有可审计记录）；
        //   但「没有任何逐对象结论、也不是取消/失败」的残缺记录仍然不出报告 —— 那才是真正的空报告。
        var runCanceledOrFailed = IsCanceledOrFailed(verify.RunStatus);
        if (verify.Objects.Count == 0 && !runCanceledOrFailed)
        {
            _log.Warning("验证报告里没有任何逐对象结论（{Path}，runStatus={Status}）：不生成空报告",
                _ctx.VerifyPath, verify.RunStatus ?? "未记录");
            return null;
        }

        var plan = LoadPlan();
        var planned = new Dictionary<string, PlannedObject>(StringComparer.OrdinalIgnoreCase);
        if (plan is not null)
            foreach (var o in plan.Objects) planned[o.ObjectId] = o;

        var sb = new StringBuilder(1 << 16);
        WriteHeader(sb, verify, plan);
        WriteHumanConclusion(sb, verify);
        WriteOverview(sb, verify, plan);
        WriteEnvironment(sb, verify, plan);
        WriteObjectTable(sb, verify, planned);
        WriteFileDiffTable(sb, verify);
        WriteStageTable(sb, verify);
        WriteErrorAdvice(sb, verify);
        WriteLimits(sb, verify);
        WriteRelatedEvidence(sb, verify);
        sb.Append(ReportStyle.FilterScript);
        sb.AppendLine("</body></html>");

        try
        {
            Directory.CreateDirectory(_ctx.ReportDir);
            // ★ 同一秒连续验证两份报告不得互相覆盖 ⇒ 文件名带运行号短码；先写临时文件再原子替换，
            //   避免「半写 HTML」被浏览器打开（用户执行令 §性能契约 5）。
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var runTag = ShortRunTag(verify.VerifyRunId);
            var path = Path.Combine(_ctx.ReportDir, $"verify-report-{stamp}{runTag}.html");
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
            _log.Information("验证报告已生成: {Path}（{Objects} 个对象，{Diffs} 条文件级差异）",
                path, verify.Objects.Count, verify.FileDiffs.Count);
            return path;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "验证报告生成失败（验证结果本身不受影响）：{Dir}", _ctx.ReportDir);
            return null;
        }
    }

    private static string ShortRunTag(string? runId)
        => string.IsNullOrEmpty(runId) || runId.Length < 6 ? "" : "-" + runId[^6..];

    private MigrationPlan? LoadPlan()
    {
        var plan = _ctx.Plan;
        if (plan is null && JsonStateStore.TryRead<MigrationPlan>(_ctx.PlanPath, out var loaded))
            plan = loaded;
        return plan;
    }

    // ═════════════════════════════ 00 页眉 ═════════════════════════════

    private void WriteHeader(StringBuilder sb, VerifyReport v, MigrationPlan? plan)
    {
        var local = v.VerifiedUtc.ToLocalTime();
        sb.AppendLine("<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        sb.AppendLine($"<title>PCMig 文件验证报告 · {ReportStyle.E(v.JobId)}</title>");
        sb.AppendLine(ReportStyle.Css);
        sb.AppendLine("</head><body>");
        sb.AppendLine("<h1>PCMig 文件验证报告</h1>");
        sb.AppendLine("<p class=\"note\">本报告由 PCMig 在<b>本机</b>生成、完全自包含、可离线打开；" +
                      "不上传任何网络位置，也不含口令/令牌/认证票据。" +
                      "<b>验证报告与迁移报告是两份不同的业务事实</b>：迁移报告记录「传了什么」，" +
                      "本报告记录「核对到了什么、没核对成什么」。</p>");

        sb.AppendLine("<h2 id=\"s00\">00 · 本次验证的基本信息</h2>");
        sb.AppendLine("<table><tbody>");
        Row(sb, "任务号（JobId）", Mono(v.JobId));
        Row(sb, "验证运行号（VerifyRunId）",
            v.VerifyRunId is null ? NotCollected("旧报告没有运行号") : Mono(v.VerifyRunId));
        Row(sb, "验证级别", ReportStyle.E(DescribeLevel(v.Level)));
        Row(sb, "验证完成时刻", $"{local:yyyy-MM-dd HH:mm:ss}（本地时间）　·　{v.VerifiedUtc:yyyy-MM-dd HH:mm:ss}Z");
        Row(sb, "报告生成时刻", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}（本地时间）");
        Row(sb, "软件构建版本", v.ToolVersion is null ? NotCollected("旧报告没有记录构建版本") : Mono(v.ToolVersion));
        Row(sb, "总体结论（业务）", v.OverallPass
            ? Ok("通过（仅代表已核对项一致）")
            : IsCanceledOrFailed(v.RunStatus)
                ? Bad("未通过（本次运行已取消 / 未跑完：没有完整结论）")
                : Bad("未通过"));
        Row(sb, "证据完整性（统计口径）", CompletenessTag(v.EvidenceCompleteness) +
            (v.EvidenceNote is null ? "" : "　<span class=\"note\">" + ReportStyle.E(v.EvidenceNote) + "</span>"));
        Row(sb, "计划对象数 / 已验证对象数",
            $"{plan?.Objects.Count.ToString(CultureInfo.InvariantCulture) ?? NotCollected("无计划文件")}　/　{v.Objects.Count}");
        sb.AppendLine("</tbody></table>");
        sb.AppendLine("<p class=\"note\"><b>一句话</b>：验证进度 100% 只表示「检查过程走完」，不代表源与目标必然一致；" +
                      "L1 的文件数/字节一致也<b>不等于</b>逐文件 SHA-256 内容一致。</p>");

        sb.AppendLine("<h3>目录</h3><ol class=\"note\">");
        foreach (var entry in new[]
        {
            ("s01", "人话结论"), ("s02", "概览统计"), ("s03", "环境档案"), ("s04", "逐对象结果表"),
            ("s05", "文件级差异表"), ("s06", "验证阶段与耗时"), ("s07", "错误与建议"),
            ("s08", "统计口径与限制（含字段来源）"), ("s09", "关联迁移证据"),
        })
            sb.AppendLine($"<li><a href=\"#{entry.Item1}\">{entry.Item2}</a></li>");
        sb.AppendLine("</ol>");
    }

    // ═════════════════════════════ 01 人话结论 ═════════════════════════════

    /// <summary>
    /// 01 · 结论（人话）。
    ///
    /// ★ v0.5.4 Preview.2（2026-10-16 真机 JOB-20261016-150319-e738）★
    /// 旧版本的这一段只写「L1 文件数/字节对账未通过：下方逐对象表列出了…」——
    /// 用户必须自己去 04/05 表里拼 object-000010 + MISMATCH + sourceFiles 10/targetFiles 9
    /// 才能知道**究竟是哪个文件少了一个**。真机上那个文件叫 DumpStack.log、8 KB、
    /// 原因是源侧拒绝访问（Windows 错误 5）。
    /// 新口径：**第一屏就必须回答「哪个盘/哪个文件夹 → 哪个文件 → 出了什么问题」**，
    /// 且只依据报告里真实存在的证据（FileDiffRecord / 对象状态 / 枚举失败），
    /// 没有证据的原因一律写「原因尚未确定」，绝不猜测。
    /// </summary>
    private static void WriteHumanConclusion(StringBuilder sb, VerifyReport v)
    {
        var ok = v.Objects.Count(o => o.Status == "OK");
        var mismatch = v.Objects.Count(o => o.Status == "MISMATCH");
        var unknown = v.Objects.Count(o => o.Status == "UNKNOWN");
        var canceled = v.Objects.Count(o => o.Status == "CANCELED");
        var emptyObjects = v.Objects.Count(o => o.Status == "EMPTY");
        var srcFiles = v.Objects.Sum(o => o.SourceFiles);
        var dstFiles = v.Objects.Sum(o => o.TargetFiles);
        var srcBytes = v.Objects.Sum(o => o.SourceBytes);
        var dstBytes = v.Objects.Sum(o => o.TargetBytes);

        sb.AppendLine("<h2 id=\"s01\">01 · 结论（人话）</h2>");
        sb.AppendLine("<div class=\"card\">");
        var levelText = v.Level == VerifyLevel.L1_CountSize ? "L1 文件数/字节对账" : "L2 抽样哈希核对";
        if (IsCanceledOrFailed(v.RunStatus))
        {
            sb.AppendLine($"<p><span class=\"bad\">{levelText}未完成（本次运行已取消 / 未跑完）</span>：" +
                          "<b>没有完整结论</b>。下面的数字只覆盖取消前已经核对到的对象；" +
                          "「没有结论」既不是通过，也不是失败。</p>");
        }
        else if (v.Level == VerifyLevel.L1_CountSize)
        {
            sb.AppendLine(v.OverallPass
                ? "<p><span class=\"ok\">L1 文件数/字节对账通过</span>：本轮核对的是<b>文件数量与总字节</b>，<b>没有</b>做全文件内容哈希。</p>"
                : "<p><span class=\"bad\">L1 文件数/字节对账未通过</span>：下方逐对象表列出了每个对象的两侧数量/字节与差异。</p>");
        }
        else
        {
            sb.AppendLine(v.OverallPass
                ? "<p><span class=\"ok\">L2 抽样哈希核对通过</span>：抽样范围内未发现内容不一致；<b>未抽到的文件没有内容级证据</b>。</p>"
                : "<p><span class=\"bad\">L2 抽样哈希核对未通过</span>：抽样中发现了内容或存在性差异。</p>");
        }

        // ═══ 首屏「哪个盘 → 哪个文件夹 → 哪个文件 → 什么问题」═══
        WritePlainLanguageVerdict(sb, v, srcFiles, dstFiles, srcBytes, dstBytes);

        sb.AppendLine($"<p>共 <b>{v.Objects.Count}</b> 个对象：通过 <b class=\"ok\">{ok}</b>，" +
                      $"存在差异 <b class=\"bad\">{mismatch}</b>，无法确认 <b class=\"warn\">{unknown}</b>" +
                      (emptyObjects > 0 ? $"，计划与实测都为空 <b>{emptyObjects}</b>（这些对象两侧都没有文件，<b>不算「传成功」</b>）" : "") +
                      (canceled > 0 ? $"，已取消 <b>{canceled}</b>" : "") + "。</p>");

        // ★ 取消/中断不是「通过」，也不是「失败」：必须单列一句，且说明这些数字只覆盖取消前的部分对象。
        if (canceled > 0 || string.Equals(v.RunStatus, "canceled", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("<p class=\"bad\">本次验证<b>已取消或未跑完</b>：没有完整结论（<b>取消不等于通过</b>）。" +
                          "下面的数字只覆盖取消前已经完成核对的对象，未核对到的对象本轮<b>没有</b>任何结论。</p>");
        }
        sb.AppendLine($"<p>文件数：源 {srcFiles:N0} / 目标 {dstFiles:N0}（差 {Signed(srcFiles - dstFiles)}）；" +
                      $"总字节：源 {Format.Bytes(srcBytes)} / 目标 {Format.Bytes(dstBytes)}" +
                      $"（差 {Signed(dstBytes - srcBytes)} 字节）。</p>");

        if (v.Level >= VerifyLevel.L2_SampleHash)
        {
            var sampled = v.Objects.Sum(o => o.HashSampled);
            sb.AppendLine($"<p>内容级抽样：已核对 <b>{sampled:N0}</b> 个文件" +
                          $"（抽样比例 {v.SampleHashPercent?.ToString(CultureInfo.InvariantCulture) ?? "未采集"}%），" +
                          $"其中哈希不一致 <b class=\"bad\">{v.Objects.Sum(o => o.HashMismatched)}</b> 个；" +
                          "其余文件本轮<b>没有</b>内容级证据。</p>");
        }
        else
        {
            sb.AppendLine("<p>内容级抽样：<b>本轮未执行</b>（L1 只对账文件数量与总字节）。</p>");
        }

        sb.AppendLine("<h3>下一步该怎么做</h3><ul>");
        if (mismatch > 0)
            sb.AppendLine("<li>先在 05 表看差异类型：<b>确证缺失 / 大小不同 / 哈希不同</b>才是「目标侧真的少了或不一样」；" +
                          "若差异类型是「目标多余」，那是目标侧多出来的文件，本工具<b>不会</b>自动清理。</li>");
        if (unknown > 0)
            sb.AppendLine("<li><b>无法确认不是缺失</b>：先确认源共享是否可达、凭据是否仍有效、目录是否枚举完整（见 07 表），" +
                          "恢复可达后<b>重新验证一次</b>，不要在无法确认的状态下执行修复。</li>");
        if (v.Objects.Any(o => o.EnumerationSkipped > 0))
            sb.AppendLine("<li>本次有对象的枚举被跳过（权限/竞态/瞬态 IO）⇒ 这些对象既不能算通过，" +
                          "也不能把没出现的文件算成缺失。</li>");
        sb.AppendLine("<li>需要「迁移过程」的证据（传了哪些文件、robocopy 退出码、失败重试）请看同目录的<b>迁移报告</b>（见 09）。</li>");
        sb.AppendLine("</ul></div>");
    }

    /// <summary>
    /// 首屏「人话判词」：把技术字段翻译成「哪个盘 → 哪个文件夹 → 哪个文件 → 什么问题 → 是否可信」。
    ///
    /// 纪律：
    ///   · 每个事实都指向报告里真实存在的字段（FileDiffRecord / ObjectVerifyResult.Status / EnumerationFailure）；
    ///   · 拿不到错误码/原因时如实写「原因尚未确定」，**绝不**从上下文猜一个看起来合理的理由；
    ///   · 明确区分「迁移过程失败」与「本次对账才发现缺失」——两者证据来源不同，
    ///     报告只陈述自己手里的事实，迁移侧的原因由迁移报告负责（并给出交叉引用）。
    /// </summary>
    private static void WritePlainLanguageVerdict(StringBuilder sb, VerifyReport v,
        long srcFiles, long dstFiles, long srcBytes, long dstBytes)
    {
        var missing = v.FileDiffs.Where(d => d.DiffKind == "missing-on-target").ToList();
        var extra = v.FileDiffs.Where(d => d.DiffKind == "extra-on-target").ToList();
        var sizeBad = v.FileDiffs.Where(d => d.DiffKind == "size-mismatch").ToList();
        var hashBad = v.FileDiffs.Where(d => d.DiffKind == "hash-mismatch").ToList();
        var unreadable = v.FileDiffs.Where(d => d.DiffKind == "unreadable").ToList();
        var badObjects = v.Objects.Where(o => o.Status is "MISMATCH" or "UNKNOWN").ToList();

        // ── 结论句 ──
        var (srcLabel, dstLabel) = DescribeEndpoints(v);
        if (v.OverallPass)
        {
            sb.AppendLine($"<p><span class=\"ok\">迁移结果：本次核对通过。</span>" +
                          $"源位置 <b class=\"mono\">{ReportStyle.E(srcLabel)}</b> → 目标位置 " +
                          $"<b class=\"mono\">{ReportStyle.E(dstLabel)}</b>；" +
                          $"文件数 {srcFiles:N0} → {dstFiles:N0}，总字节 {Format.Bytes(srcBytes)} → {Format.Bytes(dstBytes)}，" +
                          "两侧一致。<b>注意</b>：一致的是<b>文件数量与总字节</b>，不等于每个文件内容都逐字节相同。</p>");
            return;
        }
        if (IsCanceledOrFailed(v.RunStatus)) return;   // 取消/未跑完：上一段已如实说明，不在此处下结论

        sb.AppendLine($"<p><span class=\"bad\">迁移结果：没有完整完成。</span>" +
                      $"源位置 <b class=\"mono\">{ReportStyle.E(srcLabel)}</b> → 目标位置 " +
                      $"<b class=\"mono\">{ReportStyle.E(dstLabel)}</b>。" +
                      $"核对到 <b>{badObjects.Count}</b> 个对象存在问题：" +
                      $"{string.Join("、", badObjects.Take(8).Select(o => $"<span class=\"mono\">{ReportStyle.E(o.ObjectId)}</span>（{ReportStyle.E(o.StatusText)}）"))}" +
                      (badObjects.Count > 8 ? $" 等 {badObjects.Count} 个" : "") + "。</p>");

        // ── 具体到文件：逐条列出「文件夹 → 文件 → 大小 → 原因」──
        var concrete = missing.Concat(sizeBad).Concat(hashBad).Concat(unreadable).Concat(extra).ToList();
        if (concrete.Count == 0)
        {
            // 有对象不通过，却没有任何文件级记录：必须如实说明"不知道是哪个文件"，不编造。
            sb.AppendLine("<p>本次<b>没有记录到具体是哪个文件出了问题</b>（报告里没有该对象的文件级差异条目）。" +
                          "请看 04 表该对象的计数与「无法确认」的原因码，或重新验证一次以获得文件级证据。" +
                          "<b>这不等于没有问题，只等于本次没有拿到文件级证据。</b></p>");
            return;
        }

        sb.AppendLine("<h3>具体是哪些文件</h3>");
        sb.AppendLine("<p class=\"note\">下表按「哪个文件夹 → 哪个文件 → 多大 → 出了什么问题」逐条列出；" +
                      "只列报告里<b>真实记录到</b>的条目" +
                      (v.FileDiffsTruncated ? "（差异条目已达记录上限，超出的只体现在 04 表的计数里）" : "") + "。</p>");
        sb.AppendLine("<table><thead><tr><th>文件夹（目标侧）</th><th>文件</th><th>大小</th>" +
                      "<th>问题</th><th>原始错误 / 错误码</th><th>原因确定程度</th></tr></thead><tbody>");
        foreach (var d in concrete.Take(50))
        {
            var folder = FolderOf(d.TargetPath);
            var name = string.IsNullOrEmpty(d.RelativePath) ? FileNameOf(d.TargetPath) : d.RelativePath;
            var size = d.SourceLength >= 0 ? d.SourceLength : d.TargetLength;
            sb.AppendLine("<tr>" +
                          $"<td class=\"mono\">{ReportStyle.E(folder)}</td>" +
                          $"<td class=\"mono\">{ReportStyle.E(name)}</td>" +
                          $"<td>{(size < 0 ? "—" : Format.Bytes(size) + $"<br><span class=\"note\">{size:N0} 字节</span>")}</td>" +
                          $"<td>{DiffKindTag(d.DiffKind)}</td>" +
                          $"<td>{ReportStyle.E(d.Win32Code ?? (d.Detail is null ? "报告里没有记录错误码" : d.Detail))}</td>" +
                          $"<td>{CertaintyForDiff(d)}</td>" +
                          "</tr>");
        }
        if (concrete.Count > 50)
            sb.AppendLine($"<tr><td colspan=\"6\"><span class=\"note\">…另有 {concrete.Count - 50} 条同类条目，见 05 表完整清单。</span></td></tr>");
        sb.AppendLine("</tbody></table>");

        // ── 五个问题分开回答（用户任务令 §三要求）──
        sb.AppendLine("<h3>五个问题分别回答</h3><table><thead><tr><th>问题</th><th>结论</th><th>依据</th></tr></thead><tbody>");
        sb.AppendLine("<tr><td>① 迁移过程是否失败？</td><td>" +
                      "见同目录的<b>迁移报告</b>与 09 表（本报告只负责核对结果，不代替迁移过程的取证）" +
                      "</td><td>迁移报告 / receipts / robocopy 退出码</td></tr>");
        sb.AppendLine("<tr><td>② 本次对账是否发现缺失？</td><td>" +
                      (missing.Count > 0
                          ? $"<b class=\"bad\">是</b>：{missing.Count} 条「源有目标无」的文件级记录" +
                            (missing.Count > 0 && v.Objects.Any(o => o.MissingConfirmed) ? "（已确证）" : "")
                          : (v.Objects.Any(o => o.Status == "UNKNOWN")
                              ? "<b class=\"warn\">未能确认</b>：有对象无法确认，详见 04 表原因码"
                              : "否，未发现「源有目标无」的文件级记录"))
                      + "</td><td>05 表 / FileDiffRecord(diffKind=missing-on-target)</td></tr>");
        sb.AppendLine("<tr><td>③ 内容级 SHA-256 是否实际执行？</td><td>" +
                      (v.Level >= VerifyLevel.L2_SampleHash
                          ? $"<b>已执行抽样</b>：核对 {v.Objects.Sum(o => o.HashSampled):N0} 个文件" +
                            $"（抽样比例 {v.SampleHashPercent?.ToString(CultureInfo.InvariantCulture) ?? "未采集"}%），" +
                            $"不一致 {v.Objects.Sum(o => o.HashMismatched)} 个；<b>未抽到的文件仍无内容级证据</b>"
                          : "<b class=\"warn\">未执行</b>：本轮的验证级别是 " + ReportStyle.E(DescribeLevel(v.Level)) +
                            "，只对账文件数量与总字节")
                      + "</td><td>01 表「验证级别」/ 06 表内容级阶段耗时</td></tr>");
        sb.AppendLine("<tr><td>④ 数量/字节对账是否通过？</td><td>" +
                      $"文件数：源 {srcFiles:N0} / 目标 {dstFiles:N0}（差 {Signed(srcFiles - dstFiles)}）；" +
                      $"总字节：源 {Format.Bytes(srcBytes)} / 目标 {Format.Bytes(dstBytes)}" +
                      $"（差 {Signed(dstBytes - srcBytes)} 字节）⇒ " +
                      (srcFiles == dstFiles && srcBytes == dstBytes ? "<b class=\"ok\">一致</b>" : "<b class=\"bad\">不一致</b>") +
                      "</td><td>02 表概览统计</td></tr>");
        sb.AppendLine("<tr><td>⑤ 最终整体是否可以称为「完整」？</td><td>" +
                      (v.OverallPass
                          ? "<b class=\"ok\">在本次核对口径内可以</b>（仅代表已核对项一致）"
                          : "<b class=\"bad\">不可以</b>：存在缺失/差异或无法确认的对象")
                      + "</td><td>01 表「总体结论（业务）」</td></tr>");
        sb.AppendLine("</tbody></table>");
    }

    /// <summary>把「哪个盘 / 哪个共享」翻译成人话端点。</summary>
    private static (string Source, string Target) DescribeEndpoints(VerifyReport v)
    {
        var src = string.IsNullOrWhiteSpace(v.SourceHost)
            ? "（报告未记录源位置）"
            : (v.SelectedPaths.Count > 0 ? v.SelectedPaths[0] : v.SourceHost!);
        var dst = string.IsNullOrWhiteSpace(v.TargetRoot) ? "（报告未记录目标位置）" : v.TargetRoot!;
        return (src, dst);
    }

    /// <summary>目标路径的所在文件夹（去掉文件名）；拿不到就如实返回原串。</summary>
    private static string FolderOf(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return "—";
        try
        {
            var dir = Path.GetDirectoryName(targetPath);
            return string.IsNullOrEmpty(dir) ? targetPath : dir;
        }
        catch { return targetPath; }
    }

    private static string FileNameOf(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return "—";
        try { return Path.GetFileName(targetPath); } catch { return targetPath; }
    }

    /// <summary>
    /// 原因确定程度。**这是本轮最重要的诚实性字段**：只有报告里真的有错误码/证据文本才敢说"已确定"，
    /// 否则一律「原因尚未确定」，让用户知道该去别处取证，而不是接受一个猜出来的理由。
    ///
    /// ★ v0.5.4 Preview.2 真机修复（2026-10-16，用户任务令 §三）★
    ///   必须把两件事**分开陈述**，因为它们的确定程度完全不同：
    ///     ① 「文件确实缺失」——本报告用枚举比较**已确证**；
    ///     ② 「为什么没复制成功」——本报告**没有**该文件的迁移期错误码 ⇒ **原因尚未确定**。
    ///   旧写法对 missing-on-target 只写「部分确定」，于是**真机报告里根本不出现「原因尚未确定」**
    ///   这七个字（真机 verify-report.json 的 fileDiffs[0].win32Code 为 null，
    ///   错误码 5 只存在于迁移侧的 robocopy 日志与 receipt 里）。
    ///   用户看到的是"部分确定"，很容易理解成"原因大体知道了"，与事实不符。
    ///   现在：有错误码 ⇒ 已确定；没有错误码 ⇒ 明确写出「原因尚未确定」并指向迁移报告。
    /// </summary>
    private static string CertaintyForDiff(FileDiffRecord d)
    {
        if (!string.IsNullOrWhiteSpace(d.Win32Code))
            return "<b class=\"ok\">已确定</b><br><span class=\"note\">有 Win32 错误码</span>";
        if (!string.IsNullOrWhiteSpace(d.Detail) && d.DiffKind is "hash-mismatch" or "size-mismatch")
            return "<b class=\"ok\">已确定</b><br><span class=\"note\">有两侧实测值</span>";
        if (d.DiffKind == "missing-on-target")
            return "<b class=\"warn\">缺失已确证；原因尚未确定</b><br><span class=\"note\">" +
                   "本报告用枚举比较确认了<b>目标侧确实没有该文件</b>；但<b>为什么</b>没复制成功，" +
                   "本报告没有记录该文件的迁移期错误码 ⇒ <b>原因尚未确定</b>，" +
                   "请到同目录的迁移报告 / 该对象的 robocopy 日志里取证。</span>";
        if (d.DiffKind == "extra-on-target")
            return "<b class=\"warn\">多余已确证；原因尚未确定</b><br><span class=\"note\">" +
                   "目标侧确实多出该文件；<b>来源未记录</b> ⇒ 原因尚未确定。</span>";
        return "<b class=\"warn\">原因尚未确定</b><br><span class=\"note\">报告里没有该文件的错误码或对照值</span>";
    }

    // ═════════════════════════════ 02 概览统计 ═════════════════════════════

    private static void WriteOverview(StringBuilder sb, VerifyReport v, MigrationPlan? plan)
    {
        var ok = v.Objects.Count(o => o.Status == "OK");
        var mismatch = v.Objects.Count(o => o.Status == "MISMATCH");
        var unknown = v.Objects.Count(o => o.Status == "UNKNOWN");
        var empty = v.Objects.Count(o => o.Status == "EMPTY");
        var canceledObjects = v.Objects.Count(o => o.Status == "CANCELED");
        var srcFiles = v.Objects.Sum(o => o.SourceFiles);
        var dstFiles = v.Objects.Sum(o => o.TargetFiles);
        var srcBytes = v.Objects.Sum(o => o.SourceBytes);
        var dstBytes = v.Objects.Sum(o => o.TargetBytes);

        sb.AppendLine("<h2 id=\"s02\">02 · 概览统计</h2>");
        sb.AppendLine("<div class=\"cards\">");
        Card(sb, v.Objects.Count.ToString(CultureInfo.InvariantCulture), "已验证对象数");
        Card(sb, plan?.Objects.Count.ToString(CultureInfo.InvariantCulture) ?? "未采集", "计划对象数");
        Card(sb, ok.ToString(CultureInfo.InvariantCulture), "通过");
        Card(sb, mismatch.ToString(CultureInfo.InvariantCulture), "存在差异");
        Card(sb, unknown.ToString(CultureInfo.InvariantCulture), "无法确认");
        Card(sb, empty.ToString(CultureInfo.InvariantCulture), "计划与实测都为空");
        if (canceledObjects > 0)
            Card(sb, canceledObjects.ToString(CultureInfo.InvariantCulture), "已取消（无结论）");
        sb.AppendLine("</div>");
        sb.AppendLine("<table><thead><tr><th>指标</th><th>值</th><th>说明</th></tr></thead><tbody>");
        MetricRow(sb, "源文件数", srcFiles.ToString("N0"), "引擎在源侧枚举到的文件数（排除策略生效后）");
        MetricRow(sb, "目标文件数", dstFiles.ToString("N0"), "引擎在目标侧枚举到的文件数");
        MetricRow(sb, "文件数差值", Signed(srcFiles - dstFiles), "正数 = 目标缺少；负数 = 目标多余（不自动清理）");
        MetricRow(sb, "源总字节", Format.Bytes(srcBytes), $"{srcBytes:N0} 字节");
        MetricRow(sb, "目标总字节", Format.Bytes(dstBytes), $"{dstBytes:N0} 字节");
        MetricRow(sb, "总字节差值", Signed(dstBytes - srcBytes),
            $"正数 = 目标偏大；负数 = 目标偏小（{dstBytes - srcBytes:N0} 字节）");
        MetricRow(sb, "哈希抽样数", v.Objects.Sum(o => o.HashSampled).ToString("N0"),
            v.Level >= VerifyLevel.L2_SampleHash
                ? $"抽样比例 {v.SampleHashPercent?.ToString(CultureInfo.InvariantCulture) ?? "未采集"}%"
                : "本轮未执行内容级抽样");
        MetricRow(sb, "哈希不一致数", v.Objects.Sum(o => o.HashMismatched).ToString("N0"), "抽样中内容不同的文件数");
        MetricRow(sb, "哈希读取失败数", v.Objects.Sum(o => o.HashReadFailed).ToString("N0"),
            "打不开/读不到 —— 与「内容不一致」是两回事");
        MetricRow(sb, "枚举跳过条目", v.Objects.Sum(o => o.EnumerationSkipped).ToString("N0"),
            "权限/竞态等导致没枚举到的条目；大于 0 时统计口径为 Partial");
        MetricRow(sb, "验证耗时",
            v.ElapsedMs is null ? NotCollected("旧报告没有耗时") : $"{v.ElapsedMs.Value / 1000.0:0.00} 秒",
            "引擎实测总耗时；分阶段耗时见 06 表");
        MetricRow(sb, "文件级差异记录", v.FileDiffs.Count.ToString("N0"),
            v.FileDiffsTruncated
                ? $"已达记录上限（{v.FileDiffs.Count} 条），超出的差异只体现在逐对象计数里"
                : "本报告记录的全部文件级差异");
        sb.AppendLine("</tbody></table>");
    }

    // ═════════════════════════════ 03 环境档案 ═════════════════════════════

    private void WriteEnvironment(StringBuilder sb, VerifyReport v, MigrationPlan? plan)
    {
        sb.AppendLine("<h2 id=\"s03\">03 · 环境档案</h2>");
        sb.AppendLine("<p class=\"note\">本表只记录本次验证实际依据的配置；没有记录的写「未采集」，不做任何推断。</p>");
        sb.AppendLine("<table><thead><tr><th>项目</th><th>内容</th><th>来源</th></tr></thead><tbody>");
        EnvRow(sb, "源主机", v.SourceHost is null ? NotCollected("旧报告没有记录") : Mono(v.SourceHost), "job.json");
        EnvRow(sb, "目标根", v.TargetRoot is null ? NotCollected("旧报告没有记录") : Mono(v.TargetRoot), "job.json");
        EnvRow(sb, "用户显式选择",
            v.SelectedPaths.Count == 0
                ? NotCollected("未记录（或本次由计划驱动）")
                : SelectionListText(v.SelectedPaths), "job.json");
        EnvRow(sb, "源到目标映射规则", MappingRuleText(v.MappingRule), "job.json（Options.SelectedFolderMapping）");
        EnvRow(sb, "验证级别口径",
            ReportStyle.E(DescribeLevel(v.Level)) +
            "<br><span class=\"note\">L1 = 文件数量与总字节对账；L2 = 在 L1 基础上做抽样 SHA-256</span>",
            "本次验证请求");
        EnvRow(sb, "内容级算法", v.HashAlgorithm ?? NotCollected("本轮未做内容级核对"),
            v.HashAlgorithm is null ? "—" : "引擎（SHA256.HashDataAsync）");
        EnvRow(sb, "抽样百分比",
            v.SampleHashPercent is null
                ? NotCollected("旧报告没有记录")
                : $"{v.SampleHashPercent}%（本次实际生效值）",
            "job.json（Options.SampleHashPercent）");
        EnvRow(sb, "策略排除规则",
            plan?.ExcludedByPolicy is { Count: > 0 } ex
                ? $"{ex.Count} 条：" + ReportStyle.E(string.Join("、", ex.Take(12))) + (ex.Count > 12 ? " …" : "")
                : NotCollected("没有计划文件或计划未记录排除名单"),
            "plan.json（ExcludedByPolicy）");
        EnvRow(sb, "云占位符与重解析点", "枚举时跳过占位符（不把在线占位符算成缺失）；重解析点不递归", "引擎枚举规则");
        EnvRow(sb, "验证开始与结束",
            v.StartedUtc is null || v.FinishedUtc is null
                ? NotCollected("旧报告没有记录起止时刻")
                : $"{v.StartedUtc.Value.ToLocalTime():HH:mm:ss} → {v.FinishedUtc.Value.ToLocalTime():HH:mm:ss}（本地时间）",
            "引擎实测");
        EnvRow(sb, "统计完整性",
            CompletenessTag(v.EvidenceCompleteness) +
            (v.EvidenceNote is null ? "" : "　" + ReportStyle.E(v.EvidenceNote)), "引擎统计");
        EnvRow(sb, "报告文件位置", Mono(_ctx.ReportDir), "本机任务工作区（报告不上传网络）");
        sb.AppendLine("</tbody></table>");
    }

    private static string MappingRuleText(string? rule) => rule switch
    {
        "contentDirect" => "contentDirect（选中文件夹时把<b>内容</b>直接放到目标，不再多套一层同名目录）",
        "keepFolder" => "keepFolder（选中文件夹时在目标下<b>保留</b>该文件夹名）",
        null => NotCollected("旧报告没有记录映射规则"),
        _ => ReportStyle.E(rule),
    };

    // ═════════════════════════════ 04 逐对象结果表 ═════════════════════════════

    private static void WriteObjectTable(StringBuilder sb, VerifyReport v, Dictionary<string, PlannedObject> planned)
    {
        sb.AppendLine("<h2 id=\"s04\">04 · 逐对象结果表</h2>");
        sb.AppendLine("<p class=\"note\">每个对象一行，不丢数据：可关键词过滤、可只看需要处理的对象、可点表头按差异量排序；" +
                      $"首屏只渲染前 {ObjectRowInitialLimit} 行，其余点「显示更多」按需展开（避免上千行 DOM 卡死浏览器）。</p>");
        sb.AppendLine("<div class=\"toolbar\">");
        sb.AppendLine("<input type=\"text\" id=\"q\" placeholder=\"过滤：对象号 / 路径 / 结论 / 原因\" oninput=\"pcmigFilter()\">");
        sb.AppendLine("<label><input type=\"checkbox\" id=\"onlybad\" onchange=\"pcmigFilter()\"> 只看需要处理的对象（差异 / 无法确认）</label>");
        sb.AppendLine("<span class=\"note\">当前显示 <b id=\"shown\">0</b> 行</span>");
        sb.AppendLine("</div>");

        sb.AppendLine("<table class=\"grid\" id=\"objtable\"><thead><tr>");
        sb.AppendLine("<th>#</th><th>对象 ID</th><th>显示名 / 位置</th><th>类型</th>");
        sb.AppendLine("<th onclick=\"pcmigSort('objtable','srcf',true)\">源文件数 ⇅</th>");
        sb.AppendLine("<th onclick=\"pcmigSort('objtable','dstf',true)\">目标文件数 ⇅</th>");
        sb.AppendLine("<th onclick=\"pcmigSort('objtable','filediff',true)\">文件差 ⇅</th>");
        sb.AppendLine("<th onclick=\"pcmigSort('objtable','srcb',true)\">源字节 ⇅</th>");
        sb.AppendLine("<th onclick=\"pcmigSort('objtable','dstb',true)\">目标字节 ⇅</th>");
        sb.AppendLine("<th onclick=\"pcmigSort('objtable','bytediff',true)\">字节差 ⇅</th>");
        sb.AppendLine("<th>哈希已检/失败</th><th>枚举覆盖</th><th>结论</th><th>可核实原因</th><th>明细</th>");
        sb.AppendLine("</tr></thead><tbody>");
        if (v.Objects.Count == 0)
        {
            sb.AppendLine("<tr><td colspan=\"15\" class=\"note\">本次验证<b>没有产出任何逐对象结论</b>" +
                          "（例如在对象之间被取消）——这张表因此是空的：<b>空表不等于通过</b>，" +
                          "也不代表目标侧没有差异，只代表本轮没有核对到任何对象。计划里的对象清单见 03 表与 job.json。</td></tr>");
        }

        var index = 0;
        foreach (var o in v.Objects.OrderByDescending(NeedsAttention)
                     .ThenBy(o => o.ObjectId, StringComparer.OrdinalIgnoreCase))
        {
            index++;
            planned.TryGetValue(o.ObjectId, out var p);
            var bad = o.Status is not ("OK" or "EMPTY");
            var search = string.Join(" ", new[]
            {
                o.ObjectId, p?.SourcePath ?? "", p?.TargetPath ?? "", o.Status, o.StatusText, o.UnknownReason ?? "",
            }).ToLowerInvariant();
            var rowClass = index > ObjectRowInitialLimit ? " class=\"pcmig-more\"" : "";
            sb.AppendLine($"<tr{rowClass} data-obj=\"{ReportStyle.E(o.ObjectId)}\" data-bad=\"{(bad ? 1 : 0)}\"" +
                          $" data-text=\"{ReportStyle.E(search)}\" data-sort-name=\"{ReportStyle.E(o.ObjectId)}\"" +
                          $" data-num-srcf=\"{o.SourceFiles}\" data-num-dstf=\"{o.TargetFiles}\"" +
                          $" data-num-filediff=\"{Math.Abs(o.SourceFiles - o.TargetFiles)}\"" +
                          $" data-num-srcb=\"{o.SourceBytes}\" data-num-dstb=\"{o.TargetBytes}\"" +
                          $" data-num-bytediff=\"{Math.Abs(o.TargetBytes - o.SourceBytes)}\">");
            sb.AppendLine($"<td>{index}</td>");
            sb.AppendLine($"<td class=\"mono\">{ReportStyle.E(o.ObjectId)}</td>");
            sb.AppendLine($"<td class=\"mono\">{ReportStyle.E(p?.SourcePath ?? "（计划里没有这个对象）")}<br>" +
                          $"<span class=\"note\">→ {ReportStyle.E(p?.TargetPath ?? "—")}</span></td>");
            sb.AppendLine($"<td>{ReportStyle.E(KindText(p?.Kind))}</td>");
            sb.AppendLine($"<td>{o.SourceFiles:N0}</td><td>{o.TargetFiles:N0}</td>" +
                          $"<td>{Signed(o.SourceFiles - o.TargetFiles)}</td>");
            sb.AppendLine($"<td>{Format.Bytes(o.SourceBytes)}</td><td>{Format.Bytes(o.TargetBytes)}</td>" +
                          $"<td>{Signed(o.TargetBytes - o.SourceBytes)}</td>");
            sb.AppendLine($"<td>{o.HashSampled:N0} / {(o.HashReadFailed > 0 ? $"<span class=\"warn\">{o.HashReadFailed:N0}</span>" : "0")}</td>");
            sb.AppendLine($"<td>{(o.EnumerationStatus == "incomplete" || o.EnumerationSkipped > 0
                ? $"<span class=\"warn\">不完整（跳过 {o.EnumerationSkipped:N0}）</span>"
                : o.EnumerationStatus is null ? "<span class=\"note\">未记录</span>" : "完整")}</td>");
            sb.AppendLine($"<td>{StatusTag(o.Status, o.StatusText)}</td>");
            sb.AppendLine($"<td>{ReasonCell(o)}</td>");
            var anchor = o.Status is "OK" or "EMPTY" ? "" : $"<a href=\"#obj-{ReportStyle.E(o.ObjectId)}\">看差异</a>";
            sb.AppendLine($"<td>{anchor}{DetailHint(o)}</td>");
            sb.AppendLine("</tr>");
        }
        sb.AppendLine("</tbody></table>");
        sb.AppendLine($"<p><button id=\"morebtn\" onclick=\"pcmigMore()\">显示更多（还有 " +
                      $"{Math.Max(0, v.Objects.Count - ObjectRowInitialLimit)} 行）</button>　" +
                      "<button onclick=\"pcmigExpandAll(true)\">全部展开差异详情</button>　" +
                      "<button onclick=\"pcmigExpandAll(false)\">全部收起</button></p>");
    }

    private static bool NeedsAttention(ObjectVerifyResult o) => o.Status is not ("OK" or "EMPTY");

    // ═════════════════════════════ 05 文件级差异表 ═════════════════════════════

    private static void WriteFileDiffTable(StringBuilder sb, VerifyReport v)
    {
        sb.AppendLine("<h2 id=\"s05\">05 · 文件级差异表</h2>");
        sb.AppendLine("<p class=\"note\">只有引擎<b>确实逐文件核对过</b>的条目才会出现在这里（缺失/多余来自两侧清单比较，" +
                      "哈希类来自 L2 抽样）。表内长度为 <b>-1</b> 表示「该侧不存在」，与「0 字节」严格区分。" +
                      "长度仅在两侧都存在时才可比。" +
                      "★ 「确认缺失样例 N 项」里的 N 是引擎记录的<b>样例条数</b>（上限 " + EngineSampleLimit +
                      " 条），<b>不是缺失总数</b>；缺失的规模请看 04 表的文件数差额与对象结论。</p>");
        if (v.FileDiffsTruncated)
            sb.AppendLine("<p class=\"warn\">★ 文件级差异已达记录上限，本表不是全部差异：超出的部分只体现在 04 表的计数与" +
                          "文件数/字节差额里。需要完整清单请重跑一次验证（引擎按有界缓冲记录，避免百万级任务把内存吃穿）。</p>");

        if (v.FileDiffs.Count == 0)
        {
            sb.AppendLine("<p class=\"note\">本次验证<b>没有记录到文件级差异条目</b>。注意：这不等于「没有任何差异」——" +
                          "若 04 表有对象的数量/字节不一致或结论为「无法确认」，请看该对象的计数与原因码，" +
                          "旧版本引擎产出的报告也可能没有这项明细。</p>");
            return;
        }

        var byObject = v.FileDiffs.GroupBy(d => d.ObjectId, StringComparer.OrdinalIgnoreCase).ToList();
        sb.AppendLine("<div class=\"toolbar\">");
        sb.AppendLine("<input type=\"text\" id=\"q\" placeholder=\"过滤：对象 / 相对路径 / 差异类型\" oninput=\"pcmigFilter()\">");
        sb.AppendLine("<label><input type=\"checkbox\" id=\"onlybad\" onchange=\"pcmigFilter()\"> 只看需要处理的对象</label>");
        sb.AppendLine($"<span class=\"note\">本表共 <b>{v.FileDiffs.Count}</b> 条（涉及 {byObject.Count} 个对象）</span>");
        sb.AppendLine("</div>");

        foreach (var g in byObject)
        {
            var obj = v.Objects.FirstOrDefault(o => string.Equals(o.ObjectId, g.Key, StringComparison.OrdinalIgnoreCase));
            var search = (g.Key + " " + string.Join(" ", g.Select(d => d.RelativePath))).ToLowerInvariant();
            sb.AppendLine($"<details id=\"obj-{ReportStyle.E(g.Key)}\" data-obj=\"{ReportStyle.E(g.Key)}\"" +
                          $" data-bad=\"1\" data-text=\"{ReportStyle.E(search)}\">");
            sb.AppendLine($"<summary>{ReportStyle.E(g.Key)} —— {g.Count()} 条文件级差异" +
                          (obj is null ? "" : $"，对象结论：{ReportStyle.E(obj.StatusText)}") + "</summary>");
            sb.AppendLine("<table class=\"grid\"><thead><tr>" +
                          "<th>相对路径</th><th>源绝对路径</th><th>目标预期路径</th><th>差异类型</th>" +
                          "<th>源长度</th><th>目标长度</th><th>证据来源</th><th>错误码</th><th>详情与建议处理</th>" +
                          "</tr></thead><tbody>");
            foreach (var d in g)
            {
                sb.AppendLine("<tr>");
                sb.AppendLine($"<td class=\"mono\">{ReportStyle.E(d.RelativePath)}</td>");
                sb.AppendLine($"<td class=\"mono\">{ReportStyle.E(d.SourcePath)}</td>");
                sb.AppendLine($"<td class=\"mono\">{ReportStyle.E(d.TargetPath)}</td>");
                sb.AppendLine($"<td>{DiffKindTag(d.DiffKind)}</td>");
                sb.AppendLine($"<td>{(d.SourceLength < 0 ? "<span class=\"note\">不存在</span>" : d.SourceLength.ToString("N0"))}</td>");
                sb.AppendLine($"<td>{(d.TargetLength < 0 ? "<span class=\"note\">不存在</span>" : d.TargetLength.ToString("N0"))}</td>");
                sb.AppendLine($"<td>{ReportStyle.E(d.Evidence ?? "未记录")}</td>");
                sb.AppendLine($"<td class=\"mono\">{ReportStyle.E(d.Win32Code ?? "—")}</td>");
                sb.AppendLine($"<td>{(d.Detail is null ? "" : ReportStyle.E(d.Detail) + "<br>")}" +
                              $"<span class=\"note\">{ReportStyle.E(AdviceForDiff(d.DiffKind))}</span></td>");
                sb.AppendLine("</tr>");
            }
            sb.AppendLine("</tbody></table></details>");
        }
    }

    private static string AdviceForDiff(string kind) => kind switch
    {
        "missing-on-target" => "建议：确认源侧文件仍在、目标盘空间足够，再用「尝试修复」补齐；" +
                               "修复结束后必须重新验证，不能把「修复操作结束」当成「已通过」。",
        "extra-on-target" => "建议：目标侧多出来的文件本工具不会自动清理；请人工确认它是否本就该在目标目录里。",
        "size-mismatch" => "建议：核对迁移时的中断/重试记录（迁移报告），确认目标文件是否被截断；必要时重传该文件。",
        "hash-mismatch" => "建议：内容确实不同。先确认源文件在两次验证之间没有被改动过，再考虑重传该文件。",
        "unreadable" => "建议：该文件没有内容级结论（不是「内容不一致」）。先排查占用与权限" +
                        "（Win32 32/33 是文件锁，不是共享断开），再重跑验证。",
        _ => "建议：见迁移报告中的对应对象记录。",
    };

    // ═════════════════════════════ 06 验证阶段表 ═════════════════════════════

    private static void WriteStageTable(StringBuilder sb, VerifyReport v)
    {
        sb.AppendLine("<h2 id=\"s06\">06 · 验证阶段与耗时</h2>");
        sb.AppendLine("<p class=\"note\">只列引擎真实记录的阶段耗时（按对象累计），<b>不做任何估算</b>。" +
                      "旧报告没有阶段记录时如实写「未采集」。" +
                      "注意：本工具目前不逐阶段写盘中间结果，所以报告里只有这几个真实累计值。</p>");
        if (v.Stages.Count == 0)
        {
            sb.AppendLine("<table><thead><tr><th>阶段</th><th>覆盖对象/样本</th><th>耗时</th><th>口径说明</th></tr></thead><tbody>");
            sb.AppendLine("<tr><td colspan=\"4\">" + NotCollected("这份报告由旧版本引擎产生，没有阶段耗时证据") + "</td></tr>");
            sb.AppendLine("</tbody></table>");
            if (v.ElapsedMs is not null)
                sb.AppendLine($"<p class=\"note\">仅有总耗时记录：{v.ElapsedMs.Value / 1000.0:0.00} 秒。</p>");
            return;
        }
        sb.AppendLine("<table><thead><tr><th>阶段</th><th>覆盖对象/样本</th><th>耗时</th><th>口径说明</th></tr></thead><tbody>");
        foreach (var s in v.Stages)
            sb.AppendLine($"<tr><td>{ReportStyle.E(s.Stage)}</td>" +
                          $"<td>{(s.CoveredObjects == 0 ? "—" : s.CoveredObjects.ToString("N0"))}</td>" +
                          $"<td>{s.ElapsedMs / 1000.0:0.000} 秒</td>" +
                          $"<td class=\"note\">{ReportStyle.E(s.Note ?? "")}</td></tr>");
        if (v.ElapsedMs is not null)
            sb.AppendLine("<tr><td><b>合计（含调度与落盘开销）</b></td><td>—</td>" +
                          $"<td><b>{v.ElapsedMs.Value / 1000.0:0.000} 秒</b></td>" +
                          "<td class=\"note\">引擎实测总耗时；与上面各阶段之和的差值是对象循环之外的固定开销</td></tr>");
        sb.AppendLine("</tbody></table>");
    }

    // ═════════════════════════════ 07 错误与建议 ═════════════════════════════

    private static void WriteErrorAdvice(StringBuilder sb, VerifyReport v)
    {
        sb.AppendLine("<h2 id=\"s07\">07 · 错误、原因与建议</h2>");
        sb.AppendLine("<p class=\"note\">纪律：<b>不把一切差异都叫「文件缺失」</b>，" +
                      "<b>不把「修复操作结束」当「复验通过」</b>；原因里标注（推测）的是推断，不是实测。</p>");

        var unknown = v.Objects.Where(o => o.Status == "UNKNOWN").ToList();
        sb.AppendLine("<h3>① 无法确认的对象（不是缺失）</h3>");
        if (unknown.Count == 0)
            sb.AppendLine("<p class=\"note\">本次没有「无法确认」的对象。</p>");
        else
        {
            sb.AppendLine("<table><thead><tr><th>对象</th><th>原因码</th><th>人话</th><th>影响</th><th>建议</th></tr></thead><tbody>");
            foreach (var o in unknown)
            {
                sb.AppendLine("<tr>");
                sb.AppendLine($"<td class=\"mono\">{ReportStyle.E(o.ObjectId)}</td>");
                sb.AppendLine($"<td class=\"mono\">{ReportStyle.E(o.UnknownReason ?? "未记录")}</td>");
                sb.AppendLine($"<td>{UnknownReasonText(o.UnknownReason)}</td>");
                sb.AppendLine("<td>该对象的数量/字节<b>不能作为通过依据</b>，也不能据此判断文件缺失</td>");
                sb.AppendLine($"<td>{UnknownAdvice(o.UnknownReason)}</td>");
                sb.AppendLine("</tr>");
            }
            sb.AppendLine("</tbody></table>");
        }

        sb.AppendLine("<h3>② 枚举期被跳过的条目（统计口径）</h3>");
        if (v.EnumerationFailures.Count == 0)
            sb.AppendLine("<p class=\"note\">本次枚举没有跳过任何条目（两侧枚举都完整）。</p>");
        else
        {
            if (v.EnumerationFailuresTruncated)
                sb.AppendLine("<p class=\"warn\">★ 明细仅记录前若干条（引擎有界缓冲）；被跳过的<b>总数</b>见 02 表的「枚举跳过条目」。</p>");
            sb.AppendLine("<table><thead><tr><th>侧别</th><th>路径</th><th>类型</th><th>错误信息（含 Win32 码）</th><th>发生时刻</th></tr></thead><tbody>");
            foreach (var f in v.EnumerationFailures.Take(200))
                sb.AppendLine($"<tr><td>{(f.Side == "target" ? "目标侧" : "源侧")}</td>" +
                              $"<td class=\"mono\">{ReportStyle.E(f.Path)}</td>" +
                              $"<td>{(f.Kind == "directory" ? "目录" : "文件")}</td>" +
                              $"<td>{ReportStyle.E(f.Message)}</td>" +
                              $"<td class=\"note\">{f.AtUtc.ToLocalTime():HH:mm:ss}</td></tr>");
            sb.AppendLine("</tbody></table>");
        }

        var unreadable = v.FileDiffs.Where(d => d.DiffKind == "unreadable").ToList();
        sb.AppendLine("<h3>③ 读取失败的文件（没有内容级结论）</h3>");
        if (unreadable.Count == 0)
            sb.AppendLine("<p class=\"note\">本次没有抽样文件读取失败。</p>");
        else
        {
            sb.AppendLine("<table><thead><tr><th>对象</th><th>相对路径</th><th>Win32</th><th>错误</th><th>影响</th></tr></thead><tbody>");
            foreach (var d in unreadable.Take(200))
                sb.AppendLine($"<tr><td class=\"mono\">{ReportStyle.E(d.ObjectId)}</td>" +
                              $"<td class=\"mono\">{ReportStyle.E(d.RelativePath)}</td>" +
                              $"<td class=\"mono\">{ReportStyle.E(d.Win32Code ?? "—")}</td>" +
                              $"<td>{ReportStyle.E(d.Detail ?? "")}</td>" +
                              "<td>该文件<b>未</b>获得内容级结论（不等于内容不一致）</td></tr>");
            sb.AppendLine("</tbody></table>");
        }

        sb.AppendLine("<h3>④ 差异分类与处置建议</h3>");
        sb.AppendLine("<table><thead><tr><th>差异类别</th><th>本次记录条目数</th><th>含义</th><th>建议</th></tr></thead><tbody>");
        foreach (var kind in new[] { "missing-on-target", "extra-on-target", "size-mismatch", "hash-mismatch", "unreadable" })
        {
            var n = v.FileDiffs.Count(d => d.DiffKind == kind);
            var objectsWithKind = v.Objects.Count(o => kind switch
            {
                "missing-on-target" => o.MissingSamples.Count > 0,
                "extra-on-target" => o.ExtraOnTarget.Count > 0,
                "size-mismatch" => o.SizeMismatched > 0,
                "hash-mismatch" => o.HashMismatched > 0,
                _ => o.HashReadFailed > 0,
            });
            sb.AppendLine($"<tr><td>{DiffKindTag(kind)}</td><td>{n}</td>" +
                          $"<td>{ReportStyle.E(DiffKindMeaning(kind))}</td>" +
                          $"<td>{ReportStyle.E(AdviceForDiff(kind))}</td></tr>");
            if (objectsWithKind > 0 && n == 0)
                sb.AppendLine("<tr><td colspan=\"4\" class=\"note\">注：有 " + objectsWithKind +
                              " 个对象报告了这类差异计数，但引擎没有记录到具体文件名" +
                              "（旧报告或超出证据上限）⇒ 报告不编造文件名。</td></tr>");
        }
        sb.AppendLine("</tbody></table>");
    }

    // ═════════════════════════════ 08 统计口径与限制 ═════════════════════════════

    private void WriteLimits(StringBuilder sb, VerifyReport v)
    {
        sb.AppendLine("<h2 id=\"s08\">08 · 统计口径与限制</h2>");
        sb.AppendLine("<h3>本次验证做到与没做到的事</h3><ul>");
        sb.AppendLine("<li><b>L1（文件数量与总字节对账）</b>：能回答「两侧的文件个数与总字节是否一致」。数量与字节一致" +
                      "<b>不能</b>证明逐文件内容相同（两个不同文件可以有相同长度）。</li>");
        sb.AppendLine("<li><b>L2（抽样 SHA-256）</b>：只对<b>抽到的那部分文件</b>给出内容级证据；没抽到的文件本轮没有内容级结论。" +
                      "抽样比例是配置值，具体覆盖率见 04 表的「哈希已检」列。</li>");
        sb.AppendLine("<li><b>UNKNOWN（无法确认）</b>：源或目标不可达、枚举不完整、与计划矛盾的 0/0 —— 这类对象既不算通过，" +
                      "也不能把没出现的文件算成真实缺失。</li>");
        sb.AppendLine("<li><b>执行期间源文件发生变化</b>：验证是某一时刻的快照；若验证期间源侧文件被写入，" +
                      "相同文件也可能报出字节/哈希差异。</li>");
        sb.AppendLine("<li><b>范围</b>：策略排除名单（见 03 表）内的文件不计入对账；云占位符与重解析点按枚举规则跳过。</li>");
        sb.AppendLine("</ul>");
        sb.AppendLine("<h3>字段来源表（哪些是实测、哪些没有采集）</h3>");
        sb.AppendLine("<table><thead><tr><th>字段</th><th>来源</th><th>本次是否可得</th></tr></thead><tbody>");
        FieldRow(sb, "对象清单 / 源与目标路径 / 预估文件数与字节 / 策略排除名单", "plan.json", "有");
        FieldRow(sb, "实测文件数/字节、四态结论、原因码、差异样例（各不超过 50 条）", "verify-report.json", "有");
        FieldRow(sb, "运行号 / 起止时刻 / 耗时 / 阶段耗时 / 构建版本 / 抽样比例",
            "verify-report.json（2026-10-11 起新增的可选字段）",
            v.VerifyRunId is null ? "旧报告没有（写「未采集」）" : "有");
        FieldRow(sb, "文件级差异（相对路径/两侧路径/长度/证据/错误码）",
            "verify-report.json → fileDiffs（有界，最多 500 条）",
            v.FileDiffs.Count > 0 ? "有" : "本次为空（可能真的没有差异，或旧报告没有这项）");
        FieldRow(sb, "枚举跳过明细（侧别/路径/类型/错误）",
            "verify-report.json → enumerationFailures（有界）",
            v.EnumerationFailures.Count > 0 ? "有" : "本次为空");
        FieldRow(sb, "逐文件确认缺失的<b>精确总数</b>", "引擎只按样例记录（每对象不超过 50 条样例）",
            "★ 不可得：报告只陈述样例与数量差额，不把样例数当总数");
        FieldRow(sb, "逐阶段中间结果（哪一秒做了什么）", "引擎不逐步落盘", "★ 不可得：只有各阶段的累计耗时");
        FieldRow(sb, "逐文件的 Windows/SMB 错误号", "只在引擎真的捕获到异常时记录",
            "可能为空 —— 空表示「没捕获到」，不是「没问题」");
        FieldRow(sb, "传输回执与 robocopy 退出码", "receipts/*.json（见 09 表）", "有（属迁移证据，不属于本次验证）");
        sb.AppendLine("</tbody></table>");
        sb.AppendLine("<h3>安全与隐私</h3><ul>");
        sb.AppendLine("<li>报告写入本机任务工作区（" + Mono(_ctx.ReportDir) + "），<b>不上传、不发邮件、不建外部服务</b>。</li>");
        sb.AppendLine("<li>报告不含口令、令牌、认证票据；界面与报告都不回显用户输入的密码。</li>");
        sb.AppendLine("<li>报告中的路径为审计所需的原始路径；把报告转发给他人前请自行评估其中的机器名与共享名。</li>");
        sb.AppendLine("</ul>");
    }

    // ═════════════════════════════ 09 关联迁移证据 ═════════════════════════════

    private void WriteRelatedEvidence(StringBuilder sb, VerifyReport v)
    {
        sb.AppendLine("<h2 id=\"s09\">09 · 关联迁移证据（与本次验证分开看）</h2>");
        sb.AppendLine("<p class=\"note\">本节列出的东西属于<b>迁移过程</b>的证据，用来解释「为什么目标侧是这样」，" +
                      "不代表验证结论；每份证据都标注产生时间，避免把旧报告当成本次结果。</p>");
        sb.AppendLine("<table><thead><tr><th>证据</th><th>位置</th><th>产生时间</th><th>说明</th></tr></thead><tbody>");
        sb.AppendLine("<tr><td>本次验证报告（本文件）</td><td class=\"mono\">（当前页面）</td>" +
                      $"<td>{DateTime.Now:yyyy-MM-dd HH:mm:ss}</td><td>本次核对结果</td></tr>");
        var verifyPath = _ctx.VerifyPath;
        sb.AppendLine($"<tr><td>验证原始数据 verify-report.json</td><td class=\"mono\">{ReportStyle.E(verifyPath)}</td>" +
                      $"<td>{FileTime(verifyPath)}</td><td>报告的唯一数据来源（报告不编造此文件之外的事实）</td></tr>");

        var migrations = SafeFiles(_ctx.ReportDir, "migration-report-*.html");
        if (migrations.Count == 0)
            sb.AppendLine("<tr><td>迁移报告</td><td colspan=\"3\" class=\"note\">本任务目录下没有找到迁移报告（可能尚未生成）</td></tr>");
        else
            foreach (var f in migrations.OrderByDescending(File.GetLastWriteTimeUtc).Take(5))
                sb.AppendLine("<tr><td>迁移报告</td><td class=\"mono\"><a href=\"" +
                              ReportStyle.E(Path.GetFileName(f)) + "\">" + ReportStyle.E(Path.GetFileName(f)) +
                              $"</a></td><td>{FileTime(f)}</td><td>迁移过程事实（传了什么、失败在哪）</td></tr>");

        var receipts = SafeFiles(_ctx.ReceiptsDir, "object-*.json");
        sb.AppendLine($"<tr><td>逐对象迁移回执</td><td class=\"mono\">{ReportStyle.E(_ctx.ReceiptsDir)}" +
                      $"（{receipts.Count} 个文件）</td>" +
                      $"<td>{(receipts.Count == 0 ? "—" : FileTime(receipts.OrderByDescending(File.GetLastWriteTimeUtc).First()))}</td>" +
                      "<td>最近一次传输的逐对象结果</td></tr>");
        sb.AppendLine(ReceiptSummaryRow(receipts));
        sb.AppendLine($"<tr><td>运行日志</td><td class=\"mono\">{ReportStyle.E(_ctx.LogsDir)}</td>" +
                      $"<td>{DirTime(_ctx.LogsDir)}</td><td>逐行过程日志（含 Preflight、吞吐、错误）</td></tr>");
        sb.AppendLine("</tbody></table>");
        if (v.RunStatus is not null && v.RunStatus != "completed")
            sb.AppendLine($"<p class=\"warn\">注意：本报告的运行状态记录为 {ReportStyle.E(v.RunStatus)}（不是 completed）。</p>");
    }

    /// <summary>最新回执摘要（只统计引擎真的写进回执的字段；读不出来就如实写「未采集」）。</summary>
    private static string ReceiptSummaryRow(List<string> receipts)
    {
        if (receipts.Count == 0) return "";
        try
        {
            var completed = 0;
            var interrupted = 0;
            var failed = 0;
            var other = 0;
            long bytes = 0;
            var latest = DateTime.MinValue;
            foreach (var f in receipts)
            {
                if (!JsonStateStore.TryRead<ObjectReceipt>(f, out var r) || r is null) { other++; continue; }
                switch (r.Status)
                {
                    case ObjectStatus.Completed: completed++; bytes += r.TargetBytes; break;
                    case ObjectStatus.Interrupted: interrupted++; break;
                    case ObjectStatus.Failed: failed++; break;
                    default: other++; break;
                }
                if (r.CompletedUtc > latest) latest = r.CompletedUtc;
            }
            var latestText = latest == DateTime.MinValue ? "—" : latest.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            return "<tr><td>最新回执摘要</td><td class=\"mono\">按 receipts/*.json 汇总</td>" +
                   $"<td>{latestText}</td><td>完成 {completed} / 中断 {interrupted} / 失败 {failed}" +
                   (other > 0 ? $" / 其他或无法解析 {other}" : "") +
                   $"，完成对象的目标侧字节合计 {Format.Bytes(bytes)}（<b>属迁移证据</b>，不是验证结论）</td></tr>";
        }
        catch
        {
            return "<tr><td>最新回执摘要</td><td colspan=\"3\" class=\"note\">回执解析失败：报告不编造摘要</td></tr>";
        }
    }

    private static List<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern).ToList() : new List<string>(); }
        catch { return new List<string>(); }
    }

    private static string FileTime(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss") : "（文件不存在）"; }
        catch { return "未采集"; }
    }

    private static string DirTime(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetLastWriteTime(dir).ToString("yyyy-MM-dd HH:mm:ss") : "（目录不存在）"; }
        catch { return "未采集"; }
    }

    // ═════════════════════════════ 渲染助手 ═════════════════════════════

    private static string KindText(ObjectKind? kind) => kind switch
    {
        ObjectKind.DataVolume => "数据卷",
        ObjectKind.UserProfile => "用户配置",
        ObjectKind.AppState => "应用状态",
        ObjectKind.RootFiles => "根目录散落文件",
        _ => "未记录",
    };

    private static string DescribeLevel(VerifyLevel level) => level switch
    {
        VerifyLevel.L1_CountSize => "L1 · 文件数/字节对账（不含内容哈希）",
        VerifyLevel.L2_SampleHash => "L2 · 文件数/字节对账 + 抽样 SHA-256",
        _ => level.ToString(),
    };

    private static string StatusTag(string status, string text)
    {
        var cls = status switch
        {
            "OK" => "green",
            "MISMATCH" => "red",
            "UNKNOWN" => "amber",
            _ => "gray",
        };
        return $"<span class=\"tag {cls}\">{ReportStyle.E(text)}</span>";
    }

    private static string DiffKindTag(string kind) => kind switch
    {
        "missing-on-target" => "<span class=\"tag red\">确证缺失</span>",
        "extra-on-target" => "<span class=\"tag amber\">目标多余</span>",
        "size-mismatch" => "<span class=\"tag red\">大小不同</span>",
        "hash-mismatch" => "<span class=\"tag red\">内容哈希不同</span>",
        "unreadable" => "<span class=\"tag gray\">无法读取</span>",
        _ => ReportStyle.E(kind),
    };

    private static string DiffKindMeaning(string kind) => kind switch
    {
        "missing-on-target" => "源侧有、目标侧没有（逐文件核对过）",
        "extra-on-target" => "目标侧有、源侧没有（不自动清理）",
        "size-mismatch" => "两侧都存在但字节数不同",
        "hash-mismatch" => "抽样 SHA-256 两侧不同（内容确实不同）",
        "unreadable" => "打不开或读不到，未获得内容级结论",
        _ => "",
    };

    private static string ReasonCell(ObjectVerifyResult o)
    {
        var parts = new List<string>();
        if (o.ExtraOnTarget.Count > 0)
            parts.Add($"目标多余 {o.ExtraOnTarget.Count} 项（样例：" +
                      ReportStyle.E(string.Join("、", o.ExtraOnTarget.Take(3))) + "）");
        // ★ F-1（2026-10-13 定点修复）★ 只有确证缺失才写"确认缺失"；UNKNOWN 对象里的这些路径
        //   是"读不到那一侧"的副产物，必须说成"无法确认（未核实）"，否则与卡片上
        //   「无法确认不是缺失」自相矛盾（现场"假缺失"话术的来源）。
        if (o.MissingSamples.Count > 0)
            parts.Add(o.MissingConfirmed
                ? $"确认缺失样例 {o.MissingSamples.Count} 项（" +
                  ReportStyle.E(string.Join("、", o.MissingSamples.Take(3))) + "）"
                : $"无法确认（未核实，不得当作缺失）样例 {o.MissingSamples.Count} 项（" +
                  ReportStyle.E(string.Join("、", o.MissingSamples.Take(3))) + "）");
        if (o.SizeMismatched > 0) parts.Add($"大小不一致 {o.SizeMismatched} 项");
        if (o.HashMismatched > 0) parts.Add($"哈希不一致 {o.HashMismatched} 项");
        if (o.HashReadFailed > 0) parts.Add($"哈希读取失败 {o.HashReadFailed} 项（不等于内容不一致）");
        if (o.EnumerationSkipped > 0) parts.Add($"枚举跳过 {o.EnumerationSkipped} 项");
        if (o.SourceUnavailable) parts.Add("源不可达");
        if (o.TargetUnavailable) parts.Add("目标不可达");
        if (o.BothSidesEmpty) parts.Add("两侧都为空");
        return parts.Count == 0 ? "<span class=\"note\">—</span>" : string.Join("<br>", parts);
    }

    private static string DetailHint(ObjectVerifyResult o)
        => o.MissingSamples.Count >= EngineSampleLimit
            ? "<br><span class=\"note\">样例已达引擎上限 " + EngineSampleLimit +
              "：这只是<b>样例</b>，不是缺失总数（总数需靠文件数差额与重跑验证判断）</span>"
            : "";

    private static string UnknownReasonText(string? reason) => reason switch
    {
        "source-unavailable" => "源侧根路径不可达（共享断开/凭据失效/路径不存在）",
        "target-unavailable" => "目标侧根路径不可达（目标盘或目录不存在）",
        "both-sides-empty-against-plan" => "计划里预期有数据，但两侧都是 0 文件 0 字节 —— 与计划矛盾，不能算通过",
        "enumeration-incomplete" => "枚举过程中有条目被跳过（权限/竞态），统计不完整",
        null => "旧报告没有记录原因码（推测）",
        _ => ReportStyle.E(reason),
    };

    private static string UnknownAdvice(string? reason) => reason switch
    {
        "source-unavailable" => "先确认源共享可达与凭据有效（界面底部源灯应为「已连接（实测可读）」），恢复后重新验证。",
        "target-unavailable" => "确认目标盘已挂载、目标根目录存在（本工具不会替你创建或覆盖目标根）。",
        "both-sides-empty-against-plan" => "两侧都空但与计划矛盾 —— 先确认是不是路径映射错了（见 03 表映射规则），不要执行修复。",
        "enumeration-incomplete" => "排查权限、占用与网络抖动后重新验证；枚举不完整时任何「缺失」结论都不可信。",
        _ => "重新验证一次以获得完整证据。",
    };

    private static void Row(StringBuilder sb, string k, string v)
        => sb.AppendLine($"<tr><th style=\"width:220px\">{ReportStyle.E(k)}</th><td>{v}</td></tr>");

    private static void MetricRow(StringBuilder sb, string k, string v, string note)
        => sb.AppendLine($"<tr><td>{ReportStyle.E(k)}</td><td><b>{v}</b></td><td class=\"note\">{ReportStyle.E(note)}</td></tr>");

    private static void EnvRow(StringBuilder sb, string k, string v, string source)
        => sb.AppendLine($"<tr><td>{ReportStyle.E(k)}</td><td>{v}</td><td class=\"note\">{ReportStyle.E(source)}</td></tr>");

    private static void FieldRow(StringBuilder sb, string field, string source, string availability)
        => sb.AppendLine($"<tr><td>{field}</td><td class=\"mono\">{ReportStyle.E(source)}</td><td>{availability}</td></tr>");

    private static void Card(StringBuilder sb, string num, string label)
        => sb.AppendLine($"<div class=\"card\"><div class=\"num\">{ReportStyle.E(num)}</div>" +
                         $"<div class=\"label\">{ReportStyle.E(label)}</div></div>");

    private static string Ok(string s) => $"<span class=\"ok\">{ReportStyle.E(s)}</span>";
    private static string Bad(string s) => $"<span class=\"bad\">{ReportStyle.E(s)}</span>";
    private static string Mono(string s) => $"<span class=\"mono\">{ReportStyle.E(s)}</span>";
    private static string NotCollected(string why) => $"<span class=\"note\">未采集（{ReportStyle.E(why)}）</span>";

    private static string CompletenessTag(string? completeness) => completeness switch
    {
        "Complete" => "<span class=\"tag green\">完整</span>",
        "Partial" => "<span class=\"tag amber\">部分（统计可能不完整）</span>",
        null => "<span class=\"tag gray\">未记录</span>",
        _ => ReportStyle.E(completeness),
    };

    private static string Signed(long delta)
        => delta == 0 ? "0" : (delta > 0 ? "+" + delta.ToString("N0") : "-" + Math.Abs(delta).ToString("N0"));

    /// <summary>运行被取消或失败：这类运行允许用「部分证据」出报告，但结论必须写明没有完整结论。</summary>
    internal static bool IsCanceledOrFailed(string? runStatus)
        => runStatus is not null &&
           (runStatus.Equals("canceled", StringComparison.OrdinalIgnoreCase) ||
            runStatus.Equals("cancelled", StringComparison.OrdinalIgnoreCase) ||
            runStatus.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
            runStatus.Equals("interrupted", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 环境档案里的「用户显式选择」：只铺前 <see cref="SelectionListInlineLimit"/> 条，其余如实计数。
    /// 理由：413 条（乃至更多）选择项全铺进单元格会让这一格变成几百行，报告首屏直接被撑爆，
    /// 而这些路径在 04 表与 job.json 里都能逐条查到 —— 这里只做「可读的入口 + 真实总数」。
    /// </summary>
    private static string SelectionListText(IReadOnlyList<string> selected)
    {
        var shown = selected.Take(SelectionListInlineLimit).Select(p => Mono(p));
        var text = string.Join("<br>", shown);
        if (selected.Count > SelectionListInlineLimit)
            text += $"<br><span class=\"note\">…还有 {selected.Count - SelectionListInlineLimit:N0} 条（共 {selected.Count:N0} 条；完整清单见 job.json 与 04 表）</span>";
        return text;
    }
}