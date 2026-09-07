using System.IO;
using SuperCommander.Interop;

namespace SuperCommander.Services;

public enum FileOperationKind
{
    Copy,
    Move,
    Delete
}

public enum OverwriteAction
{
    Overwrite,
    OverwriteAll,
    OverwriteAllOlder,
    Skip,
    SkipAll,
    Rename,
    Cancel
}

public sealed class ConflictInfo
{
    public required string SourcePath { get; init; }
    public required string TargetPath { get; init; }
    public long SourceSize { get; init; }
    public long TargetSize { get; init; }
    public DateTime SourceModified { get; init; }
    public DateTime TargetModified { get; init; }
}

public sealed class FileOperationProgress
{
    public string CurrentFile { get; set; } = string.Empty;
    public string CurrentTarget { get; set; } = string.Empty;
    public long BytesDone { get; set; }
    public long BytesTotal { get; set; }
    public int FilesDone { get; set; }
    public int FilesTotal { get; set; }
    public double FilePercent { get; set; }

    public double TotalPercent => BytesTotal > 0 ? BytesDone * 100.0 / BytesTotal : 0;
}

public sealed class DeleteOutcome
{
    public bool Succeeded { get; init; }
    public string? Error { get; init; }
}

public sealed class FileOperationResult
{
    public int Succeeded { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public bool Cancelled { get; set; }
    public List<string> Errors { get; } = new();
}

/// <summary>
/// Threaded copy / move / delete engine with byte-level progress and a
/// caller-supplied conflict resolver.
/// </summary>
public sealed class FileOperationService
{
    private const int BufferSize = 1024 * 1024;

    private sealed record WorkItem(string Source, string Target, bool IsDirectory, long Size);

    /// <summary>
    /// Runs the operation on a worker thread. <paramref name="resolveConflict"/> is
    /// invoked on whichever thread the caller marshals it to - the UI layer wraps
    /// it in a dispatcher call.
    /// </summary>
    public Task<FileOperationResult> RunAsync(
        FileOperationKind kind,
        IReadOnlyList<string> sources,
        string targetDirectory,
        IProgress<FileOperationProgress>? progress,
        Func<ConflictInfo, OverwriteAction> resolveConflict,
        CancellationToken token,
        string? singleTargetName = null) =>
        Task.Run(() => Run(kind, sources, targetDirectory, progress, resolveConflict, token, singleTargetName), token);

    private FileOperationResult Run(
        FileOperationKind kind,
        IReadOnlyList<string> sources,
        string targetDirectory,
        IProgress<FileOperationProgress>? progress,
        Func<ConflictInfo, OverwriteAction> resolveConflict,
        CancellationToken token,
        string? singleTargetName)
    {
        var result = new FileOperationResult();
        var report = new FileOperationProgress();

        List<WorkItem> work;
        try
        {
            work = BuildWorkList(sources, targetDirectory, singleTargetName, token);
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
            return result;
        }

        report.FilesTotal = work.Count(w => !w.IsDirectory);
        report.BytesTotal = work.Where(w => !w.IsDirectory).Sum(w => w.Size);
        progress?.Report(Clone(report));

        OverwriteAction? blanket = null;

        foreach (var item in work)
        {
            if (token.IsCancellationRequested)
            {
                result.Cancelled = true;
                break;
            }

            try
            {
                if (item.IsDirectory)
                {
                    if (kind != FileOperationKind.Delete)
                        Directory.CreateDirectory(item.Target);
                    continue;
                }

                report.CurrentFile = item.Source;
                report.CurrentTarget = item.Target;
                report.FilePercent = 0;
                progress?.Report(Clone(report));

                var target = item.Target;

                if (kind != FileOperationKind.Delete && File.Exists(target))
                {
                    var action = blanket ?? Ask(resolveConflict, item, target, ref blanket);

                    switch (action)
                    {
                        case OverwriteAction.Cancel:
                            result.Cancelled = true;
                            return Finish(result, report, progress);

                        case OverwriteAction.Skip:
                        case OverwriteAction.SkipAll:
                            result.Skipped++;
                            report.FilesDone++;
                            report.BytesDone += item.Size;
                            progress?.Report(Clone(report));
                            continue;

                        case OverwriteAction.OverwriteAllOlder:
                            if (File.GetLastWriteTimeUtc(item.Source) <= File.GetLastWriteTimeUtc(target))
                            {
                                result.Skipped++;
                                report.FilesDone++;
                                report.BytesDone += item.Size;
                                progress?.Report(Clone(report));
                                continue;
                            }
                            break;

                        case OverwriteAction.Rename:
                            target = MakeUniqueName(target);
                            break;
                    }
                }

                switch (kind)
                {
                    case FileOperationKind.Copy:
                        CopyFile(item.Source, target, item.Size, report, progress, token);
                        break;

                    case FileOperationKind.Move:
                        MoveFile(item.Source, target, item.Size, report, progress, token);
                        break;

                    case FileOperationKind.Delete:
                        DeleteFile(item.Source);
                        break;
                }

                result.Succeeded++;
                report.FilesDone++;
                progress?.Report(Clone(report));
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"{item.Source}: {ex.Message}");
            }
        }

        // A move leaves the source folders behind; remove them bottom-up.
        if (kind is FileOperationKind.Move or FileOperationKind.Delete && !result.Cancelled)
            RemoveEmptySourceDirectories(work, sources, kind, result);

        return Finish(result, report, progress);
    }

    private static OverwriteAction Ask(Func<ConflictInfo, OverwriteAction> resolve, WorkItem item,
        string target, ref OverwriteAction? blanket)
    {
        var source = new FileInfo(item.Source);
        var existing = new FileInfo(target);

        var action = resolve(new ConflictInfo
        {
            SourcePath = item.Source,
            TargetPath = target,
            SourceSize = source.Exists ? source.Length : 0,
            TargetSize = existing.Exists ? existing.Length : 0,
            SourceModified = source.Exists ? source.LastWriteTime : DateTime.MinValue,
            TargetModified = existing.Exists ? existing.LastWriteTime : DateTime.MinValue
        });

        if (action is OverwriteAction.OverwriteAll or OverwriteAction.SkipAll or OverwriteAction.OverwriteAllOlder)
            blanket = action;

        return action;
    }

    private static FileOperationResult Finish(FileOperationResult result, FileOperationProgress report,
        IProgress<FileOperationProgress>? progress)
    {
        progress?.Report(Clone(report));
        return result;
    }

    private static FileOperationProgress Clone(FileOperationProgress p) => new()
    {
        CurrentFile = p.CurrentFile,
        CurrentTarget = p.CurrentTarget,
        BytesDone = p.BytesDone,
        BytesTotal = p.BytesTotal,
        FilesDone = p.FilesDone,
        FilesTotal = p.FilesTotal,
        FilePercent = p.FilePercent
    };

    // -------------------------------------------------------------- work list

    private static List<WorkItem> BuildWorkList(IReadOnlyList<string> sources, string targetDirectory,
        string? singleTargetName, CancellationToken token)
    {
        var work = new List<WorkItem>();

        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();

            var name = singleTargetName is not null && sources.Count == 1
                ? singleTargetName
                : Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar));

            if (Directory.Exists(source))
            {
                var rootTarget = Path.Combine(targetDirectory, name);
                work.Add(new WorkItem(source, rootTarget, true, 0));
                AddDirectoryTree(source, rootTarget, work, token);
            }
            else if (File.Exists(source))
            {
                long size = 0;
                try { size = new FileInfo(source).Length; } catch (Exception) { /* keep 0 */ }
                work.Add(new WorkItem(source, Path.Combine(targetDirectory, name), false, size));
            }
        }

        return work;
    }

    private static void AddDirectoryTree(string sourceRoot, string targetRoot, List<WorkItem> work,
        CancellationToken token)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = 0
        };

        var stack = new Stack<(string Source, string Target)>();
        stack.Push((sourceRoot, targetRoot));

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (source, target) = stack.Pop();

            DirectoryInfo info;
            try { info = new DirectoryInfo(source); }
            catch (Exception) { continue; }

            IEnumerable<FileSystemInfo> entries;
            try { entries = info.EnumerateFileSystemInfos("*", options); }
            catch (Exception) { continue; }

            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                var childTarget = Path.Combine(target, entry.Name);

                if ((entry.Attributes & FileAttributes.Directory) != 0)
                {
                    work.Add(new WorkItem(entry.FullName, childTarget, true, 0));
                    stack.Push((entry.FullName, childTarget));
                }
                else if (entry is FileInfo file)
                {
                    long size = 0;
                    try { size = file.Length; } catch (Exception) { /* keep 0 */ }
                    work.Add(new WorkItem(file.FullName, childTarget, false, size));
                }
            }
        }
    }

    // ---------------------------------------------------------------- transfer

    private static void CopyFile(string source, string target, long size,
        FileOperationProgress report, IProgress<FileOperationProgress>? progress, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        ClearReadOnly(target);

        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            BufferSize, FileOptions.SequentialScan);
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None,
            BufferSize, FileOptions.SequentialScan);

        var buffer = new byte[BufferSize];
        long copied = 0;
        int read;
        var lastReport = Environment.TickCount64;

        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
            copied += read;
            report.BytesDone += read;

            // Throttle so a big file does not flood the dispatcher.
            var now = Environment.TickCount64;
            if (now - lastReport >= 40)
            {
                report.FilePercent = size > 0 ? copied * 100.0 / size : 100;
                progress?.Report(Clone(report));
                lastReport = now;
            }
        }

        output.Flush();
        report.FilePercent = 100;

        try
        {
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
            File.SetCreationTimeUtc(target, File.GetCreationTimeUtc(source));
            File.SetAttributes(target, File.GetAttributes(source));
        }
        catch (Exception)
        {
            // Timestamps are best effort.
        }
    }

    private static void MoveFile(string source, string target, long size,
        FileOperationProgress report, IProgress<FileOperationProgress>? progress, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // Same volume: let the filesystem rename it, which is effectively free.
        if (string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
        {
            ClearReadOnly(target);
            File.Move(source, target, overwrite: true);
            report.BytesDone += size;
            report.FilePercent = 100;
            progress?.Report(Clone(report));
            return;
        }

        CopyFile(source, target, size, report, progress, token);
        DeleteFile(source);
    }

    private static void DeleteFile(string path)
    {
        ClearReadOnly(path);
        File.Delete(path);
    }

    private static void ClearReadOnly(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
        catch (Exception)
        {
            // If we cannot clear it the copy will surface the real error.
        }
    }

    private static void RemoveEmptySourceDirectories(List<WorkItem> work, IReadOnlyList<string> sources,
        FileOperationKind kind, FileOperationResult result)
    {
        // Deepest first so children are gone before their parents.
        var directories = work.Where(w => w.IsDirectory)
            .Select(w => w.Source)
            .Concat(sources.Where(Directory.Exists))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(p => p.Length)
            .ToList();

        foreach (var directory in directories)
        {
            try
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{directory}: {ex.Message}");
            }
        }
    }

    /// <summary>Turns "report.txt" into "report (2).txt" until the name is free.</summary>
    public static string MakeUniqueName(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (int i = 2; i < 10000; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        return path;
    }

    // ----------------------------------------------------------------- delete

    /// <summary>
    /// Deletes through the shell (recycle bin, undo, standard confirmations) on a
    /// dedicated STA thread, because the shell file APIs do nothing on the MTA
    /// thread-pool threads that Task.Run provides.
    ///
    /// Falls back to a direct delete when the shell call does not take effect -
    /// but only for a permanent delete, since a silent bypass of the recycle bin
    /// would be a nasty surprise.
    /// </summary>
    public static Task<DeleteOutcome> DeleteAsync(IntPtr owner, IReadOnlyList<string> paths,
        bool permanent, bool confirm) =>
        ShellServices.RunOnStaThread(() => Delete(owner, paths, permanent, confirm));

    public static DeleteOutcome Delete(IntPtr owner, IReadOnlyList<string> paths, bool permanent, bool confirm)
    {
        if (paths.Count == 0) return new DeleteOutcome { Succeeded = true };

        if (ShellServices.DeleteToRecycleBin(owner, paths, permanent, confirm))
            return new DeleteOutcome { Succeeded = true };

        if (!permanent)
        {
            return new DeleteOutcome
            {
                Succeeded = false,
                Error = "The Recycle Bin could not accept these items. " +
                        "Use Shift+Delete to remove them permanently."
            };
        }

        var errors = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) DeleteFile(path);
            }
            catch (Exception ex)
            {
                errors.Add($"{path}: {ex.Message}");
            }
        }

        return new DeleteOutcome
        {
            Succeeded = errors.Count == 0,
            Error = errors.Count == 0 ? null : string.Join(Environment.NewLine, errors)
        };
    }
}
