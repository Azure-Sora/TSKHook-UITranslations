using System.Windows;

namespace TSKHook.Workbench.Desktop;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        new MainWindow(e.Args).Show();
    }
}
