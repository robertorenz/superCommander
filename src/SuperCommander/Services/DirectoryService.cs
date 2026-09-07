using System.IO;
using SuperCommander.Models;

namespace SuperCommander.Services;

public sealed class DirectoryListing
{
    public required string Path { get; init; }
    public required List<FileItem> Items { get; init; }
    public string? Error { get; init; }
    public long TotalBytes { get; init; }
    public int FileCount { get; init; }
    public int DirectoryCount { get; init; }
    public bool Success => Error is null;
}

/// <summary>Reads directories off the UI thread and applies the pane's sort order.</summary>
public static class DirectoryService
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,          // we filter hidden/system ourselves
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple
    };

    public static Task<DirectoryListing> ListAsync(string path, bool showHidden, bool showSystem,
        CancellationToken token = default) =>
        Task.Run(() => List(path, showHidden, showSystem, token), token);

    public static DirectoryListing List(string path, bool showHidden, bool showSystem,
        CancellationToken token = default)
    {
        var items = new List<FileItem>(256);
        long totalBytes = 0;
        int files = 0, dirs = 0;

        try
        {
            var root = new DirectoryInfo(path);
            if (!root.Exists)
            {
                return new DirectoryListing
                {
                    Path = path,
                    Items = items,
                    Error = $"The folder \"{path}\" does not exist."
                };
            }

            // ".." first, unless we are at a drive root.
            var parent = root.Parent;
            if (parent is not null)
                items.Add(FileItem.CreateParent(parent.FullName));

            foreach (var entry in root.EnumerateFileSystemInfos("*", Options))
            {
                token.ThrowIfCancellationRequested();

                var attributes = entry.Attributes;
                if (!showHidden && (attributes & FileAttributes.Hidden) != 0) continue;
                if (!showSystem && (attributes & FileAttributes.System) != 0) continue;

                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                long size = FileItem.SizeUnknown;

                if (!isDirectory && entry is FileInfo file)
                {
                    size = SafeLength(file);
                    totalBytes += Math.Max(0, size);
                    files++;
                }
                else
                {
                    dirs++;
                }

                items.Add(new FileItem(
                    entry.FullName,
                    entry.Name,
                    isDirectory,
                    size,
                    SafeTime(entry, last: true),
                    SafeTime(entry, last: false),
                    attributes));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return new DirectoryListing
            {
                Path = path,
                Items = items,
                Error = $"Access to \"{path}\" is denied."
            };
        }
        catch (Exception ex)
        {
            return new DirectoryListing { Path = path, Items = items, Error = ex.Message };
        }

        return new DirectoryListing
        {
            Path = path,
            Items = items,
            TotalBytes = totalBytes,
            FileCount = files,
            DirectoryCount = dirs
        };
    }

    private static long SafeLength(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static DateTime SafeTime(FileSystemInfo info, bool last)
    {
        try
        {
            return last ? info.LastWriteTime : info.CreationTime;
        }
        catch (Exception)
        {
            return DateTime.MinValue;
        }
    }

    // ------------------------------------------------------------------ sorting

    public static void Sort(List<FileItem> items, SortSpec spec, bool directoriesFirst)
    {
        var comparer = new FileItemComparer(spec, directoriesFirst);
        items.Sort(comparer);
    }

    private sealed class FileItemComparer : IComparer<FileItem>
    {
        private readonly SortSpec _spec;
        private readonly bool _directoriesFirst;

        public FileItemComparer(SortSpec spec, bool directoriesFirst)
        {
            _spec = spec;
            _directoriesFirst = directoriesFirst;
        }

        public int Compare(FileItem? x, FileItem? y)
        {
            if (x is null || y is null) return 0;

            // ".." is pinned to the top regardless of sort direction.
            if (x.IsParent) return y.IsParent ? 0 : -1;
            if (y.IsParent) return 1;

            if (_directoriesFirst && x.IsDirectory != y.IsDirectory)
                return x.IsDirectory ? -1 : 1;

            int result = _spec.Column switch
            {
                SortColumn.Extension => string.Compare(x.Extension, y.Extension, StringComparison.OrdinalIgnoreCase),
                SortColumn.Size => CompareSize(x, y),
                SortColumn.Date => DateTime.Compare(x.Modified, y.Modified),
                SortColumn.Attributes => string.Compare(x.DisplayAttributes, y.DisplayAttributes, StringComparison.Ordinal),
                _ => 0
            };

            // Name is the tie breaker for every other column.
            if (result == 0)
                result = NaturalCompare(x.Name, y.Name);

            return _spec.Descending ? -result : result;
        }

        private static int CompareSize(FileItem x, FileItem y)
        {
            // Unsized directories sort together rather than as "-1 bytes".
            long left = x.Size < 0 ? long.MinValue : x.Size;
            long right = y.Size < 0 ? long.MinValue : y.Size;
            return left.CompareTo(right);
        }
    }

    /// <summary>
    /// Compares names so that "file2" sorts before "file10", which is what people
    /// expect from a file manager.
    /// </summary>
    public static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            char ca = a[i], cb = b[j];

            if (char.IsDigit(ca) && char.IsDigit(cb))
            {
                int startA = i, startB = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;

                var spanA = a.AsSpan(startA, i - startA).TrimStart('0');
                var spanB = b.AsSpan(startB, j - startB).TrimStart('0');

                if (spanA.Length != spanB.Length)
                    return spanA.Length - spanB.Length;

                int digits = spanA.SequenceCompareTo(spanB);
                if (digits != 0) return digits;
                continue;
            }

            int cmp = char.ToUpperInvariant(ca).CompareTo(char.ToUpperInvariant(cb));
            if (cmp != 0) return cmp;
            i++;
            j++;
        }
        return (a.Length - i) - (b.Length - j);
    }

    // ------------------------------------------------------------ folder sizes

    /// <summary>Recursively sums a folder. Returns -1 when cancelled.</summary>
    public static long CalculateSize(string path, CancellationToken token)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(path);

        var recursive = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = 0
        };

        while (stack.Count > 0)
        {
            if (token.IsCancellationRequested) return -1;
            var current = stack.Pop();

            try
            {
                var info = new DirectoryInfo(current);
                foreach (var entry in info.EnumerateFileSystemInfos("*", recursive))
                {
                    if (token.IsCancellationRequested) return -1;

                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                        stack.Push(entry.FullName);
                    else if (entry is FileInfo file)
                        total += SafeLength(file);
                }
            }
            catch (Exception)
            {
                // Skip folders we cannot read and keep counting the rest.
            }
        }

        return total;
    }

    // ------------------------------------------------------------- branch view

    /// <summary>
    /// Flattens every file below <paramref name="root"/> into a single list -
    /// Total Commander's Ctrl+B "branch view".
    /// </summary>
    public static List<FileItem> ListBranch(string root, bool showHidden, bool showSystem,
        CancellationToken token)
    {
        var items = new List<FileItem>(1024);
        var parent = Directory.GetParent(root);
        if (parent is not null) items.Add(FileItem.CreateParent(parent.FullName));

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = 0,
            MaxRecursionDepth = 64
        };

        try
        {
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
            {
                token.ThrowIfCancellationRequested();

                var attributes = file.Attributes;
                if (!showHidden && (attributes & FileAttributes.Hidden) != 0) continue;
                if (!showSystem && (attributes & FileAttributes.System) != 0) continue;

                // Show the path relative to the branch root so the list stays readable.
                var relative = Path.GetRelativePath(root, file.FullName);
                items.Add(new FileItem(file.FullName, relative, false, SafeLength(file),
                    SafeTime(file, true), SafeTime(file, false), attributes));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Partial results are still useful.
        }

        return items;
    }
}
