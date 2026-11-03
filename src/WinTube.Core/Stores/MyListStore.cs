using System.Text.Json;
using WinTube.Core.Models;

namespace WinTube.Core.Stores;

/// The user's local watch-later list ("My list"), kept per profile like WatchHistoryStore.
/// Holds full cards, not just ids — a saved video must render even if nothing else has ever
/// cached it. Local only: the Apple TV's own list is untouched.
public sealed class MyListStore(string rootDirectory, Func<DateTimeOffset>? clock = null)
{
    /// Own on-disk DTO (not VideoItem), so later model fields never break stored entries.
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

    /// Newest-added first.
    public IReadOnlyList<VideoItem> Items =>
        entries.OrderByDescending(pair => pair.Value.AddedAt)
            .Select(pair => pair.Value.Video(pair.Key)).ToList();

    /// Re-adding keeps the original position (AddedAt) but refreshes the card's fields.
    public void Add(VideoItem video)
    {
        if (profileId is null) return;
        var addedAt = entries.TryGetValue(video.Id, out var existing) ? existing.AddedAt : now();
        var entry = Entry.From(video, addedAt);
        if (entries.TryGetValue(video.Id, out var current) && current == entry) return;
        entries[video.Id] = entry;
        Persist();
        Changed?.Invoke();
    }

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
