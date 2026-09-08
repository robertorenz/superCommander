using System.IO;

namespace SuperCommander.Services.Archives;

/// <summary>Shared helpers for every provider.</summary>
public static class ArchivePath
{
    /// <summary>Null means "everything"; otherwise the set of selected entry paths.</summary>
    public static HashSet<string>? BuildFilter(IReadOnlyList<string>? entries) =>
        entries is { Count: > 0 }
            ? new HashSet<string>(entries.Select(e => e.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase)
            : null;

    /// <summary>A selected folder pulls in everything beneath it.</summary>
    public static bool Matches(HashSet<string>? wanted, string entryPath)
    {
        if (wanted is null) return true;
        if (wanted.Contains(entryPath)) return true;

        foreach (var w in wanted)
            if (entryPath.StartsWith(w.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>
    /// Joins an entry name onto the target directory, refusing paths that would
    /// escape it (zip slip).
    /// </summary>
    public static string SafeCombine(string targetDirectory, string entryName)
    {
        var cleaned = entryName.Replace('/', Path.DirectorySeparatorChar);

        // Strip anything that could walk upwards or re-root the path.
        cleaned = cleaned.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (cleaned.Length >= 2 && cleaned[1] == ':') cleaned = cleaned[2..].TrimStart(Path.DirectorySeparatorChar);

        var combined = Path.GetFullPath(Path.Combine(targetDirectory, cleaned));
        var root = Path.GetFullPath(targetDirectory);

        if (!combined.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(combined, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Entry \"{entryName}\" would extract outside the target folder.");
        }

        return combined;
    }

    /// <summary>Flattens the sources into (file on disk, entry name) pairs.</summary>
    public static List<(string Source, string Entry)> Enumerate(IReadOnlyList<string> sources,
        string baseDirectory, CancellationToken token)
    {
        var files = new List<(string, string)>();

        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();

            if (Directory.Exists(source))
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = 0
                };

                foreach (var file in Directory.EnumerateFiles(source, "*", options))
                    files.Add((file, Path.GetRelativePath(baseDirectory, file).Replace('\\', '/')));
            }
            else if (File.Exists(source))
            {
                files.Add((source, Path.GetRelativePath(baseDirectory, source).Replace('\\', '/')));
            }
        }

        return files;
    }
}
