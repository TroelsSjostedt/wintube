using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WinTube.Core.InnerTube;

public sealed class InnerTubeException(int statusCode)
    : Exception($"InnerTube request failed (HTTP {statusCode})")
{
    public int StatusCode { get; } = statusCode;
}

/// Thin helper for youtubei/v1 POST calls: builds the `context` and headers for a client,
/// optionally attaching a Bearer token and visitorData. Port of the tvOS InnerTubeClient.
public sealed class InnerTubeClient(HttpClient http, Secrets secrets)
{
    public async Task<JsonDocument> PostAsync(
        string endpoint,
        ClientKind kind,
        IReadOnlyDictionary<string, object?> parameters,
        string? bearer = null,
        string? visitorData = null,
        CancellationToken ct = default)
    {
        var client = Clients.Get(kind);
        var url = $"{client.Host}/youtubei/v1/{endpoint}?key={secrets.InnerTubeApiKey}&prettyPrint=false";

        var clientContext = new Dictionary<string, object?>
        {
            ["clientName"] = client.Name,
            ["clientVersion"] = client.Version,
            ["hl"] = "en",
            ["gl"] = "US",
        };
        foreach (var (key, value) in client.ExtraContext) clientContext[key] = value;
        if (visitorData is not null) clientContext["visitorData"] = visitorData;

        var body = new Dictionary<string, object?>
        {
            ["context"] = new Dictionary<string, object?> { ["client"] = clientContext },
        };
        foreach (var (key, value) in parameters) body[key] = value;

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Youtube-Client-Name", client.NameId);
        request.Headers.TryAddWithoutValidation("X-Youtube-Client-Version", client.Version);
        request.Headers.TryAddWithoutValidation("User-Agent", client.UserAgent);
        if (client.Referer is not null) request.Headers.Referrer = new Uri(client.Referer);
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (visitorData is not null)
            request.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitorData);

        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new InnerTubeException((int)response.StatusCode);
        var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }
}
