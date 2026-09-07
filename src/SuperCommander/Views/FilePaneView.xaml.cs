using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SuperCommander.Interop;
using SuperCommander.Models;
using SuperCommander.ViewModels;

namespace SuperCommander.Views;

/// <summary>
/// The visual half of a pane. Owns the keyboard model, drag and drop, the shell
/// context menu and column sizing; everything else lives in the view model.
/// </summary>
public partial class FilePaneView : UserControl
{
    private readonly DispatcherTimer _quickSearchTimer;
    private string _quickSearch = string.Empty;
    private Point _dragOrigin;
    private bool _dragArmed;
    private FilePaneViewModel? _vm;

    public FilePaneView()
    {
        InitializeComponent();

        _quickSearchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
        _quickSearchTimer.Tick += (_, _) => EndQuickSearch();

        List.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(OnHeaderClick));
        List.SizeChanged += (_, _) => ResizeNameColumn();

        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => ApplyChromeVisibility();
    }

    private MainViewModel? Main => Window.GetWindow(this)?.DataContext as MainViewModel;

    private IntPtr Handle
    {
        get
        {
            var window = Window.GetWindow(this);
            return window is null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
        }
    }

    // ---------------------------------------------------------------- binding

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.FocusRequested -= OnFocusRequested;
        }

        _vm = DataContext as FilePaneViewModel;

        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.FocusRequested += OnFocusRequested;
            UpdateSortIndicators();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(FilePaneViewModel.SelectedItem):
                ScrollSelectionIntoView();
                break;
            case nameof(FilePaneViewModel.Sort):
                UpdateSortIndicators();
                break;
            case nameof(FilePaneViewModel.IsActive):
                PaneBorder.SetResourceReference(BorderBrushProperty,
                    _vm?.IsActive == true ? "Accent" : "App.Border");
                break;
        }
    }

    private void OnFocusRequested(object? sender, EventArgs e) => FocusList();

    /// <summary>Puts keyboard focus on the cursor row so the pane feels "live".</summary>
    public void FocusList()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_vm?.SelectedItem is not null)
            {
                List.ScrollIntoView(_vm.SelectedItem);
                if (List.ItemContainerGenerator.ContainerFromItem(_vm.SelectedItem) is ListViewItem row)
                {
                    row.Focus();
                    return;
                }
            }
            List.Focus();
        });
    }

    private void ScrollSelectionIntoView()
    {
        if (_vm?.SelectedItem is null) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_vm?.SelectedItem is not null) List.ScrollIntoView(_vm.SelectedItem);
        });
    }

    /// <summary>Honours the drive bar / tab bar visibility settings.</summary>
    public void ApplyChromeVisibility()
    {
        var settings = Main?.Settings;
        if (settings is null) return;

        DriveBar.Visibility = settings.ShowDriveBar ? Visibility.Visible : Visibility.Collapsed;
    }

    // --------------------------------------------------------------- columns

    private void ResizeNameColumn()
    {
        double available = List.ActualWidth
                           - ColExt.ActualWidth - ColSize.ActualWidth
                           - ColDate.ActualWidth - ColAttr.ActualWidth
                           - 22; // scroll bar plus border

        ColName.Width = Math.Max(140, available);
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header || header.Column is null) return;
        if (_vm is null) return;

        var column = header.Column;
        SortColumn? target =
            ReferenceEquals(column, ColName) ? SortColumn.Name :
            ReferenceEquals(column, ColExt) ? SortColumn.Extension :
            ReferenceEquals(column, ColSize) ? SortColumn.Size :
            ReferenceEquals(column, ColDate) ? SortColumn.Date :
            ReferenceEquals(column, ColAttr) ? SortColumn.Attributes : null;

        if (target is null) return;

        _ = _vm.SortByAsync(target.Value);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, UpdateSortIndicators);
    }

    private void UpdateSortIndicators()
    {
        if (_vm is null) return;

        foreach (var header in FindVisualChildren<GridViewColumnHeader>(List))
        {
            if (header.Column is null) continue;

            SortColumn? column =
                ReferenceEquals(header.Column, ColName) ? SortColumn.Name :
                ReferenceEquals(header.Column, ColExt) ? SortColumn.Extension :
                ReferenceEquals(header.Column, ColSize) ? SortColumn.Size :
                ReferenceEquals(header.Column, ColDate) ? SortColumn.Date :
                ReferenceEquals(header.Column, ColAttr) ? SortColumn.Attributes : null;

            header.Tag = column == _vm.Sort.Column
                ? (_vm.Sort.Descending ? "Desc" : "Asc")
                : null;
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;

            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }

    // ------------------------------------------------------------ drive / tabs

    private void OnDriveClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string root } && _vm is not null)
        {
            Main?.SetActivePane(_vm);
            _ = _vm.NavigateAsync(root);
            FocusList();
        }
    }

    private void OnTabClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PaneTab tab } && _vm is not null)
        {
            Main?.SetActivePane(_vm);
            _ = _vm.ActivateTabAsync(tab);
            FocusList();
        }
    }

    private void OnTabRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PaneTab tab } && _vm is not null)
            _ = _vm.CloseTabAsync(tab);
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _ = _vm?.ReloadAsync();
        FocusList();
    }

    // ------------------------------------------------------------ path editor

    private void OnPathFocus(object sender, KeyboardFocusChangedEventArgs e) => PathBox.SelectAll();

    private void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                _ = _vm.NavigateAsync(PathBox.Text.Trim());
                FocusList();
                break;

            case Key.Escape:
                e.Handled = true;
                PathBox.Text = _vm.DisplayPath;
                FocusList();
                break;
        }
    }

    // ----------------------------------------------------------------- focus

    private void OnListGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_vm is not null) Main?.SetActivePane(_vm);
    }

    // -------------------------------------------------------------- keyboard

    private void OnListPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;

        var modifiers = Keyboard.Modifiers;

        // Anything with Alt or Ctrl belongs to the window-level bindings.
        if ((modifiers & (ModifierKeys.Alt | ModifierKeys.Control)) != 0) return;

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                EndQuickSearch();
                if (_vm.SelectedItem is not null) _ = ActivateAsync(_vm.SelectedItem);
                break;

            case Key.Back:
                e.Handled = true;
                EndQuickSearch();
                _ = _vm.GoUpAsync();
                break;

            case Key.Insert:
                e.Handled = true;
                MarkAndAdvance();
                break;

            case Key.Space:
                e.Handled = true;
                MarkCurrent();
                break;

            case Key.Escape:
                e.Handled = true;
                if (_quickSearch.Length > 0) EndQuickSearch();
                else if (_vm.QuickFilter.Length > 0) _vm.QuickFilter = string.Empty;
                else _vm.MarkAll(false);
                break;
        }
    }

    private void MarkAndAdvance()
    {
        if (_vm?.SelectedItem is null) return;

        int index = _vm.Items.IndexOf(_vm.SelectedItem);
        _vm.ToggleMark(_vm.SelectedItem);

        if (index >= 0 && index + 1 < _vm.Items.Count)
            _vm.SelectedItem = _vm.Items[index + 1];
    }

    private void MarkCurrent()
    {
        var item = _vm?.SelectedItem;
        if (item is null || _vm is null) return;

        _vm.ToggleMark(item);

        // Space on a folder also reports its size, exactly like Total Commander.
        if (item.IsMarked && item.IsDirectory && !item.IsParent && item.Size < 0)
            _ = _vm.CalculateSelectedFolderSizesAsync();
    }

    // ---------------------------------------------------------- quick search

    private void OnListTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_vm is null || e.Text.Length == 0) return;

        char c = e.Text[0];
        if (char.IsControl(c)) return;

        e.Handled = true;

        _quickSearch += c;
        QuickSearchText.Text = _quickSearch;
        QuickSearchBadge.Visibility = Visibility.Visible;

        _quickSearchTimer.Stop();
        _quickSearchTimer.Start();

        if (!_vm.QuickSearch(_quickSearch))
        {
            // No match - drop the last character so typing stays forgiving.
            _quickSearch = _quickSearch[..^1];
            QuickSearchText.Text = _quickSearch;
            if (_quickSearch.Length == 0) EndQuickSearch();
        }
    }

    private void EndQuickSearch()
    {
        _quickSearchTimer.Stop();
        _quickSearch = string.Empty;
        QuickSearchBadge.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ mouse

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm?.SelectedItem is null) return;
        if (GetRowUnderMouse(e) is null) return;

        e.Handled = true;
        _ = ActivateAsync(_vm.SelectedItem);
    }

    /// <summary>
    /// Opens a row. Remote files need the view model to fetch them first, so the
    /// pane defers to it when EnterAsync declines.
    /// </summary>
    private async Task ActivateAsync(FileItem item)
    {
        if (_vm is null) return;

        if (await _vm.EnterAsync(item)) return;

        if (_vm.IsFtpMode && !item.IsDirectory && Main is not null)
            await Main.OpenRemoteAsync(_vm, item);
    }

    private void OnListMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        _dragOrigin = e.GetPosition(null);
        _dragArmed = GetRowUnderMouse(e) is not null;
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed || _vm is null) return;

        var current = e.GetPosition(null);
        if (Math.Abs(current.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragArmed = false;
        if (_vm.IsArchiveMode) return;

        var paths = _vm.EffectivePaths;
        if (paths.Count == 0) return;

        var data = new DataObject(DataFormats.FileDrop, paths.ToArray());
        DragDrop.DoDragDrop(List, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    private void OnListRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null) return;

        Main?.SetActivePane(_vm);

        var row = GetRowUnderMouse(e);
        if (row?.DataContext is FileItem item && !item.IsMarked)
            _vm.SelectedItem = item;

        if (_vm.IsArchiveMode) return; // shell knows nothing about our archive rows

        var paths = row is null
            ? new List<string> { _vm.CurrentPath }
            : _vm.EffectivePaths.ToList();

        if (paths.Count == 0) paths.Add(_vm.CurrentPath);

        e.Handled = true;

        if (NativeMethods.GetCursorPos(out var point))
        {
            bool extended = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            ShellContextMenu.Show(Handle, paths, point.X, point.Y, extended);
            _ = _vm.ReloadAsync();
        }
    }

    private ListViewItem? GetRowUnderMouse(RoutedEventArgs e) => GetRow(e.OriginalSource);

    private static ListViewItem? GetRow(object? originalSource)
    {
        var source = originalSource as DependencyObject;
        while (source is not null && source is not ListViewItem)
        {
            if (source is ListView) return null;
            source = VisualTreeHelper.GetParent(source);
        }
        return source as ListViewItem;
    }

    // ------------------------------------------------------------ drag & drop

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        if (_vm is null || _vm.IsArchiveMode || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = ResolveDropEffect(e);
        e.Handled = true;
    }

    private DragDropEffects ResolveDropEffect(DragEventArgs e)
    {
        if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0) return DragDropEffects.Copy;
        if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0) return DragDropEffects.Move;

        // Same volume defaults to move, across volumes to copy - Explorer's rule.
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && _vm is not null)
        {
            var sourceRoot = Path.GetPathRoot(files[0]);
            var targetRoot = Path.GetPathRoot(_vm.CurrentPath);
            if (string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase))
                return DragDropEffects.Move;
        }

        return DragDropEffects.Copy;
    }

    private void OnListDrop(object sender, DragEventArgs e)
    {
        if (_vm is null || Main is null) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;

        e.Handled = true;
        bool move = ResolveDropEffect(e) == DragDropEffects.Move;

        // A drop onto a folder row targets that folder.
        var row = GetRowUnderMouse(e);
        if (row?.DataContext is FileItem { IsDirectory: true, IsParent: false } folder)
        {
            _ = Main.DropAsync(_vm, files, move, folder.FullPath);
            return;
        }

        _ = Main.DropAsync(_vm, files, move);
    }
}
