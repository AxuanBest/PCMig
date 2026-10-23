using System;
using System.IO;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ 2026-10-14（v0.5.4 Preview.1 回归修复）★ 动效闸门 <c>MotionDirector.SystemAnimationsEnabled</c> 契约。
///
/// 缺陷现象：**所有页面/浮层过渡动画全部消失**。
/// 现场证据（RDP 会话，`PCMIG_FLUIDZOOM_DIAG=1` → `%TEMP%\pcmig-fluidzoom-diag.log`）：
///   `08:25:02.473 gen0 settled=True StartTransition? opening=True panel=ChangelogPanel source=Button anim=False`
/// 即过渡根本没开始 —— 闸门判了"系统不允许动画"。
///
/// 根因（回归）：上一轮把这里从"**只读一次并缓存**"改成"**每次调用都读**"，
/// 而 `Windows.UI.ViewManagement.UISettings.AnimationsEnabled` 在**远程会话（RDP/RemoteApp）里恒为 false**
/// —— 那不是用户在本机做的无障碍选择，是会话属性；于是每一次过渡都被拒掉，整个动效层消失
/// （v0.5.3 稳定版在同样环境下是播放动画的：它只读一次并缓存）。
///
/// 为什么测试是"源码文本契约"而不是行为断言：`MotionDirector` 是 WinUI 侧 internal 静态类，
/// 依赖 `Microsoft.UI.Composition` / `DispatcherQueue` / `user32!GetSystemMetrics`，无法在零 WinUI 依赖的
/// Core.Tests 里实例化（与本仓库既有的 `SourceConnectionStateTests` / `P1InstallerScriptTests` 同一套路）。
/// **闸门本身的运行期行为由 `anim=False` → `anim=True` 的现场探针直接验证**，本文件锁的是"不许再退回每次读取"。
/// </summary>
public class P1MotionAvailabilityTests
{
    // ── 必须成立的四道判据（顺序即优先级） ────────────────────────────────────────

    [Fact]
    public void T1_SystemPreferenceIsSettledOnceAndCached_NeverReReadOnEveryCall()
    {
        var src = ReadMotionDirector();

        Assert.Contains("private static bool? _animationsEnabled;", src);
        Assert.Contains("if (_animationsEnabled is { } settled) return settled;", src);
        Assert.Contains("_animationsEnabled = value;", src);

        // ★ 反面断言（本次回归的原样写法）★：绝不可以在 getter 里直接把系统偏好 return 出去
        Assert.DoesNotContain("return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;", src);
    }

    [Fact]
    public void T2_RemoteSessionFalseIsNotTrusted_SoAnimationsSurviveRdp()
    {
        var src = ReadMotionDirector();

        Assert.Contains("if (!value && IsRemoteSession()) return true;", src);   // 回归修复点
        Assert.Contains("private const int SmRemoteSession = 0x1000;", src);
        Assert.Contains("GetSystemMetrics(SmRemoteSession)", src);
    }

    [Fact]
    public void T3_FalseReadBeforeShellIsInteractiveIsTreatedAsStartupTransient()
    {
        var src = ReadMotionDirector();
        var app = ReadRepoFile("src", "PCMig.WinUI", "App.xaml.cs");

        Assert.Contains("internal static void MarkShellInteractive() => _shellInteractive = true;", src);
        Assert.Contains("if (!value && !_shellInteractive) return true;", src);

        // App 必须在窗口激活之后才置位；顺序反了就等于没修
        var activated = app.IndexOf("StartupTimeline.Mark(\"window-activated\");", StringComparison.Ordinal);
        var marked = app.IndexOf("MotionDirector.MarkShellInteractive();", StringComparison.Ordinal);
        Assert.True(activated >= 0, "App.xaml.cs 必须仍然标记 window-activated");
        Assert.True(marked > activated, "MarkShellInteractive() 必须出现在 window-activated 之后");
    }

    [Fact]
    public void T4_OverrideNonUiThreadAndFailureAllFallBackToAnimating()
    {
        var src = ReadMotionDirector();

        Assert.Contains("PCMIG_FORCE_MOTION", src);                                    // 本机视觉验收的显式覆写
        Assert.Contains("DispatcherQueue.GetForCurrentThread() is null", src);         // 非 UI 线程不下结论
        Assert.Contains("UISettings unavailable, assuming animations on", src);        // 取不到 ⇒ 按允许，且不缓存
    }

    [Fact]
    public void T5_OnlyUiThreadReadAfterActivationOnLocalSessionCanDisableMotion()
    {
        var src = ReadMotionDirector();

        // 四道护栏全部要在"缓存 false"之前生效：只要有一条能救，就不许关掉动画
        var remoteGuard = src.IndexOf("if (!value && IsRemoteSession()) return true;", StringComparison.Ordinal);
        var startupGuard = src.IndexOf("if (!value && !_shellInteractive) return true;", StringComparison.Ordinal);
        var cache = src.IndexOf("_animationsEnabled = value;", StringComparison.Ordinal);

        Assert.True(remoteGuard >= 0 && startupGuard >= 0 && cache >= 0, "三道判据必须同时存在");
        Assert.True(remoteGuard < cache, "远程会话护栏必须在缓存之前");
        Assert.True(startupGuard < cache, "启动期护栏必须在缓存之前");
    }

    // ── 脚手架 ────────────────────────────────────────────────────────────────

    private static string ReadMotionDirector()
        => ReadRepoFile("src", "PCMig.WinUI", "Presentation", "MotionDirector.cs");

    private static string ReadRepoFile(params string[] parts)
    {
        var path = Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"找不到仓库文件：{path}");
        return File.ReadAllText(path);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到仓库根（向上找不到 PCMig.sln）");
    }
}
