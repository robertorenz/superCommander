using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using SuperCommander.Services;

namespace SuperCommander.Views.Dialogs;

/// <summary>Alt+F7 file finder. Results stream in while the scan runs.</summary>
public partial class SearchWindow : ThemedWindow
{
    private readonly ObservableCollection<string> _results = new();
    private CancellationTokenSource? _cts;

    public SearchWindow(string startPath)
    {
        InitializeComponent();

        RootBox.Text = startPath;
        ResultsList.ItemsSource = _results;
        StatusText.Text = "Ready.";
    }

    /// <summary>Raised with the full path when the user picks a result.</summary>
    public event EventHandler<string>? NavigateRequested;

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) return;

        var root = RootBox.Text.Trim();
        if (!Directory.Exists(root))
        {
            MessageDialog.ShowError(this, "Find files", $"The folder \"{root}\" does not exist.");
            return;
        }

        _results.Clear();

        var criteria = new SearchCriteria
        {
            Root = root,
            FileMask = string.IsNullOrWhiteSpace(MaskBox.Text) ? "*" : MaskBox.Text.Trim(),
            ContainingText = TextBox_Content.Text,
            CaseSensitive = CaseCheck.IsChecked == true,
            UseRegex = RegexCheck.IsChecked == true,
            WholeWords = WordsCheck.IsChecked == true,
            SearchSubdirectories = SubdirsCheck.IsChecked == true,
            IncludeHidden = HiddenCheck.IsChecked == true
        };

        _cts = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;

        var pending = new List<string>();
        var gate = new object();

        var progress = new Progress<SearchProgress>(p =>
        {
            List<string> batch;
            lock (gate)
            {
                batch = new List<string>(pending);
                pending.Clear();
            }
            foreach (var path in batch) _results.Add(path);

            StatusText.Text = p.CurrentDirectory.Length > 0
                ? $"Scanned {p.Scanned:N0} files, found {p.Found:N0} - {p.CurrentDirectory}"
                : $"Scanned {p.Scanned:N0} files, found {p.Found:N0}";
        });

        try
        {
            await SearchService.SearchAsync(criteria, progress,
                item =>
                {
                    lock (gate) pending.Add(item.FullPath);
                },
                _cts.Token);

            // Anything the last progress tick missed.
            List<string> tail;
            lock (gate)
            {
                tail = new List<string>(pending);
                pending.Clear();
            }
            foreach (var path in tail) _results.Add(path);

            StatusText.Text = $"Finished - {_results.Count:N0} file(s) found.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = $"Stopped - {_results.Count:N0} file(s) found.";
        }
        catch (Exception ex)
        {
            MessageDialog.ShowError(this, "Find files", ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        }
    }

    private void OnStop(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _results.Clear();
        StatusText.Text = "Ready.";
    }

    private void OnResultActivated(object sender, MouseButtonEventArgs e) => GoToSelected();

    private void OnResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        GoToSelected();
    }

    private void OnGoToFile(object sender, RoutedEventArgs e) => GoToSelected();

    private void GoToSelected()
    {
        if (ResultsList.SelectedItem is not string path) return;
        NavigateRequested?.Invoke(this, path);
        Close();
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
