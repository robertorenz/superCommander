using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SuperCommander.Models;

namespace SuperCommander.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/>. A corrupt or unreadable file is
/// never fatal - we fall back to defaults so the app always starts.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    public AppSettings Current { get; private set; } = new();

    public string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SuperCommander");

    public string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                Current = CreateDefaults();
                return;
            }

            var json = File.ReadAllText(FilePath);
            Current = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? CreateDefaults();

            if (Current.LeftPane.TabPaths.Count == 0 || Current.RightPane.TabPaths.Count == 0)
            {
                var defaults = CreateDefaults();
                if (Current.LeftPane.TabPaths.Count == 0) Current.LeftPane = defaults.LeftPane;
                if (Current.RightPane.TabPaths.Count == 0) Current.RightPane = defaults.RightPane;
            }
        }
        catch (Exception)
        {
            Current = CreateDefaults();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);

            // Write to a temp file first so a crash mid-write cannot corrupt the
            // settings that already work.
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Settings are a convenience, never a hard requirement - but a silent
            // failure here once hid a serialisation bug for an entire session.
            LastSaveError = ex;
            TryLog(ex);
        }
    }

    /// <summary>Set when the most recent <see cref="Save"/> could not complete.</summary>
    public Exception? LastSaveError { get; private set; }

    private void TryLog(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.AppendAllText(Path.Combine(DirectoryPath, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] settings save failed: {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Nothing further we can do.
        }
    }

    private static AppSettings CreateDefaults()
    {
        var settings = new AppSettings();

        var start = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(start) || !Directory.Exists(start))
            start = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

        settings.LeftPane.TabPaths.Add(start);
        settings.RightPane.TabPaths.Add(FindSecondRoot(start));

        settings.Bookmarks.Add(new Bookmark { Name = "Desktop", Path = Environment.GetFolderPath(Environment.SpecialFolder.Desktop) });
        settings.Bookmarks.Add(new Bookmark { Name = "Documents", Path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) });
        settings.Bookmarks.Add(new Bookmark { Name = "Downloads", Path = Path.Combine(start, "Downloads") });

        return settings;
    }

    /// <summary>Prefers a second physical drive for the right pane, else the profile.</summary>
    private static string FindSecondRoot(string fallback)
    {
        try
        {
            var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                if (string.Equals(drive.RootDirectory.FullName, systemRoot, StringComparison.OrdinalIgnoreCase)) continue;
                return drive.RootDirectory.FullName;
            }
        }
        catch (Exception)
        {
            // fall through
        }
        return fallback;
    }
}
