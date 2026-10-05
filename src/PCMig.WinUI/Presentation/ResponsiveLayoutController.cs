namespace PCMig.WinUI.Presentation;

/// <summary>窗口级纯视觉布局档位；只决定结构性变化（重排/换列），不接触迁移业务、导航或绑定状态。</summary>
public enum LayoutMode { Wide, Normal, Compact }

/// <summary>
/// 一次布局决策的完整 Token 结果（Final Polish §5/§6/§55）。
///
/// 口径纪律：
///   · 全部长度单位是 **DIP**（与 WinUI 布局坐标一致）；<see cref="DpiScale"/> 只用于把圆角吸附到
///     物理像素网格，**绝不**用它去缩放布局（§9：不得混淆 DIP / Effective Pixel / Physical Pixel）。
///   · <see cref="WindowWidth"/>/<see cref="WindowHeight"/> 是传入的客户区 DIP；
///     <see cref="EffectiveWidth"/>/<see cref="EffectiveHeight"/> 是扣掉 Shell 外形装饰后**真正可用**的内容区。
///   · 纯计算产物：不读业务状态、不引用 ViewModel / Command / 绑定。
///   · <see cref="LayoutMode"/> 决定结构；<see cref="DensityScale"/> 决定同一档位内部的连续收紧（§5）。
/// </summary>
public readonly record struct ResponsiveLayout(
    LayoutMode Mode,
    double WindowWidth,
    double WindowHeight,
    double EffectiveWidth,
    double EffectiveHeight,
    double AspectRatio,
    double DpiScale,
    double DensityScale,
    double PageMargin,
    double WorkspacePadding,
    double CardPadding,
    double CardGap,
    double SectionGap,
    double SidebarWidth,
    double HeaderHeight,
    double BottomBarHeight,
    double IconSize,
    double ButtonHeight,
    double InputHeight,
    double BadgeSize,
    double CardCornerRadius,
    double ControlCornerRadius,
    double FontSizeBody,
    double FontSizeCaption,
    double FontSizeTitle,
    double TitleSpacing,
    double RailGap,
    double WorkspaceTopGap,
    double HeaderGap,
    double BottomBarGap,
    /// <summary>
    /// A34（20260928，用户第三轮反馈）：侧栏 Step Card 的**固定卡高**。
    /// 用户判词：「Card 变得过大……高度明显膨胀了……像四块过大的白色占位板……
    /// 必须比现在明显更紧凑，更像精致导航项而不是大面积内容卡」。
    /// ★ 根因：卡片高度此前完全由**内容**决定（Auto），控制器只约束了 SidebarWidth / MaxWidth，
    ///   从未约束高度 —— 于是内容或内边距一变，卡高就从 70 膨胀到 104~110。
    ///   现把卡高提升为受控 Token，按密度连续缩放，保证比例稳定、不再随内容漂移。
    /// </summary>
    double StepCardHeight,
    double PanelMaxHeight)
{
    /// <summary>Canonical 客户区宽（DIP）：与 MainWindow.CanonicalClientWidthDip 及契约测试同值。</summary>
    public const double CanonicalWidth = 1424;

    // ── §7 缩放优先级权重 ────────────────────────────────────────────────────────
    // 同一个 DensityScale 下降量，按 §7 的顺序分配不同力度：外围 Margin 收得最多，
    // 字号几乎不动。绝不允许"先缩字号"。
    internal const double WeightMargin = 1.00;        // 1 外围 Margin
    internal const double WeightGap = 0.90;           // 2 区域间 Gap
    internal const double WeightCardPadding = 0.75;   // 3 Card Padding
    internal const double WeightIcon = 0.60;          // 4 装饰图标 / 圆角
    internal const double WeightControl = 0.45;       // 5 部分按钮 / Input 高度
    internal const double WeightFont = 0.25;          // 最后 轻微缩小字号

    /// <summary>有效像素宽（诊断口径，= DIP × DpiScale）。</summary>
    public double EffectiveWidthPx => EffectiveWidth * DpiScale;

    /// <summary>有效像素高（诊断口径，= DIP × DpiScale）。</summary>
    public double EffectiveHeightPx => EffectiveHeight * DpiScale;

    public bool IsCompact => Mode == LayoutMode.Compact;

    // ── 由主 Token 派生的复合 Token（§55：数值集中在 Presentaion 层，不在各 View 里散落 Magic Number）──

    /// <summary>Header 内边距：左右比工作区内边距宽 4 DIP，上下取 CardGap 的 0.75（Wide 档 = 24,12，与现状一致）。</summary>
    public double HeaderPaddingX => Half(WorkspacePadding + 4);

    /// <inheritdoc cref="HeaderPaddingX"/>
    public double HeaderPaddingY => Half(CardGap * 0.75);

    /// <summary>Header 品牌方块边长（Wide 档 = 54，与现状一致；§7 第 4 优先级：装饰图标）。</summary>
    public double HeaderLogoSize => Math.Round(BadgeSize * 1.6, MidpointRounding.AwayFromZero);

    /// <summary>Header 品牌方块圆角（Wide 档 = 16，与现状一致）。</summary>
    public double HeaderLogoCornerRadius => Half(HeaderLogoSize * 0.3);

    /// <summary>Header 标题行（产品名 + 版本 Badge + Developer Tool）内间距（Wide 档 = 10，与现状一致）。</summary>
    public double HeaderTitleRowGap => Half(HeaderGap * 0.7);

    /// <summary>版本 Badge 内边距（Wide 档 = 10,4，与现状一致）。</summary>
    public double BadgePaddingX => Half(10 * Shrink(WeightControl));

    /// <inheritdoc cref="BadgePaddingX"/>
    public double BadgePaddingY => Half(4 * Shrink(WeightControl));

    /// <summary>Header「源电脑」状态文本上限宽（Wide 档 = 170，与现状一致）。</summary>
    public double SourceStatusMaxWidth => Mode switch
    {
        LayoutMode.Wide => 170,
        LayoutMode.Normal => 140,
        _ => 110,
    };

/// <summary>底栏进度宿主的**最小宽**：本轮改为 **0（无硬下限）**。
    /// 为什么取消原来的 160 / 120 / 96：进度轨道所在的是底栏唯一的弹性列（第 2 列 <c>*</c>），
    /// 在 canonical 1424 的完整预算下该列**只剩约 138 DIP**（1424 − Shell 外边距 40 − BottomBar 边框 2
    /// − BottomBar 内边距 44 − 固定内容 1056 − 8 处列距 144）。原来的 160 下限比槽位本身还宽 22 DIP
    /// ⇒ 宿主越出槽位、右端胶囊圆弧被裁成直角（用户真机判词 C）。
    /// 现在宿主取 <c>MinWidth = 0</c> + <c>HorizontalAlignment = Stretch</c>，宽度完全由星号列决定：
    /// 宽度为 0 时控件安全地不绘制，正常窗口范围内仍有可用宽度。
    /// 与 MainWindow.xaml 的 <c>MinWidth</c> 保持**同一个事实来源**（两边都是 0），
    /// 不留"XAML 0 / 运行时 160"的双重事实。</summary>
    public double FooterProgressWidth => 0d;

    /// <summary>FIX BATCH 5（§8）：底栏四块数字区的**保留宽**。
    /// 这些值在运行过程中会在很宽的区间里变化（"9 KB/s" ↔ "112.17 MB/s"、"—" ↔ "1 小时 23 分"、
    /// "0 B / 190.2 GiB" ↔ "190.2 GiB / 190.2 GiB"）。若列宽由内容驱动（原先的 Auto），
    /// 右侧四个动作按钮会被文本宽度一路向右推（真机 P1/P2-A/B/F、OPEN-RISK R-010）。
    /// 固定保留宽 = 该档位文本宽度上限 + 省略号余量；文字超长只在自己的格子里被 CharacterEllipsis 截断，
    /// 因此按钮左边界 X 与文本内容无关（验收口径 ≤1 px）。
    /// 这些宽度**不参与密度缩放**：数字字号是样式里的固定 FontSize，宽度若随密度缩小会出现比文字还窄的格子。</summary>
    // PHASE D 校正（2026-10-05）：MainWindow.xaml 的 FooterPercentText / FooterEtaText 用的是**固定
    //   Width**（68 / 112，来源是 UI Closure 的可读性调整），而这两个 token 原先声明 48 / 100 ⇒
    //   声明值与实际渲染长期不一致（token 从未被写回控件，等于死代码）。本轮把数值对齐到 XAML 实际值，
    //   让"声明 == 渲染"重新成立；**不改底栏视觉**（用户未报告底栏宽度问题，不扩大修改面）。
    public double FooterPercentWidth => Mode switch { LayoutMode.Wide => 68, LayoutMode.Normal => 60, _ => 52 };

    /// <inheritdoc cref="FooterPercentWidth"/>
    public double FooterBytesWidth => Mode switch { LayoutMode.Wide => 150, LayoutMode.Normal => 116, _ => 90 };

    /// <inheritdoc cref="FooterPercentWidth"/>
    public double FooterSpeedWidth => Mode switch { LayoutMode.Wide => 100, LayoutMode.Normal => 84, _ => 72 };

    /// <inheritdoc cref="FooterPercentWidth"/>
    // PHASE D 校正（2026-10-05）：同 FooterPercentWidth —— 对齐 MainWindow.xaml 的实际固定宽 112。
    public double FooterEtaWidth => Mode switch { LayoutMode.Wide => 112, LayoutMode.Normal => 96, _ => 84 };

    /// <summary>FIX BATCH 5（§8）：**单个**动作按钮的保留宽。四个按钮各自定宽 ⇒ 文案变长变短
    /// （"暂停" ↔ "正在暂停…" ↔ "重试暂停"）都不会改变动作区整块的左边界 X。
    /// 改前按钮是内容定宽（MinWidth = 0），"正在暂停…" 比 "暂停" 宽约 50 DIP，会把整块向左撑 —— 直接违反
    /// §8「Pause/Resume 切换不改变 Action 区 X 坐标」。</summary>
    public double FooterActionWidth => Mode switch { LayoutMode.Wide => 124, LayoutMode.Normal => 104, _ => 96 };

    /// <summary>FIX BATCH 5：网络指示块的保留宽（Wide/Normal 有，Compact 为 0 并整块隐藏）。
    /// 它不是动作按钮，因此允许在窄档位隐藏；四个动作按钮**绝不**允许用 Visibility 消失（§8）。</summary>
    public double FooterNetWidth => Mode switch { LayoutMode.Wide => 54, LayoutMode.Normal => 44, _ => 0 };

    /// <summary>
    /// 底栏中段隔离列宽（FIX BATCH 5：恒 0）。改前这一列在 Canonical 视口给 80 DIP 的"疏密"，
    /// 越窄越让位（1320 → 0、1120 → 20、960 → 0）。现在弹性全部交给**进度轨道所在的第 2 列**（<c>*</c>）：
    /// 那块空白由它一次性吸收，右端动作区因此被钉在窗口右边缘，按钮左边界 X 只由左侧固定保留宽决定。
    /// 保留这个 token 是为了让 ShellResponsiveLayout 的赋值链不出现"某一项突然消失"的分支差异。
    /// </summary>
    public double FooterSpacerWidth => 0;

    /// <summary>底栏四动作按钮内边距（Wide 档 = 12,6，与现状一致）。</summary>
    public double FooterActionPaddingX => Half(12 * Shrink(WeightControl));

    /// <inheritdoc cref="FooterActionPaddingX"/>
    public double FooterActionPaddingY => Half(6 * Shrink(WeightControl));

    /// <summary>Step1 表单字段行距（Wide 档 = 8，与现状一致）。</summary>
    public double FieldRowSpacing => Half(8 * Shrink(WeightGap));

    /// <summary>Step1 表单字段列距（Wide 档 = 14，与现状一致）。</summary>
    public double FieldColumnSpacing => Half(14 * Shrink(WeightGap));

    /// <summary>Step1 空状态插画宽（Wide 档 = 116，与现状一致；§7 第 4 优先级：缩装饰图标）。</summary>
    public double EmptyArtWidth => Half(116 * Shrink(WeightIcon));

    /// <inheritdoc cref="EmptyArtWidth"/>
    public double EmptyArtHeight => Half(88 * Shrink(WeightIcon));

    /// <summary>Step1 共享搜索框宽（Wide 档 = 230，与现状一致）。</summary>
    public double SharesSearchWidth => Mode switch
    {
        LayoutMode.Wide => 230,
        LayoutMode.Normal => 200,
        _ => 150,
    };

    /// <summary>次卡片内边距（共享卡 Wide 档 = 14，与现状一致）。</summary>
    public double SecondaryCardPadding => Half(CardPadding + 2);

    // ── 结构性（档位驱动）复合 Token ──────────────────────────────────────────────
    // 这些量属于 §5 说的"LayoutMode 决定结构性变化"：只在档位之间取值，不随密度连续变化。
    // 每条注释都标注 Wide 档取值，且**与现有 XAML 实测字面量逐一相等**——因此 Canonical 视口下基线不变。
    // 统一放在这里的原因见 §55：不允许在各 View 里 if (width < xxx) 然后散写数值。

    /// <summary>源 / 目标状态胶囊内边距（Wide 档 = 16,11，与现状一致）。</summary>
    public double CapsulePaddingX => Mode switch { LayoutMode.Wide => 16, LayoutMode.Normal => 14, _ => 12 };

    /// <inheritdoc cref="CapsulePaddingX"/>
    public double CapsulePaddingY => Mode switch { LayoutMode.Wide => 11, LayoutMode.Normal => 9, _ => 8 };

    /// <summary>底栏内边距（Wide 档 = 22,9，与现状一致）。</summary>
    public double FooterBarPaddingX => Mode switch { LayoutMode.Wide => 22, LayoutMode.Normal => 18, _ => 13 };

    /// <inheritdoc cref="FooterBarPaddingX"/>
    public double FooterBarPaddingY => Mode switch { LayoutMode.Wide => 9, LayoutMode.Normal => 8, _ => 7 };

    /// <summary>Shell 外围底边距（Wide 档 = 16，与现状一致）。</summary>
    public double ShellBottomMargin => Mode switch { LayoutMode.Wide => 16, LayoutMode.Normal => 14, _ => 10 };

    /// <summary>工作区外壳下内边距（Wide 档 = 18，与现状一致）。</summary>
    public double WorkspacePaddingBottom => Mode switch { LayoutMode.Wide => 18, LayoutMode.Normal => 16, _ => 12 };

    /// <summary>输入框左侧内边距（给前置图标让位；Wide 档 = 42，与现状一致）。</summary>
    public double InputPaddingLeft => Mode switch { LayoutMode.Wide => 42, LayoutMode.Normal => 40, _ => 38 };

    /// <summary>输入框纵向内边距（Wide 档 = 8，与现状一致）。</summary>
    public double InputPaddingY => Mode switch { LayoutMode.Wide => 8, LayoutMode.Normal => 7, _ => 6 };

    /// <summary>输入框右侧内边距（Wide 档 = 14，与现状一致；只影响文本尾端，不参与档位收紧）。</summary>
    public double InputPaddingRight => 14;

    /// <summary>密码框右侧内边距（为「显示密码」按钮让位；Wide 档 = 46，与现状一致）。</summary>
    public double PasswordPaddingRight => 46;

    /// <summary>输入框前置图标左边距（Wide 档 = 15，与现状一致）。</summary>
    public double InputIconMarginLeft => Mode switch { LayoutMode.Wide => 15, LayoutMode.Normal => 14, _ => 13 };

    /// <summary>底栏两组速率占位的内间距（Wide 档 = 6，与现状一致）。</summary>
    public double FooterRateGap => Mode switch { LayoutMode.Wide => 6, LayoutMode.Normal => 6, _ => 5 };

    /// <summary>底栏四动作按钮组内间距（Wide 档 = 12，与现状一致）。</summary>
    public double FooterActionGroupGap => Mode switch { LayoutMode.Wide => 12, LayoutMode.Normal => 10, _ => 8 };

    /// <summary>Step1 共享卡表头列距 / 卡头内间距（Wide 档 = 10，与现状一致）。</summary>
    public double SharesHeaderGap => Mode switch { LayoutMode.Wide => 10, LayoutMode.Normal => 9, _ => 8 };

    /// <summary>Step1 页面标题块内间距（Wide 档 = 6，与现状一致）。</summary>
    public double TitleStackGap => Mode switch { LayoutMode.Wide => 6, LayoutMode.Normal => 6, _ => 4 };

    /// <summary>Step1「连接并列出共享」按钮最小宽（Wide 档 = 196，与现状一致；Compact 交给整行宽度）。</summary>
    public double ConnectButtonMinWidth => Mode switch { LayoutMode.Wide => 196, LayoutMode.Normal => 176, _ => 0 };

    /// <summary>
    /// 侧栏步骤卡副标题的可用宽度上限（DIP）。
    /// 由侧栏宽反推：扣卡片内边距 26、徽章列 32、列间距 14，再留 4 余量。
    /// 为什么必须显式给上限：副标题在 Grid 的 Auto 列里，窄档下会被卡片边缘**硬裁到半个汉字**
    /// （实测 Compact 档裁掉"错""常清单"）。给出上限后配合 CharacterEllipsis 退化为省略号，
    /// 完整文本仍由既有 ToolTip 提供。Wide 档值足够大，等同于不约束（保持现状不变）。
    /// </summary>
    public double StepSubtitleMaxWidth => Half(Math.Max(96, SidebarWidth - 76));

    /// <summary>Step1 空状态提示块内间距（Wide 档 = 8，与现状一致）。</summary>
    public double EmptyHintGap => Mode switch { LayoutMode.Wide => 8, LayoutMode.Normal => 7, _ => 6 };

    /// <summary>按 §7 优先级把密度下降量分配给某个 Token。</summary>
    internal double Shrink(double weight) =>
        ResponsiveLayoutController.UniformScaleMode
            ? ResponsiveLayoutController.CurrentUniformFactor   // A3：单一标量，忽略 weight
            : 1 - (1 - DensityScale) * weight;

    /// <summary>0.5 DIP 网格取整：避免半像素抖动，同时保住"连续收紧"（§5）。</summary>
    internal static double Half(double value) => Math.Round(value * 2, MidpointRounding.AwayFromZero) / 2;
}

/// <summary>
/// 窗口尺寸 → 视觉 Token 的**唯一**入口（Final Polish §5/§7/§55）。
/// 只做纯计算：输入客户区尺寸与 DPI，输出 <see cref="ResponsiveLayout"/>；不读业务状态、不触碰任何控件。
/// </summary>
public static class ResponsiveLayoutController
{
    /// <summary>Wide / Normal 断点（客户区宽，DIP）。</summary>
    public const double WideBreakpoint = 1320;

    /// <summary>Normal / Compact 断点（客户区宽，DIP）。</summary>
    public const double NormalBreakpoint = 1120;

    /// <summary>设计最小客户区宽（DIP），与 MainWindow.MinimumClientWidthDip 对齐。</summary>
    public const double MinimumClientWidth = 960;

    /// <summary>
    /// **DensityScale 下限（待实测确认）**：§6 明确"具体最低值不要拍脑袋，通过真实 UI 验证以后决定"，
    /// 允许区间是 0.86–0.82。这里先取 0.86 作为**占位值**；真机在多尺寸 / 多 DPI 下验收后，
    /// 只需改这一个常量（必要时下调到 0.82），无需改动任何 Token 公式与 View。
    /// </summary>
    public const double MinimumDensityScale = 0.86;

    /// <summary>DensityScale = 1.00 的起点宽（Canonical 视口）：保证既有基线在此宽度及以上**逐字不变**。</summary>
    private const double DensityFullWidth = 1424;

    private const double DensityAtWideAnchor = 1.00;

    private const double DensityAtNormalAnchor = 0.94;

    /// <summary>侧栏步骤卡的基础卡高（DIP）。Canonical 档取 72 —— 与用户认可的几何一致，
    /// 比"内容自适应"时稳定得多，也明显比膨胀后的 104~110 更紧凑。</summary>
    private const double StepCardHeightBase = 72;

    /// <summary>Shell 固定竖向装饰高度（DIP）：42（标题条）+ 16（工作区底边距）。</summary>
    private const double FixedChromeHeight = 58;

    /// <summary>
    /// 计算当前档位与全部 Token。
    /// </summary>
    /// <param name="width">客户区宽（DIP）。</param>
    /// <param name="height">客户区高（DIP）。</param>
    /// <param name="dpiScale">窗口 DPI 缩放（1.0 = 96 DPI）。可选，默认 1.0 —— 现有调用点
    /// <c>Calculate(width, height)</c> 保持可编译（向后兼容）。</param>
    public static ResponsiveLayout Calculate(double width, double height, double dpiScale = 1.0)
    {
        var dpi = dpiScale > 0 ? dpiScale : 1.0;
        var w = width > 0 ? width : MinimumClientWidth;
        var h = height > 0 ? height : 0;

        var mode = w switch
        {
            >= WideBreakpoint => LayoutMode.Wide,
            >= NormalBreakpoint => LayoutMode.Normal,
            _ => LayoutMode.Compact,
        };

        var density = DensityFor(w);

        // 档位基值（已与现有 XAML 实测字面量对齐：density = 1.00 时逐项等于当前写死的值，
        // 因此 Canonical 视口下的既有视觉基线不会被本轮改动）。
        var pageMarginBase = mode switch { LayoutMode.Wide => 20, LayoutMode.Normal => 18, _ => 14 };
        var workspacePaddingBase = mode switch { LayoutMode.Wide => 20, LayoutMode.Normal => 18, _ => 14 };
        var cardPaddingBase = mode switch { LayoutMode.Wide => 12, LayoutMode.Normal => 11, _ => 10 };
        var cardGapBase = mode switch { LayoutMode.Wide => 16, LayoutMode.Normal => 14, _ => 12 };
        var sectionGapBase = mode switch { LayoutMode.Wide => 12, LayoutMode.Normal => 10, _ => 8 };
        var sidebarBase = mode switch { LayoutMode.Wide => 276, LayoutMode.Normal => 246, _ => 220 };
        var headerHeightBase = mode switch { LayoutMode.Wide => 82, LayoutMode.Normal => 78, _ => 72 };
        var bottomBarHeightBase = mode switch { LayoutMode.Wide => 64, LayoutMode.Normal => 60, _ => 56 };
        var iconBase = mode switch { LayoutMode.Wide => 18, LayoutMode.Normal => 17, _ => 16 };
        var buttonHeightBase = mode switch { LayoutMode.Wide => 34, LayoutMode.Normal => 34, _ => 32 };
        var inputHeightBase = mode switch { LayoutMode.Wide => 46, LayoutMode.Normal => 44, _ => 42 };
        var badgeBase = mode switch { LayoutMode.Wide => 34, LayoutMode.Normal => 32, _ => 30 };
        var cardRadiusBase = mode switch { LayoutMode.Wide => 16, LayoutMode.Normal => 14, _ => 12 };
        var controlRadiusBase = mode switch { LayoutMode.Wide => 12, LayoutMode.Normal => 12, _ => 10 };
        var fontBodyBase = mode switch { LayoutMode.Wide => 15, LayoutMode.Normal => 14.5, _ => 14 };
        var fontCaptionBase = mode switch { LayoutMode.Wide => 13, LayoutMode.Normal => 12.5, _ => 12 };
        var fontTitleBase = mode switch { LayoutMode.Wide => 29, LayoutMode.Normal => 28, _ => 27 };
        var titleSpacingBase = mode switch { LayoutMode.Wide => 12, LayoutMode.Normal => 10, _ => 8 };
        var railGapBase = mode switch { LayoutMode.Wide => 18, LayoutMode.Normal => 14, _ => 10 };
        var workspaceTopGapBase = mode switch { LayoutMode.Wide => 16, LayoutMode.Normal => 10, _ => 8 };
        var headerGapBase = mode switch { LayoutMode.Wide => 14, LayoutMode.Normal => 12, _ => 10 };
        // FIX BATCH 5（§8）：底栏列距从 44/22/12 收紧到 18/14/12 —— 每列内容改为"保留宽"后，
        // 列距只需提供视觉分隔；收下来的宽度全部让给进度轨道与四个动作按钮（见 FooterProgressWidth 等）。
        var bottomBarGapBase = mode switch { LayoutMode.Wide => 18, LayoutMode.Normal => 14, _ => 12 };

        var uFactor = ResponsiveLayoutController.UniformScaleMode
            ? ResponsiveLayoutController.CalculateUniformScale(
                w, h <= 0 ? ResponsiveLayoutController.BaseClientHeight : h, 0.5, 2.0)
            : 1.0;
        ResponsiveLayoutController.CurrentUniformFactor = uFactor;

        // A3（Phase A 第 3 步）核心：两种模式下所有主 token 都经由此处。
        //   关闭（默认）→ 逐位保持现行加权紧凑公式；打开（PCMIG_UNIFORM_SCALE=1）→ 全部 = BaseValue × 同一 uFactor。
        //   夹紧值刻意放宽到 0.5~2.0：Spike 要观测"真实等比例"，Min/Max 等实测后再定。
        double Scaled(double baseValue, double weight) =>
            ResponsiveLayout.Half(ResponsiveLayoutController.UniformScaleMode
                ? baseValue * uFactor
                : baseValue * (1 - (1 - density) * weight));

        var pageMargin = Scaled(pageMarginBase, ResponsiveLayout.WeightMargin);
        var railGap = Scaled(railGapBase, ResponsiveLayout.WeightGap);
        var workspaceTopGap = Scaled(workspaceTopGapBase, ResponsiveLayout.WeightGap);
        var headerHeight = Scaled(headerHeightBase, ResponsiveLayout.WeightControl);
        var bottomBarHeight = Scaled(bottomBarHeightBase, ResponsiveLayout.WeightControl);

        // 有效内容区：扣掉 Shell 外形装饰（外围 Margin + 固定标题条 + Header + 底栏）。
        var effectiveWidth = Math.Max(0, w - 2 * pageMargin);
        var effectiveHeight = Math.Max(0, h - (FixedChromeHeight + headerHeight + bottomBarHeight + workspaceTopGap));

        // 面板只在小窗口内滚动；默认视口仍可一次展示全部调节器（公式与扩展前逐字一致，避免与 §22/§40 的调整冲突）。
        var panelMaxHeight = Math.Max(420, h - 76);

        return new ResponsiveLayout(
            Mode: mode,
            WindowWidth: w,
            WindowHeight: h,
            EffectiveWidth: effectiveWidth,
            EffectiveHeight: effectiveHeight,
            AspectRatio: h > 0 ? w / h : 0,
            DpiScale: dpi,
            DensityScale: density,
            PageMargin: pageMargin,
            WorkspacePadding: Scaled(workspacePaddingBase, ResponsiveLayout.WeightMargin),
            CardPadding: Scaled(cardPaddingBase, ResponsiveLayout.WeightCardPadding),
            CardGap: Scaled(cardGapBase, ResponsiveLayout.WeightGap),
            SectionGap: Scaled(sectionGapBase, ResponsiveLayout.WeightGap),
            SidebarWidth: sidebarBase,
            HeaderHeight: headerHeight,
            BottomBarHeight: bottomBarHeight,
            IconSize: Scaled(iconBase, ResponsiveLayout.WeightIcon),
            ButtonHeight: Scaled(buttonHeightBase, ResponsiveLayout.WeightControl),
            InputHeight: Scaled(inputHeightBase, ResponsiveLayout.WeightControl),
            BadgeSize: Scaled(badgeBase, ResponsiveLayout.WeightIcon),
            CardCornerRadius: SnapToPixel(Scaled(cardRadiusBase, ResponsiveLayout.WeightIcon), dpi),
            ControlCornerRadius: SnapToPixel(Scaled(controlRadiusBase, ResponsiveLayout.WeightIcon), dpi),
            FontSizeBody: Scaled(fontBodyBase, ResponsiveLayout.WeightFont),
            FontSizeCaption: Scaled(fontCaptionBase, ResponsiveLayout.WeightFont),
            FontSizeTitle: Scaled(fontTitleBase, ResponsiveLayout.WeightFont),
            TitleSpacing: Scaled(titleSpacingBase, ResponsiveLayout.WeightGap),
            RailGap: railGap,
            WorkspaceTopGap: workspaceTopGap,
            HeaderGap: Scaled(headerGapBase, ResponsiveLayout.WeightGap),
            BottomBarGap: Scaled(bottomBarGapBase, ResponsiveLayout.WeightGap),
            StepCardHeight: Scaled(StepCardHeightBase, ResponsiveLayout.WeightControl),
            PanelMaxHeight: panelMaxHeight);
    }

    /// <summary>
    /// DensityScale 曲线：≥1424（Canonical）恒为 1.00；1120–1424 线性 0.94→1.00；960–1120 线性 0.86→0.94。
    /// 全程**连续**（分档处两侧取同值），因此模式切换不会带来尺寸跳变（§5）。
    /// </summary>
    private static double DensityFor(double width)
    {
        if (width >= DensityFullWidth) return DensityAtWideAnchor;
        if (width >= NormalBreakpoint)
        {
            return Lerp(
                DensityAtNormalAnchor,
                DensityAtWideAnchor,
                (width - NormalBreakpoint) / (DensityFullWidth - NormalBreakpoint));
        }

        var clamped = Math.Clamp(width, MinimumClientWidth, NormalBreakpoint);
        return Lerp(
            MinimumDensityScale,
            DensityAtNormalAnchor,
            (clamped - MinimumClientWidth) / (NormalBreakpoint - MinimumClientWidth));
    }

    private static double Lerp(double from, double to, double t) => from + (to - from) * t;

    /// <summary>把圆角吸附到物理像素网格（§11/§43：避免 DPI 取整导致圆角毛边）。</summary>
    private static double SnapToPixel(double value, double dpi) =>
        dpi > 0 ? Math.Round(value * dpi, MidpointRounding.AwayFromZero) / dpi : value;

    // ══════════════════════════════════════════════════════════════════════════════
    //  A53（Phase A）Single-Scalar Uniform Responsive Layout —— 实验入口（默认关闭）
    //
    //  用户正式拍板：Responsive 采用 A3 Single-Scalar Uniform Responsive Layout，即由**同一个
    //  ApplicationUIScale** 驱动全部主要设计尺寸（Font / Margin / Padding / Gap / Card /
    //  Control / Icon / Badge / CornerRadius），取代现行 WeightMargin…WeightFont 的**加权紧凑**
    //  体系（那套是"先挤 Margin、字号几乎不动"，与产品要求相反）。
    //
    //  ★ 本次只**新增**计算与开关，不改任何现有行为：UniformScaleMode 默认 false，
    //    Calculate() 与 ResponsiveLayout 的现有成员一字未动 ⇒ 旧路径完全保留、可即刻回退。
    //    真正的渲染接入（ShellResponsiveLayout 改为 BaseValue × ApplicationUIScale）在下一步做。
    //
    //  ★ 基准来自**运行时实测**（不是猜的常数），DPI=96 客户区实测：
    //      窗口外框 1440x900 px ／ 客户区 1424x892 DIP（= XamlRoot.Size）／ 非客户区 +16 / +8
    //      ⇒ BaseClientWidth/Height = 1424 / 892（与既有 CanonicalWidth=1424 一致，互为印证）
    //      页面内容带（工作区实际可用区）= 1070 x 583 DIP
    //        宽 = 客户宽 − Shell 左右 20x2 − SidebarColumn 276 − RailGap 18 − WorkspaceShell 左右 20x2
    //        高 = 顶 160 → 底 743（由页头与 Footer 行实测推得）
    //
    //  ★ 实测得出的硬事实：16:10 附近的窗口比例下，**高度永远先被耗尽**
    //      ⇒ 必须 Min(ScaleX, ScaleY)；只按宽度缩放必然在高度上裁切（Step4 日志被裁的成因）。
    //
    //  ★ DPI 不参与第二次缩放：UIScale 全部在 DIP 体系内计算；RasterizationScale 只用于
    //      DIP ↔ 物理像素换算（WM_GETMINMAXINFO 等 Win32 接口）。
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>实验开关：true 时启用 A3 单一标量等比例缩放（默认 false = 现行加权紧凑路径）。</summary>
    public static bool UniformScaleMode { get; set; }

    /// <summary>A3：当前统一缩放因子（由 Calculate 写入；Shrink/Scaled 两个单点读取它）。</summary>
    public static double CurrentUniformFactor { get; set; } = 1.0;

    /// <summary>基准客户区宽（DIP）—— 运行时实测值，非估计。</summary>
    public const double BaseClientWidth = 1424;

    /// <summary>基准客户区高（DIP）—— 运行时实测值，非估计。</summary>
    public const double BaseClientHeight = 892;

    /// <summary>基准页面内容带宽（DIP）—— 工作区实际可用宽度，运行时实测推得。</summary>
    public const double BaseContentWidth = 1070;

    /// <summary>基准页面内容带高（DIP）—— 工作区实际可用高度，运行时实测推得。</summary>
    public const double BaseContentHeight = 583;

    /// <summary>
    /// 光学安全下限（Optical Safety Floor）—— 只允许极少数视觉保护项设下限，
    /// 不得重新演变成"每种元素一套 Weight"：
    ///   · 1 DIP 的 DAEL 边缘光不得被缩到人眼不可见；
    ///   · 字号不得低于可读下限（低于它说明窗口已到 MinimumUIScale，应停止缩窗口而非继续压字）。
    /// </summary>
    public const double OpticalMinStrokeThickness = 1.0;

    /// <summary>最小可读字号（DIP）：UIScale 降到使正文字号低于此值时，该档即为 MinimumUIScale。</summary>
    public const double MinimumReadableFontSize = 11.0;

    /// <summary>
    /// A3 核心：由客户区（DIP）算出单一标量 ApplicationUIScale。
    ///   RawScale = Min(AvailableWidth / BaseClientWidth, AvailableHeight / BaseClientHeight)
    ///   ApplicationUIScale = Clamp(RawScale, minScale, maxScale)
    /// 全部在 DIP 体系内计算，不再乘 RasterizationScale。
    /// </summary>
    /// <param name="clientWidthDip">当前客户区宽（DIP），建议直接取 XamlRoot.Size.Width。</param>
    /// <param name="clientHeightDip">当前客户区高（DIP），建议直接取 XamlRoot.Size.Height。</param>
    /// <param name="minScale">光学安全下限；由 Step4 与四页压力测试实测确定后再定值。</param>
    /// <param name="maxScale">视觉上限；防止大屏下按钮/字体像电视 UI。</param>
    public static double CalculateUniformScale(
        double clientWidthDip, double clientHeightDip, double minScale, double maxScale)
    {
        if (double.IsNaN(clientWidthDip) || double.IsNaN(clientHeightDip)) return 1.0;
        if (clientWidthDip <= 0 || clientHeightDip <= 0) return 1.0;

        var byWidth = clientWidthDip / BaseClientWidth;
        var byHeight = clientHeightDip / BaseClientHeight;
        var raw = Math.Min(byWidth, byHeight);   // 宽度与高度同时参与，取更紧张者

        var lo = minScale > 0 ? minScale : 1.0;
        var hi = maxScale >= lo ? maxScale : lo;
        return Math.Clamp(raw, lo, hi);
    }}
