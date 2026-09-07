using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SuperCommander.Models;

namespace SuperCommander.Services;

public sealed class SearchCriteria
{
    public string Root { get; set; } = string.Empty;

    /// <summary>One or more wildcard masks separated by ';' - e.g. "*.cs;*.xaml".</summary>
    public string FileMask { get; set; } = "*";

    public string ContainingText { get; set; } = string.Empty;
    public bool CaseSensitive { get; set; }
    public bool UseRegex { get; set; }
    public bool WholeWords { get; set; }
    public bool SearchSubdirectories { get; set; } = true;
    public bool SearchArchives { get; set; }
    public bool IncludeHidden { get; set; }

    public DateTime? ModifiedAfter { get; set; }
    public DateTime? ModifiedBefore { get; set; }
    public long? MinSize { get; set; }
    public long? MaxSize { get; set; }
}

public sealed class SearchProgress
{
    public string CurrentDirectory { get; set; } = string.Empty;
    public int Scanned { get; set; }
    public int Found { get; set; }
}

/// <summary>Alt+F7 file finder: name masks, attribute filters and content grep.</summary>
public static class SearchService
{
    public static Task<List<FileItem>> SearchAsync(SearchCriteria criteria,
        IProgress<SearchProgress>? progress, Action<FileItem>? onFound, CancellationToken token) =>
        Task.Run(() => Search(criteria, progress, onFound, token), token);

    private static List<FileItem> Search(SearchCriteria criteria, IProgress<SearchProgress>? progress,
        Action<FileItem>? onFound, CancellationToken token)
    {
        var results = new List<FileItem>();
        var report = new SearchProgress();

        var masks = BuildMaskRegexes(criteria.FileMask);
        Regex? content = BuildContentRegex(criteria);
        byte[]? literal = null;
        if (content is null && criteria.ContainingText.Length > 0)
            literal = Encoding.UTF8.GetBytes(criteria.ContainingText);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = 0
        };

        var stack = new Stack<string>();
        stack.Push(criteria.Root);
        long lastReport = 0;

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var directory = stack.Pop();

            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options); }
            catch (Exception) { continue; }

            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();

                var attributes = entry.Attributes;
                bool hidden = (attributes & FileAttributes.Hidden) != 0;
                if (hidden && !criteria.IncludeHidden) continue;

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (criteria.SearchSubdirectories && (attributes & FileAttributes.ReparsePoint) == 0)
                        stack.Push(entry.FullName);
                    continue;
                }

                if (entry is not FileInfo file) continue;

                report.Scanned++;
                var now = Environment.TickCount64;
                if (now - lastReport >= 100)
                {
                    report.CurrentDirectory = directory;
                    progress?.Report(new SearchProgress
                    {
                        CurrentDirectory = directory,
                        Scanned = report.Scanned,
                        Found = report.Found
                    });
                    lastReport = now;
                }

                if (!MatchesMask(masks, file.Name)) continue;
                if (!MatchesMetadata(criteria, file)) continue;
                if ((content is not null || literal is not null) && !FileContains(file, content, literal, criteria))
                    continue;

                var item = new FileItem(file.FullName, file.FullName, false, SafeLength(file),
                    file.LastWriteTime, file.CreationTime, attributes);

                results.Add(item);
                report.Found++;
                onFound?.Invoke(item);
            }
        }

        progress?.Report(new SearchProgress
        {
            CurrentDirectory = string.Empty,
            Scanned = report.Scanned,
            Found = report.Found
        });

        return results;
    }

    private static long SafeLength(FileInfo file)
    {
        try { return file.Length; } catch (Exception) { return 0; }
    }

    // ------------------------------------------------------------ name masks

    private static List<Regex> BuildMaskRegexes(string maskList)
    {
        var regexes = new List<Regex>();
        var masks = maskList.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (masks.Length == 0) masks = new[] { "*" };

        foreach (var mask in masks)
        {
            if (mask is "*" or "*.*") return new List<Regex>(); // empty means "everything"
            regexes.Add(new Regex(WildcardToRegex(mask), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        }
        return regexes;
    }

    public static string WildcardToRegex(string mask)
    {
        var builder = new StringBuilder("^");
        foreach (var c in mask)
        {
            builder.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(c.ToString())
            });
        }
        builder.Append('$');
        return builder.ToString();
    }

    private static bool MatchesMask(List<Regex> masks, string name)
    {
        if (masks.Count == 0) return true;
        foreach (var mask in masks)
            if (mask.IsMatch(name)) return true;
        return false;
    }

    private static bool MatchesMetadata(SearchCriteria criteria, FileInfo file)
    {
        if (criteria.ModifiedAfter is { } after && file.LastWriteTime < after) return false;
        if (criteria.ModifiedBefore is { } before && file.LastWriteTime > before) return false;

        long length = SafeLength(file);
        if (criteria.MinSize is { } min && length < min) return false;
        if (criteria.MaxSize is { } max && length > max) return false;

        return true;
    }

    // --------------------------------------------------------- content search

    private static Regex? BuildContentRegex(SearchCriteria criteria)
    {
        if (criteria.ContainingText.Length == 0) return null;
        if (!criteria.UseRegex && !criteria.WholeWords) return null;

        var options = RegexOptions.CultureInvariant;
        if (!criteria.CaseSensitive) options |= RegexOptions.IgnoreCase;

        var pattern = criteria.UseRegex ? criteria.ContainingText : Regex.Escape(criteria.ContainingText);
        if (criteria.WholeWords) pattern = $"\\b{pattern}\\b";

        try { return new Regex(pattern, options); }
        catch (ArgumentException) { return null; }
    }

    private const long MaxContentSearchBytes = 256L * 1024 * 1024;

    private static bool FileContains(FileInfo file, Regex? regex, byte[]? literal, SearchCriteria criteria)
    {
        try
        {
            if (file.Length == 0 || file.Length > MaxContentSearchBytes) return false;

            // Regex needs decoded text; a plain substring can stay on bytes, which
            // is dramatically faster over a large tree.
            if (regex is not null)
            {
                using var reader = new StreamReader(file.FullName, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                    if (regex.IsMatch(line)) return true;
                return false;
            }

            if (literal is null) return false;

            var comparison = criteria.CaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            using var textReader = new StreamReader(file.FullName, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string? text;
            while ((text = textReader.ReadLine()) is not null)
                if (text.Contains(criteria.ContainingText, comparison)) return true;

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
