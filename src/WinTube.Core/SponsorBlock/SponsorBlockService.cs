using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinTube.Core.SponsorBlock;

/// Fetches SponsorBlock segments for a video. SponsorBlock (https://sponsor.ajay.app, the
/// crowd-sourced database behind the browser extension) is public, unauthenticated and free.
///
/// Privacy: the videoId is never sent. The endpoint takes the first four hex characters of
/// its SHA-256 and returns every video whose hash starts with them; the match happens on
/// device. The server learns someone is watching one of ~1/65,536 of YouTube, not which video.
public sealed class SponsorBlockService(HttpClient http)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    /// Segments for the video in the given categories (default: the skippable four), sorted
    /// and merged. Empty on "nobody submitted anything" AND on every failure — this is an
    /// optional enhancement, and SponsorBlock trouble must never keep a video from playing.
    public async Task<IReadOnlyList<SponsorSegment>> FetchSegmentsAsync(
        string videoId,
        IReadOnlySet<SponsorCategory>? categories = null,
        CancellationToken ct = default)
    {
        categories ??= SponsorCategories.DefaultSkipped;
        if (categories.Count == 0) return [];
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(videoId)))
                .ToLowerInvariant();
            var categoriesJson = JsonSerializer.Serialize(
                categories.Select(c => c.ApiName()).OrderBy(n => n, StringComparer.Ordinal));
            var url = $"https://sponsor.ajay.app/api/skipSegments/{hash[..4]}"
                + $"?categories={Uri.EscapeDataString(categoriesJson)}"
                // "skip" only: SponsorBlock also has mute/poi/chapter entries, none of which
                // this app acts on — asking would only mean filtering them out again.
                + $"&actionTypes={Uri.EscapeDataString("[\"skip\"]")}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // SponsorBlock asks clients to identify themselves.
            request.Headers.TryAddWithoutValidation("User-Agent", "WinTube/0.1");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);

            using var response = await http.SendAsync(request, cts.Token);
            // 404 is the ordinary "no segments in this hash prefix" answer, not a failure.
            if (response.StatusCode != HttpStatusCode.OK) return [];
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

            // The response covers every video sharing the prefix; only ours is interesting.
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var matches = InnerTube.Json.StringAt(entry, "videoID") == videoId
                    || InnerTube.Json.StringAt(entry, "hash")?.ToLowerInvariant() == hash;
                if (!matches) continue;
                if (!entry.TryGetProperty("segments", out var raw) ||
                    raw.ValueKind != JsonValueKind.Array) return [];
                return SponsorSegment.Merge(
                    raw.EnumerateArray().Select(Parse).OfType<SponsorSegment>());
            }
            return [];
        }
        catch
        {
            return [];
        }
    }

    private static SponsorSegment? Parse(JsonElement segment)
    {
        var uuid = InnerTube.Json.StringAt(segment, "UUID");
        var categoryName = InnerTube.Json.StringAt(segment, "category");
        if (uuid is null || categoryName is null) return null;
        if (SponsorCategories.FromApiName(categoryName) is not { } category) return null;
        if (!segment.TryGetProperty("segment", out var bounds) ||
            bounds.ValueKind != JsonValueKind.Array || bounds.GetArrayLength() != 2 ||
            !bounds[0].TryGetDouble(out var start) || !bounds[1].TryGetDouble(out var end))
            return null;
        // Negative votes mean the community has disowned the submission — acting on a wrong
        // or malicious timestamp cuts real content.
        if (segment.TryGetProperty("votes", out var v) &&
            InnerTube.Json.IntValue(v) is < 0) return null;
        start = Math.Max(0, start);
        // Under a second is not worth a seek: the seek costs about as much as the segment.
        if (end - start < 1) return null;
        return new SponsorSegment(uuid, category, start, end);
    }
}
