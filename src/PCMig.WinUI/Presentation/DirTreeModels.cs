using System.Collections.ObjectModel;
using PCMig.Core.Util;

namespace PCMig.WinUI.Presentation;

/// <summary>
/// 目录树节点（懒加载：未展开时挂占位子节点以显示展开箭头）。
/// 级联三态勾选：
///   勾父 → 已加载子项全勾，未加载子项展开时继承“勾”；
///   取消某个子项 → 父变半勾（■），其他子项保持；
///   半勾父新展开的子项默认勾（因为半勾=“只是排除了你亲手取消的那些”）。
/// 共享本身是根节点（IsShareRoot=true，勾=整盘迁移）。子节点混合 DirNode（目录）与 FileRow（文件）。
///
/// 【来源】本类型是 <c>src\PCMig.Gui\BrowseModels.cs</c> 中同名类型的**逐语义移植**，
/// 只做两处机械替换：基类 <c>ViewModelBase</c> → <see cref="ObservableObject"/>（WinUI 侧薄基类），
/// 命名空间 <c>PCMig.Gui</c> → <c>PCMig.WinUI.Presentation</c>。
/// 级联 / 半勾 / 占位节点 / 重算规则**一行逻辑都没有改**：这些语义是产品契约，
/// 任何“顺手简化”都会让目录三态选择失真。
/// </summary>
public sealed class DirNode : ObservableObject
{
    private bool? _isChecked = false;

    // 注意（本包实测）：这两个属性**不能**写成 `required ... { get; init; }`。
    // WinUI 的 XAML 编译器会为“在 XAML 里可见的类型”生成 XamlTypeInfo 构造代码，
    // required 成员与 init-only 访问器会让生成的代码编译失败（CS9035 / CS8852，实测踩到）。
    // 因此改为带默认值的可读写属性：语义（必须有值、默认空串）与旧实现一致，
    // 只是不再由编译器强制——构造点全部显式赋值，另有 FullPath 空串=占位节点的既有约定兜底。
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;

    /// <summary>是否为共享根节点（如 E$ / D$ / 手动共享）。</summary>
    public bool IsShareRoot { get; set; }

    /// <summary>父节点（级联用）。根为 null。</summary>
    public DirNode? Parent { get; set; }

    /// <summary>三态勾选：true=全选，false=全不选，null=部分（半勾 ■）。UI 只读展示，点击走 SetChecked。</summary>
    public bool? IsChecked { get => _isChecked; set => SetChecked(value); }

    // ── 供 XAML 直接绑定的两个只读投影 ──────────────────────────────
    // 为什么不在 XAML 里直接绑 IsChecked：WinUI 的 CheckBox.IsChecked 是 bool?，
    // 但三态显示需要两个互斥元素（实心勾 / 半勾方块），若两处都绑 IsChecked 就会同时可见。
    // 故这里给出两个互斥的布尔投影，XAML 各绑一个，勾选动作仍统一走 ToggleFromUi()。
    /// <summary>true = 全选（实心勾可见）。</summary>
    public bool CheckStateTrue => _isChecked == true;
    /// <summary>true = 部分选中（半勾方块可见）。</summary>
    public bool CheckStateIndeterminate => _isChecked is null;

    private bool _isExpanded;
    /// <summary>TreeViewItem 展开状态（TwoWay 绑定，供 VM 自动展开节点）。</summary>
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    private bool _isSelected;
    /// <summary>TreeViewItem 选中状态（TwoWay 绑定）。</summary>
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>子节点：DirNode（子目录）与 FileRow（文件）混合。</summary>
    public ObservableCollection<object> Children { get; } = new();

    /// <summary>子内容是否已从远程加载过。</summary>
    public bool ChildrenLoaded { get; set; }

    /// <summary>
    /// 添加占位节点使 TreeView 显示展开箭头。
    /// ★ A.5（P0-1）：占位节点的唯一合法依据是 <see cref="ChildrenLoaded"/> —— 只有
    /// “子内容**尚未加载**”时才需要这个假箭头。加载完成后（真实空目录 / 子项被排除规则清空）
    /// **绝不允许**再补占位：那种箭头点下去只会重复得到同一个空结果（<c>EnsureChildrenAsync</c>
    /// 首行即 <c>if (node.ChildrenLoaded …) return;</c>），是纯粹欺骗用户的假 affordance。
    /// 同理，UI 侧（<c>Step2SelectDataPage.BuildNode</c>）判定是否补占位也只看 <see cref="ChildrenLoaded"/>，
    /// **不再看 <c>Children.Count</c>**：共享根在 <c>SyncRoots</c> 里已 AddDummy 过一次（Count==1），
    /// 旧判定因此恒为 false ⇒ TreeViewNode.Children 为空 ⇒ WinUI 不渲染箭头（A12 冒烟“点不到箭头”的根因）。
    /// </summary>
    public void AddDummy()
    {
        if (ChildrenLoaded || Children.Count > 0) return;
        Children.Add(new DirNode { Name = "…", FullPath = "" });
    }

    /// <summary>用户点击语义：只要不是全选 → 全选；全选 → 全不选。向下级联，向上重算。</summary>
    public void ToggleFromUi() => SetChecked(_isChecked == true ? false : true);

    /// <summary>设置勾选态。true/false 会向下级联到所有已加载子项；null（半勾）不级联。最后向上重算父链。</summary>
    public void SetChecked(bool? value, bool cascadeUp = true)
    {
        var changed = _isChecked != value;
        _isChecked = value;
        if (changed) RaiseCheckState();
        if (value.HasValue)
            foreach (var c in Children)
                switch (c)
                {
                    case DirNode d: d.SetCheckedSilent(value); break;
                    case FileRow f: f.SetCheckedSilent(value.Value); break;
                }
        if (cascadeUp) Parent?.RecomputeFromChildren();
    }

    /// <summary>静默赋值（级联/加载继承用）：向下级联，但不触发向上重算（由顶层统一触发一次）。</summary>
    public void SetCheckedSilent(bool? value)
    {
        if (_isChecked == value) return;
        _isChecked = value;
        RaiseCheckState();
        if (value.HasValue)
            foreach (var c in Children)
                switch (c)
                {
                    case DirNode d: d.SetCheckedSilent(value); break;
                    case FileRow f: f.SetCheckedSilent(value.Value); break;
                }
    }

    /// <summary>由子节点状态重算自身（全勾→勾，全不勾→不勾，否则→半勾），并向上传播。含未加载占位节点时保持现状。</summary>
    public void RecomputeFromChildren()
    {
        // 有占位节点 = 子内容未加载，无法判定 → 保持现状，但仍向上传播（父链其它分支可能变化）
        bool hasUnloadedDummy = false; // 未加载占位节点（ChildrenLoaded=false 时才有意义）
        var allTrue = true; var allFalse = true; var any = false;
        foreach (var c in Children)
        {
            if (c is DirNode d)
            {
                if (d.FullPath.Length == 0)
                {
                    if (!ChildrenLoaded) hasUnloadedDummy = true; // 已加载后的“提示节点”不参与判定
                    continue;
                }
                any = true;
                if (d.IsChecked != true) allTrue = false;
                if (d.IsChecked != false) allFalse = false;
            }
            else if (c is FileRow f)
            {
                any = true;
                if (!f.IsChecked) allTrue = false;
                if (f.IsChecked) allFalse = false;
            }
        }
        if (any && !hasUnloadedDummy)
        {
            bool? ns = allTrue ? true : allFalse ? false : null;
            if (ns != _isChecked) { _isChecked = ns; RaiseCheckState(); }
        }
        Parent?.RecomputeFromChildren();
    }

    /// <summary>
    /// 三态变化通知：**同时发 IsChecked 与两个只读投影**。
    /// IsChecked 必须发（与旧 `BrowseModels.cs` 的可观察性一致）：共享根↔共享行的联动就是
    /// 订阅 `IsChecked` 实现的（`DirectoryTreeViewModel.OnRootPropertyChanged`），少发它联动会静默失效。
    /// </summary>
    private void RaiseCheckState()
    {
        Raise(nameof(IsChecked));
        Raise(nameof(CheckStateTrue));
        Raise(nameof(CheckStateIndeterminate));
    }
}

/// <summary>文件节点（目录树的叶子）。勾选变化向上触发父链重算。</summary>
public sealed class FileRow : ObservableObject
{
    private bool _isChecked;
    // 同上：不能是 required/init —— XAML 编译器生成的 XamlTypeInfo 代码会因此编译失败。
    public string Name { get; set; } = string.Empty;
    public string DirPath { get; set; } = string.Empty;
    public long Size { get; set; }
    public string SizeText => Format.Bytes(Size);

    /// <summary>父目录节点（级联用）。</summary>
    public DirNode? Parent { get; set; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            // 必须同时发 IsChecked（联动订阅用）与 CheckStateTrue（XAML 里的勾选投影）：
            // 只发一个就会出现“点了没反应”（UI 不刷新）或“联动静默失效”。Set 只发 CallerMemberName 那一个，
            // 故这里显式补齐后再向上重算父链。
            if (_isChecked == value) return;
            _isChecked = value;
            Raise(nameof(IsChecked));
            Raise(nameof(CheckStateTrue));
            Parent?.RecomputeFromChildren();
        }
    }

    /// <summary>供 XAML 直接绑定的只读投影（与 DirNode.CheckStateTrue 同口径）。</summary>
    public bool CheckStateTrue => _isChecked;

    /// <summary>
    /// ★ A.5：文件行没有半选语义，恒 false。
    /// 存在的唯一理由：XAML 的半勾方块绑的是显式路径 <c>Content.CheckStateIndeterminate</c>，
    /// 若 FileRow 没有这个属性，该绑定会失败并让方块（默认 Visible）**在文件行上错误显示**。
    /// </summary>
    public bool CheckStateIndeterminate => false;

    /// <summary>
    /// 静默赋值（父级联/加载继承用）：不触发向上重算，但必须通知 UI。
    /// 注意不能写成 Set(ref _isChecked, value) —— 那样 [CallerMemberName] 取到的是本方法名
    /// "SetCheckedSilent"，PropertyChanged 名义不对，复选框绑定收不到通知（表现为：
    /// 勾整盘时目录勾上了、根目录下的散落文件没勾上）。这里显式发 IsChecked。
    /// </summary>
    public void SetCheckedSilent(bool value)
    {
        if (_isChecked == value) return;
        _isChecked = value;
        Raise(nameof(IsChecked));
        Raise(nameof(CheckStateTrue));
    }
}