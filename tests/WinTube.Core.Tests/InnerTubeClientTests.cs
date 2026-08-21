using System.Text.Json;
using WinTube.Core;
using WinTube.Core.InnerTube;

namespace WinTube.Core.Tests;

public class InnerTubeClientTests
{
    private static readonly Secrets TestSecrets = new("APIKEY", "id", "secret");

    private static (InnerTubeClient Client, StubHttpHandler Handler) Make(
        string responseJson = "{}", int status = 200)
    {
        var handler = new StubHttpHandler((_, _) => StubHttpHandler.JsonResponse(responseJson, status));
        return (new InnerTubeClient(new HttpClient(handler), TestSecrets), handler);
    }

    [Fact]
    public async Task Post_BuildsUrlHeadersAndBodyForTvClient()
    {
        var (client, handler) = Make();
        using var _ = await client.PostAsync("browse", ClientKind.Tv,
            new Dictionary<string, object?> { ["browseId"] = "default" }, bearer: "TOKEN");

        var (message, body) = handler.Requests.Single();
        Assert.Equal("https://www.youtube.com/youtubei/v1/browse?key=APIKEY&prettyPrint=false",
            message.RequestUri!.ToString());
        Assert.Equal("7", message.Headers.GetValues("X-Youtube-Client-Name").Single());
        Assert.Equal("7.20260707.07.00", message.Headers.GetValues("X-Youtube-Client-Version").Single());
        Assert.Equal("Bearer TOKEN", message.Headers.Authorization!.ToString());
        Assert.Equal("https://www.youtube.com/tv", message.Headers.Referrer!.ToString());

        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("TVHTML5", Json.StringAt(json, "context/client/clientName"));
        Assert.Equal("en", Json.StringAt(json, "context/client/hl"));
        Assert.Equal("default", Json.StringAt(json, "browseId"));
    }

    [Fact]
    public async Task Post_AndroidClient_UsesGoogleapisHostAndExtraContext()
    {
        var (client, handler) = Make();
        using var _ = await client.PostAsync("player", ClientKind.Android,
            new Dictionary<string, object?> { ["videoId"] = "abc" });

        var (message, body) = handler.Requests.Single();
        Assert.StartsWith("https://youtubei.googleapis.com/youtubei/v1/player?",
            message.RequestUri!.ToString());
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal(30, Json.ValueAt(json, "context/client/androidSdkVersion")!.Value.GetInt32());
        Assert.Null(message.Headers.Referrer);
    }

    [Fact]
    public async Task Post_VisitorData_GoesToHeaderAndContext()
    {
        var (client, handler) = Make();
        using var _ = await client.PostAsync("player", ClientKind.VisionOs,
            new Dictionary<string, object?> { ["videoId"] = "abc" }, visitorData: "VDATA");

        var (message, body) = handler.Requests.Single();
        Assert.Equal("VDATA", message.Headers.GetValues("X-Goog-Visitor-Id").Single());
        Assert.Equal("VDATA",
            Json.StringAt(JsonDocument.Parse(body).RootElement, "context/client/visitorData"));
    }

    [Fact]
    public async Task Post_Non2xx_ThrowsWithStatusCode()
    {
        var (client, _) = Make("{}", status: 401);
        var ex = await Assert.ThrowsAsync<InnerTubeException>(() =>
            client.PostAsync("browse", ClientKind.Tv, new Dictionary<string, object?>()));
        Assert.Equal(401, ex.StatusCode);
    }
}
