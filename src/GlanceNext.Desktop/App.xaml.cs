using GlanceNext.Desktop.Services;
using Microsoft.UI.Xaml;

namespace GlanceNext.Desktop;

public partial class App : Application
{
    private MainWindow? window;
    private Mutex? instance;
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            new LocalStore().Log("unhandled", args.Exception + " " + args.Message);
            window?.ClearProtection();
            // Do not keep running after an unhandled exception in safety-critical state.
        };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        instance = new Mutex(true, "Local\\GlanceNext.Desktop", out var created);
        if (!created)
        {
            WindowsHost.ShowExistingWindow();
            Exit();
            return;
        }
        window = new MainWindow();
        var tray = Environment.GetCommandLineArgs().Contains("--tray");
        window.Activate();
        if (tray)
            window.AppWindow.Hide();
    }
}
