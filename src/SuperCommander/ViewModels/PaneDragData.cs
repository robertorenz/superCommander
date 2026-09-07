using SuperCommander.Models;

namespace SuperCommander.ViewModels;

/// <summary>
/// Payload for a drag that started in one of our own panels.
///
/// A plain FileDrop list is not enough once a panel can hold an FTP session:
/// remote rows have no local path, and a local path means nothing to a server.
/// Carrying the source panel and the exact rows lets the drop decide whether it
/// is a local copy, an upload or a download.
/// </summary>
public sealed class PaneDragData
{
    /// <summary>Private clipboard format; only this process understands it.</summary>
    public const string Format = "SuperCommander.PaneItems";

    public required FilePaneViewModel Source { get; init; }

    /// <summary>Rows captured when the drag began, so later selection changes cannot alter it.</summary>
    public required IReadOnlyList<FileItem> Items { get; init; }

    public bool IsRemote => Source.IsFtpMode;
}
