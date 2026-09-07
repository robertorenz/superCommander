using SuperCommander.Services;

namespace SuperCommander.Models;

public enum SortColumn
{
    Name,
    Extension,
    Size,
    Date,
    Attributes
}

public sealed class SortSpec
{
    public SortColumn Column { get; set; } = SortColumn.Name;
    public bool Descending { get; set; }
}

public sealed class Bookmark
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}

public sealed class PaneSettings
{
    public List<string> TabPaths { get; set; } = new();
    public int ActiveTab { get; set; }
    public SortSpec Sort { get; set; } = new();
}

/// <summary>Everything persisted to %APPDATA%\SuperCommander\settings.json.</summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.Dark;

    // Window placement. Null means "never positioned yet" - NaN cannot be
    // represented in JSON and made the whole save throw.
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 760;
    public bool WindowMaximized { get; set; }

    /// <summary>Left pane share of the horizontal space, 0.1 - 0.9.</summary>
    public double SplitterRatio { get; set; } = 0.5;

    public PaneSettings LeftPane { get; set; } = new();
    public PaneSettings RightPane { get; set; } = new();

    // Behaviour
    public bool ShowHiddenFiles { get; set; }
    public bool ShowSystemFiles { get; set; }
    public bool ConfirmDelete { get; set; } = true;
    public bool UseRecycleBin { get; set; } = true;
    public bool DirectoriesFirst { get; set; } = true;
    public bool ShowFunctionKeyBar { get; set; } = true;
    public bool ShowCommandLine { get; set; } = true;
    public bool ShowDriveBar { get; set; } = true;
    public bool ShowIcons { get; set; } = true;

    /// <summary>External editor used by F4. Empty means notepad.</summary>
    public string ExternalEditor { get; set; } = string.Empty;

    /// <summary>External viewer used by Alt+F3. Empty means the internal viewer.</summary>
    public string ExternalViewer { get; set; } = string.Empty;

    public List<Bookmark> Bookmarks { get; set; } = new();
    public List<string> CommandHistory { get; set; } = new();
    public List<string> DirectoryHistory { get; set; } = new();

    // File list column widths (Name, Ext, Size, Date, Attr)
    public double ColumnNameWidth { get; set; } = 260;
    public double ColumnExtWidth { get; set; } = 60;
    public double ColumnSizeWidth { get; set; } = 92;
    public double ColumnDateWidth { get; set; } = 122;
    public double ColumnAttrWidth { get; set; } = 62;
}
