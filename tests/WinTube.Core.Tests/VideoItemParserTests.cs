using System.Text.Json;
using WinTube.Core.Feed;

namespace WinTube.Core.Tests;

public class VideoItemParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<WinTube.Core.Models.VideoItem> Parse(string json) =>
        VideoItemParser.Items(JsonDocument.Parse(json).RootElement, Now);

    [Fact]
    public void Tile_ParsesAllFields()
    {
        var items = Parse("""
            {"tileRenderer":{
              "contentType":"TILE_CONTENT_TYPE_VIDEO",
              "onSelectCommand":{"watchEndpoint":{"videoId":"vid1"}},
              "header":{"tileHeaderRenderer":{"thumbnail":{"thumbnails":[
                {"url":"//i.ytimg.com/small.jpg","width":320,"height":180},
                {"url":"https://i.ytimg.com/big.jpg","width":1280,"height":720}]},
                "thumbnailOverlays":[{"thumbnailOverlayTimeStatusRenderer":{
                  "text":{"simpleText":"21:55"}}}]}},
              "metadata":{"tileMetadataRenderer":{
                "title":{"simpleText":"A Video"},
                "lines":[{"lineRenderer":{"items":[
                  {"lineItemRenderer":{"text":{"simpleText":"Some Channel"}}},
                  {"lineItemRenderer":{"text":{"simpleText":"1.2M views • 3 days ago"}}}]}}]}},
              "onLongPressCommand":{"showMenuCommand":{"menu":{"items":[
                {"menuNavigationItemRenderer":{"navigationEndpoint":{
                  "browseEndpoint":{"browseId":"UCchannel123"}}}}]}}}}}
            """);
        var item = Assert.Single(items);
        Assert.Equal("vid1", item.Id);
        Assert.Equal("A Video", item.Title);
        Assert.Equal("Some Channel", item.Author);
        Assert.Equal("UCchannel123", item.ChannelId);
        Assert.Equal("https://i.ytimg.com/big.jpg", item.ThumbnailUrl);
        Assert.Equal("1.2M views", item.ViewCount);
        Assert.Equal(Now.AddDays(-3), item.PublishedAt);
        Assert.Equal("21:55", item.Duration);
        Assert.False(item.IsShort);
    }

    [Fact]
    public void Tile_ChannelContentType_IsSkipped()
    {
        Assert.Empty(Parse("""
            {"tileRenderer":{"contentType":"TILE_CONTENT_TYPE_CHANNEL",
             "onSelectCommand":{"watchEndpoint":{"videoId":"x"}}}}
            """));
    }

    [Fact]
    public void Lockup_ParsesVideoAndSkipsPlaylist()
    {
        var items = Parse("""
            {"a":[
              {"lockupViewModel":{"contentType":"LOCKUP_CONTENT_TYPE_PLAYLIST","contentId":"PL1"}},
              {"lockupViewModel":{
                "contentType":"LOCKUP_CONTENT_TYPE_VIDEO","contentId":"vid2",
                "contentImage":{"thumbnailViewModel":{"image":{"sources":[
                  {"url":"https://i.ytimg.com/t2.jpg","width":640,"height":360}]},
                  "overlays":[{"thumbnailBadgeViewModel":{"text":"LIVE"}},
                              {"thumbnailBadgeViewModel":{"text":"10:03"}}]}},
                "metadata":{"lockupMetadataViewModel":{
                  "title":{"content":"Search Hit"},
                  "metadata":{"contentMetadataViewModel":{"metadataRows":[
                    {"metadataParts":[{"text":{"content":"Chan"}}]},
                    {"metadataParts":[{"text":{"content":"3K views"}},
                                      {"text":{"content":"2 weeks ago"}}]}]}}}}}}]}
            """);
        var item = Assert.Single(items);
        Assert.Equal("vid2", item.Id);
        Assert.Equal("Search Hit", item.Title);
        Assert.Equal("Chan", item.Author);
        Assert.Equal("3K views", item.ViewCount);
        Assert.Equal(Now.AddDays(-14), item.PublishedAt);
        Assert.Equal("10:03", item.Duration);
    }

    [Fact]
    public void VideoRenderer_ParsesDirectFields()
    {
        var items = Parse("""
            {"videoRenderer":{"videoId":"vid3",
              "title":{"runs":[{"text":"Old Shape"}]},
              "longBylineText":{"runs":[{"text":"Bylined"}]},
              "publishedTimeText":{"simpleText":"1 hour ago"},
              "shortViewCountText":{"simpleText":"12K views"},
              "lengthText":{"simpleText":"1:02:14"},
              "thumbnail":{"thumbnails":[{"url":"//i.ytimg.com/t3.jpg","width":320}]}}}
            """);
        var item = Assert.Single(items);
        Assert.Equal("Old Shape", item.Title);
        Assert.Equal("Bylined", item.Author);
        Assert.Equal("12K views", item.ViewCount);
        Assert.Equal("1:02:14", item.Duration);
        Assert.Equal("https://i.ytimg.com/t3.jpg", item.ThumbnailUrl);
        Assert.Equal(Now.AddHours(-1), item.PublishedAt);
    }

    [Theory]
    [InlineData("""{"videoRenderer":{"videoId":"s1","title":{"simpleText":"t"},"navigationEndpoint":{"reelWatchEndpoint":{"videoId":"s1"}}}}""")]
    [InlineData("""{"tileRenderer":{"contentType":"TILE_CONTENT_TYPE_SHORTS","onSelectCommand":{"reelWatchEndpoint":{"videoId":"s2"}},"metadata":{"tileMetadataRenderer":{"title":{"simpleText":"t"}}}}}""")]
    [InlineData("""{"videoRenderer":{"videoId":"s3","title":{"simpleText":"t"},"thumbnail":{"thumbnails":[{"url":"https://x/p.jpg","width":405,"height":720}]}}}""")]
    public void Shorts_AreDetectedBySignals(string json)
    {
        Assert.True(Assert.Single(Parse(json)).IsShort);
    }

    [Fact]
    public void Dedupe_KeepsFirstOccurrence()
    {
        var items = Parse("""
            {"a":{"videoRenderer":{"videoId":"dup","title":{"simpleText":"First"}}},
             "b":{"videoRenderer":{"videoId":"dup","title":{"simpleText":"Second"}}}}
            """);
        Assert.Equal("First", Assert.Single(items).Title);
    }

    [Fact]
    public void MissingThumbnail_FallsBackToYtimg()
    {
        var item = Assert.Single(Parse(
            """{"videoRenderer":{"videoId":"v9","title":{"simpleText":"t"}}}"""));
        Assert.Equal("https://i.ytimg.com/vi/v9/hqdefault.jpg", item.ThumbnailUrl);
    }

    [Fact]
    public void ChannelAvatar_PickedByHostAndResized()
    {
        var item = Assert.Single(Parse("""
            {"videoRenderer":{"videoId":"v10","title":{"simpleText":"t"},
             "channelThumbnailSupportedRenderers":{"channelThumbnailWithLinkRenderer":{
               "thumbnail":{"thumbnails":[
                 {"url":"https://yt3.ggpht.com/abc=s88-c-k","width":88}]}}}}}
            """));
        Assert.Equal("https://yt3.ggpht.com/abc=s176-c-k", item.ChannelAvatarUrl);
    }
}
