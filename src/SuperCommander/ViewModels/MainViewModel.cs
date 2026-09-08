using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using SuperCommander.Interop;
using SuperCommander.Models;
using SuperCommander.Services;
using SuperCommander.Services.Archives;
using SuperCommander.Services.Ftp;
using SuperCommander.Views;
using SuperCommander.Views.Dialogs;

namespace SuperCommander.ViewModels;

/// <summary>
/// Owns both panes and every command the UI exposes. All modal UI goes through
/// the themed dialogs in Views.Dialogs - the app never calls MessageBox.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly FileOperationService _operations = new();

    private Window? _window;
    private FilePaneViewModel _activePane;
    private string _commandLine = string.Empty;
    private string _title = "SuperCommander";

    public MainViewModel(SettingsService settings)
    {
        _settings = settings;

        LeftPane = new FilePaneViewModel("Left", settings);
        RightPane = new FilePaneViewModel("Right", settings);
        _activePane = LeftPane;

        LeftPane.InitialiseTabs(settings.Current.LeftPane);
        RightPane.InitialiseTabs(settings.Current.RightPane);

        LeftPane.PathChanged += (_, _) => UpdateTitle();
        RightPane.PathChanged += (_, _) => UpdateTitle();

        Bookmarks = new ObservableCollection<Bookmark>(settings.Current.Bookmarks);
        FtpSites = new ObservableCollection<FtpSite>(settings.Current.FtpSites);
        CommandHistory = new ObservableCollection<string>(settings.Current.CommandHistory);

        BuildCommands();
    }

    // ------------------------------------------------------------------ panes

    public FilePaneViewModel LeftPane { get; }

    public FilePaneViewModel RightPane { get; }

    public FilePaneViewModel ActivePane
    {
        get => _activePane;
        private set
        {
            if (!SetProperty(ref _activePane, value)) return;

            LeftPane.IsActive = ReferenceEquals(value, LeftPane);
            RightPane.IsActive = ReferenceEquals(value, RightPane);
            OnPropertyChanged(nameof(InactivePane));
            UpdateTitle();
        }
    }

    public FilePaneViewModel InactivePane => ReferenceEquals(ActivePane, LeftPane) ? RightPane : LeftPane;

    public ObservableCollection<Bookmark> Bookmarks { get; }

    public ObservableCollection<FtpSite> FtpSites { get; }

    public ObservableCollection<string> CommandHistory { get; }

    public AppSettings Settings => _settings.Current;

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string CommandLine
    {
        get => _commandLine;
        set => SetProperty(ref _commandLine, value);
    }

    private IntPtr WindowHandle => _window is null ? IntPtr.Zero : new WindowInteropHelper(_window).Handle;

    // ------------------------------------------------------------- lifecycle

    public void Attach(Window window)
    {
        _window = window;

        window.Loaded += async (_, _) =>
        {
            ShellServices.SetTitleBarTheme(window, ThemeService.IsDark);
            await LeftPane.ReloadAsync();
            await RightPane.ReloadAsync();
            SetActivePane(LeftPane);
            LeftPane.RequestFocus();
        };

        window.Closing += (_, _) => PersistState();

        ThemeService.ThemeChanged += (_, _) =>
        {
            if (_window is not null) ShellServices.SetTitleBarTheme(_window, ThemeService.IsDark);
        };
    }

    public void PersistState()
    {
        var settings = _settings.Current;
        settings.LeftPane = LeftPane.CaptureSettings();
        settings.RightPane = RightPane.CaptureSettings();
        settings.Theme = ThemeService.Current;
        settings.Bookmarks = Bookmarks.ToList();
        settings.FtpSites = FtpSites.ToList();
        settings.CommandHistory = CommandHistory.Take(50).ToList();

        if (_window is not null && _window.WindowState == WindowState.Normal)
        {
            settings.WindowLeft = _window.Left;
            settings.WindowTop = _window.Top;
            settings.WindowWidth = _window.Width;
            settings.WindowHeight = _window.Height;
        }
        settings.WindowMaximized = _window?.WindowState == WindowState.Maximized;

        _settings.Save();
    }

    public void SetActivePane(FilePaneViewModel pane) => ActivePane = pane;

    public void SwitchPane()
    {
        var target = InactivePane;
        SetActivePane(target);
        target.RequestFocus();
    }

    private void UpdateTitle()
    {
        var path = ActivePane.DisplayPath;
        Title = string.IsNullOrEmpty(path) ? "SuperCommander" : $"{path} - SuperCommander";
    }

    // --------------------------------------------------------------- commands

    public RelayCommand ViewCommand { get; private set; } = null!;
    public RelayCommand EditCommand { get; private set; } = null!;
    public RelayCommand CopyCommand { get; private set; } = null!;
    public RelayCommand MoveCommand { get; private set; } = null!;
    public RelayCommand RenameCommand { get; private set; } = null!;
    public RelayCommand MakeDirectoryCommand { get; private set; } = null!;
    public RelayCommand DeleteCommand { get; private set; } = null!;
    public RelayCommand PackCommand { get; private set; } = null!;
    public RelayCommand UnpackCommand { get; private set; } = null!;
    public RelayCommand SearchCommand { get; private set; } = null!;
    public RelayCommand MultiRenameCommand { get; private set; } = null!;
    public RelayCommand PropertiesCommand { get; private set; } = null!;
    public RelayCommand RefreshCommand { get; private set; } = null!;
    public RelayCommand SwapPanesCommand { get; private set; } = null!;
    public RelayCommand TargetEqualsSourceCommand { get; private set; } = null!;
    public RelayCommand CompareDirectoriesCommand { get; private set; } = null!;
    public RelayCommand CalculateSpaceCommand { get; private set; } = null!;
    public RelayCommand BranchViewCommand { get; private set; } = null!;
    public RelayCommand SelectAllCommand { get; private set; } = null!;
    public RelayCommand UnselectAllCommand { get; private set; } = null!;
    public RelayCommand InvertSelectionCommand { get; private set; } = null!;
    public RelayCommand SelectGroupCommand { get; private set; } = null!;
    public RelayCommand UnselectGroupCommand { get; private set; } = null!;
    public RelayCommand NewTabCommand { get; private set; } = null!;
    public RelayCommand CloseTabCommand { get; private set; } = null!;
    public RelayCommand ToggleHiddenCommand { get; private set; } = null!;
    public RelayCommand ToggleThemeCommand { get; private set; } = null!;
    public RelayCommand SetThemeCommand { get; private set; } = null!;
    public RelayCommand ClipboardCopyCommand { get; private set; } = null!;
    public RelayCommand ClipboardCutCommand { get; private set; } = null!;
    public RelayCommand ClipboardPasteCommand { get; private set; } = null!;
    public RelayCommand OpenTerminalCommand { get; private set; } = null!;
    public RelayCommand RevealInExplorerCommand { get; private set; } = null!;
    public RelayCommand AddBookmarkCommand { get; private set; } = null!;
    public RelayCommand GoToBookmarkCommand { get; private set; } = null!;
    public RelayCommand SettingsCommand { get; private set; } = null!;
    public RelayCommand AboutCommand { get; private set; } = null!;
    public RelayCommand ExitCommand { get; private set; } = null!;
    public RelayCommand NavigateCommand { get; private set; } = null!;
    public RelayCommand QuickFilterCommand { get; private set; } = null!;
    public RelayCommand CopyNameToCommandLineCommand { get; private set; } = null!;
    public RelayCommand FtpConnectCommand { get; private set; } = null!;
    public RelayCommand FtpDisconnectCommand { get; private set; } = null!;

    private void BuildCommands()
    {
        ViewCommand = new RelayCommand(() => _ = ViewAsync());
        EditCommand = new RelayCommand(Edit);
        CopyCommand = new RelayCommand(() => _ = TransferAsync(FileOperationKind.Copy));
        MoveCommand = new RelayCommand(() => _ = TransferAsync(FileOperationKind.Move));
        RenameCommand = new RelayCommand(() => _ = RenameAsync());
        MakeDirectoryCommand = new RelayCommand(() => _ = MakeDirectoryAsync());
        DeleteCommand = new RelayCommand(p => _ = DeleteAsync(p is bool b && b));
        PackCommand = new RelayCommand(() => _ = PackAsync());
        UnpackCommand = new RelayCommand(() => _ = UnpackAsync());
        SearchCommand = new RelayCommand(OpenSearch);
        MultiRenameCommand = new RelayCommand(OpenMultiRename);
        PropertiesCommand = new RelayCommand(ShowProperties);
        RefreshCommand = new RelayCommand(() => _ = RefreshBothAsync());
        SwapPanesCommand = new RelayCommand(() => _ = SwapPanesAsync());
        TargetEqualsSourceCommand = new RelayCommand(() => _ = TargetEqualsSourceAsync());
        CompareDirectoriesCommand = new RelayCommand(CompareDirectories);
        CalculateSpaceCommand = new RelayCommand(() => _ = ActivePane.CalculateSelectedFolderSizesAsync());
        BranchViewCommand = new RelayCommand(() => _ = ActivePane.ToggleBranchViewAsync());
        SelectAllCommand = new RelayCommand(() => ActivePane.MarkAll(true));
        UnselectAllCommand = new RelayCommand(() => ActivePane.MarkAll(false));
        InvertSelectionCommand = new RelayCommand(() => ActivePane.InvertMarks());
        SelectGroupCommand = new RelayCommand(() => MarkGroup(true));
        UnselectGroupCommand = new RelayCommand(() => MarkGroup(false));
        NewTabCommand = new RelayCommand(() => _ = ActivePane.NewTabAsync());
        CloseTabCommand = new RelayCommand(() => _ = ActivePane.CloseTabAsync());
        ToggleHiddenCommand = new RelayCommand(() => _ = ToggleHiddenAsync());
        ToggleThemeCommand = new RelayCommand(() => SetTheme(ThemeService.IsDark ? AppTheme.Light : AppTheme.Dark));
        SetThemeCommand = new RelayCommand(p =>
        {
            if (p is AppTheme theme) SetTheme(theme);
            else if (p is string s && Enum.TryParse<AppTheme>(s, true, out var parsed)) SetTheme(parsed);
        });
        ClipboardCopyCommand = new RelayCommand(() => ClipboardPut(cut: false));
        ClipboardCutCommand = new RelayCommand(() => ClipboardPut(cut: true));
        ClipboardPasteCommand = new RelayCommand(() => _ = ClipboardPasteAsync());
        OpenTerminalCommand = new RelayCommand(OpenTerminal);
        RevealInExplorerCommand = new RelayCommand(() => ShellServices.RevealInExplorer(
            ActivePane.SelectedItem?.FullPath ?? ActivePane.CurrentPath));
        AddBookmarkCommand = new RelayCommand(AddBookmark);
        GoToBookmarkCommand = new RelayCommand(p =>
        {
            if (p is Bookmark bookmark) _ = NavigateLocalAsync(ActivePane, bookmark.Path);
            else if (p is string path) _ = NavigateLocalAsync(ActivePane, path);
        });
        SettingsCommand = new RelayCommand(OpenSettings);
        AboutCommand = new RelayCommand(ShowAbout);
        ExitCommand = new RelayCommand(() => _window?.Close());
        NavigateCommand = new RelayCommand(p =>
        {
            if (p is string path) _ = NavigateLocalAsync(ActivePane, path);
        });
        QuickFilterCommand = new RelayCommand(() => _ = QuickFilterAsync());
        FtpConnectCommand = new RelayCommand(() => _ = FtpConnectAsync());
        FtpDisconnectCommand = new RelayCommand(() => _ = FtpDisconnectAsync());
        CopyNameToCommandLineCommand = new RelayCommand(p =>
        {
            var item = ActivePane.SelectedItem;
            if (item is null) return;
            var text = p is string s && s == "full" ? item.FullPath : item.Name;
            CommandLine = string.IsNullOrEmpty(CommandLine) ? text : $"{CommandLine} {text}";
        });
    }

    // ------------------------------------------------------------ view / edit

    public async Task ViewAsync(bool external = false)
    {
        var item = ActivePane.SelectedItem;
        if (item is null || item.IsParent) return;

        if (item.IsDirectory)
        {
            await ActivePane.EnterAsync(item);
            return;
        }

        var viewer = _settings.Current.ExternalViewer;
        if (external && !string.IsNullOrWhiteSpace(viewer))
        {
            ShellServices.RunCommand($"\"{viewer}\" \"{item.FullPath}\"", ActivePane.CurrentPath);
            return;
        }

        if (ActivePane.IsFtpMode)
        {
            var downloaded = await DownloadToTempAsync(ActivePane, item);
            if (downloaded is null) return;

            var temp = new FileItem(downloaded, item.Name, false, item.Size,
                item.Modified, item.Created, System.IO.FileAttributes.Normal);

            if (external && !string.IsNullOrWhiteSpace(viewer))
            {
                ShellServices.RunCommand($"\"{viewer}\" \"{downloaded}\"", Path.GetDirectoryName(downloaded)!);
                return;
            }

            var remoteViewer = new ViewerWindow { Owner = _window };
            remoteViewer.Load(temp);
            remoteViewer.Show();
            return;
        }

        byte[]? archiveBytes = null;
        if (ActivePane.IsArchiveMode && item.ArchiveEntryPath is not null)
        {
            archiveBytes = ArchiveService.ReadEntry(ActivePane.ActiveTab.ArchivePath!, item.ArchiveEntryPath);
            if (archiveBytes is null)
            {
                MessageDialog.ShowError(_window, "View", "The archive entry could not be read.");
                return;
            }
        }

        var window = new ViewerWindow { Owner = _window };
        window.Load(item, archiveBytes);
        window.Show();
    }

    private void Edit()
    {
        var item = ActivePane.SelectedItem;
        if (item is null || item.IsParent || item.IsDirectory || ActivePane.IsArchiveMode) return;

        var editor = _settings.Current.ExternalEditor;
        if (string.IsNullOrWhiteSpace(editor)) editor = "notepad.exe";

        ShellServices.RunCommand($"\"{editor}\" \"{item.FullPath}\"", ActivePane.CurrentPath);
    }

    // --------------------------------------------------------- copy and move

    private async Task TransferAsync(FileOperationKind kind, bool sameDirectory = false)
    {
        var source = ActivePane;
        var selection = source.EffectiveSelection;

        if (selection.Count == 0)
        {
            MessageDialog.ShowInfo(_window, kind.ToString(), "No files are selected.");
            return;
        }

        // Extracting out of an archive is a copy with a different engine.
        if (source.IsArchiveMode)
        {
            if (kind == FileOperationKind.Copy) await ExtractSelectionAsync(selection);
            else MessageDialog.ShowInfo(_window, "Move", "Files cannot be moved out of an archive.");
            return;
        }

        // Either side on FTP means an upload or a download, not a local copy.
        if (source.IsFtpMode || InactivePane.IsFtpMode)
        {
            await FtpTransferAsync(kind, source, InactivePane, selection);
            return;
        }

        var defaultTarget = sameDirectory ? source.CurrentPath : InactivePane.CurrentPath;
        var caption = selection.Count == 1
            ? $"{kind} \"{selection[0].Name}\" to:"
            : $"{kind} {selection.Count} items to:";

        var initial = sameDirectory && selection.Count == 1
            ? Path.Combine(defaultTarget, selection[0].Name)
            : defaultTarget;

        var answer = InputDialog.Show(_window, kind.ToString(), caption, initial,
            okText: kind.ToString(), selectFileNameOnly: sameDirectory);

        if (answer is null) return;

        var (targetDirectory, singleName) = ResolveTarget(answer, selection, source.CurrentPath);
        if (targetDirectory is null) return;

        await RunFileOperationAsync(kind, selection.Select(i => i.FullPath).ToList(),
            targetDirectory, singleName);
    }

    /// <summary>
    /// Works out whether the typed answer is a folder or a full target file name.
    /// </summary>
    private (string? Directory, string? SingleName) ResolveTarget(string answer,
        IReadOnlyList<FileItem> selection, string basePath)
    {
        answer = answer.Trim().Trim('"');
        if (answer.Length == 0) return (null, null);

        if (!Path.IsPathRooted(answer))
            answer = Path.Combine(basePath, answer);

        try
        {
            answer = Path.GetFullPath(answer);
        }
        catch (Exception ex)
        {
            MessageDialog.ShowError(_window, "Invalid target", ex.Message);
            return (null, null);
        }

        // A trailing separator, an existing folder, or several sources all mean
        // "this is the destination folder".
        bool looksLikeDirectory = answer.EndsWith(Path.DirectorySeparatorChar)
                                  || Directory.Exists(answer)
                                  || selection.Count > 1;

        if (looksLikeDirectory)
        {
            var directory = answer.TrimEnd(Path.DirectorySeparatorChar);
            if (directory.Length == 2 && directory[1] == ':') directory += Path.DirectorySeparatorChar;
            return (directory, null);
        }

        return (Path.GetDirectoryName(answer), Path.GetFileName(answer));
    }

    private async Task RunFileOperationAsync(FileOperationKind kind, IReadOnlyList<string> sources,
        string targetDirectory, string? singleName)
    {
        try
        {
            Directory.CreateDirectory(targetDirectory);
        }
        catch (Exception ex)
        {
            MessageDialog.ShowError(_window, "Target folder", ex.Message);
            return;
        }

        var dialog = new ProgressDialog
        {
            Owner = _window,
            Caption = kind == FileOperationKind.Copy ? "Copying files" : "Moving files"
        };

        using var cts = new CancellationTokenSource();
        dialog.Cancelled += (_, _) => cts.Cancel();

        var progress = new Progress<FileOperationProgress>(dialog.Update);
        FileOperationResult result;

        SetBusy(true);
        dialog.Show();
        try
        {
            result = await _operations.RunAsync(kind, sources, targetDirectory, progress,
                info => dialog.Dispatcher.Invoke(() => OverwriteDialog.Ask(dialog, info)),
                cts.Token, singleName);
        }
        finally
        {
            dialog.Close();
            SetBusy(false);
        }

        await RefreshBothAsync();
        ReportResult(kind.ToString(), result);
    }

    /// <summary>
    /// Shows the progress dialog and runs <paramref name="action"/> against it,
    /// handing it a progress sink and a conflict resolver marshalled to the UI.
    /// </summary>
    private async Task<FileOperationResult> RunProgressAsync(string caption,
        Func<IProgress<FileOperationProgress>, Func<ConflictInfo, OverwriteAction>, CancellationToken,
            Task<FileOperationResult>> action)
    {
        var dialog = new ProgressDialog { Owner = _window, Caption = caption };
        using var cts = new CancellationTokenSource();
        dialog.Cancelled += (_, _) => cts.Cancel();

        var progress = new Progress<FileOperationProgress>(dialog.Update);
        OverwriteAction Resolve(ConflictInfo info) =>
            dialog.Dispatcher.Invoke(() => OverwriteDialog.Ask(dialog, info));

        SetBusy(true);
        dialog.Show();
        try
        {
            return await action(progress, Resolve, cts.Token);
        }
        finally
        {
            dialog.Close();
            SetBusy(false);
        }
    }

    // -------------------------------------------------------------------- FTP

    private async Task FtpConnectAsync()
    {
        var window = new FtpConnectWindow(FtpSites.ToList()) { Owner = _window };
        bool connect = window.ShowDialog() == true;

        // The dialog edits sites in place, so take its list back either way.
        FtpSites.Clear();
        foreach (var site in window.Sites) FtpSites.Add(site);
        _settings.Current.FtpSites = FtpSites.ToList();
        _settings.Save();

        if (!connect || window.SelectedSite is null) return;

        var target = ActivePane;
        Exception? failure = null;

        SetBusy(true);
        try
        {
            await target.ConnectFtpAsync(window.SelectedSite);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            SetBusy(false);
        }

        // Report only once the window is interactive again - a modal owned by a
        // disabled window cannot be dismissed.
        if (failure is not null)
        {
            LogFtp($"connect to {window.SelectedSite.Host}:{window.SelectedSite.Port} failed", failure);
            MessageDialog.ShowError(_window, "FTP",
                $"Could not connect to {window.SelectedSite.Host}.", failure.ToString());
            return;
        }

        UpdateTitle();
        target.RequestFocus();
    }

    /// <summary>Appends an FTP failure to the error log with the protocol trace.</summary>
    private void LogFtp(string what, Exception ex)
    {
        try
        {
            var directory = _settings.DirectoryPath;
            Directory.CreateDirectory(directory);

            var trace = ActivePane.Ftp is { } session
                ? string.Join(Environment.NewLine, session.Client.Log.TakeLast(40))
                : ActivePane.LastFtpLog;

            File.AppendAllText(Path.Combine(directory, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] FTP {what}: {ex}{Environment.NewLine}" +
                $"--- protocol ---{Environment.NewLine}{trace}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Diagnostics must never mask the original failure.
        }
    }

    /// <summary>
    /// Opens a local folder in a panel, offering to close an FTP session first
    /// rather than silently doing nothing (drive buttons, bookmarks, path bar).
    /// </summary>
    public async Task NavigateLocalAsync(FilePaneViewModel pane, string path)
    {
        if (pane.IsFtpMode)
        {
            var host = pane.Ftp!.Site.Display;

            if (!MessageDialog.ShowConfirm(_window, "Disconnect from server",
                    $"This panel is connected to {host}.\n\nDisconnect and open {path}?",
                    okText: "Disconnect", cancelText: "Stay connected"))
                return;

            await pane.DisconnectFtpAsync(reload: false);
        }

        SetActivePane(pane);
        await pane.NavigateAsync(path);
        UpdateTitle();
    }

    /// <summary>
    /// Path bar entry. A remote path stays on the server; anything that looks
    /// like a local path goes through the disconnect prompt.
    /// </summary>
    public async Task NavigateFromPathBarAsync(FilePaneViewModel pane, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;

        if (pane.IsFtpMode && !LooksLocal(text))
        {
            await pane.NavigateFtpAsync(text);
            return;
        }

        await NavigateLocalAsync(pane, text);
    }

    /// <summary>True for "C:\...", "\\server\share" and "ftp://" is explicitly not.</summary>
    private static bool LooksLocal(string path)
    {
        if (path.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        return path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':';
    }

    private async Task FtpDisconnectAsync()
    {
        if (!ActivePane.IsFtpMode)
        {
            MessageDialog.ShowInfo(_window, "FTP", "This panel is not connected to a server.");
            return;
        }

        await ActivePane.DisconnectFtpAsync();
        UpdateTitle();
        ActivePane.RequestFocus();
    }

    /// <summary>Uploads or downloads depending on which pane holds the session.</summary>
    private async Task FtpTransferAsync(FileOperationKind kind, FilePaneViewModel source,
        FilePaneViewModel target, IReadOnlyList<FileItem> selection)
    {
        if (source.IsFtpMode && target.IsFtpMode)
        {
            MessageDialog.ShowInfo(_window, "FTP",
                "Copying directly between two servers is not supported. Download to a local folder first.");
            return;
        }

        bool move = kind == FileOperationKind.Move;

        if (source.IsFtpMode)
        {
            var suggestion = target.IsFtpMode ? source.ActiveTab.Path : target.CurrentPath;
            var answer = InputDialog.Show(_window, move ? "Move from server" : "Download",
                $"{(move ? "Move" : "Download")} {selection.Count} item(s) to:", suggestion,
                okText: move ? "Move" : "Download");
            if (string.IsNullOrWhiteSpace(answer)) return;

            var result = await DownloadFromServerAsync(source, selection, answer, move);

            await source.ReloadAsync();
            await target.ReloadAsync();
            ReportResult(move ? "Move" : "Download", result);
            return;
        }

        // Local -> server
        var remoteAnswer = InputDialog.Show(_window, move ? "Move to server" : "Upload",
            $"{(move ? "Move" : "Upload")} {selection.Count} item(s) to:", target.Ftp!.CurrentPath,
            okText: move ? "Move" : "Upload");
        if (string.IsNullOrWhiteSpace(remoteAnswer)) return;

        var uploadResult = await UploadToServerAsync(target,
            selection.Select(i => i.FullPath).ToList(), remoteAnswer, move);

        await source.ReloadAsync();
        await target.ReloadAsync();
        ReportResult(move ? "Move" : "Upload", uploadResult);
    }

    /// <summary>Pulls remote rows into a local folder, removing them after a move.</summary>
    private async Task<FileOperationResult> DownloadFromServerAsync(FilePaneViewModel source,
        IReadOnlyList<FileItem> items, string destination, bool move)
    {
        try
        {
            Directory.CreateDirectory(destination);
        }
        catch (Exception ex)
        {
            MessageDialog.ShowError(_window, "Download", ex.Message);
            return new FileOperationResult { Failed = 1 };
        }

        var session = source.Ftp!;
        var result = await RunProgressAsync(move ? "Moving from server" : "Downloading",
            (progress, resolve, token) =>
                FtpTransferService.DownloadAsync(session, items, destination, progress, resolve, token));

        if (move && result.Failed == 0 && !result.Cancelled)
            foreach (var error in await session.DeleteAsync(items, CancellationToken.None))
                result.Errors.Add(error);

        return result;
    }

    /// <summary>Pushes local paths to the server, removing them after a move.</summary>
    private async Task<FileOperationResult> UploadToServerAsync(FilePaneViewModel target,
        IReadOnlyList<string> paths, string destination, bool move)
    {
        var session = target.Ftp!;
        var result = await RunProgressAsync(move ? "Moving to server" : "Uploading",
            (progress, resolve, token) =>
                FtpTransferService.UploadAsync(session, paths, FtpPath.Normalise(destination),
                    progress, resolve, token));

        if (move && result.Failed == 0 && !result.Cancelled)
        {
            var removed = await FileOperationService.DeleteAsync(WindowHandle, paths,
                permanent: false, confirm: false);

            if (!removed.Succeeded && removed.Error is not null) result.Errors.Add(removed.Error);
        }

        return result;
    }

    /// <summary>
    /// Pulls a remote file into the temp folder so the viewer or the shell can
    /// open it. Returns null when the download failed.
    /// </summary>
    private async Task<string?> DownloadToTempAsync(FilePaneViewModel pane, FileItem item)
    {
        if (pane.Ftp is null || item.RemotePath is null) return null;

        var folder = Path.Combine(Path.GetTempPath(), "SuperCommander", "ftp");
        Directory.CreateDirectory(folder);
        var local = Path.Combine(folder, item.Name);

        var session = pane.Ftp;
        var result = await RunProgressAsync($"Downloading {item.Name}",
            (progress, _, token) =>
                FtpTransferService.DownloadAsync(session, new[] { item }, folder, progress,
                    _ => OverwriteAction.Overwrite, token));

        if (result.Succeeded == 0 || !File.Exists(local))
        {
            ReportResult("Download", result);
            return null;
        }

        return local;
    }

    private async Task ExtractSelectionAsync(IReadOnlyList<FileItem> selection)
    {
        var zip = ActivePane.ActiveTab.ArchivePath!;
        var target = InactivePane.CurrentPath;

        var answer = InputDialog.Show(_window, "Extract",
            $"Extract {selection.Count} item(s) from \"{Path.GetFileName(zip)}\" to:", target, okText: "Extract");
        if (answer is null) return;

        var entries = selection
            .Select(i => i.ArchiveEntryPath)
            .Where(e => !string.IsNullOrEmpty(e))
            .Select(e => e!)
            .ToList();

        await RunArchiveAsync("Extracting", run =>
            ArchiveService.UnpackAsync(zip, answer, entries, run.Progress, run.Token));
    }

    private void ReportResult(string title, FileOperationResult result)
    {
        if (result.Cancelled)
        {
            MessageDialog.ShowInfo(_window, title,
                $"Cancelled. {result.Succeeded} item(s) finished, {result.Skipped} skipped.");
            return;
        }

        if (result.Failed > 0)
        {
            MessageDialog.ShowError(_window, title,
                $"{result.Succeeded} succeeded, {result.Skipped} skipped, {result.Failed} failed.",
                string.Join(Environment.NewLine, result.Errors.Take(50)));
        }
    }

    // ------------------------------------------------------------- rename etc.

    private async Task RenameAsync()
    {
        var item = ActivePane.SelectedItem;
        if (item is null || item.IsParent || ActivePane.IsArchiveMode) return;

        if (ActivePane.IsFtpMode)
        {
            if (item.RemotePath is null) return;

            var remoteName = InputDialog.Show(_window, "Rename", "New name:", item.Name,
                okText: "Rename", selectFileNameOnly: true);
            if (string.IsNullOrWhiteSpace(remoteName) || remoteName == item.Name) return;

            try
            {
                await ActivePane.Ftp!.RenameAsync(item.RemotePath, remoteName.Trim());
                await ActivePane.ReloadAsync();
            }
            catch (Exception ex)
            {
                MessageDialog.ShowError(_window, "Rename", ex.Message);
            }
            return;
        }

        var answer = InputDialog.Show(_window, "Rename", "New name:", item.Name,
            okText: "Rename", selectFileNameOnly: true);
        if (answer is null || answer == item.Name) return;

        try
        {
            var target = Path.Combine(Path.GetDirectoryName(item.FullPath)!, answer);
            if (item.IsDirectory) Directory.Move(item.FullPath, target);
            else File.Move(item.FullPath, target);

            await ActivePane.ReloadAsync();
        }
        catch (Exception ex)
        {
            MessageDialog.ShowError(_window, "Rename", ex.Message);
        }
    }

    private async Task MakeDirectoryAsync()
    {
        if (ActivePane.IsFtpMode)
        {
            var remoteName = InputDialog.Show(_window, "New folder", "Create folder on the server:",
                string.Empty, okText: "Create");
            if (string.IsNullOrWhiteSpace(remoteName)) return;

            try
            {
                await ActivePane.Ftp!.CreateDirectoryAsync(remoteName.Trim());
                await ActivePane.ReloadAsync();
            }
            catch (Exception ex)
            {
                MessageDialog.ShowError(_window, "New folder", ex.Message);
            }
            return;
        }

        if (ActivePane.IsArchiveMode)
        {
            MessageDialog.ShowInfo(_window, "New folder", "Folders cannot be created inside an archive.");
            return;
        }

        var answer = InputDialog.Show(_window, "New folder", "Create folder:", string.Empty, okText: "Create");
        if (string.IsNullOrWhiteSpace(answer)) return;

        try
        {
            var target = Path.IsPathRooted(answer)
                ? answer
                : Path.Combine(ActivePane.CurrentPath, answer);

            Directory.CreateDirectory(target);
            await ActivePane.ReloadAsync();
            await ActivePane.NavigateAsync(ActivePane.CurrentPath, Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar)), recordHistory: false);
        }
        catch (Exception ex)
        {
            MessageDialog.ShowError(_window, "New folder", ex.Message);
        }
    }

    private async Task DeleteAsync(bool permanent)
    {
        if (ActivePane.IsFtpMode)
        {
            var remoteSelection = ActivePane.EffectiveSelection;
            if (remoteSelection.Count == 0) return;

            var what = remoteSelection.Count == 1
                ? $"\"{remoteSelection[0].Name}\""
                : $"{remoteSelection.Count} selected items";

            // There is no recycle bin on a server.
            if (!MessageDialog.ShowConfirm(_window, "Delete",
                    $"Permanently delete {what} from {ActivePane.Ftp!.Site.Host}? This cannot be undone.",
                    okText: "Delete", danger: true))
                return;

            SetBusy(true);
            List<string> errors;
            try
            {
                errors = await ActivePane.Ftp.DeleteAsync(remoteSelection);
            }
            finally
            {
                SetBusy(false);
            }

            await ActivePane.ReloadAsync();

            if (errors.Count > 0)
                MessageDialog.ShowError(_window, "Delete", $"{errors.Count} item(s) could not be deleted.",
                    string.Join(Environment.NewLine, errors.Take(50)));
            return;
        }

        if (ActivePane.IsArchiveMode)
        {
            MessageDialog.ShowInfo(_window, "Delete", "Deleting inside an archive is not supported.");
            return;
        }

        var selection = ActivePane.EffectiveSelection;
        if (selection.Count == 0) return;

        var settings = _settings.Current;
        bool toRecycleBin = settings.UseRecycleBin && !permanent;

        if (settings.ConfirmDelete)
        {
            var what = selection.Count == 1
                ? $"\"{selection[0].Name}\""
                : $"{selection.Count} selected items";

            var message = toRecycleBin
                ? $"Move {what} to the Recycle Bin?"
                : $"Permanently delete {what}? This cannot be undone.";

            if (!MessageDialog.ShowConfirm(_window, "Delete", message,
                    okText: toRecycleBin ? "Move to Recycle Bin" : "Delete permanently",
                    danger: !toRecycleBin))
                return;
        }

        var paths = selection.Select(i => i.FullPath).ToList();
        DeleteOutcome outcome;

        SetBusy(true);
        try
        {
            outcome = await FileOperationService.DeleteAsync(WindowHandle, paths, !toRecycleBin, confirm: false);
        }
        finally
        {
            SetBusy(false);
        }

        await ActivePane.ReloadAsync();
        await InactivePane.ReloadAsync();

        if (!outcome.Succeeded)
            MessageDialog.ShowError(_window, "Delete", "The items could not be deleted.", outcome.Error);
    }

    // -------------------------------------------------------------- archives

    private sealed class ArchiveRun
    {
        public required IProgress<ArchiveProgress> Progress { get; init; }
        public required CancellationToken Token { get; init; }
    }

    private async Task RunArchiveAsync(string caption, Func<ArchiveRun, Task<FileOperationResult>> action)
    {
        var dialog = new ProgressDialog { Owner = _window, Caption = caption };
        using var cts = new CancellationTokenSource();
        dialog.Cancelled += (_, _) => cts.Cancel();

        var progress = new Progress<ArchiveProgress>(p => dialog.Update(p));

        SetBusy(true);
        dialog.Show();

        FileOperationResult result;
        try
        {
            result = await action(new ArchiveRun { Progress = progress, Token = cts.Token });
        }
        finally
        {
            dialog.Close();
            SetBusy(false);
        }

        await RefreshBothAsync();
        ReportResult(caption, result);
    }

    private async Task PackAsync()
    {
        var selection = ActivePane.EffectiveSelection;
        if (selection.Count == 0 || ActivePane.IsArchiveMode) return;

        var defaultName = selection.Count == 1
            ? Path.GetFileNameWithoutExtension(selection[0].Name) + ".zip"
            : Path.GetFileName(ActivePane.CurrentPath.TrimEnd(Path.DirectorySeparatorChar)) + ".zip";

        var suggestion = Path.Combine(InactivePane.IsArchiveMode ? ActivePane.CurrentPath : InactivePane.CurrentPath, defaultName);

        var answer = InputDialog.Show(_window, "Pack files",
            $"Pack {selection.Count} item(s) into:", suggestion, okText: "Pack", selectFileNameOnly: true,
            hint: $"Change the extension to pick the format: {ArchiveService.CreatableExtensions}");
        if (string.IsNullOrWhiteSpace(answer)) return;

        // Whatever format the name asks for is honoured. Only a name with no
        // archive extension at all falls back to .zip; a recognised but
        // read-only format such as .rar is left for PackAsync to explain,
        // rather than quietly producing a .rar.zip.
        if (!ArchiveService.IsSupported(answer)) answer += ".zip";

        var sources = selection.Select(i => i.FullPath).ToList();
        var baseDirectory = ActivePane.CurrentPath;

        await RunArchiveAsync("Packing files", run =>
            ArchiveService.PackAsync(sources, answer, baseDirectory, run.Progress, run.Token));
    }

    private async Task UnpackAsync()
    {
        string? archive = null;

        if (ActivePane.IsArchiveMode) archive = ActivePane.ActiveTab.ArchivePath;
        else
        {
            var item = ActivePane.SelectedItem;
            if (item is not null && !item.IsDirectory && ArchiveService.IsSupported(item.FullPath))
                archive = item.FullPath;
        }

        if (archive is null)
        {
            MessageDialog.ShowInfo(_window, "Unpack", "Select an archive first.");
            return;
        }

        var suggestion = InactivePane.IsArchiveMode ? ActivePane.CurrentPath : InactivePane.CurrentPath;
        var answer = InputDialog.Show(_window, "Unpack",
            $"Extract \"{Path.GetFileName(archive)}\" to:", suggestion, okText: "Extract");
        if (string.IsNullOrWhiteSpace(answer)) return;

        var target = answer;
        var source = archive;
        await RunArchiveAsync("Extracting", run =>
            ArchiveService.UnpackAsync(source, target, null, run.Progress, run.Token));
    }

    // -------------------------------------------------------------- utilities

    public async Task RefreshBothAsync()
    {
        await LeftPane.ReloadAsync();
        await RightPane.ReloadAsync();
        LeftPane.RefreshDrives();
        RightPane.RefreshDrives();
    }

    private async Task SwapPanesAsync()
    {
        var left = LeftPane.CurrentPath;
        var right = RightPane.CurrentPath;

        await LeftPane.NavigateAsync(right);
        await RightPane.NavigateAsync(left);
    }

    private async Task TargetEqualsSourceAsync() => await InactivePane.NavigateAsync(ActivePane.CurrentPath);

    private void CompareDirectories()
    {
        ActivePane.MarkDifferences(InactivePane);
        InactivePane.MarkDifferences(ActivePane);
    }

    private async Task ToggleHiddenAsync()
    {
        _settings.Current.ShowHiddenFiles = !_settings.Current.ShowHiddenFiles;
        _settings.Current.ShowSystemFiles = _settings.Current.ShowHiddenFiles;
        OnPropertyChanged(nameof(Settings));
        await RefreshBothAsync();
    }

    private void MarkGroup(bool mark)
    {
        var title = mark ? "Select files" : "Unselect files";
        var answer = InputDialog.Show(_window, title, "File mask (e.g. *.txt;*.log):", "*.*",
            okText: mark ? "Select" : "Unselect");
        if (string.IsNullOrWhiteSpace(answer)) return;

        ActivePane.MarkByMask(answer, mark);
    }

    private async Task QuickFilterAsync()
    {
        var answer = InputDialog.Show(_window, "Quick filter",
            "Show only files matching (empty clears):", ActivePane.QuickFilter, okText: "Filter");
        if (answer is null) return;

        ActivePane.QuickFilter = answer;
        await Task.CompletedTask;
    }

    private void SetTheme(AppTheme theme)
    {
        ThemeService.Apply(theme);
        _settings.Current.Theme = theme;
        OnPropertyChanged(nameof(Settings));
    }

    private void ClipboardPut(bool cut)
    {
        var paths = ActivePane.EffectivePaths;
        if (paths.Count == 0 || ActivePane.IsArchiveMode) return;
        ShellServices.CopyToClipboard(paths, cut);
    }

    private async Task ClipboardPasteAsync()
    {
        if (ActivePane.IsArchiveMode) return;

        var files = ShellServices.GetClipboardFiles(out bool cut);
        if (files.Count == 0) return;

        await RunFileOperationAsync(cut ? FileOperationKind.Move : FileOperationKind.Copy,
            files, ActivePane.CurrentPath, null);
    }

    /// <summary>
    /// Drop that started in one of our own panels. Routes to a download, an
    /// upload or a local copy depending on which side holds an FTP session.
    /// <paramref name="targetDirectory"/> is set when the drop landed on a folder row.
    /// </summary>
    public async Task DropFromPaneAsync(FilePaneViewModel source, FilePaneViewModel target,
        IReadOnlyList<FileItem> items, bool move, string? targetDirectory = null)
    {
        if (items.Count == 0 || target.IsArchiveMode || ReferenceEquals(source, target)) return;

        if (source.IsFtpMode && target.IsFtpMode)
        {
            MessageDialog.ShowInfo(_window, "FTP",
                "Copying directly between two servers is not supported. Download to a local folder first.");
            return;
        }

        SetActivePane(target);

        FileOperationResult result;
        string caption;

        if (source.IsFtpMode)
        {
            caption = move ? "Move" : "Download";
            result = await DownloadFromServerAsync(source, items,
                targetDirectory ?? target.CurrentPath, move);
        }
        else if (target.IsFtpMode)
        {
            caption = move ? "Move" : "Upload";
            result = await UploadToServerAsync(target, items.Select(i => i.FullPath).ToList(),
                targetDirectory ?? target.Ftp!.CurrentPath, move);
        }
        else
        {
            await DropExternalAsync(target, items.Select(i => i.FullPath).ToList(), move, targetDirectory);
            return;
        }

        await source.ReloadAsync();
        await target.ReloadAsync();
        ReportResult(caption, result);
    }

    /// <summary>
    /// Local paths dropped in - from Explorer, or from the other panel when both
    /// are local. Uploads when the target holds a session.
    /// </summary>
    public async Task DropExternalAsync(FilePaneViewModel target, IReadOnlyList<string> paths,
        bool move, string? targetDirectory = null)
    {
        if (paths.Count == 0 || target.IsArchiveMode) return;

        if (target.IsFtpMode)
        {
            SetActivePane(target);
            var uploaded = await UploadToServerAsync(target, paths,
                targetDirectory ?? target.Ftp!.CurrentPath, move);

            await target.ReloadAsync();
            ReportResult(move ? "Move" : "Upload", uploaded);
            return;
        }

        var destination = targetDirectory ?? target.CurrentPath;

        // Ignore a drop back onto the folder the files already live in.
        var first = Path.GetDirectoryName(paths[0]);
        if (string.Equals(first, destination, StringComparison.OrdinalIgnoreCase)) return;

        // And never drop a folder into itself.
        foreach (var path in paths)
        {
            if (destination.StartsWith(path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(destination, path, StringComparison.OrdinalIgnoreCase))
            {
                MessageDialog.ShowError(_window, "Drop",
                    "A folder cannot be copied or moved into itself.");
                return;
            }
        }

        SetActivePane(target);
        await RunFileOperationAsync(move ? FileOperationKind.Move : FileOperationKind.Copy,
            paths, destination, null);
    }

    /// <summary>Enter on a remote file: fetch it to temp, then hand it to the shell.</summary>
    public async Task OpenRemoteAsync(FilePaneViewModel pane, FileItem item)
    {
        var downloaded = await DownloadToTempAsync(pane, item);
        if (downloaded is null) return;

        if (!ShellServices.Open(downloaded, Path.GetDirectoryName(downloaded)))
            MessageDialog.ShowError(_window, "Open", $"Nothing is registered to open \"{item.Name}\".");
    }

    private void OpenTerminal() => ShellServices.RunCommand("", ActivePane.CurrentPath, keepOpen: true);

    private void ShowProperties()
    {
        var item = ActivePane.SelectedItem;
        var path = item is null || item.IsParent ? ActivePane.CurrentPath : item.FullPath;
        if (!ShellServices.ShowProperties(WindowHandle, path))
            MessageDialog.ShowError(_window, "Properties", "The property sheet could not be opened.");
    }

    private void AddBookmark()
    {
        var path = ActivePane.CurrentPath;
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) name = path;

        var answer = InputDialog.Show(_window, "Add bookmark", "Bookmark name:", name, okText: "Add");
        if (string.IsNullOrWhiteSpace(answer)) return;

        Bookmarks.Add(new Bookmark { Name = answer, Path = path });
        _settings.Current.Bookmarks = Bookmarks.ToList();
    }

    private void OpenSearch()
    {
        var window = new SearchWindow(ActivePane.CurrentPath) { Owner = _window };
        window.NavigateRequested += (_, path) => _ = ActivePane.NavigateAsync(
            Path.GetDirectoryName(path) ?? path, Path.GetFileName(path));
        window.Show();
    }

    private void OpenMultiRename()
    {
        var selection = ActivePane.EffectiveSelection.Where(i => !i.IsParent).ToList();
        if (selection.Count == 0)
        {
            MessageDialog.ShowInfo(_window, "Multi-rename", "Select the files you want to rename first.");
            return;
        }

        var window = new MultiRenameWindow(selection) { Owner = _window };
        window.ShowDialog();
        if (window.Applied) _ = ActivePane.ReloadAsync();
    }

    private void OpenSettings()
    {
        var window = new SettingsWindow(_settings) { Owner = _window };
        if (window.ShowDialog() == true) _ = RefreshBothAsync();
    }

    private void ShowAbout() => AboutWindow.ShowAbout(_window);

    // ---------------------------------------------------------- command line

    public async Task ExecuteCommandLineAsync()
    {
        var text = CommandLine.Trim();
        if (text.Length == 0) return;

        CommandHistory.Insert(0, text);
        while (CommandHistory.Count > 50) CommandHistory.RemoveAt(CommandHistory.Count - 1);

        CommandLine = string.Empty;

        // "cd <path>" navigates the active pane instead of spawning a shell.
        if (text.StartsWith("cd ", StringComparison.OrdinalIgnoreCase))
        {
            var target = text[3..].Trim().Trim('"');
            if (target.Length == 2 && target[1] == ':') target += Path.DirectorySeparatorChar;
            if (!Path.IsPathRooted(target)) target = Path.Combine(ActivePane.CurrentPath, target);
            await ActivePane.NavigateAsync(target);
            return;
        }

        // A bare drive letter switches drive.
        if (text.Length == 2 && text[1] == ':')
        {
            await ActivePane.NavigateAsync(text + Path.DirectorySeparatorChar);
            return;
        }

        ShellServices.RunCommand(text, ActivePane.CurrentPath, keepOpen: true);
    }

    private void SetBusy(bool busy)
    {
        if (_window is not null) _window.IsEnabled = !busy;
    }
}
