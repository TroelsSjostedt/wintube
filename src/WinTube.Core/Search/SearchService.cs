using System.Text.Json;
using WinTube.Core.Feed;
using WinTube.Core.InnerTube;
using WinTube.Core.Models;

namespace WinTube.Core.Search;

/// Search returns shelves; only the FIRST video-bearing one is the actual search results.
/// The shelves after it — "Previously watched" (the user's history matching the query, which
/// YouTube does not apply search filters to) and "Shorts" — would pollute the grid, visibly
/// so when a duration/date filter is active. Within the results shelf the structure is still
/// flattened: a flat relevance-ordered list is what the grid wants.
public sealed class SearchService(InnerTubeClient innerTube)
{
    public async Task<IReadOnlyList<VideoItem>> SearchAsync(
        string query, string accessToken, SearchFilters? filters = null,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["query"] = query };
        if (filters?.ToParams() is { } p) body["params"] = p;
        using var doc = await innerTube.PostAsync("search", ClientKind.Tv, body,
            bearer: accessToken, ct: ct);
        var now = DateTimeOffset.UtcNow;
        var items = FirstVideoBearingShelf(doc.RootElement, now)
            // No shelf structure at all (or none with videos) — fall back to the whole tree.
            ?? VideoItemParser.Items(doc.RootElement, now);
        return items.Where(item => !item.IsShort).ToList();
    }

    /// Document-order walk for shelfRenderer nodes; the first that parses to any videos wins
    /// (the ad shelf preceding the results parses to none).
    private static IReadOnlyList<VideoItem>? FirstVideoBearingShelf(
        JsonElement el, DateTimeOffset now)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in el.EnumerateObject())
            {
                if (property.Name == "shelfRenderer" &&
                    property.Value.ValueKind == JsonValueKind.Object)
                {
                    var items = VideoItemParser.Items(property.Value, now);
                    if (items.Count > 0) return items;
                    continue;   // an empty shelf never contains the results shelf
                }
                if (FirstVideoBearingShelf(property.Value, now) is { } found) return found;
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                if (FirstVideoBearingShelf(item, now) is { } found) return found;
        }
        return null;
    }
}
