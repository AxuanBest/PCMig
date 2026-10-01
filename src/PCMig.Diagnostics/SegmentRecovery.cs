using System.Security.Cryptography;
using System.Text;
using PCMig.Diagnostics.Abstractions.Serialization;

namespace PCMig.Diagnostics;

/// <summary>
/// JSONL 段恢复与清单（方案 §15）。
///
/// 崩溃后的口径：
///   · 末行不完整 ⇒ 截掉它，并**如实记下截掉多少字节**（不是"没有事件"）；
///   · 完整但解析不了的行 ⇒ 计 CorruptLines（保留在文件里，不吞掉、不假装不存在）；
///   · 从完整行里取最后一个 Sequence 作为该段的水位。
/// </summary>
public static class SegmentRecovery
{
    private const long MaxRecoverableBytes = 64L * 1024 * 1024;

    /// <summary>
    /// 扫描（可选截断）活动段。<paramref name="truncate"/> = true 时把不完整的末行从文件里去掉。
    /// </summary>
    public static SegmentRecoveryResult RecoverActive(string path, bool truncate)
    {
        if (!File.Exists(path)) return new SegmentRecoveryResult(false, 0, 0, 0, 0);

        var info = new FileInfo(path);
        if (info.Length == 0) return new SegmentRecoveryResult(true, 0, 0, 0, 0);
        if (info.Length > MaxRecoverableBytes)
            return new SegmentRecoveryResult(true, 0, info.Length, 0, 0); // 过大 ⇒ 不读，交给上层标记

        // ★ D6.3 ★ 共享模式必须允许"另一个进程/实例正持有这个段的写句柄"。
        // File.ReadAllBytes 以 FileAccess.Read + FileShare.Read 打开：共享检查是双向的，
        // 它拒绝其它写句柄 ⇒ 只要上一段会话**仍然存活**（或上一次进程还没退净），
        // 这里就抛 IOException "being used by another process"；调用方一旦把它吞掉，
        // "上次未确认正常关闭"就会**静默消失**（实测缺陷，见 D6.3 报告）。
        // 恢复扫描是只读的，不该独占；ComputeSha256 早已用同一共享模式。
        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (stream.Length > MaxRecoverableBytes)
                return new SegmentRecoveryResult(true, 0, stream.Length, 0, 0); // 过大 ⇒ 不读，交给上层标记

            bytes = new byte[stream.Length];
            var read = 0;
            while (read < bytes.Length)
            {
                var n = stream.Read(bytes, read, bytes.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read < bytes.Length) Array.Resize(ref bytes, read);
        }

        var lastNewline = Array.LastIndexOf(bytes, (byte)'\n');
        var truncatedTail = lastNewline < 0 ? bytes.Length : bytes.Length - (lastNewline + 1);

        if (truncate && truncatedTail > 0)
        {
            var keep = lastNewline < 0 ? 0 : lastNewline + 1;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            fs.SetLength(keep);
            fs.Flush(true);
            bytes = keep == 0 ? Array.Empty<byte>() : bytes.AsSpan(0, (int)keep).ToArray();
        }

        long completeLines = 0;
        long corruptLines = 0;
        long lastSequence = 0;

        var start = 0;
        while (start < bytes.Length)
        {
            var end = Array.IndexOf(bytes, (byte)'\n', start);
            if (end < 0) break;
            var length = end - start;
            if (length > 0)
            {
                completeLines++;
                if (DiagnosticEventJson.TryParse(bytes.AsSpan(start, length), out var evt, out _) && evt is not null)
                    lastSequence = Math.Max(lastSequence, evt.Sequence);
                else
                    corruptLines++;
            }
            start = end + 1;
        }

        return new SegmentRecoveryResult(true, completeLines, truncatedTail, corruptLines, lastSequence);
    }

    public static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static SegmentManifest BuildManifest(
        string family,
        string segmentPath,
        long eventCount,
        long firstSequence,
        long lastSequence,
        DateTimeOffset firstUtc,
        DateTimeOffset lastUtc,
        bool partial,
        long corruptLines)
    {
        var length = File.Exists(segmentPath) ? new FileInfo(segmentPath).Length : 0;
        return new SegmentManifest
        {
            Family = family,
            FileName = Path.GetFileName(segmentPath),
            Length = length,
            Sha256 = File.Exists(segmentPath) ? ComputeSha256(segmentPath) : string.Empty,
            EventCount = eventCount,
            FirstSequence = firstSequence,
            LastSequence = lastSequence,
            FirstTimestampUtc = firstUtc.ToUniversalTime().ToString("O"),
            LastTimestampUtc = lastUtc.ToUniversalTime().ToString("O"),
            Partial = partial,
            CorruptLines = corruptLines,
            SealedUtc = DateTimeOffset.UtcNow.ToString("O"),
        };
    }

    /// <summary>把一个段的文本内容切行（导出/离线读取用；不做截断）。</summary>
    public static IEnumerable<string> ReadLines(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
            if (line.Length > 0) yield return line;
    }
}