using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace PCMig.WinUI.Controls.ImmersiveProgress;

/// <summary>
/// ★ Round-3（执行书 §24）★ 无障碍对等体：自定义 Canvas 控件**不能**让 UIA 只看到一个 Canvas，
/// 必须以 ProgressBar / RangeValue 语义暴露。
///
/// 硬规则：
///   · <c>Minimum = 0</c> / <c>Maximum = 100</c> / <c>Value = 当前视觉百分比</c>
///     （与屏幕上的大号百分比、进度条来自**同一个** <c>VisualProgress</c>，所以读屏者听到的数字与眼睛看到的一致）；
///   · 名字形如「迁移总进度 56.3%」；
///   · 屏幕阅读器**不读取** BandPhase / 粒子 / Ripple / Glow —— 它们是纯视觉层，不承载任何业务含义；
///   · 动效关闭（Reduced Motion）时 <c>Value</c> 与语义**完全不变**（§20 / R29）。
/// </summary>
internal sealed class ImmersiveTransferProgressAutomationPeer : FrameworkElementAutomationPeer, IRangeValueProvider
{
    public ImmersiveTransferProgressAutomationPeer(ImmersiveTransferProgress owner)
        : base(owner)
    {
    }

    private ImmersiveTransferProgress Progress => (ImmersiveTransferProgress)Owner;

    /// <summary>
    /// 值变化时由控件调用（Value / Maximum 变化时）。
    /// ★ 踩坑记录（Round-3 PHASE C）★ WinUI 3 的 <see cref="IRangeValueProvider"/> **没有**
    /// <c>RangeValueChanged</c> 事件（用反射核对过 Microsoft.WinUI 2.3.9：接口只有 SetValue + 六个属性），
    /// 因此值变化必须用标准的 <see cref="AutomationPeer.RaisePropertyChangedEvent"/> 上报。
    /// </summary>
    internal void RaiseValueChanged()
    {
        var value = Progress.Value;
        if (Math.Abs(value - _lastRaisedValue) < 0.05d) return;
        var oldValue = _lastRaisedValue;
        _lastRaisedValue = value;
        if (ListenerExists(AutomationEvents.PropertyChanged))
        {
            RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, oldValue, value);
        }
    }

    private double _lastRaisedValue;

    protected override string GetClassNameCore() => nameof(ImmersiveTransferProgress);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ProgressBar;

    protected override string GetNameCore()
    {
        var value = Progress.Value;
        return $"迁移总进度 {value:0.0}%";
    }

    protected override object? GetPatternCore(PatternInterface patternInterface)
        => patternInterface == PatternInterface.RangeValue
            ? this
            : base.GetPatternCore(patternInterface);

    // ── IRangeValueProvider：只读（进度由业务真值驱动，外部不得设置）────────────────
    public bool IsReadOnly => true;

    public double LargeChange => 0d;

    public double SmallChange => 0d;

    public double Maximum => Progress.Maximum;

    public double Minimum => Progress.Minimum;

    public double Value => Progress.Value;

    /// <summary>进度条是只读的：忽略任何外部写入（不得让辅助技术"改"迁移进度）。</summary>
    public void SetValue(double value)
    {
    }
}