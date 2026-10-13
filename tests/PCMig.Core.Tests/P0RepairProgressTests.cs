using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ P0-04 ★ 进度与结果的语义分离（回归闸门）。
///
/// 现场症状：「修复的进度跑到 100%，可最后验证还是失败」——用户据此以为已经修好了。
/// 根因不是算术错，而是**一个百分比在讲两件事**：它测的是"重拷干了多少活"，
/// 却被读成"这次修复成功了"。本闸门把纠正后的语义逐条钉死：
///
///   1) 修复轮（定向重拷）的收尾**不许**出现「迁移完成」这类结论；
///   2) 修复是否达成只由**复验结论**决定（<c>ctx.LoadVerify()</c> → OverallPass），
///      读不到结论一律按"未通过"处理（不许把"不知道"当成"修好了"）；
///   3) 未通过时进度**可以**仍是 100%（它只表示重拷完成度），但状态句必须明说未达成，
///      并把 ProgressNeedsAttention 置起，让页面/底栏把百分比转成警示态；
///   4) 终局结论一旦写出即**封版**：后到的过期快照不许再改显示位（"结果页已失败、底栏又跳回运行中"）。
///
/// 形态说明：WinUI 目标不能被 Core.Tests 引用，故沿用既有做法（P0SourceLampTests /
/// P0StopChainTests / P0CrashPathGuardTests）——直接对**源码契约**断言。
/// </summary>
public class P0RepairProgressTests
{
    // ── R1：收尾封版 ──────────────────────────────────────────────────────────────
    [Fact]
    public void R1_终局结论写出后必须封版_且后到的快照被拒绝并留证据()
    {
        var finish = MethodBody("src/PCMig.WinUI/Presentation/MigrationSessionViewModel.cs", "FinishRunAsync");
        Assert.Contains("_progressSealed = true;", finish);

        // 封版必须发生在"终局写入"之后：Percent / DataLabel / 阶段句都已在它之前完成。
        var idxSeal = finish.IndexOf("_progressSealed = true;", StringComparison.Ordinal);
        Assert.True(idxSeal > finish.IndexOf("DataLabel = isRepairRun", StringComparison.Ordinal),
            "封版必须写在终局显示位（DataLabel）之后，否则终局结论自己就被挡掉了。");
        Assert.True(idxSeal > finish.IndexOf("SetOperational(phase switch", StringComparison.Ordinal),
            "封版必须写在阶段结论句之后。");

        var apply = MethodBody("src/PCMig.WinUI/Presentation/MigrationSessionViewModel.cs", "ApplySnapshot");
        Assert.Contains("if (_progressSealed)", apply);
        Assert.Contains("_postFinishSnapshotsIgnored++;", apply);
        // 拒绝本身也要有证据（不许静默丢弃）：必须留一条日志说明"这份快照被忽略了"。
        Assert.Contains("收尾后忽略过期进度快照", apply);
        Assert.True(apply.IndexOf("return;", StringComparison.Ordinal) > apply.IndexOf("_postFinishSnapshotsIgnored++", StringComparison.Ordinal),
            "守卫必须在拒绝后立刻 return，否则过期快照仍会改写显示位。");
    }

    [Fact]
    public void R2_封版只在新的运行或换任务时解除()
    {
        var reset = MethodBody("src/PCMig.WinUI/Presentation/MigrationSessionViewModel.cs", "ResetRunProjection");
        Assert.Contains("_progressSealed = false;", reset);
        Assert.Contains("ProgressNeedsAttention = false;", reset);
        Assert.Contains("_postFinishSnapshotsIgnored = 0;", reset);
    }

    // ── R3：修复轮的收尾措辞 ──────────────────────────────────────────────────────
    [Fact]
    public void R3_修复轮不得出现迁移完成的结论_且标签写明它量的是重拷()
    {
        var finish = MethodBody("src/PCMig.WinUI/Presentation/MigrationSessionViewModel.cs", "FinishRunAsync");

        // 修复轮：标签必须写清"这是重拷，不是迁移完成"，并且复验前不许给结论。
        Assert.Contains("修复重拷 / 计划（待复验）", finish);
        Assert.Contains("修复重拷 / 计划（复验通过）", finish);
        Assert.Contains("修复重拷 / 计划（复验未通过）", finish);

        // 修复轮的 Completed 分支必须与"✔ 迁移完成！"分开，且自己不含"迁移完成"字样。
        var armStart = finish.IndexOf("JobPhase.Completed when isRepairRun", StringComparison.Ordinal);
        Assert.True(armStart >= 0, "修复轮的 Completed 收尾分支必须存在（否则会落到'迁移完成'那句上）。");
        var armEnd = finish.IndexOf("JobPhase.CompletedWithErrors when isRepairRun", armStart, StringComparison.Ordinal);
        Assert.True(armEnd > armStart, "修复轮的 CompletedWithErrors 分支必须存在。");
        var repairCompletedArm = finish.Substring(armStart, armEnd - armStart);
        Assert.DoesNotContain("迁移完成", repairCompletedArm);
        Assert.Contains("重拷完成 ≠ 修复成功", repairCompletedArm);

        // 失败路径也不许说"完整完成"。
        var nextArm = finish.IndexOf("JobPhase.Completed =>", armEnd, StringComparison.Ordinal);
        var repairErrorArm = finish.Substring(armEnd, nextArm - armEnd);
        Assert.Contains("修复尚未达成", repairErrorArm);
        Assert.DoesNotContain("迁移完成", repairErrorArm);
    }

    // ── R4：结论由复验决定 ────────────────────────────────────────────────────────
    [Fact]
    public void R4_修复是否达成由复验结论决定_不由重拷进度决定()
    {
        var finish = MethodBody("src/PCMig.WinUI/Presentation/MigrationSessionViewModel.cs", "FinishRunAsync");

        Assert.Contains("ctx.LoadVerify()", finish);
        Assert.Contains("repairVerify?.OverallPass == true", finish);
        Assert.Contains("ProgressNeedsAttention = !verifyPassed;", finish);

        // 通过：才允许把百分比写满，并保留 F2b 的"本次重拷范围"信息。
        Assert.Contains("修复完成（本次重拷 {repairScope}，进度 100%）", finish);

        // 未通过：必须明说未达成，并且明说 100% 只表示重拷完成度（这是现场误解的正中心）。
        Assert.Contains("本次修复未达成", finish);
        Assert.Contains("不代表一致性已达成", finish);
        Assert.Contains("重拷完成度", finish);
        // 未通过在日志里也必须留下"进度到 100% 不代表修好了"这条可检索的事实。
        Assert.Contains("进度条到 100% 不代表修好了", finish);
    }

    [Fact]
    public void R5_读不到复验报告时按未通过处理_不假装成功()
    {
        var finish = MethodBody("src/PCMig.WinUI/Presentation/MigrationSessionViewModel.cs", "FinishRunAsync");
        Assert.Contains("读取复验报告失败", finish);
        Assert.Contains("按未通过处理", finish);
        var idxCatch = finish.IndexOf("读取复验报告失败", StringComparison.Ordinal);
        var idxVerdict = finish.IndexOf("var verifyPassed =", StringComparison.Ordinal);
        Assert.True(idxVerdict > idxCatch, "判定必须排在「读报告失败」之后，否则异常路径会跳过按未通过处理。");
    }

    [Fact]
    public void R6_旧的修复收尾文案已退出_不得回退成只有重拷结束()
    {
        var finish = MethodBody("src/PCMig.WinUI/Presentation/MigrationSessionViewModel.cs", "FinishRunAsync");
        Assert.DoesNotContain("重拷结束（{phase}，本次修复重拷 {repairScope}）：{StatusMessage}", finish);
        Assert.Contains("if (isRepairRun)", finish);
        Assert.Contains("var isRepairRun = onlyObjectIds is { Count: > 0 };", finish);
    }

    [Fact]
    public void R7_ProgressNeedsAttention只有两个写入点_且都不改业务结论()
    {
        var vm = ReadRepoFile("src/PCMig.WinUI/Presentation/MigrationSessionViewModel.cs");
        var writes = System.Text.RegularExpressions.Regex.Matches(vm, @"ProgressNeedsAttention\s*=");
        Assert.True(writes.Count == 2,
            $"ProgressNeedsAttention 的赋值点应恰好 2 个（ResetRunProjection 清标志 + 复验判定写结论），实际 {writes.Count} 个——" +
            "多出来的赋值点意味着有人拿它当业务状态用。");
    }

    [Fact]
    public void R8_过期快照的拒绝路径只记录不判定()
    {
        var apply = MethodBody("src/PCMig.WinUI/Presentation/MigrationSessionViewModel.cs", "ApplySnapshot");
        // 只截取"封版守卫"这一段（从 if (_progressSealed) 到它自己的 return;）：
        // 守卫只许计数 + 写日志，不许顺手改业务阶段/进度结论（真正的应用逻辑在守卫之后）。
        var idx = apply.IndexOf("if (_progressSealed)", StringComparison.Ordinal);
        Assert.True(idx >= 0);
        var end = apply.IndexOf("return;", idx, StringComparison.Ordinal);
        Assert.True(end > idx);
        var guard = apply.Substring(idx, end - idx);

        Assert.Contains("_postFinishSnapshotsIgnored++", guard);
        Assert.Contains("_log.Debug", guard);
        Assert.DoesNotContain("Phase =", guard);
        Assert.DoesNotContain("IsFinished =", guard);
        Assert.DoesNotContain("Percent =", guard);
        Assert.DoesNotContain("DataLabel =", guard);
    }

    // ── R9：页面与底栏的醒目位 ────────────────────────────────────────────────────
    [Fact]
    public void R9_页面与底栏必须把未通过投影成警示态_且复位回样式原色()
    {
        var page = ReadRepoFile("src/PCMig.WinUI/Views/Step3ProgressPage.xaml.cs");
        Assert.Contains("private void ApplyAttentionVisual()", page);
        Assert.Contains("_session?.ProgressNeedsAttention == true", page);
        Assert.Contains("ImmersiveProgressState.Warning", page);
        Assert.Contains("WarningBrush", page);
        // 复位必须交回样式（ClearValue），页面不许自己记一套"正常色"。
        Assert.Contains("TotalPercentText.ClearValue(TextBlock.ForegroundProperty)", page);
        // 收尾后呈现节拍可能已停：PushState 里必须无条件投影一次。
        Assert.Contains("ApplyAttentionVisual();", page);
        Assert.Contains("UpdateTotalProgressFill();", page);

        var shell = ReadRepoFile("src/PCMig.WinUI/MainWindow.xaml.cs");
        Assert.Contains("private void ApplyFooterAttentionVisual()", shell);
        Assert.Contains("if (Session.ProgressNeedsAttention) FooterImmersiveProgress.ProgressState = ImmersiveProgressState.Warning;", shell);
        Assert.Contains("FooterPercentText.Foreground = brush", shell);
        Assert.Contains("ApplyFooterAttentionVisual();", shell);
        // 底栏条的"完成/警示"判定同样只读会话真值，不许在 Shell 里重算一致性。
        Assert.DoesNotContain("OverallPass", shell);
    }

    [Fact]
    public void R10_警示色资源键确实存在()
    {
        var colors = ReadRepoFile("src/PCMig.WinUI/Themes/Colors.xaml");
        Assert.Contains("x:Key=\"WarningBrush\"", colors);
    }

    // ── 帮助函数 ──────────────────────────────────────────────────────────────────
    private static string ReadRepoFile(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative));

    /// <summary>取某个方法的方法体（按花括号配平）。用于把断言限制在该方法内，避免命中注释或别处同名文字。</summary>
    private static string MethodBody(string relative, string methodName)
    {
        var text = ReadRepoFile(relative);
        // ★ 必须跳过**调用点**（`await FinishRunAsync(...);`）——它的结尾是 `);` 而不是方法体，
        //   直接取"名字后面的第一个 {" 会落到几百行之外的某个块上，断言就会对着错误文本空转。
        //   判据：名字到 `{` 之间若出现 `;`，那就是调用点，继续找下一个。
        foreach (System.Text.RegularExpressions.Match sig in
                 System.Text.RegularExpressions.Regex.Matches(text, @"\b" + methodName + @"\s*\("))
        {
            var open = text.IndexOf('{', sig.Index + sig.Length);
            if (open < 0) continue;
            var between = text.Substring(sig.Index + sig.Length, open - (sig.Index + sig.Length));
            if (between.Contains(';')) continue;

            var depth = 0;
            for (var j = open; j < text.Length; j++)
            {
                if (text[j] == '{') depth++;
                else if (text[j] == '}')
                {
                    depth--;
                    if (depth == 0) return text.Substring(open, j - open + 1);
                }
            }
            Assert.Fail($"{relative} 的 {methodName} 花括号不配平。");
        }
        Assert.Fail($"{relative} 里找不到方法体 {methodName}（只找到调用点）。");
        return string.Empty;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PCMig.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}