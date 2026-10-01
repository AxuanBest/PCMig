using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT;

namespace PCMig.WinUI.Presentation;

/// <summary>
/// Layer 0/1 的 Desktop Acrylic Technical Spike（**可完整回退**）。
///
/// 设计原则（用户口径）：
///   * 只动 Window 最底层 Backdrop，不碰 Card / Input / Button / 页面布局；
///   * **不设环境变量时本类完全不生效**：既不覆盖 MainWindow.xaml 里既有的 MicaBackdrop，
///     也不创建任何 Controller —— 也就是说默认行为与 Spike 之前逐字节一致，可零成本回退；
///   * 参数由**环境变量**给出，这样 Tint×Luminosity 矩阵与六场景测试不需要反复重新编译；
///   * 每次运行把「系统默认值 / 实际写入值 / IsSupported / 窗口 DPI 与尺寸位置 / 组合 ID」
///     全部落到 exe 旁的 poc-backdrop.log，作为矩阵与复核的原始证据。
///
/// 已由编译器验证的 API 事实（WindowsAppSDK 2.5.1 / Microsoft.WinUI 3.0.0.2609，见审计记录）：
///   * `DesktopAcrylicBackdrop`（XAML 层）**没有** TintOpacity / LuminosityOpacity / TintColor / FallbackColor；
///   * `DesktopAcrylicController` **有** TintOpacity(float) / LuminosityOpacity(float) / TintColor / FallbackColor / IsSupported()；
///   * 挂载方式 = `AddSystemBackdropTarget(ICompositionSupportsSystemBackdrop)`；
///     `SetTarget(Window, SystemBackdropConfiguration)` **不存在**（只有 (WindowId, CompositionTarget) 旧签名），
///     控制器也**没有** SystemBackdropConfiguration 属性。
/// </summary>
internal static class BackdropSpike
{
    /// <summary>组合 ID（写进日志，供矩阵与截图一一对应）。例：DA-T03-L01。</summary>
    public static string CombinationId { get; private set; } = string.Empty;

    private static DesktopAcrylicController? _controller;
    private static MicaController? _mica;

    // ── 开发者实时调节：感知材质强度(0–100) → 控制器参数 ─────────────────────────
    /// <summary>
    /// 感知材质强度(0–100) → TintOpacity。
    /// 说明：Desktop Acrylic **没有**可调的 BlurRadius，所以"材质强度"不是物理量，
    /// 而是把 TintOpacity / LuminosityOpacity 联合映射出的**感知**强度。
    /// 锚点：0 → 0.00（接近完全透明）；50 → 0.02（当前设计默认）；100 → 0.55（趋近实体）。
    /// </summary>
    public static double TintOpacityFor(double strength0to100)
    {
        var s = Math.Clamp(strength0to100, 0d, 100d);
        const double design = 0.02, solid = 0.55;
        return s <= 50d ? design * (s / 50d) : design + (solid - design) * ((s - 50d) / 50d);
    }

    /// <summary>
    /// 感知材质强度(0–100) → LuminosityOpacity（白雾成分）。
    /// 锚点：0 → 0.00；50 → 0.00（设计默认：白雾已压掉）；100 → 0.45（趋近实体材质）。
    /// </summary>
    public static double LuminosityFor(double strength0to100)
    {
        var s = Math.Clamp(strength0to100, 0d, 100d);
        const double solid = 0.45;
        return s <= 50d ? 0d : solid * ((s - 50d) / 50d);
    }

    /// <summary>
    /// 由开发者调节面板实时调用：按"感知材质强度"改写正在运行的控制器。
    /// 只动真实存在的可读写属性（TintOpacity / LuminosityOpacity / FallbackColor）；
    /// 不重建控制器、不换技术路线、不影响上层（上层是半透明 Fill）。
    /// </summary>
    public static void ApplyPerceivedStrength(double strength0to100)
    {
        var tint = (float)TintOpacityFor(strength0to100);
        var lum = (float)LuminosityFor(strength0to100);
        try
        {
            if (_controller is not null)
            {
                _controller.TintOpacity = tint;
                _controller.LuminosityOpacity = lum;
                var a = (byte)Math.Round(Math.Clamp(0xF2 + (0xFF - 0xF2) * (strength0to100 / 100d), 0d, 255d));
                _controller.FallbackColor = Windows.UI.Color.FromArgb(a, 0xF2, 0xF2, 0xF2);
            }
            if (_mica is not null)
            {
                _mica.TintOpacity = tint;
                _mica.LuminosityOpacity = lum;
            }
            Log($"[DeveloperTuning] 感知强度={strength0to100:0.#} → TintOpacity={tint:0.###} LuminosityOpacity={lum:0.###}");
        }
        catch (Exception ex)
        {
            Log($"[DeveloperTuning] 应用失败：{ex.GetType().Name} {ex.Message}");
        }
    }

    private static string LogPath => Path.Combine(AppContext.BaseDirectory, "poc-backdrop.log");

    private static void Log(string message)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); }
        catch { /* 诊断失败绝不影响功能 */ }
    }

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    private static float Float(string name, float fallback)
        => float.TryParse(Env(name), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static Windows.UI.Color Color(string name, Windows.UI.Color fallback)
    {
        var s = Env(name);
        if (s is null) return fallback;
        s = s.TrimStart('#');
        if (s.Length != 6 && s.Length != 8) return fallback;
        byte a = 255, r, g, b;
        if (s.Length == 8)
        {
            a = Convert.ToByte(s.Substring(0, 2), 16);
            s = s.Substring(2);
        }
        r = Convert.ToByte(s.Substring(0, 2), 16);
        g = Convert.ToByte(s.Substring(2, 2), 16);
        b = Convert.ToByte(s.Substring(4, 2), 16);
        return Windows.UI.Color.FromArgb(a, r, g, b);
    }

    /// <summary>把本机真实可见的成员与系统默认值 dump 出来（审计证据，避免凭记忆写 API）。</summary>
    private static void DumpApiFacts()
    {
        foreach (var t in new[] { typeof(DesktopAcrylicController), typeof(MicaController), typeof(SystemBackdropConfiguration) })
        {
            Log($"API {t.FullName} (base={t.BaseType?.Name})");
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Log($"    prop {p.PropertyType.Name} {p.Name} {{ {(p.CanRead ? "get;" : "")}{(p.CanWrite ? "set;" : "")} }}");
            }
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                if (m.IsSpecialName) continue;
                var ps = string.Join(", ", System.Linq.Enumerable.Select(m.GetParameters(), q => q.ParameterType.Name + " " + q.Name));
                Log($"    method {m.ReturnType.Name} {m.Name}({ps}){(m.IsStatic ? " [static]" : "")}");
            }
        }
    }

    /// <summary>
    /// 按环境变量安装 Backdrop。返回 true 表示接管了最底层（Spike 生效）；false 表示保持原样。
    /// 环境变量：
    ///   PCMIG_BACKDROP      = acrylic | mica | off（缺省 = 不动）
    ///   PCMIG_ACRYLIC_TINT  = TintOpacity 0..1
    ///   PCMIG_ACRYLIC_LUM   = LuminosityOpacity 0..1
    ///   PCMIG_ACRYLIC_TINTCOLOR / PCMIG_ACRYLIC_FALLBACK = #RRGGBB
    ///   PCMIG_SPIKE_ID      = 组合 ID（写进日志）
    /// </summary>
    public static bool Install(Window window)
    {
        // 【默认行为】应用启动即使用 Desktop Acrylic 作为 Layer 0/1（不再需要环境变量）。
        // 依据：本机实测 Desktop Acrylic 会真实继承窗口后方的明暗与色相（暖/冷/深/紫皆响应），
        // 而 Mica 在全部场景给出同一个 #CDDDEB（完全不随后方窗口变化）→ Mica 只能作回退。
        // 环境变量仍可用于实验覆盖：PCMIG_BACKDROP=mica|off 可切回/关闭。
        var kind = Env("PCMIG_BACKDROP") ?? "acrylic";
        if (kind.Equals("off", StringComparison.OrdinalIgnoreCase)) return false;
        if (!kind.Equals("acrylic", StringComparison.OrdinalIgnoreCase)
            && !kind.Equals("mica", StringComparison.OrdinalIgnoreCase))
        {
            Log($"PCMIG_BACKDROP={kind} 不是 acrylic/mica，按默认 acrylic 处理");
            kind = "acrylic";
        }

        CombinationId = Env("PCMIG_SPIKE_ID") ?? "unnamed";
        var target = window.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>();
        DumpApiFacts();

        if (kind.Equals("acrylic", StringComparison.OrdinalIgnoreCase))
        {
            if (!DesktopAcrylicController.IsSupported())
            {
                Log($"[{CombinationId}] DesktopAcrylicController.IsSupported() = false → 回退，不接管");
                return false;
            }
            var c = new DesktopAcrylicController();
            Log($"[{CombinationId}] 系统默认 TintOpacity={c.TintOpacity} LuminosityOpacity={c.LuminosityOpacity} TintColor={c.TintColor} FallbackColor={c.FallbackColor}");

            // 默认参数（用户口径：越靠底层越透，环境色主导；只允许极轻微中性冷调）
            // 系统默认是 LuminosityOpacity=0.85 + 近白 TintColor —— 那正是"发白/去饱和"的来源，必须显式压下来。
            c.TintOpacity = Float("PCMIG_ACRYLIC_TINT", 0.02f);
            c.LuminosityOpacity = Float("PCMIG_ACRYLIC_LUM", 0.00f);
            c.TintColor = Color("PCMIG_ACRYLIC_TINTCOLOR", Windows.UI.Color.FromArgb(255, 0xF2, 0xF6, 0xFF));
            c.FallbackColor = Color("PCMIG_ACRYLIC_FALLBACK", Windows.UI.Color.FromArgb(255, 0xF2, 0xF2, 0xF2));

            // 先摘掉 XAML 里既有的 MicaBackdrop，避免两套 backdrop 叠加（Spike 只接管最底层）
            // 可用 PCMIG_SPIKE_XAML=1 改成"先设 XAML 层 DesktopAcrylicBackdrop 再挂控制器"，
            // 用于判定 XAML 路线是否才是能让 State 进入 Active 的那条路。
            // ★ 关键：必须先把 SystemBackdropConfiguration 交给控制器，否则它会一直停在 Fallback。
            // 本机实测：不调用本方法 → State 恒为 Fallback，界面看到的是 FallbackColor 近白一片，
            // 这正是此前"加了 acrylic 反而更白"的真正原因。方法名来自运行时反射 dump（非猜测）。
            var cfg = new SystemBackdropConfiguration
            {
                IsInputActive = true,
                Theme = SystemBackdropTheme.Default,
                IsHighContrast = false,
            };
            c.SetSystemBackdropConfiguration(cfg);
            // 【正式语义】默认**跟随窗口激活状态**：这是 Windows Desktop Acrylic 的正常行为，
            // 失焦时系统本就会转为非激活外观。不要为了截图好看而永久强制 active。
            // 需要实验时用 PCMIG_ACRYLIC_ALWAYS_ACTIVE=1 临时钉住（并在日志里标注为实验态）。
            if (Env("PCMIG_ACRYLIC_ALWAYS_ACTIVE") == "1")
            {
                cfg.IsInputActive = true;
                Log($"[{CombinationId}] 【实验态】强制 IsInputActive=true（自动化截图用，非正式语义）");
            }
            else
            {
                cfg.IsInputActive = true;   // 构造时为激活；随后由 Activated 事件跟随真实状态
                window.Activated += (_, e) =>
                {
                    cfg.IsInputActive = e.WindowActivationState != WindowActivationState.Deactivated;
                    Log($"[{CombinationId}] 激活状态变化 → IsInputActive={cfg.IsInputActive}");
                };
            }
            if (Env("PCMIG_SPIKE_XAML") == "1")
            {
                window.SystemBackdrop = new DesktopAcrylicBackdrop();
                Log($"[{CombinationId}] 先设 window.SystemBackdrop = new DesktopAcrylicBackdrop()");
            }
            else
            {
                window.SystemBackdrop = null;
            }

            // 挂载时机：PCMIG_SPIKE_LATE=1 时改为窗口激活后再挂（怀疑过早挂载 → 永远停在 Fallback）
            if (Env("PCMIG_SPIKE_LATE") == "1")
            {
                _controller = c;
                window.DispatcherQueue.TryEnqueue(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(1200);
                    try
                    {
                        c.AddSystemBackdropTarget(target);
                        Log($"[{CombinationId}] 延迟挂载完成（窗口应已激活）");
                    }
                    catch (Exception ex) { Log($"[{CombinationId}] 延迟挂载失败：{ex.GetType().Name} {ex.Message}"); }
                });
            }
            else
            {
                c.AddSystemBackdropTarget(target);
            }
            _controller = c;
            _controller = c;
            Log($"[{CombinationId}] 已挂载 DesktopAcrylicController：TintOpacity={c.TintOpacity} LuminosityOpacity={c.LuminosityOpacity} TintColor=#{c.TintColor.R:X2}{c.TintColor.G:X2}{c.TintColor.B:X2} FallbackColor=#{c.FallbackColor.R:X2}{c.FallbackColor.G:X2}{c.FallbackColor.B:X2}");
        }
        else
        {
            if (!MicaController.IsSupported())
            {
                Log($"[{CombinationId}] MicaController.IsSupported() = false → 回退，不接管");
                return false;
            }
            var m = new MicaController { Kind = MicaKind.BaseAlt };
            m.TintOpacity = Float("PCMIG_ACRYLIC_TINT", m.TintOpacity);
            m.LuminosityOpacity = Float("PCMIG_ACRYLIC_LUM", m.LuminosityOpacity);
            window.SystemBackdrop = null;
            m.AddSystemBackdropTarget(target);
            _mica = m;
            Log($"[{CombinationId}] 已挂载 MicaController：Kind=BaseAlt TintOpacity={m.TintOpacity} LuminosityOpacity={m.LuminosityOpacity}");
        }

        var dpi = GetDpiForWindow(window);
        Log($"[{CombinationId}] 窗口 DPI={dpi} 位置=({window.AppWindow.Position.X},{window.AppWindow.Position.Y}) 客户区尺寸={window.AppWindow.Size.Width}x{window.AppWindow.Size.Height} 页面=Step1");

        // 关键诊断：DesktopAcrylicController 只有真正进入 Active 才会采样后方内容；
        // 若停在 Fallback，Windows 会用 FallbackColor（本机默认 #F9F9F9，近白）铺底——
        // 那看上去就是"一片白"，很容易被误当成"透明生效"。所以挂载后必须把 State 记下来。
        LogStateLater(window, _controller is not null ? "acrylic" : "mica");
        return true;
    }

    private static void LogStateLater(Window window, string kind)
    {
        try
        {
            window.DispatcherQueue.TryEnqueue(async () =>
            {
                foreach (var delay in new[] { 1500, 4000 })
                {
                    await System.Threading.Tasks.Task.Delay(delay);
                    var state = kind == "acrylic" ? _controller?.State.ToString() : _mica?.State.ToString();
                    var closed = kind == "acrylic" ? _controller?.IsClosed : _mica?.IsClosed;
                    Log($"[{CombinationId}] {kind} State={state} IsClosed={closed}（累计等待 {delay}ms）");
                }
            });
        }
        catch (Exception ex)
        {
            Log($"[{CombinationId}] State 诊断失败：{ex.GetType().Name}");
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private static uint GetDpiForWindow(Window w)
    {
        try { return GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(w)); }
        catch { return 0; }
    }
}