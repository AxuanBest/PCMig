using System.Collections.ObjectModel;
using PCMig.Core.Util;

namespace PCMig.Gui;

/// <summary>
/// 目录树节点（懒加载：未展开时挂占位子节点以显示展开箭头）。
/// 级联三态勾选：
///   勾父 → 已加载子项全勾，未加载子项展开时继承“勾”；
///   取消某个子项 → 父变半勾（■），其他子项保持；
///   半勾父新展开的子项默认勾（因为半勾=“只是排除了你亲手取消的那些”）。
/// 共享本身是根节点（IsShareRoot=true，勾=整盘迁移）。子节点混合 DirNode（目录）与 FileRow（文件）。
/// </summary>
public sealed class DirNode : ViewModelBase
{
    private bool? _isChecked = false;

    public required string Name { get; init; }
    public required string FullPath { get; init; }

    /// <summary>是否为共享根节点（如 E$ / D$ / 手动共享）。</summary>
    public bool IsShareRoot { get; init; }

    /// <summary>父节点（级联用）。根为 null。</summary>
    public DirNode? Parent { get; set; }

    /// <summary>三态勾选：true=全选，false=全不选，null=部分（半勾 ■）。UI 只读展示，点击走 SetChecked。</summary>
    public bool? IsChecked { get => _isChecked; set => SetChecked(value); }

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

    /// <summary>添加占位节点使 TreeView 显示展开箭头。</summary>
    public void AddDummy() { if (Children.Count == 0) Children.Add(new DirNode { Name = "…", FullPath = "" }); }

    /// <summary>用户点击语义：只要不是全选 → 全选；全选 → 全不选。向下级联，向上重算。</summary>
    public void ToggleFromUi() => SetChecked(_isChecked == true ? false : true);

    /// <summary>设置勾选态。true/false 会向下级联到所有已加载子项；null（半勾）不级联。最后向上重算父链。</summary>
    public void SetChecked(bool? value, bool cascadeUp = true)
    {
        var changed = _isChecked != value;
        _isChecked = value;
        if (changed) OnPropertyChanged(nameof(IsChecked));
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
        OnPropertyChanged(nameof(IsChecked));
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
            if (ns != _isChecked) { _isChecked = ns; OnPropertyChanged(nameof(IsChecked)); }
        }
        Parent?.RecomputeFromChildren();
    }
}

/// <summary>文件节点（目录树的叶子）。勾选变化向上触发父链重算。</summary>
public sealed class FileRow : ViewModelBase
{
    private bool _isChecked;
    public required string Name { get; init; }
    public required string DirPath { get; init; }
    public required long Size { get; init; }
    public string SizeText => Format.Bytes(Size);

    /// <summary>父目录节点（级联用）。</summary>
    public DirNode? Parent { get; set; }

    public bool IsChecked
    {
        get => _isChecked;
        set { if (Set(ref _isChecked, value)) Parent?.RecomputeFromChildren(); }
    }

    /// <summary>静默赋值（父级联/加载继承用），不触发向上重算。</summary>
    public void SetCheckedSilent(bool value) => Set(ref _isChecked, value);
}
