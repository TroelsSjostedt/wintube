using WinTube.Core.Stores;

namespace WinTube.Core.Tests;

public class HistoryBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset T(int minutesAgo) => Now.AddMinutes(-minutesAgo);

    [Fact]
    public void WatchTimes_LaterOfProgressAndWatchedHereWins()
    {
        var times = HistoryBuilder.WatchTimes(
            progress: new Dictionary<string, ProgressEntry>
                { ["a"] = new(100, 600, T(60)) },
            watchedHere: [("a", T(10)), ("b", T(30))],
            accountHistoryIds: [],
            now: Now);
        Assert.Equal(T(10), times["a"]);   // watchedHere is later than progress
        Assert.Equal(T(30), times["b"]);
    }

    [Fact]
    public void WatchTimes_UndatedIdsSitBelowMostRecentKnown_InYouTubeOrder()
    {
        var times = HistoryBuilder.WatchTimes(
            progress: new Dictionary<string, ProgressEntry>
                { ["known"] = new(100, 600, T(10)) },
            watchedHere: [],
            accountHistoryIds: ["u1", "u2"],
            now: Now);
        Assert.Equal(T(10).AddSeconds(-1), times["u1"]);
        Assert.Equal(T(10).AddSeconds(-2), times["u2"]);
    }

    [Fact]
    public void WatchTimes_KnownIdInAccountList_ReanchorsTheRunBelowIt()
    {
        var times = HistoryBuilder.WatchTimes(
            progress: new Dictionary<string, ProgressEntry>
            {
                ["new"] = new(100, 600, T(5)),
                ["old"] = new(100, 600, T(120)),
            },
            watchedHere: [],
            accountHistoryIds: ["u1", "old", "u2"],
            now: Now);
        Assert.Equal(T(5).AddSeconds(-1), times["u1"]);      // below the newest known
        Assert.Equal(T(120), times["old"]);                  // keeps its real time
        Assert.Equal(T(120).AddSeconds(-1), times["u2"]);    // re-anchored below "old"
    }

    [Fact]
    public void Ordered_NewestFirst()
    {
        var ordered = HistoryBuilder.Ordered(new Dictionary<string, DateTimeOffset>
            { ["a"] = T(30), ["b"] = T(10), ["c"] = T(20) });
        Assert.Equal(["b", "c", "a"], ordered);
    }
}
