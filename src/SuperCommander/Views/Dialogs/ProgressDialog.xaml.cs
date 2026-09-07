using System.Diagnostics;
using System.Windows;
using SuperCommander.Models;
using SuperCommander.Services;

namespace SuperCommander.Views.Dialogs;

/// <summary>
/// Shown while a copy, move, pack or unpack runs. The owner window is disabled by
/// the caller so this behaves modally without blocking the await.
/// </summary>
public partial class ProgressDialog : ThemedWindow
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _closing;

    public ProgressDialog()
    {
        InitializeComponent();
    }

    public event EventHandler? Cancelled;

    public string Caption
    {
        get => CaptionText.Text;
        set
        {
            CaptionText.Text = value;
            Title = value;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        CancelButton.Content = "Cancelling...";
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Only the owner may close this window; the X button cancels instead.
        if (!_closing)
        {
            e.Cancel = true;
            OnCancel(this, new RoutedEventArgs());
            return;
        }
        base.OnClosing(e);
    }

    public new void Close()
    {
        _closing = true;
        base.Close();
    }

    // ---------------------------------------------------------------- updates

    public void Update(FileOperationProgress progress)
    {
        SourceText.Text = progress.CurrentFile;
        TargetText.Text = progress.CurrentTarget;

        FileProgress.Value = Math.Clamp(progress.FilePercent, 0, 100);
        FilePercentText.Text = $"{progress.FilePercent:F0}%";

        var total = progress.TotalPercent;
        TotalProgress.Value = Math.Clamp(total, 0, 100);
        TotalPercentText.Text = $"{total:F0}%";

        CountText.Text = progress.FilesTotal > 0
            ? $"{progress.FilesDone} of {progress.FilesTotal} files - " +
              $"{FileItem.FormatBytesShort(progress.BytesDone)} of {FileItem.FormatBytesShort(progress.BytesTotal)}"
            : string.Empty;

        UpdateSpeed(progress.BytesDone, progress.BytesTotal);
    }

    public void Update(ArchiveService.ArchiveProgress progress)
    {
        SourceText.Text = progress.CurrentEntry;
        TargetText.Text = string.Empty;

        FileProgress.Value = 0;
        FilePercentText.Text = string.Empty;

        TotalProgress.Value = Math.Clamp(progress.Percent, 0, 100);
        TotalPercentText.Text = $"{progress.Percent:F0}%";
        CountText.Text = progress.Total > 0 ? $"{progress.Done} of {progress.Total} entries" : string.Empty;
        SpeedText.Text = string.Empty;
    }

    private void UpdateSpeed(long bytesDone, long bytesTotal)
    {
        var seconds = _clock.Elapsed.TotalSeconds;
        if (seconds < 0.75 || bytesDone <= 0) return;

        double bytesPerSecond = bytesDone / seconds;
        if (bytesPerSecond <= 0) return;

        var speed = FileItem.FormatBytesShort((long)bytesPerSecond);

        long remaining = Math.Max(0, bytesTotal - bytesDone);
        var eta = TimeSpan.FromSeconds(remaining / bytesPerSecond);

        SpeedText.Text = remaining > 0
            ? $"{speed}/s - {Format(eta)} left"
            : $"{speed}/s";
    }

    private static string Format(TimeSpan span) =>
        span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : span.TotalMinutes >= 1
                ? $"{span.Minutes}m {span.Seconds}s"
                : $"{span.Seconds}s";
}
