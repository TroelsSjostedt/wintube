using System.Text.Json;
using WinTube.Core.Sync;

namespace WinTube.Core.Tests;

public class AppwriteClientTests
{
    private static readonly AppwriteConfig Config =
        new("https://aw.example/v1", "metube");

    private static (AppwriteClient Client, StubHttpHandler Handler) Make(
        Func<HttpRequestMessage, string, HttpResponseMessage> respond)
    {
        var handler = new StubHttpHandler(respond);
        return (new AppwriteClient(new HttpClient(handler), Config), handler);
    }

    [Fact]
    public async Task SignIn_UnwrapsExecutionAndCollectsSessionCookies()
    {
        var (client, handler) = Make((request, body) =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/functions/metube-auth/executions"))
            {
                var outer = JsonDocument.Parse(body).RootElement;
                var inner = JsonDocument.Parse(outer.GetProperty("body").GetString()!).RootElement;
                Assert.Equal("AT", inner.GetProperty("accessToken").GetString());
                Assert.Equal("GAIA", inner.GetProperty("accountKey").GetString());
                Assert.Equal("metube", request.Headers.GetValues("X-Appwrite-Project").Single());
                Assert.Equal("appwrite-tvos://dk.delectosoft.metube",
                    request.Headers.GetValues("Origin").Single());
                return StubHttpHandler.JsonResponse("""
                    {"responseStatusCode":200,
                     "responseBody":"{\"userId\":\"yt1234\",\"secret\":\"SEC\",\"expire\":\"x\"}"}
                    """);
            }
            Assert.Contains("/account/sessions/token", url);
            var response = StubHttpHandler.JsonResponse("""{"secret":""}""", 201);
            response.Headers.TryAddWithoutValidation("Set-Cookie",
                "a_session_metube=COOKIEVALUE; path=/; httponly");
            response.Headers.TryAddWithoutValidation("Set-Cookie",
                "a_session_metube_legacy=LEGACY; path=/");
            response.Headers.TryAddWithoutValidation("Set-Cookie",
                "unrelated=x; path=/");
            return response;
        });

        var session = await client.SignInAsync("AT", "GAIA");
        Assert.Equal("yt1234", session.UserId);
        Assert.Equal("a_session_metube=COOKIEVALUE; a_session_metube_legacy=LEGACY",
            session.Cookie);
    }

    [Fact]
    public async Task SignIn_FunctionRejection_SurfacesInnerStatusAndMessage()
    {
        var (client, _) = Make((_, _) => StubHttpHandler.JsonResponse("""
            {"responseStatusCode":401,
             "responseBody":"{\"message\":\"token does not match accountKey\"}"}
            """));
        var ex = await Assert.ThrowsAsync<AppwriteException>(() =>
            client.SignInAsync("AT", "GAIA"));
        Assert.Equal(401, ex.StatusCode);
        Assert.Contains("token does not match accountKey", ex.Message);
    }

    [Fact]
    public async Task ListRows_SendsQueriesCookieAndParsesRows()
    {
        var (client, handler) = Make((request, _) =>
        {
            Assert.Equal("COOKIE=1", request.Headers.GetValues("Cookie").Single());
            var url = request.RequestUri!.ToString();
            Assert.Contains("/tablesdb/metube/tables/watchProgress/rows?", url);
            Assert.Contains("queries%5B%5D=", url);   // queries[] percent-encoded
            Assert.Contains("total=false", url);
            return StubHttpHandler.JsonResponse("""
                {"rows":[{"$id":"p_v1","videoId":"v1","position":10.0}]}
                """);
        });
        client.Session = new AppwriteSession("yt1234", "COOKIE=1");

        var rows = await client.ListRowsAsync([AppwriteQuery.Limit(100)]);
        Assert.Equal("v1", rows.Single().GetProperty("videoId").GetString());
    }

    [Fact]
    public async Task ListRows_401_ThrowsUnauthorized()
    {
        var (client, _) = Make((_, _) =>
            StubHttpHandler.JsonResponse("""{"message":"session expired"}""", 401));
        client.Session = new AppwriteSession("yt1234", "COOKIE=1");
        var ex = await Assert.ThrowsAsync<AppwriteException>(() =>
            client.ListRowsAsync([AppwriteQuery.Limit(1)]));
        Assert.True(ex.IsUnauthorized);
        Assert.Contains("session expired", ex.Message);
    }

    [Fact]
    public async Task UpsertRow_PutsDataAndPermissions()
    {
        var (client, handler) = Make((request, body) =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.EndsWith("/tablesdb/metube/tables/watchProgress/rows/abcd_v1",
                request.RequestUri!.AbsolutePath);
            var json = JsonDocument.Parse(body).RootElement;
            Assert.Equal("v1", json.GetProperty("data").GetProperty("videoId").GetString());
            Assert.Equal("read(\"user:yt1234\")",
                json.GetProperty("permissions")[0].GetString());
            return StubHttpHandler.JsonResponse("""{"$id":"abcd_v1"}""");
        });
        client.Session = new AppwriteSession("yt1234", "COOKIE=1");

        await client.UpsertRowAsync("abcd_v1",
            new Dictionary<string, object?> { ["videoId"] = "v1", ["position"] = 12.5 },
            ["read(\"user:yt1234\")"]);
        Assert.Single(handler.Requests);
    }
}
