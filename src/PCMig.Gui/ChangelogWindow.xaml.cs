using System;
using System.Collections.Generic;
using Serilog;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace PCMig.Gui;

/// <summary>
/// 应用内「更新日志」窗口：左侧按版本列出（新→旧），右侧显示该版本明细。
/// 内容随程序内置（嵌入资源），另外安装目录下会有同名 TXT 可直接用记事本打开。
/// </summary>
public partial class ChangelogWindow : Window
{
    private sealed class VerItem
    {
        public string Version { get; set; } = "";
        public string Title { get; set; } = "";
        public string Date { get; set; } = "";
        public List<string> Lines { get; } = new();
    }

    private const string ChangelogFileName = "更新日志.txt";
    private readonly List<VerItem> _items = new();

    public ChangelogWindow()
    {
        InitializeComponent();
        try
        {
            LoadChangelog();
        }
        catch (Exception ex)
        {
            HeaderCaption.Text = "无法加载更新日志：" + ex.Message;
        }
    }

    private static string? ReadEmbeddedChangelog()
    {
        var uri = new Uri("pack://application:,,,/PCMig;component/Assets/CHANGELOG.md", UriKind.Absolute);
        var info = Application.GetResourceStream(uri);
        if (info?.Stream == null) return null;
        using var reader = new StreamReader(info.Stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private void LoadChangelog()
    {
        var text = ReadEmbeddedChangelog();
        if (string.IsNullOrWhiteSpace(text))
        {
            HeaderCaption.Text = "内置更新日志缺失。";
            return;
        }

        var raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var dates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 先扫一遍版本/日期对照表，给每个版本配日期
        foreach (var line in raw)
        {
            var t = line.Trim();
            if (!t.StartsWith("|")) continue;
            var cells = t.Trim('|').Split('|');
            if (cells.Length >= 3)
            {
                var v = cells[0].Trim();
                if (v.StartsWith("v0.") && !dates.ContainsKey(v)) dates[v] = cells[1].Trim();
            }
        }

        var header = new VerItem { Version = "总览", Title = "版本对照表与说明", Date = "" };
        VerItem? cur = null;

        foreach (var line in raw)
        {
            var t = line.TrimEnd();
            if (t.StartsWith("## v", StringComparison.Ordinal))
            {
                var head = t.Substring(3).Trim();
                var parts = head.Split(new[] { " — ", " - " }, 2, StringSplitOptions.None);
                var ver = parts[0].Trim();
                var title = parts.Length > 1 ? parts[1].Trim() : "";
                cur = new VerItem { Version = ver, Title = title, Date = dates.TryGetValue(ver, out var d) ? d : "" };
                _items.Add(cur);
                continue;
            }
            if (cur == null) header.Lines.Add(t);
            else cur.Lines.Add(t);
        }

        var all = new List<VerItem> { header };
        all.AddRange(_items);
        VerList.ItemsSource = all;

        HeaderCaption.Text = "共 " + _items.Count + " 个版本（从 v0.1.0 起）· 安装目录下的 " + ChangelogFileName
            + " 可用记事本直接打开 · 命令行 pcmig changelog 同样可查看";

        var txt = FindTxtPath();
        FooterHint.Text = txt != null
            ? "文本文件：" + txt
            : "安装目录下未找到 " + ChangelogFileName + "（便携运行时可在程序目录放置同名文件）";
        BtnOpenTxt.IsEnabled = true;

        if (all.Count > 1) VerList.SelectedIndex = 1;   // 默认选中最新版本
        else if (all.Count > 0) VerList.SelectedIndex = 0;
    }

    private static string? FindTxtPath()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, ChangelogFileName),
            Path.Combine(Environment.CurrentDirectory, ChangelogFileName)
        };
        if (Environment.ProcessPath != null)
        {
            var dir = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrEmpty(dir)) candidates.Add(Path.Combine(dir, ChangelogFileName));
        }
        foreach (var c in candidates)
            if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c;
        return null;
    }

    /// <summary>
    /// 兜底：不依赖模板命中——鼠标抬起时按点击位置回溯到所在行，显式选中它。
    /// 旧写法只靠 ListBox 自身的选中逻辑，一旦项模板/虚拟化行为异常就"点哪儿都没反应"。
    /// </summary>
    private void VerList_PreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        DependencyObject? dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not ListBoxItem)
            dep = VisualTreeHelper.GetParent(dep);
        if (dep is not ListBoxItem row) return;
        var v = (row.DataContext as VerItem)?.Version ?? "?";
        if (!ReferenceEquals(VerList.SelectedItem, row.DataContext))
        {
            App.Log?.Information("更新日志：点击命中行 {Version}，显式选中", v);
            VerList.SelectedItem = row.DataContext;
        }
        else
        {
            App.Log?.Information("更新日志：点击命中行 {Version}（本来就是选中项）", v);
        }
    }

    private void VerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VerList.SelectedItem is not VerItem item) return;
        App.Log?.Information("更新日志切换版本: {Version}", item.Version);
        ShowItem(item);
    }

    /// <summary>
    /// 把某个版本的内容渲染到右侧。列表选中与「上一版/下一版」按钮两条路都调它，
    /// 这样即使列表行点击在某些环境下不生效，用户也一定能翻看每个版本（用户反馈：点版本毫无反应）。
    /// </summary>
    private void ShowItem(VerItem item)
    {
        BodyPanel.Children.Clear();
        BodyScroll.ScrollToTop();

        if (item.Version != "总览")
        {
            BodyPanel.Children.Add(new TextBlock
            {
                Text = item.Version + (string.IsNullOrEmpty(item.Title) ? "" : "  " + item.Title),
                FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Brush("#0F172A"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2)
            });
            if (!string.IsNullOrEmpty(item.Date))
            {
                BodyPanel.Children.Add(new TextBlock
                {
                    Text = "发布日期 " + item.Date,
                    FontSize = 11, Foreground = Brush("#94A3B8"), Margin = new Thickness(0, 0, 0, 8)
                });
            }
        }

        foreach (var line in item.Lines) RenderLine(line);
    }

    private void RenderLine(string line)
    {
        var t = line.TrimEnd();
        if (t.Length == 0)
        {
            BodyPanel.Children.Add(new Border { Height = 6 });
            return;
        }
        if (t.StartsWith("### "))
        {
            BodyPanel.Children.Add(new TextBlock
            {
                Text = Strip(t.Substring(4)),
                FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Brush("#0F172A"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 3)
            });
            return;
        }
        if (t.StartsWith("# "))
        {
            BodyPanel.Children.Add(new TextBlock
            {
                Text = Strip(t.Substring(2)),
                FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Brush("#1D4ED8"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6)
            });
            return;
        }
        if (t.StartsWith("|"))
        {
            var cells = t.Trim('|').Split('|');
            var clean = new List<string>();
            foreach (var c in cells)
            {
                var v = c.Trim();
                if (v.Length == 0 || v.StartsWith("---")) continue;
                clean.Add(v);
            }
            if (clean.Count == 0) return;
            BodyPanel.Children.Add(new TextBlock
            {
                Text = string.Join("    ", clean),
                FontSize = 11.5, FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"), Foreground = Brush("#475569"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1)
            });
            return;
        }
        if (t.StartsWith("- "))
        {
            var tb = new TextBlock
            {
                FontSize = 12.5, Foreground = Brush("#334155"), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(10, 2, 0, 2)
            };
            tb.Inlines.Add(new Run("•  ") { Foreground = Brush("#2563EB") });
            AddBoldRuns(tb, t.Substring(2));
            BodyPanel.Children.Add(tb);
            return;
        }
        var para = new TextBlock
        {
            FontSize = 12.5, Foreground = Brush("#334155"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 2)
        };
        AddBoldRuns(para, t);
        BodyPanel.Children.Add(para);
    }

    /// <summary>把 markdown 的 **加粗** 逐段渲染成粗体 Run，其余原样。</summary>
    private static void AddBoldRuns(TextBlock tb, string text)
    {
        var idx = 0;
        while (true)
        {
            var a = text.IndexOf("**", idx, StringComparison.Ordinal);
            if (a < 0) { tb.Inlines.Add(new Run(text.Substring(idx))); break; }
            var b = text.IndexOf("**", a + 2, StringComparison.Ordinal);
            if (b < 0) { tb.Inlines.Add(new Run(text.Substring(idx))); break; }
            if (a > idx) tb.Inlines.Add(new Run(text.Substring(idx, a - idx)));
            tb.Inlines.Add(new Run(text.Substring(a + 2, b - a - 2)) { FontWeight = FontWeights.SemiBold });
            idx = b + 2;
        }
    }

    private static string Strip(string s) => s.Replace("**", "");

    private static SolidColorBrush Brush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex)!);

    private void PrevVer_Click(object sender, RoutedEventArgs e) => StepVersion(-1);
    private void NextVer_Click(object sender, RoutedEventArgs e) => StepVersion(1);

    /// <summary>按按钮切换上一个/下一个版本：不依赖鼠标命中列表行。</summary>
    private void StepVersion(int delta)
    {
        try
        {
            var count = VerList.Items.Count;
            if (count == 0) return;
            var i = VerList.SelectedIndex < 0 ? 0 : VerList.SelectedIndex;
            var n = Math.Max(0, Math.Min(count - 1, i + delta));
            VerList.SelectedIndex = n;
            if (VerList.SelectedItem is VerItem it) ShowItem(it);   // 双保险：事件没触发也照样刷新
            App.Log?.Information("更新日志：按钮切到 {Version}（{Index}/{Total}）",
                (VerList.SelectedItem as VerItem)?.Version ?? "?", n, count);
        }
        catch (Exception ex)
        {
            App.Log?.Error(ex, "更新日志按按钮切换版本失败");
        }
    }

    private void OpenTxt_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = FindTxtPath();
            if (path == null)
            {
                var tmp = Path.Combine(Path.GetTempPath(), "PCMig-" + ChangelogFileName);
                var embed = ReadEmbeddedChangelog() ?? "";
                File.WriteAllText(tmp, MarkdownToPlain(embed), new UTF8Encoding(true));
                path = tmp;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法打开文本文件：" + ex.Message, "PCMig 更新日志",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>没有随包 TXT 时的兜底：把内嵌 markdown 粗略转成纯文本。</summary>
    private static string MarkdownToPlain(string md)
    {
        var sb = new StringBuilder();
        foreach (var line in md.Replace("\r\n", "\n").Split('\n'))
        {
            var t = line.TrimEnd().Replace("**", "");
            if (t.StartsWith("## ")) sb.AppendLine().AppendLine("■ " + t.Substring(3));
            else if (t.StartsWith("### ")) sb.AppendLine("  --- " + t.Substring(4) + " ---");
            else if (t.StartsWith("# ")) sb.AppendLine(t.Substring(2));
            else if (t.StartsWith("- ")) sb.AppendLine("  · " + t.Substring(2));
            else if (t.StartsWith("|")) sb.AppendLine("  " + t.Trim('|').Replace("|", " ｜ ").Trim());
            else sb.AppendLine(t);
        }
        return sb.ToString();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
