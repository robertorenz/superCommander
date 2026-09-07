using System.IO;
using SuperCommander.Models;

namespace SuperCommander.Services.Ftp;

/// <summary>
/// One live FTP connection bound to a pane: keeps the current remote directory
/// and hands the pane <see cref="FileItem"/> rows it can display like any other
/// listing.
///
/// Every call is serialised through a semaphore because a single FTP control
/// connection cannot interleave commands.
/// </summary>
public sealed class FtpSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FtpSession(FtpSite site)
    {
        Site = site;
        Client = new FtpClient(FtpConnectionInfo.FromSite(site));
    }

    public FtpSite Site { get; }

    public FtpClient Client { get; }

    public string CurrentPath { get; private set; } = "/";

    public bool IsConnected => Client.IsConnected;

    /// <summary>"ftp://host/path" - what the pane shows in its path bar.</summary>
    public string DisplayPath => $"ftp://{Site.Host}{CurrentPath}";

    public event EventHandler? Disconnected;

    public async Task ConnectAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            await Client.ConnectAsync(token);

            var start = FtpPath.Normalise(Site.RemotePath);
            if (start != "/")
            {
                try
                {
                    await Client.ChangeDirectoryAsync(start, token);
                }
                catch (FtpException)
                {
                    // A missing start folder should not fail the whole connect.
                }
            }

            CurrentPath = await Client.GetWorkingDirectoryAsync(token);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Lists a remote directory as pane rows, with ".." when not at the root.</summary>
    public async Task<List<FileItem>> ListAsync(string path, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var target = FtpPath.Normalise(path);
            await Client.ChangeDirectoryAsync(target, token);
            CurrentPath = await Client.GetWorkingDirectoryAsync(token);

            var entries = await Client.ListAsync(CurrentPath, token);
            var items = new List<FileItem>(entries.Count + 1);

            // ".." always present - at the root it leaves the connection.
            items.Add(FileItem.CreateParent(FtpPath.GetParent(CurrentPath)));

            foreach (var entry in entries)
            {
                var attributes = entry.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal;
                if (entry.IsSymlink) attributes |= FileAttributes.ReparsePoint;

                items.Add(new FileItem(entry.FullPath, entry.Name, entry.IsDirectory,
                    entry.IsDirectory ? FileItem.SizeUnknown : entry.Size,
                    entry.Modified, entry.Modified, attributes)
                {
                    RemotePath = entry.FullPath
                });
            }

            return items;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> IsAtRootAsync() => await Task.FromResult(CurrentPath is "/" or "");

    // ------------------------------------------------------------ operations

    public async Task CreateDirectoryAsync(string name, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var path = name.StartsWith(FtpPath.Separator) ? name : FtpPath.Combine(CurrentPath, name);
            await Client.CreateDirectoryAsync(path, token);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RenameAsync(string fromPath, string newName, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var target = newName.StartsWith(FtpPath.Separator)
                ? newName
                : FtpPath.Combine(FtpPath.GetParent(fromPath), newName);

            await Client.RenameAsync(fromPath, target, token);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes files and folders, recursing into directories depth-first.</summary>
    public async Task<List<string>> DeleteAsync(IReadOnlyList<FileItem> items, CancellationToken token = default)
    {
        var errors = new List<string>();

        await _gate.WaitAsync(token);
        try
        {
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                if (item.RemotePath is null) continue;

                try
                {
                    if (item.IsDirectory) await DeleteTreeAsync(item.RemotePath, token);
                    else await Client.DeleteFileAsync(item.RemotePath, token);
                }
                catch (FtpException ex)
                {
                    errors.Add($"{item.Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        return errors;
    }

    private async Task DeleteTreeAsync(string path, CancellationToken token)
    {
        foreach (var entry in await Client.ListAsync(path, token))
        {
            token.ThrowIfCancellationRequested();

            if (entry.IsDirectory) await DeleteTreeAsync(entry.FullPath, token);
            else await Client.DeleteFileAsync(entry.FullPath, token);
        }

        await Client.RemoveDirectoryAsync(path, token);
    }

    // -------------------------------------------------------------- transfers

    /// <summary>Runs a transfer with the control connection held for its duration.</summary>
    public async Task<T> RunExclusiveAsync<T>(Func<FtpClient, Task<T>> action, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            return await action(Client);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        try
        {
            await Client.DisconnectAsync();
        }
        finally
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _gate.Dispose();
    }
}
