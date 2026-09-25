namespace PCMig.WinUI.Presentation;

/// <summary>WinUI display projection of a Core-discovered SMB share.</summary>
public sealed class ShareItem : ObservableObject
{
    private bool _isSelected;

    public required string Name { get; init; }
    public required string UncPath { get; init; }
    public required string Kind { get; init; }
    public string Remark { get; init; } = string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}