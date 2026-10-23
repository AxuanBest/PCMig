using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace PCMig.Core.Logging;

/// <summary>
/// PCMig 日志体系（设计之初的第一公民）：
///  - 应用级日志：%ProgramData%\PCMig\Logs\app-*.log（文本）+ app-*.jsonl（结构化）
///  - 任务级日志：&lt;JobDir&gt;\logs\job-*.log + job-*.jsonl，随 Job 一并归档/排障
///  - GUI 可通过 MemorySink 订阅实时日志流
/// 所有时间戳使用本地时区便于对照现场，滚动周期为天，默认保留 31 份。
/// </summary>
public static class LogBootstrap
{
    public const string AppName = "PCMig";

    /// <summary>
    /// 应用级日志目录：默认 <c>%ProgramData%\PCMig\Logs</c>。
    ///
    /// ★ P1-10 ★ 环境变量 <c>PCMIG_LOG_DIR</c> 可覆盖它，**仅供测量/隔离复验**：
    /// 不设置时生产路径逐位不变（仍是 ProgramData）；设置后应用级日志全部落到指定目录，
    /// 这样"启动计时/安装计时"这类复验不会往真实用户的日志目录里写东西。
    ///
    /// ★ 2026-10-15（Preview.2 隔离整改）★ 预览产物的应用日志根改由随产物分发的
    /// <c>preview-isolation.json</c> 指定（见 <see cref="PCMig.Core.Util.PreviewIsolation"/>），
    /// 优先级**高于**环境变量 —— 否则账户级环境变量又能改写预览版行为，等于没隔离。
    /// 正式产物下这一支完全不参与，语义与改动前逐位一致。
    /// </summary>
    public static string AppLogDir
    {
        get
        {
            // ① 预览隔离：有效则用隔离根；★ 预览身份但隔离失败则**抛异常**，绝不回退到正式根 ★
            //    第二道防线：即使某个入口漏了启动检查，应用日志也不可能落进 %ProgramData%\PCMig\Logs。
            if (PCMig.Core.Util.PreviewIsolation
                    .TryResolveForPersistence("Logs", "LogBootstrap.AppLogDir") is { } isolated)
            {
                return isolated;
            }

            // ② 显式环境变量覆盖（测量场景，语义保持不变）
            // ③ 生产默认：%ProgramData%\PCMig\Logs —— 只有**正式身份**才会走到这里
            var overridden = Environment.GetEnvironmentVariable("PCMIG_LOG_DIR");
            return string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppName, "Logs")
                : overridden!;
        }
    }

    public static string JobLogDir(string jobDir) => Path.Combine(jobDir, "logs");

    /// <summary>
    /// ★ P1-11 ★ 单个日志文件的体积上限（8 MB）。配合 <c>rollOnFileSizeLimit: true</c> 使用：
    /// 达到上限就滚动出新文件（<c>app-YYYYMMDD_001.log</c>），而不是继续把同一个文件写大。
    ///
    /// 为什么必须有：Serilog File sink 的默认上限是 **1 GB**，且默认 <c>rollOnFileSizeLimit = false</c> ——
    /// 达到上限后**静默停止写入**。现场实测单任务 <c>job-*.log</c> 23 MB、<c>job-*.jsonl</c> 42 MB
    /// 都是**单个文件**（逐文件 robocopy 输出被逐行复制进日志），排障时打开/检索都慢，
    /// 日志目录也会被日志自身撑爆。上限 + 滚动 + 保留份数三者合起来给出硬边界：
    /// 应用级 31 份、任务级 10 份 ⇒ 单目录最多约 31×8 MB / 10×8 MB。
    /// （逐行采样另见 <see cref="RobocopyOutputLogPolicy"/> —— 减少写入量，与本上限互补。）
    /// </summary>
    public const long LogFileSizeLimitBytes = 8L * 1024 * 1024;

    /// <summary>创建应用级根 Logger（控制台开关由调用方决定）。</summary>
    public static Serilog.ILogger CreateAppLogger(bool console, MemorySink? memorySink = null, LogEventLevel level = LogEventLevel.Debug)
    {
        Directory.CreateDirectory(AppLogDir);
        var cfg = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.WithProperty("App", AppName)
            .Enrich.WithMachineName()
            .Enrich.WithProcessId()
            .WriteTo.Async(a => a.File(
                Path.Combine(AppLogDir, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 31,
                fileSizeLimitBytes: LogFileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                shared: true, // 多实例/前进程残留句柄时不至于整会话静默丢日志
                encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true), // 带 BOM，记事本/PS5.1 读中文不乱码
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{MachineName}/{ProcessId}] {Message:lj}{NewLine}{Exception}"))
            .WriteTo.Async(a => a.File(
                new CompactJsonFormatter(),
                Path.Combine(AppLogDir, "app-.jsonl"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 31,
                fileSizeLimitBytes: LogFileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                shared: true));

        if (console)
            cfg = cfg.WriteTo.Console(outputTemplate: "[{Level:u3}] {Message:lj}{NewLine}{Exception}");
        if (memorySink != null)
            cfg = cfg.WriteTo.Sink(memorySink, LogEventLevel.Information);

        return cfg.CreateLogger();
    }

    /// <summary>创建任务级 Logger：落到 Job 目录，随 Job 归档。可选同时推送到 GUI 内存 Sink。</summary>
    public static Serilog.ILogger CreateJobLogger(string jobDir, string jobId, MemorySink? memorySink = null)
    {
        var dir = JobLogDir(jobDir);
        Directory.CreateDirectory(dir);
        var cfg = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.WithProperty("App", AppName)
            .Enrich.WithProperty("JobId", jobId)
            .WriteTo.Async(a => a.File(
                Path.Combine(dir, "job-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 10,
                fileSizeLimitBytes: LogFileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                shared: true,
                encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}"))
            .WriteTo.Async(a => a.File(
                new CompactJsonFormatter(),
                Path.Combine(dir, "job-.jsonl"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 10,
                fileSizeLimitBytes: LogFileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                shared: true));
        if (memorySink != null) cfg = cfg.WriteTo.Sink(memorySink, LogEventLevel.Information);
        return cfg.CreateLogger();
    }
}

/// <summary>内存 Sink：供 GUI 实时显示日志。环形缓冲，线程安全。</summary>
public sealed class MemorySink : ILogEventSink
{
    private readonly int _capacity;
    private readonly Queue<string> _buffer;
    private readonly object _gate = new();

    public event Action<string>? LineEmitted;

    public MemorySink(int capacity = 500) { _capacity = capacity; _buffer = new Queue<string>(capacity); }

    public void Emit(LogEvent logEvent)
    {
        var line = $"{logEvent.Timestamp:HH:mm:ss} [{logEvent.Level}] {logEvent.RenderMessage()}";
        lock (_gate)
        {
            _buffer.Enqueue(line);
            while (_buffer.Count > _capacity) _buffer.Dequeue();
        }
        LineEmitted?.Invoke(line);
    }

    public string[] Snapshot() { lock (_gate) return _buffer.ToArray(); }
}
