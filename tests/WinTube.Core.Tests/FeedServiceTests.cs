using WinTube.Core;
using WinTube.Core.Feed;
using WinTube.Core.InnerTube;
using WinTube.Core.Search;

namespace WinTube.Core.Tests;

public class FeedServiceTests
{
    private static string Tile(string id, string title = "t")
    {
        var t = title.Replace("\"", "\\\"");
        return "{\"tileRenderer\":{" +
            "\"contentType\":\"TILE_CONTENT_TYPE_VIDEO\"," +
            "\"onSelectCommand\":{\"watchEndpoint\":{\"videoId\":\"" + id + "\"}}," +
            "\"metadata\":{\"tileMetadataRenderer\":{" +
            "\"title\":{\"simpleText\":\"" + t + "\"}}}}}";
    }

    private static string ShortTile(string id)
    {
        return "{\"tileRenderer\":{" +
            "\"contentType\":\"TILE_CONTENT_TYPE_SHORTS\"," +
            "\"onSelectCommand\":{\"reelWatchEndpoint\":{\"videoId\":\"" + id + "\"}}," +
            "\"metadata\":{\"tileMetadataRenderer\":{" +
            "\"title\":{\"simpleText\":\"s\"}}}}}";
    }

    private static (FeedService Feed, SearchService Search) Make(string response)
    {
        var handler = new StubHttpHandler((_, _) => StubHttpHandler.JsonResponse(response));
        var client = new InnerTubeClient(new HttpClient(handler), new Secrets("K", "", ""));
        return (new FeedService(client), new SearchService(client));
    }

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

    [Fact]
    public async Task Home_ParsesShelvesWithTitlesRowTokensAndPageToken()
    {
        var json = "{\"contents\":{\"sectionListRenderer\":{" +
            "\"contents\":[{\"shelfRenderer\":{" +
            "\"headerRenderer\":{\"shelfHeaderRenderer\":{\"avatarLockup\":{" +
            "\"avatarLockupRenderer\":{\"title\":{\"simpleText\":\"Recommended\"}}}}}," +
            "\"content\":{\"horizontalListRenderer\":{" +
            "\"items\":[" + Tile("v1") + "," + Tile("v2") + "]," +
            "\"continuations\":[{\"nextContinuationData\":{\"continuation\":\"ROW_TOKEN\"}}]}}}}]," +
            "\"continuations\":[{\"nextContinuationData\":{\"continuation\":\"PAGE_TOKEN\"}}]}}}";

        var (feed, _) = Make(json);
        var page = await feed.LoadHomeAsync("T");
        var section = Assert.Single(page.Sections);
        Assert.Equal("Recommended", section.Title);
        Assert.Equal(["v1", "v2"], section.Items.Select(i => i.Id));
        Assert.Equal("ROW_TOKEN", section.Continuation);
        Assert.Equal("PAGE_TOKEN", page.Continuation);
    }

    [Fact]
    public async Task Home_KeepsShortsRow_AndMovesStraysIntoIt()
    {
        var (feed, _) = Make("{\"contents\":{\"sectionListRenderer\":{\"contents\":[" +
            "{\"reelShelfRenderer\":{\"items\":[" + ShortTile("s1") + "]}}," +
            "{\"shelfRenderer\":{" +
            "\"headerRenderer\":{\"shelfHeaderRenderer\":{\"title\":{\"simpleText\":\"Mixed\"}}}," +
            "\"content\":{\"horizontalListRenderer\":{" +
            "\"items\":[" + Tile("v1") + "," + ShortTile("s2") + "]}}}}]}}}");
        var page = await feed.LoadHomeAsync("T");
        Assert.Equal(2, page.Sections.Count);

        var shorts = page.Sections[0];
        Assert.True(shorts.IsShorts);
        Assert.Equal("Shorts", shorts.Title);                       // untitled reel shelf named
        Assert.Equal(["s1", "s2"], shorts.Items.Select(i => i.Id)); // stray s2 moved in
        Assert.All(shorts.Items, i => Assert.True(i.IsShort));

        var mixed = page.Sections[1];
        Assert.False(mixed.IsShorts);
        Assert.Equal(["v1"], mixed.Items.Select(i => i.Id));        // Short lifted out, not lost
    }

    [Fact]
    public async Task Home_StraysWithNoShortsShelf_BecomeASynthesizedRowAtTheEnd()
    {
        var (feed, _) = Make("{\"contents\":{\"sectionListRenderer\":{\"contents\":[" +
            "{\"shelfRenderer\":{" +
            "\"headerRenderer\":{\"shelfHeaderRenderer\":{\"title\":{\"simpleText\":\"Mixed\"}}}," +
            "\"content\":{\"horizontalListRenderer\":{" +
            "\"items\":[" + Tile("v1") + "," + ShortTile("s1") + "]}}}}]}}}");
        var page = await feed.LoadHomeAsync("T");
        Assert.Equal(2, page.Sections.Count);
        Assert.False(page.Sections[0].IsShorts);
        var shorts = page.Sections[1];
        Assert.True(shorts.IsShorts);
        Assert.Equal("Shorts", shorts.Title);
        Assert.Equal(["s1"], shorts.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task HistoryFeed_FoldingSkipsShortsRows_AndRetitlesFirstNonShortsRow()
    {
        var (feed, _) = Make("{\"contents\":{\"sectionListRenderer\":{\"contents\":[" +
            "{\"reelShelfRenderer\":{\"items\":[" + ShortTile("s1") + "]}}," +
            "{\"shelfRenderer\":{" +
            "\"headerRenderer\":{\"shelfHeaderRenderer\":{\"title\":{\"simpleText\":\"Today\"}}}," +
            "\"content\":{\"horizontalListRenderer\":{\"items\":[" + Tile("v1") + "]}}}}," +
            "{\"shelfRenderer\":{\"content\":{\"horizontalListRenderer\":{" +
            "\"items\":[" + Tile("v2") + "]}}}}]}}}");
        var page = await feed.LoadHistoryFeedAsync("T");
        Assert.Equal(2, page.Sections.Count);
        Assert.True(page.Sections[0].IsShorts);
        Assert.Equal("Shorts", page.Sections[0].Title);             // NOT retitled
        Assert.Equal("Continue watching", page.Sections[1].Title);  // first non-Shorts row
        Assert.Equal(["v1", "v2"], page.Sections[1].Items.Select(i => i.Id));
    }

    [Fact]
    public async Task RowContinuation_ReturnsRawItemsIncludingShorts()
    {
        var (feed, _) = Make("{\"continuationContents\":{\"horizontalListContinuation\":{" +
            "\"items\":[" + Tile("v3") + "," + ShortTile("s3") + "]}}}");
        var row = await feed.LoadMoreItemsAsync("TOKEN", "T");
        Assert.Equal(["v3", "s3"], row.Items.Select(i => i.Id));    // no filtering here
    }

    [Fact]
    public async Task Home_StrayShortsDedupedAcrossShelves()
    {
        var (feed, _) = Make("{\"contents\":{\"sectionListRenderer\":{\"contents\":[" +
            "{\"shelfRenderer\":{" +
            "\"headerRenderer\":{\"shelfHeaderRenderer\":{\"title\":{\"simpleText\":\"First\"}}}," +
            "\"content\":{\"horizontalListRenderer\":{" +
            "\"items\":[" + Tile("v1") + "," + ShortTile("s1") + "]}}}}," +
            "{\"shelfRenderer\":{" +
            "\"headerRenderer\":{\"shelfHeaderRenderer\":{\"title\":{\"simpleText\":\"Second\"}}}," +
            "\"content\":{\"horizontalListRenderer\":{" +
            "\"items\":[" + Tile("v2") + "," + ShortTile("s1") + "]}}}}]}}}");
        var page = await feed.LoadHomeAsync("T");
        Assert.Equal(3, page.Sections.Count);
        var shorts = page.Sections[2];
        Assert.True(shorts.IsShorts);
        Assert.Equal(["s1"], shorts.Items.Select(i => i.Id));  // s1 appears exactly once, not twice
    }

    [Fact]
    public async Task Home_NestedDuplicateShelfIsSkipped()
    {
        var json = "{\"contents\":{\"sectionListRenderer\":{\"contents\":[" +
            "{\"shelfRenderer\":{" +
            "\"headerRenderer\":{\"shelfHeaderRenderer\":{\"title\":{\"simpleText\":\"Outer\"}}}," +
            "\"content\":{\"horizontalListRenderer\":{\"items\":[" +
            Tile("v1") + "," +
            "{\"shelfRenderer\":{\"title\":{\"simpleText\":\"Inner\"}," +
            "\"content\":{\"horizontalListRenderer\":{\"items\":[" + Tile("v1") + "]}}}}]}}}}]}}}";

        var (feed, _) = Make(json);
        var page = await feed.LoadHomeAsync("T");
        Assert.Equal("Outer", Assert.Single(page.Sections).Title);
    }

    [Fact]
    public async Task HistoryFeed_FoldsUntitledRowsAndRetitles()
    {
        var json = "{\"contents\":{\"sectionListRenderer\":{\"contents\":[" +
            "{\"shelfRenderer\":{" +
            "\"headerRenderer\":{\"shelfHeaderRenderer\":{\"title\":{\"simpleText\":\"Today\"}}}," +
            "\"content\":{\"horizontalListRenderer\":{\"items\":[" + Tile("v1") + "]}}}}," +
            "{\"shelfRenderer\":{\"content\":{\"horizontalListRenderer\":{" +
            "\"items\":[" + Tile("v1") + "," + Tile("v2") + "]," +
            "\"continuations\":[{\"nextContinuationData\":{\"continuation\":\"CHUNK_TOKEN\"}}]}}}}]}}}";

        var (feed, _) = Make(json);
        var page = await feed.LoadHistoryFeedAsync("T");
        var section = Assert.Single(page.Sections);
        Assert.Equal("Continue watching", section.Title);
        Assert.Equal(["v1", "v2"], section.Items.Select(i => i.Id));   // deduped fold
        Assert.Equal("CHUNK_TOKEN", section.Continuation);             // chunk token outranks host's
    }

    [Fact]
    public async Task RowContinuation_ParsesBareItemListAndNextToken()
    {
        var json = "{\"continuationContents\":{\"horizontalListContinuation\":{" +
            "\"items\":[" + Tile("v3") + "]," +
            "\"continuations\":[{\"nextContinuationData\":{\"continuation\":\"NEXT\"}}]}}}";

        var (feed, _) = Make(json);
        var row = await feed.LoadMoreItemsAsync("TOKEN", "T");
        Assert.Equal(["v3"], row.Items.Select(i => i.Id));
        Assert.Equal("NEXT", row.Continuation);
    }

    [Fact]
    public async Task Search_ReturnsFlatListWithoutShorts()
    {
        var json = "{\"contents\":[" + Tile("v1", "hit") + "," + ShortTile("s1") + "]}";

        var (_, search) = Make(json);
        var results = await search.SearchAsync("query", "T");
        Assert.Equal(["v1"], results.Select(i => i.Id));
    }

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
}
