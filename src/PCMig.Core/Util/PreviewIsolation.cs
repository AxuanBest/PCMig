using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCMig.Core.Util;

/// <summary>
/// ★ 2026-10-15（v0.5.4 Preview.2 安装器数据隔离整改）★
/// **预览版运行数据隔离配置**：随预览产物 app 目录分发的 <c>preview-isolation.json</c>。
///
/// ── 为什么需要它 ────────────────────────────────────────────────────────────
/// 此前预览安装器用 <c>HKCU\Environment</c> 持久化写 <c>PCMIG_JOBS</c> / <c>PCMIG_LOG_DIR</c>
/// 来"隔离"运行数据。那是**账户级**配置：该用户下**所有** PCMig 进程（正式 v0.5.3、已装
/// Preview.1、任何新实例）都会继承，于是正式版的作业与日志被静默改向预览目录。
/// 工具选错了 —— 进程级隔离不该用账户级旋钮实现。
///
/// 本类改用**随产物分发、按 <see cref="AppContext.BaseDirectory"/> 定位**的配置文件：
///   · 只有预览产物自己的 app 目录里才有这个文件 ⇒ 正式产物天然不受影响；
///   · 与"怎么启动"无关（双击 EXE / 快捷方式 / 安装后启动 / CLI 全部读同一份）；
///   · 卸载时随安装目录消失，**零账户残留**。
///
/// ── Fail Closed（本类的核心合同）────────────────────────────────────────────
/// 判定"是否处于预览模式"只看**产物身份**，不看文件是否存在：
///   产物是预览身份（程序集 <c>AssemblyInformationalVersion</c> 含 <c>-preview</c> 标记）：
///     · 配置文件存在且合法  ⇒ <see cref="IsActive"/> = true，用配置里的数据根；
///     · 配置文件缺失/损坏/指向正式根/不可写 ⇒ **<see cref="Failure"/> 非空，必须阻止启动**，
///       绝不静默回退到 <c>%ProgramData%\PCMig</c>。
///   产物是正式身份（无 preview 标记）：
///     · 完全不读该配置 ⇒ <see cref="IsActive"/> = false，**生产路径逐位不变**。
///       即使目录里被人误放了一个 preview-isolation.json 也不会改变正式版行为。
///
/// ── 为什么不复用环境变量 ────────────────────────────────────────────────────
/// <c>PCMIG_JOBS</c> / <c>PCMIG_LOG_DIR</c> 的**显式用户覆盖能力必须保留**（便携场景、测量隔离、
/// 既有测试都依赖它）。本类只在**预览身份**下参与定序，且优先级**高于**环境变量：
/// 预览产物若允许环境变量反超，就又会退回"账户级变量能改写预览行为"的老问题。
/// 正式身份下本类完全不介入，环境变量语义逐位不变。
/// </summary>
public static class PreviewIsolation
{
    /// <summary>随预览产物分发的配置文件名。</summary>
    public const string FileName = "preview-isolation.json";

    /// <summary>产物预览身份标记：<c>AssemblyInformationalVersion</c> 含此子串即视为预览产物。</summary>
    public const string PreviewMarker = "-preview";

    /// <summary>
    /// 发布候选身份标记：<c>AssemblyInformationalVersion</c> 含 <c>-rc.</c> 的产物同样按"隔离身份"处理。
    ///
    /// 理由（不是放宽，而是补一个缺口）：rc.1 与正式 v0.5.3 必须**并存安装**（独立 AppId / 独立安装目录），
    /// 若 rc 仍按正式身份跑，它会把运行数据写进 <c>%LocalAppData%\PCMig</c> —— 与用户已在用的 v0.5.3
    /// 共用同一份 Jobs/Settings/Diagnostics，等于让候选版污染正式版数据。这属于安装安全，不是产品行为变化：
    /// 正式发布件（如 <c>0.5.4</c>）不含该标记，生产路径逐位不变。
    /// </summary>
    public const string ReleaseCandidateMarker = "-rc.";

    /// <summary>配置 schema 版本。不匹配即视为无效配置（Fail Closed）。</summary>
    public const int SupportedSchemaVersion = 1;

    private static readonly object Gate = new();
    private static bool _probed;
    private static PreviewIsolationState _state = PreviewIsolationState.NotPreview;

    /// <summary>
    /// 当前进程的隔离状态。<c>IsActive</c> 为 true 时，<see cref="DataRoot"/> 是唯一合法数据根。
    /// </summary>
    public static PreviewIsolationState State
    {
        get
        {
            EnsureProbed();
            return _state;
        }
    }

    /// <summary>是否处于**有效的**预览隔离模式（配置合法、路径可用）。</summary>
    public static bool IsActive => State.IsActive;

    /// <summary>预览数据根（仅在 <see cref="IsActive"/> 时非空）。</summary>
    public static string? DataRoot => State.DataRoot;

    /// <summary>
    /// Fail Closed 失败原因。非空表示"产物是预览身份但隔离不可用" ⇒ 调用方**必须阻止**
    /// 启动或任何持久化写入，并把该文案展示给用户。
    /// </summary>
    public static string? Failure => State.Failure;

    /// <summary>
    /// 子目录解析（Jobs / Logs / Diagnostics / Settings / Exports）。仅在 <see cref="IsActive"/> 时有意义。
    /// </summary>
    public static string? SubDirectory(string name)
        => IsActive && DataRoot is not null ? Path.Combine(DataRoot, name) : null;

    /// <summary>
    /// ★ 2026-10-15（安装前最后一道 Fail Closed 核验）★
    /// **持久化入口的防回退闸门**：任何要落地到"运行数据根"的调用方，在准备回退到生产默认路径之前
    /// 必须先过这里。
    ///
    /// ── 为什么在已有启动检查之外还要这一道 ──────────────────────────────────
    /// <see cref="IsActive"/> == false 其实涵盖**两种语义完全相反**的状态：
    ///   (a) 产物是正式身份 ⇒ 用生产路径**正确**；
    ///   (b) 产物是预览身份但隔离失败（<see cref="Failure"/> 非空）⇒ 用生产路径**就是污染**。
    /// 仅靠"启动时检查一次 Failure"是**单点防御**：任何新增的入口（未来多的一个 exe、一个后台服务、
    /// 一段先于 OnLaunched 的初始化，或某个调用方漏抄那三行检查）都会静默落进
    /// <c>%ProgramData%\PCMig</c>。所以把判据下沉到**写入点本身**，让"漏检"不可能发生。
    ///
    /// <paramref name="consumer"/> 只用于错误文案（例如 "JobManager.JobsRoot"），便于定位是谁触发的。
    /// </summary>
    /// <exception cref="PreviewIsolationUnavailableException">
    /// 预览身份 + 隔离不可用时抛出。**绝不返回生产路径**。
    /// </exception>
    public static void ThrowIfPreviewIsolationBroken(string consumer)
    {
        if (Failure is { } failure)
        {
            throw new PreviewIsolationUnavailableException(consumer, failure);
        }
    }

    /// <summary>
    /// 便捷组合：返回隔离子目录；若处于"预览身份 + 隔离失败"则抛异常；否则返回 null（调用方走生产路径）。
    ///
    /// 调用方写法：
    /// <code>
    /// if (PreviewIsolation.TryResolveForPersistence("Logs", "LogBootstrap.AppLogDir") is { } iso)
    ///     return iso;
    /// // 只有 (a) 正式身份才会走到这里 —— (b) 已在上面抛掉
    /// return 生产默认路径;
    /// </code>
    /// </summary>
    public static string? TryResolveForPersistence(string subDirectory, string consumer)
    {
        ThrowIfPreviewIsolationBroken(consumer);
        return SubDirectory(subDirectory);
    }

    /// <summary>
    /// 测试缝：重置探测缓存。生产代码不得调用。
    /// </summary>
    internal static void ResetForTest() { lock (Gate) { _probed = false; _state = PreviewIsolationState.NotPreview; } }

    /// <summary>
    /// 测试缝：用指定的配置文本与身份标志直接求值，绕过文件系统与程序集探测。
    /// 生产代码不得调用。
    /// </summary>
    internal static PreviewIsolationState EvaluateForTest(string? configJson, bool isPreviewIdentity, string? expectedRootOverride = null)
        => Evaluate(configJson, isPreviewIdentity, expectedRootOverride);

    private static void EnsureProbed()
    {
        if (_probed) return;
        lock (Gate)
        {
            if (_probed) return;

            var isPreview = CurrentAssemblyIsPreviewIdentity();
            string? json = null;
            if (isPreview)
            {
                var path = Path.Combine(AppContext.BaseDirectory, FileName);
                try
                {
                    if (File.Exists(path)) json = File.ReadAllText(path, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    // 读不到就当"缺失" ⇒ Fail Closed，而不是放行。
                    json = null;
                    _state = PreviewIsolationState.Failed(
                        $"预览隔离配置读取失败：{path}（{ex.GetType().Name}）。已阻止启动以免写进正式数据根。");
                    _probed = true;
                    return;
                }
            }

            _state = Evaluate(json, isPreview);
            _probed = true;
        }
    }

    /// <summary>
    /// 探测当前程序集是否为预览身份。
    ///
    /// 判据用 <c>AssemblyInformationalVersion</c>（构建期由 <c>-p:InformationalVersion=</c> 注入，
    /// 见 <c>tools\preview-build.ps1</c>）而不是文件名或目录名 —— 目录名可以被随手改，
    /// 程序集元数据不能。
    /// </summary>
    /// <summary>
    /// 产物身份是否属于"隔离运行数据"的一类（预览 <c>-preview</c> 或发布候选 <c>-rc.</c>）。
    /// 判据只看程序集 <c>AssemblyInformationalVersion</c>，不看文件名/目录名。
    /// </summary>
    internal static bool IsIsolatedIdentity(string? informationalVersion)
        => !string.IsNullOrEmpty(informationalVersion)
           && (informationalVersion!.IndexOf(PreviewMarker, StringComparison.OrdinalIgnoreCase) >= 0
               || informationalVersion!.IndexOf(ReleaseCandidateMarker, StringComparison.OrdinalIgnoreCase) >= 0);
    private static bool CurrentAssemblyIsPreviewIdentity()
    {
        try
        {
            var asm = typeof(PreviewIsolation).Assembly;
            var info = System.Reflection.CustomAttributeExtensions
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(asm);
            if (IsIsolatedIdentity(info?.InformationalVersion)) return true;
        }
        catch { /* 取不到元数据 ⇒ 按正式身份处理（最保守） */ }
        return false;
    }

    private static PreviewIsolationState Evaluate(string? configJson, bool isPreviewIdentity, string? expectedRootOverride = null)
    {
        if (!isPreviewIdentity) return PreviewIsolationState.NotPreview;

        var expectedRoot = expectedRootOverride ?? ExpectedPreviewRoot();

        if (string.IsNullOrWhiteSpace(configJson))
        {
            return PreviewIsolationState.Failed(
                $"预览版缺少隔离配置 {FileName}（应在程序目录 {AppContext.BaseDirectory} 下由安装包分发）。" +
                "已阻止启动：不允许回退到正式数据根 %ProgramData%\\PCMig。请重新安装预览包。");
        }

        PreviewIsolationConfig? cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<PreviewIsolationConfig>(configJson!, JsonOptions);
        }
        catch (Exception ex)
        {
            return PreviewIsolationState.Failed(
                $"预览隔离配置格式非法（{ex.GetType().Name}）：{FileName}。" +
                "已阻止启动以免写进正式数据根。");
        }

        if (cfg is null)
        {
            return PreviewIsolationState.Failed($"预览隔离配置为空：{FileName}。已阻止启动。");
        }

        if (cfg.SchemaVersion != SupportedSchemaVersion)
        {
            return PreviewIsolationState.Failed(
                $"预览隔离配置 schemaVersion={cfg.SchemaVersion} 不受支持（本程序要求 {SupportedSchemaVersion}）。已阻止启动。");
        }

        if (string.IsNullOrWhiteSpace(cfg.DataRoot))
        {
            return PreviewIsolationState.Failed($"预览隔离配置未指定 dataRoot：{FileName}。已阻止启动。");
        }

        string root;
        try
        {
            var raw = Environment.ExpandEnvironmentVariables(cfg.DataRoot!.Trim());

            // ── ★ 全限定性必须在 GetFullPath **之前**判定 ★ ──────────────────────
            // Path.GetFullPath("relative\\x") 会以**进程当前目录**为基准把它展开成一条绝对路径，
            // 于是"相对路径"这条错误事实被静默抹掉：轻则错误信息误导（报成"位于程序目录内"），
            // 重则 ..\\..\\escape 展开后逃出后续所有包含性检查而被判为合法。
            // 配置里的 dataRoot 必须是**本机绝对路径**：先拒相对、再拒 UNC，然后才规范化。
            if (!Path.IsPathFullyQualified(raw))
            {
                return PreviewIsolationState.Failed(
                    $"预览隔离配置的 dataRoot 必须是本机绝对路径（收到相对路径：{cfg.DataRoot}）。" +
                    "相对路径会随进程当前目录变化，可能指向正式数据目录。已阻止启动。");
            }

            if (raw.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return PreviewIsolationState.Failed(
                    $"预览隔离配置的 dataRoot 不支持 UNC 网络路径：{cfg.DataRoot}。" +
                    "网络路径不受本机卸载清理约束。已阻止启动。");
            }

            root = Path.GetFullPath(raw);
        }
        catch (Exception ex)
        {
            return PreviewIsolationState.Failed(
                $"预览隔离配置的 dataRoot 不是合法路径（{ex.GetType().Name}）：{cfg.DataRoot}。已阻止启动。");
        }

        // ── 拒绝正式数据根：这是本类存在的全部理由 ─────────────────────────────
        foreach (var forbidden in ForbiddenRoots())
        {
            if (IsSameOrUnder(root, forbidden))
            {
                return PreviewIsolationState.Failed(
                    $"预览隔离配置的 dataRoot 指向正式数据根（{root} ⊆ {forbidden}）。" +
                    "已阻止启动：预览版不得写入正式版数据目录。");
            }
        }

        // ── 拒绝安装目录：配置不得把数据写进程序目录（升级/卸载会连带删除用户数据）──
        if (IsSameOrUnder(root, AppContext.BaseDirectory))
        {
            return PreviewIsolationState.Failed(
                $"预览隔离配置的 dataRoot 位于程序目录内（{root}）。" +
                "已阻止启动：数据根不得在安装目录下（升级与卸载会连带删除迁移成果）。");
        }

        // ── 目录可用性：能建、能写 ────────────────────────────────────────────
        try
        {
            Directory.CreateDirectory(root);
            var probe = Path.Combine(root, ".write-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok", Encoding.UTF8);
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            return PreviewIsolationState.Failed(
                $"预览隔离数据根不可用（{ex.GetType().Name}）：{root}。" +
                "已阻止启动：预览版不得回退到正式数据根。");
        }

        // ── 与预期根对照：防止"配置被改指到别处"后仍宣称隔离 ──────────────────
        var mismatch = expectedRoot is not null && !PathsEqual(root, expectedRoot)
            ? $"（注意：dataRoot={root} 与按本机用户推导的预期根 {expectedRoot} 不一致，请确认这是有意为之。）"
            : string.Empty;

        return PreviewIsolationState.Active(root, mismatch);
    }

    /// <summary>按本机用户推导的预期预览数据根：<c>%LocalAppData%\PCMig-Preview2</c>。</summary>
    public static string ExpectedPreviewRoot()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCMig-Preview2");

    /// <summary>正式数据根清单：预览配置**不得**指向其中任何一个或其子目录。</summary>
    private static IEnumerable<string> ForbiddenRoots()
    {
        string common;
        string local;
        try { common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData); }
        catch { common = string.Empty; }
        try { local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); }
        catch { local = string.Empty; }

        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(common))
        {
            roots.Add(Path.Combine(common, "PCMig"));                 // %ProgramData%\PCMig
            roots.Add(Path.Combine(common, "PCMig-Preview2"));       // 也不该往 ProgramData 放预览数据
        }
        if (!string.IsNullOrWhiteSpace(local))
        {
            roots.Add(Path.Combine(local, "PCMig"));                  // %LocalAppData%\PCMig（正式版设置/诊断根）
        }
        return roots;
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <summary><paramref name="candidate"/> 是否等于 <paramref name="ancestor"/> 或位于其下。</summary>
    private static bool IsSameOrUnder(string candidate, string ancestor)
    {
        if (string.IsNullOrWhiteSpace(ancestor)) return false;
        try
        {
            var c = Path.GetFullPath(candidate)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var a = Path.GetFullPath(ancestor)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return c.StartsWith(a, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>配置文件的严格 schema。</summary>
    private sealed class PreviewIsolationConfig
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }

        [JsonPropertyName("dataRoot")] public string? DataRoot { get; set; }
    }
}

/// <summary>
/// 预览隔离求值结果。
/// <see cref="Failure"/> 非空 ⇒ 产物是预览身份但隔离不可用 ⇒ 调用方必须 Fail Closed。
/// </summary>
public sealed class PreviewIsolationState
{
    private PreviewIsolationState(bool isActive, string? dataRoot, string? failure, string? note)
    {
        IsActive = isActive;
        DataRoot = dataRoot;
        Failure = failure;
        Note = note;
    }

    /// <summary>是否为有效预览隔离模式。</summary>
    public bool IsActive { get; }

    /// <summary>预览数据根（仅 <see cref="IsActive"/> 时非空）。</summary>
    public string? DataRoot { get; }

    /// <summary>非空 ⇒ 必须 Fail Closed。</summary>
    public string? Failure { get; }

    /// <summary>非致命的提示（例如 dataRoot 与预期不一致），仅用于日志与报告。</summary>
    public string? Note { get; }

    /// <summary>正式身份：不介入任何路径解析，生产语义逐位不变。</summary>
    public static PreviewIsolationState NotPreview { get; } = new(false, null, null, null);

    internal static PreviewIsolationState Active(string dataRoot, string? note)
        => new(true, dataRoot, null, string.IsNullOrEmpty(note) ? null : note);

    internal static PreviewIsolationState Failed(string failure)
        => new(false, null, failure, null);

    /// <summary>供日志/UI 展示的一行摘要。</summary>
    public override string ToString()
        => IsActive ? $"PreviewIsolation: active, dataRoot={DataRoot}"
                    : Failure is not null ? $"PreviewIsolation: FAILED — {Failure}"
                    : "PreviewIsolation: not a preview build";
}

/// <summary>
/// ★ 2026-10-15（安装前最后一道 Fail Closed 核验）★
/// 预览产物在隔离不可用（配置缺失/损坏/指向正式根/不可写）时，任何试图解析**运行数据根**的
/// 调用都会抛本异常，而不是静默回退到 <c>%ProgramData%\PCMig</c>。
///
/// 这是**纵深防御**的第二层：第一层是进程入口（WinUI <c>App.OnLaunched</c> / CLI <c>Program.Main</c>）
/// 在早期就检查 <see cref="PreviewIsolation.Failure"/> 并拒绝启动；本异常保证即使某个入口漏检、
/// 或将来新增了别的可执行文件/后台路径，也**不可能**写进正式版数据目录。
///
/// 用专门的类型而不是 <c>InvalidOperationException</c>：调用方与测试可以精确识别，
/// 且不会被"catch 一切"的通用兜底悄悄吞掉后继续跑。
/// </summary>
public sealed class PreviewIsolationUnavailableException : Exception
{
    public PreviewIsolationUnavailableException(string consumer, string failure)
        : base($"预览版数据隔离不可用，已拒绝为 {consumer} 解析运行数据根，以免写入正式版数据目录。原因：{failure}")
    {
        Consumer = consumer;
        Failure = failure;
    }

    /// <summary>触发点标识（例如 <c>JobManager.JobsRoot</c>）。</summary>
    public string Consumer { get; }

    /// <summary>原始失败原因，用于展示给用户。</summary>
    public string Failure { get; }
}
