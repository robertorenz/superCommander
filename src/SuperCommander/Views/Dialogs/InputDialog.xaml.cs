using System.IO;
using System.Windows;
using System.Windows.Input;

namespace SuperCommander.Views.Dialogs;

/// <summary>
/// Single-line prompt used for new folder, rename, copy/move target, file masks
/// and bookmark names.
/// </summary>
public partial class InputDialog : ThemedWindow
{
    private InputDialog()
    {
        InitializeComponent();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// Returns the typed text, or null when the user cancelled.
    /// <paramref name="selectFileNameOnly"/> pre-selects just the base name so an
    /// extension is not accidentally overwritten.
    /// </summary>
    public static string? Show(Window? owner, string title, string prompt, string initial,
        string okText = "OK", bool selectFileNameOnly = false, string? hint = null)
    {
        var dialog = new InputDialog
        {
            Title = title,
            Owner = owner is not null && owner.IsLoaded ? owner : null
        };

        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dialog.PromptText.Text = prompt;
        dialog.InputBox.Text = initial;
        dialog.OkButton.Content = okText;

        if (!string.IsNullOrWhiteSpace(hint))
        {
            dialog.HintText.Text = hint;
            dialog.HintText.Visibility = Visibility.Visible;
        }

        dialog.Loaded += (_, _) =>
        {
            dialog.InputBox.Focus();

            if (selectFileNameOnly && initial.Length > 0)
            {
                var name = Path.GetFileName(initial);
                int start = initial.Length - name.Length;
                var stem = Path.GetFileNameWithoutExtension(initial);
                int length = stem.Length > 0 ? stem.Length : name.Length;

                dialog.InputBox.Select(start, length);
            }
            else
            {
                dialog.InputBox.SelectAll();
            }
        };

        return dialog.ShowDialog() == true ? dialog.InputBox.Text : null;
    }
}
