using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static SuperCommander.Interop.NativeMethods;

namespace SuperCommander.Interop;

/// <summary>
/// Friendly wrapper over the shell: icons, launching, properties, recycle bin,
/// clipboard and volume information.
/// </summary>
internal static class ShellServices
{
    // Extensions whose icon is embedded in the file itself and therefore cannot
    // be cached per extension.
    private static readonly HashSet<string> PerFileIconExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".ico", ".cur", ".ani", ".scr", ".cpl", ".msc", ".url", ".appref-ms"
    };

    private static readonly ConcurrentDictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> TypeNameCache = new(StringComparer.OrdinalIgnoreCase);

    private const string DirectoryIconKey = "dir";
    private const string DriveIconKey = "drive";

    // ------------------------------------------------------------------ icons

    /// <summary>
    /// Small (16px) system icon for a path. Safe to call from a worker thread -
    /// the returned bitmap is frozen.
    /// </summary>
    internal static ImageSource? GetSmallIcon(string path, bool isDirectory)
    {
        string key;
        if (isDirectory)
        {
            key = path.Length <= 3 && path.EndsWith(":\\", StringComparison.Ordinal) ? DriveIconKey + path : DirectoryIconKey;
        }
        else
        {
            var ext = Path.GetExtension(path);
            key = PerFileIconExtensions.Contains(ext) ? path : (string.IsNullOrEmpty(ext) ? "none" : ext);
        }

        if (IconCache.TryGetValue(key, out var cached)) return cached;

        var icon = LoadIcon(path, isDirectory,
            usePlaceholder: !string.Equals(key, path, StringComparison.Ordinal)
                            && !key.StartsWith(DriveIconKey, StringComparison.Ordinal));
        IconCache[key] = icon;
        return icon;
    }

    private static ImageSource? LoadIcon(string path, bool isDirectory, bool usePlaceholder)
    {
        var info = new SHFILEINFO();
        uint flags = (uint)(SHGFI.Icon | SHGFI.SmallIcon);
        uint attrs = 0;

        // For generic extensions we ask the shell to answer from the attributes
        // alone, which avoids touching the disk for every row.
        if (usePlaceholder)
        {
            flags |= (uint)SHGFI.UseFileAttributes;
            attrs = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        }

        var result = SHGetFileInfo(path, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    /// <summary>Localised shell type description, e.g. "Text Document".</summary>
    internal static string GetTypeName(string path, bool isDirectory)
    {
        var ext = isDirectory ? DirectoryIconKey : Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) ext = "none";
        if (TypeNameCache.TryGetValue(ext, out var cached)) return cached;

        var info = new SHFILEINFO();
        uint attrs = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        SHGetFileInfo(path, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
            (uint)(SHGFI.TypeName | SHGFI.UseFileAttributes));

        var name = info.szTypeName ?? string.Empty;
        TypeNameCache[ext] = name;
        return name;
    }

    // -------------------------------------------------------------- launching

    /// <summary>Opens a file or folder with its default shell association.</summary>
    internal static bool Open(string path, string? workingDirectory = null)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(path) ?? string.Empty
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Runs a shell verb such as "edit", "print" or "runas".</summary>
    internal static bool InvokeVerb(IntPtr hwnd, string path, string verb)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_INVOKEIDLIST,
            hwnd = hwnd,
            lpVerb = verb,
            lpFile = path,
            lpDirectory = Path.GetDirectoryName(path),
            nShow = SW_SHOWNORMAL
        };
        return ShellExecuteEx(ref info);
    }

    /// <summary>Opens the native Explorer property sheet.</summary>
    internal static bool ShowProperties(IntPtr hwnd, string path) => InvokeVerb(hwnd, path, "properties");

    /// <summary>Opens an Explorer window with the item selected.</summary>
    internal static bool RevealInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Starts a command line in the given working directory.</summary>
    internal static bool RunCommand(string command, string workingDirectory, bool keepOpen = false)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = (keepOpen ? "/k " : "/c ") + command,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ------------------------------------------------------------ recycle bin

    /// <summary>
    /// Deletes through the shell so the items land in the recycle bin and the
    /// standard confirmation / undo semantics apply.
    /// </summary>
    internal static bool DeleteToRecycleBin(IntPtr hwnd, IReadOnlyList<string> paths, bool permanent, bool confirm)
    {
        if (paths.Count == 0) return true;

        // pFrom is a double-null-terminated list of null-separated paths.
        var buffer = new StringBuilder();
        foreach (var p in paths)
        {
            buffer.Append(p);
            buffer.Append('\0');
        }
        buffer.Append('\0');

        ushort flags = FOF_NOCONFIRMMKDIR;
        if (!permanent) flags |= FOF_ALLOWUNDO;
        if (!confirm) flags |= FOF_NOCONFIRMATION;

        var op = new SHFILEOPSTRUCT
        {
            hwnd = hwnd,
            wFunc = FO_DELETE,
            pFrom = buffer.ToString(),
            pTo = null,
            fFlags = flags
        };

        var result = SHFileOperation(ref op);
        return result == 0 && !op.fAnyOperationsAborted;
    }

    // -------------------------------------------------------------- clipboard

    private const int DropEffectCopy = 5;
    private const int DropEffectMove = 2;

    /// <summary>Puts files on the clipboard the way Explorer does (copy or cut).</summary>
    internal static void CopyToClipboard(IReadOnlyList<string> paths, bool cut)
    {
        if (paths.Count == 0) return;

        var files = new System.Collections.Specialized.StringCollection();
        foreach (var p in paths) files.Add(p);

        var data = new DataObject();
        data.SetFileDropList(files);

        var effect = new MemoryStream(4);
        effect.Write(BitConverter.GetBytes(cut ? DropEffectMove : DropEffectCopy), 0, 4);
        effect.Position = 0;
        data.SetData("Preferred DropEffect", effect);

        Clipboard.SetDataObject(data, true);
    }

    /// <summary>Reads a file list off the clipboard. <paramref name="cut"/> reports a pending move.</summary>
    internal static IReadOnlyList<string> GetClipboardFiles(out bool cut)
    {
        cut = false;
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null || !data.GetDataPresent(DataFormats.FileDrop)) return Array.Empty<string>();

            if (data.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4)
            {
                var bytes = new byte[4];
                ms.Position = 0;
                _ = ms.Read(bytes, 0, 4);
                cut = (BitConverter.ToInt32(bytes, 0) & DropEffectMove) == DropEffectMove;
            }

            return (string[])data.GetData(DataFormats.FileDrop)! ?? Array.Empty<string>();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    // ----------------------------------------------------------------- window

    /// <summary>Matches the native title bar to the active theme on Windows 10/11.</summary>
    internal static void SetTitleBarTheme(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int value = dark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, ref value, sizeof(int));
    }

    // ---------------------------------------------------------------- volumes

    internal static (ulong Free, ulong Total) GetDriveSpace(string path)
    {
        try
        {
            if (GetDiskFreeSpaceEx(path, out _, out var total, out var free))
                return (free, total);
        }
        catch (Exception)
        {
            // fall through
        }
        return (0, 0);
    }

    internal static string GetVolumeLabel(string root)
    {
        try
        {
            var label = new StringBuilder(261);
            var fs = new StringBuilder(261);
            if (GetVolumeInformation(root, label, label.Capacity, out _, out _, out _, fs, fs.Capacity))
                return label.ToString();
        }
        catch (Exception)
        {
            // fall through
        }
        return string.Empty;
    }
}
