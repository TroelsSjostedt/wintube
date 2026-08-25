# WinTube SponsorBlock Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Automatic skipping of community-flagged in-video interruptions (sponsor reads, subscribe plugs, non-music sections) via SponsorBlock's public API, with the tvOS app's privacy model and skip semantics.

**Architecture:** `WinTube.Core.SponsorBlock` holds the models, the fetch (hash-prefix privacy mode), and the pure merge/selection logic — all unit-tested. `PlayerPage` polls the position every 250 ms against the fetched segments, seeks past each at most once per playback, and shows a 3 s toast. Ported from the tvOS `SponsorBlockService.swift` / `SponsorBlockSkipper.swift` (in the metube checkout; re-clone `https://github.com/claust/metube` to a temp dir if gone).

**Tech Stack:** Existing only — .NET 8, System.Text.Json, SHA256, xUnit + `StubHttpHandler`. No new packages.

**Spec:** `docs/specs/2026-08-25-wintube-sponsorblock-design.html` (approved 2026-08-25).

## Global Constraints

- This is NOT ad blocking: pre/mid-rolls never reach the app; only in-video segments are skipped. Never write copy suggesting otherwise.
- Privacy: the videoId is never sent — only the first 4 hex chars of its lowercase SHA-256; the match happens on device against `videoID` or the full `hash`.
- Failure policy: anything but HTTP 200 (404 included — the normal "no segments" answer), timeouts, garbage JSON, and every exception → empty segment list. SponsorBlock trouble must never delay or break playback; the fetch fires only after playback has started.
- Skip semantics: each segment skipped at most once per playback; seek target `min(End, duration)`; segments with `votes < 0` or shorter than 1 s dropped; overlapping-or-touching (gap ≤ 0.5 s) merged keeping the EARLIER segment's id and category.
- Default categories: sponsor, selfpromo, interaction, music_offtopic. No settings UI.
- Wire format: GET `https://sponsor.ajay.app/api/skipSegments/{prefix4}` with `categories` (JSON array of api names, sorted ordinal) and `actionTypes` (`["skip"]`) as query params; User-Agent `WinTube/0.1`; 8 s timeout.
- Tests: `dotnet test tests/WinTube.Core.Tests` (111 green before this plan). App build: `dotnet build src/WinTube.App -p:Platform=x64`. TreatWarningsAsErrors everywhere.
- Branch `master`; commit per task with the given message.

## File Structure

```
src/WinTube.Core/SponsorBlock/SponsorCategory.cs   enum + api/display names + DefaultSkipped
src/WinTube.Core/SponsorBlock/SponsorSegment.cs    record + Contains/Duration + Merge + NextToSkip
src/WinTube.Core/SponsorBlock/SponsorBlockService.cs   the fetch
src/WinTube.App/Session.cs                         MODIFY: expose SponsorBlockService
src/WinTube.App/Views/PlayerPage.xaml              MODIFY: toast element
src/WinTube.App/Views/PlayerPage.xaml.cs           MODIFY: fetch-on-play, 250 ms poll, seek, toast
tests/WinTube.Core.Tests/SponsorSegmentTests.cs
tests/WinTube.Core.Tests/SponsorBlockServiceTests.cs
```

---

### Task 1: Categories and segments (models, merge, selection)

**Files:**
- Create: `src/WinTube.Core/SponsorBlock/SponsorCategory.cs`, `src/WinTube.Core/SponsorBlock/SponsorSegment.cs`
- Test: `tests/WinTube.Core.Tests/SponsorSegmentTests.cs`

**Interfaces:**
- Produces (namespace `WinTube.Core.SponsorBlock`):
  - `enum SponsorCategory { Sponsor, SelfPromo, Interaction, Intro, Outro, Preview, Filler, MusicOffTopic }`
  - `static class SponsorCategories` with `IReadOnlySet<SponsorCategory> DefaultSkipped`, extension `string ApiName(this SponsorCategory)`, extension `string DisplayName(this SponsorCategory)`, and `SponsorCategory? FromApiName(string name)` (null for unknown — the API grows categories, and an unknown one must be ignored, not crash parsing).
  - `sealed record SponsorSegment(string Id, SponsorCategory Category, double Start, double End)` with `double Duration => End - Start`, `bool Contains(double time)` (`time >= Start && time < End`), `static IReadOnlyList<SponsorSegment> Merge(IEnumerable<SponsorSegment> segments)`, `static SponsorSegment? NextToSkip(IReadOnlyList<SponsorSegment> segments, double time, IReadOnlySet<string> skippedIds)`.

- [x] **Step 1: Write the failing tests**

`tests/WinTube.Core.Tests/SponsorSegmentTests.cs`:

```csharp
using WinTube.Core.SponsorBlock;

namespace WinTube.Core.Tests;

public class SponsorSegmentTests
{
    private static SponsorSegment Seg(string id, double start, double end,
        SponsorCategory category = SponsorCategory.Sponsor) => new(id, category, start, end);

    [Fact]
    public void ApiNames_RoundTrip()
    {
        Assert.Equal("selfpromo", SponsorCategory.SelfPromo.ApiName());
        Assert.Equal("music_offtopic", SponsorCategory.MusicOffTopic.ApiName());
        Assert.Equal(SponsorCategory.Interaction, SponsorCategories.FromApiName("interaction"));
        Assert.Null(SponsorCategories.FromApiName("exclusive_access"));   // unknown → ignored
    }

    [Fact]
    public void DisplayNames_MatchTheTvOsToastCopy()
    {
        Assert.Equal("Sponsor", SponsorCategory.Sponsor.DisplayName());
        Assert.Equal("Subscribe reminder", SponsorCategory.Interaction.DisplayName());
        Assert.Equal("Non-music section", SponsorCategory.MusicOffTopic.DisplayName());
    }

    [Fact]
    public void DefaultSkipped_IsTheFourNonEditorialCategories()
    {
        Assert.Equal(
            new[] { SponsorCategory.Sponsor, SponsorCategory.SelfPromo,
                    SponsorCategory.Interaction, SponsorCategory.MusicOffTopic }.ToHashSet(),
            SponsorCategories.DefaultSkipped);
    }

    [Fact]
    public void Contains_IsHalfOpen()
    {
        var segment = Seg("a", 10, 20);
        Assert.True(segment.Contains(10));
        Assert.True(segment.Contains(19.99));
        Assert.False(segment.Contains(20));
        Assert.False(segment.Contains(9.99));
    }

    [Fact]
    public void Merge_FoldsOverlappingAndTouching_KeepingEarlierIdentity()
    {
        var merged = SponsorSegment.Merge(
        [
            Seg("b", 15, 25, SponsorCategory.SelfPromo),
            Seg("a", 10, 16),
            Seg("c", 25.4, 30, SponsorCategory.Interaction),   // touching (gap 0.4 ≤ 0.5)
            Seg("d", 50, 60),
        ]);
        Assert.Equal(2, merged.Count);
        Assert.Equal(("a", SponsorCategory.Sponsor, 10.0, 30.0),
            (merged[0].Id, merged[0].Category, merged[0].Start, merged[0].End));
        Assert.Equal("d", merged[1].Id);
    }

    [Fact]
    public void Merge_DropsFullyContainedSegments()
    {
        var merged = SponsorSegment.Merge([Seg("a", 10, 30), Seg("b", 15, 20)]);
        Assert.Equal(("a", 30.0), (Assert.Single(merged).Id, merged[0].End));
    }

    [Fact]
    public void NextToSkip_SkipsTheSkippedSet()
    {
        var segments = new[] { Seg("a", 10, 20), Seg("b", 15, 25) };
        Assert.Equal("a", SponsorSegment.NextToSkip(segments, 12, new HashSet<string>())!.Id);
        Assert.Equal("b", SponsorSegment.NextToSkip(segments, 16, new HashSet<string> { "a" })!.Id);
        Assert.Null(SponsorSegment.NextToSkip(segments, 30, new HashSet<string>()));
        Assert.Null(SponsorSegment.NextToSkip(segments, 12, new HashSet<string> { "a", "b" }));
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/WinTube.Core.Tests` — expected: compile FAIL.

- [x] **Step 3: Implement**

`src/WinTube.Core/SponsorBlock/SponsorCategory.cs`:

```csharp
namespace WinTube.Core.SponsorBlock;

/// The kinds of interruption SponsorBlock distinguishes.
public enum SponsorCategory
{
    Sponsor,
    SelfPromo,
    Interaction,
    Intro,
    Outro,
    Preview,
    Filler,
    MusicOffTopic,
}

public static class SponsorCategories
{
    /// What gets skipped: the four that are unambiguously NOT the video the user chose to
    /// watch. Intro/outro/preview/filler are editorial parts of the video itself — plenty of
    /// people want them — so they are parsed but never requested. No settings UI, as on tvOS.
    public static readonly IReadOnlySet<SponsorCategory> DefaultSkipped = new HashSet<SponsorCategory>
    {
        SponsorCategory.Sponsor,
        SponsorCategory.SelfPromo,
        SponsorCategory.Interaction,
        SponsorCategory.MusicOffTopic,
    };

    /// The API's own identifier, sent verbatim in the categories query parameter.
    public static string ApiName(this SponsorCategory category) => category switch
    {
        SponsorCategory.Sponsor => "sponsor",
        SponsorCategory.SelfPromo => "selfpromo",
        SponsorCategory.Interaction => "interaction",
        SponsorCategory.Intro => "intro",
        SponsorCategory.Outro => "outro",
        SponsorCategory.Preview => "preview",
        SponsorCategory.Filler => "filler",
        SponsorCategory.MusicOffTopic => "music_offtopic",
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };

    /// Shown in the "skipped" toast.
    public static string DisplayName(this SponsorCategory category) => category switch
    {
        SponsorCategory.Sponsor => "Sponsor",
        SponsorCategory.SelfPromo => "Self-promotion",
        SponsorCategory.Interaction => "Subscribe reminder",
        SponsorCategory.Intro => "Intro",
        SponsorCategory.Outro => "Outro",
        SponsorCategory.Preview => "Recap",
        SponsorCategory.Filler => "Filler",
        SponsorCategory.MusicOffTopic => "Non-music section",
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };

    /// Null for a category this build doesn't know — the API grows categories, and an
    /// unknown one must be ignored, not crash the parse.
    public static SponsorCategory? FromApiName(string name) => name switch
    {
        "sponsor" => SponsorCategory.Sponsor,
        "selfpromo" => SponsorCategory.SelfPromo,
        "interaction" => SponsorCategory.Interaction,
        "intro" => SponsorCategory.Intro,
        "outro" => SponsorCategory.Outro,
        "preview" => SponsorCategory.Preview,
        "filler" => SponsorCategory.Filler,
        "music_offtopic" => SponsorCategory.MusicOffTopic,
        _ => null,
    };
}
```

`src/WinTube.Core/SponsorBlock/SponsorSegment.cs`:

```csharp
namespace WinTube.Core.SponsorBlock;

/// One community-submitted stretch of a video that isn't the video: a read-out sponsor spot,
/// a "like and subscribe" plug, an intro animation. Times in seconds.
public sealed record SponsorSegment(string Id, SponsorCategory Category, double Start, double End)
{
    public double Duration => End - Start;

    public bool Contains(double time) => time >= Start && time < End;

    /// Sorts by start time and folds overlapping or touching segments (gap ≤ 0.5 s) into one,
    /// so two adjacent sponsor reads become a single seek instead of a seek that lands inside
    /// the next segment and immediately seeks again. Keeps the EARLIER segment's identity —
    /// it's the one whose start the user reaches, so it's the category the toast names.
    public static IReadOnlyList<SponsorSegment> Merge(IEnumerable<SponsorSegment> segments)
    {
        var merged = new List<SponsorSegment>();
        foreach (var segment in segments.OrderBy(s => s.Start))
        {
            if (merged.Count == 0 || segment.Start > merged[^1].End + 0.5)
            {
                merged.Add(segment);
                continue;
            }
            if (segment.End <= merged[^1].End) continue;   // fully contained
            merged[^1] = merged[^1] with { End = segment.End };
        }
        return merged;
    }

    /// The segment playback should seek past right now, or null. `skippedIds` holds segments
    /// already skipped this playback — rewinding into one plays it normally instead of
    /// bouncing the user forward again.
    public static SponsorSegment? NextToSkip(
        IReadOnlyList<SponsorSegment> segments, double time, IReadOnlySet<string> skippedIds) =>
        segments.FirstOrDefault(s => s.Contains(time) && !skippedIds.Contains(s.Id));
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/WinTube.Core.Tests` — expected: PASS (+7).

- [x] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: SponsorBlock categories, segments, merge and selection"
```

---

### Task 2: SponsorBlockService

**Files:**
- Create: `src/WinTube.Core/SponsorBlock/SponsorBlockService.cs`
- Test: `tests/WinTube.Core.Tests/SponsorBlockServiceTests.cs`

**Interfaces:**
- Consumes: Task 1's types, `StubHttpHandler`.
- Produces: `sealed class SponsorBlockService(HttpClient http)` with
  `Task<IReadOnlyList<SponsorSegment>> FetchSegmentsAsync(string videoId, IReadOnlySet<SponsorCategory>? categories = null, CancellationToken ct = default)` — categories default `SponsorCategories.DefaultSkipped`; empty set → `[]` without a request; the whole body is wrapped so every failure returns `[]`.

- [x] **Step 1: Write the failing tests**

`tests/WinTube.Core.Tests/SponsorBlockServiceTests.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using WinTube.Core.SponsorBlock;

namespace WinTube.Core.Tests;

public class SponsorBlockServiceTests
{
    private const string VideoId = "dQw4w9WgXcQ";
    private static readonly string FullHash = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(VideoId))).ToLowerInvariant();

    private static SponsorBlockService Make(
        Func<HttpRequestMessage, string, HttpResponseMessage> respond, out StubHttpHandler handler)
    {
        handler = new StubHttpHandler(respond);
        return new SponsorBlockService(new HttpClient(handler));
    }

    [Fact]
    public async Task Fetch_SendsPrefixSortedCategoriesAndSkipActionType()
    {
        var service = Make((request, _) =>
        {
            var url = request.RequestUri!.ToString();
            Assert.Contains($"/api/skipSegments/{FullHash[..4]}?", url);
            var query = Uri.UnescapeDataString(url);
            Assert.Contains("""["interaction","music_offtopic","selfpromo","sponsor"]""", query);
            Assert.Contains("""["skip"]""", query);
            Assert.Equal("WinTube/0.1", request.Headers.GetValues("User-Agent").Single());
            return StubHttpHandler.JsonResponse("[]");
        }, out var handler);

        Assert.Empty(await service.FetchSegmentsAsync(VideoId));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Fetch_MatchesOwnVideoByIdOrHash_AndParsesSegments()
    {
        var service = Make((_, _) => StubHttpHandler.JsonResponse($$"""
            [
              {"videoID":"otherVideo","segments":[
                {"UUID":"x","category":"sponsor","segment":[0,30],"votes":5}]},
              {"videoID":"{{VideoId}}","hash":"{{FullHash}}","segments":[
                {"UUID":"keep","category":"sponsor","segment":[10,25.5],"votes":3},
                {"UUID":"downvoted","category":"sponsor","segment":[40,60],"votes":-2},
                {"UUID":"tiny","category":"sponsor","segment":[70,70.5],"votes":1},
                {"UUID":"unknowncat","category":"exclusive_access","segment":[80,95],"votes":1},
                {"UUID":"touching","category":"selfpromo","segment":[25.8,33],"votes":0}]}
            ]
            """), out _);

        var segments = await service.FetchSegmentsAsync(VideoId);
        // downvoted, tiny and unknown-category dropped; keep+touching merged, earlier id wins.
        var segment = Assert.Single(segments);
        Assert.Equal(("keep", SponsorCategory.Sponsor, 10.0, 33.0),
            (segment.Id, segment.Category, segment.Start, segment.End));
    }

    [Theory]
    [InlineData(404, "Not Found")]
    [InlineData(500, "boom")]
    [InlineData(200, "not json at all")]
    public async Task Fetch_AnyTroubleYieldsEmpty(int status, string body)
    {
        var service = Make((_, _) => new HttpResponseMessage((System.Net.HttpStatusCode)status)
        {
            Content = new StringContent(body),
        }, out _);
        Assert.Empty(await service.FetchSegmentsAsync(VideoId));
    }

    [Fact]
    public async Task Fetch_TransportExceptionYieldsEmpty()
    {
        var service = Make((_, _) => throw new HttpRequestException("dns down"), out _);
        Assert.Empty(await service.FetchSegmentsAsync(VideoId));
    }

    [Fact]
    public async Task Fetch_EmptyCategorySet_MakesNoRequest()
    {
        var service = Make((_, _) => StubHttpHandler.JsonResponse("[]"), out var handler);
        Assert.Empty(await service.FetchSegmentsAsync(VideoId, new HashSet<SponsorCategory>()));
        Assert.Empty(handler.Requests);
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/WinTube.Core.Tests` — expected: compile FAIL.

- [x] **Step 3: Implement**

`src/WinTube.Core/SponsorBlock/SponsorBlockService.cs`:

```csharp
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinTube.Core.SponsorBlock;

/// Fetches SponsorBlock segments for a video. SponsorBlock (https://sponsor.ajay.app, the
/// crowd-sourced database behind the browser extension) is public, unauthenticated and free.
///
/// Privacy: the videoId is never sent. The endpoint takes the first four hex characters of
/// its SHA-256 and returns every video whose hash starts with them; the match happens on
/// device. The server learns someone is watching one of ~1/65,536 of YouTube, not which video.
public sealed class SponsorBlockService(HttpClient http)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    /// Segments for the video in the given categories (default: the skippable four), sorted
    /// and merged. Empty on "nobody submitted anything" AND on every failure — this is an
    /// optional enhancement, and SponsorBlock trouble must never keep a video from playing.
    public async Task<IReadOnlyList<SponsorSegment>> FetchSegmentsAsync(
        string videoId,
        IReadOnlySet<SponsorCategory>? categories = null,
        CancellationToken ct = default)
    {
        categories ??= SponsorCategories.DefaultSkipped;
        if (categories.Count == 0) return [];
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(videoId)))
                .ToLowerInvariant();
            var categoriesJson = JsonSerializer.Serialize(
                categories.Select(c => c.ApiName()).OrderBy(n => n, StringComparer.Ordinal));
            var url = $"https://sponsor.ajay.app/api/skipSegments/{hash[..4]}"
                + $"?categories={Uri.EscapeDataString(categoriesJson)}"
                // "skip" only: SponsorBlock also has mute/poi/chapter entries, none of which
                // this app acts on — asking would only mean filtering them out again.
                + $"&actionTypes={Uri.EscapeDataString("[\"skip\"]")}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // SponsorBlock asks clients to identify themselves.
            request.Headers.TryAddWithoutValidation("User-Agent", "WinTube/0.1");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);

            using var response = await http.SendAsync(request, cts.Token);
            // 404 is the ordinary "no segments in this hash prefix" answer, not a failure.
            if (response.StatusCode != HttpStatusCode.OK) return [];
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

            // The response covers every video sharing the prefix; only ours is interesting.
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var matches = InnerTube.Json.StringAt(entry, "videoID") == videoId
                    || InnerTube.Json.StringAt(entry, "hash")?.ToLowerInvariant() == hash;
                if (!matches) continue;
                if (!entry.TryGetProperty("segments", out var raw) ||
                    raw.ValueKind != JsonValueKind.Array) return [];
                return SponsorSegment.Merge(
                    raw.EnumerateArray().Select(Parse).OfType<SponsorSegment>());
            }
            return [];
        }
        catch
        {
            return [];
        }
    }

    private static SponsorSegment? Parse(JsonElement segment)
    {
        var uuid = InnerTube.Json.StringAt(segment, "UUID");
        var categoryName = InnerTube.Json.StringAt(segment, "category");
        if (uuid is null || categoryName is null) return null;
        if (SponsorCategories.FromApiName(categoryName) is not { } category) return null;
        if (!segment.TryGetProperty("segment", out var bounds) ||
            bounds.ValueKind != JsonValueKind.Array || bounds.GetArrayLength() != 2 ||
            !bounds[0].TryGetDouble(out var start) || !bounds[1].TryGetDouble(out var end))
            return null;
        // Negative votes mean the community has disowned the submission — acting on a wrong
        // or malicious timestamp cuts real content.
        if (segment.TryGetProperty("votes", out var v) &&
            InnerTube.Json.IntValue(v) is < 0) return null;
        start = Math.Max(0, start);
        // Under a second is not worth a seek: the seek costs about as much as the segment.
        if (end - start < 1) return null;
        return new SponsorSegment(uuid, category, start, end);
    }
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/WinTube.Core.Tests` — expected: PASS (+7, incl. the 3-case Theory).

- [x] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: SponsorBlock segment fetch with hash-prefix privacy"
```

---

### Task 3: Player integration (poll, seek, toast)

**Files:**
- Modify: `src/WinTube.App/Session.cs` (expose the service), `src/WinTube.App/Views/PlayerPage.xaml` (toast element), `src/WinTube.App/Views/PlayerPage.xaml.cs`

**Interfaces:**
- Consumes: `SponsorBlockService.FetchSegmentsAsync`, `SponsorSegment.NextToSkip`, `SponsorCategories.DisplayName`; PlayerPage's existing fields (`video`, `mediaPlayer`, first-`Playing` hook where `History.Record` fires, `leftPage` guard, `OnNavigatedFrom` teardown).
- Produces: working skip behavior; no new public surface.

Changes:

1. **Session**: add `public SponsorBlockService SponsorBlock { get; }` initialized `= new SponsorBlockService(http);` in the constructor (namespace import `WinTube.Core.SponsorBlock`).

2. **PlayerPage.xaml**: inside the root Grid, after the MediaPlayerElement, a toast that overlays bottom-center:

```xml
<Border x:Name="SkipToast" Visibility="Collapsed" Grid.Row="1"
        VerticalAlignment="Bottom" HorizontalAlignment="Center" Margin="0,0,0,96"
        Background="{ThemeResource SystemControlBackgroundAltHighBrush}"
        CornerRadius="6" Padding="16,8" IsHitTestVisible="False">
    <TextBlock x:Name="SkipToastText" Style="{ThemeResource BodyTextBlockStyle}"/>
</Border>
```

(Adjust `Grid.Row` to whatever row hosts the MediaPlayerElement so the toast floats over the video; `IsHitTestVisible=False` keeps transport controls clickable through its margin area.)

3. **PlayerPage.xaml.cs** — new fields:

```csharp
    private IReadOnlyList<SponsorSegment> sponsorSegments = [];
    private readonly HashSet<string> sponsorSkipped = [];
    private DispatcherQueueTimer? sponsorTimer;
    private DispatcherQueueTimer? toastTimer;
```

   - **Fetch after playback starts**: in the existing first-`Playing` handler (where `History.Record(video)` runs), add `_ = LoadSponsorSegmentsAsync();`:

```csharp
    /// Fired after playback has started, so a slow SponsorBlock server can never delay the
    /// first frame. Failures yield an empty list inside the service; nothing to catch here
    /// beyond the page-left guard.
    private async Task LoadSponsorSegmentsAsync()
    {
        var segments = await App.Session.SponsorBlock.FetchSegmentsAsync(video!.Id);
        if (leftPage || segments.Count == 0) return;
        sponsorSegments = segments;
        StartSponsorTimer();
    }

    /// Polls rather than using position events: a resume landing mid-sponsor or the user
    /// scrubbing into one would sail straight past a start-boundary event. A quarter-second
    /// tick is one comparison against a handful of ranges.
    private void StartSponsorTimer()
    {
        sponsorTimer ??= DispatcherQueue.CreateTimer();
        sponsorTimer.Interval = TimeSpan.FromMilliseconds(250);
        sponsorTimer.Tick += OnSponsorTick;
        sponsorTimer.Start();
    }

    private void OnSponsorTick(DispatcherQueueTimer sender, object args)
    {
        if (leftPage || mediaPlayer?.PlaybackSession is not { } session) return;
        if (session.PlaybackState != MediaPlaybackState.Playing) return;
        var time = session.Position.TotalSeconds;
        if (SponsorSegment.NextToSkip(sponsorSegments, time, sponsorSkipped) is not { } segment)
            return;

        sponsorSkipped.Add(segment.Id);
        // A segment running to the end has nothing to seek to — clamping to the duration
        // parks playback at the last frame, which is what "the video is over" looks like.
        var duration = session.NaturalDuration.TotalSeconds;
        var target = duration > 0 ? Math.Min(segment.End, duration) : segment.End;
        session.Position = TimeSpan.FromSeconds(target);
        ShowSkipToast($"Skipped {segment.Category.DisplayName()} · {segment.Duration:F0}s");
    }

    /// A newer skip replaces the toast and owns its timer, so back-to-back skips don't have
    /// the first skip's timer hide the second skip's message.
    private void ShowSkipToast(string message)
    {
        SkipToastText.Text = message;
        SkipToast.Visibility = Visibility.Visible;
        toastTimer?.Stop();
        toastTimer ??= DispatcherQueue.CreateTimer();
        toastTimer.Interval = TimeSpan.FromSeconds(3);
        toastTimer.IsRepeating = false;
        toastTimer.Tick += (_, _) => SkipToast.Visibility = Visibility.Collapsed;
        toastTimer.Start();
    }
```

   Implementation note on the two timers: `Tick += OnSponsorTick` in `StartSponsorTimer` must not stack handlers across ladder retries — guard with a bool or subscribe once at field initialization; likewise the toast timer's lambda must be subscribed once (subscribe in a `EnsureToastTimer()` helper or move the `Tick +=` next to `CreateTimer()`). The reviewer will check for double-subscription.

   - **Teardown** in `OnNavigatedFrom`, next to the existing timer/player cleanup: stop both timers and clear `sponsorSegments`/`sponsorSkipped`. The ladder retry (same video) deliberately KEEPS both — segments belong to the video, and a segment already skipped stays skipped across a retry.

- [x] **Step 1: Implement** the three changes above.

- [x] **Step 2: Build and test**

Run: `dotnet build src/WinTube.App -p:Platform=x64` (clean) and `dotnet test tests/WinTube.Core.Tests` (all green).

- [x] **Step 3: Commit**

```bash
git add -A && git commit -m "feat: skip SponsorBlock segments in the player with a toast"
```

---

### Task 4: Wrap-up — README, plan ticks, manual verification

**Files:**
- Modify: `README.md`, `docs/superpowers/plans/2026-08-25-wintube-sponsorblock.md`

- [x] **Step 1: README** — add a "Skipping sponsors (SponsorBlock)" section in the metube voice: what it is (crowd-sourced, public API), that it is NOT YouTube-ad blocking (those never reach the app), the privacy model (4-char hash prefix, match on device), default categories, skip-once-per-playback, silent on outage. Move the scope table's SponsorBlock row from "Later" to "In (stage 3)".

- [x] **Step 2: Tick the executed checkboxes** in this plan (edit with UTF-8-safe tooling — the file carries em-dashes; PowerShell 5.1 Get-Content/Set-Content mangles them).

- [x] **Step 3: MANUAL VERIFICATION (the user)**
  *Verified 2026-08-25 by the user: sponsor segments skip with the toast on real videos.*
  1. Play a video with a known sponsor read (most large tech/podcast channels) → it skips with a "Skipped Sponsor · Ns" toast.
  2. Rewind into the skipped stretch → it plays normally.
  3. A video with no segments plays exactly as before.

- [x] **Step 4: Final gates and commit**

Run: `dotnet test tests/WinTube.Core.Tests` and `dotnet build src/WinTube.App -p:Platform=x64`.

```bash
git add -A && git commit -m "docs: SponsorBlock README and wrap-up"
```
