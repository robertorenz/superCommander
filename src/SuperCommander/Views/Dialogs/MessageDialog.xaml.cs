using System.Windows;
using System.Windows.Media;

namespace SuperCommander.Views.Dialogs;

public enum MessageKind
{
    Information,
    Warning,
    Error,
    Question
}

/// <summary>
/// The app's single modal message surface. Replaces MessageBox everywhere so the
/// look follows the active palette.
/// </summary>
public partial class MessageDialog : ThemedWindow
{
    // 24x24 viewbox glyphs.
    private const string GlyphInfo = "M12,2 A10,10 0 1 0 12,22 A10,10 0 1 0 12,2 Z M11,7 h2 v2 h-2 Z M11,11 h2 v6 h-2 Z";
    private const string GlyphWarning = "M12,2 L23,21 H1 Z M11,9 h2 v6 h-2 Z M11,16 h2 v2 h-2 Z";
    private const string GlyphError = "M12,2 A10,10 0 1 0 12,22 A10,10 0 1 0 12,2 Z M8.4,7 L12,10.6 L15.6,7 L17,8.4 L13.4,12 L17,15.6 L15.6,17 L12,13.4 L8.4,17 L7,15.6 L10.6,12 L7,8.4 Z";
    private const string GlyphQuestion = "M12,2 A10,10 0 1 0 12,22 A10,10 0 1 0 12,2 Z M11,16 h2 v2 h-2 Z M12,6 C10,6 8.6,7.2 8.4,9.2 h2 C10.6,8.3 11.1,7.8 12,7.8 c0.9,0 1.5,0.5 1.5,1.3 0,0.7 -0.4,1.1 -1.1,1.6 -0.9,0.6 -1.4,1.2 -1.4,2.4 v0.5 h2 v-0.4 c0,-0.8 0.3,-1.1 1.2,-1.7 0.9,-0.6 1.4,-1.4 1.4,-2.5 C15.6,7.2 14.1,6 12,6 Z";

    private MessageDialog()
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

    private void OnDetailsToggled(object sender, RoutedEventArgs e)
    {
        DetailsBox.Visibility = DetailsToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------- public API

    public static void ShowInfo(Window? owner, string title, string message, string? details = null) =>
        Build(owner, title, message, details, MessageKind.Information, "OK", null, false).ShowDialog();

    public static void ShowWarning(Window? owner, string title, string message, string? details = null) =>
        Build(owner, title, message, details, MessageKind.Warning, "OK", null, false).ShowDialog();

    public static void ShowError(Window? owner, string title, string message, string? details = null) =>
        Build(owner, title, message, details, MessageKind.Error, "OK", null, false).ShowDialog();

    public static bool ShowConfirm(Window? owner, string title, string message,
        string okText = "OK", string cancelText = "Cancel", bool danger = false, string? details = null) =>
        Build(owner, title, message, details, danger ? MessageKind.Warning : MessageKind.Question,
            okText, cancelText, danger).ShowDialog() == true;

    private static MessageDialog Build(Window? owner, string title, string message, string? details,
        MessageKind kind, string okText, string? cancelText, bool danger)
    {
        var dialog = new MessageDialog
        {
            Title = title,
            Owner = owner is not null && owner.IsLoaded ? owner : null
        };

        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dialog.HeadingText.Text = title;
        dialog.MessageText.Text = message;
        dialog.OkButton.Content = okText;

        if (cancelText is not null)
        {
            dialog.CancelButton.Content = cancelText;
            dialog.CancelButton.Visibility = Visibility.Visible;
        }

        if (!string.IsNullOrWhiteSpace(details))
        {
            dialog.DetailsText.Text = details;
            dialog.DetailsHost.Visibility = Visibility.Visible;
        }

        var (glyph, brushKey, backgroundKey) = kind switch
        {
            MessageKind.Warning => (GlyphWarning, "Status.Warn", "App.SurfaceAlt"),
            MessageKind.Error => (GlyphError, "Status.Bad", "App.SurfaceAlt"),
            MessageKind.Question => (GlyphQuestion, "Accent", "Accent.Subtle"),
            _ => (GlyphInfo, "Status.Info", "Accent.Subtle")
        };

        dialog.IconGlyph.Data = Geometry.Parse(glyph);
        dialog.IconGlyph.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brushKey);
        dialog.IconHost.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, backgroundKey);

        if (danger)
            dialog.OkButton.SetResourceReference(StyleProperty, "SC.Button");

        return dialog;
    }
}
