using WinTube.Core.Stores;

namespace WinTube.Core.Tests;

public sealed class ContinueWatchingDismissalsTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "wintube-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Dismiss_Records_Persists_AndRaisesChanged()
    {
        var store = new ContinueWatchingDismissals(root, () => Now);
        store.Activate("p1");
        var raised = 0;
        store.Changed += () => raised++;

        store.Dismiss("a");
        Assert.Equal(Now, store.Entries["a"]);
        Assert.Equal(1, raised);

        var reloaded = new ContinueWatchingDismissals(root, () => Now);
        reloaded.Activate("p1");
        Assert.Equal(Now, reloaded.Entries["a"]);

        reloaded.Activate("p2");
        Assert.Empty(reloaded.Entries);
    }

    [Fact]
    public void NoProfile_IsInertNotThrowing()
    {
        var store = new ContinueWatchingDismissals(root, () => Now);
        store.Dismiss("a");
        Assert.Empty(store.Entries);
    }
}
