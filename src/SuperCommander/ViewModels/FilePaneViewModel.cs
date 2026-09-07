using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using SuperCommander.Interop;
using SuperCommander.Models;
using SuperCommander.Services;
using SuperCommander.Services.Ftp;

namespace SuperCommander.ViewModels;

/// <summary>One tab inside a pane. Holds the location, not the loaded rows.</summary>
public sealed class PaneTab : ObservableObject
{
    private string _path = string.Empty;
    private bool _isLocked;
    private bool _isActive;

    public string Path
    {
        get => _path;
        set
        {
            if (SetProperty(ref _path, value)) OnPropertyChanged(nameof(Title));
        }
    }

    /// <summary>Live FTP connection when this tab is showing a remote server.</summary>
    public FtpSession? Ftp { get; set; }

    public bool IsFtp => Ftp is not null;

    /// <summary>Set while browsing inside a .zip.</summary>
    public string? ArchivePath { get; set; }

    /// <summary>Folder inside the archive; empty means the archive root.</summary>
    public string ArchiveInternalPath { get; set; } = string.Empty;

    public bool IsArchive => ArchivePath is not null;

    /// <summary>Locked tabs always reopen at their original folder.</summary>
    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            if (SetProperty(ref _isLocked, value)) OnPropertyChanged(nameof(Title));
        }
    }

    /// <summary>True for the tab currently shown by the pane.</summary>
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public bool BranchView { get; set; }

    public SortSpec Sort { get; set; } = new();

    /// <summary>Name of the row the cursor was on, restored after a reload.</summary>
    public string? CursorName { get; set; }

    public string Title
    {
        get
        {
            string? name;
            if (IsFtp) name = Ftp!.Site.Display;
            else if (IsArchive) name = System.IO.Path.GetFileName(ArchivePath);
            else name = System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));

            if (string.IsNullOrEmpty(name)) name = Path;
            return IsLocked ? "* " + name : name;
        }
    }

    /// <summary>Refreshes the tab caption after connecting or disconnecting.</summary>
    public void RaiseTitleChanged() => OnPropertyChanged(nameof(Title));
}

/// <summary>
/// A single file pane: tab strip, drive bar, listing, cursor, marks and sorting.
/// </summary>
public sealed class FilePaneViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly Dispatcher _dispatcher;

    private readonly List<FileItem> _allItems = new();
    private readonly List<string> _history = new();
    private int _historyIndex = -1;

    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _iconCts;

    private PaneTab _activeTab = new();
    private FileItem? _selectedItem;
    private bool _isActive;
    private bool _isLoading;
    private string _quickFilter = string.Empty;
    private string _statusText = string.Empty;
    private string _errorText = string.Empty;
    private string _freeSpaceText = string.Empty;

    public FilePaneViewModel(string side, SettingsService settings)
    {
        Side = side;
        _settings = settings;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        Drives = new ObservableCollection<DriveEntry>(DriveService.GetDrives());
        Tabs = new ObservableCollection<PaneTab>();
        Items = new ObservableCollection<FileItem>();
    }

    public string Side { get; }

    public ObservableCollection<PaneTab> Tabs { get; }

    public ObservableCollection<DriveEntry> Drives { get; }

    public ObservableCollection<FileItem> Items { get; }

    public event EventHandler? PathChanged;

    /// <summary>Raised when the view should pull keyboard focus back to the list.</summary>
    public event EventHandler? FocusRequested;

    // ------------------------------------------------------------------ state

    public PaneTab ActiveTab
    {
        get => _activeTab;
        private set
        {
            var previous = _activeTab;
            if (SetProperty(ref _activeTab, value))
            {
                previous.IsActive = false;
                value.IsActive = true;
                OnPropertyChanged(nameof(CurrentPath));
                OnPropertyChanged(nameof(DisplayPath));
                OnPropertyChanged(nameof(IsArchiveMode));
            }
        }
    }

    public FileItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                ActiveTab.CursorName = value?.Name;
                UpdateStatus();
                OnPropertyChanged(nameof(SelectedInfo));
            }
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string CurrentPath => ActiveTab.Path;

    public bool IsArchiveMode => ActiveTab.IsArchive;

    public bool IsFtpMode => ActiveTab.IsFtp;

    public FtpSession? Ftp => ActiveTab.Ftp;

    public string DisplayPath
    {
        get
        {
            if (ActiveTab.IsFtp) return ActiveTab.Ftp!.DisplayPath;

            if (!ActiveTab.IsArchive) return ActiveTab.Path;
            var inner = ActiveTab.ArchiveInternalPath.Replace('/', Path.DirectorySeparatorChar);
            return inner.Length == 0
                ? ActiveTab.ArchivePath!
                : Path.Combine(ActiveTab.ArchivePath!, inner);
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public string FreeSpaceText
    {
        get => _freeSpaceText;
        private set => SetProperty(ref _freeSpaceText, value);
    }

    public string SelectedInfo => SelectedItem is null || SelectedItem.IsParent
        ? string.Empty
        : $"{SelectedItem.Name}   {SelectedItem.DisplayDate}";

    /// <summary>Live name filter driven by Ctrl+S.</summary>
    public string QuickFilter
    {
        get => _quickFilter;
        set
        {
            if (SetProperty(ref _quickFilter, value)) ApplyFilter();
        }
    }

    public SortSpec Sort => ActiveTab.Sort;

    // ------------------------------------------------------------------- tabs

    public void InitialiseTabs(PaneSettings settings)
    {
        Tabs.Clear();
        foreach (var path in settings.TabPaths)
        {
            Tabs.Add(new PaneTab
            {
                Path = path,
                Sort = new SortSpec { Column = settings.Sort.Column, Descending = settings.Sort.Descending }
            });
        }

        if (Tabs.Count == 0)
            Tabs.Add(new PaneTab { Path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) });

        int index = Math.Clamp(settings.ActiveTab, 0, Tabs.Count - 1);
        ActiveTab = Tabs[index];
    }

    public PaneSettings CaptureSettings() => new()
    {
        // Tab.Path stays local even while connected, so sessions are not restored
        // as bogus local folders on the next launch.
        TabPaths = Tabs.Select(t => t.Path).ToList(),
        ActiveTab = Math.Max(0, Tabs.IndexOf(ActiveTab)),
        Sort = new SortSpec { Column = ActiveTab.Sort.Column, Descending = ActiveTab.Sort.Descending }
    };

    public async Task ActivateTabAsync(PaneTab tab)
    {
        if (!Tabs.Contains(tab)) return;
        ActiveTab = tab;
        await ReloadAsync();
        PathChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task NewTabAsync(string? path = null)
    {
        var tab = new PaneTab
        {
            Path = path ?? CurrentPath,
            Sort = new SortSpec { Column = ActiveTab.Sort.Column, Descending = ActiveTab.Sort.Descending }
        };
        Tabs.Add(tab);
        await ActivateTabAsync(tab);
    }

    public async Task CloseTabAsync(PaneTab? tab = null)
    {
        tab ??= ActiveTab;
        if (Tabs.Count <= 1) return;

        int index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);

        if (ReferenceEquals(tab, ActiveTab))
            await ActivateTabAsync(Tabs[Math.Clamp(index, 0, Tabs.Count - 1)]);
    }

    public async Task NextTabAsync(int delta)
    {
        if (Tabs.Count <= 1) return;
        int index = (Tabs.IndexOf(ActiveTab) + delta + Tabs.Count) % Tabs.Count;
        await ActivateTabAsync(Tabs[index]);
    }

    // ------------------------------------------------------------- navigation

    public async Task NavigateAsync(string path, string? cursorName = null, bool recordHistory = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        // While a tab holds an FTP session every path is a remote one.
        if (ActiveTab.IsFtp)
        {
            await NavigateFtpAsync(path, cursorName);
            return;
        }

        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            ErrorText = $"Invalid path: {path}";
            return;
        }

        if (!Directory.Exists(path))
        {
            // Entering an archive by typing its path.
            if (File.Exists(path) && ArchiveService.IsSupported(path))
            {
                await EnterArchiveAsync(path);
                return;
            }

            ErrorText = $"The folder \"{path}\" does not exist.";
            return;
        }

        if (recordHistory && !string.IsNullOrEmpty(ActiveTab.Path))
            PushHistory(ActiveTab.Path);

        ActiveTab.ArchivePath = null;
        ActiveTab.ArchiveInternalPath = string.Empty;
        ActiveTab.BranchView = false;
        ActiveTab.Path = path;
        ActiveTab.CursorName = cursorName;

        OnPropertyChanged(nameof(CurrentPath));
        OnPropertyChanged(nameof(DisplayPath));
        OnPropertyChanged(nameof(IsArchiveMode));

        await ReloadAsync();
        PathChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task GoUpAsync()
    {
        if (ActiveTab.IsFtp)
        {
            var session = ActiveTab.Ftp!;
            var remoteCurrent = session.CurrentPath;

            // ".." at the server root closes the connection.
            if (remoteCurrent is "/" or "")
            {
                await DisconnectFtpAsync();
                return;
            }

            await NavigateFtpAsync(FtpPath.GetParent(remoteCurrent), FtpPath.GetName(remoteCurrent));
            return;
        }

        if (ActiveTab.IsArchive)
        {
            if (ActiveTab.ArchiveInternalPath.Length == 0)
            {
                var container = Path.GetDirectoryName(ActiveTab.ArchivePath!) ?? ActiveTab.Path;
                var archiveName = Path.GetFileName(ActiveTab.ArchivePath!);
                await NavigateAsync(container, archiveName);
                return;
            }

            var trimmed = ActiveTab.ArchiveInternalPath.TrimEnd('/');
            int slash = trimmed.LastIndexOf('/');
            ActiveTab.ArchiveInternalPath = slash < 0 ? string.Empty : trimmed[..slash];
            await ReloadAsync();
            return;
        }

        var parent = Directory.GetParent(CurrentPath);
        if (parent is null) return;

        var current = Path.GetFileName(CurrentPath.TrimEnd(Path.DirectorySeparatorChar));
        await NavigateAsync(parent.FullName, current);
    }

    public async Task GoRootAsync()
    {
        var root = Path.GetPathRoot(CurrentPath);
        if (!string.IsNullOrEmpty(root)) await NavigateAsync(root);
    }

    public async Task GoBackAsync()
    {
        if (_historyIndex <= 0) return;
        _historyIndex--;
        await NavigateAsync(_history[_historyIndex], recordHistory: false);
    }

    public async Task GoForwardAsync()
    {
        if (_historyIndex >= _history.Count - 1) return;
        _historyIndex++;
        await NavigateAsync(_history[_historyIndex], recordHistory: false);
    }

    public IReadOnlyList<string> History => _history;

    private void PushHistory(string path)
    {
        if (_history.Count > 0 && string.Equals(_history[^1], path, StringComparison.OrdinalIgnoreCase))
            return;

        // Drop the forward branch when navigating somewhere new.
        if (_historyIndex >= 0 && _historyIndex < _history.Count - 1)
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);

        _history.Add(path);
        if (_history.Count > 64) _history.RemoveAt(0);
        _historyIndex = _history.Count - 1;
    }

    /// <summary>Opens a row: folders navigate, archives open in place, files launch.</summary>
    public async Task<bool> EnterAsync(FileItem item)
    {
        if (item.IsParent)
        {
            await GoUpAsync();
            return true;
        }

        if (ActiveTab.IsFtp)
        {
            if (item.IsDirectory && item.RemotePath is not null)
            {
                await NavigateFtpAsync(item.RemotePath);
                return true;
            }
            return false; // a remote file is opened by MainViewModel
        }

        if (ActiveTab.IsArchive)
        {
            if (item.IsDirectory && item.ArchiveEntryPath is not null)
            {
                ActiveTab.ArchiveInternalPath = item.ArchiveEntryPath;
                await ReloadAsync();
                return true;
            }
            return false; // extracting is handled by the copy command
        }

        if (item.IsDirectory)
        {
            await NavigateAsync(item.FullPath);
            return true;
        }

        if (ArchiveService.IsSupported(item.FullPath))
        {
            await EnterArchiveAsync(item.FullPath);
            return true;
        }

        return ShellServices.Open(item.FullPath, CurrentPath);
    }

    public async Task EnterArchiveAsync(string zipPath)
    {
        PushHistory(ActiveTab.Path);
        ActiveTab.ArchivePath = zipPath;
        ActiveTab.ArchiveInternalPath = string.Empty;
        ActiveTab.Path = Path.GetDirectoryName(zipPath) ?? ActiveTab.Path;

        OnPropertyChanged(nameof(DisplayPath));
        OnPropertyChanged(nameof(IsArchiveMode));

        await ReloadAsync();
        PathChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------- FTP

    /// <summary>
    /// Opens an FTP session on this tab. The tab keeps its local path so that
    /// disconnecting returns to where the user was.
    /// </summary>
    public async Task ConnectFtpAsync(FtpSite site)
    {
        await DisconnectFtpAsync(reload: false);

        var session = new FtpSession(site);
        IsLoading = true;
        ErrorText = string.Empty;

        try
        {
            await session.ConnectAsync();
        }
        catch (Exception ex)
        {
            IsLoading = false;

            // Keep the protocol log readable by the caller before tearing down.
            LastFtpLog = string.Join(Environment.NewLine, session.Client.Log.TakeLast(40));
            await session.DisposeAsync();

            throw new FtpException(ex.Message);
        }

        ActiveTab.Ftp = session;
        ActiveTab.BranchView = false;
        ActiveTab.ArchivePath = null;
        ActiveTab.CursorName = null;
        ActiveTab.RaiseTitleChanged();

        OnPropertyChanged(nameof(IsFtpMode));
        OnPropertyChanged(nameof(Ftp));
        OnPropertyChanged(nameof(DisplayPath));

        await ReloadAsync();
        PathChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Protocol trace from the most recent failed connection attempt.</summary>
    public string LastFtpLog { get; private set; } = string.Empty;

    public async Task DisconnectFtpAsync(bool reload = true)
    {
        var session = ActiveTab.Ftp;
        if (session is null) return;

        ActiveTab.Ftp = null;
        ActiveTab.CursorName = null;
        ActiveTab.RaiseTitleChanged();

        try
        {
            await session.DisposeAsync();
        }
        catch (Exception)
        {
            // The connection is being abandoned either way.
        }

        OnPropertyChanged(nameof(IsFtpMode));
        OnPropertyChanged(nameof(Ftp));
        OnPropertyChanged(nameof(DisplayPath));

        if (reload)
        {
            await ReloadAsync();
            PathChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task NavigateFtpAsync(string remotePath, string? cursorName = null)
    {
        if (ActiveTab.Ftp is null) return;

        ActiveTab.CursorName = cursorName;
        var normalised = FtpPath.Normalise(remotePath);

        IsLoading = true;
        try
        {
            var items = await ActiveTab.Ftp.ListAsync(normalised);
            ApplyRemoteListing(items, cursorName);
            ErrorText = string.Empty;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }

        OnPropertyChanged(nameof(DisplayPath));
        PathChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyRemoteListing(List<FileItem> items, string? cursorName)
    {
        DirectoryService.Sort(items, ActiveTab.Sort, _settings.Current.DirectoriesFirst);

        _allItems.Clear();
        _allItems.AddRange(items);
        ApplyFilter();
        RestoreCursor(cursorName);
        UpdateFreeSpace();

        if (_settings.Current.ShowIcons) StartIconLoad(items);
    }

    public async Task ToggleBranchViewAsync()
    {
        if (ActiveTab.IsArchive) return;
        ActiveTab.BranchView = !ActiveTab.BranchView;
        await ReloadAsync();
    }

    // ---------------------------------------------------------------- loading

    public async Task ReloadAsync()
    {
        _loadCts?.Cancel();
        _iconCts?.Cancel();

        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var token = cts.Token;

        IsLoading = true;
        ErrorText = string.Empty;

        var remembered = ActiveTab.CursorName;
        var markedNames = _allItems.Where(i => i.IsMarked)
            .Select(i => i.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            List<FileItem> items;
            string? error = null;

            if (ActiveTab.IsFtp)
            {
                var session = ActiveTab.Ftp!;
                items = await session.ListAsync(session.CurrentPath, token);
            }
            else if (ActiveTab.IsArchive)
            {
                var zip = ActiveTab.ArchivePath!;
                var inner = ActiveTab.ArchiveInternalPath;
                var archive = await Task.Run(() =>
                {
                    var list = ArchiveService.ListEntries(zip, inner, out var listError);
                    return (List: list, Error: listError);
                }, token);

                items = archive.List;
                error = archive.Error;
            }
            else if (ActiveTab.BranchView)
            {
                var path = ActiveTab.Path;
                items = await Task.Run(() => DirectoryService.ListBranch(path,
                    _settings.Current.ShowHiddenFiles, _settings.Current.ShowSystemFiles, token), token);
            }
            else
            {
                var listing = await DirectoryService.ListAsync(ActiveTab.Path,
                    _settings.Current.ShowHiddenFiles, _settings.Current.ShowSystemFiles, token);
                items = listing.Items;
                error = listing.Error;
            }

            if (token.IsCancellationRequested) return;

            DirectoryService.Sort(items, ActiveTab.Sort, _settings.Current.DirectoriesFirst);

            // Restore marks that survived the refresh.
            if (markedNames.Count > 0)
                foreach (var item in items)
                    if (!item.IsParent && markedNames.Contains(item.Name)) item.IsMarked = true;

            _allItems.Clear();
            _allItems.AddRange(items);
            ApplyFilter();

            ErrorText = error ?? string.Empty;
            RestoreCursor(remembered);
            UpdateFreeSpace();

            if (_settings.Current.ShowIcons) StartIconLoad(items);
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer load
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts)) IsLoading = false;
        }
    }

    private void RestoreCursor(string? name)
    {
        if (Items.Count == 0)
        {
            SelectedItem = null;
            return;
        }

        FileItem? target = null;
        if (!string.IsNullOrEmpty(name))
            target = Items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

        SelectedItem = target ?? Items[0];
    }

    private void ApplyFilter()
    {
        Items.Clear();

        if (_quickFilter.Length == 0)
        {
            foreach (var item in _allItems) Items.Add(item);
        }
        else
        {
            var pattern = _quickFilter.Contains('*') || _quickFilter.Contains('?')
                ? _quickFilter
                : "*" + _quickFilter + "*";

            Regex regex;
            try
            {
                regex = new Regex(SearchService.WildcardToRegex(pattern), RegexOptions.IgnoreCase);
            }
            catch (ArgumentException)
            {
                foreach (var item in _allItems) Items.Add(item);
                UpdateStatus();
                return;
            }

            foreach (var item in _allItems)
                if (item.IsParent || regex.IsMatch(item.Name)) Items.Add(item);
        }

        UpdateStatus();
    }

    // ------------------------------------------------------------------ icons

    private void StartIconLoad(List<FileItem> items)
    {
        _iconCts?.Cancel();
        var cts = new CancellationTokenSource();
        _iconCts = cts;
        var token = cts.Token;

        var snapshot = items.Where(i => !i.IsParent).ToList();

        _ = Task.Run(() =>
        {
            const int batchSize = 60;
            var batch = new List<(FileItem Item, System.Windows.Media.ImageSource? Icon)>(batchSize);

            foreach (var item in snapshot)
            {
                if (token.IsCancellationRequested) return;

                var icon = item.ArchiveEntryPath is not null
                    ? ShellServices.GetSmallIcon(item.Name, item.IsDirectory)
                    : ShellServices.GetSmallIcon(item.FullPath, item.IsDirectory);

                batch.Add((item, icon));

                if (batch.Count < batchSize) continue;
                Flush(batch, token);
                batch = new List<(FileItem, System.Windows.Media.ImageSource?)>(batchSize);
            }

            if (batch.Count > 0) Flush(batch, token);
        }, token);
    }

    private void Flush(List<(FileItem Item, System.Windows.Media.ImageSource? Icon)> batch, CancellationToken token)
    {
        // Icons are frozen, but the PropertyChanged that updates the binding has
        // to be raised on the UI thread.
        _dispatcher.InvokeAsync(() =>
        {
            if (token.IsCancellationRequested) return;
            foreach (var (item, icon) in batch) item.Icon = icon;
        }, DispatcherPriority.Background);
    }

    // ---------------------------------------------------------------- sorting

    public async Task SortByAsync(SortColumn column)
    {
        if (ActiveTab.Sort.Column == column)
            ActiveTab.Sort.Descending = !ActiveTab.Sort.Descending;
        else
        {
            ActiveTab.Sort.Column = column;
            ActiveTab.Sort.Descending = false;
        }

        OnPropertyChanged(nameof(Sort));

        var cursor = SelectedItem?.Name;
        DirectoryService.Sort(_allItems, ActiveTab.Sort, _settings.Current.DirectoriesFirst);
        ApplyFilter();
        RestoreCursor(cursor);

        await Task.CompletedTask;
    }

    // ---------------------------------------------------------------- marking

    /// <summary>Marked rows, or the cursor row when nothing is marked - the TC rule.</summary>
    public IReadOnlyList<FileItem> EffectiveSelection
    {
        get
        {
            var marked = Items.Where(i => i.IsMarked && !i.IsParent).ToList();
            if (marked.Count > 0) return marked;

            return SelectedItem is null || SelectedItem.IsParent
                ? Array.Empty<FileItem>()
                : new[] { SelectedItem };
        }
    }

    public IReadOnlyList<string> EffectivePaths => EffectiveSelection.Select(i => i.FullPath).ToList();

    public void ToggleMark(FileItem? item)
    {
        if (item is null || item.IsParent) return;
        item.IsMarked = !item.IsMarked;
        UpdateStatus();
    }

    public void MarkAll(bool marked)
    {
        foreach (var item in Items)
            if (!item.IsParent) item.IsMarked = marked;
        UpdateStatus();
    }

    public void InvertMarks()
    {
        foreach (var item in Items)
            if (!item.IsParent) item.IsMarked = !item.IsMarked;
        UpdateStatus();
    }

    public void MarkByMask(string mask, bool mark)
    {
        if (string.IsNullOrWhiteSpace(mask)) return;

        var masks = mask.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var regexes = new List<Regex>();

        foreach (var m in masks)
        {
            try { regexes.Add(new Regex(SearchService.WildcardToRegex(m), RegexOptions.IgnoreCase)); }
            catch (ArgumentException) { /* ignore a malformed mask */ }
        }
        if (regexes.Count == 0) return;

        foreach (var item in Items)
        {
            if (item.IsParent) continue;
            if (regexes.Any(r => r.IsMatch(item.Name))) item.IsMarked = mark;
        }
        UpdateStatus();
    }

    /// <summary>Marks the files that differ from the other pane (Compare directories).</summary>
    public void MarkDifferences(FilePaneViewModel other)
    {
        var theirs = other.Items
            .Where(i => !i.IsParent && !i.IsDirectory)
            .ToDictionary(i => i.Name, i => i, StringComparer.OrdinalIgnoreCase);

        foreach (var item in Items)
        {
            if (item.IsParent || item.IsDirectory) continue;

            item.IsMarked = !theirs.TryGetValue(item.Name, out var match)
                            || item.Size != match.Size
                            || item.Modified > match.Modified;
        }
        UpdateStatus();
    }

    // ------------------------------------------------------------ quick search

    /// <summary>Moves the cursor to the next row starting with <paramref name="prefix"/>.</summary>
    public bool QuickSearch(string prefix, bool forward = true)
    {
        if (Items.Count == 0 || prefix.Length == 0) return false;

        int start = SelectedItem is null ? 0 : Items.IndexOf(SelectedItem);
        int count = Items.Count;

        for (int step = 0; step < count; step++)
        {
            int index = forward
                ? (start + step) % count
                : (start - step + count * 2) % count;

            var item = Items[index];
            if (item.Name.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
            {
                SelectedItem = item;
                return true;
            }
        }
        return false;
    }

    /// <summary>Advances to the next match, used when the same letter is pressed repeatedly.</summary>
    public bool QuickSearchNext(string prefix)
    {
        if (Items.Count == 0 || prefix.Length == 0) return false;

        int start = SelectedItem is null ? -1 : Items.IndexOf(SelectedItem);
        for (int step = 1; step <= Items.Count; step++)
        {
            var item = Items[(start + step) % Items.Count];
            if (item.Name.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
            {
                SelectedItem = item;
                return true;
            }
        }
        return false;
    }

    // --------------------------------------------------------------- folder size

    public async Task CalculateSelectedFolderSizesAsync()
    {
        var folders = EffectiveSelection.Where(i => i.IsDirectory && !i.IsParent).ToList();
        if (folders.Count == 0) return;

        foreach (var folder in folders)
        {
            var path = folder.FullPath;
            var size = await Task.Run(() => DirectoryService.CalculateSize(path, CancellationToken.None));
            if (size >= 0) folder.Size = size;
        }

        UpdateStatus();
    }

    // ------------------------------------------------------------------ status

    public void UpdateStatus()
    {
        var files = _allItems.Where(i => !i.IsParent).ToList();
        var marked = files.Where(i => i.IsMarked).ToList();

        long markedBytes = marked.Where(i => i.Size > 0).Sum(i => i.Size);
        long totalBytes = files.Where(i => i.Size > 0).Sum(i => i.Size);

        StatusText = marked.Count > 0
            ? $"{FileItem.FormatBytesShort(markedBytes)} / {FileItem.FormatBytesShort(totalBytes)} in {marked.Count} of {files.Count} selected"
            : $"{files.Count(i => !i.IsDirectory)} files, {files.Count(i => i.IsDirectory)} folders, {FileItem.FormatBytesShort(totalBytes)}";

        OnPropertyChanged(nameof(EffectiveSelection));
    }

    private void UpdateFreeSpace()
    {
        if (ActiveTab.IsFtp)
        {
            FreeSpaceText = ActiveTab.Ftp!.Site.Summary;
            return;
        }

        if (ActiveTab.IsArchive)
        {
            FreeSpaceText = "archive";
            return;
        }

        var (free, total) = ShellServices.GetDriveSpace(CurrentPath);
        FreeSpaceText = total == 0
            ? string.Empty
            : $"{FileItem.FormatBytesShort((long)free)} free of {FileItem.FormatBytesShort((long)total)}";
    }

    public void RefreshDrives()
    {
        var drives = DriveService.GetDrives();
        Drives.Clear();
        foreach (var drive in drives) Drives.Add(drive);
    }

    public void RequestFocus() => FocusRequested?.Invoke(this, EventArgs.Empty);
}
