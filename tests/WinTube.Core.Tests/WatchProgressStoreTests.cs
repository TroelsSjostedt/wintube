using WinTube.Core.Stores;

namespace WinTube.Core.Tests;

public class WatchProgressStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private static string TempDir() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())).FullName;

    private static WatchProgressStore Make(string dir, string? profile = "p1") =>
        Activate(new WatchProgressStore(dir, () => T0), profile);

    private static WatchProgressStore Activate(WatchProgressStore store, string? profile)
    {
        store.Activate(profile);
        return store;
    }

    [Fact]
    public void Report_RecordsAndPersistsAcrossInstances()
    {
        var dir = TempDir();
        Make(dir).Report("v1", 120, 600);

        var reloaded = Make(dir);
        var entry = reloaded.Entries["v1"];
        Assert.Equal(120, entry.PositionSeconds);
        Assert.Equal(600, entry.DurationSeconds);
        Assert.Equal(120, reloaded.ResumePosition("v1"));
        Assert.Contains("v1", reloaded.Dirty);
    }

    [Fact]
    public void Report_UnderTenSeconds_IsIgnored()
    {
        var store = Make(TempDir());
        store.Report("v1", 9, 600);
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void Report_NearEnd_PinsToDuration_AndResumeStartsOver()
    {
        var store = Make(TempDir());
        store.Report("v1", 585, 600);   // within 20 s of the end
        Assert.Equal(600, store.Entries["v1"].PositionSeconds);
        Assert.Null(store.ResumePosition("v1"));
    }

    [Fact]
    public void Entries_AreKeyedPerProfile()
    {
        var dir = TempDir();
        var store = Make(dir, "p1");
        store.Report("v1", 60, 600);
        store.Activate("p2");
        Assert.Empty(store.Entries);
        store.Activate("p1");
        Assert.Single(store.Entries);
        store.Activate(null);
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void Report_RaisesChanged()
    {
        var store = Make(TempDir());
        var raised = 0;
        store.Changed += () => raised++;
        store.Report("v1", 60, 600);
        Assert.Equal(1, raised);
    }
}
