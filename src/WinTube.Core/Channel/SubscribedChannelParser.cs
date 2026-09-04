using System.Text.Json;
using WinTube.Core.InnerTube;
using WinTube.Core.Models;

namespace WinTube.Core.Channel;

/// Turns the `FEchannels` browse response — the "All subscriptions" grid — into
/// `SubscribedChannel`s. Port of the tvOS SubscribedChannelParser.
///
/// YouTube mixes cell shapes freely and changes them without notice, so every known shape is
/// collected in one pass rather than one being tried as a fallback for another. A channel cell
/// only has to yield a `UC…` id — that alone is enough to list the channel and open its page.
public static class SubscribedChannelParser
{
    /// The cell renderers a channel can arrive in. `tileRenderer` is what the TV client sends;
    /// the rest are shapes the same grid uses on other clients, kept because this response is
    /// undocumented and the app would otherwise show an empty screen the day it changes.
    private static readonly string[] CellKeys =
        ["tileRenderer", "gridChannelRenderer", "channelRenderer", "compactChannelRenderer", "lockupViewModel"];

    private static readonly string[] TitlePaths =
    [
        "metadata/tileMetadataRenderer/title",
        "metadata/lockupMetadataViewModel/title",
        "title",
        "headline",
        "displayName",
    ];

    /// Every channel cell in `json`, in document order, deduped by channel id.
    public static IReadOnlyList<SubscribedChannel> Channels(JsonElement json)
    {
        var results = new List<SubscribedChannel>();
        var seen = new HashSet<string>();

        void Walk(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in CellKeys)
                {
                    if (el.TryGetProperty(key, out var cell) && cell.ValueKind == JsonValueKind.Object &&
                        ParseCell(cell) is { } channel && seen.Add(channel.Id))
                        results.Add(channel);
                }
                // Sorted so a response that nests two cells under one dictionary — where there is
                // no document order to follow — always yields them the same way round.
                foreach (var property in Sorted(el)) Walk(property.Value);
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray()) Walk(item);
            }
        }
        Walk(json);

        return results;
    }

    /// Every `UC…` browse id in the response, first occurrence first. The walk takes object
    /// properties by sorted key — arbitrary, but the same on every run — since there is no
    /// document order between two branches of the same dictionary.
    public static IReadOnlyList<string> ChannelIdsInOrder(JsonElement json)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>();

        void Walk(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                if (Json.StringAt(el, "browseEndpoint/browseId") is { } id &&
                    id.StartsWith("UC", StringComparison.Ordinal) && seen.Add(id))
                    ids.Add(id);
                foreach (var property in Sorted(el)) Walk(property.Value);
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray()) Walk(item);
            }
        }
        Walk(json);

        return ids;
    }

    /// Returns null when the cell isn't a channel — the same renderers carry videos and
    /// playlists, and this grid is not guaranteed to hold only channels.
    private static SubscribedChannel? ParseCell(JsonElement cell)
    {
        if (cell.TryGetProperty("contentType", out var contentType) &&
            contentType.ValueKind == JsonValueKind.String &&
            contentType.GetString() is { } type &&
            (type.Contains("VIDEO") || type.Contains("PLAYLIST") || type.Contains("SHORT")))
            return null;

        if (ChannelId(cell) is not { } id) return null;

        return new SubscribedChannel(id, Title(cell), Avatar(cell), Detail(cell));
    }

    /// The first `UC…` browse id in the cell. Channel ids are the only `browseId` beginning
    /// with `UC` — playlist (`VL`/`PL`) and feed (`FE`) endpoints share the field.
    private static string? ChannelId(JsonElement cell)
    {
        string? found = null;

        void Walk(JsonElement el)
        {
            if (found is not null) return;
            if (el.ValueKind == JsonValueKind.Object)
            {
                if (Json.StringAt(el, "browseEndpoint/browseId") is { } id &&
                    id.StartsWith("UC", StringComparison.Ordinal))
                {
                    found = id;
                    return;
                }
                foreach (var property in Sorted(el)) Walk(property.Value);
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray()) Walk(item);
            }
        }
        Walk(cell);

        return found;
    }

    /// The channel's name, from whichever of the shapes' title slots this cell happens to use.
    private static string Title(JsonElement cell)
    {
        foreach (var path in TitlePaths)
            if (Text(Json.ValueAt(cell, path)) is { Length: > 0 } text)
                return text;
        return "";
    }

    /// The channel's picture. Every image in a channel cell is that channel's avatar, so this
    /// collects the cell's image lists and takes the widest entry.
    private static string? Avatar(JsonElement cell)
    {
        var images = new List<JsonElement>();

        void Walk(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "thumbnails", "sources" })
                    if (el.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array)
                        foreach (var item in list.EnumerateArray())
                            if (item.ValueKind == JsonValueKind.Object) images.Add(item);
                foreach (var property in el.EnumerateObject()) Walk(property.Value);
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray()) Walk(item);
            }
        }
        Walk(cell);

        (string Url, int Width)? best = null;
        foreach (var thumb in images)
        {
            var url = Json.StringAt(thumb, "url");
            if (string.IsNullOrEmpty(url)) continue;
            if (url.StartsWith("//")) url = "https:" + url;
            var width = thumb.TryGetProperty("width", out var w) && w.TryGetInt32(out var i) ? i : 0;
            if (best is null || width > best.Value.Width) best = (url, width);
        }
        return best?.Url;
    }

    /// The line under the name: a subscriber count where the cell gives one, and a video count
    /// otherwise. Which slot holds it varies by shape, so everything text-shaped in the cell is
    /// flattened to fragments and classified by what it says.
    private static string Detail(JsonElement cell)
    {
        var subscribers = "";
        var videos = "";

        foreach (var fragment in SubtitleFragments(cell))
        {
            if (fragment.Contains("subscriber", StringComparison.OrdinalIgnoreCase))
            {
                if (subscribers.Length == 0) subscribers = fragment;
            }
            else if (fragment.Contains("video", StringComparison.OrdinalIgnoreCase))
            {
                if (videos.Length == 0) videos = fragment;
            }
        }

        return subscribers.Length == 0 ? videos : subscribers;
    }

    /// Every piece of subtitle text in the cell, split on the bullets YouTube joins values with.
    /// The title is skipped: a channel called "Video Game Reviews" would otherwise read as a
    /// video count.
    private static List<string> SubtitleFragments(JsonElement cell)
    {
        var name = Title(cell);
        var fragments = new List<string>();

        void Collect(JsonElement? value)
        {
            if (Text(value) is not { Length: > 0 } text || text == name) return;
            fragments.AddRange(Split(text));
        }

        foreach (var key in new[] { "subscriberCountText", "videoCountText", "subtitle" })
            if (cell.TryGetProperty(key, out var value)) Collect(value);

        // A tile's counts sit in its metadata lines, unnamed, the same place a video card's
        // channel and view count come from.
        if (Json.ValueAt(cell, "metadata/tileMetadataRenderer/lines") is { ValueKind: JsonValueKind.Array } lines)
            foreach (var line in lines.EnumerateArray())
                if (Json.ValueAt(line, "lineRenderer/items") is { ValueKind: JsonValueKind.Array } items)
                    foreach (var item in items.EnumerateArray())
                        Collect(Json.ValueAt(item, "lineItemRenderer/text"));

        // And a lockup's in its metadata rows, likewise unnamed.
        if (Json.ValueAt(cell, "metadata/lockupMetadataViewModel/metadata/contentMetadataViewModel/metadataRows")
            is { ValueKind: JsonValueKind.Array } rows)
            foreach (var row in rows.EnumerateArray())
                if (row.TryGetProperty("metadataParts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                    foreach (var part in parts.EnumerateArray())
                        if (part.TryGetProperty("text", out var text)) Collect(text);

        return fragments;
    }

    private static List<string> Split(string text) =>
        text.Split(['•', '·', '|'])
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

    /// InnerTube's text objects and the view models' plain strings, read the same way: the
    /// renderers wrap text in `simpleText`/`runs`, the view models in `{"content": "…"}`, and a
    /// few fields are simply a string.
    private static string? Text(JsonElement? value)
    {
        if (value is not { } el) return null;
        if (el.ValueKind == JsonValueKind.String) return el.GetString()?.Trim();
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (Json.InnerTubeText(el) is { } inner) return inner.Trim();
            if (el.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                return content.GetString()?.Trim();
        }
        return null;
    }

    private static IOrderedEnumerable<JsonProperty> Sorted(JsonElement obj) =>
        obj.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal);
}
