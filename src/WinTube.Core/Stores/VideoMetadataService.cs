using System.Text.Json;
using WinTube.Core.InnerTube;
using WinTube.Core.Models;

namespace WinTube.Core.Stores;

/// Looks a video up by id alone — the VISIONOS player call minus the streaming half. One
/// video per request (InnerTube has no batch form), which is why callers cache the answers
/// for good in WatchHistoryStore.
public sealed class VideoMetadataService(InnerTubeClient innerTube, VisitorDataStore visitorData)
{
    public async Task<VideoItem?> LoadAsync(string videoId, CancellationToken ct = default)
    {
        var vd = await TryToken(ct);
        using var first = await PostAsync(videoId, vd, ct);
        var details = Details(first.RootElement);
        if (details is null && vd is not null)
        {
            // An expired token looks exactly like never having sent one — retry once fresh.
            visitorData.Invalidate();
            using var second = await PostAsync(videoId, await TryToken(ct), ct);
            return Build(videoId, Details(second.RootElement));
        }
        return Build(videoId, details);
    }

    private async Task<string?> TryToken(CancellationToken ct)
    {
        try { return await visitorData.GetTokenAsync(ct); }
        catch (VisitorDataException) { return null; }
    }

    private Task<JsonDocument> PostAsync(string videoId, string? vd, CancellationToken ct) =>
        innerTube.PostAsync("player", ClientKind.VisionOs, new Dictionary<string, object?>
        {
            ["videoId"] = videoId,
            ["contentCheckOk"] = true,
            ["racyCheckOk"] = true,
        }, visitorData: vd, ct: ct);

    private static JsonElement? Details(JsonElement json) =>
        Json.ValueAt(json, "videoDetails") is { ValueKind: JsonValueKind.Object } d &&
        Json.StringAt(d, "title") is not null ? d : null;

    private static VideoItem? Build(string videoId, JsonElement? details)
    {
        if (details is not { } d) return null;
        var thumbs = Json.ValueAt(d, "thumbnail/thumbnails");
        var widest = thumbs is { ValueKind: JsonValueKind.Array } arr
            ? arr.EnumerateArray()
                .OrderByDescending(t =>
                    t.TryGetProperty("width", out var w) ? Json.IntValue(w) ?? 0 : 0)
                .Select(t => Json.StringAt(t, "url")).FirstOrDefault()
            : null;
        return new VideoItem
        {
            Id = videoId,
            Title = Json.StringAt(d, "title") ?? "",
            Author = Json.StringAt(d, "author") ?? "",
            ChannelId = Json.StringAt(d, "channelId"),
            ThumbnailUrl = widest ?? VideoItem.FallbackThumbnail(videoId),
            // No view count on purpose: videoDetails gives an exact number where every other
            // card shows YouTube's abbreviation, and two house styles is worse than none.
            Duration = DurationText(
                int.TryParse(Json.StringAt(d, "lengthSeconds"), out var s) ? s : 0),
        };
    }

    /// lengthSeconds as a thumbnail badge renders it — "21:55", "1:02:14"; "" for live.
    public static string DurationText(int totalSeconds)
    {
        if (totalSeconds <= 0) return "";
        var (h, m, s) = (totalSeconds / 3600, totalSeconds % 3600 / 60, totalSeconds % 60);
        return h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m}:{s:D2}";
    }
}
