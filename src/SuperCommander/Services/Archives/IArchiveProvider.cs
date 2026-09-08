using SuperCommander.Services;

namespace SuperCommander.Services.Archives;

/// <summary>One entry in an archive, flattened. Directories may be implicit.</summary>
public sealed class ArchiveEntryInfo
{
    /// <summary>Path inside the archive, always '/' separated.</summary>
    public required string FullName { get; init; }

    public long Length { get; init; }
    public DateTime LastWriteTime { get; init; }
    public bool IsDirectory { get; init; }
}

public sealed class ArchiveProgress
{
    public string CurrentEntry { get; set; } = string.Empty;
    public int Done { get; set; }
    public int Total { get; set; }
    public double Percent => Total > 0 ? Done * 100.0 / Total : 0;
}

/// <summary>
/// Reads (and sometimes writes) one family of archive formats. Providers are
/// asked in order and the first that claims a path handles it.
/// </summary>
public interface IArchiveProvider
{
    /// <summary>Short name shown in dialogs, e.g. "zip" or "7-Zip".</summary>
    string Name { get; }

    /// <summary>True when this provider recognises the file by extension.</summary>
    bool CanHandle(string path);

    /// <summary>False for read-only formats such as RAR.</summary>
    bool CanCreate(string path);

    /// <summary>
    /// True when the provider is actually usable right now. The 7-Zip provider
    /// returns false when 7z.exe is not installed.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Explains why the provider is unavailable, for the error dialog.</summary>
    string? UnavailableReason { get; }

    List<ArchiveEntryInfo> List(string archivePath);

    Task<FileOperationResult> ExtractAsync(string archivePath, IReadOnlyList<string>? entries,
        string targetDirectory, IProgress<ArchiveProgress>? progress, CancellationToken token);

    Task<FileOperationResult> CreateAsync(IReadOnlyList<string> sources, string archivePath,
        string baseDirectory, IProgress<ArchiveProgress>? progress, CancellationToken token);
}
