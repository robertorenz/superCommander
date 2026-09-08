using System.Formats.Tar;
using System.IO;
using System.IO.Compression;

namespace SuperCommander.Services.Archives;

/// <summary>
/// TAR, TAR.GZ / TGZ and single-file GZ, all through the base framework
/// (System.Formats.Tar shipped in .NET 7), so no external tool is needed.
///
/// bzip2 and xz are not in the framework and are left to the 7-Zip provider.
/// </summary>
public sealed class TarArchiveProvider : IArchiveProvider
{
    public string Name => "tar";

    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public bool CanHandle(string path) => Kind(path) != TarKind.None;

    public bool CanCreate(string path) => Kind(path) is TarKind.Tar or TarKind.TarGz;

    private enum TarKind { None, Tar, TarGz, Gz }

    private static TarKind Kind(string path)
    {
        var name = Path.GetFileName(path);

        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            return TarKind.TarGz;

        if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) return TarKind.Tar;
        if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) return TarKind.Gz;

        return TarKind.None;
    }

    private static Stream OpenRead(string path, TarKind kind)
    {
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024);
        return kind == TarKind.TarGz ? new GZipStream(stream, CompressionMode.Decompress) : stream;
    }

    // ------------------------------------------------------------------ list

    public List<ArchiveEntryInfo> List(string archivePath)
    {
        var kind = Kind(archivePath);
        var entries = new List<ArchiveEntryInfo>();

        if (kind == TarKind.Gz)
        {
            // A bare .gz holds exactly one stream with no name of its own; the
            // convention is the archive name minus the extension.
            var inner = Path.GetFileNameWithoutExtension(archivePath);
            long length = -1;

            try
            {
                using var stream = OpenRead(archivePath, TarKind.Tar);
                using var gz = new GZipStream(stream, CompressionMode.Decompress);
                length = CountBytes(gz);
            }
            catch (Exception)
            {
                // Length is a nicety; listing should still work.
            }

            entries.Add(new ArchiveEntryInfo
            {
                FullName = inner,
                Length = length,
                LastWriteTime = File.GetLastWriteTime(archivePath),
                IsDirectory = false
            });

            return entries;
        }

        using var source = OpenRead(archivePath, kind);
        using var reader = new TarReader(source, leaveOpen: false);

        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            bool isDirectory = entry.EntryType is TarEntryType.Directory;

            entries.Add(new ArchiveEntryInfo
            {
                FullName = entry.Name.Replace('\\', '/').TrimEnd('/'),
                Length = isDirectory ? -1 : entry.Length,
                LastWriteTime = entry.ModificationTime.LocalDateTime,
                IsDirectory = isDirectory
            });
        }

        return entries;
    }

    private static long CountBytes(Stream stream)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) total += read;
        return total;
    }

    // --------------------------------------------------------------- extract

    public Task<FileOperationResult> ExtractAsync(string archivePath, IReadOnlyList<string>? entries,
        string targetDirectory, IProgress<ArchiveProgress>? progress, CancellationToken token) =>
        Task.Run(() =>
        {
            var result = new FileOperationResult();
            var kind = Kind(archivePath);

            try
            {
                Directory.CreateDirectory(targetDirectory);

                if (kind == TarKind.Gz)
                {
                    var destination = ArchivePath.SafeCombine(targetDirectory,
                        Path.GetFileNameWithoutExtension(archivePath));

                    progress?.Report(new ArchiveProgress { CurrentEntry = Path.GetFileName(destination), Done = 0, Total = 1 });

                    using (var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var gz = new GZipStream(input, CompressionMode.Decompress))
                    using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        gz.CopyTo(output);
                    }

                    result.Succeeded++;
                    progress?.Report(new ArchiveProgress { Done = 1, Total = 1 });
                    return result;
                }

                var wanted = ArchivePath.BuildFilter(entries);

                // TAR is a forward-only stream, so count first for a real progress bar.
                int total = 0;
                foreach (var info in List(archivePath))
                    if (!info.IsDirectory && ArchivePath.Matches(wanted, info.FullName)) total++;

                using var source = OpenRead(archivePath, kind);
                using var reader = new TarReader(source, leaveOpen: false);

                int done = 0;
                while (reader.GetNextEntry() is { } entry)
                {
                    if (token.IsCancellationRequested) { result.Cancelled = true; break; }

                    var name = entry.Name.Replace('\\', '/').TrimEnd('/');
                    if (!ArchivePath.Matches(wanted, name)) continue;

                    try
                    {
                        var destination = ArchivePath.SafeCombine(targetDirectory, name);

                        if (entry.EntryType == TarEntryType.Directory)
                        {
                            Directory.CreateDirectory(destination);
                            continue;
                        }

                        if (entry.DataStream is null) continue;

                        progress?.Report(new ArchiveProgress { CurrentEntry = name, Done = done, Total = total });

                        var directory = Path.GetDirectoryName(destination);
                        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                        using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            entry.DataStream.CopyTo(output);
                        }

                        result.Succeeded++;
                        done++;
                    }
                    catch (Exception ex)
                    {
                        result.Failed++;
                        result.Errors.Add($"{name}: {ex.Message}");
                    }
                }

                progress?.Report(new ArchiveProgress { Done = done, Total = total });
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add(ex.Message);
            }

            return result;
        }, token);

    // ---------------------------------------------------------------- create

    public Task<FileOperationResult> CreateAsync(IReadOnlyList<string> sources, string archivePath,
        string baseDirectory, IProgress<ArchiveProgress>? progress, CancellationToken token) =>
        Task.Run(() =>
        {
            var result = new FileOperationResult();
            var kind = Kind(archivePath);
            var files = ArchivePath.Enumerate(sources, baseDirectory, token);

            try
            {
                var directory = Path.GetDirectoryName(archivePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                Stream output = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None);
                if (kind == TarKind.TarGz) output = new GZipStream(output, CompressionLevel.Optimal);

                using (output)
                using (var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: false))
                {
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
                            writer.WriteEntry(source, entryName);
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
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add(ex.Message);
            }

            return result;
        }, token);
}
