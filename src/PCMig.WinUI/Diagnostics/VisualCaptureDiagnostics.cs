using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PCMig.WinUI.Diagnostics;

/// <summary>
/// ★ 诊断专用（PMML 返修 D5 像素证据，2026-10-12）★
/// 当环境变量 <c>PCMIG_PANEL_EXPORT=&lt;目录&gt;</c> 被设置时，把窗口的**真实 XAML 视觉树**渲染成 PNG。
///
/// 为什么需要它（本机实测，勿重复踩）：
///   · <c>Graphics.CopyFromScreen</c> ⇒ 抛「句柄无效。」（本会话进程拿不到交互桌面的 DC）；
///   · 直接 <c>BitBlt</c> 桌面 DC ⇒ 同样失败；
///   · <c>PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)</c> 对 WinUI 3 合成窗口 ⇒ 返回 91 色纯白空图（实测 1724×1091、98.2% 白），
///     因为 WinUI 3 由合成器渲染，GDI 抓不到内容（同一个 PrintWindow 抓普通 Chromium 窗口却完全正常）。
///   ⇒ 唯一可行的真实像素路径是应用**自己**渲染自己：RenderTargetBitmap 直接渲染 XAML 视觉树，
///     与既有 <c>PCMIG_STATCARD_EXPORT</c>（Step3ProgressPage.TryExportStatCards）同法、同纪律。
///
/// ★ 纪律 ★ 未设环境变量时所有方法立即返回；导出失败一律吞掉（只写一行日志），
/// 绝不改变任何生产行为、绝不参与任何业务判定、绝不写业务状态。
/// </summary>
internal static class VisualCaptureDiagnostics
{
    /// <summary>导出目录；未设置 <c>PCMIG_PANEL_EXPORT</c> 时为 null（= 功能关闭）。</summary>
    internal static string? ExportDir
    {
        get
        {
            try
            {
                var dir = Environment.GetEnvironmentVariable("PCMIG_PANEL_EXPORT");
                return string.IsNullOrWhiteSpace(dir) ? null : dir;
            }
            catch
            {
                return null;
            }
        }
    }

    internal static bool Enabled => ExportDir is not null;

    /// <summary>
    /// 按**指定时刻**（相对本次调用，毫秒）各抓一帧，用于取得浮层 morph 的真实像素序列。
    /// 为什么不用固定节拍定时器（实测教训，2026-10-12）：DispatcherQueueTimer 的第一拍实测晚到 ~9.7 s，
    /// 于是整段动画早已结束、连拍拍到的全是终态；而 RenderAsync **在调用那一刻**渲染当前视觉树，
    /// 所以「先等到目标时刻、再调用 RenderAsync」才能真正对齐动画进度（单帧编码慢不影响取样时刻）。
    /// 帧名 <c>{prefix}-t{offset:D4}.png</c>，取样时刻与耗时记到 <c>{prefix}-timeline.log</c>。
    /// </summary>
    internal static void CaptureTimeline(FrameworkElement? element, string prefix, params int[] offsetsMs)
    {
        var dir = ExportDir;
        if (dir is null || element is null || offsetsMs is null || offsetsMs.Length == 0) return;
        if (element.DispatcherQueue is null) return;

        try { System.IO.Directory.CreateDirectory(dir); }
        catch { return; }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var log = new System.Text.StringBuilder();
        var maxOffset = 0;
        foreach (var offset in offsetsMs)
        {
            if (offset > maxOffset) maxOffset = offset;
            _ = CaptureAtOffsetAsync(element, dir, prefix, offset, clock, log);
        }

        _ = WriteTimelineLogAsync(dir, prefix, log, maxOffset + 8000);
    }

    private static async Task CaptureAtOffsetAsync(
        FrameworkElement element, string dir, string prefix, int offsetMs,
        System.Diagnostics.Stopwatch clock, System.Text.StringBuilder log)
    {
        try
        {
            var wait = offsetMs - (int)clock.ElapsedMilliseconds;
            if (wait > 0) await Task.Delay(wait);

            var name = prefix + "-t" + offsetMs.ToString("D4");
            var startedAt = clock.ElapsedMilliseconds;   // ★ 取样时刻（RenderAsync 调用那一刻的视觉树）
            await CaptureAsync(element, dir, name);

            lock (log) log.AppendLine($"{name} started={startedAt}ms written={clock.ElapsedMilliseconds}ms ok");
        }
        catch (Exception ex)
        {
            lock (log) log.AppendLine($"{prefix}-t{offsetMs:D4} EX {ex.GetType().Name} {ex.Message}");
        }
    }

    private static async Task WriteTimelineLogAsync(string dir, string prefix, System.Text.StringBuilder log, int delayMs)
    {
        try
        {
            await Task.Delay(delayMs);
            lock (log) System.IO.File.WriteAllText(System.IO.Path.Combine(dir, prefix + "-timeline.log"), log.ToString());
        }
        catch
        {
            // 日志写不出去不影响界面。
        }
    }

    /// <summary>单帧导出（真实视觉树 ⇒ PNG）。异常由调用方决定是否吞掉。</summary>
    internal static async Task CaptureAsync(FrameworkElement element, string dir, string name)
    {
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(element);
        var pixels = await rtb.GetPixelsAsync();

        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(dir);
        var file = await folder.CreateFileAsync(name + ".png", Windows.Storage.CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
            Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            (uint)rtb.PixelWidth,
            (uint)rtb.PixelHeight,
            96,
            96,
            System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixels));
        await encoder.FlushAsync();
    }
}