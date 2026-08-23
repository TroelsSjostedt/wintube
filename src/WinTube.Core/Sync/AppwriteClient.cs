using System.Text;
using System.Text.Json;

namespace WinTube.Core.Sync;

/// A session as the app must remember it: Appwrite hands sessions out as cookies — the
/// response body's `secret` is empty (verified against 1.9.6 by the tvOS app) — so the
/// a_session_* pairs are stored and replayed by hand.
public sealed record AppwriteSession(string UserId, string Cookie);

public sealed class AppwriteException(int statusCode, string? serverMessage)
    : Exception(serverMessage is null
        ? $"Appwrite request failed (HTTP {statusCode})"
        : $"Appwrite: {serverMessage} (HTTP {statusCode})")
{
    public int StatusCode { get; } = statusCode;
    /// The session is gone or was never valid — re-authenticate rather than retry.
    public bool IsUnauthorized => StatusCode == 401;
}

/// Thin client for the handful of Appwrite endpoints the watch-progress sync needs.
/// Hand-rolled rather than an SDK — four REST calls don't justify a dependency tree.
/// Paths and headers follow the tvOS AppwriteClient (SDK v18.3.0 shapes, server 1.9.x).
public sealed class AppwriteClient(HttpClient http, AppwriteConfig config)
{
    /// Present once the profile has a session; null while only the auth function is callable.
    public AppwriteSession? Session { get; set; }

    // MARK: auth

    /// Trades a YouTube access token for an Appwrite session via the metube-auth function:
    /// the function proves who the token belongs to and mints a custom token, which is then
    /// exchanged for a session. See Backend/README.md in the metube repo.
    public async Task<AppwriteSession> SignInAsync(
        string accessToken, string accountKey, CancellationToken ct = default)
    {
        var innerBody = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["accessToken"] = accessToken,
            ["accountKey"] = accountKey,
        });
        using var execution = await SendAsync(HttpMethod.Post,
            $"/functions/{AppwriteConfig.AuthFunctionId}/executions",
            body: new Dictionary<string, object?>
            {
                ["body"] = innerBody,
                ["path"] = "/",
                ["method"] = "POST",
            }, ct: ct);

        // The function's own response rides inside the execution: its status is separate
        // from the execution's, which is 200 as long as the function ran at all.
        var json = execution.Document.RootElement;
        var status = json.TryGetProperty("responseStatusCode", out var s)
            ? InnerTube.Json.IntValue(s) ?? 0 : 0;
        JsonElement payload = default;
        if (InnerTube.Json.StringAt(json, "responseBody") is { } responseBody)
            try { payload = JsonDocument.Parse(responseBody).RootElement.Clone(); }
            catch (JsonException) { /* non-JSON function output; handled below */ }
        if (status is < 200 or >= 300)
            throw new AppwriteException(status, payload.ValueKind == JsonValueKind.Object
                ? InnerTube.Json.StringAt(payload, "message") : null);
        var userId = payload.ValueKind == JsonValueKind.Object
            ? InnerTube.Json.StringAt(payload, "userId") : null;
        var secret = payload.ValueKind == JsonValueKind.Object
            ? InnerTube.Json.StringAt(payload, "secret") : null;
        if (userId is null || secret is null)
            throw new AppwriteException(status, "auth function returned no session token");

        using var tokenResponse = await SendAsync(HttpMethod.Post, "/account/sessions/token",
            body: new Dictionary<string, object?> { ["userId"] = userId, ["secret"] = secret },
            ct: ct);
        var cookie = SessionCookie(tokenResponse.Response)
            ?? throw new AppwriteException(0, "no session cookie in response");
        return new AppwriteSession(userId, cookie);
    }

    /// The a_session_* cookies as a Cookie header value. HttpResponseMessage keeps repeated
    /// Set-Cookie headers as separate values, so no comma-fold parsing is needed.
    private static string? SessionCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies)) return null;
        var pairs = setCookies
            .Select(c => c.Split(';')[0].Trim())
            .Where(pair => pair.StartsWith("a_session_", StringComparison.Ordinal))
            .ToList();
        return pairs.Count == 0 ? null : string.Join("; ", pairs);
    }

    // MARK: rows

    /// One page of rows. `total` is not requested — the sync pages until a short page comes
    /// back, and counting rows costs the server an extra query.
    public async Task<IReadOnlyList<JsonElement>> ListRowsAsync(
        IEnumerable<string> queries, CancellationToken ct = default)
    {
        var query = string.Join("&",
            queries.Select(q => $"queries%5B%5D={Uri.EscapeDataString(q)}")
                .Append("total=false"));
        using var result = await SendAsync(HttpMethod.Get,
            $"/tablesdb/{AppwriteConfig.DatabaseId}/tables/{AppwriteConfig.TableId}/rows?{query}",
            ct: ct);
        if (!result.Document.RootElement.TryGetProperty("rows", out var rows) ||
            rows.ValueKind != JsonValueKind.Array)
            return [];
        return rows.EnumerateArray().Select(row => row.Clone()).ToList();
    }

    /// Creates or replaces a row. Row ids are derived, not generated, so this is the only
    /// write the sync needs — no read-before-write, and a repeated push is harmless.
    public async Task UpsertRowAsync(
        string rowId,
        IReadOnlyDictionary<string, object?> data,
        IReadOnlyList<string> permissions,
        CancellationToken ct = default)
    {
        using var _ = await SendAsync(HttpMethod.Put,
            $"/tablesdb/{AppwriteConfig.DatabaseId}/tables/{AppwriteConfig.TableId}/rows/{rowId}",
            body: new Dictionary<string, object?>
            {
                ["data"] = data,
                ["permissions"] = permissions,
            }, ct: ct);
    }

    // MARK: transport

    private sealed record SendResult(JsonDocument Document, HttpResponseMessage Response)
        : IDisposable
    {
        public void Dispose()
        {
            Document.Dispose();
            Response.Dispose();
        }
    }

    private async Task<SendResult> SendAsync(
        HttpMethod method, string pathAndQuery,
        IReadOnlyDictionary<string, object?>? body = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, config.Endpoint + pathAndQuery);
        request.Headers.TryAddWithoutValidation("X-Appwrite-Project", config.ProjectId);
        request.Headers.TryAddWithoutValidation(
            "X-Appwrite-Response-Format", AppwriteConfig.ResponseFormat);
        request.Headers.TryAddWithoutValidation("Origin", AppwriteConfig.Origin);
        if (Session is { } session)
            request.Headers.TryAddWithoutValidation("Cookie", session.Cookie);
        if (body is not null)
            request.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        // Parsed regardless of status: Appwrite puts its explanation in the error body,
        // which is a great deal more useful than the status code alone.
        JsonDocument? document = null;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException) { /* handled below */ }

        if (!response.IsSuccessStatusCode)
        {
            var message = document is not null
                ? InnerTube.Json.StringAt(document.RootElement, "message") : null;
            document?.Dispose();
            response.Dispose();
            throw new AppwriteException((int)response.StatusCode, message);
        }
        if (document is null)
        {
            response.Dispose();
            throw new AppwriteException(0, "response was not JSON");
        }
        return new SendResult(document, response);
    }
}
