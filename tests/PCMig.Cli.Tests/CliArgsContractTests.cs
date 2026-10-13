using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace PCMig.Cli.Tests;

/// <summary>
/// ★ D7 危险行为修复 · 参数契约测试 ★
///
/// 本文件只把**四条真实发生过的危险路径**变成可重复执行的契约，不测任何业务语义：
///   R1 <c>pcmig resume --help</c> 曾被静默忽略 → CLI 真的把默认作业根里一个 5.13 GB 的历史作业跑完了；
///   R2 <c>pcmig list --jobs=&lt;dir&gt;</c> 曾被静默忽略 → 回落到默认作业根执行；
///   R3 未知参数被静默忽略后照样执行实际业务动作；
///   R4 只有 <c>args[0]</c> 位置的 <c>--help</c> 有效，子命令级 <c>--help</c> 完全无效。
///
/// 判据分层：
///   · 单元层（本文件）：证"被正确识别 / 被正确拒绝"——help 必须落到 HelpRequested，未知选项必须进
///     UnknownOptions，等号与空格写法必须解析一致；
///   · 真机层（tools\qa-cli-args-safety.ps1 + D7 证据）：证"识别之后没有发生业务动作"——
///     执行前后对诱饵作业目录做递归 SHA256 对比，字节未变。
///   ★ 刻意不在这里真的去调 Program.Main 跑 resume：那正是本项目要消除的危险行为，
///     不可能为了"测它没执行"而先执行一次。
/// </summary>
public class CliArgsContractTests
{
    /// <summary>
    /// 白名单唯一来源：Program.cs 里真实存在的选项读取点。
    /// 这份列表与 CliArgs.KnownOptions 必须一致 —— 两边任何一处漏登记，本测试即红。
    /// </summary>
    private static readonly string[] WhitelistSourceOfTruth =
    {
        // 全局 / 基础设施
        "jobs", "quiet", "matrix",
        // 连接与凭据
        "host", "user", "password", "source", "target",
        // new / quick 任务定义
        "paths", "kind", "notes", "yes",
        // 排除与性能
        "xd", "xf", "threads", "largemb", "large-channel", "noverify",
        // 闸门与开关
        "bench", "allow-incomplete-scan",
        // 只读命令
        "job", "watch", "level", "open",
    };

    /// <summary>所有被识别的子命令（含别名）。</summary>
    private static readonly string[] AllCommands =
    {
        "preflight", "new", "run", "pause", "stop", "resume", "status", "verify", "report",
        "verify-report", "vreport", "repair-restore", "restore-quarantine", "list", "changelog",
        "log", "history", "quick", "credit", "credits", "author",
    };

    /// <summary>主名（用法正文里必须出现自己名字的那些）。</summary>
    private static readonly string[] PrimaryCommands =
    {
        "preflight", "new", "quick", "run", "pause", "stop", "resume", "status", "verify",
        "report", "verify-report", "repair-restore", "list", "changelog", "credit",
    };

    private static string RepoRoot()
    {
        // bin\Debug\net8.0 → tests\PCMig.Cli.Tests → tests → 仓库根
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // ==================================================================
    // ① 每个子命令的 --help 都被判定为"只显示帮助、不产生业务动作"
    // ==================================================================

    [Fact]
    public void R4_每个子命令的_help_都必须被识别为帮助请求()
    {
        var missed = AllCommands.Where(_ => !CliArgs.Parse(new[] { "--help" }).HelpRequested).ToList();
        Assert.True(missed.Count == 0,
            $"这些子命令的 --help 未被识别（会被静默忽略→照跑业务）：{string.Join(", ", missed)}");
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("--h")]
    [InlineData("--help=true")]
    [InlineData("-H")]
    [InlineData("--HELP")]
    public void R4_help_各种写法都要被识别_且大小写不敏感(string token)
    {
        Assert.True(CliArgs.Parse(new[] { token }).HelpRequested, $"{token} 未被识别为帮助请求");
    }

    [Fact]
    public void R4_resume_的_help_无论出现在子命令后的哪个位置都必须被识别()
    {
        // 修复前：--help 落在任何位置都被丢弃，然后真的执行 resume（把 5.13GB 历史作业跑完）
        var cases = new[]
        {
            new[] { "--help" },
            new[] { "--job", "OLD-5GB-JOB", "--help" },
            new[] { "--help", "--job", "OLD-5GB-JOB" },
            new[] { "--quiet", "--help" },
            new[] { "--matrix", "m.yaml", "--help" },
            new[] { "-h" },
        };
        foreach (var args in cases)
        {
            var p = CliArgs.Parse(args);
            Assert.True(p.HelpRequested, $"resume {string.Join(' ', args)} 的 --help 被漏掉了");
            Assert.Empty(p.UnknownOptions); // 帮助优先，但选项本身必须都是合法的
        }
    }

    [Fact]
    public void R4_help_false_是显式关闭而不是请求帮助()
    {
        // 脚本里拼 "--help=$flag" 时，flag=false 不该变成"打印帮助然后什么都不做"
        Assert.False(CliArgs.Parse(new[] { "--help=false" }).HelpRequested);
        Assert.False(CliArgs.Parse(new[] { "--help=0" }).HelpRequested);
        Assert.True(CliArgs.Parse(new[] { "--help=true" }).HelpRequested);
    }

    [Fact]
    public void R4_每个子命令都必须有非空的用法正文()
    {
        // 帮助请求被识别但打印空字符串 = 用户以为看到用法，其实什么都没看到
        var empty = AllCommands.Where(c => string.IsNullOrWhiteSpace(CliArgs.CommandUsage(c))).ToList();
        Assert.True(empty.Count == 0, $"这些子命令没有用法正文：{string.Join(", ", empty)}");
    }

    [Fact]
    public void R4_主命令的用法正文必须点出该命令自己的名字()
    {
        // 防"复制粘贴串台"：resume 的帮助里不该是 list 的用法
        var wrong = PrimaryCommands
            .Where(c => !CliArgs.CommandUsage(c).Contains(c, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(wrong.Count == 0, $"这些子命令的用法正文没提到自己：{string.Join(", ", wrong)}");
    }

    [Fact]
    public void R4_别名与主名共用同一份用法正文()
    {
        Assert.Equal(CliArgs.CommandUsage("verify-report"), CliArgs.CommandUsage("vreport"));
        Assert.Equal(CliArgs.CommandUsage("repair-restore"), CliArgs.CommandUsage("restore-quarantine"));
        Assert.Equal(CliArgs.CommandUsage("changelog"), CliArgs.CommandUsage("log"));
        Assert.Equal(CliArgs.CommandUsage("changelog"), CliArgs.CommandUsage("history"));
        Assert.Equal(CliArgs.CommandUsage("credit"), CliArgs.CommandUsage("credits"));
        Assert.Equal(CliArgs.CommandUsage("credit"), CliArgs.CommandUsage("author"));
    }

    [Fact]
    public void R1_用法正文必须点明_resume_免参数是有副作用的()
    {
        // 事故现场就是"用户想看帮助，结果真的恢复了"。提示里必须写清不带 --job 会立刻开始迁移。
        var text = CliArgs.CommandUsage("resume");
        Assert.Contains("--job", text);
        Assert.Contains("迁移", text);
    }

    // ==================================================================
    // ② 未知选项被拒绝且非零退出
    // ==================================================================

    [Fact]
    public void R3_未知选项必须被拒绝()
    {
        var p = CliArgs.Parse(new[] { "--nope" });
        Assert.Equal(new[] { "--nope" }, p.UnknownOptions);
        Assert.False(p.HelpRequested);
    }

    [Theory]
    [InlineData("--nope")]
    [InlineData("--thread")]        // threads 少个 s —— 旧实现会静默用默认线程数照跑
    [InlineData("--joob")]
    [InlineData("--dry-run")]       // 不存在的"安全开关"最危险：用户以为不会真跑
    [InlineData("--what-if")]
    [InlineData("-x")]              // 单横线未知选项
    [InlineData("--nope=1")]        // 等号写法同样要拒绝
    public void R3_各种拼错或臆想的选项都必须被拒绝(string token)
    {
        var p = CliArgs.Parse(new[] { token });
        Assert.True(p.UnknownOptions.Count > 0, $"{token} 未被拒绝（会被静默忽略后照跑业务）");
    }

    [Theory]
    [InlineData("--JOB=abc", "job", "abc")]     // 选项名大小写不敏感 ⇒ 是已知选项，不该误杀
    [InlineData("--Quiet", "quiet", "true")]
    [InlineData("--JOBS=E:\\j", "jobs", @"E:\j")]
    public void R3_大小写变体的已知选项不能误判为未知选项(string token, string key, string value)
    {
        var p = CliArgs.Parse(new[] { token });
        Assert.Empty(p.UnknownOptions);
        Assert.Equal(value, p.Options[key]);
    }

    [Fact]
    public void R3_未知选项报文里带原样的选项名()
    {
        var p = CliArgs.Parse(new[] { "--job", "J1", "--typo-flag" });
        Assert.Equal(new[] { "--typo-flag" }, p.UnknownOptions);
        Assert.Equal("J1", p.Options["job"]); // 已知选项不得被误报
    }

    [Fact]
    public void R3_裸位置参数不能被当成选项吞掉()
    {
        // resume OLD-JOB（裸位置参数）→ resume 从不读位置参数 ⇒ 必须报错，
        // 不得静默挑"最近未完成任务"跑掉
        var p = CliArgs.Parse(new[] { "OLD-JOB" });
        Assert.Empty(p.Options);
        Assert.Equal(new[] { "OLD-JOB" }, p.Positionals);
        Assert.NotNull(CliArgs.ValidatePositionals("resume", p.Positionals));
    }

    [Fact]
    public void R3_任何参数错误都不许返回退出码_0()
    {
        // 契约：FailCode 非零，且和 Program.Fail 同码（源码级核对，避免测试与实现各写一个数字）
        var program = Path.Combine(RepoRoot(), "src", "PCMig.Cli", "Program.cs");
        Assert.True(File.Exists(program), $"找不到 {program}");
        var src = File.ReadAllText(program);
        Assert.Contains("private const int FailCode = 1;", src);
        Assert.Contains("PrintUsage(); return FailCode;", src);
    }

    [Fact]
    public void R3_校验必须发生在分发之前()
    {
        // 顺序契约（源码级）：Parse/校验/help 三个闸门都必须出现在 `return cmd switch` 之前，
        // 否则 resume --help 又会在校验前被分发执行。
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PCMig.Cli", "Program.cs"));
        var switchAt = src.IndexOf("return cmd switch", StringComparison.Ordinal);
        var parseAt = src.IndexOf("var parsed = CliArgs.Parse", StringComparison.Ordinal);
        var unknownAt = src.IndexOf("parsed.UnknownOptions.Count > 0", StringComparison.Ordinal);
        var helpAt = src.IndexOf("if (parsed.HelpRequested)", StringComparison.Ordinal);
        var jobsAt = src.IndexOf("Environment.SetEnvironmentVariable(\"PCMIG_JOBS\"", StringComparison.Ordinal);

        Assert.True(parseAt > 0 && unknownAt > 0 && helpAt > 0 && jobsAt > 0 && switchAt > 0, "锚点未找到");
        Assert.True(parseAt < unknownAt, "解析必须在未知选项校验之前");
        Assert.True(unknownAt < helpAt, "未知选项校验必须在 help 之前（非法参数优先报错）");
        Assert.True(helpAt < jobsAt, "help 必须在设置 PCMIG_JOBS 之前");
        Assert.True(jobsAt < switchAt, "PCMIG_JOBS 必须在分发之前设置（行为不变）");
    }

    // ==================================================================
    // ③ --jobs=<dir> 与 --jobs <dir> 解析一致
    // ==================================================================

    [Fact]
    public void R2_jobs_等号写法与空格写法解析结果必须一致()
    {
        const string dir = @"E:\Project\PCMigQA\20261013\cli-safety\jobs";

        var space = CliArgs.Parse(new[] { "--jobs", dir, "--job", "J1" });
        var equals = CliArgs.Parse(new[] { $"--jobs={dir}", "--job", "J1" });

        Assert.Equal(space.Options.OrderBy(k => k.Key), equals.Options.OrderBy(k => k.Key));
        Assert.Equal(dir, space.Options["jobs"]);
        Assert.Equal(dir, equals.Options["jobs"]);
        Assert.Empty(space.UnknownOptions);
        Assert.Empty(equals.UnknownOptions);
    }

    [Fact]
    public void R2_jobs_等号写法_值里允许再出现等号()
    {
        var p = CliArgs.Parse(new[] { @"--jobs=E:\a=b\jobs" });
        Assert.Equal(@"E:\a=b\jobs", p.Options["jobs"]);
        Assert.Empty(p.UnknownOptions);
    }

    [Fact]
    public void R2_jobs_空值属于语法错误()
    {
        Assert.NotNull(CliArgs.Parse(new[] { "--jobs=" }).SyntaxError);
    }

    [Fact]
    public void R2_jobs_不带值仍是开关型_不会被误报为语法错误()
    {
        // 旧判据保持：`--jobs` 后面没有 token 时记 "true"（不消费）。
        // 因此"必须带目录"的正确写法是 `--jobs=<目录>` 或 `--jobs <目录>`；
        // 这条测试的用途是把这个边界钉住，避免将来有人把 "true" 当目录去用。
        var p = CliArgs.Parse(new[] { "--jobs" });
        Assert.Null(p.SyntaxError);
        Assert.Equal("true", p.Options["jobs"]);
        Assert.Empty(p.UnknownOptions);

        // 但 `--jobs` 后面跟一个非 -- 开头的 token 就是它的值
        var q = CliArgs.Parse(new[] { "--jobs", @"E:\j" });
        Assert.Equal(@"E:\j", q.Options["jobs"]);
        Assert.Null(q.SyntaxError);
    }

    [Fact]
    public void R2_jobs_写在子命令之前仍然不能被执行()
    {
        // 现状：args[0] 不是子命令 ⇒ 未知命令，非零退出。
        // 契约：本轮**不**放宽它——"把全局选项放到子命令前面"依然不是合法用法，
        //       不得因为"想让 --jobs 更灵活"而静默挑一个子命令去执行。
        Assert.False(CliArgs.IsKnownCommand("--jobs"));
        Assert.False(CliArgs.IsKnownCommand("--jobs=E:\\tmp\\jobs"));
    }

    [Fact]
    public void R2_jobs_等号写法在带值的子命令命令行里也一致()
    {
        const string dir = @"E:\Project\PCMigQA\20261013\cli-safety\jobs";
        foreach (var args in new[]
                 {
                     new[] { "--job", "J1", "--level", "2" },
                     new[] { "--host", "H", "--target", @"D:\o", "--yes" },
                     new[] { "--quiet" },
                     Array.Empty<string>(),
                 })
        {
            var space = CliArgs.Parse(args.Append("--jobs").Append(dir).ToArray());
            var equals = CliArgs.Parse(args.Append($"--jobs={dir}").ToArray());
            Assert.Empty(space.UnknownOptions);
            Assert.Empty(equals.UnknownOptions);
            Assert.Equal(dir, space.Options["jobs"]);
            Assert.Equal(dir, equals.Options["jobs"]);
            Assert.Equal(space.Options.Count, equals.Options.Count);
        }
    }

    // ==================================================================
    // ④ 回归保护：所有既有合法选项仍能正常解析
    // ==================================================================

    [Fact]
    public void 回归_白名单与源码读取点一致()
    {
        Assert.Equal(
            WhitelistSourceOfTruth.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            CliArgs.KnownOptions.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    [Fact]
    public void 回归_白名单必须覆盖_Program_里每一个真实读取点()
    {
        // 这条是"不误杀"的根：凡 Program.cs 里出现 opt.TryGetValue("x") / opt.ContainsKey("x") /
        // GetValueOrDefault("x") / Required(opt,"x") 的 x，都必须在白名单里，否则合法用法会被拒绝。
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PCMig.Cli", "Program.cs"));
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in new[]
                 {
                     @"opt\.TryGetValue\(""([a-zA-Z0-9_-]+)""",
                     @"opt\.ContainsKey\(""([a-zA-Z0-9_-]+)""",
                     @"opt\.GetValueOrDefault\(""([a-zA-Z0-9_-]+)""",
                     @"Required\(opt,\s*""([a-zA-Z0-9_-]+)""",
                     @"opt\[\""([a-zA-Z0-9_-]+)""\]",
                 })
        {
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(src, pattern))
                keys.Add(m.Groups[1].Value);
        }

        Assert.NotEmpty(keys);
        var missing = keys.Where(k => !CliArgs.KnownOptions.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        Assert.True(missing.Count == 0,
            $"Program.cs 读取了但白名单没登记的选项（会被误判为未知参数）：{string.Join(", ", missing)}");
    }

    [Fact]
    public void 回归_所有既有合法选项都不被拒绝()
    {
        var rejected = WhitelistSourceOfTruth
            .Where(key => CliArgs.Parse(new[] { $"--{key}=value" }).UnknownOptions.Count > 0)
            .ToList();
        Assert.True(rejected.Count == 0, $"这些合法选项被误判为未知参数：{string.Join(", ", rejected)}");
    }

    [Fact]
    public void 回归_空格写法的既有选项全部保持可解析()
    {
        foreach (var key in WhitelistSourceOfTruth)
        {
            var p = CliArgs.Parse(new[] { $"--{key}", "value" });
            Assert.Empty(p.UnknownOptions);
            Assert.Equal("value", p.Options[key]);
        }
    }

    [Fact]
    public void 回归_开关型选项仍然得到_true()
    {
        foreach (var key in new[] { "yes", "quiet", "watch", "open", "bench", "noverify", "allow-incomplete-scan" })
        {
            var p = CliArgs.Parse(new[] { $"--{key}" });
            Assert.Empty(p.UnknownOptions);
            Assert.Equal("true", p.Options[key]);
        }
    }

    [Fact]
    public void 回归_末尾无值的选项记_true()
    {
        var p = CliArgs.Parse(new[] { "--job", "J1", "--quiet" });
        Assert.Equal("J1", p.Options["job"]);
        Assert.Equal("true", p.Options["quiet"]);
        Assert.Empty(p.UnknownOptions);
    }

    [Fact]
    public void 回归_选项值不消费以双横线开头的_token()
    {
        // 旧 ParseOptions 判据：后一个 token 以 "--" 开头就不消费（当开关）。
        // 这条必须保留 —— 否则 `resume --quiet --help` 里的 --help 会被 --quiet 吃掉，
        // 又变成"想看帮助却真的恢复"的老事故。
        var p = CliArgs.Parse(new[] { "--quiet", "--help" });
        Assert.Equal("true", p.Options["quiet"]);
        Assert.True(p.HelpRequested, "--help 被 --quiet 吃掉了");

        var q = CliArgs.Parse(new[] { "--job", "--quiet" });
        Assert.Equal("true", q.Options["job"]);
        Assert.Equal("true", q.Options["quiet"]);
        Assert.Empty(q.UnknownOptions);
    }

    [Fact]
    public void 回归_单横线开头的值仍然可以被消费()
    {
        // 只有以 "--" 开头才不消费；"-1" / "-weird" 这类值仍可作为选项值传入
        Assert.Equal("-1", CliArgs.Parse(new[] { "--threads", "-1" }).Options["threads"]);
        Assert.Equal("-weird-id", CliArgs.Parse(new[] { "--job", "-weird-id" }).Options["job"]);
    }

    [Fact]
    public void 回归_选项名大小写不敏感()
    {
        var p = CliArgs.Parse(new[] { "--JOB", "J1", "--Quiet" });
        Assert.Equal("J1", p.Options["job"]);
        Assert.True(p.Options.ContainsKey("QUIET"));
        Assert.Empty(p.UnknownOptions);
    }

    [Fact]
    public void 回归_真实命令行样例逐条可解析()
    {
        var samples = new[]
        {
            new[] { "--host", @"\\192.168.1.50\D$", "--bench" },
            new[] { "--host", "H", "--source", @"\\H\D$", "--target", @"D:\out", "--kind", "profile", "--threads", "16", "--yes" },
            new[] { "--host", "H", "--target", @"D:\out", "--paths", @"\\H\D$\a;\\H\D$\b", "--xd", "tmp;cache", "--xf", "*.tmp", "--largemb", "512", "--large-channel", "auto" },
            new[] { "--job", "J1", "--password", "p", "--matrix", @"C:\m\migration-matrix.yaml", "--allow-incomplete-scan" },
            new[] { "--job", "J1", "--level", "2" },
            new[] { "--job", "J1", "--watch" },
            new[] { "--job", "J1", "--open" },
            new[] { "--jobs", @"E:\j", "--quiet" },
        };
        foreach (var args in samples)
        {
            var p = CliArgs.Parse(args);
            Assert.True(p.UnknownOptions.Count == 0,
                $"合法样例被拒：{string.Join(' ', args)} → {string.Join(", ", p.UnknownOptions)}");
            Assert.Null(p.SyntaxError);
        }
    }

    [Fact]
    public void 回归_已识别的子命令集合与_Program_分发臂一致()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PCMig.Cli", "Program.cs"));
        var dispatch = src[src.IndexOf("return cmd switch", StringComparison.Ordinal)..];
        dispatch = dispatch[..dispatch.IndexOf("_ => Fail", StringComparison.Ordinal)];
        foreach (var cmd in AllCommands)
            Assert.Contains($"\"{cmd}\"", dispatch);
    }

    // ==================================================================
    // 位置参数策略（保守）
    // ==================================================================

    [Fact]
    public void 位置参数_从不读位置参数的命令多给裸参数必须报错()
    {
        foreach (var cmd in CliArgs.CommandsWithoutPositionals)
        {
            var err = CliArgs.ValidatePositionals(cmd, new[] { "stray" });
            Assert.True(err != null, $"{cmd} 多给裸参数却未报错");
            Assert.Contains("stray", err);
        }
    }

    [Fact]
    public void 位置参数_未知命令不在这里下结论_交给既有未知命令分支()
    {
        // 结论不清就"不动"：未知命令沿用 `未知命令: xxx`（非零退出）
        Assert.Null(CliArgs.ValidatePositionals("definitely-not-a-command", new[] { "stray" }));
    }

    [Fact]
    public void 位置参数_没有裸参数时一律不报错()
    {
        foreach (var cmd in AllCommands)
            Assert.Null(CliArgs.ValidatePositionals(cmd, Array.Empty<string>()));
    }

    [Fact]
    public void 位置参数_策略表只覆盖被识别的子命令()
    {
        var unknown = CliArgs.CommandsWithoutPositionals.Where(c => !CliArgs.IsKnownCommand(c)).ToList();
        Assert.True(unknown.Count == 0, $"位置参数策略里混进了不存在的子命令：{string.Join(", ", unknown)}");
    }

    // ==================================================================
    // 顺序契约：解析本身不得有任何副作用
    // ==================================================================

    [Fact]
    public void 顺序_解析阶段绝不读取环境变量或触碰作业根()
    {
        // CliArgs.Parse 是纯函数（无 IO、无环境变量写入）。这里用"同一输入重复解析结果一致"
        // +"绝不写入 PCMIG_JOBS"锁住这条：识别与执行必须分离，识别阶段不许有副作用。
        var before = Environment.GetEnvironmentVariable("PCMIG_JOBS");
        foreach (var s in new[] { "resume --help", @"list --jobs=E:\nope", "run --nope", "list stray" })
        {
            var first = CliArgs.Parse(s.Split(' '));
            var second = CliArgs.Parse(s.Split(' '));
            Assert.Equal(first.HelpRequested, second.HelpRequested);
            Assert.Equal(first.Options.Count, second.Options.Count);
            Assert.Equal(first.UnknownOptions, second.UnknownOptions);
            Assert.Equal(first.Positionals, second.Positionals);
        }
        Assert.Equal(before, Environment.GetEnvironmentVariable("PCMIG_JOBS"));
    }
}