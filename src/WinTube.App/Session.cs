using WinTube.Core;
using WinTube.Core.Auth;
using WinTube.Core.Feed;
using WinTube.Core.InnerTube;
using WinTube.Core.Player;
using WinTube.Core.Search;
using WinTube.Core.Stores;
using WinTube.Core.Sync;

namespace WinTube.App;

/// Owns the signed-in state: the stored profile, a fresh access token, and retry-once-on-401.
/// Built once in App; pages reach it via App.Session.
public sealed class Session
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinTube");

    private readonly TokenStore tokenStore = new(DataDirectory);
    private readonly AppwriteSessionStore? appwriteSessions;
    private string? accessToken;

    public InnerTubeClient InnerTube { get; }
    public DeviceAuthService DeviceAuth { get; }
    public AccountService Accounts { get; }
    public FeedService Feed { get; }
    public SearchService Search { get; }
    public StreamService Streams { get; }
    public VideoMetadataService Metadata { get; }
    public WatchProgressStore Progress { get; } = new(DataDirectory);
    public WatchHistoryStore History { get; } = new(DataDirectory);
    public WatchProgressSync? ProgressSync { get; }

    public StoredProfile? Profile { get; private set; }
    public bool IsSignedIn => Profile is not null;

    /// Raised at the end of SignOut(), whether triggered by the user or by RunAsync giving up
    /// on a permanently failed refresh. Continuations in this class resume on the UI thread, so
    /// subscribers don't need to marshal back themselves.
    public event Action? SignedOut;

    public Session(HttpClient http, Secrets secrets)
    {
        InnerTube = new InnerTubeClient(http, secrets);
        DeviceAuth = new DeviceAuthService(http, secrets);
        Accounts = new AccountService(InnerTube);
        Feed = new FeedService(InnerTube);
        Search = new SearchService(InnerTube);
        var visitorData = new VisitorDataStore(http);
        Streams = new StreamService(InnerTube, visitorData);
        Metadata = new VideoMetadataService(InnerTube, visitorData);

        var appwrite = AppwriteConfig.FromSecrets(secrets);
        if (appwrite is not null)
        {
            appwriteSessions = new AppwriteSessionStore(DataDirectory);
            ProgressSync = new WatchProgressSync(
                Progress, new AppwriteClient(http, appwrite), appwriteSessions, DataDirectory);
            WatchProgressSync.LogTo(DataDirectory, $"sync on: endpoint={appwrite.Endpoint}, project={appwrite.ProjectId}");
        }
        else
        {
            WatchProgressSync.LogTo(DataDirectory,
                "sync off: appwriteHost/appwriteProjectId not set in secrets.json");
        }

        Profile = tokenStore.Load();
        accessToken = Profile?.AccessToken;
        ActivateStores();
        ActivateSync();

        _ = BackfillAccountKeyAsync();
    }

    /// Runs an authenticated call; on 401 refreshes the token once and retries. A refresh
    /// that itself fails signs the profile out (stored progress stays on disk).
    public async Task<T> RunAsync<T>(Func<string, Task<T>> call)
    {
        var profile = Profile ?? throw new InvalidOperationException("Not signed in");
        try
        {
            return await call(accessToken ?? profile.AccessToken);
        }
        catch (InnerTubeException e) when (e.StatusCode == 401)
        {
            OAuthTokens fresh;
            try { fresh = await DeviceAuth.RefreshAsync(profile.RefreshToken); }
            catch (DeviceAuthException ex)
            {
                if (!ex.IsTransient) SignOut();
                throw;
            }
            accessToken = fresh.AccessToken;
            Profile = profile with
            {
                AccessToken = fresh.AccessToken,
                RefreshToken = fresh.RefreshToken ?? profile.RefreshToken,
            };
            tokenStore.Save(Profile);
            ActivateSync();
            return await call(accessToken);
        }
    }

    /// Completes sign-in after the device flow: identify the account, derive the profile id,
    /// persist, and point the stores at the profile.
    public async Task CompleteSignInAsync(OAuthTokens tokens)
    {
        var account = await Accounts.LoadAsync(tokens.AccessToken);
        Profile = new StoredProfile(
            ProfileId.From(account.Key), account.Name, account.AvatarUrl,
            tokens.AccessToken, tokens.RefreshToken ?? "", account.Key);
        accessToken = tokens.AccessToken;
        tokenStore.Save(Profile);
        ActivateStores();
        ActivateSync();
    }

    public void SignOut()
    {
        ProgressSync?.Activate(null, null, null);
        if (Profile is { } profile) appwriteSessions?.Delete(profile.ProfileId[..16]);
        tokenStore.Delete();
        Profile = null;
        accessToken = null;
        ActivateStores();
        SignedOut?.Invoke();
    }

    private void ActivateStores()
    {
        Progress.Activate(Profile?.ProfileId);
        History.Activate(Profile?.ProfileId);
    }

    private void ActivateSync() =>
        ProgressSync?.Activate(Profile?.ProfileId, Profile?.AccountKey, accessToken ?? Profile?.AccessToken);

    /// Fills in AccountKey on profiles stored before stage 2 so sync can turn on without a
    /// fresh sign-in. Fire-and-forget from the constructor; every failure is swallowed —
    /// sync simply stays off until the next launch retries this.
    private async Task BackfillAccountKeyAsync()
    {
        try
        {
            if (Profile is not { AccountKey: null }) return;
            WatchProgressSync.LogTo(DataDirectory, "backfilling account key via accounts_list");
            var captured = Profile;
            var info = await RunAsync(t => Accounts.LoadAsync(t));
            if (!ReferenceEquals(Profile, captured)) return;
            Profile = Profile with { AccountKey = info.Key };
            tokenStore.Save(Profile);
            WatchProgressSync.LogTo(DataDirectory, "account key backfilled; re-activating sync");
            ActivateSync();
        }
        catch (Exception e)
        {
            // Swallowed: sync stays off until next launch. Logged, or a dead backfill is
            // indistinguishable from a quiet one.
            WatchProgressSync.LogTo(DataDirectory, $"account-key backfill failed: {e.Message}");
        }
    }
}
