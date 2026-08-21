using System.Text.Json;
using WinTube.Core.InnerTube;
using WinTube.Core.Models;

namespace WinTube.Core.Feed;

/// Fetches and parses TVHTML5 browse feeds. Port of the tvOS FeedService, minus Shorts:
/// v1 drops Shorts rows (and the Shorts mixed into ordinary shelves) instead of showing them.
public sealed class FeedService(InnerTubeClient innerTube)
{
    // Continuations are read off *named* containers only — a response carries tokens for both
    // axes (more shelves down, more videos right), and a blind search would happily paginate
    // the wrong one. First-page and continuation responses use different container names.
    private static readonly string[] SectionListContainers =
        ["sectionListRenderer", "sectionListContinuation"];
    private static readonly string[] RowContainers =
        ["horizontalListRenderer", "horizontalListContinuation", "gridRenderer", "gridContinuation"];
    private static readonly string[] TokenPaths =
        ["nextContinuationData/continuation", "reloadContinuationData/continuation"];

    public async Task<FeedPage> LoadHomeAsync(string accessToken, CancellationToken ct = default)
    {
        using var doc = await BrowseAsync(
            new Dictionary<string, object?> { ["browseId"] = "default" }, accessToken, ct);
        return ParsePage(doc.RootElement);
    }

    /// The account's own history feed. TVHTML5 hands it back pre-chunked into rows where only
    /// the first carries a header, so the untitled chunks are folded back into their row and
    /// the row is retitled to name the feed.
    public async Task<FeedPage> LoadHistoryFeedAsync(
        string accessToken, CancellationToken ct = default)
    {
        using var doc = await BrowseAsync(
            new Dictionary<string, object?> { ["browseId"] = "FEhistory" }, accessToken, ct);
        var page = ParsePage(doc.RootElement);
        var sections = FoldUntitledRows(page.Sections);
        if (sections.Count > 0)
            sections[0] = sections[0] with { Title = "Continue watching" };
        return page with { Sections = sections };
    }

    public async Task<FeedPage> LoadMoreShelvesAsync(
        string continuation, string accessToken, CancellationToken ct = default)
    {
        using var doc = await BrowseAsync(
            new Dictionary<string, object?> { ["continuation"] = continuation }, accessToken, ct);
        return ParsePage(doc.RootElement);
    }

    /// More videos for one row. The reply is a bare list of cells, not a section list.
    public async Task<FeedRowPage> LoadMoreItemsAsync(
        string continuation, string accessToken, CancellationToken ct = default)
    {
        using var doc = await BrowseAsync(
            new Dictionary<string, object?> { ["continuation"] = continuation }, accessToken, ct);
        var items = VideoItemParser.Items(doc.RootElement, DateTimeOffset.UtcNow)
            .Where(item => !item.IsShort).ToList();
        return new FeedRowPage(items, Continuation(doc.RootElement, RowContainers));
    }

    private Task<JsonDocument> BrowseAsync(
        Dictionary<string, object?> parameters, string accessToken, CancellationToken ct) =>
        innerTube.PostAsync("browse", ClientKind.Tv, parameters, bearer: accessToken, ct: ct);

    // MARK: parsing

    private static FeedPage ParsePage(JsonElement json)
    {
        var now = DateTimeOffset.UtcNow;
        var sections = new List<FeedSection>();
        // Shelves can nest; tracking emitted ids drops a shelf that only repeats an earlier
        // one without suppressing a video that legitimately appears in two rows.
        var emitted = new HashSet<string>();

        foreach (var (renderer, isReel) in FindShelves(json))
        {
            var items = VideoItemParser.Items(renderer, now);
            if (items.Count == 0) continue;
            if (items.All(item => emitted.Contains(item.Id))) continue;   // nested duplicate
            emitted.UnionWith(items.Select(item => item.Id));

            // The shelf's own kind first: a reel shelf is a Shorts row whatever its cells look
            // like, and so is one flying the Shorts glyph or holding nothing but Shorts.
            var isShorts = isReel || HasShortsIcon(renderer) || items.All(item => item.IsShort);
            if (isShorts) continue;   // v1 scope: Shorts rows are dropped, not shown

            var videos = items.Where(item => !item.IsShort).ToList();   // strays dropped too
            if (videos.Count == 0) continue;

            sections.Add(new FeedSection(
                Guid.NewGuid().ToString("N"), ShelfTitle(renderer) ?? "", videos,
                Continuation(renderer, RowContainers), IsShorts: false));
        }

        // Defensive fallback: no recognizable shelves — present everything playable as one
        // untitled row rather than showing nothing.
        if (sections.Count == 0)
        {
            var videos = VideoItemParser.Items(json, now).Where(item => !item.IsShort).ToList();
            if (videos.Count > 0)
                sections.Add(new FeedSection(
                    Guid.NewGuid().ToString("N"), "", videos, null, IsShorts: false));
        }

        return new FeedPage(sections, Continuation(json, SectionListContainers));
    }

    /// Every shelf-shaped renderer, in document order — which is the order YouTube wants the
    /// rows shown in. Both keys are probed on each object before recursing, so an outer shelf
    /// is always emitted before any shelf nested inside it (what the duplicate check assumes).
    private static List<(JsonElement Renderer, bool IsReel)> FindShelves(JsonElement json)
    {
        var results = new List<(JsonElement, bool)>();
        void Walk(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty("shelfRenderer", out var shelf) &&
                    shelf.ValueKind == JsonValueKind.Object)
                    results.Add((shelf, false));
                if (el.TryGetProperty("reelShelfRenderer", out var reel) &&
                    reel.ValueKind == JsonValueKind.Object)
                    results.Add((reel, true));
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

    /// The Shorts glyph on a shelf's own heading — the one dependable mark on a Shorts row
    /// whose cells claim nothing. Same string in every language, unlike the title.
    private static readonly string[] ShortsIconPaths =
    [
        "icon/iconType",
        "headerRenderer/shelfHeaderRenderer/icon/iconType",
        "header/shelfHeaderRenderer/icon/iconType",
    ];

    private static bool HasShortsIcon(JsonElement shelf) =>
        ShortsIconPaths.Any(path => Json.StringAt(shelf, path)?.Contains("SHORTS") == true);

    /// Shelf headings live under different renderers depending on the row type; the
    /// avatarLockup variant is what the TVHTML5 home feed uses.
    private static readonly string[] TitlePaths =
    [
        "headerRenderer/shelfHeaderRenderer/avatarLockup/avatarLockupRenderer/title",
        "headerRenderer/shelfHeaderRenderer/title",
        "headerRenderer/gridHeaderRenderer/title",
        "header/shelfHeaderRenderer/title",
        "title",
    ];

    private static string? ShelfTitle(JsonElement shelf) =>
        TitlePaths.Select(path => Json.InnerTubeText(Json.ValueAt(shelf, path)))
            .FirstOrDefault(text => !string.IsNullOrEmpty(text));

    /// First continuations[] token on any of the named containers within `scope`.
    private static string? Continuation(JsonElement scope, string[] containers)
    {
        foreach (var name in containers)
            foreach (var container in Json.FindAllRenderers(scope, name))
            {
                if (Json.ValueAt(container, "continuations") is not
                    { ValueKind: JsonValueKind.Array } list) continue;
                foreach (var entry in list.EnumerateArray())
                    foreach (var path in TokenPaths)
                        if (Json.StringAt(entry, path) is { } token)
                            return token;
            }
        return null;
    }

    /// Folds each untitled row into the nearest titled row above it, deduping repeats; an
    /// untitled row with no target stands alone (History's single row is this). The chunk's
    /// continuation outranks the host's, which stops at its own chunk.
    private static List<FeedSection> FoldUntitledRows(IReadOnlyList<FeedSection> sections)
    {
        var result = new List<FeedSection>();
        int? target = null;
        var seen = new HashSet<string>();

        foreach (var section in sections)
        {
            if (section.Title.Length > 0)
            {
                result.Add(section);
                target = result.Count - 1;
                seen = section.Items.Select(item => item.Id).ToHashSet();
                continue;
            }
            if (target is not { } index)
            {
                result.Add(section);
                continue;
            }
            var fresh = section.Items.Where(item => seen.Add(item.Id)).ToList();
            if (fresh.Count == 0) continue;
            var host = result[index];
            result[index] = host with
            {
                Items = [.. host.Items, .. fresh],
                Continuation = section.Continuation ?? host.Continuation,
            };
        }
        return result;
    }
}
