using WinTube.Core;
using WinTube.Core.InnerTube;
using WinTube.Core.Player;

namespace WinTube.Core.Tests;

public class StreamServiceTests
{
    private const string VisitorPage = """{"visitorData":"VDATA"}""";

    private const string HlsResponse = """
        {"playabilityStatus":{"status":"OK"},
         "streamingData":{
           "hlsManifestUrl":"https://manifest.example/master.m3u8",
           "adaptiveFormats":[
             {"itag":140,"audioTrack":{"id":"de-DE.3","audioIsDefault":true}},
             {"itag":141,"audioTrack":{"id":"en-US.4","audioIsDefault":false}}]}}
        """;

    private const string Itag18Response = """
        {"playabilityStatus":{"status":"OK"},
         "streamingData":{"formats":[
           {"itag":22,"mimeType":"video/mp4","signatureCipher":"blocked"},
           {"itag":18,"mimeType":"video/mp4; codecs=\"avc1\"","url":"https://v.example/18.mp4"}]}}
        """;

    private const string LoginRequired = """
        {"playabilityStatus":{"status":"LOGIN_REQUIRED","reason":"Sign in to confirm your age"}}
        """;

    /// Routes the visitor-data page scrape and per-client /player calls to canned responses.
    private static StreamService Make(Func<string, string> playerResponseForHost,
        out List<string> playerHosts)
    {
        var hosts = new List<string>();
        var handler = new StubHttpHandler((request, _) =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/tv?bpctr")) return StubHttpHandler.JsonResponse(VisitorPage);
            hosts.Add(request.RequestUri!.Host);
            return StubHttpHandler.JsonResponse(playerResponseForHost(request.RequestUri!.Host));
        });
        var http = new HttpClient(handler);
        playerHosts = hosts;
        return new StreamService(
            new InnerTubeClient(http, new Secrets("K", "", "")), new VisitorDataStore(http));
    }

    [Fact]
    public async Task Resolve_PrefersHlsFromVisionOs()
    {
        var service = Make(_ => HlsResponse, out _);
        var stream = await service.ResolveAsync("abc");
        Assert.Equal("https://manifest.example/master.m3u8", stream.Url.ToString());
        Assert.True(stream.IsAdaptive);
        Assert.Equal(ClientKind.VisionOs, stream.Client);
        Assert.Equal("de-DE", stream.OriginalAudioLanguage);
    }

    [Fact]
    public async Task Resolve_FallsBackToAndroidItag18OnLoginRequired()
    {
        // VISIONOS (www.youtube.com) is gated; ANDROID (youtubei.googleapis.com) serves itag 18.
        var service = Make(
            host => host == "youtubei.googleapis.com" ? Itag18Response : LoginRequired, out _);
        var stream = await service.ResolveAsync("abc");
        Assert.Equal("https://v.example/18.mp4", stream.Url.ToString());
        Assert.False(stream.IsAdaptive);
        Assert.Equal(ClientKind.Android, stream.Client);
        Assert.Null(stream.OriginalAudioLanguage);
    }

    [Fact]
    public async Task Resolve_AllClientsGated_SurfacesYouTubesReason()
    {
        var service = Make(_ => LoginRequired, out _);
        var ex = await Assert.ThrowsAsync<StreamException>(() => service.ResolveAsync("abc"));
        Assert.Contains("Sign in to confirm your age", ex.Message);
    }

    [Fact]
    public async Task Resolve_After_SkipsPastFailedClient()
    {
        var service = Make(
            host => host == "youtubei.googleapis.com" ? Itag18Response : HlsResponse,
            out var playerHosts);
        var stream = await service.ResolveAsync("abc", after: ClientKind.VisionOs);
        Assert.Equal(ClientKind.Android, stream.Client);
        Assert.DoesNotContain("www.youtube.com", playerHosts);  // VISIONOS never called
    }
}
