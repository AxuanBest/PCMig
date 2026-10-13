using System;
using System.Diagnostics;
using System.IO;
using PCMig.Core.Native;
using Serilog;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ P0-06 ★ 源身份指纹探测**必须有界**。
///
/// 缺陷回顾（日志取证 + 本机实测）：<c>SourceIdentity.Capture</c> 里的 CreateFileW 与
/// srvsvc RPC 都是不可取消的阻塞调用；当共享被撤销 / 主机离线时，**每个对象实测阻塞约 16s**
/// （一个 15 对象的计划 ⇒ 4 分钟界面卡死、停止按钮点不动）。这不是"网络慢"，
/// 而是产品把"探测"做成了不可中断的等待。
///
/// 本文件锁死三件事：
///   ① 死主机 / 已撤销共享：必须在预算内返回 **null（本次不判定）**，绝不返回猜测值；
///   ② 同一死主机的重复探测必须走短缓存，不再一个个等（否则多对象计划仍然是分钟级卡顿）；
///   ③ 本地路径不受影响，正常取到指纹（"不判定"只针对真的探不到的情况）。
/// </summary>
public class P0ProbeBudgetTests : IDisposable
{
    private static readonly ILogger Log = new LoggerConfiguration().CreateLogger();
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "pcmig-probe-" + Guid.NewGuid().ToString("N"));

    public P0ProbeBudgetTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 清理失败不影响结论 */ }
    }

    [Fact]
    public void P0_06_死主机指纹探测必须在预算内返回不判定_而不是阻塞十几秒()
    {
        // 不存在的 UNC 主机（现场对应"共享被撤销 / 旧电脑关机"）。
        const string dead = @"\\SRC-PC-ABSENT\d970";

        var sw = Stopwatch.StartNew();
        var first = SourceIdentity.Capture(dead, Log);
        sw.Stop();

        Assert.Null(first);                                     // 取不到 ⇒ 不判定，绝不瞎猜
        Assert.True(sw.ElapsedMilliseconds < 4000,
            $"指纹探测耗时 {sw.ElapsedMilliseconds}ms，超过 4s 上限——预算保护没有生效");
    }

    [Fact]
    public void P0_06_同一死主机的重复探测必须走短缓存()
    {
        const string dead = @"\\SRC-PC-ABSENT\d971";

        _ = SourceIdentity.Capture(dead, Log);                  // 第一次：最多等一个探测预算

        var sw = Stopwatch.StartNew();
        var second = SourceIdentity.Capture(dead, Log);
        sw.Stop();

        Assert.Null(second);
        Assert.True(sw.ElapsedMilliseconds < 600,
            $"重复探测耗时 {sw.ElapsedMilliseconds}ms，说明死主机结论没有被缓存（多对象计划仍会逐项卡顿）");
    }

    [Fact]
    public void P0_06_本地路径不受预算保护影响_仍取到指纹()
    {
        var sw = Stopwatch.StartNew();
        var id = SourceIdentity.Capture(_dir, Log);
        sw.Stop();

        Assert.NotNull(id);                                     // 本地必然取得到 fsid
        Assert.StartsWith("fsid:", id);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"本地指纹耗时 {sw.ElapsedMilliseconds}ms，异常偏慢");
    }
}