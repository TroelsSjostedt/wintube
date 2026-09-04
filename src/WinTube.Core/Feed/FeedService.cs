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
    private const string ShortsShelfTitle = "Shorts";

    public async Task<FeedPage> LoadHomeAsync(string accessToken, CancellationToken ct = default)
    {
        using var doc = await BrowseAsync(
            new Dictionary<string, object?> { ["browseId"] = "default" }, accessToken, ct);
        return ParsePage(doc.RootElement);
    }

    /// The account's own history feed. TVHTML5 hands it back pre-chunked into rows where only
    /// the first carries a header, so the untitled chunks are folded back into their row and
    /// the row is retitled to name the feed.
    public Task<FeedPage> LoadHistoryFeedAsync(string accessToken, CancellationToken ct = default) =>
        LoadSupplementaryAsync("FEhistory", "Continue watching", accessToken, ct);

    /// The account's subscriptions feed — same pre-chunked shape as History.
    public Task<FeedPage> LoadSubscriptionsFeedAsync(string accessToken, CancellationToken ct = default) =>
        LoadSupplementaryAsync("FEsubscriptions", "From your subscriptions", accessToken, ct);

    /// A supplementary feed: pre-chunked untitled rows folded back together, and the first
    /// non-Shorts row retitled to name the feed (a Shorts row already says what it holds).
    private async Task<FeedPage> LoadSupplementaryAsync(
        string browseId, string title, string accessToken, CancellationToken ct)
    {
        using var doc = await BrowseAsync(
            new Dictionary<string, object?> { ["browseId"] = browseId }, accessToken, ct);
        var page = ParsePage(doc.RootElement);
        var sections = FoldUntitledRows(page.Sections);
        var first = sections.FindIndex(section => !section.IsShorts);
        if (first >= 0)
            sections[first] = sections[first] with { Title = title };
        return page with { Sections = sections };
    }

    /// A channel is just another browseId; only the header parse is new.
    public async Task<ChannelPage> LoadChannelAsync(
        string channelId, string accessToken, CancellationToken ct = default)
    {
        using var doc = await BrowseAsync(
            new Dictionary<string, object?> { ["browseId"] = channelId }, accessToken, ct);
        var root = doc.RootElement;
        return new ChannelPage(
            ChannelHeaderParser.Title(root),
            ChannelHeaderParser.AvatarUrl(root),
            ChannelHeaderParser.BannerUrl(root),
            ChannelHeaderParser.IsSubscribed(root),
            ParsePage(root));
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
        var items = VideoItemParser.Items(doc.RootElement, DateTimeOffset.UtcNow);
        return new FeedRowPage(items, Continuation(doc.RootElement, RowContainers));
    }

    /// The tvOS Home collage: three feeds fetched in parallel, interleaved by a fixed rule.
    /// A failed supplementary feed just leaves its rows out; only the Home fetch throws.
    public async Task<CompositeHomePage> LoadCompositeHomeAsync(
        string accessToken, CancellationToken ct = default)
    {
        var homeTask = LoadHomeAsync(accessToken, ct);
        var subscriptionsTask = Quietly(LoadSubscriptionsFeedAsync(accessToken, ct));
        var historyTask = Quietly(LoadHistoryFeedAsync(accessToken, ct));
        var home = await homeTask;
        var subscriptions = await subscriptionsTask;
        var history = await historyTask;

        // Home's lead: everything up to and including the first non-Shorts row. A response
        // that is all Shorts rows leads with all of them.
        var firstNonShorts = home.Sections.ToList().FindIndex(section => !section.IsShorts);
        var leadCount = firstNonShorts >= 0 ? firstNonShorts + 1 : home.Sections.Count;

        var composed = new List<(FeedSection Section, bool IsHistory)>();
        composed.AddRange(home.Sections.Take(leadCount).Select(s => (s, false)));
        if (subscriptions is not null)
            composed.AddRange(subscriptions.Sections.Select(s => (s, false)));
        composed.AddRange(home.Sections.Skip(leadCount).Select(s => (s, false)));
        if (history is not null)
            composed.AddRange(history.Sections.Select(s => (s, true)));

        var deduped = DedupeShortsAcross(composed);
        return new CompositeHomePage(
            deduped.Select(entry => entry.Section).ToList(),
            home.Continuation,
            deduped.Count(entry => entry.IsHistory));
    }

    private static async Task<FeedPage?> Quietly(Task<FeedPage> fetch)
    {
        try { return await fetch; }
        catch { return null; }
    }

    /// A later Shorts row drops the ids an earlier one already shows; one emptied by that
    /// is removed entirely.
    private static List<(FeedSection Section, bool IsHistory)> DedupeShortsAcross(
        List<(FeedSection Section, bool IsHistory)> sections)
    {
        var shown = new HashSet<string>();
        var result = new List<(FeedSection, bool)>();
        foreach (var (section, isHistory) in sections)
        {
            if (!section.IsShorts)
            {
                result.Add((section, isHistory));
                continue;
            }
            var fresh = section.Items.Where(item => shown.Add(item.Id)).ToList();
            if (fresh.Count == 0) continue;
            result.Add((section with { Items = fresh }, isHistory));
        }
        return result;
    }

    private Task<JsonDocument> BrowseAsync(
        Dictionary<string, object?> parameters, string accessToken, CancellationToken ct) =>
        innerTube.PostAsync("browse", ClientKind.Tv, parameters, bearer: accessToken, ct: ct);

    // MARK: parsing

    /// Puts the Shorts lifted out of ordinary shelves where they belong: appended to the
    /// response's own Shorts row, or a row of their own at the end. Without this a Short
    /// filtered out of Recommended would simply disappear.
    private static List<FeedSection> Merging(List<VideoItem> shorts, List<FeedSection> sections)
    {
        if (shorts.Count == 0) return sections;
        shorts = shorts.DistinctBy(item => item.Id).ToList();
        var index = sections.FindIndex(section => section.IsShorts);
        if (index >= 0)
        {
            var existing = sections[index].Items.Select(item => item.Id).ToHashSet();
            var fresh = shorts.Where(item => !existing.Contains(item.Id))
                .Select(item => item with { IsShort = true }).ToList();
            if (fresh.Count == 0) return sections;
            sections[index] = sections[index] with
            {
                Items = [.. sections[index].Items, .. fresh],
            };
        }
        else
        {
            sections.Add(new FeedSection(
                Guid.NewGuid().ToString("N"), ShortsShelfTitle,
                shorts.Select(item => item with { IsShort = true }).ToList(),
                null, IsShorts: true));
        }
        return sections;
    }

    private static FeedPage ParsePage(JsonElement json)
    {
        var now = DateTimeOffset.UtcNow;
        var sections = new List<FeedSection>();
        // Shelves can nest; tracking emitted ids drops a shelf that only repeats an earlier
        // one without suppressing a video that legitimately appears in two rows.
        var emitted = new HashSet<string>();
        // Shorts pulled out of ordinary shelves, in the order they were met. Merged in below,
        // once it's known whether the response has a Shorts row of its own.
        var strayShorts = new List<VideoItem>();

        foreach (var (renderer, isReel) in FindShelves(json))
        {
            var items = VideoItemParser.Items(renderer, now);
            if (items.Count == 0) continue;
            if (items.All(item => emitted.Contains(item.Id))) continue;   // nested duplicate
            emitted.UnionWith(items.Select(item => item.Id));

            // The shelf's own kind first: a reel shelf is a Shorts row whatever its cells look
            // like, and so is one flying the Shorts glyph or holding nothing but Shorts.
            var isShorts = isReel || HasShortsIcon(renderer) || items.All(item => item.IsShort);
            var title = ShelfTitle(renderer) ?? "";
            var continuation = Continuation(renderer, RowContainers);

            if (isShorts)
            {
                sections.Add(new FeedSection(
                    Guid.NewGuid().ToString("N"),
                    title.Length == 0 ? ShortsShelfTitle : title,
                    items.Select(item => item with { IsShort = true }).ToList(),
                    continuation, IsShorts: true));
                continue;
            }

            strayShorts.AddRange(items.Where(item => item.IsShort));
            var videos = items.Where(item => !item.IsShort).ToList();
            // Everything in the shelf was a Short, and they're kept for the Shorts row.
            if (videos.Count == 0) continue;

            sections.Add(new FeedSection(
                Guid.NewGuid().ToString("N"), title, videos, continuation, IsShorts: false));
        }

        // Defensive fallback: no recognizable shelves — present everything playable as one
        // untitled row, Shorts still separated into their own.
        if (sections.Count == 0 && strayShorts.Count == 0)
        {
            var items = VideoItemParser.Items(json, now);
            strayShorts.AddRange(items.Where(item => item.IsShort));
            var videos = items.Where(item => !item.IsShort).ToList();
            if (videos.Count > 0)
                sections.Add(new FeedSection(
                    Guid.NewGuid().ToString("N"), "", videos, null, IsShorts: false));
        }

        return new FeedPage(
            Merging(strayShorts, sections), Continuation(json, SectionListContainers));
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
            if (section.IsShorts) { result.Add(section); continue; }
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
