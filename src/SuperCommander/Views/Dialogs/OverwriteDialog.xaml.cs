using System.Windows;
using System.Windows.Controls;
using SuperCommander.Models;
using SuperCommander.Services;

namespace SuperCommander.Views.Dialogs;

/// <summary>Conflict resolver shown by the copy/move engine.</summary>
public partial class OverwriteDialog : ThemedWindow
{
    private OverwriteAction _action = OverwriteAction.Cancel;

    private OverwriteDialog()
    {
        InitializeComponent();
    }

    private void OnChoose(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<OverwriteAction>(tag, out var action))
            _action = action;

        DialogResult = true;
        Close();
    }

    /// <summary>Blocks on the UI thread and returns the user's decision.</summary>
    public static OverwriteAction Ask(Window? owner, ConflictInfo info)
    {
        var dialog = new OverwriteDialog
        {
            Owner = owner is not null && owner.IsLoaded ? owner : null
        };

        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dialog.NameText.Text = info.TargetPath;
        dialog.SourceSize.Text = FileItem.FormatBytesShort(info.SourceSize);
        dialog.TargetSize.Text = FileItem.FormatBytesShort(info.TargetSize);
        dialog.SourceDate.Text = info.SourceModified == DateTime.MinValue
            ? string.Empty
            : info.SourceModified.ToString("yyyy-MM-dd HH:mm:ss");
        dialog.TargetDate.Text = info.TargetModified == DateTime.MinValue
            ? string.Empty
            : info.TargetModified.ToString("yyyy-MM-dd HH:mm:ss");

        // Point out which copy is newer so the choice is obvious at a glance.
        if (info.SourceModified > info.TargetModified)
            dialog.SourceDate.SetResourceReference(ForegroundProperty, "Status.Good");
        else if (info.TargetModified > info.SourceModified)
            dialog.TargetDate.SetResourceReference(ForegroundProperty, "Status.Good");

        dialog.ShowDialog();
        return dialog._action;
    }
}
