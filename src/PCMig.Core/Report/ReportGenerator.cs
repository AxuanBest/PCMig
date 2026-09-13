using System.Text;
using PCMig.Core.Jobs;
using PCMig.Core.Models;
using PCMig.Core.State;
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

        // ---- 结论与建议（人话区，置顶） ----
        var incomplete = (_ctx.Plan?.Objects ?? [])
            .Where(o => latest.TryGetValue(o.ObjectId, out var r) &&
                        r.Status is ObjectStatus.Failed or ObjectStatus.CompletedWithErrors)
            .ToList();
        if (state.Phase == JobPhase.Completed)
        {
            sb.Append("<h2>✅ 结论</h2><p class='ok' style='font-size:15px'>迁移全部完成。建议回到工具点「验证」做一次数据一致性核对。</p>");
        }
        else if (incomplete.Count > 0)
        {
            sb.Append("<h2>⚠ 结论与建议</h2><p style='font-size:15px'>以下内容尚未传完，<b>数据没有丢</b>——已传部分都在，处理后点「恢复任务」即可补齐：</p><ul>");
            foreach (var o in incomplete)
            {
                var r = latest[o.ObjectId];
                var friendlyName = o.SourcePath.TrimEnd('\\').Split('\\').LastOrDefault() ?? o.SourcePath;
                sb.Append($"<li><b>{E(friendlyName)}</b>（已传 {Format.Bytes(r.TargetBytes)}）");
                var explain = ErrorTranslator.Advice(r.ErrorDetail);
                if (explain.Length > 0) sb.Append($"<br>{E(explain)}");
                else if (!string.IsNullOrWhiteSpace(r.ErrorDetail)) sb.Append($"<br><span class='mono'>{E(r.ErrorDetail)}</span>");
                sb.Append("</li>");
            }
            sb.Append("</ul>");
        }

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
                    $"<td>{E(r.ErrorDetail ?? "")}</td></tr>");
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
        sb.Append($"<p style='color:#9ca3af;margin-top:40px'>PCMig v{ver} —— 报告由引擎自动生成，可作为验收与审计依据。</p></body></html>");

        Directory.CreateDirectory(_ctx.ReportDir);
        var path = Path.Combine(_ctx.ReportDir, $"migration-report-{DateTime.Now:yyyyMMdd-HHmmss}.html");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        _log.Information("报告已生成: {Path}", path);
        return path;
    }

    private static void Card(StringBuilder sb, string label, string value)
        => sb.Append($"<div class='card'><div class='num'>{value}</div><div class='label'>{label}</div></div>");

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
