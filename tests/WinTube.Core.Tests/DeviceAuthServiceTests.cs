using WinTube.Core;
using WinTube.Core.Auth;

namespace WinTube.Core.Tests;

public class DeviceAuthServiceTests
{
    private static readonly Secrets TestSecrets = new("K", "CLIENT_ID", "CLIENT_SECRET");

    private static System.Text.Json.JsonElement ParseBody(string body) =>
        System.Text.Json.JsonDocument.Parse(body).RootElement;

    [Fact]
    public async Task RequestCode_SendsJsonWithDeviceIdentity()
    {
        var handler = new StubHttpHandler((request, body) =>
        {
            Assert.Equal("https://www.youtube.com/o/oauth2/device/code",
                request.RequestUri!.ToString());
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            var json = ParseBody(body);
            Assert.Equal("CLIENT_ID", json.GetProperty("client_id").GetString());
            Assert.Contains("gdata.youtube.com", json.GetProperty("scope").GetString());
            // The endpoint rejects requests without a device identity (verified 2026-08-22:
            // bare client_id+scope now answers invalid_request).
            Assert.Equal("ytlr::", json.GetProperty("device_model").GetString());
            Assert.Equal(32, json.GetProperty("device_id").GetString()!.Length);
            return StubHttpHandler.JsonResponse("""
                {"device_code":"DEV","user_code":"ABC-DEF",
                 "verification_url":"https://www.youtube.com/activate",
                 "interval":5,"expires_in":1800}
                """);
        });
        var code = await new DeviceAuthService(new HttpClient(handler), TestSecrets)
            .RequestCodeAsync();
        Assert.Equal("DEV", code.Code);
        Assert.Equal("ABC-DEF", code.UserCode);
        Assert.Equal("https://www.youtube.com/activate", code.VerificationUrl);
        Assert.Equal(5, code.IntervalSeconds);
    }

    [Fact]
    public async Task Poll_PendingThenSuccess()
    {
        var polls = 0;
        var handler = new StubHttpHandler((_, body) =>
        {
            Assert.Equal("http://oauth.net/grant_type/device/1.0",
                ParseBody(body).GetProperty("grant_type").GetString());
            polls++;
            return polls < 3
                ? StubHttpHandler.JsonResponse("""{"error":"authorization_pending"}""", 428)
                : StubHttpHandler.JsonResponse(
                    """{"access_token":"AT","refresh_token":"RT"}""");
        });
        var tokens = await new DeviceAuthService(new HttpClient(handler), TestSecrets)
            .PollAsync("DEV", intervalSeconds: 0);
        Assert.Equal("AT", tokens.AccessToken);
        Assert.Equal("RT", tokens.RefreshToken);
        Assert.Equal(3, polls);
    }

    [Fact]
    public async Task Poll_AccessDenied_ThrowsFriendlyMessage()
    {
        var handler = new StubHttpHandler((_, _) =>
            StubHttpHandler.JsonResponse("""{"error":"access_denied"}""", 403));
        var ex = await Assert.ThrowsAsync<DeviceAuthException>(() =>
            new DeviceAuthService(new HttpClient(handler), TestSecrets)
                .PollAsync("DEV", intervalSeconds: 0));
        Assert.Contains("denied", ex.Message);
    }

    [Fact]
    public async Task Refresh_KeepsOldRefreshTokenWhenResponseOmitsIt()
    {
        var handler = new StubHttpHandler((_, body) =>
        {
            var json = ParseBody(body);
            Assert.Equal("refresh_token", json.GetProperty("grant_type").GetString());
            Assert.Equal("RT", json.GetProperty("refresh_token").GetString());
            return StubHttpHandler.JsonResponse("""{"access_token":"AT2"}""");
        });
        var tokens = await new DeviceAuthService(new HttpClient(handler), TestSecrets)
            .RefreshAsync("RT");
        Assert.Equal("AT2", tokens.AccessToken);
        Assert.Null(tokens.RefreshToken);   // caller keeps the existing one
    }

    [Fact]
    public async Task Refresh_NonJsonServerErrorIsTransient()
    {
        var handler = new StubHttpHandler((_, _) => new HttpResponseMessage
        {
            StatusCode = System.Net.HttpStatusCode.ServiceUnavailable,
            Content = new StringContent(
                "<html><body>503 Service Unavailable</body></html>",
                System.Text.Encoding.UTF8, "text/html"),
        });
        var ex = await Assert.ThrowsAsync<DeviceAuthException>(() =>
            new DeviceAuthService(new HttpClient(handler), TestSecrets)
                .RefreshAsync("RT"));
        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task Refresh_OAuthErrorIsNotTransient()
    {
        var handler = new StubHttpHandler((_, _) =>
            StubHttpHandler.JsonResponse("""{"error":"invalid_grant"}""", 400));
        var ex = await Assert.ThrowsAsync<DeviceAuthException>(() =>
            new DeviceAuthService(new HttpClient(handler), TestSecrets)
                .RefreshAsync("RT"));
        Assert.False(ex.IsTransient);
    }
}
