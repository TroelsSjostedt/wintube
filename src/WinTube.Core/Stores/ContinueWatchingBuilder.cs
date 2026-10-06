using WinTube.Core.Models;

namespace WinTube.Core.Stores;

/// Builds Home's locally-made "Continue watching" row: the videos played here and left
/// part-way, newest first. Pure — the stores are read by the caller, so nothing here
/// touches disk or the network. Distinct from the account's own history row, which is
/// whatever YouTube says was watched; this one is what has a resumable position on this
/// device.
public static class ContinueWatchingBuilder
{
    public const string Title = "Continue watching";
    public const int MaxCards = 30;

    /// Past this fraction a video counts as finished — the player's own end threshold would
    /// make a nearly-done one "finished" anyway, so it isn't worth resuming.
    private const double FinishedFraction = 0.95;

    public static IReadOnlyList<(VideoItem Video, double Fraction)> Build(
        IReadOnlyList<(string Id, DateTimeOffset WatchedAt)> watched,
        IReadOnlyDictionary<string, ProgressEntry> progress,
        Func<string, VideoItem?> card)
    {
        var result = new List<(VideoItem, double)>();
        foreach (var (id, _) in watched.OrderByDescending(w => w.WatchedAt))
        {
            if (!progress.TryGetValue(id, out var entry) || entry.DurationSeconds <= 0) continue;
            var fraction = entry.PositionSeconds / entry.DurationSeconds;
            if (fraction <= 0 || fraction >= FinishedFraction) continue;
            // A Short in a landscape row would draw badly, and its progress isn't a "resume".
            if (card(id) is not { IsShort: false } video) continue;
            result.Add((video, fraction));
            if (result.Count == MaxCards) break;
        }
        return result;
    }

    /// The row as Home draws it, or null when there is nothing to resume — an empty row is
    /// omitted entirely rather than shown as a bare title.
    public static FeedSection? Row(
        IReadOnlyList<(string Id, DateTimeOffset WatchedAt)> watched,
        IReadOnlyDictionary<string, ProgressEntry> progress,
        Func<string, VideoItem?> card)
    {
        var videos = Build(watched, progress, card);
        return videos.Count == 0
            ? null
            : new FeedSection(
                Guid.NewGuid().ToString("N"), Title, videos.Select(v => v.Video).ToList(),
                null, IsShorts: false);
    }
}
