using System.IO;
using System.IO.Compression;
using SuperCommander.Models;
using SuperCommander.Services.Archives;

namespace SuperCommander.Services;

/// <summary>
/// Front door for archive support. Picks a provider by extension and presents
/// the flat entry list as a browsable folder tree.
///
/// ZIP and TAR/GZ are handled by the base framework; everything else is offered
/// to an installed 7-Zip, which is optional.
/// </summary>
public static class ArchiveService
{
    private static readonly IArchiveProvider[] Providers =
    {
        new ZipArchiveProvider(),
        new TarArchiveProvider(),
        new SevenZipProvider()
    };

    public static IArchiveProvider? Find(string path) =>
        Providers.FirstOrDefault(p => p.CanHandle(path));

    /// <summary>True when some provider recognises the extension.</summary>
    public static bool IsSupported(string path) => Find(path) is not null;

    /// <summary>True when the format can be read right now (7-Zip present, etc.).</summary>
    public static bool IsReadable(string path) => Find(path) is { IsAvailable: true };

    public static bool CanCreate(string path) => Find(path) is { } p && p.IsAvailable && p.CanCreate(path);

    /// <summary>Explains why a recognised format cannot be opened, or null.</summary>
    public static string? UnavailableReason(string path) => Find(path)?.UnavailableReason;

    /// <summary>Extensions the app can create, for the pack dialog hint.</summary>
    public static string CreatableExtensions =>
        string.Join(", ", Providers.Where(p => p.IsAvailable)
            .SelectMany(p => new[] { ".zip", ".tar", ".tar.gz", ".7z" }.Where(p.CanCreate))
            .Distinct());

    // ------------------------------------------------------------- browsing

    /// <summary>
    /// Lists the immediate children of <paramref name="internalDirectory"/> inside
    /// an archive. Pass an empty string for the archive root.
    /// </summary>
    public static List<FileItem> ListEntries(string archivePath, string internalDirectory, out string? error)
    {
        error = null;
        var items = new List<FileItem>();

        // ".." always walks out - either up a level or back to the filesystem.
        items.Add(FileItem.CreateParent(internalDirectory.Length == 0
            ? Path.GetDirectoryName(archivePath) ?? archivePath
            : ParentOf(internalDirectory)));

        var provider = Find(archivePath);
        if (provider is null)
        {
            error = $"\"{Path.GetFileName(archivePath)}\" is not a supported archive.";
            return items;
        }

        if (!provider.IsAvailable)
        {
            error = provider.UnavailableReason;
            return items;
        }

        List<ArchiveEntryInfo> entries;
        try
        {
            entries = provider.List(archivePath);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return items;
        }

        var prefix = internalDirectory.Length == 0 ? string.Empty : internalDirectory.TrimEnd('/') + "/";
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
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
                        Path.Combine(archivePath, prefix + folder),
                        folder, isDirectory: true, FileItem.SizeUnknown,
                        entry.LastWriteTime, entry.LastWriteTime, FileAttributes.Directory)
                    {
                        ArchiveEntryPath = prefix + folder
                    });
                }
                continue;
            }

            if (entry.IsDirectory)
            {
                if (!directories.Add(relative)) continue;

                items.Add(new FileItem(
                    Path.Combine(archivePath, full),
                    relative, isDirectory: true, FileItem.SizeUnknown,
                    entry.LastWriteTime, entry.LastWriteTime, FileAttributes.Directory)
                {
                    ArchiveEntryPath = full
                });
                continue;
            }

            items.Add(new FileItem(
                Path.Combine(archivePath, full),
                relative, isDirectory: false, entry.Length,
                entry.LastWriteTime, entry.LastWriteTime, FileAttributes.Normal)
            {
                ArchiveEntryPath = full
            });
        }

        return items;
    }

    private static string ParentOf(string internalDirectory)
    {
        var trimmed = internalDirectory.TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        return slash < 0 ? string.Empty : trimmed[..slash];
    }

    // -------------------------------------------------------- pack / unpack

    public static Task<FileOperationResult> PackAsync(IReadOnlyList<string> sources, string archivePath,
        string baseDirectory, IProgress<ArchiveProgress>? progress, CancellationToken token)
    {
        var provider = Find(archivePath);

        if (provider is null || !provider.CanCreate(archivePath))
        {
            return Task.FromResult(Failure(
                $"Creating \"{Path.GetExtension(archivePath)}\" archives is not supported. " +
                $"Try one of: {CreatableExtensions}."));
        }

        if (!provider.IsAvailable) return Task.FromResult(Failure(provider.UnavailableReason!));

        return provider.CreateAsync(sources, archivePath, baseDirectory, progress, token);
    }

    public static Task<FileOperationResult> UnpackAsync(string archivePath, string targetDirectory,
        IReadOnlyList<string>? onlyEntries, IProgress<ArchiveProgress>? progress, CancellationToken token)
    {
        var provider = Find(archivePath);

        if (provider is null)
            return Task.FromResult(Failure($"\"{Path.GetFileName(archivePath)}\" is not a supported archive."));

        if (!provider.IsAvailable) return Task.FromResult(Failure(provider.UnavailableReason!));

        return provider.ExtractAsync(archivePath, onlyEntries, targetDirectory, progress, token);
    }

    private static FileOperationResult Failure(string message)
    {
        var result = new FileOperationResult { Failed = 1 };
        result.Errors.Add(message);
        return result;
    }

    // ----------------------------------------------------------- single read

    /// <summary>Reads one entry into memory - used by the internal viewer.</summary>
    public static byte[]? ReadEntry(string archivePath, string entryPath, long maxBytes = 64 * 1024 * 1024)
    {
        var provider = Find(archivePath);
        if (provider is null || !provider.IsAvailable) return null;

        // ZIP can stream a single entry directly; everything else goes via a
        // temporary extraction, which is still far cheaper than the whole archive.
        if (provider is ZipArchiveProvider)
        {
            try
            {
                using var archive = ZipFile.OpenRead(archivePath);
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

        var scratch = Path.Combine(Path.GetTempPath(), "SuperCommander", "view",
            Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(scratch);

            var result = provider.ExtractAsync(archivePath, new[] { entryPath }, scratch, null,
                CancellationToken.None).GetAwaiter().GetResult();

            if (result.Succeeded == 0) return null;

            var extracted = Directory.EnumerateFiles(scratch, "*", SearchOption.AllDirectories).FirstOrDefault();
            if (extracted is null) return null;

            var info = new FileInfo(extracted);
            return info.Length > maxBytes ? null : File.ReadAllBytes(extracted);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch (Exception) { /* temp */ }
        }
    }
}
