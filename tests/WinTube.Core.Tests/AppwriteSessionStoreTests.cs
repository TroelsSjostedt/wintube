using System.Runtime.Versioning;
using WinTube.Core.Sync;

namespace WinTube.Core.Tests;

[SupportedOSPlatform("windows")]
public class AppwriteSessionStoreTests
{
    private static string TempDir() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())).FullName;

    [Fact]
    public void SaveLoadDelete_RoundTripsPerProfile()
    {
        var dir = TempDir();
        var store = new AppwriteSessionStore(dir);
        var session = new AppwriteSession("yt1234", "a_session_metube=abc");
        store.Save("p1", session);

        Assert.Equal(session, new AppwriteSessionStore(dir).Load("p1"));
        Assert.Null(store.Load("p2"));

        var raw = File.ReadAllBytes(Path.Combine(dir, "profiles", "p1", "appwrite-session.bin"));
        Assert.DoesNotContain("a_session", System.Text.Encoding.UTF8.GetString(raw));

        store.Delete("p1");
        Assert.Null(store.Load("p1"));
    }

    [Fact]
    public void Load_MissingOrCorrupt_ReturnsNull()
    {
        var dir = TempDir();
        var store = new AppwriteSessionStore(dir);
        Assert.Null(store.Load("p1"));
        Directory.CreateDirectory(Path.Combine(dir, "profiles", "p1"));
        File.WriteAllText(Path.Combine(dir, "profiles", "p1", "appwrite-session.bin"), "junk");
        Assert.Null(store.Load("p1"));
    }
}
