using WinTube.Core.Models;
using WinTube.Core.Stores;

namespace WinTube.Core.Tests;

public class WatchHistoryStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private static VideoItem Video(string id, string title = "Title") =>
        new() { Id = id, Title = title, Author = "Chan", Duration = "10:00" };

    private static WatchHistoryStore Make(string dir)
    {
        var store = new WatchHistoryStore(dir, () => T0);
        store.Activate("p1");
        return store;
    }

    private static string TempDir() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())).FullName;

    [Fact]
    public void Record_MarksWatchedHereAndPersists()
    {
        var dir = TempDir();
        Make(dir).Record(Video("v1"));

        var reloaded = Make(dir);
        Assert.Equal(("v1", T0), reloaded.WatchedHere.Single());
        Assert.Equal("Title", reloaded.Card("v1")!.Title);
    }

    [Fact]
    public void Record_BlankTitle_KeepsStoredCard()
    {
        var store = Make(TempDir());
        store.Remember([Video("v1", "Real Title")]);
        store.Record(Video("v1", ""));
        Assert.Equal("Real Title", store.Card("v1")!.Title);
        Assert.Single(store.WatchedHere);
    }

    [Fact]
    public void Remember_DoesNotClaimWatched()
    {
        var store = Make(TempDir());
        store.Remember([Video("v1")]);
        Assert.Empty(store.WatchedHere);
        Assert.NotNull(store.Card("v1"));
    }

    [Fact]
    public void Prune_KeepsWatchedHereAndNamedIds()
    {
        var store = Make(TempDir());
        store.Record(Video("played"));
        store.Remember([Video("kept"), Video("dropped")]);
        store.Prune(new HashSet<string> { "kept" });
        Assert.NotNull(store.Card("played"));
        Assert.NotNull(store.Card("kept"));
        Assert.Null(store.Card("dropped"));
    }
}
