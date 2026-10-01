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

    // Step 2 选择数据与目标（D6.1 §12 补齐）
    public const string Step2Prepare = "Step2.Prepare";
    public const string Step2Start = "Step2.Start";
    public const string Step2Pause = "Step2.Pause";
    public const string Step2Stop = "Step2.Stop";
    public const string Step2Resume = "Step2.Resume";
    public const string Step2ExistingJobs = "Step2.ExistingJobs";
    public const string Step2BrowseTarget = "Step2.BrowseTarget";

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

    /// <summary>D6.1 §12 关键业务控件清单（契约测试按这份清单校验"XAML 里真的有这个 ID"）。</summary>
    public static readonly IReadOnlyList<string> BusinessCritical = new[]
    {
        Step1Connect, Step1AddShare, Step2Prepare, Step2Start, Step2Pause, Step2Stop, Step2Resume,
        Step2ExistingJobs, Step4Verify, Step4Repair, ShellTransferStart, ShellTransferPause, ShellTransferStop,
        ShellTransferResume, ShellMaterialTuning, ShellDiagnostics, ShellDiagnosticsPanel,
        DiagnosticsRefresh, DiagnosticsDeepToggle, DiagnosticsWarnOnly, DiagnosticsExport,
    };
}