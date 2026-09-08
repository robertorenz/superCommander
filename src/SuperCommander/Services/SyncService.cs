using System.IO;
using System.Text.RegularExpressions;
using SuperCommander.Models;

namespace SuperCommander.Services;

/// <summary>What to compare, and how strict to be about it.</summary>
public sealed class SyncOptions
{
    public required string Left { get; init; }
    public required string Right { get; init; }

    /// <summary>Semicolon separated, the same syntax the finder takes. "*" means everything.</summary>
    public string FileMask { get; init; } = "*";

    public bool Subdirectories { get; init; } = true;
    public bool IncludeHidden { get; init; }

    /// <summary>Read both files through when the size matches, instead of trusting the timestamp.</summary>
    public bool CompareContent { get; init; }

    /// <summary>Compare on size alone, so a file only touched by a copy tool is not "newer".</summary>
    public bool IgnoreDate { get; init; }

    /// <summary>
    /// FAT stores timestamps to two seconds and DST can shift them by an hour, so
    /// anything inside the tolerance counts as the same moment.
    /// </summary>
    public int ToleranceSeconds { get; init; } = 2;
}

public sealed class SyncProgress
{
    public string CurrentFolder { get; init; } = string.Empty;
    public int Compared { get; init; }
    public int Differences { get; init; }
}

/// <summary>
/// Compares two folder trees and applies the decisions the user made about them.
/// The comparison is pure: it reads, classifies and returns, and nothing is written
/// until <see cref="SynchronizeAsync"/> is called with the entries.
/// </summary>
public static class SyncService
{
    private const int CompareBufferSize = 256 * 1024;

    public static Task<List<SyncEntry>> CompareAsync(SyncOptions options,
        IProgress<SyncProgress>? progress, CancellationToken token) =>
        Task.Run(() => Compare(options, progress, token), token);

    public static List<SyncEntry> Compare(SyncOptions options,
        IProgress<SyncProgress>? progress = null, CancellationToken token = default)
    {
        var masks = SearchService.BuildMaskRegexes(options.FileMask);

        var left = Collect(options.Left, options, masks, progress, token);
        var right = Collect(options.Right, options, masks, progress, token);

        var names = new SortedSet<string>(left.Keys, StringComparer.OrdinalIgnoreCase);
        names.UnionWith(right.Keys);

        var results = new List<SyncEntry>(names.Count);
        int differences = 0;
        long lastReport = 0;

        foreach (var relative in names)
        {
            token.ThrowIfCancellationRequested();

            bool onLeft = left.TryGetValue(relative, out var l);
            bool onRight = right.TryGetValue(relative, out var r);

            var state = Classify(options, onLeft ? l : null, onRight ? r : null, token);
            var action = Suggest(state);

            results.Add(new SyncEntry
            {
                RelativePath = relative,
                LeftPath = onLeft ? l!.FullName : Path.Combine(options.Left, relative),
                RightPath = onRight ? r!.FullName : Path.Combine(options.Right, relative),
                OnLeft = onLeft,
                OnRight = onRight,
                LeftSize = onLeft ? SafeLength(l!) : 0,
                RightSize = onRight ? SafeLength(r!) : 0,
                LeftModified = onLeft ? l!.LastWriteTime : DateTime.MinValue,
                RightModified = onRight ? r!.LastWriteTime : DateTime.MinValue,
                State = state,
                DefaultAction = action,
                Action = action
            });

            if (state != SyncState.Same) differences++;

            var now = Environment.TickCount64;
            if (now - lastReport >= 100)
            {
                progress?.Report(new SyncProgress
                {
                    CurrentFolder = Path.GetDirectoryName(relative) ?? string.Empty,
                    Compared = results.Count,
                    Differences = differences
                });
                lastReport = now;
            }
        }

        progress?.Report(new SyncProgress { Compared = results.Count, Differences = differences });
        return results;
    }

    /// <summary>The direction a fresh comparison suggests. A tie is left alone.</summary>
    private static SyncAction Suggest(SyncState state) => state switch
    {
        SyncState.LeftOnly or SyncState.LeftNewer => SyncAction.ToRight,
        SyncState.RightOnly or SyncState.RightNewer => SyncAction.ToLeft,
        _ => SyncAction.None
    };

    // -------------------------------------------------------------- collection

    private static Dictionary<string, FileInfo> Collect(string root, SyncOptions options,
        List<Regex> masks, IProgress<SyncProgress>? progress, CancellationToken token)
    {
        var found = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return found;

        var enumeration = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = 0
        };

        var stack = new Stack<(string Path, string Relative)>();
        stack.Push((root, string.Empty));
        long lastReport = 0;

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (directory, relative) = stack.Pop();

            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(directory).EnumerateFileSystemInfos("*", enumeration); }
            catch (Exception) { continue; }

            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();

                FileAttributes attributes;
                try { attributes = entry.Attributes; }
                catch (Exception) { continue; }

                if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 && !options.IncludeHidden)
                    continue;

                var childRelative = relative.Length == 0 ? entry.Name : Path.Combine(relative, entry.Name);

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    // Junctions and symlinks would walk into themselves.
                    if (options.Subdirectories && (attributes & FileAttributes.ReparsePoint) == 0)
                        stack.Push((entry.FullName, childRelative));
                    continue;
                }

                if (entry is not FileInfo file) continue;
                if (!SearchService.MatchesMask(masks, file.Name)) continue;

                found[childRelative] = file;
            }

            var now = Environment.TickCount64;
            if (now - lastReport >= 100)
            {
                progress?.Report(new SyncProgress { CurrentFolder = directory, Compared = found.Count });
                lastReport = now;
            }
        }

        return found;
    }

    // ------------------------------------------------------------ comparison

    private static SyncState Classify(SyncOptions options, FileInfo? left, FileInfo? right,
        CancellationToken token)
    {
        if (left is null) return SyncState.RightOnly;
        if (right is null) return SyncState.LeftOnly;

        long leftSize = SafeLength(left);
        long rightSize = SafeLength(right);

        // Size alone settles it when the timestamps are not to be trusted.
        if (options.IgnoreDate)
        {
            if (leftSize != rightSize) return SyncState.Different;
            return options.CompareContent && !ContentsMatch(left, right, token)
                ? SyncState.Different
                : SyncState.Same;
        }

        var difference = left.LastWriteTimeUtc - right.LastWriteTimeUtc;
        bool sameMoment = Math.Abs(difference.TotalSeconds) <= options.ToleranceSeconds;

        if (sameMoment)
        {
            if (leftSize != rightSize) return SyncState.Different;
            return options.CompareContent && !ContentsMatch(left, right, token)
                ? SyncState.Different
                : SyncState.Same;
        }

        // Same bytes with different stamps is still the same file, if we looked.
        if (options.CompareContent && leftSize == rightSize && ContentsMatch(left, right, token))
            return SyncState.Same;

        return difference > TimeSpan.Zero ? SyncState.LeftNewer : SyncState.RightNewer;
    }

    private static bool ContentsMatch(FileInfo left, FileInfo right, CancellationToken token)
    {
        try
        {
            using var a = new FileStream(left.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                CompareBufferSize, FileOptions.SequentialScan);
            using var b = new FileStream(right.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                CompareBufferSize, FileOptions.SequentialScan);

            if (a.Length != b.Length) return false;

            var bufferA = new byte[CompareBufferSize];
            var bufferB = new byte[CompareBufferSize];

            while (true)
            {
                token.ThrowIfCancellationRequested();

                int readA = ReadBlock(a, bufferA);
                int readB = ReadBlock(b, bufferB);

                if (readA != readB) return false;
                if (readA == 0) return true;
                if (!bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB))) return false;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // An unreadable file is not provably identical.
            return false;
        }
    }

    private static int ReadBlock(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static long SafeLength(FileInfo file)
    {
        try { return file.Length; } catch (Exception) { return 0; }
    }

    // --------------------------------------------------------------- applying

    public static Task<FileOperationResult> SynchronizeAsync(IReadOnlyList<SyncEntry> entries,
        IProgress<FileOperationProgress>? progress, CancellationToken token) =>
        Task.Run(() => Synchronize(entries, progress, token), token);

    /// <summary>
    /// Carries out every entry whose action is not <see cref="SyncAction.None"/>.
    /// Copies go through the same engine as F5, so timestamps and attributes survive
    /// and the progress contract is identical.
    /// </summary>
    public static FileOperationResult Synchronize(IReadOnlyList<SyncEntry> entries,
        IProgress<FileOperationProgress>? progress = null, CancellationToken token = default)
    {
        var work = entries.Where(e => e.Action != SyncAction.None).ToList();
        var result = new FileOperationResult();

        var report = new FileOperationProgress
        {
            FilesTotal = work.Count,
            BytesTotal = work.Sum(e => e.TransferSize)
        };
        progress?.Report(Snapshot(report));

        foreach (var entry in work)
        {
            if (token.IsCancellationRequested)
            {
                result.Cancelled = true;
                break;
            }

            report.CurrentFile = entry.SourcePath;
            report.CurrentTarget = entry.TargetPath;
            report.FilePercent = 0;
            progress?.Report(Snapshot(report));

            try
            {
                if (entry.Action is SyncAction.DeleteLeft or SyncAction.DeleteRight)
                    FileOperationService.DeleteOneFile(entry.SourcePath);
                else
                    FileOperationService.CopyOneFile(entry.SourcePath, entry.TargetPath,
                        entry.TransferSize, report, progress, token);

                result.Succeeded++;
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"{entry.RelativePath}: {ex.Message}");
            }

            report.FilesDone++;
            progress?.Report(Snapshot(report));
        }

        progress?.Report(Snapshot(report));
        return result;
    }

    private static FileOperationProgress Snapshot(FileOperationProgress p) => new()
    {
        CurrentFile = p.CurrentFile,
        CurrentTarget = p.CurrentTarget,
        BytesDone = p.BytesDone,
        BytesTotal = p.BytesTotal,
        FilesDone = p.FilesDone,
        FilesTotal = p.FilesTotal,
        FilePercent = p.FilePercent
    };
}
