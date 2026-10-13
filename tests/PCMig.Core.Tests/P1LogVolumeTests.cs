using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PCMig.Core.Logging;
using Serilog;
using Serilog.Formatting.Compact;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ P1-11 ★ 日志过大不得拖累界面/排障：两条独立边界必须同时成立 ——
///   ① **写入量**有界：逐行复制的 robocopy 输出按采样写日志（错误行永不省略，省略量如实计数）；
///   ② **文件体积**有界：Serilog File sink 设 8 MB 上限 + 达到上限就滚动（不再出现几十 MB 的单文件）。
///
/// 现场基线（私有 raw-logzip，JOB-20261010-152632-be58）：job-*.log 23,357,132 B / 117,310 行，
/// job-*.jsonl 42,601,763 B，几乎全是 robocopy 逐文件输出行的副本。
/// 本文件用真实 Serilog logger 复算"同一批行经过采样后的落盘体积"，把"瘦身"变成可核对的事实。
/// </summary>
public sealed class P1LogVolumeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmig-p1log-" + Guid.NewGuid().ToString("N"));

    public P1LogVolumeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* 清理失败不影响结论 */ }
    }

    // ────────────────────────── ① 写入量：采样语义 ──────────────────────────

    [Fact]
    public void 前两百行全记_之后每五百行记一行()
    {
        var p = new RobocopyOutputLogPolicy();
        var taken = new List<int>();
        for (var i = 1; i <= 3000; i++)
        {
            if (p.ShouldLog(isError: false)) taken.Add(i);
        }

        // 头部 200 行一行不少
        Assert.Equal(Enumerable.Range(1, RobocopyOutputLogPolicy.HeadLines), taken.Take(RobocopyOutputLogPolicy.HeadLines));
        // 头部之后只剩步长命中的行（500、1000、1500…）
        var afterHead = taken.Skip(RobocopyOutputLogPolicy.HeadLines).ToList();
        Assert.NotEmpty(afterHead);
        Assert.All(afterHead, n => Assert.True(n > RobocopyOutputLogPolicy.HeadLines,
            $"头部之后的命中行必须是步长行，实际 {n}"));
        Assert.Equal(3000 / RobocopyOutputLogPolicy.Stride, afterHead.Count);
        Assert.True(p.Observed == 3000, "Observed 必须是真实行数");
        Assert.Equal(p.Observed, p.Logged + p.Dropped);
    }

    [Fact]
    public void 错误行永不因采样被省略()
    {
        var p = new RobocopyOutputLogPolicy();
        var errorAt = new List<int>();
        for (var i = 1; i <= 1200; i++)
        {
            // 头部之外、步长之外的行全部当错误行：一个都不许丢
            var isError = i > RobocopyOutputLogPolicy.HeadLines && i % RobocopyOutputLogPolicy.Stride != 0;
            if (isError) errorAt.Add(i);
            Assert.True(p.ShouldLog(isError), $"第 {i} 行是错误行，必须写进日志");
        }

        Assert.NotEmpty(errorAt);
        Assert.Equal(1200, p.Logged);
        Assert.Equal(0, p.Dropped);
        Assert.False(p.HasSkipped);
    }

    [Fact]
    public void 硬上限撞上时连错误行也不再写_但省略量如实计数()
    {
        var p = new RobocopyOutputLogPolicy();
        for (var i = 1; i <= 100_000; i++) p.ShouldLog(isError: true);

        Assert.Equal(100_000, p.Observed);
        Assert.Equal(RobocopyOutputLogPolicy.MaxLoggedLines, p.Logged);
        Assert.Equal(100_000 - RobocopyOutputLogPolicy.MaxLoggedLines, p.Dropped);
        Assert.True(p.HasSkipped);
        Assert.Equal(p.Observed, p.Logged + p.Dropped);
    }

    [Fact]
    public void 现场规模下的写入量必须收敛到几百行()
    {
        // 现场那个任务被写进日志的 robocopy 输出行 = 117,310 行（全部是普通文件行）
        var p = new RobocopyOutputLogPolicy();
        for (var i = 0; i < 117_310; i++) p.ShouldLog(isError: false);

        Assert.Equal(117_310, p.Observed);
        Assert.True(p.Logged >= RobocopyOutputLogPolicy.HeadLines, "头部现场必须保留");
        Assert.True(p.Logged < 600, $"采样后写入量应远小于现场 117,310 行，实际 {p.Logged}");
        Assert.Equal(117_310 - p.Logged, p.Dropped);
    }

    [Fact]
    public void 摘要必须自洽且指向逐行全文()
    {
        var p = new RobocopyOutputLogPolicy();
        for (var i = 0; i < 5000; i++) p.ShouldLog(isError: false);
        Assert.True(p.HasSkipped);

        var text = p.Describe(@"D:\Job\logs\robocopy\object-000001.log");
        Assert.Contains("5000", text);
        Assert.Contains(RobocopyOutputLogPolicy.HeadLines.ToString(), text);
        Assert.Contains(RobocopyOutputLogPolicy.Stride.ToString(), text);
        Assert.Contains(p.Logged.ToString(), text);
        Assert.Contains(p.Dropped.ToString(), text);
        Assert.Contains("错误行永不", text);
        Assert.Contains(@"object-000001.log", text);
    }

    [Fact]
    public void 未省略时不写摘要()
    {
        var p = new RobocopyOutputLogPolicy();
        for (var i = 0; i < 50; i++) p.ShouldLog(isError: false);
        Assert.False(p.HasSkipped);
    }

    // ────────────────────────── ② 落盘体积：真实 Serilog 复算 ──────────────────────────

    [Fact]
    public void 同一批现场行经过采样后落盘体积从数十兆降到一兆以内()
    {
        var dir = Path.Combine(_root, "sampled");
        Directory.CreateDirectory(dir);
        var p = new RobocopyOutputLogPolicy();

        using (var log = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(Path.Combine(dir, "job-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 10,
                fileSizeLimitBytes: LogBootstrap.LogFileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger())
        {
            // 每行的形态照着现场取：robocopy /MT 文件行 + 一条 UNC 长路径（≈200 字符）
            var line = "	    新文件    		  5,742	" +
                       @"\\10.0.15.28\Users\d970\AppData\Local\Microsoft\Edge\User Data\Default\Service Worker\CacheStorage\abc\def\ghi\file.bin";
            for (var i = 0; i < 117_310; i++)
            {
                if (p.ShouldLog(isError: false)) log.Debug("robocopy| {Line}", line);
            }
        }

        var files = Directory.GetFiles(dir);
        var total = files.Sum(f => new FileInfo(f).Length);
        Assert.Single(files);                                  // 采样后连一次滚动都不需要
        Assert.True(total < 1024 * 1024,
            $"采样后单任务日志应在一兆以内（现场是 23 MB / 42 MB），实际 {total} 字节");
        Assert.Equal(117_310, p.Observed);
    }

    [Fact]
    public void 单文件达到上限就滚动_且保留份数给出硬边界()
    {
        var dir = Path.Combine(_root, "rolling");
        Directory.CreateDirectory(dir);
        const long limit = 64 * 1024;

        using (var log = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(Path.Combine(dir, "job-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 4,
                fileSizeLimitBytes: limit,
                rollOnFileSizeLimit: true,
                encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}")
            .CreateLogger())
        {
            var filler = new string('x', 200);
            for (var i = 0; i < 20_000; i++) log.Debug("roll| {Line}", filler + i);
        }

        var files = Directory.GetFiles(dir).Select(f => new FileInfo(f)).ToList();
        Assert.True(files.Count >= 2, $"达到上限必须滚动出新文件，实际只有 {files.Count} 个");
        Assert.Contains(files, f => Regex.IsMatch(f.Name, @"_\d{3}\.log$"));   // 滚动产物形如 job-20261011_001.log
        Assert.All(files, f => Assert.True(f.Length <= limit + limit / 2,
            $"单文件不得超过上限的 1.5 倍，实际 {f.Name} = {f.Length} 字节"));
        Assert.True(files.Sum(f => f.Length) <= 4 * limit + limit,
            $"保留 4 份 ⇒ 总量必须被封顶，实际 {files.Sum(f => f.Length)} 字节");
    }

    // ────────────────────────── ③ 配置与接线契约（源码文本） ──────────────────────────

    [Fact]
    public void 四个文件汇都必须设上限并开启滚动()
    {
        var src = ReadRepoFile("src", "PCMig.Core", "Logging", "LogBootstrap.cs");

        Assert.Equal(8L * 1024 * 1024, LogBootstrap.LogFileSizeLimitBytes);
        Assert.True(LogBootstrap.LogFileSizeLimitBytes < 1024L * 1024 * 1024,
            "必须明显小于 Serilog 默认的 1 GB 上限（否则默认值下就只是'静默停写'）");
        Assert.Equal(4, Regex.Matches(src, @"fileSizeLimitBytes: LogFileSizeLimitBytes,").Count);
        Assert.Equal(4, Regex.Matches(src, @"rollOnFileSizeLimit: true,").Count);
        Assert.DoesNotContain("rollOnFileSizeLimit: false", src);
        // 不设 rollOnFileSizeLimit 时 Serilog 达到上限会**静默停写**：这条注释必须留在源码里防止被改回去
        Assert.Contains("静默停止写入", src);
    }

    [Fact]
    public void 逐行外部事件一行都不能少_只有写日志被采样()
    {
        var src = ReadRepoFile("src", "PCMig.Core", "Transfer", "RobocopyRunner.cs");

        // ① UI 的实时行流（OutputLine）必须仍然逐行触发，且早于写日志
        Assert.Contains("try { OutputLine?.Invoke(trimmed); }", src);

        // ② "robocopy| {Line}" 只允许出现一次，且必须被采样策略守住
        var matches = Regex.Matches(src, @"_log\.Debug\(""robocopy\| \{Line\}"", trimmed\);");
        Assert.Single(matches);
        var idx = matches[0].Index;
        var before = src.Substring(Math.Max(0, idx - 400), Math.Min(400, idx));
        Assert.Contains("outputLog.ShouldLog(isErrorLine)", before);

        // ③ 错误行判据仍是同一处（ERROR/错误），采样不得另造一套判据
        Assert.Contains("var isErrorLine = trimmed.Contains(\"ERROR\", StringComparison.OrdinalIgnoreCase)", src);
        Assert.Contains("trimmed.Contains(\"错误\", StringComparison.Ordinal)", src);

        // ④ 省略必须留摘要（真实计数 + 采样口径 + 逐行全文位置），且只在真的省略时写
        Assert.Contains("if (outputLog.HasSkipped) _log.Information(\"{Summary}\", outputLog.Describe(unicodeLogPath));", src);
        Assert.Contains("var outputLog = new Logging.RobocopyOutputLogPolicy();", src);
    }

    private static string ReadRepoFile(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray()));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
        }
        throw new InvalidOperationException("找不到含 PCMig.sln 的仓库根：" + AppContext.BaseDirectory);
    }
}