using System.Text.Json;
using WinTube.Core.Models;

namespace WinTube.Core.Stores;

/// The cards behind the History page: what each watched video *is*, kept per profile. The
/// list of what was watched and when is WatchProgressStore; this holds the other half — a
/// card per video, from the player, the account's history list, or a metadata lookup.
/// WatchedAt says the video was played in this app (a video bailed out of after five seconds
/// records no position but still belongs in the history); a merely-cached card leaves it null.
public sealed class WatchHistoryStore(string rootDirectory, Func<DateTimeOffset>? clock = null)
{
    /// The on-disk card — its own DTO, not a stored VideoItem, so a model field added later
    /// never makes previously stored entries undecodable.
    private sealed record Entry(
        string Title, string Author, string? ChannelId, string? ThumbnailUrl,
        DateTimeOffset? PublishedAt, string ViewCount, string Duration, bool IsShort,
        DateTimeOffset? WatchedAt)
    {
        public static Entry From(VideoItem video, DateTimeOffset? watchedAt) => new(
            video.Title, video.Author, video.ChannelId, video.ThumbnailUrl,
            video.PublishedAt, video.ViewCount, video.Duration, video.IsShort, watchedAt);

        public VideoItem Video(string id) => new()
        {
            Id = id, Title = Title, Author = Author, ChannelId = ChannelId,
            ThumbnailUrl = ThumbnailUrl, PublishedAt = PublishedAt,
            ViewCount = ViewCount, Duration = Duration, IsShort = IsShort,
        };
    }

    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private Dictionary<string, Entry> entries = [];
    private string? profileId;

    public void Activate(string? newProfileId)
    {
        if (newProfileId == profileId) return;
        profileId = newProfileId;
        entries = profileId is null ? [] : Load(profileId);
    }

    /// The card for a video, or null when nothing here has ever seen it.
    public VideoItem? Card(string videoId) =>
        entries.TryGetValue(videoId, out var entry) ? entry.Video(videoId) : null;

    /// Videos played in this app, newest first.
    public IReadOnlyList<(string Id, DateTimeOffset WatchedAt)> WatchedHere =>
        entries.Where(pair => pair.Value.WatchedAt is not null)
            .Select(pair => (pair.Key, pair.Value.WatchedAt!.Value))
            .OrderByDescending(pair => pair.Item2).ToList();

    /// Records that a video is being watched here, now. A blank-titled item (a deep link with
    /// nothing but an id) keeps whatever card is already stored rather than overwriting it.
    public void Record(VideoItem video)
    {
        if (profileId is null) return;
        var card = video.Title.Length == 0 && entries.TryGetValue(video.Id, out var known)
            ? known
            : Entry.From(video, null);
        entries[video.Id] = card with { WatchedAt = now() };
        Persist();
    }

    /// Caches cards without claiming a watch. Skips empty titles (a failed lookup must not
    /// blank out what a feed already supplied) and skips the write when nothing changed —
    /// the account history is re-fetched on every visit.
    public void Remember(IEnumerable<VideoItem> videos)
    {
        if (profileId is null) return;
        var changed = false;
        foreach (var video in videos.Where(v => v.Title.Length > 0))
        {
            var existing = entries.TryGetValue(video.Id, out var e) ? e : null;
            var card = Entry.From(video, existing?.WatchedAt);
            if (card == existing) continue;
            entries[video.Id] = card;
            changed = true;
        }
        if (changed) Persist();
    }

    /// Drops cards the history no longer draws: everything neither watched here nor named.
    /// The cards worth keeping are exactly the ones some other list still points at.
    public void Prune(IReadOnlySet<string> keepIds)
    {
        var kept = entries.Where(pair => pair.Value.WatchedAt is not null ||
            keepIds.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
        if (kept.Count == entries.Count) return;
        entries = kept;
        Persist();
    }

    // MARK: storage — <root>\profiles\<profileId>\watch-history.json

    private string FilePath(string profile) =>
        Path.Combine(rootDirectory, "profiles", profile, "watch-history.json");

    private Dictionary<string, Entry> Load(string profile)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(
                File.ReadAllText(FilePath(profile))) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException) { return []; }
    }

    private void Persist()
    {
        if (profileId is null) return;
        var path = FilePath(profileId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(entries));
    }
}
