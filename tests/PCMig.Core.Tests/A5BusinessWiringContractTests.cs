using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PCMig.WinUI.Presentation;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// 阶段 A.5「业务接线正确性修正」的静态护栏（源码级契约测试）。
///
/// 覆盖三项（对应主控复核成立的三条静态审查）：
///   · P1-3：<c>CompletedWithErrors</c> 不得被强制显示 100%，且文案必须明说"未完整完成"；
///   · P1-4：失败清单清空必须同步去重索引；从回执恢复时 Failed / CompletedWithErrors / Interrupted
///           三态都要投影；换任务/续传/修复的 UI 状态必须彻底复位；
///   · 第 7 条：三处搜索框与 ExpertMode 的**用户可见文案**不得承诺核心没有的行为。
///
/// 这是"源码文本契约"而非行为测试：WinUI 3 的 ViewModel 需要 DispatcherQueue/UI 线程才能实例化，
/// 单测进程无法真实驱动它；因此用"改前会失败的文本断言"锁住修好的写法，防止被改回去。
/// </summary>
public sealed class A5BusinessWiringContractTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
        }
        throw new InvalidOperationException("找不到含 PCMig.sln 的仓库根：" + AppContext.BaseDirectory);
    }

    private static string ReadWinUi(params string[] parts)
    {
        var path = Path.Combine(new[] { FindRepoRoot(), "src", "PCMig.WinUI" }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), "缺少契约文件：" + path);
        return File.ReadAllText(path);
    }

    private static string ViewModel => ReadWinUi("Presentation", "MigrationSessionViewModel.cs");
    private static string PageReadiness => ReadWinUi("Presentation", "PageReadiness.cs");
    private static string MainWindow => ReadWinUi("MainWindow.xaml.cs");
    private static string DirectoryTree => ReadWinUi("Presentation", "DirectoryTreeViewModel.cs");
    private static string Step2Page => ReadWinUi("Views", "Step2SelectDataPage.xaml.cs");

    /// <summary>
    /// 取某个方法（按**完整签名**定位）的完整方法体（含嵌套花括号）。
    ///
    /// 为什么不能沿用上面 `[^}]*` 那种正则：本文件新增的契约要检查的方法体里都有嵌套块
    /// （`if` / `try` / lambda），`[^}]*` 会在**第一个** `}` 处截断 ⇒ 断言会在"恰好为真"时假绿
    /// （截断后的片段永远不含后半段要禁止的东西）。这里做一次真正的花括号配平。
    /// </summary>
    private static string MethodBody(string text, string signature)
    {
        var i = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(i >= 0, "源码里找不到方法签名：" + signature);
        var open = text.IndexOf('{', i + signature.Length);
        Assert.True(open >= 0, "方法签名之后找不到方法体起始花括号：" + signature);
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
        throw new InvalidOperationException("方法体花括号不配平：" + signature);
    }

    /// <summary>
    /// 去掉行注释（`//` 之后到行尾）。用于**"代码位置/次数"型**断言：
    /// 源码注释里为了说明修复原因会引用旧写法，注释不构成行为，把它算进来会让断言假红或假绿。
    /// （局限：不处理字符串字面量里的 `//`；本文件要检查的这几个方法体里没有这种字面量。）
    /// </summary>
    private static string StripLineComments(string code)
        => string.Join("\n", code.Split('\n').Select(line =>
        {
            var i = line.IndexOf("//", StringComparison.Ordinal);
            return i >= 0 ? line.Substring(0, i) : line;
        }));

    /// <summary>递归读 `src\PCMig.WinUI` 下的全部 .cs（排除 obj/bin），用于"调用点计数"型契约。</summary>
    private static List<(string Path, string Text)> ReadAllWinUiSources()
    {
        var root = Path.Combine(FindRepoRoot(), "src", "PCMig.WinUI");
        var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(files.Count > 10, "src\\PCMig.WinUI 下读到的 .cs 太少（路径/过滤有问题）：" + files.Count);
        return files.Select(p => (p, File.ReadAllText(p))).ToList();
    }

    /// <summary>取某个标记之后的定长窗口（用于只检查"该控件自己那段 XAML"，不受同文件其它控件干扰）。</summary>
    private static string WindowAfter(string text, string marker, int length)
    {
        var i = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(i >= 0, "源码里找不到标记：" + marker);
        var start = Math.Max(0, i - 200);
        var len = Math.Min(length, text.Length - start);
        return text.Substring(start, len);
    }

    // ────────────────────────────── P1-3 ──────────────────────────────

    /// <summary>
    /// ★ P1-3 ★ 只有真 Completed 才允许把百分比写成 100；CompletedWithErrors 必须用实际完成比例。
    /// 改前的坏写法是 <c>var done = phase is Completed or CompletedWithErrors; if (done) Percent = 100.0;</c>。
    /// </summary>
    [Fact]
    public void P1_3_OnlyTrueCompletedForcesHundredPercent()
    {
        var vm = ViewModel;

        // 坏写法必须彻底消失：一个"流程结束"的 done 布尔量不允许直接决定 100%。
        Assert.DoesNotContain("if (done) Percent = 100.0;", vm, StringComparison.Ordinal);
        Assert.DoesNotContain("Percent = done ? 100.0", vm, StringComparison.Ordinal);
        Assert.DoesNotContain("Percent = finished ? 100.0", vm, StringComparison.Ordinal);

        // 三条强制 100% 的路径都必须以"真 Completed"为唯一条件。
        Assert.Contains("if (completed) Percent = 100.0;", vm, StringComparison.Ordinal);
        // ★ FIX BATCH 4（进度真值）★ 同一句现在优先取引擎真值（ProgressTruthSnapshot.Percent），
        //   但"强制 100% 只认真 Completed"这一条语义**不变**：Truth 为 null 时依旧
        //   `s.Phase == JobPhase.Completed ? 100.0 : s.Percent`，Truth 有值时 100% 也只在
        //   收尾（settled）路径由 Core 判出（运行中真值上限 99.9）。这是口径升级，不是放宽。
        // ★ Round-2 2026-10-05（§2 P0）★ 显示的 percent/bytes 唯一来源从 `s.Truth` 改为
        //   `displayTruth`（= ContinuationDisplayState.Apply(rawTruth) 的结果）：它只是引擎 raw
        //   真值的**包装**，仅在"暂停 / 停止 / 恢复"期间把**显示分子**托在用户已看到的水平
        //   （raw 追平高水位即自动解除），CommittedBytes / 判定 / 收尾语义全部原样透传 ⇒ 本条语义不变。
        Assert.Contains("Percent = displayTruth?.Percent ?? (s.Phase == JobPhase.Completed ? 100.0 : s.Percent);", vm, StringComparison.Ordinal);
        Assert.Contains("var displayTruth = continuity.Effective;", vm, StringComparison.Ordinal);
        Assert.Contains("Percent = completed\n                ? 100.0", vm.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    /// <summary>
    /// ★ P1-3 ★ 实际完成比例的唯一算法必须与 Core <c>TransferOrchestrator.Percent(JobState)</c> 同口径
    /// （先按字节、再按对象数），不允许 UI 自己发明一个更高的数字。
    /// </summary>
    [Fact]
    public void P1_3_ActualPercentMirrorsCoreFormula()
    {
        var vm = ViewModel;
        var m = Regex.Match(vm, @"private static double ActualPercent\(JobState st\)\s*\{(?<body>[^}]*)\}",
            RegexOptions.Singleline);
        Assert.True(m.Success, "找不到 ActualPercent(JobState) —— P1-3 的唯一算法入口被删了");

        var body = m.Groups["body"].Value;
        Assert.Contains("st.TotalBytes > 0", body, StringComparison.Ordinal);
        Assert.Contains("st.CompletedBytes * 100.0 / st.TotalBytes", body, StringComparison.Ordinal);
        Assert.Contains("st.CompletedObjects * 100.0 / st.TotalObjects", body, StringComparison.Ordinal);

        // Core 侧同一公式（只读断言，Core 不许被改）：确认两边不是两套口径。
        var core = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "PCMig.Core", "Transfer", "TransferOrchestrator.cs"));
        Assert.Contains("s.CompletedBytes * 100.0 / s.TotalBytes", core, StringComparison.Ordinal);
    }

    /// <summary>
    /// ★ P1-3 ★ 用户可见文案：阶段名与状态句都必须表达"未完整完成"，禁止"完成但有失败对象"这类会被读成"已完成"的措辞，
    /// 并且必须给出实际百分比与可恢复路径。
    /// </summary>
    [Fact]
    public void P1_3_WordingNeverClaimsFullCompletion()
    {
        var vm = ViewModel;
        Assert.DoesNotContain("JobPhase.CompletedWithErrors => \"完成但有失败对象\"", vm, StringComparison.Ordinal);
        Assert.DoesNotContain("◐ 完成(有错误)", vm, StringComparison.Ordinal);

        Assert.Contains("JobPhase.CompletedWithErrors => \"未完整完成（存在失败对象，可恢复）\"", vm, StringComparison.Ordinal);
        Assert.Contains("迁移**未完整完成**", vm, StringComparison.Ordinal);
        Assert.Contains("ObjectStatus.CompletedWithErrors => \"◐ 未完整完成（有错误）\"", vm, StringComparison.Ordinal);

        // IsFinished 仍然可以包含两态（流程语义），但它的注释必须点明"只表示流程结束、不表示进度已满"。
        Assert.Contains("只表示\"流程结束\"，绝不表示\"进度已满\"", vm, StringComparison.Ordinal);

        // PageReadiness：Completed 与 CompletedWithErrors 必须分成两句（改前合并成"迁移已结束"）。
        var pr = PageReadiness;
        Assert.DoesNotContain("_session.Phase is JobPhase.Completed or JobPhase.CompletedWithErrors", pr, StringComparison.Ordinal);
        Assert.Contains("_session.Phase == JobPhase.Completed", pr, StringComparison.Ordinal);
        Assert.Contains("_session.Phase == JobPhase.CompletedWithErrors", pr, StringComparison.Ordinal);
        Assert.Contains("迁移**未完整完成**", pr, StringComparison.Ordinal);
    }

    /// <summary>★ 硬约束 ★ Core 不得被本次修正碰到（P1-3 明确要求"只改 Presentation 层解释"）。</summary>
    [Fact]
    public void P1_3_CoreStaysUntouchedByPresentationFix()
    {
        var core = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "PCMig.Core", "Models", "Models.cs"));
        // 存档字段语义不变：JobState 仍是这三件套，且没有新增"UI 专用"字段。
        Assert.Contains("public long TotalBytes { get; set; }", core, StringComparison.Ordinal);
        Assert.Contains("public long CompletedBytes { get; set; }", core, StringComparison.Ordinal);
        Assert.Contains("public double Percent { get; set; }", core, StringComparison.Ordinal);
        Assert.DoesNotContain("ActualPercent", core, StringComparison.Ordinal);
        Assert.DoesNotContain("未完整完成", core, StringComparison.Ordinal);   // 文案只在 Presentation 层
    }

    // ────────────────────────────── P1-4 ──────────────────────────────

    /// <summary>
    /// ★ P1-4 ★ 失败清单清空必须**同步**去重索引：允许 <c>FailItems.Clear()</c> 出现的地方只有 ClearFailItems 一处，
    /// 且它必须同时清 <c>_failIndex</c>。改前 <c>_failIndex</c> 全项目从不清 ⇒ 清空后同一失败项再也显示不出来。
    /// </summary>
    [Fact]
    public void P1_4_ClearingFailItemsAlsoClearsDedupIndex()
    {
        var vm = ViewModel;

        // 只看**代码语句**（行首缩进的调用）；文档注释里提到这个方法名不算违规。
        var clears = Regex.Matches(vm, @"^[ \t]*FailItems\.Clear\(\);", RegexOptions.Multiline).Count;
        Assert.True(clears == 1, $"FailItems.Clear() 只允许出现在 ClearFailItems() 里，现在有 {clears} 处（漏清 _failIndex 的风险）");

        var m = Regex.Match(vm, @"private void ClearFailItems\(\)\s*\{(?<body>[^}]*)\}", RegexOptions.Singleline);
        Assert.True(m.Success, "找不到 ClearFailItems() —— 清空失败清单的唯一入口");
        Assert.Contains("FailItems.Clear();", m.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains("_failIndex.Clear();", m.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains("_verifyFailKeys.Clear();", m.Groups["body"].Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// ★ P1-4 ★ 从回执恢复时，Failed / CompletedWithErrors / Interrupted **三态都要进失败清单**，
    /// 且缺 ErrorDetail 时必须给出可读说明（不许静默留空）。
    /// 改前只对 <c>Status == Failed</c> 调 AddFail ⇒ 另外两态的对象在报告清单里凭空消失。
    /// </summary>
    [Fact]
    public void P1_4_ReceiptProjectionCoversThreeUnsuccessfulStates()
    {
        var vm = ViewModel;

        Assert.Contains(
            "if (r.Status is ObjectStatus.Failed or ObjectStatus.CompletedWithErrors or ObjectStatus.Interrupted)",
            vm, StringComparison.Ordinal);
        Assert.DoesNotContain("if (r.Status == ObjectStatus.Failed)\n                    AddFail(", vm.Replace("\r\n", "\n"), StringComparison.Ordinal);

        // 三态的标题与"无 ErrorDetail 也要有话说"必须都在。
        Assert.Contains("ObjectStatus.Failed => \"✘ 对象失败\"", vm, StringComparison.Ordinal);
        Assert.Contains("ObjectStatus.CompletedWithErrors => \"◐ 对象未完整完成\"", vm, StringComparison.Ordinal);
        Assert.Contains("\"‖ 对象中断\"", vm, StringComparison.Ordinal);
        Assert.Contains("回执未记录错误明细", vm, StringComparison.Ordinal);

        // 收集修复目标同样要覆盖三态（与 WPF 同口径，不能被改窄）。
        Assert.Contains(
            "if (r.Status is ObjectStatus.Failed or ObjectStatus.CompletedWithErrors or ObjectStatus.Interrupted)",
            vm, StringComparison.Ordinal);
        Assert.Contains("ObjectStatus.Failed or ObjectStatus.CompletedWithErrors or ObjectStatus.Interrupted",
            vm, StringComparison.Ordinal);
    }

    /// <summary>
    /// ★ P1-4 ★ 五个换任务/重跑场景必须各自复位到位：
    /// 新建任务、载入另一个任务、Resume/Repair 开跑前，都要走统一复位入口；
    /// 且复位包含 LogLines（上一个 Job 的日志投影）与验证投影。
    /// </summary>
    [Fact]
    public void P1_4_AllJobSwitchScenariosResetProjection()
    {
        var vm = ViewModel;

        // 统一复位入口存在，且覆盖任务要求的每一项。
        var run = Regex.Match(vm, @"private void ResetRunProjection\(\)\s*\{(?<body>[^}]*)\}", RegexOptions.Singleline);
        Assert.True(run.Success, "找不到 ResetRunProjection()");
        var runBody = run.Groups["body"].Value;
        Assert.Contains("ClearFailItems();", runBody, StringComparison.Ordinal);
        Assert.Contains("LiveFiles.Clear();", runBody, StringComparison.Ordinal);
        Assert.Contains("CurrentFileText = \"—\";", runBody, StringComparison.Ordinal);
        Assert.Contains("CurrentObjectPath = string.Empty;", runBody, StringComparison.Ordinal);
        Assert.Contains("CurrentObjectDetail = string.Empty;", runBody, StringComparison.Ordinal);

        var verify = Regex.Match(vm, @"private void ResetVerifyProjection\(\)\s*\{(?<body>[^}]*)\}", RegexOptions.Singleline);
        Assert.True(verify.Success, "找不到 ResetVerifyProjection()");
        var verifyBody = verify.Groups["body"].Value;
        Assert.Contains("LastVerifyResultText = string.Empty;", verifyBody, StringComparison.Ordinal);
        Assert.Contains("TotalHashSampled = 0;", verifyBody, StringComparison.Ordinal);
        Assert.Contains("_hasVerifyReport = false;", verifyBody, StringComparison.Ordinal);
        Assert.Contains("ClearVerifyDerivedFails();", verifyBody, StringComparison.Ordinal);

        var switcher = Regex.Match(vm, @"private void ResetForJobSwitch\(\)\s*\{(?<body>[^}]*)\}", RegexOptions.Singleline);
        Assert.True(switcher.Success, "找不到 ResetForJobSwitch()");
        var switchBody = switcher.Groups["body"].Value;
        Assert.Contains("ResetRunProjection();", switchBody, StringComparison.Ordinal);
        Assert.Contains("ResetVerifyProjection();", switchBody, StringComparison.Ordinal);
        Assert.Contains("LogLines.Clear();", switchBody, StringComparison.Ordinal);
        Assert.Contains("FailedObjects = 0;", switchBody, StringComparison.Ordinal);

        // 场景①新建任务 / 场景②载入另一个任务 / 场景③④⑤Run·Resume·Repair 开跑前 —— 三处调用点。
        Assert.True(Regex.Matches(vm, @"ResetForJobSwitch\(\);").Count >= 3,
            "ResetForJobSwitch() 的调用点少于 3 处（新建任务 / 预检新任务 / 载入另一个任务）");
        Assert.Contains("if (forceRecopy) IsRepairing = true;\n        ResetRunProjection();", vm.Replace("\r\n", "\n"),
            StringComparison.Ordinal);

        // 载入既有任务时，失败对象数必须按本任务存档重建（改前会留着上一个任务的内存值）。
        Assert.Contains("FailedObjects = unreliable ? 0 : st.FailedObjects;", vm, StringComparison.Ordinal);

        // 修复后重新验证：上一轮"校验不一致"的条目必须先撤掉，否则修好了还挂着旧结论。
        var apply = Regex.Match(vm, @"private void ApplyVerifyToRows\(VerifyReport report\)\s*\{(?<body>.*?)\n    \}",
            RegexOptions.Singleline);
        Assert.True(apply.Success, "找不到 ApplyVerifyToRows(VerifyReport)");
        Assert.Contains("ClearVerifyDerivedFails();", apply.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains("verifyDerived: true", apply.Groups["body"].Value, StringComparison.Ordinal);
    }

    // ────────────────────────────── 第 7 条（产品诚实性）──────────────────────────────

    /// <summary>
    /// ★ 第 7 条 ★ 三处搜索框都不许"能输入却无效果"：必须禁用，且"暂未启用"要写在**占位符**上
    /// （禁用控件悬停默认不弹 tooltip，标注写在 tooltip 里等于没写）。
    /// </summary>
    [Theory]
    [InlineData("Views", "Step1ConnectPage.xaml")]
    [InlineData("Views", "Step2SelectDataPage.xaml")]
    [InlineData("Views", "Step4ResultPage.xaml")]
    public void Task7_SearchBoxesAreDisabledAndSaySo(string dir, string file)
    {
        var xaml = ReadWinUi(dir, file);
        var window = WindowAfter(xaml, "x:Name=\"SearchBox\"", 520);

        Assert.Contains("IsEnabled=\"False\"", window, StringComparison.Ordinal);
        Assert.Contains("暂未启用", window, StringComparison.Ordinal);
        Assert.DoesNotContain("筛选逻辑待接入", xaml, StringComparison.Ordinal);   // 旧的"待接入"占位说法一并清除
        Assert.DoesNotContain("PlaceholderText=\"搜索目录或文件名...\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaceholderText=\"搜索文件或错误原因...\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaceholderText=\"搜索共享名称...\"", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// ★ 第 7 条 ★ ExpertMode 的 tooltip 必须与真实语义一致：改前那句"仅传输文件清单与增量，不预读全部目录"
    /// 是 Core 里根本不存在的产品语义（凭空的承诺）。
    /// </summary>
    [Fact]
    public void Task7_ExpertModeTooltipMatchesReality()
    {
        var xaml = ReadWinUi("Views", "Step2SelectDataPage.xaml");

        // 只看**ToolTip 属性的真实取值**（注释里为了说明改了什么而引用旧文案，不算违规）。
        var tooltips = Regex.Matches(xaml, "ToolTipService\\.ToolTip=\"(?<v>[^\"]*)\"")
            .Select(m => m.Groups["v"].Value).ToList();
        Assert.DoesNotContain(tooltips, v => v.Contains("仅传输文件清单与增量", StringComparison.Ordinal));
        Assert.DoesNotContain(tooltips, v => v.Contains("超大数数据模式", StringComparison.Ordinal));
        Assert.Contains("超大数据模式：暂未启用，当前迁移范围以目录选择为准。", tooltips);

        // 标签错别字「超大数数据模式」必须修掉（"数数"）。
        Assert.DoesNotContain("超大数数据模式", xaml, StringComparison.Ordinal);

        // WinUI 3 没有 WPF 的 ShowOnDisabled（实测 XamlCompiler 报 WMC0010）：不许有人再"想当然"地加回去。
        // 只看**属性用法**（注释里为了记录这次实测结论会提到这个名字）。
        Assert.DoesNotContain("ToolTipService.ShowOnDisabled=", xaml, StringComparison.Ordinal);
        // 因此同一句话必须同时挂在可命中的 ⓘ 图标上（悬停可读 = 标注可验证）。
        Assert.True(tooltips.Count(v => v == "超大数据模式：暂未启用，当前迁移范围以目录选择为准。") >= 2,
            "tooltip 文案只挂了一处：禁用控件本身弹不出 tooltip，必须同时挂在可命中的 ⓘ 图标上");
    }

    /// <summary>★ 硬约束 ★ 第 7 条只许改文案与 IsEnabled，不许动冻结的视觉（颜色/几何/动画/主题）。</summary>
    [Fact]
    public void Task7_NoVisualFreezeViolation()
    {
        foreach (var file in new[] { "Step1ConnectPage.xaml", "Step2SelectDataPage.xaml", "Step4ResultPage.xaml" })
        {
            var xaml = ReadWinUi("Views", file);
            // 搜索框/开关的几何与样式必须原样保留（这些是冻结过的视觉值）。
            Assert.DoesNotContain("Opacity=\"", WindowAfter(xaml, "x:Name=\"SearchBox\"", 520), StringComparison.Ordinal);
        }

        var step2 = ReadWinUi("Views", "Step2SelectDataPage.xaml");
        var toggle = WindowAfter(step2, "x:Name=\"ExpertModeToggle\"", 400);
        Assert.Contains("MinWidth=\"0\"", toggle, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"False\"", toggle, StringComparison.Ordinal);
    }

    // ══════════════════════════ P2-6（Host 异步探测竞态）══════════════════════════

    /// <summary>
    /// ★ P2-6 / B7 ★ Shell 侧的探测入口必须是**同步**的：改前 `OnSessionChangedForUnfinishedProbe`
    /// 是 `async void` + `await ProbeUnfinishedAsync()`（每敲一个字符就可能跑一次真实的全量任务目录扫描，
    /// 且旧结果会覆盖新状态）。契约锁死："不是 async void / 不含 await / 只调同步的 RequestUnfinishedProbe"。
    /// </summary>
    [Fact]
    public void P2_6_ShellProbeHandlerIsSynchronousAndDelegatesToTheSession()
    {
        var mw = MainWindow;
        var body = MethodBody(mw, "private void OnSessionChangedForUnfinishedProbe(");

        Assert.DoesNotContain("async", body, StringComparison.Ordinal);
        Assert.DoesNotContain("await", body, StringComparison.Ordinal);
        Assert.Contains("Session.RequestUnfinishedProbe();", body, StringComparison.Ordinal);

        // 改前的写法必须彻底消失（async void 无法 await、无法观测异常、可并发重入）。
        Assert.DoesNotContain("private async void OnSessionChangedForUnfinishedProbe", mw, StringComparison.Ordinal);
        Assert.DoesNotContain("private async System.Threading.Tasks.Task OnSessionChangedForUnfinishedProbe", mw, StringComparison.Ordinal);
    }

    /// <summary>
    /// ★ P2-6 / notes §15.4 ★ `PendingResumeCandidate` 必须自己发通知 —— 从而替掉 Shell 里那次
    /// **手工** `PageResult.RefreshFromSession()`（改前它是因为该属性不 Raise 才不得不加的补丁；
    /// 旧探测结果一旦覆盖候选，这次手工刷新就把「恢复任务」指到错任务上）。
    /// </summary>
    [Fact]
    public void P2_6_PendingResumeCandidateRaisesItsOwnChangeNotification()
    {
        var vm = ViewModel;
        Assert.Contains("Raise(nameof(PendingResumeCandidate));", vm, StringComparison.Ordinal);

        // 自动属性的写法必须消失（它没有 backing field ⇒ 不可能带通知）。
        Assert.DoesNotContain("public JobSummary? PendingResumeCandidate { get; private set; }", vm, StringComparison.Ordinal);

        // Shell 的探测路径不再手工刷新页面（Step4 已订阅会话的 PropertyChanged ⇒ 自己刷）。
        var probe = MethodBody(MainWindow, "private async System.Threading.Tasks.Task ProbeUnfinishedAsync()");
        Assert.DoesNotContain("RefreshFromSession()", probe, StringComparison.Ordinal);
    }

    /// <summary>
    /// ★ P2-6 / B8 + §15.1 ★ 关闭路径：去抖计时器**折进同一个 <c>StopUiRefresh()</c> 出口**，
    /// **禁止**新增 `StopUnfinishedProbeTimer()` 之类的第二个公开出口；
    /// `MainWindow.ShutdownAndExit` 仍在 `Environment.Exit(0)` 之前调它。
    /// （它与 P1-5 的 A11 契约合并成一条：同一个出口停两个 DispatcherQueueTimer。）
    /// </summary>
    [Fact]
    public void P2_6_DebounceTimerStopsThroughTheSingleExistingShutdownExit()
    {
        var stopBody = MethodBody(ViewModel, "public void StopUiRefresh()");
        Assert.Contains("StopFlushTimer();", stopBody, StringComparison.Ordinal);       // P1-5 的节拍
        Assert.Contains("StopProbeDebouncePump();", stopBody, StringComparison.Ordinal); // P2-6 的去抖
        Assert.Contains("_uiRefreshStopped = true;", stopBody, StringComparison.Ordinal);

        // 唯一出口：不许再有第二个**公开**停止入口（注释里为说明"禁止什么"而提到名字不算违规）。
        foreach (var (path, text) in ReadAllWinUiSources())
        {
            Assert.DoesNotContain("public void StopUnfinishedProbeTimer", text, StringComparison.Ordinal);
            Assert.DoesNotContain("void StopUnfinishedProbeTimer()", text, StringComparison.Ordinal);
            Assert.True(text.Length > 0, path);   // 读到的确实是文件内容
        }

        var shutdown = MethodBody(MainWindow, "private void ShutdownAndExit()");
        Assert.Contains("Session.StopUiRefresh();", shutdown, StringComparison.Ordinal);
        // 必须在 Environment.Exit(0) 之前。
        Assert.True(shutdown.IndexOf("Session.StopUiRefresh();", StringComparison.Ordinal)
                    < shutdown.IndexOf("Environment.Exit(0);", StringComparison.Ordinal),
            "StopUiRefresh() 必须排在 Environment.Exit(0) 之前（否则复现关闭期崩溃族）");
    }

    /// <summary>
    /// ★ P2-6 / 测试缝纪律 ★ 两个新测试缝必须在**装配层无处可设**：
    /// `MainWindow` 里不得出现 `UnfinishedProbeForTest` / `UnfinishedProbeDebouncePumpFactoryForTest` /
    /// `ForceConnectedForTest`（否则"测试缝变成第二条生产路径"）；且两个缝的 setter 必须留 Warning 日志。
    /// </summary>
    [Fact]
    public void P2_6_TestSeamsAreNeverWiredByTheShell()
    {
        var mw = MainWindow;
        Assert.DoesNotContain("UnfinishedProbeForTest", mw, StringComparison.Ordinal);
        Assert.DoesNotContain("UnfinishedProbeDebouncePumpFactoryForTest", mw, StringComparison.Ordinal);
        Assert.DoesNotContain("ForceConnectedForTest", mw, StringComparison.Ordinal);
        Assert.DoesNotContain("LastProbeTaskForTest", mw, StringComparison.Ordinal);

        var vm = ViewModel;
        Assert.Contains("_log.Warning(\"UnfinishedProbeForTest 测试缝被赋值", vm, StringComparison.Ordinal);
        Assert.Contains("_log.Warning(\"UnfinishedProbeDebouncePumpFactoryForTest 测试缝被赋值", vm, StringComparison.Ordinal);

        // 探测实现只允许"二选一"（缝 / Core 的 JobManager），不得出现第三条路径。
        var core = MethodBody(vm, "private IReadOnlyList<JobSummary> FindUnfinishedCore(");
        Assert.Contains("seam is not null ? seam(host, target) : new JobManager(_log).FindUnfinished(host, target)", core, StringComparison.Ordinal);
    }

    /// <summary>
    /// ★ P2-6 / 主控裁决 notes §15.3 ★ `PromptResumeIfAnyAsync` 在 WinUI 壳里**当前无调用方**：
    /// 不额外加世代门，但必须**显式注明**"无调用方、不参与输入风暴"（否则后人会误判风险面）。
    /// </summary>
    [Fact]
    public void P2_6_UncalledPromptApiIsExplicitlyDocumentedAsUncalled()
    {
        var vm = ViewModel;
        var sig = vm.IndexOf("public async Task<bool> PromptResumeIfAnyAsync(", StringComparison.Ordinal);
        Assert.True(sig > 0, "找不到 PromptResumeIfAnyAsync");

        // 方法上方的 XML 注释里必须写明"无调用方/不参与输入风暴"。
        var doc = vm.Substring(Math.Max(0, sig - 1200), Math.Min(1200, sig));
        Assert.Contains("没有任何调用点", doc, StringComparison.Ordinal);
        Assert.Contains("不参与输入风暴", doc, StringComparison.Ordinal);

        // 并且**确实**没有任何调用点（本契约随"将来接线"一起失效：那时必须补世代门）。
        foreach (var (path, text) in ReadAllWinUiSources())
            Assert.False(text.Contains("PromptResumeIfAnyAsync(", StringComparison.Ordinal)
                         && !path.EndsWith("MigrationSessionViewModel.cs", StringComparison.OrdinalIgnoreCase),
                "PromptResumeIfAnyAsync 出现了调用点：" + path + " —— 接线时必须同批补上世代门");
    }

    // ══════════════════════════ 问题 C（C1 / C2）══════════════════════════════════

    /// <summary>
    /// ★ C1 ★ `EnsureChildrenAsync`：`ChildrenLoaded = true` **只能在成功路径**（必须晚于 `await Task.Run`），
    /// 且方法体内**恰好一次**；同时必须有 `_loading` 重入守卫（首行加、`finally` 移除）。
    /// 改前的坏写法（`await` 之前就置位）会随本契约失效。
    /// </summary>
    [Fact]
    public void C1_ChildrenLoadedIsSetOnlyOnTheSuccessPath_WithAReentrancyGuard()
    {
        var body = MethodBody(DirectoryTree, "public async Task EnsureChildrenAsync(DirNode node, int? version = null)");
        // ★ 先去行注释再断言"代码位置"：源码注释里为说明修复原因会**引用旧写法**
        //   （例如 `node.ChildrenLoaded = true; // 防重入  ← 改前在 await 之前就置位`），
        //   若把注释算进来，这类断言会假红/假绿。注释不构成行为，必须剥掉。
        var code = StripLineComments(body);

        var awaitIndex = code.IndexOf("await Task.Run", StringComparison.Ordinal);
        var setIndex = code.IndexOf("node.ChildrenLoaded = true;", StringComparison.Ordinal);
        Assert.True(awaitIndex > 0, "找不到 await Task.Run（C1 的先后顺序断言依赖它）");
        Assert.True(setIndex > 0, "找不到 `node.ChildrenLoaded = true;` —— 成功路径的置位点被删了");
        Assert.True(setIndex > awaitIndex,
            "`ChildrenLoaded = true` 又跑到了 await 之前 —— 失败/作废后节点会永久不可重试（C1 回归）");

        // 恰好一次。
        var occurrences = Regex.Matches(code, Regex.Escape("node.ChildrenLoaded = true;")).Count;
        Assert.True(occurrences == 1, $"`node.ChildrenLoaded = true;` 在方法体内出现了 {occurrences} 次（应恰好 1 次）");

        Assert.Contains("_loading.Contains(node)", code, StringComparison.Ordinal);
        Assert.Contains("_loading.Add(node);", code, StringComparison.Ordinal);
        Assert.Contains("_loading.Remove(node);", code, StringComparison.Ordinal);
        Assert.DoesNotContain("_loading.Clear();", code, StringComparison.Ordinal);   // 绝不整体清（会误放行别的节点）

        // 失败/作废两条路径都必须"不置位"：作废分支的 return 之前不得出现置位。
        var stale = code.IndexOf("if (v != Volatile.Read(ref _browseVersion)) return;", StringComparison.Ordinal);
        Assert.True(stale > 0, "找不到「结果被作废就丢弃」的那条判据（C2 的代次比较）");
        Assert.True(stale < setIndex, "作废判据必须排在置位点之前");
    }

    /// <summary>
    /// ★ C2 / C3 / C4 / R19 / C8 ★ `InvalidateInFlightBrowses()` 的**唯二**调用点：
    /// ① `Step2SelectDataPage.AttachConnection`（连接重建）；
    /// ② `DirectoryTreeViewModel.SyncRoots`（**真的**增/删根时）。
    /// 并且**禁止**出现在 `OnShareItemChanged`（反复勾/取消共享不得触发新读盘）。
    /// </summary>
    [Fact]
    public void C2_InvalidationIsWiredAtExactlyTheTwoSanctionedPlaces()
    {
        // 全仓计数：1 处定义 + 至少 1 处调用（"设计了却从不接线"正是这条要防的）。
        var all = ReadAllWinUiSources();
        var callSites = all.SelectMany(f => Regex.Matches(f.Text, @"InvalidateInFlightBrowses\(")
                .Select(m => f.Path)).ToList();
        Assert.True(callSites.Count >= 2, $"InvalidateInFlightBrowses( 在 src\\PCMig.WinUI 下只出现 {callSites.Count} 次（应 ≥ 2：定义 + 调用）");
        Assert.Contains(callSites, p => p.EndsWith("Step2SelectDataPage.xaml.cs", StringComparison.OrdinalIgnoreCase));

        // ① 连接重建路径：订阅重建之后、SyncTreeRootsFromShares 之前/之后各一次都算，但必须在方法体内。
        var attach = MethodBody(Step2Page, "public void AttachConnection(ConnectionViewModel connection)");
        Assert.Contains("InvalidateInFlightBrowses();", attach, StringComparison.Ordinal);

        // ② 根集合变化路径：只在"真的增/删"时（`changed` 标记）。
        var syncRoots = MethodBody(DirectoryTree, "public void SyncRoots(IEnumerable<ShareRootSpec> shares)");
        Assert.Contains("if (changed) InvalidateInFlightBrowses();", syncRoots, StringComparison.Ordinal);

        // ✗ 明确不该加：共享行勾选变化（R19/C8 的验收标准是"反复勾/取消共享不得触发新读盘"）。
        var shareChanged = MethodBody(Step2Page, "private void OnShareItemChanged(");
        Assert.DoesNotContain("InvalidateInFlightBrowses", shareChanged, StringComparison.Ordinal);
    }

    /// <summary>
    /// ★ C2a / R20 ★ `_browseVersion` 是跨线程读写的（`_queue is null` 时续体可能在线程池线程）
    /// ⇒ 只允许 `Volatile.Read` / `Interlocked.Increment`，不得保留裸的 `++`。
    /// </summary>
    [Fact]
    public void C2_BrowseVersionUsesVolatileAndInterlocked()
    {
        var tree = DirectoryTree;
        Assert.Contains("Interlocked.Increment(ref _browseVersion)", tree, StringComparison.Ordinal);
        Assert.Contains("Volatile.Read(ref _browseVersion)", tree, StringComparison.Ordinal);
        Assert.DoesNotContain("_browseVersion++", tree, StringComparison.Ordinal);
    }

    /// <summary>
    /// ★ C1 的前提（不变式，防回归）★ "失败可重试"要求**未成功加载的节点仍然显示可展开箭头**。
    /// ★ 2026-09-30 更新（A.5 P0-4）★ affordance 的实现从"占位子节点"换成 `HasUnrealizedChildren`
    ///   （用户真实鼠标实测证明占位方案下箭头点了毫无反应），本契约随之锁定新写法。
    /// </summary>
    [Fact]
    public void C1_TheRetryChevronPrerequisiteStaysIntact()
    {
        // 未加载 ⇒ 保留展开 affordance（可重试）
        Assert.Contains("if (!model.ChildrenLoaded) node.HasUnrealizedChildren = true;",
            Step2Page, StringComparison.Ordinal);
        Assert.Contains("node.HasUnrealizedChildren = !model.ChildrenLoaded;",
            Step2Page, StringComparison.Ordinal);
        // 不得退回"占位子节点"方案
        Assert.DoesNotContain("if (node.Children.Count == 0 && !model.ChildrenLoaded) node.Children.Add(new TreeViewNode());",
            Step2Page, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  P0「Step2 目录树选择状态串线」收敛回归（2026-09-30）
    //  对应验收清单第 9 节 1–6 条。本区段是本文件里**唯一的**行为级区段：
    //  DirNode / FileRow 已按 csproj 的 A5LinkedPresentationSources 链入（见 DirTreeModels.cs），
    //  可真实 new 出对象断言，而不是只扫源码文本。上面全部区段仍是契约级。
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 取"一行模板"那段 XAML（<c>DirTreeRowTemplate</c> 的 CheckBox 所在的 DataTemplate）。
    /// 只在这段窗口里断言，避免被同文件其它控件（例如别的 CheckBox/Tag）干扰。
    /// </summary>
    private static string RowTemplateXaml()
        => WindowAfter(ReadWinUi("Views", "Step2SelectDataPage.xaml"), "x:Key=\"DirTreeRowTemplate\"", 3500);

    /// <summary>
    /// ★ 第 9 节 1/2/3 条 · 契约级 ★ 单一数据流：
    /// ① CheckBox 的状态源必须是 TwoWay 绑定 <c>Content.IsChecked</c>；
    /// ② 模板里不得再出现 <c>Click="NodeCheck_Click"</c>（Click 里再 ToggleFromUi 就是第二套写入路径）；
    /// ③ 不得再拿 <c>Content.CheckStateTrue</c> 当 CheckBox 的状态源（它是只读投影，绑上去只会"点了不回写"）；
    /// ④ <c>Step2SelectDataPage.xaml.cs</c> 里不得再有 <c>NodeCheck_Click</c> 的**方法签名**；
    /// ⑤ 半选投影必须仍是"只读 + 不拦截命中测试"的纯视觉方块（防有人把半选改成第三个可点控件）。
    /// </summary>
    [Fact]
    public void Step2Row_CheckBoxIsTwoWayBoundToModel_AndNoSecondWritePathExistsOnTheRow()
    {
        var template = RowTemplateXaml();

        // ① 唯一写入路径：TwoWay 绑定模型。
        Assert.Contains("IsChecked=\"{Binding Content.IsChecked, Mode=TwoWay}\"", template, StringComparison.Ordinal);

        // ② 模板里不得有任何 Click 处理器（行上只剩绑定这一条写入路径）。
        Assert.DoesNotContain("Click=", template, StringComparison.Ordinal);
        Assert.DoesNotContain("NodeCheck_Click", template, StringComparison.Ordinal);

        // ③ 旧的"状态源 = CheckStateTrue + Converter"写法必须彻底消失。
        Assert.DoesNotContain("Content.CheckStateTrue", template, StringComparison.Ordinal);
        Assert.DoesNotContain("Converter={StaticResource RowChecked}", template, StringComparison.Ordinal);

        // ④ code-behind 侧：不许再有这个方法（注释里为记录"已删除"而提到名字不算 —— 故用签名形式断言）。
        Assert.False(Regex.IsMatch(Step2Page, @"\bvoid\s+NodeCheck_Click\s*\("),
            "NodeCheck_Click 方法签名又回来了 —— 那会重新引入第二套模型写入路径（双重 Toggle / 点一次不生效）");

        // ⑤ 半选仍是纯视觉投影，且 CheckBox 不自己造第三态。
        Assert.Contains("IsThreeState=\"False\"", template, StringComparison.Ordinal);
        Assert.Contains("IsHitTestVisible=\"False\"", template, StringComparison.Ordinal);
        Assert.Contains("Content.CheckStateIndeterminate", template, StringComparison.Ordinal);
    }

    /// <summary>
    /// ★ 第 9 节 3 条 · 契约级 ★ "代码直推"（Row Push）整体删除后不得复活。
    /// 它缓存 CheckBox 控件引用 + 延迟 DispatcherQueue 回调，在 TreeView 容器复用（虚拟化）时
    /// 会**跨行错写** ⇒ 这正是"点 A 却把 B 也改了 / 跨盘联动"的机制性根因。
    /// 逐个成员名断言，一个都不许在 <c>src\PCMig.WinUI\**</c> 里出现（.cs 与 .xaml 都扫）。
    /// </summary>
    [Fact]
    public void Step2Row_PushedRowStateMachineryStaysDeleted()
    {
        string[] forbidden =
        {
            "RowVisual", "_rowVisuals", "_rowHandlers", "PushRowState", "OnRowModelChanged",
            "DirTreeRow_Loaded", "DirTreeRow_Unloaded", "ResolveRowModel", "QaLogRowBinding",
            "RowCheckTag", "RowIndTag", "_qaPushDisabled", "_qaRowLogEnabled",
        };

        var root = Path.Combine(FindRepoRoot(), "src", "PCMig.WinUI");
        var files = Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(p => (p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                         || p.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(files.Count > 20, "src\\PCMig.WinUI 下读到的 .cs/.xaml 太少（路径/过滤有问题）：" + files.Count);

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var name in forbidden)
                Assert.False(text.Contains(name, StringComparison.Ordinal),
                    $"Row Push 成员 `{name}` 又在 {Path.GetFileName(file)} 里出现了 —— 容器复用时会跨行错写（选择状态串线回归）");
        }

        // XAML 侧：行模板不得再挂 Loaded/Unloaded 回调与 Tag 标记（它们就是 Row Push 的钩子）。
        var template = RowTemplateXaml();
        Assert.DoesNotContain("Loaded=", template, StringComparison.Ordinal);
        Assert.DoesNotContain("Unloaded=", template, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=", template, StringComparison.Ordinal);
    }

    /// <summary>建"父 + 两个 DirNode 子"的最小树（父子关系与级联方向都按生产约定）。</summary>
    private static (DirNode Root, DirNode A, DirNode B) TwoDirChildRoot()
    {
        var root = new DirNode { Name = "root", FullPath = @"\\h\s" };
        var a = new DirNode { Name = "A", FullPath = @"\\h\s\A", Parent = root };
        var b = new DirNode { Name = "B", FullPath = @"\\h\s\B", Parent = root };
        root.ChildrenLoaded = true;
        root.Children.Add(a);
        root.Children.Add(b);
        return (root, a, b);
    }

    /// <summary>
    /// ★ 第 9 节 4 条 · 行为级 ★ 父级全选必须能**重新覆盖**此前被手动取消的子节点。
    /// 构造：父 + 两个子，先把子设为 false ⇒ 父应为 null（半选）；
    /// 再对父 SetChecked(true) ⇒ 两个子都必须变 true。
    /// 这条锁的是"父的级联是**无条件向下写**，不因某个子曾有过本地值而跳过"。
    /// </summary>
    [Fact]
    public void Step2Behavior_ParentFullCheck_OverwritesPreviouslyManuallyUncheckedChildren()
    {
        var (root, a, b) = TwoDirChildRoot();

        a.SetChecked(false);
        b.SetChecked(false);
        Assert.False(a.IsChecked);
        Assert.False(b.IsChecked);
        Assert.False(root.IsChecked);      // 两个子全不勾 ⇒ 父是全不勾，不是半选

        a.SetChecked(true);                // 只手勾一个 ⇒ 父半选
        Assert.Null(root.IsChecked);
        Assert.True(a.IsChecked);
        Assert.False(b.IsChecked);

        root.SetChecked(true);             // ★ 父级全选：必须覆盖掉"曾被手动取消"的 b
        Assert.True(root.IsChecked);
        Assert.True(a.IsChecked);
        Assert.True(b.IsChecked);
        Assert.Equal(2, root.Children.Count);   // 级联不得把子节点弄丢/重建
    }

    /// <summary>
    /// ★ 第 9 节 5 条 · 行为级 ★ <c>FileRow</c> 的改动只影响**自身与父链**：
    /// 改 A 后 A 变、兄弟 B 不变、父按规则重算（同目录还有别的文件 ⇒ 半选）。
    /// 这条是"点一行却改了别的行"的反向对照。
    /// </summary>
    [Fact]
    public void Step2Behavior_FileRowChange_TouchesOnlyItselfAndItsParentChain()
    {
        var root = new DirNode { Name = "root", FullPath = @"\\h\s" };
        var dir = new DirNode { Name = "D", FullPath = @"\\h\s\D", Parent = root };
        var fileA = new FileRow { Name = "A.txt", DirPath = dir.FullPath, Size = 10, Parent = dir };
        var fileB = new FileRow { Name = "B.txt", DirPath = dir.FullPath, Size = 20, Parent = dir };
        root.ChildrenLoaded = true;
        dir.ChildrenLoaded = true;
        root.Children.Add(dir);
        dir.Children.Add(fileA);
        dir.Children.Add(fileB);

        root.SetChecked(true);
        Assert.True(fileA.IsChecked);
        Assert.True(fileB.IsChecked);

        fileA.IsChecked = false;

        Assert.False(fileA.IsChecked);     // ① 自身变
        Assert.True(fileB.IsChecked);      // ② 兄弟**不变**
        Assert.Null(dir.IsChecked);        // ③ 父按规则重算 = 半选
        Assert.Null(root.IsChecked);       //    并继续向上传播

        fileA.IsChecked = true;
        Assert.True(fileA.IsChecked);
        Assert.True(fileB.IsChecked);
        Assert.True(dir.IsChecked);        // 全部回勾 ⇒ 父回到全选
        Assert.True(root.IsChecked);
    }

    /// <summary>
    /// ★ 第 9 节 6 条 · 行为级 ★ 两个兄弟 <c>FileRow</c> 状态互不影响：
    /// 反复切换 A 20 次（奇数轮结束时 A=false、偶数轮 A=true），B **全程**不变（恒 true）。
    /// 父的取值按规则重算（半选 ⇄ 全选）；并顺带断言 A 的每次全量切换都发了通知
    /// （少发通知 = 界面不刷新 = "点了没反应"）。
    /// </summary>
    [Fact]
    public void Step2Behavior_SiblingFileRows_NeverInterfereAcrossRepeatedToggles()
    {
        var root = new DirNode { Name = "root", FullPath = @"\\h\s" };
        var dir = new DirNode { Name = "D", FullPath = @"\\h\s\D", Parent = root };
        var fileA = new FileRow { Name = "A.txt", DirPath = dir.FullPath, Size = 10, Parent = dir };
        var fileB = new FileRow { Name = "B.txt", DirPath = dir.FullPath, Size = 20, Parent = dir };
        root.ChildrenLoaded = true;
        dir.ChildrenLoaded = true;
        root.Children.Add(dir);
        dir.Children.Add(fileA);
        dir.Children.Add(fileB);

        root.SetChecked(true);             // 起点：A、B 都勾着
        var bEvents = 0;
        fileB.PropertyChanged += (_, __) => bEvents++;

        var aEvents = 0;
        fileA.PropertyChanged += (_, __) => aEvents++;

        for (var i = 1; i <= 20; i++)
        {
            fileA.IsChecked = i % 2 == 0;      // 首轮 false、次轮 true、交替；20 轮全是真实切换
            var expected = i % 2 == 0;

            Assert.Equal(expected, fileA.IsChecked);
            Assert.True(fileB.IsChecked);          // ★ B 全程不变
            // 父按规则跟随：两兄弟同态 ⇒ 全勾 / 全不勾；一勾一不勾（本用例 B 恒勾）⇒ **半选**。
            // （半选由上一句"B 恒勾 + A == expected"确定，故这里父的取值是唯一确定的。）
            if (expected) Assert.True(dir.IsChecked);
            else Assert.Null(dir.IsChecked);
            Assert.Equal(dir.IsChecked, root.IsChecked);   // 父链继续向上传播，口径一致
        }

        Assert.Equal(0, bEvents);          // ★ B 连一次通知都没收到（半选重算不写子节点）
        // A 的每次真实切换必须发**两次**通知（IsChecked 供联动订阅 + CheckStateTrue 供勾选投影）：
        // 少发前者 ⇒ 联动静默失效；少发后者 ⇒ 复选框不刷新（表现就是"点了没反应"）。
        // 循环从 i=1 起且首轮就与起点相反 ⇒ 20 次全是**真实**切换，故恰好 20 × 2 = 40：
        // 既防漏发，也防有人拿"多发通知"糊过去。
        Assert.Equal(40, aEvents);
    }
}