using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SuperCommander.Services;
using SuperCommander.ViewModels;
using SuperCommander.Views.Dialogs;

namespace SuperCommander.Views;

/// <summary>
/// Window shell: menus, layout persistence and the global keyboard model.
/// Per-row behaviour lives in <see cref="FilePaneView"/>.
/// </summary>
public partial class MainWindow : ThemedWindow
{
    private ModifierKeys _lastModifiers = ModifierKeys.None;

    public MainWindow()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        PreviewKeyUp += (_, _) => UpdateFunctionKeyBar();
        Activated += (_, _) => UpdateFunctionKeyBar();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private FilePaneView ActivePaneView =>
        ReferenceEquals(Vm.ActivePane, Vm.LeftPane) ? LeftPaneView : RightPaneView;

    // ------------------------------------------------------------- lifecycle

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestorePlacement();
        ApplyChromeVisibility();
        UpdateFunctionKeyBar();
    }

    private void RestorePlacement()
    {
        var settings = Vm.Settings;

        if (settings.WindowWidth > 400 && settings.WindowHeight > 300)
        {
            Width = settings.WindowWidth;
            Height = settings.WindowHeight;
        }

        if (settings.WindowLeft is { } left && settings.WindowTop is { } top)
        {
            // Only restore a position that is still on a connected screen.
            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
            var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

            if (left > virtualLeft - 50 && left < virtualRight - 100 &&
                top > virtualTop - 50 && top < virtualBottom - 100)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }

        if (settings.WindowMaximized) WindowState = WindowState.Maximized;

        var ratio = Math.Clamp(settings.SplitterRatio, 0.15, 0.85);
        LeftColumn.Width = new GridLength(ratio, GridUnitType.Star);
        RightColumn.Width = new GridLength(1 - ratio, GridUnitType.Star);
    }

    public void ApplyChromeVisibility()
    {
        var settings = Vm.Settings;
        CommandLineHost.Visibility = settings.ShowCommandLine ? Visibility.Visible : Visibility.Collapsed;
        FunctionBarHost.Visibility = settings.ShowFunctionKeyBar ? Visibility.Visible : Visibility.Collapsed;
        LeftPaneView.ApplyChromeVisibility();
        RightPaneView.ApplyChromeVisibility();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Capture the splitter position before the panes are torn down.
        var total = LeftColumn.Width.Value + RightColumn.Width.Value;
        if (total > 0) Vm.Settings.SplitterRatio = LeftColumn.Width.Value / total;

        base.OnClosing(e);
    }

    // -------------------------------------------------------------- keyboard

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        UpdateFunctionKeyBar();

        var modifiers = Keyboard.Modifiers;
        bool typing = IsTextEntryFocused();

        // Function keys and Alt combinations always belong to the app; plain and
        // Ctrl keys are left alone while a text box has focus.
        if (typing && !IsFunctionKey(e.Key) && (modifiers & ModifierKeys.Alt) == 0)
            return;

        if (Handle(e.Key, modifiers, typing)) e.Handled = true;
    }

    private static bool IsFunctionKey(Key key) => key is >= Key.F1 and <= Key.F12;

    private bool IsTextEntryFocused() => Keyboard.FocusedElement is TextBox;

    private bool Handle(Key key, ModifierKeys modifiers, bool typing)
    {
        bool shift = (modifiers & ModifierKeys.Shift) != 0;
        bool ctrl = (modifiers & ModifierKeys.Control) != 0;
        bool alt = (modifiers & ModifierKeys.Alt) != 0;

        switch (key)
        {
            // ------------------------------------------------ function keys
            case Key.F1 when !ctrl && !alt:
                Vm.AboutCommand.Execute(null);
                return true;

            case Key.F2 when !ctrl && !alt:
                Vm.RefreshCommand.Execute(null);
                return true;

            case Key.F3:
                _ = Vm.ViewAsync(external: alt);
                return true;

            case Key.F4 when !alt:
                Vm.EditCommand.Execute(null);
                return true;

            case Key.F5 when alt:
                Vm.PackCommand.Execute(null);
                return true;

            case Key.F5:
                Vm.CopyCommand.Execute(null);
                return true;

            case Key.F6 when shift:
                Vm.RenameCommand.Execute(null);
                return true;

            case Key.F6:
                Vm.MoveCommand.Execute(null);
                return true;

            case Key.F7 when alt:
                Vm.SearchCommand.Execute(null);
                return true;

            case Key.F7:
                Vm.MakeDirectoryCommand.Execute(null);
                return true;

            case Key.F8 when shift:
                Vm.DeleteCommand.Execute(true);
                return true;

            case Key.F8:
                Vm.DeleteCommand.Execute(false);
                return true;

            case Key.F9 when alt:
                Vm.UnpackCommand.Execute(null);
                return true;

            // ---------------------------------------------------- deleting
            case Key.Delete when !typing && shift:
                Vm.DeleteCommand.Execute(true);
                return true;

            case Key.Delete when !typing:
                Vm.DeleteCommand.Execute(false);
                return true;

            // ------------------------------------------------ pane switching
            case Key.Tab when !ctrl && !alt && !typing:
                Vm.SwitchPane();
                return true;

            case Key.Tab when ctrl:
                _ = Vm.ActivePane.NextTabAsync(shift ? -1 : 1);
                return true;

            // -------------------------------------------------- navigation
            case Key.PageUp when ctrl:
                _ = Vm.ActivePane.GoUpAsync();
                return true;

            case Key.PageDown when ctrl:
                if (Vm.ActivePane.SelectedItem is { } item) _ = Vm.ActivePane.EnterAsync(item);
                return true;

            case Key.Left when alt:
                _ = Vm.ActivePane.GoBackAsync();
                return true;

            case Key.Right when alt:
                _ = Vm.ActivePane.GoForwardAsync();
                return true;

            case Key.Left when ctrl:
            case Key.Right when ctrl:
                Vm.TargetEqualsSourceCommand.Execute(null);
                return true;

            case Key.OemBackslash when ctrl:
            case Key.Oem5 when ctrl:
                _ = Vm.ActivePane.GoRootAsync();
                return true;

            case Key.Enter when alt:
                Vm.PropertiesCommand.Execute(null);
                return true;

            case Key.F1 when alt:
                ShowDriveMenu(LeftPaneView, Vm.LeftPane);
                return true;

            case Key.F2 when alt:
                ShowDriveMenu(RightPaneView, Vm.RightPane);
                return true;

            // ---------------------------------------------------- selection
            case Key.Add when !typing:
                Vm.SelectGroupCommand.Execute(null);
                return true;

            case Key.Subtract when !typing:
                Vm.UnselectGroupCommand.Execute(null);
                return true;

            case Key.Multiply when !typing:
                Vm.InvertSelectionCommand.Execute(null);
                return true;

            // ------------------------------------------------ Ctrl commands
            case Key.A when ctrl && !typing:
                Vm.SelectAllCommand.Execute(null);
                return true;

            case Key.B when ctrl:
                Vm.BranchViewCommand.Execute(null);
                return true;

            case Key.C when ctrl && !typing:
                Vm.ClipboardCopyCommand.Execute(null);
                return true;

            case Key.D when ctrl:
                Vm.AddBookmarkCommand.Execute(null);
                return true;

            case Key.H when ctrl:
                Vm.ToggleHiddenCommand.Execute(null);
                return true;

            case Key.L when ctrl:
                Vm.CalculateSpaceCommand.Execute(null);
                return true;

            case Key.M when ctrl:
                Vm.MultiRenameCommand.Execute(null);
                return true;

            case Key.P when ctrl:
                Vm.CopyNameToCommandLineCommand.Execute(shift ? "full" : "name");
                return true;

            case Key.Q when ctrl:
                Vm.ToggleThemeCommand.Execute(null);
                return true;

            case Key.R when ctrl:
                Vm.RefreshCommand.Execute(null);
                return true;

            case Key.S when ctrl:
                Vm.QuickFilterCommand.Execute(null);
                return true;

            case Key.T when ctrl:
                Vm.NewTabCommand.Execute(null);
                return true;

            case Key.U when ctrl:
                Vm.SwapPanesCommand.Execute(null);
                return true;

            case Key.V when ctrl && !typing:
                Vm.ClipboardPasteCommand.Execute(null);
                return true;

            case Key.W when ctrl:
                Vm.CloseTabCommand.Execute(null);
                return true;

            case Key.X when ctrl && !typing:
                Vm.ClipboardCutCommand.Execute(null);
                return true;

            default:
                return false;
        }
    }

    /// <summary>Alt+F1 / Alt+F2 drive chooser for the matching pane.</summary>
    private void ShowDriveMenu(FilePaneView view, FilePaneViewModel pane)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = view,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Center
        };

        foreach (var drive in DriveService.GetDrives())
        {
            var item = new MenuItem
            {
                Header = drive.IsReady ? $"{drive.Letter.ToUpperInvariant()}:   {drive.Label}" : $"{drive.Letter.ToUpperInvariant()}:",
                InputGestureText = drive.IsReady ? Models.FileItem.FormatBytesShort((long)drive.FreeBytes) + " free" : "not ready",
                IsEnabled = drive.IsReady
            };

            var root = drive.Root;
            item.Click += (_, _) =>
            {
                Vm.SetActivePane(pane);
                _ = pane.NavigateAsync(root);
                view.FocusList();
            };

            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    // ------------------------------------------------------- command line

    private void OnCommandLineKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                _ = Vm.ExecuteCommandLineAsync();
                ActivePaneView.FocusList();
                break;

            case Key.Escape:
                e.Handled = true;
                Vm.CommandLine = string.Empty;
                ActivePaneView.FocusList();
                break;

            case Key.Up when Vm.CommandHistory.Count > 0:
                e.Handled = true;
                Vm.CommandLine = Vm.CommandHistory[0];
                CommandBox.CaretIndex = CommandBox.Text.Length;
                break;
        }
    }

    // --------------------------------------------------- function key bar

    private void OnFunctionKey(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;

        var modifiers = Keyboard.Modifiers;
        bool shift = (modifiers & ModifierKeys.Shift) != 0;
        bool alt = (modifiers & ModifierKeys.Alt) != 0;

        switch (tag)
        {
            case "F3": _ = Vm.ViewAsync(external: alt); break;
            case "F4": Vm.EditCommand.Execute(null); break;
            case "F5": (alt ? Vm.PackCommand : Vm.CopyCommand).Execute(null); break;
            case "F6": (shift ? Vm.RenameCommand : Vm.MoveCommand).Execute(null); break;
            case "F7": Vm.MakeDirectoryCommand.Execute(null); break;
            case "F8": Vm.DeleteCommand.Execute(shift); break;
            case "F9": Vm.SearchCommand.Execute(null); break;
            case "F10": Close(); break;
        }

        ActivePaneView.FocusList();
    }

    /// <summary>Relabels the bottom bar as Shift and Alt change, like the original.</summary>
    private void UpdateFunctionKeyBar()
    {
        var modifiers = Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Alt);
        if (modifiers == _lastModifiers) return;
        _lastModifiers = modifiers;

        bool shift = (modifiers & ModifierKeys.Shift) != 0;
        bool alt = (modifiers & ModifierKeys.Alt) != 0;

        if (alt)
        {
            KeyF3.Content = "Alt+F3 ExtView";
            KeyF4.Content = "F4 Edit";
            KeyF5.Content = "Alt+F5 Pack";
            KeyF6.Content = "F6 Move";
            KeyF7.Content = "Alt+F7 Find";
            KeyF8.Content = "F8 Delete";
            KeyF9.Content = "Alt+F9 Unpack";
            KeyF10.Content = "Alt+F4 Exit";
        }
        else if (shift)
        {
            KeyF3.Content = "F3 View";
            KeyF4.Content = "F4 Edit";
            KeyF5.Content = "F5 Copy";
            KeyF6.Content = "Shift+F6 Rename";
            KeyF7.Content = "F7 NewFolder";
            KeyF8.Content = "Shift+F8 Wipe";
            KeyF9.Content = "Alt+F7 Find";
            KeyF10.Content = "Alt+F4 Exit";
        }
        else
        {
            KeyF3.Content = "F3 View";
            KeyF4.Content = "F4 Edit";
            KeyF5.Content = "F5 Copy";
            KeyF6.Content = "F6 Move";
            KeyF7.Content = "F7 NewFolder";
            KeyF8.Content = "F8 Delete";
            KeyF9.Content = "Alt+F7 Find";
            KeyF10.Content = "Alt+F4 Exit";
        }
    }
}
