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
