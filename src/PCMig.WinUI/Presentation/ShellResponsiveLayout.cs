using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PCMig.WinUI.Presentation;

/// <summary>
/// 把 <see cref="ResponsiveLayout"/> 的 Token 落到 **Shell（Header / 底栏 / 工作区外壳）与 Step1 页面**
/// 的真实元素上（Final Polish §36 / §37 / §38 / §39 / §55）。
///
/// 纪律：
///   · 只改 Margin / Padding / Spacing / 尺寸 / 字号 / 行高 / 列宽，**不碰**任何绑定、命令、业务状态、
///     导航语义 —— 纯视觉层，与功能冻结（§49）不冲突；
///   · 元素一律按 <c>x:Name</c> 解析（FindName），解析不到就**跳过**，不抛异常、不影响任何业务路径；
///   · 所有数值都来自 <see cref="ResponsiveLayout"/> 的 Token，这里不写 Magic Number（§55）；
///   · 不碰 <c>Translation</c>：位移属于 Motion 体系（MotionDirector）的领地，避免与动画互相覆盖。
///
/// 接入方式（由 MainWindow.xaml.cs 的 ApplyResponsiveLayout 补 **1 行**）：
/// <code>
///     var layout = ResponsiveLayoutController.Calculate(width, height, CurrentScale());
///     ShellResponsiveLayout.Apply(this, layout);   // ← 新增
/// </code>
/// </summary>
public static class ShellResponsiveLayout
{
    // ── A53（Phase A 第 2 步）：A3 单一标量等比例缩放的接线 ──────────────────────────
    /// <summary>当前生效的 ApplicationUIScale（诊断口径；仅在 UniformScaleMode 打开时由客户区算出）。</summary>
    public static double CurrentApplicationUIScale { get; private set; } = 1.0;

    /// <summary>光学安全下限：由 Step4 与四页压力测试实测确定后再定值（当前占位 0.85）。</summary>
    public static double MinimumUIScale { get; set; } = 0.85;

    /// <summary>视觉上限：防止大屏下按钮/字体像电视 UI（实测后定值，当前占位 1.10）。</summary>
    public static double MaximumUIScale { get; set; } = 1.10;
    private static readonly string[] FooterActionButtons =
    {
        "FooterStartButton", "FooterPauseButton", "FooterStopButton", "FooterResumeButton",
    };

    private static readonly string[] FooterActionLabels =
    {
        "FooterStartLabel", "FooterPauseLabel", "FooterStopLabel", "FooterResumeLabel",
    };

    private static readonly string[] Step1Inputs = { "HostInput", "UsernameInput" };

    private static readonly string[] Step1InputIcons = { "HostInputIcon", "UsernameInputIcon", "PasswordInputIcon" };

    /// <summary>Shell 入口：一张布局决策 → Header / 底栏 / 工作区外壳 / Step1。</summary>
    public static void Apply(Window window, ResponsiveLayout layout)
    {
        if (window is null) return;
        Apply(window.Content as FrameworkElement, layout);
    }

    /// <summary>按 XAML 根元素应用（便于单测/复用；<paramref name="root"/> 为空时安全返回）。</summary>
    public static void Apply(FrameworkElement? root, ResponsiveLayout layout)
    {
        // A53（Phase A 第 2 步）：算出单一标量 ApplicationUIScale，**暂不施加**，先让数值可观测。
        // UniformScaleMode 默认 false ⇒ 现行加权紧凑路径完全不受影响（可即刻回退）。
        // A3 实验开关：PCMIG_UNIFORM_SCALE=1 启用（与既有 PCMIG_ACRYLIC_* 同手法；默认关闭）。
        ResponsiveLayoutController.UniformScaleMode =
            Environment.GetEnvironmentVariable("PCMIG_UNIFORM_SCALE") == "1";

        if (ResponsiveLayoutController.UniformScaleMode && root is not null)
        {
            var clientW = root.XamlRoot is { } xr ? xr.Size.Width : root.ActualWidth;
            var clientH = root.XamlRoot is { } xr2 ? xr2.Size.Height : root.ActualHeight;
            CurrentApplicationUIScale = ResponsiveLayoutController.CalculateUniformScale(
                clientW, clientH, MinimumUIScale, MaximumUIScale);
        }
        else
        {
            CurrentApplicationUIScale = 1.0;
        }
        if (root is null) return;
        ApplyShellChrome(root, layout);
        if (root.FindName("PageConnect") is FrameworkElement connectPage) ApplyConnectPage(connectPage, layout);
    }

    /// <summary>Header（§37）+ Bottom Status Bar（§36）+ 工作区外壳。</summary>
    private static void ApplyShellChrome(FrameworkElement root, ResponsiveLayout layout)
    {
        // §7 第 1 优先级：外围 Margin 与骨架行高。
        if (root.FindName("ShellRowsGrid") is Grid shellRows)
        {
            shellRows.Margin = new Thickness(layout.PageMargin, 0, layout.PageMargin, layout.ShellBottomMargin);
        }

        if (root.FindName("HeaderRow") is RowDefinition headerRow) headerRow.Height = new GridLength(layout.HeaderHeight);
        if (root.FindName("BottomBarRow") is RowDefinition bottomRow) bottomRow.Height = new GridLength(layout.BottomBarHeight);

        // 侧栏步骤卡副标题：窄档下必须给宽度上限，否则会被卡片边缘硬裁到半个汉字
        // （实测 Compact 档裁掉"错""常清单"，且无省略号）。上限负责"裁得干净"，
        // XAML 侧的 TextTrimming=CharacterEllipsis 负责"退化为省略号"。
        if (root.FindName("StepNav") is FrameworkElement stepNav)
        {
            stepNav.MaxWidth = layout.SidebarWidth;
            // A34 修正（用户第三轮反馈）：**不要**对步骤卡强制固定高度。
            // 用户要求「恢复原来的 Step Navigation 几何样式」，并明确"不要再凭感觉重新设计尺寸"。
            // 已核对修改前的真实实现（镜像备份 PCMig-修复前快照-20260927-230006）：
            //   基线 StepNavigationControl.xaml 的 Button **没有任何 Height/MinHeight**，
            //   ShellResponsiveLayout 也只设 MaxWidth —— 卡高完全由 Padding="13,15" + 内容决定。
            //   我此前新增的固定卡高正是"Card 太扁 + 间距显大"的来源，故移除。
        }

        if (root.FindName("WorkspaceShell") is Border workspace)
        {
            workspace.Padding = new Thickness(
                layout.WorkspacePadding, layout.WorkspacePadding, layout.WorkspacePadding, layout.WorkspacePaddingBottom);
        }

        // §37 Header：先收紧 Gap → 再缩装饰块/胶囊 → **最后**才轻微缩辅助文字。
        if (root.FindName("AppTitleBar") is Border header)
        {
            header.Padding = new Thickness(
                layout.HeaderPaddingX, layout.HeaderPaddingY, layout.HeaderPaddingX, layout.HeaderPaddingY);
        }

        if (root.FindName("HeaderIdentityStack") is StackPanel identity) identity.Spacing = layout.HeaderGap;
        if (root.FindName("HeaderTitleRow") is StackPanel headerTitleRow) headerTitleRow.Spacing = layout.HeaderTitleRowGap;

        if (root.FindName("HeaderLogo") is Border logo)
        {
            logo.Width = layout.HeaderLogoSize;
            logo.Height = layout.HeaderLogoSize;
            logo.CornerRadius = new CornerRadius(layout.HeaderLogoCornerRadius);
        }

        // 辅助文字（§37 第 2 步；第 3 步的"隐藏次要描述"在最小尺寸下并不需要，故不做）。
        if (root.FindName("HeaderSubtitle") is TextBlock subtitle) subtitle.FontSize = layout.FontSizeCaption;

        if (root.FindName("ChangelogButton") is Button versionBadge)
        {
            versionBadge.Padding = new Thickness(
                layout.BadgePaddingX, layout.BadgePaddingY, layout.BadgePaddingX, layout.BadgePaddingY);
            versionBadge.CornerRadius = new CornerRadius(layout.ControlCornerRadius);
            versionBadge.MinWidth = 0; // 防止系统默认最小宽把 Badge 撑成"按钮"
        }

        if (root.FindName("DeveloperTuningButton") is Button developerTool)
        {
            var size = Math.Max(20, layout.BadgeSize - 6); // Wide 档 = 28，与现状一致
            developerTool.Width = size;
            developerTool.Height = size;
        }

        if (root.FindName("HeaderStatusCapsule") is Border capsule)
        {
            capsule.Padding = new Thickness(
                layout.CapsulePaddingX, layout.CapsulePaddingY, layout.CapsulePaddingX, layout.CapsulePaddingY);
            capsule.CornerRadius = new CornerRadius(layout.CardCornerRadius);
        }

        if (root.FindName("SourceStatusText") is TextBlock sourceStatus) sourceStatus.MaxWidth = layout.SourceStatusMaxWidth;

        // §36 Bottom Status Bar：先缩间距/内边距 → 再缩进度轨道与按钮内边距；
        // 百分比、字节数、进度、两组速率、网络、四个动作按钮**一律保留**（不隐藏关键操作）。
        if (root.FindName("BottomBar") is Border bottomBar)
        {
            bottomBar.Padding = new Thickness(
                layout.FooterBarPaddingX, layout.FooterBarPaddingY, layout.FooterBarPaddingX, layout.FooterBarPaddingY);
        }

        if (root.FindName("BottomBarGrid") is Grid bottomBarGrid)
        {
            bottomBarGrid.ColumnSpacing = layout.BottomBarGap;
            // 第 6 列是中段隔离列：FIX BATCH 5 起恒为 0（弹性全部交给第 7 列的 `*`），
            // 右端动作区因此被钉在窗口右边缘，按钮左边界 X 只由左侧各保留宽决定（§8 验收 ≤1 px）。
            if (bottomBarGrid.ColumnDefinitions.Count > 5)
            {
                bottomBarGrid.ColumnDefinitions[5].Width = new GridLength(layout.FooterSpacerWidth);
            }
        }

        // FIX BATCH 5（§8）：进度轨道只设**最小宽**，实际宽度由它所在的弹性列（第 2 列 *）决定 ——
        // 这样轨道总是填满可用宽度，且任何窗口宽度下都不会把右端动作区挤出可视范围。
        if (root.FindName("FooterProgressHost") is Grid progressHost) progressHost.MinWidth = layout.FooterProgressWidth;

        // FIX BATCH 5（§8）：四块数字区的保留宽。文案变化（"9 KB/s" ↔ "112.17 MB/s"、"—" ↔ "1 小时 23 分"）
        // 只在自己格子里被 CharacterEllipsis 截断，不会把右侧动作区推走。
        if (root.FindName("FooterPercentText") is TextBlock percentText) percentText.Width = layout.FooterPercentWidth;
        if (root.FindName("FooterBytesText") is TextBlock bytesText) bytesText.Width = layout.FooterBytesWidth;
        if (root.FindName("FooterSpeedText") is TextBlock speedText) speedText.Width = layout.FooterSpeedWidth;
        if (root.FindName("FooterEtaText") is TextBlock etaText) etaText.Width = layout.FooterEtaWidth;

        // 网络指示块：Wide/Normal 占保留宽，Compact（FooterNetWidth = 0）整块隐藏。
        // 它不是动作按钮 —— 四个动作按钮**绝不**允许用 Visibility 消失（§8）。
        if (root.FindName("FooterNetPanel") is StackPanel netPanel)
        {
            netPanel.Width = layout.FooterNetWidth;
            netPanel.Visibility = layout.FooterNetWidth > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        if (root.FindName("FooterRatePrimary") is StackPanel ratePrimary) ratePrimary.Spacing = layout.FooterRateGap;
        if (root.FindName("FooterRateSecondary") is StackPanel rateSecondary) rateSecondary.Spacing = layout.FooterRateGap;

        foreach (var name in FooterActionButtons)
        {
            if (root.FindName(name) is not Button action) continue;
            action.Padding = new Thickness(
                layout.FooterActionPaddingX, layout.FooterActionPaddingY, layout.FooterActionPaddingX, layout.FooterActionPaddingY);
            action.Width = layout.FooterActionWidth;   // FIX BATCH 5：固定保留宽 ⇒ 文案变长不改变动作区 X
            action.MinWidth = 0;            // 内容定宽：Compact 下不被系统最小宽顶出去
            action.MinHeight = layout.ButtonHeight;
        }

        // 四动作按钮组的组内间距（组本身没有 x:Name，用首按钮的逻辑父级取到，避免为它单独加名）。
        if (root.FindName("FooterStartButton") is Button firstAction && firstAction.Parent is StackPanel actionGroup)
        {
            actionGroup.Spacing = layout.FooterActionGroupGap;
        }

        foreach (var name in FooterActionLabels)
        {
            if (root.FindName(name) is TextBlock label) label.FontSize = layout.FontSizeBody;
        }
    }

    /// <summary>Step1（§38 页面内部横向布局；§39 不写死会裁切的高度）。</summary>
    private static void ApplyConnectPage(FrameworkElement page, ResponsiveLayout layout)
    {
        if (page.FindName("RootGrid") is Grid rootGrid) rootGrid.RowSpacing = layout.SectionGap;

        if (page.FindName("PageTitleStack") is StackPanel titleStack) titleStack.Spacing = layout.TitleStackGap;
        if (page.FindName("PageTitleRow") is StackPanel titleRow) titleRow.Spacing = layout.TitleSpacing;
        if (page.FindName("PageTitleText") is TextBlock pageTitle) pageTitle.FontSize = layout.FontSizeTitle;
        if (page.FindName("PageSubtitle") is TextBlock pageSubtitle) pageSubtitle.FontSize = layout.FontSizeCaption;

        if (page.FindName("PageBadge") is Border pageBadge)
        {
            // 正圆 + 数字居中：半径恒取边长一半，任何档位都不会变成椭圆或被裁。
            pageBadge.Width = layout.BadgeSize;
            pageBadge.Height = layout.BadgeSize;
            pageBadge.CornerRadius = new CornerRadius(layout.BadgeSize / 2);
        }

        if (page.FindName("FormCard") is Border formCard) formCard.Padding = new Thickness(layout.CardPadding);
        if (page.FindName("SharesCard") is Border sharesCard) sharesCard.Padding = new Thickness(layout.SecondaryCardPadding);
        if (page.FindName("SharesHeaderGrid") is Grid sharesHeader) sharesHeader.ColumnSpacing = layout.SharesHeaderGap;
        if (page.FindName("EmptySharesHint") is StackPanel emptyHint) emptyHint.Spacing = layout.EmptyHintGap;

        var inputPadding = new Thickness(layout.InputPaddingLeft, layout.InputPaddingY, layout.InputPaddingRight, layout.InputPaddingY);
        foreach (var name in Step1Inputs)
        {
            if (page.FindName(name) is not TextBox box) continue;
            box.MinHeight = layout.InputHeight;
            box.Padding = inputPadding;
        }

        if (page.FindName("PasswordInput") is PasswordBox password)
        {
            password.MinHeight = layout.InputHeight;
            password.Padding = new Thickness(
                layout.InputPaddingLeft, layout.InputPaddingY, layout.PasswordPaddingRight, layout.InputPaddingY);
        }

        foreach (var name in Step1InputIcons)
        {
            if (page.FindName(name) is not FontIcon icon) continue;
            icon.FontSize = layout.IconSize;
            icon.Margin = new Thickness(layout.InputIconMarginLeft, 0, 0, 0);
        }

        if (page.FindName("FormGrid") is Grid formGrid)
        {
            formGrid.RowSpacing = layout.FieldRowSpacing;
            formGrid.ColumnSpacing = layout.FieldColumnSpacing;
            ApplyFormReflow(page, formGrid, layout);
        }
    }

    /// <summary>
    /// §38 核心：Compact 时把「IP / 用户名 / 密码 / 连接按钮」从硬挤的横向排布改为**局部两行**
    /// （用户名、密码各自整行，连接按钮整行右对齐区），仍不够时才允许；Wide / Normal 恢复原样。
    /// 重排只动 Row/Column/对齐与行定义数量，不改任何绑定或事件。
    /// </summary>
    private static void ApplyFormReflow(FrameworkElement page, Grid formGrid, ResponsiveLayout layout)
    {
        var hostLabel = page.FindName("HostLabel") as TextBlock;
        var hostInput = page.FindName("HostInputGrid") as Grid;
        var userLabel = page.FindName("UsernameLabel") as TextBlock;
        var userInput = page.FindName("UsernameInputGrid") as Grid;
        var passwordLabel = page.FindName("PasswordLabel") as TextBlock;
        var passwordInput = page.FindName("PasswordInputGrid") as Grid;
        var benchmark = page.FindName("BenchmarkPanel") as StackPanel;
        var connect = page.FindName("ConnectButton") as Button;

        if (layout.IsCompact)
        {
            // 5 行：IP / 用户名 / 密码 / 测速说明 / 连接按钮（后两行是本次重排新增的 Auto 行）。
            while (formGrid.RowDefinitions.Count < 5) formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            if (hostLabel is not null) { Grid.SetRow(hostLabel, 0); Grid.SetColumn(hostLabel, 0); Grid.SetColumnSpan(hostLabel, 1); }
            if (hostInput is not null) { Grid.SetRow(hostInput, 0); Grid.SetColumn(hostInput, 1); Grid.SetColumnSpan(hostInput, 3); }
            if (userLabel is not null) { Grid.SetRow(userLabel, 1); Grid.SetColumn(userLabel, 0); Grid.SetColumnSpan(userLabel, 1); }
            if (userInput is not null) { Grid.SetRow(userInput, 1); Grid.SetColumn(userInput, 1); Grid.SetColumnSpan(userInput, 3); }
            if (passwordLabel is not null) { Grid.SetRow(passwordLabel, 2); Grid.SetColumn(passwordLabel, 0); Grid.SetColumnSpan(passwordLabel, 1); }
            if (passwordInput is not null) { Grid.SetRow(passwordInput, 2); Grid.SetColumn(passwordInput, 1); Grid.SetColumnSpan(passwordInput, 3); }
            if (benchmark is not null) { Grid.SetRow(benchmark, 3); Grid.SetColumn(benchmark, 0); Grid.SetColumnSpan(benchmark, 5); }
            if (connect is not null)
            {
                Grid.SetRow(connect, 4);
                Grid.SetColumn(connect, 0);
                Grid.SetColumnSpan(connect, 5);
                Grid.SetRowSpan(connect, 1);
                connect.HorizontalAlignment = HorizontalAlignment.Stretch; // 整行宽，消除"按钮文字被裁"
                connect.VerticalAlignment = VerticalAlignment.Center;      // A24：Compact 只有一行高，必须显式复位成 Center，
                                                                           //      否则从 Wide/Normal 缩回来后仍带 Stretch（按钮被拉成整行高）
                connect.MinWidth = layout.ConnectButtonMinWidth;           // Compact = 0
            }

            return;
        }

        // Wide / Normal：恢复原 3 行结构与原始站位（先复位站位，再删掉多余行）。
        if (hostLabel is not null) { Grid.SetRow(hostLabel, 0); Grid.SetColumn(hostLabel, 0); Grid.SetColumnSpan(hostLabel, 1); }
        if (hostInput is not null) { Grid.SetRow(hostInput, 0); Grid.SetColumn(hostInput, 1); Grid.SetColumnSpan(hostInput, 3); }
        if (userLabel is not null) { Grid.SetRow(userLabel, 1); Grid.SetColumn(userLabel, 0); Grid.SetColumnSpan(userLabel, 1); }
        if (userInput is not null) { Grid.SetRow(userInput, 1); Grid.SetColumn(userInput, 1); Grid.SetColumnSpan(userInput, 1); }
        if (passwordLabel is not null) { Grid.SetRow(passwordLabel, 1); Grid.SetColumn(passwordLabel, 2); Grid.SetColumnSpan(passwordLabel, 1); }
        if (passwordInput is not null) { Grid.SetRow(passwordInput, 1); Grid.SetColumn(passwordInput, 3); Grid.SetColumnSpan(passwordInput, 1); }
        if (benchmark is not null) { Grid.SetRow(benchmark, 2); Grid.SetColumn(benchmark, 0); Grid.SetColumnSpan(benchmark, 5); }
        if (connect is not null)
        {
            Grid.SetRow(connect, 0);
            Grid.SetColumn(connect, 4);
            Grid.SetColumnSpan(connect, 1);
            Grid.SetRowSpan(connect, 2);
            connect.HorizontalAlignment = HorizontalAlignment.Stretch;
            // A24（用户人工标注项）：按钮跨 Row0+Row1 却设了 VerticalAlignment=Center，
            // 因此只在两行中间居中、上下留空 —— 标注要求「上沿贴 IP 输入框上沿、下沿贴用户名/密码框下沿」。
            // 跨两行 + Stretch 正好得到该几何：两行都是 Auto 高（＝输入框 MinHeight），
            // 故按钮高度 = 输入框高 × 2 + RowSpacing；只改对齐方式，不动绑定与 Click。
            connect.VerticalAlignment = VerticalAlignment.Stretch;
            connect.MinWidth = layout.ConnectButtonMinWidth; // Wide = 196（与现状一致）
        }

        while (formGrid.RowDefinitions.Count > 3) formGrid.RowDefinitions.RemoveAt(formGrid.RowDefinitions.Count - 1);
    }

    /// <summary>
    /// A34（20260928，用户第三轮反馈）：把受控卡高写到侧栏里每一张步骤卡上。
    /// 步骤卡位于 ItemsControl 的 DataTemplate 内、元素名为 StepCardButton；
    /// 因此这里遍历侧栏元素的视觉树去命中它们。
    /// 纯视觉赋值：找不到就跳过，不影响任何绑定、状态与点击行为。
    /// </summary>
    private static void ApplyStepCardHeight(DependencyObject parent, double height)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is not FrameworkElement fe) continue;
            if (fe.Name == "StepCardButton") fe.Height = height;
            ApplyStepCardHeight(child, height);
        }
    }
}