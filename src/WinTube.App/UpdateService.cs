using Velopack;
using Velopack.Sources;

namespace WinTube.App;

/// Background self-update against the public GitHub releases. A dev run (not installed via
/// Velopack) reports IsInstalled false and the whole thing quietly does nothing; so does any
/// failure — an update check must never delay or break startup.
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/TroelsSjostedt/wintube";

    private UpdateManager? manager;
    private UpdateInfo? pending;

    /// Checks and downloads. Returns the version string ready to apply, or null. `progress`
    /// gets (target version, percent) once the download starts (0) and then on each whole-percent
    /// change; Velopack fires its callback often and from a background thread, so the callback
    /// here runs off the UI thread and the caller must marshal.
    public async Task<string?> CheckAsync(Action<string, int>? progress = null)
    {
        try
        {
            manager ??= new UpdateManager(new GithubSource(RepoUrl, null, false));
            if (!manager.IsInstalled) return null;
            pending = await manager.CheckForUpdatesAsync();
            if (pending is null) return null;
            var version = pending.TargetFullRelease.Version.ToString();
            var lastPercent = -1;
            void Report(int percent)
            {
                percent = Math.Clamp(percent, 0, 100);
                if (percent == lastPercent) return;
                lastPercent = percent;
                progress?.Invoke(version, percent);
            }
            Report(0);
            await manager.DownloadUpdatesAsync(pending, Report);
            return version;
        }
        catch (Exception e)
        {
            WinTube.Core.Sync.WatchProgressSync.LogTo(Session.DataDirectory,
                $"update check failed: {e.Message}");
            return null;
        }
    }

    /// The installed Velopack package version — the one true version, since the assembly
    /// version is never stamped. Null for a dev run (not installed via Velopack).
    public string? InstalledVersion()
    {
        try
        {
            manager ??= new UpdateManager(new GithubSource(RepoUrl, null, false));
            return manager.IsInstalled ? manager.CurrentVersion?.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// The GitHub release page for a version, or the latest-release page for a dev run.
    public static string ReleaseUrl(string? version) =>
        version is null ? $"{RepoUrl}/releases/latest" : $"{RepoUrl}/releases/tag/v{version}";

    /// Applies the downloaded update and restarts the app. No-op without a pending update.
    public void ApplyAndRestart()
    {
        if (pending is null) return;
        manager!.ApplyUpdatesAndRestart(pending.TargetFullRelease);
    }
}
