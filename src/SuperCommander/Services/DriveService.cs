using System.IO;
using System.Windows.Media;
using SuperCommander.Interop;

namespace SuperCommander.Services;

public sealed class DriveEntry
{
    public required string Root { get; init; }
    public required string Letter { get; init; }
    public string Label { get; init; } = string.Empty;
    public DriveType Type { get; init; }
    public bool IsReady { get; init; }
    public ulong FreeBytes { get; init; }
    public ulong TotalBytes { get; init; }
    public ImageSource? Icon { get; set; }

    public string Caption => string.IsNullOrEmpty(Label) ? Letter : $"{Letter}  {Label}";

    public string Tooltip
    {
        get
        {
            if (!IsReady) return $"{Root} (not ready)";
            var free = FileItemFormat(FreeBytes);
            var total = FileItemFormat(TotalBytes);
            return string.IsNullOrEmpty(Label)
                ? $"{Root}\n{free} free of {total}"
                : $"{Label} ({Root})\n{free} free of {total}";
        }
    }

    private static string FileItemFormat(ulong bytes) =>
        Models.FileItem.FormatBytesShort((long)Math.Min(bytes, long.MaxValue));
}

public static class DriveService
{
    public static List<DriveEntry> GetDrives()
    {
        var drives = new List<DriveEntry>();

        DriveInfo[] all;
        try { all = DriveInfo.GetDrives(); }
        catch (Exception) { return drives; }

        foreach (var drive in all)
        {
            string root = drive.RootDirectory.FullName;
            string letter = root.Length >= 1 ? root[..1].ToLowerInvariant() : "?";
            bool ready = false;
            string label = string.Empty;
            ulong free = 0, total = 0;

            try
            {
                ready = drive.IsReady;
                if (ready)
                {
                    label = drive.VolumeLabel;
                    (free, total) = ShellServices.GetDriveSpace(root);
                }
            }
            catch (Exception)
            {
                ready = false;
            }

            drives.Add(new DriveEntry
            {
                Root = root,
                Letter = letter,
                Label = label,
                Type = drive.DriveType,
                IsReady = ready,
                FreeBytes = free,
                TotalBytes = total,
                Icon = ShellServices.GetSmallIcon(root, isDirectory: true)
            });
        }

        return drives;
    }

    /// <summary>Drive letter that owns a path, e.g. "O:\\ai\\x" -> "o".</summary>
    public static string GetLetter(string path)
    {
        var root = Path.GetPathRoot(path);
        return string.IsNullOrEmpty(root) ? string.Empty : root[..1].ToLowerInvariant();
    }
}
