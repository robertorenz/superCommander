using System.Windows;
using System.Windows.Threading;
using SuperCommander.Services;
using SuperCommander.ViewModels;
using SuperCommander.Views;
using SuperCommander.Views.Dialogs;

namespace SuperCommander;

public partial class App : Application
{
    internal static SettingsService Settings { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Settings = new SettingsService();
        Settings.Load();

        ThemeService.Apply(Settings.Current.Theme);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        var vm = new MainViewModel(Settings);
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        vm.Attach(window);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Settings.Save();
        }
        catch (Exception)
        {
            // Never block shutdown on a settings write.
        }
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ReportError(e.Exception);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            Dispatcher.Invoke(() => ReportError(ex));
    }

    /// <summary>Modal themed error dialog - the app never uses MessageBox.</summary>
    private void ReportError(Exception ex)
    {
        LogError(ex);

        try
        {
            MessageDialog.ShowError(MainWindow, "Unexpected error",
                ex.Message, ex.ToString());
        }
        catch (Exception)
        {
            // If even the dialog fails there is nothing sensible left to do.
        }
    }

    /// <summary>Appends the fault to %APPDATA%\SuperCommander\error.log.</summary>
    private static void LogError(Exception ex)
    {
        try
        {
            var directory = Settings.DirectoryPath;
            System.IO.Directory.CreateDirectory(directory);

            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}";
            System.IO.File.AppendAllText(System.IO.Path.Combine(directory, "error.log"), line);
        }
        catch (Exception)
        {
            // Logging must never throw on top of the original fault.
        }
    }
}
