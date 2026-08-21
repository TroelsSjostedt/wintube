using Microsoft.UI.Xaml;
using WinTube.Core;

namespace WinTube.App;

public partial class App : Application
{
    /// One HttpClient for every Core service — sockets are pooled per instance.
    public static HttpClient Http { get; } = new();
    public static Secrets Secrets { get; } = Secrets.Load();

    public static MainWindow? Window { get; private set; }

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        Window.Activate();
    }
}
