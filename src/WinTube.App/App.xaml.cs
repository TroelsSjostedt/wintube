using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinTube.Core;
using WinTube.Core.Links;
using ProtocolActivatedEventArgs = Windows.ApplicationModel.Activation.ProtocolActivatedEventArgs;

namespace WinTube.App;

public partial class App : Application
{
    /// One HttpClient for every Core service — sockets are pooled per instance.
    public static HttpClient Http { get; } = new();
    public static Secrets Secrets { get; } = Secrets.Load();
    public static Session Session { get; } = new(Http, Secrets);

    public static MainWindow? Window { get; private set; }

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        ProtocolRegistration.EnsureRegistered();
        AppInstance.GetCurrent().Activated += OnRedirectedActivation;

        Window = new MainWindow();
        Window.Activate();

        try
        {
            var candidates = ActivationCandidates(AppInstance.GetCurrent().GetActivatedEventArgs())
                .Concat(Environment.GetCommandLineArgs().Skip(1));
            foreach (var candidate in candidates)
            {
                if (YouTubeLink.TryParse(candidate) is not { } link) continue;
                Window.OpenVideo(link.VideoId, link.StartAt);
                break;
            }
        }
        catch
        {
            // A malformed initial activation must never crash startup.
        }
    }

    /// Fires on a background thread when a second launch redirects here instead of starting
    /// its own instance.
    private void OnRedirectedActivation(object? sender, AppActivationArguments args)
    {
        try
        {
            var candidates = ActivationCandidates(args);
            var window = Window;
            if (window is null) return;
            window.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    foreach (var candidate in candidates)
                    {
                        if (YouTubeLink.TryParse(candidate) is not { } link) continue;
                        window.OpenVideo(link.VideoId, link.StartAt);
                        break;
                    }
                }
                catch
                {
                    // A malformed redirected activation must never crash the app.
                }
            });
        }
        catch
        {
            // A malformed redirected activation must never crash the app.
        }
    }

    private static IEnumerable<string> ActivationCandidates(AppActivationArguments args)
    {
        if (args.Kind == ExtendedActivationKind.Protocol
            && args.Data is ProtocolActivatedEventArgs protocolArgs)
        {
            yield return protocolArgs.Uri.ToString();
        }
        else if (args.Kind == ExtendedActivationKind.Launch
            && args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launchArgs
            && launchArgs.Arguments is { } arguments)
        {
            // A redirected command-line launch: the running instance's own
            // Environment.GetCommandLineArgs() doesn't see the second process's args, so this
            // is the only way to recover them. The whole string covers an unquoted single
            // token; each whitespace-separated piece covers a quoted URL passed alongside
            // other args. TryParse rejects anything that isn't a link, so over-yielding is
            // harmless.
            yield return arguments;
            foreach (var token in arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                yield return token;
            }
        }
    }
}
