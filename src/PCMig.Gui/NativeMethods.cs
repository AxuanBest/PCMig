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
}
