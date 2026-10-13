namespace PCMig.Core.Report;

/// <summary>
/// ★ UX-5（2026-10-11 用户要求）★ 报告的**视觉语言唯一来源**：
///   迁移报告（<see cref="ReportGenerator"/>）与验证报告（<see cref="VerifyReportGenerator"/>）
///   共用同一份 CSS 与同一套组件类名（cards / card / table / tag / note / mono），
///   这样两份报告看起来是同一个产品的产物，同时**各自记录各自的业务事实**：
///   迁移报告讲"传了什么、失败在哪"，验证报告讲"核对到了什么、没核对成什么"。
///   任何一份报告都不允许把 L1 数量/字节对账说成"完整内容哈希验证"。
/// </summary>
public static class ReportStyle
{
    /// <summary>
    /// 共用样式块（含迁移报告原有全部规则 + 验证报告的大表浏览规则）。
    /// 已被两份报告共用：改动这里会同时影响两份报告的观感，因此只增不改语义。
    /// </summary>
    public const string Css = """
<style>
body{font-family:"Microsoft YaHei",sans-serif;margin:32px;color:#222;background:#fafafa}
h1{font-size:24px} h2{font-size:18px;border-bottom:2px solid #3b82f6;padding-bottom:6px;margin-top:32px}
h3{font-size:15px;margin:18px 0 4px}
.cards{display:flex;gap:16px;flex-wrap:wrap;margin:16px 0}
.card{background:#fff;border:1px solid #e5e7eb;border-radius:8px;padding:16px 24px;min-width:160px}
.card .num{font-size:26px;font-weight:700;color:#1d4ed8} .card .label{color:#6b7280;font-size:13px}
table{border-collapse:collapse;width:100%;background:#fff;margin-top:8px;font-size:13px}
th,td{border:1px solid #e5e7eb;padding:8px 10px;text-align:left;vertical-align:top}
th{background:#eff6ff} tr:nth-child(even){background:#f9fafb}
.ok{color:#16a34a;font-weight:600} .bad{color:#dc2626;font-weight:600} .warn{color:#d97706;font-weight:600}
.mono{font-family:Consolas,monospace;font-size:12px}
.tag{display:inline-block;padding:2px 8px;border-radius:10px;font-size:12px;color:#fff}
.tag.green{background:#16a34a}.tag.red{background:#dc2626}.tag.amber{background:#d97706}.tag.gray{background:#6b7280}
.note{color:#6b7280;font-size:12px}
ol,ul{margin:6px 0 6px 22px}
/* ---- 大量数据浏览（验证报告：上千个对象/差异清单）---- */
.grid{font-size:12px}
.grid thead th{position:sticky;top:0;z-index:2}
.toolbar{position:sticky;top:0;z-index:3;background:#fafafa;display:flex;gap:12px;align-items:center;
         flex-wrap:wrap;padding:8px 0;border-bottom:1px solid #e5e7eb}
.toolbar input[type=text]{font:inherit;padding:4px 8px;border:1px solid #d1d5db;border-radius:6px;min-width:260px}
.samples{max-height:320px;overflow:auto;border:1px solid #e5e7eb;border-radius:6px;background:#fff}
details{margin:6px 0 14px}
summary{cursor:pointer;font-weight:600;color:#1d4ed8}
</style>
""";

    /// <summary>
    /// 验证报告的大表过滤脚本（自包含、无外部依赖）：关键词过滤 + "只看需要处理的对象" + 计数。
    /// 纯前端，不发起任何请求，报告可离线打开。
    /// </summary>
    public const string FilterScript = """
<script>
function pcmigFilter(){
  var q=(document.getElementById('q').value||'').toLowerCase();
  var onlyBad=document.getElementById('onlybad').checked;
  var rows=document.querySelectorAll('tr[data-obj]');
  var shown=0;
  for(var i=0;i<rows.length;i++){
    var r=rows[i];
    var bad=r.getAttribute('data-bad')==='1';
    var txt=r.getAttribute('data-text')||'';
    var vis=(!onlyBad||bad)&&(q===''||txt.indexOf(q)>=0);
    if(r.classList.contains('pcmig-more'))vis=false;
    r.style.display=vis?'':'none';
    if(vis)shown++;
  }
  document.getElementById('shown').textContent=shown;
  var det=document.querySelectorAll('details[data-obj]');
  for(var j=0;j<det.length;j++){
    var d=det[j];
    var dtext=d.getAttribute('data-text')||'';
    var dbad=d.getAttribute('data-bad')==='1';
    d.style.display=((!onlyBad||dbad)&&(q===''||dtext.indexOf(q)>=0))?'':'none';
  }
}
function pcmigMore(){
  var more=document.querySelectorAll('tr.pcmig-more');
  var n=0;
  for(var i=0;i<more.length&&n<200;i++){ more[i].classList.remove('pcmig-more'); n++; }
  var left=document.querySelectorAll('tr.pcmig-more').length;
  var b=document.getElementById('morebtn');
  if(b){ b.textContent=left>0?('显示更多（还有 '+left+' 行）'):'已全部显示'; if(left===0)b.disabled=true; }
  pcmigFilter();
}
function pcmigExpandAll(open){
  var det=document.querySelectorAll('details[data-obj]');
  for(var i=0;i<det.length;i++){ det[i].open=open; }
}
/* 表头排序：只排已显示的行，数字列按 data-num-<key>，文本列按单元格文本 */
function pcmigSort(tableId,key,numeric){
  var tb=document.getElementById(tableId);
  if(!tb)return;
  var body=tb.tBodies[0];
  var rows=Array.prototype.slice.call(body.rows);
  var dir=parseInt(tb.getAttribute('data-dir-'+key)||'0',10)===1?-1:1;
  tb.setAttribute('data-dir-'+key,dir===1?'1':'0');
  rows.sort(function(a,b){
    var av,bv;
    if(numeric){
      av=parseFloat(a.getAttribute('data-num-'+key)||'0');
      bv=parseFloat(b.getAttribute('data-num-'+key)||'0');
      return (av-bv)*dir;
    }
    av=(a.getAttribute('data-sort-'+key)||'').toLowerCase();
    bv=(b.getAttribute('data-sort-'+key)||'').toLowerCase();
    return av<bv?-dir:(av>bv?dir:0);
  });
  for(var i=0;i<rows.length;i++){ body.appendChild(rows[i]); }
}
</script>
""";

    /// <summary>
    /// 最简 HTML 转义：只转义 5 个结构字符，中文原样输出。
    /// （HtmlEncoder.Default 会把所有非 ASCII 转成 &#x....; 实体，浏览器能渲染但文本查看器里像乱码。）
    /// </summary>
    public static string E(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;").Replace("'", "&#39;");
    }
}