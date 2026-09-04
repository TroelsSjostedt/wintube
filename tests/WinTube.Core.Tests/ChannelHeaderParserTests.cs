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
