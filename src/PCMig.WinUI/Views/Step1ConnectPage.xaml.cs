using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PCMig.Diagnostics.Abstractions;
using PCMig.WinUI.Diagnostics;
using PCMig.WinUI.Presentation;

namespace PCMig.WinUI.Views;

/// <summary>
/// Step 1 — 连接旧电脑。
///
/// 职责边界（重要）：
///   * 本页**不新建任何业务状态**：ViewModel 由 Shell 注入（唯一实例，跨页共享）；
///   * 本页只做"把已有 ViewModel 的状态显示出来 + 把用户动作转回 ViewModel 的命令"；
///   * 不引用 Robocopy / TransferOrchestrator / PCMig.Gui，也不在页面里重实现业务规则。
/// </summary>
public sealed partial class Step1ConnectPage : UserControl
{
    private ConnectionViewModel? _vm;

    /// <summary>投影代际（D6.1 §13）：每次"推送 → 回读"自增，使证据能对齐到**当前**状态版本。</summary>
    private long _projectionGeneration;

    public Step1ConnectPage()
    {
        InitializeComponent();
    }

    /// <summary>Shell 注入的共享 ViewModel（唯一实例）。</summary>
    public ConnectionViewModel? Vm
    {
        get => _vm;
        set
        {
            _vm = value;
            // 挂到 DataContext，页面用经典 {Binding} 读它。
            // 原因同 Step2：嵌套路径的 x:Bind 在 UserControl 上不会随后置注入重算，
            // 而这里的 Host/Username/Shares/CanConnect 等若绑不上，Step 1 的输入与共享列表就是死绑定。
            DataContext = value;
            if (_vm is not null)
            {
                _vm.Shares.CollectionChanged += (_, _) => UpdateEmptySharesHint();
                _vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(ConnectionViewModel.IsConnected) or nameof(ConnectionViewModel.Host))
                    {
                        UpdateSourceStatus();
                    }
                };
            }
            UpdateEmptySharesHint();
            UpdateSourceStatus();
        }
    }

    private TextBlock? SourceStatusText2 => null;

    /// <summary>
    /// 当前口令框里的口令（**只读取值**，供 Shell 转交给 Step 2 的预检/传输用；
    /// 本类不保存、不缓存、不写任何存档）。x:Name 生成的字段是 private，
    /// 因此这里给出唯一的对外读取口，Step2 无需也不应直接触碰口令控件。
    /// </summary>
    public string CurrentPassword => PasswordInput.Password;

    private void UpdateSourceStatus()
    {
        // 源状态胶囊属于 Shell 的 ProductHeader（MainWindow 的 SourceStatusText），
        // 页面这里不再重复实现；保留钩子以便后续把胶囊也迁到页面时使用。
    }

    private void UpdateEmptySharesHint()
    {
        var hasShares = _vm is { Shares.Count: > 0 };
        var expectedVisibility = hasShares ? "Collapsed" : "Visible";

        // 先真实推送（原有行为）
        EmptySharesHint.Visibility = hasShares ? Visibility.Collapsed : Visibility.Visible;

        // ★ D6.1 §13 ★ 推送**之后**读**真实控件**：期望值来自投影承诺，实际值来自控件本身。
        ProjectionObserver.ReadControl(
            EmptySharesHint,
            ControlIds.Step1EmptySharesHint,
            "Step1ConnectPage",
            expectedEnabled: null,
            expectedVisibility: expectedVisibility,
            sourceObservationVersion: ++_projectionGeneration);
    }


    /// <summary>
    /// 「连接旧电脑」的**完整动作链**（D3 第一条垂直切片）：
    /// 用户动作 → 既有 guard 结果 → 进入处理 → 反馈期望 → 域层执行（Core 侧 NET/PFL 事件）
    /// → VM 状态 → **控件投影回读** → 反馈确认 → 动作结束。
    ///
    /// 纪律：只观察**原来就已经执行**的判断，不额外调用任何判据、不改任何业务分支。
    /// </summary>
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        // 该分支是**已有的既有判断**（IsConnecting 守卫）；这里只是把它记下来。
        var permitted = _vm is { IsConnecting: false };
        using var trace = ActionTrace.Begin(ActionKinds.Connect, ControlIds.Step1Connect, "click", "Step1ConnectPage");

        trace.Eligibility(permitted, permitted ? "allowed" : "busy:IsConnecting");

        if (permitted)
        {
            trace.Started();
            trace.Expect("connect", "vm-status-change");
            try
            {
                await _vm!.ConnectAsync(PasswordInput.Password);
            }
            catch (Exception ex)
            {
                // VM 内部一般已吞掉异常；这里只兜住真正的漏网异常（记录类型，不落原文）。
                trace.Fault(ex, "ConnectAsync");
                throw;
            }

            // ★ L2 投影回读 ★ 在真实推送完成之后读取**控件本身**的状态。
            //   期望值来自投影承诺（VM 的 guard/状态），实际值来自控件 ⇒ 两者不同才报 mismatch。
            //   连接按钮：动作结束后 VM 已不在连接中，则按钮应当回到可用。
            ProjectionObserver.ReadControl(
                ConnectButton,
                ControlIds.Step1Connect,
                "Step1ConnectPage",
                expectedEnabled: _vm is { IsConnecting: false } or null,
                expectedVisibility: null,
                sourceObservationVersion: ++_projectionGeneration);

            // 共享列表空态：与 UpdateEmptySharesHint 的推送口径一致（再次回读，覆盖"动作后"的当前代际）。
            ProjectionObserver.ReadControl(
                EmptySharesHint,
                ControlIds.Step1EmptySharesHint,
                "Step1ConnectPage",
                expectedEnabled: null,
                expectedVisibility: _vm is { Shares.Count: > 0 } ? "Collapsed" : "Visible",
                sourceObservationVersion: _projectionGeneration);

            if (_vm is { IsConnected: true })
            {
                trace.Confirm("connect", "vm-status-change");
                // ★ D6.1 §2.3 ★ Phase 必须是**稳定 code**，不能塞 UI 展示文字（可能含主机名/路径/用户输入）。
                trace.Complete(DiagnosticOutcome.Succeeded, "connected");
            }
            else
            {
                // 未连上**也是**一个明确结果（不是"没反应"）：结束为 Failed（Phase 用稳定 code）。
                // 人可读的失败说明已经由 VM 侧的状态事件承载，这里不再把展示文字塞进 Phase。
                trace.Complete(DiagnosticOutcome.Failed, "not-connected");
            }

            trace.ProjectionChanged("Vm", 2, "Step1ConnectPage");
            return;
        }

        trace.Reject("busy:IsConnecting", "Step1ConnectPage");
    }

    private async void AddShare_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is { IsConnecting: false })
        {
            await _vm.AddManualShareAsync(PasswordInput.Password);
        }
    }

    /// <summary>密码可见性切换（A3 的眼睛图标）。纯展示层，不读取/不缓存口令。</summary>
    private void TogglePasswordReveal_Click(object sender, RoutedEventArgs e) =>
        PasswordInput.PasswordRevealMode = PasswordInput.PasswordRevealMode == PasswordRevealMode.Visible
            ? PasswordRevealMode.Hidden
            : PasswordRevealMode.Visible;
}