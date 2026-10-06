using WinTube.Core.Models;
using WinTube.Core.Stores;

namespace WinTube.Core.Tests;

public class ContinueWatchingBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset T(int minutesAgo) => Now.AddMinutes(-minutesAgo);

    private static VideoItem Card(string id, bool isShort = false) =>
        new() { Id = id, Title = id, IsShort = isShort };

    private static Func<string, VideoItem?> AllCards => id => Card(id);

    private static IReadOnlyDictionary<string, ProgressEntry> Progress(
        params (string Id, double Position, double Duration)[] entries) =>
        entries.ToDictionary(e => e.Id, e => new ProgressEntry(e.Position, e.Duration, Now));

    [Fact]
    public void Build_OrdersByWatchedAtDescending_WithFractions()
    {
        var result = ContinueWatchingBuilder.Build(
            watched: [("old", T(60)), ("new", T(5)), ("mid", T(30))],
            progress: Progress(("old", 100, 400), ("new", 300, 600), ("mid", 50, 200)),
            card: AllCards);
        Assert.Equal(["new", "mid", "old"], result.Select(r => r.Video.Id));
        Assert.Equal([0.5, 0.25, 0.25], result.Select(r => r.Fraction));
    }

    [Fact]
    public void Build_KeepsOnlyFractionsStrictlyBetweenZeroAndNinetyFivePercent()
    {
        var result = ContinueWatchingBuilder.Build(
            watched: [("zero", T(1)), ("low", T(2)), ("edge", T(3)), ("high", T(4)), ("done", T(5))],
            progress: Progress(
                ("zero", 0, 1000), ("low", 1, 1000), ("edge", 950, 1000),
                ("high", 949, 1000), ("done", 1000, 1000)),
            card: AllCards);
        Assert.Equal(["low", "high"], result.Select(r => r.Video.Id));
    }

    [Fact]
    public void Build_SkipsZeroDurationAndUnknownProgress()
    {
        var result = ContinueWatchingBuilder.Build(
            watched: [("nodur", T(1)), ("noentry", T(2)), ("ok", T(3))],
            progress: Progress(("nodur", 30, 0), ("ok", 30, 300)),
            card: AllCards);
        Assert.Equal(["ok"], result.Select(r => r.Video.Id));
    }

    [Fact]
    public void Build_SkipsIdsWithNoCachedCard()
    {
        var result = ContinueWatchingBuilder.Build(
            watched: [("a", T(1)), ("b", T(2))],
            progress: Progress(("a", 30, 300), ("b", 30, 300)),
            card: id => id == "b" ? Card("b") : null);
        Assert.Equal(["b"], result.Select(r => r.Video.Id));
    }

    [Fact]
    public void Build_SkipsShorts()
    {
        var result = ContinueWatchingBuilder.Build(
            watched: [("s", T(1)), ("v", T(2))],
            progress: Progress(("s", 15, 59), ("v", 30, 300)),
            card: id => Card(id, isShort: id == "s"));
        Assert.Equal(["v"], result.Select(r => r.Video.Id));
    }

    [Fact]
    public void Build_CapsAtThirty_KeepingTheNewest()
    {
        var watched = Enumerable.Range(0, 40).Select(i => ($"v{i}", T(i))).ToList();
        var progress = Progress(watched.Select(w => (w.Item1, 30.0, 300.0)).ToArray());
        var result = ContinueWatchingBuilder.Build(watched, progress, AllCards);
        Assert.Equal(30, result.Count);
        Assert.Equal("v0", result[0].Video.Id);
        Assert.Equal("v29", result[^1].Video.Id);
    }

    [Fact]
    public void Build_EmptyInputs_GiveAnEmptyList()
    {
        Assert.Empty(ContinueWatchingBuilder.Build([], Progress(("a", 30, 300)), AllCards));
        Assert.Empty(ContinueWatchingBuilder.Build([("a", T(1))], Progress(), AllCards));
    }

    [Fact]
    public void Row_IsNullWhenNothingQualifies_AndOtherwiseANamedNonShortsSection()
    {
        Assert.Null(ContinueWatchingBuilder.Row([], Progress(), AllCards));

        var row = ContinueWatchingBuilder.Row([("a", T(1))], Progress(("a", 30, 300)), AllCards);
        Assert.NotNull(row);
        Assert.Equal("Continue watching", row.Title);
        Assert.False(row.IsShorts);
        Assert.Null(row.Continuation);
        Assert.Equal(["a"], row.Items.Select(i => i.Id));
    }
}
