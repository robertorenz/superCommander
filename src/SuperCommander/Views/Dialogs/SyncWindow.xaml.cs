using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using SuperCommander.Models;
using SuperCommander.Services;

namespace SuperCommander.Views.Dialogs;

/// <summary>
/// Compares two folder trees and copies or deletes whatever the user decides.
/// Nothing is written until "Synchronize" is pressed, and the grid always shows
/// exactly what that press will do.
/// </summary>
public partial class SyncWindow : ThemedWindow
{
    private readonly ObservableCollection<SyncEntry> _entries = new();
    private readonly ICollectionView _view;
    private CancellationTokenSource? _cts;

    public SyncWindow(string left, string right)
    {
        InitializeComponent();

        LeftBox.Text = left;
        RightBox.Text = right;

        EntryGrid.ItemsSource = _entries;
        _view = CollectionViewSource.GetDefaultView(_entries);
        _view.Filter = o => o is SyncEntry entry && PassesFilter(entry);

        StatusText.Text = "Press Compare to read both folders.";
    }

    /// <summary>True once files have actually been copied or deleted, so panes need re-reading.</summary>
    public bool Changed { get; private set; }

    // -------------------------------------------------------------- comparing

    private async void OnCompare(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) return;

        var left = LeftBox.Text.Trim();
        var right = RightBox.Text.Trim();

        if (!Directory.Exists(left))
        {
            MessageDialog.ShowError(this, "Synchronize", $"The left folder \"{left}\" does not exist.");
            return;
        }
        if (!Directory.Exists(right))
        {
            MessageDialog.ShowError(this, "Synchronize", $"The right folder \"{right}\" does not exist.");
            return;
        }
        if (string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase))
        {
            MessageDialog.ShowInfo(this, "Synchronize", "Both sides are the same folder.");
            return;
        }

        var options = new SyncOptions
        {
            Left = left,
            Right = right,
            FileMask = string.IsNullOrWhiteSpace(MaskBox.Text) ? "*" : MaskBox.Text.Trim(),
            Subdirectories = SubfoldersCheck.IsChecked == true,
            IncludeHidden = HiddenCheck.IsChecked == true,
            CompareContent = ContentCheck.IsChecked == true,
            IgnoreDate = IgnoreDateCheck.IsChecked == true
        };

        _entries.Clear();
        _cts = new CancellationTokenSource();
        CompareButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        SyncButton.IsEnabled = false;

        var progress = new Progress<SyncProgress>(p =>
            StatusText.Text = p.CurrentFolder.Length > 0
                ? $"Comparing - {p.Compared:N0} file(s), {p.Differences:N0} differing - {p.CurrentFolder}"
                : $"Comparing - {p.Compared:N0} file(s), {p.Differences:N0} differing");

        try
        {
            var result = await SyncService.CompareAsync(options, progress, _cts.Token);
            foreach (var entry in result)
            {
                entry.PropertyChanged += OnEntryChanged;
                _entries.Add(entry);
            }
            UpdateStatus();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Comparison stopped.";
        }
        catch (Exception ex)
        {
            MessageDialog.ShowError(this, "Synchronize", ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            CompareButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        }
    }

    private void OnStop(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SyncEntry.Action)) UpdateStatus();
    }

    // --------------------------------------------------------------- filtering

    private bool PassesFilter(SyncEntry entry) => entry.State switch
    {
        SyncState.Same => ShowEqual.IsChecked == true,
        SyncState.LeftOnly => ShowLeftOnly.IsChecked == true,
        SyncState.RightOnly => ShowRightOnly.IsChecked == true,
        _ => ShowDiffering.IsChecked == true
    };

    private void OnFilterChanged(object sender, RoutedEventArgs e) => _view.Refresh();

    // --------------------------------------------------------------- decisions

    private IEnumerable<SyncEntry> Selection =>
        EntryGrid.SelectedItems.Count > 0
            ? EntryGrid.SelectedItems.Cast<SyncEntry>().ToList()
            : Enumerable.Empty<SyncEntry>();

    private void OnSetToRight(object sender, RoutedEventArgs e) => Apply(SyncAction.ToRight);
    private void OnSetToLeft(object sender, RoutedEventArgs e) => Apply(SyncAction.ToLeft);
    private void OnSetNone(object sender, RoutedEventArgs e) => Apply(SyncAction.None);

    /// <summary>Deleting only makes sense for a file the other side does not have.</summary>
    private void OnSetDelete(object sender, RoutedEventArgs e)
    {
        foreach (var entry in Selection)
        {
            entry.Action = entry.State switch
            {
                SyncState.LeftOnly => SyncAction.DeleteLeft,
                SyncState.RightOnly => SyncAction.DeleteRight,
                _ => entry.Action
            };
        }
        UpdateStatus();
    }

    private void Apply(SyncAction action)
    {
        foreach (var entry in Selection)
        {
            // A copy needs something to copy: skip the side that has no file.
            if (action == SyncAction.ToRight && !entry.OnLeft) continue;
            if (action == SyncAction.ToLeft && !entry.OnRight) continue;
            entry.Action = action;
        }
        UpdateStatus();
    }

    private void OnPresetBothWays(object sender, RoutedEventArgs e) =>
        Preset(entry => entry.DefaultAction);

    private void OnPresetLeftToRight(object sender, RoutedEventArgs e) =>
        Preset(entry => entry.OnLeft && entry.State != SyncState.Same ? SyncAction.ToRight : SyncAction.None);

    private void OnPresetRightToLeft(object sender, RoutedEventArgs e) =>
        Preset(entry => entry.OnRight && entry.State != SyncState.Same ? SyncAction.ToLeft : SyncAction.None);

    private void OnPresetMirror(object sender, RoutedEventArgs e) =>
        Preset(entry => entry.State switch
        {
            SyncState.Same => SyncAction.None,
            SyncState.RightOnly => SyncAction.DeleteRight,
            _ => SyncAction.ToRight
        });

    private void OnPresetNothing(object sender, RoutedEventArgs e) => Preset(_ => SyncAction.None);

    private void Preset(Func<SyncEntry, SyncAction> decide)
    {
        foreach (var entry in _entries) entry.Action = decide(entry);
        UpdateStatus();
    }

    // ----------------------------------------------------------- row shortcuts

    private void OnRowActivated(object sender, MouseButtonEventArgs e)
    {
        if (EntryGrid.SelectedItem is SyncEntry entry) Cycle(entry);
    }

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                Apply(SyncAction.None);
                break;
            case Key.Right when Keyboard.Modifiers == ModifierKeys.Control:
                Apply(SyncAction.ToRight);
                break;
            case Key.Left when Keyboard.Modifiers == ModifierKeys.Control:
                Apply(SyncAction.ToLeft);
                break;
            case Key.Delete:
                OnSetDelete(sender, new RoutedEventArgs());
                break;
            case Key.Enter when EntryGrid.SelectedItem is SyncEntry entry:
                Cycle(entry);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Walks one row through the directions it can legally take.</summary>
    private void Cycle(SyncEntry entry)
    {
        entry.Action = entry.Action switch
        {
            SyncAction.None when entry.OnLeft => SyncAction.ToRight,
            SyncAction.None => SyncAction.ToLeft,
            SyncAction.ToRight when entry.OnRight => SyncAction.ToLeft,
            SyncAction.ToRight => SyncAction.None,
            SyncAction.ToLeft => SyncAction.None,
            _ => SyncAction.None
        };
        UpdateStatus();
    }

    // ------------------------------------------------------------ the transfer

    private async void OnSynchronize(object sender, RoutedEventArgs e)
    {
        var work = _entries.Where(x => x.Action != SyncAction.None).ToList();
        if (work.Count == 0)
        {
            MessageDialog.ShowInfo(this, "Synchronize", "Nothing is marked to copy or delete.");
            return;
        }

        int right = work.Count(x => x.Action == SyncAction.ToRight);
        int left = work.Count(x => x.Action == SyncAction.ToLeft);
        int deletions = work.Count(x => x.Action is SyncAction.DeleteLeft or SyncAction.DeleteRight);
        long bytes = work.Sum(x => x.TransferSize);

        var lines = new List<string>();
        if (right > 0) lines.Add($"{right:N0} file(s) to the right");
        if (left > 0) lines.Add($"{left:N0} file(s) to the left");
        if (deletions > 0) lines.Add($"{deletions:N0} file(s) deleted permanently");

        var details = deletions > 0
            ? "Deleted files do not go to the Recycle Bin."
            : $"{FileItem.FormatBytesShort(bytes)} will be copied. Existing files are overwritten.";

        if (!MessageDialog.ShowConfirm(this, "Synchronize", string.Join(", ", lines) + ".",
                okText: "Synchronize", danger: deletions > 0, details: details))
            return;

        var dialog = new ProgressDialog { Owner = this, Caption = "Synchronizing" };
        using var cts = new CancellationTokenSource();
        dialog.Cancelled += (_, _) => cts.Cancel();

        var progress = new Progress<FileOperationProgress>(dialog.Update);
        FileOperationResult result;

        IsEnabled = false;
        dialog.Show();
        try
        {
            result = await SyncService.SynchronizeAsync(work, progress, cts.Token);
        }
        finally
        {
            dialog.Close();
            IsEnabled = true;
        }

        if (result.Succeeded > 0) Changed = true;

        if (result.Failed > 0)
        {
            MessageDialog.ShowError(this, "Synchronize",
                $"{result.Succeeded:N0} done, {result.Failed:N0} failed.",
                string.Join(Environment.NewLine, result.Errors.Take(20)));
        }
        else if (result.Cancelled)
        {
            MessageDialog.ShowInfo(this, "Synchronize", $"Cancelled after {result.Succeeded:N0} file(s).");
        }

        // The grid is now stale in exactly the rows that worked, so read both sides again.
        OnCompare(sender, e);
    }

    // ---------------------------------------------------------------- footer

    private void UpdateStatus()
    {
        if (_entries.Count == 0)
        {
            StatusText.Text = "No files matched.";
            SyncButton.IsEnabled = false;
            return;
        }

        int right = _entries.Count(x => x.Action == SyncAction.ToRight);
        int left = _entries.Count(x => x.Action == SyncAction.ToLeft);
        int deletions = _entries.Count(x => x.Action is SyncAction.DeleteLeft or SyncAction.DeleteRight);
        int same = _entries.Count(x => x.State == SyncState.Same);

        StatusText.Text = $"{_entries.Count:N0} compared - {right:N0} to the right, {left:N0} to the left, " +
                          $"{deletions:N0} to delete, {same:N0} equal";

        SyncButton.IsEnabled = right + left + deletions > 0;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        base.OnClosed(e);
    }
}
