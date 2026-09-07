using System.Windows;

namespace SuperCommander.Services;

public enum AppTheme
{
    Dark,
    Light
}

/// <summary>
/// Swaps the palette dictionary in place. Every control binds its colours with
/// DynamicResource, so the change is live and needs no restart.
/// </summary>
public static class ThemeService
{
    private const int PaletteSlot = 0;

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static bool IsDark => Current == AppTheme.Dark;

    public static event EventHandler? ThemeChanged;

    public static void Apply(AppTheme theme)
    {
        var app = Application.Current;
        if (app is null) return;

        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"Themes/Palette.{theme}.xaml", UriKind.Relative)
        };

        var merged = app.Resources.MergedDictionaries;
        if (merged.Count > PaletteSlot)
            merged[PaletteSlot] = dictionary;
        else
            merged.Insert(PaletteSlot, dictionary);

        Current = theme;
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void Toggle() => Apply(Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);
}
