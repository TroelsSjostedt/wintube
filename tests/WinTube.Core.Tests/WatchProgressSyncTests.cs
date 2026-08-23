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

        public List<string> PushedIds() => Requests
            .Where(r => r.Message.Method == HttpMethod.Put)
            .Select(r => new Uri(r.Url).AbsolutePath.Split('_')[^1]).ToList();
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
