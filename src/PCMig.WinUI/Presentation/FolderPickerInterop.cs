using System;
using System.Runtime.InteropServices;

namespace PCMig.WinUI.Presentation;

/// <summary>
/// 文件夹选择对话框的 **Win32 原生实现**（<c>IFileOpenDialog</c> + <c>FOS_PICKFOLDERS</c>）。
///
/// 【为什么不用 Windows.Storage.Pickers.FolderPicker】（2026-09-30 实测定位）
///   本应用是 **unpackaged**（`PCMig.WinUI.csproj` 里 `WindowsPackageType = None`）。
///   在 unpackaged 的 WinUI 3 应用里，`FolderPicker.PickSingleFolderAsync()` 是已知不可靠的：
///   它会**静默返回 null**——不弹窗、不抛异常，界面上表现为"点了没反应"，
///   正是用户实测反馈的「浏览按钮像假的」。
///   ⇒ 改用 Win32 原生 `IFileOpenDialog`：它不依赖 MSIX 打包上下文，
///     在任何 Win32/WinUI 进程里都能正常弹出标准"选择文件夹"对话框。
///
/// 【边界】只做"弹标准文件夹选择框并返回路径"这一件事：
///   · 不碰业务、不读盘、不写盘、不改任何状态；
///   · 自身不弹任何自有 UI（失败时返回 null，由调用方决定如何提示用户）。
/// </summary>
internal static class FolderPickerInterop
{
    // ── 常量（shobjidl_core.h）──
    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint FOS_PATHMUSTEXIST = 0x00000800;
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const int ERROR_CANCELLED = unchecked((int)0x800704C7);
    private const int S_OK = 0;

    /// <summary>
    /// 弹出"选择文件夹"对话框（父窗口为 <paramref name="ownerHwnd"/>）。
    /// 用户选中返回其绝对路径；取消或失败返回 <c>null</c>（调用方据此提示，不抛异常）。
    /// </summary>
    public static string? PickFolder(IntPtr ownerHwnd, string? title = null, string? initialPath = null)
    {
        IFileOpenDialog? dialog = null;
        IShellItem? item = null;
        try
        {
            Trace($"=== PickFolder 开始 ownerHwnd=0x{ownerHwnd.ToInt64():X} title={title} seed={initialPath} ===");
            // ★ 关键（2026-09-30 实测修正）★ 不要用 `Activator.CreateInstance(Type.GetTypeFromCLSID(...))`：
            //   实测抛 `80040154 没有注册类 (REGDB_E_CLASSNOTREG)` —— 该方式在本进程拿不到 COM 类工厂。
            //   直接 `new` 这个 [ComImport] 类即可，运行时会走标准 CoCreateInstance(CLSID_FileOpenDialog)。
            dialog = new FileOpenDialogRCW() as IFileOpenDialog;
            if (dialog is null) { Trace("new FileOpenDialogRCW() 未能转成 IFileOpenDialog"); return null; }
            Trace("CoCreateInstance 成功（new FileOpenDialogRCW）");

            var options = FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST;
            dialog.GetOptions(out var current);
            dialog.SetOptions(current | options);
            Trace($"SetOptions 成功 (current=0x{current:X})");

            if (!string.IsNullOrWhiteSpace(title))
                dialog.SetTitle(title);

            // 可选：把对话框定位到已填的路径（不存在时静默忽略，不影响弹出）
            if (!string.IsNullOrWhiteSpace(initialPath))
            {
                try
                {
                    var iid = typeof(IShellItem).GUID;
                    var hrSeed = SHCreateItemFromParsingName(initialPath!, IntPtr.Zero, ref iid, out var folder);
                    Trace($"SHCreateItemFromParsingName hr=0x{hrSeed:X} folder={(folder is null ? "null" : "ok")}");
                    if (hrSeed == S_OK && folder is not null)
                    {
                        dialog.SetFolder(folder);
                        Marshal.ReleaseComObject(folder);
                    }
                }
                catch (Exception exSeed) { Trace("seed 定位异常(忽略): " + exSeed.GetType().Name + ":" + exSeed.Message); }
            }

            Trace("即将 Show()（此处若卡住说明对话框已弹出但被遮挡/未激活）");
            var hr = dialog.Show(ownerHwnd);
            Trace($"Show 返回 hr=0x{hr:X}（ERROR_CANCELLED=0x{ERROR_CANCELLED:X}）");
            if (hr == ERROR_CANCELLED) return null;       // 用户点了取消：不是错误
            if (hr != S_OK) return null;

            dialog.GetResult(out item);
            if (item is null) { Trace("GetResult 返回 null"); return null; }
            item.GetDisplayName(SIGDN_FILESYSPATH, out var path);
            Trace("GetDisplayName = " + (path ?? "(null)"));
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (Exception ex)
        {
            Trace("★ 异常: " + ex.GetType().FullName + " : " + ex.Message);
            return null;
        }
        finally
        {
            if (item is not null) { try { Marshal.ReleaseComObject(item); } catch { } }
            if (dialog is not null) { try { Marshal.ReleaseComObject(dialog); } catch { } }
            Trace("=== PickFolder 结束 ===");
        }
    }

    /// <summary>诊断（写 exe 旁日志）。定位"浏览点了没反应"用；失败也绝不影响业务。</summary>
    private static void Trace(string msg)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppContext.BaseDirectory, "a5-folderpicker-trace.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
        }
        catch { }
    }

    // ══════════════════════ COM 互操作（方法顺序必须与 shobjidl_core.h 一致）══════════════════════

    // ★ CLSID_FileOpenDialog ★（注意：**不是** IID_IFileDialog）
    //   CLSID_FileOpenDialog = {DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7}
    //   曾经的错误：把 IID_IFileDialog({42F85136-DB7E-439C-85F1-E4075D135FC8}) 当 CLSID 用
    //   ⇒ CoCreateInstance 报 `80040154 没有注册类 (REGDB_E_CLASSNOTREG)`（实测日志 a5-folderpicker-trace.log）。
    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7"), ClassInterface(ClassInterfaceType.None)]
    private class FileOpenDialogRCW { }

    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        // IModalWindow
        [PreserveSig] int Show(IntPtr parent);
        // IFileDialog
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        // IFileOpenDialog
        void GetResults(out IntPtr ppenum);
        void GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem? ppv);
}