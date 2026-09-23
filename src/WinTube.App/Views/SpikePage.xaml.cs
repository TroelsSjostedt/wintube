using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinTube.App.Mpv;

namespace WinTube.App.Views;

/// TEMPORARY (libmpv stage, Task 3; deleted in Task 10). Smoke test for MpvPlayerHost: hosts it
/// in a plain container Grid, drives Load/pause/seek/volume/speed, and exercises the
/// construct-Load-Dispose lifecycle across repeated navigation (Unloaded disposes the host).
public sealed partial class SpikePage : Page
{
    private const string VideoId = "LXb3EKWsInQ";

    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherQueueTimer statusTimer;
    private MpvPlayerHost? host;
    private string lastEvent = "";

    public SpikePage()
    {
        InitializeComponent();
        statusTimer = dispatcher.CreateTimer();
        statusTimer.Interval = TimeSpan.FromMilliseconds(500);
        statusTimer.Tick += (_, _) => UpdateStatus();
        Unloaded += (_, _) => StopSession();
    }

    private async void OnPlay(object sender, RoutedEventArgs e)
    {
        if (host is not null) return;
        try
        {
            Status.Text = "resolving…";
            var stream = await App.Session.Streams.ResolveAsync(VideoId, null);
            SpikeLog.Write($"resolved client={stream.Client} url={stream.Url}");

            host = new MpvPlayerHost();
            host.Opened += () => lastEvent = "opened";
            host.EndReached += () => lastEvent = "end-reached";
            host.Errored += msg => lastEvent = $"error: {msg}";
            Container.Children.Add(host);
            host.Load(stream.Url.ToString(), 0, stream.UserAgent, null);
            statusTimer.Start();
        }
        catch (Exception ex)
        {
            SpikeLog.Write($"play failed: {ex}");
            Status.Text = $"failed: {ex.Message}";
        }
    }

    private void OnStop(object sender, RoutedEventArgs e) => StopSession();
    private void OnTogglePause(object sender, RoutedEventArgs e) => host?.TogglePause();
    private void OnSeekForward(object sender, RoutedEventArgs e) => host?.SeekTo(host.Position + 10);
    private void OnVolumeUp(object sender, RoutedEventArgs e) { if (host is not null) host.Volume = Math.Min(1.0, host.Volume + 0.1); }
    private void OnVolumeDown(object sender, RoutedEventArgs e) { if (host is not null) host.Volume = Math.Max(0.0, host.Volume - 0.1); }
    private void OnSpeedUp(object sender, RoutedEventArgs e) { if (host is not null) host.Speed = Math.Min(2.0, host.Speed + 0.25); }
    private void OnSpeedDown(object sender, RoutedEventArgs e) { if (host is not null) host.Speed = Math.Max(0.25, host.Speed - 0.25); }

    private void StopSession()
    {
        statusTimer.Stop();
        host?.Dispose();
        host = null;
        Container.Children.Clear();
        lastEvent = "";
        Status.Text = "stopped";
    }

    private void UpdateStatus()
    {
        if (host is null) return;
        Status.Text = $"pos={host.Position:F1} dur={host.Duration:F1} paused={host.IsPaused} " +
                      $"vol={host.Volume:F2} speed={host.Speed:F2} {lastEvent}";
    }
}

internal static class SpikeLog
{
    private static readonly object Gate = new();
    public static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "spike.log");

    public static void Write(string line)
    {
        lock (Gate) File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {line}\n");
    }
}
