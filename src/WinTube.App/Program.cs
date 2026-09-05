using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace WinTube.App;

/// Custom entry point so a second launch (a wintube:// activation, a command line) redirects
/// to the running instance instead of opening a second window.
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Velopack.VelopackApp.Build().Run();

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
