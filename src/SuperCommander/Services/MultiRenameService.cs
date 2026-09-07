using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SuperCommander.Models;

namespace SuperCommander.Services;

public enum NameCase
{
    Unchanged,
    Lower,
    Upper,
    TitleCase,
    FirstUpper
}

public sealed class RenameRule
{
    /// <summary>Name pattern - supports [N], [N1-3], [E], [C], [P], [Y][M][D][h][m][s].</summary>
    public string NamePattern { get; set; } = "[N]";

    /// <summary>Extension pattern, usually just [E].</summary>
    public string ExtensionPattern { get; set; } = "[E]";

    public string SearchFor { get; set; } = string.Empty;
    public string ReplaceWith { get; set; } = string.Empty;
    public bool UseRegex { get; set; }
    public bool CaseSensitive { get; set; }

    public NameCase Case { get; set; } = NameCase.Unchanged;

    public int CounterStart { get; set; } = 1;
    public int CounterStep { get; set; } = 1;
    public int CounterDigits { get; set; } = 1;
}

public sealed class RenamePreview
{
    public required FileItem Item { get; init; }
    public required string OldName { get; init; }
    public required string NewName { get; init; }
    public string? Error { get; set; }
    public bool Changed => !string.Equals(OldName, NewName, StringComparison.Ordinal);
}

/// <summary>Ctrl+M multi-rename tool.</summary>
public static class MultiRenameService
{
    private static readonly Regex Placeholder = new(@"\[(?<key>[A-Za-z])(?<args>[0-9\-]*)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static List<RenamePreview> BuildPreview(IReadOnlyList<FileItem> items, RenameRule rule)
    {
        var previews = new List<RenamePreview>(items.Count);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int counter = rule.CounterStart;

        foreach (var item in items)
        {
            var name = Expand(rule.NamePattern, item, counter, rule);
            var extension = Expand(rule.ExtensionPattern, item, counter, rule);

            var combined = extension.Length == 0 ? name : $"{name}.{extension}";
            combined = ApplyReplace(combined, rule);
            combined = ApplyCase(combined, rule.Case);
            combined = Sanitise(combined);

            var preview = new RenamePreview
            {
                Item = item,
                OldName = item.Name,
                NewName = combined
            };

            if (combined.Length == 0)
                preview.Error = "Empty name";
            else if (!taken.Add(combined))
                preview.Error = "Duplicate name";

            previews.Add(preview);
            counter += rule.CounterStep;
        }

        return previews;
    }

    private static string Expand(string pattern, FileItem item, int counter, RenameRule rule)
    {
        var timestamp = item.Modified == DateTime.MinValue ? DateTime.Now : item.Modified;

        return Placeholder.Replace(pattern, match =>
        {
            var key = match.Groups["key"].Value;
            var args = match.Groups["args"].Value;

            return key switch
            {
                "N" => Slice(item.BaseName, args),
                "E" => Slice(item.Extension, args),
                "P" => Path.GetFileName(Path.GetDirectoryName(item.FullPath) ?? string.Empty),
                "C" => counter.ToString(CultureInfo.InvariantCulture)
                        .PadLeft(Math.Max(1, rule.CounterDigits), '0'),
                "Y" => timestamp.ToString("yyyy", CultureInfo.InvariantCulture),
                "M" => timestamp.ToString("MM", CultureInfo.InvariantCulture),
                "D" => timestamp.ToString("dd", CultureInfo.InvariantCulture),
                "h" => timestamp.ToString("HH", CultureInfo.InvariantCulture),
                "m" => timestamp.ToString("mm", CultureInfo.InvariantCulture),
                "s" => timestamp.ToString("ss", CultureInfo.InvariantCulture),
                _ => match.Value
            };
        });
    }

    /// <summary>Handles [N2] (from char 2) and [N2-5] (chars 2 through 5), 1-based.</summary>
    private static string Slice(string value, string args)
    {
        if (args.Length == 0 || value.Length == 0) return value;

        var parts = args.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return value;

        if (!int.TryParse(parts[0], out int start)) return value;
        start = Math.Max(1, start);
        if (start > value.Length) return string.Empty;

        int end = value.Length;
        if (parts.Length > 1 && int.TryParse(parts[1], out int parsedEnd))
            end = Math.Min(value.Length, Math.Max(start, parsedEnd));

        return value[(start - 1)..end];
    }

    private static string ApplyReplace(string value, RenameRule rule)
    {
        if (rule.SearchFor.Length == 0) return value;

        try
        {
            if (rule.UseRegex)
            {
                var options = RegexOptions.CultureInvariant;
                if (!rule.CaseSensitive) options |= RegexOptions.IgnoreCase;
                return Regex.Replace(value, rule.SearchFor, rule.ReplaceWith, options);
            }

            return value.Replace(rule.SearchFor, rule.ReplaceWith,
                rule.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return value;
        }
    }

    private static string ApplyCase(string value, NameCase mode)
    {
        if (value.Length == 0) return value;

        return mode switch
        {
            NameCase.Lower => value.ToLower(CultureInfo.CurrentCulture),
            NameCase.Upper => value.ToUpper(CultureInfo.CurrentCulture),
            NameCase.TitleCase => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(value.ToLower(CultureInfo.CurrentCulture)),
            NameCase.FirstUpper => char.ToUpper(value[0], CultureInfo.CurrentCulture) + value[1..].ToLower(CultureInfo.CurrentCulture),
            _ => value
        };
    }

    private static string Sanitise(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return builder.ToString().Trim();
    }

    /// <summary>Applies the previews. Returns how many renames succeeded.</summary>
    public static (int Renamed, List<string> Errors) Apply(IReadOnlyList<RenamePreview> previews)
    {
        int renamed = 0;
        var errors = new List<string>();

        foreach (var preview in previews)
        {
            if (!preview.Changed || preview.Error is not null) continue;

            try
            {
                var directory = Path.GetDirectoryName(preview.Item.FullPath) ?? string.Empty;
                var target = Path.Combine(directory, preview.NewName);

                if (preview.Item.IsDirectory) Directory.Move(preview.Item.FullPath, target);
                else File.Move(preview.Item.FullPath, target);

                renamed++;
            }
            catch (Exception ex)
            {
                errors.Add($"{preview.OldName}: {ex.Message}");
            }
        }

        return (renamed, errors);
    }
}
