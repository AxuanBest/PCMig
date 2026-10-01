using System;
using System.IO;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PCMig.WinUI.Presentation;
using Windows.ApplicationModel.DataTransfer;

namespace PCMig.WinUI.Views;

/// <summary>
/// Developer Visual Tuning 面板。
/// 只做三件事：把 Settings 的 7 个值推到 Slider、把 Slider 变化写回 Settings、以及四个工具动作。
/// 全部视觉计算都在 <see cref="DeveloperVisualTuning"/> 里（映射曲线唯一来源），本面板不含任何材质逻辑。
/// </summary>
public sealed partial class DeveloperTuningPanel : UserControl
{
    private DeveloperVisualTuning? _settings;
    private bool _pushing;   // 防回环：把值推到 Slider 时会触发 ValueChanged

    public event EventHandler? CloseRequested;

    public DeveloperTuningPanel()
    {
        InitializeComponent();
    }

    /// <summary>由 Shell 注入唯一实例并同步一次当前值。</summary>
    public void Attach(DeveloperVisualTuning settings)
    {
        _settings = settings;
        PushValues();
    }

    private void PushValues()
    {
        if (_settings is null) return;
        _pushing = true;
        try
        {
            GlobalSlider.Value = _settings.Global;
            BackdropSlider.Value = _settings.Backdrop;
            WorkspaceSlider.Value = _settings.Workspace;
            CardSlider.Value = _settings.Card;
            InsetSlider.Value = _settings.Inset;
            ControlSlider.Value = _settings.Control;
            PrimarySlider.Value = _settings.Primary;
            UpdateValueLabels();
        }
        finally { _pushing = false; }
    }

    private void UpdateValueLabels()
    {
        GlobalValue.Text = $"{GlobalSlider.Value:0}";
        BackdropValue.Text = $"{BackdropSlider.Value:0}";
        WorkspaceValue.Text = $"{WorkspaceSlider.Value:0}";
        CardValue.Text = $"{CardSlider.Value:0}";
        InsetValue.Text = $"{InsetSlider.Value:0}";
        ControlValue.Text = $"{ControlSlider.Value:0}";
        PrimaryValue.Text = $"{PrimarySlider.Value:0}";

        // A26：全局处于 0 / 100 时，GlobalCurve 会把各层最终值推到全透明 / 全不透明，
        // 此时下面六个分层滑块不再产生视觉变化 —— 必须显式告知，否则会被当成"滑块坏了"。
        var overridden = _settings?.IsGlobalOverriding ?? false;
        GlobalOverrideNotice.Visibility = overridden ? Visibility.Visible : Visibility.Collapsed;
        if (overridden)
        {
            GlobalOverrideTitle.Text = GlobalSlider.Value <= 0
                ? "全局强度 = 0，各分层已被推成全透明"
                : "全局强度 = 100，各分层已被推成全不透明";
        }
    }

    /// <summary>拖动立即生效：写回 Settings，由 Settings.Apply() 实时改画刷 alpha 与 Backdrop 控制器参数。</summary>
    private void Slider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_pushing || _settings is null) return;
        switch (((FrameworkElement)sender).Name)
        {
            case nameof(GlobalSlider): _settings.Global = e.NewValue; break;
            case nameof(BackdropSlider): _settings.Backdrop = e.NewValue; break;
            case nameof(WorkspaceSlider): _settings.Workspace = e.NewValue; break;
            case nameof(CardSlider): _settings.Card = e.NewValue; break;
            case nameof(InsetSlider): _settings.Inset = e.NewValue; break;
            case nameof(ControlSlider): _settings.Control = e.NewValue; break;
            case nameof(PrimarySlider): _settings.Primary = e.NewValue; break;
        }
        UpdateValueLabels();
        StatusText.Text = $"已应用（未保存）：全局 {_settings.Global:0} · 窗口背景 {_settings.Backdrop:0} · 外壳与工作区 {_settings.Workspace:0} · 内容卡片 {_settings.Card:0} · 内嵌区域 {_settings.Inset:0} · 输入框与控件面 {_settings.Control:0} · 主操作强调色 {_settings.Primary:0}";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _settings?.ResetToDefaults();
        PushValues();
        StatusText.Text = "已恢复全部默认值（全部 50）。";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        try
        {
            var dp = new DataPackage();
            dp.SetText(_settings.ToJson());
            Clipboard.SetContent(dp);
            StatusText.Text = "已复制当前参数 JSON 到剪贴板。";
        }
        catch (Exception ex) { StatusText.Text = "复制失败：" + ex.Message; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        try { StatusText.Text = "已保存：" + _settings.Save(); }
        catch (Exception ex) { StatusText.Text = "保存失败：" + ex.Message; }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        try
        {
            var dir = Path.GetDirectoryName(DeveloperVisualTuning.SettingsPath)!;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "DeveloperVisualSettings.export.json");
            File.WriteAllText(path, _settings.ToJson(), new UTF8Encoding(false));
            StatusText.Text = "已导出：" + path;
        }
        catch (Exception ex) { StatusText.Text = "导出失败：" + ex.Message; }
    }
}