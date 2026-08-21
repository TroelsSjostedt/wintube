namespace WinTube.Core.Stores;

/// Builds the History page's ordering. Port of HistoryView.watchTimes() from tvOS: real
/// times where known, and the account's undated FEhistory ids *placed* rather than dated —
/// each run sits directly below the most recent video with a known time, in YouTube's order,
/// re-anchoring whenever the list names a video whose time is known. A second apiece is a
/// sort key, not a claim about when anything happened.
public static class HistoryBuilder
{
    public static IReadOnlyDictionary<string, DateTimeOffset> WatchTimes(
        IReadOnlyDictionary<string, ProgressEntry> progress,
        IReadOnlyList<(string Id, DateTimeOffset WatchedAt)> watchedHere,
        IReadOnlyList<string> accountHistoryIds,
        DateTimeOffset now)
    {
        var when = progress.ToDictionary(p => p.Key, p => p.Value.UpdatedAt);
        foreach (var (id, watchedAt) in watchedHere)
            if (!when.TryGetValue(id, out var existing) || existing < watchedAt)
                when[id] = watchedAt;

        var anchor = when.Count > 0 ? when.Values.Max() : now;
        foreach (var id in accountHistoryIds)
        {
            if (when.TryGetValue(id, out var known)) { anchor = known; continue; }
            anchor = anchor.AddSeconds(-1);
            when[id] = anchor;
        }
        return when;
    }

    public static IReadOnlyList<string> Ordered(
        IReadOnlyDictionary<string, DateTimeOffset> watchTimes) =>
        watchTimes.OrderByDescending(pair => pair.Value).Select(pair => pair.Key).ToList();
}
