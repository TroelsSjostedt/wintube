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
    public async Task Home_ShortsShelvesAndStrayShortsAreDropped()
    {
        var json = "{\"contents\":{\"sectionListRenderer\":{\"contents\":[" +
            "{\"reelShelfRenderer\":{\"items\":[" + ShortTile("s1") + "]}}," +
            "{\"shelfRenderer\":{" +
            "\"headerRenderer\":{\"shelfHeaderRenderer\":{\"title\":{\"simpleText\":\"Mixed\"}}}," +
            "\"content\":{\"horizontalListRenderer\":{" +
            "\"items\":[" + Tile("v1") + "," + ShortTile("s2") + "]}}}}]}}}";

        var (feed, _) = Make(json);
        var page = await feed.LoadHomeAsync("T");
        var section = Assert.Single(page.Sections);        // reel shelf gone entirely
        Assert.Equal("Mixed", section.Title);
        Assert.Equal(["v1"], section.Items.Select(i => i.Id));   // stray Short dropped
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
}
