using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace PCMig.WinUI.Presentation;

/// <summary>
/// 只读更新日志条目。内容唯一来源是随程序嵌入的 docs\更新日志.md，
/// 不读取或写入任何迁移任务、业务配置或存档。
/// </summary>
public sealed class ChangelogEntry
{
    public string Version { get; init; } = "";
    public string Title { get; init; } = "";
    public string Date { get; init; } = "";
    public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Version : $"{Version} · {Title}";
}

/// <summary>把既有 Markdown 更新日志投影为 WinUI 面板需要的只读版本列表。</summary>
public static class ChangelogReader
{
    private const string ResourceName = "PCMig.WinUI.Assets.CHANGELOG.md";

    public static IReadOnlyList<ChangelogEntry> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("内置更新日志缺失。");
        using var reader = new StreamReader(stream);
        var raw = reader.ReadToEnd().Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var dates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in raw)
        {
            var text = line.Trim();
            if (!text.StartsWith("|", StringComparison.Ordinal)) continue;
            var cells = text.Trim('|').Split('|');
            if (cells.Length >= 3 && cells[0].Trim().StartsWith("v", StringComparison.OrdinalIgnoreCase))
                dates.TryAdd(cells[0].Trim(), cells[1].Trim());
        }

        var entries = new List<ChangelogEntry>();
        string? version = null;
        string title = "";
        var lines = new List<string>();

        void Commit()
        {
            if (version is null) return;
            entries.Add(new ChangelogEntry
            {
                Version = version,
                Title = title,
                Date = dates.TryGetValue(version, out var date) ? date : "",
                Lines = lines.ToArray(),
            });
        }

        foreach (var line in raw)
        {
            var text = line.TrimEnd();
            if (text.StartsWith("## v", StringComparison.OrdinalIgnoreCase))
            {
                Commit();
                var header = text[3..].Trim();
                var parts = header.Split(new[] { " — ", " - " }, 2, StringSplitOptions.None);
                version = parts[0].Trim();
                title = parts.Length > 1 ? parts[1].Trim() : "";
                lines = new List<string>();
            }
            else if (version is not null)
            {
                lines.Add(text);
            }
        }

        Commit();
        return entries;
    }
}
