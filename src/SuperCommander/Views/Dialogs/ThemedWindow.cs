using System.Windows;
using SuperCommander.Interop;
using SuperCommander.Services;

namespace SuperCommander.Views.Dialogs;

/// <summary>
/// Base for every window in the app. Keeps the native title bar in step with the
/// active palette and repaints itself when the theme is toggled.
/// </summary>
public class ThemedWindow : Window
{
    private EventHandler? _themeHandler;

    public ThemedWindow()
    {
        SetResourceReference(BackgroundProperty, "App.Background");
        SetResourceReference(ForegroundProperty, "App.Text");
        SetResourceReference(FontFamilyProperty, "Font.Ui");
        SetResourceReference(FontSizeProperty, "Font.Size.Normal");

        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        SourceInitialized += (_, _) => ShellServices.SetTitleBarTheme(this, ThemeService.IsDark);

        Loaded += (_, _) =>
        {
            _themeHandler = (_, _) => ShellServices.SetTitleBarTheme(this, ThemeService.IsDark);
            ThemeService.ThemeChanged += _themeHandler;
        };

        Closed += (_, _) =>
        {
            if (_themeHandler is not null) ThemeService.ThemeChanged -= _themeHandler;
            _themeHandler = null;
        };
    }
}
