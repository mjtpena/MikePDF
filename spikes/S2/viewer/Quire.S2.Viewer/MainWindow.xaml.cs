using Microsoft.UI.Xaml;

namespace Quire.S2.Viewer;

public sealed partial class MainWindow : Window
{
    bool _started;
    public Microsoft.UI.Xaml.Controls.Grid HostGrid => Host;
    public MainWindow()
    {
        InitializeComponent();
        Activated += (s, e) =>
        {
            if (_started) return; _started = true;
            var opt = new Options(Environment.GetCommandLineArgs().Skip(1).ToArray());
            _ = Flow.Main2(this, opt);
        };
    }
    public void SetOverlay(string t) { Overlay.Text = t; }
}
