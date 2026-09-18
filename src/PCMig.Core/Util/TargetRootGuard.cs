using System;
using System.IO;
using Serilog;

namespace PCMig.Core.Util
{
    /// <summary>
    /// 目标根目录可见性保护。
    ///
    /// 根因（本地合成实验已复现）：robocopy 会把"源根目录"的**目录属性**写到"目标根目录"上，
    /// 且 /DCOPY:T 拦不住（真实日志里就有一行 "正在复制目标目录属性 D:\新建文件夹\"）。
    /// 整盘迁移时源根是盘根（Windows 盘根天然带 Hidden+System），于是迁移完成后用户在资源管理器里
    /// 「找不到目标文件夹，但空间确实被占用」——实测踩中 D:\新建文件夹、E:\测试 两处，
    /// 甚至预先建好目标目录也照样被改（实验：预建 [Directory] → 复制后 [Hidden, System, Directory]）。
    ///
    /// 对策：PCMig 在跑完涉及目标根的通道后（以及每次开始/结束时）主动清掉 Hidden/System，
    /// 保证用户永远能在资源管理器里看到自己的数据。
    /// </summary>
    public static class TargetRootGuard
    {
        /// <summary>确保目标根目录存在且不带 Hidden/System。返回是否真的做了修正。</summary>
        public static bool EnsureVisible(string? targetRoot, ILogger? log = null)
        {
            if (string.IsNullOrWhiteSpace(targetRoot)) return false;
            try
            {
                Directory.CreateDirectory(targetRoot);
                var attr = File.GetAttributes(targetRoot);
                var bad = attr & (FileAttributes.Hidden | FileAttributes.System);
                if (bad == 0) return false;
                File.SetAttributes(targetRoot, attr & ~(FileAttributes.Hidden | FileAttributes.System));
                log?.Warning("目标根目录 {Path} 原先带 {Attr}（源盘根属性被 robocopy 带过来，会导致资源管理器里『看不见但空间已占』），已自动解除隐藏",
                    targetRoot, bad);
                return true;
            }
            catch (Exception ex)
            {
                log?.Warning(ex, "目标根目录可见性检查失败 {Path}", targetRoot);
                return false;
            }
        }
    }
}
