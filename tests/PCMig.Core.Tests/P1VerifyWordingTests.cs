using PCMig.Core.Models;
using PCMig.Core.Verify;
using PCMig.WinUI.Presentation;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ m03985 执行令 T01/T02（提示卡只给短结论）★ 验证文案的**两套口径**契约：
///   · 左侧 `ShellHintCard` 用 <see cref="MigrationSessionViewModel.DescribeVerifyHint"/> 的一句话结论
///     （状态 + 四态数量，**一个 object-xxxxx 都不出现**）；
///   · Step4 正文/结论行用 <see cref="MigrationSessionViewModel.DescribeVerifyOutcome"/>
///     （最多列 <see cref="MigrationSessionViewModel.MaxObjectIdsInSummary"/> 个编号示例）。
///
/// 为什么必须有这份测试：现场（2026-10-11）一次 413/1000 对象的验证把全部编号拼进结论行，
/// 左侧提示卡与 Step4 正文被铺成满屏文本。当时只有"源码契约"与真实界面实测守着这两条，
/// 文案函数本身没有任何单元测试 —— 这里把措辞、上限与接线一次性钉死。
///
/// 不变量：
///   ① 提示卡永远不含对象编号；② 结论行的编号示例有硬上限；
///   ③ L1 通过必须带口径限定语（数量/字节一致 ≠ 内容一致），绝不说成"完整性核对通过"；
///   ④ L2 抽样通过不得说成"全部文件已核对"；
///   ⑤ "无法确认"（源/目标不可达、枚举不完整）不得说成"存在差异"。
/// </summary>
public sealed class P1VerifyWordingTests
{
    // ── 夹具：只构造内存中的报告，不碰磁盘 ─────────────────────────────────

    // StatusText 是只读计算属性（由 Status 派生），夹具因此只设 Status。
    private static ObjectVerifyResult Obj(string id, string status)
        => new() { ObjectId = id, Status = status };

    private static VerifyReport Report(bool pass, params ObjectVerifyResult[] objects)
    {
        var report = new VerifyReport { JobId = "JOB-WORDING", OverallPass = pass, Level = VerifyLevel.L1_CountSize };
        foreach (var o in objects) report.Objects.Add(o);
        return report;
    }

    private static VerifyReport Many(string status, int count)
    {
        var report = new VerifyReport { JobId = "JOB-WORDING", OverallPass = false };
        for (var i = 1; i <= count; i++) report.Objects.Add(Obj($"object-{i:000000}", status));
        return report;
    }

    private static int Occurrences(string text, string needle)
    {
        var n = 0;
        var idx = text.IndexOf(needle, StringComparison.Ordinal);
        while (idx >= 0) { n++; idx = text.IndexOf(needle, idx + needle.Length, StringComparison.Ordinal); }
        return n;
    }

    // ── ① 提示卡：一个编号都不列，长度可控 ─────────────────────────────────

    [Fact]
    public void P1_12_提示卡一句话结论不列任何对象编号()
    {
        var report = new VerifyReport { JobId = "J", OverallPass = false };
        report.Objects.Add(Obj("object-000001", "OK"));
        for (var i = 2; i <= 413; i++) report.Objects.Add(Obj($"object-{i:000000}", "MISMATCH"));

        var hint = MigrationSessionViewModel.DescribeVerifyHint(report, VerifyLevel.L1_CountSize);

        Assert.DoesNotContain("object-", hint);            // ★ 核心：现场 413 个编号的回归线 ★
        Assert.Contains("L1 对账未通过", hint);
        Assert.Contains("通过 1", hint);
        Assert.Contains("差异 412", hint);
        Assert.Contains("无法确认 0", hint);
        Assert.Contains("点「打开验证报告」查看详细清单。", hint);
        Assert.True(hint.Length < 200, $"提示卡一句话结论过长（{hint.Length} 字符）：{hint}");
    }

    [Fact]
    public void P1_12_提示卡按四态如实分组且零值项不出现()
    {
        var mixed = new VerifyReport { JobId = "J", OverallPass = false };
        mixed.Objects.Add(Obj("object-000001", "MISMATCH"));
        mixed.Objects.Add(Obj("object-000002", "EMPTY"));
        mixed.Objects.Add(Obj("object-000003", "EMPTY"));
        mixed.Objects.Add(Obj("object-000004", "CANCELED"));

        var hint = MigrationSessionViewModel.DescribeVerifyHint(mixed, VerifyLevel.L1_CountSize);
        Assert.Contains("无可核对数据 2", hint);
        Assert.Contains("已取消 1", hint);

        var clean = Report(true, Obj("object-000001", "OK"));
        var cleanHint = MigrationSessionViewModel.DescribeVerifyHint(clean, VerifyLevel.L1_CountSize);
        Assert.DoesNotContain("无可核对数据", cleanHint);
        Assert.DoesNotContain("已取消", cleanHint);
    }

    [Theory]
    [InlineData(VerifyLevel.L1_CountSize, "L1 对账完成")]
    [InlineData(VerifyLevel.L2_SampleHash, "L2 抽样哈希核对完成")]
    public void P1_12_提示卡层级措辞与通过口径正确(VerifyLevel level, string expectedHead)
    {
        var report = Report(true, Obj("object-000001", "OK"));
        var hint = MigrationSessionViewModel.DescribeVerifyHint(report, level);

        Assert.Contains(expectedHead, hint);
        // ★ T07 ★ 任何层级都不得把"通过"扩大成"内容一致/完整"
        Assert.DoesNotContain("完整性核对通过", hint);
        Assert.DoesNotContain("内容一致）", hint.Replace("不等于内容一致）", ""));
        Assert.DoesNotContain("全部文件内容一致", hint);

        // L1 通过必须带口径限定语；L2 不带（它本身已含抽样语义）
        Assert.Equal(level < VerifyLevel.L2_SampleHash, hint.Contains("不等于内容一致"));
    }

    // ── ② 结论行：编号示例有硬上限 ────────────────────────────────────────

    [Fact]
    public void P1_12_结论行的对象编号示例最多三个()
    {
        Assert.Equal(3, MigrationSessionViewModel.MaxObjectIdsInSummary);

        var outcome = MigrationSessionViewModel.DescribeVerifyOutcome(
            Many("MISMATCH", 412), VerifyLevel.L1_CountSize);

        Assert.Equal(MigrationSessionViewModel.MaxObjectIdsInSummary, Occurrences(outcome, "object-"));
        Assert.Contains("存在差异 412 个对象", outcome);
        Assert.Contains("等 412 个", outcome);
        Assert.Contains("逐对象明细、差异文件清单与建议操作见验证报告。", outcome);
    }

    [Fact]
    public void P1_12_编号不超过上限时全部列出且不出现等N个()
    {
        var outcome = MigrationSessionViewModel.DescribeVerifyOutcome(
            Many("MISMATCH", 2), VerifyLevel.L1_CountSize);

        Assert.Equal(2, Occurrences(outcome, "object-"));
        Assert.Contains("（object-000001、object-000002）", outcome);
        Assert.DoesNotContain("等 2 个", outcome);
    }

    [Fact]
    public void P1_12_无法确认不得说成存在差异()
    {
        var outcome = MigrationSessionViewModel.DescribeVerifyOutcome(
            Many("UNKNOWN", 5), VerifyLevel.L1_CountSize);

        Assert.Contains("无法确认 5 个对象", outcome);
        Assert.Contains("属「没核对成」而非「不一致」", outcome);
        Assert.DoesNotContain("存在差异", outcome);
        Assert.Equal(MigrationSessionViewModel.MaxObjectIdsInSummary, Occurrences(outcome, "object-"));
    }

    [Fact]
    public void P1_12_结论行在两种层级下的通过与未通过措辞()
    {
        var pass = Report(true, Obj("object-000001", "OK"));
        var l1 = MigrationSessionViewModel.DescribeVerifyOutcome(pass, VerifyLevel.L1_CountSize);
        Assert.Contains(MigrationSessionViewModel.L1ResultText, l1);
        Assert.DoesNotContain("完整性核对通过", l1);

        var l2Pass = Report(true, Obj("object-000001", "OK"));
        l2Pass.Objects[0].HashSampled = 4;
        var l2 = MigrationSessionViewModel.DescribeVerifyOutcome(l2Pass, VerifyLevel.L2_SampleHash);
        Assert.Contains("本次另有 4 个文件做了 SHA-256 双向内容核对", l2);
        Assert.Contains("抽样通过不等于全部文件内容一致", l2);

        var noSample = Report(true, Obj("object-000001", "OK"));
        var l2NoSample = MigrationSessionViewModel.DescribeVerifyOutcome(noSample, VerifyLevel.L2_SampleHash);
        Assert.Contains(MigrationSessionViewModel.NoHashSampleText, l2NoSample);

        var empty = MigrationSessionViewModel.DescribeVerifyOutcome(new VerifyReport { JobId = "J" }, VerifyLevel.L1_CountSize);
        Assert.Contains("验证未执行", empty);
    }

    /// <summary>
    /// ★ K5（2026-10-12 实测缺口）★ 验证动作必须写 **app 日志**（不只写本页日志卡）：
    /// 现场读 `%ProgramData%\PCMig\Logs\app-*.log` 时 grep「验证」零命中，无法定位"点击→开始/结束"时刻，
    /// 只能拿 `verify-report.json` 的 mtime 反推。开始 / 结束 / 取消 / 闸门拒绝四条路径都要留证据。
    /// </summary>
    [Fact]
    public void P1_12_验证动作必须写app日志以定位开始结束与取消()
    {
        var vm = CodeOnly(VmCode);

        Assert.Contains("_log.Information(\"验证开始：级别={Level}，任务={JobId}，计划对象数={ObjectCount}", vm);
        Assert.Contains("\"验证结束：任务={JobId}，级别={Level}，运行号={RunId}，耗时={Elapsed} ms", vm);
        Assert.Contains("_log.Warning(\"验证已取消：没有结论（取消不等于通过）", vm);
        Assert.Contains("_log.Warning(\"验证被闸门拒绝（未开始核对）", vm);
        Assert.Contains("未写出任何验证报告（取消不产生报告）", vm);

        // 计时器必须在**进入方法时**就起（否则"点击 → 验证开始"的那段时间没有证据）
        var head = Segment(vm, "public async Task VerifyAsync(", "var ctx = Ctx;");
        Assert.Contains("System.Diagnostics.Stopwatch.StartNew()", head);
    }

    // ── ③ 接线：两套口径各归其位，旧的无限拼接不许回来 ─────────────────────

    [Fact]
    public void P1_12_提示卡与结论行分别接线到自己的出口()
    {
        var vm = CodeOnly(VmCode);

        Assert.Contains("LastVerifyResultText = DescribeVerifyOutcome(report, level);", vm);
        Assert.Contains("LastVerifyHintText = DescribeVerifyHint(report, level);", vm);
        Assert.Contains("SetOperational(LastVerifyHintText);", vm);

        // ★ 回归线：旧写法把全部差异对象编号 string.Join 进结论行，必须不再出现 ★
        Assert.DoesNotContain("string.Join(\"、\", bad)", vm);
        Assert.DoesNotContain("string.Join(\"、\", mismatch)", vm);

        // 结论行的示例必须走 IdSample（有上限），不许直接 string.Join 全部编号
        var outcomeBody = Segment(vm, "public static string DescribeVerifyOutcome(", "private static string IdSample(");
        Assert.Contains("IdSample(mismatch.Select(o => o.ObjectId))", outcomeBody);
        Assert.Contains("IdSample(unknown.Select(o => o.ObjectId))", outcomeBody);

        // 提示卡正文里不许出现任何 object- id 拼接
        var hintBody = Segment(vm, "public static string DescribeVerifyHint(", "public async Task<VerifyGateResult> EvaluateVerifyGateAsync(");
        Assert.DoesNotContain("ObjectId", hintBody);
        Assert.DoesNotContain("object-", hintBody);
    }

    // ── 工具 ──────────────────────────────────────────────────────────────

    private static string VmCode => Read("src", "PCMig.WinUI", "Presentation", "MigrationSessionViewModel.cs");

    /// <summary>负向断言前必须剥掉行注释（本项目已踩过两次：解释历史的注释会被误判成违规）。</summary>
    private static string CodeOnly(string source)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var idx = lines[i].IndexOf("//", StringComparison.Ordinal);
            if (idx >= 0) lines[i] = lines[i][..idx];
        }
        return string.Join("\n", lines);
    }

    private static string Segment(string text, string from, string to)
    {
        var i = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(i >= 0, $"没找到起点：{from}");
        var j = text.IndexOf(to, i, StringComparison.Ordinal);
        Assert.True(j > i, $"没找到终点：{to}");
        return text[i..j];
    }

    private static string Read(params string[] parts)
    {
        var path = Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), "缺少文件：" + path);
        return File.ReadAllText(path);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}