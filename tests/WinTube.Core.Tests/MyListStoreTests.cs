using WinTube.Core.Models;
using WinTube.Core.Stores;

namespace WinTube.Core.Tests;

public sealed class MyListStoreTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "wintube-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    private static VideoItem Card(string id) => new() { Id = id, Title = "T:" + id, Author = "A" };

    private MyListStore Make(DateTimeOffset now) => new(root, () => now);

    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Touch_Contains_Remove_RoundTrip()
    {
        var store = Make(Now);
        store.Activate("p1");
        Assert.False(store.Contains("a"));
        store.Touch(Card("a"));
        Assert.True(store.Contains("a"));
        store.Remove("a");
        Assert.False(store.Contains("a"));
        Assert.Empty(store.Items);
    }

    [Fact]
    public void Items_LatestTouchedFirst_WithFullCards()
    {
        var time = Now;
        var store = new MyListStore(root, () => time);
        store.Activate("p1");
        store.Touch(Card("old"));
        time = Now.AddMinutes(1);
        store.Touch(Card("new"));
        Assert.Equal(["new", "old"], store.Items.Select(v => v.Id));
        Assert.Equal("T:new", store.Items[0].Title);
        Assert.Equal("A", store.Items[0].Author);
    }

    [Fact]
    public void Touch_SameVideoAgain_BumpsToFront_WithoutDuplicating()
    {
        var time = Now;
        var store = new MyListStore(root, () => time);
        store.Activate("p1");
        store.Touch(Card("a"));
        time = Now.AddMinutes(1);
        store.Touch(Card("b"));
        time = Now.AddMinutes(2);
        store.Touch(Card("a"));
        Assert.Equal(["a", "b"], store.Items.Select(v => v.Id));
    }

    [Fact]
    public void Persists_AcrossInstances_PerProfile()
    {
        var store = Make(Now);
        store.Activate("p1");
        store.Touch(Card("a"));

        var reloaded = Make(Now);
        reloaded.Activate("p1");
        Assert.True(reloaded.Contains("a"));

        reloaded.Activate("p2");
        Assert.False(reloaded.Contains("a"));
    }

    [Fact]
    public void Changed_RaisedOnTouchAndRemove_NotOnNoOps()
    {
        var store = Make(Now);
        store.Activate("p1");
        var raised = 0;
        store.Changed += () => raised++;
        store.Touch(Card("a"));
        Assert.Equal(1, raised);
        store.Touch(Card("a"));   // same card, same clock instant — nothing changed
        Assert.Equal(1, raised);
        store.Remove("missing");
        Assert.Equal(1, raised);
        store.Remove("a");
        Assert.Equal(2, raised);
    }

    [Fact]
    public void Seed_AddsOnlyWhenAbsent_AtTheGivenTime_WithoutRaisingChanged()
    {
        var store = Make(Now);
        store.Activate("p1");
        var raised = 0;
        store.Changed += () => raised++;
        store.Touch(Card("a"));
        raised = 0;

        store.Seed(Card("a") with { Title = "overwritten?" }, Now.AddMinutes(5));
        store.Seed(Card("b"), Now.AddMinutes(-5));

        Assert.Equal(0, raised);
        Assert.Equal("T:a", store.Items.First(v => v.Id == "a").Title);   // existing entry kept
        Assert.Equal(["a", "b"], store.Items.Select(v => v.Id));          // b seeded older

        var reloaded = Make(Now);
        reloaded.Activate("p1");
        Assert.True(reloaded.Contains("b"));   // seeding persists
    }

    [Fact]
    public void Visible_FiltersFinishedVideos_KeepsUnstartedAndPartial()
    {
        VideoItem[] items = [Card("unstarted"), Card("partial"), Card("finished"), Card("noDuration")];
        var progress = new Dictionary<string, ProgressEntry>
        {
            ["partial"] = new(100, 1000, Now),
            ["finished"] = new(960, 1000, Now),
            ["noDuration"] = new(10, 0, Now),
        };

        Assert.Equal(["unstarted", "partial", "noDuration"],
            MyListStore.Visible(items, progress).Select(v => v.Id));
    }

    [Fact]
    public void NoProfile_IsInertNotThrowing()
    {
        var store = Make(Now);
        store.Touch(Card("a"));
        store.Seed(Card("b"), Now);
        store.Remove("a");
        Assert.False(store.Contains("a"));
        Assert.Empty(store.Items);
    }
}
