# WinTube Comments Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Comments button in the player opens a translucent right-edge panel — over the video, which keeps playing — showing top comments and one level of replies, read-only.

**Architecture:** A new `ClientKind.Web` identity carries unauthenticated `/next` calls. `CommentService` (Core) ports the tvOS two-step fetch and the joined entity/renderer page parser. `PlayerPage` gains the toggle button and the overlay panel with a two-level list.

**Tech Stack:** Existing only. No new packages.

**Spec:** `docs/specs/2026-09-05-wintube-comments-design.html` (approved 2026-09-05). tvOS arbiters in the scratchpad metube checkout (re-clone https://github.com/claust/metube if gone): `YouTubeTV/Sources/Player/CommentService.swift`, `YouTubeTV/Sources/Player/CommentsOverlayView.swift`, `YouTubeTV/Sources/Core/AppConfig.swift` (.web).

## Global Constraints

- WEB client identity, verbatim from AppConfig.swift: name `WEB`, version `2.20260726.00.00`, nameId `1`, UA `Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36`, referer `https://www.youtube.com`, host `https://www.youtube.com`, no visitorData, no extra context. Comment calls send NO Bearer.
- Two-step fetch: `/next {"videoId"}` only locates the token (the `itemSectionRenderer` whose `sectionIdentifier` is `"comment-item-section"`); no section = typed "comments unavailable". `/next {"continuation"}` returns comments.
- Page parse: bodies are `commentEntityPayload` entities keyed by `properties/commentId`; order and reply tokens come from `commentThreadRenderer`s (top-level pages, at most one replies `continuationCommand` under `replies`) or bare `commentViewModel`s in order (reply pages). Next-page token = the trailing `continuationItemRenderer` of the response's `onResponseReceivedEndpoints` continuation-items list — never tokens nested inside threads.
- `CommentItem` fields verbatim strings from InnerTube (handle, relative time, abbreviated counts); LikeCount falls back to `"0"`; RepliesToken null when no replies; reply pages never carry reply tokens.
- Panel: 400 px, right edge, over the playing video (dark translucent), toggled by a title-bar button and Esc; two levels with the parent pinned; "Load more" while a continuation exists; loaded once per video, kept while the player page lives, discarded on navigation; comment failures never touch playback.
- Copy (English, matching app language): "Comments aren't available for this video." / "No comments yet." / error line + "Retry" / "Load more" / "Show more replies".
- Tests: `dotnet test tests/WinTube.Core.Tests` (190 green before this plan). App build: `dotnet build src/WinTube.App -p:Platform=x64`; MSB3027-only failure → `Get-Process WinTube.App | Stop-Process`, retry, touch nothing else.
- Branch `master`; commit per task with the given message.

## File Structure

```
src/WinTube.Core/InnerTube/Clients.cs         MODIFY: ClientKind.Web + Web ClientInfo
src/WinTube.Core/Models/CommentModels.cs      CREATE: CommentItem, CommentPage
src/WinTube.Core/Player/CommentService.cs     CREATE: two-step fetch + page parser
src/WinTube.App/Session.cs                    MODIFY: expose Comments service
src/WinTube.App/Views/PlayerPage.xaml(.cs)    MODIFY: Comments button + overlay panel
tests/WinTube.Core.Tests/CommentServiceTests.cs CREATE
```

---

### Task 1: Core — WEB client and CommentService

**Files:**
- Modify: `src/WinTube.Core/InnerTube/Clients.cs`
- Create: `src/WinTube.Core/Models/CommentModels.cs`
- Create: `src/WinTube.Core/Player/CommentService.cs`
- Test: `tests/WinTube.Core.Tests/CommentServiceTests.cs`

**Interfaces:**
- Consumes: `InnerTubeClient.PostAsync(endpoint, kind, parameters, bearer: null, ct: ct)`, `Json` helpers (read `Json.cs` and `ChannelHeaderParser.cs` for the traversal idiom).
- Produces (exact — Task 2 relies on these):
  `ClientKind.Web`;
  `public sealed record CommentItem(string Id, string Author, string? AvatarUrl, string Text, string PublishedTime, string LikeCount, string ReplyCount, string? RepliesToken) { public bool HasReplies => RepliesToken is not null; }`
  `public sealed record CommentPage(IReadOnlyList<CommentItem> Comments, string? Continuation);`
  `public sealed class CommentsUnavailableException() : Exception("Comments aren't available for this video.");`
  `CommentService(InnerTubeClient)` with `Task<CommentPage> TopLevelAsync(string videoId, CancellationToken ct = default)` and `Task<CommentPage> PageAsync(string continuation, CancellationToken ct = default)`.

- [ ] **Step 1: Write the failing tests**

New `tests/WinTube.Core.Tests/CommentServiceTests.cs`:

```csharp
using WinTube.Core;
using WinTube.Core.InnerTube;
using WinTube.Core.Player;

namespace WinTube.Core.Tests;

public class CommentServiceTests
{
    private static string EntityPayload(string id, string text, string author = "@a",
        string likes = "12K", string replies = "") =>
        "{\"commentEntityPayload\":{" +
        "\"properties\":{\"commentId\":\"" + id + "\"," +
        "\"content\":{\"content\":\"" + text + "\"},\"publishedTime\":\"2 years ago\"}," +
        "\"author\":{\"displayName\":\"" + author + "\"," +
        "\"avatarThumbnailUrl\":\"https://yt3.ggpht.com/" + id + "\"}," +
        "\"toolbar\":{\"likeCountNotliked\":\"" + likes + "\",\"replyCount\":\"" + replies + "\"}}}";

    private static string Thread(string id, string? repliesToken = null) =>
        "{\"commentThreadRenderer\":{" +
        "\"commentViewModel\":{\"commentViewModel\":{\"commentId\":\"" + id + "\"}}" +
        (repliesToken is null ? "" :
            ",\"replies\":{\"commentRepliesRenderer\":{\"contents\":[{\"continuationItemRenderer\":{" +
            "\"button\":{\"buttonRenderer\":{\"command\":{\"continuationCommand\":{\"token\":\"" +
            repliesToken + "\"}}}}}}]}}") +
        "}}";

    /// A page response: threads (or bare view models), the entity payloads, and optionally a
    /// trailing continuation item for the next page.
    private static string PageResponse(string items, string payloads, string? nextToken) =>
        "{\"onResponseReceivedEndpoints\":[{\"reloadContinuationItemsCommand\":{" +
        "\"continuationItems\":[" + items +
        (nextToken is null ? "" :
            ",{\"continuationItemRenderer\":{\"continuationEndpoint\":{" +
            "\"continuationCommand\":{\"token\":\"" + nextToken + "\"}}}}") +
        "]}}]," +
        "\"frameworkUpdates\":{\"entityBatchUpdate\":{\"mutations\":[" + payloads + "]}}}";

    private static (CommentService Service, StubHttpHandler Handler) Make(
        Func<string, string> respondByBody)
    {
        var handler = new StubHttpHandler((_, body) => StubHttpHandler.JsonResponse(respondByBody(body)));
        var client = new InnerTubeClient(new HttpClient(handler), new Secrets("K", "", ""));
        return (new CommentService(client), handler);
    }

    [Fact]
    public async Task TopLevel_FindsTheCommentSectionToken_ThenFetchesThePage()
    {
        var watchPage = "{\"contents\":{\"sections\":[" +
            "{\"itemSectionRenderer\":{\"sectionIdentifier\":\"other-section\"," +
            "\"contents\":[{\"continuationItemRenderer\":{\"continuationEndpoint\":{" +
            "\"continuationCommand\":{\"token\":\"WRONG\"}}}}]}}," +
            "{\"itemSectionRenderer\":{\"sectionIdentifier\":\"comment-item-section\"," +
            "\"contents\":[{\"continuationItemRenderer\":{\"continuationEndpoint\":{" +
            "\"continuationCommand\":{\"token\":\"COMMENTS_TOKEN\"}}}}]}}]}}";
        var page = PageResponse(Thread("c1"), EntityPayload("c1", "hello"), nextToken: null);
        var (service, handler) = Make(body => body.Contains("\"videoId\"") ? watchPage : page);

        var result = await service.TopLevelAsync("vid");

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("\"continuation\":\"COMMENTS_TOKEN\"", handler.Requests[1].Body);
        Assert.Equal("hello", Assert.Single(result.Comments).Text);
        // WEB client, unauthenticated:
        Assert.Null(handler.Requests[0].Message.Headers.Authorization);
        Assert.Equal("1", handler.Requests[0].Message.Headers.GetValues("X-Youtube-Client-Name").Single());
        Assert.Contains("\"clientName\":\"WEB\"", handler.Requests[0].Body);
        Assert.Contains("\"clientVersion\":\"2.20260726.00.00\"", handler.Requests[0].Body);
    }

    [Fact]
    public async Task TopLevel_NoCommentSection_IsTyped()
    {
        var (service, _) = Make(_ => "{\"contents\":{}}");
        await Assert.ThrowsAsync<CommentsUnavailableException>(() => service.TopLevelAsync("vid"));
    }

    [Fact]
    public async Task Page_JoinsThreadsWithPayloads_InThreadOrder_WithReplyTokens()
    {
        var response = PageResponse(
            Thread("c2", repliesToken: "REPLIES_C2") + "," + Thread("c1"),
            EntityPayload("c1", "first", author: "@one", likes: "", replies: "") + "," +
            EntityPayload("c2", "second", author: "@two", likes: "3.4K", replies: "214"),
            nextToken: "PAGE2");
        var (service, _) = Make(_ => response);

        var page = await service.PageAsync("T");

        Assert.Equal(["c2", "c1"], page.Comments.Select(c => c.Id));   // renderer order wins
        var c2 = page.Comments[0];
        Assert.Equal("@two", c2.Author);
        Assert.Equal("https://yt3.ggpht.com/c2", c2.AvatarUrl);
        Assert.Equal("2 years ago", c2.PublishedTime);
        Assert.Equal("3.4K", c2.LikeCount);
        Assert.Equal("214", c2.ReplyCount);
        Assert.Equal("REPLIES_C2", c2.RepliesToken);
        Assert.True(c2.HasReplies);
        var c1 = page.Comments[1];
        Assert.Equal("0", c1.LikeCount);          // empty like count falls back to "0"
        Assert.Null(c1.RepliesToken);
        Assert.Equal("PAGE2", page.Continuation); // trailing item, not the nested reply token
    }

    [Fact]
    public async Task Page_RepliesShape_BareViewModelsInOrder_NoReplyTokens()
    {
        var response = PageResponse(
            "{\"commentViewModel\":{\"commentId\":\"r1\"}},{\"commentViewModel\":{\"commentId\":\"r2\"}}",
            EntityPayload("r1", "reply one") + "," + EntityPayload("r2", "reply two"),
            nextToken: null);
        var (service, _) = Make(_ => response);

        var page = await service.PageAsync("T");

        Assert.Equal(["r1", "r2"], page.Comments.Select(c => c.Id));
        Assert.All(page.Comments, c => Assert.Null(c.RepliesToken));
        Assert.Null(page.Continuation);
    }

    [Fact]
    public async Task Page_PayloadWithoutRenderer_AndRendererWithoutPayload_AreDropped()
    {
        var response = PageResponse(
            Thread("c1") + "," + Thread("ghost"),
            EntityPayload("c1", "kept") + "," + EntityPayload("orphan", "dropped"),
            nextToken: null);
        var (service, _) = Make(_ => response);
        var page = await service.PageAsync("T");
        Assert.Equal(["c1"], page.Comments.Select(c => c.Id));
    }

    [Fact]
    public async Task Page_Empty_IsAnEmptyPage()
    {
        var (service, _) = Make(_ => "{\"onResponseReceivedEndpoints\":[]}");
        var page = await service.PageAsync("T");
        Assert.Empty(page.Comments);
        Assert.Null(page.Continuation);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/WinTube.Core.Tests`
Expected: compile FAILURE — `ClientKind.Web`, `CommentService`, models not defined.

- [ ] **Step 3: Implement**

`Clients.cs`: add `Web` to `ClientKind` (doc comment: "WEB — comments via /next, the one
client verified to return the comment section. Unauthenticated.") and the info:

```csharp
    private const string WebUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private static readonly ClientInfo Web = new(
        Name: "WEB", Version: "2.20260726.00.00", NameId: "1",
        UserAgent: WebUserAgent, Referer: "https://www.youtube.com",
        Host: "https://www.youtube.com", RequiresVisitorData: false, ExtraContext: NoExtra);
```

(and the `Get` switch arm).

New `src/WinTube.Core/Models/CommentModels.cs`: the records from the Interfaces block,
with doc comments carrying the tvOS semantics (verbatim strings, "0" fallback, flat threads).

New `src/WinTube.Core/Player/CommentService.cs` — port of CommentService.swift. Structure:

```csharp
using System.Text.Json;
using WinTube.Core.InnerTube;
using WinTube.Core.Models;

namespace WinTube.Core.Player;

public sealed class CommentsUnavailableException()
    : Exception("Comments aren't available for this video.");

/// Fetches a video's comments and replies via InnerTube /next (WEB client, unauthenticated).
/// Two-step flow: /next {"videoId"} only says WHERE the comments are (a continuation token on
/// the watch page's comment section); /next {"continuation"} returns the comments. Bodies live
/// in frameworkUpdates as commentEntityPayload entities keyed by commentId; ordering and reply
/// tokens come from the renderer list — a page is parsed by joining the two on commentId.
public sealed class CommentService(InnerTubeClient innerTube)
{
    public async Task<CommentPage> TopLevelAsync(string videoId, CancellationToken ct = default)
    {
        using var doc = await innerTube.PostAsync("next", ClientKind.Web,
            new Dictionary<string, object?> { ["videoId"] = videoId }, ct: ct);
        // The watch page carries the comment section twice (inline + engagement panel), both
        // with the same sectionIdentifier; their tokens differ but resolve to the same list.
        var token = FindRenderers(doc.RootElement, "itemSectionRenderer")
            .Where(s => Json.StringAt(s, "sectionIdentifier") == "comment-item-section")
            .Select(FirstContinuationToken)
            .FirstOrDefault(t => t is not null)
            ?? throw new CommentsUnavailableException();
        return await PageAsync(token, ct);
    }

    public async Task<CommentPage> PageAsync(string continuation, CancellationToken ct = default)
    {
        using var doc = await innerTube.PostAsync("next", ClientKind.Web,
            new Dictionary<string, object?> { ["continuation"] = continuation }, ct: ct);
        var root = doc.RootElement;

        var payloads = new Dictionary<string, JsonElement>();
        foreach (var payload in FindRenderers(root, "commentEntityPayload"))
            if (Json.StringAt(payload, "properties/commentId") is { } id)
                payloads[id] = payload;

        var comments = new List<CommentItem>();
        var threads = FindRenderers(root, "commentThreadRenderer").ToList();
        if (threads.Count == 0)
        {
            foreach (var viewModel in FindRenderers(root, "commentViewModel"))
                if (Json.StringAt(viewModel, "commentId") is { } id
                    && payloads.TryGetValue(id, out var payload))
                    comments.Add(Item(payload, id, repliesToken: null));
        }
        else
        {
            foreach (var thread in threads)
            {
                var id = FindRenderers(thread, "commentViewModel")
                    .Select(vm => Json.StringAt(vm, "commentId"))
                    .FirstOrDefault(x => x is not null);
                if (id is null || !payloads.TryGetValue(id, out var payload)) continue;
                var repliesToken = thread.TryGetProperty("replies", out var replies)
                    ? FirstContinuationToken(replies) : null;
                comments.Add(Item(payload, id, repliesToken));
            }
        }

        return new CommentPage(comments, NextPageToken(root));
    }
    // ... Item, FindRenderers, FirstContinuationToken, NextPageToken below
}
```

The helpers, all ported 1:1 from the Swift file (adapt to the codebase's `Json`/walker idiom —
read `ChannelHeaderParser.cs` first):

- `Item(payload, id, repliesToken)`: author = `author/displayName`, avatar =
  `author/avatarThumbnailUrl`, text = `properties/content/content`, time =
  `properties/publishedTime`, likes = `toolbar/likeCountNotliked` with `""`→`"0"`, replies =
  `toolbar/replyCount` (empty allowed).
- `FindRenderers(element, name)`: every object-valued property named `name`, anywhere,
  document order (objects by property order, arrays in order) — the codebase walker pattern.
- `FirstContinuationToken(element)`: first `continuationCommand` with a `token` string.
- `NextPageToken(root)`: for each entry of `onResponseReceivedEndpoints`, for each of its
  object values holding a `continuationItems` array — take the LAST item; if it is a
  `continuationItemRenderer`, return its first continuation token. Never scan non-trailing
  items (those are threads whose nested tokens are reply tokens).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/WinTube.Core.Tests`
Expected: PASS (190 + 6 new).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: WEB client and comment fetching"
```

---

### Task 2: App — the comments panel

**Files:**
- Modify: `src/WinTube.App/Session.cs`
- Modify: `src/WinTube.App/Views/PlayerPage.xaml`, `src/WinTube.App/Views/PlayerPage.xaml.cs`

**Interfaces:**
- Consumes: `CommentService.TopLevelAsync/PageAsync`, `CommentItem`/`CommentPage`,
  `CommentsUnavailableException` (Task 1).
- Produces: nothing new outside PlayerPage.

- [ ] **Step 1: Session wiring**

`Session.cs`: `public CommentService Comments { get; }` constructed as
`Comments = new CommentService(InnerTube);` beside the other services.

- [ ] **Step 2: The panel XAML**

`PlayerPage.xaml` title bar: a `ToggleButton x:Name="CommentsButton"` beside CopyLinkButton
(glyph `&#xE90A;` + "Comments"), `Click="OnToggleComments"`.

In the row-1 Grid (over the video, alongside SkipToast/LoadingRing), add:

```xml
<Grid x:Name="CommentsPanel" Width="400" HorizontalAlignment="Right"
      Visibility="Collapsed" Background="#D9141414">
    <Grid.RowDefinitions>
        <RowDefinition Height="Auto"/>
        <RowDefinition Height="*"/>
    </Grid.RowDefinitions>
    <Grid Grid.Row="0" Padding="16,12" ColumnDefinitions="Auto,*,Auto">
        <Button x:Name="RepliesBackButton" Grid.Column="0" Content="&#xE72B;"
                FontFamily="{ThemeResource SymbolThemeFontFamily}"
                Visibility="Collapsed" Click="OnRepliesBack" Margin="0,0,8,0"/>
        <TextBlock x:Name="PanelTitle" Grid.Column="1" Text="COMMENTS"
                   VerticalAlignment="Center" CharacterSpacing="60" FontSize="13"/>
        <Button Grid.Column="2" Content="&#xE711;"
                FontFamily="{ThemeResource SymbolThemeFontFamily}" Click="OnToggleComments"/>
    </Grid>
    <ListView x:Name="CommentsList" Grid.Row="1" SelectionMode="None"
              IsItemClickEnabled="True" ItemClick="OnCommentClicked"
              ItemsSource="{x:Bind commentRows}">
        <!-- DataTemplate x:DataType local:CommentRowViewModel: horizontal 32px Ellipse
             (ImageBrush from AvatarUrl, themed fallback fill), then a vertical stack:
             muted 12px "@handle · time" line, wrapping 14px Text, muted 12px likes/replies
             line with a chevron when HasReplies. A pinned parent row draws a bottom border;
             the Load More row is centered accent text. -->
    </ListView>
    <TextBlock x:Name="CommentsStatus" Grid.Row="1" Margin="16" TextWrapping="Wrap"
               Visibility="Collapsed" Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
    <Button x:Name="CommentsRetry" Grid.Row="1" Content="Retry" VerticalAlignment="Center"
            HorizontalAlignment="Center" Visibility="Collapsed" Click="OnCommentsRetry"/>
</Grid>
```

Concretize the commented DataTemplate (write the real XAML; a `CommentRowViewModel` in
PlayerPage.xaml.cs carries `Author`, `TimeAndAuthorLine`, `Text`, `MetaLine`, `AvatarUrl`,
`HasReplies`, `IsPinnedParent`, `IsLoadMore`, and the underlying `CommentItem`).

- [ ] **Step 3: The behavior (PlayerPage.xaml.cs)**

State: `ObservableCollection<CommentRowViewModel> commentRows`, `CommentPage? topLevel` cache
(list + continuation), `CommentItem? repliesParent`, `string? repliesContinuation`,
`bool commentsLoaded`, plus a `CancellationTokenSource` tied to the page's video.

- `OnToggleComments`: flips `CommentsPanel.Visibility`; first open per video triggers
  `LoadTopLevelAsync()`.
- `LoadTopLevelAsync`: status "Loading…" → `App.Session.Comments.TopLevelAsync(video.Id)`;
  on success renders rows (+ "Load more" row while `Continuation != null`); empty list →
  status "No comments yet."; `CommentsUnavailableException` → status
  "Comments aren't available for this video." (no retry); other exceptions → status message +
  Retry button. All awaits guarded by `leftPage` like every other player path.
- `OnCommentClicked`: Load More row → fetch the pending continuation (top-level appends;
  replies appends); a comment with `HasReplies` at top level → drill down: `repliesParent` set,
  `PanelTitle` = "REPLIES", back button visible, rows = pinned parent + `PageAsync(RepliesToken)`
  results; a "Show more replies" row pages the reply list.
- `OnRepliesBack`: restore the cached top-level rows (list + continuation), hide back button,
  title "COMMENTS".
- Esc: PlayerPage `KeyDown` (wire `KeyDown="OnKeyDown"` on the Page): if the panel is open,
  close it and mark handled — replies level first goes back one level, matching tvOS.
- `OnNavigatedTo`: reset everything (panel hidden, caches cleared, button unchecked) — a new
  video starts clean. `OnNavigatedFrom`: cancel any in-flight comment fetch.
- Comment failures must never touch playback: every comment path is fully try/caught and only
  ever writes panel UI.

- [ ] **Step 4: Build, run tests, verify visually**

Run: `dotnet test tests/WinTube.Core.Tests` and `dotnet build src/WinTube.App -p:Platform=x64`.
Launch, play a video, open Comments: list appears over the playing video; click a comment with
replies → parent pinned + replies + back; Load more pages; Esc walks back then closes; a video
with comments off shows the quiet line; navigating away and back starts clean.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: read-only comments panel in the player"
```
