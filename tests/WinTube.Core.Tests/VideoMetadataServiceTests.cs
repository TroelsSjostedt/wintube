using WinTube.Core;
using WinTube.Core.InnerTube;
using WinTube.Core.Stores;

namespace WinTube.Core.Tests;

public class VideoMetadataServiceTests
{
    [Fact]
    public async Task Load_BuildsCardFromVideoDetails()
    {
        var handler = new StubHttpHandler((request, _) =>
            request.RequestUri!.ToString().Contains("/tv?bpctr")
                ? StubHttpHandler.JsonResponse("""{"visitorData":"VD"}""")
                : StubHttpHandler.JsonResponse("""
                    {"videoDetails":{"videoId":"v1","title":"Looked Up","author":"Chan",
                      "channelId":"UCx","lengthSeconds":"3734",
                      "thumbnail":{"thumbnails":[{"url":"https://t/1.jpg","width":640}]}}}
                    """));
        var http = new HttpClient(handler);
        var service = new VideoMetadataService(
            new InnerTubeClient(http, new Secrets("K", "", "")), new VisitorDataStore(http));

        var item = await service.LoadAsync("v1");
        Assert.Equal("Looked Up", item!.Title);
        Assert.Equal("Chan", item.Author);
        Assert.Equal("UCx", item.ChannelId);
        Assert.Equal("1:02:14", item.Duration);
    }

    [Fact]
    public async Task Load_NoDetails_ReturnsNull()
    {
        var handler = new StubHttpHandler((request, _) =>
            request.RequestUri!.ToString().Contains("/tv?bpctr")
                ? StubHttpHandler.JsonResponse("""{"visitorData":"VD"}""")
                : StubHttpHandler.JsonResponse("""{"playabilityStatus":{"status":"ERROR"}}"""));
        var http = new HttpClient(handler);
        var service = new VideoMetadataService(
            new InnerTubeClient(http, new Secrets("K", "", "")), new VisitorDataStore(http));
        Assert.Null(await service.LoadAsync("gone"));
    }

    [Theory]
    [InlineData(3734, "1:02:14")]
    [InlineData(1315, "21:55")]
    [InlineData(0, "")]
    public void DurationText_FormatsLikeAThumbnailBadge(int seconds, string expected)
    {
        Assert.Equal(expected, VideoMetadataService.DurationText(seconds));
    }
}
