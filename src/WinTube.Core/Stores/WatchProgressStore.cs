using System.Text.Json;

namespace WinTube.Core.Stores;

public sealed record ProgressEntry(
    double PositionSeconds, double DurationSeconds, DateTimeOffset UpdatedAt);

/// Remembers how far into each video the user got, so playback resumes and cards can draw a
/// progress line. Plain JSON per profile — a position is not a secret and is cheap to lose.
/// The dirty set is persisted so a future sync stage can push a queue that survived restarts.
public sealed class WatchProgressStore(string rootDirectory, Func<DateTimeOffset>? clock = null)
{
    /// Below this a video counts as "opened", not watched.
    private const double MinimumPosition = 10;
    /// Within this of the end the video counts as finished: full line, replay from the start.
    private const double EndThreshold = 20;

    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly object gate = new();
    private Dictionary<string, ProgressEntry> entries = [];
    private HashSet<string> dirty = [];
    private string? profileId;

    /// Snapshot copy — callers (including the sync engine, from a thread-pool task) must
    /// never enumerate the live collection while Report/Merge/etc. may be mutating it.
    public IReadOnlyDictionary<string, ProgressEntry> Entries
    {
        get { lock (gate) return new Dictionary<string, ProgressEntry>(entries); }
    }

    public IReadOnlyCollection<string> Dirty
    {
        get { lock (gate) return dirty.ToList(); }
    }

    public event Action? Changed;

    /// Raised by Report only — a real local playback write, the thing a sync should push.
    /// Merges deliberately do not raise it: pushing back what the backend just sent would
    /// be a round trip that changes nothing.
    public event Action? LocalChanged;

    /// Points the store at a profile's history. Null when nobody is signed in.
    public void Activate(string? newProfileId)
    {
        lock (gate)
        {
            if (newProfileId == profileId) return;
            profileId = newProfileId;
            if (profileId is null) { entries = []; dirty = []; }
            else (entries, dirty) = Load(profileId);
        }
        Changed?.Invoke();
    }

    public void Report(string videoId, double positionSeconds, double durationSeconds)
    {
        lock (gate)
        {
            if (profileId is null || durationSeconds <= 0) return;
            if (positionSeconds < MinimumPosition) return;
            if (durationSeconds - positionSeconds <= EndThreshold) positionSeconds = durationSeconds;

            entries[videoId] = new ProgressEntry(positionSeconds, durationSeconds, now());
            dirty.Add(videoId);
            Persist();
        }
        Changed?.Invoke();
        LocalChanged?.Invoke();
    }

    /// Null when there is nothing to resume — no entry, or the video was finished.
    public double? ResumePosition(string videoId)
    {
        lock (gate)
        {
            return entries.TryGetValue(videoId, out var entry) &&
                entry.PositionSeconds < entry.DurationSeconds ? entry.PositionSeconds : null;
        }
    }

    // MARK: syncing

    /// Folds in what the backend has, keeping whichever version of each video is newer.
    /// Last-writer-wins by UpdatedAt — the right rule for one household: an unpushed local
    /// edit is newer than anything the backend can know about, so it wins and stays queued.
    public void Merge(IReadOnlyDictionary<string, ProgressEntry> remote)
    {
        bool changed;
        lock (gate)
        {
            if (profileId is null) return;
            changed = false;
            foreach (var (videoId, entry) in remote)
            {
                if (entries.TryGetValue(videoId, out var local) && local.UpdatedAt >= entry.UpdatedAt)
                    continue;
                entries[videoId] = entry;
                changed = true;
            }
            if (!changed) return;
            Persist();
        }
        Changed?.Invoke();
    }

    /// Queues everything the device already knows, so a history that predates syncing is
    /// uploaded rather than sitting there being older than a backend that never heard of it.
    /// Run once per profile, after its first successful pull; `except` names what that pull
    /// just took FROM the backend.
    public void QueueAll(IReadOnlySet<string> except)
    {
        lock (gate)
        {
            if (profileId is null) return;
            var owed = entries.Keys.Where(id => !except.Contains(id) && !dirty.Contains(id)).ToList();
            if (owed.Count == 0) return;
            foreach (var id in owed) dirty.Add(id);
            Persist();
        }
    }

    /// Drops pushed entries from the queue — but only those the user hasn't moved on from:
    /// a video still playing while its position uploads gets a newer UpdatedAt mid-flight,
    /// and clearing that would strand the newer position.
    public void MarkSynced(IReadOnlyDictionary<string, DateTimeOffset> pushed)
    {
        lock (gate)
        {
            if (profileId is null) return;
            foreach (var (videoId, updatedAt) in pushed)
                if (entries.TryGetValue(videoId, out var entry) && entry.UpdatedAt == updatedAt)
                    dirty.Remove(videoId);
            Persist();
        }
    }

    // MARK: storage — <root>\profiles\<profileId>\watch-progress.json

    private sealed record FileShape(Dictionary<string, ProgressEntry> Entries, HashSet<string> Dirty);

    private string FilePath(string profile) =>
        Path.Combine(rootDirectory, "profiles", profile, "watch-progress.json");

    private (Dictionary<string, ProgressEntry>, HashSet<string>) Load(string profile)
    {
        try
        {
            var shape = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(FilePath(profile)));
            return shape is null ? ([], []) : (shape.Entries, shape.Dirty);
        }
        catch (Exception e) when (e is IOException or JsonException) { return ([], []); }
    }

    private void Persist()
    {
        if (profileId is null) return;
        var path = FilePath(profileId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new FileShape(entries, dirty)));
    }
}
