using System.IO;
using System.IO.Compression;

namespace SuperCommander.Services.Archives;

/// <summary>ZIP via the base framework. Always available, reads and writes.</summary>
public sealed class ZipArchiveProvider : IArchiveProvider
{
    public string Name => "zip";

    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public bool CanHandle(string path) =>
        string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

    public bool CanCreate(string path) => CanHandle(path);

    public List<ArchiveEntryInfo> List(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);

        return archive.Entries.Select(entry => new ArchiveEntryInfo
        {
            FullName = entry.FullName.Replace('\\', '/'),
            Length = entry.Length,
            LastWriteTime = entry.LastWriteTime.LocalDateTime,
            IsDirectory = entry.Name.Length == 0
        }).ToList();
    }

    public Task<FileOperationResult> ExtractAsync(string archivePath, IReadOnlyList<string>? entries,
        string targetDirectory, IProgress<ArchiveProgress>? progress, CancellationToken token) =>
        Task.Run(() =>
        {
            var result = new FileOperationResult();

            try
            {
                Directory.CreateDirectory(targetDirectory);
                using var archive = ZipFile.OpenRead(archivePath);

                var wanted = ArchivePath.BuildFilter(entries);
                var selected = archive.Entries
                    .Where(e => e.Name.Length > 0)
                    .Where(e => ArchivePath.Matches(wanted, e.FullName.Replace('\\', '/')))
                    .ToList();

                int done = 0;
                foreach (var entry in selected)
                {
                    if (token.IsCancellationRequested) { result.Cancelled = true; break; }

                    progress?.Report(new ArchiveProgress
                    {
                        CurrentEntry = entry.FullName,
                        Done = done,
                        Total = selected.Count
                    });

                    try
                    {
                        var destination = ArchivePath.SafeCombine(targetDirectory, entry.FullName);
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

                progress?.Report(new ArchiveProgress { Done = done, Total = selected.Count });
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add(ex.Message);
            }

            return result;
        }, token);

    public Task<FileOperationResult> CreateAsync(IReadOnlyList<string> sources, string archivePath,
        string baseDirectory, IProgress<ArchiveProgress>? progress, CancellationToken token) =>
        Task.Run(() =>
        {
            var result = new FileOperationResult();
            var files = ArchivePath.Enumerate(sources, baseDirectory, token);

            try
            {
                var directory = Path.GetDirectoryName(archivePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                using var stream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

                int done = 0;
                foreach (var (source, entryName) in files)
                {
                    if (token.IsCancellationRequested) { result.Cancelled = true; break; }

                    progress?.Report(new ArchiveProgress
                    {
                        CurrentEntry = entryName,
                        Done = done,
                        Total = files.Count
                    });

                    try
                    {
                        archive.CreateEntryFromFile(source, entryName, CompressionLevel.Optimal);
                        result.Succeeded++;
                    }
                    catch (Exception ex)
                    {
                        result.Failed++;
                        result.Errors.Add($"{source}: {ex.Message}");
                    }

                    done++;
                }

                progress?.Report(new ArchiveProgress { Done = done, Total = files.Count });
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add(ex.Message);
            }

            return result;
        }, token);
}
