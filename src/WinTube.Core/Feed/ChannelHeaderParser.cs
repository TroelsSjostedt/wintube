using System.Text.Json;
using WinTube.Core.InnerTube;

namespace WinTube.Core.Feed;

/// Reads a channel browse response's header: name, avatar, the 16:9 TV banner crop, and
/// whether the account subscribes (per the page's own subscribe button). Port of the tvOS
/// FeedService header readers.
public static class ChannelHeaderParser
{
    private static readonly string[] HeaderRenderers =
        ["channelHeaderRenderer", "c4TabbedHeaderRenderer"];

    public static string Title(JsonElement json)
    {
        foreach (var header in Renderers(json))
            if (header.TryGetProperty("title", out var title) &&
                Json.InnerTubeText(title) is { Length: > 0 } text)
                return text;
        return "";
    }

    public static string? AvatarUrl(JsonElement json)
    {
        foreach (var header in Renderers(json))
            if (Json.ValueAt(header, "avatar/thumbnails") is { ValueKind: JsonValueKind.Array } thumbs
                && LargestUrl(thumbs) is { } url)
                return url;
        return null;
    }

    /// TVHTML5 serves the 16:9 crop (urls carry fcrop64); rungs run 320x180 to 2120x1192.
    /// The narrowest rung covering 1920 wins, else the widest on offer.
    public static string? BannerUrl(JsonElement json)
    {
        foreach (var header in Renderers(json))
        {
            var thumbs = Json.ValueAt(header, "backgroundImage/thumbnails")
                ?? Json.ValueAt(header, "banner/thumbnails");
            if (thumbs is not { ValueKind: JsonValueKind.Array } list) continue;

            (string Url, int Width)? covering = null, widest = null;
            foreach (var thumb in list.EnumerateArray())
            {
                var url = Json.StringAt(thumb, "url");
                if (string.IsNullOrEmpty(url)) continue;
                if (url.StartsWith("//")) url = "https:" + url;
                var width = thumb.TryGetProperty("width", out var w) && w.TryGetInt32(out var i) ? i : 0;
                if (widest is null || width > widest.Value.Width) widest = (url, width);
                if (width >= 1920 && (covering is null || width < covering.Value.Width))
                    covering = (url, width);
            }
            if ((covering ?? widest) is { } best) return best.Url;
        }
        return null;
    }

    public static bool? IsSubscribed(JsonElement json)
    {
        bool? found = null;
        void Walk(JsonElement el)
        {
            if (found is not null) return;
            if (el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty("subscribeButtonRenderer", out var button)
                    && button.TryGetProperty("subscribed", out var subscribed)
                    && subscribed.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    found = subscribed.GetBoolean();
                    return;
                }
                foreach (var property in el.EnumerateObject()) Walk(property.Value);
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray()) Walk(item);
            }
        }
        Walk(json);
        return found;
    }

    /// Every channel header renderer in the response, in document order.
    private static IEnumerable<JsonElement> Renderers(JsonElement json)
    {
        var results = new List<JsonElement>();
        void Walk(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in HeaderRenderers)
                    if (el.TryGetProperty(name, out var renderer)) results.Add(renderer);
                foreach (var property in el.EnumerateObject()) Walk(property.Value);
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray()) Walk(item);
            }
        }
        Walk(json);
        return results;
    }

    private static string? LargestUrl(JsonElement thumbnails)
    {
        (string Url, int Width)? best = null;
        foreach (var thumb in thumbnails.EnumerateArray())
        {
            var url = Json.StringAt(thumb, "url");
            if (string.IsNullOrEmpty(url)) continue;
            if (url.StartsWith("//")) url = "https:" + url;
            var width = thumb.TryGetProperty("width", out var w) && w.TryGetInt32(out var i) ? i : 0;
            if (best is null || width > best.Value.Width) best = (url, width);
        }
        return best?.Url;
    }
}
