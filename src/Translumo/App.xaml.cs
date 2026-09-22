using System.Windows;
using Translumo.Local;

namespace Translumo;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        MainWindow = new LocalWindow();
        MainWindow.Show();
    }
}
