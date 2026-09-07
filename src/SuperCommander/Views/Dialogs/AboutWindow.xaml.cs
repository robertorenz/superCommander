using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace SuperCommander.Views.Dialogs;

/// <summary>About box doubling as the keyboard reference.</summary>
public partial class AboutWindow : ThemedWindow
{
    private static readonly (string Section, (string Keys, string Action)[] Items)[] Shortcuts =
    {
        ("NAVIGATION", new[]
        {
            ("Tab", "Switch to the other pane"),
            ("Enter", "Open folder, archive or file"),
            ("Backspace / Ctrl+PgUp", "Go to the parent folder"),
            ("Ctrl+PgDn", "Enter folder or archive"),
            ("Ctrl+\\", "Go to the drive root"),
            ("Alt+Left / Alt+Right", "Back / forward in history"),
            ("Alt+F1 / Alt+F2", "Change the left / right drive"),
            ("Ctrl+T / Ctrl+W", "New tab / close tab"),
            ("Ctrl+Tab", "Next tab"),
            ("Ctrl+B", "Branch view (all files in subfolders)"),
            ("Ctrl+D", "Bookmarks"),
            ("Ctrl+F", "Connect to an FTP server"),
            ("Ctrl+Shift+F", "Disconnect from the server")
        }),
        ("FILE COMMANDS", new[]
        {
            ("F3", "View"),
            ("Alt+F3", "View with the external viewer"),
            ("F4", "Edit"),
            ("F5", "Copy"),
            ("Shift+F5", "Copy inside the same folder"),
            ("F6", "Move / rename"),
            ("Shift+F6", "Rename in place"),
            ("F7", "New folder"),
            ("F8 / Delete", "Delete to the Recycle Bin"),
            ("Shift+Delete", "Delete permanently"),
            ("Alt+F5", "Pack into a .zip"),
            ("Alt+F9", "Unpack an archive"),
            ("Alt+F7", "Find files"),
            ("Ctrl+M", "Multi-rename tool"),
            ("Alt+Enter", "Properties")
        }),
        ("SELECTION", new[]
        {
            ("Insert", "Mark and move down"),
            ("Space", "Mark (folders also get their size)"),
            ("Ctrl+A", "Mark everything"),
            ("Num +", "Mark by file mask"),
            ("Num -", "Unmark by file mask"),
            ("Num *", "Invert the marks")
        }),
        ("PANES AND VIEW", new[]
        {
            ("Ctrl+U", "Swap the two panes"),
            ("Ctrl+Left / Ctrl+Right", "Send the current folder to the other pane"),
            ("Ctrl+R / F2", "Re-read the folder"),
            ("Ctrl+L", "Calculate the size of marked folders"),
            ("Ctrl+S", "Quick filter"),
            ("Ctrl+H", "Show or hide hidden files"),
            ("Ctrl+Q", "Switch theme"),
            ("Ctrl+C / Ctrl+X / Ctrl+V", "Clipboard copy, cut and paste"),
            ("Ctrl+P", "Put the current name on the command line")
        })
    };

    private AboutWindow()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Version {version?.ToString(3) ?? "1.0.0"}   -   .NET {Environment.Version}";

        BuildShortcutList();
    }

    private void BuildShortcutList()
    {
        foreach (var (section, items) in Shortcuts)
        {
            var heading = new TextBlock { Text = section, Margin = new Thickness(0, 0, 0, 8) };
            heading.SetResourceReference(StyleProperty, "SC.Label.Section");
            ShortcutHost.Children.Add(heading);

            var grid = new Grid { Margin = new Thickness(0, 0, 0, 18) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (int i = 0; i < items.Length; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var keys = new TextBlock
                {
                    Text = items[i].Keys,
                    Margin = new Thickness(0, 2, 12, 2),
                    FontSize = 11
                };
                keys.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
                keys.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
                Grid.SetRow(keys, i);
                Grid.SetColumn(keys, 0);
                grid.Children.Add(keys);

                var action = new TextBlock
                {
                    Text = items[i].Action,
                    Margin = new Thickness(0, 2, 0, 2),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                };
                action.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
                Grid.SetRow(action, i);
                Grid.SetColumn(action, 1);
                grid.Children.Add(action);
            }

            ShortcutHost.Children.Add(grid);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    public static void ShowAbout(Window? owner)
    {
        var window = new AboutWindow
        {
            Owner = owner is not null && owner.IsLoaded ? owner : null
        };
        if (window.Owner is null) window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
    }
}
