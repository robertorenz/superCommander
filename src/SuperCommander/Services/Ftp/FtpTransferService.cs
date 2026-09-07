using System.IO;
using SuperCommander.Models;

namespace SuperCommander.Services.Ftp;

/// <summary>
/// Moves files between the local filesystem and an FTP server, reporting through
/// the same progress and conflict contract as the local copy engine so the
/// existing progress and overwrite dialogs work unchanged.
/// </summary>
public static class FtpTransferService
{
    private const int BufferSize = 128 * 1024;

    private sealed record Job(string Remote, string Local, bool IsDirectory, long Size);

    // ------------------------------------------------------------- download

    public static Task<FileOperationResult> DownloadAsync(FtpSession session,
        IReadOnlyList<FileItem> items, string localDirectory,
        IProgress<FileOperationProgress>? progress,
        Func<ConflictInfo, OverwriteAction> resolveConflict,
        CancellationToken token) =>
        session.RunExclusiveAsync(async client =>
        {
            var result = new FileOperationResult();
            var report = new FileOperationProgress();

            List<Job> jobs;
            try
            {
                jobs = await BuildDownloadJobsAsync(client, items, localDirectory, token);
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                return result;
            }
            catch (FtpException ex)
            {
                result.Failed++;
                result.Errors.Add(ex.Message);
                return result;
            }

            report.FilesTotal = jobs.Count(j => !j.IsDirectory);
            report.BytesTotal = jobs.Where(j => !j.IsDirectory).Sum(j => Math.Max(0, j.Size));
            progress?.Report(Clone(report));

            OverwriteAction? blanket = null;

            foreach (var job in jobs)
            {
                if (token.IsCancellationRequested) { result.Cancelled = true; break; }

                if (job.IsDirectory)
                {
                    Directory.CreateDirectory(job.Local);
                    continue;
                }

                report.CurrentFile = $"ftp://{session.Site.Host}{job.Remote}";
                report.CurrentTarget = job.Local;
                report.FilePercent = 0;
                progress?.Report(Clone(report));

                var target = job.Local;

                if (File.Exists(target))
                {
                    var action = blanket ?? Ask(resolveConflict, job, target, remoteIsSource: true, ref blanket);

                    if (action == OverwriteAction.Cancel) { result.Cancelled = true; break; }

                    if (action is OverwriteAction.Skip or OverwriteAction.SkipAll)
                    {
                        result.Skipped++;
                        report.FilesDone++;
                        report.BytesDone += Math.Max(0, job.Size);
                        progress?.Report(Clone(report));
                        continue;
                    }

                    if (action == OverwriteAction.Rename)
                        target = FileOperationService.MakeUniqueName(target);
                }

                try
                {
                    var directory = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                    long baseBytes = report.BytesDone;
                    await using (var file = new FileStream(target, FileMode.Create, FileAccess.Write,
                                     FileShare.None, BufferSize))
                    {
                        var inner = new Progress<long>(done =>
                        {
                            report.BytesDone = baseBytes + done;
                            report.FilePercent = job.Size > 0 ? done * 100.0 / job.Size : 0;
                            progress?.Report(Clone(report));
                        });

                        await client.DownloadAsync(job.Remote, file, inner, token);
                    }

                    report.BytesDone = baseBytes + Math.Max(0, job.Size);
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
                    result.Errors.Add($"{job.Remote}: {ex.Message}");
                }

                report.FilesDone++;
                progress?.Report(Clone(report));
            }

            return result;
        }, token);

    private static async Task<List<Job>> BuildDownloadJobsAsync(FtpClient client,
        IReadOnlyList<FileItem> items, string localDirectory, CancellationToken token)
    {
        var jobs = new List<Job>();

        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            if (item.RemotePath is null || item.IsParent) continue;

            var local = Path.Combine(localDirectory, Sanitise(item.Name));

            if (item.IsDirectory)
            {
                jobs.Add(new Job(item.RemotePath, local, true, 0));
                await WalkAsync(client, item.RemotePath, local, jobs, token);
            }
            else
            {
                jobs.Add(new Job(item.RemotePath, local, false, item.Size));
            }
        }

        return jobs;
    }

    private static async Task WalkAsync(FtpClient client, string remote, string local,
        List<Job> jobs, CancellationToken token)
    {
        foreach (var entry in await client.ListAsync(remote, token))
        {
            token.ThrowIfCancellationRequested();
            var childLocal = Path.Combine(local, Sanitise(entry.Name));

            if (entry.IsDirectory)
            {
                jobs.Add(new Job(entry.FullPath, childLocal, true, 0));
                await WalkAsync(client, entry.FullPath, childLocal, jobs, token);
            }
            else
            {
                jobs.Add(new Job(entry.FullPath, childLocal, false, entry.Size));
            }
        }
    }

    // --------------------------------------------------------------- upload

    public static Task<FileOperationResult> UploadAsync(FtpSession session,
        IReadOnlyList<string> localPaths, string remoteDirectory,
        IProgress<FileOperationProgress>? progress,
        Func<ConflictInfo, OverwriteAction> resolveConflict,
        CancellationToken token) =>
        session.RunExclusiveAsync(async client =>
        {
            var result = new FileOperationResult();
            var report = new FileOperationProgress();

            List<Job> jobs;
            try
            {
                jobs = BuildUploadJobs(localPaths, remoteDirectory, token);
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                return result;
            }

            report.FilesTotal = jobs.Count(j => !j.IsDirectory);
            report.BytesTotal = jobs.Where(j => !j.IsDirectory).Sum(j => Math.Max(0, j.Size));
            progress?.Report(Clone(report));

            OverwriteAction? blanket = null;

            foreach (var job in jobs)
            {
                if (token.IsCancellationRequested) { result.Cancelled = true; break; }

                if (job.IsDirectory)
                {
                    try
                    {
                        await client.CreateDirectoryAsync(job.Remote, token);
                    }
                    catch (FtpException)
                    {
                        // Almost always "already exists", which is fine.
                    }
                    continue;
                }

                report.CurrentFile = job.Local;
                report.CurrentTarget = $"ftp://{session.Site.Host}{job.Remote}";
                report.FilePercent = 0;
                progress?.Report(Clone(report));

                var target = job.Remote;

                long existing = await client.GetSizeAsync(target, token);
                if (existing >= 0)
                {
                    var action = blanket ?? Ask(resolveConflict, job with { Size = existing },
                        target, remoteIsSource: false, ref blanket);

                    if (action == OverwriteAction.Cancel) { result.Cancelled = true; break; }

                    if (action is OverwriteAction.Skip or OverwriteAction.SkipAll)
                    {
                        result.Skipped++;
                        report.FilesDone++;
                        report.BytesDone += Math.Max(0, job.Size);
                        progress?.Report(Clone(report));
                        continue;
                    }

                    if (action == OverwriteAction.Rename)
                        target = MakeUniqueRemoteName(target);
                }

                try
                {
                    long baseBytes = report.BytesDone;
                    await using (var file = new FileStream(job.Local, FileMode.Open, FileAccess.Read,
                                     FileShare.ReadWrite, BufferSize))
                    {
                        var inner = new Progress<long>(done =>
                        {
                            report.BytesDone = baseBytes + done;
                            report.FilePercent = job.Size > 0 ? done * 100.0 / job.Size : 0;
                            progress?.Report(Clone(report));
                        });

                        await client.UploadAsync(file, target, inner, token);
                    }

                    report.BytesDone = baseBytes + Math.Max(0, job.Size);
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
                    result.Errors.Add($"{job.Local}: {ex.Message}");
                }

                report.FilesDone++;
                progress?.Report(Clone(report));
            }

            return result;
        }, token);

    private static List<Job> BuildUploadJobs(IReadOnlyList<string> localPaths, string remoteDirectory,
        CancellationToken token)
    {
        var jobs = new List<Job>();

        foreach (var path in localPaths)
        {
            token.ThrowIfCancellationRequested();

            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            var remote = FtpPath.Combine(remoteDirectory, name);

            if (Directory.Exists(path))
            {
                jobs.Add(new Job(remote, path, true, 0));
                WalkLocal(path, remote, jobs, token);
            }
            else if (File.Exists(path))
            {
                long size = 0;
                try { size = new FileInfo(path).Length; } catch (Exception) { /* keep 0 */ }
                jobs.Add(new Job(remote, path, false, size));
            }
        }

        return jobs;
    }

    private static void WalkLocal(string local, string remote, List<Job> jobs, CancellationToken token)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };

        foreach (var entry in new DirectoryInfo(local).EnumerateFileSystemInfos("*", options))
        {
            token.ThrowIfCancellationRequested();
            var childRemote = FtpPath.Combine(remote, entry.Name);

            if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                jobs.Add(new Job(childRemote, entry.FullName, true, 0));
                WalkLocal(entry.FullName, childRemote, jobs, token);
            }
            else if (entry is FileInfo file)
            {
                long size = 0;
                try { size = file.Length; } catch (Exception) { /* keep 0 */ }
                jobs.Add(new Job(childRemote, file.FullName, false, size));
            }
        }
    }

    // --------------------------------------------------------------- helpers

    private static OverwriteAction Ask(Func<ConflictInfo, OverwriteAction> resolve, Job job,
        string targetPath, bool remoteIsSource, ref OverwriteAction? blanket)
    {
        long sourceSize, targetSize;
        DateTime sourceTime = DateTime.MinValue, targetTime = DateTime.MinValue;

        if (remoteIsSource)
        {
            sourceSize = job.Size;
            var info = new FileInfo(targetPath);
            targetSize = info.Exists ? info.Length : 0;
            targetTime = info.Exists ? info.LastWriteTime : DateTime.MinValue;
        }
        else
        {
            var info = new FileInfo(job.Local);
            sourceSize = info.Exists ? info.Length : 0;
            sourceTime = info.Exists ? info.LastWriteTime : DateTime.MinValue;
            targetSize = job.Size;
        }

        var action = resolve(new ConflictInfo
        {
            SourcePath = remoteIsSource ? job.Remote : job.Local,
            TargetPath = targetPath,
            SourceSize = sourceSize,
            TargetSize = targetSize,
            SourceModified = sourceTime,
            TargetModified = targetTime
        });

        // "Overwrite older" needs both timestamps; FTP rarely gives a reliable
        // one for the target, so treat it as a plain overwrite-all here.
        if (action == OverwriteAction.OverwriteAllOlder) action = OverwriteAction.OverwriteAll;

        if (action is OverwriteAction.OverwriteAll or OverwriteAction.SkipAll) blanket = action;

        return action;
    }

    private static string MakeUniqueRemoteName(string path)
    {
        var parent = FtpPath.GetParent(path);
        var name = FtpPath.GetName(path);

        int dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var extension = dot > 0 ? name[dot..] : string.Empty;

        return FtpPath.Combine(parent, $"{stem} (2){extension}");
    }

    /// <summary>Remote names may contain characters Windows will not accept.</summary>
    private static string Sanitise(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();

        for (int i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';

        return new string(chars);
    }

    private static FileOperationProgress Clone(FileOperationProgress p) => new()
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
