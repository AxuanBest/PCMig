using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using PCMig.Core.Diagnostics;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ P1-10 ★ 启动分段证据通道的行为测试。
///
/// 交接口径是"安装器双击→欢迎页与首启慢（**无分段证据**）"——本文件要保证的正是"有证据、且证据可信"：
///   · 顺序与单调：分段必须按发生顺序记录，时间不得倒退（否则读表会得出荒谬结论）；
///   · 基线正确：距"进程创建"的毫秒数必须真的从进程创建算起（这是"双击→这一段"的唯一可信口径）；
///   · 落盘即用：每条都要能被 jsonl 解析、中文不得被转义成 \uXXXX（这份文件是给人 grep 的）；
///   · 写入失败降级：磁盘不可写时**只丢证据、不抛异常**（证据通道绝不许把应用启动搞崩）；
///   · 并发安全：多线程同时记不得撕裂行；
///   · 上限轮转：文件超限轮转，且新证据照旧落盘；
///   · 开关：关掉后零写入。
/// </summary>
[Collection("startup-timeline")]
public sealed class P1StartupTimelineTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousRoot;

    public P1StartupTimelineTests()
    {
        StartupTimeline.ResetForTests();
        _root = Path.Combine(Path.GetTempPath(), "pcmig-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _previousRoot = null;
        StartupTimeline.RootDirectory = _root;
    }

    public void Dispose()
    {
        StartupTimeline.ResetForTests();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        _ = _previousRoot;
    }

    private string FilePath => Path.Combine(_root, "startup-timeline.jsonl");

    private static string[] Lines(string path)
        => File.Exists(path) ? File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToArray() : Array.Empty<string>();

    // ── ① 顺序与单调 ────────────────────────────────────────────────────────────
    [Fact]
    public void T1_分段按发生顺序记录且时间不倒退()
    {
        StartupTimeline.Begin("unit-test");
        foreach (var phase in new[] { "app-ctor", "onlaunched", "diagnostics-started", "window-created", "window-activated" })
        {
            StartupTimeline.Mark(phase);
        }

        var phases = StartupTimeline.Phases;
        Assert.Equal(
            new[] { "begin", "app-ctor", "onlaunched", "diagnostics-started", "window-created", "window-activated" },
            phases.Select(p => p.Phase).ToArray());

        for (var i = 1; i < phases.Count; i++)
        {
            Assert.True(phases[i].MsSinceProcessStart >= phases[i - 1].MsSinceProcessStart,
                $"第 {i} 段（{phases[i].Phase}）的距进程创建时间比上一段更早：{phases[i].MsSinceProcessStart} < {phases[i - 1].MsSinceProcessStart}");
            Assert.True(phases[i].MsSinceBegin >= phases[i - 1].MsSinceBegin,
                $"第 {i} 段（{phases[i].Phase}）的进程内时间倒退了");
            Assert.False(string.IsNullOrWhiteSpace(phases[i].AtUtc));
        }
    }

    // ── ② 基线真的是"进程创建" ─────────────────────────────────────────────────
    [Fact]
    public void T2_距进程创建的时间不会小于进程内相对时间()
    {
        StartupTimeline.Begin("unit-test");
        var begin = StartupTimeline.Phases.Single(p => p.Phase == "begin");

        // 进程创建早于 Begin() ⇒ 距进程创建必然 ≥ 距 Begin。
        Assert.True(begin.MsSinceProcessStart >= begin.MsSinceBegin,
            $"基线不对：距进程创建 {begin.MsSinceProcessStart} ms < 距 Begin {begin.MsSinceBegin} ms");
    }

    // ── ③ 落盘可解析、中文不转义 ───────────────────────────────────────────────
    [Fact]
    public void T3_每条分段都能被解析且中文不转义()
    {
        StartupTimeline.Begin("单元测试");
        StartupTimeline.Mark("shell-loaded", "根内容 Loaded（首帧已提交）");

        var lines = Lines(FilePath);
        Assert.Equal(2, lines.Length);

        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("Phase").GetString()));
            Assert.True(root.GetProperty("MsSinceProcessStart").GetDouble() >= 0);
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("AtUtc").GetString()));
        }

        var text = string.Join("\n", lines);
        Assert.Contains("根内容", text);
        Assert.DoesNotContain("\\u", text);   // ★ 给人 grep 的文件不允许被转义成 \uXXXX
    }

    // ── ④ 写不成也只降级 ───────────────────────────────────────────────────────
    [Fact]
    public void T4_目标不可写时只丢证据不抛异常()
    {
        StartupTimeline.RootDirectory = Path.Combine("Z:", "不存在的盘", "启动证据");
        var before = StartupTimeline.Phases.Count;

        var ex = Record.Exception(() =>
        {
            StartupTimeline.Begin("unit-test");
            StartupTimeline.Mark("app-ctor");
            StartupTimeline.Mark("interactive");
            StartupTimeline.Complete("interactive");
        });

        Assert.Null(ex);
        Assert.True(StartupTimeline.Phases.Count >= before + 3, "内存里的分段也必须照记（写盘失败只影响文件）");
    }

    // ── ⑤ 汇总行 ───────────────────────────────────────────────────────────────
    [Fact]
    public void T5_汇总行含全部阶段与首个可交互耗时()
    {
        StartupTimeline.Begin("unit-test");
        StartupTimeline.Mark("shell-loaded");
        StartupTimeline.Mark("interactive", "首个可交互调度轮");
        StartupTimeline.Complete("interactive");

        var last = Lines(FilePath)[^1];
        using var doc = JsonDocument.Parse(last);
        var root = doc.RootElement;

        Assert.Equal("phase-summary", root.GetProperty("Phase").GetString());
        Assert.Equal(StartupTimeline.Phases.Count, root.GetProperty("Count").GetInt32());

        var interactive = StartupTimeline.ElapsedMs("interactive");
        Assert.NotNull(interactive);
        Assert.Equal(interactive!.Value, root.GetProperty("FirstInteractiveMs").GetDouble(), 3);
        Assert.Equal(root.GetProperty("TotalMs").GetDouble(), StartupTimeline.Phases[^1].MsSinceProcessStart, 3);

        var stages = root.GetProperty("Stages").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Contains(stages, s => s.StartsWith("interactive@", StringComparison.Ordinal));
        Assert.Equal(StartupTimeline.Phases.Count, stages.Length);
    }

    // ── ⑥ 并发不撕裂 ───────────────────────────────────────────────────────────
    [Fact]
    public void T6_多线程同时记录不丢行也不撕裂()
    {
        StartupTimeline.Begin("unit-test");

        Parallel.For(0, 8, _ =>
        {
            for (var i = 0; i < 50; i++) StartupTimeline.Mark("stress", "并发写入");
        });

        var lines = Lines(FilePath);
        Assert.Equal(1 + 400, lines.Length);
        Assert.All(lines, line =>
        {
            using var doc = JsonDocument.Parse(line);
            Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("Phase").GetString()));
        });
        Assert.Equal(1 + 400, StartupTimeline.Phases.Count);
    }

    // ── ⑦ 超限轮转 ─────────────────────────────────────────────────────────────
    [Fact]
    public void T7_超过上限自动轮转且新证据照旧落盘()
    {
        var filler = new string('x', StartupTimeline.MaxBytes + 16);
        File.WriteAllText(FilePath, filler);

        StartupTimeline.Begin("unit-test");
        StartupTimeline.Mark("app-ctor");

        var rolled = FilePath + ".1";
        Assert.True(File.Exists(rolled), "超限的旧文件必须被轮转为 .1");
        Assert.Equal(filler.Length, new FileInfo(rolled).Length);

        var lines = Lines(FilePath);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"app-ctor\"", string.Join("\n", lines));
    }

    // ── ⑧ 开关 ─────────────────────────────────────────────────────────────────
    [Fact]
    public void T8_关掉开关后零写入()
    {
        StartupTimeline.Enabled = false;
        StartupTimeline.Begin("unit-test");
        StartupTimeline.Mark("app-ctor");
        StartupTimeline.Complete();

        Assert.False(File.Exists(FilePath));
        Assert.Empty(StartupTimeline.Phases);
    }

    // ── ⑨ Begin 幂等 ───────────────────────────────────────────────────────────
    [Fact]
    public void T9_重复Begin不重置基线()
    {
        StartupTimeline.Begin("first");
        var beginCount = StartupTimeline.Phases.Count(p => p.Phase == "begin");
        StartupTimeline.Begin("second");

        Assert.Equal(1, beginCount);
        Assert.Equal(1, StartupTimeline.Phases.Count(p => p.Phase == "begin"));
        Assert.True(StartupTimeline.Begun);
    }

    // ── ⑩ 接线契约：启动路径真的挂了标记 ───────────────────────────────────────
    [Fact]
    public void T10_应用与壳的启动路径都必须挂上标记()
    {
        var app = ReadRepoFile("src", "PCMig.WinUI", "App.xaml.cs");
        Assert.Contains("StartupTimeline.Begin(\"PCMig.WinUI\")", app);
        foreach (var phase in new[] { "app-ctor", "onlaunched", "diagnostics-started", "window-created", "window-activated" })
        {
            Assert.Contains($"StartupTimeline.Mark(\"{phase}\"", app);
        }

        var shell = ReadRepoFile("src", "PCMig.WinUI", "MainWindow.xaml.cs");
        Assert.Contains("StartupTimeline.Mark(\"shell-xaml-loaded\")", shell);
        Assert.Contains("InstallStartupTimelineHook();", shell);
        Assert.Contains("StartupTimeline.Mark(\"mainwindow-ctor\")", shell);
        Assert.Contains("StartupTimeline.Mark(\"shell-loaded\"", shell);
        Assert.Contains("StartupTimeline.Mark(\"interactive\"", shell);
        Assert.Contains("StartupTimeline.Complete(", shell);
    }

    // ── 辅助 ────────────────────────────────────────────────────────────────────
    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln")))
            {
                var path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
                Assert.True(File.Exists(path), "缺少文件：" + path);
                return File.ReadAllText(path);
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到仓库根");
    }
}