using System.Globalization;

namespace SuperCommander.Services.Ftp;

public sealed class FtpEntry
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsSymlink { get; init; }
    public long Size { get; init; } = -1;
    public DateTime Modified { get; init; } = DateTime.MinValue;
    public string Permissions { get; init; } = string.Empty;
}

/// <summary>
/// Turns directory listings into entries. Prefers MLSD, which is machine
/// readable; falls back to the Unix and DOS shapes of LIST that servers in the
/// wild actually emit.
/// </summary>
public static class FtpListParser
{
    // ------------------------------------------------------------------ MLSD

    /// <summary>Parses RFC 3659 MLSD lines: "fact=value;fact=value; name".</summary>
    public static List<FtpEntry> ParseMlsd(IEnumerable<string> lines, string directory)
    {
        var entries = new List<FtpEntry>();

        foreach (var line in lines)
        {
            int space = line.IndexOf(' ');
            if (space < 0) continue;

            var name = line[(space + 1)..].Trim();
            if (name is "." or ".." || name.Length == 0) continue;

            string type = string.Empty, permissions = string.Empty;
            long size = -1;
            var modified = DateTime.MinValue;

            foreach (var fact in line[..space].Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = fact.IndexOf('=');
                if (equals < 0) continue;

                var key = fact[..equals].Trim();
                var value = fact[(equals + 1)..].Trim();

                if (key.Equals("type", StringComparison.OrdinalIgnoreCase)) type = value;
                else if (key.Equals("size", StringComparison.OrdinalIgnoreCase)) long.TryParse(value, out size);
                else if (key.Equals("perm", StringComparison.OrdinalIgnoreCase)) permissions = value;
                else if (key.Equals("modify", StringComparison.OrdinalIgnoreCase)) modified = ParseMlsdTime(value);
            }

            // cdir/pdir are the listed directory and its parent - not children.
            if (type.Equals("cdir", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("pdir", StringComparison.OrdinalIgnoreCase)) continue;

            bool isDirectory = type.StartsWith("dir", StringComparison.OrdinalIgnoreCase);

            entries.Add(new FtpEntry
            {
                Name = name,
                FullPath = FtpPath.Combine(directory, name),
                IsDirectory = isDirectory,
                Size = isDirectory ? -1 : size,
                Modified = modified,
                Permissions = permissions
            });
        }

        return entries;
    }

    private static DateTime ParseMlsdTime(string value)
    {
        // YYYYMMDDHHMMSS, optionally with fractional seconds.
        var trimmed = value.Length > 14 ? value[..14] : value;

        return DateTime.TryParseExact(trimmed, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToLocalTime()
            : DateTime.MinValue;
    }

    // ------------------------------------------------------------------ LIST

    public static List<FtpEntry> ParseList(IEnumerable<string> lines, string directory)
    {
        var entries = new List<FtpEntry>();

        foreach (var line in lines)
        {
            var entry = ParseUnix(line, directory) ?? ParseDos(line, directory);
            if (entry is not null && entry.Name is not ("." or "..")) entries.Add(entry);
        }

        return entries;
    }

    /// <summary>"-rw-r--r--   1 owner group     1234 Jan 02 12:00 name"</summary>
    private static FtpEntry? ParseUnix(string line, string directory)
    {
        if (line.Length < 10) return null;

        char first = line[0];
        if (first is not ('-' or 'd' or 'l' or 'b' or 'c' or 'p' or 's')) return null;

        // permissions, links, owner, group, size, month, day, time-or-year, name
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 8) return null;

        var permissions = parts[0];
        bool isDirectory = first == 'd';
        bool isSymlink = first == 'l';

        // The size column is the last purely numeric field before the date.
        int dateIndex = -1;
        for (int i = 2; i < parts.Length - 3; i++)
        {
            if (IsMonth(parts[i + 1]) && long.TryParse(parts[i], out _))
            {
                dateIndex = i + 1;
                break;
            }
        }
        if (dateIndex < 0 || dateIndex + 2 >= parts.Length) return null;

        long size = long.TryParse(parts[dateIndex - 1], out long parsedSize) ? parsedSize : -1;
        var modified = ParseUnixDate(parts[dateIndex], parts[dateIndex + 1], parts[dateIndex + 2]);

        // Re-join from the original line so names containing spaces survive.
        int nameStart = IndexOfField(line, parts, dateIndex + 3);
        if (nameStart < 0) return null;

        var name = line[nameStart..].Trim();
        if (isSymlink)
        {
            int arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow > 0) name = name[..arrow];
        }
        if (name.Length == 0) return null;

        return new FtpEntry
        {
            Name = name,
            FullPath = FtpPath.Combine(directory, name),
            IsDirectory = isDirectory,
            IsSymlink = isSymlink,
            Size = isDirectory ? -1 : size,
            Modified = modified,
            Permissions = permissions
        };
    }

    /// <summary>"01-02-24  12:00PM       &lt;DIR&gt;          name"</summary>
    private static FtpEntry? ParseDos(string line, string directory)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return null;

        if (!DateTime.TryParse($"{parts[0]} {parts[1]}", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var modified))
            return null;

        bool isDirectory = parts[2].Equals("<DIR>", StringComparison.OrdinalIgnoreCase);
        long size = isDirectory ? -1 : (long.TryParse(parts[2], out long parsed) ? parsed : -1);

        int nameStart = IndexOfField(line, parts, 3);
        if (nameStart < 0) return null;

        var name = line[nameStart..].Trim();
        if (name.Length == 0) return null;

        return new FtpEntry
        {
            Name = name,
            FullPath = FtpPath.Combine(directory, name),
            IsDirectory = isDirectory,
            Size = size,
            Modified = modified
        };
    }

    /// <summary>Character offset of the nth whitespace-separated field in the raw line.</summary>
    private static int IndexOfField(string line, string[] parts, int fieldIndex)
    {
        if (fieldIndex >= parts.Length) return -1;

        int position = 0;
        for (int i = 0; i < fieldIndex; i++)
        {
            position = line.IndexOf(parts[i], position, StringComparison.Ordinal);
            if (position < 0) return -1;
            position += parts[i].Length;
        }

        while (position < line.Length && line[position] == ' ') position++;
        return position < line.Length ? position : -1;
    }

    private static readonly string[] Months =
        { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

    private static bool IsMonth(string value) =>
        Array.Exists(Months, m => m.Equals(value, StringComparison.OrdinalIgnoreCase));

    private static DateTime ParseUnixDate(string month, string day, string timeOrYear)
    {
        int monthIndex = Array.FindIndex(Months, m => m.Equals(month, StringComparison.OrdinalIgnoreCase));
        if (monthIndex < 0 || !int.TryParse(day, out int dayNumber)) return DateTime.MinValue;

        try
        {
            // Within the last six months the listing carries a time and no year;
            // older entries carry the year instead.
            if (timeOrYear.Contains(':'))
            {
                var parts = timeOrYear.Split(':');
                int hour = int.Parse(parts[0], CultureInfo.InvariantCulture);
                int minute = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;

                var now = DateTime.Now;
                var candidate = new DateTime(now.Year, monthIndex + 1, dayNumber, hour, minute, 0);

                // A date more than a day ahead must belong to last year.
                if (candidate > now.AddDays(1)) candidate = candidate.AddYears(-1);
                return candidate;
            }

            int year = int.Parse(timeOrYear, CultureInfo.InvariantCulture);
            return new DateTime(year, monthIndex + 1, dayNumber);
        }
        catch (Exception)
        {
            return DateTime.MinValue;
        }
    }
}
