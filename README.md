# WinTube

**A YouTube client for Windows, built for one viewer.**

> **This is a personal project, for my own use only.** It reimplements YouTube's private
> InnerTube API against my own Google account, it is distributed to nobody but me, and it is
> not intended, supported, or fit for anyone else's use. The code is public because there is
> no reason to hide it — not because it is a product.

## What it is

A Windows desktop port of [metube](https://github.com/claust/metube), the same client built
for Apple TV: sign in with YouTube's TV device-activation flow, browse your own Home
recommendations, search, and play videos with resume — as an ordinary windowed
mouse-and-keyboard app instead of a 10-foot remote-driven one. Same account, same InnerTube
recipe, same watch-progress rules.

## Install

Download the latest `*-Setup.exe` from
[the releases page](https://github.com/TroelsSjostedt/wintube/releases/latest) and run it.
SmartScreen will warn — this is an unsigned personal project — so pick "More info" and then
"Run anyway". It installs per-user (no admin), puts WinTube in the Start menu, and launches.
Sign in with the device code it shows. Updates arrive automatically: the app checks at
startup and applies new versions on the next restart.

## Optional overrides

The app ships with the public YouTube-on-TV client constants embedded (the same app-identity
values every TV firmware carries — nothing personal). A `%LOCALAPPDATA%\WinTube\secrets.json`
can override them field by field, and is also where the optional watch-progress sync fields
go:

```powershell
mkdir $env:LOCALAPPDATA\WinTube -ea 0
cp secrets.example.json $env:LOCALAPPDATA\WinTube\secrets.json
notepad $env:LOCALAPPDATA\WinTube\secrets.json
```

## Build, run, test

```powershell
dotnet build src/WinTube.App -p:Platform=x64
.\src\WinTube.App\bin\x64\Debug\net8.0-windows10.0.19041.0\WinTube.App.exe
# or, equivalently:
dotnet run --project src/WinTube.App -p:Platform=x64

dotnet test tests/WinTube.Core.Tests
```

## Watch-progress sync

**Optional:** Resume positions sync with an Apple TV running metube and an Appwrite backend. Off by default.

To enable: add `appwriteHost` (host only, no scheme) and `appwriteProjectId` to `%LOCALAPPDATA%\WinTube\secrets.json`, leave both empty to keep the app fully local. The backend is the metube repo's `Backend/` project — see its README to deploy. The backend schema and API endpoints are shared between WinTube and tvOS; no schema migration is needed.

Sync is best-effort: network and auth failures are logged at debug level and never crash the app. A stale or expired session re-authenticates transparently on the next pull. Sign-out keeps the backend copy and stored watch progress by design.

## Skipping sponsors (SponsorBlock)

The player automatically skips community-flagged interruptions — sponsor reads, subscribe reminders, and similar non-content sections — using [SponsorBlock](https://sponsor.ajay.app), a public crowd-sourced database. Built in and always on; no configuration.

This **is NOT YouTube ad blocking**: pre-rolls and mid-rolls never reach the app. Only in-video segments that viewers submit are acted on.

**Privacy:** Your videoId is never sent to SponsorBlock. Only the first 4 hex characters of its SHA-256 go to the server — a 1-in-65,536 slice of all of YouTube — which answers with every video in that slice, and the match happens on your device. The server never learns which video you are watching.

**What gets skipped:** sponsor reads, self-promotion, subscribe reminders, and non-music sections in music videos — the four unambiguously not-the-video categories. Intros, outros, recaps, and filler are editorial parts of the video itself; plenty of people want them, so they're parsed but never skipped. No settings UI.

Each segment is skipped at most once per playback. If you rewind into a skipped stretch, it plays normally — the skip only fires on forward playback. Videos with no submissions play exactly as before. SponsorBlock is best-effort; if the service is down or slow, the video plays with no interruption — the skip fetch fires after playback starts, never blocks the first frame.

## Opening and sharing links

Copy a video's YouTube URL from the player with a single click, or with the current playback position. The copy button is a SplitButton in the title bar: tap to copy as `https://youtu.be/{id}`, or open the chevron for "Copy link at 12:34" to include the position as a `?t=` parameter — useful for jumping to a specific moment when passing around a video.

Open YouTube links in WinTube three ways: paste a URL directly into Search and the player opens immediately; run `WinTube.App.exe <url>` from the command line or any tool; or register the app for `wintube://watch?v=…` URIs so tools and web automation can target it. All three parse the same YouTube link grammar — any `youtube.com`, `youtu.be`, shorts, or music URL, with optional start times in any form (`t=754`, `t=12m34s`, `start=1h2m3s`). The app runs single-instance: a second launch of any kind fronts the existing window and plays the target immediately.

**Honest limitation:** clicking a `youtube.com` link in a browser still opens the browser — Windows has no per-site handler for desktop apps short of becoming the default browser, which this app will not do. What this stage delivers is the three ways in above.

## First run

The one real technical risk the design spec called out (§3, "stage-0 playback gate") — whether
`MediaPlayerElement` plays YouTube's HLS manifest at all — **passed on 2026-08-23**: video and
audio play, seeking and resume work. The ANDROID client's muxed itag-18 fallback (360p) remains
in the ladder for videos the VISIONOS client can't serve; libmpv stays the escape hatch if HLS
ever regresses.

## Project layout

| Path | What's there |
|---|---|
| `src/WinTube.Core/InnerTube` | Client identities, JSON traversal, `InnerTubeClient` POST wrapper, `visitorData` scrape/cache |
| `src/WinTube.Core/Auth` | OAuth device flow, DPAPI token store, `accounts_list`, profile id |
| `src/WinTube.Core/Feed` | Home feed shelf parsing, relative-time formatting |
| `src/WinTube.Core/Search` | Search |
| `src/WinTube.Core/Player` | `StreamService` — the VISIONOS → ANDROID resolve ladder |
| `src/WinTube.Core/Stores` | Watch progress, watch history, video metadata |
| `src/WinTube.App` | WinUI 3 (net8.0-windows, x64, unpackaged) — Views, Session, MainWindow shell |
| `tests/WinTube.Core.Tests` | xUnit — Core only, against captured/synthetic responses with injected HTTP |
| `reference/INNERTUBE.md` | Copied from the metube repo — the shared InnerTube contract |
| `docs/specs/` | Approved design spec (self-contained HTML) |
| `docs/superpowers/plans/` | The implementation plan this repo was built from |

## v1 scope

| Feature | v1 | Notes |
|---|---|---|
| Sign-in (OAuth device flow) | In | URL + code, poll for token. One account. |
| Home feed | In | Shelf rows with sideways paging (continuations). |
| Search | In | One page of results, as on tvOS. |
| Playback with resume | In | HLS via VISIONOS client, ladder fallback, position written every 5 s. |
| History | In | Watch progress + `FEhistory`, folded by the tvOS placement rules. |
| Multiple profiles | Later | Profile id (`sha256(obfuscatedGaiaId)`) is stored from day one so this needs no migration. |
| Appwrite watch-progress sync | **In (stage 2)** | Shares rows with the Apple TV; see the sync section above. |
| SponsorBlock | **In (stage 3)** | Automatic skipping of sponsor reads and similar non-content. See the SponsorBlock section above. |
| Copy YouTube URL | **In (stage 4)** | With or without a timestamp for the current position. |
| Open YouTube links in the app | **In (stage 4)** | Search box, command line, and `wintube://` protocol; single-instance. |
| Preview on hover/focus | Later | |
| Shorts | **In (stage 5)** | Own row with portrait tiles, plays in the ordinary player; strays are moved, never lost. |
| Channels / subscriptions | Later | |
| Comments | Later | |
| News banner | Never | Not wanted on Windows. |
| Top Shelf | Never | No Windows equivalent. |

## Licence

Not affiliated with, endorsed by, or connected to YouTube or Google.
