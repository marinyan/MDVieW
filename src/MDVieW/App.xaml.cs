using System.Windows;

namespace MDVieW;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        foreach (var path in e.Args.Where(System.IO.File.Exists)) window.OpenPath(path);
    }
}
