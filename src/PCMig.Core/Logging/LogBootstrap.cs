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

    public static string AppLogDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppName, "Logs");

    public static string JobLogDir(string jobDir) => Path.Combine(jobDir, "logs");

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
                shared: true, // 多实例/前进程残留句柄时不至于整会话静默丢日志
                encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true), // 带 BOM，记事本/PS5.1 读中文不乱码
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{MachineName}/{ProcessId}] {Message:lj}{NewLine}{Exception}"))
            .WriteTo.Async(a => a.File(
                new CompactJsonFormatter(),
                Path.Combine(AppLogDir, "app-.jsonl"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 31,
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
                shared: true,
                encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}"))
            .WriteTo.Async(a => a.File(
                new CompactJsonFormatter(),
                Path.Combine(dir, "job-.jsonl"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 10,
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
