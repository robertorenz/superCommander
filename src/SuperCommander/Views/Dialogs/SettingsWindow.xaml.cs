using System.Windows;
using SuperCommander.Services;

namespace SuperCommander.Views.Dialogs;

public partial class SettingsWindow : ThemedWindow
{
    private readonly SettingsService _settings;
    private readonly AppTheme _originalTheme;
    private bool _ready;

    public SettingsWindow(SettingsService settings)
    {
        InitializeComponent();

        _settings = settings;
        _originalTheme = ThemeService.Current;

        var current = settings.Current;
        ThemeDark.IsChecked = current.Theme == AppTheme.Dark;
        ThemeLight.IsChecked = current.Theme == AppTheme.Light;

        ShowIcons.IsChecked = current.ShowIcons;
        ShowDriveBar.IsChecked = current.ShowDriveBar;
        ShowFunctionBar.IsChecked = current.ShowFunctionKeyBar;
        ShowCommandLine.IsChecked = current.ShowCommandLine;
        ShowHidden.IsChecked = current.ShowHiddenFiles;
        DirectoriesFirst.IsChecked = current.DirectoriesFirst;
        ConfirmDelete.IsChecked = current.ConfirmDelete;
        UseRecycleBin.IsChecked = current.UseRecycleBin;
        EditorBox.Text = current.ExternalEditor;
        ViewerBox.Text = current.ExternalViewer;

        SettingsPathText.Text = $"Settings file: {settings.FilePath}";
        _ready = true;
    }

    /// <summary>Theme previews live so the choice can be judged before saving.</summary>
    private void OnThemeChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ThemeService.Apply(ThemeLight.IsChecked == true ? AppTheme.Light : AppTheme.Dark);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var current = _settings.Current;

        current.Theme = ThemeService.Current;
        current.ShowIcons = ShowIcons.IsChecked == true;
        current.ShowDriveBar = ShowDriveBar.IsChecked == true;
        current.ShowFunctionKeyBar = ShowFunctionBar.IsChecked == true;
        current.ShowCommandLine = ShowCommandLine.IsChecked == true;
        current.ShowHiddenFiles = ShowHidden.IsChecked == true;
        current.ShowSystemFiles = ShowHidden.IsChecked == true;
        current.DirectoriesFirst = DirectoriesFirst.IsChecked == true;
        current.ConfirmDelete = ConfirmDelete.IsChecked == true;
        current.UseRecycleBin = UseRecycleBin.IsChecked == true;
        current.ExternalEditor = EditorBox.Text.Trim();
        current.ExternalViewer = ViewerBox.Text.Trim();

        _settings.Save();

        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        // Undo the live theme preview.
        if (ThemeService.Current != _originalTheme) ThemeService.Apply(_originalTheme);

        DialogResult = false;
        Close();
    }
}
