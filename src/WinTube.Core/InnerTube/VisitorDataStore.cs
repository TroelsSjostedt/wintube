using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinTube.Core.InnerTube;

public sealed class VisitorDataException(string message) : Exception(message);

/// Fetches and caches the visitorData token the VISIONOS client needs on /player.
/// Without it that client answers LOGIN_REQUIRED with no streamingData; with it (and nothing
/// else — no PO token, no signature deciphering) it returns the full ladder plus an
/// hlsManifestUrl. The token is not tied to a user account.
public sealed partial class VisitorDataStore(HttpClient http)
{
    /// Tokens stay valid far longer; refetching hourly just bounds staleness.
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    private static readonly Uri PageUrl =
        new("https://www.youtube.com/tv?bpctr=9999999999&has_verified=1");

    private readonly SemaphoreSlim gate = new(1, 1);
    private string? cached;
    private DateTimeOffset fetchedAt;

    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (cached is not null && DateTimeOffset.UtcNow - fetchedAt < Ttl) return cached;

            using var request = new HttpRequestMessage(HttpMethod.Get, PageUrl);
            request.Headers.TryAddWithoutValidation(
                "User-Agent", Clients.Get(ClientKind.Tv).UserAgent);
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en");
            // Skips the EU consent interstitial, which otherwise replaces the bootstrap JSON.
            request.Headers.TryAddWithoutValidation("Cookie", "SOCS=CAE=");

            var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new VisitorDataException(
                    $"Could not reach YouTube to start a session (HTTP {(int)response.StatusCode})");
            var html = await response.Content.ReadAsStringAsync(ct);

            cached = Extract(html)
                ?? throw new VisitorDataException("Could not start a YouTube session");
            fetchedAt = DateTimeOffset.UtcNow;
            return cached;
        }
        finally { gate.Release(); }
    }

    /// Drops the cache so the next call refetches. Called when a request that used the token
    /// still came back unauthorized — what an expired token looks like.
    public void Invalidate()
    {
        cached = null;
        fetchedAt = default;
    }

    [GeneratedRegex("\"visitorData\":(\"[^\"]*\")")]
    private static partial Regex VisitorDataPattern();

    /// Pulls the value out of `"visitorData":"…"` in the page bootstrap. The embedded value is
    /// JSON-escaped (routinely contains = padding), so the complete string literal is
    /// decoded as JSON rather than unescaped by hand.
    public static string? Extract(string html)
    {
        var match = VisitorDataPattern().Match(html);
        if (!match.Success) return null;
        var decoded = JsonSerializer.Deserialize<string>(match.Groups[1].Value);
        return string.IsNullOrEmpty(decoded) ? null : decoded;
    }
}
