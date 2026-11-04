using System.Text.Json;
using WinTube.Core.Models;

namespace WinTube.Core.Stores;

/// The user's single local "things to watch" list ("My list"), kept per profile like
/// WatchHistoryStore. Everything lands here the same way: the card's plus button and starting
/// a video both Touch the list (the user's call 2026-11-04 — starting a video IS choosing it),
/// the card's cross removes, and a video watched past the finished threshold drops out of the
/// VISIBLE list (Visible) while its entry stays — restarting it re-adds via Touch. Holds full
/// cards, not just ids — a saved video must render even if nothing else has ever cached it.
/// Local only: the Apple TV's own list is untouched.
public sealed class MyListStore(string rootDirectory, Func<DateTimeOffset>? clock = null)
{
    /// Past this fraction a video counts as finished and leaves the visible list — same
    /// threshold the retired Continue watching row used.
    private const double FinishedFraction = 0.95;

    /// Own on-disk DTO (not VideoItem), so later model fields never break stored entries.
    /// AddedAt keeps its name for file compatibility but now means "last touched".
    private sealed record Entry(
        string Title, string Author, string? ChannelId, string? ThumbnailUrl,
        DateTimeOffset? PublishedAt, string ViewCount, string Duration, bool IsShort,
        DateTimeOffset AddedAt)
    {
        public static Entry From(VideoItem video, DateTimeOffset addedAt) => new(
            video.Title, video.Author, video.ChannelId, video.ThumbnailUrl,
            video.PublishedAt, video.ViewCount, video.Duration, video.IsShort, addedAt);

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

    /// Raised after any mutation that changed the list — the Home row and the My list page
    /// listen to stay current without re-reading disk.
    public event Action? Changed;

    public void Activate(string? newProfileId)
    {
        if (newProfileId == profileId) return;
        profileId = newProfileId;
        entries = profileId is null ? [] : Load(profileId);
    }

    public bool Contains(string videoId) => entries.ContainsKey(videoId);

    /// Latest-touched first.
    public IReadOnlyList<VideoItem> Items =>
        entries.OrderByDescending(pair => pair.Value.AddedAt)
            .Select(pair => pair.Value.Video(pair.Key)).ToList();

    /// Adds the video, or bumps an existing entry to the front and refreshes its card.
    /// The plus button and starting playback both land here.
    public void Touch(VideoItem video)
    {
        if (profileId is null) return;
        var entry = Entry.From(video, now());
        if (entries.TryGetValue(video.Id, out var current) && current == entry) return;
        entries[video.Id] = entry;
        Persist();
        Changed?.Invoke();
    }

    /// One-time migration helper: inserts an entry at a given time, only when absent, without
    /// raising Changed — callers seed before any UI listens.
    public void Seed(VideoItem video, DateTimeOffset touchedAt)
    {
        if (profileId is null || entries.ContainsKey(video.Id)) return;
        entries[video.Id] = Entry.From(video, touchedAt);
        Persist();
    }

    /// The list as rows show it: finished videos (past FinishedFraction) are hidden, their
    /// entries kept — restarting one resets its position and it reappears on the next Touch
    /// anyway. Videos without usable progress always show.
    public static IReadOnlyList<VideoItem> Visible(
        IEnumerable<VideoItem> items, IReadOnlyDictionary<string, ProgressEntry> progress) =>
        items.Where(v =>
            !progress.TryGetValue(v.Id, out var entry) || entry.DurationSeconds <= 0 ||
            entry.PositionSeconds / entry.DurationSeconds < FinishedFraction).ToList();

    public void Remove(string videoId)
    {
        if (profileId is null || !entries.Remove(videoId)) return;
        Persist();
        Changed?.Invoke();
    }

    // MARK: storage — <root>\profiles\<profileId>\my-list.json

    private string FilePath(string profile) =>
        Path.Combine(rootDirectory, "profiles", profile, "my-list.json");

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
