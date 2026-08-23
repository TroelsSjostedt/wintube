using WinTube.Core.Stores;

namespace WinTube.Core.Tests;

public class WatchProgressStoreSyncTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private static WatchProgressStore Make(out string dir)
    {
        dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())).FullName;
        var store = new WatchProgressStore(dir, () => T0);
        store.Activate("p1");
        return store;
    }

    [Fact]
    public void Merge_NewerRemoteWins_AndIsNotDirty()
    {
        var store = Make(out _);
        store.Report("v1", 100, 600);                          // local, dirty, UpdatedAt = T0
        store.Merge(new Dictionary<string, ProgressEntry>
        {
            ["v1"] = new(200, 600, T0.AddMinutes(5)),          // newer remote
            ["v2"] = new(50, 300, T0.AddMinutes(-5)),          // new video
        });
        Assert.Equal(200, store.Entries["v1"].PositionSeconds);
        Assert.Equal(50, store.Entries["v2"].PositionSeconds);
        Assert.Contains("v1", store.Dirty);                    // local edit still queued
        Assert.DoesNotContain("v2", store.Dirty);              // merged, never dirty
    }

    [Fact]
    public void Merge_OlderRemoteLoses()
    {
        var store = Make(out _);
        store.Report("v1", 100, 600);
        store.Merge(new Dictionary<string, ProgressEntry>
        {
            ["v1"] = new(30, 600, T0.AddMinutes(-10)),
        });
        Assert.Equal(100, store.Entries["v1"].PositionSeconds);
    }

    [Fact]
    public void Merge_RaisesChangedButNeverLocalChanged()
    {
        var store = Make(out _);
        int changed = 0, local = 0;
        store.Changed += () => changed++;
        store.LocalChanged += () => local++;
        store.Merge(new Dictionary<string, ProgressEntry>
        {
            ["v1"] = new(50, 300, T0),
        });
        Assert.Equal(1, changed);
        Assert.Equal(0, local);
    }

    [Fact]
    public void Report_RaisesLocalChanged()
    {
        var store = Make(out _);
        var local = 0;
        store.LocalChanged += () => local++;
        store.Report("v1", 100, 600);
        Assert.Equal(1, local);
    }

    [Fact]
    public void QueueAll_QueuesEverythingExceptMerged()
    {
        var store = Make(out _);
        store.Report("v1", 100, 600);
        store.Merge(new Dictionary<string, ProgressEntry>
        {
            ["v2"] = new(50, 300, T0),
            ["v3"] = new(60, 300, T0),
        });
        store.QueueAll(new HashSet<string> { "v2", "v3" });
        Assert.Equal(new[] { "v1" }, store.Dirty.OrderBy(x => x));

        store.QueueAll(new HashSet<string>());                 // nothing excepted now
        Assert.Equal(new[] { "v1", "v2", "v3" }, store.Dirty.OrderBy(x => x));
    }

    [Fact]
    public void MarkSynced_ClearsOnlyUnchangedEntries_AndPersists()
    {
        var store = Make(out var dir);
        store.Report("v1", 100, 600);
        var pushedAt = store.Entries["v1"].UpdatedAt;
        store.Report("v2", 100, 600);

        store.MarkSynced(new Dictionary<string, DateTimeOffset>
        {
            ["v1"] = pushedAt,
            ["v2"] = pushedAt.AddMinutes(-1),                  // stale push — moved on since
        });
        Assert.DoesNotContain("v1", store.Dirty);
        Assert.Contains("v2", store.Dirty);

        var reloaded = new WatchProgressStore(dir, () => T0);
        reloaded.Activate("p1");
        Assert.DoesNotContain("v1", reloaded.Dirty);
    }
}
