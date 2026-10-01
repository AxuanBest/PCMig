using System;
using System.Runtime.InteropServices;

namespace PCMig.WinUI.Presentation;

/// <summary>
/// Win32 最小追踪尺寸：WinUI 3 未公开 min-size API，这里在窗口 HWND 上挂一个
/// WM_GETMINMAXINFO 子类过程，把 ptMinTrackSize 抬到设计下限。
/// 纯窗口行为，不接触迁移业务、导航或绑定状态。
///
/// 关键点：ptMinTrackSize 是**含边框的整窗**尺寸，而设计下限是**客户区**尺寸。
/// 直接赋值会让客户区被边框吃掉（实测 100% DPI 下水平少 16、垂直少 8）。
/// 因此安装时用 AdjustWindowRectExForDpi 反算出"该客户区对应的整窗尺寸"再赋值。
/// </summary>
internal static class WindowMinimumSize
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const nuint SubclassId = 1;

    // AdjustWindowRectExForDpi 需要的窗口样式位（只影响边框计算，不修改窗口本身）。
    private const uint WsCaption = 0x00C00000;
    private const uint WsThickFrame = 0x00040000;
    private const uint WsSysMenu = 0x00080000;
    private const uint WsMinimizeBox = 0x00020000;
    private const uint WsMaximizeBox = 0x00010000;

    // 委托必须保活：一旦被 GC 回收，框架回调会命中已释放的 thunk（进程级崩溃）。
    private static SubclassProc? _proc;
    private static int _minWidthPx;
    private static int _minHeightPx;
    private static bool _installed;

    /// <summary>在窗口句柄可用后安装一次；重复调用幂等。</summary>
    public static void Install(IntPtr hwnd, double minWidthDip, double minHeightDip, double scale)
    {
        if (_installed || hwnd == IntPtr.Zero) return;
        var s = scale > 0 ? scale : 1.0;
        var clientW = Math.Max(1, (int)Math.Round(minWidthDip * s));
        var clientH = Math.Max(1, (int)Math.Round(minHeightDip * s));

        // 把"客户区下限"换算成"整窗下限"：加上标题栏与可调边框。
        var frameW = 0;
        var frameH = 0;
        try
        {
            var dpi = (int)GetDpiForWindow(hwnd);
            if (dpi <= 0) dpi = (int)Math.Round(96 * s);
            var r = new Rect { Left = 0, Top = 0, Right = clientW, Bottom = clientH };
            var style = WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox;
            if (AdjustWindowRectExForDpi(ref r, style, false, 0, (uint)dpi))
            {
                frameW = (r.Right - r.Left) - clientW;
                frameH = (r.Bottom - r.Top) - clientH;
                if (frameW < 0) frameW = 0;
                if (frameH < 0) frameH = 0;
            }
        }
        catch
        {
            // 换算失败则退回加常量边框的安全值，宁可略大也不要小于设计下限。
            frameW = 16;
            frameH = 39;
        }

        _minWidthPx = clientW + frameW;
        _minHeightPx = clientH + frameH;
        _proc = OnSubclass;
        _installed = SetWindowSubclass(hwnd, _proc, SubclassId, IntPtr.Zero);
    }

    private static IntPtr OnSubclass(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, nuint idSubclass, IntPtr refData)
    {
        // 先让底层过程填默认值，再用我们的下限覆盖；顺序颠倒会被 DefWindowProc 覆盖回去。
        var result = DefSubclassProc(hWnd, msg, wParam, lParam);
        if (msg == WmGetMinMaxInfo && lParam != IntPtr.Zero)
        {
            var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            if (mmi.MinTrackSize.X < _minWidthPx) mmi.MinTrackSize.X = _minWidthPx;
            if (mmi.MinTrackSize.Y < _minHeightPx) mmi.MinTrackSize.Y = _minHeightPx;
            Marshal.StructureToPtr(mmi, lParam, false);
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, nuint idSubclass, IntPtr refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AdjustWindowRectExForDpi(ref Rect lpRect, uint dwStyle, bool bMenu, uint dwExStyle, uint dpi);
}
