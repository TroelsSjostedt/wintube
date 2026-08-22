using System.Text.Json;

namespace WinTube.Core.Auth;

public sealed record DeviceCode(
    string Code, string UserCode, string VerificationUrl, int IntervalSeconds, int ExpiresInSeconds);

public sealed record OAuthTokens(string AccessToken, string? RefreshToken);

public sealed class DeviceAuthException(string message) : Exception(message)
{
    /// True for a failure that says nothing about the tokens themselves (e.g. a 502/503 from
    /// the token endpoint) — callers should let the user retry rather than signing out.
    public bool IsTransient { get; init; }

    public static DeviceAuthException Expired() =>
        new("The sign-in code expired before you finished. Please try again.");

    public static DeviceAuthException OAuth(string code) => code switch
    {
        "access_denied" => new("Sign-in was denied. Please try again."),
        _ => new($"Sign-in failed ({code}). Please try again."),
    };

    public static DeviceAuthException Invalid() =>
        new("The server sent an unexpected response. Please try again.");
}

/// Drives the YouTube-on-TV OAuth 2.0 device-activation flow: request a user code, then poll
/// the token endpoint until the user authorizes in a browser. Port of the tvOS
/// DeviceAuthService; protocol details in reference/INNERTUBE.md (Auth section).
public sealed class DeviceAuthService(HttpClient http, Secrets secrets)
{
    private const string DeviceCodeUrl = "https://www.youtube.com/o/oauth2/device/code";
    private const string TokenUrl = "https://www.youtube.com/o/oauth2/token";
    private const string Scope =
        "http://gdata.youtube.com https://www.googleapis.com/auth/youtube-paid-content";
    private const string DeviceGrantType = "http://oauth.net/grant_type/device/1.0";

    /// Hard cap on total polling time, even if expires_in is larger.
    private const int MaxPollSeconds = 300;

    public async Task<DeviceCode> RequestCodeAsync(CancellationToken ct = default)
    {
        // device_id/device_model are required: without them the endpoint answers
        // invalid_request (verified 2026-08-22; bare client_id+scope worked in July 2026 and
        // no longer does). "ytlr::" is what the YouTube-on-TV client family identifies as —
        // the same value yt-dlp's oauth2 flow sends.
        using var doc = await PostJsonAsync(DeviceCodeUrl, new()
        {
            ["client_id"] = secrets.OAuthClientId,
            ["scope"] = Scope,
            ["device_id"] = Guid.NewGuid().ToString("N"),
            ["device_model"] = "ytlr::",
        }, ct);
        var json = doc.RootElement;

        if (json.TryGetProperty("error", out var error))
            throw DeviceAuthException.OAuth(error.GetString() ?? "unknown");

        var code = InnerTube.Json.StringAt(json, "device_code");
        var userCode = InnerTube.Json.StringAt(json, "user_code");
        var verificationUrl = InnerTube.Json.StringAt(json, "verification_url");
        if (code is null || userCode is null || verificationUrl is null)
            throw DeviceAuthException.Invalid();

        var interval = json.TryGetProperty("interval", out var i) ? i.GetInt32() : 5;
        var expires = json.TryGetProperty("expires_in", out var e) ? e.GetInt32() : MaxPollSeconds;
        return new DeviceCode(code, userCode, verificationUrl, Math.Max(1, interval), expires);
    }

    public async Task<OAuthTokens> PollAsync(
        string deviceCode, int intervalSeconds, CancellationToken ct = default)
    {
        var interval = intervalSeconds;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(MaxPollSeconds);
        var form = new Dictionary<string, string>
        {
            ["client_id"] = secrets.OAuthClientId,
            ["client_secret"] = secrets.OAuthClientSecret,
            ["code"] = deviceCode,
            ["grant_type"] = DeviceGrantType,
        };

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) throw DeviceAuthException.Expired();

            // Wait before (re)polling — the code isn't ready immediately. Clamped to the time
            // remaining so the cap is never overshot by a full interval.
            var sleep = TimeSpan.FromSeconds(Math.Min(interval, remaining.TotalSeconds));
            if (sleep > TimeSpan.Zero) await Task.Delay(sleep, ct);

            using var doc = await PostJsonAsync(TokenUrl, form, ct);
            var json = doc.RootElement;

            if (InnerTube.Json.StringAt(json, "access_token") is { } accessToken)
                return new OAuthTokens(accessToken, InnerTube.Json.StringAt(json, "refresh_token"));

            switch (InnerTube.Json.StringAt(json, "error"))
            {
                case "authorization_pending": continue;
                case "slow_down": interval += 5; continue;
                case "expired_token": throw DeviceAuthException.Expired();
                case { } other: throw DeviceAuthException.OAuth(other);
                default: throw DeviceAuthException.Invalid();
            }
        }
    }

    /// The response usually omits a new refresh token; the caller then keeps the existing one.
    public async Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        using var doc = await PostJsonAsync(TokenUrl, new()
        {
            ["client_id"] = secrets.OAuthClientId,
            ["client_secret"] = secrets.OAuthClientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        }, ct);
        var json = doc.RootElement;

        if (InnerTube.Json.StringAt(json, "error") is { } error)
            throw DeviceAuthException.OAuth(error);
        if (InnerTube.Json.StringAt(json, "access_token") is not { } accessToken)
            throw DeviceAuthException.Invalid();
        return new OAuthTokens(accessToken, InnerTube.Json.StringAt(json, "refresh_token"));
    }

    /// OAuth errors come back as JSON on 4xx, so JSON is parsed regardless of status; only a
    /// non-JSON failure throws on the HTTP status. The body is JSON, not form-encoded — this
    /// endpoint pair accepts (and yt-dlp sends) JSON, and the form shape stopped working for
    /// device/code in Aug 2026.
    private async Task<JsonDocument> PostJsonAsync(
        string url, Dictionary<string, string> fields, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(fields), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw response.IsSuccessStatusCode
                ? DeviceAuthException.Invalid()
                : new DeviceAuthException(
                    $"Network error (HTTP {(int)response.StatusCode}). Please try again.")
                    { IsTransient = true };
        }
    }
}
