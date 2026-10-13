using System;
using System.IO;
using System.Linq;
using PCMig.Core.Logging;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ P1-10 ★ 安装器脚本与测量工具的可核查契约。
///
/// 交接口径是"安装器双击→欢迎页与首启慢（**无分段证据**）"。这里的断言只钉两件容易出人命的事：
///   ① 候选包**绝不能覆盖正式发布件**（dist\PCMigSetup-&lt;版本&gt;.exe）；
///   ② 分段日志只在测量开关打开时编译进来（正式发布包的安装行为逐位不变）。
/// 另外钉住"测量一律隔离"，防止以后有人拿真实用户的 %ProgramData%\PCMig 做试验。
/// </summary>
public sealed class P1InstallerScriptTests
{
    private static string Iss => ReadRepoFile("installer", "pcmig.iss");
    private static string TimingTool => ReadRepoFile("tools", "startup-timing.ps1");

    // ── ① 候选包不得覆盖正式发布件 ─────────────────────────────────────────────
    [Fact]
    public void P1_10_候选包输出名必须可加后缀且默认仍是正式发布名()
    {
        var iss = Iss;
        Assert.Contains("#ifndef SetupSuffix", iss);
        Assert.Contains("#define SetupSuffix \"\"", iss);
        Assert.Contains("OutputBaseFilename=PCMigSetup-{#MyAppVersion}{#SetupSuffix}", iss);
        // 默认（不加开关）时必须仍然是发布名本身，绝不能变成别的名字。
        Assert.DoesNotContain("OutputBaseFilename=PCMigSetup-candidate", iss);
    }

    // ── ② 分段日志必须在开关之内 ───────────────────────────────────────────────
    [Fact]
    public void P1_10_分段日志只在测量开关打开时才编译进来()
    {
        var iss = Iss;
        Assert.Contains("[Code]", iss);
        Assert.Contains("#ifdef SetupStageLog", iss);
        Assert.Contains("#endif", iss);
        Assert.Contains("SetupLogging=yes", iss);

        // [Setup] 里的开关必须就地包住 SetupLogging。
        var setupGuard = iss.IndexOf("#ifdef SetupStageLog", StringComparison.Ordinal);
        var setupGuardEnd = iss.IndexOf("#endif", setupGuard, StringComparison.Ordinal);
        var logging = iss.IndexOf("SetupLogging=yes", StringComparison.Ordinal);
        Assert.True(setupGuard > 0 && logging > setupGuard && logging < setupGuardEnd,
            "SetupLogging 应当被 [Setup] 里的 #ifdef SetupStageLog 包住");

        // [Code] 里的开关必须包住三个挂钩点。
        var code = iss.IndexOf("[Code]", StringComparison.Ordinal);
        var guardStart = iss.IndexOf("#ifdef SetupStageLog", code, StringComparison.Ordinal);
        var guardEnd = iss.IndexOf("#endif", guardStart, StringComparison.Ordinal);
        Assert.True(code > 0 && guardStart > code && guardEnd > guardStart, "找不到 [Code] 的 SetupStageLog 保护段");

        foreach (var hook in new[] { "function InitializeSetup(): Boolean;", "procedure InitializeWizard();", "procedure CurPageChanged(CurPageID: Integer);" })
        {
            var at = iss.IndexOf(hook, StringComparison.Ordinal);
            Assert.True(at > guardStart && at < guardEnd, $"{hook} 必须在 [Code] 的保护段内");
        }

        // 分段日志的字面标记：报告里要能直接 grep 出来。
        Assert.Contains("PCMIG-STAGE setup-init", iss);
        Assert.Contains("PCMIG-STAGE wizard-init", iss);
        Assert.Contains("PCMIG-STAGE page-", iss);
    }

    // ── ③ 原有安装行为不得被这次改动弄丢 ───────────────────────────────────────
    [Fact]
    public void P1_10_安装器的既有条目仍在()
    {
        var iss = Iss;
        Assert.Contains("AppId={{3B8E5A42-7C1F-4E9A-A2D1-9C0F5E7B3D61}", iss);
        Assert.Contains("PrivilegesRequired=admin", iss);
        Assert.Contains("[Run]", iss);
        Assert.Contains("Filename: \"{app}\\PCMig.WinUI.exe\"; Description:", iss);
        Assert.Contains("postinstall nowait skipifsilent unchecked", iss);
        Assert.Contains("Source: \"..\\dist\\app\\*\"; DestDir: \"{app}\"; Flags: recursesubdirs ignoreversion", iss);
    }

    // ── ④ 测量必须隔离，且真的读了应用自己写的分段 ─────────────────────────────
    [Fact]
    public void P1_10_计时工具必须隔离且读取真实分段证据()
    {
        var tool = TimingTool;
        foreach (var knob in new[] { "PCMIG_JOBS", "PCMIG_STARTUP_DIR", "PCMIG_LOG_DIR" })
        {
            Assert.Contains(knob, tool);
        }
        Assert.Contains("startup-timeline.jsonl", tool);
        Assert.Contains("phase-summary", tool);
        Assert.Contains("PCMIG-STAGE", tool);
        // 安装器模式必须只到欢迎页就结束进程：脚本必须真的 Stop-Process。
        Assert.Contains("Stop-Process", tool);
        // 冷/热口径必须写出来（不得含糊其辞）。
        Assert.Contains("OS 待机缓存", tool);
    }

    // ── ⑤ 日志目录的测量旋钮 ───────────────────────────────────────────────────
    [Fact]
    public void P1_10_日志目录可被测量旋钮覆盖且未设置时仍是生产路径()
    {
        var previous = Environment.GetEnvironmentVariable("PCMIG_LOG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("PCMIG_LOG_DIR", null);
            var production = LogBootstrap.AppLogDir;
            Assert.EndsWith(Path.Combine("PCMig", "Logs"), production);
            Assert.Contains("ProgramData", production);

            var isolated = Path.Combine(Path.GetTempPath(), "pcmig-logdir-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("PCMIG_LOG_DIR", isolated);
            Assert.Equal(isolated, LogBootstrap.AppLogDir);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PCMIG_LOG_DIR", previous);
        }
    }

    // ── 辅助 ────────────────────────────────────────────────────────────────────
    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln")))
            {
                var path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
                Assert.True(File.Exists(path), "缺少文件：" + path);
                return File.ReadAllText(path);
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到仓库根");
    }
}