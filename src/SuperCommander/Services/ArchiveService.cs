using System.IO;
using System.IO.Compression;
using SuperCommander.Models;

namespace SuperCommander.Services;

/// <summary>
/// ZIP support: browse in place, pack and unpack. Other archive formats are
/// handed to their registered application instead of being opened here.
/// </summary>
public static class ArchiveService
{
    public static bool IsSupported(string path) =>
        string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------- browsing

    /// <summary>
    /// Lists the immediate children of <paramref name="internalDirectory"/> inside a zip.
    /// Pass an empty string for the archive root.
    /// </summary>
    public static List<FileItem> ListEntries(string zipPath, string internalDirectory, out string? error)
    {
        error = null;
        var items = new List<FileItem>();

        // ".." always walks out - either up a level or back to the filesystem.
        items.Add(FileItem.CreateParent(internalDirectory.Length == 0
            ? Path.GetDirectoryName(zipPath) ?? zipPath
            : ParentOf(internalDirectory)));

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);

            var prefix = internalDirectory.Length == 0 ? string.Empty : internalDirectory.TrimEnd('/') + "/";
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in archive.Entries)
            {
                var full = entry.FullName.Replace('\\', '/');
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                var relative = full[prefix.Length..];
                if (relative.Length == 0) continue;

                int slash = relative.IndexOf('/');
                if (slash >= 0)
                {
                    // Nested - surface the intermediate folder once.
                    var folder = relative[..slash];
                    if (folder.Length > 0 && directories.Add(folder))
                    {
                        items.Add(new FileItem(
                            Path.Combine(zipPath, prefix + folder),
                            folder, isDirectory: true, FileItem.SizeUnknown,
                            entry.LastWriteTime.LocalDateTime, entry.LastWriteTime.LocalDateTime,
                            FileAttributes.Directory)
                        {
                            ArchiveEntryPath = prefix + folder
                        });
                    }
                    continue;
                }

                // An explicit directory entry ends with '/', already handled above.
                if (entry.Name.Length == 0) continue;

                items.Add(new FileItem(
                    Path.Combine(zipPath, full),
                    entry.Name, isDirectory: false, entry.Length,
                    entry.LastWriteTime.LocalDateTime, entry.LastWriteTime.LocalDateTime,
                    FileAttributes.Normal)
                {
                    ArchiveEntryPath = full
                });
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        return items;
    }

    private static string ParentOf(string internalDirectory)
    {
        var trimmed = internalDirectory.TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        return slash < 0 ? string.Empty : trimmed[..slash];
    }

    // --------------------------------------------------------------- packing

    public sealed class ArchiveProgress
    {
        public string CurrentEntry { get; set; } = string.Empty;
        public int Done { get; set; }
        public int Total { get; set; }
        public double Percent => Total > 0 ? Done * 100.0 / Total : 0;
    }

    /// <summary>Creates a zip from files and folders.</summary>
    public static Task<FileOperationResult> PackAsync(IReadOnlyList<string> sources, string zipPath,
        string baseDirectory, CompressionLevel level, IProgress<ArchiveProgress>? progress,
        CancellationToken token) =>
        Task.Run(() =>
        {
            var result = new FileOperationResult();
            var report = new ArchiveProgress();

            var files = new List<(string Source, string Entry)>();
            foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested();

                if (Directory.Exists(source))
                {
                    foreach (var file in Directory.EnumerateFiles(source, "*", new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = 0
                    }))
                    {
                        files.Add((file, Path.GetRelativePath(baseDirectory, file).Replace('\\', '/')));
                    }
                }
                else if (File.Exists(source))
                {
                    files.Add((source, Path.GetRelativePath(baseDirectory, source).Replace('\\', '/')));
                }
            }

            report.Total = files.Count;

            try
            {
                var directory = Path.GetDirectoryName(zipPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                using var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

                foreach (var (source, entryName) in files)
                {
                    if (token.IsCancellationRequested)
                    {
                        result.Cancelled = true;
                        break;
                    }

                    report.CurrentEntry = entryName;
                    progress?.Report(new ArchiveProgress
                    {
                        CurrentEntry = entryName,
                        Done = report.Done,
                        Total = report.Total
                    });

                    try
                    {
                        archive.CreateEntryFromFile(source, entryName, level);
                        result.Succeeded++;
                    }
                    catch (Exception ex)
                    {
                        result.Failed++;
                        result.Errors.Add($"{source}: {ex.Message}");
                    }

                    report.Done++;
                }
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add(ex.Message);
            }

            progress?.Report(new ArchiveProgress { Done = report.Done, Total = report.Total });
            return result;
        }, token);

    // ------------------------------------------------------------- unpacking

    /// <summary>
    /// Extracts a zip. When <paramref name="onlyEntries"/> is non-empty only those
    /// entry paths are extracted, which is how F5 works inside an archive.
    /// </summary>
    public static Task<FileOperationResult> UnpackAsync(string zipPath, string targetDirectory,
        IReadOnlyList<string>? onlyEntries, IProgress<ArchiveProgress>? progress, CancellationToken token) =>
        Task.Run(() =>
        {
            var result = new FileOperationResult();

            try
            {
                Directory.CreateDirectory(targetDirectory);
                using var archive = ZipFile.OpenRead(zipPath);

                var wanted = onlyEntries is { Count: > 0 }
                    ? new HashSet<string>(onlyEntries.Select(e => e.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase)
                    : null;

                var entries = archive.Entries
                    .Where(e => e.Name.Length > 0)
                    .Where(e => wanted is null || Matches(wanted, e.FullName.Replace('\\', '/')))
                    .ToList();

                int done = 0;
                foreach (var entry in entries)
                {
                    if (token.IsCancellationRequested)
                    {
                        result.Cancelled = true;
                        break;
                    }

                    progress?.Report(new ArchiveProgress
                    {
                        CurrentEntry = entry.FullName,
                        Done = done,
                        Total = entries.Count
                    });

                    try
                    {
                        var destination = SafeCombine(targetDirectory, entry.FullName);
                        var directory = Path.GetDirectoryName(destination);
                        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                        entry.ExtractToFile(destination, overwrite: true);
                        result.Succeeded++;
                    }
                    catch (Exception ex)
                    {
                        result.Failed++;
                        result.Errors.Add($"{entry.FullName}: {ex.Message}");
                    }

                    done++;
                }

                progress?.Report(new ArchiveProgress { Done = done, Total = entries.Count });
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add(ex.Message);
            }

            return result;
        }, token);

    private static bool Matches(HashSet<string> wanted, string entryPath)
    {
        if (wanted.Contains(entryPath)) return true;

        // A selected folder pulls in everything beneath it.
        foreach (var w in wanted)
        {
            if (entryPath.StartsWith(w.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Joins an entry name onto the target directory while refusing paths that
    /// would escape it (zip slip).
    /// </summary>
    private static string SafeCombine(string targetDirectory, string entryName)
    {
        var cleaned = entryName.Replace('/', Path.DirectorySeparatorChar);
        var combined = Path.GetFullPath(Path.Combine(targetDirectory, cleaned));
        var root = Path.GetFullPath(targetDirectory);

        if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Entry \"{entryName}\" would extract outside the target folder.");

        return combined;
    }

    /// <summary>Reads a single entry into memory - used by the internal viewer.</summary>
    public static byte[]? ReadEntry(string zipPath, string entryPath, long maxBytes = 64 * 1024 * 1024)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.GetEntry(entryPath.Replace('\\', '/'));
            if (entry is null || entry.Length > maxBytes) return null;

            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
