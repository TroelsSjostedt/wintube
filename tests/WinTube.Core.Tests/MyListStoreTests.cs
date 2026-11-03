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
    public void Add_Contains_Remove_RoundTrip()
    {
        var store = Make(Now);
        store.Activate("p1");
        Assert.False(store.Contains("a"));
        store.Add(Card("a"));
        Assert.True(store.Contains("a"));
        store.Remove("a");
        Assert.False(store.Contains("a"));
        Assert.Empty(store.Items);
    }

    [Fact]
    public void Items_NewestFirst_WithFullCards()
    {
        var time = Now;
        var store = new MyListStore(root, () => time);
        store.Activate("p1");
        store.Add(Card("old"));
        time = Now.AddMinutes(1);
        store.Add(Card("new"));
        Assert.Equal(["new", "old"], store.Items.Select(v => v.Id));
        Assert.Equal("T:new", store.Items[0].Title);
        Assert.Equal("A", store.Items[0].Author);
    }

    [Fact]
    public void Add_SameVideoAgain_DoesNotDuplicate_OrReorder()
    {
        var time = Now;
        var store = new MyListStore(root, () => time);
        store.Activate("p1");
        store.Add(Card("a"));
        time = Now.AddMinutes(1);
        store.Add(Card("b"));
        time = Now.AddMinutes(2);
        store.Add(Card("a"));
        Assert.Equal(["b", "a"], store.Items.Select(v => v.Id));
    }

    [Fact]
    public void Persists_AcrossInstances_PerProfile()
    {
        var store = Make(Now);
        store.Activate("p1");
        store.Add(Card("a"));

        var reloaded = Make(Now);
        reloaded.Activate("p1");
        Assert.True(reloaded.Contains("a"));

        reloaded.Activate("p2");
        Assert.False(reloaded.Contains("a"));
    }

    [Fact]
    public void Changed_RaisedOnAddAndRemove_NotOnNoOps()
    {
        var store = Make(Now);
        store.Activate("p1");
        var raised = 0;
        store.Changed += () => raised++;
        store.Add(Card("a"));
        Assert.Equal(1, raised);
        store.Add(Card("a"));   // already there, same card
        Assert.Equal(1, raised);
        store.Remove("missing");
        Assert.Equal(1, raised);
        store.Remove("a");
        Assert.Equal(2, raised);
    }

    [Fact]
    public void NoProfile_IsInertNotThrowing()
    {
        var store = Make(Now);
        store.Add(Card("a"));
        store.Remove("a");
        Assert.False(store.Contains("a"));
        Assert.Empty(store.Items);
    }
}
