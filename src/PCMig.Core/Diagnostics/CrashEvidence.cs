using System.Text;
using System.Text.Json;

namespace PCMig.Core.Diagnostics;

/// <summary>
/// ★ P0-06(4) ★ 崩溃 / 卡死证据的**独立落盘通道**。
///
/// 为什么要脱离日志体系单写一份：
///  1) 应用日志走 Serilog（`WriteTo.Async`，后台队列）。进程被**强杀 / WER 回收**时队列里
///     最后几条（恰恰是崩溃与卡死那几条）可能还没落盘 ⇒ 现场"点了停止就没了"的会话留不下证据；
///  2) 本通道每次写入都 `Flush(flushToDisk: true)`，写完即落盘，不依赖任何后台线程是否还活着；
///  3) 只写**事实**，不读也不改任何业务状态 —— 证据通道坏了不许影响业务结论。
///
/// 文件：`&lt;证据目录&gt;\crash-evidence.jsonl`，一行一条 JSON（追加，线程安全，超限自动轮转一份 .1）。
/// 本类**任何情况下都不抛**：写证据失败只返回 false。
/// </summary>
public static class CrashEvidence
{
    /// <summary>单文件上限（超过则把现文件轮转为 .1 再继续写，避免证据把磁盘写满）。</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    /// <summary>单条 detail 上限（崩溃堆栈可能极长，超出截断并留标记）。</summary>
    public const int MaxDetailChars = 4000;

    private static readonly object Gate = new();
    private static string? _root;

    /// <summary>
    /// ★ 中文不转义 ★：这份文件是给人直接看 / 直接 grep 的证据（排障现场不会去解 \uXXXX）。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 证据目录。默认与应用日志同目录（%ProgramData%\PCMig\Logs），可被测试整体改写。
    /// 读取时不创建目录、不做 IO。
    /// </summary>
    public static string RootDirectory
    {
        get => _root ?? Logging.LogBootstrap.AppLogDir;
        set => _root = value;
    }

    /// <summary>证据文件完整路径（不保证存在）。</summary>
    public static string FilePath => Path.Combine(RootDirectory, "crash-evidence.jsonl");

    /// <summary>
    /// 追加一条证据。返回是否成功落盘（**不抛异常**）。
    /// </summary>
    /// <param name="kind">证据类别，例如 "ui-stall" / "ui-exception" / "appdomain-unhandled"。</param>
    /// <param name="detail">人类可读细节（异常 ToString、耗时等），超长自动截断。</param>
    /// <param name="where">来源位置，例如 "MainWindow.FooterStop_Click"。</param>
    /// <param name="dir">可选：本次写入使用的证据目录（默认 <see cref="RootDirectory"/>）。</param>
    public static bool Write(string kind, string detail, string? where = null, string? dir = null)
    {
        try
        {
            var root = dir ?? RootDirectory;
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "crash-evidence.jsonl");

            var line = JsonSerializer.Serialize(new
            {
                t = DateTime.UtcNow.ToString("O"),
                pid = Environment.ProcessId,
                kind,
                where,
                detail = Truncate(detail ?? string.Empty),
            }, JsonOptions);

            lock (Gate)
            {
                RotateIfTooBig(path);
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true); // ★关键★：写完即落盘，进程随后被强杀也不丢
            }
            return true;
        }
        catch
        {
            return false; // 证据通道自身绝不外溢异常
        }
    }

    /// <summary>读回最近若干条原始行（排障 / 复验用）。读失败返回空列表。</summary>
    public static IReadOnlyList<string> ReadRecent(int maxLines = 50, string? dir = null)
    {
        try
        {
            var path = Path.Combine(dir ?? RootDirectory, "crash-evidence.jsonl");
            if (!File.Exists(path)) return Array.Empty<string>();
            var lines = File.ReadAllLines(path);
            return lines.Length <= maxLines ? lines : lines[^maxLines..];
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static void RotateIfTooBig(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < MaxBytes) return;
            var rolled = path + ".1";
            File.Delete(rolled);
            File.Move(path, rolled);
        }
        catch
        {
            // 轮转失败也要继续写（宁可文件超一点，也不能丢证据）
        }
    }

    private static string Truncate(string s)
        => s.Length <= MaxDetailChars ? s : s[..MaxDetailChars] + "…（已截断）";
}