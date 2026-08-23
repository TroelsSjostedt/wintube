# WinTube Sync Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Watch-progress sync against the self-hosted Appwrite backend the tvOS app already uses, so resume positions travel between the Apple TV and Windows.

**Architecture:** A hand-rolled `AppwriteClient` (4 REST calls, no SDK) plus a local-first `WatchProgressSync` engine, both in `WinTube.Core/Sync`, ported from the tvOS `AppwriteClient.swift`/`WatchProgressSync.swift`. `WatchProgressStore` gains the merge/queue primitives the engine needs. The app's `Session` owns the engine and wires the triggers. The backend is untouched.

**Tech Stack:** Existing stack only — .NET 8, System.Text.Json, DPAPI (`System.Security.Cryptography.ProtectedData`), xUnit with `StubHttpHandler`. No new NuGet packages.

**Spec:** `docs/specs/2026-08-23-wintube-sync-design.html` (approved 2026-08-23). Reference Swift sources: the metube checkout's `Sources/Core/AppwriteClient.swift`, `WatchProgressSync.swift`, `WatchProgressStore.swift` and `Backend/README.md` (re-clone `https://github.com/claust/metube` to a temp dir if the checkout named in the v1 plan is gone).

## Global Constraints

- Local-first and best-effort: the store stays the single source of truth; every sync/network failure is swallowed (a debug log line at most) and must never surface in the UI or crash anything.
- Identity: sync profile id = first 16 chars of the stored 64-hex `ProfileId`; Appwrite user id = `"yt" + syncProfileId` (received from the backend, never computed client-side for auth); row id = `{syncProfileId}_{videoId}`.
- Protocol constants (verbatim): endpoint = `https://{appwriteHost}/v1`; headers `X-Appwrite-Project: {projectId}`, `X-Appwrite-Response-Format: 1.9.5`, `Origin: appwrite-tvos://dk.delectosoft.metube`; database `metube`, table `watchProgress`, function `metube-auth`; session cookies are the `a_session_*` pairs from `Set-Cookie`, replayed by hand in a `Cookie` header (never a CookieContainer).
- Dates on the wire: ISO 8601 UTC with exactly 3 fractional digits — format string `yyyy-MM-dd'T'HH:mm:ss.fff'Z'` (invariant culture) on write; parse leniently with `DateTimeOffset.TryParse`.
- Engine numbers: flush debounce 15 s; pull page size 100; Window.Activated sync cooldown 60 s.
- Config: optional `appwriteHost` / `appwriteProjectId` keys in secrets.json; absent/empty = sync fully off (`AppwriteConfig.FromSecrets` returns null and nothing is constructed).
- Tests: `dotnet test tests/WinTube.Core.Tests` (79 green before this plan; verified by a fresh run at plan commit time). App build: `dotnet build src/WinTube.App -p:Platform=x64`. TreatWarningsAsErrors everywhere; `[SupportedOSPlatform("windows")]` for DPAPI types, as `TokenStore` already does.
- Working branch `master`; commit after each task with the given message.
- Live verification against the real server is the user's step at the end; nothing before it needs real credentials.

## File Structure

```
src/WinTube.Core/Secrets.cs                     MODIFY: + AppwriteHost, AppwriteProjectId
src/WinTube.Core/Sync/AppwriteConfig.cs         endpoint/ids derived from Secrets; protocol constants
src/WinTube.Core/Sync/AppwriteQuery.cs          JSON query-string builders
src/WinTube.Core/Sync/AppwriteClient.cs         sign-in, listRows, upsertRow; AppwriteSession; AppwriteException
src/WinTube.Core/Sync/AppwriteSessionStore.cs   DPAPI-protected session per profile
src/WinTube.Core/Sync/WatchProgressSync.cs      the engine
src/WinTube.Core/Stores/WatchProgressStore.cs   MODIFY: + LocalChanged, Merge, QueueAll, MarkSynced
src/WinTube.Core/Auth/TokenStore.cs             MODIFY: StoredProfile + AccountKey
src/WinTube.App/Session.cs                      MODIFY: owns sync; backfills AccountKey; wires activation
src/WinTube.App/MainWindow.xaml.cs              MODIFY: Activated → cooldown sync; Closed → flush
src/WinTube.App/Views/PlayerPage.xaml.cs        MODIFY: OnNavigatedFrom → FlushNow
tests/WinTube.Core.Tests/AppwriteQueryTests.cs
tests/WinTube.Core.Tests/AppwriteClientTests.cs
tests/WinTube.Core.Tests/AppwriteSessionStoreTests.cs
tests/WinTube.Core.Tests/WatchProgressStoreSyncTests.cs
tests/WinTube.Core.Tests/WatchProgressSyncTests.cs
```

---

### Task 1: Secrets extension and AppwriteConfig

**Files:**
- Modify: `src/WinTube.Core/Secrets.cs`
- Create: `src/WinTube.Core/Sync/AppwriteConfig.cs`
- Test: `tests/WinTube.Core.Tests/SecretsTests.cs` (extend), `tests/WinTube.Core.Tests/AppwriteConfigTests.cs`

**Interfaces:**
- Consumes: `Secrets` (v1).
- Produces: `Secrets` gains `string AppwriteHost` and `string AppwriteProjectId` (both `""` when absent — same policy as the existing keys). Namespace `WinTube.Core.Sync`: `sealed record AppwriteConfig(string Endpoint, string ProjectId)` with `static AppwriteConfig? FromSecrets(Secrets secrets)` (null unless both values are non-empty; `Endpoint = $"https://{host}/v1"`), and constants `const string DatabaseId = "metube"`, `const string TableId = "watchProgress"`, `const string AuthFunctionId = "metube-auth"`, `const string Origin = "appwrite-tvos://dk.delectosoft.metube"`, `const string ResponseFormat = "1.9.5"`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/WinTube.Core.Tests/SecretsTests.cs`:

```csharp
    [Fact]
    public void Load_ReadsOptionalAppwriteValues()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(path, """
            {"innerTubeApiKey":"K","oauthClientId":"I","oauthClientSecret":"S",
             "appwriteHost":"appwrite.example.dk","appwriteProjectId":"metube"}
            """);
        try
        {
            var secrets = Secrets.Load(path);
            Assert.Equal("appwrite.example.dk", secrets.AppwriteHost);
            Assert.Equal("metube", secrets.AppwriteProjectId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_AbsentAppwriteValues_AreEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(path, """{"innerTubeApiKey":"K"}""");
        try
        {
            var secrets = Secrets.Load(path);
            Assert.Equal("", secrets.AppwriteHost);
            Assert.Equal("", secrets.AppwriteProjectId);
        }
        finally { File.Delete(path); }
    }
```

New `tests/WinTube.Core.Tests/AppwriteConfigTests.cs`:

```csharp
using WinTube.Core;
using WinTube.Core.Sync;

namespace WinTube.Core.Tests;

public class AppwriteConfigTests
{
    [Fact]
    public void FromSecrets_BuildsEndpointFromHost()
    {
        var config = AppwriteConfig.FromSecrets(new Secrets("K", "I", "S")
        {
            AppwriteHost = "appwrite.example.dk",
            AppwriteProjectId = "metube",
        });
        Assert.NotNull(config);
        Assert.Equal("https://appwrite.example.dk/v1", config!.Endpoint);
        Assert.Equal("metube", config.ProjectId);
    }

    [Theory]
    [InlineData("", "metube")]
    [InlineData("host", "")]
    [InlineData("", "")]
    public void FromSecrets_MissingValue_ReturnsNull(string host, string projectId)
    {
        Assert.Null(AppwriteConfig.FromSecrets(new Secrets("K", "I", "S")
        {
            AppwriteHost = host,
            AppwriteProjectId = projectId,
        }));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/WinTube.Core.Tests` — expected: compile FAIL (`AppwriteHost` missing).

- [ ] **Step 3: Implement**

In `src/WinTube.Core/Secrets.cs`, change the record to carry the two optional values as init properties (keeps the existing 3-arg positional ctor so nothing else changes):

```csharp
public sealed record Secrets(string InnerTubeApiKey, string OAuthClientId, string OAuthClientSecret)
{
    /// Optional: the personal Appwrite behind watch-progress sync. Host only — the scheme
    /// and Appwrite's fixed /v1 are appended in code. Empty (the fresh-clone default) keeps
    /// sync off and the app fully local, same policy as the tvOS Secrets.xcconfig.
    public string AppwriteHost { get; init; } = "";
    public string AppwriteProjectId { get; init; } = "";
    // …existing DefaultPath + Load…
}
```

and in `Load`, after building the record: `return new Secrets(…) { AppwriteHost = Get("appwriteHost"), AppwriteProjectId = Get("appwriteProjectId") };` (reusing the existing `Get` local).

New `src/WinTube.Core/Sync/AppwriteConfig.cs`:

```csharp
namespace WinTube.Core.Sync;

/// Where the watch-progress backend lives, and the protocol constants every call shares.
/// Null when secrets.json names no Appwrite — sync is then fully off (fresh-clone default).
public sealed record AppwriteConfig(string Endpoint, string ProjectId)
{
    public const string DatabaseId = "metube";
    public const string TableId = "watchProgress";
    public const string AuthFunctionId = "metube-auth";
    /// Appwrite matches this against the platforms registered on the project. The tvOS
    /// platform registration is reused deliberately — zero server changes; a dedicated
    /// Windows platform can be registered later without touching this app's rows.
    public const string Origin = "appwrite-tvos://dk.delectosoft.metube";
    public const string ResponseFormat = "1.9.5";

    public static AppwriteConfig? FromSecrets(Secrets secrets) =>
        string.IsNullOrEmpty(secrets.AppwriteHost) || string.IsNullOrEmpty(secrets.AppwriteProjectId)
            ? null
            : new AppwriteConfig($"https://{secrets.AppwriteHost}/v1", secrets.AppwriteProjectId);
}
```

Also add the two keys (empty) to `secrets.example.json` with a one-line comment removed (JSON has none — instead name them in README later, Task 9):

```json
{
  "innerTubeApiKey": "<the public InnerTube web API key>",
  "oauthClientId": "<the public YouTube-on-TV client id, *.apps.googleusercontent.com>",
  "oauthClientSecret": "<the public YouTube-on-TV client secret>",
  "appwriteHost": "",
  "appwriteProjectId": ""
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/WinTube.Core.Tests` — expected: PASS (previous count + 4).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: optional Appwrite configuration in secrets"
```

---

### Task 2: AppwriteQuery

**Files:**
- Create: `src/WinTube.Core/Sync/AppwriteQuery.cs`
- Test: `tests/WinTube.Core.Tests/AppwriteQueryTests.cs`

**Interfaces:**
- Produces (namespace `WinTube.Core.Sync`): `static class AppwriteQuery` with `string Equal(string attribute, string value)`, `string GreaterThan(string attribute, string value)`, `string OrderAsc(string attribute)`, `string Limit(int value)`, `string CursorAfter(string rowId)` — each a compact JSON object `{"method":…,"attribute":…,"values":[…]}` (attribute omitted for Limit/CursorAfter; values omitted for OrderAsc), exactly the shape the tvOS `AppwriteQuery` emits.

- [ ] **Step 1: Write the failing tests**

`tests/WinTube.Core.Tests/AppwriteQueryTests.cs`:

```csharp
using System.Text.Json;
using WinTube.Core.Sync;

namespace WinTube.Core.Tests;

public class AppwriteQueryTests
{
    private static JsonElement Parse(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void Equal_EncodesMethodAttributeAndValues()
    {
        var json = Parse(AppwriteQuery.Equal("userId", "yt123"));
        Assert.Equal("equal", json.GetProperty("method").GetString());
        Assert.Equal("userId", json.GetProperty("attribute").GetString());
        Assert.Equal("yt123", json.GetProperty("values")[0].GetString());
    }

    [Fact]
    public void GreaterThan_EncodesLikeEqual()
    {
        var json = Parse(AppwriteQuery.GreaterThan("watchedAt", "2026-01-01T00:00:00.000Z"));
        Assert.Equal("greaterThan", json.GetProperty("method").GetString());
        Assert.Equal("watchedAt", json.GetProperty("attribute").GetString());
    }

    [Fact]
    public void OrderAsc_OmitsValues()
    {
        var json = Parse(AppwriteQuery.OrderAsc("watchedAt"));
        Assert.Equal("orderAsc", json.GetProperty("method").GetString());
        Assert.False(json.TryGetProperty("values", out _));
    }

    [Fact]
    public void Limit_CarriesNumericValue_NoAttribute()
    {
        var json = Parse(AppwriteQuery.Limit(100));
        Assert.Equal(100, json.GetProperty("values")[0].GetInt32());
        Assert.False(json.TryGetProperty("attribute", out _));
    }

    [Fact]
    public void CursorAfter_CarriesRowId()
    {
        var json = Parse(AppwriteQuery.CursorAfter("abc_def"));
        Assert.Equal("cursorAfter", json.GetProperty("method").GetString());
        Assert.Equal("abc_def", json.GetProperty("values")[0].GetString());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/WinTube.Core.Tests` — expected: compile FAIL.

- [ ] **Step 3: Implement**

`src/WinTube.Core/Sync/AppwriteQuery.cs`:

```csharp
using System.Text.Json;

namespace WinTube.Core.Sync;

/// The query strings Appwrite expects in queries[], built by hand for the handful the sync
/// uses. The SDK's Query helper is a thin JSON encoder over the same shape.
public static class AppwriteQuery
{
    public static string Equal(string attribute, string value) =>
        Encode("equal", attribute, [value]);

    public static string GreaterThan(string attribute, string value) =>
        Encode("greaterThan", attribute, [value]);

    public static string OrderAsc(string attribute) =>
        Encode("orderAsc", attribute, null);

    public static string Limit(int value) =>
        Encode("limit", null, [value]);

    public static string CursorAfter(string rowId) =>
        Encode("cursorAfter", null, [rowId]);

    private static string Encode(string method, string? attribute, object[]? values)
    {
        var obj = new Dictionary<string, object> { ["method"] = method };
        if (attribute is not null) obj["attribute"] = attribute;
        if (values is not null) obj["values"] = values;
        return JsonSerializer.Serialize(obj);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/WinTube.Core.Tests` — expected: PASS (+5).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: Appwrite query-string builders"
```

---

### Task 3: AppwriteClient

**Files:**
- Create: `src/WinTube.Core/Sync/AppwriteClient.cs`
- Test: `tests/WinTube.Core.Tests/AppwriteClientTests.cs`

**Interfaces:**
- Consumes: `AppwriteConfig` (Task 1), `AppwriteQuery` (Task 2 — in tests), `Json` helpers, `StubHttpHandler`.
- Produces (namespace `WinTube.Core.Sync`):
  - `sealed record AppwriteSession(string UserId, string Cookie)`
  - `sealed class AppwriteException(int statusCode, string? serverMessage) : Exception` — message `"Appwrite: {serverMessage} (HTTP {statusCode})"` or `"Appwrite request failed (HTTP {statusCode})"`; `int StatusCode`; `bool IsUnauthorized => StatusCode == 401`.
  - `sealed class AppwriteClient(HttpClient http, AppwriteConfig config)` with mutable `AppwriteSession? Session { get; set; }` and:
    - `Task<AppwriteSession> SignInAsync(string accessToken, string accountKey, CancellationToken ct = default)` — POST `/functions/metube-auth/executions` with body `{"body": "<json of {accessToken, accountKey}>", "path": "/", "method": "POST"}`; unwrap `responseStatusCode`/`responseBody`; non-2xx inner status → `AppwriteException(innerStatus, message from responseBody)`; then POST `/account/sessions/token` `{userId, secret}` and build the session from the `a_session_*` cookies (name=value pairs, `; `-joined, order preserved). Missing cookie → `AppwriteException(0, "no session cookie in response")`.
    - `Task<IReadOnlyList<JsonElement>> ListRowsAsync(IEnumerable<string> queries, CancellationToken ct = default)` — GET `/tablesdb/metube/tables/watchProgress/rows?queries[]=…&queries[]=…&total=false`; returns the `rows` array items (each `.Clone()`d so they outlive the document).
    - `Task UpsertRowAsync(string rowId, IReadOnlyDictionary<string, object?> data, IReadOnlyList<string> permissions, CancellationToken ct = default)` — PUT `/tablesdb/metube/tables/watchProgress/rows/{rowId}` body `{"data": …, "permissions": …}`.
  - Every call: headers per Global Constraints; `Cookie: {Session.Cookie}` when a session is set; error body parsed regardless of status and `message` used; 401 → `AppwriteException` with `IsUnauthorized` true.

- [ ] **Step 1: Write the failing tests**

`tests/WinTube.Core.Tests/AppwriteClientTests.cs`:

```csharp
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
        Assert.Equal(1, handler.Requests.Count);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/WinTube.Core.Tests` — expected: compile FAIL.

- [ ] **Step 3: Implement**

`src/WinTube.Core/Sync/AppwriteClient.cs`:

```csharp
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
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/WinTube.Core.Tests` — expected: PASS (+5).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: hand-rolled Appwrite client (sign-in, list, upsert)"
```

---

### Task 4: AppwriteSessionStore

**Files:**
- Create: `src/WinTube.Core/Sync/AppwriteSessionStore.cs`
- Test: `tests/WinTube.Core.Tests/AppwriteSessionStoreTests.cs`

**Interfaces:**
- Consumes: `AppwriteSession` (Task 3).
- Produces (namespace `WinTube.Core.Sync`): `sealed class AppwriteSessionStore(string rootDirectory)` (annotated `[SupportedOSPlatform("windows")]`) with `void Save(string profileId, AppwriteSession session)`, `AppwriteSession? Load(string profileId)`, `void Delete(string profileId)`. DPAPI-protected JSON at `<root>\profiles\<profileId>\appwrite-session.bin`; `Load` returns null on missing/corrupt/undecryptable. Same pattern as `TokenStore` — the cookie authenticates requests, so it gets the same protection the OAuth tokens do.

- [ ] **Step 1: Write the failing tests**

`tests/WinTube.Core.Tests/AppwriteSessionStoreTests.cs`:

```csharp
using System.Runtime.Versioning;
using WinTube.Core.Sync;

namespace WinTube.Core.Tests;

[SupportedOSPlatform("windows")]
public class AppwriteSessionStoreTests
{
    private static string TempDir() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())).FullName;

    [Fact]
    public void SaveLoadDelete_RoundTripsPerProfile()
    {
        var dir = TempDir();
        var store = new AppwriteSessionStore(dir);
        var session = new AppwriteSession("yt1234", "a_session_metube=abc");
        store.Save("p1", session);

        Assert.Equal(session, new AppwriteSessionStore(dir).Load("p1"));
        Assert.Null(store.Load("p2"));

        var raw = File.ReadAllBytes(Path.Combine(dir, "profiles", "p1", "appwrite-session.bin"));
        Assert.DoesNotContain("a_session", System.Text.Encoding.UTF8.GetString(raw));

        store.Delete("p1");
        Assert.Null(store.Load("p1"));
    }

    [Fact]
    public void Load_MissingOrCorrupt_ReturnsNull()
    {
        var dir = TempDir();
        var store = new AppwriteSessionStore(dir);
        Assert.Null(store.Load("p1"));
        Directory.CreateDirectory(Path.Combine(dir, "profiles", "p1"));
        File.WriteAllText(Path.Combine(dir, "profiles", "p1", "appwrite-session.bin"), "junk");
        Assert.Null(store.Load("p1"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/WinTube.Core.Tests` — expected: compile FAIL.

- [ ] **Step 3: Implement**

`src/WinTube.Core/Sync/AppwriteSessionStore.cs`:

```csharp
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace WinTube.Core.Sync;

/// The Appwrite session cookie, DPAPI-protected per profile — it authenticates requests,
/// so it gets the same treatment as the OAuth tokens. Deleting it merely forces a fresh
/// sign-in through the auth function; the backend rows are untouched.
[SupportedOSPlatform("windows")]
public sealed class AppwriteSessionStore(string rootDirectory)
{
    private string FilePath(string profileId) =>
        Path.Combine(rootDirectory, "profiles", profileId, "appwrite-session.bin");

    public void Save(string profileId, AppwriteSession session)
    {
        var path = FilePath(profileId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var plain = JsonSerializer.SerializeToUtf8Bytes(session);
        File.WriteAllBytes(path,
            ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
    }

    public AppwriteSession? Load(string profileId)
    {
        try
        {
            var plain = ProtectedData.Unprotect(
                File.ReadAllBytes(FilePath(profileId)), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<AppwriteSession>(plain);
        }
        catch (Exception e) when (e is IOException or CryptographicException or JsonException)
        {
            return null;
        }
    }

    public void Delete(string profileId)
    {
        try { File.Delete(FilePath(profileId)); }
        catch (IOException) { /* a locked or missing file is not worth failing sign-out over */ }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/WinTube.Core.Tests` — expected: PASS (+2).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: DPAPI-protected Appwrite session store"
```

---

### Task 5: WatchProgressStore sync primitives

**Files:**
- Modify: `src/WinTube.Core/Stores/WatchProgressStore.cs`
- Test: `tests/WinTube.Core.Tests/WatchProgressStoreSyncTests.cs` (new file; existing `WatchProgressStoreTests.cs` must stay green untouched)

**Interfaces:**
- Produces (additions to `WatchProgressStore`):
  - `event Action? LocalChanged;` — raised by `Report` only (after persisting), never by `Merge`/`QueueAll`/`MarkSynced`/`Activate`. The existing `Changed` (UI refresh) keeps firing exactly as today, plus after a `Merge` that changed anything.
  - `void Merge(IReadOnlyDictionary<string, ProgressEntry> remote)` — last-writer-wins on `UpdatedAt` (strictly newer wins; ties keep local); merged entries are NOT added to `Dirty`; persists and raises `Changed` only when something changed; no-op when no profile is active.
  - `void QueueAll(IReadOnlySet<string> except)` — adds every entry key not in `except` to `Dirty`; persists only when the set grew.
  - `void MarkSynced(IReadOnlyDictionary<string, DateTimeOffset> pushed)` — removes a video from `Dirty` only when its current `UpdatedAt` equals the pushed value (playback that moved on mid-upload stays queued); persists.

These are the tvOS `merge(remote:)`, `queueAll(except:)`, `markSynced(_:)` semantics verbatim (see `WatchProgressStore.swift:192-234`).

- [ ] **Step 1: Write the failing tests**

`tests/WinTube.Core.Tests/WatchProgressStoreSyncTests.cs`:

```csharp
using WinTube.Core.Stores;

namespace WinTube.Core.Tests;

public class WatchProgressStoreSyncTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private static WatchProgressStore Make(out string dir)
    {
        dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())).FullName;
        var store = new WatchProgressStore(dir, () => T0);
        store.Activate("p1");
        return store;
    }

    [Fact]
    public void Merge_NewerRemoteWins_AndIsNotDirty()
    {
        var store = Make(out _);
        store.Report("v1", 100, 600);                          // local, dirty, UpdatedAt = T0
        store.Merge(new Dictionary<string, ProgressEntry>
        {
            ["v1"] = new(200, 600, T0.AddMinutes(5)),          // newer remote
            ["v2"] = new(50, 300, T0.AddMinutes(-5)),          // new video
        });
        Assert.Equal(200, store.Entries["v1"].PositionSeconds);
        Assert.Equal(50, store.Entries["v2"].PositionSeconds);
        Assert.Contains("v1", store.Dirty);                    // local edit still queued
        Assert.DoesNotContain("v2", store.Dirty);              // merged, never dirty
    }

    [Fact]
    public void Merge_OlderRemoteLoses()
    {
        var store = Make(out _);
        store.Report("v1", 100, 600);
        store.Merge(new Dictionary<string, ProgressEntry>
        {
            ["v1"] = new(30, 600, T0.AddMinutes(-10)),
        });
        Assert.Equal(100, store.Entries["v1"].PositionSeconds);
    }

    [Fact]
    public void Merge_RaisesChangedButNeverLocalChanged()
    {
        var store = Make(out _);
        int changed = 0, local = 0;
        store.Changed += () => changed++;
        store.LocalChanged += () => local++;
        store.Merge(new Dictionary<string, ProgressEntry>
        {
            ["v1"] = new(50, 300, T0),
        });
        Assert.Equal(1, changed);
        Assert.Equal(0, local);
    }

    [Fact]
    public void Report_RaisesLocalChanged()
    {
        var store = Make(out _);
        var local = 0;
        store.LocalChanged += () => local++;
        store.Report("v1", 100, 600);
        Assert.Equal(1, local);
    }

    [Fact]
    public void QueueAll_QueuesEverythingExceptMerged()
    {
        var store = Make(out _);
        store.Report("v1", 100, 600);
        store.Merge(new Dictionary<string, ProgressEntry>
        {
            ["v2"] = new(50, 300, T0),
            ["v3"] = new(60, 300, T0),
        });
        store.QueueAll(new HashSet<string> { "v2", "v3" });
        Assert.Equal(new[] { "v1" }, store.Dirty.OrderBy(x => x));

        store.QueueAll(new HashSet<string>());                 // nothing excepted now
        Assert.Equal(new[] { "v1", "v2", "v3" }, store.Dirty.OrderBy(x => x));
    }

    [Fact]
    public void MarkSynced_ClearsOnlyUnchangedEntries_AndPersists()
    {
        var store = Make(out var dir);
        store.Report("v1", 100, 600);
        var pushedAt = store.Entries["v1"].UpdatedAt;
        store.Report("v2", 100, 600);

        store.MarkSynced(new Dictionary<string, DateTimeOffset>
        {
            ["v1"] = pushedAt,
            ["v2"] = pushedAt.AddMinutes(-1),                  // stale push — moved on since
        });
        Assert.DoesNotContain("v1", store.Dirty);
        Assert.Contains("v2", store.Dirty);

        var reloaded = new WatchProgressStore(dir, () => T0);
        reloaded.Activate("p1");
        Assert.DoesNotContain("v1", reloaded.Dirty);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/WinTube.Core.Tests` — expected: compile FAIL.

- [ ] **Step 3: Implement**

In `src/WinTube.Core/Stores/WatchProgressStore.cs`, add beside `Changed`:

```csharp
    /// Raised by Report only — a real local playback write, the thing a sync should push.
    /// Merges deliberately do not raise it: pushing back what the backend just sent would
    /// be a round trip that changes nothing.
    public event Action? LocalChanged;
```

raise it at the end of `Report` (after `Changed?.Invoke()`), and add:

```csharp
    // MARK: syncing

    /// Folds in what the backend has, keeping whichever version of each video is newer.
    /// Last-writer-wins by UpdatedAt — the right rule for one household: an unpushed local
    /// edit is newer than anything the backend can know about, so it wins and stays queued.
    public void Merge(IReadOnlyDictionary<string, ProgressEntry> remote)
    {
        if (profileId is null) return;
        var changed = false;
        foreach (var (videoId, entry) in remote)
        {
            if (entries.TryGetValue(videoId, out var local) && local.UpdatedAt >= entry.UpdatedAt)
                continue;
            entries[videoId] = entry;
            changed = true;
        }
        if (!changed) return;
        Persist();
        Changed?.Invoke();
    }

    /// Queues everything the device already knows, so a history that predates syncing is
    /// uploaded rather than sitting there being older than a backend that never heard of it.
    /// Run once per profile, after its first successful pull; `except` names what that pull
    /// just took FROM the backend.
    public void QueueAll(IReadOnlySet<string> except)
    {
        if (profileId is null) return;
        var owed = entries.Keys.Where(id => !except.Contains(id) && !dirty.Contains(id)).ToList();
        if (owed.Count == 0) return;
        foreach (var id in owed) dirty.Add(id);
        Persist();
    }

    /// Drops pushed entries from the queue — but only those the user hasn't moved on from:
    /// a video still playing while its position uploads gets a newer UpdatedAt mid-flight,
    /// and clearing that would strand the newer position.
    public void MarkSynced(IReadOnlyDictionary<string, DateTimeOffset> pushed)
    {
        if (profileId is null) return;
        foreach (var (videoId, updatedAt) in pushed)
            if (entries.TryGetValue(videoId, out var entry) && entry.UpdatedAt == updatedAt)
                dirty.Remove(videoId);
        Persist();
    }
```

(`profileId`, `entries`, `dirty`, `Persist` already exist with these names in the v1 file.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/WinTube.Core.Tests` — expected: PASS (+6), all pre-existing tests still green.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: watch-progress store sync primitives (merge, queue, mark-synced)"
```

---

### Task 6: StoredProfile.AccountKey

**Files:**
- Modify: `src/WinTube.Core/Auth/TokenStore.cs` (the `StoredProfile` record)
- Test: `tests/WinTube.Core.Tests/TokenStoreTests.cs` (extend)

**Interfaces:**
- Produces: `StoredProfile` gains a trailing optional positional parameter `string? AccountKey = null` — the raw YouTube account key (obfuscated Gaia id) that the metube-auth function verifies. Optional-with-default keeps both the existing 5-argument construction sites and old serialized tokens.bin files (missing property → null) working. Task 8 backfills it via `accounts_list` when null.

- [ ] **Step 1: Write the failing tests**

Append to `tests/WinTube.Core.Tests/TokenStoreTests.cs`:

```csharp
    [Fact]
    public void SaveLoad_RoundTripsAccountKey()
    {
        var dir = TempDir();
        var store = new TokenStore(dir);
        var profile = new StoredProfile("pid", "Name", null, "AT", "RT", "GAIA123");
        store.Save(profile);
        Assert.Equal("GAIA123", store.Load()!.AccountKey);
    }

    [Fact]
    public void Load_ProfileSavedWithoutAccountKey_HasNull()
    {
        var dir = TempDir();
        new TokenStore(dir).Save(new StoredProfile("pid", "Name", null, "AT", "RT"));
        Assert.Null(new TokenStore(dir).Load()!.AccountKey);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/WinTube.Core.Tests` — expected: compile FAIL (6th argument).

- [ ] **Step 3: Implement**

```csharp
public sealed record StoredProfile(
    string ProfileId, string Name, string? AvatarUrl, string AccessToken, string RefreshToken,
    string? AccountKey = null);
```

with a doc comment on the new parameter's line: the raw YouTube account key (obfuscated Gaia id), needed by the sync's auth function; null in profiles stored before stage 2, backfilled on the next launch.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/WinTube.Core.Tests` — expected: PASS (+2).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: persist the raw account key alongside the tokens"
```

---

### Task 7: WatchProgressSync engine

**Files:**
- Create: `src/WinTube.Core/Sync/WatchProgressSync.cs`
- Test: `tests/WinTube.Core.Tests/WatchProgressSyncTests.cs`

**Interfaces:**
- Consumes: `WatchProgressStore` (+Task 5 members), `AppwriteClient`/`AppwriteSession` (Task 3), `AppwriteSessionStore` (Task 4), `AppwriteQuery` (Task 2), `AppwriteConfig` (Task 1).
- Produces (namespace `WinTube.Core.Sync`, `[SupportedOSPlatform("windows")]`):

```csharp
public sealed class WatchProgressSync(
    WatchProgressStore store,
    AppwriteClient client,
    AppwriteSessionStore sessions,
    string rootDirectory,
    Func<TimeSpan, CancellationToken, Task>? delay = null)   // injectable for tests; default Task.Delay
{
    public void Activate(string? profileId, string? accountKey, string? accessToken);
    public void Sync();       // pull then push, cancelling any run in flight
    public void FlushNow();   // immediate push, cancelling the debounce and any run
    public Task? RunningTask { get; }   // the current pull/push task, for tests to await
}
```

Behavior to port from `WatchProgressSync.swift` (the Swift file is the arbiter):

- **Activation** (`Activate`): cancel the debounce, the running task, and any sign-in in flight (a switch mid-sign-in must not hand the new profile the old one's user id). Null/missing any of the three parameters → deactivated. Otherwise: derive `syncProfileId = profileId[..16]`, load a stored session into `client.Session` (`sessions.Load(syncProfileId)`), subscribe to `store.LocalChanged` (subscribe once in the constructor; the handler checks activation), and run `Sync()`.
- **Debounce**: `LocalChanged` schedules a push after **15 s**, restarting the window on each further change. `FlushNow` cancels the debounce and pushes immediately (skips when `store.Dirty` is empty).
- **Single-flight sign-in**: when `client.Session` is null, call `client.SignInAsync(accessToken, accountKey)`; concurrent callers await the same task; on success adopt (set `client.Session`, `sessions.Save`) — but only if the same profile is still active; on ANY exception log-and-return-null (sync skipped this round).
- **Pull**: incremental by high-water mark, persisted as plain text at `<root>\profiles\<syncProfileId>\sync-pulled-at.txt` (missing file = never pulled). Page with `Equal("userId", userId)`, `OrderAsc("watchedAt")`, `Limit(100)`, plus `GreaterThan("watchedAt", since)` when a mark exists and `CursorAfter(lastRowId)` from page 2 on. Fold rows into a merged dictionary (`videoId` → `ProgressEntry(position, duration, watchedAt)`; skip malformed rows); track the newest `watchedAt` string seen (rows arrive ascending, so the last one wins). Stop on a short page. A failed page ends the pull WITHOUT advancing the mark. Then: `store.Merge(merged)`; if the mark file did not exist (first pull ever), `store.QueueAll(except: merged.Keys)`; finally write the new mark (only when one was seen).
- **Push**: snapshot `store.Dirty`; for each queued id with an entry, `UpsertRowAsync(rowId: $"{syncProfileId}_{videoId}", data: {userId, videoId, position, duration, watchedAt}, permissions: read/update/delete for `user:{userId}`)`; collect pushed `(videoId, UpdatedAt)`; on `AppwriteException.IsUnauthorized` → discard session (`client.Session = null`, `sessions.Delete`) and stop; on any other exception stop (row stays queued). Afterwards `store.MarkSynced(pushed)` if anything pushed and the profile is unchanged.
- **Dates**: write `entry.UpdatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)`; parse with `DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out …)`.
- **Failure policy**: every network/auth failure is caught inside the engine; nothing ever escapes to callers. Debug-log via `System.Diagnostics.Debug.WriteLine("[WatchProgressSync] …")`.
- **Concurrency**: the engine is called from the UI thread; each Activate creates a fresh `CancellationTokenSource` whose token guards all async continuations (check `ct.IsCancellationRequested` after every await before touching state; also compare a captured activation generation so a stale continuation can't write another profile's data).

- [ ] **Step 1: Write the failing tests**

`tests/WinTube.Core.Tests/WatchProgressSyncTests.cs` — a stub-HTTP harness that scripts server behavior. All 64-hex profile ids in tests use `new string('a', 64)` so `syncId` is `"aaaaaaaaaaaaaaaa"` and `userId` is `"ytaaaaaaaaaaaaaaaa"`:

```csharp
using System.Runtime.Versioning;
using System.Text.Json;
using WinTube.Core.Stores;
using WinTube.Core.Sync;

namespace WinTube.Core.Tests;

[SupportedOSPlatform("windows")]
public class WatchProgressSyncTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly string ProfileId = new('a', 64);
    private const string SyncId = "aaaaaaaaaaaaaaaa";
    private const string UserId = "ytaaaaaaaaaaaaaaaa";

    private sealed class Harness
    {
        public string Dir { get; }
        public WatchProgressStore Store { get; }
        public WatchProgressSync Sync { get; }
        public List<(HttpRequestMessage Message, string Body, string Url)> Requests { get; } = [];
        /// Rows the fake server returns for the first list call; second call returns empty.
        public List<string> PullRows { get; set; } = [];
        public int ListCalls { get; private set; }
        public bool RejectWith401 { get; set; }
        public TaskCompletionSource? DebounceGate { get; private set; }

        public Harness()
        {
            Dir = Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())).FullName;
            Store = new WatchProgressStore(Dir, () => T0);
            Store.Activate(ProfileId);

            var handler = new StubHttpHandler((request, body) =>
            {
                var url = request.RequestUri!.ToString();
                Requests.Add((request, body, url));
                if (RejectWith401)
                    return StubHttpHandler.JsonResponse("""{"message":"expired"}""", 401);
                if (url.Contains("/executions"))
                    return StubHttpHandler.JsonResponse("""
                        {"responseStatusCode":200,
                         "responseBody":"{\"userId\":\"ytaaaaaaaaaaaaaaaa\",\"secret\":\"SEC\"}"}
                        """);
                if (url.Contains("/sessions/token"))
                {
                    var response = StubHttpHandler.JsonResponse("""{"secret":""}""", 201);
                    response.Headers.TryAddWithoutValidation(
                        "Set-Cookie", "a_session_metube=C1; path=/");
                    return response;
                }
                if (url.Contains("/rows?"))
                {
                    ListCalls++;
                    var rows = ListCalls == 1 ? string.Join(",", PullRows) : "";
                    return StubHttpHandler.JsonResponse($$"""{"rows":[{{rows}}]}""");
                }
                return StubHttpHandler.JsonResponse("""{"$id":"row"}""");   // upsert
            });
            var client = new AppwriteClient(
                new HttpClient(handler), new AppwriteConfig("https://aw.example/v1", "metube"));
            // Debounce is gated so tests control when the 15 s window "elapses".
            Sync = new WatchProgressSync(Store, client, new AppwriteSessionStore(Dir), Dir,
                delay: (_, ct) =>
                {
                    DebounceGate = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    return DebounceGate.Task.WaitAsync(ct);
                });
        }

        public static string Row(string videoId, double position, string watchedAt) => $$"""
            {"$id":"{{SyncId}}_{{videoId}}","userId":"{{UserId}}","videoId":"{{videoId}}",
             "position":{{position}},"duration":600.0,"watchedAt":"{{watchedAt}}"}
            """;

        public async Task Activated()
        {
            Sync.Activate(ProfileId, "GAIA", "AT");
            if (Sync.RunningTask is { } task) await task;
        }
    }

    [Fact]
    public async Task Activate_SignsInPullsAndMergesRemoteRows()
    {
        var harness = new Harness
        {
            PullRows = [Harness.Row("v1", 120, "2026-08-23T10:00:00.000Z")],
        };
        await harness.Activated();

        Assert.Equal(120, harness.Store.Entries["v1"].PositionSeconds);
        Assert.DoesNotContain("v1", harness.Store.Dirty);   // merged, not queued
        // Sign-in happened: execution + token exchange preceded the list.
        Assert.Contains(harness.Requests, r => r.Url.Contains("/executions"));
        // Session was persisted for the next launch.
        Assert.NotNull(new AppwriteSessionStore(harness.Dir).Load(SyncId));
    }

    [Fact]
    public async Task FirstPull_QueuesPreexistingLocalHistory()
    {
        var harness = new Harness
        {
            PullRows = [Harness.Row("remote", 60, "2026-08-23T10:00:00.000Z")],
        };
        harness.Store.Report("local", 100, 600);
        harness.Store.MarkSynced(new Dictionary<string, DateTimeOffset>
            { ["local"] = harness.Store.Entries["local"].UpdatedAt });   // start with empty queue
        Assert.Empty(harness.Store.Dirty);

        await harness.Activated();

        // The pre-sync local video was owed to the backend: it is either still queued or
        // already pushed by the activation's own push pass.
        Assert.True(harness.Store.Dirty.Contains("local") ||
            harness.PushedIds().Contains("local"));
        // The remote row must NOT have been queued or pushed back.
        Assert.DoesNotContain(harness.Requests,
            r => r.Url.Contains($"/rows/{SyncId}_remote"));
    }

    [Fact]
    public async Task Pull_PersistsHighWaterMark_AndUsesItNextTime()
    {
        var harness = new Harness
        {
            PullRows = [Harness.Row("v1", 60, "2026-08-23T10:00:00.000Z")],
        };
        await harness.Activated();

        var markPath = Path.Combine(harness.Dir, "profiles", SyncId, "sync-pulled-at.txt");
        Assert.Equal("2026-08-23T10:00:00.000Z", File.ReadAllText(markPath));

        harness.Requests.Clear();
        harness.Sync.Sync();
        if (harness.Sync.RunningTask is { } task) await task;
        var listUrl = harness.Requests.First(r => r.Url.Contains("/rows?")).Url;
        Assert.Contains("greaterThan", Uri.UnescapeDataString(listUrl));
        Assert.Contains("2026-08-23T10:00:00.000Z", Uri.UnescapeDataString(listUrl));
    }

    [Fact]
    public async Task DebouncedLocalChange_PushesRowWithPermissions()
    {
        var harness = new Harness();
        await harness.Activated();
        harness.Requests.Clear();

        harness.Store.Report("v9", 240, 600);
        Assert.NotNull(harness.DebounceGate);           // debounce armed, nothing sent yet
        Assert.Empty(harness.Requests);
        harness.DebounceGate!.SetResult();              // 15 s "elapse"
        if (harness.Sync.RunningTask is { } task) await task;

        var (_, body, url) = harness.Requests.Single(r => r.Url.Contains("/rows/"));
        Assert.EndsWith($"/rows/{SyncId}_v9", new Uri(url).AbsolutePath);
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal(UserId, json.GetProperty("data").GetProperty("userId").GetString());
        Assert.Equal(240, json.GetProperty("data").GetProperty("position").GetDouble());
        Assert.Equal("2026-08-23T12:00:00.000Z",
            json.GetProperty("data").GetProperty("watchedAt").GetString());
        Assert.Contains($"update(\"user:{UserId}\")",
            json.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        Assert.Empty(harness.Store.Dirty);              // MarkSynced ran
    }

    [Fact]
    public async Task Unauthorized_DiscardsSession_NothingEscapes()
    {
        var harness = new Harness();
        await harness.Activated();                      // signs in, persists session
        harness.RejectWith401 = true;

        harness.Store.Report("v1", 100, 600);
        harness.Sync.FlushNow();
        if (harness.Sync.RunningTask is { } task) await task;   // must not throw

        Assert.Null(new AppwriteSessionStore(harness.Dir).Load(SyncId));
        Assert.Contains("v1", harness.Store.Dirty);     // still queued for next time
    }

    [Fact]
    public async Task Activate_Null_DeactivatesAndLocalChangesDoNothing()
    {
        var harness = new Harness();
        await harness.Activated();
        harness.Sync.Activate(null, null, null);
        harness.Requests.Clear();

        harness.Store.Report("v1", 100, 600);
        harness.Sync.FlushNow();
        if (harness.Sync.RunningTask is { } task) await task;
        Assert.Empty(harness.Requests);
    }
}
```

Add this helper to the `Harness` class (used by `FirstPull_QueuesPreexistingLocalHistory`):

```csharp
        public List<string> PushedIds() => Requests
            .Where(r => r.Message.Method == HttpMethod.Put)
            .Select(r => new Uri(r.Url).AbsolutePath.Split('_')[^1]).ToList();
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/WinTube.Core.Tests` — expected: compile FAIL.

- [ ] **Step 3: Implement `WatchProgressSync`**

`src/WinTube.Core/Sync/WatchProgressSync.cs` — the full engine per the Interfaces block; the Swift `WatchProgressSync.swift` is the arbiter for anything this plan leaves ambiguous:

```csharp
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using WinTube.Core.Stores;

namespace WinTube.Core.Sync;

/// Keeps a profile's watch history on the Appwrite backend. Strictly local-first: the store
/// remains the single source of truth; this only pushes what's there and folds in what came
/// back. Every failure is swallowed — an unreachable home server costs nothing but the sync.
/// Port of the tvOS WatchProgressSync.
[SupportedOSPlatform("windows")]
public sealed class WatchProgressSync
{
    private static readonly TimeSpan FlushDelay = TimeSpan.FromSeconds(15);
    private const int PageSize = 100;
    private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private sealed record Target(string SyncId, string AccountKey, string AccessToken);

    private readonly WatchProgressStore store;
    private readonly AppwriteClient client;
    private readonly AppwriteSessionStore sessions;
    private readonly string rootDirectory;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    private Target? target;
    private CancellationTokenSource activation = new();
    private CancellationTokenSource? debounce;
    private Task<string?>? signInTask;

    public Task? RunningTask { get; private set; }

    public WatchProgressSync(
        WatchProgressStore store,
        AppwriteClient client,
        AppwriteSessionStore sessions,
        string rootDirectory,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.store = store;
        this.client = client;
        this.sessions = sessions;
        this.rootDirectory = rootDirectory;
        this.delay = delay ?? Task.Delay;
        store.LocalChanged += ScheduleFlush;
    }

    // MARK: lifecycle

    /// Points the sync at a profile, then pulls and pushes once. Null any argument to
    /// deactivate. Cancelling the in-flight sign-in matters: left running, the incoming
    /// profile would be handed the outgoing one's user id and read that account's rows.
    public void Activate(string? profileId, string? accountKey, string? accessToken)
    {
        activation.Cancel();
        activation = new CancellationTokenSource();
        debounce?.Cancel();
        signInTask = null;

        if (profileId is null || accountKey is null || accessToken is null)
        {
            target = null;
            client.Session = null;
            return;
        }
        var syncId = profileId[..Math.Min(16, profileId.Length)];
        target = new Target(syncId, accountKey, accessToken);
        client.Session = sessions.Load(syncId);
        Sync();
    }

    /// Pulls anything new and pushes anything queued.
    public void Sync()
    {
        if (target is null) return;
        var ct = activation.Token;
        RunningTask = Task.Run(async () =>
        {
            await PullAsync(ct);
            await PushAsync(ct);
        }, CancellationToken.None);
    }

    /// Pushes now rather than waiting out the debounce — on player close and app exit,
    /// the two moments where "later" may never come.
    public void FlushNow()
    {
        debounce?.Cancel();
        if (target is null || store.Dirty.Count == 0) return;
        var ct = activation.Token;
        RunningTask = Task.Run(() => PushAsync(ct), CancellationToken.None);
    }

    private void ScheduleFlush()
    {
        if (target is null) return;
        debounce?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(activation.Token);
        debounce = cts;
        // The delay task is created synchronously with the local change, so a caller (and a
        // test) that just wrote an entry can observe the armed debounce deterministically.
        var wait = delay(FlushDelay, cts.Token);
        RunningTask = Task.Run(async () =>
        {
            try { await wait; }
            catch (OperationCanceledException) { return; }
            if (cts.Token.IsCancellationRequested) return;
            await PushAsync(cts.Token);
        }, CancellationToken.None);
    }

    // MARK: session

    /// Signs in if there is no session yet; concurrent callers share one attempt — two
    /// sign-ins racing is two Appwrite users being created for one account.
    private async Task<string?> AuthenticateAsync(CancellationToken ct)
    {
        if (target is not { } profile) return null;
        if (client.Session is { } session) return session.UserId;
        if (signInTask is { } running) return await running;

        var task = SignInOnceAsync(profile, ct);
        signInTask = task;
        var userId = await task;
        if (ReferenceEquals(signInTask, task)) signInTask = null;
        return target?.SyncId == profile.SyncId ? userId : null;
    }

    private async Task<string?> SignInOnceAsync(Target profile, CancellationToken ct)
    {
        try
        {
            var session = await client.SignInAsync(profile.AccessToken, profile.AccountKey, ct);
            if (ct.IsCancellationRequested || target?.SyncId != profile.SyncId) return null;
            client.Session = session;
            sessions.Save(profile.SyncId, session);
            return session.UserId;
        }
        catch (Exception e)
        {
            Log($"sign-in failed: {e.Message}");
            return null;
        }
    }

    private void InvalidateSession()
    {
        if (target is not { } profile) return;
        client.Session = null;
        sessions.Delete(profile.SyncId);
    }

    // MARK: pull

    private string MarkPath(string syncId) =>
        Path.Combine(rootDirectory, "profiles", syncId, "sync-pulled-at.txt");

    private async Task PullAsync(CancellationToken ct)
    {
        try
        {
            if (target is not { } profile) return;
            var userId = await AuthenticateAsync(ct);
            if (userId is null || ct.IsCancellationRequested || target?.SyncId != profile.SyncId)
                return;

            var markPath = MarkPath(profile.SyncId);
            string? since = File.Exists(markPath) ? File.ReadAllText(markPath) : null;
            string? cursor = null;
            string? latest = null;
            var merged = new Dictionary<string, ProgressEntry>();

            while (!ct.IsCancellationRequested)
            {
                var queries = new List<string>
                {
                    AppwriteQuery.Equal("userId", userId),
                    AppwriteQuery.OrderAsc("watchedAt"),
                    AppwriteQuery.Limit(PageSize),
                };
                if (since is not null) queries.Add(AppwriteQuery.GreaterThan("watchedAt", since));
                if (cursor is not null) queries.Add(AppwriteQuery.CursorAfter(cursor));

                IReadOnlyList<JsonElement> rows;
                try
                {
                    rows = await client.ListRowsAsync(queries, ct);
                }
                catch (AppwriteException e) when (e.IsUnauthorized)
                {
                    Log($"session rejected: {e.Message}");
                    InvalidateSession();
                    return;   // the mark is not advanced — nothing was recorded as caught up
                }

                foreach (var row in rows)
                {
                    if (Entry(row) is not { } parsed) continue;
                    merged[parsed.VideoId] = parsed.Entry;
                    latest = parsed.WatchedAt;   // rows arrive ascending; the last one wins
                }
                if (rows.Count < PageSize) break;
                cursor = InnerTube.Json.StringAt(rows[^1], "$id");
                if (cursor is null) break;
            }

            if (ct.IsCancellationRequested || target?.SyncId != profile.SyncId) return;
            Log($"pulled {merged.Count} row(s); since={since ?? "never"}");
            if (merged.Count > 0) store.Merge(merged);
            // Nothing had ever been pulled → nothing has ever been pushed either: everything
            // this device already knows is owed to the backend.
            if (since is null) store.QueueAll(merged.Keys.ToHashSet());
            if (latest is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(markPath)!);
                File.WriteAllText(markPath, latest);
            }
        }
        catch (Exception e)
        {
            Log($"pull failed: {e.Message}");
        }
    }

    private static (string VideoId, ProgressEntry Entry, string WatchedAt)? Entry(JsonElement row)
    {
        var videoId = InnerTube.Json.StringAt(row, "videoId");
        var watchedAt = InnerTube.Json.StringAt(row, "watchedAt");
        if (videoId is null || watchedAt is null) return null;
        if (!row.TryGetProperty("position", out var p) || !p.TryGetDouble(out var position))
            return null;
        if (!row.TryGetProperty("duration", out var d) || !d.TryGetDouble(out var duration))
            return null;
        if (!DateTimeOffset.TryParse(watchedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var updatedAt)) return null;
        return (videoId, new ProgressEntry(position, duration, updatedAt), watchedAt);
    }

    // MARK: push

    /// Sequential on purpose: the queue is normally a handful of videos, and a home server
    /// on the far side of a domestic uplink is happier with one request at a time.
    private async Task PushAsync(CancellationToken ct)
    {
        try
        {
            if (target is not { } profile) return;
            var userId = await AuthenticateAsync(ct);
            if (userId is null || ct.IsCancellationRequested || target?.SyncId != profile.SyncId)
                return;

            // Snapshotted: playback can queue more videos while this loop awaits the
            // network, and those belong to the next flush.
            var queued = store.Dirty.ToList();
            var pushed = new Dictionary<string, DateTimeOffset>();
            foreach (var videoId in queued)
            {
                if (ct.IsCancellationRequested) break;
                if (!store.Entries.TryGetValue(videoId, out var entry)) continue;
                try
                {
                    await client.UpsertRowAsync(
                        rowId: $"{profile.SyncId}_{videoId}",
                        data: new Dictionary<string, object?>
                        {
                            ["userId"] = userId,
                            ["videoId"] = videoId,
                            ["position"] = entry.PositionSeconds,
                            ["duration"] = entry.DurationSeconds,
                            ["watchedAt"] = entry.UpdatedAt.UtcDateTime
                                .ToString(DateFormat, CultureInfo.InvariantCulture),
                        },
                        permissions:
                        [
                            $"read(\"user:{userId}\")",
                            $"update(\"user:{userId}\")",
                            $"delete(\"user:{userId}\")",
                        ], ct);
                    pushed[videoId] = entry.UpdatedAt;
                }
                catch (AppwriteException e) when (e.IsUnauthorized)
                {
                    Log($"session rejected: {e.Message}");
                    InvalidateSession();
                    break;
                }
                catch (Exception e)
                {
                    Log($"push of {videoId} failed: {e.Message}");
                    break;   // stays queued; next flush retries
                }
            }

            Log($"pushed {pushed.Count} of {queued.Count} queued");
            if (pushed.Count > 0 && target?.SyncId == profile.SyncId)
                store.MarkSynced(pushed);
        }
        catch (Exception e)
        {
            Log($"push failed: {e.Message}");
        }
    }

    private static void Log(string message) =>
        System.Diagnostics.Debug.WriteLine($"[WatchProgressSync] {message}");
}
```

Porting note: `WatchProgressStore` raises `LocalChanged` synchronously on the caller's thread (the UI thread in the app, the test thread in tests); the engine hops to the thread pool for all network work, and the store's own members are only touched from `Merge`/`QueueAll`/`MarkSynced` calls that the store serializes by being effectively single-threaded per the v1 review — acceptable at this app's volume, and the same shape the tvOS actor gives. If the store's `Persist` proves racy under the thread-pool calls, marshal the three store calls back through a captured `SynchronizationContext` — but only if a test or review demonstrates the race.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/WinTube.Core.Tests` — expected: PASS (+7).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: local-first watch-progress sync engine"
```

---

### Task 8: App wiring

**Files:**
- Modify: `src/WinTube.App/Session.cs`, `src/WinTube.App/MainWindow.xaml.cs`, `src/WinTube.App/Views/PlayerPage.xaml.cs`

**Interfaces:**
- Consumes: everything above; `AccountService.LoadAsync` (v1) for the backfill.
- Produces: `Session.ProgressSync` (`WatchProgressSync?` — null when `AppwriteConfig.FromSecrets` returns null); activation wiring; the three triggers.

Changes, all small and mechanical:

1. **Session construction**: after building the existing services, `var appwrite = AppwriteConfig.FromSecrets(secrets); if (appwrite is not null) ProgressSync = new WatchProgressSync(Progress, new AppwriteClient(http, appwrite), new AppwriteSessionStore(DataDirectory), DataDirectory);`.
2. **ActivateSync helper** in Session, called from the constructor (after `ActivateStores`), from `CompleteSignInAsync`, from the refresh-success path in `RunAsync`, and from `SignOut`:
   ```csharp
   private void ActivateSync() =>
       ProgressSync?.Activate(Profile?.ProfileId, Profile?.AccountKey, accessToken ?? Profile?.AccessToken);
   ```
3. **CompleteSignInAsync**: include `AccountKey: account.Key` when building the `StoredProfile` (the `AccountInfo` is already in hand).
4. **AccountKey backfill**: in the constructor, when `Profile is { AccountKey: null }`, fire a background task: `RunAsync(t => Accounts.LoadAsync(t))` → on success `Profile = Profile with { AccountKey = info.Key }; tokenStore.Save(Profile); ActivateSync();` — all failures swallowed (sync just stays off until next launch). Guard with try/catch; never crash startup.
5. **SignOut**: also `ProgressSync?.Activate(null, null, null)` and delete the stored Appwrite session for the profile (`new AppwriteSessionStore(DataDirectory).Delete(profileId[..16])` — or hold the store as a field). The backend rows are deliberately left alone.
6. **MainWindow**: subscribe `Activated` — when `e.WindowActivationState != WindowActivationState.Deactivated` and more than 60 s have passed since the last sync trigger (`DateTimeOffset` field), call `App.Session.ProgressSync?.Sync()`. Subscribe `Closed` → `App.Session.ProgressSync?.FlushNow()`.
7. **PlayerPage.OnNavigatedFrom**: after the existing final `Report`, call `App.Session.ProgressSync?.FlushNow()`.

- [ ] **Step 1: Implement the wiring** per the list above.

- [ ] **Step 2: Build and test**

Run: `dotnet build src/WinTube.App -p:Platform=x64` — expected: clean. `dotnet test tests/WinTube.Core.Tests` — expected: all green (no new tests; the wiring is exercised live in Task 9).

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "feat: wire watch-progress sync into session, window and player"
```

---

### Task 9: Wrap-up — README and live verification

**Files:**
- Modify: `README.md`, `docs/superpowers/plans/2026-08-23-wintube-sync.md` (tick outcomes)

- [ ] **Step 1: README** — add a "Watch-progress sync" section: optional feature; add `appwriteHost` (host only, no scheme) and `appwriteProjectId` to `%LOCALAPPDATA%\WinTube\secrets.json`; empty = off; the backend is the metube repo's `Backend/` project; sign-out keeps the backend copy by design. Keep the metube README voice.

- [ ] **Step 2: LIVE VERIFICATION (the user)** — with real values in secrets.json:
  1. Launch, play something for >30 s, close the player, wait ~15 s → row visible in the Appwrite console (or resume position appears on the Apple TV).
  2. Watch something on the Apple TV → reopen WinTube (or alt-tab to it after 60 s) → the video shows the TV's position.
  3. Sign out and back in on Windows → history restored from the backend.

- [ ] **Step 3: Final test run**

Run: `dotnet test tests/WinTube.Core.Tests` and `dotnet build src/WinTube.App -p:Platform=x64` — all green.

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "docs: watch-progress sync README and verification"
```

---

## Deviations from tvOS (by design)

| tvOS | WinTube stage 2 |
|---|---|
| Multiple profiles, profile switching | One active profile; the generation/cancellation guards are kept so profiles can arrive later |
| Session in Keychain | DPAPI file beside the tokens |
| UserDefaults pulledAt key | `sync-pulled-at.txt` beside the profile's progress file |
| Foreground trigger (scene phase) | `Window.Activated` with 60 s cooldown |
| tvOS platform Origin | Same Origin reused (`appwrite-tvos://…`) — zero server changes |

