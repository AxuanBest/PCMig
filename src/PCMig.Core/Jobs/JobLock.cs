namespace PCMig.Core.Jobs;

/// <summary>
/// Job 级进程锁：运行/恢复前必须先拿到。
/// 实现：独占打开 job.lock（FileShare.None）。OS 保证进程死亡自动释放——
/// 因此崩溃留下的锁文件不构成死锁，下次启动直接获得。
/// </summary>
public sealed class JobLock : IDisposable
{
    private readonly FileStream _fs;
    private JobLock(FileStream fs) { _fs = fs; }

    public static JobLock? TryAcquire(JobContext ctx, out string reason)
        => TryAcquire(ctx, TimeSpan.Zero, out reason);

    /// <summary>
    /// 带等待窗口的抢锁：暂停/停止后的进程退出有收尾窗口（写回执、释放句柄），
    /// 此刻 resume 会撞上锁——公司环境实测踩中。宽限 waitWindow 秒内重试，超时才报错。
    /// </summary>
    public static JobLock? TryAcquire(JobContext ctx, TimeSpan waitWindow, out string reason)
    {
        var path = Path.Combine(ctx.JobDir, "job.lock");
        var deadline = DateTime.UtcNow + waitWindow;
        while (true)
        {
            try
            {
                var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                fs.SetLength(0);
                var pid = System.Text.Encoding.ASCII.GetBytes(Environment.ProcessId.ToString());
                fs.Write(pid);
                fs.Flush();
                reason = "";
                return new JobLock(fs);
            }
            catch (IOException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    reason = $"Job {ctx.JobId} 正被另一个进程持有（lock 文件占用，已等待 {(int)waitWindow.TotalSeconds} 秒）。若确认无进程在跑，可直接删除 {path}";
                    return null;
                }
                Thread.Sleep(500);
            }
            catch (Exception ex)
            {
                reason = $"获取 Job 锁失败: {ex.Message}";
                return null;
            }
        }
    }

    public void Dispose()
    {
        try { _fs.Dispose(); } catch { /* ignore */ }
    }
}
