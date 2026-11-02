using Microsoft.UI.Xaml;

namespace Quire.S2.Viewer;

public partial class App : Application
{
    MainWindow? _w;
    public App() { InitializeComponent(); }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _w = new MainWindow();
        _w.Activate();
    }
}
