# WinTube Distribution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Friends install one Setup.exe from GitHub Releases and get automatic updates; releasing is pushing a tag.

**Architecture:** `Secrets.Load` gains embedded defaults for the three public TV-client constants. Velopack bootstraps in `Main` and checks GitHub Releases in the background, surfacing one "Update ready" InfoBar. A tag-triggered GitHub Actions workflow publishes self-contained, packs with `vpk`, and uploads the release.

**Tech Stack:** Velopack (NuGet + `vpk` CLI), GitHub Actions, `gh` CLI. No other new dependencies.

**Spec:** `docs/specs/2026-09-05-wintube-distribution-design.html` (approved 2026-09-05).

## Global Constraints

- Embedded defaults, verbatim: InnerTube API key `AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8`, OAuth client id `861556708454-d6dlm3lh05idd8npek18k6be8ba3oc68.apps.googleusercontent.com`, OAuth client secret `SboVhoG9s0rNafixCSGGKXAT`. These are Google's public YouTube-on-TV app constants (present in every TV firmware and in yt-dlp's source) — the app's identity, not a user's. `secrets.json` overrides FIELD BY FIELD; the Appwrite fields get NO defaults (sync stays personal opt-in).
- Repo: public `TroelsSjostedt/wintube`; pushes stay user-initiated except release tags.
- Update flow: background check after the window is up; silent download; "Update ready — restart for vX.Y.Z" InfoBar with a Restart button; ignored updates apply on a later launch; every failure silent (debug log only); a non-Velopack (dev) run checks nothing.
- Release = `git tag vX.Y.Z && git push origin vX.Y.Z`; CI publishes self-contained (`-r win-x64 --self-contained -p:WindowsAppSDKSelfContained=true`) so friends pre-install nothing.
- Tests: `dotnet test tests/WinTube.Core.Tests` (196 green before this plan). App build: `dotnet build src/WinTube.App -p:Platform=x64`; MSB3027-only failure → `Get-Process WinTube.App | Stop-Process`, retry, touch nothing else.
- Branch `master`; commit per task with the given message.

## File Structure

```
src/WinTube.Core/Secrets.cs                MODIFY: embedded defaults
src/WinTube.App/WinTube.App.csproj         MODIFY: Velopack package reference
src/WinTube.App/Program.cs                 MODIFY: VelopackApp bootstrap
src/WinTube.App/UpdateService.cs           CREATE: check/download/apply
src/WinTube.App/MainWindow.xaml(.cs)       MODIFY: update InfoBar + restart
.github/workflows/release.yml              CREATE
README.md                                  MODIFY: Install section; setup now optional
tests/WinTube.Core.Tests/SecretsTests.cs   CREATE (or extend if one exists)
```

---

### Task 1: Core — embedded default constants

**Files:**
- Modify: `src/WinTube.Core/Secrets.cs`
- Test: `tests/WinTube.Core.Tests/SecretsTests.cs` (create; check no Secrets tests exist elsewhere first)

**Interfaces:**
- Produces: `Secrets.Load` semantics change only — absent/empty fields fall back to `Secrets.Defaults` (a `public static Secrets Defaults` with the three constants and empty Appwrite fields). Signature unchanged.

- [ ] **Step 1: Write the failing tests**

New `tests/WinTube.Core.Tests/SecretsTests.cs`:

```csharp
using WinTube.Core;

namespace WinTube.Core.Tests;

public class SecretsTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), "wintube-tests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(directory, "secrets.json");

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void MissingFile_YieldsTheEmbeddedDefaults()
    {
        var secrets = Secrets.Load(FilePath);
        Assert.Equal("AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8", secrets.InnerTubeApiKey);
        Assert.Equal("861556708454-d6dlm3lh05idd8npek18k6be8ba3oc68.apps.googleusercontent.com",
            secrets.OAuthClientId);
        Assert.Equal("SboVhoG9s0rNafixCSGGKXAT", secrets.OAuthClientSecret);
        Assert.Equal("", secrets.AppwriteHost);      // sync stays opt-in
        Assert.Equal("", secrets.AppwriteProjectId);
    }

    [Fact]
    public void PartialFile_OverridesOnlyItsOwnFields()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, "{\"innerTubeApiKey\":\"MY_KEY\",\"appwriteHost\":\"my.host\"}");
        var secrets = Secrets.Load(FilePath);
        Assert.Equal("MY_KEY", secrets.InnerTubeApiKey);
        Assert.Equal(Secrets.Defaults.OAuthClientId, secrets.OAuthClientId);
        Assert.Equal(Secrets.Defaults.OAuthClientSecret, secrets.OAuthClientSecret);
        Assert.Equal("my.host", secrets.AppwriteHost);
    }

    [Fact]
    public void EmptyFieldInFile_FallsBackToTheDefault()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, "{\"oauthClientId\":\"\"}");
        Assert.Equal(Secrets.Defaults.OAuthClientId, Secrets.Load(FilePath).OAuthClientId);
    }

    [Fact]
    public void FullFile_WinsOnEveryField()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, "{\"innerTubeApiKey\":\"K\",\"oauthClientId\":\"I\"," +
            "\"oauthClientSecret\":\"S\",\"appwriteHost\":\"H\",\"appwriteProjectId\":\"P\"}");
        var secrets = Secrets.Load(FilePath);
        Assert.Equal(("K", "I", "S", "H", "P"),
            (secrets.InnerTubeApiKey, secrets.OAuthClientId, secrets.OAuthClientSecret,
             secrets.AppwriteHost, secrets.AppwriteProjectId));
    }

    [Fact]
    public void CorruptFile_YieldsTheDefaults()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, "{not json");
        Assert.Equal(Secrets.Defaults.InnerTubeApiKey, Secrets.Load(FilePath).InnerTubeApiKey);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/WinTube.Core.Tests`
Expected: compile FAILURE (`Secrets.Defaults` missing) — and note the old behavior returned
empty strings, which several of these assert against.

- [ ] **Step 3: Implement**

Rework `Secrets.cs` (keep the record shape and `DefaultPath`):

```csharp
    /// Google's own public YouTube-on-TV app constants — the same values baked into every
    /// TV firmware and printed in yt-dlp's source. They identify the app, not a user; the
    /// per-user secrets (OAuth tokens) are DPAPI-stored and never ship. secrets.json can
    /// still override any field, and the Appwrite fields deliberately have no defaults:
    /// sync is a personal opt-in.
    public static Secrets Defaults { get; } = new(
        InnerTubeApiKey: "AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8",
        OAuthClientId: "861556708454-d6dlm3lh05idd8npek18k6be8ba3oc68.apps.googleusercontent.com",
        OAuthClientSecret: "SboVhoG9s0rNafixCSGGKXAT");

    public static Secrets Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            string Get(string name) =>
                doc.RootElement.TryGetProperty(name, out var el) ? el.GetString() ?? "" : "";
            string Or(string value, string fallback) => value.Length > 0 ? value : fallback;
            return new Secrets(
                Or(Get("innerTubeApiKey"), Defaults.InnerTubeApiKey),
                Or(Get("oauthClientId"), Defaults.OAuthClientId),
                Or(Get("oauthClientSecret"), Defaults.OAuthClientSecret))
            {
                AppwriteHost = Get("appwriteHost"),
                AppwriteProjectId = Get("appwriteProjectId")
            };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return Defaults;
        }
    }
```

Update the class doc comment: constants ship embedded; secrets.json is optional overrides.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/WinTube.Core.Tests`
Expected: PASS (196 + 5 new). If any existing test constructed Secrets expecting
empty-on-missing, adjust it to the new contract.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: embed the public TV-client constants as defaults"
```

---

### Task 2: App — Velopack bootstrap and update flow

**Files:**
- Modify: `src/WinTube.App/WinTube.App.csproj` (PackageReference `Velopack`, latest stable)
- Modify: `src/WinTube.App/Program.cs`
- Create: `src/WinTube.App/UpdateService.cs`
- Modify: `src/WinTube.App/MainWindow.xaml`, `src/WinTube.App/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: `UpdateService.CheckAsync()` → `Task<string?>` (the ready version string, or null);
  `UpdateService.ApplyAndRestart()`; wired only inside MainWindow.

- [ ] **Step 1: Bootstrap in Main**

`dotnet add src/WinTube.App package Velopack`. In `Program.Main`, the FIRST statement
(before ComWrappers init):

```csharp
        Velopack.VelopackApp.Build().Run();
```

- [ ] **Step 2: UpdateService**

New `src/WinTube.App/UpdateService.cs`:

```csharp
using Velopack;
using Velopack.Sources;

namespace WinTube.App;

/// Background self-update against the public GitHub releases. A dev run (not installed via
/// Velopack) reports IsInstalled false and the whole thing quietly does nothing; so does any
/// failure — an update check must never delay or break startup.
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/TroelsSjostedt/wintube";

    private readonly UpdateManager manager = new(new GithubSource(RepoUrl, null, false));
    private UpdateInfo? pending;

    /// Checks and silently downloads. Returns the version string ready to apply, or null.
    public async Task<string?> CheckAsync()
    {
        try
        {
            if (!manager.IsInstalled) return null;
            pending = await manager.CheckForUpdatesAsync();
            if (pending is null) return null;
            await manager.DownloadUpdatesAsync(pending);
            return pending.TargetFullRelease.Version.ToString();
        }
        catch (Exception e)
        {
            WinTube.Core.Sync.WatchProgressSync.LogTo(Session.DataDirectory,
                $"update check failed: {e.Message}");
            return null;
        }
    }

    /// Applies the downloaded update and restarts the app. No-op without a pending update.
    public void ApplyAndRestart()
    {
        if (pending is null) return;
        manager.ApplyUpdatesAndRestart(pending);
    }
}
```

NOTE: verify the exact Velopack API names against the installed package version
(`UpdateManager.IsInstalled`, `CheckForUpdatesAsync`, `DownloadUpdatesAsync`,
`ApplyUpdatesAndRestart`, `UpdateInfo.TargetFullRelease.Version`) and adapt — the package
evolves. Velopack also auto-applies a downloaded-but-not-applied update on the NEXT launch
by itself; rely on that for the "ignored toast" path rather than adding exit hooks.

- [ ] **Step 3: The InfoBar**

`MainWindow.xaml`: inside the root layout, over/below the NavigationView content, add:

```xml
<InfoBar x:Name="UpdateBar" IsOpen="False" Severity="Success"
         VerticalAlignment="Bottom" Margin="16">
    <InfoBar.ActionButton>
        <Button Content="Restart" Click="OnRestartForUpdate"/>
    </InfoBar.ActionButton>
</InfoBar>
```

(place it so it never overlaps the player's transport controls — bottom of the shell is fine;
match the file's existing layout structure).

`MainWindow.xaml.cs`:

```csharp
    private readonly UpdateService updates = new();

    // called from the constructor after InitializeComponent:
    private async void CheckForUpdatesAsync()
    {
        var version = await updates.CheckAsync();
        if (version is null) return;
        UpdateBar.Message = $"Update ready — restart for v{version}";
        UpdateBar.IsOpen = true;
    }

    private void OnRestartForUpdate(object sender, RoutedEventArgs e) => updates.ApplyAndRestart();
```

- [ ] **Step 4: Build, tests, commit**

Run: `dotnet test tests/WinTube.Core.Tests` (green) and
`dotnet build src/WinTube.App -p:Platform=x64` (clean). A dev launch must behave exactly as
before (no bar, no delay).

```bash
git add -A
git commit -m "feat: Velopack self-update from GitHub releases"
```

---

### Task 3: Repo, CI pipeline, README, and the end-to-end release

This task is the controller's (side effects: repo creation, pushes, releases) — dispatch
nothing; the steps document what the controller does.

**Files:**
- Create: `.github/workflows/release.yml`
- Modify: `README.md`

- [ ] **Step 1: Create the repo and push**

```bash
gh repo create TroelsSjostedt/wintube --public --source . --remote origin \
  --description "A YouTube client for Windows, built for one viewer" --push
```

- [ ] **Step 2: The workflow**

`.github/workflows/release.yml`:

```yaml
name: Release
on:
  push:
    tags: ["v*"]
permissions:
  contents: write
jobs:
  release:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 8.0.x
      - name: Test
        run: dotnet test tests/WinTube.Core.Tests
      - name: Publish
        run: >
          dotnet publish src/WinTube.App -c Release -p:Platform=x64 -r win-x64
          --self-contained -p:WindowsAppSDKSelfContained=true -o publish
      - name: Version from tag
        shell: pwsh
        run: '"VERSION=$($env:GITHUB_REF_NAME.TrimStart(''v''))" >> $env:GITHUB_ENV'
      - name: Install vpk
        run: dotnet tool install -g vpk
      - name: Download previous releases (delta chain)
        run: vpk download github --repoUrl https://github.com/TroelsSjostedt/wintube
        continue-on-error: true
      - name: Pack
        run: >
          vpk pack -u WinTube -v ${{ env.VERSION }} -p publish
          -e WinTube.App.exe --packTitle WinTube
      - name: Upload release
        run: >
          vpk upload github --repoUrl https://github.com/TroelsSjostedt/wintube
          --publish --releaseName "WinTube v${{ env.VERSION }}" --tag ${{ github.ref_name }}
          --token ${{ secrets.GITHUB_TOKEN }}
```

Verify flag names against the installed vpk version's `--help` before committing; adapt.

- [ ] **Step 3: README**

Add an **Install** section above "Setup": download the latest `*-Setup.exe` from
`https://github.com/TroelsSjostedt/wintube/releases/latest`, SmartScreen "More info → Run
anyway" (unsigned, personal project), per-user install, updates arrive automatically.
Rewrite "Setup" as "Optional overrides": the app ships with the public TV-client constants
embedded; `secrets.json` overrides them field by field and is where the optional Appwrite
sync fields go.

- [ ] **Step 4: Local smoke before the first tag**

On this machine: run the Publish command from the workflow, then
`dotnet tool install -g vpk` and the Pack command with `-v 0.0.1`, and verify
`Releases\WinTube-win-Setup.exe` exists and installs/launches (it shares
`%LOCALAPPDATA%\WinTube` with the dev copy — expected). Fix publish/pack issues here where
iteration is cheap, then commit the workflow + README:

```bash
git add -A
git commit -m "feat: release pipeline and install docs"
git push origin master
```

- [ ] **Step 5: End-to-end**

```bash
git tag v0.9.0
git push origin v0.9.0
```

Watch the Actions run to green; install from the GitHub release; then make a trivial bump,
tag `v0.9.1`, push, and verify the installed copy shows "Update ready — restart for v0.9.1"
and restarts into it.
