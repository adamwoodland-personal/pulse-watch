using System.ComponentModel;
using System.Runtime.CompilerServices;
using ConnectionChecker.Models;

namespace ConnectionChecker;

/// <summary>One tab above the target list: Default (Id null) or a user group.</summary>
public sealed class GroupTab : INotifyPropertyChanged
{
    private string _name = "";
    private int _count;
    private int _offline;
    private bool _isEditing;
    private bool _isDropTarget;
    private bool _canMoveLeft;
    private bool _canMoveRight;

    public Guid? Id { get; init; }

    public bool IsDefault => Id == null;

    /// <summary>Default can't be renamed, moved or deleted.</summary>
    public bool CanEdit => !IsDefault;

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    /// <summary>Targets in this group.</summary>
    public int Count
    {
        get => _count;
        set { _count = value; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); }
    }

    /// <summary>Active targets in this group that are down: the tab shows a red dot, so an outage on a tab that isn't showing still stands out.</summary>
    public int Offline
    {
        get => _offline;
        set { _offline = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasOffline)); OnPropertyChanged(nameof(Summary)); }
    }

    public bool HasOffline => Offline > 0;

    public string Summary =>
        (Count == 1 ? "1 target" : $"{Count} targets") +
        (Offline > 0 ? $" · {Offline} offline" : "") +
        (IsDefault ? "" : " · double-click to rename, right-click for more");

    /// <summary>The name is being edited in place.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set { _isEditing = value; OnPropertyChanged(); }
    }

    /// <summary>A target row is being dragged over this tab.</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set { _isDropTarget = value; OnPropertyChanged(); }
    }

    public bool CanMoveLeft
    {
        get => _canMoveLeft;
        set { _canMoveLeft = value; OnPropertyChanged(); }
    }

    public bool CanMoveRight
    {
        get => _canMoveRight;
        set { _canMoveRight = value; OnPropertyChanged(); }
    }

    public static GroupTab Default() => new() { Name = HostGroup.DefaultName };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
