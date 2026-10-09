namespace PCMig.WinUI.Diagnostics;

using System.Collections.Generic;

/// <summary>
/// 稳定动作类型 token（**不是显示文字**）。规则、反馈契约与测试都只依赖这些常量。
/// </summary>
public static class ActionKinds
{
    public const string Connect = "Connect";
    public const string AddShare = "AddShare";
    public const string Prepare = "Prepare";
    public const string Start = "Start";
    public const string Pause = "Pause";
    public const string Resume = "Resume";
    public const string Stop = "Stop";
    public const string Verify = "Verify";
    public const string Repair = "Repair";
    public const string Export = "Export";
    public const string Navigate = "Navigate";
    public const string DeepToggle = "DeepToggle";
}

/// <summary>
/// 稳定控件 ID 注册表（方案 §9）。
///
/// 纪律：
///   · **不允许**用显示文字、列表序号、屏幕坐标当控件标识；
///   · 同一个 ID 同时服务三件事：诊断、GUI 自动化、三 VM 自动测试；
///   · 官方只承诺 AutomationId 在兄弟间唯一、**不承诺跨版本稳定** ⇒ 稳定性由本表自己承诺
///     （改名即等于破坏契约，必须在交接文档里登记）。
///
/// D3 只登记连接垂直切片用到的控件；随插桩逐步扩充。
/// </summary>
public static class ControlIds
{
    // Step 1 连接
    public const string Step1HostInput = "Step1.HostInput";
    public const string Step1UsernameInput = "Step1.UsernameInput";
    public const string Step1PasswordInput = "Step1.PasswordInput";
    public const string Step1Connect = "Step1.Connect";
    public const string Step1AddShare = "Step1.AddShare";
    public const string Step1ManualShareName = "Step1.ManualShareName";
    public const string Step1BenchmarkToggle = "Step1.BenchmarkToggle";
    public const string Step1ShareList = "Step1.ShareList";
    public const string Step1EmptySharesHint = "Step1.EmptySharesHint";

    /// <summary>
    /// Step 1 的**连接/添加共享结果状态行**（缺陷 A-02 / A-03 引入的可见反馈面）。
    /// ★ B-FIX-BATCH 补登记（本批）★：XAML 早已带上这个 AutomationId（三 VM 用例
    /// `valueNotEmpty id=Step1.StatusText` 一直在用它读连接结论），但它没进稳定 ID 表
    /// ⇒ D6.3 的 AutomationId 双向闭合契约测试判失败（"稳定 ID 表不再是唯一事实源"）。
    /// 它同时也是 O-B22c-1 修复后承载"凭据未被证实 / 凭据被复用"结论的控件，必须登记。
    /// </summary>
    public const string Step1StatusText = "Step1.StatusText";

    /// <summary>
    /// ★ 2026-10-08（真机问题 1）★ 状态行的**第二层：操作建议**（"现在该怎么做"）。
    /// 为什么必须单独登记：连接失败提示改为"摘要 + 建议"两层后，建议行是一个新的可见反馈面，
    /// 外部自动化要能分别读到"发生了什么"和"该怎么做"（只读诊断锚点，不是点击契约）。
    /// 不登记会让 D6.3 的 AutomationId 双向闭合契约判失败（稳定 ID 表不再是唯一事实源）。
    /// </summary>
    public const string Step1StatusAdvice = "Step1.StatusAdvice";

    // Step 2 选择数据与目标（D6.1 §12 补齐）
    public const string Step2Prepare = "Step2.Prepare";
    public const string Step2Start = "Step2.Start";
    public const string Step2Pause = "Step2.Pause";
    public const string Step2Stop = "Step2.Stop";
    public const string Step2Resume = "Step2.Resume";
    public const string Step2ExistingJobs = "Step2.ExistingJobs";
    public const string Step2BrowseTarget = "Step2.BrowseTarget";

    // Step 3 统计卡数值（Round-2 PHASE 5B 加入，Round-3 PHASE A 补登记）
    //
    // 为什么需要：这四张卡原先只有 `x:Name`，而 `x:Name` **不产出 AutomationId** ⇒ 真机 UIA
    // 枚举不到它们，字形顶部轮廓/排版取证只能靠猜坐标（每次启动窗口原点都变）。
    // 它们不是点击契约，而是**只读诊断锚点**：外部自动化据此读取真实矩形与文本。
    public const string Step3StatSpeedValue = "Step3.Stat.Speed.Value";
    public const string Step3StatEtaValue = "Step3.Stat.Eta.Value";
    public const string Step3StatObjectValue = "Step3.Stat.Object.Value";
    public const string Step3StatBytesValue = "Step3.Stat.Bytes.Value";

    // Step 4 结果与校验（D6.1 §12 补齐）
    public const string Step4Verify = "Step4.Verify";
    public const string Step4Repair = "Step4.Repair";
    public const string Step4Resume = "Step4.Resume";
    public const string Step4OpenReport = "Step4.OpenReport";

    // Shell 底栏（四态按钮）
    public const string ShellTransferStart = "Shell.Transfer.Start";
    public const string ShellTransferPause = "Shell.Transfer.Pause";
    public const string ShellTransferStop = "Shell.Transfer.Stop";
    public const string ShellTransferResume = "Shell.Transfer.Resume";

    // Shell 导航（四张步骤卡）
    //
    // ★ D6.3 WP I ★ 这四条属于"模板生成的 ID"：四张卡共用一个 DataTemplate，
    // 不可能写四个字面量 ⇒ 由 `Presentation\StepNavigation.StepNavItem.ControlId` 按 StepKind 生成，
    // XAML 模板里以 `AutomationProperties.AutomationId="{x:Bind ControlId}"` 绑定。
    // 绑定前的实测事实：UIA 树里 `StepCardButton` 这个 x:Name 派生的 ID 出现 **4 次**
    // （SubtitleText 亦重复）⇒ 外部自动化按名字取控件时会选错对象。
    public const string ShellNavStep1 = "Shell.Nav.Step1";
    public const string ShellNavStep2 = "Shell.Nav.Step2";
    public const string ShellNavStep3 = "Shell.Nav.Step3";
    public const string ShellNavStep4 = "Shell.Nav.Step4";

    // Shell 工具入口 / 浮层
    public const string ShellMaterialTuning = "Shell.Tool.MaterialTuning";
    public const string ShellDiagnostics = "Shell.Tool.Diagnostics";

    /// <summary>诊断浮层自身（**必须**与入口区分开：AutomationId 在兄弟间唯一是不够的，重复会让自动化选错对象）。</summary>
    public const string ShellDiagnosticsPanel = "Shell.Panel.Diagnostics";

    // 诊断中心内部控件
    public const string DiagnosticsRefresh = "Diagnostics.Refresh";
    public const string DiagnosticsDeepToggle = "Diagnostics.DeepTrace";
    public const string DiagnosticsWarnOnly = "Diagnostics.WarnOnly";
    public const string DiagnosticsExport = "Diagnostics.Export";

    /// <summary>
    /// D6.1 §12 关键业务控件清单（契约测试按这份清单校验"XAML 里真的有这个 ID"）。
    /// ★ D6.3 §11（缺口②）★ `Step4.Resume` 与 Step2 的「恢复」/底栏的「恢复」是**同一个业务动作**
    /// 的三个入口，原先漏在清单外（实机确认它点击后零 UI-00x）⇒ 补齐。
    /// </summary>
    public static readonly IReadOnlyList<string> BusinessCritical = new[]
    {
        Step1Connect, Step1AddShare, Step2Prepare, Step2Start, Step2Pause, Step2Stop, Step2Resume,
        Step2ExistingJobs, Step4Verify, Step4Repair, Step4Resume, ShellTransferStart, ShellTransferPause,
        ShellTransferStop, ShellTransferResume, ShellMaterialTuning, ShellDiagnostics, ShellDiagnosticsPanel,
        DiagnosticsRefresh, DiagnosticsDeepToggle, DiagnosticsWarnOnly, DiagnosticsExport,
    };
}