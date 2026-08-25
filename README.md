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

## Setup

```powershell
mkdir $env:LOCALAPPDATA\WinTube -ea 0
cp secrets.example.json $env:LOCALAPPDATA\WinTube\secrets.json
notepad $env:LOCALAPPDATA\WinTube\secrets.json
```

Fill in the same three values as the tvOS repo's `YouTubeTV/Config/Secrets.xcconfig`: the
public InnerTube web API key, and the YouTube-on-TV OAuth client id and secret. These are not
per-user secrets — they are kept out of the repo the same way the tvOS app keeps them out of
its repo.

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

**Privacy:** Your videoId is never sent to SponsorBlock. The service takes the first 4 hex chars of its SHA-256 hash (the same prefix could match ~65,000 videos), returns every video whose hash starts with them, and the match happens on your device. SponsorBlock learns someone is watching one of those 65,000 videos, not which one.

**What gets skipped:** sponsor reads, self-promotion, subscribe reminders, and non-music sections in music videos — the four unambiguously not-the-video categories. Intros, outros, recaps, and filler are editorial parts of the video itself; plenty of people want them, so they're parsed but never skipped. No settings UI.

Each segment is skipped at most once per playback. If you rewind into a skipped stretch, it plays normally — the skip only fires on forward playback. Videos with no submissions play exactly as before. SponsorBlock is best-effort; if the service is down or slow, the video plays with no interruption — the skip fetch fires after playback starts, never blocks the first frame.

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
| Preview on hover/focus | Later | |
| Shorts | Later | Filtered out entirely in v1. |
| Channels / subscriptions | Later | |
| Comments | Later | |
| Copy YouTube URL | Later | With or without a timestamp for the current position. |
| Open YouTube links in the app | Later | Register for `youtube.com`/`youtu.be` links. |
| News banner | Never | Not wanted on Windows. |
| Top Shelf | Never | No Windows equivalent. |

## Licence

Not affiliated with, endorsed by, or connected to YouTube or Google.
