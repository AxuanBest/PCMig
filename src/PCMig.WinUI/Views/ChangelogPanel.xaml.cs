using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PCMig.WinUI.Presentation;

namespace PCMig.WinUI.Views;

/// <summary>WinUI 内置更新日志面板；只呈现嵌入的版本历史，不参与迁移业务状态。</summary>
public sealed partial class ChangelogPanel : UserControl
{
    private IReadOnlyList<ChangelogEntry> _entries = Array.Empty<ChangelogEntry>();

    public event EventHandler? CloseRequested;

    public ChangelogPanel()
    {
        InitializeComponent();
        try
        {
            _entries = ChangelogReader.Load();
            VersionList.ItemsSource = _entries;
            CaptionText.Text = $"共 {_entries.Count} 个版本（新 → 旧）";
            if (_entries.Count > 0) VersionList.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            CaptionText.Text = "无法加载内置更新日志";
            EntryBodyText.Text = ex.Message;
        }
    }

    public void FocusFirstElement()
    {
        CloseButton.Focus(FocusState.Programmatic);
    }

    private void VersionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VersionList.SelectedItem is not ChangelogEntry entry) return;
        EntryTitleText.Text = entry.DisplayTitle;
        EntryDateText.Text = string.IsNullOrWhiteSpace(entry.Date) ? "" : $"发布日期 {entry.Date}";
        EntryBodyText.Text = Format(entry.Lines);
    }

    private static string Format(IEnumerable<string> lines) => string.Join("\n", lines.Select(line =>
    {
        var text = line.TrimEnd();
        if (text.StartsWith("### ", StringComparison.Ordinal)) return text[4..].Replace("**", "");
        if (text.StartsWith("- ", StringComparison.Ordinal)) return "• " + text[2..].Replace("**", "");
        return text.Replace("**", "");
    }));

    private void Previous_Click(object sender, RoutedEventArgs e) => MoveSelection(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => MoveSelection(1);

    private void MoveSelection(int delta)
    {
        if (VersionList.Items.Count == 0) return;
        var current = VersionList.SelectedIndex < 0 ? 0 : VersionList.SelectedIndex;
        VersionList.SelectedIndex = Math.Clamp(current + delta, 0, VersionList.Items.Count - 1);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
