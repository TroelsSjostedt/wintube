using WinTube.Core.Player;

namespace WinTube.Core.Tests;

public class TranscriptServiceTests
{
    [Fact]
    public async Task Get_DownloadsAndParsesTheVtt()
    {
        var handler = new StubHttpHandler((request, _) =>
        {
            Assert.Equal("https://yt.example/timedtext?fmt=vtt", request.RequestUri!.ToString());
            return StubHttpHandler.TextResponse("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nhello\n");
        });
        var service = new TranscriptService(new HttpClient(handler));

        var cues = await service.GetAsync("https://yt.example/timedtext?fmt=vtt");

        Assert.Equal([new TranscriptCue(TimeSpan.FromSeconds(1), "hello")], cues);
    }
}
