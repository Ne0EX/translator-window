using System.Threading;
using System.Windows;
using Translumo.Local;

namespace Translumo;
public partial class App : Application
{
    private static Mutex? instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        instanceMutex = new Mutex(true, @"Local\Ne0EX.Translumo.Local", out var firstInstance);
        if (!firstInstance)
        {
            Shutdown();
            return;
        }
        base.OnStartup(e);
        MainWindow = new LocalWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
