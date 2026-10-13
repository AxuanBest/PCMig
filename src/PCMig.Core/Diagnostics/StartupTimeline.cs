using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PCMig.Core.Diagnostics;

/// <summary>
/// 一个启动阶段（时间戳事实，不是判定）。
/// </summary>
/// <param name="Phase">阶段名（稳定标识，供脚本按名取值）。</param>
/// <param name="Detail">人类可读补充（可为 null）。</param>
/// <param name="MsSinceProcessStart">距**进程创建**的毫秒数 —— 这就是"双击→这一段"的真实耗时。</param>
/// <param name="MsSinceBegin">距 <see cref="StartupTimeline.Begin"/> 的毫秒数（进程内相对量，便于对比）。</param>
/// <param name="AtUtc">该阶段的 UTC 时间（ISO-8601 "O"）。</param>
public readonly record struct StartupPhase(
    string Phase,
    string? Detail,
    double MsSinceProcessStart,
    double MsSinceBegin,
    string AtUtc);

/// <summary>
/// 末行汇总（一行拿到全部阶段，便于脚本一次读取）。
/// </summary>
public readonly record struct StartupSummary(
    string Phase,
    int Count,
    double TotalMs,
    double? FirstInteractiveMs,
    string[] Stages,
    string AtUtc);

/// <summary>
/// ★ P1-10 ★ 启动分段证据通道。
///
/// 要解决的问题（用户口径）："安装器双击→欢迎页慢、首启慢"一直是**主观描述**，
/// 因为既没有进程创建时刻的基线，也没有分段（哪一段花了几毫秒）。本类把这条链路变成可核对的数字：
///
///   进程创建 → app-ctor → onlaunched → diagnostics-started → window-created
///            → window-activated → mainwindow-ctor → shell-xaml-loaded → shell-loaded → interactive
///
/// 三条纪律：
///   1) **只记事实**：不参与任何业务判定、不改启动路径行为、不吞异常（写失败一律降级）；
///   2) **同步落盘**：应用日志走异步队列，进程刚启动/刚退出时队列里的最后几条可能还没落地，
///      所以分段证据直接以 jsonl 追加写 + flush，供事后 grep；
///   3) **中文不转义**：这份文件要给人直接看（<c>UnsafeRelaxedJsonEscaping</c>），不是给机器读的 \uXXXX。
///
/// 与 <see cref="CrashEvidence"/> 同构（独立文件、独立上限、独立轮转），互不影响：
/// 崩溃证据回答"为什么挂了"，启动时间线回答"慢在哪一段"。
/// </summary>
public static class StartupTimeline
{
    /// <summary>文件上限：超过即轮转为 <c>.1</c>（保留上一份），与崩溃证据同一策略。</summary>
    public const int MaxBytes = 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly List<StartupPhase> Marks = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static Stopwatch? _clock;
    private static DateTime _processStartUtc;
    private static string? _root;

    /// <summary>总开关（默认开）。关掉后 <see cref="Mark"/> 是零成本空操作。</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// 写入目录，默认 <c>%ProgramData%\PCMig\Logs</c>（与应用日志同目录）。
    /// 可用环境变量 <c>PCMIG_STARTUP_DIR</c> 覆盖 —— 这是**测量用**旋钮（把冷/热启动跑在隔离目录里），
    /// 不是产品功能；未设置时生产路径逐位不变。
    /// </summary>
    public static string RootDirectory
    {
        get
        {
            if (_root is not null) return _root;
            try
            {
                var configured = Environment.GetEnvironmentVariable("PCMIG_STARTUP_DIR");
                if (!string.IsNullOrWhiteSpace(configured)) return configured!;
            }
            catch { }
            return Logging.LogBootstrap.AppLogDir;
        }
        set => _root = value;
    }

    public static string FilePath => Path.Combine(RootDirectory, "startup-timeline.jsonl");

    /// <summary>是否已经 <see cref="Begin"/> 过（幂等保护用）。</summary>
    public static bool Begun
    {
        get { lock (Gate) { return _clock is not null; } }
    }

    /// <summary>
    /// 开始计时（进程内只生效一次）。<paramref name="appTag"/> 作为 begin 阶段的 detail，便于区分 exe。
    /// </summary>
    public static void Begin(string appTag)
    {
        if (!Enabled) return;
        lock (Gate)
        {
            if (_clock is not null) return;
            _processStartUtc = ProcessStartUtc();
            _clock = Stopwatch.StartNew();
        }
        Mark("begin", appTag);
    }

    /// <summary>记一段。永不抛：写盘失败只是丢一条证据，绝不影响启动。</summary>
    public static void Mark(string phase, string? detail = null)
    {
        if (!Enabled) return;
        if (string.IsNullOrWhiteSpace(phase)) return;

        StartupPhase record;
        lock (Gate)
        {
            _clock ??= Stopwatch.StartNew();
            if (_processStartUtc == default) _processStartUtc = ProcessStartUtc();
            record = new StartupPhase(
                phase,
                detail,
                MsSinceProcessStart(_processStartUtc),
                Math.Round(_clock.Elapsed.TotalMilliseconds, 3),
                DateTime.UtcNow.ToString("O"));
            Marks.Add(record);
        }

        try { AppendLine(JsonSerializer.Serialize(record, JsonOptions)); } catch { }
    }

    /// <summary>
    /// 写一行汇总（含全部分段），让"读一份启动"变成一次读取。
    /// <paramref name="interactivePhase"/> 是"到哪一段算用户可以动手了"的阶段名（默认 interactive）。
    /// </summary>
    public static void Complete(string interactivePhase = "interactive")
    {
        if (!Enabled) return;

        string json;
        lock (Gate)
        {
            var snapshot = Marks.ToArray();
            var total = snapshot.Length == 0 ? 0d : snapshot[^1].MsSinceProcessStart;
            double? firstInteractive = null;
            foreach (var mark in snapshot)
            {
                if (string.Equals(mark.Phase, interactivePhase, StringComparison.Ordinal))
                {
                    firstInteractive = mark.MsSinceProcessStart;
                    break;
                }
            }

            var stages = snapshot
                .Select(m => m.Phase + "@" + m.MsSinceProcessStart.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();

            json = JsonSerializer.Serialize(
                new StartupSummary(
                    "phase-summary",
                    snapshot.Length,
                    Math.Round(total, 3),
                    firstInteractive,
                    stages,
                    DateTime.UtcNow.ToString("O")),
                JsonOptions);
        }

        try { AppendLine(json); } catch { }
    }

    /// <summary>本次运行已记录的分段（快照，线程安全）。</summary>
    public static IReadOnlyList<StartupPhase> Phases
    {
        get { lock (Gate) { return Marks.ToArray(); } }
    }

    /// <summary>某阶段的 <c>MsSinceProcessStart</c>；没有该阶段返回 null。</summary>
    public static double? ElapsedMs(string phase)
    {
        lock (Gate)
        {
            foreach (var mark in Marks)
            {
                if (string.Equals(mark.Phase, phase, StringComparison.Ordinal)) return mark.MsSinceProcessStart;
            }
            return null;
        }
    }

    /// <summary>★ 仅供测试 ★ 清空进程内状态（静态状态必须能被测试隔离）。</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Marks.Clear();
            _clock = null;
            _processStartUtc = default;
            _root = null;
            Enabled = true;
        }
    }

    private static void AppendLine(string json)
    {
        var root = RootDirectory;
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "startup-timeline.jsonl");
        lock (Gate)
        {
            RotateIfTooBig(path);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.WriteLine(json);
            writer.Flush();
            stream.Flush(true);
        }
    }

    private static void RotateIfTooBig(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxBytes) return;
            var rolled = path + ".1";
            if (File.Exists(rolled)) File.Delete(rolled);
            File.Move(path, rolled);
        }
        catch
        {
            // 轮转失败也必须继续写：宁可文件超一点，也不丢本次启动的证据。
        }
    }

    private static DateTime ProcessStartUtc()
    {
        try
        {
            return Process.GetCurrentProcess().StartTime.ToUniversalTime();
        }
        catch
        {
            // 拿不到进程创建时刻时退化为"现在"：分段仍然是单调的，只是总量偏小（如实退化，不编造）。
            return DateTime.UtcNow;
        }
    }

    private static double MsSinceProcessStart(DateTime processStartUtc)
    {
        var ms = (DateTime.UtcNow - processStartUtc).TotalMilliseconds;
        return Math.Round(ms < 0d ? 0d : ms, 3);
    }
}