using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace PCMig.WinUI.Presentation;

/// <summary>四个 Step 的身份。顺序即导航顺序，不允许在别处重新定义。</summary>
public enum StepKind
{
    Connect = 1,
    SelectData = 2,
    Progress = 3,
    Result = 4,
}

/// <summary>
/// 单个 Step 的导航项（纯 UI 状态）。
/// 只承载"侧栏要显示什么、是否当前页"，**不承载任何业务可用性**：
/// 按用户口径，Navigation availability 与 Business action availability 必须分离——
/// 未连接 / 未选数据 / 未开始迁移都不得让后三个 Step 变成"点不了"。
///
/// 选中态在这里一次性翻译成**实际 Brush / Style 键 / Translation 字符串**，
/// 好处：XAML 侧只需要一个 ItemsControl + 一个 DataTemplate，四个页面的侧栏样式
/// 不可能各写一套，也就不可能选态不一致。
/// </summary>
public sealed class StepNavItem : ObservableObject
{
    private bool _isSelected;
    private bool _isVisited;

    public StepNavItem(StepKind kind, string title, string subtitle, string glyph)
    {
        Kind = kind;
        Title = title;
        Subtitle = subtitle;
        Glyph = glyph;
    }

    public StepKind Kind { get; }
    public int Index => (int)Kind;
    public string Title { get; }
    public string Subtitle { get; }
    public string Glyph { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set
        {
            if (!Set(ref _isSelected, value)) return;
            RaiseAll();

        }
    }

    /// <summary>选中卡左缘的蓝色竖条（参考图里选中态独有特征）。用 Opacity 绑定避免转换器。</summary>
    public double SelectionBarOpacity => IsSelected ? 1.0 : 0.0;

    // ── 选中态 → 展示值（纯映射，不含业务语义） ──────────────────────────────

    /// <summary>
    /// "已访问态"：当前 Step 之前的步骤。参考图里这些步骤的序号圆与标题变蓝
    /// （Step2 稿里第 1 张仍是普通态，Step3/Step4 稿里它已变蓝）——即用户走过之后留下的轨迹。
    /// 纯展示状态，不含任何业务可用性，也不影响卡片是否可点。
    /// </summary>
    public bool IsVisited
    {
        get => _isVisited;
        internal set
        {
            if (!Set(ref _isVisited, value)) return;
            RaiseAll();
        }
    }

    private void RaiseAll()
    {
        Raise(nameof(CardBackground));
        Raise(nameof(CardBorder));
        Raise(nameof(BadgeBrush));
        Raise(nameof(IconBrush));
        Raise(nameof(TitleBrush));
        Raise(nameof(SubtitleBrush));
        Raise(nameof(SelectionBarOpacity));
    }
    /// <summary>
    /// 【事故恢复 / A36】Step Card 的材质**接回全局语义 Material Token**。
    ///
    /// 事故：此前为做"浮雕/受光"，把卡片背景改成自建固定画刷
    /// （NavStepCardIdleSurfaceBrush / NavStepCardSelectedSurfaceBrush），
    /// 并把高光/暗部做成独立叠加层盖在卡面上。后果：
    ///   · Step Card 表面不再引用任何 Semantic Material Token；
    ///   · Developer Visual Tuning 只会改 6 个 PCMig*Material 画刷的 alpha，
    ///     因此对 Step Card **完全失去作用**；
    ///   · 叠加的暗部渐变让卡面出现"灰黑脏阴影"观感。
    ///
    /// 恢复：背景直接引用**受 DVT 控制**的语义 token ——
    ///   · 未选中 → PCMigCardMaterial（"Card" 层：DVT 的「内容卡片」与「全局材质强度」都作用于它）
    ///   · 选中   → PCMigInsetMaterial（"Inset" 层：35% 白，比未选中**更轻透**）
    /// DVT 是**原地修改画刷实例的 Color**（不替换资源对象），故这里直接返回资源实例即可实时响应拖动。
    ///
    /// A37（20260928，用户验收反馈）：选中态此前映射到 PCMigControlMaterial（#D6FFFFFF，84% 白），
    /// 实测呈现为「纯白不透明」，用户明确要求「暂时不需要这个」。
    /// 现改为 PCMigInsetMaterial —— 同样是受 DVT 控制的语义 token（"Inset" 层），
    /// 但明显更轻、更透；选中态的「当前步骤」识别主要来自
    /// 左侧蓝色 Indicator + 数字 Badge + 标题颜色，而不是一整块不透明白底。
    ///
    /// 边框：不再自绘"高光边/轮廓"。未选中 = 无描边（边界由 Surface 明暗与阴影定义）；
    ///       选中 = 语义强调色 AccentEdgeBrush（同样来自 Token 体系）。
    /// </summary>
    public Brush? CardBackground => Res(IsSelected ? "PCMigInsetMaterial" : "PCMigCardMaterial");
    public Brush? CardBorder => IsSelected ? Res("AccentEdgeBrush") : null;

    /// <summary>
    /// 展示画刷。
    ///
    /// ★ U63（2026-09-28，用户人工标注，紧急修复）：**"已访问态"不得再使用强调色**。
    ///   用户标注原文：「这两个我没有点击，没有选中状态，他们俩为什么也是蓝色？」
    ///   —— 红框框住的正是 Step1 / Step2 两张卡：它们只是"被走过"，却因为下面的
    ///   <c>IsSelected || IsVisited</c> 映射把**序号圆与标题**都染成了强调蓝 `#1677F2`
    ///   （像素实测：导航卡 1 标题区 winner = #1677F2，14–22% 像素精确命中）。
    ///   用户口径：**没有选中就是灰色**，蓝色只属于"当前步骤"。
    ///   因此这里把 <see cref="IsVisited"/> 从画刷映射中移除 —— 强调色只由 <see cref="IsSelected"/> 决定。
    ///
    ///   <see cref="IsVisited"/> 字段本身保留（导航语义/将来若要表达"已完成"仍可用），
    ///   但**不再驱动任何强调色**；未选中态一律走中性色：
    ///     序号圆 = StepBadgeIdleBrush(#8FA6C6) · 标题 = TextPrimaryBrush(#10244A) · 图标 = StepIconBrush(#7C93B3)。
    /// </summary>
    public Brush? BadgeBrush => Res(IsSelected ? "AccentBrush" : "StepBadgeIdleBrush");
    public Brush? IconBrush => Res(IsSelected ? "AccentBrush" : "StepIconBrush");
    public Brush? TitleBrush => Res(IsSelected ? "AccentBrush" : "TextPrimaryBrush");
    public Brush? SubtitleBrush => Res(IsSelected ? "TextSecondaryBrush" : "TextMutedBrush");

    private static Brush? Res(string key) =>
        Application.Current?.Resources is { } r && r.TryGetValue(key, out var v) && v is Brush b ? b : null;

}

/// <summary>
/// 四步导航的宿主状态：一个 ObservableCollection + 一个"当前页"。
/// 页面切换本身只改这个状态；**业务动作是否可用由各页自己按 Core 状态决定**，
/// 导航层不做任何"未连接就不许去 Step2"之类的拦截。
/// </summary>
public sealed class StepNavigation : ObservableObject
{
    private StepKind _current = StepKind.Connect;

    public StepNavigation()
    {
        Items = new ObservableCollection<StepNavItem>(Build());
        Apply();
    }

    public ObservableCollection<StepNavItem> Items { get; }

    /// <summary>
    /// 导航**即将**切换（此时 <see cref="Current"/> 仍是旧值）。
    /// 只在状态与可见性变化之前给 Shell 一个时点，用来判定"这次入场要不要重放动画"。
    /// **纯通知**：不参与可用性判断，也不会取消任何导航请求。
    /// </summary>
    public event Action<StepKind, StepKind>? Changing;

    /// <summary>
    /// 导航**已经**切换完成（<see cref="Current"/> 已是新值、侧栏选中态与页面可见性都已刷新）。
    /// 与 <see cref="Changing"/> 对称，专供 Shell 在目标页真正可见**之后**播放入场动画 ——
    /// 在 Changing 时页面还不可见，任何基于 Storyboard 的起始态都会被渲染成闪帧。
    /// **纯通知**：不参与可用性判断，也不会取消任何导航请求。
    /// </summary>
    public event Action<StepKind, StepKind>? Changed;

    public StepKind Current
    {
        get => _current;
        set
        {
            if (!Enum.IsDefined(typeof(StepKind), value)) return;
            if (_current == value) return;
            var from = _current;
            Changing?.Invoke(from, value);
            if (Set(ref _current, value))
            {
                Apply();
                Changed?.Invoke(from, value);
            }
        }
    }

    public StepNavItem CurrentItem => Items.First(i => i.Kind == _current);

    public void GoTo(StepKind kind) => Current = kind;

    private void Apply()
    {
        foreach (var item in Items)
        {
            item.IsSelected = item.Kind == _current;
            item.IsVisited = (int)item.Kind < (int)_current;
        }
        Raise(nameof(CurrentItem));
        Raise(nameof(IsConnectCurrent));
        Raise(nameof(IsSelectDataCurrent));
        Raise(nameof(IsProgressCurrent));
        Raise(nameof(IsResultCurrent));
    }

    // 四个显式布尔：XAML 直接绑（不需要 Visibility 转换器）
    public bool IsConnectCurrent => _current == StepKind.Connect;
    public bool IsSelectDataCurrent => _current == StepKind.SelectData;
    public bool IsProgressCurrent => _current == StepKind.Progress;
    public bool IsResultCurrent => _current == StepKind.Result;

    /// <summary>四步的标题/副标题/图标只在**这里**定义一次（侧栏与页面标题区共用同一份来源）。</summary>
    private static IEnumerable<StepNavItem> Build() => new[]
    {
        new StepNavItem(StepKind.Connect, "连接旧电脑", "填 IP 与账号，列出共享", "\uE7F4"),
        new StepNavItem(StepKind.SelectData, "选择数据与目标", "勾选要迁移的内容", "\uE8FD"),
        new StepNavItem(StepKind.Progress, "迁移进度", "实时进度、文件流与报错", "\uE9D2"),
        new StepNavItem(StepKind.Result, "结果与校验", "完整性校验、报告、异常清单", "\uE73E"),
    };
}
