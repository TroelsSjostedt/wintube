# WinTube Channels Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Channels and subscriptions, ported from tvOS: the composite Home (subscriptions + history rows woven in), channel pages with a subscribe toggle, a Subscriptions grid, and click-through from every card.

**Architecture:** `FeedService` gains the subscriptions feed, channel-page loading and the composite Home merge; a new `SubscriptionService` owns FEchannels and the two mutation endpoints; the App gets `ChannelPage` and `SubscriptionsPage` views plus channel links on `VideoCard` and `PlayerPage`.

**Tech Stack:** Existing only. No new packages.

**Spec:** `docs/specs/2026-09-04-wintube-channels-design.html` (approved 2026-09-04). Swift arbiters in the scratchpad metube checkout (re-clone https://github.com/claust/metube if gone): `YouTubeTV/Sources/Feed/FeedService.swift` (loadFeed/loadChannel/header parsers, HomeView merge), `YouTubeTV/Sources/Channel/SubscriptionService.swift`, `YouTubeTV/Sources/Channel/SubscribedChannelParser.swift`.

## Global Constraints

- All InnerTube calls go through the existing `InnerTubeClient.PostAsync` on `ClientKind.Tv` with the caller's Bearer token. No new headers, no visitorData on feeds.
- The composite order is fixed: Home shelves up to and including the first non-Shorts row → all subscriptions-feed rows → the rest of Home ("Load more" grows this part) → history rows last. A failed subscriptions or history fetch silently omits its rows; only the Home fetch can fail the page.
- Cross-feed Shorts dedupe: walking the composed sections in order, a later Shorts row drops any id an earlier Shorts row already shows; a Shorts row emptied by this is removed.
- The subscriptions feed folds untitled rows exactly like History (existing `FoldUntitledRows`) and retitles its first non-Shorts row `"From your subscriptions"`.
- `IsSubscribed` is `bool?`: `null` means the page carried no subscribe button and callers must not read it as "no" (the toggle stays hidden).
- FEchannels fallback: when no cells parse but `UC…` ids exist, list bare `SubscribedChannel(id)` entries in deterministic order rather than an empty screen.
- Channel entry points show only when the cell carried a `ChannelId` (`VideoItem.ChannelId`, already parsed).
- Tests: `dotnet test tests/WinTube.Core.Tests` (160 green before this plan). App build: `dotnet build src/WinTube.App -p:Platform=x64` (VS-lock rule: MSB3027-only failure → `-c Release` must be clean; never touch a VS process).
- Branch `master`; commit per task with the given message. Plan files are edited with the Edit tool or `[IO.File]::ReadAllText/WriteAllText` (PS 5.1 Get-Content mangles em-dashes).

## File Structure

```
src/WinTube.Core/Models/FeedModels.cs             MODIFY: ChannelPage, CompositeHomePage records
src/WinTube.Core/Models/SubscriptionModels.cs     CREATE: SubscribedChannel, SubscriptionListing
src/WinTube.Core/Feed/FeedService.cs              MODIFY: subscriptions feed, channel page, composite
src/WinTube.Core/Feed/ChannelHeaderParser.cs      CREATE: title/avatar/banner/subscribed from a channel response
src/WinTube.Core/Channel/SubscribedChannelParser.cs CREATE: FEchannels cells -> SubscribedChannel
src/WinTube.Core/Channel/SubscriptionService.cs   CREATE: subscribe/unsubscribe/load
src/WinTube.App/Session.cs                        MODIFY: expose Subscriptions service
src/WinTube.App/Views/HomePage.xaml.cs            MODIFY: composite home + insert-before-history paging
src/WinTube.App/Views/ChannelPage.xaml(.cs)       CREATE: banner/header/subscribe + feed rows
src/WinTube.App/Views/SubscriptionsPage.xaml(.cs) CREATE: grid of followed channels
src/WinTube.App/MainWindow.xaml(.cs)              MODIFY: Subscriptions nav item
src/WinTube.App/Controls/VideoCard.xaml(.cs)      MODIFY: author link + context menu
src/WinTube.App/Views/PlayerPage.xaml(.cs)        MODIFY: author link in title bar
src/WinTube.App/ChannelRequest.cs                 CREATE: navigation parameter record
tests/WinTube.Core.Tests/ChannelHeaderParserTests.cs   CREATE
tests/WinTube.Core.Tests/SubscribedChannelParserTests.cs CREATE
tests/WinTube.Core.Tests/SubscriptionServiceTests.cs   CREATE
tests/WinTube.Core.Tests/FeedServiceTests.cs      MODIFY: subscriptions feed + channel + composite tests
```

---

### Task 1: Core — subscriptions feed and channel page in FeedService

**Files:**
- Modify: `src/WinTube.Core/Models/FeedModels.cs`
- Create: `src/WinTube.Core/Feed/ChannelHeaderParser.cs`
- Modify: `src/WinTube.Core/Feed/FeedService.cs`
- Test: `tests/WinTube.Core.Tests/ChannelHeaderParserTests.cs`, `tests/WinTube.Core.Tests/FeedServiceTests.cs`

**Interfaces:**
- Consumes: existing `FeedService` internals (`BrowseAsync`, `ParsePage`, `FoldUntitledRows`), `Json` helpers.
- Produces:
  `public sealed record ChannelPage(string Title, string? AvatarUrl, string? BannerUrl, bool? IsSubscribed, FeedPage Feed);`
  `FeedService.LoadSubscriptionsFeedAsync(string accessToken, CancellationToken ct = default) -> Task<FeedPage>`
  `FeedService.LoadChannelAsync(string channelId, string accessToken, CancellationToken ct = default) -> Task<ChannelPage>`
  `ChannelHeaderParser.Title/AvatarUrl/BannerUrl/IsSubscribed(JsonElement)` statics.

- [ ] **Step 1: Write the failing tests**

Append to `tests/WinTube.Core.Tests/FeedServiceTests.cs`:

```csharp
    [Fact]
    public async Task SubscriptionsFeed_FoldsAndRetitles_LeavingShortsAlone()
    {
        var (feed, _) = Make("{\"contents\":{\"sectionListRenderer\":{\"contents\":[" +
            "{\"reelShelfRenderer\":{\"items\":[" + ShortTile("s1") + "]}}," +
            "{\"shelfRenderer\":{" +
            "\"headerRenderer\":{\"shelfHeaderRenderer\":{\"title\":{\"simpleText\":\"Today\"}}}," +
            "\"content\":{\"horizontalListRenderer\":{\"items\":[" + Tile("v1") + "]}}}}," +
            "{\"shelfRenderer\":{\"content\":{\"horizontalListRenderer\":{" +
            "\"items\":[" + Tile("v2") + "]}}}}]}}}");
        var page = await feed.LoadSubscriptionsFeedAsync("T");
        Assert.Equal(2, page.Sections.Count);
        Assert.True(page.Sections[0].IsShorts);
        Assert.Equal("Shorts", page.Sections[0].Title);
        Assert.Equal("From your subscriptions", page.Sections[1].Title);
        Assert.Equal(["v1", "v2"], page.Sections[1].Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Channel_ParsesHeaderAndShelves()
    {
        var (feed, _) = Make("{" +
            "\"header\":{\"channelHeaderRenderer\":{" +
            "\"title\":{\"simpleText\":\"Veritasium\"}," +
            "\"avatar\":{\"thumbnails\":[{\"url\":\"https://yt3.ggpht.com/a=s88\",\"width\":88}]}," +
            "\"banner\":{\"thumbnails\":[" +
            "{\"url\":\"//i.ytimg.com/banner-small\",\"width\":320}," +
            "{\"url\":\"https://i.ytimg.com/banner-big\",\"width\":2120}]}," +
            "\"subscribeButtonRenderer\":{\"subscribed\":true}}}," +
            "\"contents\":{\"sectionListRenderer\":{\"contents\":[{\"shelfRenderer\":{" +
            "\"headerRenderer\":{\"shelfHeaderRenderer\":{\"title\":{\"simpleText\":\"Videos\"}}}," +
            "\"content\":{\"horizontalListRenderer\":{\"items\":[" + Tile("v1") + "]}}}}]}}}");
        var page = await feed.LoadChannelAsync("UCx", "T");
        Assert.Equal("Veritasium", page.Title);
        Assert.Equal("https://yt3.ggpht.com/a=s88", page.AvatarUrl);
        Assert.Equal("https://i.ytimg.com/banner-big", page.BannerUrl);
        Assert.True(page.IsSubscribed);
        Assert.Equal("Videos", Assert.Single(page.Feed.Sections).Title);
    }

    [Fact]
    public async Task Channel_MissingHeaderPieces_AreNullNotWrong()
    {
        var (feed, _) = Make("{\"contents\":{\"sectionListRenderer\":{\"contents\":[" +
            "{\"shelfRenderer\":{\"content\":{\"horizontalListRenderer\":{" +
            "\"items\":[" + Tile("v1") + "]}}}}]}}}");
        var page = await feed.LoadChannelAsync("UCx", "T");
        Assert.Equal("", page.Title);
        Assert.Null(page.AvatarUrl);
        Assert.Null(page.BannerUrl);
        Assert.Null(page.IsSubscribed);   // no button is "unknown", never "no"
    }
```

New `tests/WinTube.Core.Tests/ChannelHeaderParserTests.cs`:

```csharp
using System.Text.Json;
using WinTube.Core.Feed;

namespace WinTube.Core.Tests;

public class ChannelHeaderParserTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Banner_PicksNarrowestRungCoveringAScreen_AndFixesSchemelessUrls()
    {
        var json = Parse("{\"c4TabbedHeaderRenderer\":{\"banner\":{\"thumbnails\":[" +
            "{\"url\":\"//host/b320\",\"width\":320}," +
            "{\"url\":\"//host/b1920\",\"width\":1920}," +
            "{\"url\":\"//host/b2120\",\"width\":2120}]}}}");
        Assert.Equal("https://host/b1920", ChannelHeaderParser.BannerUrl(json));
    }

    [Fact]
    public void Banner_FallsBackToWidest_WhenNothingCovers()
    {
        var json = Parse("{\"channelHeaderRenderer\":{\"backgroundImage\":{\"thumbnails\":[" +
            "{\"url\":\"https://host/b320\",\"width\":320}," +
            "{\"url\":\"https://host/b640\",\"width\":640}]}}}");
        Assert.Equal("https://host/b640", ChannelHeaderParser.BannerUrl(json));
    }

    [Fact]
    public void Subscribed_ReadsTheFirstButton_AndNullWhenNone()
    {
        Assert.False(ChannelHeaderParser.IsSubscribed(
            Parse("{\"a\":{\"subscribeButtonRenderer\":{\"subscribed\":false}}}")));
        Assert.Null(ChannelHeaderParser.IsSubscribed(Parse("{\"a\":1}")));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/WinTube.Core.Tests`
Expected: FAIL — `LoadSubscriptionsFeedAsync`, `LoadChannelAsync`, `ChannelHeaderParser` not defined.

- [ ] **Step 3: Implement**

Append to `src/WinTube.Core/Models/FeedModels.cs`:

```csharp
/// A channel's browse page: who it is, and its shelves in the same shape as any feed's.
/// IsSubscribed is null when the page carried no subscribe button — "unknown", never "no".
public sealed record ChannelPage(
    string Title, string? AvatarUrl, string? BannerUrl, bool? IsSubscribed, FeedPage Feed);
```

New `src/WinTube.Core/Feed/ChannelHeaderParser.cs` — port of the tvOS header readers
(`channelTitle`/`channelAvatarURL`/`channelBannerURL`/`subscribedState` in FeedService.swift).
Every reader searches the whole response for the header renderers rather than assuming a path:

```csharp
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
            if (Json.InnerTubeText(Json.ValueAt(header, "title")) is { Length: > 0 } title)
                return title;
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
```

NOTE: check `Json`'s actual helper names before writing — `Json.ValueAt` returns `JsonElement?`
in this codebase; adjust the null-pattern matches to its real signatures (see existing usages in
`VideoItemParser.cs`).

In `src/WinTube.Core/Feed/FeedService.cs`, extract the History fold+retitle into a shared
private and add the two loaders:

```csharp
    public Task<FeedPage> LoadSubscriptionsFeedAsync(string accessToken, CancellationToken ct = default) =>
        LoadSupplementaryAsync("FEsubscriptions", "From your subscriptions", accessToken, ct);

    public Task<FeedPage> LoadHistoryFeedAsync(string accessToken, CancellationToken ct = default) =>
        LoadSupplementaryAsync("FEhistory", "Continue watching", accessToken, ct);

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
```

(The old `LoadHistoryFeedAsync` body is replaced by the shared private; behavior is identical
and the existing history tests must stay green.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/WinTube.Core.Tests`
Expected: PASS, including all pre-existing tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: subscriptions feed and channel pages in FeedService"
```

---

### Task 2: Core — SubscribedChannelParser and SubscriptionService

**Files:**
- Create: `src/WinTube.Core/Models/SubscriptionModels.cs`
- Create: `src/WinTube.Core/Channel/SubscribedChannelParser.cs`
- Create: `src/WinTube.Core/Channel/SubscriptionService.cs`
- Test: `tests/WinTube.Core.Tests/SubscribedChannelParserTests.cs`, `tests/WinTube.Core.Tests/SubscriptionServiceTests.cs`

**Interfaces:**
- Consumes: `InnerTubeClient.PostAsync`, `Json` helpers.
- Produces:
  `public sealed record SubscribedChannel(string Id, string Title = "", string? AvatarUrl = null, string Detail = "");`
  `public sealed record SubscriptionListing(IReadOnlySet<string> ChannelIds, IReadOnlyList<SubscribedChannel> Channels);`
  `SubscriptionService(InnerTubeClient)` with `SubscribeAsync(string channelId, string accessToken, ct)`, `UnsubscribeAsync(...)` (both `Task`), `LoadSubscriptionsAsync(string accessToken, ct) -> Task<SubscriptionListing>`.
  `SubscribedChannelParser.Channels(JsonElement) -> IReadOnlyList<SubscribedChannel>`, `SubscribedChannelParser.ChannelIdsInOrder(JsonElement) -> IReadOnlyList<string>`.

- [ ] **Step 1: Write the failing tests**

New `tests/WinTube.Core.Tests/SubscribedChannelParserTests.cs`:

```csharp
using System.Text.Json;
using WinTube.Core.Channel;

namespace WinTube.Core.Tests;

public class SubscribedChannelParserTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Channels_ParsesTvTiles_InDocumentOrder_DedupedById()
    {
        var json = Parse("{\"items\":[" +
            "{\"tileRenderer\":{" +
            "\"onSelectCommand\":{\"browseEndpoint\":{\"browseId\":\"UCaaa\"}}," +
            "\"metadata\":{\"tileMetadataRenderer\":{\"title\":{\"simpleText\":\"Vsauce\"}," +
            "\"lines\":[{\"lineRenderer\":{\"items\":[{\"lineItemRenderer\":{" +
            "\"text\":{\"simpleText\":\"1.2M subscribers\"}}}]}}]}}," +
            "\"thumbnail\":{\"thumbnails\":[{\"url\":\"https://yt3.ggpht.com/x=s176\",\"width\":176}]}}}," +
            "{\"tileRenderer\":{" +
            "\"onSelectCommand\":{\"browseEndpoint\":{\"browseId\":\"UCaaa\"}}," +
            "\"metadata\":{\"tileMetadataRenderer\":{\"title\":{\"simpleText\":\"Vsauce again\"}}}}}]}");
        var channel = Assert.Single(SubscribedChannelParser.Channels(json));
        Assert.Equal("UCaaa", channel.Id);
        Assert.Equal("Vsauce", channel.Title);
        Assert.Equal("https://yt3.ggpht.com/x=s176", channel.AvatarUrl);
        Assert.Equal("1.2M subscribers", channel.Detail);
    }

    [Fact]
    public void Channels_RejectsVideoShapedCells()
    {
        var json = Parse("{\"items\":[{\"tileRenderer\":{" +
            "\"contentType\":\"TILE_CONTENT_TYPE_VIDEO\"," +
            "\"onSelectCommand\":{\"browseEndpoint\":{\"browseId\":\"UCbbb\"}}}}]}");
        Assert.Empty(SubscribedChannelParser.Channels(json));
    }

    [Fact]
    public void ChannelIdsInOrder_CollectsEveryUcId_FirstOccurrenceFirst()
    {
        var json = Parse("{\"a\":[{\"browseEndpoint\":{\"browseId\":\"UCzz\"}}," +
            "{\"browseEndpoint\":{\"browseId\":\"FEhistory\"}}," +
            "{\"browseEndpoint\":{\"browseId\":\"UCaa\"}}," +
            "{\"browseEndpoint\":{\"browseId\":\"UCzz\"}}]}");
        Assert.Equal(["UCzz", "UCaa"], SubscribedChannelParser.ChannelIdsInOrder(json));
    }
}
```

New `tests/WinTube.Core.Tests/SubscriptionServiceTests.cs`:

```csharp
using WinTube.Core;
using WinTube.Core.Channel;
using WinTube.Core.InnerTube;

namespace WinTube.Core.Tests;

public class SubscriptionServiceTests
{
    private static (SubscriptionService Service, StubHttpHandler Handler) Make(string response)
    {
        var handler = new StubHttpHandler((_, _) => StubHttpHandler.JsonResponse(response));
        var client = new InnerTubeClient(new HttpClient(handler), new Secrets("K", "", ""));
        return (new SubscriptionService(client), handler);
    }

    [Fact]
    public async Task Subscribe_PostsTheChannelIdList_WithBearer()
    {
        var (service, handler) = Make("{}");
        await service.SubscribeAsync("UCx", "TOKEN");
        var (message, body) = Assert.Single(handler.Requests);
        Assert.Contains("/youtubei/v1/subscription/subscribe", message.RequestUri!.ToString());
        Assert.Contains("\"channelIds\":[\"UCx\"]", body);
        Assert.Equal("TOKEN", message.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task Unsubscribe_PostsToTheUnsubscribeEndpoint()
    {
        var (service, handler) = Make("{}");
        await service.UnsubscribeAsync("UCx", "TOKEN");
        Assert.Contains("/youtubei/v1/subscription/unsubscribe",
            Assert.Single(handler.Requests).Message.RequestUri!.ToString());
    }

    [Fact]
    public async Task LoadSubscriptions_ReturnsParsedCells_AndTheIdSet()
    {
        var (service, handler) = Make("{\"items\":[{\"tileRenderer\":{" +
            "\"onSelectCommand\":{\"browseEndpoint\":{\"browseId\":\"UCaaa\"}}," +
            "\"metadata\":{\"tileMetadataRenderer\":{\"title\":{\"simpleText\":\"Vsauce\"}}}}}]}");
        var listing = await service.LoadSubscriptionsAsync("TOKEN");
        Assert.Contains("\"browseId\":\"FEchannels\"", Assert.Single(handler.Requests).Body);
        Assert.Equal("Vsauce", Assert.Single(listing.Channels).Title);
        Assert.Equal(new HashSet<string> { "UCaaa" }, listing.ChannelIds);
    }

    [Fact]
    public async Task LoadSubscriptions_FallsBackToBareIds_WhenNoCellParses()
    {
        var (service, _) = Make("{\"weird\":[{\"browseEndpoint\":{\"browseId\":\"UCaaa\"}}," +
            "{\"browseEndpoint\":{\"browseId\":\"UCbbb\"}}]}");
        var listing = await service.LoadSubscriptionsAsync("TOKEN");
        Assert.Equal(["UCaaa", "UCbbb"], listing.Channels.Select(c => c.Id));
        Assert.All(listing.Channels, c => Assert.Equal("", c.Title));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/WinTube.Core.Tests`
Expected: FAIL — types not defined.

- [ ] **Step 3: Implement**

New `src/WinTube.Core/Models/SubscriptionModels.cs`:

```csharp
namespace WinTube.Core.Models;

/// One channel the account follows, as the FEchannels grid lists it. Only Id is guaranteed —
/// a cell that parses no further still lists the channel and opens its page.
public sealed record SubscribedChannel(
    string Id, string Title = "", string? AvatarUrl = null, string Detail = "");

/// Both readings of the one FEchannels response: the id set (membership tests) and the
/// named, pictured channels in YouTube's own order (the Subscriptions screen).
public sealed record SubscriptionListing(
    IReadOnlySet<string> ChannelIds, IReadOnlyList<SubscribedChannel> Channels);
```

New `src/WinTube.Core/Channel/SubscribedChannelParser.cs` — port of the Swift parser. Cell
renderers tried: `tileRenderer`, `gridChannelRenderer`, `channelRenderer`,
`compactChannelRenderer`, `lockupViewModel`. Rules, all from the Swift source:

- A cell whose `contentType` contains `VIDEO`, `PLAYLIST` or `SHORT` is rejected.
- The id is the first `browseEndpoint/browseId` starting with `UC` found anywhere in the cell
  (walk objects by sorted property name for determinism, arrays in order). No id, no channel.
- Title: first non-empty of `metadata/tileMetadataRenderer/title`,
  `metadata/lockupMetadataViewModel/title`, `title`, `headline`, `displayName` — each read as
  InnerTube text, `{"content": "..."}` view-model text, or a bare string.
- Avatar: collect every entry of every `thumbnails`/`sources` array in the cell and take the
  largest by width (every image in a channel cell is the avatar; scheme-less `//` urls get
  `https:`).
- Detail: flatten subtitle text (named fields `subscriberCountText`, `videoCountText`,
  `subtitle`, plus tile metadata `lines` and lockup `metadataRows`), split fragments on
  `•`, `·`, `|`, skip the title itself, and return the first fragment containing
  "subscriber" (case-insensitive), else the first containing "video", else "".
- `Channels(json)`: walk the whole response, collecting each recognized cell in document order,
  deduped by id.
- `ChannelIdsInOrder(json)`: every `UC…` browse id in the response, first occurrence first,
  walking objects by sorted key (matches the Swift determinism rule).

New `src/WinTube.Core/Channel/SubscriptionService.cs`:

```csharp
using WinTube.Core.InnerTube;
using WinTube.Core.Models;

namespace WinTube.Core.Channel;

/// Subscribing, unsubscribing, and reading back which channels the account follows.
/// Same TV client and Bearer token the feeds use; the mutations take a list of channel ids,
/// of which this app only ever sends one. Success is any 2xx — the response body carries
/// nothing the caller needs.
public sealed class SubscriptionService(InnerTubeClient innerTube)
{
    public async Task SubscribeAsync(string channelId, string accessToken, CancellationToken ct = default)
    {
        using var _ = await innerTube.PostAsync("subscription/subscribe", ClientKind.Tv,
            new Dictionary<string, object?> { ["channelIds"] = new[] { channelId } },
            bearer: accessToken, ct: ct);
    }

    public async Task UnsubscribeAsync(string channelId, string accessToken, CancellationToken ct = default)
    {
        using var _ = await innerTube.PostAsync("subscription/unsubscribe", ClientKind.Tv,
            new Dictionary<string, object?> { ["channelIds"] = new[] { channelId } },
            bearer: accessToken, ct: ct);
    }

    /// FEchannels — the "All subscriptions" grid. Cells that parse are the listing; when none
    /// do but UC ids exist, they are listed bare rather than showing an empty screen.
    public async Task<SubscriptionListing> LoadSubscriptionsAsync(
        string accessToken, CancellationToken ct = default)
    {
        using var doc = await innerTube.PostAsync("browse", ClientKind.Tv,
            new Dictionary<string, object?> { ["browseId"] = "FEchannels" },
            bearer: accessToken, ct: ct);
        var ordered = SubscribedChannelParser.ChannelIdsInOrder(doc.RootElement);
        var parsed = SubscribedChannelParser.Channels(doc.RootElement);
        var channels = parsed.Count > 0
            ? parsed
            : ordered.Select(id => new SubscribedChannel(id)).ToList();
        return new SubscriptionListing(ordered.ToHashSet(), channels);
    }
}
```

NOTE: `InnerTubeClient.PostAsync` serializes `["channelIds"] = new[] { channelId }` through
`JsonSerializer.Serialize` — verify the body comes out as `"channelIds":["UCx"]` (the test
asserts it).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/WinTube.Core.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: subscription service and FEchannels parsing"
```

---

### Task 3: Core — the composite Home

**Files:**
- Modify: `src/WinTube.Core/Models/FeedModels.cs`
- Modify: `src/WinTube.Core/Feed/FeedService.cs`
- Test: `tests/WinTube.Core.Tests/FeedServiceTests.cs`

**Interfaces:**
- Consumes: `LoadHomeAsync`, `LoadSubscriptionsFeedAsync`, `LoadHistoryFeedAsync` (Tasks 1).
- Produces:
  `public sealed record CompositeHomePage(IReadOnlyList<FeedSection> Sections, string? Continuation, int HistoryRowCount);`
  `FeedService.LoadCompositeHomeAsync(string accessToken, CancellationToken ct = default) -> Task<CompositeHomePage>`

- [ ] **Step 1: Write the failing tests**

The composite needs per-request responses. Add this helper to `FeedServiceTests`:

```csharp
    /// Routes each browse request by the browseId in its body; unrouted ids fail with 500.
    private static FeedService MakeRouted(Dictionary<string, string> responsesByBrowseId)
    {
        var handler = new StubHttpHandler((_, body) =>
        {
            foreach (var (browseId, response) in responsesByBrowseId)
                if (body.Contains($"\"browseId\":\"{browseId}\""))
                    return StubHttpHandler.JsonResponse(response);
            return StubHttpHandler.JsonResponse("{}", 500);
        });
        return new FeedService(new InnerTubeClient(new HttpClient(handler), new Secrets("K", "", "")));
    }

    private static string Shelf(string title, params string[] tiles) =>
        "{\"shelfRenderer\":{" +
        "\"headerRenderer\":{\"shelfHeaderRenderer\":{\"title\":{\"simpleText\":\"" + title + "\"}}}," +
        "\"content\":{\"horizontalListRenderer\":{\"items\":[" + string.Join(",", tiles) + "]}}}}";

    private static string SectionList(params string[] shelves) =>
        "{\"contents\":{\"sectionListRenderer\":{\"contents\":[" + string.Join(",", shelves) + "]}}}";
```

Then the tests:

```csharp
    [Fact]
    public async Task CompositeHome_InterleavesInTheTvosOrder()
    {
        var feed = MakeRouted(new()
        {
            ["default"] = SectionList(Shelf("Recommended", Tile("h1")), Shelf("New to you", Tile("h2"))),
            ["FEsubscriptions"] = SectionList(Shelf("Today", Tile("s1"))),
            ["FEhistory"] = SectionList(Shelf("x", Tile("w1"))),
        });
        var page = await feed.LoadCompositeHomeAsync("T");
        Assert.Equal(
            ["Recommended", "From your subscriptions", "New to you", "Continue watching"],
            page.Sections.Select(s => s.Title));
        Assert.Equal(1, page.HistoryRowCount);
    }

    [Fact]
    public async Task CompositeHome_SupplementaryFailuresAreSilent()
    {
        var feed = MakeRouted(new()
        {
            ["default"] = SectionList(Shelf("Recommended", Tile("h1"))),
            // FEsubscriptions and FEhistory unrouted -> HTTP 500
        });
        var page = await feed.LoadCompositeHomeAsync("T");
        Assert.Equal(["Recommended"], page.Sections.Select(s => s.Title));
        Assert.Equal(0, page.HistoryRowCount);
    }

    [Fact]
    public async Task CompositeHome_HomeFailureStillFails()
    {
        var feed = MakeRouted(new() { ["FEsubscriptions"] = SectionList(Shelf("Today", Tile("s1"))) });
        await Assert.ThrowsAsync<InnerTubeException>(() => feed.LoadCompositeHomeAsync("T"));
    }

    [Fact]
    public async Task CompositeHome_DedupesShortsAcrossFeeds_AndDropsEmptiedRows()
    {
        var feed = MakeRouted(new()
        {
            ["default"] = SectionList(
                "{\"reelShelfRenderer\":{\"items\":[" + ShortTile("s1") + "," + ShortTile("s2") + "]}}",
                Shelf("Recommended", Tile("h1"))),
            ["FEsubscriptions"] = SectionList(
                "{\"reelShelfRenderer\":{\"items\":[" + ShortTile("s1") + "]}}",
                Shelf("Today", Tile("v1"))),
        });
        var page = await feed.LoadCompositeHomeAsync("T");
        // Home's Shorts row leads (a Shorts row before the first non-Shorts row stays in the
        // lead); the subscriptions Shorts row lost its only id to it and is dropped.
        Assert.Equal(["Shorts", "Recommended", "From your subscriptions"],
            page.Sections.Select(s => s.Title));
        Assert.Equal(["s1", "s2"], page.Sections[0].Items.Select(i => i.Id));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/WinTube.Core.Tests`
Expected: FAIL — `CompositeHomePage`/`LoadCompositeHomeAsync` not defined.

- [ ] **Step 3: Implement**

Append to `FeedModels.cs`:

```csharp
/// The stitched Home screen: Home's own shelves with the subscriptions feed's rows woven in
/// after the first non-Shorts row and the history rows at the end. HistoryRowCount says how
/// many trailing sections are history, so paging can insert new Home shelves above them.
public sealed record CompositeHomePage(
    IReadOnlyList<FeedSection> Sections, string? Continuation, int HistoryRowCount);
```

In `FeedService.cs`:

```csharp
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
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/WinTube.Core.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: composite Home stitched from three feeds"
```

---

### Task 4: App — HomePage renders the composite

**Files:**
- Modify: `src/WinTube.App/Views/HomePage.xaml.cs`

**Interfaces:**
- Consumes: `FeedService.LoadCompositeHomeAsync` (Task 3), existing `ShelfViewModel`.
- Produces: nothing new — Home just shows more rows.

- [ ] **Step 1: Switch the load and remember the history row count**

In `LoadHomeAsync` replace the `LoadHomeAsync` call and add a field:

```csharp
    private int historyRowCount;
```

```csharp
            var page = await App.Session.RunAsync(t => App.Session.Feed.LoadCompositeHomeAsync(t));
            shelves.Clear();
            foreach (var section in page.Sections) shelves.Add(new ShelfViewModel(section));
            historyRowCount = page.HistoryRowCount;
```

(The rest of the method — continuation, Remember, progress — stays as is; `page.Continuation`
and `page.Sections` keep their names on `CompositeHomePage`.)

- [ ] **Step 2: Page new shelves in above the history rows**

In `OnLoadMoreShelves`, replace the `shelves.Add(...)` loop:

```csharp
            foreach (var section in page.Sections)
                shelves.Insert(shelves.Count - historyRowCount, new ShelfViewModel(section));
```

- [ ] **Step 3: Build and verify visually**

Run: `dotnet build src/WinTube.App -p:Platform=x64` — clean.
Launch the exe, screenshot Home: the order must read Home lead row → "From your subscriptions"
(plus its Shorts row when the account's feed carries one) → remaining Home rows → "Continue
watching" last. "Load more" must add rows above "Continue watching".

- [ ] **Step 4: Run all tests, then commit**

```bash
git add -A
git commit -m "feat: Home renders the composite feed"
```

---

### Task 5: App — ChannelPage view and Session wiring

**Files:**
- Create: `src/WinTube.App/ChannelRequest.cs`
- Create: `src/WinTube.App/Views/ChannelPage.xaml`, `src/WinTube.App/Views/ChannelPage.xaml.cs`
- Modify: `src/WinTube.App/Session.cs`

**Interfaces:**
- Consumes: `FeedService.LoadChannelAsync`, `SubscriptionService` (Tasks 1-2).
- Produces:
  `public sealed record ChannelRequest(string ChannelId, string FallbackTitle);`
  `Session.Subscriptions` (`SubscriptionService`).
  `Frame.Navigate(typeof(ChannelPage), new ChannelRequest(...))` — the contract every entry point in Task 6/7 uses.

- [ ] **Step 1: Wire the service and the request record**

`Session.cs` constructor, next to the other services:

```csharp
        Subscriptions = new SubscriptionService(InnerTube);
```

with property `public SubscriptionService Subscriptions { get; }` and
`using WinTube.Core.Channel;`.

New `src/WinTube.App/ChannelRequest.cs`:

```csharp
namespace WinTube.App;

/// Navigation parameter for ChannelPage: the channel to open and the name from the card the
/// user came from, shown until the channel's own header arrives.
public sealed record ChannelRequest(string ChannelId, string FallbackTitle);
```

- [ ] **Step 2: Build the page**

`ChannelPage.xaml`: mirror `HomePage.xaml`'s shelf ItemsControl (same two-ListView row
template, same `ShortRowVisibility` switch, same InfoBar + Retry) with a header above it:

```xml
<Grid>
    <Grid.RowDefinitions>
        <RowDefinition Height="Auto"/>
        <RowDefinition Height="*"/>
    </Grid.RowDefinitions>
    <Grid Grid.Row="0">
        <Image x:Name="Banner" Height="160" Stretch="UniformToFill" Visibility="Collapsed"/>
        <StackPanel Orientation="Horizontal" Spacing="16" Padding="24,16"
                    VerticalAlignment="Bottom">
            <Ellipse Width="64" Height="64">
                <Ellipse.Fill><ImageBrush x:Name="AvatarBrush" Stretch="UniformToFill"/></Ellipse.Fill>
            </Ellipse>
            <TextBlock x:Name="TitleText" VerticalAlignment="Center"
                       Style="{StaticResource TitleTextBlockStyle}"/>
            <ToggleButton x:Name="SubscribeButton" VerticalAlignment="Center"
                          Visibility="Collapsed" Click="OnSubscribeToggled"/>
        </StackPanel>
    </Grid>
    <!-- Row 1: the InfoBar + shelves ScrollViewer copied from HomePage.xaml, minus the
         Load more button (first page of shelves only, the tvOS scope choice). -->
</Grid>
```

`ChannelPage.xaml.cs` behavior:

- `OnNavigatedTo`: read the `ChannelRequest`, show `FallbackTitle` immediately, then
  `var page = await App.Session.RunAsync(t => App.Session.Feed.LoadChannelAsync(request.ChannelId, t));`
  fill header (banner hidden when `BannerUrl` is null; title falls back when empty), build
  the same `ShelfViewModel` list HomePage does, wire row paging and progress identically.
- Subscribe toggle: hidden while `IsSubscribed is null`; otherwise visible with content
  `"Subscribed"` (checked) / `"Subscribe"` (unchecked). `OnSubscribeToggled` flips
  optimistically, calls `SubscribeAsync`/`UnsubscribeAsync` via `RunAsync`, and on exception
  reverts `IsChecked` and shows the InfoBar with the message (no retry action needed —
  the user can click again).
- A channel with no sections shows one quiet `TextBlock`: "This channel has nothing to show."
- Card clicks navigate to the player exactly as HomePage's `OnVideoCardClicked`; channel
  clicks (Task 7's event) navigate to another `ChannelPage`.

- [ ] **Step 3: Build, launch, verify visually**

Run: `dotnet build src/WinTube.App -p:Platform=x64` — clean. Full verification of navigation
happens in Task 7 when the entry points exist; for now navigate by temporarily wiring any
card click if needed, or defer the visual check to Task 7 and just require a clean build here.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat: channel page with subscribe toggle"
```

---

### Task 6: App — Subscriptions nav page

**Files:**
- Create: `src/WinTube.App/Views/SubscriptionsPage.xaml`, `src/WinTube.App/Views/SubscriptionsPage.xaml.cs`
- Modify: `src/WinTube.App/MainWindow.xaml`, `src/WinTube.App/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `Session.Subscriptions.LoadSubscriptionsAsync`, `ChannelRequest` (Task 5).
- Produces: the Subscriptions nav destination.

- [ ] **Step 1: Nav item**

`MainWindow.xaml`, between Search and History:

```xml
<NavigationViewItem x:Name="SubscriptionsItem" Content="Subscriptions" Tag="Subscriptions"/>
```

`MainWindow.xaml.cs` `OnSelectionChanged` switch gains:

```csharp
            case "Subscriptions": RootFrame.Navigate(typeof(Views.SubscriptionsPage)); break;
```

(match the existing switch/if shape in that method).

- [ ] **Step 2: The page**

`SubscriptionsPage.xaml`: an `ItemsRepeater`- or `GridView`-based wrapping grid (GridView is
fine and already themed): each item is a 120-wide vertical tile — 96 px round avatar
(`Ellipse` + `ImageBrush`; when `AvatarUrl` is null, a plain `Ellipse` fill with the theme's
`ControlFillColorSecondaryBrush`) with the channel name beneath (12 px, two lines max,
center-aligned; the bare-id fallback shows the id). InfoBar + Retry like every page.

`SubscriptionsPage.xaml.cs`:

- `OnNavigatedTo`: `var listing = await App.Session.RunAsync(t => App.Session.Subscriptions.LoadSubscriptionsAsync(t));`
  bind `listing.Channels` in arrival order (no sorting — YouTube's own order).
- Item click: `Frame.Navigate(typeof(ChannelPage), new ChannelRequest(channel.Id, channel.Title));`
- Reload on every navigation to the page (no cache), errors to the InfoBar with Retry.

- [ ] **Step 3: Build, launch, verify visually**

Run: `dotnet build src/WinTube.App -p:Platform=x64` — clean. Launch, open Subscriptions,
screenshot: grid of followed channels with avatars and names; click one → channel page with
header, subscribe state and rows; back returns to the grid.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat: subscriptions grid in the navigation"
```

---

### Task 7: App — ways into a channel

**Files:**
- Modify: `src/WinTube.App/Controls/VideoCard.xaml`, `src/WinTube.App/Controls/VideoCard.xaml.cs`
- Modify: `src/WinTube.App/Views/HomePage.xaml(.cs)`, `src/WinTube.App/Views/SearchPage.xaml(.cs)`, `src/WinTube.App/Views/HistoryPage.xaml(.cs)`, `src/WinTube.App/Views/ChannelPage.xaml(.cs)`
- Modify: `src/WinTube.App/Views/PlayerPage.xaml`, `src/WinTube.App/Views/PlayerPage.xaml.cs`

**Interfaces:**
- Consumes: `VideoItem.ChannelId`, `ChannelRequest`, `ChannelPage` (Task 5).
- Produces: `VideoCard.ChannelClicked` event (`EventHandler<VideoItem>`).

- [ ] **Step 1: VideoCard — author link and context menu**

`VideoCard.xaml`: replace the single `SubtitleText` TextBlock with a horizontal StackPanel:

```xml
<StackPanel Grid.Row="2" Orientation="Horizontal" Margin="0,2,0,0" Spacing="0">
    <TextBlock x:Name="AuthorText" FontSize="12"
               Foreground="{ThemeResource TextFillColorSecondaryBrush}"
               PointerEntered="OnAuthorEntered" PointerExited="OnAuthorExited"
               Tapped="OnAuthorTapped"/>
    <TextBlock x:Name="RestText" FontSize="12"
               Foreground="{ThemeResource TextFillColorSecondaryBrush}"
               TextTrimming="CharacterEllipsis"/>
</StackPanel>
```

And a `MenuFlyout` on the root Grid:

```xml
<Grid.ContextFlyout>
    <MenuFlyout>
        <MenuFlyoutItem x:Name="GoToChannelItem" Text="Go to channel" Click="OnGoToChannel"/>
    </MenuFlyout>
</Grid.ContextFlyout>
```

`VideoCard.xaml.cs`:

```csharp
    /// Raised when the channel name (or the context menu's Go to channel) is picked.
    /// Only wired to cells that carried a ChannelId.
    public event EventHandler<VideoItem>? ChannelClicked;
```

In `RenderVideo`, replace the `SubtitleText.Text` line:

```csharp
        var hasChannel = video.ChannelId is not null && video.Author.Length > 0;
        AuthorText.Text = hasChannel ? video.Author : "";
        AuthorText.Visibility = hasChannel ? Visibility.Visible : Visibility.Collapsed;
        RestText.Text = hasChannel ? ComposeSubtitle(video with { Author = "" }) : ComposeSubtitle(video);
        GoToChannelItem.IsEnabled = video.ChannelId is not null;
```

`ComposeSubtitle(video with { Author = "" })` drops the author part but keeps
"ViewCount · age" — prepend `" · "` to `RestText.Text` when both halves are present.
Handlers: `OnAuthorEntered`/`OnAuthorExited` toggle `AuthorText.TextDecorations` underline;
`OnAuthorTapped` and `OnGoToChannel` raise `ChannelClicked` with the video and, for the tap,
set `e.Handled = true` so the card's play-click doesn't also fire.

- [ ] **Step 2: Pages subscribe to ChannelClicked**

Every `<controls:VideoCard ... Clicked="OnVideoCardClicked"/>` in HomePage, SearchPage,
HistoryPage and ChannelPage gains `ChannelClicked="OnChannelClicked"` and the code-behind:

```csharp
    private void OnChannelClicked(object sender, VideoItem video)
    {
        if (video.ChannelId is not { } channelId) return;
        Frame.Navigate(typeof(ChannelPage), new ChannelRequest(channelId, video.Author));
    }
```

(ShortCard is left alone — a Shorts tile has no visible author to click; its channel is
reachable from the player.)

- [ ] **Step 3: PlayerPage — author link in the title bar**

`PlayerPage.xaml`: after `TitleText` (grid column 1), append to the same column a second line
or inline author: simplest is a `StackPanel` replacing `TitleText`'s slot with `TitleText`
plus a small `AuthorLink` TextBlock (12 px, secondary color, hover underline, Tapped handler).
`PlayerPage.xaml.cs`: populate from the request's `VideoItem` (`Author`/`ChannelId`), collapse
when `ChannelId` is null, and on tap
`Frame.Navigate(typeof(ChannelPage), new ChannelRequest(video.ChannelId, video.Author));`.

- [ ] **Step 4: Build, run all tests, verify visually**

Run: `dotnet test tests/WinTube.Core.Tests` and `dotnet build src/WinTube.App -p:Platform=x64`.
Launch and screenshot: author names render with hover underline; click opens the channel;
right-click a card shows "Go to channel"; the player shows the author under/next to the title
and it opens the channel; a channel page's own cards lead to other channels; system back walks
the whole chain home.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: channel links on cards and in the player"
```
