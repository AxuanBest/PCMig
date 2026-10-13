using PCMig.Core.Transfer;

namespace PCMig.Core.Util;

/// <summary>
/// ★ P1-08 / R8 ★ 用**实测**区分「.pst/.ost 候选」与「真的被独占打不开」。
///
/// 为什么需要它：旧实现只看扩展名就断言"这些文件会被锁定导致迁移失败"——
/// 实际上**归档用的 .pst 根本不归 Outlook 打开**，未锁定完全可以迁移；
/// 反过来，真被 Outlook 独占的文件才该报 Win32 32/33。把两件事混成一件会造成假警报
/// （用户被告知"会失败"却实际上一直迁得好好的）。
///
/// 判据（与 <see cref="ShareLossSignal.IsFileLockWin32"/> 同一真值：32 = ERROR_SHARING_VIOLATION、
/// 33 = ERROR_LOCK_VIOLATION，且**文件锁不是共享断开**）：
///   · 能以"只读 + 允许他人读写删除"打开 ⇒ <see cref="LockState.Readable"/>（可迁移）
///   · 打开被共享冲突拒绝（32/33）    ⇒ <see cref="LockState.ExclusivelyLocked"/>（会被跳过/失败）
///   · 权限不足                       ⇒ <see cref="LockState.AccessDenied"/>（是另一件事，分开报）
///   · 文件不存在 / 其他 I/O          ⇒ <see cref="LockState.Missing"/> / <see cref="LockState.Error"/>
/// </summary>
public static class LockProbe
{
    public enum LockState
    {
        /// <summary>能打开（含以共享方式被别人打开）⇒ 可以迁移。</summary>
        Readable,
        /// <summary>被独占 ⇒ 打开被 Win32 32/33 拒绝。</summary>
        ExclusivelyLocked,
        /// <summary>权限不足（不是"被占用"，不得混为一谈）。</summary>
        AccessDenied,
        /// <summary>文件不存在。</summary>
        Missing,
        /// <summary>其他 I/O 错误（网络中断等）。</summary>
        Error,
    }

    public readonly record struct Result(LockState State, int Win32Code, string Evidence)
    {
        public bool IsReadable => State == LockState.Readable;
        public bool IsLocked => State == LockState.ExclusivelyLocked;
    }

    /// <summary>探测一个文件当前能否被读取（不改变任何共享语义：只读打开 + 最宽共享）。</summary>
    public static Result Probe(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            return new Result(LockState.Readable, 0, "只读打开成功（允许他人读写删除）");
        }
        catch (FileNotFoundException)
        {
            return new Result(LockState.Missing, 2, "文件不存在（ERROR_FILE_NOT_FOUND）");
        }
        catch (DirectoryNotFoundException)
        {
            return new Result(LockState.Missing, 3, "路径不存在（ERROR_PATH_NOT_FOUND）");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new Result(LockState.AccessDenied, Win32Of(ex), "访问被拒绝（权限不足，不是被占用）");
        }
        catch (IOException ex)
        {
            var code = Win32Of(ex);
            if (ShareLossSignal.IsFileLockWin32(code))
                return new Result(LockState.ExclusivelyLocked, code,
                    code == 32
                        ? "被其他程序独占打开（Win32 32 ERROR_SHARING_VIOLATION）"
                        : "文件区域被锁定（Win32 33 ERROR_LOCK_VIOLATION）");
            return new Result(LockState.Error, code, "I/O 错误：" + ex.Message);
        }
        catch (Exception ex)
        {
            return new Result(LockState.Error, Win32Of(ex), "探测失败：" + ex.Message);
        }
    }

    /// <summary>从异常里取 Win32 错误码（HResult 低 16 位；.NET 的 IOException.HResult 就是 0x8007xxxx 形式）。</summary>
    public static int Win32Of(Exception ex) => ex.HResult & 0xFFFF;
}