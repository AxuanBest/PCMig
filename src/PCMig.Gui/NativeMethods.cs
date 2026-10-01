using System;
using System.Runtime.InteropServices;

namespace PCMig.Gui;

/// <summary>Win32 P/Invoke 集合。</summary>
internal static class NativeMethods
{
    /// <summary>查询磁盘容量（支持 UNC 路径，如 \\host\E$\）。零枚举、即时返回。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetDiskFreeSpaceEx(string lpDirectoryName,
        out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    // ── 显示器工作区（自适应窗口尺寸用；只读查询，不修改任何系统设置）────────────────────
    internal const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;   // 显示器全区域（设备像素）
        public RECT rcWork;      // 工作区（已扣掉任务栏，设备像素）
        public uint dwFlags;

        internal static MONITORINFO Create() => new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int x;
        public int y;
    }

    /// <summary>
    /// WM_GETMINMAXINFO 的载荷（全部为设备像素）。
    /// 用它限制"最大化尺寸"与"拖拽缩放上限"：这是唯一不会与 WPF 的 DIP 级 Max* 约束打架的做法
    /// —— 实测给 Window.MaxWidth/MaxHeight 赋值会让最大化窗口的可视边框越过工作区 9px。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;       // 最大化时的尺寸
        public POINT ptMaxPosition;   // 最大化时的左上角
        public POINT ptMinTrackSize;  // 拖拽缩放下限（由 WPF 按 MinWidth/MinHeight 维护，不动）
        public POINT ptMaxTrackSize;  // 拖拽缩放上限
    }

    /// <summary>取窗口所在（或最近的）显示器句柄。</summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    /// <summary>取显示器信息（含工作区）。用于把窗口夹进可见区域。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>移动/缩放窗口。自适应夹取用它移动窗口（而不是写 WPF 的 Left/Top，见 MainWindow 说明）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
}
