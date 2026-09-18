using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PCMig.Gui;

/// <summary>
/// 苹果风的窗口材质与动效（Win11 系统级亚克力 + 圆角 + 弹出缩放）。
/// 全部调用都吞异常：旧系统或不支持时自动退化为普通窗口，绝不影响功能。
/// </summary>
internal static class WindowEffects
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public int AccentState, AccentFlags, GradientColor, AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfData; }

    /// <summary>半透明亚克力背景 + 圆角窗口，并把根元素底色降透明度让模糊透出来。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    public static void ApplyAcrylic(Window w)
    {
        try
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) return;
            int round = 2;     // 圆角窗口
            DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

            // 亚克力走 SetWindowCompositionAttribute，并自带色调（0xAABBGGRR）。
            // 关键：绝不再把窗口背景设成透明——那会让整个窗口变全透明/黑屏（上一版就栽在这里）。
            // Win11 官方材质：DWMWA_SYSTEMBACKDROP_TYPE=38，3=亚克力(TRANSIENTWINDOW)。
            // 26200 上旧的 SetWindowCompositionAttribute 亚克力会画成黑底，必须走这条；失败再回退旧路。
            int backdrop = 3;
            int hr = DwmSetWindowAttribute(h, 38, ref backdrop, sizeof(int));
            if (hr != 0)
            {
                var accent = new AccentPolicy
                {
                    AccentState = 4,                              // ACCENT_ENABLE_ACRYLICBLURBEHIND
                    AccentFlags = 2,
                    GradientColor = unchecked((int)0xD8F9F5F1),    // 半透明 #F1F5F9 色调
                    AnimationId = 0
                };
                int size = Marshal.SizeOf(accent);
                var ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(accent, ptr, false);
                    var data = new WindowCompositionAttributeData { Attribute = 19, Data = ptr, SizeOfData = size };
                    if (SetWindowCompositionAttribute(h, ref data) == 0) return;  // 不支持 → 保持原样，不冒险
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }

            // 标题栏文字改深色（玻璃底偏亮，默认白字会看不见）：DWMWA_CAPTION_COLOR=36，0x00BBGGRR
            int caption = unchecked((int)0x002A170F);              // #0F172A
            DwmSetWindowAttribute(h, 36, ref caption, sizeof(int));

            // 让根面板也半透明一点，模糊才透得出来（文字仍清晰）
            if (w.Content is Panel p && p.Background is SolidColorBrush sb && sb.Color.A == 255)
                p.Background = new SolidColorBrush(Color.FromArgb(0xE6, sb.Color.R, sb.Color.G, sb.Color.B));
        }
        catch { /* 不支持就保持原样 */ }
    }

    /// <summary>真·实时毛玻璃：DWM 官方 backdrop（亚克力，实时模糊窗后内容）。成功 true；失败由调用方回退渐变底。</summary>
    public static bool TryApplyLiveGlass(Window w)
    {
        try
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) return false;
            int round = 2;
            DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            int backdrop = 3;   // DWMSBT_TRANSIENTWINDOW = 亚克力
            if (DwmSetWindowAttribute(h, 38, ref backdrop, sizeof(int)) != 0) return false;
            // 关键一步：把 DWM 玻璃面扩展进客户区（缺了它 backdrop 只铺标题栏——红蓝实验证实）
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(h, ref margins);
            int caption = unchecked((int)0x002A170F);   // 深色标题字
            DwmSetWindowAttribute(h, 36, ref caption, sizeof(int));
            w.Background = System.Windows.Media.Brushes.Transparent;
            return true;
        }
        catch { return false; }
    }

    /// <summary>二级窗口弹出：淡入 + 轻微放大（180ms，ease-out），关闭时反向不做（Windows 自身负责收起）。</summary>
    public static void PlayEnter(Window w)
    {
        try
        {
            if (w.Content is not UIElement root) return;
            w.Opacity = 0;
            var scale = new ScaleTransform(0.96, 0.96);
            root.RenderTransformOrigin = new Point(0.5, 0.5);
            root.RenderTransform = scale;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            w.BeginAnimation(Window.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        }
        catch { }
    }
}
