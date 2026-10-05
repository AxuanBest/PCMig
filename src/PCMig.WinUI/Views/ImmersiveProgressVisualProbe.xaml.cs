using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PCMig.WinUI.Controls.ImmersiveProgress;
using PCMig.WinUI.Presentation;

namespace PCMig.WinUI.Views;

/// <summary>
/// ★ Round-3 PHASE D（执行书 §26）★ ImmersiveProgressVisualProbe 的代码侧：
/// 确定性真值序列 + 每拍 <c>timeline.csv</c>。
///
/// 这一层刻意**没有**任何业务依赖：它不知道 robocopy / 回执 / 作业状态，只做两件事：
///   1. 按固定时刻表把"已确认真值"喂给 <see cref="ProgressPresentationCoordinator"/>（与生产同一条视觉时间线）；
///   2. 把 <see cref="ProgressPresentationCoordinator.VisualPercent"/> 写到被验收控件上，并把每拍数值落 CSV。
///
/// 于是执行书 §26 的 13 条验收判据全部可以从 CSV 直接算出来（headX 与 visualPercent 的一致性、
/// visualPercent ≤ confirmedPercent、无倒退、无无证据前跳、Push Band 单调且 ≤1 道、Particle 是否越界、
/// Paused/Failed 后动态层是否停止、Holding 时 Head 是否不动而 Band 是否继续、Completed 是否只在 settled 后到 100）。
///
/// 输出目录取环境变量 <c>PCMIG_IMMERSIVE_PROBE_DIR</c>，缺省 <c>E:\PCMigLab\Staging\phI-probe</c>。
/// </summary>
public sealed partial class ImmersiveProgressVisualProbe : UserControl
{
    /// <summary>执行书 §5/§26 采用的计划量（42 GiB），与真机 /Z 场景同量纲。</summary>
    private const long PlannedBytes = 45_097_156_608L;   // 42 GiB

    /// <summary>探针节拍（毫秒）：16 ms ≈ 60 Hz，和 Step3 的呈现节拍一致。</summary>
    private const int TickMs = 16;

    private enum StepAction
    {
        None = 0,
        Freeze = 1,
        ResumeLive = 2,
        Reset = 3,
    }

    private sealed record ProbeStep(
        string Name,
        double RawPercent,
        double Seconds,
        ImmersiveProgressState State,
        StepAction Action,
        bool Settled);

    /// <summary>
    /// 固定序列（执行书 §26）：0 / 10 / 24.8 / 47.3 / 62.3 / 75 / 90 / 100
    /// + Pause（24.8 处冻结）+ Resume + Holding（47.3 处 1.6 s 不给新真值）+ Completed + Failed。
    /// </summary>
    private static readonly ProbeStep[] Steps =
    [
        new("t0",        0d,    0.80d, ImmersiveProgressState.Running,   StepAction.Reset,      false),
        new("t10",       10d,   0.60d, ImmersiveProgressState.Running,   StepAction.None,       false),
        new("t24.8",     24.8d, 0.60d, ImmersiveProgressState.Running,   StepAction.None,       false),
        new("pause",     24.8d, 1.00d, ImmersiveProgressState.Paused,    StepAction.Freeze,     false),
        new("resume",    24.8d, 0.40d, ImmersiveProgressState.Running,   StepAction.ResumeLive, false),
        new("t47.3",     47.3d, 0.60d, ImmersiveProgressState.Running,   StepAction.None,       false),
        new("holding",   47.3d, 1.60d, ImmersiveProgressState.Holding,   StepAction.None,       false),
        new("t62.3",     62.3d, 0.60d, ImmersiveProgressState.Running,   StepAction.None,       false),
        new("t75",       75d,   0.60d, ImmersiveProgressState.Running,   StepAction.None,       false),
        new("t90",       90d,   0.60d, ImmersiveProgressState.Running,   StepAction.None,       false),
        new("t100",      100d,  0.90d, ImmersiveProgressState.Completed, StepAction.None,       true),
        new("failed",    0d,    1.00d, ImmersiveProgressState.Failed,    StepAction.Reset,      false),
        new("idle",      0d,    0.50d, ImmersiveProgressState.Idle,      StepAction.None,       false),
    ];

    private static readonly string[] CsvHeader =
    [
        "tMs", "step", "state", "rawPercent", "effectiveConfirmedPercent", "visualPercent",
        "headX", "bandCenterX", "bandPhase", "bandOpacity", "haloStrength",
        "activeParticles", "activeRipples", "tickCount", "frameMs", "progressWidth", "hostWidth",
        "particleMinX", "particleMaxX",
    ];

    private readonly ProgressPresentationCoordinator _coordinator = new();
    private readonly StringBuilder _row = new(256);
    private readonly Dictionary<string, int> _stepIndex = new(StringComparer.Ordinal);

    private DispatcherTimer? _timer;
    private StreamWriter? _writer;
    private DateTime _startUtc;
    private DateTime _stepStartedUtc;
    private int _currentStep = -1;
    private long _tick;
    private long _rows;
    private string _outDir = string.Empty;
    private string _lastCsvRow = "—";

    public ImmersiveProgressVisualProbe()
    {
        InitializeComponent();
        AssignProbeAutomationIds();

        for (var i = 0; i < Steps.Length; i++)
        {
            _stepIndex[Steps[i].Name] = i;
        }

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// ★ AutomationId 一律在这里赋值，不在 XAML 里写字面量 ★
    /// <para>
    /// 与既有的两个开发探针同写法（<c>Views\GlyphContourProbe.xaml.cs</c>、
    /// <c>Views\MetricTypographyProbe.xaml.cs</c>）。理由：D63 Fixture12 的契约是
    /// 「XAML 字面量 AutomationId ⊆ <c>Diagnostics\ControlIds</c> 登记表」，而那张表是
    /// **生产**真实点击的稳定 ID 表 —— 开发探针的 ID 是取证用的临时锚点，不是生产契约。
    /// 混进登记表会让那张表同时承担两种语义，反而破坏"唯一事实源"。
    /// </para>
    /// <para>
    /// 证明力不降级：这些 ID 仍然出现在真实 UIA 树里（取证脚本 <c>Find-RgAid</c> 照旧按名取到），
    /// 只是不由 XAML 字面量产生。
    /// </para>
    /// </summary>
    private void AssignProbeAutomationIds()
    {
        var ids = new (DependencyObject Element, string Id)[]
        {
            (HeroProgress, "Probe.Hero"),
            (CompactProgress, "Probe.Compact"),
            (EdgeTiny, "Probe.EdgeTiny"),
            (EdgeSmall, "Probe.EdgeSmall"),
            (EdgeFull, "Probe.EdgeFull"),
            (SurfaceHost, "Probe.SurfaceHost"),
            (SurfaceTest, "Probe.SurfaceTest"),
            (StatePaused, "Probe.StatePaused"),
            (StateHolding, "Probe.StateHolding"),
            (StateWarning, "Probe.StateWarning"),
            (StateFailed, "Probe.StateFailed"),
            (QualityHigh, "Probe.QualityHigh"),
            (QualityBalanced, "Probe.QualityBalanced"),
            (QualityReduced, "Probe.QualityReduced"),
            (ProbeStatusText, "Probe.Status"),
            (ProbeDiagText, "Probe.Diag"),
        };

        foreach (var (element, id) in ids)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(element, id);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var configured = Environment.GetEnvironmentVariable("PCMIG_IMMERSIVE_PROBE_DIR");
        _outDir = string.IsNullOrWhiteSpace(configured) ? @"E:\PCMigLab\Staging\phI-probe" : configured;

        try
        {
            Directory.CreateDirectory(_outDir);
            var path = Path.Combine(_outDir, "timeline.csv");
            _writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))
            {
                AutoFlush = true,
            };
            _writer.WriteLine(string.Join(',', CsvHeader));
        }
        catch
        {
            _writer = null;   // 取证失败绝不影响探针本身
        }

        _startUtc = DateTime.UtcNow;
        _stepStartedUtc = _startUtc;
        EnterStep(0);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };
        _timer.Tick += OnTick;
        _timer.Start();

        ProbeStatusText.Text = $"PROBE started dir={_outDir} steps={Steps.Length}";
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_timer is not null)
        {
            _timer.Tick -= OnTick;
            _timer.Stop();
            _timer = null;
        }

        _writer?.Dispose();
        _writer = null;
    }

    private void EnterStep(int index)
    {
        if (index >= Steps.Length)
        {
            return;
        }

        _currentStep = index;
        _stepStartedUtc = DateTime.UtcNow;
        var step = Steps[index];

        switch (step.Action)
        {
            case StepAction.Reset:
                _coordinator.Reset("probe", "probe-reset");
                break;
            case StepAction.Freeze:
                _coordinator.Freeze(_coordinator.VisualPercent > 0d ? _coordinator.VisualPercent : step.RawPercent,
                    DateTime.UtcNow, "probe-pause");
                break;
            case StepAction.ResumeLive:
                _coordinator.ResumeLive(DateTime.UtcNow, "probe-resume");
                break;
        }

        HeroProgress.ProgressState = step.State;
    }

    private void OnTick(object? sender, object e)
    {
        var now = DateTime.UtcNow;
        var step = Steps[_currentStep];

        if ((now - _stepStartedUtc).TotalSeconds >= step.Seconds && _currentStep + 1 < Steps.Length)
        {
            EnterStep(_currentStep + 1);
            step = Steps[_currentStep];
        }

        // 与生产同一条时间线：先接收一拍"已确认真值"（Running/Holding/Completed 才喂），再 Advance 取视觉值
        if (step.State is ImmersiveProgressState.Running or ImmersiveProgressState.Holding
            or ImmersiveProgressState.Completed)
        {
            var bytes = (long)Math.Round(PlannedBytes * step.RawPercent / 100d);
            _coordinator.ApplyTruth(step.RawPercent, bytes, PlannedBytes, step.Settled, "probe", now, "probe");
        }

        var visual = _coordinator.Advance(now);

        // ★ 同源（§16 / R23）★ 被验收控件消费的就是这里返回的同一个 visual
        HeroProgress.Value = visual;
        HeroProgress.ProgressState = step.State;

        _tick++;
        WriteRow(now, step, visual);
        UpdateText(now, step, visual);
    }

    private void WriteRow(DateTime now, ProbeStep step, double visual)
    {
        if (_writer is null)
        {
            return;
        }

        var inv = CultureInfo.InvariantCulture;
        _row.Clear();
        _row.Append(((long)(now - _startUtc).TotalMilliseconds).ToString(inv)).Append(',');
        _row.Append(step.Name).Append(',');
        _row.Append(step.State).Append(',');
        _row.Append(step.RawPercent.ToString("0.###", inv)).Append(',');
        _row.Append(_coordinator.ConfirmedPercent.ToString("0.###", inv)).Append(',');
        _row.Append(visual.ToString("0.###", inv)).Append(',');
        _row.Append(HeroProgress.HeadX.ToString("0.###", inv)).Append(',');
        _row.Append(HeroProgress.BandCenterX.ToString("0.###", inv)).Append(',');
        _row.Append(HeroProgress.BandPhaseSeconds.ToString("0.###", inv)).Append(',');
        _row.Append(HeroProgress.BandOpacity.ToString("0.###", inv)).Append(',');
        _row.Append(HeroProgress.HaloStrength.ToString("0.###", inv)).Append(',');
        _row.Append(HeroProgress.ActiveParticleCount.ToString(inv)).Append(',');
        _row.Append(HeroProgress.ActiveRippleCount.ToString(inv)).Append(',');
        _row.Append(HeroProgress.TickCount.ToString(inv)).Append(',');
        _row.Append(HeroProgress.LastFrameSeconds.ToString("0.####", inv)).Append(',');
        _row.Append(HeroProgress.ProgressWidth.ToString("0.###", inv)).Append(',');
        _row.Append(HeroProgress.Metrics.Width.ToString("0.###", inv)).Append(',');
        _row.Append(HeroProgress.ParticleMinX.ToString("0.###", inv)).Append(',');
        _row.Append(HeroProgress.ParticleMaxX.ToString("0.###", inv));

        var line = _row.ToString();
        _lastCsvRow = line;
        _writer.WriteLine(line);
        _rows++;

        if (_currentStep == Steps.Length - 1 && (now - _stepStartedUtc).TotalSeconds >= Steps[^1].Seconds)
        {
            _writer.WriteLine($"# PROBE_DONE ticks={_tick} rows={_rows}");
            _writer.Flush();

            _timer?.Stop();

            // 离屏自检是异步的（需要先停帧）⇒ probe-done.txt 必须等它写完再落盘，
            // 否则采集脚本会在自检完成前杀掉进程（PHASE D 踩坑）。
            _ = FinishAsync(now);
        }
    }

    private async System.Threading.Tasks.Task FinishAsync(DateTime now)
    {
        await RunOffscreenSelfTestIfRequestedAsync();

        try
        {
            File.WriteAllText(Path.Combine(_outDir, "probe-done.txt"),
                $"ticks={_tick}\nrows={_rows}\ndoneUtc={now:O}\n", new UTF8Encoding(true));
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// PHASE D 取证（§26）：`PCMIG_IMMERSIVE_SELFTEST=1` 时，把若干控件的**当前一帧**分别画进离屏目标并落盘，
    /// 用于把「Renderer 自己的输出颜色」与「屏幕合成后的颜色」分开判定。仅取证，绝不影响探针序列或任何业务值。
    /// </summary>
    private async System.Threading.Tasks.Task RunOffscreenSelfTestIfRequestedAsync()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("PCMIG_IMMERSIVE_SELFTEST"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var targets = new (string Name, ImmersiveTransferProgress Control, double W, double H)[]
        {
            ("hero", HeroProgress, 1010d, 16d),
            ("surface-zero", SurfaceTest, 240d, 16d),
            ("edge-tiny", EdgeTiny, 240d, 16d),
            ("edge-small", EdgeSmall, 240d, 16d),
            ("edge-full", EdgeFull, 240d, 16d),
            ("quality-high", QualityHigh, 240d, 16d),
        };

        var inv = CultureInfo.InvariantCulture;
        foreach (var t in targets)
        {
            try
            {
                var text = await t.Control.OffscreenSelfTestAsync(t.W, t.H);
                File.WriteAllText(Path.Combine(_outDir, $"selftest-{t.Name}.txt"), text, new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(Path.Combine(_outDir, $"selftest-{t.Name}.txt"),
                        "SELFTEST-OUTER-ERROR " + ex.GetType().Name + ": " + ex.Message, new UTF8Encoding(true));
                }
                catch
                {
                    // 忽略
                }
            }
        }

        try
        {
            File.WriteAllText(Path.Combine(_outDir, "selftest-done.txt"),
                string.Concat("controls=", targets.Length.ToString(inv), "\n"), new UTF8Encoding(true));
        }
        catch
        {
            // 忽略
        }
    }

    private void UpdateText(DateTime now, ProbeStep step, double visual)
    {
        var inv = CultureInfo.InvariantCulture;
        ProbeStatusText.Text = string.Concat(
            "PROBE step=", step.Name,
            " state=", step.State,
            " raw=", step.RawPercent.ToString("0.###", inv),
            " confirmed=", _coordinator.ConfirmedPercent.ToString("0.###", inv),
            " visual=", visual.ToString("0.###", inv),
            " headX=", HeroProgress.HeadX.ToString("0.###", inv),
            " bandX=", HeroProgress.BandCenterX.ToString("0.###", inv),
            " bandPhase=", HeroProgress.BandPhaseSeconds.ToString("0.###", inv),
            " bandOpacity=", HeroProgress.BandOpacity.ToString("0.###", inv));

        ProbeDiagText.Text = string.Concat(
            "diag tick=", HeroProgress.TickCount.ToString(inv),
            " frameMs=", (HeroProgress.LastFrameSeconds * 1000d).ToString("0.##", inv),
            " particles=", HeroProgress.ActiveParticleCount.ToString(inv),
            " ripples=", HeroProgress.ActiveRippleCount.ToString(inv),
            " halo=", HeroProgress.HaloStrength.ToString("0.###", inv),
            " canvas=", HeroProgress.IsCanvasAttached,
            "\nlayers=", HeroProgress.LastLayerSummary,
            "\ncoord=", _coordinator.Describe(),
            "\ncsv=", _lastCsvRow,
            "\nelapsedSec=", (now - _startUtc).TotalSeconds.ToString("0.0", inv));
    }
}