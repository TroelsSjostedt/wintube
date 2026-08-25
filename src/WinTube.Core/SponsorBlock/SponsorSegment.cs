namespace WinTube.Core.SponsorBlock;

/// One community-submitted stretch of a video that isn't the video: a read-out sponsor spot,
/// a "like and subscribe" plug, an intro animation. Times in seconds.
public sealed record SponsorSegment(string Id, SponsorCategory Category, double Start, double End)
{
    public double Duration => End - Start;

    public bool Contains(double time) => time >= Start && time < End;

    /// Sorts by start time and folds overlapping or touching segments (gap ≤ 0.5 s) into one,
    /// so two adjacent sponsor reads become a single seek instead of a seek that lands inside
    /// the next segment and immediately seeks again. Keeps the EARLIER segment's identity —
    /// it's the one whose start the user reaches, so it's the category the toast names.
    public static IReadOnlyList<SponsorSegment> Merge(IEnumerable<SponsorSegment> segments)
    {
        var merged = new List<SponsorSegment>();
        foreach (var segment in segments.OrderBy(s => s.Start))
        {
            if (merged.Count == 0 || segment.Start > merged[^1].End + 0.5)
            {
                merged.Add(segment);
                continue;
            }
            if (segment.End <= merged[^1].End) continue;   // fully contained
            merged[^1] = merged[^1] with { End = segment.End };
        }
        return merged;
    }

    /// The segment playback should seek past right now, or null. `skippedIds` holds segments
    /// already skipped this playback — rewinding into one plays it normally instead of
    /// bouncing the user forward again.
    public static SponsorSegment? NextToSkip(
        IReadOnlyList<SponsorSegment> segments, double time, IReadOnlySet<string> skippedIds) =>
        segments.FirstOrDefault(s => s.Contains(time) && !skippedIds.Contains(s.Id));
}
