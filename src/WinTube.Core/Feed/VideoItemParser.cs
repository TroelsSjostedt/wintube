using System.Text.Json;
using WinTube.Core.InnerTube;
using WinTube.Core.Models;

namespace WinTube.Core.Feed;

/// Turns any InnerTube response subtree into VideoItems. Both browse and search return the
/// same cells, so cell parsing lives here. Port of the tvOS VideoItemParser; the Swift file
/// is the arbiter when a rule here reads ambiguous.
public static class VideoItemParser
{
    private static readonly string[] ShapeKeys =
        ["tileRenderer", "lockupViewModel", "gridVideoRenderer", "videoRenderer", "reelItemRenderer"];

    public static IReadOnlyList<VideoItem> Items(JsonElement root, DateTimeOffset now)
    {
        var results = new List<VideoItem>();
        void Walk(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in ShapeKeys)
                    if (el.TryGetProperty(key, out var cell) &&
                        cell.ValueKind == JsonValueKind.Object &&
                        ParseCell(key, cell, now) is { } item)
                        results.Add(item);
                foreach (var property in el.EnumerateObject()) Walk(property.Value);
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray()) Walk(item);
            }
        }
        Walk(root);

        // Keep the first occurrence of each videoId — YouTube repeats videos freely.
        var seen = new HashSet<string>();
        return results.Where(item => seen.Add(item.Id)).ToList();
    }

    private static VideoItem? ParseCell(string key, JsonElement cell, DateTimeOffset now) =>
        key switch
        {
            "tileRenderer" => ParseTile(cell, now),
            "lockupViewModel" => ParseLockup(cell, now),
            _ => ParseVideoRenderer(cell, now),   // gridVideo / video / reelItem share a shape
        };

    // MARK: tileRenderer (the TV feed's cell)

    private static VideoItem? ParseTile(JsonElement tile, DateTimeOffset now)
    {
        var videoId = Json.StringAt(tile, "onSelectCommand/watchEndpoint/videoId")
            ?? Json.StringAt(tile, "onSelectCommand/reelWatchEndpoint/videoId");
        if (videoId is null) return null;   // tiles without one are channels/playlists

        // Only skip when we're sure it's not a video — some non-video types carry endpoints.
        if (Json.StringAt(tile, "contentType") is { } contentType &&
            contentType.StartsWith("TILE_CONTENT_TYPE_") &&
            (contentType.Contains("CHANNEL") || contentType.Contains("PLAYLIST")))
            return null;

        var thumbnails = Json.ValueAt(tile, "header/tileHeaderRenderer/thumbnail/thumbnails");
        var fragments = new List<string>();
        if (Json.ValueAt(tile, "metadata/tileMetadataRenderer/lines") is
            { ValueKind: JsonValueKind.Array } lines)
            foreach (var line in lines.EnumerateArray())
                if (Json.ValueAt(line, "lineRenderer/items") is
                    { ValueKind: JsonValueKind.Array } items)
                    foreach (var item in items.EnumerateArray())
                        if (Json.InnerTubeText(Json.ValueAt(item, "lineItemRenderer/text"))
                            is { } text)
                            fragments.Add(text);
        var subtitle = new Subtitle(fragments, now);

        return new VideoItem
        {
            Id = videoId,
            Title = Json.InnerTubeText(
                Json.ValueAt(tile, "metadata/tileMetadataRenderer/title")) ?? "",
            Author = subtitle.Author,
            ChannelId = ChannelId(tile),
            ThumbnailUrl = LargestThumbnailUrl(thumbnails) ?? VideoItem.FallbackThumbnail(videoId),
            ChannelAvatarUrl = ChannelAvatarUrl(tile),
            PublishedAt = subtitle.PublishedAt,
            ViewCount = subtitle.ViewCount,
            Duration = DurationOverlay(tile),
            IsShort = IsShortCell(tile, thumbnails),
        };
    }

    // MARK: lockupViewModel (the view-model cell search returns most hits in)

    private static VideoItem? ParseLockup(JsonElement lockup, DateTimeOffset now)
    {
        // Playlists and channels use the same cell with a non-video contentType, and their
        // contentId is a playlist/channel id — handing one to the player would 404.
        if (Json.StringAt(lockup, "contentType") != "LOCKUP_CONTENT_TYPE_VIDEO") return null;
        var videoId = Json.StringAt(lockup, "contentId") ?? Json.StringAt(lockup,
            "rendererContext/commandContext/onTap/innertubeCommand/watchEndpoint/videoId");
        if (videoId is null) return null;

        var thumbnails = Json.ValueAt(lockup, "contentImage/thumbnailViewModel/image/sources");
        var fragments = new List<string>();
        if (Json.ValueAt(lockup,
                "metadata/lockupMetadataViewModel/metadata/contentMetadataViewModel/metadataRows")
            is { ValueKind: JsonValueKind.Array } rows)
            foreach (var row in rows.EnumerateArray())
                if (Json.ValueAt(row, "metadataParts") is { ValueKind: JsonValueKind.Array } parts)
                    foreach (var part in parts.EnumerateArray())
                        if (Json.StringAt(part, "text/content") is { } text)
                            fragments.Add(text);
        var subtitle = new Subtitle(fragments, now);

        // The running time is a badge on the thumbnail; LIVE badges the same slot, so take
        // only what looks like a clock value.
        var duration = Json.FindAllRenderers(lockup, "thumbnailBadgeViewModel")
            .Select(badge => Json.StringAt(badge, "text"))
            .FirstOrDefault(text => text?.Contains(':') == true) ?? "";

        return new VideoItem
        {
            Id = videoId,
            Title = Json.StringAt(lockup, "metadata/lockupMetadataViewModel/title/content") ?? "",
            Author = subtitle.Author,
            ChannelId = ChannelId(lockup),
            ThumbnailUrl = LargestThumbnailUrl(thumbnails) ?? VideoItem.FallbackThumbnail(videoId),
            ChannelAvatarUrl = ChannelAvatarUrl(lockup),
            PublishedAt = subtitle.PublishedAt,
            ViewCount = subtitle.ViewCount,
            Duration = duration,
            IsShort = IsShortCell(lockup, thumbnails),
        };
    }

    // MARK: gridVideoRenderer / videoRenderer / reelItemRenderer (older shapes)

    private static VideoItem? ParseVideoRenderer(JsonElement renderer, DateTimeOffset now)
    {
        if (Json.StringAt(renderer, "videoId") is not { } videoId) return null;
        var thumbnails = Json.ValueAt(renderer, "thumbnail/thumbnails");

        DateTimeOffset? published = null;
        if (Json.InnerTubeText(Json.ValueAt(renderer, "publishedTimeText")) is { } age)
            published = RelativeTime.Parse(age, now);

        return new VideoItem
        {
            Id = videoId,
            // reelItemRenderer uses `headline` where videoRenderer uses `title`.
            Title = Json.InnerTubeText(Json.ValueAt(renderer, "title"))
                ?? Json.InnerTubeText(Json.ValueAt(renderer, "headline")) ?? "",
            Author = Json.InnerTubeText(Json.ValueAt(renderer, "longBylineText"))
                ?? Json.InnerTubeText(Json.ValueAt(renderer, "shortBylineText")) ?? "",
            ChannelId = ChannelId(renderer),
            ThumbnailUrl = LargestThumbnailUrl(thumbnails) ?? VideoItem.FallbackThumbnail(videoId),
            ChannelAvatarUrl = ChannelAvatarUrl(renderer),
            PublishedAt = published,
            ViewCount = Json.InnerTubeText(Json.ValueAt(renderer, "shortViewCountText"))
                ?? Json.InnerTubeText(Json.ValueAt(renderer, "viewCountText")) ?? "",
            Duration = Json.InnerTubeText(Json.ValueAt(renderer, "lengthText"))
                ?? DurationOverlay(renderer),
            IsShort = IsShortCell(renderer, thumbnails),
        };
    }

    // MARK: Shorts detection — no single field is on every cell shape; any signal is enough.

    public static bool IsShortCell(JsonElement cell, JsonElement? thumbnails)
    {
        if (Json.ContainsKey(cell, "reelWatchEndpoint")) return true;
        if (Json.StringAt(cell, "contentType")?.Contains("SHORT") == true) return true;
        if (Json.FindAllRenderers(cell, "thumbnailOverlayTimeStatusRenderer")
            .Any(overlay => Json.StringAt(overlay, "style") == "SHORTS")) return true;
        return IsPortrait(thumbnails);
    }

    /// Every sized entry must agree — the list is a ladder of renders of one image, so a
    /// single landscape entry means this isn't portrait artwork. A list with no usable
    /// dimensions is not evidence either way.
    private static bool IsPortrait(JsonElement? thumbnails)
    {
        if (thumbnails is not { ValueKind: JsonValueKind.Array } array) return false;
        var sawSized = false;
        foreach (var image in array.EnumerateArray())
        {
            var width = Json.ValueAt(image, "width") is { } w ? Json.IntValue(w) ?? 0 : 0;
            var height = Json.ValueAt(image, "height") is { } h ? Json.IntValue(h) ?? 0 : 0;
            if (width <= 0 || height <= 0) continue;
            sawSized = true;
            if (height <= width) return false;
        }
        return sawSized;
    }

    // MARK: shared helpers

    /// The three things worth showing, picked out of a cell's subtitle text. Slots vary by
    /// shelf and shape, and one slot often carries several values ("1.2M views • 3 days ago"),
    /// so everything is flattened to bullet-separated fragments and classified by shape.
    private readonly struct Subtitle
    {
        public string Author { get; }
        public string ViewCount { get; }
        public DateTimeOffset? PublishedAt { get; }

        public Subtitle(IEnumerable<string> fragments, DateTimeOffset now)
        {
            string author = "", views = "";
            DateTimeOffset? published = null;
            foreach (var fragment in fragments
                .SelectMany(f => f.Split('•', '·', '|'))
                .Select(f => f.Trim())
                .Where(f => f.Length > 0))
            {
                if (RelativeTime.Parse(fragment, now) is { } date)
                    published ??= date;
                else if (fragment.Contains("view", StringComparison.OrdinalIgnoreCase))
                    views = views.Length > 0 ? views : fragment;
                else if (author.Length == 0)
                    author = fragment;
            }
            (Author, ViewCount, PublishedAt) = (author, views, published);
        }
    }

    /// The running time stamped on the thumbnail. LIVE items carry the same renderer with
    /// "LIVE" in it — skip anything that doesn't look like a clock value.
    private static string DurationOverlay(JsonElement cell) =>
        Json.FindAllRenderers(cell, "thumbnailOverlayTimeStatusRenderer")
            .Select(overlay => Json.InnerTubeText(Json.ValueAt(overlay, "text")))
            .FirstOrDefault(text => text?.Contains(':') == true) ?? "";

    /// The UC… id of the cell's channel. Every shape carries it as a browseEndpoint somewhere
    /// different, so the cell is searched; the UC prefix separates channels from feed and
    /// playlist ids. Document order, first wins.
    private static string? ChannelId(JsonElement cell)
    {
        string? found = null;
        void Walk(JsonElement el)
        {
            if (found is not null) return;
            if (el.ValueKind == JsonValueKind.Object)
            {
                if (Json.StringAt(el, "browseEndpoint/browseId") is { } id && id.StartsWith("UC"))
                {
                    found = id;
                    return;
                }
                foreach (var property in el.EnumerateObject()) Walk(property.Value);
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray()) Walk(item);
            }
        }
        Walk(cell);
        return found;
    }

    /// The channel's avatar, found by what the URL looks like rather than by path: every cell
    /// shape buries it somewhere different, but all serve avatars from Google's
    /// profile-picture hosts, which nothing else in a video cell uses.
    private static string? ChannelAvatarUrl(JsonElement cell)
    {
        var candidates = new List<(string Url, int Width)>();
        void Walk(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in el.EnumerateObject())
                {
                    if (property.Name is "thumbnails" or "sources" &&
                        property.Value.ValueKind == JsonValueKind.Array)
                        foreach (var image in property.Value.EnumerateArray())
                            if (Json.StringAt(image, "url") is { } url)
                                candidates.Add((url, Json.ValueAt(image, "width") is { } w
                                    ? Json.IntValue(w) ?? 0 : 0));
                    Walk(property.Value);
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray()) Walk(item);
            }
        }
        Walk(cell);

        var avatars = candidates.Where(c =>
            c.Url.Contains("yt3.ggpht.com") || c.Url.Contains("yt3.googleusercontent.com") ||
            c.Url.Contains("/ytc/")).ToList();
        if (avatars.Count == 0) return null;

        // Smallest that still covers the card's circle; larger is wasted bytes, smaller is soft.
        const int wanted = 176;
        var covering = avatars.Where(a => a.Width >= wanted).OrderBy(a => a.Width).ToList();
        var best = covering.Count > 0 ? covering[0] : avatars.OrderByDescending(a => a.Width).First();

        var url = best.Url.StartsWith("//") ? "https:" + best.Url : best.Url;
        return best.Width < wanted ? Resized(url, wanted) : url;
    }

    /// Asks Google's image CDN for a bigger render by rewriting the `=s<digits>` size token.
    private static string Resized(string url, int size)
    {
        var marker = url.LastIndexOf("=s", StringComparison.Ordinal);
        if (marker < 0) return url;
        var start = marker + 2;
        var end = start;
        while (end < url.Length && char.IsAsciiDigit(url[end])) end++;
        return end == start ? url : url[..start] + size + url[end..];
    }

    /// The widest entry of an image list; protocol-relative URLs upgraded to https.
    private static string? LargestThumbnailUrl(JsonElement? thumbnails)
    {
        if (thumbnails is not { ValueKind: JsonValueKind.Array } array) return null;
        string? best = null;
        var bestWidth = -1;
        foreach (var image in array.EnumerateArray())
        {
            if (Json.StringAt(image, "url") is not { } url) continue;
            var width = Json.ValueAt(image, "width") is { } w ? Json.IntValue(w) ?? 0 : 0;
            if (width > bestWidth) (bestWidth, best) = (width, url);
        }
        return best is null ? null : best.StartsWith("//") ? "https:" + best : best;
    }
}
