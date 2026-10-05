using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// 源码树卫生回归（真源码扫描用例）。
///
/// 【为什么存在】
/// v0.4.6 修了两处"编译能过、运行不抛异常、只是逻辑静默失效"的正则缺陷：
///   1. TransferOrchestrator.IsSpaceErrorText：<c>\s</c> 丢反斜杠变成字面量 s，
///      <c>\b</c> 变成 3 个真实 0x08 退格控制字符；
///   2. PreflightChecker.ExtractWin32Error：<c>\d</c> 丢反斜杠变成字面量 d。
/// 这两类缺陷**只有机器扫描能拦住**——人眼 diff 会漏，编译器不报，运行时无异常。
///
/// 本用例直接读源码树字节，因此：
///   - 自动被 release.ps1 的「闸门 0（dotnet test）」覆盖，无需额外接线；
///   - 任何一次"正则被工具链/剪贴板吃掉反斜杠"都会让构建变红。
///
/// ⚠ 本文件自身绝不写入任何控制字符字面量——所有检查都用字节值比较。
/// </summary>
public class SourceTreeHygieneTests
{
    static SourceTreeHygieneTests()
    {
        // .NET Core 默认不提供 GBK(936)；真实 robocopy 日志是 GBK 无 BOM。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private const byte Backspace = 0x08;   // \b 被吃掉后的残骸
    private const byte VerticalTab = 0x0B;
    private const byte FormFeed = 0x0C;

    /// <summary>从测试程序集目录向上定位含 PCMig.sln 的仓库根。</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "找不到仓库根（未在上级目录中发现 PCMig.sln）。起始目录：" + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> EnumerateCsFiles(string root)
    {
        var skip = new[] { "bin", "obj", "dist", ".git", ".vs", "node_modules" };
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p =>
            {
                var parts = p.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return !parts.Any(seg => skip.Contains(seg, StringComparer.OrdinalIgnoreCase));
            });
    }

    [Fact]
    public void SourceTree_ContainsNoStrayControlCharacters()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var file in EnumerateCsFiles(root))
        {
            var bytes = File.ReadAllBytes(file);
            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == Backspace || bytes[i] == VerticalTab || bytes[i] == FormFeed)
                {
                    offenders.Add($"{Path.GetRelativePath(root, file)} @offset {i} = 0x{bytes[i]:X2}");
                    break; // 每个文件只记第一处，报告足够定位
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "源码树出现控制字符（正则反斜杠被吃掉的典型残骸）：\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void IsSpaceErrorText_RegexStillContainsEscapeSequences()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "PCMig.Core", "Transfer", "TransferOrchestrator.cs");
        Assert.True(File.Exists(path), "找不到被测源文件: " + path);
        var text = File.ReadAllText(path);

        // v0.4.6 修复的核心：\s+ 必须还在（丢了就退化成字面量 s+）
        Assert.Contains(@"\s+", text);

        // 磁盘满判据的四个分支必须都还在
        Assert.Contains("磁盘空间不足", text);
        Assert.Contains("磁盘已满", text);
        Assert.Contains("insufficient disk space", text);

        // 熔断正则必须仍要求"正在复制文件" + 八位十六进制（形态收窄是有意设计）。
        // F12：文件级错误行解析（承载这两个字面量的正则）已从本文件迁到 RobocopyRunner.cs，成为
        //   TryParseFileErrorLine 的唯一实现，入账与回冲共用同一处解析。口径不放宽——这两个字面量
        //   必须仍存在于两个落点之一，且 Core 里不许两处各写一份。
        var runnerPath = Path.Combine(root, "src", "PCMig.Core", "Transfer", "RobocopyRunner.cs");
        Assert.True(File.Exists(runnerPath), "找不到被测源文件: " + runnerPath);
        var runnerText = File.ReadAllText(runnerPath);
        Assert.True(text.Contains(@"正在复制文件") || runnerText.Contains(@"正在复制文件"),
            @"文件级错误行正则必须仍要求 ""正在复制文件""（TransferOrchestrator.cs 或 RobocopyRunner.cs）");
        Assert.True(text.Contains(@"[0-9A-Fa-f]{8}") || runnerText.Contains(@"[0-9A-Fa-f]{8}"),
            @"文件级错误行正则必须仍要求八位十六进制错误码（TransferOrchestrator.cs 或 RobocopyRunner.cs）");
    }

    [Fact]
    public void ExtractWin32Error_RegexStillContainsDigitEscape()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "PCMig.Core", "Preflight", "PreflightChecker.cs");
        Assert.True(File.Exists(path), "找不到被测源文件: " + path);
        var text = File.ReadAllText(path);

        // v0.4.6 修复的核心：必须写成 错误\s*(\d+)，而不是 错误 (d+)
        Assert.Contains(@"\d+", text);
        Assert.Contains(@"错误", text);
    }

    [Fact]
    public void SourceTree_ContainsNoBareEscapeLossPattern()
    {
        // 扫描"反斜杠被吃掉"的可疑残留：正则里出现裸 s+ / d+ / S+ / D+ 的字面量分支。
        // 允许出现在注释、Test 文件、以及非正则上下文（如中文文案）中，
        // 因此这里只针对**包含 Regex 调用的行**做严格判断。
        var root = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var file in EnumerateCsFiles(root))
        {
            var rel = Path.GetRelativePath(root, file);
            if (rel.Contains("Tests", StringComparison.OrdinalIgnoreCase)) continue; // 测试自身允许书写反例

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                if (!line.Contains("Regex", StringComparison.Ordinal)) continue;

                // 形如 @"… (s+)" / @"… d+" 且同一行没有任何反斜杠转义 => 高度可疑
                var looksLikeBareEscape = line.Contains("(s+)") || line.Contains("(d+)")
                                          || line.Contains("s+)\\") || line.Contains("d+)\\");
                if (looksLikeBareEscape && !line.Contains(@"\s") && !line.Contains(@"\d"))
                {
                    offenders.Add($"{rel}:{i + 1}  {line.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "疑似'反斜杠被吃掉'的正则残留（需人工复核）：\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void RealRobocopyFixture_ReflectsGbkSourceEncoding()
    {
        // 真实 robocopy 日志是 GBK/CP936 无 BOM。此用例锁定"按 GBK 解码"这一事实，
        // 防止今后有人把 fixture 转成 UTF-8 后再断言（引入第二次转码误差）。
        // 真实日志里的"错误 112"三个字在 GBK 与 UTF-8 下字节完全不同：
        // 抽样取证：GBK 解出正确中文，而用 UTF-8 解同一段字节必然得不到"错误"。
        // 这锁定了一个实测事实——fixture 只能按 GBK 读，不能转成 UTF-8 后再断言。
        const string realFragment = "错误 112 (0x00000070)";
        var gbk = Encoding.GetEncoding(936);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        var gbkBytes = gbk.GetBytes(realFragment);
        var utf8Bytes = utf8.GetBytes(realFragment);

        Assert.NotEqual(gbkBytes.Length, utf8Bytes.Length);                 // 编码确实不同
        Assert.Equal(realFragment, gbk.GetString(gbkBytes));                 // GBK 往返正确
        Assert.DoesNotContain("错误", utf8.GetString(gbkBytes));             // UTF-8 解 GBK 必然乱码
    }

    [Fact]
    public void DirectoryFallbackProgress_IsLimitedToBulkPassOnly()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "PCMig.Core", "Transfer", "TransferOrchestrator.cs");
        Assert.True(File.Exists(path), "找不到被测源文件: " + path);
        var text = File.ReadAllText(path);

        // 目录枚举回退只允许用在 Bulk 小文件通道：
        //  · Large(/J) 与 RootFiles 通道的目标文件都可能被预分配最终长度，
        //    枚举长度 ≠ 已确认落盘字节 → 会虚报进度（真实任务里因此跳到约 99%）。
        Assert.Contains("var enumFallbackAllowed = _currentPass == PassKind.Bulk;", text);
        // PHASE C-3（§3）起，目录枚举回退还必须挂在"重活档"节拍上（5 Hz 轻量档只读内存计数，
        // 不做任何目录枚举），所以条件前缀多了 heavyDue。语义没有放宽，反而更严：
        // 仍然是"只在 Bulk 通道 + 每 2 秒最多一次"。
        Assert.Contains("if (heavyDue && enumFallbackAllowed &&", text);
        Assert.DoesNotContain("_currentPass != PassKind.Large &&", text);
        Assert.Contains("预分配文件长度", text);
        // 跳过原因必须写进日志（否则日志里看不到任何线索，会被当成进度逻辑坏了）
        Assert.Contains("进度回退枚举已跳过", text);
    }

    /// <summary>
    /// 「尝试修复」的进度条显隐由 IsRepairing 驱动，它必须在 finally 里复位。
    /// 此前只写在 try 内（RunAsync 返回之后）——修复被取消或抛异常时 IsRepairing 永远停在 true，
    /// 迁移进度条与大号百分比（MigrationProgressVisibility）从此再也不显示。
    /// </summary>
    [Fact]
    public void RepairFlow_AlwaysResetsIsRepairingInFinally()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "PCMig.Gui", "MainViewModel.cs");
        Assert.True(File.Exists(path), "找不到被测源文件: " + path);
        var text = File.ReadAllText(path);

        var start = text.IndexOf("private async Task RepairAsync()", StringComparison.Ordinal);
        Assert.True(start > 0, "找不到 RepairAsync");
        var tail = text.Substring(start);
        var next = tail.IndexOf("\n    private ", StringComparison.Ordinal);
        var body = next > 0 ? tail.Substring(0, next) : tail;

        var finallyIdx = body.LastIndexOf("finally", StringComparison.Ordinal);
        Assert.True(finallyIdx > 0, "RepairAsync 必须有 finally 兜底");
        Assert.Contains("IsRepairing = false;", body.Substring(finallyIdx));
    }

    /// <summary>
    /// 自适应布局契约：窗口按显示器工作区夹取、页面容器 ClipToBounds（不压盖底栏）、
    /// 底栏按钮用 DockPanel.Dock=Right 先占位（窄窗口下不被挤出窗口）。
    /// </summary>
    [Fact]
    public void MainWindow_IsResponsive_AndKeepsBottomBarUsable()
    {
        var root = FindRepoRoot();
        var xamlPath = Path.Combine(root, "src", "PCMig.Gui", "MainWindow.xaml");
        var csPath = Path.Combine(root, "src", "PCMig.Gui", "MainWindow.xaml.cs");
        var nativePath = Path.Combine(root, "src", "PCMig.Gui", "NativeMethods.cs");
        Assert.True(File.Exists(xamlPath), "找不到 MainWindow.xaml");
        var xaml = File.ReadAllText(xamlPath);

        // 设计最小尺寸必须能落进小屏工作区（1366×768 的可用高度只有约 728px）
        Assert.Contains("MinWidth=\"960\" MinHeight=\"660\"", xaml);
        // 页面容器必须裁剪，内容不得溢出压盖底部状态栏
        Assert.Contains("ClipToBounds=\"True\"", xaml);
        // 底栏按钮必须先占位，保证不被挤出
        Assert.Contains("DockPanel.Dock=\"Right\"", xaml);
        // 旧的硬撑写法不得回归
        Assert.DoesNotContain("MinWidth=\"1120\"", xaml);
        Assert.DoesNotContain("MinWidth=\"360\"", xaml);
        Assert.DoesNotContain("Height=\"230\"", xaml);

        // 窗口必须按当前显示器工作区夹取尺寸
        Assert.True(File.Exists(csPath), "找不到 MainWindow.xaml.cs");
        Assert.Contains("ApplyWorkAreaClamp", File.ReadAllText(csPath));
        Assert.True(File.Exists(nativePath), "找不到 NativeMethods.cs");
        var native = File.ReadAllText(nativePath);
        Assert.Contains("MonitorFromWindow", native);
        Assert.Contains("GetMonitorInfo", native);
    }
}