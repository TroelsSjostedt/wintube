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
    private Dictionary<string, ProgressEntry> entries = [];
    private HashSet<string> dirty = [];
    private string? profileId;

    public IReadOnlyDictionary<string, ProgressEntry> Entries => entries;
    public IReadOnlyCollection<string> Dirty => dirty;
    public event Action? Changed;

    /// Points the store at a profile's history. Null when nobody is signed in.
    public void Activate(string? newProfileId)
    {
        if (newProfileId == profileId) return;
        profileId = newProfileId;
        if (profileId is null) { entries = []; dirty = []; }
        else (entries, dirty) = Load(profileId);
        Changed?.Invoke();
    }

    public void Report(string videoId, double positionSeconds, double durationSeconds)
    {
        if (profileId is null || durationSeconds <= 0) return;
        if (positionSeconds < MinimumPosition) return;
        if (durationSeconds - positionSeconds <= EndThreshold) positionSeconds = durationSeconds;

        entries[videoId] = new ProgressEntry(positionSeconds, durationSeconds, now());
        dirty.Add(videoId);
        Persist();
        Changed?.Invoke();
    }

    /// Null when there is nothing to resume — no entry, or the video was finished.
    public double? ResumePosition(string videoId) =>
        entries.TryGetValue(videoId, out var entry) &&
        entry.PositionSeconds < entry.DurationSeconds ? entry.PositionSeconds : null;

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
