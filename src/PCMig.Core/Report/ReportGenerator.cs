using System.Text;
using PCMig.Core.Jobs;
using PCMig.Core.Models;
using PCMig.Core.State;
using PCMig.Core.Transfer;
using PCMig.Core.Util;
using Serilog;

namespace PCMig.Core.Report;

/// <summary>生成自包含 HTML 迁移报告（IT 明细 + 用户须知 双视角）。</summary>
public sealed class ReportGenerator
{
    private readonly JobContext _ctx;
    private readonly ILogger _log;

    public ReportGenerator(JobContext ctx, ILogger log)
    {
        _ctx = ctx;
        _log = log.ForContext<ReportGenerator>();
    }

    public string Generate()
    {
        var job = _ctx.Definition;
        var state = _ctx.LoadStateOrNew();
        var receipts = _ctx.LoadReceipts(_log);
        VerifyReport? verify = JsonStateStore.TryRead<VerifyReport>(_ctx.VerifyPath, out var v) ? v : null;
        PreflightReport? preflight = JsonStateStore.TryRead<PreflightReport>(_ctx.PreflightPath, out var p) ? p : null;

        // 每个对象取最新一条 Receipt
        var latest = receipts.GroupBy(r => r.ObjectId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.CompletedUtc).Last(), StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.Append("""
<!DOCTYPE html>
<html lang="zh-CN"><head><meta charset="utf-8">
<title>PCMig 迁移报告</title>
<style>
body{font-family:"Microsoft YaHei",sans-serif;margin:32px;color:#222;background:#fafafa}
h1{font-size:24px} h2{font-size:18px;border-bottom:2px solid #3b82f6;padding-bottom:6px;margin-top:32px}
.cards{display:flex;gap:16px;flex-wrap:wrap;margin:16px 0}
.card{background:#fff;border:1px solid #e5e7eb;border-radius:8px;padding:16px 24px;min-width:160px}
.card .num{font-size:26px;font-weight:700;color:#1d4ed8} .card .label{color:#6b7280;font-size:13px}
table{border-collapse:collapse;width:100%;background:#fff;margin-top:8px;font-size:13px}
th,td{border:1px solid #e5e7eb;padding:8px 10px;text-align:left}
th{background:#eff6ff} tr:nth-child(even){background:#f9fafb}
.ok{color:#16a34a;font-weight:600} .bad{color:#dc2626;font-weight:600} .warn{color:#d97706;font-weight:600}
.mono{font-family:Consolas,monospace;font-size:12px}
.tag{display:inline-block;padding:2px 8px;border-radius:10px;font-size:12px;color:#fff}
.tag.green{background:#16a34a}.tag.red{background:#dc2626}.tag.amber{background:#d97706}.tag.gray{background:#6b7280}
.note{color:#6b7280;font-size:12px}
</style></head><body>
""");
        sb.Append($"<h1>PCMig 迁移报告 <span class='mono'>{E(job.JobId)}</span></h1>");
        sb.Append($"<p>生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}　源电脑：<b>{E(job.SourceHost)}</b>　目标：<span class='mono'>{E(job.TargetRoot)}</span></p>");

        // ---- 概览卡片 ----
        var totalDur = receipts.Count > 0
            ? receipts.Max(r => r.CompletedUtc) - receipts.Min(r => r.StartedUtc) : TimeSpan.Zero;
        sb.Append("<div class='cards'>");
        Card(sb, "任务状态", PhaseTag(state.Phase));
        Card(sb, "传输总量", Format.Bytes(state.CompletedBytes));
        Card(sb, "对象完成", $"{state.CompletedObjects}/{state.TotalObjects}");
        Card(sb, "失败对象", state.FailedObjects.ToString());
        Card(sb, "总耗时", totalDur.TotalMinutes >= 1 ? $"{totalDur.TotalMinutes:0} 分钟" : $"{totalDur.TotalSeconds:0} 秒");
        sb.Append("</div>");
        if (!string.IsNullOrWhiteSpace(state.LastError))
            sb.Append($"<p class='warn'>⚠ 最近错误：{E(state.LastError)}</p>");

        // ---- 环境档案（实测基准：供跨机器对比、安全软件排查、验收归档） ----
        {
            var opts = job.Options;
            // 口径修正（v0.5.3 Preview.2）：平均速度的分母改用"各趟实际在传时间"的并集。
            // 旧口径拿 最早开始→最晚完成 的墙钟跨度当分母，把等待审阅、暂停、被强杀后等待续传的空白
            // 全算成传输时间，真机上就得出 87.07 MB/s 这种既不是网卡速率、也解释不清的数字。
            var transferSeconds = MergedTransferSeconds(receipts);
            var avgSpeed = transferSeconds > 1 && state.CompletedBytes > 0
                ? state.CompletedBytes / transferSeconds : 0;
            sb.Append("<h2>环境档案</h2><table>");
            void Row(string k, string v) => sb.Append($"<tr><th style='width:190px'>{k}</th><td>{v}</td></tr>");
            Row("源电脑", E(job.SourceHost));
            Row("迁移账号", E(job.SourceUser ?? "<当前 Windows 身份>"));
            Row("操作人", E(job.CreatedBy));
            Row("目标路径", $"<span class='mono'>{E(job.TargetRoot)}</span>");
            Row("任务创建时间", job.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            Row("robocopy 线程数", opts.Threads + "（/MT）");
            Row("大文件阈值", $"{opts.LargeFileThresholdMB} MB（≥阈值走 /Z 可续传通道）");
            Row("重试策略", $"{opts.RetryCount} 次 / 间隔 {opts.RetryWaitSec} 秒");
            Row("平均传输速度", avgSpeed > 0
                ? $"{Format.Speed(avgSpeed)} <span class='note'>（按各趟回执的实际在传时间并集计算，不含暂停/等待空白；不是网卡实测速率）</span>"
                : "-");
            Row("任务跨度", totalDur.TotalSeconds >= 1
                ? $"{(totalDur.TotalMinutes >= 1 ? $"{totalDur.TotalMinutes:0.#} 分钟" : $"{totalDur.TotalSeconds:0} 秒")} <span class='note'>（最早开始 → 最晚完成，含暂停、等待审阅与中断空白）</span>"
                : "-");
            Row("规模", $"{state.TotalObjects} 个对象 / {Format.Bytes(state.TotalBytes)}");
            Row("自定义排除规则", job.CustomExclusions.Count > 0 ? $"{job.CustomExclusions.Count} 条（超大数据模式）" : "无");
            sb.Append("</table>");
        }

        // ---- 结论与建议（人话区，置顶） ----
        // 口径修正（v0.5.3 Preview.2）：被标"中断"的对象同样没传完，必须进结论区。
        // 旧口径只认 Failed/CompletedWithErrors，真机上被强杀、还没续传的 object-000003 因此从报告里整段消失。
        var incomplete = (_ctx.Plan?.Objects ?? [])
            .Where(o => latest.TryGetValue(o.ObjectId, out var r) &&
                        r.Status is ObjectStatus.Failed or ObjectStatus.CompletedWithErrors or ObjectStatus.Interrupted)
            .ToList();
        if (state.Phase == JobPhase.Completed)
        {
            sb.Append("<h2>✅ 结论</h2><p class='ok' style='font-size:15px'>迁移全部完成。建议回到工具点「验证」做一次数据一致性核对。</p>");
        }
        else if (incomplete.Count > 0)
        {
            // 措辞口径（v0.5.3 Stable）：**报告不做未经验证的绝对保证**。
            // 本报告只陈述回执与日志事实；"数据没有丢 / 已传部分都在"这类结论必须由用户点「验证」后取得，
            // 不能由报告代发（旧文案在本分支里不看 verify 就宣告"数据没有丢"，属未经验证的保证）。
            sb.Append("<h2>⚠ 结论与建议</h2><p style='font-size:15px'>以下对象尚未全部传完（部分条目未复制成功或未走完）。" +
                      "<b>本报告未做数据一致性核对</b>——已传部分仍留在目标位置，处理后点「恢复任务」补齐，再点「验证」确认：</p><ul>");
            if (verify is { OverallPass: false })
            {
                sb.Append("<p class='bad' style='font-size:14px'>注意：已有的验证结果显示<b>存在不一致对象</b>，请以「验证结果」一节为准。</p>");
            }
            foreach (var o in incomplete)
            {
                var r = latest[o.ObjectId];
                var friendlyName = o.SourcePath.TrimEnd('\\').Split('\\').LastOrDefault() ?? o.SourcePath;
                sb.Append($"<li><b>{E(friendlyName)}</b>（{StatusText(r.Status)}，已传 {Format.Bytes(r.TargetBytes)}）");
                var codeNote = ExitCodeNote(r);
                if (codeNote.Length > 0) sb.Append($"<br>{E(codeNote)}");
                var explain = ErrorTranslator.Advice(r.ErrorDetail);
                if (explain.Length > 0) sb.Append($"<br>{E(explain)}");
                // 原始 ErrorDetail 不再直接贴在这里：真机上它是 robocopy 的『新文件』状态行，
                // 贴出来就等于告诉用户"文件明明传了却报错"。它连同对账结论一起放证据区。
                sb.Append($"<br><a href='#ev-{E(o.ObjectId)}'>查看日志证据 ↓</a>");
                sb.Append("</li>");
            }
            sb.Append("</ul>");
        }

        // ---- 失败与中断证据（逐对象，v0.5.3 Preview.2 新增；紧跟在结论后面，用户点结论里的链接直达） ----
        var evidenceIds = incomplete.Select(o => o.ObjectId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        WriteFailureEvidence(sb, incomplete, latest, receipts);

        // ---- 对象明细 ----
        sb.Append("<h2>对象明细</h2><table><tr><th>对象</th><th>类别</th><th>源路径</th><th>目标路径</th><th>落盘量</th><th>文件数</th><th>状态</th><th>耗时</th><th>错误</th></tr>");
        foreach (var obj in _ctx.Plan?.Objects ?? [])
        {
            if (latest.TryGetValue(obj.ObjectId, out var r))
            {
                var dur = r.CompletedUtc - r.StartedUtc;
                sb.Append($"<tr><td class='mono'>{E(r.ObjectId)}</td><td>{r.Kind}</td>" +
                    $"<td class='mono'>{E(r.SourcePath)}</td><td class='mono'>{E(r.TargetPath)}</td>" +
                    $"<td>{Format.Bytes(r.TargetBytes)}</td><td>{r.TargetFiles}</td>" +
                    $"<td>{StatusText(r.Status)}</td><td>{dur.TotalMinutes:0.#} 分</td>" +
                    $"<td>{ErrorCell(r, evidenceIds.Contains(r.ObjectId))}</td></tr>");
            }
            else
            {
                sb.Append($"<tr><td class='mono'>{E(obj.ObjectId)}</td><td>{obj.Kind}</td>" +
                    $"<td class='mono'>{E(obj.SourcePath)}</td><td class='mono'>{E(obj.TargetPath)}</td>" +
                    $"<td>~{Format.Bytes(obj.EstimatedBytes)}</td><td>~{obj.EstimatedFiles}</td>" +
                    $"<td><span class='tag gray'>未执行</span></td><td>-</td><td></td></tr>");
            }
        }
        sb.Append("</table>");

        // ---- 验证结果 ----
        if (verify != null)
        {
            sb.Append($"<h2>验证结果（{verify.Level}，{verify.VerifiedUtc:yyyy-MM-dd HH:mm} UTC）</h2>");
            sb.Append(verify.OverallPass
                ? "<p class='ok'>✔ 全部对象通过验证</p>"
                : "<p class='bad'>✘ 存在不一致对象，请检查下表</p>");
            sb.Append("<table><tr><th>对象</th><th>源文件</th><th>目标文件</th><th>源字节</th><th>目标字节</th><th>计数</th><th>字节</th><th>哈希抽样</th><th>结论</th></tr>");
            foreach (var o in verify.Objects)
            {
                sb.Append($"<tr><td class='mono'>{E(o.ObjectId)}</td><td>{o.SourceFiles}</td><td>{o.TargetFiles}</td>" +
                    $"<td>{Format.Bytes(o.SourceBytes)}</td><td>{Format.Bytes(o.TargetBytes)}</td>" +
                    $"<td>{(o.CountMatch ? "<span class='ok'>✔</span>" : "<span class='bad'>✘</span>")}</td>" +
                    $"<td>{(o.BytesMatch ? "<span class='ok'>✔</span>" : "<span class='bad'>✘</span>")}</td>" +
                    $"<td>{(o.HashSampled > 0 ? $"{o.HashSampled} 项/{o.HashMismatched} 不一致" : "-")}</td>" +
                    $"<td>{(o.Status == "OK" ? "<span class='ok'>OK</span>" : "<span class='bad'>MISMATCH</span>")}</td></tr>");
                foreach (var m in o.MissingSamples.Take(10))
                    sb.Append($"<tr><td colspan='9' class='mono bad'>缺失: {E(m)}</td></tr>");
            }
            sb.Append("</table>");
        }

        // ---- 用户须知 ----
        sb.Append("""
<h2>用户须知（新机首次使用前请阅读）</h2>
<ul>
<li>浏览器密码与网站登录状态不会迁移（它们被源电脑的加密密钥保护，搬过去也无法解开）——请在新机上重新登录浏览器与各个网站。</li>
<li>邮箱（Outlook 等）需要重新登录账号；邮件数据会自动从服务器同步回来。</li>
<li>公司安全软件（GlobalProtect / DLP / EDR）与驱动程序不在自动迁移范围，由 IT 统一安装。</li>
<li>如使用 OneDrive/云盘，新机登录账号后文件会自动同步。</li>
</ul>
""");

        // ---- 附录：Preflight + 日志位置 ----
        if (preflight != null)
        {
            sb.Append("<h2>附录：Preflight 检查记录</h2><table><tr><th>检查项</th><th>结果</th><th>说明</th></tr>");
            foreach (var c in preflight.Checks)
                sb.Append($"<tr><td>{E(c.Name)}</td><td>{(c.Pass ? "<span class='ok'>通过</span>" : c.Severity == "Warning" ? "<span class='warn'>警告</span>" : "<span class='bad'>失败</span>")}</td><td>{E(c.Detail)}</td></tr>");
            sb.Append("</table>");
        }
        sb.Append($"<h2>附录：排障信息</h2><ul>" +
            $"<li>任务目录：<span class='mono'>{E(_ctx.JobDir)}</span></li>" +
            $"<li>任务日志：<span class='mono'>{E(_ctx.LogsDir)}</span>（job-*.log / job-*.jsonl / robocopy/*.log）</li>" +
            $"<li>应用日志：<span class='mono'>{E(Logging.LogBootstrap.AppLogDir)}</span></li></ul>");

        var ver = typeof(ReportGenerator).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        sb.Append($"<p style='color:#9ca3af;margin-top:40px'>PCMig v{ver} —— 报告由引擎自动生成，只陈述回执与日志事实，不代替「验证」的数据一致性结论。</p></body></html>");

        Directory.CreateDirectory(_ctx.ReportDir);
        var path = Path.Combine(_ctx.ReportDir, $"migration-report-{DateTime.Now:yyyyMMdd-HHmmss}.html");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        _log.Information("报告已生成: {Path}", path);
        return path;
    }

    private static void Card(StringBuilder sb, string label, string value)
        => sb.Append($"<div class='card'><div class='num'>{value}</div><div class='label'>{label}</div></div>");

    /// <summary>
    /// 逐对象写"失败与中断证据"。证据只来自机器日志本身：robocopy 汇总表的"失败"计数、错误码直方图、
    /// 日志里真实出现的错误行；日志里没有的内容如实写"没有"，绝不用普通行充数。
    /// 真机教训（v0.5.3 Preview.2）：150.7 GB 那次任务的三个 robocopy 日志里<b>一条文件级错误行都没有</b>，
    /// 唯一的失败证据是汇总表的"失败 1"（目录级）；而回执里那条"原因"其实是一条普通『新文件』状态行。
    /// 所以除了证据，这里还专门做一次"任务记录的原因 vs 日志实况"的对账。
    /// </summary>
    private void WriteFailureEvidence(
        StringBuilder sb,
        IReadOnlyList<PlannedObject> incomplete,
        IReadOnlyDictionary<string, ObjectReceipt> latest,
        IReadOnlyList<ObjectReceipt> allReceipts)
    {
        if (incomplete.Count == 0) return;

        sb.Append("<h2>失败与中断证据（逐对象）</h2>");
        sb.Append("<p style='font-size:13px'>下面每一条都来自机器日志本身：robocopy 汇总表的“失败”计数、错误码直方图、" +
                  "以及日志里真实出现的错误行。日志里没有的内容会直接写“没有”——<b>不会拿普通行充数</b>。</p>");

        foreach (var obj in incomplete)
        {
            var r = latest[obj.ObjectId];
            var friendlyName = obj.SourcePath.TrimEnd('\\').Split('\\').LastOrDefault() ?? obj.SourcePath;
            var hint = HintPathFrom(r.ErrorDetail);
            var ev = FailureEvidenceCollector.Collect(_ctx.RoboLogsDir, obj.ObjectId, hint);
            var attempts = allReceipts
                .Where(x => string.Equals(x.ObjectId, obj.ObjectId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.StartedUtc)
                .ToList();

            const string pre = "<pre class='mono' style='background:#fff;border:1px solid #e5e7eb;border-radius:6px;padding:8px;overflow:auto;font-size:12px'>";
            const string h3 = "<h3 style='font-size:14px;margin:14px 0 4px'>";
            const string note = "<p style='font-size:12px;color:#6b7280;margin:4px 0'>";

            sb.Append("<details id='ev-").Append(E(obj.ObjectId))
              .Append("' style='background:#fff;border:1px solid #e5e7eb;border-radius:8px;padding:10px 14px;margin:10px 0'>");
            sb.Append("<summary style='cursor:pointer'><b class='mono'>").Append(E(obj.ObjectId)).Append("</b>　")
              .Append(E(friendlyName)).Append("　").Append(StatusText(r.Status))
              .Append("　日志 <span class='mono'>").Append(E(ev.LogFileName)).Append("</span>（")
              .Append(ev.LogFound ? Format.Bytes(ev.LogBytes) : "缺失").Append("，").Append(ev.TotalLines).Append(" 行，")
              .Append(E(ev.EncodingLabel)).Append("）</summary>");

            // ---- 逐趟回执：跨趟退出码才解释得清"第一趟被杀、第二趟成功" ----
            sb.Append(h3).Append("任务回执（每趟一条）</h3>");
            sb.Append("<table><tr><th>趟</th><th>状态</th><th>落盘量</th><th>文件数</th><th>Bulk 退出码</th><th>Large 退出码</th><th>错误类别</th><th>耗时</th></tr>");
            foreach (var a in attempts)
            {
                var dur = a.CompletedUtc - a.StartedUtc;
                sb.Append($"<tr><td>{a.Attempt}</td><td>{StatusText(a.Status)}</td><td>{Format.Bytes(a.TargetBytes)}</td><td>{a.TargetFiles}</td>" +
                          $"<td class='mono'>{E(ExitCodeCell(a.RobocopyExitCodeBulk))}</td><td class='mono'>{E(ExitCodeCell(a.RobocopyExitCodeLarge))}</td>" +
                          $"<td>{E(a.ErrorClass.ToString())}</td><td>{dur.TotalMinutes:0.#} 分</td></tr>");
            }
            sb.Append("</table>");

            // ---- robocopy 汇总表：真机上这是唯一的失败证据 ----
            var summaryRows = ev.Lines.Where(l => l.Kind == "summary").Select(l => l.Raw).ToList();
            if (summaryRows.Count > 0)
            {
                sb.Append(h3).Append($"robocopy 汇总表（日志原文，{summaryRows.Count} 行）</h3>");
                sb.Append(pre).Append(E(string.Join("\n", summaryRows))).Append("</pre>");
                sb.Append(note).Append("看“失败”那一列：非 0 就是这一趟真实没能复制的条目数。" +
                    "目录级失败在日志里不留错误行，所以汇总表往往就是唯一证据。</p>");
            }

            // ---- 错误码直方图 ----
            if (ev.Codes.Count > 0)
            {
                sb.Append(h3).Append("错误码统计</h3><table><tr><th>错误码</th><th>含义</th><th>出现次数</th></tr>");
                foreach (var c in ev.Codes)
                    sb.Append($"<tr><td class='mono'>{E(c.Code)}</td><td>{E(CodeMeaning(c.Code))}</td><td>{c.Count}</td></tr>");
                sb.Append("</table>");
            }
            else
            {
                sb.Append(note).Append("错误码统计：<b>日志里没有任何错误码</b>（没有出现“错误 NNNN”这类行）。</p>");
            }

            // ---- 文件级错误行 ----
            sb.Append(h3).Append($"文件级错误行（{ev.ErrorLineCount} 条）</h3>");
            if (ev.HasEvidence)
            {
                sb.Append(pre);
                foreach (var l in ev.Lines.Where(l => l.Kind != "summary"))
                    sb.Append(E($"{(l.Code.Length > 0 ? "[" + l.Code + "] " : "")}{l.Path}\n    {l.Raw}")).Append('\n');
                sb.Append("</pre>");
            }
            else
            {
                sb.Append("<p style='font-size:13px'>").Append(E(ev.NoEvidenceText())).Append("</p>");
            }
            if (ev.Truncated)
                sb.Append(note).Append($"日志过长，已按上限保留前 {FailureEvidenceCollector.MaxLinesPerObject} 行证据，其余 {ev.DroppedLines} 条同类行未列出（计数仍为全量）。</p>");
            if (ev.HasReadError)
                sb.Append("<p class='warn' style='font-size:13px'>⚠ 日志读取失败：").Append(E(ev.ReadError ?? "")).Append("</p>");

            // ---- 对账：任务记录里那条"原因"到底是不是错误 ----
            sb.Append(h3).Append("任务记录的原因 vs 日志实况</h3>");
            if (!string.IsNullOrWhiteSpace(r.ErrorDetail))
            {
                // v0.5.3：回执原文出口与日志证据共用同一脱敏口径，疑似含凭据的行只报"已隐去"。
                var shown = FailureEvidenceCollector.LooksSensitive(r.ErrorDetail)
                    ? "（该行疑似含凭据，已隐去）"
                    : Project(r.ErrorDetail!);
                sb.Append(note).Append("任务记录（回执）里给出的原因原文，<b>未经日志核实</b>：<br><span class='mono'>")
                  .Append(E(shown)).Append("</span></p>");
            }
            if (hint is not null && ev.HintAppearsInLog)
            {
                var verdict = ev.HintIsNotAnError || ev.ErrorLineCount == 0
                    ? "<b class='bad'>它不是错误行</b>——不能把它当失败原因看（旧版报告正是因为直接贴它，才让人以为“文件明明传了却报错”）"
                    : "它在日志里也作为错误行出现过（上面的文件级错误行里有它）";
                sb.Append("<p style='font-size:13px'>这条路径在日志里共出现 ").Append(ev.HintMentions).Append(" 次：")
                  .Append(verdict).Append("。下面是原文：</p>");
                sb.Append(pre);
                foreach (var l in ev.HintLines) sb.Append(E(l.Raw)).Append('\n');
                sb.Append("</pre>");
            }
            else if (hint is not null)
            {
                sb.Append("<p style='font-size:13px'>这条路径在日志里<b>一次都没出现</b>，也就是说日志本身完全没提到它。</p>");
            }
            else if (!string.IsNullOrWhiteSpace(r.ErrorDetail))
            {
                sb.Append(note).Append("任务记录里这条内容里抠不出路径，无法与日志对账。</p>");
            }

            sb.Append("</details>");
        }
    }

    /// <summary>回执的 ErrorDetail 原文做过无信息截断（100 字符），报告里也别铺满一屏。</summary>
    private static string Project(string s)
    {
        var t = s.Trim().Replace("\r", " ").Replace("\n", " ");
        return t.Length > 300 ? t[..300] + "…" : t;
    }

    /// <summary>错误码（十进制或 0x 十六进制）→ 人话；未知码原样回显。</summary>
    private static string CodeMeaning(string code)
    {
        if (code.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(code[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex))
            return ErrorTranslator.ReasonForCode(hex);
        return int.TryParse(code, out var c) ? ErrorTranslator.ReasonForCode(c) : "";
    }

    /// <summary>
    /// 从回执的 ErrorDetail 里抠出"任务记录声称的原因路径"，供与日志对账。抠不出就返回 null。
    /// </summary>
    private static string? HintPathFrom(string? errorDetail)
    {
        if (string.IsNullOrWhiteSpace(errorDetail)) return null;
        var viaRunner = RobocopyRunner.ExtractErrorPath(errorDetail);
        if (!string.IsNullOrWhiteSpace(viaRunner)) return viaRunner;
        // 兜底：真机那条是『新文件 … 9934 \\主机\共享\…\文件.txt』，按空白切开取最后一个像路径的片段。
        foreach (var token in errorDetail.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries).Reverse())
            if (token.StartsWith(@"\\", StringComparison.Ordinal) || token.Contains(@":\", StringComparison.Ordinal))
                return token.Trim();
        return null;
    }

    /// <summary>退出码单元格：-1 = 该通道没跑过，其余按位掩码给全含义（真机上 9 → 1+8）。</summary>
    private static string ExitCodeCell(int code)
        => code < 0 ? "未执行" : $"{code}（{ErrorTranslator.ExitCodeText(code)}）";

    /// <summary>
    /// 退出码人话（Bulk / Large 两通道分别给）。只有<b>确实失败</b>（robocopy 的 8/16 位）才输出：
    /// 成功码（0-7）不该出现在"错误"列里，否则整张表每行都挂一句"成功（有文件被复制）"。
    /// </summary>
    private static string ExitCodeNote(ObjectReceipt r)
    {
        var parts = new List<string>();
        if (r.RobocopyExitCodeBulk >= 0 && !RobocopyRunner.IsSuccess(r.RobocopyExitCodeBulk))
            parts.Add($"Bulk 退出码 {r.RobocopyExitCodeBulk}（{ErrorTranslator.ExitCodeText(r.RobocopyExitCodeBulk)}）");
        if (r.RobocopyExitCodeLarge >= 0 && !RobocopyRunner.IsSuccess(r.RobocopyExitCodeLarge))
            parts.Add($"Large 退出码 {r.RobocopyExitCodeLarge}（{ErrorTranslator.ExitCodeText(r.RobocopyExitCodeLarge)}）");
        return string.Join("；", parts);
    }

    /// <summary>
    /// 对象明细表"错误"列。只给<b>可核实</b>的内容：退出码位掩码的含义 + 指向证据区的深链。
    /// 原始 ErrorDetail 不在这里出现 —— 真机上它是一条 robocopy『新文件』状态行，
    /// 贴在"错误"列会让用户以为"文件明明传了却报错"（对账过程见证据区）。
    /// </summary>
    private static string ErrorCell(ObjectReceipt r, bool linked)
    {
        var parts = new List<string>();
        var note = ExitCodeNote(r);
        if (note.Length > 0) parts.Add(E(note));
        if (linked) parts.Add($"<a href='#ev-{E(r.ObjectId)}'>证据</a>");
        return string.Join("<br>", parts);
    }

    /// <summary>
    /// 各趟回执 [StartedUtc, CompletedUtc] 区间的并集秒数（"实际在传时间"）。
    /// 只依赖 append-only 回执，不新增任何采集数据；重叠区间只算一次，避免并发对象被重复计时。
    /// </summary>
    private static double MergedTransferSeconds(IReadOnlyList<ObjectReceipt> receipts)
    {
        var spans = receipts
            .Select(r => (Start: r.StartedUtc, End: r.CompletedUtc))
            .Where(s => s.End > s.Start)
            .OrderBy(s => s.Start)
            .ToList();
        var total = 0d;
        DateTime? cursor = null;
        foreach (var (start, end) in spans)
        {
            if (cursor is null || start > cursor.Value)
            {
                total += (end - start).TotalSeconds;
                cursor = end;
                continue;
            }
            if (end > cursor.Value)
            {
                total += (end - cursor.Value).TotalSeconds;
                cursor = end;
            }
        }
        return total;
    }

    private static string PhaseTag(JobPhase p) => p switch
    {
        JobPhase.Completed => "<span class='tag green'>已完成</span>",
        JobPhase.CompletedWithErrors => "<span class='tag amber'>完成(有错误)</span>",
        JobPhase.Running => "<span class='tag gray'>运行中</span>",
        JobPhase.Paused => "<span class='tag amber'>已暂停</span>",
        JobPhase.Interrupted => "<span class='tag amber'>已中断(可续传)</span>",
        JobPhase.Failed or JobPhase.Canceled => $"<span class='tag red'>{p}</span>",
        _ => $"<span class='tag gray'>{p}</span>"
    };

    private static string StatusText(ObjectStatus s) => s switch
    {
        ObjectStatus.Completed => "<span class='ok'>✔ 完成</span>",
        ObjectStatus.CompletedWithErrors => "<span class='warn'>◐ 完成(有错误)</span>",
        ObjectStatus.Failed => "<span class='bad'>✘ 失败</span>",
        ObjectStatus.Interrupted => "<span class='warn'>‖ 中断</span>",
        ObjectStatus.Skipped => "<span class='tag gray'>跳过</span>",
        _ => s.ToString()
    };

    /// <summary>
    /// 最简 HTML 转义：只转义 5 个结构字符，中文原样输出。
    /// （HtmlEncoder.Default 会把所有非 ASCII 转成 &#x....; 实体，浏览器能渲染但文本查看器里像乱码。）
    /// </summary>
    private static string E(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;").Replace("'", "&#39;");
    }

}
