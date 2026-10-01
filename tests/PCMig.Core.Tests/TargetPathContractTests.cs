using PCMig.Core.Preflight;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// 目标位置契约回归（UNC 目标的统一行为）。
///
/// 【背景 — 2026-09-19 取证结论】
/// 产品契约：本工具是"在新电脑上运行、把数据写到**本机磁盘**"的直拉模式，
/// CLI 帮助与 README 中所有 --target 示例均为本地盘符（D:\ 等），**从未出现 UNC 目标**；
/// GUI 的目标选择器是 FolderBrowserDialog（选不到 UNC）。→ UNC 目标**不在契约内**。
///
/// 修复前的问题：PreflightChecker 对 UNC 目标会走到 `new DriveInfo("\\\\server\\share")`，
/// 抛出的 ArgumentException 被 catch 后**直接把异常文案当检查详情**
/// （"Drive name must be a root directory (i.e. 'C:\') or a drive letter ('C')"）——
/// 用户看不懂，也看不出"UNC 不被支持"。
///
/// 本轮修复：catch 内显式识别 UNC，给出一条**面向用户、说明原因与替代做法**的 Error 详情。
/// 由于 CLI 与 GUI 共用同一个 PreflightChecker，两者拿到的检查项与 OverallPass 天然一致。
///
/// 注：CLI 与 GUI 都依据 `report.OverallPass` 阻断
/// （CLI Program.cs:212/332；GUI MainViewModel.cs:763）——本组用例保护的是这条契约的消息可读性。
/// </summary>
public class TargetPathContractTests
{
    /// <summary>
    /// [运行级别：L0 纯逻辑]
    /// UNC 路径必须被识别（含前导空格的情况，因为调用点做过 Trim）。
    /// </summary>
    [Theory]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\192.168.1.10\D$")]
    [InlineData(@"\\server\share\sub\dir")]
    [InlineData(@"  \\server\share  ")]
    public void IsUncTarget_DetectsUncPaths(string path)
        => Assert.True(PreflightChecker.IsUncTarget(path));

    /// <summary>
    /// [运行级别：L0 纯逻辑]
    /// 本地盘符路径与空值绝不能被误判为 UNC（否则会误阻断合法目标）。
    /// </summary>
    [Theory]
    [InlineData(@"D:\")]
    [InlineData(@"D:\迁移目标")]
    [InlineData(@"C:\Users\someone\Migrated")]
    [InlineData(@"D:/forward/slash")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsUncTarget_RejectsLocalAndEmptyPaths(string? path)
        => Assert.False(PreflightChecker.IsUncTarget(path));

    /// <summary>
    /// [运行级别：L0 纯逻辑]
    /// 单反斜杠开头（如 "\foo"）不是 UNC，仅是相对/异常路径 —— 不得误判。
    /// </summary>
    [Fact]
    public void IsUncTarget_SingleBackslashIsNotUnc()
        => Assert.False(PreflightChecker.IsUncTarget(@"\foo\bar"));
}