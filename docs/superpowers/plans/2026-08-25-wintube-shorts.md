# WinTube Shorts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Shorts get a row of their own with portrait tiles (and appear nowhere else), replacing v1's drop-them-all scope cut with the tvOS behavior.

**Architecture:** `FeedService` keeps Shorts rows and moves stray Shorts into them (tvOS `parseSections`/`merging`); `FeedSection.Admitting` gives row paging its per-row rule; a new `ShortCard` renders the portrait tile and `HomePage` picks card type per row. Playback is the ordinary player.

**Tech Stack:** Existing only. No new packages.

**Spec:** `docs/specs/2026-08-25-wintube-shorts-design.html` (approved 2026-08-25). Swift arbiters: the metube checkout's `Sources/Feed/FeedService.swift` (parseSections/merging) and `Sources/Core/Models.swift` (`FeedSection.admitting`).

## Global Constraints

- Shorts row classification is UNCHANGED (reel shelf / SHORTS header glyph / all-Short cells); what changes is keep-vs-drop. An untitled Shorts row is titled `"Shorts"`. Every item in a Shorts row is stamped `IsShort = true` (the row vouches for unlabeled cells).
- Stray Shorts (mixed into ordinary shelves) are collected in document order, deduped by id, appended to the response's Shorts row — or become one synthesized `"Shorts"` row at the END when the response has none. They are never silently lost.
- `FeedSection.Admitting(items)`: Shorts row → all items stamped `IsShort = true`; ordinary row → Shorts filtered out. `LoadMoreItemsAsync` returns RAW items (no filtering) from now on; callers admit per row.
- History folding: a Shorts row is never a fold target and never folded; the history retitle lands on the first NON-Shorts row.
- Search keeps filtering Shorts (they appear only in the Home row).
- Tile: portrait 9:16, 150 × 267 px, 8 px corners; NO title/stats/duration; channel avatar 24 px circle bottom-right when present; hover = light 2 px edge; click → `PlayerRequest` into the ordinary player.
- Tests: `dotnet test tests/WinTube.Core.Tests` (154 green before this plan). App build: `dotnet build src/WinTube.App -p:Platform=x64` (known VS-lock rule: MSB3027-only failure → `-c Release` must be clean; never touch the VS process).
- Branch `master`; commit per task with the given message.

## File Structure

```
src/WinTube.Core/Models/FeedModels.cs        MODIFY: FeedSection.Admitting
src/WinTube.Core/Feed/FeedService.cs         MODIFY: keep/merge Shorts, raw row pages, fold guard
src/WinTube.App/Controls/ShortCard.xaml(.cs) portrait tile
src/WinTube.App/Views/HomePage.xaml(.cs)     MODIFY: per-row card type; Admitting on row paging
tests/WinTube.Core.Tests/FeedServiceTests.cs MODIFY: rewrite 2 drop-pinning tests; new keep/merge tests
tests/WinTube.Core.Tests/FeedSectionTests.cs Admitting
```

---

### Task 1: Core — keep Shorts rows, Admitting, fold guard

**Files:**
- Modify: `src/WinTube.Core/Models/FeedModels.cs`, `src/WinTube.Core/Feed/FeedService.cs`
- Test: `tests/WinTube.Core.Tests/FeedSectionTests.cs` (new), `tests/WinTube.Core.Tests/FeedServiceTests.cs` (modify)

**Interfaces:**
- Produces: `FeedSection` gains
  `public IReadOnlyList<VideoItem> Admitting(IReadOnlyList<VideoItem> items)`;
  `FeedService.LoadMoreItemsAsync` now returns raw (unfiltered) items; `FeedPage.Sections` may
  contain `IsShorts: true` sections whose items all have `IsShort = true`.

- [ ] **Step 1: Write/modify the failing tests**

New `tests/WinTube.Core.Tests/FeedSectionTests.cs`:

```csharp
using WinTube.Core.Models;

namespace WinTube.Core.Tests;

public class FeedSectionTests
{
    private static VideoItem Video(string id, bool isShort = false) =>
        new() { Id = id, Title = "t", IsShort = isShort };

    [Fact]
    public void Admitting_ShortsRow_VouchesForEverything()
    {
        var row = new FeedSection("id", "Shorts", [], null, IsShorts: true);
        var admitted = row.Admitting([Video("a"), Video("b", isShort: true)]);
        Assert.Equal(2, admitted.Count);
        Assert.All(admitted, item => Assert.True(item.IsShort));
    }

    [Fact]
    public void Admitting_OrdinaryRow_DropsShorts()
    {
        var row = new FeedSection("id", "Recommended", [], null, IsShorts: false);
        var admitted = row.Admitting([Video("a"), Video("b", isShort: true)]);
        Assert.Equal("a", Assert.Single(admitted).Id);
    }
}
```

In `tests/WinTube.Core.Tests/FeedServiceTests.cs`:

Rewrite `Home_ShortsShelvesAndStrayShortsAreDropped` as:

```csharp
    [Fact]
    public async Task Home_KeepsShortsRow_AndMovesStraysIntoIt()
    {
        var (feed, _) = Make($$"""
            {"contents":{"sectionListRenderer":{"contents":[
              {"reelShelfRenderer":{"items":[{{ShortTile("s1")}}]}},
              {"shelfRenderer":{
                 "headerRenderer":{"shelfHeaderRenderer":{"title":{"simpleText":"Mixed"}}},
                 "content":{"horizontalListRenderer":{
                   "items":[{{Tile("v1")}},{{ShortTile("s2")}}]}}}}]}}}
            """);
        var page = await feed.LoadHomeAsync("T");
        Assert.Equal(2, page.Sections.Count);

        var shorts = page.Sections[0];
        Assert.True(shorts.IsShorts);
        Assert.Equal("Shorts", shorts.Title);                       // untitled reel shelf named
        Assert.Equal(["s1", "s2"], shorts.Items.Select(i => i.Id)); // stray s2 moved in
        Assert.All(shorts.Items, i => Assert.True(i.IsShort));

        var mixed = page.Sections[1];
        Assert.False(mixed.IsShorts);
        Assert.Equal(["v1"], mixed.Items.Select(i => i.Id));        // Short lifted out, not lost
    }
```

Add:

```csharp
    [Fact]
    public async Task Home_StraysWithNoShortsShelf_BecomeASynthesizedRowAtTheEnd()
    {
        var (feed, _) = Make($$"""
            {"contents":{"sectionListRenderer":{"contents":[
              {"shelfRenderer":{
                 "headerRenderer":{"shelfHeaderRenderer":{"title":{"simpleText":"Mixed"}}},
                 "content":{"horizontalListRenderer":{
                   "items":[{{Tile("v1")}},{{ShortTile("s1")}}]}}}}]}}}
            """);
        var page = await feed.LoadHomeAsync("T");
        Assert.Equal(2, page.Sections.Count);
        Assert.False(page.Sections[0].IsShorts);
        var shorts = page.Sections[1];
        Assert.True(shorts.IsShorts);
        Assert.Equal("Shorts", shorts.Title);
        Assert.Equal(["s1"], shorts.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task HistoryFeed_FoldingSkipsShortsRows_AndRetitlesFirstNonShortsRow()
    {
        var (feed, _) = Make($$"""
            {"contents":{"sectionListRenderer":{"contents":[
              {"reelShelfRenderer":{"items":[{{ShortTile("s1")}}]}},
              {"shelfRenderer":{
                 "headerRenderer":{"shelfHeaderRenderer":{"title":{"simpleText":"Today"}}},
                 "content":{"horizontalListRenderer":{"items":[{{Tile("v1")}}]}}}},
              {"shelfRenderer":{"content":{"horizontalListRenderer":{
                 "items":[{{Tile("v2")}}]}}}}]}}}
            """);
        var page = await feed.LoadHistoryFeedAsync("T");
        Assert.Equal(2, page.Sections.Count);
        Assert.True(page.Sections[0].IsShorts);
        Assert.Equal("Shorts", page.Sections[0].Title);             // NOT retitled
        Assert.Equal("Continue watching", page.Sections[1].Title);  // first non-Shorts row
        Assert.Equal(["v1", "v2"], page.Sections[1].Items.Select(i => i.Id));
    }

    [Fact]
    public async Task RowContinuation_ReturnsRawItemsIncludingShorts()
    {
        var (feed, _) = Make($$"""
            {"continuationContents":{"horizontalListContinuation":{
               "items":[{{Tile("v3")}},{{ShortTile("s3")}}]}}}
            """);
        var row = await feed.LoadMoreItemsAsync("TOKEN", "T");
        Assert.Equal(["v3", "s3"], row.Items.Select(i => i.Id));    // no filtering here
    }
```

(`Search_ReturnsFlatListWithoutShorts` stays untouched — the search filter is unchanged.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/WinTube.Core.Tests` — expected: FAIL (new assertions against drop-behavior code).

- [ ] **Step 3: Implement**

`src/WinTube.Core/Models/FeedModels.cs`, on `FeedSection`:

```csharp
    /// Items from this row's continuation, shaped to what this row shows: a Shorts row
    /// vouches for everything it pages in, and every other row drops the Shorts YouTube
    /// mixes into it. Applied where a page is appended rather than where it's fetched,
    /// because a continuation reply is a bare cell list with nothing naming its shelf.
    public IReadOnlyList<VideoItem> Admitting(IReadOnlyList<VideoItem> items) =>
        IsShorts
            ? items.Select(item => item with { IsShort = true }).ToList()
            : items.Where(item => !item.IsShort).ToList();
```

`src/WinTube.Core/Feed/FeedService.cs`:

1. `LoadMoreItemsAsync`: drop the `.Where(item => !item.IsShort)` — return raw parsed items.
2. Add `private const string ShortsShelfTitle = "Shorts";`
3. Rewrite `ParsePage`'s shelf loop and fallback:

```csharp
    private static FeedPage ParsePage(JsonElement json)
    {
        var now = DateTimeOffset.UtcNow;
        var sections = new List<FeedSection>();
        // Shelves can nest; tracking emitted ids drops a shelf that only repeats an earlier
        // one without suppressing a video that legitimately appears in two rows.
        var emitted = new HashSet<string>();
        // Shorts pulled out of ordinary shelves, in the order they were met. Merged in below,
        // once it's known whether the response has a Shorts row of its own.
        var strayShorts = new List<VideoItem>();

        foreach (var (renderer, isReel) in FindShelves(json))
        {
            var items = VideoItemParser.Items(renderer, now);
            if (items.Count == 0) continue;
            if (items.All(item => emitted.Contains(item.Id))) continue;   // nested duplicate
            emitted.UnionWith(items.Select(item => item.Id));

            // The shelf's own kind first: a reel shelf is a Shorts row whatever its cells look
            // like, and so is one flying the Shorts glyph or holding nothing but Shorts.
            var isShorts = isReel || HasShortsIcon(renderer) || items.All(item => item.IsShort);
            var title = ShelfTitle(renderer) ?? "";
            var continuation = Continuation(renderer, RowContainers);

            if (isShorts)
            {
                sections.Add(new FeedSection(
                    Guid.NewGuid().ToString("N"),
                    title.Length == 0 ? ShortsShelfTitle : title,
                    items.Select(item => item with { IsShort = true }).ToList(),
                    continuation, IsShorts: true));
                continue;
            }

            strayShorts.AddRange(items.Where(item => item.IsShort));
            var videos = items.Where(item => !item.IsShort).ToList();
            // Everything in the shelf was a Short, and they're kept for the Shorts row.
            if (videos.Count == 0) continue;

            sections.Add(new FeedSection(
                Guid.NewGuid().ToString("N"), title, videos, continuation, IsShorts: false));
        }

        // Defensive fallback: no recognizable shelves — present everything playable as one
        // untitled row, Shorts still separated into their own.
        if (sections.Count == 0 && strayShorts.Count == 0)
        {
            var items = VideoItemParser.Items(json, now);
            strayShorts.AddRange(items.Where(item => item.IsShort));
            var videos = items.Where(item => !item.IsShort).ToList();
            if (videos.Count > 0)
                sections.Add(new FeedSection(
                    Guid.NewGuid().ToString("N"), "", videos, null, IsShorts: false));
        }

        return new FeedPage(
            Merging(strayShorts, sections), Continuation(json, SectionListContainers));
    }

    /// Puts the Shorts lifted out of ordinary shelves where they belong: appended to the
    /// response's own Shorts row, or a row of their own at the end. Without this a Short
    /// filtered out of Recommended would simply disappear.
    private static List<FeedSection> Merging(List<VideoItem> shorts, List<FeedSection> sections)
    {
        if (shorts.Count == 0) return sections;
        var index = sections.FindIndex(section => section.IsShorts);
        if (index >= 0)
        {
            var existing = sections[index].Items.Select(item => item.Id).ToHashSet();
            var fresh = shorts.Where(item => !existing.Contains(item.Id))
                .Select(item => item with { IsShort = true }).ToList();
            if (fresh.Count == 0) return sections;
            sections[index] = sections[index] with
            {
                Items = [.. sections[index].Items, .. fresh],
            };
        }
        else
        {
            sections.Add(new FeedSection(
                Guid.NewGuid().ToString("N"), ShortsShelfTitle,
                shorts.Select(item => item with { IsShort = true }).ToList(),
                null, IsShorts: true));
        }
        return sections;
    }
```

4. `FoldUntitledRows`: first line of the loop body becomes
   `if (section.IsShorts) { result.Add(section); continue; }` — a Shorts row is a genuine
   shelf that happens to be titled; it is never a target and never folded away.
5. `LoadHistoryFeedAsync`: replace the retitle of `sections[0]` with the first NON-Shorts row:

```csharp
        var first = sections.FindIndex(section => !section.IsShorts);
        if (first >= 0)
            sections[first] = sections[first] with { Title = "Continue watching" };
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/WinTube.Core.Tests` — expected: PASS (154 − 0 rewritten + 5 new/changed ≈ 159; the exact count from the run is the record).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: keep Shorts rows in the feed with per-row admitting"
```

---

### Task 2: ShortCard and the Shorts row in Home

**Files:**
- Create: `src/WinTube.App/Controls/ShortCard.xaml`, `src/WinTube.App/Controls/ShortCard.xaml.cs`
- Modify: `src/WinTube.App/Views/HomePage.xaml`, `src/WinTube.App/Views/HomePage.xaml.cs`

**Interfaces:**
- Consumes: `FeedSection.Admitting` (Task 1), `PlayerRequest`, the existing shelf view-model + row-paging machinery in HomePage.
- Produces: `ShortCard` — `Video` dependency property (`VideoItem`), `Clicked` event carrying it; Home renders Shorts rows with it.

Requirements:

1. **ShortCard**: 150 × 267 root; `Image` with `Stretch="UniformToFill"` and 8 px `CornerRadius` (clip via a `Border`); a 24 px circular avatar bottom-right (`Ellipse` with `ImageBrush`, `Visibility` collapsed when `ChannelAvatarUrl` is null); a light 2 px border shown on `PointerEntered`, hidden on `PointerExited` (transparent border always reserved so layout doesn't jump); `Tapped` raises `Clicked`. No title, stats, duration, or progress line.
2. **HomePage row template**: each shelf row shows EITHER the existing VideoCard ListView OR a ShortCard ListView, by the section's `IsShorts` (two ListViews with `x:Bind` visibility, or a `DataTemplateSelector` — implementer's choice; whichever fits the existing shelf template with least churn). Row height for a Shorts row fits the 267 px tile. The row header renders the section title as today ("Shorts" arrives from Core).
3. **Row paging**: where a continuation page's items are appended, replace the current non-Short filtering with `section.Admitting(page.Items)` (the view-model keeps whatever section record it wraps).
4. **Click**: ShortCard → `Frame.Navigate(typeof(PlayerPage), new PlayerRequest(video))` — same as VideoCard.
5. `History.Remember` keeps receiving all loaded items (Shorts included), unchanged.

- [ ] **Step 1: Implement** per the requirements.

- [ ] **Step 2: Build and test**

Run: `dotnet build src/WinTube.App -p:Platform=x64` (Release fallback under the known lock) and `dotnet test tests/WinTube.Core.Tests` — all green.

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "feat: portrait Shorts row in Home"
```

---

### Task 3: Wrap-up — README, plan ticks, manual verification

**Files:**
- Modify: `README.md`, `docs/superpowers/plans/2026-08-25-wintube-shorts.md`

- [ ] **Step 1: README** — scope table: "Shorts" row → "In (stage 5)" with a one-liner (own row, portrait tiles, plays in the ordinary player; strays are moved, never lost). No new section needed — one row-note carries it.

- [ ] **Step 2: Tick executed checkboxes** in this plan (UTF-8-safe tooling — em-dashes).

- [ ] **Step 3: MANUAL VERIFICATION (the user)** — leave unticked with an italic deferral note:
  1. Home shows a Shorts row of portrait tiles (no titles/durations on them).
  2. A tile plays in the normal player; SponsorBlock/copy/progress work as on any video.
  3. No portrait Shorts inside ordinary rows.

- [ ] **Step 4: Final gates and commit**

Run: `dotnet test tests/WinTube.Core.Tests` and `dotnet build src/WinTube.App -p:Platform=x64`.

```bash
git add -A && git commit -m "docs: Shorts row wrap-up"
```
