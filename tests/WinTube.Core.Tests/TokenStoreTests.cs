using System.Runtime.Versioning;
using WinTube.Core.Auth;

namespace WinTube.Core.Tests;

[SupportedOSPlatform("windows")]
public class TokenStoreTests
{
    private static string TempDir() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())).FullName;

    [Fact]
    public void SaveLoadDelete_RoundTrips()
    {
        var dir = TempDir();
        var store = new TokenStore(dir);
        var profile = new StoredProfile("pid", "Name", "https://a/img", "AT", "RT");
        store.Save(profile);

        // On disk it must not be plaintext JSON.
        var raw = File.ReadAllBytes(Path.Combine(dir, "tokens.bin"));
        Assert.DoesNotContain("accessToken", System.Text.Encoding.UTF8.GetString(raw));

        Assert.Equal(profile, new TokenStore(dir).Load());
        store.Delete();
        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_MissingOrCorrupt_ReturnsNull()
    {
        var dir = TempDir();
        Assert.Null(new TokenStore(dir).Load());
        File.WriteAllText(Path.Combine(dir, "tokens.bin"), "garbage");
        Assert.Null(new TokenStore(dir).Load());
    }

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
}
