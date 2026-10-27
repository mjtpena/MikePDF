using Microsoft.UI.Xaml;

namespace HelloQuire;

public partial class App : Application
{
    public static MainWindow? MainWin;

    public App()
    {
        Timing.Mark("App.ctor");
        InitializeComponent();
        Timing.Mark("App.InitializeComponent.done");
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Timing.Mark("OnLaunched");
        MainWin = new MainWindow();
        Timing.Mark("MainWindow.ctor.done");
        MainWin.Activate();
        Timing.Mark("Window.Activate.returned");
    }
}
