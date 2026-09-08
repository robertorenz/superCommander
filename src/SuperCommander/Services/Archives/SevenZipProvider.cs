using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace SuperCommander.Services.Archives;

/// <summary>
/// Formats the base framework cannot read - 7z, RAR, CAB, ISO, xz, bzip2 and
/// the rest - by driving an installed 7-Zip.
///
/// This is an *optional* enhancement, not a dependency: when 7z.exe is absent
/// the provider reports itself unavailable and the app explains how to enable it
/// rather than failing obscurely.
/// </summary>
public sealed class SevenZipProvider : IArchiveProvider
{
    private static readonly Lazy<string?> Executable = new(Locate);

    // Formats 7-Zip reads that the native providers do not cover.
    private static readonly string[] ReadExtensions =
    {
        ".7z", ".rar", ".cab", ".iso", ".arj", ".lzh", ".lha", ".xz", ".bz2", ".tbz", ".tbz2",
        ".txz", ".lzma", ".z", ".taz", ".cpio", ".wim", ".swm", ".rpm", ".deb", ".dmg", ".chm", ".msi"
    };

    // 7-Zip can only create a subset; RAR in particular is read-only.
    private static readonly string[] WriteExtensions = { ".7z", ".xz", ".bz2", ".wim" };

    public string Name => "7-Zip";

    public string? Path7z => Executable.Value;

    public bool IsAvailable => Executable.Value is not null;

    public string? UnavailableReason => IsAvailable
        ? null
        : "7-Zip is not installed. Install it from https://www.7-zip.org and this format will work.";

    public bool CanHandle(string path)
    {
        var name = System.IO.Path.GetFileName(path);

        // Compound extensions the tar provider deliberately leaves alone.
        if (name.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase))
            return true;

        var extension = System.IO.Path.GetExtension(path);
        return ReadExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    public bool CanCreate(string path) =>
        WriteExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------- discovery

    private static string? Locate()
    {
        foreach (var candidate in CandidatePaths())
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)) return candidate;
            }
            catch (Exception)
            {
                // A malformed candidate must not stop the search.
            }
        }
        return null;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        // Registry first - that is where an installed 7-Zip records itself.
        foreach (var key in new[] { @"SOFTWARE\7-Zip", @"SOFTWARE\WOW6432Node\7-Zip" })
        {
            string? installed = null;
            try
            {
                using var handle = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
                installed = handle?.GetValue("Path") as string;
            }
            catch (Exception)
            {
                // Registry access can be denied; fall through to the fixed paths.
            }

            if (!string.IsNullOrWhiteSpace(installed))
                yield return System.IO.Path.Combine(installed, "7z.exe");
        }

        foreach (var variable in new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" })
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(root))
                yield return System.IO.Path.Combine(root, "7-Zip", "7z.exe");
        }

        // Finally anything on PATH, including the standalone 7za.
        var search = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in search.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var exe in new[] { "7z.exe", "7za.exe" })
            {
                string combined;
                try { combined = System.IO.Path.Combine(directory.Trim(), exe); }
                catch (Exception) { continue; }
                yield return combined;
            }
        }
    }

    // ------------------------------------------------------------------ list

    public List<ArchiveEntryInfo> List(string archivePath)
    {
        var entries = new List<ArchiveEntryInfo>();
        if (Executable.Value is null) return entries;

        var output = Run($"l -slt -ba -y -p -- \"{archivePath}\"", null, null, CancellationToken.None, out _);

        string? path = null;
        long size = 0;
        var modified = DateTime.MinValue;
        bool isDirectory = false;

        void Flush()
        {
            if (path is null) return;

            entries.Add(new ArchiveEntryInfo
            {
                FullName = path.Replace('\\', '/'),
                Length = isDirectory ? -1 : size,
                LastWriteTime = modified,
                IsDirectory = isDirectory
            });

            path = null;
            size = 0;
            modified = DateTime.MinValue;
            isDirectory = false;
        }

        foreach (var line in output.Split('\n'))
        {
            var text = line.TrimEnd('\r');

            if (text.Length == 0) { Flush(); continue; }

            int equals = text.IndexOf('=');
            if (equals < 1) continue;

            var key = text[..equals].Trim();
            var value = text[(equals + 1)..].Trim();

            switch (key)
            {
                case "Path":
                    Flush();
                    path = value;
                    break;
                case "Size":
                    long.TryParse(value, out size);
                    break;
                case "Modified":
                    DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out modified);
                    break;
                case "Attributes":
                    isDirectory = value.Contains('D');
                    break;
                case "Folder":
                    if (value.Equals("+", StringComparison.Ordinal)) isDirectory = true;
                    break;
            }
        }

        Flush();
        return entries;
    }

    // --------------------------------------------------------------- extract

    public Task<FileOperationResult> ExtractAsync(string archivePath, IReadOnlyList<string>? entries,
        string targetDirectory, IProgress<ArchiveProgress>? progress, CancellationToken token) =>
        Task.Run(() =>
        {
            var result = new FileOperationResult();

            if (Executable.Value is null)
            {
                result.Failed++;
                result.Errors.Add(UnavailableReason!);
                return result;
            }

            try
            {
                Directory.CreateDirectory(targetDirectory);

                int total = 0;
                try
                {
                    var wanted = ArchivePath.BuildFilter(entries);
                    total = List(archivePath).Count(e => !e.IsDirectory && ArchivePath.Matches(wanted, e.FullName));
                }
                catch (Exception)
                {
                    // Progress total is a nicety.
                }

                var arguments = new StringBuilder();
                arguments.Append("x -y -aoa -bb1 -p -o\"").Append(targetDirectory.TrimEnd('\\')).Append("\" -- \"")
                         .Append(archivePath).Append('"');

                if (entries is { Count: > 0 })
                    foreach (var entry in entries)
                        arguments.Append(" \"").Append(entry.Replace('\\', '/')).Append('"');

                int done = 0;
                Run(arguments.ToString(), line =>
                {
                    // -bb1 emits "- relative/path" for each item processed.
                    if (!line.StartsWith("- ", StringComparison.Ordinal)) return;

                    done++;
                    progress?.Report(new ArchiveProgress
                    {
                        CurrentEntry = line[2..].Trim(),
                        Done = done,
                        Total = total
                    });
                }, null, token, out int exitCode);

                if (token.IsCancellationRequested) result.Cancelled = true;

                if (exitCode == 0) result.Succeeded = Math.Max(done, 1);
                else
                {
                    result.Failed++;
                    result.Errors.Add(Describe(exitCode));
                }

                progress?.Report(new ArchiveProgress { Done = done, Total = total });
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add(ex.Message);
            }

            return result;
        }, token);

    // ---------------------------------------------------------------- create

    public Task<FileOperationResult> CreateAsync(IReadOnlyList<string> sources, string archivePath,
        string baseDirectory, IProgress<ArchiveProgress>? progress, CancellationToken token) =>
        Task.Run(() =>
        {
            var result = new FileOperationResult();

            if (Executable.Value is null)
            {
                result.Failed++;
                result.Errors.Add(UnavailableReason!);
                return result;
            }

            try
            {
                var directory = System.IO.Path.GetDirectoryName(archivePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                var arguments = new StringBuilder();
                arguments.Append("a -y -bb1 -- \"").Append(archivePath).Append('"');
                foreach (var source in sources)
                    arguments.Append(" \"").Append(source.TrimEnd('\\')).Append('"');

                int done = 0;
                Run(arguments.ToString(), line =>
                {
                    if (!line.StartsWith("+ ", StringComparison.Ordinal)) return;

                    done++;
                    progress?.Report(new ArchiveProgress { CurrentEntry = line[2..].Trim(), Done = done, Total = 0 });
                }, baseDirectory, token, out int exitCode);

                if (token.IsCancellationRequested) result.Cancelled = true;

                if (exitCode == 0) result.Succeeded = Math.Max(done, 1);
                else
                {
                    result.Failed++;
                    result.Errors.Add(Describe(exitCode));
                }
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add(ex.Message);
            }

            return result;
        }, token);

    // ------------------------------------------------------------- process

    private static string Run(string arguments, Action<string>? onLine, string? workingDirectory,
        CancellationToken token, out int exitCode)
    {
        var info = new ProcessStartInfo
        {
            FileName = Executable.Value!,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };

        using var process = Process.Start(info) ?? throw new IOException("Could not start 7-Zip.");

        // Never let a password prompt block the pipe.
        try { process.StandardInput.Close(); } catch (Exception) { /* already closed */ }

        var captured = new StringBuilder();
        string? line;
        while ((line = process.StandardOutput.ReadLine()) is not null)
        {
            if (token.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                break;
            }

            if (onLine is null) captured.Append(line).Append('\n');
            else onLine(line);
        }

        process.WaitForExit();
        exitCode = process.ExitCode;
        return captured.ToString();
    }

    private static string Describe(int exitCode) => exitCode switch
    {
        1 => "7-Zip finished with warnings; some files may not have been processed.",
        2 => "7-Zip reported a fatal error. The archive may be corrupt or password protected.",
        7 => "7-Zip rejected the command line.",
        8 => "7-Zip ran out of memory.",
        255 => "The operation was cancelled.",
        _ => $"7-Zip exited with code {exitCode}."
    };
}
