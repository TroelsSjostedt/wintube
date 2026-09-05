using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Velopack;
using Velopack.Sources;

namespace WinTube.App;

/// Custom entry point so a second launch (a wintube:// activation, a command line) redirects
/// to the running instance instead of opening a second window.
public static class Program
{
    private const string RepoUrl = "https://github.com/TroelsSjostedt/wintube";

    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack's default behavior applies any staged update and exits the process BEFORE
        // any of our own code runs. If a wintube:// click launches a second process while an
        // update is staged, that default would apply the update and exit here - never reaching
        // the single-instance redirect below, so the activation that triggered this launch is
        // silently dropped. Disable the auto-apply-on-startup default; the main-instance path
        // below applies a staged update explicitly, once we know this process is the one that
        // will actually keep running.
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();

        WinRT.ComWrappersSupport.InitializeComWrappers();

        var main = AppInstance.FindOrRegisterForKey("wintube-main");
        if (!main.IsCurrent)
        {
            // RedirectActivationToAsync(...).AsTask().Wait() run directly on this STA thread is
            // the pattern Microsoft's AppLifecycle docs warn can deadlock: the COM completion may
            // need this thread pumping messages, which a blocking Wait() never does. Running the
            // redirect on a background thread keeps the STA thread out of its own way; the bounded
            // wait below is just a backstop so a hung redirect can't strand this process forever.
            var activatedArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            var done = new ManualResetEventSlim(false);
            _ = Task.Run(async () =>
            {
                try { await main.RedirectActivationToAsync(activatedArgs); }
                finally { done.Set(); }
            });
            done.Wait(TimeSpan.FromSeconds(10));
            return 0;
        }

        // This process is the one that will keep running, so it's safe to apply an update that
        // was staged (and left un-restarted) on an earlier launch. Doing this only here - after
        // the redirect check above - is what keeps a pending update from swallowing an
        // activation meant for the running instance.
        try
        {
            var manager = new UpdateManager(new GithubSource(RepoUrl, null, false));
            if (manager.IsInstalled && manager.UpdatePendingRestart is { } pendingAsset)
            {
                manager.ApplyUpdatesAndRestart(pendingAsset);
                return 0;
            }
        }
        catch (Exception e)
        {
            WinTube.Core.Sync.WatchProgressSync.LogTo(Session.DataDirectory,
                $"pending update apply failed: {e.Message}");
        }

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
