using System.Windows;
using SuperCommander.Views.Dialogs;

namespace SuperCommander.Tests;

/// <summary>
/// A dialog whose XAML names a style or brush that does not exist still compiles
/// and only fails when someone opens it. This loads the window for real - on an
/// STA thread, against the app's own dictionaries - so a bad key fails the build
/// instead of the user.
/// </summary>
internal static partial class Program
{
    private static void DialogLoads(string root)
    {
        Section("The synchronise dialog loads against the real palette");

        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (var source in new[] { "Themes/Palette.Dark.xaml", "Themes/Controls.xaml" })
                {
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/SuperCommander;component/" + source)
                    });
                }

                var window = new SyncWindow(root, root);
                window.Close();
                app.Shutdown();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Check(failure is null, failure is null
            ? "every style and brush the dialog names resolves"
            : "every style and brush the dialog names resolves - " + failure.Message);
    }
}
