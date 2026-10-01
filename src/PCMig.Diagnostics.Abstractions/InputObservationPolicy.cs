namespace PCMig.Diagnostics.Abstractions;

/// <summary>
/// 输入来源节点（叶子 → 根方向的祖先链的一项）。**UI 无关**：
/// WinUI 适配器负责从可视树构建它，契约层只做判定，因此隐私边界可以被任何测试项目直接行为验证。
/// </summary>
public readonly record struct InputSourceNode(string TypeName, string? AutomationId);

/// <summary>
/// 输入观察**类别**。只有语义类别，**永不包含按键身份**（D6.1 §1）。
/// 类别名固定、可枚举、与具体字符无关；这是"记录类别而不是字符"在类型层面的落地。
/// </summary>
public static class InputCategories
{
    public const string PointerPressed = "pointer-pressed";
    public const string Tapped = "tapped";

    public const string KeyTab = "key-tab";
    public const string KeyActivation = "key-activation";
    public const string KeyEscape = "key-escape";
    public const string KeyDirectional = "key-directional";
    public const string KeyPageNavigation = "key-page-navigation";
    public const string KeyShortcut = "key-shortcut";
    public const string KeyOtherNonText = "key-other-nontext";

    /// <summary>观测生命周期标记（不是输入类别，但同属该 payload 的合法取值）。</summary>
    public const string LifecycleStarted = "deep-trace-started";
    public const string LifecycleStopped = "deep-trace-stopped";

    /// <summary>全部允许出现的类别（白名单；不在其中的字符串一律不得进入事件）。</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        PointerPressed, Tapped,
        KeyTab, KeyActivation, KeyEscape, KeyDirectional, KeyPageNavigation, KeyShortcut, KeyOtherNonText,
        LifecycleStarted, LifecycleStopped,
    };

    public static bool IsKnown(string? category) =>
        category is not null && ((IReadOnlyList<string>)All).Contains(category);
}

/// <summary>
/// Deep Trace 输入观察的**纯策略**（D6.1 §1：P0 隐私边界）。
///
/// 两条不可协商的规则：
///   1. **按键只归类，不记身份**：<see cref="ClassifyKey"/> 的返回值只能取自
///      <see cref="InputCategories"/> 的固定集合 —— 因此它在结构上**无法**携带 "A"/"B"/"3" 这类身份；
///   2. **敏感来源直接丢弃**：只要祖先链里出现 PasswordBox（或与密码/凭据有关的稳定 ControlId），
///      该输入事件**整体丢弃**（连 ControlId 都不解析、不发布），且不记录长度/文本/IME/剪贴板。
///      判定必须**走完整条祖先链**，不能用祖先的其它 AutomationId 绕过去。
/// </summary>
public static class InputObservationPolicy
{
    /// <summary>祖先链的最大深度（有界；`PointerPressed` 在深层嵌套里也够用）。</summary>
    public const int MaxAncestryDepth = 32;

    /// <summary>敏感控件类型名（不含命名空间）。比较为不区分大小写的"包含"。</summary>
    private static readonly string[] SensitiveTypeNames = { "PasswordBox" };

    /// <summary>
    /// 敏感 ControlId 片段（不区分大小写）。命中即视为敏感来源 ⇒ 丢弃该输入事件。
    /// 只用**稳定 ID 语义**判断，绝不用显示文字。
    /// </summary>
    private static readonly string[] SensitiveIdFragments =
    {
        "password", "passwd", "pwd", "secret", "credential", "token", "pin",
    };

    /// <summary>来源（含任一祖先）是否属于敏感输入 ⇒ 调用方必须**丢弃**该输入事件。</summary>
    public static bool IsSensitiveSource(IReadOnlyList<InputSourceNode>? ancestry)
    {
        if (ancestry is null || ancestry.Count == 0) return false;

        for (var i = 0; i < ancestry.Count; i++)
        {
            var node = ancestry[i];

            var type = node.TypeName;
            if (!string.IsNullOrEmpty(type))
            {
                for (var t = 0; t < SensitiveTypeNames.Length; t++)
                    if (type.Contains(SensitiveTypeNames[t], StringComparison.OrdinalIgnoreCase)) return true;
            }

            var id = node.AutomationId;
            if (!string.IsNullOrEmpty(id))
            {
                for (var f = 0; f < SensitiveIdFragments.Length; f++)
                    if (id.Contains(SensitiveIdFragments[f], StringComparison.OrdinalIgnoreCase)) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 把虚拟键码映射为**语义类别**（Windows VK 码，`Microsoft.UI.Xaml.Input.VirtualKey` 同值域）。
    /// 返回值必定属于 <see cref="InputCategories"/> 的固定集合 ⇒ 不可能携带按键身份。
    /// </summary>
    public static string ClassifyKey(int virtualKey) => virtualKey switch
    {
        0x09 => InputCategories.KeyTab,                                  // Tab
        0x0D or 0x20 => InputCategories.KeyActivation,                   // Enter / Space
        0x1B => InputCategories.KeyEscape,                               // Escape
        0x25 or 0x26 or 0x27 or 0x28 => InputCategories.KeyDirectional,   // Left/Up/Right/Down
        0x21 or 0x22 or 0x23 or 0x24 => InputCategories.KeyPageNavigation, // PageUp/PageDown/End/Home
        0x10 or 0x11 or 0x12 or 0x5B or 0x5C => InputCategories.KeyShortcut, // Shift/Ctrl/Alt/LWin/RWin
        >= 0x70 and <= 0x87 => InputCategories.KeyShortcut,               // F1–F24
        _ => InputCategories.KeyOtherNonText,
    };

    /// <summary>
    /// 该类别是否属于"导航/控制类"（有诊断价值：说明"输入到达了控件并被处理"）。
    /// 文本类按键一律归入其它类别，且身份永不记录。
    /// </summary>
    public static bool IsNavigationCategory(string? category) =>
        category is InputCategories.KeyTab or InputCategories.KeyActivation or InputCategories.KeyEscape
            or InputCategories.KeyDirectional or InputCategories.KeyPageNavigation or InputCategories.KeyShortcut;

    /// <summary>
    /// 从祖先链里挑出**稳定 ControlId**：取最靠近叶子的、非空且在敏感判定中已确认安全的 AutomationId。
    /// 返回 null 表示"没有稳定 ID"（上层显示 `unknown`）——**绝不用显示文字/序号/坐标兜底**。
    /// </summary>
    public static string? ResolveControlId(IReadOnlyList<InputSourceNode>? ancestry)
    {
        if (ancestry is null) return null;
        for (var i = 0; i < ancestry.Count; i++)
        {
            var id = ancestry[i].AutomationId;
            if (!string.IsNullOrWhiteSpace(id)) return id;
        }
        return null;
    }
}