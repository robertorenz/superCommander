using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace SuperCommander.Models;

/// <summary>Drives the row colouring in the file list.</summary>
public enum FileKind
{
    Normal,
    Parent,
    Directory,
    Executable,
    Archive
}

/// <summary>
/// One row in a file pane. Doubles as the row view model: <see cref="IsMarked"/>,
/// <see cref="Icon"/> and the folder-size result all change after construction.
/// </summary>
public sealed class FileItem : INotifyPropertyChanged
{
    public const long SizeUnknown = -1;

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".msi", ".ps1", ".scr", ".vbs", ".js", ".wsf", ".jar", ".appx"
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".cab", ".iso", ".lzh", ".arj", ".zst"
    };

    private bool _isMarked;
    private ImageSource? _icon;
    private long _size;

    public FileItem(string fullPath, string name, bool isDirectory, long size,
        DateTime modified, DateTime created, FileAttributes attributes, bool isParent = false)
    {
        FullPath = fullPath;
        Name = name;
        IsDirectory = isDirectory;
        IsParent = isParent;
        _size = size;
        Modified = modified;
        Created = created;
        Attributes = attributes;

        Extension = isDirectory || isParent ? string.Empty : ExtractExtension(name);
        BaseName = Extension.Length == 0 ? name : name[..(name.Length - Extension.Length - 1)];
        Kind = Classify();
    }

    /// <summary>Builds the ".." row that walks to the parent directory.</summary>
    public static FileItem CreateParent(string parentPath) =>
        new(parentPath, "..", isDirectory: true, SizeUnknown,
            DateTime.MinValue, DateTime.MinValue, FileAttributes.Directory, isParent: true);

    // ------------------------------------------------------------ identity

    public string FullPath { get; }

    /// <summary>Full file name including extension.</summary>
    public string Name { get; }

    /// <summary>Name without the extension - the "Name" column.</summary>
    public string BaseName { get; }

    /// <summary>Extension without the leading dot - the "Ext" column.</summary>
    public string Extension { get; }

    public bool IsDirectory { get; }

    public bool IsParent { get; }

    public FileKind Kind { get; }

    /// <summary>
    /// Entry path inside a .zip when this row is being browsed from an archive
    /// rather than from disk. Null for ordinary filesystem rows.
    /// </summary>
    public string? ArchiveEntryPath { get; set; }

    /// <summary>
    /// Absolute path on the FTP server when this row came from a remote listing.
    /// Null for local rows.
    /// </summary>
    public string? RemotePath { get; set; }

    /// <summary>True when this row lives on an FTP server rather than on disk.</summary>
    public bool IsRemote => RemotePath is not null;

    // ------------------------------------------------------------- metadata

    public DateTime Modified { get; }

    public DateTime Created { get; }

    public FileAttributes Attributes { get; }

    public bool IsHidden => !IsParent && (Attributes & FileAttributes.Hidden) != 0;

    public bool IsSystem => (Attributes & FileAttributes.System) != 0;

    public bool IsReadOnly => (Attributes & FileAttributes.ReadOnly) != 0;

    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>
    /// File length in bytes. Directories start at <see cref="SizeUnknown"/> and are
    /// filled in when the user asks for occupied space.
    /// </summary>
    public long Size
    {
        get => _size;
        set
        {
            if (_size == value) return;
            _size = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplaySize));
        }
    }

    // --------------------------------------------------------------- row state

    /// <summary>Total Commander style selection, independent of the cursor row.</summary>
    public bool IsMarked
    {
        get => _isMarked;
        set
        {
            if (_isMarked == value) return;
            _isMarked = value;
            OnPropertyChanged();
        }
    }

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            OnPropertyChanged();
        }
    }

    // -------------------------------------------------------------- formatting

    public string DisplayName => IsDirectory || IsParent ? Name : BaseName;

    public string DisplaySize
    {
        get
        {
            if (IsParent) return "<UP>";
            if (IsDirectory) return _size < 0 ? "<DIR>" : FormatBytes(_size);
            return FormatBytes(_size);
        }
    }

    public string DisplayDate =>
        IsParent || Modified == DateTime.MinValue
            ? string.Empty
            : Modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    public string DisplayAttributes
    {
        get
        {
            if (IsParent) return string.Empty;
            Span<char> flags = stackalloc char[5];
            flags[0] = (Attributes & FileAttributes.ReadOnly) != 0 ? 'r' : '-';
            flags[1] = (Attributes & FileAttributes.Archive) != 0 ? 'a' : '-';
            flags[2] = (Attributes & FileAttributes.Hidden) != 0 ? 'h' : '-';
            flags[3] = (Attributes & FileAttributes.System) != 0 ? 's' : '-';
            flags[4] = (Attributes & FileAttributes.ReparsePoint) != 0 ? 'l' : '-';
            return new string(flags);
        }
    }

    public static string FormatBytes(long bytes) =>
        bytes < 0 ? string.Empty : bytes.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Human readable size used in status bars and dialogs.</summary>
    public static string FormatBytesShort(long bytes)
    {
        if (bytes < 0) return string.Empty;
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes:N0} B"
            : string.Create(CultureInfo.CurrentCulture, $"{value:N1} {units[unit]}");
    }

    // ---------------------------------------------------------------- helpers

    private static string ExtractExtension(string name)
    {
        // "readme.txt" -> "txt";  ".gitignore" -> "" (dotfiles keep the whole name)
        int dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1) return string.Empty;
        return name[(dot + 1)..];
    }

    private FileKind Classify()
    {
        if (IsParent) return FileKind.Parent;
        if (IsDirectory) return FileKind.Directory;

        var dotted = Extension.Length == 0 ? string.Empty : "." + Extension;
        if (ExecutableExtensions.Contains(dotted)) return FileKind.Executable;
        if (ArchiveExtensions.Contains(dotted)) return FileKind.Archive;
        return FileKind.Normal;
    }

    public static bool IsArchiveExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && ArchiveExtensions.Contains(ext);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public override string ToString() => FullPath;
}
