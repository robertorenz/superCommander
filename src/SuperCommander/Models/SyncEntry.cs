using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;

namespace SuperCommander.Models;

/// <summary>How the two sides of one compared file relate.</summary>
public enum SyncState
{
    Same,
    LeftOnly,
    RightOnly,
    LeftNewer,
    RightNewer,
    Different
}

/// <summary>What the synchroniser will do about a compared file.</summary>
public enum SyncAction
{
    None,
    ToRight,
    ToLeft,
    DeleteLeft,
    DeleteRight
}

/// <summary>
/// One row of the synchronise grid: the same relative path seen on both sides.
/// <see cref="State"/> is the comparison result and never changes; <see cref="Action"/>
/// is the decision, which the user is free to override before synchronising.
/// </summary>
public sealed class SyncEntry : INotifyPropertyChanged
{
    private SyncAction _action;

    public required string RelativePath { get; init; }

    /// <summary>Where the file is on the left, or where a copy would land.</summary>
    public required string LeftPath { get; init; }

    /// <summary>Where the file is on the right, or where a copy would land.</summary>
    public required string RightPath { get; init; }

    public bool OnLeft { get; init; }
    public bool OnRight { get; init; }

    public long LeftSize { get; init; }
    public long RightSize { get; init; }
    public DateTime LeftModified { get; init; }
    public DateTime RightModified { get; init; }

    public SyncState State { get; init; }

    /// <summary>The action the comparison suggested, so a preset can be undone.</summary>
    public SyncAction DefaultAction { get; init; }

    public SyncAction Action
    {
        get => _action;
        set
        {
            if (_action == value) return;
            _action = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Direction));
            OnPropertyChanged(nameof(TransferSize));
        }
    }

    // -------------------------------------------------------------- the work

    /// <summary>The file the action reads, or removes when it is a delete.</summary>
    public string SourcePath => Action switch
    {
        SyncAction.ToRight or SyncAction.DeleteLeft => LeftPath,
        SyncAction.ToLeft or SyncAction.DeleteRight => RightPath,
        _ => string.Empty
    };

    /// <summary>Where a copy lands. Empty for a delete, which has no target.</summary>
    public string TargetPath => Action switch
    {
        SyncAction.ToRight => RightPath,
        SyncAction.ToLeft => LeftPath,
        _ => string.Empty
    };

    /// <summary>Bytes this action moves, so the progress total can be built up front.</summary>
    public long TransferSize => Action switch
    {
        SyncAction.ToRight => LeftSize,
        SyncAction.ToLeft => RightSize,
        _ => 0
    };

    // ------------------------------------------------------------ formatting

    public string Name => Path.GetFileName(RelativePath);

    /// <summary>The subfolder the file sits in, relative to the compared roots.</summary>
    public string Folder => Path.GetDirectoryName(RelativePath) ?? string.Empty;

    public string LeftSizeText => OnLeft ? FileItem.FormatBytes(LeftSize) : string.Empty;
    public string RightSizeText => OnRight ? FileItem.FormatBytes(RightSize) : string.Empty;

    public string LeftDateText => OnLeft ? Format(LeftModified) : string.Empty;
    public string RightDateText => OnRight ? Format(RightModified) : string.Empty;

    /// <summary>The arrow in the middle column - the whole grid at a glance.</summary>
    public string Direction => Action switch
    {
        SyncAction.ToRight => "-->",
        SyncAction.ToLeft => "<--",
        SyncAction.DeleteLeft => "x--",
        SyncAction.DeleteRight => "--x",
        _ => State == SyncState.Same ? "=" : "!="
    };

    private static string Format(DateTime value) =>
        value == DateTime.MinValue ? string.Empty : value.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public override string ToString() => $"{RelativePath} [{State}] {Direction}";
}
