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

    /// Checks and silently downloads. Returns the version string ready to apply, or null.
    public async Task<string?> CheckAsync()
    {
        try
        {
            manager ??= new UpdateManager(new GithubSource(RepoUrl, null, false));
            if (!manager.IsInstalled) return null;
            pending = await manager.CheckForUpdatesAsync();
            if (pending is null) return null;
            await manager.DownloadUpdatesAsync(pending);
            return pending.TargetFullRelease.Version.ToString();
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
