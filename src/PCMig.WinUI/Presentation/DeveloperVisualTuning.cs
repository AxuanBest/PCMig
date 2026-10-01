using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace PCMig.WinUI.Presentation;

/// <summary>
/// Developer Visual Tuning —— 开发者材质实时调节器（**视觉标定工具，不是业务功能**）。
///
/// 目的：把原先写死在 Themes\Materials.xaml 里的材质参数变成可实时拖动的参数，
/// 由人肉眼完成最终视觉标定，不再依赖"截图猜数值"。
///
/// 设计约束（用户口径）：
///   * **不推翻任何既有架构**：Desktop Acrylic Backdrop、Materials.xaml、语义 Token、Layer 0–5、
///     四页 UI、业务逻辑全部保留；
///   * **不重新引入嵌套 Acrylic**：只有窗口最底层是 Desktop Acrylic；上层依旧是半透明 Fill。
///     本类只改**已存在画刷的 Color**（alpha 通道），不替换资源对象、不改模板、不动 Opacity；
///   * 50 = **当前设计默认值**（不是 50% 不透明度）。每层有自己的映射曲线（见 LayerCurve）；
///   * 0 = 极限透明，100 = 极限实体，中间连续插值（标定工具允许出现不适合正式 UI 的极限态）；
///   * 视觉参数与业务参数**完全隔离**：只写 %LocalAppData%\PCMig\DeveloperVisualSettings.json。
///
/// 为什么是"改画刷 Color"而不是"换资源"：四页里的材质引用都是 {StaticResource ...}，
/// 解析后指向同一个画刷实例；替换资源字典里的对象不会让已解析的引用更新。改 Color 才会实时生效。
/// </summary>
public sealed class DeveloperVisualTuning : ObservableObject
{
    public static DeveloperVisualTuning Current { get; } = new();

    // ── 七个连续参数（0–100），默认全部 50 ──────────────────────────────────────
    public const double DesignValue = 50d;

    public double Global    { get => _global;    set { if (Set(ref _global, Clamp(value))) { Apply(); RaiseAll(); } } }
    public double Backdrop  { get => _backdrop;  set { if (Set(ref _backdrop, Clamp(value))) { Apply(); RaiseAll(); } } }
    public double Workspace { get => _workspace; set { if (Set(ref _workspace, Clamp(value))) { Apply(); RaiseAll(); } } }
    public double Card      { get => _card;      set { if (Set(ref _card, Clamp(value))) { Apply(); RaiseAll(); } } }
    public double Inset     { get => _inset;     set { if (Set(ref _inset, Clamp(value))) { Apply(); RaiseAll(); } } }
    public double Control   { get => _control;   set { if (Set(ref _control, Clamp(value))) { Apply(); RaiseAll(); } } }
    public double Primary   { get => _primary;   set { if (Set(ref _primary, Clamp(value))) { Apply(); RaiseAll(); } } }

    private double _global = DesignValue, _backdrop = DesignValue, _workspace = DesignValue,
                   _card = DesignValue, _inset = DesignValue, _control = DesignValue, _primary = DesignValue;

    private static double Clamp(double v) => double.IsNaN(v) ? DesignValue : Math.Round(Math.Clamp(v, 0d, 100d), 1);

    private void RaiseAll()
    {
        Raise(nameof(Global)); Raise(nameof(Backdrop)); Raise(nameof(Workspace)); Raise(nameof(Card));
        Raise(nameof(Inset)); Raise(nameof(Control)); Raise(nameof(Primary));
    }

    /// <summary>各层 token 的**设计默认 alpha**（与 Themes\Materials.xaml 当前值一一对应）。</summary>
    private static readonly (string Key, byte DesignAlpha, string SliderName)[] Layers =
    {
        ("PCMigShellMaterial",      0x2B, "Workspace"),
        ("PCMigWorkspaceMaterial",  0x40, "Workspace"),
        ("PCMigCardMaterial",       0xAF, "Card"),
        ("PCMigInsetMaterial",      0x59, "Inset"),
        ("PCMigControlMaterial",    0xD6, "Control"),
        ("PCMigPrimaryMaterial",    0xF2, "Primary"),
    };

    // ── A26：实际控件材质的接线（用户标注"Inset / Control / Primary 拖动看不出区别"）─────────
    //
    // 已核实的三个原因：
    //   1) Control 原先只改 PCMigControlMaterial，而输入框的面**并不引用它** —— 输入框用的是本项目
    //      Themes\Controls.xaml 自己定义的 TextControlBackground 系列（第 18–21 行），
    //      所以拖 Control 时输入框纹丝不动（只有调节面板自身那 4 个工具按钮会变）。
    //   2) Primary 原先只改 PCMigPrimaryMaterial，而**全仓 XAML 里没有任何元素引用这个 token**
    //      （主按钮用的是 AccentGradientBrush）→ 拖 Primary 在界面上零变化。
    //   3) Global 到 0 / 100 时 GlobalCurve 会把各层最终 alpha 推到 0 / 255，局部滑块于是被"吞掉"，
    //      但界面没有任何提示，看起来就像滑块坏了。
    //
    // 这里把三个滑块接到**界面上真实存在、且用户看得见**的画刷上；设计默认值（50）严格等于
    // Themes\Controls.xaml / Colors.xaml / Materials.xaml 的现值，因此 50 时逐像素与改前一致。
    private static readonly (string Key, byte DesignAlpha, string SliderName)[] Surfaces =
    {
        ("TextControlBackground",             0xC4, "Control"),  // 输入框常态面（渐变末端即最亮一档）
        ("TextControlBackgroundPointerOver",  0xD2, "Control"),  // 悬停态
        ("TextControlBackgroundFocused",      0xE0, "Control"),  // 聚焦态
        ("TextControlBackgroundDisabled",     0x7A, "Control"),  // 禁用态
        ("AccentBrush",                       0xFF, "Primary"),  // 主按钮 / 强调元素的基色
        ("AccentHoverBrush",                  0xFF, "Primary"),
        ("AccentPressedBrush",                0xFF, "Primary"),
    };

    /// <summary>Accent 系列三档原始 RGB（= colors 设计值）：Primary 拖动时按同一方向明暗平移。</summary>
    private static readonly (string Key, byte R, byte G, byte B)[] AccentBase =
    {
        ("AccentBrush",        0x16, 0x77, 0xF2),
        ("AccentHoverBrush",   0x33, 0x8C, 0xFA),
        ("AccentPressedBrush", 0x0D, 0x5F, 0xC8),
    };

    /// <summary>全局滑块处于极值时，各层最终 alpha 会被推到 0 / 255，局部滑块因此不再产生变化。</summary>
    public bool IsGlobalOverriding => _global <= 0d || _global >= 100d;

    /// <summary>某层当前最终 alpha（0–255），供界面提示"谁被全局覆盖"。与 ResolvedAlpha 同源。</summary>
    public int ResolvedSurfaceAlpha(string key)
    {
        foreach (var (k, design, sliderName) in Surfaces)
        {
            if (k != key) continue;
            return (int)Math.Round(Math.Clamp(GlobalCurve(Global, LayerCurve(SliderOf(sliderName), design)), 0d, 255d));
        }
        return ResolvedAlpha(key);
    }

    // ── 映射曲线（三个锚点必须成立：0→0，50→设计值，100→255）────────────────────
    private static double LayerCurve(double slider, byte designAlpha)
    {
        if (slider <= DesignValue) return designAlpha * (slider / DesignValue);
        return designAlpha + (255d - designAlpha) * ((slider - DesignValue) / DesignValue);
    }

    /// <summary>Global：50 时完全不改变各层；向 0 收敛到全透明，向 100 收敛到全实体。</summary>
    private static double GlobalCurve(double global, double alpha)
    {
        if (global <= DesignValue) return alpha * (global / DesignValue);
        return alpha + (255d - alpha) * ((global - DesignValue) / DesignValue);
    }

    private double SliderOf(string name) => name switch
    {
        "Workspace" => Workspace,
        "Card" => Card,
        "Inset" => Inset,
        "Control" => Control,
        "Primary" => Primary,
        "Backdrop" => Backdrop,
        _ => DesignValue,
    };

    /// <summary>某层在当前滑块下的**最终 alpha**（0–255），用于导出与自检。</summary>
    public int ResolvedAlpha(string key)
    {
        foreach (var (k, design, sliderName) in Layers)
        {
            if (k != key) continue;
            var a = LayerCurve(SliderOf(sliderName), design);
            return (int)Math.Round(Math.Clamp(GlobalCurve(Global, a), 0d, 255d));
        }
        return 0;
    }

    /// <summary>Backdrop 的**感知材质强度**(0–100)：先过 Global，再交给 BackdropSpike 转成控制器参数。</summary>
    public double ResolvedBackdropStrength()
    {
        var b = Backdrop;
        if (Global <= DesignValue) return Math.Clamp(b * (Global / DesignValue), 0d, 100d);
        return Math.Clamp(b + (100d - b) * ((Global - DesignValue) / DesignValue), 0d, 100d);
    }

    /// <summary>把当前参数应用到真实 UI（改画刷 Color 的 alpha；Backdrop 交给 BackdropSpike）。</summary>
    public void Apply()
    {
        var app = Application.Current;
        if (app?.Resources is null) return;

        foreach (var (key, design, sliderName) in Layers)
        {
            if (app.Resources.TryGetValue(key, out var res) && res is SolidColorBrush brush)
            {
                var alpha = ResolvedAlpha(key);
                var c = brush.Color;
                brush.Color = Color.FromArgb((byte)alpha, c.R, c.G, c.B);
            }
        }

        ApplySurfaces(app);

        BackdropSpike.ApplyPerceivedStrength(ResolvedBackdropStrength());
    }

    /// <summary>
    /// A26：把 Control / Primary 落到**界面上真实存在**的画刷上（输入框四态面 + Accent 三档）。
    ///   • 输入框面：按设计 alpha 与当前 alpha 的比例缩放**原有 RGB 与 alpha**（不改色相），
    ///     因此 50 时与原值逐位相同；0 时全透明，100 时最实。
    ///   • Accent 三档：alpha 恒为 255，改为在"更亮 ↔ 更暗"方向平移（0 = 提亮 60%，100 = 压暗 45%），
    ///     让主按钮与强调元素给出立即可见的反馈，同时不破坏原有蓝色语言。
    /// 只能用**已存在的画刷实例**改属性（StaticResource 引用已解析，换字典对象不会刷新）；
    /// 输入框常态面是 LinearGradientBrush，按比例改它自己的 GradientStop.Color。
    /// </summary>
    private void ApplySurfaces(Application app)
    {
        foreach (var (key, design, sliderName) in Surfaces)
        {
            if (!app.Resources.TryGetValue(key, out var res)) continue;
            var cur = ResolvedSurfaceAlpha(key);

            switch (res)
            {
                case LinearGradientBrush grad:
                    foreach (var stop in grad.GradientStops)
                    {
                        var baseStop = stop.Color;
                        // 该渐变的设计基准就是它当下的颜色（captured by design alpha），按比例缩 alpha 即可。
                        var a = (byte)Math.Round(Math.Clamp(baseStop.A * (cur / (double)design), 0d, 255d));
                        stop.Color = Color.FromArgb(a, baseStop.R, baseStop.G, baseStop.B);
                    }
                    break;

                case SolidColorBrush solid:
                    solid.Color = Color.FromArgb((byte)cur, solid.Color.R, solid.Color.G, solid.Color.B);
                    break;
            }
        }

        // Accent：色相不变，只做明暗平移。Primary=50 时 factor=0 → 与设计值逐位相同。
        var pf = (_primary - DesignValue) / DesignValue; // -1 … +1
        foreach (var (key, br, bg, bb) in AccentBase)
        {
            if (!app.Resources.TryGetValue(key, out var res) || res is not SolidColorBrush b) continue;
            byte Shift(byte v)
            {
                var t = Math.Abs(pf) * (pf <= 0 ? 0.6 : 0.45); // 负向提亮 60%、正向压暗 45%
                var to = pf <= 0 ? 255d : 0d;
                var mixed = v + (to - v) * t;
                return (byte)Math.Round(Math.Clamp(mixed, 0d, 255d));
            }
            b.Color = Color.FromArgb(255, Shift(br), Shift(bg), Shift(bb));
        }
    }

    // ── 工具动作 ───────────────────────────────────────────────────────────────
    public void ResetToDefaults()
    {
        _global = _backdrop = _workspace = _card = _inset = _control = _primary = DesignValue;
        Apply(); RaiseAll();
    }

    public string ToJson(bool pretty = true) => JsonSerializer.Serialize(BuildExport(), new JsonSerializerOptions { WriteIndented = pretty });

    private Dictionary<string, object> BuildExport()
    {
        var tokens = new Dictionary<string, string>();
        foreach (var (key, _, _) in Layers)
        {
            var a = ResolvedAlpha(key);
            var c = ResolveBrushColor(key);
            tokens[key] = $"#{(byte)a:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        }
        return new Dictionary<string, object>
        {
            ["schemaVersion"] = 1,
            ["kind"] = "PCMig DeveloperVisualSettings（视觉标定，独立于业务配置）",
            ["sliders"] = new Dictionary<string, double>
            {
                ["Global"] = Global, ["Backdrop"] = Backdrop, ["Workspace"] = Workspace, ["Card"] = Card,
                ["Inset"] = Inset, ["Control"] = Control, ["Primary"] = Primary,
            },
            ["resolvedTokens"] = tokens,
            ["resolvedBackdrop"] = new Dictionary<string, object>
            {
                ["perceivedStrength"] = Math.Round(ResolvedBackdropStrength(), 2),
                ["tintOpacity"] = Math.Round(BackdropSpike.TintOpacityFor(ResolvedBackdropStrength()), 4),
                ["luminosityOpacity"] = Math.Round(BackdropSpike.LuminosityFor(ResolvedBackdropStrength()), 4),
            },
        };
    }

    private static Color ResolveBrushColor(string key)
    {
        if (Application.Current?.Resources is { } r && r.TryGetValue(key, out var res) && res is SolidColorBrush b) return b.Color;
        return Color.FromArgb(255, 255, 255, 255);
    }

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCMig", "DeveloperVisualSettings.json");

    public string Save()
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(SettingsPath, ToJson(), new UTF8Encoding(false));
        return SettingsPath;
    }

    /// <summary>启动时加载（存在才加载），加载后立即应用一次。</summary>
    public void LoadIfExists()
    {
        try
        {
            if (!File.Exists(SettingsPath)) { Apply(); return; }
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (doc.RootElement.TryGetProperty("sliders", out var s))
            {
                double Get(string n, double fb) => s.TryGetProperty(n, out var v) && v.TryGetDouble(out var d) ? Clamp(d) : fb;
                _global = Get("Global", DesignValue); _backdrop = Get("Backdrop", DesignValue);
                _workspace = Get("Workspace", DesignValue); _card = Get("Card", DesignValue);
                _inset = Get("Inset", DesignValue); _control = Get("Control", DesignValue);
                _primary = Get("Primary", DesignValue);
            }
            Apply(); RaiseAll();
        }
        catch
        {
            // 配置坏了不该影响界面：回默认值
            ResetToDefaults();
        }
    }
}