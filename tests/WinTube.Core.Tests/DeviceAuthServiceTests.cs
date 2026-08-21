using WinTube.Core;
using WinTube.Core.Auth;

namespace WinTube.Core.Tests;

public class DeviceAuthServiceTests
{
    private static readonly Secrets TestSecrets = new("K", "CLIENT_ID", "CLIENT_SECRET");

    [Fact]
    public async Task RequestCode_ParsesResponseAndSendsForm()
    {
        var handler = new StubHttpHandler((request, body) =>
        {
            Assert.Equal("https://www.youtube.com/o/oauth2/device/code",
                request.RequestUri!.ToString());
            Assert.Contains("client_id=CLIENT_ID", body);
            Assert.Contains("scope=", body);
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
            Assert.Contains("grant_type=http", body);
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
            Assert.Contains("grant_type=refresh_token", body);
            Assert.Contains("refresh_token=RT", body);
            return StubHttpHandler.JsonResponse("""{"access_token":"AT2"}""");
        });
        var tokens = await new DeviceAuthService(new HttpClient(handler), TestSecrets)
            .RefreshAsync("RT");
        Assert.Equal("AT2", tokens.AccessToken);
        Assert.Null(tokens.RefreshToken);   // caller keeps the existing one
    }
}
