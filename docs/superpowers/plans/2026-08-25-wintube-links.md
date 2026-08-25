# WinTube Links Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Copy a video's YouTube URL from the player (SplitButton: plain / at current position) and open YouTube links in WinTube via the search box, the command line, and a `wintube://` protocol — single-instance.

**Architecture:** `WinTube.Core.Links.YouTubeLink` (pure, TDD) builds and parses links; the app adds a SplitButton to the player, URL-awareness to Search, a `PlayerRequest(Video, StartAt?)` navigation parameter, and a custom `Main` with `AppInstance` single-instancing + HKCU protocol registration.

**Tech Stack:** Existing only — .NET 8, WinUI 3 (`SplitButton`, `Microsoft.Windows.AppLifecycle.AppInstance`), `Microsoft.Win32.Registry` (in-box on the Windows TFM), xUnit. No new packages.

**Spec:** `docs/specs/2026-08-25-wintube-links-design.html` (approved 2026-08-25; SplitButton per the user's explicit choice).

## Global Constraints

- Copy format: `https://youtu.be/{id}` and `https://youtu.be/{id}?t={whole seconds}` (floor). Flyout label `Copy link at {Format(position)}` with `12:34` / `1:02:34` shapes.
- `TryParse` accepts ONLY: absolute http(s) URLs on hosts youtu.be / youtube.com / www.youtube.com / m.youtube.com / music.youtube.com with paths `/watch?v={id}`, youtu.be first-segment id, `/shorts/{id}`, `/embed/{id}`, `/live/{id}`; and `wintube://watch?v={id}` URIs. Start from `t=` or `start=`: raw seconds, `NNNs`, or `1h2m3s`. Video id = exactly 11 chars of `[A-Za-z0-9_-]`. Bare non-URL text is NEVER an id.
- A parsed `StartAt` overrides the stored resume position for that playback; progress recording unchanged.
- Single instance key `"wintube-main"`; a redirected second launch exits 0. Protocol key `HKCU\Software\Classes\wintube` written idempotently at startup; never touch HKLM; never register for http/https.
- Signed-out activation opens the app normally (login page); the link is dropped, not queued.
- Tests: `dotnet test tests/WinTube.Core.Tests` (125 green before this plan). App build: `dotnet build src/WinTube.App -p:Platform=x64` — if it fails ONLY with the known MSB3027 file-lock (user's VS session), `-c Release` must build clean as the substitute; never touch the VS process.
- Branch `master`; commit per task with the given message.

## File Structure

```
src/WinTube.Core/Links/YouTubeLink.cs        For / TryParse / Format (pure)
src/WinTube.App/PlayerRequest.cs             navigation parameter record
src/WinTube.App/Program.cs                   custom Main: single-instance + activation
src/WinTube.App/ProtocolRegistration.cs      idempotent HKCU wintube:// registration
src/WinTube.App/WinTube.App.csproj           MODIFY: DISABLE_XAML_GENERATED_MAIN
src/WinTube.App/App.xaml.cs                  MODIFY: register protocol, route activation
src/WinTube.App/MainWindow.xaml.cs           MODIFY: OpenVideo(videoId, startAt)
src/WinTube.App/Views/PlayerPage.xaml(.cs)   MODIFY: SplitButton + PlayerRequest + StartAt
src/WinTube.App/Views/HomePage.xaml.cs       MODIFY: navigate with PlayerRequest
src/WinTube.App/Views/SearchPage.xaml.cs     MODIFY: PlayerRequest + URL-aware queries
src/WinTube.App/Views/HistoryPage.xaml.cs    MODIFY: navigate with PlayerRequest
tests/WinTube.Core.Tests/YouTubeLinkTests.cs
```

---

### Task 1: YouTubeLink (build, parse, format)

**Files:**
- Create: `src/WinTube.Core/Links/YouTubeLink.cs`
- Test: `tests/WinTube.Core.Tests/YouTubeLinkTests.cs`

**Interfaces:**
- Produces (namespace `WinTube.Core.Links`): `static partial class YouTubeLink` with
  `string For(string videoId, TimeSpan? at = null)`,
  `(string VideoId, TimeSpan? StartAt)? TryParse(string text)`,
  `string Format(TimeSpan position)`.

- [x] **Step 1: Write the failing tests**

`tests/WinTube.Core.Tests/YouTubeLinkTests.cs`:

```csharp
using WinTube.Core.Links;

namespace WinTube.Core.Tests;

public class YouTubeLinkTests
{
    private const string Id = "dQw4w9WgXcQ";

    [Fact]
    public void For_PlainAndTimestamped()
    {
        Assert.Equal($"https://youtu.be/{Id}", YouTubeLink.For(Id));
        Assert.Equal($"https://youtu.be/{Id}?t=754",
            YouTubeLink.For(Id, TimeSpan.FromSeconds(754.9)));   // floored
    }

    [Theory]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", null)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=754", 754)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", null)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=90s", 90)]
    [InlineData("https://youtube.com/watch?list=PL123&v=dQw4w9WgXcQ&start=30", 30)]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ", null)]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ", null)]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", null)]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ?t=5", 5)]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ", null)]
    [InlineData("http://youtu.be/dQw4w9WgXcQ", null)]
    [InlineData("  https://youtu.be/dQw4w9WgXcQ  ", null)]          // trimmed
    [InlineData("wintube://watch?v=dQw4w9WgXcQ&t=1h2m3s", 3723)]
    public void TryParse_AcceptedShapes(string url, int? startSeconds)
    {
        var parsed = YouTubeLink.TryParse(url);
        Assert.NotNull(parsed);
        Assert.Equal(Id, parsed!.Value.VideoId);
        Assert.Equal(startSeconds is { } s ? TimeSpan.FromSeconds(s) : null,
            parsed.Value.StartAt);
    }

    [Theory]
    [InlineData("dQw4w9WgXcQ")]                                     // bare id is a search
    [InlineData("6502 computer")]
    [InlineData("https://vimeo.com/12345")]
    [InlineData("https://www.youtube.com/watch?v=short")]           // malformed id
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQtoolong")]
    [InlineData("https://www.youtube.com/feed/subscriptions")]
    [InlineData("https://youtu.be/")]
    [InlineData("wintube://sync")]
    [InlineData("")]
    public void TryParse_Rejections(string text)
    {
        Assert.Null(YouTubeLink.TryParse(text));
    }

    [Fact]
    public void TryParse_BadStartTime_StillParsesTheVideo()
    {
        var parsed = YouTubeLink.TryParse($"https://youtu.be/{Id}?t=abc");
        Assert.Equal((Id, (TimeSpan?)null), parsed);
    }

    [Theory]
    [InlineData(754, "12:34")]
    [InlineData(3754, "1:02:34")]
    [InlineData(34, "0:34")]
    public void Format_MatchesTheMenuShapes(int seconds, string expected)
    {
        Assert.Equal(expected, YouTubeLink.Format(TimeSpan.FromSeconds(seconds)));
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/WinTube.Core.Tests` — expected: compile FAIL.

- [x] **Step 3: Implement**

`src/WinTube.Core/Links/YouTubeLink.cs`:

```csharp
using System.Text.RegularExpressions;

namespace WinTube.Core.Links;

/// Builds and parses YouTube video links — the one grammar the copy button, the search box,
/// the command line and the wintube:// protocol all share.
public static partial class YouTubeLink
{
    /// The share form: youtu.be, with the position as whole seconds when given.
    public static string For(string videoId, TimeSpan? at = null) =>
        at is { } position
            ? $"https://youtu.be/{videoId}?t={(int)position.TotalSeconds}"
            : $"https://youtu.be/{videoId}";

    /// The parsed target of a link, or null when `text` isn't a YouTube link. Bare text is
    /// never treated as a video id — an 11-letter search term must stay a search.
    public static (string VideoId, TimeSpan? StartAt)? TryParse(string text)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0 || !Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;

        string? id = null;
        if (uri.Scheme is "http" or "https")
        {
            var host = uri.Host.ToLowerInvariant();
            var segments = uri.AbsolutePath.Trim('/').Split('/');
            if (host == "youtu.be")
            {
                id = segments.Length >= 1 ? segments[0] : null;
            }
            else if (host is "youtube.com" or "www.youtube.com" or "m.youtube.com"
                or "music.youtube.com")
            {
                if (segments.Length >= 1 && segments[0] == "watch")
                    id = QueryValue(uri, "v");
                else if (segments.Length >= 2 && segments[0] is "shorts" or "embed" or "live")
                    id = segments[1];
            }
            else
            {
                return null;
            }
        }
        else if (uri.Scheme == "wintube")
        {
            // wintube://watch?v={id}&t=… — the same watch grammar, for tools targeting us.
            if (uri.Host == "watch") id = QueryValue(uri, "v");
        }
        else
        {
            return null;
        }

        if (id is null || !VideoIdPattern().IsMatch(id)) return null;
        return (id, ParseStart(QueryValue(uri, "t") ?? QueryValue(uri, "start")));
    }

    /// The position as the copy menu shows it: 12:34, or 1:02:34 past an hour.
    public static string Format(TimeSpan position) =>
        position.TotalHours >= 1
            ? $"{(int)position.TotalHours}:{position.Minutes:D2}:{position.Seconds:D2}"
            : $"{position.Minutes}:{position.Seconds:D2}";

    private static string? QueryValue(Uri uri, string name)
    {
        foreach (var pair in uri.Query.TrimStart('?')
                     .Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (!pair[..eq].Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }

    /// t=754, t=754s, t=1h2m3s, start=30 — anything else contributes no start time (a broken
    /// timestamp must not reject the video itself).
    private static TimeSpan? ParseStart(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var match = StartPattern().Match(value);
        if (!match.Success) return null;
        var total = 0;
        if (match.Groups[1].Success) total += int.Parse(match.Groups[1].Value) * 3600;
        if (match.Groups[2].Success) total += int.Parse(match.Groups[2].Value) * 60;
        if (match.Groups[3].Success) total += int.Parse(match.Groups[3].Value);
        return TimeSpan.FromSeconds(total);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdPattern();

    [GeneratedRegex(@"^(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s?)?$")]
    private static partial Regex StartPattern();
}
```

Note: `StartPattern` matches the empty string (all groups optional) — but `ParseStart` only runs on non-empty values, and a non-empty value that matches with NO successful groups is impossible (some digits must have matched); a value like `"abc"` fails the match → null, which the `t=abc` test pins.

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/WinTube.Core.Tests` — expected: PASS (the Theories expand to ~22 new cases).

- [x] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: YouTube link grammar (build, parse, format)"
```

---

### Task 2: Copy SplitButton, PlayerRequest, URL-aware search

**Files:**
- Create: `src/WinTube.App/PlayerRequest.cs`
- Modify: `src/WinTube.App/Views/PlayerPage.xaml`, `src/WinTube.App/Views/PlayerPage.xaml.cs`, `src/WinTube.App/Views/HomePage.xaml.cs`, `src/WinTube.App/Views/SearchPage.xaml.cs`, `src/WinTube.App/Views/HistoryPage.xaml.cs`

**Interfaces:**
- Consumes: `YouTubeLink` (Task 1), `VideoItem` (+`FallbackThumbnail`), the player's existing toast (`ShowSkipToast(string)` — generic despite the name), clipboard pattern from LoginPage.
- Produces: `public sealed record PlayerRequest(WinTube.Core.Models.VideoItem Video, TimeSpan? StartAt = null);` (namespace `WinTube.App`) — the ONLY navigation parameter PlayerPage accepts from now on; Task 3 navigates with it too.

Changes:

1. **PlayerRequest.cs** — the record above, with a doc comment: StartAt overrides the stored resume position for this playback (a timestamped link's whole point).

2. **PlayerPage.OnNavigatedTo**: parameter is now `PlayerRequest`; store `startAt` in a field. Where resume is applied (MediaOpened), use `startAt ?? resume-from-store` (both in seconds; `startAt.Value.TotalSeconds`). Everything else (progress recording, SponsorBlock) unchanged — a resume landing mid-sponsor is already handled by the poll.

3. **All three navigation call sites** (`HomePage`, `SearchPage`, `HistoryPage` card-click handlers): `Frame.Navigate(typeof(PlayerPage), new PlayerRequest(video))`.

4. **SplitButton** in PlayerPage's title-bar row, right of the title (before or after the existing elements as layout allows):

```xml
<SplitButton x:Name="CopyLinkButton" Click="OnCopyLink" VerticalAlignment="Center">
    <StackPanel Orientation="Horizontal" Spacing="6">
        <FontIcon Glyph="&#xE71B;" FontSize="14"/>
        <TextBlock Text="Copy link"/>
    </StackPanel>
    <SplitButton.Flyout>
        <MenuFlyout Opening="OnCopyFlyoutOpening">
            <MenuFlyoutItem x:Name="CopyLinkAtItem" Click="OnCopyLinkAt"/>
        </MenuFlyout>
    </SplitButton.Flyout>
</SplitButton>
```

Code-behind:

```csharp
    private TimeSpan flyoutPosition;

    private void OnCopyLink(SplitButton sender, SplitButtonClickEventArgs args) =>
        CopyLink(YouTubeLink.For(video!.Id));

    /// The label carries the live position captured when the flyout opens, so what the item
    /// says is exactly what a click copies.
    private void OnCopyFlyoutOpening(object sender, object e)
    {
        flyoutPosition = mediaPlayer?.PlaybackSession?.Position ?? TimeSpan.Zero;
        CopyLinkAtItem.Text = $"Copy link at {YouTubeLink.Format(flyoutPosition)}";
    }

    private void OnCopyLinkAt(object sender, RoutedEventArgs e) =>
        CopyLink(YouTubeLink.For(video!.Id, flyoutPosition));

    private void CopyLink(string url)
    {
        var package = new DataPackage();
        package.SetText(url);
        Clipboard.SetContent(package);
        ShowSkipToast("Link copied");
    }
```

(`using Windows.ApplicationModel.DataTransfer;` — the same API LoginPage's copy-code uses.)

5. **SearchPage.OnQuerySubmitted**, before running a search:

```csharp
        if (YouTubeLink.TryParse(query) is { } link)
        {
            Frame.Navigate(typeof(PlayerPage), new PlayerRequest(
                new VideoItem
                {
                    Id = link.VideoId,
                    Title = "",
                    ThumbnailUrl = VideoItem.FallbackThumbnail(link.VideoId),
                },
                link.StartAt));
            return;
        }
```

- [x] **Step 1: Implement** the five changes.

- [x] **Step 2: Build and test**

Run: `dotnet build src/WinTube.App -p:Platform=x64` (or `-c Release` under the known VS lock) and `dotnet test tests/WinTube.Core.Tests` — all green.

- [x] **Step 3: Commit**

```bash
git add -A && git commit -m "feat: copy-link split button and URL-aware search"
```

---

### Task 3: Single instance, wintube:// protocol, command line

**Files:**
- Create: `src/WinTube.App/Program.cs`, `src/WinTube.App/ProtocolRegistration.cs`
- Modify: `src/WinTube.App/WinTube.App.csproj` (define `DISABLE_XAML_GENERATED_MAIN`), `src/WinTube.App/App.xaml.cs`, `src/WinTube.App/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `YouTubeLink.TryParse`, `PlayerRequest` (Task 2), `App.Session.IsSignedIn`.
- Produces: `MainWindow.OpenVideo(string videoId, TimeSpan? startAt)` (Task 4's manual steps exercise it).

Changes:

1. **csproj**: in the main PropertyGroup add
   `<DefineConstants>$(DefineConstants);DISABLE_XAML_GENERATED_MAIN</DefineConstants>`.

2. **Program.cs** — the standard unpackaged single-instance bootstrap:

```csharp
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace WinTube.App;

/// Custom entry point so a second launch (a wintube:// activation, a command line) redirects
/// to the running instance instead of opening a second window.
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var main = AppInstance.FindOrRegisterForKey("wintube-main");
        if (!main.IsCurrent)
        {
            main.RedirectActivationToAsync(
                AppInstance.GetCurrent().GetActivatedEventArgs()).AsTask().Wait();
            return 0;
        }

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
```

3. **ProtocolRegistration.cs**:

```csharp
using Microsoft.Win32;

namespace WinTube.App;

/// Registers wintube:// for the current user, so links and tools can target the app.
/// HKCU only, idempotent — rewritten only when the stored command drifts (a moved exe).
/// Never registers for http/https: stealing browser links is not this app's place.
public static class ProtocolRegistration
{
    public static void EnsureRegistered()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            var command = $"\"{exe}\" \"%1\"";

            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\wintube");
            using var commandKey = key.CreateSubKey(@"shell\open\command");
            if (Equals(commandKey.GetValue(null), command)) return;
            key.SetValue(null, "URL:WinTube");
            key.SetValue("URL Protocol", "");
            using var icon = key.CreateSubKey("DefaultIcon");
            icon.SetValue(null, $"\"{exe}\",0");
            commandKey.SetValue(null, command);
        }
        catch (Exception e) when (e is System.Security.SecurityException
            or UnauthorizedAccessException or IOException)
        {
            // A locked-down registry costs the protocol, not the app.
        }
    }
}
```

4. **App.xaml.cs** (`OnLaunched`): call `ProtocolRegistration.EnsureRegistered();`, subscribe
   `AppInstance.GetCurrent().Activated += OnRedirectedActivation;`, create/activate the window
   as today, then handle the INITIAL activation: collect candidate strings from
   `AppInstance.GetCurrent().GetActivatedEventArgs()` (`Kind == Protocol` →
   `((ProtocolActivatedEventArgs)Data).Uri.ToString()`) plus
   `Environment.GetCommandLineArgs().Skip(1)`; the first that `YouTubeLink.TryParse`s →
   `Window.OpenVideo(...)`. `OnRedirectedActivation` does the same for the redirected args,
   marshalled via the window's `DispatcherQueue.TryEnqueue`. All of it wrapped so a parse
   surprise can never crash startup.

5. **MainWindow.OpenVideo(string videoId, TimeSpan? startAt)**: if `!App.Session.IsSignedIn`
   do nothing (login page is already showing); else front the window (`Activate()`) and
   `RootFrame.Navigate(typeof(Views.PlayerPage), new PlayerRequest(new VideoItem { Id = videoId, Title = "", ThumbnailUrl = VideoItem.FallbackThumbnail(videoId) }, startAt));`.

- [x] **Step 1: Implement** the five changes.

- [x] **Step 2: Build and test**

Run: `dotnet build src/WinTube.App -p:Platform=x64` (or `-c Release` under the known VS lock) and `dotnet test tests/WinTube.Core.Tests` — all green.

- [x] **Step 3: Commit**

```bash
git add -A && git commit -m "feat: single-instance activation, wintube protocol, command-line open"
```

---

### Task 4: Wrap-up — README, plan ticks, manual verification

**Files:**
- Modify: `README.md`, `docs/superpowers/plans/2026-08-25-wintube-links.md`

- [x] **Step 1: README** — an "Opening and sharing links" section in the metube voice: the copy SplitButton (plain / at position, youtu.be form); the three ways in (paste a URL in Search, `WinTube.App.exe <url>`, `wintube://watch?v=…`); single instance; the honest browser limitation verbatim from the spec's callout. Scope table: move BOTH rows — "Copy YouTube URL" and "Open YouTube links in the app" — to "In (stage 4)".

- [x] **Step 2: Tick the executed checkboxes** in this plan (UTF-8-safe tooling — the file carries em-dashes).

- [ ] **Step 3: MANUAL VERIFICATION (the user)**
  *Partially verified 2026-08-25: both copy actions confirmed by the user (with and without timestamp). Search-URL open and wintube:// activation still pending.*
  1. Copy link → clipboard holds `https://youtu.be/{id}`; chevron → "Copy link at 12:34" → `?t=754`.
  2. Paste a timestamped YouTube link in Search → the player opens at that position.
  3. With the app running: `start wintube://watch?v=dQw4w9WgXcQ` in a terminal → the existing window fronts and plays; with the app closed, the same command starts it and plays.

- [ ] **Step 4: Final gates and commit**

Run: `dotnet test tests/WinTube.Core.Tests` and `dotnet build src/WinTube.App -p:Platform=x64` (Release fallback under the known lock).

```bash
git add -A && git commit -m "docs: links README and wrap-up"
```
