using System.Windows;
using ForceDelete.Services;

namespace ForceDelete;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Elevated worker invocation (--op …): do the job headlessly and exit.
        if (Elevation.TryRunWorker(e.Args))
        {
            Shutdown(0);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
