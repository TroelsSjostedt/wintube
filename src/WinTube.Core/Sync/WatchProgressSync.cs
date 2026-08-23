using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using WinTube.Core.Stores;

namespace WinTube.Core.Sync;

/// Keeps a profile's watch history on the Appwrite backend. Strictly local-first: the store
/// remains the single source of truth; this only pushes what's there and folds in what came
/// back. Every failure is swallowed — an unreachable home server costs nothing but the sync.
/// Port of the tvOS WatchProgressSync.
[SupportedOSPlatform("windows")]
public sealed class WatchProgressSync
{
    private static readonly TimeSpan FlushDelay = TimeSpan.FromSeconds(15);
    private const int PageSize = 100;
    private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private sealed record Target(string SyncId, string AccountKey, string AccessToken);

    private readonly WatchProgressStore store;
    private readonly AppwriteClient client;
    private readonly AppwriteSessionStore sessions;
    private readonly string rootDirectory;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    private Target? target;
    private CancellationTokenSource activation = new();
    private CancellationTokenSource? debounce;
    private Task<string?>? signInTask;
    private readonly object signInGate = new();

    public Task? RunningTask { get; private set; }

    public WatchProgressSync(
        WatchProgressStore store,
        AppwriteClient client,
        AppwriteSessionStore sessions,
        string rootDirectory,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.store = store;
        this.client = client;
        this.sessions = sessions;
        this.rootDirectory = rootDirectory;
        this.delay = delay ?? Task.Delay;
        store.LocalChanged += ScheduleFlush;
    }

    // MARK: lifecycle

    /// Points the sync at a profile, then pulls and pushes once. Null any argument to
    /// deactivate. Cancelling the in-flight sign-in matters: left running, the incoming
    /// profile would be handed the outgoing one's user id and read that account's rows.
    public void Activate(string? profileId, string? accountKey, string? accessToken)
    {
        activation.Cancel();
        activation = new CancellationTokenSource();
        debounce?.Cancel();
        signInTask = null;

        if (profileId is null || accountKey is null || accessToken is null)
        {
            target = null;
            client.Session = null;
            return;
        }
        var syncId = profileId[..Math.Min(16, profileId.Length)];
        target = new Target(syncId, accountKey, accessToken);
        client.Session = sessions.Load(syncId);
        Sync();
    }

    /// Pulls anything new and pushes anything queued.
    public void Sync()
    {
        if (target is null) return;
        var ct = activation.Token;
        RunningTask = Task.Run(async () =>
        {
            await PullAsync(ct);
            await PushAsync(ct);
        }, CancellationToken.None);
    }

    /// Pushes now rather than waiting out the debounce — on player close and app exit,
    /// the two moments where "later" may never come.
    public void FlushNow()
    {
        debounce?.Cancel();
        if (target is null || store.Dirty.Count == 0) return;
        var ct = activation.Token;
        RunningTask = Task.Run(() => PushAsync(ct), CancellationToken.None);
    }

    private void ScheduleFlush()
    {
        if (target is null) return;
        debounce?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(activation.Token);
        debounce = cts;
        // The delay task is created synchronously with the local change, so a caller (and a
        // test) that just wrote an entry can observe the armed debounce deterministically.
        var wait = delay(FlushDelay, cts.Token);
        RunningTask = Task.Run(async () =>
        {
            try { await wait; }
            catch (OperationCanceledException) { return; }
            if (cts.Token.IsCancellationRequested) return;
            await PushAsync(cts.Token);
        }, CancellationToken.None);
    }

    // MARK: session

    /// Signs in if there is no session yet; concurrent callers share one attempt — two
    /// sign-ins racing is two Appwrite users being created for one account.
    private async Task<string?> AuthenticateAsync(CancellationToken ct)
    {
        if (target is not { } profile) return null;
        if (client.Session is { } session) return session.UserId;

        Task<string?> task;
        lock (signInGate)
        {
            task = signInTask ??= SignInOnceAsync(profile, ct);
        }
        var userId = await task;
        lock (signInGate)
        {
            if (ReferenceEquals(signInTask, task)) signInTask = null;
        }
        return target?.SyncId == profile.SyncId ? userId : null;
    }

    private async Task<string?> SignInOnceAsync(Target profile, CancellationToken ct)
    {
        try
        {
            var session = await client.SignInAsync(profile.AccessToken, profile.AccountKey, ct);
            if (ct.IsCancellationRequested || target?.SyncId != profile.SyncId) return null;
            client.Session = session;
            sessions.Save(profile.SyncId, session);
            return session.UserId;
        }
        catch (Exception e)
        {
            Log($"sign-in failed: {e.Message}");
            return null;
        }
    }

    /// Only acts if `profile` — the profile whose request just failed — is still the live
    /// target: a stale continuation from a profile Activate already switched away from must
    /// not null out the session (or delete the stored one) of whoever is active now.
    private void InvalidateSession(Target profile)
    {
        if (target?.SyncId != profile.SyncId) return;
        client.Session = null;
        sessions.Delete(profile.SyncId);
    }

    // MARK: pull

    private string MarkPath(string syncId) =>
        Path.Combine(rootDirectory, "profiles", syncId, "sync-pulled-at.txt");

    private async Task PullAsync(CancellationToken ct)
    {
        try
        {
            if (target is not { } profile) return;
            var userId = await AuthenticateAsync(ct);
            if (userId is null || ct.IsCancellationRequested || target?.SyncId != profile.SyncId)
                return;

            var markPath = MarkPath(profile.SyncId);
            string? since = File.Exists(markPath) ? File.ReadAllText(markPath) : null;
            string? cursor = null;
            string? latest = null;
            var merged = new Dictionary<string, ProgressEntry>();

            while (!ct.IsCancellationRequested)
            {
                var queries = new List<string>
                {
                    AppwriteQuery.Equal("userId", userId),
                    AppwriteQuery.OrderAsc("watchedAt"),
                    AppwriteQuery.Limit(PageSize),
                };
                if (since is not null) queries.Add(AppwriteQuery.GreaterThan("watchedAt", since));
                if (cursor is not null) queries.Add(AppwriteQuery.CursorAfter(cursor));

                IReadOnlyList<JsonElement> rows;
                try
                {
                    rows = await client.ListRowsAsync(queries, ct);
                }
                catch (AppwriteException e) when (e.IsUnauthorized)
                {
                    Log($"session rejected: {e.Message}");
                    InvalidateSession(profile);
                    return;   // the mark is not advanced — nothing was recorded as caught up
                }

                foreach (var row in rows)
                {
                    if (Entry(row) is not { } parsed) continue;
                    merged[parsed.VideoId] = parsed.Entry;
                    latest = parsed.WatchedAt;   // rows arrive ascending; the last one wins
                }
                if (rows.Count < PageSize) break;
                cursor = InnerTube.Json.StringAt(rows[^1], "$id");
                if (cursor is null) break;
            }

            if (ct.IsCancellationRequested || target?.SyncId != profile.SyncId) return;
            Log($"pulled {merged.Count} row(s); since={since ?? "never"}");
            if (merged.Count > 0) store.Merge(merged);
            // Nothing had ever been pulled → nothing has ever been pushed either: everything
            // this device already knows is owed to the backend.
            if (since is null) store.QueueAll(merged.Keys.ToHashSet());
            if (latest is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(markPath)!);
                File.WriteAllText(markPath, latest);
            }
        }
        catch (Exception e)
        {
            Log($"pull failed: {e.Message}");
        }
    }

    private static (string VideoId, ProgressEntry Entry, string WatchedAt)? Entry(JsonElement row)
    {
        var videoId = InnerTube.Json.StringAt(row, "videoId");
        var watchedAt = InnerTube.Json.StringAt(row, "watchedAt");
        if (videoId is null || watchedAt is null) return null;
        if (!row.TryGetProperty("position", out var p) || !p.TryGetDouble(out var position))
            return null;
        if (!row.TryGetProperty("duration", out var d) || !d.TryGetDouble(out var duration))
            return null;
        if (!DateTimeOffset.TryParse(watchedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var updatedAt)) return null;
        return (videoId, new ProgressEntry(position, duration, updatedAt), watchedAt);
    }

    // MARK: push

    /// Sequential on purpose: the queue is normally a handful of videos, and a home server
    /// on the far side of a domestic uplink is happier with one request at a time.
    private async Task PushAsync(CancellationToken ct)
    {
        try
        {
            if (target is not { } profile) return;
            var userId = await AuthenticateAsync(ct);
            if (userId is null || ct.IsCancellationRequested || target?.SyncId != profile.SyncId)
                return;

            // Snapshotted: playback can queue more videos while this loop awaits the
            // network, and those belong to the next flush.
            var queued = store.Dirty.ToList();
            var pushed = new Dictionary<string, DateTimeOffset>();
            foreach (var videoId in queued)
            {
                if (ct.IsCancellationRequested) break;
                if (!store.Entries.TryGetValue(videoId, out var entry)) continue;
                try
                {
                    await client.UpsertRowAsync(
                        rowId: $"{profile.SyncId}_{videoId}",
                        data: new Dictionary<string, object?>
                        {
                            ["userId"] = userId,
                            ["videoId"] = videoId,
                            ["position"] = entry.PositionSeconds,
                            ["duration"] = entry.DurationSeconds,
                            ["watchedAt"] = entry.UpdatedAt.UtcDateTime
                                .ToString(DateFormat, CultureInfo.InvariantCulture),
                        },
                        permissions:
                        [
                            $"read(\"user:{userId}\")",
                            $"update(\"user:{userId}\")",
                            $"delete(\"user:{userId}\")",
                        ], ct);
                    pushed[videoId] = entry.UpdatedAt;
                }
                catch (AppwriteException e) when (e.IsUnauthorized)
                {
                    Log($"session rejected: {e.Message}");
                    InvalidateSession(profile);
                    break;
                }
                catch (Exception e)
                {
                    Log($"push of {videoId} failed: {e.Message}");
                    break;   // stays queued; next flush retries
                }
            }

            Log($"pushed {pushed.Count} of {queued.Count} queued");
            if (pushed.Count > 0 && target?.SyncId == profile.SyncId)
                store.MarkSynced(pushed);
        }
        catch (Exception e)
        {
            Log($"push failed: {e.Message}");
        }
    }

    private static void Log(string message) =>
        System.Diagnostics.Debug.WriteLine($"[WatchProgressSync] {message}");
}
