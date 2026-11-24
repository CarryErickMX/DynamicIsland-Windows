using System.Windows;

namespace DynamicIsland;

public partial class App : Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Evita abrir dos islas al mismo tiempo
        _mutex = new Mutex(true, "DynamicIsland.Windows.SingleInstance", out bool isNew);
        if (!isNew)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
